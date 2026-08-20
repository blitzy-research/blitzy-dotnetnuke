import { environment } from '../../../environments/environment';

import {
  API_ENDPOINTS,
  AUTH_ENDPOINTS,
  anonymousAuthEndpoints,
  apiUrl,
  isAnonymousAuthEndpoint,
  isApiRequest,
} from './api-endpoints';
import { adoptTenantPathBase, forgetTenantPathBase } from './tenant-path';

/**
 * The exact production API base, spelled out here rather than read from the module under test. ⚠ THE
 * LITERAL IS THE WHOLE POINT AND MUST NOT BE REPLACED BY A REFERENCE. Comparing `environment.apiBaseUrl`
 * against itself, or against anything derived from it, would pass for every value it could ever hold —
 * including each of the three absolute values that break the deployed application.
 */
const PRODUCTION_API_BASE = '/api/v1';

describe('the production API base', () => {
  // `docker/nginx.conf` proxies `location /api/` to `http://api:8080/api/` on the origin that served the
  // bundle, so the browser must address the API through a ROOT-RELATIVE path.

  it('is exactly the root-relative path the reverse proxy serves', () => {
    expect(environment.apiBaseUrl).toBe(PRODUCTION_API_BASE);
  });

  it('is the production module rather than the development override', () => {
    // Pins the OTHER permitted divergence between the twins, so a specification that accidentally compiled
    // against the override could not pass the case above by coincidence.
    expect(environment.production).toBeTrue();
  });

  it('is root-relative and carries no origin of any kind', () => {
    expect(environment.apiBaseUrl.startsWith('/')).toBeTrue();
    expect(environment.apiBaseUrl.startsWith('//')).toBeFalse();
    expect(/^[a-z][a-z0-9+.-]*:/i.test(environment.apiBaseUrl)).toBeFalse();
  });

  it('composes every generated address as a root-relative path beneath that base', () => {
    // The base being correct is necessary but not sufficient: the endpoint registry could still compose an
    // absolute address from it. These are the concrete addresses the application issues, sampled across
    // every collection plus the anonymous auth paths.
    const generated: readonly string[] = [
      apiUrl('portals'),
      API_ENDPOINTS.portals.collection(),
      API_ENDPOINTS.portals.byId(-1),
      API_ENDPOINTS.users.collection(),
      API_ENDPOINTS.users.byId(0),
      API_ENDPOINTS.roles.forCurrentPortal.collection(),
      API_ENDPOINTS.roles.forCurrentPortal.members(0),
      API_ENDPOINTS.modules.collection(),
      API_ENDPOINTS.tabs.forPortal(-1),
      AUTH_ENDPOINTS.login(),
      AUTH_ENDPOINTS.refresh(),
      AUTH_ENDPOINTS.logout(),
      AUTH_ENDPOINTS.me(),
    ];

    for (const address of generated) {
      expect(address.startsWith(`${PRODUCTION_API_BASE}/`))
        .withContext(`generated address ${address}`)
        .toBeTrue();
      expect(/^[a-z][a-z0-9+.-]*:/i.test(address))
        .withContext(`generated address ${address} must carry no scheme`)
        .toBeFalse();
      expect(address.startsWith('//'))
        .withContext(`generated address ${address} must carry no authority`)
        .toBeFalse();
    }
  });
});

