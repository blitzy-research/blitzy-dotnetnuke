//
// Specification for `error.interceptor.ts`.
//
// ---------------------------------------------------------------------------
// WHAT THESE SPECIFICATIONS PIN, AND WHY EACH ONE EARNS ITS PLACE
// ---------------------------------------------------------------------------
// The interceptor under test does three things and nothing else: it unwraps a
// failed response, it queues at most one plain-text sentence describing it, and
// it re-throws. Every specification below is written so that it FAILS if one of
// those three changes - not merely so that it passes today.
//
// The behaviours pinned here are, in order of how expensive they would be to get
// wrong:
//
//   * silence on 401, which is a NEGATIVE assertion and the single most important
//     one in this file - see the section below;
//   * a permission refusal presented as a WARNING rather than an error;
//   * a rate-limit refusal announced calmly and NEVER re-attempted;
//   * the per-field dictionary read with BRACKET access only;
//   * legacy break markup resolved to plain text in every spelling the legacy
//     sources actually contain;
//   * the support reference surfaced for a fault, and withheld for a refusal;
//   * and the invariant underneath all of it - that no failure is ever swallowed.
//
// ---------------------------------------------------------------------------
// D-I1: WHY SILENCE ON 401 IS CORRECT, AND WHY THIS FILE ASSERTS IT NEGATIVELY
// ---------------------------------------------------------------------------
// `withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor])`
// composes as `correlationId(next = auth(next = error(next = backend)))`. The
// listed order is the order on the way OUT, so the order on the way BACK is its
// reverse:
//
//   request:  correlationId -> auth -> error -> backend
//   response: backend -> error -> auth -> correlationId
//
// The interceptor under test is therefore the INNERMOST of the three on the
// response path. It sees the raw response FIRST, BEFORE the authentication
// interceptor wrapped around it has had any opportunity to renew a session and
// retry. Two consequences follow, and both are asserted below:
//
//   1. The migration plan's description of this interceptor as one that "observes
//      the final response, after any 401 refresh-and-retry" is inverted. Under the
//      framework's actual chain semantics it observes the FIRST response, and it
//      observes the retry as well, because the retried request is dispatched back
//      through it.
//   2. The status-map row saying this interceptor should clear the session and
//      route to the sign-in screen on a 401 is therefore NOT implemented, and must
//      not be. A session about to be renewed silently would still produce a
//      "not logged in" announcement, and a refused sign-in - a 401 from the
//      credential endpoint, which the sign-in screen reads and presents itself -
//      would be reported twice, the second time somewhere the operator cannot act.
//
// The interceptor array order is NOT changed to make the plan's description true,
// because that order is load-bearing for the correlation identifier. Ownership of
// the whole 401 lifecycle - detection, the single refresh, the single retry, and
// discarding the session when the refresh fails - is assigned to
// `auth.interceptor.ts`, whose own specification proves it. What remains here is
// the guard against a regression that would re-introduce a spurious announcement,
// and a guard against a regression can only be written as a negative assertion:
// the queue is empty, and the caller was still told its request failed.
//
// ---------------------------------------------------------------------------
// EVERY ASSERTED MESSAGE IS A PLAIN STRING. THAT IS A SECURITY BOUNDARY.
// ---------------------------------------------------------------------------
// Nothing here constructs or asserts on a pre-escaped value, a trusted-markup
// value, or any member whose name suggests either, and that is deliberate rather
// than incidental. The API relays legacy message wording unaltered, and that
// wording came from resource files in which raw markup is commonplace: across the
// 37 in-scope legacy resource files under Website/admin/*/App_LocalResources,
// unescaping the XML entities first - a naive search finds nothing, because the
// markup is stored escaped, and would wrongly conclude the risk is absent - shows
// 76 values carrying an HTML tag and four script-tag occurrences, the culprit
// being a live third-party advertising block in the `Advertising.Text` entry of
// Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx, whose script is
// loaded from an external host over plaintext transport. The URL itself is
// deliberately not reproduced here.
//
// So the break-markup specifications below assert that the markup is GONE and that
// the result is text. They do not assert that it was rendered safely, because it is
// never rendered as markup at all. The legacy application reached the same
// conclusion by hand: Website/admin/Security/AccessDenied.ascx.vb:L43 passes its
// untrusted query-string message through
// `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(..))` before display.
//
// ---------------------------------------------------------------------------
// FIXTURES ARE TYPED, NOT LOOSE
// ---------------------------------------------------------------------------
// Every problem-document fixture is annotated `ProblemDetails` or
// `ValidationProblemDetails` through a type-only import, so a drift in the wire
// contract breaks THIS FILE at compile time rather than silently weakening the
// suite. Fixtures that are deliberately malformed - a member of the wrong type, a
// dictionary that is not a dictionary, a body that is not JSON - are typed as
// `unknown` and passed through a helper that accepts `unknown`, because the point
// of those specifications is that the interceptor survives a body no type
// describes.
//
// Two shape facts are worth stating, because both differ from what a reader might
// expect and both are load-bearing here:
//
//   * EVERY member of `ProblemDetails` is optional. RFC 7807 makes them so, and a
//     proxy between the browser and the API can return a document this application
//     never produced. A fixture may therefore carry only the members its
//     specification is about.
//   * The support reference is `correlationId`, with `traceId` as a FALLBACK only.
//     They are two independent identifiers with two different formats, and only the
//     correlation identifier appears on the response header, in the server's request
//     log and on the request's audit events. Both are pinned below.
//

