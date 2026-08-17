import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { BannerAdvertisingMode, UserRegistrationMode } from '../models/portal.model';
import { PortalStore } from './portal.store';

import type { TestRequest } from '@angular/common/http/testing';

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
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../models/problem-details.model';
import type { PortalFailure } from './portal.store';

import { HttpParams } from '@angular/common/http';

import type { HttpRequest } from '@angular/common/http';

/**
 * The filters a request carried, presented as ONE parameter bag whichever transport carried them. ⚠ A
 * LISTING READ THAT CARRIES A TERM A PERSON TYPED SENDS ITS FILTERS IN THE BODY, because a query string is
 * written into the reverse proxy's access log and into the API's own request log; a term-free read keeps
 * them in the query string. Specifications below are about WHAT was sent, not about WHERE, so they read
 * through here and stay true across both transports.
 *
 * @param request The request to read, or the raw request it wraps.
 * @returns Every filter it carried, as query-parameter-shaped strings.
 */
function sentFilters(request: TestRequest | HttpRequest<unknown>): HttpParams {
  const raw: HttpRequest<unknown> = 'request' in request ? request.request : request;
  const body = raw.body as Record<string, unknown> | null | undefined;

  if (body === null || body === undefined) {
    return raw.params;
  }

  let carried: HttpParams = new HttpParams();

  for (const [name, value] of Object.entries(body)) {
    if (value !== null && value !== undefined) {
      carried = carried.set(name, String(value));
    }
  }

  return carried;
}


/**
 * Whether a request is a listing read, on EITHER transport.
 *
 * @param candidate The request to test.
 * @returns True for the term-free read and for the body-bound search alike.
 */
function isListingRead(candidate: HttpRequest<unknown>): boolean {
  return candidate.url === PORTALS_URL || candidate.url === PORTALS_SEARCH_URL;
}


// ---------------------------------------------------------------------------
// THE ADDRESSES UNDER TEST, AS RELATIVE LITERALS.
// ---------------------------------------------------------------------------

/** The portal collection. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The body-bound search address. ⚠ A SEPARATE ADDRESS FROM {@link PORTALS_URL} ON PURPOSE: a listing read that
 * carries a term a person typed goes here, so the term never appears in a logged request line.
 */
const PORTALS_SEARCH_URL = '/api/v1/portals/search';

/**
 * The identifier of the FIRST portal any legacy installation ever created. `01.00.00.SqlDataProvider:L77`
 * seeds the identity at minus one, and `Null.vb:L41-L45` defines the absent-integer marker as the same
 * number.
 */
const SEEDED_PORTAL_ID = -1;

/** One portal, addressed by the seeded identity. The highest-value address in the file. */
const SEEDED_PORTAL_URL = '/api/v1/portals/-1';

/** The identifier of the SECOND portal, which the same identity seed makes nought. */
const SECOND_PORTAL_ID = 0;

/** One portal, addressed by nought. */
const SECOND_PORTAL_URL = '/api/v1/portals/0';

/** A plainly ordinary identifier, used wherever the value itself is beside the point. */
const PORTAL_ID = 3;

/** One portal, addressed ordinarily. */
const PORTAL_URL = '/api/v1/portals/3';

/** One portal's settings projection. */
const PORTAL_SETTINGS_URL = '/api/v1/portals/3/settings';

/** One portal's host-name alias collection. */
const PORTAL_ALIASES_URL = '/api/v1/portals/3/aliases';

/** The identifier used for every single-alias case. */
const PORTAL_ALIAS_ID = 7;

/** One alias of one portal. */
const PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/7';

/** A second alias, for the collection-mutation cases. */
const OTHER_PORTAL_ALIAS_ID = 8;

/** The second alias of one portal. */
const OTHER_PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/8';

// ---------------------------------------------------------------------------
// THE WIRE PARAMETER NAMES, SPELLED OUT.
// ---------------------------------------------------------------------------

/** The parameter names a portal-listing request may carry. */
const QUERY_KEY = {
  pageIndex: 'pageIndex',
  pageSize: 'pageSize',
  sortBy: 'sortBy',
  sortDir: 'sortDir',
  query: 'query',
  name: 'name',
} as const;

const LEGACY_URL_KEY = {
  filter: 'filter',
  currentPage: 'currentpage',
  aliasId: 'paid',
} as const;

// ---------------------------------------------------------------------------
// SENTINEL VALUES, NAMED.
// ---------------------------------------------------------------------------

/** A quota of nought: UNLIMITED. The provisioning path seeded this. */
const QUOTA_UNLIMITED = 0;

/** A quota of minus one: NOT SET. The read path produced this from a database null. */
const QUOTA_NOT_SET = -1;

/**
 * A page reference holding minus one: NO SUCH PAGE IS CONFIGURED. Six members of the portal contract are
 * page references and each admits this value.
 */
const NO_SUCH_PAGE = -1;

/** The prefix the API wraps a reason code in when it writes a failure document. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * The refusal answered when the sole surviving portal is asked to be removed. ONE dotted string, not a
 * path into a nested structure, and never split at its full stop.
 */
const LAST_PORTAL_REFUSAL = 'portal.last_remaining';

/**
 * The refusal answered when a host name is already bound. Both legacy alias keys collapse onto this one
 * code, because the server reports one code for the collision however it was reached.
 */
const DUPLICATE_ALIAS_REFUSAL = 'portal.alias_duplicate';

/** The distributed-trace identifier a problem document carries. */
const TRACE_ID = '00-8f4b2c1d9e6a47f3b5c8d1e2f3a4b5c6-1a2b3c4d5e6f7a8b-01';

/** The correlation identifier a problem document carries. */
const CORRELATION_ID = 'b7f3d2a1-4c5e-4a9b-8d6f-2e3c4a5b6d7e';

/**
 * One row of the portal listing.
 *
 * @param portalId The identifier the row carries.
 * @param portalName The display name.
 * @param overrides Members to replace, for the fidelity cases.
 * @returns The row.
 */
function portalListItem(
  portalId: number,
  portalName: string,
  overrides: Partial<PortalListItem> = {},
): PortalListItem {
  return {
    portalId,
    portalName,
    aliases: ['localhost'],
    users: 3,
    pages: 12,
    hostSpace: 0,
    hostFee: 0,
    expiryDate: null,
    ...overrides,
  };
}

/**
 * A page of portal rows, in the envelope the collection endpoint actually writes. ⚠️ THE PAGING FACTS ARE
 * NESTED UNDER `meta`; they are not siblings of `items`.
 *
 * @param items The rows on the page.
 * @param pageIndex The zero-based index of the page these rows came from.
 * @param pageSize The size of the page the server served.
 * @param totalCount The size of the whole match set.
 * @param totalPages The page count the server reported.
 * @returns The body to flush.
 */
function portalPage(
  items: readonly PortalListItem[],
  pageIndex: number,
  pageSize: number,
  totalCount: number,
  totalPages: number,
): PagedResponse<PortalListItem> {
  return { items, meta: { totalCount, pageIndex, pageSize, totalPages } };
}

/**
 * A single-row first page, for the many specifications whose subject is the request rather than the
 * response.
 *
 * @param portalId The identifier of the row.
 * @returns The body to flush.
 */
function singleRowPage(portalId: number): PagedResponse<PortalListItem> {
  return portalPage([portalListItem(portalId, 'Baseline Portal')], 0, 10, 1, 1);
}

/**
 * One portal in full. EVERY member the contract declares is present, including the ones holding a legacy
 * sentinel, because the API serialises with its ignore condition set never to elide a written member: a
 * member with no value travels as its sentinel or as an explicit null and never goes missing.
 *
 * @param portalId The identifier.
 * @param overrides Members to replace, for the sentinel cases.
 * @returns The record.
 */
