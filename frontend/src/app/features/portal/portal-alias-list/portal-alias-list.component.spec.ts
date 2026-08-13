/**
 * Specification for {@link PortalAliasListComponent} — the HTTP aliases of one portal. ## WHY THIS SCREEN
 * NEEDS ITS OWN SPECIFICATION An alias is not decoration: it is the ONLY thing that resolves an incoming
 * request to a tenant.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { EMPTY } from 'rxjs';

import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { CONFLICT_MESSAGE } from '../../../core/utils/form-errors.util';
import { PortalAliasListComponent } from './portal-alias-list.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { PortalAlias } from '../../../core/models/portal.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
} from '../../../core/models/problem-details.model';

// ADDRESSES

/** The alias collection of one portal. */
function aliasesUrl(portalId: number): string {
  return `/api/v1/portals/${portalId}/aliases`;
}

/**
 * One alias of one portal. THE LEGACY FOUR-LETTER ABBREVIATED QUERY KEY DOES NOT SURVIVE. The legacy
 * listing composed an edit address from it — `Website/admin/Portal/portalalias.ascx:L8` passes that
 * abbreviation to `EditURL` as its first argument — and the edit control read it straight back out of the
 * query string at `Website/admin/Portal/EditPortalAlias.ascx.vb:L55`, coercing it to an integer at L57.
 */
function aliasUrl(portalId: number, portalAliasId: number): string {
  return `${aliasesUrl(portalId)}/${portalAliasId}`;
}

// THE WORDING THIS SCREEN PUBLISHES

/** `ControlTitle_.Text` in `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx`. */
const HEADING = 'Portal Aliases';

const ADD_ACTION_LABEL = 'Add New HTTP Alias';

const ALIAS_COLUMN_HEADING = 'HTTP Alias';

/** `Edit.Text` in the GLOBAL `Website/App_GlobalResources/SharedResources.resx`. */
const EDIT_LABEL = 'Edit';

/** `cmdDelete.Text`, global. */
const DELETE_LABEL = 'Delete';

/** `cmdCancel.Text`, global. */
const CANCEL_LABEL = 'Cancel';

const ADD_SUBMIT_LABEL = 'Add New Alias';

/** `cmdUpdate.Text`, global; the edit-mode label of that same one control. */
const UPDATE_SUBMIT_LABEL = 'Update';

/** `DeleteItem.Text`, global. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `Success.Text` in `EditPortalAlias.ascx.resx`, measured ABSENT from the global file. */
const SAVED_MESSAGE = 'The Portal Alias has been saved.';

/** Authored wording; the legacy delete handler emitted no message at all. */
const DELETED_MESSAGE = 'The Portal Alias has been deleted.';

/** `DuplicateAlias.Text` in `EditPortalAlias.ascx.resx` — THIS screen's own, terser wording. */
const DUPLICATE_ALIAS_MESSAGE = 'The Portal Alias already exists.';

const VIEW_DENIED_MESSAGE = 'You do not have access to view this Portal Alias.';

/** `AccessDenied.Text` in `EditPortalAlias.ascx.resx`, read by the handler at L183. */
const DELETE_DENIED_MESSAGE = 'You do not have access to delete this Portal Alias.';

/** Authored wording; neither resource file declares an empty-state key. */
const EMPTY_MESSAGE = 'This portal has no HTTP aliases.';

/** The server's own `NotEmpty` wording, reproduced so one refusal is not described two ways. */
const ALIAS_REQUIRED_MESSAGE = 'An HTTP alias is required.';

const ALIAS_ENTRY_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 255 characters.';

/** The authoritative maximum, applied to the value that is actually transmitted. */
const ALIAS_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 200 characters.';

// =====================================================================================================
// MEASURED LIMITS AND IDENTIFIERS
// =====================================================================================================

const SCHEME_SEPARATOR = '://';

/** The secure scheme name, which the strip must treat exactly like any other. */
const SECURE_SCHEME = 'https';

/** The insecure scheme name, which it must treat identically. */
const INSECURE_SCHEME = 'http';

/**
 * Prefixes one host name with a protocol scheme, exactly as an operator might type it.
 *
 * @param scheme The scheme name, without its separator.
 * @param alias The host name the operator meant.
 * @returns The raw entry to type into the field.
 */
function withScheme(scheme: string, alias: string): string {
  return `${scheme}${SCHEME_SEPARATOR}${alias}`;
}

/**
 * The rendered control's own character cap, `MaxLength="255"` at
 * `Website/admin/Portal/editportalalias.ascx:L7`, preserved exactly so the typing experience is the
 * legacy one. The storage column is the shorter 200, which is why both bounds are asserted and why they
 * are asserted against different values.
 */
const ALIAS_ENTRY_MAX_LENGTH = 255;

/** The authoritative maximum, measured on the NORMALISED value. */
const ALIAS_MAX_LENGTH = 200;

/** The element identifier the shared field's label association names, so the two cannot drift. */
const ALIAS_CONTROL_ID = 'portal-alias-http-alias';

/**
 * The name of the one form control, which is also the WIRE MEMBER of both write contracts. ⚠ TWO
 * DIFFERENT STRINGS ARE IN PLAY AND THEY ARE NOT INTERCHANGEABLE. This is the wire key, camel-cased by
 * the server's serialiser policy. The MODEL-STATE ERROR-DICTIONARY key is {@link MODEL_STATE_ALIAS_KEY},
 * which is the DTO property name and therefore Pascal-cased.
 */
const ALIAS_WIRE_KEY = 'httpAlias';

/** The Pascal-cased key a model binder reports a per-field refusal under. */
const MODEL_STATE_ALIAS_KEY = 'HttpAlias';

/** The published prefix every application failure code is namespaced under. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** The refusal code the server publishes for a host name already bound. */
const DUPLICATE_ALIAS_CODE = 'portal.alias_duplicate';

/** The refusal code the server publishes for a write to the alias the request arrived through. */
const ACTIVE_ALIAS_CODE = 'portal.alias_in_use.conflict';

/** A refusal of authority, which is the successor of both legacy denials. */
const NOT_PERMITTED_CODE = 'auth.not_permitted';

/** A per-field refusal from the request validator. */
const VALIDATION_FAILED_CODE = 'portal.validation_failed';

/** A fault, which is the case the legacy bare `Catch` mislabelled as a duplicate. */
const SERVER_FAULT_CODE = 'server.unexpected';

/** The status vocabulary's own titles, so no document carries an invented one. */
const STATUS_TITLE: Readonly<Record<number, string>> = Object.freeze({
  400: 'Bad Request',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
});

/**
 * A fixed W3C trace identifier. ⚠ FIXED, NEVER GENERATED. Nothing in this file reads a clock or a random
 * source, so no case can pass on one run and fail on the next. It is a diagnostic identifier and not a
 * credential — no real secret appears in any fixture here, which the legacy configuration cannot claim:
 * it committed a reversible decryption key at `Website/release.config:L89-L93`.
 */
const TRACE_ID = '00-4b8e1f2a7c934dd6bb18eb211c80319c-91bd6b7169203331-01';

/** A fixed correlation identifier — the reference an operator quotes, when the server sends one. */
const CORRELATION_ID = '7d2a9c41-3e6b-42f8-9051-6b0e3a9d5f17';

/** How a document's diagnostic identifiers are supplied, so each case can choose deliberately. */
interface DiagnosticIdentifiers {
  /** The W3C trace identifier, or undefined to omit the member entirely. */
  readonly traceId?: string;

  /** The correlation identifier, or undefined to omit the member entirely. */
  readonly correlationId?: string;
}

