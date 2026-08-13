/**
 * Specification for `app.config.ts` — the application's SINGLE provider composition root.
 *
 * ## WHY THIS FILE EXISTS, AND WHAT NO OTHER FILE DOES
 *
 * Every other specification in this workspace builds its own injector, and every one of
 * them therefore asserts something about a provider graph it assembled itself.
 * `app.routes.spec.ts` installs `provideRouter(APP_ROUTES, withComponentInputBinding())`,
 * and `app.component.spec.ts` installs `provideRouter([])` with a bare HTTP client. Both
 * are correct for their subject and neither can see the composition root: with only those
 * two, deleting `withPreloading`, deleting `withInMemoryScrolling`, dropping an
 * interceptor or REORDERING the interceptor array leaves the whole suite green.
 *
 * That is the gap this file closes. It installs the REAL exported array —
 * `[...appConfig.providers, provideHttpClientTesting()]` — and nothing else. There is
 * deliberately no second copy of the provider list here: a duplicated array would agree
 * with whatever the production one became and would prove nothing, which is precisely the
 * failure mode being removed. `provideHttpClientTesting()` is appended LAST and is the
 * only addition, because it REPLACES the backend that `appConfig`'s own
 * `provideHttpClient(...)` installed; the interceptor chain that call configured survives
 * the substitution untouched, which is what makes it observable here.
 *
 * ## WHAT IS ASSERTED
 *
 * Two groups, matching the two things the composition root actually decides.
 *
 * 1. THE ROUTER'S FOUR ARGUMENTS. The route table is the imported one; component input
 *    binding is on; the preloading strategy is the eager one; and scroll position is
 *    restored to the top of each newly activated screen. Each is asserted from the
 *    injector or from observed behaviour rather than by re-reading the source.
 * 2. THE INTERCEPTOR CHAIN, IN ORDER. `withInterceptors([A, B, C])` composes as
 *    `A(next = B(next = C(next = backend)))`, so the declared order is the order on the
 *    way out and its REVERSE on the way back:
 *
 *        request:   correlationId -> auth -> error -> backend
 *        response:  backend -> error -> auth -> correlationId
 *
 *    Two observable consequences pin that order, and both are asserted below. Because the
 *    correlation interceptor is OUTERMOST, a renewal-driven retry — which is cloned from
 *    the already-stamped original — carries the SAME identifier as the first attempt; an
 *    interceptor listed after the auth one would stamp the retry afresh and split one
 *    logical operation across two identifiers. And because the error interceptor is
 *    INNERMOST, it sees the raw 401 before the auth interceptor surrounding it can renew
 *    anything, which is why it must say nothing about a 401 — asserted here as an EMPTY
 *    notification queue across a complete, successful recovery.
 *
 * ## WHAT IS DELIBERATELY NOT ASSERTED HERE
 *
 * The behaviour of each interceptor in isolation, of each guard, and of the route table's
 * twenty-five addresses. Those have their own specifications and duplicating them here
 * would obscure this file's single subject: that the real composition root wires them
 * together in the arrangement its own comments call load-bearing.
 */

import { ViewportScroller } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { APP_BOOTSTRAP_LISTENER, NgZone } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PreloadAllModules, PreloadingStrategy, Router, Scroll } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import type { Observable } from 'rxjs';

import { APP_ROUTES } from './app.routes';
import { appConfig } from './app.config';
import { NotificationService } from './core/services/notification.service';
import { TokenStorageService } from './core/services/token-storage.service';
import { SESSION_ENDED_MESSAGE } from './core/state/session-teardown.service';

import type { Event as RouterNavigationEvent } from '@angular/router';
import type { AuthSession, CurrentUser } from './core/models/auth.model';

// ---------------------------------------------------------------------------
// WIRE FIXTURES
//
// Spelled here rather than imported from the production modules for the usual reason: a
// fixture that borrowed a production constant would agree with whatever that constant
// became, and these values are what the API and the browser genuinely exchange.
// ---------------------------------------------------------------------------

/** The header the correlation interceptor stamps and the API echoes back. */
const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/** The header the bearer interceptor writes. */
const AUTHORIZATION_HEADER = 'Authorization';

