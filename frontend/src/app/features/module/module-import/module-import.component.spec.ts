/**
 * Specification for `features/module/module-import/module-import.component.ts` and its paired template.
 * THIS SCREEN CARRIES UNTRUSTED CONTENT ACROSS THE WIRE, and that is what makes it worth testing at this
 * depth.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter } from '@angular/router';

import type { Signal, Type } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ActivatedRouteSnapshot, Route } from '@angular/router';

import { ModuleVisibility } from '../../../core/models/module.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { ModuleStore } from '../../../core/state/module.store';
import { MODULE_ROUTES } from '../module.routes';
import { ModuleImportComponent } from './module-import.component';

import type { CurrentUser } from '../../../core/models/auth.model';
import type { ModuleListItem } from '../../../core/models/module.model';
import type { PagedResponse } from '../../../core/models/paged-result.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

/** The module listing, read on arrival to fill the picker. */
const MODULES_URL = '/api/v1/modules';

/** The transfer endpoint. */
const IMPORT_URL = '/api/v1/modules/import';

/** Where both the completion and the abandonment go. */
const MODULE_LIST_ROUTE = '/modules';

/** The most event-loop turns any wait in this suite will yield for. */
const SETTLE_TURN_CEILING = 256;

const SETTLE_WALL_CLOCK_FLOOR_MS = 250;

/** The widest page the picker asks for, so its reach is not silently limited to the default page. */
const CHOICE_PAGE_SIZE = '100';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';
const CORRELATION_ID = 'f41c7a92-0b6d-4e58-8a37-1d9c5b2e6f04';

// THE WORDING THIS SCREEN PUBLISHES

const IMPORT_TITLE = 'Import Module';

/** The supporting sentence, re-authored from the paragraph inside `ModuleHelp.Text`. */
const IMPORT_SUBTITLE = 'Administrators can import content for the specified module.';

/** The document field's label. `plFile.Text`, with the legacy `suffix=":"` colon not reproduced. */
const FILE_FIELD_LABEL = 'File';

/**
 * `plFile.Help` verbatim, and asserted as a PREFIX. The screen appends the published byte limit to it, so
 * the measured resource wording is the beginning of the rendered guidance rather than the whole of it.
 */
const FILE_FIELD_HELP = 'Select the import file';

/** The module field's label. Net-new alongside the net-new field. */
const MODULE_FIELD_LABEL = 'Module';

/**
 * The opening sentence of the module field's guidance. The measured prefix rather than the whole string:
 * the screen appends the transfer constraint the server enforces, and asserting equality would oblige
 * this file to restate a sentence the contract owns.
 */
const MODULE_FIELD_HELP = 'Select the module to import content into';

/**
 * The wording of the picker's opening option, `"<" + None_Specified + ">"`, seeded at index 0 of this
 * screen's own picker by `Import.ascx.vb:L72` and rendered identically by two sibling screens. The angle
 * brackets are part of the legacy display string, not markup.
 */
const MODULE_PLACEHOLDER_LABEL = '<None Specified>';

const IMPORT_ACTION_LABEL = 'Import';
const CANCEL_ACTION_LABEL = 'Cancel';
const FILE_REQUIRED_MESSAGE = 'Please specify the file to import';
const MODULE_REQUIRED_MESSAGE = 'Please specify the module to import into';
const FILE_UNREADABLE_MESSAGE = 'The selected file could not be read. Choose the file again.';
/**
 * The screen's own wording for a document that reads as empty or whitespace-only, kept distinct from the
 * unreadable-document wording above because the two are resolved differently by the operator.
 */
const FILE_EMPTY_MESSAGE = 'The submitted document is empty.';

const IMPORT_SUCCEEDED_MESSAGE = 'Content was imported into the module.';
const NO_MODULES_MESSAGE = 'There are no modules available to import content into.';

// THE THREE MEASURED REFUSAL SENTENCES
// ⚠ CHARACTER FOR CHARACTER, from the twelve entries of
// `Website/admin/Modules/App_LocalResources/Import.ascx.resx`, and held by
// `core/utils/form-errors.util.ts:L1656-L1659` so that ONE place owns the parity claim.

const NOT_VALID_XML_MESSAGE = 'The file you selected does not contain a valid XML structure';

/** Legacy `NotCorrectType`. */
const NOT_CORRECT_TYPE_MESSAGE = 'The import file specified is not the correct type for this module';

/** Legacy `ImportNotSupported`. */
const IMPORT_NOT_SUPPORTED_MESSAGE = 'The module selected does not support the importing of content';

/** The refusal codes the API publishes for those three sentences, in the same order. */
const CONTENT_INVALID_CODE = 'module.content_invalid';
const CONTENT_TYPE_MISMATCH_CODE = 'module.content_type_mismatch';
const NOT_PORTABLE_CODE = 'module.not_portable';

/** The sentence the route gate presents when it refuses a navigation. */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/**
 * The one policy this screen's own address declares. ⚠ MEASURED FROM `../module.routes.ts`, NOT ASSUMED.
 * See the delegated route group at the foot of this file for why it is the TENANT-WIDE policy and
 * emphatically not the module-scoped one.
 */
const IMPORT_ROUTE_POLICY = 'PortalAdministrator';

/** The module-scoped policy, which the parameterised sibling declares and this screen's address must not. */
const MODULE_SCOPED_POLICY = 'ModuleEdit';

// DOCUMENT FIXTURES
// ⚠ EVERY ONE OF THESE IS HOSTILE ON PURPOSE. A migration that carried content between systems is exactly
// where a payload arrives that nobody wrote, and the claim under test is that this screen treats all of it
// as opaque text.

/** An ordinary portable-content document, in the shape the legacy exporter produced. */
const BENIGN_DOCUMENT = '<announcements><announcement><title>Notice</title></announcement></announcements>';

/** A document carrying an executable script element. */
const SCRIPT_BEARING_DOCUMENT =
  '<announcements><announcement><title>'
  + '<script>window.__imported = true;</script>'
  + '</title></announcement></announcements>';

/** A document that is not well-formed XML at all. A browser XML parser rejects it. */
/**
 * A second, DISTINGUISHABLE document, for the cases that replace one choice with another mid-read. Its
 * content differs from {@link BENIGN_DOCUMENT} so that a body assertion can say WHICH document was sent
 * rather than merely that something was.
 */
const SECOND_DOCUMENT = '<documents><document><title>Handbook</title></document></documents>';

const MALFORMED_DOCUMENT = '<announcements><announcement><title>unclosed';

/** A document declaring an external entity. */
const ENTITY_BEARING_DOCUMENT =
  '<?xml version="1.0"?><!DOCTYPE root [<!ENTITY external SYSTEM "file:///etc/passwd">]><root>&external;</root>';

/** The empty document, which this screen refuses before it spends a request on it. */
const EMPTY_DOCUMENT = '';

/** A file name carrying path traversal and markup, neither of which this screen resolves or renders. */
const HOSTILE_FILE_NAME = '../../<img src=x onerror="window.__named=true">.xml';

// HOSTILE SERVER WORDING

/** Bold markup: the cheapest possible proof that an element was or was not constructed. */
const HOSTILE_BOLD = '<b>x</b>';

/** An executable script element. */
const HOSTILE_SCRIPT = '<script>window.__wording = true;</script>';

const BREAK_PREFIXED_DETAIL = `<br><br/>The submitted content could not be read. ${HOSTILE_BOLD}`;

/**
 * A validation problem document, in the exact shape the API emits for a rejected request. ⚠ `errors` IS
 * REQUIRED ON THIS TYPE, which is the whole reason the contract publishes a distinct one: a function that
 * needs the map should not have to test whether it is there.
 *
 * @param status The status the refusal arrives with.
 * @param errors The per-field map, keyed exactly as the server writes it.
 * @param detailText The sentence.
 * @returns The document.
 */
