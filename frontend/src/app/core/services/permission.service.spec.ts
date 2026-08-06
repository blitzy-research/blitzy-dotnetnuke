/**
 * Specification for {@link PermissionService}, the client half of the read-only
 * permission catalogue.
 *
 * NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains ZERO automated tests of
 * any kind — no test project, no fixture, no harness — so nothing here is a translation of
 * a prior assertion. Every case below was derived from the two contracts this spec sits
 * between: the service's own public surface and the server's binder.
 *
 * WHAT IS ACTUALLY BEING PROTECTED HERE. The service under test is transport only: two
 * reads, no state, no caching, no reshaping. A spec over code that thin earns its place by
 * pinning the things a compiler cannot see and a browser will not warn about — the exact
 * spelling of a query parameter, the exact shape of a URL, and above all the rule that
 * decides whether a parameter is sent at all. Each of those is a silent failure mode: get
 * one wrong and the application still builds, still bundles, still deploys, and then
 * quietly returns the wrong rows or none. That is the class of defect these assertions
 * exist to catch.
 *
 * TWO ENDPOINTS, AND PROOF THERE ARE NO OTHERS. The service exposes exactly `list` and
 * `getById`, and the catalogue is deliberately read-only: no create, update or delete
 * method exists on the client, and no mutation route exists on the server to receive one.
 * `verify()` in `afterEach` is what turns that claim into an executable one — it fails the
 * spec if any request was issued that the test did not explicitly expect, so an accidental
 * second call, a retry, a prefetch or an invented endpoint cannot slip through unnoticed.
 *
 * ⚠️ EVERY ASSERTED URL IS RELATIVE, AND THAT IS THE POINT. Specs compile against the
 * production environment: the workspace's `test` target declares no file replacements at
 * all, and the `production` build configuration's replacement list is empty while only
 * `development` substitutes an alternative — so `environment.ts` IS the production file,
 * and its `apiBaseUrl` is the relative `'/api/v1'`. The consequence reaches well past this
 * spec. The reverse proxy in front of the deployed application forwards `/api/` to the API
 * container over the SAME origin, and the container's service hostname does not resolve in
 * a browser at all. An absolute base URL would therefore type-check, build, bundle and
 * deploy without a single complaint from any step in the toolchain, and then fail for every
 * real user. These relative assertions are part of the defence against that, which is why
 * they are hand-written literals rather than values derived from the endpoint builder: a
 * literal fails when the builder changes, whereas a derived value would agree with the
 * mistake and report success.
 *
 * THE URL BUILDER AND THE PARAMETER SERIALISER ARE NOT IMPORTED HERE, DELIBERATELY. This
 * spec depends on the service and the catalogue model and nothing else. Importing the
 * builder to construct the expected URL, or the serialiser to construct the expected query
 * string, would compare each helper against itself and pass even when both were wrong
 * together. Those modules carry their own specs; this one asserts the observable result.
 *
 * NO REQUEST HEADER IS ASSERTED. The correlation identifier and the bearer credential are
 * attached by interceptors configured in the application's provider set, not by this
 * service, and this spec configures no interceptor. Asserting a header here would test a
 * pipeline that is deliberately absent from this test bed, and would begin to duplicate the
 * interceptor specs that own those concerns.
 *
 * NO TRANSLATED ERROR MESSAGE IS ASSERTED. Translation of an RFC 7807 problem document into
 * something a person reads belongs to the error interceptor. The single failure case below
 * proves only that the transport failure reaches the caller intact and unswallowed — which
 * is this service's entire responsibility on the failure path, since it handles nothing.
 *
 * MIGRATION: the catalogue is UNPAGED and the spec pins that. It is small, bounded
 * reference data seeded by the upgrade scripts and returned whole, so the listing sends no
 * page coordinate, page size or ordering parameter, and the response envelope's metadata
 * companion is present-and-null because a sequence payload has no page to describe. A page
 * parameter appearing on this request later would be a contract change, and the assertion
 * below is what surfaces it.
 *
 * MIGRATION: SENTINEL FIDELITY IS THE LOAD-BEARING PROPERTY IN THIS FILE. The legacy code
 * encoded "missing" in-band rather than with a null, and it chose values that the schema
 * also uses as real keys. `Library/Components/Shared/Null.vb` returns `-1` for a missing
 * integer (L41-L45, whose body is `Return -1`) and — the trap — the EMPTY STRING for a
 * missing string (L71-L75, whose body is literally `Return ""`), never a null; its
 * companion test consequently reports "missing" for `-1`, for `""` and for `false` alike.
 * Meanwhile `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`
 * seeds identities into that same value space: the portal key is declared `IDENTITY(-1, 1)`
 * at L77, and the role, page and module keys are all `IDENTITY(0, 1)` at L115, L140 and
 * L221. So `-1` is simultaneously "no integer" and a real portal, and `0` is simultaneously
 * falsy and a real role, page and module. Any code that decided whether to send a
 * parameter by asking whether it was truthy would silently discard a legitimate row, and
 * the request would succeed while selecting the wrong data. The cases below prove the
 * decision is made by an explicit test against undefined and null and by nothing else, so
 * `0`, `-1` and the empty string all travel as the real values they are.
 *
 * MIGRATION: the `";"`-delimited role string and the bracketed user token are NOT built,
 * asserted or parsed anywhere in this spec. The legacy display format is
 * `Library/Components/Security/Permissions/ModulePermissionController.vb:L239-L251` —
 * `GetModulePermissions` gated each grant on `AllowAccess = True AndAlso PermissionKey =
 * PermissionKey` (L243), appended `RoleName + ";"` for a role grant (L246) or
 * `"[" + UserID.ToString + "];"` for a user grant (L248), and returned
 * `";" & strRoles & strUsers` (L251). It packed two different kinds of subject into one
 * string that every consumer then had to split apart again, and a role whose name contained
 * the delimiter could not survive the round trip. The current API publishes catalogue
 * definitions and bare keys, so no fixture here contains either form.
 *
 * MIGRATION: two closed vocabularies exist in this system and they are NOT interchangeable;
 * conflating them is the likeliest mistake available in this area, so this spec touches only
 * one. The PERSISTED key vocabulary is `VIEW`, `EDIT`, `READ` and `WRITE` — upper case,
 * where the name IS the stored value, compared with exact string equality and never
 * case-insensitively (`ModulePermissionController.vb:L36`, `:L243` and `:L333`). The
 * AUTHORISATION POLICY names are a different set entirely, they are server start-up
 * identifiers rather than data, and they never travel in a request from this service. Every
 * key fixture below is drawn from the persisted vocabulary, and no policy name is ever
 * asserted as though it were one.
 *
 * MIGRATION: there is no refusal prefix in this generation of the platform. Later
 * permission systems marked a denial by prefixing the key with an exclamation mark; this
 * codebase does not, and the absence was measured rather than assumed — searching every
 * file under `Library/Components/Security/Permissions/` for that literal returns zero hits.
 * Denial is carried by the allow-access flag on a stored grant row alone. No fixture below
 * carries a prefixed key.
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

/**
 * The catalogue listing URL, written out in full rather than derived.
 *
 * RELATIVE BY REQUIREMENT — see this file's header for why an absolute origin would pass
 * every build step and then fail in a browser. Hand-written on purpose: deriving it from
 * the endpoint builder would make this spec agree with that builder even when both were
 * wrong.
 */
