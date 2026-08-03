import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { AUTH_ENDPOINTS } from '../config/api-endpoints';
import { AuthSession, CurrentUser, LoginResponse } from '../models/auth.model';
import { ApiResponse } from '../models/paged-result.model';
import { AuthService } from './auth.service';
import { TokenStorageService } from './token-storage.service';

const USER: CurrentUser = {
  userId: 7,
  portalId: 0,
  portalName: 'Primary',
  username: 'admin',
  displayName: 'Administrator',
  email: 'admin@example.test',
  isSuperUser: false,
  roles: ['Administrators'],
  permissions: ['VIEW'],
};

/**
 * A successful token response BODY, varying only the two token values.
 *
 * Returns the payload inside the shared success envelope, because that is what the
 * server writes: every action that answers with a payload wraps it, so a spec that
 * flushed the payload bare would be testing a body the server never sends. The failure
 * is worth naming, because it is silent rather than loud - the service would map an
 * envelope-shaped object with no `accessToken`, and the stored session would be a
 * shape-correct blank rather than an error.
 *
 * The metadata companion is deliberately absent. It describes a page, and a token
 * response has none; asserting its absence is asserting a real property of the
 * contract.
 */
function loginResponse(accessToken: string, refreshToken: string): ApiResponse<LoginResponse> {
  return {
    data: {
      accessToken,
      expiresAtUtc: '2100-01-01T00:00:00.000Z',
      refreshToken,
      mustChangePassword: false,
      passwordExpiring: false,
      user: USER,
    },
  };
}

/** A session already in place, so refresh and logout have something to work from. */
function existingSession(): AuthSession {
  return {
    accessToken: 'access-old',
    expiresAtUtc: '2000-01-01T00:00:00.000Z',
    refreshToken: 'refresh-old',
    mustChangePassword: false,
    passwordExpiring: false,
    user: USER,
  };
}

