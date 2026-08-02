import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { AUTH_ENDPOINTS, apiUrl } from '../config/api-endpoints';
import { AuthSession, CurrentUser, LoginResponse } from '../models/auth.model';
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
    tokenType: 'Bearer',
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken,
    refreshTokenExpiresAtUtc: '2100-01-02T00:00:00.000Z',
    user: USER,
  };
}

function loginResponse(accessToken: string, refreshToken: string): LoginResponse {
  return {
    accessToken,
    tokenType: 'Bearer',
    expiresIn: 900,
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken,
    refreshTokenExpiresAtUtc: '2100-01-02T00:00:00.000Z',
    user: USER,
  };
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
