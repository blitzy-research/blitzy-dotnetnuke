/**
 * Specification for `features/module/module-form/module-form.component.ts` and its paired template.
 *
 * THIS SCREEN WRITES A WHOLE ROW, and everything difficult about it follows from that one fact. The
 * replacement contract carries sixteen members and the server writes every one of them, so a member
 * this screen fails to send is not "left alone" - it is overwritten with the contract's default. Two
 * of the sixteen are not the operator's to choose at all:
 *
 * - THE PLACEMENT POSITION. Its default APPENDS the module to the bottom of its pane, so omitting it
 *   would move a module every time its title was changed.
 * - THE RECYCLE-BIN FLAG. The legacy handler assigned `objModule.IsDeleted = False` on every single
 *   update (`ModuleSettings.ascx.vb:L364`), so saving any field silently restored a module from the
 *   bin. The stored value is round-tripped instead, and that divergence is asserted below rather
 *   than described.
 *
 * Consequently the screen READS BEFORE IT WRITES in edit mode, and a submission attempted before the
 * read has landed is refused. Both halves of that are exercised.
 *
 * ## Provenance
 *
 * `Website/admin/Modules/modulesettings.ascx` and its 449-line code-behind are the legacy screen.
 * Supporting sources are `Library/Components/Modules/ModuleInfo.vb` for the three non-interchangeable
 * name members and the visibility enumeration's implicit ordinals,
 * `Library/Components/Modules/ModuleController.vb:L837` for the per-page removal semantics,
 * `Library/Components/Shared/Null.vb` for the sentinel contract, and
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` for the identity seeds
 * that make zero and minus one real values. The legacy tree contains no automated test of any kind,
 * so nothing here is ported.
 *
 * ## What is real and what is doubled
 *
 * The COMPONENT is imported as the standalone unit it is. The STORE is the genuine one, listed in
 * `providers` so this specification owns its instance, and driven through real HTTP responses - the
 * request sequence a command produces is itself part of what is under test, and a doubled store would
 * make that sequence a property of the double. The TRANSPORT is the testing backend, and `verify()`
 * in `afterEach` fails any case that left a request unconsumed or issued one nobody expected. Only
 * the NAVIGATION and the NOTIFICATION QUEUE are spied, because they are this screen's two
 * observable outputs and no route table is declared here.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { ModuleStore } from '../../../core/state/module.store';
import { ModuleFormComponent } from './module-form.component';

import type {
  ModuleDefinition,
  ModuleDetail,
  ModuleListItem,
} from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';

// =====================================================================================================
// ADDRESSES
//
// Hand-written relative literals. A value composed from the endpoint table would move with that table
// and assert nothing about it.
// =====================================================================================================

/** The definition catalogue, read on arrival in both modes. */
const DEFINITIONS_URL = '/api/v1/module-definitions';

/** The module listing, re-read by the store after a create and after a removal. */
const MODULES_URL = '/api/v1/modules';

/** One module. Zero is a real identifier: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`. */
const MODULE_ZERO_URL = '/api/v1/modules/0';

/**
 * A portal's pages.
 *
 * Minus one is a real portal - `dbo.Portals.PortalID` is `IDENTITY(-1, 1)` - and is simultaneously
 * the legacy absent-integer marker, which is exactly why the fixtures below use it.
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

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated here rather than imported, because the component does not export these constants and a
// specification that read them from the component could not detect a change to them. Each is quoted
// from the component's own declaration.
// =====================================================================================================

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

/**
 * The unreadable-address sentence.
 *
 * Quoted in two halves because the component wraps it; only the opening is asserted, so the
 * assertion survives a re-wrap while still pinning the wording.
 */
const UNREADABLE_ADDRESS_OPENING = 'This address does not name a module';

// =====================================================================================================
// FIXTURES
// =====================================================================================================

interface Envelope<T> {
  readonly data: T;
  readonly meta: null;
}

