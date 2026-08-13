/**
 * Specification for {@link PortalService} — the typed transport over the portal resource, its settings
 * projection and its host-name aliases. The legacy tree carries ZERO automated tests of any kind, so
 * every assertion below is net-new coverage rather than a port of an existing one.
 */

import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { BannerAdvertisingMode, UserRegistrationMode } from '../models/portal.model';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';
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

/** The identifier used for every single-portal case. */
const PORTAL_ID = 3;

/** One portal. */
const PORTAL_URL = '/api/v1/portals/3';

/** One portal's settings projection. */
const PORTAL_SETTINGS_URL = '/api/v1/portals/3/settings';

/** One portal's alias collection. */
const PORTAL_ADMINISTRATORS_URL = '/api/v1/portals/3/administrators';

const PORTAL_ALIASES_URL = '/api/v1/portals/3/aliases';

/** The identifier used for every single-alias case. */
const PORTAL_ALIAS_ID = 7;

/** One alias of one portal. */
const PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/7';

const LEGACY_ALIAS_QUERY_KEY = 'paid';

// ---------------------------------------------------------------------------
// The wire query-parameter names, spelled out.
// ---------------------------------------------------------------------------

/**
 * The parameter names a collection request may carry, as literals. Spelled out here rather than imported
 * from the query-string helper for the same reason the URLs are: a specification that reads the names
 * back from the module that emits them cannot detect a rename, because both sides would move together.
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

/** The shape of an RFC 7807 failure document, declared LOCALLY and minimally. */
interface FailureDocument {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly traceId: string;
  readonly correlationId: string;
}

/**
 * The W3C trace identifier and the pipeline-validated correlation identifier. The API attaches BOTH to
 * every problem document, so a fixture omitting them describes a response it does not send.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

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
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * Recovers the failure code from a document that reached the caller.
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
 * @param portalId The identifier the row carries.
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
 * One portal in full. Every member the contract declares is present, including the ones holding a legacy
 * sentinel, because the API serialises with its ignore condition set to never: a member with no value
 * travels as its sentinel or as `null`, and never goes missing.
 *
 * @param portalId The identifier.
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

    // The opaque revision marker every portal read publishes. A fixture carries one because a real
    // read does, and because a screen that round-trips it must have something to round-trip.
    concurrencyToken: 'revision-1',
  };
}

/**
 * One portal's settings projection. ⚠️ There is NO settings TABLE behind this contract. Portal
 * configuration lives as COLUMNS on the portal row, so this is a projection of that row and not a bag of
 * key/value pairs — which is why the service publishes no `getSetting(key)`, `setSetting(key, value)` or
 * per-key patch member, and why nothing here asserts one.
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

    // Deliberately the SAME token the detail fixture publishes: one server-side derivation serves both
    // reads, so the two projections of an unchanged record agree and either token satisfies either write.
    concurrencyToken: 'revision-1',
  };
}

/**
 * One host-name alias.
 *
 * @param portalId The portal the alias resolves to.
 * @param portalAliasId The alias identifier.
 * @param httpAlias The host name, optionally with a port.
 * @param isCurrent Whether the request that read the row resolved the tenant through it.
 * @returns The alias.
 */
function portalAlias(
  portalId: number,
  portalAliasId: number,
  httpAlias: string,
  isCurrent = false,
): PortalAlias {
  return { portalAliasId, portalId, httpAlias, isCurrent };
}

/**
 * Wraps a single record in the success envelope the API writes.
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

    // Round-tripped from the read, which is what makes a stale save refusable.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * A settings-update request.
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

/** An alias-creation request. */
function createPortalAliasRequest(httpAlias = 'contoso.example.test'): CreatePortalAliasRequest {
  return { httpAlias };
}

/** An alias-update request. */
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

  /** The failures the caller received. */
  readonly failures: readonly unknown[];

  /** Whether the call completed. */
  isComplete(): boolean;
}

