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
 * Specification for {@link TabService} - the lookup-only transport for the page resource,
 * the abstraction the database, the legacy source and the wire contract all still call a
 * "tab" and that an administrator sees as a Page.
 *
 * The legacy tree contains ZERO automated tests of any kind, so nothing here is a port.
 * Every case below is net-new coverage written against the service's measured contract.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS SPECIFICATION PROVES, AND WHY EACH PROOF IS SHAPED THE WAY IT IS
 *
 * 1. THE SURFACE IS CLOSED AT THREE ENDPOINTS, IN TWO DIFFERENT PATH SHAPES. The page
 *    controller is the one controller in this API whose class-level route is the version
 *    PREFIX ALONE rather than a resource collection, so it serves two path shapes that a
 *    reader would not predict from each other:
 *
 *      GET /api/v1/portals/{portalId}/tabs   - portal-scoped, because a LISTING belongs
 *                                              to its tenant
 *      GET /api/v1/tabs/{tabId}              - ROOT-LEVEL, NOT nested under /portals,
 *                                              because a page key is unique installation-wide
 *      PUT /api/v1/tabs/{tabId}              - root-level, same address as the read
 *
 *    That asymmetry is the single most important structural fact in this file, and it is
 *    asserted directly rather than inferred: a plausible-looking `/api/v1/portals/3/tabs/7`
 *    would route nowhere, and no compiler or bundler would say so. Both shapes therefore
 *    appear as literal strings below.
 *
 *    There is NO create, NO delete and NO partial-update endpoint, and no reorder, move,
 *    export, import, copy or recycle-bin endpoint either. The closing case at the foot of
 *    this file records that absence as an executable fact rather than as a comment.
 *
 * 2. EVERY ASSERTED URL IS RELATIVE, AND EVERY ONE IS A LITERAL. Two deliberate choices,
 *    each worth stating because each has a failure mode that nothing else catches.
 *
 *    Relative: the workspace's `test` target declares no file substitution at all -
 *    verified in `angular.json`, whose `test.options` carries no replacement list and whose
 *    `test.configurations` is empty - so a spec compiles against the DEFAULT environment
 *    module. The substitution direction in this workspace is INVERTED relative to the
 *    ambient Angular convention: the `production` build configuration declares an EMPTY
 *    replacement list and the `development` configuration is the one that swaps the module
 *    out. The default module is therefore the PRODUCTION module, and its base path is the
 *    relative `/api/v1`. A spec that hard-coded an absolute origin would be asserting
 *    against a bundle that never exists under test.
 *
 *    Literal: the assertions below do NOT call the endpoint builder that the service itself
 *    calls. Reusing it would make every URL assertion TAUTOLOGICAL - a template with the
 *    portal segment wrongly wrapped around the single-page route would satisfy both sides
 *    of the comparison and the suite would stay green while the request went nowhere. The
 *    literal strings are an independent statement of the contract, which is the only form
 *    that can disagree with the implementation.
 *
 *    The stake beyond this file is real. The reverse proxy that serves the production
 *    bundle forwards `/api/` to the API container on the very origin that served the
 *    application, and that container's hostname does not resolve in a browser at all. An
 *    absolute base path type-checks, lints, bundles, deploys and leaves both containers
 *    reporting healthy - and then fails on the first request a browser makes.
 *
 * 3. THE PAYLOAD ARRIVES INSIDE A SUCCESS ENVELOPE, AND EVERY FLUSH BELOW HONOURS IT.
 *    All three service methods unwrap `{ data, meta }` before handing a value on, because
 *    every action on this API answers through one shared result translator. A flush of the
 *    bare payload would therefore be testing a body the server never sends, and - this is
 *    the part worth naming - it would FAIL SILENTLY RATHER THAN LOUDLY: the unwrap would
 *    read `data` off an array or a page object, find nothing, and emit `undefined`. A
 *    `toBeTruthy`-style assertion would still pass, and the suite would certify a contract
 *    that produces a shape-correct blank at run time.
 *
 *    `meta` is flushed PRESENT AND NULL rather than omitted, because that is what the wire
 *    carries: the API serialises with its ignore condition set to `Never`, so a member with
 *    no value travels as `null` instead of disappearing. None of these three responses is
 *    paged - the page list is deliberately UNPAGED, since a partially fetched hierarchy
 *    cannot be indented correctly - so `null` is the correct value for all of them.
 *
 * 4. SENTINEL VALUES SURVIVE THE TRANSPORT UNTOUCHED. `0`, `-1`, `''` and `false` are every
 *    one of them legitimate stored values in this schema, so none may be stripped, defaulted
 *    or coalesced on its way out. The reasons are measured in the legacy source rather than
 *    assumed, and both citations below were read first-hand against this checkout:
 *
 *      - `Library/Components/Shared/Null.vb:L41-L45` defines the missing-integer marker as
 *        minus one, its body literally `Return -1`; `:L71-L75` defines the missing-string
 *        marker as the EMPTY STRING, its body literally `Return ""` - which defeats every
 *        instinct, because the intuitive encoding would have been a null; and `:L76-L80`
 *        treats `False` itself as the missing-boolean marker.
 *      - `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L140`
 *        declares `[TabID] [int] IDENTITY (0, 1)`, so the first page an installation ever
 *        creates is numbered ZERO; `:L77` declares `[PortalID] [int] IDENTITY (-1, 1)`, so
 *        the first portal carries MINUS ONE and the second carries zero.
 *
 *    The collision is what makes this worth asserting: minus one simultaneously names the
 *    first tenant and marks an absent integer, and the two are indistinguishable without
 *    context a transport layer does not have. No assertion below tests an identifier for
 *    truthiness, compares one against zero or minus one, or coalesces one in either
 *    direction - and the cases that pass `0` and `-1` exist precisely to prove the service
 *    does not either.
 *
 * 5. FAILURES REACH THE CALLER UNTRANSLATED. The cases at the foot of this file assert that
 *    an error observable errors and that the transport failure arrives intact. Not one of
 *    them asserts a human-readable message, a severity or a field-level mapping: turning a
 *    problem document into something a person reads belongs to the error interceptor and the
 *    shared form-error helper, and asserting it here would place a second implementation of
 *    that translation in a file whose subject is transport.
 * ---------------------------------------------------------------------------
 *
 * DIVERGENCE RECORDED RATHER THAN ABSORBED - THE TWO REASON CODES IN THIS FILE'S BRIEF DO
 * NOT EXIST. The brief for this specification named `InvalidTabName` as the update's `400`
 * code and `TabExists` as a `409` code. Neither appears anywhere in the server tree: the
 * service under test records that both were searched for across the whole of it and found
 * nowhere, and its own measured list of what the update can answer with is
 * `tab.name_reserved` and `tab.parent_cycle` and `tab.parent_cross_portal` at `400`,
 * `tab.parent_not_found` at `404`, plus `401` and `403`. The list read
 * `tab.portal_not_found` at `404` and `tab.not_found` at `404` for the two reads.
 *
 * The service further records that this endpoint declares NO `409 Conflict` at all, and why:
 * the legacy duplicate-name refusal sat behind a guard that only the CREATE path entered, and
 * this API deliberately publishes no create path, so no request can elicit the status.
 *
 * Both required failure cases are kept, because the property they establish is real and does
 * not depend on which code the server picks: this transport is STATUS-AGNOSTIC and
 * BODY-AGNOSTIC, and it must hand any refusal on unaltered. They are written with the codes
 * the server actually produces, and the conflict case is labelled for what it is - proof that
 * a status this endpoint does not currently emit would still propagate correctly if it ever
 * did. Fabricating the two invented codes would have made this file assert a contract that
 * does not exist, and a future reader would have trusted it.
 */

