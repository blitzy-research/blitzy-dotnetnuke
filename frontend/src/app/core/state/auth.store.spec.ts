/**
 * Specification for `core/state/auth.store.ts`. Four properties of that store are load-bearing, and each
 * of them is a property no compiler can check, so each is proven here by exercising the store against a
 * mock transport rather than by reading it: 1.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { CurrentUser, LoginRequest, LoginResponse } from '../models/auth.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import { AuthService } from '../services/auth.service';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';
import { isContractViolation } from '../utils/decode.util';
import { AUTH_STORE_PHASES, AuthStore, REVOCATION_FAILED_MESSAGE } from './auth.store';
import { SESSION_ENDED_MESSAGE, SessionTeardownService } from './session-teardown.service';

// PATHS

const LOGIN_URL = '/api/v1/auth/login';

const REFRESH_URL = '/api/v1/auth/refresh';

const LOGOUT_URL = '/api/v1/auth/logout';

const ME_URL = '/api/v1/auth/me';

// THE FAILURE-CODE CHANNEL

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** Legacy `"EnterCode"`. */
const VERIFICATION_REQUIRED_CODE = 'auth.verification_required';

/** Legacy `"InvalidCode"`. */
const VERIFICATION_CODE_INVALID_CODE = 'auth.verification_code_invalid';

/** Legacy `"UserNotAuthorized"`. */
const ACCOUNT_NOT_APPROVED_CODE = 'auth.account_not_approved';

/** Wording carried with the verification-required outcome. */
const VERIFICATION_REQUIRED_MESSAGE = 'Enter Your Verification Code';

/** Wording carried with the invalid-code outcome. */
const VERIFICATION_CODE_INVALID_MESSAGE = 'Invalid Verification Code';

/** Wording carried with the not-authorised outcome. */
const ACCOUNT_NOT_APPROVED_MESSAGE = 'You are not currently authorized to login to this site.';

/**
 * A refused credential, which is deliberately OUTSIDE the closed verification vocabulary — reaching the
 * ladder with it would be the defect this file guards against.
 */
const INVALID_CREDENTIALS_CODE = 'auth.invalid_credentials';

const ACCOUNT_LOCKED_OUT_CODE = 'auth.account_locked_out';

/**
 * The legacy message literal, spelled exactly as `Login.ascx.vb:L175` spelled it. Present as a NEGATIVE
 * fixture.
 */
const LEGACY_ENTER_CODE_LITERAL = 'EnterCode';

// CREDENTIAL-SHAPED FIXTURES
// ⚠ OBVIOUSLY FAKE, WITHOUT EXCEPTION. The legacy anti-pattern is a real decryption key committed to source
// control at `Website/release.config:L91-L92`, identical in the development configuration, which — combined
// with reversible storage and retrieval both enabled — made every stored credential recoverable by anyone
// who could read the repository.

const FAKE_ACCESS_TOKEN = 'fake-access-token-not-a-real-credential';

const FAKE_ACCESS_TOKEN_ROTATED = 'fake-rotated-access-token-not-a-real-credential';

const FAKE_RENEWAL_TOKEN = 'fake-renewal-token-not-a-real-credential';

const FAKE_RENEWAL_TOKEN_ROTATED = 'fake-rotated-renewal-token-not-a-real-credential';

const FAKE_PASSWORD = 'not-a-real-password';

const ACCOUNT_NAME = 'admin';

const OTHER_ACCOUNT_NAME = 'no-such-account';

const SUBMITTED_VERIFICATION_CODE = 'wrong-code';

/** The expiry instant, as an absolute instant in Coordinated Universal Time. */
const EXPIRES_AT_UTC = '2100-01-01T00:00:00.000Z';

/** A rotated instant, so a renewal can be told from the sign-in that preceded it. */
const EXPIRES_AT_UTC_ROTATED = '2100-01-02T00:00:00.000Z';

// ---------------------------------------------------------------------------
// DIAGNOSTIC FIXTURES
// ---------------------------------------------------------------------------

/** The identifier the server validated for the request. */
const CORRELATION_ID = 'correlation-id-for-this-attempt';

/** The framework's own request identifier, which is a different value in a different format. */
const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';

/** A message carrying an executable payload. NOT hypothetical. */
const SCRIPT_BEARING_DETAIL = '<script>alert(1)</script>';

const UNCLOSED_BREAK_DETAIL = '<br>The account could not be created.';

const CLOSED_BREAK_DETAIL = '<br/>The account could not be created.';

/** A per-field key, reproduced with the server's own casing. */
const MODEL_STATE_KEY = 'UserName';

// THE RESPONSE ENVELOPE

interface SuccessEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

// FACTORIES
// Functions rather than shared constants, so no specification can observe a value a previous one mutated.

/**
 * An identity. The default tenant key is ZERO, which is a real key rather than a tidy one: the tenant
 * table's key column is seeded at minus one, so both minus one and zero identify real tenants and neither
 * may be read as absence.
 */
function currentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 7,
    portalId: 0,
    portalName: 'Primary Portal',
    username: ACCOUNT_NAME,
    displayName: 'Administrator',
    email: 'admin@example.test',
    isSuperUser: false,
    isPortalAdministrator: false,
    roles: ['Administrators'],
    permissions: ['VIEW'],
    ...overrides,
  };
}

/** Credentials, carrying only the three members the contract declares. */
function credentials(overrides: Partial<LoginRequest> = {}): LoginRequest {
  return {
    username: ACCOUNT_NAME,
    password: FAKE_PASSWORD,
    ...overrides,
  };
}

/**
 * A credential-exchange payload, wrapped. All three advisories default to `false`, because `false` is
 * DATA on this contract and a fixture that omitted them would not compile.
 */
function credentialPayload(
  overrides: Partial<LoginResponse> = {},
): SuccessEnvelope<LoginResponse> {
  return {
    data: {
      accessToken: FAKE_ACCESS_TOKEN,
      expiresAtUtc: EXPIRES_AT_UTC,
      refreshToken: FAKE_RENEWAL_TOKEN,
      mustChangePassword: false,
      passwordExpiring: false,
      mustUpdateProfile: false,
      user: currentUser(),
      ...overrides,
    },
    meta: null,
  };
}

/** An identity payload, wrapped. */
function identityPayload(user: CurrentUser = currentUser()): SuccessEnvelope<CurrentUser> {
  return { data: user, meta: null };
}

/** The `type` member for a code, with the prefix the resolver requires. */
function failureType(code: string): string {
  return `${FAILURE_TYPE_PREFIX}${code}`;
}

/** A problem document carrying a code. */
function refusal(code: string, status: number, overrides: Partial<ProblemDetails> = {}): ProblemDetails {
  return {
    type: failureType(code),
    title: 'The request was refused.',
    status,
    detail: 'The sign-in attempt was refused.',
    instance: LOGIN_URL,
    ...overrides,
  };
}

/** A problem document carrying NO code. */
function codelessRefusal(status: number, overrides: Partial<ProblemDetails> = {}): ProblemDetails {
  return {
    title: 'The request was refused.',
    status,
    detail: 'The request could not be completed.',
    ...overrides,
  };
}

/** A per-field failure document, whose dictionary member is required rather than optional. */
function fieldRefusal(): ValidationProblemDetails {
  return {
    type: failureType('validation.rejected'),
    title: 'One or more validation errors occurred.',
    status: 422,
    detail: 'The submitted values were rejected.',
    errors: {
      [MODEL_STATE_KEY]: ['The account name is required.'],
    },
  };
}

// ---------------------------------------------------------------------------
// INSPECTION HELPERS
// ---------------------------------------------------------------------------

/**
 * The sorted member names of a request body. The body arrives loosely typed from the mock transport, so
 * it is narrowed to `unknown` on the way in and tested structurally.
 *
 * @param body A request body.
 * @returns The member names, sorted, or nothing when the body is not an object.
 */
function bodyMemberNames(body: unknown): readonly string[] {
  if (typeof body !== 'object' || body === null) {
    return [];
  }

  return Object.keys(body).sort();
}

/**
 * @param value Anything the store published.
 * @param secret The fixture value that must not appear.
 * @param seen The object graph already walked, so a cycle terminates.
 * @returns True when the secret appears anywhere inside the value.
 */
function containsSecret(value: unknown, secret: string, seen = new WeakSet<object>()): boolean {
  if (typeof value === 'string') {
    return value.includes(secret);
  }

  if (typeof value === 'object' && value !== null) {
    if (seen.has(value)) {
      return false;
    }

    seen.add(value);
  }

  if (Array.isArray(value)) {
    const entries: readonly unknown[] = value;

    return entries.some((entry) => containsSecret(entry, secret, seen));
  }

  if (typeof value === 'object' && value !== null) {
    return Object.values(value as Record<string, unknown>).some((entry: unknown) =>
      containsSecret(entry, secret, seen),
    );
  }

  return false;
}

/**
 * The store's two injected collaborators, excluded from the custody sweep below. Excluded because they
 * are not slices of this store and because ONE OF THEM HOLDS A CREDENTIAL BY DESIGN — the custodian is
 * the single place a session lives, and reaching through it would assert the opposite of what the sweep
 * is for.
 */
const COLLABORATOR_MEMBERS: readonly string[] = Object.freeze(['auth', 'tokenStorage']);

/**
 * Every value the store makes readable, by member name. The mechanism, stated plainly because it is
 * unusual: a signal is a zero-argument function held as an own member of the instance, so enumerating the
 * instance's own members and invoking each zero-argument function-valued one reads EVERY slice the store
 * carries — including the ones it declares private, since that keyword vanishes at run time.
 *
 * @param store The store to read.
 * @returns Each readable member name paired with the value it yielded.
 */
function readableMembers(store: AuthStore): readonly (readonly [string, unknown])[] {
  const instance = store as unknown as Record<string, unknown>;
  const readings: (readonly [string, unknown])[] = [];

  for (const member of Object.keys(instance)) {
    if (COLLABORATOR_MEMBERS.includes(member)) {
      continue;
    }

    const held: unknown = instance[member];

    if (typeof held === 'function' && held.length === 0) {
      const read = held as () => unknown;

      readings.push([member, read()] as const);

      continue;
    }

    readings.push([member, held] as const);
  }

  return readings;
}

/**
 * The member names whose readable value contains a secret. Returned as names rather than as a boolean so
 * a failure reports WHICH member leaked, which is the difference between a diagnosis and a puzzle.
 *
 * @param store The store to read.
 * @param secret The fixture value that must not appear.
 * @returns The offending member names, empty when nothing leaked.
 */
function membersLeaking(store: AuthStore, secret: string): readonly string[] {
  return readableMembers(store)
    .filter(([, value]) => containsSecret(value, secret))
    .map(([member]) => member);
}