const LIST_URL = '/api/v1/permissions';

/**
 * The whole catalogue vocabulary, as the listing endpoint publishes it.
 *
 * A BARE ARRAY OF KEY STRINGS, NOT AN ARRAY OF RECORDS. That is the server's contract
 * rather than a convenience taken here: a list of keys is precisely what the vocabulary is,
 * and it is what a client-side permission check tests against. Full records come from the
 * single-definition read instead, where a key alone would be ambiguous because the same key
 * is declared repeatedly across scopes.
 *
 * All four persisted keys appear, upper case and exactly as stored. `READ` and `WRITE` are
 * folder-scope keys that a module or page screen will never encounter, but they belong to
 * the vocabulary, so a fixture claiming to be the whole catalogue includes them.
 */
const CATALOGUE_KEYS: readonly string[] = ['VIEW', 'EDIT', 'READ', 'WRITE'];

/**
 * One catalogue definition, spelled with the model's own member names.
 *
 * IDENTIFIER SPELLING IS LOAD-BEARING. Each identifier member ends in a single lower-case
 * `d`, because the server's camelCase policy lower-cases a leading run of capitals: a
 * server property named with a trailing all-capitals abbreviation would serialise with that
 * abbreviation intact and would then arrive as `undefined` on the client with no compile
 * error anywhere to catch it. The names below match the server DTO exactly.
 *
 * `moduleDefId` IS ZERO HERE ON PURPOSE, AND ZERO MEANS SOMETHING. It is how the
 * page-scoped and folder-scoped rows are stored — a definition belonging to no module
 * definition — so it must never be read as "missing". The member is not nullable, because
 * the column is not.
 */
