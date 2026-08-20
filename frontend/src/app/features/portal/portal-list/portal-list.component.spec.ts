// Specification for the portal (tenant) listing screen at /portals.

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { signal } from '@angular/core';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalListComponent } from './portal-list.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { PortalListItem } from '../../../core/models/portal.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
} from '../../../core/models/problem-details.model';
import type { AppNotification } from '../../../core/services/notification.service';
import type {

  DataTableColumn,
  DataTableTextColumn,
} from '../../../shared/components/data-table/data-table.component';

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


/** The collection address, relative because the production environment is relative. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The body-bound search address. ⚠ A SEPARATE ADDRESS FROM {@link PORTALS_URL} ON PURPOSE: a listing read that
 * carries a term a person typed goes here, so the term never appears in a logged request line.
 */
const PORTALS_SEARCH_URL = '/api/v1/portals/search';

/** The legacy absent-integer marker, and simultaneously the portal identity seed. */
const FIRST_PORTAL_ID = -1;

/** The second portal an installation ever creates. */
const SECOND_PORTAL_ID = 0;

/** The legacy absent-date marker, which survives on the wire. */
const NULL_DATE = '0001-01-01T00:00:00';

/** A real expiry, chosen so its rendering cannot be mistaken for a coincidence. */
const REAL_EXPIRY_DATE = '2027-03-15T00:00:00';

/** The URN prefix every failure code this API publishes is carried behind. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** A fixed trace identifier, shaped like the trace parent the server derives one from. */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/** A fixed correlation identifier - the value an operator quotes when reporting a refusal. */
const CORRELATION_ID = '0f7d3c81-9a24-4b6e-8c5d-2e91b7a40f36';

/** The reason phrase the API publishes as the problem `title`, keyed by status. */
const STATUS_TITLE: Readonly<Record<number, string>> = Object.freeze({
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
});

/**
 * A problem document as this API publishes one. They wrote `type: 'about:blank'`, which this API never
 * sends: a refusal reaches the wire through one shared problem factory that fills an unspecified type
 * from the status vocabulary, so the type is ALWAYS a `urn:dnnmigration:error:` code and a screen
 * branching on the code would have been tested against a document from which no code can be read.
 *
 * @param status The status the server answered with.
 * @param code The failure code, published behind the URN prefix.
 * @param detail The authored explanation.
 * @returns The document.
 */
