//
// Specification for the portal (tenant) listing screen at /portals.
//
// Every expectation below pins a MEASURED legacy behaviour that a refactor could plausibly get wrong without
// any compiler or linter noticing:
//
//   * the filter strip holds twenty-seven entries, a to Z with the clear-filter entry APPENDED AFTER Z, and
//     the twenty-eighth legacy entry is absent;
//   * the clear-filter entry sends no filter value rather than its own label;
//   * a filter change returns to the first page, and the free-text filter is forwarded byte for byte with no
//     pattern character;
//   * the ten columns are in legacy order, with keys distinct from labels and alignment declared per column
//     rather than once for the grid;
//   * both `0` and `-1` survive as portal identifiers, in the rendered cell and in the route the row's
//     affordance targets;
//   * the account and page counts render the legacy absent-integer marker as received; the fee carries
//     exactly two decimals and no group separator; an absent or marker expiry renders as an empty cell;
//   * host names become real anchors with a scheme, and an already-absolute one is left alone;
//   * the delete affordance is withheld from the row for the tenant being browsed, and a deletion announces
//     the legacy success or refusal wording at the matching severity;
//   * the pager is a SIBLING BELOW the grid, drawn only when the total exceeds the served page, and reports a
//     zero-based index.
//
// Five expectations assert that something is NOT there: no `Expired` filter entry, no portal-template action,
// no bulk expired-portal deletion, no raw resource markup, and no bare table or text control. The first three
// name affordances the legacy screen genuinely offered and whose endpoints this API deliberately does not
// expose, so each is what stops a later author "restoring" a control that can only ever fail.
//
// The listing is driven through the real store and the real transport with the HTTP layer under test control,
// so the request the screen actually causes is asserted rather than assumed. The test target declares no
// environment file replacement, so the transport resolves the PRODUCTION base - a RELATIVE `/api/v1` - and
// every expectation is written against a relative address for that reason.
//
// Every string asserted here is a RESOURCE VALUE and never a markup attribute, and the legacy application
// proves that is the right way round: the legacy screen localised its grid at run time by looking each
// column's own header text up as a resource key, so the markup read `PortalId`, `DiskSpace` and `HostingFee`
// while the screen PAINTED `Portal Id`, `Disk Space` and `Hosting Fee`. Resource keys may also contain
// spaces - `Portal Aliases.Header` is a measured example in this very screen.
//

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

/**
 * The collection address, relative because the production environment is relative.
 */
const PORTALS_URL = '/api/v1/portals';

/**
 * The legacy absent-integer marker, and simultaneously the portal identity seed.
 */
const FIRST_PORTAL_ID = -1;

/**
 * The second portal an installation ever creates. Zero is a real portal.
 */
const SECOND_PORTAL_ID = 0;

/**
 * The legacy absent-date marker, which survives on the wire.
 */
const NULL_DATE = '0001-01-01T00:00:00';

/**
 * A real expiry, chosen so its rendering cannot be mistaken for a coincidence.
 *
 * The day is past the twelfth, so a month/day transposition changes the answer rather than yielding a
 * plausible alternative date; and the instant is midnight, which is where a renderer that resolved the date
 * in a westward local zone instead of UTC would slip a day.
 */
const REAL_EXPIRY_DATE = '2027-03-15T00:00:00';

/**
 * The URN prefix every failure code this API publishes is carried behind.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * A fixed trace identifier, shaped like the trace parent the server derives one from.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/**
 * A fixed correlation identifier - the value an operator quotes when reporting a refusal.
 */
const CORRELATION_ID = '0f7d3c81-9a24-4b6e-8c5d-2e91b7a40f36';

/**
 * The reason phrase the API publishes as the problem `title`, keyed by status.
 */
const STATUS_TITLE: Readonly<Record<number, string>> = Object.freeze({
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
});