function portalDetail(portalId: number, overrides: Partial<PortalDetail> = {}): PortalDetail {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: '',
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
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
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
    superTabId: NO_SUCH_PAGE,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    // Nought is a meaningful offset rather than a missing value, so this member is
    // subject to the same prohibition on truthiness tests as the identifiers.
    timeZoneOffset: 0,
    homeDirectory: 'Portals/0',
    aliases: null,

    // The opaque revision marker every portal read publishes, carried because a real read carries one.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * One portal's settings projection. ⚠️ There is NO settings TABLE behind this contract; see migration
 * note 8. This is a projection of columns on the portal row, which is why no factory here produces a
 * key/value pair, a dictionary or an entry list, and why nothing below asserts one.
 *
 * @param portalId The portal the projection belongs to.
 * @param overrides Members to replace.
 * @returns The projection.
 */
function portalSettings(
  portalId: number,
  overrides: Partial<PortalSettings> = {},
): PortalSettings {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: '',
    logoFile: 'logo.gif',
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    timeZoneOffset: 0,
    homeDirectory: 'Portals/0',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',

    // Deliberately the SAME token the detail fixture publishes: one server-side derivation serves both
    // reads, so the two projections of an unchanged record agree.
    concurrencyToken: 'revision-1',
    ...overrides,
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
// REQUEST FIXTURES.
// ---------------------------------------------------------------------------

/**
 * A portal-creation request, carrying every member the contract declares. Replaces a FIFTEEN-parameter
 * positional call; see migration note 6.
 *
 * @param overrides Members to replace.
 * @returns The request body.
 */
function createPortalRequest(overrides: Partial<CreatePortalRequest> = {}): CreatePortalRequest {
  return {
    portalName: 'Contoso',
    portalAlias: 'contoso.example.test',
    description: 'A tenant for the composition cases.',
    keyWords: 'contoso',
    homeDirectory: 'Portals/1',
    templateFile: 'Default Website.template',
    // A false flag is DATA on this contract and travels as itself. The legacy contract
    // could not make that distinction; this one declares the member non-nullable.
    isChildPortal: false,
    administratorFirstName: 'Ada',
    administratorLastName: 'Lovelace',
    administratorUsername: 'ada',
    administratorPassword: 'not-a-real-credential',
    administratorEmail: 'ada@example.test',
    ...overrides,
  };
}

/**
 * A portal-update request, carrying every member the contract declares.
 *
 * @param overrides Members to replace, for the sentinel cases.
 * @returns The request body.
 */
function updatePortalRequest(overrides: Partial<UpdatePortalRequest> = {}): UpdatePortalRequest {
  return {
    portalId: PORTAL_ID,
    portalName: 'Contoso',
    logoFile: 'logo.gif',
    footerText: '',
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
    paymentProcessor: null,
    processorUserId: null,
    processorCredentialReference: null,
    description: 'A tenant for the sentinel cases.',
    keyWords: 'contoso',
    backgroundFile: null,
    siteLogHistory: -1,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    timeZoneOffset: 0,
    homeDirectory: 'Portals/1',

    // Round-tripped from the read, which is what makes a stale save refusable.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * A settings-replacement request.
 *
 * @param overrides Members to replace.
 * @returns The request body.
 */
function updatePortalSettingsRequest(
  overrides: Partial<UpdatePortalSettingsRequest> = {},
): UpdatePortalSettingsRequest {
  const { portalId: identifierInPath, ...withoutIdentifier } = updatePortalRequest();
  void identifierInPath;

  return { ...withoutIdentifier, ...overrides };
}

/**
 * A host-name binding request.
 *
 * @param httpAlias The host name to bind.
 * @returns The request body.
 */
function createAliasRequest(httpAlias: string): CreatePortalAliasRequest {
  return { httpAlias };
}

/**
 * A host-name replacement request.
 *
 * @param httpAlias The host name to store in place of the current one.
 * @returns The request body.
 */
function updateAliasRequest(httpAlias: string): UpdatePortalAliasRequest {
  return { httpAlias };
}

/**
 * Builds the RFC 7807 document the API writes for a reason-coded failure.
 *
 * @param code The reason code, dotted, as the server spells it.
 * @param status The status the document accompanies.
 * @param detail The explanatory sentence, which is UNTRUSTED text — see the group on message text.
 * @returns The document to flush.
 */
function reasonedProblem(code: string, status: number, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: 'Request refused',
    status,
    detail,
    instance: PORTALS_URL,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * Builds a document carrying no reason code, which is what a plain fault looks like.
 *
 * @param status The status the document accompanies.
 * @param detail The explanatory sentence.
 * @param traceId The trace identifier to carry.
 * @returns The document to flush.
 */
function plainProblem(status: number, detail: string, traceId: string): ProblemDetails {
  return {
    type: 'about:blank',
    title: 'An unexpected fault occurred',
    status,
    detail,
    instance: PORTALS_URL,
    traceId,
  };
}

/**
 * Builds the per-field failure document the model-state factory writes. ⚠️ The dictionary is an index
 * signature and this workspace enables the compiler option that forbids reading one through dot access,
 * so every assertion against it below uses BRACKET access.
 *
 * @param detail The explanatory sentence.
 * @param reported The per-field messages, keyed as the server keys them.
 * @returns The document to flush.
 */
function validationProblem(
  detail: string,
  reported: Readonly<Record<string, readonly string[]>>,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.failed`,
    title: 'One or more validation failures occurred',
    status: 400,
    detail,
    instance: PORTALS_URL,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
    errors: reported,
  };
}

/**
 * Reads a query parameter that the request under assertion is required to carry.
 *
 * @param request The observed request.
 * @param key The wire parameter name.
 * @returns The transmitted value, as transmitted.
 */
function queryValue(request: TestRequest, key: string): string {
  const held: string | null = sentFilters(request).get(key);

  if (held === null) {
    throw new Error(`The request carried no ${key} parameter; the addressed URL was ${request.request.urlWithParams}.`);
  }

  return held;
}

/**
 * Narrows a failure slice that a specification has arranged to be populated.
 *
 * @param held The slice as read.
 * @param concern What the slice describes, for the failure message.
 * @returns The classified failure.
 */
function heldFailure(held: PortalFailure | null, concern: string): PortalFailure {
  if (held === null) {
    throw new Error(`The ${concern} failure slice held null; the specification expected a classified failure.`);
  }

  return held;
}

/**
 * Narrows the problem document inside a classified failure.
 *
 * @param failure The classified failure.
 * @returns The document as received.
 */
function heldProblem(failure: PortalFailure): ProblemDetails {
  const document: ProblemDetails | null = failure.problem;

  if (document === null) {
    throw new Error('The classified failure carried no problem document.');
  }

  return document;
}

/**
 * Narrows the per-field document inside a classified failure.
 *
 * @param failure The classified failure.
 * @returns The narrowed document, whose dictionary is present by construction.
 */
function heldValidation(failure: PortalFailure): ValidationProblemDetails {
  const document: ValidationProblemDetails | null = failure.validation;

  if (document === null) {
    throw new Error('The classified failure was not narrowed to a per-field document.');
  }

  return document;
}

/**
 * Narrows a slice that holds a record once a read has completed.
 *
 * @param held The slice as read.
 * @param concern What the slice describes, for the failure message.
 * @returns The record.
 * @typeParam T The record contract.
 */
function heldRecord<T>(held: T | null, concern: string): T {
  if (held === null) {
    throw new Error(`The ${concern} slice held null; the specification expected a record.`);
  }

  return held;
}

/**
 * Narrows the alias collection, which is null until it has been read. The three states of that slice are
 * load-bearing and the first two must not be collapsed: null means the collection was never read and says
 * nothing about how many aliases exist, an empty array means it was read and there are none, and a
 * populated array means it was read and these are they.
 *
 * @param held The slice as read.
 * @returns The collection.
 */
function heldAliases(held: readonly PortalAlias[] | null): readonly PortalAlias[] {
  if (held === null) {
    throw new Error('The alias collection slice held null; the specification expected a read collection.');
  }

  return held;
}

// ---------------------------------------------------------------------------
// STRUCTURAL PROBES.
// ---------------------------------------------------------------------------

/**
 * Enumerates the member names a store instance carries: its fields plus the methods on its prototype. ⚠️
 * HOW THIS IS AND IS NOT USED. Every probe built on it below asserts the ABSENCE of a member, never the
 * presence or the behaviour of one.
 *
 * @param target The instance to enumerate.
 * @returns Every own field name and every prototype method name, the constructor aside.
 */
function memberNames(target: object): readonly string[] {
  const prototype: object = Object.getPrototypeOf(target);
  const fields: readonly string[] = Object.keys(target);
  const methods: readonly string[] = Object.getOwnPropertyNames(prototype).filter(
    (name: string) => name !== 'constructor',
  );

  return [...fields, ...methods];
}

/**
 * Reports whether a value carries the two members a writable signal exposes. ⚠️ WHY PROPERTY PRESENCE AND
 * NOT AN ATTEMPTED WRITE. Attempting a write would need a type assertion to get past the read-only
 * declaration, and an assertion is forbidden here — it would also prove less, because it would
 * demonstrate that one particular cast fails rather than that the member is absent.
 *
 * @param candidate The signal to inspect.
 * @returns Which of the two mutating members the value carries.
 */
function mutatingMembers(candidate: object): { readonly setter: boolean; readonly updater: boolean } {
  return { setter: 'set' in candidate, updater: 'update' in candidate };
}

// ---------------------------------------------------------------------------
// REQUEST ASSERTIONS.
// ---------------------------------------------------------------------------

/**
 * Asserts that a request carried no query string whatsoever.
 *
 * @param request The observed request.
 * @param description What the request was, for the failure message.
 */
function expectNoQueryString(request: TestRequest, description: string): void {
  // ⚠ THIS HELPER READS THE REAL QUERY STRING AND MUST NOT GO THROUGH `sentFilters`. Its whole assertion is
  // about the ADDRESS, so a view that folds a request body into parameter shape would make every body-bearing
  // request appear to carry a query string and the assertion would invert its own meaning.
  expect(request.request.params.keys().length)
    .withContext(`${description}: carries no query parameter`)
    .toBe(0);
  expect(request.request.params.keys())
    .withContext(`${description}: parameter names`)
    .toEqual([]);
  expect(request.request.urlWithParams)
    .withContext(`${description}: the addressed URL is the bare path`)
    .toBe(request.request.url);
  expect(request.request.params.has(LEGACY_URL_KEY.aliasId))
    .withContext(`${description}: the legacy alias key is not sent`)
    .toBeFalse();
}

/**
 * Asserts that neither legacy listing key was transmitted.
 *
 * @param request The observed request.
 */
function expectNoLegacyListingKeys(request: TestRequest): void {
  expect(sentFilters(request).has(LEGACY_URL_KEY.filter))
    .withContext('the legacy lower-case filter key is not sent')
    .toBeFalse();
  expect(sentFilters(request).has(LEGACY_URL_KEY.currentPage))
    .withContext('the legacy lower-case one-based page key is not sent')
    .toBeFalse();
}

describe('PortalStore', () => {
  let store: PortalStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ⚠️ THE ORDER OF THESE TWO IS LOAD-BEARING. The real client registers a live backend; the testing
        // provider then replaces it. Listing them the other way round leaves the live backend in place, and
        // the specification silently starts issuing real requests to a host that is not there.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The store under test. Declared root-provided, and named here so that the instance under assertion
        // is created inside this test module rather than wherever a previous specification happened to
        // leave one.
        PortalStore,
      ],
    });

    store = TestBed.inject(PortalStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // Harness helpers, closing over the mock backend.
  // -------------------------------------------------------------------------

  /**
   * Takes the pending portal-listing request. Uses the PREDICATE overload against the bare path rather
   * than a string matcher, because a listing request always carries at least a page index and a string
   * matcher compares against the address WITH its query string — which would force this helper to spell
   * out a serialised query string and thereby assert encoding order as though it were contract.
   *
   * @param description What the request was, for the failure message.
   * @returns The pending request.
   */
  function expectListing(description: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => isListingRead(candidate),
      description,
    );
  }

  /**
   * Answers the listing re-read that follows a successful write.
   *
   * @param description What the re-read follows, for the failure message.
   */
  function settleListingReread(description: string): void {
    expectListing(description).flush(singleRowPage(PORTAL_ID));
  }

  /**
   * Asserts that a write issued NO listing read. ⚠ THE INVERSE OF THE HELPER ABOVE, AND THE CREATE AND
   * UPDATE COMMANDS MOVED FROM ONE TO THE OTHER. Both used to re-read the listing on success, and both
   * had exactly one caller that navigates TO the listing - which reads itself from its own address on
   * entry, unconditionally, so the store's read was a second read of the same page.
   *
   * @param description What the absence proves, quoted on failure.
   */
  function expectNoListingReread(description: string): void {
    expect(httpMock.match((candidate) => isListingRead(candidate)))
      .withContext(description)
      .toHaveSize(0);
  }

  /**
   * Reads a page of rows into the store and returns the rows as the store holds them.
   *
   * @param body The page to answer with.
   * @param description What the read was, for the failure message.
   */
  function readListing(body: PagedResponse<PortalListItem>, description: string): void {
    store.loadPortals();
    expectListing(description).flush(body);
  }

  // =========================================================================
  // THE STORE ITSELF
  // =========================================================================

  describe('the store itself', () => {
    it('resolves from the root injector as a single instance', () => {
      expect(store).toBeTruthy();
      expect(TestBed.inject(PortalStore))
        .withContext('one instance is shared by every consumer')
        .toBe(store);
    });

    it('seeds the first page with nothing selected, and issues no request until asked', () => {
      expect(store.pageIndex()).withContext('the first page is index nought').toBe(0);
      expect(store.requestedPageSize())
        .withContext('no size preference, so the server applies its own default')
        .toBeNull();
      expect(store.nameFilter()).withContext('no filter').toBeNull();
      expect(store.isFiltered()).toBeFalse();
      expect(store.sort())
        .withContext('no ordering preference, so the server orders as it chooses')
        .toEqual({ sortBy: null, sortDir: null });
      expect(store.portals()).withContext('no rows in hand').toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.totalPages()).toBe(0);
      expect(store.isListEmpty()).toBeTrue();
      expect(store.isPastEnd())
        .withContext('nothing matched at all is not the same as a page past the end')
        .toBeFalse();
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).withContext('the collection was never read').toBeNull();
      expect(store.aliasesLoaded()).toBeFalse();
      expect(store.aliasCount())
        .withContext('an unread collection reports no count, rather than a count of nought')
        .toBeNull();
      expect(store.selectedAliasId()).toBeUndefined();
      expect(store.selectedAlias()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasFailure()).toBeFalse();

      // No request is issued by construction, and `verify()` in `afterEach` is what asserts it: a store
      // that read eagerly on injection would leave a request outstanding here.
    });

    it('does nothing at all when asked to re-read a portal while nothing is selected', () => {
      expect(store.hasSelection()).toBeFalse();

      store.loadSelectedPortal();

      // Deliberately no expectation of a request. A store that guessed an identifier
      // here would ask for a portal nobody named, and `verify()` is the assertion.
      expect(store.detailLoading()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
    });

    it('returns every slice to its seeded value on reset, issuing no request', () => {
      readListing(
        portalPage([portalListItem(SEEDED_PORTAL_ID, 'First')], 0, 10, 1, 1),
        'the listing that reset will discard',
      );
      store.selectPortal(SEEDED_PORTAL_ID);
      expect(store.hasSelection()).toBeTrue();

      store.reset();

      expect(store.pageIndex()).toBe(0);
      expect(store.requestedPageSize()).toBeNull();
      expect(store.nameFilter()).toBeNull();
      expect(store.portals()).toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasFailure()).toBeFalse();
    });
  });

  // =========================================================================
  // LIST PAGING — the zero-based wire index, and the coordinates it answers with
  // =========================================================================

  describe('list paging', () => {
    it('sends a zero-based page index of nought for the first page, and never one', () => {
      store.loadPortals();

      const request = expectListing('the first page of the portal listing');

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('a relative address, reached through the reverse proxy on one origin')
        .toBe(PORTALS_URL);

      const transmitted: string = queryValue(request, QUERY_KEY.pageIndex);

      expect(transmitted).withContext('the first page addresses itself as nought').toBe('0');
      expect(transmitted)
        .withContext('the one-based screen value never reaches the wire')
        .not.toBe('1');
      expect(request.request.urlWithParams)
        .withContext('and it appears nowhere in the addressed URL either')
        .not.toContain('pageIndex=1');
      expectNoLegacyListingKeys(request);

      request.flush(singleRowPage(SEEDED_PORTAL_ID));

      expect(store.listLoading()).toBeFalse();
      expect(store.listFailure()).toBeNull();
    });

    it('sends two for the third page', () => {
      store.goToPage(2);

      const request = expectListing('the third page of the portal listing');

      expect(queryValue(request, QUERY_KEY.pageIndex))
        .withContext('index two addresses the third page and is transmitted as itself')
        .toBe('2');
      expect(store.pageIndex()).toBe(2);

      request.flush(portalPage([portalListItem(PORTAL_ID, 'Third page row')], 2, 10, 27, 3));

      expect(store.servedPageIndex()).toBe(2);
    });

    it('omits the page size when none was requested, and sends the requested size by name', () => {
      store.loadPortals();

      const unsized = expectListing('an unsized listing');

      expect(sentFilters(unsized).has(QUERY_KEY.pageSize))
        .withContext('an unsupplied size is an omission, so the server applies its own')
        .toBeFalse();
      unsized.flush(singleRowPage(PORTAL_ID));

      store.setPageSize(25);

      const sized = expectListing('a listing with an explicit size');

      expect(queryValue(sized, QUERY_KEY.pageSize)).toBe('25');
      expect(store.requestedPageSize()).toBe(25);
      sized.flush(portalPage([portalListItem(PORTAL_ID, 'Sized')], 0, 25, 1, 1));
    });

    it('retains the coordinates and the total the server reported, unchanged', () => {
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 2, 20, 137, 7),
        'a listing whose coordinates are the subject',
      );

      expect(store.totalCount()).withContext('the total, as reported').toBe(137);
      expect(store.servedPageIndex()).withContext('the served index, as reported').toBe(2);
      expect(store.servedPageSize()).withContext('the served size, as reported').toBe(20);
      expect(store.totalPages()).withContext('the page count, as reported').toBe(7);
      expect(store.isListEmpty()).toBeFalse();
      expect(store.listMeta()).toEqual({
        totalCount: 137,
        pageIndex: 2,
        pageSize: 20,
        totalPages: 7,
      });
    });

    it('returns to the first page when the page size changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));
      expect(store.pageIndex()).toBe(4);

      store.setPageSize(50);

      const request = expectListing('the listing re-read at the new size');

      expect(queryValue(request, QUERY_KEY.pageIndex))
        .withContext('a size change invalidates the page address, so the read starts again')
        .toBe('0');
      expect(store.pageIndex()).toBe(0);
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('returns to the first page when the filter changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));

      store.setNameFilter('Cont');

      const request = expectListing('the filtered listing');

      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      expect(queryValue(request, QUERY_KEY.name)).toBe('Cont');
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('returns to the first page when the ordering changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));

      store.setSort({ sortBy: 'portalName', sortDir: 'Descending' });

      const request = expectListing('the reordered listing');

      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('transmits the ordering direction under the server own capitalised member name', () => {
      store.setSort({ sortBy: 'portalName', sortDir: 'Ascending' });

      const request = expectListing('an ordered listing');

      expect(queryValue(request, QUERY_KEY.sortBy)).toBe('portalName');
      expect(queryValue(request, QUERY_KEY.sortDir)).toBe('Ascending');
      expect(store.sort()).toEqual({ sortBy: 'portalName', sortDir: 'Ascending' });
      request.flush(singleRowPage(PORTAL_ID));

      store.clearSort();

      const unordered = expectListing('the listing returned to the server own ordering');

      expect(sentFilters(unordered).has(QUERY_KEY.sortBy))
        .withContext('no preference is an omission, not a blank value')
        .toBeFalse();
      expect(sentFilters(unordered).has(QUERY_KEY.sortDir)).toBeFalse();
      unordered.flush(singleRowPage(PORTAL_ID));
    });

    it('sends the general free-text parameter for no listing request of its own accord', () => {
      store.setNameFilter('Cont');

      const request = expectListing('a name-filtered listing');

      expect(sentFilters(request).has(QUERY_KEY.query)).toBeFalse();
      expect(sentFilters(request).keys().length)
        .withContext('the page index and the name, and nothing else')
        .toBe(2);
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('reports the pager requirement from the served coordinates, and nothing more', () => {
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 3, 1),
        'a set that fits on one page',
      );
      expect(store.pagerRequired())
        .withContext('a total inside one page needs no pager')
        .toBeFalse();

      store.reloadPortals();
      expectListing('a set that overflows one page').flush(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 42, 5),
      );
      expect(store.pagerRequired()).toBeTrue();
    });

    it('distinguishes a page past the end of the set from a set that matched nothing', () => {
      readListing(portalPage([], 9, 10, 0, 0), 'a listing that matched nothing');
      expect(store.isListEmpty()).withContext('nothing matched at all').toBeTrue();
      expect(store.isPastEnd()).toBeFalse();

      store.reloadPortals();
      expectListing('a listing addressed past the end').flush(portalPage([], 9, 10, 12, 2));
      expect(store.isListEmpty())
        .withContext('records exist, so the set is not empty')
        .toBeFalse();
      expect(store.isPastEnd())
        .withContext('but the requested page holds none of them')
        .toBeTrue();
    });

    it('cancels a superseded listing read rather than letting a late answer land', () => {
      store.loadPortals();
      const abandoned = expectListing('the first read');

      store.goToPage(2);

      expect(abandoned.cancelled)
        .withContext('the superseded read is cancelled, so a slow answer cannot overwrite a fast one')
        .toBeTrue();

      const current = expectListing('the superseding read');

      expect(queryValue(current, QUERY_KEY.pageIndex)).toBe('2');
      current.flush(portalPage([portalListItem(PORTAL_ID, 'Row')], 2, 10, 27, 3));
      expect(store.servedPageIndex()).toBe(2);
    });
  });

  // =========================================================================
  // THE NAME FILTER — transmitted as typed, decorated by nobody on this side
  // =========================================================================

  describe('name filter', () => {
    it('transmits the operator text byte for byte, adding no pattern character', () => {
      store.setNameFilter('Cont');

      const request = expectListing('a filtered listing');

      const transmitted: string = queryValue(request, QUERY_KEY.name);

      expect(transmitted)
        .withContext('the text travels as typed; the pattern is the server to compose')
        .toBe('Cont');
      expect(transmitted).withContext('no wildcard is appended on this side').not.toContain('%');
      expect(transmitted.endsWith('%'))
        .withContext('nor is one trailing')
        .toBeFalse();
      expect(transmitted.startsWith('%'))
        .withContext('nor leading')
        .toBeFalse();
      expect(transmitted).withContext('and no underscore pattern character either').not.toContain('_');
      expectNoLegacyListingKeys(request);

      request.flush(singleRowPage(PORTAL_ID));

      expect(store.nameFilter())
        .withContext('and the slice holds the raw text, not a pattern')
        .toBe('Cont');
      expect(store.isFiltered()).toBeTrue();
    });

    it('preserves surrounding space and mixed case exactly as typed', () => {
      const typed = '  CoNtOsO  ';

      store.setNameFilter(typed);

      const request = expectListing('a listing filtered by untidy text');

      const transmitted: string = queryValue(request, QUERY_KEY.name);

      expect(transmitted).withContext('as typed, character for character').toBe(typed);
      expect(transmitted)
        .withContext('no trimming happens on this side; the server trims if it wishes')
        .not.toBe(typed.trim());
      expect(transmitted)
        .withContext('no case folding happens on this side either')
        .not.toBe(typed.toLowerCase());
      expect(transmitted).not.toContain('%');
      request.flush(portalPage([], 0, 10, 0, 0));
    });

    it('treats an empty filter as a value rather than as an absence', () => {
      store.setNameFilter('');

      const request = expectListing('a listing filtered by an empty value');

      expect(sentFilters(request).has(QUERY_KEY.name))
        .withContext('the parameter is present')
        .toBeTrue();
      expect(queryValue(request, QUERY_KEY.name))
        .withContext('holding the empty string, transmitted as itself')
        .toBe('');
      expect(store.nameFilter()).toBe('');
      expect(store.isFiltered())
        .withContext('an empty filter is a filter, and is distinct from no filter')
        .toBeTrue();
      request.flush(portalPage([], 0, 10, 0, 0));
    });

    it('drops the parameter altogether and returns to the first page when the filter is cleared', () => {
      store.setNameFilter('Cont');
      expectListing('a filtered listing').flush(singleRowPage(PORTAL_ID));
      store.goToPage(3);
      expectListing('a deep filtered page').flush(portalPage([], 3, 10, 4, 1));

      store.clearNameFilter();

      const request = expectListing('the unfiltered listing');

      expect(sentFilters(request).has(QUERY_KEY.name))
        .withContext('null is an omission, not a blank value')
        .toBeFalse();
      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      expect(store.nameFilter()).toBeNull();
      expect(store.isFiltered()).toBeFalse();
      request.flush(singleRowPage(PORTAL_ID));
    });
  });

  // =========================================================================
  // PORTAL IDENTIFIER SENTINELS — the highest-value group in the file
  // =========================================================================

  describe('portal identifier sentinels', () => {
    it('addresses the seeded portal, whose identifier is minus one, at its literal path', () => {
      store.loadPortal(SEEDED_PORTAL_ID);

      const request = httpMock.expectOne(SEEDED_PORTAL_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('the identifier is interpolated as itself, sign and all')
        .toBe('/api/v1/portals/-1');
      expectNoQueryString(request, 'a single-portal read');

      request.flush(envelope(portalDetail(SEEDED_PORTAL_ID)));

      expect(store.selectedPortalId())
        .withContext('minus one is retained as the selection, not read as an absence')
        .toBe(-1);
      expect(store.hasSelection()).toBeTrue();
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId)
        .withContext('and minus one survives the round trip on the record')
        .toBe(-1);
      expect(store.detailFailure()).toBeNull();
    });

    it('addresses the second portal, whose identifier is nought, at its literal path', () => {
      store.loadPortal(SECOND_PORTAL_ID);

      const request = httpMock.expectOne(SECOND_PORTAL_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('nought is a real identifier and is interpolated as itself')
        .toBe('/api/v1/portals/0');
      expectNoQueryString(request, 'a single-portal read');

      request.flush(envelope(portalDetail(SECOND_PORTAL_ID)));

      expect(store.selectedPortalId())
        .withContext('nought is retained as the selection, not read as an absence')
        .toBe(0);
      expect(store.hasSelection()).toBeTrue();
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId).toBe(0);
    });

    it('retains both extraordinary identifiers in one page, through every derived view', () => {
      readListing(
        portalPage(
          [
            portalListItem(SEEDED_PORTAL_ID, 'First portal'),
            portalListItem(SECOND_PORTAL_ID, 'Second portal'),
            portalListItem(PORTAL_ID, 'An ordinary portal'),
          ],
          0,
          10,
          3,
          1,
        ),
        'a page holding both extraordinary identifiers',
      );

      const rows: readonly PortalListItem[] = store.portals();

      expect(rows.length).withContext('all three rows survive').toBe(3);
      expect(rows.map((row: PortalListItem) => row.portalId))
        .withContext('in order, with neither sentinel-shaped identifier removed')
        .toEqual([-1, 0, 3]);
      expect(rows.map((row: PortalListItem) => row.portalName)).toEqual([
        'First portal',
        'Second portal',
        'An ordinary portal',
      ]);
      expect(store.totalCount()).toBe(3);
      expect(store.isListEmpty()).toBeFalse();
      expect(store.page().items).toBe(rows);
    });

    it('distinguishes a selection of nought from no selection at all', () => {
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();

      store.selectPortal(SECOND_PORTAL_ID);

      expect(store.selectedPortalId()).toBe(0);
      expect(store.selectedPortalId())
        .withContext('nought is a selection; the absence of one is a distinct undefined')
        .not.toBeUndefined();
      expect(store.hasSelection())
        .withContext('and the derived test is an explicit absence test, never a truthiness test')
        .toBeTrue();

      store.clearSelection();

      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();

      // Selecting issues no request; `verify()` asserts it.
    });

    it('distinguishes a selection of minus one from no selection at all', () => {
      store.selectPortal(SEEDED_PORTAL_ID);

      expect(store.selectedPortalId()).toBe(-1);
      expect(store.selectedPortalId())
        .withContext('minus one is a selection, notwithstanding being the legacy absence marker')
        .not.toBeUndefined();
      expect(store.hasSelection()).toBeTrue();

      store.clearSelection();

      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
    });

    it('retains page references holding minus one without conflating them with the identifier', () => {
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            adminTabId: 4,
            superTabId: NO_SUCH_PAGE,
            splashTabId: NO_SUCH_PAGE,
            homeTabId: NO_SUCH_PAGE,
            loginTabId: NO_SUCH_PAGE,
            userTabId: NO_SUCH_PAGE,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.portalId).withContext('the identifier is its own value').toBe(3);
      expect([
        held.superTabId,
        held.splashTabId,
        held.homeTabId,
        held.loginTabId,
        held.userTabId,
      ])
        .withContext('five unconfigured page references, each retained as minus one')
        .toEqual([-1, -1, -1, -1, -1]);
      expect(held.adminTabId)
        .withContext('and a configured reference is unaffected by its neighbours')
        .toBe(4);
      expect(store.selectedPortalId())
        .withContext('the selection tracks the portal and never a page reference')
        .toBe(3);
    });

    it('retains a page reference of nought, which names a real page', () => {
      store.loadPortal(SEEDED_PORTAL_ID);

      httpMock
        .expectOne(SEEDED_PORTAL_URL)
        .flush(envelope(portalDetail(SEEDED_PORTAL_ID, { homeTabId: 0, adminTabId: 0 })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      // The page identity seed is nought as well, so a page numbered nought is as real as
      // a portal numbered nought. Both appear on this one record.
      expect(held.homeTabId).withContext('page nought is a page').toBe(0);
      expect(held.adminTabId).toBe(0);
      expect(held.portalId).withContext('beside a portal identified as minus one').toBe(-1);
    });

    it('retains a zero-valued enumeration member on both enumerations', () => {
      // 15: the first member of each enumeration is a genuine choice, not an absence. The registration
      // enumeration was renamed from its legacy spelling and is declared in the portal model and nowhere
      // else.
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            userRegistration: UserRegistrationMode.NoRegistration,
            bannerAdvertising: BannerAdvertisingMode.None,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userRegistration).toBe(UserRegistrationMode.NoRegistration);
      expect(held.userRegistration).withContext('whose ordinal is nought').toBe(0);
      expect(held.bannerAdvertising).toBe(BannerAdvertisingMode.None);
      expect(held.bannerAdvertising).toBe(0);
      expect(held.userRegistration).not.toBeUndefined();
      expect(held.bannerAdvertising).not.toBeUndefined();
    });

    it('retains an empty string as a value and a nought offset as a measurement', () => {
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            footerText: '',
            keyWords: '',
            backgroundFile: null,
            timeZoneOffset: 0,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.footerText).withContext('an empty string is retained as itself').toBe('');
      expect(held.footerText)
        .withContext('and is not normalised into a null on the way in')
        .not.toBeNull();
      expect(held.keyWords).toBe('');
      expect(held.backgroundFile)
        .withContext('while a null that was sent as a null stays a null — the two are not merged')
        .toBeNull();
      expect(held.timeZoneOffset)
        .withContext('nought is a real offset — it is the United Kingdom')
        .toBe(0);
    });
  });

  // =========================================================================
  // USER QUOTA — two meanings on one column, never merged
  // =========================================================================

  describe('user quota', () => {
    it('retains a quota of nought, which means unlimited', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_UNLIMITED })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userQuota).withContext('nought is retained as nought').toBe(0);
      expect(held.userQuota).not.toBeNull();
      expect(held.userQuota).not.toBeUndefined();
    });

    it('retains a quota of minus one, which means not set', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_NOT_SET })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userQuota).withContext('minus one is retained as minus one').toBe(-1);
      expect(held.userQuota)
        .withContext('and is emphatically not folded onto the unlimited value')
        .not.toBe(QUOTA_UNLIMITED);
      expect(held.userQuota).not.toBeNull();
    });

    it('retains the two meanings independently when both appear on one record', () => {
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            userQuota: QUOTA_NOT_SET,
            pageQuota: QUOTA_UNLIMITED,
            hostSpace: QUOTA_UNLIMITED,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userQuota).withContext('accounts: not set').toBe(-1);
      expect(held.pageQuota).withContext('pages: unlimited').toBe(0);
      expect(held.hostSpace).withContext('disk space: unlimited').toBe(0);
      expect(held.userQuota).not.toBe(held.pageQuota);
    });

    it('carries both quota values onto the wire when a portal is written', () => {
      const composed = updatePortalRequest({
        userQuota: QUOTA_NOT_SET,
        pageQuota: QUOTA_UNLIMITED,
      });

      store.updatePortal(PORTAL_ID, composed);

      const written = httpMock.expectOne(PORTAL_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body)
        .withContext('the body reaches the transport unaltered, sentinels included')
        .toBe(composed);
      expect(composed.userQuota).toBe(-1);
      expect(composed.pageQuota).toBe(0);
      expect(Object.keys(written.request.body))
        .withContext('a member holding nought is present rather than elided as empty')
        .toContain('pageQuota');

      written.flush(
        envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_NOT_SET, pageQuota: QUOTA_UNLIMITED })),
      );
      expectNoListingReread('a replacement asks for no listing read; the listing reads itself on entry');
    });

    it('publishes no derived member that merges the two quota meanings', () => {
      const merged: readonly string[] = memberNames(store).filter((name: string) =>
        name.toLowerCase().includes('quota'),
      );

      expect(merged)
        .withContext('no effective, resolved or displayed quota member exists on the store')
        .toEqual([]);
    });
  });

  // =========================================================================
  // CREATE, UPDATE AND DELETE
  // =========================================================================

  describe('create, update and delete', () => {
    it('posts the creation request as a named contract and adopts the portal it receives', () => {
      const composed = createPortalRequest();
      const created: PortalDetail[] = [];

      store.createPortal(composed).subscribe((portal: PortalDetail) => {
        created.push(portal);
      });

      expect(store.detailLoading()).withContext('the write is in flight').toBeTrue();

      const posted = httpMock.expectOne(PORTALS_URL);

      expect(posted.request.method).toBe('POST');
      expectNoQueryString(posted, 'a portal creation');

      const transmitted: unknown = posted.request.body;

      expect(Array.isArray(transmitted))
        .withContext('a named contract, never a positional sequence')
        .toBeFalse();
      expect(transmitted)
        .withContext('and the body reaches the transport unaltered — nothing rebuilt, nothing stripped')
        .toBe(composed);

      const names: readonly string[] = Object.keys(posted.request.body);

      expect(names.length).withContext('every member the contract declares').toBe(12);
      expect(names)
        .withContext('including the false-valued flag, which is data rather than an absence')
        .toContain('isChildPortal');
      expect(composed.isChildPortal)
        .withContext('and it holds false — the legacy contract could not tell that from unset')
        .toBeFalse();

      posted.flush(envelope(portalDetail(SECOND_PORTAL_ID)), {
        status: 201,
        statusText: 'Created',
      });

      expect(store.selectedPortalId())
        .withContext('the created portal becomes the selection, identifier nought and all')
        .toBe(0);
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId).toBe(0);
      expect(store.settings())
        .withContext('and the previous portal scoped state is discarded rather than carried over')
        .toBeNull();
      expect(store.aliases()).toBeNull();
      expect(store.detailLoading()).toBeFalse();
      expect(created.length).withContext('the caller is notified once').toBe(1);
      expect(created[0].portalId).toBe(0);

      expectNoListingReread('a creation asks for no listing read; the listing reads itself on entry');
    });

    it('puts the update request unaltered, keeps what the server stored, and refreshes the listing', () => {
      const composed = updatePortalRequest({ portalId: PORTAL_ID, portalName: 'Renamed' });
      const stored: PortalDetail[] = [];

      store.updatePortal(PORTAL_ID, composed).subscribe((portal: PortalDetail) => {
        stored.push(portal);
      });

      const written = httpMock.expectOne(PORTAL_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body).toBe(composed);

      const names: readonly string[] = Object.keys(written.request.body);

      // MIGRATION 6: twenty-seven positional parameters became twenty-seven named members. The arity did
      // not shrink, which is the point — what changed is that a member is addressed by name, so a
      // transposed pair is no longer expressible.
      expect(names.length).withContext('every member the contract declares').toBe(28);
      expect(names)
        .withContext('the one addition to the legacy set, and the only non-attribute member')
        .toContain('concurrencyToken');
      expect(names).toContain('portalId');

      written.flush(envelope(portalDetail(PORTAL_ID, { portalName: 'Renamed' })));

      expect(heldRecord(store.selectedPortal(), 'selected portal').portalName)
        .withContext('the record held is the one the server stored, not the one composed')
        .toBe('Renamed');
      expect(store.detailLoading()).toBeFalse();
      expect(stored.length).toBe(1);

      expectNoListingReread('an update asks for no listing read; the listing reads itself on entry');
    });

    it('removes the row identified by nought and leaves the row identified by minus one', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the listing that will be depleted');

      const readEarlier: readonly PortalListItem[] = store.portals();

      expect(readEarlier.length).toBe(2);

      let removals = 0;

      store.deletePortal(SECOND_PORTAL_ID).subscribe(() => {
        removals += 1;
      });

      const removed = httpMock.expectOne(SECOND_PORTAL_URL);

      expect(removed.request.method).toBe('DELETE');
      expectNoQueryString(removed, 'a portal removal');

      removed.flush(null, { status: 204, statusText: 'No Content' });

      const readLater: readonly PortalListItem[] = store.portals();

      expect(readLater.map((row: PortalListItem) => row.portalId))
        .withContext('the row identified by nought is gone; the one identified by minus one stays')
        .toEqual([-1]);
      expect(readLater)
        .withContext('the collection was REPLACED, so an on-push consumer re-renders')
        .not.toBe(readEarlier);
      expect(readEarlier.length)
        .withContext('and the array a caller read earlier was not edited underneath it')
        .toBe(2);
      expect(removals).toBe(1);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('removes the row identified by minus one and leaves the row identified by nought', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the listing that will be depleted');

      store.deletePortal(SEEDED_PORTAL_ID);

      httpMock.expectOne(SEEDED_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.portals().map((row: PortalListItem) => row.portalId))
        .withContext('a comparison against the absent-integer marker would have removed the wrong row')
        .toEqual([0]);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('clears the selection when the portal removed was the selected one', () => {
      store.loadPortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(envelope(portalDetail(PORTAL_ID)));
      expect(store.hasSelection()).toBeTrue();

      store.deletePortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.selectedPortalId())
        .withContext('a removed portal cannot remain the selection')
        .toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).toBeNull();

      settleListingReread('the listing re-read that follows a removal');
    });

    it('leaves an unrelated selection alone when a different portal is removed', () => {
      store.selectPortal(PORTAL_ID);

      store.deletePortal(SECOND_PORTAL_ID);
      httpMock.expectOne(SECOND_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.selectedPortalId())
        .withContext('nought being removed says nothing about three being selected')
        .toBe(3);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('surfaces the last-remaining refusal verbatim, as one dotted value, and re-reads nothing', () => {
      store.deletePortal(SEEDED_PORTAL_ID);

      httpMock.expectOne(SEEDED_PORTAL_URL).flush(
        reasonedProblem(
          LAST_PORTAL_REFUSAL,
          409,
          'You Can Not Delete The Last Portal In Your Database',
        ),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const surfaced: string | null = failure.conflictCode;

      expect(failure.status).toBe(409);
      // MIGRATION 13, and divergence D-A: the value compared here is the code the API PUBLISHES, not a
      // legacy resource key. It is ONE dotted string rather than a path into a nested structure, so neither
      // fragment of a naive split is the code.
      expect(surfaced).withContext('the published code, verbatim').toBe(LAST_PORTAL_REFUSAL);
      expect(LAST_PORTAL_REFUSAL.split('.').length)
        .withContext('a naive split would yield two fragments')
        .toBe(2);
      expect(surfaced).withContext('and the first fragment is not the code').not.toBe('portal');
      expect(surfaced).withContext('nor is the second').not.toBe('last_remaining');
      expect(heldProblem(failure).type)
        .withContext('the code travels inside the document type, behind the published prefix')
        .toBe(`${FAILURE_TYPE_PREFIX}${LAST_PORTAL_REFUSAL}`);
      expect(failure.severity)
        .withContext('a state refusal an operator cannot act around is an error, not a caution')
        .toBe('error');
      expect(store.detailLoading()).toBeFalse();
      expect(store.hasFailure()).toBeTrue();

      // NO listing re-read follows a refusal. Nothing is expected here on purpose, and `verify()` is what
      // asserts it: a store that refreshed after a failed write would leave a request outstanding and fail
      // this specification.
    });

    it('holds the rows it already had when a removal is refused', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');

      readListing(portalPage([first], 0, 10, 1, 1), 'the sole portal');

      store.deletePortal(SEEDED_PORTAL_ID);
      httpMock
        .expectOne(SEEDED_PORTAL_URL)
        .flush(reasonedProblem(LAST_PORTAL_REFUSAL, 409, 'Refused'), {
          status: 409,
          statusText: 'Conflict',
        });

      expect(store.portals().map((row: PortalListItem) => row.portalId))
        .withContext('the optimistic removal did not happen, because the server refused')
        .toEqual([-1]);
      expect(store.totalCount()).toBe(1);
    });
  });

  // =========================================================================
  // THE SETTINGS PROJECTION — columns on a row, never a key/value bag
  // =========================================================================

  describe('settings projection', () => {
    it('reads the whole projection from the settings path', () => {
      store.loadSettings(PORTAL_ID);

      const read = httpMock.expectOne(PORTAL_SETTINGS_URL);

      expect(read.request.method).toBe('GET');
      expect(read.request.url).toBe('/api/v1/portals/3/settings');
      expectNoQueryString(read, 'a settings read');

      const projection = portalSettings(PORTAL_ID, {
        userQuota: QUOTA_NOT_SET,
        pageQuota: QUOTA_UNLIMITED,
        footerText: '',
      });

      read.flush(envelope(projection));

      const held: PortalSettings = heldRecord(store.settings(), 'settings');

      expect(held)
        .withContext('the projection is held member for member as the server wrote it')
        .toEqual(projection);
      expect(held.userQuota).toBe(-1);
      expect(held.pageQuota).toBe(0);
      expect(held.footerText).toBe('');
      expect(store.selectedPortalId())
        .withContext('reading settings selects the portal they belong to')
        .toBe(3);
      expect(store.settingsLoading()).toBeFalse();
      expect(store.settingsFailure()).toBeNull();
    });

    it('replaces the whole projection and refreshes a listing it has read', () => {
      // The listing is read FIRST, and that is now load-bearing rather than incidental setup: the refresh
      // keeps a listing ALREADY IN HAND coherent with the write - the projection carries the portal name,
      // the expiry date, the host fee and the host space, each also a listing column - and the store
      // deliberately does not ask for a listing it has never read.
      readListing(singleRowPage(PORTAL_ID), 'the listing this refresh will keep coherent');

      const composed = updatePortalSettingsRequest({ portalName: 'Renamed' });
      const saved: PortalSettings[] = [];

      store.saveSettings(PORTAL_ID, composed).subscribe((projection: PortalSettings) => {
        saved.push(projection);
      });

      const written = httpMock.expectOne(PORTAL_SETTINGS_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body).toBe(composed);

      const names: readonly string[] = Object.keys(written.request.body);

      // TWENTY-SEVEN: the twenty-six editable members plus the revision marker. Both portal write
      // paths carry it, because both replace the same columns through the same mapper.
      expect(names.length)
        .withContext('the update contract without its identifier, plus the revision marker')
        .toBe(27);
      expect(names)
        .withContext('the settings route refuses a stale save too, so it carries the marker as well')
        .toContain('concurrencyToken');
      expect(names)
        .withContext('so a second, contradictable copy of the identifier is not sent')
        .not.toContain('portalId');

      written.flush(envelope(portalSettings(PORTAL_ID, { portalName: 'Renamed' })));

      expect(heldRecord(store.settings(), 'settings').portalName).toBe('Renamed');
      expect(store.settingsLoading()).toBeFalse();
      expect(saved.length).toBe(1);

      settleListingReread('the listing re-read that follows a settings write');
    });

    it('issues no listing read after a settings write when no listing has been read', () => {
      // ⚠ THE MEASURED DEFECT. The refresh above is right when a listing is in hand and wrong when one is
      // not: the settings screen is reachable by a portal administrator who is NOT permitted to read the
      // portal listing, so against the running API every save produced `GET /api/v1/portals` → 403, a
      // console error, and a warning notification - raised globally, so it followed the operator to
      // whatever screen they had moved on to - complaining about a listing they never asked for,
      // immediately after a save that had SUCCEEDED.
      store.saveSettings(PORTAL_ID, updatePortalSettingsRequest({ portalName: 'Renamed' }));

      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(envelope(portalSettings(PORTAL_ID, { portalName: 'Renamed' })));

      expect(heldRecord(store.settings(), 'settings').portalName)
        .withContext('the write itself still lands')
        .toBe('Renamed');
      httpMock.expectNone(
        (candidate) => isListingRead(candidate),
        'no listing read follows a settings write when no listing has been read',
      );
    });

    it('folds the stored projection back into a detail it is already holding', () => {
      store.loadPortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(envelope(portalDetail(PORTAL_ID, { portalName: 'Before' })));

      expect(heldRecord(store.selectedPortal(), 'the detail').portalName).toBe('Before');

      store.saveSettings(PORTAL_ID, updatePortalSettingsRequest({ portalName: 'After' }));
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(envelope(portalSettings(PORTAL_ID, { portalName: 'After' })));

      expect(heldRecord(store.selectedPortal(), 'the detail').portalName).toBe('After');
    });

    it('leaves the detail members the projection does not carry exactly as they were', () => {
      store.loadPortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            portalName: 'Before',
            users: 42,
            pages: 7,
            administratorRoleName: 'Administrators',
            registeredRoleName: 'Registered Users',
          }),
        ),
      );

      store.saveSettings(PORTAL_ID, updatePortalSettingsRequest({ portalName: 'After' }));
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(envelope(portalSettings(PORTAL_ID, { portalName: 'After' })));

      const held = heldRecord(store.selectedPortal(), 'the detail');

      expect(held.portalName).withContext('the shared member is updated').toBe('After');
      expect(held.users).toBe(42);
      expect(held.pages).toBe(7);
      expect(held.administratorRoleName).toBe('Administrators');
      expect(held.registeredRoleName).toBe('Registered Users');
    });

    it('refuses to write one portal\u2019s projection onto another portal\u2019s detail', () => {
      // A save can complete after the operator has already moved to a different portal, and writing the
      // first portal's values over the second one's detail is a worse outcome than leaving the first one
      // stale.
      store.saveSettings(PORTAL_ID, updatePortalSettingsRequest({ portalName: 'Late Arrival' }));
      const written = httpMock.expectOne(PORTAL_SETTINGS_URL);

      store.loadPortal(SECOND_PORTAL_ID);
      httpMock
        .expectOne(`${PORTALS_URL}/${SECOND_PORTAL_ID}`)
        .flush(envelope(portalDetail(SECOND_PORTAL_ID, { portalName: 'The Other Portal' })));

      written.flush(envelope(portalSettings(PORTAL_ID, { portalName: 'Late Arrival' })));

      const held = heldRecord(store.selectedPortal(), 'the detail');

      expect(held.portalId).toBe(SECOND_PORTAL_ID);
      expect(held.portalName).toBe('The Other Portal');
    });

    it('reconciles nothing, and fails at nothing, when no detail has been read', () => {
      // The mirror of the listing guard: a slice never read has nothing to keep coherent. This is the
      // ordinary case for a portal administrator, who reaches settings by address and never loads a detail,
      // so it must not throw and must not issue a request of its own.
      expect(store.selectedPortal()).toBeNull();

      store.saveSettings(PORTAL_ID, updatePortalSettingsRequest({ portalName: 'Renamed' }));
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(envelope(portalSettings(PORTAL_ID, { portalName: 'Renamed' })));

      expect(store.selectedPortal()).toBeNull();
      httpMock.expectNone(
        (candidate) => candidate.url === PORTAL_URL,
        'no detail read is provoked by the reconciliation',
      );
    });

    it('publishes no per-key settings accessor to be called', () => {
      // 8: there is no portal-settings table, so there is no key to be read by. The legacy composite of the
      // same name was assembled per request and held in ambient request state; portal configuration is
      // columns on the portal row.
      const perKey: readonly string[] = memberNames(store).filter(
        (name: string) =>
          /^(get|set|read|write|patch)Setting$/i.test(name) ||
          /settingByKey|settingValue|settingEntries|settingsMap|settingsDictionary/i.test(name),
      );

      expect(perKey).withContext('the projection is read and replaced whole').toEqual([]);
    });

    it('reports a settings read that faults without disturbing the listing it holds', () => {
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 1, 1),
        'a listing that must survive a settings fault',
      );

      store.loadSettings(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(500, 'The server could not complete the request.', TRACE_ID), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      expect(heldFailure(store.settingsFailure(), 'settings').status).toBe(500);
      expect(store.listFailure())
        .withContext('each concern holds its own failure; one faulting does not blank another')
        .toBeNull();
      expect(store.portals().length).toBe(1);
      expect(store.settings()).toBeNull();
    });
  });

  // =========================================================================
  // HOST-NAME ALIASES — deliberately unpaged
  // =========================================================================

  describe('host-name aliases (unpaged)', () => {
    it('reads the alias collection with no query string at all', () => {
      store.loadAliases(PORTAL_ID);

      // ⚠️ THE STRING MATCHER IS THE ASSERTION. `expectOne` filters on the address WITH its query string,
      // so matching the bare path proves both the path and the total absence of a parameter in one step.
      const read = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(read.request.method).toBe('GET');
      expect(read.request.url).toBe('/api/v1/portals/3/aliases');
      expectNoQueryString(read, 'an unpaged alias read');

      const collection: readonly PortalAlias[] = [
        portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost'),
        portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200'),
      ];

      read.flush(envelope(collection));

      const held: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(Array.isArray(held))
        .withContext('a bare array reaches the store, never a paged envelope')
        .toBeTrue();
      expect(held).toEqual(collection);
      expect(store.aliasesLoaded()).toBeTrue();
      expect(store.aliasCount()).toBe(2);
      expect(store.aliasesPortalId())
        .withContext('the collection records which portal it belongs to')
        .toBe(3);
      expect(store.aliasLoading()).toBeFalse();
    });

    it('holds no page index, no page size and no total for the alias collection', () => {
      const aliasPaging: readonly string[] = memberNames(store).filter((name: string) => {
        const lowered: string = name.toLowerCase();

        return (
          lowered.includes('alias') &&
          (lowered.includes('page') || lowered.includes('total') || lowered.includes('size'))
        );
      });

      expect(aliasPaging)
        .withContext('the alias collection carries no paging coordinate of any kind')
        .toEqual([]);
    });

    it('distinguishes a collection never read from one read and found empty', () => {
      expect(store.aliases()).withContext('never read').toBeNull();
      expect(store.aliasesLoaded()).toBeFalse();
      expect(store.aliasCount())
        .withContext('and reports no count, which is a different fact from a count of nought')
        .toBeNull();

      store.loadAliases(PORTAL_ID);
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([]));

      expect(store.aliases()).withContext('read, and there are none').toEqual([]);
      expect(store.aliasesLoaded()).toBeTrue();
      expect(store.aliasCount()).toBe(0);
    });

    it('re-reads the collection after a creation, so both write paths leave the same state', () => {
      store.loadAliases(PORTAL_ID);
      const existing = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([existing]));

      const composed = createAliasRequest('contoso.example.test');
      const created: PortalAlias[] = [];

      store.createAlias(PORTAL_ID, composed).subscribe((alias: PortalAlias) => {
        created.push(alias);
      });

      const posted = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(posted.request.method).toBe('POST');
      expect(posted.request.body).toBe(composed);
      expect(Object.keys(posted.request.body))
        .withContext('the request carries the host name and nothing else')
        .toEqual(['httpAlias']);

      const stored = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'contoso.example.test');

      posted.flush(envelope(stored), { status: 201, statusText: 'Created' });

      const reread = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(reread.request.method).toBe('GET');

      reread.flush(envelope([existing, stored]));

      const held: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(held.map((alias: PortalAlias) => alias.portalAliasId))
        .withContext("the server's collection, in the server's order")
        .toEqual([PORTAL_ALIAS_ID, OTHER_PORTAL_ALIAS_ID]);

      // The response body is still used for the record just written - nothing about it is
      // discarded, only the collection is taken from the server.
      expect(store.selectedAliasId()).toBe(OTHER_PORTAL_ALIAS_ID);
      expect(heldRecord(store.selectedAlias(), 'selected alias')).toEqual(stored);
      expect(created.length).toBe(1);
      expect(store.aliasLoading()).toBeFalse();
    });

    it('adopts the row an update answers with and still re-reads the collection', () => {
      store.loadAliases(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_ALIASES_URL)
        .flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));

      const composed = updateAliasRequest('renamed.example.test');
      const emitted: PortalAlias[] = [];

      store.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, composed).subscribe((stored: PortalAlias) => {
        emitted.push(stored);
      });

      const written = httpMock.expectOne(PORTAL_ALIAS_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.url).toBe('/api/v1/portals/3/aliases/7');
      expect(written.request.body).toBe(composed);

      // The endpoint answers 200 with the row it stored, so the record just written comes from the response
      // rather than from the request.
      written.flush(envelope(portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'renamed.example.test')));

      const reread = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(reread.request.method).toBe('GET');
      reread.flush(
        envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'renamed.example.test')]),
      );

      expect(heldAliases(store.aliases())[0].httpAlias)
        .withContext('the collection now reports what the server stored')
        .toBe('renamed.example.test');
      expect(emitted.length).toBe(1);
      expect(emitted[0].httpAlias)
        .withContext('the ticket carries the stored row, not the submitted host name')
        .toBe('renamed.example.test');
      expect(heldRecord(store.aliasDetail(), 'alias detail').httpAlias)
        .withContext('the record just written is held, exactly as a create holds its answer')
        .toBe('renamed.example.test');
      expect(store.aliasLoading()).toBeFalse();
    });

    it('removes an unbound alias from the collection already in hand and clears its selection', () => {
      store.loadAliases(PORTAL_ID);
      const kept = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      const going = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([kept, going]));

      store.selectAlias(OTHER_PORTAL_ALIAS_ID);
      expect(store.selectedAliasId()).toBe(OTHER_PORTAL_ALIAS_ID);

      let removals = 0;

      store.deleteAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID).subscribe(() => {
        removals += 1;
      });

      const removed = httpMock.expectOne(OTHER_PORTAL_ALIAS_URL);

      expect(removed.request.method).toBe('DELETE');
      expectNoQueryString(removed, 'an alias removal');
      removed.flush(null, { status: 204, statusText: 'No Content' });

      expect(heldAliases(store.aliases()).map((alias: PortalAlias) => alias.portalAliasId))
        .withContext('removed from the collection in hand before the re-read lands')
        .toEqual([PORTAL_ALIAS_ID]);
      expect(store.selectedAliasId())
        .withContext('an unbound alias cannot remain selected')
        .toBeUndefined();
      expect(store.selectedAlias()).toBeNull();
      expect(removals).toBe(1);

      // ⚠ AND THE COLLECTION IS THEN RE-READ, matching create and update. The local filter above is exact
      // for the row REMOVED and is why it disappears at once; it cannot see the rows nobody here wrote.
      const reread = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(reread.request.method).toBe('GET');
      reread.flush(envelope([kept]));

      expect(heldAliases(store.aliases()).map((alias: PortalAlias) => alias.portalAliasId))
        .withContext("the server's answer, adopted")
        .toEqual([PORTAL_ALIAS_ID]);
      expect(store.aliasLoading()).toBeFalse();
    });

    it('reads one alias by its own path, with the identifier named as a segment', () => {
      store.loadAlias(PORTAL_ID, PORTAL_ALIAS_ID);

      const read = httpMock.expectOne(PORTAL_ALIAS_URL);

      expect(read.request.method).toBe('GET');
      expectNoQueryString(read, 'a single-alias read');

      const stored = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');

      read.flush(envelope(stored));

      expect(store.selectedAliasId()).toBe(PORTAL_ALIAS_ID);
      expect(heldRecord(store.aliasDetail(), 'alias detail')).toEqual(stored);
      expect(heldRecord(store.selectedAlias(), 'selected alias'))
        .withContext('the derived view resolves through the record fetched, absent a collection')
        .toEqual(stored);
      expect(store.selectedPortalId())
        .withContext('reading an alias selects the portal that owns it')
        .toBe(3);
    });

    it('surfaces the duplicate-host-name refusal verbatim, as one dotted value', () => {
      store.loadAliases(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_ALIASES_URL)
        .flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));

      store.createAlias(PORTAL_ID, createAliasRequest('localhost'));

      httpMock.expectOne(PORTAL_ALIASES_URL).flush(
        reasonedProblem(
          DUPLICATE_ALIAS_REFUSAL,
          409,
          'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
        ),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: PortalFailure = heldFailure(store.aliasFailure(), 'alias');
      const surfaced: string | null = failure.conflictCode;

      // Divergence D-A again: BOTH legacy resource keys — the one the signup screen read and the one the
      // alias editor read — collapse onto this single published code, because the server reports one code
      // for the collision however it was reached.
      expect(surfaced).withContext('the published code, verbatim').toBe(DUPLICATE_ALIAS_REFUSAL);
      expect(surfaced).not.toBe('portal');
      expect(surfaced).not.toBe('alias_duplicate');
      expect(failure.status).toBe(409);
      expect(heldAliases(store.aliases()).length)
        .withContext('the collection is unchanged: nothing was appended, because nothing was stored')
        .toBe(1);
      expect(store.aliasLoading()).toBeFalse();
    });
  });

  // =========================================================================
  // FAILURES — the structured document, classified once and held whole
  // =========================================================================

  // WRITES REPORT THROUGH A TICKET, NOT THROUGH A RETAINED CALLBACK

  describe('write outcome tickets', () => {
    it('takes NO callback argument on any of the seven writes', () => {
      // ⚠ ARITY IS THE CONTRACT, and asserting it is what stops a continuation creeping back
      // in. Each count is the write's real arguments with no trailing callback slot.
      expect(store.createPortal.length).withContext('createPortal(request)').toBe(1);
      expect(store.updatePortal.length).withContext('updatePortal(id, request)').toBe(2);
      expect(store.deletePortal.length).withContext('deletePortal(id)').toBe(1);
      expect(store.saveSettings.length).withContext('saveSettings(id, request)').toBe(2);
      expect(store.createAlias.length).withContext('createAlias(id, request)').toBe(2);
      expect(store.updateAlias.length)
        .withContext('updateAlias(id, aliasId, request)')
        .toBe(3);
      expect(store.deleteAlias.length).withContext('deleteAlias(id, aliasId)').toBe(2);
    });

    it('emits the outcome ONCE, and only after the state has settled', () => {
      // ⚠ THE ORDERING PROPERTY. The value is published as the LAST act of the response handler, so a
      // continuation can never observe a half-finished write. Here the continuation reads the store back
      // and finds it already correct.
      const observedSelection: (number | undefined)[] = [];
      const emissions: PortalDetail[] = [];
      let completions = 0;

      store.createPortal(createPortalRequest()).subscribe({
        next: (created: PortalDetail) => {
          emissions.push(created);
          observedSelection.push(store.selectedPortalId());
        },
        complete: () => {
          completions += 1;
        },
      });

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      expect(emissions.length).withContext('exactly one outcome per write').toBe(1);
      expect(completions).toBe(1);
      expect(observedSelection)
        .withContext('the continuation sees settled state, not state mid-update')
        .toEqual([0]);
      expect(store.detailLoading()).toBeFalse();

      expectNoListingReread('a creation asks for no listing read; the listing reads itself on entry');
    });

    it('COMPLETES WITHOUT EMITTING when the write fails, and does not error', () => {
      let succeeded = 0;
      let errored = 0;
      let completed = 0;

      store.createPortal(createPortalRequest()).subscribe({
        next: () => {
          succeeded += 1;
        },
        error: () => {
          errored += 1;
        },
        complete: () => {
          completed += 1;
        },
      });

      httpMock.expectOne(PORTALS_URL).flush(
        { title: 'Conflict', status: 409, detail: 'That host name is already bound.' },
        { status: 409, statusText: 'Conflict' },
      );

      expect(succeeded).withContext('the success continuation must not run').toBe(0);
      expect(errored).withContext('and the caller is handed no error to catch').toBe(0);
      expect(completed).withContext('the ticket still settles, so nothing waits forever').toBe(1);

      // The failure keeps its single existing route to the operator.
      expect(store.detailFailure()).withContext('reported once, through the slice').not.toBeNull();
      expect(store.detailLoading()).toBeFalse();
    });

    it('UPDATES STATE EVEN WHEN NOBODY SUBSCRIBES, so ignoring the ticket is safe', () => {
      readListing(portalPage([portalListItem(SEEDED_PORTAL_ID, 'First portal')], 0, 10, 1, 1), 'a listing');

      store.deletePortal(SEEDED_PORTAL_ID);

      const removed = httpMock.expectOne(SEEDED_PORTAL_URL);

      expect(removed.request.method)
        .withContext('the request went out with no subscriber at all')
        .toBe('DELETE');

      removed.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.portals().length).withContext('and the optimistic edit still ran').toBe(0);
      expect(store.detailLoading()).toBeFalse();

      settleListingReread('the listing re-read that follows a removal');
    });

    it('REPLAYS to a subscriber that arrives after the response, which a plain Subject would drop', () => {
      // ⚠ WHY AN `AsyncSubject` AND NOT A `Subject`. A caller subscribes after the command returns, and a
      // transport answering synchronously would already have completed a plain subject by then — silently
      // dropping the outcome and, in production, a screen's success notification.
      const ticket = store.createPortal(createPortalRequest());

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      const received: PortalDetail[] = [];

      ticket.subscribe((created: PortalDetail) => {
        received.push(created);
      });

      expect(received.length).withContext('the late subscriber still gets its outcome').toBe(1);
      expect(received[0].portalId).toBe(0);

      expectNoListingReread('a creation asks for no listing read; the listing reads itself on entry');
    });

    it('gives each write its OWN ticket, so two writes cannot cross-report', () => {
      // One ticket per operation. A shared subject would deliver the second write's outcome to the first
      // write's continuation — the same class of cross-record confusion the continuations themselves
      // caused.
      const firstOutcomes: PortalDetail[] = [];
      const secondOutcomes: PortalDetail[] = [];

      store
        .updatePortal(PORTAL_ID, updatePortalRequest({ portalId: PORTAL_ID, portalName: 'First' }))
        .subscribe((stored: PortalDetail) => {
          firstOutcomes.push(stored);
        });
      store
        .updatePortal(PORTAL_ID, updatePortalRequest({ portalId: PORTAL_ID, portalName: 'Second' }))
        .subscribe((stored: PortalDetail) => {
          secondOutcomes.push(stored);
        });

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url === PORTAL_URL,
      );

      expect(writes.length).withContext('a write is never superseded by a later one').toBe(2);

      writes[0].flush(envelope(portalDetail(PORTAL_ID, { portalName: 'First' })));
      writes[1].flush(envelope(portalDetail(PORTAL_ID, { portalName: 'Second' })));

      expect(firstOutcomes.map((entry) => entry.portalName)).toEqual(['First']);
      expect(secondOutcomes.map((entry) => entry.portalName)).toEqual(['Second']);

      for (const reread of httpMock.match(
        (candidate) => isListingRead(candidate),
      )) {
        if (!reread.cancelled) {
          reread.flush(portalPage([], 0, 10, 0, 0));
        }
      }
    });

    it('lets a caller UNSUBSCRIBE, which a retained callback gave no way to do', () => {
      // ⚠ THE PROPERTY THE WHOLE CHANGE EXISTS FOR. A component binds its subscription to its own lifetime,
      // so a response arriving after teardown reaches nothing.
      let ran = 0;

      const subscription = store.createPortal(createPortalRequest()).subscribe(() => {
        ran += 1;
      });

      // The screen goes away while the write is in flight.
      subscription.unsubscribe();

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      expect(ran).withContext("the departed screen's continuation does not run").toBe(0);

      // The store's own state still settled, because its subscription is its own.
      expect(store.selectedPortalId()).toBe(0);
      expect(store.detailLoading()).toBeFalse();

      expectNoListingReread('a creation asks for no listing read; the listing reads itself on entry');
    });
  });

  describe('failures', () => {
    it('retains the trace identifier, and reports it as the support reference when it stands alone', () => {
      store.loadPortals();

      expectListing('a listing that faults').flush(
        plainProblem(500, 'The server could not complete the request.', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      const failure: PortalFailure = heldFailure(store.listFailure(), 'listing');
      const document: ProblemDetails = heldProblem(failure);

      expect(document.traceId)
        .withContext('the join key between a browser report and the request the server logged')
        .toBe(TRACE_ID);
      expect(failure.supportReference)
        .withContext('with no correlation identifier present, the trace identifier is quoted instead')
        .toBe(TRACE_ID);
      expect(failure.status).toBe(500);
      expect(failure.severity).toBe('error');
      expect(failure.conflictCode)
        .withContext('a fault carries no state-refusal code')
        .toBeNull();
      expect(failure.validation).toBeNull();
      expect(store.listLoading()).toBeFalse();
      expect(store.hasFailure()).toBeTrue();
    });

    it('retains both identifiers and prefers the correlation one as the support reference', () => {
      store.deletePortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(reasonedProblem(LAST_PORTAL_REFUSAL, 409, 'Refused'), {
          status: 409,
          statusText: 'Conflict',
        });

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const document: ProblemDetails = heldProblem(failure);

      expect(document.traceId).withContext('both survive on the held document').toBe(TRACE_ID);
      expect(document.correlationId).toBe(CORRELATION_ID);
      expect(failure.supportReference)
        .withContext('and the correlation identifier is the one an operator quotes')
        .toBe(CORRELATION_ID);
      expect(document.instance)
        .withContext('the document is held WHOLE, so its other members survive too')
        .toBe(PORTALS_URL);
      expect(document.title).toBe('Request refused');
    });

    it('classifies a refused request at warning severity rather than as a fault', () => {
      store.loadSettings(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(403, 'You do not have permission to perform this action.', TRACE_ID), {
          status: 403,
          statusText: 'Forbidden',
        });

      const failure: PortalFailure = heldFailure(store.settingsFailure(), 'settings');

      expect(failure.status).toBe(403);
      expect(failure.severity)
        .withContext('a refusal is a caution: the system is working exactly as configured')
        .toBe('warning');
      expect(failure.severity)
        .withContext('and never danger styling, which would report a fault that has not occurred')
        .not.toBe('error');
    });

    it('classifies a portal that does not exist at warning severity', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(plainProblem(404, 'The requested item could not be found.', TRACE_ID), {
          status: 404,
          statusText: 'Not Found',
        });

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');

      expect(failure.status).toBe(404);
      expect(failure.severity).toBe('warning');
      expect(store.selectedPortal())
        .withContext('and nothing is invented to stand in for the record that is not there')
        .toBeNull();
    });

    it('narrows a per-field document and reads its dictionary by bracket', () => {
      store.createPortal(createPortalRequest({ portalName: '' }));

      httpMock.expectOne(PORTALS_URL).flush(
        validationProblem('One or more validation failures occurred.', {
          PortalName: ['The Site Name is required.'],
          'Administrator.Email': ['A valid email address is required.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const reported: ValidationProblemDetails = heldValidation(failure);

      // ⚠️ BRACKET ACCESS THROUGHOUT. The dictionary is an index signature and this workspace enables the
      // compiler option that makes dot access on one a compile error, deliberately — so that a lookup is
      // visibly a lookup.
      expect(reported.errors['PortalName']).toBeDefined();
      expect(reported.errors['PortalName']).toEqual(['The Site Name is required.']);
      expect(reported.errors['portalName'])
        .withContext('the keys name model members and are NOT camel-cased')
        .toBeUndefined();
      expect(reported.errors['Administrator.Email'])
        .withContext('a dotted key is one key, not a path into a nested structure')
        .toEqual(['A valid email address is required.']);
      expect(Object.keys(reported.errors).length).toBe(2);
      expect(failure.status).toBe(400);
      expect(failure.severity).toBe('error');
      expect(failure.conflictCode)
        .withContext('a per-field rejection is not one of the published state refusals')
        .toBeNull();
      expect(store.selectedPortal()).toBeNull();
    });

    it('holds message text as an inert plain string, neither escaped nor wrapped', () => {
      const hostile = '<script>alert(1)</script>';

      store.loadPortals();
      expectListing('a listing whose failure carries markup').flush(
        plainProblem(500, hostile, TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      const document: ProblemDetails = heldProblem(
        heldFailure(store.listFailure(), 'listing'),
      );

      expect(typeof document.detail)
        .withContext('a plain string, not a wrapped or pre-escaped value')
        .toBe('string');
      expect(document.detail)
        .withContext('held exactly as received, character for character')
        .toBe(hostile);
      expect(document.detail)
        .withContext('nothing was stripped out of it')
        .toContain('<script>');
    });

    it('holds a legacy break-tag prefix as received, in either spelling', () => {
      store.loadPortals();
      expectListing('a failure prefixed with the unclosed spelling').flush(
        plainProblem(500, '<br>Invalid Site Name', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(heldProblem(heldFailure(store.listFailure(), 'listing')).detail)
        .withContext('unstripped, prefix and all')
        .toBe('<br>Invalid Site Name');

      store.clearFailures();
      store.reloadPortals();
      expectListing('a failure prefixed with the closed spelling').flush(
        plainProblem(500, '<br/>Invalid Site Name', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(heldProblem(heldFailure(store.listFailure(), 'listing')).detail).toBe(
        '<br/>Invalid Site Name',
      );
    });

    it('holds a message that accumulated several break tags without collapsing them', () => {
      const accumulated = '<br>Invalid Site Name<br>Invalid Site Name<br>Invalid Site Name';

      store.createPortal(createPortalRequest({ portalName: 'a b/c' }));
      httpMock
        .expectOne(PORTALS_URL)
        .flush(plainProblem(400, accumulated, TRACE_ID), {
          status: 400,
          statusText: 'Bad Request',
        });

      const detail: string | undefined = heldProblem(
        heldFailure(store.detailFailure(), 'single-portal'),
      ).detail;

      expect(detail).withContext('all three, in order, unaltered').toBe(accumulated);
      expect(detail).toBeDefined();
      if (detail !== undefined) {
        expect(detail.split('<br>').length - 1)
          .withContext('three prefixes, one per rejected character')
          .toBe(3);
      }
    });

    it('composes a truthful document for a failure that arrived with none', () => {
      store.loadPortals();

      expectListing('a listing that never reached the server').error(
        new ProgressEvent('error'),
        { status: 0, statusText: 'Unknown Error' },
      );

      const failure: PortalFailure = heldFailure(store.listFailure(), 'listing');

      expect(failure.problem)
        .withContext('a document is composed so every consumer has one presentation')
        .not.toBeNull();
      expect(failure.problem?.type)
        .withContext('declared as carrying no semantics beyond the status')
        .toBe('about:blank');
      expect(failure.problem?.detail ?? '')
        .withContext('and it says what actually happened')
        .toContain('could not be reached');
      expect(failure.status).toBe(0);
      expect(failure.supportReference).toBeNull();
      expect(failure.conflictCode).toBeNull();
      expect(failure.validation).toBeNull();
      expect(failure.severity)
        .withContext('an unreachable server is a fault')
        .toBe('error');
      expect(store.listLoading()).toBeFalse();
    });

    it('clears every failure slice on request, issuing no request of its own', () => {
      store.loadPortals();
      expectListing('a listing that faults').flush(plainProblem(500, 'Faulted', TRACE_ID), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      store.loadSettings(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(500, 'Faulted', TRACE_ID), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      expect(store.hasFailure()).toBeTrue();

      store.clearFailures();

      expect(store.listFailure()).toBeNull();
      expect(store.detailFailure()).toBeNull();
      expect(store.settingsFailure()).toBeNull();
      expect(store.aliasFailure()).toBeNull();
      expect(store.hasFailure()).toBeFalse();

      // Clearing a failure re-reads nothing: an operator dismissing a message has not
      // asked for the data again. `verify()` asserts it.
    });
  });

  // =========================================================================
  // THE READ-ONLY SURFACE
  // =========================================================================

  describe('read-only surface', () => {
    /**
     * Every public state slice, by name, so that a failure says which one leaked.
     *
     * @param subject The store under assertion.
     * @returns Each slice against the name it is published under.
     */
    function publishedSlices(subject: PortalStore): ReadonlyMap<string, object> {
      return new Map<string, object>([
        ['page', subject.page],
        ['pageIndex', subject.pageIndex],
        ['requestedPageSize', subject.requestedPageSize],
        ['nameFilter', subject.nameFilter],
        ['sortBy', subject.sortBy],
        ['sortDir', subject.sortDir],
        ['listLoading', subject.listLoading],
        ['listFailure', subject.listFailure],
        ['selectedPortalId', subject.selectedPortalId],
        ['selectedPortal', subject.selectedPortal],
        ['detailLoading', subject.detailLoading],
        ['detailFailure', subject.detailFailure],
        ['settings', subject.settings],
        ['settingsLoading', subject.settingsLoading],
        ['settingsFailure', subject.settingsFailure],
        ['aliases', subject.aliases],
        ['aliasesPortalId', subject.aliasesPortalId],
        ['selectedAliasId', subject.selectedAliasId],
        ['aliasDetail', subject.aliasDetail],
        ['aliasLoading', subject.aliasLoading],
        ['aliasFailure', subject.aliasFailure],
      ]);
    }

    /**
     * Every derived view, by name.
     *
     * @param subject The store under assertion.
     * @returns Each view against the name it is published under.
     */
    function publishedViews(subject: PortalStore): ReadonlyMap<string, object> {
      return new Map<string, object>([
        ['portals', subject.portals],
        ['listMeta', subject.listMeta],
        ['totalCount', subject.totalCount],
        ['servedPageSize', subject.servedPageSize],
        ['totalPages', subject.totalPages],
        ['servedPageIndex', subject.servedPageIndex],
        ['isListEmpty', subject.isListEmpty],
        ['isPastEnd', subject.isPastEnd],
        ['pagerRequired', subject.pagerRequired],
        ['sort', subject.sort],
        ['isFiltered', subject.isFiltered],
        ['hasSelection', subject.hasSelection],
        ['aliasesLoaded', subject.aliasesLoaded],
        ['aliasCount', subject.aliasCount],
        ['selectedAlias', subject.selectedAlias],
        ['busy', subject.busy],
        ['hasFailure', subject.hasFailure],
      ]);
    }

    it('the probe discriminates: a writable signal exposes both mutating members', () => {
      // POSITIVE CONTROL, and without it the two negative cases below would be
      // unfalsifiable — a probe that always answered false would pass them both.
      const writable = signal<number>(0);
      const observed = mutatingMembers(writable);

      expect(observed.setter).withContext('a writable signal carries a setter').toBeTrue();
      expect(observed.updater).withContext('and an updater').toBeTrue();
    });

    it('exposes no setter and no updater on any of the twenty-one state slices', () => {
      const slices: ReadonlyMap<string, object> = publishedSlices(store);

      expect(slices.size).withContext('every published slice is covered').toBe(21);

      for (const [name, slice] of slices) {
        const observed = mutatingMembers(slice);

        expect(observed.setter).withContext(`${name} exposes no setter`).toBeFalse();
        expect(observed.updater).withContext(`${name} exposes no updater`).toBeFalse();
      }
    });

    it('exposes no setter and no updater on any of the seventeen derived views', () => {
      const views: ReadonlyMap<string, object> = publishedViews(store);

      expect(views.size).withContext('every published view is covered').toBe(17);

      for (const [name, view] of views) {
        const observed = mutatingMembers(view);

        expect(observed.setter).withContext(`${name} exposes no setter`).toBeFalse();
        expect(observed.updater).withContext(`${name} exposes no updater`).toBeFalse();
      }
    });

    it('replaces the collection it holds rather than editing the one a caller already read', () => {
      // ⚠️ WHAT THIS CAN AND CANNOT PROVE, STATED PLAINLY. A read-only array declaration is a compile-time
      // guarantee, and attempting a run-time write to demonstrate its absence would need a type assertion —
      // forbidden here, and it would prove less than this does anyway.
      store.loadAliases(PORTAL_ID);
      const kept = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      const going = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([kept, going]));

      const readEarlier: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(readEarlier.length).toBe(2);

      store.deleteAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID);
      httpMock.expectOne(OTHER_PORTAL_ALIAS_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      const readLater: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(readLater.length).toBe(1);
      expect(readLater).withContext('a NEW array, not the one edited in place').not.toBe(readEarlier);
      expect(readEarlier.length)
        .withContext('and the array read earlier still reports what it reported')
        .toBe(2);

      // The removal re-reads the collection, as every write on this resource does. Answered here so the
      // identity property above is measured on the LOCAL edit - which is the subject of this case - and so
      // the unconditional verification in `afterEach` has nothing outstanding.
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([kept]));
      // Deep equality rather than identity, and the distinction is worth stating.
      expect(readEarlier[1])
        .withContext('holding the very record that was unbound')
        .toEqual(going);
      expect(store.aliasCount()).toBe(1);
    });

    it('replaces the page it holds rather than editing the rows a caller already read', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the page that will be depleted');

      const rowsReadEarlier: readonly PortalListItem[] = store.portals();
      const pageReadEarlier = store.page();

      store.deletePortal(SECOND_PORTAL_ID);
      httpMock.expectOne(SECOND_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.portals()).not.toBe(rowsReadEarlier);
      expect(store.page()).withContext('a new envelope as well as a new array').not.toBe(pageReadEarlier);
      expect(rowsReadEarlier.length).toBe(2);
      expect(pageReadEarlier.items.length).toBe(2);
      expect(store.listMeta())
        .withContext('while the coordinates the server reported are carried forward untouched')
        .toEqual(pageReadEarlier.meta);

      settleListingReread('the listing re-read that follows a removal');
    });
  });
  // =========================================================================
  // THE SETTLED LATCH — "NOT ASKED YET" IS NOT "ASKED AND EMPTY"
  // =========================================================================

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so a screen reading only the rows and the in-flight flag
  // painted "No portals match the current filter." over a listing nobody had read yet — the empty-table
  // flash reported on every post-save return to a listing.
  describe('the settled latch', () => {
    it('is DOWN on a fresh store, so a screen can tell an un-asked listing from an empty one', () => {
      expect(store.listSettled()).toBeFalse();
      expect(store.portals()).toEqual([]);
      expect(store.listLoading())
        .withContext('and nothing is in flight, which is exactly what made the two states identical')
        .toBeFalse();
    });

    it('stays DOWN while the first read is outstanding and rises when it answers', () => {
      store.loadPortals();
      const request = expectListing('the first listing read');

      expect(store.listSettled())
        .withContext('a request in flight has not settled the question')
        .toBeFalse();

      request.flush(singleRowPage(PORTAL_ID));

      expect(store.listSettled()).toBeTrue();
    });

    it('rises on a FAILED read too, because the question of whether one happened is answered either way', () => {
      store.loadPortals();
      expectListing('a listing read that fails').flush(
        { title: 'Server Error', status: 500 },
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(store.listSettled())
        .withContext('a failure must not leave a waiting indicator standing over a reportable failure')
        .toBeTrue();
      expect(store.listFailure()).not.toBeNull();
    });

    it('goes back DOWN on reset, because the page it spoke for is discarded with the session', () => {
      readListing(singleRowPage(PORTAL_ID), 'a listing read before the session ends');
      expect(store.listSettled()).toBeTrue();

      store.reset();

      expect(store.listSettled()).toBeFalse();
      expect(store.portals()).toEqual([]);
    });
  });

});
