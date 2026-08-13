import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';

import { ModuleExportComponent } from './module-export.component';
import { MODULE_VISIBILITY } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';

import type { ModuleDetail, ModuleExportRequest } from '../../../core/models/module.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';
import type { NotificationSeverity } from '../../../core/services/notification.service';

/** The identifier used by every case that does not care which module is addressed. */
const MODULE_ID = 7;

/** The characters the legacy name sanitiser removed, in the order it removed them. */
const SANITISED_CHARACTERS: readonly string[] = [
  '.',
  ' ',
  '~',
  '`',
  '!',
  '@',
  '#',
  '$',
  '%',
  '^',
  '&',
  '*',
  '(',
  ')',
  '-',
  '_',
  '+',
  '=',
  '{',
  '[',
  '}',
  ']',
  '|',
  '\\',
  ':',
  ';',
  '<',
  ',',
  '>',
  '?',
  '/',
  '"',
  "'",
];

/**
 * A title carrying every removable character, each one separated by text that must survive.
 *
 * Interleaved rather than run together, which is what makes this stronger than a contiguous block: an
 * implementation that collapsed runs, or that stopped at the first match, or that removed a neighbour along
 * with the character would all produce a different result. The surviving segments are `k` followed by the
 * character's index, because letters and digits are both outside the removable set.
 */
const TITLE_WITH_EVERY_REMOVABLE_CHARACTER: string = SANITISED_CHARACTERS.map(
  (character, index) => `k${index}${character}`,
).join('');

/** What {@link TITLE_WITH_EVERY_REMOVABLE_CHARACTER} must sanitise to: the surviving segments, unaltered. */
const TITLE_WITH_EVERY_REMOVABLE_CHARACTER_CLEANED: string = SANITISED_CHARACTERS.map(
  (_character, index) => `k${index}`,
).join('');

/**
 * The scheme and namespace the API puts in front of every failure code it publishes.
 *
 * The code travels in the problem document's `type` member and nowhere else - there is no `code` member on
 * the document - so a specification that wanted to drive a code-specific branch has to build the `type`.
 * Lower case throughout, exactly as the server writes it.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** The failure code the API publishes when a module cannot export its content. */
const NOT_PORTABLE_CODE = 'module.not_portable';

// -------------------------------------------------------------------------------------------------------
// THE WORDING UNDER TEST
// -------------------------------------------------------------------------------------------------------
//
// Each constant below is the VALUE of a legacy resource entry, transcribed from the resource file rather
// than from the component, so that a component whose wording drifted fails here. The resource file is the
// authority even where the markup disagrees with it - see the note on EXPECTED_EXPORT_LABEL.

/** `Export.ascx.resx` key `ControlTitle_exportmodule.Text`. */
const EXPECTED_PAGE_TITLE = 'Export Module';

/** `Export.ascx.resx` key `plFile.Text`. */
const EXPECTED_FILE_LABEL = 'File';

/** `Export.ascx.resx` key `plFile.Help`. */
const EXPECTED_FILE_HELP = 'Enter the export filename';

/**
 * `Export.ascx.resx` key `cmdExport.Text`.
 *
 * D10 - THE MARKUP AND THE RESOURCE FILE DISAGREE, AND THE RESOURCE FILE WINS. `export.ascx:L15` declares
 * the export action with the inline attribute `text="Import"`, a copy-paste defect carried over from the
 * near-identical import screen. The same element carries `resourcekey="cmdExport"`, and the legacy page
 * framework overwrote the inline text with the resource value at run time, so an operator always read
 * "Export" and never saw the defect. Asserting "Export" is therefore asserting the behaviour that always
 * applied, not correcting it.
 */
const EXPECTED_EXPORT_LABEL = 'Export';

/**
 * The dismissing action's label.
 *
 * Absent from this screen's own resource file: it resolves from the shared global file
 * `Website/App_GlobalResources/SharedResources.resx:L138`, key `cmdCancel.Text`, which the legacy framework
 * consulted when a local entry was missing. The wording rule is unchanged by the fallback - the value still
 * comes from a resource entry rather than from the `text="Cancel"` attribute on `export.ascx:L16`.
 */
const EXPECTED_CANCEL_LABEL = 'Cancel';

/**
 * `Export.ascx.resx` key `Validation.Text`, asserted VERBATIM.
 *
 * The sentence names two things because the legacy gate at `Export.ascx.vb:L121` tested two -
 * `cboFolders.SelectedIndex <> 0 And txtFile.Text <> ""`. The folder half is gone with the folder picker, so
 * only the filename half is enforced; the sentence is nonetheless carried across unaltered, because the
 * operator-facing wording is part of the contract this migration preserves. The narrowing is the documented
 * divergence; the words are not. A reworded sentence must fail this expectation.
 */
const EXPECTED_VALIDATION_MESSAGE = 'You must specify a folder and file for export';

/**
 * `Export.ascx.resx` key `ExportNotSupported.Text`, asserted VERBATIM.
 *
 * NOTE THE WORD "specified". The sibling import screen's equivalent entry reads "selected"; the
 * inconsistency is in the legacy wording itself and is reproduced rather than harmonised, because
 * equivalent wording means the wording that was there.
 */
const EXPECTED_NOT_SUPPORTED_MESSAGE =
  'The module specified does not support the exporting of content';

/**
 * `Export.ascx.resx` key `NoContent.Text`, asserted VERBATIM.
 *
 * Reached by an EMPTY DOCUMENT on an otherwise successful response rather than by a status code. The legacy
 * branch at `Export.ascx.vb:L159` tested the module's payload with `Content <> ""` before wrapping it; the
 * API wraps an empty payload and answers successfully, so it publishes no failure code for this condition,
 * and both the client service and the store deliberately keep an empty document distinct from an absent
 * one. That distinction is what the component consumes. "specified" here too.
 */
const EXPECTED_NO_CONTENT_MESSAGE = 'The module specified does not have any content';

/**
 * The sentence shown when the address does not name a module.
 *
 * NET-NEW WORDING, and deliberately so: this is the D-M6 correction, and the condition it describes had no
 * message at all in the legacy screen because the legacy screen treated it as a success.
 */
const EXPECTED_NO_MODULE_MESSAGE = 'This address does not name a module to export.';

/** The sentence shown when a document was produced but could not be handed to the browser. */
const EXPECTED_DOWNLOAD_FAILED_MESSAGE = 'The export document could not be saved to your device.';

/** The greatest number of characters the filename field accepts, from `export.ascx:L11 maxlength="200"`. */
const EXPECTED_MAX_LENGTH = 200;

/**
 * The sentence shown when the filename is longer than the field accepts.
 *
 * NET-NEW WORDING, because the legacy condition was unreachable rather than unhandled: the legacy input
 * carried the same length attribute, so a browser refused the two-hundred-and-first character and no message
 * was ever needed. Composed from {@link EXPECTED_MAX_LENGTH} so the number cannot drift out of step with the
 * bound the field actually enforces.
 */
const EXPECTED_TOO_LONG_MESSAGE = `The export filename may be at most ${EXPECTED_MAX_LENGTH} characters.`;

/** The media type the exported document is offered under. */
const EXPECTED_MEDIA_TYPE = 'application/xml';

/**
 * The envelope every non-paged read on this API answers with.
 *
 * DECLARED LOCALLY RATHER THAN IMPORTED, and the reason is a scope rule rather than a preference: the
 * canonical `ApiResponse<T>` lives in `core/models/paged-result.model.ts`, which is not among the files this
 * specification declares a dependency on. Restating the two members here keeps the fixture fully typed
 * without widening this file's import surface, and `meta` is stated rather than omitted because the envelope
 * declares it as present-and-nullable for every response, paged or not.
 */
interface ModuleReadEnvelope {
  readonly data: ModuleDetail | null;
  readonly meta: null;
}

/**
 * A fully populated module, overriding only what a case cares about.
 *
 * THE THREE NAME FIELDS ARE DELIBERATELY ALL DIFFERENT, and that is the single most important property of
 * this fixture. The legacy class declared `ModuleTitle`, `FriendlyName` and `ModuleName` as three separate
 * properties with three separate XML element names ("title", "friendlyname", "modulename"); the screen seeds
 * its field from the TITLE and builds the document name from the programmatic NAME. A fixture whose values
 * coincided would pass with any of the three wired into either place.
 *
 * The title also carries removable characters, so the sanitiser is exercised by the default fixture rather
 * than only by the cases that name it.
 *
 * @param overrides The members to replace.
 * @returns The module as the read endpoint would return it.
 */
function moduleOf(overrides: Partial<ModuleDetail> = {}): ModuleDetail {
  return {
    moduleId: MODULE_ID,
    tabModuleId: 31,
    tabId: 12,
    portalId: 0,
    moduleDefId: 14,
    desktopModuleId: 9,
    moduleTitle: 'Latest News (2024)',
    allTabs: false,
    header: '<p>Header</p>',
    footer: '<p>Footer</p>',
    startDate: '2024-03-01T00:00:00Z',
    endDate: '2024-12-31T00:00:00Z',
    inheritViewPermissions: true,
    isDeleted: false,
    moduleOrder: 6,
    cacheTime: 120,
    iconFile: 'module.gif',
    visibility: MODULE_VISIBILITY.maximized,
    displayTitle: true,
    friendlyName: 'News Announcer',
    moduleName: 'Announcements',
    description: 'Announcement content',
    version: '01.00.00',
    ...overrides,
  };
}

/**
 * A refusal document as the API writes one.
 *
 * @param status The HTTP status the server chose.
 * @param detail The sentence describing this occurrence.
 * @param code The published failure code, or null when the server published none.
 * @returns The problem document.
 */
function problemOf(status: number, detail: string, code: string | null = null): ProblemDetails {
  const document: ProblemDetails = {
    title: 'Request rejected',
    status,
    detail,
    // The support reference, which is the one value joining what an operator saw to what the server logged.
    // Present on the fixture so the cases can prove it survives the screen substituting its own sentence.
    correlationId: '8f7c1b2d-4a6e-4f10-9c3b-5d2e7a1f0b64',
  };

  return code === null ? document : { ...document, type: `${FAILURE_TYPE_PREFIX}${code}` };
}

