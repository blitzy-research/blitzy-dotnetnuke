import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { AUTH_ENDPOINTS, apiUrl } from '../config/api-endpoints';
import { AuthSession, CurrentUser, LoginResponse } from '../models/auth.model';
import { ApiResponse } from '../models/paged-result.model';
import { TokenStorageService } from '../services/token-storage.service';
import { authInterceptor } from './auth.interceptor';

/**
 * The header the interceptor writes, spelled independently of the implementation.
 *
 * The interceptor deliberately does not export it, so spelling it again here means a
 * rename on either side fails these expectations rather than passing silently.
 */
const AUTHORIZATION_HEADER = 'Authorization';

const USER: CurrentUser = {
  userId: 7,
  portalId: 0,
  portalName: 'Primary',
  username: 'admin',
  displayName: 'Administrator',
  email: 'admin@example.test',
  isSuperUser: false,
  roles: [],
  permissions: [],
};

function session(accessToken: string, refreshToken: string): AuthSession {
  return {
    accessToken,
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken,
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: USER,
  };
}

/**
 * A successful token-renewal BODY, varying only the two token values.
 *
 * Wrapped in the shared success envelope because that is what the server writes for any
 * payload-bearing response, the renewal included. The interceptor never sees this shape
 * itself - it delegates the renewal to the auth service, which unwraps - but the fake
 * transport must still answer with the body the real server sends, or the spec proves
 * the retry works against a body that does not exist.
 *
 * The metadata companion is present and `null`, matching the wire: the server writes
 * every declared member, so a response with no page to describe carries the member with
 * a null value rather than omitting it.
 */
function loginResponse(accessToken: string, refreshToken: string): ApiResponse<LoginResponse> {
  return {
    data: {
      accessToken,
      expiresAtUtc: '2100-01-01T00:00:00.000Z',
      refreshToken,
      mustChangePassword: false,
      passwordExpiring: false,
      // All three advisory flags are always present on the wire: the API serialises with
      // its ignore condition set to never, so a `false` is transmitted rather than omitted.
      mustUpdateProfile: false,
      user: USER,
    },
    // Present and null rather than omitted: the API serialises with its ignore condition set
    // to never, so a response with no page to describe writes the key with a null value.
    meta: null,
  };
}

/** Completes the identity bootstrap performed by AuthService after a successful refresh. */
function flushCurrentUser(
  controller: HttpTestingController,
  accessToken: string,
): void {
  const request = controller.expectOne(AUTH_ENDPOINTS.me);
  expect(request.request.method).toBe('GET');
  expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe(`Bearer ${accessToken}`);
  request.flush({ data: USER, meta: null } satisfies ApiResponse<CurrentUser>);
}

/** An arbitrary protected endpoint, used wherever the route itself is not the subject. */
const PROTECTED_URL = apiUrl('portals');

