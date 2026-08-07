/**
 * Specification for {@link ModuleListComponent} — the module listing at `/modules`.
 *
 * ## WHAT THIS SPECIFICATION IS FOR
 *
 * The listing is the navigational entry point of the whole module feature: every other module screen
 * is reached from a row of it, and it is the only screen that removes a placement. Nothing else in the
 * workspace asserts any of that, so the behaviours proven here are proven nowhere else.
 *
 * ## HOW IT IS DRIVEN
 *
 * Through the REAL collaborators, over the REAL transport, against the REAL shared components:
 *
 *   - {@link ModuleStore} is genuine and listed in `providers`, so each case gets its own instance and
 *     no state leaks between cases. Its requests are answered through `HttpTestingController`, so every
 *     assertion about an address, a parameter or a status is an assertion about the wire.
 *   - The five shared components the screen composes are the genuine ones, so the DOM asserted here is
 *     the DOM an operator sees. That is what lets a case press a real sort button, a real pager button
 *     and a real confirmation, rather than calling a handler and hoping the template is wired to it.
 *   - `NotificationService.notify` is spied and CALLED THROUGH, so both the invocation and the queue it
 *     fills are observable.
 *   - No router is spied, because the screen injects none: every cross-screen movement it offers is a
 *     link, which is asserted as an address rather than as a navigation.
 *
 * ⚠ THE PAGED LISTING BODY IS `{ items, meta }`, NOT `{ data, meta }`. Every single-resource route
 * answers with a `data` member, but a collection's body IS the page envelope, whose records live under
 * `items` — `paged-result.model.ts:L493-L505` reads `response.items` and substitutes an EMPTY ARRAY
 * when it is absent. A fixture spelling it `data` flushes successfully and unwraps to no rows at all,
 * so every assertion afterwards would be made against an empty listing rather than against the screen.
 *
 * ⚠ A ROW IS A PLACEMENT, NOT A MODULE. One module whose all-pages flag is set contributes one row per
 * page of the site, each with its own `tabModuleId` while `moduleId` repeats. That single fact drives
 * the removal contract asserted below: BOTH identities travel, the confirmation speaks of a placement,
 * and a `204` is followed by a mandatory re-read because only the listing endpoint knows whether the
 * row survived the soft delete.
 *
 * ## WHAT IS BEING PRESERVED, AND WHAT IS NET-NEW
 *
 * Both halves are stated because a specification that blurs them invites a later reader to "restore"
 * behaviour that never existed, or to relax an assertion that is the only record of a measured legacy
 * fact. Each claim below is annotated again, with its legacy line, at the case that asserts it.
 *
 * NET-NEW, WITH NO LEGACY ANCESTOR AT ALL:
 *
 *   - THE SCREEN ITSELF. `grep -rio "<asp:DataGrid" Website/admin/Modules/` returns ZERO. That directory
 *     holds `export.ascx`, `import.ascx`, `modulesettings.ascx`, their three code-behinds, one icon and
 *     `App_LocalResources` — and nothing that lists modules. All nine `<table>` elements in
 *     `modulesettings.ascx` are `summary="… Design Table"` layout tables. So no legacy screen is being
 *     reproduced, and none is manufactured to pretend otherwise.
 *   - THE FREE-TEXT FILTER. `Library/Components/Modules/ModuleController.vb:L1032` `GetSearchModules` is
 *     the search-INDEXING surface, not a user-facing query.
 *   - EVERY ACCESSIBILITY AFFORDANCE. `Website/admin/Portal/portals.ascx` has no `<caption>`, no
 *     `summary`, no column scope, and its two command columns at `:L21` and `:L22` carry neither
 *     `HeaderText` nor alternative text.
 *   - AND THESE TESTS. THE LEGACY TREE CONTAINS ZERO AUTOMATED TESTS OF ANY KIND — not a unit test, not
 *     an integration test, not a fixture. There is no legacy suite to port, so every assertion in this
 *     file is authored against measured legacy SOURCE rather than against a legacy test.
 *
 * MEASURED AND PRESERVED EXACTLY:
 *
 *   - The confirmation wording, including its space before the question mark
 *     (`SharedResources.resx` → `DeleteModule.Confirm`).
 *   - The affirmative and negative words (`Yes.Text`, `No.Text`) and the edit and removal wording
 *     (`Edit.Text`, `cmdDelete.Text` — there is no `Delete.Text` key in that file).
 *   - The three visibility words and their implicit ordinals (`ModuleInfo.vb:L30-L34`).
 *   - A minimum-value date rendering blank (`ModuleSettings.ascx.vb:L152-L157`).
 *   - A refusal reported at warning severity (`AccessDenied.ascx.vb`).
 *
 * ANNOTATED AS A LEGACY DEFECT AND DELIBERATELY NOT FIXED: `ModuleController.vb:L829` documents the
 * removal as permanent while `:L850` — in the same routine — says `' soft delete the module`. The code
 * is authoritative and the comment is wrong; Minimal Change Clause item 1 forbids correcting it, so it
 * is recorded here and the assertions follow the CODE.
 */
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { ModuleStore } from '../../../core/state/module.store';
import { ModuleListComponent } from './module-list.component';

