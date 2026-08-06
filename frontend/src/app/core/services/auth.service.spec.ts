/**
 * Specification for {@link AuthService} — the authentication client whose surface is
 * closed at four operations and which orchestrates nothing.
 *
 * The legacy application shipped no automated tests of any kind, so nothing here is a
 * port. Every expectation was authored from two measured sources: the Web Forms sign-in
 * screen this client replaces, cited by line throughout, and the destination wire
 * contract read from the service and model this file exercises.
 *
 * ## What this specification is FOR
 *
 * The valuable assertions here are the negative ones. Authentication is where
 * orchestration creep is most likely — a retry folded into the client, a credential
 * cached in a second place, a status code interpreted locally — and each of those is
 * invisible to a compiler and to a reviewer skimming a diff. Three properties are
 * therefore proved mechanically rather than trusted:
 *
 * - **Nothing is retried here.** A refused request produces exactly the requests the
 *   operation declares and not one more. Recovering from an expired credential is the
 *   interceptor's responsibility, and no interceptor is installed in this harness, so a
 *   recovery attempt smuggled into the service would show up as an unexpected request.
 * - **No credential is held here.** The service owns no store of its own; the three
 *   session values it publishes are projections of the dedicated custody collaborator,
 *   and its own field surface is pinned so a fourth cannot appear unnoticed.
 * - **Sign-out has no shape of its own.** It transmits the one credential it revokes and
 *   nothing else, using the same body contract renewal uses.
 *
 * `httpMock.verify()` in `afterEach` is what makes all three enforceable: it fails the
 * test if the service issued a request the test did not account for. It is the single
 * most load-bearing line in this file.
 *
 * ## The harness deliberately omits the interceptor chain
 *
 * The real client is provided with a fixed chain of three interceptors — a correlation
 * identifier, then the bearer credential, then error translation. None is installed
 * here, and that omission is what gives the header assertions below their meaning: when
 * a request reaching the mock backend carries no headers at all, that is proof the
 * service set none, because nothing else in the pipeline could have.
 *
 * ## Addresses are asserted as literals, on purpose
 *
 * The service composes its four addresses from a shared endpoint module, which in turn
 * composes them from the configured API base. Asserting against that same module would
 * be tautological — it would agree with the service however the base changed. The four
 * literals below independently pin the contract instead.
 *
 * They are ROOT-RELATIVE, and that is a deployment requirement rather than a
 * convenience. The production environment module publishes a relative base so the
 * browser reaches the API through the same origin that served the application, by way of
 * the reverse proxy. The build's file substitutions are inverted from the usual
 * arrangement — the production configuration substitutes nothing and the development
 * configuration is the one that swaps the environment module — and the test target
 * declares no substitution at all. Specs therefore compile against the production
 * environment module and so against the relative base. An absolute base would type-check,
 * bundle and deploy while breaking every call a browser made.
 *
 * ## Deliberately NOT asserted here
 *
 * - **The progressive sign-in ladder.** The legacy screen's flow was stateful: on a
 *   not-approved outcome (`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L168`,
 *   `:L170`) the FIRST rejection merely revealed the code rows and asked for a code
 *   (`:L171`, `:L175`); a later non-empty but wrong code answered differently (`:L177`,
 *   `:L178`) from one still empty (`:L180`); and outside verified sign-up mode the account
 *   was simply refused (`:L184`). That "a code is now required" flag belongs to the
 *   feature store that owns the screen. This file asserts only that the three outcome
 *   codes — EnterCode, InvalidCode and UserNotAuthorized, and no fourth — reach the
 *   caller untranslated.
 * - **The status mapping, including the legacy defect.** `Login.ascx.vb:L187` decided the
 *   outcome with `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`; because
 *   the not-approved value was consumed by the branch at `:L168`, every other non-zero
 *   value fell into that arm — so a locked-out account (3) and both insecure-credential
 *   outcomes (5 and 6) all authenticated, against the seven explicitly valued members at
 *   `Library/Components/Users/Membership/UserLoginStatus.vb:L24-L30`. The correction is
 *   the server's, the ordinals never travel on the wire, and this file deliberately does
 *   not test it.
 * - **Any credential policy.** `Website/release.config:L240-L245` shipped its policy —
 *   reset enabled, no question-and-answer requirement, a seven-character floor, no
 *   non-alphanumeric requirement, no unique-address requirement — and it is preserved
 *   verbatim on the server. Asserting a client-side rule here would institutionalise a
 *   divergence and refuse accounts the legacy application accepted.
 * - **Navigation, problem-document rendering and credential decoding.** Each belongs to a
 *   different unit. Nothing here reads a clock, performs expiry arithmetic or decodes a
 *   credential segment.
 *
 * ## Fixtures carry no real or real-looking secrets
 *
 * Every credential below is an obviously fake placeholder. The legacy deployment
 * committed a reversible symmetric key to source control in the clear alongside a
 * recoverable credential format (`Website/release.config:L239`, `:L245`) — the precise
 * anti-pattern this migration eliminates — and no part of it is reproduced here, in a
 * fixture or in a comment. Nothing in this file is written to a log sink.
 */

