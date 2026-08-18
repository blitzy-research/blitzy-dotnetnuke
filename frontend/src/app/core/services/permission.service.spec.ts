/**
 * Specification for {@link PermissionService}, the client half of the read-only permission catalogue.
 * NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains ZERO automated tests of any kind — no
 * test project, no fixture, no harness — so nothing here is a translation of a prior assertion.
 */

import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { Permission } from '../models/permission.model';
import { PermissionService } from './permission.service';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';

import type { Observable } from 'rxjs';

/** The catalogue listing URL, written out in full rather than derived. */
const LIST_URL = '/api/v1/permissions';

/**
 * The catalogue as the listing endpoint publishes it: DEFINITIONS, each carrying its own identifier.
 *
 * The last entry names a key outside the four this codebase declares. That is deliberate — an installation
 * may register any key, the endpoint reports what the table holds, and this client must carry it through
 * rather than drop it.
 */
const CATALOGUE_DEFINITIONS: readonly Permission[] = [
  {
    permissionId: 1,
    permissionCode: 'SYSTEM_MODULE_DEFINITION',
    moduleDefId: 0,
    permissionKey: 'VIEW',
    permissionName: 'View',
  },
  {
    permissionId: 2,
    permissionCode: 'SYSTEM_MODULE_DEFINITION',
    moduleDefId: 0,
    permissionKey: 'EDIT',
    permissionName: 'Edit',
  },
  {
    permissionId: 9,
    permissionCode: 'SYSTEM_MODULE_DEFINITION',
    moduleDefId: 2,
    permissionKey: 'QA_CUSTOM',
    permissionName: 'Custom',
  },
];

/** One catalogue definition, spelled with the model's own member names. */
const PERMISSION_DEFINITION: Permission = {
  permissionId: 5,
  permissionCode: 'SYSTEM_MODULE_DEFINITION',
  moduleDefId: 0,
  permissionKey: 'EDIT',
  permissionName: 'Edit Module',
};

const SENTINEL_DEFINITION: Permission = {
  permissionId: 0,
  permissionCode: '',
  moduleDefId: -1,
  permissionKey: 'VIEW',
  permissionName: '',
};

const RESOURCE_NOT_FOUND = Object.freeze({
  type: 'urn:dnnmigration:error:resource.not_found',
  title: 'Not Found',
  status: 404,
  detail: 'The requested resource does not exist.',
  traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
  correlationId: '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d',
});