describe('AuthStore', () => {
  let store: AuthStore;
  let httpMock: HttpTestingController;
  let tokenStorage: TokenStorageService;
  let auth: AuthService;
  let sessionTeardown: SessionTeardownService;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport is registered first and the mock backend second,
        // because the mock REPLACES the backend the first provider installed.
        provideHttpClient(),
        provideHttpClientTesting(),
        AuthStore,
      ],
    });

    store = TestBed.inject(AuthStore);
    httpMock = TestBed.inject(HttpTestingController);
    tokenStorage = TestBed.inject(TokenStorageService);
    auth = TestBed.inject(AuthService);
    sessionTeardown = TestBed.inject(SessionTeardownService);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    // ⚠ MANDATORY. Without it an unflushed request, or one nobody expected, passes in silence — and a
    // specification that proves the store issued NO request is worth nothing unless something checks that
    // claim at the end.
    httpMock.verify();
  });

  // SHARED SEQUENCES
  // A sign-in is TWO requests: the credential exchange, then an identity read carrying the freshly issued
  // credential as a header. Both must be answered or the verification above fails, so every path that signs
  // in goes through one of these.

  /**
   * Answers the identity read that follows a credential exchange.
   *
   * @param expectedToken The credential the read is required to present.
   * @param user The identity to answer with.
   */
  function answerIdentityRead(expectedToken: string, user: CurrentUser = currentUser()): void {
    const request = httpMock.expectOne(ME_URL);

    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('Authorization'))
      .withContext('the identity read presents the credential just issued')
      .toBe(`Bearer ${expectedToken}`);

    request.flush(identityPayload(user));
  }

  /**
   * Signs in successfully.
   *
   * @param payload The credential payload to answer the exchange with.
   * @param request The credentials to submit.
   * @returns The identity the store settled on.
   */
  async function signIn(
    payload: SuccessEnvelope<LoginResponse> = credentialPayload(),
    request: LoginRequest = credentials(),
  ): Promise<CurrentUser> {
    const inFlight = firstValueFrom(store.login(request));

    httpMock.expectOne(LOGIN_URL).flush(payload);
    answerIdentityRead(payload.data.accessToken, payload.data.user);

    return inFlight;
  }

  /**
   * Attempts a sign-in that the server refuses.
   *
   * @param body The document to answer with, or null for a response carrying no body.
   * @param status The transport status.
   * @param statusText The transport status text.
   * @param request The credentials to submit.
   */
  async function refuseSignIn(
    body: ProblemDetails | null,
    status: number,
    statusText: string,
    request: LoginRequest = credentials(),
  ): Promise<void> {
    const inFlight = firstValueFrom(store.login(request));

    httpMock.expectOne(LOGIN_URL).flush(body, { status, statusText });

    await expectAsync(inFlight).toBeRejected();
  }

  /**
   * Resolves to the rejection reason, or null when the promise resolved instead.
   *
   * @param pending The in-flight command.
   * @returns The rejection reason, or null.
   */
  async function rejectionOf(pending: Promise<unknown>): Promise<unknown> {
    return pending.then(
      () => null,
      (reason: unknown) => reason,
    );
  }

  /** Asserts that no request reached any of the four credential paths. */
  function expectNoCredentialTraffic(): void {
    httpMock.expectNone(LOGIN_URL);
    httpMock.expectNone(REFRESH_URL);
    httpMock.expectNone(LOGOUT_URL);
    httpMock.expectNone(ME_URL);
  }

  describe('harness and initial state', () => {
    it('resolves the store, the mock transport and both collaborators from one injector', () => {
      expect(store).toBeInstanceOf(AuthStore);
      expect(tokenStorage).toBeInstanceOf(TokenStorageService);
      expect(auth).toBeInstanceOf(AuthService);
      expect(httpMock).toBeTruthy();
    });

    it('starts idle, with nobody signed in, no failure recorded and the ladder unstarted', () => {
      expect(store.phase()).toBe('idle');
      expect(store.isBusy()).toBe(false);
      expect(store.isAuthenticating()).toBe(false);
      expect(store.isRefreshing()).toBe(false);
      expect(store.isSigningOut()).toBe(false);
      expect(store.isLoadingIdentity()).toBe(false);

      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.portalId()).toBeNull();
      expect(store.accessTokenExpiresAt()).toBeNull();
      expect(store.isSuperUser()).toBe(false);

      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.failureCode()).toBeNull();
      expect(store.validationErrors()).toBeNull();
      expect(store.rateLimited()).toBe(false);

      expect(store.verificationRequired()).toBe(false);
      expect(store.verificationPrompt()).toBeNull();
      expect(store.hasAdvisory()).toBe(false);
    });

    it('reports no severity while nothing has failed', () => {
      expect(store.severity()).toBeNull();
      expect(store.supportReference()).toBeNull();
    });

    it('changes no state and issues no request for a command that is never subscribed', () => {
      store.login(credentials());
      store.refreshSession();
      store.logout();
      store.loadCurrentUser();

      expect(store.phase()).toBe('idle');
      expect(store.isBusy()).toBe(false);
      expectNoCredentialTraffic();
    });

    it('publishes the phase vocabulary as a frozen list', () => {
      expect(Object.isFrozen(AUTH_STORE_PHASES)).toBe(true);
      expect(AUTH_STORE_PHASES.length).toBe(5);
      expect(AUTH_STORE_PHASES.join(','))
        .withContext('the closed set of operations this store can be part-way through')
        .toBe('idle,authenticating,refreshing,signingOut,loadingIdentity');
    });
  });

  describe('login', () => {
    it('posts the credentials to the relative versioned path, with no absolute origin', async () => {
      const inFlight = firstValueFrom(store.login(credentials()));

      const request = httpMock.expectOne(LOGIN_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.url)
        .withContext('relative, because the proxy serving the bundle forwards this prefix on the same origin')
        .toBe(LOGIN_URL);

      request.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });

    it('sends exactly the two declared members when no verification code is supplied', async () => {
      const inFlight = firstValueFrom(store.login(credentials()));

      const request = httpMock.expectOne(LOGIN_URL);
      const body: unknown = request.request.body;

      expect(bodyMemberNames(body)).toEqual(['password', 'username']);

      request.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });

    it('sends exactly the three declared members when a verification code is supplied', async () => {
      const inFlight = firstValueFrom(
        store.login(credentials({ verificationCode: SUBMITTED_VERIFICATION_CODE })),
      );

      const request = httpMock.expectOne(LOGIN_URL);
      const body: unknown = request.request.body;

      expect(bodyMemberNames(body)).toEqual(['password', 'username', 'verificationCode']);

      request.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });

    it('sends no human-verification member and no authentication-type discriminator', async () => {
      // The legacy call took EIGHT arguments, of which a literal authentication-type discriminator and a
      // challenge-adjacent argument both disappear with the single bearer-credential path. A discriminator
      // that can hold one value is not modelled.
      const inFlight = firstValueFrom(
        store.login(credentials({ verificationCode: SUBMITTED_VERIFICATION_CODE })),
      );

      const request = httpMock.expectOne(LOGIN_URL);
      const names = bodyMemberNames(request.request.body);

      expect(names.length)
        .withContext('the contract declares three members and the wire carries no more')
        .toBeLessThanOrEqual(3);
      expect(names).not.toContain('captcha');
      expect(names).not.toContain('authType');
      expect(names).not.toContain('portalName');
      expect(names).not.toContain('ipAddress');

      request.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });

    it('hands the request to the authentication service unaltered rather than building its own', async () => {
      // Proves the division of labour: the store sequences, the service transports. If the store enriched
      // the request - an account name for a log line, a tenant hint the argument seen here would differ
      // from the argument passed in.
      const spy = spyOn(auth, 'login').and.callThrough();
      const request = credentials({ verificationCode: SUBMITTED_VERIFICATION_CODE });

      const inFlight = firstValueFrom(store.login(request));

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();

      expect(spy).toHaveBeenCalledTimes(1);
      expect(spy.calls.mostRecent().args[0])
        .withContext('the credential body is handed through by identity, unaltered')
        .toBe(request);
      expect(spy.calls.mostRecent().args[1])
        .withContext('and no tenant selector is invented when the caller supplied none')
        .toBeUndefined();
    });

    it('reads the identity through a second request instead of trusting the credential payload', async () => {
      // The identity is fetched with the credential just issued rather than taken from the exchange body,
      // so the entitlements the store publishes are the ones the server will actually enforce.
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });

    it('holds the identity and reports a session once both requests answer', async () => {
      const expected = currentUser();

      const settled = await signIn();

      expect(settled).toEqual(expected);
      expect(store.currentUser()).toEqual(expected);
      expect(store.isAuthenticated()).toBe(true);
      expect(store.roles()).toEqual(['Administrators']);
      expect(store.permissions()).toEqual(['VIEW']);
      expect(store.phase()).toBe('idle');
      expect(store.hasFailure()).toBe(false);
    });

    it('reports itself authenticating only while the exchange is in flight', async () => {
      const inFlight = firstValueFrom(store.login(credentials()));

      expect(store.phase()).toBe('authenticating');
      expect(store.isAuthenticating()).toBe(true);
      expect(store.isBusy()).toBe(true);
      expect(store.isRefreshing()).toBe(false);
      expect(store.isSigningOut()).toBe(false);

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();

      expect(store.phase()).toBe('idle');
      expect(store.isAuthenticating()).toBe(false);
      expect(store.isBusy()).toBe(false);
    });

    it('returns to idle and records the refusal when the credentials are rejected', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.phase()).toBe('idle');
      expect(store.isBusy()).toBe(false);
      expect(store.hasFailure()).toBe(true);
      expect(store.failureStatus()).toBe(401);
      expect(store.failureCode()).toBe(INVALID_CREDENTIALS_CODE);
      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
    });

    it('reads no identity when the exchange is refused', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      // Nothing was issued, so there is nothing to present. The verification in
      // afterEach is what makes this assertion binding.
      httpMock.expectNone(ME_URL);
    });

    it('never publishes the submitted password, on success or on refusal', async () => {
      await signIn();

      expect(membersLeaking(store, FAKE_PASSWORD))
        .withContext('the credential travels once, inside the request, and is compared against a hash')
        .toEqual([]);

      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(membersLeaking(store, FAKE_PASSWORD)).toEqual([]);
    });

    it('clears a previously recorded failure when a fresh attempt begins', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.hasFailure()).toBe(true);

      const inFlight = firstValueFrom(store.login(credentials()));

      expect(store.hasFailure())
        .withContext('a new attempt is not presented alongside the last one that failed')
        .toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(inFlight).toBeResolved();
    });
  });

  // THE VERIFICATION LADDER
  describe('verification ladder', () => {
    it('reveals the field on the first refusal that asks for a verification code', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationRequired())
        .withContext('the legacy reveal at L173 and L174, now a signal')
        .toBe(true);
      expect(store.verificationPrompt()).not.toBeNull();
      expect(store.verificationPrompt()?.code).toBe(VERIFICATION_REQUIRED_CODE);
      expect(store.verificationPrompt()?.message).toBe(VERIFICATION_REQUIRED_MESSAGE);
      expect(store.verificationPrompt()?.revealVerification)
        .withContext('reported only on the turn that first reveals the field')
        .toBe(true);
      expect(store.verificationPrompt()?.severity).toBe('warning');
    });

    it('reports the invalid-code outcome only once the field is already on screen', async () => {
      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials({ verificationCode: SUBMITTED_VERIFICATION_CODE }),
      );

      expect(store.verificationRequired()).toBe(true);
      expect(store.verificationPrompt()?.code)
        .withContext('the reveal rung precedes the judgement rung')
        .toBe(VERIFICATION_REQUIRED_CODE);
      expect(store.verificationPrompt()?.revealVerification).toBe(true);
    });

    it('judges a wrong code once the field has been revealed, and keeps the field revealed', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationRequired()).toBe(true);

      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials({ verificationCode: SUBMITTED_VERIFICATION_CODE }),
      );

      expect(store.verificationRequired())
        .withContext('a failed attempt never retracts a field the person is being asked to fill in')
        .toBe(true);
      expect(store.verificationPrompt()?.code).toBe(VERIFICATION_CODE_INVALID_CODE);
      expect(store.verificationPrompt()?.message).toBe(VERIFICATION_CODE_INVALID_MESSAGE);
      expect(store.verificationPrompt()?.revealVerification)
        .withContext('the field is already on screen, so this turn reveals nothing')
        .toBe(false);
    });

    it('asks again, rather than calling the code invalid, when the revealed field was left empty', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials({ verificationCode: '' }),
      );

      expect(store.verificationPrompt()?.code)
        .withContext('the legacy `<> ""` comparison at L177, untrimmed and unrewritten')
        .toBe(VERIFICATION_REQUIRED_CODE);
      expect(store.verificationRequired()).toBe(true);
    });

    it('treats an omitted code and an empty code identically', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials(),
      );

      const omitted = store.verificationPrompt()?.code;

      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials({ verificationCode: '' }),
      );

      expect(omitted).toBe(VERIFICATION_REQUIRED_CODE);
      expect(store.verificationPrompt()?.code)
        .withContext('neither spelling is rewritten into the other')
        .toBe(omitted);
    });

    it('also treats an explicitly null code as none supplied', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      await refuseSignIn(
        refusal(VERIFICATION_CODE_INVALID_CODE, 401),
        401,
        'Unauthorized',
        credentials({ verificationCode: null }),
      );

      expect(store.verificationPrompt()?.code).toBe(VERIFICATION_REQUIRED_CODE);
    });

    it('never reveals the field when the tenant does not verify registrations', async () => {
      // The legacy gate at L170 read the tenant's registration mode from ambient per-request page state and
      // produced `"UserNotAuthorized"` at L184 when it was anything else.
      await refuseSignIn(refusal(ACCOUNT_NOT_APPROVED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationRequired())
        .withContext('this rung is reached only when verification is not configured, so no field exists to reveal')
        .toBe(false);
      expect(store.verificationPrompt()?.code).toBe(ACCOUNT_NOT_APPROVED_CODE);
      expect(store.verificationPrompt()?.message).toBe(ACCOUNT_NOT_APPROVED_MESSAGE);
      expect(store.verificationPrompt()?.revealVerification).toBe(false);
      expect(store.verificationPrompt()?.severity).toBe('warning');
    });

    it('leaves the field hidden for every failure outside the closed verification vocabulary', async () => {
      // ⚠ THE NEGATIVE HALF OF THE PROOF, WITHOUT WHICH THE POSITIVE HALF SHOWS NOTHING. A signal that
      // flipped for an ordinary refused credential would put a verification field in front of someone who
      // simply mistyped a password, and a specification that only ever fed it the code that SHOULD flip it
      // could not tell the difference.
      const entries: readonly (readonly [string, ProblemDetails | null, number, string])[] = [
        ['a refused credential', refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized'],
        ['a refusal carrying no document at all', null, 401, 'Unauthorized'],
        ['a refusal carrying a document with no code', codelessRefusal(401), 401, 'Unauthorized'],
        ['a locked-out account', refusal(ACCOUNT_LOCKED_OUT_CODE, 403), 403, 'Forbidden'],
        ['a refusal to authorise', codelessRefusal(403), 403, 'Forbidden'],
        ['a rate-limiter rejection', codelessRefusal(429), 429, 'Too Many Requests'],
        ['a server fault', codelessRefusal(500), 500, 'Internal Server Error'],
        [
          'the legacy message literal, which is no longer a code',
          refusal(LEGACY_ENTER_CODE_LITERAL, 401),
          401,
          'Unauthorized',
        ],
      ];

      for (const [description, body, status, statusText] of entries) {
        await refuseSignIn(body, status, statusText);

        expect(store.verificationRequired())
          .withContext(`${description} must not reveal the verification field`)
          .toBe(false);
        expect(store.verificationPrompt())
          .withContext(`${description} must not advance the ladder`)
          .toBeNull();
      }
    });

    it('returns the ladder to its first rung on a successful sign-in, and only then', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationRequired()).toBe(true);

      await signIn(credentialPayload(), credentials({ verificationCode: 'accepted-code' }));

      expect(store.verificationRequired()).toBe(false);
      expect(store.verificationPrompt()).toBeNull();
    });

    it('does not retract a revealed field when a consumer dismisses the message', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      store.clearError();

      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.verificationRequired())
        .withContext('dismissing a message must not take away the field it asked to be filled in')
        .toBe(true);
      expect(store.verificationPrompt()?.code).toBe(VERIFICATION_REQUIRED_CODE);
    });

    it('returns the ladder to its first rung on an explicit reset', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      store.reset();

      expect(store.verificationRequired()).toBe(false);
      expect(store.verificationPrompt()).toBeNull();
      expect(store.hasFailure()).toBe(false);
      expect(store.isAuthenticated()).toBe(false);
      expectNoCredentialTraffic();
    });

    it('presents every rung of the ladder as a warning rather than a fault', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationPrompt()?.severity).toBe('warning');
      expect(store.severity()).toBe('warning');

      await refuseSignIn(refusal(ACCOUNT_NOT_APPROVED_CODE, 403), 403, 'Forbidden');

      expect(store.verificationPrompt()?.severity).toBe('warning');
      expect(store.severity()).toBe('warning');
    });
  });

  // THE CORRECTED SIGN-IN OUTCOME MAPPING
  // The seven outcomes are mapped deliberately server-side now — ordinal 3 becomes a refusal to authorise,
  // and ordinals 5 and 6 become COMPLETED sign-ins carrying a security advisory.
  describe('sign-in outcome mapping', () => {
    it('refuses a locked-out account instead of admitting it', async () => {
      await refuseSignIn(refusal(ACCOUNT_LOCKED_OUT_CODE, 403), 403, 'Forbidden');

      expect(store.isAuthenticated())
        .withContext('the legacy defect at L187 admitted this outcome; it is refused now')
        .toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.portalId()).toBeNull();
      expect(store.hasFailure()).toBe(true);
      expect(store.failureStatus()).toBe(403);
      expect(store.failureCode()).toBe(ACCOUNT_LOCKED_OUT_CODE);
      expect(store.hasAdvisory())
        .withContext('a refusal is not an advisory; nothing was admitted to advise')
        .toBe(false);
      expect(store.verificationRequired()).toBe(false);
    });

    it('presents a locked-out refusal as a warning rather than a fault', async () => {
      await refuseSignIn(refusal(ACCOUNT_LOCKED_OUT_CODE, 403), 403, 'Forbidden');

      expect(store.severity()).toBe('warning');
    });

    it('completes a sign-in carrying the insecure administrator credential advisory', async () => {
      await signIn(credentialPayload({ mustChangePassword: true }));

      expect(store.isAuthenticated())
        .withContext('an outcome of this kind is a completed sign-in with a warning, never a refusal')
        .toBe(true);
      expect(store.mustChangePassword()).toBe(true);
      expect(store.hasAdvisory()).toBe(true);
      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.severity())
        .withContext('nothing failed, so there is nothing to present at any forcefulness')
        .toBeNull();
    });

    it('completes a sign-in carrying the insecure host credential advisory', async () => {
      await signIn(
        credentialPayload({
          mustChangePassword: true,
          user: currentUser({ isSuperUser: true }),
        }),
      );

      expect(store.isAuthenticated()).toBe(true);
      expect(store.isSuperUser())
        .withContext('a host account widens which tenants are reachable, not what is permitted within one')
        .toBe(true);
      expect(store.mustChangePassword()).toBe(true);
      expect(store.hasFailure()).toBe(false);
    });

    it('carries more than one advisory at once, which the single-valued legacy outcome could not', async () => {
      // The legacy post-credential check reported exactly one of five states, with precedence deciding
      // which.
      await signIn(credentialPayload({ mustChangePassword: true, passwordExpiring: true }));

      expect(store.mustChangePassword()).toBe(true);
      expect(store.passwordExpiring()).toBe(true);
      expect(store.mustUpdateProfile()).toBe(false);
      expect(store.hasAdvisory()).toBe(true);
      expect(store.isAuthenticated()).toBe(true);
    });

    it('carries all three advisories at once', async () => {
      await signIn(
        credentialPayload({
          mustChangePassword: true,
          passwordExpiring: true,
          mustUpdateProfile: true,
        }),
      );

      expect(store.mustChangePassword()).toBe(true);
      expect(store.passwordExpiring()).toBe(true);
      expect(store.mustUpdateProfile()).toBe(true);
      expect(store.hasAdvisory()).toBe(true);
    });

    it('reports the session RESTRICTED for either blocking advisory', async () => {
      await signIn(credentialPayload({ mustChangePassword: true }));
      expect(store.sessionRestricted()).toBe(true);

      TestBed.inject(TokenStorageService).clear();
      await signIn(credentialPayload({ mustUpdateProfile: true }));
      expect(store.sessionRestricted()).toBe(true);
    });

    it('does NOT report the session restricted for the informational advisory alone', async () => {
      await signIn(credentialPayload({ passwordExpiring: true }));

      expect(store.hasAdvisory())
        .withContext('there IS an advisory, and that is a different question')
        .toBe(true);
      expect(store.sessionRestricted()).toBe(false);
    });

    it('reports no restriction when nobody is signed in', async () => {
      expect(store.sessionRestricted())
        .withContext('an unauthenticated caller is stopped by authentication, not by an advisory')
        .toBe(false);

      await signIn(credentialPayload());

      expect(store.sessionRestricted()).toBe(false);
    });

    it('clears a satisfied CREDENTIAL advisory without issuing a request', async () => {
      // ⚠ WHY THIS IS NOT A RENEWAL. `POST api/v1/users/{id}/password` deliberately revokes every refresh
      // token the account holds, so the token this client is holding is dead the instant the change
      // succeeds.
      await signIn(credentialPayload({ mustChangePassword: true, mustUpdateProfile: true }));

      store.noteCredentialRemediated();

      httpMock.expectNone(REFRESH_URL);
      expect(store.mustChangePassword()).toBe(false);
      expect(store.mustUpdateProfile())
        .withContext('only the SATISFIED advisory is cleared, so the root can move the caller on')
        .toBe(true);
      expect(store.sessionRestricted())
        .withContext('and the session is still restricted by the one that remains')
        .toBe(true);
      expect(store.isAuthenticated())
        .withContext('the session survives: the access token was never revoked, only the renewal')
        .toBe(true);
    });

    it('clears a satisfied PROFILE advisory without issuing a request', async () => {
      await signIn(credentialPayload({ mustUpdateProfile: true }));

      store.noteProfileRemediated();

      httpMock.expectNone(REFRESH_URL);
      expect(store.mustUpdateProfile()).toBe(false);
      expect(store.sessionRestricted()).toBe(false);
      expect(store.isAuthenticated()).toBe(true);
    });

    it('leaves the identity and the credentials untouched when clearing an advisory', async () => {
      // The advisory is the only member replaced. Anything else changing here would make this a
      // session transition in disguise, and a caller mid-journey would lose their identity.
      await signIn(credentialPayload({ mustChangePassword: true }));

      const before = tokenStorage.session();

      store.noteCredentialRemediated();

      const after = tokenStorage.session();

      expect(after?.accessToken).toBe(before?.accessToken);
      expect(after?.refreshToken).toBe(before?.refreshToken);
      expect(after?.expiresAtUtc).toBe(before?.expiresAtUtc);
      expect(after?.user).toEqual(before?.user);
      expect(after?.passwordExpiring).toBe(before?.passwordExpiring);
      expect(store.currentUser()).not.toBeNull();
    });

    it('does nothing at all when the advisory was never outstanding, or nobody is signed in', () => {
      // Idempotent and safe to call unconditionally, so a screen does not have to test first.
      store.noteCredentialRemediated();
      store.noteProfileRemediated();

      httpMock.expectNone(REFRESH_URL);
      expect(tokenStorage.session())
        .withContext('no session is manufactured for a caller who has none')
        .toBeNull();
    });

    it('retains a false advisory as data rather than reading it as absent', async () => {
      await signIn(credentialPayload());

      const session = tokenStorage.session();

      expect(session).not.toBeNull();
      expect(typeof session?.mustChangePassword)
        .withContext('present and boolean, not absent')
        .toBe('boolean');
      expect(typeof session?.passwordExpiring).toBe('boolean');
      expect(typeof session?.mustUpdateProfile).toBe('boolean');

      expect(store.mustChangePassword()).toBe(false);
      expect(store.passwordExpiring()).toBe(false);
      expect(store.mustUpdateProfile()).toBe(false);
      expect(store.hasAdvisory()).toBe(false);
      expect(store.isAuthenticated())
        .withContext('a session is held, so a false advisory is distinguishable from having no session')
        .toBe(true);
    });

    it('reports every advisory as false while nobody is signed in', async () => {
      // The same answer as "no advisory", which is the correct one for a gate: an unauthenticated caller is
      // stopped by the authentication check, not by an advisory.
      expect(store.mustChangePassword()).toBe(false);
      expect(store.passwordExpiring()).toBe(false);
      expect(store.mustUpdateProfile()).toBe(false);
      expect(store.hasAdvisory()).toBe(false);

      await signIn(credentialPayload({ mustChangePassword: true }));

      expect(store.hasAdvisory()).toBe(true);

      store.reset();

      expect(store.hasAdvisory())
        .withContext('an advisory cannot outlive the session it described')
        .toBe(false);
    });

    it('keys the outcome on the document type and on no other member', async () => {
      // The code travels on ONE member. A consumer that read a code out of the wording
      // would key on prose, and prose is exactly what a migration is allowed to change.
      await refuseSignIn(
        codelessRefusal(401, {
          title: VERIFICATION_REQUIRED_CODE,
          detail: VERIFICATION_REQUIRED_CODE,
        }),
        401,
        'Unauthorized',
      );

      expect(store.failureCode()).toBeNull();
      expect(store.verificationRequired()).toBe(false);
      expect(store.verificationPrompt()).toBeNull();
    });

    it('reads no code from a document whose type omits the required prefix', async () => {
      await refuseSignIn(
        codelessRefusal(401, { type: VERIFICATION_REQUIRED_CODE }),
        401,
        'Unauthorized',
      );

      expect(store.failureCode()).toBeNull();
      expect(store.verificationRequired()).toBe(false);
    });
  });

  // CUSTODY
  // Custody belongs to `core/services/token-storage.service.ts`, which keeps the session in MEMORY ONLY.
  // That is a stated non-functional requirement rather than a preference, and the store's part in it is
  // negative: it must not become a second copy.
  describe('credential custody', () => {
    it('exposes no readable member holding the access credential', async () => {
      await signIn();

      expect(membersLeaking(store, FAKE_ACCESS_TOKEN))
        .withContext('the sweep reads every own member, including the private slices')
        .toEqual([]);
    });

    it('exposes no readable member holding the renewal credential', async () => {
      await signIn();

      expect(membersLeaking(store, FAKE_RENEWAL_TOKEN)).toEqual([]);
    });

    it('leaves the session with the custodian, where the single copy lives', async () => {
      await signIn();

      expect(tokenStorage.accessToken()).toBe(FAKE_ACCESS_TOKEN);
      expect(tokenStorage.refreshToken()).toBe(FAKE_RENEWAL_TOKEN);
      expect(store.isAuthenticated())
        .withContext('the store reports the presence of a session without holding it')
        .toBe(true);
    });

    it('writes exactly one copy of the session across a whole sign-in', async () => {
      const spy = spyOn(tokenStorage, 'store').and.callThrough();

      await signIn();

      expect(spy).toHaveBeenCalledTimes(1);
    });

    it('publishes the expiry instant exactly as the server stamped it', async () => {
      await signIn();

      expect(store.accessTokenExpiresAt()).toBe(EXPIRES_AT_UTC);
      expect(typeof store.accessTokenExpiresAt()).toBe('string');
    });

    it('reports the presence of a session rather than re-deciding its validity', async () => {
      // Reports what the custodian reports. An access credential that has lapsed still yields true, because
      // the correct response to lapsing is to renew — which requires the session to still be here.
      await signIn();

      expect(store.isAuthenticated()).toBe(tokenStorage.isAuthenticated());
    });

    it('asks the custodian to discard the session on an explicit reset, with no request', () => {
      const spy = spyOn(tokenStorage, 'clear').and.callThrough();

      store.reset();

      expect(spy).toHaveBeenCalled();
      expectNoCredentialTraffic();
    });

    it('publishes nothing about a session once it has been discarded', async () => {
      await signIn();

      expect(store.isAuthenticated()).toBe(true);

      store.reset();

      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.portalId()).toBeNull();
      expect(store.accessTokenExpiresAt()).toBeNull();
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.isSuperUser()).toBe(false);
      expect(membersLeaking(store, FAKE_ACCESS_TOKEN)).toEqual([]);
    });
  });

  // SIGNING OUT
  // The legacy cookie clear has no stateless counterpart: it took effect at once, whereas a signed bearer
  // credential cannot be recalled once issued. Signing out is therefore revocation of the RENEWAL
  // credential plus a local discard.
  describe('logout', () => {
    it('revokes the renewal credential and carries nothing else in the request', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.logout());

      const request = httpMock.expectOne(LOGOUT_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(LOGOUT_URL);
      expect(bodyMemberNames(request.request.body))
        .withContext('the renewal credential is the only thing there is to revoke')
        .toEqual(['refreshToken']);

      request.flush(null, { status: 204, statusText: 'No Content' });

      await expectAsync(inFlight).toBeResolved();

      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.phase()).toBe('idle');
    });

    it('discards the session locally even when the revocation request fails', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.logout());

      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(500), { status: 500, statusText: 'Internal Server Error' });

      await expectAsync(inFlight).toBeResolved();

      expect(store.isAuthenticated())
        .withContext('signing out never leaves a client authenticated')
        .toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.accessTokenExpiresAt()).toBeNull();
      expect(store.phase()).toBe('idle');
      expect(membersLeaking(store, FAKE_ACCESS_TOKEN)).toEqual([]);
    });

    it('discards the session locally when the caller unsubscribes before the revocation answers', async () => {
      await signIn();

      const subscription = store.logout().subscribe();

      expect(store.phase()).toBe('signingOut');
      expect(store.isSigningOut()).toBe(true);

      const revocations = httpMock.match(LOGOUT_URL);

      expect(revocations.length).toBe(1);

      subscription.unsubscribe();

      expect(store.phase())
        .withContext('the local discard covers an early unsubscribe, not just the two answered paths')
        .toBe('idle');
      expect(store.isSigningOut()).toBe(false);
      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
    });

    it('issues no revocation request when there is no session to revoke', async () => {
      const inFlight = firstValueFrom(store.logout());

      httpMock.expectNone(LOGOUT_URL);

      await expectAsync(inFlight).toBeResolved();

      expect(store.isAuthenticated()).toBe(false);
      expect(store.phase()).toBe('idle');
    });

    it('clears a recorded failure as part of signing out', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.hasFailure()).toBe(true);

      const inFlight = firstValueFrom(store.logout());

      await expectAsync(inFlight).toBeResolved();

      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.severity()).toBeNull();
    });

    it('returns the verification ladder to its first rung when signing out', async () => {
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationRequired()).toBe(true);

      const inFlight = firstValueFrom(store.logout());

      await expectAsync(inFlight).toBeResolved();

      expect(store.verificationRequired()).toBe(false);
      expect(store.verificationPrompt()).toBeNull();
    });
  });

  // RENEWAL
  // ⚠ RENEWING AFTER A REFUSED REQUEST IS NOT THIS STORE'S JOB. That policy — deciding WHEN a renewal is
  // warranted, and what a refused one means for the request that provoked it — belongs to
  // `core/interceptors/auth.interceptor.ts`, which the store references by path and never imports.
  describe('renewal', () => {
    it('does not renew the session when an unrelated request is refused', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.loadCurrentUser());

      httpMock
        .expectOne(ME_URL)
        .flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });

      await expectAsync(inFlight).toBeRejected();

      httpMock.expectNone(REFRESH_URL);
      expect(store.hasFailure()).toBe(true);
      expect(store.failureStatus()).toBe(401);
    });

    it('does not renew the session when the sign-in itself is refused', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      httpMock.expectNone(REFRESH_URL);
    });

    it('renews the session only when a caller asks it to', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.refreshSession());

      expect(store.phase()).toBe('refreshing');
      expect(store.isRefreshing()).toBe(true);

      const request = httpMock.expectOne(REFRESH_URL);

      expect(request.request.method).toBe('POST');
      expect(bodyMemberNames(request.request.body))
        .withContext('the renewal contract declares exactly one member')
        .toEqual(['refreshToken']);

      request.flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
          expiresAtUtc: EXPIRES_AT_UTC_ROTATED,
        }),
      );
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      await expectAsync(inFlight).toBeResolved();

      expect(store.phase()).toBe('idle');
      expect(store.isRefreshing()).toBe(false);
      expect(store.isAuthenticated()).toBe(true);
      expect(store.accessTokenExpiresAt())
        .withContext('the rotated instant, again exactly as stamped')
        .toBe(EXPIRES_AT_UTC_ROTATED);
      expect(store.hasFailure()).toBe(false);
    });

    it('publishes no rotated credential of its own after a renewal', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.refreshSession());

      httpMock.expectOne(REFRESH_URL).flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
          expiresAtUtc: EXPIRES_AT_UTC_ROTATED,
        }),
      );
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      await expectAsync(inFlight).toBeResolved();

      expect(membersLeaking(store, FAKE_ACCESS_TOKEN_ROTATED)).toEqual([]);
      expect(membersLeaking(store, FAKE_RENEWAL_TOKEN_ROTATED)).toEqual([]);
    });

    it('discards the session when a renewal is refused, and explains it without quoting the wire', async () => {
      // ⚠ THE RENEWAL'S OWN PROBLEM DOCUMENT IS NOT RECORDED. Recording it - on the reasoning that the
      // sign-in screen needs it in order to explain why the caller is back at it - is what this case
      // refuses.
      await signIn();

      const inFlight = firstValueFrom(store.refreshSession());

      httpMock
        .expectOne(REFRESH_URL)
        .flush(refusal(INVALID_CREDENTIALS_CODE, 401, { correlationId: CORRELATION_ID }), {
          status: 401,
          statusText: 'Unauthorized',
        });

      await expectAsync(inFlight).toBeRejected();

      expect(store.isAuthenticated()).toBe(false);
      expect(store.currentUser()).toBeNull();
      expect(store.phase()).toBe('idle');

      // Nothing is left in the slot the sign-in screen reads as "your sign-in attempt was refused",
      // because this was not a sign-in attempt.
      expect(store.hasFailure())
        .withContext('a refused renewal is not a refused sign-in and must not present as one')
        .toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.supportReference()).toBeNull();

      // And the operator is still told, in one notice, what actually happened to them.
      const queued = notifications.notifications();

      expect(queued.length).toBe(1);
      expect(queued[0]?.message).toBe(SESSION_ENDED_MESSAGE);
      expect(queued[0]?.severity)
        .withContext('nothing failed on the operator\'s part, so it is a warning and not an error')
        .toBe('warning');
      expect(queued[0]?.message)
        .withContext('and it names no token, no status code and no correlation identifier')
        .not.toContain('token');
    });

    it('reports a renewal that had nothing to renew from, and reports it as an ending', async () => {
      // ⚠ THAT PROOF MOVED RATHER THAN DISAPPEARING, and it moved because it was in the wrong place. The
      // invariant belongs to a REFUSED SIGN-IN, where the recorded failure is what the sign-in screen
      // shows; it is covered there.
      const inFlight = firstValueFrom(store.refreshSession());

      await expectAsync(inFlight).toBeRejected();

      httpMock.expectNone(REFRESH_URL);
      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.failureCode()).toBeNull();
      expect(store.phase()).toBe('idle');

      // Silence is still not an option: a caller asked to renew and the session is over.
      expect(notifications.notifications().map((entry) => entry.message)).toEqual([
        SESSION_ENDED_MESSAGE,
      ]);
    });
  });

  // -------------------------------------------------------------------------
  // THE CALLER'S OWN IDENTITY
  // -------------------------------------------------------------------------
  describe('current user', () => {
    it('reads the identity from the relative versioned path', async () => {
      const inFlight = firstValueFrom(store.loadCurrentUser());

      expect(store.phase()).toBe('loadingIdentity');
      expect(store.isLoadingIdentity()).toBe(true);

      const request = httpMock.expectOne(ME_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(ME_URL);

      request.flush(identityPayload());

      await expectAsync(inFlight).toBeResolved();

      expect(store.currentUser()).toEqual(currentUser());
      expect(store.phase()).toBe('idle');
      expect(store.isLoadingIdentity()).toBe(false);
    });

    it('lets a freshly read identity take precedence over the snapshot taken at issue time', async () => {
      await signIn();

      expect(store.roles()).toEqual(['Administrators']);

      const widened = currentUser({ roles: ['Administrators', 'Editors'], permissions: ['VIEW', 'EDIT'] });
      const inFlight = firstValueFrom(store.loadCurrentUser());

      httpMock.expectOne(ME_URL).flush(identityPayload(widened));

      await expectAsync(inFlight).toBeResolved();

      expect(store.roles()).toEqual(['Administrators', 'Editors']);
      expect(store.permissions()).toEqual(['VIEW', 'EDIT']);
      expect(tokenStorage.currentUser()?.roles)
        .withContext('the snapshot is untouched; only the projection moved on')
        .toEqual(['Administrators']);
    });

    it('exposes no member that decides an authorisation question', () => {
      // ⚠ THE ROLE AND PERMISSION LISTS DECIDE NOTHING. They exist so a screen can avoid offering an action
      // the server would refuse. THE SERVER IS AUTHORITATIVE: it re-authorises every request against stored
      // state and answers with a refusal to authorise, and nothing here may stand in for that.
      expect('hasPermission' in store).toBe(false);
      expect('isInRole' in store).toBe(false);
      expect('can' in store).toBe(false);
      expect('authorize' in store).toBe(false);
      expect('isAuthorized' in store).toBe(false);
    });

    it('exposes no credential-recovery command', () => {
      // Retrieval is ABOLISHED rather than ported. The legacy provider was registered with retrieval
      // enabled and reversible storage, against a key committed to source control, so every stored
      // credential was recoverable by anyone with repository access.
      expect('retrievePassword' in store).toBe(false);
      expect('getPassword' in store).toBe(false);
      expect('recoverPassword' in store).toBe(false);
      expect('sendPassword' in store).toBe(false);
    });

    it('reports empty, stable role and permission lists while nobody is signed in', () => {
      // A stable reference rather than a fresh list per read, so a consumer using the
      // on-push change-detection strategy does not see a change on every evaluation.
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.roles()).toBe(store.roles());
      expect(store.permissions()).toBe(store.permissions());
    });
  });

  // FAILURE REPORTING
  describe('failure reporting', () => {
    it('retains the framework trace identifier a report has to quote', async () => {
      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, { traceId: TRACE_ID }),
        401,
        'Unauthorized',
      );

      expect(store.problem()?.traceId).toBe(TRACE_ID);
      expect(store.supportReference()).toBe(TRACE_ID);
    });

    it('prefers the correlation identifier the server validated over the framework one', async () => {
      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, {
          correlationId: CORRELATION_ID,
          traceId: TRACE_ID,
        }),
        401,
        'Unauthorized',
      );

      expect(store.supportReference()).toBe(CORRELATION_ID);
      expect(store.problem()?.correlationId).toBe(CORRELATION_ID);
      expect(store.problem()?.traceId)
        .withContext('the framework identifier is kept as well, not displaced')
        .toBe(TRACE_ID);
    });

    it('reports no support reference for a failure that carried no identifier', async () => {
      await refuseSignIn(codelessRefusal(500), 500, 'Internal Server Error');

      expect(store.supportReference()).toBeNull();
    });

    it('presents a refusal to authorise as a warning rather than a fault', async () => {
      await refuseSignIn(codelessRefusal(403), 403, 'Forbidden');

      expect(store.severity()).toBe('warning');
      expect(store.severity()).not.toBe('error');
    });

    it('presents an unauthenticated refusal as a warning', async () => {
      await refuseSignIn(codelessRefusal(401), 401, 'Unauthorized');

      expect(store.severity()).toBe('warning');
    });

    it('presents a server fault as an error', async () => {
      await refuseSignIn(codelessRefusal(500), 500, 'Internal Server Error');

      expect(store.severity()).toBe('error');
    });

    it('reports the rate-limiter rejection as its own calm state rather than a fault', async () => {
      // The compensating control for the dropped human-verification challenge is a request rate limiter on
      // the credential paths, partitioned by calling address.
      await refuseSignIn(codelessRefusal(429), 429, 'Too Many Requests');

      expect(store.rateLimited()).toBe(true);
      expect(store.failureStatus()).toBe(429);
      expect(store.severity()).toBe('info');
      expect(store.hasFailure()).toBe(true);
      expect(store.verificationRequired())
        .withContext('being early is not being unverified')
        .toBe(false);
    });

    it('reports no rate-limiter rejection for any other refusal', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.rateLimited()).toBe(false);
    });

    it('reads per-field failures with bracket access, which the index signature requires', async () => {
      // ⚠ BRACKET ACCESS, ALWAYS. The member is an index signature and this workspace enables the compiler
      // option that makes dot access on one an error, deliberately: a key is only ever known at run time,
      // and dot access would let a typo compile as a silent absent value.
      await refuseSignIn(fieldRefusal(), 422, 'Unprocessable Content');

      const errors = store.validationErrors();

      expect(errors).not.toBeNull();
      expect(errors?.[MODEL_STATE_KEY])
        .withContext('the model-member key from the server, reproduced byte for byte and not lower-camel-cased')
        .toEqual(['The account name is required.']);
      expect(Object.keys(errors === null ? {} : errors)).toEqual([MODEL_STATE_KEY]);
    });

    it('reports no per-field failures for a refusal that is not a validation failure', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.validationErrors()).toBeNull();
    });

    it('holds message text as an inert plain string, laundering nothing', async () => {
      // Unescaping the 37 in-scope legacy resource files first — a naive search finds nothing — shows 76
      // values carrying markup, four of them carrying a script element, including a live one in the
      // site-settings resource file.
      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, { detail: SCRIPT_BEARING_DETAIL }),
        401,
        'Unauthorized',
      );

      expect(store.problem()?.detail).toBe(SCRIPT_BEARING_DETAIL);
      expect(typeof store.problem()?.detail)
        .withContext('a plain string, not a trusted-markup wrapper')
        .toBe('string');
    });

    it('keeps the legacy leading break tag in both of its spellings rather than pre-stripping it', async () => {
      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, { detail: UNCLOSED_BREAK_DETAIL }),
        401,
        'Unauthorized',
      );

      expect(store.problem()?.detail).toBe(UNCLOSED_BREAK_DETAIL);

      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, { detail: CLOSED_BREAK_DETAIL }),
        401,
        'Unauthorized',
      );

      expect(store.problem()?.detail).toBe(CLOSED_BREAK_DETAIL);
    });

    it('discloses only a code, never which credential was wrong', async () => {
      // ⚠ A SECURITY PROPERTY, PRESERVED DELIBERATELY. The legacy flow drew no distinction between an
      // unknown account and an incorrect credential, and neither does this.
      const body = refusal(INVALID_CREDENTIALS_CODE, 401);

      await refuseSignIn(body, 401, 'Unauthorized', credentials({ username: ACCOUNT_NAME }));

      const first = {
        code: store.failureCode(),
        status: store.failureStatus(),
        severity: store.severity(),
        problem: store.problem(),
        fields: store.validationErrors(),
      };

      expect(membersLeaking(store, ACCOUNT_NAME)).toEqual([]);

      await refuseSignIn(body, 401, 'Unauthorized', credentials({ username: OTHER_ACCOUNT_NAME }));

      expect({
        code: store.failureCode(),
        status: store.failureStatus(),
        severity: store.severity(),
        problem: store.problem(),
        fields: store.validationErrors(),
      })
        .withContext('an unknown account and a wrong credential are indistinguishable, by design')
        .toEqual(first);
      expect(membersLeaking(store, OTHER_ACCOUNT_NAME)).toEqual([]);
      expect(membersLeaking(store, FAKE_PASSWORD)).toEqual([]);
    });

    it('clears a recorded failure without disturbing the session', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.loadCurrentUser());

      httpMock
        .expectOne(ME_URL)
        .flush(codelessRefusal(500), { status: 500, statusText: 'Internal Server Error' });

      await expectAsync(inFlight).toBeRejected();

      expect(store.hasFailure()).toBe(true);

      store.clearError();

      expect(store.hasFailure()).toBe(false);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.severity()).toBeNull();
      expect(store.supportReference()).toBeNull();
      expect(store.isAuthenticated())
        .withContext('dismissing a message is not signing out')
        .toBe(true);
      expect(store.currentUser()).not.toBeNull();
    });
  });

  // SENTINEL FIDELITY
  // The collision that makes this section necessary: the tenant key column is seeded at minus one, so MINUS
  // ONE IS SIMULTANEOUSLY A REAL TENANT AND THE LEGACY MARKER FOR "no integer", and zero is a real tenant
  // too. Absence is therefore expressed as null and never as zero or minus one.
  describe('sentinel fidelity', () => {
    it('retains a tenant key of zero rather than reading it as absent', async () => {
      await signIn(credentialPayload({ user: currentUser({ portalId: 0 }) }));

      expect(store.portalId())
        .withContext('zero is a real tenant, inserted explicitly, and is passed through untouched')
        .toBe(0);
      expect(store.portalId()).not.toBeNull();
      expect(store.currentUser()?.portalId).toBe(0);
    });

    it('retains a tenant key of minus one, which is both a real tenant and the legacy absent marker', async () => {
      await signIn(credentialPayload({ user: currentUser({ portalId: -1 }) }));

      expect(store.portalId())
        .withContext('the seed of the tenant key column, and simultaneously the legacy marker for no integer')
        .toBe(-1);
      expect(store.portalId()).not.toBeNull();
      expect(store.currentUser()?.portalId).toBe(-1);
    });

    it('retains an account key of zero', async () => {
      // Defensive symmetry: the role, page and module key columns are all seeded at zero,
      // so a zero key is ordinary rather than exceptional across this schema.
      await signIn(credentialPayload({ user: currentUser({ userId: 0 }) }));

      expect(store.currentUser()?.userId).toBe(0);
    });

    it('distinguishes having no identity from holding an identity whose tenant key is zero', async () => {
      // THE WHOLE POINT OF THE NULL CONVENTION, in one specification. Before signing in there is no tenant
      // to report and the answer is null; after signing in the answer is the number the server sent, even
      // when that number is zero.
      expect(store.portalId()).toBeNull();
      expect(store.currentUser()).toBeNull();

      await signIn(credentialPayload({ user: currentUser({ portalId: 0 }) }));

      expect(store.portalId()).toBe(0);

      store.reset();

      expect(store.portalId())
        .withContext('absence is null again, never zero')
        .toBeNull();
    });

    it('retains an empty string rather than normalising it away', async () => {
      await signIn(
        credentialPayload({
          user: currentUser({ displayName: '', portalName: '', email: '' }),
        }),
      );

      expect(store.currentUser()?.displayName).toBe('');
      expect(store.currentUser()?.portalName).toBe('');
      expect(store.currentUser()?.email).toBe('');
      expect(typeof store.currentUser()?.displayName)
        .withContext('present and empty, which is not the same as absent')
        .toBe('string');
    });

    it('retains empty role and permission lists sent by the server', async () => {
      // An identity with no entitlement is a real identity. Reporting it the same way as "nobody is signed
      // in" would let a screen offer nothing to a caller who is signed in — and, worse, let a guard mistake
      // the two.
      await signIn(credentialPayload({ user: currentUser({ roles: [], permissions: [] }) }));

      expect(store.isAuthenticated()).toBe(true);
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.currentUser()).not.toBeNull();
    });

    it('retains a false super-user flag as data', async () => {
      await signIn(credentialPayload({ user: currentUser({ isSuperUser: false }) }));

      expect(store.isSuperUser()).toBe(false);
      expect(typeof store.currentUser()?.isSuperUser).toBe('boolean');
      expect(store.isAuthenticated())
        .withContext('a false flag on a held session is distinguishable from having no session')
        .toBe(true);
    });

    it('retains a transport status of zero, which the transport reports when no response arrived', async () => {
      // Zero is a real observation rather than a missing one, so it is recorded as zero
      // and classified rather than folded into "no status".
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush(null, { status: 0, statusText: 'Unknown Error' });

      await expectAsync(inFlight).toBeRejected();

      expect(store.failureStatus()).toBe(0);
      expect(store.hasFailure()).toBe(true);
      expect(store.severity())
        .withContext('nothing anticipated this, so it is the one most worth showing')
        .toBe('error');
    });
  });

  // -------------------------------------------------------------------------
  // PHASE OWNERSHIP AND CANCELLATION
  // -------------------------------------------------------------------------
  /**
   * ⚠ THE DEFECT THESE CASES PIN IS A PERMANENTLY BUSY STORE. Every command is a cold observable the
   * CALLER subscribes to, and that caller is normally a component. When the component is destroyed
   * mid-flight — a navigation away from the sign-in screen, a route change during an identity read — the
   * subscription is torn down.
   */
  // =========================================================================
  // TENANT ADMINISTRATION — THE ONE ANSWER THE APPLICATION ASKS
  // =========================================================================
  describe('tenant administration', () => {
    it('reports no administration before anybody signs in', () => {
      expect(store.administersCurrentPortal())
        .withContext('an unresolved caller administers nothing, which is the safe direction')
        .toBe(false);
      expect(store.holdsPortalAdministration()).toBe(false);
      expect(store.isSuperUser()).toBe(false);
    });

    it('reports administration when the server derives it, with no role of that name held', async () => {
      // ⚠ THE REASON THIS PROJECTION EXISTS. Administration is conferred by
      // `Portals.AdministratorRoleId`, a per-tenant COLUMN naming whichever role administers that tenant —
      // and `Roles.RoleName` is an ordinary updatable column.
      await signIn(
        credentialPayload({
          user: currentUser({
            isSuperUser: false,
            isPortalAdministrator: true,
            roles: ['Site Managers'],
          }),
        }),
      );

      expect(store.administersCurrentPortal()).toBe(true);
      expect(store.roles())
        .withContext('the role list is data, and it is not what decided this')
        .toEqual(['Site Managers']);
    });

    it('reports NO administration for a caller holding the literal administrator role name', async () => {
      await signIn(
        credentialPayload({
          user: currentUser({
            isSuperUser: false,
            isPortalAdministrator: false,
            roles: ['Administrators'],
          }),
        }),
      );

      expect(store.roles()).toEqual(['Administrators']);
      expect(store.administersCurrentPortal())
        .withContext('a role NAME confers nothing; the server\u2019s determination decides')
        .toBe(false);
    });

    it('reports administration for a host account whose derived fact is false', async () => {
      // ⚠ THE HOST ARM, AND WHY THIS IS NOT SIMPLY THE DERIVED FACT. The API's own handler opens with `if
      // (account.IsSuperUser) return true` — a host account administers every tenant — but the sign-in and
      // renewal responses carry an authority-minimised snapshot in which the derived fact is FALSE while
      // the host flag is present and true.
      await signIn(
        credentialPayload({
          user: currentUser({
            isSuperUser: true,
            isPortalAdministrator: false,
            roles: [],
          }),
        }),
      );

      expect(store.holdsPortalAdministration())
        .withContext('the derived arm alone is false, exactly as the snapshot reports it')
        .toBe(false);
      expect(store.isSuperUser()).toBe(true);
      expect(store.administersCurrentPortal())
        .withContext('the host arm carries it, which is what the enforcing policy also does')
        .toBe(true);
    });

    it('withdraws administration the moment the session ends', async () => {
      await signIn(credentialPayload({ user: currentUser({ isPortalAdministrator: true }) }));

      expect(store.administersCurrentPortal()).toBe(true);

      store.reset();

      expect(store.administersCurrentPortal())
        .withContext('a discarded session administers nothing')
        .toBe(false);
    });
  });

  describe('phase ownership', () => {
    it('returns to idle when a sign-in is abandoned before it answers', () => {
      const subscription = store.login({ username: 'admin', password: FAKE_PASSWORD }).subscribe({
        // A cancelled command produces neither, and an unhandled error would fail the run.
        error: () => undefined,
      });

      expect(store.phase()).toBe('authenticating');
      expect(store.isAuthenticating()).toBe(true);

      // What a destroyed component does to its subscription.
      subscription.unsubscribe();

      expect(store.phase())
        .withContext('an abandoned sign-in must not leave the store busy for good')
        .toBe('idle');
      expect(store.isAuthenticating()).toBe(false);

      // The request was cancelled rather than answered, so nothing is outstanding to verify.
      httpMock.match(LOGIN_URL).forEach((request) => {
        expect(request.cancelled).toBeTrue();
      });
    });

    it('returns to idle when an identity read is abandoned before it answers', async () => {
      await signIn();

      const subscription = store.loadCurrentUser().subscribe({ error: () => undefined });

      expect(store.phase()).toBe('loadingIdentity');

      subscription.unsubscribe();

      expect(store.phase()).toBe('idle');

      httpMock.match(ME_URL).forEach((request) => {
        expect(request.cancelled).toBeTrue();
      });
    });

    it('returns to idle when a renewal is abandoned before it answers', async () => {
      await signIn();

      const subscription = store.refreshSession().subscribe({ error: () => undefined });

      expect(store.phase()).toBe('refreshing');

      subscription.unsubscribe();

      expect(store.phase())
        .withContext('an abandoned renewal must not leave the store refreshing for good')
        .toBe('idle');

      // ⚠ AND THE RENEWAL ITSELF DELIBERATELY SURVIVES, which is the one place cancellation is the WRONG
      // response.
      const renewals = httpMock.match(REFRESH_URL);

      expect(renewals.length).toBe(1);
      expect(renewals[0].cancelled)
        .withContext('the shared renewal outlives the subscriber that started it')
        .toBeFalse();

      renewals[0].flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }),
      );
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);
    });

    // ⚠ THE MIRROR DEFECT, and the reason the release is ticketed rather than unconditional.
    it('does not let an abandoned command reset a phase claimed after it', async () => {
      const abandoned = store
        .login({ username: 'first', password: FAKE_PASSWORD })
        .subscribe({ error: () => undefined });

      expect(store.phase()).toBe('authenticating');

      // The successor claims the phase BEFORE the loser's cleanup runs.
      const successor = store
        .loadCurrentUser()
        .subscribe({ error: () => undefined });

      expect(store.phase()).toBe('loadingIdentity');

      abandoned.unsubscribe();

      expect(store.phase())
        .withContext("the loser's cleanup must be inert, not authoritative")
        .toBe('loadingIdentity');

      // And the successor still releases it correctly on its own terms.
      successor.unsubscribe();

      expect(store.phase()).toBe('idle');

      httpMock.match(LOGIN_URL).forEach((request) => {
        expect(request.cancelled).toBeTrue();
      });
      httpMock.match(ME_URL).forEach((request) => {
        expect(request.cancelled).toBeTrue();
      });
    });

    it('still returns to idle after a command that fails', async () => {
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.phase())
        .withContext('the failure path releases the phase like every other exit')
        .toBe('idle');
    });
  });

  // -------------------------------------------------------------------------
  // IDENTITY LIFETIME
  // -------------------------------------------------------------------------
  /**
   * ⚠ AN IDENTITY IS A DESCRIPTION OF ONE PARTICULAR SESSION and has no meaning apart from one.
   * `currentUser` PREFERS the fetched identity over the copy inside the stored session, so a stale write
   * wins the disagreement — which meant one account's roles, display name and e-mail address could be on
   * screen while another account's credentials were the ones being sent.
   */
  describe('identity lifetime', () => {
    it('discards an identity read that lands after the session ended', async () => {
      await signIn();

      const abandoned = firstValueFrom(store.loadCurrentUser());
      const read = httpMock.expectOne(ME_URL);

      // The session ends while the read is in the air.
      tokenStorage.clear();

      read.flush({ data: currentUser(), meta: null });

      await expectAsync(abandoned).toBeResolved();

      expect(store.currentUser())
        .withContext('a late identity read must not republish a signed-out account')
        .toBeNull();
      expect(store.isAuthenticated()).toBe(false);
    });

    it('discards an identity read that lands after a different account signed in', async () => {
      await signIn();

      const abandoned = firstValueFrom(store.loadCurrentUser());
      const read = httpMock.expectOne(ME_URL);

      // A different account is established while the read is in the air.
      const replacement: CurrentUser = { ...currentUser(), userId: 99, username: 'other' };

      tokenStorage.store({
        accessToken: 'fake-access-token-other',
        expiresAtUtc: '2100-01-01T00:00:00.000Z',
        refreshToken: 'fake-refresh-token-other',
        mustChangePassword: false,
        mustUpdateProfile: false,
        passwordExpiring: false,
        user: replacement,
      });

      read.flush({ data: currentUser(), meta: null });

      await expectAsync(abandoned).toBeResolved();

      expect(store.currentUser())
        .withContext("the newer account's identity must win, not the older read")
        .toEqual(replacement);
    });

    it('clears the identity synchronously when signing out, before the server answers', async () => {
      await signIn();

      expect(store.currentUser()).not.toBeNull();

      store.logout().subscribe();

      // ⚠ NOT AWAITED. The point is that the identity is already gone at this instant - before the
      // revocation round trip completes.
      expect(store.currentUser())
        .withContext('sign-out takes effect locally when it is asked for, not when it answers')
        .toBeNull();
      expect(store.isAuthenticated()).toBe(false);

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  // -------------------------------------------------------------------------
  // THE SESSION'S FOOTPRINT BEYOND THIS STORE
  // -------------------------------------------------------------------------
  /**
   * ⚠ ENDING A SESSION'S AUTHORITY IS NOT THE SAME AS ERASING ITS FOOTPRINT. Discarding the token and the
   * identity projection stops the application ACTING as the account.
   */
  describe('session footprint', () => {
    it('purges the domain stores when signing out', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      store.logout().subscribe();

      // ⚠ NOT AWAITED, for the same reason as the identity case above: the purge must happen when sign-out
      // is ASKED FOR, not when the revocation round trip answers.
      expect(purge)
        .withContext('signing out empties the domain stores immediately')
        .toHaveBeenCalledTimes(1);

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
    });

    it('purges the domain stores even when revocation fails', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      store.logout().subscribe({ error: () => undefined });

      httpMock
        .expectOne(LOGOUT_URL)
        .flush({ title: 'Service Unavailable' }, { status: 503, statusText: 'Service Unavailable' });

      // A server that cannot revoke the renewal credential does not get to keep the previous operator's
      // data on this device. The local discard is unconditional precisely because it is the one part of
      // signing out that cannot fail.
      expect(purge)
        .withContext('an unreachable server does not leave the footprint behind')
        .toHaveBeenCalledTimes(1);
    });

    it('purges the domain stores when a session is replaced by a different account', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      const replacement = firstValueFrom(store.login({ username: 'other', password: 'Secret-2' }));

      expect(purge)
        .withContext('the purge happens as the attempt starts, not after it succeeds')
        .toHaveBeenCalledTimes(1);

      const exchange = httpMock.expectOne(LOGIN_URL);

      exchange.flush(credentialPayload({ accessToken: 'fake-access-token-other' }));
      answerIdentityRead('fake-access-token-other');

      await expectAsync(replacement).toBeResolved();
    });

    it('purges the domain stores even when the replacing sign-in is refused', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      const refused = firstValueFrom(store.login({ username: 'other', password: 'wrong' }));

      httpMock
        .expectOne(LOGIN_URL)
        .flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

      await expectAsync(refused).toBeRejected();

      // Purging only on SUCCESS would leave the previous account's data in memory for the whole duration of
      // a failed attempt — which is exactly the case where the person at the keyboard is LEAST likely to be
      // the previous operator.
      expect(purge)
        .withContext('a failed sign-in still ends the session it displaced')
        .toHaveBeenCalledTimes(1);
    });

    it('purges the domain stores when a renewal cannot be completed', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      const renewal = firstValueFrom(store.refreshSession());

      httpMock
        .expectOne(REFRESH_URL)
        .flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

      await expectAsync(renewal).toBeRejected();

      expect(purge)
        .withContext('a session that cannot be renewed is over, footprint included')
        .toHaveBeenCalledTimes(1);
    });

    it('does not purge the domain stores while a session is merely being renewed', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      const renewal = firstValueFrom(store.refreshSession());

      httpMock.expectOne(REFRESH_URL).flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
          expiresAtUtc: EXPIRES_AT_UTC_ROTATED,
        }),
      );

      // A renewal re-reads the identity with the rotated credential, so the sequence is two
      // requests and both must be answered or the harness reports an outstanding request.
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      await expectAsync(renewal).toBeResolved();

      // A successful renewal is the SAME session continuing. Purging here would discard the listings the
      // operator is looking at every time their token rotated, which is a functional regression rather than
      // a hardening.
      expect(purge)
        .withContext('renewing a session does not discard the work in progress')
        .not.toHaveBeenCalled();
    });

    it('does not purge the domain stores for an ordinary recoverable failure', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      const read = firstValueFrom(store.loadCurrentUser());

      httpMock
        .expectOne(ME_URL)
        .flush({ title: 'Internal Server Error' }, { status: 500, statusText: 'Server Error' });

      await expectAsync(read).toBeRejected();

      expect(purge)
        .withContext('a server fault is not a session ending')
        .not.toHaveBeenCalled();
      expect(store.isAuthenticated())
        .withContext('and the session itself survives it')
        .toBeTrue();
    });
  });

  // -------------------------------------------------------------------------
  // THE READ-ONLY SURFACE
  // -------------------------------------------------------------------------
  describe('read-only surface', () => {
    it('publishes every projection without a writer', () => {
      const projections: readonly (readonly [string, () => unknown])[] = [
        ['phase', store.phase],
        ['isBusy', store.isBusy],
        ['isAuthenticating', store.isAuthenticating],
        ['isRefreshing', store.isRefreshing],
        ['isSigningOut', store.isSigningOut],
        ['isLoadingIdentity', store.isLoadingIdentity],
        ['currentUser', store.currentUser],
        ['isAuthenticated', store.isAuthenticated],
        ['accessTokenExpiresAt', store.accessTokenExpiresAt],
        ['portalId', store.portalId],
        ['roles', store.roles],
        ['permissions', store.permissions],
        ['isSuperUser', store.isSuperUser],
        ['holdsPortalAdministration', store.holdsPortalAdministration],
        ['administersCurrentPortal', store.administersCurrentPortal],
        ['mustChangePassword', store.mustChangePassword],
        ['passwordExpiring', store.passwordExpiring],
        ['mustUpdateProfile', store.mustUpdateProfile],
        ['hasAdvisory', store.hasAdvisory],
        ['problem', store.problem],
        ['failureStatus', store.failureStatus],
        ['hasFailure', store.hasFailure],
        ['severity', store.severity],
        ['supportReference', store.supportReference],
        ['failureCode', store.failureCode],
        ['validationErrors', store.validationErrors],
        ['rateLimited', store.rateLimited],
        ['verificationRequired', store.verificationRequired],
        ['verificationPrompt', store.verificationPrompt],
      ];

      for (const [name, projection] of projections) {
        expect('set' in projection)
          .withContext(`${name} must expose no setter`)
          .toBe(false);
        expect('update' in projection)
          .withContext(`${name} must expose no updater`)
          .toBe(false);
      }

      expect(projections.length)
        .withContext('every projection the store declares is covered above')
        .toBe(29);
    });

    it('reports a stable reading for a projection nothing has changed', async () => {
      await signIn();

      expect(store.currentUser()).toBe(store.currentUser());
      expect(store.phase()).toBe(store.phase());
    });
  });

  describe('session lifetime', () => {
    it('adopts nothing from a renewal that completes after the session has been signed out', async () => {
      await signIn();

      const renewal = firstValueFrom(store.refreshSession());
      const renewalRequest = httpMock.expectOne(REFRESH_URL);

      // The sign-out lands while the renewal is outstanding.
      const signOut = firstValueFrom(store.logout());

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(signOut).toBeResolved();

      expect(store.isAuthenticated())
        .withContext('the sign-out took effect immediately, not when the renewal settled')
        .toBeFalse();

      // Only now does the server answer the renewal, with a perfectly valid rotated pair, and the identity
      // read the renewal chains is answered too - so nothing is left outstanding and the assertions below
      // describe a settled store rather than a mid-flight one.
      renewalRequest.flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }),
      );
      httpMock.expectOne(ME_URL).flush(identityPayload(currentUser()));

      await expectAsync(renewal).toBeResolved();

      // The property that matters: NOTHING the renewal carried was adopted.
      expect(store.isAuthenticated())
        .withContext('the rotated pair is discarded rather than adopted')
        .toBeFalse();
      expect(tokenStorage.accessToken())
        .withContext('no credential is held at all')
        .toBeNull();
      expect(store.currentUser())
        .withContext('and the identity the renewal read is not published either')
        .toBeNull();
    });

    it('adopts nothing from a renewal that completes after the session was ended terminally', async () => {
      await signIn();

      const renewal = firstValueFrom(store.refreshSession());
      const renewalRequest = httpMock.expectOne(REFRESH_URL);

      store.endSession();

      expect(store.isAuthenticated()).toBeFalse();

      renewalRequest.flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }),
      );
      httpMock.expectOne(ME_URL).flush(identityPayload(currentUser()));

      await expectAsync(renewal).toBeResolved();

      expect(store.isAuthenticated())
        .withContext('a terminal end is as final as a sign-out')
        .toBeFalse();
      expect(tokenStorage.accessToken()).toBeNull();
      expect(store.currentUser()).toBeNull();
    });

    it('publishes no identity from a read that answers after the session has been signed out', async () => {
      await signIn();

      const read = firstValueFrom(store.loadCurrentUser());
      const request = httpMock.expectOne(ME_URL);

      const signOut = firstValueFrom(store.logout());

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(signOut).toBeResolved();

      request.flush(identityPayload(currentUser()));

      await expectAsync(read).toBeResolved();

      expect(store.currentUser())
        .withContext('a caller handed an identity must not have it rendered for a session that ended')
        .toBeNull();
      expect(store.isAuthenticated()).toBeFalse();
    });

    it('leaves a deliberate sign-out with nothing to explain when a renewal is superseded', async () => {
      await signIn();

      const renewal = firstValueFrom(store.refreshSession());
      const renewalRequest = httpMock.expectOne(REFRESH_URL);

      const signOut = firstValueFrom(store.logout());

      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(signOut).toBeResolved();

      renewalRequest.flush(
        credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }),
      );
      httpMock.expectOne(ME_URL).flush(identityPayload(currentUser()));

      await expectAsync(renewal).toBeResolved();

      expect(store.hasFailure())
        .withContext('supersession by a deliberate sign-out is not a fault to report')
        .toBeFalse();
      expect(store.problem()).toBeNull();
    });

    it('does not advance the verification ladder when a sign-in is superseded', async () => {
      const first = firstValueFrom(store.login(credentials()));
      const firstRequest = httpMock.expectOne(LOGIN_URL);

      // A second sign-in begins, which is the supersession.
      const second = firstValueFrom(store.login(credentials()));
      const secondRequest = httpMock.expectOne(LOGIN_URL);

      firstRequest.flush(refusal(INVALID_CREDENTIALS_CODE, 401), {
        status: 401,
        statusText: 'Unauthorized',
      });

      await expectAsync(first).toBeRejected();

      expect(store.verificationRequired())
        .withContext('a superseded attempt reveals nothing about the credentials given')
        .toBeFalse();

      // The live attempt then succeeds, and it is the one that decides the store's state.
      secondRequest.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);

      await expectAsync(second).toBeResolved();

      expect(store.isAuthenticated()).toBeTrue();
      expect(store.verificationRequired()).toBeFalse();
    });

    it('returns the phase to idle once the renewal settles', async () => {
      await signIn();

      const renewal = firstValueFrom(store.refreshSession());
      const renewalRequest = httpMock.expectOne(REFRESH_URL);

      expect(store.phase())
        .withContext('the renewal really is in flight')
        .toBe('refreshing');

      renewalRequest.flush(credentialPayload({ accessToken: FAKE_ACCESS_TOKEN_ROTATED }));
      httpMock.expectOne(ME_URL).flush(identityPayload(currentUser()));

      await expectAsync(renewal).toBeResolved();

      // The phase belongs to the command that claimed it, and only that command returns it.
      expect(store.phase()).toBe('idle');
      expect(store.isRefreshing()).toBeFalse();
    });

    it('keeps a recorded failure when a session is ended terminally, and clears it on reset', async () => {
      // The sign-in screen the operator is about to be sent to reads the recorded problem in order to
      // explain why they are back at it, so ending a session must not discard it. `reset` is the operation
      // that clears both.
      await refuseSignIn(refusal(INVALID_CREDENTIALS_CODE, 401), 401, 'Unauthorized');

      expect(store.hasFailure()).toBeTrue();

      store.endSession();

      expect(store.hasFailure())
        .withContext('the explanation survives the session it explains')
        .toBeTrue();
      expect(store.isAuthenticated()).toBeFalse();

      store.reset();

      expect(store.hasFailure()).toBeFalse();
      expect(store.problem()).toBeNull();
    });

    it('discards every session slice when a session is ended terminally', async () => {
      await signIn();

      expect(store.isAuthenticated()).toBeTrue();

      store.endSession();

      expect(store.isAuthenticated()).toBeFalse();
      expect(store.currentUser()).toBeNull();
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.phase()).toBe('idle');
    });
  });

  describe('coalescing and the shared renewal slot', () => {
    it('answers two simultaneous renewals with one request', async () => {
      await signIn();

      const first = firstValueFrom(store.renewSession());
      const second = firstValueFrom(store.renewSession());

      // `expectOne` is the assertion: a second request would fail it here, and any request
      // left unanswered would fail `verify()` afterwards.
      const renewal = httpMock.expectOne(REFRESH_URL);
      expect(bodyMemberNames(renewal.request.body)).toEqual(['refreshToken']);

      renewal.flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      const [one, other] = await Promise.all([first, second]);

      expect(one.accessToken)
        .withContext('both callers are answered by the single renewal that was issued')
        .toBe(FAKE_ACCESS_TOKEN_ROTATED);
      expect(other.accessToken).toBe(FAKE_ACCESS_TOKEN_ROTATED);
    });

    it('commits the rotated pair exactly once however many callers shared it', async () => {
      await signIn();

      const store_ = spyOn(tokenStorage, 'store').and.callThrough();

      const first = firstValueFrom(store.renewSession());
      const second = firstValueFrom(store.renewSession());
      const third = firstValueFrom(store.renewSession());

      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      await Promise.all([first, second, third]);

      expect(store_)
        .withContext('one renewal, one commit, whatever the subscriber count')
        .toHaveBeenCalledTimes(1);
    });

    it('frees the slot once a renewal settles, so a later one is a fresh request', async () => {
      await signIn();

      const first = firstValueFrom(store.renewSession());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);
      await first;

      // ⚠ THE SETTLED OBSERVABLE MUST NOT BE REPLAYED. `shareReplay` with no reference counting keeps a
      // buffered value indefinitely, so a slot that was never released would hand every future caller the
      // SAME rotated pair — a credential the server has already spent — for the rest of the application's
      // life.
      const second = firstValueFrom(store.renewSession());
      const renewal = httpMock.expectOne(REFRESH_URL);

      expect(renewal.request.body)
        .withContext('the second renewal presents the rotated credential, not the consumed one')
        .toEqual({ refreshToken: FAKE_RENEWAL_TOKEN_ROTATED });

      renewal.flush(credentialPayload({
        accessToken: 'fake-access-token-third',
        refreshToken: 'fake-renewal-token-third',
      }));
      answerIdentityRead('fake-access-token-third');

      expect((await second).accessToken).toBe('fake-access-token-third');
    });

    it('frees the slot even when the renewal was refused', async () => {
      await signIn();

      const refused = firstValueFrom(store.renewSession());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });
      await expectAsync(refused).toBeRejected();

      // Nothing is held now, so a later renewal has no credential to present — which is itself the proof
      // that the slot was released rather than replayed: a held slot would have answered from its buffer
      // instead of failing on the missing credential.
      await expectAsync(firstValueFrom(store.renewSession())).toBeRejected();
      httpMock.expectNone(REFRESH_URL);
    });

    it('surrenders the slot when signing out', async () => {
      await signIn();

      const abandoned = firstValueFrom(store.renewSession());
      const inFlight = httpMock.expectOne(REFRESH_URL);

      const signOut = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;

      // The abandoned renewal still arrives, because the shared source stays subscribed. Its commit is
      // epoch-suppressed, and the case below the group proves that; what THIS case proves is that the slot
      // no longer holds it.
      inFlight.flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);
      await abandoned;

      expect(store.isAuthenticated())
        .withContext('a renewal in flight during a sign-out cannot reinstate the session')
        .toBeFalse();

      // A renewal attempted now finds no credential rather than replaying the abandoned one.
      await expectAsync(firstValueFrom(store.renewSession())).toBeRejected();
      httpMock.expectNone(REFRESH_URL);
    });

    it('surrenders the slot when a different account signs in', async () => {
      await signIn();

      const abandoned = firstValueFrom(store.renewSession());
      const inFlight = httpMock.expectOne(REFRESH_URL);

      await signIn(
        credentialPayload({
          accessToken: 'fake-access-token-other',
          refreshToken: 'fake-renewal-token-other',
          user: currentUser({ userId: 99, username: OTHER_ACCOUNT_NAME }),
        }),
        credentials({ username: OTHER_ACCOUNT_NAME }),
      );

      inFlight.flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);
      await abandoned;

      expect(store.currentUser()?.username)
        .withContext('the newer account keeps the session the older renewal could not commit to')
        .toBe(OTHER_ACCOUNT_NAME);

      // Not replayed: the new session's own credential is presented instead.
      const renewal = firstValueFrom(store.renewSession());
      const request = httpMock.expectOne(REFRESH_URL);
      expect(request.request.body).toEqual({ refreshToken: 'fake-renewal-token-other' });

      request.flush(credentialPayload({
        accessToken: 'fake-access-token-fourth',
        refreshToken: 'fake-renewal-token-fourth',
      }));
      answerIdentityRead('fake-access-token-fourth');
      await renewal;
    });

    it('issues no request, and throws nothing synchronously, when no credential is held', async () => {
      // Posting an empty credential would be answered 400, which a caller could not tell apart from a
      // genuine rejection of a real one.
      const renewal = store.renewSession();

      httpMock.expectNone(REFRESH_URL);

      await expectAsync(firstValueFrom(renewal)).toBeRejected();
      httpMock.expectNone(REFRESH_URL);
    });

    it('withholds the commit from a renewal that lands after the session was signed out', async () => {
      await signIn();

      const abandoned = firstValueFrom(store.renewSession());
      const inFlight = httpMock.expectOne(REFRESH_URL);

      tokenStorage.clear();

      inFlight.flush(credentialPayload({
          accessToken: FAKE_ACCESS_TOKEN_ROTATED,
          refreshToken: FAKE_RENEWAL_TOKEN_ROTATED,
        }));
      answerIdentityRead(FAKE_ACCESS_TOKEN_ROTATED);

      // The caller is still ANSWERED — only the write to shared state is withheld.
      expect((await abandoned).accessToken).toBe(FAKE_ACCESS_TOKEN_ROTATED);
      expect(store.isAuthenticated())
        .withContext('a superseded renewal does not resurrect the session it renewed')
        .toBeFalse();
    });

    it('leaves a newer session alone when an older renewal is refused', async () => {
      await signIn();

      const refused = firstValueFrom(store.renewSession());
      const inFlight = httpMock.expectOne(REFRESH_URL);

      await signIn(
        credentialPayload({
          accessToken: 'fake-access-token-other',
          refreshToken: 'fake-renewal-token-other',
        }),
        credentials({ username: OTHER_ACCOUNT_NAME }),
      );

      inFlight.flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });
      await expectAsync(refused).toBeRejected();

      expect(store.isAuthenticated())
        .withContext('the newer session survives an older renewal being refused')
        .toBeTrue();
    });

    it('records nothing and claims no phase when the primitive is used', async () => {
      // The bookkeeping difference between the two entry points, asserted rather than described. A failure
      // record here would put a stale message in front of an operator who never asked to renew, and a phase
      // claim would make an unrelated screen report itself busy.
      await signIn();

      const refused = firstValueFrom(store.renewSession());

      expect(store.phase())
        .withContext('the primitive claims no phase')
        .toBe('idle');

      httpMock
        .expectOne(REFRESH_URL)
        .flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });
      await expectAsync(refused).toBeRejected();

      expect(store.hasFailure())
        .withContext('the primitive records no failure')
        .toBeFalse();
      expect(store.phase()).toBe('idle');
    });
  });

  // REVOCATION REPORTING
  // The sign-out POLICY moved here from `core/services/auth.service.ts`, and it arrived corrected.

  describe('renewal reporting', () => {
    it('words a refused renewal in shared wording, and lets the ending itself have the last word', async () => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      for (const status of [429, 500, 503]) {
        notify.calls.reset();

        await signIn();

        const renewal = firstValueFrom(store.refreshSession());
        httpMock
          .expectOne(REFRESH_URL)
          .flush(codelessRefusal(status), { status, statusText: 'Refused' });

        await expectAsync(renewal).toBeRejected();

        const worded: string[] = notify.calls.allArgs().map((args) => String(args[1]));
        const fromThisStore: string[] = worded.filter(
          (message) => message !== SESSION_ENDED_MESSAGE,
        );

        expect(fromThisStore.length)
          .withContext(`a ${status} is worded by this store, in the shared summariser's sentence`)
          .toBe(1);
        expect(fromThisStore[0]?.length)
          .withContext('and it says something rather than announcing an empty sentence')
          .toBeGreaterThan(0);

        expect(notifications.notifications().map((entry) => entry.message))
          .withContext(`and the ${status} ended the session, so the ending is what is left standing`)
          .toEqual([SESSION_ENDED_MESSAGE]);

        store.reset();
      }
    });

    it('says why the session ended when authority itself is refused, without wording it here', async () => {
      // The silence is still correct HERE, and for the reason the old comment gave — anything queued inside
      // the renewal is erased by the teardown that follows it. So the sentence comes from the boundary
      // owner, after the erasure, and this store adds nothing on top of it.
      const notify = spyOn(notifications, 'notify').and.callThrough();

      await signIn();
      notify.calls.reset();

      const renewal = firstValueFrom(store.refreshSession());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });

      await expectAsync(renewal).toBeRejected();

      expect(notify.calls.allArgs().map((args) => String(args[1])))
        .withContext('one report of one event, and this store is not the one making it')
        .toEqual([SESSION_ENDED_MESSAGE]);
      expect(notifications.notifications().map((entry) => entry.message)).toEqual([
        SESSION_ENDED_MESSAGE,
      ]);
      expect(store.isAuthenticated())
        .withContext('and the session is gone regardless')
        .toBeFalse();
    });

    it('reports an unreachable endpoint, which carries no status to be terminal by', async () => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      await signIn();
      notify.calls.reset();

      const renewal = firstValueFrom(store.refreshSession());
      httpMock
        .expectOne(REFRESH_URL)
        .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      await expectAsync(renewal).toBeRejected();

      // A renewal that never reached the server is still worded by this store...
      expect(
        notify.calls
          .allArgs()
          .map((args) => String(args[1]))
          .filter((message) => message !== SESSION_ENDED_MESSAGE).length,
      )
        .withContext('a renewal that never reached the server is still worded')
        .toBe(1);
      // ...and the session ended all the same, which is the part the operator has to act on.
      expect(notifications.notifications().map((entry) => entry.message)).toEqual([
        SESSION_ENDED_MESSAGE,
      ]);
    });

    it('says nothing about a renewal belonging to a session that has already been replaced', async () => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      await signIn();

      const renewal = firstValueFrom(store.refreshSession());
      const pending = httpMock.expectOne(REFRESH_URL);

      store.reset();
      notify.calls.reset();

      pending.flush(codelessRefusal(503), { status: 503, statusText: 'Service Unavailable' });

      await expectAsync(renewal).toBeRejected();

      expect(notify)
        .withContext('a superseded renewal reports nothing to anybody')
        .not.toHaveBeenCalled();
      expect(notifications.notifications())
        .withContext('and leaves nothing behind for whoever is signed in now to read')
        .toEqual([]);
    });
  });

  describe('revocation reporting', () => {
    it('reports no outstanding revocation before anything has been signed out', () => {
      expect(store.revocationOutstanding()).toBeFalse();
    });

    it('completes the sign-out and reports the revocation as outstanding when it is refused', async () => {
      const warning = spyOn(notifications, 'warning').and.callThrough();

      // 400 IS NO LONGER IN THIS SET, AND ITS REMOVAL IS THE POINT. A malformed or unknown credential is
      // TERMINAL: the server has said the value can never name a session, so there is no residue to report
      // and no retry that could help - reporting one would leave a notice nothing could ever clear.
      for (const status of [429, 503]) {
        warning.calls.reset();

        await signIn();

        const signOut = firstValueFrom(store.logout());
        httpMock
          .expectOne(LOGOUT_URL)
          .flush(codelessRefusal(status), { status, statusText: 'Refused' });

        // COMPLETES, because local sign-out already happened and the caller navigates away
        // from the signed-in shell when it does.
        await expectAsync(signOut).toBeResolved();

        expect(store.isAuthenticated())
          .withContext('the operator is signed out on this device whatever the server said')
          .toBeFalse();
        expect(store.revocationOutstanding())
          .withContext(`a ${status} means the credential may still be live, and it is reported`)
          .toBeTrue();
        expect(warning)
          .withContext(
            'the store records the residue but must not announce it: anything it raises here is ' +
              'erased by the teardown that follows, so the statement belongs to the lifecycle service',
          )
          .not.toHaveBeenCalled();

        store.reset();
      }
    });

    it('reports an unreachable endpoint as an outstanding revocation too', async () => {
      await signIn();

      const signOut = firstValueFrom(store.logout());
      httpMock
        .expectOne(LOGOUT_URL)
        .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      await expectAsync(signOut).toBeResolved();
      expect(store.revocationOutstanding()).toBeTrue();
    });

    it('reports a confirmed withdrawal as confirmed', async () => {
      const warning = spyOn(notifications, 'warning').and.callThrough();

      await signIn();

      const signOut = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await signOut;

      expect(store.revocationOutstanding()).toBeFalse();
      expect(warning)
        .withContext('nothing to announce when the server confirmed the withdrawal')
        .not.toHaveBeenCalled();
    });

    it('clears an earlier outstanding report once a later sign-out is confirmed', async () => {
      await signIn();

      const refused = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 503, statusText: 'Service Unavailable' });
      await refused;
      expect(store.revocationOutstanding()).toBeTrue();

      await signIn();

      const confirmed = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await confirmed;

      expect(store.revocationOutstanding())
        .withContext('a screen that reported an unconfirmed sign-out stops reporting it')
        .toBeFalse();
    });

    it('does not record the refused withdrawal as a session failure', async () => {
      await signIn();

      const signOut = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 503, statusText: 'Service Unavailable' });
      await signOut;

      expect(store.hasFailure()).toBeFalse();
      expect(store.problem()).toBeNull();
      expect(store.phase()).toBe('idle');
    });

    it('announces nothing, and reports nothing outstanding, when there was no credential to withdraw', async () => {
      const warning = spyOn(notifications, 'warning').and.callThrough();

      const signOut = firstValueFrom(store.logout());

      httpMock.expectNone(LOGOUT_URL);
      await expectAsync(signOut).toBeResolved();

      expect(store.revocationOutstanding())
        .withContext('nothing was left live, so there is nothing to warn about')
        .toBeFalse();
      expect(warning).not.toHaveBeenCalled();
    });

    // HOW LONG THE REPORT LIVES

    it('retires the report the moment a new attempt BEGINS, not when it succeeds', async () => {
      await signIn();

      const refused = firstValueFrom(store.logout());
      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(503), { status: 503, statusText: 'Service Unavailable' });
      await refused;

      expect(store.revocationOutstanding())
        .withContext('the precondition: a withdrawal the server never confirmed')
        .toBeTrue();

      const nextAttempt = firstValueFrom(store.login(credentials()));

      // ⚠ ASSERTED WHILE THE CREDENTIAL EXCHANGE IS STILL IN FLIGHT. Clearing on success would leave the
      // previous session's warning standing above the form for the whole duration of the attempt — which is
      // precisely when somebody is reading that form.
      expect(store.revocationOutstanding())
        .withContext('retired at the start of the attempt, before its outcome is known')
        .toBeFalse();

      const payload = credentialPayload();
      httpMock.expectOne(LOGIN_URL).flush(payload);
      answerIdentityRead(payload.data.accessToken, payload.data.user);
      await nextAttempt;

      expect(store.revocationOutstanding()).toBeFalse();
    });

    it('keeps the report retired when that new attempt is REFUSED', async () => {
      // The other half of clearing at the start: a refused attempt must not inherit the warning either. The
      // person at the keyboard has already been shown it once, and re-presenting it beside a rejected
      // sign-in reads as an explanation of the rejection.
      await signIn();

      const refused = firstValueFrom(store.logout());
      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(503), { status: 503, statusText: 'Service Unavailable' });
      await refused;
      expect(store.revocationOutstanding()).toBeTrue();

      await refuseSignIn(codelessRefusal(401), 401, 'Unauthorized');

      expect(store.revocationOutstanding())
        .withContext('the refusal is reported through the failure record, not through this flag')
        .toBeFalse();
      expect(store.hasFailure())
        .withContext('and the refusal itself IS reported')
        .toBeTrue();
    });

    it('retires the report when the store is reset', async () => {
      await signIn();

      const refused = firstValueFrom(store.logout());
      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(503), { status: 503, statusText: 'Service Unavailable' });
      await refused;
      expect(store.revocationOutstanding()).toBeTrue();

      store.reset();

      expect(store.revocationOutstanding())
        .withContext('a reset session carries nothing forward from the one it replaced')
        .toBeFalse();
    });

    it('raises the report only in the withdrawal request FAILING, so the discard cannot erase it', async () => {
      // ⚠ THE ORDERING CASE, and the reason clearing on every boundary is safe at all. Sign-out discards
      // the session — which retires this report — BEFORE it posts the withdrawal, so a naive reading
      // suggests the clear could race ahead of the report it is meant to raise.
      await signIn();

      const inFlight = firstValueFrom(store.logout());

      expect(store.revocationOutstanding())
        .withContext('the discard has already run and retired any earlier report')
        .toBeFalse();

      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(429), { status: 429, statusText: 'Too Many Requests' });
      await inFlight;

      expect(store.revocationOutstanding())
        .withContext('and the refusal raises it afterwards, where nothing can clear it back')
        .toBeTrue();
    });
  });

  // CONTRACT ENFORCEMENT AT THE COMPOSITION POINT
  // ⚠ EVERY CASE IN THIS SECTION EXISTS BECAUSE THE MECHANISM IT EXERCISES WAS DEAD. The store retained a
  // credential after a refused sign-out and published a bounded retry for it, and a security review found
  // that NOTHING in the application ever called that retry and that no specification ever exercised it — so
  // every refused withdrawal was retained and then left retained, and the session it named stayed renewable
  // until its absolute expiry.

  describe('outstanding withdrawal retries', () => {
    /** The delays the ladder waits between attempts, in the order it waits them. */
    const LADDER_DELAYS_MS = [1_000, 2_000, 4_000];

    /**
     * Signs in without awaiting, for the cases that run under virtual time. `fakeAsync` forbids an
     * `async` body, so the promise-based {@link signIn} helper cannot be used inside one.
     *
     * @param payload The credential payload to answer the exchange with.
     */
    function signInSynchronously(
      payload: SuccessEnvelope<LoginResponse> = credentialPayload(),
    ): void {
      store.login(credentials()).subscribe();
      httpMock.expectOne(LOGIN_URL).flush(payload);
      answerIdentityRead(payload.data.accessToken, payload.data.user);
    }

    /**
     * Signs out with a withdrawal the server refuses, leaving the credential retained.
     *
     * @param status The transport status to refuse with.
     */
    function signOutRefused(status: number): void {
      store.logout().subscribe();
      httpMock
        .expectOne(LOGOUT_URL)
        .flush(codelessRefusal(status), { status, statusText: 'Refused' });
    }

    /**
     * Answers one withdrawal attempt, asserting which credential it presented.
     *
     * @param expectedToken The credential the attempt is required to carry.
     * @param status The transport status to answer with.
     */
    function answerWithdrawal(expectedToken: string, status: number): void {
      const attempt = httpMock.expectOne(LOGOUT_URL);

      expect(attempt.request.method).toBe('POST');
      expect(attempt.request.body)
        .withContext('a withdrawal presents the retained credential and nothing else')
        .toEqual({ refreshToken: expectedToken });

      attempt.flush(
        status === 204 ? null : codelessRefusal(status),
        { status, statusText: status === 204 ? 'No Content' : 'Refused' },
      );
    }

    it('does nothing, and posts nothing, when no withdrawal is outstanding', async () => {
      // The ordinary case on the sign-in screen: it is driven on every arrival and almost every
      // arrival has nothing to drain.
      await expectAsync(firstValueFrom(store.retryOutstandingRevocation())).toBeResolved();

      expectNoCredentialTraffic();
      expect(store.revocationOutstanding()).toBeFalse();
    });

    it('withdraws the retained credential and retires it when the server confirms', async () => {
      await signIn();
      signOutRefused(503);

      expect(tokenStorage.pendingRevocations())
        .withContext('the precondition: a credential is held because the withdrawal was refused')
        .toEqual([FAKE_RENEWAL_TOKEN]);
      expect(store.revocationOutstanding()).toBeTrue();

      const drain = firstValueFrom(store.retryOutstandingRevocation());

      answerWithdrawal(FAKE_RENEWAL_TOKEN, 204);
      await expectAsync(drain).toBeResolved();

      expect(tokenStorage.pendingRevocations())
        .withContext('the residue is gone because the server has now ended the session')
        .toEqual([]);
      expect(store.revocationOutstanding())
        .withContext('and the notice the sign-in screen renders is retired with it')
        .toBeFalse();
    });

    it('retires the credential on a terminal refusal WITHOUT retrying it', async () => {
      for (const status of [400, 404, 422]) {
        await signIn();
        signOutRefused(503);

        const drain = firstValueFrom(store.retryOutstandingRevocation());

        answerWithdrawal(FAKE_RENEWAL_TOKEN, status);
        await expectAsync(drain).toBeResolved();

        httpMock.expectNone(LOGOUT_URL);
        expect(tokenStorage.pendingRevocations())
          .withContext(`a ${status} is terminal, so the credential is retired`)
          .toEqual([]);
        expect(store.revocationOutstanding()).toBeFalse();

        store.reset();
      }
    });

    it('keeps the credential, and reports the residue, when every permitted attempt is refused', fakeAsync(() => {
      signInSynchronously();
      signOutRefused(503);

      store.retryOutstandingRevocation().subscribe();

      // The initial attempt, then one per rung of the ladder.
      answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);
      }

      expect(tokenStorage.pendingRevocations())
        .withContext('the session may still be live, so the credential is kept for a later drive')
        .toEqual([FAKE_RENEWAL_TOKEN]);
      expect(store.revocationOutstanding())
        .withContext('and the residue is reported rather than absorbed')
        .toBeTrue();

      // The ladder is bounded: nothing further is scheduled once it is exhausted.
      tick(60_000);
      httpMock.expectNone(LOGOUT_URL);
    }));

    it('stops climbing the moment the server confirms, rather than spending the whole ladder', fakeAsync(() => {
      signInSynchronously();
      signOutRefused(503);

      store.retryOutstandingRevocation().subscribe();

      answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);
      tick(LADDER_DELAYS_MS[0]);
      answerWithdrawal(FAKE_RENEWAL_TOKEN, 204);

      tick(60_000);
      httpMock.expectNone(LOGOUT_URL);
      expect(tokenStorage.pendingRevocations()).toEqual([]);
      expect(store.revocationOutstanding()).toBeFalse();
    }));

    // ---------------------------------------------------------------------
    // ⚠ SEVERAL SESSIONS AT ONCE — THE HALF THE SINGLE SLOT LOST
    // ---------------------------------------------------------------------

    it('retains a second refused sign-out ALONGSIDE the first rather than overwriting it', async () => {
      await signIn(credentialPayload({ refreshToken: 'fake-first-session-credential' }));
      signOutRefused(503);

      await signIn(credentialPayload({ refreshToken: 'fake-second-session-credential' }));
      signOutRefused(503);

      expect(tokenStorage.pendingRevocations())
        .withContext(
          'the measured defect: the second sign-out used to displace the first session\'s ' +
            'credential, leaving that session renewable with nothing able to withdraw it',
        )
        .toEqual(['fake-first-session-credential', 'fake-second-session-credential']);
    });

    it('withdraws every retained credential, oldest first, one at a time', async () => {
      await signIn(credentialPayload({ refreshToken: 'fake-first-session-credential' }));
      signOutRefused(503);

      await signIn(credentialPayload({ refreshToken: 'fake-second-session-credential' }));
      signOutRefused(429);

      const drain = firstValueFrom(store.retryOutstandingRevocation());

      // SEQUENTIAL, and asserted as such: the second attempt cannot have been issued while the first is
      // unanswered, because the failure being recovered from is usually a rate limit and firing the whole
      // set at once is the surest way to be refused again.
      answerWithdrawal('fake-first-session-credential', 204);
      answerWithdrawal('fake-second-session-credential', 204);

      await expectAsync(drain).toBeResolved();

      expect(tokenStorage.pendingRevocations()).toEqual([]);
      expect(store.revocationOutstanding()).toBeFalse();
    });

    it('keeps reporting a residue when one credential is withdrawn and another is not', fakeAsync(() => {
      signInSynchronously(credentialPayload({ refreshToken: 'fake-first-session-credential' }));
      signOutRefused(503);

      signInSynchronously(credentialPayload({ refreshToken: 'fake-second-session-credential' }));
      signOutRefused(503);

      store.retryOutstandingRevocation().subscribe();

      answerWithdrawal('fake-first-session-credential', 204);

      // The second exhausts its ladder.
      answerWithdrawal('fake-second-session-credential', 503);

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        answerWithdrawal('fake-second-session-credential', 503);
      }

      expect(tokenStorage.pendingRevocations())
        .withContext('one confirmed withdrawal does not retire another session\'s residue')
        .toEqual(['fake-second-session-credential']);
      expect(store.revocationOutstanding())
        .withContext('and the report describes what is STILL held, not the last outcome seen')
        .toBeTrue();
    }));

    it('reports a confirmed sign-out as confirmed only when nothing else is still held', async () => {
      await signIn(credentialPayload({ refreshToken: 'fake-first-session-credential' }));
      signOutRefused(503);

      await signIn(credentialPayload({ refreshToken: 'fake-second-session-credential' }));

      const signOut = firstValueFrom(store.logout());
      httpMock.expectOne(LOGOUT_URL).flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(signOut).toBeResolved();

      expect(tokenStorage.pendingRevocations())
        .withContext('this session was withdrawn cleanly, so only its credential is retired')
        .toEqual(['fake-first-session-credential']);
      expect(store.revocationOutstanding())
        .withContext('the earlier session is still live on the server, and that is still reported')
        .toBeTrue();
    });

    // ---------------------------------------------------------------------
    // ⚠ COALESCING AND THE REPORT'S LIFETIME
    // ---------------------------------------------------------------------

    it('coalesces concurrent drives onto one set of withdrawals', async () => {
      await signIn();
      signOutRefused(503);

      const first = firstValueFrom(store.retryOutstandingRevocation());
      const second = firstValueFrom(store.retryOutstandingRevocation());

      answerWithdrawal(FAKE_RENEWAL_TOKEN, 204);

      await expectAsync(first).toBeResolved();
      await expectAsync(second).toBeResolved();

      expect(tokenStorage.pendingRevocations()).toEqual([]);
    });

    it('starts a fresh drive once the previous one has finished', fakeAsync(() => {
      signInSynchronously();
      signOutRefused(503);

      // A first drive that spends its whole ladder and gives up.
      store.retryOutstandingRevocation().subscribe();
      answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);
      }

      expect(tokenStorage.pendingRevocations()).toEqual([FAKE_RENEWAL_TOKEN]);

      // The coalescing slot must be surrendered on completion, or the sign-in screen's next
      // arrival would replay a settled drain forever and never post anything again.
      store.retryOutstandingRevocation().subscribe();
      answerWithdrawal(FAKE_RENEWAL_TOKEN, 204);

      expect(tokenStorage.pendingRevocations()).toEqual([]);
      expect(store.revocationOutstanding()).toBeFalse();
    }));

    it('does not raise the previous session\'s report behind a sign-in that has already begun', fakeAsync(() => {
      // The report has a documented lifetime of exactly one session boundary: it is retired the moment a
      // new attempt BEGINS, so one operator is never shown a sentence about another's session.
      signInSynchronously();
      signOutRefused(503);
      expect(store.revocationOutstanding()).toBeTrue();

      store.retryOutstandingRevocation().subscribe();
      const firstAttempt = httpMock.expectOne(LOGOUT_URL);

      // A new sign-in begins while the drain is still climbing, which retires the report.
      store.login(credentials()).subscribe();
      const exchange = httpMock.expectOne(LOGIN_URL);

      expect(store.revocationOutstanding())
        .withContext('retired by the attempt starting, as it has always been')
        .toBeFalse();

      firstAttempt.flush(codelessRefusal(503), { status: 503, statusText: 'Refused' });

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);
      }

      expect(store.revocationOutstanding())
        .withContext('the drain must not put the previous session\'s sentence in front of this one')
        .toBeFalse();
      expect(tokenStorage.pendingRevocations())
        .withContext('though the residue itself is still held, for the next drive to try')
        .toEqual([FAKE_RENEWAL_TOKEN]);

      exchange.flush(credentialPayload());
      answerIdentityRead(FAKE_ACCESS_TOKEN);
    }));

    it('withdraws a retained credential even though a different account is signed in now', async () => {
      // The credential names a session that has ended. Withholding the withdrawal because
      // somebody else is signed in would leave that session renewable for its full lifetime.
      await signIn(credentialPayload({ refreshToken: 'fake-first-session-credential' }));
      signOutRefused(503);

      await signIn(
        credentialPayload({
          refreshToken: 'fake-second-session-credential',
          user: currentUser({ username: OTHER_ACCOUNT_NAME }),
        }),
      );

      const drain = firstValueFrom(store.retryOutstandingRevocation());

      answerWithdrawal('fake-first-session-credential', 204);
      await expectAsync(drain).toBeResolved();

      expect(tokenStorage.pendingRevocations()).toEqual([]);
      expect(store.isAuthenticated())
        .withContext('and withdrawing it does not disturb the session now held')
        .toBeTrue();
      expect(store.currentUser()?.username).toBe(OTHER_ACCOUNT_NAME);
    });

    it('announces nothing itself, leaving the wording to the lifecycle owner', fakeAsync(() => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      signInSynchronously();
      signOutRefused(503);
      notify.calls.reset();

      store.retryOutstandingRevocation().subscribe();
      answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        answerWithdrawal(FAKE_RENEWAL_TOKEN, 503);
      }

      expect(notify)
        .withContext(
          'the drain records the residue in a signal the sign-in screen renders; a transient ' +
            'message raised here would describe a session two boundaries ago',
        )
        .not.toHaveBeenCalled();
    }));

    it('never errors, whatever the server answers, so a caller needs no failure handler', fakeAsync(() => {
      // An unreachable server is the failure this mechanism exists for, and it must arrive as
      // COMPLETION: the caller is a screen's initialisation, which has nowhere to put an error.
      signInSynchronously();
      signOutRefused(503);

      let completed = false;
      let errored: unknown = null;

      store.retryOutstandingRevocation().subscribe({
        error: (cause: unknown) => {
          errored = cause;
        },
        complete: () => {
          completed = true;
        },
      });

      const unreachable = (): void => {
        httpMock
          .expectOne(LOGOUT_URL)
          .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
      };

      unreachable();

      for (const delay of LADDER_DELAYS_MS) {
        tick(delay);
        unreachable();
      }

      expect(errored).toBeNull();
      expect(completed).toBeTrue();
      expect(store.revocationOutstanding()).toBeTrue();
    }));
  });

  describe('refuses a payload that does not match its contract', () => {
    it('refuses a credential exchange whose access token is blank', async () => {
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush({
        data: { ...credentialPayload().data, accessToken: '' },
        meta: null,
      });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();

      // ⚠ AND NO SESSION IS ESTABLISHED. The identity read is never reached, so nothing was
      // stored, and the sign-in reads as the refusal it was.
      httpMock.expectNone(ME_URL);
      expect(store.isAuthenticated()).toBeFalse();
      expect(store.currentUser()).toBeNull();
      expect(store.phase()).toBe('idle');
    });

    it('refuses a credential exchange whose expiry instant cannot be parsed', async () => {
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush({
        data: { ...credentialPayload().data, expiresAtUtc: 'whenever' },
        meta: null,
      });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();
      expect(store.isAuthenticated()).toBeFalse();
    });

    it('refuses a bootstrap identity whose role list is not a list of strings', async () => {
      // The SECOND request of the sign-in, and the more dangerous of the two to leave unchecked: a role
      // list arriving as null would fault the first membership test, and the session would already have
      // been half-composed.
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      httpMock
        .expectOne(ME_URL)
        .flush({ data: { ...currentUser(), roles: null }, meta: null });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();
      expect(store.isAuthenticated())
        .withContext('the credential was never committed, so no half-populated session survives')
        .toBeFalse();
      expect(store.roles()).toEqual([]);
    });

    it('refuses a bootstrap identity whose super-user flag is a string', async () => {
      // `'false'` is truthy, so an unchecked read would grant the whole console to a caller
      // the server described as an ordinary member.
      const inFlight = firstValueFrom(store.login(credentials()));

      httpMock.expectOne(LOGIN_URL).flush(credentialPayload());
      httpMock
        .expectOne(ME_URL)
        .flush({ data: { ...currentUser(), isSuperUser: 'false' }, meta: null });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();
      expect(store.isSuperUser()).toBeFalse();
      expect(store.isAuthenticated()).toBeFalse();
    });

    it('refuses a renewal whose rotated credential is blank, and discards the session', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.refreshSession());

      httpMock.expectOne(REFRESH_URL).flush({
        data: { ...credentialPayload().data, refreshToken: '  ' },
        meta: null,
      });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();

      // A renewal that cannot be trusted is terminal exactly as a refused one is: keeping the
      // consumed credential would mean presenting it again and being refused again.
      httpMock.expectNone(ME_URL);
      expect(store.isAuthenticated()).toBeFalse();

      expect(store.hasFailure()).toBeFalse();
      expect(notifications.notifications().map((entry) => entry.message)).toEqual([
        SESSION_ENDED_MESSAGE,
      ]);
    });

    it('refuses a deliberate identity read whose payload is not an object', async () => {
      await signIn();

      const inFlight = firstValueFrom(store.loadCurrentUser());

      httpMock.expectOne(ME_URL).flush({ data: 'admin', meta: null });

      expect(isContractViolation(await rejectionOf(inFlight))).toBeTrue();
      expect(store.currentUser()?.username)
        .withContext('the identity established at sign-in is left as it was')
        .toBe(ACCOUNT_NAME);
    });

    it('admits an empty display name, because the column defaults to one', async () => {
      // The counterpart to the refusals above. `Users.DisplayName` is NOT NULL defaulting to the empty
      // string, so the empty-string encoding of absence is a schema constraint here rather than a
      // data-layer convention, and a stricter rule would refuse real accounts.
      await signIn(credentialPayload({ user: currentUser({ displayName: '' }) }));

      expect(store.currentUser()?.displayName).toBe('');
      expect(store.isAuthenticated()).toBeTrue();
    });
  });
});