/**
 * A field-level refusal, whose per-field dictionary uses .NET model-state keys.
 *
 * THE KEY IS `FileName`, NOT `fileName`. Model-state keys name model members rather than JSON members, so
 * the camel-case naming policy that governs body properties does not apply to them and the server sends them
 * Pascal-cased. The shared matcher is what reconciles the two spellings, and this fixture is what proves it
 * has to. Every read of the dictionary in this file is an INDEX EXPRESSION, because the member is typed as an
 * index signature and property access on one is a compile error in this workspace.
 *
 * @param status The HTTP status the server chose.
 * @param messages The messages for the filename member.
 * @returns The validation problem document.
 */
function validationProblemOf(
  status: number,
  messages: readonly string[],
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.failed`,
    title: 'One or more validation errors occurred.',
    status,
    detail: 'The request was rejected.',
    correlationId: '2c9a4e77-1b58-4f3a-8de6-0a91c4b7e512',
    errors: { FileName: messages },
  };
}

/** A record of one attempted download, captured at the instant the anchor was activated. */
interface DownloadAttempt {
  /** The filename the document was offered under. */
  readonly fileName: string;

  /** The address the anchor pointed at, read as the raw attribute so it is not resolved against the page. */
  readonly href: string;

  /** Whether the anchor declared itself hidden, so activation cannot flash a visible element. */
  readonly hidden: boolean;

  /** The anchor's relationship declaration. */
  readonly rel: string;
}

describe('ModuleExportComponent', () => {
  let fixture: ComponentFixture<ModuleExportComponent>;
  let component: ModuleExportComponent;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy<(severity: NotificationSeverity, message: string) => void>;
  let navigateSpy: jasmine.Spy<Router['navigate']>;
  let clickSpy: jasmine.Spy<() => void>;

  let createdObjectUrls: string[] = [];
  let createdBlobs: Blob[] = [];
  let revokedObjectUrls: string[] = [];
  let downloadAttempts: DownloadAttempt[] = [];
  let objectUrlCounter = 0;

  // -----------------------------------------------------------------------------------------------------
  // READING THE SCREEN
  // -----------------------------------------------------------------------------------------------------
  //
  // Every assertion below reads the rendered document or the HTTP wire rather than the component's members,
  // and that is forced rather than chosen: the component publishes exactly ONE public member, its route
  // input. Everything else is protected or private, which is correct for a screen and means these
  // specifications exercise it the way an operator and the network do.

  /** The component's host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * The first element matching a selector, or null.
   *
   * @param selector A CSS selector.
   * @returns The element, or null when nothing matches.
   */
  function q<T extends HTMLElement>(selector: string): T | null {
    return host().querySelector<T>(selector);
  }

  /**
   * Every element matching a selector, in document order.
   *
   * @param selector A CSS selector.
   * @returns The elements.
   */
  function qa<T extends HTMLElement>(selector: string): readonly T[] {
    return Array.from(host().querySelectorAll<T>(selector));
  }

  /**
   * The collapsed text of the first element matching a selector, or null when nothing matches.
   *
   * Whitespace is collapsed because the templates wrap for readability and a rendered label therefore
   * carries newlines and indentation that no reader ever sees.
   *
   * @param selector A CSS selector.
   * @returns The text, or null.
   */
  function textOf(selector: string): string | null {
    const element = q(selector);

    return element === null ? null : (element.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  /** The filename input, or null when it is not rendered. */
  function fileInput(): HTMLInputElement | null {
    return q<HTMLInputElement>('#module-export-file-name');
  }

  /**
   * The filename input, having asserted that it is rendered.
   *
   * The expectation is what makes the narrowing below sound, so no assertion operator is needed anywhere in
   * this file: the caller receives a definitely-present element or the case has already failed.
   *
   * @returns The input.
   */
  function requireFileInput(): HTMLInputElement {
    const element = fileInput();

    expect(element).withContext('the filename input must be rendered').not.toBeNull();

    if (element === null) {
      throw new Error('the filename input was not rendered');
    }

    return element;
  }

  /**
   * The action carrying a label, from the form's action block.
   *
   * Located by its rendered text rather than by position, because the labels are exactly what several of
   * these cases are about and a positional lookup would keep passing if the two swapped places.
   *
   * @param label The action's visible text.
   * @returns The button, or null when no action carries that text.
   */
  function actionButton(label: string): HTMLButtonElement | null {
    return (
      qa<HTMLButtonElement>('.module-export__actions button').find(
        (button) => (button.textContent ?? '').trim() === label,
      ) ?? null
    );
  }

  /**
   * An action, having asserted that it is rendered.
   *
   * @param label The action's visible text.
   * @returns The button.
   */
  function requireActionButton(label: string): HTMLButtonElement {
    const button = actionButton(label);

    expect(button).withContext(`the '${label}' action must be rendered`).not.toBeNull();

    if (button === null) {
      throw new Error(`the '${label}' action was not rendered`);
    }

    return button;
  }

  /** Every field-level message the shared form field is currently showing, in document order. */
  function fieldMessages(): readonly string[] {
    return qa('.form-field__error').map((element) =>
      (element.textContent ?? '').replace(/\s+/g, ' ').trim(),
    );
  }

  /** The screen-level sentence in the polite live region, or null when the region is not rendered. */
  function notice(): string | null {
    return textOf('p[role="status"]');
  }

  /** The sentence the shared error banner is presenting, or null when it is presenting none. */
  function bannerMessage(): string | null {
    return textOf('.error-banner__message');
  }

  /** Every notification the screen has raised, in the order it raised them. */
  function raisedNotifications(): readonly { severity: NotificationSeverity; message: string }[] {
    return notifySpy.calls
      .allArgs()
      .map(([severity, message]) => ({ severity: severity, message: message }));
  }

  /** Every severity the screen has raised a notification at, in order. */
  function raisedSeverities(): readonly NotificationSeverity[] {
    return raisedNotifications().map((entry) => entry.severity);
  }

  /**
   * The one document the screen prepared, having asserted that it prepared exactly one.
   *
   * @returns The binary container the document was placed in.
   */
  function requireSingleBlob(): Blob {
    expect(createdBlobs.length).withContext('exactly one document must be prepared').toBe(1);

    const blob = createdBlobs.at(0);

    if (blob === undefined) {
      throw new Error('no document was prepared');
    }

    return blob;
  }

  /**
   * The one download the screen attempted, having asserted that it attempted exactly one.
   *
   * @returns The attempt.
   */
  function requireSingleDownload(): DownloadAttempt {
    expect(downloadAttempts.length).withContext('exactly one download must be offered').toBe(1);

    const attempt = downloadAttempts.at(0);

    if (attempt === undefined) {
      throw new Error('no download was offered');
    }

    return attempt;
  }

  /**
   * The one object URL the screen created, having asserted that it created exactly one.
   *
   * @returns The address.
   */
  function requireSingleObjectUrl(): string {
    expect(createdObjectUrls.length).withContext('exactly one object URL must be created').toBe(1);

    const objectUrl = createdObjectUrls.at(0);

    if (objectUrl === undefined) {
      throw new Error('no object URL was created');
    }

    return objectUrl;
  }

  // -----------------------------------------------------------------------------------------------------
  // DRIVING THE SCREEN
  // -----------------------------------------------------------------------------------------------------

  /**
   * Points the screen at a module, exactly as the router would.
   *
   * THE INPUT NAME IS `moduleId` AND NOTHING ELSE. Component input binding delivers a route parameter by
   * MATCHING THE INPUT'S NAME, so any other spelling compiles cleanly, raises no warning and simply never
   * receives the parameter. `setInput` refuses a name the component does not declare, which is what makes
   * every call below a standing proof of the name; one case asserts the refusal directly.
   *
   * The value is passed as a STRING by default because that is what a route parameter is. A number is
   * accepted too, so the cases that address a module directly can say so.
   *
   * @param moduleId The route parameter to supply.
   */
  function addressModule(moduleId: number | string): void {
    fixture.componentRef.setInput('moduleId', moduleId);
    fixture.detectChanges();
  }

  /**
   * The outstanding read of a module.
   *
   * Matched on the method and the path so that the presence of any query parameter is asserted separately
   * rather than being absorbed into the match.
   *
   * @param moduleId The module the screen should be reading.
   * @returns The parked request.
   */
  function expectModuleRead(moduleId: number = MODULE_ID): TestRequest {
    return httpMock.expectOne({ method: 'GET', url: `/api/v1/modules/${moduleId}` });
  }

  /**
   * Answers the read of a module and renders the result.
   *
   * @param detail The module to return, or null to answer successfully with no module.
   * @param moduleId The module being read.
   */
  function answerModuleRead(detail: ModuleDetail | null, moduleId: number = MODULE_ID): void {
    const envelope: ModuleReadEnvelope = { data: detail, meta: null };

    expectModuleRead(moduleId).flush(envelope);
    fixture.detectChanges();
  }

  /**
   * Opens the screen on a module: addresses it, answers its read, and renders.
   *
   * @param overrides The module members to replace.
   * @param moduleId The identifier to supply, as the route would.
   */
  function openOn(overrides: Partial<ModuleDetail> = {}, moduleId: number = MODULE_ID): void {
    addressModule(String(moduleId));
    answerModuleRead(moduleOf({ moduleId, ...overrides }), moduleId);
  }

  /**
   * Types into the filename field, the way an operator does.
   *
   * Driven through the DOM rather than through the control so that the control becomes dirty, which is the
   * condition under which the screen reveals field messages and stops re-seeding the suggestion.
   *
   * @param value The text to type.
   */
  function typeFileName(value: string): void {
    const element = requireFileInput();

    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Submits the form.
   *
   * The event is dispatched on the form rather than routed through the confirming action, so that a case
   * about validation is not also a case about which element is a submit control. That the confirming action
   * IS a submit control inside this form is asserted separately and structurally.
   */
  function submitForm(): void {
    const form = q<HTMLFormElement>('form.module-export__form');

    expect(form).withContext('the form must be rendered').not.toBeNull();

    if (form === null) {
      throw new Error('the form was not rendered');
    }

    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  /**
   * The outstanding export request.
   *
   * @param moduleId The module being exported.
   * @returns The parked request.
   */
  function expectExportRequest(moduleId: number = MODULE_ID): TestRequest {
    return httpMock.expectOne({ method: 'POST', url: `/api/v1/modules/${moduleId}/export` });
  }

  /**
   * Submits the form and answers the export successfully, rendering the result.
   *
   * THE STATUS IS STATED EXPLICITLY AS `200`, which is the whole point of stating it: this operation answers
   * with the document IN THE RESPONSE BODY, and it is the one exception to the status this API returns for
   * every other write. Letting the status default, or flushing the created status this API uses everywhere
   * else, would encode the wrong contract and still pass - so no case in this file leaves it unstated.
   *
   * @param content The document the API returns.
   * @param moduleId The module being exported.
   */
  function exportSucceedsWith(content: string, moduleId: number = MODULE_ID): void {
    submitForm();
    expectExportRequest(moduleId).flush(content, { status: 200, statusText: 'OK' });
    fixture.detectChanges();
  }

  /**
   * Submits the form and refuses the export, rendering the result.
   *
   * THE BODY IS SERIALISED BY HAND, and that is required rather than stylistic. The export response is read
   * as TEXT, because the document it carries is markup, so a problem document arrives at the client as an
   * unparsed string; the testing backend enforces the same rule and refuses to convert an object for a
   * text response. The store parses the string in one place, which is exactly the path these cases exercise.
   *
   * @param problem The refusal document.
   * @param statusText The reason phrase.
   * @param moduleId The module being exported.
   */
  function exportIsRefusedWith(
    problem: ProblemDetails,
    statusText: string,
    moduleId: number = MODULE_ID,
  ): void {
    const status: number | undefined = problem.status;

    // Read as an explicit absence test rather than coalesced to a default: a refusal fixture that named no
    // status would otherwise be flushed under a status nobody chose, and the severity every case below
    // asserts is derived from exactly that value.
    if (status === undefined) {
      throw new Error('a refusal fixture must state the status it was answered with');
    }

    submitForm();
    expectExportRequest(moduleId).flush(JSON.stringify(problem), { status, statusText });
    fixture.detectChanges();
  }

  beforeEach(async () => {
    // The real client FIRST, then the testing backend that displaces it. Reversing the order leaves the live
    // backend in place and every expectation times out against a request nothing intercepted.
    await TestBed.configureTestingModule({
      imports: [ModuleExportComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // Both collaborators are injected by the component's field initialisers, which run at construction, so
    // the spies have to be installed before the component is created. Both services are application-scoped
    // and the testing injector is rebuilt per case, so these are the very instances the component receives.
    //
    // `notify` is the single sink: the four convenience methods all delegate to it, so spying here records
    // every notification whatever route raised it. It calls through, so the service's own queue fills too.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // No routes are declared, so a real navigation would fail to match. The destination is what these cases
    // assert, not the navigation itself.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

    createdObjectUrls = [];
    createdBlobs = [];
    revokedObjectUrls = [];
    downloadAttempts = [];
    objectUrlCounter = 0;

    spyOn(URL, 'createObjectURL').and.callFake((source: Blob | MediaSource): string => {
      if (source instanceof Blob) {
        createdBlobs.push(source);
      }

      objectUrlCounter += 1;

      const objectUrl = `blob:module-export/${objectUrlCounter}`;

      createdObjectUrls.push(objectUrl);

      return objectUrl;
    });

    spyOn(URL, 'revokeObjectURL').and.callFake((objectUrl: string): void => {
      revokedObjectUrls.push(objectUrl);
    });

    // ANCHOR ACTIVATION IS INTERCEPTED SO NO REAL DOWNLOAD OR NAVIGATION HAPPENS. The spy is installed on
    // the anchor prototype rather than on the element prototype, so the screen's own two buttons keep their
    // real activation behaviour and the cancel cases still exercise a genuine click.
    //
    // The anchor is read out of the document rather than captured through the spy's receiver, which makes
    // this stronger as well as simpler: the component attaches the element before activating it and removes
    // it immediately afterwards, so FINDING it here is the proof that it was attached at the instant it was
    // activated. An implementation that activated a detached element would record nothing and fail.
    clickSpy = spyOn(HTMLAnchorElement.prototype, 'click').and.callFake((): void => {
      const anchor = document.body.querySelector<HTMLAnchorElement>('a[download]');

      if (anchor === null) {
        return;
      }

      downloadAttempts.push({
        fileName: anchor.download,
        href: anchor.getAttribute('href') ?? '',
        hidden: anchor.hidden,
        rel: anchor.rel,
      });
    });

    fixture = TestBed.createComponent(ModuleExportComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    // MANDATORY. Every request the screen issues is accounted for by the case that provoked it; an
    // unanswered one is a request the screen made and nobody expected, which is exactly the class of defect
    // a specification that only asserted the requests it knew about would miss.
    httpMock.verify();
  });

  // =====================================================================================================
  // THE ADDRESS
  // =====================================================================================================
  //
  // `Website/admin/Modules/Export.ascx.vb:L49` declared its target as
  // `Private Shadows ModuleId As Integer = -1` - an identifier field seeded with the legacy integer absence
  // marker - and `:L65-L66` read it from a request value under a lower-case key while the sibling settings
  // screen read the same value under a mixed-case one, which worked only because the legacy request
  // collection compared keys case-insensitively. Both facts matter here: minus one is a value a caller may
  // genuinely hold, and the parameter's spelling is now load-bearing in a way no compiler checks.
  describe('the address', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('reads the addressed module through a relative path, carrying no query parameter', () => {
      addressModule(String(MODULE_ID));

      const read: TestRequest = expectModuleRead();

      expect(read.request.method).toBe('GET');
      // RELATIVE, and asserted twice over so a query parameter cannot hide inside the match. The served
      // application reaches the API through a reverse proxy on its own origin, so no host belongs here.
      expect(read.request.url).toBe(`/api/v1/modules/${MODULE_ID}`);
      expect(read.request.urlWithParams).toBe(`/api/v1/modules/${MODULE_ID}`);
      expect(read.request.params.keys().length).toBe(0);
      expect(read.request.params.has('tabModuleId')).toBeFalse();

      const envelope: ModuleReadEnvelope = { data: moduleOf(), meta: null };

      read.flush(envelope);
      fixture.detectChanges();
    });

    it('treats module ZERO as a real module and exports it', () => {
      // `Modules.ModuleID` is declared `IDENTITY(0,1)`, so the first module ever created carries the
      // identifier zero. A guard written as `if (id)`, `id > 0` or `id ?? -1` compiles, raises nothing, and
      // silently refuses this module - which is why the whole path is driven here rather than only the URL.
      addressModule('0');

      const read: TestRequest = expectModuleRead(0);

      // BOTH ADDRESSES ARE WRITTEN OUT IN FULL rather than composed from a constant, so this case states the
      // relative paths literally and cannot drift along with a fixture value.
      expect(read.request.url).toBe('/api/v1/modules/0');

      const envelope: ModuleReadEnvelope = { data: moduleOf({ moduleId: 0 }), meta: null };

      read.flush(envelope);
      fixture.detectChanges();

      expect(requireFileInput().value).toBe('LatestNews2024');
      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeFalse();

      submitForm();

      const request: TestRequest = expectExportRequest(0);

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe('/api/v1/modules/0/export');

      request.flush('<content type="Announcements" />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      expect(createdBlobs.length).toBe(1);
      expect(downloadAttempts.length).toBe(1);
      expect(downloadAttempts.map((attempt) => attempt.fileName)).toEqual([
        'content.Announcements.LatestNews2024.xml',
      ]);
      expect(raisedSeverities()).toEqual(['success']);
    });

    it('transmits MINUS ONE as an ordinary identifier rather than reading it as "absent"', () => {
      // The legacy absence marker is minus one, and this API's contracts carry it as a transmitted value, so
      // the screen must not borrow it to mean "no module". It addresses it, reads it, and exports it.
      openOn({}, -1);

      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeFalse();
      expect(notice()).toBeNull();

      exportSucceedsWith('<content type="Announcements" />', -1);

      expect(downloadAttempts.length).toBe(1);
    });

    it('reports an address that names no integer at all, and issues no request for it', () => {
      // NOT-A-NUMBER IS THE ONLY USABLE MARKER FOR AN UNUSABLE PARAMETER, and that is forced rather than
      // preferred: zero and minus one are both legitimate identifiers, so neither can be borrowed to mean
      // "none". The legacy conversion at `Export.ascx.vb:L66` raised on non-numeric text and the enclosing
      // handler at `:L90` absorbed it into a generic page failure, so an operator saw no explanation.
      addressModule('not-a-module');

      expect(notice()).toBe(EXPECTED_NO_MODULE_MESSAGE);
      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeTrue();

      // Submitting anyway repeats the sentence and still sends nothing; the outstanding-request check at
      // teardown is what proves the second half.
      submitForm();

      expect(notice()).toBe(EXPECTED_NO_MODULE_MESSAGE);
      expect(fieldMessages()).toEqual([]);
    });

    it('declares its route input under exactly the name the router binds, and under no other', () => {
      openOn();

      expect(requireFileInput().value).toBe('LatestNews2024');

      // A DIFFERENT CASE IS A DIFFERENT INPUT. Component input binding matches a route parameter to an input
      // BY NAME, so a rename produces no compile error, no runtime error and no data - the screen simply
      // stays permanently unaddressed. The framework's refusal to set an undeclared name is the only
      // mechanism that can catch it, and every other case in this file leans on it implicitly.
      //
      // D-M5. `Export.ascx.vb:L65-L66` read the request value `"moduleid"` in lower case while the sibling
      // settings screen read `"ModuleId"` in mixed case; both worked only because the legacy request
      // collection compared keys case-insensitively. There is ONE spelling now, and this expectation is what
      // pins it: the two cannot drift apart without the binding simply not happening.
      expect(() => {
        fixture.componentRef.setInput('moduleID', MODULE_ID);
      }).toThrowError(/moduleID/);
    });

    it('publishes its identifier as a read-only signal', () => {
      openOn();

      // The input is the component's only public member, and it is readable but not writable: a consumer
      // cannot reach in and change which module the screen is showing. Tested by absence of the writing
      // members rather than by a cast, so nothing here weakens the type.
      expect(component.moduleId()).toBe(MODULE_ID);
      expect('set' in component.moduleId).toBeFalse();
      expect('update' in component.moduleId).toBeFalse();
    });
  });

  // =====================================================================================================
  // THE SUGGESTED FILENAME
  // =====================================================================================================
  //
  // `Export.ascx.vb:L86` is one line - `txtFile.Text = CleanName(objModule.ModuleTitle)` - and it carries two
  // facts that are easy to lose. The suggestion comes from the module's TITLE, which is not the field the
  // produced filename is built from; and it passes through a sanitiser that removes thirty-three characters,
  // four of which look harmless.
  describe('the suggested filename', () => {
    it('seeds the field from the module TITLE, sanitised', () => {
      openOn();

      // 'Latest News (2024)' loses its spaces and both brackets and keeps everything else.
      expect(requireFileInput().value).toBe('LatestNews2024');
    });

    it('removes every one of the thirty-three characters the legacy sanitiser removed, and only those', () => {
      // Interleaved rather than run together, so each character is proven individually with surviving text on
      // both sides of it. An implementation that collapsed runs, stopped at the first match, or removed a
      // neighbour would produce a different string here.
      openOn({ moduleTitle: TITLE_WITH_EVERY_REMOVABLE_CHARACTER });

      expect(SANITISED_CHARACTERS.length).toBe(33);
      expect(requireFileInput().value).toBe(TITLE_WITH_EVERY_REMOVABLE_CHARACTER_CLEANED);
    });

    it('removes the four characters a reader is most likely to assume are safe', () => {
      // The space, the full stop, the hyphen and the underscore are all in the legacy set. Preserving any one
      // of them changes every filename this screen produces, and none of the four would look wrong in a
      // diff, so they are named explicitly rather than left to the table above.
      openOn({ moduleTitle: 'Ann. News-Feed_v2 Beta' });

      expect(requireFileInput().value).toBe('AnnNewsFeedv2Beta');
    });

    it('removes the two characters the legacy supplied by code point', () => {
      // The legacy set is a thirty-one character literal followed by `Chr(34)` and `Chr(39)`, so the double
      // quote and the apostrophe are easy to miss when transcribing the line by eye.
      openOn({ moduleTitle: `He said "Ann's News"` });

      expect(requireFileInput().value).toBe('HesaidAnnsNews');
    });

    it('keeps letters, digits and non-ASCII text, because the legacy set was not extended', () => {
      openOn({ moduleTitle: 'Nouvelles Générales 2024 · 漢字 · Ñandú' });

      // The middle dot and both accented forms survive: they are outside the legacy set, and widening it
      // would change names an operator may already have scripted against.
      expect(requireFileInput().value).toBe('NouvellesGénérales2024·漢字·Ñandú');
    });

    it('leaves the field empty for a title made entirely of removable characters', () => {
      // THE LEGACY DEFECT IS REPRODUCED, NOT REPAIRED, and the component documents that choice: the sanitiser
      // has no empty-result guard, no length cap and no trim, so a title of nothing but removable characters
      // sanitises away completely. The blank-name validator is what then refuses the submission, which is the
      // adjacent condition the component DOES fix - and it fixes it on the field rather than by inventing a
      // substitute name here.
      openOn({ moduleTitle: '... --- ___' });

      expect(requireFileInput().value).toBe('');

      submitForm();

      expect(fieldMessages()).toEqual([EXPECTED_VALIDATION_MESSAGE]);
    });

    it('leaves the field empty for a module carrying no title at all', () => {
      // The legacy guarded the assignment with `If Not objModule Is Nothing` and left the box empty rather
      // than failing; an absent title is treated the same way, which strict typing makes mandatory here.
      openOn({ moduleTitle: null });

      expect(requireFileInput().value).toBe('');
    });

    it('does not overwrite text the operator typed while the read was still in flight', () => {
      // The legacy prepopulated on a first render only and left the value untouched on every postback. A
      // pristine, untouched control expresses the same condition against a form instead of a page lifecycle,
      // and this is the case where the two genuinely differ: the field is reachable before the read settles
      // now, which it never was when the page was assembled on the server.
      addressModule(String(MODULE_ID));

      typeFileName('OperatorChoice');

      // The read settles afterwards and must not reach in and replace what was typed.
      answerModuleRead(moduleOf());

      expect(requireFileInput().value).toBe('OperatorChoice');
    });

    it('re-seeds from the new module when the address changes, discarding the previous suggestion', () => {
      // Every piece of per-address state is returned to its starting point when the parameter changes.
      // Discarding the loaded module is the part that matters most: without it the suggestion would be seeded
      // from the PREVIOUS module's title during the window before the new read returns, and an operator would
      // export one module under another's name.
      openOn({}, 4);

      expect(requireFileInput().value).toBe('LatestNews2024');

      addressModule('5');

      // Cleared while the second read is in flight, rather than left showing module four's suggestion.
      expect(requireFileInput().value).toBe('');

      answerModuleRead(moduleOf({ moduleId: 5, moduleTitle: 'Site Links' }), 5);

      expect(requireFileInput().value).toBe('SiteLinks');
    });
  });

  // =====================================================================================================
  // THE REQUEST
  // =====================================================================================================
  //
  // `Export.ascx.vb:L143` declared a private helper
  // `ExportModule(ModuleID As Integer, FileName As String, Folder As String) As String` whose return value
  // was a status string with the empty string overloaded to mean success. Three positional arguments become
  // one identifier in the path and one request object in the body, and the overloaded status string becomes
  // an HTTP status with a published failure code - which is what allows a missing module to be told apart
  // from a successful export at all.
  describe('the request', () => {
    it('posts to the addressed module through a relative path, with no query parameter', () => {
      openOn();
      submitForm();

      const request: TestRequest = expectExportRequest();

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(`/api/v1/modules/${MODULE_ID}/export`);
      expect(request.request.urlWithParams).toBe(`/api/v1/modules/${MODULE_ID}/export`);
      expect(request.request.params.keys().length).toBe(0);
      // The dropped folder picker has no counterpart anywhere on the wire, in the path or in a parameter.
      expect(request.request.params.has('folder')).toBeFalse();

      request.flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();
    });

    it('is answered with 200 and takes the document from the RESPONSE BODY', async () => {
      const exported =
        '<?xml version="1.0" encoding="utf-8" ?>' +
        '<content type="Announcements" version="01.00.00">' +
        '<announcement><title>Release</title><text></text></announcement>' +
        '</content>';

      openOn();
      submitForm();

      const request: TestRequest = expectExportRequest();

      // THE BODY IS THE PAYLOAD CHANNEL, which is the structural reason this operation cannot answer the way
      // every other write on this API answers. The response is read as TEXT because it carries markup; a
      // write that reported only a status and a location would have no way to deliver a payload at all.
      expect(request.request.responseType).toBe('text');

      // The status is stated rather than defaulted, so this case pins the success status this operation
      // publishes instead of inheriting whatever the harness would have chosen.
      request.flush(exported, { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      const blob: Blob = requireSingleBlob();

      // Byte for byte out of the response body and into the container handed to the browser.
      await expectAsync(blob.text()).toBeResolvedTo(exported);
      expect(blob.type).toBe(EXPECTED_MEDIA_TYPE);
      expect(requireSingleDownload().href).toBe(requireSingleObjectUrl());
    });

    it('sends exactly the two members the request contract declares, with no folder value', () => {
      openOn();
      typeFileName('Quarterly Report');
      submitForm();

      const request: TestRequest = expectExportRequest();

      // The contract declares `fileName` and `folder` and nothing else, so the body is asserted WHOLE rather
      // than member by member: a member added to the wire contract must fail here.
      const expected: ModuleExportRequest = { fileName: 'Quarterly Report', folder: null };

      // `toEqual` compares the key sets in both directions, so this expectation is what refuses a THIRD
      // member as well as a wrong value for either of the two.
      expect(request.request.body).toEqual(expected);

      // THE FOLDER MEMBER IS PRESENT AND NULL rather than absent, because the contract declares it - and it
      // is null because this screen has no folder to name. There is no folder control, no folder signal and
      // no folder browser: the picker at `export.ascx:L6-L7` is dropped with the filesystem it addressed, and
      // no folder-listing, file-listing, upload or disk-space endpoint exists for it to be filled from.
      expect(request.request.body).toEqual(jasmine.objectContaining({ folder: null }));

      request.flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();
    });

    it("transmits the operator's text exactly as typed, unsanitised", () => {
      // The API neither derives a name from this value nor stores anything under it: it labels the response
      // and is checked for presence. Sanitising before transmission would discard the operator's intent for
      // nothing. The sanitiser applies to the COMPOSED NAME, which is the value that becomes a filename.
      openOn();
      typeFileName('  Q1 2024 - Final_draft.v2  ');
      submitForm();

      const request: TestRequest = expectExportRequest();
      const expected: ModuleExportRequest = {
        fileName: '  Q1 2024 - Final_draft.v2  ',
        folder: null,
      };

      expect(request.request.body).toEqual(expected);

      request.flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      // ...while the produced filename is sanitised, including the surrounding spaces.
      expect(requireSingleDownload().fileName).toBe('content.Announcements.Q12024Finaldraftv2.xml');
    });

    it('sets no header of its own on either request', () => {
      // THE CORRELATION IDENTIFIER IS THE INTERCEPTOR'S, NOT THIS SCREEN'S. Features set no headers at all,
      // which is what keeps one identifier on every outbound request instead of one per call site that
      // remembered. No interceptor is registered in this harness, so any header seen here was set by the
      // screen - and there are none.
      addressModule(String(MODULE_ID));

      const read: TestRequest = expectModuleRead();

      expect(read.request.headers.keys()).toEqual([]);
      expect(read.request.headers.has('X-Correlation-Id')).toBeFalse();
      expect(read.request.headers.has('Authorization')).toBeFalse();

      const envelope: ModuleReadEnvelope = { data: moduleOf(), meta: null };

      read.flush(envelope);
      fixture.detectChanges();
      submitForm();

      const request: TestRequest = expectExportRequest();

      expect(request.request.headers.keys()).toEqual([]);
      expect(request.request.headers.has('X-Correlation-Id')).toBeFalse();
      expect(request.request.headers.has('Content-Type')).toBeFalse();

      request.flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();
    });
  });

  // =====================================================================================================
  // THE COMPOSED DOCUMENT NAME
  // =====================================================================================================
  //
  // `Export.ascx.vb:L124` is the whole specification:
  //
  //   Dim strFile As String = "content." & CleanName(objModule.ModuleName) & "." & CleanName(txtFile.Text) & ".xml"
  //
  // Four segments joined by full stops, with both middle segments sanitised - which is what removes the full
  // stops that would otherwise turn either segment into several.
  describe('the composed document name', () => {
    it('joins a fixed prefix, the module NAME, the typed text and the extension', () => {
      openOn();
      typeFileName('Backup');

      exportSucceedsWith('<content />');

      expect(requireSingleDownload().fileName).toBe('content.Announcements.Backup.xml');
    });

    it("builds the first segment from the module's programmatic NAME and never from its title", () => {
      // THE DISCRIMINATING CASE. The fixture's three name fields are all different - the programmatic name is
      // 'Announcements', the title is 'Latest News (2024)' and the definition's display name is
      // 'News Announcer' - so this expectation fails if any of the other two is wired in. The legacy class
      // declared all three separately, under three separate XML element names, and this screen keeps them
      // separate: the SUGGESTION comes from the title, the FILENAME comes from the programmatic name.
      openOn();

      // What the operator sees suggested is built from the title...
      expect(requireFileInput().value).toBe('LatestNews2024');

      exportSucceedsWith('<content />');

      // ...and what they receive is built from the programmatic name. The asymmetry is visible and intended:
      // collapsing the two would be tidier and would change the produced filename.
      expect(requireSingleDownload().fileName).toBe(
        'content.Announcements.LatestNews2024.xml',
      );
    });

    it('sanitises the module name too, not only the typed text', () => {
      openOn({ moduleName: 'Site Links_v2' });
      typeFileName('Backup');

      exportSucceedsWith('<content />');

      expect(requireSingleDownload().fileName).toBe('content.SiteLinksv2.Backup.xml');
    });

    it('contributes an empty segment for a module carrying no programmatic name', () => {
      // The legacy read the name straight off an object it had not checked, so an unreadable module raised a
      // null reference here, which the handler at `:L134` swallowed into the generic page failure. An absent
      // name contributes an empty segment instead - which is exactly what the legacy produced for a name that
      // sanitised away to nothing, so the shape of the result is unchanged.
      openOn({ moduleName: null });
      typeFileName('Backup');

      exportSucceedsWith('<content />');

      expect(requireSingleDownload().fileName).toBe('content..Backup.xml');
    });

    it('removes every removable character from the typed text while transmitting it untouched', () => {
      // One table-driven pass over all thirty-three characters, asserting both halves of the split on each:
      // the transmitted value keeps the character, and the produced filename does not.
      const expectedNames: readonly string[] = SANITISED_CHARACTERS.map(
        (_character, index) => `content.Announcements.x${index}y.xml`,
      );

      openOn();

      for (const [index, character] of SANITISED_CHARACTERS.entries()) {
        const typed = `x${index}${character}y`;

        typeFileName(typed);
        submitForm();

        const request: TestRequest = expectExportRequest();
        const expected: ModuleExportRequest = { fileName: typed, folder: null };

        expect(request.request.body)
          .withContext(`transmitting ${JSON.stringify(character)} untouched`)
          .toEqual(expected);

        request.flush('<content />', { status: 200, statusText: 'OK' });
        fixture.detectChanges();
      }

      expect(downloadAttempts.map((attempt) => attempt.fileName)).toEqual(expectedNames);

      // AT MOST ONE OBJECT URL IS EVER ALIVE. Each export releases the previous one before creating its own,
      // so after thirty-three exports every address but the last has been withdrawn.
      expect(createdObjectUrls.length).toBe(SANITISED_CHARACTERS.length);
      expect(revokedObjectUrls).toEqual(createdObjectUrls.slice(0, -1));
    });
  });

  // =====================================================================================================
  // VALIDATION
  // =====================================================================================================
  //
  // The legacy screen carried NO validator markup at all - no required-field validator, no validation
  // summary, not even a form element in its seventeen lines - and enforced its condition imperatively inside
  // the click handler at `Export.ascx.vb:L121`, reporting `Localization.GetString("Validation")` at the error
  // severity from `:L132`. The rule is declarative now, stated once beside the field it governs, and the
  // sentence is carried across unaltered even though the rule behind it is narrower.
  describe('validation', () => {
    it('refuses an empty filename with the legacy sentence, VERBATIM', () => {
      openOn({ moduleTitle: null });

      // Nothing is said while the field is untouched and empty by nature.
      expect(fieldMessages()).toEqual([]);

      submitForm();

      // The wording is the legacy resource value character for character. It names a folder as well as a
      // file, because the legacy gate tested both; the folder half of the rule is gone with the picker, and
      // the narrowing is the documented divergence while the words are not.
      expect(fieldMessages()).toEqual([EXPECTED_VALIDATION_MESSAGE]);

      // NO REQUEST WHILE THE FORM IS INVALID. Asserted positively here as well as by the outstanding-request
      // check at teardown.
      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });

    it('states the rule on the control, so it is enforced without a round trip', () => {
      openOn({ moduleTitle: null });

      submitForm();

      // The message came from the control's own validator: nothing was sent, so nothing could have answered.
      expect(fieldMessages()).toEqual([EXPECTED_VALIDATION_MESSAGE]);
      expect(httpMock.match(() => true)).toEqual([]);
    });

    it('refuses a filename that is nothing but white space', () => {
      // THE LEGACY TEST WAS UNTRIMMED, AND THAT DEFECT IS NOT REPRODUCED. `Export.ascx.vb:L121` tested
      // `txtFile.Text <> ""`, which a single space satisfies - and the sanitiser then removed that space,
      // leaving an empty segment in the middle of the composed name. The API refuses a blank name for the
      // same reason, so enforcing it here states ONE rule at both ends rather than spending a round trip to
      // learn what is already known. Reported under the same message, so a missing value has one sentence.
      openOn({ moduleTitle: null });
      typeFileName('   ');
      submitForm();

      expect(fieldMessages()).toEqual([EXPECTED_VALIDATION_MESSAGE]);
      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });

    it('carries the legacy length bound on the input and enforces it as a rule as well', () => {
      openOn();

      // `export.ascx:L11 maxlength="200"`, so the browser still refuses the two-hundred-and-first character
      // exactly as it did before.
      expect(requireFileInput().getAttribute('maxlength')).toBe(String(EXPECTED_MAX_LENGTH));

      // A value arriving by some route other than typing is refused by the rule behind the attribute.
      typeFileName('n'.repeat(EXPECTED_MAX_LENGTH + 1));
      submitForm();

      expect(fieldMessages()).toEqual([EXPECTED_TOO_LONG_MESSAGE]);
      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });

    it('accepts a filename of exactly the permitted length', () => {
      openOn();
      typeFileName('n'.repeat(EXPECTED_MAX_LENGTH));
      submitForm();

      expect(fieldMessages()).toEqual([]);

      expectExportRequest().flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      expect(downloadAttempts.length).toBe(1);
    });

    it('marks the control invalid only once the operator has engaged with it', () => {
      openOn({ moduleTitle: null });

      // `aria-invalid` carries the failure on the CONTROL, which the message region alone cannot: a reader
      // who arrives at the input later, or returns after the announcement has passed, would otherwise find a
      // field giving no indication of being in error. It is not applied pre-emptively.
      expect(requireFileInput().getAttribute('aria-invalid')).toBe('false');

      submitForm();

      expect(requireFileInput().getAttribute('aria-invalid')).toBe('true');

      typeFileName('Backup');

      expect(requireFileInput().getAttribute('aria-invalid')).toBe('false');
      expect(fieldMessages()).toEqual([]);

      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });
  });

  // =====================================================================================================
  // REFUSALS
  // =====================================================================================================
  //
  // The legacy screen emitted six resource keys from this one handler. Two survive: `ExportNotSupported`,
  // which `Export.ascx.vb` emitted from both `:L195` and `:L201`, and `NoContent` from `:L159`. The
  // disk-space refusal goes with the filesystem this screen no longer touches, and the bare catch-all at
  // `:L197-L198` - one generic sentence for every unanticipated fault - is subsumed by a structured problem
  // document that says strictly more. Neither of the two dropped sentences is asserted anywhere in this file.
  describe('refusals', () => {
    it('renders the legacy sentence for a module that cannot export, with the document intact around it', () => {
      openOn();
      exportIsRefusedWith(
        problemOf(422, 'The module cannot export its content.', NOT_PORTABLE_CODE),
        'Unprocessable Content',
      );

      // VERBATIM, and note the word "specified". The sibling import screen's equivalent entry says
      // "selected"; the inconsistency is in the legacy wording itself and is reproduced rather than
      // harmonised. The shared refusal table maps this same server code to the sibling screen's wording,
      // which is correct there and wrong here - which is exactly why this screen owns the sentence.
      expect(bannerMessage()).toBe(EXPECTED_NOT_SUPPORTED_MESSAGE);
      expect(bannerMessage()).toContain('specified');
      expect(bannerMessage()).not.toContain('selected');

      // THE SENTENCE IS SUBSTITUTED INTO THE DOCUMENT RATHER THAN RENDERED BESIDE IT, so three things
      // survive that a plain paragraph would have discarded: the severity classification, the problem's
      // title, and the support reference - the only value joining what an operator saw to what the server
      // logged, and therefore the one thing worth quoting in a report.
      expect(textOf('.error-banner__title')).toBe('Request rejected');
      expect(textOf('.error-banner__severity')).toBe('Error');
      expect(q('.error-banner')?.getAttribute('data-severity')).toBe('danger');
      expect(textOf('.error-banner__trace')).toBe(
        'Reference: 8f7c1b2d-4a6e-4f10-9c3b-5d2e7a1f0b64',
      );

      // Nothing was offered to the browser, and nothing was reported as a success.
      expect(raisedNotifications()).toEqual([
        { severity: 'error', message: EXPECTED_NOT_SUPPORTED_MESSAGE },
      ]);
      expect(createdObjectUrls).toEqual([]);
      expect(downloadAttempts).toEqual([]);
      expect(raisedSeverities()).not.toContain('success');
    });

    it('reports an EMPTY document with the legacy no-content sentence and offers no download', () => {
      // THE CONDITION MOVED SIDES, AND THE WORDING IS RETAINED FOR IT ANYWAY. The legacy branch at
      // `Export.ascx.vb:L159` tested the module's raw payload with `Content <> ""` BEFORE wrapping it. The
      // API wraps an empty payload and answers successfully with an envelope that is not itself empty, so it
      // publishes no failure code for this condition and it is no longer reachable from a status. The
      // distinction is preserved on the wire and in the store, both of which keep an empty document distinct
      // from an absent one, and that is what this branch consumes. A file containing nothing is not an export.
      openOn();
      exportSucceedsWith('');

      expect(notice()).toBe(EXPECTED_NO_CONTENT_MESSAGE);
      expect(notice()).toContain('specified');
      expect(createdObjectUrls).toEqual([]);
      expect(downloadAttempts).toEqual([]);
      expect(raisedNotifications()).toEqual([]);
    });

    it('surfaces a refused export as a FAILURE, never as a silent success', () => {
      // D-M6. `Export.ascx.vb:L149` opens `If Not objModule Is Nothing` and never writes an `Else`, so a
      // module that could not be read left the status string empty - and `:L126` read an empty status string
      // as success and redirected away. An operator therefore saw a successful export of a module that did
      // not exist. Nothing here treats the absence of a document as an outcome.
      openOn();
      exportIsRefusedWith(problemOf(404, 'No such module.'), 'Not Found');

      expect(bannerMessage()).toBe('No such module.');
      expect(createdObjectUrls).toEqual([]);
      expect(downloadAttempts).toEqual([]);
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(raisedSeverities()).not.toContain('success');
      expect(raisedNotifications()).toEqual([
        { severity: 'warning', message: 'No such module.' },
      ]);
    });

    it('refuses to export a module the read could not produce, without blaming the filename', () => {
      // MIGRATION: this case used to answer the read with a `200` carrying nothing, on the reading
      //   that the module transport could report absence inside the envelope. It cannot - the API
      //   answers a not-found problem document as soon as a value-bearing outcome carries no value -
      //   so the reachable form of "there is no module to export" is a REFUSED read, and that is what
      //   is exercised here. The claim being protected is unchanged and is the valuable half: with no
      //   module read, the confirming action reports the condition the operator cannot repair and
      //   sends nothing.
      addressModule(String(MODULE_ID));
      expectModuleRead().flush(problemOf(404, 'No such module.'), {
        status: 404,
        statusText: 'Not Found',
      });
      fixture.detectChanges();

      // The refused read is presented structurally, by the banner, and the screen adds no second
      // weaker report beside it.
      expect(bannerMessage()).toBe('No such module.');
      expect(notice()).toBeNull();

      // THE ORDER IS DELIBERATE. A condition the operator cannot repair outranks one they can, so
      // the missing module is reported instead of the field rule - which would otherwise tell them to supply
      // a filename for a module that does not exist.
      submitForm();

      expect(notice()).toBe(EXPECTED_NO_MODULE_MESSAGE);
      expect(fieldMessages()).toEqual([]);
      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });

    it('presents a refused READ through the banner and stays silent in the live region', () => {
      // A read that FAILED is already presented structurally, with the server's own sentence, its severity
      // and a support reference - strictly more than the screen's own sentence conveys - so the screen does
      // not add a second, weaker report beside it.
      addressModule(String(MODULE_ID));
      expectModuleRead().flush(problemOf(404, 'No such module.'), {
        status: 404,
        statusText: 'Not Found',
      });
      fixture.detectChanges();

      expect(bannerMessage()).toBe('No such module.');
      expect(notice()).toBeNull();
      expect(q('.error-banner')?.getAttribute('data-severity')).toBe('warning');
    });

    it('reads a per-field refusal by its model-state key and shows it beside the field', () => {
      const refusal: ValidationProblemDetails = validationProblemOf(400, [
        'The export filename is not acceptable.',
      ]);

      openOn();
      typeFileName('Backup');
      exportIsRefusedWith(refusal, 'Bad Request');

      // The server sent the key as `FileName`; the control is `fileName`. The shared matcher reconciles the
      // two spellings, and the dictionary is read through an INDEX EXPRESSION because it is typed as an index
      // signature and property access on one is a compile error in this workspace.
      expect(refusal.errors['FileName']).toEqual(['The export filename is not acceptable.']);
      expect(fieldMessages()).toEqual(['The export filename is not acceptable.']);

      // Shown beside the field ONCE. A refusal the server keyed to this field is not repeated as a
      // notification, which would state the same thing twice in two places.
      expect(raisedNotifications()).toEqual([]);
      expect(downloadAttempts).toEqual([]);
    });

    it('classifies a denied request as a WARNING rather than as a fault', () => {
      // The legacy access-denied screen renders at the warning severity in BOTH branches of its page load -
      // the message passed through the query string and the localised default - and the legacy renderer
      // itself withheld the red styling for a refusal. A denial means "not you", not "something broke".
      openOn();
      exportIsRefusedWith(problemOf(403, 'You may not export this module.'), 'Forbidden');

      expect(raisedNotifications()).toEqual([
        { severity: 'warning', message: 'You may not export this module.' },
      ]);
      expect(raisedSeverities()).not.toContain('error');
      expect(q('.error-banner')?.getAttribute('data-severity')).toBe('warning');
      expect(textOf('.error-banner__severity')).toBe('Warning');
      expect(downloadAttempts).toEqual([]);
    });

    it('still classifies a refusal whose body carried no document at all', () => {
      // An infrastructure device can answer with something that is not a problem document. The status is what
      // the server sent, so nothing is invented, and it is what lets the severity and the wording resolve -
      // which is why a refusal with an unusable body is still presented as a refusal.
      openOn();
      submitForm();
      expectExportRequest().flush('<html>Gateway problem</html>', {
        status: 502,
        statusText: 'Bad Gateway',
      });
      fixture.detectChanges();

      expect(bannerMessage()).toBe('The server could not complete the request. Try again shortly.');
      expect(raisedSeverities()).toEqual(['error']);
      expect(downloadAttempts).toEqual([]);
    });
  });

  // =====================================================================================================
  // NOTIFICATIONS
  // =====================================================================================================
  //
  // The legacy severity vocabulary had exactly three members and no informational one, and the legacy screen
  // used NONE of them on success: `Export.ascx.vb:L126-L127` redirected away and raised no message at all, so
  // an operator's only evidence that anything had happened was a file appearing in a folder on the server.
  // There is no such folder now, so silence would leave the outcome entirely unreported.
  describe('notifications', () => {
    it('reports a completed export at the SUCCESS severity, naming the file', () => {
      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      // NET ADDITION. The sentence names the file, which is safe to interpolate as text: every character that
      // could open a tag or close an attribute - the angle brackets, the ampersand, the double quote and the
      // apostrophe - is removed by the name sanitiser before it can reach the message.
      expect(raisedNotifications()).toEqual([
        {
          severity: 'success',
          message: 'Export complete. content.Announcements.Backup.xml has been downloaded.',
        },
      ]);
    });

    it('chooses the severity from the status, so a refusal is never dressed as a fault', () => {
      // A denial and a missing item mean "not you" and "not there"; neither is a fault, and the legacy
      // access-denied screen used the warning presentation in BOTH branches of its page load. A rate-limit
      // refusal means "you are early", which is quieter still: it is the compensating control for the
      // legacy image-verification field this migration removed, so it is announced informationally rather
      // than as a warning, and reporting it as an error would report a fault where the system is working
      // exactly as configured. Every severity here is the shared classifier's answer, never this screen's.
      const expected: readonly { readonly status: number; readonly severity: NotificationSeverity }[] =
        [
          { status: 400, severity: 'error' },
          { status: 403, severity: 'warning' },
          { status: 404, severity: 'warning' },
          { status: 409, severity: 'error' },
          { status: 422, severity: 'error' },
          { status: 429, severity: 'info' },
          { status: 500, severity: 'error' },
        ];

      openOn();
      typeFileName('Backup');

      for (const outcome of expected) {
        exportIsRefusedWith(
          problemOf(outcome.status, `Refused under ${outcome.status}.`),
          `Status ${outcome.status}`,
        );
      }

      expect(raisedNotifications()).toEqual(
        expected.map((outcome) => ({
          severity: outcome.severity,
          message: `Refused under ${outcome.status}.`,
        })),
      );

      // Every severity used is a member of the four-valued vocabulary, and none of these is a success.
      expect(raisedSeverities()).not.toContain('success');
      expect(downloadAttempts).toEqual([]);
    });
  });

  // =====================================================================================================
  // THE DOWNLOAD
  // =====================================================================================================
  //
  // THIS CAPABILITY IS ENTIRELY NEW, and it is worth being exact about that, because the legacy source
  // contains something that looks like a stream and is not. `application/octet-stream` appears at
  // `Export.ascx.vb:L184` and `:L186`, but both are arguments to the calls that insert or update the
  // CATALOGUE ROW for the written file - a column value in a table. The legacy screen set no response header,
  // wrote no bytes to the response and offered the operator nothing to save: it wrote a text file beneath the
  // portal's home directory (`:L171-L174`) and redirected away. There is no such directory in the target
  // topology and no endpoint that would write to one.
  describe('the download', () => {
    it('offers the document under the composed name and leaves no element behind', () => {
      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      const attempt: DownloadAttempt = requireSingleDownload();

      expect(attempt.fileName).toBe('content.Announcements.Backup.xml');
      expect(attempt.href).toBe(requireSingleObjectUrl());
      // Hidden, so activation cannot flash a visible element, and marked so the opened context cannot reach
      // back into this one.
      expect(attempt.hidden).toBeTrue();
      expect(attempt.rel).toBe('noopener');

      // Attached before activation - which is what finding it in the document during the activation proved -
      // and removed again afterwards, so no trace is left either way.
      expect(document.body.querySelectorAll('a[download]').length).toBe(0);
    });

    it('hands the document over unread, even when it carries markup that could execute', async () => {
      // The exported document is module-authored, which makes it the least trustworthy string this screen
      // handles. It travels from the store into a binary container without being parsed, re-serialised or
      // searched, and it creates no element anywhere.
      const hostile =
        '<content type="Announcements"><script>alert(1)</script><p>&amp; &lt; &gt;</p></content>';

      openOn();
      typeFileName('Hostile');
      exportSucceedsWith(hostile);

      const blob: Blob = requireSingleBlob();

      await expectAsync(blob.text()).toBeResolvedTo(hostile);
      expect(blob.size).toBe(hostile.length);
      expect(blob.type).toBe(EXPECTED_MEDIA_TYPE);

      // It is a FILE, not content: nothing was rendered from it.
      expect(q('script')).toBeNull();
      expect(host().textContent ?? '').not.toContain('alert(1)');
      expect(requireSingleDownload().fileName).toBe('content.Announcements.Hostile.xml');
    });

    it('releases the object URL when the screen goes away', () => {
      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      const objectUrl: string = requireSingleObjectUrl();

      // DELIBERATELY NOT RELEASED THE INSTANT THE ACTIVATION RETURNS. An object URL withdrawn in the same
      // task as the activation can be taken away before the browser has finished resolving it, which turns a
      // working download into a silent failure on some engines. Nothing is revoked within this task, which is
      // what this assertion pins; the release happens one turn later, and the case below proves that.
      expect(revokedObjectUrls).toEqual([]);

      // Teardown remains the last unskippable path, and it is what releases an entry whose scheduled release
      // has not run yet - a screen closed inside the turn.
      fixture.destroy();

      expect(revokedObjectUrls).toEqual([objectUrl]);
    });

    it('releases the object URL one turn later, without waiting for teardown', async () => {
      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      const objectUrl: string = requireSingleObjectUrl();

      // The activation has happened, so the browser has begun resolving the entry.
      expect(clickSpy).toHaveBeenCalled();
      expect(revokedObjectUrls).toEqual([]);

      await new Promise((resolve) => setTimeout(resolve, 0));

      // ⚠ RELEASED WHILE THE SCREEN IS STILL OPEN, which is the whole point of this case. Holding the entry
      // until the next export or until the screen goes away keeps the whole exported document pinned in memory
      // for as long as an operator stays on an administration screen left open after one export.
      expect(revokedObjectUrls).toEqual([objectUrl]);
    });

    it('revokes exactly once when teardown races the scheduled release', async () => {
      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      const objectUrl: string = requireSingleObjectUrl();

      fixture.destroy();
      await new Promise((resolve) => setTimeout(resolve, 0));

      // Teardown cancels the pending release, so the entry is not revoked a second time and no stray callback
      // runs against a component that no longer exists.
      expect(revokedObjectUrls).toEqual([objectUrl]);
    });

    it('releases the previous object URL before creating another', () => {
      openOn();
      typeFileName('First');
      exportSucceedsWith('<content>1</content>');
      typeFileName('Second');
      exportSucceedsWith('<content>2</content>');

      // AT MOST ONE IS EVER ALIVE, which is what bounds the leak the deferral would otherwise create.
      expect(createdObjectUrls.length).toBe(2);
      expect(revokedObjectUrls).toEqual(createdObjectUrls.slice(0, 1));
      expect(downloadAttempts.map((attempt) => attempt.fileName)).toEqual([
        'content.Announcements.First.xml',
        'content.Announcements.Second.xml',
      ]);
    });

    it('releases the object URL when the address changes', () => {
      openOn({}, 4);
      typeFileName('First');
      exportSucceedsWith('<content>1</content>', 4);

      const objectUrl: string = requireSingleObjectUrl();

      addressModule('5');

      expect(revokedObjectUrls).toEqual([objectUrl]);

      answerModuleRead(moduleOf({ moduleId: 5 }), 5);
    });

    it('releases the object URL and reports the failure when activation is refused', () => {
      clickSpy.and.throwError('activation refused');

      openOn();
      typeFileName('Backup');
      exportSucceedsWith('<content />');

      const objectUrl: string = requireSingleObjectUrl();

      // Nothing was handed to the browser, so there is no download racing the revocation and the address can
      // be withdrawn at once. The document itself is not lost: it is still in the store, and activating the
      // action again retries only this step.
      expect(revokedObjectUrls).toEqual([objectUrl]);
      expect(raisedNotifications()).toEqual([
        { severity: 'error', message: EXPECTED_DOWNLOAD_FAILED_MESSAGE },
      ]);
      expect(raisedSeverities()).not.toContain('success');

      // Removed on every path, including a failure part-way through.
      expect(document.body.querySelectorAll('a[download]').length).toBe(0);
    });
  });

  // =====================================================================================================
  // THE DISMISSING ACTION
  // =====================================================================================================
  describe('the dismissing action', () => {
    it('neither validates nor submits, abandoning a half-typed filename silently', () => {
      openOn({ moduleTitle: null });

      const cancel: HTMLButtonElement = requireActionButton(EXPECTED_CANCEL_LABEL);

      // `type="button"` is the faithful translation of `causesvalidation="False"` on `export.ascx:L16`, stated
      // explicitly so this action can never submit the form it sits in.
      expect(cancel.type).toBe('button');

      cancel.click();
      fixture.detectChanges();

      // Nothing was marked up on the way out, and nothing was sent.
      expect(fieldMessages()).toEqual([]);
      expect(requireFileInput().getAttribute('aria-invalid')).toBe('false');
      httpMock.expectNone({ method: 'POST', url: `/api/v1/modules/${MODULE_ID}/export` });
    });

    it('returns to the module it was addressing', () => {
      openOn();

      requireActionButton(EXPECTED_CANCEL_LABEL).click();

      // The legacy handler redirected to the current page's own address, returning the operator to the portal
      // page hosting the module - a page assembled by the server from skins and containers, and a concept with
      // no counterpart here. The module's administration screen is the nearest destination that exists, and it
      // is expressed as a path rather than as an import of a neighbouring screen.
      expect(navigateSpy).toHaveBeenCalledWith(['/modules', MODULE_ID, 'settings']);
    });

    it('returns to the application root when the address named no module', () => {
      addressModule('not-a-module');

      requireActionButton(EXPECTED_CANCEL_LABEL).click();

      expect(navigateSpy).toHaveBeenCalledWith(['/']);
    });
  });

  // =====================================================================================================
  // WORDING, AFFORDANCES AND WHAT IS DELIBERATELY ABSENT
  // =====================================================================================================
  describe('wording and affordances', () => {
    it("titles the page from the resource entry, in the page's single heading", () => {
      openOn();

      expect(textOf('h1')).toBe(EXPECTED_PAGE_TITLE);
      expect(qa('h1').length).toBe(1);
      // Taken from the paragraph inside the legacy help entry; the heading element in that same value
      // duplicates the title, and the shared page header already emits the page's only `h1`.
      expect(textOf('.page-header__subtitle')).toBe(
        'Administrators can export content for the specified module.',
      );
    });

    it('labels the field from the resource entry and associates the label with the control', () => {
      openOn();

      const label = q<HTMLLabelElement>('.form-field__label');

      expect(label).withContext('the field label must be rendered').not.toBeNull();

      if (label === null) {
        throw new Error('the field label was not rendered');
      }

      // 'File', followed by the required marker the shared field adds - a glyph hidden from assistive
      // technology and a word hidden visually, so requiredness never depends on colour or punctuation alone.
      // The marker and the word are adjacent elements with no whitespace between them, which is why the
      // collapsed text reads exactly as it does; asserting the whole string is what pins the field to the
      // shared component's affordance rather than to a marker this screen might have invented.
      expect(label.textContent ?? '').toContain(EXPECTED_FILE_LABEL);
      expect(textOf('.form-field__label')).toBe(`${EXPECTED_FILE_LABEL} *required`);
      expect(label.getAttribute('for')).toBe('module-export-file-name');
      expect(requireFileInput().id).toBe('module-export-file-name');
    });

    it('carries the legacy help wording behind a keyboard-reachable disclosure', () => {
      openOn();

      const toggle = q<HTMLButtonElement>('.form-field__help-toggle');

      expect(toggle).withContext('the help disclosure must be rendered').not.toBeNull();

      if (toggle === null) {
        throw new Error('the help disclosure was not rendered');
      }

      // D11. The legacy affordance was withdrawn from the tab order by a NEGATIVE TAB INDEX - twice over, on
      // the link button and again on the image nested inside it - so the legacy help was operable by pointer
      // only and unreachable by keyboard. It is a real button here, reachable by Tab and activated by both
      // Enter and Space, and `type="button"` is stated so it can never submit the form it sits in. The shared
      // component owns the whole affordance; nothing about it is re-implemented on this screen.
      expect(toggle.type).toBe('button');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      toggle.click();
      fixture.detectChanges();

      expect(textOf('.form-field__help')).toBe(EXPECTED_FILE_HELP);
    });

    it("labels the confirming action 'Export', not the 'Import' written on the legacy element", () => {
      openOn();

      // D10. `export.ascx:L15` declares the export action with the inline attribute `text="Import"`, a
      // copy-paste defect from the near-identical import screen. The same element carries
      // `resourcekey="cmdExport"`, and the legacy framework overwrote the inline text with the resource value
      // at run time, so an operator always read "Export". The corrected label is the behaviour that always
      // applied, not a change to it.
      const exportAction: HTMLButtonElement = requireActionButton(EXPECTED_EXPORT_LABEL);

      expect(actionButton('Import')).toBeNull();
      expect(host().textContent ?? '').not.toContain('Import');

      // A real submit control inside the form, so the field's default action is the export and the Enter key
      // submits from within the field - which the legacy postback model gave for free.
      expect(exportAction.type).toBe('submit');

      const form = q<HTMLFormElement>('form.module-export__form');

      expect(form?.contains(exportAction)).toBeTrue();
    });

    it("labels the dismissing action 'Cancel', from the shared global resource entry", () => {
      openOn();

      expect(requireActionButton(EXPECTED_CANCEL_LABEL).textContent?.trim()).toBe(
        EXPECTED_CANCEL_LABEL,
      );
      expect(qa('.module-export__actions button').length).toBe(2);
    });

    it('presents both actions as buttons rather than as links', () => {
      openOn();

      // Both legacy actions were link buttons, which render as anchors - elements that navigate rather than
      // act, are announced as links, and are not activated by the space key. There is no anchor on this screen
      // at all; the only one it ever creates is the transient download element, which is removed immediately.
      expect(qa('a').length).toBe(0);
      expect(qa('.module-export__actions button').length).toBe(2);
    });

    it('has no folder control of any kind', () => {
      openOn();

      // THE PICKER AT `export.ascx:L6-L7` IS DROPPED ENTIRELY. It was a labelled drop-down list of server
      // folders, filled from the folders the operator could read and write and opened on a placeholder. No
      // folder-listing, file-listing, upload or disk-space endpoint exists in this API, so there is nothing to
      // fill such a list from and nowhere for a chosen folder to write to. Three pieces of legacy wording go
      // with it - the folder label, its help text and the localised name of the portal root - as does the
      // placeholder the picker opened on.
      expect(q('select')).toBeNull();
      expect(qa('option').length).toBe(0);

      const rendered = host().textContent ?? '';

      expect(rendered).not.toContain('Folder');
      expect(rendered).not.toContain('Select the export folder');
      expect(rendered).not.toContain('Root');
      expect(rendered).not.toContain('None Specified');
    });

    it('replaces the legacy layout table with a real form', () => {
      openOn();

      // `export.ascx:L4` opened a fixed-width table used purely for positioning, carrying a summary that
      // described a completely different screen because the markup was copied from one. A table used for
      // layout announces rows and cells that are not data.
      expect(q('table')).toBeNull();
      expect(q('form.module-export__form')).not.toBeNull();
      // There is a form element now, where the legacy markup had none of its own.
      expect(qa('form').length).toBe(1);
    });

    it('emits no semantic landmark, because the application shell owns each of them', () => {
      openOn();

      // The shell emits the banner, navigation, main and content-info landmarks exactly once each. A screen
      // that emitted another would duplicate a landmark and break landmark navigation.
      expect(q('header')).toBeNull();
      expect(q('main')).toBeNull();
      expect(q('nav')).toBeNull();
      expect(q('footer')).toBeNull();
    });

    it('announces each wait distinctly, and only while it is in flight', () => {
      addressModule(String(MODULE_ID));

      // Two distinct waits, announced distinctly, because they mean different things to an operator. The
      // legacy screen had nothing to announce: it read the module during its own server-side render, so the
      // field was already populated by the time an operator saw the page.
      expect(textOf('app-loading-spinner .label')).toBe('Loading module…');

      answerModuleRead(moduleOf());

      expect(q('app-loading-spinner')).toBeNull();

      submitForm();

      expect(textOf('app-loading-spinner .label')).toBe('Exporting module content…');
      // The confirming action is disabled only while a request is in flight - never on portability grounds,
      // because the server owns that answer and says so in words.
      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeTrue();

      expectExportRequest().flush('<content />', { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      expect(q('app-loading-spinner')).toBeNull();
      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeFalse();
    });

    it('renders nothing at all in either message surface when there is nothing to report', () => {
      openOn();

      expect(notice()).toBeNull();
      expect(bannerMessage()).toBeNull();
      expect(fieldMessages()).toEqual([]);
      expect(raisedNotifications()).toEqual([]);
    });

    /**
     * A REFUSED READ LEAVES NOTHING TO EXPORT, AND THE CONFIRMING ACTION MUST NOT SAY OTHERWISE.
     *
     * The server answers `GET /api/v1/modules/{id}` with 403 when the caller may not see the module. A gate
     * that tested the ADDRESS - `Number.isInteger(moduleId)` - is satisfied by a refusal, so the button
     * would stay enabled over a screen that holds no module. Pressing it could never succeed, and it would
     * actively make the screen lie: `submit` clears the store failure as it begins, so the press would
     * erase the banner accurately reporting the refusal and replace it with a sentence about the address.
     *
     * The presentation of a refusal is the banner and nothing else - the same presentation the sibling
     * module form and settings screens give the same status, and the same severity the legacy
     * access-denied page used (`Website/admin/Security/AccessDenied.ascx.vb:L41-L45`).
     *
     * ⚠ THIS IS NOT A PORTABILITY GATE, and the case below it proves the distinction survives: a module
     * that WAS read stays exportable however unlikely the export is to succeed, because the server owns
     * that answer.
     */
    it('withholds the export when the read was refused, and says so once', () => {
      addressModule(String(MODULE_ID));

      expectModuleRead().flush(
        problemOf(403, 'You are not permitted to export this module.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled)
        .withContext('there is no module in hand to export')
        .toBeTrue();

      // One statement, and it is the server's own. The address sentence must NOT accompany it: the address
      // is perfectly well formed.
      expect(bannerMessage()).toContain('You are not permitted to export this module.');
      expect(notice()).toBeNull();
      expect(raisedNotifications()).toEqual([]);
    });

    it('keeps the export offered for a module that WAS read, whatever its portability', () => {
      // The counterpart to the case above: the widened gate must not have become a portability judgement.
      openOn();

      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeFalse();
      expect(bannerMessage()).toBeNull();
    });

    it('withholds the export while the read is still in flight', () => {
      addressModule(String(MODULE_ID));

      // Nothing has arrived yet, so there is nothing to export and the press would only have produced the
      // address sentence for an address that is not at fault.
      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeTrue();

      answerModuleRead(moduleOf());

      expect(requireActionButton(EXPECTED_EXPORT_LABEL).disabled).toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // STALENESS ACROSS A ROUTE MOVE
  // ---------------------------------------------------------------------------------------------------
  /**
   * ⚠ THE EXFILTRATION RACE, and the sink it guards cannot be undone once it fires.
   *
   * `/modules/7/export` and `/modules/9/export` resolve to the SAME route configuration, so moving
   * between them changes the bound input WITHOUT recreating this component. The store it reads is
   * root-provided and shared with every other module screen, so what it publishes is whatever was read
   * LAST — by this screen or by another.
   *
   * Two consequences follow, and both put tenant data in the wrong place:
   *
   * - The composed filename is derived from the LOADED module's name. A detail describing module 7 while
   *   the route names module 9 labels module 9's document with module 7's name.
   * - An export requested for module 7 can settle after the route has moved to module 9. Delivering it
   *   then writes module 7's serialised data into a file the operator will read as module 9's, and
   *   nothing downstream can detect the substitution.
   */
  describe('staleness across a route move', () => {
    it('does not seed the filename from a module the address no longer names', () => {
      openOn({ moduleTitle: 'Announcements', moduleName: 'Announcements' });

      expect(requireFileInput().value)
        .withContext('precondition: the suggestion comes from the addressed module')
        .toBe('Announcements');

      // The route moves. The store still holds module 7 until module 9's read answers, and that held
      // detail must not seed this form.
      addressModule(String(MODULE_ID + 2));

      const stale = requireFileInput().value;

      expect(stale)
        .withContext("the previous module's title must not survive the address change")
        .not.toBe('Announcements');

      answerModuleRead(
        moduleOf({ moduleId: MODULE_ID + 2, moduleTitle: 'Links', moduleName: 'Links' }),
        MODULE_ID + 2,
      );

      expect(requireFileInput().value)
        .withContext('the newly addressed module seeds the suggestion instead')
        .toBe('Links');
    });

    it('does not download a document that finished after the address moved', () => {
      openOn({ moduleTitle: 'Announcements', moduleName: 'Announcements' });
      typeFileName('Announcements');
      submitForm();

      const inFlight = expectExportRequest();

      // The operator moves to a different module while the export is still running.
      addressModule(String(MODULE_ID + 2));
      expectModuleRead(MODULE_ID + 2).flush({ data: moduleOf({ moduleId: MODULE_ID + 2 }), meta: null });
      fixture.detectChanges();

      // Leaving the address abandons the transfer, so the request is cancelled outright and its answer
      // is never delivered. That is the strongest available outcome: the document cannot be mishandled
      // because it never arrives.
      expect(inFlight.cancelled)
        .withContext('the export is abandoned when its module stops being addressed')
        .toBeTrue();

      // Flushed anyway where the transport still permits it, so the guards behind cancellation are
      // exercised rather than assumed.
      if (!inFlight.cancelled) {
        inFlight.flush('<content><secret>module seven data</secret></content>');
      }

      fixture.detectChanges();

      // ⚠ NOTHING REACHES THE DEVICE. Without a guard this is module 7's data in a file the operator
      // will read as module 9's.
      expect(document.body.querySelectorAll('a[download]').length)
        .withContext('no file is handed over for a module the operator has left')
        .toBe(0);

      // WHICH guard stops it is deliberately not asserted, because TWO do and they are layered.
      // Changing the address withdraws the pending filename, so the delivery is refused on that
      // ground first; the captured-module comparison behind it is the backstop for any path that
      // reaches a delivery with a name still pending. Asserting the outcome rather than the mechanism
      // is what keeps this case honest if either layer is ever reorganised.
      expect(requireFileInput().value)
        .withContext('and the form now belongs to the newly addressed module')
        .not.toBe('Announcements');
    });

    it('still downloads a document that finishes while its own module is addressed', () => {
      // The positive control. A guard like this most easily regresses by being too strict, so the
      // legitimate delivery is asserted rather than assumed.
      openOn({ moduleTitle: 'Announcements', moduleName: 'Announcements' });
      typeFileName('Announcements');
      submitForm();

      expectExportRequest().flush('<content>module seven data</content>');
      fixture.detectChanges();

      expect(requireSingleDownload().fileName)
        .withContext('the addressed module\u2019s own export is delivered normally')
        .toContain('Announcements');
    });
  });
});