/** A protected listing endpoint, which requires a token and is not an auth endpoint. */
const PORTALS_URL = '/api/v1/portals';

/** The anonymous renewal endpoint the recovery path posts to. */
const REFRESH_URL = '/api/v1/auth/refresh';

/** The identity endpoint the renewal reads once the rotated token is in hand. */
const IDENTITY_URL = '/api/v1/auth/me';

/** The access token seeded before a case runs. */
const ACCESS_TOKEN = 'fake-access-token';

/** The renewal credential seeded before a case runs. */
const REFRESH_TOKEN = 'fake-refresh-token';

/** The access token a successful renewal hands back. */
const ROTATED_ACCESS_TOKEN = 'fake-access-token-2';

/** The renewal credential a successful renewal hands back. */
const ROTATED_REFRESH_TOKEN = 'fake-refresh-token-2';

/** An expiry comfortably in the future; no case here depends on a lapse. */
const FUTURE_EXPIRY = '2999-12-31T23:59:59.000Z';

/** The status line the platform writes alongside a refusal. */
const UNAUTHORIZED_INIT = Object.freeze({ status: 401, statusText: 'Unauthorized' });

/** The status line the platform writes alongside a server fault. */
const SERVER_ERROR_INIT = Object.freeze({ status: 500, statusText: 'Internal Server Error' });

/**
 * The identity the renewal response and the identity endpoint both carry.
 *
 * Minimal on purpose: no case reads a member of it, and a credential-shaped member must
 * never appear on this contract.
 */
const OPERATOR: CurrentUser = Object.freeze({
  userId: 7,
  portalId: -1,
  portalName: 'Measured Portal',
  username: 'operator',
  displayName: 'Operator',
  email: 'operator@example.test',
  isSuperUser: false,
  isPortalAdministrator: false,
  roles: Object.freeze(['Administrators']),
  permissions: Object.freeze(['EDIT']),
});

/**
 * The correlation identifier the API publishes in a problem document.
 *
 * A different value from anything the browser stamps, so that a notification quoting it
 * can only have obtained it from the body.
 */
const SERVER_CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/** The trace identifier the API publishes alongside it, which is NOT the one to quote. */
const SERVER_TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/**
 * The API's own sentence for an unexpected server fault, from its status vocabulary.
 *
 * Transcribed rather than invented: the notification asserted below must be the wording a
 * caller would really be shown.
 */
const SERVER_ERROR_DETAIL = 'An unexpected error occurred while processing the request.';

/**
 * A page of portals, as `GET /api/v1/portals` really answers.
 *
 * The tenant is `-1`, which is the first portal an installation has rather than a marker
 * for "no portal": `01.00.00.SqlDataProvider:L77` declares `[PortalID] [int] IDENTITY
 * (-1, 1)`.
 */
const PORTAL_PAGE_BODY = Object.freeze({
  items: [
    {
      portalId: -1,
      portalName: 'Measured Portal',
      aliases: ['localhost'],
      users: 3,
      pages: 7,
      hostSpace: 0,
      hostFee: 0,
      expiryDate: null,
    },
  ],
  meta: { pageIndex: 0, pageSize: 10, totalCount: 1, totalPages: 1 },
});

/**
 * Builds a session to seed the custodian with.
 *
 * Every declared member is supplied, because the API serialises with its ignore condition
 * set to never and therefore transmits a `false` rather than omitting it.
 *
 * @param accessToken The token to present on an API request.
 * @param refreshToken The renewal credential.
 * @returns The session to store.
 */
function sessionFor(accessToken: string, refreshToken: string): AuthSession {
  return {
    accessToken,
    expiresAtUtc: FUTURE_EXPIRY,
    refreshToken,
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: OPERATOR,
  };
}

/**
 * The body a successful renewal answers with.
 *
 * @returns The enveloped renewal response.
 */
