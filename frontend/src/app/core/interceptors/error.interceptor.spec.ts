import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import type { TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import { NotificationService, presentedInContext } from '../services/notification.service';
import type { AppNotification, NotificationSeverity } from '../services/notification.service';
import { CORRELATION_ID_HEADER } from './correlation-id.interceptor';
import { errorInterceptor } from './error.interceptor';

// ENDPOINTS

/** A representative resource endpoint. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The credential endpoint. Used by the specifications that matter most for it: a 401 here is a refused
 * sign-in rather than an expired session, and a 429 here is the rate limiter that replaced the legacy
 * verification control.
 */
const LOGIN_URL = '/api/v1/auth/login';

// SYNTHETIC IDENTIFIERS
// Shaped like the real values so that a reader recognises them, and obviously synthetic so that no reader
// mistakes one for a captured production identifier. Runs of a single hex digit are used for exactly that
// reason.

const TRACE_ID = '00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-00';

/** A correlation identifier, in the opaque form this application stamps. */
const CORRELATION_ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

// EXPECTED WORDING
// Re-declared here as literals rather than imported from the utility that produces them, and the choice is
// deliberate. Importing the constants would make these specifications tautological: a change that reworded
// every message would still pass, because both sides of the comparison would have moved together.

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

/** Shown for 429. */
const TOO_MANY_ATTEMPTS_TEXT = 'Too many attempts. Wait a moment and try again.';

/** Shown for any status at or above 500. */
const SERVER_ERROR_TEXT = 'The server could not complete the request. Try again shortly.';

/** Shown when no response arrived at all. */
const NETWORK_UNAVAILABLE_TEXT =
  'The server could not be reached. Check your connection and try again.';

/** Introduces the support reference when one is quoted. */
const REFERENCE_LABEL = 'If you report this, quote reference';

/** A canonical identifier as the reverse proxy returns it on a response. */
const GATEWAY_CORRELATION_ID = '4d19ae7c1b8f4e2a9d6c3f5b7a091e2d';

/** Any body the testing backend can be told to respond with. */
type FlushableBody = Parameters<TestRequest['flush']>[0];

describe('errorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ORDER MATTERS AND IS NOT COSMETIC. The real client is configured first, WITH the interceptor
        // under test, and the testing backend then replaces the transport underneath that
        // already-configured chain.
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
      ],
      // NotificationService is deliberately NOT listed. It is provided at the root, so the interceptor's
      // own `inject` resolves the very same instance this specification reads - which is what makes reading
      // its signal a genuine observation of the interceptor's behaviour rather than of a double's.
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  // -------------------------------------------------------------------------

  /**
   * Issues a request, fails it with the supplied body and status, and asserts that the caller was told.
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
   * Raises one failure and returns the message it queued, leaving the queue EMPTY afterwards. ⚠ NEEDED
   * BECAUSE THE SERVICE COLLAPSES AN IMMEDIATE REPETITION. Several specifications below prove that
   * several DIFFERENT inputs normalise to the SAME sentence, and they can only do that by raising them
   * one after another - at which point the queue legitimately holds one row rather than one per raise.
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

  // NOTHING IS EVER SWALLOWED
  // The invariant underneath every other specification in this file.

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

      // The IDENTITY of the thrown value matters, not merely that something was thrown. A feature reads the
      // problem document off this object to render its banner, so re-throwing a fresh error - or the parsed
      // body, or a string would leave every caller with a rejection it cannot render.
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

  // D-I1: A 401 IS ANNOUNCED BY NOBODY HERE
  // The negative specification described at the head of this file.

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

      // A 401 from the credential endpoint is a REFUSED SIGN-IN rather than an expired session, and the
      // sign-in screen reads that problem document and presents it beside the form itself.
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

      // The early return happens BEFORE any reference is read, and it must stay there. A reference-bearing
      // 401 is the tempting exception that would reintroduce the spurious announcement this whole section
      // exists to prevent.
      await expectRejection(body, 401, 'Unauthorized');

      expect(queued()).toEqual([]);
    });
  });

  // SEVERITY: A REFUSAL IS NOT A FAULT
  // The distinction is load-bearing rather than decorative, and it is measured against the legacy
  // application rather than chosen.

  describe('severity', () => {
    it('presents a permission refusal as a WARNING, never as an error', async () => {
      // Deliberately carries NEITHER a detail nor a title, so the status sentence is what remains.
      const body: ProblemDetails = {
        type: 'urn:dnnmigration:error:authorization.forbidden',
        status: 403,
        detail: '',
      };

      await expectRejection(body, 403, 'Forbidden');

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
        // Deliberately the empty string rather than omitted.
        detail: '',
        traceId: TRACE_ID,
        // Keys are the server's model-state keys, reproduced byte for byte. They are Pascal-cased because
        // they name model members rather than JSON members, and the camel-case naming policy that governs
        // body property names does not apply to dictionary keys.
        errors: {
          Email: ['The Email field is required.'],
          UserName: ['The UserName field is required.'],
        },
      };

      expect(body.errors['Email']).toEqual(['The Email field is required.']);
      expect(body.errors['UserName']).toEqual(['The UserName field is required.']);

      await expectRejection(body, 400, 'Bad Request');

      expect(queued())
        .withContext('validation belongs on the form, not in a toast')
        .toEqual([]);
    });

    it('is recognised by its dictionary rather than by its status', async () => {
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

  // LEGACY BREAK MARKUP IS RESOLVED TO PLAIN TEXT

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
        '<br>You Must Enter a Valid Name',
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

      // The ordering that makes this work is load-bearing: the markup is resolved BEFORE the blank test, so
      // a detail of only break tags is recognised as empty and a perfectly usable title is not discarded.
      expect(onlyMessage()).toBe('You Must Enter a Valid Name');
    });

    it('leaves every other tag as literal text rather than parsing untrusted markup', async () => {
      const body: ProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: '<br/>Enter a <b>valid</b> name',
      };

      await expectRejection(body, 400, 'Bad Request');

      // Deliberate, and the safer of the two options. Stripping arbitrary tags would mean parsing untrusted
      // markup in order to display it as text, which is a larger attack surface than the cosmetic problem
      // it solves.
      expect(onlyMessage()).toBe('Enter a <b>valid</b> name');
    });

    it('does not mistake an element whose name merely begins with the same letters', async () => {
      const body: ProblemDetails = {
        title: 'Bad Request',
        status: 400,
        detail: 'The <brochure> module could not be exported',
      };

      await expectRejection(body, 400, 'Bad Request');

      expect(onlyMessage()).toBe('The <brochure> module could not be exported');
    });
  });

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
      // Asserted as an EXACT sentence rather than only by substring, and that is the stronger form for a
      // second reason beyond wording: it proves the message carries NOTHING ELSE. The envelope is
      // environment-invariant - the server's detail is fixed text per status and never carries an exception
      // message, a stack trace, a file path or a product version - and an exact match is what pins that no
      // diagnostic internals leaked into what an operator reads.
      expect(message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${TRACE_ID}.`);
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

      expect(message).toContain(CORRELATION_ID);
      expect(message)
        .withContext('the findable identifier wins, and only one is quoted')
        .not.toContain(TRACE_ID);
      expect(message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${CORRELATION_ID}.`);
    });

    it('falls back to the response header when the body carries no correlation identifier', async () => {
      const body: ProblemDetails = {
        title: 'Service Unavailable',
        status: 503,
        detail: 'The application is temporarily unavailable.',
      };

      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).flush(body, {
        status: 503,
        statusText: 'Service Unavailable',
        headers: {
          'Content-Type': 'application/problem+json',
          'X-Correlation-Id': GATEWAY_CORRELATION_ID,
        },
      });

      await expectAsync(pending).toBeRejected();

      expect(onlyMessage())
        .withContext('the identifier the proxy returned is what the operator quotes')
        .toBe(
          `The application is temporarily unavailable. ${REFERENCE_LABEL} ${GATEWAY_CORRELATION_ID}.`,
        );
    });

    it('refuses a response header that is not a canonical identifier', async () => {
      // ⚠ A RESPONSE HEADER IS SERVER-SUPPLIED TEXT. An intermediary that echoed something arbitrary into
      // it would otherwise put that arbitrary text in front of an operator - the exact class of defect the
      // canonical shape was introduced to close on the REQUEST path, reopened on the response path.
      const body: ProblemDetails = {
        title: 'Service Unavailable',
        status: 503,
        detail: 'The application is temporarily unavailable.',
      };

      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).flush(body, {
        status: 503,
        statusText: 'Service Unavailable',
        headers: {
          'Content-Type': 'application/problem+json',
          'X-Correlation-Id': 'Integr8tion!Pass',
        },
      });

      await expectAsync(pending).toBeRejected();

      const message = onlyMessage();

      expect(message)
        .withContext('nothing arbitrary from a header reaches an operator')
        .not.toContain('Integr8tion!Pass');
      expect(message)
        .withContext('and no empty reference label is left behind either')
        .toBe('The application is temporarily unavailable.');
    });

    it('prefers the document over the header, because the API knows its own identifier', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
        correlationId: CORRELATION_ID,
      };

      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).flush(body, {
        status: 500,
        statusText: 'Internal Server Error',
        headers: {
          'Content-Type': 'application/problem+json',
          'X-Correlation-Id': GATEWAY_CORRELATION_ID,
        },
      });

      await expectAsync(pending).toBeRejected();

      const message = onlyMessage();

      expect(message).toContain(CORRELATION_ID);
      expect(message).not.toContain(GATEWAY_CORRELATION_ID);
    });

    // ⚠ INVERTED FROM WHAT THIS ONCE REQUIRED, AND THE INVERSION IS THE FIX. It asserted that every one
    // of the six refusal statuses quoted NO identifier, on the reasoning that a refusal explains itself to
    // whoever provoked it. That holds for a `400` an operator can see in their own form; it fails badly
    // for the rest. A `404` on a record someone was sent a link to, a `403` they believe they should have
    // passed, a `409` whose other party they cannot see - all refusals, all needing a support report, and
    // all left with nothing to quote. The banner meanwhile rendered `Reference:` from the same document,
    // so the two surfaces contradicted each other about one response.
    it('is quoted for every refusal too, because a refusal is exactly what gets reported', async () => {
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

      expect(raisedMessages.length).toBe(refusals.length);

      for (const [index, raised] of raisedMessages.entries()) {
        expect(raised)
          .withContext(`status ${refusals[index]} must offer something to quote`)
          .toContain(REFERENCE_LABEL);
        // `correlationId` wins over `traceId` where a document carries both, which is the
        // precedence `problemSupportReference` already publishes.
        expect(raised).toContain(CORRELATION_ID);
      }
    });

    it('is absent from the message when the document carried none', async () => {
      const body: ProblemDetails = {
        title: 'Internal Server Error',
        status: 500,
        detail: 'An unexpected error occurred.',
      };

      await expectRejection(body, 500, 'Internal Server Error');

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

      // The consequence asserted here is the second half: having normalised to nothing, it is DROPPED
      // rather than quoted, so no "Reference:" label is left dangling with nothing after it.
      const message = onlyMessage();

      expect(message).toBe('An unexpected error occurred.');
      expect(message).not.toContain(REFERENCE_LABEL);
      expect(message).not.toContain('<br');
    });
  });

  // A RATE-LIMIT REFUSAL IS ANNOUNCED AND NEVER RE-ATTEMPTED
  // MIGRATION: this status exists in this application ONLY because the legacy verification control was
  // deliberately dropped.

  describe('a rate-limit refusal', () => {
    it('is announced calmly, because nothing failed and the caller is simply early', async () => {
      const body: ProblemDetails = { status: 429 };

      await expectRejection(body, 429, 'Too Many Requests', LOGIN_URL);

      const message = onlyMessage();

      expect(message).toBe(TOO_MANY_ATTEMPTS_TEXT);
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

      httpMock.expectNone(LOGIN_URL);
    });

    it('is not re-attempted for a fault either', async () => {
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
    /**
     * ⚠ THIS CASE USED TO ASSERT THAT ALL THREE REFUSALS STAYED ON SCREEN AT ONCE, AND THAT WAS THE
     * DEFECT — QA-26. Three refusals of the SAME request are three answers to one question, and only the
     * last of them is still true; stacking them left an operator reading a refusal about an attempt they
     * had already replaced. The interceptor now scopes each report by the request's method and address, so
     * a later answer retires the earlier one.
     *
     * What the case was written to prove is unchanged and still proved: each code reaches the surface
     * VERBATIM rather than being collapsed into a generic sentence. It is proved per attempt, which is when
     * the claim is actually about something, rather than by inspecting a queue of stale entries.
     */
    it('surfaces each import-validation code verbatim, one current answer at a time', async () => {
      const codes: readonly string[] = ['NotValidXml', 'NotCorrectType', 'ImportNotSupported'];

      for (const detail of codes) {
        const body: ProblemDetails = { title: 'Unprocessable Entity', status: 422, detail };

        await expectRejection(body, 422, 'Unprocessable Entity');

        expect(onlyMessage())
          .withContext(`${detail} is surfaced as the server wrote it`)
          .toBe(detail);
      }

      expect(messages())
        .withContext('and the two it replaced are gone, not stacked beneath it')
        .toEqual(['ImportNotSupported']);
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

  // AN EMPTY VALUE AND AN ABSENT VALUE TAKE THE SAME BRANCH

  describe('an empty value and an absent value', () => {
    it('produce the same message, so neither is normalised into the other', async () => {
      const empty: ProblemDetails = { title: 'Not Found', status: 404, detail: '' };
      const absent: ProblemDetails = { title: 'Not Found', status: 404 };

      const fromEmpty = await messageFrom(empty, 404, 'Not Found');
      const fromAbsent = await messageFrom(absent, 404, 'Not Found');

      expect(fromEmpty).toBe('Not Found');
      expect(fromAbsent)
        .withContext('an empty string and an absent member take the same branch')
        .toBe(fromEmpty);
    });

    it('are both preserved rather than tidied into a null on the way through', async () => {
      // The server serialises with its null-omission condition set to NEVER, so a member holding an empty
      // string, a zero or a false is WRITTEN rather than elided; the two conditions that would drop them
      // are forbidden server-side for exactly that reason.
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

  // A BODY THAT IS NOT A PROBLEM DOCUMENT DEGRADES GRACEFULLY
  // Every fixture in this section is typed {@link FlushableBody} rather than with the wire contract,
  // because being a shape that contract does not describe IS the point - and deriving the type from the
  // testing backend is what lets them be expressed without a cast.

  describe('a body that is not a problem document', () => {
    it('describes an unreachable server rather than reciting a status of zero', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL));

      httpMock.expectOne(PORTALS_URL).error(new ProgressEvent('error'), {
        status: 0,
        statusText: '',
      });

      await expectAsync(pending).toBeRejected();

      // The ORDERING inside the interceptor is what makes this work, and it is load-bearing rather than
      // tidy: a status of zero is resolved BEFORE the body is read, because the framework puts a DOM
      // progress event in the body slot for this condition and such an event carries a string `type` member
      // - one of the very members the deliberately permissive shape test accepts.
      expect(onlyMessage()).toBe(NETWORK_UNAVAILABLE_TEXT);
      expect(severities()).toEqual(['error']);
    });

    // ⚠ THE ONLY REFERENCE THAT SURVIVES A FAILURE WITH NO RESPONSE. Every other route to a reference reads
    // the ANSWER - the problem document's `correlationId`, or the response header - and a request that was
    // aborted, blocked, or sent while the network was down has no answer to read. This branch passed a
    // hard-coded null on the reasoning that there is no server-side reference, which is true and beside the
    // point: the identifier this application generated for the request still exists, and if the request did
    // reach the API before the connection broke, the server logged this very value.
    it('quotes the identifier it put on the outbound request when no response arrives', async () => {
      const pending = firstValueFrom(
        http.get(PORTALS_URL, { headers: { [CORRELATION_ID_HEADER]: CORRELATION_ID } }),
      );

      httpMock.expectOne(PORTALS_URL).error(new ProgressEvent('error'), {
        status: 0,
        statusText: '',
      });

      await expectAsync(pending).toBeRejected();

      const entry: AppNotification = queued()[0];

      expect(entry.reference)
        .withContext('a transport failure is still reportable to support')
        .toBe(CORRELATION_ID);
      // The quoted reference ends the sentence, exactly as it does for every other message this interceptor
      // composes - see the cases above. A message that stopped mid-sentence on this one branch would be the
      // only one that did.
      expect(entry.message).toBe(
        `${NETWORK_UNAVAILABLE_TEXT} ${REFERENCE_LABEL} ${CORRELATION_ID}.`,
      );
    });

    it('quotes nothing when the outbound identifier is not canonical', async () => {
      // A caller-supplied header must not be able to put arbitrary text in front of an operator.
      const pending = firstValueFrom(
        http.get(PORTALS_URL, { headers: { [CORRELATION_ID_HEADER]: 'not-an-identifier' } }),
      );

      httpMock.expectOne(PORTALS_URL).error(new ProgressEvent('error'), {
        status: 0,
        statusText: '',
      });

      await expectAsync(pending).toBeRejected();

      const entry: AppNotification = queued()[0];

      expect(entry.reference).toBeNull();
      expect(entry.message).toBe(NETWORK_UNAVAILABLE_TEXT);
    });

    it('falls back by status when the body is a proxy error page rather than JSON', async () => {
      const pending = firstValueFrom(http.get(PORTALS_URL, { responseType: 'text' }));

      httpMock.expectOne(PORTALS_URL).flush('<html><body>Bad Gateway</body></html>', {
        status: 502,
        statusText: 'Bad Gateway',
        headers: { 'Content-Type': 'text/html' },
      });

      await expectAsync(pending).toBeRejected();

      // A gateway is free to return anything at all, so the string is parsed defensively and a parse
      // failure yields no document rather than propagating. No part of that page would describe the failure
      // better than the status does, and the markup must not reach an operator.
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
      // The body that a shape test asking whether SOME member matched would have accepted: the status is a
      // number, so the document would have been narrowed to a shape declaring a string detail, and the
      // message extractor would then have called a string method on the number 42 and thrown INSIDE the
      // interceptor replacing a server refusal the caller could have rendered with an unhandled client
      // fault, and queueing nothing for the person who submitted the request.
      const malformed: FlushableBody = { status: 400, detail: 42 };

      await expectRejection(malformed, 400, 'Bad Request');

      expect(onlyMessage())
        .withContext('a malformed document is announced by status, not by faulting')
        .toBe(REQUEST_REJECTED_TEXT);
    });

    it('falls back by status for a dictionary that is null or holds a bare string', async () => {
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

      httpMock.expectOne(PORTALS_URL).flush('{"items":[],"totalCount":0}', {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'Content-Type': 'application/json' },
      });

      await expectAsync(pending).toBeRejected();

      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
    });

    it('falls back by status for a JSON object with no problem member at all', async () => {
      const unrelated: FlushableBody = { items: [], totalCount: 0 };

      await expectRejection(unrelated, 500, 'Internal Server Error');

      expect(onlyMessage()).toBe(SERVER_ERROR_TEXT);
    });
  });

  // WHO PRESENTS A FAILURE

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
      expect(entry.message).toBe(`An unexpected error occurred. ${REFERENCE_LABEL} ${CORRELATION_ID}.`);
    });

    // ⚠ THIS SPECIFICATION IS INVERTED FROM WHAT IT ONCE ASSERTED, AND THE INVERSION IS THE FIX. It
    // required that a refusal quote NO reference, on the reasoning that a refusal is self-explanatory to
    // whoever provoked it. Measured against real reports, that reasoning fails exactly where help is most
    // needed: a `404` on a record someone was sent a link to, a `403` they believe they should have
    // passed, a `409` whose other party they cannot see. Those are refusals, and they were the cases with
    // nothing to quote. Worse, the two surfaces disagreed - the banner rendered `Reference:` from the very
    // same document while the notification beside it showed none.
    it('quotes the reference on a refusal too, so every failure can be reported', async () => {
      const body: ProblemDetails = {
        title: 'Conflict',
        status: 409,
        detail: 'That name is already in use.',
        correlationId: CORRELATION_ID,
      };

      await expectRejection(body, 409, 'Conflict');

      const entry: AppNotification = queued()[0];

      expect(entry.reference)
        .withContext('the identifier is carried structurally, so the queue bound cannot truncate it away')
        .toBe(CORRELATION_ID);
      expect(entry.message).toContain(REFERENCE_LABEL);
      expect(entry.message).toContain(CORRELATION_ID);
    });

    it('quotes the reference on a not-found, which is the most reported refusal of all', async () => {
      const body: ProblemDetails = {
        title: 'Not Found',
        status: 404,
        detail: 'That role could not be found.',
        traceId: TRACE_ID,
      };

      await expectRejection(body, 404, 'Not Found');

      const entry: AppNotification = queued()[0];

      expect(entry.reference).toBe(TRACE_ID);
      expect(entry.message).toContain(REFERENCE_LABEL);
    });

    it('still quotes nothing when the server supplied no identifier at all', async () => {
      const body: ProblemDetails = {
        title: 'Forbidden',
        status: 403,
        detail: 'You are not permitted to read this portal.',
      };

      await expectRejection(body, 403, 'Forbidden');

      const entry: AppNotification = queued()[0];

      // Availability is now the ONLY test, so absence must still mean absence rather than an
      // invented or blank reference.
      expect(entry.reference).toBeNull();
      expect(entry.message).not.toContain(REFERENCE_LABEL);
    });
  });

  // -------------------------------------------------------------------------
  // THE NAVIGATION SWEEP IS NOT PRE-EMPTED
  // -------------------------------------------------------------------------

  describe('the navigation exemption', () => {
    it('is NOT claimed for a failure this interceptor announces', async () => {
      const body: ProblemDetails = {
        title: 'Conflict',
        status: 409,
        detail: 'That name is already in use.',
      };

      await expectRejection(body, 409, 'Conflict');

      expect(messages()).withContext('the failure is announced').toHaveSize(1);

      // The shell calls this on a COMPLETED navigation, which is the event that means the operator has
      // genuinely arrived somewhere else.
      notifications.clearOnNavigation();

      expect(messages())
        .withContext('a conflict the operator stays put for does not follow them to the next screen')
        .toEqual([]);
    });

    it('leaves retention to the callers that know a departure is coming', async () => {
      // REMOVING THE BLANKET EXEMPTION LOSES NOTHING, because every path that actually redirects or ejects
      // claims it explicitly and at the point it knows a departure is imminent - the permission guard, the
      // authentication store, the session-teardown service, and the module-import, portal-settings,
      // role-form and membership-settings screens.
      const body: ProblemDetails = {
        title: 'Forbidden',
        status: 403,
        detail: 'You do not hold that permission.',
      };

      await expectRejection(body, 403, 'Forbidden');

      // Exactly what a redirecting caller does immediately after the announcement and before requesting
      // the navigation.
      notifications.retainAcrossNavigation();
      notifications.clearOnNavigation();

      expect(messages())
        .withContext('a caller that asks for the reprieve still gets it')
        .toEqual(['You do not hold that permission.']);
    });
  });
});