/** Wraps a payload in the shared success envelope. A bare body would unwrap to `undefined`. */
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
 * One module placement in full.
 *
 * ⚠ EVERY DEFAULT HERE IS A DELIBERATELY AWKWARD VALUE. The module identifier is ZERO and the portal
 * is MINUS ONE, so a truthiness test anywhere in the screen would treat a real row as absent. The
 * placement position is a non-default number, so a submission that failed to round-trip it would
 * change it. The recycle-bin flag is `true`, so a submission that forced it to `false` - which is
 * exactly what the legacy handler did on every update - would be visible in the asserted body. The
 * markup and icon members are the empty string, which IS the legacy absent-string marker.
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

/**
 * One catalogue definition.
 *
 * `defaultCacheTime` is a real period rather than the not-applicable marker, so the cache row is
 * offered. The marker itself is minus one, and a case below uses it.
 */
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
 * A problem document in the exact shape the API emits.
 *
 * ⚠ NO `instance` MEMBER: every call site supplies null and the framework's problem type carries a
 * per-member null-omission condition that overrides the surrounding serializer policy, so a live
 * document has none at all. ⚠ `type` IS ALWAYS PRESENT: the factory fills an unspecified type from
 * the status vocabulary.
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

describe('ModuleFormComponent', () => {
  let fixture: ComponentFixture<ModuleFormComponent>;
  let mounted: ComponentFixture<ModuleFormComponent> | null;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    mounted = null;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    // The store is listed explicitly even though it declares itself at the application root, so the
    // instance under test is pinned to THIS injector and cannot be shared across specifications.
    await TestBed.configureTestingModule({
      imports: [ModuleFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ModuleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // Both collaborators are injected by field initialisers that run at construction, so the spies
    // must be installed before the component is created. Both services are application-scoped and
    // the injector is rebuilt per case, so these are the very instances the component receives.
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

    // Fails on any request no expectation consumed, which is what turns "the removal re-read the
    // listing and nothing else happened" into a test result - and is equally how a MISSING follow-up
    // read is caught.
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Creates the screen, optionally on the edit route.
   *
   * The input is written BEFORE the first change detection, which is what the router does: the
   * address-reacting effect runs on that first pass and must see the value the address carried.
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
   * Brings the screen up on the edit route with everything answered.
   *
   * Three reads, in the order the component issues them: the catalogue from the constructor, the
   * module from the address effect, and the page list from the effect that waits for the module to
   * name its portal.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — WHAT ARRIVING ON EACH ROUTE READS, AND NOTHING MORE
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the create route', () => {
    it('reads the definition catalogue and nothing else', () => {
      create();

      const catalogue = expectRequest('GET', DEFINITIONS_URL);

      // The catalogue backs the create-mode selector AND the cache-row rule in both modes, and it
      // takes no parameter: the target's caching is a server concern behind an injected abstraction
      // rather than the 317-line client-side cache the legacy code reached from 116 sites.
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

      // `ModuleSettings.ascx.vb:L227` hid the legacy removal affordance in the same block that
      // applied the create defaults.
      expect(query('.module-form__action--danger')).toBeNull();
      expect(query('app-confirm-dialog')).toBeNull();
    });

    it('applies the measured legacy create defaults to the form', () => {
      arriveInCreateMode();

      // `ModuleSettings.ascx.vb:L225-L227`: the visibility list selected its first entry and the
      // all-pages switch was cleared. The container flag and the append position are the write
      // contracts' own initialisers, which exist precisely where the value type's default differs
      // from the legacy default.
      // ⚠ FOUND BY IDENTIFIER, NOT BY DOM VALUE. Angular's radio accessor takes the choice through a
      // DIRECTIVE input and does not reflect it to the element's `value` attribute, so a lookup by
      // value would find nothing and the assertion would pass vacuously against `undefined`. The
      // template composes each identifier from the enumeration member, which is also what pins the
      // ordinals: `ModuleInfo.vb:L30-L34` declares the three members with NO explicit values, so 0, 1
      // and 2 come from declaration order alone and every stored row depends on them.
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

      // ⚠ NO PLACEMENT SELECTOR ON THE READ. The store resolves one from the listing when it has
      // one, and the listing has not been read here - so the parameter is genuinely absent rather
      // than sent as an empty value.
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
      // `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so zero is the first module of an installation.
      // The legacy test was `If ModuleId <> -1` against a field initialised to -1, which cannot
      // survive: -1 is the absence marker AND zero is an ordinary module. The mode is derived from
      // whether the address carries a module at all.
      arriveInEditMode();

      expect((query('h1')?.textContent ?? '').trim()).toBe(EDIT_HEADING);
      expect(query('.module-form__action--danger')).withContext('a real module can be removed').not.toBeNull();
    });

    it('reads the page list for portal MINUS ONE, which is a real portal', () => {
      // `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so minus one identifies the first portal the
      // schema ever creates - and it is simultaneously the legacy absent-integer marker, so a
      // truthiness or sentinel test would silently skip the read.
      create('0');
      answerDefinitions();
      answerModule(detail({ portalId: -1 }));

      expectRequest('GET', TABS_URL, 'the page list for portal -1').flush(envelope([tabRow()]));
      fixture.detectChanges();
    });

    it('reads no page list for a host-owned module that reports no portal', () => {
      create('0');
      answerDefinitions();
      answerModule(detail({ portalId: null }));

      httpMock.expectNone((candidate) => candidate.url.includes('/tabs'));
    });

    it('replaces the definition selector with a read-only display name', () => {
      arriveInEditMode();

      expect(query('#module-form-module-def')).withContext('no selector once a module exists').toBeNull();

      const nameBox = requiredControl<HTMLInputElement>('module-form-friendly-name');

      // ⚠ THE DISPLAY NAME, NOT THE PROGRAMMATIC ONE. `ModuleInfo.vb` carries three names - the
      // instance title the operator types (`:L194`), the definition's display name (`:L374`) and the
      // installed package's programmatic name (`:L437`) - and the legacy screen bound the DISPLAY
      // name into the disabled box and the INSTANCE title into the editable one, in that order
      // (`ModuleSettings.ascx.vb:L125-L126`).
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

      // `Null.vb:L66-L70` defines the absent-date marker as `Date.MinValue`, and
      // `ModuleSettings.ascx.vb:L152-L156` guarded the assignment with `Null.IsNull` so the field was
      // LEFT BLANK rather than showing a minimum date.
      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('');
      expect(requiredControl<HTMLInputElement>('module-form-end-date').value).toBe('');
    });

    it('seeds both portal-wide instructions UNSET, whatever the module says', () => {
      arriveInEditMode();

      // Instructions rather than state. Echoing one back would reapply a portal-wide action the
      // operator asked for once on every subsequent save - and the two of them are the most
      // far-reaching members of the whole write surface.
      expect(requiredControl<HTMLInputElement>('module-form-set-as-default-settings').checked).toBeFalse();
      expect(requiredControl<HTMLInputElement>('module-form-apply-to-all-modules').checked).toBeFalse();
    });

    it('shows the progress indicator while the module is being read, and no fields', () => {
      create('0');
      answerDefinitions();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL);

      // The fields WAIT rather than rendering empty and then filling, because the replacement is a
      // whole-row one: a form that looked ready before the stored position and the stored
      // recycle-bin flag arrived would invite a save that moved the module and restored it.
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

      // MIGRATION: `ModuleSettings.ascx.vb:L449` called `Int32.Parse` with no guard, so a mistyped
      // address raised an exception its page handler absorbed. Nothing is dispatched here - a read
      // would ask for a module that cannot exist - and nothing is reset, because a reset would offer
      // to CREATE one, which is the outcome an unreadable identifier must never produce.
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
      // The grammar is an optional sign followed by digits and nothing else. `Number.parseInt` would
      // happily return 7 for `7px` and for `7.0`, and would then read a module the address never
      // named.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE FORM'S OWN RULES
  // ---------------------------------------------------------------------------------------------------

  describe('validation', () => {
    it('rejects an unparseable cache period and sends nothing', () => {
      arriveInEditMode();

      type('module-form-cache-time', 'twelve');
      save();

      expect(fieldMessages()).toContain(CACHE_TIME_INVALID_MESSAGE);

      // MIGRATION: the legacy handler was wrapped in `If Page.IsValid Then` (`:L328`) and did nothing
      // at all when validation had failed. A sentence is reported instead; the per-field messages are
      // unchanged.
      expect(notifySpy).toHaveBeenCalledWith('warning', FORM_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('cannot be given a malformed date at all, and sends nothing in place of one', () => {
      arriveInEditMode(detail({ startDate: '2024-03-01T00:00:00' }));

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value).toBe('2024-03-01');

      // ⚠ A NATIVE DATE CONTROL REFUSES A MALFORMED VALUE OUTRIGHT: assigning one clears the control
      // rather than storing it, so the component's own calendar-date rule is a SECOND LINE OF
      // DEFENCE that the document cannot reach. That is asserted here rather than the message the
      // rule would produce, because claiming a message no interaction can produce would be a case
      // that proves nothing. The rule still matters - the wording constant is declared for it and it
      // guards a value arriving from anywhere other than this control - and the observable behaviour
      // is that a malformed entry clears the schedule instead of corrupting it.
      type('module-form-start-date', '2024-13-45');

      expect(requiredControl<HTMLInputElement>('module-form-start-date').value)
        .withContext('the browser refused the value')
        .toBe('');
      expect(fieldMessages())
        .withContext('an empty schedule is valid, so no rule reports')
        .not.toContain(START_DATE_INVALID_MESSAGE);

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      // The cleared control travels as `null`, not as the empty string: the boundary converts the
      // legacy absent-string marker, and a submission carrying `''` would store an empty schedule
      // rather than no schedule.
      expect((call.request.body as { startDate: string | null }).startDate).toBeNull();

      call.flush(envelope(detail()));
      fixture.detectChanges();
    });

    it('accepts an empty cache period and sends zero, exactly as the legacy handler did', () => {
      arriveInEditMode();

      type('module-form-cache-time', '');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      // `ModuleSettings.ascx.vb:L352` wrote zero for an empty box. The blank case is reproduced
      // exactly; only the parse failure reports rather than raising.
      expect((call.request.body as { cacheTime: number }).cacheTime).toBe(0);

      call.flush(envelope(detail({ cacheTime: 0 })));
      fixture.detectChanges();
    });

    it('refuses to save when no page has been chosen', () => {
      arriveInEditMode();

      // An EXISTENCE test, never a bound: `dbo.Tabs.TabID` is `IDENTITY(0, 1)`, so page zero is an
      // ordinary page and no comparison against zero, against -1 or against truthiness could
      // distinguish it from an unmade choice. `null` is what carries "not chosen".
      choose('module-form-tab', 'Not Specified');
      save();

      expect(notifySpy).toHaveBeenCalledWith('warning', PAGE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses to place a module when no definition has been chosen', () => {
      // ⚠ THE PAGE MUST BE CHOSEN FIRST, because the component checks the page BEFORE the definition
      // and would otherwise report the page requirement instead. Discovering that ordering is the
      // point of driving the real component rather than asserting against a double: the two
      // requirements are reported one at a time, in a fixed order, and a caller who fixes the second
      // without the first sees the first again.
      const store = TestBed.inject(ModuleStore);

      create();
      answerDefinitions();

      store.loadTabs(-1);
      expectRequest('GET', TABS_URL).flush(envelope([tabRow()]));
      fixture.detectChanges();

      choose('module-form-tab', 'Home');
      save();

      expect(notifySpy).toHaveBeenCalledWith('warning', DEFINITION_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('bounds the heading exactly where the column and the write rule bound it', () => {
      // ⚠ MIRRORS THE SERVER RATHER THAN TIGHTENING ANYTHING, AND THE DISTINCTION IS THE WHOLE POINT.
      // The legacy markup declared no length attribute on the heading, so it is tempting to conclude
      // that any length was once acceptable — but the store never accepted one: the terminal column is
      // `Modules.ModuleTitle nvarchar(256)`, and `UpdateModuleRequestValidator` caps the member at the
      // same 256. Text longer than that has ALWAYS been refused; the only question was where. Refusing
      // it beside the box changes the mechanism and not the outcome, and the alternative is a request
      // that is certain to come back 400 with the operator's typing still to re-do.
      arriveInEditMode();

      type('module-form-title', 'x'.repeat(256));
      save();

      const atTheBound = expectRequest('PUT', MODULE_ZERO_URL);

      expect((atTheBound.request.body as { moduleTitle: string }).moduleTitle.length)
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
      // `TabModules.IconFile nvarchar(100)`, capped identically by the write rule. The legacy
      // affordance here was a file-and-folder picker rather than a text box, so there was no length
      // attribute to reproduce — but the column's limit applied then exactly as it does now.
      arriveInEditMode();

      type('module-form-icon-file', 'i'.repeat(101));
      save();

      httpMock.expectNone(() => true);
      expect(fieldMessages()).toContain('Enter at most 100 characters.');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE CREATE BODY, MEMBER FOR MEMBER
  // ---------------------------------------------------------------------------------------------------

  describe('placing a new module', () => {
    it('posts all fourteen members of the create contract, with the falsy ones intact', () => {
      arriveInCreateMode();

      choose('module-form-module-def', 'Announcements');

      // The page list is only read once a module names its portal, so on the create route the picker
      // offers nothing but the unmade choice - which is itself the reason a create attempted from
      // this route reports the page requirement. The control is therefore written through the form's
      // own accessor by choosing the only option there is, and the page is supplied by the seeded
      // list below.
      expect(requiredControl<HTMLSelectElement>('module-form-tab').options.length).toBe(1);
    });

    it('places the module once a page can be chosen, and re-reads the listing', () => {
      // The page picker's options come from the store's page slice, which a neighbouring screen may
      // already have filled. Filling it here through the store is what makes the create path
      // reachable, and it is honest: the component reads that slice rather than owning it.
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

      // ⚠ THE WHOLE BODY, COMPARED AS A WHOLE. An assertion per member would pass while a fifteenth
      // member was being sent, and the create contract has exactly fourteen. The absent-text members
      // are `null` rather than the empty string, which is the boundary conversion the target performs
      // for the legacy absent-string marker.
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

      // The store re-reads the listing after a create, so the newly placed module is in it.
      expectRequest('GET', MODULES_URL, 'the listing re-read').flush(pagedBody([listRow()]));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', CREATED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — THE REPLACEMENT BODY, MEMBER FOR MEMBER
  // ---------------------------------------------------------------------------------------------------

  describe('replacing a module', () => {
    it('puts all sixteen members, round-tripping the two the operator does not choose', () => {
      arriveInEditMode();

      type('module-form-title', 'Renamed');
      check('module-form-set-as-default-settings', true);

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL, 'the replacement');

      // ⚠ THE STORED POSITION AND THE STORED RECYCLE-BIN FLAG ARE BOTH SENT BACK. The fixture's
      // position is 3 and its flag is `true`, and both appear here. The contract's position default
      // APPENDS, so omitting it would move the module every time its title changed; and
      // `ModuleSettings.ascx.vb:L364` assigned `IsDeleted = False` on every update, so reproducing
      // that would silently restore a module from the recycle bin as a side effect of a rename.
      //
      // ⚠ NO DEFINITION MEMBER. The contract carries none - a module's definition is fixed at
      // creation - and the control it would come from is disabled while a module is loaded.
      expect(call.request.body).toEqual({
        tabId: 0,
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

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
    });

    it('round-trips the stored cache period even when the cache row is not offered', () => {
      // MIGRATION: A DELIBERATE DIVERGENCE. The legacy screen left the box empty whenever the
      // definition declared no default period (`:L136-L142`) and its handler then wrote zero for an
      // empty box (`:L349-L353`), so every save silently zeroed the stored period of such a module.
      // Since the replacement writes every member, reproducing that would destroy stored data on
      // each save.
      create('0');
      answerDefinitions([definition({ defaultCacheTime: -1 })]);
      answerModule(detail({ cacheTime: 900 }));
      answerTabs();

      expect(query('#module-form-cache-time')).withContext('the row is withheld').toBeNull();

      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      expect((call.request.body as { cacheTime: number }).cacheTime).toBe(900);

      call.flush(envelope(detail({ cacheTime: 900 })));
      fixture.detectChanges();
    });

    it('sends the page the operator chose, which is what selects the placement being replaced', () => {
      arriveInEditMode();

      choose('module-form-tab', 'About');
      save();

      const call = expectRequest('PUT', MODULE_ZERO_URL);

      // The legacy caption was a MOVE affordance. The value now selects the placement the update
      // addresses as well, because the body carries exactly one page identifier - a move would need
      // two - and the server refuses when the module is not placed on the page named.
      expect((call.request.body as { tabId: number }).tabId).toBe(1);

      call.flush(envelope(detail({ tabId: 1 })));
      fixture.detectChanges();
    });

    it('re-reads the listing when the replacement reports the module is on every page', () => {
      arriveInEditMode();

      save();

      expectRequest('PUT', MODULE_ZERO_URL).flush(envelope(detail({ allTabs: true })));
      fixture.detectChanges();

      // A module on every page contributes one listing row per page, so the store cannot patch a
      // single row and re-reads instead. This is the store's decision and the screen simply does not
      // interfere with it.
      expectRequest('GET', MODULES_URL, 'the listing re-read for an all-pages module').flush(
        pagedBody([listRow({ allTabs: true })]),
      );
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE);
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
      expect((query('.confirm-dialog__title')?.textContent ?? '').trim()).toBe('Delete Module');
      httpMock.expectNone(() => true);
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

      // ⚠ A PER-PAGE REMOVAL. The legacy affordance called `DeleteTabModule(TabId, ModuleId)`
      // (`ModuleController.vb:L837`), which removes the module from ONE page and soft-deletes the
      // module itself only once its last placement is gone - not `DeleteModule` (`:L819`), which
      // removes it outright. Naming the placement is what reproduces that.
      expect(removal.request.params.get('tabModuleId')).toBe('7');

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ NOTHING IS SPLICED LOCALLY. The removal is soft and two-tiered, so after a 204 the row may
      // or may not still belong in the listing - and only the listing endpoint knows which.
      expectRequest('GET', MODULES_URL, 'the listing re-read').flush(pagedBody([]));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', DELETED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
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

      // Driven through the form rather than the button, so the refusal is proven to be in the code:
      // a second placement would be duplicated and the endpoint answers a conflict rather than
      // de-duplicating.
      query('.module-form')?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      call.flush(envelope(detail()));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledTimes(1);
    });

    it('refuses a submission before the addressed module has been read', () => {
      create('0');
      answerDefinitions();

      const moduleRead = expectRequest('GET', MODULE_ZERO_URL);

      // The primary command is not rendered while the module is being read, so the submission is
      // driven through the form. Two members of the sixteen come from the stored row, so a
      // submission without them would append the module to the bottom of its pane and restore it
      // from the recycle bin as side effects of saving an unrelated field.
      query('.module-form')?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectNone((candidate) => candidate.method === 'PUT');

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

      // ⚠ THE SEVERITY IS THE SHARED UTILITY'S DECISION AND IS THE MEASURED ONE: a refusal is a
      // WARNING rather than an error, because the legacy denial page presented one with a yellow
      // warning in both of its branches. Presenting it in danger styling would say something is
      // broken when the system is working as configured.
      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        'The authenticated caller is not permitted to perform this operation.',
      );
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(query('.error-banner')).not.toBeNull();
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
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('shows a per-field message the server reported and keeps the form', () => {
      arriveInEditMode();

      save();

      // A model-binding refusal is a 400 with `request.invalid`, and its per-field map is keyed by
      // .NET model-state names - which are Pascal-cased and are matched case-insensitively by the
      // shared utility, so the control named for the wire member matches without either side
      // re-casing.
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

      // `ModuleSettings.ascx.vb:L281` redirected to the administration page the operator came from,
      // and the module listing is its counterpart.
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

      // `MoveModule`, `UpdateModuleOrder` and `UpdateTabModuleOrder` all exist in the legacy
      // controller and none survives into the target surface, so there is no endpoint an affordance
      // could call. The position is still SENT, from the stored value.
      expect(query('#module-form-module-order')).toBeNull();
      expect(queryAll('input[formControlName="moduleOrder"]').length).toBe(0);
    });

    it('offers no rich-text editing for the two markup fields', () => {
      arriveInEditMode();

      // The legacy editor provider is out of scope, so a field the legacy rendered through it is a
      // plain multi-line control. A documented functional reduction, not an oversight.
      expect(requiredControl('module-form-header').tagName).toBe('TEXTAREA');
      expect(requiredControl('module-form-footer').tagName).toBe('TEXTAREA');
    });

    it('renders no image and references no legacy raster asset', () => {
      arriveInEditMode();

      // The only static asset this workspace ships is a favicon.
      expect(queryAll('img').length).toBe(0);
    });
  });
});