describe('AuthService', () => {
  let service: AuthService;
  let storage: TokenStorageService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client with NO interceptors: this specification is about the service's
      // own behaviour, and running the interceptor chain here would mean asserting two
      // units at once.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(AuthService);
    storage = TestBed.inject(TokenStorageService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  describe('login', () => {
    it('posts the credentials to the login endpoint', async () => {
      const pending = firstValueFrom(service.login({ username: 'admin', password: 'secret' }));

      const request = http.expectOne(AUTH_ENDPOINTS.login);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ username: 'admin', password: 'secret' });

      request.flush(loginResponse('access-1', 'refresh-1'));
      await pending;
    });

    it('stores the session and returns the identity', async () => {
      const pending = firstValueFrom(service.login({ username: 'admin', password: 'secret' }));

      http.expectOne(AUTH_ENDPOINTS.login).flush(loginResponse('access-1', 'refresh-1'));

      expect(await pending).toEqual(USER);
      expect(storage.accessToken()).toBe('access-1');
      expect(storage.refreshToken()).toBe('refresh-1');
    });

    it('clears any earlier session before attempting, so a failed sign-in leaves none behind', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.login({ username: 'admin', password: 'wrong' }));

      // The clear happens synchronously at the call, before the response arrives.
      expect(storage.session()).withContext('cleared at the point of the call').toBeNull();

      http.expectOne(AUTH_ENDPOINTS.login).flush(
        { title: 'Unauthorized', status: 401 },
        { status: 401, statusText: 'Unauthorized' },
      );

      await expectAsync(pending).toBeRejected();
      expect(storage.session()).toBeNull();
    });

    it('re-throws the failure unchanged, so the caller can render the server problem document', async () => {
      const pending = firstValueFrom(service.login({ username: 'admin', password: 'wrong' }));

      http.expectOne(AUTH_ENDPOINTS.login).flush(
        { title: 'Unauthorized', status: 401, type: 'urn:dnnmigration:error:auth.locked_out' },
        { status: 401, statusText: 'Unauthorized' },
      );

      const error = await pending.then(
        () => null,
        (reason: unknown) => reason,
      );

      expect(error instanceof HttpErrorResponse)
        .withContext('the transport failure reaches the caller intact')
        .toBeTrue();
      expect((error as HttpErrorResponse).status).toBe(401);
    });

    it('passes the tenant through when one is stated explicitly', async () => {
      const pending = firstValueFrom(
        service.login({ username: 'admin', password: 'secret', portalId: 3 }),
      );

      const request = http.expectOne(AUTH_ENDPOINTS.login);
      expect(request.request.body).toEqual({ username: 'admin', password: 'secret', portalId: 3 });

      request.flush(loginResponse('access-1', 'refresh-1'));
      await pending;
    });
  });

  describe('refresh', () => {
    it('fails without issuing a request when no refresh token is held', async () => {
      await expectAsync(firstValueFrom(service.refresh())).toBeRejected();

      // `verify` in afterEach would also catch a stray request, but asserting none here
      // names the behaviour: an empty token is not posted for the server to reject.
      http.expectNone(AUTH_ENDPOINTS.refresh);
    });

    it('returns an observable rather than throwing synchronously, so an interceptor can compose it', () => {
      // An interceptor calls this inside `catchError` and must always receive an
      // observable back; a synchronous throw there would escape the pipeline.
      expect(() => service.refresh()).not.toThrow();
    });

    it('posts the held refresh token and stores the rotated pair', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.refresh());

      const request = http.expectOne(AUTH_ENDPOINTS.refresh);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ refreshToken: 'refresh-old' });

      request.flush(loginResponse('access-new', 'refresh-new'));
      await pending;

      expect(storage.accessToken()).toBe('access-new');
      expect(storage.refreshToken()).toBe('refresh-new');
    });

    it('coalesces concurrent callers onto ONE request', async () => {
      // The defect this prevents is specific and severe: several requests expiring
      // together would each present the same refresh token, the first would rotate it,
      // and the rest would present a consumed token - which the server treats as a
      // replay and answers by revoking the whole family, signing the person out
      // precisely because the client tried to keep them signed in.
      storage.store(existingSession());

      const first = firstValueFrom(service.refresh());
      const second = firstValueFrom(service.refresh());
      const third = firstValueFrom(service.refresh());

      const requests = http.match(AUTH_ENDPOINTS.refresh);
      expect(requests.length).withContext('one refresh for three callers').toBe(1);

      requests[0]!.flush(loginResponse('access-new', 'refresh-new'));

      const sessions = await Promise.all([first, second, third]);
      expect(sessions[0]!.accessToken).toBe('access-new');
      expect(sessions[1]).toBe(sessions[0]!);
      expect(sessions[2]).toBe(sessions[0]!);
    });

    it('starts a fresh request for a later caller once the first has completed', async () => {
      storage.store(existingSession());

      const first = firstValueFrom(service.refresh());
      http.expectOne(AUTH_ENDPOINTS.refresh).flush(loginResponse('access-2', 'refresh-2'));
      await first;

      const second = firstValueFrom(service.refresh());
      const request = http.expectOne(AUTH_ENDPOINTS.refresh);
      expect(request.request.body)
        .withContext('the second renewal presents the rotated token, not the consumed one')
        .toEqual({ refreshToken: 'refresh-2' });

      request.flush(loginResponse('access-3', 'refresh-3'));
      await second;
    });

    it('discards the session when the server refuses the refresh token', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.refresh());

      http.expectOne(AUTH_ENDPOINTS.refresh).flush(
        { title: 'Unauthorized', status: 401 },
        { status: 401, statusText: 'Unauthorized' },
      );

      await expectAsync(pending).toBeRejected();
      expect(storage.session())
        .withContext('a refused refresh token cannot be retried, so it is not kept')
        .toBeNull();
    });

    it('allows a later renewal attempt after a failure, rather than replaying the failure forever', async () => {
      storage.store(existingSession());

      const failing = firstValueFrom(service.refresh());
      http.expectOne(AUTH_ENDPOINTS.refresh).flush(
        { title: 'Unauthorized', status: 401 },
        { status: 401, statusText: 'Unauthorized' },
      );
      await expectAsync(failing).toBeRejected();

      // The session was cleared by the failure, so the next attempt fails for the
      // correct reason - no token - rather than by replaying the cached error.
      storage.store(existingSession());
      const retry = firstValueFrom(service.refresh());
      http.expectOne(AUTH_ENDPOINTS.refresh).flush(loginResponse('access-4', 'refresh-4'));

      await retry;
      expect(storage.accessToken()).toBe('access-4');
    });
  });

  describe('logout', () => {
    it('posts the refresh token for revocation and clears local state', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.logout());

      const request = http.expectOne(AUTH_ENDPOINTS.logout);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ refreshToken: 'refresh-old' });

      request.flush(null);
      await pending;

      expect(storage.session()).toBeNull();
    });

    it('clears local state before the server answers, so signing out cannot be undone by a failure', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.logout());

      expect(storage.session()).withContext('cleared at the point of the call').toBeNull();

      http.expectOne(AUTH_ENDPOINTS.logout).flush(null);
      await pending;
    });

    it('completes successfully even when revocation fails, because the person asked to sign out', async () => {
      storage.store(existingSession());

      const pending = firstValueFrom(service.logout());

      http.expectOne(AUTH_ENDPOINTS.logout).flush(
        { title: 'Server Error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );

      await expectAsync(pending).toBeResolved();
      expect(storage.session()).toBeNull();
    });

    it('issues no request when no session is held', async () => {
      await expectAsync(firstValueFrom(service.logout())).toBeResolved();

      http.expectNone(AUTH_ENDPOINTS.logout);
    });
  });

  describe('exposed session state', () => {
    it('reflects the stored session, so consumers need one injection rather than two', () => {
      expect(service.isAuthenticated()).withContext('nothing stored yet').toBeFalse();

      storage.store(existingSession());

      expect(service.isAuthenticated()).withContext('a session is stored').toBeTrue();
      expect(service.currentUser()).toEqual(USER);
    });
  });
});
