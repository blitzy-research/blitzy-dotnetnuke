/**
 * Specification for {@link AuthService} — the authentication TRANSPORT, whose surface is
 * closed at four operations, which holds nothing and which orchestrates nothing.
 *
 * The legacy application shipped no automated tests of any kind, so nothing here is a
 * port. Every expectation was authored from two measured sources: the Web Forms sign-in
 * screen this client replaces, cited by line throughout, and the destination wire
 * contract read from the service and model this file exercises.
 *
 * MIGRATION: THIS FILE USED TO SPECIFY A SESSION LIFECYCLE AS WELL AS A TRANSPORT, and it
 *   is a great deal shorter for having stopped. The service it describes previously cleared
 *   and stored sessions through the custodian, held the single-flight renewal slot,
 *   performed a second describe-caller read inside sign-in and renewal, re-published three
 *   session signals, and turned a refused revocation into successful completion. All of
 *   that is session lifecycle rather than API communication, which Minimal Change Clause
 *   item 5 places outside a service, so it now lives on `core/state/auth.store.ts` and is
 *   specified by `core/state/auth.store.spec.ts`. Cases about coalescing, epoch races,
 *   resurrection after a sign-out, the revocation report and the sign-out policy were moved
 *   there rather than deleted — they are assertions about an owner, and they followed the
 *   owner. What remains here is the wire contract and the decoding of it.
 *
 * ## What this specification is FOR
 *
 * The valuable assertions here are still the negative ones. Authentication is where
 * orchestration creep is most likely — a retry folded into the client, a credential
 * cached in a second place, a status code interpreted locally — and each of those is
 * invisible to a compiler and to a reviewer skimming a diff. Four properties are
 * therefore proved mechanically rather than trusted:
 *
 * - **One call is one request.** Every operation issues exactly the request it declares
 *   and not one more. Recovering from an expired credential is the interceptor's
 *   responsibility and composing a two-request sign-in is the store's; no interceptor is
 *   installed in this harness and no store is driven, so either smuggled in here would
 *   show up as an unexpected request.
 * - **No credential is held here.** The service owns exactly one field, the transport
 *   itself. It exposes no signal, it reads nothing from the custodian, and the custodian is
 *   resolved from the same injector purely to prove that it stays empty across every call.
 * - **No status code is interpreted here.** A refusal — including a refused revocation —
 *   propagates exactly as the server sent it. That is the substance of one of the findings
 *   this file now pins: absorbing a failed withdrawal and reporting completion made a live
 *   renewal credential indistinguishable from a withdrawn one.
 * - **No response member is taken on trust.** Each payload-bearing operation decodes its
 *   body, so a drifted or hostile shape is refused at the seam rather than carried into a
 *   bearer header, a role list or a permission check.
 *
 * `httpMock.verify()` in `afterEach` is what makes the first three enforceable: it fails
 * the test if the service issued a request the test did not account for. It is the single
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
 * - **Session commits, coalescing, epoch races, the revocation report and the sign-out
 *   policy.** Every one of these belongs to the lifecycle owner and is specified beside it.
 * - **Navigation, problem-document wording and credential parsing.** Each belongs to a
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

import type { CurrentUser, LoginResponse } from '../models/auth.model';
import { isContractViolation } from '../utils/decode.util';
import { AuthService } from './auth.service';
// A value import, unlike the type-only import above: the custodian is resolved from the same
// injector in order to prove that it stays EMPTY across every operation. This service used to
// write to it, and pinning the absence of those writes is what stops them creeping back.
import { TokenStorageService } from './token-storage.service';

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

/** `POST` — revokes a renewal credential. Anonymous, and answers 204 for what it finds. */
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
    isPortalAdministrator: false,
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
  accessToken: string = FAKE_ACCESS_TOKEN,
  refreshToken: string = FAKE_RENEWAL_TOKEN,
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
  let tokenStorage: TokenStorageService;

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
    tokenStorage = TestBed.inject(TokenStorageService);
  });

  afterEach(() => {
    // The proof that nothing is orchestrated. This fails on any request the test did not
    // account for, which is exactly what a recovery attempt folded into the service, a
    // second describe-caller read, or a revoke-then-confirm sequence would produce.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  // -------------------------------------------------------------------------

  /** Resolves to the rejection reason, or null when the promise resolved instead. */
  async function rejectionOf(pending: Promise<unknown>): Promise<unknown> {
    return pending.then(
      () => null,
      (reason: unknown) => reason,
    );
  }

  /**
   * Asserts that a body the contract forbids is REFUSED rather than carried.
   *
   * The refusal is checked by kind and by position, not by message: the position is the
   * durable part of the contract and the wording is not. Paths are stated as the decoder
   * reports them — rooted at `response`, then the envelope's payload member — so a case
   * pins WHERE the drift was found and not merely that something was wrong.
   *
   * @param pending The in-flight call.
   * @param path The position the violation must be reported at.
   */
  async function expectViolationAt(pending: Promise<unknown>, path: string): Promise<void> {
    const reason = await rejectionOf(pending);

    expect(isContractViolation(reason))
      .withContext(`a body violating the contract at ${path} must be refused, not carried`)
      .toBeTrue();
    expect((reason as { path: string }).path).toBe(path);
  }

  /** Asserts that the custodian holds nothing whatsoever. */
  function expectNothingHeld(): void {
    expect(tokenStorage.session())
      .withContext('the transport never commits a session')
      .toBeNull();
    expect(tokenStorage.accessToken()).toBeNull();
    expect(tokenStorage.refreshToken()).toBeNull();
    expect(tokenStorage.isAuthenticated()).toBeFalse();
  }

  // =========================================================================
  // THE FOUR NEGATIVE PROOFS
  //
  // Written first because they are what this specification exists for.
  // =========================================================================

  describe('one call issues one request, and orchestrates nothing', () => {
    it('performs no second read of its own when credentials are exchanged', async () => {
      // The DEFINING property of the transport-only shape, and the one that most obviously
      // regressed before. Sign-in is genuinely two requests, but the SECOND one is composed
      // by the lifecycle owner: it is that owner which knows the freshly issued token must
      // be presented by hand and must not be stored until the identity has arrived. Here,
      // exactly one request is issued, and `verify()` proves it.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush(credentialResponse());
      httpMock.expectNone(ME_URL);

      const response = await pending;

      expect(response.accessToken).toBe(FAKE_ACCESS_TOKEN);
      expectNothingHeld();
    });

    it('performs no second read of its own when a credential is renewed', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      httpMock.expectNone(ME_URL);

      expect((await pending).refreshToken).toBe(FAKE_RENEWAL_TOKEN_ROTATED);
      expectNothingHeld();
    });

    it('does not attempt a renewal when sign-in is refused', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
      httpMock.expectNone(REFRESH_URL);

      await expectAsync(pending).toBeRejected();
    });

    it('does not sign out, or retry, when a renewal is refused', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock.expectOne(REFRESH_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(LOGOUT_URL);

      await expectAsync(pending).toBeRejected();
    });

    it('does not back off or repeat when the rate limiter refuses sign-in', async () => {
      // The server's fixed-window limiter is the named compensating control for the deleted
      // challenge-image gate at `Login.ascx.vb:L162`. A client-side back-off would be a
      // second, weaker limiter and would hide the first one's answer from the caller.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock
        .expectOne(LOGIN_URL)
        .flush(refusal(429, 'TooManyAttempts'), { status: 429, statusText: 'Too Many Requests' });
      httpMock.expectNone(LOGIN_URL);

      expect((await rejectionOf(pending) as HttpErrorResponse).status).toBe(429);
    });

    it('coalesces nothing, so two renewals are two requests', async () => {
      // The inverse of the store's guarantee, asserted HERE so the two files together say
      // where the guarantee lives. A slot in this service would be a second source of truth
      // for the same fact: signing out abandons the owner's slot and advances its session
      // generation, and a slot held here could be reached by neither.
      const first = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));
      const second = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      const issued = httpMock.match(REFRESH_URL);
      expect(issued.length)
        .withContext('no coalescing here: serialising renewals belongs to the session owner')
        .toBe(2);

      issued[0].flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      issued[1].flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));

      await first;
      await second;
    });
  });

  describe('holds no state of any kind', () => {
    it('owns exactly one field, and it is the transport', () => {
      // Enumerating the instance is what makes "holds nothing" mechanical rather than a
      // review note. A credential cached in a field, a single-flight slot, a revocation flag
      // or a re-published signal would each appear here and fail this case.
      expect(Object.keys(service)).toEqual(['http']);
    });

    it('publishes no signal, so nothing reads session state through it', () => {
      // The three projections this service used to re-expose — whether a session is held,
      // who the caller is, and whether a profile update is outstanding — were all readings
      // of the custodian's own signals, re-exported one layer away from their owner. Screens
      // read them from the lifecycle owner now, and their absence here is asserted rather
      // than assumed.
      const surface = service as unknown as Record<string, unknown>;

      for (const removed of ['isAuthenticated', 'currentUser', 'mustUpdateProfile']) {
        expect(surface[removed])
          .withContext(`${removed} belongs to the session's owner, not to a transport`)
          .toBeUndefined();
      }
    });

    it('leaves the custodian untouched across all four operations', async () => {
      const signIn = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock.expectOne(LOGIN_URL).flush(credentialResponse());
      await signIn;
      expectNothingHeld();

      const renewal = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      await renewal;
      expectNothingHeld();

      const described = firstValueFrom(service.me(FAKE_ACCESS_TOKEN));
      httpMock.expectOne(ME_URL).flush(identityResponse());
      await described;
      expectNothingHeld();

      const signOut = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;
      expectNothingHeld();
    });

    it('takes the renewal credential as an argument rather than reading one', async () => {
      // Both halves matter. The credential is whatever the caller supplied, and NOTHING is
      // read from the custodian to obtain or to check one — which is why this call succeeds
      // with no session held at all, and why the body carries the argument verbatim.
      const pending = firstValueFrom(service.refresh({ refreshToken: 'fake-supplied-credential' }));

      const sent = httpMock.expectOne(REFRESH_URL);
      expect(sent.request.body).toEqual({ refreshToken: 'fake-supplied-credential' });

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      await pending;
    });
  });

  describe('interprets no status code', () => {
    it('propagates a refused revocation instead of reporting success', async () => {
      // ⚠ THE FINDING THIS CASE EXISTS FOR. The previous implementation ended sign-out with
      // `catchError(() => of(undefined))`, so a 400, a 429, a 503 or a dropped connection
      // completed successfully and a renewal credential still live on the server was
      // indistinguishable from a withdrawn one. Whether local sign-out proceeds anyway is a
      // POLICY, and the lifecycle owner applies it — but it cannot apply a policy to an
      // outcome it is never told about.
      for (const status of [400, 429, 503]) {
        const pending = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));

        httpMock
          .expectOne(LOGOUT_URL)
          .flush(refusal(status, 'RevocationRefused'), { status, statusText: 'Refused' });

        const reason = await rejectionOf(pending);

        expect(reason instanceof HttpErrorResponse)
          .withContext('the refusal arrives as a refusal, unwrapped and unabsorbed')
          .toBeTrue();
        expect((reason as HttpErrorResponse).status).toBe(status);
      }
    });

    it('propagates a transport-level revocation failure too', async () => {
      const pending = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock
        .expectOne(LOGOUT_URL)
        .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      expect(await rejectionOf(pending))
        .withContext('an unreachable endpoint is a failure, not a withdrawal')
        .toBeInstanceOf(HttpErrorResponse);
    });

    it('retries nothing when revocation is refused', async () => {
      const pending = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 503, statusText: 'Service Unavailable' });
      httpMock.expectNone(LOGOUT_URL);

      await expectAsync(pending).toBeRejected();
    });
  });

  // =========================================================================
  // THE WIRE CONTRACT, OPERATION BY OPERATION
  // =========================================================================

  describe('login', () => {
    it('posts the credentials unmodified to the sign-in address', async () => {
      const request = { username: 'admin', password: FAKE_PASSWORD };
      const pending = firstValueFrom(service.login(request));

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.body).toEqual(request);

      sent.flush(credentialResponse());
      await pending;
    });

    it('accepts 200 as the success status, and does not expect a created status', async () => {
      // A sign-in creates no resource. The API answers 200 with the pair, and a client that
      // insisted on 201 would reject every successful exchange.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush(credentialResponse(), { status: 200, statusText: 'OK' });

      expect((await pending).accessToken).toBe(FAKE_ACCESS_TOKEN);
    });

    it('unwraps the success envelope and returns the decoded pair', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush(credentialResponse());

      const response = await pending;

      // The whole payload, not just the credential: the identity the pair was issued for
      // travels with it and is the value the lifecycle owner composes its session from.
      expect(response.refreshToken).toBe(FAKE_RENEWAL_TOKEN);
      expect(response.expiresAtUtc).toBe(EXPIRES_AT);
      expect(response.user.username).toBe('admin');
    });

    it('sets no headers on the credential exchange itself', async () => {
      // Anonymous by contract, and the empty header set proves the service added none — no
      // interceptor is installed in this harness, so nothing else could have.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(credentialResponse());
      await pending;
    });

    it('transmits a tenant selector as a query parameter, not in the body', async () => {
      // The endpoint resolves the tenant from the arrival host and admits the selector only
      // as the fallback for a host with no alias row. A body member would be a
      // tenant-crossing surface; a query parameter is what the API declares.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }, { portalId: 0 }),
      );

      const sent = httpMock.expectOne(
        (candidate) => candidate.url === LOGIN_URL && candidate.params.get('portalId') === '0',
      );
      expect(Object.keys(sent.request.body as object).sort()).toEqual(['password', 'username']);

      sent.flush(credentialResponse());
      await pending;
    });

    it('sends no selector parameter when none is supplied', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(sent.request.params.keys()).toEqual([]);

      sent.flush(credentialResponse());
      await pending;
    });
  });

  describe('refresh', () => {
    it('posts the supplied renewal credential to the renewal address', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      const sent = httpMock.expectOne(REFRESH_URL);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.body).toEqual({ refreshToken: FAKE_RENEWAL_TOKEN });
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      await pending;
    });

    it('returns the rotated pair, so the consumed credential is replaced', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));

      const response = await pending;
      expect(response.accessToken).toBe(FAKE_ACCESS_TOKEN_ROTATED);
      expect(response.refreshToken).toBe(FAKE_RENEWAL_TOKEN_ROTATED);
    });

    it('answers with the same payload shape sign-in does', async () => {
      // The API declares no separate renewal response, so one decoder serves both. A second
      // shape here would be an invention the server does not honour.
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock
        .expectOne(REFRESH_URL)
        .flush(
          credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED, {
            mustUpdateProfile: true,
          }),
        );

      const response = await pending;
      expect(Object.keys(response).sort()).toEqual([
        'accessToken',
        'expiresAtUtc',
        'mustChangePassword',
        'mustUpdateProfile',
        'passwordExpiring',
        'refreshToken',
        'user',
      ]);
      expect(response.mustUpdateProfile).toBeTrue();
    });
  });

  describe('logout', () => {
    it('transmits the renewal credential and nothing else', async () => {
      // Sign-out has NO body shape of its own: the API declares a required body of the same
      // shape renewal uses. Posting nothing would be refused as malformed before the
      // operation ran, and inventing a member would be a contract this server does not have.
      const pending = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));

      const sent = httpMock.expectOne(LOGOUT_URL);
      expect(sent.request.method).toBe('POST');
      expect(Object.keys(sent.request.body as object)).toEqual(['refreshToken']);
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(null, { status: 204, statusText: 'No Content' });
      await pending;
    });

    it('answers 204 with an empty body and still completes', async () => {
      // No envelope is expected and none can arrive: 204 forbids a body, which is why this
      // is the one operation with nothing to decode.
      const pending = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });

      await expectAsync(pending).toBeResolved();
    });
  });

  describe('me', () => {
    it('reads the describe-caller address with no headers of its own', async () => {
      // Once a session is held the bearer credential is attached by the interceptor. The
      // empty header set is the proof that this call leaves it to do so.
      const pending = firstValueFrom(service.me());

      const sent = httpMock.expectOne(ME_URL);
      expect(sent.request.method).toBe('GET');
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(identityResponse());
      await pending;
    });

    it('presents a supplied credential by hand, for the bootstrap read alone', async () => {
      // The ONE place a credential header is written by hand, and it is not an alternative
      // to the plain read — it is the same read performed at a moment when the interceptor
      // cannot serve it, because the rotated token exists only as a local value and is
      // deliberately not stored until the identity has arrived. The count is asserted as
      // well as the value, so a second hand-set header would fail here.
      const pending = firstValueFrom(service.me(FAKE_ACCESS_TOKEN_ROTATED));

      const sent = httpMock.expectOne(ME_URL);
      expect(sent.request.headers.keys().length).toBe(1);
      expect(sent.request.headers.get('Authorization')).toBe(
        `Bearer ${FAKE_ACCESS_TOKEN_ROTATED}`,
      );

      sent.flush(identityResponse());
      await pending;
    });

    it('treats an explicit null credential as no credential', async () => {
      // The optional argument admits null as well as absent, and the two mean the same
      // thing here: leave the credential to the interceptor. Emitting `Bearer null` would be
      // refused on every request.
      const pending = firstValueFrom(service.me(null));

      const sent = httpMock.expectOne(ME_URL);
      expect(sent.request.headers.keys()).toEqual([]);

      sent.flush(identityResponse());
      await pending;
    });

    it('unwraps the envelope and returns the decoded caller description', async () => {
      const pending = firstValueFrom(service.me());

      httpMock
        .expectOne(ME_URL)
        .flush(identityResponse(currentUser({ roles: ['Administrators', 'Editors'] })));

      const described = await pending;
      expect(described.username).toBe('admin');
      expect(described.roles).toEqual(['Administrators', 'Editors']);
    });
  });

  // =========================================================================
  // DECODING — no response member is taken on trust
  // =========================================================================

  describe('refuses a response that does not match its contract', () => {
    // ⚠ THE SECOND FINDING THIS FILE PINS. Both decoders existed and neither had a
    // production consumer: the calls asserted their own response types through the
    // transport's type argument, which is a promise the compiler makes on the server's
    // behalf and cannot keep. Same-origin is not the same as in-process — the response
    // crosses a reverse proxy — and the values concerned become a bearer header, a role
    // list and a permission check. Each case below flushes a body that type-checks as JSON
    // and is refused at the seam.

    it('refuses a credential response whose envelope root is missing', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({ meta: null });

      await expectViolationAt(pending, 'response.data');
    });

    it('refuses a credential response whose payload is null', async () => {
      // A 200 whose payload is null is not a success the server can produce: its own helper
      // answers 404 when it has nothing to send. Admitting null here would let a session be
      // composed from nothing.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({ data: null, meta: null });

      await expectViolationAt(pending, 'response.data');
    });

    it('refuses a blank access token rather than sending an empty bearer header', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({
        data: { ...credentialResponse().data, accessToken: '' },
        meta: null,
      });

      await expectViolationAt(pending, 'response.data.accessToken');
    });

    it('refuses a blank renewal credential rather than establishing an unrenewable session', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));

      httpMock.expectOne(REFRESH_URL).flush({
        data: { ...credentialResponse().data, refreshToken: '   ' },
        meta: null,
      });

      await expectViolationAt(pending, 'response.data.refreshToken');
    });

    it('refuses an unparseable expiry instant rather than storing an invalid date', async () => {
      // The value is stored as the string it arrived as and handed to a date constructor by
      // whoever compares it against a clock, so an unparseable string becomes an invalid
      // date that compares false and makes an expired session look current.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({
        data: { ...credentialResponse().data, expiresAtUtc: 'not-an-instant' },
        meta: null,
      });

      await expectViolationAt(pending, 'response.data.expiresAtUtc');
    });

    it('refuses a stringified advisory flag rather than coercing it', async () => {
      // Coercing the string `'false'` would raise a blocking password-change requirement
      // the server never asserted; coercing the other way would drop one it did.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({
        data: { ...credentialResponse().data, mustChangePassword: 'false' },
        meta: null,
      });

      await expectViolationAt(pending, 'response.data.mustChangePassword');
    });

    it('refuses a credential response whose nested identity is malformed', async () => {
      // The nested position is reported, not just the fact of a violation: the identity
      // inside a credential response populates the session, the shell header and the
      // permission lists, so where the drift is matters when it is diagnosed.
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      httpMock.expectOne(LOGIN_URL).flush({
        data: {
          ...credentialResponse().data,
          user: { ...currentUser(), roles: null },
        },
        meta: null,
      });

      await expectViolationAt(pending, 'response.data.user.roles');
    });

    it('refuses a caller description whose role list is not a list of strings', async () => {
      const pending = firstValueFrom(service.me());

      httpMock
        .expectOne(ME_URL)
        .flush({ data: { ...currentUser(), roles: ['Administrators', 7] }, meta: null });

      await expectViolationAt(pending, 'response.data.roles[1]');
    });

    it('refuses a caller description whose superuser flag is a string', async () => {
      // `'false'` is truthy, so an unchecked read here would grant the whole console to a
      // caller the server described as an ordinary member.
      const pending = firstValueFrom(service.me());

      httpMock
        .expectOne(ME_URL)
        .flush({ data: { ...currentUser(), isSuperUser: 'false' }, meta: null });

      await expectViolationAt(pending, 'response.data.isSuperUser');
    });

    it('refuses a caller description whose identifier is fractional', async () => {
      const pending = firstValueFrom(service.me());

      httpMock.expectOne(ME_URL).flush({ data: { ...currentUser(), userId: 7.5 }, meta: null });

      await expectViolationAt(pending, 'response.data.userId');
    });

    it('refuses a caller description that is not an object at all', async () => {
      const pending = firstValueFrom(service.me());

      httpMock.expectOne(ME_URL).flush({ data: 'admin', meta: null });

      await expectViolationAt(pending, 'response.data');
    });

    it('admits an empty display name, because the column defaults to one', async () => {
      // The counterpart to the refusals above, and it is the reason none of them uses a
      // non-empty-string rule for this member: `Users.DisplayName` is NOT NULL defaulting to
      // `''`, so the empty-string encoding of "absent" is a schema constraint here rather
      // than a data-layer convention. A stricter rule would refuse real accounts.
      const pending = firstValueFrom(service.me());

      httpMock.expectOne(ME_URL).flush(identityResponse(currentUser({ displayName: '' })));

      expect((await pending).displayName).toBe('');
    });
  });

  // =========================================================================
  // REFUSALS — the server's own answer reaches the caller
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

      sent.flush(credentialResponse());
      await pending;
    });

    it('leaves an omitted verification code absent rather than inventing a null', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );

      const sent = httpMock.expectOne(LOGIN_URL);
      expect(Object.keys(sent.request.body as object)).toEqual(['username', 'password']);

      sent.flush(credentialResponse());
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

      sent.flush(credentialResponse());
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

      sent.flush(credentialResponse());
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
      // The decoder is deliberately not range-checked for this reason.
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
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock.expectOne(LOGIN_URL).flush(credentialResponse());

      const response = await pending;

      expect(response.mustUpdateProfile)
        .withContext('false arrived as itself and is returned as itself')
        .toBeFalse();
      expect(response.mustChangePassword).toBeFalse();
      expect(response.passwordExpiring).toBeFalse();
    });

    it('carries a true advisory flag out of both credential operations', async () => {
      // The advisory is computed by the server and travels with the pair. Whether an unmet
      // prompt survives a renewal is the session owner's concern; that it REACHES the owner
      // from both operations is this file's.
      const signIn = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock
        .expectOne(LOGIN_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN, FAKE_RENEWAL_TOKEN, {
          mustUpdateProfile: true,
        }));
      expect((await signIn).mustUpdateProfile).toBeTrue();

      const renewal = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(
          credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED, {
            mustUpdateProfile: true,
          }),
        );
      expect((await renewal).mustUpdateProfile).toBeTrue();
    });

    it('returns the published expiry instant as the string it arrived as', async () => {
      const pending = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));

      const response = await pending;

      // No arithmetic, no clock read, no conversion. How long credentials live is a
      // deployment fact — the access credential's lifetime matches the legacy forms
      // authentication timeout of sixty minutes declared at `Website/release.config:L147` —
      // whereas when THIS one lapses is a fact about this response, and only the latter is
      // carried. The client neither computes nor re-derives it.
      expect(response.expiresAtUtc).toBe(EXPIRES_AT);
      expect(typeof response.expiresAtUtc).toBe('string');
    });
  });

  // =========================================================================
  // THE SURFACE IS CLOSED AT FOUR
  // =========================================================================

  describe('the endpoint surface is closed at four', () => {
    it('exposes exactly four operations and nothing else', () => {
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
      // MIGRATION: a fifth, private member used to appear in this list — the identity
      //   bootstrap the two credential operations performed for themselves. It is gone
      //   because the bootstrap is now composed by the session's owner out of the PUBLIC
      //   describe-caller operation, which takes the freshly issued credential as an
      //   optional argument. One operation, one endpoint, no private duplicate of it.
      expect(Object.getOwnPropertyNames(AuthService.prototype).sort()).toEqual([
        'constructor',
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
      // they touched nothing but the four addresses this file names — and that each issued
      // exactly one request.
      const signIn = firstValueFrom(
        service.login({ username: 'admin', password: FAKE_PASSWORD }),
      );
      httpMock.expectOne(LOGIN_URL).flush(credentialResponse());
      await signIn;

      const described = firstValueFrom(service.me(FAKE_ACCESS_TOKEN));
      httpMock.expectOne(ME_URL).flush(identityResponse());
      await described;

      const renewal = firstValueFrom(service.refresh({ refreshToken: FAKE_RENEWAL_TOKEN }));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialResponse(FAKE_ACCESS_TOKEN_ROTATED, FAKE_RENEWAL_TOKEN_ROTATED));
      await renewal;

      const signOut = firstValueFrom(service.logout({ refreshToken: FAKE_RENEWAL_TOKEN_ROTATED }));
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;

      expectNothingHeld();
    });
  });
});