/** Both identifiers, which is what the API attaches to every document it produces. */
const BOTH_IDENTIFIERS: DiagnosticIdentifiers = Object.freeze({
  traceId: TRACE_ID,
  correlationId: CORRELATION_ID,
});

/** The trace identifier ALONE, for proving that it survives when it is the only join key there is. */
const TRACE_ONLY: DiagnosticIdentifiers = Object.freeze({ traceId: TRACE_ID });

/**
 * Builds one RFC 7807 document, typed as the real contract. ⚠ THE RETURN TYPE IS THE PRODUCTION
 * INTERFACE, WHICH IS THE POINT. A mis-cased or invented member is then a compile error rather than a
 * value that silently reads back as undefined — and the casing hazard is real, because the serialiser
 * lower-cases the leading upper-case RUN of a name, so `PortalID` becomes `portalID` while `PortalId`
 * becomes `portalId`.
 *
 * @param code The application failure code, without its namespace prefix.
 * @param status The status the document declares, which is also the transport status.
 * @param detail The occurrence-specific sentence.
 * @param errors The per-field map, attached ONLY for a case exercising a model-state refusal — never as
 * an empty object, because the server does not emit one.
 * @param identifiers Which diagnostic identifiers to attach.
 * @returns The document, ready to flush.
 */
function problemDocument(
  code: string,
  status: number,
  detail: string,
  errors?: ProblemDetailsErrors,
  identifiers: DiagnosticIdentifiers = BOTH_IDENTIFIERS,
): ProblemDetails {
  const titled: string | undefined = STATUS_TITLE[status];

  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: titled === undefined ? 'Error' : titled,
    status,
    detail,
    ...identifiers,
  };

  return errors === undefined ? document : { ...document, errors };
}

/** The empty field map, so an absent one can be read without a non-null assertion. */
const NO_FIELD_ERRORS: ProblemDetailsErrors = Object.freeze({});

/**
 * Reads the messages one document reported for the alias field. ⚠ BRACKET ACCESS, NEVER DOT ACCESS. The
 * field map is an index-signature type and this workspace enables `noPropertyAccessFromIndexSignature`,
 * so reaching the entry through a dotted member name would not compile at all. The absence of the map is
 * handled by substituting the frozen empty one rather than by asserting non-null.
 *
 * @param document The document whose field map to read.
 * @returns The messages reported for the alias field, possibly empty.
 */
