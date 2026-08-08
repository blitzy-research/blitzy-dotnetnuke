import { environment } from '../../../environments/environment';

import { API_ENDPOINTS, AUTH_ENDPOINTS, apiUrl, isApiRequest } from './api-endpoints';

/**
 * The exact production API base, spelled out here rather than read from the module under
 * test.
 *
 * ⚠ THE LITERAL IS THE WHOLE POINT AND MUST NOT BE REPLACED BY A REFERENCE. Comparing
 * `environment.apiBaseUrl` against itself, or against anything derived from it, would pass
 * for every value it could ever hold — including each of the three absolute values that
 * break the deployed application. The assertions below are only evidence because this
 * string is written out independently.
 */
const PRODUCTION_API_BASE = '/api/v1';

describe('the production API base', () => {
  /*
   * WHY THIS SUITE EXISTS, AND WHY NO OTHER CHECK COVERS IT.
   *
   * `docker/nginx.conf` proxies `location /api/` to `http://api:8080/api/` on the origin
   * that served the bundle, so the browser must address the API through a ROOT-RELATIVE
   * path. An absolute value type-checks, lints, bundles, deploys and leaves both containers
   * reporting healthy — nothing in the toolchain detects the substitution — and then fails
   * for every browser, in a different way depending on which absolute value was written:
   * a compose service name resolves only inside the Docker network; `http://localhost:8080`
   * works from the machine that published the port and nowhere else; any other host bypasses
   * the proxy and becomes a cross-origin request the API's policy is not written to admit.
   *
   * The behavioural suite below cannot catch it either, and that gap is the finding these
   * cases close. `isApiRequest` classifies by resolving both the candidate and the base
   * against the document, so it answers identically for the relative base and for an
   * absolute base whose origin happens to match the document's — which is exactly what
   * `http://localhost:8080/api/v1` is under a Karma run served from `localhost`. A suite
   * that only exercises classification therefore passes for a bundle that is broken in
   * production.
   *
   * ⚠ THE `test` TARGET DECLARES NO `fileReplacements`, which is what makes these
   * assertions meaningful. Verified in `angular.json`: only the `development` build
   * configuration substitutes the environment module, so every Karma spec compiles against
   * `environment.ts` — the PRODUCTION module — and the value asserted here is the value that
   * ships in the container image.
   */

  it('is exactly the root-relative path the reverse proxy serves', () => {
    expect(environment.apiBaseUrl).toBe(PRODUCTION_API_BASE);
  });

  it('is the production module rather than the development override', () => {
    // Pins the OTHER permitted divergence between the twins, so a specification that
    // accidentally compiled against the override could not pass the case above by
    // coincidence.
    expect(environment.production).toBeTrue();
  });

  it('is root-relative and carries no origin of any kind', () => {
    // Stated as three independent clauses rather than one equality, so the failure names
    // which property was lost. Each rules out one of the three absolute values that break
    // the deployed bundle.
    expect(environment.apiBaseUrl.startsWith('/')).toBeTrue();
    expect(environment.apiBaseUrl.startsWith('//')).toBeFalse();
    expect(/^[a-z][a-z0-9+.-]*:/i.test(environment.apiBaseUrl)).toBeFalse();
  });

  it('composes every generated address as a root-relative path beneath that base', () => {
    // The base being correct is necessary but not sufficient: the endpoint registry could
    // still compose an absolute address from it. These are the concrete addresses the
    // application issues, sampled across every collection plus the anonymous auth paths.
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
