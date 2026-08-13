import { APP_ROUTES } from '../../app.routes';

import {
  RESERVED_PLATFORM_SEGMENTS,
  RESERVED_TOP_LEVEL_SEGMENTS,
  appBaseHref,
  detectTenantPathBase,
  tenantPathBase,
} from './tenant-path';

/**
 * Specification for the tenant path prefix a child portal is addressed beneath.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS FILE PROVES, AND WHY EACH HALF IS WORTHLESS WITHOUT THE OTHER
 * ---------------------------------------------------------------------------
 * The legacy product addressed a child portal by a path segment beneath a shared host name -
 * `Website/admin/Portal/Signup.ascx.vb` L232-L236 composes and stores exactly `domain/segment`
 * - and the API resolves the tenant from the host AND the path before routing runs. The
 * browser therefore has to put that segment back onto every API request, and the only thing it
 * can derive it from is the address this document was served at.
 *
 * The derivation rests entirely on the console's route table being CLOSED, because the rule is
 * "a first segment the application does not own belongs to a tenant". So this file asserts two
 * things:
 *
 *   1. THE RESERVED SET IS EXACTLY THE TABLE'S OWN TOP-LEVEL PATHS. Without this, a route added
 *      to the table and not to the set would have its own first segment read as a tenant, and
 *      every API request from that screen would be addressed to a tenant that does not exist.
 *      Nothing else in the build would report it: both values are string arrays that compile
 *      independently.
 *   2. THE DERIVATION ITSELF, case by case, including the two cases that are NOT tenants for
 *      reasons unrelated to the route table - a served document, and the bare root.
 *
 * ---------------------------------------------------------------------------
 * WHY THE ROUTE TABLE IS IMPORTED HERE AND NOT BY THE MODULE UNDER TEST
 * ---------------------------------------------------------------------------
 * `app.routes.ts` reaches every feature route barrel, and each barrel reaches the components it
 * mounts. The module under test is imported by `api-endpoints.ts`, which every service imports,
 * so importing the table THERE would pull the whole route graph into the initial bundle and
 * defeat the lazy loading the initial-payload budget depends on. A specification has no bundle,
 * so it is the right place for the comparison - which is precisely why the duplication is
 * policed rather than removed.
 */
describe('the tenant path prefix', () => {
  /**
   * Restores the address after a case that arranged one.
   *
   * `history.replaceState` rather than a stub on `window.location`: the platform's own history
   * API changes the very value the module under test reads, so the case exercises the real
   * derivation rather than a substitute for it. Restoring is mandatory - the address outlives a
   * single case, and a leaked one would prefix every URL a later suite asserts.
   */
  const originalUrl = window.location.href;

  afterEach(() => {
    history.replaceState({}, '', originalUrl);
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

      // Compared as SETS rather than by length or by spot checks: a missing member makes the
      // application's own screen look like a tenant, and a surplus member makes a real tenant
      // whose segment spells it unreachable. Both directions matter, so both are asserted by
      // one equality over sorted values.
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
      // The legacy signup screen composes `domain/segment` - one segment - and the reverse
      // proxy's matcher admits one. Reading two here would put the browser and the proxy into
      // disagreement rather than supporting a deeper tenant.
      expect(detectTenantPathBase('/one/two/portals')).toBe('/one');
    });

    it('reports no prefix for an address that names a document or an asset', () => {
      // ⚠ THE CASE THAT KEEPS EVERY OTHER SPECIFICATION HONEST. The test harness serves its
      // page at /context.html, whose first segment is not one of the application's own routes;
      // reading it as a tenant would silently prefix every URL every suite in this workspace
      // asserts. The same is true of an asset opened directly, which the proxy answers from disk
      // before the document fallback is ever reached.
      //
      // It holds STRUCTURALLY now rather than by enumeration: every one of these carries a dot,
      // the server's alias vocabulary has no dot in it, so none of them could ever have been
      // stored as a tenant address. The closed list of nineteen extensions this module used to
      // carry is gone with the ambiguity that required it.
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
      // ⚠ THIS EXPECTATION IS THE INVERSE OF THE ONE IT REPLACES, and the inversion is the fix.
      // The earlier specification asserted that `/acme.co` was a tenant prefix, on the reasoning
      // that a dot is legal in an alias path. It no longer is: `PortalAliasTopology` admits only
      // letters, digits, hyphens and underscores in a path segment, and both alias write paths
      // enforce it, so `host/acme.co` cannot be stored and therefore cannot name a tenant.
      // Reading it as one would prefix every request with a segment no alias can match, which
      // under the API's fail-closed resolution resolves NO tenant at all.
      expect(detectTenantPathBase('/acme.co')).toBe('');
      expect(detectTenantPathBase('/acme.co/roles')).toBe('');
    });

    it("reports no prefix for one of the server's own roots", () => {
      // The four platform segments. `api` is the one that matters most: every request this
      // application makes is addressed beneath it, so reading it as a tenant would compose
      // `/api/api/v1/...` and nothing would answer.
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
      // ⚠ HELD APART FROM THE ROUTE-DERIVED SET ON PURPOSE. The assertion above pins that set to
      // be EXACTLY the route table's own top-level paths, which is what makes it trustworthy;
      // folding these four into it would break that assertion. On the server the two are one
      // union - `PortalAliasTopology.ReservedPathSegments` - and a backend test asserts that
      // every prefix the request pipeline exempts from tenant resolution appears in it.
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

  describe('tenantPathBase and appBaseHref', () => {
    it('read the live address, and agree with the pure derivation', () => {
      history.replaceState({}, '', '/child/portals');

      expect(tenantPathBase()).toBe('/child');
      expect(appBaseHref()).toBe('/child/');
    });

    it('follow the address across a navigation within the same tenant', () => {
      history.replaceState({}, '', '/child/portals');
      expect(tenantPathBase()).toBe('/child');

      // What the router does when a link is followed: the prefix is the first segment of every
      // address it pushes, so the answer is stable for the life of the document.
      history.replaceState({}, '', '/child/users/1/profile');
      expect(tenantPathBase()).toBe('/child');
    });

    it("answers the document's own base for a root deployment", () => {
      history.replaceState({}, '', '/portals');

      expect(tenantPathBase()).toBe('');
      expect(appBaseHref())
        .withContext("'/' rather than empty: the location strategy requires a non-empty base")
        .toBe('/');
    });
  });
});