function renewalBody(): Record<string, unknown> {
  return {
    data: {
      accessToken: ROTATED_ACCESS_TOKEN,
      expiresAtUtc: FUTURE_EXPIRY,
      refreshToken: ROTATED_REFRESH_TOKEN,
      mustChangePassword: false,
      passwordExpiring: false,
      mustUpdateProfile: false,
      user: OPERATOR,
    },
    meta: null,
  };
}

/**
 * A problem document of the shape this API actually emits.
 *
 * The type is a `urn:dnnmigration:error:<code>` built by one server method; `about:blank`
 * appears nowhere in the server tree, so a fixture carrying it would describe a body no
 * endpoint can produce.
 *
 * @param status The transport status.
 * @param title The per-status title from the server's own vocabulary.
 * @param code The failure code, spelled as the server publishes it.
 * @param detail The sentence the producing service placed on the outcome.
 * @returns The document to flush.
 */
function problemDocument(
  status: number,
  title: string,
  code: string,
  detail: string,
): Record<string, unknown> {
  return {
    type: `urn:dnnmigration:error:${code}`,
    title,
    status,
    detail,
    traceId: SERVER_TRACE_ID,
    correlationId: SERVER_CORRELATION_ID,
  };
}

/** The refusal a protected endpoint answers with when the presented token is not accepted. */
const UNAUTHENTICATED_PROBLEM = problemDocument(
  401,
  'Unauthorized',
  'auth.unauthenticated',
  'Authentication is required to reach this resource.',
);

/** The document the API emits for an unexpected fault. */
const SERVER_ERROR_PROBLEM = problemDocument(
  500,
  'Internal Server Error',
  'server.unexpected_failure',
  SERVER_ERROR_DETAIL,
);

/**
 * Awaits a pending call and returns the reason it failed, or null when it succeeded.
 *
 * Returning the reason rather than asserting inside a callback keeps every expectation in
 * the body of the case that owns it.
 *
 * @param pending The in-flight call.
 * @returns The rejection reason, or null when the call succeeded.
 */
async function reasonFor(pending: Promise<unknown>): Promise<unknown> {
  return pending.then(
    () => null,
    (reason: unknown) => reason,
  );
}

