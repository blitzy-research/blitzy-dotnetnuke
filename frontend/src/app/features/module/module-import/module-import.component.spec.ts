/**
 * Specification for `features/module/module-import/module-import.component.ts` and its paired template.
 *
 * THIS SCREEN CARRIES UNTRUSTED CONTENT ACROSS THE WIRE, and that is what makes it worth testing at
 * this depth. The operator chooses a document from their own machine, the screen reads it AS TEXT and
 * posts that text, and three properties of the transfer are load-bearing:
 *
 * 1. THE TEXT IS TRANSMITTED VERBATIM AND IS NEVER PARSED OR RENDERED. `Import.ascx.vb:L188-L200`
 *    constructed a document, read its declared type attribute, compared it against the module's own
 *    names, and handed the ROOT ELEMENT'S INNER MARKUP to the module's portability contract. Every one
 *    of those steps is now server-side. A browser that parsed the document, unwrapped a root element,
 *    or put any of it into the page would be re-deriving a server decision AND opening a rendering
 *    surface for content nobody vetted.
 * 2. A DOCUMENT THAT CANNOT BE READ SENDS NOTHING. The read is asynchronous and can genuinely fail -
 *    the file may have been moved, renamed or made unreadable between being chosen and being
 *    submitted - and the only correct outcome is to say so and send nothing at all.
 * 3. THE AWAIT IS A WINDOW. The read is the one awaited step, so it is the one moment at which the
 *    screen can have been destroyed, or the operator can have chosen something else, before the
 *    request would be issued.
 *
 * ## Provenance
 *
 * `Website/admin/Modules/import.ascx` and its code-behind are the legacy screen. Supporting sources
 * are `Library/Components/Modules/ModuleInfo.vb` for the portability flag that this screen
 * deliberately does not compute, `Library/Components/Shared/Null.vb` for the absent-string marker, and
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` for the identity seed
 * that makes module ZERO real. The legacy tree contains no automated test of any kind.
 *
 * ## What is real and what is doubled
 *
 * The COMPONENT is imported as the standalone unit it is; the STORE is genuine and pinned to this
 * injector; the TRANSPORT is the testing backend and `verify()` fails any case that left a request
 * unconsumed or issued one nobody expected. Only the NAVIGATION and the NOTIFICATION QUEUE are spied.
 *
 * THE CHOSEN DOCUMENT IS A REAL `File`, constructed in the browser, so `file.text()` is the genuine
 * asynchronous read rather than a stub - which is the only way the verbatim-transmission claim can be
 * made honestly. The one case that needs a failing read supplies a `File` whose `text()` rejects,
 * because no real file can be made unreadable from inside a browser.
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

/** The transfer endpoint. Its route carries NO identifier - the target travels in the body. */
const IMPORT_URL = '/api/v1/modules/import';

/** Where both the completion and the abandonment go. */
const MODULE_LIST_ROUTE = '/modules';

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

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported: the component exports none of these, and a specification that read
// them from the component could not detect a change to them.
// =====================================================================================================

const IMPORT_TITLE = 'Import Module';

/**
 * The supporting sentence, re-authored from the paragraph inside `ModuleHelp.Text`.
 *
 * MIGRATION: THE STORED RESOURCE VALUE IS MARKUP AND ONLY ITS SENTENCE SURVIVES. `ModuleHelp.Text`
 *   holds a top-level heading element followed by a paragraph element; the heading duplicated the screen
 *   title exactly, so it is dropped and the sentence is carried across as PLAIN TEXT. Nothing binds a
 *   resource value as markup, which is what the untrusted-markup group below proves at runtime.
 */
const IMPORT_SUBTITLE = 'Administrators can import content for the specified module.';

/** The document field's label. `plFile.Text`, with the legacy `suffix=":"` colon not reproduced. */
const FILE_FIELD_LABEL = 'File';

/**
 * `plFile.Help` verbatim, and asserted as a PREFIX.
 *
 * The screen appends the published byte limit to it, so the measured resource wording is the beginning
 * of the rendered guidance rather than the whole of it. Asserting equality would either fail against a
 * screen that is behaving correctly or force the limit to be restated here, where it would drift.
 */
const FILE_FIELD_HELP = 'Select the import file';

/** The module field's label. Net-new alongside the net-new field. */
const MODULE_FIELD_LABEL = 'Module';

const IMPORT_ACTION_LABEL = 'Import';
const CANCEL_ACTION_LABEL = 'Cancel';
const FILE_REQUIRED_MESSAGE = 'Please specify the file to import';
const MODULE_REQUIRED_MESSAGE = 'Please specify the module to import into';
const FILE_UNREADABLE_MESSAGE = 'The selected file could not be read. Choose the file again.';
/**
 * The screen's own wording for a document that reads as empty or whitespace-only, kept distinct from
 * the unreadable-document wording above because the two are resolved differently by the operator.
 */
const FILE_EMPTY_MESSAGE = 'The submitted document is empty.';

const IMPORT_SUCCEEDED_MESSAGE = 'Content was imported into the module.';
const NO_MODULES_MESSAGE = 'There are no modules available to import content into.';

// =====================================================================================================
// THE THREE MEASURED REFUSAL SENTENCES
//
// ⚠ CHARACTER FOR CHARACTER, from the twelve entries of
// `Website/admin/Modules/App_LocalResources/Import.ascx.resx`, and held by
// `core/utils/form-errors.util.ts:L1656-L1659` so that ONE place owns the parity claim.
//
// MIGRATION: THE "SELECTED" AND "SPECIFIED" ASYMMETRY IS MEASURED AND IS DELIBERATELY NOT TIDIED. The
//   import screen's own two sentences say "selected" while its sibling transfer screen says
//   "specified" — and `NotCorrectType` mixes the two, saying "The import file SPECIFIED". Normalising
//   any of it would be an unrequested wording change dressed up as consistency, so all three are
//   reproduced exactly as measured, including that inconsistency.
//
// MIGRATION: THE LEGACY RESOURCE KEYS THEMSELVES DO NOT TRAVEL. `Import.ascx.vb` selected its wording
//   with the keys `NotValidXml` (L192), `NotCorrectType` (L204, L217) and `ImportNotSupported` (L208,
//   L214). The API publishes its OWN vocabulary in the problem document's `type` member, so a table
//   keyed on the legacy names could match nothing taken off the wire. The WORDING is what survives.
// =====================================================================================================

/** Legacy `NotValidXml`. Raised where `Import.ascx.vb:L190-L193` failed to load the document. */
const NOT_VALID_XML_MESSAGE = 'The file you selected does not contain a valid XML structure';

/** Legacy `NotCorrectType`. Raised where `L176` or `L196-L197` rejected the declared type. */
const NOT_CORRECT_TYPE_MESSAGE = 'The import file specified is not the correct type for this module';

/** Legacy `ImportNotSupported`. Raised where `L208` or `L214` found the module could not receive. */
const IMPORT_NOT_SUPPORTED_MESSAGE = 'The module selected does not support the importing of content';

/** The refusal codes the API publishes for those three sentences, in the same order. */
const CONTENT_INVALID_CODE = 'module.content_invalid';
const CONTENT_TYPE_MISMATCH_CODE = 'module.content_type_mismatch';
const NOT_PORTABLE_CODE = 'module.not_portable';

/**
 * The sentence the route gate presents when it refuses a navigation.
 *
 * Restated rather than imported for the same reason every other sentence here is: the gate does not
 * export it, and a specification that read it from the gate could not detect a change to it. Its
 * provenance is `AccessDenied.ascx.resx` → `AccessDenied.Text`, second clause only.
 */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/**
 * The one policy this screen's own address declares.
 *
 * ⚠ MEASURED FROM `../module.routes.ts`, NOT ASSUMED. See the delegated route group at the foot of this
 * file for why it is the TENANT-WIDE policy and emphatically not the module-scoped one.
 */
const IMPORT_ROUTE_POLICY = 'PortalAdministrator';

