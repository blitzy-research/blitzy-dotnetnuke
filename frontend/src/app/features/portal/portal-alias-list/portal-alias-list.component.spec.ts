/**
 * Specification for {@link PortalAliasListComponent} — the HTTP aliases of one portal.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * An alias is not decoration: it is the ONLY thing that resolves an incoming request to a
 * tenant. The API matches the request's host header against `dbo.PortalAlias` with an EXACT
 * comparison, so an alias saved wrongly, saved against the wrong portal, or silently
 * normalised into something else takes a whole tenant off the air — and no status code
 * anywhere reveals it. Every assertion below exists because of that consequence.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - The component is mounted as the standalone unit it is, and the portal identifier is
 *     delivered the way the router delivers it: through `componentRef.setInput`, as the STRING
 *     a route parameter is. That is what exercises the input's own transform rather than
 *     bypassing it.
 *   - {@link PortalStore} is genuine and pinned to each case's injector, so its commands, its
 *     continuations and its cancellation all behave as they do in life, and no state leaks
 *     between cases.
 *   - Every request is answered through `HttpTestingController`, so each assertion about an
 *     address, a body or a status is an assertion about the wire. `verify()` runs after every
 *     case, because an unflushed or unexpected request that nobody verifies is the single most
 *     common way an Angular HTTP specification reports a false green.
 *   - `NotificationService.notify` is spied and called through, so the announcements are
 *     observable without being faked.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ `dbo.Portals.PortalID` IS `IDENTITY(-1, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L76-L77`). Minus
 * one is the FIRST portal of an installation and zero is the second, while minus one is ALSO
 * the legacy absent-integer marker `Null.NullInteger`. Both values must therefore travel into
 * the address untouched, and no expression may treat either as "no portal". The input's
 * transform distinguishes them from an unusable parameter by yielding `NaN`, which is the only
 * value this screen treats as absent.
 *
 * ⚠ A COLLECTION ENVELOPE IS `{ data, meta }` HERE, NOT `{ items, meta }`. The alias list is
 * deliberately UNPAGED — a tenant has a handful of host names, and a pager that truncated them
 * would hide the very alias a tenant is failing to resolve on — so the body is the
 * single-resource envelope carrying an array, with `meta` null.
 *
 * ⚠ THE WRITE PATHS DIFFER IN WHETHER THEY RE-READ, and the difference is observable as a
 * second request. A replacement is answered with no body, so the store RE-READS the collection
 * rather than guessing the stored value from the request; a creation is answered with the
 * created row, so the store appends it and re-reads nothing; a removal is exact for an unpaged
 * collection, so the store filters locally and re-reads nothing. Each is asserted as measured.
 *
 * ⚠ RATE LIMITING IS NOT MODELLED, AND THAT OMISSION IS DELIBERATE. See the note above
 * {@link problemDocument} — the limiter is registered against the authentication routes alone,
 * so `429` cannot arise from a portal-alias request and no case pretends otherwise.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

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

// =====================================================================================================
// ADDRESSES
//
// ⚠ RELATIVE, AND THAT IS A DEPLOYMENT REQUIREMENT RATHER THAN A CONVENIENCE. The test target
// declares no file replacement, so a specification compiles against the PRODUCTION environment
// module, whose API base is the root-relative `/api/v1`. The containerised application is served
// through a proxy that forwards `/api/` to the API, so an absolute base would bypass the proxy and
// turn every call into a cross-origin request. Nothing in the toolchain detects that, which is why
// the addresses are written out here rather than read back off the endpoint registry: a change to
// the registry has to fail these cases rather than travel through them.
// =====================================================================================================

/** The alias collection of one portal. */
function aliasesUrl(portalId: number): string {
  return `/api/v1/portals/${portalId}/aliases`;
}

/**
 * One alias of one portal.
 *
 * MIGRATION: THE LEGACY FOUR-LETTER ABBREVIATED QUERY KEY DOES NOT SURVIVE. The legacy listing
 *   composed an edit address from it — `Website/admin/Portal/portalalias.ascx:L8` passes that
 *   abbreviation to `EditURL` as its first argument — and the edit control read it straight back
 *   out of the query string at `Website/admin/Portal/EditPortalAlias.ascx.vb:L55`, coercing it to
 *   an integer at L57. The successor is the PATH SEGMENT below, carrying the member's full name.
 *
 *   ⚠ THE ABBREVIATION ITSELF IS DELIBERATELY NOT SPELLED ANYWHERE IN THIS FILE, not even inside
 *   this comment, so that a search for it across the target tree returns nothing at all. Naming it
 *   here would make the very check that proves it gone report that it had survived.
 */
function aliasUrl(portalId: number, portalAliasId: number): string {
  return `${aliasesUrl(portalId)}/${portalAliasId}`;
}

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported: the component exports none of it, and a specification that read the
// wording off the component could not detect a change to it. Every value below is a legacy resource
// VALUE, measured in the file named beside it, and never a markup attribute.
// =====================================================================================================

/** `ControlTitle_.Text` in `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx`. */
const HEADING = 'Portal Aliases';

/** `AddContent.Action` in the same file; published as a module action at `PortalAlias.ascx.vb:L90`. */
const ADD_ACTION_LABEL = 'Add New HTTP Alias';

/**
 * `HTTP Alias.Header` in the same file — note the SPACE inside the resource key.
 *
 * MIGRATION: the heading is the RESOURCE VALUE and not the markup attribute. The legacy grid
 *   declared `HeaderText="HTTP Alias"` inline at `Website/admin/Portal/portalalias.ascx:L14`, but
 *   `Localization.LocalizeDataGrid` at `Website/admin/Portal/PortalAlias.ascx.vb:L69` replaced it
 *   at run time from the resource key. The two happen to agree, which is exactly why asserting
 *   the attribute would prove nothing about which of them the screen honours.
 */
const ALIAS_COLUMN_HEADING = 'HTTP Alias';

/** `Edit.Text` in the GLOBAL `Website/App_GlobalResources/SharedResources.resx`. */
const EDIT_LABEL = 'Edit';

/** `cmdDelete.Text`, global. */
const DELETE_LABEL = 'Delete';

/** `cmdCancel.Text`, global. */
const CANCEL_LABEL = 'Cancel';

