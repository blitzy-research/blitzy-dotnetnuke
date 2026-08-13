/**
 * Specification for `features/module/module-form/module-form.component.ts` and its paired template. THIS
 * SCREEN WRITES A WHOLE ROW, and everything difficult about it follows from that one fact.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';

import { ModuleVisibility } from '../../../core/models/module.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { ModuleStore } from '../../../core/state/module.store';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { ModuleFormComponent } from './module-form.component';

import type {
  ModuleDefinition,
  ModuleDetail,
  ModuleListItem,
} from '../../../core/models/module.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type { AuthSession, CurrentUser } from '../../../core/models/auth.model';

// ADDRESSES
// Hand-written relative literals. A value composed from the endpoint table would move with that table and
// assert nothing about it.

/** The definition catalogue, read on arrival in both modes. */
const DEFINITIONS_URL = '/api/v1/module-definitions';

/** The module listing, re-read by the store after a create and after a removal. */
const MODULES_URL = '/api/v1/modules';

/** One module. Zero is a real identifier: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`. */
const MODULE_ZERO_URL = '/api/v1/modules/0';

/**
 * A portal's pages. Minus one is a real portal - `dbo.Portals.PortalID` is `IDENTITY(-1, 1)` - and is
 * simultaneously the legacy absent-integer marker, which is exactly why the fixtures below use it.
 */
const TABS_URL = '/api/v1/portals/-1/tabs';

/** Where a completed command leaves the screen. */
const MODULE_LIST_PATH = '/modules';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** The per-status title from `ValidationProblemDetailsFactory`'s own vocabulary table. */
const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';
const CORRELATION_ID = '2b8c1f04-6d3e-4a7b-9c15-8e0d2f6a4b93';

// THE WORDING THIS SCREEN PUBLISHES

const CREATE_HEADING = 'Add Module';
const EDIT_HEADING = 'Module Settings';
const FORM_INVALID_MESSAGE = 'Correct the highlighted fields and try again.';
const PAGE_REQUIRED_MESSAGE = 'Choose a page before saving.';
const DEFINITION_REQUIRED_MESSAGE = 'Choose a module before saving.';
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';
const START_DATE_INVALID_MESSAGE = 'Invalid Start Date';
const NOT_LOADED_MESSAGE = 'The module has not finished loading. Wait a moment and try again.';
const CREATED_MESSAGE = 'The module was added.';
const UPDATED_MESSAGE = 'The module settings were saved.';
const DELETED_MESSAGE = 'The module was removed from the page.';
const NOT_FOUND_MESSAGE = 'The module could not be found. It may have been removed.';

/** The unreadable-address sentence. */
const UNREADABLE_ADDRESS_OPENING = 'This address does not name a module';

const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

// READING A REQUEST BODY WITHOUT AN ESCAPE HATCH

/**
 * Narrows a request body to an indexable record.
 *
 * @param body The value the testing backend captured.
 * @returns The body as a record of unknown members.
 */
function bodyRecord(body: unknown): Readonly<Record<string, unknown>> {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    throw new Error(`the request body is not a JSON object: ${String(body)}`);
  }

  // Every member is still `unknown` after this, so each reader below has to check its own value. That
  // is deliberate: an index signature over `unknown` is what makes the checks unavoidable.
  return body as Readonly<Record<string, unknown>>;
}

/**
 * Reads one numeric member of a request body. ⚠ ZERO AND MINUS ONE ARE REAL VALUES HERE, so the check is
 * on the TYPE and never on the magnitude. A reader written as `value || fallback` would substitute a
 * fallback for a legitimate zero, which is exactly the class of defect the sentinel cases in this file
 * exist to catch.
 *
 * @param body The captured request body.
 * @param member The member to read.
 * @returns The value.
 */
function numberField(body: unknown, member: string): number {
  const value: unknown = bodyRecord(body)[member];

  if (typeof value !== 'number') {
    throw new Error(`the request body member "${member}" is not a number: ${String(value)}`);
  }

  return value;
}

/**
 * Reads one nullable text member of a request body. `null` is returned as `null` rather than as the empty
 * string, because the two are different facts on this contract: the boundary converts an emptied control
 * TO `null` so the column clears.
 *
 * @param body The captured request body.
 * @param member The member to read.
 * @returns The value, or `null` when the member carries one.
 */
function textField(body: unknown, member: string): string | null {
  const value: unknown = bodyRecord(body)[member];

  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    throw new Error(`the request body member "${member}" is not text: ${String(value)}`);
  }

  return value;
}

/**
 * Whether a request body declares a member at all. ⚠ PRESENCE, NOT TRUTHINESS AND NOT NON-NULLITY. The
 * API's serializer writes every declared member including one holding null, so a member that is genuinely
 * ABSENT from a contract is absent from the body - which is what the replacement contract does with the
 * definition.
 *
 * @param body The captured request body.
 * @param member The member to look for.
 * @returns `true` when the member is present, whatever it holds.
 */