/** The module-scoped policy, which the parameterised sibling declares and this screen's address must not. */
const MODULE_SCOPED_POLICY = 'ModuleEdit';

// =====================================================================================================
// DOCUMENT FIXTURES
//
// ⚠ EVERY ONE OF THESE IS HOSTILE ON PURPOSE. A migration that carried content between systems is
// exactly where a payload arrives that nobody wrote, and the claim under test is that this screen
// treats all of it as opaque text.
// =====================================================================================================

/** An ordinary portable-content document, in the shape the legacy exporter produced. */
const BENIGN_DOCUMENT = '<announcements><announcement><title>Notice</title></announcement></announcements>';

/**
 * A document carrying an executable script element.
 *
 * If any part of this screen rendered the content, this would be the payload that proved it - and it
 * would prove it by executing.
 */
const SCRIPT_BEARING_DOCUMENT =
  '<announcements><announcement><title>'
  + '<script>window.__imported = true;</script>'
  + '</title></announcement></announcements>';

/**
 * A document that is not well-formed XML at all.
 *
 * A browser XML parser rejects it. If this screen parsed before sending, this document could never
 * reach the server - and the server is the only thing entitled to refuse it, because the structure
 * rule lives there.
 */
const MALFORMED_DOCUMENT = '<announcements><announcement><title>unclosed';

/**
 * A document declaring an external entity.
 *
 * The classic entity-expansion vector. It is sent verbatim precisely because it is never parsed here.
 */
const ENTITY_BEARING_DOCUMENT =
  '<?xml version="1.0"?><!DOCTYPE root [<!ENTITY external SYSTEM "file:///etc/passwd">]><root>&external;</root>';

/** The empty document, which this screen refuses before it spends a request on it. */
const EMPTY_DOCUMENT = '';

/** A file name carrying path traversal and markup, neither of which this screen resolves or renders. */
const HOSTILE_FILE_NAME = '../../<img src=x onerror="window.__named=true">.xml';

// =====================================================================================================
// HOSTILE SERVER WORDING
//
// ⚠ THE SERVER'S OWN SENTENCES ARE TREATED AS UNTRUSTED TOO, AND THAT IS NOT PARANOIA — IT CLOSES A
// MEASURED VULNERABILITY. `Library/Components/Skins/ModuleMessage.vb:L150` assigned its message
// straight onto a label with no encoding of any kind, and legacy resource values are demonstrably not
// inert: across the in-scope resource files a substantial minority carry an HTML tag and at least one -
// `SiteSettings.ascx.resx` → `Advertising.Text` - holds a live advertising SCRIPT block with a REMOTE
// source, stored HTML-escaped so a naive search for it comes back clean. A problem document is composed
// from stored state and from request values, so a sentence arriving off the wire can carry any of that.
//
// The property under test is therefore the same one the document fixtures above test, applied to the
// other direction of travel: every sentence is bound as TEXT, so hostile wording becomes visible
// characters and NO ELEMENT is ever constructed from it.
// =====================================================================================================

/** Bold markup: the cheapest possible proof that an element was or was not constructed. */
const HOSTILE_BOLD = '<b>x</b>';

/** An executable script element. If any sentence were bound as markup, this would prove it by running. */
const HOSTILE_SCRIPT = '<script>window.__wording = true;</script>';

/**
 * A sentence carrying a leading legacy break element, both spellings, plus hostile markup.
 *
 * MIGRATION: THE LEGACY ACCUMULATED BREAK ELEMENTS INSIDE ITS MESSAGES. `Website/admin/Users/User.ascx.vb:L187`
 *   used the self-closing spelling, `Website/admin/Portal/Signup.ascx.vb:L193` and L214/L221/L323 used
 *   the bare one, and L191-L196 appended ONE PER INVALID CHARACTER inside a loop; `editroles.ascx`
 *   carries them inside validator message attributes at L31/L67/L92/L95/L110/L113 and beyond, so they
 *   arrive in the per-field map's VALUES as well as in the sentence. The shared problem-document utility
 *   strips them, and the two spellings are both included here so neither survives by being overlooked.
 */
const BREAK_PREFIXED_DETAIL = `<br><br/>The submitted content could not be read. ${HOSTILE_BOLD}`;

/**
 * A validation problem document, in the exact shape the API emits for a rejected request.
 *
 * ⚠ `errors` IS REQUIRED ON THIS TYPE, which is the whole reason the contract publishes a distinct one:
 * a function that needs the map should not have to test whether it is there. The keys are the server's
 * .NET model-state keys reproduced byte for byte — PASCAL-CASED, because they name model members rather
 * than JSON members and the camel-case body policy does not reach dictionary keys.
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
 * Wraps rows in the shared paged envelope.
 *
 * ⚠ THE PAYLOAD MEMBER IS `items`, NOT `data`, AND THE DISTINCTION IS LOAD-BEARING. Every
 * single-resource route answers `{ data, meta }`, but a paged collection's body IS the page envelope
 * itself, whose records live under `items` - `paged-result.model.ts:L493-L505` reads `response.items`
 * and substitutes an EMPTY ARRAY when it is absent. A fixture spelling it `data` therefore flushes
 * successfully, unwraps to no records at all, and every assertion about the picker then fails against
 * an empty listing rather than against the screen. The declared type is the contract's own, so this
 * cannot drift from it silently.
 */
function pagedBody(items: readonly ModuleListItem[]): PagedResponse<ModuleListItem> {
  return {
    items,
    meta: { totalCount: items.length, pageIndex: 0, pageSize: 100, totalPages: 1 },
  };
}

/**
 * One listing row.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so module zero is the
 * first module of an installation: a truthiness test on the chosen identifier would silently refuse
 * to import into it, and so would a comparison against zero or a positivity test.
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
 * A problem document in the exact shape the API emits.
 *
 * ⚠ NO `instance` MEMBER, and `type` ALWAYS PRESENT - both are properties of the real factory rather
 * than of this fixture.
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
 * Yields one turn of the event loop's TASK queue, with no timer and no clock.
 *
 * ⚠ WHY NOT A ZERO-DELAY TIMER. A document read resolves off a task rather than a microtask, so
 * awaiting resolved promises alone never reaches it; a real task has to run. The obvious way to queue
 * one introduces WALL-CLOCK TIME, and time is the one input a specification cannot hold still - under
 * load a zero-delay timer fires whenever the machine gets to it, which makes the number of turns a
 * property of the hardware rather than of the code. A message posted to a private channel is queued by
 * the event loop directly: it is a genuine task, it is ordered, and no duration is expressible in it.
 * Nothing in this file therefore depends on a clock, a timer, a random value or the current date.
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
 * Narrows an outgoing request body to a plain object, by THROWING rather than by asserting.
 *
 * ⚠ THE BODY IS TYPED AS UNKNOWN AT THE TESTING BACKEND, AND THAT IS THE POINT. The transport cannot know
 * what a caller composed, so the only honest way to read a member is to establish that the body IS a plain
 * object first. A cast would assert what this narrowing proves, and would then read a member off something
 * that might be a form upload, a stream or nothing at all - which is precisely the mistake the multipart
 * assertions elsewhere in this file exist to rule out.
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
 * One member of an outgoing request body, read by BRACKET ACCESS.
 *
 * Bracket access is not a style choice: a record's keys are only ever known at runtime and
 * `noPropertyAccessFromIndexSignature` is enabled for this workspace, so dot access on one would not
 * compile.
 *
 * ⚠ RETURNS UNKNOWN RATHER THAN A GUESSED TYPE, so every call site compares against a literal and no
 * assertion smuggles in an assumption about what the member holds. That matters most for the members whose
 * whole point is that `0`, `-1` and `''` are real values.
 *
 * @param body The request body.
 * @param key The member to read.
 * @returns Whatever the member holds, including null and undefined.
 */
function bodyMember(body: unknown, key: string): unknown {
  return asRecord(body)[key];
}