describe('appConfig', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // ⚠ THE WHOLE POINT OF THIS FILE IS THIS ONE LINE. The application's real provider
      // array is SPREAD IN rather than reproduced, so a provider removed from
      // `app.config.ts` is removed from here too and the cases below start failing. The
      // only addition is the testing backend, and it comes LAST because it replaces the
      // backend `appConfig`'s own `provideHttpClient(...)` installed — appended the other
      // way round, the live backend would win and every expectation below would find
      // nothing while the requests left the browser.
      providers: [...appConfig.providers, provideHttpClientTesting()],
    });

    httpMock = TestBed.inject(HttpTestingController);
  });

  // MANDATORY, and it is doing real work rather than tidying up. `verify()` fails a case
  // when any request was issued that no expectation accounted for, which is the mechanism
  // by which "exactly one renewal" and "exactly one retry" are proved below. No case in
  // this file drains requests with an all-matching matcher; every request is claimed by
  // name.
  afterEach(() => {
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // THE ROUTER'S FOUR ARGUMENTS
  // -------------------------------------------------------------------------

  describe('the router the composition root installs', () => {
    it('installs the application route table rather than a table of its own', () => {
      // Compared STRUCTURALLY against the imported array rather than by identity: the
      // router standardises the configuration it is handed, so `router.config` is an
      // equivalent copy and not the same object. The expectation is nevertheless DERIVED
      // from the import — no path is written out here — so adding, removing or renaming a
      // top-level route needs no edit to this case and cannot silently disagree with it.
      const installed = TestBed.inject(Router).config;

      expect(installed.length).toBe(APP_ROUTES.length);
      expect(installed.map((route) => route.path)).toEqual(APP_ROUTES.map((route) => route.path));
      expect(installed.filter((route) => route.loadChildren !== undefined).length)
        .withContext('every lazily declared feature group survives standardisation')
        .toBe(APP_ROUTES.filter((route) => route.loadChildren !== undefined).length);
    });

    it('binds route parameters and route data into declared component inputs', () => {
      // Both failure modes of omitting `withComponentInputBinding()` are silent: the
      // parameterised detail screens render without their record and the catch-all view
      // renders without its wording, with nothing anywhere to say why. The router
      // publishes whether the binder is installed, so the fact is read rather than
      // inferred from a navigation.
      expect(TestBed.inject(Router).componentInputBindingEnabled).toBeTrue();
    });

    it("preloads every lazily declared feature bundle through the framework's own strategy", () => {
      /*
       * ASSERTED BY IDENTITY **AND** BY BEHAVIOUR, because each catches a different loss.
       *
       * The identity assertion is the one the project plan requires: it fixes the router
       * configuration verbatim as `withPreloading(PreloadAllModules)`, so substituting a
       * bespoke strategy of the same shape is a specification change however well it behaves.
       * A session-gated preloader is the tempting substitution, because `PreloadAllModules`
       * begins fetching as soon as the FIRST navigation settles and for an anonymous visitor that
       * navigation settles on the sign-in screen. The measurement behind that temptation is real
       * (163,918 bytes, 54.49% of the application's JavaScript, reachable by anyone who could
       * reach that screen) and it is recorded in the migration notes, but a bundle name carries no
       * authority: every route inside those bundles is refused by its own gate and re-authorised
       * server-side. The plan stands, and this case names the built-in so a substitute cannot pass
       * by imitating it.
       *
       * The behavioural assertion is that preloading happens AT ALL and happens
       * UNCONDITIONALLY. The router's default is no strategy, which type-checks, builds and
       * serves while paying the lazy bundle cost again on the first navigation into each
       * feature - invisible except as a slower console. Driving the loader with no session held
       * proves the eager semantics rather than merely the type.
       */
      const strategy = TestBed.inject(PreloadingStrategy);

      expect(strategy)
        .withContext("the plan names the framework's built-in strategy verbatim")
        .toBeInstanceOf(PreloadAllModules);

      let loaded = 0;
      const load = (): Observable<unknown> => {
        loaded += 1;

        return of(null);
      };

      // Anonymous, and it still loads. The returned stream is subscribed, because a strategy
      // that deferred the work into the subscription rather than performing it would otherwise
      // pass while loading nothing.
      strategy.preload({ path: 'portals' }, load).subscribe();

      expect(loaded)
        .withContext('the eager behaviour does not wait for a session')
        .toBe(1);
    });

    it('takes over scroll restoration from the browser and puts each screen at the top', async () => {
      const scroller = TestBed.inject(ViewportScroller);
      const takeOverRestoration = spyOn(scroller, 'setHistoryScrollRestoration');
      const scrollToPosition = spyOn(scroller, 'scrollToPosition');

      // The scroll feature is initialised by the router's bootstrap listener rather than
      // by the injector, so the listeners are run here — which is what makes this a
      // BOOTSTRAP assertion rather than an injector one. They are invoked with no
      // component reference because the router's listener only compares the argument with
      // the application's first bootstrapped component and TestBed has none, so both
      // sides are absent and the listener proceeds.
      startBootstrapListeners();

      expect(takeOverRestoration)
        .withContext('the browser must stop restoring scroll position itself')
        .toHaveBeenCalledWith('manual');

      const router = TestBed.inject(Router);
      const observed: RouterNavigationEvent[] = [];
      const subscription = router.events.subscribe((event) => observed.push(event));

      await router.navigateByUrl('/login');
      await settleScrollEvent();
      subscription.unsubscribe();

      expect(observed.some((event) => event instanceof Scroll))
        .withContext('the scroll feature publishes its own event on the router stream')
        .toBeTrue();
      expect(scrollToPosition)
        .withContext("restoration is 'top', so a newly activated screen starts at the top")
        .toHaveBeenCalledWith([0, 0]);
    });

    it('runs change detection inside a real zone, as the polyfill configuration requires', () => {
      // `angular.json` declares `zone.js` as a polyfill for both the build and the test
      // target, so the zoneless provider would contradict the workspace configuration.
      // The zone-based provider is what keeps `NgZone` a real implementation rather than
      // the no-op stand-in a zoneless application receives.
      const zone = TestBed.inject(NgZone);

      expect(NgZone.isInAngularZone())
        .withContext('a specification body runs outside the zone')
        .toBeFalse();
      expect(zone.run(() => NgZone.isInAngularZone()))
        .withContext('and the injected zone really enters it')
        .toBeTrue();
    });
  });

  // -------------------------------------------------------------------------
  // THE INTERCEPTOR CHAIN
  // -------------------------------------------------------------------------

  describe('the interceptor chain the composition root installs', () => {
    let http: HttpClient;
    let tokens: TokenStorageService;
    let notifications: NotificationService;

    beforeEach(() => {
      http = TestBed.inject(HttpClient);
      tokens = TestBed.inject(TokenStorageService);
      notifications = TestBed.inject(NotificationService);
    });

    it('stamps a correlation identifier and attaches the bearer token to one request', async () => {
      tokens.store(sessionFor(ACCESS_TOKEN, REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PORTALS_URL));
      const request = httpMock.expectOne(PORTALS_URL);

      // BOTH headers on ONE request is the assertion. Either interceptor missing from the
      // array leaves the other's header in place, so checking them together is what proves
      // the chain has two members rather than one.
      //
      // ⚠ PRESENCE IS ASSERTED SEPARATELY FROM CONTENT, AND THE SEPARATION IS NOT
      // PEDANTRY. An absent header reads back as `null`, and a pattern match would coerce
      // that `null` to the string "null" and pass — so a chain with no correlation
      // interceptor at all would satisfy a single pattern expectation. `has` answers
      // presence, and `expectStampedIdentifier` answers content by type and length.
      expect(request.request.headers.has(CORRELATION_ID_HEADER))
        .withContext('every request that leaves this application carries an identifier')
        .toBeTrue();
      expectStampedIdentifier(request.request.headers.get(CORRELATION_ID_HEADER));
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the scheme is exactly "Bearer" and one space')
        .toBe(`Bearer ${ACCESS_TOKEN}`);

      request.flush(PORTAL_PAGE_BODY);

      await expectAsync(pending).toBeResolvedTo(PORTAL_PAGE_BODY);
    });

    it('recovers a refused request with one renewal and one retry, and says nothing about it', async () => {
      tokens.store(sessionFor(ACCESS_TOKEN, REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PORTALS_URL));

      const first = httpMock.expectOne(PORTALS_URL);
      const stampedIdentifier = first.request.headers.get(CORRELATION_ID_HEADER);

      // Established as a real identifier BEFORE it is used as the yardstick below.
      // Without this, an absent header would make the comparison `null === null`, and the
      // retry assertion — the one that pins the array order — would pass on a chain
      // carrying no correlation interceptor whatsoever.
      expectStampedIdentifier(stampedIdentifier);
      first.flush(UNAUTHENTICATED_PROBLEM, UNAUTHORIZED_INIT);

      // The renewal issues TWO requests, and that is the single most surprising fact about
      // this path: the rotated pair is posted first, then the identity is read with the
      // freshly issued token set BY HAND — which is what makes the bearer interceptor pass
      // that second request through untouched, so a refused identity read cannot itself
      // provoke another renewal.
      const renewal = httpMock.expectOne(REFRESH_URL);

      expect(renewal.request.method).toBe('POST');
      renewal.flush(renewalBody());

      const identity = httpMock.expectOne(IDENTITY_URL);

      expect(identity.request.method).toBe('GET');
      expect(identity.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${ROTATED_ACCESS_TOKEN}`,
      );
      identity.flush({ data: OPERATOR, meta: null });

      const retry = httpMock.expectOne(PORTALS_URL);

      expect(retry.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the retry presents the ROTATED token, not the refused one')
        .toBe(`Bearer ${ROTATED_ACCESS_TOKEN}`);

      // ⚠ THIS IS THE ORDER ASSERTION. The retry is cloned from an original that was
      // already stamped, so it carries the SAME identifier — which is only true because
      // the correlation interceptor is listed FIRST and is therefore outermost. Move it
      // after the bearer interceptor and the retry gets a fresh identifier, splitting one
      // logical operation into two unrelated ones in the server's logs. Nothing in the
      // toolchain detects that; this expectation does.
      expect(retry.request.headers.get(CORRELATION_ID_HEADER))
        .withContext('the retry is the same logical operation as the first attempt')
        .toBe(stampedIdentifier);

      retry.flush(PORTAL_PAGE_BODY);

      await expectAsync(pending).toBeResolvedTo(PORTAL_PAGE_BODY);

      // ⚠ AND THIS IS THE OWNERSHIP ASSERTION. The error interceptor is listed LAST and is
      // therefore INNERMOST on the response path, so it saw the raw 401 before the bearer
      // interceptor around it renewed anything — and it saw the retry too. Announcing
      // either would tell an operator their session had expired at the very moment it was
      // being renewed for them. Silence across a successful recovery is the observable
      // form of that agreement.
      expect(notifications.notifications())
        .withContext('a recovered session is announced to nobody')
        .toEqual([]);
    });

    it('announces a server fault exactly once, in the server\'s own words', async () => {
      tokens.store(sessionFor(ACCESS_TOKEN, REFRESH_TOKEN));

      const pending = reasonFor(firstValueFrom(http.get(PORTALS_URL)));

      httpMock.expectOne(PORTALS_URL).flush(SERVER_ERROR_PROBLEM, SERVER_ERROR_INIT);

      expect(await pending)
        .withContext('the fault reaches the caller as well as the notification queue')
        .not.toBeNull();

      const queued = notifications.notifications();

      // ONE entry, not two. The interceptor is the fallback announcer: a caller that
      // presents its own failures marks the request and is not announced for, and a
      // caller that does not — as here — is announced for exactly once.
      expect(queued.length).withContext('one incident, one report').toBe(1);
      expect(queued[0].severity)
        .withContext('a fault is an error, unlike a refusal')
        .toBe('error');
      expect(queued[0].message)
        .withContext("the server's own sentence, with the reference appended as its own clause")
        .toBe(`${SERVER_ERROR_DETAIL} Reference: ${SERVER_CORRELATION_ID}`);
      expect(queued[0].reference)
        .withContext('the correlation identifier is the value to quote, never the trace id')
        .toBe(SERVER_CORRELATION_ID);
    });

    it('routes an unrecoverable 401 to sign-in, preserving the destination and saying so', async () => {
      /*
       * The terminal branch: a session with no renewal credential cannot be recovered, so the
       * caller learns its request failed and the operator is sent to sign in.
       *
       * ⚠ THIS SPECIFICATION USED TO ASSERT THE DEFECT, in both of its halves. It was named
       * "and still announces nothing", it required `notifications()` to be empty, and it
       * required the navigation to carry no options - reasoning that "the sign-in screen IS
       * the message". A review measured why that reasoning does not hold: every successful
       * create in this application also ends by navigating away, so a sign-in screen arriving
       * unannounced is indistinguishable from work that was saved, and a submission destroyed
       * this way was reported by nothing at all - both live regions empty, no console entry.
       *
       * The two requirements below replace it. Both are contracts rather than preferences: the
       * destination is preserved because a gate-blocked navigation already preserved it and the
       * asymmetry favoured the rarer case, and the address is REPLACED rather than pushed
       * because the abandoned screen cannot be restored once the session is gone.
       *
       * The navigation is spied rather than performed, and resolved rather than left pending. The
       * real route table is installed, so an actual navigation would fetch the sign-in feature's
       * lazy bundle over several microtasks that the subject deliberately does not await — it is
       * re-throwing the server's own response and must not have its outcome displaced by routing —
       * which would make an address assertion a race. Asserting the REQUEST to navigate is
       * deterministic and is the fact this case is about.
       *
       * The expected `returnUrl` is `'/'` because no navigation has been performed in this harness,
       * so that is genuinely the address the refused request was issued from.
       */
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      tokens.store(sessionFor(ACCESS_TOKEN, ''));

      const pending = reasonFor(firstValueFrom(http.get(PORTALS_URL)));

      httpMock.expectOne(PORTALS_URL).flush(UNAUTHENTICATED_PROBLEM, UNAUTHORIZED_INIT);

      expect(await pending)
        .withContext('the original refusal reaches the caller rather than being swallowed')
        .not.toBeNull();
      expect(navigate)
        .withContext('the operator is asked to sign in again, and told where they were')
        .toHaveBeenCalledOnceWith(['/login'], {
          replaceUrl: true,
          queryParams: { returnUrl: '/' },
        });
      expect(tokens.accessToken())
        .withContext('and the discarded session leaves no credential behind')
        .toBeNull();

      // The statement itself. Asserted through the real queue rather than a spy, because the part
      // that matters is that something READABLE survives to the sign-in screen - a call that was
      // made and then discarded by the navigation would satisfy a spy and help nobody.
      const announced = notifications.notifications();

      expect(announced.length).withContext('exactly one statement, not none and not two').toBe(1);
      expect(announced[0]?.severity)
        .withContext('a lapsed session is ordinary, so it is a warning and not a failure')
        .toBe('warning');
      // Imported rather than spelled again: a specification that restates the sentence passes while
      // the application says something else.
      expect(announced[0]?.message)
        .withContext('it says what happened and what to do, and quotes nothing from the refusal')
        .toBe(SESSION_ENDED_MESSAGE);
      // ⚠ ASSERTED AS SURVIVAL RATHER THAN AS A FLAG, WHICH IS AN IMPROVEMENT ON WHAT THIS CASE USED
      // TO CHECK. It read `announced[0].survivesNavigation` and required it true - one of the TWO
      // mechanisms the queue exempts an entry by, and not the one this path uses. The flag was true only
      // because the authentication interceptor raised a SECOND, identical statement with the flag set,
      // alongside the one the session teardown already raises; removing that duplicate left the flag
      // false while the exemption itself was untouched, because the teardown claims it through
      // `retainAcrossNavigation()`.
      //
      // A specification that names one of two equivalent mechanisms fails when the other is used and
      // passes when neither works but the flag happens to be set. Driving the sweep instead asserts the
      // property the operator actually depends on - the sentence is still there to read once the
      // redirect has landed - and holds however the exemption was obtained.
      notifications.clearOnNavigation();

      expect(notifications.notifications().map((entry) => entry.message))
        .withContext('it outlives the very navigation that follows it')
        .toEqual([SESSION_ENDED_MESSAGE]);
    });
  });
});

/**
 * Asserts that a header value really is a stamped correlation identifier.
 *
 * Typed `string | null` because that is what `HttpHeaders.get` returns, and narrowed by
 * an explicit type test rather than by a pattern: a regular-expression matcher coerces its
 * actual value to a string, so `null` would arrive as `"null"` and satisfy any pattern
 * that only demands a non-blank character. The type test is what makes the absence of the
 * header a failure.
 *
 * @param value The header value read off an outbound request.
 */
function expectStampedIdentifier(value: string | null): void {
  expect(typeof value).withContext('the header is present and carries a string').toBe('string');
  expect((value ?? '').trim().length)
    .withContext('and the identifier is not blank')
    .toBeGreaterThan(0);
}

/**
 * Runs every registered bootstrap listener, which is what initialises the router's
 * scroll handling and its preloader.
 *
 * The listeners are invoked with no component reference. The router's own listener
 * compares its argument with the application's FIRST bootstrapped component and returns
 * early when they differ; `TestBed` bootstraps none, so both sides are absent, they
 * compare equal, and the listener proceeds — which is exactly the path a real
 * `bootstrapApplication` takes for the root component.
 */
function startBootstrapListeners(): void {
  const listeners: ReadonlyArray<(component: never) => void> =
    TestBed.inject(APP_BOOTSTRAP_LISTENER);

  for (const listener of listeners) {
    listener(undefined as never);
  }
}

/**
 * Waits for the scroll event the router schedules after a navigation.
 *
 * The router defers it to a macrotask deliberately — the position must not be restored
 * before the outlet has rendered the activated component — so a microtask flush is not
 * enough and the wait has to be a real one.
 *
 * @returns Completion once the deferred event has been delivered.
 */
async function settleScrollEvent(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}