function reportedAliasMessages(document: ProblemDetails): readonly string[] {
  const reported: ProblemDetailsErrors = document.errors ?? NO_FIELD_ERRORS;

  return reported[MODEL_STATE_ALIAS_KEY] ?? [];
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * One alias row.
 *
 * @param portalAliasId The row's surrogate key.
 * @param httpAlias The host name, or null for a row that carries none.
 * @param portalId The owning portal.
 * @param isCurrent Whether this is the alias the reading request resolved the tenant through.
 * @returns One row, exactly as the contract declares it.
 */
function alias(
  portalAliasId: number,
  httpAlias: string | null,
  portalId = -1,
  isCurrent = false,
): PortalAlias {
  return { portalAliasId, portalId, httpAlias, isCurrent };
}

/**
 * The single-resource envelope exactly as the server writes it. DECLARED HERE RATHER THAN IMPORTED, and
 * the reason is a dependency boundary rather than convenience: the paging contract is not among this
 * specification's declared dependencies, and a specification has no business reaching for a module it was
 * not given.
 */
interface SingleResourceEnvelope<T> {
  /** The payload the endpoint produced. */
  readonly data: T;

  /** The paging metadata, which is always null on this envelope — it describes no page. */
  readonly meta: null;
}

/**
 * @param data The payload the endpoint produced.
 * @returns The envelope, ready to flush.
 */
function envelope<T>(data: T): SingleResourceEnvelope<T> {
  return { data, meta: null };
}

/** A body a request whose member set is being asserted was sent with. */
interface AliasWriteBody {
  /** The host name, normalised by the screen before it was sent. */
  readonly httpAlias: string;
}

/**
 * Narrows an outgoing request body to the write contract.
 *
 * @param body The body as the transport recorded it.
 * @returns True when the body is exactly the one-member write contract.
 */
function isAliasWriteBody(body: unknown): body is AliasWriteBody {
  if (body === null || typeof body !== 'object') {
    return false;
  }

  const members: readonly string[] = Object.keys(body);

  if (members.length !== 1 || members[0] !== ALIAS_WIRE_KEY) {
    return false;
  }

  // Read into an explicitly `unknown` binding, so the member's type is proved by the test below
  // rather than claimed by a cast.
  const held: unknown = Reflect.get(body, ALIAS_WIRE_KEY);

  return typeof held === 'string';
}

/**
 * The host name one request carried, proved to be there rather than assumed.
 *
 * @param call The recorded request.
 * @returns The transmitted host name.
 */
function transmittedAlias(call: TestRequest): string {
  const body: unknown = call.request.body;

  expect(isAliasWriteBody(body))
    .withContext('the body is exactly the one-member write contract')
    .toBe(true);

  return isAliasWriteBody(body) ? body.httpAlias : '';
}

describe('PortalAliasListComponent', () => {
  let fixture: ComponentFixture<PortalAliasListComponent>;
  let httpMock: HttpTestingController;
  let store: PortalStore;
  let notifySpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ THE ORDER OF THESE TWO IS LOAD-BEARING. The real client is provided FIRST and the testing backend
    // SECOND, so that the second displaces the first. Reversing them leaves the live backend in place and
    // every case below attempts a real network request.
    await TestBed.configureTestingModule({
      imports: [PortalAliasListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), PortalStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(PortalStore);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Mounts the screen with a route parameter. ⚠ THE PARAMETER IS DELIVERED AS A STRING, BECAUSE THAT IS
   * WHAT A ROUTE PARAMETER IS. Component input binding hands path parameters over as text, so passing a
   * number here would bypass the input's own transform and prove nothing about how the screen behaves
   * when routed to.
   *
   * @param portalId The path parameter, as text.
   */
  function create(portalId = '-1'): void {
    fixture = TestBed.createComponent(PortalAliasListComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  /**
   * Consumes the collection re-read that a WRITE triggers, answering with `rows`. ⚠ WHY A WRITE ISSUES A
   * SECOND REQUEST AT ALL. Creation used to splice the 201 body onto the array already in hand, while an
   * update re-read the collection because its `PUT` answers with no body; a REMOVAL filtered the row out
   * locally and asked nothing.
   *
   * @param rows The collection the server answers the re-read with.
   */
  function settleWriteReread(rows: readonly PortalAlias[]): void {
    expectRequest('GET', aliasesUrl(-1)).flush(envelope(rows));
    settle();
  }

  /** Settles the view and runs any pending reaction. */
  function settle(): void {
    fixture.detectChanges();
    TestBed.flushEffects();
  }

  /**
   * Consumes exactly one pending request, asserted by verb AND address.
   *
   * @param method The expected verb.
   * @param url The expected address.
   * @returns The recorded request.
   */
  function expectRequest(method: string, url: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      `${method} ${url}`,
    );
  }

  /**
   * Answers the outstanding alias read for one portal.
   *
   * @param rows The collection to answer with.
   * @param portalId The portal being read.
   * @returns The recorded request, so a case may interrogate it further.
   */
  function answerAliases(rows: readonly PortalAlias[], portalId = -1): TestRequest {
    const call = expectRequest('GET', aliasesUrl(portalId));

    call.flush(envelope(rows));
    settle();

    return call;
  }

  /**
   * Mounts the screen and settles its first read.
   *
   * @param rows The collection to answer the first read with.
   * @param portalId The path parameter, as text.
   */
  function arrive(rows: readonly PortalAlias[] = [alias(7, 'localhost')], portalId = '-1'): void {
    create(portalId);
    answerAliases(rows, Number(portalId));
  }

  function host(): HTMLElement {
    // The fixture's host is published untyped, so it is NARROWED BY A REAL RUNTIME TEST rather than by an
    // assertion. An assertion would claim a type the compiler then stops checking and would let an
    // unexpected host travel silently into every query below.
    const element: unknown = fixture.nativeElement;

    if (element instanceof HTMLElement) {
      return element;
    }

    throw new Error('the component host is not an element');
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

  /** A button found by its rendered wording, anywhere on the screen. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** A button found by its rendered wording, asserted to exist, and pressed. */
  function press(label: string): void {
    const control = button(label);

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    control?.click();
    settle();
  }

  /** The open confirmation, or null when none is on screen. */
  function dialogue(): HTMLDialogElement | null {
    return query<HTMLDialogElement>('dialog.confirm-dialog');
  }

  /**
   * Presses a button of the OPEN CONFIRMATION, scoped to the dialogue. ⚠ THIS SCOPING IS LOAD-BEARING,
   * NOT TIDINESS. The form and the dialogue BOTH render a button whose wording is `Cancel`, and the
   * form's comes first in document order — so an unscoped lookup by wording dismisses the ENTRY instead
   * of the question and silently proves something other than what the case claims.
   *
   * @param label The wording of the dialogue button to press.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(control)
      .withContext(`the "${label}" button of the confirmation is offered`)
      .not.toBeUndefined();

    control?.click();
    settle();
  }

  /** Dismisses the open confirmation with the keyboard, the way a person escapes a modal. */
  function pressEscape(): void {
    const open = dialogue();

    expect(open).withContext('a confirmation is open to escape from').not.toBeNull();

    open?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    settle();
  }

  /** The alias entry field, which exists only while the form is open. */
  function entryField(): HTMLInputElement {
    const field = query<HTMLInputElement>(`#${ALIAS_CONTROL_ID}`);

    expect(field).withContext('the alias field is rendered').not.toBeNull();

    // Narrowed rather than asserted, so a missing field fails HERE with a sentence naming the
    // problem instead of surfacing later as a null dereference inside whichever case reached for it.
    if (field === null) {
      throw new Error('the alias field is not rendered');
    }

    return field;
  }

  /** Types a value into the alias field the way a person does. */
  function type(value: string): void {
    const field = entryField();

    field.value = value;
    field.dispatchEvent(new Event('input'));
    settle();
  }

  /** Submits the open form through the form element, which is what the submit control does. */
  function submit(): void {
    const form = query<HTMLFormElement>('form');

    expect(form).withContext('the form is open').not.toBeNull();

    form?.dispatchEvent(new Event('submit'));
    settle();
  }

  /** The painted rows of the listing. */
  function paintedRows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>('tr.data-table__row');
  }

  /** The edit command of one painted row, or undefined when the row withholds it. */
  function rowEditCommand(rowIndex: number): HTMLButtonElement | undefined {
    const row: HTMLTableRowElement | undefined = paintedRows()[rowIndex];

    expect(row).withContext(`row ${rowIndex} is painted`).not.toBeUndefined();

    if (row === undefined) {
      return undefined;
    }

    return Array.from(row.querySelectorAll<HTMLButtonElement>('button')).find(
      (candidate) => (candidate.textContent ?? '').trim() === EDIT_LABEL,
    );
  }

  /** Presses the edit command of one painted row, asserted to be offered. */
  function editRow(rowIndex = 0): void {
    const command = rowEditCommand(rowIndex);

    expect(command).withContext('the edit command is offered on the row').not.toBeUndefined();

    command?.click();
    settle();
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return textOf('.form-field__error');
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls
      .allArgs()
      .map((args) => ({ severity: String(args[0]), message: String(args[1]) }));
  }

  /** The banner's rendered severity word, or null when no banner is showing. */
  function bannerSeverity(): string | null {
    const shown = textOf('.error-banner__severity');

    return shown.length === 0 ? null : shown[0];
  }

  /** Everything the banner is currently saying, joined for a containment assertion. */
  function bannerText(): string {
    return textOf('.error-banner').join(' ');
  }

  /** A host name of the given length, built from characters the shape rule permits. */
  function hostNameOfLength(length: number): string {
    return 'a'.repeat(length);
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — THE ROUTE INPUT, AND THE TWO IDENTITY SEEDS
  // ---------------------------------------------------------------------------------------------------

  describe('the route input', () => {
    it('reads the aliases of portal -1, which is the first portal of an installation', () => {
      create('-1');

      const call = expectRequest('GET', aliasesUrl(-1));

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases');

      call.flush(envelope([alias(7, 'localhost')]));
      settle();

      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('localhost');
    });

    it('reads the aliases of portal 0, which is an ordinary portal and not an absence', () => {
      create('0');

      // Zero is the SECOND portal under that identity seed, so it is as real as any other. A
      // truthiness test on the identifier would send this read nowhere.
      const call = expectRequest('GET', aliasesUrl(0));

      expect(call.request.url).toBe('/api/v1/portals/0/aliases');

      call.flush(envelope([alias(11, 'example.com', 0)]));
      settle();

      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('example.com');
    });

    it('offers the way back to this portal\u2019s settings, built from the same identifier', () => {
      arrive([alias(7, 'localhost')], '-1');

      const links = queryAll<HTMLAnchorElement>('app-page-header a');

      expect(links).toHaveSize(1);
      expect(links[0]?.getAttribute('href')).toBe('/portals/-1/settings');
      expect(textOf('app-page-header a')).toEqual(['Site Settings']);
    });

    it('composes that address for portal 0 as readily as for the negative seed', () => {
      arrive([alias(11, 'example.com', 0)], '0');

      expect(queryAll<HTMLAnchorElement>('app-page-header a')[0]?.getAttribute('href')).toBe(
        '/portals/0/settings',
      );
    });

    it('mounts the shared header as a direct child of the root, with nothing constraining it', () => {
      arrive();

      const root = query<HTMLElement>('.portal-alias-list');
      const header = query<HTMLElement>('app-page-header');

      expect(root).not.toBeNull();
      expect(header).not.toBeNull();
      expect(header?.parentElement).toBe(root as HTMLElement);
    });

    it('keeps the create action first, so navigation cannot displace it', () => {
      arrive();

      expect(textOf('app-page-header button, app-page-header a')).toEqual([
        'Add New HTTP Alias',
        'Site Settings',
      ]);
    });

    it('binds the parameter through an input named exactly portalId', () => {
      fixture = TestBed.createComponent(PortalAliasListComponent);

      expect(() => {
        fixture.componentRef.setInput('portalId', '-1');
      }).not.toThrow();

      fixture.detectChanges();

      answerAliases([alias(7, 'localhost')]);

      expect(fixture.componentInstance.portalId).toBe(-1);
    });

    it('issues exactly one read, because the input and the lifecycle hook do not both load', () => {
      create('-1');

      // The setter issues the read and the lifecycle hook guards against issuing it again. Two
      // reads would double every request this screen makes for the rest of its life.
      answerAliases([alias(7, 'localhost')]);

      // Counted rather than asserted through `expectNone`, which throws and therefore records no
      // expectation: the emptiness of what `match` returns is the claim that the lifecycle hook did not
      // issue a second read.
      expect(httpMock.match((candidate) => candidate.url === aliasesUrl(-1)))
        .withContext('the setter read; the lifecycle hook did not read again')
        .toEqual([]);
    });

    it('re-reads when the route names a different portal, and not when it repeats one', () => {
      arrive([alias(7, 'localhost')]);

      // Re-delivering the SAME parameter must not re-request: the router re-delivers on every navigation
      // that reuses the component, and a request per delivery is a request per keystroke in the address
      // bar.
      fixture.componentRef.setInput('portalId', '-1');
      settle();
      httpMock.expectNone((candidate) => candidate.url === aliasesUrl(-1));

      // A DIFFERENT portal must re-request, or the screen shows the previous tenant's host names.
      fixture.componentRef.setInput('portalId', '0');
      settle();
      answerAliases([alias(11, 'example.com', 0)], 0);

      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('example.com');
      expect(textOf('td.data-table__cell,th.data-table__cell')).not.toContain('localhost');
    });

    it('sends nothing at all for an unusable route parameter, and says why', () => {
      create('not-a-portal');

      // Refusing to guess is the only safe answer, because EVERY integer is a legitimate portal
      // identifier — so a fallback would read a real tenant's host names under a mistyped address.
      httpMock.expectNone((candidate) => candidate.method === 'GET');
      expect(query('app-empty-state')).withContext('the reason is stated').not.toBeNull();
      expect(query('app-data-table')).withContext('and no grid is painted').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE GRID
  // ---------------------------------------------------------------------------------------------------

  describe('the grid', () => {
    it('renders exactly two columns, a command column and the host-name column', () => {
      arrive([alias(7, 'localhost')]);

      // TWO, reproducing the legacy grid exactly: `Website/admin/Portal/portalalias.ascx:L5-L17` declares a
      // narrow template column holding an edit link (L6 gives it a fifteen-pixel item style) and one bound
      // column for the host name. A third column would be an invention.
      const headings = queryAll<HTMLTableCellElement>('th.data-table__header');

      expect(headings).withContext('exactly two columns').toHaveSize(2);
      expect(headings.every((cell) => cell.getAttribute('scope') === 'col'))
        .withContext('each heading is scoped to its column')
        .toBe(true);
    });

    it('names the host-name column from the resource value, not from the markup attribute', () => {
      arrive([alias(7, 'localhost')]);

      expect(textOf('th.data-table__header')).toContain(ALIAS_COLUMN_HEADING);

      // The command column carries no heading text of its own in the legacy grid, so its accessible
      // name is supplied and hidden rather than invented as visible text.
      expect(textOf('th.data-table__header')).toContain(EDIT_LABEL);
    });

    it('offers no delete column, because removal lives on the edit surface', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      expect(textOf('th.data-table__header')).not.toContain(DELETE_LABEL);

      const rowCommands: readonly string[] = paintedRows().flatMap((row) =>
        Array.from(row.querySelectorAll<HTMLButtonElement>('button')).map((control) =>
          (control.textContent ?? '').trim(),
        ),
      );

      expect(rowCommands).withContext('no row offers a removal').not.toContain(DELETE_LABEL);
    });

    it('renders no pager, because the collection is deliberately unpaged', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200'), alias(9, '127.0.0.1')]);

      expect(query('app-pagination')).toBeNull();
    });

    it('renders no filter, because the legacy screen had none', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      // HOST-NAME MATCHING IS THE SERVER'S AND IS NOW EXACT. The legacy tenant-resolution procedure matched
      // on a SUBSTRING — `01.00.00.SqlDataProvider:L4569` declares `GetPortalSettings` and L4580-L4582 read
      // `select @PortalID = min(PortalID) from Portals where PortalAlias like '%' + @PortalAlias + '%'` —
      // so one tenant's alias could resolve to another's, and `min(PortalID)` then picked the lowest of the
      // collisions.
      expect(query('app-search-input')).toBeNull();
    });

    it('states the absence rather than painting an empty grid when a portal has no aliases', () => {
      arrive([]);

      // The screen distinguishes "not yet read" from "read and empty", so this never appears while a
      // request is outstanding.
      expect(query('app-empty-state')).withContext('the empty affordance appears').not.toBeNull();
      expect(textOf('.empty-state__message')).toContain(EMPTY_MESSAGE);
      expect(paintedRows()).withContext('and no row is painted').toHaveSize(0);
    });

    it('withholds the edit command for the alias the request arrived through and offers it for the others', () => {
      arrive([alias(7, 'localhost', -1, false), alias(8, 'localhost:4200', -1, true)]);

      expect(rowEditCommand(0)).withContext('an ordinary row offers the command').not.toBeUndefined();
      expect(rowEditCommand(1)).withContext('the current row withholds it').toBeUndefined();

      expect(rowEditCommand(1)).withContext('still no command on the current row').toBeUndefined();
      expect((paintedRows()[1]?.textContent ?? '').trim()).toBe('In use localhost:4200');
    });

    it('marks an absent host name rather than painting an empty cell, for null and for empty alike', () => {
      arrive([alias(7, null)]);

      const nullRow = (paintedRows()[0]?.textContent ?? '').trim();

      expect(nullRow).withContext('a stored null is marked').toContain('\u2014');
      expect(nullRow)
        .withContext('and the mark carries its words for a reader')
        .toContain('no host name recorded');
      expect(bannerText()).withContext('and it is not a failure').toBe('');

      const mark = query('span[aria-hidden="true"]');
      expect(mark?.textContent?.trim()).toBe('\u2014');
      expect(query('.portal-alias-list__absent-host-description')?.textContent?.trim()).toBe(
        'no host name recorded',
      );

      // A stored EMPTY STRING is answered identically, which is the premise above made explicit.
      arrive([alias(8, '')]);

      expect((paintedRows()[0]?.textContent ?? '').trim()).toContain('\u2014');
    });

    // ⚠ Pf-M3 — the row-qualified accessible name.
    it('qualifies each edit command with the row it acts on, so two blank rows are distinguishable', () => {
      arrive([alias(7, 'first.example'), alias(8, null), alias(9, '')]);

      const commands = paintedRows().map((row) => row.querySelector('button'));

      expect(commands.map((command) => command?.textContent?.trim())).toEqual([
        'Edit',
        'Edit',
        'Edit',
      ]);

      // The ACCESSIBLE name names the row. The two rows with no host name would otherwise be
      // indistinguishable from each other as well as from every other row.
      expect(commands[0]?.getAttribute('aria-label')).toBe('Edit first.example');
      expect(commands[1]?.getAttribute('aria-label')).toBe('Edit no host name recorded');
      expect(commands[2]?.getAttribute('aria-label')).toBe('Edit no host name recorded');

      // WCAG 2.5.3: every accessible name OPENS with the visible word, so speech input still matches.
      for (const command of commands) {
        expect(command?.getAttribute('aria-label')?.startsWith('Edit')).toBeTrue();
      }
    });

    // ⚠ Pf-M4 — requiredness reaches the CONTROL, not only the label beside it.
    it('marks the host-name control itself as required, not merely the field around it', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      expect(entryField().getAttribute('aria-required')).toBe('true');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE INLINE CREATE AND EDIT FORM
  // ---------------------------------------------------------------------------------------------------

  describe('the inline form', () => {
    it('registers an unsaved-entry probe that a closed editor leaves silent', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arrive([alias(7, 'localhost')]);

      expect(tracker.isDirty()).withContext('a closed editor is not unsaved entry').toBeFalse();

      press(ADD_ACTION_LABEL);
      type('www.example.com');

      expect(tracker.isDirty())
        .withContext('a typed alias with no write in flight is what the guard exists to catch')
        .toBeTrue();

      press(CANCEL_LABEL);

      expect(tracker.isDirty())
        .withContext('an abandoned editor must not warn on every later departure')
        .toBeFalse();
    });

    it('opens the create form from the page action, labelled with this screen own add wording', () => {
      arrive([alias(7, 'localhost')]);

      expect(query('form')).withContext('no form before the action is pressed').toBeNull();

      press(ADD_ACTION_LABEL);

      expect(query('form')).withContext('the form opens in place').not.toBeNull();
      expect(button(ADD_SUBMIT_LABEL)).withContext('labelled for a creation').not.toBeUndefined();
      expect(button(UPDATE_SUBMIT_LABEL)).withContext('and not for a replacement').toBeUndefined();
      expect(entryField().value).withContext('and empty').toBe('');
    });

    it('labels the one submit control for a replacement when a row is being edited', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);

      expect(button(UPDATE_SUBMIT_LABEL)).not.toBeUndefined();
      expect(button(ADD_SUBMIT_LABEL)).toBeUndefined();
      expect(entryField().value).withContext('holding the row being edited').toBe('localhost');
    });

    it('refuses a blank entry, says so, and sends nothing', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      submit();

      expect(fieldMessages()).toContain(ALIAS_REQUIRED_MESSAGE);
      expect(entryField().getAttribute('aria-invalid'))
        .withContext('and a reader is told, not only a viewer')
        .toBe('true');
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('caps the entry at the legacy control own length, in the document and in the rule', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      expect(entryField().getAttribute('maxlength')).toBe(String(ALIAS_ENTRY_MAX_LENGTH));

      // And the rule behind it holds for a programmatic write that the attribute cannot bound.
      type(hostNameOfLength(ALIAS_ENTRY_MAX_LENGTH + 1));
      submit();

      expect(fieldMessages()).toContain(ALIAS_ENTRY_TOO_LONG_MESSAGE);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses a host name longer than the column at the column bound, not the entry bound', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // The legacy entry cap OVERSHOT the storage column by fifty-five characters — the column is
      // `[nvarchar] (200)` — so a value between the two bounds was accepted by the browser and could not be
      // stored.
      type(hostNameOfLength(ALIAS_MAX_LENGTH + 1));
      submit();

      expect(fieldMessages()).toContain(ALIAS_TOO_LONG_MESSAGE);
      expect(fieldMessages()).not.toContain(ALIAS_ENTRY_TOO_LONG_MESSAGE);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('accepts the longest permitted host name and sends it whole', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      const longest = hostNameOfLength(ALIAS_MAX_LENGTH);

      type(longest);
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe(longest);

      call.flush(envelope(alias(21, longest)), { status: 201, statusText: 'Created' });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(21, longest)]);

      expect(fieldMessages()).toHaveSize(0);
    });

    it('says nothing at all before a person has acted', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      expect(fieldMessages()).toHaveSize(0);
      expect(entryField().getAttribute('aria-invalid')).toBe('false');
    });

    it('abandons the entry without judging it and without sending anything', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('');

      press(CANCEL_LABEL);

      // The legacy cancel control declared `causesvalidation="False"`, so abandoning the form must not
      // report whatever was half-typed. It redirected to a stored referrer — and to the empty string when
      // there was none; here the form is inline, so cancelling simply closes it.
      expect(query('form')).withContext('the form closes').toBeNull();
      expect(fieldMessages()).withContext('and nothing is reported').toHaveSize(0);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
      httpMock.expectNone((candidate) => candidate.method === 'PUT');
    });

    it('opens the removal prompt without judging the entry', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);
      type('');

      press(DELETE_LABEL);

      expect(fieldMessages()).toHaveSize(0);
      expect(dialogue()).withContext('the question is asked').not.toBeNull();
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
    });
  });

  describe('judging an entry exactly as typed', () => {
    it('refuses a secure protocol prefix, reports it and sends nothing', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type(withScheme(SECURE_SCHEME, 'example.com'));
      submit();

      // The message the screen already promised. It is the server's own sentence, so the operator is
      // told the same thing whichever side notices.
      expect(fieldMessages()).toContain(
        'An HTTP alias must be a host name, an IP address or a server name, optionally followed by ' +
          'a port and a path, and must not include a protocol prefix.',
      );

      // ⚠ AND THE ENTRY IS STILL THERE, UNALTERED. This is the half of the defect that mattered most:
      // the old behaviour replaced what the operator typed with something else. Nothing may rewrite it.
      expect(entryField().value).toBe(withScheme(SECURE_SCHEME, 'example.com'));

      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses an insecure protocol prefix on the same rule, not on a list of schemes', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type(withScheme(INSECURE_SCHEME, 'example.com'));
      submit();

      // The rule tests the separator and never a scheme name, so every scheme is refused identically
      // and there is no list of schemes to fall out of date.
      expect(fieldMessages()).not.toHaveSize(0);
      expect(entryField().value).toBe(withScheme(INSECURE_SCHEME, 'example.com'));
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses a share prefix, because the backslash is a forbidden character', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type('\\\\MYSERVER');
      submit();

      expect(fieldMessages()).not.toHaveSize(0);
      expect(entryField().value).toBe('\\\\MYSERVER');
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses an entry carrying both prefixes', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type(withScheme(SECURE_SCHEME, '\\\\MYSERVER'));
      submit();

      expect(fieldMessages()).not.toHaveSize(0);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('transmits an acceptable entry byte for byte, altering nothing', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // The positive control, and the assertion that closes the defect: what the operator sees is what
      // is sent. A host with a port and a path exercises every optional part of the shape rule at once.
      type('example.com:8443/child');
      submit();

      expect(fieldMessages()).withContext('nothing is reported against an acceptable entry').toHaveSize(0);

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe('example.com:8443/child');
      expect(entryField().value).toBe('example.com:8443/child');

      call.flush(envelope(alias(21, 'example.com:8443/child')), { status: 201, statusText: 'Created' });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(21, 'example.com:8443/child')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
    });

    // THE ADDRESSABLE TOPOLOGY
    // ⚠ WHY THESE CASES EXIST. This screen is one of FIVE mirrors of one contract, whose authority is
    // backend/src/DnnMigration.Domain/Common/PortalAliasTopology.cs.

    it('refuses an entry carrying more than one path segment', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type('example.com/first/second');
      submit();

      // The precise sentence, not merely "something was reported": an operator who is told only that
      // the value is not a storable form cannot tell WHICH part of it was refused.
      expect(fieldMessages()).toContain(
        'An HTTP alias may carry at most 1 path segment beneath its host name; that segment may ' +
          'contain only letters, digits, hyphens and underscores, and may not be one of the addresses ' +
          'this application reserves for itself (api, health, login, modules, openapi, portals, ' +
          'role-groups, roles, settings, swagger, users).',
      );

      expect(entryField().value).toBe('example.com/first/second');
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it("refuses an entry whose segment names one of the deployment's own addresses", () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // `api` is the one that matters most: a tenant addressed at host/api would make every request
      // this console issues ambiguous with the API's own root.
      type('example.com/api');
      submit();

      expect(fieldMessages()).not.toHaveSize(0);
      expect(entryField().value).toBe('example.com/api');
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses an entry whose segment names one of the console screens', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type('example.com/users');
      submit();

      expect(fieldMessages()).not.toHaveSize(0);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('refuses an entry whose path segment carries a dot', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // A dot in the AUTHORITY is ordinary - example.com above is accepted - so this case proves the
      // rule applies to the segment beneath it and not to the whole value.
      type('example.com/acme.co');
      submit();

      expect(fieldMessages()).not.toHaveSize(0);
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('accepts a single segment of letters, digits, hyphens and underscores', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // The positive control for the tightening: everything that WAS addressable still is.
      type('example.com/acme_legal-7');
      submit();

      expect(fieldMessages()).withContext('an addressable segment is not refused').toHaveSize(0);

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe('example.com/acme_legal-7');

      call.flush(envelope(alias(22, 'example.com/acme_legal-7')), {
        status: 201,
        statusText: 'Created',
      });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(22, 'example.com/acme_legal-7')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — THE HTTP CONTRACT
  // ---------------------------------------------------------------------------------------------------

  describe('the wire contract', () => {
    it('reads the collection with no query parameter of any kind', () => {
      create('-1');

      const call = expectRequest('GET', aliasesUrl(-1));

      expect(call.request.params.has('page')).withContext('no page index').toBe(false);
      expect(call.request.params.has('pageSize')).withContext('no page size').toBe(false);
      expect(call.request.params.has('sort')).withContext('no ordering key').toBe(false);
      expect(call.request.params.has('sortDirection')).withContext('no ordering sense').toBe(false);
      expect(call.request.params.has('query')).withContext('no filter').toBe(false);
      expect(call.request.params.keys()).withContext('and nothing else either').toHaveSize(0);

      call.flush(envelope([alias(7, 'localhost')]));
      settle();

      expect(paintedRows()).toHaveSize(1);
    });

    it('creates with the one declared member, announces the measured wording and shows the new row', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type('example.com');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe('example.com');

      call.flush(envelope(alias(21, 'example.com')), { status: 201, statusText: 'Created' });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(21, 'example.com')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('example.com');
      expect(query('form')).withContext('and the form closes').toBeNull();

      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('replaces at the row address, answers 200 with the stored row and then re-reads', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('example.com');
      submit();

      const call = expectRequest('PUT', aliasUrl(-1, 7));

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases/7');
      expect(transmittedAlias(call)).toBe('example.com');

      call.flush(envelope(alias(7, 'example.com')));
      settle();

      answerAliases([alias(7, 'example.com')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('example.com');
    });

    it('shows the host name the server stored, not the one that was typed', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('typed-by-operator.example');
      submit();

      // ⚠ THE RESOLVED DISCREPANCY, ASSERTED FROM THE OTHER SIDE. This case used to prove the screen could
      // not tell a `204` from a `200`, because the controller answered `204` while the migration brief
      // stated `200` and neither could be relied upon.
      expectRequest('PUT', aliasUrl(-1, 7)).flush(envelope(alias(7, 'stored-by-server.example')));
      settle();

      answerAliases([alias(7, 'stored-by-server.example')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('stored-by-server.example');
      expect(textOf('td.data-table__cell,th.data-table__cell'))
        .withContext('the typed spelling is not what the screen reports')
        .not.toContain('typed-by-operator.example');
    });

    it('removes at the row address, answers 204 and drops the row from the listing', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', aliasUrl(-1, 7));

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases/7');
      expect(call.request.body).withContext('a removal carries no body').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      settle();

      expect(notifications()).toEqual([{ severity: 'success', message: DELETED_MESSAGE }]);
      expect(textOf('td.data-table__cell,th.data-table__cell')).not.toContain('localhost');
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('localhost:4200');

      settleWriteReread([alias(8, 'localhost:4200')]);
      expect(textOf('td.data-table__cell,th.data-table__cell')).not.toContain('localhost');
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('localhost:4200');
    });

    it('addresses the row whose command was pressed, not the first one', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200'), alias(9, '127.0.0.1')]);

      editRow(1);
      type('renamed.example.com');
      submit();

      // Off-by-one here would rename the wrong tenant address, which is exactly the class of defect
      // no status code reveals.
      expectRequest('PUT', aliasUrl(-1, 8)).flush(envelope(alias(8, 'renamed.example.com')));
      settle();

      answerAliases([alias(7, 'localhost'), alias(8, 'renamed.example.com'), alias(9, '127.0.0.1')]);

      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('renamed.example.com');
    });

    it('clears the selection when an entry is abandoned, so the next add is not a replace', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      press(CANCEL_LABEL);

      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(envelope(alias(21, 'example.com')), {
        status: 201,
        statusText: 'Created',
      });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(21, 'example.com')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
    });

    it('keeps the submit control out of use while a request is outstanding', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      const control = button(ADD_SUBMIT_LABEL);

      expect(control?.disabled).withContext('a second press cannot queue a second write').toBe(true);

      expectRequest('POST', aliasesUrl(-1)).flush(envelope(alias(21, 'example.com')), {
        status: 201,
        statusText: 'Created',
      });
      settle();
      settleWriteReread([alias(7, 'localhost'), alias(21, 'example.com')]);
    });

    it('shows the wait while a read is outstanding, and only then', () => {
      create('-1');

      expect(query('app-loading-spinner')).withContext('the wait is shown').not.toBeNull();

      answerAliases([alias(7, 'localhost')]);

      expect(query('app-loading-spinner')).withContext('and withdrawn once answered').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — REFUSALS AND FAULTS
  // ---------------------------------------------------------------------------------------------------

  describe('a refused write', () => {
    it('reports a duplicate host name beside the field on a creation, in this screen own wording', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('localhost');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problemDocument(
          DUPLICATE_ALIAS_CODE,
          409,
          'The host name is already bound to a portal.',
        ),
        { status: 409, statusText: 'Conflict' },
      );
      settle();

      expect(fieldMessages()).toContain(DUPLICATE_ALIAS_MESSAGE);

      const sharedSentence: string = CONFLICT_MESSAGE[DUPLICATE_ALIAS_CODE];

      expect(sharedSentence)
        .withContext('the two wordings really are different')
        .not.toBe(DUPLICATE_ALIAS_MESSAGE);
      expect(fieldMessages()).not.toContain(sharedSentence);

      expect(fieldMessages()).toEqual([DUPLICATE_ALIAS_MESSAGE]);
      expect(bannerText()).withContext('and it is not repeated in the banner').not.toContain(
        DUPLICATE_ALIAS_MESSAGE,
      );
    });

    it('keeps the server sentence and the support reference for a duplicate host name', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('localhost');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problemDocument(
          DUPLICATE_ALIAS_CODE,
          409,
          "The host name 'localhost' is already bound to a portal.",
        ),
        { status: 409, statusText: 'Conflict' },
      );
      settle();

      // The field attribution is unchanged - this is an addition, not a replacement.
      expect(fieldMessages()).toEqual([DUPLICATE_ALIAS_MESSAGE]);

      expect(bannerText()).toContain("The host name 'localhost' is already bound to a portal.");

      // And the reference survives, exactly as it does for every failure with no field to sit beside.
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    it('reports a duplicate host name beside the field on a replacement as well', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      type('localhost:4200');
      submit();

      // THE BARE, BROAD `Catch` IS GONE. The legacy replacement path wrapped its write in `Try … Catch`
      // with no exception variable and no filter and reported a DUPLICATE HOST NAME for anything it caught
      // — a dropped connection, a timeout, a permission refusal, all described as a name collision.
      expectRequest('PUT', aliasUrl(-1, 7)).flush(
        problemDocument(DUPLICATE_ALIAS_CODE, 409, 'The host name is already bound to a portal.'),
        { status: 409, statusText: 'Conflict' },
      );
      settle();

      expect(fieldMessages()).toContain(DUPLICATE_ALIAS_MESSAGE);
      expect(fieldMessages()).not.toContain(CONFLICT_MESSAGE[DUPLICATE_ALIAS_CODE]);
    });

    it('does not mislabel a fault on the replacement path as a duplicate', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('example.com');
      submit();

      // The other half of the same migration: a `500` is the exact case the legacy bare `Catch` reported as
      // a collision. The server's own sentence is shown instead, in the banner, where a failure with no
      // field to sit beside belongs.
      expectRequest('PUT', aliasUrl(-1, 7)).flush(
        problemDocument(SERVER_FAULT_CODE, 500, 'The server could not complete the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      settle();

      expect(fieldMessages()).not.toContain(DUPLICATE_ALIAS_MESSAGE);
      expect(bannerText()).toContain('The server could not complete the request.');
      expect(bannerSeverity()).withContext('a fault is presented as a fault').toBe('Error');
    });

    it('reports a per-field refusal against the control the server named', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      // ⚠ TWO DIFFERENT KEYS ARE IN PLAY. The WIRE member is camel-cased; the model-state error-dictionary
      // key is the DTO property name and is therefore Pascal-cased.
      const refusal = problemDocument(
        VALIDATION_FAILED_CODE,
        400,
        'The request was not valid.',
        { [MODEL_STATE_ALIAS_KEY]: ['The HTTP alias is not acceptable.'] },
      );

      expectRequest('POST', aliasesUrl(-1)).flush(refusal, {
        status: 400,
        statusText: 'Bad Request',
      });
      settle();

      // Read back with BRACKET ACCESS, because the field map is an index-signature type and this
      // workspace forbids property access on one.
      const reported: readonly string[] = reportedAliasMessages(refusal);

      expect(reported).toHaveSize(1);
      expect(fieldMessages()).toContain(reported[0]);
    });

    it('retains the trace identifier, which is the operator only join key when it is the only one sent', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('example.com');
      submit();

      expectRequest('PUT', aliasUrl(-1, 7)).flush(
        problemDocument(
          SERVER_FAULT_CODE,
          500,
          'The server could not complete the request.',
          undefined,
          TRACE_ONLY,
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      settle();

      expect(textOf('.error-banner__trace').join(' ')).toContain(TRACE_ID);
    });

    it('quotes the correlation identifier when the server sends one', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problemDocument(NOT_PERMITTED_CODE, 403, 'The caller is not permitted to read this.'),
        { status: 403, statusText: 'Forbidden' },
      );
      settle();

      // Both identifiers are independent values in different formats, and the correlation identifier
      // is the one the server validated for the request and wrote to its own log.
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    it('announces a refused reading as a view refusal, at warning severity', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problemDocument(NOT_PERMITTED_CODE, 403, 'The caller is not permitted to read this.'),
        { status: 403, statusText: 'Forbidden' },
      );
      settle();

      expect(notifications()).toEqual([{ severity: 'warning', message: VIEW_DENIED_MESSAGE }]);
      expect(bannerSeverity()).withContext('and the surface agrees').toBe('Warning');
    });

    it('announces a refused removal in this screen own delete wording, at warning severity', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', aliasUrl(-1, 7)).flush(
        problemDocument(NOT_PERMITTED_CODE, 403, 'The caller is not permitted to do this.'),
        { status: 403, statusText: 'Forbidden' },
      );
      settle();

      expect(notifications()).toEqual([{ severity: 'warning', message: DELETE_DENIED_MESSAGE }]);
      expect(bannerSeverity()).toBe('Warning');
      expect(textOf('td.data-table__cell,th.data-table__cell'))
        .withContext('and nothing was removed')
        .toContain('localhost');
    });

    it('announces a refusal once, however many times the screen re-renders', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problemDocument(NOT_PERMITTED_CODE, 403, 'Not permitted.'),
        { status: 403, statusText: 'Forbidden' },
      );
      settle();
      settle();
      settle();

      // The announcing reaction holds the failure it last announced BY REFERENCE, so a re-render
      // cannot repeat it. A message a person sees three times is a message they learn to ignore.
      expect(notifications()).toHaveSize(1);
    });

    it('shows a portal that does not exist without announcing a permission refusal', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problemDocument('portal.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      settle();

      expect(notifications()).toHaveSize(0);
      expect(bannerText()).toContain('The requested resource does not exist.');
    });

    it('refuses a write to the alias the request arrived through, and says how to recover', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      type('renamed.example.com');
      submit();

      // Both alias endpoints answer `409` for TWO different reasons, so the STATUS is not diagnostic and
      // the published code is what tells them apart. This is the second reason, and it must not be
      // described as a duplicate.
      expectRequest('PUT', aliasUrl(-1, 7)).flush(
        problemDocument(
          ACTIVE_ALIAS_CODE,
          409,
          'The alias the request arrived through cannot be changed.',
        ),
        { status: 409, statusText: 'Conflict' },
      );
      settle();

      expect(fieldMessages()).not.toContain(DUPLICATE_ALIAS_MESSAGE);
      expect(fieldMessages().join(' ')).toContain('cannot be changed or removed');
    });

    it('renders hostile resource text as inert text, parsing no element out of it', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      const hostile = '<script>alert(1)</script>Enter a valid alias.';

      expectRequest('POST', aliasesUrl(-1)).flush(
        problemDocument(VALIDATION_FAILED_CODE, 400, `${hostile} And nothing ran.`, {
          [MODEL_STATE_ALIAS_KEY]: [hostile],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      settle();

      expect(host().querySelectorAll('script'))
        .withContext('no element was parsed out of it')
        .toHaveSize(0);
      expect(fieldMessages()).withContext('and the characters are shown as characters').toContain(
        hostile,
      );
    });

    it('renders a message that arrives with a leading break tag without it', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problemDocument(VALIDATION_FAILED_CODE, 400, 'The request was not valid.', {
          [MODEL_STATE_ALIAS_KEY]: ['<br/>You Must Enter a Valid HTTP Alias'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      settle();

      expect(fieldMessages()).toContain('You Must Enter a Valid HTTP Alias');
      expect(fieldMessages().join(' ')).not.toContain('<br');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE REMOVAL GUARDS AND THE CONFIRMATION
  // ---------------------------------------------------------------------------------------------------

  // PROOF 6b — WHERE THE FORM SITS, AND WHERE FOCUS GOES
  // ⚠ NOTHING MANAGED FOCUS ACROSS THIS FORM, AND THE FORM RENDERED AFTER THE WHOLE GRID. Measured, the
  // editor appeared at y≈550 while the row that opened it sat at y≈496, and reaching it from that row by
  // keyboard took twenty-two tab stops - every remaining cell and command of the table came first.

  describe('where the form sits and where focus goes', () => {
    it('renders the form BEFORE the listing it edits', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);

      const form = query<HTMLElement>('form.portal-alias-list__edit');
      const table = query<HTMLElement>('app-data-table');

      expect(form).not.toBeNull();
      expect(table).not.toBeNull();

      // Document order, asserted through the DOM's own comparison rather than by counting elements:
      // `DOCUMENT_POSITION_FOLLOWING` on the form means the table comes after it.
      const relation: number = form?.compareDocumentPosition(table as Node) ?? 0;

      expect(relation & Node.DOCUMENT_POSITION_FOLLOWING)
        .withContext('the listing follows the form, so the form is not behind the whole table')
        .toBeGreaterThan(0);
    });

    it('moves focus into the entry box when a row is opened for editing', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);

      expect(document.activeElement).toBe(entryField());
    });

    it('moves focus into the entry box when the create action is pressed', () => {
      arrive();
      press(ADD_ACTION_LABEL);

      expect(document.activeElement).toBe(entryField());
    });

    it('hands focus back to the control that opened the form when it is cancelled', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      const invoker = rowEditCommand(0);

      expect(invoker).not.toBeUndefined();
      invoker?.focus();
      editRow(0);

      expect(document.activeElement).toBe(entryField());

      press(CANCEL_LABEL);

      expect(document.activeElement)
        .withContext('back to the row command, not dropped to the document body')
        .toBe(invoker as Element);
      expect(document.activeElement).not.toBe(document.body);
    });

    it('falls back to the create action when the invoker has left the document', () => {
      // A save re-reads the collection, so the row that was edited is replaced and the remembered element
      // is detached.
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      const invoker = rowEditCommand(0);

      invoker?.focus();
      editRow(0);
      type('renamed.example.com');
      submit();

      expectRequest('PUT', aliasUrl(-1, 7)).flush(envelope(alias(7, 'renamed.example.com')));
      settle();

      expectRequest('GET', aliasesUrl(-1)).flush(envelope([alias(7, 'renamed.example.com')]));
      settle();

      expect(document.activeElement)
        .withContext('somewhere usable rather than the document body')
        .not.toBe(document.body);
      expect((document.activeElement?.textContent ?? '').trim()).toBe(ADD_ACTION_LABEL);
    });

    it('says nothing about the entry while a removal is pending', () => {
      // ⚠ THE INTERACTION THE FOCUS MOVE CREATED, AND THE REASON THE GUARD IS WHERE IT IS. Pressing Delete
      // only OPENS the prompt; the blur arrives one render later, when the confirmation opens modally and
      // the platform moves focus onto its Cancel button.
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);
      type('');

      press(DELETE_LABEL);

      expect(dialogue()).withContext('the question is asked').not.toBeNull();
      expect(fieldMessages()).toHaveSize(0);
    });
  });

  describe('the removal guards', () => {
    it('gives the inline removal command the destructive treatment', () => {
      // ⚠ THE MEASURED DEFECT. Delete sat between Update and Cancel as a BARE button, indistinguishable
      // from either, while the confirmation it opens paints its own Delete with a red border and a danger
      // label.
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);

      const remove = button(DELETE_LABEL);

      expect(remove).withContext('the removal command is offered').not.toBeUndefined();

      if (remove === undefined) {
        return;
      }

      expect(getComputedStyle(remove).color)
        .withContext('#FF0000, the danger token, as on the listing row commands')
        .toBe('rgb(255, 0, 0)');

      const cancel = button(CANCEL_LABEL);

      expect(cancel).not.toBeUndefined();
      expect(getComputedStyle(remove).color)
        .withContext('and distinguishable from the non-destructive command beside it')
        .not.toBe(cancel === undefined ? '' : getComputedStyle(cancel).color);
    });

    it('withholds removal while a portal has only one alias', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);

      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('withholds removal for a portal with no aliases at all', () => {
      arrive([]);

      press(ADD_ACTION_LABEL);

      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('offers removal once a portal has more than one alias', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);

      expect(button(DELETE_LABEL)).withContext('two aliases, so one may go').not.toBeUndefined();
    });

    it('offers removal only for a row that exists, never for an unsaved entry', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      press(ADD_ACTION_LABEL);

      // A DELIBERATE DIVERGENCE, and the legacy behaviour was a defect.
      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('withholds removal for the alias the request arrived through', () => {
      arrive([alias(7, 'localhost', -1, true), alias(8, 'localhost:4200')]);

      // The withheld row cannot be reached by its own command, so the row press is used — which the
      // screen answers by explaining rather than by doing nothing.
      const withheldRow = paintedRows()[0];

      withheldRow?.click();
      settle();

      expect(query('form')).withContext('no form is opened on it').toBeNull();
      expect(notifications()).toHaveSize(1);
      expect(notifications()[0].severity).toBe('warning');
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
    });
  });

  describe('the confirmation', () => {
    it('asks before removing, and asks with the platform own wording', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);

      press(DELETE_LABEL);

      const open = dialogue();

      expect(open).not.toBeNull();

      expect(textOf('.confirm-dialog__message')).toEqual([
        `${DELETE_CONFIRM_MESSAGE} localhost`,
      ]);

      // The dialogue is opened MODALLY, which is what confines focus natively — the trap is the
      // platform's rather than hand-rolled, and this is the observable proof that it is in force.
      expect(open?.open).withContext('opened as a modal').toBe(true);
      expect(host().contains(document.activeElement))
        .withContext('and focus has moved inside it')
        .toBe(true);

      // ⚠ ASKING SENDS NOTHING. A prompt that had already issued the removal would be theatre.
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
    });

    it('cancels on Escape and sends nothing', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);
      press(DELETE_LABEL);

      pressEscape();

      expect(dialogue()).withContext('the question is withdrawn').toBeNull();
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
      expect(notifications()).toHaveSize(0);
      expect(query('form')).withContext('and the entry survives the dismissal').not.toBeNull();
    });

    it('sends nothing when the question is dismissed by its own cancel command', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);
      press(DELETE_LABEL);

      pressDialogue(CANCEL_LABEL);

      expect(dialogue()).toBeNull();
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
      expect(textOf('td.data-table__cell,th.data-table__cell')).toContain('localhost');
    });

    it('removes only once the question is answered', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);
      editRow(0);
      press(DELETE_LABEL);

      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', aliasUrl(-1, 7)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      settle();

      // The removal re-reads the collection, as every other write on this resource does.
      settleWriteReread([alias(8, 'localhost:4200')]);

      expect(dialogue()).withContext('the question is closed').toBeNull();
      expect(query('form')).withContext('and the form with it').toBeNull();
      expect(notifications()).toEqual([{ severity: 'success', message: DELETED_MESSAGE }]);
      expect(paintedRows()).toHaveSize(1);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE STORE COUPLING
  // ---------------------------------------------------------------------------------------------------

  describe('the store coupling', () => {
    it('exposes the alias slices as readonly signals the screen cannot write', () => {
      arrive([alias(7, 'localhost')]);

      expect('set' in store.aliases).withContext('the collection is not writable').toBe(false);
      expect('update' in store.aliases).toBe(false);
      expect('set' in store.aliasesPortalId).toBe(false);
      expect('update' in store.aliasesPortalId).toBe(false);
      expect('set' in store.selectedAliasId).toBe(false);
      expect('update' in store.selectedAliasId).toBe(false);
      expect('set' in store.aliasLoading).toBe(false);
      expect('update' in store.aliasLoading).toBe(false);
      expect('set' in store.aliasFailure).toBe(false);
      expect('update' in store.aliasFailure).toBe(false);
    });

    it('is unharmed when a caller mutates a collection it handed out', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      const handedOut: readonly PortalAlias[] | null = store.aliases();

      expect(handedOut).not.toBeNull();

      const copy: PortalAlias[] = [...(handedOut ?? [])];

      copy.length = 0;

      expect(store.aliases()).withContext('the slice still holds both rows').toHaveSize(2);
      expect(paintedRows()).withContext('and the grid still paints both').toHaveSize(2);
    });

    it('reads and writes only through the store, never through a transport of its own', () => {
      // The screen declares no providers and injects no transport: the whole of its data path is the store,
      // and the store is what composes an address. Substituting the store with a Jasmine double therefore
      // silences the wire completely — which is only true if nothing else on the screen can reach it.
      const listSpy = spyOn(store, 'loadAliases').and.callFake(() => undefined);

      create('-1');

      // ⚠ THE CALL COUNT IS DELIBERATELY NOT ASSERTED HERE, and the reason is worth recording rather than
      // working around.
      expect(listSpy).toHaveBeenCalledWith(-1);
      expect(listSpy).toHaveBeenCalledWith(jasmine.any(Number));

      // This is the claim the case exists for: with the one data path silenced, NOTHING reaches the
      // wire — so the screen holds no transport of its own and composes no address of its own.
      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('forwards a creation to the store with the portal from the address and the entry verbatim', () => {
      arrive([alias(7, 'localhost')]);

      const createSpy = spyOn(store, 'createAlias').and.returnValue(EMPTY);

      press(ADD_ACTION_LABEL);

      type('example.com');
      submit();

      expect(createSpy).toHaveBeenCalledWith(
        -1,
        jasmine.objectContaining({ httpAlias: 'example.com' }),
      );
    });
  });
});
