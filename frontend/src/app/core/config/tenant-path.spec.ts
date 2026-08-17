import { APP_ROUTES } from '../../app.routes';

import {
  RESERVED_PLATFORM_SEGMENTS,
  RESERVED_TOP_LEVEL_SEGMENTS,
  adoptTenantPathBase,
  appBaseHref,
  candidateTenantPathBase,
  deploymentPathBase,
  deploymentPathBaseFrom,
  detectTenantPathBase,
  forgetTenantPathBase,
  tenantPathBase,
} from './tenant-path';

/**
 * Specification for the mount point the application is served beneath.
 *
 * ⚠ THIS SUITE REPLACES ONE THAT ASSERTED THE OPPOSITE CONTRACT, and the replacement is the point. The
 * previous module derived the mount point from the first segment of the ADDRESS, and these cases pinned
 * that derivation - including, in so many words, that `/child` alone was claimed as a prefix. Runtime
 * measurement proved the derivation broke the route table's `**` fallback for every single-segment address
 * and poisoned every rendered `href`, so the contract itself was wrong and the cases that pinned it are
 * replaced rather than adapted. The rule the suite now holds the module to is the inverse: NOTHING about
 * the address may influence the answer.
 */
describe('the deployment mount point', () => {
  /** Restores the address after a case that arranged one. */
  const originalUrl = window.location.href;

  /** The base element the harness inserts, so each case can remove exactly what it added. */
  let inserted: HTMLBaseElement | null = null;

  /** The document's own base element, whose `href` a case may borrow and must put back. */
  const existing = document.querySelector('base');

  /** The `href` that element arrived with, or `null` when the document declares no base element. */
  const existingHref = existing?.getAttribute('href') ?? null;

  /**
   * Declares the mount point the way a DEPLOYMENT does - by the document's base element - so that the case
   * exercises the real source rather than a substitute for it.
   *
   * @param href The `href` to declare, or `null` to leave the document with no base element at all.
   */
  function declareBaseHref(href: string | null): void {
    if (existing !== null) {
      if (href === null) {
        existing.removeAttribute('href');
      } else {
        existing.setAttribute('href', href);
      }

      return;
    }

    if (href === null) {
      return;
    }

    inserted = document.createElement('base');
    inserted.setAttribute('href', href);
    document.head.appendChild(inserted);
  }

  afterEach(() => {
    history.replaceState({}, '', originalUrl);

    if (inserted !== null) {
      inserted.remove();
      inserted = null;
    }

    if (existing !== null) {
      if (existingHref === null) {
        existing.removeAttribute('href');
      } else {
        existing.setAttribute('href', existingHref);
      }
    }
  });

  describe('deploymentPathBaseFrom', () => {
    it('reports no prefix for a root mount, in every spelling of it', () => {
      expect(deploymentPathBaseFrom('/')).toBe('');
      expect(deploymentPathBaseFrom('')).toBe('');
      expect(deploymentPathBaseFrom('   ')).toBe('');
      expect(deploymentPathBaseFrom('//')).toBe('');
      expect(deploymentPathBaseFrom('http://localhost:4200/')).toBe('');
    });

    it('reports the declared prefix, with or without its trailing slash', () => {
      expect(deploymentPathBaseFrom('/child/')).toBe('/child');
      expect(deploymentPathBaseFrom('/child')).toBe('/child');
      expect(deploymentPathBaseFrom('child/')).toBe('/child');
      expect(deploymentPathBaseFrom('/acme-legal/')).toBe('/acme-legal');
      expect(deploymentPathBaseFrom('http://localhost:4200/child/')).toBe('/child');
    });

    it('preserves a multi-segment mount point, which a reverse proxy may legitimately declare', () => {
      expect(deploymentPathBaseFrom('/apps/dnn-admin/')).toBe('/apps/dnn-admin');
    });

    it('preserves the case the deployment authored, because a stored alias may carry any', () => {
      expect(deploymentPathBaseFrom('/Acme-Legal/')).toBe('/Acme-Legal');
    });

    it('normalises doubled separators rather than passing them through', () => {
      // A doubled separator in a request path is a different URL to most servers, so the prefix that other
      // URLs are built from must not carry one.
      expect(deploymentPathBaseFrom('/child//')).toBe('/child');
      expect(deploymentPathBaseFrom('/apps//dnn-admin/')).toBe('/apps/dnn-admin');
    });

    it('reports no local prefix for a PROTOCOL-RELATIVE base, which names another origin', () => {
      // `//host/` is a URL whose authority is `host`, not a path beginning with two slashes. A base element
      // pointing at another origin mounts nothing locally, so there is no prefix to prepend to a
      // root-relative API path - and prepending the foreign host's name would compose an address this
      // application must never request.
      expect(deploymentPathBaseFrom('//child/')).toBe('');
      expect(deploymentPathBaseFrom('//child//')).toBe('');
    });

    it('answers a root mount for a value no browser would accept, rather than throwing', () => {
      // A malformed base must not stop the application from booting.
      expect(deploymentPathBaseFrom('http://')).toBe('');
    });
  });

  describe('deploymentPathBase and appBaseHref', () => {
    it("read the document's declared base element", () => {
      declareBaseHref('/child/');

      expect(deploymentPathBase()).toBe('/child');
      expect(appBaseHref()).toBe('/child/');
    });

    it("answers the root for a root deployment, and never the empty string as a base href", () => {
      declareBaseHref('/');

      expect(deploymentPathBase()).toBe('');
      expect(appBaseHref())
        .withContext("'/' rather than empty: the location strategy requires a non-empty base")
        .toBe('/');
    });

    it('IGNORES THE ADDRESS ENTIRELY, which is the whole contract', () => {
      declareBaseHref('/');

      // Every one of these addresses was previously read as a mount point. The unknown single segment is
      // the measured defect: claiming it left the router an empty URL, so the route table's `**` fallback
      // was never consulted and a mistyped address resolved to the caller's landing screen.
      for (const address of [
        '/this-route-does-not-exist',
        '/nope',
        '/nope/deeper',
        '/child',
        '/child/portals',
        '/acme-legal/users/1/profile',
      ]) {
        history.replaceState({}, '', address);

        expect(deploymentPathBase()).withContext(`address ${address}`).toBe('');
        expect(appBaseHref()).withContext(`address ${address}`).toBe('/');
      }
    });

    it('treats a document with no declared base element as the root mount', () => {
      // NOT as `document.baseURI`, which the platform resolves to the current address: that fallback would
      // reinstate the very defect this module exists to end.
      declareBaseHref(null);
      history.replaceState({}, '', '/nope/deeper');

      expect(deploymentPathBase()).toBe('');
      expect(appBaseHref()).toBe('/');
    });

    it('leaves every route in the table reachable, whatever its first segment spells', () => {
      // The previous module carried a reserved-segment list precisely because a new top-level route could
      // otherwise be read as a tenant. Nothing is reserved now, because nothing is inferred, so this case
      // asserts the property that list existed to protect rather than the list itself.
      declareBaseHref('/');

      const topLevelPaths = APP_ROUTES.map((route) => route.path ?? '').filter(
        (path) => path.length > 0 && path !== '**',
      );

      expect(topLevelPaths.length).toBeGreaterThan(0);

      for (const path of topLevelPaths) {
        history.replaceState({}, '', `/${path}`);

        expect(deploymentPathBase()).withContext(`route ${path}`).toBe('');
      }
    });
  });
});

