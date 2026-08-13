import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { TabDetail, TabListItem, UpdateTabRequest } from '../models/tab.model';
import { TabService } from './tab.service';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';

import type { Observable } from 'rxjs';

/**
 * Specification for {@link TabService} - the lookup-only transport for the page resource, the abstraction
 * the database, the legacy source and the wire contract all still call a "tab" and that an administrator
 * sees as a Page. The legacy tree contains ZERO automated tests of any kind, so nothing here is a port.
 */

/** The success envelope, restated locally rather than imported. */
interface SuccessEnvelope<TPayload> {
  readonly data: TPayload;
  readonly meta: null;
}

/** Wraps a payload in the success envelope exactly as the server writes it. */
function envelope<TPayload>(data: TPayload): SuccessEnvelope<TPayload> {
  return { data, meta: null };
}

/**
 * One page-list row, carrying all fourteen members the list projection declares. Every member is spelled
 * from the wire contract rather than from the legacy source, and two of those spellings would have failed
 * silently had they been guessed.
 */
function listRow(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 7,
    tabName: 'Home',
    title: 'Home Page',
    tabOrder: 1,
    parentId: null,
    level: 0,
    tabPath: '//Home',
    isVisible: true,
    disableLink: false,
    isDeleted: false,
    hasChildren: true,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

/**
 * One page in full, carrying all twenty-three members the detail projection declares. The skin and
 * container tokens ARE present here, and their presence is the counterpart to their absence from the
 * update shape: a stored choice stays observable even though page skinning is out of scope for this
 * migration, so both are readable and inert while neither is settable.
 */
function detail(overrides: Partial<TabDetail> = {}): TabDetail {
  return {
    tabId: 7,
    tabOrder: 1,
    portalId: 0,
    tabName: 'Home',
    isVisible: true,
    parentId: null,
    level: 0,
    iconFile: null,
    disableLink: false,
    title: 'Home Page',
    description: 'The landing page.',
    keywords: 'home,landing',
    isDeleted: false,
    url: null,
    skinSrc: '[G]Skins/Default/Home.ascx',
    containerSrc: '[G]Containers/Default/Title.ascx',
    tabPath: '//Home',
    startDate: null,
    endDate: null,
    refreshInterval: null,
    pageHeadText: null,
    isSecure: false,
    hasChildren: true,
    ...overrides,
  };
}

/**
 * A complete update body, carrying all fifteen members the request shape declares - and, as importantly,
 * not one member more.
 */
function updateRequest(overrides: Partial<UpdateTabRequest> = {}): UpdateTabRequest {
  return {
    tabName: 'Home',
    title: 'Home Page',
    description: 'The landing page.',
    keywords: 'home,landing',
    parentId: null,
    isVisible: true,
    disableLink: false,
    iconFile: null,
    url: null,
    startDate: '2024-01-01T00:00:00.000Z',
    endDate: null,
    refreshInterval: null,
    pageHeadText: null,
    isSecure: false,
    isDeleted: false,
    ...overrides,
  };
}

/**
 * The fifteen member names the update body carries, sorted, so that the write shape can be asserted as an
 * exact set rather than one member at a time.
 */
const UPDATE_BODY_MEMBERS: readonly string[] = [
  'description',
  'disableLink',
  'endDate',
  'iconFile',
  'isDeleted',
  'isSecure',
  'isVisible',
  'keywords',
  'pageHeadText',
  'parentId',
  'refreshInterval',
  'startDate',
  'tabName',
  'title',
  'url',
];

/**
 * The two extension members the API attaches to every problem document. `ValidationProblemDetailsFactory`
 * writes both on every refusal, so a fixture without them describes a response this API does not send.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * @param status The status the server's mapping yields for the code.
 * @param title The per-status title from the server's own vocabulary.
 * @param code The failure code, spelled exactly as the server publishes it.
 * @returns The complete document, ready to flush.
 */
function problemDocument(status: number, title: string, code: string): Readonly<Record<string, unknown>> {
  return {
    type: `urn:dnnmigration:error:${code}`,
    title,
    status,
    detail: 'The request could not be completed.',
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * Recovers the failure code from a refusal that reached the caller.
 *
 * @param body The body carried by the failure.
 * @returns The code, or null when the body carries no recognisable problem type.
 */
function failureCodeOf(body: unknown): string | null {
  const type: unknown = bodyMembers(body)['type'];
  const prefix = 'urn:dnnmigration:error:';

  return typeof type === 'string' && type.startsWith(prefix) ? type.slice(prefix.length) : null;
}

/** Reads a flushed request body as a plain member map. */
function bodyMembers(body: unknown): Readonly<Record<string, unknown>> {
  return body as Readonly<Record<string, unknown>>;
}

/** Awaits a failed call and returns the transport failure it produced. */
async function captureFailure(pending: Promise<unknown>): Promise<HttpErrorResponse> {
  const outcome: unknown = await pending.then(
    () => null,
    (reason: unknown) => reason,
  );

  expect(outcome instanceof HttpErrorResponse)
    .withContext('the transport failure reaches the caller intact, as an HttpErrorResponse')
    .toBeTrue();

  return outcome as HttpErrorResponse;
}

describe('TabService', () => {
  let service: TabService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client with NO interceptors registered.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(TabService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // MANDATORY, and the executable guard that makes every "no such endpoint" claim in this file
    // enforceable rather than aspirational.
    httpMock.verify();
  });

  describe('construction', () => {
    it('resolves from the root injector as a single shared instance', () => {
      expect(service).toBeTruthy();

      // Declared with root-level provision, so the injector must hand back the very same object rather than
      // a fresh one per request.
      expect(TestBed.inject(TabService))
        .withContext('root-provided, therefore one instance for the whole application')
        .toBe(service);
    });
  });

  describe('getByPortal - the portal-scoped list, path shape A', () => {
    it('issues a GET to the portal-nested collection and emits the rows it received', async () => {
      const rows: readonly TabListItem[] = [
        listRow({ tabId: 0, tabName: 'Home', parentId: null, level: 0 }),
        listRow({ tabId: 1, tabName: 'About', parentId: 0, level: 1, tabPath: '//Home//About' }),
      ];

      const pending = firstValueFrom(service.getByPortal(3));

      const request = httpMock.expectOne('/api/v1/portals/3/tabs');
      expect(request.request.method).toBe('GET');

      request.flush(envelope(rows));

      // The emitted value is the payload from INSIDE the envelope, not the envelope itself.
      expect(await pending).toEqual(rows);
    });

    it('hands the rows on in the order the server sent them, without folding them into a tree', async () => {
      // A child precedes its own parent here on purpose. The endpoint answers a FLAT list and this service
      // is transport, so assembling a hierarchy from the parent reference, the depth and the sort position
      // is a derivation that belongs to a signal store or to the component that renders the indentation.
      const rows: readonly TabListItem[] = [
        listRow({ tabId: 2, tabName: 'Child', parentId: 0, level: 1 }),
        listRow({ tabId: 0, tabName: 'Parent', parentId: null, level: 0 }),
      ];

      const pending = firstValueFrom(service.getByPortal(3));
      httpMock.expectOne('/api/v1/portals/3/tabs').flush(envelope(rows));

      const emitted = await pending;

      expect(emitted).toEqual(rows);
      expect(emitted.map((row) => row.tabId))
        .withContext('server order preserved exactly; no sorting and no tree building')
        .toEqual([2, 0]);
    });

    it('sends no query string at all, because the page list is deliberately unpaged', async () => {
      const pending = firstValueFrom(service.getByPortal(3));

      const request = httpMock.expectOne('/api/v1/portals/3/tabs');

      expect(request.request.params.keys().length)
        .withContext('unpaged: no page, pageSize, sort or sortDirection')
        .toBe(0);
      expect(request.request.urlWithParams)
        .withContext('the request URL carries no appended query string')
        .toBe('/api/v1/portals/3/tabs');

      request.flush(envelope([listRow()]));
      await pending;
    });

    it('emits an empty collection unchanged when the portal has no pages', async () => {
      const pending = firstValueFrom(service.getByPortal(3));
      httpMock.expectOne('/api/v1/portals/3/tabs').flush(envelope([]));

      // An empty list is a successful answer, not a failure and not an absent value. It must arrive as an
      // empty array rather than as null or undefined, so that a caller can distinguish "this tenant has no
      // pages" from "the call did not happen".
      expect(await pending).toEqual([]);
    });

    it('transmits portal 0 as a real tenant identifier', async () => {
      // Zero is the SECOND identity value on the tenant table, which is declared `IDENTITY (-1, 1)`.
      const pending = firstValueFrom(service.getByPortal(0));

      const request = httpMock.expectOne('/api/v1/portals/0/tabs');
      expect(request.request.method).toBe('GET');

      request.flush(envelope([listRow()]));
      await pending;
    });

    it('transmits portal -1 as a real tenant identifier, despite it also being the legacy absence marker', async () => {
      // THE COLLISION, ASSERTED. Minus one is the FIRST identity value on the tenant table
      // (`01.00.00.SqlDataProvider:L77`), so it names the first portal an installation ever created; and it
      // is simultaneously the legacy missing-integer marker.
      const pending = firstValueFrom(service.getByPortal(-1));

      const request = httpMock.expectOne('/api/v1/portals/-1/tabs');
      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('minus one is transmitted, never elided and never re-encoded')
        .toBe('/api/v1/portals/-1/tabs');

      request.flush(envelope([listRow()]));
      await pending;
    });
  });

  describe('getById - the root-level detail, path shape B', () => {
    it('issues a GET to the root-level page address, NOT to a portal-nested one', async () => {
      // A page key is unique across an installation, which is why the detail route needs no tenant segment
      // - and the outstanding-request check in the teardown is what proves no portal-nested variant was
      // requested alongside this one.
      const page = detail({ tabId: 7 });

      const pending = firstValueFrom(service.getById(7));

      const request = httpMock.expectOne('/api/v1/tabs/7');
      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('root-level: the detail route carries no /portals/{portalId} prefix')
        .toBe('/api/v1/tabs/7');

      request.flush(envelope(page));

      expect(await pending).toEqual(page);
    });

    it('transmits page 0 as a real page identifier rather than treating it as absent', async () => {
      // `dbo.Tabs` is declared `[TabID] [int] IDENTITY (0, 1)` (`01.00.00.SqlDataProvider:L140`), so the
      // first page an installation ever creates is numbered ZERO. Zero is an ordinary key here and never
      // means "not yet saved".
      const page = detail({ tabId: 0, tabName: 'First Page', parentId: null, level: 0 });

      const pending = firstValueFrom(service.getById(0));

      const request = httpMock.expectOne('/api/v1/tabs/0');
      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('zero is interpolated as a segment; the trailing-slash collection form is wrong')
        .toBe('/api/v1/tabs/0');

      request.flush(envelope(page));

      expect(await pending).toEqual(page);
    });

    it('emits the nulls the server sent, without substituting a legacy sentinel for any of them', async () => {
      // The API converts every legacy in-band sentinel to a genuine null at its boundary, and this service
      // must leave that conversion alone.
      const page = detail({
        tabId: 4,
        portalId: null,
        parentId: null,
        startDate: null,
        endDate: null,
        refreshInterval: null,
        title: null,
        iconFile: null,
      });

      const pending = firstValueFrom(service.getById(4));
      httpMock.expectOne('/api/v1/tabs/4').flush(envelope(page));

      const emitted = await pending;

      expect(emitted.parentId).withContext('a root-level page: null, never -1').toBeNull();
      expect(emitted.portalId).withContext('a host-level page: null, never -1').toBeNull();
      expect(emitted.refreshInterval).withContext('no automatic refresh: null, never -1').toBeNull();
      expect(emitted.startDate).withContext('unset date: null, never the minimum instant').toBeNull();
      expect(emitted.endDate).withContext('unset date: null, never the minimum instant').toBeNull();
      expect(emitted.title).withContext('no stored title: null, never the empty string').toBeNull();
    });
  });

  describe('update - the only write on this surface', () => {
    it('issues a PUT to the same root-level address the detail read uses', async () => {
      const request_ = updateRequest();
      const saved = detail({ tabId: 7, tabName: request_.tabName });

      const pending = firstValueFrom(service.update(7, request_));

      const call = httpMock.expectOne('/api/v1/tabs/7');
      expect(call.request.method).toBe('PUT');
      expect(call.request.url)
        .withContext('the write shares the read address; there is no separate edit route')
        .toBe('/api/v1/tabs/7');

      call.flush(envelope(saved));

      // The response is the page as it now stands, so a caller can render the server's own
      // result rather than the state it hoped it had written.
      expect(await pending).toEqual(saved);
    });

    it('forwards the request body member for member, adding nothing and removing nothing', async () => {
      const request_ = updateRequest({
        tabName: 'Contact',
        title: 'Contact Us',
        description: 'Reach the team.',
        keywords: 'contact,support',
        parentId: 0,
        isVisible: true,
        disableLink: true,
        iconFile: 'contact.gif',
        url: 'https://example.test/contact',
        startDate: '2024-03-01T00:00:00.000Z',
        endDate: '2024-12-31T23:59:59.000Z',
        refreshInterval: 300,
        pageHeadText: '<meta name="robots" content="noindex">',
        isSecure: true,
        isDeleted: false,
      });

      const pending = firstValueFrom(service.update(7, request_));

      const call = httpMock.expectOne('/api/v1/tabs/7');

      // Deep equality against the object the caller supplied.
      expect(call.request.body).toEqual(request_);

      call.flush(envelope(detail({ tabId: 7, tabName: 'Contact' })));
      await pending;
    });

    it('preserves -1, the empty string, zero and false in the body rather than eliding any of them', async () => {
      // SENTINEL FIDELITY, WHICH IS THE WHOLE POINT OF THIS CASE. Each of the four values below is a
      // legitimate stored value in this schema, and each is also what some serialiser somewhere would call
      // "empty" and drop:
      const request_ = updateRequest({
        refreshInterval: -1,
        title: '',
        description: '',
        keywords: '',
        parentId: 0,
        isVisible: false,
        disableLink: false,
        isSecure: false,
        isDeleted: false,
      });

      const pending = firstValueFrom(service.update(7, request_));

      const call = httpMock.expectOne('/api/v1/tabs/7');
      const members = bodyMembers(call.request.body);

      expect(members['refreshInterval']).withContext('-1 survives as a number').toBe(-1);
      expect(members['title']).withContext('the empty string survives, un-nulled').toBe('');
      expect(members['description']).withContext('the empty string survives, un-nulled').toBe('');
      expect(members['keywords']).withContext('the empty string survives, un-nulled').toBe('');
      expect(members['parentId']).withContext('zero survives; it names a real parent page').toBe(0);
      expect(members['isVisible']).withContext('false survives, un-dropped').toBeFalse();
      expect(members['disableLink']).withContext('false survives, un-dropped').toBeFalse();
      expect(members['isSecure']).withContext('false survives, un-dropped').toBeFalse();
      expect(members['isDeleted']).withContext('false survives, un-dropped').toBeFalse();

      expect(Object.keys(members).sort())
        .withContext('every member is on the wire even when its value looks empty')
        .toEqual([...UPDATE_BODY_MEMBERS]);

      call.flush(envelope(detail({ tabId: 7, title: null })));
      await pending;
    });

    it('sends no skin token and no container token, because neither is settable through this API', async () => {
      const request_ = updateRequest();

      const pending = firstValueFrom(service.update(7, request_));

      const call = httpMock.expectOne('/api/v1/tabs/7');
      const members = bodyMembers(call.request.body);

      // The omission is also strictly SAFER than carrying them, which is the part that makes this worth an
      // assertion rather than a comment.
      expect(Object.keys(members))
        .withContext('the skin token is deliberately absent from the write surface')
        .not.toContain('skinSrc');
      expect(Object.keys(members))
        .withContext('the container token is deliberately absent from the write surface')
        .not.toContain('containerSrc');

      // The exact-set comparison closes the door on any other member being smuggled in later,
      // which a pair of per-member checks could never do on its own.
      expect(Object.keys(members).sort())
        .withContext('the write surface is exactly these fifteen members')
        .toEqual([...UPDATE_BODY_MEMBERS]);

      call.flush(envelope(detail({ tabId: 7 })));
      await pending;
    });

    it('carries no identifier, tenant, sort position, depth or materialised path in the body', async () => {
      const pending = firstValueFrom(service.update(7, updateRequest()));

      const call = httpMock.expectOne('/api/v1/tabs/7');
      const memberNames = Object.keys(bodyMembers(call.request.body));

      // The identifier is route-supplied and never body-supplied - the legacy editor took it from the
      // page's own context and no form field ever contributed it - so the route value is authoritative and
      // a body member would create a second, disagreeing source. The tenant is absent for the same reason.
      expect(memberNames).not.toContain('tabId');
      expect(memberNames).not.toContain('portalId');
      expect(memberNames).not.toContain('tabOrder');
      expect(memberNames).not.toContain('level');
      expect(memberNames).not.toContain('tabPath');
      expect(memberNames).not.toContain('hasChildren');

      call.flush(envelope(detail({ tabId: 7 })));
      await pending;
    });
  });

  describe('failure propagation - refusals reach the caller unaltered', () => {
    it('errors when the portal-scoped list answers 404', async () => {
      const pending = firstValueFrom(service.getByPortal(3));

      httpMock
        .expectOne('/api/v1/portals/3/tabs')
        .flush(problemDocument(404, 'Not Found', 'tab.portal_not_found'), {
          status: 404,
          statusText: 'Not Found',
        });

      const failure = await captureFailure(pending);

      expect(failure.status).toBe(404);
      // The problem document reaches the caller intact.
      expect(failureCodeOf(failure.error))
        .withContext('the server reason code arrives untranslated, carried by the problem type')
        .toBe('tab.portal_not_found');
    });

    it('errors when the detail read answers 404', async () => {
      const pending = firstValueFrom(service.getById(7));

      httpMock.expectOne('/api/v1/tabs/7').flush(problemDocument(404, 'Not Found', 'tab.not_found'), {
        status: 404,
        statusText: 'Not Found',
      });

      const failure = await captureFailure(pending);

      expect(failure.status).toBe(404);
      expect(failureCodeOf(failure.error)).toBe('tab.not_found');
    });

    it('errors when the update is refused with 400, forwarding the reason code untouched', async () => {
      const pending = firstValueFrom(service.update(7, updateRequest({ tabName: 'CON' })));

      httpMock
        .expectOne('/api/v1/tabs/7')
        .flush(problemDocument(400, 'Bad Request', 'tab.name_reserved'), {
          status: 400,
          statusText: 'Bad Request',
        });

      const failure = await captureFailure(pending);

      expect(failure.status).toBe(400);
      expect(failureCodeOf(failure.error))
        .withContext('the reserved-name refusal arrives as the server wrote it')
        .toBe('tab.name_reserved');
    });

    it('errors on each of the three parentage refusals the update actually emits', async () => {
      const causes: readonly string[] = [
        'tab.parent_not_found',
        'tab.parent_cycle',
        'tab.parent_cross_portal',
      ];

      for (const code of causes) {
        const pending = firstValueFrom(service.update(7, updateRequest({ parentId: 3 })));

        httpMock
          .expectOne('/api/v1/tabs/7')
          .flush(problemDocument(400, 'Bad Request', code), {
            status: 400,
            statusText: 'Bad Request',
          });

        const failure = await captureFailure(pending);

        expect(failure.status)
          .withContext(`${code} carries no conflict, not-found or forbidden token, so it is 400`)
          .toBe(400);
        expect(failureCodeOf(failure.error))
          .withContext('the cause is distinguishable from its siblings by type alone')
          .toBe(code);
      }
    });

    it('errors when a call is rejected with 403, and does not retry it', async () => {
      const pending = firstValueFrom(service.update(7, updateRequest()));

      httpMock
        .expectOne('/api/v1/tabs/7')
        .flush(problemDocument(403, 'Forbidden', 'auth.not_permitted'), {
          status: 403,
          statusText: 'Forbidden',
        });

      const failure = await captureFailure(pending);

      expect(failure.status).toBe(403);
      expect(failureCodeOf(failure.error)).toBe('auth.not_permitted');
    });
  });

  describe('the closed write surface', () => {
    it('issues exactly one request per call across all three methods, and no others', async () => {
      // WHAT DOES NOT EXIST ON THIS BACKEND, and therefore has no case of its own here: page CREATE (no
      // POST), page DELETE (no DELETE verb), partial update (no PATCH), reorder, move, copy, export,
      // import, and recycle-bin restore or purge.
      const list = firstValueFrom(service.getByPortal(3));
      httpMock.expectOne('/api/v1/portals/3/tabs').flush(envelope([listRow()]));
      expect((await list).length).toBe(1);

      const read = firstValueFrom(service.getById(7));
      httpMock.expectOne('/api/v1/tabs/7').flush(envelope(detail({ tabId: 7 })));
      expect((await read).tabId).toBe(7);

      const written = firstValueFrom(service.update(7, updateRequest({ tabName: 'Renamed' })));
      httpMock.expectOne('/api/v1/tabs/7').flush(envelope(detail({ tabId: 7, tabName: 'Renamed' })));
      expect((await written).tabName).toBe('Renamed');

      // Asserted here as well as in the teardown, so that this case states the claim it exists
      // to make instead of relying on a hook the reader has to go and find.
      httpMock.verify();
    });

    it('does not request the health endpoint, which lives outside the versioned API', async () => {
      // The health probe is mapped at the host root, is anonymous and unthrottled, and sits OUTSIDE
      // `/api/v1` entirely. It exists for the container health check and the orchestrator's readiness gate,
      // and no application service may call it.
      const pending = firstValueFrom(service.getById(7));

      httpMock.expectOne('/api/v1/tabs/7').flush(envelope(detail({ tabId: 7 })));
      expect(httpMock.match('/health').length)
        .withContext('no service touches the health endpoint')
        .toBe(0);

      await pending;
    });
  });
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  describe('refuses a response that does not match its contract', () => {
    /**
     * Asserts that answering the one pending request with `body` fails at `path`.
     *
     * @param source The call under test.
     * @param url The url the call addresses.
     * @param body The malformed body to answer with.
     * @param path The member path the violation must name.
     */
    function expectViolationAt(
      source: Observable<unknown>,
      url: string,
      body: object,
      path: string,
    ): void {
      const values: unknown[] = [];
      const failures: unknown[] = [];

      source.subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      httpMock.expectOne(url).flush(body);

      expect(values).toEqual([]);
      expect(failures.length).toBe(1);
      expect(isContractViolation(failures[0])).toBeTrue();

      if (isContractViolation(failures[0])) {
        expect(failures[0].path).toBe(path);
      }
    }

    it('refuses a listed page whose level is absent', () => {
      const malformed: Record<string, unknown> = { ...listRow() };

      delete malformed['level'];

      expectViolationAt(
        service.getByPortal(4),
        '/api/v1/portals/4/tabs',
        envelope([malformed]),
        'response.data[0].level',
      );
    });

    it('refuses a listed page whose order arrived as text', () => {
      expectViolationAt(
        service.getByPortal(4),
        '/api/v1/portals/4/tabs',
        envelope([{ ...listRow(), tabOrder: '1' }]),
        'response.data[0].tabOrder',
      );
    });

    it('refuses a hierarchy that is not an array', () => {
      expectViolationAt(
        service.getByPortal(4),
        '/api/v1/portals/4/tabs',
        envelope(listRow()),
        'response.data',
      );
    });

    it('admits an empty hierarchy, which is a portal with no pages', () => {
      const values: unknown[] = [];

      service.getByPortal(4).subscribe({ next: (rows: unknown) => values.push(rows) });

      httpMock.expectOne('/api/v1/portals/4/tabs').flush(envelope([]));

      expect(values).toEqual([[]]);
    });

    it('admits page zero and portal minus one, which are both real identifiers', () => {
      // ⚠ THE DOUBLE SENTINEL COLLISION. `dbo.Tabs` is `IDENTITY (0, 1)` so zero is the first page;
      // `dbo.Portals` is `IDENTITY (-1, 1)` so minus one is the first portal — and minus one is
      // simultaneously the legacy absent-integer marker. Neither may be read as absent.
      const values: unknown[] = [];

      service.getById(0).subscribe({ next: (page: unknown) => values.push(page) });

      httpMock
        .expectOne('/api/v1/tabs/0')
        .flush(envelope(detail({ tabId: 0, portalId: -1, parentId: null })));

      expect(values.length).toBe(1);
    });

    it('refuses a root page whose parent arrived as text rather than null', () => {
      expectViolationAt(
        service.getById(7),
        '/api/v1/tabs/7',
        envelope({ ...detail(), parentId: '' }),
        'response.data.parentId',
      );
    });
  });

  // -------------------------------------------------------------------------
  // WHO ANNOUNCES A FAILURE
  // -------------------------------------------------------------------------
  describe('marks every request as presented by its caller', () => {
    it('marks all three operations', () => {
      const swallow = { error: () => undefined };

      service.getByPortal(4).subscribe(swallow);
      service.getById(7).subscribe(swallow);
      service.update(7, updateRequest()).subscribe(swallow);

      const issued = httpMock.match(() => true);

      expect(issued.length).toBe(3);

      for (const pending of issued) {
        expect(pending.request.context.get(PRESENTED_IN_CONTEXT))
          .withContext(`${pending.request.method} ${pending.request.url} is unmarked`)
          .toBeTrue();
      }

      for (const pending of issued) {
        pending.flush(null, { status: 500, statusText: 'Server Error' });
      }
    });
  });
});