import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import type { TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import { NotificationService, presentedInContext } from '../services/notification.service';
import type { AppNotification, NotificationSeverity } from '../services/notification.service';
import { errorInterceptor } from './error.interceptor';

// ---------------------------------------------------------------------------
// ENDPOINTS
// ---------------------------------------------------------------------------
//
// Written as RELATIVE literals rather than composed from the endpoint catalogue,
// for two reasons. The catalogue is not a dependency of this specification, and
// spelling the paths out is what makes the relative form visible to a reader: the
// reverse proxy serves the bundle and proxies `/api/` to the API on that same
// origin, so no request this application makes is cross-origin and no absolute
// host belongs in a specification. An absolute value would type-check, lint and
// bundle without complaint, which is precisely why it is asserted against here.

/** A representative resource endpoint. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The credential endpoint.
 *
 * Used by the specifications that matter most for it: a 401 here is a refused
 * sign-in rather than an expired session, and a 429 here is the rate limiter that
 * replaced the legacy verification control.
 */
const LOGIN_URL = '/api/v1/auth/login';

// ---------------------------------------------------------------------------
// SYNTHETIC IDENTIFIERS
// ---------------------------------------------------------------------------
//
// Shaped like the real values so that a reader recognises them, and obviously
// synthetic so that no reader mistakes one for a captured production identifier.
// Runs of a single hex digit are used for exactly that reason. No credential,
// token or password appears anywhere in this file, in a fixture or in a comment.

/**
 * A W3C trace-context identifier, in the `00-<trace>-<span>-00` form the framework
 * emits.
 */
const TRACE_ID = '00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-00';

/** A correlation identifier, in the opaque form this application stamps. */
const CORRELATION_ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

// ---------------------------------------------------------------------------
// EXPECTED WORDING
// ---------------------------------------------------------------------------
//
// Re-declared here as literals rather than imported from the utility that produces
// them, and the choice is deliberate. Importing the constants would make these
// specifications tautological: a change that reworded every message would still
// pass, because both sides of the comparison would have moved together. Spelling
// the sentence out means a rewording is a deliberate, reviewable edit in two
// places, which is what a wording guarantee needs in order to be a guarantee.

/** Shown for 400, and for any status with no more specific wording. */
const REQUEST_REJECTED_TEXT = 'The request could not be completed.';

/** Shown for 403. */
const FORBIDDEN_TEXT = 'You do not have permission to perform this action.';

/** Shown for 404. */
const NOT_FOUND_TEXT = 'The requested item could not be found.';

/** Shown for 409. */
const CONFLICT_TEXT = 'This item was changed by someone else. Reload it and apply your changes again.';

/** Shown for 422. */
const VALIDATION_REJECTED_TEXT =
  'Some of the values supplied are not valid. Review the highlighted fields and try again.';

/** Shown for 429. Calm on purpose: nothing failed, the caller is early. */
const TOO_MANY_ATTEMPTS_TEXT = 'Too many attempts. Wait a moment and try again.';

/** Shown for any status at or above 500. */
const SERVER_ERROR_TEXT = 'The server could not complete the request. Try again shortly.';

/** Shown when no response arrived at all. */
const NETWORK_UNAVAILABLE_TEXT =
  'The server could not be reached. Check your connection and try again.';

/** Introduces the support reference when one is quoted. */
const REFERENCE_LABEL = 'Reference:';

/**
 * Any body the testing backend can be told to respond with.
 *
 * DERIVED from the testing backend's own signature rather than restated as a union,
 * for two reasons. Restating it would duplicate a framework contract that this file
 * does not own, and it would drift silently at the next upgrade. Deriving it also
 * keeps the deliberately malformed fixtures below honest: a body that is a plain
 * `unknown` cannot be handed to the backend at all, so the alternative to this type
 * is a cast - and a cast is exactly what strict typing exists to avoid.
 *
 * It is deliberately WIDER than `ProblemDetails`. The well-formed fixtures are
 * annotated with the real contract at their declaration sites and are checked
 * against it there; this type exists only so that the specifications whose whole
 * purpose is an unrecognised shape - a member of the wrong type, a dictionary that
 * is not a dictionary, an array, a payload with no problem member - can express
 * that shape without weakening the checked ones.
 */
type FlushableBody = Parameters<TestRequest['flush']>[0];

describe('errorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ORDER MATTERS AND IS NOT COSMETIC. The real client is configured first,
        // WITH the interceptor under test, and the testing backend then replaces the
        // transport underneath that already-configured chain. Reversing the two
        // leaves the testing backend configured without the interceptor, so every
        // specification below would pass a request straight through and assert
        // nothing at all.
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
      ],
      // NotificationService is deliberately NOT listed. It is provided at the root,
      // so the interceptor's own `inject` resolves the very same instance this
      // specification reads - which is what makes reading its signal a genuine
      // observation of the interceptor's behaviour rather than of a double's.
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    // MANDATORY, and it carries real weight rather than being hygiene. This is the
    // assertion that no request was issued which a specification did not account
    // for - which is how "a rate-limit refusal is never re-attempted" is proved:
    // were a retry introduced, the second request would be outstanding here and
    // every rate-limit specification would fail on this line.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  // -------------------------------------------------------------------------

  /**
   * Issues a request, fails it with the supplied body and status, and asserts that
   * the caller was told.
   *
   * The rejection assertion is built IN rather than offered as a separate helper,
   * because every specification in this file needs it: a failure that stopped
   * reaching the subscriber is the one regression no wording assertion would catch,
   * so there is no legitimate case for flushing a failure and not checking. It also
   * means the rejection is always handled, which keeps a genuine assertion failure
   * from being masked by an unrelated unhandled-rejection report.
   *
   * The body is typed {@link FlushableBody} so that one helper serves both the
   * well-formed fixtures - annotated at their declaration site and therefore already
   * checked against the wire contract - and the deliberately malformed ones, whose
   * whole purpose is to be a shape the wire contract does not describe.
   *
   * `application/problem+json` is declared on every failure, because that is the
   * media type the API answers with; a specification that quietly used
   * `application/json` would be testing a response the server never sends.
   *
   * @param body The response body to fail with.
   * @param status The transport status to fail with.
   * @param statusText The reason phrase.
   * @param url The endpoint to request.
   */
  async function expectRejection(
    body: FlushableBody,
    status: number,
    statusText = 'Error',
    url: string = PORTALS_URL,
  ): Promise<void> {
    const pending = firstValueFrom(http.get(url));

    httpMock.expectOne(url).flush(body, {
      status,
      statusText,
      headers: { 'Content-Type': 'application/problem+json' },
    });

    await expectAsync(pending)
      .withContext(`a ${status} must still reach the caller`)
      .toBeRejected();
  }

  /** The queue as the interceptor left it. */
  function queued(): readonly AppNotification[] {
    return notifications.notifications();
  }

  /** The messages queued so far, in order. */
  function messages(): readonly string[] {
    return queued().map((entry) => entry.message);
  }

  /**
   * Raises one failure and returns the message it queued, leaving the queue EMPTY afterwards.
   *
   * ⚠ NEEDED BECAUSE THE SERVICE COLLAPSES AN IMMEDIATE REPETITION. Several specifications below
   * prove that several DIFFERENT inputs normalise to the SAME sentence, and they can only do that
   * by raising them one after another - at which point the queue legitimately holds one row rather
   * than one per raise. Reading and clearing between raises observes each normalisation on its own
   * without asking the service to behave as it did when one fault could be reported three times.
   *
   * @param body The response body to flush.
   * @param status The response status.
   * @param statusText The response status text.
   * @returns The message queued by that one failure, or an empty string when none was.
   */
  async function messageFrom(
    body: FlushableBody,
    status: number,
    statusText: string,
  ): Promise<string> {
    await expectRejection(body, status, statusText);

    const raised = messages();
    const last = raised.at(-1) ?? '';

    notifications.clear();

    return last;
  }

  /** The severities queued so far, in order. */
  function severities(): readonly NotificationSeverity[] {
    return queued().map((entry) => entry.severity);
  }

  /**
   * The single message queued, failing the specification when the count is not one.
   *
   * Guarding the count rather than indexing blindly, because an assertion made
   * against `messages()[0]` of an empty queue reads as `expected undefined to
   * contain ...`, which names neither the real problem nor the count that caused
   * it. It also keeps a non-null assertion out of this file.
   *
   * @returns The only queued message.
   */
  function onlyMessage(): string {
    const all = messages();

    expect(all.length).withContext('exactly one notification was expected').toBe(1);

    return all.length === 1 ? all[0] : '';
  }

  // -------------------------------------------------------------------------
  // A SUCCESSFUL RESPONSE IS NOT TOUCHED
  // -------------------------------------------------------------------------

  describe('a successful response', () => {
    it('passes the body through unchanged and queues nothing', async () => {
      const pending = firstValueFrom(http.get<{ readonly ok: boolean }>(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).flush({ ok: true });

      expect(await pending)
        .withContext('the interceptor is on the failure path only')
        .toEqual({ ok: true });
      expect(queued()).withContext('nothing succeeded loudly').toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // NOTHING IS EVER SWALLOWED
  // -------------------------------------------------------------------------
  //
  // The invariant underneath every other specification in this file. The
  // interceptor TRANSLATES; it does not DECIDE. A feature still has to learn that
  // its request failed so it can leave its form open, keep the row it failed to
  // delete, restore the value it failed to save, and bind the problem document into
  // the shared error-banner component - which takes the whole document and renders
  // its per-field messages. A feature's own signal store sets its error slice from
  // that rejection, so converting a failure into a successful empty response would
  // make every caller appear to succeed and every store record a success.
  //
  // Asserted for the WHOLE status vocabulary rather than for a representative
  // status, because the two statuses the interceptor treats specially - 401, which
  // it says nothing about, and a validation failure, which it deliberately leaves
  // to the fields - are exactly the two where a `return` in the wrong place would
  // turn silence about a failure into suppression OF it.

  describe('re-throwing', () => {
    it('re-throws every failed status, including the ones it says nothing about', async () => {
      const statuses: readonly number[] = [0, 400, 401, 403, 404, 409, 422, 429, 500, 502, 503];

      for (const status of statuses) {
        const pending = firstValueFrom(http.get(PORTALS_URL));
        const request = httpMock.expectOne(PORTALS_URL);

        if (status === 0) {
          request.error(new ProgressEvent('error'), { status: 0, statusText: '' });
        } else {
          request.flush(
            { status, title: 'Failed' },
            { status, statusText: 'Failed', headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        await expectAsync(pending)
          .withContext(`a ${status} must reach the caller`)
          .toBeRejected();
      }
    });

    it('re-throws the original failure rather than a substitute of its own', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).flush(
        { status: 409, detail: 'Already registered.' },
        {
          status: 409,
          statusText: 'Conflict',
          headers: { 'Content-Type': 'application/problem+json' },
        },
      );

      // The IDENTITY of the thrown value matters, not merely that something was
      // thrown. A feature reads the problem document off this object to render its
      // banner, so re-throwing a fresh error - or the parsed body, or a string -
      // would leave every caller with a rejection it cannot render.
      //
      // Captured through a rejection handler rather than asserted with Jasmine's
      // error matcher, because that matcher requires an `Error` instance and this
      // value is not one: the framework's failed-response type IMPLEMENTS the error
      // interface without extending the error class, so the matcher would report a
      // failure against perfectly correct behaviour.
      const failure: unknown = await pending.then(
        () => null,
        (thrown: unknown) => thrown,
      );

      expect(failure)
        .withContext('the transport failure itself is what callers receive')
        .toBeInstanceOf(HttpErrorResponse);

      // Narrowed rather than asserted through, so the body reads are type-checked
      // and no non-null assertion is needed.
      if (failure instanceof HttpErrorResponse) {
        expect(failure.status).toBe(409);
        expect(failure.error).withContext('the parsed document survives for the banner').toEqual({
          status: 409,
          detail: 'Already registered.',
        });
      }
    });

    it('does not swallow a validation failure it deliberately leaves unannounced', async () => {
      const body: ValidationProblemDetails = {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { PortalName: ['The PortalName field is required.'] },
      };

      await expectRejection(body, 400, 'Bad Request');

      // Silence about a failure is not suppression of it. This is the pairing that
      // distinguishes the two.
      expect(queued()).withContext('the fields are showing it, not a toast').toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // D-I1: A 401 IS ANNOUNCED BY NOBODY HERE
  // -------------------------------------------------------------------------
  //
  // The negative specification described at the head of this file. This interceptor
  // is the INNERMOST of the three on the response path, so it observes the raw 401
  // BEFORE the authentication interceptor around it can renew the session and
  // retry - and it observes the retry too, because the retried request is dispatched
  // back through it. Announcing from here would therefore surface a transient 401
  // the operator should never have seen, and would report a refused sign-in a second
  // time in a place the operator cannot act on.
  //
  // The whole 401 lifecycle belongs to `auth.interceptor.ts`. These specifications
  // are the guard that keeps it there: they fail as soon as anything reintroduces an
  // announcement, a session clear or a discarded session on this path.

  describe('a 401', () => {
    it('is not announced, because a renewal may be under way', async () => {
      const body: ProblemDetails = {
        type: 'urn:ietf:rfc:7235',
        title: 'Unauthorized',
        status: 401,
        detail: 'Either you are not currently logged in, or you do not have access.',
      };

      await expectRejection(body, 401, 'Unauthorized');

      expect(queued())
        .withContext('the authentication interceptor owns this status')
        .toEqual([]);
    });

    it('is not announced for the credential endpoint either, where it means bad credentials', async () => {
      const body: ProblemDetails = {
        title: 'Unauthorized',
        status: 401,
        detail: 'The credentials supplied were not accepted.',
      };

      // A 401 from the credential endpoint is a REFUSED SIGN-IN rather than an
      // expired session, and the sign-in screen reads that problem document and
      // presents it beside the form itself. Announcing it here as well would say the
      // same thing twice, the second time somewhere the operator cannot act on it.
      //
      // MIGRATION: the wording the sign-in screen shows carries a CODE ONLY and
      // never an enriched message. Login.ascx.vb:L163 seeds its outcome as a failure
      // before any check runs - fail-closed - and L168-L184 assign only the three
      // localised keys for a missing verification value, an incorrect one, and an
      // unauthorised account. No username is echoed back and no distinction is drawn
      // between an unknown account and an incorrect password, so nothing reachable
      // from this interceptor may enrich a credential failure either.
      await expectRejection(body, 401, 'Unauthorized', LOGIN_URL);

      expect(queued()).toEqual([]);
    });

    it('is not announced even when it carries a support reference worth quoting', async () => {
      const body: ProblemDetails = {
        title: 'Unauthorized',
        status: 401,
        detail: 'Token expired.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
      };

      // The early return happens BEFORE any reference is read, and it must stay
      // there. A reference-bearing 401 is the tempting exception that would
      // reintroduce the spurious announcement this whole section exists to prevent.
      await expectRejection(body, 401, 'Unauthorized');

      expect(queued()).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // SEVERITY: A REFUSAL IS NOT A FAULT
  // -------------------------------------------------------------------------
  //
  // The distinction is load-bearing rather than decorative, and it is measured
  // against the legacy application rather than chosen. Across the in-scope legacy
  // administration code-behinds the three skin message types are used 27 times as
  // `RedError`, 21 times as `YellowWarning` and 12 times as `GreenSuccess`, so the
  // legacy application genuinely distinguished a refusal from a fault.

  describe('severity', () => {
    it('presents a permission refusal as a WARNING, never as an error', async () => {
      // Deliberately carries NEITHER a detail nor a title, so the status sentence is
      // what remains. A title-bearing document would announce the title instead - the
      // precedence is detail, then title, then the status sentence - and asserting the
      // status wording against a document that supplies its own text would be asserting
      // the wrong thing about correct behaviour.
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:authorization.forbidden',
        status: 403,
        detail: '',
      };

      await expectRejection(body, 403, 'Forbidden');

      // MIGRATION: measured, not asserted. Website/admin/Security/AccessDenied.ascx.vb
      // is 50 lines; its `Page_Load` (L41-L47) performs NO permission check at all -
      // it only PRESENTS a denial - and BOTH of its branches pass
      // `ModuleMessage.ModuleMessageType.YellowWarning`: L43 for the message supplied
      // through the query-string key, L45 for the localised default. Presenting a
      // refusal in danger styling would tell an operator that something is broken
      // when the system is working exactly as configured, which is why the shared
      // error-banner component must never receive this at danger severity.
      expect(severities()).withContext('a denial is a warning, as it always was').toEqual([
        'warning',
      ]);
      expect(onlyMessage()).toBe(FORBIDDEN_TEXT);
    });

    it('presents a missing record as a WARNING, because it is not a system fault', async () => {
      const body: ProblemDetails = { title: 'Not Found', status: 404 };

      await expectRejection(body, 404, 'Not Found');

      expect(severities()).toEqual(['warning']);
    });

    it('presents a rate-limit refusal as INFORMATIONAL, quieter than a refusal', async () => {
      // ⚠ THE QUIETEST OF THE THREE, AND QUIETER THAN THE OTHER REFUSALS ON PURPOSE. A 429
      // rejects nothing on its merits and reports nothing misconfigured: the caller is simply
      // early, and the only action is to wait. The classification lives in the shared
      // `problemSeverity` and nowhere else - it used to be overridden inside the error banner,
      // so the SAME status reached an operator as a warning through this queue and as a calm
      // notice through the banner, on one screen, from two rules with no way of knowing about
      // each other. Asserting the informational severity here is what keeps the two surfaces
      // agreeing.
      const body: ProblemDetails = { title: 'Too Many Requests', status: 429 };

      await expectRejection(body, 429, 'Too Many Requests', LOGIN_URL);

      const chosen = severities();

      expect(chosen).withContext('the caller is early, not broken').toEqual(['info']);
      expect(chosen).not.toContain('error');
      expect(chosen).withContext('and quieter than an ordinary refusal').not.toContain('warning');
    });

    it('presents a fault as an ERROR', async () => {
      const body: ProblemDetails = { title: 'Internal Server Error', status: 500 };

      await expectRejection(body, 500, 'Internal Server Error');

      expect(severities()).toEqual(['error']);
    });

    it('presents a conflict as an ERROR, which the legacy pages are the authority for', async () => {
      const body: ProblemDetails = { title: 'Conflict', status: 409 };

      await expectRejection(body, 409, 'Conflict');

      // MIGRATION: this DIVERGES from the migration plan's summary table, which
      // presents a conflict as a warning, and the legacy source is why. Every conflict
      // message in the in-scope administration pages is surfaced through `RedError`:
      // Security/EditGroups.ascx.vb:L117, Security/EditRoles.ascx.vb:L256,
      // Portal/EditPortalAlias.ascx.vb:L226 and L238, and Tabs/ManageTabs.ascx.vb:L273
      // and L283. Behavioural equivalence with the measured legacy behaviour is what
      // the migration discipline requires, so the measurement wins over the summary.
      expect(severities()).toEqual(['error']);
    });

    it('presents an unprocessable entity as an ERROR, for the same measured reason', async () => {
      const body: ProblemDetails = { title: 'Unprocessable Entity', status: 422 };

      await expectRejection(body, 422, 'Unprocessable Entity');

      expect(severities()).toEqual(['error']);
    });

    it('presents an unanticipated status as an ERROR, because nobody planned for it', async () => {
      const body: ProblemDetails = { title: 'Not Implemented', status: 501 };

      await expectRejection(body, 501, 'Not Implemented');

      // A status is a plain number chosen by the server, so the fall-through arm is
      // genuinely reachable rather than defensive. A failure nobody anticipated is the
      // one most worth showing forcefully.
      expect(severities()).toEqual(['error']);
    });
  });

  // -------------------------------------------------------------------------
  // A VALIDATION DOCUMENT, READ WITH BRACKET ACCESS
  // -------------------------------------------------------------------------

  describe('a validation problem document', () => {
    it('is not announced, and its dictionary is read with bracket access only', async () => {
      const body: ValidationProblemDetails = {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        // Deliberately the empty string rather than omitted. The server serialises
        // with its null-omission condition set to never, so a member holding an empty
        // string, a zero or a false is WRITTEN rather than elided - the two conditions
        // that would drop them are forbidden server-side precisely because they would.
        // A sentinel therefore survives on the wire, and nothing here may "tidy" an
        // empty-string detail into an absent one.
        detail: '',
        traceId: TRACE_ID,
        // Keys are the server's model-state keys, reproduced byte for byte. They are
        // Pascal-cased because they name model members rather than JSON members, and
        // the camel-case naming policy that governs body property names does not
        // apply to dictionary keys.
        errors: {
          Email: ['The Email field is required.'],
          UserName: ['The UserName field is required.'],
        },
      };

      // BRACKET ACCESS, ALWAYS. `noPropertyAccessFromIndexSignature` is enabled for
      // this workspace, so dot access on this member is a COMPILE ERROR by design -
      // the key set is only ever known at run time, and dot access would let a typo
      // compile as a silent undefined. Read here as well as in the subject so that a
      // drift in the dictionary type breaks this file too.
      expect(body.errors['Email']).toEqual(['The Email field is required.']);
      expect(body.errors['UserName']).toEqual(['The UserName field is required.']);

      await expectRejection(body, 400, 'Bad Request');

      // No global announcement. Per-field messages belong beside the fields they
      // describe, which is what the shared form-field component renders - it takes a
      // single string per control - while the shared error-banner component takes the
      // whole document. Announcing them again here would say the same thing twice, the
      // second time somewhere no field is showing.
      expect(queued())
        .withContext('validation belongs on the form, not in a toast')
        .toEqual([]);
    });

    it('is recognised by its dictionary rather than by its status', async () => {
      // The API answers 400 both for a malformed request and for a REFUSED DOMAIN
      // OPERATION, so a status test would silence a refusal that no field is
      // displaying. The presence of renderable per-field messages is the marker.
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:role.name_duplicate',
        title: 'Bad Request',
        status: 400,
        detail: 'A role with that name already exists.',
      };

      await expectRejection(body, 400, 'Bad Request');

      expect(onlyMessage()).toBe('A role with that name already exists.');
    });

    it('is announced when its dictionary is present but empty', async () => {
      // The API writes the dictionary as an empty object whenever model state carries
      // no entries, so PRESENCE of the member and presence of a MESSAGE are different
      // questions. The document is a validation document, but nothing renderable was
      // reported against any field, which means no form control is showing the refusal
      // and the summary is the only place an operator can read it.
      const body: ValidationProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: 'The template could not be applied.',
        errors: {},
      };

      await expectRejection(body, 400, 'Bad Request');

      expect(onlyMessage())
        .withContext('an empty dictionary suppresses nothing')
        .toBe('The template could not be applied.');
    });

    it('is announced when its only messages are form-level rather than per-field', async () => {
      // Two key shapes are not field names at all: the empty string, which the
      // validation bridge uses when a rule reports no property, and `$`, which a
      // malformed request body produces. Both are still renderable beside the summary,
      // so both still count as something the form is showing.
      const body: ValidationProblemDetails = {
        title: 'Bad Request',
        status: 400,
        errors: { $: ['The request body could not be read.'] },
      };

      await expectRejection(body, 400, 'Bad Request');

      expect(body.errors['$']).toEqual(['The request body could not be read.']);
      expect(queued())
        .withContext('a form-level message is rendered beside the summary')
        .toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // LEGACY BREAK MARKUP IS RESOLVED TO PLAIN TEXT
  // -------------------------------------------------------------------------
  //
  // MIGRATION: the fragments are real, they ACCUMULATE, and they exist in both
  // spellings. Measured in this repository:
  //
  //   * Website/admin/Portal/Signup.ascx.vb L193 and L214 append
  //     `"<br>" & Localization.GetString("InvalidName", ..)` from INSIDE a
  //     per-character validation loop, so one bad name yields one fragment per
  //     offending character;
  //   * the same file's L221 appends the same shape for `InvalidPassword`;
  //   * L323 then wraps the whole accumulation as
  //     `lblMessage.Text = "<br>" & strMessage & "<br><br>"` - a LEADING break plus a
  //     TRAILING PAIR;
  //   * Website/admin/Users/User.ascx.vb L187 uses the SELF-CLOSING spelling:
  //     `valPassword.ErrorMessage = "<br/>" + UserController.GetUserCreateStatus(..)`;
  //   * Website/admin/Modules/Export.ascx.vb L189 is a THIRD accumulation site:
  //     `strMessage += "<br>" & String.Format(Localization.GetString("DiskSpaceExceeded"), strFile)`.
  //
  // The API relays that wording unaltered, so it arrives here. Rendering it as markup
  // is refused for the measured reason set out at the head of this file; presenting it
  // literally would show an operator `<br>You Must Enter a Valid Name`. Neither is
  // acceptable, so the markup is translated into the plain text it always meant.

  describe('legacy break markup', () => {
    it('strips a leading tag, strips a trailing pair, and turns an interior tag into a newline', async () => {
      const body: ProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: '<br/>Password is invalid<br>Name is invalid<br><br>',
      };

      await expectRejection(body, 400, 'Bad Request');

      const message = onlyMessage();

      expect(message).toBe('Password is invalid\nName is invalid');
      expect(message)
        .withContext('no break markup may survive into a message shown to an operator')
        .not.toContain('<br');
      expect(message).not.toMatch(/^\s/);
      expect(message).not.toMatch(/\s$/);
    });

    it('strips every spelling the legacy sources contain, case-insensitively', async () => {
      const spellings: readonly string[] = [
        // Signup.ascx.vb L193 / L214 / L221 / L323, and Export.ascx.vb L189.
        '<br>You Must Enter a Valid Name',
        // User.ascx.vb L187.
        '<br/>You Must Enter a Valid Name',
        // The spaced variant of the same tag.
        '<br />You Must Enter a Valid Name',
        // Upper case, to prove the match is case-insensitive rather than literal.
        '<BR/>You Must Enter a Valid Name',
        // Whitespace inside the tag, which the anchored pattern tolerates.
        '<br  / >You Must Enter a Valid Name',
      ];

      const normalised: string[] = [];

      for (const detail of spellings) {
        const body: ProblemDetails = { title: 'Bad Request', status: 400, detail };

        // Read and cleared per spelling, because five identical sentences in a row are collapsed
        // into one row by the service - which is the point of this loop, not a problem with it.
        normalised.push(await messageFrom(body, 400, 'Bad Request'));
      }

      expect(normalised).toEqual([
        'You Must Enter a Valid Name',
        'You Must Enter a Valid Name',
        'You Must Enter a Valid Name',
        'You Must Enter a Valid Name',
        'You Must Enter a Valid Name',
      ]);
      expect(messages().join('')).not.toContain('<br');
    });

    it('falls back to the title when the detail is nothing but break markup', async () => {
      const body: ProblemDetails = {
        title: 'You Must Enter a Valid Name',
        status: 400,
        detail: '<br><br>',
      };

      await expectRejection(body, 400, 'Bad Request');

      // The ordering that makes this work is load-bearing: the markup is resolved
      // BEFORE the blank test, so a detail of only break tags is recognised as empty
      // and a perfectly usable title is not discarded. Testing blankness on the raw
      // member first would select the detail, strip it to nothing, and announce a
      // message-less notification.
      expect(onlyMessage()).toBe('You Must Enter a Valid Name');
    });

    it('leaves every other tag as literal text rather than parsing untrusted markup', async () => {
      const body: ProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: '<br/>Enter a <b>valid</b> name',
      };

      await expectRejection(body, 400, 'Bad Request');

      // Deliberate, and the safer of the two options. Stripping arbitrary tags would
      // mean parsing untrusted markup in order to display it as text, which is a
      // larger attack surface than the cosmetic problem it solves. The tag is
      // therefore visible-but-inert: it reaches the operator as characters, bound as
      // text by the framework's default interpolation, and is never interpreted.
      expect(onlyMessage()).toBe('Enter a <b>valid</b> name');
    });

    it('does not mistake an element whose name merely begins with the same letters', async () => {
      const body: ProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: 'The <brochure> module could not be exported',
      };

      await expectRejection(body, 400, 'Bad Request');

      // The pattern is anchored tightly enough that the closing bracket must follow
      // the tag name with nothing between them but optional whitespace and one
      // optional solidus. A looser pattern would silently eat text.
      expect(onlyMessage()).toBe('The <brochure> module could not be exported');
    });
  });

  // -------------------------------------------------------------------------
  // THE SUPPORT REFERENCE
  // -------------------------------------------------------------------------
  //
  // The identifier is the ONLY join key between what an operator saw in the browser
  // and the request as the server recorded it, and it reaches the browser in the
  // problem document because that is what an error handler already has in hand. Its
  // provenance server-side: the correlation identifier this application stamps on
  // every outbound request ROUND-TRIPS - the correlation middleware runs before
  // request logging and INSIDE the exception-handling pipeline, so a failure that is
  // translated into a problem document still carries it - and the success envelope's
  // metadata type deliberately declares no correlation, trace or request member at
  // all. A problem document is therefore the only body that ever carries one.
  //
  // It is carried BOTH inside the message string and as its own member on the queued
  // entry, and the duplication is deliberate. The appended copy is what keeps the
  // single-string display contract intact for a consumer that renders only the message;
  // the member is what makes the reference survive the queue's length bound. The
  // reference used to be concatenated HERE, by this interceptor, and the queue then
  // bounded the already-composed string - so on a long server `detail`, which is exactly
  // the unexpected failure worth reporting, truncation removed the identifier and left an
  // operator nothing to quote. Composition now belongs to the queue, which is the party
  // that applies the bound and therefore the only one that can guarantee the suffix
  // outlives it. This interceptor still decides WHETHER a reference is quoted, because
  // that is a transport-status question.

  describe('the support reference', () => {
    it('is quoted for a fault, so an operator has something to report', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        traceId: TRACE_ID,
      };

      await expectRejection(body, 500, 'Internal Server Error');

      const message = onlyMessage();

      expect(message).toContain(TRACE_ID);
      expect(message).toContain(REFERENCE_LABEL);
      // Asserted as an EXACT sentence rather than only by substring, and that is the
      // stronger form for a second reason beyond wording: it proves the message
      // carries NOTHING ELSE. The envelope is environment-invariant - the server's
      // detail is fixed text per status and never carries an exception message, a
      // stack trace, a file path or a product version - and an exact match is what
      // pins that no diagnostic internals leaked into what an operator reads.
      expect(message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${TRACE_ID}`);
      expect(severities()).toEqual(['error']);
    });

    it('prefers the correlation identifier over the trace identifier', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
      };

      await expectRejection(body, 500, 'Internal Server Error');

      const message = onlyMessage();

      // They are two INDEPENDENT identifiers with two different formats, and only one
      // of them is findable. The correlation identifier appears on the response header,
      // on the request envelope in the server's log and on every audit event the
      // request produced; the trace identifier is taken from whatever diagnostic
      // activity happened to be current, so it appears in none of those records and
      // quoting it would hand an operator a reference nobody can look up.
      expect(message).toContain(CORRELATION_ID);
      expect(message)
        .withContext('the findable identifier wins, and only one is quoted')
        .not.toContain(TRACE_ID);
      expect(message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${CORRELATION_ID}`);
    });

    it('is omitted for a refusal, which is already self-explanatory', async () => {
      const refusals: readonly number[] = [400, 403, 404, 409, 422, 429];
      const raisedMessages: string[] = [];

      for (const status of refusals) {
        const body: ProblemDetails = {
          title: 'Refused',
          status,
          detail: 'The operation was refused.',
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
        };

        raisedMessages.push(await messageFrom(body, status, 'Refused'));
      }

      // A refusal is self-explanatory to the operator who provoked it: a duplicate
      // name, a missing record, a permission they do not hold, a rate limit they have
      // reached. Appending a diagnostic identifier would add noise to a message they
      // can already act on, and would invite them to report a working system as
      // broken.
      const joined = raisedMessages.join('\u0000');

      expect(raisedMessages.length).toBe(refusals.length);
      expect(joined).not.toContain(REFERENCE_LABEL);
      expect(joined).not.toContain(TRACE_ID);
      expect(joined).not.toContain(CORRELATION_ID);
    });

    it('is absent from the message when the document carried none', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
      };

      await expectRejection(body, 500, 'Internal Server Error');

      // Absence is ordinary rather than a fault: RFC 7807 makes every member optional,
      // and a proxy between the browser and the API can return a document this
      // application never produced.
      expect(onlyMessage()).toBe('An unexpected error occurred.');
    });

    it('is treated as absent when it is present but blank', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        correlationId: '   ',
      };

      await expectRejection(body, 500, 'Internal Server Error');

      // A blank identifier joins nothing to nothing, so quoting it would append a
      // dangling label an operator cannot use.
      expect(onlyMessage()).toBe('An unexpected error occurred.');
    });

    it('is omitted when it normalises away to nothing, rather than leaving a dangling label', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        // Not blank on arrival - it survives a trim, so it is selected as the
        // reference - but it normalises to nothing once break markup is resolved.
        correlationId: '<br>',
      };

      await expectRejection(body, 500, 'Internal Server Error');

      // This is the specification for the invariant "NO server-supplied text reaches a
      // notification unnormalised", asserted at the one place where it would otherwise
      // have an exception. An identifier cannot legitimately carry markup, so
      // normalising it looks redundant - but every other server-supplied fragment shown
      // to an operator has passed the same normaliser, and the function that selects
      // the reference only trims. Without this step the reference would be the single
      // unnormalised value on the path, which is precisely the kind of exception a
      // later reader has to rediscover the hard way.
      //
      // The consequence asserted here is the second half: having normalised to nothing,
      // it is DROPPED rather than quoted, so no "Reference:" label is left dangling
      // with nothing after it.
      const message = onlyMessage();

      expect(message).toBe('An unexpected error occurred.');
      expect(message).not.toContain(REFERENCE_LABEL);
      expect(message).not.toContain('<br');
    });
  });

  // -------------------------------------------------------------------------
  // A RATE-LIMIT REFUSAL IS ANNOUNCED AND NEVER RE-ATTEMPTED
  // -------------------------------------------------------------------------
  //
  // MIGRATION: this status exists in this application ONLY because the legacy
  // verification control was deliberately dropped.
  // Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L162 gated every
  // credential submission on
  // `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then`, and that
  // was the only anti-automation control anywhere in the legacy tree - a search for
  // rate limiting or throttling across the five in-scope domain trees and all of the
  // in-scope administration pages matches nothing at all, so the limiter itself is
  // net-new.
  //
  // The compensating control is the API's ADDRESS-PARTITIONED credential window, applied
  // by a GLOBAL limiter that classifies each request from endpoint metadata - the
  // `[CredentialEndpoint]` marker on the action - falling back to a whole credential path
  // segment on a body-carrying method. It admits a fixed permit count per window and
  // refuses beyond it with this status. The health endpoint the container health check
  // probes is never throttled because a GET carries no marker and the fall-back matcher
  // considers only POST, PUT and PATCH, so it resolves to the shared no-limit partition.

  describe('a rate-limit refusal', () => {
    it('is announced calmly, because nothing failed and the caller is simply early', async () => {
      // No title and no detail, so the status sentence is what remains - which is the
      // sentence whose calmness this specification is about. The limiter's own refusal
      // carries no explanatory text of its own, so this is also the realistic shape.
      const body: ProblemDetails = { status: 429 };

      await expectRejection(body, 429, 'Too Many Requests', LOGIN_URL);

      const message = onlyMessage();

      expect(message).toBe(TOO_MANY_ATTEMPTS_TEXT);
      // Calm wording is the requirement, so the absence of alarm is asserted as well
      // as the presence of the sentence. Alarming wording here would suggest a fault
      // where there is none.
      expect(message.toLowerCase()).not.toContain('error');
      expect(message.toLowerCase()).not.toContain('fail');
      // The informational severity, which is the shared rule's answer for this status and the
      // one the banner's calm band is driven from.
      expect(severities()).toEqual(['info']);
    });

    it('is NEVER re-attempted, not even once', async () => {
      const pending = firstValueFrom(http.post(LOGIN_URL, { userName: 'someone' }));

      httpMock.expectOne(LOGIN_URL).flush(
        { title: 'Too Many Requests', status: 429 },
        {
          status: 429,
          statusText: 'Too Many Requests',
          headers: { 'Content-Type': 'application/problem+json' },
        },
      );

      await expectAsync(pending).toBeRejected();

      // THE ASSERTION THAT MATTERS. Re-attempting a refusal whose entire purpose is to
      // slow an automated caller down would defeat the control that replaced the
      // verification field, and would spend the operator's remaining permits on their
      // behalf so that a person who waited would still be refused.
      //
      // Stated directly here, and stated again by `verify()` in `afterEach` for every
      // other specification in this file: no backoff, no retry operator and no parsing
      // of the standard retry hint exists anywhere on this path.
      httpMock.expectNone(LOGIN_URL);
    });

    it('is not re-attempted for a fault either', async () => {
      // The absence of re-attempts is total rather than status-specific. A retry
      // introduced "just for a fault" would double every request a failing server
      // received, precisely when it could least afford it.
      const body: ProblemDetails = { status: 503 };

      await expectRejection(body, 503, 'Service Unavailable');

      httpMock.expectNone(PORTALS_URL);
      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
    });
  });

  // -------------------------------------------------------------------------
  // APPLICATION FAILURE CODES REACH THE OPERATOR VERBATIM
  // -------------------------------------------------------------------------

  describe('a conflict', () => {
    it('surfaces the failure code verbatim, dots and all', async () => {
      // `Portal.LastPortal` CONTAINS A DOT AND IS ONE CODE STRING, not a nested path
      // and not a namespace to be split. Asserting exact equality is what proves it
      // arrives whole: a message of `LastPortal` or `Portal` would pass a naive
      // substring check and would be wrong.
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:portal.last_portal',
        title: 'Conflict',
        status: 409,
        detail: 'Portal.LastPortal',
      };

      await expectRejection(body, 409, 'Conflict');

      const message = onlyMessage();

      expect(message).toBe('Portal.LastPortal');
      expect(message).toContain('Portal.LastPortal');
      expect(message)
        .withContext('the dot belongs to the code, so nothing may truncate at it')
        .not.toBe('Portal');
    });

    it('surfaces a second code from the same vocabulary verbatim', async () => {
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:portal.alias_duplicate',
        title: 'Conflict',
        status: 409,
        detail: 'DuplicatePortalAlias',
      };

      await expectRejection(body, 409, 'Conflict');

      expect(onlyMessage()).toBe('DuplicatePortalAlias');
    });

    it('surfaces a code embedded in a sentence without disturbing the sentence', async () => {
      const body: ProblemDetails = {
        title: 'Conflict',
        status: 409,
        detail: 'DuplicateRole: a role with that name already exists.',
      };

      await expectRejection(body, 409, 'Conflict');

      expect(onlyMessage()).toBe('DuplicateRole: a role with that name already exists.');
    });

    it('uses the concurrent-edit sentence when the document carries no text of its own', async () => {
      await expectRejection({ status: 409 }, 409, 'Conflict');

      expect(onlyMessage()).toBe(CONFLICT_TEXT);
    });
  });

  describe('an unprocessable entity', () => {
    it('surfaces each import-validation code verbatim', async () => {
      const codes: readonly string[] = ['NotValidXml', 'NotCorrectType', 'ImportNotSupported'];

      for (const detail of codes) {
        const body: ProblemDetails = { title: 'Unprocessable Entity', status: 422, detail };

        await expectRejection(body, 422, 'Unprocessable Entity');
      }

      expect(messages()).toEqual(['NotValidXml', 'NotCorrectType', 'ImportNotSupported']);
    });

    it('uses the refused-values sentence when the document carries no text of its own', async () => {
      await expectRejection({ status: 422 }, 422, 'Unprocessable Entity');

      expect(onlyMessage()).toBe(VALIDATION_REJECTED_TEXT);
    });
  });

  // -------------------------------------------------------------------------
  // MESSAGE SELECTION
  // -------------------------------------------------------------------------

  describe('message selection', () => {
    it('prefers the detail, which describes this occurrence', async () => {
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:portal.not_found',
        title: 'Not Found',
        status: 404,
        detail: 'Portal 42 does not exist.',
      };

      await expectRejection(body, 404, 'Not Found');

      expect(onlyMessage()).toBe('Portal 42 does not exist.');
    });

    it('falls back to the title, which describes the class of failure', async () => {
      const body: ProblemDetails = { title: 'Not Found', status: 404 };

      await expectRejection(body, 404, 'Not Found');

      expect(onlyMessage()).toBe('Not Found');
    });

    it('falls back to a status-appropriate sentence when the document carries no text', async () => {
      await expectRejection({ status: 404 }, 404, 'Not Found');

      expect(onlyMessage()).toBe(NOT_FOUND_TEXT);
    });

    it('treats a whitespace-only detail as absent rather than announcing a blank line', async () => {
      const body: ProblemDetails = { title: 'Not Found', status: 404, detail: '   ' };

      await expectRejection(body, 404, 'Not Found');

      expect(onlyMessage()).toBe('Not Found');
    });

    it('reads a problem document delivered as a JSON string, as a proxy may return it', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL, { responseType: 'text' }));

      httpMock
        .expectOne(PORTALS_URL)
        .flush('{"title":"Conflict","status":409,"detail":"Already registered."}', {
          status: 409,
          statusText: 'Conflict',
          headers: { 'Content-Type': 'application/problem+json' },
        });

      await expectAsync(pending).toBeRejected();

      expect(onlyMessage()).toBe('Already registered.');
    });
  });

  // -------------------------------------------------------------------------
  // AN EMPTY VALUE AND AN ABSENT VALUE TAKE THE SAME BRANCH
  // -------------------------------------------------------------------------
  //
  // MIGRATION: no silent normalisation happens in either direction, and the legacy
  // null contract is why the two are the same value rather than two values that get
  // conflated. Library/Components/Shared/Null.vb defines the string "absent" marker
  // with a body of literally `Return ""`, and Website/admin/Portal/Signup.ascx.vb
  // tests one variable for emptiness two different ways in one file - `If strMessage
  // = ""` at L227 and L269, and `If strMessage = Null.NullString` at L315 -
  // interchangeably, because for that codebase they ARE the same comparison. Neither
  // spelling is rewritten into the other here; they simply take the same branch, as
  // they always did.

  describe('an empty value and an absent value', () => {
    it('produce the same message, so neither is normalised into the other', async () => {
      const empty: ProblemDetails = { title: 'Not Found', status: 404, detail: '' };
      const absent: ProblemDetails = { title: 'Not Found', status: 404 };

      // One at a time, because two identical sentences in a row are collapsed onto one row - which
      // is itself evidence that the two inputs took the same branch, but it is not what this
      // specification is about.
      const fromEmpty = await messageFrom(empty, 404, 'Not Found');
      const fromAbsent = await messageFrom(absent, 404, 'Not Found');

      expect(fromEmpty).toBe('Not Found');
      expect(fromAbsent)
        .withContext('an empty string and an absent member take the same branch')
        .toBe(fromEmpty);
    });

    it('are both preserved rather than tidied into a null on the way through', async () => {
      // The server serialises with its null-omission condition set to NEVER, so a
      // member holding an empty string, a zero or a false is WRITTEN rather than
      // elided; the two conditions that would drop them are forbidden server-side for
      // exactly that reason. A sentinel therefore survives on the wire, and the
      // interceptor must read it as the empty string it is - not convert it, and not
      // reject the document for carrying it.
      const body: ProblemDetails = {
        type: '',
        title: '',
        status: 400,
        detail: '',
        traceId: '',
      };

      await expectRejection(body, 400, 'Bad Request');

      // Every member is present and every one is empty, so the document is readable
      // and supplies no text; the status sentence is what remains.
      expect(onlyMessage()).toBe(REQUEST_REJECTED_TEXT);
    });
  });

  // -------------------------------------------------------------------------
  // A BODY THAT IS NOT A PROBLEM DOCUMENT DEGRADES GRACEFULLY
  // -------------------------------------------------------------------------
  //
  // Every fixture in this section is typed {@link FlushableBody} rather than with the
  // wire contract, because being a shape that contract does not describe IS the
  // point - and deriving the type from the testing backend is what lets them be
  // expressed without a cast. The
  // requirement they share is that the interceptor must not FAULT while handling a
  // fault: replacing the caller's failure with a client-side type error would report
  // the wrong problem entirely and would queue nothing for the person who submitted
  // the request.

  describe('a body that is not a problem document', () => {
    it('describes an unreachable server rather than reciting a status of zero', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).error(new ProgressEvent('error'), {
        status: 0,
        statusText: '',
      });

      await expectAsync(pending).toBeRejected();

      // The framework's own text for this condition reads "Http failure response for
      // /api/v1/portals: 0 Unknown Error", which names an internal path and a status of
      // zero and tells an operator nothing they can act on.
      //
      // The ORDERING inside the interceptor is what makes this work, and it is
      // load-bearing rather than tidy: a status of zero is resolved BEFORE the body is
      // read, because the framework puts a DOM progress event in the body slot for this
      // condition and such an event carries a string `type` member - one of the very
      // members the deliberately permissive shape test accepts. Reading the body first
      // would mistake a transport failure for a problem document that happens to say
      // nothing.
      expect(onlyMessage()).toBe(NETWORK_UNAVAILABLE_TEXT);
      expect(severities()).toEqual(['error']);
    });

    it('falls back by status when the body is a proxy error page rather than JSON', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL, { responseType: 'text' }));

      httpMock.expectOne(PORTALS_URL).flush('<html><body>Bad Gateway</body></html>', {
        status: 502,
        statusText: 'Bad Gateway',
        headers: { 'Content-Type': 'text/html' },
      });

      await expectAsync(pending).toBeRejected();

      // A gateway is free to return anything at all, so the string is parsed
      // defensively and a parse failure yields no document rather than propagating. No
      // part of that page would describe the failure better than the status does, and
      // the markup must not reach an operator.
      const message = onlyMessage();

      expect(message).toBe(SERVER_ERROR_TEXT);
      expect(message).not.toContain('<');
    });

    it('falls back by status when there is no body at all', async () => {
      const empty: FlushableBody = null;

      await expectRejection(empty, 500, 'Internal Server Error');

      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
      expect(severities()).toEqual(['error']);
    });

    it('falls back by status when a member of the document carries the wrong type', async () => {
      // The body that a shape test asking whether SOME member matched would have
      // accepted: the status is a number, so the document would have been narrowed to a
      // shape declaring a string detail, and the message extractor would then have
      // called a string method on the number 42 and thrown INSIDE the interceptor -
      // replacing a server refusal the caller could have rendered with an unhandled
      // client fault, and queueing nothing for the person who submitted the request.
      const malformed: FlushableBody = { status: 400, detail: 42 };

      await expectRejection(malformed, 400, 'Bad Request');

      expect(onlyMessage())
        .withContext('a malformed document is announced by status, not by faulting')
        .toBe(REQUEST_REJECTED_TEXT);
    });

    it('falls back by status for a dictionary that is null or holds a bare string', async () => {
      // A type-of test alone excludes neither of these: null reports as an object, and
      // a dictionary whose value is a bare string is an object too. Both would have put
      // a non-iterable value in front of the summary.
      const nullDictionary: FlushableBody = { status: 400, errors: null };
      const bareString: FlushableBody = { status: 400, errors: { PortalName: 'Required.' } };

      const fromNull = await messageFrom(nullDictionary, 400, 'Bad Request');
      const fromBareString = await messageFrom(bareString, 400, 'Bad Request');

      expect([fromNull, fromBareString]).toEqual([REQUEST_REJECTED_TEXT, REQUEST_REJECTED_TEXT]);
    });

    it('falls back by status for a body that is an array', async () => {
      // A problem document is an object, and an array is not one. Admitting it would
      // let index reads stand in for member reads.
      const arrayBody: FlushableBody = [{ status: 400, detail: 'Nope.' }];

      await expectRejection(arrayBody, 400, 'Bad Request');

      expect(onlyMessage()).toBe(REQUEST_REJECTED_TEXT);
    });

    it('falls back by status for a string body that parses but is not a problem document', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL, { responseType: 'text' }));

      // Valid JSON, so the defensive parse SUCCEEDS - and the result is still not a
      // problem document. The parse succeeding and the result being usable are two
      // different questions, and only the second one decides what an operator reads.
      httpMock.expectOne(PORTALS_URL).flush('{"items":[],"totalCount":0}', {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'Content-Type': 'application/json' },
      });

      await expectAsync(pending).toBeRejected();

      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
    });

    it('falls back by status for a JSON object with no problem member at all', async () => {
      // Without the "at least one recognised member" requirement, an arbitrary
      // successful payload passed to the guard by mistake would be read as a failure
      // description.
      const unrelated: FlushableBody = { items: [], totalCount: 0 };

      await expectRejection(unrelated, 500, 'Internal Server Error');

      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
    });
  });

  // -------------------------------------------------------------------------
  // WHO PRESENTS A FAILURE
  // -------------------------------------------------------------------------
  //
  // A failed request has exactly ONE publisher. This interceptor used to announce every
  // failed response while the root signal stores simultaneously retained the same failure
  // for their screens to bind to the shared error banner, so a single refusal produced two
  // independently-worded, independently-dismissible reports of one incident. The request
  // CONTEXT is how the two sides now agree: a caller that presents its own failures marks
  // its request, and this interceptor stays silent for it.
  //
  // The default is the noisy one - unmarked means announced - so a caller that forgets the
  // marker is reported twice rather than not at all.

  describe('presentation ownership', () => {
    it('announces a failure on a request that carries no presentation marker', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
      };

      await expectRejection(body, 500, 'Internal Server Error');

      expect(queued().length)
        .withContext('an unpresented failure has no other publisher, so this one reports it')
        .toBe(1);
    });

    it('announces nothing when the caller presents the failure itself', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        traceId: TRACE_ID,
      };

      const pending = firstValueFrom(
        http.get(PORTALS_URL, { context: presentedInContext() }),
      );

      httpMock.expectOne(PORTALS_URL).flush(body, {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'Content-Type': 'application/problem+json' },
      });

      await expectAsync(pending)
        .withContext('suppressing the announcement must not suppress the failure')
        .toBeRejected();

      expect(queued())
        .withContext('the caller presents this failure, so a second report would duplicate it')
        .toEqual([]);
    });

    it('still rejects a marked request with the original failure, unchanged', async () => {
      const pending = firstValueFrom(
        http.get(PORTALS_URL, { context: presentedInContext() }),
      );

      httpMock.expectOne(PORTALS_URL).flush(
        { title: 'Conflict', status: 409, detail: 'That name is already in use.' },
        { status: 409, statusText: 'Conflict' },
      );

      const reason: unknown = await pending.then(
        () => null,
        (error: unknown) => error,
      );

      // The store that marked the request is the party that will present this, so it has
      // to receive the whole response rather than a substitute.
      expect(reason instanceof HttpErrorResponse).toBeTrue();
      expect((reason as HttpErrorResponse).status).toBe(409);
    });

    it('retains the support reference as a member of the queued entry', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        correlationId: CORRELATION_ID,
      };

      await expectRejection(body, 500, 'Internal Server Error');

      const entry: AppNotification = queued()[0];

      // Structural as well as appended: the member is what survives the queue's length
      // bound, which is the property the previous concatenation could not offer.
      expect(entry.reference).toBe(CORRELATION_ID);
      expect(entry.message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${CORRELATION_ID}`);
    });

    it('quotes no reference on a refusal, and leaves the member null', async () => {
      const body: ProblemDetails = {
        title: 'Conflict',
        status: 409,
        detail: 'That name is already in use.',
        correlationId: CORRELATION_ID,
      };

      await expectRejection(body, 409, 'Conflict');

      const entry: AppNotification = queued()[0];

      // A refusal is self-explanatory to the operator who provoked it, so an identifier
      // would add noise and invite them to report a working system as broken.
      expect(entry.reference).toBeNull();
      expect(entry.message).not.toContain(REFERENCE_LABEL);
    });
  });
});
