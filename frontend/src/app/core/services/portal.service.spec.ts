/**
 * Specification for {@link PortalService} — the typed transport over the portal
 * resource, its settings projection and its host-name aliases.
 *
 * The legacy tree carries ZERO automated tests of any kind, so every assertion below
 * is net-new coverage rather than a port of an existing one. This file is also where
 * the paging and search contract is pinned down for the whole application: the role,
 * user and module specifications follow the shape established here, so a decision
 * taken in this file is a decision taken four more times downstream.
 *
 * ---------------------------------------------------------------------------
 * WHAT IS UNDER TEST, AND WHAT DELIBERATELY IS NOT.
 *
 * The service is a transport, so the only behaviour it can get wrong is the request
 * it puts on the wire and the payload it hands back. Every test therefore asserts
 * some combination of four things and nothing else: the URL, the method, the request
 * body or query string, and the value the caller receives. That is the whole surface.
 *
 * Three neighbouring concerns are owned elsewhere and are NOT asserted here, because
 * asserting them would mean testing two units at once and would leave both specs
 * claiming the same guarantee:
 *
 *   * TRANSLATION of a failure into something a person reads. The error interceptor
 *     owns that. Here a failure is only ever checked for having reached the caller
 *     unchanged — no message wording, no severity, no user-facing text.
 *   * HEADERS. The correlation identifier and the bearer token are attached by
 *     interceptors registered in `app.config.ts`, and the testing backend below is
 *     configured with the real client but NO interceptor chain, precisely so that
 *     what this file observes is the service's own contribution and nothing else.
 *   * URL CONSTRUCTION. `core/config/api-endpoints.ts` builds every path from the
 *     configured base. This specification does not import it — see the next section.
 *
 * ---------------------------------------------------------------------------
 * ⚠️ EVERY EXPECTED URL IS A RELATIVE LITERAL, AND THAT IS THE POINT.
 *
 * The `test` target in `angular.json` declares NO `fileReplacements` and no
 * configurations at all, so a specification compiles against `environment.ts` — which
 * IS the production environment file, the development one being the replacement rather
 * than the other way round. Its `apiBaseUrl` is the RELATIVE `'/api/v1'`, and it has
 * to stay relative: the proxy serves the bundle and forwards `/api/` to the API
 * container on that same origin, and the compose service name it forwards to does not
 * resolve in a browser at all.
 *
 * So the expected URLs below are spelled out as literal relative strings rather than
 * read back from the endpoint catalogue. Importing the catalogue would make this file
 * agree with whatever that module happens to produce, including a doubled version
 * segment or an absolute host — the two mistakes in this area that no compiler, no
 * linter and no successful build detects. Writing the literal makes the specification
 * an independent check on the catalogue instead of a mirror of it.
 *
 * ---------------------------------------------------------------------------
 * ⚠️ A STRING MATCHER COMPARES AGAINST THE URL *INCLUDING* ITS QUERY STRING.
 *
 * `HttpTestingController.expectOne('...')` filters on `request.urlWithParams`, not on
 * `request.url`. Two consequences run through every test below, and both are
 * deliberate rather than incidental:
 *
 *   * For a call that sends NO query string, the string matcher is the STRICTEST
 *     available assertion — matching `'/api/v1/portals/3/aliases'` proves both the
 *     path and the total absence of a query string in one step. That is exactly why
 *     the unpaged alias listing and the "no legacy query key" cases use it.
 *   * For a call that DOES send a query string, a string matcher would have to spell
 *     out the serialised parameters and would then be asserting encoding order as
 *     though it were contract. Those calls use the predicate overload against
 *     `request.url` and assert the parameters individually, by name and by value.
 *
 * Paging here is OFFSET paging — a page index and a page size — and there is no
 * alternative addressing scheme to assert, so none is looked for.
 */

import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { BannerAdvertisingMode, UserRegistrationMode } from '../models/portal.model';
import { DEFAULT_PAGE_SIZE } from '../models/paged-result.model';
import { PortalService } from './portal.service';

import type { TestRequest } from '@angular/common/http/testing';
import type { Observable } from 'rxjs';

import type { ApiResponse, PagedResponse } from '../models/paged-result.model';
import type {
  CreatePortalAliasRequest,
  CreatePortalRequest,
  PortalAlias,
  PortalDetail,
  PortalListItem,
  PortalSettings,
  UpdatePortalAliasRequest,
  UpdatePortalRequest,
  UpdatePortalSettingsRequest,
} from '../models/portal.model';

// ---------------------------------------------------------------------------
// The addresses under test, as relative literals.
// ---------------------------------------------------------------------------

/** The portal collection. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The identifier used for every single-portal case.
 *
 * A plainly ordinary value, so that the two extraordinary ones — zero and minus one —
 * stand out as the deliberate cases they are rather than blending into the rest.
 */
const PORTAL_ID = 3;

/** One portal. */
const PORTAL_URL = '/api/v1/portals/3';

/** One portal's settings projection. */
const PORTAL_SETTINGS_URL = '/api/v1/portals/3/settings';

/** One portal's alias collection. */
const PORTAL_ALIASES_URL = '/api/v1/portals/3/aliases';

/** The identifier used for every single-alias case. */
const PORTAL_ALIAS_ID = 7;

/** One alias of one portal. */
const PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/7';

/**
 * The query-string key the legacy alias editor used, retained here ONLY so that its
 * absence can be asserted by name rather than inferred from a count.
 *
 * MIGRATION: the alias identifier is a PATH segment named `portalAliasId` in the
 * target, not a query parameter. The legacy edit screen passed it in the query string
 * under this abbreviated spelling. Naming it makes the regression it guards against
 * legible: a reintroduction would be a request that still reaches the right path and
 * still returns 200, differing only by a parameter the server ignores.
 *
 * Honesty note, reproduced verbatim from the planning record for the sibling endpoint
 * catalogue rather than re-measured for this file: the legacy citation for this key is
 * `Website/admin/Portal/EditPortalAlias.ascx.vb:L57`.
 */
const LEGACY_ALIAS_QUERY_KEY = 'paid';

// ---------------------------------------------------------------------------
// The wire query-parameter names, spelled out.
// ---------------------------------------------------------------------------

/**
 * The parameter names a collection request may carry, as literals.
 *
 * Spelled out here rather than imported from the query-string helper for the same
 * reason the URLs are: a specification that reads the names back from the module that
 * emits them cannot detect a rename, because both sides would move together. These
 * are the names the SERVER binds, so they are contract, and the two that are easiest
 * to get wrong are recorded explicitly:
 *
 *   * `pageIndex`, not `page`. The shared pagination component's input is a ONE-based
 *     `page`, mapped to this ZERO-based wire index in the feature stores. Neither side
 *     is to be corrected to match the other.
 *   * `sortDir`, not `sortDirection`. The server binds the abbreviated name and its
 *     values are the capitalised member names, so an abbreviated or lower-cased value
 *     is answered with a 400 rather than quietly defaulted.
 */
const QUERY_KEY = {
  pageIndex: 'pageIndex',
  pageSize: 'pageSize',
  sortBy: 'sortBy',
  sortDir: 'sortDir',
  query: 'query',
  name: 'name',
} as const;

