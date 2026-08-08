/**
 * Specification for `core/state/auth.store.ts`.
 *
 * Four properties of that store are load-bearing, and each of them is a property no
 * compiler can check, so each is proven here by exercising the store against a mock
 * transport rather than by reading it:
 *
 * 1. The verification ladder is PROGRESSIVE AND STATEFUL, and its revealed state flips
 *    only for a verification refusal — never for an ordinary refused credential, a
 *    refusal to authorise, a rate-limiter rejection or a server fault.
 * 2. The legacy sign-in defect recorded below is CORRECTED: a locked-out account is
 *    refused, and the two insecure-default-credential outcomes are completed sign-ins
 *    carrying an advisory rather than refusals.
 * 3. NO READABLE MEMBER OF THE STORE HOLDS A CREDENTIAL. Not the access token, not the
 *    renewal token, not the submitted password.
 * 4. Every projection is genuinely read-only, so a consumer cannot write to it.
 *
 * ## Provenance of every assertion below
 *
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197` is the
 * only place in the legacy application where the ladder existed. Supporting sources
 * are `Library/Components/Users/Membership/UserLoginStatus.vb:L23-L31` for the outcome
 * vocabulary, `Library/Components/Shared/Null.vb:L36-L85` for the sentinel contract,
 * `Website/admin/Security/AccessDenied.ascx.vb:L41-L47` for the severity a refusal is
 * presented at, and `Website/release.config` for the session lifetime and the
 * credential policy. The legacy tree contains no automated test of any kind, so
 * nothing here is ported — it is authored against those sources.
 *
 * ## Migration context this specification makes observable
 *
 * 1. THE SIGN-IN DEFECT IS CORRECTED. `Login.ascx.vb:L187` reads
 *    `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`, and because the
 *    branch at L168 consumes only the not-approved outcome, EVERY other non-zero
 *    outcome fell through and counted as authenticated — ordinal 3 (locked out),
 *    ordinal 5 and ordinal 6 all admitted the caller. Ordinal 3 is now a refusal;
 *    ordinals 5 and 6 are successes carrying an advisory.
 * 2. THE LADDER IS REPRODUCED AS A SIGNAL. L171 branches on whether the field had
 *    already been revealed, which in the legacy survived a postback through control
 *    state. Exactly three codes existed — L175 and L180 `"EnterCode"`, L178
 *    `"InvalidCode"`, L184 `"UserNotAuthorized"` — and exactly three exist now.
 * 3. THE HUMAN-VERIFICATION CHALLENGE IS DROPPED. L162 gated the whole handler on
 *    `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)`; the control
 *    belongs to a tree this migration excludes. The compensating control is a request
 *    rate limiter on the credential endpoints, whose rejection is proven below to be a
 *    calm state of its own rather than a fault.
 * 4. CONTROL STATE AND SESSION STATE ARE ELIMINATED. Across the five in-scope
 *    `Library/Components` trees there are exactly FOUR control-state sites, all
 *    carrying one key, all in `Library/Components/Users/UserModuleBase.vb` (L469,
 *    L491, L498, L503), and no server-session site at all. One fact survives, and it
 *    is the ladder's revealed state.
 * 5. CUSTODY OF THE SESSION IS THE CUSTODIAN'S. `core/services/token-storage.service.ts`
 *    keeps it in memory only. The specifications below prove there is exactly ONE copy
 *    of a credential in the application and that the store is not a second one.
 * 6. RENEWAL AFTER A REFUSED REQUEST IS THE INTERCEPTOR'S. That policy belongs to
 *    `core/interceptors/auth.interceptor.ts`; it is proven below that this store never
 *    renews on its own initiative, only when a caller asks.
 * 7. SIGNING OUT HAS NO STATELESS COUNTERPART TO THE LEGACY COOKIE CLEAR. It is
 *    renewal-credential revocation plus local discard, and the discard is proven to
 *    happen on success, on failure and on an early unsubscribe alike.
 * 8. THE SINGLE-VALUED LEGACY POST-CREDENTIAL OUTCOME BECAME THREE INDEPENDENT
 *    BOOLEANS. `Library/Components/Users/Membership/UserValidStatus.vb` could report
 *    exactly one of five states; more than one advisory can now be true at once, and a
 *    specification below proves it.
 * 9. THE TENANT'S REGISTRATION MODE IS NOT CONSULTED CLIENT-SIDE. The legacy gate at
 *    L170 read `PortalRegistrationType.VerifiedRegistration` from ambient per-request
 *    page state; the renamed enumeration is declared in `core/models/portal.model.ts`
 *    alone and is deliberately neither imported here nor by the store, because the
 *    SERVER already applied that fact when it chose which of the three codes to emit.
 * 10. THE NUMERIC OUTCOME VOCABULARY NEVER CROSSES THE WIRE. `auth.model.ts` declares
 *    it as reference wording only; a specification below proves the store keys on the
 *    string code carried by the document's `type` member and on nothing else.
 * 11. THE SESSION LIFETIME IS PARITY WITH `Website/release.config:L147`,
 *    `<forms name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/>`.
 *    The number is a server setting; only the absolute instant the server stamped
 *    crosses the wire, and it is proven below to be retained byte for byte. Nothing
 *    here computes an expiry, reads a clock or decodes a token.
 * 12. THE CREDENTIAL POLICY IS PRESERVED RATHER THAN TIGHTENED, and RETRIEVAL IS
 *    ABOLISHED. `release.config` registered the membership provider with
 *    `enablePasswordRetrieval="true"` (L239) and `passwordFormat="Encrypted"` (L245)
 *    against a decryption key committed to source control at L91-L92, so every stored
 *    credential was recoverable by anyone with repository access. A specification below
 *    proves the store publishes no retrieval command. Policy enforcement is the
 *    server's and the form's; the store surfaces no policy metadata that could
 *    contradict either, so no policy specification belongs here.
 * 13. A REFUSAL DISCLOSES ONLY A CODE. The legacy drew no distinction between an
 *    unknown account and an incorrect credential, and a specification below proves two
 *    attempts with different account names publish identical state.
 * 14. A REFUSAL IS A WARNING, NOT A FAULT. `AccessDenied.ascx.vb` performs no
 *    permission check at all and BOTH branches of its handler use the yellow-warning
 *    message type — L43 for the message arriving on the query string, which it renders
 *    encoded after decoding, and L45 for the localised default.
 * 15. LOCALISATION IS NOT PORTED. No translation runtime is present in this workspace
 *    and none is referenced here; the legacy resource files are wording reference only.
 * 16. IMPLICIT COERCIONS ARE MADE EXPLICIT. The 39 legacy administrative code-behinds
 *    compiled with strict type checking DISABLED — `release.config:L125`,
 *    `<compilation debug="false" strict="false">` — so they could legally rely on late
 *    binding and silent narrowing. Strict TypeScript is what forces each such coercion
 *    to surface, and typing every fixture below as the real contract interface is part
 *    of that forcing function: a mistyped member is a compile error rather than an
 *    undefined value discovered at run time.
 *
 * ## Harness notes
 *
 * - The framework is Karma with Jasmine, which is what the validation command's
 *   browser argument selects. No other test framework, spy library or assertion
 *   library is referenced.
 * - The real authentication service runs against the mock transport, so each
 *   specification proves the path, the method, the request body AND the store's
 *   handling of the response in one pass.
 * - EVERY ASSERTED PATH IS RELATIVE. The build target that compiles this file declares
 *   no file replacement, so the production configuration is in play and its base is the
 *   relative `/api/v1`. The proxy that serves the bundle forwards that prefix to the
 *   interface on the same origin, so no absolute origin is correct here and none
 *   appears.
 * - No effect is scheduled by the store — every derivation is a computed projection
 *   and reads synchronously — so no effect-flushing primitive is called. Verified
 *   against the installed `@angular/core` 19.2.25, where the testing harness exposes
 *   `flushEffects` and no tick primitive at all.
 * - Deterministic throughout: no timer, no clock read, no randomness and no real
 *   network. No web-storage interface, no cookie jar and no client-side database is
 *   referenced, so this file cannot normalise a custody violation by accident.
 * - ZERO-BASED PAGE INDEXING IS DELIBERATELY NOT EXERCISED HERE. This store paginates
 *   nothing: authentication has no list, no page size and no total count. Its absence
 *   is a property of the subject, not an omission in this specification.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { CurrentUser, LoginRequest, LoginResponse } from '../models/auth.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import { AuthService } from '../services/auth.service';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';
import { isContractViolation } from '../utils/decode.util';
import { AUTH_STORE_PHASES, AuthStore, REVOCATION_FAILED_MESSAGE } from './auth.store';
import { SessionTeardownService } from './session-teardown.service';

// ---------------------------------------------------------------------------
// PATHS
//
// Relative, and asserted as written. The base is the production one because the
// build target that compiles this file replaces no file.
// ---------------------------------------------------------------------------

const LOGIN_URL = '/api/v1/auth/login';

const REFRESH_URL = '/api/v1/auth/refresh';

const LOGOUT_URL = '/api/v1/auth/logout';

const ME_URL = '/api/v1/auth/me';

// ---------------------------------------------------------------------------
// THE FAILURE-CODE CHANNEL
//
// A code travels inside the problem document's `type` member behind this prefix and
// on NO other member. The lowercase spelling is the literal the workspace's own
// resolver matches, and it is reproduced here rather than approximated: a document
// whose `type` misses the prefix yields no code at all, which would leave the ladder
// untouched and make a ladder specification silently vacuous.
// ---------------------------------------------------------------------------

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** Legacy `"EnterCode"` (`Login.ascx.vb:L175` and L180). */
const VERIFICATION_REQUIRED_CODE = 'auth.verification_required';