/**
 * A problem document as this API publishes one.
 *
 * They wrote `type: 'about:blank'`, which this API never sends: a refusal reaches the wire through one
 * shared problem factory that fills an unspecified type from the status vocabulary, so the type is ALWAYS a
 * `urn:dnnmigration:error:` code and a screen branching on the code would have been tested against a
 * document from which no code can be read. They wrote titles such as `'Server Error'` that belong to no
 * status. They omitted both identifiers, so nothing proved the support reference survives. And they omitted
 * `detail` on the conflict, which meant the message-precedence rule fell through to the title instead of
 * exercising the real path.
 *
 * There is deliberately NO `instance` member: every call site in the API supplies null for it and the
 * framework's problem type omits a null one per member, so a live document has none.
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

/**
 * The shape a collection endpoint answers with, as the paging contract publishes it.
 */
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
  // Typed against the HARNESS'S root component rather than against the screen, because that is what the
  // fixture actually wraps: the harness mounts a root component carrying a router outlet and the screen is
  // rendered INSIDE it. Every use below is a rendering concern - `nativeElement` and `detectChanges` - and the
  // screen instance itself is held separately in `component`, returned by the navigation.
  let fixture: ComponentFixture<unknown>;
  let component: PortalListComponent;
  let http: HttpTestingController;
  let notifications: NotificationService;

  /**
   * The router, read for the address the screen has navigated to.
   *
   * Asserted directly in several places, because "the screen put the filter in the address" and "the screen
   * asked the server for the filtered page" are two different promises and only one of them is visible in an
   * HTTP expectation. A reload reproduces the view only if the first holds.
   */
  let router: Router;

  /**
   * The harness the screen is mounted through, so a navigation can be driven the way the browser drives one.
   */
  let harness: RouterTestingHarness;

  /**
   * The tenant the signed-in session is scoped to, under test control.
   *
   * The screen consults exactly ONE fact from the session store - the browsed tenant - so the collaborator
   * is stood in for by a value carrying exactly that one signal. Driving it through the real store would
   * mean seeding a credential store, which would couple this specification to how a session is persisted
   * rather than to what this screen does with it. `null` is the default, which is the "nobody is signed in"
   * state.
   */
  let sessionPortalId: ReturnType<typeof signal<number | null>>;

  /**
   * Reads a protected member without widening it to the forbidden catch-all type.
   */
  function member<T>(name: string): T {
    return (component as unknown as Record<string, T>)[name];
  }

  function invoke<T>(name: string, ...args: unknown[]): T {
    const method = member<(...called: unknown[]) => T>(name);

    return method.apply(component, args);
  }

  /**
   * The rendered host element, typed.
   *
   * `ComponentFixture.nativeElement` is deliberately untyped by the framework, and an untyped receiver
   * cannot take the type argument the query helpers below need, so the narrowing happens once here rather
   * than at every call site.
   */
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
   * Lets an address change reach the store, then renders.
   *
   * ⚠ EVERY AFFORDANCE ON THIS SCREEN IS ASYNCHRONOUS NOW, AND OMITTING THIS MAKES A SPECIFICATION MEASURE
   * NOTHING. A letter, a search, a page turn and a heading all NAVIGATE, and a router navigation settles in a
   * microtask - so a synchronous expectation placed straight after one of them runs before the address has
   * changed, before the store has been told and before any request exists. The symptom is "found none" from
   * the HTTP expectation, which reads like a wiring fault rather than a timing one.
   */
  async function settleAddress(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /**
   * The query parameters the screen has actually navigated to.
   *
   * Read off the router's own parsed tree rather than off `location`, so the expectation is about what the
   * application asked for rather than about how the platform serialised it.
   */
  function addressParams(): Readonly<Record<string, string>> {
    return router.parseUrl(router.url).queryParams as Readonly<Record<string, string>>;
  }

  /**
   * The hosting-fee cell of every rendered row, in row order.
   *
   * The fee is the NINTH cell: settings, delete, identifier, title, host names, accounts, pages, disk
   * space, fee, expiry. Derived from the row rather than from a flat cell list so a change in the
   * number of rows cannot silently shift which cell is read.
   *
   * ⚠ BOTH CELL ELEMENTS ARE READ, and that is what keeps this ordinal aligned with the heading
   * ordinal above. The title cell IDENTIFIES its row, so the grid renders it as a `th` with a row
   * scope rather than as a `td`; querying only `td` drops it from the list and silently reads the
   * cell one place to the right - measured, when this returned the expiry cell for the fee.
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
      (candidate) => candidate.url === PORTALS_URL,
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
        // navigates, and the address change is what reaches the store and causes the request. A component
        // created with `TestBed.createComponent` receives the ROOT route and no route configuration to
        // navigate within, so every one of those affordances would silently do nothing and the specification
        // would pass while measuring an inert screen. Mounting it at `portals` is what makes the round trip -
        // affordance to address to store to request - the thing under test.
        provideRouter([{ path: 'portals', component: PortalListComponent }]),
        {
          provide: AuthStore,
          // This screen reads exactly ONE member of the identity store — the browsed tenant's identifier —
          // so that is all the double supplies.
          //
          // And nothing more, deliberately. `useValue` is not checked against the token it stands in for, so
          // a member added here "just in case" is never reported as unused and survives long after the code
          // that wanted it has gone. This double previously carried a session-boundary callback for a
          // coordinator the stores registered themselves with; teardown is now driven from
          // `session-teardown.service.ts` and `session-lifecycle.service.ts`, which call each store's own
          // `reset()`, and no domain store imports the identity store at all. Keeping the member would have
          // described an arrangement that no longer exists.
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

    const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

    expect(request.request.method).toBe('GET');
    // No page size is requested: the size is the server's to decide.
    expect(request.request.params.has('pageSize')).toBeFalse();
    // No filter is sent on the first read.
    expect(request.request.params.has('name')).toBeFalse();
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

      // The letter reaches the ADDRESS under the legacy parameter spelling, which is what makes a reload and
      // a back navigation reproduce the filtered view. `Portals.ascx.vb:L215-L222` composed exactly this
      // pair - `filter` and `currentpage` - into a real navigation.
      expect(addressParams()['filter']).toBe('B');
      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.params.get('name') === 'B',
      );

      // The repository composes any pattern it needs; a per-cent sign here would double it.
      expect(request.request.params.get('name')).toBe('B');
      expect(request.request.params.get('name')).not.toContain('%');

      // MIGRATION: no expiry parameter is sent, in any spelling. `Portals.ascx.vb` special-cased one entry
      // of the strip by calling a different reader altogether - `PortalController.GetExpiredPortals()` -
      // and hiding the pager alongside it. That reader has no successor endpoint, and the collection
      // endpoint accepts no expiry filter, so a request carrying one would be silently ignored rather than
      // refused. The absent parameter is asserted in three spellings because the wrong one would look
      // plausible in review.
      expect(request.request.params.has('expired')).toBeFalse();
      expect(request.request.params.has('isExpired')).toBeFalse();
      expect(request.request.params.has('expiryDate')).toBeFalse();

      // And the only parameters carried at all are the paging index and the name.
      expect(request.request.params.keys().sort()).toEqual(['name', 'pageIndex']);
      request.flush(pageOf([]));
    });

    it('clears the filter rather than sending the word All', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'C', value: 'C' });
      await settleAddress();
      http.expectOne((candidate) => candidate.params.get('name') === 'C').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'All', value: null });
      await settleAddress();

      // The parameter is REMOVED from the address rather than written empty, so an unfiltered listing is the
      // bare path - which is also what makes the reset distinguishable from a filter on the empty string.
      expect(addressParams()['filter']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('name')).toBeFalse();
      request.flush(pageOf([portalRow()]));
    });

    it('returns to the first page whenever the filter changes', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onPageChange', 3);
      await settleAddress();
      // ONE-BASED in the address and zero-based on the wire, which is the legacy arrangement:
      // `Portals.ascx.vb:L47` seeds `_CurrentPage` at 1 and its reader takes `CurrentPage - 1`.
      expect(addressParams()['currentpage']).toBe('4');
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '3')
        .flush(pageOf([portalRow()], 40, 3));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'D', value: 'D' });
      await settleAddress();

      // The page is dropped from the address as well as reset on the wire, so a reload of the filtered view
      // does not land back on the fourth page of a match set that may now have one.
      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne(
        (candidate) => candidate.params.get('name') === 'D',
      );

      expect(request.request.params.get('pageIndex')).toBe('0');
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
      http.expectOne((candidate) => candidate.params.get('name') === 'M').flush(pageOf([]));
      fixture.detectChanges();

      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual(['M']);
    });

    it('shows the letter it applied in the search box, so the filter is visible and clearable', async () => {
      settleFirstPage([portalRow()], 40);

      // ⚠ THE DEFECT THIS PINS: the strip and the box are two affordances over ONE filter, and the box used
      // to be left untouched when the strip acted. An operator then saw a filtered listing behind an empty
      // box - no visible cause for the missing rows, and nothing to clear, because clearing an already-empty
      // box changes no value and therefore emits nothing at all.
      invoke<void>('onFilterSelected', { label: 'N', value: 'N' });
      await settleAddress();
      http.expectOne((candidate) => candidate.params.get('name') === 'N').flush(pageOf([]));
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
      http.expectOne((candidate) => candidate.params.get('name') === 'QA010').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'All', value: null });
      await settleAddress();
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 40));
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
      http.expectOne((candidate) => candidate.params.get('name') === 'base').flush(pageOf([]));
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

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('name')).toBe('  BaSe ');
      // Untrimmed in the address too, so a reload asks for exactly what was asked for the first time.
      expect(addressParams()['filter']).toBe('  BaSe ');
      request.flush(pageOf([]));
    });

    it('treats empty text as no filter, which is the legacy test', async () => {
      settleFirstPage([portalRow()]);

      // ⚠ CLEARED FROM A FILTERED STATE, WHICH IS THE ONLY STATE IN WHICH CLEARING MEANS ANYTHING. The
      // reproduction that found this defect was exactly this sequence - narrow to one row, then empty the box
      // - and it issued ZERO requests, leaving the single filtered row behind a visibly empty box with no way
      // back. Clearing an ALREADY-empty box is a different thing and is deliberately not asserted here: it
      // changes no coordinate, so it correctly asks for nothing.
      invoke<void>('onSearch', 'QA010');
      await settleAddress();
      http.expectOne((candidate) => candidate.params.get('name') === 'QA010').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onSearch', '');
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('name')).toBeFalse();
      expect(addressParams()['filter']).toBeUndefined();
      request.flush(pageOf([portalRow()]));
    });

    it('does not write an echo of its own emission back into the box being typed in', async () => {
      settleFirstPage([portalRow()]);

      const box: HTMLInputElement = host().querySelector<HTMLInputElement>(
        'app-search-input input',
      ) as HTMLInputElement;

      // The debounced emission of "ab" is in flight when the operator types the third character. Reconciling
      // unconditionally would adopt "ab" back into the control - deleting the "c" and cancelling the delay it
      // had started - so the guard that recognises the screen's own request is what this pins.
      invoke<void>('onSearch', 'ab');
      box.value = 'abc';
      box.dispatchEvent(new Event('input'));
      await settleAddress();
      http.expectOne((candidate) => candidate.params.get('name') === 'ab').flush(pageOf([]));
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

    it('binds the title column to the portal-name member, not to a title member', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));
      const title = byKey.get('portalName');

      // The HEADING is `Title` - the local `Title.Header` value - while the bound member is `PortalName`.
      // The two differ, and following the heading instead of the markup would read a member that does not
      // exist on the contract: `undefined` at run time with no compile error to warn of it.
      expect(title?.label).toBe('Title');
      expect((title as DataTableTextColumn<PortalListItem>).field).toBe('portalName');
      expect(byKey.has('title')).toBeFalse();
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
      // aid. The endpoint implements ordering, so the affordance is offered.
      //
      // The SET is the endpoint's and not this screen's, which is what keeps it honest: `SortableFields`
      // admits `PortalId`, `PortalName`, `ExpiryDate`, `HostFee` and `HostSpace` - matched without regard to
      // case, so these camel-cased keys are accepted as they stand - and answers a sixth name with a
      // field-level 400 listing the five. A heading that produced a refused request would be worse than no
      // heading, so the three columns it does not admit must stay unsortable: the two tallies are computed
      // counts rather than columns, and the host names are a collection with no single value to order by.
      const sortable: readonly string[] = columns()
        .filter((column) => (column as { readonly sortable?: boolean }).sortable === true)
        .map((column) => column.key);

      expect(sortable).toEqual(['portalId', 'portalName', 'hostSpace', 'hostFee', 'expiryDate']);

      const unsortable: readonly string[] = columns()
        .filter((column) => (column as { readonly sortable?: boolean }).sortable !== true)
        .map((column) => column.key);

      expect(unsortable).toEqual(['edit', 'delete', 'aliases', 'users', 'pages']);
    });

    it('sizes the two command columns from their content', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));

      expect(byKey.get('edit')?.width).toBe('min-content');
      expect(byKey.get('delete')?.width).toBe('min-content');
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
      // ⚠ THE SELECTOR EXCLUDES THE SORT INDICATOR, WHICH IS NOT A LABEL. Every sortable heading now
      // renders its direction indicator unconditionally so that activating a sort cannot change the
      // control's size — measured at 44.000 -> 46.469 px on one heading and 70.859 -> 86.750 px on
      // another when the element was conditional. That reservation adds one `<span>` per sortable
      // column, which a bare `thead th span` count folds into the label total and turns 10 into 15.
      // This assertion is about the COLUMN NAMES, so it counts the elements that carry a name; the
      // indicator is `aria-hidden` and carries none.
      const labels: readonly HTMLElement[] = queryAll<HTMLElement>(
        'thead th span:not(.data-table__sort-indicator)',
      );

      expect(labels.length).toBe(10);

      // MIGRATION: the command columns gain a name they never had. The legacy image columns rendered no
      // heading text at all, so a screen-reader user reading the header row heard two unnamed columns. Both
      // now carry the wording the legacy column would have resolved - the local `Edit.Text` phrase and, for
      // delete, the global `cmdDelete.Text` word, since `Portals.ascx.resx` declares no entry of that name -
      // clipped rather than removed, so the header row looks unchanged.
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
      // The HEADING changed wording between the markup and the resource file; the MODEL FIELD did not. This
      // pins both halves at once: the descriptor still reads the original member name, and the value it
      // reads reaches the cell that sits under the reworded heading.
      const headings: readonly string[] = textOf('thead th');
      const cells: readonly string[] = textOf('tbody td,tbody th');

      const byKey = new Map(columns().map((column) => [column.key, column]));
      const diskSpace = byKey.get('hostSpace');

      expect((diskSpace as DataTableTextColumn<PortalListItem>).field).toBe('hostSpace');
      expect(headings.indexOf('Disk Space')).toBe(7);
      expect(cells[7]).toBe('0');

      // The fee column is a formatted column rather than a field column, because the legacy grid attached a
      // format string to it and to no other; the fee it formats is `hostFee`.
      expect(headings.indexOf('Hosting Fee')).toBe(8);
      expect(cells[8]).toBe('0.00');
    });
  });

  // THE HEADER ACTIONS
  //
  //  The legacy screen published THREE actions from one property. One survives; two are withheld, and the two
  //  negative expectations below are the most durable part of this block: they are what stops a later author
  //  restoring an affordance whose endpoint does not exist, which would present the operator with a control
  //  that can only ever fail.

  describe('the header actions', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    it('offers the create action, targeting the create route', () => {
      const action: HTMLAnchorElement | null = host().querySelector<HTMLAnchorElement>(
        'app-page-header a.portal-list__action',
      );

      // The wording is the local resource value for the add-content action key (`AddContent.Action`), which
      // the legacy property read.
      expect(action?.textContent?.trim()).toBe('Add New Portal');
      // targeted the signup page; the target here is the create route of this feature.
      expect(action?.getAttribute('href')).toBe('/portals/new');
    });

    // MIGRATION: the template-export action is withheld. `Portals.ascx.vb` published an action reading the
    // local `ExportTemplate.Action` value and targeting the template page. No portal-template endpoint
    // exists on the API, so the affordance would have no destination; `core/config/api-endpoints.ts`
    // declares the portal group closed for that reason. Withheld rather than disabled, so nothing
    // advertises a capability that is absent.
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

      // Identity, not equality. A link array rebuilt per row per pass would satisfy `toEqual` and fail this,
      // and it is identity that decides whether the router re-parses a target that has not changed - once
      // per row, on every pass, under push change detection.
      expect(after).toBe(before);
    });

    it('answers the absent-integer marker in the two tallies with a mark, never with minus one', () => {
      // ⚠ THIS REPLACES A FACT THAT PINNED THE MARKER BEING PAINTED AS `-1`, AND THE COLLISION IS WHY. That
      // rendering is only defensible in isolation: in THIS grid `Portals.PortalID` is `IDENTITY(-1,1)`, so
      // minus one is a real portal identifier and the first column paints it verbatim - correctly. Runtime
      // measurement found minus one in the page tally of all 251 rows of a real installation while the
      // identifier column showed it on one of them, so the same characters in the same row meant "portal
      // number minus one" in one cell and "no count available" in another. Rule T7 puts the fix in the
      // DISPLAY: the contract still carries minus one and the wire is untouched.
      settleFirstPage([portalRow({ portalId: -1, users: -1, pages: -1 })]);

      const cells: readonly string[] = textOf('tbody td,tbody th');

      expect(cells.filter((cell) => cell === '-1').length)
        .withContext('the identifier still paints minus one, because there it is real')
        .toBe(1);
      expect(cells.filter((cell) => cell.startsWith('\u2014')).length)
        .withContext('both tallies paint the mark')
        .toBe(2);
      // The mark is decorative and the words are the content, so the state reaches a screen reader.
      expect(textOf('.portal-list__absent-tally')).toEqual(['not recorded', 'not recorded']);
      expect(
        queryAll<HTMLElement>('tbody td span[aria-hidden="true"]').map((node) => node.textContent),
      ).toEqual(['\u2014', '\u2014']);
    });

    it('paints a real tally verbatim, including nought', () => {
      settleFirstPage([portalRow({ users: 0, pages: 7 })]);

      const cells: readonly string[] = textOf('tbody td');

      expect(cells).toContain('0');
      expect(cells).toContain('7');
      expect(queryAll<HTMLElement>('.portal-list__absent-tally').length).toBe(0);
    });

    it('drops no row and coerces no identifier across the whole sentinel range', () => {
      // The highest-value expectation in this file. Each of `if (id)`, `id > 0` and `id ?? -1` compiles
      // cleanly, passes every other expectation here, and silently loses a row or renames a portal. The
      // identifier column is read back in full and compared as a sequence, so a dropped row changes the
      // length and a coerced identifier changes a member.
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

  // -------------------------------------------------------------------------------------------------
  //  ⚠ MINOR — THE HOSTING FEE
  // -------------------------------------------------------------------------------------------------
  //
  // Its own block, and deliberately NOT inside `formatting`, because these two specifications need to
  // choose what the FIRST page holds. `formatting` settles a single baseline row before each of its
  // specifications, and a second read issued after that settle is not adopted by the grid, so every
  // case here is read from one page of several rows instead - which is also closer to what an operator
  // actually sees than four successive single-row reads would be.
  describe('the hosting fee', () => {
    // ⚠ MINOR (money differentiation) — THIS SPECIFICATION WAS REWRITTEN BECAUSE THE COLUMN CHANGED SHAPE.
    //
    // It reached into the column descriptor and called its `value` formatter directly, which is no longer
    // there: the column is a TEMPLATE column now, because a formatted column paints a string and can carry
    // no per-value treatment, so a negative fee could not be told from a positive one. The characters
    // painted are unchanged and the same formatter still produces them, so the assertions below are the
    // same assertions read from the rendered cells instead of from a function.
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

    // The non-finite case is asserted where it can actually happen, which is NOT in the cell.
    //
    // The old specification called the column's formatter directly with `NaN` and asserted an empty
    // string. Reading the same case from the rendered grid proved something better and previously
    // unstated: a page carrying a non-finite fee never reaches a cell at all, because the listing
    // decoder refuses the whole page and the screen reports a failure instead of painting a row. So the
    // formatter's finiteness guard is defence in depth behind a boundary that already refuses the value,
    // and the guarantee an operator actually gets is stronger than "an empty cell" - they are told.
    it('refuses a page whose fee is not a finite number rather than painting a row from it', () => {
      settleFirstPage([portalRow({ portalId: 44, hostFee: Number.NaN })]);

      expect(host().querySelectorAll('tbody tr').length)
        .withContext('no row is painted from an unusable page')
        .toBe(0);
      expect(host().querySelector<HTMLElement>('app-error-banner'))
        .withContext('and the failure is reported through the shared banner')
        .not.toBeNull();
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
  });

  describe('formatting', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    it('renders an absent expiry and the legacy marker date as an empty cell', () => {
      const rendered = (expiryDate: string | null): string => {
        settleFirstPage([portalRow({ expiryDate })]);

        const cells: readonly string[] = textOf('tbody td,tbody th');

        return cells[cells.length - 1] ?? 'MISSING';
      };

      invoke<void>('onRetry');
      expect(rendered(null)).toBe('');

      invoke<void>('onRetry');
      expect(rendered(NULL_DATE)).toBe('');

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
      // ⚠ THE DEFECT THIS PINS: runtime measurement found an expiry of `1/15/2020` and one of `12/31/2099`
      // rendered identically - same colour, same weight, no badge, no title - on the one screen whose purpose
      // is administering hosting terms. The legacy list formatter (`Portals.ascx.vb:L250-L260`) printed the
      // date and nothing else, but the account-services screen DID test the clock
      // (`MemberServices.ascx.vb:L172-L186`: the date when `expiryDate > Date.Today`, the word `Expired`
      // otherwise), and the word itself is this screen's own `Expired.Text` resource entry. Both are used.
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: '2020-01-15T00:00:00' })]);

      expect(textOf('.portal-list__expired')).toEqual(['Expired']);
      // The DATE survives beside the qualifier: an administrator needs to know WHEN a term lapsed, which is
      // more than either legacy screen showed - the account-services one replaced the date with the word.
      expect((textOf('tbody td').at(-1) ?? '')).toContain('1/15/2020');

      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: '2099-12-31T00:00:00' })]);

      expect(textOf('.portal-list__expired')).toEqual([]);
      expect((textOf('tbody td').at(-1) ?? '')).toContain('12/31/2099');
    });

    it('asserts no state where the legacy asserted none: an absent expiry gets no qualifier', () => {
      // The empty cell stays entirely empty - no dash, no placeholder word, no qualifier - because the legacy
      // formatter seeded its result with the empty string and returned it. A qualifier beside an empty cell
      // would also be unreadable: it would describe a term nobody can see.
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: null })]);
      expect(textOf('.portal-list__expired')).toEqual([]);

      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: NULL_DATE })]);
      expect(textOf('.portal-list__expired')).toEqual([]);

      // An unparseable value is treated the same way, so the qualifier and the cell can never disagree about
      // whether there is an expiry at all - both read the one parser.
      invoke<void>('onRetry');
      settleFirstPage([portalRow({ expiryDate: 'not-a-date' })]);
      expect(textOf('.portal-list__expired')).toEqual([]);
      expect((textOf('tbody td').at(-1) ?? '')).toBe('');
    });

    it('puts today itself on the expired side, because the legacy comparison is strict', () => {
      // `expiryDate > Date.Today` is a STRICT comparison against midnight, so a term expiring at today's
      // midnight had already lapsed by the legacy screen's reckoning. Reproduced rather than rounded in the
      // operator's favour, which would silently extend every term by a day.
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
     * An anchor's visible text, with the visually-hidden new-context phrase removed.
     *
     * The phrase is deliberately part of the anchor's ACCESSIBLE NAME - that is how the change of context is
     * announced - so `textContent` is no longer the host name on its own. Reading the label through a clone
     * keeps the two concerns separate: this returns what a sighted reader sees, and the case below asserts
     * the accessible name in full.
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
      expect(anchors[0]?.getAttribute('href')).toBe('http://localhost:4200');
      // Already absolute - left exactly as stored, not re-serialised by the parser: normalising would
      // lower-case the host and append a trailing slash, so the address in the status bar would stop being
      // the value the operator actually stored.
      expect(anchors[1]?.getAttribute('href')).toBe('https://secure.example');
      // The empty host name produced NO entry, where the legacy screen appended an empty anchor with no
      // emptiness test — a focusable, unlabelled link pointing at the current page. The host name is the
      // anchor's TEXT, escaped by interpolation. Read WITHOUT the visually-hidden new-context phrase, which
      // is part of the accessible name and not part of the host name.
      expect(visibleLabel(anchors[0])).toBe('localhost:4200');
    });

    it('renders a host name whose scheme is not http as INERT TEXT rather than as a link', () => {
      // An allowlist of two schemes, and the refused values reach the dom as text. The legacy screen built
      // the anchor by string concatenation at `Portals.ascx.vb` and assigned the result to a label's `Text`,
      // so a stored host name became markup unexamined and unescaped. Enumerating the schemes to refuse is a
      // losing game — `javascript:`, `data:`, `vbscript:`, `blob:` and `file:` are merely the ones anybody
      // thinks of, and mixed case or percent-encoding slips past a fragment test — so exactly `http:` and
      // `https:` are admitted and everything else yields no href at all.
      //
      // And the value is still shown. Dropping it would hide stored state from the operator administering
      // it, which is the one thing this screen exists to report. It is shown as text, which the framework
      // escapes through an ordinary interpolation, so it is readable and inert.
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
      // MIGRATION - THE `target` IS THE HALF TO ADD, NOT THE `rel` THE HALF TO DELETE. The address leaves
      // this application entirely, and the session's token is held in memory alone, so a same-tab navigation
      // signs the operator out of the console they were administering. `rel` states BOTH keywords rather
      // than relying on a modern browser's implicit `noopener`: `noreferrer` additionally withholds the
      // console's own address from the site being opened, which is a tenant's public site and not
      // necessarily trusted.
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
    // constraint on it, so a host name is UNTRUSTED INPUT that reaches an anchor's `href` and its text. The
    // cases below are the security half of this group, and they exist because the cases above cover only
    // well-formed values - which is exactly the coverage that lets an injection through.
    //
    // ONE mechanism defends this, and it is an ALLOWLIST rather than a denylist. The projection prefixes
    // `http://` unless the value already carries `mailto:`, `://`, `~` or a double backslash, and then
    // requires the result to PARSE as a URL whose protocol is exactly `http:` or `https:` and whose host is
    // non-empty. Everything else projects a null href, and a null href renders no anchor at all - the stored
    // value still appears, as interpolated text.
    //
    // That is deliberately stronger than demoting a hostile value to a path under an http origin, and
    // stronger than leaning on the framework's URL sanitiser to mark an executable scheme `unsafe:`. Both of
    // those still emit a focusable anchor pointing somewhere; this emits none.
    //
    // In every case below the value must therefore render with NO anchor, its label must be TEXT rather than
    // markup, and no `innerHTML` may appear anywhere in the template.

    it('renders a bare javascript scheme with no anchor at all, and shows it as text', () => {
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, aliases: ['javascript:alert(1)'] }),
      ]);

      // The value carries none of the four markers, so `http://` is prefixed - and
      // `http://javascript:alert(1)` does not parse, because `alert(1)` is not a port. No address is
      // projected, so nothing is navigable.
      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a'))
        .withContext('nothing navigable')
        .toHaveSize(0);

      const cell: HTMLElement | undefined = queryAll<HTMLElement>('.portal-list__alias')[0];

      expect((cell?.textContent ?? '').trim()).toBe('javascript:alert(1)');
      expect(cell?.children.length).withContext('text, not markup').toBe(0);
    });

    it('renders a javascript scheme that carries the address marker with no anchor either', () => {
      // `javascript://` contains `://`, so it is taken as already absolute and parsed as stored. It parses -
      // and its protocol is `javascript:`, which the allowlist does not admit. Nothing here relies on the
      // framework rewriting an executable attribute, because no attribute is emitted.
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

      // Prefixing yields `http://<img …>host.example`, which is not a host, so no address is projected and
      // the value is shown as it was stored.
      expect(queryAll<HTMLAnchorElement>('.portal-list__alias > a')).toHaveSize(0);
      expect(cell?.querySelector('img'))
        .withContext('interpolation escapes it; nothing is parsed as an element')
        .toBeNull();
      expect(cell?.children.length).toBe(0);
      expect(cell?.textContent).toContain('<img src=x onerror=alert(1)>host.example');
    });

    it('shows a network share and an application-relative path exactly as stored, unlinked', () => {
      // The two legacy exclusions, and they are exclusions rather than oversights: a share and a
      // tilde-rooted path are addresses in their own right, so no scheme is prefixed - and neither parses as
      // an http address, so neither becomes a link. Rewriting either would produce a value that resolves
      // nowhere and would make this row disagree with the edit screen.
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
      // The empty string is the legacy spelling of an absent string, so it arrives often. The legacy screen
      // appended an anchor per row with no emptiness test, producing `<a href=""></a>` - a focusable,
      // unlabelled link pointing at the current page. Rendering nothing is the honest representation, and it
      // is asserted alongside a real host name in the same row so that the empty value is proved to be
      // DROPPED rather than to have suppressed the row.
      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, aliases: ['', 'one.example', '', ''] }),
      ]);

      const anchors: readonly HTMLAnchorElement[] = queryAll<HTMLAnchorElement>(
        '.portal-list__alias > a',
      );

      expect(anchors.length).toBe(1);
      expect(anchors[0]?.getAttribute('href')).toBe('http://one.example');
      expect(anchors[0]?.getAttribute('href'))
        .withContext('never the empty attribute the legacy screen rendered')
        .not.toBe('');
      expect(queryAll<HTMLElement>('.portal-list__alias').length).toBe(1);
    });

    it('uses no innerHTML anywhere in the rendered host-name cell', () => {
      // The class-level guarantee behind every case above: the template interpolates and binds, and nothing
      // in it assigns markup. Asserted structurally - a cell whose only element child is the visually-hidden
      // new-context phrase, with everything else a text node, cannot have been produced by an assignment of
      // markup.
      //
      // Both arms are exercised in one row: the markup-bearing value is refused by the address allowlist and
      // rendered as text, and the well-formed one becomes an anchor.
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([]));
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
      // ⚠ THE MEASURED DEFECT. The body was the bare legacy question and named nothing, while this
      // dialog is modal and PHYSICALLY COVERS the listing - the row being destroyed included. An
      // operator who reached a row command by keyboard, sixteen tab stops in, had nothing on screen
      // telling them which of several near-identical records they had reached. The legacy prompt
      // could take that context for granted because it was a browser `confirm()` raised from the
      // row the pointer had just clicked; a centred modal cannot.
      //
      // The legacy wording is unchanged - it is the measured global resource value - and the
      // identity is appended to it.
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([]));
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

      // The live refusal, complete: the code the server publishes, the title that belongs to the status, the
      // authored sentence, and both identifiers. `last_remaining` is the token the shared status translator
      // reads to answer 409.
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
      // The legacy screen surfaced this at RedError, not at the success severity - and 409 is one of the
      // statuses that stays an error, since only 401, 403, 404 and 429 soften to a warning.
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
      http.expectNone((candidate) => candidate.url === PORTALS_URL);

      // And the row the operator declined to remove is still there.
      expect(queryAll<Element>('tbody tr').length).toBe(1);
    });

    it('opens the confirmation through the shared dialogue rather than a scripted prompt', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('requestDeletion', portalRow());
      fixture.detectChanges();

      const dialog: HTMLDialogElement | null =
        host().querySelector<HTMLDialogElement>('app-confirm-dialog dialog');

      // MIGRATION: the browser confirmation becomes a real dialogue. `Portals.ascx.vb` attached the global
      // `DeleteItem.Text` wording to the delete column as a scripted `confirm()`, which offered no focus
      // management, no escape handling and no nameable cancel affordance. The contract of the shared
      // component is asserted here - not its internals - because those three properties are what the
      // replacement buys.
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
      // And a confirm affordance labelled with the global `cmdDelete.Text` value, which is also why the
      // delete COLUMN carries that same word: `Portals.ascx.vb` localised each image column by its command
      // name, and no local entry of that name exists in `Portals.ascx.resx` to override the global one.
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
      // forwards as a cancellation. Dispatched rather than simulated with a key press, because that event IS
      // the platform's Escape contract.
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
    it('shows the empty surface with the add action when nothing matched', () => {
      settleFirstPage([], 0);

      const empty: HTMLElement | null = host().querySelector<HTMLElement>('app-empty-state');

      expect(empty).not.toBeNull();
      expect(empty?.textContent).toContain('No portals match the current filter.');
      expect(empty?.textContent).toContain('Add New Portal');
      expect(host().querySelector('app-data-table')).toBeNull();
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
      // Reached by ASKING for the fourth page, not merely by being handed one: the address is what the screen
      // reads its page from, so a response that only SAYS it is page three would leave the address at page one
      // and the affordance under test would be a no-op this expectation could not tell from a broken binding.
      settleFirstPage([portalRow()], 40, 0);
      invoke<void>('onPageChange', 3);
      await settleAddress();
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '3')
        .flush(pageOf([], 40, 3));
      fixture.detectChanges();

      const back: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        'app-empty-state button',
      );
      expect(back.length).toBe(1);
      back[0]?.click();
      await settleAddress();

      expect(addressParams()['currentpage']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40));
      fixture.detectChanges();
    });

    it('keeps the pager and its count when the whole match set fits on one page', () => {
      settleFirstPage([portalRow()], 40);
      expect(host().querySelector('app-pagination')).not.toBeNull();
      expect(host().querySelectorAll('app-pagination .pagination__button').length).toBe(4);

      invoke<void>('onRetry');
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 1));
      fixture.detectChanges();

      // ⚠ THE GROUP STAYS AND THE STEPS GO. The range summary is the only place this screen states how
      // many portals matched, which a filtered listing needs most; runtime testing measured the previous
      // behaviour removing the whole group and leaving the count unstated.
      expect(host().querySelector('app-pagination')).not.toBeNull();
      expect(host().querySelector('app-pagination .pagination__status')?.textContent ?? '')
        .withContext('the count survives')
        .toContain('of 1');
      expect(host().querySelectorAll('app-pagination .pagination__button').length)
        .withContext('nowhere to step to')
        .toBe(0);
    });

    it('shows the indicator while the FIRST page is read, and not on a later read', async () => {
      // Nothing has settled yet, so this is the first read.
      expect(host().querySelector<HTMLElement>('app-loading-spinner')).not.toBeNull();
      expect(host().querySelector<HTMLElement>('app-data-table')).toBeNull();

      settleFirstPage([portalRow()], 40);
      expect(host().querySelector<HTMLElement>('app-loading-spinner')).toBeNull();

      // A later read keeps the rows on screen rather than blanking the table.
      invoke<void>('onPageChange', 1);
      await settleAddress();
      expect(host().querySelector<HTMLElement>('app-data-table')).not.toBeNull();

      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '1')
        .flush(pageOf([portalRow()], 40, 1));
      fixture.detectChanges();
    });

    it('reports a failure that carried no problem document through the SAME shared banner', () => {
      // A request that never reached the server carries no RFC 7807 document at all.
      //
      // IT USED TO GET A SECOND, POORER PRESENTATION: a bare `<p role="status">` carrying one
      // sentence, with no severity word, no title, no support reference and no retry - and
      // runtime testing found it byte-identical to the presentation for a response whose body
      // was not a problem document, so an operator could not tell "the server refused this"
      // from "the request never left the browser". The store now composes a truthful document
      // from the status, so there is ONE failure presentation and both modes are legible.
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
        .error(new ProgressEvent('error'));
      fixture.detectChanges();

      const banner: HTMLElement | null = host().querySelector<HTMLElement>('app-error-banner');

      expect(banner).not.toBeNull();
      expect(banner?.textContent ?? '')
        .withContext('the composed document says the server could not be reached')
        .toContain('could not be reached');

      // Dismissing clears the surface without issuing a request.
      invoke<void>('onFailureDismissed');
      fixture.detectChanges();
      expect(host().querySelector<HTMLElement>('app-error-banner')).toBeNull();
    });

    it('reports a failed listing through the shared banner and can retry', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()]));
      fixture.detectChanges();

      expect(host().querySelector('app-error-banner')).toBeNull();
    });
  });

  // THE PAGER

  describe('ordering', () => {
    /** The rendered sort controls, in column order. */
    function sortControls(): readonly HTMLButtonElement[] {
      return queryAll<HTMLButtonElement>('th.data-table__header button.data-table__sort');
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

      sortControls()[0]?.click();
      await settleAddress();

      const ordered: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(ordered.request.params.get('sortBy')).toBe('portalId');
      // The server's own member spelling. `asc` is refused by the model binder with 400.
      expect(ordered.request.params.get('sortDir')).toBe('Ascending');
      // A row's page depends on the ordering, so the coordinate returns to the first page.
      expect(ordered.request.params.get('pageIndex')).toBe('0');

      ordered.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      expect(announcedDirections())
        .withContext('exactly one column reports itself sorted')
        .toEqual(['ascending']);
    });

    it('reverses on the second press and CLEARS on the third', async () => {
      settleFirstPage([portalRow()], 40, 0);

      sortControls()[1]?.click();
      await settleAddress();
      const ascending: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);
      expect(ascending.request.params.get('sortBy')).toBe('portalName');
      expect(ascending.request.params.get('sortDir')).toBe('Ascending');
      ascending.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      sortControls()[1]?.click();
      await settleAddress();
      const descending: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);
      expect(descending.request.params.get('sortDir')).toBe('Descending');
      descending.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();

      // THE THIRD PRESS RETURNS THE LISTING TO THE SERVER'S OWN ORDER. That is the state this screen
      // arrives in - the store initialises both coordinates to null and the first request carries neither
      // parameter - and a two-step toggle made it reachable only by reloading the page. Asserted on the
      // WIRE because the omission is the point: a key with no direction would be a different question.
      sortControls()[1]?.click();
      await settleAddress();
      const cleared: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);
      expect(cleared.request.params.has('sortBy')).withContext('no key is sent').toBeFalse();
      expect(cleared.request.params.has('sortDir')).withContext('no direction is sent').toBeFalse();
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 11));
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

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      // Zero-based on the wire and one-based in the address, matching the legacy reader's own
      // `CurrentPage - 1` at `Portals.ascx.vb:L142`: the second page is index one and `currentpage=2`.
      expect(request.request.params.get('pageIndex')).toBe('1');
      expect(addressParams()['currentpage']).toBe('2');
      request.flush(pageOf([portalRow()], 40, 1));
      fixture.detectChanges();
    });

    it('returns to the first page from the pager, still zero-based', async () => {
      settleFirstPage([portalRow()], 40, 0);

      // Walk forward first, because the pager reads the page the screen ASKED for and the backward
      // affordances are correctly inert on the first page. Reaching page three by flushing a response that
      // merely SAYS it is page three would leave the request the screen made at zero, and the click under
      // test would be a no-op that this expectation could not distinguish from a broken binding.
      invoke<void>('onPageChange', 3);
      await settleAddress();
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '3')
        .flush(pageOf([portalRow()], 40, 3));
      fixture.detectChanges();

      const first: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        'app-pagination button[aria-label="First page"]',
      );
      expect(first).not.toBeNull();
      expect(first?.disabled).toBeFalse();
      first?.click();
      await settleAddress();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40, 0));
      fixture.detectChanges();
    });
  });

  // ORDERING
  //
  //  A net addition, so every expectation here pins a promise the endpoint makes rather than a legacy
  //  behaviour: the five names it accepts, the direction spelling its binder requires, and the return to the
  //  first page that a reordering implies.

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

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('sortBy')).toBe('portalId');
      expect(request.request.params.get('sortDir')).toBe('Ascending');
      request.flush(pageOf([portalRow()], 40));
    });

    it('flips the direction on the heading that already carries the ordering', async () => {
      settleFirstPage([portalRow()], 40);

      host().querySelector<HTMLButtonElement>('thead th button')?.click();
      await settleAddress();
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 40));
      fixture.detectChanges();

      host().querySelector<HTMLButtonElement>('thead th button')?.click();
      await settleAddress();

      expect(addressParams()['sortdir']).toBe('Descending');
      http
        .expectOne((candidate) => candidate.params.get('sortDir') === 'Descending')
        .flush(pageOf([portalRow()], 40));
    });

    it('returns to the first page when the ordering changes', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onPageChange', 2);
      await settleAddress();
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '2')
        .flush(pageOf([portalRow()], 40, 2));
      fixture.detectChanges();

      invoke<void>('onSortChange', { key: 'portalName', direction: 'Descending' });
      await settleAddress();

      // Which page a row falls on depends on the ordering, so holding the index would land an operator on a
      // page of rows they have already seen.
      expect(addressParams()['currentpage']).toBeUndefined();
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '0')
        .flush(pageOf([portalRow()], 40));
    });

    it('marks the ordered heading, and only that heading, from the address', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onSortChange', { key: 'portalName', direction: 'Descending' });
      await settleAddress();
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 40));
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
  //
  //  The listing's coordinates live in the address, and these expectations are the round trip: what the screen
  //  WRITES when an affordance is used is asserted beside the strip and pager blocks above, so this block
  //  asserts what it READS - on entry, on a reload and after a back navigation - plus what it does with an
  //  address that says something unusable.

  describe('the address', () => {
    it('restores a whole view from the address on entry: page, filter and ordering together', async () => {
      // The initial read from `beforeEach` is settled first, because this navigation is a second entry.
      settleFirstPage([portalRow()], 40);

      await harness.navigateByUrl(
        '/portals?filter=QA&currentpage=3&sortby=hostFee&sortdir=Descending',
      );
      fixture.detectChanges();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      // ONE request for the whole query, not one per coordinate. Four separate store commands would issue
      // four reads for the same page and reset the page index three times on the way to it.
      expect(request.request.params.get('name')).toBe('QA');
      expect(request.request.params.get('pageIndex')).toBe('2');
      expect(request.request.params.get('sortBy')).toBe('hostFee');
      expect(request.request.params.get('sortDir')).toBe('Descending');
      request.flush(pageOf([portalRow()], 40, 2));
      fixture.detectChanges();

      // And the controls agree with the rows: the box shows the filter and the heading shows the ordering.
      expect(host().querySelector<HTMLInputElement>('app-search-input input')?.value).toBe('QA');
    });

    it('starts clean on a fresh entry, even though the store outlives the route', async () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'Q', value: 'Q' });
      await settleAddress();
      http.expectOne((candidate) => candidate.params.get('name') === 'Q').flush(pageOf([]));
      fixture.detectChanges();

      // ⚠ THE DEFECT THIS PINS. The store is provided at the application ROOT, so it outlives this route and
      // still holds the previous visit's filter, page and ordering when an operator comes back. Runtime
      // testing measured a fresh sidebar click landing on page three of a filter the operator could not see,
      // with the search box empty and every strip entry unpressed - the only clue being the pager's total.
      await harness.navigateByUrl('/portals');
      fixture.detectChanges();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('name')).toBeFalse();
      expect(request.request.params.get('pageIndex')).toBe('0');
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

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.has('sortBy')).toBeFalse();
      expect(request.request.params.has('sortDir')).toBeFalse();
      request.flush(pageOf([portalRow()], 40));
    });

    it('accepts a direction abbreviation on the way in and writes the full spelling out', async () => {
      settleFirstPage([portalRow()], 40);

      // An address is typed by people, so `desc` is understood; it is never SENT, because the binder refuses
      // it. The correction is what makes the two facts consistent.
      await harness.navigateByUrl('/portals?sortby=portalName&sortdir=desc');
      fixture.detectChanges();
      await settleAddress();

      expect(addressParams()['sortdir']).toBe('Descending');

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('sortDir')).toBe('Descending');
      request.flush(pageOf([portalRow()], 40));
    });

    it('drops a direction that has no field to apply it to', async () => {
      settleFirstPage([portalRow()], 40);

      await harness.navigateByUrl('/portals?sortdir=Descending');
      fixture.detectChanges();
      await settleAddress();

      expect(addressParams()['sortdir']).toBeUndefined();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('sortDir')).toBeFalse();
      request.flush(pageOf([portalRow()], 40));
    });
  });

  // The shared component set
  //
  //  The design-system rule this block enforces: a feature template composes shared components and
  //  contributes no raw control that one of them already covers. Written as expectations rather than left to
  //  review because the failure mode is silent - a bare control renders, looks approximately right, and
  //  quietly loses the label association, the keyboard behaviour and the token vocabulary the shared
  //  component carries.

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
      // MIGRATION: resource text is treated as untrusted HTML and rendered as plain text. A substantial
      // minority of the in-scope resource values carry HTML tags, stored escaped and so invisible to a naive
      // search; this screen's own `ModuleHelp.Text` opens with a heading tag, and a sibling portal screen's
      // `Advertising.Text` carries a literal script block. Nothing on this screen binds a raw-HTML property
      // or a trusted-HTML wrapper, so a value like that cannot execute or restructure the page.
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
    // ⚠ MINOR (WCAG 2.5.3 Label in Name) — THIS SPECIFICATION WAS REWRITTEN, AND THE REWRITE IS THE FIX.
    //
    // It required the accessible name to CONTAIN the legacy tooltip wording "Edit this Portal", which is
    // a real measured resource value and was the right thing to preserve - but the affordance PAINTS the
    // word "Settings", so a name built from the tooltip did not contain the visible text and a
    // speech-input user saying "click Settings" matched none of the ten on the page.
    //
    // The measured wording is not discarded. It moves to the affordance's DESCRIPTION, which is what a
    // tooltip is: `portals.ascx:L21` rendered an unlabelled image and this string was its tooltip, so
    // relocating it corrects a category error as well as a conformance failure. Both are asserted below,
    // in their new places.
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

      // THE PAINTED TITLE IS DELIBERATELY LEFT ALONE. An operator correcting a title with a stray tab in
      // it must be able to see the tab, and the cell is the only place they can. The DOM keeps the value
      // exactly as received; only the announced names are normalised.
      // ⚠ BOTH CELL ELEMENTS, because the title cell IDENTIFIES its row and is therefore a `th` with a
      // row scope rather than a `td`. Querying only `td` skips it altogether and reads the host-names cell
      // in its place - measured, as this case finding 'localhost (opens in a new tab)' where it expected a
      // padded title.
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

    it('announces a reported failure through a live region', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()]));
      fixture.detectChanges();
    });

    it('surfaces the field errors of a validation document by name', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
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
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()]));
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

      // ⚠ THE ROWS ARE NOT TAB STOPS, AND THIS CASE USED TO REQUIRE THAT THEY WERE. The shared
      // grid gave every row a zero tab index unconditionally, so a page of forty portals put
      // forty inert stops between the filter strip and the first row command — a reader
      // reaching the last row's Delete had to pass through every row above it to get there,
      // and none of those stops did anything when activated, this screen binding no
      // `rowSelect`. The grid now offers the row affordance only where something is listening,
      // so the correct expectation here is that no row is in the tab order and no row announces
      // a selection state it cannot enter.
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

    it('groups and names the filter strip', () => {
      settleFirstPage([portalRow()]);

      const strip: HTMLElement | null = host().querySelector<HTMLElement>('.portal-list__letters');

      // The legacy strip was a centred panel of hyperlinks with no grouping and no name, so twenty-seven
      // adjacent single-letter links were announced with nothing to say what they filtered.
      expect(strip?.getAttribute('role')).toBe('group');
      expect(strip?.getAttribute('aria-label')).toBe('Filter portals by first letter');
    });
  });
});