describe('isApiRequest', () => {
  const documentUrl = new URL(document.baseURI);

  it('accepts relative API paths at the configured origin', () => {
    expect(isApiRequest('/api/v1')).toBeTrue();
    expect(isApiRequest('/api/v1/')).toBeTrue();
    expect(isApiRequest('/api/v1/portals/0/modules/0')).toBeTrue();
  });

  it('accepts an absolute URL only when its origin exactly matches the configured origin', () => {
    const sameOrigin = new URL('/api/v1/portals/0', documentUrl);
    const differentPort = new URL(sameOrigin.toString());
    differentPort.port = documentUrl.port === '65534' ? '65533' : '65534';

    expect(isApiRequest(sameOrigin.toString())).toBeTrue();
    expect(isApiRequest(differentPort.toString())).toBeFalse();
  });

  it('ignores query strings and fragments when classifying an API path', () => {
    expect(isApiRequest('/api/v1/users?query=/outside#results')).toBeTrue();
    expect(isApiRequest('/outside?next=/api/v1/users#api/v1')).toBeFalse();
  });

  it('rejects a hostile absolute URL that merely contains the configured path', () => {
    expect(isApiRequest('https://attacker.example/api/v1/users')).toBeFalse();
    expect(isApiRequest('https://attacker.example/redirect/api/v1/users')).toBeFalse();
    expect(isApiRequest('https://attacker.example/?next=/api/v1/users')).toBeFalse();
  });

  it('requires a segment boundary after the configured API version', () => {
    expect(isApiRequest('/api/v10/users')).toBeFalse();
    expect(isApiRequest('/api/v1-preview/users')).toBeFalse();
    expect(isApiRequest('/prefix/api/v1/users')).toBeFalse();
  });

  it('fails closed for text that cannot be parsed as a URL', () => {
    expect(isApiRequest('http://[invalid-host')).toBeFalse();
  });
});

