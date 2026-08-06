//
// Specification for the portal (tenant) listing screen at /portals.
//
// WHAT IS EXERCISED, AND WHY THESE THINGS. Every expectation below pins a MEASURED legacy
// behaviour that a refactor could plausibly get wrong without any compiler or linter
// noticing:
//
//   * the filter strip holds twenty-seven entries, A to Z with the clear-filter entry
//     APPENDED AFTER Z, and the twenty-eighth legacy entry is absent;
//   * the clear-filter entry sends no filter value rather than its own label;
//   * a filter change returns to the first page;
//   * the free-text filter is forwarded byte for byte with no pattern character;
//   * the ten columns are in legacy order, with keys distinct from labels and alignment
//     declared per column rather than once for the grid;
//   * both `0` and `-1` survive as portal identifiers, in the rendered cell and in the
//     route the row's affordance targets;
//   * the account and page counts render the legacy absent-integer marker as received;
//   * the fee carries exactly two decimals and no group separator;
//   * an absent or marker expiry renders as an empty cell;
//   * host names become real anchors with a scheme, and an already-absolute one is left
//     alone;
//   * the delete affordance is withheld from the row for the tenant being browsed;
//   * a successful deletion announces the legacy success wording at success severity, and a
//     refusal announces the legacy refusal wording at error severity.
//
// The listing is driven through the real store and the real transport with the HTTP layer
// under test control, so the request the screen actually causes is asserted rather than
// assumed. The test target declares no environment file replacement, so the transport
// resolves the PRODUCTION base - a RELATIVE `/api/v1` - and every expectation below is
// written against a relative address for that reason.
//

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { signal } from '@angular/core';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalListComponent } from './portal-list.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { PortalListItem } from '../../../core/models/portal.model';
import type { AppNotification } from '../../../core/services/notification.service';
import type {
  DataTableColumn,
  DataTableFormattedColumn,
} from '../../../shared/components/data-table/data-table.component';

/** The collection address, relative because the production environment is relative. */
const PORTALS_URL = '/api/v1/portals';

/** The legacy absent-integer marker, and simultaneously the portal identity seed. */
const FIRST_PORTAL_ID = -1;

/** The second portal an installation ever creates. Zero is a real portal. */
const SECOND_PORTAL_ID = 0;

