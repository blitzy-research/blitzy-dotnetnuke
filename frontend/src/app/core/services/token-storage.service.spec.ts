import { TestBed } from '@angular/core/testing';

// Type-only, matching the service under test and the convention already used by the
// sibling utility specifications. The session shape is erased at compile time, so this
// specification pulls no runtime code out of the model and cannot be the reason a model
// export appears in the test bundle.
import type { AuthSession, CurrentUser } from '../models/auth.model';

import { TokenStorageService } from './token-storage.service';

/**
 * Specification for {@link TokenStorageService}.
 *
 * ## What is under test, and what deliberately is not
 *
 * The unit is the service and nothing else. It performs no HTTP, so no client provider,
 * no testing-transport provider and no request-verifying stub appear below — configuring
 * any of them would advertise a transport dependency this service does not have. The
 * bearer header, renewal on a 401 and refresh orchestration belong to the authentication
 * interceptor's own specification; the sign-in and renewal calls belong to
 * `auth.service.spec.ts`. Neither is exercised here.
 *
 * `sessionFromLoginResponse` is likewise not tested here. It is a pure projection
 * declared in `core/models/auth.model.ts`, so its coverage belongs with that model
 * rather than with this service; `auth.service.ts` routes both sign-in and renewal
 * through it, so `auth.service.spec.ts` exercises it in place. Sessions below are built
 * directly as {@link AuthSession} literals, which is exactly what that projection
 * produces.
 *
 * ## The load-bearing expectation
 *
 * `persistence posture` is the reason this file matters more than a getter round-trip
 * would justify. The migration plan requires token custody to be memory-first, and that
 * requirement is otherwise unfalsifiable — a service can claim it in a comment while
 * quietly writing to web storage. Those expectations measure the claim: after a session
 * is held, local storage, session storage and the cookie jar must contain no trace of
 * it. Web storage is readable by any script on the origin AND it survives a reload, so a
 * refresh token left there outlives the tab that obtained it; a cookie would additionally
 * be attached to every same-origin request, reintroducing the request-forgery surface
 * that bearer tokens exist to avoid.
 *
 * The cost is asserted rather than glossed: a fresh injector holds no session, which is
 * the sign-out-on-reload behaviour that memory-first custody accepts by design.
 *
 * ## Fixtures
 *
 * Every token value is transparently a placeholder — `fake-access-token`,
 * `fake-refresh-token`. Nothing here is shaped like a real credential: no JSON Web Token
 * with a decodable payload, no key material, no value that a secret scanner should have
 * to reason about. The legacy configuration this migration replaces committed a live
 * symmetric key to source control (`Website/release.config:L91`, a 3DES
 * `decryptionKey`), which is the habit these fixtures are chosen against. No expectation
 * logs a token, and this file makes no logging call of any kind.
 *
 * MIGRATION: the sixty-minute lifetime referenced by the fixtures is parity with the
 * legacy forms-authentication ticket — `Website/release.config:L147` declares
 * `timeout="60"` on `.DOTNETNUKE`. That ticket was a cookie (`cookieless="UseCookies"`
 * on the same line), so it survived a reload; nothing held by this service does. The
 * divergence is deliberate and is asserted below rather than left to prose.
 */