/**
 * `cmdAdd.Text` in `EditPortalAlias.ascx.resx`, measured ABSENT from the global file.
 *
 * MIGRATION: THERE IS NO SEPARATE ADD CONTROL. `cmdAdd` is a resource KEY read into the ONE
 *   submit control, `cmdUpdate` — `Website/admin/Portal/editportalalias.ascx:L11` declares a
 *   single link button, and `EditPortalAlias.ascx.vb` set its text from `cmdAdd` on both add
 *   branches (L83 and L88) and from `cmdUpdate` on the edit branch (L75). One control, two
 *   labels, chosen by mode.
 */
const ADD_SUBMIT_LABEL = 'Add New Alias';

/** `cmdUpdate.Text`, global; the edit-mode label of that same one control. */
const UPDATE_SUBMIT_LABEL = 'Update';

/** `DeleteItem.Text`, global. Measured ABSENT from both local files. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `Success.Text` in `EditPortalAlias.ascx.resx`, measured ABSENT from the global file. */
const SAVED_MESSAGE = 'The Portal Alias has been saved.';

/** Authored wording; the legacy delete handler emitted no message at all. */
const DELETED_MESSAGE = 'The Portal Alias has been deleted.';

/** `DuplicateAlias.Text` in `EditPortalAlias.ascx.resx` — THIS screen's own, terser wording. */
const DUPLICATE_ALIAS_MESSAGE = 'The Portal Alias already exists.';

/** The hard-coded English literal at `EditPortalAlias.ascx.vb:L67`, which has no resource entry. */
const VIEW_DENIED_MESSAGE = 'You do not have access to view this Portal Alias.';

/** `AccessDenied.Text` in `EditPortalAlias.ascx.resx`, read by the handler at L183. */
const DELETE_DENIED_MESSAGE = 'You do not have access to delete this Portal Alias.';

/** Authored wording; neither resource file declares an empty-state key. */
const EMPTY_MESSAGE = 'This portal has no HTTP aliases.';

/** The server's own `NotEmpty` wording, reproduced so one refusal is not described two ways. */
const ALIAS_REQUIRED_MESSAGE = 'An HTTP alias is required.';

/** The entry cap's wording. The cap itself is the legacy control's `MaxLength`. */
const ALIAS_ENTRY_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 255 characters.';

/** The authoritative maximum, applied to the value that is actually transmitted. */
const ALIAS_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 200 characters.';

// =====================================================================================================
// MEASURED LIMITS AND IDENTIFIERS
// =====================================================================================================

/**
 * The separator the legacy strip is keyed on.
 *
 * `Website/admin/Portal/EditPortalAlias.ascx.vb:L210-L212` tests
 * `strAlias.IndexOf("://") <> -1` and then removes `IndexOf("://") + 3` characters — so the strip is
 * on THIS separator and never on a scheme NAME, which is why there is no list of schemes anywhere to
 * fall out of date.
 *
 * ⚠ THE SCHEME FIXTURES BELOW ARE COMPOSED FROM IT RATHER THAN WRITTEN OUT WHOLE, for two reasons.
 * It states the measured rule structurally instead of by comment, so a fixture cannot drift from the
 * thing it is testing. And it keeps a fully-qualified address literal out of this file entirely, so
 * the check that proves no absolute API base survived anywhere in the target tree finds nothing here
 * to explain away.
 */
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
 * `Website/admin/Portal/editportalalias.ascx:L7`, preserved exactly so the typing experience is
 * the legacy one. The storage column is the shorter 200, which is why both bounds are asserted
 * and why they are asserted against different values.
 */
const ALIAS_ENTRY_MAX_LENGTH = 255;

/** The authoritative maximum, measured on the NORMALISED value. */
const ALIAS_MAX_LENGTH = 200;

/** The element identifier the shared field's label association names, so the two cannot drift. */
const ALIAS_CONTROL_ID = 'portal-alias-http-alias';

/**
 * The name of the one form control, which is also the WIRE MEMBER of both write contracts.
 *
 * ⚠ TWO DIFFERENT STRINGS ARE IN PLAY AND THEY ARE NOT INTERCHANGEABLE. This is the wire key,
 * camel-cased by the server's serialiser policy. The MODEL-STATE ERROR-DICTIONARY key is
 * {@link MODEL_STATE_ALIAS_KEY}, which is the DTO property name and therefore Pascal-cased. The
 * shared field reader lower-cases only the FIRST character of a reported key, which is precisely
 * what makes the second resolve onto the first.
 */
const ALIAS_WIRE_KEY = 'httpAlias';

