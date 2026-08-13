import {
  HttpClient,
  HttpContext,
  HttpContextToken,
  HttpHeaders,
  HttpParams,
  HttpRequest,
  HttpResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import type { HttpEvent, HttpHandlerFn, HttpInterceptorFn } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { correlationIdInterceptor } from './correlation-id.interceptor';

/**
 * The header the interceptor attaches. Restated here rather than imported: the interceptor keeps its
 * `CORRELATION_ID_HEADER` constant module-private, and widening its exported surface merely to be
 * observable from a spec would be the wrong trade.
 */
const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/**
 * The same header name in lower case. HTTP compares header names case-insensitively and `HttpHeaders`
 * normalises them accordingly, so a caller who supplies this spelling must be recognised as having
 * already supplied the header.
 */
const LOWER_CASE_CORRELATION_ID_HEADER = 'x-correlation-id';

/**
 * The number of random bytes a version 4 UUID is built from, mirroring the interceptor's own
 * `UUID_BYTE_LENGTH`.
 */
const UUID_BYTE_LENGTH = 16;

/**
 * The canonical RFC 4122 version 4 shape: 36 characters, lower-case hexadecimal, the version nibble fixed
 * at `4` and the variant nibble one of `8`, `9`, `a`, `b`. Anchored at both ends so a value with leading
 * or trailing content fails.
 */
const CANONICAL_UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** A collection endpoint, matching the `/api/v1` prefix the API exposes. */
const PORTAL_LIST_URL = '/api/v1/portals';

/**
 * A page of portals, as `GET /api/v1/portals` really answers. ⚠ THE RESPONSE SHAPE MATTERS EVEN WHERE THE
 * ASSERTION IS ABOUT A REQUEST HEADER. The five ordering probes below flushed a bare `null` for this
 * endpoint, and the listing cannot answer that: it is the one endpoint whose body IS the page envelope,
 * so it always answers `{ items, meta }`.
 */
const PORTAL_PAGE_BODY = Object.freeze({
  items: [
    {
      portalId: -1,
      portalName: 'Baseline Portal',
      aliases: ['localhost'],
      users: 3,
      pages: 7,
      hostSpace: 0,
    },
  ],
  meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
});

/**
 * A second endpoint, used where two distinct requests must be told apart. The canonical role collection
 * is flat; its tenant is resolved from the request host.
 */
const ROLE_LIST_URL = '/api/v1/roles';

/**
 * The identifier of the portal a delete spec addresses. `0` rather than `1`, because the legacy
 * `Portals.PortalID` column is declared `IDENTITY(-1,1)`: the seed and first generated value is `-1`,
 * while the shipped default portal row is inserted explicitly with `PortalID` `0`, so both are legitimate
 * row identifiers - and `-1` is simultaneously the legacy `Null.NullInteger` sentinel.
 */
const DELETED_PORTAL_ID = 0;

/**
 * A caller-supplied identifier in the COMPACT canonical form: 32 hexadecimal characters. Deliberately a
 * form this interceptor could not have produced - it generates the hyphenated 36-character rendering and
 * nothing else - so a forwarded value matching this one is the strongest available evidence that the
 * caller's value really was preserved rather than regenerated and coincidentally equal.
 */
const CALLER_SUPPLIED_ID = '9f2c4d6e8a0b1c3d5e7f0a1b2c3d4e5f';

/**
 * A second caller-supplied identifier, used only to build a request whose correlation header arrives on
 * **two** header lines.
 */
const SECOND_CALLER_SUPPLIED_ID = '0123456789abcdef0123456789abcdef';

const OTHER_HEADER = 'X-Other-Header';

/** The value of {@link OTHER_HEADER}, expected back byte-identical. */
const OTHER_HEADER_VALUE = 'preserved verbatim';

/** A second unrelated header, so the "everything else" claim covers more than one. */
const ACCEPT_HEADER = 'Accept';

/** The value of {@link ACCEPT_HEADER}. */
const ACCEPT_HEADER_VALUE = 'application/json';

/**
 * The credential header the next interceptor in the real chain attaches. Named here so the ordering specs
 * can assert against the same spelling the negative spec uses, and so that "the identifier is stamped
 * before credentials are attached" is expressed once rather than as a scattered string literal.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * An obviously synthetic credential for the ordering probe to attach. Deliberately unmistakable as a
 * placeholder.
 */
const FAKE_BEARER_CREDENTIAL = 'Bearer fake-access-token';

/** A request body, expected to survive the header clone by identity. */
const REQUEST_BODY: Readonly<Record<string, string>> = {
  portalName: 'Migrated Portal',
  description: 'authored by this specification',
};

/** A query string, expected to survive the header clone. */
const QUERY_PARAMETER_NAME = 'pageSize';

/** The value of {@link QUERY_PARAMETER_NAME}. */
const QUERY_PARAMETER_VALUE = '25';

/** The value a stubbed `crypto.randomUUID` returns. */
const STUBBED_RANDOM_UUID: `${string}-${string}-${string}-${string}-${string}` =
  '11111111-2222-4333-8444-555555555555';

/**
 * The exact identifier produced when `crypto.getRandomValues` fills the 16 byte buffer with the ascending
 * sequence `0x00 .. 0x0f`.
 */
const ASCENDING_BYTES_UUID = '00010203-0405-4607-8809-0a0b0c0d0e0f';

/** The exact identifier produced from 16 zero bytes. */
const ALL_BITS_CLEAR_UUID = '00000000-0000-4000-8000-000000000000';

/** The exact identifier produced from 16 `0xff` bytes. */
const ALL_BITS_SET_UUID = 'ffffffff-ffff-4fff-bfff-ffffffffffff';

/**
 * The constant `Math.random` result used to make the last-resort path deterministic. `Math.floor(0.5 *
 * 256)` is `0x80`, so every one of the 16 bytes arrives as `0x80` before the version and variant
 * rewriting.
 */
const CONSTANT_RANDOM_FRACTION = 0.5;

/**
 * The exact identifier produced when every byte is `0x80`. byte 6 (0x80 & 0x0f) | 0x40 = 0x40 byte 8
 * (0x80 & 0x3f) | 0x80 = 0x80 hex 80808080808040808080808080808080 grouped
 * 80808080-8080-4080-8080-808080808080.
 */
const CONSTANT_FRACTION_UUID = '80808080-8080-4080-8080-808080808080';

/**
 * How many identifiers a uniqueness spec draws. Large enough that a stuck or memoised generator is caught
 * immediately, small enough that the spec stays instantaneous.
 */
const UNIQUENESS_SAMPLE_SIZE = 128;

/**
 * The sentinel recorded in place of a missing identifier, so a uniqueness spec distinguishes "128
 * distinct values" from "127 distinct values plus one request that was never stamped".
 */
const MISSING_IDENTIFIER = '<no identifier was attached>';

/**
 * The response a stub `next` hands back. Compared by identity in the forwarding specs, which is what
 * proves the interceptor returns the downstream observable untouched rather than wrapping, re-emitting or
 * replacing the response.
 */
const TERMINAL_RESPONSE = new HttpResponse<unknown>({ status: 204, statusText: 'No Content' });

/** The default of {@link DIAGNOSTIC_CONTEXT}, which no spec should ever observe. */
const CONTEXT_DEFAULT = 'the token default, which means the context was lost';

/** The value stored in the request context, expected to survive the header clone. */
const CARRIED_CONTEXT_VALUE = 'carried across the clone';

/**
 * An `HttpContext` token, used to prove that cloning for the header does not drop the request context.
 * Downstream interceptors carry per-request switches in the context, so losing it would break them
 * silently.
 */
const DIAGNOSTIC_CONTEXT = new HttpContextToken<string>(() => CONTEXT_DEFAULT);

/** What a single direct invocation of the interceptor was observed to do. */
interface Interception {
  /** Every request handed to `next`, in order. Exactly one for a healthy call. */
  readonly forwarded: readonly HttpRequest<unknown>[];
  /** Every event the returned observable emitted. */
  readonly events: readonly HttpEvent<unknown>[];
  /** Whether the returned observable completed. */
  readonly completed: boolean;
  /** Whether the returned observable errored. */
  readonly errored: boolean;
}

/**
 * Invokes the interceptor directly with a stub `next` and records everything observable about the call.
 * The stub returns a synchronous `of(...)`, so the returned observable completes before this function
 * does and the recorded flags are final by the time the caller asserts on them - no `fakeAsync`, no
 * `tick`, no scheduler involved.
 *
 * @param request The request to hand to the interceptor.
 * @returns What the interceptor forwarded and what its observable emitted.
 */
function runInterceptor(request: HttpRequest<unknown>): Interception {
  const forwarded: HttpRequest<unknown>[] = [];
  const events: HttpEvent<unknown>[] = [];
  let completed = false;
  let errored = false;

  const next: HttpHandlerFn = (outbound) => {
    forwarded.push(outbound);

    return of(TERMINAL_RESPONSE);
  };

  correlationIdInterceptor(request, next).subscribe({
    next: (event) => {
      events.push(event);
    },
    error: () => {
      errored = true;
    },
    complete: () => {
      completed = true;
    },
  });

  return { forwarded, events, completed, errored };
}

/**
 * Builds a `GET` request that carries two unrelated headers and no correlation identifier - the ordinary
 * case the interceptor exists to handle.
 */
function requestWithoutIdentifier(): HttpRequest<unknown> {
  return new HttpRequest<unknown>('GET', PORTAL_LIST_URL, {
    headers: new HttpHeaders({
      [ACCEPT_HEADER]: ACCEPT_HEADER_VALUE,
      [OTHER_HEADER]: OTHER_HEADER_VALUE,
    }),
  });
}

/**
 * Builds a fully populated `PUT` request: body, query parameters, request context, response type and
 * credentials flag all set to non-default values, so a clone that dropped any one of them is detectable.
 *
 * @param headerName The spelling under which the correlation identifier is supplied, or `undefined` to
 * omit it entirely.
 */
function fullyPopulatedRequest(headerName?: string): HttpRequest<unknown> {
  const headers =
    headerName === undefined
      ? new HttpHeaders({ [OTHER_HEADER]: OTHER_HEADER_VALUE })
      : new HttpHeaders({ [OTHER_HEADER]: OTHER_HEADER_VALUE, [headerName]: CALLER_SUPPLIED_ID });

  return new HttpRequest<unknown>('PUT', PORTAL_LIST_URL, REQUEST_BODY, {
    headers,
    params: new HttpParams().set(QUERY_PARAMETER_NAME, QUERY_PARAMETER_VALUE),
    context: new HttpContext().set(DIAGNOSTIC_CONTEXT, CARRIED_CONTEXT_VALUE),
    responseType: 'json',
    withCredentials: true,
    reportProgress: true,
  });
}

/** Reads the correlation identifier off a request, or `null` when absent. */
function identifierOn(request: HttpRequest<unknown>): string | null {
  return request.headers.get(CORRELATION_ID_HEADER);
}

/**
 * Builds a request carrying an arbitrary inbound identifier. `HttpHeaders` is constructed from an object
 * literal rather than by cloning, because a value that the interceptor is expected to REJECT must reach
 * it exactly as the caller wrote it — untrimmed, unnormalised and of whatever length the test chose.
 *
 * @param value The value to place in the correlation header.
 * @returns A GET request carrying that value and nothing else of interest.
 */
function requestCarrying(value: string): HttpRequest<unknown> {
  return new HttpRequest<unknown>('GET', PORTAL_LIST_URL).clone({
    setHeaders: { [CORRELATION_ID_HEADER]: value },
  });
}

/**
 * Builds a request whose correlation header arrives on **two** header lines. `append` is the only way to
 * reach this state, and reaching it is the entire reason this helper exists rather than reusing {@link
 * requestCarrying}.
 *
 * @param first The value on the first header line.
 * @param second The value on the second header line.
 * @returns A GET request carrying both values under the correlation header.
 */
function requestCarryingTwoIdentifiers(first: string, second: string): HttpRequest<unknown> {
  const headers = new HttpHeaders()
    .append(CORRELATION_ID_HEADER, first)
    .append(CORRELATION_ID_HEADER, second);

  return new HttpRequest<unknown>('GET', PORTAL_LIST_URL, { headers });
}

/** Reads every value the correlation header carries, or `null` when it is absent. */
function allIdentifiersOn(request: HttpRequest<unknown>): readonly string[] | null {
  return request.headers.getAll(CORRELATION_ID_HEADER);
}

/** The hyphenated canonical form, in upper case. */
const UPPER_CASE_HYPHENATED_ID = '4D19AE7C-1B8F-4E2A-9D6C-3F5B7A091E2D';

/** A value one character short of the compact canonical form. */
const ONE_CHARACTER_SHORT_ID = '9f2c4d6e8a0b1c3d5e7f0a1b2c3d4e5';

/** A value one character longer than the compact canonical form. */
const ONE_CHARACTER_LONG_ID = '9f2c4d6e8a0b1c3d5e7f0a1b2c3d4e5f0';

/**
 * Builds a `crypto.getRandomValues` replacement that fills the buffer deterministically.
 *
 * @param byteAt Produces the byte for a given index.
 * @param observeLength Optional hook receiving the length actually requested.
 */
function fillingWith(
  byteAt: (index: number) => number,
  observeLength?: (length: number) => void,
) {
  return function fill<T extends ArrayBufferView | null>(array: T): T {
    if (array instanceof Uint8Array) {
      observeLength?.(array.length);

      for (let index = 0; index < array.length; index += 1) {
        array[index] = byteAt(index);
      }
    }

    return array;
  };
}

/** A property that was shadowed on a global, together with how to put it back. */
interface OwnPropertyShim {
  /** The object the shim was installed on. */
  readonly target: object;
  /** The property name that was shadowed. */
  readonly name: string;
  /**
   * The own descriptor found before shadowing, or `undefined` when the property was inherited from a
   * prototype and the object had no own descriptor at all.
   */
  readonly original: PropertyDescriptor | undefined;
}

/**
 * Every shim installed by the currently running spec. Emptied by the suite-wide `afterEach`, which is
 * what keeps randomised spec order safe.
 */
const installedShims: OwnPropertyShim[] = [];

/**
 * Makes an inherited platform primitive look absent to the code under test. The interceptor decides
 * between its three generation sources with `typeof crypto.randomUUID === 'function'`, and a Jasmine spy
 * is itself a function - so `spyOn` cannot express "not present".
 *
 * @param target The object to shadow a property on.
 * @param name The property to make appear absent.
 */
function shadowAsAbsent(target: object, name: string): void {
  installedShims.push({
    target,
    name,
    original: Object.getOwnPropertyDescriptor(target, name),
  });

  Object.defineProperty(target, name, {
    value: undefined,
    writable: true,
    enumerable: true,
    configurable: true,
  });
}

function restoreShadowedProperties(): void {
  for (const shim of installedShims.splice(0).reverse()) {
    if (shim.original === undefined) {
      delete (shim.target as Record<string, unknown>)[shim.name];
    } else {
      Object.defineProperty(shim.target, shim.name, shim.original);
    }
  }
}

describe('correlationIdInterceptor', () => {
  // Suite-wide safety net. Jasmine unwinds `spyOn` on its own; the own-property shims cannot be spies, so
  // they are unwound here for every spec whether or not that spec installed one.
  afterEach(() => {
    restoreShadowedProperties();
  });

  describe('the environment these specs assume', () => {
    it('exposes crypto.randomUUID, so the preferred path is reachable', () => {
      expect(typeof crypto.randomUUID).toBe('function');
    });

    it('exposes crypto.getRandomValues, so the middle path is reachable', () => {
      expect(typeof crypto.getRandomValues).toBe('function');
    });

    it('carries neither primitive as an own property of crypto', () => {
      expect([
        Object.getOwnPropertyDescriptor(crypto, 'randomUUID'),
        Object.getOwnPropertyDescriptor(crypto, 'getRandomValues'),
      ]).toEqual([undefined, undefined]);
    });
  });

  describe('the request boundary', () => {
    let httpClient: HttpClient;
    let httpMock: HttpTestingController;

    /** A third-party address on an origin this application does not own. */
    const FOREIGN_URL = 'https://third-party.example/collect';

    /** A same-origin address that is not beneath the configured API base. */
    const NON_API_SAME_ORIGIN_URL = '/assets/config.json';

    /** The liveness probe, published at the host root rather than under the API base. */
    const HEALTH_URL = '/health';

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(withInterceptors([correlationIdInterceptor])),
          provideHttpClientTesting(),
        ],
      });

      httpClient = TestBed.inject(HttpClient);
      httpMock = TestBed.inject(HttpTestingController);
    });

    afterEach(() => {
      httpMock.verify();
    });

    it('stamps a request addressed to this API', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();

      expect(httpMock.expectOne(PORTAL_LIST_URL).request.headers.has(CORRELATION_ID_HEADER)).toBeTrue();
    });

    it('leaves a foreign origin unstamped', () => {
      httpClient.get(FOREIGN_URL).subscribe();

      expect(httpMock.expectOne(FOREIGN_URL).request.headers.has(CORRELATION_ID_HEADER)).toBeFalse();
    });

    it('leaves a same-origin address outside the API base unstamped', () => {
      httpClient.get(NON_API_SAME_ORIGIN_URL).subscribe();

      expect(
        httpMock.expectOne(NON_API_SAME_ORIGIN_URL).request.headers.has(CORRELATION_ID_HEADER),
      ).toBeFalse();
    });

    it('does not carry a caller-supplied identifier to a foreign origin', () => {
      // The leak, asserted directly. Preservation is the branch that made the unscoped
      // version disclose rather than merely add.
      httpClient
        .get(FOREIGN_URL, { headers: { [CORRELATION_ID_HEADER]: 'caller-supplied-identifier' } })
        .subscribe();

      const forwarded = httpMock.expectOne(FOREIGN_URL).request;

      // The caller's own header is not STRIPPED either - the interceptor removes nothing it did not add -
      // so what is asserted is that the interceptor neither replaced it nor took any part in sending it.
      expect(forwarded.headers.get(CORRELATION_ID_HEADER)).toBe('caller-supplied-identifier');
    });

    it('forwards a non-API request as the very same object rather than a clone', () => {
      // Evidence that the exclusion is a pass-through: a clone would mean the interceptor
      // had rebuilt the request, and anything it rebuilt it could also change.
      const observed: HttpRequest<unknown>[] = [];

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(
            withInterceptors([
              correlationIdInterceptor,
              (req, next) => {
                observed.push(req);

                return next(req);
              },
            ]),
          ),
          provideHttpClientTesting(),
        ],
      });

      const client = TestBed.inject(HttpClient);
      const mock = TestBed.inject(HttpTestingController);

      client.get(FOREIGN_URL).subscribe();

      const dispatched = mock.expectOne(FOREIGN_URL).request;

      expect(observed.length).toBe(1);
      expect(observed[0]).toBe(dispatched);
      mock.verify();
    });

    it('stamps the health probes even though they sit outside the API base', () => {
      // The one explicit inclusion. The probes are this API's own endpoints and its correlation middleware
      // runs for them, so a probe must stay traceable - the address test alone would exclude them because
      // they are published at the host root.
      for (const probe of ['/health', '/health/live', '/health/ready']) {
        httpClient.get(probe).subscribe();

        expect(httpMock.expectOne(probe).request.headers.has(CORRELATION_ID_HEADER))
          .withContext(`probe ${probe}`)
          .toBeTrue();
      }
    });

    it('recognises a probe addressed with a trailing separator', () => {
      httpClient.get(`${HEALTH_URL}/`).subscribe();

      expect(
        httpMock.expectOne(`${HEALTH_URL}/`).request.headers.has(CORRELATION_ID_HEADER),
      ).toBeTrue();
    });

    it('does not treat a foreign host as a probe merely because its path matches', () => {
      const impostor = `https://third-party.example${HEALTH_URL}`;

      httpClient.get(impostor).subscribe();

      expect(httpMock.expectOne(impostor).request.headers.has(CORRELATION_ID_HEADER)).toBeFalse();
    });
  });

  describe('registration through withInterceptors', () => {
    let httpClient: HttpClient;
    let httpMock: HttpTestingController;

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          // The real client is provided first and the testing backend second, which is the order Angular
          // requires: `provideHttpClientTesting` replaces the backend `provideHttpClient` installed, and a
          // reversed order leaves the live backend in place and sends the request at the network.
          provideHttpClient(withInterceptors([correlationIdInterceptor])),
          provideHttpClientTesting(),
        ],
      });

      httpClient = TestBed.inject(HttpClient);
      httpMock = TestBed.inject(HttpTestingController);
    });

    afterEach(() => {
      httpMock.verify();
    });

    it('attaches the header to an ordinary client call', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();

      expect(httpMock.expectOne(PORTAL_LIST_URL).request.headers.has(CORRELATION_ID_HEADER)).toBeTrue();
    });

    it('attaches a canonical version 4 UUID', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();

      expect(identifierOn(httpMock.expectOne(PORTAL_LIST_URL).request)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('forwards exactly one request per client call', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();

      // An interceptor that subscribed to its own downstream observable, or that
      // returned a merged stream, would issue the request more than once.
      expect(httpMock.match(PORTAL_LIST_URL).length).toBe(1);
    });

    it('leaves a caller-supplied header in place on a real client call', () => {
      httpClient
        .get(PORTAL_LIST_URL, { headers: { [CORRELATION_ID_HEADER]: CALLER_SUPPLIED_ID } })
        .subscribe();

      expect(identifierOn(httpMock.expectOne(PORTAL_LIST_URL).request)).toBe(CALLER_SUPPLIED_ID);
    });

    it('stamps a request whose response is an error', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe({ error: () => undefined });

      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      const identifier = identifierOn(pending.request);
      pending.flush('failed', { status: 500, statusText: 'Internal Server Error' });

      // The identifier is attached on the way out, before any outcome is known, so a failing request
      // carries it exactly as a succeeding one does. That is what lets a server-side error log line be
      // joined to the call that provoked it.
      expect(identifier).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('stamps a request that carries a body', () => {
      httpClient.post(PORTAL_LIST_URL, REQUEST_BODY).subscribe();

      expect(identifierOn(httpMock.expectOne(PORTAL_LIST_URL).request)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('leaves the body of that request untouched', () => {
      httpClient.post(PORTAL_LIST_URL, REQUEST_BODY).subscribe();

      expect(httpMock.expectOne(PORTAL_LIST_URL).request.body).toEqual(REQUEST_BODY);
    });

    it('stamps a bodyless mutating request', () => {
      httpClient.delete(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).subscribe();

      // The third verb completes the method-agnosticism claim. The interceptor branches on the inbound
      // header alone and never inspects the method, so a regression that started keying off the verb -
      // stamping only requests with a body, say - would pass the GET and POST specs above and fail here.
      expect(
        identifierOn(httpMock.expectOne(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).request),
      ).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('sends that request under the method the caller chose', () => {
      httpClient.delete(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).subscribe();

      // Stamping the header must not disturb the verb: the clone carries the method across, and a delete
      // that arrived as anything else would be a different operation entirely.
      expect(httpMock.expectOne(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).request.method).toBe(
        'DELETE',
      );
    });

    it('gives all three verbs an identifier, and a different one each', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      httpClient.post(PORTAL_LIST_URL, REQUEST_BODY).subscribe();
      httpClient.delete(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).subscribe();

      const identifiers = httpMock
        .match(() => true)
        .map((pending) => identifierOn(pending.request) ?? MISSING_IDENTIFIER);

      // Three requests, three identifiers, none missing - asserted as a set so the
      // spec states "all distinct" rather than enumerating pairs.
      expect(new Set(identifiers).size).toBe(3);
      expect(identifiers).not.toContain(MISSING_IDENTIFIER);
    });

    it('gives two concurrent client calls two different identifiers', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      httpClient.get(ROLE_LIST_URL).subscribe();

      expect(identifierOn(httpMock.expectOne(PORTAL_LIST_URL).request)).not.toBe(
        identifierOn(httpMock.expectOne(ROLE_LIST_URL).request),
      );
    });
  });

  describe('position in the interceptor chain', () => {
    let httpClient: HttpClient;
    let httpMock: HttpTestingController;
    let identifierSeenByProbe: (string | null)[];
    let authorizationSeenByProbe: (string | null)[];

    beforeEach(() => {
      identifierSeenByProbe = [];
      authorizationSeenByProbe = [];

      // A stand-in for the auth interceptor, registered *after* the one under test exactly as
      // `app.config.ts` registers the real one.
      const orderProbeInterceptor: HttpInterceptorFn = (request, next) => {
        identifierSeenByProbe.push(identifierOn(request));
        authorizationSeenByProbe.push(request.headers.get(AUTHORIZATION_HEADER));

        return next(
          request.clone({ setHeaders: { [AUTHORIZATION_HEADER]: FAKE_BEARER_CREDENTIAL } }),
        );
      };

      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(withInterceptors([correlationIdInterceptor, orderProbeInterceptor])),
          provideHttpClientTesting(),
        ],
      });

      httpClient = TestBed.inject(HttpClient);
      httpMock = TestBed.inject(HttpTestingController);
    });

    afterEach(() => {
      httpMock.verify();
    });

    it('has already stamped the header by the time the next interceptor runs', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      httpMock.expectOne(PORTAL_LIST_URL).flush(PORTAL_PAGE_BODY);

      expect(identifierSeenByProbe).toEqual([jasmine.stringMatching(CANONICAL_UUID_PATTERN)]);
    });

    it('runs before the credential header is attached', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      httpMock.expectOne(PORTAL_LIST_URL).flush(PORTAL_PAGE_BODY);

      // The other half of the same ordering claim. Asserting only that the probe saw the identifier would
      // be satisfied by either order if some later change also stamped the identifier late; asserting that
      // the probe had not yet added its own header pins the direction unambiguously.
      expect(authorizationSeenByProbe).toEqual([null]);
    });

    it('hands the later interceptor the identifier that reaches the backend', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(PORTAL_PAGE_BODY);

      expect(identifierSeenByProbe).toEqual([identifierOn(pending.request)]);
    });

    it('lets the later interceptor add its header without disturbing the identifier', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(PORTAL_PAGE_BODY);

      // Both headers arrive together. This is the shape a real authenticated call has on the wire, and it
      // proves the two interceptors compose rather than overwrite one another's work.
      expect([
        identifierOn(pending.request),
        pending.request.headers.get(AUTHORIZATION_HEADER),
      ]).toEqual([jasmine.stringMatching(CANONICAL_UUID_PATTERN), FAKE_BEARER_CREDENTIAL]);
    });

    it('gives the first attempt and a downstream re-send the same identifier', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(PORTAL_PAGE_BODY);

      // The mechanism the preservation contract exists to serve, observed end to end. `auth.interceptor.ts`
      // recovers a 401 by re-sending the request object it was handed - one this interceptor has already
      // stamped - so the retry carries the first attempt's identifier rather than a second one.
      const reSent = runInterceptor(pending.request).forwarded[0];

      expect(identifierOn(reSent)).toBe(identifierOn(pending.request));
    });
  });

  describe('a request that already carries the header', () => {
    // The consumer is `auth.interceptor.ts`. When the API answers 401 it renews the session and retries
    // ONCE, and it builds that retry by cloning the ORIGINAL request object it was handed - the very object
    // this interceptor, sitting outside it, has already stamped - adding only a refreshed bearer token.
    it('forwards the very same request object rather than a clone', () => {
      const original = fullyPopulatedRequest(CORRELATION_ID_HEADER);

      // Identity, not equality. A retry must re-send byte-identical headers so the server recognises the
      // second attempt as the same logical operation; an equal-looking clone would satisfy `toEqual` while
      // still being a different object, and the contract is explicit that the original is forwarded.
      expect(runInterceptor(original).forwarded[0]).toBe(original);
    });

    it('forwards it exactly once', () => {
      expect(runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER)).forwarded.length).toBe(1);
    });

    it('preserves the supplied value byte for byte', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER)).forwarded[0];

      expect(identifierOn(forwarded)).toBe(CALLER_SUPPLIED_ID);
    });

    it('does not replace a value that could not have been generated here', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER)).forwarded[0];

      // The preserved value is the COMPACT canonical form, which this interceptor never generates — it
      // emits the hyphenated rendering — so a value matching it cannot have been minted here and
      // coincidentally compared equal.
      expect(identifierOn(forwarded)).not.toMatch(CANONICAL_UUID_PATTERN);
    });

    it('recognises the header under its lower-case spelling too', () => {
      const original = fullyPopulatedRequest(LOWER_CASE_CORRELATION_ID_HEADER);

      expect(runInterceptor(original).forwarded[0]).toBe(original);
    });

    it('generates nothing when the header is already present', () => {
      const randomUUID = spyOn(crypto, 'randomUUID').and.returnValue(STUBBED_RANDOM_UUID);
      const getRandomValues = spyOn(crypto, 'getRandomValues').and.callThrough();

      runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER));

      expect([randomUUID.calls.count(), getRandomValues.calls.count()]).toEqual([0, 0]);
    });

    it('returns the downstream observable untouched', () => {
      const interception = runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER));

      expect(interception.events).toEqual([TERMINAL_RESPONSE]);
    });

    it('lets the downstream observable complete', () => {
      const interception = runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER));

      expect([interception.completed, interception.errored]).toEqual([true, false]);
    });
  });

  describe('a request carrying an inbound value the API would refuse', () => {
    // THE SINGLE-LINE CLAUSE. Asserted first because it is the first clause the comment above names, and
    // separately from the three value-shape clauses below because it is the only one that is not a property
    // of a value at all: both values here would be forwarded untouched on their own.

    it('replaces a repeated header even though either value alone would be kept', () => {
      const forwarded = runInterceptor(
        requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID),
      ).forwarded[0];

      expect(identifierOn(runInterceptor(requestCarrying(CALLER_SUPPLIED_ID)).forwarded[0]))
        .toBe(CALLER_SUPPLIED_ID);
      expect(identifierOn(runInterceptor(requestCarrying(SECOND_CALLER_SUPPLIED_ID)).forwarded[0]))
        .toBe(SECOND_CALLER_SUPPLIED_ID);

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('collapses the two lines into exactly one', () => {
      const forwarded = runInterceptor(
        requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID),
      ).forwarded[0];

      // The premise: the request really did arrive with two lines. Without this the assertion below would
      // also be satisfied by a helper that had quietly collapsed them before the interceptor ever ran.
      expect(
        allIdentifiersOn(requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID)),
      ).toEqual([CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID]);

      expect(allIdentifiersOn(forwarded)?.length).toBe(1);
    });

    it('forwards neither inbound value, and leaves the caller\'s request untouched', () => {
      const original = requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID);
      const forwarded = runInterceptor(original).forwarded[0];

      // Neither value survives.
      expect(allIdentifiersOn(forwarded)).not.toContain(CALLER_SUPPLIED_ID);
      expect(allIdentifiersOn(forwarded)).not.toContain(SECOND_CALLER_SUPPLIED_ID);

      expect(forwarded).not.toBe(original);
      expect(allIdentifiersOn(original)).toEqual([CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID]);
    });

    it('replaces an empty value rather than forwarding it', () => {
      const forwarded = runInterceptor(requestCarrying('')).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a value that is only whitespace', () => {
      const forwarded = runInterceptor(requestCarrying('   ')).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
      expect(identifierOn(runInterceptor(requestCarrying(` ${CALLER_SUPPLIED_ID} `)).forwarded[0]))
        .toMatch(CANONICAL_UUID_PATTERN);
    });

    it('accepts the hyphenated canonical form in upper case', () => {
      const forwarded = runInterceptor(requestCarrying(UPPER_CASE_HYPHENATED_ID)).forwarded[0];

      expect(identifierOn(forwarded)).toBe(UPPER_CASE_HYPHENATED_ID);
    });

    it('replaces a value one character short of the compact form', () => {
      const forwarded = runInterceptor(requestCarrying(ONE_CHARACTER_SHORT_ID)).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a value one character longer than the compact form', () => {
      const forwarded = runInterceptor(requestCarrying(ONE_CHARACTER_LONG_ID)).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a compact-length value carrying one non-hexadecimal character', () => {
      // `g` is the first letter past the hexadecimal alphabet, so this is the smallest
      // possible departure from an otherwise acceptable value.
      const forwarded = runInterceptor(
        requestCarrying('9f2c4d6e8a0b1c3d5e7f0a1b2c3d4e5g'),
      ).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a hyphenated-length value whose hyphens are out of position', () => {
      const forwarded = runInterceptor(
        requestCarrying('4d19ae7c-1b8f-4e2a-9d6c3f5b7a091e2d'),
      ).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a value that looks like a password, an address, a token or a key', () => {
      const secrets: readonly string[] = [
        'Integr8tion!Pass',
        'operator@contoso.example',
        'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl',
        'sk-live-9f2c4d6e8a0b1c3d5e7f',
      ];

      for (const secret of secrets) {
        const forwarded = runInterceptor(requestCarrying(secret)).forwarded[0];

        expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
        expect(identifierOn(forwarded)).not.toBe(secret);
      }
    });

    it('replaces a value containing a control character', () => {
      const withControl = `first${String.fromCharCode(0x1f)}second`;
      const forwarded = runInterceptor(requestCarrying(withControl)).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces a value containing a character above the printable range', () => {
      const forwarded = runInterceptor(requestCarrying('caf\u00e9')).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('replaces the header outright rather than appending a second value', () => {
      const forwarded = runInterceptor(requestCarrying('')).forwarded[0];

      expect(forwarded.headers.getAll(CORRELATION_ID_HEADER)?.length).toBe(1);
    });

    it('forwards a clone, leaving the caller\'s request untouched', () => {
      const original = requestCarrying('');
      const forwarded = runInterceptor(original).forwarded[0];

      expect(forwarded).not.toBe(original);
      expect(original.headers.get(CORRELATION_ID_HEADER)).toBe('');
    });
  });

  describe('a request that lacks the header', () => {
    it('forwards a clone rather than the original object', () => {
      const original = requestWithoutIdentifier();

      expect(runInterceptor(original).forwarded[0]).not.toBe(original);
    });

    it('forwards exactly one request', () => {
      expect(runInterceptor(requestWithoutIdentifier()).forwarded.length).toBe(1);
    });

    it('leaves the original request unstamped', () => {
      const original = requestWithoutIdentifier();

      runInterceptor(original);

      expect(original.headers.has(CORRELATION_ID_HEADER)).toBeFalse();
    });

    it('attaches a canonical version 4 UUID', () => {
      const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('attaches exactly one header and changes no other name', () => {
      const original = requestWithoutIdentifier();
      const forwarded = runInterceptor(original).forwarded[0];

      expect(forwarded.headers.keys().sort()).toEqual(
        [...original.headers.keys(), CORRELATION_ID_HEADER].sort(),
      );
    });

    it('carries the other header values across unchanged', () => {
      const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];

      expect([forwarded.headers.get(ACCEPT_HEADER), forwarded.headers.get(OTHER_HEADER)]).toEqual([
        ACCEPT_HEADER_VALUE,
        OTHER_HEADER_VALUE,
      ]);
    });

    it('carries the method and URL across unchanged', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest()).forwarded[0];

      expect([forwarded.method, forwarded.url]).toEqual(['PUT', PORTAL_LIST_URL]);
    });

    it('carries the body across by identity', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest()).forwarded[0];

      // Identity rather than equality: the body must not be re-serialised, copied
      // or otherwise touched on the way through.
      expect(forwarded.body).toBe(REQUEST_BODY);
    });

    it('carries the query parameters across unchanged', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest()).forwarded[0];

      expect(forwarded.params.get(QUERY_PARAMETER_NAME)).toBe(QUERY_PARAMETER_VALUE);
    });

    it('carries the request context across unchanged', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest()).forwarded[0];

      // Downstream interceptors read per-request switches out of the context, so a
      // clone that dropped it would break them without breaking anything here.
      expect(forwarded.context.get(DIAGNOSTIC_CONTEXT)).toBe(CARRIED_CONTEXT_VALUE);
    });

    it('carries the remaining request options across unchanged', () => {
      const forwarded = runInterceptor(fullyPopulatedRequest()).forwarded[0];

      expect([forwarded.responseType, forwarded.withCredentials, forwarded.reportProgress]).toEqual([
        'json',
        true,
        true,
      ]);
    });

    it('returns the downstream observable untouched', () => {
      const interception = runInterceptor(requestWithoutIdentifier());

      expect(interception.events).toEqual([TERMINAL_RESPONSE]);
    });

    it('lets the downstream observable complete', () => {
      const interception = runInterceptor(requestWithoutIdentifier());

      expect([interception.completed, interception.errored]).toEqual([true, false]);
    });
  });

  describe('identifier generation - crypto.randomUUID, the preferred source', () => {
    it('uses the primitive when the platform exposes it', () => {
      spyOn(crypto, 'randomUUID').and.returnValue(STUBBED_RANDOM_UUID);

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        STUBBED_RANDOM_UUID,
      );
    });

    it('calls it exactly once per stamped request', () => {
      const randomUUID = spyOn(crypto, 'randomUUID').and.returnValue(STUBBED_RANDOM_UUID);

      runInterceptor(requestWithoutIdentifier());

      expect(randomUUID).toHaveBeenCalledTimes(1);
    });

    it('does not fall through to the byte-filling source', () => {
      spyOn(crypto, 'randomUUID').and.returnValue(STUBBED_RANDOM_UUID);
      const getRandomValues = spyOn(crypto, 'getRandomValues').and.callThrough();

      runInterceptor(requestWithoutIdentifier());

      expect(getRandomValues).not.toHaveBeenCalled();
    });

    it('does not fall through to the last-resort source', () => {
      spyOn(crypto, 'randomUUID').and.returnValue(STUBBED_RANDOM_UUID);
      const random = spyOn(Math, 'random').and.returnValue(CONSTANT_RANDOM_FRACTION);

      runInterceptor(requestWithoutIdentifier());

      expect(random).not.toHaveBeenCalled();
    });

    it('produces a canonical identifier from the real primitive', () => {
      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toMatch(
        CANONICAL_UUID_PATTERN,
      );
    });
  });

  describe('identifier generation - crypto.getRandomValues, the fallback that earns its place', () => {
    beforeEach(() => {
      shadowAsAbsent(crypto, 'randomUUID');
    });

    it('takes this path once the preferred primitive is absent', () => {
      const getRandomValues = spyOn(crypto, 'getRandomValues').and.callThrough();

      runInterceptor(requestWithoutIdentifier());

      expect(getRandomValues).toHaveBeenCalledTimes(1);
    });

    it('formats ascending bytes into the identifier worked out by hand', () => {
      spyOn(crypto, 'getRandomValues').and.callFake(fillingWith((index) => index));

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        ASCENDING_BYTES_UUID,
      );
    });

    it('forces the version and variant nibbles even when every bit is clear', () => {
      spyOn(crypto, 'getRandomValues').and.callFake(fillingWith(() => 0x00));

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        ALL_BITS_CLEAR_UUID,
      );
    });

    it('forces the version and variant nibbles even when every bit is set', () => {
      spyOn(crypto, 'getRandomValues').and.callFake(fillingWith(() => 0xff));

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        ALL_BITS_SET_UUID,
      );
    });

    it('asks the platform for exactly one byte per UUID position', () => {
      const observedLengths: number[] = [];
      spyOn(crypto, 'getRandomValues').and.callFake(
        fillingWith((index) => index, (length) => observedLengths.push(length)),
      );

      runInterceptor(requestWithoutIdentifier());

      expect(observedLengths).toEqual([UUID_BYTE_LENGTH]);
    });

    it('asks for those bytes in a Uint8Array', () => {
      const getRandomValues = spyOn(crypto, 'getRandomValues').and.callFake(
        fillingWith((index) => index),
      );

      runInterceptor(requestWithoutIdentifier());

      expect(getRandomValues.calls.mostRecent().args[0]).toBeInstanceOf(Uint8Array);
    });

    it('does not fall through to the last-resort source', () => {
      const random = spyOn(Math, 'random').and.returnValue(CONSTANT_RANDOM_FRACTION);

      runInterceptor(requestWithoutIdentifier());

      expect(random).not.toHaveBeenCalled();
    });

    it('produces a canonical identifier from real platform randomness', () => {
      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toMatch(
        CANONICAL_UUID_PATTERN,
      );
    });

    it('still produces distinct identifiers on this path', () => {
      const identifiers = new Set<string>();

      for (let attempt = 0; attempt < UNIQUENESS_SAMPLE_SIZE; attempt += 1) {
        const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];
        identifiers.add(identifierOn(forwarded) ?? MISSING_IDENTIFIER);
      }

      expect(identifiers.size).toBe(UNIQUENESS_SAMPLE_SIZE);
    });
  });

  describe('identifier generation - Math.random, the last resort', () => {
    beforeEach(() => {
      shadowAsAbsent(crypto, 'randomUUID');
      shadowAsAbsent(crypto, 'getRandomValues');
    });

    it('formats a constant fraction into the identifier worked out by hand', () => {
      spyOn(Math, 'random').and.returnValue(CONSTANT_RANDOM_FRACTION);

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        CONSTANT_FRACTION_UUID,
      );
    });

    it('draws exactly one value per UUID byte', () => {
      const random = spyOn(Math, 'random').and.returnValue(CONSTANT_RANDOM_FRACTION);

      runInterceptor(requestWithoutIdentifier());

      expect(random).toHaveBeenCalledTimes(UUID_BYTE_LENGTH);
    });

    it('keeps the largest possible fraction inside byte range', () => {
      // `Math.random()` is specified as `[0, 1)`, and `1 - Number.EPSILON / 2` is exactly the largest
      // double below 1. Scaling it by 256 yields the largest double below 256, which floors to 255 - so the
      // top of the generator's range maps onto the top of the byte range with nothing left over.
      spyOn(Math, 'random').and.returnValue(1 - Number.EPSILON / 2);

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        ALL_BITS_SET_UUID,
      );
    });

    it('renders a zero fraction as the all-bits-clear identifier', () => {
      spyOn(Math, 'random').and.returnValue(0);

      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toBe(
        ALL_BITS_CLEAR_UUID,
      );
    });

    it('produces a canonical identifier from the real generator', () => {
      expect(identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0])).toMatch(
        CANONICAL_UUID_PATTERN,
      );
    });

    it('still produces distinct identifiers on this path', () => {
      const identifiers = new Set<string>();

      for (let attempt = 0; attempt < UNIQUENESS_SAMPLE_SIZE; attempt += 1) {
        const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];
        identifiers.add(identifierOn(forwarded) ?? MISSING_IDENTIFIER);
      }

      // The interceptor's own header concedes that this source is weaker than the other two and explains
      // why that is acceptable for a diagnostic label. What it must still deliver is that two requests do
      // not collide, which is what this observes.
      expect(identifiers.size).toBe(UNIQUENESS_SAMPLE_SIZE);
    });
  });

  describe('uniqueness across requests', () => {
    it('gives every stamped request its own identifier', () => {
      const identifiers = new Set<string>();

      for (let attempt = 0; attempt < UNIQUENESS_SAMPLE_SIZE; attempt += 1) {
        const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];
        identifiers.add(identifierOn(forwarded) ?? MISSING_IDENTIFIER);
      }

      expect(identifiers.size).toBe(UNIQUENESS_SAMPLE_SIZE);
    });

    it('never omits the identifier across that sample', () => {
      const identifiers = new Set<string>();

      for (let attempt = 0; attempt < UNIQUENESS_SAMPLE_SIZE; attempt += 1) {
        const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];
        identifiers.add(identifierOn(forwarded) ?? MISSING_IDENTIFIER);
      }

      expect(identifiers.has(MISSING_IDENTIFIER)).toBeFalse();
    });

    it('produces a canonical identifier every time across that sample', () => {
      const malformed: string[] = [];

      for (let attempt = 0; attempt < UNIQUENESS_SAMPLE_SIZE; attempt += 1) {
        const identifier = identifierOn(runInterceptor(requestWithoutIdentifier()).forwarded[0]);

        if (identifier === null || !CANONICAL_UUID_PATTERN.test(identifier)) {
          malformed.push(identifier ?? MISSING_IDENTIFIER);
        }
      }

      expect(malformed).toEqual([]);
    });
  });

  describe('what it deliberately does not do', () => {
    it('logs nothing', () => {
      // The interceptor's header states that a log call here would duplicate what the server already
      // records against the same identifier.
      const consoleSpies = [
        spyOn(console, 'debug'),
        spyOn(console, 'error'),
        spyOn(console, 'info'),
        spyOn(console, 'log'),
        spyOn(console, 'warn'),
      ];

      runInterceptor(requestWithoutIdentifier());
      runInterceptor(fullyPopulatedRequest(CORRELATION_ID_HEADER));

      expect(consoleSpies.map((spy) => spy.calls.count())).toEqual([0, 0, 0, 0, 0]);
    });

    it('adds no authorisation header of its own', () => {
      const forwarded = runInterceptor(requestWithoutIdentifier()).forwarded[0];

      // Attaching credentials is the auth interceptor's job. This one is registered ahead of it precisely
      // so that the identifier exists before any credential handling happens, and it must not stray into
      // that territory.
      expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBeFalse();
    });

    it('reads no response and rewrites no event', () => {
      const interception = runInterceptor(requestWithoutIdentifier());

      expect(interception.events).toEqual([TERMINAL_RESPONSE]);
    });

    it('needs no injection context, matching its no-dependency claim', () => {
      // Every direct invocation in this suite already relies on this; stating it once as an expectation
      // makes the reliance deliberate. An interceptor that called `inject()` outside an injection context
      // would throw NG0203 here.
      expect(() => runInterceptor(requestWithoutIdentifier())).not.toThrow();
    });
  });

  describe('global state restoration', () => {
    it('restores crypto.randomUUID to exactly the descriptor it found', () => {
      const descriptorBefore = Object.getOwnPropertyDescriptor(crypto, 'randomUUID');
      const valueBefore = crypto.randomUUID;

      shadowAsAbsent(crypto, 'randomUUID');
      restoreShadowedProperties();

      expect(Object.getOwnPropertyDescriptor(crypto, 'randomUUID')).toEqual(descriptorBefore);
      expect(crypto.randomUUID).toBe(valueBefore);
    });

    it('restores crypto.getRandomValues to exactly the descriptor it found', () => {
      const descriptorBefore = Object.getOwnPropertyDescriptor(crypto, 'getRandomValues');
      const valueBefore = crypto.getRandomValues;

      shadowAsAbsent(crypto, 'getRandomValues');
      restoreShadowedProperties();

      expect(Object.getOwnPropertyDescriptor(crypto, 'getRandomValues')).toEqual(descriptorBefore);
      expect(crypto.getRandomValues).toBe(valueBefore);
    });

    it('really does hide the primitive while the shim stands', () => {
      shadowAsAbsent(crypto, 'randomUUID');

      // Without this the two restoration specs above would pass even if
      // `shadowAsAbsent` did nothing at all.
      expect(crypto.randomUUID).toBeUndefined();
    });

    it('unwinds both shims when both are installed', () => {
      shadowAsAbsent(crypto, 'randomUUID');
      shadowAsAbsent(crypto, 'getRandomValues');

      restoreShadowedProperties();

      expect([typeof crypto.randomUUID, typeof crypto.getRandomValues]).toEqual([
        'function',
        'function',
      ]);
    });

    it('empties its shim register, so nothing leaks into the next spec', () => {
      shadowAsAbsent(crypto, 'randomUUID');

      restoreShadowedProperties();

      expect(installedShims).toEqual([]);
    });

    it('starts every spec with an unstubbed Math.random', () => {
      // Jasmine unwinds `spyOn` itself; observing that here means a spec that stubbed `Math.random` cannot
      // silently poison a later one under randomised order.
      expect(jasmine.isSpy(Math.random)).toBeFalse();
    });

    it('starts every spec with an unstubbed crypto.randomUUID', () => {
      expect(jasmine.isSpy(crypto.randomUUID)).toBeFalse();
    });

    it('starts every spec with an unstubbed crypto.getRandomValues', () => {
      expect(jasmine.isSpy(crypto.getRandomValues)).toBeFalse();
    });

    it('starts every spec with an empty shim register', () => {
      expect(installedShims).toEqual([]);
    });
  });
});
