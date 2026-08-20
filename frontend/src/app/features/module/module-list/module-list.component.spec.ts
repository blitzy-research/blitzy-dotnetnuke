import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { ModuleStore } from '../../../core/state/module.store';
import { ModuleListComponent } from './module-list.component';

import type { Signal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { AuthSession, CurrentUser } from '../../../core/models/auth.model';
import type { ModuleListItem } from '../../../core/models/module.model';
import type { ApiMeta, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';

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
  return candidate.url === MODULES_URL || candidate.url === MODULES_SEARCH_URL;
}


// THE SESSION THE ROW COMMANDS ARE GATED ON
// The row commands are behind an `administration OR EDIT` gate, so a case that asserts anything about them
// has to state which of those two things the caller is.

/** Builds a caller snapshot carrying exactly the standing each case needs. */
function userWith(permissions: readonly string[], administersPortal: boolean): CurrentUser {
  return {
    userId: 3,
    portalId: -1,
    portalName: 'Runtime Portal',
    username: 'runtime_operator',
    displayName: 'Runtime Operator',
    email: 'operator@runtime.test',
    // A host account is a separate fact from tenant administration and is deliberately NOT set here: the
    // gate reads the tenant determination, so leaving this false keeps each case honest about which arm
    // admitted it.
    isSuperUser: false,
    isPortalAdministrator: administersPortal,
    mustChangePassword: false,
    mustUpdateProfile: false,
    roles: administersPortal ? ['Administrators'] : [],
    permissions,
  };
}

/** Wraps a caller snapshot in a session the storage service accepts. */
function sessionWith(permissions: readonly string[], administersPortal: boolean): AuthSession {
  return {
    // Opaque to every consumer in this file: no case decodes, parses or asserts on either token.
    accessToken: 'access-token-placeholder',
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    refreshToken: 'refresh-token-placeholder',
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: userWith(permissions, administersPortal),
  };
}

// NARROWING WITHOUT AN ESCAPE HATCH

/**
 * Unwraps a required descendant, throwing with the selector when it is absent.
 *
 * @param root The element to search within.
 * @param selector The selector that must match.
 * @returns The matched element, guaranteed present.
 * @throws Error when the selector matches nothing.
 */
function requireElement(root: Element, selector: string): Element {
  const found = root.querySelector(selector);
  if (found === null) {
    throw new Error(`Expected to find "${selector}" in the rendered template.`);
  }
  return found;
}

// ADDRESSES

/** The listing address. */
const MODULES_URL = '/api/v1/modules';

/**
 * The body-bound search address. ⚠ A SEPARATE ADDRESS FROM {@link MODULES_URL} ON PURPOSE: a listing read that
 * carries a term a person typed goes here, so the term never appears in a logged request line.
 */
const MODULES_SEARCH_URL = '/api/v1/modules/search';

/** The per-placement address, which the removal addresses. */
function moduleUrl(moduleId: number): string {
  return `${MODULES_URL}/${moduleId}`;
}

/** The default page size the store asks for when nobody has changed it. */
const DEFAULT_PAGE_SIZE = '10';

// THE WORDING THIS SCREEN PUBLISHES

const PAGE_TITLE = 'Modules';
const PAGE_SUBTITLE = 'Every module placed on a page of this site, one row per placement.';
const TABLE_CAPTION = 'Modules placed on this site';
const SEARCH_PLACEHOLDER = 'Search modules';

const CREATE_ACTION_LABEL = 'Add Module';
const IMPORT_ACTION_LABEL = 'Import Module';

const EDIT_COMMAND_LABEL = 'Edit';
const SETTINGS_COMMAND_LABEL = 'Settings';
const EXPORT_COMMAND_LABEL = 'Export';
const REMOVE_COMMAND_LABEL = 'Delete';

const REMOVE_CONFIRM_TITLE = 'Confirm Delete';

const REMOVE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Module ?';

const REMOVE_SUCCESS_MESSAGE = 'The module placement was removed.';

const DISMISS_LABEL = 'Dismiss';

const RETRY_LABEL = 'Try again';

// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-8c1e4f2b7a934dd6bb18eb211c80319c-a3bd6b7169203331-01';
const CORRELATION_ID = '2f7b8c14-6d3e-42a9-9c51-7e0d3b9a5f62';

function problem(code: string, status: number, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * One listing row. ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so
 * module zero is the first module of an installation. Every default here is chosen so that a truthiness
 * test anywhere in the screen would be caught rather than accidentally satisfied.
 */
function moduleRow(overrides: Partial<ModuleListItem> = {}): ModuleListItem {
  return {
    moduleId: 0,
    tabModuleId: 7,
    tabId: 0,
    moduleDefId: 4,
    moduleTitle: 'Announcements',
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    moduleName: 'DNN_Announcements',
    description: 'Displays announcements',
    version: '01.00.00',
    isAdmin: false,
    moduleOrder: 1,
    allTabs: false,
    visibility: ModuleVisibility.Maximized,
    isDeleted: false,
    displayTitle: true,
    startDate: null,
    endDate: null,
    ...overrides,
  };
}

/** A page of rows. */
function pageOf(
  items: readonly ModuleListItem[],
  totalCount: number = items.length,
  pageIndex = 0,
  pageSize = 10,
): PagedResponse<ModuleListItem> {
  const totalPages: number = pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0;

  return { items, meta: { totalCount, pageIndex, pageSize, totalPages } };
}

describe('ModuleListComponent', () => {
  let fixture: ComponentFixture<ModuleListComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let tokenStorage: TokenStorageService;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The
    // store is pinned to this injector so no case shares its instance with another.
    await TestBed.configureTestingModule({
      imports: [ModuleListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // ⚠ A ROUTE THAT ALWAYS MATCHES, because this screen now keeps its search, ordering and page in the
        // ADDRESS and writes them with a real navigation. An empty route table refuses every navigation, so
        // the write would silently fail and the read that follows the address change would never be issued.
        provideRouter([{ path: '**', component: ModuleListComponent }]),
        ModuleStore,
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    tokenStorage = TestBed.inject(TokenStorageService);

    // THE DEFAULT CALLER ADMINISTERS THE TENANT, AND THAT IS THE HONEST DEFAULT RATHER THAN A CONVENIENCE.
    // This screen's route is declared under the portal-administrator gate, so an administrator is who
    // actually reaches it; every case below that asserts a row command therefore describes a real caller
    // instead of an anonymous one.
    tokenStorage.store(sessionWith([], true));

    // `notify` is the single sink every convenience method delegates to, so this records every message
    // whatever raised it, and it calls through so the service's own queue fills as it would in life.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The first read is issued from `ngOnInit`, during this first pass. */
  /** Lets a navigation this screen started actually happen. */
  /** Navigates to an address BEFORE the screen mounts, which is how an entry on a later page is simulated. */
  async function enterAt(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
  }

  /** The query parameters the screen has actually navigated to. */
  function addressParams(): Readonly<Record<string, string>> {
    const router: Router = TestBed.inject(Router);

    return router.parseUrl(router.url).queryParams as Readonly<Record<string, string>>;
  }

  async function settleAddress(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function create(): void {
    fixture = TestBed.createComponent(ModuleListComponent);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** The one outstanding listing read. */
  function expectList(description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => isListingRead(candidate),
      description ?? 'the listing read',
    );
  }

  /** Answers the outstanding listing read. */
  function answerList(
    items: readonly ModuleListItem[] = [moduleRow()],
    totalCount: number = items.length,
    pageIndex = 0,
  ): TestRequest {
    const call = expectList();

    call.flush(pageOf(items, totalCount, pageIndex));
    fixture.detectChanges();

    return call;
  }

  /** Brings the screen up with a settled page. */
  function arrive(
    items: readonly ModuleListItem[] = [moduleRow()],
    totalCount: number = items.length,
    pageIndex = 0,
  ): void {
    create();
    answerList(items, totalCount, pageIndex);
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** The body rows the shared table painted, excluding its own message row. */
  function bodyRows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>('tr.data-table__row');
  }

  /** The cell text of one painted row, in column order. */
  function cellsOf(rowIndex: number): readonly string[] {
    const row: HTMLTableRowElement | undefined = bodyRows()[rowIndex];

    expect(row).withContext(`row ${rowIndex} is painted`).not.toBeUndefined();

    return Array.from((row as HTMLTableRowElement).querySelectorAll('td,th')).map((cell) =>
      (cell.textContent ?? '').trim(),
    );
  }

  /** Finds a control by its rendered wording, whatever element it is. */
  function control<E extends HTMLElement>(selector: string, label: string): E | undefined {
    return queryAll<E>(selector).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** Finds a control by its rendered wording and asserts it exists. */
  function requiredControl<E extends HTMLElement>(selector: string, label: string): E {
    const element: E | undefined = control<E>(selector, label);

    expect(element).withContext(`the "${label}" control is rendered`).not.toBeUndefined();

    return element as E;
  }

  /** Presses the removal command of one painted row and lets the dialogue open. */
  function requestRemoval(rowIndex = 0): void {
    const row: HTMLTableRowElement | undefined = bodyRows()[rowIndex];

    expect(row).withContext(`row ${rowIndex} is painted`).not.toBeUndefined();

    const button: HTMLButtonElement | undefined = Array.from(
      (row as HTMLTableRowElement).querySelectorAll<HTMLButtonElement>('button'),
    ).find((candidate) => (candidate.textContent ?? '').trim() === REMOVE_COMMAND_LABEL);

    expect(button).withContext('the removal command is offered on the row').not.toBeUndefined();

    (button as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** Presses a button of the open confirmation, by its rendered wording. */
  function pressDialogue(label: string): void {
    const button: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(button).withContext(`the "${label}" button of the dialogue is offered`).not.toBeUndefined();

    (button as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** The messages the notification service was asked to emit, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the first page from the relative collection address and nothing else', () => {
      create();

      const call = expectList();

      expect(call.request.url.startsWith('http')).withContext('relative address').toBeFalse();
      expect(sentFilters(call).get('pageIndex')).toBe('0');
      expect(sentFilters(call).get('pageSize')).toBe(DEFAULT_PAGE_SIZE);

      // No filter and no ordering are sent on the first read: both are the server's to choose until an
      // operator states one.
      expect(sentFilters(call).has('query')).withContext('no filter').toBeFalse();
      expect(sentFilters(call).has('sortBy')).withContext('no ordering').toBeFalse();
      expect(sentFilters(call).has('sortDir')).withContext('no direction').toBeFalse();

      call.flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // One read, and one only. A screen that loaded from an effect would issue an unpredictable
      // number here, which is exactly why the read lives in the lifecycle hook.
      httpMock.expectNone(() => true);
      expect(bodyRows()).toHaveSize(1);
    });

    it('paints the page heading and the sentence that states what a row is', () => {
      arrive();

      expect((query('h1')?.textContent ?? '').trim()).toBe(PAGE_TITLE);
      expect(textOf('.page-header__subtitle')).toContain(PAGE_SUBTITLE);
    });

    it('names the grid for a reader through the shared caption slot', () => {
      arrive();

      // The caption is the grid's accessible name. It is visually hidden by the shared table and is
      // therefore asserted as text rather than as something visible.
      expect((query('caption')?.textContent ?? '').trim()).toBe(TABLE_CAPTION);
    });

    it('offers the two page-level actions as real links carrying their routes', () => {
      arrive();

      const create$ = requiredControl<HTMLAnchorElement>('a', CREATE_ACTION_LABEL);
      const import$ = requiredControl<HTMLAnchorElement>('a', IMPORT_ACTION_LABEL);

      // Links rather than imperative navigations, so both can be middle-clicked, copied and
      // bookmarked. The rendered attribute is what an operator's browser acts on.
      expect(create$.getAttribute('href')).toBe('/modules/new');
      expect(import$.getAttribute('href')).toBe('/modules/import');
    });

    it('offers the filter control with this screen own placeholder', () => {
      arrive();

      const field = query<HTMLInputElement>('input[type="search"]');

      expect(field).withContext('the filter control is rendered').not.toBeNull();
      expect((field as HTMLInputElement).placeholder).toBe(SEARCH_PLACEHOLDER);
    });

    it('shows the wait inside the grid rather than beside it while the first read is outstanding', () => {
      create();

      // The shared table renders both the wait and the empty state itself, in one spanning row, and lets
      // the wait win. This screen must therefore render NEITHER of its own, or a person would meet two
      // indicators for one wait.
      const message = query<HTMLElement>('td.data-table__message[data-placeholder]');

      expect(message).withContext('the table carries the wait').not.toBeNull();

      // ⚠ THE INDICATOR THE TABLE OWNS IS ITSELF AN `app-loading-spinner`, SO ITS MERE PRESENCE PROVES
      // NOTHING. What distinguishes the two is WHERE it sits: the table's own lives inside the spanning
      // message cell, so an indicator anywhere OUTSIDE that cell would be this screen's second one.
      expect((message as HTMLElement).querySelector('app-loading-spinner'))
        .withContext('the wait is announced from inside the spanning cell')
        .not.toBeNull();
      expect(
        queryAll<HTMLElement>('app-loading-spinner').filter(
          (indicator) => indicator.closest('td.data-table__message') === null,
        ),
      )
        .withContext('no second indicator is emitted by this screen')
        .toHaveSize(0);
      expect(query('app-empty-state'))
        .withContext('no second empty state is emitted by this screen')
        .toBeNull();

      answerList();
    });

    it('shows the grid own empty state when the tenant has no placements', () => {
      arrive([], 0);

      expect(bodyRows()).toHaveSize(0);
      expect(query('.data-table__message[data-placeholder]'))
        .withContext('the table states the absence itself')
        .not.toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE PROJECTION
  // ---------------------------------------------------------------------------------------------------

  describe('the projection', () => {
    it('paints a placement identified by zero, which is an ordinary module', () => {
      arrive([moduleRow({ moduleId: 0, tabModuleId: 0 })]);

      const cells: readonly string[] = cellsOf(0);

      // ⚠ IF ANY LINK BUILDER OR ANY CELL TESTED THE IDENTIFIER FOR TRUTHINESS, THIS ROW WOULD BE THE ONE
      // IT BROKE. The identity column is seeded from zero, so zero addresses the first module of an
      // installation and must render and link exactly like any other.
      expect(cells).toContain('0');
      expect(textOf('tr.data-table__row a').length).withContext('links offered').toBeGreaterThan(0);
    });

    it('renders the all-pages flag as words, including the negative one', async () => {
      arrive([moduleRow({ allTabs: false })]);

      expect(cellsOf(0)).toContain('No');

      await answerAfterReplacement([moduleRow({ allTabs: true })]);

      expect(cellsOf(0)).toContain('Yes');
    });

    it('renders each declared visibility code as its display word', () => {
      arrive([
        moduleRow({ moduleId: 0, visibility: ModuleVisibility.Maximized }),
        moduleRow({ moduleId: 1, tabModuleId: 8, visibility: ModuleVisibility.Minimized }),
        moduleRow({ moduleId: 2, tabModuleId: 9, visibility: ModuleVisibility.None }),
      ]);

      expect(cellsOf(0)).toContain('Maximized');
      expect(cellsOf(1)).toContain('Minimized');

      // ⚠ ZERO IS A REAL STATE AND IS THE LEGACY DEFAULT, so the first row must carry a word rather than an
      // empty cell. A lookup written as `VISIBILITY_LABEL[code] || ''` would pass the two rows below and
      // fail this one, which is exactly why it is asserted as NON-EMPTY and not merely as "contains".
      expect(cellsOf(0).filter((text) => text === 'Maximized'))
        .withContext('code 0 resolves to a real word, never to an empty cell')
        .toHaveSize(1);

      expect(cellsOf(2)).toContain('None');
      expect(cellsOf(2).filter((text) => text === 'None'))
        .withContext('code 2 renders the real word "None", never an empty cell')
        .toHaveSize(1);
    });

    // ⚠ THIS REPLACES A FACT THAT REQUIRED BOTH CELLS TO RENDER EMPTY, AND THE EMPTINESS WAS THE DEFECT —
    // QA-15. Two different absences on the wire — a null and the legacy minimum-date marker — still produce
    // ONE rendering, because to a person they mean the same thing, and that half of the claim is unchanged.
    // What changed is what that one rendering IS: a cell with no text and no children told a sighted reader
    // nothing distinguishable from a failed render and told a screen reader nothing at all. Both now render
    // the shared absent value, identically to an absent tally on the portal listing and an absent period on
    // the role listing.
    // ⚠ EXACTLY ONE COLUMN TRACK IS LEFT FLEXIBLE, AND THAT IS A REQUIREMENT RATHER THAN AN OMISSION — QA-09.
    //
    // Under `table-layout: fixed` the percentage tracks resolve against the table width and whatever is LEFT
    // OVER goes to the columns that declared something else. With every column weighted, that leftover went to
    // the command columns: the single commands column was left one percent, so its four commands stacked one per line and made every row 196px tall. One unweighted column absorbs the slack instead, so every
    // other track resolves to exactly the share it declares.
    // ⚠ A NAME IS ONE TOKEN, AND THE TWO NARROWEST COLUMNS HERE USED TO SPLIT THEIRS. Measured before these
    // were marked atomic: `Announcements` painted `Announcem` + `ents` at 1280 and `Announce` + `ments` at 768,
    // `Text/HTML` painted `Text/HTM` + `L`, and the package beside it painted `QA_Announcement` + `s`,
    // orphaning one letter by 3.39px. Both columns now declare a weight as well, so at the shared floor they
    // hold their values outright rather than relying on the ellipsis; see the case below for which column
    // carries the slack instead, and why it is no longer this one.
    it('keeps the module and package names whole instead of breaking them mid-word', () => {
      arrive([moduleRow()]);

      const headers: readonly Element[] = queryAll<Element>('thead th');
      const atomicAt = (index: number): string | null => headers[index]?.getAttribute('data-atomic') ?? null;
      const headingAt = (index: number): string => (headers[index]?.textContent ?? '').trim();

      // Addressed by position and cross-checked by heading, so a reordering of the set cannot silently move
      // these assertions onto a different track.
      expect(headingAt(3)).toContain('Module');
      expect(atomicAt(3)).withContext('a module type name is one token').toBe('true');

      expect(headingAt(4)).toContain('Package');
      expect(atomicAt(4)).withContext('a package name is one token').toBe('true');

      // ⚠ THE COUNTERPART, AND THE REASON THIS IS NOT A BLANKET RULE. The title column holds a phrase an
      // operator wrote, so wrapping is correct there and ellipsising it would hide text that fits on a second
      // line. It measured zero fractures at both viewports even for the deliberately long title.
      expect(atomicAt(2)).withContext('a title is a phrase and should wrap').toBeNull();
    });

    it('leaves exactly one column track flexible so the declared tracks resolve as written', () => {
      arrive([moduleRow()]);

      const tracks = queryAll<HTMLTableColElement>('colgroup col');
      const headings = queryAll<Element>('thead th');

      expect(tracks.length).withContext('one track per rendered column').toBeGreaterThan(0);
      expect(tracks.length).toBe(headings.length);

      const flexible: readonly number[] = tracks
        .map((track, index) => ({ index, declared: track.style.inlineSize }))
        .filter((entry) => entry.declared === '')
        .map((entry) => entry.index);

      expect(flexible.length).withContext('one and only one flexible track').toBe(1);

      // ⚠ AND IT MATTERS WHICH ONE, WHICH THIS CASE PREVIOUSLY DID NOT SAY. The leftover is the SMALLEST share
      // on this grid, not the largest - the weighted tracks claim 71% and the command token takes a fixed 144px -
      // so whichever column abstains is the one that gets squeezed. It used to be the row header, and measured at
      // 1024 that gave the identity 76.83px against a 117.41px requirement, ellipsising six of eight module
      // names: the column hoisted to the front and pinned sticky so a reader can identify a row was the first
      // value cut. The title carries it now, being the only prose column here and the only one that wraps
      // gracefully rather than clipping.
      const identity = headings.findIndex(
        (heading) => heading.getAttribute('data-row-identity') === 'true',
      );

      expect(identity).withContext('the grid declares a row identity').toBeGreaterThan(-1);
      expect(flexible).not.toContain(identity);
      expect(headings[flexible[0]]?.textContent ?? '')
        .withContext('the flexible track is the title')
        .toContain('Title');
      expect(tracks[identity]?.style.inlineSize)
        .withContext('and the identity declares a real weight')
        .toMatch(/%$/);

      // The identity's requirement, measured in Chrome: `Announcements` needs 108.41px, and this cell carries a
      // 1px separator border in its pinned state on top of the shared 4px inline padding either side, so 9px of
      // chrome rather than 8px. The floor every listing sits at or above is 960px.
      const identityPx = (Number.parseFloat(tracks[identity]?.style.inlineSize ?? '0') / 100) * 960;

      expect(identityPx - 9)
        .withContext(`the identity resolves to ${String(identityPx)}px at the floor, and needs 108.41px`)
        .toBeGreaterThanOrEqual(108.41);

      // The command tracks declare their own token rather than inheriting the slack.
      for (let index = 0; index < 1; index += 1) {
        expect(tracks[index]?.style.inlineSize).toContain('--table-commands-column-inline-size');
      }

      // Every remaining track declares a percentage, so nothing else can quietly become flexible.
      tracks.forEach((track, index) => {
        if (index < 1 || flexible.includes(index)) {
          return;
        }

        expect(track.style.inlineSize).withContext(`track ${index}`).toMatch(/%$/);
      });
    });

    // ⚠ AN ENDED PLACEMENT IS QUALIFIED IN WORDS — QA-19, and it is a consistency correction rather than a
    // flourish. Measured before it existed: a placement whose term ended in 2021 and one running to 2027
    // rendered byte-identically — same colour, same weight, no other mark — on the listing whose purpose is
    // administering placements, while the portal listing beside it had already grown exactly this qualifier
    // for exactly this fact. The word, the class name and the judgement point (the start of today, so a term
    // ending today reads as ended on both screens) are shared with that listing deliberately.
    it('qualifies an ended term in words, and leaves a running one unqualified', () => {
      arrive([moduleRow({ startDate: '2020-01-01T00:00:00Z', endDate: '2021-01-01T00:00:00Z' })]);

      const qualifiers = queryAll<HTMLElement>('.module-list__expired');

      expect(qualifiers.map((node) => (node.textContent ?? '').trim())).toEqual(['Expired']);

      // ⚠ AND THE TWO VALUES ARE SEPARATED BY A REAL SPACE CHARACTER, not by a margin. The compiler strips
      // whitespace between elements, so without the explicit entity the cell's text content was the single
      // run "1/1/2021Expired" — one word to a screen reader, to a copy-paste and to any text extraction,
      // while a sighted reader saw a gap that exists only in paint.
      const cells: readonly string[] = cellsOf(0);
      const endCell: string = cells.at(-1) ?? '';

      expect(endCell).toBe('1/1/2021 Expired');
      expect(endCell).withContext('the date itself is untouched').toContain('1/1/2021');
    });

    it('leaves a running term unqualified', () => {
      arrive([moduleRow({ startDate: '2026-01-01T00:00:00Z', endDate: '2099-01-01T00:00:00Z' })]);

      expect(queryAll<Element>('.module-list__expired')).toHaveSize(0);
      expect(cellsOf(0).at(-1)).toBe('1/1/2099');
    });

    it('renders an absent date and the legacy marker date as the shared absent value alike', () => {
      arrive([moduleRow({ startDate: null, endDate: '0001-01-01T00:00:00Z' })]);

      const cells: readonly string[] = cellsOf(0);

      expect(cells.filter((text) => text === '\u2014not recorded').length)
        .withContext('both absences render the shared mark and its shared wording')
        .toBe(2);

      const absent = queryAll<HTMLElement>('tbody app-absent-value');
      expect(absent).toHaveSize(2);
      expect(
        absent.map((node) => node.querySelector('span[aria-hidden="true"]')?.textContent),
      ).toEqual(['\u2014', '\u2014']);

      const rowText: string = cells.join(' ');

      expect(rowText).withContext('the marker date is never painted').not.toContain('0001');
      expect(rowText).not.toContain('01/01/0001');
      expect(rowText).not.toContain('1/1/0001');
    });

    it('paints a far-future date as the real value it is, rather than blanking it too', () => {
      // ⚠ THE COMPANION TO THE CASE ABOVE, AND THE ONE THAT STOPS THE BLANKING FROM OVER-REACHING. A
      // perpetual expiry is ordinary data: the shared pipe tests for `0001-01-01` specifically and never
      // for an upper bound, so a year-9999 value must survive to the cell.
      arrive([moduleRow({ startDate: '2024-03-01T00:00:00Z', endDate: '9999-12-31T00:00:00Z' })]);

      const cells: readonly string[] = cellsOf(0);
      const rowText: string = cells.join(' ');

      // The rendered spelling is the runtime's locale decision and is deliberately NOT asserted; the
      // YEAR is what proves the value was neither blanked nor rewritten.
      expect(rowText).withContext('the far-future year survives to the cell').toContain('9999');
      expect(rowText).withContext('the real start date survives too').toContain('2024');

      // And neither date collapsed into the empty rendering the sentinel gets.
      expect(cells.filter((text) => text.length === 0).length)
        .withContext('no schedule cell was blanked')
        .toBe(0);
    });

    it('paints one row per placement when a module is placed on every page', () => {
      // The same module, twice, with two placements. This is the listing contract's central fact and
      // the reason the removal carries both identities.
      arrive([
        moduleRow({ moduleId: 4, tabModuleId: 11, allTabs: true, moduleOrder: 1 }),
        moduleRow({ moduleId: 4, tabModuleId: 12, allTabs: true, moduleOrder: 2 }),
      ]);

      expect(bodyRows()).withContext('two placements, two rows').toHaveSize(2);
    });

    it('offers three links and one removal on every row, each named for a reader', () => {
      arrive([moduleRow({ moduleId: 3 })]);

      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;
      const links: readonly HTMLAnchorElement[] = Array.from(
        row.querySelectorAll<HTMLAnchorElement>('a'),
      );

      expect(links.map((link) => (link.textContent ?? '').trim())).toEqual([
        EDIT_COMMAND_LABEL,
        SETTINGS_COMMAND_LABEL,
        EXPORT_COMMAND_LABEL,
      ]);
      expect(links.map((link) => link.getAttribute('href'))).toEqual([
        '/modules/3',
        '/modules/3/settings',
        '/modules/3/export',
      ]);

      // Every command names its subject, because 'Edit' repeated down a column tells a screen-reader
      // user which row they are on only if the accessible name says so.
      expect(links.map((link) => link.getAttribute('aria-label'))).toEqual([
        'Edit Announcements',
        'Settings Announcements',
        'Export Announcements',
      ]);

      const remove = requiredControl<HTMLButtonElement>('button', REMOVE_COMMAND_LABEL);

      expect(remove.getAttribute('aria-label')).toBe('Delete Announcements');
      // An explicit type, so it can never submit an ancestor form.
      expect(remove.getAttribute('type')).toBe('button');
    });

    /** ⚠ REWRITTEN. This spec used to assert `labels).toContain('Edit ')` — it pinned the defect. */
    it('names a command of an untitled placement from the definition and the identifier', () => {
      arrive([moduleRow({ moduleTitle: null, moduleId: 10 })]);

      const row = bodyRows()[0] as HTMLTableRowElement;
      const names: readonly string[] = Array.from(
        row.querySelectorAll<HTMLElement>('a, button'),
      ).map((control) => control.getAttribute('aria-label') ?? '');

      expect(names.length).withContext('all four commands').toBe(4);
      expect(names).toEqual([
        'Edit Announcements (module 10)',
        'Settings Announcements (module 10)',
        'Export Announcements (module 10)',
        'Delete Announcements (module 10)',
      ]);

      names.forEach((name) => {
        expect(name).withContext('never a bare verb').not.toMatch(/^(Edit|Settings|Export|Delete)\s*$/);
        expect(name).withContext('no absence marker leaks into a name').not.toContain('null');
        expect(name).withContext('nor the other one').not.toContain('undefined');
      });

      // The pointer user gets the same phrase, which is what the legacy `Edit.Text` tooltip did.
      Array.from(row.querySelectorAll<HTMLElement>('a, button')).forEach((control) => {
        expect(control.getAttribute('title')).toBe(control.getAttribute('aria-label'));
      });
    });

    /**
     * A titled row is deliberately left exactly as it was: its title already identifies it, and appending
     * an identifier to every name would add noise a reader hears on every row.
     */
    it('leaves a titled placement to its own title, with no identifier appended', () => {
      arrive([moduleRow({ moduleTitle: 'Announcements', moduleId: 10 })]);

      const remove = requiredControl<HTMLButtonElement>('button', REMOVE_COMMAND_LABEL);

      expect(remove.getAttribute('aria-label')).toBe('Delete Announcements');
      expect(remove.getAttribute('aria-label')).not.toContain('module 10');
    });

    /**
     * The last resort. A placement with neither a title nor any definition name still has an identity,
     * and `Modules.ModuleID` is `IDENTITY(0, 1)` — so nought is a real module and must never be treated
     * as absent.
     */
    it('falls back to the identifier alone when no name of any kind is recorded', () => {
      arrive([moduleRow({ moduleTitle: '   ', friendlyName: null, moduleName: null, moduleId: 0 })]);

      const remove = requiredControl<HTMLButtonElement>('button', REMOVE_COMMAND_LABEL);

      expect(remove.getAttribute('aria-label')).toBe('Delete module 0');
    });

    /**
     * The title CELL states the absence rather than rendering blank. Measured with the cell left blank:
     * its whole content is two literal spaces in a single text node with no element children, and Chrome
     * reports the cell as unnamed — indistinguishable from a cell that failed to render.
     */
    it('states an absent title in the cell instead of leaving it blank', () => {
      arrive([moduleRow({ moduleTitle: '' })]);

      const cell = (bodyRows()[0] as HTMLTableRowElement).querySelectorAll('td')[2] as HTMLElement;

      expect((cell.textContent ?? '').trim()).withContext('not blank').not.toBe('');
      expect(cell.querySelector('[aria-hidden="true"]')?.textContent?.trim())
        .withContext('the painted mark')
        .toBe('\u2014');
      expect(cell.querySelector('.module-list__absent-value')?.textContent?.trim())
        .withContext('and the meaning, for a reader who cannot see it')
        .toBe('no title recorded');
    });

    it('escapes a hostile title rather than parsing it into elements', () => {
      arrive([moduleRow({ moduleTitle: '<img src=x onerror="window.__listed=true">' })]);

      expect(queryAll('img')).withContext('no element parsed out of a title').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__listed'])
        .withContext('the title was never evaluated')
        .toBeUndefined();
      expect(host().innerHTML).toContain('&lt;img');
    });
  });

  // THE PERMISSION GATE ON THE ROW COMMANDS
  // Three cases, because the gate has three outcomes and the first two would collapse into one under a
  // single-arm gate: admitted BY ADMINISTRATION with no key held, admitted BY THE KEY without
  // administering, and refused.
  describe('the permission gate on the row commands', () => {
    /** Every command the row offers, by its accessible name, in document order. */
    function rowCommandNames(): readonly string[] {
      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;

      return Array.from(row.querySelectorAll<HTMLElement>('a, button')).map((control) =>
        control.getAttribute('aria-label') ?? (control.textContent ?? '').trim(),
      );
    }

    it('offers the commands to a tenant administrator who holds no permission key at all', () => {
      tokenStorage.store(sessionWith([], true));
      arrive([moduleRow({ moduleId: 3 })]);

      expect(rowCommandNames()).toEqual([
        'Edit Announcements',
        'Settings Announcements',
        'Export Announcements',
        'Delete Announcements',
      ]);
    });

    it('offers the commands to a caller who holds EDIT without administering the tenant', () => {
      // The second arm, which is the shared directive. The key list the session carries is the union of
      // everything the caller holds anywhere in the portal, so a grant on a single module is enough to
      // offer the column — the server still decides each request.
      tokenStorage.store(sessionWith(['EDIT'], false));
      arrive([moduleRow({ moduleId: 3 })]);

      expect(rowCommandNames()).toEqual([
        'Edit Announcements',
        'Settings Announcements',
        'Export Announcements',
        'Delete Announcements',
      ]);
    });

    it('offers no command to a caller who neither administers the tenant nor holds EDIT', () => {
      // `VIEW` is held deliberately rather than nothing at all: it proves the gate tests the REQUIRED
      // key rather than merely testing whether any key is held.
      tokenStorage.store(sessionWith(['VIEW'], false));
      arrive([moduleRow({ moduleId: 3 })]);

      expect(rowCommandNames()).withContext('the whole column is withheld').toEqual([]);

      // Removal, not concealment: the controls are absent from the document rather than hidden, so
      // nothing remains for a cleared style or a stale accessibility tree to expose.
      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;

      expect(row.querySelectorAll('.module-list__row-command')).toHaveSize(0);

      expect(bodyRows()).withContext('the placement is still listed').toHaveSize(1);
      expect((row.textContent ?? '')).toContain('Announcements');
    });

    it('withdraws the commands when the session is replaced by one that no longer qualifies', () => {
      tokenStorage.store(sessionWith(['EDIT'], false));
      arrive([moduleRow({ moduleId: 3 })]);

      expect(rowCommandNames()).withContext('admitted by the key').toHaveSize(4);

      tokenStorage.clear();
      fixture.detectChanges();

      expect(rowCommandNames()).withContext('withdrawn with the session').toEqual([]);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — FILTERING
  // ---------------------------------------------------------------------------------------------------

  /**
   * A failed read must offer a way back. Measured before this existed: with the network unreachable the
   * whole page offered ZERO affordances matching retry, try-again, reload or refresh — sixty-four
   * interactive elements healthy and sixty-five failed, the single addition being `Dismiss`, which clears
   * the banner and re-attempts nothing.
   */
  describe('recovering from a failed read', () => {
    /** Puts the listing into its failed state through the transport, as the defect occurs. */
    function failTheRead(): void {
      create();
      expectList('the first read').error(new ProgressEvent('error'), {
        status: 0,
        statusText: 'Unknown Error',
      });
      fixture.detectChanges();
    }

    it('offers a retry command beside the dismiss command', () => {
      failTheRead();

      const names: readonly string[] = Array.from(
        queryAll<HTMLButtonElement>('.module-list__status button'),
      ).map((control) => (control.textContent ?? '').trim());

      expect(names).toEqual([RETRY_LABEL, DISMISS_LABEL]);
    });

    it('re-issues the read, and does not reset the operator to the first page of everything', fakeAsync(() => {
      create();
      answerList([moduleRow()], 250, 0);

      // Put the operator somewhere specific first, so a retry that discarded their place would show.
      const field = query<HTMLInputElement>('input[type="search"]') as HTMLInputElement;

      field.value = 'news';
      field.dispatchEvent(new Event('input'));
      tick(300);
      fixture.detectChanges();

      const filtered = expectList('the filtered read');

      filtered.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
      fixture.detectChanges();

      requiredControl<HTMLButtonElement>('button', RETRY_LABEL).click();
      fixture.detectChanges();

      const retried = expectList('the retried read');

      expect(sentFilters(retried).get('query'))
        .withContext('the retry resumes the filter the failure interrupted')
        .toBe('news');
    }));

    it('clears the recorded failure so a second attempt is judged on its own outcome', () => {
      failTheRead();

      expect(query('.module-list__status')).withContext('failed').not.toBeNull();

      requiredControl<HTMLButtonElement>('button', RETRY_LABEL).click();
      fixture.detectChanges();

      expect(query('.module-list__status'))
        .withContext('the old banner does not stand while the retry is in flight')
        .toBeNull();

      answerList();

      expect(query('.module-list__status')).withContext('and is gone on success').toBeNull();
    });
  });

  describe('the free-text filter', () => {
    /**
     * The box must show the filter that is actually in force. Measured before the reconciling effect
     * existed: search for a term, leave the screen, come back, and the box read the empty string while
     * the grid was still filtered — 248 rows of a collection of 250, the two withheld rows unmentioned,
     * and the return request still carrying the term.
     */
    it('shows a filter that was already in force when the screen was reached', fakeAsync(() => {
      create();
      answerList();

      const field = query<HTMLInputElement>('input[type="search"]') as HTMLInputElement;

      field.value = 'news';
      field.dispatchEvent(new Event('input'));
      tick(300);
      fixture.detectChanges();
      expectList('the filtered read').flush(pageOf([moduleRow()], 1, 0));
      fixture.detectChanges();

      // The operator's own text is left alone while they are still on the screen — the echo of this
      // screen's own request must never be adopted back over what the box holds.
      expect((query<HTMLInputElement>('input[type="search"]') as HTMLInputElement).value).toBe('news');

      // Now rebuild the component against the SAME root-provided store, which is what leaving and
      // returning does.
      fixture.destroy();
      create();
      answerList();

      expect((query<HTMLInputElement>('input[type="search"]') as HTMLInputElement).value)
        .withContext('the box reports the filter the grid is actually showing')
        .toBe('news');
    }));

    it('re-reads with the typed text once the shared control emits', fakeAsync(() => {
      create();
      answerList();

      const field = query<HTMLInputElement>('input[type="search"]') as HTMLInputElement;

      field.value = 'news';
      field.dispatchEvent(new Event('input'));

      tick(300);
      fixture.detectChanges();

      const call = expectList('the filtered read');

      // MIGRATION: THE FREE-TEXT FILTER IS NET-NEW; THE LEGACY HAD NO MODULE SEARCH OF ANY KIND.
      // `Library/Components/Modules/ModuleController.vb:L1032` declares `Public Function
      // GetSearchModules(ByVal PortalId As Integer) As ArrayList`, which is the search-INDEXING surface of
      // the legacy searchable-module contract — it returns the modules that SUPPORT search so an indexer
      // can walk them — and is emphatically not a user-facing query.
      expect(sentFilters(call).get('query')).toBe('news');

      const sent: string = sentFilters(call).get('query') ?? '';

      expect(sent).withContext('no wildcard is added by the client').not.toContain('%');
      expect(sent.length).withContext('the text travels byte-for-byte').toBe('news'.length);
      // The whole serialised query string is checked too, because a wildcard added by the parameter
      // builder rather than by the screen would still reach the server.
      expect(call.request.urlWithParams).not.toContain('%25');

      // A changed filter returns to the first page: a coordinate measured against one match set does
      // not address the same rows once the set changes.
      expect(sentFilters(call).get('pageIndex')).toBe('0');

      call.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
      tick();
    }));

    it('treats emptied text as no filter rather than as a filter for nothing', fakeAsync(() => {
      create();
      answerList();

      const field = query<HTMLInputElement>('input[type="search"]') as HTMLInputElement;

      field.value = 'news';
      field.dispatchEvent(new Event('input'));
      tick(300);
      fixture.detectChanges();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      field.value = '';
      field.dispatchEvent(new Event('input'));
      tick(300);
      fixture.detectChanges();

      const cleared = expectList('the unfiltered read');

      expect(sentFilters(cleared).has('query')).withContext('the parameter is omitted').toBeFalse();

      cleared.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
      tick();
    }));
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — ORDERING
  // ---------------------------------------------------------------------------------------------------

  describe('ordering', () => {
    /** The sort buttons the shared table rendered, in column order. */
    function sortButtons(): readonly HTMLButtonElement[] {
      return queryAll<HTMLButtonElement>('button.data-table__sort');
    }

    it('offers ordering on exactly the four keys the endpoint accepts', () => {
      arrive();

      // Only a column whose key IS a permitted sort name declares itself sortable, so no control on
      // this screen can produce a request the server would reject.
      const headings: readonly string[] = sortButtons().map((button) =>
        (button.textContent ?? '').trim(),
      );

      expect(headings).withContext('four sortable columns').toHaveSize(4);
    });

    it('re-reads ordered by the pressed column, from the first page', async () => {
      arrive([moduleRow()], 40);

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();

      const call = expectList('the ordered read');

      expect(sentFilters(call).get('sortBy')).withContext('a permitted key').not.toBeNull();
      // The direction arrives in the server's own spelling and needs no translation.
      expect(sentFilters(call).get('sortDir')).toBe('Ascending');
      expect(sentFilters(call).get('pageIndex')).toBe('0');

      call.flush(pageOf([moduleRow()], 40));
      fixture.detectChanges();
    });

    it('reverses the direction on a second press of the same column', async () => {
      arrive();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();

      const reversed = expectList('the reversed read');

      expect(sentFilters(reversed).get('sortDir')).toBe('Descending');

      reversed.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });

    it('clears the ordering on a third press, and sends neither parameter', async () => {
      arrive();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();

      const cleared = expectList('the unordered read');

      expect(sentFilters(cleared).has('sortBy')).withContext('no key is sent').toBeFalse();
      expect(sentFilters(cleared).has('sortDir')).withContext('no direction is sent').toBeFalse();
      expect(sentFilters(cleared).get('pageIndex')).toBe('0');

      cleared.flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // And the headings say so. ⚠ CLEARING RETURNS THIS SCREEN TO ITS ARRIVAL STATE, WHICH IS NOT THE SAME
      // AS AN UNORDERED ONE, and that distinction is this screen's alone among the four listings.
      const orderedHeadings: readonly string[] = queryAll<HTMLElement>('th.data-table__header')
        .filter((cell) => {
          const state: string | null = cell.getAttribute('aria-sort');

          return state === 'ascending' || state === 'descending';
        })
        .map((cell) => (cell.textContent ?? '').replace(/\s+/g, ' ').trim());

      expect(orderedHeadings.length)
        .withContext('one heading states the order, and it is the server\'s own')
        .toBe(1);
      expect(orderedHeadings[0] ?? '')
        .withContext('the pressed column has fallen silent and the default order stands again')
        .toContain('Title');
    });

    it('reflects the held ordering back into the grid own sort state', async () => {
      arrive();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // The state a reader perceives is the column heading's own, which the shared table derives from the
      // two inputs this screen supplies from the store. Asserting the attribute proves the round trip
      // rather than just the request.
      const sorted: readonly string[] = queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');

      expect(sorted).withContext('exactly one column reports itself sorted').toEqual(['ascending']);
    });

    it('announces the ordering on the ACTIVE column alone, and on no other', async () => {
      arrive();

      const headers: readonly HTMLElement[] = queryAll<HTMLElement>('th.data-table__header');
      const announced = (): readonly (string | null)[] =>
        queryAll<HTMLElement>('th.data-table__header').map((cell) => cell.getAttribute('aria-sort'));

      expect(announced().filter((value) => value === 'none'))
        .withContext('the three inactive sortable columns each report themselves unsorted')
        .toHaveSize(3);
      expect(announced().filter((value) => value === null))
        .withContext('the six non-sortable columns carry no ordering state at all')
        .toHaveSize(6);
      expect(announced().filter((value) => value === 'ascending'))
        .withContext('exactly one column states the order the rows are already in')
        .toHaveSize(1);
      expect(announced().filter((value) => value === 'descending'))
        .withContext('and the default order is ascending, not descending')
        .toHaveSize(0);

      // ⚠ NAMED, NOT COUNTED. A count alone would pass if the WRONG column claimed the order, which
      // would be a worse lie than claiming none at all.
      const effective: readonly string[] = headers
        .filter((cell) => cell.getAttribute('aria-sort') === 'ascending')
        .map((cell) => (cell.textContent ?? '').replace(/\s+/g, ' ').trim());

      expect(effective.length).toBe(1);
      expect(effective[0] ?? '').withContext('the endpoint orders by title').toContain('Title');

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // ⚠ `sortBy` AND `sortDir` ARE THE SOLE SOURCE OF TRUTH FOR THIS ATTRIBUTE. They are supplied from
      // the store, so the heading that announces itself sorted is the one the NEXT request would order by —
      // the announcement and the wire cannot disagree.
      expect(announced().filter((value) => value === 'ascending')).toHaveSize(1);
      expect(announced().filter((value) => value === 'descending')).toHaveSize(0);
      expect(announced().filter((value) => value === 'none'))
        .withContext('the other three sortable columns fall back to unsorted')
        .toHaveSize(3);
      expect(headers).withContext('the column count is unchanged by ordering').toHaveSize(10);
    });

    it('scopes every heading to its column and names the grid with a real caption', () => {
      arrive();

      const headers: readonly HTMLElement[] = queryAll<HTMLElement>('th[scope="col"]');

      expect(headers).withContext('ten columns, ten headings').toHaveSize(10);

      // EVERY heading, without exception — including the header-less commands column, which is hidden
      // from sight but stays named in the accessibility tree.
      headers.forEach((heading, index) => {
        expect(heading.getAttribute('scope'))
          .withContext(`heading ${index} scopes itself to its column`)
          .toBe('col');
      });

      // The caption must resolve to a REAL TEXT NODE rather than to an empty element: the shared table
      // renders the element unconditionally and falls back to its own placeholder wording when nothing is
      // projected, so an empty caption would leave the grid with a misleading accessible name rather than
      // with none.
      const caption: Element = requireElement(host(), 'caption');

      expect((caption.textContent ?? '').trim())
        .withContext('the caption carries this screen own wording')
        .toBe(TABLE_CAPTION);
      expect((caption.textContent ?? '').trim().length)
        .withContext('and it is a real text node, not an empty element')
        .toBeGreaterThan(0);

      // Every row command is an icon-free text control, and each carries a non-empty accessible name. The
      // global register measured for the two that have one is `Website/App_GlobalResources/
      // SharedResources.resx`: `Edit.Text` = 'Edit' and `cmdDelete.Text` = 'Delete'.
      const row: Element = requireElement(host(), 'tr.data-table__row');

      Array.from(row.querySelectorAll('a, button')).forEach((command) => {
        expect((command.getAttribute('aria-label') ?? '').trim().length)
          .withContext(`the "${(command.textContent ?? '').trim()}" command has an accessible name`)
          .toBeGreaterThan(0);
      });

      // And no raster icon stands in for a name anywhere, which is the specific legacy defect above.
      expect(row.querySelectorAll('img')).withContext('no unnamed graphic commands').toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — PAGING
  // ---------------------------------------------------------------------------------------------------

  describe('paging', () => {
    it('draws no steps when the whole match set fits on one page, but still states the count', () => {
      arrive([moduleRow()], 1);

      expect(query('.pagination')).withContext('the group and its count').not.toBeNull();
      expect((query('.pagination__status')?.textContent ?? '').trim()).toContain('of 1');
      expect(queryAll('button.pagination__button')).withContext('nowhere to step to').toHaveSize(0);
    });

    it('mounts nothing at all when nothing matched, so no empty container is left behind', () => {
      arrive([], 0);

      // The table's own empty state says what happened in words; a pager counting to nought would say it
      // worse, and an empty custom element would say nothing while still being in the document.
      expect(query('app-pagination')).toBeNull();
      expect(query('.pagination')).toBeNull();
    });

    it('draws the pager once more records exist than fit on the page', () => {
      arrive([moduleRow()], 40);

      expect(query('.pagination')).not.toBeNull();
      // The counter a person reads is one-based and is rendered INSIDE the pager. It never leaves it.
      expect((query('.pagination__position')?.textContent ?? '').trim()).toBe('1 / 4');
    });

    it('re-reads the requested page with no arithmetic in either direction', async () => {
      arrive([moduleRow()], 40);

      const next = queryAll<HTMLButtonElement>('button.pagination__button').find(
        (button) => button.getAttribute('aria-label') === 'Next page',
      );

      expect(next).withContext('the next-page control is offered').not.toBeUndefined();

      (next as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();

      const call = expectList('the second page');

      // ⚠ NO `+ 1` AND NO `- 1`, ANYWHERE. The wire coordinate is zero-based, the pager's input IS that
      // index and its event emits that index back. Any adjustment on either side would serve the
      // NEIGHBOURING page behind a perfectly successful response, which no status code would reveal.
      expect(sentFilters(call).get('pageIndex')).toBe('1');

      call.flush(pageOf([moduleRow()], 40, 1));
      fixture.detectChanges();

      expect((query('.pagination__position')?.textContent ?? '').trim()).toBe('2 / 4');
    });

    it('returns to the first page from the last, again without adjustment', async () => {
      // ⚠ THE LATER PAGE IS REACHED THROUGH THE ADDRESS, not by handing the store a page in a response.
      await enterAt('/modules?currentpage=4');
      arrive([moduleRow()], 40, 3);

      const first = queryAll<HTMLButtonElement>('button.pagination__button').find(
        (button) => button.getAttribute('aria-label') === 'First page',
      );

      expect(first).withContext('the first-page control is offered').not.toBeUndefined();

      (first as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();

      const call = expectList('the first page');

      expect(sentFilters(call).get('pageIndex')).toBe('0');

      call.flush(pageOf([moduleRow()], 40, 0));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — REMOVAL
  // ---------------------------------------------------------------------------------------------------

  describe('removing a placement', () => {
    it('opens the confirmation carrying the measured legacy wording', () => {
      arrive();

      expect(query('.confirm-dialog')).withContext('closed until asked').toBeNull();

      requestRemoval();

      expect(query('.confirm-dialog')).withContext('open once asked').not.toBeNull();
      expect((query('.confirm-dialog__title')?.textContent ?? '').trim()).toBe(REMOVE_CONFIRM_TITLE);

      // ⚠ ASSERTED WITH ITS SPACE BEFORE THE QUESTION MARK. The wording constant lives on the component
      // precisely so the significant spacing survives the template compiler's whitespace collapsing, and
      // this is the assertion that would catch it being tidied.
      // ⚠ THE QUESTION IS ASSERTED AS A PREFIX AND THE RECORD BY NAME, WHICH IS STRONGER THAN THE EQUALITY
      // THIS REPLACES. The body used to be the bare legacy sentence and named nothing - searched against
      // every identifier on the page it matched none of them - while the dialog is a real modal that covers
      // the grid, including the row being destroyed. Keeping the sentence as a PREFIX is what still proves
      // the measured wording survives verbatim; asserting the name is what proves the operator can tell
      // which record is at risk without seeing the row.
      const body: string = (query('.confirm-dialog__message')?.textContent ?? '').trim();

      expect(body.startsWith(REMOVE_CONFIRM_MESSAGE))
        .withContext(`the measured question, spacing included, at the front of: ${body}`)
        .toBeTrue();
      expect(body).toContain('Announcements');

      // A destructive action, and the dialogue is told so, which is what marks its confirming button.
      expect(query('.confirm-dialog__button--danger'))
        .withContext('the confirming button is marked destructive')
        .not.toBeNull();

      const spoken: string = (query('.confirm-dialog__message')?.textContent ?? '').toLowerCase();

      expect(spoken).withContext('a soft delete is not permanent').not.toContain('permanently');
      expect(spoken).withContext('and it is not permanent either').not.toContain('permanent');
      expect(spoken).not.toContain('cannot be undone');
      expect(spoken).not.toContain('irreversible');
      expect(spoken).not.toContain('you will not be able to recover');

      // Nothing has been sent yet: the dialogue IS the guard.
      httpMock.expectNone(() => true);
      expect(notifications()).toHaveSize(0);
    });

    // ⚠ THE COMMAND IS PLACEMENT-SCOPED WHILE THE LEGACY SENTENCE SAYS "MODULE" - QA-10. The two cases have
    // genuinely different consequences, so the dialogue states which one the operator is in. This matters
    // because of the placement-delete rule this migration corrected: removing the FINAL placement no longer
    // strands a live module, it recycles it.
    it('says the removal reaches this page only when the module is on every page', () => {
      arrive([moduleRow({ allTabs: true })], 1);

      requestRemoval();

      const body: string = (query('.confirm-dialog__message')?.textContent ?? '').trim();

      expect(body)
        .withContext('the legacy question still opens it')
        .toContain(REMOVE_CONFIRM_MESSAGE);
      expect(body)
        .withContext('and the scope is stated: this page only')
        .toContain('removes it from this page only');
      expect(body)
        .withContext('and the other placements are said to survive')
        .toContain('left as they are');
      expect(body)
        .withContext('an every-page module is NOT described as losing its last placement')
        .not.toContain('no placement');
    });

    it('says the module is recycled when this is its only placement', () => {
      arrive([moduleRow({ allTabs: false })], 1);

      requestRemoval();

      const body: string = (query('.confirm-dialog__message')?.textContent ?? '').trim();

      expect(body)
        .withContext('the legacy question still opens it')
        .toContain(REMOVE_CONFIRM_MESSAGE);
      expect(body)
        .withContext('and the consequence of losing the last placement is stated')
        .toContain('leaves the module with no placement');
      expect(body)
        .withContext('and that the module goes with it')
        .toContain('recycled');
      expect(body)
        .withContext('a single-placement module is NOT described as surviving elsewhere')
        .not.toContain('this page only');
    });

    it('addresses the placement with both identities and re-reads once it succeeds', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      const call = expectRequest('DELETE', moduleUrl(4), 'the removal');

      // ⚠ BOTH IDENTITIES TRAVEL, and that is the whole contract. A module placed on every page has one
      // placement per page, so the module identity alone does not name a single row.
      expect(sentFilters(call).get('tabModuleId')).toBe('11');

      // The dialogue is dismissed the moment the command is issued, so the screen is not left holding
      // a modal over an in-flight request.
      expect(query('.confirm-dialog')).withContext('dismissed immediately').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const reread = expectList('the mandatory re-read');

      reread.flush(pageOf([moduleRow({ moduleId: 4, tabModuleId: 12 })]));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'success', message: REMOVE_SUCCESS_MESSAGE }]);
      // The message speaks of the PLACEMENT rather than the module, because after a `204` the module
      // itself may well still exist.
      expect(bodyRows()).withContext('the surviving placement is still listed').toHaveSize(1);
    });

    it('offers no way back, because no endpoint reverses a removal', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      expectRequest('DELETE', moduleUrl(4)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const forbidden: readonly string[] = ['restore', 'recycle-bin', 'recyclebin', 'undelete', 'purge'];

      forbidden.forEach((segment) => {
        httpMock.expectNone(
          (candidate) => candidate.url.includes(segment),
          `no request to any "${segment}" address`,
        );
      });

      // Nor is any such control painted, which is the same claim made against the DOM.
      const rendered: string = host().innerHTML.toLowerCase();

      forbidden.forEach((segment) => {
        expect(rendered).withContext(`no "${segment}" affordance is offered`).not.toContain(segment);
      });

      // Only the mandatory re-read is outstanding, and it is a plain listing read — not a reversal.
      answerList([moduleRow({ moduleId: 4, tabModuleId: 12 })]);
      httpMock.expectNone(() => true);
    });

    it('removes a placement whose identifiers are both zero', () => {
      arrive([moduleRow({ moduleId: 0, tabModuleId: 0 })]);

      requestRemoval();
      pressDialogue('Delete');

      const call = expectRequest('DELETE', moduleUrl(0), 'the removal of module zero');

      // Both seeds are legitimate values, and a truthiness test on either would send a request that
      // addressed a different placement or none at all.
      expect(call.request.url).toBe('/api/v1/modules/0');
      expect(sentFilters(call).get('tabModuleId')).toBe('0');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerList([]);

      expect(notifications()).toEqual([{ severity: 'success', message: REMOVE_SUCCESS_MESSAGE }]);
    });

    it('announces nothing while the removal is still outstanding, and holds no modal over it', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      const call = expectRequest('DELETE', moduleUrl(4), 'the outstanding removal');

      // The outcome bridge is gated on the store's SAVING flag, so nothing is announced while the command
      // is in flight - an operator is not told a placement was removed before it was. And the dialogue is
      // already dismissed, so the screen is not holding a modal over an open request.
      expect(notifications()).withContext('nothing announced yet').toHaveSize(0);
      expect(query('.confirm-dialog')).withContext('no modal over the request').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'success', message: REMOVE_SUCCESS_MESSAGE }]);

      // Meanwhile the re-read is outstanding, and the wait for it is reported by the shared table - both
      // programmatically, through its busy state, and visibly, through its own refetch strip - and NOT by
      // replacing the rows the operator is looking at, nor by an indicator of this screen's own.
      expect(query('table.data-table')?.getAttribute('aria-busy'))
        .withContext('the re-read is reported as a busy region')
        .toBe('true');
      expect(query('td.data-table__message[data-placeholder]'))
        .withContext('and the rows are not torn down to say so')
        .toBeNull();
      expect(query('.data-table__refetch app-loading-spinner'))
        .withContext('the shared table reports the re-read visibly as well as programmatically')
        .not.toBeNull();
      expect(
        queryAll<HTMLElement>('app-loading-spinner').filter(
          (indicator) =>
            indicator.closest('td.data-table__message') === null &&
            indicator.closest('.data-table__refetch') === null,
        ),
      )
        .withContext('no indicator of this screen\u2019s own, outside the shared table\u2019s two')
        .toHaveSize(0);

      answerList([moduleRow({ moduleId: 4, tabModuleId: 12 })]);
    });

    it('requires a fresh confirmation for every removal rather than disabling the affordance', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      const first = expectRequest('DELETE', moduleUrl(4), 'the first removal');

      requestRemoval();

      expect(query('.confirm-dialog')).withContext('the question is asked again').not.toBeNull();
      httpMock.expectNone(
        (candidate) => candidate.method === 'DELETE' && candidate.url === moduleUrl(4),
      );
      expect(notifications()).withContext('and nothing is announced').toHaveSize(0);

      pressDialogue('Cancel');

      first.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerList([]);

      expect(notifications()).toEqual([{ severity: 'success', message: REMOVE_SUCCESS_MESSAGE }]);
    });

    it('sends nothing when the confirmation is dismissed', () => {
      arrive();

      requestRemoval();
      pressDialogue('Cancel');

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      httpMock.expectNone(() => true);
      // Nothing happened, so nothing is announced. A cancelled action that reported itself would be
      // indistinguishable from one that ran.
      expect(notifications()).toHaveSize(0);
    });

    it('reports a refusal of authority as a WARNING, not as an error', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      expectRequest('DELETE', moduleUrl(4)).flush(
        problem(
          'module.edit_forbidden',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // ⚠ AND EXACTLY ONE MESSAGE IS RAISED, WHICH IS A CLAIM ABOUT OWNERSHIP. `ModuleStore` injects only
      // the two transports — it records failures structurally and notifies NOBODY — so the reporting duty
      // falls to this component alone and there is no double-notification hazard to guard against.
      expect(notifications()).toEqual([
        {
          severity: 'warning',
          message: 'The authenticated caller is not permitted to perform this operation.',
        },
      ]);
      expect(notifications())
        .withContext('one event, one message — the store does not report as well')
        .toHaveSize(1);

      // A failure means NO re-read: the listing is unchanged, so re-reading it would be a request with
      // nothing to learn.
      httpMock.expectNone(() => true);

      // ⚠ THE REFERENCE IS THE CORRELATION IDENTIFIER, NOT THE TRACE IDENTIFIER, AND THE PRECEDENCE IS THE
      // SHARED UTILITY'S: `form-errors.util.ts:L474-L486` returns the correlation identifier when one is
      // present and falls back to the trace identifier only when it is not.
      expect(query('.error-banner__title')).not.toBeNull();
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    it('reports a placement that no longer exists at 404, also as a warning', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      expectRequest('DELETE', moduleUrl(4)).flush(
        problem('module.placement_not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([
        { severity: 'warning', message: 'The requested resource does not exist.' },
      ]);
    });

    it('reports a server fault at 500 at error severity', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      expectRequest('DELETE', moduleUrl(4)).flush(
        problem(
          'server.unexpected_failure',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([
        {
          severity: 'error',
          message: 'An unexpected error occurred while processing the request.',
        },
      ]);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE FAILURE SURFACE
  // ---------------------------------------------------------------------------------------------------

  describe('the failure surface', () => {
    it('keeps the announcing region mounted even with nothing to announce', () => {
      arrive();

      // The region is mounted unconditionally and guards its own CONTENT, because a region created in
      // the same instant as its content is the case assistive technology frequently fails to announce.
      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
      expect(control('button', DISMISS_LABEL))
        .withContext('and offers no dismissal')
        .toBeUndefined();
    });

    it('shows a failed listing read in full, with no transient message for the same event', () => {
      create();

      expectList().flush(problem('portal.tenant_unresolved', 403, 'No portal could be resolved.'), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).toContain('No portal could be resolved.');
      expect(notifications()).toHaveSize(0);
    });

    it('summarises a per-field refusal without ever announcing it twice', () => {
      create();

      // ⚠ THE PER-FIELD MAP IS READ WITH AN INDEX EXPRESSION, NEVER WITH A PROPERTY ACCESS. Its keys are
      // .NET model-state keys — PascalCase, NOT camel-cased on the way out — and the member is declared as
      // an index signature, which `noPropertyAccessFromIndexSignature` makes a compile error to reach with
      // a dot.
      const refusal: ProblemDetails = {
        type: `${FAILURE_TYPE_PREFIX}module.request_invalid`,
        title: STATUS_TITLE[400] ?? 'Error',
        status: 400,
        detail: 'One or more validation errors occurred.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
        errors: {
          ModuleTitle: ['The module title is too long.'],
          PageIndex: ['The page index must not be negative.'],
        },
      };

      expect(refusal.errors?.['ModuleTitle']).toEqual(['The module title is too long.']);
      expect(refusal.errors?.['PageIndex']).toHaveSize(1);

      expectList().flush(refusal, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      // The banner owns the whole document — its wording, its per-field summary and its reference — so
      // the screen renders the refusal in one place and adds nothing to it.
      expect(textOf('.error-banner__message').join(' ')).toContain(
        'One or more validation errors occurred.',
      );
      expect(host().textContent ?? '')
        .withContext('the per-field wording reaches the reader')
        .toContain('The module title is too long.');

      expect(notifications()).withContext('the banner is the only report').toHaveSize(0);
    });

    it('dismisses the banner without sending anything', () => {
      create();

      expectList().flush(problem('request.invalid', 400, 'The paging arguments are invalid.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      requiredControl<HTMLButtonElement>('button', DISMISS_LABEL).click();
      fixture.detectChanges();

      expect(query('.error-banner__title')).withContext('cleared').toBeNull();
      // A banner a person cannot dismiss outlives its cause; dismissing it must not re-read anything.
      httpMock.expectNone(() => true);
    });

    it('re-issues the failed read when the recovery affordance is pressed', () => {
      create();

      expectList().flush(problem('request.invalid', 400, 'The paging arguments are invalid.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      expect(query('.error-banner__title')).withContext('the failure is reported').not.toBeNull();

      requiredControl<HTMLButtonElement>('button', RETRY_LABEL).click();
      fixture.detectChanges();

      // The report is cleared and the SAME request is issued again, so a second failure reads as a
      // fresh report rather than as the one still on screen.
      expect(query('.error-banner__title')).withContext('cleared before the retry').toBeNull();
      answerList([]);
      fixture.detectChanges();

      expect(query('.error-banner__title')).withContext('and the retry succeeded').toBeNull();
    });

    it('leaves no outcome waiting when a refusal is dismissed rather than re-tried', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      expectRequest('DELETE', moduleUrl(4)).flush(
        problem('module.edit_forbidden', 403, 'Not permitted.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const announced: number = notifications().length;

      requiredControl<HTMLButtonElement>('button', DISMISS_LABEL).click();
      fixture.detectChanges();

      // Clearing the failure also clears the awaited removal, so the reporting effect cannot be left
      // waiting on an outcome that will never arrive — and cannot announce the same refusal twice.
      expect(notifications()).toHaveSize(announced);
      expect(query('.error-banner__title')).toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — KEYBOARD AND ACCESSIBILITY
  // ---------------------------------------------------------------------------------------------------

  describe('operability', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      arrive();

      // `header`, `main`, `nav` and `footer` belong to the layout shell, which emits each exactly once.
      // A second one here would compete with it.
      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).withContext('one screen heading').toHaveSize(1);
    });

    it('makes every command a natively operable element rather than a scripted one', () => {
      arrive();

      // Not one `div` with a click handler and not one anchor standing in for an action: links navigate,
      // buttons act, and both are reachable and activatable from the keyboard with no scripting.
      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;

      Array.from(row.querySelectorAll<HTMLAnchorElement>('a')).forEach((link) => {
        expect(link.getAttribute('href')).withContext('a real address').not.toBeNull();
      });
      expect(row.querySelectorAll('[onclick]')).toHaveSize(0);
    });

    it('confirms a removal from the keyboard alone', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      const remove = requiredControl<HTMLButtonElement>('button', REMOVE_COMMAND_LABEL);

      // A real button responds to Enter and Space natively; dispatching the click the keystroke
      // produces is what the browser does on either.
      remove.focus();
      remove.click();
      fixture.detectChanges();

      const dialogue = query<HTMLElement>('.confirm-dialog');

      expect(dialogue).not.toBeNull();
      // The dialogue announces itself as a modal alert, which is what stops a reader wandering out of
      // it while a destructive question is open.
      expect((dialogue as HTMLElement).getAttribute('role')).toBe('alertdialog');
      expect((dialogue as HTMLElement).getAttribute('aria-modal')).toBe('true');

      pressDialogue('Cancel');
      httpMock.expectNone(() => true);
    });
  });

  // PROOF 9 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER

  describe('the affordances that deliberately do not exist', () => {
    it('offers no reorder, pane, copy, bulk, cache or synchronise control', () => {
      arrive([
        moduleRow({ moduleId: 4, tabModuleId: 11, moduleOrder: 1 }),
        moduleRow({ moduleId: 5, tabModuleId: 12, moduleOrder: 2 }),
      ]);

      const rendered: string = host().innerHTML.toLowerCase();
      const absent: readonly string[] = [
        'draggable',
        'reorder',
        'move up',
        'move down',
        'move to',
        'pane picker',
        'panename',
        'copy',
        'duplicate',
        'synchron',
        'clear cache',
        'select all',
        'delete all',
      ];

      absent.forEach((affordance) => {
        expect(rendered)
          .withContext(`no "${affordance}" affordance is offered`)
          .not.toContain(affordance);
      });

      // No bulk selection, which is what a bulk removal would need first. Not one checkbox, not one
      // radio, and no row carries a selection state.
      expect(queryAll('input[type="checkbox"]')).withContext('no bulk selection').toHaveSize(0);
      expect(queryAll('input[type="radio"]')).toHaveSize(0);
      expect(queryAll('[draggable="true"]')).withContext('no drag-to-reorder').toHaveSize(0);

      // The position is rendered as text. The ONLY editable control on the screen is the filter, and the
      // only buttons inside a row are the removals — one per row.
      expect(queryAll('input')).withContext('one control, and it is the filter').toHaveSize(1);
      expect(query('input[type="search"]')).not.toBeNull();
      expect(queryAll('tr.data-table__row button'))
        .withContext('one removal per row and nothing else')
        .toHaveSize(2);
      expect(queryAll('select')).withContext('no pane or page picker').toHaveSize(0);

      // And pressing nothing has sent nothing beyond the arrival read.
      httpMock.expectNone(() => true);
    });

    it('publishes its listing state as read-only signals that a consumer cannot write', () => {
      arrive();

      const store: ModuleStore = TestBed.inject(ModuleStore);

      const rows: Signal<readonly ModuleListItem[]> = store.modules;
      const meta: Signal<ApiMeta> = store.meta;

      expect('set' in rows).withContext('the row projection cannot be replaced').toBeFalse();
      expect('update' in rows).withContext('nor mutated in place').toBeFalse();
      expect('set' in meta).withContext('the paging coordinates cannot be replaced').toBeFalse();
      expect('update' in meta).withContext('nor mutated in place').toBeFalse();

      // Still readable, which is the other half of the contract: closing writes must not close reads.
      expect(rows()).toHaveSize(1);
      expect(meta().pageIndex).withContext('the coordinate is the server own').toBe(0);
    });

    it('sets no request header of its own, because the correlation identifier is the interceptor own', () => {
      create();

      const call = expectList();

      // ⚠ THE CORRELATION IDENTIFIER IS ATTACHED BY THE SHARED INTERCEPTOR, WHICH IS NOT REGISTERED IN THIS
      // HARNESS — so its absence here is the expected reading and is precisely what proves the screen does
      // not write it.
      expect(call.request.headers.has('X-Correlation-Id'))
        .withContext('the feature does not write the correlation header')
        .toBeFalse();
      expect(call.request.headers.keys().filter((name) => name.toLowerCase() !== 'accept'))
        .withContext('and sets no other header of its own')
        .toHaveSize(0);

      call.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // SHARED HELPER USED BY THE PROJECTION CASES
  // ---------------------------------------------------------------------------------------------------

  /**
   * Replaces the painted page by re-reading through the real filter path. Used where a case needs a
   * second, differently shaped page without creating a second component: the ordering path is the
   * cheapest genuine trigger for a re-read, and using a genuine one keeps the case honest about how a
   * page is replaced.
   */
  async function answerAfterReplacement(items: readonly ModuleListItem[]): Promise<void> {
    const sort = queryAll<HTMLButtonElement>('button.data-table__sort')[0];

    expect(sort).withContext('a sortable column exists to trigger a re-read').not.toBeUndefined();

    (sort as HTMLButtonElement).click();
    fixture.detectChanges();
    await settleAddress();

    expectList('the replacement read').flush(pageOf(items));
    fixture.detectChanges();
  }
  // PROOF — THE ADDRESS CARRIES THE SEARCH, THE ORDERING AND THE PAGE

  // ⚠ #36 — A FILTERED LISTING MUST SAY SO. The term lived in the address and in the search box, and the box
  // is cleared on every return to the screen, so a filtered grid was indistinguishable from a short one: three
  // rows with nothing on the page saying the other 247 were withheld rather than absent.
  describe('the active filter, stated', () => {
    /** The disclosure sentence, or null when the screen shows none. */
    function disclosure(): string | null {
      const node = query<HTMLElement>('.module-list__filter-disclosure');

      return node === null ? null : (node.textContent ?? '').trim();
    }

    /** Types a term into the shared control and settles the navigation it starts. */
    async function search(term: string): Promise<void> {
      const control = query<HTMLInputElement>('app-search-input input');

      expect(control).withContext('the filter control is offered').not.toBeNull();

      (control as HTMLInputElement).value = term;
      (control as HTMLInputElement).dispatchEvent(new Event('input'));
      fixture.detectChanges();

      const form = query<HTMLFormElement>('app-search-input form');

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      await settleAddress();
    }

    it('says nothing while the listing is unfiltered', () => {
      arrive([moduleRow()], 40);

      expect(disclosure()).toBeNull();
    });

    it('names the term the rows on screen answer', async () => {
      arrive([moduleRow()], 40);
      await search('announce');
      expectList('the filtered read').flush(pageOf([moduleRow()], 1));
      fixture.detectChanges();

      // ⚠ THE WORDING CHANGED HERE, AND THE OLD WORDING WAS WRONG - QA-9. This previously read "module
      // title or name", copied from the account listing for consistency of phrasing. But
      // ModuleRepository.cs:356-360 filters on `placement.Module.ModuleTitle` ALONE - there is no name
      // term in the query at all - so the sentence promised a search the endpoint does not perform and
      // a reader whose module matched on name only was told it had been looked for. Matching a sibling
      // screen's phrasing does not outrank describing this screen's own behaviour accurately.
      expect(disclosure())
        .withContext('the wording names only what the endpoint actually matches on')
        .toBe('Filtered: module title contains \u201cannounce\u201d.');
      expect(query<HTMLElement>('.module-list__filter-disclosure')?.getAttribute('aria-live'))
        .withContext('and it is announced, not only painted')
        .toBe('polite');
    });

    it('reports a term of nothing but spaces as ignored, and does not send it', async () => {
      arrive([moduleRow()], 40);

      // A real filter first, so the whitespace entry has something to undo and the read it provokes can be
      // inspected. Entering spaces from an already-unfiltered listing changes the address not at all, which
      // is why the sequence starts here.
      await search('announce');
      expectList('the filtered read').flush(pageOf([moduleRow()], 1));
      fixture.detectChanges();

      await search('   ');

      const call = expectList('the read that returns to unfiltered');

      expect(sentFilters(call).get('searchtext'))
        .withContext('a term with nothing to match on is not transmitted')
        .toBeNull();

      call.flush(pageOf([moduleRow()], 40));
      fixture.detectChanges();

      // ⚠ THE EXPLANATION COMES FROM THE SHARED CONTROL, NOT FROM THIS SCREEN, AND THAT IS WHERE IT BELONGS.
      // This listing once composed a sentence of its own for an ignored entry. The shared search control now
      // recognises a term with nothing to match on, emits the EMPTY term - which is why no filter travels -
      // and states the reason itself in a polite region, so every listing that uses the control says the same
      // thing in the same words instead of each one wording it differently or not at all.
      expect(query<HTMLElement>('app-search-input .search-input__advisory')?.textContent?.trim())
        .withContext('the screen says the entry was ignored rather than looking filtered')
        .toBe('A search of only spaces matches every record, so no filter was applied.');
      expect(disclosure())
        .withContext('and it is not ALSO claimed as a filter in force')
        .toBeNull();
    });
  });

  describe('the address', () => {
    /** Presses a pager step by its accessible name and settles the navigation it starts. */
    async function pressStep(name: string): Promise<void> {
      const step = queryAll<HTMLButtonElement>('button.pagination__button').find(
        (candidate) => candidate.getAttribute('aria-label') === name,
      );

      expect(step).withContext(`the "${name}" step is offered`).not.toBeUndefined();

      (step as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
    }

    it('writes the ordering into the address, in the direction the grid reports', async () => {
      arrive([moduleRow()], 40);

      const heading = queryAll<HTMLButtonElement>('button.data-table__sort')[0];

      expect(heading).withContext('a sortable heading is offered').not.toBeUndefined();

      (heading as HTMLButtonElement).click();
      fixture.detectChanges();
      await settleAddress();
      expectList('the ordered read').flush(pageOf([moduleRow()], 40));
      fixture.detectChanges();

      expect(addressParams()['sortby']).withContext('a key the endpoint admits').toBeTruthy();
      expect(addressParams()['sortdir']).toBe('Ascending');
      expect(addressParams()['currentpage'])
        .withContext('re-ordering returns to the first page, which is written as absence')
        .toBeUndefined();
    });

    it('writes a page turn into the address, one-based', async () => {
      arrive([moduleRow()], 40);

      await pressStep('Next page');
      expectList('the second page').flush(pageOf([moduleRow()], 40, 1));
      fixture.detectChanges();

      expect(addressParams()['currentpage'])
        .withContext('the address is one-based even though the store and the wire are not')
        .toBe('2');
    });

    it('restores a whole view from the address on entry: search, ordering and page together', async () => {
      // ⚠ ONE READ, AT THE RIGHT COORDINATE. The store's search and ordering setters each return the
      // listing to the first page, so applying the three in the wrong order would discard the page the
      // address asked for. This is the case that catches that.
      await enterAt('/modules?filter=news&sortby=moduleTitle&sortdir=Descending&currentpage=3');
      create();

      const read: TestRequest = expectList('the restored read');

      expect(sentFilters(read).get('query')).toBe('news');
      expect(sentFilters(read).get('sortBy')).toBe('moduleTitle');
      expect(sentFilters(read).get('sortDir')).toBe('Descending');
      expect(sentFilters(read).get('pageIndex'))
        .withContext('the page survived both resets')
        .toBe('2');

      read.flush(pageOf([moduleRow()], 40, 2));
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('and nothing further, so the restore cost exactly one read')
        .toHaveSize(0);
      // The restored term is shown, so a filter in force is visible and clearable rather than invisible.
      expect((query<HTMLInputElement>('input[type="search"]') as HTMLInputElement).value).toBe('news');
    });

    it('starts clean on a fresh entry, even though the store outlives the route', async () => {
      // THE MEASURED DEFECT: a fresh sidebar arrival landed on the page and filter of a previous visit.
      await enterAt('/modules?filter=news&currentpage=3');
      arrive([moduleRow()], 40, 2);
      fixture.destroy();

      await enterAt('/modules');
      create();

      const read: TestRequest = expectList('the fresh read');

      expect(sentFilters(read).get('pageIndex'))
        .withContext('the bare address means the first page, whatever the store still held')
        .toBe('0');
      expect(sentFilters(read).has('query'))
        .withContext('and no filter, whatever the store still held')
        .toBeFalse();

      read.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });

    it('corrects an address that names no page, and reads only once it has', async () => {
      await enterAt('/modules?currentpage=abc');
      create();

      expect(httpMock.match(() => true))
        .withContext('the uncorrected address reads nothing')
        .toHaveSize(0);

      await settleAddress();

      expect(addressParams()['currentpage']).toBeUndefined();

      const read: TestRequest = expectList('the corrected read');

      expect(sentFilters(read).get('pageIndex')).toBe('0');
      read.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });

    it('drops a direction that has no column to apply it to', async () => {
      // Half an ordering is not an ordering. The endpoint has its own default, so a lone direction is
      // corrected away rather than forwarded and refused.
      await enterAt('/modules?sortdir=Descending');
      create();
      await settleAddress();

      expect(addressParams()['sortdir']).toBeUndefined();

      const read: TestRequest = expectList('the read with no ordering');

      expect(sentFilters(read).has('sortDir')).toBeFalse();
      read.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });

    it('refuses a column the endpoint does not order by', async () => {
      // Forwarding it would produce a field-level refusal naming the admitted set, so it is treated as
      // naming nothing at all.
      await enterAt('/modules?sortby=description&sortdir=Ascending');
      create();
      await settleAddress();

      expect(addressParams()['sortby']).toBeUndefined();
      expect(addressParams()['sortdir']).toBeUndefined();

      const read: TestRequest = expectList('the read with no ordering');

      expect(sentFilters(read).has('sortBy')).toBeFalse();
      read.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // THE EMPTY-TABLE FLASH
  // ---------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so the grid painted "Nothing to Display" over a listing it
  // had not yet asked about - reported as an empty-table flash on every post-save return to a listing.
  describe('an un-asked listing waits rather than claiming to be empty', () => {
    it('shows the waiting placeholder, and NO zero-result surface, while the address correction is in flight', async () => {
      // An unusable address takes the correction arm, which replaces the address and returns WITHOUT
      // reading. The replacement navigation happens a task later, so this is exactly the window in which
      // nothing is in flight and nothing is held.
      await enterAt('/modules?currentpage=abc');
      create();

      expect(httpMock.match(() => true))
        .withContext('the precondition: the uncorrected address reads nothing')
        .toHaveSize(0);

      expect(queryAll('td[data-placeholder] app-loading-spinner').length)
        .withContext('the listing has not been asked about, so the grid is waiting')
        .toBe(1);
      expect(queryAll('app-empty-state').length)
        .withContext('nothing may assert that this tenant has no modules before one has been read')
        .toBe(0);

      await settleAddress();
      answerList([moduleRow()]);
    });

    it('shows the zero-result surface once a read has genuinely answered with nothing', () => {
      arrive([]);

      expect(queryAll('td[data-placeholder] app-loading-spinner').length).toBe(0);
      expect(queryAll('app-empty-state').length)
        .withContext('a settled read that matched nothing IS the empty state')
        .toBe(1);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // CLEARING THE SEARCH
  // ---------------------------------------------------------------------------------------------------
  // Reported as a NO-OP: with a term in force, emptying the box and submitting left the address carrying
  // the term, the rows filtered and no request issued. Reproduced here through the control's own submit
  // path rather than by calling the handler, so a break anywhere between the button and the read is caught.
  describe('clearing the search', () => {
    /** Presses the search control's own submit button. */
    function pressSearch(): void {
      const button: HTMLButtonElement | null = query<HTMLButtonElement>('.search-input__submit');

      expect(button).withContext('the control offers a submit affordance').not.toBeNull();
      button?.click();
      fixture.detectChanges();
    }

    /** Types into the search control's own field. */
    function typeSearch(value: string): void {
      const field: HTMLInputElement | null = query<HTMLInputElement>('.search-input__field');

      expect(field).not.toBeNull();
      field!.value = value;
      field!.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    it('drops the term from the address and re-reads unfiltered', async () => {
      await enterAt('/modules?filter=Links');
      create();
      answerList([moduleRow()]);

      expect(addressParams()['filter']).withContext('the term is in force').toBe('Links');

      typeSearch('');
      pressSearch();
      await settleAddress();

      expect(addressParams()['filter'])
        .withContext('an emptied box withdraws the term from the address')
        .toBeUndefined();

      // ⚠ THE READ IS THE POINT. A cleared box that leaves the rows filtered is worse than one that does
      // nothing: the caption asserts a total the rows do not match.
      const reread: TestRequest = expectList('the unfiltered re-read');

      expect(reread.request.params.get('query'))
        .withContext('and the re-read carries no term at all')
        .toBeNull();

      reread.flush(pageOf([moduleRow()], 1, 0));
      fixture.detectChanges();
    });

    it('shows the term the address carries, so an emptied box is a deliberate act', async () => {
      // Without this the box reads blank while the grid is filtered, and "clearing" it is a no-op because
      // there was nothing in it to clear.
      await enterAt('/modules?filter=Links');
      create();
      answerList([moduleRow()]);

      expect(query<HTMLInputElement>('.search-input__field')?.value)
        .withContext('the box reflects the term in force')
        .toBe('Links');
    });

    it('offers a clear affordance beside a zero-result search, and it works', async () => {
      await enterAt('/modules?filter=nothingmatches');
      create();
      answerList([], 0);

      const clear: HTMLButtonElement | null = query<HTMLButtonElement>('.module-list__clear-search');

      expect(clear)
        .withContext('a search that matched nothing must offer a way back to everything')
        .not.toBeNull();

      clear?.click();
      await settleAddress();

      expect(addressParams()['filter']).toBeUndefined();

      const reread: TestRequest = expectList('the unfiltered re-read');

      expect(reread.request.params.get('query')).toBeNull();
      reread.flush(pageOf([moduleRow()], 1, 0));
      fixture.detectChanges();
    });

    /**
     * ⚠ THE OTHER HALF OF THE AFFORDANCE ABOVE, AND IT WAS MISSING. Runtime testing measured the gap: the
     * button dropped the term from the address and brought every row back, and then the box went on showing
     * `nothingmatches` - a populated search field disagreeing with both the address bar and the grid beneath
     * it, so the one recovery offered to an operator left the screen contradicting itself.
     *
     * The cause was the echo guard that protects live typing when the term came FROM the box. This button is
     * a SECOND affordance over the same term, so nothing in the box needs protecting and the guard has to be
     * answered by writing the box explicitly. The hand-clear path is asserted alongside it, because that is
     * the path the guard is genuinely for and it must not regress.
     */
    it('empties the box as well as the address, so the screen cannot contradict itself', async () => {
      await enterAt('/modules?filter=nothingmatches');
      create();
      answerList([], 0);

      expect(query<HTMLInputElement>('.search-input__field')?.value)
        .withContext('precondition: the box shows the term that matched nothing')
        .toBe('nothingmatches');

      query<HTMLButtonElement>('.module-list__clear-search')?.click();
      await settleAddress();

      expect(addressParams()['filter'])
        .withContext('the term leaves the address')
        .toBeUndefined();

      const reread: TestRequest = expectList('the unfiltered re-read');

      reread.flush(pageOf([moduleRow()], 1, 0));
      fixture.detectChanges();

      expect(query<HTMLInputElement>('.search-input__field')?.value)
        .withContext('AND it leaves the box, which is the half that was measured missing')
        .toBe('');
    });

    it('states the zero-result outcome once, not twice', async () => {
      await enterAt('/modules?filter=nothingmatches');
      create();
      answerList([], 0);

      const occurrences: number = (host().textContent ?? '').split('No records found').length - 1;

      expect(occurrences).withContext('the empty state is asserted exactly once').toBe(1);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // AN ADDRESS PAST THE END
  // ---------------------------------------------------------------------------------------------------
  describe('an address past the end of the results', () => {
    it('does not show a populated range beside the zero-result surface', async () => {
      // Reported: `21-30 of 30` rendered beside "No records found." - a caption describing records the
      // grid is not showing and cannot show.
      await enterAt('/modules?currentpage=99');
      create();
      await settleAddress();
      answerList([], 30, 98);

      // The WHOLE screen is examined rather than one region, because the range and the zero-result
      // sentence were rendered by two different components - the pager below the grid and the grid's own
      // empty row - and an assertion scoped to either would have missed the contradiction between them.
      const shown: string = (host().textContent ?? '').replace(/\s+/g, ' ');

      expect(shown)
        .withContext('no range describing records the grid is not showing and cannot show')
        .not.toContain('of 30');
      expect(shown.toLowerCase())
        .withContext('it says the page is past the end instead')
        .toContain('past the end');
    });

    it('offers the same return-to-first-page recovery the portal listing offers', async () => {
      await enterAt('/modules?currentpage=99');
      create();
      await settleAddress();
      answerList([], 30, 98);

      const recovery: HTMLButtonElement | null = query<HTMLButtonElement>('.module-list__first-page');

      expect(recovery).not.toBeNull();

      recovery?.click();
      await settleAddress();

      expect(addressParams()['currentpage']).toBeUndefined();

      const reread: TestRequest = expectList('the first-page re-read');

      reread.flush(pageOf([moduleRow()], 30, 0));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // AN ADMINISTRATIVE MODULE'S SETTINGS
  // ---------------------------------------------------------------------------------------------------
  // Reported: every row offered `Settings`, and on the row created from the administrative `User Accounts`
  // package the screen behind it answered 403 `module.settings_protected` every single time - "available only
  // through their typed privileged endpoint", a sentence about an endpoint shown to an operator, with no way
  // on. The refusal is deliberate; offering a command that provokes it is not.
  describe('the settings command for an administrative module', () => {
    /** Every row command's destination, in document order, with a button reported as having none. */
    function commandTargets(): readonly (string | null)[] {
      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;

      return Array.from(row.querySelectorAll<HTMLElement>('a, button')).map((control) =>
        control.getAttribute('href'),
      );
    }

    /** The names of this row's commands. */
    function names(): readonly string[] {
      const row: HTMLTableRowElement = bodyRows()[0] as HTMLTableRowElement;

      return Array.from(row.querySelectorAll<HTMLElement>('a, button')).map((control) =>
        (control.textContent ?? '').trim(),
      );
    }

    it('sends it to the screen that owns those settings, not to the generic one', () => {
      arrive([
        moduleRow({
          moduleId: 7,
          friendlyName: 'User Accounts',
          moduleTitle: 'User Accounts',
          isAdmin: true,
        }),
      ]);

      expect(names())
        .withContext('the command is still offered - it simply leads somewhere that works')
        .toContain('Settings');

      // The membership settings screen IS this module's settings, which is why the server refuses to serve
      // them generically. The address is the one the role listing already names for the same screen.
      expect(commandTargets())
        .withContext('the destination is the screen that genuinely owns them')
        .toContain('/settings/membership');
      expect(commandTargets())
        .withContext('and never the generic settings screen, which answers 403 for this module')
        .not.toContain('/modules/7/settings');
    });

    it('withholds it entirely for an administrative package this console does not administer', () => {
      arrive([
        moduleRow({
          moduleId: 5,
          friendlyName: 'Site Log',
          moduleTitle: 'Site Log',
          isAdmin: true,
        }),
      ]);

      expect(names())
        .withContext('no affordance is better than one that always ends in a refusal')
        .not.toContain('Settings');

      // The other three commands are untouched: the module can still be edited, exported and removed.
      expect(names()).toEqual(['Edit', 'Export', 'Delete']);
    });

    it('offers the generic screen for an ordinary module, and for one whose package could not be read', () => {
      arrive([moduleRow({ moduleId: 3, isAdmin: false })]);

      expect(commandTargets()).toContain('/modules/3/settings');

      // ⚠ `null` IS NOT `true`. An unresolved package makes no claim, so the ordinary destination stands and
      // the server remains the authority - treating absence as "administrative" would withhold a working
      // command from every module whose definition join failed.
      arrive([moduleRow({ moduleId: 3, isAdmin: null })]);

      expect(commandTargets())
        .withContext('an unresolved package is not a claim that the module is administrative')
        .toContain('/modules/3/settings');
    });
  });

});
