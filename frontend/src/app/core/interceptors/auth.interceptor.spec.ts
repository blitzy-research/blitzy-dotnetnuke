import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';

// Type-only, and deliberately so. `HttpErrorResponse` above is a VALUE import because the narrowing helper
// below performs an `instanceof` test against the concrete class, whereas these three are erased at compile
// time and must emit no runtime import. The project compiles with isolated modules, under which the
// distinction has to be stated rather than inferred.
import type { HttpInterceptorFn } from '@angular/common/http';
import type { TestRequest } from '@angular/common/http/testing';
import type { AuthSession, CurrentUser, LoginResponse } from '../models/auth.model';

import { ModuleStore } from '../state/module.store';
import { RoleStore } from '../state/role.store';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';
import { SESSION_ENDED_MESSAGE, SessionTeardownService } from '../state/session-teardown.service';
import { authInterceptor } from './auth.interceptor';
import { correlationIdInterceptor } from './correlation-id.interceptor';

/**
 * Specification for `authInterceptor` — the interceptor that presents the stored bearer token and owns the
 * whole 401 recovery lifecycle. Nine behaviours are load-bearing and each is asserted here:
 *
 * 1. The bearer token is attached to an API request when a session is held.
 * 2. Nothing is attached when no session is held, and the header is ABSENT rather than present-and-empty.
 * 3. The three anonymous credential endpoints — sign in, renew, sign out — receive no token, while the
 *    identity endpoint, which requires one, does.
 * 4. The host-root health probes are left entirely alone, including when one answers 401.
 * 5. A 401 produces EXACTLY ONE renewal and EXACTLY ONE retry, and the retry presents the ROTATED token.
 * 6. A 401 on the retry does not recurse, and a 401 from the renewal call itself does not provoke a second
 *    renewal.
 * 7. Several requests expiring together share ONE renewal rather than one each.
 * 8. The retry carries the SAME correlation identifier as the first attempt, so the two are joinable as one
 *    logical operation in the server's logs.
 * 9. A terminal renewal failure discards the session, routes to sign-in, and re-throws the ORIGINAL 401.
 *
 * `withInterceptors([A, B, C])` composes as `A(next = B(next = C(next = backend)))`, so the application's
 * fixed array `[correlationIdInterceptor, authInterceptor, errorInterceptor]` places error translation
 * INNERMOST on the response path: it observes the raw 401 before this interceptor, which surrounds it, has
 * had any chance to renew and retry. The array is not rearranged to make that tidier, because stamping the
 * correlation identifier outermost is exactly what makes a retry identifiable as a retry of the same
 * operation. Ownership is split instead — this interceptor owns detection, the single renewal, the single
 * retry and discarding the session, and `error.interceptor.ts` says nothing at all about a 401.
 *
 * One recovered request produces FOUR exchanges, because `AuthStore.renewSession()` reads the caller's
 * identity inside the same observable before it commits anything: the original request (401), the renewal
 * POST, a hand-authenticated identity GET, then the retry. A case that flushes only the renewal leaves the
 * identity read outstanding and fails at `verify()`, so `completeRenewal` performs both. The hand-set header
 * on that identity read is also what keeps a refused identity read from provoking another renewal.
 *
 * The interceptor holds no state — no in-flight slot, no attempt counter, no marker header. Coalescing
 * concurrent renewals belongs to `core/state/auth.store.ts` and must have exactly one owner: signing out
 * abandons that owner's slot AND advances its session generation, so a renewal already in flight fails
 * instead of storing. A private second slot here could not be advanced or cleared, so a 401 racing a
 * sign-out would replay a cached renewal and resurrect the session the operator had just ended.
 *
 * The one-retry bound is structural rather than counted: the renewal handler is attached BEFORE the retry
 * and the handler the retry carries is closed, so a second refusal has nothing to re-enter. That handler is
 * status-specific, because a 403, 404, 409, 422, 429, 500 or dropped connection says nothing about the
 * credential and ending a session for any of those would destroy a valid one.
 *
 * URLs are written as literal root-relative `/api/v1` paths rather than composed from the endpoint
 * catalogue: an expectation built from the module the subject builds from would agree tautologically, so a
 * change of prefix would pass unnoticed. The root-relative form is itself a deployment requirement — the
 * reverse proxy forwards `/api/` on the same origin, so an absolute base would resolve only inside the
 * container network or would turn every call cross-origin. The success envelope and the header names are
 * re-spelled for the same reason. Every token value is transparently fake, and no case writes a token, a
 * password or a request body to any output sink.
 */

/**
 * The request header the subject writes, spelled independently of the implementation.
 *
 * The subject deliberately does not export its constant, so re-spelling it here means a rename on either
 * side fails these expectations rather than passing silently.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * The request-correlation header, likewise re-spelled rather than imported.
 *
 * Read on both attempts of a recovered request to prove they carry ONE identifier.
 */
const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/**
 * An arbitrary protected resource, used wherever the route itself is not the subject.
 */
const PROTECTED_URL = '/api/v1/portals';

/**
 * A second protected resource, so simultaneous refusals can be told apart.
 */
const OTHER_PROTECTED_URL = '/api/v1/users';

/**
 * The renewal endpoint. Anonymous, and excluded from the bearer header by the subject.
 */
const REFRESH_URL = '/api/v1/auth/refresh';

/**
 * The sign-in endpoint. Anonymous: it authenticates the payload it carries.
 */
const LOGIN_URL = '/api/v1/auth/login';

/**
 * The sign-out endpoint. Anonymous, and answers 204 unconditionally.
 */
const LOGOUT_URL = '/api/v1/auth/logout';

/**
 * The identity endpoint.
 *
 * Requires a bearer token and is deliberately NOT excluded, which is what the skip-list cases below guard
 * against being "simplified" into a blanket prefix match.
 */
const IDENTITY_URL = '/api/v1/auth/me';

/**
 * Where the subject sends an operator whose session cannot be renewed.
 */
const LOGIN_ROUTE = '/login';

/**
 * The access token seeded before a case runs.
 *
 * Transparently fake. It is not a token of any real shape, carries no encoded payload, and could not be
 * mistaken for a credential by a scanner or a reader.
 */
const FAKE_ACCESS_TOKEN = 'fake-access-token';

/**
 * The rotated access token a successful renewal hands back.
 */
const FAKE_ROTATED_ACCESS_TOKEN = 'fake-access-token-2';

/**
 * The renewal credential seeded before a case runs.
 */
const FAKE_REFRESH_TOKEN = 'fake-refresh-token';

/**
 * The rotated renewal credential a successful renewal hands back.
 */
const FAKE_ROTATED_REFRESH_TOKEN = 'fake-refresh-token-2';

/**
 * A token a caller presented by hand, so the do-not-overwrite rule can be observed.
 */
const CALLER_SUPPLIED_TOKEN = 'fake-caller-supplied-token';

/**
 * An expiry comfortably in the future, used by every case except the one below it.
 *
 * The single expiry representation the sign-in contract publishes is an absolute instant in Coordinated
 * Universal Time, serialised as a string — not a relative seconds-remaining number — so the fixtures mirror
 * that and nothing here computes a duration.
 */
const FUTURE_EXPIRY = '2999-12-31T23:59:59.000Z';

/**
 * An expiry comfortably in the past.
 *
 * Used by exactly one case, which proves that a stale stamp changes nothing: the subject consults no clock
 * and no stored expiry, so the token is still presented and the server still gets to decide.
 */
const PAST_EXPIRY = '2001-01-01T00:00:00.000Z';

/**
 * The API's success envelope, re-declared rather than imported.
 *
 * Spelled here for the same reason the URLs are: an expectation that borrowed the production shape would
 * agree with it whatever it became. The metadata companion is present and null rather than omitted, matching
 * the wire — the API writes every declared member, so a response with no page to describe carries the member
 * with a null value.
 *
 * A mis-shaped body cannot pass silently. The subject reads the rotated access token out of this envelope
 * and presents it on the retry, so a body the service cannot unwrap produces a retry with the wrong header
 * and the case fails on that expectation.
 */
interface SuccessEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

/**
 * The identity carried inside a renewal response and returned by the identity endpoint.
 *
 * Minimal on purpose: no case below reads a member of it, and a credential-shaped member must never appear
 * on this contract.
 */
const FAKE_USER: CurrentUser = Object.freeze({
  userId: 7,
  portalId: 0,
  portalName: 'Primary',
  username: 'operator',
  displayName: 'Operator',
  email: 'operator@example.test',
  isSuperUser: false,
  isPortalAdministrator: false,
  roles: Object.freeze([]),
  permissions: Object.freeze([]),
});

/**
 * Builds a session to seed the store with.
 *
 * Written through the store's real public surface rather than by stubbing a reader, so the cases stay honest
 * about the shape the application actually holds. Every member the contract declares is supplied, because
 * the API serialises with its ignore condition set to never and therefore transmits a `false` rather than
 * omitting it.
 *
 * @param accessToken The token to present on an API request.
 * @param refreshToken The renewal credential, or the empty string for "none held".
 * @param expiresAtUtc When the access token lapses, as an absolute instant.
 * @returns The session to hand to the store.
 */
function sessionFor(
  accessToken: string,
  refreshToken: string,
  expiresAtUtc: string = FUTURE_EXPIRY,
): AuthSession {
  return {
    accessToken,
    expiresAtUtc,
    refreshToken,
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: FAKE_USER,
  };
}