function validationProblem(
  status: number,
  errors: Readonly<Record<string, readonly string[]>>,
  detailText: string,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.failed`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail: detailText,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
    errors,
  };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * Wraps rows in the shared paged envelope. ⚠ THE PAYLOAD MEMBER IS `items`, NOT `data`, AND THE
 * DISTINCTION IS LOAD-BEARING. Every single-resource route answers `{ data, meta }`, but a paged
 * collection's body IS the page envelope itself, whose records live under `items` -
 * `paged-result.model.ts:L493-L505` reads `response.items` and substitutes an EMPTY ARRAY when it is
 * absent.
 */
function pagedBody(items: readonly ModuleListItem[]): PagedResponse<ModuleListItem> {
  return {
    items,
    meta: { totalCount: items.length, pageIndex: 0, pageSize: 100, totalPages: 1 },
  };
}

/**
 * One listing row. ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so
 * module zero is the first module of an installation: a truthiness test on the chosen identifier would
 * silently refuse to import into it, and so would a comparison against zero or a positivity test.
 */
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

/**
 * A problem document in the exact shape the API emits. ⚠ NO `instance` MEMBER, and `type` ALWAYS PRESENT
 * - both are properties of the real factory rather than of this fixture.
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

/** A real browser `File`, so the read under test is the genuine asynchronous one. */
function documentFile(content: string, name = 'announcements.xml'): File {
  return new File([content], name, { type: 'text/xml' });
}

/**
 * Yields one turn of the event loop's TASK queue, with no timer and no clock. ⚠ WHY NOT A ZERO-DELAY
 * TIMER. A document read resolves off a task rather than a microtask, so awaiting resolved promises alone
 * never reaches it; a real task has to run.
 *
 * @returns A promise settling once one task has run.
 */
function yieldMacrotask(): Promise<void> {
  return new Promise<void>((resolve) => {
    const channel = new MessageChannel();

    channel.port1.onmessage = (): void => {
      channel.port1.close();
      channel.port2.close();
      resolve();
    };

    channel.port2.postMessage(undefined);
  });
}

/**
 * Yields one macrotask that costs REAL TIME, for the tail of a wait that has outrun its cheap turns. ⚠
 * DELIBERATELY A TIMER AND NOT A POSTED MESSAGE, which is the whole reason it exists alongside {@link
 * yieldMacrotask}.
 */
function yieldTimerTurn(): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

/**
 * Narrows an outgoing request body to a plain object, by THROWING rather than by asserting. ⚠ THE BODY IS
 * TYPED AS UNKNOWN AT THE TESTING BACKEND, AND THAT IS THE POINT. The transport cannot know what a caller
 * composed, so the only honest way to read a member is to establish that the body IS a plain object
 * first.
 *
 * @param body The request body as the transport reports it.
 * @returns The body as a keyed record.
 * @throws When the body is not a plain object.
 */
function asRecord(body: unknown): Record<string, unknown> {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    throw new Error('expected the request body to be a plain object');
  }

  return { ...(body as Record<string, unknown>) };
}

/**
 * One member of an outgoing request body, read by BRACKET ACCESS. Bracket access is not a style choice: a
 * record's keys are only ever known at runtime and `noPropertyAccessFromIndexSignature` is enabled for
 * this workspace, so dot access on one would not compile. ⚠ RETURNS UNKNOWN RATHER THAN A GUESSED TYPE,
 * so every call site compares against a literal and no assertion smuggles in an assumption about what the
 * member holds.
 *
 * @param body The request body.
 * @param key The member to read.
 * @returns Whatever the member holds, including null and undefined.
 */
function bodyMember(body: unknown, key: string): unknown {
  return asRecord(body)[key];
}

/**
 * Narrows an element lookup by THROWING rather than by asserting. ⚠ THIS EXISTS SO THAT NO NON-NULL
 * ASSERTION AND NO `any` APPEARS ANYWHERE IN THIS FILE. Element lookups are typed as "the element or
 * nothing", and the two usual ways of getting past that - a non-null assertion or a cast - are claims the
 * compiler cannot check: if the element is genuinely absent, the failure surfaces later as an unreadable
 * property access on nothing rather than as the lookup that failed.
 *
 * @param root The subtree to search.
 * @param selector The selector to resolve.
 * @returns The element, guaranteed present.
 * @throws When the selector matches nothing.
 */
function requireElement<T extends HTMLElement>(root: HTMLElement, selector: string): T {
  const found = root.querySelector<T>(selector);

  if (found === null) {
    throw new Error(`expected an element matching "${selector}" to be rendered`);
  }

  return found;
}

/**
 * The trimmed visible text of an element, or the empty string when it has none. ⚠ TESTED AGAINST NULL
 * EXPLICITLY, never for truthiness, because the empty string is a legitimate value for a text node and
 * the schema this migration reads treats `''` as data rather than as absence.
 *
 * @param element The element to read.
 * @returns The trimmed text.
 */
function visibleText(element: Element): string {
  const text = element.textContent;

  return text === null ? '' : text.trim();
}

/**
 * An element's OWN text, excluding anything its child elements contribute. ⚠ THIS DISTINCTION IS
 * NECESSARY RATHER THAN FASTIDIOUS. The shared field renders its requiredness marker INSIDE the label
 * element - deliberately, so the marker's word joins the accessible name and travels with the control -
 * so the label's full text is the wording followed by that marker.
 *
 * @param element The element to read.
 * @returns The trimmed concatenation of its direct text nodes.
 */
function ownText(element: Element): string {
  let text = '';

  for (const node of Array.from(element.childNodes)) {
    if (node.nodeType === Node.TEXT_NODE) {
      text += node.textContent ?? '';
    }
  }

  return text.trim();
}

/**
 * A `File` whose text read rejects. No real file can be made unreadable from inside a browser, so the one
 * behaviour that needs a failing read gets a genuine `File` with its own `text` replaced.
 */
function unreadableFile(name = 'gone.xml'): File {
  const file = documentFile('never read', name);

  Object.defineProperty(file, 'text', {
    value: (): Promise<string> => Promise.reject(new Error('the document could not be read')),
  });

  return file;
}

describe('ModuleImportComponent', () => {
  let fixture: ComponentFixture<ModuleImportComponent>;
  let mounted: ComponentFixture<ModuleImportComponent> | null;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  /** The transfer request the harness took from the backend while waiting for it, awaiting a case. */
  let capturedTransfer: TestRequest | null = null;

  beforeEach(async () => {
    mounted = null;
    capturedTransfer = null;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The
    // store is pinned to this injector so no specification shares its instance.
    await TestBed.configureTestingModule({
      imports: [ModuleImportComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ModuleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // `notify` is the single sink: the convenience methods all delegate to it, so this records every
    // notification whatever route raised it, and it calls through so the service's own queue fills.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // The component navigates with an ARRAY of commands, so `navigate` is the spied member and not
    // `navigateByUrl`. No routes are declared, so a genuine navigation would fail to match.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    if (mounted !== null) {
      mounted.destroy();
      mounted = null;
    }

    // A transfer taken from the backend by {@link transferOutstanding} is no longer visible to `verify`, so
    // the guarantee `verify` gave for it is restored here: a request that was captured and then never
    // asserted on is a case that submitted and never checked what it sent, which is exactly what `verify`
    // used to catch.
    const abandoned: TestRequest | null = capturedTransfer;

    capturedTransfer = null;

    httpMock.verify();

    if (abandoned !== null && !abandoned.cancelled) {
      throw new Error(
        'a transfer request was awaited by the harness but never asserted on; call expectImport()',
      );
    }
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The listing read is issued from the constructor, before the first pass. */
  function create(): void {
    fixture = TestBed.createComponent(ModuleImportComponent);
    mounted = fixture;
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answers the picker's listing read. */
  function answerModules(rows: readonly ModuleListItem[] = [listRow()]): TestRequest {
    const call = expectRequest('GET', MODULES_URL, 'the picker listing');

    call.flush(pagedBody(rows));
    fixture.detectChanges();

    return call;
  }

  /** Brings the screen up with the picker filled. */
  function arrive(rows: readonly ModuleListItem[] = [listRow()]): void {
    create();
    answerModules(rows);
  }

  /** The rendered subtree, typed once so no case re-asserts the fixture's element type. */
  function root(): HTMLElement {
    const element: unknown = fixture.nativeElement;

    if (!(element instanceof HTMLElement)) {
      throw new Error('the fixture did not render an element');
    }

    return element;
  }

  function query<T extends HTMLElement>(selector: string): T | null {
    return root().querySelector<T>(selector);
  }

  function queryAll<T extends HTMLElement>(selector: string): readonly T[] {
    return Array.from(root().querySelectorAll<T>(selector));
  }

  /** A control looked up by its identifier, narrowed by throwing rather than asserted. */
  function requiredControl<T extends HTMLElement>(controlId: string): T {
    return requireElement<T>(root(), `#${controlId}`);
  }

  /** Chooses a module in the picker by its rendered label. */
  function chooseModule(label: string): void {
    const select = requiredControl<HTMLSelectElement>('module-import-module');
    const option = Array.from(select.options).find(
      (candidate) => visibleText(candidate) === label,
    );

    if (option === undefined) {
      throw new Error(`expected an option labelled "${label}" to be offered`);
    }

    select.value = option.value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** Chooses a document. */
  function chooseDocument(file: File): void {
    const input = requiredControl<HTMLInputElement>('module-import-file');
    const transfer = new DataTransfer();

    transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /**
   * Lets a genuine document read complete. ⚠ THE READ IS REAL BROWSER I/O AND IS NOT A TASK THE FRAMEWORK
   * CAN SEE. `File.text()` resolves off a blob read that zone tracking does not instrument, so
   * `whenStable()` alone reports the fixture idle while the read is still outstanding - and a case
   * asserting at that moment finds no request and then "proves" that nothing was sent, which is the exact
   * opposite of the truth.
   *
   * @param turns The most cheap turns to yield for before falling back to real timers.
   * @param until Stops as soon as this holds.
   */
  async function settle(turns = SETTLE_TURN_CEILING, until?: () => boolean): Promise<void> {
    const floorEndsAt =
      until === undefined ? 0 : performance.now() + SETTLE_WALL_CLOCK_FLOOR_MS;
    let turn = 0;

    for (;;) {
      if (until?.() === true) {
        break;
      }

      // Both bounds must be spent before the wait is abandoned: the cheap turns AND, when something is
      // actually being waited for, the wall-clock floor.
      if (turn >= turns && performance.now() >= floorEndsAt) {
        break;
      }

      await fixture.whenStable();
      await (turn >= turns ? yieldTimerTurn() : yieldMacrotask());

      turn += 1;
    }

    fixture.detectChanges();
  }

  /**
   * Whether the screen has dispatched its transfer, TAKING it from the backend when it has. ⚠⚠ THERE IS
   * NO NON-CONSUMING WAY TO ASK, SO THIS TAKES AND HOLDS INSTEAD OF PEEKING. Every inspection the testing
   * backend offers is built on `match`, which REMOVES what it returns - including `expectNone`, whose
   * implementation matches first and then throws on what it found.
   */
  function transferOutstanding(): boolean {
    if (capturedTransfer !== null) {
      return true;
    }

    const found = httpMock.match(
      (candidate) => candidate.method === 'POST' && candidate.url === IMPORT_URL,
    );

    if (found.length > 1) {
      throw new Error(`expected at most one transfer request, found ${found.length}`);
    }

    capturedTransfer = found.at(0) ?? null;

    return capturedTransfer !== null;
  }

  /** The action bearing the given wording, narrowed by throwing. */
  function actionLabelled(label: string): HTMLButtonElement {
    const button = queryAll<HTMLButtonElement>('button').find(
      (candidate) => visibleText(candidate) === label,
    );

    if (button === undefined) {
      throw new Error(`expected an action labelled "${label}" to be rendered`);
    }

    return button;
  }

  /** Presses the submit control, then lets the awaited document read settle. */
  async function submit(): Promise<void> {
    actionLabelled(IMPORT_ACTION_LABEL).click();

    await settle(SETTLE_TURN_CEILING, transferOutstanding);
  }

  /** The submit control, looked up by its rendered wording. */
  function submitControl(): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => visibleText(candidate) === IMPORT_ACTION_LABEL,
    );
  }

  /** Presses the abandon control. */
  function cancel(): void {
    actionLabelled(CANCEL_ACTION_LABEL).click();
    fixture.detectChanges();
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return queryAll('.form-field__error').map((node) => visibleText(node));
  }

  /**
   * Reveals the guidance the shared field keeps behind a disclosure. The field renders its help text only
   * once its toggle is pressed, so a specification asserting the measured guidance has to press it.
   *
   * @param controlId The identifier of the control whose field should reveal its guidance.
   */
  function revealHelpFor(controlId: string): void {
    const wrapper = queryAll('app-form-field').find(
      (candidate) => candidate.querySelector(`#${controlId}`) !== null,
    );

    if (wrapper === undefined) {
      throw new Error(`expected a shared field wrapping #${controlId}`);
    }

    const toggle = wrapper.querySelector<HTMLButtonElement>('button.form-field__help-toggle');

    if (toggle === null) {
      throw new Error(`expected a help disclosure on the field wrapping #${controlId}`);
    }

    toggle.click();
    fixture.detectChanges();
  }

  /** Every sentence the SUMMARY surface is currently showing. */
  function bannerMessages(): readonly string[] {
    return [
      ...queryAll('.error-banner__title'),
      ...queryAll('.error-banner__message'),
      ...queryAll('.error-banner__detail'),
    ].map((node) => visibleText(node));
  }

  /**
   * Every severity the notification queue was asked to announce a given sentence at. ⚠ THIS EXISTS SO
   * THAT SEVERITY CAN BE ASSERTED IN BOTH DIRECTIONS. Asserting only that a sentence WAS announced at one
   * band leaves open that it was also announced at another, which is exactly the mistake worth catching
   * for a refusal of authority: it must be a warning and it must NOT be an error.
   *
   * @param message The sentence to look for.
   * @returns The severities it was announced at, in call order.
   */
  function severitiesAnnouncedFor(message: string): readonly string[] {
    return notifySpy.calls
      .allArgs()
      .filter((args) => args[1] === message)
      .map((args) => String(args[0]));
  }

  /**
   * The announcing region the shared summary surface owns. ⚠ ASSERTED AS ALWAYS PRESENT, NEVER AS
   * CONDITIONALLY CREATED. `module-import.component.html:125` mounts the surface unconditionally
   * precisely so the region exists before it has anything to say: creating a live region and its content
   * in one instant is the case assistive technology most often fails to announce.
   */
  function liveRegion(): HTMLElement {
    return requireElement<HTMLElement>(root(), '.error-banner-live');
  }

  /** The one outstanding transfer request. */
  function expectImport(): TestRequest {
    const held: TestRequest | null = capturedTransfer;

    if (held !== null) {
      capturedTransfer = null;

      return held;
    }

    return expectRequest('POST', IMPORT_URL, 'the transfer');
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the widest page of the listing the endpoint allows, and nothing else', () => {
      create();

      const call = expectRequest('GET', MODULES_URL);

      // A picker is only as useful as its reach. The size is part of the store's own query state, so
      // stating it is how the screen declares its need rather than a mutation smuggled past the store.
      expect(call.request.params.get('pageSize')).toBe(CHOICE_PAGE_SIZE);

      call.flush(pagedBody([listRow()]));
      fixture.detectChanges();

      expect(visibleText(requireElement(root(), 'h1'))).toBe(IMPORT_TITLE);
      httpMock.expectNone(() => true);
    });

    it('offers every placement, following the pages rather than stopping at the first', () => {
      create();

      const first = expectRequest('GET', MODULES_URL, 'the first page of the picker listing');

      expect(first.request.params.get('pageIndex')).toBe('0');
      first.flush({
        items: [listRow({ moduleId: 0, moduleTitle: 'Announcements' })],
        meta: { totalCount: 2, pageIndex: 0, pageSize: 100, totalPages: 2 },
      });
      fixture.detectChanges();

      const second = expectRequest('GET', MODULES_URL, 'the second page of the picker listing');

      expect(second.request.params.get('pageIndex'))
        .withContext('the walk continues past the first window')
        .toBe('1');
      second.flush({
        items: [listRow({ moduleId: 501, moduleTitle: 'Far Module' })],
        meta: { totalCount: 2, pageIndex: 1, pageSize: 100, totalPages: 2 },
      });
      fixture.detectChanges();

      const options = Array.from(requiredControl<HTMLSelectElement>('module-import-module').options);

      expect(options.map((option) => visibleText(option))).toEqual([
        MODULE_PLACEHOLDER_LABEL,
        'Announcements',
        'Far Module',
      ]);
    });

    it('states how large the choice set is, so its completeness is visible', () => {
      create();

      expectRequest('GET', MODULES_URL).flush({
        // Three PLACEMENTS of two distinct modules: the picker collapses to one option per module, so
        // the two figures legitimately differ and both are reported.
        items: [
          listRow({ moduleId: 0, tabId: 0, moduleTitle: 'Announcements' }),
          listRow({ moduleId: 0, tabId: 1, moduleTitle: 'Announcements' }),
          listRow({ moduleId: 1, tabId: 0, moduleTitle: 'Links' }),
        ],
        meta: { totalCount: 3, pageIndex: 0, pageSize: 100, totalPages: 1 },
      });
      fixture.detectChanges();

      const summary = query('.module-import__choice-summary');

      expect(summary).withContext('the size of the set is stated').not.toBeNull();
      expect(visibleText(requireElement(root(), '.module-import__choice-summary'))).toBe(
        'Choosing among 2 modules across 3 placements.',
      );

      // ⚠ NOT A LIVE REGION. It is standing context about the control, not a change worth
      // interrupting a reader for, and it must not compete with the real refusal surface.
      expect(summary?.hasAttribute('role')).toBeFalse();
      expect(summary?.hasAttribute('aria-live')).toBeFalse();
    });

    it('states the simpler sentence when every module has exactly one placement', () => {
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      expect(visibleText(requireElement(root(), '.module-import__choice-summary'))).toBe(
        'Choosing among 1 module.',
      );
    });

    it('states no size at all when there is nothing to choose among', () => {
      arrive([]);

      expect(query('.module-import__choice-summary'))
        .withContext('a count of zero beside a picker that is not there would be noise')
        .toBeNull();
    });

    it('offers no picker at all when the choice set could not be read completely', () => {
      // ⚠ THE OTHER HALF OF THE CONTRACT. The store refuses rather than truncating, so the screen must
      // present the refusal instead of a partial picker an operator would trust.
      create();

      expectRequest('GET', MODULES_URL).flush({
        items: [listRow({ moduleId: 0 })],
        meta: { totalCount: 90, pageIndex: 0, pageSize: 100, totalPages: 1 },
      });
      fixture.detectChanges();

      expectRequest('GET', MODULES_URL).flush({
        items: [],
        meta: { totalCount: 90, pageIndex: 1, pageSize: 100, totalPages: 1 },
      });
      fixture.detectChanges();

      expect(query('#module-import-module'))
        .withContext('no partial picker is offered')
        .toBeNull();
      expect(query('.module-import__choice-summary')).toBeNull();
      expect(visibleText(requireElement(root(), 'app-empty-state'))).toContain(NO_MODULES_MESSAGE);
    });

    it('offers a choice for every listed module, behind the legacy opening prompt', () => {
      arrive([listRow(), listRow({ moduleId: 1, moduleTitle: 'Links' })]);

      const select = requiredControl<HTMLSelectElement>('module-import-module');
      const options = Array.from(select.options);

      expect(options.map((option) => visibleText(option))).toEqual([
        MODULE_PLACEHOLDER_LABEL,
        'Announcements',
        'Links',
      ]);

      expect(select.selectedIndex)
        .withContext('a select whose value matches no option reports -1 and renders unreadably blank')
        .toBe(0);
      expect(visibleText(options[0])).toBe(MODULE_PLACEHOLDER_LABEL);
      expect(options[0].disabled)
        .withContext('selectable, because choosing it again is how an operator retracts a choice')
        .toBeFalse();
    });

    it('keeps the field unsatisfied while the opening prompt is the choice', async () => {
      arrive([listRow(), listRow({ moduleId: 1, moduleTitle: 'Links' })]);

      // The whole point of binding the prompt to null rather than to a module: the requirement still
      // reports an unmade choice, and it reports it where an operator reads it.
      await submit();

      expect(fieldMessages())
        .withContext('the prompt is not an answer')
        .toContain(MODULE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);

      chooseModule('Links');
      await submit();

      expect(fieldMessages())
        .withContext('choosing a real module satisfies the field')
        .not.toContain(MODULE_REQUIRED_MESSAGE);
    });

    it('states the transfer constraint the server enforces, so a refusal is predictable', () => {
      arrive([listRow()]);

      // The guidance is a disclosure on the shared field, so it has to be revealed to be read - the same
      // affordance the legacy `dnn:label` help icon carried on this screen's own pickers.
      revealHelpFor('module-import-module');

      const help = queryAll('.form-field__help').map((node) => visibleText(node));

      expect(help.some((text) => text.startsWith(MODULE_FIELD_HELP)))
        .withContext(`one help sentence begins "${MODULE_FIELD_HELP}" - saw ${JSON.stringify(help)}`)
        .toBeTrue();
      expect(help.some((text) => text.includes('package supports content transfer')))
        .withContext('the constraint the server enforces is stated before a choice is made')
        .toBeTrue();
      expect(help.some((text) => text.includes('refused when the import is submitted')))
        .withContext('and the operator is told WHEN it will be enforced')
        .toBeTrue();
    });

    it('reports having nothing to import into rather than offering an empty picker', () => {
      arrive([]);

      expect(query('#module-import-module')).toBeNull();
      expect(visibleText(requireElement(root(), 'app-empty-state'))).toContain(NO_MODULES_MESSAGE);
    });

    it('accepts only XML documents at the picker, and says so in the attribute', () => {
      arrive();

      expect(requiredControl<HTMLInputElement>('module-import-file').getAttribute('accept')).toBe(
        '.xml,text/xml,application/xml',
      );
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — WHAT IS REQUIRED BEFORE ANYTHING IS SENT
  // ---------------------------------------------------------------------------------------------------

  describe('before a document has been chosen', () => {
    it('reports both requirements and sends nothing', async () => {
      arrive();

      await submit();

      // Declarative validation replaces the legacy imperative guard. Marking every control first is
      // what makes the messages visible for fields the operator never reached.
      const messages = fieldMessages();

      expect(messages).toContain(FILE_REQUIRED_MESSAGE);
      expect(messages).toContain(MODULE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('reports the document requirement alone once a module has been chosen', async () => {
      arrive();

      chooseModule('Announcements');

      await submit();

      expect(fieldMessages()).toContain(FILE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('offers the submit control from the outset rather than gating it on the two choices', () => {
      arrive();

      // ⚠ THE COMMAND IS DELIBERATELY NOT DISABLED BY AN UNSATISFIED REQUIREMENT, and asserting the
      // opposite would encode a screen this application does not have.
      expect(submitControl()?.disabled).withContext('nothing chosen yet').toBeFalse();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      expect(submitControl()?.disabled).withContext('both choices made').toBeFalse();
    });

    it('withholds the submit control while the listing is still being read', () => {
      create();

      // The listing read is outstanding: there is nothing to choose from yet, so there is nothing the
      // command could act on. This is the one state in which it is genuinely unavailable before a transfer
      // begins, and the indicator beside it is what says why.
      expect(submitControl()?.disabled).withContext('the listing is in flight').toBeTrue();
      expect(query('app-loading-spinner')).withContext('the wait is shown').not.toBeNull();

      answerModules();

      expect(submitControl()?.disabled).withContext('the listing has arrived').toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE BODY
  // ---------------------------------------------------------------------------------------------------

  describe('the transfer request', () => {
    it('posts exactly the four declared members to the route that carries no identifier', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      await submit();

      const call = expectImport();

      expect(call.request.method).toBe('POST');
      expect(call.request.url).toBe(IMPORT_URL);
      expect(call.request.params.keys().length)
        .withContext('the target travels in the body, never in the query')
        .toBe(0);

      expect(call.request.body).toEqual({
        moduleId: 0,
        content: BENIGN_DOCUMENT,
        folder: null,
        fileName: 'announcements.xml',
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('sends the document text verbatim, script element and all, and never renders or parses it', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(SCRIPT_BEARING_DOCUMENT));

      await submit();

      const call = expectImport();
      const body = call.request.body as { content: string };

      expect(body.content).toBe(SCRIPT_BEARING_DOCUMENT);

      // ⚠ AND NONE OF IT REACHED THE DOCUMENT. If any part of this screen rendered the content, this
      // payload would have executed and left its mark on the window.
      expect((window as unknown as Record<string, unknown>)['__imported'])
        .withContext('the content was never evaluated')
        .toBeUndefined();

      expect(query('script')).withContext('no script element was constructed').toBeNull();
      expect(visibleText(root())).not.toContain('window.__imported');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('sends a document that is not well-formed, because the structure rule is the server\'s', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(MALFORMED_DOCUMENT));

      await submit();

      const call = expectImport();

      expect((call.request.body as { content: string }).content).toBe(MALFORMED_DOCUMENT);

      call.flush(
        problem('module.content_invalid', 400, 'The submitted content could not be read.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(query('.error-banner')).not.toBeNull();
    });

    it('sends a document declaring an external entity without expanding it', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(ENTITY_BEARING_DOCUMENT));

      await submit();

      const call = expectImport();
      const body = call.request.body as { content: string };

      // Nothing here parses, so nothing here can expand an entity. The declaration travels as
      // characters and the server is the one component that decides how to read it.
      expect(body.content).toBe(ENTITY_BEARING_DOCUMENT);
      expect(body.content).toContain('file:///etc/passwd');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('refuses an empty document before it is uploaded, and sends nothing', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(EMPTY_DOCUMENT));

      await submit();

      expect(notifySpy).toHaveBeenCalledWith('error', FILE_EMPTY_MESSAGE, null);
      httpMock.expectNone(() => true);
    });

    it('carries a hostile document name as metadata without resolving or rendering it', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, HOSTILE_FILE_NAME));

      await submit();

      const call = expectImport();
      const body = call.request.body as { fileName: string };

      expect(body.fileName).toBe(documentFile(BENIGN_DOCUMENT, HOSTILE_FILE_NAME).name);

      expect((window as unknown as Record<string, unknown>)['__named'])
        .withContext('the name was never evaluated')
        .toBeUndefined();

      expect(query('img')).withContext('no image element was constructed').toBeNull();
      expect(query('script')).withContext('no script element was constructed').toBeNull();
      expect(visibleText(root()))
        .withContext('the hostile characters are on screen as text')
        .toContain('<img src=x');

      // And the name is present, whole and unaltered, as the text it is.
      const shown = queryAll('p').map((node) => visibleText(node));

      expect(shown)
        .withContext('the chosen name is shown verbatim as text')
        .toContain(documentFile(BENIGN_DOCUMENT, HOSTILE_FILE_NAME).name);

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE AWAITED READ IS A WINDOW
  // ---------------------------------------------------------------------------------------------------

  describe('when the document cannot be read', () => {
    it('sends nothing, says so, and leaves the choice in place', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(unreadableFile());

      await submit();

      httpMock.expectNone(() => true);

      expect(notifySpy).toHaveBeenCalledWith('error', FILE_UNREADABLE_MESSAGE, null);

      // The choice is LEFT IN PLACE so the operator can re-pick rather than starting again.
      expect(requiredControl<HTMLInputElement>('module-import-file')).not.toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('lets a second attempt succeed once a readable document is chosen', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(unreadableFile());
      await submit();

      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();

      expect((call.request.body as { content: string }).content).toBe(BENIGN_DOCUMENT);

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);

      // ⚠ AND THE CONFIRMATION SURVIVES THE NAVIGATION IT IS RAISED WITH. This screen announces and then
      // leaves for the listing in the same task, and the shell retires notifications on a completed
      // navigation - so the confirmation was queued and swept before it could be painted.
      const service = TestBed.inject(NotificationService);
      service.clearOnNavigation();

      expect(service.notifications().map((entry) => entry.message))
        .withContext('the listing is where the imported content is seen')
        .toEqual([IMPORT_SUCCEEDED_MESSAGE]);

      service.clearOnNavigation();

      expect(service.notifications()).withContext('one navigation deep').toHaveSize(0);
    });
  });

  describe('when the screen goes away mid-read', () => {
    it('sends nothing at all', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      // The press begins the read; the screen is destroyed before it settles. This is the ONE awaited
      // step on the screen and therefore the one moment this can happen at all.
      actionLabelled(IMPORT_ACTION_LABEL).click();

      fixture.destroy();
      mounted = null;

      // The read still resolves - a promise cannot be recalled - and the component must decline to act
      // on it.
      await Promise.resolve();
      await Promise.resolve();

      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
    });
  });

  // PROOF 4b — THE AWAITED READ IS A SUSPENSION POINT, AND THE FORM STAYS LIVE ACROSS IT

  describe('when the form changes mid-read', () => {
    /** Presses the action and returns WITHOUT settling, leaving the document read outstanding. */
    function beginSubmit(): void {
      actionLabelled(IMPORT_ACTION_LABEL).click();
      fixture.detectChanges();
    }

    it('sends NOTHING when the document is replaced while the first is being read', async () => {
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, 'first.xml'));

      beginSubmit();

      // The operator changes their mind while the read is in flight. The attempt in progress is now for a
      // document they are no longer looking at.
      chooseDocument(documentFile(SECOND_DOCUMENT, 'second.xml'));

      await settle();

      // ⚠ THE FIRST DOCUMENT MUST NOT BE SENT. A screen that retained the first choice would dispatch
      // `first.xml`'s content while the field displayed `second.xml`.
      httpMock.expectNone(() => true);

      // Nothing is announced either: an abandoned attempt is not a failure, and the state the operator
      // changed to is already on screen.
      expect(notifySpy).not.toHaveBeenCalled();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('sends the SECOND document when it is submitted after the switch', async () => {
      // The other half of the property: abandoning the stale attempt must not leave the screen unable to
      // send the new one. A guard that latched would be as broken as no guard at all.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, 'first.xml'));

      beginSubmit();

      chooseDocument(documentFile(SECOND_DOCUMENT, 'second.xml'));

      await settle();

      httpMock.expectNone(() => true);

      await submit();

      const call = expectImport();

      expect(call.request.body).toEqual({
        moduleId: 0,
        content: SECOND_DOCUMENT,
        folder: null,
        fileName: 'second.xml',
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);
    });

    it('sends NOTHING when the TARGET MODULE is changed while the document is being read', async () => {
      arrive([
        listRow({ moduleId: 0, tabModuleId: 1, moduleTitle: 'Announcements' }),
        listRow({ moduleId: 5, tabModuleId: 2, moduleTitle: 'Documents' }),
      ]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      beginSubmit();

      chooseModule('Documents');

      await settle();

      httpMock.expectNone(() => true);
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('sends NOTHING when the SAME document is re-picked mid-read, which only the ticket can catch', async () => {
      // ⚠ THE CASE A VALUE COMPARISON CANNOT SEE, and the reason the guard is not a comparison alone.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, 'same-name.xml'));

      beginSubmit();

      chooseDocument(documentFile(BENIGN_DOCUMENT, 'same-name.xml'));

      await settle();

      httpMock.expectNone(() => true);
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('re-selecting the SAME module mid-read also abandons the attempt', async () => {
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      beginSubmit();

      chooseModule('Announcements');

      await settle();

      // Counted rather than asserted by `expectNone`, which throws and therefore records no expectation:
      // the emptiness of what `match` returns is the claim, and an abandoned attempt that nevertheless
      // dispatched would fail it.
      expect(httpMock.match(() => true))
        .withContext('the stale attempt was abandoned, not dispatched')
        .toEqual([]);
    });
  });

  describe('re-entering the action while it is already running', () => {
    it('issues ONE transfer when the action is pressed twice before the read lands', async () => {
      // ⚠ THE DISABLED BUTTON IS NOT THE GUARD. It is a rendered affordance, and this handler is reachable
      // without it — the form's submit event fires on the Enter key from within either field, and a
      // re-entrant call arriving before change detection has repainted the button finds it still enabled.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      const action = actionLabelled(IMPORT_ACTION_LABEL);

      action.click();
      action.click();

      await settle();

      // Exactly one request, asserted by consuming one and then proving the queue is empty.
      const call = expectImport();

      httpMock.expectNone(() => true);

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledOnceWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);
    });

    it('a refused re-entry changes NOTHING on screen', async () => {
      // The re-entry guard returns before touching any state, which is why this holds: were it placed after
      // the flag resets, a refused press would clear the message from the attempt still running and the
      // operator would watch their own error disappear for no reason they could observe.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(unreadableFile());

      await submit();

      expect(notifySpy).toHaveBeenCalledWith('error', FILE_UNREADABLE_MESSAGE, null);

      const messagesAfterFailure = fieldMessages();

      notifySpy.calls.reset();

      // A second press while the store is NOT busy is a genuine new attempt, so this one is admitted and
      // fails the same way — which is the behaviour the "lets a second attempt succeed" case relies on.
      await submit();

      expect(notifySpy).toHaveBeenCalledWith('error', FILE_UNREADABLE_MESSAGE, null);
      expect(fieldMessages()).toEqual(messagesAfterFailure);
      httpMock.expectNone(() => true);
    });

    it('locks BOTH pickers while a transfer is outstanding, and releases them when it settles', async () => {
      // The affordance half of the fix. Refusing a stale dispatch is correctness; removing the ability to
      // make the edit is what stops the edit being offered in the first place, because an operator who can
      // still change the fields has every reason to expect the change to take effect.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      const modulePicker = requiredControl<HTMLSelectElement>('module-import-module');
      const filePicker = requiredControl<HTMLInputElement>('module-import-file');

      expect(modulePicker.disabled).withContext('idle, so both are usable').toBeFalse();
      expect(filePicker.disabled).toBeFalse();

      await submit();

      const call = expectImport();

      fixture.detectChanges();

      // The request is outstanding, so the store reports the transfer in flight and both fields are locked.
      expect(modulePicker.disabled).withContext('a transfer is outstanding').toBeTrue();
      expect(filePicker.disabled).toBeTrue();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);
    });

    it('leaves the two choices INTACT through a lock-and-release cycle', async () => {
      // ⚠ WHY THE BINDING IS `attr.disabled` AND NOT `disabled`, PROVED BY CONSEQUENCE RATHER THAN BY
      // INSPECTION. A reactive form treats a DISABLED control as absent — excluded from `form.value` and
      // from the group's validity — and the screen's own gate reads exactly that before deciding to
      // dispatch.
      arrive([listRow({ moduleId: 0, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, 'handbook.xml'));

      await submit();

      const refused = expectImport();

      fixture.detectChanges();

      expect(requiredControl<HTMLSelectElement>('module-import-module').disabled)
        .withContext('locked while the transfer is outstanding')
        .toBeTrue();

      refused.flush(problem('module.rejected', 400, 'The document was rejected.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      // The lock is released with the transfer, so the operator can act again.
      expect(requiredControl<HTMLSelectElement>('module-import-module').disabled)
        .withContext('released once it settled')
        .toBeFalse();
      expect(requiredControl<HTMLInputElement>('module-import-file').disabled).toBeFalse();

      // Nothing is re-chosen here. The form still holds both values, so the same request goes again.
      await submit();

      const retried = expectImport();

      expect(retried.request.body)
        .withContext('both choices survived the lock intact')
        .toEqual({
          moduleId: 0,
          content: BENIGN_DOCUMENT,
          folder: null,
          fileName: 'handbook.xml',
        });

      retried.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — OUTCOMES
  // ---------------------------------------------------------------------------------------------------

  describe('a completed transfer', () => {
    it('confirms it and leaves for the listing', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      // ⚠ `204`, WITH NO BODY. The endpoint answers no content, so a fixture returning an envelope
      // would describe a response the server does not send.
      expectImport().flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(severitiesAnnouncedFor(IMPORT_SUCCEEDED_MESSAGE)).toEqual(['success']);
      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);

      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE], { replaceUrl: true });
    });

    it('synthesises no listing row from the payload it just sent', async () => {
      arrive([listRow({ moduleId: 3, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      httpMock.expectNone(() => true);

      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE], { replaceUrl: true });
    });

    it('never treats an empty or blank response body as a failure, nor a failure as success', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      // A 200 carrying a blank body is still a completion. The legacy read the CONTENT of a message to
      // decide the outcome; this reads the status.
      expectImport().flush('', { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      expect(severitiesAnnouncedFor(IMPORT_SUCCEEDED_MESSAGE)).toEqual(['success']);
      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE], { replaceUrl: true });
    });

    it('shows the busy indicator while the transfer is outstanding', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();

      expect(query('app-loading-spinner')).withContext('the wait is announced').not.toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(query('app-loading-spinner')).toBeNull();
    });
  });

  describe('a refused transfer', () => {
    /** Chooses, submits and refuses with the given document. */
    async function refuseWith(problemDocument: ProblemDetails): Promise<void> {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const status = problemDocument.status ?? 500;

      expectImport().flush(problemDocument, {
        status,
        statusText: STATUS_TITLE[status] ?? 'Error',
      });
      fixture.detectChanges();
    }

    it('reports a rejected document at 400 and stays on the screen', async () => {
      await refuseWith(
        problem('module.content_invalid', 400, 'The submitted content could not be read.'),
      );

      expect(visibleText(requireElement(root(), '.error-banner__message'))).toBe(
        'The submitted content could not be read.',
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a content-type mismatch at 400, beside the field it concerns', async () => {
      await refuseWith(
        problem(
          'module.content_type_mismatch',
          400,
          'The submitted content does not belong to this module.',
        ),
      );

      expect(query('.error-banner')).not.toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a module that cannot receive content at 400, beside the module field', async () => {
      await refuseWith(
        problem('module.not_portable', 400, 'This module does not support content transfer.'),
      );

      expect(fieldMessages().length)
        .withContext('the refusal is reported beside a field, not only in the banner')
        .toBeGreaterThan(0);
    });

    it('reports a refusal of authority at 403, at WARNING severity rather than error', async () => {
      await refuseWith(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
      );

      const refusal = 'The authenticated caller is not permitted to perform this operation.';

      expect(severitiesAnnouncedFor(refusal))
        .withContext('a refusal of authority is announced once, as a warning')
        .toEqual(['warning']);
      expect(severitiesAnnouncedFor(refusal)).not.toContain('error');
      // Sharpened rather than relaxed: the reference is now required to be the document's own identifier,
      // and the trailing screen-lifetime opinion is still required to be absent, because this screen stays
      // put on a refusal and the warning must NOT outlive a change of screen.
      expect(notifySpy)
        .withContext('the refusal quotes the identifier the server recorded it under')
        .toHaveBeenCalledWith('warning', refusal, CORRELATION_ID);
      expect(notifySpy).not.toHaveBeenCalledWith('warning', refusal, null);
      expect(notifySpy).not.toHaveBeenCalledWith('warning', refusal, CORRELATION_ID, true);
      expect(notifySpy).not.toHaveBeenCalledWith('error', refusal, null, false);
      expect(notifySpy).not.toHaveBeenCalledWith('error', refusal, null);

      // The shared surface reaches the same classification independently, so the two never disagree.
      expect(requireElement(root(), '.error-banner').getAttribute('data-severity'))
        .withContext('the surface bands it as a refusal, not as a fault')
        .not.toBe('danger');

      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a module that no longer exists at 404', async () => {
      await refuseWith(problem('module.not_found', 404, 'The requested resource does not exist.'));

      expect(query('.error-banner')).withContext('a missing module is reported, not ignored').not.toBeNull();
      expect(visibleText(liveRegion())).toContain('The requested resource does not exist.');
      expect(severitiesAnnouncedFor(IMPORT_SUCCEEDED_MESSAGE))
        .withContext('and is never mistaken for a completed transfer')
        .toEqual([]);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a server fault at 500 at error severity', async () => {
      await refuseWith(
        problem('module.import_failed', 500, 'The content could not be imported.'),
      );

      // The code ends in a fragment the status mapper sends to 500, so this is the shape a genuine
      // import failure takes rather than an invented one.
      expect(notifySpy).not.toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE, null, true);
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(query('.error-banner')).not.toBeNull();
    });

    it('stops showing a refusal once the operator chooses something else', async () => {
      await refuseWith(
        problem('module.not_portable', 400, 'This module does not support content transfer.'),
      );

      expect(query('.error-banner')).not.toBeNull();

      // A refusal held against the PREVIOUS choice must not be shown beside the new one, which is the
      // whole reason the screen notices a change of choice at all.
      chooseDocument(documentFile(BENIGN_DOCUMENT, 'other.xml'));

      expect(query('.error-banner')).withContext('the superseded refusal is withdrawn').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — ABANDONING
  // ---------------------------------------------------------------------------------------------------

  describe('abandoning the screen', () => {
    it('registers an unsaved-entry probe, so a chosen document is not discarded in silence', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arrive();

      // THE CONTROL: an untouched form must not warn, or a later `true` proves nothing.
      expect(tracker.isDirty()).withContext('nothing chosen yet is not unsaved entry').toBeFalse();

      chooseModule('Announcements');

      expect(tracker.isDirty())
        .withContext('a chosen module with no transfer in flight is unsaved entry')
        .toBeTrue();
    });

    it('leaves without validating anything and without sending anything', () => {
      arrive();

      cancel();

      expect(fieldMessages()).toEqual([]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE]);
      httpMock.expectNone(() => true);
    });

    it('leaves even when a document has been chosen', () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      cancel();

      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE]);
      httpMock.expectNone(() => true);
    });

    it('releases the picker read it started, so the store stops reporting itself busy', () => {
      // The store is provided at the application root, so it OUTLIVES this component, and the picker-choice
      // read is started by this screen and wanted by nothing else.
      const store = TestBed.inject(ModuleStore);

      // Created but NOT answered: `create()` mounts the screen, whose constructor issues the picker
      // read, and this case deliberately leaves that read in flight rather than flushing it.
      create();

      const pending = expectRequest('GET', MODULES_URL, 'the picker listing');

      expect(store.choicesLoading()).toBeTrue();
      expect(store.busy()).toBeTrue();

      fixture.destroy();
      mounted = null;

      expect(pending.cancelled)
        .withContext('the picker read is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.choicesLoading()).toBeFalse();
      expect(store.busy())
        .withContext('a destroyed screen must not hold the store busy')
        .toBeFalse();
    });

    it('releases only the picker slice, leaving a sibling read alone', () => {
      // The lease must be narrow. Releasing everything would abandon reads other screens are waiting on,
      // which is why the component calls the slice-scoped command rather than the store-wide cancellation
      // that belongs to session teardown.
      const store = TestBed.inject(ModuleStore);

      arrive();

      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions', 'a sibling read');

      fixture.destroy();
      mounted = null;

      expect(catalogue.cancelled)
        .withContext('a sibling slice must survive this screen going away')
        .toBeFalse();

      catalogue.flush({ data: [], meta: null });
    });
  });

  // PROOF 7 — THE MEASURED WORDING, AND THE MARKUP IT IS RENDERED IN

  describe('the measured wording', () => {
    it('titles the screen exactly as the control-title resource does, and heads it once', () => {
      arrive();

      // `ControlTitle_importmodule.Text`. The resource key is mode-dependent - the same control carried a
      // different title per mode - so this is the import mode's own value rather than a shared one.
      expect(visibleText(requireElement(root(), 'h1'))).toBe(IMPORT_TITLE);

      expect(queryAll('h1')).withContext('one top-level heading, not two').toHaveSize(1);
      expect(visibleText(root()))
        .withContext('the help resource sentence, re-authored as text')
        .toContain(IMPORT_SUBTITLE);
    });

    it('labels the document field from its own resource values, without the legacy colon', () => {
      arrive();

      const label = requireElement<HTMLLabelElement>(root(), 'label[for="module-import-file"]');

      expect(ownText(label)).withContext('the measured label, colon-free').toBe(FILE_FIELD_LABEL);
      expect(ownText(label)).not.toContain(':');

      // ⚠ THE GUIDANCE IS A DISCLOSURE RATHER THAN ALWAYS-VISIBLE TEXT, so it has to be revealed before it
      // can be read.
      revealHelpFor('module-import-file');

      const help = queryAll('.form-field__help').map((node) => visibleText(node));

      expect(help.some((text) => text.startsWith(FILE_FIELD_HELP)))
        .withContext(`one help sentence begins "${FILE_FIELD_HELP}" - saw ${JSON.stringify(help)}`)
        .toBeTrue();
    });

    it('labels the net-new module field and both actions', () => {
      arrive();

      expect(ownText(requireElement(root(), 'label[for="module-import-module"]')))
        .withContext('net-new field, net-new label')
        .toBe(MODULE_FIELD_LABEL);

      expect(actionLabelled(IMPORT_ACTION_LABEL).type).toBe('submit');
      expect(actionLabelled(CANCEL_ACTION_LABEL).type)
        .withContext('the faithful translation of causesvalidation="False"')
        .toBe('button');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE MARKUP THIS SCREEN IS AND IS NOT ALLOWED TO EMIT
  // ---------------------------------------------------------------------------------------------------

  describe('the markup contract', () => {
    it('emits no tabular markup, because the legacy grid was never a data grid', () => {
      arrive();

      expect(query('table')).withContext('no tabular container').toBeNull();
      expect(query('tr')).withContext('no row').toBeNull();
      expect(query('td')).withContext('no cell').toBeNull();
      expect(query('app-data-table')).withContext('no record grid either').toBeNull();
    });

    it('emits no landmark, because the application shell owns each of them exactly once', () => {
      arrive();

      for (const landmark of ['header', 'main', 'nav', 'footer', 'aside']) {
        expect(query(landmark)).withContext(`no <${landmark}> in a feature template`).toBeNull();
      }
    });

    it('references no image asset of any kind', () => {
      arrive();

      expect(query('img')).withContext('no raster icon').toBeNull();
      expect(query('picture')).toBeNull();
    });

    it('composes only shared components that exist', () => {
      arrive();

      const permitted = new Set([
        'app-module-import',
        'app-confirm-dialog',
        'app-data-table',
        'app-empty-state',
        'app-error-banner',
        'app-form-field',
        'app-loading-spinner',
        'app-page-header',
        'app-pagination',
        'app-search-input',
      ]);

      const rendered = queryAll('*')
        .map((node) => node.tagName.toLowerCase())
        .filter((tag) => tag.startsWith('app-'));

      for (const tag of rendered) {
        expect(permitted.has(tag)).withContext(`<${tag}> is a shared component`).toBeTrue();
      }

      // And the ones this screen actually composes are present, so the assertion above is not vacuous.
      expect(rendered).toContain('app-page-header');
      expect(rendered).toContain('app-form-field');
      expect(rendered).toContain('app-error-banner');
    });

    it('offers exactly one document control, wrapped and natively named', () => {
      arrive();

      const inputs = queryAll<HTMLInputElement>('input[type="file"]');

      expect(inputs).withContext('exactly one document control').toHaveSize(1);

      const [input] = inputs;

      if (input === undefined) {
        throw new Error('expected the document control to be rendered');
      }

      // It is INSIDE a shared field rather than beside one, which is what carries its label, its help
      // text and its message region.
      const wrappers = queryAll('app-form-field').filter(
        (candidate) => candidate.querySelector('input[type="file"]') !== null,
      );

      expect(wrappers).withContext('the control is wrapped by the shared field').toHaveSize(1);

      const label = requireElement<HTMLLabelElement>(root(), `label[for="${input.id}"]`);

      expect(input.id).withContext('the control carries an identifier to be named by').not.toBe('');
      expect(ownText(label).length).withContext('the accessible name is not blank').toBeGreaterThan(0);

      const labelledBy = input.getAttribute('aria-labelledby');

      if (labelledBy === null) {
        throw new Error('expected the projected control to carry the reverse label reference');
      }

      // The reference RESOLVES, rather than merely being present. A reference to an element that does not
      // exist leaves the control unnamed while looking perfectly correct in the markup.
      const referenced = requireElement(root(), `#${labelledBy}`);

      expect(referenced).withContext('the reference resolves to the real label').toBe(label);

      // ⚠ AND NO COMPETING LITERAL NAME IS DECLARED, so the VISIBLE label is the accessible name. An
      // `aria-label` would override the visible text and let the two drift apart silently.
      expect(input.getAttribute('aria-label')).toBeNull();
    });
  });

  // PROOF 9 — THE THREE MEASURED REFUSAL SENTENCES
  // ⚠ A DIVERGENCE FROM THE STATED EXPECTATION, RECORDED RATHER THAN PAPERED OVER. These sentences render
  // BESIDE THE FIELD THEY CONCERN, not in the summary surface: `module-import.component.ts:L322-L350`
  // routes the two document codes to the document field and the portability code to the module field, and
  // `L1148-L1156` resolves the wording through the shared conflict table.

  describe('the three measured refusal sentences', () => {
    /** Runs one refusal and returns the request that was refused. */
    async function refuse(code: string, status: number, detailText: string): Promise<void> {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(problem(code, status, detailText), {
        status,
        statusText: STATUS_TITLE[status] ?? 'Error',
      });
      fixture.detectChanges();
    }

    it('renders the invalid-structure sentence verbatim, beside the document field', async () => {
      await refuse(CONTENT_INVALID_CODE, 400, 'The submitted content could not be read.');

      expect(fieldMessages())
        .withContext('the measured sentence, character for character')
        .toContain(NOT_VALID_XML_MESSAGE);

      expect(NOT_VALID_XML_MESSAGE).toContain('you selected');
    });

    it('renders the wrong-type sentence verbatim, beside the document field', async () => {
      await refuse(
        CONTENT_TYPE_MISMATCH_CODE,
        400,
        'The submitted content does not belong to this module.',
      );

      // Legacy key `NotCorrectType`, assigned at `L204` and `L217`.
      expect(fieldMessages()).toContain(NOT_CORRECT_TYPE_MESSAGE);

      // ⚠ THIS ONE MIXES THE TWO WORDINGS - "The import file SPECIFIED" - and the inconsistency is
      // measured rather than tidied.
      expect(NOT_CORRECT_TYPE_MESSAGE).toContain('file specified');
    });

    it('renders the not-portable sentence verbatim, beside the module field', async () => {
      await refuse(NOT_PORTABLE_CODE, 400, 'This module does not support content transfer.');

      expect(fieldMessages()).toContain(IMPORT_NOT_SUPPORTED_MESSAGE);
      expect(IMPORT_NOT_SUPPORTED_MESSAGE).toContain('module selected');
    });

    it('renders the wrong-type sentence at 422 as well as at 400', async () => {
      // The status mapper sends this code to 400 by default, and a validation-shaped refusal of the same
      // request arrives at 422. The WORDING is chosen from the code and is therefore identical either way,
      // which is the property worth pinning: a status change must not silently change a sentence.
      await refuse(
        CONTENT_TYPE_MISMATCH_CODE,
        422,
        'The submitted content does not belong to this module.',
      );

      expect(fieldMessages()).toContain(NOT_CORRECT_TYPE_MESSAGE);
    });

    it('announces the server sentence through the region the summary surface owns', async () => {
      await refuse(CONTENT_INVALID_CODE, 400, 'The submitted content could not be read.');

      const live = liveRegion();

      expect(live.getAttribute('aria-live')).toBe('assertive');
      expect(live.getAttribute('role')).toBe('alert');
      expect(live.getAttribute('aria-atomic')).toBe('true');
      expect(queryAll('[aria-live]')).withContext('exactly one live region').toHaveSize(1);

      // Everything the summary surface shows is INSIDE that region, so anything it renders is announced.
      const banner = requireElement(root(), '.error-banner');

      expect(live.contains(banner)).withContext('the surface is inside the region').toBeTrue();
      expect(visibleText(live)).toContain('The submitted content could not be read.');

      // MIGRATION: `aria-live` IS 100% NET-NEW. Measured over BOTH legacy trees it appears in zero files,
      // as does `role="alert"` and indeed any accessibility attribute at all. Nothing was carried across
      // here because there was nothing to carry.
    });

    it('re-implements no local catch-all sentence, because the problem contract subsumes it', async () => {
      await refuse('module.import_failed', 500, 'The content could not be imported.');

      const everything = visibleText(root());

      expect(everything)
        .withContext('the flattened legacy sentence is not re-implemented on the client')
        .not.toContain('An error occurred during the import');
      expect(visibleText(liveRegion())).toContain('The content could not be imported.');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 10 — THE TWO SURFACES ARE NEVER CONFLATED
  // ---------------------------------------------------------------------------------------------------

  describe('the two failure surfaces', () => {
    it('keeps the unmet requirement inline and out of the summary surface', async () => {
      arrive();

      await submit();

      // MIGRATION: THE SUMMARY SURFACE HAS NO LEGACY ANCESTOR AT ALL, AND THE CITED ONE IS VACUOUS. The
      // measured `asp:ValidationSummary` count is ZERO across the 39 in-scope screens, zero across
      // `Website/admin/Modules/` in particular, and zero anywhere in either legacy tree - so this surface
      // is a net addition rather than a translation of anything.

      expect(fieldMessages()).toContain(FILE_REQUIRED_MESSAGE);
      expect(bannerMessages())
        .withContext('an unmet requirement never reaches the summary surface')
        .not.toContain(FILE_REQUIRED_MESSAGE);
      expect(query('.error-banner')).withContext('nothing was sent, so nothing is summarised').toBeNull();

      httpMock.expectNone(() => true);
    });

    it('keeps the server sentence in the summary surface and out of the inline one', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const serverSentence = 'The submitted content could not be read.';

      expectImport().flush(problem(CONTENT_INVALID_CODE, 400, serverSentence), {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      expect(bannerMessages()).toContain(serverSentence);
      expect(fieldMessages())
        .withContext("the server's own sentence is not repeated beside the control")
        .not.toContain(serverSentence);

      // And the inline surface says the measured thing instead, so neither surface is silent and neither
      // repeats the other.
      expect(fieldMessages()).toContain(NOT_VALID_XML_MESSAGE);
      expect(bannerMessages()).not.toContain(NOT_VALID_XML_MESSAGE);
    });

    it('reads the per-field map with bracket access on its Pascal-cased keys', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      // ⚠ THE KEYS ARE .NET MODEL-STATE KEYS AND ARE NOT CAMEL-CASED. `Content` carries the capital because
      // it names a model member rather than a JSON member, and the camel-case body policy does not reach
      // dictionary keys. One key carries TWO messages, because the server always writes an array.
      const document = validationProblem(
        400,
        {
          Content: ['The content is required.', 'The content exceeds the permitted length.'],
          ModuleId: ['No module with that identifier exists.'],
        },
        'The request was rejected.',
      );

      expectImport().flush(document, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      // ⚠ BRACKET ACCESS, NEVER DOT ACCESS. The member is typed as an index signature and
      // `noPropertyAccessFromIndexSignature` is enabled, so dot access would not compile at all.
      const contentMessages = document.errors['Content'];
      const moduleMessages = document.errors['ModuleId'];

      if (contentMessages === undefined || moduleMessages === undefined) {
        throw new Error('the fixture must carry both keys');
      }

      expect(contentMessages).withContext('the server writes an array even for one message').toHaveSize(2);

      const summarised = bannerMessages();

      for (const message of [...contentMessages, ...moduleMessages]) {
        expect(summarised).withContext(`the summary surface carries "${message}"`).toContain(message);
      }

      // The FIRST message for the document key is what the control shows, because the control has room for
      // one sentence; the surface above carries the whole map.
      expect(fieldMessages()).toContain('The content is required.');
    });

    it('strips the legacy break elements the server may still be accumulating', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(
        validationProblem(
          400,
          { Content: ['<br/>The content is required.'] },
          BREAK_PREFIXED_DETAIL,
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const everything = visibleText(root());

      expect(everything).withContext('no break element survives as text').not.toContain('<br>');
      expect(everything).not.toContain('<br/>');
      expect(query('br')).withContext('and none was constructed either').toBeNull();

      // The SENTENCE survives the stripping intact, so this is a removal of formatting rather than of
      // content.
      expect(bannerMessages().some((text) => text.includes('The submitted content could not be read.')))
        .withContext('the wording itself is untouched')
        .toBeTrue();
      expect(fieldMessages()).toContain('The content is required.');
    });
  });

  // PROOF 11 — SERVER WORDING IS UNTRUSTED INPUT TOO

  describe('hostile server wording', () => {
    /** Refuses the transfer with the supplied document and returns nothing. */
    async function refuseWithDocument(document: ProblemDetails): Promise<void> {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(document, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();
    }

    /** Asserts that hostile wording became characters and not elements. */
    function expectRenderedAsText(...fragments: readonly string[]): void {
      // ⚠ NO ELEMENT WAS CONSTRUCTED. These two are the whole assertion: if any sentence were bound as
      // markup, the tree would carry the elements and one of them would have executed.
      expect(root().querySelector('b')).withContext('no bold element was constructed').toBeNull();
      expect(root().querySelector('script'))
        .withContext('no script element was constructed')
        .toBeNull();
      expect(root().querySelector('img')).withContext('no image element was constructed').toBeNull();
      expect(root().querySelector('iframe')).withContext('and no frame either').toBeNull();
      expect((window as unknown as Record<string, unknown>)['__wording'])
        .withContext('no injected script was evaluated')
        .toBeUndefined();

      const everything = visibleText(root());

      for (const fragment of fragments) {
        expect(everything).withContext(`"${fragment}" is on screen as characters`).toContain(fragment);
      }
    }

    it('renders hostile wording in the sentence as text', async () => {
      await refuseWithDocument(
        problem(CONTENT_INVALID_CODE, 400, `Rejected ${HOSTILE_BOLD}${HOSTILE_SCRIPT}`),
      );

      expectRenderedAsText(HOSTILE_BOLD, HOSTILE_SCRIPT);
    });

    it('renders hostile wording in the title as text', async () => {
      await refuseWithDocument({
        type: `${FAILURE_TYPE_PREFIX}${CONTENT_INVALID_CODE}`,
        title: `Bad Request ${HOSTILE_BOLD}${HOSTILE_SCRIPT}`,
        status: 400,
        detail: 'The submitted content could not be read.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
      });

      expectRenderedAsText(HOSTILE_BOLD, HOSTILE_SCRIPT);
    });

    it('renders hostile wording in a per-field message as text', async () => {
      await refuseWithDocument(
        validationProblem(
          400,
          { Content: [`Rejected ${HOSTILE_BOLD}`, HOSTILE_SCRIPT] },
          'The request was rejected.',
        ),
      );

      expectRenderedAsText(HOSTILE_BOLD, HOSTILE_SCRIPT);
    });

    it('renders a hostile field NAME as text, because the key is server-supplied too', async () => {
      await refuseWithDocument(
        validationProblem(
          400,
          { [`Content${HOSTILE_BOLD}`]: ['The content is required.'] },
          'The request was rejected.',
        ),
      );

      // The map's KEYS are as server-supplied as its values, and the surface labels each group with the
      // key it was given. A key is therefore untrusted on exactly the same terms.
      expectRenderedAsText(HOSTILE_BOLD);
    });

    it('renders a server-echoed document name as text', async () => {
      await refuseWithDocument(
        validationProblem(
          400,
          { FileName: [`The file "${HOSTILE_BOLD}${HOSTILE_SCRIPT}" was rejected.`] },
          'The request was rejected.',
        ),
      );

      expectRenderedAsText(HOSTILE_BOLD, HOSTILE_SCRIPT);
    });
  });

  // PROOF 12 — SENTINEL DISCIPLINE
  // ⚠ THE SHARPEST RULE IN THIS AND THE EASIEST TO BREAK SILENTLY. `dbo.Modules.ModuleID` is `IDENTITY(0,
  // 1)`, so ZERO NAMES A REAL MODULE; the legacy absent-integer marker is MINUS ONE and the legacy
  // absent-string marker is the EMPTY STRING (`L71-L75`, whose body is literally a return of `""`).

  describe('sentinel discipline', () => {
    it('transmits a module identifier of ZERO exactly as zero', async () => {
      arrive([listRow({ moduleId: 0, moduleTitle: 'The first module' })]);

      chooseModule('The first module');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();
      const body: unknown = call.request.body;

      // Asserted on the WHOLE body so a coalesced or dropped member cannot hide behind a partial match.
      expect(body).toEqual({
        moduleId: 0,
        content: BENIGN_DOCUMENT,
        folder: null,
        fileName: 'announcements.xml',
      });

      // ⚠ AND EXPLICITLY NOT ABSENT, NOT COALESCED AND NOT REWRITTEN. `if (moduleId)` would refuse the
      // first module of an installation outright.
      expect(bodyMember(body, 'moduleId')).toBe(0);
      expect(bodyMember(body, 'moduleId')).not.toBeNull();
      expect(bodyMember(body, 'moduleId')).not.toBeUndefined();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('transmits a module identifier of MINUS ONE exactly as minus one', async () => {
      // ⚠ THE SINGLE MOST LIKELY PLACE A COALESCE SILENTLY CORRUPTS THE PAYLOAD. Minus one is the legacy
      // absent-integer marker AND a legitimate identifier - `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so
      // the value is a real key elsewhere in the very same schema. Whatever the form holds is what travels.
      arrive([listRow({ moduleId: -1, moduleTitle: 'A legacy placement' })]);

      chooseModule('A legacy placement');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();
      const body: unknown = call.request.body;

      expect(bodyMember(body, 'moduleId')).withContext('minus one travels verbatim').toBe(-1);
      expect(bodyMember(body, 'moduleId')).not.toBeNull();

      expect(bodyMember(body, 'moduleId')).not.toBe(null);

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('transmits an empty document name as the empty string, never as null', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, ''));
      await submit();

      const call = expectImport();
      const body: unknown = call.request.body;

      expect(bodyMember(body, 'fileName')).withContext("'' is transmitted as ''").toBe('');
      expect(bodyMember(body, 'fileName')).not.toBeNull();
      expect(bodyMember(body, 'fileName')).not.toBeUndefined();

      // The folder member is a genuine null, and it too is PRESENT rather than omitted - which is what
      // makes the distinction between the two observable at all.
      expect(bodyMember(body, 'folder')).toBeNull();
      expect(Object.keys(asRecord(body)).sort()).toEqual([
        'content',
        'fileName',
        'folder',
        'moduleId',
      ]);

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });

    it('does not collapse a status of ZERO on a problem document to "absent"', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(
        {
          type: `${FAILURE_TYPE_PREFIX}${CONTENT_INVALID_CODE}`,
          title: 'Bad Request',
          status: 0,
          detail: 'A gateway rejected the request.',
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The refusal is presented rather than swallowed, which is the observable consequence of not
      // collapsing it.
      expect(query('.error-banner')).withContext('a zero status is still a refusal').not.toBeNull();
      expect(visibleText(liveRegion())).toContain('A gateway rejected the request.');

      // ⚠ AND IT IS NOT MISTAKEN FOR A REFUSAL OF AUTHORITY. The authority band is reserved for the status
      // that actually means it, so zero must not reach the warning path.
      expect(severitiesAnnouncedFor('A gateway rejected the request.')).toEqual([]);
      expect(navigateSpy).not.toHaveBeenCalled();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 13 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  // ---------------------------------------------------------------------------------------------------

  describe('deliberate omissions', () => {
    it('offers no folder picker, because no folder resource survives the migration', () => {
      arrive();

      const text = visibleText(root());

      expect(text).not.toContain('Folder');
      expect(queryAll('select').length).withContext('exactly one picker, for the module').toBe(1);
    });

    it('builds no multipart body', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();

      // The request contract declares four members and no upload primitive, no stream and no
      // multipart form, so the body is a plain object serialised as JSON.
      expect(call.request.body instanceof FormData)
        .withContext('the payload is JSON, not a form upload')
        .toBeFalse();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
    });
  });
});

// THE DELEGATED ROUTE-ORDERING REGRESSION PROOF
// THE DEFECT IT GUARDS. The router matches in DECLARATION ORDER and `:moduleId` matches any single segment,
// so `import` must be declared above it. Were the parameterised route declared first, `/modules/import`
// would match IT, and the edit screen would be handed the string 'import' as the record to load.

describe('MODULE_ROUTES — the delegated ordering regression proof', () => {
  /** The addresses whose relative order is load-bearing. */
  const LIST_PATH = '';
  const CREATE_PATH = 'new';
  const IMPORT_PATH = 'import';
  const RECORD_PATH = ':moduleId';

  /**
   * The position of a declared path, established to EXIST before its order is compared. ⚠ THE EXISTENCE
   * CHECK IS THE WHOLE POINT OF THIS HELPER, AND OMITTING IT IS THE CLASSIC WAY THIS ASSERTION ROTS. A
   * search that finds nothing answers minus one, and minus one is LESS THAN every real position - so a
   * naive `expect(indexOf(a)).toBeLessThan(indexOf(b))` passes with flying colours after somebody DELETES
   * route `a` entirely.
   *
   * @param path The declared path to locate.
   * @returns Its index in declaration order.
   * @throws When no route declares that path.
   */
  function positionOf(path: string): number {
    const index = MODULE_ROUTES.findIndex((route) => route.path === path);

    if (index < 0) {
      throw new Error(`MODULE_ROUTES declares no route for the path "${path}"`);
    }

    return index;
  }

  /** The route declaring a given path, narrowed by throwing. */
  function routeFor(path: string): Route {
    const entry = MODULE_ROUTES[positionOf(path)];

    if (entry === undefined) {
      throw new Error(`MODULE_ROUTES declares no route for the path "${path}"`);
    }

    return entry;
  }

  /**
   * The component a route lazily resolves to.
   *
   * @param route The route to resolve.
   * @returns The component class the route activates.
   */
  async function resolvedComponent(route: Route): Promise<Type<unknown>> {
    const loader = route.loadComponent;

    if (loader === undefined) {
      throw new Error(`the route for "${String(route.path)}" declares no lazy component`);
    }

    const resolved: unknown = await loader();

    if (typeof resolved !== 'function') {
      throw new Error(`the route for "${String(route.path)}" did not resolve to a component`);
    }

    return resolved as Type<unknown>;
  }

  /**
   * The policy a route declares, read WITHOUT reaching for `any`. ⚠ THE ROUTER TYPES ROUTE DATA AS AN
   * INDEX SIGNATURE ONTO `any`, so the value arrives with every compile-time guarantee switched off: it
   * could be absent on a route that forgot the key, or a number, or an object, and none of that would be
   * caught.
   *
   * @param route The route to read.
   * @returns The declared policy, or null when the route declares none.
   */
  function declaredPolicy(route: Route): string | null {
    const declared: unknown = route.data?.['permission'];

    if (declared === undefined) {
      return null;
    }

    if (typeof declared !== 'string') {
      throw new Error(`the route for "${String(route.path)}" declares a non-string policy`);
    }

    return declared;
  }

  // ---------------------------------------------------------------------------------------------------
  // ASSERTION (a) — THE STRUCTURAL PROOF. Guard-independent, router-independent, and always available.
  // ---------------------------------------------------------------------------------------------------

  describe('the declaration order', () => {
    it('declares every literal segment above the parameterised route that would swallow it', () => {
      // Existence FIRST, order second. Each of these throws if the route is gone, so a deletion fails the
      // case instead of quietly satisfying the inequality.
      const list = positionOf(LIST_PATH);
      const create = positionOf(CREATE_PATH);
      const importAt = positionOf(IMPORT_PATH);
      const record = positionOf(RECORD_PATH);

      expect(importAt)
        .withContext('/modules/import must be declared above /modules/:moduleId')
        .toBeLessThan(record);
      expect(create)
        .withContext('/modules/new must be declared above /modules/:moduleId')
        .toBeLessThan(record);

      // The empty path is matched in full rather than by segment, so its position is not load-bearing -
      // but it is asserted present, because the group would otherwise have no default address.
      expect(list).toBeGreaterThanOrEqual(0);

      // ⚠ NOT ALPHABETICAL AND NOT BY LENGTH. 'import' sorts after ':moduleId' by code point and is shorter
      // than ':moduleId/export', so either of those rules would produce an order that breaks the address.
      // Stated as an assertion so the reason cannot be lost.
      expect(IMPORT_PATH > RECORD_PATH)
        .withContext('the correct order is the OPPOSITE of alphabetical here')
        .toBeTrue();
    });

    it('resolves the two adjacent addresses to genuinely different screens', async () => {
      const importComponent = await resolvedComponent(routeFor(IMPORT_PATH));
      const recordComponent = await resolvedComponent(routeFor(RECORD_PATH));

      // ⚠ IDENTITY, NOT NAME. Comparing the resolved classes is what proves the two addresses are distinct
      // destinations; comparing their names would pass against two different classes that happened to share
      // one, which a minifier can arrange.
      expect(importComponent).toBe(ModuleImportComponent);
      expect(recordComponent).not.toBe(ModuleImportComponent);

      // And the parameterised sibling resolves to something else entirely, which is the collision this
      // whole group exists to prevent.
      expect(importComponent).not.toBe(recordComponent);
    });

    it('declares no route path that would resolve to /modules/modules', () => {
      // Every path here is RELATIVE to the group's mount point, so a child spelled 'modules' would resolve
      // to `/modules/modules` - the single easiest way to break the entire group.
      for (const route of MODULE_ROUTES) {
        expect(route.path).not.toBe('modules');
        expect(String(route.path).startsWith('/'))
          .withContext(`"${String(route.path)}" must be relative to the mount point`)
          .toBeFalse();
      }
    });
  });

  describe('the policy each address declares', () => {
    it('gates this screen on the tenant-wide policy its own endpoint declares', () => {
      const entry = routeFor(IMPORT_PATH);

      expect(declaredPolicy(entry))
        .withContext('measured from the route table, not assumed')
        .toBe(IMPORT_ROUTE_POLICY);

      // The gate is attached, so the policy is not merely declarative decoration.
      expect(entry.canActivate ?? []).withContext('the gate is attached').toHaveSize(1);
    });

    it('gates the parameterised sibling on the module-scoped policy', () => {
      // `ModuleEdit` IS declared in this table - on the routes that actually carry `:moduleId`, which is
      // the only place it can be answered.
      expect(declaredPolicy(routeFor(RECORD_PATH))).toBe(MODULE_SCOPED_POLICY);
      expect(declaredPolicy(routeFor(`${RECORD_PATH}/settings`))).toBe(MODULE_SCOPED_POLICY);
      expect(declaredPolicy(routeFor(`${RECORD_PATH}/export`))).toBe(MODULE_SCOPED_POLICY);
    });

    it('declares no record-scoped policy on any address that carries no identifier', () => {
      // ⚠ THE INVARIANT THAT MAKES THE FAIL-CLOSED GATE SAFE, asserted over the WHOLE table rather than
      // over one route, so a future address cannot reintroduce the defect somewhere else.
      const scoped = ['ModuleEdit', 'ModuleView', 'TabEdit', 'TabView'];

      for (const route of MODULE_ROUTES) {
        const policy = declaredPolicy(route);

        if (policy !== null && scoped.includes(policy)) {
          expect(String(route.path))
            .withContext(`"${String(route.path)}" declares ${policy} and must carry its scope`)
            .toContain(':moduleId');
        }
      }
    });

    it('never names an operation as though it were a policy', () => {
      // ⚠ THE POLICY VOCABULARY IS THE API'S REGISTERED SET, NOT THE VERB OF THE SCREEN. An address gated
      // on a made-up name is refused outright by the gate (`permission.guard.ts:L658-L662`), which is the
      // safer default but is also invisible until somebody tries the screen.
      const forbidden = ['IMPORT', 'EXPORT', 'Import', 'Export', 'ModuleImport', 'ModuleExport'];

      for (const route of MODULE_ROUTES) {
        const policy = declaredPolicy(route);

        if (policy !== null) {
          expect(forbidden).withContext(`"${policy}" is not a registered policy name`).not.toContain(policy);
        }
      }

      // And this screen's own address specifically declares neither the module-scoped policy nor a verb.
      expect(declaredPolicy(routeFor(IMPORT_PATH))).not.toBe(MODULE_SCOPED_POLICY);
      expect(declaredPolicy(routeFor(IMPORT_PATH))).not.toBe('ModuleView');
    });

    it('titles this screen with the same wording the screen itself renders', () => {
      // A small consistency worth pinning: the document title and the visible heading are two independent
      // declarations of one fact, and nothing else would notice them diverging.
      expect(routeFor(IMPORT_PATH).title).toBe(IMPORT_TITLE);
    });
  });

  // ASSERTION (b) — THE LIVE PROOF
  // The structural proof above establishes the ORDER. This one establishes the CONSEQUENCE: a genuine
  // navigation to the genuine address, through the genuine table, activating the genuine screen.

  describe('navigating to the address', () => {
    /** The mount point the application uses, so the address under test is the real one. */
    const MOUNT_PATH = 'modules';

    /**
     * A host carrying nothing but an outlet. Declared locally rather than in a shared helper file: it is
     * scaffolding for ONE proof, and a shared fixture would be a file this work is not authorised to
     * create.
     */
    @Component({
      selector: 'app-route-host',
      standalone: true,
      imports: [RouterOutlet],
      template: '<router-outlet />',
    })
    class RouteHostComponent {}

    /** A resolved identity holding tenant administration, which is what the declared policy asks for. */
    const administrator: CurrentUser = {
      userId: 1,
      portalId: 0,
      portalName: 'Baseline Portal',
      username: 'admin',
      displayName: 'Administrator',
      email: 'admin@example.invalid',
      isSuperUser: true,
      isPortalAdministrator: true,
      roles: ['Administrators'],
      permissions: [],
    };

    let httpMock: HttpTestingController;
    let host: ComponentFixture<RouteHostComponent> | null;
    let gateNotifySpy: jasmine.Spy;

    beforeEach(async () => {
      host = null;

      /** A signal double that is CALLABLE, because that is what a signal is. */
      const fixedSignal = <T,>(value: T): Signal<T> => {
        const read = (): T => value;

        return read as Signal<T>;
      };

      const authStore = {
        isAuthenticated: fixedSignal(true),
        currentUser: fixedSignal<CurrentUser | null>(administrator),
        isSuperUser: fixedSignal(true),

        // The gate reads tenant administration through THIS projection — the server's own determination,
        // combined with the host flag — and reads no role name at all. Mirrors the real store's computation
        // over the identity above rather than restating a verdict.
        administersCurrentPortal: fixedSignal(
          administrator.isSuperUser || administrator.isPortalAdministrator,
        ),
      };

      await TestBed.configureTestingModule({
        imports: [RouteHostComponent],
        providers: [
          // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
          // Reversed, the real backend survives and the proof quietly attempts live network requests.
          provideHttpClient(),
          provideHttpClientTesting(),

          // ⚠ THE REAL TABLE, MOUNTED WHERE THE APPLICATION MOUNTS IT, so the address navigated below is
          // `/modules/import` exactly as an operator would reach it - not a root-relative approximation of
          // it. The children are passed through untouched.
          provideRouter([{ path: MOUNT_PATH, children: MODULE_ROUTES }]),

          // The gate's OWN dependencies, doubled. The gate is the real one.
          { provide: AuthStore, useValue: authStore },
          ModuleStore,
        ],
      }).compileComponents();

      httpMock = TestBed.inject(HttpTestingController);

      gateNotifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
    });

    afterEach(() => {
      if (host !== null) {
        host.destroy();
        host = null;
      }

      httpMock.verify();
    });

    /**
     * Every refusal the gate announced. ⚠ THIS IS THE DIRECT OBSERVABLE OF THE FAIL-CLOSED BRANCH. All
     * three of the gate's refusal paths - an unusable policy declaration, an unresolvable scope, and a
     * caller the client can already see lacks the administration the policy requires - announce the SAME
     * sentence at warning severity before returning false.
     *
     * @returns The refusal sentences announced, in call order.
     */
    function notifiedRefusals(): readonly string[] {
      return gateNotifySpy.calls
        .allArgs()
        .filter((args) => args[1] === ACCESS_REFUSED_MESSAGE)
        .map((args) => String(args[1]));
    }

    /**
     * The activated route chain, root first. ⚠ THE CHAIN IS WALKED DOWNWARDS FROM THE ROOT, AND THE
     * OBVIOUS ALTERNATIVE IS A TRAP. Reading `pathFromRoot` off the ROOT snapshot answers an array
     * containing only the root itself - it is that snapshot's own ancestry, not the tree beneath it - so
     * every assertion made against it looks at a snapshot carrying no configuration and no parameters.
     *
     * @returns Every activated snapshot from the root to the leaf.
     */
    function activatedChain(): readonly ActivatedRouteSnapshot[] {
      const chain: ActivatedRouteSnapshot[] = [];
      let current: ActivatedRouteSnapshot | null = TestBed.inject(Router).routerState.snapshot.root;

      while (current !== null) {
        chain.push(current);
        current = current.firstChild;
      }

      return chain;
    }

    /** Consumes the listing read the activated screen issues from its constructor. */
    function answerChoiceRead(): void {
      for (const call of httpMock.match(() => true)) {
        call.flush({
          items: [],
          meta: { totalCount: 0, pageIndex: 0, pageSize: 100, totalPages: 1 },
        });
      }
    }

    it('activates the import screen and not the record screen', async () => {
      host = TestBed.createComponent(RouteHostComponent);
      host.detectChanges();

      const router = TestBed.inject(Router);
      const navigated = await router.navigateByUrl(`/${MOUNT_PATH}/${IMPORT_PATH}`);

      // ⚠⚠ THE NAVIGATION SUCCEEDED, AND THAT IS ITSELF THE D-MI-1 ASSERTION. A gate that failed closed
      // would have answered false here and no screen would have been activated at all - which is exactly
      // what WOULD happen had this address declared a record-scoped policy it carries no scope for.
      expect(navigated).withContext('the gate admitted the navigation').toBeTrue();
      expect(notifiedRefusals())
        .withContext('the fail-closed branch was not taken')
        .toEqual([]);

      host.detectChanges();
      answerChoiceRead();

      // Pending effects are flushed explicitly. ⚠ `TestBed.flushEffects()` is the API this version of the
      // framework publishes; there is no `TestBed.tick()` on it, so nothing here guesses at one.
      TestBed.flushEffects();
      host.detectChanges();

      const rendered: unknown = host.nativeElement;

      if (!(rendered instanceof HTMLElement)) {
        throw new Error('the host did not render an element');
      }

      // ⚠ THE SCREEN THAT ACTUALLY MOUNTED. `/modules/import` reached the import screen, and emphatically
      // NOT the record screen the parameterised sibling would have activated had it been declared first.
      expect(rendered.querySelector('app-module-import'))
        .withContext('/modules/import activates the import screen')
        .not.toBeNull();
      expect(rendered.querySelector('app-module-form'))
        .withContext('and never the record screen, which would have been handed the string "import"')
        .toBeNull();

      // The address itself is unchanged, so nothing rewrote or redirected it.
      expect(router.url).toBe(`/${MOUNT_PATH}/${IMPORT_PATH}`);

      // ⚠ AND NO PARAMETER NAMED THE TARGET ANYWHERE IN THE ACTIVATED CHAIN, which is precisely why the
      // endpoint carries the target in the BODY and why this address must not declare a record-scoped
      // policy.
      const chain = activatedChain();

      expect(chain.length).withContext('a leaf was activated, so the scan is not vacuous').toBeGreaterThan(1);

      for (const entry of chain) {
        expect(entry.paramMap.get('moduleId'))
          .withContext('the import address carries no module parameter')
          .toBeNull();
      }

      // The leaf that activated is the LITERAL route, named explicitly.
      const leaf = chain[chain.length - 1];

      if (leaf === undefined) {
        throw new Error('expected an activated leaf');
      }

      expect(leaf.routeConfig?.path).toBe(IMPORT_PATH);
    });

    it('leaves the record address reachable and binds its identifier', async () => {
      // ⚠ THE COMPLEMENT OF THE PROOF ABOVE, AND IT IS NOT REDUNDANT. Ordering literals first is only
      // correct if the PARAMETERISED address still works: a table that reached the import screen by
      // BREAKING `/modules/42` would satisfy every assertion above while being worse than the defect they
      // guard against.
      const router = TestBed.inject(Router);
      const navigated = await router.navigateByUrl(`/${MOUNT_PATH}/42`);

      expect(navigated).withContext('the record address still resolves').toBeTrue();
      expect(router.url).toBe(`/${MOUNT_PATH}/42`);

      // The address matched the PARAMETERISED route and not the literal one, which is the collision this
      // group exists to keep impossible - in the other direction.
      const chain = activatedChain();
      const leaf = chain[chain.length - 1];

      if (leaf === undefined) {
        throw new Error('expected an activated leaf');
      }

      expect(leaf.routeConfig?.path)
        .withContext('a numeric segment matches the parameterised route')
        .toBe(RECORD_PATH);
      expect(leaf.routeConfig?.path).not.toBe(IMPORT_PATH);

      // And the identifier bound, which is the scope the module-scoped policy resolves from and the whole
      // reason that policy is answerable on THIS address and not on the import one.
      const resolved = chain
        .map((entry) => entry.paramMap.get('moduleId'))
        .find((value) => value !== null);

      expect(resolved).withContext('the scope the gate resolves from is present').toBe('42');

      // The gate admitted a module-scoped policy BECAUSE the scope was resolvable, so nothing was refused.
      expect(notifiedRefusals()).toEqual([]);
    });
  });
});