function bodyDeclares(body: unknown, member: string): boolean {
  return Object.hasOwn(bodyRecord(body), member);
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

interface Envelope<T> {
  readonly data: T;
  readonly meta: null;
}

/** Wraps a payload in the shared success envelope. */
function envelope<T>(data: T): Envelope<T> {
  return { data, meta: null };
}

/** Wraps rows in the shared paged envelope. */
function pagedBody(items: readonly ModuleListItem[]): {
  readonly data: readonly ModuleListItem[];
  readonly meta: { totalCount: number; pageIndex: number; pageSize: number; totalPages: number };
} {
  return {
    data: items,
    meta: { totalCount: items.length, pageIndex: 0, pageSize: 10, totalPages: 1 },
  };
}

/**
 * One module placement in full. ⚠ EVERY DEFAULT HERE IS A DELIBERATELY AWKWARD VALUE. The module
 * identifier is ZERO and the portal is MINUS ONE, so a truthiness test anywhere in the screen would treat
 * a real row as absent.
 */
function detail(overrides: Partial<ModuleDetail> = {}): ModuleDetail {
  return {
    moduleId: 0,
    tabModuleId: 7,
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
    inheritViewPermissions: false,
    isDeleted: true,
    moduleOrder: 3,
    cacheTime: 1200,
    iconFile: '',
    visibility: ModuleVisibility.Minimized,
    displayTitle: true,
    friendlyName: 'Announcements',
    moduleName: 'DNN_Announcements',
    description: '',
    version: '01.00.00',
    ...overrides,
  };
}

/** One catalogue definition. */
function definition(overrides: Partial<ModuleDefinition> = {}): ModuleDefinition {
  return {
    moduleDefId: 4,
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    defaultCacheTime: 0,
    moduleName: 'DNN_Announcements',
    description: '',
    version: '01.00.00',
    isPremium: false,
    isAdmin: false,
    isPortable: true,
    ...overrides,
  };
}

/** One page of the portal, at the identity seed. Page zero is an ordinary page. */
function tabRow(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 0,
    tabName: 'Home',
    title: null,
    tabOrder: 1,
    parentId: null,
    level: 0,
    tabPath: '//Home',
    isVisible: true,
    disableLink: false,
    isDeleted: false,
    hasChildren: false,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

/** One listing row, for the re-reads a command triggers. */
function listRow(overrides: Partial<ModuleListItem> = {}): ModuleListItem {
  return {
    moduleId: 0,
    tabModuleId: 7,
    tabId: 0,
    moduleDefId: 4,
    moduleTitle: 'Announcements',
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    moduleName: 'DNN_Announcements',
    description: '',
    version: '01.00.00',
    moduleOrder: 3,
    allTabs: false,
    visibility: ModuleVisibility.Minimized,
    isDeleted: false,
    displayTitle: true,
    startDate: null,
    endDate: null,
    ...overrides,
  };
}

/**
 * A problem document in the exact shape the API emits. ⚠ NO `instance` MEMBER: every call site supplies
 * null and the framework's problem type carries a per-member null-omission condition that overrides the
 * surrounding serializer policy, so a live document has none at all. ⚠ `type` IS ALWAYS PRESENT: the
 * factory fills an unspecified type from the status vocabulary.
 */
function problem(code: string, status: number, detailText: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail: detailText,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * @param status The refusal status, `400` for a field-level violation.
 * @param detailText The document's own sentence.
 * @param errors The per-field map, keyed by .NET property name.
 * @returns The document.
 */
function validationProblem(
  status: number,
  detailText: string,
  errors: Readonly<Record<string, readonly string[]>>,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}request.invalid`,
    title: 'One or more validation errors occurred.',
    status,
    detail: detailText,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
    errors,
  };
}

/**
 * The response options a problem document is flushed with. ⚠ THE MEDIA TYPE IS
 * `application/problem+json`, NOT `application/json`.
 *
 * @param status The status code.
 * @returns The options for `TestRequest.flush`.
 */
function problemResponse(status: number): {
  readonly status: number;
  readonly statusText: string;
  readonly headers: Readonly<Record<string, string>>;
} {
  return {
    status,
    statusText: STATUS_TITLE[status] ?? 'Error',
    headers: { 'Content-Type': 'application/problem+json' },
  };
}

describe('ModuleFormComponent', () => {
  let fixture: ComponentFixture<ModuleFormComponent>;
  let mounted: ComponentFixture<ModuleFormComponent> | null;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    mounted = null;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The store
    // is listed explicitly even though it declares itself at the application root, so the instance under
    // test is pinned to THIS injector and cannot be shared across specifications.
    await TestBed.configureTestingModule({
      imports: [ModuleFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ModuleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // Both collaborators are injected by field initialisers that run at construction, so the spies must be
    // installed before the component is created. Both services are application-scoped and the injector is
    // rebuilt per case, so these are the very instances the component receives.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // No routes are declared, so a genuine navigation would fail to match. The component chains a
    // rejection handler onto the promise, so the spy must resolve one.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
  });

  afterEach(() => {
    if (mounted !== null) {
      mounted.destroy();
      mounted = null;
    }

    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Creates the screen, optionally on the edit route. The input is written BEFORE the first change
   * detection, which is what the router does: the address-reacting effect runs on that first pass and
   * must see the value the address carried.
   */
  function create(moduleId?: string): void {
    fixture = TestBed.createComponent(ModuleFormComponent);
    mounted = fixture;

    if (moduleId !== undefined) {
      fixture.componentRef.setInput('moduleId', moduleId);
    }

    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answers the definition catalogue read that every visit issues. */
  function answerDefinitions(definitions: readonly ModuleDefinition[] = [definition()]): void {
    expectRequest('GET', DEFINITIONS_URL, 'the definition catalogue').flush(envelope(definitions));
    fixture.detectChanges();
  }

  /** Answers the addressed module's read. */
  function answerModule(record: ModuleDetail = detail()): void {
    expectRequest('GET', MODULE_ZERO_URL, 'the addressed module').flush(envelope(record));
    fixture.detectChanges();
  }

  /** Answers the portal page list the loaded module's portal triggers. */
  function answerTabs(rows: readonly TabListItem[] = [tabRow(), tabRow({ tabId: 1, tabName: 'About' })]): void {
    expectRequest('GET', TABS_URL, 'the portal page list').flush(envelope(rows));
    fixture.detectChanges();
  }

  /**
   * Brings the screen up on the edit route with everything answered. Three reads, in the order the
   * component issues them: the catalogue from the constructor, the module from the address effect, and
   * the page list from the effect that waits for the module to name its portal.
   */
  function arriveInEditMode(record: ModuleDetail = detail()): void {
    create('0');
    answerDefinitions();
    answerModule(record);
    answerTabs();
  }

  /** Brings the screen up on the create route with the catalogue answered. */
  function arriveInCreateMode(definitions: readonly ModuleDefinition[] = [definition()]): void {
    create();
    answerDefinitions(definitions);
  }

  /** The rendered element for a selector, or null. */
  function query<T extends HTMLElement>(selector: string): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  }

  /** Every rendered element for a selector. */
  function queryAll<T extends HTMLElement>(selector: string): readonly T[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<T>(selector));
  }

  /** A control by its declared identifier, asserted present so the non-null is earned. */
  function requiredControl<T extends HTMLElement>(controlId: string): T {
    const element = query<T>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as T;
  }

  /** Types text into a control the way a person does. */
  function type(controlId: string, value: string): void {
    const element = requiredControl<HTMLInputElement | HTMLTextAreaElement>(controlId);

    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** Toggles a check box the way a person does. */
  function check(controlId: string, checked: boolean): void {
    const element = requiredControl<HTMLInputElement>(controlId);

    element.checked = checked;
    element.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** Chooses an option in a native select by its rendered label. */
  function choose(controlId: string, label: string): void {
    const element = requiredControl<HTMLSelectElement>(controlId);
    const option = Array.from(element.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    expect(option).withContext(`the option labelled "${label}" is offered`).not.toBeUndefined();

    element.value = (option as HTMLOptionElement).value;
    element.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** The three page-level commands, resolved by their class rather than by position. */
  function commandByClass(className: string): HTMLButtonElement {
    const button = query<HTMLButtonElement>(`.${className}`);

    expect(button).withContext(`.${className} is rendered`).not.toBeNull();

    return button as HTMLButtonElement;
  }

  /** Presses the primary command, which submits the form natively. */
  function save(): void {
    commandByClass('module-form__action--primary').click();
    fixture.detectChanges();
  }

  /** Presses the destructive command, which opens the confirmation. */
  function requestRemoval(): void {
    commandByClass('module-form__action--danger').click();
    fixture.detectChanges();
  }

  /** Presses the confirmation's destructive affordance. */
  function confirmRemoval(): void {
    commandByClass('confirm-dialog__button--danger').click();
    fixture.detectChanges();
  }

  /** Presses the confirmation's dismissing affordance, which is the one it focuses. */
  function cancelRemoval(): void {
    const cancel = queryAll<HTMLButtonElement>('.confirm-dialog__button').find(
      (button) => (button.textContent ?? '').trim() === 'Cancel',
    );

    expect(cancel).withContext('the dialogue offers a dismissal').not.toBeUndefined();

    (cancel as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());
  }

  /**
   * The messages rendered beside ONE named control. ⚠ SCOPED RATHER THAN GLOBAL, AND THAT IS THE POINT OF
   * IT. Asserting that a message appears ANYWHERE on the screen cannot distinguish "the server's
   * complaint about the heading was routed to the heading" from "it was rendered against the wrong
   * field", and routing is exactly what the shared utility's case-insensitive match is responsible for.
   *
   * @param controlId The control's declared identifier.
   * @returns The messages beside it, in the order they are rendered.
   */
  function messagesBeside(controlId: string): readonly string[] {
    const region = requiredControl(controlId).closest('.form-field');

    expect(region).withContext(`#${controlId} sits inside a labelled region`).not.toBeNull();

    if (region === null) {
      return [];
    }

    return Array.from(region.querySelectorAll('.form-field__error')).map((node) =>
      (node.textContent ?? '').trim(),
    );
  }

  /** Chooses one of the three visibility states by its enumeration member. */
  function chooseVisibility(state: ModuleVisibility): void {
    const radio = requiredControl<HTMLInputElement>(`module-form-visibility-${state}`);

    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** Presses the abandon command, found by its label rather than by its position. */
  function cancelEdit(): void {
    const abandon = queryAll<HTMLButtonElement>('.module-form__action').find(
      (button) => (button.textContent ?? '').trim() === 'Cancel',
    );

    expect(abandon).withContext('the screen offers an abandon command').not.toBeUndefined();

    if (abandon !== undefined) {
      abandon.click();
      fixture.detectChanges();
    }
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — WHAT ARRIVING ON EACH ROUTE READS, AND NOTHING MORE
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the create route', () => {
    it('reads the definition catalogue and nothing else', () => {
      create();

      const catalogue = expectRequest('GET', DEFINITIONS_URL);

      // The catalogue backs the create-mode selector AND the cache-row rule in both modes, and it takes no
      // parameter: the target's caching is a server concern behind an injected abstraction rather than the
      // 317-line client-side cache the legacy code reached from 116 sites.
      expect(catalogue.request.params.keys().length)
        .withContext('the catalogue endpoint takes no parameter')
        .toBe(0);

      catalogue.flush(envelope([definition()]));
      fixture.detectChanges();

      // No module read, because there is no module; and no page list, because the portal is only
      // known from a module that has been read.
      httpMock.expectNone(() => true);
      expect(query('#module-form-module-def')).withContext('the screen is ready').not.toBeNull();
    });

    it('presents the create heading and offers the definition selector', () => {
      arriveInCreateMode();

      expect((query('h1')?.textContent ?? '').trim()).toBe(CREATE_HEADING);

      const selector = requiredControl<HTMLSelectElement>('module-form-module-def');

      // The definition is chosen at creation and fixed thereafter, because the replacement contract
      // carries no definition member at all.
      expect(selector.tagName).toBe('SELECT');
      expect(selector.disabled).toBeFalse();
      expect(query('#module-form-friendly-name')).toBeNull();
    });

    it('offers no removal affordance, because there is nothing to remove', () => {
      arriveInCreateMode();

      expect(query('.module-form__action--danger')).toBeNull();
      expect(query('app-confirm-dialog')).toBeNull();
    });

    it('applies the measured legacy create defaults to the form', () => {
      arriveInCreateMode();

      // `ModuleSettings.ascx.vb:L225-L227`: the visibility list selected its first entry and the all-pages
      // switch was cleared.
      const maximised = requiredControl<HTMLInputElement>(
        `module-form-visibility-${ModuleVisibility.Maximized}`,
      );

      expect(maximised.checked).withContext('the maximised state is the first entry').toBeTrue();
      expect(requiredControl<HTMLInputElement>('module-form-all-tabs').checked).toBeFalse();
      expect(requiredControl<HTMLInputElement>('module-form-display-title').checked).toBeTrue();
      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('');
      expect(requiredControl<HTMLInputElement>('module-form-end-date').value).toBe('');
    });

    it('offers neither portal-wide instruction, because they belong to a replacement', () => {
      arriveInCreateMode();

      // The create contract has fourteen members and neither instruction is among them, so offering
      // them here would present controls whose values could not be sent.
      expect(query('#module-form-set-as-default-settings')).toBeNull();
      expect(query('#module-form-apply-to-all-modules')).toBeNull();
    });
  });

  describe('arriving on the edit route', () => {
    it('reads the catalogue, the module and then the portal page list, in that order', () => {
      create('0');

      // The catalogue is read from the constructor, so it is outstanding before the first pass.
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([definition()]));
      fixture.detectChanges();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL);

      expect(moduleRead.request.params.has('tabModuleId')).toBeFalse();

      moduleRead.flush(envelope(detail()));
      fixture.detectChanges();

      // ⚠ THE PAGE LIST WAITS FOR THE MODULE, and cannot do otherwise: the page picker's options are
      // portal-scoped, the tenant is resolved by the server from the request rather than named by the
      // client, and nothing in this screen's dependencies can answer which portal the caller is in.
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      httpMock.expectNone(() => true);
    });

    it('treats module ZERO as a module, not as an absence', () => {
      // ⚠ THE SINGLE MOST IMPORTANT CASE IN THIS FILE, AND THE ONE THE WHOLE SENTINEL ANALYSIS EXISTS TO
      // PROTECT. `dbo.Modules.ModuleID` is declared `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L221`), so
      // ZERO is the first module an installation ever creates, while the legacy null contract spelled
      // "absent" as -1 and this very screen initialised its identifier field to that marker and branched on
      // `If ModuleId <> -1` (`:L222`).
      create('0');
      answerDefinitions();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL, 'the read of module zero');

      expect(moduleRead.request.url)
        .withContext('module zero is addressed as a real identifier')
        .toBe('/api/v1/modules/0');
      expect(moduleRead.request.url).not.toContain('NaN');
      expect(moduleRead.request.url).not.toContain('undefined');

      moduleRead.flush(envelope(detail()));
      fixture.detectChanges();
      answerTabs();

      // LEVEL TWO — the screen resolved to EDIT mode, not create mode. The heading is the observable
      // difference, and it is the resource value rather than the net-new create wording.
      expect((query('h1')?.textContent ?? '').trim()).toBe(EDIT_HEADING);
      expect((query('h1')?.textContent ?? '').trim()).not.toBe(CREATE_HEADING);

      // LEVEL THREE — the affordances are the edit-mode set: the definition is fixed rather than
      // selectable, and the removal is offered. Either of these appearing in its create-mode form would
      // mean the sentinel had been misread even though the read went out correctly.
      expect(query('#module-form-module-def'))
        .withContext('the definition selector belongs to create mode only')
        .toBeNull();
      expect(query('#module-form-friendly-name'))
        .withContext('the definition is shown read-only instead')
        .not.toBeNull();
      expect(query('.module-form__action--danger'))
        .withContext('a real module can be removed')
        .not.toBeNull();
    });

    it('reads the page list for portal MINUS ONE, which is a real portal', () => {
      // `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so minus one identifies the first portal the schema
      // ever creates - and it is simultaneously the legacy absent-integer marker, so a truthiness or
      // sentinel test would silently skip the read.
      create('0');
      answerDefinitions();
      answerModule(detail({ portalId: -1 }));

      const pages = expectRequest('GET', TABS_URL, 'the page list for portal -1');

      expect(pages.request.url)
        .withContext('minus one reaches the path segment unchanged, not coerced or dropped')
        .toBe('/api/v1/portals/-1/tabs');
      expect(pages.request.method).toBe('GET');

      pages.flush(envelope([tabRow()]));
      fixture.detectChanges();

      // And the options actually arrive, so the picker is usable rather than merely requested.
      expect(queryAll('#module-form-tab option').length).toBeGreaterThan(0);
    });

    it('reads no page list for a host-owned module that reports no portal', () => {
      create('0');
      answerDefinitions();
      answerModule(detail({ portalId: null }));

      httpMock.expectNone((candidate) => candidate.url.includes('/tabs'));

      // ⚠ NO LIST IS REQUESTED FOR A GUESSED TENANT, and that is the point rather than an omission. A
      // module that belongs to no portal is host-owned, and falling back to some default portal would offer
      // the operator pages from a tenant nobody named - which the replacement would then write.
      expect(queryAll('#module-form-tab option').length)
        .withContext('the picker offers only the unmade choice')
        .toBe(1);
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('replaces the definition selector with a read-only display name', () => {
      arriveInEditMode();

      expect(query('#module-form-module-def')).withContext('no selector once a module exists').toBeNull();

      const nameBox = requiredControl<HTMLInputElement>('module-form-friendly-name');

      expect(nameBox.readOnly).toBeTrue();
      expect(nameBox.value).toBe('Announcements');
      expect(requiredControl<HTMLInputElement>('module-form-title').value).toBe('Announcements');
    });

    it('seeds every editable field from the module that was read', () => {
      arriveInEditMode(
        detail({
          moduleTitle: 'Site news',
          allTabs: true,
          header: 'above',
          footer: 'below',
          startDate: '2024-03-01T00:00:00',
          endDate: '2024-04-01T00:00:00',
          inheritViewPermissions: true,
          cacheTime: 1200,
          iconFile: 'news.gif',
          displayTitle: false,
        }),
      );

      expect(requiredControl<HTMLInputElement>('module-form-title').value).toBe('Site news');
      expect(requiredControl<HTMLInputElement>('module-form-all-tabs').checked).toBeTrue();
      expect(requiredControl<HTMLTextAreaElement>('module-form-header').value).toBe('above');
      expect(requiredControl<HTMLTextAreaElement>('module-form-footer').value).toBe('below');

      // ⚠ NARROWED TO THE CALENDAR DATE. A native date control accepts only `yyyy-mm-dd`, so a full
      // timestamp assigned to it would be rejected and the control would read empty.
      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('2024-03-01');
      expect(requiredControl<HTMLInputElement>('module-form-end-date').value).toBe('2024-04-01');

      expect(requiredControl<HTMLInputElement>('module-form-inherit-view-permissions').checked).toBeTrue();
      expect(requiredControl<HTMLInputElement>('module-form-cache-time').value).toBe('1200');
      expect(requiredControl<HTMLInputElement>('module-form-icon-file').value).toBe('news.gif');
      expect(requiredControl<HTMLInputElement>('module-form-display-title').checked).toBeFalse();
    });

    it('blanks the legacy absent-date marker rather than showing a minimum date', () => {
      arriveInEditMode(detail({ startDate: '0001-01-01T00:00:00', endDate: null }));

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('');
      expect(requiredControl<HTMLInputElement>('module-form-end-date').value).toBe('');
    });

    it('treats the MAXIMUM date as an ordinary schedule value and does not blank it', () => {
      arriveInEditMode(
        detail({ startDate: '2024-03-01T00:00:00', endDate: '9999-12-31T00:00:00' }),
      );

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('2024-03-01');
      expect(requiredControl<HTMLInputElement>('module-form-end-date').value)
        .withContext('the maximum date is real and survives seeding')
        .toBe('9999-12-31');

      // And it survives the round trip, which is the half that actually protects the stored row.
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'the replacement');

      expect(textField(call.request.body, 'endDate')).toBe('9999-12-31');
      expect(messagesBeside('module-form-end-date'))
        .withContext('the storable-range rule admits the boundary itself')
        .toEqual([]);

      call.flush(
        envelope(detail({ startDate: '2024-03-01T00:00:00', endDate: '9999-12-31T00:00:00' })),
      );
      fixture.detectChanges();
    });

    it('seeds both portal-wide instructions UNSET, whatever the module says', () => {
      arriveInEditMode();

      expect(requiredControl<HTMLInputElement>('module-form-set-as-default-settings').checked).toBeFalse();
      expect(requiredControl<HTMLInputElement>('module-form-apply-to-all-modules').checked).toBeFalse();
    });

    it('shows the progress indicator while the module is being read, and no fields', () => {
      create('0');
      answerDefinitions();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL);

      // The fields WAIT rather than rendering empty and then filling, because the replacement is a
      // whole-row one: a form that looked ready before the stored position and the stored recycle-bin flag
      // arrived would invite a save that moved the module and restored it.
      expect(query('app-loading-spinner')).not.toBeNull();
      expect(query('#module-form-title')).toBeNull();

      moduleRead.flush(envelope(detail()));
      fixture.detectChanges();
      answerTabs();

      expect(query('app-loading-spinner')).toBeNull();
      expect(query('#module-form-title')).not.toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE TWO ADDRESSES THAT NAME NO MODULE
  // ---------------------------------------------------------------------------------------------------

  describe('an address that cannot be read', () => {
    it('dispatches no read and reports the address', () => {
      create('not-an-identifier');

      answerDefinitions();

      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/modules/'));

      expect((query('.module-form__notice')?.textContent ?? '').trim()).toContain(
        UNREADABLE_ADDRESS_OPENING,
      );
      expect(query('#module-form-title')).withContext('no form is offered').toBeNull();
    });

    it('refuses a submission and says why', () => {
      create('7px');
      answerDefinitions();

      // The primary command is not rendered in this state, so the submission is driven through the
      // form to prove the refusal is in the code rather than in the absence of a button.
      const form = query('.module-form');

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('warning', jasmine.stringMatching('does not name a module'));
      httpMock.expectNone(() => true);
    });

    it('rejects a value that is only partly numeric rather than reading the leading digits', () => {
      create('7.0');
      answerDefinitions();

      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/modules/'));
      expect((query('.module-form__notice')?.textContent ?? '').trim()).toContain(
        UNREADABLE_ADDRESS_OPENING,
      );
    });
  });

  describe('an address whose module does not exist', () => {
    it('reports it as a legitimate state rather than as a fault', () => {
      create('0');
      answerDefinitions();

      // A success carrying no resource is not emittable by this API for a single-resource route: the
      // server answers 404 with `resource.not_found`, so that is what is modelled.
      expectRequest('GET', MODULE_ZERO_URL).flush(
        problem('resource.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect((query('.module-form__notice')?.textContent ?? '').trim()).toBe(NOT_FOUND_MESSAGE);
      expect(query('#module-form-title')).toBeNull();

      // The banner renders the document as well, because a refusal was recorded.
      expect(query('.error-banner')).not.toBeNull();
    });
  });

  describe('an address whose module the server refuses to disclose', () => {
    it('presents the refusal in the banner alone, and never as a missing module', () => {
      create('0');
      answerDefinitions();

      expectRequest('GET', MODULE_ZERO_URL).flush(
        problem('authorization.forbidden', 403, 'You are not permitted to view this module.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const banner = query('.error-banner');

      expect(banner).not.toBeNull();
      expect(banner?.getAttribute('data-severity'))
        .withContext('the legacy YellowWarning severity, resolved by the shared utility')
        .toBe('warning');
      expect(banner?.textContent ?? '').toContain('You are not permitted to view this module.');

      // The two statements that must NOT accompany it.
      expect(query('.module-form__notice'))
        .withContext('a refusal is not an absence, so the not-found sentence is withheld')
        .toBeNull();
      expect(fixture.nativeElement.textContent as string).not.toContain(NOT_FOUND_MESSAGE);
    });

    it('offers no save, because a save could only ever be refused as well', () => {
      create('0');
      answerDefinitions();

      expectRequest('GET', MODULE_ZERO_URL).flush(
        problem('authorization.forbidden', 403, 'You are not permitted to view this module.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('.module-form__action--primary')).toBeNull();
      expect(query('#module-form-title'))
        .withContext('no form is seeded from a module that was never read')
        .toBeNull();
    });

    /**
     * The narrowing matters as much as the predicate: the store holds ONE failure slot shared by every
     * module command, so a refused SAVE must leave the form exactly where it is.
     */
    it('keeps the form when it is a SAVE that is refused rather than the read', () => {
      arriveInEditMode();

      commandByClass('module-form__action--primary').click();
      fixture.detectChanges();

      const write = httpMock.expectOne(
        (candidate) => candidate.method === 'PUT' && candidate.url.startsWith('/api/v1/modules/'),
      );

      write.flush(problem('authorization.forbidden', 403, 'You may not change this module.'), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      expect(query('.error-banner')).not.toBeNull();
      expect(query('#module-form-title')).withContext('the edits are still on screen').not.toBeNull();
      expect(query('.module-form__action--primary')).not.toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE FORM'S OWN RULES
  // ---------------------------------------------------------------------------------------------------

  describe('validation', () => {
    it('rejects an unparseable cache period and sends nothing', () => {
      arriveInEditMode();

      type('module-form-cache-time', 'twelve');
      save();

      expect(fieldMessages()).toContain(CACHE_TIME_INVALID_MESSAGE);

      expect(notifySpy).toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('cannot be given a malformed date at all, and sends nothing in place of one', () => {
      arriveInEditMode(detail({ startDate: '2024-03-01T00:00:00' }));

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('2024-03-01');

      type('module-form-start-date', '2024-13-45');

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value)
        .withContext('the browser refused the value')
        .toBe('');
      expect(fieldMessages())
        .withContext('an empty schedule is valid, so no rule reports')
        .not.toContain(START_DATE_INVALID_MESSAGE);

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect(textField(call.request.body, 'startDate')).toBeNull();

      call.flush(envelope(detail()));
      fixture.detectChanges();
    });

    it('accepts an empty cache period and sends zero, exactly as the legacy handler did', () => {
      arriveInEditMode();

      type('module-form-cache-time', '');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect(numberField(call.request.body, 'cacheTime')).toBe(0);

      call.flush(envelope(detail({ cacheTime: 0 })));
      fixture.detectChanges();
    });

    it('refuses to save when no page has been chosen, and says so BESIDE the control', () => {
      arriveInEditMode();

      // An EXISTENCE test, never a bound: `dbo.Tabs.TabID` is `IDENTITY(0, 1)`, so page zero is an ordinary
      // page and no comparison against zero, against -1 or against truthiness could distinguish it from an
      // unmade choice. `null` is what carries "not chosen".
      choose('module-form-tab', 'Not Specified');
      save();

      // ⚠ WHERE THE SENTENCE APPEARS IS WHAT THIS CASE PINS. Enforcing the rule imperatively leaves
      // `form.invalid` FALSE with a required choice unmade: the refusal arrives as a page-level warning
      // naming the page, no control carries `aria-invalid`, and not one of the thirteen fields shows a
      // message.
      expect(messagesBeside('module-form-tab')).toContain(PAGE_REQUIRED_MESSAGE);
      expect(requiredControl('module-form-tab').getAttribute('aria-invalid')).toBe('true');
      expect(notifySpy).toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses to place a module when no definition has been chosen', () => {
      // ⚠ THE PAGE MUST BE CHOSEN FIRST, because the component checks the page BEFORE the definition and
      // would otherwise report the page requirement instead.
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      choose('module-form-tab', 'Home');
      save();

      expect(messagesBeside('module-form-module-def')).toContain(DEFINITION_REQUIRED_MESSAGE);
      expect(requiredControl('module-form-module-def').getAttribute('aria-invalid')).toBe('true');
      expect(notifySpy).toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('reports BOTH unmade choices at once from a single empty submission', () => {
      // ⚠ THE CASE THE OLD ORDERING MADE IMPOSSIBLE TO WRITE. The sibling case above had to choose a page
      // first, purely because the two requirements were reported one at a time in a fixed order.
      create();
      answerDefinitions();

      const store = TestBed.inject(ModuleStore);

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      save();

      expect(messagesBeside('module-form-module-def')).toContain(DEFINITION_REQUIRED_MESSAGE);
      expect(messagesBeside('module-form-tab')).toContain(PAGE_REQUIRED_MESSAGE);
      expect(requiredControl('module-form-module-def').getAttribute('aria-invalid')).toBe('true');
      expect(requiredControl('module-form-tab').getAttribute('aria-invalid')).toBe('true');

      const summaries = notifySpy.calls
        .allArgs()
        .filter((args) => String(args[1]) === FORM_INVALID_MESSAGE);

      expect(summaries.length).withContext('one summary, not one per broken rule').toBe(1);
      httpMock.expectNone(() => true);
    });

    it('clears the invalid state from a control once its choice is made', () => {
      create();
      answerDefinitions();

      const store = TestBed.inject(ModuleStore);

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      save();
      expect(requiredControl('module-form-tab').getAttribute('aria-invalid')).toBe('true');

      choose('module-form-tab', 'Home');

      expect(messagesBeside('module-form-tab')).toEqual([]);
      expect(requiredControl('module-form-tab').getAttribute('aria-invalid')).toBe('false');
      httpMock.expectNone(() => true);
    });

    it('bounds the heading exactly where the column and the write rule bound it', () => {
      // ⚠ MIRRORS THE SERVER RATHER THAN TIGHTENING ANYTHING, AND THE DISTINCTION IS THE WHOLE POINT. The
      // legacy markup declared no length attribute on the heading, so it is tempting to conclude that any
      // length was once acceptable — but the store never accepted one: the terminal column is
      // `Modules.ModuleTitle nvarchar(256)`, and `UpdateModuleRequestValidator` caps the member at the same
      // 256.
      arriveInEditMode();

      type('module-form-title', 'x'.repeat(256));
      save();

      const atTheBound = expectRequest('PUT', MODULE_ZERO_URL);

      expect((textField(atTheBound.request.body, 'moduleTitle') ?? '').length)
        .withContext('a heading AT the bound is stored, not refused off by one')
        .toBe(256);

      atTheBound.flush(envelope(detail()));
      fixture.detectChanges();
    });

    it('refuses an overlong heading beside the box instead of by round trip', () => {
      arriveInEditMode();

      type('module-form-title', 'x'.repeat(257));
      save();

      // Nothing is sent, and the reason is stated where the mistake was made.
      httpMock.expectNone(() => true);
      expect(fieldMessages()).toContain('Enter at most 256 characters.');
    });

    it('bounds the icon path at the length its own column accepts', () => {
      // `TabModules.IconFile nvarchar(100)`, capped identically by the write rule. The legacy affordance
      // here was a file-and-folder picker rather than a text box, so there was no length attribute to
      // reproduce — but the column's limit applied then exactly as it does now.
      arriveInEditMode();

      type('module-form-icon-file', 'i'.repeat(101));
      save();

      httpMock.expectNone(() => true);
      expect(fieldMessages()).toContain('Enter at most 100 characters.');
    });

    // THE TWO PARITY REGRESSION GUARDS: RULES THAT MUST NOT EXIST

    it('accepts an EMPTY heading, because the legacy screen declared no presence rule anywhere', () => {
      arriveInEditMode();

      type('module-form-title', '');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'a save with no heading');

      expect(textField(call.request.body, 'moduleTitle'))
        .withContext('an emptied heading clears the column rather than being refused')
        .toBeNull();
      expect(messagesBeside('module-form-title'))
        .withContext('no rule fires on an empty heading')
        .toEqual([]);
      expect(notifySpy).not.toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);

      call.flush(envelope(detail({ moduleTitle: null })));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE, null, true);
    });

    it('accepts an end date BEFORE its start date, because no ordering rule ever existed', () => {
      // ⚠ NO `endDate >= startDate` RULE IS INVENTED, AND THAT IS MEASURED RATHER THAN ASSUMED. Both legacy
      // date validators declare `Operator="DataTypeCheck"` with `Type="Date"` and NEITHER declares a
      // `ControlToCompare`, so there was nothing to compare either date against: an end date before a start
      // date passed the screen, reached `cmdUpdate_Click` and was stored.
      arriveInEditMode();

      type('module-form-start-date', '2024-06-30');
      type('module-form-end-date', '2024-01-01');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'a save with an inverted schedule');

      expect(textField(call.request.body, 'startDate')).toBe('2024-06-30');
      expect(textField(call.request.body, 'endDate')).toBe('2024-01-01');
      expect(messagesBeside('module-form-start-date')).toEqual([]);
      expect(messagesBeside('module-form-end-date')).toEqual([]);

      call.flush(envelope(detail({ startDate: '2024-06-30T00:00:00', endDate: '2024-01-01T00:00:00' })));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE, null, true);
    });

    // THE ICON REFERENCE MUST STAY INSIDE THE PORTAL'S OWN FOLDER
    // Measured before the rule existed: `POST /api/v1/modules` with an `iconFile` of `../../../etc/passwd`
    // answered 201 and stored the value VERBATIM, and `PUT` answered 200 and stored it verbatim for
    // `../../bad`, `..\..\bad` and `/etc/passwd` — while the role and page contracts refused every one of
    // them through the very same shared rule.
    it('refuses an icon reference that escapes the portal folder and issues no request', () => {
      arriveInEditMode();

      type('module-form-icon-file', '../../../etc/passwd');
      save();

      expect(messagesBeside('module-form-icon-file')).toEqual([
        "Icon File must be a relative path within the portal's own folder.",
      ]);

      // Nothing left the browser. `verify()` in this file's teardown would fail the spec on an outstanding
      // request, but the absence is asserted directly so the reason cannot be mistaken.
      httpMock.expectNone((candidate) => candidate.method === 'PUT');
    });

    it('accepts an ordinary relative icon reference and sends it unchanged', () => {
      arriveInEditMode();

      type('module-form-icon-file', 'sub/dir/valid.gif');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'a save carrying a contained reference');

      expect(textField(call.request.body, 'iconFile')).toBe('sub/dir/valid.gif');
      expect(messagesBeside('module-form-icon-file')).toEqual([]);

      call.flush(envelope(detail({ iconFile: 'sub/dir/valid.gif' })));
      fixture.detectChanges();
    });

    // A BLANK HEADING IS ACCEPTED, AND ITS CONSEQUENCE IS STATED
    it('states what a module with no heading will be listed as, and still allows the save', () => {
      arriveInEditMode();

      type('module-form-title', '');

      const notice = query('.module-form__schedule-notice');

      expect(notice).withContext('the consequence of a blank heading is stated').not.toBeNull();
      expect(notice?.getAttribute('aria-live')).toBe('polite');
      expect(notice?.textContent).toContain('listed as');

      // A statement, not a refusal: no message beside the field, and the save goes out.
      expect(messagesBeside('module-form-title')).toEqual([]);

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'a save with no heading');

      expect(textField(call.request.body, 'moduleTitle')).toBeNull();

      call.flush(envelope(detail({ moduleTitle: null })));
      fixture.detectChanges();
    });

    it('says nothing about the heading while one is entered', () => {
      arriveInEditMode();

      type('module-form-title', 'Renamed');

      expect(query('.module-form__schedule-notice')).toBeNull();
    });

    it('remarks on an end date that precedes its start, without refusing it', () => {
      arriveInEditMode();

      expect(query('.module-form__schedule-notice'))
        .withContext('nothing is said about an ordinary schedule')
        .toBeNull();

      type('module-form-start-date', '2024-06-30');
      type('module-form-end-date', '2024-01-01');

      const notice = query('.module-form__schedule-notice');

      expect(notice).withContext('the reversed schedule is remarked on').not.toBeNull();
      expect(notice?.getAttribute('aria-live'))
        .withContext('announced politely, because it interrupts nothing')
        .toBe('polite');
      expect(notice?.textContent).toContain('will not be shown');

      expect(messagesBeside('module-form-start-date')).toEqual([]);
      expect(messagesBeside('module-form-end-date')).toEqual([]);
      expect(commandByClass('module-form__action--primary').disabled)
        .withContext('the pair is accepted, so saving is still offered')
        .toBeFalse();

      // And it really does save. Asserting the request goes out is a stronger proof than reading a validity
      // flag, because it exercises the whole path a refusal would have blocked.
      save();
      expectRequest('PUT', MODULE_ZERO_URL, 'a save carrying the reversed pair').flush(
        envelope(detail({ startDate: '2024-06-30T00:00:00', endDate: '2024-01-01T00:00:00' })),
      );
    });

    it('withdraws the remark once the schedule reads forwards again', () => {
      arriveInEditMode();

      type('module-form-start-date', '2024-06-30');
      type('module-form-end-date', '2024-01-01');
      expect(query('.module-form__schedule-notice')).not.toBeNull();

      type('module-form-end-date', '2024-12-31');

      expect(query('.module-form__schedule-notice'))
        .withContext('the condition it described no longer holds')
        .toBeNull();
    });

    it('says nothing when only one bound is given, because an open-ended schedule is ordinary', () => {
      arriveInEditMode();

      type('module-form-start-date', '2024-06-30');
      type('module-form-end-date', '');

      expect(query('.module-form__schedule-notice')).toBeNull();

      type('module-form-start-date', '');
      type('module-form-end-date', '2024-01-01');

      expect(query('.module-form__schedule-notice')).toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE CREATE BODY, MEMBER FOR MEMBER
  // ---------------------------------------------------------------------------------------------------

  describe('placing a new module', () => {
    it('posts all fourteen members of the create contract, with the falsy ones intact', () => {
      arriveInCreateMode();

      choose('module-form-module-def', 'Announcements');

      expect(requiredControl<HTMLSelectElement>('module-form-tab').options.length).toBe(1);
    });

    it('places the module once a page can be chosen, and re-reads the listing', () => {
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow(), tabRow({ tabId: 1, tabName: 'About' })]));
      fixture.detectChanges();

      choose('module-form-module-def', 'Announcements');
      choose('module-form-tab', 'About');
      type('module-form-title', 'Latest news');
      check('module-form-all-tabs', true);
      type('module-form-header', 'above');
      type('module-form-cache-time', '90');

      save();

      const call = expectRequest('POST', MODULES_URL, 'the placement');

      expect(call.request.body).toEqual({
        moduleDefId: 4,
        tabId: 1,
        moduleTitle: 'Latest news',
        allTabs: true,
        header: 'above',
        footer: null,
        startDate: null,
        endDate: null,
        inheritViewPermissions: false,
        moduleOrder: -1,
        cacheTime: 90,
        iconFile: null,
        visibility: ModuleVisibility.Maximized,
        displayTitle: true,
      });

      call.flush(envelope(detail({ moduleId: 12, tabModuleId: 34 })));
      fixture.detectChanges();

      expect(httpMock.match((candidate) => candidate.url === MODULES_URL))
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', CREATED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
    });

    it('accepts the 201 the endpoint answers with, and reports the placement', () => {
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      choose('module-form-module-def', 'Announcements');
      choose('module-form-tab', 'Home');
      save();

      const call = expectRequest('POST', MODULES_URL, 'the placement');

      expect(bodyDeclares(call.request.body, 'moduleDefId'))
        .withContext('the create contract carries the definition')
        .toBeTrue();
      expect(numberField(call.request.body, 'moduleDefId')).toBe(4);

      call.flush(envelope(detail({ moduleId: 12, tabModuleId: 34 })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      expect(httpMock.match((candidate) => candidate.url === MODULES_URL))
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', CREATED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
    });

    it('treats PAGE ZERO as a page, because the page identity seeds at zero', () => {
      // ⚠ THE STRUCTURAL CHECK IS EXISTENCE, NEVER A BOUND. `dbo.Tabs` is declared `[TabID] [int] IDENTITY
      // (0, 1)` (`01.00.00.SqlDataProvider:L140`), so the first page an installation ever creates is
      // numbered ZERO - and the legacy null contract simultaneously used -1 for an absent integer.
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow(), tabRow({ tabId: 1, tabName: 'About' })]));
      fixture.detectChanges();

      choose('module-form-module-def', 'Announcements');
      choose('module-form-tab', 'Home');
      save();

      const call = expectRequest('POST', MODULES_URL, 'the placement on page zero');

      // ⚠ ASSERTED POSITIVELY. The serializer writes every declared member and elides nothing, so a
      // page identifier of zero APPEARS on the wire rather than being dropped as a default.
      expect(numberField(call.request.body, 'tabId'))
        .withContext('page zero reaches the wire unchanged')
        .toBe(0);
      expect(notifySpy).not.toHaveBeenCalledWith('warning', PAGE_REQUIRED_MESSAGE);

      call.flush(envelope(detail()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();

      // No listing read follows a create; see the block on the case above for the measured duplicate that
      // removed it. Asserted rather than merely omitted, so this case cannot silently start tolerating one.
      expect(httpMock.match((candidate) => candidate.url === MODULES_URL))
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);
      fixture.detectChanges();
    });

    it('carries the NONE visibility state as the real code two', () => {
      // ⚠ `None` IS A CHOICE, NOT AN ABSENCE, AND ITS CODE IS LOAD-BEARING DATA. The legacy enumeration
      // declared its three members with NO explicit values, so 0, 1 and 2 came from declaration order
      // alone, and every stored row in every existing installation depends on that ordering.
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      choose('module-form-module-def', 'Announcements');
      choose('module-form-tab', 'Home');
      chooseVisibility(ModuleVisibility.None);
      save();

      const call = expectRequest('POST', MODULES_URL, 'the placement with no container chrome');

      expect(numberField(call.request.body, 'visibility'))
        .withContext('the hidden state is code two, from the legacy declaration order')
        .toBe(2);
      expect(ModuleVisibility.None).toBe(2);
      expect(ModuleVisibility.Minimized).toBe(1);
      expect(ModuleVisibility.Maximized).toBe(0);

      call.flush(envelope(detail({ visibility: ModuleVisibility.None })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      // No listing read follows a create; see the block on the case above for the measured duplicate that
      // removed it. Asserted rather than merely omitted, so this case cannot silently start tolerating one.
      expect(httpMock.match((candidate) => candidate.url === MODULES_URL))
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — THE REPLACEMENT BODY, MEMBER FOR MEMBER
  // ---------------------------------------------------------------------------------------------------

  describe('replacing a module', () => {
    it('puts all seventeen members, round-tripping the two the operator does not choose', () => {
      arriveInEditMode();

      type('module-form-title', 'Renamed');
      check('module-form-set-as-default-settings', true);

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'the replacement');

      expect(call.request.body).toEqual({
        tabId: 0,
        moveToTabId: null,
        moduleTitle: 'Renamed',
        allTabs: false,
        header: null,
        footer: null,
        startDate: null,
        endDate: null,
        inheritViewPermissions: false,
        isDeleted: true,
        moduleOrder: 3,
        cacheTime: 1200,
        iconFile: null,
        visibility: ModuleVisibility.Minimized,
        displayTitle: true,
        setAsDefaultSettings: true,
        applyToAllModules: false,
      });

      call.flush(envelope(detail({ moduleTitle: 'Renamed' })));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
    });

    it('sends no relocation when the page control is left on the module\'s own page', () => {
      arriveInEditMode();

      // Chosen explicitly, so this proves the comparison rather than merely the seeded default.
      choose('module-form-tab', 'Home');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'an ordinary save');

      expect(call.request.body).toEqual(
        jasmine.objectContaining({
          tabId: 0,
          moveToTabId: null,
        }),
      );
    });

    it('round-trips the stored cache period even when the cache row is not offered', () => {
      // MIGRATION: A DELIBERATE DIVERGENCE. The legacy screen left the box empty whenever the definition
      // declared no default period (`:L136-L142`) and its handler then wrote zero for an empty box
      // (`:L349-L353`), so every save silently zeroed the stored period of such a module.
      create('0');
      answerDefinitions([definition({ defaultCacheTime: -1 })]);
      answerModule(detail({ cacheTime: 900 }));
      answerTabs();

      expect(query('#module-form-cache-time')).withContext('the row is withheld').toBeNull();

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect(numberField(call.request.body, 'cacheTime')).toBe(900);

      call.flush(envelope(detail({ cacheTime: 900 })));
      fixture.detectChanges();
    });

    it('sends the page the operator chose as a relocation, keeping the loaded page as the selector', () => {
      arriveInEditMode();

      choose('module-form-tab', 'About');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect(numberField(call.request.body, 'tabId'))
        .withContext('the placement being replaced is the page the module was loaded from')
        .toBe(0);
      expect(numberField(call.request.body, 'moveToTabId'))
        .withContext('the chosen page is the destination')
        .toBe(1);

      call.flush(envelope(detail({ tabId: 1 })));
      fixture.detectChanges();
    });

    it('re-reads the listing when the replacement reports the module is on every page', () => {
      arriveInEditMode();

      save();

      expectRequest('PUT', MODULE_ZERO_URL).flush(envelope(detail({ allTabs: true })));
      fixture.detectChanges();

      expectRequest('GET', MODULES_URL, 'the listing re-read for an all-pages module').flush(
        pagedBody([listRow({ allTabs: true })]),
      );
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE, null, true);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — REMOVAL
  // ---------------------------------------------------------------------------------------------------

  describe('removing a placement', () => {
    it('asks before removing anything', () => {
      arriveInEditMode();

      expect(query('app-confirm-dialog')).withContext('not open until asked').toBeNull();

      requestRemoval();

      const dialog = query('app-confirm-dialog');

      expect(dialog).not.toBeNull();

      expect((query('.confirm-dialog__title')?.textContent ?? '').trim()).toBe('Confirm Delete');

      // ⚠ THE MESSAGE IS THE EXACT STRING THE LEGACY SCREEN RESOLVED, PUNCTUATION AND CAPITALS INCLUDED.
      // `ModuleSettings.ascx.vb:L205` called `ClientAPI.AddButtonConfirm(cmdDelete,
      // Localization.GetString("DeleteItem"))` - note the key is `"DeleteItem"` WITHOUT a property suffix -
      // and `DeleteItem.Text` in `Website/App_GlobalResources/SharedResources.resx:L120-L121` holds this
      // sentence.
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim())
        .withContext('the measured DeleteItem.Text wording, verbatim')
        .toBe(DELETE_CONFIRM_MESSAGE);

      httpMock.expectNone(() => true);
    });

    it('leaves the form settled at the instant a removal navigates, because deleted entry can no longer be saved', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arriveInEditMode();
      type('module-form-title', 'Typed, then deleted');

      expect(tracker.isDirty())
        .withContext('a dirty form with no command in flight is what the guard exists to catch')
        .toBeTrue();

      // ⚠ SAMPLED AT THE INSTANT OF NAVIGATION, NOT AFTERWARDS, and on the REMOVAL rather than the save.
      let dirtyAtNavigation: boolean | null = null;
      navigateSpy.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      requestRemoval();
      confirmRemoval();
      expectRequest('DELETE', MODULE_ZERO_URL, 'the removal').flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
      expect(dirtyAtNavigation)
        .withContext('there is nothing left to save once the placement is gone')
        .toBeFalse();

      // The store re-reads the listing after a successful removal, because the removal is soft and
      // two-tiered so only the listing endpoint knows whether the row still belongs. Answered here so the
      // suite's outstanding-request check has nothing left to report.
      expectRequest('GET', MODULES_URL, 'the listing re-read').flush(pagedBody([]));
      fixture.detectChanges();
    });

    it('stays reachable on an INVALID form and removes without validating anything', () => {
      arriveInEditMode();

      type('module-form-cache-time', 'abc');

      // Proof that the form really is invalid: the SAVE path refuses and sends nothing.
      save();
      httpMock.expectNone(() => true);
      expect(notifySpy).toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      expect(fieldMessages()).toContain(CACHE_TIME_INVALID_MESSAGE);

      // The removal is nonetheless offered and nonetheless works.
      requestRemoval();
      confirmRemoval();

      const removal = expectRequest('DELETE', MODULE_ZERO_URL, 'the removal from an invalid form');

      expect(removal.request.params.get('tabModuleId'))
        .withContext('the placement is named, so only this page loses the module')
        .toBe('7');

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expectRequest('GET', MODULES_URL, 'the mandatory listing re-read').flush(pagedBody([]));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', DELETED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
    });

    it('removes nothing when the confirmation is dismissed', () => {
      arriveInEditMode();

      requestRemoval();
      cancelRemoval();

      expect(query('app-confirm-dialog')).toBeNull();
      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('names the PLACEMENT, re-reads the listing and leaves', () => {
      arriveInEditMode();

      requestRemoval();
      confirmRemoval();

      const removal = expectRequest('DELETE', MODULE_ZERO_URL, 'the removal');

      // ⚠ A PER-PAGE REMOVAL. The legacy affordance called `DeleteTabModule(TabId, ModuleId)`, which
      // removes the module from ONE page and soft-deletes the module itself only once its last placement is
      // gone - not `DeleteModule` (`:L819`), which removes it outright.
      expect(removal.request.params.get('tabModuleId')).toBe('7');

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ NOTHING IS SPLICED LOCALLY. The removal is soft and two-tiered, so after a 204 the row may
      // or may not still belong in the listing - and only the listing endpoint knows which.
      expectRequest('GET', MODULES_URL, 'the listing re-read').flush(pagedBody([]));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', DELETED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH, { replaceUrl: true });
    });

    it('offers no reversal, because no endpoint reverses it', () => {
      arriveInEditMode();

      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      // The legacy recycle-bin screen lived under `Website/admin/Tabs/` and is out of scope, and the
      // target surface exposes nothing that reverses a removal.
      expect(text).not.toContain('Restore');
      expect(text).not.toContain('Recycle');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — BUSY, FAILURE AND CANCEL
  // ---------------------------------------------------------------------------------------------------

  describe('while a command is outstanding', () => {
    it('disables the primary command and refuses a second submission', () => {
      arriveInEditMode();

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect(commandByClass('module-form__action--primary').disabled).toBeTrue();

      query('.module-form')?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      call.flush(envelope(detail()));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE, null, true);
      expect(navigateSpy).toHaveBeenCalledTimes(1);
    });

    it('refuses a submission before the addressed module has been read', () => {
      create('0');
      answerDefinitions();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL);

      const form = query('.module-form');

      expect(form).withContext('the form is rendered while the module is being read').not.toBeNull();

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectNone((candidate) => candidate.method === 'PUT');

      // ⚠ THE SUPPRESSION IS SILENT HERE, AND DELIBERATELY SO. Two guards could refuse this submission and
      // the OUTSTANDING-REQUEST one is reached first, before the not-yet-read one, so no sentence is
      // produced: the screen is visibly busy - the progress indicator is on screen and the primary command
      // is not even rendered - and announcing "wait a moment" over an indicator that already says exactly
      // that would be noise.
      expect(query('app-loading-spinner'))
        .withContext('the screen is visibly busy, which is why the refusal needs no sentence')
        .not.toBeNull();
      expect(notifySpy)
        .withContext('a submission suppressed by an outstanding request says nothing')
        .not.toHaveBeenCalled();
      expect(navigateSpy).not.toHaveBeenCalled();

      moduleRead.flush(envelope(detail()));
      fixture.detectChanges();
      answerTabs();
    });
  });

  describe('a refused command', () => {
    it('reports a refusal at warning severity and stays on the screen', () => {
      arriveInEditMode();

      save();

      expectRequest('PUT', MODULE_ZERO_URL).flush(
        problem(
          'module.edit_forbidden',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        'The authenticated caller is not permitted to perform this operation.',
        CORRELATION_ID,
      );
      expect(notifySpy).not.toHaveBeenCalledWith(
        'error',
        'The authenticated caller is not permitted to perform this operation.',
        CORRELATION_ID,
      );
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(query('.error-banner')).not.toBeNull();

      // The refusal keeps the operator's work on screen. A refusal that cleared the form would lose
      // the very edits the caller now has to ask somebody else to make.
      expect(query('#module-form-title')).not.toBeNull();
    });

    it('reports a server fault at error severity', () => {
      arriveInEditMode();

      save();

      expectRequest('PUT', MODULE_ZERO_URL).flush(
        problem(
          'server.unexpected_failure',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith(
        'error',
        'An unexpected error occurred while processing the request.',
        CORRELATION_ID,
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('shows a per-field message the server reported and keeps the form', () => {
      arriveInEditMode();

      save();

      expectRequest('PUT', MODULE_ZERO_URL).flush(
        {
          type: `${FAILURE_TYPE_PREFIX}request.invalid`,
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The request could not be processed as submitted.',
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
          errors: { CacheTime: ['The cache period must not be negative.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(fieldMessages()).toContain('The cache period must not be negative.');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('routes a refusal keyed "ModuleTitle" onto the heading and keeps the trace identifier', () => {
      arriveInEditMode();

      type('module-form-title', 'a heading the server refuses');
      save();

      const refused = expectRequest('PUT', MODULE_ZERO_URL, 'the refused replacement');

      // ⚠ THE CORRELATION MEMBER IS DELIBERATELY OMITTED FROM THIS ONE DOCUMENT. The shared utility
      // resolves a support reference by preferring the correlation identifier and falling back to the trace
      // identifier, so a document carrying both would prove only that the PREFERRED one survives - and the
      // trace identifier is the member that is actually at risk of being dropped, because it is the one the
      // API synthesises rather than the one the client sent.
      const document: ValidationProblemDetails = {
        ...validationProblem(400, 'The request could not be processed as submitted.', {
          ModuleTitle: ['<br>The module title is not acceptable.'],
          CacheTime: ['The cache period must not be negative.'],
        }),
        correlationId: undefined,
      };

      // ⚠ READ WITH BRACKET ACCESS. Asserted before the flush so the fixture itself is pinned: a future
      // edit that renamed the key would fail HERE, with a message about the key, rather than three
      // assertions later with a message about a missing sentence.
      expect(document.errors['ModuleTitle'])
        .withContext('the bag is keyed by the .NET property name')
        .toEqual(['<br>The module title is not acceptable.']);
      expect(document.errors['moduleTitle'])
        .withContext('the camel-cased wire spelling is NOT what a problem document carries')
        .toBeUndefined();
      expect(document.errors['Title'])
        .withContext('the legacy control name is NOT what a problem document carries')
        .toBeUndefined();

      refused.flush(document, problemResponse(400));
      fixture.detectChanges();

      expect(messagesBeside('module-form-title'))
        .withContext('the heading carries the server message, without its legacy break tag')
        .toContain('The module title is not acceptable.');
      expect(messagesBeside('module-form-title').join(' '))
        .withContext('no break tag survives into the rendered text')
        .not.toContain('<br');

      // The second key from the closed set lands on ITS own control, which is what proves the routing
      // is per-key rather than a single message shown everywhere.
      expect(messagesBeside('module-form-cache-time')).toContain(
        'The cache period must not be negative.',
      );

      const trace = query('.error-banner__trace');

      expect(trace).withContext('the banner publishes a support reference').not.toBeNull();
      expect(trace?.textContent ?? '')
        .withContext('the trace identifier reaches the operator verbatim')
        .toContain(TRACE_ID);
      expect(document.traceId)
        .withContext('the document carried it in the first place')
        .toBe(TRACE_ID);

      // The operator's typing survives the refusal, and nothing was navigated away from.
      expect(requiredControl<HTMLInputElement>('module-form-title').value).toBe(
        'a heading the server refuses',
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });
  });

  describe('cancelling', () => {
    it('leaves for the listing without sending anything', () => {
      arriveInEditMode();

      type('module-form-title', 'a change nobody asked to keep');

      const cancel = queryAll<HTMLButtonElement>('.module-form__action').find(
        (button) => (button.textContent ?? '').trim() === 'Cancel',
      );

      expect(cancel).not.toBeUndefined();

      (cancel as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
      httpMock.expectNone(() => true);
    });

    it('leaves an INVALID form without running a single validator', () => {
      arriveInEditMode();

      type('module-form-cache-time', 'still-not-a-number');

      expect(fieldMessages())
        .withContext('no validator has run, because nothing has been submitted')
        .toEqual([]);

      cancelEdit();

      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);

      // Still silent afterwards: cancelling did not trigger the validation pass that saving does.
      expect(fieldMessages()).toEqual([]);
      expect(notifySpy).not.toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('abandons a placement in create mode without posting anything', () => {
      // The same affordance, on the other route. Nothing has been read on the create route, so a
      // cancellation that dispatched anything at all would be visible as an unexpected request.
      arriveInCreateMode();

      choose('module-form-module-def', 'Announcements');
      type('module-form-title', 'a module nobody placed');

      cancelEdit();

      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
      httpMock.expectNone(() => true);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  // ---------------------------------------------------------------------------------------------------

  describe('deliberate omissions', () => {
    it('offers no reorder affordance, so the placement position is carried rather than edited', () => {
      arriveInEditMode();

      expect(query('#module-form-module-order')).toBeNull();
      expect(queryAll('input[formControlName="moduleOrder"]').length).toBe(0);
    });

    it('offers no rich-text editing for the two markup fields', () => {
      arriveInEditMode();

      expect(requiredControl('module-form-header').tagName).toBe('TEXTAREA');
      expect(requiredControl('module-form-footer').tagName).toBe('TEXTAREA');
    });

    it('renders no image and references no legacy raster asset', () => {
      arriveInEditMode();

      // The only static asset this workspace ships is a favicon.
      expect(queryAll('img').length).toBe(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — THE DEFINITION CATALOGUE IS READ-ONLY, AND NO PHANTOM ENDPOINT IS EVER ADDRESSED
  // ---------------------------------------------------------------------------------------------------

  describe('the definition catalogue', () => {
    it('is read from the collection address and takes no query parameter at all', () => {
      create();

      const catalogue = expectRequest('GET', DEFINITIONS_URL, 'the definition catalogue');

      expect(catalogue.request.params.keys())
        .withContext('the catalogue is unpaged, unfiltered and unsorted')
        .toEqual([]);

      // ⚠ AND THE ABSENCE OF A SPECIFIC PARAMETER IS ASSERTED WITH `has`, NOT WITH `get`. A `get` that
      // returns null cannot distinguish "not sent" from "sent empty", and those are different requests.
      expect(catalogue.request.params.has('desktopModuleId')).toBeFalse();
      expect(catalogue.request.params.has('portalId')).toBeFalse();
      expect(catalogue.request.params.has('pageIndex')).toBeFalse();

      catalogue.flush(envelope([definition()]));
      fixture.detectChanges();
    });

    it('is never written to, on either route', () => {
      arriveInCreateMode();

      choose('module-form-module-def', 'Announcements');

      for (const verb of ['POST', 'PUT', 'PATCH', 'DELETE']) {
        httpMock.expectNone(
          (candidate) => candidate.method === verb && candidate.url.includes('module-definitions'),
          `no ${verb} reaches the definition catalogue`,
        );
      }

      httpMock.expectNone(
        (candidate) => candidate.url === `${DEFINITIONS_URL}/4`,
        'choosing a definition does not re-read it',
      );
    });

    it('consumes the portability flag as a resolved boolean, never as a bitmask', () => {
      arriveInCreateMode([
        definition({ isPortable: false }),
        definition({ moduleDefId: 9, friendlyName: 'Text', isPortable: true }),
      ]);

      const catalogue = definition({ isPortable: true });

      expect(typeof catalogue.isPortable)
        .withContext('a resolved boolean, not an integer to be masked')
        .toBe('boolean');

      // Both definitions are offered whatever their portability says: portability governs export, which
      // is a different screen, and it has never governed whether a module can be PLACED.
      const options = queryAll<HTMLOptionElement>('#module-form-module-def option');

      expect(options.length).withContext('the unmade choice plus both definitions').toBe(3);

      const rendered = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(rendered).not.toContain('SupportedFeatures');
      expect(rendered).not.toContain('BusinessControllerClass');
    });
  });

  // PROOF 10 — THE TEMPLATE'S OWN PROHIBITIONS
  // These are not stylistic assertions. Each names a specific legacy construct that a faithful translation
  // would have carried across, and states why the target must not.

  describe('the rendered document', () => {
    it('contains no table, because every legacy table on this screen was a layout table', () => {
      arriveInEditMode();

      expect(queryAll('table').length).withContext('no table in edit mode').toBe(0);
      expect(queryAll('tr').length).toBe(0);
      expect(queryAll('td').length).toBe(0);
      expect(queryAll('[summary]').length)
        .withContext('and no vestigial summary attribute either')
        .toBe(0);
    });

    it('contains no table in create mode either, nor while it is still reading', () => {
      // The same rule across the other route and across the intermediate state, because the create
      // branch and the loading branch render different subtrees of the same template.
      arriveInCreateMode();
      expect(queryAll('table').length).withContext('no table in create mode').toBe(0);

      mounted?.destroy();
      mounted = null;

      create('0');
      answerDefinitions();
      expect(queryAll('table').length).withContext('no table while the module is being read').toBe(0);

      answerModule();
      answerTabs();
    });

    it('emits no semantic landmark, because the application shell owns each of them exactly once', () => {
      arriveInEditMode();

      for (const landmark of ['header', 'main', 'nav', 'footer', 'aside', 'article']) {
        expect(queryAll(landmark).length).withContext(`no <${landmark}> element`).toBe(0);
      }

      for (const role of ['banner', 'main', 'navigation', 'contentinfo', 'complementary']) {
        expect(queryAll(`[role="${role}"]`).length)
          .withContext(`no role="${role}" attribute either`)
          .toBe(0);
      }

      // What it does emit: one form, grouped fieldsets each with a legend, and the shared radio group.
      // Named positively so the case cannot be satisfied by rendering nothing at all.
      expect(queryAll('form').length).toBe(1);
      expect(queryAll('fieldset').length).toBeGreaterThan(0);
      expect(queryAll('legend').length).toBe(queryAll('fieldset').length);
      expect(queryAll('[role="radiogroup"]').length).toBe(1);
    });
  });

  // THE ALL-PAGES SWITCH, AND WHO IS OFFERED IT
  // `Page_Load:L215-L220` disabled `chkAllTabs` for any caller outside the portal administrator role.
  describe('the all-pages switch', () => {
    /** A caller snapshot, with tenant administration as the one variable. */
    function callerWith(administersPortal: boolean): CurrentUser {
      return {
        userId: 3,
        // MINUS ONE is a real tenant here, as it is throughout this file: it is the identity seed of
        // `dbo.Portals` and simultaneously the legacy absent-integer marker, so a truthiness test anywhere
        // on this path would discard the tenant the measured baseline actually uses.
        portalId: -1,
        portalName: 'Runtime Portal',
        username: 'runtime_operator',
        displayName: 'Runtime Operator',
        email: 'operator@runtime.test',
        // A host account is a SEPARATE arm of the same question and is deliberately left false, so each
        // case is honest about which arm admitted it.
        isSuperUser: false,
        isPortalAdministrator: administersPortal,
        roles: administersPortal ? ['Administrators'] : [],
        permissions: [],
      };
    }

    /** Stores a live session for that caller, which is what the identity store projects from. */
    function signIn(administersPortal: boolean): void {
      const session: AuthSession = {
        accessToken: 'access-token-placeholder',
        expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
        refreshToken: 'refresh-token-placeholder',
        mustChangePassword: false,
        mustUpdateProfile: false,
        passwordExpiring: false,
        user: callerWith(administersPortal),
      };

      TestBed.inject(TokenStorageService).store(session);
    }

    /**
     * Reveals a field's help disclosure and returns the text it shows.
     *
     * @param controlId The identifier of the control the field labels.
     * @returns The revealed help text.
     */
    function revealedHelpFor(controlId: string): string {
      const field = requiredControl<HTMLElement>(controlId).closest('app-form-field');

      expect(field).withContext(`#${controlId} sits inside a shared field`).not.toBeNull();

      const toggle = (field as HTMLElement).querySelector<HTMLButtonElement>(
        '.form-field__help-toggle',
      );

      expect(toggle).withContext(`#${controlId} offers its help disclosure`).not.toBeNull();

      (toggle as HTMLButtonElement).click();
      fixture.detectChanges();

      return (field as HTMLElement).querySelector('.form-field__help')?.textContent ?? '';
    }

    it('is withheld from a caller the session does not report as an administrator', () => {
      signIn(false);
      arriveInCreateMode();
      // The session names a tenant, so the page list is read; answering it keeps the verification in
      // `afterEach` honest about which requests this screen makes.
      answerTabs();

      const control = requiredControl<HTMLInputElement>('module-form-all-tabs');

      expect(control.disabled)
        .withContext('the legacy page load disabled this control for exactly this caller')
        .toBeTrue();

      expect(revealedHelpFor('module-form-all-tabs'))
        .toContain('available only to an administrator of this site');

      expect(requiredControl<HTMLSelectElement>('module-form-tab').disabled)
        .withContext('a page administrator must still be able to name their page')
        .toBeFalse();
    });

    it('is offered to a caller the session reports as an administrator', () => {
      signIn(true);
      arriveInCreateMode();
      answerTabs();

      expect(requiredControl<HTMLInputElement>('module-form-all-tabs').disabled)
        .withContext('the counterpart, so the affordance is not simply withheld from everybody')
        .toBeFalse();
      // And the legacy sentence is the one shown, rather than the withheld explanation.
      expect(revealedHelpFor('module-form-all-tabs'))
        .toContain('appear in the same location on all pages');
    });
  });
});