/** Legacy `"InvalidCode"` (`Login.ascx.vb:L178`). */
const VERIFICATION_CODE_INVALID_CODE = 'auth.verification_code_invalid';

/** Legacy `"UserNotAuthorized"` (`Login.ascx.vb:L184`). */
const ACCOUNT_NOT_APPROVED_CODE = 'auth.account_not_approved';

/** Wording carried with the verification-required outcome. */
const VERIFICATION_REQUIRED_MESSAGE = 'Enter Your Verification Code';

/** Wording carried with the invalid-code outcome. */
const VERIFICATION_CODE_INVALID_MESSAGE = 'Invalid Verification Code';

/** Wording carried with the not-authorised outcome. */
const ACCOUNT_NOT_APPROVED_MESSAGE = 'You are not currently authorized to login to this site.';

/**
 * A refused credential, which is deliberately OUTSIDE the closed verification
 * vocabulary — reaching the ladder with it would be the defect this file guards
 * against.
 */
const INVALID_CREDENTIALS_CODE = 'auth.invalid_credentials';

/** Legacy outcome ordinal 3, which `Login.ascx.vb:L187` admitted and which is refused now. */
const ACCOUNT_LOCKED_OUT_CODE = 'auth.account_locked_out';

/**
 * The legacy message literal, spelled exactly as `Login.ascx.vb:L175` spelled it.
 *
 * Present as a NEGATIVE fixture. The legacy spelling is no longer a code, so a
 * document carrying it must leave the ladder alone; asserting that is what proves the
 * ladder keys on the migrated vocabulary rather than on a string that merely looks
 * familiar.
 */
const LEGACY_ENTER_CODE_LITERAL = 'EnterCode';

// ---------------------------------------------------------------------------
// CREDENTIAL-SHAPED FIXTURES
//
// ⚠ OBVIOUSLY FAKE, WITHOUT EXCEPTION. The legacy anti-pattern is a real decryption
// key committed to source control at `Website/release.config:L91-L92`, identical in
// the development configuration, which — combined with reversible storage and
// retrieval both enabled — made every stored credential recoverable by anyone who
// could read the repository. No value below could be mistaken for a real secret, and
// every one of them exists so a specification can prove the store does NOT hold it.
// ---------------------------------------------------------------------------

const FAKE_ACCESS_TOKEN = 'fake-access-token-not-a-real-credential';

const FAKE_ACCESS_TOKEN_ROTATED = 'fake-rotated-access-token-not-a-real-credential';

const FAKE_RENEWAL_TOKEN = 'fake-renewal-token-not-a-real-credential';

const FAKE_RENEWAL_TOKEN_ROTATED = 'fake-rotated-renewal-token-not-a-real-credential';

const FAKE_PASSWORD = 'not-a-real-password';

const ACCOUNT_NAME = 'admin';

const OTHER_ACCOUNT_NAME = 'no-such-account';

const SUBMITTED_VERIFICATION_CODE = 'wrong-code';

/**
 * The expiry instant, as an absolute instant in Coordinated Universal Time.
 *
 * A LITERAL, never a computation. Deriving it from a clock read would make the
 * specification's outcome depend on when it ran, and asserting a DURATION rather than
 * the instant would test arithmetic this store deliberately does not perform.
 */
const EXPIRES_AT_UTC = '2100-01-01T00:00:00.000Z';

/** A rotated instant, so a renewal can be told from the sign-in that preceded it. */
const EXPIRES_AT_UTC_ROTATED = '2100-01-02T00:00:00.000Z';

// ---------------------------------------------------------------------------
// DIAGNOSTIC FIXTURES
// ---------------------------------------------------------------------------

/**
 * The identifier the server validated for the request.
 *
 * The operator's only join key between a browser-side report and a server-side
 * record, which is why a specification below proves it survives into the store's
 * failure slice rather than being reduced away.
 */
const CORRELATION_ID = 'correlation-id-for-this-attempt';

/** The framework's own request identifier, which is a different value in a different format. */
const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';

/**
 * A message carrying an executable payload.
 *
 * NOT hypothetical. Unescaping the 37 in-scope legacy resource files first — a naive
 * search finds nothing — shows 76 values carrying markup, four of them carrying a
 * script element, including a live one in the site-settings resource file under the
 * advertising key. A message reaching this store is therefore untrusted text, and the
 * store's contract is to hold it inert.
 */
const SCRIPT_BEARING_DETAIL = '<script>alert(1)</script>';

/** The legacy leading break tag, in the spelling used by `Website/admin/Portal/Signup.ascx.vb`. */
const UNCLOSED_BREAK_DETAIL = '<br>The account could not be created.';

/** The same tag in the spelling used by `Website/admin/Users/User.ascx.vb:L187`. */
const CLOSED_BREAK_DETAIL = '<br/>The account could not be created.';

/**
 * A per-field key, reproduced with the server's own casing.
 *
 * The keys name model members rather than serialised members, so they are NOT
 * lower-camel-cased on the way out. A fixture that camel-cased this would read as
 * absent at run time with no compile error, which is exactly the trap the bracket-access
 * specification below exists to close.
 */
const MODEL_STATE_KEY = 'UserName';