/** The legacy absent-date marker, which survives on the wire. */
const NULL_DATE = '0001-01-01T00:00:00';

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
  let fixture: ComponentFixture<PortalListComponent>;
  let component: PortalListComponent;
  let http: HttpTestingController;
  let notifications: NotificationService;

  /**
   * The tenant the signed-in session is scoped to, under test control.
   *
   * The screen consults exactly ONE fact from the session store - the browsed tenant - so
   * the collaborator is stood in for by a value carrying exactly that one signal. Driving
   * it through the real store would mean seeding a credential store, which would couple
   * this specification to how a session is persisted rather than to what this screen does
   * with it. `null` is the default, which is the "nobody is signed in" state.
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

  /**
   * The rendered host element, typed.
   *
   * `ComponentFixture.nativeElement` is deliberately untyped by the framework, and an
   * untyped receiver cannot take the type argument the query helpers below need, so the
   * narrowing happens once here rather than at every call site.
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

  beforeEach(() => {
    sessionPortalId = signal<number | null>(null);

    TestBed.configureTestingModule({
      imports: [PortalListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthStore,
          // This screen reads exactly ONE member of the identity store — the browsed tenant's
          // identifier — so that is all the double supplies.
          //
          // ⚠ AND NOTHING MORE, DELIBERATELY. `useValue` is not checked against the token it
          // stands in for, so a member added here "just in case" is never reported as unused and
          // survives long after the code that wanted it has gone. This double previously carried a
          // session-boundary callback for a coordinator the stores registered themselves with;
          // teardown is now driven from `session-teardown.service.ts` and
          // `session-lifecycle.service.ts`, which call each store's own `reset()`, and no domain
          // store imports the identity store at all. Keeping the member would have described an
          // arrangement that no longer exists.
          useValue: {
            portalId: sessionPortalId.asReadonly(),
          },
        },
      ],
    });

    fixture = TestBed.createComponent(PortalListComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  // -------------------------------------------------------------------------
  // CREATION AND THE INITIAL READ
  // -------------------------------------------------------------------------

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

  // -------------------------------------------------------------------------
  // THE FILTER STRIP
  // -------------------------------------------------------------------------

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

    it('sends the chosen letter as the name filter with no pattern character', () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'B', value: 'B' });

      const request: TestRequest = http.expectOne(
        (candidate) => candidate.url === PORTALS_URL && candidate.params.get('name') === 'B',
      );

      // The repository composes any pattern it needs; a per-cent sign here would double it.
      expect(request.request.params.get('name')).toBe('B');
      request.flush(pageOf([]));
    });

    it('clears the filter rather than sending the word All', () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'C', value: 'C' });
      http.expectOne((candidate) => candidate.params.get('name') === 'C').flush(pageOf([]));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'All', value: null });

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('name')).toBeFalse();
      request.flush(pageOf([portalRow()]));
    });

    it('returns to the first page whenever the filter changes', () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onPageChange', 3);
      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '3')
        .flush(pageOf([portalRow()], 40, 3));
      fixture.detectChanges();

      invoke<void>('onFilterSelected', { label: 'D', value: 'D' });

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

    it('marks the chosen letter as pressed, and only that letter', () => {
      settleFirstPage([portalRow()], 40);

      invoke<void>('onFilterSelected', { label: 'M', value: 'M' });
      http.expectOne((candidate) => candidate.params.get('name') === 'M').flush(pageOf([]));
      fixture.detectChanges();

      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual(['M']);
    });

    it('leaves every entry unpressed while a free-text filter is in force', () => {
      settleFirstPage([portalRow()], 40);

      // Truthful: the list is filtered, but by none of the strip's entries.
      invoke<void>('onSearch', 'base');
      http.expectOne((candidate) => candidate.params.get('name') === 'base').flush(pageOf([]));
      fixture.detectChanges();

      expect(textOf('.portal-list__letter[aria-pressed="true"]')).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // THE FREE-TEXT FILTER
  // -------------------------------------------------------------------------

  describe('the free-text name filter', () => {
    it('forwards the text byte for byte, untrimmed and with its case unchanged', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('onSearch', '  BaSe ');

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('name')).toBe('  BaSe ');
      request.flush(pageOf([]));
    });

    it('treats empty text as no filter, which is the legacy test', () => {
      settleFirstPage([portalRow()]);

      invoke<void>('onSearch', '');

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.has('name')).toBeFalse();
      request.flush(pageOf([portalRow()]));
    });
  });

  // -------------------------------------------------------------------------
  // THE COLUMN SET
  // -------------------------------------------------------------------------

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

    it('offers no sortable column, matching a grid that declared no sorting', () => {
      for (const column of columns()) {
        expect((column as { readonly sortable?: boolean }).sortable).not.toBeTrue();
      }
    });

    it('sizes the two command columns from their content', () => {
      const byKey = new Map(columns().map((column) => [column.key, column]));

      expect(byKey.get('edit')?.width).toBe('min-content');
      expect(byKey.get('delete')?.width).toBe('min-content');
    });
  });

  // -------------------------------------------------------------------------
  // SENTINEL DISCIPLINE
  // -------------------------------------------------------------------------

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

      // Read through the precomputed lookup the template indexes, so the assertion exercises
      // the same path the rendered link takes. A NEGATIVE key is a legitimate key here.
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

      // Identity, not equality. A link array rebuilt per row per pass would satisfy `toEqual`
      // and fail this, and it is identity that decides whether the router re-parses a target
      // that has not changed - once per row, on every pass, under push change detection.
      expect(after).toBe(before);
    });

    it('renders the absent-integer marker in the account and page counts as received', () => {
      settleFirstPage([portalRow({ users: -1, pages: -1 })]);

      const cells: readonly string[] = textOf('tbody td');

      // Two cells carry the marker, exactly as the legacy grid painted it.
      expect(cells.filter((cell) => cell === '-1').length).toBe(2);
    });
  });

  // -------------------------------------------------------------------------
  // FORMATTING
  // -------------------------------------------------------------------------

  describe('formatting', () => {
    beforeEach(() => {
      settleFirstPage([portalRow()]);
    });

    it('formats the hosting fee with exactly two decimals and no group separator', () => {
      const feeColumn = columns().find((column) => column.key === 'hostFee');
      const format = (feeColumn as DataTableFormattedColumn<PortalListItem>).value;

      expect(format(portalRow({ hostFee: 0 }))).toBe('0.00');
      expect(format(portalRow({ hostFee: 9.5 }))).toBe('9.50');
      // No group separator, unlike the site-settings screen's own helper.
      expect(format(portalRow({ hostFee: 1234.5 }))).toBe('1234.50');
      // A malformed payload renders an empty cell rather than the word NaN.
      expect(format(portalRow({ hostFee: Number.NaN }))).toBe('');
    });

    it('renders an absent expiry and the legacy marker date as an empty cell', () => {
      const rendered = (expiryDate: string | null): string => {
        settleFirstPage([portalRow({ expiryDate })]);

        const cells: readonly string[] = textOf('tbody td');

        return cells[cells.length - 1] ?? 'MISSING';
      };

      invoke<void>('onRetry');
      expect(rendered(null)).toBe('');

      invoke<void>('onRetry');
      expect(rendered(NULL_DATE)).toBe('');
    });
  });

  // -------------------------------------------------------------------------
  // HOST NAMES
  // -------------------------------------------------------------------------

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
      // lower-case the host and append a trailing slash, so the address in the status bar would stop
      // being the value the operator actually stored.
      expect(anchors[1]?.getAttribute('href')).toBe('https://secure.example');
      // The empty host name produced NO entry, where the legacy screen appended an empty anchor with
      // no emptiness test — a focusable, unlabelled link pointing at the current page.
      // The host name is the anchor's TEXT, escaped by interpolation. Read WITHOUT the visually-hidden
      // new-context phrase, which is part of the accessible name and not part of the host name.
      expect(visibleLabel(anchors[0])).toBe('localhost:4200');
    });

    it('renders a host name whose scheme is not http as INERT TEXT rather than as a link', () => {
      // ⚠ AN ALLOWLIST OF TWO SCHEMES, AND THE REFUSED VALUES REACH THE DOM AS TEXT. The legacy screen
      // built the anchor by string concatenation at `Portals.ascx.vb:L282` and assigned the result to a
      // label's `Text`, so a stored host name became markup unexamined and unescaped. Enumerating the
      // schemes to refuse is a losing game — `javascript:`, `data:`, `vbscript:`, `blob:` and `file:`
      // are merely the ones anybody thinks of, and mixed case or percent-encoding slips past a
      // fragment test — so exactly `http:` and `https:` are admitted and everything else yields no
      // href at all.
      //
      // ⚠ AND THE VALUE IS STILL SHOWN. Dropping it would hide stored state from the operator
      // administering it, which is the one thing this screen exists to report. It is shown as text,
      // which the framework escapes through an ordinary interpolation, so it is readable and inert.
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
      // MIGRATION - ⚠ THE `target` IS THE HALF TO ADD, NOT THE `rel` THE HALF TO DELETE. The address leaves
      // this application entirely, and the session's token is held in memory alone, so a same-tab navigation
      // signs the operator out of the console they were administering. `rel` states BOTH keywords rather than
      // relying on a modern browser's implicit `noopener`: `noreferrer` additionally withholds the console's
      // own address from the site being opened, which is a tenant's public site and not necessarily trusted.
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

    it('answers a row that is not on the page in hand with no host names', () => {
      settleFirstPage([portalRow({ portalId: 1, aliases: ['one.example'] })]);

      // A row absent from the projection can only mean the page has changed since it was
      // built; an empty list is the safe reading and no identifier is defaulted to reach it.
      expect(
        invoke<readonly unknown[]>('aliasLinks', portalRow({ portalId: 99, aliases: [] })),
      ).toEqual([]);
      expect(
        invoke<readonly unknown[]>('aliasLinks', portalRow({ portalId: 1, aliases: [] })).length,
      ).toBe(1);
    });
  });

  // -------------------------------------------------------------------------
  // THE DELETE FLOW
  // -------------------------------------------------------------------------

  describe('the delete flow', () => {
    it('keeps the affordance on every row when no tenant is resolved', () => {
      settleFirstPage([portalRow()]);

      // No session is signed in, so no row matches and every row keeps its affordance.
      expect(invoke<boolean>('canDelete', portalRow({ portalId: SECOND_PORTAL_ID }))).toBeTrue();
      expect(invoke<boolean>('canDelete', portalRow({ portalId: FIRST_PORTAL_ID }))).toBeTrue();
    });

    it('withholds the delete affordance from the row for the tenant being browsed', () => {
      // Zero is a real tenant, and the row rule has to work for it: a truthiness test on
      // the identifier would classify it as "no tenant" and leave the affordance in place.
      sessionPortalId.set(SECOND_PORTAL_ID);

      settleFirstPage([
        portalRow({ portalId: SECOND_PORTAL_ID, portalName: 'Browsed Portal' }),
        portalRow({ portalId: FIRST_PORTAL_ID, portalName: 'Other Portal' }),
      ]);

      expect(invoke<boolean>('canDelete', portalRow({ portalId: SECOND_PORTAL_ID }))).toBeFalse();
      expect(invoke<boolean>('canDelete', portalRow({ portalId: FIRST_PORTAL_ID }))).toBeTrue();

      // Withheld from the DOM entirely rather than disabled, because the legacy rule set
      // the control's visibility to false: one row of two offers the command.
      const commands: readonly string[] = textOf('.portal-list__row-command--danger');
      expect(commands.length).toBe(1);
    });

    it('withholds the affordance for a browsed tenant numbered -1', () => {
      // -1 is simultaneously a real tenant and the legacy absent-integer marker, so a
      // comparison against the marker would misclassify this exact case.
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

      http.expectOne(`${PORTALS_URL}/7`).flush(
        { type: 'about:blank', title: 'Conflict', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const queued: readonly AppNotification[] = notifications.notifications();
      expect(queued.length).toBe(1);
      // The legacy screen surfaced this at RedError, not at the success severity.
      expect(queued[0]?.severity).toBe('error');
      expect(queued[0]?.message).toBe(
        'You Can Not Delete The Last Portal In Your Database',
      );
    });

    it('announces a permission refusal at WARNING severity, never at error', () => {
      settleFirstPage([portalRow({ portalId: 9 })]);

      invoke<void>('requestDeletion', portalRow({ portalId: 9 }));
      invoke<void>('onDeletionConfirmed');

      http.expectOne(`${PORTALS_URL}/9`).flush(
        {
          type: 'about:blank',
          title: 'Forbidden',
          status: 403,
          detail: '<br>You do not have permission to perform this action.',
        },
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const queued: readonly AppNotification[] = notifications.notifications();
      expect(queued.length).toBe(1);
      // The legacy access-denied surface used a warning, not an error: the system is
      // working exactly as configured.
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
  });

  // -------------------------------------------------------------------------
  // EMPTY, PAST-THE-END AND FAILURE SURFACES
  // -------------------------------------------------------------------------

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

    it('returns to the first page from the past-the-end surface', () => {
      settleFirstPage([], 40, 3);

      const back: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        'app-empty-state button',
      );
      expect(back.length).toBe(1);
      back[0]?.click();

      const request: TestRequest = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([portalRow()], 40));
      fixture.detectChanges();
    });

    it('draws the pager only when more records exist than fit on one page', () => {
      settleFirstPage([portalRow()], 40);
      expect(host().querySelector('app-pagination')).not.toBeNull();

      invoke<void>('onRetry');
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()], 1));
      fixture.detectChanges();

      expect(host().querySelector('app-pagination')).toBeNull();
    });

    it('shows the indicator while the FIRST page is read, and not on a later read', () => {
      // Nothing has settled yet, so this is the first read.
      expect(host().querySelector<HTMLElement>('app-loading-spinner')).not.toBeNull();
      expect(host().querySelector<HTMLElement>('app-data-table')).toBeNull();

      settleFirstPage([portalRow()], 40);
      expect(host().querySelector<HTMLElement>('app-loading-spinner')).toBeNull();

      // A later read keeps the rows on screen rather than blanking the table.
      invoke<void>('onPageChange', 1);
      fixture.detectChanges();
      expect(host().querySelector<HTMLElement>('app-data-table')).not.toBeNull();

      http
        .expectOne((candidate) => candidate.params.get('pageIndex') === '1')
        .flush(pageOf([portalRow()], 40, 1));
      fixture.detectChanges();
    });

    it('reports a failure that carried no problem document as a plain status message', () => {
      // A request that never reached the server carries no RFC 7807 document at all.
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
        .error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(host().querySelector<HTMLElement>('app-error-banner')).toBeNull();

      const status: HTMLElement | null = host().querySelector<HTMLElement>('[role="status"]');

      expect(status?.textContent?.trim()).toBe('The portals could not be loaded.');

      // Dismissing clears the surface without issuing a request.
      invoke<void>('onFailureDismissed');
      fixture.detectChanges();
      expect(host().querySelector<HTMLElement>('[role="status"]')).toBeNull();
    });

    it('reports a failed listing through the shared banner and can retry', () => {
      http
        .expectOne((candidate) => candidate.url === PORTALS_URL)
        .flush(
          { type: 'about:blank', title: 'Server Error', status: 500, detail: 'Boom.' },
          { status: 500, statusText: 'Server Error' },
        );
      fixture.detectChanges();

      const banner: HTMLElement | null = host().querySelector<HTMLElement>('app-error-banner');
      expect(banner).not.toBeNull();
      expect(banner?.textContent).toContain('Boom.');

      invoke<void>('onRetry');
      http.expectOne((candidate) => candidate.url === PORTALS_URL).flush(pageOf([portalRow()]));
      fixture.detectChanges();

      expect(host().querySelector('app-error-banner')).toBeNull();
    });
  });
});