/** The Pascal-cased key a model binder reports a per-field refusal under. */
const MODEL_STATE_ALIAS_KEY = 'HttpAlias';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
//
// ⚠ EVERY DOCUMENT BELOW IS ONE THE API CAN EMIT. The type is built by the server's own type builder,
// so it is lower-cased with hyphens folded to underscores; the title comes from the status vocabulary;
// and the diagnostic identifiers are attached to every document. No document carries an `instance`
// member, because every call site supplies null and the serialiser omits it.
//
// MIGRATION: `429` IS DELIBERATELY NOT TESTED, AND THE OMISSION IS REASONED RATHER THAN AN
// OVERSIGHT. Rate limiting is registered against `/api/v1/auth/*` alone — one named policy, ten
// requests per sixty seconds, partitioned by address — so a portal-alias request cannot be
// throttled at all. A case asserting that it can would assert a behaviour the application does not
// have, and would then keep passing after the behaviour it claims to guard was removed. The legacy
// screen had no throttling of any kind either, so there is nothing to preserve.
// =====================================================================================================

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
 * A fixed W3C trace identifier.
 *
 * ⚠ FIXED, NEVER GENERATED. Nothing in this file reads a clock or a random source, so no case can
 * pass on one run and fail on the next. It is a diagnostic identifier and not a credential — no
 * real secret appears in any fixture here, which the legacy configuration cannot claim: it
 * committed a reversible decryption key at `Website/release.config:L89-L93`.
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
 * Builds one RFC 7807 document, typed as the real contract.
 *
 * ⚠ THE RETURN TYPE IS THE PRODUCTION INTERFACE, WHICH IS THE POINT. A mis-cased or invented
 * member is then a compile error rather than a value that silently reads back as undefined — and
 * the casing hazard is real, because the serialiser lower-cases the leading upper-case RUN of a
 * name, so `PortalID` becomes `portalID` while `PortalId` becomes `portalId`.
 *
 * Every document is built fresh. Nothing here hands one case a fixture another case has held, so
 * no case can be corrupted by a neighbour.
 *
 * @param code The application failure code, without its namespace prefix.
 * @param status The status the document declares, which is also the transport status.
 * @param detail The occurrence-specific sentence.
 * @param errors The per-field map, attached ONLY for a case exercising a model-state refusal —
 * never as an empty object, because the server does not emit one.
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
 * Reads the messages one document reported for the alias field.
 *
 * ⚠ BRACKET ACCESS, NEVER DOT ACCESS. The field map is an index-signature type and this workspace
 * enables `noPropertyAccessFromIndexSignature`, so reaching the entry through a dotted member name
 * would not compile at all. The absence of the map is handled by substituting the frozen empty one
 * rather than by asserting non-null.
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
 * `isCurrent` defaults to FALSE — not the alias this request arrived through — so every ordinary
 * case keeps both the rename and the unbind affordance. The cases that exercise the withholding
 * pass `true` explicitly, which is what makes the withholding legible at the call site rather
 * than incidental to a default.
 *
 * @param portalAliasId The row's surrogate key. Every integer is meaningful.
 * @param httpAlias The host name, or null for a row that carries none.
 * @param portalId The owning portal. Defaults to the first portal of an installation.
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
 * The single-resource envelope exactly as the server writes it.
 *
 * DECLARED HERE RATHER THAN IMPORTED, and the reason is a dependency boundary rather than
 * convenience: the paging contract is not among this specification's declared dependencies, and a
 * specification has no business reaching for a module it was not given. Nothing is lost by
 * restating three members' worth of shape, because AGREEMENT IS ENFORCED AT RUN TIME ANYWAY — the
 * transport decodes every response through the real envelope decoder, so a body shaped by this
 * declaration that the production contract no longer recognises fails the decode and fails the
 * case, loudly, rather than passing on a stale local type.
 *
 * `meta` is PRESENT AND NULL rather than absent, because that is what the serialiser writes: it
 * emits every declared member including one holding null, so absence is expressed by the value and
 * never by the member disappearing.
 */
interface SingleResourceEnvelope<T> {
  /** The payload the endpoint produced. */
  readonly data: T;

  /** The paging metadata, which is always null on this envelope — it describes no page. */
  readonly meta: null;
}