// ---------------------------------------------------------------------------
// THE RESPONSE ENVELOPE
//
// Every successful payload arrives wrapped, so a fixture that returned a bare
// contract object would be unwrapped into `undefined` and every downstream assertion
// would pass vacuously against absent data. The wrapper is declared locally rather
// than imported: it is declared in the paging model, which this file has no other
// reason to reach into, and the shape needed here is two members wide.
// ---------------------------------------------------------------------------

interface SuccessEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

// ---------------------------------------------------------------------------
// FACTORIES
//
// Functions rather than shared constants, so no specification can observe a value a
// previous one mutated. Each is typed as the real contract interface, which is what
// makes a misspelled member — the single-lowercase-letter identity-casing trap in
// particular — a compile error rather than an undefined value at run time.
// ---------------------------------------------------------------------------

/**
 * An identity.
 *
 * The default tenant key is ZERO, which is a real key rather than a tidy one: the
 * tenant table's key column is seeded at minus one, so both minus one and zero
 * identify real tenants and neither may be read as absence.
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
 * A credential-exchange payload, wrapped.
 *
 * All three advisories default to `false`, because `false` is DATA on this contract
 * and a fixture that omitted them would not compile.
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

/**
 * A problem document carrying a code.
 *
 * The five standard members are always present so the document narrows, and the
 * diagnostic members are supplied per specification.
 */
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

/**
 * A problem document carrying NO code.
 *
 * The document members are all optional by specification, and an intermediary between
 * the browser and the interface can answer with a body this application never wrote,
 * so a codeless refusal is a real case rather than a contrived one.
 */
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
 * The sorted member names of a request body.
 *
 * The body arrives loosely typed from the mock transport, so it is narrowed to
 * `unknown` on the way in and tested structurally. Sorted so an assertion does not
 * depend on the order a contract happens to declare its members in.
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
 * Whether a value, or anything nested inside it, contains a secret.
 *
 * Recursive on purpose. A credential could leak as a whole slice, as one member of a
 * held contract object, or as one entry of a held list, and a check that only compared
 * whole strings would miss the latter two. There is no cycle to guard against: every
 * value this store publishes is a primitive, a frozen list of strings, or a flat
 * contract object.
 *
 * @param value Anything the store published.
 * @param secret The fixture value that must not appear.
 * @returns True when the secret appears anywhere inside the value.
 */
function containsSecret(value: unknown, secret: string): boolean {
  if (typeof value === 'string') {
    return value.includes(secret);
  }

  if (Array.isArray(value)) {
    const entries: readonly unknown[] = value;

    return entries.some((entry) => containsSecret(entry, secret));
  }

  if (typeof value === 'object' && value !== null) {
    return Object.values(value as Record<string, unknown>).some((entry: unknown) =>
      containsSecret(entry, secret),
    );
  }

  return false;
}

/**
 * The store's two injected collaborators, excluded from the custody sweep below.
 *
 * Excluded because they are not slices of this store and because ONE OF THEM HOLDS A
 * CREDENTIAL BY DESIGN — the custodian is the single place a session lives, and
 * reaching through it would assert the opposite of what the sweep is for. What the
 * sweep proves is that the store is not a SECOND copy.
 */
const COLLABORATOR_MEMBERS: readonly string[] = Object.freeze(['auth', 'tokenStorage']);

