import { environment } from '../../../environments/environment';

import { API_ENDPOINTS, AUTH_ENDPOINTS, apiUrl, isApiRequest } from './api-endpoints';

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
      AUTH_ENDPOINTS.login,
      AUTH_ENDPOINTS.refresh,
      AUTH_ENDPOINTS.logout,
      AUTH_ENDPOINTS.me,
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

  const originalUrl = window.location.href;

  beforeEach(() => {
    // The platform's own history API, so the module under test reads a genuinely different
    // address rather than a substitute for one.
    history.replaceState({}, '', '/child/portals');
  });

  afterEach(() => {
    // Mandatory: the address outlives a single case, and a leaked prefix would silently change
    // every URL asserted by every suite that runs after this one.
    history.replaceState({}, '', originalUrl);
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
});