/**
 * Narrows an element lookup by THROWING rather than by asserting.
 *
 * ⚠ THIS EXISTS SO THAT NO NON-NULL ASSERTION AND NO `any` APPEARS ANYWHERE IN THIS FILE. Element
 * lookups are typed as "the element or nothing", and the two usual ways of getting past that - a
 * non-null assertion or a cast - are claims the compiler cannot check: if the element is genuinely
 * absent, the failure surfaces later as an unreadable property access on nothing rather than as the
 * lookup that failed. Throwing narrows the type HONESTLY, and the message names the selector, so a
 * missing element fails the case at the exact line that looked for it.
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
 * The trimmed visible text of an element, or the empty string when it has none.
 *
 * ⚠ TESTED AGAINST NULL EXPLICITLY, never for truthiness, because the empty string is a legitimate
 * value for a text node and the schema this migration reads treats `''` as data rather than as absence
 * (`Library/Components/Shared/Null.vb:L71-L75` returns the empty string as its absence marker, which is
 * exactly why nothing here may conflate the two).
 *
 * @param element The element to read.
 * @returns The trimmed text.
 */
function visibleText(element: Element): string {
  const text = element.textContent;

  return text === null ? '' : text.trim();
}

/**
 * An element's OWN text, excluding anything its child elements contribute.
 *
 * ⚠ THIS DISTINCTION IS NECESSARY RATHER THAN FASTIDIOUS. The shared field renders its requiredness
 * marker INSIDE the label element - deliberately, so the marker's word joins the accessible name and
 * travels with the control - so the label's full text is the wording followed by that marker. Asserting a
 * measured resource value against the full text would therefore fail against a field that is behaving
 * exactly as designed, and the only ways to make it pass would be to weaken the assertion to a substring
 * match or to change the shared component. Reading the direct text nodes compares the measured value
 * against the wording and nothing else, which is what the parity claim is actually about.
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
 * A `File` whose text read rejects.
 *
 * No real file can be made unreadable from inside a browser, so the one behaviour that needs a
 * failing read gets a genuine `File` with its own `text` replaced. Everything else about it - its
 * name, its type, its identity as a `File` - is real, so the component's narrowing still holds.
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

  beforeEach(async () => {
    mounted = null;

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

    httpMock.verify();
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

  /**
   * A control looked up by its identifier, narrowed by throwing rather than asserted.
   *
   * Delegates to {@link requireElement} so the whole file has ONE narrowing mechanism and no case
   * reaches for a non-null assertion.
   */
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

  /**
   * Chooses a document.
   *
   * A file input's selection cannot be assigned from script, so the change handler is driven with a
   * genuine `Event` whose target carries a real `FileList` built through `DataTransfer` - which is the
   * one browser API that can produce one. The component narrows its event target with `instanceof`, so
   * the target must be the real input element.
   */
  function chooseDocument(file: File): void {
    const input = requiredControl<HTMLInputElement>('module-import-file');
    const transfer = new DataTransfer();

    transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /**
   * Lets a genuine document read complete.
   *
   * ⚠ THE READ IS REAL BROWSER I/O AND IS NOT A TASK THE FRAMEWORK CAN SEE. `File.text()` resolves off
   * a blob read that zone tracking does not instrument, so `whenStable()` alone reports the fixture idle
   * while the read is still outstanding - and a case asserting at that moment finds no request and then
   * "proves" that nothing was sent, which is the exact opposite of the truth. Yielding the event loop a
   * few times covers the read without a fake clock, which would defeat the point of reading a real file.
   *
   * ⚠ THE YIELD IS TIMER-FREE, AND THAT IS A CORRECTNESS PROPERTY RATHER THAN A STYLE PREFERENCE. A
   * timer-based yield introduces WALL-CLOCK TIME into a specification, and time is the one input a test
   * cannot control: a machine under load turns a zero-delay timer into an arbitrary one, so the number of
   * turns needed becomes a property of the machine instead of a property of the code.
   * {@link yieldMacrotask} posts a message to itself instead, which is a genuine task queued by the
   * event loop with no clock involved at all - so this helper is deterministic, and every value it
   * depends on is fixed.
   *
   * A rejected read needs none of this - a rejected promise is a microtask - so this is deliberately
   * tolerant rather than exact: it settles as soon as the work is done and costs nothing when it already
   * was.
   *
   * ⚠⚠ THE TURN COUNT IS A BOUND, NOT A MEASUREMENT, AND IT MUST STAY GENEROUS. A blob read completes
   * in however many event-loop turns the browser needs, and that number is not a property of this
   * screen - it rises with whatever else is contending for the loop. At four turns this helper passed
   * in isolation and failed roughly once per full-suite run: with several thousand specs sharing one
   * Karma page the read had not landed by the fourth turn, the case that happened to be running found
   * no request, and Jasmine's randomised ordering moved the victim from run to run. A fixed count
   * tuned against an idle machine is not a synchronisation primitive.
   *
   * Each turn is one `whenStable()` plus one already-elapsed timeout, so an unnecessary turn costs
   * essentially nothing and the loop exits as soon as the work is done regardless. The bound is
   * therefore set well above anything a read needs rather than close to it. Do NOT lower it, and do
   * not replace it with a fake clock: the point of this suite is that it reads a REAL file.
   */
  async function settle(turns = 32): Promise<void> {
    for (let turn = 0; turn < turns; turn += 1) {
      await fixture.whenStable();
      await yieldMacrotask();
    }

    fixture.detectChanges();
  }

  /**
   * The action bearing the given wording, narrowed by throwing.
   *
   * Both actions are looked up BY THEIR RENDERED WORDING rather than by a class or a position, which is
   * what makes the label assertions and the interaction assertions the same assertion: a case that finds
   * and presses the control labelled `Import` has already proved that wording is on screen.
   */
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

    await settle();
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
   * Reveals the guidance the shared field keeps behind a disclosure.
   *
   * The field renders its help text only once its toggle is pressed, so a specification asserting the
   * measured guidance has to press it. Locating the toggle through the WRAPPER that contains the named
   * control is what keeps this correct on a screen carrying two fields: a bare selector would find the
   * first toggle on the page rather than the one belonging to the field under assertion.
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

  /**
   * Every sentence the SUMMARY surface is currently showing.
   *
   * Deliberately separate from {@link fieldMessages}, because the division of labour between the two
   * surfaces is itself under test: the inline surface carries a requirement about ONE control, the
   * summary surface carries the server's outcome, and no sentence may appear in both.
   */
  function bannerMessages(): readonly string[] {
    return [
      ...queryAll('.error-banner__title'),
      ...queryAll('.error-banner__message'),
      ...queryAll('.error-banner__detail'),
    ].map((node) => visibleText(node));
  }

  /**
   * Every severity the notification queue was asked to announce a given sentence at.
   *
   * ⚠ THIS EXISTS SO THAT SEVERITY CAN BE ASSERTED IN BOTH DIRECTIONS. Asserting only that a sentence WAS
   * announced at one band leaves open that it was also announced at another, which is exactly the mistake
   * worth catching for a refusal of authority: it must be a warning and it must NOT be an error. Reading
   * the recorded arguments also makes the assertion insensitive to arity - the convenience methods pass two
   * arguments and the failure method passes three - so a matcher spelling the wrong number of arguments
   * cannot report a false absence.
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
   * The announcing region the shared summary surface owns.
   *
   * ⚠ ASSERTED AS ALWAYS PRESENT, NEVER AS CONDITIONALLY CREATED. `module-import.component.html:125`
   * mounts the surface unconditionally precisely so the region exists before it has anything to say:
   * creating a live region and its content in one instant is the case assistive technology most often
   * fails to announce.
   */
  function liveRegion(): HTMLElement {
    return requireElement<HTMLElement>(root(), '.error-banner-live');
  }

  /** The one outstanding transfer request. */
  function expectImport(): TestRequest {
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

    it('offers a choice for every listed module', () => {
      arrive([listRow(), listRow({ moduleId: 1, moduleTitle: 'Links' })]);

      const options = Array.from(requiredControl<HTMLSelectElement>('module-import-module').options);

      // ⚠ NO PLACEHOLDER OPTION. The picker offers exactly the modules there are, so a submission with
      // nothing chosen is prevented by the control's own requirement rather than by a sentinel value
      // that could collide with module zero.
      expect(options.map((option) => visibleText(option))).toEqual([
        'Announcements',
        'Links',
      ]);
    });

    it('reports having nothing to import into rather than offering an empty picker', () => {
      arrive([]);

      expect(query('#module-import-module')).toBeNull();
      expect(visibleText(requireElement(root(), 'app-empty-state'))).toContain(NO_MODULES_MESSAGE);
    });

    it('accepts only XML documents at the picker, and says so in the attribute', () => {
      arrive();

      // A hint to the file dialogue and nothing more: the attribute is advisory, the operator can
      // always override it, and the server remains the authority on structure. That is why nothing on
      // this screen refuses a document by its extension.
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
      // opposite would encode a screen this application does not have. `canSubmit()` is
      // `!busy() && hasModuleChoices() && !loadingModules()` - it consults the LISTING and the two
      // waits, never the form - so an operator who has chosen nothing can still press it, and pressing
      // is precisely what surfaces both requirements in the two cases above. That is the accessible
      // arrangement as well: a disabled control announces nothing about why it cannot be used, whereas
      // a press that answers with two field messages says exactly what is missing.
      expect(submitControl()?.disabled).withContext('nothing chosen yet').toBeFalse();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));

      expect(submitControl()?.disabled).withContext('both choices made').toBeFalse();
    });

    it('withholds the submit control while the listing is still being read', () => {
      create();

      // The listing read is outstanding: there is nothing to choose from yet, so there is nothing the
      // command could act on. This is the one state in which it is genuinely unavailable before a
      // transfer begins, and the indicator beside it is what says why.
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

      // ⚠ THE WHOLE BODY, COMPARED AS A WHOLE, so a fifth member would fail here. The target module
      // is ZERO - a real module - and it is transmitted as zero rather than being refused by a
      // truthiness test. `folder` is `null` because the target has no server-side folder concept left
      // to name: the legacy screen offered a folder dropdown reading the portal's own directory list,
      // and the migrated API exposes no folder resource at all. `fileName` carries the chosen
      // document's own name as the descriptive metadata the contract documents it to be.
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

      // ⚠ CHARACTER FOR CHARACTER. Not escaped, not sanitised, not re-serialised, not unwrapped. The
      // legacy screen handed the ROOT ELEMENT'S INNER MARKUP to the portability contract
      // (`Import.ascx.vb:L200`); doing that here would put one decision in two places.
      expect(body.content).toBe(SCRIPT_BEARING_DOCUMENT);

      // ⚠ AND NONE OF IT REACHED THE DOCUMENT. If any part of this screen rendered the content, this
      // payload would have executed and left its mark on the window.
      expect((window as unknown as Record<string, unknown>)['__imported'])
        .withContext('the content was never evaluated')
        .toBeUndefined();

      // ⚠ THE ASSERTION IS ABOUT THE ELEMENT TREE. No script element was CONSTRUCTED from the content,
      // which is the property that matters: a browser that had parsed this payload into the page would
      // have created one whether or not it also executed.
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

      // `Import.ascx.vb:L188-L193` built a document object and treated a load failure as the
      // invalid-structure refusal. That judgement is now server-side, so this screen must not
      // pre-empt it: a browser that parsed first would make the document unreachable and the server's
      // own `module.content_invalid` refusal unobservable.
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

      // A document that reads as empty or whitespace-only can only ever be refused, so it is refused
      // HERE rather than spending a request and an upload of the whole file to be told so. The message
      // is the screen's own and is deliberately distinct from the unreadable-document one, because the
      // operator resolves the two differently.
      expect(notifySpy).toHaveBeenCalledWith('error', FILE_EMPTY_MESSAGE, null);
      httpMock.expectNone(() => true);

      // ⚠ THIS DOES NOT WEAKEN THE RULE THAT AN EMPTY STRING IS DATA. Nothing on this screen rewrites
      // an empty string into null on its way out; the point is that no empty content reaches a request
      // at all, so there is no coercion of one to observe.
    });

    it('carries a hostile document name as metadata without resolving or rendering it', async () => {
      arrive();

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT, HOSTILE_FILE_NAME));

      await submit();

      const call = expectImport();
      const body = call.request.body as { fileName: string };

      // ⚠ THE NAME COMES FROM THE OPERATOR'S FILESYSTEM AND IS UNTRUSTED INPUT. It is carried as
      // descriptive metadata that no decision depends on: it is never resolved as a path - the target
      // has no folder concept to resolve one against - and never rendered as markup.
      //
      // The browser may normalise a name at the point of construction, so the traversal is asserted
      // as "whatever the browser produced, unaltered by this screen" rather than as a fixed string.
      expect(body.fileName).toBe(documentFile(BENIGN_DOCUMENT, HOSTILE_FILE_NAME).name);

      expect((window as unknown as Record<string, unknown>)['__named'])
        .withContext('the name was never evaluated')
        .toBeUndefined();

      // ⚠ THE ASSERTION IS ABOUT THE ELEMENT TREE, NOT ABOUT THE TEXT OF THE MARKUP. Interpolation
      // escapes the angle brackets, so the name's characters are all still on screen - `onerror=` among
      // them, as ordinary text, and always will be. Asserting the absence of those CHARACTERS would
      // therefore fail against a screen behaving perfectly, and would pass only if the wording were
      // silently rewritten. What actually matters is that the name became a TEXT NODE and NO ELEMENT was
      // constructed from it, which is what these assert: the tree gained no image element, no bold
      // element and no script element, while the literal characters remain visible.
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

      // ⚠ NOTHING IS SENT. A read can genuinely fail - the document may have been moved, renamed or
      // made unreadable between being chosen and being submitted - and a request carrying no content,
      // or carrying the string "undefined", would be worse than no request at all.
      httpMock.expectNone(() => true);

      // ⚠ THE THIRD ARGUMENT IS THE ASSERTION, NOT AN ARTEFACT OF THE CALL. A failure is the one
      // outcome that carries a support reference, so the queue accepts one — and this failure has
      // NONE to carry, because the document could not be read in the browser and no request was ever
      // made for a server to correlate. `null` is therefore the truthful value, and pinning it here
      // is what stops a later change quoting a reference the operator could not use.
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

      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE);
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

      // MIGRATION: SUCCESS FEEDBACK IS ADDED. `Import.ascx.vb:L151` redirected on success and said nothing
      //   at all, so an operator could not tell a completed import from a navigation that had simply lost
      //   their input. The measured legacy message vocabulary is three-valued - 27 error, 21 warning and
      //   12 success sites across the in-scope screens - so a completed import belongs at SUCCESS severity
      //   rather than being announced as information.
      //
      // ⚠ ASSERTED IN BOTH DIRECTIONS, like the refusal above: it IS success, and it is announced at no
      // other band.
      expect(severitiesAnnouncedFor(IMPORT_SUCCEEDED_MESSAGE)).toEqual(['success']);
      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE);

      // `Import.ascx.vb:L151` redirected on success, and again at L202 from inside its helper - a
      // redirect issued mid-computation, which is why that helper's remaining branches could never be
      // reached once it fired. The navigation here is that intent expressed once.
      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE]);
    });

    it('synthesises no listing row from the payload it just sent', async () => {
      arrive([listRow({ moduleId: 3, moduleTitle: 'Announcements' })]);

      chooseModule('Announcements');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      expectImport().flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ THE OUTCOME IS THE TRANSPORT'S COMPLETION SIGNAL AND NOTHING ELSE. The endpoint answers no
      // content, so there is no representation to adopt; a screen that composed one from the request it
      // had just sent would be asserting the server's state from the client's intention. This screen
      // leaves for the listing instead, and the listing reads for itself when it opens - which is why NO
      // further request is issued from here.
      httpMock.expectNone(() => true);

      // MIGRATION: THE EMPTY-STRING-MEANS-SUCCESS TEST IS CORRECTED. `L150` read `If strMessage = ""` as
      //   proof of success - the absent-string marker of `Null.vb:L71-L75` pressed into service as a status
      //   flag - so any path that failed to set a message was indistinguishable from one that succeeded.
      //   Success is now the transport's own completion signal; no string is compared against the empty
      //   string to decide an outcome anywhere on this screen.
      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE]);
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
      expect(navigateSpy).toHaveBeenCalledOnceWith([MODULE_LIST_ROUTE]);
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

      // ⚠ 400 AND NOT 409. `ModulesController` declares 200, 201, 204, 400, 401, 403, 404 and 500 on
      // its content-transfer actions and NO 409 anywhere, and the status mapper sends this code to
      // 400 by default because none of its conflict fragments matches. A fixture claiming 409 would
      // describe a response this API cannot send.
      expect(query('.error-banner')).not.toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a module that cannot receive content at 400, beside the module field', async () => {
      await refuseWith(
        problem('module.not_portable', 400, 'This module does not support content transfer.'),
      );

      // The legacy gate at `Import.ascx.vb:L177` read `objModule.IsPortable`, which
      // `ModuleInfo.vb:L608` derives by masking a bit out of a feature word. The listing contract this
      // picker reads exposes NO resolved portability flag, so the attempt is always allowed and the
      // server's refusal is what an operator sees - which is why this screen contains no bitwise
      // expression of any kind.
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

      // MIGRATION: the legacy access-denied screen rendered BOTH of its branches at warning severity
      // (`Website/admin/Security/AccessDenied.ascx.vb:L41-L47`) - and performed no permission check of
      // its own at all - while the legacy message renderer gave warning the ordinary heading style and
      // reserved the red one for errors (`Library/Components/Skins/ModuleMessage.vb:L115-L158`, whose
      // `NormalRed` class sits under the comment "text style used for error messages"). Two independent
      // proofs that the legacy application itself treated a refusal as distinct from a fault, and that
      // distinction is preserved: the system is working exactly as configured and the operator simply may
      // not do this.
      const refusal = 'The authenticated caller is not permitted to perform this operation.';

      // ⚠ ASSERTED IN BOTH DIRECTIONS. It IS a warning, and it is NOT an error. The positive assertion
      // alone would still pass if the sentence were ALSO announced as a fault, which is the misreport
      // worth catching - so the recorded severities are read as a whole and compared exactly.
      expect(severitiesAnnouncedFor(refusal))
        .withContext('a refusal of authority is announced once, as a warning')
        .toEqual(['warning']);
      expect(severitiesAnnouncedFor(refusal)).not.toContain('error');
      expect(notifySpy).toHaveBeenCalledWith('warning', refusal);
      expect(notifySpy).not.toHaveBeenCalledWith('error', refusal);
      expect(notifySpy).not.toHaveBeenCalledWith('error', refusal, null);

      // The shared surface reaches the same classification independently, so the two never disagree.
      expect(requireElement(root(), '.error-banner').getAttribute('data-severity'))
        .withContext('the surface bands it as a refusal, not as a fault')
        .not.toBe('danger');

      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a module that no longer exists at 404', async () => {
      await refuseWith(problem('module.not_found', 404, 'The requested resource does not exist.'));

      // MIGRATION: THE SILENT NO-OP IS CORRECTED, AND THIS IS A DELIBERATE BEHAVIOURAL CHANGE RATHER THAN
      //   AN INCIDENTAL ONE. `Import.ascx.vb:L148` opened its transfer with `If Not objModule Is Nothing
      //   Then` and declared NO `Else` branch at all, so an import aimed at a module that had since been
      //   deleted fell straight through: nothing was written, nothing was reported, and the screen then
      //   redirected as though the transfer had succeeded. An operator had no way to tell the difference.
      //   A module the server cannot find is now a not-found response and surfaces as a refusal like any
      //   other - the summary surface says so, and the screen does NOT leave.
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
      expect(notifySpy).not.toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE);
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
    it('leaves without validating anything and without sending anything', () => {
      arrive();

      cancel();

      // `import.ascx:L16` marks the abandon action `causesvalidation="False"` and its handler does
      // nothing but redirect, so nothing is marked touched, no validator is re-evaluated and no
      // message appears.
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
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE MEASURED WORDING, AND THE MARKUP IT IS RENDERED IN
  //
  // Every sentence below is traced to a measured legacy value. None is invented, and none is imported
  // from the component: the component exports none of them, so a specification reading them from it
  // could not detect a change to them.
  // ---------------------------------------------------------------------------------------------------

  describe('the measured wording', () => {
    it('titles the screen exactly as the control-title resource does, and heads it once', () => {
      arrive();

      // `ControlTitle_importmodule.Text`. The resource key is mode-dependent - the same control carried a
      // different title per mode - so this is the import mode's own value rather than a shared one.
      expect(visibleText(requireElement(root(), 'h1'))).toBe(IMPORT_TITLE);

      // MIGRATION: `ModuleHelp.Text` stored a top-level heading followed by a paragraph. Its heading
      //   duplicated this title exactly, so ONLY its sentence is carried across - which is why exactly
      //   one top-level heading is emitted rather than two.
      expect(queryAll('h1')).withContext('one top-level heading, not two').toHaveSize(1);
      expect(visibleText(root()))
        .withContext('the help resource sentence, re-authored as text')
        .toContain(IMPORT_SUBTITLE);
    });

    it('labels the document field from its own resource values, without the legacy colon', () => {
      arrive();

      // `plFile.Text` is 'File' and `plFile.Help` is 'Select the import file'.
      //
      // MIGRATION: `import.ascx:L6` and `L10` both appended a colon through the legacy label control's
      //   suffix attribute. The wording carries none and the shared field normalises the punctuation, so
      //   a colon is never authored twice and never doubled.
      //
      // Read as the label's OWN text, because the shared field renders its requiredness marker inside the
      // label element on purpose - see {@link ownText}.
      const label = requireElement<HTMLLabelElement>(root(), 'label[for="module-import-file"]');

      expect(ownText(label)).withContext('the measured label, colon-free').toBe(FILE_FIELD_LABEL);
      expect(ownText(label)).not.toContain(':');

      // ⚠ THE GUIDANCE IS A DISCLOSURE RATHER THAN ALWAYS-VISIBLE TEXT, so it has to be revealed before it
      // can be read. That is the shared field's own design - a keyboard-reachable button that reverses a
      // legacy defect, since `labelcontrol.ascx` withdrew its help affordance from the tab order entirely -
      // and this specification honours it rather than asserting against a field that does not exist.
      revealHelpFor('module-import-file');

      // The guidance BEGINS with the measured value; the screen appends the published byte limit, because
      // a limit an operator cannot see is one they can only meet by accident. Asserting equality would
      // oblige this file to restate the limit, where it would drift from the contract that owns it.
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

      // `cmdImport.Text` is 'Import' and is local to this screen's twelve entries.
      //
      // MIGRATION: A FIFTH RESOURCE-KEY CONVENTION. `import.ascx:L16` names the key `cmdCancel`, yet no
      //   such entry exists among those twelve - it falls through to the shared global resource table,
      //   where the value is 'Cancel'. A local key resolving against the global table is a convention
      //   beyond the four previously catalogued, and it is reported rather than assumed.
      //
      // Both lookups THROW when the wording is absent, so finding the controls at all is the assertion.
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

      // MIGRATION: `import.ascx:L4` opened a fixed-width positioning container carrying the summary
      //   "Edit Links Design Table" - a description of a completely different screen, because the markup
      //   was copied from one. Tabular markup used for positioning announces rows and cells that are not
      //   data, and a summary describing another screen is worse than none. The measured in-scope ratio
      //   is 68 positioning containers against 8 genuine record grids, so this is the ordinary case; the
      //   shared record-grid component is deliberately NOT used here.
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

      // MIGRATION: the legacy help affordance was a 344-byte image and the three message bands each had
      //   their own; all four are a documented non-port. Icons here are text, inline vector or stylesheet
      //   only, and the single asset that ships is the site icon.
      expect(query('img')).withContext('no raster icon').toBeNull();
      expect(query('picture')).toBeNull();
    });

    it('introduces no eleventh shared component', () => {
      arrive();

      // The shared library is CLOSED at ten members - nine of which are elements and one a structural
      // directive - so a screen may compose them but may not grow the set. Every custom element in the
      // rendered tree is therefore either this screen's own host or one of the nine.
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
        expect(permitted.has(tag)).withContext(`<${tag}> is a member of the closed set`).toBeTrue();
      }

      // And the ones this screen actually composes are present, so the assertion above is not vacuous.
      expect(rendered).toContain('app-page-header');
      expect(rendered).toContain('app-form-field');
      expect(rendered).toContain('app-error-banner');
    });

    it('offers exactly one document control, wrapped and natively named', () => {
      arrive();

      // MIGRATION: THIS NATIVE CONTROL IS THE SINGLE DOCUMENTED EXCEPTION to the rule that a feature
      //   template uses a shared component wherever one covers the need. The closed set contains no
      //   document picker, so the native control is used - WRAPPED in the shared field, never loose -
      //   and no eleventh shared component was created for it. The two actions are the same exception
      //   for the same reason: the set contains no button component either.
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

      // ⚠ THE ACCESSIBLE NAME IS A REAL LABEL, ASSOCIATED TWO WAYS, AND IT IS NOT BLANK. The legacy
      // control carried a name too - `import.ascx:L10` bound its label to `cboFiles` - so this is
      // continuity rather than an addition; the legacy association was the framework's and this one is the
      // platform's. The label element points AT the control by identifier, and the shared field
      // additionally writes the reverse reference onto the projected control, so the name survives
      // whichever direction a reader resolves it from.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — THE THREE MEASURED REFUSAL SENTENCES
  //
  // ⚠ THESE THREE SENTENCES ARE THE PARITY CLAIM OF THE WHOLE SCREEN. Each is a value from the twelve
  // entries of `Website/admin/Modules/App_LocalResources/Import.ascx.resx`, reproduced character for
  // character, and each is asserted where the screen ACTUALLY renders it.
  //
  // ⚠ A DIVERGENCE FROM THE STATED EXPECTATION, RECORDED RATHER THAN PAPERED OVER. These sentences render
  // BESIDE THE FIELD THEY CONCERN, not in the summary surface: `module-import.component.ts:L322-L350`
  // routes the two document codes to the document field and the portability code to the module field, and
  // `L1148-L1156` resolves the wording through the shared conflict table. The summary surface carries the
  // SERVER'S OWN sentence for the same refusal. Both are announced - the field region through its alert
  // role, the summary region through the live region it owns - and the two are asserted separately below,
  // because asserting the measured wording in the surface that does not carry it would be asserting a
  // screen this application does not have.
  // ---------------------------------------------------------------------------------------------------

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

      // Legacy key `NotValidXml`, assigned at `Import.ascx.vb:L192`. The legacy KEY does not travel - the
      // API publishes its own vocabulary - but the WORDING does, exactly.
      expect(fieldMessages())
        .withContext('the measured sentence, character for character')
        .toContain(NOT_VALID_XML_MESSAGE);

      // ⚠ "SELECTED", not "specified". The import screen's own two sentences say "selected" while its
      // sibling transfer screen says "specified"; normalising either would be an unrequested wording
      // change dressed up as consistency.
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

      // Legacy key `ImportNotSupported`, which `L208` and `L214` both produced - L214 when the module
      // carried no business controller or was not portable, L208 when the resolved controller turned out
      // not to implement the portability contract. Both arrive as ONE code, which is correct: the
      // distinction was about how the server discovered the module could not accept content, not about
      // anything an operator can act on.
      expect(fieldMessages()).toContain(IMPORT_NOT_SUPPORTED_MESSAGE);
      expect(IMPORT_NOT_SUPPORTED_MESSAGE).toContain('module selected');
    });

    it('renders the wrong-type sentence at 422 as well as at 400', async () => {
      // The status mapper sends this code to 400 by default, and a validation-shaped refusal of the same
      // request arrives at 422. The WORDING is chosen from the code and is therefore identical either
      // way, which is the property worth pinning: a status change must not silently change a sentence.
      await refuse(
        CONTENT_TYPE_MISMATCH_CODE,
        422,
        'The submitted content does not belong to this module.',
      );

      expect(fieldMessages()).toContain(NOT_CORRECT_TYPE_MESSAGE);
    });

    it('announces the server sentence through the region the summary surface owns', async () => {
      await refuse(CONTENT_INVALID_CODE, 400, 'The submitted content could not be read.');

      // ⚠ ONE LIVE REGION, AND THE FEATURE TEMPLATE DECLARES NONE OF ITS OWN. The shared surface owns the
      // announcing semantics; this screen neither declares a second region nor a redundant alert role on
      // the same node.
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
      //   as does `role="alert"` and indeed any accessibility attribute at all. Nothing was carried
      //   across here because there was nothing to carry.
    });

    it('re-implements no local catch-all sentence, because the problem contract subsumes it', async () => {
      await refuse('module.import_failed', 500, 'The content could not be imported.');

      // MIGRATION: `Import.ascx.vb` wrapped every handler in a catch-all funnelled through the framework's
      //   exception reporter (L86, L131, L161), and the helper's own bare `Catch` at L210-L211 flattened
      //   every remaining fault to the single sentence 'An error occurred during the import'. Neither is
      //   reproduced. Faults now arrive as RFC 7807 documents from ONE server-side handler, and the shared
      //   surface renders the server's own sentence, its per-field messages and the reference an operator
      //   can quote - none of which the flattened sentence could carry.
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
      //   measured `asp:ValidationSummary` count is ZERO across the 39 in-scope screens, zero across
      //   `Website/admin/Modules/` in particular, and zero anywhere in either legacy tree - so this surface
      //   is a net addition rather than a translation of anything. Its nearest real analogue is the message
      //   renderer at `Library/Components/Skins/ModuleMessage.vb`, which sits in a tree the migration
      //   excludes outright. The inline surface is equally net-new here: the measured validator census
      //   under `Website/admin/Modules/` is zero required-field, zero regular-expression, zero custom and
      //   zero range validators, and only four comparison validators - all four in the settings screen -
      //   so `import.ascx`'s seventeen content lines carry no validator markup whatsoever. The single check
      //   the legacy screen performed was the imperative test at `Import.ascx.vb:L145`, whose failure
      //   branch rendered a hard-coded literal. Both surfaces below therefore carry a real declarative rule
      //   where the legacy carried an `If`.

      // ⚠ THE DIVISION OF LABOUR. The requirement concerns ONE control, so it belongs beside that control
      // and nowhere else. Nothing was sent, so the summary surface has no server outcome to carry and
      // must show nothing at all.
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

      // ⚠ THE KEYS ARE .NET MODEL-STATE KEYS AND ARE NOT CAMEL-CASED. `Content` carries the capital
      // because it names a model member rather than a JSON member, and the camel-case body policy does not
      // reach dictionary keys. One key carries TWO messages, because the server always writes an array.
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
      // `noPropertyAccessFromIndexSignature` is enabled, so dot access would not compile at all. The rule
      // earns its keep: a key is only ever known at runtime, and dot access would let a typo compile as a
      // silent absence.
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

      // MIGRATION: THE LEGACY ACCUMULATED BREAK ELEMENTS INSIDE ITS MESSAGES, IN BOTH SPELLINGS.
      //   `Website/admin/Users/User.ascx.vb:L187` used the self-closing form; `Signup.ascx.vb:L193` and
      //   L214/L221/L323 used the bare one, and L191-L196 appended ONE PER INVALID CHARACTER inside a
      //   loop; `editroles.ascx` carries them inside validator message attributes, so they arrive in the
      //   per-field map's VALUES as well as in the sentence. Both spellings are stripped, by the shared
      //   utility, in one place.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 11 — SERVER WORDING IS UNTRUSTED INPUT TOO
  //
  // ⚠ THIS GROUP CLOSES A MEASURED VULNERABILITY RATHER THAN A HYPOTHETICAL ONE.
  // `Library/Components/Skins/ModuleMessage.vb:L150` assigned its message straight onto a label with no
  // encoding of any kind, and legacy resource values are demonstrably not inert: across the in-scope
  // resource files a substantial minority carry an HTML tag and at least one - `SiteSettings.ascx.resx`
  // → `Advertising.Text` - holds a live advertising SCRIPT block with a REMOTE source, stored escaped so
  // a naive search for it comes back clean. A problem document is composed from stored state and from
  // request values, so a sentence arriving off the wire can carry any of that.
  // ---------------------------------------------------------------------------------------------------

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
      // markup, the tree would carry the elements and one of them would have executed. The lookups are
      // spelled out against the rendered tree rather than routed through a helper, because they are the
      // load-bearing claim of this entire group and are worth reading literally.
      expect(root().querySelector('b')).withContext('no bold element was constructed').toBeNull();
      expect(root().querySelector('script'))
        .withContext('no script element was constructed')
        .toBeNull();
      expect(root().querySelector('img')).withContext('no image element was constructed').toBeNull();
      expect(root().querySelector('iframe')).withContext('and no frame either').toBeNull();
      expect((window as unknown as Record<string, unknown>)['__wording'])
        .withContext('no injected script was evaluated')
        .toBeUndefined();

      // ⚠ AND THE LITERAL CHARACTERS ARE VISIBLE, which is what proves it rendered as escaped plain text
      // rather than having been silently discarded. An assertion that only checked for the absence of the
      // elements would also pass against a screen that dropped the sentence entirely.
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

      // ⚠ THE UPLOADED DOCUMENT'S NAME IS THE OPERATOR'S OWN INPUT TRAVELLING OUT AND BACK. It is chosen
      // on their machine, carried on the request as descriptive metadata, and may be echoed into a refusal
      // - so it is untrusted in both directions and is bound as text in both.
      expectRenderedAsText(HOSTILE_BOLD, HOSTILE_SCRIPT);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 12 — SENTINEL DISCIPLINE
  //
  // ⚠ THE SHARPEST RULE IN THIS MIGRATION, AND THE EASIEST TO BREAK SILENTLY. `dbo.Modules.ModuleID` is
  // `IDENTITY(0, 1)`, so ZERO NAMES A REAL MODULE; the legacy absent-integer marker is MINUS ONE
  // (`Library/Components/Shared/Null.vb:L41-L45`) and the legacy absent-string marker is the EMPTY STRING
  // (`L71-L75`, whose body is literally a return of `""`). A truthiness test, a positivity test, or a
  // coalesce to zero or to minus one would silently corrupt the payload in a way no type checker can see.
  // ---------------------------------------------------------------------------------------------------

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
      // absent-integer marker AND a legitimate identifier - `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`,
      // so the value is a real key elsewhere in the very same schema. Whatever the form holds is what
      // travels.
      arrive([listRow({ moduleId: -1, moduleTitle: 'A legacy placement' })]);

      chooseModule('A legacy placement');
      chooseDocument(documentFile(BENIGN_DOCUMENT));
      await submit();

      const call = expectImport();
      const body: unknown = call.request.body;

      expect(bodyMember(body, 'moduleId')).withContext('minus one travels verbatim').toBe(-1);
      expect(bodyMember(body, 'moduleId')).not.toBeNull();

      // MIGRATION: AND MINUS ONE IS NEVER REINTRODUCED AS AN "UNSET" MARKER. `Import.ascx.vb:L51` declared
      //   its target as `Private Shadows ModuleId As Integer = -1` - an identifier seeded with the absence
      //   marker. Nothing on this screen initialises an identifier to minus one, tests one against it, or
      //   coalesces one to it; the form opens holding NOTHING, which is what "nothing chosen yet" honestly
      //   is, and the requirement is what reports it.
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

      // ⚠ THE EMPTY STRING IS DATA, NOT ABSENCE. The legacy absent-string marker IS the empty string, so a
      // stored `''` and a stored null are indistinguishable through the legacy path and neither the domain
      // model nor the wire is free to fold one into the other. The API serialises absent members as
      // present-and-null rather than omitting them, so a member arriving as `''` means `''`.
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

      // ⚠ A BODY STATUS OF ZERO IS DATA. A proxy or a gateway between the browser and the API can compose
      // an error body this application never produced, and zero is a value it can carry. The severity
      // resolver tests for null and undefined EXPLICITLY and sends everything else - zero included - to
      // its default band, so a truthiness test in its place would misreport zero as a missing status.
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

      // `Import.ascx.vb:L166-L172` filled a folder dropdown from the portal's own directory list,
      // labelling the root folder - whose stored path is the empty string, the absence marker of
      // `Null.vb:L71-L75` - as `Root`. The migrated API exposes no folder listing, no file listing, no
      // upload browse and no disk-space resource, so there is nothing for it to read.
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

// =====================================================================================================
// THE DELEGATED ROUTE-ORDERING REGRESSION PROOF
//
// ⚠⚠ THIS GROUP IS NOT ABOUT THIS COMPONENT. It guards the FEATURE'S HIGHEST-RISK DEFECT, and it lives
// here because no specification of its own exists for `../module.routes.ts`.
//
// THE DEFECT IT GUARDS. The router matches in DECLARATION ORDER and `:moduleId` matches any single
// segment, so `import` must be declared above it. Were the parameterised route declared first,
// `/modules/import` would match IT, and the edit screen would be handed the string 'import' as the
// record to load. That is a runtime failure with NO compile error, NO type error and NO build warning -
// the route objects are all well-formed and the configuration is valid. `/modules/import` is the ONLY
// address in this application whose literal segment sits at the same depth as a parameterised sibling,
// which makes this barrel the strictest instance of the rule. The sort key is neither alphabetical nor
// by length: it is literal segments before parameterised ones.
//
// ⚠⚠ THE ROUTE TABLE AND THE ROUTE GATE ARE READ, NEVER WRITTEN. Neither `../module.routes.ts` nor
// `../../../core/guards/permission.guard.ts` is edited by this work - not to make an assertion pass and
// not for any other reason. Where reality and expectation differed, reality is what is asserted and the
// difference is recorded, which is what the D-MI-1 note below does.
//
// ⚠ THE UPWARD IMPORT IS DELIBERATE AND IS THE ONLY ONE IN THIS FILE. `MODULE_ROUTES` is imported from
// one level up so that the assertions exercise the REAL route table rather than a hand-built copy of it -
// a copy would agree with itself for ever while the real table drifted. It is not a cross-FEATURE import
// and creates no dependency between sibling screens.
// =====================================================================================================

describe('MODULE_ROUTES — the delegated ordering regression proof', () => {
  /** The addresses whose relative order is load-bearing. */
  const LIST_PATH = '';
  const CREATE_PATH = 'new';
  const IMPORT_PATH = 'import';
  const RECORD_PATH = ':moduleId';

  /**
   * The position of a declared path, established to EXIST before its order is compared.
   *
   * ⚠ THE EXISTENCE CHECK IS THE WHOLE POINT OF THIS HELPER, AND OMITTING IT IS THE CLASSIC WAY THIS
   * ASSERTION ROTS. A search that finds nothing answers minus one, and minus one is LESS THAN every real
   * position - so a naive `expect(indexOf(a)).toBeLessThan(indexOf(b))` passes with flying colours after
   * somebody DELETES route `a` entirely. The whole regression it was written to prevent then ships green.
   * Throwing here makes a missing route fail at the line that looked for it.
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
   * The loader is declared as a function returning either a promise of a component or the component
   * itself, so both shapes are awaited; awaiting a non-promise is harmless and covers the eager form
   * without a second branch. The result is narrowed by throwing rather than asserted, so an entry that
   * declares no loader at all fails HERE rather than producing an unreadable comparison later.
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
   * The policy a route declares, read WITHOUT reaching for `any`.
   *
   * ⚠ THE ROUTER TYPES ROUTE DATA AS AN INDEX SIGNATURE ONTO `any`, so the value arrives with every
   * compile-time guarantee switched off: it could be absent on a route that forgot the key, or a number,
   * or an object, and none of that would be caught. Widening to `unknown` discards that false confidence
   * and forces the narrowing to be written out - which is also the only way to read it at all here, since
   * `any` is not available. Bracket access is likewise required rather than preferred:
   * `noPropertyAccessFromIndexSignature` is enabled, so reading the key as a property would not compile.
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

      // ⚠ NOT ALPHABETICAL AND NOT BY LENGTH. 'import' sorts after ':moduleId' by code point and is
      // shorter than ':moduleId/export', so either of those rules would produce an order that breaks the
      // address. Stated as an assertion so the reason cannot be lost.
      expect(IMPORT_PATH > RECORD_PATH)
        .withContext('the correct order is the OPPOSITE of alphabetical here')
        .toBeTrue();
    });

    it('resolves the two adjacent addresses to genuinely different screens', async () => {
      const importComponent = await resolvedComponent(routeFor(IMPORT_PATH));
      const recordComponent = await resolvedComponent(routeFor(RECORD_PATH));

      // ⚠ IDENTITY, NOT NAME. Comparing the resolved classes is what proves the two addresses are distinct
      // destinations; comparing their names would pass against two different classes that happened to
      // share one, which a minifier can arrange.
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

  // ---------------------------------------------------------------------------------------------------
  // ASSERTION (a), CONTINUED — THE POLICY THE ADDRESS DECLARES
  //
  // ⚠⚠ D-MI-1, REPORTED AND RESOLVED IN THE ROUTE TABLE RATHER THAN BLOCKED.
  //
  // The hazard is real: `permission.guard.ts:L682-L689` resolves a SCOPE IDENTIFIER for the record-scoped
  // policies and FAILS CLOSED when the route carries none - it announces a refusal and returns false. The
  // module-scoped policies resolve from exactly ONE parameter name, `moduleId`, with NO fallback to a bare
  // identifier (`L184-L205` records that an earlier revision listed such a fallback, that no such fallback
  // exists server-side, and that accepting one would let the gate authorise a different record from the one
  // the endpoint authorised). `/modules/import` carries NO route parameter at all.
  //
  // The consequence is therefore concrete: had this address declared the module-scoped policy, the screen
  // would be unreachable to EVERY caller - including a host account - with the build green and the route
  // object present in the configuration. `module.routes.ts:L39-L49` records exactly that reasoning, and the
  // address declares the TENANT-WIDE policy instead, taken from the endpoint that actually serves it. The
  // tenant-wide policy resolves no scope (`permission.guard.ts:L333-L334`), so the gate has an answerable
  // question and the live proof below can pass.
  //
  // The assertions below therefore pin the RESOLUTION rather than the hazard: this address declares the
  // tenant-wide policy, the parameterised sibling declares the module-scoped one, and no parameterless
  // address declares a scoped policy. Asserting the module-scoped policy on THIS route - which an earlier
  // reading of the brief expected - would have asserted a screen nobody can reach.
  // ---------------------------------------------------------------------------------------------------

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

  // ---------------------------------------------------------------------------------------------------
  // ASSERTION (b) — THE LIVE PROOF
  //
  // The structural proof above establishes the ORDER. This one establishes the CONSEQUENCE: a genuine
  // navigation to the genuine address, through the genuine table, activating the genuine screen.
  //
  // ⚠ THE GATE IS NEUTRALISED THROUGH ITS INJECTED DEPENDENCIES AND NOTHING ELSE. The route table is used
  // exactly as authored - no parameter is fabricated, no policy is overridden, no `:moduleId` is added to
  // the import address, and the gate itself is not replaced. Only the identity the gate CONSULTS is
  // doubled, which is the one substitution that leaves the behaviour under test intact.
  // ---------------------------------------------------------------------------------------------------

  describe('navigating to the address', () => {
    /** The mount point the application uses, so the address under test is the real one. */
    const MOUNT_PATH = 'modules';

    /**
     * A host carrying nothing but an outlet.
     *
     * Declared locally rather than in a shared helper file: it is scaffolding for ONE proof, and a shared
     * fixture would be a file this work is not authorised to create. Standalone, like every component in
     * this workspace - there is no module declaration anywhere in the target.
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

      /**
       * A signal double that is CALLABLE, because that is what a signal is.
       *
       * A plain property would satisfy the type checker and then throw the moment the gate read it, since
       * every member the gate consults is invoked. Returning a fixed value keeps the whole proof
       * deterministic - no clock, no timer and no random value is involved anywhere.
       */
      const fixedSignal = <T,>(value: T): Signal<T> => {
        const read = (): T => value;

        return read as Signal<T>;
      };

      const authStore = {
        isAuthenticated: fixedSignal(true),
        currentUser: fixedSignal<CurrentUser | null>(administrator),
        isSuperUser: fixedSignal(true),
        roles: fixedSignal<readonly string[]>(['Administrators']),
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

      // The gate's OTHER dependency. Spied rather than replaced, because a refusal announced HERE is the
      // observable signature of the fail-closed branch - so recording it is how these cases prove the
      // branch was not taken.
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
     * Every refusal the gate announced.
     *
     * ⚠ THIS IS THE DIRECT OBSERVABLE OF THE FAIL-CLOSED BRANCH. All three of the gate's refusal paths -
     * an unusable policy declaration, an unresolvable scope, and a caller the client can already see lacks
     * the administration the policy requires - announce the SAME sentence at warning severity before
     * returning false. An empty result therefore proves none of them was taken, which is a stronger claim
     * than merely observing that a navigation returned true.
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
     * The activated route chain, root first.
     *
     * ⚠ THE CHAIN IS WALKED DOWNWARDS FROM THE ROOT, AND THE OBVIOUS ALTERNATIVE IS A TRAP. Reading
     * `pathFromRoot` off the ROOT snapshot answers an array containing only the root itself - it is that
     * snapshot's own ancestry, not the tree beneath it - so every assertion made against it looks at a
     * snapshot carrying no configuration and no parameters. Such an assertion does not fail loudly: it
     * quietly finds nothing, and a case asserting the ABSENCE of a parameter then passes for the wrong
     * reason. Descending through the first child of each level reaches the leaf the address actually
     * activated.
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
      // what WOULD happen had this address declared a record-scoped policy it carries no scope for. The
      // route table declares the tenant-wide policy instead, so the question is answerable and the address
      // is reachable.
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
      // policy. The chain is walked from the root DOWNWARDS - see {@link activatedChain} for why the
      // obvious alternative would make this assertion pass vacuously.
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
      //
      // ⚠ DELIBERATELY NAVIGATED WITHOUT AN OUTLET, and the reason is scope rather than convenience. What
      // this case has to establish is that the ROUTER resolves the address, that the GATE admits it, and
      // that the identifier BINDS - all of which the router settles on its own. Mounting the destination
      // would additionally instantiate a screen belonging to a sibling folder, drag in its own data reads
      // and its own view of the session, and make this case fail whenever THAT screen changed. This file
      // asserts the route table, not a neighbour's screen.
      const router = TestBed.inject(Router);
      const navigated = await router.navigateByUrl(`/${MOUNT_PATH}/42`);

      expect(navigated).withContext('the record address still resolves').toBeTrue();
      expect(router.url).toBe(`/${MOUNT_PATH}/42`);

      // The address matched the PARAMETERISED route and not the literal one, which is the collision this
      // group exists to keep impossible - in the other direction.
      //
      // ⚠ THE LEAF, NOT THE FIRST NON-EMPTY PATH IN THE CHAIN. The chain begins with the router's own root
      // and then the group's MOUNT path, so a search for the first non-empty entry answers `modules` and
      // never reaches the child that actually matched.
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