describe('a child portal addressed beneath a path segment', () => {
  // The legacy product let a child portal be reached at `domain/segment` - the signup screen composes and
  // stores exactly that - and the API reproduces it: `TenantPathBaseMiddleware` resolves the tenant from
  // the host AND the path before routing runs, then moves the segment into the request's path base so
  // `/child/api/v1/portals` routes to the same action as `/api/v1/portals` while resolving the CHILD.
  //
  // ⚠ THE TENANT IS ARRANGED BY THE SERVER'S CONFIRMATION, NOT BY THE ADDRESS ALONE, and this suite used to
  // arrange it the other way round. `core/config/tenant-path.ts` records why that changed: inferring the
  // prefix from the first path segment made the route table's `**` fallback unreachable for every unknown
  // single-segment address. These cases therefore arrange the address AND the confirmation, which is what a
  // document served for a genuine child portal actually has in hand.

  const originalUrl = window.location.href;

  /**
   * The document's own base element, pinned at the root for these cases and restored afterwards.
   *
   * ⚠ THE TWO PREFIXES COMPOSE, so the deployment half is held at the root while the TENANT half is under
   * test. `configuredApiBase` is the mount point followed by the confirmed tenant segment, and a runner page
   * declaring a mount point of its own would make every expectation below read `/runner-mount/child/api/v1`.
   * The suite below this one tests the deployment half on its own terms.
   */
  const baseElement = document.querySelector('base');

  /** The `href` that element arrived with, or `null` when the document declares no base element. */
  const originalBaseHref = baseElement?.getAttribute('href') ?? null;

  beforeEach(() => {
    baseElement?.setAttribute('href', '/');

    // The platform's own history API, so the module under test reads a genuinely different
    // address rather than a substitute for one.
    history.replaceState({}, '', '/child/portals');

    // ⚠ THE PREFIX IS NOW ADOPTED EXPLICITLY, AND THE ADDRESS ALONE IS NO LONGER ENOUGH. These cases used
    // to arrange a tenant purely by changing the address, which is precisely the inference that was found
    // to be unsafe: a typo has the same shape as a real child alias, so the application now believes only
    // what the server confirmed. Arranging the address AND the confirmation states the same scenario -
    // a document served for a genuine child portal - without asserting an inference that no longer happens.
    adoptTenantPathBase('/child');
  });

  afterEach(() => {
    // Mandatory: all three outlive a single case, and any of them leaking would silently change every URL
    // asserted by every suite that runs after this one.
    history.replaceState({}, '', originalUrl);
    forgetTenantPathBase();

    if (baseElement !== null) {
      if (originalBaseHref === null) {
        baseElement.removeAttribute('href');
      } else {
        baseElement.setAttribute('href', originalBaseHref);
      }
    }
  });

  it('composes every generated address beneath the tenant prefix', () => {
    expect(apiUrl('portals')).toBe('/child/api/v1/portals');
    expect(API_ENDPOINTS.portals.collection()).toBe('/child/api/v1/portals');
    expect(API_ENDPOINTS.portals.byId(-1))
      .withContext('the sentinel-valued identifier is still interpolated unchanged')
      .toBe('/child/api/v1/portals/-1');
    expect(API_ENDPOINTS.users.byId(0)).toBe('/child/api/v1/users/0');
    expect(API_ENDPOINTS.roles.forCurrentPortal.members(0)).toBe(
      '/child/api/v1/roles/0/users',
    );
  });

  it('leaves the address root-relative, carrying no origin', () => {
    const address = API_ENDPOINTS.modules.collection();

    expect(address.startsWith('/child/api/v1/')).toBeTrue();
    expect(/^[a-z][a-z0-9+.-]*:/i.test(address)).toBeFalse();
    expect(address.startsWith('//')).toBeFalse();
  });

  it('classifies a prefixed API path as this API, so the bearer token is still attached', () => {
    expect(isApiRequest('/child/api/v1/portals')).toBeTrue();
    expect(isApiRequest('/child/api/v1')).toBeTrue();
    expect(isApiRequest(API_ENDPOINTS.users.collection())).toBeTrue();
  });

  it('classifies the ROOT API path as somebody else, because it addresses another tenant', () => {
    // `/api/v1/...` under a child document is the PARENT tenant's API. Nothing in this application composes
    // it - every URL comes from this module - so declining to recognise it costs nothing, and recognising
    // it would mean this session's credential travelled to a tenant the caller did not ask for.
    expect(isApiRequest('/api/v1/portals')).toBeFalse();
  });

  it('still refuses a foreign origin that spells the same prefixed path', () => {
    expect(isApiRequest('https://attacker.example/child/api/v1/users')).toBeFalse();
  });

  it('still requires a segment boundary after the version beneath the prefix', () => {
    expect(isApiRequest('/child/api/v10/users')).toBeFalse();
    expect(isApiRequest('/child/api/v1-preview/users')).toBeFalse();
  });

  it('COMPOSES ALL FOUR AUTHENTICATION ADDRESSES BENEATH THE PREFIX', () => {
    // ⚠ THE ONE CASE THIS SUITE WAS MISSING, AND ITS ABSENCE COST A TENANT-ISOLATION FAILURE. These four
    // were the only endpoints in the module held as eager module-load strings, so they were fixed while the
    // confirmed prefix was still empty and no later confirmation could revise them. Measured through a real
    // child-prefix alias: every other request carried the prefix and these four alone went to the root, so a
    // caller who addressed a child portal was issued authority for PortalID -1.
    expect(AUTH_ENDPOINTS.login()).toBe('/child/api/v1/auth/login');
    expect(AUTH_ENDPOINTS.refresh()).toBe('/child/api/v1/auth/refresh');
    expect(AUTH_ENDPOINTS.logout()).toBe('/child/api/v1/auth/logout');
    expect(AUTH_ENDPOINTS.me()).toBe('/child/api/v1/auth/me');
    expect(API_ENDPOINTS.auth.login())
      .withContext('the grouped alias must not diverge from the standalone group')
      .toBe('/child/api/v1/auth/login');
  });

  it('exempts the PREFIXED anonymous endpoints from carrying a bearer token', () => {
    // The other half of the same change. While the declarations were eager and unprefixed, the interceptor's
    // exemption test compared a prefixed candidate against an unprefixed declaration, found no match, and
    // attached a bearer token to login and refresh - the opposite of what those endpoints require.
    expect(isAnonymousAuthEndpoint(AUTH_ENDPOINTS.login())).toBeTrue();
    expect(isAnonymousAuthEndpoint(AUTH_ENDPOINTS.refresh())).toBeTrue();
    expect(isAnonymousAuthEndpoint(AUTH_ENDPOINTS.logout())).toBeTrue();
    expect(anonymousAuthEndpoints()).toEqual([
      '/child/api/v1/auth/login',
      '/child/api/v1/auth/refresh',
      '/child/api/v1/auth/logout',
    ]);
  });

  it('still requires a bearer token on the prefixed identity read', () => {
    // `me` is the one authentication endpoint that DOES take a bearer token, so a 401 from it is a genuine
    // expiry worth refreshing. Exempting it would make the session unrecoverable.
    expect(isAnonymousAuthEndpoint(AUTH_ENDPOINTS.me())).toBeFalse();
  });

  it('does not exempt the ROOT authentication addresses, which belong to another tenant', () => {
    expect(isAnonymousAuthEndpoint('/api/v1/auth/login')).toBeFalse();
    expect(isAnonymousAuthEndpoint('/api/v1/auth/refresh')).toBeFalse();
  });
});