/**
 * Every value the store makes readable, by member name.
 *
 * The mechanism, stated plainly because it is unusual: a signal is a zero-argument
 * function held as an own member of the instance, so enumerating the instance's own
 * members and invoking each zero-argument function-valued one reads EVERY slice the
 * store carries — including the ones it declares private, since that keyword vanishes
 * at run time. That breadth is the point. A sweep restricted to the members it happened
 * to know the names of would not notice a credential copied into a slice nobody thought
 * to name, and a leak into a private slice is still a leak. A member that is not a
 * zero-argument function is reported as it stands, so a credential parked in a plain
 * field is caught too.
 *
 * The cast is a structural read of an instance whose member names are known only at run
 * time. It widens rather than narrows, so it asserts nothing about the shape and cannot
 * conceal a type error.
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
 * The member names whose readable value contains a secret.
 *
 * Returned as names rather than as a boolean so a failure reports WHICH member leaked,
 * which is the difference between a diagnosis and a puzzle.
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
        // ⚠ ORDER IS LOAD-BEARING. The real transport is registered first and the
        // mock backend second, because the mock REPLACES the backend the first
        // provider installed. Reversing the two leaves the real backend in place and
        // the specifications below would attempt live requests — which, against a
        // relative path with no server behind it, fails in a way that reads like a
        // defect in the store rather than a defect in this harness.
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
    // ⚠ MANDATORY. Without it an unflushed request, or one nobody expected, passes
    // in silence — and a specification that proves the store issued NO request is
    // worth nothing unless something checks that claim at the end.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // SHARED SEQUENCES
  //
  // A sign-in is TWO requests: the credential exchange, then an identity read
  // carrying the freshly issued credential as a header. Both must be answered or the
  // verification above fails, so every path that signs in goes through one of these.
  // -------------------------------------------------------------------------

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
   * Asserts the rejection itself, because the store re-throws every failure unchanged
   * so a caller can still act on it — and an unhandled rejection would surface as a
   * suite-level error rather than as this specification's outcome.
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
   * Returning the reason rather than asserting inside a callback keeps every expectation in
   * the body of the case, where a failure is attributed to the case that caused it.
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

  // -------------------------------------------------------------------------
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
      // Distinct from reporting the mildest severity. Nothing has failed, so there is
      // nothing to present at any forcefulness, and a banner keyed on this must show
      // nothing rather than show something calm.
      expect(store.severity()).toBeNull();
      expect(store.supportReference()).toBeNull();
    });

    it('changes no state and issues no request for a command that is never subscribed', () => {
      // Every command body is deferred, so building one has no effect. Without that,
      // a command built and discarded would leave the store reporting itself busy
      // forever and a spinner keyed on it would never stop.
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

  // -------------------------------------------------------------------------
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
      // The legacy call took EIGHT arguments, of which a literal authentication-type
      // discriminator and a challenge-adjacent argument both disappear with the single
      // bearer-credential path. A discriminator that can hold one value is not modelled.
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
      // Proves the division of labour: the store sequences, the service transports. If
      // the store enriched the request - an account name for a log line, a tenant hint -
      // the argument seen here would differ from the argument passed in.
      //
      // ⚠ ASSERTED ON THE REQUEST ARGUMENT, NOT ON THE WHOLE ARGUMENT LIST. The service also
      // accepts an optional TENANT SELECTOR, which the sign-in screen resolves from the address
      // and hands through for an arrival host that matches no alias row. It is a separate
      // argument precisely so it cannot be smuggled into the credential body, and this case is
      // about the body: the selector's own absence is asserted immediately below.
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
      // The identity is fetched with the credential just issued rather than taken from
      // the exchange body, so the entitlements the store publishes are the ones the
      // server will actually enforce.
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

  // -------------------------------------------------------------------------
  // THE VERIFICATION LADDER
  //
  // A faithful reproduction of `Login.ascx.vb:L168-L185`, and the reason it is
  // PROGRESSIVE rather than a lookup from code to message: L171 reads
  // `If Not rowVerification1.Visible Then`, so the legacy branched on whether the
  // field had ALREADY been revealed. The first refusal revealed it and asked for a
  // code (L173, L174, L175); only a SUBSEQUENT refusal could judge what had been
  // typed into it (L177, L178). A flat mapping would lose that progression, and the
  // ladder would silently restart at its first rung on every attempt.
  //
  // The revealed state is therefore the ONE piece of legacy control state that
  // survives the migration, and it lives in the store rather than on a component
  // precisely so navigation destroying a component cannot reset it.
  // -------------------------------------------------------------------------
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
      // ⚠ THE PROGRESSION, MADE OBSERVABLE. The server may report an invalid code on
      // the very first attempt, and the ladder still answers "enter your code" —
      // because `Login.ascx.vb` tests VISIBILITY at L171 before it inspects what was
      // typed at L177. Judging content that has not been asked for yet would be the
      // flat mapping this ladder exists to avoid.
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
      // The legacy absent-text marker IS the empty string — `Null.vb:L71-L75` has the
      // body `Return ""`, not `Return Nothing` — so an empty code and an absent one
      // were always one branch. The two spellings are interchangeable in the legacy
      // source itself: `Signup.ascx.vb:L227` tests against a literal empty string while
      // L315 tests against the marker, in the same file.
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
      // The legacy gate at L170 read the tenant's registration mode from ambient
      // per-request page state and produced `"UserNotAuthorized"` at L184 when it was
      // anything else. That fact is the SERVER's to apply — it already applied it when
      // it chose this code — so the renamed enumeration, which is declared in the
      // portal model alone, is neither imported here nor consulted by the store.
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
      // ⚠ THE NEGATIVE HALF OF THE PROOF, WITHOUT WHICH THE POSITIVE HALF SHOWS
      // NOTHING. A signal that flipped for an ordinary refused credential would put a
      // verification field in front of someone who simply mistyped a password, and a
      // specification that only ever fed it the code that SHOULD flip it could not
      // tell the difference.
      //
      // The revealed state is never cleared by a failure, so these can run in sequence:
      // if any single entry flipped it, every later assertion would fail too.
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
      // Nothing is broken when someone is asked for a code, so nothing may be presented
      // as though it were. The legacy authority is `AccessDenied.ascx.vb`, whose two
      // branches at L43 and L45 both use the yellow-warning message type.
      await refuseSignIn(refusal(VERIFICATION_REQUIRED_CODE, 401), 401, 'Unauthorized');

      expect(store.verificationPrompt()?.severity).toBe('warning');
      expect(store.severity()).toBe('warning');

      await refuseSignIn(refusal(ACCOUNT_NOT_APPROVED_CODE, 403), 403, 'Forbidden');

      expect(store.verificationPrompt()?.severity).toBe('warning');
      expect(store.severity()).toBe('warning');
    });
  });

  // -------------------------------------------------------------------------
  // THE CORRECTED SIGN-IN OUTCOME MAPPING
  //
  // `Login.ascx.vb:L187` reads `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`.
  // The branch above it at L168 consumes ONLY the not-approved outcome, so every other
  // non-zero outcome fell into that else arm and counted as authenticated. Three
  // outcomes were admitted that way: ordinal 3, a locked-out account, and ordinals 5
  // and 6, the two sign-ins with the product's well-known default credentials.
  //
  // The seven outcomes are mapped deliberately server-side now — ordinal 3 becomes a
  // refusal to authorise, and ordinals 5 and 6 become COMPLETED sign-ins carrying a
  // security advisory. The correction is forced by the target's transport semantics
  // rather than chosen, which is exactly why it is annotated rather than absorbed.
  //
  // ⚠ EVERY ASSERTION HERE IS ON AN ADVISORY BOOLEAN OR A TRANSPORT STATUS, NEVER ON A
  // NUMERIC OUTCOME. The numeric vocabulary is declared as reference wording and never
  // crosses the wire, so it is deliberately not imported by this file.
  // -------------------------------------------------------------------------
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
      // The legacy post-credential check reported exactly one of five states, with
      // precedence deciding which. Three INDEPENDENT booleans replace it, so a consumer
      // must render each on its own terms instead of switching on a single value — and
      // that difference is only observable if more than one can be true together.
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

    it('retains a false advisory as data rather than reading it as absent', async () => {
      // ⚠ `false` IS DATA. In the legacy null contract the absence test reported true
      // for `false` itself, so a legacy `false` and a legacy "unknown" were
      // indistinguishable. Every boolean on this contract is a plain non-nullable
      // boolean precisely because admitting a third state would advertise a distinction
      // the source data cannot make. The serialiser is configured never to elide a
      // default, so `false` arrives on the wire rather than being dropped.
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
      // The same answer as "no advisory", which is the correct one for a gate: an
      // unauthenticated caller is stopped by the authentication check, not by an
      // advisory.
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

  // -------------------------------------------------------------------------
  // CUSTODY
  //
  // Custody belongs to `core/services/token-storage.service.ts`, which keeps the
  // session in MEMORY ONLY. That is a stated non-functional requirement rather than a
  // preference, and the store's part in it is negative: it must not become a second
  // copy. No web-storage interface, no cookie jar and no client-side database is
  // referenced anywhere in this file, so nothing here can normalise a violation of
  // that by accident.
  // -------------------------------------------------------------------------
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
      // ⚠ THE CUSTODIAN'S WRITE METHOD IS CALLED ONCE, BY THE SERVICE THAT PERFORMED
      // THE EXCHANGE. A second call would mean a second copy, and a second copy is a
      // second thing to forget to clear.
      const spy = spyOn(tokenStorage, 'store').and.callThrough();

      await signIn();

      expect(spy).toHaveBeenCalledTimes(1);
    });

    it('publishes the expiry instant exactly as the server stamped it', async () => {
      // An absolute instant, passed through as the string the contract publishes. No
      // parse, no clock read and no lapsed-or-valid verdict — and deliberately no
      // assertion on a DURATION, because asserting sixty minutes here would test
      // arithmetic the client does not perform. The lifetime itself is parity with
      // `release.config:L147`, and it is a server setting.
      await signIn();

      expect(store.accessTokenExpiresAt()).toBe(EXPIRES_AT_UTC);
      expect(typeof store.accessTokenExpiresAt()).toBe('string');
    });

    it('reports the presence of a session rather than re-deciding its validity', async () => {
      // Reports what the custodian reports. An access credential that has lapsed still
      // yields true, because the correct response to lapsing is to renew — which
      // requires the session to still be here.
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

  // -------------------------------------------------------------------------
  // SIGNING OUT
  //
  // The legacy cookie clear has no stateless counterpart: it took effect at once,
  // whereas a signed bearer credential cannot be recalled once issued. Signing out is
  // therefore revocation of the RENEWAL credential plus a local discard. The server
  // keeps no deny-list, and the already-issued access credential stays technically
  // valid until it lapses — which is why that lifetime is short and why the expiry
  // instant is published rather than left implicit.
  // -------------------------------------------------------------------------
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
      // A person who asks to sign out must end up signed out on this device. Leaving
      // the session in place because a revocation request failed would be the opposite
      // of what they asked for, and they could not act on the error in any case.
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

  // -------------------------------------------------------------------------
  // RENEWAL
  //
  // ⚠ RENEWING AFTER A REFUSED REQUEST IS NOT THIS STORE'S JOB. That policy — deciding
  // WHEN a renewal is warranted, and what a refused one means for the request that
  // provoked it — belongs to `core/interceptors/auth.interceptor.ts`, which the store
  // references by path and never imports. No interceptor is registered in this harness, so
  // any renewal seen below could only have come from the store itself, which is what makes
  // the negative assertions binding.
  //
  // MIGRATION: COALESCING concurrent renewals, by contrast, IS this store's, and moved here
  //   from `core/services/auth.service.ts`. The thing being coalesced is the session, and
  //   abandoning an in-flight renewal and advancing the session generation are two halves of
  //   one act — a slot held by any other owner could be reached by neither. The cases for it
  //   are in their own group further down, and they came with the responsibility.
  //
  // The store still exposes a renewal COMMAND, so a caller may renew deliberately — a
  // route resolver re-establishing a session after a reload, for instance. That is
  // caller-driven, and it is tested separately from the automatic behaviour the store
  // must not have.
  // -------------------------------------------------------------------------
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

    it('discards the session when a renewal is refused, and keeps the problem that explains why', async () => {
      // ⚠ ORDER MATTERS AND IS ASSERTED. The session is discarded FIRST and the failure
      // recorded second. Reversing the two would clear the very problem a sign-in screen
      // needs in order to explain why the caller is back at it.
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
      expect(store.hasFailure()).toBe(true);
      expect(store.failureStatus()).toBe(401);
      expect(store.problem())
        .withContext('the problem survives the discard that preceded it')
        .not.toBeNull();
      expect(store.supportReference()).toBe(CORRELATION_ID);
      expect(store.phase()).toBe('idle');
    });

    it('reports a failure that carries neither a problem document nor a transport status', async () => {
      // A renewal with nothing to renew from fails before any request is made, so there
      // is no document and no status to record — yet the command unquestionably failed.
      // Inferring failure from the presence of its details would report this as success.
      const inFlight = firstValueFrom(store.refreshSession());

      await expectAsync(inFlight).toBeRejected();

      httpMock.expectNone(REFRESH_URL);
      expect(store.hasFailure()).toBe(true);
      expect(store.problem()).toBeNull();
      expect(store.failureStatus()).toBeNull();
      expect(store.failureCode()).toBeNull();
      expect(store.severity())
        .withContext('a failure nobody anticipated is the one most worth showing')
        .toBe('error');
      expect(store.phase()).toBe('idle');
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
      // The copy inside the stored session is a snapshot taken when the credentials were
      // issued, so an entitlement granted afterwards does not appear in it until the
      // session is renewed. A deliberate re-read is how a caller learns about it without
      // renewing.
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
      // ⚠ THE ROLE AND PERMISSION LISTS DECIDE NOTHING. They exist so a screen can avoid
      // offering an action the server would refuse. THE SERVER IS AUTHORITATIVE: it
      // re-authorises every request against stored state and answers with a refusal to
      // authorise, and nothing here may stand in for that.
      //
      // Two closed, non-interchangeable vocabularies also apply — the persisted
      // permission keys are not the server's authorisation policy names, and there is no
      // deny prefix in this generation of the product, so nothing here parses one.
      expect('hasPermission' in store).toBe(false);
      expect('isInRole' in store).toBe(false);
      expect('can' in store).toBe(false);
      expect('authorize' in store).toBe(false);
      expect('isAuthorized' in store).toBe(false);
    });

    it('exposes no credential-recovery command', () => {
      // Retrieval is ABOLISHED rather than ported. The legacy provider was registered
      // with retrieval enabled and reversible storage, against a key committed to source
      // control, so every stored credential was recoverable by anyone with repository
      // access. An administrative reset is the only remedy now, and it belongs to the
      // user store rather than here.
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

  // -------------------------------------------------------------------------
  // FAILURE REPORTING
  //
  // The whole structured document is kept, never reduced to a sentence. It carries the
  // failure code, the per-field dictionary and the support reference an operator needs,
  // and collapsing it to text would discard all three. Wording belongs to
  // the workspace's form-errors utility, under `core/utils`; this store keeps the
  // document and the classifications derived from it.
  // -------------------------------------------------------------------------
  describe('failure reporting', () => {
    it('retains the framework trace identifier a report has to quote', async () => {
      // ⚠ REQUIRED, NOT OPTIONAL. The identifier is derived server-side and appears on
      // the response, on the request envelope in the server's log and on every audit
      // event the request produced, so it is the operator's only join key between a
      // browser-side report and a server-side record. Reducing this slice to a sentence
      // would throw it away.
      await refuseSignIn(
        refusal(INVALID_CREDENTIALS_CODE, 401, { traceId: TRACE_ID }),
        401,
        'Unauthorized',
      );

      expect(store.problem()?.traceId).toBe(TRACE_ID);
      expect(store.supportReference()).toBe(TRACE_ID);
    });

    it('prefers the correlation identifier the server validated over the framework one', async () => {
      // Two independent values in different formats, of which only the former appears in
      // the server's own records. Both are RETAINED; the preference decides only which
      // one a person is asked to quote.
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
      // The legacy authority is `AccessDenied.ascx.vb`: 50 lines that perform NO
      // permission check and only present a denial, whose handler at L41-L47 uses the
      // yellow-warning message type on BOTH branches — L43 for the message arriving on
      // the query string, rendered encoded after decoding, and L45 for the localised
      // default. Presenting this in danger styling would tell a person something is
      // broken when the system is working exactly as configured.
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
      // The compensating control for the dropped human-verification challenge is a
      // request rate limiter on the credential paths, partitioned by calling address.
      // Nothing has failed when it rejects — the caller has simply attempted too often —
      // so it is reported separately from a refused credential and presented calmly. The
      // retry hint accompanying such a rejection arrives on a response header rather than
      // in the body, so no slice here carries one.
      await refuseSignIn(codelessRefusal(429), 429, 'Too Many Requests');

      expect(store.rateLimited()).toBe(true);
      expect(store.failureStatus()).toBe(429);
      // The INFORMATIONAL severity, not a warning: the shared classification treats a
      // rate-limit refusal as quieter than an ordinary refusal, because nothing was rejected
      // on its merits and the only action is to wait. The rule lives in one place so this
      // status cannot reach an operator as two different severities from two surfaces.
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
      // ⚠ BRACKET ACCESS, ALWAYS. The member is an index signature and this workspace
      // enables the compiler option that makes dot access on one an error, deliberately:
      // a key is only ever known at run time, and dot access would let a typo compile as
      // a silent absent value.
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
      // Unescaping the 37 in-scope legacy resource files first — a naive search finds
      // nothing — shows 76 values carrying markup, four of them carrying a script
      // element, including a live one in the site-settings resource file. A message
      // reaching this store is untrusted text, so it is held as a string and never as a
      // trusted-markup wrapper. Nothing here sanitises it, marks it safe, or renders it.
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
      // The legacy prefixed messages with a break tag in TWO spellings — the unclosed
      // form in `Website/admin/Portal/Signup.ascx.vb` at L193, L214, L221 and L323, and
      // the self-closing form in `Website/admin/Users/User.ascx.vb:L187`. Stripping is
      // the form-errors utility's job, at the point of presentation. The store keeps the
      // raw document, so a consumer that needs the original still has it.
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
      // ⚠ A SECURITY PROPERTY, PRESERVED DELIBERATELY. The legacy flow drew no
      // distinction between an unknown account and an incorrect credential, and neither
      // does this. Two attempts with different account names must publish IDENTICAL
      // state — if the store enriched a failure with the submitted name, or told the two
      // cases apart, these two readings would differ.
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

  // -------------------------------------------------------------------------
  // SENTINEL FIDELITY
  //
  // ⚠ THE MEASURED LEGACY NULL CONTRACT, from `Library/Components/Shared/Null.vb:L36-L85`:
  // the 16-bit and 32-bit integer markers are BOTH minus one (L36-L40, L41-L45), the byte
  // marker is 255 (L46-L50), the floating and decimal markers are the minimum value
  // (L51-L65), the date marker is the minimum date (L66-L70), THE STRING MARKER IS THE
  // EMPTY STRING — the body is literally `Return ""` (L71-L75) — the boolean marker is
  // false (L76-L80) and the identifier marker is the empty one (L81-L85). Its absence
  // test reports true for every one of those. 168 in-scope call sites depend on it.
  //
  // The collision that makes this section necessary: the tenant key column is seeded at
  // minus one, so MINUS ONE IS SIMULTANEOUSLY A REAL TENANT AND THE LEGACY MARKER FOR
  // "no integer", and zero is a real tenant too. Absence is therefore expressed as null
  // and never as zero or minus one. A guard written as a truthiness test, or as a
  // comparison against zero, would reject two real tenants.
  //
  // The serialiser is configured never to elide a default, precisely so zero, the empty
  // string and false all arrive on the wire instead of vanishing.
  // -------------------------------------------------------------------------
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
      // THE WHOLE POINT OF THE NULL CONVENTION, in one specification. Before signing in
      // there is no tenant to report and the answer is null; after signing in the answer
      // is the number the server sent, even when that number is zero.
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
      // The legacy marker for an absent string IS the empty string, and the two spellings
      // are used interchangeably in one legacy file — `Signup.ascx.vb:L227` tests against
      // a literal empty string while L315 tests against the marker. Neither is rewritten
      // into the other here.
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
      // An identity with no entitlement is a real identity. Reporting it the same way as
      // "nobody is signed in" would let a screen offer nothing to a caller who is signed
      // in — and, worse, let a guard mistake the two.
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
   * ⚠ THE DEFECT THESE CASES PIN IS A PERMANENTLY BUSY STORE.
   *
   * Every command is a cold observable the CALLER subscribes to, and that caller is normally a
   * component. When the component is destroyed mid-flight — a navigation away from the sign-in
   * screen, a route change during an identity read — the subscription is torn down. Neither
   * `tap` nor `catchError` runs on unsubscription, so a phase set on subscribe was never
   * returned to idle: the store stayed `authenticating` or `loadingIdentity` for the remainder
   * of the application's life, and every derived busy projection stayed busy with it. A submit
   * button that never re-enables is the visible symptom.
   *
   * `finalize` alone would trade that for the mirror defect, so the release is TICKETED: a
   * command may only return the store to idle while nothing has claimed the phase since. Both
   * halves are asserted below, and the second half is the one a naive fix would fail.
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
      // ⚠ THE CORRECTION THIS PROJECTION EXISTS FOR. Administration is conferred by
      // `Portals.AdministratorRoleId`, a per-tenant COLUMN naming whichever role administers
      // that tenant — and `Roles.RoleName` is an ordinary updatable column. So a legitimate
      // administrator routinely holds a role list containing nothing called `Administrators`,
      // and the API's `IsPortalAdministratorAsync` resolves the designated role ID against the
      // caller's active assignments rather than comparing any name.
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
      // ⚠ THE REGRESSION TEST. Three screens and the route gate each used to answer this
      // question for themselves by testing the role list for `Administrators`. A role of that
      // name may belong to a DIFFERENT tenant, and the tenant in hand may designate another
      // role entirely — so the name is right about the word and wrong about the portal.
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
      // ⚠ THE HOST ARM, AND WHY THIS IS NOT SIMPLY THE DERIVED FACT. The API's own handler
      // opens with `if (account.IsSuperUser) return true` — a host account administers every
      // tenant — but the sign-in and renewal responses carry an authority-minimised snapshot
      // in which the derived fact is FALSE while the host flag is present and true. Reading
      // the derived fact alone would withhold every administrative affordance from a host
      // account for the whole window between signing in and the current-account read landing.
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

      // ⚠ AND THE RENEWAL ITSELF DELIBERATELY SURVIVES, which is the one place cancellation is
      // the WRONG response. The authentication service shares one renewal between every caller
      // with `refCount: false` precisely so that abandoning one subscriber does not abandon the
      // renewal for the others still waiting on it — and because a rotated refresh token that
      // was issued but never stored would be presented again on the next attempt and read by
      // the server as a replay, which revokes the whole token family. So the phase is released
      // by the ticket while the request continues, and the epoch check inside the service is
      // what keeps its eventual result from committing against a session it no longer belongs
      // to. Answered here rather than left outstanding so `verify()` has nothing to report.
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

    // ⚠ THE MIRROR DEFECT, and the reason the release is ticketed rather than unconditional. An
    // operator who abandons one sign-in and immediately starts another must see the SECOND one
    // reported as in flight; a bare `finalize` would have the first one's cleanup reset the
    // phase the second had just claimed, so the screen would report idle while a request was
    // genuinely outstanding.
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
   * ⚠ AN IDENTITY IS A DESCRIPTION OF ONE PARTICULAR SESSION and has no meaning apart from
   * one. `currentUser` PREFERS the fetched identity over the copy inside the stored session, so
   * a stale write wins the disagreement — which meant one account's roles, display name and
   * e-mail address could be on screen while another account's credentials were the ones being
   * sent. These cases pin the fix at each of the three writers.
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

      // ⚠ NOT AWAITED. The point is that the identity is already gone at this instant - before
      // the revocation round trip completes. The previous arrangement discarded in a
      // `finalize`, leaving the account's display name and records on screen for the whole
      // duration of the request, unbounded on a slow or failing network.
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
   * ⚠ ENDING A SESSION'S AUTHORITY IS NOT THE SAME AS ERASING ITS FOOTPRINT.
   *
   * Discarding the token and the identity projection stops the application ACTING as the
   * account. It does not empty the four domain stores, each of which is `providedIn: 'root'`
   * and therefore holds ONE instance that outlives the session it was populated for — and a
   * single-page application is not reloaded between sign-outs, so the same instances carry
   * straight across to the next account.
   *
   * Each of those stores had a `reset()` written for exactly this moment and NOTHING CALLED
   * ANY OF THEM, so signing out left the previous operator's tenant listings, the account
   * record they had open, the role assignments naming other accounts, and a serialised export
   * of a module's data in memory. These cases pin the fan-out at every path that ends a
   * session.
   *
   * The fan-out is asserted through a spy on `SessionTeardownService.purge` rather than by
   * populating four stores here: what belongs to this store is WHETHER IT DELEGATES, and what
   * the delegate then does is proven in `session-teardown.service.spec.ts`.
   */
  describe('session footprint', () => {
    it('purges the domain stores when signing out', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      store.logout().subscribe();

      // ⚠ NOT AWAITED, for the same reason as the identity case above: the purge must happen
      // when sign-out is ASKED FOR, not when the revocation round trip answers. On a slow or
      // failing network the gap between those two is unbounded, and for its whole duration
      // the previous account's records would still be readable from the domain stores.
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

      // A server that cannot revoke the renewal credential does not get to keep the previous
      // operator's data on this device. The local discard is unconditional precisely because
      // it is the one part of signing out that cannot fail.
      expect(purge)
        .withContext('an unreachable server does not leave the footprint behind')
        .toHaveBeenCalledTimes(1);
    });

    it('purges the domain stores when a session is replaced by a different account', async () => {
      await signIn();

      const purge = spyOn(sessionTeardown, 'purge').and.callThrough();

      // ⚠ THE ACCOUNT-REPLACEMENT PATH, which does not pass through `logout` at all. Signing
      // in while a session is held discards that session eagerly, so without this the previous
      // account's listings would remain behind the new account's screens.
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

      // Purging only on SUCCESS would leave the previous account's data in memory for the whole
      // duration of a failed attempt — which is exactly the case where the person at the
      // keyboard is LEAST likely to be the previous operator.
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

      // A successful renewal is the SAME session continuing. Purging here would discard the
      // listings the operator is looking at every time their token rotated, which is a
      // functional regression rather than a hardening.
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
      // ⚠ THE PROOF IS MEMBER PRESENCE, NOT AN ATTEMPTED WRITE. A writable signal carries
      // both a set and an update member; a read-only projection carries neither. Testing
      // presence is exact and needs no cast, whereas calling a writer through a cast
      // would only prove that the cast compiled.
      //
      // Every slice is private and exposed through a projection, so a consumer is
      // STRUCTURALLY unable to mutate this store — the only way state changes is a
      // command. That also means an update replaces a value rather than mutating it,
      // which is what lets a consumer using the on-push change-detection strategy observe
      // a change at all.
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

  // -------------------------------------------------------------------------
  // THE SESSION LIFETIME
  //
  // The store is the SINGLE owner of the session lifecycle, and these cases pin the properties
  // that ownership exists to provide. Each one was a defect before the responsibilities were
  // gathered here: a renewal could resurrect a session after a sign-out, a renewal left the
  // identity projection reporting the previous sign-in, and an unsubscribed command left the
  // phase reporting work that was no longer happening.
  //
  // ⚠ WHAT SUPERSESSION LOOKS LIKE HERE, AND WHY IT IS NOT A REJECTION. A superseded renewal
  // COMPLETES for its own subscriber and WRITES NOTHING SHARED. The credential store is the
  // owner of the session generation, and every write in this file is conditioned on the
  // generation the command captured still being current; a command that loses that test simply
  // has no effect. That is the guarantee the resurrection defect needed - the rotated pair must
  // not be adopted - and it is asserted directly below, on the stored credential and on the
  // published identity, rather than on the shape of the caller's notification. Asserting a
  // rejection instead would pin a mechanism rather than the property, and would say nothing
  // about whether anything was stored.
  // -------------------------------------------------------------------------
  describe('session lifetime', () => {
    it('adopts nothing from a renewal that completes after the session has been signed out', async () => {
      // ⚠ THE RESURRECTION CASE. Signing out advances the session generation, so a renewal
      // already past its own refusal check cannot store the rotated pair it is carrying.
      //
      // MIGRATION: the renewal used to be held by the authentication service, and signing out
      //   merely nulled that reference. Nulling a reference does not cancel the shared source
      //   behind it, so the renewal went on to complete and STORED a rotated session after the
      //   operator had signed out - leaving them signed in against their explicit instruction,
      //   with credentials the server had just issued.
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

      // Only now does the server answer the renewal, with a perfectly valid rotated pair, and
      // the identity read the renewal chains is answered too - so nothing is left outstanding
      // and the assertions below describe a settled store rather than a mid-flight one.
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
      // The same guarantee reached the other way: `endSession` is what the authentication
      // interceptor calls on a terminal refusal, and it must supersede an outstanding renewal
      // exactly as a sign-out does.
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
      // A sign-out the operator asked for is not a failure, so a renewal superseded BY that
      // sign-out must not leave a banner behind explaining something nobody did wrong.
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
      // The ladder is what reveals the verification field and words the prompt. A sign-in that
      // was superseded says nothing about the operator's credentials, so it must not move it.
      const first = firstValueFrom(store.login(credentials()));
      const firstRequest = httpMock.expectOne(LOGIN_URL);

      // A second sign-in begins, which is the supersession.
      const second = firstValueFrom(store.login(credentials()));
      const secondRequest = httpMock.expectOne(LOGIN_URL);

      // The superseded attempt is refused only afterwards.
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
      // The sign-in screen the operator is about to be sent to reads the recorded problem in
      // order to explain why they are back at it, so ending a session must not discard it.
      // `reset` is the operation that clears both.
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

  // -------------------------------------------------------------------------
  // COALESCING AND THE SHARED RENEWAL SLOT
  //
  // MIGRATION: this whole group arrived with a responsibility rather than being written
  //   fresh. The slot used to live on `core/services/auth.service.ts`, alongside custody of
  //   the session it renewed, and these properties were specified beside it there. Minimal
  //   Change Clause item 5 confines a service to API communication, so the slot moved to the
  //   session's owner and its specification moved with it.
  //
  // WHY A SLOT EXISTS AT ALL. The renewal credential ROTATES ON USE. Six list requests
  // expiring together would each present the same credential; the first rotates it and the
  // other five present one that has already been spent, which the server treats as a replay
  // and answers by revoking the account's whole credential family — signing the operator out
  // precisely because the client tried to keep them signed in.
  //
  // TWO ENTRY POINTS, AND THE DIFFERENCE IS BOOKKEEPING. `renewSession` is the primitive: it
  // coalesces, commits once, and touches nothing else. `refreshSession` wraps it and adds the
  // phase claim, the failure record and the discard, which is what a caller that ASKED to
  // renew needs and what the refused-request path must not have.
  // -------------------------------------------------------------------------
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

      // ⚠ THE SETTLED OBSERVABLE MUST NOT BE REPLAYED. `shareReplay` with no reference
      // counting keeps a buffered value indefinitely, so a slot that was never released would
      // hand every future caller the SAME rotated pair — a credential the server has already
      // spent — for the rest of the application's life.
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

      // Nothing is held now, so a later renewal has no credential to present — which is
      // itself the proof that the slot was released rather than replayed: a held slot would
      // have answered from its buffer instead of failing on the missing credential.
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

      // The abandoned renewal still arrives, because the shared source stays subscribed. Its
      // commit is epoch-suppressed, and the case below the group proves that; what THIS case
      // proves is that the slot no longer holds it.
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
      // Posting an empty credential would be answered 400, which a caller could not tell
      // apart from a genuine rejection of a real one. The failure is produced LAZILY inside
      // the returned observable, because the refused-request path reaches this inside a
      // `catchError` and must be able to rely on getting an observable back.
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
      // ⚠ THE SHARPEST CASE IN THIS GROUP, and the reason the primitive exists separately
      // from the deliberate command: the command discards unconditionally, which here would
      // sign out an operator whose own sign-in had just succeeded.
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
      // The bookkeeping difference between the two entry points, asserted rather than
      // described. A failure record here would put a stale message in front of an operator
      // who never asked to renew, and a phase claim would make an unrelated screen report
      // itself busy.
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

  // -------------------------------------------------------------------------
  // REVOCATION REPORTING
  //
  // MIGRATION: the sign-out POLICY moved here from `core/services/auth.service.ts`, and it
  //   arrived corrected. That service ended its revocation with a handler that swallowed the
  //   failure and completed successfully, so a 400, a 429, a 503 or a dropped connection was
  //   indistinguishable from a clean withdrawal — a renewal credential still live on the
  //   server for its full lifetime, reported as ended. Absorbing the failure was never the
  //   defect: a person who asked to sign out must end up signed out, and erroring here would
  //   leave them looking at a screen that behaves as though they had not. CLAIMING SUCCESS was
  //   the defect. The transport now propagates the refusal and this store absorbs it
  //   DELIBERATELY, recording it and saying so.
  // -------------------------------------------------------------------------
  // =========================================================================
  // WHO REPORTS A REFUSED RENEWAL
  //
  // `core/services/auth.service.ts` marks every credential request as reported by its caller, which
  // stopped the global announcer in `core/interceptors/error.interceptor.ts` reporting a refused
  // sign-in twice and a rate-limited one three times. A renewal, though, has NO SCREEN: it happens
  // behind whatever the operator is doing, so nothing binds its outcome and moving ownership without
  // giving it an owner would have turned a duplicate report into no report at all. This store is
  // that owner, and these cases are what stop the silence coming back.
  // =========================================================================

  describe('renewal reporting', () => {
    it('announces a renewal refused for a reason other than authority, once, in shared wording', async () => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      for (const status of [429, 500, 503]) {
        notify.calls.reset();

        await signIn();

        const renewal = firstValueFrom(store.refreshSession());
        httpMock
          .expectOne(REFRESH_URL)
          .flush(codelessRefusal(status), { status, statusText: 'Refused' });

        await expectAsync(renewal).toBeRejected();

        expect(notify.calls.count())
          .withContext(`a ${status} is announced exactly once, by this store and by nobody else`)
          .toBe(1);
        expect(String(notify.calls.mostRecent().args[1]).length)
          .withContext('and it says something rather than announcing an empty sentence')
          .toBeGreaterThan(0);

        store.reset();
      }
    });

    it('stays silent when authority itself is refused, because arriving at sign-in is the report', async () => {
      // ⚠ THE ONE STATUS THIS OWNER MUST NOT SPEAK ON, and it was silent before this store took
      // ownership too: the global announcer returns early on it, on the documented grounds that
      // `core/interceptors/auth.interceptor.ts` owns the lifecycle of a refused credential. What
      // that owner does is end the session and send the operator to the sign-in screen. A queued
      // sentence would arrive alongside the navigation, and the teardown clears the queue anyway.
      const notify = spyOn(notifications, 'notify').and.callThrough();

      await signIn();
      notify.calls.reset();

      const renewal = firstValueFrom(store.refreshSession());
      httpMock
        .expectOne(REFRESH_URL)
        .flush(codelessRefusal(401), { status: 401, statusText: 'Unauthorized' });

      await expectAsync(renewal).toBeRejected();

      expect(notify)
        .withContext('the terminal refusal is reported by navigation, not by a sentence')
        .not.toHaveBeenCalled();
      expect(store.isAuthenticated())
        .withContext('and the session is gone regardless')
        .toBeFalse();
    });

    it('announces an unreachable endpoint, which carries no status to be terminal by', async () => {
      const notify = spyOn(notifications, 'notify').and.callThrough();

      await signIn();
      notify.calls.reset();

      const renewal = firstValueFrom(store.refreshSession());
      httpMock
        .expectOne(REFRESH_URL)
        .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      await expectAsync(renewal).toBeRejected();

      expect(notify.calls.count())
        .withContext('a renewal that never reached the server is still reported')
        .toBe(1);
    });

    it('says nothing about a renewal belonging to a session that has already been replaced', async () => {
      // The epoch guard the announcement sits behind. A renewal begun before a sign-out arrives
      // afterwards and must neither clear the session that replaced it nor tell the operator that
      // something failed: from where they are standing, nothing did.
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
    });
  });

  describe('revocation reporting', () => {
    it('reports no outstanding revocation before anything has been signed out', () => {
      expect(store.revocationOutstanding()).toBeFalse();
    });

    it('completes the sign-out and reports the revocation as outstanding when it is refused', async () => {
      const warning = spyOn(notifications, 'warning').and.callThrough();

      for (const status of [400, 429, 503]) {
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
          .withContext('and the person is told, once, in words naming what they can do')
          .toHaveBeenCalledOnceWith(REVOCATION_FAILED_MESSAGE);

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
      // The failure record is read by the sign-in screen to explain why a caller is back at
      // it, and a failed WITHDRAWAL is not a reason they were signed out — they asked to be.
      // It is reported as its own boolean instead.
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

    // ---------------------------------------------------------------------
    // HOW LONG THE REPORT LIVES
    //
    // ⚠ THE CASES THAT EXIST BECAUSE THE REPORT USED TO OUTLIVE ITS SESSION. It was cleared
    // by exactly one thing — a later sign-out whose withdrawal SUCCEEDED — so after a single
    // refused withdrawal the sign-in screen carried the warning indefinitely: still there
    // after the next successful sign-in, still there when the next expiry returned somebody
    // to that screen, and still above the form for whoever typed in it next. A sentence about
    // a session two boundaries ago, presented as though it described theirs.
    //
    // The lifetime is now exactly ONE SESSION BOUNDARY: raised by a withdrawal that could not
    // be confirmed, and retired by the next boundary of any kind — a new attempt beginning, a
    // confirmed later withdrawal, or the store being reset. These cases pin both ends of that
    // interval, because a report retired too early says nothing when it should and one retired
    // too late says the wrong thing to the wrong person.
    // ---------------------------------------------------------------------

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

      // ⚠ ASSERTED WHILE THE CREDENTIAL EXCHANGE IS STILL IN FLIGHT. Clearing on success
      // would leave the previous session's warning standing above the form for the whole
      // duration of the attempt — which is precisely when somebody is reading that form.
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
      // The other half of clearing at the start: a refused attempt must not inherit the
      // warning either. The person at the keyboard has already been shown it once, and
      // re-presenting it beside a rejected sign-in reads as an explanation of the rejection.
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
      // ⚠ THE ORDERING CASE, and the reason clearing on every boundary is safe at all.
      // Sign-out discards the session — which retires this report — BEFORE it posts the
      // withdrawal, so a naive reading suggests the clear could race ahead of the report it is
      // meant to raise. It cannot, because the flag is set in that same request's failure
      // handler, strictly after the discard. Asserted across the boundary rather than argued:
      // false while the withdrawal is unanswered, true once it is refused.
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

  // -------------------------------------------------------------------------
  // CONTRACT ENFORCEMENT AT THE COMPOSITION POINT
  //
  // MIGRATION: both decoders existed and NEITHER HAD A PRODUCTION CONSUMER. The transport
  //   asserted its own response types through the type argument, which is a promise the
  //   compiler makes on the server's behalf and cannot keep. The values concerned become a
  //   bearer header, the shell's caption, and the role and permission lists a screen uses to
  //   decide which actions to offer — and the response crosses a reverse proxy, so
  //   same-origin is not the same as in-process. The transport decodes now, and these cases
  //   prove the refusal reaches the composition point and leaves nothing half-established.
  // -------------------------------------------------------------------------
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
      // The SECOND request of the sign-in, and the more dangerous of the two to leave
      // unchecked: a role list arriving as null would fault the first membership test, and
      // the session would already have been half-composed.
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
      expect(store.hasFailure()).toBeTrue();
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
      // The counterpart to the refusals above. `Users.DisplayName` is NOT NULL defaulting to
      // the empty string, so the empty-string encoding of absence is a schema constraint here
      // rather than a data-layer convention, and a stricter rule would refuse real accounts.
      await signIn(credentialPayload({ user: currentUser({ displayName: '' }) }));

      expect(store.currentUser()?.displayName).toBe('');
      expect(store.isAuthenticated()).toBeTrue();
    });
  });
});