import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { AuthSession, CurrentUser, LoginRequest, LoginResponse } from '../models/auth.model';
import { AuthService } from './auth.service';

// ---------------------------------------------------------------------------
// THE FOUR ADDRESSES — the entire authentication surface, as literals
//
// Written out rather than imported so that this file states the wire contract
// independently of the module the service composes it from. See the note above.
// ---------------------------------------------------------------------------

/** `POST` — exchanges credentials for a credential pair. Anonymous. */
const LOGIN_URL = '/api/v1/auth/login';

/** `POST` — exchanges a renewal credential for a rotated pair. Anonymous. */
const REFRESH_URL = '/api/v1/auth/refresh';

/** `POST` — revokes a renewal credential. Anonymous, and answers 204 regardless. */
const LOGOUT_URL = '/api/v1/auth/logout';

/** `GET` — describes the caller. The one operation of the four that needs a credential. */
const ME_URL = '/api/v1/auth/me';

// ---------------------------------------------------------------------------
// FIXTURES
// ---------------------------------------------------------------------------

/**
 * The success envelope every payload-bearing endpoint wraps its payload in.
 *
 * Declared locally rather than imported, because what a fixture flushes is the JSON the
 * server writes, not an application type. Stating the shape here means this file pins the
 * envelope contract independently: a change to the application's own envelope type cannot
 * quietly bring these expectations along with it.
 *
 * The metadata companion is PRESENT AND NULL rather than absent. The server serialises
 * with its ignore condition set to never, so a member holding no value travels as `null`
 * instead of going missing, and a credential response has no page to describe. Flushing
 * the member out altogether would test a body the server never sends.
 */
interface SuccessEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

/**
 * An RFC 7807 problem document, as the API produces for every refusal.
 *
 * The five members the API contract mandates, with the per-field map read through bracket
 * access — index-signature property access is disallowed by the compiler configuration,
 * and reaching for a member with a dot would not compile.
 */
interface ProblemDocument {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly errors: Readonly<Record<string, readonly string[]>>;
}

/** Obviously fake. Never a value that could resemble a real issued credential. */
const FAKE_ACCESS_TOKEN = 'fake-access-token-first';
const FAKE_ACCESS_TOKEN_ROTATED = 'fake-access-token-rotated';
const FAKE_RENEWAL_TOKEN = 'fake-renewal-token-first';
const FAKE_RENEWAL_TOKEN_ROTATED = 'fake-renewal-token-rotated';
const FAKE_PASSWORD = 'not-a-real-password';

/**
 * A far-future instant as an absolute ISO 8601 string.
 *
 * A string rather than a date object because that is what the transport produces: parsing
 * JSON yields a string and nothing converts it. Typing or fabricating a date here would be
 * a claim the runtime does not honour, and computing one would be the expiry arithmetic
 * this client is specified not to perform.
 */
const EXPIRES_AT = '2100-01-01T00:00:00.000Z';

/**
 * The caller as the server describes it.
 *
 * The identity members carry a single lowercase `d` — `userId`, not an upper-cased run —
 * because the server's camel-casing policy lowercases a leading uppercase run and binding
 * the wrong spelling yields undefined at runtime with no compile error.
 */
function currentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 7,
    portalId: 0,
    portalName: 'Primary',
    username: 'admin',
    displayName: 'Administrator',
    email: 'admin@example.test',
    isSuperUser: false,
    roles: ['Administrators'],
    permissions: ['VIEW'],
    ...overrides,
  };
}

/**
 * A successful credential response, enveloped exactly as the server writes it.
 *
 * All three advisory flags are present and `false` rather than omitted: the server's
 * ignore condition is never, so a `false` reaches the wire as itself. A fixture that
 * dropped them would be testing a body the server does not send, and would hide a
 * consumer that mistook absent for false.
 */
function credentialResponse(
  accessToken: string,
  refreshToken: string,
  overrides: Partial<LoginResponse> = {},
): SuccessEnvelope<LoginResponse> {
  return {
    data: {
      accessToken,
      expiresAtUtc: EXPIRES_AT,
      refreshToken,
      mustChangePassword: false,
      passwordExpiring: false,
      mustUpdateProfile: false,
      user: currentUser(),
      ...overrides,
    },
    meta: null,
  };
}