/**
 * Subscribes immediately and records the outcome. Subscribing is what ISSUES the request — the service
 * returns a cold observable and nothing in it starts a call — so every test below observes first and only
 * then expects a request.
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
      // The real client with NO interceptor chain. This specification is about the service's own
      // contribution to a request, and running the interceptors here would mean asserting two units at once
      // and would attach headers this file has no business claiming.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(PortalService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('the service itself', () => {
    it('resolves from the root injector as a single instance', () => {
      expect(service).toBeTruthy();
      // Declared `providedIn: 'root'`, so two resolutions are the same object. A per-injector service would
      // give each caller its own, which for a stateless transport would be harmless but would also mean the
      // declaration was not doing what it says.
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

      // A predicate matcher, because this call DOES carry a query string and a string matcher would have to
      // spell out the serialised parameters — which would assert encoding order as though it were contract.
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
      // The third page is index 2. Neither the service nor the query-string helper may add or subtract one:
      // the one-based counter survives only inside the shared pagination component, and the mapping between
      // the two bases happens in the feature store.
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

      expect(observed.values).toEqual([detail]);
      expect(observed.isComplete()).toBeTrue();
    });

    it('addresses portal 0 and portal -1, because both are real portals', () => {
      // ⚠️ TWO FACTS COLLIDE, and a guard on either one drops a legitimate request.
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
      // The refusal is reported by the server when the requested host name already resolves to a portal.
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
      // The zero-valued enumeration member is the mirror image of the same hazard. Its first member is
      // deliberately 0, so a serialiser that treated falsy as absent would drop the very setting the caller
      // was changing.
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
      // TWENTY-EIGHT: the twenty-seven legacy members plus the revision marker, which describes no portal
      // attribute and lands on no column. The point of this count is unchanged - NOTHING is pruned for
      // looking empty - and the marker is the one deliberate addition to the set.
      expect(Object.keys(body).length)
        .withContext('all twenty-seven members travel plus the revision marker, none pruned for looking empty')
        .toBe(28);

      pending.flush(envelope(portalDetail(PORTAL_ID)), { status: 200, statusText: 'OK' });

      expect(observed.isComplete()).toBeTrue();
    });

    it('distinguishes a user quota of zero from a user quota of minus one', () => {
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
      const emittedCode = 'portal.last_remaining';
      const document = failureDocument(
        emittedCode,
        409,
        'Conflict',
        'You Can Not Delete The Last Portal In Your Database',
      );

      const observed = observe(service.delete(PORTAL_ID));

      const pending = httpMock.expectOne(PORTAL_URL);
      pending.flush(document, { status: 409, statusText: 'Conflict' });

      const failure = soleFailure(observed);
      expect(failure.status)
        .withContext(
          'the reason token is `last_remaining`, which the server maps to 409 — the removal ' +
            'is refused by the STATE of the installation, not by anything wrong with the request',
        )
        .toBe(409);
      expect(failure.error as unknown)
        .withContext('the document arrives exactly as written')
        .toEqual(document);

      const recovered = failureCodeOf(failure.error);
      expect(recovered)
        .withContext('the whole dotted identifier survives as one string')
        .toBe(emittedCode);
      expect(recovered)
        .withContext('and is not reduced to the part after the dot')
        .not.toBe('last_remaining');
      expect(recovered?.includes('.'))
        .withContext('the dot is part of the identifier, not a separator that was consumed')
        .toBeTrue();
      expect(recovered?.length).toBe(emittedCode.length);
    });

    it('carries the refusal as a COMPLETE document, extensions included', () => {
      // Every member below is present on every refusal this API emits: `ApiResults.Problem` supplies the
      // type, status and detail, and `ValidationProblemDetailsFactory` then fills the title from its
      // per-status vocabulary and attaches the trace and correlation identifiers.
      const document = failureDocument(
        'portal.last_remaining',
        409,
        'Conflict',
        'You Can Not Delete The Last Portal In Your Database',
      );

      const observed = observe(service.delete(PORTAL_ID));

      httpMock.expectOne(PORTAL_URL).flush(document, { status: 409, statusText: 'Conflict' });

      const body = soleFailure(observed).error as FailureDocument;

      expect(body.type).toBe('urn:dnnmigration:error:portal.last_remaining');
      expect(body.title)
        .withContext('the title names the class of failure and is derived from the status alone')
        .toBe('Conflict');
      expect(body.status)
        .withContext('the body agrees with the transport, as a real response does')
        .toBe(409);
      expect(body.detail.length).toBeGreaterThan(0);
      expect(body.traceId).toBe(TRACE_ID);
      expect(body.correlationId).toBe(CORRELATION_ID);
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
        .withContext('the update contract without its identifier, plus the revision marker: twenty-seven members')
        .toBe(27);
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
      // ⚠️ THE ONLY PAGED MEMBER OF THIS SERVICE IS THE PORTAL LISTING. A portal has a handful of host
      // names, so paging them would add coordinates a caller would then have to thread through for no
      // benefit — and a pager that silently truncated an alias list would hide the alias a tenant is
      // failing to resolve on.
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
      // The tenant-resolution consequence of getting this wrong is why the server refuses rather than
      // accepting a second identical host name: an alias identifies a tenant, so two portals answering to
      // one host name would make resolution depend on row order.
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
      expectNoQueryString(pending, 'reading one portal alias');

      pending.flush(envelope(alias));

      expect(observed.values).toEqual([alias]);
      expect(observed.isComplete()).toBeTrue();
    });
  });

  describe('updateAlias', () => {
    it('puts the alias to its own address and unwraps the stored row from the 200', () => {
      const request = updatePortalAliasRequest();
      const stored = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'www.contoso.example.test');

      const observed = observe(service.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, request));

      const pending = httpMock.expectOne(PORTAL_ALIAS_URL);
      expect(pending.request.method).toBe('PUT');
      expectNoQueryString(pending, 'updating one portal alias');
      expect(pending.request.body)
        .withContext('the body is the request the caller composed')
        .toEqual(request);
      expect(pending.request.responseType)
        .withContext('the response is JSON, and it is decoded rather than trusted')
        .toBe('json');

      pending.flush(envelope(stored));

      expect(observed.isComplete()).toBeTrue();
      expect(observed.failures).toEqual([]);
      expect(observed.values)
        .withContext('the stored row, unwrapped from the envelope')
        .toEqual([stored]);
    });

    it('lets a duplicate-alias refusal reach the caller as the 409 it is', () => {
      // `portal.alias_duplicate` carries the `duplicate` token, which the server's mapping classifies as a
      // conflict: the request is well formed and the STATE of the tenant declines it, so renaming the alias
      // makes the identical request succeed.
      const document = failureDocument(
        'portal.alias_duplicate',
        409,
        'Conflict',
        'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
      );

      const observed = observe(
        service.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, updatePortalAliasRequest()),
      );

      httpMock
        .expectOne(PORTAL_ALIAS_URL)
        .flush(document, { status: 409, statusText: 'Conflict' });

      const failure = soleFailure(observed);
      expect(failure.status).toBe(409);
      expect(failureCodeOf(failure.error)).toBe('portal.alias_duplicate');
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
    it('issues exactly thirteen requests for its thirteen members, at exactly these addresses', () => {
      const observations = [
        observe(service.list({})),
        observe(service.getById(PORTAL_ID)),
        observe(service.create(createPortalRequest())),
        observe(service.update(PORTAL_ID, updatePortalRequest())),
        observe(service.delete(PORTAL_ID)),
        observe(service.getSettings(PORTAL_ID)),
        observe(service.updateSettings(PORTAL_ID, updatePortalSettingsRequest())),
        observe(service.listAdministrators(PORTAL_ID)),
        observe(service.listAliases(PORTAL_ID)),
        observe(service.createAlias(PORTAL_ID, createPortalAliasRequest())),
        observe(service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID)),
        observe(service.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, updatePortalAliasRequest())),
        observe(service.deleteAlias(PORTAL_ID, PORTAL_ALIAS_ID)),
      ];

      const issued = httpMock.match(() => true);

      expect(issued.length)
        .withContext('one request per member, and no member issues two')
        .toBe(13);
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
          `GET ${PORTAL_ADMINISTRATORS_URL}`,
          `GET ${PORTAL_ALIASES_URL}`,
          `POST ${PORTAL_ALIASES_URL}`,
          `GET ${PORTAL_ALIAS_URL}`,
          `PUT ${PORTAL_ALIAS_URL}`,
          `DELETE ${PORTAL_ALIAS_URL}`,
        ]);

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
      issued[7].flush(
        envelope([{ userId: 11, username: 'host', displayName: 'Host Account' }]),
      );
      issued[8].flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));
      issued[9].flush(envelope(portalAlias(PORTAL_ID, 9, 'contoso.example.test')), {
        status: 201,
        statusText: 'Created',
      });
      issued[10].flush(envelope(portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')));
      issued[11].flush(envelope(portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'renamed.example.test')), {
        status: 200,
        statusText: 'OK',
      });
      issued[12].flush(null, { status: 204, statusText: 'No Content' });

      for (const observed of observations) {
        expect(observed.isComplete())
          .withContext('every member completes on its success response')
          .toBeTrue();
        expect(observed.failures).toEqual([]);
      }
    });
  });
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  describe('refuses a response that does not match its contract', () => {
    /**
     * Asserts that a pending request answered with `body` fails with a contract violation naming `path`.
     *
     * @param observed The observation of the call under test.
     * @param pending The request the call issued.
     * @param body The malformed body to answer with.
     * @param path The member path the violation must name.
     */
    function expectViolationAt(
      observed: Observed<unknown>,
      pending: TestRequest,
      body: object,
      path: string,
    ): void {
      pending.flush(body);

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);

      const failure: unknown = observed.failures[0];

      expect(isContractViolation(failure))
        .withContext('a contract violation, not an HTTP error and not a raw TypeError')
        .toBeTrue();

      if (isContractViolation(failure)) {
        expect(failure.path).toBe(path);
        expect(failure.received)
          .withContext('a type name, never the value')
          .not.toContain('Contoso');
      }
    }

    it('refuses a page with no metadata rather than reporting an empty first page', () => {
      const observed = observe<unknown>(service.list({}));

      expectViolationAt(observed, httpMock.expectOne(PORTALS_URL), { items: [] }, 'response.meta');
    });

    it('refuses a page whose items member is absent', () => {
      const observed = observe<unknown>(service.list({}));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTALS_URL),
        { meta: { totalCount: 0, pageIndex: 0, pageSize: DEFAULT_PAGE_SIZE, totalPages: 0 } },
        'response.items',
      );
    });

    it('refuses a listed row whose count arrived as text', () => {
      const observed = observe<unknown>(service.list({}));
      const malformed = { ...portalListItem(PORTAL_ID, 'Contoso'), users: '12' };

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTALS_URL),
        { ...portalListBody([], 0, DEFAULT_PAGE_SIZE, 1), items: [malformed] },
        'response.items[0].users',
      );
    });

    it('refuses a detail whose identifier is absent', () => {
      // Absence is refused rather than defaulted. `0` and `-1` are both real portal
      // identifiers here, so there is no value a missing identifier could safely become.
      const observed = observe<unknown>(service.getById(PORTAL_ID));
      const malformed: Record<string, unknown> = { ...portalDetail(PORTAL_ID) };

      delete malformed['portalId'];

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_URL),
        envelope(malformed),
        'response.data.portalId',
      );
    });

    it('refuses a registration mode outside the published code table', () => {
      const observed = observe<unknown>(service.getById(PORTAL_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_URL),
        envelope({ ...portalDetail(PORTAL_ID), userRegistration: 99 }),
        'response.data.userRegistration',
      );
    });

    // `PortalDetailDto.ConcurrencyToken` and `PortalSettingsDto.ConcurrencyToken` are both declared `public
    // string ... = string.Empty` and are both populated by `PortalMappings.ConcurrencyTokenFor`, so neither
    // read can legitimately omit the member or serve a null for it.

    it('refuses a detail whose revision marker is absent', () => {
      const observed = observe<unknown>(service.getById(PORTAL_ID));
      const malformed: Record<string, unknown> = { ...portalDetail(PORTAL_ID) };

      delete malformed['concurrencyToken'];

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_URL),
        envelope(malformed),
        'response.data.concurrencyToken',
      );
    });

    it('refuses a detail whose revision marker arrived as null', () => {
      const observed = observe<unknown>(service.getById(PORTAL_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_URL),
        envelope({ ...portalDetail(PORTAL_ID), concurrencyToken: null }),
        'response.data.concurrencyToken',
      );
    });

    it('refuses a settings projection whose revision marker is absent', () => {
      const observed = observe<unknown>(service.getSettings(PORTAL_ID));
      const malformed: Record<string, unknown> = { ...portalSettings(PORTAL_ID) };

      delete malformed['concurrencyToken'];

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_SETTINGS_URL),
        envelope(malformed),
        'response.data.concurrencyToken',
      );
    });

    it('refuses a settings projection whose revision marker arrived as null', () => {
      const observed = observe<unknown>(service.getSettings(PORTAL_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_SETTINGS_URL),
        envelope({ ...portalSettings(PORTAL_ID), concurrencyToken: null }),
        'response.data.concurrencyToken',
      );
    });

    it('accepts the empty string as a revision marker, because that is the server unset spelling', () => {
      // The counterpart case, and it matters: the member is non-nullable on the server and its declared
      // default IS the empty string, so an installation that has never derived a token serves one.
      const observed = observe(service.getById(PORTAL_ID));

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope({ ...portalDetail(PORTAL_ID), concurrencyToken: '' }));

      expect(observed.failures).toEqual([]);
      expect(observed.values).toEqual([{ ...portalDetail(PORTAL_ID), concurrencyToken: '' }]);
    });

    it('refuses a detail whose envelope carries the payload at the top level', () => {
      const observed = observe<unknown>(service.getById(PORTAL_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_URL),
        portalDetail(PORTAL_ID),
        'response.data',
      );
    });

    it('refuses an alias list that is not an array', () => {
      const observed = observe<unknown>(service.listAliases(PORTAL_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_ALIASES_URL),
        envelope({ portalAliasId: PORTAL_ALIAS_ID }),
        'response.data',
      );
    });

    it('refuses an alias whose host name arrived as a number', () => {
      const observed = observe<unknown>(service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID));

      expectViolationAt(
        observed,
        httpMock.expectOne(PORTAL_ALIAS_URL),
        envelope({ ...portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost'), httpAlias: 8080 }),
        'response.data.httpAlias',
      );
    });

    it('accepts a null host name, because the read projection publishes it as nullable', () => {
      const observed = observe(service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID));

      httpMock
        .expectOne(PORTAL_ALIAS_URL)
        .flush(
          envelope({
            portalAliasId: PORTAL_ALIAS_ID,
            portalId: PORTAL_ID,
            httpAlias: null,
            isCurrent: false,
          }),
        );

      expect(observed.failures).toEqual([]);
      expect(observed.values).toEqual([
        { portalAliasId: PORTAL_ALIAS_ID, portalId: PORTAL_ID, httpAlias: null, isCurrent: false },
      ]);
    });

    it('ignores a member the server added that this client does not declare', () => {
      // Additive server changes must not turn every client into a blocker. What matters is
      // that everything this client READS is what this client declared.
      const observed = observe(service.getById(PORTAL_ID));

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope({ ...portalDetail(PORTAL_ID), someMemberAddedLater: 'ignored' }));

      expect(observed.failures).toEqual([]);
      expect(observed.values).toEqual([portalDetail(PORTAL_ID)]);
    });
  });

  // WHO ANNOUNCES A FAILURE
  // Every request is marked as presented by its caller, which is what stops one failure being shown twice —
  // once as the interceptor's transient notification and once as the caller's own in-page banner or
  // legacy-worded message. The interceptor still re-throws; it only stays silent.
  describe('marks every request as presented by its caller', () => {
    it('marks every one of the twelve operations', () => {
      service.list({}).subscribe({ error: () => undefined });
      service.getById(PORTAL_ID).subscribe({ error: () => undefined });
      service.create(createPortalRequest()).subscribe({ error: () => undefined });
      service.update(PORTAL_ID, updatePortalRequest()).subscribe({ error: () => undefined });
      service.delete(PORTAL_ID).subscribe({ error: () => undefined });
      service.getSettings(PORTAL_ID).subscribe({ error: () => undefined });
      service
        .updateSettings(PORTAL_ID, updatePortalSettingsRequest())
        .subscribe({ error: () => undefined });
      service.listAliases(PORTAL_ID).subscribe({ error: () => undefined });
      service
        .createAlias(PORTAL_ID, createPortalAliasRequest())
        .subscribe({ error: () => undefined });
      service.getAlias(PORTAL_ID, PORTAL_ALIAS_ID).subscribe({ error: () => undefined });
      service
        .updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, updatePortalAliasRequest())
        .subscribe({ error: () => undefined });
      service.deleteAlias(PORTAL_ID, PORTAL_ALIAS_ID).subscribe({ error: () => undefined });

      const issued: readonly TestRequest[] = httpMock.match(() => true);

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