/**
 * Builds the body a successful renewal answers with.
 *
 * @param accessToken The rotated access token.
 * @param refreshToken The rotated renewal credential.
 * @returns The enveloped renewal response.
 */
function renewalBody(
  accessToken: string,
  refreshToken: string,
): SuccessEnvelope<LoginResponse> {
  return {
    data: {
      accessToken,
      expiresAtUtc: FUTURE_EXPIRY,
      refreshToken,
      mustChangePassword: false,
      passwordExpiring: false,
      mustUpdateProfile: false,
      user: FAKE_USER,
    },
    meta: null,
  };
}

/**
 * The two extension members this API attaches to every problem document.
 *
 * `ValidationProblemDetailsFactory` writes both on every refusal. They are not interchangeable: the
 * correlation identifier is the value the pipeline validated for this request and the one that appears in
 * the server's log and on the audit trail.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * Builds a problem document of the shape the API ACTUALLY emits for a failure.
 *
 * THE PROBLEM TYPE IS `urn:dnnmigration:error:<code>`, PRODUCED BY EXACTLY ONE SERVER METHOD —
 * `ApiResults.BuildProblemType`. It does, and it also describes a body this API never sends: every refusal
 * it produces carries its own URN, and the `about:blank` value appears nowhere in the server tree. The
 * consequence is not cosmetic — the single-flight behaviour asserted below exists to serve the error
 * interceptor and the auth store, both of which branch on the type, so a fixture without one exercised the
 * renewal machinery against a refusal no endpoint can produce.
 *
 * And the title and detail are the server's own, not composed here. The title comes from the per-status
 * vocabulary in `ValidationProblemDetailsFactory` and the detail from the producing service, so a case that
 * flushes a refusal flushes what a caller would really receive. `errors` is omitted rather than empty: only
 * the model binder attaches a per-field map, and it never attaches an empty one.
 *
 * @param status The status the server's mapping yields for the code.
 * @param title The per-status title from the server's own vocabulary.
 * @param code The failure code, spelled exactly as the server publishes it.
 * @param detail The authored sentence the producing service placed on the outcome.
 * @returns The problem document to flush.
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
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * The server's OWN title, code and sentence for each status these cases flush.
 *
 * TRANSCRIBED FROM `ValidationProblemDetailsFactory.StatusVocabulary`, NOT COMPOSED HERE. That dictionary is
 * the one place the API decides what a bare refusal of a given status looks like, and every entry below is
 * its triple for that status verbatim.
 *
 * Held as one table rather than repeated at each call site so a status cannot acquire two different
 * vocabularies in one file, which is the drift this whole fixture exists to prevent.
 */
const STATUS_VOCABULARY: Readonly<
  Record<number, { readonly title: string; readonly code: string; readonly detail: string }>
> = Object.freeze({
  403: {
    title: 'Forbidden',
    code: 'auth.not_permitted',
    detail: 'The authenticated caller is not permitted to perform this operation.',
  },
  404: {
    title: 'Not Found',
    code: 'resource.not_found',
    detail: 'The requested resource does not exist.',
  },
  409: {
    title: 'Conflict',
    code: 'resource.conflict',
    detail: 'The request conflicts with the current state of the resource.',
  },
  422: {
    title: 'Unprocessable Content',
    code: 'request.unprocessable',
    detail: 'The request was understood but could not be processed.',
  },
  429: {
    title: 'Too Many Requests',
    code: 'request.rate_limited',
    detail: 'Too many requests have been submitted. Retry after a short delay.',
  },
  500: {
    title: 'Internal Server Error',
    code: 'server.unexpected_failure',
    detail: 'An unexpected error occurred while processing the request.',
  },
});

/**
 * Builds the bare refusal the API emits for a status, from that status's own vocabulary.
 *
 * @param status The status to build a refusal for; it must appear in {@link STATUS_VOCABULARY}.
 * @returns The problem document a caller would really receive.
 */
function bareProblem(status: number): Record<string, unknown> {
  const entry = STATUS_VOCABULARY[status];

  if (entry === undefined) {
    throw new Error(`no server vocabulary is recorded for status ${String(status)}`);
  }

  return problemDocument(status, entry.title, entry.code, entry.detail);
}

/**
 * The status line the platform writes for a status, from that same vocabulary.
 *
 * @param status The status to build a status line for.
 * @returns The `status`/`statusText` pair to flush alongside the document.
 */
function bareProblemInit(status: number): { status: number; statusText: string } {
  const entry = STATUS_VOCABULARY[status];

  if (entry === undefined) {
    throw new Error(`no server vocabulary is recorded for status ${String(status)}`);
  }

  return { status, statusText: entry.title };
}

/**
 * The status line for a refusal, reused by every case that flushes one.
 */
const UNAUTHORIZED_INIT = Object.freeze({ status: 401, statusText: 'Unauthorized' });

/**
 * The refusal a protected endpoint answers with when the presented token is not accepted.
 *
 * `auth.unauthenticated` IS THE RIGHT CODE HERE, and choosing it is the whole point of making this fixture
 * live. This is the 401 produced with no explicit failure code — the bearer scheme's own challenge — so it
 * carries the status vocabulary's default type and sentence. It is exactly what a request whose access token
 * has lapsed earns, which is the condition every single-flight case below is about.
 */
const UNAUTHENTICATED_PROBLEM = problemDocument(
  401,
  'Unauthorized',
  'auth.unauthenticated',
  'Authentication is required to reach this resource.',
);

/**
 * The refusal the renewal endpoint answers with when the presented credential is unusable.
 *
 * One code covers unknown, consumed and lapsed alike, so a caller cannot use the refusal to learn which of
 * the three it was.
 */
const INVALID_REFRESH_TOKEN_PROBLEM = problemDocument(
  401,
  'Unauthorized',
  'auth.invalid_refresh_token',
  'The refresh token is not valid.',
);

/**
 * A page of portals, as `GET /api/v1/portals` really answers.
 *
 * The success body matters even where the assertion is about a header. Neither is a shape it can produce:
 * the listing is the one endpoint whose body IS the page envelope, so it always answers `{ items, meta }`.
 * Using a realistic body is what lets the "the caller receives the success, not the refusal that preceded
 * it" assertion mean something: comparing against `{ ok: true }` proved only that an invented object
 * survived a round trip, whereas comparing against the real envelope proves the REPLAYED response is the one
 * the endpoint would have returned had the token never lapsed.
 *
 * The tenant is `-1`, which is the first portal an installation has rather than a marker for "no portal":
 * `01.00.00.SqlDataProvider` declares `[PortalID] [int] IDENTITY (-1, 1)`.
 */
const PORTAL_PAGE_BODY = Object.freeze({
  items: [
    {
      portalId: -1,
      portalName: 'Baseline Portal',
      aliases: ['localhost'],
      users: 3,
      pages: 7,
      hostSpace: 0,
    },
  ],
  meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
});

/**
 * A page of accounts, as `GET /api/v1/users` really answers.
 *
 * Present for the same reason as the portal page: the other protected endpoint these cases exercise is also
 * a paged listing, and driving it with a body it cannot produce would make the concurrency assertions rest
 * on a fiction.
 */
const USER_PAGE_BODY = Object.freeze({
  items: [
    {
      userId: 0,
      portalId: -1,
      username: 'operator',
      firstName: 'Ada',
      lastName: 'Lovelace',
      displayName: 'Ada Lovelace',
      address: null,
      telephone: null,
      email: 'operator@example.invalid',
      createdDate: '2024-01-01T00:00:00.000Z',
      lastLoginDate: null,
      isApproved: true,
      isOnline: false,
      isSuperUser: false,
      isLockedOut: false,
    },
  ],
  meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
});

/**
 * Answers an outstanding request with the 401 a protected endpoint really produces.
 *
 * @param request The outstanding request to refuse.
 */
function refuse(request: TestRequest): void {
  request.flush(UNAUTHENTICATED_PROBLEM, UNAUTHORIZED_INIT);
}

/**
 * Answers an outstanding renewal with the 401 the renewal endpoint really produces.
 *
 * Kept distinct from {@link refuse} because the two refusals are different facts: one says the access token
 * was not accepted and is recoverable by renewal, the other says the renewal credential itself is gone and
 * the session is over. Flushing the same body for both would blur exactly the distinction the single-flight
 * machinery turns on.
 *
 * @param request The outstanding renewal request to refuse.
 */
function refuseRenewal(request: TestRequest): void {
  request.flush(INVALID_REFRESH_TOKEN_PROBLEM, UNAUTHORIZED_INIT);
}

/**
 * Reads the HTTP status off a caught value, or null when it is not an HTTP response.
 *
 * The caught value is typed `unknown` and narrowed by an `instanceof` test rather than asserted into a
 * shape, because a value reaching a subscriber's error path is not necessarily an HTTP response at all — an
 * operator can throw anything — and reading a member off such a value would yield undefined and compare
 * unequal without explanation.
 *
 * @param error The caught value.
 * @returns The status, or null when the value is not an HTTP response.
 */
function httpStatusOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/**
 * Awaits a pending call and returns the reason it failed, or null when it succeeded.
 *
 * Returning the reason rather than asserting inside a callback keeps every expectation in the body of the
 * case, where a failure is attributed to the case that caused it.
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

/**
 * The options every ejection to the sign-in screen must carry.
 *
 * ⚠ DECLARED ONCE, AND FOR THE SAME REASON THE PRODUCTION CODE NOW IMPORTS THE PARAMETER NAME
 * FROM ONE PLACE. Seven cases in this file assert this navigation, and until this constant existed
 * each spelled the expectation itself — so seven copies had to agree that a bare `['/login']` was
 * correct, and they did agree, which is exactly how they came to guard the defect together. The
 * ejection used to carry no options at all: no destination, so signing in again returned the
 * operator to the default landing screen rather than to the work they were interrupted in, and no
 * replacement, so the abandoned screen stayed in history as an entry that could never be restored.
 *
 * `returnUrl` is `'/'` in this harness because no navigation is performed in it, so that is
 * genuinely the address the refused request was issued from.
 */
const SIGN_IN_EJECTION_OPTIONS = {
  replaceUrl: true,
  queryParams: { returnUrl: '/' },
} as const;

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let navigate: jasmine.Spy<Router['navigate']>;

  /**
   * Asserts the operator was ejected to the sign-in screen exactly once, on the full contract.
   *
   * Written as a helper rather than repeated so that the contract has ONE definition in this file.
   * A case that wants to assert something different about the navigation still asserts it inline;
   * this covers only the plain ejection, which is what most of the terminal cases are about.
   */
  function expectEjectedToSignIn(): void {
    expect(navigate)
      .withContext('the operator is ejected to sign-in, told where they were, and not pushed')
      .toHaveBeenCalledOnceWith([LOGIN_ROUTE], SIGN_IN_EJECTION_OPTIONS);
  }

  /**
   * Builds the injector for a case, with the chain the case is about.
   *
   * Ordering inside the provider array is load-bearing. `provideHttpClient` is declared BEFORE
   * `provideHttpClientTesting`, because the testing provider replaces the backend the former installed;
   * declared the other way round, the real backend wins and the requests leave the browser. The route table
   * is empty on purpose — the navigation itself is stubbed below — and the router is provided through its
   * own function rather than a testing module, which is the arrangement a standalone application uses.
   *
   * Every collaborator the subject needs is resolved from this injector rather than substituted: the session
   * store, the authentication service and the router are all the real implementations, so these cases
   * exercise the real wiring. The authentication service is therefore not imported by this file at all — it
   * reaches the subject through injection, which is precisely the relationship under test.
   *
   * A fresh injector per case is also what makes the single-flight guarantee observable without any reset
   * step: the service that holds the in-flight renewal is root-provided, so each case starts with its own
   * instance and no case can inherit another's slot.
   *
   * @param interceptors The chain to install, outermost first.
   */
  function configureWith(interceptors: HttpInterceptorFn[]): void {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors(interceptors)),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);

    // Stubbed rather than exercised, and resolved rather than left pending. A real navigation settles over
    // several microtasks that the subject deliberately does not await — it is re-throwing the server's own
    // response and must not wait on routing — so asserting the call is deterministic where asserting the
    // resulting URL would be a race. The sign-in screen is also not authored yet, so a real navigation would
    // land on the catch-all route and prove nothing about the destination.
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  }

  /**
   * Completes a renewal by answering BOTH requests it issues, in order.
   *
   * The renewal is posted first and the identity is read second, with the rotated token presented by hand —
   * see the file header. Answering only the first leaves the second outstanding and fails `verify()`, so
   * both live in one helper and no case can answer half of it.
   *
   * The hand-set header on the identity read is asserted here rather than in a case of its own, because it
   * is what stops the subject from wrapping that request for recovery and so is what makes the renewal flow
   * non-recursive.
   *
   * @param rotatedAccessToken The access token the renewal hands back.
   * @param rotatedRefreshToken The renewal credential the renewal hands back.
   */
  function completeRenewal(
    rotatedAccessToken: string = FAKE_ROTATED_ACCESS_TOKEN,
    rotatedRefreshToken: string = FAKE_ROTATED_REFRESH_TOKEN,
  ): void {
    const renewal = httpMock.expectOne(REFRESH_URL);
    expect(renewal.request.method).toBe('POST');
    renewal.flush(renewalBody(rotatedAccessToken, rotatedRefreshToken));

    const identity = httpMock.expectOne(IDENTITY_URL);
    expect(identity.request.method).toBe('GET');
    expect(identity.request.headers.get(AUTHORIZATION_HEADER))
      .withContext('the identity read presents the freshly issued token by hand')
      .toBe(`Bearer ${rotatedAccessToken}`);
    identity.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);
  }

  // MANDATORY, and it is doing real work rather than tidying up. `verify()` fails the case when any request
  // was issued that no expectation accounted for, which is the mechanism by which "exactly one renewal" and
  // "exactly one retry" are proved: a second renewal or a third attempt has nothing to answer it and is
  // reported here. Declared once at the top level so that it runs after every case in every group below.
  afterEach(() => {
    httpMock.verify();
  });

  describe('presenting the bearer token', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
    });

    it('attaches the stored token to an API request', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const request = httpMock.expectOne(PROTECTED_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the scheme is exactly "Bearer" and one space')
        .toBe(`Bearer ${FAKE_ACCESS_TOKEN}`);

      request.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('attaches NO header at all when no session is held', async () => {
      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const request = httpMock.expectOne(PROTECTED_URL);

      // Absence is asserted deliberately, and is stronger than checking the value. A present-but-empty
      // header, or the string "Bearer null", would satisfy a value comparison against something falsy while
      // still telling the server that a credential was offered and is malformed.
      expect(request.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('there is no token to attach, so no header is written')
        .toBeFalse();
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('and nothing resembling a token is present under any value')
        .toBeNull();

      request.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('does not overwrite an Authorization header the caller set explicitly', async () => {
      // Load-bearing rather than merely polite. The authentication service presents a freshly issued token
      // by hand while it is establishing an identity, at a point where the rotated pair is deliberately not
      // stored yet. Overwriting it would attach the PREVIOUS token, and on a renewal would treat the
      // resulting refusal as cause for another renewal.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(
        http.get(PROTECTED_URL, {
          headers: { [AUTHORIZATION_HEADER]: `Bearer ${CALLER_SUPPLIED_TOKEN}` },
        }),
      );

      const request = httpMock.expectOne(PROTECTED_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('a caller that set one had a reason')
        .toBe(`Bearer ${CALLER_SUPPLIED_TOKEN}`);

      request.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('still attaches the token to a same-origin absolute API URL', async () => {
      // The address test compares RESOLVED origins, so an API URL written in absolute form against this
      // document's own origin must keep working. Without this case the refusals below could all be satisfied
      // by a subject that refused everything.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const absolute = new URL(PROTECTED_URL, document.baseURI).href;

      const pending = firstValueFrom(http.get(absolute));

      const request = httpMock.expectOne(absolute);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ACCESS_TOKEN}`,
      );

      request.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('still attaches the token to an API URL carrying a query string', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const withQuery = `${PROTECTED_URL}?pageIndex=0&pageSize=10`;

      const pending = firstValueFrom(http.get(withQuery));

      const request = httpMock.expectOne(withQuery);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ACCESS_TOKEN}`,
      );

      request.flush(PORTAL_PAGE_BODY);
      await pending;
    });
  });

  describe('what is left untouched', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));
    });

    /**
     * Asserts that a URL receives no credential.
     *
     * @param url The URL to request.
     * @param why The reason the header must not be written, quoted on failure.
     */
    async function expectNoCredential(url: string, why: string): Promise<void> {
      const pending = firstValueFrom(http.get(url));

      const request = httpMock.expectOne(url);
      expect(request.request.headers.has(AUTHORIZATION_HEADER)).withContext(why).toBeFalse();

      // These addresses are deliberately NOT this API — that is the whole point of the case — so there is no
      // endpoint contract to honour and no realistic envelope to borrow. The response is answered as an
      // empty body, which is what a foreign host's own reply shape must not be presumed to be.
      request.flush(null);
      await pending;
    }

    it('leaves a request that is not addressed to the API alone', async () => {
      await expectNoCredential(
        '/assets/config.json',
        'a static asset is served by the proxy, not the API, and must not see the token',
      );
    });

    // Credential-exfiltration cases.
    //
    //  Each URL below would be accepted as an API request by a TEXTUAL prefix test and would therefore have
    //  received the bearer token. The first three resolve to a FOREIGN ORIGIN, so the token would be
    //  disclosed to whoever serves that host; the fourth is a same-origin path that merely shares a textual
    //  prefix with the configured base and is a different API version. Each expectation is expressed against
    //  the HEADER rather than against a predicate, because the header is the disclosure.

    const foreignUrls: readonly { readonly url: string; readonly why: string }[] = [
      {
        url: 'https://not-ours.example/api/v1/exfiltrate',
        why: 'an absolute foreign origin that merely contains the configured base path',
      },
      {
        url: '//not-ours.example/api/v1/exfiltrate',
        why: 'a protocol-relative URL that reads as a path but resolves to a foreign host',
      },
      {
        url: 'https://not-ours.example/x?next=/api/v1/users',
        why: 'a foreign origin carrying the configured base inside a query parameter',
      },
      {
        url: 'https://not-ours.example/api/v1/auth/login',
        why: 'a foreign origin is never one of our own anonymous endpoints, however spelled',
      },
    ];

    for (const { url, why } of foreignUrls) {
      it(`attaches nothing to ${url}`, async () => {
        await expectNoCredential(url, `${why} — the bearer token must never reach it`);
      });
    }

    it('attaches nothing to a path that only shares a textual prefix with the base', async () => {
      // A different API version, not a descendant of the configured base. A prefix test admits it; a
      // segment-bounded test does not.
      await expectNoCredential('/api/v10/users', 'a sibling version is outside the API base');
    });

    it('attaches nothing to a value that is not a URL in any recognised form', async () => {
      // Both address tests resolve the URL rather than comparing strings, and a value the platform cannot
      // resolve is therefore neither a probe nor an API address. It must fall through as "not ours" rather
      // than raising out of the subject, because a predicate that threw would replace the server's answer
      // with a type error.
      await expectNoCredential('http://', 'an unresolvable value is not addressed to the API');
    });

    // The three anonymous credential endpoints authenticate the payload they carry rather than a bearer
    // token. Excluding the renewal endpoint is also what makes recursion unreachable: that call is issued
    // through this same chain, so a refused renewal would otherwise be answered by another renewal.
    //
    // Sign-out is on this list and answers 204 unconditionally. It reuses the renewal request shape rather
    // than declaring one of its own, so the credential payload contracts number four in total: sign in, its
    // response, renew, and the identity projection.
    const anonymousUrls: readonly string[] = [LOGIN_URL, REFRESH_URL, LOGOUT_URL];

    for (const url of anonymousUrls) {
      it(`leaves the anonymous endpoint ${url} alone`, async () => {
        const pending = firstValueFrom(http.post(url, {}));

        const request = httpMock.expectOne(url);
        expect(request.request.headers.has(AUTHORIZATION_HEADER))
          .withContext(`${url} authenticates its own payload, not a bearer token`)
          .toBeFalse();

        // Answered with THIS endpoint's own success: the two credential exchanges answer 200 carrying a
        // token pair, and the revocation answers 204 with no body at all. Driving all three with one
        // invented body would have stated a contract none of them has.
        if (url === LOGOUT_URL) {
          request.flush(null, { status: 204, statusText: 'No Content' });
        } else {
          request.flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));
        }

        await pending;
      });
    }

    it('DOES attach the token to the identity endpoint, which requires one', async () => {
      // The guard against "simplifying" the exclusion into a blanket prefix match on the credential segment.
      // The identity endpoint requires a bearer token and is fully eligible for renew-and-retry, so
      // excluding it would make every expired identity read a hard failure instead of a recovered one.
      const pending = firstValueFrom(http.get(IDENTITY_URL));

      const request = httpMock.expectOne(IDENTITY_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the identity endpoint is authorised, not anonymous')
        .toBe(`Bearer ${FAKE_ACCESS_TOKEN}`);

      request.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);
      await pending;
    });

    // The health probes are published at the HOST ROOT, OUTSIDE the versioned API prefix, and are anonymous
    // and un-throttled by design: a container health check must reach them without a credential and without
    // spending a rate-limit budget. The compose file gates the front-end container on the first of them
    // reporting healthy, and the API image's own health directive probes it with a spider-mode fetch because
    // the Alpine runtime ships that tool rather than a transfer client. The trailing-slash and query-string
    // forms are included because a probe is matched on its RESOLVED path rather than on spelling.
    const probeUrls: readonly string[] = [
      '/health',
      '/health/',
      '/health?full=true',
      '/health/ready',
      '/health/live',
    ];

    for (const url of probeUrls) {
      it(`leaves the health probe ${url} alone`, async () => {
        const pending = firstValueFrom(http.get(url));

        const request = httpMock.expectOne(url);
        expect(request.request.headers.has(AUTHORIZATION_HEADER))
          .withContext('a health probe is anonymous and must never carry a credential')
          .toBeFalse();

        request.flush({ status: 'Healthy' });
        await pending;
      });
    }

    it('does not renew or retry when a health probe itself answers 401', async () => {
      // A probe is not wrapped for recovery at all, so a refusal from one is reported as it stands.
      // Recovering it would be wrong twice over: the probe carries no credential to renew, and a failing
      // probe is a signal the orchestrator is meant to act on rather than something the client should paper
      // over. Silently spending a renewal on it would also let an unhealthy container consume a rate-limit
      // budget.
      const pending = firstValueFrom(http.get('/health'));

      refuse(httpMock.expectOne('/health'));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the refusal reaches the caller unchanged')
        .toBe(401);

      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone('/health');
      expect(navigate)
        .withContext('a probe failure must not sign the operator out')
        .not.toHaveBeenCalled();
      expect(tokens.session())
        .withContext('and must not discard a perfectly good session')
        .not.toBeNull();
    });
  });

  describe('recovering from a 401', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
    });

    it('renews ONCE and retries ONCE, presenting the rotated token', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      // Typed as the listing's own paged envelope, so the "the caller receives the success" assertion below
      // compares against the shape the endpoint really returns rather than against an invented marker
      // object.
      const pending = firstValueFrom(http.get<typeof PORTAL_PAGE_BODY>(PROTECTED_URL));

      const first = httpMock.expectOne(PROTECTED_URL);
      expect(first.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the first attempt presents the stored token')
        .toBe(`Bearer ${FAKE_ACCESS_TOKEN}`);
      refuse(first);

      // The renewal presents the stored renewal credential and nothing else. Exactly one member: no user
      // name, no tenant, no caller address and no provider discriminator — the token IS the credential, so
      // the server derives the caller from it.
      const renewal = httpMock.expectOne(REFRESH_URL);
      expect(renewal.request.method).toBe('POST');
      expect(renewal.request.body)
        .withContext('the renewal request carries exactly one member')
        .toEqual({ refreshToken: FAKE_REFRESH_TOKEN });
      renewal.flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));

      const identity = httpMock.expectOne(IDENTITY_URL);
      expect(identity.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the identity read presents the freshly issued token by hand')
        .toBe(`Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`);
      identity.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);

      // The retry re-reads the token from the refreshed session rather than reusing the value captured
      // before the first attempt. Asserting the ROTATED value is what makes this case fail if the subject
      // were to replay the stale token.
      const retry = httpMock.expectOne(PROTECTED_URL);
      expect(retry.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the retry presents the refreshed token, not the captured one')
        .toBe(`Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`);
      expect(retry.request.method)
        .withContext('and is the same operation, not a different one')
        .toBe('GET');
      retry.flush(PORTAL_PAGE_BODY);

      expect(await pending)
        .withContext('the caller receives the success, not the refusal that preceded it')
        .toEqual(PORTAL_PAGE_BODY);

      // The rotated pair replaced the consumed one exactly once. Storing only the access token would leave
      // the consumed renewal credential in place, and presenting it again is treated by the server as a
      // replay and revokes the whole family.
      expect(tokens.accessToken()).toBe(FAKE_ROTATED_ACCESS_TOKEN);
      expect(tokens.refreshToken()).toBe(FAKE_ROTATED_REFRESH_TOKEN);
      expect(navigate)
        .withContext('a session renewed without the operator noticing must not move them')
        .not.toHaveBeenCalled();

      // `verify()` in the top-level teardown now proves there was no THIRD attempt at the protected resource
      // and no SECOND renewal.
    });

    it('does not recurse when the RETRY is refused as well', async () => {
      // The bound is structural rather than counted: the subject attaches its RECOVERY handler to the first
      // attempt only, and the handler on the retry is closed - it issues no request and asks for no renewal.
      // A persistently refusing server therefore cannot loop.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();
      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the second refusal reaches the caller')
        .toBe(401);

      // Exactly two attempts at the resource and exactly one renewal, in total. Both halves are asserted
      // explicitly here as well as through the teardown, because this is the case a regression would break
      // first.
      httpMock.expectNone(PROTECTED_URL);
      httpMock.expectNone(REFRESH_URL);
    });

    it('ends the session when the retry is refused with a 401', async () => {
      // The complement of the non-401 cases below, and the one this file used to GET wrong.
      //
      //  MIGRATION: the case above used to close with three assertions stating that the rotated session
      //  SURVIVED a refused retry - that `tokens.session()` was not null, that the rotated token was still
      //  held, and that no navigation had happened. Those assertions encoded a defect. A 401 answering a
      //  request that carried a token issued moments earlier means the server refuses a credential it has
      //  just minted; renewal is the only recovery this subject has and it has already been spent, so nothing
      //  can repair the session. Leaving it installed left the custodian reporting an authenticated session,
      //  the shell rendering the account, the route gates admitting navigations, and every later request
      //  presenting the same rejected credential - an operator stranded on a screen where nothing worked,
      //  with no way back to signing in but a manual reload.
      //
      //    The reasoning those assertions carried was not wholly wrong, and the cases that follow preserve
      //    the part that was: a retry failing for a reason unrelated to the credential must NOT end the
      //    session. The distinction is the STATUS, which is why the handler is status-specific rather than a
      //    catch-all.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);
      expect(retry.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the retry presented the freshly issued credential')
        .toBe(`Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`);
      refuse(retry);

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext("the retry's own refusal reaches the caller, not a swallowed one")
        .toBe(401);

      expect(tokens.session())
        .withContext('a credential the server refuses immediately after issuing it is not held')
        .toBeNull();
      expect(tokens.accessToken()).toBeNull();
      expect(navigate)
        .withContext('and the operator is asked to sign in rather than left on a dead screen')
        .toHaveBeenCalledWith([LOGIN_ROUTE], SIGN_IN_EJECTION_OPTIONS);

      // Still exactly two attempts and one renewal: ending the session is not another attempt.
      httpMock.expectNone(PROTECTED_URL);
      httpMock.expectNone(REFRESH_URL);
    });

    it('leaves the session intact for every retry status that is not a 401', async () => {
      // The discriminating matrix. A 403 means the server knows who the caller is and is refusing the
      // OPERATION; the rest are not authentication conditions at all. Each is driven through the same
      // recovered path so the handler's status test is pinned by behaviour rather than by reading it.
      for (const status of [403, 404, 422, 429, 500]) {
        tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

        const pending = firstValueFrom(http.get(PROTECTED_URL));

        refuse(httpMock.expectOne(PROTECTED_URL));
        completeRenewal();

        httpMock
          .expectOne(PROTECTED_URL)
          .flush(bareProblem(status), bareProblemInit(status));

        expect(httpStatusOf(await reasonFor(pending)))
          .withContext(`a ${status} reaches the caller as itself`)
          .toBe(status);

        expect(tokens.session())
          .withContext(`a ${status} on the retried request is not an authentication failure`)
          .not.toBeNull();
        expect(tokens.accessToken()).toBe(FAKE_ROTATED_ACCESS_TOKEN);
        expect(navigate)
          .withContext(`a ${status} must not sign the operator out`)
          .not.toHaveBeenCalled();
        httpMock.expectNone(REFRESH_URL);

        tokens.clear();
      }
    });

    it('propagates a NON-401 retry failure exactly as the server sent it', async () => {
      // The discriminating case for the operator ordering. With the renewal handler after the retry, this
      // 409 was caught as a renewal failure: the session was discarded, the operator was navigated away, and
      // the caller was told 401 — so a duplicate-name conflict was reported as an expired session and the
      // error interceptor never saw the status it needed in order to word it.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      httpMock
        .expectOne(PROTECTED_URL)
        .flush(bareProblem(409), bareProblemInit(409));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the caller hears the status the server actually sent')
        .toBe(409);

      expect(tokens.session())
        .withContext('a conflict on the retried request is not an authentication failure')
        .not.toBeNull();
      expect(tokens.accessToken())
        .withContext('and the renewed credential is still the one held')
        .toBe(FAKE_ROTATED_ACCESS_TOKEN);
      expect(navigate).not.toHaveBeenCalled();
      httpMock.expectNone(REFRESH_URL);
    });

    it('propagates a retry that fails with no response at all', async () => {
      // A transport failure carries status zero and no body. Reported as itself rather than as a 401, so the
      // error interceptor can say the server could not be reached instead of claiming the session expired.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      httpMock.expectOne(PROTECTED_URL).error(new ProgressEvent('error'));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('a network failure is not an authentication failure')
        .toBe(0);

      expect(tokens.session()).not.toBeNull();
      expect(navigate).not.toHaveBeenCalled();
    });

    it('does not answer a refused RENEWAL with another renewal', async () => {
      // Excluding the renewal endpoint from the bearer header is what makes this unreachable, because that
      // call re-enters this same chain through the HTTP client. Without the exclusion a refused renewal
      // would provoke a renewal of the renewal, and so on without bound. Asserted rather than trusted.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      refuseRenewal(httpMock.expectOne(REFRESH_URL));

      // The ORIGINAL refusal is reported, not the renewal's. Reporting the renewal failure would replace
      // "your request was not authorised" with an unrelated message about a token the caller never sent.
      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the caller hears about their own request')
        .toBe(401);

      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(PROTECTED_URL);

      // The terminal behaviour, asserted here as well because it is the same event.
      expect(tokens.session()).withContext('the session is discarded').toBeNull();
      expectEjectedToSignIn();
    });

    it('reports the ORIGINAL refusal even when the renewal fails for another reason', async () => {
      // The discriminating case for "which failure does the caller hear about". When the renewal is itself
      // refused with 401, re-throwing either failure produces the same observable status and the distinction
      // is invisible; giving the renewal a DIFFERENT status is what makes the choice testable. The caller
      // asked about their own request, so 401 is the answer they get — not a message about a credential they
      // never sent.
      //
      // The renewal failure is also deliberately never logged: the values in scope at that point include the
      // credential the server has just refused.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(
          problemDocument(
            403,
            'Forbidden',
            'auth.not_permitted',
            'The authenticated caller is not permitted to perform this operation.',
          ),
          { status: 403, statusText: 'Forbidden' },
        );

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the ORIGINAL 401 is reported, not the renewal 403')
        .toBe(401);

      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(PROTECTED_URL);
      expect(tokens.session())
        .withContext('a renewal credential the server refuses cannot be retried')
        .toBeNull();
      expectEjectedToSignIn();
    });

    it('does not recover the identity read that follows a renewal', async () => {
      // The second half of the renewal flow presents its token by hand, so the subject passes it through
      // untouched and it is not wrapped for recovery. A refusal there therefore fails the renewal as a whole
      // rather than starting a second one — which is the property that keeps the two-request flow
      // non-recursive.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      httpMock
        .expectOne(REFRESH_URL)
        .flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));
      refuse(httpMock.expectOne(IDENTITY_URL));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the original refusal is still what the caller hears about')
        .toBe(401);

      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(IDENTITY_URL);
      httpMock.expectNone(PROTECTED_URL);
      expect(tokens.session())
        .withContext('a half-established session is never left behind')
        .toBeNull();
      expectEjectedToSignIn();
    });

    it('issues ONE renewal for several requests refused together', async () => {
      // The storm guard. Without a single-flight owner, six parallel list requests expiring together would
      // each present the same renewal credential; the first would rotate it and the remaining five would
      // present a consumed token, which the server treats as a replay and answers by revoking the account
      // entire credential family — signing the person out precisely because the client tried to keep them
      // signed in.
      //
      // The guarantee is observed here through behaviour alone. Nothing in this file names, reaches for or
      // resets the mechanism that provides it, and the mechanism holds no event stream of its own: it is a
      // single shared in-flight observable that clears its slot on completion, error and unsubscription
      // alike.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const first = firstValueFrom(http.get(PROTECTED_URL));
      const second = firstValueFrom(http.get(OTHER_PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      refuse(httpMock.expectOne(OTHER_PROTECTED_URL));

      // Matched rather than expected-one so the count itself is the assertion, and iterated rather than
      // indexed so no element access has to be asserted away.
      const renewals = httpMock.match(REFRESH_URL);
      expect(renewals.length)
        .withContext('a second renewal would present a consumed credential')
        .toBe(1);

      for (const renewal of renewals) {
        expect(renewal.request.body).toEqual({ refreshToken: FAKE_REFRESH_TOKEN });
        renewal.flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));
      }

      const identities = httpMock.match(IDENTITY_URL);
      expect(identities.length)
        .withContext('the identity read is part of the one shared renewal, so it happens once')
        .toBe(1);

      for (const identity of identities) {
        identity.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);
      }

      // Both originals are retried exactly once each, and both present the rotated token.
      const firstRetry = httpMock.expectOne(PROTECTED_URL);
      expect(firstRetry.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`,
      );
      firstRetry.flush(PORTAL_PAGE_BODY);

      const secondRetry = httpMock.expectOne(OTHER_PROTECTED_URL);
      expect(secondRetry.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`,
      );
      secondRetry.flush(USER_PAGE_BODY);

      await Promise.all([first, second]);

      expect(navigate).not.toHaveBeenCalled();
    });

    it('does not attempt a renewal when the renewal credential has vanished', async () => {
      // The state a concurrent sign-out leaves behind: the access token is still held but the renewal
      // credential is not. An empty value is treated as absent rather than transmitted, because the legacy
      // absent-string sentinel WAS the empty string, so the two forms are the same statement — and posting
      // one would be refused as a malformed request instead of reported as a session that has ended.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, ''));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the refusal is re-thrown, never swallowed')
        .toBe(401);

      httpMock.expectNone(REFRESH_URL);
      expect(tokens.session()).toBeNull();
      expectEjectedToSignIn();
    });

    it('does not attempt a renewal when no session is held at all', async () => {
      // A request made with no session is not wrapped for recovery in the first place, so the server refusal
      // is the correct and final answer. Nothing is discarded and nobody is routed anywhere: there is no
      // session to end, and moving an operator who never had one would interrupt whatever anonymous screen
      // they are actually on.
      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      httpMock.expectNone(REFRESH_URL);
      expect(navigate)
        .withContext('there was no session to end, so there is nothing to announce')
        .not.toHaveBeenCalled();
    });

    it('discards EVERY domain slice, not merely the credential, when the session ends', async () => {
      // The regression this pins down. Every domain store is root-provided, so each one outlives the session
      // and keeps whatever it last read. Clearing the credential alone would leave one operator's portals,
      // accounts, roles and module content resident and legible to whoever signs in next on this browser - a
      // disclosure, not untidiness. The complete discard lives in `core/state/session-lifecycle.service.ts`;
      // what is asserted here is that a TERMINAL refusal reaches it, which is the half only this file can
      // prove.
      //
      // The two stores exercised are the ROLE and MODULE stores, deliberately: the portal listing is
      // addressed by the very URL this file uses as its protected request, so loading it here would make one
      // expectation ambiguous against another.
      const roles = TestBed.inject(RoleStore);
      const modules = TestBed.inject(ModuleStore);

      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      roles.loadRoles();
      const roleRead = httpMock.expectOne((candidate) => candidate.url === '/api/v1/roles');
      roleRead.flush({
        items: [
          {
            roleId: 0,
            portalId: -1,
            roleGroupId: null,
            roleName: 'Held Role',
            description: null,
            isPublic: false,
            autoAssignment: false,
            serviceFee: 0,
            billingFrequency: 'N',
            billingPeriod: null,
            trialFee: null,
            trialPeriod: null,
            trialFrequency: null,
          },
        ],
        meta: { totalCount: 1, pageIndex: 0, pageSize: 100, totalPages: 1 },
      });

      modules.loadModules();
      const moduleRead = httpMock.expectOne((candidate) => candidate.url === '/api/v1/modules');
      moduleRead.flush({
        items: [
          {
            moduleId: 0,
            tabModuleId: 0,
            tabId: 0,
            portalId: -1,
            moduleDefId: 1,
            moduleTitle: 'Held Announcements',
            moduleOrder: 1,
            paneName: 'ContentPane',
            allTabs: false,
            visibility: 0,
            isDeleted: false,
            displayTitle: true,
            startDate: null,
            endDate: null,
            friendlyName: 'Announcements',
            desktopModuleId: 2,
            moduleName: 'Announcements',
            description: null,
            version: '01.00.00',
          },
        ],
        meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
      });

      expect(roles.roleItems().length).toBe(1);
      expect(modules.modules().length).toBe(1);

      const pending = firstValueFrom(http.get(OTHER_PROTECTED_URL));

      refuse(httpMock.expectOne(OTHER_PROTECTED_URL));
      // The renewal is refused too, which is what makes the outcome TERMINAL.
      refuse(httpMock.expectOne(REFRESH_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(tokens.session()).toBeNull();
      expect(roles.roleItems())
        .withContext('the previous operator roles must not survive a terminal refusal')
        .toEqual([]);
      expect(modules.modules())
        .withContext('module content is session content and goes with the credential')
        .toEqual([]);
      expectEjectedToSignIn();
    });

    it('still reports the original refusal when routing to sign-in itself fails', async () => {
      // The subject is mid-way through re-throwing the response the server actually sent, and a routing
      // problem must not displace it — nor become an unhandled rejection surfacing somewhere unrelated. The
      // session is still discarded, because whether the operator can be moved is independent of whether the
      // session is over.
      navigate.and.rejectWith(new Error('the sign-in screen is not routable yet'));

      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      refuseRenewal(httpMock.expectOne(REFRESH_URL));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the caller still hears about their own request')
        .toBe(401);
      expect(tokens.session()).toBeNull();
      expectEjectedToSignIn();
    });
  });

  /**
   * Cross-session recovery.
   *
   * The defect these cases pin is an authorisation crossing, not a cosmetic one. Recovery used to ask only
   * "is SOME renewal credential held?" before renewing and retrying, and it re-read the token to retry with
   * from storage. So an operator who signed out and back in as somebody else while a request was in the air
   * could have that request — composed under account A's authority — executed by the server AS ACCOUNT B.
   *
   * Every case below drives the session transition WHILE the original request or its renewal is in the air,
   * which is what puts the recovery decision on the wrong side of it. The testing backend makes that
   * ordering exact, so these are deterministic rather than timing-dependent.
   *
   * Two properties are asserted throughout and both matter. The original request must not be retried under
   * the new authority; and the NEW session must be left completely alone — not cleared, not navigated away
   * from — because whoever performed the transition did so deliberately.
   */
  describe('recovery across a session change', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
    });

    it('does not retry under a different session established while the request was in flight', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const first = httpMock.expectOne(PROTECTED_URL);
      expect(first.request.headers.get(AUTHORIZATION_HEADER)).toBe(`Bearer ${FAKE_ACCESS_TOKEN}`);

      // The operator signs out and back in as somebody else. Both transitions advance the auth epoch, so the
      // request in flight no longer belongs to the session being held.
      tokens.clear();
      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      refuse(first);

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext("the caller learns its own request failed")
        .toBe(401);

      // The three things that must NOT have happened.
      httpMock.expectNone(REFRESH_URL);
      httpMock.expectNone(PROTECTED_URL);
      expect(tokens.accessToken())
        .withContext('the newer session is untouched')
        .toBe('fake-access-token-other');
      expect(navigate)
        .withContext('and the operator is not moved off the screen they just reached')
        .not.toHaveBeenCalled();
    });

    it('does not renew a healthy session on the strength of an ended one being refused', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));
      const first = httpMock.expectOne(PROTECTED_URL);

      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      refuse(first);
      await reasonFor(pending);

      // The old presence test would have passed here - a refresh token IS held - and renewed a session that
      // had nothing wrong with it, consuming its rotation for no reason.
      httpMock.expectNone(REFRESH_URL);
      expect(tokens.refreshToken()).toBe('fake-refresh-token-other');
    });

    // The widest window in the whole path: a renewal is two round trips, and a transition landing inside it
    // is the race the second guard exists for.
    it('does not retry when the session changes while the renewal is in flight', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      const renewal = httpMock.expectOne(REFRESH_URL);

      // The transition lands after the renewal was requested but before it is answered.
      tokens.clear();
      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      renewal.flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));

      const identity = httpMock.expectOne(IDENTITY_URL);
      identity.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the original refusal reaches the caller unchanged')
        .toBe(401);

      httpMock.expectNone(PROTECTED_URL);
      expect(tokens.accessToken())
        .withContext('the renewal was obsoleted, so its rotated pair was never stored')
        .toBe('fake-access-token-other');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('leaves a newer session intact when the older renewal is refused', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      const renewal = httpMock.expectOne(REFRESH_URL);

      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      refuse(renewal);

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      // The sharpest assertion in this block. Tearing down here would sign out an operator whose own sign-in
      // had just succeeded - the same defect one operator further along.
      expect(tokens.accessToken())
        .withContext('the newer session survives an older renewal being refused')
        .toBe('fake-access-token-other');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('leaves a newer session intact when the retry is refused after an account switch', async () => {
      // The retry is a further round trip, so a sign-in can land while it is in the air - and the handler
      // that ends a session on a refused retry must be conditioned on the retried credential still being the
      // one held, exactly as the gate before the retry is. Without that condition, an operator whose own
      // sign-in had just succeeded would be signed out by a 401 belonging to a session that is already over.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);

      // Somebody signs in while the retry is in flight.
      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      refuse(retry);

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('the caller still learns its request failed')
        .toBe(401);

      expect(tokens.accessToken())
        .withContext('the newer session survives a refusal belonging to the older one')
        .toBe('fake-access-token-other');
      expect(navigate).not.toHaveBeenCalled();
      httpMock.expectNone(REFRESH_URL);
    });

    it('does not navigate again when the retry is refused after a sign-out', async () => {
      // Nothing is held, so there is nothing to end. The sign-out path has already torn the session down and
      // decided where the browser goes; a second navigation from here would contend with the one already
      // under way, and the operator is going where they asked to go regardless.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);

      tokens.clear();

      refuse(retry);

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(tokens.session()).toBeNull();
      expect(navigate)
        .withContext('the sign-out owns the destination, not this handler')
        .not.toHaveBeenCalled();
      httpMock.expectNone(REFRESH_URL);
    });

    // The complement: when the transition was a SIGN-OUT rather than a sign-in, nothing is held, and asking
    // the operator to sign in is both correct and what they asked for.
    it('still ends the session when the change was a sign-out', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      const renewal = httpMock.expectOne(REFRESH_URL);

      tokens.clear();

      refuse(renewal);

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);
      expect(tokens.session()).toBeNull();
      expectEjectedToSignIn();
    });
  });

  // The session's footprint beyond the custodian
  /**
   * This is the likeliest place a session actually ends.
   *
   * A deliberate sign-out goes through `core/state/auth.store.ts`, but a token whose renewal cannot be
   * completed ends the session from inside this interceptor, with no screen involved and nobody having
   * asked. Clearing the custodian ends the session's AUTHORITY and — because the identity projection is
   * stamped with the generation the custodian advances — also retracts the published account.
   *
   * It does NOT empty the domain stores. Those are each `providedIn: 'root'`, so each holds one instance
   * that survives the session, and without an explicit purge the previous operator's tenant listings, the
   * account record they had open, the role assignments naming other accounts and a serialised export of a
   * module's data would all still be in memory behind the sign-in screen — legible to whoever signed in next
   * on the same page load.
   *
   * The delegation is asserted through a spy rather than by populating four stores, because what belongs to
   * this file is WHETHER IT DELEGATES; what the delegate then does is proven in
   * `core/state/session-teardown.service.spec.ts`.
   */
  describe('the session footprint on an unrecoverable refusal', () => {
    let purge: jasmine.Spy<SessionTeardownService['purge']>;

    beforeEach(() => {
      configureWith([authInterceptor]);
      purge = spyOn(TestBed.inject(SessionTeardownService), 'purge').and.callThrough();
    });

    it('purges the domain stores when there is no renewal credential to try', async () => {
      // An access token with no renewal credential beside it: the refusal is terminal on the first response,
      // with no second request to wait for. The empty string is this contract's "none held", not null - see
      // `sessionFor`.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, ''));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(purge)
        .withContext('a session that cannot be renewed takes its footprint with it')
        .toHaveBeenCalledTimes(1);
      // ⚠ THE REASON IS PART OF THE DELEGATION, NOT DECORATION. The one owner of the boundary
      // publishes which boundary was crossed, and this path is a renewal the server refused
      // rather than an operator asking to leave — so a consumer, and this specification, can
      // tell the two apart. Reporting `signedOut` here would attribute an involuntary ending to
      // a deliberate one.
      expect(purge).toHaveBeenCalledWith('renewalRefused');
      expect(tokens.session()).toBeNull();
    });

    it('purges the domain stores when the renewal is itself refused', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      refuse(httpMock.expectOne(REFRESH_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(purge)
        .withContext('a refused renewal ends the session, footprint included')
        .toHaveBeenCalledTimes(1);
    });

    it('purges the domain stores when the retry is refused with a 401', async () => {
      // The termination reached through the retry uses the SAME terminal path as the other two, so it must
      // take the footprint with it. A session ended without the purge would leave the previous operator's
      // tenant listings, open account record and module export on screen for whoever signs in next.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();
      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(purge)
        .withContext('a credential refused straight after issue ends the session, footprint too')
        .toHaveBeenCalledTimes(1);
      expect(tokens.session()).toBeNull();
    });

    it('tells the operator why the session ended, in a message that survives the navigation', async () => {
      // ⚠ THE TEARDOWN WAS ALREADY CORRECT AND THE SCREEN WAS STILL WRONG. Measured in a browser: the
      // credential was cleared, every domain slice purged and the operator returned to sign-in, with BOTH
      // live regions empty - so someone mid-task was left looking at a sign-in screen with no account, no
      // work and no explanation, and a non-visual operator had nothing at all to go on.
      //
      // ⚠ AND THIS FILE NO LONGER RAISES IT, WHICH IS WHY THE CASE STAYS HERE. The sentence comes from
      // the purge this interceptor delegates to, because a SECOND path ends a session un-asked-for - the
      // navigation gate's renewal - and one sentence raised from two files could not have covered it.
      // What is asserted here is that ending a session THROUGH THIS PATH still reaches the operator; the
      // purge's own conditions are proven in `core/state/session-teardown.service.spec.ts`.
      const notifications = TestBed.inject(NotificationService);
      const retain = spyOn(notifications, 'retainAcrossNavigation').and.callThrough();

      // ⚠ SPIED AT THE RAISING SEAM, NOT ONLY AT THE QUEUE, AND THAT IS WHAT MAKES THE OWNERSHIP
      // ASSERTION REAL. This file used to raise the sentence itself as well, so it had TWO publishers -
      // and the queue-depth assertion below could not see it, because `notify` collapses an entry that
      // repeats the newest one on every stored member. The duplicate was absorbed, the depth stayed one,
      // and the operator's experience depended on a de-duplication rule holding rather than on only one
      // party speaking; anything that perturbed the identity comparison, a differing retention flag most
      // of all, would have surfaced the message twice. Counting the raises is what catches a second
      // publisher whether or not the queue happens to hide it.
      const warn = spyOn(notifications, 'warning').and.callThrough();

      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      refuse(httpMock.expectOne(REFRESH_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(warn)
        .withContext('one owner raises the sentence, and it is the purge rather than this interceptor')
        .toHaveBeenCalledTimes(1);

      const queued = notifications.notifications();

      expect(queued.length).toBe(1);
      expect(queued[0]?.message).toBe(SESSION_ENDED_MESSAGE);
      // A warning, not an error: nothing failed on the operator's part, and the severity carries the
      // longer on-screen lifetime a message read on ARRIVAL at another screen needs.
      expect(queued[0]?.severity).toBe('warning');

      // Exempted from the navigation sweep, or the shell would clear it on the very `NavigationEnd`
      // this message exists to accompany - which is how it would come to be raised and never seen.
      expect(retain).toHaveBeenCalledTimes(1);

      // Proven as SURVIVAL rather than as a flag: two mechanisms exempt an entry - the per-entry
      // `survivesNavigation` member and the queue's one-shot reprieve - and this path uses the second.
      // Driving the sweep asserts the property the operator depends on however it was obtained.
      notifications.clearOnNavigation();

      expect(notifications.notifications().map((entry) => entry.message))
        .withContext('the explanation is still there to read once the redirect has landed')
        .toEqual([SESSION_ENDED_MESSAGE]);
    });

    it('does not announce anything when the refusal was recoverable', async () => {
      // The negative half. A renewal that succeeds is not a session ending, so there is nothing to
      // explain and no message may appear.
      const notifications = TestBed.inject(NotificationService);

      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();
      httpMock.expectOne(PROTECTED_URL).flush({ ok: true });

      await pending;

      expect(notifications.notifications().length).toBe(0);
    });

    it('does not purge when the retry fails for a reason other than authentication', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();

      httpMock
        .expectOne(PROTECTED_URL)
        .flush(bareProblem(500), bareProblemInit(500));

      expect(httpStatusOf(await reasonFor(pending))).toBe(500);

      expect(purge)
        .withContext('a server fault must not empty the work the operator has in progress')
        .not.toHaveBeenCalled();
      expect(tokens.session()).not.toBeNull();
    });

    it('purges after clearing the custodian, so no read still believes its session is current', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, ''));

      // Order, not merely occurrence. Clearing advances the session generation that every late callback
      // tests itself against. Purging FIRST would leave a read already in flight still believing its session
      // was current, free to land afterwards and repopulate the very slices the purge had just emptied —
      // which is worse than not purging, because the stores look correctly emptied and refill a moment later
      // with nobody watching.
      let generationWhenPurged: number | null = null;

      purge.and.callFake((): number => {
        generationWhenPurged = tokens.generation();

        return 0;
      });

      const generationBefore = tokens.generation();
      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(purge).toHaveBeenCalledTimes(1);
      expect(generationWhenPurged)
        .withContext('the custodian was already cleared when the purge ran')
        .toBeGreaterThan(generationBefore);
    });

    it('does not purge when a refusal is recovered, because the session continues', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));

      // A renewal is TWO requests - the rotation and then an identity read presenting the fresh token - and
      // both must be answered, which is what this shared helper does.
      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);

      expect(retry.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ROTATED_ACCESS_TOKEN}`,
      );
      retry.flush({ ok: true });

      await expectAsync(pending).toBeResolved();

      // A recovered request is the SAME session continuing. Purging here would discard the listings the
      // operator is looking at every time their token rotated — a functional regression dressed up as
      // hardening.
      expect(purge)
        .withContext('a successful recovery leaves the work in progress alone')
        .not.toHaveBeenCalled();
    });

    it('does not purge when the session changed underneath the request', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));
      const first = httpMock.expectOne(PROTECTED_URL);

      // Somebody else's session is now the current one. Whoever established it did so deliberately, and an
      // unrelated stale 401 must not empty THEIR stores.
      tokens.clear();
      purge.calls.reset();
      tokens.store(sessionFor('fake-access-token-other', 'fake-refresh-token-other'));

      refuse(first);

      expect(httpStatusOf(await reasonFor(pending))).toBe(401);

      expect(purge)
        .withContext("a stale refusal must not discard the newer session's data")
        .not.toHaveBeenCalled();
      expect(tokens.accessToken()).toBe('fake-access-token-other');
    });

    it('does not purge for a status that is not an authentication failure', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      httpMock
        .expectOne(PROTECTED_URL)
        .flush(bareProblem(403), bareProblemInit(403));

      expect(httpStatusOf(await reasonFor(pending))).toBe(403);

      // A refusal to AUTHORISE is not a refusal to AUTHENTICATE. The session is valid and the operator is
      // still signed in; they simply may not do that one thing.
      expect(purge)
        .withContext('being told "no" is not being signed out')
        .not.toHaveBeenCalled();
      expect(tokens.session()).not.toBeNull();
    });
  });

  describe('statuses that are not an authentication failure', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
    });

    //  Only an expired or rejected token is recoverable. Everything else passes through exactly as the server
    //  sent it, and the reason differs per status:
    //
    //  - 403 means the server knows who the caller is and is refusing the OPERATION, so renewing the
    //    credential would change nothing and discarding the session would sign out an operator who is
    //    perfectly well signed in.
    //  - 429 is the fixed-window limiter answering, and is the named compensating control for the deleted
    //    legacy challenge-image gate. It must NEVER be retried: a retry is the one response guaranteed to
    //    make a rate-limit condition worse. There is no backoff here, no attempt counter and no reading of a
    //    retry hint — the document is presented to the operator and the decision is theirs.
    //  - 500 is a server fault and says nothing about the caller identity.
    //
    //  Each entry carries the code and sentence the SERVER pairs with that status, taken from the per-status
    //  vocabulary in `ValidationProblemDetailsFactory`: `auth.not_permitted` for 403, `request.rate_limited`
    //  for 429 and `server.unexpected_failure` for 500. A fixture that named no code could not have
    //  distinguished "the interceptor ignores the body" from "the interceptor never saw a body worth
    //  reading". The three statuses are named here; their titles, codes and sentences are READ FROM
    //  STATUS_VOCABULARY rather than restated, so this case and the recovered-path matrix above cannot end up
    //  asserting two different documents for the same status.
    const nonAuthStatuses: readonly number[] = [403, 429, 500];

    for (const status of nonAuthStatuses) {
      it(`passes a ${String(status)} through without renewing or retrying`, async () => {
        tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

        const pending = firstValueFrom(http.get(PROTECTED_URL));

        httpMock
          .expectOne(PROTECTED_URL)
          .flush(bareProblem(status), bareProblemInit(status));

        const reason = await reasonFor(pending);

        expect(httpStatusOf(reason))
          .withContext('the status reaches the caller unchanged')
          .toBe(status);
        expect(reason instanceof HttpErrorResponse ? reason.error : null)
          .withContext('and so does the document, untouched, for the error interceptor to read')
          .toEqual(bareProblem(status));

        httpMock.expectNone(REFRESH_URL);
        httpMock.expectNone(PROTECTED_URL);
        expect(tokens.session())
          .withContext('a status that is not an authentication failure leaves the session alone')
          .not.toBeNull();
        expect(navigate).not.toHaveBeenCalled();
      });
    }

    it('passes a transport-level failure through without renewing', async () => {
      // A network failure surfaces with status 0 and no body. It is not an authentication condition, so
      // renewing would spend a credential on a connection that is not there.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      httpMock.expectOne(PROTECTED_URL).error(new ProgressEvent('error'));

      expect(httpStatusOf(await reasonFor(pending)))
        .withContext('a transport failure is reported with no status of its own')
        .toBe(0);

      httpMock.expectNone(REFRESH_URL);
      expect(tokens.session()).not.toBeNull();
      expect(navigate).not.toHaveBeenCalled();
    });
  });

  describe('the absence of a proactive expiry check', () => {
    beforeEach(() => {
      configureWith([authInterceptor]);
    });

    it('presents a token whose stamped expiry has already passed', async () => {
      // This case exists to stop a proactive check being added. Renewal is REACTIVE on a refusal: no stored
      // expiry is read and no clock is consulted anywhere in the subject. A proactive check would only add a
      // second, disagreeing opinion about whether a token the server has not yet rejected is still good —
      // and it would disagree in the dangerous direction, spending a renewal that the server never asked for
      // and, on a client whose clock is wrong, doing so on every single request.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN, PAST_EXPIRY));

      // The premise, established through the store's own reader rather than asserted by eye, so this case
      // cannot pass vacuously against a fixture that is not actually stale. The store CAN answer the
      // question; the subject simply never asks it.
      expect(tokens.accessTokenExpiresAt())
        .withContext('the expiry is handed back exactly as stamped, unparsed')
        .toBe(PAST_EXPIRY);
      expect(tokens.isAccessTokenExpired(new Date()))
        .withContext('the seeded session really is stale')
        .toBeTrue();

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      // No renewal precedes the request. Asserted BEFORE the request is answered, so the ordering claim is
      // real rather than incidental.
      httpMock.expectNone(REFRESH_URL);

      const request = httpMock.expectOne(PROTECTED_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the stale token is presented and the server decides')
        .toBe(`Bearer ${FAKE_ACCESS_TOKEN}`);

      request.flush(PORTAL_PAGE_BODY);
      await pending;

      httpMock.expectNone(REFRESH_URL);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('recovers a stale session only once the server has actually refused it', async () => {
      // The other half of the same statement: a stale stamp changes nothing by itself, and a refusal changes
      // everything. Together the two cases pin renewal to the server answer and to nothing else.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN, PAST_EXPIRY));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      refuse(httpMock.expectOne(PROTECTED_URL));
      completeRenewal();
      httpMock.expectOne(PROTECTED_URL).flush(PORTAL_PAGE_BODY);

      await pending;

      expect(tokens.accessToken()).toBe(FAKE_ROTATED_ACCESS_TOKEN);
    });
  });

  describe('within the application configured interceptor chain', () => {
    // Both interceptors, in the ORDER THE APPLICATION FIXES. See the file header for why that order puts the
    // error interceptor innermost on the response path and why ownership of the 401 lifecycle was reassigned
    // rather than the order changed.
    //
    // Registering the correlation interceptor here — rather than hand-setting the header — is what makes the
    // correlation cases meaningful. A caller-supplied header exercises only the pass-through branch; a
    // stamped one exercises the branch the application actually uses, and proves the identifier survives a
    // retry it never sees.
    beforeEach(() => {
      configureWith([correlationIdInterceptor, authInterceptor]);
    });

    it('carries the SAME correlation identifier onto the retry', async () => {
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const first = httpMock.expectOne(PROTECTED_URL);
      const originalCorrelationId = first.request.headers.get(CORRELATION_ID_HEADER);
      expect(originalCorrelationId)
        .withContext('the outer interceptor stamps every outbound request')
        .not.toBeNull();
      expect(originalCorrelationId)
        .withContext('and stamps something, not an empty value')
        .not.toBe('');
      refuse(first);

      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);

      // The subject clones the ORIGINAL request — which the outer interceptor has already stamped — and
      // hands the clone to the INNER chain, so the retry never passes the correlation interceptor again.
      // Both attempts therefore carry ONE identifier, which is what lets the server logs join them into a
      // single operation instead of showing an unexplained refusal followed by an unrelated success.
      // Rebuilding the request from scratch, or letting the retry re-enter from the top, would split one
      // operation across two identifiers and defeat the purpose of having one.
      expect(retry.request.headers.get(CORRELATION_ID_HEADER))
        .withContext('both attempts are one logical operation and must be joinable as one')
        .toBe(originalCorrelationId);

      retry.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('gives the renewal and the identity read their own identifiers', async () => {
      // The renewal is a SEPARATE logical call, not part of the caller operation, and it re-enters the chain
      // from the top through the HTTP client — so the correlation interceptor stamps it afresh. Sharing the
      // caller identifier would make one identifier describe two different operations, which is the same
      // loss of meaning as splitting one operation across two.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const first = httpMock.expectOne(PROTECTED_URL);
      const originalCorrelationId = first.request.headers.get(CORRELATION_ID_HEADER);
      refuse(first);

      const renewal = httpMock.expectOne(REFRESH_URL);
      const renewalCorrelationId = renewal.request.headers.get(CORRELATION_ID_HEADER);
      expect(renewalCorrelationId)
        .withContext('the renewal is stamped like any other outbound request')
        .not.toBeNull();
      expect(renewalCorrelationId)
        .withContext('but it is its own logical call')
        .not.toBe(originalCorrelationId);
      renewal.flush(renewalBody(FAKE_ROTATED_ACCESS_TOKEN, FAKE_ROTATED_REFRESH_TOKEN));

      const identity = httpMock.expectOne(IDENTITY_URL);
      const identityCorrelationId = identity.request.headers.get(CORRELATION_ID_HEADER);
      expect(identityCorrelationId).not.toBeNull();
      expect(identityCorrelationId)
        .withContext('the identity read is a third call, and is traceable as one')
        .not.toBe(renewalCorrelationId);
      expect(identityCorrelationId).not.toBe(originalCorrelationId);
      identity.flush({ data: FAKE_USER, meta: null } satisfies SuccessEnvelope<CurrentUser>);

      httpMock.expectOne(PROTECTED_URL).flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('preserves an identifier the caller supplied, on both attempts', async () => {
      // The correlation interceptor does not overwrite a usable caller-supplied value, which is what lets a
      // caller stitch a browser-side observation to the server log lines for the same request. The retry
      // inherits it for the same reason it inherits a stamped one: the clone is taken from the request the
      // subject was handed.
      //
      // ⚠ THE SUPPLIED VALUE IS CANONICAL, AND IT HAS TO BE. "Usable" means one of the two canonical
      // shapes — 32 hexadecimal characters or the hyphenated 36-character rendering — because a
      // correlation identifier of arbitrary printable text can carry a secret into the server's logs.
      // A readable label such as `'from-the-caller'` is REPLACED by the correlation interceptor, so a
      // specification written with one would assert the replacement path while claiming to assert
      // preservation.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const supplied = '7c1b8f4e2a9d6c3f5b7a091e2d4d19ae';

      const pending = firstValueFrom(
        http.get(PROTECTED_URL, { headers: { [CORRELATION_ID_HEADER]: supplied } }),
      );

      const first = httpMock.expectOne(PROTECTED_URL);
      expect(first.request.headers.get(CORRELATION_ID_HEADER)).toBe(supplied);
      refuse(first);

      completeRenewal();

      const retry = httpMock.expectOne(PROTECTED_URL);
      expect(retry.request.headers.get(CORRELATION_ID_HEADER))
        .withContext('the caller identifier survives the retry it did not ask for')
        .toBe(supplied);

      retry.flush(PORTAL_PAGE_BODY);
      await pending;
    });

    it('leaves the bearer rules unchanged when the chain is fully assembled', async () => {
      // The two interceptors are independent: adding the outer one must not make the inner one attach a
      // credential where it otherwise would not, nor withhold one where it otherwise would. Both halves are
      // checked in one case, because the point is the pair.
      tokens.store(sessionFor(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      const protectedCall = firstValueFrom(http.get(PROTECTED_URL));
      const anonymousCall = firstValueFrom(http.post(LOGIN_URL, {}));

      const protectedRequest = httpMock.expectOne(PROTECTED_URL);
      expect(protectedRequest.request.headers.get(AUTHORIZATION_HEADER)).toBe(
        `Bearer ${FAKE_ACCESS_TOKEN}`,
      );
      expect(protectedRequest.request.headers.has(CORRELATION_ID_HEADER)).toBeTrue();
      protectedRequest.flush(PORTAL_PAGE_BODY);

      const anonymousRequest = httpMock.expectOne(LOGIN_URL);
      expect(anonymousRequest.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('an anonymous endpoint stays anonymous inside the full chain')
        .toBeFalse();
      expect(anonymousRequest.request.headers.has(CORRELATION_ID_HEADER))
        .withContext('but it is still traceable')
        .toBeTrue();
      // The sign-in endpoint answers 200 with a token pair, not with a page of portals.
      anonymousRequest.flush(renewalBody(FAKE_ACCESS_TOKEN, FAKE_REFRESH_TOKEN));

      await Promise.all([protectedCall, anonymousCall]);
    });
  });
});