describe('PermissionService', () => {
  let service: PermissionService;
  let httpMock: HttpTestingController;

  /** Claims the one open `GET` whose PATH is as given, whatever query string it carries. */
  const expectGet = (path: string): TestRequest =>
    httpMock.expectOne((request) => request.method === 'GET' && request.url === path);

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The function-based providers, which are the supported way to configure the HTTP client in a test
      // bed for this version.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(PermissionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // MANDATORY, AND THE MOST VALUABLE LINE IN THIS FILE. It fails the spec if any request was issued that
    // the test did not explicitly expect and consume, which is what makes "there are exactly two endpoints
    // and no others" an executable claim rather than a comment.
    httpMock.verify();
  });

  it('is created, and is a single instance held by the root injector', () => {
    expect(service).toBeTruthy();

    // The class registers itself against the root injector, so a second resolution must hand back the very
    // same object. Were it provided per component instead, each screen would hold its own copy, and the
    // tree-shaking that the root registration buys would be lost.
    expect(TestBed.inject(PermissionService))
      .withContext('resolved twice, the root injector yields one instance')
      .toBe(service);
  });

  describe('list', () => {
    it('reads the catalogue from the relative collection URL with no query string at all', async () => {
      const pending = firstValueFrom(service.list());

      const request = httpMock.expectOne(LIST_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.urlWithParams)
        .withContext('no filter was supplied, so no parameter may be invented')
        .toBe(LIST_URL);
      expect(request.request.params.keys().length)
        .withContext('the parameter set is empty')
        .toBe(0);

      request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });

      const emitted = await pending;

      // The payload is handed back exactly as it arrived. This service reshapes nothing, so
      // a sorted, de-duplicated or re-cased array here would be a defect.
      expect(emitted.data).toEqual(CATALOGUE_DEFINITIONS);
      expect(emitted.data.map((definition) => definition.permissionId))
        .withContext('each entry carries the identifier the detail read is addressed by')
        .toEqual([1, 2, 9]);
      expect(emitted.data[2].permissionKey)
        .withContext('a key outside the four this codebase declares reaches the caller intact')
        .toBe('QA_CUSTOM');
      expect(emitted.meta)
        .withContext('an unpaged sequence has no page to describe, so metadata is null')
        .toBeNull();
    });

    it('omits every parameter when the filter itself is absent or null', async () => {
      for (const absentFilter of [undefined, null]) {
        const pending = firstValueFrom(service.list(absentFilter));

        const request = httpMock.expectOne(LIST_URL);
        expect(request.request.params.keys().length).toBe(0);

        request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
        await pending;
      }
    });

    it('sends the module-definition filter under exactly that parameter name', async () => {
      const pending = firstValueFrom(service.list({ moduleDefinitionId: 42 }));

      // Selected by path and method, so the parameters can then be inspected individually
      // rather than compared as one opaque query string — see `expectGet` for why.
      const request = expectGet(LIST_URL);

      expect(request.request.params.get('moduleDefinitionId')).toBe('42');
      expect(request.request.params.keys())
        .withContext('one filter was supplied, so exactly one parameter travels')
        .toEqual(['moduleDefinitionId']);

      request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
      await pending;
    });

    it('scopes the catalogue by neither module nor page, so it sends no such parameter', async () => {
      // ⚠️ DIVERGENCE RECORDED DELIBERATELY. The permission CATALOGUE takes three filters and only three —
      // a permission code, a module-definition identifier and a permission key — as the server's own binder
      // declares.
      const pending = firstValueFrom(
        service.list({ permissionCode: 'SYSTEM_MODULE_DEFINITION', moduleDefinitionId: 42 }),
      );

      const request = expectGet(LIST_URL);

      expect(request.request.params.has('moduleId'))
        .withContext('the catalogue is not module-scoped')
        .toBeFalse();
      expect(request.request.params.has('tabId'))
        .withContext('the catalogue is not page-scoped')
        .toBeFalse();
      expect(request.request.params.has('portalId'))
        .withContext('a definition is installation-wide, so it is not tenant-scoped either')
        .toBeFalse();

      request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
      await pending;
    });

    it('sends the code and the key together, composing them as one conjunctive query', async () => {
      // The legacy code-and-key lookup, which selected a single definition by its scope and
      // its access right. Both parameters travel on one request; they are not two calls.
      const pending = firstValueFrom(
        service.list({ permissionCode: 'SYSTEM_MODULE_DEFINITION', permissionKey: 'EDIT' }),
      );

      const request = expectGet(LIST_URL);

      expect(request.request.params.get('permissionCode')).toBe('SYSTEM_MODULE_DEFINITION');
      expect(request.request.params.get('permissionKey')).toBe('EDIT');
      expect(request.request.params.keys().sort())
        .withContext('both filters travel, and nothing else joins them')
        .toEqual(['permissionCode', 'permissionKey']);

      request.flush({ data: [CATALOGUE_DEFINITIONS[1]], meta: null });
      await pending;
    });

    it('transmits filter values verbatim, neither trimming nor case-folding them', async () => {
      const pending = firstValueFrom(service.list({ permissionCode: '  system_Module  ' }));

      const request = expectGet(LIST_URL);

      expect(request.request.params.get('permissionCode'))
        .withContext('the value reaches the wire byte for byte')
        .toBe('  system_Module  ');

      request.flush({ data: [], meta: null });

      const emitted = await pending;

      // Nothing matching is a 200 carrying an empty array — never a 404, and never a null
      // array that a caller would have to guard against.
      expect(emitted.data).toEqual([]);
    });

    it('⚠️ sends zero, minus one and the empty string as real values, never as absent', async () => {
      // THE LOAD-BEARING CASE IN THIS FILE. Absence is decided by an explicit test against undefined and
      // null and by nothing else — not by truthiness, not by a sign test, not by a comparison against the
      // legacy marker.
      const cases: readonly { readonly moduleDefinitionId: number; readonly expected: string }[] =
        [
          // Zero: the role, page and module identities are all seeded at zero, so zero is a
          // real key throughout this schema rather than a stand-in for "none".
          { moduleDefinitionId: 0, expected: '0' },
          // Minus one: the legacy marker for a missing integer AND the seed of the portal identity, so one
          // value means both "no row" and "the first row" depending on context that a transport layer does
          // not have. It is forwarded, not interpreted.
          { moduleDefinitionId: -1, expected: '-1' },
        ];

      for (const { moduleDefinitionId, expected } of cases) {
        const pending = firstValueFrom(service.list({ moduleDefinitionId }));

        const request = expectGet(LIST_URL);

        expect(request.request.params.has('moduleDefinitionId'))
          .withContext(`${expected} is a value, so the parameter is present`)
          .toBeTrue();
        expect(request.request.params.get('moduleDefinitionId'))
          .withContext('forwarded exactly as written')
          .toBe(expected);

        request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
        await pending;
      }

      const pendingBlank = firstValueFrom(service.list({ permissionCode: '' }));

      const blankRequest = expectGet(LIST_URL);

      expect(blankRequest.request.params.has('permissionCode'))
        .withContext('an empty string is a value, not an omission')
        .toBeTrue();
      expect(blankRequest.request.params.get('permissionCode')).toBe('');

      blankRequest.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
      await pendingBlank;
    });

    it('omits an individual filter member that is explicitly undefined or null', async () => {
      const partialFilters = [
        { permissionCode: 'SYSTEM_TAB', moduleDefinitionId: undefined },
        { permissionCode: 'SYSTEM_TAB', moduleDefinitionId: null },
      ] as const;

      for (const filter of partialFilters) {
        const pending = firstValueFrom(service.list(filter));

        const request = expectGet(LIST_URL);

        expect(request.request.params.has('moduleDefinitionId'))
          .withContext('an unspecified filter contributes no parameter')
          .toBeFalse();
        expect(request.request.params.get('permissionCode'))
          .withContext('its neighbour is unaffected')
          .toBe('SYSTEM_TAB');
        expect(request.request.params.keys()).toEqual(['permissionCode']);

        request.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
        await pending;
      }
    });

    it('is unpaged, sending no page, page-size or ordering parameter', async () => {
      const pending = firstValueFrom(service.list({ permissionKey: 'VIEW' }));

      const request = expectGet(LIST_URL);

      for (const pagingParameter of ['page', 'pageIndex', 'pageSize', 'sort', 'sortDirection']) {
        expect(request.request.params.has(pagingParameter))
          .withContext(`the catalogue is unpaged, so it sends no ${pagingParameter}`)
          .toBeFalse();
      }

      expect(request.request.params.keys())
        .withContext('only the supplied filter travels')
        .toEqual(['permissionKey']);

      request.flush({ data: [CATALOGUE_DEFINITIONS[0]], meta: null });

      const emitted = await pending;
      expect(emitted.meta).toBeNull();
    });
  });

  describe('getById', () => {
    it('reads one definition from the relative identifier URL', async () => {
      const pending = firstValueFrom(service.getById(5));

      const request = httpMock.expectOne('/api/v1/permissions/5');

      expect(request.request.method).toBe('GET');
      expect(request.request.params.keys().length)
        .withContext('the identifier travels in the path, so no parameter is needed')
        .toBe(0);

      request.flush({ data: PERMISSION_DEFINITION, meta: null });

      const emitted = await pending;

      // The full record, not a bare key: a key alone could not say which scope code or module
      // definition it was declared under, and the same key recurs across scopes.
      expect(emitted.data).toEqual(PERMISSION_DEFINITION);
      expect(emitted.meta).toBeNull();
    });

    it('⚠️ treats zero as a real identifier, requesting it as a path segment', async () => {
      // The trap this case exists for: zero is falsy, and the role, page and module identities in this
      // schema are all seeded at zero, so zero names a genuine row.
      const pending = firstValueFrom(service.getById(0));

      const request = httpMock.expectOne('/api/v1/permissions/0');

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('zero is interpolated, not elided into a collection read')
        .toBe('/api/v1/permissions/0');

      request.flush({ data: SENTINEL_DEFINITION, meta: null });

      const emitted = await pending;

      expect(emitted.data).toEqual(SENTINEL_DEFINITION);
      expect(emitted.data.permissionId).toBe(0);
      expect(emitted.data.moduleDefId)
        .withContext('the legacy missing-integer marker is data here, and is preserved')
        .toBe(-1);
      expect(emitted.data.permissionCode)
        .withContext('the legacy missing-string marker is the empty string, and is preserved')
        .toBe('');
      expect(emitted.data.permissionName).toBe('');
    });

    it('requests a negative identifier as given, without interpreting it as absent', async () => {
      // Minus one is the legacy marker for a missing integer and simultaneously the seed of the portal
      // identity, so a transport layer cannot know which it is and must not guess.
      const pending = firstValueFrom(service.getById(-1));

      const request = httpMock.expectOne('/api/v1/permissions/-1');

      expect(request.request.method).toBe('GET');

      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      await expectAsync(pending).toBeRejected();
    });

    it('lets a failure reach the caller untouched, translating nothing', async () => {
      const pending = firstValueFrom(service.getById(5));

      httpMock
        .expectOne('/api/v1/permissions/5')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      const failure: unknown = await pending.then(
        () => null,
        (reason: unknown) => reason,
      );

      // The assertion stops at "the failure arrived, intact and of the transport's own type".
      expect(failure instanceof HttpErrorResponse)
        .withContext('the service swallows nothing, so the caller sees the transport failure')
        .toBeTrue();

      // Narrowed by the check above rather than cast, so the compiler proves the access is
      // sound instead of being told to trust it.
      if (failure instanceof HttpErrorResponse) {
        expect(failure.status).toBe(404);
        // The document arrives WHOLE, extensions included.
        expect(failure.error).toEqual(RESOURCE_NOT_FOUND);
      }
    });
  });

  describe('mutation surface', () => {
    it('offers no write path: two reads are the entire surface, and nothing else is issued', async () => {
      const listing = firstValueFrom(service.list());
      const listRequest = httpMock.expectOne(LIST_URL);
      expect(listRequest.request.method)
        .withContext('the collection is read, never written')
        .toBe('GET');
      listRequest.flush({ data: CATALOGUE_DEFINITIONS, meta: null });
      await listing;

      const single = firstValueFrom(service.getById(5));
      const byIdRequest = httpMock.expectOne('/api/v1/permissions/5');
      expect(byIdRequest.request.method)
        .withContext('a definition is read, never written')
        .toBe('GET');
      byIdRequest.flush({ data: PERMISSION_DEFINITION, meta: null });
      await single;

      // Stated explicitly as well as relied upon in afterEach, so a reader sees the claim at the point it
      // is made: the entire public surface has now been exercised, and the request log is empty.
      httpMock.verify();
    });

    it('performs no request until the returned observable is consumed', () => {
      // Both methods hand back a COLD observable, so nothing reaches the network until a caller subscribes
      // and every caller controls its own teardown.
      service.list();
      service.list({ permissionKey: 'READ' });
      service.getById(5);

      expect(httpMock.match(() => true).length)
        .withContext('no subscription, so nothing was ever sent')
        .toBe(0);
    });
  });
  // -------------------------------------------------------------------------
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  // -------------------------------------------------------------------------
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

    it('refuses a catalogue listing whose entry is not an object', () => {
      expectViolationAt(
        service.list(),
        LIST_URL,
        { data: [CATALOGUE_DEFINITIONS[0], 'VIEW'], meta: null },
        'response.data[1]',
      );
    });

    it('refuses a catalogue listing entry whose identifier is missing', () => {
      const malformedEntry: Record<string, unknown> = { ...CATALOGUE_DEFINITIONS[0] };

      delete malformedEntry['permissionId'];

      expectViolationAt(
        service.list(),
        LIST_URL,
        { data: [malformedEntry], meta: null },
        'response.data[0].permissionId',
      );
    });

    it('refuses a catalogue listing that is not an array', () => {
      expectViolationAt(service.list(), LIST_URL, { data: 'VIEW', meta: null }, 'response.data');
    });

    it('refuses a definition whose scope code is absent', () => {
      const malformed: Record<string, unknown> = { ...PERMISSION_DEFINITION };

      delete malformed['permissionCode'];

      expectViolationAt(
        service.getById(5),
        '/api/v1/permissions/5',
        { data: malformed, meta: null },
        'response.data.permissionCode',
      );
    });

    it('admits a definition carrying both legacy in-band markers', () => {
      const values: unknown[] = [];

      service.getById(0).subscribe({ next: (value: unknown) => values.push(value) });

      httpMock
        .expectOne('/api/v1/permissions/0')
        .flush({ data: SENTINEL_DEFINITION, meta: null });

      expect(values).toEqual([{ data: SENTINEL_DEFINITION, meta: null }]);
    });

    it('admits a permission key the catalogue extends with', () => {
      const values: unknown[] = [];

      service.getById(5).subscribe({ next: (value: unknown) => values.push(value) });

      httpMock
        .expectOne('/api/v1/permissions/5')
        .flush({ data: { ...PERMISSION_DEFINITION, permissionKey: 'DEPLOY' }, meta: null });

      expect(values.length).toBe(1);
    });
  });

  // -------------------------------------------------------------------------
  // WHO ANNOUNCES A FAILURE
  // -------------------------------------------------------------------------
  describe('marks every request as presented by its caller', () => {
    it('marks both operations', () => {
      const swallow = { error: () => undefined };

      service.list().subscribe(swallow);
      service.getById(5).subscribe(swallow);

      const issued = httpMock.match(() => true);

      expect(issued.length).toBe(2);

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