describe('the authentication addresses across a change of tenant', () => {
  // ⚠ THIS SUITE EXISTS TO PIN *WHEN* THE ADDRESS IS COMPOSED, WHICH IS THE ROOT CAUSE RATHER THAN A
  // SYMPTOM OF IT. Every assertion above would pass equally against an eager registry that happened to be
  // evaluated after a prefix was adopted, so none of them can distinguish a URL fixed at module evaluation
  // from one composed per request. `main.ts` awaits `resolveTenantPathBase` before `bootstrapApplication`,
  // but an ES module's top-level initialisers run when the graph is EVALUATED - strictly before the first
  // statement of `main.ts` - so the only safe shape is one that resolves later. Adopting a prefix here,
  // long after this module was evaluated, is what proves it does.

  const originalUrl = window.location.href;
  const baseElement = document.querySelector('base');
  const originalBaseHref = baseElement?.getAttribute('href') ?? null;

  beforeEach(() => {
    baseElement?.setAttribute('href', '/');
    forgetTenantPathBase();
  });

  afterEach(() => {
    history.replaceState({}, '', originalUrl);
    forgetTenantPathBase();

    if (baseElement !== null) {
      if (originalBaseHref === null) {
        baseElement.removeAttribute('href');
      } else {
        baseElement.setAttribute('href', originalBaseHref);
      }
    }
  });

  it('FOLLOWS A PREFIX ADOPTED AFTER THIS MODULE WAS EVALUATED', () => {
    // Before any confirmation: the root, which is the correct answer for a single-tenant deployment and the
    // safe default while nothing has been established.
    expect(AUTH_ENDPOINTS.login()).toBe('/api/v1/auth/login');

    adoptTenantPathBase('/acme-legal');

    expect(AUTH_ENDPOINTS.login()).toBe('/acme-legal/api/v1/auth/login');
    expect(AUTH_ENDPOINTS.refresh()).toBe('/acme-legal/api/v1/auth/refresh');
    expect(AUTH_ENDPOINTS.logout()).toBe('/acme-legal/api/v1/auth/logout');
    expect(AUTH_ENDPOINTS.me()).toBe('/acme-legal/api/v1/auth/me');
  });

  it('follows a REJECTED candidate back to the root, so a typo signs in against nothing prefixed', () => {
    adoptTenantPathBase('/acme-legal');
    expect(AUTH_ENDPOINTS.login()).toBe('/acme-legal/api/v1/auth/login');

    // The empty string is a DECISION - a candidate rejected - not an absence, and the authentication
    // addresses must follow it exactly as every other address does.
    adoptTenantPathBase('');

    expect(AUTH_ENDPOINTS.login()).toBe('/api/v1/auth/login');
    expect(anonymousAuthEndpoints()).toEqual([
      '/api/v1/auth/login',
      '/api/v1/auth/refresh',
      '/api/v1/auth/logout',
    ]);
  });

  it('keeps the exemption set in step with the endpoints through the change', () => {
    adoptTenantPathBase('/acme-legal');

    // The set and the endpoints are one change: a stale exemption set would attach a bearer to login.
    for (const endpoint of anonymousAuthEndpoints()) {
      expect(isAnonymousAuthEndpoint(endpoint))
        .withContext(`endpoint ${endpoint}`)
        .toBeTrue();
    }

    expect(isAnonymousAuthEndpoint('/api/v1/auth/login'))
      .withContext('the previous tenant\u2019s address must stop matching')
      .toBeFalse();
  });
});