/**
 * The single-resource envelope, which is what an UNPAGED collection travels in.
 *
 * MIGRATION: THE COLLECTION IS DELIBERATELY UNPAGED, so no paged envelope and no paging
 *   contract appears anywhere in this file. The legacy listing bound a plain `ArrayList` —
 *   `Website/admin/Portal/PortalAlias.ascx.vb:L39` declares it and L42 fills it from
 *   `GetPortalAliasArrayByPortalID`, with L43-L44 binding and rendering it — and
 *   `Website/admin/Portal/portalalias.ascx` declares no pager. `meta` is present and null
 *   rather than absent, because that is what the serialiser writes.
 *
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
 * A type predicate rather than an assertion, so a body that does not match is a FAILED CASE
 * rather than a value read through a cast. The alternative — widening the body and reading it —
 * is what lets a contract change travel silently through a specification.
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
    // ⚠ THE ORDER OF THESE TWO IS LOAD-BEARING. The real client is provided FIRST and the testing
    // backend SECOND, so that the second displaces the first. Reversing them leaves the live
    // backend in place and every case below attempts a real network request.
    //
    // ⛔ No component-declaring array appears here, because every component in this application is
    // standalone; and neither of the deprecated router or HTTP testing modules appears either, in
    // favour of the two providers used instead.
    //
    // The store is pinned to this injector so each case owns its own instance and no slice leaks
    // between cases. The router is provided because the shell's own routing surface is reachable
    // from the components this screen composes.
    await TestBed.configureTestingModule({
      imports: [PortalAliasListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), PortalStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(PortalStore);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    // ⚠ MANDATORY. Without this, a request nobody flushed and a request nobody expected both pass
    // in silence, which is the commonest way an HTTP specification reports a green it has not
    // earned.
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Mounts the screen with a route parameter.
   *
   * ⚠ THE PARAMETER IS DELIVERED AS A STRING, BECAUSE THAT IS WHAT A ROUTE PARAMETER IS.
   * Component input binding hands path parameters over as text, so passing a number here would
   * bypass the input's own transform and prove nothing about how the screen behaves when routed
   * to.
   *
   * @param portalId The path parameter, as text.
   */
  function create(portalId = '-1'): void {
    fixture = TestBed.createComponent(PortalAliasListComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  /**
   * Settles the view and runs any pending reaction.
   *
   * `TestBed.flushEffects()` is the effect-flushing member the installed framework exposes; there
   * is no `TestBed.tick()` on this version, and the free `tick()` belongs to `fakeAsync`, which
   * nothing here needs because every request is answered synchronously.
   */
  function settle(): void {
    fixture.detectChanges();
    TestBed.flushEffects();
  }

  /**
   * Consumes exactly one pending request, asserted by verb AND address.
   *
   * The predicate form is used throughout rather than the address form, because it is the only
   * form that also lets a case interrogate the parameter set — see the read cases, which prove an
   * ABSENCE of query parameters.
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
    // The fixture's host is published untyped, so it is NARROWED BY A REAL RUNTIME TEST rather than
    // by an assertion. An assertion would claim a type the compiler then stops checking and would
    // let an unexpected host travel silently into every query below.
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
   * Presses a button of the OPEN CONFIRMATION, scoped to the dialogue.
   *
   * ⚠ THIS SCOPING IS LOAD-BEARING, NOT TIDINESS. The form and the dialogue BOTH render a button
   * whose wording is `Cancel`, and the form's comes first in document order — so an unscoped
   * lookup by wording dismisses the ENTRY instead of the question and silently proves something
   * other than what the case claims.
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

      // ⚠ MINUS ONE IS A REAL ROW AND ALSO THE LEGACY ABSENT-INTEGER MARKER.
      // `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L76-L77` declares
      // `[PortalID] [int] IDENTITY (-1, 1)`, so the very first portal an installation creates is
      // numbered minus one — while `Library/Components/Shared/Null.vb` simultaneously uses minus one
      // to mean "no integer". This case is the regression guard against every expression that would
      // confuse the two: a truthiness test, a positivity test, or a coalescing default.
      const call = expectRequest('GET', aliasesUrl(-1));

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases');

      call.flush(envelope([alias(7, 'localhost')]));
      settle();

      expect(textOf('td.data-table__cell')).toContain('localhost');
    });

    it('reads the aliases of portal 0, which is an ordinary portal and not an absence', () => {
      create('0');

      // Zero is the SECOND portal under that identity seed, so it is as real as any other. A
      // truthiness test on the identifier would send this read nowhere.
      const call = expectRequest('GET', aliasesUrl(0));

      expect(call.request.url).toBe('/api/v1/portals/0/aliases');

      call.flush(envelope([alias(11, 'example.com', 0)]));
      settle();

      expect(textOf('td.data-table__cell')).toContain('example.com');
    });

    it('binds the parameter through an input named exactly portalId', () => {
      // ⚠ THE NAME IS THE ROUTE CONTRACT AND NOTHING ELSE ENFORCES IT. Component input binding
      // matches a path parameter to an input of the SAME NAME, so a rename compiles, renders and
      // silently never receives a portal. Setting the input by that literal name is the only
      // detector there is; `setInput` throws for a name the component does not declare.
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
      httpMock.expectNone((candidate) => candidate.url === aliasesUrl(-1));
    });

    it('re-reads when the route names a different portal, and not when it repeats one', () => {
      arrive([alias(7, 'localhost')]);

      // Re-delivering the SAME parameter must not re-request: the router re-delivers on every
      // navigation that reuses the component, and a request per delivery is a request per keystroke
      // in the address bar.
      fixture.componentRef.setInput('portalId', '-1');
      settle();
      httpMock.expectNone((candidate) => candidate.url === aliasesUrl(-1));

      // A DIFFERENT portal must re-request, or the screen shows the previous tenant's host names.
      fixture.componentRef.setInput('portalId', '0');
      settle();
      answerAliases([alias(11, 'example.com', 0)], 0);

      expect(textOf('td.data-table__cell')).toContain('example.com');
      expect(textOf('td.data-table__cell')).not.toContain('localhost');
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

      // TWO, reproducing the legacy grid exactly: `Website/admin/Portal/portalalias.ascx:L5-L17`
      // declares a narrow template column holding an edit link (L6 gives it a fifteen-pixel item
      // style) and one bound column for the host name (L14). A third column would be an invention.
      const headings = queryAll<HTMLTableCellElement>('th.data-table__header');

      expect(headings).withContext('exactly two columns').toHaveSize(2);
      expect(headings.every((cell) => cell.getAttribute('scope') === 'col'))
        .withContext('each heading is scoped to its column')
        .toBe(true);
    });

    it('names the host-name column from the resource value, not from the markup attribute', () => {
      arrive([alias(7, 'localhost')]);

      // The resource key is `HTTP Alias.Header` — the key itself contains a SPACE, which the legacy
      // resource files use freely — and `Localization.LocalizeDataGrid`
      // (`Website/admin/Portal/PortalAlias.ascx.vb:L69`) overwrote the markup's own `HeaderText` at
      // run time, which is why the resource VALUE is the authority.
      expect(textOf('th.data-table__header')).toContain(ALIAS_COLUMN_HEADING);

      // The command column carries no heading text of its own in the legacy grid, so its accessible
      // name is supplied and hidden rather than invented as visible text.
      expect(textOf('th.data-table__header')).toContain(EDIT_LABEL);
    });

    it('offers no delete column, because removal lives on the edit surface', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      // Asserted as an ABSENCE, deliberately. Deletion lived on the separate edit control
      // (`Website/admin/Portal/EditPortalAlias.ascx.vb:L173-L193`), never in the grid, so a per-row
      // delete would be a new and irreversible affordance one press away from every row.
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

      // MIGRATION: THE COLLECTION IS UNPAGED AND NO PAGER IS RENDERED. The legacy listing bound a
      //   plain `ArrayList` — `Website/admin/Portal/PortalAlias.ascx.vb:L39` declares it, L42 fills
      //   it from `GetPortalAliasArrayByPortalID` and L43-L44 bind it — and `portalalias.ascx`
      //   declares no pager anywhere in its nineteen lines. A pager here would be an invention that
      //   could HIDE the very host name a tenant is failing to resolve on.
      expect(query('app-pagination')).toBeNull();
    });

    it('renders no filter, because the legacy screen had none', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      // MIGRATION: HOST-NAME MATCHING IS THE SERVER'S AND IS NOW EXACT. The legacy tenant-resolution
      //   procedure matched on a SUBSTRING — `01.00.00.SqlDataProvider:L4569` declares
      //   `GetPortalSettings` and L4580-L4582 read
      //   `select @PortalID = min(PortalID) from Portals where PortalAlias like '%' + @PortalAlias + '%'`
      //   — so one tenant's alias could resolve to another's, and `min(PortalID)` then picked the
      //   lowest of the collisions. The successor matches exactly. No substring filter of any kind is
      //   asserted here, because none exists to assert.
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

      // ⚠ THE FACT IS THE SERVER'S, READ FROM THE ROW. The alias contract publishes `isCurrent` per
      // row, computed for each request from the resolved tenant context, so the comparison stays
      // where the legacy one was: `Website/admin/Portal/PortalAlias.ascx.vb:L51-L60` compared each
      // row against the request's own alias server-side and `portalalias.ascx:L8` bound the answer
      // to the hyperlink's `Visible` property. Nothing here consults the browser's address, because
      // the application is served through a proxy and the address in the location bar need not be
      // the host name the API matched.
      expect(rowEditCommand(0)).withContext('an ordinary row offers the command').not.toBeUndefined();
      expect(rowEditCommand(1)).withContext('the current row withholds it').toBeUndefined();

      // An invisible server control emitted nothing at all, so the withheld cell is EMPTY rather
      // than carrying substitute text.
      expect((paintedRows()[1]?.textContent ?? '').trim()).toBe('localhost:4200');
    });

    it('renders an alias held as an absent string as an empty cell rather than as text', () => {
      arrive([alias(7, null)]);

      // The column is nullable and the legacy absent-string marker was the EMPTY STRING rather than
      // a null reference (`Library/Components/Shared/Null.vb` returns `""`), so the two mean the
      // same thing and are rendered the same way. Neither is rewritten into the other.
      expect(textOf('td.data-table__cell')).toContain('');
      expect(bannerText()).withContext('and it is not a failure').toBe('');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE INLINE CREATE AND EDIT FORM
  // ---------------------------------------------------------------------------------------------------

  describe('the inline form', () => {
    it('opens the create form from the page action, labelled with this screen own add wording', () => {
      arrive([alias(7, 'localhost')]);

      expect(query('form')).withContext('no form before the action is pressed').toBeNull();

      press(ADD_ACTION_LABEL);

      // The action's wording is the measured `AddContent.Action`; the legacy screen published it as a
      // module action navigating to a separate control
      // (`Website/admin/Portal/PortalAlias.ascx.vb:L90`), and here it opens the form in place.
      expect(query('form')).withContext('the form opens in place').not.toBeNull();
      expect(button(ADD_SUBMIT_LABEL)).withContext('labelled for a creation').not.toBeUndefined();
      expect(button(UPDATE_SUBMIT_LABEL)).withContext('and not for a replacement').toBeUndefined();
      expect(entryField().value).withContext('and empty').toBe('');
    });

    it('labels the one submit control for a replacement when a row is being edited', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);

      // ONE CONTROL, TWO LABELS, exactly as the legacy screen had it: the markup declares a single
      // `cmdUpdate` link button (`Website/admin/Portal/editportalalias.ascx:L11`) whose text the
      // handler set from the `cmdAdd` key on both add branches
      // (`EditPortalAlias.ascx.vb:L83` and L88) and from `cmdUpdate` on the edit branch (L75).
      expect(button(UPDATE_SUBMIT_LABEL)).not.toBeUndefined();
      expect(button(ADD_SUBMIT_LABEL)).toBeUndefined();
      expect(entryField().value).withContext('holding the row being edited').toBe('localhost');
    });

    it('refuses a blank entry, says so, and sends nothing', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      submit();

      // MIGRATION: VALIDATION IS DECLARATIVE WHERE THE LEGACY SCREEN HAD NONE.
      //   `Website/admin/Portal/editportalalias.ascx` declares no validator of any kind in its
      //   fifteen lines — no required-field validator, no expression validator and no validation
      //   summary — and `Website/admin/Portal/EditPortalAlias.ascx.vb:L209` wraps the ENTIRE handler
      //   in `If strAlias <> "" Then` with no `Else`, so a blank box saved nothing AND said nothing.
      //   An operator pressed the button and the screen simply sat there. The rule is now stated, and
      //   the accepted input set is unchanged because the server already enforced it.
      expect(fieldMessages()).toContain(ALIAS_REQUIRED_MESSAGE);
      expect(entryField().getAttribute('aria-invalid'))
        .withContext('and a reader is told, not only a viewer')
        .toBe('true');
      httpMock.expectNone((candidate) => candidate.method === 'POST');
    });

    it('caps the entry at the legacy control own length, in the document and in the rule', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // The DOM attribute is the legacy `MaxLength="255"` at
      // `Website/admin/Portal/editportalalias.ascx:L7`, preserved exactly, which bounds typing and
      // pasting alike.
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
      // `[nvarchar] (200)` — so a value between the two bounds was accepted by the browser and could
      // not be stored. The transmitted value is judged at the column's bound, which is the one that
      // matters, and the message names that bound rather than the entry cap's.
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

      expect(fieldMessages()).toHaveSize(0);
    });

    it('says nothing at all before a person has acted', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // An operator is not told a field is required before they have reached it. The legacy screen
      // said nothing ever; saying it too early would be its own kind of noise.
      expect(fieldMessages()).toHaveSize(0);
      expect(entryField().getAttribute('aria-invalid')).toBe('false');
    });

    it('abandons the entry without judging it and without sending anything', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);
      type('');

      press(CANCEL_LABEL);

      // The legacy cancel control declared `causesvalidation="False"`
      // (`Website/admin/Portal/editportalalias.ascx:L12`), so abandoning the form must not report
      // whatever was half-typed. It redirected to a stored referrer — and to the empty string when
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

      // The legacy delete control declared `causesvalidation="False"` as well
      // (`Website/admin/Portal/editportalalias.ascx:L13`), so a removal neither judges nor reports
      // the entry — the value is about to cease to exist.
      expect(fieldMessages()).toHaveSize(0);
      expect(dialogue()).withContext('the question is asked').not.toBeNull();
      httpMock.expectNone((candidate) => candidate.method === 'DELETE');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — NORMALISATION, WHICH RUNS BEFORE JUDGEMENT AND IS REFLECTED BACK
  // ---------------------------------------------------------------------------------------------------

  describe('normalising an entry', () => {
    it('strips a secure protocol prefix, sends the remainder and shows what will be saved', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type(withScheme(SECURE_SCHEME, 'example.com'));
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      // The legacy handler removed everything up to and including the separator —
      // `Website/admin/Portal/EditPortalAlias.ascx.vb:L210-L212` tests
      // `strAlias.IndexOf("://") <> -1` and removes `IndexOf("://") + 3` characters — and this
      // reproduces it exactly.
      expect(transmittedAlias(call)).toBe('example.com');

      // ⚠ AND IT IS REFLECTED BACK INTO THE CONTROL, which the legacy screen never did: an operator
      // must see the value that will be stored rather than the one they typed.
      expect(entryField().value).toBe('example.com');

      call.flush(envelope(alias(21, 'example.com')), { status: 201, statusText: 'Created' });
      settle();
    });

    it('strips an insecure protocol prefix too, because the strip is on the separator', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      type(withScheme(INSECURE_SCHEME, 'example.com'));
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      // The legacy test was on the literal `://` and never on a scheme name, so every scheme is
      // stripped identically and no list of schemes exists to fall out of date.
      expect(transmittedAlias(call)).toBe('example.com');
      expect(entryField().value).toBe('example.com');

      call.flush(envelope(alias(21, 'example.com')), { status: 201, statusText: 'Created' });
      settle();
    });

    it('strips a share prefix of exactly two characters', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // MIGRATION: VB `"\\"` IS A TWO-CHARACTER LITERAL, AND THIS IS THE MOST DANGEROUS LINE IN THE
      //   WHOLE PORT. `Website/admin/Portal/EditPortalAlias.ascx.vb:L213-L215` tests
      //   `strAlias.IndexOf("\\") <> -1` and then removes `IndexOf("\\") + 2` characters. VB string
      //   literals have NO escape sequences, so that literal is a backslash followed by a backslash,
      //   and the `+ 2` offset is the proof: a one-character literal would have been removed with
      //   `+ 1`. TypeScript DOES have escape sequences, so the fixture below is written with FOUR
      //   backslashes to produce TWO — writing two would produce one and would silently test the
      //   wrong strip.
      //
      // ⚠ THIS CASE DISCRIMINATES BETWEEN THE TWO READINGS RATHER THAN MERELY DEMONSTRATING ONE.
      // A two-character strip yields `MYSERVER`, which the shape rule accepts and which is therefore
      // sent; a one-character strip would yield a value still carrying a backslash, which the shape
      // rule refuses outright, and no request would be made at all.
      type('\\\\MYSERVER');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe('MYSERVER');
      expect(entryField().value).toBe('MYSERVER');

      call.flush(envelope(alias(21, 'MYSERVER')), { status: 201, statusText: 'Created' });
      settle();
    });

    it('strips the protocol prefix first and the share prefix second, in that order', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // The legacy order is protocol then share (`EditPortalAlias.ascx.vb:L210-L215`), and the order
      // is preserved because it is observable: reversing it would leave the separator in place for an
      // entry carrying both.
      type(withScheme(SECURE_SCHEME, '\\\\MYSERVER'));
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      expect(transmittedAlias(call)).toBe('MYSERVER');

      call.flush(envelope(alias(21, 'MYSERVER')), { status: 201, statusText: 'Created' });
      settle();
    });

    it('treats normalisation as normalisation and not as validation', () => {
      arrive([alias(7, 'localhost')]);
      press(ADD_ACTION_LABEL);

      // ⚠ THE DISTINCTION IS LOAD-BEARING. Normalisation runs BEFORE judgement, so a value that
      // needed only normalising must SUCCEED rather than be refused — the legacy screen accepted a
      // scheme-prefixed entry and stored the host name alone, so refusing it now would break parity
      // for an entry an operator could previously make.
      type(withScheme(SECURE_SCHEME, 'example.com'));
      submit();

      expect(fieldMessages()).withContext('nothing is reported against it').toHaveSize(0);

      const call = expectRequest('POST', aliasesUrl(-1));

      call.flush(envelope(alias(21, 'example.com')), { status: 201, statusText: 'Created' });
      settle();

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

      // ⚠ ABSENCE IS PROVED WITH `has`, NEVER WITH `get(...) === null`. The two are different
      // assertions: a parameter that is PRESENT AND EMPTY reads back as null from `get`, so the
      // second test passes for a request that does carry the parameter. `has` is the only one that
      // answers the question this case is asking.
      //
      // The collection is unpaged and unordered and unfiltered, and a parameter the server does not
      // bind is discarded in silence — which would leave this screen believing it had asked for
      // something it had not.
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

      // The owning portal is NOT a member of the body: it comes from the address and is
      // authoritative there, so there is no second copy to disagree with it. That is also what makes
      // the legacy defect at `EditPortalAlias.ascx.vb:L234` structurally impossible here — it set the
      // owning portal from a page-state entry the branch had never populated, so the alias was
      // created against portal nought, which is a REAL tenant under an identity seeded at minus one.
      expect(transmittedAlias(call)).toBe('example.com');

      call.flush(envelope(alias(21, 'example.com')), { status: 201, statusText: 'Created' });
      settle();

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell')).toContain('example.com');
      expect(query('form')).withContext('and the form closes').toBeNull();

      // ⚠ A CREATION IS ANSWERED WITH THE CREATED ROW, so the listing is refreshed by APPENDING it
      // and no second read is issued. Asserting the absence is what makes the difference from the
      // replacement path below a measured fact rather than an assumption.
      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('replaces at the row address, answers a bodiless 204 and then re-reads the collection', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('example.com');
      submit();

      // ⚠ THE ADDRESS CARRIES THE ROW AS A PATH SEGMENT, and the legacy abbreviated query key
      // survives nowhere. `Website/admin/Portal/portalalias.ascx:L8` composed an edit address from it
      // and `EditPortalAlias.ascx.vb:L55` read it back, coercing it at L57.
      const call = expectRequest('PUT', aliasUrl(-1, 7));

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases/7');
      expect(transmittedAlias(call)).toBe('example.com');

      call.flush(null, { status: 204, statusText: 'No Content' });
      settle();

      // A replacement is answered with NO BODY, so the stored value must not be guessed from the
      // request — the collection is re-read instead. The second request is the proof.
      answerAliases([alias(7, 'example.com')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell')).toContain('example.com');
    });

    it('treats a 200 answer to a replacement exactly as it treats a 204', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      type('example.com');
      submit();

      // ⚠ RECORDED DISCREPANCY, ASSERTED BOTH WAYS. The controller answers `204 No Content` for a
      // replacement while the migration brief for this screen states `200`. The transport declares no
      // response body either way, so the screen cannot depend on the distinction — and this case
      // proves it does not, which is a stronger guarantee than picking one status and hoping.
      expectRequest('PUT', aliasUrl(-1, 7)).flush(null, { status: 200, statusText: 'OK' });
      settle();

      answerAliases([alias(7, 'example.com')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(textOf('td.data-table__cell')).toContain('example.com');
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

      expect(notifications()).toEqual([{ severity: 'info', message: DELETED_MESSAGE }]);
      expect(textOf('td.data-table__cell')).not.toContain('localhost');
      expect(textOf('td.data-table__cell')).toContain('localhost:4200');

      // A removal is exact for an unpaged collection, so the row is dropped in place and nothing is
      // re-read.
      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('addresses the row whose command was pressed, not the first one', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200'), alias(9, '127.0.0.1')]);

      editRow(1);
      type('renamed.example.com');
      submit();

      // Off-by-one here would rename the wrong tenant address, which is exactly the class of defect
      // no status code reveals.
      expectRequest('PUT', aliasUrl(-1, 8)).flush(null, { status: 204, statusText: 'No Content' });
      settle();

      answerAliases([alias(7, 'localhost'), alias(8, 'renamed.example.com'), alias(9, '127.0.0.1')]);

      expect(textOf('td.data-table__cell')).toContain('renamed.example.com');
    });

    it('clears the selection when an entry is abandoned, so the next add is not a replace', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);
      press(CANCEL_LABEL);

      press(ADD_ACTION_LABEL);
      type('example.com');
      submit();

      // The absence of a selection is what distinguishes adding from replacing — the successor of
      // `If Not ViewState("PortalAliasID") Is Nothing Then` at `EditPortalAlias.ascx.vb:L218`, which
      // was an explicit ABSENCE test because a row numbered nought is real. A stale selection here
      // would silently overwrite the row last looked at.
      expectRequest('POST', aliasesUrl(-1)).flush(envelope(alias(21, 'example.com')), {
        status: 201,
        statusText: 'Created',
      });
      settle();

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

      // ⚠ AND IT IS NOT THE SIBLING SCREEN'S SENTENCE. The shared failure vocabulary collapses this
      // legacy key onto the same published code as the create-portal screen's own duplicate refusal
      // and keeps that screen's LONGER wording — which is right for a shared code-to-message map and
      // wrong for this screen, whose parity obligation is to its own resource file
      // (`DuplicateAlias.Text` in `EditPortalAlias.ascx.resx`, read by the handler at
      // `EditPortalAlias.ascx.vb:L226` and again at L238). The shared sentence is imported rather
      // than restated, so this proof cannot drift away from the thing it is distinguishing itself
      // from.
      const sharedSentence: string = CONFLICT_MESSAGE[DUPLICATE_ALIAS_CODE];

      expect(sharedSentence)
        .withContext('the two wordings really are different')
        .not.toBe(DUPLICATE_ALIAS_MESSAGE);
      expect(fieldMessages()).not.toContain(sharedSentence);

      // EXHAUSTIVE rather than a denial list: comparing the whole message set proves that the
      // sibling sentence is absent AND that nothing else has been added beside it, which a
      // containment test on one known string could never establish.
      expect(fieldMessages()).toEqual([DUPLICATE_ALIAS_MESSAGE]);
      expect(bannerText()).withContext('and it is not repeated in the banner').not.toContain(
        DUPLICATE_ALIAS_MESSAGE,
      );
    });

    it('reports a duplicate host name beside the field on a replacement as well', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      type('localhost:4200');
      submit();

      // MIGRATION: THE BARE, BROAD `Catch` IS GONE. The legacy replacement path wrapped its write in
      //   `Try … Catch` with no exception variable and no filter
      //   (`Website/admin/Portal/EditPortalAlias.ascx.vb:L223-L228`) and reported a DUPLICATE HOST
      //   NAME for anything it caught — a dropped connection, a timeout, a permission refusal, all
      //   described as a name collision. Only a real refusal produces that message now, and the
      //   discriminator is the published code and never the status, because these endpoints answer
      //   `409` for two different reasons.
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

      // The other half of the same migration: a `500` is the exact case the legacy bare `Catch`
      // reported as a collision. The server's own sentence is shown instead, in the banner, where a
      // failure with no field to sit beside belongs.
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

      // ⚠ TWO DIFFERENT KEYS ARE IN PLAY. The WIRE member is camel-cased; the model-state
      // error-dictionary key is the DTO property name and is therefore Pascal-cased. The shared field
      // reader lower-cases only the FIRST character of a reported key, which is what makes the second
      // resolve onto the first — and the fixture is typed as the real contract, so a mis-cased member
      // would be a compile error rather than a value that reads back as undefined.
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

      // ⚠ THE TRACE IDENTIFIER MUST SURVIVE. The correlation identifier is PREFERRED as the quoted
      // reference, so this document deliberately carries the trace identifier ALONE — which is what a
      // proxy answering on its own behalf looks like. Discarding it would leave a browser-side report
      // with nothing to join to a server-side one.
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

      // MIGRATION: A PERMISSION DENIAL IS A WARNING AND NOT A DANGER-SEVERITY FAULT. Both legacy
      //   handlers used the red error severity — the HARD-CODED English literal at
      //   `Website/admin/Portal/EditPortalAlias.ascx.vb:L67`, which has no resource entry anywhere,
      //   and the resourced delete refusal at L183 — but the legacy vocabulary was three-valued and
      //   its own canonical access-denied screen chose the middle value at BOTH of its branches
      //   (`Website/admin/Security/AccessDenied.ascx.vb:L43` and L45). A working authorisation
      //   decision is not a broken system.
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

      // ⚠ THE SEVERITY IS ASSERTED, NOT ONLY THE TEXT. The wording is this screen's own
      // `AccessDenied.Text`, which is measured ABSENT from the global resource file, and the
      // announcement is specific to the operation that was refused — a refused removal is not
      // reported as a refused reading.
      expect(notifications()).toEqual([{ severity: 'warning', message: DELETE_DENIED_MESSAGE }]);
      expect(bannerSeverity()).toBe('Warning');
      expect(textOf('td.data-table__cell'))
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

      // Only a refusal of AUTHORITY carries this screen's own wording. Everything else is reported by
      // the shared surface in the server's words, because inventing a sentence for it would be
      // inventing wording the legacy screen never had.
      expect(notifications()).toHaveSize(0);
      expect(bannerText()).toContain('The requested resource does not exist.');
    });

    it('refuses a write to the alias the request arrived through, and says how to recover', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      type('renamed.example.com');
      submit();

      // Both alias endpoints answer `409` for TWO different reasons, so the STATUS is not diagnostic
      // and the published code is what tells them apart. This is the second reason, and it must not
      // be described as a duplicate.
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

      // ⚠ LEGACY RESOURCE TEXT IS UNTRUSTED MARKUP, MEASURED. Seventy-six values across the
      // in-scope resource files carry an HTML tag, and this very feature's
      // `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` holds a live third-party
      // advertising script in `Advertising.Text`. The tags are stored XML-escaped, so a naive search
      // returns nothing and would wrongly clear the risk. Nothing on this screen binds message text
      // as markup, so a hostile value is shown as characters and creates no element.
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

      // Both spellings occur in the legacy message set, and the stripping is owned by the shared
      // message reader — this case asserts the OUTCOME and reimplements nothing. Rendering the tag
      // would need message text bound as markup, which the evidence above rules out; showing it
      // literally would present a person with a tag.
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

  describe('the removal guards', () => {
    it('withholds removal while a portal has only one alias', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);

      // MIGRATION: THE LAST-ALIAS GUARD IS CLIENT-SIDE ONLY, AND THAT IS DELIBERATE.
      //   `Website/admin/Portal/EditPortalAlias.ascx.vb:L102-L110` read the portal's aliases and hid
      //   the command when there were not more than one — L107 reads
      //   `If colPortalAlias.Count <= 1 Then cmdDelete.Visible = False`, so a count of NOUGHT and a
      //   count of ONE both hid it. The delete endpoint declares NO conflict response for this case:
      //   unbinding a portal's last address is answered normally, because inventing a refusal the
      //   legacy console did not have would refuse a save an operator could previously make. The
      //   withheld affordance is therefore the ONLY thing standing between an operator and a portal
      //   with no way to reach it, which is unlike the portal listing's documented last-portal
      //   refusal — a single dotted code string the server really does answer.
      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('withholds removal for a portal with no aliases at all', () => {
      arrive([]);

      // Nought also hides it, because the legacy test was not-greater-than-one rather than
      // equal-to-one. There is no row to edit here, so the create form is the only surface — which is
      // also the surface the next case is about.
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

      // A DELIBERATE DIVERGENCE, and the legacy behaviour was a defect. The legacy screen called the
      // same visibility helper on its ADD branches too — `EditPortalAlias.ascx.vb:L81` and L86 — so a
      // brand-new unsaved alias showed a Delete button whenever the portal already had two or more,
      // and that button acted on a page-state entry the add branch had never set.
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

      // MIGRATION: THE CONFIRMATION IS AN ADDITION. The legacy delete command carried no client-side
      //   confirmation of any kind — `Website/admin/Portal/editportalalias.ascx:L13` declares no
      //   client-side click handler — and the handler removed the row on the FIRST click at
      //   `Website/admin/Portal/EditPortalAlias.ascx.vb:L187`, redirecting at L189. The wording is the
      //   platform's own `DeleteItem.Text` from the global resource file rather than an invention, and
      //   the dialogue owns its focus trap and its dismissal key. Recorded as a deliberate safety
      //   affordance and not presented as parity.
      const open = dialogue();

      expect(open).not.toBeNull();
      expect(textOf('.confirm-dialog__message')).toContain(DELETE_CONFIRM_MESSAGE);

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
      expect(textOf('td.data-table__cell')).toContain('localhost');
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

      expect(dialogue()).withContext('the question is closed').toBeNull();
      expect(query('form')).withContext('and the form with it').toBeNull();
      expect(notifications()).toEqual([{ severity: 'info', message: DELETED_MESSAGE }]);
      expect(paintedRows()).toHaveSize(1);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE STORE COUPLING
  // ---------------------------------------------------------------------------------------------------

  describe('the store coupling', () => {
    it('exposes the alias slices as readonly signals the screen cannot write', () => {
      arrive([alias(7, 'localhost')]);

      // ⚠ A CAST-FREE PROOF, DELIBERATELY. A writable signal carries both `set` and `update`; a
      // readonly projection carries neither, so testing for the absence of the two members proves the
      // slice cannot be written from the consumer side without asserting a type the compiler would
      // then stop checking.
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

      // A readonly array type stops this at compile time, which is the primary defence; copying it
      // and emptying the copy is what proves the SLICE is not the array a caller holds, so a caller
      // that reached past the type could still not corrupt the state.
      const copy: PortalAlias[] = [...(handedOut ?? [])];

      copy.length = 0;

      expect(store.aliases()).withContext('the slice still holds both rows').toHaveSize(2);
      expect(paintedRows()).withContext('and the grid still paints both').toHaveSize(2);
    });

    it('reads and writes only through the store, never through a transport of its own', () => {
      // The screen declares no providers and injects no transport: the whole of its data path is the
      // store, and the store is what composes an address. Substituting the store with a Jasmine
      // double therefore silences the wire completely — which is only true if nothing else on the
      // screen can reach it.
      const listSpy = spyOn(store, 'loadAliases').and.callFake(() => undefined);

      create('-1');

      // ⚠ THE CALL COUNT IS DELIBERATELY NOT ASSERTED HERE, and the reason is worth recording rather
      // than working around. A faked command reports NOTHING back — the collection stays unread and
      // the store never says it is loading — so the lifecycle hook's guard,
      // `listReady() === false && loading() === false`, is legitimately still true when it runs and
      // legitimately asks again. That is the guard behaving correctly against a silent double, not a
      // duplicate read: with the genuine store the first call sets the loading slice synchronously
      // and the hook suppresses itself, which the earlier case `issues exactly one read` proves
      // against that genuine store. Asserting a count against the double would therefore assert a
      // property of the double.
      expect(listSpy).toHaveBeenCalledWith(-1);
      expect(listSpy).toHaveBeenCalledWith(jasmine.any(Number));

      // This is the claim the case exists for: with the one data path silenced, NOTHING reaches the
      // wire — so the screen holds no transport of its own and composes no address of its own.
      httpMock.expectNone((candidate) => candidate.method === 'GET');
    });

    it('forwards a creation to the store with the portal from the address and the normalised value', () => {
      arrive([alias(7, 'localhost')]);

      const createSpy = spyOn(store, 'createAlias').and.callFake(() => undefined);

      press(ADD_ACTION_LABEL);
      type(withScheme(SECURE_SCHEME, 'example.com'));
      submit();

      // `jasmine.objectContaining` rather than a whole-object comparison, so the assertion is about
      // the member that matters and does not silently accept a second member appearing beside it —
      // the exact member set is proved on the wire by {@link isAliasWriteBody}.
      expect(createSpy).toHaveBeenCalledWith(
        -1,
        jasmine.objectContaining({ httpAlias: 'example.com' }),
        jasmine.any(Function),
      );
    });
  });
});