// ---------------------------------------------------------------------------
// Failure-document fixtures.
// ---------------------------------------------------------------------------

/**
 * The shape of an RFC 7807 failure document, declared LOCALLY and minimally.
 *
 * Deliberately not imported from `core/models/problem-details.model`: this file does
 * not assert the failure contract, it asserts that whatever the server wrote reaches
 * the caller unchanged. A local declaration keeps the fixture honest about being a
 * fixture, and keeps the failure contract owned by the specification that does test it.
 *
 * MEASURED, not assumed: the API writes NO `code` member. It writes the standard
 * members plus exactly two extensions, a trace identifier and a correlation
 * identifier, so a failure CODE travels inside `type`, rendered as
 * `urn:dnnmigration:error:<code>`. That is why the cases below carry their code there.
 */
interface FailureDocument {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
}

/**
 * Builds a failure document carrying a code, exactly as the API renders one.
 *
 * @param code The failure code, dotted, as the server spells it.
 * @param status The HTTP status the document accompanies.
 * @param title The short summary the server writes.
 * @param detail The explanatory sentence the server writes.
 * @returns The document to flush.
 */
function failureDocument(
  code: string,
  status: number,
  title: string,
  detail: string,
): FailureDocument {
  return {
    type: `urn:dnnmigration:error:${code}`,
    title,
    status,
    detail,
  };
}

/**
 * Recovers the failure code from a document that reached the caller.
 *
 * Reads the value back out of the transport rather than out of the fixture, which is
 * what makes the dotted-code cases below real assertions instead of tautologies: the
 * string is written on one side of the wire and recovered on the other.
 *
 * Accepts `unknown` because that is honestly what a failure body is — the caller
 * cannot know the server wrote a document at all — and narrows explicitly rather than
 * asserting a type it has not checked.
 *
 * @param body The body carried by the failure.
 * @returns The code, or `null` when the body carries no recognisable one.
 */
function failureCodeOf(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const held: unknown = (body as { readonly type?: unknown }).type;
  if (typeof held !== 'string') {
    return null;
  }

  const marker = 'urn:dnnmigration:error:';
  return held.startsWith(marker) ? held.slice(marker.length) : null;
}

// ---------------------------------------------------------------------------
// Payload fixtures. Every member name below is taken from the model contracts, so a
// renamed member breaks compilation here rather than silently reading `undefined`.
// ---------------------------------------------------------------------------

/**
 * One row of the portal listing.
 *
 * The listing row is a genuinely different contract from the detail record — it carries
 * the alias host names as bare strings and the tallies the grid displayed, and it omits
 * everything the grid never showed. Using the detail contract here would test a body
 * the server does not send for this endpoint.
 *
 * @param portalId The identifier the row carries. Callers pass the extraordinary values
 * deliberately, so nothing is defaulted.
 * @param portalName The display name.
 * @returns The row.
 */
function portalListItem(portalId: number, portalName: string): PortalListItem {
  return {
    portalId,
    portalName,
    aliases: ['localhost'],
    users: 3,
    pages: 12,
    hostSpace: 0,
    hostFee: 0,
    expiryDate: null,
  };
}

/**
 * A page of portal rows, in the envelope a collection endpoint actually writes.
 *
 * The paging facts are NESTED under `meta`; they are not siblings of `items`. The two
 * arrangements are indistinguishable to a type checker and to an assertion on a
 * successful status, because reading a member the body does not carry yields
 * `undefined` at run time while compiling perfectly — so flushing the flat shape would
 * produce a green test over a body the server never sends.
 *
 * @param items The rows on the page.
 * @param pageIndex The zero-based index of the page these rows came from.
 * @param pageSize The size of the page.
 * @param totalCount The size of the whole match set.
 * @returns The body to flush.
 */
function portalListBody(
  items: readonly PortalListItem[],
  pageIndex: number,
  pageSize: number,
  totalCount: number,
): PagedResponse<PortalListItem> {
  return {
    items,
    meta: {
      totalCount,
      pageIndex,
      pageSize,
      totalPages: pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0,
    },
  };
}

/**
 * One portal in full.
 *
 * Every member the contract declares is present, including the ones holding a legacy
 * sentinel, because the API serialises with its ignore condition set to never: a member
 * with no value travels as its sentinel or as `null`, and never goes missing.
 *
 * @param portalId The identifier. Passed rather than defaulted, so the zero and
 * minus-one cases read as the deliberate choices they are.
 * @returns The record.
 */
function portalDetail(portalId: number): PortalDetail {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: 'Copyright 2026',
    logoFile: 'logo.gif',
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    email: 'admin@example.test',
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    users: 3,
    pages: 12,
    administratorRoleId: 0,
    administratorRoleName: 'Administrators',
    registeredRoleId: 1,
    registeredRoleName: 'Registered Users',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    adminTabId: 2,
    superTabId: -1,
    splashTabId: -1,
    homeTabId: -1,
    loginTabId: -1,
    userTabId: -1,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    aliases: [portalAlias(portalId, PORTAL_ALIAS_ID, 'localhost')],
  };
}

/**
 * One portal's settings projection.
 *
 * ⚠️ There is NO settings TABLE behind this contract. Portal configuration lives as
 * COLUMNS on the portal row, so this is a projection of that row and not a bag of
 * key/value pairs — which is why the service publishes no `getSetting(key)`,
 * `setSetting(key, value)` or per-key patch member, and why nothing here asserts one.
 * The legacy per-request composite of the same name was an ambient object assembled
 * from those columns, never a persisted aggregate.
 *
 * @param portalId The identifier the projection belongs to.
 * @returns The projection.
 */
function portalSettings(portalId: number): PortalSettings {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: 'Copyright 2026',
    logoFile: 'logo.gif',
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    splashTabId: -1,
    homeTabId: -1,
    loginTabId: -1,
    userTabId: -1,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',
  };
}

/**
 * One host-name alias.
 *
 * @param portalId The portal the alias resolves to.
 * @param portalAliasId The alias identifier.
 * @param httpAlias The host name, optionally with a port.
 * @returns The alias.
 */
function portalAlias(portalId: number, portalAliasId: number, httpAlias: string): PortalAlias {
  return { portalAliasId, portalId, httpAlias };
}

/**
 * Wraps a single record in the success envelope the API writes.
 *
 * The metadata companion is PRESENT AND NULL rather than absent, because that is what
 * the server writes: it serialises with its ignore condition set to never, so a
 * response with no page to describe writes the member with a null value. Flushing the
 * member out altogether would test a body the server never sends.
 *
 * @param data The payload.
 * @returns The envelope to flush.
 * @typeParam T The payload contract.
 */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

// ---------------------------------------------------------------------------
// Request fixtures.
// ---------------------------------------------------------------------------

/**
 * A portal-creation request, with every member the contract declares.
 *
 * MIGRATION: replaces a FIFTEEN-parameter positional call, eleven of whose parameters
 * were strings, so adjacent arguments were interchangeable to the compiler and a
 * transposed pair produced a portal with its description in its keywords and no error
 * anywhere. A named contract cannot be transposed.
 *
 * @param overrides Members to replace, for the fidelity cases.
 * @returns The request body.
 */