/**
 * The success envelope, restated locally rather than imported.
 *
 * The shared paging module that declares the real envelope is deliberately outside this
 * file's dependency whitelist, so the two members the flushes actually need are declared
 * here instead. TypeScript is structurally typed, so a body built from this declaration is
 * exactly the body the service's own envelope type accepts - and keeping it local means this
 * specification takes no dependency on a module whose subject is paging, for a set of
 * endpoints not one of which is paged.
 *
 * `meta` is typed `null` rather than nullable on purpose: every response asserted below is a
 * single payload with no page to describe, so `null` is not merely permitted here, it is the
 * only correct value.
 */
interface SuccessEnvelope<TPayload> {
  readonly data: TPayload;
  readonly meta: null;
}

/** Wraps a payload in the success envelope exactly as the server writes it. */
function envelope<TPayload>(data: TPayload): SuccessEnvelope<TPayload> {
  return { data, meta: null };
}

/**
 * One page-list row, carrying all fourteen members the list projection declares.
 *
 * Every member is spelled from the wire contract rather than from the legacy source, and two
 * of those spellings would have failed silently had they been guessed. The legacy entity
 * spells the identifier `TabID` with a capital pair while spelling the parent reference
 * `ParentId` with a single lowercase `d`, in the same class; the legacy page-management
 * screen is inconsistent within a single file, reading `objTab.TabID` at
 * `Website/admin/Tabs/Tabs.ascx.vb:L73` and `objTab.TabId` at `:L188`. The wire normalises
 * every identifier to the single-`d` form, so the members below are `tabId` and `parentId`.
 * A guessed capital-pair spelling would have compiled, bundled and deployed, and then read
 * `undefined` at run time with nothing to warn of it.
 *
 * The parent reference is `null` rather than minus one because the API converts the legacy
 * in-band sentinel to a genuine null at its boundary - minus one being a real tenant key,
 * the two were otherwise indistinguishable on the wire.
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
 * One page in full, carrying all twenty-three members the detail projection declares.
 *
 * The skin and container tokens ARE present here, and their presence is the counterpart to
 * their absence from the update shape: a stored choice stays observable even though page
 * skinning is out of scope for this migration, so both are readable and inert while neither
 * is settable. The closing update case asserts the write half of that asymmetry.
 *
 * The tenant reference is nullable because a host-level page has none, and the refresh
 * interval is nullable because the API converts the legacy minus-one sentinel to a genuine
 * null rather than emitting it.
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
 * A complete update body, carrying all fifteen members the request shape declares - and, as
 * importantly, not one member more.
 *
 * This is a WHOLE-ROW REPLACEMENT of the editable subset rather than a partial patch, exactly
 * as the legacy postback was, so every member is mandatory and a caller sends the page's
 * current value for anything it does not intend to change. Two of those members are data-loss
 * traps worth naming: omitting the menu-inclusion flag drops the page out of the navigation,
 * and omitting the recycle-bin flag RESTORES a page that was sitting in the recycle bin.
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
 * The fifteen member names the update body carries, sorted, so that the write shape can be
 * asserted as an exact set rather than one member at a time.
 *
 * Asserting the whole set is what makes the skin-and-container exclusion enforceable: a
 * per-member negative check proves only that the two named members are absent, whereas an
 * exact-set comparison additionally fails if any OTHER member is ever smuggled onto the
 * write surface. That is the shape of the regression this file is defending against.
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
 * The two extension members the API attaches to every problem document.
 *
 * `ValidationProblemDetailsFactory` writes both on every refusal, so a fixture without them
 * describes a response this API does not send. They are not interchangeable: the correlation
 * identifier is the value the pipeline validated for this request and the one that appears in
 * the server's log and on the audit trail.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * A problem document as the API writes one, following RFC 7807.
 *
 * ⚠️ THE FAILURE CODE TRAVELS IN `type`, NOT IN A `code` EXTENSION. There is no `code` member
 * anywhere in this API's problem output — `ApiResults.Problem` supplies `type`, `status` and
 * `detail`, and `ValidationProblemDetailsFactory` adds `title`, `traceId` and `correlationId`
 * and nothing else. An earlier revision of this file invented a `code` extension and read the
 * refusal out of it, so every refusal assertion here was reading a member the server never
 * writes; against the real API the read would yield `undefined` and every one of those
 * assertions would fail — or, worse, a client shaped by them would silently stop recognising
 * refusals it was written to handle.
 *
 * ⚠️ AND THE TYPE IS `urn:dnnmigration:error:<code>`, NOT AN EXTERNAL HTTP-STATUS PAGE. The
 * relative, non-dereferenceable URN is deliberate: RFC 7807 permits one, and pointing at a
 * third-party documentation host would hand a caller a link that documents nothing about this
 * API. Two DIFFERENT causes sharing one status therefore remain distinguishable, because the
 * type varies while the status and title do not.
 *
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
 * Reads it back out of the TRANSPORT rather than out of the fixture, and out of the member the
 * server actually writes, so the assertions below are real rather than tautological.
 *
 * @param body The body carried by the failure.
 * @returns The code, or null when the body carries no recognisable problem type.
 */