import type { Signal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ModuleListItem } from '../../../core/models/module.model';
import type { ApiMeta, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';

// =====================================================================================================
// NARROWING WITHOUT AN ESCAPE HATCH
//
// No escape hatch is used anywhere in this file: no wildcard type annotation, no compiler-directive
// comment and no non-null assertion. Where an assertion needs an element that must exist, it is
// unwrapped through the helper below, which FAILS BY NAME rather than letting `null` travel on into a
// property read that then reports itself as an unrelated type error somewhere else.
// =====================================================================================================

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

// =====================================================================================================
// ADDRESSES
//
// Hand-written relative literals. Building them from the endpoint registry would assert the registry
// against itself and could not detect a change to it.
// =====================================================================================================

/** The listing address. */
const MODULES_URL = '/api/v1/modules';

/** The per-placement address, which the removal addresses. */
function moduleUrl(moduleId: number): string {
  return `${MODULES_URL}/${moduleId}`;
}

/** The default page size the store asks for when nobody has changed it. */
const DEFAULT_PAGE_SIZE = '10';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported: the component exports none of these, and a specification reading them
// off the component could not detect a change to them.
// =====================================================================================================

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

/**
 * The removal confirmation.
 *
 * ⚠ THE SPACE BEFORE THE QUESTION MARK IS PART OF THE VALUE. `SharedResources.resx` declares
 * `DeleteModule.Confirm` as `'Are You Sure You Wish To Delete This Module ?'`, and the Minimal Change
 * Clause forbids tidying it: an equivalent message means the same message.
 */
const REMOVE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Module ?';

const REMOVE_SUCCESS_MESSAGE = 'The module placement was removed.';

const DISMISS_LABEL = 'Dismiss';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
//
// ⚠ EVERY DOCUMENT BELOW IS ONE THE API CAN ACTUALLY EMIT. The type is built by the server's own
// `BuildProblemType`, so it is lower-cased with hyphens folded to underscores; the title comes from the
// status vocabulary; the trace and correlation identifiers are attached to every document by the
// problem-details factory; and NO document carries an `instance` member, because every call site
// supplies null and the serialiser omits it.
// =====================================================================================================

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
 * One listing row.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so module zero is the
 * first module of an installation. Every default here is chosen so that a truthiness test anywhere in
 * the screen would be caught rather than accidentally satisfied.
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

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The
    // store is pinned to this injector so no case shares its instance with another.
    await TestBed.configureTestingModule({
      imports: [ModuleListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ModuleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

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
    return expectRequest('GET', MODULES_URL, description ?? 'the listing read');
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

    return Array.from((row as HTMLTableRowElement).querySelectorAll('td')).map((cell) =>
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

      // The address must never be absolute: the application is served from the same origin as the API
      // through the reverse proxy, and an absolute address would turn every read into a cross-origin
      // request that resolves only inside the container network.
      expect(call.request.url.startsWith('http')).withContext('relative address').toBeFalse();
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe(DEFAULT_PAGE_SIZE);

      // No filter and no ordering are sent on the first read: both are the server's to choose until an
      // operator states one.
      expect(call.request.params.has('query')).withContext('no filter').toBeFalse();
      expect(call.request.params.has('sortBy')).withContext('no ordering').toBeFalse();
      expect(call.request.params.has('sortDir')).withContext('no direction').toBeFalse();

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

      // The shared table renders both the wait and the empty state itself, in one spanning row, and
      // lets the wait win. This screen must therefore render NEITHER of its own, or a person would meet
      // two indicators for one wait.
      const message = query<HTMLElement>('td.data-table__message[data-placeholder]');

      expect(message).withContext('the table carries the wait').not.toBeNull();

      // ⚠ THE INDICATOR THE TABLE OWNS IS ITSELF AN `app-loading-spinner`, SO ITS MERE PRESENCE PROVES
      // NOTHING. What distinguishes the two is WHERE it sits: the table's own lives inside the spanning
      // message cell, so an indicator anywhere OUTSIDE that cell would be this screen's second one. That
      // is the assertion, and it is why the sibling component's imports list contains neither component.
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

      // ⚠ IF ANY LINK BUILDER OR ANY CELL TESTED THE IDENTIFIER FOR TRUTHINESS, THIS ROW WOULD BE THE
      // ONE IT BROKE. The identity column is seeded from zero, so zero addresses the first module of
      // an installation and must render and link exactly like any other.
      expect(cells).toContain('0');
      expect(textOf('tr.data-table__row a').length).withContext('links offered').toBeGreaterThan(0);
    });

    it('renders the all-pages flag as words, including the negative one', () => {
      arrive([moduleRow({ allTabs: false })]);

      // `false` is DATA here, not an absence: the legacy grids drew this flag as a pair of images with
      // no alternative text, so the state was drawn but never announced. It is words now, and the
      // negative word must appear rather than an empty cell.
      expect(cellsOf(0)).toContain('No');

      answerAfterReplacement([moduleRow({ allTabs: true })]);

      expect(cellsOf(0)).toContain('Yes');
    });

    it('renders each declared visibility code as its display word', () => {
      arrive([
        moduleRow({ moduleId: 0, visibility: ModuleVisibility.Maximized }),
        moduleRow({ moduleId: 1, tabModuleId: 8, visibility: ModuleVisibility.Minimized }),
        moduleRow({ moduleId: 2, tabModuleId: 9, visibility: ModuleVisibility.None }),
      ]);

      // MIGRATION: THE ORDINALS ARE IMPLICIT IN THE LEGACY SOURCE AND ARE THEREFORE LOAD-BEARING.
      //   `Library/Components/Modules/ModuleInfo.vb:L30-L34` is, verbatim,
      //   `Public Enum VisibilityState / Maximized / Minimized / None / End Enum` — with NO explicit
      //   values, so 0, 1 and 2 come from declaration order alone and every stored row depends on that
      //   order never changing. The enumeration was renamed `ModuleVisibility` on the way across and the
      //   numbers were not touched, which is what this case pins. `Website/App_GlobalResources/
      //   SharedResources.resx` independently declares `Maximized.Text` = 'Maximized' and
      //   `None.Text` = 'None', so the wording is measured rather than invented.
      expect(cellsOf(0)).toContain('Maximized');
      expect(cellsOf(1)).toContain('Minimized');

      // ⚠ ZERO IS A REAL STATE AND IS THE LEGACY DEFAULT, so the first row must carry a word rather
      // than an empty cell. A lookup written as `VISIBILITY_LABEL[code] || ''` would pass the two rows
      // below and fail this one, which is exactly why it is asserted as NON-EMPTY and not merely as
      // "contains".
      expect(cellsOf(0).filter((text) => text === 'Maximized'))
        .withContext('code 0 resolves to a real word, never to an empty cell')
        .toHaveSize(1);

      // ⚠ THE THIRD MEMBER IS A STATE, NOT AN ABSENCE. A placement set to it exists and is placed; it
      // simply draws no container chrome. Rendering it blank would tell an operator the value was
      // missing when the operator themselves chose it.
      expect(cellsOf(2)).toContain('None');
      expect(cellsOf(2).filter((text) => text === 'None'))
        .withContext('code 2 renders the real word "None", never an empty cell')
        .toHaveSize(1);
    });

    it('renders an absent date and the legacy marker date as empty cells alike', () => {
      arrive([moduleRow({ startDate: null, endDate: '0001-01-01T00:00:00Z' })]);

      const cells: readonly string[] = cellsOf(0);

      // Two different absences on the wire — a null and the legacy minimum-date marker — and one
      // rendering, because to a person they mean the same thing. The shared pipe owns that decision.
      expect(cells.filter((text) => text.length === 0).length)
        .withContext('both absences render empty')
        .toBeGreaterThanOrEqual(2);

      // MIGRATION: THE MARKER DATE RENDERS BLANK, WHICH IS EXACTLY WHAT THE LEGACY SCREEN DID.
      //   `Website/admin/Modules/ModuleSettings.ascx.vb:L152-L154` reads
      //   `If Not Null.IsNull(objModule.StartDate) Then txtStartDate.Text =
      //   objModule.StartDate.ToShortDateString / End If`, and `:L155-L157` is the identical pair for
      //   the end date — the field was LEFT BLANK on the sentinel rather than showing a minimum date.
      //   `Library/Components/Shared/Null.vb:L66-L68` defines that sentinel as `Date.MinValue`, and it
      //   still travels on the wire because the API serialises it as a real ISO-8601 string rather than
      //   omitting the member. So `01/01/0001` must appear NOWHERE on the row, in any locale spelling.
      const rowText: string = cells.join(' ');

      expect(rowText).withContext('the marker date is never painted').not.toContain('0001');
      expect(rowText).not.toContain('01/01/0001');
      expect(rowText).not.toContain('1/1/0001');
    });

    it('paints a far-future date as the real value it is, rather than blanking it too', () => {
      // ⚠ THE COMPANION TO THE CASE ABOVE, AND THE ONE THAT STOPS THE BLANKING FROM OVER-REACHING.
      // A perpetual expiry is ordinary data: the shared pipe tests for `0001-01-01` specifically and
      // never for an upper bound, so a year-9999 value must survive to the cell. Without this case, a
      // pipe that blanked "implausible" dates in general would satisfy the sentinel case and silently
      // erase every perpetual schedule in the tenant.
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

    it('names a command of an untitled placement without the word null', () => {
      arrive([moduleRow({ moduleTitle: null })]);

      const labels: readonly (string | null)[] = Array.from(
        (bodyRows()[0] as HTMLTableRowElement).querySelectorAll('a'),
      ).map((link) => link.getAttribute('aria-label'));

      // An absent title is a legitimate state on this contract, so the accessible name degrades to the
      // command alone rather than to the text 'null'.
      expect(labels).toContain('Edit ');
      labels.forEach((label) => {
        expect(label ?? '').withContext('no absence marker leaks into a name').not.toContain('null');
      });
    });

    it('escapes a hostile title rather than parsing it into elements', () => {
      arrive([moduleRow({ moduleTitle: '<img src=x onerror="window.__listed=true">' })]);

      // Legacy resource and content values are untrusted markup, so every string on this screen is
      // interpolated. What matters is the element tree, not the text of the serialised markup: the
      // brackets survive as characters, and no element is constructed from them.
      expect(queryAll('img')).withContext('no element parsed out of a title').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__listed'])
        .withContext('the title was never evaluated')
        .toBeUndefined();
      expect(host().innerHTML).toContain('&lt;img');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — FILTERING
  // ---------------------------------------------------------------------------------------------------

  describe('the free-text filter', () => {
    it('re-reads with the typed text once the shared control emits', fakeAsync(() => {
      create();
      answerList();

      const field = query<HTMLInputElement>('input[type="search"]') as HTMLInputElement;

      field.value = 'news';
      field.dispatchEvent(new Event('input'));

      // The shared control debounces its own emission, so the read does not exist until the delay has
      // elapsed. Driving it through the real control is what proves the template is wired to the
      // handler at all — a case calling the handler directly could pass with no binding present.
      tick(300);
      fixture.detectChanges();

      const call = expectList('the filtered read');

      // MIGRATION: THE FREE-TEXT FILTER IS NET-NEW; THE LEGACY HAD NO MODULE SEARCH OF ANY KIND.
      //   `Library/Components/Modules/ModuleController.vb:L1032` declares
      //   `Public Function GetSearchModules(ByVal PortalId As Integer) As ArrayList`, which is the
      //   search-INDEXING surface of the legacy searchable-module contract — it returns the modules that
      //   SUPPORT search so an indexer can walk them — and is emphatically not a user-facing query.
      //   Nothing in `Website/admin/Modules/` filtered a module list by text, because there was no
      //   module list to filter: `grep -rio "<asp:DataGrid" Website/admin/Modules/` returns ZERO. So
      //   this parameter has no legacy contract to preserve and the only thing to pin is that the screen
      //   adds nothing to what a person typed.
      //
      // Recorded EXACTLY AS EMITTED: no wildcard appended, no pattern syntax introduced, no escaping
      // applied. Match semantics belong to the server, which is where the legacy call-site pattern
      // decoration moved to.
      expect(call.request.params.get('query')).toBe('news');

      // ⚠ NO PER-CENT SIGN, IN EITHER POSITION. Server-side matching is `LIKE 'text%'` — STARTS-WITH,
      // with the wildcard appended SERVER-SIDE — so a screen that appended one would ask for
      // `LIKE 'news%%'`, and one that prepended it would silently convert a starts-with filter into a
      // contains filter. Both would be a behaviour change no status code reveals. The parameter is
      // asserted against the raw text and separately against the character itself, because an equality
      // assertion alone would not say WHY the value must be bare.
      const sent: string = call.request.params.get('query') ?? '';

      expect(sent).withContext('no wildcard is added by the client').not.toContain('%');
      expect(sent.length).withContext('the text travels byte-for-byte').toBe('news'.length);
      // The whole serialised query string is checked too, because a wildcard added by the parameter
      // builder rather than by the screen would still reach the server.
      expect(call.request.urlWithParams).not.toContain('%25');

      // A changed filter returns to the first page: a coordinate measured against one match set does
      // not address the same rows once the set changes.
      expect(call.request.params.get('pageIndex')).toBe('0');

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

      // ⚠ THE EMPTY STRING IS THE LEGACY ABSENT-STRING MARKER — `Null.vb:L71-L75` returns `""` — so the
      // two were indistinguishable in the legacy and are deliberately held apart here. `null` means
      // "no filter" and is therefore OMITTED from the request; the empty string would be a filter for
      // nothing, and sending it would ask the server a different question.
      expect(cleared.request.params.has('query')).withContext('the parameter is omitted').toBeFalse();

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

    it('re-reads ordered by the pressed column, from the first page', () => {
      arrive([moduleRow()], 40);

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();

      const call = expectList('the ordered read');

      expect(call.request.params.get('sortBy')).withContext('a permitted key').not.toBeNull();
      // The direction arrives in the server's own spelling and needs no translation.
      expect(call.request.params.get('sortDir')).toBe('Ascending');
      expect(call.request.params.get('pageIndex')).toBe('0');

      call.flush(pageOf([moduleRow()], 40));
      fixture.detectChanges();
    });

    it('reverses the direction on a second press of the same column', () => {
      arrive();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();

      const reversed = expectList('the reversed read');

      expect(reversed.request.params.get('sortDir')).toBe('Descending');

      reversed.flush(pageOf([moduleRow()]));
      fixture.detectChanges();
    });

    it('reflects the held ordering back into the grid own sort state', () => {
      arrive();

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // The state a reader perceives is the column heading's own, which the shared table derives from
      // the two inputs this screen supplies from the store. Asserting the attribute proves the round
      // trip rather than just the request.
      const sorted: readonly string[] = queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');

      expect(sorted).withContext('exactly one column reports itself sorted').toEqual(['ascending']);
    });

    it('announces the ordering on the ACTIVE column alone, and on no other', () => {
      arrive();

      const headers: readonly HTMLElement[] = queryAll<HTMLElement>('th.data-table__header');
      const announced = (): readonly (string | null)[] =>
        queryAll<HTMLElement>('th.data-table__header').map((cell) => cell.getAttribute('aria-sort'));

      // ⚠ BEFORE ANY COLUMN IS PRESSED, NOTHING CLAIMS TO BE SORTED. A grid in which every sortable
      // heading reported a direction would convey nothing at all, so the shared table distinguishes
      // three states and this case pins all three at once:
      //   * a NON-SORTABLE heading carries NO `aria-sort` attribute — six of the ten columns;
      //   * a sortable heading that is not the active one carries `none` — four columns here;
      //   * only the active one carries a direction.
      expect(announced().filter((value) => value === 'none'))
        .withContext('the four sortable columns each report themselves unsorted')
        .toHaveSize(4);
      expect(announced().filter((value) => value === null))
        .withContext('the six non-sortable columns carry no ordering state at all')
        .toHaveSize(6);
      expect(announced().filter((value) => value === 'ascending' || value === 'descending'))
        .withContext('and nothing claims a direction before one is chosen')
        .toHaveSize(0);

      (sortButtons()[0] as HTMLButtonElement).click();
      fixture.detectChanges();
      expectList().flush(pageOf([moduleRow()]));
      fixture.detectChanges();

      // ⚠ `sortBy` AND `sortDir` ARE THE SOLE SOURCE OF TRUTH FOR THIS ATTRIBUTE. They are supplied
      // from the store, so the heading that announces itself sorted is the one the NEXT request would
      // order by — the announcement and the wire cannot disagree.
      expect(announced().filter((value) => value === 'ascending')).toHaveSize(1);
      expect(announced().filter((value) => value === 'descending')).toHaveSize(0);
      expect(announced().filter((value) => value === 'none'))
        .withContext('the other three sortable columns fall back to unsorted')
        .toHaveSize(3);
      expect(headers).withContext('the column count is unchanged by ordering').toHaveSize(10);
    });

    it('scopes every heading to its column and names the grid with a real caption', () => {
      arrive();

      // MIGRATION: THE GRID'S ACCESSIBILITY AFFORDANCES ARE NET-NEW ADDITIONS MADE AT ZERO VISUAL COST,
      //   AND THIS IS THE CASE THAT PROVES THEY EXIST. Neither measured legacy grid had any of them.
      //   `Website/admin/Portal/portals.ascx` declares its grid and its columns with NO `<caption>`, NO
      //   `summary` and no column-scope information whatsoever — `grep -ciE "<caption|scope=|summary="`
      //   over that file returns ZERO — and its two command columns at `:L21` and `:L22` are
      //   `dnn:imagecommandcolumn` entries carrying an `ImageUrl` and neither `HeaderText` nor
      //   alternative text, so both reached assistive technology as unnamed graphics.
      //   `Website/admin/Users/users.ascx:L35-L39` is worse: an entirely header-less template column
      //   whose only content is an icon with no alternative text. None of what is asserted below has a
      //   legacy ancestor, and none of it changes a painted pixel.
      const headers: readonly HTMLElement[] = queryAll<HTMLElement>('th');

      expect(headers).withContext('ten columns, ten headings').toHaveSize(10);

      // EVERY heading, without exception — including the header-less commands column, which is hidden
      // from sight but stays named in the accessibility tree.
      headers.forEach((heading, index) => {
        expect(heading.getAttribute('scope'))
          .withContext(`heading ${index} scopes itself to its column`)
          .toBe('col');
      });

      // The caption must resolve to a REAL TEXT NODE rather than to an empty element: the shared table
      // renders the element unconditionally and falls back to its own placeholder wording when nothing
      // is projected, so an empty caption would leave the grid with a misleading accessible name rather
      // than with none.
      const caption: Element = requireElement(host(), 'caption');

      expect((caption.textContent ?? '').trim())
        .withContext('the caption carries this screen own wording')
        .toBe(TABLE_CAPTION);
      expect((caption.textContent ?? '').trim().length)
        .withContext('and it is a real text node, not an empty element')
        .toBeGreaterThan(0);

      // Every row command is an icon-free text control, and each carries a non-empty accessible name.
      // The global register measured for the two that have one is `Website/App_GlobalResources/
      // SharedResources.resx`: `Edit.Text` = 'Edit' and `cmdDelete.Text` = 'Delete'. There is NO
      // `Delete.Text` key in that file, which is why the removal wording is taken from `cmdDelete`.
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
    it('draws no pager when the whole match set fits on one page', () => {
      arrive([moduleRow()], 1);

      // The shared pager decides for itself whether it has anything to offer, and this screen wraps it
      // in no condition of its own — a wrapper would be a second opinion about the same question.
      expect(query('.pagination')).toBeNull();
    });

    it('draws the pager once more records exist than fit on the page', () => {
      arrive([moduleRow()], 40);

      expect(query('.pagination')).not.toBeNull();
      // The counter a person reads is one-based and is rendered INSIDE the pager. It never leaves it.
      expect((query('.pagination__position')?.textContent ?? '').trim()).toBe('1 / 4');
    });

    it('re-reads the requested page with no arithmetic in either direction', () => {
      arrive([moduleRow()], 40);

      const next = queryAll<HTMLButtonElement>('button.pagination__button').find(
        (button) => button.getAttribute('aria-label') === 'Next page',
      );

      expect(next).withContext('the next-page control is offered').not.toBeUndefined();

      (next as HTMLButtonElement).click();
      fixture.detectChanges();

      const call = expectList('the second page');

      // ⚠ NO `+ 1` AND NO `- 1`, ANYWHERE. The wire coordinate is zero-based, the pager's input IS
      // that index and its event emits that index back. Any adjustment on either side would serve the
      // NEIGHBOURING page behind a perfectly successful response, which no status code would reveal.
      expect(call.request.params.get('pageIndex')).toBe('1');

      call.flush(pageOf([moduleRow()], 40, 1));
      fixture.detectChanges();

      expect((query('.pagination__position')?.textContent ?? '').trim()).toBe('2 / 4');
    });

    it('returns to the first page from the last, again without adjustment', () => {
      arrive([moduleRow()], 40, 3);

      const first = queryAll<HTMLButtonElement>('button.pagination__button').find(
        (button) => button.getAttribute('aria-label') === 'First page',
      );

      expect(first).withContext('the first-page control is offered').not.toBeUndefined();

      (first as HTMLButtonElement).click();
      fixture.detectChanges();

      const call = expectList('the first page');

      expect(call.request.params.get('pageIndex')).toBe('0');

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

      // ⚠ ASSERTED WITH ITS SPACE BEFORE THE QUESTION MARK. The wording constant lives on the
      // component precisely so the significant spacing survives the template compiler's whitespace
      // collapsing, and this is the assertion that would catch it being tidied.
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim()).toBe(
        REMOVE_CONFIRM_MESSAGE,
      );

      // A destructive action, and the dialogue is told so, which is what marks its confirming button.
      expect(query('.confirm-dialog__button--danger'))
        .withContext('the confirming button is marked destructive')
        .not.toBeNull();

      // MIGRATION: THE REMOVAL IS SOFT AND TWO-TIERED, SO THE WORDING MUST PROMISE NO PERMANENCE.
      //   `Library/Components/Modules/ModuleController.vb:L837` `DeleteTabModule(TabId, ModuleId)`
      //   hard-deletes ONLY the per-page reference row (`:L843`), reorders the survivors on that page
      //   (`:L846`), and then tests whether any OTHER page still references the module (`:L849`). Only
      //   if none does does it soft-delete, under the source's own verbatim comment at `:L850`,
      //   `' soft delete the module`, followed by `objModule.TabID = Null.NullInteger` (`:L851`),
      //   `objModule.IsDeleted = True` (`:L852`) and `UpdateModule(objModule)` (`:L853`). Nothing is
      //   destroyed outright, so every one of the phrases below would be a FALSE PROMISE about a flag
      //   flip. This is asserted rather than trusted because the legacy DOCUMENTATION says the opposite:
      //   `:L829` reads `''' Delete a module reference permanently from the database.`, twenty-one lines
      //   above the comment that contradicts it. The code is authoritative and the doc comment is a
      //   legacy defect — Minimal Change Clause item 1 forbids correcting it, so it is annotated here and
      //   left alone in the legacy tree, and this case makes sure the wrong word never reaches a person.
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

    it('addresses the placement with both identities and re-reads once it succeeds', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      const call = expectRequest('DELETE', moduleUrl(4), 'the removal');

      // ⚠ BOTH IDENTITIES TRAVEL, and that is the whole contract. A module placed on every page has one
      // placement per page, so the module identity alone does not name a single row. This mirrors the
      // legacy `DeleteTabModule(TabId, ModuleId)` and emphatically NOT `DeleteModule`, the hard delete.
      expect(call.request.params.get('tabModuleId')).toBe('11');

      // The dialogue is dismissed the moment the command is issued, so the screen is not left holding
      // a modal over an in-flight request.
      expect(query('.confirm-dialog')).withContext('dismissed immediately').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // MIGRATION: THE RE-READ IS MANDATORY AND IS NOT AN OPTIMISATION, BECAUSE THE REMOVAL IS SOFT AND
      //   TWO-TIERED AND THE ROW MAY LEGITIMATELY SURVIVE IT.
      //   `Library/Components/Modules/ModuleController.vb:L837` `DeleteTabModule(TabId, ModuleId)`
      //   hard-deletes only the per-page reference row (`:L843`), reorders the survivors on that page
      //   (`:L846`), then tests whether any other page still references the module (`:L849`) and ONLY
      //   THEN soft-deletes it — `:L850` reads verbatim `' soft delete the module`, with `:L851` setting
      //   the page reference to the absence marker, `:L852` setting `IsDeleted = True` and `:L853`
      //   persisting it. So a `204` does NOT imply the row disappears: whether it still belongs in the
      //   listing is the LISTING endpoint's decision, expressed through its inclusion flag, and an
      //   optimistic local splice would wrongly hide a placement that survived. The fixture below
      //   answers the re-read with a DIFFERENT surviving placement of the SAME module for exactly that
      //   reason — a screen that spliced locally would show zero rows here and this case would catch it.
      //
      //   The legacy XML doc at `:L829` — `''' Delete a module reference permanently from the
      //   database.` — contradicts the comment sixteen lines below it. The code is authoritative; the
      //   doc comment is a legacy defect, annotated and deliberately not fixed. Note too that the
      //   neighbouring `DeleteModule` at `:L819` IS the hard delete, and is emphatically NOT what this
      //   screen invokes.
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

      // MIGRATION: THERE IS NO RESTORE, RECYCLE-BIN, PURGE OR UNDELETE PATH, AND THAT IS AN ABSENCE OF
      //   ENDPOINT RATHER THAN AN ABSENCE OF AFFORDANCE. `ModuleController.vb` contains no
      //   `RestoreModule` of any kind, and the target surface exposes nothing that reverses a removal —
      //   the legacy recycle bin lived under `Website/admin/Tabs/`, which this feature does not replace.
      //   A soft-removed module simply stops appearing. This is asserted as a NEGATIVE because the
      //   temptation runs the other way: the soft delete makes a restore look implementable, and a
      //   screen that offered one would address a route the API does not serve and fail at run time with
      //   a 404 an operator could not act on.
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
      expect(call.request.params.get('tabModuleId')).toBe('0');

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

      // The outcome bridge is gated on the store's SAVING flag, so nothing is announced while the
      // command is in flight - an operator is not told a placement was removed before it was. And the
      // dialogue is already dismissed, so the screen is not holding a modal over an open request.
      expect(notifications()).withContext('nothing announced yet').toHaveSize(0);
      expect(query('.confirm-dialog')).withContext('no modal over the request').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // \u26a0 THE OUTCOME IS ANNOUNCED AS SOON AS THE REMOVAL SETTLES, NOT WHEN THE RE-READ DOES, and the
      // gate is deliberately on the saving flag rather than on the store's aggregate busy flag: a
      // success triggers the mandatory re-read, so the aggregate flag is STILL raised at the moment the
      // removal itself has finished. Waiting for it would delay the message behind an unrelated request
      // and, on a slow listing, could leave a person with no feedback at all.
      expect(notifications()).toEqual([{ severity: 'success', message: REMOVE_SUCCESS_MESSAGE }]);

      // Meanwhile the re-read is outstanding, and the wait for it is shown inside the grid by the shared
      // table - not by a second indicator of this screen's own.
      expect(query('td.data-table__message[data-placeholder]'))
        .withContext('the re-read is visibly in progress')
        .not.toBeNull();
      expect(
        queryAll<HTMLElement>('app-loading-spinner').filter(
          (indicator) => indicator.closest('td.data-table__message') === null,
        ),
      )
        .withContext('still no second indicator')
        .toHaveSize(0);

      answerList([moduleRow({ moduleId: 4, tabModuleId: 12 })]);
    });

    it('requires a fresh confirmation for every removal rather than disabling the affordance', () => {
      arrive([moduleRow({ moduleId: 4, tabModuleId: 11 })]);

      requestRemoval();
      pressDialogue('Delete');

      const first = expectRequest('DELETE', moduleUrl(4), 'the first removal');

      // \u26a0 THE ROW COMMAND CARRIES NO `disabled` BINDING, AND THAT IS DELIBERATE RATHER THAN AN
      // OVERSIGHT: THE CONFIRMATION IS THE GUARD. Pressing it again while a removal is outstanding
      // therefore re-opens the dialogue and sends NOTHING - a second command cannot be dispatched
      // without a second, explicit confirmation. A disabled control would also announce nothing about
      // why it could not be used, whereas a confirmation states the question in words.
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

      // MIGRATION: A REFUSAL OF AUTHORITY IS A WARNING, NOT AN ERROR, AND THE CLASSIFICATION IS MEASURED
      //   FROM THE LEGACY SCREEN THAT DID THE SAME JOB. `Website/admin/Security/AccessDenied.ascx.vb`
      //   reported a refusal with `ModuleMessageType.YellowWarning` in BOTH of its branches, never with
      //   `RedError` — and the legacy severity vocabulary was genuinely three-valued, so choosing the
      //   warning was a choice rather than the only option available. `form-errors.util.ts:L401-L412`
      //   reproduces that decision for 401 and 403, and this screen passes the resolved severity through
      //   UNALTERED: re-deriving it here would give one decision two homes free to disagree.
      //
      // ⚠ AND EXACTLY ONE MESSAGE IS RAISED, WHICH IS A CLAIM ABOUT OWNERSHIP. `ModuleStore` injects
      // only the two transports — it records failures structurally and notifies NOBODY — so the
      // reporting duty falls to this component alone and there is no double-notification hazard to
      // guard against. The single-element assertion below is what would catch a notification being
      // added to the store later: the count, not just the severity, is the contract.
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

      // The banner carries the same event in full, with its reference an operator can quote.
      //
      // ⚠ THE REFERENCE IS THE CORRELATION IDENTIFIER, NOT THE TRACE IDENTIFIER, AND THE PRECEDENCE IS
      // THE SHARED UTILITY'S: `form-errors.util.ts:L474-L486` returns the correlation identifier when
      // one is present and falls back to the trace identifier only when it is not. Every live document
      // carries BOTH, so the fallback is unreachable in practice - which is exactly why asserting the
      // trace identifier here would encode a screen nobody ever sees.
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
      // ⚠ NOTHING IS ANNOUNCED TRANSIENTLY FOR A LISTING FAILURE. The banner already shows it in full,
      // with its per-field detail and its reference, and the reporting effect is gated on a removal
      // being outstanding precisely so one event is never reported twice.
      expect(notifications()).toHaveSize(0);
    });

    it('summarises a per-field refusal without ever announcing it twice', () => {
      create();

      // ⚠ THE PER-FIELD MAP IS READ WITH AN INDEX EXPRESSION, NEVER WITH A PROPERTY ACCESS. Its keys are
      // .NET model-state keys — PascalCase, NOT camel-cased on the way out — and the member is declared
      // as an index signature, which `noPropertyAccessFromIndexSignature` makes a compile error to reach
      // with a dot. Writing the fixture the same way the production code must read it is what keeps this
      // case honest about the constraint.
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

      // ⚠ AND NOTHING IS RAISED TRANSIENTLY FOR IT. A listing failure is shown by the banner alone; the
      // reporting bridge is gated on a REMOVAL being outstanding precisely so one event is never
      // reported twice, once in a banner a person must dismiss and once in a message that disappears.
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

      // Not one `div` with a click handler and not one anchor standing in for an action: links
      // navigate, buttons act, and both are reachable and activatable from the keyboard with no
      // scripting. The legacy screens declared their commands as link buttons, which render as
      // anchors — announced as links and not activated by the space key.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  //
  // Negative cases, and they carry real weight here. Every affordance below EXISTS in the legacy
  // controller and NONE survives into the target surface, so each is a thing a well-meaning change could
  // plausibly add — and adding one would paint a control whose request the server refuses, which is
  // strictly worse than offering no control at all.
  // ---------------------------------------------------------------------------------------------------

  describe('the affordances that deliberately do not exist', () => {
    it('offers no reorder, pane, copy, bulk, cache or synchronise control', () => {
      arrive([
        moduleRow({ moduleId: 4, tabModuleId: 11, moduleOrder: 1 }),
        moduleRow({ moduleId: 5, tabModuleId: 12, moduleOrder: 2 }),
      ]);

      // MIGRATION: SIX LEGACY OPERATIONS HAVE NO ENDPOINT IN THE TARGET AND THEREFORE NO AFFORDANCE HERE.
      //   Each is named in `Library/Components/Modules/ModuleController.vb` and none is reachable now:
      //   `MoveModule` (`:L1078`) for pane placement, both `CopyModule` overloads (`:L700` and `:L743`),
      //   `DeleteAllModules` (`:L795`) for bulk removal, `UpdateModuleOrder` (`:L1160`) and
      //   `UpdateTabModuleOrder` (`:L1197`) for ordering, and `SynchronizeModule` (`:L614`) for the cache
      //   flush. The placement position is consequently DISPLAYED AS DATA and is not editable — which is
      //   itself asserted below, because a numeric column is exactly where an editor would be added.
      // Asserted against the rendered WORDING rather than against class names, and each needle is chosen
      // to be unambiguous: bare `pane` is deliberately avoided because it is a substring of the shared
      // dialogue's own `panel` class, and an assertion that fails for that reason would report the wrong
      // defect. The structural claims that follow are the stronger half of this case in any event.
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

      // ⚠ THE PROOF IS STRUCTURAL RATHER THAN BEHAVIOURAL, AND IT HAS TO BE. A writable signal carries
      // `set` and `update`; a read-only projection does not. Asserting their ABSENCE is the only check
      // that cannot be satisfied by a signal that merely happens not to be written today — and it is
      // made with the `in` operator rather than by casting to a writable type, because such a cast would
      // assert the very thing under test away.
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

      // ⚠ THE CORRELATION IDENTIFIER IS ATTACHED BY THE SHARED INTERCEPTOR, WHICH IS NOT REGISTERED IN
      // THIS HARNESS — so its absence here is the expected reading and is precisely what proves the
      // screen does not write it. A feature that set the header itself would produce it in this
      // configuration, and every request would then carry two sources of one value free to disagree.
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
   * Replaces the painted page by re-reading through the real filter path.
   *
   * Used where a case needs a second, differently shaped page without creating a second component: the
   * ordering path is the cheapest genuine trigger for a re-read, and using a genuine one keeps the
   * case honest about how a page is replaced.
   */
  function answerAfterReplacement(items: readonly ModuleListItem[]): void {
    const sort = queryAll<HTMLButtonElement>('button.data-table__sort')[0];

    expect(sort).withContext('a sortable column exists to trigger a re-read').not.toBeUndefined();

    (sort as HTMLButtonElement).click();
    fixture.detectChanges();

    expectList('the replacement read').flush(pageOf(items));
    fixture.detectChanges();
  }
});
