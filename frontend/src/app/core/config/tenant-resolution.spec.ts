import { environment } from '../../../environments/environment';

import { forgetTenantPathBase, tenantPathBase } from './tenant-path';
import { resolveTenantPathBase } from './tenant-resolution';

/**
 * Specification for the step that settles whether the first path segment of this document's address really
 * names a tenant.
 *
 * The defect being pinned was measured through the container topology: a one-character typo of the
 * most-typed admin path was adopted as a tenant prefix, which produced a fully styled sign-in screen that
 * refused CORRECT credentials and blamed them, with no in-application way back and with the route table's
 * own not-found view unreachable for that address. Every case below is stated over the deployment's ANSWER
 * rather than over a rendered screen, because the answer is the whole input to the decision.
 */
describe('settling the tenant path prefix', () => {
  const originalUrl = window.location.href;

  /** The API base as the production topology configures it: relative, so the API is same-origin. */
  const RELATIVE_API_BASE = '/api/v1';

  /**
   * The API base the development topology configures. Absolute, because `ng serve` and the API have no
   * shared origin there.
   */
  const ABSOLUTE_API_BASE = 'http://localhost:8080/api/v1';

  /**
   * Builds a success answer carrying a reported prefix.
   *
   * @param pathPrefix The prefix the deployment reports, in the API's own envelope.
   * @returns A 200 response with that body.
   */
  function reporting(pathPrefix: string): Response {
    return new Response(JSON.stringify({ data: { pathPrefix }, meta: null }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  /**
   * Builds a refusal in the shape this API answers with.
   *
   * @param status The status the deployment answers with.
   * @returns A response carrying that status and a problem document.
   */
  function refusing(status: number): Response {
    return new Response(
      JSON.stringify({
        type: 'urn:dnnmigration:error:auth.unauthenticated',
        title: 'Unauthorized',
        status,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    );
  }

  /**
   * Installs the recording stub for the one request this module can make.
   *
   * @returns The spy, so a case can arrange its answer and assert what was asked.
   */
  function stubFetch(): jasmine.Spy<typeof window.fetch> {
    return spyOn(window, 'fetch');
  }

  afterEach(() => {
    history.replaceState({}, '', originalUrl);

    // The decision is MODULE state that outlives a TestBed, so each case undoes its own.
    forgetTenantPathBase();
  });

  describe('when the address proposes no candidate', () => {
    it('decides on the root without asking anything', async () => {
      history.replaceState({}, '', '/portals');
      const fetchSpy = stubFetch();

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('');

      expect(fetchSpy)
        .withContext(
          "the console's own routes and the bare host are every address the application itself " +
            'navigates to, so first paint costs no extra request',
        )
        .not.toHaveBeenCalled();
      expect(tenantPathBase()).toBe('');
    });
  });

  describe('the question it asks', () => {
    it('is addressed beneath the candidate itself, anonymously and uncached', async () => {
      history.replaceState({}, '', '/acme/portals');
      const fetchSpy = stubFetch();
      fetchSpy.and.resolveTo(reporting('/acme'));

      await resolveTenantPathBase(RELATIVE_API_BASE);

      // ⚠ ADDRESSED BENEATH THE CANDIDATE, AND THAT IS THE WHOLE MECHANISM. The API moves a tenant's
      // segment out of the routable path only for a stored alias and falls back to no bare host, so
      // reaching this address at all is itself evidence that the segment names a tenant.
      expect(fetchSpy).toHaveBeenCalledTimes(1);
      expect(fetchSpy.calls.mostRecent().args[0]).toBe('/acme/api/v1/tenant-address');

      const options = fetchSpy.calls.mostRecent().args[1] as RequestInit;

      expect(options.method).toBe('GET');
      expect(options.credentials)
        .withContext('a cold load holds no token, which is why the endpoint is anonymous')
        .toBe('omit');
      expect(options.cache).toBe('no-store');
      expect(options.signal)
        .withContext('the request is bounded, because the application start awaits it')
        .not.toBeUndefined();
    });

    it('is not asked at all when the API is configured at an absolute address', async () => {
      // The development topology: the document and the API are served by different origins, so a segment on
      // the document's origin is not an address the API could match an alias against, and asking would
      // answer about the wrong address.
      history.replaceState({}, '', '/acme/portals');
      const fetchSpy = stubFetch();

      await expectAsync(resolveTenantPathBase(ABSOLUTE_API_BASE)).toBeResolvedTo('/acme');

      expect(fetchSpy).not.toHaveBeenCalled();
    });
  });

  describe('an answer that confirms the candidate', () => {
    it('adopts it', async () => {
      history.replaceState({}, '', '/acme/portals');
      stubFetch().and.resolveTo(reporting('/acme'));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('/acme');

      expect(tenantPathBase()).toBe('/acme');
    });

    it('adopts the spelling the CALLER used when the two differ only in case', async () => {
      // Aliases are matched case-insensitively and reported as stored, so a caller that typed `/Acme` for a
      // row stored as `/acme` has reached the tenant - and its own spelling is the one in the address bar.
      history.replaceState({}, '', '/Acme/portals');
      stubFetch().and.resolveTo(reporting('/acme'));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('/Acme');
    });

    it('adopts it even when the body cannot be read, because reaching it is the primary signal', async () => {
      history.replaceState({}, '', '/acme/portals');
      stubFetch().and.resolveTo(
        new Response('not json at all', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('/acme');
    });
  });

  describe('an answer that refuses the candidate', () => {
    it('rejects it, so the router matches the catch-all for the address as typed', async () => {
      history.replaceState({}, '', '/portls');
      stubFetch().and.resolveTo(refusing(401));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('');

      expect(tenantPathBase())
        .withContext('the full mistyped path then resolves against the root and matches **')
        .toBe('');
    });

    it('rejects it for every caller-correctable refusal, not only the challenge', async () => {
      const fetchSpy = stubFetch();
      fetchSpy.and.returnValues(
        Promise.resolve(refusing(403)),
        Promise.resolve(refusing(404)),
        Promise.resolve(refusing(400)),
      );

      for (const status of [403, 404, 400]) {
        history.replaceState({}, '', '/portls');
        forgetTenantPathBase();

        await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE))
          .withContext(`status ${status.toString()}: nothing answers beneath the segment`)
          .toBeResolvedTo('');
      }
    });

    it('rejects it when the answer reports no prefix at all', async () => {
      // The defensive half of the body check. A success reporting nothing describes a tenant addressed
      // somewhere other than beneath this segment, so the segment is not this document's tenant.
      history.replaceState({}, '', '/portls');
      stubFetch().and.resolveTo(reporting(''));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('');
    });

    it('rejects it when the answer names a DIFFERENT segment', async () => {
      history.replaceState({}, '', '/portls');
      stubFetch().and.resolveTo(reporting('/acme'));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('');
    });
  });

  describe('an answer that settles nothing', () => {
    it('keeps the candidate when the deployment faults, so an outage cannot strand a real tenant', async () => {
      history.replaceState({}, '', '/acme/portals');
      stubFetch().and.resolveTo(refusing(503));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE))
        .withContext(
          'a server fault says nothing about the address; rendering "nothing answers this address" for a ' +
            'legitimate child portal would misdescribe an outage as a missing tenant',
        )
        .toBeResolvedTo('/acme');
    });

    it('keeps the candidate for the two transient caller-correctable statuses', async () => {
      const fetchSpy = stubFetch();
      fetchSpy.and.returnValues(Promise.resolve(refusing(408)), Promise.resolve(refusing(429)));

      for (const status of [408, 429]) {
        history.replaceState({}, '', '/acme/portals');
        forgetTenantPathBase();

        await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE))
          .withContext(`status ${status.toString()} describes the moment, not the address`)
          .toBeResolvedTo('/acme');
      }
    });

    it('keeps the candidate when the request cannot be made at all', async () => {
      history.replaceState({}, '', '/acme/portals');
      stubFetch().and.rejectWith(new TypeError('Failed to fetch'));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolvedTo('/acme');
    });

    it('never rejects, so it can never be the reason a document fails to bootstrap', async () => {
      history.replaceState({}, '', '/acme/portals');
      stubFetch().and.rejectWith(new Error('anything at all'));

      await expectAsync(resolveTenantPathBase(RELATIVE_API_BASE)).toBeResolved();
    });
  });

  describe('the base it is asked with', () => {
    it('is the one the shipped configuration declares, and that value is relative', () => {
      // ⚠ THE CONTRACT WITH THE PROXY. `docker/nginx.conf` proxies `/api/` on the origin that served the
      // bundle, including beneath a tenant segment, so the probe can only be addressed beneath a candidate
      // while this value stays relative. An absolute value would take every request off the proxy.
      expect(environment.apiBaseUrl).toBe(RELATIVE_API_BASE);
    });
  });
});