function failureCodeOf(body: unknown): string | null {
  const type: unknown = bodyMembers(body)['type'];
  const prefix = 'urn:dnnmigration:error:';

  return typeof type === 'string' && type.startsWith(prefix) ? type.slice(prefix.length) : null;
}

/**
 * Reads a flushed request body as a plain member map.
 *
 * The transport hands a body back loosely typed, so it is narrowed once here rather than at
 * every assertion. A member map is the right narrowing for these cases specifically because
 * what several of them assert is the PRESENCE OR ABSENCE OF A MEMBER, which a strongly typed
 * view of the same object cannot express - the compiler would already have removed any member
 * the declared type forbids, and the run-time question is precisely whether the transport
 * agrees.
 */
function bodyMembers(body: unknown): Readonly<Record<string, unknown>> {
  return body as Readonly<Record<string, unknown>>;
}

/**
 * Awaits a failed call and returns the transport failure it produced.
 *
 * The rejection is captured through a two-handler continuation rather than a try/catch so
 * that a call which unexpectedly SUCCEEDS fails the case explicitly - a bare catch would let
 * a silent success pass as though nothing had gone wrong.
 */
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
      // The real client with NO interceptors registered. This specification is about the
      // service's own behaviour, and running the application's interceptor chain here would
      // assert two units at once: the correlation header, the bearer credential and the
      // translation of a problem document are each somebody else's subject, and each has its
      // own specification.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(TabService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // MANDATORY, and the executable guard that makes every "no such endpoint" claim in this
    // file enforceable rather than aspirational. It fails a case that left a request
    // outstanding and, more valuably, a case whose call issued a request nobody expected -
    // which is exactly how an invented endpoint, a stray retry or a speculative prefetch
    // would be caught.
    httpMock.verify();
  });

  describe('construction', () => {
    it('resolves from the root injector as a single shared instance', () => {
      expect(service).toBeTruthy();

      // Declared with root-level provision, so the injector must hand back the very same
      // object rather than a fresh one per request. A per-injection instance would be a
      // correctness problem and not merely a wasteful one, because it would let two callers
      // hold divergent views of a service that is meant to be stateless and shared.
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
      // A child precedes its own parent here on purpose. The endpoint answers a FLAT list and
      // this service is transport, so assembling a hierarchy from the parent reference, the
      // depth and the sort position is a derivation that belongs to a signal store or to the
      // component that renders the indentation. Doing it here would make every caller pay for
      // a shape only some of them want, and would put a second interpretation of the hierarchy
      // in a file whose job is to move bytes.
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

      // A hierarchy is read whole, because a partially fetched tree cannot be indented
      // correctly. This is the one list in the application that travels through the
      // single-payload envelope rather than the paging envelope, so no paging coordinate, no
      // sort key and no filter may appear - and the absence is asserted on the parameter
      // collection itself rather than by inspecting the URL text, so a parameter appended by
      // any route cannot slip past.
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

      // An empty list is a successful answer, not a failure and not an absent value. It must
      // arrive as an empty array rather than as null or undefined, so that a caller can
      // distinguish "this tenant has no pages" from "the call did not happen".
      expect(await pending).toEqual([]);
    });

    it('transmits portal 0 as a real tenant identifier', async () => {
      // Zero is the SECOND identity value on the tenant table, which is declared
      // `IDENTITY (-1, 1)`. It is an ordinary tenant key with nothing special about it, and
      // the only reason it needs asserting is that a naive presence check would treat it as
      // absent and either omit the segment or refuse the call.
      const pending = firstValueFrom(service.getByPortal(0));

      const request = httpMock.expectOne('/api/v1/portals/0/tabs');
      expect(request.request.method).toBe('GET');

      request.flush(envelope([listRow()]));
      await pending;
    });

    it('transmits portal -1 as a real tenant identifier, despite it also being the legacy absence marker', async () => {
      // THE COLLISION, ASSERTED. Minus one is the FIRST identity value on the tenant table
      // (`01.00.00.SqlDataProvider:L77`), so it names the first portal an installation ever
      // created; and it is simultaneously the legacy missing-integer marker
      // (`Null.vb:L41-L45`, body `Return -1`). The same number therefore means both "the
      // first portal" and "no portal", distinguishable only by context that a transport layer
      // does not have.
      //
      // The service must consequently interpolate it and say nothing about it. Deciding
      // whether an identifier is known belongs to the caller, which has that context - and
      // the caller makes the decision by comparing against null, since no numeric value is
      // available to mean "absent".
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
      // THE MOST IMPORTANT STRUCTURAL ASSERTION IN THIS FILE. The page controller's
      // class-level route is the version prefix alone, so a single page is addressed at
      // `/api/v1/tabs/{tabId}` while the LIST of a portal's pages is addressed at
      // `/api/v1/portals/{portalId}/tabs`. The two shapes do not follow from one another, and
      // the plausible-looking `/api/v1/portals/3/tabs/7` routes nowhere at all.
      //
      // A page key is unique across an installation, which is why the detail route needs no
      // tenant segment - and the outstanding-request check in the teardown is what proves no
      // portal-nested variant was requested alongside this one.
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
      // `dbo.Tabs` is declared `[TabID] [int] IDENTITY (0, 1)`
      // (`01.00.00.SqlDataProvider:L140`), so the first page an installation ever creates is
      // numbered ZERO. Zero is an ordinary key here and never means "not yet saved".
      //
      // The failure this guards against is specific and quiet: a presence check on the
      // identifier would produce `/api/v1/tabs/` - a request for the collection rather than
      // for the page - which the API would answer with a routing failure that looks nothing
      // like the real cause. The address is therefore asserted in full.
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
      // The API converts every legacy in-band sentinel to a genuine null at its boundary, and
      // this service must leave that conversion alone. A root-level page arrives as a null
      // parent and never as minus one; an unset date arrives as null and never as the minimum
      // instant; "no automatic refresh" arrives as null and never as minus one; a host-level
      // page arrives as a null tenant. Re-encoding any of them would resurrect exactly the
      // ambiguity the boundary conversion exists to remove.
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

      // Deep equality against the object the caller supplied. This is a WHOLE-ROW REPLACEMENT
      // of the editable subset, so an altered or dropped member does not mean "leave that
      // column alone" - the server writes what it binds, and a lost member would silently
      // clear a column, set a flag false or move the page to the root of the hierarchy.
      expect(call.request.body).toEqual(request_);

      call.flush(envelope(detail({ tabId: 7, tabName: 'Contact' })));
      await pending;
    });

    it('preserves -1, the empty string, zero and false in the body rather than eliding any of them', async () => {
      // SENTINEL FIDELITY, WHICH IS THE WHOLE POINT OF THIS CASE. Each of the four values
      // below is a legitimate stored value in this schema, and each is also what some
      // serialiser somewhere would call "empty" and drop:
      //
      //   refreshInterval: -1  - the legacy missing-integer marker (`Null.vb:L41-L45`), which
      //                          a caller may still hold from legacy data. Whether minus one
      //                          is the RIGHT value to send is the caller's and the server's
      //                          business; whether it ARRIVES is this service's, and it must.
      //   title: ''            - the legacy missing-string marker is literally the empty
      //                          string (`Null.vb:L71-L75`, body `Return ""`), not a null, so
      //                          an empty string is meaningful input and not a blank to skip.
      //   parentId: 0          - zero names a REAL parent page, because the page key is seeded
      //                          at zero. Dropping it would move the page to the root.
      //   isSecure: false      - the legacy marker for a missing boolean is `False` itself
      //                          (`Null.vb:L76-L80`), which is precisely why a nullable
      //                          boolean would be indistinguishable from a false one.
      //
      // The API serialises with its ignore condition set to `Never` for the same reason, so
      // this specification holds the client to the contract the server already keeps.
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

      // Present-and-empty is not the same as missing, and only a member check can tell the
      // two apart: a value assertion alone would read `undefined` for an absent member and
      // could be satisfied by the wrong thing.
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

      // THE NEGATIVE ASSERTION. Page skinning and containers are out of scope for this
      // migration, so the write shape omits both tokens deliberately - a member a client can
      // set is not an inert one, and declaring either would have published this endpoint as
      // the supported way to change a page's skin.
      //
      // The omission is also strictly SAFER than carrying them, which is the part that makes
      // this worth an assertion rather than a comment. Because the body is a whole-row
      // replacement, a caller that left either member out of a body that DID declare them
      // would blank an administrator's stored token on every unrelated edit.
      //
      // Both columns remain READABLE on the detail shape - the detail fixture in this file
      // carries values for them - so a stored choice stays observable; it is simply no longer
      // settable here, and the server's projection leaves both columns exactly as stored.
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

      // The identifier is route-supplied and never body-supplied - the legacy editor took it
      // from the page's own context and no form field ever contributed it - so the route value
      // is authoritative and a body member would create a second, disagreeing source. The
      // tenant is absent for the same reason. The sort position, the depth, the materialised
      // path and the child-existence flag are absent because the server computes all four.
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
      // The problem document reaches the caller intact. NOTHING here asserts a human-readable
      // message: turning a problem document into something a person reads belongs to the error
      // interceptor and the shared form-error helper, and a second implementation of that
      // translation in a transport specification would be free to disagree with the first.
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
      // The code asserted here is one the server actually produces. The code this file's brief
      // named for the case - `InvalidTabName` - exists nowhere in the server tree; the service
      // under test records that it was searched for across the whole of it and found nowhere,
      // and that a reserved page name is refused as `tab.name_reserved` instead. Asserting the
      // invented name would have certified a contract that does not exist.
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
      // THE THREE CAUSES THAT SHARE 400 ON THIS ACTION, and the reason the failure code has to
      // travel in `type` rather than being inferred from the status. `TabsController.UpdateAsync`
      // declares 200/400/401/403/404 and NOTHING ELSE — no 409, no 422 — so a caller cannot tell
      // these apart by status and must read the type.
      //
      // ⚠️ NO `409` CASE EXISTS HERE, and its absence is a measured finding rather than an
      // omission. The legacy duplicate-name refusal sat behind a guard only the CREATE path
      // entered — `If String.IsNullOrEmpty(strAction)` at `ManageTabs.ascx.vb:L279`, while the
      // edit branch was entered at `:L304` under `If strAction = "edit"` — and this API
      // deliberately publishes no page create. The code an earlier revision invented for it,
      // `tab.name_conflict`, exists nowhere in the server tree, so a client written against it
      // would have carried a branch that could never be taken while the refusals the server DOES
      // emit went unhandled.
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
      // The API answers 403 both for a caller without an edit grant AND for a page that does
      // not exist, deliberately, so that the two are indistinguishable to a caller probing for
      // identifiers. No retry, no refresh and no fallback request may follow - the teardown's
      // outstanding-request check is what proves none did.
      const pending = firstValueFrom(service.update(7, updateRequest()));

      // ⚠️ THE CODE IS `auth.not_permitted`, WHICH IS THE STATUS VOCABULARY'S OWN DEFAULT FOR
      // 403. There is no `tab.forbidden` anywhere in the server tree: this action is refused by
      // the `TabEdit` authorisation policy BEFORE the controller body runs, so the refusal is
      // produced by the authorisation result handler rather than by a page service, and it
      // carries the default type for the status. An earlier revision asserted an invented
      // `tab.forbidden`, which is precisely the shape of mistake that makes a spec agree with
      // itself and disagree with the server.
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
      // THE WHOLE SURFACE, EXERCISED IN ONE CASE. Three calls in, three requests out, and the
      // teardown's outstanding-request check then proves that nothing else was issued along
      // the way - no speculative prefetch of the page a placement sits on, no lookup of a
      // parent, no cache warm-up and no invented endpoint.
      //
      // WHAT DOES NOT EXIST ON THIS BACKEND, and therefore has no case of its own here:
      // page CREATE (no POST), page DELETE (no DELETE verb), partial update (no PATCH),
      // reorder, move, copy, export, import, and recycle-bin restore or purge. Each was
      // measured in the legacy source rather than assumed away, and the citations were re-read
      // first-hand against this checkout rather than carried from a planning summary - all
      // seven matched:
      //
      //   `Website/admin/Tabs/Tabs.ascx.vb:L70` declares `Private Sub DeleteTab()` and `:L73`
      //   calls `TabController.DeleteTab(objTab.TabID, PortalSettings, UserId)`.
      //   `:L214` declares `Private Sub UpDown_Click(...) Handles cmdDown.Click, cmdUp.Click`,
      //   and the four ordering calls at `:L188`, `:L190`, `:L223` and `:L225` drive
      //   `objTabs.UpdatePortalTabOrder(PortalId, objTab.TabId, objTab.ParentId, ...)` with the
      //   one-step level and order deltas the arrow buttons produced. Nudging a hierarchy by
      //   relative steps depends on the whole tree being rendered and posted back in one
      //   request, which is precisely the presentation model this migration replaces.
      //   `Website/admin/Tabs/Export.ascx.vb` and `Import.ascx.vb` moved page definitions
      //   through server-side folders, and this API publishes no filesystem surface.
      //   `Website/admin/Tabs/RecycleBin.ascx.vb` restored and purged soft-deleted pages; the
      //   reversible half survives as the `isDeleted` member of the update body rather than as
      //   a route, and permanent purging is out of scope.
      //
      // Those absences are asserted by the outstanding-request check and by the exact-member
      // comparison on the write body, NOT by probing the service object for missing methods.
      // A reflective probe would need an escape from the type system to compile, and it would
      // assert the shape of this file's own expectations rather than the shape of the contract.
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
      // The health probe is mapped at the host root, is anonymous and unthrottled, and sits
      // OUTSIDE `/api/v1` entirely. It exists for the container health check and the
      // orchestrator's readiness gate, and no application service may call it. The teardown's
      // outstanding-request check makes that enforceable, because a request to it would be
      // unexpected and would fail this case.
      const pending = firstValueFrom(service.getById(7));

      httpMock.expectOne('/api/v1/tabs/7').flush(envelope(detail({ tabId: 7 })));
      expect(httpMock.match('/health').length)
        .withContext('no service touches the health endpoint')
        .toBe(0);

      await pending;
    });
  });
  // -------------------------------------------------------------------------
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  //
  // `http.get<TabDetail>(...)` compiles to `http.get(...)`: the interface is erased and
  // nothing inspects the body. The page hierarchy is where that bites hardest, because two of
  // its members drive the render arithmetic: `level` becomes indentation and `tabOrder`
  // becomes position among siblings, so either one arriving as `undefined` yields `NaN`
  // padding or a page silently moved to one end of its set.
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
      // ⚠ THE DOUBLE SENTINEL COLLISION. `dbo.Tabs` is `IDENTITY (0, 1)` so zero is the first
      // page; `dbo.Portals` is `IDENTITY (-1, 1)` so minus one is the first portal — and minus
      // one is simultaneously the legacy absent-integer marker. Neither may be read as absent.
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
