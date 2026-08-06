import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ModuleVisibility } from '../models/module.model';
import { ModuleService } from './module.service';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';

import type { HttpHeaders } from '@angular/common/http';
import type { Observable } from 'rxjs';
import type {
  CreateModuleRequest,
  ModuleDefinition,
  ModuleDetail,
  ModuleExportRequest,
  ModuleImportRequest,
  ModuleListItem,
  ModuleSettingsBag,
  UpdateModuleRequest,
} from '../models/module.model';
import type { ApiResponse, PagedResponse } from '../models/paged-result.model';

/**
 * Specification for {@link ModuleService}: TWELVE ENDPOINTS, AND NOTHING ELSE.
 *
 * The legacy tree contains no automated test of any kind, so nothing here was ported.
 * Every assertion below was authored either from the destination wire contract - read
 * out of `module.service.ts`, `module.model.ts` and `paged-result.model.ts` rather than
 * from a summary of them - or from the measured legacy behaviour cited inline, and each
 * legacy line reference in this file was opened and read rather than copied forward.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS FILE IS FOR, STATED AS A BOUNDARY RATHER THAN AS A COVERAGE TARGET
 * ---------------------------------------------------------------------------
 * The service under test is a transport layer: one method, one endpoint, one request,
 * no decision. That is a claim which is very easy to make and very easy to break by
 * accident, because the defects it admits are all SILENT - a request that succeeds
 * against the wrong row, a payload that arrives with a member quietly missing, a second
 * request nobody asked for. None of those fails a build, a type check or a lint pass.
 * So this specification is written to lock the SHAPE OF THE CALL: the verb, the exact
 * path, which values became query parameters, which values stayed in the body, and the
 * fact that exactly one request left the client.
 *
 * Three mechanisms do that work, and they are worth naming because each catches a
 * different class of mistake:
 *
 * 1. `httpMock.verify()` runs after EVERY specification below. It fails on an
 *    unmatched request, which is what turns "this method issues exactly one request"
 *    from a review comment into a test result. It is the only automated proof that the
 *    removal did not also re-read the collection, and that nothing here retries.
 * 2. Paths are asserted as LITERAL STRINGS. Building the expected path from the same
 *    route helper the service uses would assert nothing at all - the two sides would
 *    agree by construction and a wrong template would pass. The literals below are the
 *    independent statement of where each call goes.
 * 3. Bodies are compared WHOLE with `toEqual`, and the falsy members are chosen
 *    deliberately. See the sentinel note.
 *
 * ---------------------------------------------------------------------------
 * PATHS ARE RELATIVE HERE, AND THAT IS A PROPERTY OF THE TEST TARGET
 * ---------------------------------------------------------------------------
 * `angular.json` declares its `fileReplacements` in the direction opposite to the usual
 * Angular scaffold: the `production` configuration substitutes nothing, and only
 * `development` swaps the environment module out. `environment.ts` is therefore the
 * PRODUCTION module, and it carries a RELATIVE base path because the reverse proxy
 * serves the application and the API from one origin.
 *
 * The `test` target declares no substitution at all. Every specification in this file
 * consequently compiles against the production module and against its relative base, so
 * the expected paths below begin with the version prefix and no origin. Asserting an
 * absolute origin here would be asserting against a bundle that is never built under
 * test - it would pass, and it would be describing nothing.
 *
 * The environment module is deliberately NOT imported. Reading the base from it and
 * concatenating the rest would reproduce mechanism 2's failure: the assertion would
 * follow the configuration wherever it went instead of pinning it.
 *
 * ---------------------------------------------------------------------------
 * SENTINELS: WHY THE FIXTURES BELOW LOOK LIKE THEY ARE FULL OF EMPTY VALUES
 * ---------------------------------------------------------------------------
 * They are, and every one of them is load-bearing. This schema makes falsy values real
 * data, so a truthiness test anywhere in the transport layer corrupts a request while
 * returning a perfectly successful status:
 *
 * - `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` line 221
 *   declares `[ModuleID] [int] IDENTITY (0, 1)` and line 140 declares
 *   `[TabID] [int] IDENTITY (0, 1)`, so ZERO is the first module and the first page.
 * - Line 77 declares `[PortalID] [int] IDENTITY (-1, 1)`, so MINUS ONE is the first
 *   portal and ZERO is the second - both are real tenants.
 * - `Library/Components/Shared/Null.vb` lines 41-45 define the absent-integer marker as
 *   MINUS ONE, colliding head-on with that portal seed, and lines 71-75 define the
 *   absent-string marker as THE EMPTY STRING - the property body is literally
 *   `Return ""`, not `Nothing`.
 * - `Website/admin/Modules/Import.ascx.vb` line 51 shows the collision in the legacy
 *   source itself: `Private Shadows ModuleId As Integer = -1`, an identifier field
 *   seeded at the absent-integer marker.
 *
 * So `0`, `-1`, `''` and `false` must all reach the wire, and only a member that was
 * genuinely not supplied may be omitted. Several specifications below exist for no
 * other purpose than to fail if a truthiness filter is ever introduced.
 *
 * ---------------------------------------------------------------------------
 * WHAT IS DELIBERATELY NOT ASSERTED
 * ---------------------------------------------------------------------------
 * No business rule, because the service holds none: no validation, no ordering, no
 * filtering, no permission decision, no derived name and no cache policy. Refusals are
 * asserted as PROPAGATING and never as being interpreted - translating a problem
 * document into a message belongs to the shared error handler in the HTTP chain and to
 * the form-error helper, neither of which is registered in this test module, and
 * asserting their behaviour here would test two units at once. The exported document is
 * compared as text and never parsed, because deciding whether it is well formed is a
 * server decision. And no specification chains a second request onto a first, because
 * a second request is exactly the defect `httpMock.verify()` exists to catch.
 */

// ---------------------------------------------------------------------------
// Fixtures. Shared so that a member appearing in an assertion can be traced to one
// declaration, and shaped to the ACTUAL model contracts rather than to a convenient
// subset: every required member of every contract is present, because a partial fixture
// would let a required member be dropped from the wire without any specification
// noticing.
// ---------------------------------------------------------------------------

/**
 * One row of the module listing, carrying the full thirteen-plus-four member projection.
 *
 * `moduleId` and `tabId` are ZERO on purpose: a row keyed at the identity seed is the
 * one a truthiness test loses, and the listing is where such a loss would be least
 * visible.
 */
const LIST_ROW: ModuleListItem = {
  moduleId: 0,
  tabModuleId: 1,
  tabId: 0,
  moduleDefId: 4,
  moduleTitle: 'Announcements',
  friendlyName: 'Announcements',
  desktopModuleId: 2,
  moduleName: 'Announcements',
  description: '',
  version: '01.00.00',
  moduleOrder: 1,
  allTabs: false,
  visibility: ModuleVisibility.Maximized,
  isDeleted: false,
  displayTitle: true,
  startDate: null,
  endDate: null,
};

/**
 * One module placement in full.
 *
 * `portalId` is MINUS ONE - the first portal the schema ever creates and simultaneously
 * the legacy absent-integer marker - and `cacheTime` is ZERO, which is a chosen
 * lifetime rather than an unset one. Both survive verbatim in the specifications below.
 */
const DETAIL: ModuleDetail = {
  moduleId: 0,
  tabModuleId: 1,
  tabId: 0,
  portalId: -1,
  moduleDefId: 4,
  desktopModuleId: 2,
  moduleTitle: 'Announcements',
  allTabs: false,
  header: '',
  footer: '',
  startDate: null,
  endDate: null,
  inheritViewPermissions: true,
  isDeleted: false,
  moduleOrder: 1,
  cacheTime: 0,
  iconFile: '',
  visibility: ModuleVisibility.Maximized,
  displayTitle: true,
  friendlyName: 'Announcements',
  moduleName: 'Announcements',
  description: '',
  version: '01.00.00',
};

/**
 * One catalogue definition.
 *
 * `defaultCacheTime` is deliberately NOT zero while {@link DETAIL} carries a zero
 * `cacheTime`. The two are separate facts on separate contracts - the legacy class
 * declared them separately at `Library/Components/Modules/ModuleInfo.vb` line 203 and
 * line 482 - so a lifetime of zero folded into this default would be visible as the
 * value below appearing where a zero belongs.
 */
const DEFINITION: ModuleDefinition = {
  moduleDefId: 4,
  friendlyName: 'Announcements',
  desktopModuleId: 2,
  defaultCacheTime: 900,
  moduleName: 'Announcements',
  description: '',
  version: '01.00.00',
  isPremium: false,
  isAdmin: false,
  isPortable: true,
};

/**
 * A creation request in which EVERY optional-looking member is falsy.
 *
 * Four kinds of falsy value appear on purpose - the empty string, `false`, `null` and
 * `0` - so that a body comparison fails if any single kind is filtered out. A fixture
 * of plausible non-empty values would pass against a truthiness filter and prove
 * nothing.
 */
const CREATE_REQUEST: CreateModuleRequest = {
  moduleDefId: 4,
  tabId: 0,
  moduleTitle: '',
  allTabs: false,
  header: '',
  footer: '',
  startDate: null,
  endDate: null,
  inheritViewPermissions: false,
  moduleOrder: 0,
  cacheTime: 0,
  iconFile: null,
  visibility: ModuleVisibility.Maximized,
  displayTitle: false,
};

/**
 * A replacement request with the all-pages flag SET, which is the state the server
 * enforces its own field-level rule against.
 *
 * The three optional members are present and `false` rather than omitted, so that a
 * present-and-false member is proved to survive alongside an absent one.
 */
const UPDATE_REQUEST: UpdateModuleRequest = {
  tabId: 0,
  moduleTitle: '',
  allTabs: true,
  header: '',
  footer: '',
  startDate: null,
  endDate: null,
  inheritViewPermissions: false,
  isDeleted: false,
  moduleOrder: 0,
  cacheTime: 0,
  iconFile: null,
  visibility: ModuleVisibility.None,
  displayTitle: false,
  setAsDefaultSettings: false,
  applyToAllModules: false,
};

/**
 * Both settings maps, with cleared values in each.
 *
 * This is the most sentinel-sensitive payload in the whole contract. The absent-string
 * marker IS the empty string (`Library/Components/Shared/Null.vb` lines 71-75), the maps
 * legitimately hold cleared values, and a truthiness filter applied before sending would
 * DELETE every setting an operator had cleared while the request still answered
 * successfully. The empty-valued keys below are what makes that failure loud.
 */
const SETTINGS_BAG: ModuleSettingsBag = {
  moduleId: 0,
  tabModuleId: 1,
  moduleSettings: {
    Announcements_Description: '',
    Announcements_Length: '0',
    Announcements_Template: 'default',
  },
  tabModuleSettings: {
    Announcements_Heading: '',
    Announcements_Collapsed: 'false',
  },
};

/**
 * An exported document, as the server writes it.
 *
 * A STRING, and treated as an opaque one throughout. The legacy page obtained exactly
 * this - `Website/admin/Modules/Export.ascx.vb` line 157 read the document through
 * `CType(CType(objObject, IPortable).ExportModule(ModuleID), String)`, a live double cast
 * that only compiled because the administration code-behinds were built with Option
 * Strict OFF (`Website/release.config` line 125) - and then wrapped it in a declaration
 * and a type-carrying root element at lines 161-164 before writing it to a folder.
 *
 * The wrapping is server work now and the writing is gone, so this fixture is the whole
 * of what a caller receives. Nothing in this file inspects inside it.
 */
const EXPORTED_DOCUMENT =
  '<?xml version="1.0" encoding="utf-8" ?>' +
  '<content type="Announcements" version="01.00.00">' +
  '<announcement><title>Release</title><text></text></announcement>' +
  '</content>';

/**
 * The trailing-wildcard character, obtained by code point.
 *
 * Written this way so the character appears NOWHERE as a literal in this file. The
 * legacy readers decorated a filter pattern at the call site; the target appends any
 * pattern server-side inside the repositories, so a filter that arrived here already
 * decorated would be double-decorated. The specification that uses this constant fails
 * if the transport layer ever starts composing the pattern itself.
 */
const WILDCARD = String.fromCharCode(37);

