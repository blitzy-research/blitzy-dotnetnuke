import { TestBed } from '@angular/core/testing';

import { AuthSession, CurrentUser, LoginResponse, sessionFromLoginResponse } from '../models/auth.model';
import { TokenStorageService } from './token-storage.service';

/**
 * A signed-in identity with two permission keys, used wherever the identity itself is
 * not what is under test.
 */
const USER: CurrentUser = {
  userId: 7,
  portalId: 0,
  portalName: 'Primary',
  username: 'admin',
  displayName: 'Administrator',
  email: 'admin@example.test',
  isSuperUser: false,
  roles: ['Administrators'],
  permissions: ['VIEW', 'EDIT'],
};

/**
 * Builds a session whose access token expires at the supplied instant.
 *
 * The expiry is the only member most expectations vary, so it is the only parameter.
 */
function sessionExpiringAt(expiresAtUtc: string, accessToken = 'access-1'): AuthSession {
  return {
    accessToken,
    expiresAtUtc,
    refreshToken: 'refresh-1',
    mustChangePassword: false,
    passwordExpiring: false,
    user: USER,
  };
}

describe('TokenStorageService', () => {
  let service: TokenStorageService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(TokenStorageService);
  });

  describe('initial state', () => {
    it('holds no session', () => {
      expect(service.session()).toBeNull();
    });

    it('reports no access token and no refresh token', () => {
      expect(service.accessToken()).toBeNull();
      expect(service.refreshToken()).toBeNull();
    });

    it('reports nobody signed in', () => {
      expect(service.isAuthenticated()).withContext('no session has been stored').toBeFalse();
      expect(service.currentUser()).toBeNull();
    });

    it('reports an empty permission list rather than null, so consumers can iterate unconditionally', () => {
      expect(service.permissions()).toEqual([]);
    });

    it('returns the same permission array instance on repeated reads, so a derived signal is stable', () => {
      // A fresh array per read would make every consumer see a change on every
      // evaluation, defeating the point of a computed signal.
      expect(service.permissions()).toBe(service.permissions());
    });
  });

  describe('store', () => {
    it('exposes the stored session and its tokens', () => {
      const session = sessionExpiringAt('2100-01-01T00:00:00.000Z');

      service.store(session);

      expect(service.session()).toBe(session);
      expect(service.accessToken()).toBe('access-1');
      expect(service.refreshToken()).toBe('refresh-1');
    });

    it('reports somebody signed in, with their identity and permissions', () => {
      service.store(sessionExpiringAt('2100-01-01T00:00:00.000Z'));

      expect(service.isAuthenticated()).withContext('a session was stored').toBeTrue();
      expect(service.currentUser()).toBe(USER);
      expect(service.permissions()).toEqual(['VIEW', 'EDIT']);
    });

    it('replaces a previous session, so a rotated refresh token does not linger', () => {
      // This is the case that matters: a refresh returns BOTH a new access token and a
      // new refresh token, and keeping the consumed one would have it re-presented on
      // the next renewal, which the server treats as a replay.
      service.store(sessionExpiringAt('2100-01-01T00:00:00.000Z', 'access-1'));

      const rotated: AuthSession = {
        ...sessionExpiringAt('2100-01-01T00:00:00.000Z', 'access-2'),
        refreshToken: 'refresh-2',
      };

      service.store(rotated);

      expect(service.accessToken()).toBe('access-2');
      expect(service.refreshToken()).toBe('refresh-2');
    });
  });

  describe('clear', () => {
    it('discards the session', () => {
      service.store(sessionExpiringAt('2100-01-01T00:00:00.000Z'));

      service.clear();

      expect(service.session()).toBeNull();
      expect(service.accessToken()).toBeNull();
      expect(service.isAuthenticated()).withContext('the session was cleared').toBeFalse();
    });

    it('is idempotent, so a sign-out racing an expiry needs no guard', () => {
      service.clear();
      service.clear();

      expect(service.session()).toBeNull();
    });
  });

  describe('isAccessTokenExpired', () => {
    it('reports expired when no session is held, because there is no token to rely on', () => {
      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('no session')
        .toBeTrue();
    });

    it('reports valid strictly before the expiry instant', () => {
      service.store(sessionExpiringAt('2024-01-01T00:00:10.000Z'));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:09.999Z')))
        .withContext('one millisecond before expiry')
        .toBeFalse();
    });

    it('reports expired exactly at the expiry instant, so the boundary is not usable', () => {
      service.store(sessionExpiringAt('2024-01-01T00:00:10.000Z'));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:10.000Z')))
        .withContext('exactly at expiry')
        .toBeTrue();
    });

    it('reports expired after the expiry instant', () => {
      service.store(sessionExpiringAt('2024-01-01T00:00:10.000Z'));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:11.000Z')))
        .withContext('after expiry')
        .toBeTrue();
    });

    it('reports expired for an unreadable expiry, rather than assuming it is in the future', () => {
      // An instant that cannot be parsed cannot be shown to be valid, and treating it as
      // valid would send a token the server will certainly reject.
      service.store(sessionExpiringAt('not-an-instant'));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('unparseable expiry')
        .toBeTrue();
    });
  });

  describe('persistence posture', () => {
    it('writes nothing to web storage, which is the security decision the service exists to make', () => {
      // Measured rather than asserted in prose: a token in web storage is readable by any
      // script on the origin, so one successful injection anywhere exfiltrates a
      // long-lived refresh token.
      const localBefore = window.localStorage.length;
      const sessionBefore = window.sessionStorage.length;

      service.store(sessionExpiringAt('2100-01-01T00:00:00.000Z'));

      expect(window.localStorage.length).toBe(localBefore);
      expect(window.sessionStorage.length).toBe(sessionBefore);
    });

    it('does not survive a new service instance, which is the accepted cost of holding it in memory', () => {
      service.store(sessionExpiringAt('2100-01-01T00:00:00.000Z'));

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({});

      expect(TestBed.inject(TokenStorageService).session())
        .withContext('a fresh injector holds no session')
        .toBeNull();
    });
  });

  describe('sessionFromLoginResponse', () => {
    it('keeps the single absolute expiry and every advisory flag', () => {
      const response: LoginResponse = {
        accessToken: 'access-9',
        expiresAtUtc: '2100-01-01T00:00:00.000Z',
        refreshToken: 'refresh-9',
        mustChangePassword: true,
        passwordExpiring: true,
        user: USER,
      };

      const session = sessionFromLoginResponse(response);

      expect(session.accessToken).toBe('access-9');
      expect(session.expiresAtUtc).toBe('2100-01-01T00:00:00.000Z');
      expect(session.refreshToken).toBe('refresh-9');
      expect(session.user).toBe(USER);

      // Carried through so a reload does not lose a prompt the caller has not acted on.
      expect(session.mustChangePassword).toBeTrue();
      expect(session.passwordExpiring).toBeTrue();

      // The server publishes one expiry representation and no bearer-scheme member, so
      // neither a relative lifetime nor a refresh-token expiry can reach stored state. The
      // profile-completeness advisory is absent for a DIFFERENT reason, now that the server
      // has a producer for it: nothing in this application consumes that advisory, so keeping
      // it would put a signal into stored state that no screen can act on. See the note on
      // LoginResponse in auth.model.ts.
      const keys = Object.keys(session);
      expect(keys).not.toContain('expiresIn');
      expect(keys).not.toContain('tokenType');
      expect(keys).not.toContain('refreshTokenExpiresAtUtc');
      expect(keys).not.toContain('mustUpdateProfile');
    });

    it('carries a cleared advisory through as false rather than dropping it', () => {
      // `false` means "no advisory" and is always present on the wire, so there is no third
      // state: a missing key and an explicit false must never become indistinguishable.
      const response: LoginResponse = {
        accessToken: 'access-10',
        expiresAtUtc: '2100-01-01T00:00:00.000Z',
        refreshToken: 'refresh-10',
        mustChangePassword: false,
        passwordExpiring: false,
        user: USER,
      };

      const session = sessionFromLoginResponse(response);
      const keys = Object.keys(session);

      expect(keys).toContain('mustChangePassword');
      expect(keys).toContain('passwordExpiring');
      expect(session.mustChangePassword).toBeFalse();
      expect(session.passwordExpiring).toBeFalse();
    });
  });
});