/** Specification for the tenant path prefix a child portal is addressed beneath. */
describe('the tenant path prefix', () => {
  /**
   * Restores the address after a case that arranged one. `history.replaceState` rather than a stub on
   * `window.location`: the platform's own history API changes the very value the module under test reads,
   * so the case exercises the real derivation rather than a substitute for it.
   */
  const originalUrl = window.location.href;

  /**
   * The document's own base element, and the `href` it arrived with.
   *
   * ⚠ NEUTRALISED FOR EVERY CASE IN THIS SUITE, BECAUSE THE TWO PREFIXES COMPOSE. `appBaseHref` is the
   * deployment's mount point followed by the confirmed tenant segment, so a runner whose page declared a
   * mount point of its own would make every expectation below read `/runner-mount/child/` instead of
   * `/child/`. These cases are about the TENANT half, so the deployment half is pinned at the root - which
   * is also what the shipped container serves - and restored afterwards.
   */
  const existingBase = document.querySelector('base');

  /** The `href` that element arrived with, or `null` when the document declares no base element. */
  const existingBaseHref = existingBase?.getAttribute('href') ?? null;

  beforeEach(() => {
    existingBase?.setAttribute('href', '/');
  });

  afterEach(() => {
    history.replaceState({}, '', originalUrl);

    if (existingBase !== null) {
      if (existingBaseHref === null) {
        existingBase.removeAttribute('href');
      } else {
        existingBase.setAttribute('href', existingBaseHref);
      }
    }

    // The recorded decision is MODULE state and outlives a TestBed, so a case that arranges one has to undo
    // it or every case after it would read that case's answer.
    forgetTenantPathBase();
  });

  describe('the reserved segment set', () => {
    /** The top-level paths the route table declares, excluding the two that name no segment. */
    function declaredTopLevelSegments(): readonly string[] {
      return APP_ROUTES.map((route) => route.path ?? '')
        .filter((path) => path.length > 0 && path !== '**')
        .map((path) => path.split('/')[0].toLowerCase());
    }

    it('is exactly the set of first segments the route table declares', () => {
      const declared = [...new Set(declaredTopLevelSegments())].sort();

      // Compared as SETS rather than by length or by spot checks: a missing member makes the application's
      // own screen look like a tenant, and a surplus member makes a real tenant whose segment spells it
      // unreachable. Both directions matter, so both are asserted by one equality over sorted values.
      expect([...RESERVED_TOP_LEVEL_SEGMENTS].sort()).toEqual(declared);
    });

    it('holds every member in lower case, because the comparison folds case', () => {
      for (const segment of RESERVED_TOP_LEVEL_SEGMENTS) {
        expect(segment).withContext(`reserved segment ${segment}`).toBe(segment.toLowerCase());
      }
    });

    it('excludes the redirect and the fallback, which name no segment at all', () => {
      expect(RESERVED_TOP_LEVEL_SEGMENTS).not.toContain('');
      expect(RESERVED_TOP_LEVEL_SEGMENTS).not.toContain('**');
    });
  });

  describe('detectTenantPathBase', () => {
    it('reports no prefix for the bare root', () => {
      // The ordinary single-tenant deployment, and the address a bare-host alias is served at.
      expect(detectTenantPathBase('/')).toBe('');
      expect(detectTenantPathBase('')).toBe('');
      expect(detectTenantPathBase('//')).toBe('');
    });

    it("reports no prefix for the application's own screens", () => {
      expect(detectTenantPathBase('/login')).toBe('');
      expect(detectTenantPathBase('/portals')).toBe('');
      expect(detectTenantPathBase('/portals/0/settings')).toBe('');
      expect(detectTenantPathBase('/users/1/password')).toBe('');
      expect(detectTenantPathBase('/settings/membership')).toBe('');
      expect(detectTenantPathBase('/role-groups/new')).toBe('');
    });

    it('folds case when recognising its own screens, because an address may be typed', () => {
      expect(detectTenantPathBase('/PORTALS')).toBe('');
      expect(detectTenantPathBase('/Users/1')).toBe('');
    });

    it('reports the first segment for a tenant address, with or without a trailing path', () => {
      // The three shapes a child tenant is reached at: its own root, its root with a trailing
      // separator, and a deep link into one of the console's screens beneath it.
      expect(detectTenantPathBase('/child')).toBe('/child');
      expect(detectTenantPathBase('/child/')).toBe('/child');
      expect(detectTenantPathBase('/child/portals')).toBe('/child');
      expect(detectTenantPathBase('/acme-legal/users/1/profile')).toBe('/acme-legal');
    });

    it('preserves the segment verbatim rather than folding its case', () => {
      // The alias is stored as it was authored and the API matches it under the database's own
      // collation, so imposing a case here would only hide a collation an operator chose.
      expect(detectTenantPathBase('/Acme-Legal/portals')).toBe('/Acme-Legal');
    });

    it('honours ONE segment, matching the depth the proxy and the legacy screen support', () => {
      // The legacy signup screen composes `domain/segment` - one segment - and the reverse proxy's matcher
      // admits one. Reading two here would put the browser and the proxy into disagreement rather than
      // supporting a deeper tenant.
      expect(detectTenantPathBase('/one/two/portals')).toBe('/one');
    });

    it('reports no prefix for an address that names a document or an asset', () => {
      expect(detectTenantPathBase('/context.html')).toBe('');
      expect(detectTenantPathBase('/debug.html')).toBe('');
      expect(detectTenantPathBase('/main-ABCD1234.js')).toBe('');
      expect(detectTenantPathBase('/styles-ABCD1234.css')).toBe('');
      expect(detectTenantPathBase('/favicon.ico')).toBe('');

      // And the extensions no closed list could have anticipated, which is the point of the
      // change: each of these was read as a TENANT before, because the list did not name it.
      expect(detectTenantPathBase('/robots.txt')).toBe('');
      expect(detectTenantPathBase('/site.webmanifest')).toBe('');
      expect(detectTenantPathBase('/legacy.aspx')).toBe('');
    });

    it('refuses a segment carrying a dot, because the server cannot store one', () => {
      // ⚠ A DOTTED SEGMENT IS NOT A TENANT PREFIX, however legal a dot is in an alias path. Reading
      // `/acme.co` as one is the tempting inverse of this expectation, and the server cannot store such a
      // path.
      expect(detectTenantPathBase('/acme.co')).toBe('');
      expect(detectTenantPathBase('/acme.co/roles')).toBe('');
    });

    it("reports no prefix for one of the server's own roots", () => {
      // The four platform segments. `api` is the one that matters most: every request this application
      // makes is addressed beneath it, so reading it as a tenant would compose `/api/api/v1/...` and
      // nothing would answer.
      expect(detectTenantPathBase('/api/v1/portals')).toBe('');
      expect(detectTenantPathBase('/health')).toBe('');
      expect(detectTenantPathBase('/health/ready')).toBe('');
      expect(detectTenantPathBase('/openapi/v1.json')).toBe('');
      expect(detectTenantPathBase('/swagger/index.html')).toBe('');
      expect(detectTenantPathBase('/API/v1/portals')).toBe('');
    });

    it('refuses a segment carrying a character the alias vocabulary excludes', () => {
      // A segment the server could not have stored cannot name a tenant, so anything outside
      // letters, digits, hyphen and underscore belongs to the application's own address space.
      expect(detectTenantPathBase('/child%20portal')).toBe('');
      expect(detectTenantPathBase('/~child')).toBe('');
      expect(detectTenantPathBase('/child!')).toBe('');
      expect(detectTenantPathBase('/.well-known/security.txt')).toBe('');
    });

    it('still admits the segments the server CAN store', () => {
      // The other half of the tightening: nothing that was addressable stopped being so.
      expect(detectTenantPathBase('/child')).toBe('/child');
      expect(detectTenantPathBase('/acme-legal')).toBe('/acme-legal');
      expect(detectTenantPathBase('/acme_legal')).toBe('/acme_legal');
      expect(detectTenantPathBase('/tenant7')).toBe('/tenant7');
    });
  });

  describe('the platform segment set', () => {
    it('holds the roots the API answers, in lower case', () => {
      // ⚠ HELD APART FROM THE ROUTE-DERIVED SET ON PURPOSE. The assertion above pins that set to be EXACTLY
      // the route table's own top-level paths, which is what makes it trustworthy; folding these four into
      // it would break that assertion.
      expect([...RESERVED_PLATFORM_SEGMENTS].sort()).toEqual([
        'api',
        'health',
        'openapi',
        'swagger',
      ]);

      for (const segment of RESERVED_PLATFORM_SEGMENTS) {
        expect(segment).withContext(`platform segment ${segment}`).toBe(segment.toLowerCase());
      }
    });

    it('shares no member with the route-derived set, so neither can mask the other', () => {
      for (const segment of RESERVED_PLATFORM_SEGMENTS) {
        expect(RESERVED_TOP_LEVEL_SEGMENTS)
          .withContext(`platform segment ${segment} is not also a route`)
          .not.toContain(segment);
      }
    });
  });

  /**
   * ⚠ THESE CASES USED TO ASSERT THAT `tenantPathBase` READ THE LIVE ADDRESS, AND THAT WAS THE DEFECT
   * WRITTEN DOWN AS A REQUIREMENT. Reading the address means believing it, and a mistyped one-segment
   * address is indistinguishable from a real child alias by inspection alone. The function now answers the
   * prefix the SERVER confirmed, so each case states which answer is in hand before asking what the
   * application does with it.
   */
  describe('tenantPathBase and appBaseHref', () => {
    afterEach(() => {
      forgetTenantPathBase();
    });

    it('answers the confirmed prefix, and agrees with it rather than with the address', () => {
      history.replaceState({}, '', '/child/portals');
      adoptTenantPathBase('/child');

      expect(tenantPathBase()).toBe('/child');
      expect(appBaseHref()).toBe('/child/');
    });

    it('holds the confirmed answer across a navigation within the same tenant', () => {
      adoptTenantPathBase('/child');
      history.replaceState({}, '', '/child/portals');
      expect(tenantPathBase()).toBe('/child');

      // What the router does when a link is followed. The answer is now stable BECAUSE it is held rather
      // than re-derived, so no address the router pushes can change it for the life of the document.
      history.replaceState({}, '', '/child/users/1/profile');
      expect(tenantPathBase()).toBe('/child');
    });

    it("answers the document's own base for a root deployment", () => {
      history.replaceState({}, '', '/portals');
      adoptTenantPathBase('');

      expect(tenantPathBase()).toBe('');
      expect(appBaseHref())
        .withContext("'/' rather than empty: the location strategy requires a non-empty base")
        .toBe('/');
    });

    // A CASE PINNING "NO PREFIX UNTIL SOMETHING IS CONFIRMED" STOOD HERE, AND THE PRE-DECISION ANSWER IS NOW
    // THE CANDIDATE INSTEAD. Both readings close the same defect - acting on an unconfirmed segment - and they
    // differ only in what is answered during a window nothing observes: `main.ts` AWAITS the decision before
    // the application is created, and the decision is recorded for every outcome including rejection and an
    // unreachable API, so no route is resolved and no request is composed while the fallback is what answers.
    // The candidate is the better answer for the two places the window is real: a specification that arranges
    // no decision, and a deployment whose API could not be reached, where demoting a genuine child portal to
    // the root would address every later request to the wrong tenant. What the defect actually needed - that a
    // REJECTED candidate stays rejected even though the address goes on proposing it - is pinned by 'the
    // recorded decision' below, which states it over the mistyped address that was measured.
  });

  // A SUITE FOR A SECOND PROBE STOOD HERE, AND THE PROBE IT SPECIFIED IS GONE RATHER THAN ITS CONTRACT.
  // Two probes were written for the one question a client cannot answer for itself - is this first segment a
  // stored alias? - and only one may run, or a document would issue two requests before first paint and the
  // two could disagree about the same address. The surviving probe is `tenant-resolution.ts`, and
  // `tenant-resolution.spec.ts` states every reply it can receive: the candidate confirmed, the candidate
  // rejected, another prefix reported, a refusal, a transient status, a timeout and a transport failure -
  // including the asymmetry this module's own probe did not have, that only a DEFINITE answer demotes a
  // candidate. What remains specified here is what this module owns: the candidate, the recorded decision,
  // and the base href the two compose into.

  describe('the recorded decision', () => {
    it('is what tenantPathBase answers once one exists, in place of the candidate', () => {
      history.replaceState({}, '', '/child/portals');

      // What resolution records for an address whose segment the deployment CONFIRMED.
      adoptTenantPathBase('/child');

      expect(tenantPathBase()).toBe('/child');
      expect(appBaseHref()).toBe('/child/');
    });

    it('answers the root for a REJECTED candidate, even though the address still proposes one', () => {
      // ⚠ THE CASE THE WHOLE ARRANGEMENT EXISTS FOR. A mistyped console route keeps its segment in the
      // address bar - deliberately, so the mistake stays visible and the catch-all can render for it - so
      // the address goes on proposing a candidate for the life of the document. Reading the address here
      // would re-adopt the very segment the deployment had just refused, and every API URL composed
      // afterwards would carry it.
      history.replaceState({}, '', '/portls');

      expect(candidateTenantPathBase())
        .withContext('the address still proposes the mistyped segment')
        .toBe('/portls');

      adoptTenantPathBase('');

      expect(tenantPathBase())
        .withContext('the recorded rejection wins over the address')
        .toBe('');
      expect(appBaseHref())
        .withContext('so the router resolves the full mistyped path against the root and matches **')
        .toBe('/');
    });

    it('is forgotten again, and the answer returns to NO prefix rather than to the address', () => {
      history.replaceState({}, '', '/child/portals');
      adoptTenantPathBase('/child');
      expect(tenantPathBase()).toBe('/child');

      forgetTenantPathBase();

      // ⚠ NOT BACK TO THE CANDIDATE, AND THE DIFFERENCE IS THE WHOLE POINT OF THE REWRITE. The unrecorded
      // state is the one the defect exploited, and the address is exactly as convincing there as it was
      // before anything asked the deployment. It also has to answer this way for the sibling contract to
      // hold: the mount point IGNORES the address entirely, and a tenant fallback read from the address
      // would put a mistyped segment straight back into `appBaseHref()` and into every composed URL.
      //
      // Keeping a candidate when the deployment could not be reached is still done - `tenant-resolution.ts`
      // RECORDS the candidate for an inconclusive answer, deliberately, so an outage cannot strand a real
      // child portal at the root. That is a decision this module is told, never one it infers.
      expect(tenantPathBase())
        .withContext('an unrecorded prefix is no prefix, so nothing is claimed on the address alone')
        .toBe('');
      expect(candidateTenantPathBase())
        .withContext('the candidate is still derivable, and is still only a candidate')
        .toBe('/child');
    });
  });

  describe('candidateTenantPathBase', () => {
    it('reads the live address and agrees with the pure derivation', () => {
      history.replaceState({}, '', '/child/users/1/profile');

      expect(candidateTenantPathBase()).toBe(detectTenantPathBase('/child/users/1/profile'));
      expect(candidateTenantPathBase()).toBe('/child');
    });

    it('proposes nothing for the console\'s own routes', () => {
      history.replaceState({}, '', '/roles');

      expect(candidateTenantPathBase()).toBe('');
    });
  });
});