function createPortalRequest(overrides: Partial<CreatePortalRequest> = {}): CreatePortalRequest {
  return {
    portalName: 'Contoso',
    portalAlias: 'contoso.example.test',
    description: 'A tenant for the fidelity cases.',
    keyWords: 'contoso',
    homeDirectory: 'Portals/1',
    templateFile: 'Default Website.template',
    isChildPortal: false,
    administratorFirstName: 'Ada',
    administratorLastName: 'Lovelace',
    administratorUsername: 'ada',
    administratorPassword: 'not-a-real-password',
    administratorEmail: 'ada@example.test',
    ...overrides,
  };
}

/**
 * A portal-update request, with every member the contract declares.
 *
 * MIGRATION: replaces a TWENTY-SEVEN-parameter positional call. The member count here
 * is the same twenty-seven, which is the point — the arity did not shrink, the
 * addressing changed from position to name.
 *
 * @param overrides Members to replace, for the sentinel cases.
 * @returns The request body.
 */
function updatePortalRequest(overrides: Partial<UpdatePortalRequest> = {}): UpdatePortalRequest {
  return {
    portalId: PORTAL_ID,
    portalName: 'Contoso',
    logoFile: 'logo.gif',
    footerText: 'Copyright 2026',
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    paymentProcessor: null,
    processorUserId: null,
    processorCredentialReference: null,
    description: 'A tenant for the sentinel cases.',
    keyWords: 'contoso',
    backgroundFile: null,
    siteLogHistory: -1,
    splashTabId: -1,
    homeTabId: -1,
    loginTabId: -1,
    userTabId: -1,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/1',
    ...overrides,
  };
}

/**
 * A settings-update request.
 *
 * The contract is the portal-update contract WITHOUT the identifier, because the
 * identifier is already in the path and accepting a second copy in the body would let
 * the two disagree.
 *
 * @param overrides Members to replace.
 * @returns The request body.
 */
function updatePortalSettingsRequest(
  overrides: Partial<UpdatePortalSettingsRequest> = {},
): UpdatePortalSettingsRequest {
  // The identifier is destructured away rather than deleted afterwards, so the
  // exclusion is a property of the expression rather than a mutation of the result.
  const { portalId: _excludedFromBody, ...settings } = updatePortalRequest();
  return { ...settings, ...overrides };
}

/** An alias-creation request. Its single member is the host name. */
function createPortalAliasRequest(httpAlias = 'contoso.example.test'): CreatePortalAliasRequest {
  return { httpAlias };
}

/** An alias-update request. Its single member is the host name. */
function updatePortalAliasRequest(httpAlias = 'www.contoso.example.test'): UpdatePortalAliasRequest {
  return { httpAlias };
}

// ---------------------------------------------------------------------------
// Observation helpers.
// ---------------------------------------------------------------------------

/** Everything one call produced, recorded as it happened. */
interface Observed<T> {
  /** The values the caller received, in order. */
  readonly values: readonly T[];

  /**
   * The failures the caller received.
   *
   * Typed as `unknown` because that is honestly what reaches a subscriber: a caller
   * cannot know in advance that a failure is a transport failure, so the narrowing is
   * done explicitly by {@link soleFailure} rather than assumed by this declaration.
   */
  readonly failures: readonly unknown[];

  /** Whether the call completed. */
  isComplete(): boolean;
}

/**
 * Subscribes immediately and records the outcome.
 *
 * Subscribing is what ISSUES the request — the service returns a cold observable and
 * nothing in it starts a call — so every test below observes first and only then
 * expects a request. Recording rather than awaiting keeps each test synchronous and
 * deterministic, and lets an assertion state exactly how many values arrived, which is
 * how an extra emission or a missing one gets caught rather than overlooked.
 *
 * @param source The call to observe.
 * @returns The record, readable once the request has been flushed.
 * @typeParam T The value the call produces.
 */
function observe<T>(source: Observable<T>): Observed<T> {
  const values: T[] = [];
  const failures: unknown[] = [];
  let complete = false;

  source.subscribe({
    next: (value: T) => {
      values.push(value);
    },
    error: (failure: unknown) => {
      failures.push(failure);
    },
    complete: () => {
      complete = true;
    },
  });

  return { values, failures, isComplete: () => complete };
}

/**
 * Asserts that exactly one transport failure reached the caller, and returns it.
 *
 * @param observed The recorded outcome.
 * @returns The failure.
 */
function soleFailure(observed: Observed<unknown>): HttpErrorResponse {
  expect(observed.failures.length)
    .withContext('exactly one failure reaches the caller')
    .toBe(1);
  expect(observed.values)
    .withContext('a failed call emits no value')
    .toEqual([]);

  const failure: unknown = observed.failures[0];
  if (failure instanceof HttpErrorResponse) {
    return failure;
  }

  throw new Error(`Expected a transport failure; received ${String(failure)}.`);
}

/**
 * Asserts that a request carried no query string whatsoever.
 *
 * Three checks rather than one, because they fail differently and each names its own
 * regression: the count catches an added parameter, the equality names which one was
 * added, and comparing the addressed URL against the bare path catches a parameter
 * appended to the path itself rather than through the parameter collection. The legacy
 * alias key is then asserted absent BY NAME, so its reintroduction is reported as
 * itself rather than as an off-by-one in a count.
 *
 * @param request The observed request.
 * @param description What the request was, for the failure message.
 */
function expectNoQueryString(request: TestRequest, description: string): void {
  expect(request.request.params.keys().length)
    .withContext(`${description}: carries no query parameter`)
    .toBe(0);
  expect(request.request.params.keys())
    .withContext(`${description}: parameter names`)
    .toEqual([]);
  expect(request.request.urlWithParams)
    .withContext(`${description}: the addressed URL is the bare path`)
    .toBe(request.request.url);
  expect(request.request.params.has(LEGACY_ALIAS_QUERY_KEY))
    .withContext(`${description}: the legacy alias query key is not sent`)
    .toBeFalse();
}