describe('TokenStorageService', () => {
  /**
   * A signed-in identity carrying two permission keys.
   *
   * Shared and frozen in effect by convention: no expectation mutates it, so a failure
   * cannot be caused by an earlier one having altered it. `portalId` is deliberately 0,
   * which is a legitimate tenant key rather than an absent one — `Portals.PortalID` is
   * declared `IDENTITY(-1, 1)`, so both 0 and -1 are real keys and neither may be
   * treated as "missing".
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
    roles: ['Administrators'],
    permissions: ['VIEW', 'EDIT'],
  };

  /** An expiry far enough ahead that no expectation depends on the current clock. */
  const FAR_FUTURE = '2100-01-01T00:00:00.000Z';

  /**
   * Builds a session, overriding only what an expectation actually varies.
   *
   * Every member of {@link AuthSession} is required and none is optional, so the
   * defaults are spelled out rather than left to inference.
   */
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
    // Cleared before the service is resolved, so the persistence expectations measure
    // what THIS service wrote and not whatever the runner or a previously executed
    // specification happened to leave behind. Nothing else is touched, so there is
    // nothing to restore afterwards.
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
      // The identity matters rather than the equality: two instances would each hold
      // their own session, and an interceptor could then present a token that a guard
      // believed had been discarded.
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
      // The load-bearing part of this expectation is the OFFSET form. A `Date`
      // round-trip would normalise `+00:00` to `Z` and add the millisecond field,
      // yielding '2100-01-01T00:00:00.000Z' — a different string for the same instant.
      // Asserting the original comes back byte-for-byte therefore proves the getter
      // parses nothing, reformats nothing and performs no arithmetic; a weaker fixture
      // already in canonical form could not distinguish the two implementations.
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
      // This is the case that matters. A renewal returns BOTH a new access token and a
      // new refresh token; keeping the consumed one would have it presented again on the
      // next renewal, which the server treats as a replay and answers by revoking the
      // whole token family.
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
        roles: ['Administrators', 'Hosts'],
        permissions: ['VIEW'],
      };

      service.store(aSession());
      service.store(aSession({ user: second }));

      expect(service.currentUser()).withContext('identity').toBe(second);
      expect(service.permissions()).withContext('permissions follow the identity').toEqual(['VIEW']);
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

      // Absolute rather than a before-and-after delta, because the surrounding
      // `beforeEach` empties both stores, so an entry of whatever kind is one this
      // service created.
      expect(localStorage.length).toBe(0);
    });

    it('writes nothing to session storage', () => {
      service.store(aSession());

      expect(sessionStorage.length).toBe(0);
    });

    it('leaks neither token into any local-storage value, whatever the key', () => {
      service.store(aSession());

      // Defence against the case a length check alone would miss: an entry written under
      // an unexpected key, or a whole session serialised into one blob. Every value is
      // read rather than only the keys this specification could think to guess, and the
      // offenders are collected so a failure names what leaked instead of only that
      // something did.
      const leaked = Object.keys(localStorage)
        .map((key) => localStorage.getItem(key))
        .filter((value) => value !== null && value.includes('fake-'));

      expect(leaked).toEqual([]);
    });

    it('leaks neither token into the cookie jar', () => {
      service.store(aSession());

      // A cookie is the worst of the three surfaces: it survives a reload AND is attached
      // automatically to every same-origin request, which reintroduces the request-forgery
      // exposure that presenting a bearer token in a header avoids.
      expect(document.cookie).withContext('access token').not.toContain('fake-access-token');
      expect(document.cookie).withContext('refresh token').not.toContain('fake-refresh-token');
    });

    it('does not survive a fresh injector, which is the accepted cost of holding it in memory', () => {
      service.store(aSession());

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({});

      // The executable form of the divergence from legacy behaviour: the `.DOTNETNUKE`
      // ticket was a cookie and survived a reload for its full sixty minutes, whereas a
      // reload here ends the session and the person signs in again.
      expect(TestBed.inject(TokenStorageService).session()).toBeNull();
    });
  });

  describe('empty-string fidelity', () => {
    // The legacy data layer encoded an absent string as the EMPTY STRING rather than as a
    // null reference — `Library/Components/Shared/Null.vb` L71-L75 returns `""` literally.
    // An empty value and an absent value are therefore distinct facts, and collapsing one
    // into the other is the specific mistake these expectations exist to catch. The
    // service derives each token with `??`, which is null-and-undefined-triggered only;
    // had it been written with `||`, every expectation below would fail, because `''` is
    // falsy. No expectation here uses `??`, `||` or any other coalescing, since doing so
    // would paper over exactly the defect being tested.

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
      // The two states the legacy sentinel could not tell apart, asserted side by side:
      // holding a session whose token is empty is not the same as holding no session, and
      // only the second yields null.
      service.store(aSession({ accessToken: '' }));
      const whileHeld = service.accessToken();

      service.clear();

      expect(whileHeld).withContext('session held, token empty').toBe('');
      expect(service.accessToken()).withContext('no session held').toBeNull();
      expect(whileHeld).withContext('the two states are distinguishable').not.toBe(service.accessToken());
    });

    it('still reports a session as held when its access token is empty', () => {
      // Presence of a session and usability of its token are separate questions. An empty
      // token is a server contract violation, not a sign-out, and the caller that decides
      // what to do about it needs to be able to see that a session exists.
      service.store(aSession({ accessToken: '' }));

      expect(service.isAuthenticated()).toBeTrue();
    });
  });

  describe('isAccessTokenExpired', () => {
    // The service separates the expiry FACT from the expiry VERDICT deliberately, and the
    // two are tested separately for the same reason. `accessTokenExpiresAt` hands back the
    // stamped instant unread — proven above by the offset-form round trip — while this
    // method is the only member that consults a clock, and it takes that clock as an
    // argument. That is what makes both sides of the boundary assertable without waiting
    // for real time to pass, so no clock stubbing, no installed fake timers and no
    // patching of the global date constructor are needed anywhere in this file.

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
      // Failing closed is the only safe direction: an instant that cannot be parsed cannot
      // be shown to be valid, and treating it as valid would present a token the server is
      // certain to reject.
      service.store(aSession({ expiresAtUtc: 'not-an-instant' }));

      expect(service.isAccessTokenExpired(new Date('2024-01-01T00:00:00.000Z')))
        .withContext('unparseable expiry')
        .toBeTrue();
    });

    it('reports expired for an empty expiry, while the getter still reports it as empty', () => {
      // The two halves of the empty-string contract held together. The stored value is
      // returned untouched because that is what was stamped, yet the verdict fails closed
      // because an empty string is not a readable instant. Preserving the value and
      // trusting it are different decisions.
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
      // The default argument is part of the contract: a caller with no opinion about the
      // clock should not have to construct one. A far-future expiry keeps this expectation
      // independent of when it runs, which is why the fixture is dated 2100 rather than
      // relative to now.
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
   * The auth epoch.
   *
   * ⚠ THIS COUNTER IS THE FOUNDATION EVERY ASYNCHRONOUS AUTHENTICATION PATH RESTS ON. The
   * authentication service, the bearer interceptor and the session store each capture it before
   * starting work and compare it before committing anything, so a defect here is a defect in
   * all three at once. That is why its contract is specified at the owner rather than only
   * through its consumers.
   *
   * Two properties matter and both are non-obvious:
   *
   * - EVERY transition advances it, including a session REPLACING another and including a clear
   *   with nothing to clear. Skipping either would leave a window in which stale asynchronous
   *   work observes a matching epoch and commits.
   * - The comparison is exact equality, never an ordering test. "The session changed" is the
   *   only fact being established; how much it changed by is not a question worth answering.
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

    // ⚠ THE LOAD-BEARING CASE. Advancing only when a session was present would leave the second
    // of two successive clears silent, so a renewal that began between them would still see a
    // matching epoch and would resurrect a session that had been ended twice over.
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
});