describe('authInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let storage: TokenStorageService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
    storage = TestBed.inject(TokenStorageService);
  });

  afterEach(() => {
    controller.verify();
  });

  describe('attaching the token', () => {
    it('attaches the bearer token to an API request', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const request = controller.expectOne(PROTECTED_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe('Bearer access-1');

      request.flush({});
      await pending;
    });

    it('attaches nothing when no session is held', async () => {
      const pending = firstValueFrom(http.get(PROTECTED_URL));

      const request = controller.expectOne(PROTECTED_URL);
      expect(request.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('no token exists to attach')
        .toBeFalse();

      request.flush({});
      await pending;
    });

    it('leaves a request that is not addressed to the API alone', async () => {
      // Attaching a bearer token to a static asset or a third-party URL would disclose it
      // to whoever serves that URL.
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get('/assets/config.json'));

      const request = controller.expectOne('/assets/config.json');
      expect(request.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('not an API request')
        .toBeFalse();

      request.flush({});
      await pending;
    });

    // -----------------------------------------------------------------------------------
    // CREDENTIAL-EXFILTRATION REGRESSION CASES
    //
    // Each URL below was accepted as an API request by the previous textual base test and
    // therefore received `Authorization: Bearer <token>`. Every one of the first three is a
    // FOREIGN ORIGIN, so the token was disclosed to whoever served that host; the fourth is
    // a same-origin path that merely shares a textual prefix with the configured base and
    // is a different API version. The expectation is deliberately expressed against the
    // header rather than against the predicate, because the header is the disclosure.
    // -----------------------------------------------------------------------------------

    const foreignOriginUrls: readonly { readonly url: string; readonly why: string }[] = [
      {
        url: 'https://evil.example/api/v1/exfiltrate',
        why: 'an absolute foreign origin that merely contains the configured base path',
      },
      {
        url: '//evil.example/api/v1/exfiltrate',
        why: 'a protocol-relative URL that reads as a path but resolves to a foreign host',
      },
      {
        url: 'https://evil.example/x?next=/api/v1/users',
        why: 'a foreign origin carrying the configured base inside a query parameter',
      },
    ];

    for (const { url, why } of foreignOriginUrls) {
      it(`attaches nothing to ${url}`, async () => {
        storage.store(session('access-1', 'refresh-1'));

        const pending = firstValueFrom(http.get(url));

        const request = controller.expectOne(url);
        expect(request.request.headers.has(AUTHORIZATION_HEADER))
          .withContext(`${why} — the bearer token must never reach it`)
          .toBeFalse();

        request.flush({});
        await pending;
      });
    }

    it('attaches nothing to a path that only shares a textual prefix with the base', async () => {
      // `/api/v10` is a different API version, not a descendant of `/api/v1`. A prefix test
      // admits it; a segment-bounded test does not.
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get('/api/v10/users'));

      const request = controller.expectOne('/api/v10/users');
      expect(request.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('/api/v10 is not inside /api/v1')
        .toBeFalse();

      request.flush({});
      await pending;
    });

    it('still attaches the token to a same-origin absolute API URL', async () => {
      // The tightened test compares resolved origins, so an API URL written in absolute
      // form against this document's own origin must keep working. Without this case the
      // three refusals above could be satisfied by a predicate that refused everything.
      storage.store(session('access-1', 'refresh-1'));

      const absolute = new URL(PROTECTED_URL, document.baseURI).href;

      const pending = firstValueFrom(http.get(absolute));

      const request = controller.expectOne(absolute);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe('Bearer access-1');

      request.flush({});
      await pending;
    });

    it('still attaches the token to a relative API URL carrying a query string', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const withQuery = `${PROTECTED_URL}?pageIndex=0&pageSize=10`;

      const pending = firstValueFrom(http.get(withQuery));

      const request = controller.expectOne(withQuery);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe('Bearer access-1');

      request.flush({});
      await pending;
    });

    it('treats a foreign URL ending in an anonymous endpoint path as not ours', async () => {
      // The anonymous-endpoint test previously matched on a path suffix, so any host willing
      // to serve a path ending in `/auth/login` was classified as this application's own
      // sign-in endpoint. It is refused as an API request first, so the classification can
      // no longer be reached from a foreign origin at all.
      storage.store(session('access-1', 'refresh-1'));

      const foreign = `https://evil.example${new URL(AUTH_ENDPOINTS.login, document.baseURI).pathname}`;

      const pending = firstValueFrom(http.post(foreign, {}));

      const request = controller.expectOne(foreign);
      expect(request.request.headers.has(AUTHORIZATION_HEADER))
        .withContext('a foreign origin is never one of our anonymous endpoints')
        .toBeFalse();

      request.flush({});
      await pending;
    });

    it('leaves the anonymous authentication endpoints alone', async () => {
      storage.store(session('access-1', 'refresh-1'));

      for (const url of [AUTH_ENDPOINTS.login, AUTH_ENDPOINTS.refresh, AUTH_ENDPOINTS.logout]) {
        const pending = firstValueFrom(http.post(url, {}));

        const request = controller.expectOne(url);
        expect(request.request.headers.has(AUTHORIZATION_HEADER))
          .withContext(`${url} authenticates its own payload, not a bearer token`)
          .toBeFalse();

        request.flush({});
        await pending;
      }
    });

    it('does attach the token to the identity endpoint, which requires one', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get(AUTH_ENDPOINTS.me));

      const request = controller.expectOne(AUTH_ENDPOINTS.me);
      expect(request.request.headers.get(AUTHORIZATION_HEADER)).toBe('Bearer access-1');

      request.flush({});
      await pending;
    });

    it('does not overwrite an Authorization header the caller set explicitly', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(
        http.get(PROTECTED_URL, { headers: { Authorization: 'Bearer caller-supplied' } }),
      );

      const request = controller.expectOne(PROTECTED_URL);
      expect(request.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('a caller that set one had a reason')
        .toBe('Bearer caller-supplied');

      request.flush({});
      await pending;
    });
  });

  describe('recovering from a 401', () => {
    it('refreshes once and retries with the rotated token', async () => {
      storage.store(session('access-old', 'refresh-old'));

      const pending = firstValueFrom(http.get<{ ok: boolean }>(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      const refresh = controller.expectOne(AUTH_ENDPOINTS.refresh);
      expect(refresh.request.body).toEqual({ refreshToken: 'refresh-old' });
      refresh.flush(loginResponse('access-new', 'refresh-new'));
      flushCurrentUser(controller, 'access-new');

      const retry = controller.expectOne(PROTECTED_URL);
      expect(retry.request.headers.get(AUTHORIZATION_HEADER))
        .withContext('the retry presents the refreshed token, not the captured one')
        .toBe('Bearer access-new');
      retry.flush({ ok: true });

      expect(await pending).toEqual({ ok: true });
    });

    it('reports the ORIGINAL 401 when the refresh itself fails', async () => {
      // Reporting the refresh failure instead would replace "your request was not
      // authorised" with an unrelated message about a token the caller never sent.
      storage.store(session('access-old', 'refresh-old'));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      controller
        .expectOne(AUTH_ENDPOINTS.refresh)
        .flush({ title: 'Forbidden', status: 403 }, { status: 403, statusText: 'Forbidden' });

      const error = await pending.then(
        () => null,
        (reason: unknown) => reason,
      );

      expect((error as { status?: number }).status)
        .withContext('the caller hears about their own request')
        .toBe(401);
      expect(storage.session()).toBeNull();
    });

    it('does not retry more than once, so a persistently rejecting server cannot loop', async () => {
      storage.store(session('access-old', 'refresh-old'));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      controller.expectOne(AUTH_ENDPOINTS.refresh).flush(loginResponse('access-new', 'refresh-new'));
      flushCurrentUser(controller, 'access-new');

      // The retry is refused as well. No further refresh and no further retry may follow.
      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      await expectAsync(pending).toBeRejected();
      controller.expectNone(AUTH_ENDPOINTS.refresh);
    });

    it('issues ONE refresh for several requests that expire together', async () => {
      storage.store(session('access-old', 'refresh-old'));

      const first = firstValueFrom(http.get(apiUrl('portals')));
      const second = firstValueFrom(http.get(apiUrl('users')));

      for (const url of [apiUrl('portals'), apiUrl('users')]) {
        controller
          .expectOne(url)
          .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });
      }

      const refreshes = controller.match(AUTH_ENDPOINTS.refresh);
      expect(refreshes.length)
        .withContext('a second refresh would present a consumed token and revoke the family')
        .toBe(1);
      refreshes[0]!.flush(loginResponse('access-new', 'refresh-new'));
      flushCurrentUser(controller, 'access-new');

      controller.expectOne(apiUrl('portals')).flush({});
      controller.expectOne(apiUrl('users')).flush({});

      await Promise.all([first, second]);
    });

    it('does not attempt a refresh when the session has already gone', async () => {
      // A concurrent refresh failure or an explicit sign-out can clear the session while
      // this request is in flight; there is then nothing to renew.
      const pending = firstValueFrom(http.get(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      await expectAsync(pending).toBeRejected();
      controller.expectNone(AUTH_ENDPOINTS.refresh);
    });

    it('does not refresh on a 403, because the caller is known and the operation is refused', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Forbidden', status: 403 }, { status: 403, statusText: 'Forbidden' });

      await expectAsync(pending).toBeRejected();
      controller.expectNone(AUTH_ENDPOINTS.refresh);
      expect(storage.session())
        .withContext('a 403 is not an authentication failure, so the session stands')
        .not.toBeNull();
    });

    it('does not refresh on a 500', async () => {
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.get(PROTECTED_URL));

      controller
        .expectOne(PROTECTED_URL)
        .flush({ title: 'Server Error', status: 500 }, { status: 500, statusText: 'Server Error' });

      await expectAsync(pending).toBeRejected();
      controller.expectNone(AUTH_ENDPOINTS.refresh);
    });

    it('does not attempt to recover a failed refresh with another refresh', async () => {
      // The exclusion of the refresh endpoint from this interceptor is what makes the
      // recursion unreachable; this asserts it rather than trusting it.
      storage.store(session('access-1', 'refresh-1'));

      const pending = firstValueFrom(http.post(AUTH_ENDPOINTS.refresh, { refreshToken: 'x' }));

      controller
        .expectOne(AUTH_ENDPOINTS.refresh)
        .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

      await expectAsync(pending).toBeRejected();
      controller.expectNone(AUTH_ENDPOINTS.refresh);
    });
  });
});