describe('PortalService', () => {
  let service: PortalService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client with NO interceptor chain. This specification is about the
      // service's own contribution to a request, and running the interceptors here
      // would mean asserting two units at once and would attach headers this file has
      // no business claiming.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(PortalService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The single most important line in this file. It fails if the service issued a
    // request no test expected, which is the only automatic way an invented endpoint
    // or a stray second call gets caught. Every test below therefore also contributes
    // to one collective assertion: that the surface is exactly the twelve members it
    // claims to be and no more.
    httpMock.verify();
  });

  describe('the service itself', () => {
    it('resolves from the root injector as a single instance', () => {
      expect(service).toBeTruthy();
      // Declared `providedIn: 'root'`, so two resolutions are the same object. A
      // per-injector service would give each caller its own, which for a stateless
      // transport would be harmless but would also mean the declaration was not doing
      // what it says.
      expect(TestBed.inject(PortalService))
        .withContext('the same instance is shared by every caller')
        .toBe(service);
    });
  });

  describe('list', () => {
    it('gets the portal collection with no query string when nothing is filtered or paged', () => {
      const body = portalListBody([portalListItem(-1, 'Baseline Portal')], 0, 20, 1);

      const observed = observe(service.list({}));

      // The string matcher compares against the URL INCLUDING its query string, so
      // matching the bare path is itself the proof that nothing was appended.
      const request = httpMock.expectOne(PORTALS_URL);
      expect(request.request.method).toBe('GET');
      expectNoQueryString(request, 'an unfiltered, unpaged listing');

      request.flush(body);

      expect(observed.values)
        .withContext('the page reaches the caller with its rows and its coordinates')
        .toEqual([body]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('sends every paging, sorting and filtering parameter under its wire name', () => {
      const observed = observe(
        service.list({
          pageIndex: 1,
          pageSize: 20,
          sortBy: 'portalName',
          sortDir: 'Ascending',
          query: 'Contoso',
        }),
      );

      // A predicate matcher, because this call DOES carry a query string and a string
      // matcher would have to spell out the serialised parameters — which would assert
      // encoding order as though it were contract.
      const request = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'the paged portal listing',
      );

      expect(request.request.url)
        .withContext('the path is unchanged by the presence of parameters')
        .toBe(PORTALS_URL);
      expect(request.request.params.get(QUERY_KEY.pageIndex)).toBe('1');
      expect(request.request.params.get(QUERY_KEY.pageSize)).toBe('20');
      expect(request.request.params.get(QUERY_KEY.sortBy)).toBe('portalName');
      // The capitalised member name, because the server binds the enumeration member
      // and answers an abbreviated or lower-cased value with a 400.
      expect(request.request.params.get(QUERY_KEY.sortDir)).toBe('Ascending');
      expect(request.request.params.get(QUERY_KEY.query)).toBe('Contoso');
      expect(request.request.params.keys().length)
        .withContext('and nothing beyond those five')
        .toBe(5);

      request.flush(portalListBody([portalListItem(-1, 'Contoso')], 1, 20, 21));

      expect(observed.isComplete()).toBeTrue();
    });

    it('sends a page index of zero as "0" rather than omitting it', () => {
      // ⚠️ ZERO-BASED, and load-bearing. The legacy screen counted from one and
      // subtracted one immediately before calling the provider:
      // `Website/admin/Portal/Portals.ascx.vb:L142` reads
      //   Portals = PortalController.GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)
      // with the one-based counter declared at `:L47` as
      //   Private _CurrentPage As Integer = 1
      // The wire now carries the DATA layer's base directly, so the first page is 0.
      //
      // Zero is the value most easily lost on the way out, because a helper that
      // treats a falsy value as absent drops it — and the request then succeeds,
      // returning whichever page the server defaults to. The assertion is that the
      // parameter is PRESENT and reads "0".
      const observed = observe(service.list({ pageIndex: 0, pageSize: 20 }));

      const request = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'the first page of the portal listing',
      );

      expect(request.request.params.has(QUERY_KEY.pageIndex))
        .withContext('the first page addresses itself explicitly')
        .toBeTrue();
      expect(request.request.params.get(QUERY_KEY.pageIndex)).toBe('0');

      request.flush(portalListBody([portalListItem(-1, 'Baseline Portal')], 0, 20, 1));

      expect(observed.isComplete()).toBeTrue();
    });

    it('sends a later page index verbatim, with no adjustment in either direction', () => {
      // The third page is index 2. Neither the service nor the query-string helper may
      // add or subtract one: the one-based counter survives only inside the shared
      // pagination component, and the mapping between the two bases happens in the
      // feature store. An adjustment here would serve the neighbouring page and report
      // success while doing it — a defect no status code and no type checker reveals.
      const observed = observe(service.list({ pageIndex: 2, pageSize: 20 }));

      const request = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'the third page of the portal listing',
      );

      expect(request.request.params.get(QUERY_KEY.pageIndex))
        .withContext('index 2 addresses the third page and is transmitted as itself')
        .toBe('2');
      expect(request.request.params.get(QUERY_KEY.pageSize)).toBe('20');

      request.flush(portalListBody([portalListItem(-1, 'Baseline Portal')], 2, 20, 61));

      expect(observed.isComplete()).toBeTrue();
    });

    it('transmits the free-text filter exactly as typed, undecorated', () => {
      // ⚠️ NO CLIENT-SIDE WILDCARD. The legacy call site composed the pattern itself,
      // concatenating a trailing wildcard onto the operator's text BEFORE it reached
      // the data layer — the `Filter + "%"` argument at `Portals.ascx.vb:L142`.
      // Pattern composition now belongs entirely behind the repository interface, so
      // neither this service, nor the shared search input, nor the query-string helper
      // may contribute a character. Decorating the value here would double whatever
      // pattern the repository already builds.
      const observed = observe(service.list({ pageIndex: 0, pageSize: 20, query: 'Cont' }));

      const request = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'a filtered portal listing',
      );

      const transmitted = request.request.params.get(QUERY_KEY.query);
      expect(transmitted)
        .withContext('the search text travels byte for byte, with no pattern syntax added')
        .toBe('Cont');
      expect(transmitted?.endsWith('%'))
        .withContext('no trailing wildcard is appended')
        .toBeFalse();
      expect(transmitted?.startsWith('%'))
        .withContext('and no leading one either')
        .toBeFalse();

      request.flush(portalListBody([portalListItem(-1, 'Contoso')], 0, 20, 1));

      expect(observed.isComplete()).toBeTrue();
    });

    it('transmits the portal-name filter exactly as typed, under its own wire name', () => {
      // The listing accepts TWO textual restrictions and they are different
      // parameters: the paging contract's general free-text filter, asserted above,
      // and this name-specific one supplied separately. Both are raw, and the
      // undecorated rule applies to each — so both are asserted rather than one being
      // taken as evidence for the other.
      const observed = observe(service.list({ pageIndex: 0, pageSize: 20 }, { name: 'Cont' }));

      const request = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'a name-filtered portal listing',
      );

      expect(request.request.params.get(QUERY_KEY.name)).toBe('Cont');
      expect(request.request.params.keys().length)
        .withContext('the paging pair plus the name, and nothing else')
        .toBe(3);

      request.flush(portalListBody([portalListItem(-1, 'Contoso')], 0, 20, 1));

      expect(observed.isComplete()).toBeTrue();
    });

    it('omits a page size the caller did not supply rather than substituting its own', () => {
      // The service contributes no default. The effective page size is a per-portal
      // setting, so an omitted size has to reach the server as an omission for the
      // server's own default to apply. The shared client-side default exists for a
      // caller to send deliberately, and is sent verbatim when it is.
      const withoutSize = observe(service.list({ pageIndex: 0 }));

      const first = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'a listing with no page size',
      );
      expect(first.request.params.has(QUERY_KEY.pageSize))
        .withContext('an unsupplied size is an omission, not a substitution')
        .toBeFalse();
      expect(first.request.params.keys()).toEqual([QUERY_KEY.pageIndex]);
      first.flush(portalListBody([], 0, 0, 0));
      expect(withoutSize.isComplete()).toBeTrue();

      const withSize = observe(service.list({ pageIndex: 0, pageSize: DEFAULT_PAGE_SIZE }));

      const second = httpMock.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
        'a listing with the shared default page size',
      );
      expect(second.request.params.get(QUERY_KEY.pageSize)).toBe(String(DEFAULT_PAGE_SIZE));
      second.flush(portalListBody([], 0, DEFAULT_PAGE_SIZE, 0));
      expect(withSize.isComplete()).toBeTrue();
    });
  });

  describe('getById', () => {
    it('gets one portal and hands back the payload rather than the envelope', () => {
      const detail = portalDetail(PORTAL_ID);

      const observed = observe(service.getById(PORTAL_ID));

      const request = httpMock.expectOne(PORTAL_URL);
      expect(request.request.method).toBe('GET');
      expectNoQueryString(request, 'reading one portal');

      request.flush(envelope(detail));

      // Asserting the exact array proves three things at once: one emission, the
      // unwrapped payload rather than the envelope, and nothing extra. Returning the
      // envelope is the quietest defect available here — it compiles, it returns 200,
      // and every member reads as `undefined` because the real body had them a level
      // deeper.
      expect(observed.values).toEqual([detail]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('addresses portal 0 and portal -1, because both are real portals', () => {
      // ⚠️ TWO FACTS COLLIDE, and a guard on either one drops a legitimate request.
      //
      //   * `01.00.00.SqlDataProvider:L77` declares
      //       [PortalID] [int] IDENTITY (-1, 1) NOT NULL
      //     so the FIRST portal ever created carries -1 and the second carries 0.
      //   * `Library/Components/Shared/Null.vb:L41-L45` defines the absent-integer
      //     marker as -1, its body being literally `Return -1`.
      //
      // The same number therefore means both "the first portal" and "no portal",
      // distinguishable only by context a transport does not have. A truthiness test
      // would drop the request for portal 0; a comparison against the marker would
      // drop the request for portal -1. Neither may be treated as absent, and the
      // route constraint on the server accepts every integer for the same reason.
      const zero = observe(service.getById(0));
      const first = httpMock.expectOne('/api/v1/portals/0');
      expect(first.request.method).toBe('GET');
      first.flush(envelope(portalDetail(0)));
      expect(zero.values.length)
        .withContext('portal 0 is addressable')
        .toBe(1);

      const seeded = observe(service.getById(-1));
      const second = httpMock.expectOne('/api/v1/portals/-1');
      expect(second.request.method).toBe('GET');
      second.flush(envelope(portalDetail(-1)));
      expect(seeded.values.length)
        .withContext('portal -1 is addressable, and is the seeded first portal')
        .toBe(1);
    });
  });

  describe('create', () => {
    it('posts the request to the collection and returns the created portal on 201', () => {
      const request = createPortalRequest();
      const created = portalDetail(1);

      const observed = observe(service.create(request));

      const pending = httpMock.expectOne(PORTALS_URL);
      expect(pending.request.method).toBe('POST');
      expectNoQueryString(pending, 'creating a portal');
      expect(pending.request.body)
        .withContext('the body is the request the caller composed, member for member')
        .toEqual(request);

      pending.flush(envelope(created), { status: 201, statusText: 'Created' });

      expect(observed.values).toEqual([created]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('transmits an empty string and a false boolean rather than eliding either', () => {
      // ⚠️ SENTINEL FIDELITY on the way OUT. The legacy absent-text marker is literally
      // the empty string — `Library/Components/Shared/Null.vb:L71-L75` has the body
      // `Return ""` — and the absent-boolean marker is literally false, at `:L76-L80`
      // with the body `Return False`. So neither value means "no value" in this domain;
      // each is a value. The API serialises with its ignore condition set to NEVER, so
      // the two sides of the wire agree that a written member is present regardless of
      // what it holds.
      //
      // Dropping a member here because it looked empty would ask for a different portal
      // than the one described, and would do it silently: the request still succeeds.
      //
      // The false boolean rides on the child-portal flag because that is the ONLY
      // boolean either portal request contract declares — the update contract has none.
      const request = createPortalRequest({
        description: '',
        keyWords: '',
        isChildPortal: false,
      });

      const observed = observe(service.create(request));

      const pending = httpMock.expectOne(PORTALS_URL);
      const body = pending.request.body;

      expect(body)
        .withContext('every member survives, whatever it holds')
        .toEqual(request);
      expect(Object.keys(body as object))
        .withContext('the empty-string members are present as keys')
        .toContain('description');
      expect((body as CreatePortalRequest).description)
        .withContext('an empty string is transmitted as an empty string')
        .toBe('');
      expect((body as CreatePortalRequest).keyWords).toBe('');
      expect((body as CreatePortalRequest).isChildPortal)
        .withContext('false is transmitted as false, not omitted')
        .toBeFalse();

      pending.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });

      expect(observed.isComplete()).toBeTrue();
    });

    it('lets a 409 duplicate-alias conflict reach the caller untouched', () => {
      // The refusal is reported by the server when the requested host name already
      // resolves to a portal. The document is asserted for having ARRIVED INTACT, not
      // for what it says: turning it into something a person reads is the error
      // interceptor's job, and asserting a message here would duplicate that guarantee
      // in a file that does not own it.
      const document = failureDocument(
        'portal.alias_duplicate',
        409,
        'Conflict',
        'The portal alias specified is already in use.',
      );

      const observed = observe(service.create(createPortalRequest()));

      const pending = httpMock.expectOne(PORTALS_URL);
      pending.flush(document, { status: 409, statusText: 'Conflict' });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(409);
      expect(failure.error as unknown)
        .withContext('the document the server wrote is the document the caller holds')
        .toEqual(document);
      expect(failureCodeOf(failure.error))
        .withContext('the code survives the crossing')
        .toBe('portal.alias_duplicate');
    });
  });

  describe('update', () => {
    it('puts the request to the addressed portal and returns the stored portal on 200', () => {
      const request = updatePortalRequest();
      const stored = portalDetail(PORTAL_ID);

      const observed = observe(service.update(PORTAL_ID, request));

      const pending = httpMock.expectOne(PORTAL_URL);
      expect(pending.request.method).toBe('PUT');
      expectNoQueryString(pending, 'updating a portal');
      expect(pending.request.body)
        .withContext('the body is the request the caller composed, member for member')
        .toEqual(request);

      pending.flush(envelope(stored), { status: 200, statusText: 'OK' });

      expect(observed.values).toEqual([stored]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('transmits the minus-one marker and a zero-valued enumeration member unchanged', () => {
      // ⚠️ SENTINEL FIDELITY, the numeric half. `Null.vb:L41-L45` defines the
      // absent-integer marker as -1, and the page references below legitimately hold
      // it: a portal with no splash page really does carry -1 in that column. A helper
      // that mapped -1 to an omission would silently clear the reference instead of
      // leaving it alone.
      //
      // The zero-valued enumeration member is the mirror image of the same hazard. Its
      // first member is deliberately 0, so a serialiser that treated falsy as absent
      // would drop the very setting the caller was changing.
      const request = updatePortalRequest({
        splashTabId: -1,
        siteLogHistory: -1,
        footerText: '',
        bannerAdvertising: BannerAdvertisingMode.None,
        userRegistration: UserRegistrationMode.NoRegistration,
      });

      const observed = observe(service.update(PORTAL_ID, request));

      const pending = httpMock.expectOne(PORTAL_URL);
      const body = pending.request.body as UpdatePortalRequest;

      expect(pending.request.body).toEqual(request);
      expect(body.splashTabId)
        .withContext('the absent-page marker is a value, not an omission')
        .toBe(-1);
      expect(body.siteLogHistory).toBe(-1);
      expect(body.footerText)
        .withContext('an empty string is transmitted as an empty string')
        .toBe('');
      expect(body.bannerAdvertising)
        .withContext('a zero-valued enumeration member is transmitted as 0')
        .toBe(0);
      expect(body.userRegistration).toBe(0);
      expect(Object.keys(body).length)
        .withContext('all twenty-seven members travel, none pruned for looking empty')
        .toBe(27);

      pending.flush(envelope(portalDetail(PORTAL_ID)), { status: 200, statusText: 'OK' });

      expect(observed.isComplete()).toBeTrue();
    });

    it('distinguishes a user quota of zero from a user quota of minus one', () => {
      // ⚠️ TWO DIFFERENT FACTS THAT MUST NEVER BE COALESCED.
      //
      //   * 0 is the value a newly created portal receives when the host has configured
      //     no quota: `Library/Components/Portal/PortalController.vb:L355-L357` reads
      //       Dim intUserQuota As Integer = 0
      //       If Convert.ToString(Common.Globals.HostSettings("UserQuota")) <> "" Then
      //           intUserQuota = Convert.ToInt32(Common.Globals.HostSettings("UserQuota"))
      //     so the zero is a deliberate "no ceiling", not a missing value.
      //   * -1 is what a database NULL becomes on the way in:
      //     `PortalController.vb:L87` reads
      //       objPortalInfo.UserQuota = Convert.ToInt32(Null.SetNull(dr("UserQuota"), objPortalInfo.UserQuota))
      //     and the property it assigns is a non-nullable `Integer`
      //     (`PortalInfo.vb:L181`), which is precisely WHY absence needed a sentinel at
      //     all rather than simply being null.
      //
      // Both therefore occur in real data and mean different things. Sending them as
      // one value would change a portal's ceiling without anyone asking.
      const unlimited = observe(service.update(PORTAL_ID, updatePortalRequest({ userQuota: 0 })));
      const firstCall = httpMock.expectOne(PORTAL_URL);
      expect((firstCall.request.body as UpdatePortalRequest).userQuota)
        .withContext('zero means no ceiling and is transmitted as 0')
        .toBe(0);
      firstCall.flush(envelope(portalDetail(PORTAL_ID)), { status: 200, statusText: 'OK' });
      expect(unlimited.isComplete()).toBeTrue();

      const unset = observe(service.update(PORTAL_ID, updatePortalRequest({ userQuota: -1 })));
      const secondCall = httpMock.expectOne(PORTAL_URL);
      const transmitted = (secondCall.request.body as UpdatePortalRequest).userQuota;
      expect(transmitted)
        .withContext('minus one means not set and is transmitted as -1')
        .toBe(-1);
      expect(transmitted)
        .withContext('the two are never folded into one another')
        .not.toBe(0);
      secondCall.flush(envelope(portalDetail(PORTAL_ID)), { status: 200, statusText: 'OK' });
      expect(unset.isComplete()).toBeTrue();
    });

    it('lets a 403 refusal reach the caller, having attempted no check of its own', () => {
      // Some members of the update contract may only be set by a host-level operator.
      // That rule lives on the server, and there is deliberately no client-side
      // pre-check to assert: a transport that decided for itself which members a caller
      // may send would be a second, divergent copy of an authorisation rule, and the
      // copy would be the one nobody updated. The request goes out in full and the
      // refusal comes back.
      const document = failureDocument(
        'portal.not_permitted',
        403,
        'Forbidden',
        'The current identity may not modify host-level portal settings.',
      );

      const observed = observe(service.update(PORTAL_ID, updatePortalRequest({ hostFee: 25 })));

      const pending = httpMock.expectOne(PORTAL_URL);
      expect(pending.request.method).toBe('PUT');
      expect((pending.request.body as UpdatePortalRequest).hostFee)
        .withContext('the member is sent regardless; the server decides')
        .toBe(25);

      pending.flush(document, { status: 403, statusText: 'Forbidden' });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(403);
      expect(failure.error as unknown).toEqual(document);
    });
  });

  describe('delete', () => {
    it('deletes the addressed portal and completes on a 204 with no body', () => {
      const observed = observe(service.delete(PORTAL_ID));

      const pending = httpMock.expectOne(PORTAL_URL);
      expect(pending.request.method).toBe('DELETE');
      expectNoQueryString(pending, 'deleting a portal');
      expect(pending.request.body)
        .withContext('a deletion carries no body')
        .toBeNull();

      pending.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.isComplete())
        .withContext('an empty success completes rather than stalling')
        .toBeTrue();
      expect(observed.failures).toEqual([]);
    });

    it('lets the last-portal refusal reach the caller with its dotted code intact', () => {
      // ⚠️ THE CODE IS ONE STRING THAT HAPPENS TO CONTAIN A DOT.
      //
      // The dot is a spelling convention inside a single opaque identifier, not a
      // separator between a namespace and a name. Splitting on it — or matching only
      // the trailing half — would turn one identifier into two tokens and break the
      // one thing a caller is supposed to branch on. So the value is written on one
      // side of the wire, recovered on the other, and compared WHOLE.
      //
      // The refusal itself is the migration of a legacy behaviour rather than a new
      // rule: the legacy member reported it by RETURNING the localised message keyed
      // `LastPortal` from the shared resources, with an empty string meaning success,
      // so a caller had to compare display text to learn whether the delete had
      // happened. Here the outcome is a failure whose stable code is what a caller
      // branches on and whose message is what a human reads.
      const legacyResourceKey = 'Portal.LastPortal';
      const document = failureDocument(
        legacyResourceKey,
        409,
        'Conflict',
        'You Can Not Delete The Last Portal In Your Database.',
      );

      const observed = observe(service.delete(PORTAL_ID));

      const pending = httpMock.expectOne(PORTAL_URL);
      pending.flush(document, { status: 409, statusText: 'Conflict' });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(409);
      expect(failure.error as unknown)
        .withContext('the document arrives exactly as written')
        .toEqual(document);

      const recovered = failureCodeOf(failure.error);
      expect(recovered)
        .withContext('the whole dotted identifier survives as one string')
        .toBe('Portal.LastPortal');
      expect(recovered)
        .withContext('and is not reduced to the part after the dot')
        .not.toBe('LastPortal');
      expect(recovered?.includes('.'))
        .withContext('the dot is part of the identifier, not a separator that was consumed')
        .toBeTrue();
      expect(recovered?.length).toBe(legacyResourceKey.length);
    });

    it('lets the refusal reach the caller under the code the server actually emits', () => {
      // The live server spells its failure codes in lower case with underscores, and
      // renders them into the problem type as `urn:dnnmigration:error:<code>`. The
      // dotted form asserted above is the legacy resource key the refusal descends
      // from; this case covers the spelling in use, so the dot-integrity guarantee is
      // not accidentally tied to one taxonomy. Both are single opaque identifiers, and
      // both must cross unchanged.
      const document = failureDocument(
        'portal.last_remaining',
        409,
        'Conflict',
        'You Can Not Delete The Last Portal In Your Database. The installation must retain at least one portal.',
      );

      const observed = observe(service.delete(PORTAL_ID));

      httpMock.expectOne(PORTAL_URL).flush(document, { status: 409, statusText: 'Conflict' });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(409);
      expect(failureCodeOf(failure.error)).toBe('portal.last_remaining');
    });
  });

  describe('getSettings', () => {
    it('gets the settings projection for the addressed portal', () => {
      const settings = portalSettings(PORTAL_ID);

      const observed = observe(service.getSettings(PORTAL_ID));

      const pending = httpMock.expectOne(PORTAL_SETTINGS_URL);
      expect(pending.request.method).toBe('GET');
      expectNoQueryString(pending, 'reading portal settings');

      pending.flush(envelope(settings));

      expect(observed.values).toEqual([settings]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('projects columns of the portal row, carrying none of the retired settings', () => {
      // ⚠️ THERE IS NO SETTINGS TABLE, so there is no key/value surface to test.
      //
      // Four findings settle this, three negative and one positive. The legacy abstract
      // data surface declares no portal-setting member among its 269; the concrete
      // provider invokes no portal-setting procedure among its 245; no such table
      // appears anywhere in the 88-script schema chain, which contains only module,
      // host, page-module and schedule settings tables; and positively, the legacy
      // composite of this name was built per request from
      // `HttpContext.Current.Items("PortalSettings")` — an ambient object assembled from
      // COLUMNS ON THE PORTAL ROW, never a persisted aggregate.
      //
      // Consequently the service publishes one whole-document read and one whole-document
      // write, and no `getSetting(key)`, `setSetting(key, value)` or per-key patch member
      // exists. Nothing below probes for one: reaching for an absent method would require
      // defeating the type system, and a specification that does that stops describing
      // the contract it is meant to pin down.
      //
      // Seven legacy properties are deliberately NOT carried forward, and their absence
      // is asserted by name rather than left implied — an accidental reintroduction would
      // otherwise pass unnoticed, since a superset still satisfies every other assertion
      // in this file.
      const retired = [
        'inlineEditorEnabled',
        'controlPanelMode',
        'controlPanelVisibility',
        'controlPanelSecurity',
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
      ];

      const observed = observe(service.getSettings(PORTAL_ID));
      const pending = httpMock.expectOne(PORTAL_SETTINGS_URL);
      pending.flush(envelope(portalSettings(PORTAL_ID)));

      const projected = observed.values[0];
      const members = Object.keys(projected);

      for (const property of retired) {
        expect(members)
          .withContext(`${property} is not carried forward`)
          .not.toContain(property);
      }

      // And positively: what the projection DOES carry is the portal's own columns.
      expect(members).toContain('portalId');
      expect(members).toContain('userRegistration');
      expect(members).toContain('homeDirectory');
      // Not a key/value pair, which is the shape the retired accessors would have needed.
      expect(members).not.toContain('key');
      expect(members).not.toContain('value');
      expect(members).not.toContain('settings');
    });
  });

  describe('updateSettings', () => {
    it('puts the whole projection to the addressed portal, not one key at a time', () => {
      const request = updatePortalSettingsRequest();
      const stored = portalSettings(PORTAL_ID);

      const observed = observe(service.updateSettings(PORTAL_ID, request));

      const pending = httpMock.expectOne(PORTAL_SETTINGS_URL);
      expect(pending.request.method).toBe('PUT');
      expectNoQueryString(pending, 'updating portal settings');
      expect(pending.request.body)
        .withContext('the body is the whole projected document, member for member')
        .toEqual(request);

      const body = pending.request.body as UpdatePortalSettingsRequest;
      const members = Object.keys(body);
      expect(members.length)
        .withContext('the update contract without its identifier: twenty-six members')
        .toBe(26);
      expect(members)
        .withContext('the identifier travels in the path, so a second copy is not sent')
        .not.toContain('portalId');
      expect(members)
        .withContext('a whole document, not a key/value pair')
        .not.toContain('key');
      expect(members).not.toContain('value');

      pending.flush(envelope(stored), { status: 200, statusText: 'OK' });

      expect(observed.values).toEqual([stored]);
      expect(observed.isComplete()).toBeTrue();
    });
  });

  describe('listAliases', () => {
    it('gets the alias collection UNPAGED, with no query string at all', () => {
      // ⚠️ THE ONLY PAGED MEMBER OF THIS SERVICE IS THE PORTAL LISTING. A portal has a
      // handful of host names, so paging them would add coordinates a caller would then
      // have to thread through for no benefit — and a pager that silently truncated an
      // alias list would hide the alias a tenant is failing to resolve on.
      //
      // The string matcher below compares against the URL INCLUDING its query string, so
      // matching the bare path already proves that no page index, page size, sort field
      // or sort direction was sent. The explicit checks that follow name each regression
      // rather than leaving it to a count.
      const aliases = [
        portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost'),
        portalAlias(PORTAL_ID, 8, 'localhost:4200'),
      ];

      const observed = observe(service.listAliases(PORTAL_ID));

      const pending = httpMock.expectOne(PORTAL_ALIASES_URL);
      expect(pending.request.method).toBe('GET');
      expectNoQueryString(pending, 'listing portal aliases');
      expect(pending.request.params.has(QUERY_KEY.pageIndex)).toBeFalse();
      expect(pending.request.params.has(QUERY_KEY.pageSize)).toBeFalse();
      expect(pending.request.params.has(QUERY_KEY.sortBy)).toBeFalse();
      expect(pending.request.params.has(QUERY_KEY.sortDir)).toBeFalse();

      pending.flush(envelope(aliases));

      expect(observed.values).toEqual([aliases]);
      expect(observed.isComplete()).toBeTrue();
    });
  });

  describe('createAlias', () => {
    it('posts the alias to the portal it belongs to and returns it on 201', () => {
      const request = createPortalAliasRequest();
      const created = portalAlias(PORTAL_ID, 9, 'contoso.example.test');

      const observed = observe(service.createAlias(PORTAL_ID, request));

      const pending = httpMock.expectOne(PORTAL_ALIASES_URL);
      expect(pending.request.method).toBe('POST');
      expectNoQueryString(pending, 'creating a portal alias');
      expect(pending.request.body).toEqual(request);

      pending.flush(envelope(created), { status: 201, statusText: 'Created' });

      expect(observed.values).toEqual([created]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('lets a 409 duplicate-alias conflict reach the caller untouched', () => {
      // The tenant-resolution consequence of getting this wrong is why the server
      // refuses rather than accepting a second identical host name: an alias identifies
      // a tenant, so two portals answering to one host name would make resolution
      // depend on row order.
      const document = failureDocument(
        'portal.alias_duplicate',
        409,
        'Conflict',
        'The Portal Alias specified is already in use.',
      );

      const observed = observe(
        service.createAlias(PORTAL_ID, createPortalAliasRequest('localhost')),
      );

      httpMock.expectOne(PORTAL_ALIASES_URL).flush(document, {
        status: 409,
        statusText: 'Conflict',
      });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(409);
      expect(failure.error as unknown).toEqual(document);
      expect(failureCodeOf(failure.error)).toBe('portal.alias_duplicate');
    });
  });

  describe('getAlias', () => {
    it('gets one alias by its own identifier, nested under its portal', () => {
      const alias = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');

      const observed = observe(service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID));

      const pending = httpMock.expectOne(PORTAL_ALIAS_URL);
      expect(pending.request.method).toBe('GET');
      // ⚠️ The alias identifier is the last PATH segment. The legacy screen carried it
      // in the query string under an abbreviated name; asserting its absence here is
      // what stops that spelling drifting back in, since a stray parameter the server
      // ignores would still return 200.
      expectNoQueryString(pending, 'reading one portal alias');

      pending.flush(envelope(alias));

      expect(observed.values).toEqual([alias]);
      expect(observed.isComplete()).toBeTrue();
    });
  });

  describe('updateAlias', () => {
    it('puts the alias to its own address and completes on 200', () => {
      const request = updatePortalAliasRequest();

      const observed = observe(service.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, request));

      const pending = httpMock.expectOne(PORTAL_ALIAS_URL);
      expect(pending.request.method).toBe('PUT');
      expectNoQueryString(pending, 'updating one portal alias');
      expect(pending.request.body)
        .withContext('the body is the request the caller composed')
        .toEqual(request);

      pending.flush(null, { status: 200, statusText: 'OK' });

      // The member returns no payload, so there is nothing to unwrap and nothing to
      // assert beyond the request and the completion.
      expect(observed.isComplete()).toBeTrue();
      expect(observed.failures).toEqual([]);
    });
  });

  describe('deleteAlias', () => {
    it('deletes the alias at its own address and completes on a 204 with no body', () => {
      const observed = observe(service.deleteAlias(PORTAL_ID, PORTAL_ALIAS_ID));

      const pending = httpMock.expectOne(PORTAL_ALIAS_URL);
      expect(pending.request.method).toBe('DELETE');
      expectNoQueryString(pending, 'deleting one portal alias');
      expect(pending.request.body).toBeNull();

      pending.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.isComplete()).toBeTrue();
      expect(observed.failures).toEqual([]);
    });
  });

  describe('the closed surface', () => {
    it('issues exactly twelve requests for its twelve members, at exactly these addresses', () => {
      // The collective assertion this whole file has been building toward. Every member
      // is called once, WITHOUT flushing, so that every request it produced is open at
      // the same moment and can be enumerated together. The enumeration is what makes
      // the closure explicit: a thirteenth member, a duplicated call, a retried request
      // or a second call smuggled inside one member would all change this list.
      //
      // Several plausible routes DO NOT EXIST and none is invented here: there is no
      // key/value settings accessor, no site-wizard operation, no portal-template
      // operation, no arbitrary-SQL execution, no expired-portal listing, no bulk
      // delete, no recycle-bin route, no filesystem or upload route, no permission
      // mutation, no cache invalidation and no users-online route. The page collection
      // beneath a portal is absent too — that route is served by the page controller and
      // belongs to `tab.service.ts`, whose own specification asserts it; requesting it
      // from here would make two services own the same rows. The health probe is
      // likewise unreachable: it is mapped at the host root, outside the versioned
      // prefix, so no member of a versioned resource service can address it.
      const observations = [
        observe(service.list({})),
        observe(service.getById(PORTAL_ID)),
        observe(service.create(createPortalRequest())),
        observe(service.update(PORTAL_ID, updatePortalRequest())),
        observe(service.delete(PORTAL_ID)),
        observe(service.getSettings(PORTAL_ID)),
        observe(service.updateSettings(PORTAL_ID, updatePortalSettingsRequest())),
        observe(service.listAliases(PORTAL_ID)),
        observe(service.createAlias(PORTAL_ID, createPortalAliasRequest())),
        observe(service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID)),
        observe(service.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, updatePortalAliasRequest())),
        observe(service.deleteAlias(PORTAL_ID, PORTAL_ALIAS_ID)),
      ];

      const issued = httpMock.match(() => true);

      expect(issued.length)
        .withContext('one request per member, and no member issues two')
        .toBe(12);
      expect(issued.map((request) => `${request.request.method} ${request.request.urlWithParams}`))
        .withContext('every address, in call order, including the absence of any query string')
        .toEqual([
          `GET ${PORTALS_URL}`,
          `GET ${PORTAL_URL}`,
          `POST ${PORTALS_URL}`,
          `PUT ${PORTAL_URL}`,
          `DELETE ${PORTAL_URL}`,
          `GET ${PORTAL_SETTINGS_URL}`,
          `PUT ${PORTAL_SETTINGS_URL}`,
          `GET ${PORTAL_ALIASES_URL}`,
          `POST ${PORTAL_ALIASES_URL}`,
          `GET ${PORTAL_ALIAS_URL}`,
          `PUT ${PORTAL_ALIAS_URL}`,
          `DELETE ${PORTAL_ALIAS_URL}`,
        ]);

      // Every address sits under the versioned prefix and every one is RELATIVE. An
      // absolute base would type-check, lint, bundle and deploy without complaint, and
      // would leave both containers reporting healthy while every call from a browser
      // failed — so it is asserted here rather than assumed.
      for (const request of issued) {
        expect(request.request.url.startsWith('/api/v1/'))
          .withContext(`${request.request.url} is relative and versioned`)
          .toBeTrue();
      }

      issued[0].flush(portalListBody([], 0, DEFAULT_PAGE_SIZE, 0));
      issued[1].flush(envelope(portalDetail(PORTAL_ID)));
      issued[2].flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      issued[3].flush(envelope(portalDetail(PORTAL_ID)), { status: 200, statusText: 'OK' });
      issued[4].flush(null, { status: 204, statusText: 'No Content' });
      issued[5].flush(envelope(portalSettings(PORTAL_ID)));
      issued[6].flush(envelope(portalSettings(PORTAL_ID)), { status: 200, statusText: 'OK' });
      issued[7].flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));
      issued[8].flush(envelope(portalAlias(PORTAL_ID, 9, 'contoso.example.test')), {
        status: 201,
        statusText: 'Created',
      });
      issued[9].flush(envelope(portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')));
      issued[10].flush(null, { status: 200, statusText: 'OK' });
      issued[11].flush(null, { status: 204, statusText: 'No Content' });

      for (const observed of observations) {
        expect(observed.isComplete())
          .withContext('every member completes on its success response')
          .toBeTrue();
        expect(observed.failures).toEqual([]);
      }
    });
  });
});