const PERMISSION_DEFINITION: Permission = {
  permissionId: 5,
  permissionCode: 'SYSTEM_MODULE_DEFINITION',
  moduleDefId: 0,
  permissionKey: 'EDIT',
  permissionName: 'Edit Module',
};

/**
 * A definition carrying both legacy in-band markers, to prove the transport preserves them.
 *
 * The integer member holds `-1` and a text member holds the empty string: the two values
 * `Null.vb` returns for a missing integer (L41-L45) and a missing string (L71-L75). The API
 * serialises with its ignore condition set to never, so neither is elided on the wire — a
 * member with nothing to say arrives PRESENT, carrying `-1` or `""` rather than going
 * missing. This fixture is flushed unchanged and compared unchanged, so any coalescing or
 * normalisation introduced between the wire and the caller would fail the comparison.
 *
 * The identifier is `0`, which the single-definition read treats as a real key rather than
 * as an absent one.
 */
const SENTINEL_DEFINITION: Permission = {
  permissionId: 0,
  permissionCode: '',
  moduleDefId: -1,
  permissionKey: 'VIEW',
  permissionName: '',
};

/**
 * The `404` this endpoint answers with when no definition bears the identifier, COMPLETE.
 *
 * ⚠️ EVERY MEMBER IS PRESENT ON THE REAL RESPONSE, and the shape was read off the server
 * rather than abbreviated for convenience. `ApiResults.Complete<T>` turns a value-free success
 * into this exact document — its code, title and detail are fixed constants there
 * (`ResourceNotFoundCode`, `ResourceNotFoundDetail`) — and
 * `ValidationProblemDetailsFactory` then attaches the trace and correlation identifiers.
 *
 * An earlier revision of this file flushed `{ title: 'Not Found', status: 404 }`. Nothing
 * asserted against it was WRONG, because these cases only claim that a failure reaches the
 * caller untranslated — but a partial document is a poor oracle for the layers above: the
 * error interceptor branches on `type`, and the support reference a person is asked to quote
 * is read from `correlationId` first and `traceId` second. A fixture missing all three lets a
 * consumer that depends on them pass here and fail against the real server.
 *
 * The detail deliberately names neither the identifier asked for nor the resource kind, so an
 * unauthorised caller cannot distinguish "this exists but is not yours" from "this does not
 * exist" — the enumeration oracle every refusal in this API is written to avoid.
 */
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

  /**
   * Claims the one open `GET` whose PATH is as given, whatever query string it carries.
   *
   * WHY A PREDICATE RATHER THAN A URL OR AN OBJECT. Every one of the testing backend's three
   * matcher forms compares against the URL INCLUDING its query string — verified by reading
   * the installed matcher in `node_modules/@angular/common/fesm2022/http/testing.mjs`, which
   * tests `request.urlWithParams` for the string form, and again for the object form's `url`
   * member. So neither of those can select a request by path alone. The alternative would be
   * to spell out a full expected query string, which would silently bake in a parameter ORDER
   * that no contract promises and would turn every parameter assertion into one opaque
   * string comparison whose failures are hard to read. The predicate matches on the path, and
   * each parameter is then asserted individually by name, which is what makes a failure say
   * WHICH parameter was wrong.
   *
   * The bare-path assertion is deliberately kept here too: `request.url` carries no query
   * string, so a request that appended its filters to the path instead of sending them as
   * parameters would fail to match rather than passing quietly.
   */
  const expectGet = (path: string): TestRequest =>
    httpMock.expectOne((request) => request.method === 'GET' && request.url === path);

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The function-based providers, which are the supported way to configure the HTTP
      // client in a test bed for this version. The real client is provided first and the
      // testing backend then replaces its transport, so the service under test resolves the
      // ordinary client and cannot tell it is being tested.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(PermissionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // MANDATORY, AND THE MOST VALUABLE LINE IN THIS FILE. It fails the spec if any request
    // was issued that the test did not explicitly expect and consume, which is what makes
    // "there are exactly two endpoints and no others" an executable claim rather than a
    // comment. An invented endpoint, a stray retry, a duplicated call or a prefetch all
    // surface here.
    httpMock.verify();
  });

  it('is created, and is a single instance held by the root injector', () => {
    expect(service).toBeTruthy();

    // The class registers itself against the root injector, so a second resolution must
    // hand back the very same object. Were it provided per component instead, each screen
    // would hold its own copy, and the tree-shaking that the root registration buys would
    // be lost.
    expect(TestBed.inject(PermissionService))
      .withContext('resolved twice, the root injector yields one instance')
      .toBe(service);

    // Constructing the service issues no request. `verify()` in afterEach proves it: a
    // service that fetched anything eagerly would fail this test without a single explicit
    // assertion about it, which is the point of keeping the guard in afterEach.
  });

  describe('list', () => {
    it('reads the catalogue from the relative collection URL with no query string at all', async () => {
      const pending = firstValueFrom(service.list());

      // Matching by plain string compares against the URL INCLUDING its query string, so
      // this expectation succeeding is itself the proof that nothing was appended. The two
      // assertions that follow state the same fact directly rather than leaving it implicit
      // in the matcher's behaviour.
      const request = httpMock.expectOne(LIST_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.urlWithParams)
        .withContext('no filter was supplied, so no parameter may be invented')
        .toBe(LIST_URL);
      expect(request.request.params.keys().length)
        .withContext('the parameter set is empty')
        .toBe(0);

      request.flush({ data: CATALOGUE_KEYS, meta: null });

      const emitted = await pending;

      // The payload is handed back exactly as it arrived. This service reshapes nothing, so
      // a sorted, de-duplicated or re-cased array here would be a defect.
      expect(emitted.data).toEqual(CATALOGUE_KEYS);
      expect(emitted.meta)
        .withContext('an unpaged sequence has no page to describe, so metadata is null')
        .toBeNull();
    });

    it('omits every parameter when the filter itself is absent or null', async () => {
      // Both spellings of "no filter" must behave identically. `undefined` arises from
      // calling with no argument at all; `null` arises from a caller forwarding a cleared
      // filter, which a signal-backed store does routinely.
      for (const absentFilter of [undefined, null]) {
        const pending = firstValueFrom(service.list(absentFilter));

        const request = httpMock.expectOne(LIST_URL);
        expect(request.request.params.keys().length).toBe(0);

        request.flush({ data: CATALOGUE_KEYS, meta: null });
        await pending;
      }
    });

    it('sends the module-definition filter under exactly that parameter name', async () => {
      const pending = firstValueFrom(service.list({ moduleDefinitionId: 42 }));

      // Selected by path and method, so the parameters can then be inspected individually
      // rather than compared as one opaque query string — see `expectGet` for why.
      const request = expectGet(LIST_URL);

      // ⚠️ THE SPELLING IS THE ASSERTION. The server binds this parameter by name, so a
      // near-miss — an abbreviated form, or a trailing all-capitals abbreviation — binds to
      // nothing, is silently ignored, and returns the UNFILTERED catalogue. That is a
      // successful response carrying the wrong rows, which no status code reveals.
      expect(request.request.params.get('moduleDefinitionId')).toBe('42');
      expect(request.request.params.keys())
        .withContext('one filter was supplied, so exactly one parameter travels')
        .toEqual(['moduleDefinitionId']);

      request.flush({ data: CATALOGUE_KEYS, meta: null });
      await pending;
    });

    it('scopes the catalogue by neither module nor page, so it sends no such parameter', async () => {
      // ⚠️ DIVERGENCE RECORDED DELIBERATELY. The permission CATALOGUE takes three filters
      // and only three — a permission code, a module-definition identifier and a permission
      // key — as the server's own binder declares. It accepts no module identifier and no
      // page identifier, because a catalogue row is a DEFINITION of an access right rather
      // than a grant of one, and definitions are installation-wide: the table carries no
      // portal column, let alone a module or page column. Scoped GRANT rows are a
      // server-side concern that this API does not publish at all.
      //
      // Asserting the absence is worth a test rather than a comment, because the mistake it
      // guards against is a plausible one. Someone reaching for "which permissions apply to
      // this module" would naturally try a module identifier here; the parameter would bind
      // to nothing, be ignored in silence, and return the whole catalogue as though the
      // filter had been honoured. This case fails the moment such a parameter starts being
      // sent, whether by a widened filter type or by a caller passing one through.
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

      request.flush({ data: CATALOGUE_KEYS, meta: null });
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

      request.flush({ data: ['EDIT'], meta: null });
      await pending;
    });

    it('transmits filter values verbatim, neither trimming nor case-folding them', async () => {
      // Surrounding whitespace and mixed case are both preserved. The server adjudicates
      // whether such a value matches anything; normalising it here would be a second copy of
      // a rule that lives there, and the two copies would eventually disagree. Worse, the
      // stored key comparison is exact and never case-insensitive, so a client that helpfully
      // upper-cased a code would change which rows the server returned.
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
      // THE LOAD-BEARING CASE IN THIS FILE. Absence is decided by an explicit test against
      // undefined and null and by nothing else — not by truthiness, not by a sign test, not
      // by a comparison against the legacy marker. Each value below is falsy in JavaScript
      // and legitimate in this schema at the same time, so a truthiness test anywhere on the
      // path would drop the filter, and the request would still succeed while returning the
      // whole catalogue instead of the selected rows.
      const cases: readonly { readonly moduleDefinitionId: number; readonly expected: string }[] =
        [
          // Zero: the role, page and module identities are all seeded at zero, so zero is a
          // real key throughout this schema rather than a stand-in for "none".
          { moduleDefinitionId: 0, expected: '0' },
          // Minus one: the legacy marker for a missing integer AND the seed of the portal
          // identity, so one value means both "no row" and "the first row" depending on
          // context that a transport layer does not have. It is forwarded, not interpreted.
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

        request.flush({ data: CATALOGUE_KEYS, meta: null });
        await pending;
      }

      // The empty string is the third trap, and the subtlest: the legacy helper returns it
      // for a missing string rather than returning a null, so `""` is simultaneously "no
      // value" in the legacy encoding and a perfectly transmissible value here. It travels.
      const pendingBlank = firstValueFrom(service.list({ permissionCode: '' }));

      const blankRequest = expectGet(LIST_URL);

      expect(blankRequest.request.params.has('permissionCode'))
        .withContext('an empty string is a value, not an omission')
        .toBeTrue();
      expect(blankRequest.request.params.get('permissionCode')).toBe('');

      blankRequest.flush({ data: CATALOGUE_KEYS, meta: null });
      await pendingBlank;
    });

    it('omits an individual filter member that is explicitly undefined or null', async () => {
      // The counterpart to the case above, and the reason it is safe to send falsy values:
      // the two spellings of "no value" ARE honoured, so a caller never has to smuggle
      // absence in as a magic number. A store that clears one filter while keeping another
      // relies on exactly this.
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

        request.flush({ data: CATALOGUE_KEYS, meta: null });
        await pending;
      }
    });

    it('is unpaged, sending no page, page-size or ordering parameter', async () => {
      // The catalogue is small, bounded reference data seeded by the upgrade scripts, so it
      // is returned whole. This is a contract statement rather than an optimisation: the
      // response envelope's metadata companion is null precisely because there is no page to
      // describe, and a caller reading a page count from this endpoint would find none. If a
      // paging parameter ever starts appearing here, this case fails and the envelope
      // expectation has to be revisited with it.
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

      request.flush({ data: ['VIEW'], meta: null });

      const emitted = await pending;
      expect(emitted.meta).toBeNull();
    });
  });

  describe('getById', () => {
    it('reads one definition from the relative identifier URL', async () => {
      const pending = firstValueFrom(service.getById(5));

      // The literal is written out in full, including the identifier, so the assertion pins
      // the whole shape of the URL: the base, the version segment, the collection segment and
      // the identifier appended as a path segment rather than as a query parameter.
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
      // The trap this case exists for: zero is falsy, and the role, page and module
      // identities in this schema are all seeded at zero, so zero names a genuine row.
      // A truthiness test or a lower-bound check in the URL builder would either drop the
      // segment — producing a request to the COLLECTION, which answers 200 with an array and
      // so looks like a success — or refuse the call outright. Neither is acceptable, and
      // neither would be caught by a type check.
      const pending = firstValueFrom(service.getById(0));

      const request = httpMock.expectOne('/api/v1/permissions/0');

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('zero is interpolated, not elided into a collection read')
        .toBe('/api/v1/permissions/0');

      request.flush({ data: SENTINEL_DEFINITION, meta: null });

      const emitted = await pending;

      // The response carries both legacy in-band markers, and both survive the round trip
      // untouched: the empty strings are not turned into nulls and the minus one is not
      // turned into a zero or a null. The API's ignore condition is set to never for exactly
      // this reason, so these members arrive present rather than omitted.
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
      // Minus one is the legacy marker for a missing integer and simultaneously the seed of
      // the portal identity, so a transport layer cannot know which it is and must not guess.
      // It is interpolated exactly as supplied; the read itself reports whether such a
      // definition exists, which is a better answer than any local test could give.
      const pending = firstValueFrom(service.getById(-1));

      const request = httpMock.expectOne('/api/v1/permissions/-1');

      expect(request.request.method).toBe('GET');

      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      await expectAsync(pending).toBeRejected();
    });

    it('lets a failure reach the caller untouched, translating nothing', async () => {
      const pending = firstValueFrom(service.getById(5));

      // Absence is reported by status rather than by a null payload, so a successful response
      // from this endpoint always carries a definition. The legacy accessor returned a null
      // object for an unknown identifier, which every caller then had to test for.
      httpMock
        .expectOne('/api/v1/permissions/5')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      const failure: unknown = await pending.then(
        () => null,
        (reason: unknown) => reason,
      );

      // The assertion stops at "the failure arrived, intact and of the transport's own type".
      // Turning a problem document into something a person reads is the error interceptor's
      // responsibility, and this test bed configures no interceptor, so asserting a message,
      // a severity or a problem-document shape here would be testing a collaborator that is
      // deliberately absent.
      expect(failure instanceof HttpErrorResponse)
        .withContext('the service swallows nothing, so the caller sees the transport failure')
        .toBeTrue();

      // Narrowed by the check above rather than cast, so the compiler proves the access is
      // sound instead of being told to trust it.
      if (failure instanceof HttpErrorResponse) {
        expect(failure.status).toBe(404);
        // The document arrives WHOLE, extensions included. Asserting the identity of the
        // fixture is deliberately as far as this goes: what each member MEANS to a person is
        // the error interceptor's concern, but that the members are all still there when the
        // interceptor gets them is this transport's concern, and it is the property a partial
        // fixture could not have stated.
        expect(failure.error).toEqual(RESOURCE_NOT_FOUND);
      }
    });
  });

  describe('mutation surface', () => {
    it('offers no write path: two reads are the entire surface, and nothing else is issued', async () => {
      // THE CATALOGUE IS READ-ONLY, AND THIS IS THE HONEST WAY TO ASSERT IT. Catalogue rows
      // are reference data seeded by the upgrade scripts and written only by the module
      // installer, which is out of scope; there is no administration screen for them anywhere
      // in the legacy tree. So no create, update or delete method exists on this service, and
      // no route exists on the server to receive one — a write here would invite a partial
      // reimplementation of the installer over installer-owned data.
      //
      // The absence is proven structurally rather than by probing for missing members.
      // Reflection or a loosened type could ask "is there a `delete` property?", but that
      // would assert against a shape the compiler already guarantees, and it would need the
      // very escape hatches this codebase forbids. Instead: exercise the whole public surface,
      // account for every request it makes, and let `verify()` in afterEach fail if a single
      // additional request was issued by anything. A mutating call added later would either
      // fail to compile at its call site or surface here as an unexpected request.
      //
      // The compile-time half of the guarantee is the stronger half and needs no runtime
      // assertion: `PermissionService` declares exactly `list` and `getById`, so
      // `service.create(...)`, `service.update(...)` and `service.delete(...)` are not calls
      // this spec could be written to make — they do not type-check, and no cast is available
      // here to force one.
      const listing = firstValueFrom(service.list());
      const listRequest = httpMock.expectOne(LIST_URL);
      expect(listRequest.request.method)
        .withContext('the collection is read, never written')
        .toBe('GET');
      listRequest.flush({ data: CATALOGUE_KEYS, meta: null });
      await listing;

      const single = firstValueFrom(service.getById(5));
      const byIdRequest = httpMock.expectOne('/api/v1/permissions/5');
      expect(byIdRequest.request.method)
        .withContext('a definition is read, never written')
        .toBe('GET');
      byIdRequest.flush({ data: PERMISSION_DEFINITION, meta: null });
      await single;

      // Stated explicitly as well as relied upon in afterEach, so a reader sees the claim at
      // the point it is made: the entire public surface has now been exercised, and the
      // request log is empty.
      httpMock.verify();
    });

    it('performs no request until the returned observable is consumed', () => {
      // Both methods hand back a COLD observable, so nothing reaches the network until a
      // caller subscribes and every caller controls its own teardown. This matters for a
      // service whose callers are signal-backed stores: a method that fired eagerly would
      // request on construction, before any screen had asked for data, and would do so again
      // on every subsequent call regardless of whether anyone was listening.
      service.list();
      service.list({ permissionKey: 'READ' });
      service.getById(5);

      // Nothing was subscribed, so nothing was sent. Claiming EVERY open request with an
      // always-true predicate and asserting the count is zero is the strongest available form:
      // it is indifferent to path and query string, so it would catch a request to any URL at
      // all, including the filtered listing whose query string a literal comparison would miss.
      // It is also a real expectation rather than a bare matcher call, so the case reports a
      // genuine assertion instead of being flagged as having none.
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

    it('refuses a key list carrying a non-string element', () => {
      expectViolationAt(
        service.list(),
        LIST_URL,
        { data: ['VIEW', 7], meta: null },
        'response.data[1]',
      );
    });

    it('refuses a key list that is not an array', () => {
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
      // ⚠ `-1` AND `""` ARE VALUES HERE, NOT ABSENCES. They are what `Null.vb` returns for a
      // missing integer and a missing string, they arrive PRESENT because the API never elides
      // a written member, and no decoder may coalesce either one.
      const values: unknown[] = [];

      service.getById(0).subscribe({ next: (value: unknown) => values.push(value) });

      httpMock
        .expectOne('/api/v1/permissions/0')
        .flush({ data: SENTINEL_DEFINITION, meta: null });

      expect(values).toEqual([{ data: SENTINEL_DEFINITION, meta: null }]);
    });

    it('admits a permission key the catalogue extends with', () => {
      // The four keys the model's union names are the ones this application SWITCHES on, but
      // the catalogue is extensible: a module package may register its own. Closing the set
      // would refuse a whole catalogue because one third-party entry was unfamiliar.
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
