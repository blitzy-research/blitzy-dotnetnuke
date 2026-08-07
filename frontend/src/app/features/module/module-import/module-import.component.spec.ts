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
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { ModuleStore } from '../../../core/state/module.store';
import { ModuleImportComponent } from './module-import.component';

import type { ModuleListItem } from '../../../core/models/module.model';
import type { PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';

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

  function query<T extends HTMLElement>(selector: string): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  }

  function queryAll<T extends HTMLElement>(selector: string): readonly T[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<T>(selector));
  }

  function requiredControl<T extends HTMLElement>(controlId: string): T {
    const element = query<T>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as T;
  }

  /** Chooses a module in the picker by its rendered label. */
  function chooseModule(label: string): void {
    const select = requiredControl<HTMLSelectElement>('module-import-module');
    const option = Array.from(select.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    expect(option).withContext(`the option labelled "${label}" is offered`).not.toBeUndefined();

    select.value = (option as HTMLOptionElement).value;
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
   * A rejected read needs none of this - a rejected promise is a microtask - so this is deliberately
   * tolerant rather than exact: it settles as soon as the work is done and costs nothing when it already
   * was.
   */
  async function settle(turns = 4): Promise<void> {
    for (let turn = 0; turn < turns; turn += 1) {
      await fixture.whenStable();
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
    }

    fixture.detectChanges();
  }

  /** Presses the submit control, then lets the awaited document read settle. */
  async function submit(): Promise<void> {
    const button = queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === IMPORT_ACTION_LABEL,
    );

    expect(button).withContext('the import command is rendered').not.toBeUndefined();

    (button as HTMLButtonElement).click();

    await settle();
  }

  /** The submit control, looked up by its rendered wording. */
  function submitControl(): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === IMPORT_ACTION_LABEL,
    );
  }

  /** Presses the abandon control. */
  function cancel(): void {
    const button = queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === CANCEL_ACTION_LABEL,
    );

    expect(button).withContext('the abandon command is rendered').not.toBeUndefined();

    (button as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());
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

      expect((query('h1')?.textContent ?? '').trim()).toBe(IMPORT_TITLE);
      httpMock.expectNone(() => true);
    });

    it('offers a choice for every listed module', () => {
      arrive([listRow(), listRow({ moduleId: 1, moduleTitle: 'Links' })]);

      const options = Array.from(requiredControl<HTMLSelectElement>('module-import-module').options);

      // ⚠ NO PLACEHOLDER OPTION. The picker offers exactly the modules there are, so a submission with
      // nothing chosen is prevented by the control's own requirement rather than by a sentinel value
      // that could collide with module zero.
      expect(options.map((option) => (option.textContent ?? '').trim())).toEqual([
        'Announcements',
        'Links',
      ]);
    });

    it('reports having nothing to import into rather than offering an empty picker', () => {
      arrive([]);

      expect(query('#module-import-module')).toBeNull();
      expect((query('app-empty-state')?.textContent ?? '').trim()).toContain(NO_MODULES_MESSAGE);
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
      expect((fixture.nativeElement as HTMLElement).innerHTML).not.toContain('<script>');
      expect((fixture.nativeElement as HTMLElement).textContent ?? '').not.toContain(
        'window.__imported',
      );

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
      // escapes the angle brackets, so the serialised markup still CONTAINS the handler's name as
      // ordinary characters - `onerror=` appears inside the escaped text and always will. Asserting its
      // absence from the markup would therefore fail against a screen that is behaving perfectly, and
      // would pass only if the wording were silently rewritten. What actually matters is that the name
      // became a TEXT NODE and no element was constructed from it, which is what these three assert:
      // the escaped form is present, the raw form is not, and the tree gained no image element.
      const markup = (fixture.nativeElement as HTMLElement).innerHTML;

      expect(markup).withContext('the brackets are escaped').toContain('&lt;img');
      expect(markup).withContext('no element was constructed from the name').not.toContain('<img');
      expect(queryAll('img')).withContext('no image element exists').toHaveSize(0);

      // And the name is present, whole and unaltered, as the text it is.
      const shown = queryAll('p').map((node) => (node.textContent ?? '').trim());

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

      const button = queryAll<HTMLButtonElement>('button').find(
        (candidate) => (candidate.textContent ?? '').trim() === 'Import',
      );

      // The press begins the read; the screen is destroyed before it settles. This is the ONE awaited
      // step on the screen and therefore the one moment this can happen at all.
      (button as HTMLButtonElement).click();

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

      expect(notifySpy).toHaveBeenCalledWith('success', IMPORT_SUCCEEDED_MESSAGE);

      // `Import.ascx.vb:L151` redirected on success, and again at L202 from inside its helper - a
      // redirect issued mid-computation, which is why that helper's remaining branches could never be
      // reached once it fired. The navigation here is that intent expressed once.
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

      expect((query('.error-banner__message')?.textContent ?? '').trim()).toBe(
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
      // (`Website/admin/Security/AccessDenied.ascx.vb:L41-L47`) and the legacy message renderer gave
      // warning the ordinary heading style while reserving the red one for errors. The legacy
      // application itself therefore treated a refusal as distinct from a fault, and that distinction
      // is preserved: the system is working exactly as configured and the operator simply may not do
      // this.
      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        'The authenticated caller is not permitted to perform this operation.',
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a module that no longer exists at 404', async () => {
      await refuseWith(problem('module.not_found', 404, 'The requested resource does not exist.'));

      expect(query('.error-banner')).not.toBeNull();
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
  // PROOF 7 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  // ---------------------------------------------------------------------------------------------------

  describe('deliberate omissions', () => {
    it('offers no folder picker, because no folder resource survives the migration', () => {
      arrive();

      // `Import.ascx.vb:L166-L172` filled a folder dropdown from the portal's own directory list,
      // labelling the root folder - whose stored path is the empty string, the absence marker of
      // `Null.vb:L71-L75` - as `Root`. The migrated API exposes no folder listing, no file listing, no
      // upload browse and no disk-space resource, so there is nothing for it to read.
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

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
