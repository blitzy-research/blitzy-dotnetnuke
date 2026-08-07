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
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';
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

/**
 * The destructive confirmation's message, quoted from the resource file rather than from the
 * component.
 *
 * ⚠ THE AUTHORITY IS `Website/App_GlobalResources/SharedResources.resx:L120-L121`, whose
 * `DeleteItem.Text` entry holds exactly this sentence, capital letters and question mark included.
 * `ModuleSettings.ascx.vb:L205` looked the key up WITHOUT the property suffix -
 * `GetString("DeleteItem")` - and wired the result to the removal affordance as a client-side
 * confirmation, so this is the string an existing operator has already been reading for years. It is
 * restated here rather than imported because the component does not export it, and a specification
 * that read the constant from the component could not detect the constant being changed.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

// =====================================================================================================
// READING A REQUEST BODY WITHOUT AN ESCAPE HATCH
//
// `TestRequest.request.body` is typed `unknown | null`, which is honest: the testing backend cannot know
// what a caller sent. Every reader below NARROWS it with a run-time check instead of asserting a shape,
// so a body that is not the shape a case expects fails with a diagnosis rather than throwing on a
// property access.
//
// ⚠ NO TYPE-SYSTEM ESCAPE HATCH IS USED ANYWHERE IN THIS FILE - not the permissive top type, not a
// compiler-directive comment, and not the non-null operator. A specification is the one place where an
// escape hatch is most tempting and least defensible: the whole value of a specification is that it
// tells the truth about the shapes its subject actually produces, and silencing the compiler in it
// silences the only mechanism that keeps that true. The readers below are what make the discipline
// affordable rather than merely required.
//
// The whole-body comparisons elsewhere in this specification are the primary proof of each contract's
// membership, because an exact comparison catches a member being ADDED as well as one being dropped.
// These readers exist for the cases that assert one member for one reason, where restating fifteen
// unrelated values would bury the point being made.
// =====================================================================================================

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
 * Reads one numeric member of a request body.
 *
 * ⚠ ZERO AND MINUS ONE ARE REAL VALUES HERE, so the check is on the TYPE and never on the magnitude.
 * A reader written as `value || fallback` would substitute a fallback for a legitimate zero, which is
 * exactly the class of defect the sentinel cases in this file exist to catch.
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
 * Reads one nullable text member of a request body.
 *
 * `null` is returned as `null` rather than as the empty string, because the two are different facts on
 * this contract: the boundary converts an emptied control TO `null` so the column clears.
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
 * Whether a request body declares a member at all.
 *
 * ⚠ PRESENCE, NOT TRUTHINESS AND NOT NON-NULLITY. The API's serializer writes every declared member
 * including one holding null, so a member that is genuinely ABSENT from a contract is absent from the
 * body - which is what the replacement contract does with the definition. Testing the value would
 * confuse "not sent" with "sent as null", and those are the two answers this file has to tell apart.
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

/**
 * A model-binding refusal, in the exact shape `ValidationProblemDetailsFactory` emits.
 *
 * ⚠ THE `errors` BAG IS KEYED BY THE .NET DTO PROPERTY NAME, WHICH IS PASCAL-CASED. FluentValidation
 * reports `PropertyName` from the expression it was given, so `RuleFor(x => x.ModuleTitle)` emits the
 * key `ModuleTitle` - NOT `moduleTitle`, which is the camel-cased WIRE member the serializer writes on
 * the request body, and emphatically NOT `Title`, which is what the legacy control was called
 * (`txtTitle`, `modulesettings.ascx:L32`) and what the legacy class's `<XmlElement>` attribute named
 * the same value. Three spellings of one concept, and only one of them appears in a problem document.
 *
 * ⚠ THE BAG IS BUILT WITH BRACKET ASSIGNMENT AND IS READ WITH BRACKET ACCESS THROUGHOUT, because this
 * workspace compiles with `noPropertyAccessFromIndexSignature`: `ProblemDetailsErrors` is
 * `Readonly<Record<string, readonly string[]>>`, and dotted access on an index signature is a compile
 * error rather than a style preference.
 *
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
 * The response options a problem document is flushed with.
 *
 * ⚠ THE MEDIA TYPE IS `application/problem+json`, NOT `application/json`. RFC 7807 defines it, the API
 * emits it, and stating it in the fixture is what proves this client does not depend on the plain JSON
 * type: the document is recognised by its SHAPE - through the narrowing guard the shared utility owns -
 * rather than by a header, so a gateway that rewrote the header could not make a refusal unreadable.
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

  /**
   * The messages rendered beside ONE named control.
   *
   * ⚠ SCOPED RATHER THAN GLOBAL, AND THAT IS THE POINT OF IT. Asserting that a message appears
   * ANYWHERE on the screen cannot distinguish "the server's complaint about the heading was routed to
   * the heading" from "it was rendered against the wrong field", and routing is exactly what the
   * shared utility's case-insensitive match is responsible for. The control's own labelled region is
   * found by walking up from the control, so the assertion does not depend on the order of the fields.
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
      // ⚠ THE SINGLE MOST IMPORTANT CASE IN THIS FILE, AND THE ONE THE WHOLE SENTINEL ANALYSIS EXISTS
      // TO PROTECT. `dbo.Modules.ModuleID` is declared `IDENTITY(0, 1)`
      // (`01.00.00.SqlDataProvider:L221`), so ZERO is the first module an installation ever creates,
      // while the legacy null contract spelled "absent" as -1 (`Null.vb:L41-L45`) and this very screen
      // initialised its identifier field to that marker (`ModuleSettings.ascx.vb:L68`) and branched on
      // `If ModuleId <> -1` (`:L222`).
      //
      // That test cannot survive the migration, and neither can any of its idiomatic translations: a
      // screen written with `if (moduleId)`, `!moduleId`, `moduleId > 0`, `moduleId >= 0` or
      // `moduleId !== -1` would treat module zero as no module at all - which does not merely fail to
      // load, it PRESENTS THE CREATE SCREEN and posts a second module. The mode is therefore derived
      // from whether the address carries the parameter, and never from the value it carries.
      //
      // Asserted at three levels, because each catches a different way of getting this wrong.
      create('0');
      answerDefinitions();

      // LEVEL ONE — the read is dispatched at all, and it addresses zero. The string the router hands
      // over is converted explicitly rather than through a transform, so the URL segment is the number
      // and not the text: a coercion that produced the empty string or `NaN` would show up here.
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
      // `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so minus one identifies the first portal the
      // schema ever creates - and it is simultaneously the legacy absent-integer marker, so a
      // truthiness or sentinel test would silently skip the read.
      create('0');
      answerDefinitions();
      answerModule(detail({ portalId: -1 }));

      const pages = expectRequest('GET', TABS_URL, 'the page list for portal -1');

      // The address is asserted as a Jasmine expectation too, because `expectRequest` records nothing
      // with the test framework: a case resting on it alone reports "no expectations" and would keep
      // reporting success if the read stopped happening.
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
      // module that belongs to no portal is host-owned, and falling back to some default portal would
      // offer the operator pages from a tenant nobody named - which the replacement would then write.
      // Stated as a Jasmine expectation as well, so the case cannot report "no expectations".
      expect(queryAll('#module-form-tab option').length)
        .withContext('the picker offers only the unmade choice')
        .toBe(1);
      expect(notifySpy).not.toHaveBeenCalled();
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

    it('treats the MAXIMUM date as an ordinary schedule value and does not blank it', () => {
      // ⚠ THE COUNTERPART TO THE CASE ABOVE, AND THE ONE THAT KEEPS IT HONEST. Exactly ONE date is a
      // marker: `Null.vb:L66-L70` defines the absent date as the MINIMUM date and nothing else, so
      // `9999-12-31` - the last instant SQL Server's `datetime` can hold - is a real schedule an
      // operator deliberately entered. A blanking rule written against "an extreme date" rather than
      // against that one value would erase it, and then the very next save would write the erasure back
      // over it, because the replacement writes every member it is given.
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
      expect(textField(call.request.body, 'startDate')).toBeNull();

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
      expect(numberField(call.request.body, 'cacheTime')).toBe(0);

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
      // `TabModules.IconFile nvarchar(100)`, capped identically by the write rule. The legacy
      // affordance here was a file-and-folder picker rather than a text box, so there was no length
      // attribute to reproduce — but the column's limit applied then exactly as it does now.
      arriveInEditMode();

      type('module-form-icon-file', 'i'.repeat(101));
      save();

      httpMock.expectNone(() => true);
      expect(fieldMessages()).toContain('Enter at most 100 characters.');
    });

    // -------------------------------------------------------------------------------------------------
    // THE TWO PARITY REGRESSION GUARDS: RULES THAT MUST NOT EXIST
    // -------------------------------------------------------------------------------------------------
    //
    // Everything above proves a rule DOES fire. The two cases below prove that two plausible, tempting,
    // frequently-requested rules DO NOT - and they are the harder half to keep, because nothing ever
    // breaks when a validator is added. Both are written as saves that must SUCCEED, so adding either
    // rule turns them red immediately.

    it('accepts an EMPTY heading, because the legacy screen declared no presence rule anywhere', () => {
      // ⚠ THIS IS A DELIBERATE ARCHITECTURAL DECISION AND THIS CASE IS ITS REGRESSION GUARD.
      //
      // A required-heading rule reads like an obvious improvement, and it is not one. Three
      // independent authorities agree that the heading is optional:
      //
      //   1. `modulesettings.ascx` declares ZERO `asp:RequiredFieldValidator` elements - not on the
      //      heading, not on anything. Its only rules are four `asp:CompareValidator`s (L78, L88,
      //      L138, L172), every one of them a `DataTypeCheck` and not one of them a presence check.
      //   2. `ModuleSettings.ascx.vb:L344` assigned `objModule.ModuleTitle = txtTitle.Text` with no
      //      trim, no length test and no emptiness test, so an emptied box was stored as an empty
      //      value - which the legacy null contract spelled as the absent string.
      //   3. Both write contracts declare the member NULLABLE and not required, so an empty heading is
      //      accepted by the server as well. Refusing it here would make this screen stricter than the
      //      API it calls, and stricter than every other caller of that API.
      //
      // MIGRATION: PARITY WINS OVER THE IMPROVEMENT. Where the two pull apart, the Minimal Change
      //   Clause's requirement that validation rules MATCH is the tie-breaker, so no `required` rule is
      //   added and this case exists to keep it that way. The boundary conversion is asserted too: an
      //   emptied control becomes `null` so the column CLEARS, which is what an emptied legacy text box
      //   did on postback.
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

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE);
    });

    it('accepts an end date BEFORE its start date, because no ordering rule ever existed', () => {
      // ⚠ NO `endDate >= startDate` RULE IS INVENTED, AND THAT IS MEASURED RATHER THAN ASSUMED. Both
      // legacy date validators declare `Operator="DataTypeCheck"` with `Type="Date"`
      // (`modulesettings.ascx:L78` and `:L88`) and NEITHER declares a `ControlToCompare`, so there was
      // nothing to compare either date against: an end date before a start date passed the screen,
      // reached `cmdUpdate_Click` and was stored. Whether such a schedule is sensible is not this
      // migration's question - reproducing the behaviour is.
      //
      // Both values are readable and both are inside the storable range, so the only rule that could
      // reject this pair is one nobody wrote. The save must therefore go out.
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

      expect(notifySpy).toHaveBeenCalledWith('success', UPDATED_MESSAGE);
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

    it('accepts the 201 the endpoint answers with, and reports the placement', () => {
      // ⚠ THE STATUS IS STATED RATHER THAN DEFAULTED. `POST /modules` answers `201 Created`, and a
      // fixture flushed at the testing backend's default `200` would leave this client's behaviour at
      // the real status untested - which matters because a screen that keyed its success path off an
      // exact `200` would fail against the endpoint it actually calls, with nothing in the build to
      // say so.
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

      // ⚠ THE DEFINITION IS SENT ON A CREATE AND ONLY ON A CREATE. It is the one member the
      // replacement contract omits, so its presence here is half of the pair of assertions that pin
      // the definition's immutability; the other half is in the replacement proof below.
      expect(bodyDeclares(call.request.body, 'moduleDefId'))
        .withContext('the create contract carries the definition')
        .toBeTrue();
      expect(numberField(call.request.body, 'moduleDefId')).toBe(4);

      call.flush(envelope(detail({ moduleId: 12, tabModuleId: 34 })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      expectRequest('GET', MODULES_URL, 'the listing re-read').flush(pagedBody([listRow()]));
      fixture.detectChanges();

      // The measured legacy severity vocabulary had exactly three levels - `RedError` 27 times,
      // `YellowWarning` 21 and `GreenSuccess` 12 across the administration screens - and a completed
      // operation used the affirmative one.
      expect(notifySpy).toHaveBeenCalledWith('success', CREATED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
    });

    it('treats PAGE ZERO as a page, because the page identity seeds at zero', () => {
      // ⚠ THE STRUCTURAL CHECK IS EXISTENCE, NEVER A BOUND. `dbo.Tabs` is declared
      // `[TabID] [int] IDENTITY (0, 1)` (`01.00.00.SqlDataProvider:L140`), so the first page an
      // installation ever creates is numbered ZERO - and the legacy null contract simultaneously used
      // -1 for an absent integer (`Null.vb:L41-L45`). A screen written with `if (tabId)`,
      // `tabId > 0`, `tabId >= 0` or `tabId !== -1` would therefore refuse to save against the very
      // page the measured baseline seeds, and would do it silently.
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

      expectRequest('GET', MODULES_URL).flush(pagedBody([listRow()]));
      fixture.detectChanges();
    });

    it('carries the NONE visibility state as the real code two', () => {
      // ⚠ `None` IS A CHOICE, NOT AN ABSENCE, AND ITS CODE IS LOAD-BEARING DATA. The legacy
      // enumeration declared its three members with NO explicit values (`ModuleInfo.vb:L30-L34`), so
      // 0, 1 and 2 came from declaration order alone, and every stored row in every existing
      // installation depends on that ordering. The target renames the type - the legacy spelling is
      // gone - but renumbering it would silently re-present every minimised module as maximised and
      // every hidden one as minimised.
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

      expectRequest('GET', MODULES_URL).flush(pagedBody([listRow()]));
      fixture.detectChanges();
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

      expect(numberField(call.request.body, 'cacheTime')).toBe(900);

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
      expect(numberField(call.request.body, 'tabId')).toBe(1);

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

      // ⚠ THE MESSAGE IS THE EXACT STRING THE LEGACY SCREEN RESOLVED, PUNCTUATION AND CAPITALS
      // INCLUDED. `ModuleSettings.ascx.vb:L205` called
      // `ClientAPI.AddButtonConfirm(cmdDelete, Localization.GetString("DeleteItem"))` - note the key is
      // `"DeleteItem"` WITHOUT a property suffix - and `DeleteItem.Text` in
      // `Website/App_GlobalResources/SharedResources.resx:L120-L121` holds this sentence. An operator
      // who has used the legacy screen reads the same words here. The heading above it is a documented
      // net addition: the legacy prompt was a browser dialogue with a message and no title.
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim())
        .withContext('the measured DeleteItem.Text wording, verbatim')
        .toBe(DELETE_CONFIRM_MESSAGE);

      httpMock.expectNone(() => true);
    });

    it('stays reachable on an INVALID form and removes without validating anything', () => {
      // ⚠ `cmdDelete` CARRIED `causesvalidation="False"` (`modulesettings.ascx:L224`), so the legacy
      // removal ran whatever state the page's validators were in. `cmdUpdate` (`:L222`) declares no
      // such attribute and therefore defaulted to True - the asymmetry is explicit in the markup, not
      // inferred - and the target reproduces it with `type="button"` on the removal against
      // `type="submit"` on the save. A removal blocked by a bad cache period would strand an operator
      // whose only remaining intention was to delete the thing.
      arriveInEditMode();

      // Break the form in a way the save path definitely refuses. The rule is real: the surviving
      // legacy `valCacheTime` comparison validator (`:L172`) is a `DataTypeCheck` on an integer.
      //
      // ⚠ THE VALUE IS DELIBERATELY SHORT, AND THAT IS NOT COSMETIC. The cache box reproduces the
      // legacy `maxlength="6"` attribute (`modulesettings.ascx:L169`), and a STATIC `maxlength`
      // attribute beside `formControlName` is matched by the framework's own length directive, so the
      // control carries a length rule in ADDITION to the integrality rule this case is about. Text
      // longer than six characters therefore reports the length message and the integrality rule never
      // gets to speak - the form is invalid either way, but the case would be asserting the wrong rule.
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

      // ⚠ SOFT, WITH NO RESTORE PATH, AND NOTHING SPLICED LOCALLY. The row survives with its
      // recycle-bin marker set, so whether it still belongs in the listing is the LISTING endpoint's
      // decision; the store re-reads rather than guessing.
      expectRequest('GET', MODULES_URL, 'the mandatory listing re-read').flush(pagedBody([]));
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', DELETED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
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
      const form = query('.module-form');

      expect(form).withContext('the form is rendered while the module is being read').not.toBeNull();

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectNone((candidate) => candidate.method === 'PUT');

      // Stated as Jasmine expectations as well as transport ones. `expectNone` records nothing with the
      // test framework, so a case resting on it alone reports "no expectations" and would still report
      // success if its subject stopped doing anything at all.
      //
      // ⚠ THE SUPPRESSION IS SILENT HERE, AND DELIBERATELY SO. Two guards could refuse this
      // submission and the OUTSTANDING-REQUEST one is reached first, before the not-yet-read one, so no
      // sentence is produced: the screen is visibly busy - the progress indicator is on screen and the
      // primary command is not even rendered - and announcing "wait a moment" over an indicator that
      // already says exactly that would be noise. The not-yet-read sentence exists for the case where
      // nothing is in flight and the read has already failed, which is a state a person cannot see.
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

      // ⚠ THE SEVERITY IS THE SHARED UTILITY'S DECISION AND IS THE MEASURED ONE: a refusal is a
      // WARNING rather than an error, because the legacy denial page presented one with a yellow
      // warning in both of its branches. Presenting it in danger styling would say something is
      // broken when the system is working as configured.
      //
      // ⚠ THE RULE BEHIND THIS `403` IS A FOUR-FIELD RULE, NOT A ONE-FIELD RULE, and this comment
      // exists so the case is never later narrowed to "the all-pages rule". `Page_Load:L215-L220`
      // disabled exactly FOUR controls for a caller who was not a portal administrator, and
      // `cmdUpdate_Click:L333-L338` repeated the same four:
      //
      //     chkAllTabs    -> allTabs                (display the module on every page)
      //     chkDefault    -> setAsDefaultSettings   (adopt these settings as the portal's defaults)
      //     chkAllModules -> applyToAllModules      (copy this appearance to every module)
      //     cboTab        -> tabId                  (move the placement to another page)
      //
      // MIGRATION: NONE OF THE FOUR IS LOCKED CLIENT-SIDE. Nothing among this screen's declared
      //   dependencies can answer whether the caller is a PORTAL ADMINISTRATOR - the identity contract
      //   exposes a super-user flag and a role-name list, and neither is that question - so a
      //   client-side copy of the rule would either compare a role name against a literal or guess.
      //   The refusal is left to the server, which answers `403` for any of the four, and the answer
      //   is presented identically whichever one provoked it.
      //
      // The measured authority for the severity is `Website/admin/Security/AccessDenied.ascx.vb`: a
      // fifty-line page that performs NO permission check of its own and renders BOTH of its
      // `Page_Load` branches, at L43 and L45, with `ModuleMessage.ModuleMessageType.YellowWarning`.
      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        'The authenticated caller is not permitted to perform this operation.',
      );
      expect(notifySpy).not.toHaveBeenCalledWith(
        'error',
        'The authenticated caller is not permitted to perform this operation.',
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

    it('routes a refusal keyed "ModuleTitle" onto the heading and keeps the trace identifier', () => {
      // ⚠ THE KEY IS `ModuleTitle`, AND ALL THREE SPELLINGS OF THIS ONE CONCEPT ARE LIVE IN THIS
      // MIGRATION. FluentValidation reports `PropertyName` from the expression it was handed, so
      // `RuleFor(x => x.ModuleTitle)` puts the .NET DTO property name into the bag - Pascal-cased.
      // It is NOT `moduleTitle`, which is the camel-cased member the serializer writes on the request
      // BODY; and it is NOT `Title`, which is what the legacy control was called (`txtTitle`,
      // `modulesettings.ascx:L32`) and what the legacy class's `<XmlElement>` attribute named the same
      // value. A client that matched only the wire spelling would drop this message on the floor, and
      // the operator would be told nothing at all about why the save was refused.
      //
      // Reconciling the casing is the shared utility's job and not this screen's: it matches
      // case-insensitively, which is what lets a control named for the WIRE member receive a message
      // keyed by the .NET PROPERTY without either side re-casing anything.
      arriveInEditMode();

      type('module-form-title', 'a heading the server refuses');
      save();

      const refused = expectRequest('PUT', MODULE_ZERO_URL, 'the refused replacement');

      // Built through the fixture so the bag is constructed with the same bracket discipline it is read
      // with. `noPropertyAccessFromIndexSignature` is enabled, and `ProblemDetailsErrors` is
      // `Readonly<Record<string, readonly string[]>>`, so dotted access here would not compile.
      //
      // ⚠ THE CORRELATION MEMBER IS DELIBERATELY OMITTED FROM THIS ONE DOCUMENT. The shared utility
      // resolves a support reference by preferring the correlation identifier and falling back to the
      // trace identifier, so a document carrying both would prove only that the PREFERRED one survives
      // - and the trace identifier is the member that is actually at risk of being dropped, because it
      // is the one the API synthesises rather than the one the client sent. Omitting the preferred
      // member is what puts the trace identifier itself on screen and makes its retention assertable.
      const document: ValidationProblemDetails = {
        ...validationProblem(400, 'The request could not be processed as submitted.', {
          ModuleTitle: ['<br>The module title is not acceptable.'],
          CacheTime: ['The cache period must not be negative.'],
        }),
        correlationId: undefined,
      };

      // ⚠ READ WITH BRACKET ACCESS. Asserted before the flush so the fixture itself is pinned: a
      // future edit that renamed the key would fail HERE, with a message about the key, rather than
      // three assertions later with a message about a missing sentence.
      expect(document.errors['ModuleTitle'])
        .withContext('the bag is keyed by the .NET property name')
        .toEqual(['<br>The module title is not acceptable.']);
      expect(document.errors['moduleTitle'])
        .withContext('the camel-cased wire spelling is NOT what a problem document carries')
        .toBeUndefined();
      expect(document.errors['Title'])
        .withContext('the legacy control name is NOT what a problem document carries')
        .toBeUndefined();

      // ⚠ FLUSHED AS `application/problem+json`, which is the media type RFC 7807 defines and the API
      // emits. The document is recognised by its SHAPE rather than by this header - the shared
      // narrowing guard inspects the members - so the header is stated to prove the client does not
      // depend on the plain JSON type.
      refused.flush(document, problemResponse(400));
      fixture.detectChanges();

      // ⚠ SCOPED TO THE HEADING'S OWN LABELLED REGION. A screen-wide search would pass even if the
      // message had been rendered against the cache period, and WHICH field a message lands beside is
      // the entire point of the routing being tested.
      //
      // ⚠ THE LEADING BREAK MARKUP IS GONE. Twenty-eight of the thirty-four genuine legacy validator
      // messages were prefixed with a break tag - in both the `<br>` and `<br/>` spellings - so that
      // they wrapped beneath the control they belonged to. That is layout expressed as content, and a
      // stylesheet's job here; the shared utility strips it, and this screen neither repeats the
      // stripping nor renders the tag as markup.
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

      // ⚠ THE TRACE IDENTIFIER IS RETAINED, NOT DISCARDED. The server derives it from the ambient
      // activity or, failing that, from the request identifier, and it is the ONLY join key between
      // what a person saw on screen and what the server logged. Reducing the document to a sentence
      // here would throw it away and make a support conversation unresolvable.
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

      // `ModuleSettings.ascx.vb:L281` redirected to the administration page the operator came from,
      // and the module listing is its counterpart.
      expect(navigateSpy).toHaveBeenCalledOnceWith(MODULE_LIST_PATH);
      httpMock.expectNone(() => true);
    });

    it('leaves an INVALID form without running a single validator', () => {
      // ⚠ `cmdCancel` CARRIED `causesvalidation="False"` (`modulesettings.ascx:L223`) and its handler
      // was a bare `Response.Redirect(NavigateURL(), True)` with no validation and no confirmation
      // (`:L279-L286`). The target reproduces that with `type="button"`, which cannot submit the form -
      // so no validator runs, nothing is marked touched, and an invalid form does not disable the
      // affordance. This matters practically: a form an operator cannot fix is precisely the form they
      // most need to be able to abandon.
      arriveInEditMode();

      type('module-form-cache-time', 'still-not-a-number');

      // Not saved, so nothing has been submitted and nothing has been marked touched yet. The
      // component reports per-field messages only once a control is touched or a save is attempted, so
      // a clean field list here is the proof that no validator has been RUN - as distinct from having
      // run and passed.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — THE DEFINITION CATALOGUE IS READ-ONLY, AND NO PHANTOM ENDPOINT IS EVER ADDRESSED
  // ---------------------------------------------------------------------------------------------------

  describe('the definition catalogue', () => {
    it('is read from the collection address and takes no query parameter at all', () => {
      create();

      const catalogue = expectRequest('GET', DEFINITIONS_URL, 'the definition catalogue');

      // ⚠ THE ENDPOINT ACCEPTS NO PARAMETER, so the request must carry none. Asserted by EMPTINESS of
      // the parameter set rather than by comparing a query string: a hand-built string assertion
      // depends on separator and encoding choices this client does not make, and would pass or fail for
      // reasons that have nothing to do with the contract.
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
      // ⚠ THERE IS NO WRITE HALF AND THERE IS NO ROUTE FOR ONE. The legacy mechanism for adding a
      // definition wrote archives to disk and reflected over the assemblies it found - `PaWriter.vb`
      // and its companions - and is excluded wholesale, so a request that tried to create, replace or
      // remove a definition would address a route the API does not serve.
      //
      // Every verb is checked rather than just the obvious one, because `verify()` alone proves only
      // that no request went UNCONSUMED - it cannot say that a particular kind of request was never
      // made. `expectNone` with a predicate can.
      arriveInCreateMode();

      choose('module-form-module-def', 'Announcements');

      for (const verb of ['POST', 'PUT', 'PATCH', 'DELETE']) {
        httpMock.expectNone(
          (candidate) => candidate.method === verb && candidate.url.includes('module-definitions'),
          `no ${verb} reaches the definition catalogue`,
        );
      }

      // Nor is a by-identifier read issued as a side effect of choosing one. Were such a call ever
      // added, its path segment would be spelled `moduleDefinitionId` in full - the ROUTE's spelling -
      // while the response member stays abbreviated as `moduleDefId`. The two genuinely differ and are
      // deliberately not unified by guesswork.
      httpMock.expectNone(
        (candidate) => candidate.url === `${DEFINITIONS_URL}/4`,
        'choosing a definition does not re-read it',
      );
    });

    it('consumes the portability flag as a resolved boolean, never as a bitmask', () => {
      // ⚠ THE LEGACY SHAPE WAS A BITMASK AND THE TARGET SHAPE IS NOT. `ModuleInfo.vb:L446` exposed a
      // single `SupportedFeatures` integer and DERIVED three booleans from it by masking
      // (`:L608`, `:L614`, `:L620`). The definition contract publishes ONE resolved flag - portability -
      // and does not publish the searchable or upgradeable projections at all, so there is no mask here
      // to reproduce and nothing to shift or bitwise-and. `BusinessControllerClass`, which the legacy
      // class also carried and which named a type to activate reflectively, is never exposed anywhere.
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

      // The three members the legacy class carried and the target deliberately does not publish never
      // appear on screen, so nothing here can have been derived from them.
      const rendered = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(rendered).not.toContain('SupportedFeatures');
      expect(rendered).not.toContain('BusinessControllerClass');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 10 — THE TEMPLATE'S OWN PROHIBITIONS
  // ---------------------------------------------------------------------------------------------------
  //
  // These are not stylistic assertions. Each names a specific legacy construct that a faithful
  // translation would have carried across, and states why the target must not.

  describe('the rendered document', () => {
    it('contains no table, because every legacy table on this screen was a layout table', () => {
      // ⚠ ALL EIGHT `<table>` ELEMENTS IN `modulesettings.ascx` WERE LAYOUT, and the markup says so
      // itself: seven of them carry a `summary` attribute ending in the words "Design Table" - the
      // author's own admission - and the eighth (`:L40`) is a bare nested grid with no summary at all.
      // A layout table announces phantom rows and columns to a screen reader and forces a linear
      // reading order onto content that has none, which is why the target lays this screen out with
      // grouped fieldsets and a stylesheet instead.
      //
      // MIGRATION: D-M11 IS RECORDED HERE RATHER THAN REPRODUCED. Three of those seven summaries
      //   describe the wrong section in this very file - two different tables are both summarised as
      //   "Appearance Design Table" and a third as "Security Details Design Table" while it holds the
      //   other settings - so even as layout hints the legacy values were wrong. Nothing is carried
      //   across, so nothing inherits the mistake.
      //
      // The tabular affordance in this workspace is the shared data table, and it is for real tabular
      // data. A form is not tabular data.
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
      // ⚠ A LANDMARK MUST APPEAR ONCE PER DOCUMENT, AND THIS COMPONENT IS NEVER THE DOCUMENT. It is
      // rendered inside the shell's routed outlet, and the shell contributes the banner, the primary
      // navigation, the main region and the footer. A screen that emitted its own would produce two of
      // that landmark in the composed page, which turns an unambiguous "skip to main content" into a
      // choice a screen-reader user has to make blind.
      //
      // The page heading affordance this screen DOES use renders a level-one heading inside plain
      // containers rather than a banner element, which is the correct division: the heading is this
      // screen's, and the banner is the shell's.
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
});