/** The describe-caller payload, enveloped. */
function identityResponse(user: CurrentUser = currentUser()): SuccessEnvelope<CurrentUser> {
  return { data: user, meta: null };
}

/**
 * A refusal body carrying one of the three legacy outcome codes.
 *
 * The authoritative English wording for these three lives beside the legacy
 * ADMINISTRATIVE sign-in control, at
 * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` — EnterCode at L162,
 * InvalidCode at L165 and UserNotAuthorized at L222 — and NOT beside the
 * authentication-services control whose behaviour this client was measured from, whose own
 * resource file contains none of the three. Legacy resource text is treated as untrusted
 * markup and is never routed into a trusted-HTML sink, so only the stable codes appear
 * here, never the display strings.
 */
function refusal(status: number, code: string): ProblemDocument {
  return {
    type: `urn:dnn:error:auth:${code}`,
    title: 'Authentication failed',
    status,
    detail: 'The sign-in attempt was refused.',
    errors: { verificationCode: [code] },
  };
}

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client with NO interceptor chain installed. Ordering matters: the real
      // backend is provided first and the testing backend then overrides it. Providing
      // them the other way round leaves the real backend in place and the expectations
      // below would never see a request at all.
      //
      // The service is tree-shakeable and root-provided, so it is deliberately absent
      // from this list — naming it here would construct a second instance alongside the
      // injector's own and test the wrong object.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The proof that nothing is orchestrated. This fails on any request the test did not
    // account for, which is exactly what a recovery attempt folded into the service, a
    // second describe-caller read, or a revoke-then-confirm sequence would produce.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  //
  // Session state is established ONLY by driving the service's own public surface. The
  // custody collaborator is never injected here: doing so would let a test set up a state
  // the service itself has no way to reach, and would couple this specification to a
  // collaborator that has its own.
  // -------------------------------------------------------------------------

  /**
   * Answers the identity bootstrap that follows every successful credential response.
   *
   * Both sign-in and renewal deliberately perform a second read to describe the caller,
   * because the credential responses carry an authority-minimised identity and the issued
   * credential carries no role or permission claims. That second request is part of each
   * operation's contract, not an accident, so every successful flow below answers it.
   *
   * It is also the ONE request in the whole surface that carries a header the service set
   * by hand, and the reason is structural: during sign-in and renewal the rotated
   * credential exists only as a local value and is deliberately not stored until the
   * identity has been fetched, so an interceptor reading storage at that instant would
   * attach the previous credential or none. The count is asserted rather than the name, so
   * that a second header appearing here would fail even though the assertion never spells
   * a header out.
   */
  function answerIdentityBootstrap(
    expectedToken: string,
    user: CurrentUser = currentUser(),
  ): void {
    const request = httpMock.expectOne(ME_URL);

    expect(request.request.method).toBe('GET');

    const headerNames = request.request.headers.keys();
    expect(headerNames.length)
      .withContext('the bootstrap read carries exactly one hand-set header and no more')
      .toBe(1);
    expect(request.request.headers.get(headerNames[0]))
      .withContext('it presents the freshly issued credential, not a stored one')
      .toBe(`Bearer ${expectedToken}`);

    request.flush(identityResponse(user));
  }

  /**
   * Drives a complete sign-in so that renewal and sign-out have a session to work from.
   *
   * @returns The identity the service emitted.
   */
  async function completeSignIn(
    accessToken: string = FAKE_ACCESS_TOKEN,
    refreshToken: string = FAKE_RENEWAL_TOKEN,
  ): Promise<CurrentUser> {
    const pending = firstValueFrom(
      service.login({ username: 'admin', password: FAKE_PASSWORD }),
    );

    httpMock.expectOne(LOGIN_URL).flush(credentialResponse(accessToken, refreshToken));
    answerIdentityBootstrap(accessToken);

    return pending;
  }

  /** Resolves to the rejection reason, or null when the promise resolved instead. */
  async function rejectionOf(pending: Promise<unknown>): Promise<unknown> {
    return pending.then(
      () => null,
      (reason: unknown) => reason,
    );
  }

  // =========================================================================
  // THE THREE NEGATIVE PROOFS
  //
  // Written first because they are what this specification exists for.
  // =========================================================================

  describe('orchestrates no recovery of its own', () => {
    // Recovering from a refused credential belongs to the interceptor that observes the
    // refusal, which is not installed in this harness. Every case below therefore proves
    // that the operation produced exactly the requests it declares and stopped.

    it('does not attempt a renewal when sign-in is refused', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock
        .expectOne(LOGIN_URL)
        .flush(refusal(401, 'InvalidCredentials'), { status: 401, statusText: 'Unauthorized' });

      await expectAsync(pending).toBeRejected();

      // Named explicitly as well as covered by verify(), because the absence of this
      // request is the property under test rather than a side effect of it.
      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(ME_URL);
    });

    it('does not sign out, or retry, when a renewal is refused', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.refresh());

      const requests = httpMock.match(REFRESH_URL);
      expect(requests.length).withContext('exactly one renewal attempt').toBe(1);
      requests[0].flush(refusal(401, 'InvalidRefreshToken'), {
        status: 401,
        statusText: 'Unauthorized',
      });

      await expectAsync(pending).toBeRejected();

      // A refused renewal discards the session locally; it does not call the revoke
      // endpoint to do so, and it does not present the consumed credential again.
      httpMock.expectNone(LOGOUT_URL);
      httpMock.expectNone(REFRESH_URL);
    });

    it('does not attempt a renewal when the describe-caller read is refused', async () => {
      const pending = firstValueFrom(service.me());

      const requests = httpMock.match(ME_URL);
      expect(requests.length).withContext('exactly one describe-caller read').toBe(1);
      requests[0].flush(refusal(401, 'TokenExpired'), {
        status: 401,
        statusText: 'Unauthorized',
      });

      await expectAsync(pending).toBeRejected();

      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(ME_URL);
    });

    it('does not back off or repeat when the rate limiter refuses sign-in', async () => {
      // 429 is the only place in the entire API that this status occurs, and it is the
      // named compensating control for the dropped legacy challenge-image guard at
      // `Login.ascx.vb:L162`. The client's whole responsibility is to let it through: no
      // delay, no attempt counter, no reading of the standard retry-hint header, and no
      // second attempt. Anything else would spend the caller's remaining budget on its
      // own behalf.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const requests = httpMock.match(LOGIN_URL);
      expect(requests.length).withContext('exactly one attempt').toBe(1);
      requests[0].flush(refusal(429, 'TooManyRequests'), {
        status: 429,
        statusText: 'Too Many Requests',
      });

      const reason = await rejectionOf(pending);

      expect(reason instanceof HttpErrorResponse).toBeTrue();
      expect((reason as HttpErrorResponse).status).toBe(429);

      httpMock.expectNone(LOGIN_URL);
      httpMock.expectNone(REFRESH_URL);
    });
  });

  describe('holds no credential store of its own', () => {
    it('publishes exactly three derived session values and keeps no fourth field', () => {
      // Custody belongs to the dedicated collaborator, which holds the session in memory
      // only. The three values below are projections of it, re-published so a consumer
      // needs one injection rather than two — sign-in returns the identity rather than the
      // session, so without them the server's advisory flags would have no reader at all.
      //
      // Pinning the instance field surface is what makes "no credential store of its own"
      // checkable: a cached identity, a duplicated credential or a bespoke session flag
      // added later would appear here and fail.
      expect(Object.keys(service).sort()).toEqual([
        'currentUser',
        'http',
        'isAuthenticated',
        'mustUpdateProfile',
        'refreshInFlight',
        'tokenStorage',
      ]);
    });

    it('reports no session before a sign-in', () => {
      expect(service.isAuthenticated()).toBeFalse();
      expect(service.currentUser()).toBeNull();
      expect(service.mustUpdateProfile()).toBeFalse();
    });

    it('reflects the session after a sign-in and abandons it on sign-out', async () => {
      const user = await completeSignIn();

      expect(service.isAuthenticated()).toBeTrue();
      expect(service.currentUser()).toEqual(user);

      const signOut = firstValueFrom(service.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;

      expect(service.isAuthenticated())
        .withContext('the session is gone, so nothing survives the sign-out')
        .toBeFalse();
      expect(service.currentUser()).toBeNull();
    });

    it('discards any earlier session synchronously, before a sign-in attempt is answered', async () => {
      await completeSignIn();
      expect(service.isAuthenticated()).toBeTrue();

      const pending = firstValueFrom(
        service.login({ username: 'someone-else', password: FAKE_PASSWORD }),
      );

      expect(service.isAuthenticated())
        .withContext('cleared at the point of the call, not on the response')
        .toBeFalse();

      httpMock
        .expectOne(LOGIN_URL)
        .flush(refusal(401, 'InvalidCredentials'), { status: 401, statusText: 'Unauthorized' });

      await expectAsync(pending).toBeRejected();

      expect(service.isAuthenticated())
        .withContext('a failed sign-in cannot leave the previous session in place')
        .toBeFalse();
    });
  });

  describe('sign-out has no body shape of its own', () => {
    it('transmits the renewal credential and nothing else', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.logout());

      const request = httpMock.expectOne(LOGOUT_URL);
      expect(request.request.method).toBe('POST');

      // Exactly the renewal contract's single member. There is no distinct sign-out body
      // shape in the API's contract set, and asserting the key list rather than a subset
      // is what proves none was invented: an access credential, an identity, a device
      // name or an "everywhere" flag added later would all fail here.
      expect(request.request.body).toEqual({ refreshToken: FAKE_RENEWAL_TOKEN });
      expect(Object.keys(request.request.body as object)).toEqual(['refreshToken']);

      request.flush(null, { status: 204, statusText: 'No Content' });

      await expectAsync(pending).toBeResolved();
    });

    it('takes no argument, so a caller cannot choose which session to end', () => {
      // A sign-out that accepted a credential would let a caller revoke a session other
      // than its own. The operation reads the held credential itself instead.
      expect(service.logout.length).toBe(0);
    });
  });

  // =========================================================================
  // THE WIRE CONTRACT — four addresses, four verbs, one envelope
  // =========================================================================

  describe('login', () => {
    it('posts the credentials unmodified to the sign-in address', async () => {
      const request: LoginRequest = { username: 'admin', password: FAKE_PASSWORD };
      const pending = firstValueFrom(service.login(request));

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.body)
        .withContext('passed through exactly as the caller supplied it')
        .toEqual(request);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });

    it('accepts 200 as the success status, and does not expect a created status', async () => {
      // Signing in creates no resource and returns no location, so it answers 200. A spec
      // written against 201 would pass against a server that had it wrong.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock
        .expectOne(LOGIN_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN), {
          status: 200,
          statusText: 'OK',
        });
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);

      await expectAsync(pending).toBeResolved();
    });

    it('unwraps the success envelope and emits the described identity', async () => {
      // The payload arrives as the data member of an envelope. Reading it as a bare payload
      // would fail in the quietest possible way — every member would be undefined and the
      // stored session would be a shape-correct blank rather than an error — so the
      // unwrapping is asserted rather than assumed.
      const expected = currentUser({ userId: 42, displayName: 'Someone Else' });

      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock
        .expectOne(LOGIN_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN, expected);

      expect(await pending).toEqual(expected);
    });

    it('sets no headers on the credential exchange itself', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);

      // Asserting the header list is EMPTY is stronger than naming two headers that must
      // be absent, and it needs no header name at all: the bearer credential and the
      // correlation identifier are both attached by interceptors, none of which is
      // installed here, so anything present would have been set by the service.
      expect(sent.request.headers.keys())
        .withContext('the service sets no headers; the interceptor chain owns them')
        .toEqual([]);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });
  });

  describe('refresh', () => {
    it('posts the held renewal credential to the renewal address', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.refresh());

      const sent = httpMock.expectOne(REFRESH_URL);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.body).toEqual({ refreshToken: FAKE_RENEWAL_TOKEN });
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);
      await pending;
    });

    it('emits the renewed session, including the rotated credential pair', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.refresh());

      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);

      const session: AuthSession = await pending;

      expect(session.accessToken).toBe(FAKE_ACCESS_TOKEN_ROTATED);
      expect(session.refreshToken).toBe(FAKE_RENEWAL_TOKEN_ROTATED);
      expect(session.expiresAtUtc)
        .withContext('the published instant is stored verbatim, never recomputed')
        .toBe(EXPIRES_AT);
    });

    it('takes no argument, so the credential it presents cannot be supplied by a caller', () => {
      expect(service.refresh.length).toBe(0);
    });

    it('issues no request at all when no session is held', async () => {
      await expectAsync(firstValueFrom(service.refresh())).toBeRejected();

      // An empty credential is not posted for the server to refuse: doing so would answer
      // 400 and the caller would have to tell that apart from a genuine rejection.
      httpMock.expectNone(REFRESH_URL);
    });

    it('returns an observable rather than throwing where it is called', () => {
      // An interceptor composes this inside an error handler and must always receive an
      // observable back; a synchronous throw there would escape the pipeline entirely.
      expect(() => service.refresh()).not.toThrow();
    });

    it('coalesces concurrent callers onto one renewal', async () => {
      // The defect this prevents is specific and severe. Several requests expiring together
      // would each present the same renewal credential; the first would rotate it and the
      // rest would present a consumed one, which the server treats as a replay and answers
      // by revoking the whole family — signing the person out precisely because the client
      // tried to keep them signed in.
      await completeSignIn();

      const first = firstValueFrom(service.refresh());
      const second = firstValueFrom(service.refresh());
      const third = firstValueFrom(service.refresh());

      const requests = httpMock.match(REFRESH_URL);
      expect(requests.length).withContext('one renewal for three callers').toBe(1);

      requests[0].flush(
        credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED),
      );
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);

      const sessions = await Promise.all([first, second, third]);

      expect(sessions[0].accessToken).toBe(FAKE_ACCESS_TOKEN_ROTATED);
      expect(sessions[1]).toBe(sessions[0]);
      expect(sessions[2]).toBe(sessions[0]);
    });

    it('presents the rotated credential, not the consumed one, on a later renewal', async () => {
      await completeSignIn();

      const first = firstValueFrom(service.refresh());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);
      await first;

      const second = firstValueFrom(service.refresh());
      const sent = httpMock.expectOne(REFRESH_URL);
      expect(sent.request.body).toEqual({ refreshToken: FAKE_RENEWAL_TOKEN_ROTATED });

      sent.flush(credentialResponse('fake-access-token-third', 'fake-renewal-token-third'));
      answerIdentityBootstrap('fake-access-token-third');
      await second;
    });
  });

  describe('logout', () => {
    it('answers 204 with an empty body and still completes', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.logout());

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });

      await expectAsync(pending).toBeResolved();
    });

    it('sets no headers on the revocation', async () => {
      await completeSignIn();

      const pending = firstValueFrom(service.logout());

      const sent = httpMock.expectOne(LOGOUT_URL);
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(null, { status: 204, statusText: 'No Content' });
      await pending;
    });

    it('completes successfully even when revocation fails', async () => {
      // A person who asks to sign out must end up signed out on this device. Surfacing the
      // failure would be the opposite of what they asked for, and they could not act on it.
      await completeSignIn();

      const pending = firstValueFrom(service.logout());

      httpMock
        .expectOne(LOGOUT_URL)
        .flush({ title: 'Server Error', status: 500 }, { status: 500, statusText: 'Server Error' });

      await expectAsync(pending).toBeResolved();
      expect(service.isAuthenticated()).toBeFalse();
    });

    it('issues no request when there is no session to end', async () => {
      await expectAsync(firstValueFrom(service.logout())).toBeResolved();

      httpMock.expectNone(LOGOUT_URL);
    });
  });

  describe('me', () => {
    it('reads the describe-caller address with no headers of its own', async () => {
      // This is the one operation of the four that requires a credential, and it still sets
      // no header: the bearer credential is attached by the interceptor that owns it, so
      // that a refusal here remains recoverable. Setting one here would substitute a value
      // the interceptor is responsible for choosing and defeat that recovery.
      const pending = firstValueFrom(service.me());

      const sent = httpMock.expectOne(ME_URL);
      expect(sent.request.method).toBe('GET');
      expect(sent.request.headers.keys())
        .withContext('no credential and no correlation identifier are set here')
        .toEqual([]);

      sent.flush(identityResponse());
      await pending;
    });

    it('unwraps the envelope and emits the caller description', async () => {
      const expected = currentUser({
        roles: ['Administrators', 'Subscribers'],
        permissions: ['VIEW', 'EDIT'],
      });

      const pending = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse(expected));

      expect(await pending).toEqual(expected);
    });

    it('does not store a session, because describing a caller establishes none', async () => {
      const pending = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse());
      await pending;

      expect(service.isAuthenticated())
        .withContext('a description is not a credential')
        .toBeFalse();
    });
  });

  // =========================================================================
  // REFUSALS PROPAGATE UNTRANSLATED
  // =========================================================================

  describe('refusals', () => {
    it('re-throws the transport failure intact so the caller can render the document', async () => {
      const document = refusal(401, 'InvalidCredentials');

      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock
        .expectOne(LOGIN_URL)
        .flush(document, { status: 401, statusText: 'Unauthorized' });

      const reason = await rejectionOf(pending);

      expect(reason instanceof HttpErrorResponse)
        .withContext('no wrapping, no re-shaping, no bespoke error type')
        .toBeTrue();

      const response = reason as HttpErrorResponse;
      expect(response.status).toBe(401);
      expect(response.error)
        .withContext('the body reaches the caller byte-for-byte')
        .toEqual(document);
    });

    it('carries each of the three legacy outcome codes through unchanged', async () => {
      // Exactly three codes existed and no fourth is invented: EnterCode when a code is
      // wanted (`Login.ascx.vb:L175`, `:L180`), InvalidCode when a non-empty one is wrong
      // (`:L177-L178`), and UserNotAuthorized outside verified sign-up mode (`:L184`).
      // Translating any of them into client-side wording would move a server decision into
      // the client; that translation belongs to the error interceptor and the feature store.
      const codes = ['EnterCode', 'InvalidCode', 'UserNotAuthorized'] as const;

      for (const code of codes) {
        const document = refusal(403, code);

        const pending = firstValueFrom(
          service.login({ username: 'pending-approval', password: FAKE_PASSWORD }),
        );
        httpMock.expectOne(LOGIN_URL).flush(document, { status: 403, statusText: 'Forbidden' });

        const reason = await rejectionOf(pending);
        const body = (reason as HttpErrorResponse).error as ProblemDocument;

        // Bracket access, because index-signature members cannot be reached with a dot
        // under this compiler configuration.
        expect(body.errors['verificationCode']).toEqual([code]);
        expect(body.type).toBe(`urn:dnn:error:auth:${code}`);
      }
    });

    it('sets no state of its own when a code is demanded', async () => {
      // The legacy ladder was progressive and revealed the code rows on the FIRST rejection
      // (`Login.ascx.vb:L171`). That flag is screen state and belongs to the feature store
      // that owns the screen; the client neither records it nor changes its own behaviour on
      // a second attempt.
      const pending = firstValueFrom(
        service.login({ username: 'pending-approval', password: FAKE_PASSWORD }),
      );
      httpMock
        .expectOne(LOGIN_URL)
        .flush(refusal(403, 'EnterCode'), { status: 403, statusText: 'Forbidden' });
      await expectAsync(pending).toBeRejected();

      const second = firstValueFrom(
        service.login({ username: 'pending-approval', password: FAKE_PASSWORD }),
      );
      const sent = httpMock.expectOne(LOGIN_URL);

      expect(sent.request.body)
        .withContext('the second attempt is shaped by the caller alone, not by the first refusal')
        .toEqual({ username: 'pending-approval', password: FAKE_PASSWORD });

      sent.flush(refusal(403, 'EnterCode'), { status: 403, statusText: 'Forbidden' });
      await expectAsync(second).toBeRejected();
    });
  });

  // =========================================================================
  // FIDELITY — the legacy sentinel distinctions survive the crossing
  // =========================================================================

  describe('request fidelity', () => {
    it('transmits an empty verification code as an empty string rather than dropping it', async () => {
      // The legacy absent-text sentinel WAS the empty string, not a null reference:
      // `Library/Components/Shared/Null.vb:L71-L75` returns `""`, and the sign-in path
      // seeded its own message variable from that sentinel at `Login.ascx.vb:L166`. The
      // screen then branched on `txtVerification.Text <> ""` at `:L177` to tell a missing
      // code from a wrong one, answering EnterCode for the first and InvalidCode for the
      // second. Rewriting `''` into absent on the way out would collapse two different
      // server answers into one.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD, verificationCode: '' }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.body).toEqual({
        username: 'admin',
        password: FAKE_PASSWORD,
        verificationCode: '',
      });
      expect(Object.keys(sent.request.body as object))
        .withContext('the member is present, holding an empty string')
        .toContain('verificationCode');

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });

    it('leaves an omitted verification code absent rather than inventing a null', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(Object.keys(sent.request.body as object)).toEqual(['username', 'password']);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });

    it('transmits an explicit null verification code as null', async () => {
      // The contract admits null as well as absent, and the two are not silently merged in
      // either direction.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD, verificationCode: null }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.body).toEqual({
        username: 'admin',
        password: FAKE_PASSWORD,
        verificationCode: null,
      });

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });

    it('carries no authentication-type member, because one value is not a discriminator', async () => {
      // The legacy call at `Login.ascx.vb:L164` passed a four-character provider
      // discriminator positionally into an eight-argument sign-in, and `:L191` passed it a
      // second time when raising the authenticated event. It disappears with the single
      // bearer path. Asserting the exact key list is what proves it, and proves no tenant
      // key, tenant display name or caller address was added either — the last three were
      // ambient server-side values that a browser never posted, and accepting them from a
      // body would be a tenant-crossing and audit-spoofing surface.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(Object.keys(sent.request.body as object).sort()).toEqual(['password', 'username']);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;
    });
  });

  describe('response fidelity', () => {
    it('preserves a tenant key of 0 verbatim', async () => {
      const pending = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse(currentUser({ portalId: 0 })));

      expect((await pending).portalId).toBe(0);
    });

    it('preserves a tenant key of -1 verbatim', async () => {
      // Two facts collide here. The tenant table is declared with an identity seed of minus
      // one, so minus one is a real tenant key and the shipped default tenant is inserted
      // as zero; and `Null.vb:L41-L45` simultaneously defines minus one as the marker for a
      // missing integer. A truthiness test, a positive-value test or a coalescing default
      // would each rewrite a real tenant into a missing one, and nothing would report it.
      const pending = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse(currentUser({ portalId: -1 })));

      const described = await pending;
      expect(described.portalId).toBe(-1);
      expect(described.portalId).not.toBeNull();
    });

    it('preserves a caller key of 0 verbatim', async () => {
      const pending = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse(currentUser({ userId: 0 })));

      expect((await pending).userId).toBe(0);
    });

    it('preserves false advisory flags rather than treating them as absent', async () => {
      await completeSignIn();

      expect(service.mustUpdateProfile())
        .withContext('false arrived as itself and is read as itself')
        .toBeFalse();
    });

    it('preserves a true advisory flag through sign-in and through a renewal', async () => {
      // Why the projection exists at all: sign-in emits the identity rather than the
      // session, so without a re-published signal the advisory the server computed would
      // have no reader, and the legacy blocking profile prompt would be lost rather than
      // deferred. A renewal must not clear an unmet prompt either.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock
        .expectOne(LOGIN_URL)
        .flush(
          credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN, { mustUpdateProfile: true }),
        );
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN);
      await pending;

      expect(service.mustUpdateProfile()).toBeTrue();

      const renewal = firstValueFrom(service.refresh());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(
          credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED, {
            mustUpdateProfile: true,
          }),
        );
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);
      await renewal;

      expect(service.mustUpdateProfile())
        .withContext('a renewal does not clear an unmet prompt')
        .toBeTrue();
    });

    it('stores the published expiry instant as the string it arrived as', async () => {
      await completeSignIn();

      const renewal = firstValueFrom(service.refresh());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);

      const session = await renewal;

      // No arithmetic, no clock read, no conversion. How long credentials live is a
      // deployment fact — the access credential's lifetime matches the legacy forms
      // authentication timeout of sixty minutes declared at `Website/release.config:L147` —
      // whereas when THIS one lapses is a fact about this response, and only the latter is
      // published. The client neither computes nor re-derives it.
      expect(session.expiresAtUtc).toBe(EXPIRES_AT);
      expect(typeof session.expiresAtUtc).toBe('string');
    });
  });

  // =========================================================================
  // THE SURFACE IS CLOSED AT FOUR
  // =========================================================================

  describe('the endpoint surface is closed at four', () => {
    it('exposes exactly four operations and one documented private bootstrap', () => {
      // Enumerating the prototype is what makes "closed" mechanical rather than a review
      // note. Nothing else under the authentication prefix exists to be called, and each
      // absence is a decision: there is no separate verification operation, because the
      // code is a member of the sign-in body; no account-creation operation, because
      // creating an account is an administrative action on the user resource; no
      // credential-recovery or credential-change operation, because recovery is abolished
      // outright and changing a credential is an operation on the user resource; no
      // external-identity or single-sign-on operation, because there is one bearer path; no
      // challenge-image operation, because that guard is replaced by request rate limiting;
      // no permission-catalogue operation, because the catalogue is a separate read-only
      // resource; and no health operation, because the probe is published at the host root
      // outside the versioned prefix so a container check can reach it anonymously and
      // without spending a rate-limit budget.
      //
      // `loadCurrentUser` is compile-time private and therefore still a prototype member at
      // runtime. It is listed deliberately: it is the identity bootstrap the two credential
      // operations perform, and it is the single place a header is written by hand.
      expect(Object.getOwnPropertyNames(AuthService.prototype).sort()).toEqual([
        'constructor',
        'loadCurrentUser',
        'login',
        'logout',
        'me',
        'refresh',
      ]);
    });

    it('exposes the four operations as callable members', () => {
      expect(typeof service.login).toBe('function');
      expect(typeof service.refresh).toBe('function');
      expect(typeof service.logout).toBe('function');
      expect(typeof service.me).toBe('function');
    });

    it('addresses only the four declared paths across every operation', async () => {
      // Drives all four operations in one test and lets verify() prove that between them
      // they touched nothing but the four addresses this file names.
      await completeSignIn();

      const described = firstValueFrom(service.me());
      httpMock.expectOne(ME_URL).flush(identityResponse());
      await described;

      const renewal = firstValueFrom(service.refresh());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      answerIdentityBootstrap(FAKE_ACCESS_TOKEN_ROTATED);
      await renewal;

      const signOut = firstValueFrom(service.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;

      expect(service.isAuthenticated()).toBeFalse();
    });
  });
});