describe('a bundle mounted beneath a path segment', () => {
  // ⚠ THE MOUNT POINT IS DECLARED BY THE DOCUMENT'S BASE ELEMENT, NOT BY THE ADDRESS, and this suite used
  // to arrange it the other way round. `core/config/tenant-path.ts` records why that changed: inferring a
  // prefix from the first path segment made the route table's `**` fallback unreachable for every unknown
  // single-segment address. These cases therefore arrange a deployment mounted at `/child/` the way a real
  // one is arranged - by serving an `index.html` whose base element says so - and the address is left
  // deliberately at whatever the runner is on, to prove it no longer contributes.

  /** Restores the address after the case that arranges one. */
  const originalUrl = window.location.href;

  /** The document's own base element, whose `href` these cases borrow and must put back. */
  const baseElement = document.querySelector('base');

  /** The `href` that element arrived with, or `null` when the document declares no base element. */
  const originalBaseHref = baseElement?.getAttribute('href') ?? null;

  /** The base element this suite inserted, when the document declared none of its own. */
  let insertedBaseElement: HTMLBaseElement | null = null;

  beforeEach(() => {
    if (baseElement !== null) {
      baseElement.setAttribute('href', '/child/');

      return;
    }

    insertedBaseElement = document.createElement('base');
    insertedBaseElement.setAttribute('href', '/child/');
    document.head.appendChild(insertedBaseElement);
  });

  afterEach(() => {
    // Mandatory: the mount point outlives a single case, and a leaked prefix would silently change every
    // URL asserted by every suite that runs after this one.
    history.replaceState({}, '', originalUrl);

    if (insertedBaseElement !== null) {
      insertedBaseElement.remove();
      insertedBaseElement = null;
    }

    if (baseElement !== null) {
      if (originalBaseHref === null) {
        baseElement.removeAttribute('href');
      } else {
        baseElement.setAttribute('href', originalBaseHref);
      }
    }
  });

  it('composes every generated address beneath the declared mount point', () => {
    expect(apiUrl('portals')).toBe('/child/api/v1/portals');
    expect(API_ENDPOINTS.portals.collection()).toBe('/child/api/v1/portals');
    expect(API_ENDPOINTS.users.byId(0)).toBe('/child/api/v1/users/0');
  });

  it('classifies a mounted API path as this API, so the bearer token is still attached', () => {
    expect(isApiRequest('/child/api/v1/portals')).toBeTrue();
    expect(isApiRequest(API_ENDPOINTS.users.collection())).toBeTrue();
  });

  it('IGNORES THE ADDRESS ENTIRELY, which is the whole contract of the mount point', () => {
    // Every one of these addresses was once read as a mount point. The unknown single segment is the
    // measured defect: claiming it left the router an empty URL, so the route table's `**` fallback was
    // never consulted and a mistyped address resolved to the caller's landing screen.
    for (const address of ['/this-route-does-not-exist', '/nope/deeper', '/acme-legal/users/1/profile']) {
      history.replaceState({}, '', address);

      expect(apiUrl('portals'))
        .withContext(`address ${address}`)
        .toBe('/child/api/v1/portals');
    }
  });
});