function problemOf(status: number, code: string, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

function portalRow(overrides: Partial<PortalListItem> = {}): PortalListItem {
  return {
    portalId: SECOND_PORTAL_ID,
    portalName: 'Baseline Portal',
    aliases: ['localhost'],
    users: 3,
    pages: 7,
    hostSpace: 0,
    hostFee: 0,
    expiryDate: null,
    ...overrides,
  };
}

/** The shape a collection endpoint answers with, as the paging contract publishes it. */
interface PortalPageBody {
  readonly items: readonly PortalListItem[];
  readonly meta: {
    readonly totalCount: number;
    readonly pageIndex: number;
    readonly pageSize: number;
    readonly totalPages: number;
  };
}

function pageOf(
  items: readonly PortalListItem[],
  totalCount: number = items.length,
  pageIndex = 0,
  pageSize = 10,
): PortalPageBody {
  return {
    items,
    meta: {
      totalCount,
      pageIndex,
      pageSize,
      totalPages: pageSize === 0 ? 0 : Math.ceil(totalCount / pageSize),
    },
  };
}

describe('PortalListComponent', () => {
  let fixture: ComponentFixture<unknown>;
  let component: PortalListComponent;
  let http: HttpTestingController;
  let notifications: NotificationService;

  /**
   * The router, read for the address the screen has navigated to. Asserted directly in several places,
   * because "the screen put the filter in the address" and "the screen asked the server for the filtered
   * page" are two different promises and only one of them is visible in an HTTP expectation.
   */
  let router: Router;

  /**
   * The harness the screen is mounted through, so a navigation can be driven the way the browser drives
   * one.
   */
  let harness: RouterTestingHarness;

  /**
   * The tenant the signed-in session is scoped to, under test control. The screen consults exactly ONE
   * fact from the session store - the browsed tenant - so the collaborator is stood in for by a value
   * carrying exactly that one signal.
   */
  let sessionPortalId: ReturnType<typeof signal<number | null>>;

  /** Reads a protected member without widening it to the forbidden catch-all type. */
  function member<T>(name: string): T {
    return (component as unknown as Record<string, T>)[name];
  }

  function invoke<T>(name: string, ...args: unknown[]): T {
    const method = member<(...called: unknown[]) => T>(name);

    return method.apply(component, args);
  }

  /** The rendered host element, typed. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  function columns(): readonly DataTableColumn<PortalListItem>[] {
    return member<() => readonly DataTableColumn<PortalListItem>[]>('columns')();
  }

  /**
   * Lets an address change reach the store, then renders. ⚠ EVERY AFFORDANCE ON THIS SCREEN IS
   * ASYNCHRONOUS NOW, AND OMITTING THIS MAKES A SPECIFICATION MEASURE NOTHING. A letter, a search, a page
   * turn and a heading all NAVIGATE, and a router navigation settles in a microtask - so a synchronous
   * expectation placed straight after one of them runs before the address has changed, before the store
   * has been told and before any request exists.
   */
  async function settleAddress(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** The query parameters the screen has actually navigated to. */
  function addressParams(): Readonly<Record<string, string>> {
    return router.parseUrl(router.url).queryParams as Readonly<Record<string, string>>;
  }

  /**
   * The hosting-fee cell of every rendered row, in row order. The fee is the NINTH cell: settings,
   * delete, identifier, title, host names, accounts, pages, disk space, fee, expiry.
   */
  function feeCells(): readonly string[] {
    return Array.from(host().querySelectorAll('tbody tr')).map((row) =>
      (row.querySelectorAll('td,th')[8]?.textContent ?? 'MISSING').trim(),
    );
  }

  function settleFirstPage(
    items: readonly PortalListItem[],
    totalCount?: number,
    pageIndex = 0,
  ): void {
    const request: TestRequest = http.expectOne(
      (candidate) => isListingRead(candidate),
      'the initial portal listing request',
    );
    request.flush(pageOf(items, totalCount, pageIndex));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    sessionPortalId = signal<number | null>(null);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // ⚠ MOUNTED THROUGH A REAL ROUTER AT A REAL PATH, AND A DIRECTLY CREATED COMPONENT CANNOT TEST THIS
        // SCREEN. Its listing state - page, filter and ordering - lives in the ADDRESS: every affordance
        // navigates, and the address change is what reaches the store and causes the request.
        provideRouter([{ path: 'portals', component: PortalListComponent }]),
        {
          provide: AuthStore,
          useValue: {
            portalId: sessionPortalId.asReadonly(),
          },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
    router = TestBed.inject(Router);

    harness = await RouterTestingHarness.create();
    component = await harness.navigateByUrl('/portals', PortalListComponent);
    fixture = harness.fixture;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  // Creation and the initial read

  it('creates and reads the first page from the relative collection address', () => {
    expect(component).toBeTruthy();

    const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

    expect(request.request.method).toBe('GET');
    // No page size is requested: the size is the server's to decide.
    expect(sentFilters(request).has('pageSize')).toBeFalse();
    // No filter is sent on the first read.
    expect(sentFilters(request).has('name')).toBeFalse();
    // The address must never be absolute - the SPA is served from the same origin.
    expect(request.request.url.startsWith('http')).toBeFalse();

    request.flush(pageOf([portalRow()]));
  });

  it('paints the page heading taken from the local resource value', () => {
    settleFirstPage([portalRow()]);

    const heading: HTMLElement | null = host().querySelector<HTMLElement>('h1');

    expect(heading?.textContent?.trim()).toBe('Portals');
  });

  // THE FILTER STRIP

  describe('the first-letter filter strip', () => {
    it('holds twenty-seven entries: A to Z, then the clear-filter entry appended last', () => {
      settleFirstPage([portalRow()]);

      const labels: readonly string[] = textOf('.portal-list__letter');

      expect(labels.length).toBe(27);
      expect(labels[0]).toBe('A');
      expect(labels[25]).toBe('Z');
      // Appended AFTER Z, exactly as the legacy list was assembled.
      expect(labels[26]).toBe('All');
      // The twenty-eighth legacy entry has no successor endpoint and must not appear.
      expect(labels).not.toContain('Expired');
      expect(labels).not.toContain('0-9');
    });

    it('sends the chosen letter as the name filter with no pattern character', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'B', value: 'B' });
      await settleAddress();

      expect(addressParams()['filter']).toBe('B');
      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne(
        (candidate) => isListingRead(candidate) && sentFilters(candidate).get('name') === 'B',
      );

      // The repository composes any pattern it needs; a per-cent sign here would double it.
      expect(sentFilters(request).get('name')).toBe('B');
      expect(sentFilters(request).get('name')).not.toContain('%');

      expect(sentFilters(request).has('expired')).toBeFalse();
      expect(sentFilters(request).has('isExpired')).toBeFalse();
      expect(sentFilters(request).has('expiryDate')).toBeFalse();

      // And the only parameters carried at all are the paging index and the name.
      expect(sentFilters(request).keys().sort()).toEqual(['name', 'pageIndex']);
      request.flush(pageOf([]));
    });

    it('clears the filter rather than sending the word All', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'C', value: 'C' });
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'C').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'All', value: null });
      await settleAddress();

      // The parameter is REMOVED from the address rather than written empty, so an unfiltered listing is the
      // bare path - which is also what makes the reset distinguishable from a filter on the empty string.
      expect(addressParams()['filter']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).has('name')).toBeFalse();
      request.flush(pageOf([portalRow()]));
    });

    it('returns to the first page whenever the filter changes', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onPageChange', 3);
      await settleAddress();
      expect(addressParams()['currentpage']).toBe('4');
      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '3')
        .flush(pageOf([portalRow()], 40, 3));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'D', value: 'D' });
      await settleAddress();

      // The page is dropped from the address as well as reset on the wire, so a reload of the filtered view
      // does not land back on the fourth page of a match set that may now have one.
      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne(
        (candidate) => sentFilters(candidate).get('name') === 'D',
      );

      expect(sentFilters(request).get('pageIndex')).toBe('0');
      request.flush(pageOf([]));
    });

    it('marks the clear-filter entry as pressed while no filter is held', () => {
      settleFirstPage([portalRow()]);

      const pressed: readonly string[] = textOf('.portal-list__letter[aria-pressed="true"]');

      expect(pressed).toEqual(['All']);
    });

    it('marks the chosen letter as pressed, and only that letter', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'M', value: 'M' });
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'M').flush(pageOf([]));
      fixture.detectChanges();

      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual(['M']);
    });

    it('shows the letter it applied in the search box, so the filter is visible and clearable', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'N', value: 'N' });
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'N').flush(pageOf([]));
      fixture.detectChanges();

      const box: HTMLInputElement | null = host().querySelector<HTMLInputElement>(
        'app-search-input input',
      );

      expect(box).not.toBeNull();
      expect(box?.value).toBe('N');
    });

    it('empties the search box when the reset entry clears the filter', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onSearch', 'QA010');
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'QA010').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'All', value: null });
      await settleAddress();
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()], 40));
      fixture.detectChanges();

      // Measured before this was reconciled: the listing was restored while the box still read `QA010`, so
      // the screen contradicted itself and re-submitting the stale text was suppressed as a duplicate.
      expect(host().querySelector<HTMLInputElement>('app-search-input input')?.value).toBe('');
    });

    it('leaves every entry unpressed while a free-text filter is in force', async () => {
      settleFirstPage([portalRow()], 40);

      // Truthful: the list is filtered, but by none of the strip's entries.
      invoke<void>('onSearch', 'base');
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'base').flush(pageOf([]));
      fixture.detectChanges();

      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual([]);
    });
  });

  // The free-text filter

  describe('the free-text name filter', () => {
    it('forwards the text byte for byte, untrimmed and with its case unchanged', async () => {
      settleFirstPage([portalRow()]);

      invoke<void>('onSearch', '  BaSe ');
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('name')).toBe('  BaSe ');
      // Untrimmed in the address too, so a reload asks for exactly what was asked for the first time.
      expect(addressParams()['filter']).toBe('  BaSe ');
      request.flush(pageOf([]));
    });

    it('treats empty text as no filter, which is the legacy test', async () => {
      settleFirstPage([portalRow()]);

      invoke<void>('onSearch', 'QA010');
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'QA010').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onSearch', '');
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).has('name')).toBeFalse();
      expect(addressParams()['filter']).toBeUndefined();
      request.flush(pageOf([portalRow()]));
    });

    it('does not write an echo of its own emission back into the box being typed in', async () => {
      settleFirstPage([portalRow()]);

      const box: HTMLInputElement = host().querySelector<HTMLInputElement>(
        'app-search-input input',
      ) as HTMLInputElement;

      invoke<void>('onSearch', 'ab');
      box.value = 'abc';
      box.dispatchEvent(new Event('input'));
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'ab').flush(pageOf([]));
      fixture.detectChanges();

      expect(host().querySelector<HTMLInputElement>('app-search-input input')?.value).toBe('abc');
    });
  });

  // THE COLUMN SET

  describe('the column set', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    it('declares the ten legacy columns in legacy order', () => {
      expect(columns().map((column) => column.key)).toEqual([
        'edit',
        'delete',
        'portalId',
        'portalName',
        'aliases',
        'users',
        'pages',
        'hostSpace',
        'hostFee',
        'expiryDate',
      ]);
    });

    it('paints the resource values, which differ from the markup attributes', () => {
      const labels = new Map(columns().map((column) => [column.key, column.label]));

      expect(labels.get('portalId')).toBe('Portal Id');
      expect(labels.get('portalName')).toBe('Title');
      expect(labels.get('aliases')).toBe('Portal Aliases');
      expect(labels.get('users')).toBe('Users');
      expect(labels.get('pages')).toBe('Pages');
      // The markup said `DiskSpace`; the resource value is two words.
      expect(labels.get('hostSpace')).toBe('Disk Space');
      // The markup said `HostingFee`; the resource value is two words.
      expect(labels.get('hostFee')).toBe('Hosting Fee');
      expect(labels.get('expiryDate')).toBe('Expires');
    });

    it('keys the columns distinctly from their labels and uniquely within the set', () => {
      const keys: readonly string[] = columns().map((column) => column.key);

      expect(new Set(keys).size).toBe(keys.length);
      // The two columns whose key and label genuinely differ.
      expect(keys).toContain('hostSpace');
      expect(keys).toContain('hostFee');

      // No key is its own label. Keying a column by the words it paints looks harmless and is not: the
      // wording is a resource value that may be reworded, and duplicate labels do occur across this legacy
      // administration set, so a label-derived key can collide.
      for (const column of columns()) {
        expect(column.key).not.toBe(column.label);
      }
    });

    // ⚠ MAJOR (reachability) — THIS SPECIFICATION WAS REWRITTEN BECAUSE THE COLUMN CHANGED KIND. It pinned
    // the column's `field` at `portalName`, which was the right assertion while the title was BOUND text -
    // and bound text can carry no affordance, which is why the portal record screen at `/portals/:portalId`
    // had no inbound link anywhere in the application.
    it('renders the title column from the portal-name member, not from a title member', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));
      const title = byKey.get('portalName');

      expect(title?.label).toBe('Title');
      expect(title?.kind).toBe('template');
      expect(byKey.has('title')).toBeFalse();

      expect(
        host().querySelector<HTMLElement>('tbody th[scope="row"]')?.textContent?.trim(),
      ).toBe('Baseline Portal');
    });
    it('renders the per-column alignment onto the header and body cells alike', () => {
      const headerAligns: readonly (string | null)[] = queryAll<HTMLElement>('thead th').map(
        (cell) => cell.getAttribute('data-align'),
      );
      const bodyAligns: readonly (string | null)[] = queryAll<HTMLElement>('tbody td,tbody th').map(
        (cell) => cell.getAttribute('data-align'),
      );

      const expected: readonly string[] = [
        'center',
        'center',
        'start',
        'start',
        'start',
        'center',
        'center',
        'center',
        'center',
        'center',
      ];

      expect(headerAligns).toEqual(expected);
      expect(bodyAligns).toEqual(expected);
    });

    it('declares alignment per column rather than once for the grid', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));

      // The three legacy template columns that overrode BOTH sides to the start.
      for (const key of ['portalId', 'portalName', 'aliases']) {
        expect(byKey.get(key)?.headerAlign).toBe('start');
        expect(byKey.get(key)?.bodyAlign).toBe('start');
      }

      // The five that declared only a vertical alignment and inherited the centre.
      for (const key of ['users', 'pages', 'hostSpace', 'hostFee', 'expiryDate']) {
        expect(byKey.get(key)?.headerAlign).toBe('center');
        expect(byKey.get(key)?.bodyAlign).toBe('center');
      }
    });

    it('offers exactly the five orderings the endpoint accepts, and no others', () => {
      // ⚠ THIS REPLACES A FACT THAT PINNED "NO COLUMN IS SORTABLE", AND THE REPLACEMENT IS THE POINT. The
      // legacy grid was declared without `AllowSorting`, which the earlier fact reproduced faithfully - and
      // faithfully reproduced a screen that, on a real installation of hundreds of portals at ten to a page
      // with its own page-size setting commented out, offered a twenty-six-letter strip as its only finding
      // aid.
      const sortable: readonly string[] = columns()
        .filter((column) => (column as { readonly sortable?: boolean }).sortable === true)
        .map((column) => column.key);

      expect(sortable).toEqual(['portalId', 'portalName', 'hostSpace', 'hostFee', 'expiryDate']);

      const unsortable: readonly string[] = columns()
        .filter((column) => (column as { readonly sortable?: boolean }).sortable !== true)
        .map((column) => column.key);

      expect(unsortable).toEqual(['edit', 'delete', 'aliases', 'users', 'pages']);
    });

    // ⚠ THIS REPLACES A FACT THAT PINNED `min-content` ON THE TWO COMMAND COLUMNS, AND THE REPLACEMENT IS
    // THE WHOLE POINT — QA-09. `min-content` is a valid CSS width KEYWORD but it is not a LENGTH, and the
    // grid this descriptor feeds sets `table-layout: fixed`. The fixed algorithm resolves a column from its
    // first row only and accepts a length or a percentage there; handed a keyword it cannot resolve, it
    // discards the declaration and falls the column back to the automatic equal share. So the descriptor
    // asked for "as narrow as the glyph" and the browser painted "one tenth of the table" — measured as a
    // 16-unit icon sitting in a track wide enough for a sentence, while the Title and Aliases columns that
    // genuinely needed the room were squeezed into the same share and broke their words mid-token.
    //
    // The token is a real length, so the algorithm honours it, and it is a TOKEN rather than a literal so
    // all four listings size their command columns identically from one declaration.
    it('sizes the two command columns from the shared command-column token', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));

      // The TEXT variant of the token, because this screen's two commands are WORDS rather than glyphs —
      // "Settings" and "Delete", from the legacy resource keys. Measured in the icon track, the word plus
      // the cell's own inline padding overflowed its cell and painted into the column beside it.
      expect(byKey.get('edit')?.width).toBe('var(--table-command-column-text-inline-size)');
      expect(byKey.get('delete')?.width).toBe('var(--table-command-column-text-inline-size)');

      // Every data column but ONE declares a percentage, which the same algorithm resolves.
      for (const key of ['portalId', 'aliases', 'users', 'pages', 'expiryDate'] as const) {
        // The percentage form the shared contract admits, DECIMALS INCLUDED. A column is sized to hold its
        // own heading at the table's floor width, and several of those measurements do not land on a
        // whole percent; the runtime validator and the `${number}%` type both accept a fraction, so
        // pinning this to integers only would assert something stricter than the contract itself.
        expect(byKey.get(key)?.width).toMatch(/^\d+(?:\.\d+)?%$/);
      }

      // ⚠ AND EXACTLY ONE COLUMN DECLARES NOTHING, WHICH IS A REQUIREMENT RATHER THAN AN OMISSION. Under
      // `table-layout: fixed` the leftover after the percentages is handed to whichever columns declared
      // something else — so with EVERY column weighted, the leftover went to the two command columns and
      // they painted 89.875px against the 5rem they asked for. The title carries the slack instead.
      expect(byKey.get('portalName')?.width).toBeUndefined();

      const unweighted: readonly string[] = columns()
        .filter((column) => column.width === undefined)
        .map((column) => column.key);

      expect(unweighted).withContext('exactly one flexible column').toEqual(['portalName']);
    });

    // The descriptor set is one half of the claim; the other half is that the grid actually PAINTS those
    // values. Asserted separately because a correct descriptor handed to a mis-wired grid input renders
    // nothing at all, and only the rendered heading catches that.

    it('paints the eight data headings in legacy order, with the resource wording', () => {
      const headings: readonly string[] = textOf('thead th');

      expect(headings).toEqual([
        // The two command columns declare `headerHidden`, so their heading is NAMED but not PAINTED - see
        // the expectation below, which pins that distinction.
        'Edit this Portal',
        'Delete',
        'Portal Id',
        'Title',
        'Portal Aliases',
        'Users',
        'Pages',
        'Disk Space',
        'Hosting Fee',
        'Expires',
      ]);

      // The markup attributes, which the run-time localisation overwrote, must never surface.
      expect(headings).not.toContain('PortalId');
      expect(headings).not.toContain('DiskSpace');
      expect(headings).not.toContain('HostingFee');
    });

    it('names the two command columns without painting them', () => {
      const labels: readonly HTMLElement[] = queryAll<HTMLElement>(
        'thead th span:not(.data-table__sort-indicator)',
      );

      expect(labels.length).toBe(10);

      // The command columns gain a name they never had. The legacy image columns rendered no heading text
      // at all, so a screen-reader user reading the header row heard two unnamed columns.
      expect(labels[0]?.classList.contains('data-table__label--hidden')).toBeTrue();
      expect(labels[1]?.classList.contains('data-table__label--hidden')).toBeTrue();

      // The eight data headings are painted, which is the legacy appearance.
      for (const label of labels.slice(2)) {
        expect(label.classList.contains('data-table__label--hidden')).toBeFalse();
      }
    });

    it('renders every heading as a real column-scoped header cell', () => {
      const headers: readonly HTMLTableCellElement[] = queryAll<HTMLTableCellElement>('thead th');

      expect(headers.length).toBe(10);
      for (const header of headers) {
        expect(header.getAttribute('scope')).toBe('col');
      }

      // And no heading is a styled body cell masquerading as one.
      expect(queryAll<Element>('thead td').length).toBe(0);
    });

    it('binds the disk-space and hosting-fee columns to their unrenamed model members', () => {
      const headings: readonly string[] = textOf('thead th');
      const cells: readonly string[] = textOf('tbody td,tbody th');

      const byKey = new Map(columns().map((column) => [column.key, column]));
      const diskSpace = byKey.get('hostSpace');

      // ⚠ THE KEY IS WHAT CARRIES THE NAME-PRESERVATION CLAIM NOW, and this column is deliberately no
      // longer a field column. It was one, which is exactly why a portal holding the legacy absent-integer
      // marker printed a literal `-1` here while the two tally columns beside it painted an em dash - the
      // same row disagreeing with itself about the same sentinel. It is a template column over the shared
      // tally cell, and the key it is still bound by is the unrenamed model member.
      expect(diskSpace).withContext('the column is still keyed by the model member').not.toBeUndefined();
      expect(diskSpace?.key).toBe('hostSpace');
      expect(headings.indexOf('Disk Space')).toBe(7);
      expect(cells[7]).toBe('0');

      // The fee column is a formatted column rather than a field column, because the legacy grid attached a
      // format string to it and to no other; the fee it formats is `hostFee`.
      expect(headings.indexOf('Hosting Fee')).toBe(8);
      expect(cells[8]).toBe('0.00');
    });
  });

  // THE RECORD SCREEN'S WAY IN
  // ⚠ MAJOR (reachability) — its own block, because it is the only thing standing between the portal record
  // form and being unreachable.

  describe('the record link', () => {
    // `/portals/:portalId` is part of the frozen route table and renders the portal record form, yet before
    // this every affordance naming a portal went to the create form or to the settings CHILD route, so the
    // record screen was reachable only by typing its address.
    it('links the row title to the portal RECORD, distinctly from the settings command', () => {
      settleFirstPage([portalRow({ portalId: 31, portalName: 'Contoso Intranet' })]);

      const record: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'a.portal-list__record-link',
      );

      // The record route, with no trailing child segment - that segment is the settings screen.
      expect(record?.getAttribute('href')).toBe('/portals/31');
      expect(record?.getAttribute('href')).not.toBe('/portals/31/settings');

      // WCAG 2.5.3: the TITLE is the accessible name, so a person driving this by voice says the portal's
      // own name. No `aria-label`, which would replace that name with a composed sentence.
      expect(record?.textContent?.trim()).toBe('Contoso Intranet');
      expect(record?.hasAttribute('aria-label')).toBeFalse();

      // Where it leads is carried as a DESCRIPTION, announced after the name rather than instead of it.
      expect(record?.getAttribute('title')).toBe('Open this portal record: Contoso Intranet');

      // It lives in the cell that identifies the row, so the link and the row header are one element deep
      // rather than two competing affordances in one cell.
      expect(
        host().querySelector<HTMLElement>('tbody th[scope="row"] a.portal-list__record-link'),
      ).not.toBeNull();

      // And the settings command still goes where it went, so the two destinations stay distinct.
      expect(
        host().querySelector<HTMLAnchorElement>('a.portal-list__row-command')?.getAttribute('href'),
      ).toBe('/portals/31/settings');
    });

    it('targets the record route with the identifier untouched, including -1 and 0', () => {
      // Both markers are legitimate portal identifiers: `Portals.PortalID` is `[int] IDENTITY (-1, 1) NOT
      // NULL`, so the first portal is numbered -1 and the second 0, and -1 is simultaneously the legacy
      // absent-integer marker.
      settleFirstPage([
        portalRow({ portalId: FIRST_PORTAL_ID }),
        portalRow({ portalId: SECOND_PORTAL_ID }),
      ]);

      const links: Record<number, (string | number)[]> =
        member<() => Record<number, (string | number)[]>>('recordLinks')();

      expect(links[FIRST_PORTAL_ID]).toEqual(['/portals', -1]);
      expect(links[SECOND_PORTAL_ID]).toEqual(['/portals', 0]);

      const hrefs: readonly string[] = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.portal-list__record-link'),
      ).map((anchor) => anchor.getAttribute('href') ?? '');

      expect(hrefs).toEqual(['/portals/-1', '/portals/0']);
    });
  });

  // THE HEADER ACTIONS

  describe('the header actions', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    it('offers the create action, targeting the create route', () => {
      const action: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'app-page-header a.portal-list__action',
      );

      expect(action?.textContent?.trim()).toBe('Add New Portal');
      // targeted the signup page; the target here is the create route of this feature.
      expect(action?.getAttribute('href')).toBe('/portals/new');
    });

    it('publishes no portal-template action, which has no endpoint behind it', () => {
      const painted: string = host().textContent ?? '';

      expect(painted).not.toContain('Export Portal Template');
      expect(painted).not.toContain('Template');
    });

    // MIGRATION: the bulk expired-portal deletion is withheld. `Portals.ascx.vb` published an action reading
    // the local `DeleteExpired.Action` value, guarded only by a scripted confirmation carrying the plural
    // global wording `DeleteItems.Confirm`, and reaching `DeleteExpiredPortals` - which iterated the expired
    // listing and destroyed an unbounded number of portals from one click, with no per-row confirmation and
    // no way to review the set first. The API exposes no bulk operation on any resource, so removal is
    // per-portal and each one is confirmed on its own row.
    it('publishes no bulk expired-portal deletion, and never the plural confirmation', () => {
      const painted: string = host().textContent ?? '';

      expect(painted).not.toContain('Delete Expired Portals');
      expect(painted).not.toContain('Are You Sure You Wish To Delete These Items?');
    });

    it('publishes exactly one header action', () => {
      // A count, so an action added without a corresponding endpoint fails here even if its wording is not
      // one of the two named above.
      expect(queryAll<Element>('app-page-header a, app-page-header button').length).toBe(1);
    });
  });

  // SENTINEL DISCIPLINE

  describe('sentinel discipline', () => {
    it('renders a portal identifier of -1 and of 0 as received', () => {
      settleFirstPage([
        portalRow({ portalId: FIRST_PORTAL_ID, portalName: 'Seed Portal' }),
        portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Second Portal' }),
      ]);

      const text: string = host().textContent ?? '';

      expect(text).toContain('-1');
      expect(text).toContain('Seed Portal');
      expect(text).toContain('Second Portal');
    });

    it('targets the settings route with the identifier untouched, including -1 and 0', () => {
      settleFirstPage([
        portalRow({ portalId: FIRST_PORTAL_ID }),
        portalRow({ portalId: SECOND_PORTAL_ID }),
      ]);

      // Read through the precomputed lookup the template indexes, so the assertion exercises the same path
      // the rendered link takes. A NEGATIVE key is a legitimate key here.
      const links: Record<number, (string | number)[]> =
        member<() => Record<number, (string | number)[]>>('editSettingsLinks')();

      expect(links[FIRST_PORTAL_ID]).toEqual(['/portals', -1, 'settings']);
      expect(links[SECOND_PORTAL_ID]).toEqual(['/portals', 0, 'settings']);
    });

    it('renders the settings target for a portal keyed -1 and for one keyed 0', () => {
      settleFirstPage([
        portalRow({ portalId: FIRST_PORTAL_ID }),
        portalRow({ portalId: SECOND_PORTAL_ID }),
      ]);

      const hrefs: readonly string[] = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.portal-list__row-command'),
      ).map((anchor) => anchor.getAttribute('href') ?? '');

      expect(hrefs).toContain('/portals/-1/settings');
      expect(hrefs).toContain('/portals/0/settings');
    });

    it('hands the same link instance back on a later change-detection pass', () => {
      settleFirstPage([portalRow({ portalId: FIRST_PORTAL_ID })]);

      const before: (string | number)[] | undefined = member<
        () => Record<number, (string | number)[]>
      >('editSettingsLinks')()[FIRST_PORTAL_ID];
      fixture.detectChanges();
      fixture.detectChanges();
      const after: (string | number)[] | undefined = member<
        () => Record<number, (string | number)[]>
      >('editSettingsLinks')()[FIRST_PORTAL_ID];

      // Identity, not equality. A link array rebuilt per row per pass would satisfy `toEqual` and fail
      // this, and it is identity that decides whether the router re-parses a target that has not changed -
      // once per row, on every pass, under push change detection.
      expect(after).toBe(before);
    });

    it('answers the absent-integer marker in the two tallies with a mark, never with minus one', () => {
      // ⚠ THIS REPLACES A FACT THAT PINNED THE MARKER BEING PAINTED AS `-1`, AND THE COLLISION IS WHY. That
      // rendering is only defensible in isolation: in THIS grid `Portals.PortalID` is `IDENTITY(-1,1)`, so
      // minus one is a real portal identifier and the first column paints it verbatim - correctly.
      settleFirstPage([portalRow({ portalId: -1, users: -1, pages: -1 })]);

      const cells: readonly string[] = textOf('tbody td,tbody th');

      expect(cells.filter((cell) => cell === '-1').length)
        .withContext('the identifier still paints minus one, because there it is real')
        .toBe(1);

      // ⚠ THIS COUNTS THREE WHERE IT ONCE COUNTED TWO, AND THE THIRD IS THE POINT — QA-15. The row this
      // fixture builds carries no expiry either, and that cell used to render entirely empty: no text, no
      // children, nothing announced. Three absent values in this row now render the SAME way, which is the
      // whole claim of the shared convention.
      expect(cells.filter((cell) => cell.startsWith('\u2014')).length)
        .withContext('both tallies and the absent expiry paint the shared mark')
        .toBe(3);

      // The mark is decorative and the words are the content, so the state reaches a screen reader — and the
      // wording is now the SHARED wording rather than this screen's own, which is what makes an absent tally
      // here and an absent period on the role listing announce identically.
      expect(textOf('app-absent-value .absent-value__description')).toEqual([
        'not recorded',
        'not recorded',
        'not recorded',
      ]);
      expect(
        queryAll<HTMLElement>('tbody td app-absent-value span[aria-hidden="true"]').map(
          (node) => node.textContent,
        ),
      ).toEqual(['\u2014', '\u2014', '\u2014']);
    });

    /**
     * Pf-D1 / Pf-P12. `Website/admin/Portal/portals.ascx:L44-L46` declared Users, Pages and DiskSpace as
     * bare `dnn:textcolumn` data fields, so LEGACY printed `-1` in all three. The target had already chosen
     * the dash marker for two of them and left the third printing the sentinel, which made one row state
     * the same absence two different ways - and made `-1` read as a real, negative allowance.
     */
    it('marks an absent disk allowance the same way it marks an absent tally', () => {
      // ⚠ AN EXPIRY IS GIVEN DELIBERATELY, so this row carries exactly the two absences the case is about.
      // An absent expiry renders the same shared marker - which is its own contract, pinned by the case above
      // - and leaving it absent here would make this case count that third absence too and say nothing
      // clearer for it.
      settleFirstPage([
        portalRow({ users: 4, pages: -1, hostSpace: -1, expiryDate: '2030-06-30T00:00:00Z' }),
      ]);

      // Two absences on ONE row, and they must now read identically. Asserted through the SHARED
      // absent-value component, which is the one idiom every listing reports absence with - this listing
      // once carried a local span of its own beside it, and two idioms in one row is the defect.
      expect(textOf('app-absent-value .absent-value__description'))
        .withContext('one idiom, stated twice, rather than two idioms')
        .toEqual(['not recorded', 'not recorded']);

      const cells: readonly string[] = textOf('tbody td,tbody th');

      expect(cells.some((cell) => cell.includes('-1')))
        .withContext('the sentinel never reaches the page as a number')
        .toBeFalse();
      expect(cells[7]).withContext('the disk cell carries the mark and its words').toContain('\u2014');
    });

    it('paints a real disk allowance verbatim, including nought', () => {
      settleFirstPage([portalRow({ hostSpace: 0 })]);

      // ⚠ NOUGHT IS A REAL ALLOWANCE AND MUST NOT JOIN THE SENTINEL. Only `-1` is the marker, so a test
      // written as `if (!hostSpace)` would pass every other case here and quietly hide a genuine zero.
      expect(textOf('tbody td,tbody th')[7]).toBe('0');

      // Scoped to the DISK cell rather than to the row: this fixture states no expiry, and an absent expiry
      // renders the shared marker by its own contract, so a row-wide count would be measuring that instead.
      expect(queryAll<HTMLElement>('tbody td,tbody th')[7].querySelector('app-absent-value'))
        .withContext('a real allowance paints no absence marker')
        .toBeNull();
    });

    it('keeps the disk column orderable after the change of column kind', () => {
      settleFirstPage([portalRow()]);

      const byKey = new Map(columns().map((column) => [column.key, column]));

      // `SortableFields.Portals` admits `hostSpace`, so losing this would remove an ordering the endpoint
      // still offers - the silent kind of regression a change of column kind invites.
      expect(byKey.get('hostSpace')?.sortable).toBeTrue();
    });

    it('paints a real tally verbatim, including nought', () => {
      settleFirstPage([portalRow({ users: 0, pages: 7 })]);

      const cells: readonly string[] = textOf('tbody td');

      expect(cells).toContain('0');
      expect(cells).toContain('7');

      // NOUGHT IS A REAL TALLY AND MUST NOT BE MARKED ABSENT. The only absent value in this row is its
      // expiry, so exactly ONE shared absent value is rendered — a second would mean a real nought had been
      // swallowed, which is the precise failure `if (!tally)` produces and every other expectation here
      // passes through.
      expect(queryAll<Element>('tbody app-absent-value').length).toBe(1);
    });

    it('drops no row and coerces no identifier across the whole sentinel range', () => {
      // The highest-value expectation in this file. Each of `if (id)`, `id > 0` and `id ?? -1` compiles
      // cleanly, passes every other expectation here, and silently loses a row or renames a portal.
      settleFirstPage([
        portalRow({ portalId: FIRST_PORTAL_ID, portalName: 'Seed Portal' }),
        portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Second Portal' }),
        portalRow({ portalId: 1, portalName: 'Third Portal' }),
      ]);

      expect(queryAll<Element>('tbody tr').length).toBe(3);

      // The identifier is column three, so every third-of-ten cell in body order.
      const cells: readonly string[] = textOf('tbody td,tbody th');
      const identifiers: readonly string[] = cells.filter((_cell, index) => index % 10 === 2);

      expect(identifiers).toEqual(['-1', '0', '1']);

      // And the row order is the server's, not a re-sort that a numeric coercion invited.
      expect(textOf('tbody td,tbody th').filter((_cell, index) => index % 10 === 3)).toEqual([
        'Seed Portal',
        'Second Portal',
        'Third Portal',
      ]);
    });
  });

  // FORMATTING

  // ⚠ MINOR — THE HOSTING FEE
  // Its own block, and deliberately NOT inside `formatting`, because these two specifications need to
  // choose what the FIRST page holds.
  describe('the hosting fee', () => {
    // ⚠ MINOR (money differentiation) — THIS SPECIFICATION WAS REWRITTEN BECAUSE THE COLUMN CHANGED SHAPE.
    it('paints the fee with exactly two decimals and no group separator', () => {
      settleFirstPage([
        portalRow({ portalId: 41, hostFee: 0 }),
        portalRow({ portalId: 42, hostFee: 9.5 }),
        portalRow({ portalId: 43, hostFee: 1234.5 }),
      ]);

      const fees: readonly string[] = feeCells();

      expect(fees[0]).toBe('0.00');
      expect(fees[1]).toBe('9.50');
      // No group separator, unlike the site-settings screen's own helper.
      expect(fees[2]).toBe('1234.50');
    });

    // ⚠ THE ROW ASSERTION WAS NARROWED, AND THE REASON IS THE POINT OF THE SPECIFICATION. It counted
    // `tbody tr` and required zero, which passed for the wrong reason: the table was not rendered at all
    // on a failure, so the screen fell through to "No portals match the current filter." - asserting the
    // site had none when the read had in fact returned one row this client could not decode. The table
    // shell is now kept and states that it could not read its contents, which puts exactly ONE row in
    // the body: the full-width placeholder. So the thing to require is that no row is painted FROM THE
    // PAGE, counted as data cells, not that the body is bare.
    it('refuses a page whose fee is not a finite number rather than painting a row from it', () => {
      settleFirstPage([portalRow({ portalId: 44, hostFee: Number.NaN })]);

      expect(host().querySelectorAll('tbody tr .data-table__cell').length)
        .withContext('no row is painted from an unusable page')
        .toBe(0);
      expect(host().querySelector<HTMLElement>('app-error-banner'))
        .withContext('and the failure is reported through the shared banner')
        .not.toBeNull();
      expect(host().textContent ?? '')
        .withContext('while the grid disclaims its contents instead of claiming there are none')
        .toContain('could not be read');
      expect(host().querySelector('app-empty-state'))
        .withContext('so the empty surface must not stand in for the failure')
        .toBeNull();
    });

    // ⚠ MINOR (money differentiation) — a fee below zero is marked; zero and positive are not.
    it('marks a negative fee and leaves zero and positive fees unmarked', () => {
      settleFirstPage([
        portalRow({ portalId: 51, hostFee: -125.5 }),
        portalRow({ portalId: 52, hostFee: 0 }),
        portalRow({ portalId: 53, hostFee: 4321.99 }),
      ]);

      // Runtime measurement found `-125.50`, `0.00` and `4321.99` sharing colour, weight and size, so a
      // loss was distinguishable only by a single minus glyph. EXACTLY ONE of the three is marked.
      const marked = host().querySelectorAll<HTMLElement>('.portal-list__fee--negative');

      expect(marked.length).withContext('only the negative fee is marked').toBe(1);
      expect(marked[0]?.textContent?.trim()).toBe('-125.50');

      // The cue is not colour alone (WCAG 1.4.1): the sign is also stated in words, announced and
      // unpainted, which is why the cell's own text content carries both.
      const qualifiers = host().querySelectorAll<HTMLElement>('.portal-list__fee-qualifier');

      expect(qualifiers.length).toBe(1);
      expect(qualifiers[0]?.textContent?.trim()).toBe('negative');

      // Every value is painted unchanged - this is a disclosure, not a coercion. `sitesettings.ascx`
      // gives the fee a currency comparison with no lower bound, so a negative fee is legal and stays so.
      expect(feeCells()[0]).toContain('-125.50');
      expect(feeCells()[1]).toBe('0.00');
      expect(feeCells()[2]).toBe('4321.99');
    });

    /**
     * R7 — THE QUALIFIER MUST BE SEPARATED FROM THE FIGURE IT QUALIFIES.
     *
     * ⚠ THE SIBLING SPEC ABOVE COULD NOT CATCH THIS, AND THE REASON IS INSTRUCTIVE: it asserts the figure
     * with `toContain` and the qualifier through its own element, so both are satisfied whether or not
     * anything separates them. The defect lives in exactly the gap those two assertions leave.
     *
     * Angular compiles with `preserveWhitespaces` disabled, and that pass DELETES a whitespace-only text
     * node standing between two elements — which is precisely what sat between the figure's span and the
     * qualifier's. The qualifier is `visually-hidden`, so nothing was wrong on screen; the damage was
     * confined to the accessibility tree, where the cell announced the single token `-99.99negative`.
     *
     *
     * ⚠ THE ASSERTION PINS U+00A0 RATHER THAN "SOME WHITESPACE", AND THAT PRECISION IS ITSELF THE FIX. An
     * ordinary space reaches the DOM but is not always handed to a screen reader: where Angular's anchor
     * comments leave the separator an isolated whitespace-only text node, Blink builds no text box for it, so
     * it never becomes a StaticText node and name-from-contents concatenates regardless. That was measured
     * through the CDP Accessibility domain in two independent browsers on the sibling role-assignment screen,
     * where a cell whose DOM `textContent` read "9/20/2027 Active" computed an accessible name of
     * "9/20/2027Active". A spec asserting only `\s` would accept the very character that regressed.
     *
     * Contrast the expiry column, which reads correctly as `1/1/2020 Expired` for one incidental reason
     * only: its interpolation and the newline after it share ONE text node, so a collapsed space survives.
     */
    it('separates a negative fee from the word that qualifies it', () => {
      settleFirstPage([portalRow({ portalId: 51, hostFee: -99.99 })]);

      const cell: string = feeCells()[0] ?? '';

      expect(cell)
        .withContext('the figure and the word are announced as two tokens, not one')
        .toMatch(/-99\.99\u00a0negative/u);
      expect(cell)
        .withContext('and never run together, which is what a screen reader measured')
        .not.toMatch(/-99\.99negative/u);
    });

    /*
     * ⚠ NO COMPANION SPEC ASSERTS THE SEPARATOR'S PLACEMENT, AND THAT IS DELIBERATE. The obvious one to
     * write - that an unqualified row carries no stray separator - CANNOT FAIL: `feeCells()` trims, and
     * edge whitespace inside a cell is collapsed away by the renderer and stripped by accessible-name
     * computation, so an unconditionally emitted separator is unobservable by any instrument. Asserting it
     * would have been asserting an incidental value while appearing to protect an invariant. That the
     * qualifier itself never widens onto a zero or positive fee IS real, and the sibling spec above already
     * proves it by counting exactly one qualifier across a mixed page.
     */
  });

  describe('formatting', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    // ⚠ THIS REPLACES A FACT THAT PINNED AN EMPTY CELL, AND THE EMPTINESS WAS THE DEFECT — QA-15. The legacy
    // formatter seeded its result with the empty string and formatted the date only when it was not the
    // absent-date marker, and the earlier fact reproduced that faithfully. What it reproduced was a cell with
    // no text, no children and nothing announced, on the six-of-seven rows of a real installation that carry
    // no expiry — indistinguishable from a cell that had failed to render, and completely silent to a screen
    // reader. Both absent inputs now render the SHARED absent value: the same mark, colour and wording as an
    // absent tally beside it and an absent period on the role listing.
    it('renders an absent expiry and the legacy marker date as the shared absent value', () => {
      const rendered = (expiryDate: string | null): string => {
        settleFirstPage([portalRow({ expiryDate })]);

        const cells: readonly string[] = textOf('tbody td,tbody th');

        return cells[cells.length - 1] ?? 'MISSING';
      };

      invoke<void>('onRetry');
      expect(rendered(null)).toBe('\u2014not recorded');
      expect(queryAll<Element>('tbody app-absent-value').length).toBeGreaterThanOrEqual(1);

      invoke<void>('onRetry');
      expect(rendered(NULL_DATE)).toBe('\u2014not recorded');

      // And the three wrong answers a naive renderer gives, named so a regression is unambiguous rather than
      // merely "not empty".
      const painted: string = host().textContent ?? '';
      expect(painted).not.toContain('01/01/0001');
      expect(painted).not.toContain('1/1/1');
      expect(painted).not.toContain('N/A');
      expect(painted).not.toContain('Invalid Date');
    });

    it('renders a real expiry in short-date form', () => {
      // The legacy cell called `ToShortDateString` once the marker test had passed, so a real expiry is a
      // DATE and never a timestamp.
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: REAL_EXPIRY_DATE })]);

      const cells: readonly string[] = textOf('tbody td,tbody th');

      expect(cells[cells.length - 1]).toBe('3/15/2027');
      // No time component, and nothing that would betray a local-timezone shift of the day.
      expect(cells[cells.length - 1]).not.toContain(':');
      expect(cells[cells.length - 1]).not.toContain('3/14/2027');
    });

    it('qualifies a lapsed term in words, and leaves a running one unqualified', () => {
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: '2020-01-15T00:00:00' })]);

      expect(textOf('.portal-list__expired')).toEqual(['Expired']);
      expect((textOf('tbody td').at(-1) ?? '')).toContain('1/15/2020');

      // ⚠ THE TWO VALUES ARE SEPARATED BY A REAL SPACE CHARACTER, not by a margin. Measured before the
      // separator was added: the cell's text content was the single run "6/30/2021Expired", so the date and
      // its qualifier were one word to a screen reader, to a copy-paste and to any text extraction — while a
      // sighted reader saw a four-pixel gap that came from `margin-inline-start` and exists only in paint.
      const painted: string = (
        queryAll<HTMLElement>('tbody td').at(-1)?.textContent ?? ''
      ).trim();

      expect(painted).toBe('1/15/2020 Expired');

      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: '2099-12-31T00:00:00' })]);

      expect(textOf('.portal-list__expired')).toEqual([]);
      expect((textOf('tbody td').at(-1) ?? '')).toContain('12/31/2099');
    });

    it('asserts no state where the legacy asserted none: an absent expiry gets no qualifier', () => {
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: null })]);
      expect(textOf('.portal-list__expired')).toEqual([]);

      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: NULL_DATE })]);
      expect(textOf('.portal-list__expired')).toEqual([]);

      // An unparseable value is treated the same way, so the qualifier and the cell can never disagree about
      // whether there is an expiry at all - both read the one parser.
      //
      // ⚠ THE CELL EXPECTATION CHANGED FROM EMPTY TO THE SHARED ABSENT VALUE — QA-15. That an unparseable
      // value carries NO QUALIFIER is the claim of this case and it is unchanged; what changed is that the
      // cell beside the missing qualifier now says "there is no expiry recorded" rather than saying nothing
      // at all. The two verdicts still come from one parser, which is what this case guards.
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: 'not-a-date' })]);
      expect(textOf('.portal-list__expired')).toEqual([]);
      expect((textOf('tbody td').at(-1) ?? '')).toBe('\u2014not recorded');
    });

    it('puts today itself on the expired side, because the legacy comparison is strict', () => {
      const midnightToday: Date = new Date();
      midnightToday.setUTCHours(0, 0, 0, 0);

      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: midnightToday.toISOString() })]);

      expect(textOf('.portal-list__expired')).toEqual(['Expired']);
    });
  });

  // HOST NAMES

  describe('host names', () => {
    /**
     * An anchor's visible text, with the visually-hidden new-context phrase removed. The phrase is
     * deliberately part of the anchor's ACCESSIBLE NAME - that is how the change of context is announced
     * - so `textContent` is no longer the host name on its own.
     *
     * @param anchor The anchor to read, or `undefined` when the row rendered none.
     * @returns The trimmed visible text, or the empty string.
     */
    function visibleLabel(anchor: HTMLAnchorElement | undefined): string {
      if (anchor === undefined) {
        return '';
      }

      const clone = anchor.cloneNode(true) as HTMLAnchorElement;
      clone.querySelector('.portal-list__new-context')?.remove();

      return (clone.textContent ?? '').trim();
    }

    it('renders one anchor per host name, prefixing a scheme only when none is stated', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['localhost:4200', 'https://secure.example', ''],
        }),
      ]);

      const anchors: readonly HTMLAnchorElement[] = queryAll<HTMLAnchorElement>(
        '.portal-list__alias > a',
      );

      expect(anchors.length).toBe(2);
      // ⚠ MAJOR (CWE-319) — HTTPS, WHERE THIS ASSERTED `http://`. A bare host name is the normal stored
      // form on this column, so the previous scheme downgraded practically every link on the screen to
      // cleartext even from a TLS-served administration session.
      expect(anchors[0]?.getAttribute('href')).toBe('https://localhost:4200');
      expect(anchors[1]?.getAttribute('href')).toBe('https://secure.example');
      expect(visibleLabel(anchors[0])).toBe('localhost:4200');
    });

    it('gives a bare host name TLS and never rewrites an explicit http one', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: [
            'bare.example',
            'bare-with-port.example:8080',
            'http://insecure.example',
            'https://secure.example',
          ],
        }),
      ]);

      const hrefs: readonly string[] = queryAll<HTMLAnchorElement>('.portal-list__alias > a').map(
        (anchor) => anchor.getAttribute('href') ?? '',
      );

      expect(hrefs).toEqual([
        // Supplied: the secure scheme, for the two values that state none.
        'https://bare.example',
        'https://bare-with-port.example:8080',
        // Stated: left exactly as the operator stored it, in both directions.
        'http://insecure.example',
        'https://secure.example',
      ]);

      // Stated as a negative too, because this is the regression that matters: no link on the screen may
      // acquire a cleartext scheme this screen invented.
      expect(hrefs.filter((href) => href.startsWith('http://')))
        .withContext('the only cleartext link is the one the operator asked for')
        .toEqual(['http://insecure.example']);
    });

    it('renders a host name whose scheme is not http as INERT TEXT rather than as a link', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: [
            'mailto:host@example.com',
            'javascript:alert(1)',
            'data:text/html,<script>alert(1)</script>',
            'vbscript:msgbox(1)',
            'file:///etc/passwd',
            '\\\\fileserver\\share',
            '~/app-relative',
          ],
        }),
      ]);

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a'))
        .withContext('not one of these may be navigable')
        .toHaveSize(0);

      const shown = queryAll<HTMLElement>('.portal-list__alias').map((node) =>
        (node.textContent ?? '').trim(),
      );

      expect(shown).toContain('mailto:host@example.com');
      expect(shown).toContain('javascript:alert(1)');
      expect(shown).toContain('vbscript:msgbox(1)');
      expect(shown).toContain('file:///etc/passwd');
      expect(shown).toContain('~/app-relative');
    });

    it('opens a host name in a new context, and says so in the accessible name', () => {
      // THE `target` IS THE HALF TO ADD, NOT THE `rel` THE HALF TO DELETE. The address leaves this
      // application entirely, and the session's token is held in memory alone, so a same-tab navigation
      // signs the operator out of the console they were administering.
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, aliases: ['localhost:4200'] }),
      ]);

      const anchor: HTMLAnchorElement | undefined = queryAll<HTMLAnchorElement>(
        '.portal-list__alias > a',
      )[0];

      expect(anchor).withContext('a host name must render an anchor').not.toBeUndefined();
      expect(anchor?.getAttribute('target')).toBe('_blank');
      expect(anchor?.getAttribute('rel')).toBe('noopener noreferrer');

      // The change of context is ANNOUNCED rather than left to be discovered: a link that behaves unlike
      // every other link on the screen has to say so, and the phrase is hidden visually so nothing moves.
      const context: HTMLElement | null = anchor?.querySelector('.portal-list__new-context') ?? null;
      expect(context).withContext('the new context must be stated').not.toBeNull();
      expect((context?.textContent ?? '').trim()).toBe('(opens in a new tab)');
      expect((anchor?.textContent ?? '').trim()).toBe('localhost:4200 (opens in a new tab)');
    });

    it('renders nothing for a portal with no host names', () => {
      settleFirstPage([portalRow({ aliases: [] })]);

      expect(
        host().querySelectorAll('.portal-list__alias').length,
      ).toBe(0);
    });

    // HOSTILE HOST NAMES. `dbo.PortalAlias.HTTPAlias` is an operator-supplied string with no scheme
    // constraint on it, so a host name is UNTRUSTED INPUT that reaches an anchor's `href` and its text.

    it('renders a bare javascript scheme with no anchor at all, and shows it as text', () => {
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, aliases: ['javascript:alert(1)'] }),
      ]);

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a'))
        .withContext('nothing navigable')
        .toHaveSize(0);

      const cell: HTMLElement | undefined = queryAll<HTMLElement>('.portal-list__alias')[0];

      expect((cell?.textContent ?? '').trim()).toBe('javascript:alert(1)');
      expect(cell?.children.length).withContext('text, not markup').toBe(0);
    });

    it('renders a javascript scheme that carries the address marker with no anchor either', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['javascript://comment%0aalert(1)'],
        }),
      ]);

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);
      expect(queryAll<HTMLElement>('.portal-list__alias').map((node) => (node.textContent ?? '').trim()))
        .toContain('javascript://comment%0aalert(1)');
    });

    it('renders a data scheme inertly, as text and with no address', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['data:text/html,<script>alert(1)</script>'],
        }),
      ]);

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);

      const cell: HTMLElement | undefined = queryAll<HTMLElement>('.portal-list__alias')[0];

      // The script text is TEXT: the cell holds no child element at all, so nothing was parsed as markup on
      // its way to the DOM.
      expect(cell?.querySelector('script')).toBeNull();
      expect(cell?.children.length).toBe(0);
      expect(cell?.textContent).toContain('<script>alert(1)</script>');
    });

    it('renders a markup label as text, with no element parsed out of it', () => {
      // A host name is operator-supplied, so it is treated the same way.
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['<img src=x onerror=alert(1)>host.example'],
        }),
      ]);

      const cell: HTMLElement | undefined = queryAll<HTMLElement>('.portal-list__alias')[0];

      // Prefixing yields `https://<img …>host.example`, which is not a host, so no address is projected
      // and the value is shown as it was stored.
      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);
      expect(cell?.querySelector('img'))
        .withContext('interpolation escapes it; nothing is parsed as an element')
        .toBeNull();
      expect(cell?.children.length).toBe(0);
      expect(cell?.textContent).toContain('<img src=x onerror=alert(1)>host.example');
    });

    it('shows a network share and an application-relative path exactly as stored, unlinked', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['\\\\fileserver\\portals', '~/portals/dnn'],
        }),
      ]);

      const shown: readonly string[] = queryAll<HTMLElement>('.portal-list__alias').map((node) =>
        (node.textContent ?? '').trim(),
      );

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);
      expect(shown).toHaveSize(2);
      expect(shown).toContain('\\\\fileserver\\portals');
      expect(shown).toContain('~/portals/dnn');
    });

    it('carries a control character into the label as text and into no address at all', () => {
      // C0 controls and DEL can be stored in the column, and a control embedded in a scheme is the classic
      // way of smuggling one past a naive prefix test. Here there is no prefix test to pass: the prefixed
      // value has to parse as an http address, and this one does not.
      const hostile = 'java\u0000script\u0009:alert(1)\u007f';

      settleFirstPage([portalRow({ portalId: SECOND_PORTAL_ID, aliases: [hostile] })]);

      const cell: HTMLElement | undefined = queryAll<HTMLElement>('.portal-list__alias')[0];

      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);
      expect(cell?.children.length).toBe(0);
      expect(cell?.textContent).toContain('alert(1)');
    });

    it('renders NOTHING for an empty host name, and the handling is stable across rows', () => {
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, aliases: ['', 'one.example', '', ''] }),
      ]);

      const anchors: readonly HTMLAnchorElement[] = queryAll<HTMLAnchorElement>(
        '.portal-list__alias > a',
      );

      expect(anchors.length).toBe(1);
      expect(anchors[0]?.getAttribute('href')).toBe('https://one.example');
      expect(anchors[0]?.getAttribute('href'))
        .withContext('never the empty attribute the legacy screen rendered')
        .not.toBe('');
      expect(queryAll<HTMLElement>('.portal-list__alias').length).toBe(1);
    });

    it('uses no innerHTML anywhere in the rendered host-name cell', () => {
      settleFirstPage([
        portalRow({
          portalId: SECOND_PORTAL_ID,
          aliases: ['<b>bold</b>.example', 'plain.example'],
        }),
      ]);

      const cells: readonly HTMLElement[] = queryAll<HTMLElement>('.portal-list__alias');

      expect(cells).withContext('both host names are shown').toHaveSize(2);
      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a'))
        .withContext('only the well-formed one is navigable')
        .toHaveSize(1);

      for (const cell of cells) {
        expect(cell.querySelector('b')).withContext('nothing is parsed as markup').toBeNull();

        for (const element of Array.from(cell.querySelectorAll('*'))) {
          expect(['A', 'SPAN'])
            .withContext('only an anchor and its hidden phrase are emitted')
            .toContain(element.tagName);
        }

        const anchor: HTMLAnchorElement | null = cell.querySelector('a');

        if (anchor !== null) {
          const clone = anchor.cloneNode(true) as HTMLAnchorElement;
          clone.querySelector('.portal-list__new-context')?.remove();

          expect(clone.children.length)
            .withContext('the anchor holds its label as text and nothing else')
            .toBe(0);

          for (const node of Array.from(clone.childNodes)) {
            expect(node.nodeType).toBe(Node.TEXT_NODE);
          }
        }
      }
    });

    it('answers a row that is not on the page in hand with no host names', () => {
      settleFirstPage([portalRow({ portalId: 1, aliases: ['one.example'] })]);

      // A row absent from the projection can only mean the page has changed since it was built; an empty
      // list is the safe reading and no identifier is defaulted to reach it.
      expect(
        invoke<readonly unknown[]>('aliasLinks', portalRow({ portalId: 99, aliases: [] })),
      ).toEqual([]);
      expect(
        invoke<readonly unknown[]>('aliasLinks', portalRow({ portalId: 1, aliases: [] })).length,
      ).toBe(1);
    });
  });

  // THE DELETE FLOW

  describe('the delete flow', () => {
    it('keeps the affordance on every row when no tenant is resolved', () => {
      settleFirstPage([portalRow()]);

      // No session is signed in, so no row matches and every row keeps its affordance.
      expect(invoke<boolean>('canDelete', portalRow({ portalId: SECOND_PORTAL_ID }))).toBeTrue();
      expect(invoke<boolean>('canDelete', portalRow({ portalId: FIRST_PORTAL_ID }))).toBeTrue();
    });

    it('withholds the delete affordance from the row for the tenant being browsed', () => {
      // Zero is a real tenant, and the row rule has to work for it: a truthiness test on the identifier
      // would classify it as "no tenant" and leave the affordance in place.
      sessionPortalId.set(SECOND_PORTAL_ID);

      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Browsed Portal' }),
        portalRow({ portalId: FIRST_PORTAL_ID, portalName: 'Other Portal' }),
      ]);

      expect(invoke<boolean>('canDelete', portalRow({ portalId: SECOND_PORTAL_ID }))).toBeFalse();
      expect(invoke<boolean>('canDelete', portalRow({ portalId: FIRST_PORTAL_ID }))).toBeTrue();

      // Withheld from the DOM entirely rather than disabled, because the legacy rule set the control's
      // visibility to false: one row of two offers the command.
      const commands: readonly string[] = textOf('.portal-list__row-command--danger');
      expect(commands.length).toBe(1);
    });

    it('withholds the affordance for a browsed tenant numbered -1', () => {
      // -1 is simultaneously a real tenant and the legacy absent-integer marker, so a comparison against the
      // marker would misclassify this exact case.
      sessionPortalId.set(FIRST_PORTAL_ID);

      settleFirstPage([portalRow({ portalId: FIRST_PORTAL_ID })]);

      expect(invoke<boolean>('canDelete', portalRow({ portalId: FIRST_PORTAL_ID }))).toBeFalse();
      expect(textOf('.portal-list__row-command--danger').length).toBe(0);
    });

    it('disables the affordance while a deletion is in flight', () => {
      settleFirstPage([portalRow({ portalId: 5 }), portalRow({ portalId: 6 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 5 }));
      invoke<void>('onDeletionConfirmed');
      fixture.detectChanges();

      const commands: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        '.portal-list__row-command--danger',
      );
      expect(commands.length).toBeGreaterThan(0);
      for (const command of commands) {
        expect(command.disabled).toBeTrue();
      }

      http.expectOne(`${PORTALS_URL}/5`).flush(null, { status: 204, statusText: 'No Content' });
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([]));
      fixture.detectChanges();
    });

    it('opens the confirmation carrying the global resource wording', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('requestDeletion', portalRow());
      fixture.detectChanges();

      const dialog: HTMLElement | null = host().querySelector<HTMLElement>('app-confirm-dialog');

      expect(dialog).not.toBeNull();
      expect(dialog?.textContent).toContain('Are You Sure You Wish To Delete This Item?');

      invoke<void>('onDeletionCancelled');
      fixture.detectChanges();
      expect(host().querySelector('app-confirm-dialog')).toBeNull();
    });

    it('names the record it will destroy, beside that wording', () => {
      // ⚠ THE MEASURED DEFECT. The body was the bare legacy question and named nothing, while this dialog
      // is modal and PHYSICALLY COVERS the listing - the row being destroyed included.
      settleFirstPage([portalRow({ portalName: 'Contoso Intranet' })]);

      invoke<void>('requestDeletion', portalRow({ portalName: 'Contoso Intranet' }));
      fixture.detectChanges();

      expect(
        (host().querySelector('.confirm-dialog__message')?.textContent ?? '').trim(),
      ).toBe('Are You Sure You Wish To Delete This Item? Contoso Intranet');
    });

    it('deletes, announces the legacy success wording, and re-reads the page', () => {
      settleFirstPage([portalRow({ portalId: 5 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 5 }));
      invoke<void>('onDeletionConfirmed');

      const removal: TestRequest = http.expectOne(`${PORTALS_URL}/5`);
      expect(removal.request.method).toBe('DELETE');
      removal.flush(null, { status: 204, statusText: 'No Content' });

      // The store re-reads the page, reproducing the legacy BindData() call.
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([]));
      fixture.detectChanges();

      const queued: readonly AppNotification[] = notifications.notifications();
      expect(queued.length).toBe(1);
      expect(queued[0]?.severity).toBe('success');
      expect(queued[0]?.message).toBe('Portal deleted successfully');
    });

    it('announces the last-portal refusal at error severity on a conflict', () => {
      settleFirstPage([portalRow({ portalId: 7 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 7 }));
      invoke<void>('onDeletionConfirmed');

      // The live refusal, complete: the code the server publishes, the title that belongs to the status,
      // the authored sentence, and both identifiers. `last_remaining` is the token the shared status
      // translator reads to answer 409.
      http.expectOne(`${PORTALS_URL}/7`).flush(
        problemOf(
          409,
          'portal.last_remaining',
          'You Can Not Delete The Last Portal In Your Database',
        ),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const queued: readonly AppNotification[] = notifications.notifications();
      expect(queued.length).toBe(1);
      expect(queued[0]?.severity).toBe('error');
      expect(queued[0]?.message).toBe(
        'You Can Not Delete The Last Portal In Your Database',
      );

      // The row survives, because nothing was deleted. A screen that removed it optimistically would show
      // the operator an empty installation it still has.
      expect(host().querySelectorAll('tbody tr').length).toBe(1);
    });

    it('announces a permission refusal at WARNING severity, never at error', () => {
      settleFirstPage([portalRow({ portalId: 9 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 9 }));
      invoke<void>('onDeletionConfirmed');

      // `auth.not_permitted` is the code the authorisation result handler publishes for every refused policy
      // in this API; the leading break tag is a legacy wording artefact that the shared summariser strips.
      http.expectOne(`${PORTALS_URL}/9`).flush(
        problemOf(
          403,
          'auth.not_permitted',
          '<br>You do not have permission to perform this action.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const queued: readonly AppNotification[] = notifications.notifications();
      expect(queued.length).toBe(1);
      // The legacy access-denied surface used a warning, not an error: the system is working exactly as
      // configured.
      expect(queued[0]?.severity).toBe('warning');
      // The leading break tag is stripped rather than painted as literal characters.
      expect(queued[0]?.message).toBe('You do not have permission to perform this action.');
    });

    it('does nothing when a confirmation is settled with no row pending', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('onDeletionConfirmed');
      fixture.detectChanges();

      // No request is issued, which the afterEach verification also enforces.
      expect(notifications.notifications().length).toBe(0);
    });

    it('announces nothing when a confirmation is cancelled', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('requestDeletion', portalRow());
      invoke<void>('onDeletionCancelled');
      fixture.detectChanges();

      expect(notifications.notifications().length).toBe(0);
    });

    it('issues NO request at all when a confirmation is cancelled', () => {
      settleFirstPage([portalRow({ portalId: 11 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 11 }));
      fixture.detectChanges();
      expect(host().querySelector('app-confirm-dialog')).not.toBeNull();

      invoke<void>('onDeletionCancelled');
      fixture.detectChanges();

      // Stated positively rather than left to the teardown verification, so a failure names the defect - a
      // screen that removed the row before the operator agreed - instead of reporting an unexpected open
      // request from another expectation's teardown.
      http.expectNone(`${PORTALS_URL}/11`);
      http.expectNone((candidate) => isListingRead(candidate));

      // And the row the operator declined to remove is still there.
      expect(queryAll<Element>('tbody tr').length).toBe(1);
    });

    it('opens the confirmation through the shared dialogue rather than a scripted prompt', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('requestDeletion', portalRow());
      fixture.detectChanges();

      const dialog: HTMLDialogElement | null =
        host().querySelector<HTMLDialogElement>('app-confirm-dialog dialog');

      expect(dialog).not.toBeNull();
      // A modal dialogue, which is what confines focus: the platform's own focus trap.
      expect(dialog?.open).toBeTrue();
      expect(dialog?.getAttribute('aria-modal')).toBe('true');
      // Named by its own heading and message rather than by a bare string.
      expect(dialog?.getAttribute('aria-labelledby')).toBeTruthy();
      expect(dialog?.getAttribute('aria-describedby')).toBeTruthy();

      // A nameable cancel affordance, whose wording is the global `cmdCancel.Text` value.
      const buttons: readonly string[] = textOf('app-confirm-dialog button');
      expect(buttons).toContain('Cancel');
      expect(buttons.some((label) => label.includes('Delete'))).toBeTrue();
    });

    it('cancels on Escape, which the scripted prompt it replaces could not do', () => {
      settleFirstPage([portalRow({ portalId: 12 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 12 }));
      fixture.detectChanges();

      const dialog: HTMLDialogElement | null =
        host().querySelector<HTMLDialogElement>('app-confirm-dialog dialog');
      expect(dialog).not.toBeNull();

      // The platform answers Escape on a modal dialogue with its `cancel` event, which the shared component
      // forwards as a cancellation. Dispatched rather than simulated with a key press, because that event
      // IS the platform's Escape contract.
      dialog?.dispatchEvent(new Event('cancel'));
      fixture.detectChanges();

      expect(host().querySelector('app-confirm-dialog')).toBeNull();
      http.expectNone(`${PORTALS_URL}/12`);
      expect(notifications.notifications().length).toBe(0);
      expect(queryAll<Element>('tbody tr').length).toBe(1);
    });
  });

  // Empty, past-the-end and failure surfaces

  describe('the non-grid surfaces', () => {
    // ⚠ THE LAST ASSERTION IS INVERTED, AND THE INVERSION IS THE FIX. It required that the table be
    // DESTROYED when nothing matched, which took the polite `role="status"` region down with it - so
    // narrowing the listing to nothing announced NOTHING, the one transition most in need of a report. The
    // roles and modules listings already rendered their empty state inside the table and kept the region;
    // this screen and the accounts listing did not, which is the inconsistency being closed. The wording and
    // the recovery affordance are unchanged; only their home is.
    it('shows the empty surface with the add action inside the table, so the status survives', () => {
      settleFirstPage([], 0);

      const empty: HTMLElement | null = host().querySelector<HTMLElement>('app-empty-state');

      expect(empty).not.toBeNull();
      expect(empty?.textContent).toContain('No portals match the current filter.');
      expect(empty?.textContent).toContain('Add New Portal');

      expect(host().querySelector('app-data-table'))
        .withContext('the table shell is kept, because the announcing region lives in it')
        .not.toBeNull();
      expect(host().querySelector('app-empty-state')?.closest('table'))
        .withContext('and the panel sits inside that table rather than replacing it')
        .not.toBeNull();

      const status: HTMLElement | null = host().querySelector<HTMLElement>(
        '.data-table__result-status',
      );

      expect(status?.getAttribute('role')).toBe('status');
      expect((status?.textContent ?? '').trim())
        .withContext('and it announces the narrowing rather than staying silent')
        .toBe('No records found.');
    });

    it('distinguishes past-the-end from nothing-matched, offering a way back', () => {
      settleFirstPage([], 40);

      const empty: HTMLElement | null = host().querySelector<HTMLElement>('app-empty-state');

      expect(empty?.textContent).toContain('past the end');
      expect(empty?.textContent).toContain('First page');
      // The add action belongs to the nothing-matched case and must not appear here.
      expect(empty?.textContent).not.toContain('Add New Portal');
    });

    it('returns to the first page from the past-the-end surface', async () => {
      settleFirstPage([portalRow()], 40, 0);
      invoke<void>('onPageChange', 3);
      await settleAddress();
      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '3')
        .flush(pageOf([], 40, 3));
      fixture.detectChanges();

      const back: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        'app-empty-state button',
      );
      expect(back.length).toBe(1);
      back[0]?.click();
      await settleAddress();

      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40));
      fixture.detectChanges();
    });

    it('keeps the pager and its count when the whole match set fits on one page', () => {
      settleFirstPage([portalRow()], 40);
      expect(host().querySelector('app-pagination')).not.toBeNull();
      expect(host().querySelectorAll('app-pagination .pagination__button').length).toBe(4);

      invoke<void>('onRetry');
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()], 1));
      fixture.detectChanges();

      expect(host().querySelector('app-pagination')).not.toBeNull();
      expect(host().querySelector('app-pagination .pagination__status')?.textContent ?? '')
        .withContext('the count survives')
        .toContain('of 1');
      expect(host().querySelectorAll('app-pagination .pagination__button').length)
        .withContext('nowhere to step to')
        .toBe(0);
    });

    it('reports the FIRST read inside the grid, and a later read without blanking it', async () => {
      // ⚠ THIS CASE USED TO ASSERT THAT THE GRID WAS ABSENT DURING THE FIRST READ, because this screen
      // substituted a full-width indicator for the whole table while the other three listings kept their
      // table and let the shared grid draw the indicator in its own placeholder row. One loading shape now
      // serves all four, so the assertion is inverted rather than adapted: the grid is always present, and
      // the indicator lives inside it.
      const spinnerInsidePlaceholder = (): HTMLElement | null =>
        host().querySelector<HTMLElement>('app-data-table td[data-placeholder] app-loading-spinner');

      // Nothing has settled yet, so this is the first read.
      expect(host().querySelector<HTMLElement>('app-data-table'))
        .withContext('the grid owns the waiting state, so it is on screen for it')
        .not.toBeNull();
      expect(spinnerInsidePlaceholder())
        .withContext('the indicator replaces the ROWS, not the table')
        .not.toBeNull();
      expect(host().querySelector<HTMLElement>('app-pagination'))
        .withContext('the pager reports a match set, and no read has produced one yet')
        .toBeNull();

      settleFirstPage([portalRow()], 40);
      expect(host().querySelector<HTMLElement>('app-loading-spinner')).toBeNull();
      expect(host().querySelector<HTMLElement>('app-pagination')).not.toBeNull();

      // A later read keeps the rows on screen rather than blanking the table, and says so visibly.
      invoke<void>('onPageChange', 1);
      await settleAddress();
      expect(host().querySelector<HTMLElement>('app-data-table')).not.toBeNull();
      expect(spinnerInsidePlaceholder())
        .withContext('the rows are kept, so nothing replaces them')
        .toBeNull();
      expect(host().querySelector<HTMLElement>('.data-table__refetch app-loading-spinner'))
        .withContext('the refetch strip is the seen half of the table\u2019s aria-busy')
        .not.toBeNull();

      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '1')
        .flush(pageOf([portalRow()], 40, 1));
      fixture.detectChanges();

      expect(host().querySelector<HTMLElement>('.data-table__refetch')).toBeNull();
    });

    // ⚠ REGRESSION GUARD. A failed read left `portals()` empty, and the branch that chose between the
    // grid and the empty surface was keyed on row count alone - so every failure rendered
    // `app-empty-state` and asserted "No portals match the current filter." over a read that had in fact
    // returned rows this client could not decode. Two things were wrong at once: the screen made a false
    // statement about the data, and the table's own `failed` placeholder was unreachable because the
    // table was not in the view to receive it.
    it('renders the grid shell rather than the empty surface when the read failed', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
        .flush(
          problemOf(500, 'server.unexpected_failure', 'The listing could not be produced.'),
          { status: 500, statusText: 'Internal Server Error' },
        );
      fixture.detectChanges();

      expect(host().querySelector('app-empty-state'))
        .withContext('the empty surface must never stand in for a failure')
        .toBeNull();
      expect(host().textContent ?? '')
        .withContext('and its sentence must not be on screen at all')
        .not.toContain('No portals match the current filter.');

      expect(host().querySelector('app-data-table'))
        .withContext('the table shell is kept so it can disclaim its own contents')
        .not.toBeNull();
      expect(host().textContent ?? '')
        .withContext('which is what the shared placeholder says')
        .toContain('could not be read');

      expect(queryAll<Element>('tbody tr .data-table__cell').length)
        .withContext('no data row is drawn')
        .toBe(0);
      expect(host().querySelector('app-pagination'))
        .withContext('and no range is claimed, because none is known')
        .toBeNull();
    });

    it('restores the empty surface once a retry genuinely returns nothing', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
        .error(new ProgressEvent('error'));
      fixture.detectChanges();
      expect(host().querySelector('app-empty-state')).toBeNull();

      invoke<void>('onRetry');
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([], 0));
      fixture.detectChanges();

      const empty: HTMLElement | null = host().querySelector<HTMLElement>('app-empty-state');
      expect(empty)
        .withContext('a genuinely empty result still gets the empty surface')
        .not.toBeNull();
      expect(empty?.textContent).toContain('No portals match the current filter.');
      expect(host().textContent ?? '')
        .withContext('and the failure wording is gone')
        .not.toContain('could not be read');
    });

    it('reports a failure that carried no problem document through the SAME shared banner', () => {
      http
        .expectOne((candidate) => isListingRead(candidate))
        .error(new ProgressEvent('error'));
      fixture.detectChanges();

      const banner: HTMLElement | null = host().querySelector<HTMLElement>('app-error-banner');

      expect(banner).not.toBeNull();
      expect(banner?.textContent ?? '')
        .withContext('the composed document says the server could not be reached')
        .toContain('could not be reached');

      invoke<void>('onFailureDismissed');
      fixture.detectChanges();
      expect(host().querySelector<HTMLElement>('app-error-banner'))
        .withContext('the live region persists between failures')
        .not.toBeNull();
      expect(host().querySelector<HTMLElement>('app-error-banner [role="alert"]'))
        .withContext('and it keeps its announcement semantics while empty')
        .not.toBeNull();
      expect(host().querySelector<HTMLElement>('app-error-banner .error-banner'))
        .withContext('while the painted banner is gone')
        .toBeNull();
    });

    it('reports a failed listing through the shared banner and can retry', () => {
      http
        .expectOne((candidate) => isListingRead(candidate))
        .flush(
          problemOf(
            500,
            'server.unexpected_failure',
            'An unexpected error occurred while processing the request.',
          ),
          { status: 500, statusText: 'Internal Server Error' },
        );
      fixture.detectChanges();

      const banner: HTMLElement | null = host().querySelector<HTMLElement>('app-error-banner');
      expect(banner).not.toBeNull();
      expect(banner?.textContent).toContain(
        'An unexpected error occurred while processing the request.',
      );

      invoke<void>('onRetry');
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()]));
      fixture.detectChanges();

      // The successful re-read empties the banner; the live region it lives in is permanent. See the
      // note on the dismissal expectation above for why absence is the wrong thing to assert.
      expect(host().querySelector('app-error-banner'))
        .withContext('the live region survives the recovery')
        .not.toBeNull();
      expect(host().querySelector('app-error-banner .error-banner'))
        .withContext('and paints nothing once the read succeeds')
        .toBeNull();
    });
  });

  // THE PAGER

  describe('ordering', () => {
    /** The rendered sort controls, in column order. */
    function sortControls(): readonly HTMLButtonElement[] {
      return queryAll<HTMLButtonElement>('th.data-table__header button.data-table__sort');
    }

    /**
     * One sort control, chosen by the heading it carries rather than by its position.
     *
     * ⚠ POSITION IS NOT A STABLE HANDLE ON THIS GRID, and the specifications below used to rely on it. The
     * shared table hoists a row-header column to the front while its scroll region clips, so a column's
     * INDEX depends on a measurement of the rendered width — which lands a frame after the first paint, and
     * differs between this runner's zero-width host and a real viewport. A specification that clicked index 1
     * was therefore asserting the column order incidentally, and would report an ordering defect when the
     * only thing that had changed was where a column sits. The heading text is what the reader presses on.
     *
     * @param heading The visible heading text of the column to press.
     * @returns The control, or undefined when no column carries that heading.
     */
    function sortControlFor(heading: string): HTMLButtonElement {
      // ⚠ THE LABEL ELEMENT, NOT THE BUTTON'S WHOLE TEXT. A heading that currently carries the ordering also
      // paints a direction glyph inside the same button, so its text content becomes "Title ▲" — matching the
      // button's text would find the column before the first press and lose it afterwards, which is precisely
      // the press these specifications need to repeat.
      const control = sortControls().find(
        (candidate) =>
          (candidate.querySelector('.data-table__label')?.textContent ?? '').trim() === heading,
      );

      if (control === undefined) {
        // Loud rather than optional. An undefined control clicked through `?.` produces no request, and the
        // failure then surfaces as "expected one matching request, found none" several lines later — which
        // reads as an ordering defect when the real cause is a heading that has been renamed.
        throw new Error(
          `no sort control is headed "${heading}"; rendered: ${sortControls()
            .map((candidate) =>
              (candidate.querySelector('.data-table__label')?.textContent ?? '').trim(),
            )
            .join(', ')}`,
        );
      }

      return control;
    }

    /** The heading cells reporting an active direction. */
    function announcedDirections(): readonly string[] {
      return queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');
    }

    it('paints a sort control on the five permitted columns and on no other', () => {
      settleFirstPage([portalRow()]);

      // The descriptor set is asserted separately, above; this proves the grid actually RENDERS them, which
      // a correct descriptor handed to a mis-wired grid input would not.
      expect(sortControls()).toHaveSize(5);
    });

    it('names each control by its action while keeping the visible heading text', () => {
      settleFirstPage([portalRow()]);

      const names: readonly string[] = sortControls().map(
        (control) => control.getAttribute('aria-label') ?? '',
      );

      for (const name of names) {
        expect(name.startsWith('Sort by ')).withContext(name).toBeTrue();
      }

      // WCAG 2.5.3: the accessible name contains the visible label verbatim.
      for (const control of sortControls()) {
        expect(control.getAttribute('aria-label') ?? '').toContain(
          (control.textContent ?? '').trim(),
        );
      }
    });

    it('re-reads ordered by the pressed column, from the first page', async () => {
      settleFirstPage([portalRow()], 40, 0);

      sortControlFor('Portal Id').click();
      await settleAddress();

      const ordered: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(ordered).get('sortBy')).toBe('portalId');
      // The server's own member spelling. `asc` is refused by the model binder with 400.
      expect(sentFilters(ordered).get('sortDir')).toBe('Ascending');
      // A row's page depends on the ordering, so the coordinate returns to the first page.
      expect(sentFilters(ordered).get('pageIndex')).toBe('0');

      ordered.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      expect(announcedDirections())
        .withContext('exactly one column reports itself sorted')
        .toEqual(['ascending']);
    });

    it('reverses on the second press and CLEARS on the third', async () => {
      settleFirstPage([portalRow()], 40, 0);

      sortControlFor('Title').click();
      await settleAddress();
      const ascending: TestRequest = http.expectOne((candidate) => isListingRead(candidate));
      expect(sentFilters(ascending).get('sortBy')).toBe('portalName');
      expect(sentFilters(ascending).get('sortDir')).toBe('Ascending');
      ascending.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      sortControlFor('Title').click();
      await settleAddress();
      const descending: TestRequest = http.expectOne((candidate) => isListingRead(candidate));
      expect(sentFilters(descending).get('sortDir')).toBe('Descending');
      descending.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      // THE THIRD PRESS RETURNS THE LISTING TO THE SERVER'S OWN ORDER. That is the state this screen
      // arrives in - the store initialises both coordinates to null and the first request carries neither
      // parameter - and a two-step toggle made it reachable only by reloading the page.
      sortControlFor('Title').click();
      await settleAddress();
      const cleared: TestRequest = http.expectOne((candidate) => isListingRead(candidate));
      expect(sentFilters(cleared).has('sortBy')).withContext('no key is sent').toBeFalse();
      expect(sentFilters(cleared).has('sortDir')).withContext('no direction is sent').toBeFalse();
      cleared.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      expect(announcedDirections()).withContext('no column reports itself sorted').toEqual([]);
    });
  });

  describe('the pager', () => {
    it('sits BELOW the grid as a sibling, and never as a row inside it', () => {
      settleFirstPage([portalRow()], 40);

      const grid: Element | null = host().querySelector('app-data-table');
      const pager: Element | null = host().querySelector('app-pagination');

      expect(grid).not.toBeNull();
      expect(pager).not.toBeNull();

      // So the pager FOLLOWS the grid in document order and shares its parent.
      expect(pager?.parentElement).toBe(grid?.parentElement ?? null);
      expect(grid?.compareDocumentPosition(pager as Node)).toBe(
        Node.DOCUMENT_POSITION_FOLLOWING,
      );

      // Never a table row: a pager inside the grid would be announced as data, and would be swept away by
      // the grid's own empty and loading states.
      expect(host().querySelector('tfoot')).toBeNull();
      expect(host().querySelector('app-data-table app-pagination')).toBeNull();
      expect(host().querySelector('table app-pagination')).toBeNull();
    });

    it('offers steps only when the total exceeds the page the server served, and counts either way', () => {
      // The legacy predicate verbatim - `PageSize < TotalRecords` - now governs THE STEPS rather than the
      // whole control, so equality is not a reason to step but is still a reason to state the total.
      settleFirstPage([portalRow()], 10, 0);
      expect(host().querySelector('app-pagination')).withContext('the count is shown').not.toBeNull();
      expect(host().querySelectorAll('app-pagination .pagination__button').length).toBe(0);

      invoke<void>('onRetry');
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()], 11));
      fixture.detectChanges();
      expect(host().querySelectorAll('app-pagination .pagination__button').length)
        .withContext('first, previous, next and last')
        .toBe(4);
    });

    it('requests the page the operator asked for, zero-based, through the pager itself', async () => {
      settleFirstPage([portalRow()], 40, 0);

      // Driven through the RENDERED control rather than the component method, so the output binding is
      // exercised too - a pager wired to nothing would pass the method-level expectation and fail this one.
      const next: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'app-pagination button[aria-label="Next page"]',
      );
      expect(next).not.toBeNull();
      next?.click();
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('pageIndex')).toBe('1');
      expect(addressParams()['currentpage']).toBe('2');
      request.flush(pageOf([portalRow()], 40, 1));
      fixture.detectChanges();
    });

    it('returns to the first page from the pager, still zero-based', async () => {
      settleFirstPage([portalRow()], 40, 0);

      // Walk forward first, because the pager reads the page the screen ASKED for and the backward
      // affordances are correctly inert on the first page.
      invoke<void>('onPageChange', 3);
      await settleAddress();
      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '3')
        .flush(pageOf([portalRow()], 40, 3));
      fixture.detectChanges();

      const first: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'app-pagination button[aria-label="First page"]',
      );
      expect(first).not.toBeNull();
      expect(first?.disabled).toBeFalse();
      first?.click();
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();
    });
  });

  // ORDERING
  // A net addition, so every expectation here pins a promise the endpoint makes rather than a legacy
  // behaviour: the five names it accepts, the direction spelling its binder requires, and the return to the
  // first page that a reordering implies.

  describe('ordering', () => {
    it('orders by a heading through the address, in the direction the grid reports', async () => {
      settleFirstPage([portalRow()], 40);

      const heading: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'thead th button',
      );

      // Driven through the RENDERED heading rather than the component method, so the grid's own output
      // binding is exercised: a grid wired to nothing would pass a method-level expectation and fail this.
      expect(heading).not.toBeNull();
      heading?.click();
      await settleAddress();

      expect(addressParams()['sortby']).toBe('portalId');
      // Spelled out in full, because the binder answers `sortDir=asc` with a 400.
      expect(addressParams()['sortdir']).toBe('Ascending');

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('sortBy')).toBe('portalId');
      expect(sentFilters(request).get('sortDir')).toBe('Ascending');
      request.flush(pageOf([portalRow()], 40));
    });

    it('flips the direction on the heading that already carries the ordering', async () => {
      settleFirstPage([portalRow()], 40);

      // ⚠ THE SAME HEADING TWICE, AND IT IS NAMED RATHER THAN TAKEN FROM THE FRONT OF THE ROW. "The first
      // heading that has a button" is not a stable reference on this grid: the shared table hoists a
      // row-header column to the front while its scroll region clips, and that measurement lands a frame
      // after the first paint. Pressing the front heading twice could therefore press two DIFFERENT columns
      // and report a direction defect where the only change was the column order.
      const ordered = (): HTMLButtonElement | undefined =>
        queryAll<HTMLButtonElement>('thead th button.data-table__sort').find(
          (control) =>
            (control.querySelector('.data-table__label')?.textContent ?? '').trim() === 'Title',
        );

      ordered()?.click();
      await settleAddress();
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()], 40));
      fixture.detectChanges();

      ordered()?.click();
      await settleAddress();

      expect(addressParams()['sortdir']).toBe('Descending');
      http
        .expectOne((candidate) => sentFilters(candidate).get('sortDir') === 'Descending')
        .flush(pageOf([portalRow()], 40));
    });

    it('returns to the first page when the ordering changes', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onPageChange', 2);
      await settleAddress();
      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '2')
        .flush(pageOf([portalRow()], 40, 2));
      fixture.detectChanges();

      invoke<void>('onSortChange', { key: 'portalName', direction: 'Descending' });
      await settleAddress();

      // Which page a row falls on depends on the ordering, so holding the index would land an operator on a
      // page of rows they have already seen.
      expect(addressParams()['currentpage']).toBeUndefined();
      http
        .expectOne((candidate) => sentFilters(candidate).get('pageIndex') === '0')
        .flush(pageOf([portalRow()], 40));
    });

    it('marks the ordered heading, and only that heading, from the address', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onSortChange', { key: 'portalName', direction: 'Descending' });
      await settleAddress();
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()], 40));
      fixture.detectChanges();

      const sorted: readonly string[] = queryAll<HTMLElement>('thead th[aria-sort]')
        .filter((cell) => (cell.getAttribute('aria-sort') ?? 'none') !== 'none')
        .map((cell) => cell.getAttribute('aria-sort') ?? '');

      expect(sorted).toEqual(['descending']);
      // The three the endpoint refuses stay unsortable, so no heading can produce a rejected request.
      expect(queryAll<HTMLElement>('thead th[aria-sort]').length).toBe(5);
    });
  });

  // THE ADDRESS
  // The listing's coordinates live in the address, and these expectations are the round trip: what the
  // screen WRITES when an affordance is used is asserted beside the strip and pager blocks above, so this
  // block asserts what it READS - on entry, on a reload and after a back navigation - plus what it does
  // with an address that says something unusable.

  describe('the address', () => {
    it('restores a whole view from the address on entry: page, filter and ordering together', async () => {
      // The initial read from `beforeEach` is settled first, because this navigation is a second entry.
      settleFirstPage([portalRow()], 40);

      await harness.navigateByUrl(
        '/portals?filter=QA&currentpage=3&sortby=hostFee&sortdir=Descending',
      );
      fixture.detectChanges();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      // ONE request for the whole query, not one per coordinate. Four separate store commands would issue
      // four reads for the same page and reset the page index three times on the way to it.
      expect(sentFilters(request).get('name')).toBe('QA');
      expect(sentFilters(request).get('pageIndex')).toBe('2');
      expect(sentFilters(request).get('sortBy')).toBe('hostFee');
      expect(sentFilters(request).get('sortDir')).toBe('Descending');
      request.flush(pageOf([portalRow()], 40, 2));
      fixture.detectChanges();

      // And the controls agree with the rows: the box shows the filter and the heading shows the ordering.
      expect(host().querySelector<HTMLInputElement>('app-search-input input')?.value).toBe('QA');
    });

    it('starts clean on a fresh entry, even though the store outlives the route', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'Q', value: 'Q' });
      await settleAddress();
      http.expectOne((candidate) => sentFilters(candidate).get('name') === 'Q').flush(pageOf([]));
      fixture.detectChanges();

      // ⚠ THE DEFECT THIS PINS. The store is provided at the application ROOT, so it outlives this route
      // and still holds the previous visit's filter, page and ordering when an operator comes back.
      await harness.navigateByUrl('/portals');
      fixture.detectChanges();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).has('name')).toBeFalse();
      expect(sentFilters(request).get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40));
      fixture.detectChanges();

      expect(host().querySelector<HTMLInputElement>('app-search-input input')?.value).toBe('');
      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual(['All']);
    });

    it('corrects an address that states something unusable, and reads once', async () => {
      settleFirstPage([portalRow()], 40);

      // Each of these is refused rather than obeyed: a page that names no page, an ordering field the
      // endpoint would answer with a 400, and an abbreviation its binder rejects.
      await harness.navigateByUrl('/portals?currentpage=abc&sortby=nonsense&sortdir=desc');
      fixture.detectChanges();
      await settleAddress();

      expect(addressParams()['currentpage']).toBeUndefined();
      expect(addressParams()['sortby']).toBeUndefined();
      expect(addressParams()['sortdir']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('pageIndex')).toBe('0');
      expect(sentFilters(request).has('sortBy')).toBeFalse();
      expect(sentFilters(request).has('sortDir')).toBeFalse();
      request.flush(pageOf([portalRow()], 40));
    });

    it('accepts a direction abbreviation on the way in and writes the full spelling out', async () => {
      settleFirstPage([portalRow()], 40);

      // An address is typed by people, so `desc` is understood; it is never SENT, because the binder refuses
      // it. The inbound normalisation is what makes the two facts consistent.
      await harness.navigateByUrl('/portals?sortby=portalName&sortdir=desc');
      fixture.detectChanges();
      await settleAddress();

      expect(addressParams()['sortdir']).toBe('Descending');

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).get('sortDir')).toBe('Descending');
      request.flush(pageOf([portalRow()], 40));
    });

    it('drops a direction that has no field to apply it to', async () => {
      settleFirstPage([portalRow()], 40);

      await harness.navigateByUrl('/portals?sortdir=Descending');
      fixture.detectChanges();
      await settleAddress();

      expect(addressParams()['sortdir']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(request).has('sortDir')).toBeFalse();
      request.flush(pageOf([portalRow()], 40));
    });
  });

  // The shared component set
  // The design-system rule this block enforces: a feature template composes shared components and
  // contributes no raw control that one of them already covers.

  describe('the shared component set', () => {
    it('composes the grid, the pager and the header from shared components', () => {
      settleFirstPage([portalRow()], 40);

      expect(host().querySelector('app-page-header')).not.toBeNull();
      expect(host().querySelector('app-search-input')).not.toBeNull();
      expect(host().querySelector('app-data-table')).not.toBeNull();
      expect(host().querySelector('app-pagination')).not.toBeNull();
    });

    it('contributes no bare table of its own: the only one is the shared grid', () => {
      settleFirstPage([portalRow()], 40);

      const tables: readonly Element[] = queryAll<Element>('table');

      expect(tables.length).toBe(1);
      // And it belongs to the shared grid rather than to this feature.
      expect(tables[0]?.closest('app-data-table')).not.toBeNull();
    });

    it('contributes no bare text control of its own: the only one is the shared filter', () => {
      settleFirstPage([portalRow()], 40);

      // No select at all - this screen offers no dropdown, and the legacy one offered none either. Every
      // input belongs to the shared filter component.
      expect(queryAll<Element>('select').length).toBe(0);
      for (const control of queryAll<Element>('input')) {
        expect(control.closest('app-search-input')).not.toBeNull();
      }
    });

    it('renders no raw resource markup anywhere, in any state', () => {
      settleFirstPage([
        portalRow({ portalName: '<script>window.__portalListXss = true;</script>' }),
        portalRow({ portalId: 21, portalName: '<img src="x" onerror="window.__portalListXss">' }),
      ]);

      const globals: Record<string, unknown> = window as unknown as Record<string, unknown>;

      expect(globals['__portalListXss']).toBeUndefined();
      expect(host().querySelector('script')).toBeNull();
      expect(host().querySelector('img')).toBeNull();
      // The payload survives as TEXT, which is the whole point: the name is not silently dropped, it is
      // simply not interpreted.
      expect(host().textContent).toContain('<script>');
    });
  });

  // ACCESSIBILITY
  //
  // Each item is achieved with no visual change, which is the condition under which it was admitted at all.

  describe('accessibility', () => {
    it('opens the row settings affordance name with its visible word and keeps the tooltip as its description', () => {
      settleFirstPage([portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Baseline Portal' })]);

      const edit: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'a.portal-list__row-command',
      );

      // The painted word is unchanged, so the screen looks exactly as it did.
      expect(edit?.textContent?.trim()).toBe('Settings');

      // WCAG 2.5.3: the accessible name BEGINS with the visible word, which is the stronger form of
      // "contains" and is what makes a speech command match.
      const label: string = edit?.getAttribute('aria-label') ?? '';
      expect(label.startsWith('Settings')).toBeTrue();
      expect(label).toContain('Baseline Portal');
      expect(label).toBe('Settings: Baseline Portal');

      // The legacy tooltip wording survives as the description, still qualified by the row - which is
      // more than the legacy tooltip could do, since it repeated identically down every row.
      const description: string = edit?.getAttribute('title') ?? '';
      expect(description).toContain('Edit this Portal');
      expect(description).toBe('Edit this Portal: Baseline Portal');
      // The global `Edit.Text` value is the bare word; the LOCAL value is the phrase, and the local file
      // wins for a control declared on this screen.
      expect(description).not.toBe('Edit');
    });

    // ⚠ MINOR (whitespace-trimming parity) — untrimmed titles were reaching `aria-label` verbatim.
    it('collapses whitespace inside an announced name while leaving the painted title untouched', () => {
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, portalName: '  Padded\tPortal  ' }),
      ]);

      const edit: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'a.portal-list__row-command',
      );
      const remove: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'button.portal-list__row-command--danger',
      );

      // Leading and trailing whitespace is inaudible, unremovable by a reader and capable of making one
      // announced name differ from an apparently identical one, so both composed names are normalised.
      expect(edit?.getAttribute('aria-label')).toBe('Settings: Padded Portal');
      expect(edit?.getAttribute('title')).toBe('Edit this Portal: Padded Portal');
      expect(remove?.getAttribute('aria-label')).toBe('Delete Padded Portal');

      const titleCell: string = textOf('tbody td,tbody th')[3] ?? '';
      expect(titleCell).toContain('\t');
    });

    it('names the row delete affordance with the global delete wording', () => {
      settleFirstPage([portalRow({ portalId: 31, portalName: 'Removable Portal' })]);

      const remove: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'button.portal-list__row-command--danger',
      );

      // There is no `Delete.Text` entry in `Portals.ascx.resx` at all, so the legacy image column's own
      // localisation by command name fell through to the global `cmdDelete.Text` value - the bare word
      // "Delete". That word is the visible label, and the accessible name qualifies it with the row.
      expect(remove?.textContent?.trim()).toBe('Delete');
      const label: string = remove?.getAttribute('aria-label') ?? '';
      expect(label).toContain('Delete');
      expect(label).toContain('Removable Portal');
    });

    it('gives the grid an accessible name through a real caption', () => {
      settleFirstPage([portalRow()]);

      const caption: HTMLElement | null = host().querySelector<HTMLElement>('table caption');

      // A caption rather than a heading association, because a caption is the element the table role
      // expects; it is clipped rather than hidden, so it costs nothing visually and still reaches the
      // accessibility tree.
      expect(caption).not.toBeNull();
      expect(caption?.textContent?.trim().length).toBeGreaterThan(0);
      expect(caption?.hasAttribute('data-visually-hidden')).toBeTrue();
    });

    it('keeps the announcing region mounted before anything has failed', () => {
      settleFirstPage([portalRow()]);

      expect(host().querySelector('app-error-banner'))
        .withContext('the region is present on a healthy screen')
        .not.toBeNull();
      expect(host().querySelector('app-error-banner [role="alert"]')?.getAttribute('aria-live'))
        .withContext('with its announcement semantics already declared')
        .toBe('assertive');
      expect(host().querySelector('app-error-banner .error-banner'))
        .withContext('and nothing painted inside it')
        .toBeNull();
    });

    it('announces a reported failure through a live region', () => {
      http
        .expectOne((candidate) => isListingRead(candidate))
        .flush(problemOf(500, 'server.unexpected_failure', 'The listing could not be read.'), {
          status: 500,
          statusText: 'Internal Server Error',
        });
      fixture.detectChanges();

      const live: HTMLElement | null = host().querySelector<HTMLElement>('[aria-live]');

      expect(live).not.toBeNull();
      // Assertive, because the operator is not necessarily looking at this region: the failure may have
      // arrived from the initial read rather than from an action.
      expect(live?.getAttribute('aria-live')).toBe('assertive');
      expect(live?.getAttribute('role')).toBe('alert');
      expect(host().querySelector('app-error-banner [aria-live]')).not.toBeNull();

      invoke<void>('onRetry');
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()]));
      fixture.detectChanges();
    });

    it('surfaces the field errors of a validation document by name', () => {
      http
        .expectOne((candidate) => isListingRead(candidate))
        .flush(
          {
            ...problemOf(400, 'request.validation_failed', 'One or more fields are invalid.'),
            // Bracket access below, not property access: the field-error map is an index signature and the
            // workspace forbids reading one as a property.
            errors: { PortalName: ['Portal Name is required.'] },
          },
          { status: 400, statusText: 'Bad Request' },
        );
      fixture.detectChanges();

      const banner: HTMLElement | null = host().querySelector<HTMLElement>('app-error-banner');
      const problem: ProblemDetails | null = member<() => ProblemDetails | null>('listProblem')();
      const fieldErrors: ProblemDetailsErrors = problem?.errors ?? {};

      expect(banner).not.toBeNull();
      expect(fieldErrors['PortalName']).toEqual(['Portal Name is required.']);
      expect(banner?.textContent).toContain('Portal Name is required.');

      invoke<void>('onRetry');
      http.expectOne((candidate) => isListingRead(candidate)).flush(pageOf([portalRow()]));
      fixture.detectChanges();
    });

    it('makes every control in the grid reachable by keyboard, with no trap', () => {
      settleFirstPage([portalRow({ portalId: 41 }), portalRow({ portalId: 42 })], 40);

      const controls: readonly HTMLElement[] = queryAll<HTMLElement>('table a, table button');

      expect(controls.length).toBeGreaterThan(0);
      for (const control of controls) {
        // Natively focusable elements only - a real anchor with an address, or a real button - so Tab, Enter
        // and Space all work with no key handling written anywhere.
        const name: string = control.tagName.toLowerCase();
        expect(['a', 'button']).toContain(name);
        if (name === 'a') {
          expect(control.getAttribute('href')).toBeTruthy();
        }
        // Nothing is removed from the tab order, which is how a control becomes unreachable while still
        // looking operable.
        expect(control.getAttribute('tabindex')).not.toBe('-1');
      }

      for (const row of queryAll<HTMLElement>('tbody tr')) {
        expect(row.getAttribute('tabindex'))
          .withContext('a row nothing listens to must not be a tab stop')
          .toBeNull();
        expect(row.hasAttribute('aria-selected'))
          .withContext('nor announce a selection state')
          .toBeFalse();
      }

      // No element is made operable by a handler on a non-interactive tag either.
      expect(queryAll<Element>('table div[tabindex], table span[tabindex]').length).toBe(0);

      // The filter strip is keyboard-operable on the same terms.
      for (const chip of queryAll<HTMLElement>('.portal-list__letter')) {
        expect(chip.tagName.toLowerCase()).toBe('button');
        expect(chip.getAttribute('type')).toBe('button');
      }
    });

    it('groups, names and declares the filter strip as arrow-navigable', () => {
      settleFirstPage([portalRow()]);

      const strip: HTMLElement | null = host().querySelector<HTMLElement>('.portal-list__letters');

      // The legacy strip was a centred panel of hyperlinks with no grouping and no name, so twenty-seven
      // adjacent single-letter links were announced with nothing to say what they filtered. That much is
      // unchanged and is still asserted.
      expect(strip?.getAttribute('aria-label')).toBe('Filter portals by first letter');

      // ⚠ THIS EXPECTATION USED TO READ `group`, AND THE CHANGE IS A STRENGTHENING RATHER THAN A
      // RELAXATION. `group` grouped the strip and said nothing else; the twenty-seven entries were each their
      // own tab stop and a real ArrowRight press moved focus nowhere - measured, on this strip and on the
      // account listing's. They are now one stop with the arrows moving between them, and `toolbar` is the
      // role that tells a reader those arrows apply at all. A roving tabindex inside a plain `group` would
      // leave the strip navigable with no way to discover it, so the role and the tab stops are asserted
      // TOGETHER: either alone is the defect in a different disguise.
      expect(strip?.getAttribute('role')).toBe('toolbar');

      const entries: readonly HTMLElement[] = Array.from(
        strip?.querySelectorAll<HTMLElement>('button') ?? [],
      );

      expect(entries.length).withContext('the full alphabet plus the clearing entry').toBe(27);
      expect(entries.filter((entry) => entry.tabIndex >= 0).length)
        .withContext('one stop, not twenty-seven')
        .toBe(1);
    });
  });
  // THE EMPTY-TABLE FLASH
  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so this screen painted its zero-result surface over a
  // listing it had not yet asked about - reported as an empty-table flash on every post-save return.
  describe('an un-asked listing waits rather than claiming to be empty', () => {
    it('shows the indicator and NO zero-result surface while the address rewrite is in flight', async () => {
      settleFirstPage([]);

      // A non-canonical address takes the correction arm, which REPLACES the address and returns WITHOUT
      // reading. The replacement navigation happens a task later, so this is exactly the window in which
      // nothing is in flight and nothing is held.
      await harness.navigateByUrl('/portals?currentpage=not-a-number', PortalListComponent);
      fixture.detectChanges();

      expect(queryAll('app-loading-spinner').length)
        .withContext('the listing has not been asked about, so it is waiting')
        .toBe(1);
      expect(queryAll('app-empty-state').length)
        .withContext('nothing may assert that no portals match a filter that has not been applied yet')
        .toBe(0);
      expect(host().textContent ?? '')
        .not.withContext('and no zero-result wording of any kind is on screen')
        .toContain('No portals');

      await settleAddress();
      settleFirstPage([]);
    });

    it('shows the zero-result surface once a read has genuinely answered with nothing', () => {
      settleFirstPage([]);

      expect(queryAll('app-loading-spinner').length).toBe(0);
      expect(queryAll('app-empty-state').length)
        .withContext('a settled read that matched nothing IS the empty state')
        .toBe(1);
    });
  });

  // ⚠ MINOR (interaction state) — the broad row-command hover rule was claiming the Delete command.
  describe('the hover treatment of the command that destroys a tenant', () => {
    /**
     * Every rule in the document that tints a row command on hover, flattened out of any media query.
     *
     * Nested rules are walked whatever their query resolves to, because the defect is device-dependent: the
     * override that keeps Delete red is guarded by a pointer-capability query, and on a device with no fine
     * pointer it leaves the cascade entirely and the broad rule wins. This runner reports no pointing device
     * at all, so a specification that only measured what THIS browser resolves would be measuring the
     * failing device and calling it the passing one.
     *
     * @returns The hover rules, with their selectors and declarations.
     */
    function commandHoverRules(): readonly { selectorText: string; style: CSSStyleDeclaration }[] {
      const collected: { selectorText: string; style: CSSStyleDeclaration }[] = [];

      const walk = (list: CSSRuleList): void => {
        Array.from(list).forEach((rule) => {
          if (rule instanceof CSSMediaRule) {
            walk(rule.cssRules);

            return;
          }

          if (
            rule instanceof CSSStyleRule &&
            rule.selectorText.includes('portal-list__row-command') &&
            rule.selectorText.includes(':hover')
          ) {
            collected.push({ selectorText: rule.selectorText, style: rule.style });
          }
        });
      };

      Array.from(document.styleSheets).forEach((sheet) => {
        try {
          walk(sheet.cssRules);
        } catch {
          return;
        }
      });

      return collected;
    }

    it('never lets the generic hover tint reach the Delete command', () => {
      settleFirstPage([portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Doomed Portal' })]);

      const remove: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'button.portal-list__row-command--danger',
      );

      if (remove === null) {
        throw new Error('the Delete command did not render');
      }

      const generic = commandHoverRules().filter(
        (rule) => rule.style.getPropertyValue('color').trim() === 'var(--color-primary-hover)',
      );

      // The control: if this reads zero the stylesheet was never loaded and the assertion below would pass
      // for the wrong reason.
      expect(generic.length).withContext('a generic command hover tint is declared').toBeGreaterThan(0);

      // ⚠ THE CASCADE IS ASKED DIRECTLY RATHER THAN THE RULE TEXT MATCHED. Stripping `:hover` from the
      // selector turns each rule into a question the rendered element can answer: "would you be tinted if a
      // pointer were over you?" Under the defect the answer is yes and the one command that destroys a
      // tenant hovers `rgb(37, 86, 154)` — measured — which spends the one unambiguous colour in the
      // vocabulary on a benign hover. Under the fix the exclusion makes every answer no.
      generic.forEach((rule) => {
        const withoutHover: string = rule.selectorText.split(':hover').join('');

        expect(remove.matches(withoutHover))
          .withContext(`the Delete command must fall outside ${withoutHover}`)
          .toBeFalse();
      });
    });

    it('still lets the generic hover tint reach the commands it is meant for', () => {
      // The narrowing that keeps the specification above from being satisfied by deleting the rule outright.
      settleFirstPage([portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Doomed Portal' })]);

      const edit: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'a.portal-list__row-command',
      );

      if (edit === null) {
        throw new Error('the Settings command did not render');
      }

      const generic = commandHoverRules().filter(
        (rule) => rule.style.getPropertyValue('color').trim() === 'var(--color-primary-hover)',
      );
      const reaching = generic.filter((rule) => edit.matches(rule.selectorText.split(':hover').join('')));

      expect(reaching.length).withContext('the benign commands are still tinted').toBeGreaterThan(0);
    });
  });

});