/**
 * The five members an RFC 7807 problem document carries on this API.
 *
 * Declared locally rather than imported: the problem model is not a dependency of this
 * specification, and the point of the failure cases below is that the body is NOT
 * interpreted - it is flushed, and it comes back out of the error whole. Reading a field
 * map uses an index expression because `noPropertyAccessFromIndexSignature` is enabled.
 */
interface WireProblem {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly traceId: string;
  readonly correlationId: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

/**
 * The W3C trace identifier and the pipeline-validated correlation identifier.
 *
 * Both are attached to EVERY problem document by
 * `backend/src/DnnMigration.Api/Filters/ValidationProblemDetailsFactory.cs`, so a fixture
 * without them describes a response this API does not send. They are distinct on purpose:
 * the correlation identifier is the one an operator can find in the server's log and on the
 * audit trail, and it is the one a client should quote.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * The response title for each status code, exactly as the server's vocabulary spells it.
 *
 * Read from the `StatusVocabulary` table in
 * `backend/src/DnnMigration.Api/Filters/ValidationProblemDetailsFactory.cs`. The title
 * describes the CLASS of failure and is derived purely from the status code, which is why a
 * fixture that put the failure CODE in the title described a body no endpoint produces.
 */
const PROBLEM_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

/**
 * Builds a complete, server-emittable refusal body for one failure code.
 *
 * ⚠ THE PROBLEM TYPE IS `urn:dnnmigration:error:<code>` AND NOTHING ELSE, produced by the
 * single method `ApiResults.BuildProblemType`. ⚠ AND THE STATUS IS NOT A FREE PARAMETER:
 * `ApiResults.MapStatusCode` derives it from the code's final dotted segment, so the caller
 * must pass the status that mapping actually yields. The mapping relevant to this file:
 *
 *   * `not_found` ⇒ 404 — `module.not_found`, `module.definition_not_found`,
 *     `module.tab_not_found`, `module.placement_not_found`, `module.portal_not_found`
 *   * `forbidden` / `protected` ⇒ 403 — `module.edit_forbidden`,
 *     `module.administrator_forbidden`, `module.settings_protected`
 *   * `export_failed` / `upgrade_failed` ⇒ 500
 *   * everything else ⇒ 400 — `module.content_invalid`, `module.content_type_mismatch`,
 *     `module.not_portable`, `module.setting_invalid`, `module.request_invalid`
 *
 * ⚠ NO MODULE ENDPOINT DECLARES `409` OR `422`. `ModulesController` declares exactly
 * 200/201/204, 400, 401, 403, 404 and — on the two content-transfer actions — 500. An
 * earlier revision of this spec asserted the content refusals at `422`, which is a status
 * this controller cannot reach: the codes carry no conflict, not-found, forbidden or
 * internal-failure token, so they classify to `400` by the mapping's default arm.
 *
 * `errors` is deliberately absent rather than an empty object. Only the model binder
 * attaches a per-field map, and it never attaches an empty one; a fixture carrying `{}`
 * invites a consumer to treat "no field errors" and "field errors present but empty" as the
 * same thing.
 *
 * @param code The failure code, spelled exactly as the server publishes it.
 * @param status The status the server's mapping yields for that code.
 * @param detail The authored sentence the producing service placed on the outcome.
 * @returns The complete document, ready to flush.
 */
function problem(code: string, status: number, detail: string): WireProblem {
  return {
    type: `urn:dnnmigration:error:${code}`,
    // Non-null asserted on a lookup whose key set covers every status this file uses; an
    // unmapped status is a fixture defect and surfaces immediately.
    title: PROBLEM_TITLE[status]!,
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * Asserts that a single-resource read answered `404` rather than a payload-free success.
 *
 * ⚠ THE ONE SHAPE A SINGLE-RESOURCE READ CANNOT PRODUCE IS `200` WITH A NULL PAYLOAD.
 * `ApiResults.Complete<T>` converts a successful outcome carrying no value into `404` with
 * the code `resource.not_found`, so absence is reported by STATUS and never by a null inside
 * a success envelope. This helper is what the affected cases assert instead.
 *
 * @param recorded Everything the call produced.
 */
function expectResourceNotFound<T>(recorded: Recorded<T>): void {
  expect(recorded.values.length).withContext('absence is not an emitted value').toBe(0);
  expect(recorded.completions.length).toBe(0);
  expect(recorded.failures.length).toBe(1);

  const failure = recorded.failures[0];
  expect(failure).toBeInstanceOf(HttpErrorResponse);
  expect(failure.status).toBe(404);

  const body = failure.error as WireProblem;
  expect(body.type).toBe('urn:dnnmigration:error:resource.not_found');
  expect(body.title).toBe('Not Found');
  expect(body.detail)
    .withContext(
      'the detail names neither the identifier nor the resource kind, so an unauthorised ' +
        'caller cannot tell "not yours" from "does not exist"',
    )
    .toBe('The requested resource does not exist.');
}

/**
 * The `404` a single-resource read answers with when the thing addressed does not exist.
 *
 * Its code, title and detail are all fixed constants in `ApiResults`
 * (`ResourceNotFoundCode`, and `ResourceNotFoundDetail`), deliberately identical whichever
 * endpoint produced it so a client branches on one type.
 */
const RESOURCE_NOT_FOUND = problem(
  'resource.not_found',
  404,
  'The requested resource does not exist.',
);

/**
 * Everything one call produced: its values, its refusal and whether it completed.
 *
 * Collected into arrays rather than into single variables for two reasons. It makes the
 * ARITY assertable - "emitted exactly once" is as important as "emitted the right
 * value", and a transport method that emitted twice would otherwise pass - and it keeps
 * the captured refusal usable without a non-null assertion, which strict mode would
 * otherwise force at every failure specification.
 */
interface Recorded<T> {
  readonly values: T[];
  readonly failures: HttpErrorResponse[];
  readonly completions: boolean[];
}

/**
 * Subscribes immediately and records the outcome.
 *
 * Subscription must happen BEFORE the expectation, because these methods return cold
 * observables: nothing is dispatched until something subscribes, so an expectation
 * placed first would find no request at all. Everything the testing backend does is
 * synchronous, so by the time a flush returns the arrays below are already populated
 * and no specification in this file needs to be asynchronous.
 */
function record<T>(source: Observable<T>): Recorded<T> {
  const values: T[] = [];
  const failures: HttpErrorResponse[] = [];
  const completions: boolean[] = [];

  source.subscribe({
    next: (value: T) => {
      values.push(value);
    },
    error: (failure: HttpErrorResponse) => {
      failures.push(failure);
    },
    complete: () => {
      completions.push(true);
    },
  });

  return { values, failures, completions };
}

/** Asserts that a call carried a refusal out untranslated, and produced no value. */
function expectRefusal<T>(recorded: Recorded<T>, status: number, code: string): void {
  expect(recorded.values.length).toBe(0);
  expect(recorded.completions.length).toBe(0);
  expect(recorded.failures.length).toBe(1);

  const failure = recorded.failures[0];
  expect(failure).toBeInstanceOf(HttpErrorResponse);
  expect(failure.status).toBe(status);

  // The body is compared, not read for meaning: proving it arrived WHOLE is the whole
  // claim. Shaping it into something a user reads belongs to the shared error handler.
  //
  // The CODE is asserted through the problem TYPE, which is where the server publishes it —
  // `urn:dnnmigration:error:<code>`. An earlier revision asserted it through `title`, which
  // is derived from the status code alone and is therefore identical across every refusal
  // that shares a status; such an assertion could not tell one code from another.
  const body: WireProblem = failure.error as WireProblem;
  expect(body.type).toBe(`urn:dnnmigration:error:${code}`);
  expect(body.title)
    .withContext('the title names the class of failure, which the status alone decides')
    .toBe(PROBLEM_TITLE[status]!);
  expect(body.status)
    .withContext('the body agrees with the transport, as a real response does')
    .toBe(status);
}

/**
 * Asserts that a request set no header of its own.
 *
 * The bearer token and the correlation identifier are attached by the two functional
 * interceptors registered once at application configuration, and NEITHER is registered
 * in this test module - so a header appearing here would mean the service had started
 * setting one itself, which would put a second, divergent source of truth in the chain.
 *
 * `Accept` is checked for the same reason and one more: the export response is read as
 * text because the server declares an XML media type on THAT success response alone,
 * leaving refusals to arrive as the JSON problem documents every other endpoint returns.
 * Pinning an XML `Accept` would break exactly that negotiation.
 */
function expectNoClientHeaders(headers: HttpHeaders): void {
  expect(headers.has('Authorization')).toBeFalse();
  expect(headers.has('X-Correlation-Id')).toBeFalse();
  expect(headers.get('Accept')).toBeNull();
}

describe('ModuleService', () => {
  let service: ModuleService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client FIRST and the testing backend SECOND, and the order matters:
      // `provideHttpClientTesting()` overrides the backend that `provideHttpClient()`
      // installed, so reversing the two leaves the real backend in place and every
      // expectation below finds nothing. There are deliberately no interceptors here -
      // this specification is about the service's own behaviour, and running the chain
      // would mean asserting several units at once.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    // Resolved from the injector rather than listed above: the service declares itself
    // at the application root, so registering it here would create a SECOND instance and
    // test a registration this application never performs.
    service = TestBed.inject(ModuleService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The single most valuable line in the file. It fails on any request that no
    // expectation consumed, which is what proves each method issued exactly one - no
    // duplicate, no retry, and above all no follow-up read chained onto a write.
    httpMock.verify();
  });

  it('is resolvable from the application root injector', () => {
    expect(service).toBeInstanceOf(ModuleService);
  });

  // =========================================================================
  // PROOF A - the import route carries NO identifier, and the target travels in
  // the body. Written first because it is one of the two highest-value assertions
  // in this file: a target smuggled into the path would be a plausible-looking
  // 404, or worse a successful import against the wrong module.
  // =========================================================================
  describe('importModule - POST /api/v1/modules/import', () => {
    /** A document, a target and the two descriptive members, with the target present. */
    function importRequest(moduleId: number | null): ModuleImportRequest {
      return {
        moduleId,
        content: EXPORTED_DOCUMENT,
        folder: '',
        fileName: 'Announcements.Backup.xml',
      };
    }

    it('posts to the import route with NO identifier segment and no query parameter', () => {
      const recorded = record(service.importModule(importRequest(12)));

      // The exact string, asserted as a whole. The obvious mistake is to interpolate the
      // target between the collection segment and the operation segment, the way every
      // other module operation addresses its subject; doing so fails here, because this
      // path has no identifier position at all.
      const call = httpMock.expectOne('/api/v1/modules/import');
      expect(call.request.method).toBe('POST');
      expect(call.request.url).toBe('/api/v1/modules/import');
      expect(call.request.urlWithParams).toBe('/api/v1/modules/import');
      expect(call.request.params.keys()).toEqual([]);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('carries the target module in the request body rather than in the route', () => {
      const body = importRequest(12);
      const recorded = record(service.importModule(body));

      const call = httpMock.expectOne('/api/v1/modules/import');

      // MIGRATION: the legacy page received its target OUT OF BAND rather than as part of
      //   its address - `Website/admin/Modules/Import.ascx.vb` lines 67-68 parsed it from
      //   a request value into the field declared at line 51 - and line 169 then passed it
      //   as an argument alongside the file and folder, exactly as a body member is passed
      //   here. Line 200 handed that same identifier to the module's portability contract.
      //   Keeping the target in the payload also reflects that the payload is adjudicated
      //   WHOLE: the document, its declared type and the target are refused together, so
      //   splitting the target into the path would let a caller address a module the
      //   payload contradicts.
      expect(call.request.body).toEqual(body);
      expect(call.request.body as ModuleImportRequest).toEqual({
        moduleId: 12,
        content: EXPORTED_DOCUMENT,
        folder: '',
        fileName: 'Announcements.Backup.xml',
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('transmits a target of MINUS ONE unchanged, neither coalesced nor dropped', () => {
      // MIGRATION: minus one is a LEGITIMATE transmitted value on this contract.
      //   `Import.ascx.vb` line 51 declared `Private Shadows ModuleId As Integer = -1` -
      //   an identifier field seeded with the absent-integer marker from
      //   `Library/Components/Shared/Null.vb` lines 41-45. The member is nullable on the
      //   target contract precisely so an omission can be told apart from a caller naming
      //   module zero, which is a real module, so minus one must NOT be rewritten into an
      //   omission on the way out. The server rejects it on the caller's behalf.
      const recorded = record(service.importModule(importRequest(-1)));

      const call = httpMock.expectOne('/api/v1/modules/import');
      const sent = call.request.body as ModuleImportRequest;
      expect(sent.moduleId).toBe(-1);
      expect(Object.prototype.hasOwnProperty.call(sent, 'moduleId')).toBeTrue();

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('transmits a target of ZERO unchanged, because zero is a real module', () => {
      // `01.00.00.SqlDataProvider` line 221 seeds the module identity at zero, so a
      // truthiness or positive-value guard here would drop a request for the FIRST module
      // ever created and answer as though nothing were wrong.
      const recorded = record(service.importModule(importRequest(0)));

      const call = httpMock.expectOne('/api/v1/modules/import');
      const sent = call.request.body as ModuleImportRequest;
      expect(sent.moduleId).toBe(0);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('preserves an explicitly omitted target as null rather than substituting one', () => {
      const recorded = record(service.importModule(importRequest(null)));

      const call = httpMock.expectOne('/api/v1/modules/import');
      const sent = call.request.body as ModuleImportRequest;
      expect(sent.moduleId).toBeNull();
      expect(Object.prototype.hasOwnProperty.call(sent, 'moduleId')).toBeTrue();

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('sends the document as a JSON member, not as a multipart upload', () => {
      const recorded = record(service.importModule(importRequest(12)));

      const call = httpMock.expectOne('/api/v1/modules/import');

      // The strongest available proof that the body is a plain object and NOT a form: the
      // request's own content-type detection answers `null` for a form payload and
      // `application/json` for an object, so this single assertion rules out both a
      // multipart body and any hand-set multipart header.
      expect(call.request.detectContentTypeHeader()).toBe('application/json');
      expect(Object.prototype.toString.call(call.request.body)).toBe('[object Object]');
      expect(typeof (call.request.body as ModuleImportRequest).content).toBe('string');
      expectNoClientHeaders(call.request.headers);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('reads no file and derives nothing from the descriptive members', () => {
      // MIGRATION: `Import.ascx.vb` line 184 opened a stream against the portal's mapped
      //   home directory joined to the operator's folder and file name, line 190 then
      //   built a document from what it read, and line 197 compared a declared type
      //   against the module's own name. Every part of that is server behaviour now: the
      //   folder and file name below are descriptive payload members, forwarded verbatim
      //   and resolved against nothing.
      const recorded = record(
        service.importModule({
          moduleId: 12,
          content: EXPORTED_DOCUMENT,
          folder: 'Backups/',
          fileName: 'Announcements.Backup.xml',
        }),
      );

      const call = httpMock.expectOne('/api/v1/modules/import');
      const sent = call.request.body as ModuleImportRequest;
      expect(sent.folder).toBe('Backups/');
      expect(sent.fileName).toBe('Announcements.Backup.xml');
      expect(sent.content).toBe(EXPORTED_DOCUMENT);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('completes with no payload on 204', () => {
      const recorded = record(service.importModule(importRequest(12)));

      httpMock
        .expectOne('/api/v1/modules/import')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(recorded.completions.length).toBe(1);
      expect(recorded.failures.length).toBe(0);
    });

    it('propagates an invalid-document refusal untranslated', () => {
      // A SERVER decision, taken at `Import.ascx.vb` lines 190-192 in the legacy page:
      // the document was loaded and a parse failure became this message. Nothing on this
      // side parses, so nothing on this side can produce or interpret it.
      const recorded = record(service.importModule(importRequest(12)));

      httpMock.expectOne('/api/v1/modules/import').flush(
        problem(
          'module.content_invalid',
          400,
          'The submitted content is not a well-formed XML document.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );

      // 400, because the code carries no conflict, not-found, forbidden or
      // internal-failure token and therefore classifies by the mapping's default arm. The
      // import action declares 204/400/401/403/404/500 and no 422 at all.
      expectRefusal(recorded, 400, 'module.content_invalid');
    });

    it('propagates a wrong-type refusal untranslated', () => {
      // `Import.ascx.vb` line 197 compared the document's declared type against the
      // module's own name and reported this at line 204, with the same message reached
      // from the file-name check at line 217.
      const recorded = record(service.importModule(importRequest(12)));

      httpMock.expectOne('/api/v1/modules/import').flush(
        problem(
          'module.content_type_mismatch',
          400,
          'The submitted content does not belong to this module definition.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );

      expectRefusal(recorded, 400, 'module.content_type_mismatch');
    });

    it('propagates an unsupported-module refusal untranslated', () => {
      // Reached at `Import.ascx.vb` lines 208 and 214, both of which turn on the module
      // declaring a business controller class and being portable.
      const recorded = record(service.importModule(importRequest(12)));

      httpMock.expectOne('/api/v1/modules/import').flush(
        problem(
          'module.not_portable',
          400,
          'This module does not support the transfer of content.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );

      // ONE code covers both halves of the legacy gate. `Export.ascx.vb:L150` and
      // `Import.ascx.vb:L208`/`:L214` each tested `BusinessControllerClass <> "" And
      // IsPortable`, and the server reports the single conclusion rather than which half
      // failed - a distinction the caller could do nothing with.
      expectRefusal(recorded, 400, 'module.not_portable');
    });
  });

  // =========================================================================
  // PROOF B - the exported document comes back IN THE RESPONSE BODY at 200, and
  // nothing is written, parsed or named on the way. The other of the two
  // highest-value assertions here: an export that answered 201, or whose payload
  // had to be reassembled from somewhere other than the body, would be a contract
  // break that no compiler and no type check detects.
  // =========================================================================
  describe('exportModule - POST /api/v1/modules/{moduleId}/export', () => {
    /** The name a caller intends for the payload, plus an optional folder. */
    const EXPORT_REQUEST: ModuleExportRequest = {
      fileName: 'Announcements.Backup.xml',
      folder: 'Backups/',
    };

    /** Asserts a text-response refusal reached the caller byte for byte. */
    function expectTextRefusal<T>(
      recorded: Recorded<T>,
      status: number,
      rawBody: string,
    ): void {
      expect(recorded.values.length).toBe(0);
      expect(recorded.completions.length).toBe(0);
      expect(recorded.failures.length).toBe(1);

      const failure = recorded.failures[0];
      expect(failure).toBeInstanceOf(HttpErrorResponse);
      expect(failure.status).toBe(status);
      // Byte for byte, and deliberately not parsed: a refusal is carried out of this
      // layer exactly as it arrived.
      expect(failure.error).toBe(rawBody);
    }

    it('posts to the export sub-path of the addressed module', () => {
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12/export');
      expect(call.request.method).toBe('POST');
      expect(call.request.url).toBe('/api/v1/modules/12/export');
      expect(call.request.params.keys()).toEqual([]);

      call.flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });
      expect(recorded.values.length).toBe(1);
    });

    it('exports module ZERO at the literal path /api/v1/modules/0/export', () => {
      const recorded = record(service.exportModule(0, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/0/export')
        .flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });

      expect(recorded.values.length).toBe(1);
    });

    it('emits the flushed document byte for byte from the 200 RESPONSE BODY', () => {
      // THE ASSERTION THIS WHOLE BLOCK EXISTS FOR. Two things are locked at once: the
      // success status is 200 rather than the 201 the rest of this API's writes use,
      // because the request produces a payload rather than creating a resource; and the
      // payload is the response BODY, not a header, not a location to follow and not
      // something reassembled here.
      //
      // MIGRATION: `Website/admin/Modules/Export.ascx.vb` line 143 declared
      //   `Private Function ExportModule(ByVal ModuleID As Integer, ByVal FileName As
      //   String, ByVal Folder As String) As String`, and having obtained the document at
      //   line 157 the legacy page wrote it to a folder on the server. No filesystem
      //   endpoint exists in this API by design, so the string is simply returned.
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12/export')
        .flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });

      expect(recorded.values.length).toBe(1);
      expect(recorded.completions.length).toBe(1);
      expect(recorded.values[0]).toBe(EXPORTED_DOCUMENT);
      // Identical LENGTH as well as identical value: a re-serialising round trip would
      // preserve equality of meaning while changing the text, and this catches that.
      expect(recorded.values[0].length).toBe(EXPORTED_DOCUMENT.length);
      expect(typeof recorded.values[0]).toBe('string');
    });

    it('neither parses the document nor inspects anything inside it', () => {
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12/export')
        .flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });

      // Still raw markup on arrival: the declaration is intact, the wrapping element the
      // server added at `Export.ascx.vb` lines 161-164 is intact, and the empty element
      // inside it has not been normalised away. A document that had been through a parse
      // and a re-serialise would differ in at least one of the three.
      const emitted = recorded.values[0];
      expect(emitted.startsWith('<?xml version="1.0" encoding="utf-8" ?>')).toBeTrue();
      expect(emitted.includes('<content type="Announcements" version="01.00.00">')).toBeTrue();
      expect(emitted.includes('<text></text>')).toBeTrue();
      expect(emitted.endsWith('</content>')).toBeTrue();
    });

    it('emits an EMPTY document as an empty string rather than as an absence', () => {
      // `Export.ascx.vb` line 159 tested `If Content <> ""` and treated an empty document
      // as its own distinct outcome. That distinction survives: an empty payload is an
      // empty string here, never null and never a completion without a value. The
      // absent-string marker being the empty string
      // (`Library/Components/Shared/Null.vb` lines 71-75) is exactly why this needs saying.
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12/export')
        .flush('', { status: 200, statusText: 'OK' });

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toBe('');
      expect(recorded.values[0]).not.toBeNull();
      expect(recorded.completions.length).toBe(1);
    });

    it('forwards the requested name verbatim and derives no name of its own', () => {
      // MIGRATION: NO NAME IS COMPOSED HERE. `Export.ascx.vb` line 124 built the stored
      //   name by joining a fixed prefix, the module's programmatic name, the operator's
      //   text and an extension through a name-sanitising helper, behind the folder-and-text
      //   gate at line 121. Neither the composition nor the helper nor the gate is carried
      //   forward: whatever name the caller puts on the request is transmitted unchanged,
      //   and nothing is derived from it. This assertion fails the moment a prefix, an
      //   extension or a sanitising pass is reintroduced.
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12/export');
      expect(call.request.body).toEqual(EXPORT_REQUEST);

      const sent = call.request.body as ModuleExportRequest;
      expect(sent.fileName).toBe('Announcements.Backup.xml');
      expect(sent.folder).toBe('Backups/');

      call.flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });
      expect(recorded.values.length).toBe(1);
    });

    it('transmits an empty name and an omitted folder exactly as supplied', () => {
      const request: ModuleExportRequest = { fileName: '', folder: null };
      const recorded = record(service.exportModule(12, request));

      const call = httpMock.expectOne('/api/v1/modules/12/export');
      const sent = call.request.body as ModuleExportRequest;
      expect(sent.fileName).toBe('');
      expect(sent.folder).toBeNull();
      expect(Object.keys(sent).sort()).toEqual(['fileName', 'folder']);

      call.flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });
      expect(recorded.values.length).toBe(1);
    });

    it('sets no XML accept header and no header of any other kind', () => {
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12/export');
      expectNoClientHeaders(call.request.headers);
      // Explicit rather than implied by the helper: an XML accept header would break the
      // server's arrangement whereby only the SUCCESS response carries an XML media type
      // and refusals arrive as JSON problem documents.
      expect(call.request.headers.has('Accept')).toBeFalse();
      expect(call.request.responseType).toBe('text');

      call.flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });
      expect(recorded.values.length).toBe(1);
    });

    it('propagates an unsupported-export refusal untranslated', () => {
      // `Export.ascx.vb` line 150 gated the whole operation on
      // `objModule.BusinessControllerClass <> "" And objModule.IsPortable`. Failing that
      // gate is the origin of this refusal, and it is a SERVER decision: nothing here
      // knows whether a module is portable.
      const rawBody = JSON.stringify(
        problem(
          'module.not_portable',
          400,
          'This module does not support the transfer of content.',
        ),
      );
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12/export')
        .flush(rawBody, { status: 400, statusText: 'Bad Request' });

      expectTextRefusal(recorded, 400, rawBody);
    });

    it('treats an EMPTY document as a success rather than as a refusal', () => {
      // ⚠ AN EXPORT THAT PRODUCES NOTHING IS A 200 WITH AN EMPTY BODY, NOT AN ERROR. The
      // export action declares exactly 200/400/401/403/404/500, and a module whose content
      // is legitimately empty has nothing to refuse: the operation completed and the answer
      // is an empty document. An earlier revision of this spec asserted a 422
      // "empty-document refusal", inventing both a status this controller cannot emit and a
      // failure the server does not raise — a client written against it would have shown an
      // error for a perfectly successful export of an empty module.
      //
      // The empty string must therefore reach the caller AS A VALUE. This is the case a
      // truthiness test silently breaks: `''` is falsy, so any code that treated the body as
      // "missing when empty" would convert a success into a failure. The legacy page drew the
      // same distinction at `Export.ascx.vb:L159`.
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock.expectOne('/api/v1/modules/12/export').flush('', { status: 200, statusText: 'OK' });

      expect(recorded.failures.length).withContext('an empty export is not a failure').toBe(0);
      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0])
        .withContext('the empty document arrives as the empty string, not as null')
        .toBe('');
      expect(recorded.completions.length).toBe(1);
    });

    it('propagates an export-failed refusal untranslated, as the 500 it is', () => {
      // `module.export_failed` carries the `export_failed` token, which the server's mapping
      // classifies as an INTERNAL failure: the operation had already passed validation and
      // authorisation when it failed, so nothing the caller could change would make the
      // identical request succeed. The export action declares 500 for exactly this reason.
      // The client's whole responsibility is to let it through unaltered — no retry, no
      // substitution of an empty document.
      const rawBody = JSON.stringify(
        problem(
          'module.export_failed',
          500,
          'The module could not produce its content. Quote the X-Correlation-Id response ' +
            'header when reporting this problem.',
        ),
      );
      const recorded = record(service.exportModule(12, EXPORT_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12/export')
        .flush(rawBody, { status: 500, statusText: 'Internal Server Error' });

      expectTextRefusal(recorded, 500, rawBody);
      httpMock.expectNone('/api/v1/modules/12/export');
    });
  });

  describe('listModules - GET /api/v1/modules', () => {
    /** A page body. The listing is the ONE endpoint whose body IS the page envelope. */
    function page(items: readonly ModuleListItem[]): PagedResponse<ModuleListItem> {
      return {
        items,
        meta: { totalCount: items.length, pageIndex: 0, pageSize: 10, totalPages: 1 },
      };
    }

    it('gets the collection at the literal path with the paging coordinates verbatim', () => {
      const recorded = record(
        service.listModules({
          pageIndex: 0,
          pageSize: 10,
          sortBy: 'moduleTitle',
          sortDir: 'Ascending',
          query: 'news',
        }),
      );

      // Matched on the PATH ONLY through the predicate overload, so the assertion is not
      // coupled to the order the parameters happen to be serialised in.
      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe('10');
      expect(call.request.params.get('sortBy')).toBe('moduleTitle');
      expect(call.request.params.get('sortDir')).toBe('Ascending');
      expect(call.request.params.get('query')).toBe('news');

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('performs NO arithmetic on the page index in either direction', () => {
      // The legacy screens carried a ONE-based counter and converted it at the call site -
      // `Website/admin/Users/Users.ascx.vb` line 265 and
      // `Website/admin/Portal/Portals.ascx.vb` line 142 both pass `CurrentPage - 1`. The
      // wire contract here is zero-based, and that conversion is a presentation concern
      // that lives in the feature store. An adjustment applied here as well would serve
      // the neighbouring page behind a perfectly successful status code.
      const first = record(service.listModules({ pageIndex: 0, pageSize: 10 }));
      const firstCall = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(firstCall.request.params.get('pageIndex')).toBe('0');
      firstCall.flush(page([LIST_ROW]));
      expect(first.values.length).toBe(1);

      const second = record(service.listModules({ pageIndex: 1, pageSize: 10 }));
      const secondCall = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(secondCall.request.params.get('pageIndex')).toBe('1');
      secondCall.flush(page([]));
      expect(second.values.length).toBe(1);
    });

    it('transmits a page filter of ZERO unchanged', () => {
      // `01.00.00.SqlDataProvider` line 140 declares `[TabID] [int] IDENTITY (0, 1)`, so
      // zero addresses the FIRST page of the portal. A truthiness test would silently
      // widen this request to every page.
      const recorded = record(service.listModules({ pageIndex: 0, pageSize: 10 }, { tabId: 0 }));

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(call.request.params.get('tabId')).toBe('0');
      expect(call.request.params.has('tabId')).toBeTrue();

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('transmits an includeDeleted flag of FALSE rather than omitting it', () => {
      // `false` here is a CHOICE - "list the live placements" - and it is materially
      // different from not having expressed a preference at all, which is the next
      // specification. Only `undefined` and `null` may be omitted.
      const recorded = record(
        service.listModules({ pageIndex: 0, pageSize: 10 }, { tabId: 3, includeDeleted: false }),
      );

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(call.request.params.get('includeDeleted')).toBe('false');
      expect(call.request.params.get('tabId')).toBe('3');

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('omits only what was not supplied, while sending zero, minus one, empty and false', () => {
      // THE REGRESSION TEST FOR TRUTHINESS. `undefined` is absent; every falsy value that
      // is a real datum is present. If the omission test is ever weakened from an
      // explicit undefined-or-null comparison to a truthiness check, this fails on four
      // separate assertions at once.
      const recorded = record(
        service.listModules(
          { pageIndex: 0, pageSize: 10, query: '', sortBy: undefined, sortDir: undefined },
          { tabId: 0, includeDeleted: false },
        ),
      );

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );

      // Present because each is a real value.
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('query')).toBe('');
      expect(call.request.params.get('tabId')).toBe('0');
      expect(call.request.params.get('includeDeleted')).toBe('false');
      // Absent because neither was supplied.
      expect(call.request.params.has('sortBy')).toBeFalse();
      expect(call.request.params.has('sortDir')).toBeFalse();
      expect(call.request.params.get('sortBy')).toBeNull();

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('sends a negative filter value through unchanged', () => {
      // Minus one is the absent-integer marker in `Library/Components/Shared/Null.vb`
      // lines 41-45 AND a real identifier elsewhere in this schema, so the transport layer
      // must not adjudicate which it is: it forwards, and the server decides.
      const recorded = record(service.listModules({ pageIndex: 0, pageSize: 10 }, { tabId: -1 }));

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(call.request.params.get('tabId')).toBe('-1');

      call.flush(page([]));
      expect(recorded.values.length).toBe(1);
    });

    it('appends no trailing wildcard to the free-text filter', () => {
      // The legacy readers decorated the pattern at the call site; the target composes any
      // pattern server-side inside the repositories, so a filter decorated here would be
      // decorated twice. The wildcard character is obtained by code point above so that it
      // appears nowhere as a literal in this file.
      const recorded = record(
        service.listModules({ pageIndex: 0, pageSize: 10, query: 'announce' }),
      );

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      const sentQuery = call.request.params.get('query');
      expect(sentQuery).toBe('announce');
      expect(sentQuery === null ? false : sentQuery.includes(WILDCARD)).toBeFalse();
      expect(call.request.urlWithParams.includes(WILDCARD)).toBeFalse();

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('sends no query parameter at all when nothing was supplied', () => {
      const recorded = record(service.listModules({}));

      const call = httpMock.expectOne('/api/v1/modules');
      expect(call.request.method).toBe('GET');
      expect(call.request.params.keys()).toEqual([]);
      expect(call.request.urlWithParams).toBe('/api/v1/modules');

      call.flush(page([LIST_ROW]));
      expect(recorded.values.length).toBe(1);
    });

    it('sends no filter parameter when the filter argument is omitted or null', () => {
      const omitted = record(service.listModules({ pageIndex: 0, pageSize: 10 }));
      const omittedCall = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(omittedCall.request.params.has('tabId')).toBeFalse();
      expect(omittedCall.request.params.has('includeDeleted')).toBeFalse();
      omittedCall.flush(page([LIST_ROW]));
      expect(omitted.values.length).toBe(1);

      const explicitNull = record(service.listModules({ pageIndex: 0, pageSize: 10 }, null));
      const nullCall = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );
      expect(nullCall.request.params.has('tabId')).toBeFalse();
      nullCall.flush(page([LIST_ROW]));
      expect(explicitNull.values.length).toBe(1);
    });

    it('emits the page envelope with every row and every paging fact intact', () => {
      // The listing body IS the page envelope - it is NOT wrapped in the single-payload
      // success envelope the other reads use - so nothing is lifted out of it here.
      const recorded = record(service.listModules({ pageIndex: 0, pageSize: 10 }));

      httpMock
        .expectOne(
          (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
        )
        .flush({
          items: [LIST_ROW],
          meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
        } satisfies PagedResponse<ModuleListItem>);

      expect(recorded.values.length).toBe(1);
      expect(recorded.completions.length).toBe(1);
      expect(recorded.values[0].items).toEqual([LIST_ROW]);
      expect(recorded.values[0].meta).toEqual({
        totalCount: 1,
        pageIndex: 0,
        pageSize: 10,
        totalPages: 1,
      });
      // A row of this listing is a PLACEMENT, so the identity that makes it unique is the
      // placement identity while the module identity legitimately repeats across pages.
      expect(recorded.values[0].items[0].moduleId).toBe(0);
      expect(recorded.values[0].items[0].tabModuleId).toBe(1);
    });

    it('emits an empty page without inventing rows or a total', () => {
      const recorded = record(service.listModules({ pageIndex: 0, pageSize: 10 }));

      httpMock
        .expectOne(
          (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
        )
        .flush({
          items: [],
          meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 },
        } satisfies PagedResponse<ModuleListItem>);

      expect(recorded.values[0].items).toEqual([]);
      expect(recorded.values[0].meta.totalCount).toBe(0);
      expect(recorded.values[0].meta.totalPages).toBe(0);
    });
  });

  describe('getModule - GET /api/v1/modules/{moduleId}', () => {
    /** The single-payload success envelope, whose metadata member is present and null. */
    function envelope(data: ModuleDetail | null): ApiResponse<ModuleDetail | null> {
      // Present and NULL rather than omitted, because that is what the server writes: it
      // serialises every declared member including one holding null. Flushing the member
      // out altogether would test a body the server never sends.
      return { data, meta: null };
    }

    it('gets module ZERO at the literal path /api/v1/modules/0', () => {
      // THE MANDATED IDENTITY-SEED ASSERTION. `01.00.00.SqlDataProvider` line 221 declares
      // `[ModuleID] [int] IDENTITY (0, 1)`, so zero is the FIRST module ever created and a
      // perfectly ordinary identifier. A truthiness guard, a positive-value test or a
      // null-coalescing default anywhere on the path builder would produce a different
      // path here - most likely `/api/v1/modules/` or a substituted identifier - and this
      // exact-string expectation is what makes that fail rather than pass quietly.
      const recorded = record(service.getModule(0));

      const call = httpMock.expectOne('/api/v1/modules/0');
      expect(call.request.method).toBe('GET');
      expect(call.request.url).toBe('/api/v1/modules/0');
      expect(call.request.urlWithParams).toBe('/api/v1/modules/0');

      call.flush(envelope(DETAIL));
      expect(recorded.values.length).toBe(1);
    });

    it('gets a negative identifier at its own literal path', () => {
      const recorded = record(service.getModule(-1));

      httpMock.expectOne('/api/v1/modules/-1').flush(envelope(DETAIL));

      expect(recorded.values.length).toBe(1);
    });

    it('lifts the payload out of the success envelope', () => {
      // Typing the call as the bare payload instead of as the envelope compiles and then
      // fails in the quietest possible way: every member reads as undefined and the result
      // is a shape-correct blank. This asserts the lift actually happens.
      const recorded = record(service.getModule(0));

      httpMock.expectOne('/api/v1/modules/0').flush(envelope(DETAIL));

      expect(recorded.values.length).toBe(1);
      expect(recorded.completions.length).toBe(1);
      expect(recorded.values[0]).toEqual(DETAIL);
    });

    it('reports absence as a 404 rather than as a payload-free success', () => {
      // ⚠ THE ENDPOINT CANNOT ANSWER 200 WITH A NULL PAYLOAD. `ApiResults.Complete<T>`
      // converts a successful outcome carrying no value into a 404 whose code is
      // `resource.not_found`, so absence arrives as a STATUS. An earlier revision of this
      // spec asserted the 200/null shape as this endpoint's contract, which meant the whole
      // chain above it - store, then screen - was specified against a response the server
      // never sends, while the one response it DOES send for a missing module went untested.
      const recorded = record(service.getModule(0));

      httpMock
        .expectOne('/api/v1/modules/0')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expectResourceNotFound(recorded);
    });

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // DEFENCE IN DEPTH, AND LABELLED AS SUCH. The method's own return type admits null
      // because a proxy or gateway between the browser and the API can return a document
      // this application never produced, and a client that dereferenced it blindly would
      // fail with a type error rather than a diagnosable one. This is NOT the endpoint's
      // contract - the case above is - and no store or screen specification may treat it as
      // the normal path.
      const recorded = record(service.getModule(0));

      httpMock.expectOne('/api/v1/modules/0').flush(envelope(null));

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toBeNull();
    });

    it('reads a portal identity of MINUS ONE and of ZERO as the real tenants they are', () => {
      // `01.00.00.SqlDataProvider` line 77 declares `[PortalID] [int] IDENTITY (-1, 1)`, so
      // the first portal carries minus one and the second carries zero - while
      // `Library/Components/Shared/Null.vb` lines 41-45 simultaneously define minus one as
      // the absent-integer marker. Both values must therefore survive a read as numbers
      // rather than being normalised into an absence. There is no portal PARAMETER on this
      // API - the server resolves the tenant from the request - so the payload is where
      // this fidelity is observable, and this is where it is asserted.
      const first = record(service.getModule(0));
      httpMock.expectOne('/api/v1/modules/0').flush(envelope({ ...DETAIL, portalId: -1 }));
      expect(first.values[0]?.portalId).toBe(-1);

      const second = record(service.getModule(0));
      httpMock.expectOne('/api/v1/modules/0').flush(envelope({ ...DETAIL, portalId: 0 }));
      expect(second.values[0]?.portalId).toBe(0);
    });

    it('reads a cache period of ZERO as zero and never as the definition default', () => {
      // MIGRATION: the placement's own cache period and the definition's DEFAULT period are
      //   DISTINCT facts. `Library/Components/Modules/ModuleInfo.vb` declared them
      //   separately - `CacheTime` at line 203 and `DefaultCacheTime` at line 482 - and the
      //   target keeps them on separate contracts: the period on the module contracts, the
      //   default on the definition contract. A coalescing expression that fell back to the
      //   default whenever the period was falsy would change how long a module caches, and
      //   zero is precisely the value that triggers it.
      const recorded = record(service.getModule(0));

      httpMock.expectOne('/api/v1/modules/0').flush(envelope({ ...DETAIL, cacheTime: 0 }));

      expect(recorded.values[0]?.cacheTime).toBe(0);
      // The default is not smuggled onto the placement contract under either spelling.
      const emitted: ModuleDetail | null = recorded.values[0];
      expect(Object.prototype.hasOwnProperty.call(emitted, 'defaultCacheTime')).toBeFalse();
      expect(DEFINITION.defaultCacheTime).toBe(900);
    });

    it('selects one placement when a placement is named, and sends no selector otherwise', () => {
      // Supplying the selector and omitting it are materially different reads - a module
      // with the all-pages flag set has one placement per page, so the module identity
      // alone does not name a single row - and the difference must not be blurred by a
      // default.
      const selected = record(service.getModule(0, { tabModuleId: 7 }));
      const selectedCall = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules/0',
      );
      expect(selectedCall.request.params.get('tabModuleId')).toBe('7');
      selectedCall.flush(envelope(DETAIL));
      expect(selected.values.length).toBe(1);

      const unselected = record(service.getModule(0));
      const unselectedCall = httpMock.expectOne('/api/v1/modules/0');
      expect(unselectedCall.request.params.keys()).toEqual([]);
      unselectedCall.flush(envelope(DETAIL));
      expect(unselected.values.length).toBe(1);
    });

    it('propagates a not-found refusal untranslated', () => {
      const recorded = record(service.getModule(0));

      httpMock
        .expectOne('/api/v1/modules/0')
        .flush(problem('module.not_found', 404, 'No such module is visible to the caller.'), {
          status: 404,
          statusText: 'Not Found',
        });

      expectRefusal(recorded, 404, 'module.not_found');
    });
  });

  describe('createModule - POST /api/v1/modules', () => {
    it('posts the creation request WHOLE and unmodified, and answers 201', () => {
      const recorded = record(service.createModule(CREATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules');
      expect(call.request.method).toBe('POST');
      expect(call.request.url).toBe('/api/v1/modules');
      expect(call.request.params.keys()).toEqual([]);
      // Deep equality against the exact object the caller handed over: every empty string,
      // every `false`, every `null` and every `0` in the fixture must appear on the wire.
      expect(call.request.body).toEqual(CREATE_REQUEST);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail>, {
        status: 201,
        statusText: 'Created',
      });

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toEqual(DETAIL);
    });

    it('drops no member of the creation request for being falsy', () => {
      // Key-set equality as well as value equality. A body compared only by value would
      // still pass if a member were dropped AND the expectation were built from the same
      // filtered object, so the key set is compared against the fixture's own key set.
      const recorded = record(service.createModule(CREATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules');
      const sent = call.request.body as CreateModuleRequest;

      expect(Object.keys(sent).sort()).toEqual(Object.keys(CREATE_REQUEST).sort());
      expect(Object.keys(sent).length).toBe(14);
      expect(sent.moduleTitle).toBe('');
      expect(sent.header).toBe('');
      expect(sent.footer).toBe('');
      expect(sent.iconFile).toBeNull();
      expect(sent.startDate).toBeNull();
      expect(sent.allTabs).toBeFalse();
      expect(sent.displayTitle).toBeFalse();
      expect(sent.inheritViewPermissions).toBeFalse();
      expect(sent.tabId).toBe(0);
      expect(sent.moduleOrder).toBe(0);
      expect(sent.cacheTime).toBe(0);
      // The visibility code zero is the legacy default AND a real chosen state, so it is
      // exactly the member a truthiness filter would erase.
      expect(sent.visibility).toBe(ModuleVisibility.Maximized);
      expect(sent.visibility).toBe(0);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail>, {
        status: 201,
        statusText: 'Created',
      });
      expect(recorded.values.length).toBe(1);
    });

    it('sends the request as JSON rather than as a form', () => {
      const recorded = record(service.createModule(CREATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules');
      expect(call.request.detectContentTypeHeader()).toBe('application/json');
      expectNoClientHeaders(call.request.headers);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail>, {
        status: 201,
        statusText: 'Created',
      });
      expect(recorded.values.length).toBe(1);
    });

    it('propagates a validation refusal untranslated, field map included', () => {
      const recorded = record(service.createModule(CREATE_REQUEST));

      // A VALIDATION refusal, which is the ONE shape that carries a per-field map. Its
      // title and type are the framework's own defaults for a model-binding failure and are
      // deliberately different from the coded refusals above: the binder rejected named
      // members, so the client can attach each message to its control.
      const body: WireProblem = {
        type: 'urn:dnnmigration:error:request.invalid',
        title: 'One or more validation errors occurred.',
        status: 400,
        detail: 'The request could not be processed as submitted.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
        errors: { moduleTitle: ['The module title is required.'] },
      };

      httpMock
        .expectOne('/api/v1/modules')
        .flush(body, { status: 400, statusText: 'Bad Request' });

      expect(recorded.values.length).toBe(0);
      expect(recorded.failures.length).toBe(1);
      expect(recorded.failures[0].status).toBe(400);

      // The refusal arrives WHOLE and is not reshaped here. Read with an index expression
      // because `noPropertyAccessFromIndexSignature` is enabled - a dotted read of a member
      // of an index signature does not compile in this workspace.
      const received = recorded.failures[0].error as WireProblem;
      expect(received).toEqual(body);
      const fieldMessages: readonly string[] | undefined = received.errors?.['moduleTitle'];
      expect(fieldMessages).toEqual(['The module title is required.']);
    });
  });

  describe('updateModule - PUT /api/v1/modules/{moduleId}', () => {
    it('puts the replacement state WHOLE at the addressed module and answers 200', () => {
      const recorded = record(service.updateModule(12, UPDATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12');
      expect(call.request.method).toBe('PUT');
      expect(call.request.url).toBe('/api/v1/modules/12');
      expect(call.request.body).toEqual(UPDATE_REQUEST);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail | null>, {
        status: 200,
        statusText: 'OK',
      });

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toEqual(DETAIL);
    });

    it('replaces module ZERO at the literal path /api/v1/modules/0', () => {
      const recorded = record(service.updateModule(0, UPDATE_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/0')
        .flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail | null>);

      expect(recorded.values.length).toBe(1);
    });

    it('keeps every present-and-false optional member on the wire', () => {
      const recorded = record(service.updateModule(12, UPDATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12');
      const sent = call.request.body as UpdateModuleRequest;

      expect(Object.keys(sent).sort()).toEqual(Object.keys(UPDATE_REQUEST).sort());
      expect(sent.isDeleted).toBeFalse();
      expect(sent.setAsDefaultSettings).toBeFalse();
      expect(sent.applyToAllModules).toBeFalse();
      expect(sent.allTabs).toBeTrue();
      expect(sent.moduleTitle).toBe('');
      expect(sent.cacheTime).toBe(0);
      // Visibility code two is a CHOSEN state - render without container chrome - and never
      // an absent one, so it travels as the number it is.
      expect(sent.visibility).toBe(ModuleVisibility.None);
      expect(sent.visibility).toBe(2);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail | null>);
      expect(recorded.values.length).toBe(1);
    });

    it('omits an optional member that the caller genuinely did not supply', () => {
      const partial: UpdateModuleRequest = {
        tabId: 3,
        moduleTitle: 'Announcements',
        allTabs: false,
        header: null,
        footer: null,
        startDate: null,
        endDate: null,
        inheritViewPermissions: true,
        moduleOrder: 2,
        cacheTime: 120,
        iconFile: null,
        visibility: ModuleVisibility.Minimized,
        displayTitle: true,
      };
      const recorded = record(service.updateModule(12, partial));

      const call = httpMock.expectOne('/api/v1/modules/12');
      const sent = call.request.body as UpdateModuleRequest;
      expect(Object.prototype.hasOwnProperty.call(sent, 'isDeleted')).toBeFalse();
      expect(Object.prototype.hasOwnProperty.call(sent, 'setAsDefaultSettings')).toBeFalse();
      expect(Object.prototype.hasOwnProperty.call(sent, 'applyToAllModules')).toBeFalse();
      expect(sent).toEqual(partial);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail | null>);
      expect(recorded.values.length).toBe(1);
    });

    it('reports a vanished module as a 404 rather than as a payload-free success', () => {
      // The update action answers through `ApiResults.Complete<T>` exactly as the read does,
      // so a successful outcome with no representation becomes a 404 here too. A 200 with a
      // null payload is not a response this action can produce.
      const recorded = record(service.updateModule(12, UPDATE_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expectResourceNotFound(recorded);
    });

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // Defence in depth, exactly as on the read path and for the same reason. Labelled so
      // it is not mistaken for the endpoint's contract.
      const recorded = record(service.updateModule(12, UPDATE_REQUEST));

      httpMock
        .expectOne('/api/v1/modules/12')
        .flush({ data: null, meta: null } satisfies ApiResponse<ModuleDetail | null>);

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toBeNull();
    });

    it('propagates a 403 on the all-pages rule UNCHANGED, without pre-empting it', () => {
      // A refusal is a legitimate answer to a well-formed request here: the server enforces
      // its own field-level rule on the all-pages flag. No check anticipating that rule
      // exists in the service, and none may be added - duplicating a server rule on the
      // client gives an HTTP caller a different answer from every other caller, and the two
      // copies drift apart at the first change. The request is therefore SENT, and the
      // refusal is carried out whole.
      const recorded = record(service.updateModule(12, UPDATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules/12');
      // Sent, not withheld: the body reached the wire before the refusal came back.
      expect(call.request.body).toEqual(UPDATE_REQUEST);
      expect((call.request.body as UpdateModuleRequest).allTabs).toBeTrue();

      const body = problem('module.edit_forbidden', 403, 'Policy denies this change.');
      call.flush(body, { status: 403, statusText: 'Forbidden' });

      expectRefusal(recorded, 403, 'module.edit_forbidden');
      expect(recorded.failures[0].error).toEqual(body);
    });
  });

  describe('deleteModule - DELETE /api/v1/modules/{moduleId}', () => {
    it('deletes the addressed module and completes on 204 with no payload', () => {
      // MIGRATION: THE REMOVAL IS SOFT. A 204 here does NOT mean the row was removed - it
      //   survives with its deleted marker set, which is precisely what the legacy bin
      //   screen at `Website/admin/Tabs/RecycleBin.ascx.vb` consumed, and the listing hides
      //   it unless deleted rows are asked for. Nothing in this specification asserts that
      //   the row is gone, because the status does not license that conclusion, and nothing
      //   re-reads the collection to find out: the legacy bin screen has no target
      //   counterpart, so no restore and no purge endpoint exists to consume it either.
      const recorded = record(service.deleteModule(12));

      const call = httpMock.expectOne('/api/v1/modules/12');
      expect(call.request.method).toBe('DELETE');
      expect(call.request.url).toBe('/api/v1/modules/12');
      expect(call.request.params.keys()).toEqual([]);
      expect(call.request.body).toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });

      expect(recorded.completions.length).toBe(1);
      expect(recorded.failures.length).toBe(0);
    });

    it('deletes module ZERO at the literal path /api/v1/modules/0', () => {
      const recorded = record(service.deleteModule(0));

      httpMock
        .expectOne('/api/v1/modules/0')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(recorded.completions.length).toBe(1);
    });

    it('issues EXACTLY ONE request and never a follow-up read', () => {
      // Chaining a second request onto the first would be orchestration, which this layer
      // does not do - the feature store decides whether to refresh. Two mechanisms prove it
      // here: the explicit expectation that nothing further is outstanding, and the
      // `verify()` in `afterEach` which fails on any request no expectation consumed.
      const recorded = record(service.deleteModule(12));

      httpMock
        .expectOne('/api/v1/modules/12')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(recorded.completions.length).toBe(1);
      httpMock.expectNone('/api/v1/modules/12');
      httpMock.expectNone('/api/v1/modules');
      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('narrows the removal to one placement when a placement is named', () => {
      // Supplying the selector leaves the module on its other pages; omitting it addresses
      // the module itself. Two materially different requests, and no default blurs them.
      const recorded = record(service.deleteModule(12, { tabModuleId: 7 }));

      const call = httpMock.expectOne(
        (candidate) => candidate.method === 'DELETE' && candidate.url === '/api/v1/modules/12',
      );
      expect(call.request.params.get('tabModuleId')).toBe('7');

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('sends no placement selector when the selector is null', () => {
      const recorded = record(service.deleteModule(12, null));

      const call = httpMock.expectOne('/api/v1/modules/12');
      expect(call.request.params.keys()).toEqual([]);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('propagates a policy refusal untranslated', () => {
      const recorded = record(service.deleteModule(12));

      httpMock
        .expectOne('/api/v1/modules/12')
        .flush(problem('module.edit_forbidden', 403, 'Policy denies this removal.'), {
          status: 403,
          statusText: 'Forbidden',
        });

      expectRefusal(recorded, 403, 'module.edit_forbidden');
    });
  });

  describe('getModuleSettings - GET /api/v1/modules/{moduleId}/settings', () => {
    /** The settings envelope, metadata present and null as the server writes it. */
    function envelope(data: ModuleSettingsBag | null): ApiResponse<ModuleSettingsBag | null> {
      return { data, meta: null };
    }

    it('gets the nested settings path exactly, including for module ZERO', () => {
      const recorded = record(service.getModuleSettings(0));

      const call = httpMock.expectOne('/api/v1/modules/0/settings');
      expect(call.request.method).toBe('GET');
      expect(call.request.url).toBe('/api/v1/modules/0/settings');
      expect(call.request.urlWithParams).toBe('/api/v1/modules/0/settings');

      call.flush(envelope(SETTINGS_BAG));
      expect(recorded.values.length).toBe(1);
    });

    it('returns BOTH settings maps without merging them', () => {
      // The two maps land in two different tables, one keyed by the module and one by the
      // placement, and the distinction decides which page a value applies to. Merging them
      // would lose which is which - so a single flattened map arriving here would be a
      // silent data-scope error, and the two separate assertions below are what catch it.
      const recorded = record(service.getModuleSettings(0, { tabModuleId: 7 }));

      const call = httpMock.expectOne(
        (candidate) =>
          candidate.method === 'GET' && candidate.url === '/api/v1/modules/0/settings',
      );
      expect(call.request.params.get('tabModuleId')).toBe('7');
      call.flush(envelope(SETTINGS_BAG));

      expect(recorded.values.length).toBe(1);
      const emitted = recorded.values[0];
      expect(emitted).toEqual(SETTINGS_BAG);
      expect(emitted?.moduleSettings).toEqual(SETTINGS_BAG.moduleSettings);
      expect(emitted?.tabModuleSettings).toEqual(SETTINGS_BAG.tabModuleSettings);
      // Index expressions rather than dotted reads: the maps carry index signatures and
      // `noPropertyAccessFromIndexSignature` is enabled in this workspace.
      expect(emitted?.moduleSettings['Announcements_Description']).toBe('');
      expect(emitted?.tabModuleSettings['Announcements_Heading']).toBe('');
      expect(Object.keys(emitted?.moduleSettings ?? {}).length).toBe(3);
      expect(Object.keys(emitted?.tabModuleSettings ?? {}).length).toBe(2);
    });

    it('reads a null placement identity as null rather than as zero', () => {
      const recorded = record(service.getModuleSettings(0));

      httpMock
        .expectOne('/api/v1/modules/0/settings')
        .flush(envelope({ ...SETTINGS_BAG, tabModuleId: null, tabModuleSettings: {} }));

      expect(recorded.values[0]?.tabModuleId).toBeNull();
      expect(recorded.values[0]?.tabModuleSettings).toEqual({});
    });

    it('reports absence as a 404 rather than as a payload-free success', () => {
      // The settings read is a single-resource read like any other, so a module the caller
      // cannot see produces a 404 rather than an empty settings bag. The distinction matters
      // to the screen: an empty bag is a module with no settings, and a 404 is no module.
      const recorded = record(service.getModuleSettings(0));

      httpMock
        .expectOne('/api/v1/modules/0/settings')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expectResourceNotFound(recorded);
    });

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // Defence in depth. Note the contrast with the case above it: an EMPTY settings bag is
      // a legitimate 200 and is asserted separately, whereas a null payload is not something
      // this API emits at all.
      const recorded = record(service.getModuleSettings(0));

      httpMock.expectOne('/api/v1/modules/0/settings').flush(envelope(null));

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toBeNull();
    });

    it('sends no selector when no placement is named', () => {
      const recorded = record(service.getModuleSettings(12));

      const call = httpMock.expectOne('/api/v1/modules/12/settings');
      expect(call.request.params.keys()).toEqual([]);

      call.flush(envelope(SETTINGS_BAG));
      expect(recorded.values.length).toBe(1);
    });
  });

  describe('updateModuleSettings - PUT /api/v1/modules/{moduleId}/settings', () => {
    it('puts the settings object WHOLE, preserving every cleared value', () => {
      // THE MOST SENTINEL-SENSITIVE ASSERTION IN THE FILE. The absent-string marker IS the
      // empty string - `Library/Components/Shared/Null.vb` lines 71-75 return `""` and not
      // `Nothing` - the settings maps legitimately hold cleared values, and the server does
      // not elide default values in either direction. Filtering the maps for truthiness
      // before sending, which is the obvious and wrong implementation, would silently delete
      // every setting an operator had cleared AND still answer 204. Every key below is
      // therefore asserted individually, cleared values included.
      const recorded = record(service.updateModuleSettings(0, SETTINGS_BAG));

      const call = httpMock.expectOne('/api/v1/modules/0/settings');
      expect(call.request.method).toBe('PUT');
      expect(call.request.url).toBe('/api/v1/modules/0/settings');
      expect(call.request.body).toEqual(SETTINGS_BAG);

      const sent = call.request.body as ModuleSettingsBag;
      expect(Object.keys(sent.moduleSettings).sort()).toEqual([
        'Announcements_Description',
        'Announcements_Length',
        'Announcements_Template',
      ]);
      expect(sent.moduleSettings['Announcements_Description']).toBe('');
      // A stringified zero and a stringified `false` are values, not absences.
      expect(sent.moduleSettings['Announcements_Length']).toBe('0');
      expect(sent.moduleSettings['Announcements_Template']).toBe('default');
      expect(Object.keys(sent.tabModuleSettings).sort()).toEqual([
        'Announcements_Collapsed',
        'Announcements_Heading',
      ]);
      expect(sent.tabModuleSettings['Announcements_Heading']).toBe('');
      expect(sent.tabModuleSettings['Announcements_Collapsed']).toBe('false');
      // The module identity of zero survives on the payload as well as in the path.
      expect(sent.moduleId).toBe(0);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('transmits two entirely empty maps rather than omitting them', () => {
      // A module with no settings at all is a real, representable state. Dropping the empty
      // maps would make "no settings" indistinguishable from "settings not supplied", and
      // the server would have no way to tell a clearing request from a partial one.
      const empty: ModuleSettingsBag = {
        moduleId: 0,
        tabModuleId: null,
        moduleSettings: {},
        tabModuleSettings: {},
      };
      const recorded = record(service.updateModuleSettings(0, empty));

      const call = httpMock.expectOne('/api/v1/modules/0/settings');
      const sent = call.request.body as ModuleSettingsBag;
      expect(Object.prototype.hasOwnProperty.call(sent, 'moduleSettings')).toBeTrue();
      expect(Object.prototype.hasOwnProperty.call(sent, 'tabModuleSettings')).toBeTrue();
      expect(sent.moduleSettings).toEqual({});
      expect(sent.tabModuleSettings).toEqual({});
      expect(sent.tabModuleId).toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('completes with NO payload on 204, so a caller reads the state back deliberately', () => {
      const recorded = record(service.updateModuleSettings(12, SETTINGS_BAG));

      httpMock
        .expectOne('/api/v1/modules/12/settings')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(recorded.completions.length).toBe(1);
      expect(recorded.failures.length).toBe(0);
      // Nothing is fetched back automatically: a re-read would be orchestration.
      httpMock.expectNone('/api/v1/modules/12/settings');
    });

    it('carries the placement selector when a placement scope is being replaced', () => {
      const recorded = record(service.updateModuleSettings(12, SETTINGS_BAG, { tabModuleId: 7 }));

      const call = httpMock.expectOne(
        (candidate) =>
          candidate.method === 'PUT' && candidate.url === '/api/v1/modules/12/settings',
      );
      expect(call.request.params.get('tabModuleId')).toBe('7');
      expect(call.request.body).toEqual(SETTINGS_BAG);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });

    it('sends the settings as JSON rather than as a form', () => {
      const recorded = record(service.updateModuleSettings(12, SETTINGS_BAG));

      const call = httpMock.expectOne('/api/v1/modules/12/settings');
      expect(call.request.detectContentTypeHeader()).toBe('application/json');
      expect(Object.prototype.toString.call(call.request.body)).toBe('[object Object]');
      expectNoClientHeaders(call.request.headers);

      call.flush(null, { status: 204, statusText: 'No Content' });
      expect(recorded.completions.length).toBe(1);
    });
  });

  describe('listModuleDefinitions - GET /api/v1/module-definitions', () => {
    it('gets the catalogue at the literal path with NO query parameter at all', () => {
      // DELIBERATELY UNPAGED AND UNFILTERED, and the method accordingly takes no argument.
      // The catalogue is small, bounded reference data the upgrade scripts seed and this
      // application only reads, so it is returned whole. Nothing is sent: not a page
      // coordinate, not an ordering and not a restriction.
      const recorded = record(service.listModuleDefinitions());

      const call = httpMock.expectOne('/api/v1/module-definitions');
      expect(call.request.method).toBe('GET');
      expect(call.request.url).toBe('/api/v1/module-definitions');
      expect(call.request.urlWithParams).toBe('/api/v1/module-definitions');
      expect(call.request.params.keys()).toEqual([]);
      expect(service.listModuleDefinitions.length).toBe(0);

      call.flush({ data: [DEFINITION], meta: null } satisfies ApiResponse<
        readonly ModuleDefinition[]
      >);
      expect(recorded.values.length).toBe(1);
    });

    it('lifts the definition list out of the envelope and keeps the default cache period', () => {
      const recorded = record(service.listModuleDefinitions());

      httpMock.expectOne('/api/v1/module-definitions').flush({
        data: [DEFINITION],
        meta: null,
      } satisfies ApiResponse<readonly ModuleDefinition[]>);

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toEqual([DEFINITION]);
      // `defaultCacheTime` belongs to the DEFINITION and is a different fact from a
      // placement's own `cacheTime` (`ModuleInfo.vb` line 482 versus line 203). It survives
      // here under its own name, on its own contract, uncoalesced.
      expect(recorded.values[0][0].defaultCacheTime).toBe(900);
      expect(Object.prototype.hasOwnProperty.call(recorded.values[0][0], 'cacheTime')).toBeFalse();
      // The portability flag is data the server reads when adjudicating an export - the
      // legacy gate at `Export.ascx.vb` line 150 - and never a decision taken here.
      expect(recorded.values[0][0].isPortable).toBeTrue();
    });

    it('emits an empty catalogue as an empty list', () => {
      const recorded = record(service.listModuleDefinitions());

      httpMock.expectOne('/api/v1/module-definitions').flush({
        data: [],
        meta: null,
      } satisfies ApiResponse<readonly ModuleDefinition[]>);

      expect(recorded.values[0]).toEqual([]);
      expect(recorded.completions.length).toBe(1);
    });
  });

  describe('getModuleDefinition - GET /api/v1/module-definitions/{moduleDefinitionId}', () => {
    it('gets one definition, the route segment spelled in full', () => {
      // The route parameter is spelled `moduleDefinitionId` IN FULL and is deliberately NOT
      // the spelling of the corresponding response member, which the contract abbreviates.
      // The two are different names for the same concept, each taken from its own
      // declaration - the parameter from the route template, the member from the model - and
      // they are not unified by guesswork in either direction.
      const recorded = record(service.getModuleDefinition(4));

      const call = httpMock.expectOne('/api/v1/module-definitions/4');
      expect(call.request.method).toBe('GET');
      expect(call.request.url).toBe('/api/v1/module-definitions/4');
      expect(call.request.params.keys()).toEqual([]);

      call.flush({ data: DEFINITION, meta: null } satisfies ApiResponse<ModuleDefinition | null>);

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toEqual(DEFINITION);
      // The response member keeps the contract's abbreviated spelling.
      expect(recorded.values[0]?.moduleDefId).toBe(4);
    });

    it('gets definition ZERO at its own literal path', () => {
      const recorded = record(service.getModuleDefinition(0));

      httpMock
        .expectOne('/api/v1/module-definitions/0')
        .flush({ data: DEFINITION, meta: null } satisfies ApiResponse<ModuleDefinition | null>);

      expect(recorded.values.length).toBe(1);
    });

    it('reports absence as a 404 rather than as a payload-free success', () => {
      // The definition catalogue answers through the same envelope helper, so an unknown
      // definition is a 404. The 200/null shape asserted by an earlier revision cannot occur.
      const recorded = record(service.getModuleDefinition(4));

      httpMock
        .expectOne('/api/v1/module-definitions/4')
        .flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expectResourceNotFound(recorded);
    });

    it('still tolerates a null definition from a non-conforming intermediary', () => {
      // Defence in depth, labelled so it is not mistaken for the endpoint's contract.
      const recorded = record(service.getModuleDefinition(4));

      httpMock
        .expectOne('/api/v1/module-definitions/4')
        .flush({ data: null, meta: null } satisfies ApiResponse<ModuleDefinition | null>);

      expect(recorded.values.length).toBe(1);
      expect(recorded.values[0]).toBeNull();
    });

    it('propagates a not-found refusal untranslated', () => {
      const recorded = record(service.getModuleDefinition(4));

      httpMock
        .expectOne('/api/v1/module-definitions/4')
        .flush(problem('module.definition_not_found', 404, 'No such definition is visible.'), {
          status: 404,
          statusText: 'Not Found',
        });

      expectRefusal(recorded, 404, 'module.definition_not_found');
    });
  });

  describe('listDesktopModuleDefinitions - GET /api/v1/module-definitions/desktop-modules/{id}', () => {
    it('gets the HYPHENATED sub-path with the package identifier as a PATH SEGMENT', () => {
      // Two things about this route are easy to get wrong, so both are locked here. The
      // package identifier is a PATH SEGMENT and not a query parameter; and the tenant is
      // not a parameter at all - the server resolves it from the request - so the template
      // takes exactly one identifier and sends no portal value of any kind.
      const recorded = record(service.listDesktopModuleDefinitions(2));

      const call = httpMock.expectOne('/api/v1/module-definitions/desktop-modules/2');
      expect(call.request.method).toBe('GET');
      expect(call.request.url).toBe('/api/v1/module-definitions/desktop-modules/2');
      expect(call.request.urlWithParams).toBe('/api/v1/module-definitions/desktop-modules/2');
      // The hyphenated segment, asserted on its own so a camel-cased or underscored variant
      // fails loudly rather than resolving to a plausible-looking 404.
      expect(call.request.url.includes('/api/v1/module-definitions/desktop-modules')).toBeTrue();
      // No tenant parameter, under any spelling.
      expect(call.request.params.keys()).toEqual([]);
      expect(call.request.params.has('portalId')).toBeFalse();
      expect(call.request.params.has('desktopModuleId')).toBeFalse();

      call.flush({ data: [DEFINITION], meta: null } satisfies ApiResponse<
        readonly ModuleDefinition[]
      >);
      expect(recorded.values.length).toBe(1);
    });

    it('addresses package ZERO at the literal hyphenated path', () => {
      const recorded = record(service.listDesktopModuleDefinitions(0));

      httpMock.expectOne('/api/v1/module-definitions/desktop-modules/0').flush({
        data: [DEFINITION],
        meta: null,
      } satisfies ApiResponse<readonly ModuleDefinition[]>);

      expect(recorded.values.length).toBe(1);
    });

    it('returns the DEFINITIONS owned by that package, not a list of packages', () => {
      const recorded = record(service.listDesktopModuleDefinitions(2));

      httpMock.expectOne('/api/v1/module-definitions/desktop-modules/2').flush({
        data: [DEFINITION, { ...DEFINITION, moduleDefId: 5, friendlyName: 'Announcements Archive' }],
        meta: null,
      } satisfies ApiResponse<readonly ModuleDefinition[]>);

      expect(recorded.values[0].length).toBe(2);
      expect(recorded.values[0][0].moduleDefId).toBe(4);
      expect(recorded.values[0][1].moduleDefId).toBe(5);
      // Every row carries the owning package identity, which is what makes these definitions
      // rather than packages: no endpoint in this API enumerates packages.
      expect(recorded.values[0][0].desktopModuleId).toBe(2);
      expect(recorded.values[0][1].desktopModuleId).toBe(2);
    });

    it('emits an empty list for a package that owns no definition', () => {
      const recorded = record(service.listDesktopModuleDefinitions(2));

      httpMock.expectOne('/api/v1/module-definitions/desktop-modules/2').flush({
        data: [],
        meta: null,
      } satisfies ApiResponse<readonly ModuleDefinition[]>);

      expect(recorded.values[0]).toEqual([]);
    });
  });

  describe('request headers - none are set by this layer', () => {
    it('sets no bearer, correlation or content-negotiation header on a read', () => {
      // The bearer token and the correlation identifier are attached by the two functional
      // interceptors registered once at application configuration, in a fixed order, so the
      // correlation value reaches the server's middleware and the token is attached after
      // it. NEITHER is registered in this test module. A header appearing here would mean
      // the service had begun setting one itself, creating a second and divergent source of
      // truth in the chain - and it would mask a broken interceptor registration, because
      // the requests would keep working in tests and fail only in the application.
      const recorded = record(service.getModule(0));

      const call = httpMock.expectOne('/api/v1/modules/0');
      expectNoClientHeaders(call.request.headers);
      expect(call.request.headers.keys()).toEqual([]);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail | null>);
      expect(recorded.values.length).toBe(1);
    });

    it('sets no header on a write, a removal, an export or an import', () => {
      const created = record(service.createModule(CREATE_REQUEST));
      const createCall = httpMock.expectOne('/api/v1/modules');
      expectNoClientHeaders(createCall.request.headers);
      createCall.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail>, {
        status: 201,
        statusText: 'Created',
      });
      expect(created.values.length).toBe(1);

      const removed = record(service.deleteModule(12));
      const deleteCall = httpMock.expectOne('/api/v1/modules/12');
      expectNoClientHeaders(deleteCall.request.headers);
      deleteCall.flush(null, { status: 204, statusText: 'No Content' });
      expect(removed.completions.length).toBe(1);

      const exported = record(service.exportModule(12, { fileName: 'a.xml', folder: null }));
      const exportCall = httpMock.expectOne('/api/v1/modules/12/export');
      expectNoClientHeaders(exportCall.request.headers);
      exportCall.flush(EXPORTED_DOCUMENT, { status: 200, statusText: 'OK' });
      expect(exported.values.length).toBe(1);

      const imported = record(
        service.importModule({
          moduleId: 12,
          content: EXPORTED_DOCUMENT,
          folder: null,
          fileName: null,
        }),
      );
      const importCall = httpMock.expectOne('/api/v1/modules/import');
      expectNoClientHeaders(importCall.request.headers);
      importCall.flush(null, { status: 204, statusText: 'No Content' });
      expect(imported.completions.length).toBe(1);
    });
  });

  describe('the closed surface - twelve transport methods, and nothing else', () => {
    /**
     * The complete public surface, declared here as the independent statement of it.
     *
     * Written out rather than derived, because a list derived from the class would agree
     * with the class by construction and would therefore accept a thirteenth method
     * without complaint. Adding a method to the service without adding it here fails the
     * next specification, which is the intended friction: every addition has to be a
     * decision, and each of these twelve corresponds to a route the API actually serves.
     */
    const DECLARED_METHODS: readonly string[] = [
      'createModule',
      'deleteModule',
      'exportModule',
      'getModule',
      'getModuleDefinition',
      'getModuleSettings',
      'importModule',
      'listDesktopModuleDefinitions',
      'listModuleDefinitions',
      'listModules',
      'updateModule',
      'updateModuleSettings',
    ];

    it('exposes exactly the twelve declared transport methods', () => {
      const surface = Object.getOwnPropertyNames(ModuleService.prototype)
        .filter((name) => name !== 'constructor')
        .sort();

      expect(surface).toEqual([...DECLARED_METHODS].sort());
      expect(surface.length).toBe(12);
      // The injected client is an INSTANCE field and so is deliberately absent from the
      // prototype; asserting that keeps this count meaningful rather than incidental.
      expect(surface.includes('http')).toBeFalse();
      surface.forEach((name) => {
        const member: unknown = Object.getOwnPropertyDescriptor(
          ModuleService.prototype,
          name,
        )?.value;
        expect(typeof member).toBe('function');
      });
    });

    it('exposes no method for any operation this API deliberately does not serve', () => {
      // Each fragment below names a capability that is out of scope, and each is out of
      // scope for a stated reason rather than by omission:
      //   * installing, packaging and manifest handling - the legacy installer wrote
      //     archives to disk and reflected over the assemblies it found, across
      //     `PortalModuleBase.vb` (881 lines), `PaWriter.vb` (563), `PaFileInfo.vb` (87)
      //     and `EventMessageProcessor.vb` (125), all excluded. Module registration and
      //     lifecycle survive as a server-side domain concern; the Web Forms
      //     control-loading mechanism does not.
      //   * filesystem, folder listing and upload - no filesystem endpoint exists, which is
      //     why the export document returns in the response body and the import document
      //     travels in the request body.
      //   * restore and purge - the removal is soft, but the legacy bin screen has no target
      //     counterpart, so nothing here reverses a removal or empties a store.
      //   * permission mutation, page create/delete/reorder/move, bulk operations, cache
      //     invalidation, portal key-value settings, users-online, password retrieval, the
      //     health probe and anything under the authentication family - each belongs to
      //     another service or to no route at all.
      const outOfScope: readonly string[] = [
        'install',
        'uninstall',
        'deploy',
        'package',
        'manifest',
        'upload',
        'download',
        'folder',
        'filesystem',
        'restore',
        'purge',
        'recycle',
        'permission',
        'reorder',
        'move',
        'copy',
        'bulk',
        'cache',
        'invalidate',
        'refresh',
        'online',
        'password',
        'health',
        'auth',
        'login',
        'logout',
      ];

      const surface = Object.getOwnPropertyNames(ModuleService.prototype).filter(
        (name) => name !== 'constructor',
      );

      outOfScope.forEach((fragment) => {
        const offenders = surface.filter((name) => name.toLowerCase().includes(fragment));
        expect(offenders).toEqual([]);
      });
    });

    it('exposes no mutating method against the definition catalogue', () => {
      // READ-ONLY, proved by name as well as by the verb assertions above: the three
      // definition methods are the two reads and the package lookup, and no fourth exists.
      // A write here would address a route the server does not serve.
      const definitionMethods = Object.getOwnPropertyNames(ModuleService.prototype).filter(
        (name) => name.toLowerCase().includes('definition'),
      );

      expect(definitionMethods.sort()).toEqual([
        'getModuleDefinition',
        'listDesktopModuleDefinitions',
        'listModuleDefinitions',
      ]);
      ['create', 'update', 'delete', 'patch', 'save', 'remove', 'add', 'set'].forEach(
        (verb) => {
          const offenders = definitionMethods.filter((name) =>
            name.toLowerCase().startsWith(verb),
          );
          expect(offenders).toEqual([]);
        },
      );
    });

    it('transmits the target visibility member under its migrated name only', () => {
      // The legacy enumeration was RENAMED on the way across, and the pre-migration
      // spelling must appear in no transmitted body. The rejected name is assembled from
      // fragments below so that it exists nowhere in this file as a literal, which is what
      // stops a future search-and-replace from resurrecting it here.
      const rejectedName = 'Visibility'.concat('State');
      const recorded = record(service.createModule(CREATE_REQUEST));

      const call = httpMock.expectOne('/api/v1/modules');
      const sent = call.request.body as CreateModuleRequest;

      expect(Object.prototype.hasOwnProperty.call(sent, 'visibility')).toBeTrue();
      expect(Object.prototype.hasOwnProperty.call(sent, rejectedName)).toBeFalse();
      expect(Object.keys(sent).includes(rejectedName)).toBeFalse();
      // The migrated enumeration carries the three legacy codes unchanged, because the
      // numbers are stored data rather than an implementation detail.
      expect(ModuleVisibility.Maximized).toBe(0);
      expect(ModuleVisibility.Minimized).toBe(1);
      expect(ModuleVisibility.None).toBe(2);

      call.flush({ data: DETAIL, meta: null } satisfies ApiResponse<ModuleDetail>, {
        status: 201,
        statusText: 'Created',
      });
      expect(recorded.values.length).toBe(1);
    });
  });
  // -------------------------------------------------------------------------
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  //
  // `http.get<ModuleDetail>(...)` compiles to `http.get(...)`: the interface is erased and
  // nothing inspects the body. Each case below answers with a body the server would never
  // send and requires the OBSERVABLE TO FAIL at the boundary, naming the member — rather
  // than letting a blank field, a `NaN`, a wrongly expanded module or a silently empty
  // grid surface layers away from the response that caused it.
  //
  // The refusal names the member path and the expected TYPE, never the value. That matters
  // most here: the settings maps and the exported document are module CONTENT.
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
      const failures: unknown[] = [];
      const values: unknown[] = [];

      source.subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      httpMock.expectOne(url).flush(body);

      expect(values).toEqual([]);
      expect(failures.length).toBe(1);

      const failure: unknown = failures[0];

      expect(isContractViolation(failure)).toBeTrue();

      if (isContractViolation(failure)) {
        expect(failure.path).toBe(path);
        expect(failure.received)
          .withContext('a type name, never the value')
          .not.toContain('Announcements');
      }
    }

    it('refuses a page with no metadata rather than reporting the tenant has no modules', () => {
      expectViolationAt(
        service.listModules({}),
        '/api/v1/modules',
        { items: [LIST_ROW] },
        'response.meta',
      );
    });

    it('refuses a listed row whose order arrived as text', () => {
      expectViolationAt(
        service.listModules({}),
        '/api/v1/modules',
        {
          items: [{ ...LIST_ROW, moduleOrder: '1' }],
          meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
        },
        'response.items[0].moduleOrder',
      );
    });

    it('refuses a visibility code outside the published table', () => {
      // ⚠ ZERO IS `Maximized`, WHICH IS WHY THIS IS REFUSED RATHER THAN COERCED. Coercing an
      // unrecognised code would silently present a module as fully expanded — the most
      // visible of the three states — on the strength of a code this client did not know.
      expectViolationAt(
        service.getModule(0),
        '/api/v1/modules/0',
        { data: { ...DETAIL, visibility: 7 }, meta: null },
        'response.data.visibility',
      );
    });

    it('refuses a cache lifetime that arrived as null', () => {
      // `cacheTime` is required and non-nullable. A null reaching the settings form would
      // render blank and then be written back as zero — turning caching off for a module
      // nobody asked to change.
      expectViolationAt(
        service.getModule(0),
        '/api/v1/modules/0',
        { data: { ...DETAIL, cacheTime: null }, meta: null },
        'response.data.cacheTime',
      );
    });

    it('refuses a settings map whose value arrived as a number', () => {
      // The server publishes both maps as string-to-string. Stringifying a number here
      // would write back a value the operator never typed, and the write answers 204 either
      // way, so nothing downstream would ever reveal it.
      expectViolationAt(
        service.getModuleSettings(0),
        '/api/v1/modules/0/settings',
        {
          data: { ...SETTINGS_BAG, moduleSettings: { cacheTime: 3600 } },
          meta: null,
        },
        'response.data.moduleSettings.cacheTime',
      );
    });

    it('keeps a settings entry whose value is the empty string', () => {
      // ⚠ THE CLEARED-SETTING CASE. An empty value IS the legacy spelling of an absent
      // string — `Null.vb:L71-L75` returns `""` literally — so an entry the operator
      // cleared must survive the boundary intact rather than being filtered out as falsy.
      const values: (ModuleSettingsBag | null)[] = [];

      service.getModuleSettings(0).subscribe({
        next: (bag: ModuleSettingsBag | null) => values.push(bag),
      });

      httpMock.expectOne('/api/v1/modules/0/settings').flush({
        data: {
          moduleId: 0,
          tabModuleId: 1,
          moduleSettings: { announcementLength: '', cacheTime: '0' },
          tabModuleSettings: {},
        },
        meta: null,
      });

      expect(values.length).toBe(1);
      expect(values[0]?.moduleSettings).toEqual({ announcementLength: '', cacheTime: '0' });
    });

    it('admits a null settings payload, because the contract publishes it as nullable', () => {
      const values: (ModuleSettingsBag | null)[] = [];

      service.getModuleSettings(0).subscribe({
        next: (bag: ModuleSettingsBag | null) => values.push(bag),
      });

      httpMock.expectOne('/api/v1/modules/0/settings').flush({ data: null, meta: null });

      expect(values).toEqual([null]);
    });

    it('refuses a definition whose friendly name is absent', () => {
      // Non-nullable on the catalogue contract, where the listing publishes it as nullable:
      // a definition in the catalogue always has both names, whereas a listed placement may
      // join to a definition that no longer resolves.
      const malformed: Record<string, unknown> = { ...DEFINITION };

      delete malformed['friendlyName'];

      expectViolationAt(
        service.getModuleDefinition(14),
        '/api/v1/module-definitions/14',
        { data: malformed, meta: null },
        'response.data.friendlyName',
      );
    });

    it('refuses a definition catalogue that is not an array', () => {
      expectViolationAt(
        service.listModuleDefinitions(),
        '/api/v1/module-definitions',
        { data: DEFINITION, meta: null },
        'response.data',
      );
    });

    it('accepts an empty exported document, and refuses one that is not text', () => {
      // An empty export is legitimate — a module with no content exports nothing — so the
      // decoder asserts the type and imposes no minimum length.
      const documents: string[] = [];

      service
        .exportModule(0, { fileName: 'Announcements.xml', folder: null })
        .subscribe({ next: (text: string) => documents.push(text) });

      httpMock.expectOne('/api/v1/modules/0/export').flush('', {
        status: 200,
        statusText: 'OK',
      });

      expect(documents).toEqual(['']);

      const failures: unknown[] = [];

      service
        .exportModule(0, { fileName: 'Announcements.xml', folder: null })
        .subscribe({ error: (failure: unknown) => failures.push(failure) });

      // A 204 answers with no body at all, which the client surfaces as null. Left
      // unchecked it would flow on as the document an export screen writes to a file.
      httpMock
        .expectOne('/api/v1/modules/0/export')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(failures.length).toBe(1);
      expect(isContractViolation(failures[0])).toBeTrue();
    });

    it('ignores a member the server added that this client does not declare', () => {
      const values: (ModuleDetail | null)[] = [];

      service.getModule(0).subscribe({
        next: (module: ModuleDetail | null) => values.push(module),
      });

      httpMock
        .expectOne('/api/v1/modules/0')
        .flush({ data: { ...DETAIL, someMemberAddedLater: 'ignored' }, meta: null });

      expect(values).toEqual([DETAIL]);
    });
  });

  // -------------------------------------------------------------------------
  // WHO ANNOUNCES A FAILURE
  //
  // Every request is marked as presented by its caller, which is what stops one failure
  // being shown twice — once as the interceptor's transient notification and once as the
  // in-page banner `module.store` records it for. The interceptor still re-throws.
  // -------------------------------------------------------------------------
  describe('marks every request as presented by its caller', () => {
    it('marks every one of the twelve operations', () => {
      const swallow = { error: () => undefined };

      service.listModules({}).subscribe(swallow);
      service.getModule(0).subscribe(swallow);
      service.createModule(CREATE_REQUEST).subscribe(swallow);
      service.updateModule(0, UPDATE_REQUEST).subscribe(swallow);
      service.deleteModule(0).subscribe(swallow);
      service.getModuleSettings(0).subscribe(swallow);
      service.updateModuleSettings(0, SETTINGS_BAG).subscribe(swallow);
      service.exportModule(0, { fileName: 'a.xml', folder: null }).subscribe(swallow);
      service
        .importModule({ moduleId: 0, content: '<a />', folder: null, fileName: 'a.xml' })
        .subscribe(swallow);
      service.listModuleDefinitions().subscribe(swallow);
      service.getModuleDefinition(14).subscribe(swallow);
      service.listDesktopModuleDefinitions(2).subscribe(swallow);

      const issued = httpMock.match(() => true);

      expect(issued.length).toBe(12);

      for (const pending of issued) {
        expect(pending.request.context.get(PRESENTED_IN_CONTEXT))
          .withContext(`${pending.request.method} ${pending.request.urlWithParams} is unmarked`)
          .toBeTrue();
      }

      for (const pending of issued) {
        pending.flush(null, { status: 500, statusText: 'Server Error' });
      }
    });
  });
});
