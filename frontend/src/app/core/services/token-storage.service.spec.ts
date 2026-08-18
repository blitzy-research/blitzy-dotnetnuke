import { TestBed } from '@angular/core/testing';

import type { AuthSession, CurrentUser } from '../models/auth.model';

import { TokenStorageService } from './token-storage.service';

describe('TokenStorageService', () => {
  /**
   * A signed-in identity carrying two permission keys. Shared and frozen in effect by convention: no
   * expectation mutates it, so a failure cannot be caused by an earlier one having altered it.
   */
  const USER: CurrentUser = {
    userId: 7,
    portalId: 0,
    portalName: 'Primary',
    username: 'admin',
    displayName: 'Administrator',
    email: 'admin@example.test',
    isSuperUser: false,
    // FALSE even though the held role is named for administration, which is the whole point of the
    // member existing: the name confers nothing, and only the tenant's own designation does.
    isPortalAdministrator: false,
    mustChangePassword: false,
    mustUpdateProfile: false,
    roles: ['Administrators'],
    permissions: ['VIEW', 'EDIT'],
  };

  /** An expiry far enough ahead that no expectation depends on the current clock. */
  const FAR_FUTURE = '2100-01-01T00:00:00.000Z';

  /** Builds a session, overriding only what an expectation actually varies. */
  function aSession(overrides: Partial<AuthSession> = {}): AuthSession {
    return {
      accessToken: 'fake-access-token',
      expiresAtUtc: FAR_FUTURE,
      refreshToken: 'fake-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: USER,
      ...overrides,
    };
  }

  let service: TokenStorageService;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();

    TestBed.configureTestingModule({});
    service = TestBed.inject(TokenStorageService);
  });

  describe('creation', () => {
    it('is resolvable from the root injector without being declared by a provider', () => {
      // Registered with `providedIn: 'root'`, so no test bed configuration is needed to
      // obtain it. A failure here means the registration was changed.
      expect(service).toBeInstanceOf(TokenStorageService);
    });

    it('is a singleton, so every reader agrees about who is signed in', () => {
      // The identity matters rather than the equality: two instances would each hold their own session, and
      // an interceptor could then present a token that a guard believed had been discarded.
      expect(TestBed.inject(TokenStorageService)).toBe(service);
    });

    it('shares one session across every consumer that resolves it', () => {
      const other = TestBed.inject(TokenStorageService);

      service.store(aSession());

      expect(other.accessToken()).toBe('fake-access-token');
    });
  });

  describe('initial state', () => {
    it('holds no session', () => {
      expect(service.session()).toBeNull();
    });

    it('reports no access token and no refresh token', () => {
      expect(service.accessToken()).withContext('access token').toBeNull();
      expect(service.refreshToken()).withContext('refresh token').toBeNull();
    });

    it('reports no access-token expiry', () => {
      expect(service.accessTokenExpiresAt()).toBeNull();
    });

    it('reports nobody signed in, with no identity', () => {
      expect(service.isAuthenticated()).withContext('no session stored').toBeFalse();
      expect(service.currentUser()).withContext('no identity').toBeNull();
    });

    it('reports an empty permission list rather than null, so consumers iterate unconditionally', () => {
      expect(service.permissions()).toEqual([]);
    });

    it('returns the same permission array instance on repeated reads, so the derived signal is stable', () => {
      // A freshly allocated array per read would make every consumer observe a change on
      // every evaluation, which defeats the point of deriving the value at all.
      expect(service.permissions()).toBe(service.permissions());
    });

    it('reports no blocking profile advisory, which is the correct answer for a gate', () => {
      // The same answer as "no advisory": an unauthenticated caller is stopped by the
      // authentication check rather than by this one.
      expect(service.mustUpdateProfile()).toBeFalse();
    });
  });

  describe('store', () => {
    it('exposes the stored session by reference, without copying or normalising it', () => {
      const stored = aSession();

      service.store(stored);

      expect(service.session()).toBe(stored);
    });

    it('returns each token exactly as supplied', () => {
      service.store(aSession());

      expect(service.accessToken()).withContext('access token').toBe('fake-access-token');
      expect(service.refreshToken()).withContext('refresh token').toBe('fake-refresh-token');
    });

    it('returns the expiry exactly as the server stamped it, with no conversion or reformatting', () => {
      // The load-bearing part of this expectation is the OFFSET form. A `Date` round-trip would normalise
      // `+00:00` to `Z` and add the millisecond field, yielding '2100-01-01T00:00:00.000Z' — a different
      // string for the same instant.
      const stamped = '2100-01-01T00:00:00+00:00';

      service.store(aSession({ expiresAtUtc: stamped }));

      expect(service.accessTokenExpiresAt()).toBe(stamped);
    });

    it('reports somebody signed in, with their identity and permissions', () => {
      service.store(aSession());

      expect(service.isAuthenticated()).withContext('a session was stored').toBeTrue();
      expect(service.currentUser()).withContext('identity').toBe(USER);
      expect(service.permissions()).withContext('permission keys').toEqual(['VIEW', 'EDIT']);
    });

    it('projects the blocking profile advisory so a gate can read one value', () => {
      service.store(aSession({ mustUpdateProfile: true }));

      expect(service.mustUpdateProfile()).toBeTrue();
    });

    it('replaces a previous session, so a rotated refresh token does not linger', () => {
      // This is the case that matters. A renewal returns BOTH a new access token and a new refresh token;
      // keeping the consumed one would have it presented again on the next renewal, which the server treats
      // as a replay and answers by revoking the whole token family.
      service.store(aSession());

      service.store(
        aSession({
          accessToken: 'fake-access-token-rotated',
          refreshToken: 'fake-refresh-token-rotated',
          expiresAtUtc: '2100-06-30T12:00:00.000Z',
        }),
      );

      expect(service.accessToken()).withContext('access token').toBe('fake-access-token-rotated');
      expect(service.refreshToken()).withContext('refresh token').toBe('fake-refresh-token-rotated');
      expect(service.accessTokenExpiresAt())
        .withContext('expiry follows the rotated pair')
        .toBe('2100-06-30T12:00:00.000Z');
    });

    it('replaces the identity as well, so a stale account cannot outlive a re-authentication', () => {
      const second: CurrentUser = {
        userId: 8,
        portalId: -1,
        portalName: 'Secondary',
        username: 'host',
        displayName: 'Host Account',
        email: 'host@example.test',
        isSuperUser: true,
        // A host account administers every tenant, which is what the server reports for one.
        isPortalAdministrator: true,
        mustChangePassword: false,
        mustUpdateProfile: false,
        roles: ['Administrators', 'Hosts'],
        permissions: ['VIEW'],
      };

      service.store(aSession());
      service.store(aSession({ user: second }));

      expect(service.currentUser()).withContext('identity').toBe(second);
      expect(service.permissions()).withContext('permissions follow the identity').toEqual(['VIEW']);
    });
  });

  describe('refreshIdentity', () => {
    /**
     * An identity carrying both blocking obligations, which is the state a mid-session imposition produces
     * and the state the sign-in response could not have described.
     */
    const ENCUMBERED: CurrentUser = {
      ...USER,
      mustChangePassword: true,
      mustUpdateProfile: true,
    };

    it('folds a freshly read identity onto the held session', () => {
      service.store(aSession());

      service.refreshIdentity(ENCUMBERED);

      expect(service.currentUser()).withContext('identity').toBe(ENCUMBERED);
    });

    it('adopts the obligations the identity publishes, which is how a mid-session one is learned', () => {
      // The session began unencumbered, exactly as a sign-in before an administrator imposed anything.
      service.store(aSession());

      expect(service.mustUpdateProfile())
        .withContext('nothing was owed when the session began')
        .toBeFalse();

      service.refreshIdentity(ENCUMBERED);

      expect(service.mustUpdateProfile())
        .withContext('the describe-caller read is the only response that can carry a later obligation')
        .toBeTrue();
      expect(service.session()?.mustChangePassword)
        .withContext('its credential twin is folded by the same call')
        .toBeTrue();
    });

    it('does NOT advance the generation, because neither the credentials nor the account changed', () => {
      service.store(aSession());

      const before = service.generation();

      service.refreshIdentity(ENCUMBERED);

      // ⚠ THE POINT OF THE MEMBER. Every asynchronous authentication path captures the generation before it
      // starts and discards its own result if it has moved, so advancing it here would abort a renewal
      // already in the air for a read that changed nothing about who the caller is.
      expect(service.generation()).withContext('auth epoch').toBe(before);
      expect(service.isCurrentGeneration(before))
        .withContext('work already in flight still belongs to this session')
        .toBeTrue();
    });

    it('leaves both credentials and the expiry exactly as they were', () => {
      service.store(aSession());

      service.refreshIdentity(ENCUMBERED);

      expect(service.accessToken()).withContext('access token').toBe('fake-access-token');
      expect(service.refreshToken()).withContext('refresh token').toBe('fake-refresh-token');
      expect(service.accessTokenExpiresAt()).withContext('expiry').toBe(FAR_FUTURE);
    });

    it('is ignored when no session is held, so a late read cannot resurrect a signed-out account', () => {
      service.refreshIdentity(ENCUMBERED);

      expect(service.isAuthenticated()).withContext('still signed out').toBeFalse();
      expect(service.currentUser()).withContext('no identity').toBeNull();
      expect(service.mustUpdateProfile())
        .withContext('a gate must read no obligation for an account that is not signed in')
        .toBeFalse();
    });

    it('clears an obligation the server no longer reports, so a completed remediation is not re-imposed', () => {
      service.store(aSession({ mustChangePassword: true, mustUpdateProfile: true }));

      service.refreshIdentity(USER);

      expect(service.mustUpdateProfile()).withContext('profile obligation').toBeFalse();
      expect(service.session()?.mustChangePassword).withContext('credential obligation').toBeFalse();
    });

    it('leaves the non-blocking expiry advisory alone, which the identity does not publish', () => {
      service.store(aSession({ passwordExpiring: true }));

      service.refreshIdentity(ENCUMBERED);

      expect(service.session()?.passwordExpiring)
        .withContext('a member the identity carries no opinion about must not be overwritten')
        .toBeTrue();
    });
  });

  describe('clear', () => {
    it('discards the session and every value derived from it', () => {
      service.store(aSession({ mustUpdateProfile: true }));

      service.clear();

      expect(service.session()).withContext('session').toBeNull();
      expect(service.accessToken()).withContext('access token').toBeNull();
      expect(service.refreshToken()).withContext('refresh token').toBeNull();
      expect(service.accessTokenExpiresAt()).withContext('expiry').toBeNull();
      expect(service.currentUser()).withContext('identity').toBeNull();
      expect(service.isAuthenticated()).withContext('authenticated').toBeFalse();
      expect(service.permissions()).withContext('permissions').toEqual([]);
      expect(service.mustUpdateProfile()).withContext('profile advisory').toBeFalse();
    });

    it('is idempotent, so a sign-out racing an expiry needs no guard', () => {
      service.clear();
      service.clear();

      expect(service.session()).toBeNull();
    });
  });

  describe('persistence posture', () => {
    it('writes nothing to local storage', () => {
      service.store(aSession());

      // Absolute rather than a before-and-after delta, because the surrounding `beforeEach` empties both
      // stores, so an entry of whatever kind is one this service created.
      expect(localStorage.length).toBe(0);
    });

    it('writes nothing to session storage', () => {
      service.store(aSession());

      expect(sessionStorage.length).toBe(0);
    });

    it('leaks neither token into any local-storage value, whatever the key', () => {
      service.store(aSession());

      const leaked = Object.keys(localStorage)
        .map((key) => localStorage.getItem(key))
        .filter((value) => value !== null && value.includes('fake-'));

      expect(leaked).toEqual([]);
    });

    it('leaks neither token into the cookie jar', () => {
      service.store(aSession());

      // A cookie is the worst of the three surfaces: it survives a reload AND is attached automatically to
      // every same-origin request, which reintroduces the request-forgery exposure that presenting a bearer
      // token in a header avoids.
      expect(document.cookie).withContext('access token').not.toContain('fake-access-token');
      expect(document.cookie).withContext('refresh token').not.toContain('fake-refresh-token');
    });

    it('does not survive a fresh injector, which is the accepted cost of holding it in memory', () => {
      service.store(aSession());

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({});

      // The executable form of the divergence from legacy behaviour: the `.DOTNETNUKE` ticket was a cookie
      // and survived a reload for its full sixty minutes, whereas a reload here ends the session and the
      // person signs in again.
      expect(TestBed.inject(TokenStorageService).session()).toBeNull();
    });
  });

  describe('empty-string fidelity', () => {
    it('returns a stored empty access token as an empty string, not as null', () => {
      service.store(aSession({ accessToken: '' }));

      expect(service.accessToken()).withContext('empty is not absent').toBe('');
      expect(service.accessToken()).withContext('empty must not become null').not.toBeNull();
    });

    it('returns a stored empty refresh token as an empty string, not as null', () => {
      service.store(aSession({ refreshToken: '' }));

      expect(service.refreshToken()).toBe('');
      expect(service.refreshToken()).not.toBeNull();
    });

    it('returns a stored empty expiry as an empty string, not as null', () => {
      service.store(aSession({ expiresAtUtc: '' }));

      expect(service.accessTokenExpiresAt()).toBe('');
      expect(service.accessTokenExpiresAt()).not.toBeNull();
    });

    it('distinguishes an empty token from the absence of a session', () => {
      // The two states the legacy sentinel could not tell apart, asserted side by side: holding a session
      // whose token is empty is not the same as holding no session, and only the second yields null.
      service.store(aSession({ accessToken: '' }));
      const whileHeld = service.accessToken();

      service.clear();

      expect(whileHeld).withContext('session held, token empty').toBe('');
      expect(service.accessToken()).withContext('no session held').toBeNull();
      expect(whileHeld).withContext('the two states are distinguishable').not.toBe(service.accessToken());
    });

    it('still reports a session as held when its access token is empty', () => {
      // Presence of a session and usability of its token are separate questions. An empty token is a server
      // contract violation, not a sign-out, and the caller that decides what to do about it needs to be
      // able to see that a session exists.
      service.store(aSession({ accessToken: '' }));

      expect(service.isAuthenticated()).toBeTrue();
    });
  });

  describe('isAccessTokenExpired', () => {
    it('reports expired when no session is held, because there is no token to rely on', () => {
      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('no session')
        .toBeTrue();
    });

    it('reports valid one millisecond before the expiry instant', () => {
      service.store(aSession({ expiresAtUtc: '2024-01-01T00:00:10.000Z' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:09.999Z')))
        .withContext('one millisecond before expiry')
        .toBeFalse();
    });

    it('reports expired exactly at the expiry instant, so the boundary itself is not usable', () => {
      // The comparison is inclusive. Stated explicitly because the off-by-one is invisible
      // in the implementation and a later change from `<=` to `<` would otherwise pass.
      service.store(aSession({ expiresAtUtc: '2024-01-01T00:00:10.000Z' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:10.000Z')))
        .withContext('exactly at expiry')
        .toBeTrue();
    });

    it('reports expired one millisecond after the expiry instant', () => {
      service.store(aSession({ expiresAtUtc: '2024-01-01T00:00:10.000Z' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:10.001Z')))
        .withContext('one millisecond after expiry')
        .toBeTrue();
    });

    it('reports expired for an unreadable expiry rather than assuming it lies in the future', () => {
      // Failing closed is the only safe direction: an instant that cannot be parsed cannot be shown to be
      // valid, and treating it as valid would present a token the server is certain to reject.
      service.store(aSession({ expiresAtUtc: 'not-an-instant' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('unparseable expiry')
        .toBeTrue();
    });

    it('reports expired for an empty expiry, while the getter still reports it as empty', () => {
      service.store(aSession({ expiresAtUtc: '' }));

      expect(service.accessTokenExpiresAt()).withContext('value preserved').toBe('');
      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('verdict fails closed')
        .toBeTrue();
    });

    it('accepts an offset-form expiry, which denotes the same instant as the canonical form', () => {
      service.store(aSession({ expiresAtUtc: '2024-01-01T00:00:10+00:00' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:09.999Z')))
        .withContext('before the offset-form expiry')
        .toBeFalse();
    });

    it('reads the clock itself when no instant is supplied', () => {
      service.store(aSession({ expiresAtUtc: FAR_FUTURE }));

      expect(service.isAccessTokenExpired()).withContext('expiry far in the future').toBeFalse();
    });

    it('reports expired without an argument once the session is discarded', () => {
      service.store(aSession());
      service.clear();

      expect(service.isAccessTokenExpired()).toBeTrue();
    });
  });

  /**
   * The auth epoch. ⚠ THIS COUNTER IS THE FOUNDATION EVERY ASYNCHRONOUS AUTHENTICATION PATH RESTS ON. The
   * authentication service, the bearer interceptor and the session store each capture it before starting
   * work and compare it before committing anything, so a defect here is a defect in all three at once.
   * That is why its contract is specified at the owner rather than only through its consumers.
   */
  describe('the auth epoch', () => {
    it('starts at zero, so no work can have been captured under an earlier value', () => {
      expect(service.generation()).toBe(0);
    });

    it('advances when a session is stored', () => {
      service.store(aSession());

      expect(service.generation()).toBe(1);
    });

    // A replacement IS an identity change from the point of view of anything holding a request
    // already in flight, so it must advance like any other transition.
    it('advances when one session replaces another', () => {
      service.store(aSession());
      service.store(aSession({ accessToken: 'fake-access-token-second' }));

      expect(service.generation()).toBe(2);
    });

    it('advances when a session is discarded', () => {
      service.store(aSession());
      service.clear();

      expect(service.generation()).toBe(2);
    });

    // ⚠ THE LOAD-BEARING CASE. Advancing only when a session was present would leave the second of two
    // successive clears silent, so a renewal that began between them would still see a matching epoch and
    // would resurrect a session that had been ended twice over.
    it('advances on a clear even when no session was held', () => {
      service.clear();

      expect(service.generation()).toBe(1);

      service.clear();

      expect(service.generation()).toBe(2);
    });

    it('never decreases across a mixed sequence of transitions', () => {
      const observed: number[] = [service.generation()];

      service.store(aSession());
      observed.push(service.generation());
      service.clear();
      observed.push(service.generation());
      service.store(aSession());
      observed.push(service.generation());
      service.clear();
      observed.push(service.generation());

      expect(observed).toEqual([0, 1, 2, 3, 4]);
    });

    describe('isCurrentGeneration', () => {
      it('accepts a value captured with no transition since', () => {
        const captured = service.generation();

        expect(service.isCurrentGeneration(captured)).toBeTrue();
      });

      it('refuses a value captured before a store', () => {
        const captured = service.generation();

        service.store(aSession());

        expect(service.isCurrentGeneration(captured)).toBeFalse();
      });

      it('refuses a value captured before a clear', () => {
        service.store(aSession());

        const captured = service.generation();

        service.clear();

        expect(service.isCurrentGeneration(captured)).toBeFalse();
      });

      // An ordering test would accept this, and accepting it is the defect: the captured epoch
      // is from the future only if a caller invented it, and inventing one must not pass.
      it('refuses a value that is ahead of the current one', () => {
        expect(service.isCurrentGeneration(service.generation() + 1)).toBeFalse();
      });

      it('refuses a stale value however many transitions have occurred since', () => {
        const captured = service.generation();

        service.store(aSession());
        service.clear();
        service.store(aSession());

        expect(service.isCurrentGeneration(captured)).toBeFalse();
      });
    });

    it('exposes the counter read-only, so nothing outside can invalidate work at will', () => {
      const projection: unknown = service.generation;

      // A writable signal carries both members; a read-only projection carries neither.
      // Presence is exact and needs no cast that would only prove itself.
      expect((projection as { set?: unknown }).set).toBeUndefined();
      expect((projection as { update?: unknown }).update).toBeUndefined();
    });
  });

  // THE RETENTION SET FOR UNACKNOWLEDGED WITHDRAWALS
  // ⚠ WHY THIS SECTION EXISTS AT ALL. Signing out discards the session at once, and the refresh token
  // inside it is the ONLY credential that can end that session on the server — so a withdrawal refused by a
  // rate limit, an outage or a dropped connection needs the credential to survive the discard or the
  // session stays renewable until its absolute expiry with nothing left to withdraw it.

  describe('pending revocations', () => {
    it('holds nothing before any sign-out', () => {
      expect(service.pendingRevocations()).toEqual([]);
    });

    it('returns the same empty array instance on repeated reads, so a derived signal is stable', () => {
      expect(service.pendingRevocations()).toBe(service.pendingRevocations());
    });

    it('retains a credential exactly as supplied', () => {
      service.retainForRevocation('fake-refresh-token');

      expect(service.pendingRevocations()).toEqual(['fake-refresh-token']);
    });

    it('ignores an empty credential, which would be answered 400 and reported as a false residue', () => {
      service.retainForRevocation('');

      expect(service.pendingRevocations()).toEqual([]);
    });

    it('retains several credentials, oldest first', () => {
      // The sequence the review named: each sign-out whose withdrawal was refused leaves its
      // own credential, and none may displace another.
      service.retainForRevocation('first-session');
      service.retainForRevocation('second-session');
      service.retainForRevocation('third-session');

      expect(service.pendingRevocations()).toEqual(['first-session', 'second-session', 'third-session']);
    });

    it('does not retain the same credential twice, so one withdrawal is not posted twice', () => {
      service.retainForRevocation('fake-refresh-token');
      service.retainForRevocation('fake-refresh-token');

      expect(service.pendingRevocations()).toEqual(['fake-refresh-token']);
    });

    it('keeps a repeated credential in its ORIGINAL position rather than moving it to the back', () => {
      // It has been outstanding since the first retention, and the order is what decides which credential
      // is given up when the bound is reached — so re-retaining must not make an old residue look new.
      service.retainForRevocation('first-session');
      service.retainForRevocation('second-session');
      service.retainForRevocation('first-session');

      expect(service.pendingRevocations()).toEqual(['first-session', 'second-session']);
    });

    it('holds at most four, dropping the OLDEST when a fifth arrives', () => {
      // Four is the declared ceiling. The oldest end is given up deliberately: a credential retained
      // earlier is nearer its own absolute expiry, so its residual window is the shortest of the set and
      // abandoning it costs least.
      for (const credential of ['one', 'two', 'three', 'four', 'five']) {
        service.retainForRevocation(credential);
      }

      expect(service.pendingRevocations()).toEqual(['two', 'three', 'four', 'five']);
    });

    it('keeps the ceiling under sustained pressure rather than growing without bound', () => {
      for (let index = 0; index < 40; index += 1) {
        service.retainForRevocation(`credential-${index}`);
      }

      const retained = service.pendingRevocations();

      expect(retained.length).withContext('a live credential set must stay enumerable').toBe(4);
      expect(retained).toEqual(['credential-36', 'credential-37', 'credential-38', 'credential-39']);
    });

    it('retires exactly the credential named and leaves every other one held', () => {
      service.retainForRevocation('first-session');
      service.retainForRevocation('second-session');

      service.releasePendingRevocation('first-session');

      expect(service.pendingRevocations())
        .withContext('one withdrawal being confirmed does not retire another session\'s residue')
        .toEqual(['second-session']);
    });

    it('reports nothing outstanding once the last credential is retired', () => {
      service.retainForRevocation('fake-refresh-token');
      service.releasePendingRevocation('fake-refresh-token');

      expect(service.pendingRevocations()).toEqual([]);
      expect(service.pendingRevocations())
        .withContext('and returns to the shared empty instance')
        .toBe(service.pendingRevocations());
    });

    it('is idempotent, so a withdrawal path that runs twice needs no guard', () => {
      service.retainForRevocation('fake-refresh-token');

      service.releasePendingRevocation('fake-refresh-token');
      service.releasePendingRevocation('fake-refresh-token');

      expect(service.pendingRevocations()).toEqual([]);
    });

    it('leaves the set untouched when asked to retire a credential it never held', () => {
      service.retainForRevocation('fake-refresh-token');

      const before = service.pendingRevocations();

      service.releasePendingRevocation('a-credential-from-another-tab');

      expect(service.pendingRevocations())
        .withContext('not even replaced by an equal copy, so no reader is woken for nothing')
        .toBe(before);
    });

    // ---------------------------------------------------------------------
    // ⚠ THE INVARIANT THE WHOLE MECHANISM RESTS ON
    // ---------------------------------------------------------------------

    it('survives a clear, which is the entire reason it is not part of the session', () => {
      service.store(aSession({ refreshToken: 'fake-refresh-token' }));
      service.retainForRevocation('fake-refresh-token');

      service.clear();

      expect(service.refreshToken())
        .withContext('the session is gone, as signing out requires')
        .toBeNull();
      expect(service.pendingRevocations())
        .withContext('but the only credential that can end it on the server is not')
        .toEqual(['fake-refresh-token']);
    });

    it('survives a later sign-in, so the previous session\'s residue is not lost to it', () => {
      service.retainForRevocation('first-session');

      service.store(aSession({ refreshToken: 'second-session' }));

      expect(service.pendingRevocations()).toEqual(['first-session']);
    });

    it('retains the second session\'s credential ALONGSIDE the first, not instead of it', () => {
      service.store(aSession({ refreshToken: 'first-session' }));
      service.retainForRevocation('first-session');
      service.clear();

      service.store(aSession({ refreshToken: 'second-session' }));
      service.retainForRevocation('second-session');
      service.clear();

      expect(service.pendingRevocations()).toEqual(['first-session', 'second-session']);
    });

    it('advances no generation, because retention is not a session transition', () => {
      // The counter is what every late callback tests itself against, so moving it here would
      // invalidate in-flight work for a reason that has nothing to do with the session.
      const captured = service.generation();

      service.retainForRevocation('fake-refresh-token');
      service.releasePendingRevocation('fake-refresh-token');

      expect(service.generation()).toBe(captured);
      expect(service.isCurrentGeneration(captured)).toBeTrue();
    });

    it('exposes the set read-only, so nothing outside can retire a residue at will', () => {
      const projection: unknown = service.pendingRevocations;

      expect((projection as { set?: unknown }).set).toBeUndefined();
      expect((projection as { update?: unknown }).update).toBeUndefined();
    });

    it('hands out a frozen array, so a consumer cannot retire a residue by mutating it', () => {
      service.retainForRevocation('fake-refresh-token');

      const retained = service.pendingRevocations();

      expect(Object.isFrozen(retained)).toBeTrue();
      expect(() => (retained as string[]).pop())
        .withContext('a frozen array refuses mutation under the strict mode Angular compiles to')
        .toThrowError(TypeError);
      expect(service.pendingRevocations()).toEqual(['fake-refresh-token']);
    });

    it('writes no retained credential to any persistent tier', () => {
      // The same posture as the session itself: a retained credential is a live refresh token,
      // and persisting one would outlive the tab that obtained it.
      service.retainForRevocation('fake-refresh-token');

      const localValues = Object.keys(localStorage).map((key) => localStorage.getItem(key) ?? '');
      const sessionValues = Object.keys(sessionStorage).map((key) => sessionStorage.getItem(key) ?? '');

      expect(localValues.some((value) => value.includes('fake-refresh-token'))).toBeFalse();
      expect(sessionValues.some((value) => value.includes('fake-refresh-token'))).toBeFalse();
      expect(document.cookie.includes('fake-refresh-token')).toBeFalse();
    });
  });
});
