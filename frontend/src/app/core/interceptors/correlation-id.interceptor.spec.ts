//
// Specification for `correlationIdInterceptor` - the outermost functional HTTP
// interceptor of the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// THERE IS NO PREDECESSOR SUITE, AND NO PREDECESSOR BEHAVIOUR EITHER
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// tests of any kind, so there is no assertion to port. It also carried no
// request-scoped identifier of any kind: the interceptor's own source records that
// a case-insensitive search for `correlation`, `x-request-id` and `requestid`
// across the five in-scope `Library/Components` domain trees, `Website/admin` and
// the legacy web configuration matched zero files. Every expectation below is
// therefore net-new coverage of net-new behaviour - nothing here is a translation,
// and no legacy outcome changes by adding it.
//
// ---------------------------------------------------------------------------
// WHY THIS SUITE EXISTS AT ALL
// ---------------------------------------------------------------------------
// The interceptor is the only participant in the correlation loop that lives in
// the browser. If it silently stops attaching the header, nothing breaks
// visibly - requests still succeed, screens still render - and the loss shows up
// only later as server log lines that cannot be joined to the browser-side
// observation that provoked them. That failure mode is invisible to every other
// spec in this workspace, which is precisely why the behaviour is pinned here.
//
// ---------------------------------------------------------------------------
// TWO COMPLEMENTARY STYLES OF EXERCISE, AND WHY BOTH ARE NEEDED
// ---------------------------------------------------------------------------
// 1. Through the real `HttpClient`, with the interceptor registered exactly as
//    `app.config.ts` registers it and the testing backend standing in for the
//    network. This is the only way to prove the *registration* works - that
//    `withInterceptors([...])` reaches an ordinary `HttpClient` call - and it is
//    the only way to observe chain ordering against a second interceptor.
// 2. By calling the exported function directly with a hand-built request and a
//    hand-built `next`. This is the only way to observe the request object that
//    was handed downstream *by identity*, which is the whole substance of the
//    "a caller-supplied header is never overwritten" contract: the interceptor
//    must forward the original object, not an equal-looking clone of it.
//
// Direct invocation is legitimate here rather than a shortcut. `HttpInterceptorFn`
// is allowed to call `inject()`, and one that did would have to be invoked inside
// an injection context; this one declares in its own header that it "takes no
// dependency injection", and its source confirms it - there is no `inject` call
// anywhere in the module. Calling it as a plain function both relies on and
// asserts that property, and it keeps the generation-path specs hermetic: the
// only code running between installing a `Math.random` stub and counting its
// calls is the interceptor itself.
//
// ---------------------------------------------------------------------------
// EVERY URL BELOW IS RELATIVE, AND THAT IS A CONTRACT RATHER THAN A HABIT
// ---------------------------------------------------------------------------
// Every request issued and every `expectOne` matcher used in this file addresses a
// RELATIVE path beginning `/api/v1/`. No absolute host appears anywhere, and none
// may be introduced.
//
// The reason is that specs see the PRODUCTION environment. `angular.json` declares
// no `configurations` block at all on the `test` target, so that target has no
// `fileReplacements`; the module that reaches a spec is therefore
// `src/environments/environment.ts`, which is itself the production file
// (`production: true`, `apiBaseUrl: '/api/v1'`). The replacements run the other way
// round from the usual arrangement - the `production` build configuration lists
// none, while `development` is the one that swaps in `environment.development.ts` -
// so a spec written against an absolute development host would be asserting a base
// URL that this compilation never sees.
//
// The relative base is also load-bearing at run time, and its failure mode is
// invisible to every compiler and every unit test. `docker/nginx.conf` proxies
// `/api/` through to the API container, so the browser reaches the API through the
// very origin that served the application. An absolute value naming the API
// service by its compose service name would resolve only from inside the Docker
// network - that hostname does not resolve in a browser at all - and would
// additionally turn every call into a cross-origin request subject to the API's
// CORS policy. Both containers would still build and both would still report
// healthy, and the end-to-end validation gate would fail anyway. Pinning the
// relative shape here is what keeps that from reaching a container image.
//
// ---------------------------------------------------------------------------
// THE OTHER HALF OF THE LOOP IS SERVER-SIDE AND CANNOT BE ASSERTED HERE
// ---------------------------------------------------------------------------
// Recorded rather than tested, because no client-side assertion can reach it. The
// API's `CorrelationIdMiddleware` consumes this header, keeps the inbound value
// when it passes the same four clauses the interceptor validates against, and
// pushes it onto the server's structured logging scope - which is the join between
// a browser-side observation and the server log lines for the same request.
//
// On a failure the value also commonly surfaces in the response body:
// `GlobalExceptionHandler` writes an RFC 7807 `ProblemDetails` whose `traceId` is
// derived as `Activity.Current?.Id ?? httpContext?.TraceIdentifier`. That is why
// `traceId` matching this header is the common case rather than a guarantee - an
// ambient trace, when one is running, wins over the request identifier the
// middleware aligns to this value.
//
// On a SUCCESS there is no body-borne appearance at all: `Dtos/Common/ApiMeta.cs`
// deliberately declares no `CorrelationId`, `TraceId` or `RequestId` member, so the
// response header is the only carrier. That asymmetry is the reason the specs below
// pin the REQUEST header so precisely: it is the one end of the loop this
// application controls and the one a regression here would silently sever.
//
// ---------------------------------------------------------------------------
// SPEC ORDER IS RANDOMISED
// ---------------------------------------------------------------------------
// `karma.conf.js` deliberately leaves Jasmine's `random` at its default of `true`.
// Every spec below is therefore written to be order-independent: each one installs
// whatever global stubbing it needs, and every stub is removed again before the
// next spec runs. Jasmine restores `spyOn` automatically; the own-property shims
// used to make a platform primitive *absent* cannot be expressed as spies, so they
// are tracked explicitly and unwound by a suite-wide `afterEach`. The
// `global state restoration` block below proves that unwinding actually works
// rather than assuming it.
//

import { HttpClient, HttpContext, HttpContextToken, HttpHeaders, HttpParams, HttpRequest, HttpResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import type { HttpEvent, HttpHandlerFn, HttpInterceptorFn } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { correlationIdInterceptor } from './correlation-id.interceptor';

/**
 * The header the interceptor attaches.
 *
 * Restated here rather than imported: the interceptor keeps its
 * `CORRELATION_ID_HEADER` constant module-private, and widening its exported
 * surface merely to be observable from a spec would be the wrong trade. Restating
 * the literal means a change to the spelling has to be made in both places, which
 * is the intent - an accidental change fails these specs loudly instead of
 * breaking the loop silently, each side looking for a header the other never
 * sends. The identical literal is declared server-side by the API's
 * correlation-id middleware.
 */
const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/**
 * The same header name in lower case.
 *
 * HTTP compares header names case-insensitively and `HttpHeaders` normalises them
 * accordingly, so a caller who supplies this spelling must be recognised as having
 * already supplied the header. That is asserted rather than assumed, because the
 * interceptor's short-circuit depends entirely on `HttpHeaders.has` performing
 * that normalisation.
 */
const LOWER_CASE_CORRELATION_ID_HEADER = 'x-correlation-id';

/**
 * The number of random bytes a version 4 UUID is built from, mirroring the
 * interceptor's own `UUID_BYTE_LENGTH`.
 */
const UUID_BYTE_LENGTH = 16;

/**
 * The canonical RFC 4122 version 4 shape: 36 characters, lower-case hexadecimal,
 * the version nibble fixed at `4` and the variant nibble one of `8`, `9`, `a`, `b`.
 *
 * Anchored at both ends so a value with leading or trailing content fails. The
 * interceptor documents that all three of its generation sources must be
 * indistinguishable in shape from one another, so this single pattern is applied
 * to every path.
 */
const CANONICAL_UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** A collection endpoint, matching the `/api/v1` prefix the API exposes. */
const PORTAL_LIST_URL = '/api/v1/portals';

/**
 * A second endpoint, used where two distinct requests must be told apart.
 *
 * The canonical role collection is flat; its tenant is resolved from the request host.
 */
const ROLE_LIST_URL = '/api/v1/roles';

/**
 * The identifier of the portal a delete spec addresses.
 *
 * `0` rather than `1`, because the legacy `Portals.PortalID` column is declared
 * `IDENTITY(-1,1)`: the seed and first generated value is `-1`, while the shipped default
 * portal row is inserted explicitly with `PortalID` `0`, so both are legitimate row
 * identifiers - and `-1` is simultaneously the legacy `Null.NullInteger` sentinel. Nothing in
 * this interceptor interprets the value - it is a path segment here and no more -
 * but choosing a realistic one keeps the fixture honest about the schema this
 * migration maps onto.
 */
const DELETED_PORTAL_ID = 0;

/**
 * A caller-supplied identifier that is deliberately *not* UUID-shaped.
 *
 * The interceptor neither validates nor normalises an inbound value - it forwards
 * the request untouched - so a value that could not have been generated by any of
 * its three sources is the strongest available evidence that the value really was
 * preserved rather than regenerated and coincidentally matched.
 */
const CALLER_SUPPLIED_ID = 'retry-of-a-prior-attempt';

/**
 * A second caller-supplied identifier, used only to build a request whose
 * correlation header arrives on **two** header lines.
 *
 * It is deliberately as acceptable as {@link CALLER_SUPPLIED_ID} on its own merits -
 * non-blank, well inside the length bound and printable US-ASCII throughout - and
 * that is the whole point. The single-line clause is the only reason a request
 * carrying both must be replaced, so a specification built from two individually
 * *valid* values fails the moment that clause is weakened, whereas one built from
 * invalid values would keep passing on the strength of a different clause entirely.
 */
const SECOND_CALLER_SUPPLIED_ID = 'a-concurrent-and-unrelated-attempt';

/** An unrelated header, used to prove that everything else is carried across. */
const OTHER_HEADER = 'X-Other-Header';

/** The value of {@link OTHER_HEADER}, expected back byte-identical. */
const OTHER_HEADER_VALUE = 'preserved verbatim';

/** A second unrelated header, so the "everything else" claim covers more than one. */
const ACCEPT_HEADER = 'Accept';

/** The value of {@link ACCEPT_HEADER}. */
const ACCEPT_HEADER_VALUE = 'application/json';

/**
 * The credential header the next interceptor in the real chain attaches.
 *
 * Named here so the ordering specs can assert against the same spelling the
 * negative spec uses, and so that "the identifier is stamped before credentials are
 * attached" is expressed once rather than as a scattered string literal.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * An obviously synthetic credential for the ordering probe to attach.
 *
 * Deliberately unmistakable as a placeholder. It is not a token, is not derived
 * from one, authorises nothing and matches no real credential format - the probe
 * only needs *some* value to prove a header was added after the identifier, and a
 * value that could be mistaken for a live credential has no place in a fixture.
 * Nothing in this suite reads, decodes or transmits it beyond the in-memory
 * testing backend.
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

/**
 * The value a stubbed `crypto.randomUUID` returns.
 *
 * Typed as the template literal shape `lib.dom` declares for `randomUUID` so the
 * stub is assignable without a cast. The value is canonical - version nibble `4`,
 * variant nibble `8` - yet obviously synthetic, so seeing it arrive verbatim in the
 * header proves the primitive's output is used as-is and is not re-formatted.
 */
const STUBBED_RANDOM_UUID: `${string}-${string}-${string}-${string}-${string}` =
  '11111111-2222-4333-8444-555555555555';

/**
 * The exact identifier produced when `crypto.getRandomValues` fills the 16 byte
 * buffer with the ascending sequence `0x00 .. 0x0f`.
 *
 * Worked through by hand from the interceptor's own formatting rules, so the
 * assertion pins the byte-to-character mapping rather than merely the shape:
 *
 *   raw bytes  00 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f
 *   byte 6     (0x06 & 0x0f) | 0x40 = 0x46   <- version nibble forced to 4
 *   byte 8     (0x08 & 0x3f) | 0x80 = 0x88   <- variant bits forced to binary 10
 *   hex        000102030405460788090a0b0c0d0e0f
 *   grouped    00010203-0405-4607-8809-0a0b0c0d0e0f
 */
const ASCENDING_BYTES_UUID = '00010203-0405-4607-8809-0a0b0c0d0e0f';

/**
 * The exact identifier produced from 16 zero bytes.
 *
 * The only two non-zero nibbles in it are the ones the specification forces, which
 * is what makes this the sharpest available proof that the version and variant
 * rewriting happens at all rather than being inherited from lucky randomness.
 */
const ALL_BITS_CLEAR_UUID = '00000000-0000-4000-8000-000000000000';

/**
 * The exact identifier produced from 16 `0xff` bytes.
 *
 * The mirror image of {@link ALL_BITS_CLEAR_UUID}: here the forced nibbles are the
 * only two that are *not* `f`, proving the masks clear the bits they must clear
 * (`0x4f` keeps the low nibble, `0xbf` keeps the low six bits).
 */
const ALL_BITS_SET_UUID = 'ffffffff-ffff-4fff-bfff-ffffffffffff';

/**
 * The constant `Math.random` result used to make the last-resort path
 * deterministic. `Math.floor(0.5 * 256)` is `0x80`, so every one of the 16 bytes
 * arrives as `0x80` before the version and variant rewriting.
 */
const CONSTANT_RANDOM_FRACTION = 0.5;

/**
 * The exact identifier produced when every byte is `0x80`.
 *
 *   byte 6  (0x80 & 0x0f) | 0x40 = 0x40
 *   byte 8  (0x80 & 0x3f) | 0x80 = 0x80
 *   hex     80808080808040808080808080808080
 *   grouped 80808080-8080-4080-8080-808080808080
 */
const CONSTANT_FRACTION_UUID = '80808080-8080-4080-8080-808080808080';

/**
 * How many identifiers a uniqueness spec draws.
 *
 * Large enough that a stuck or memoised generator is caught immediately, small
 * enough that the spec stays instantaneous. Nothing statistical is being claimed
 * here: the interceptor's contract is that consecutive requests get *distinct*
 * identifiers, and this observes exactly that.
 */
const UNIQUENESS_SAMPLE_SIZE = 128;

/**
 * The sentinel recorded in place of a missing identifier, so a uniqueness spec
 * distinguishes "128 distinct values" from "127 distinct values plus one request
 * that was never stamped".
 */
const MISSING_IDENTIFIER = '<no identifier was attached>';

/**
 * The response a stub `next` hands back.
 *
 * Compared by identity in the forwarding specs, which is what proves the
 * interceptor returns the downstream observable untouched rather than wrapping,
 * re-emitting or replacing the response.
 */
const TERMINAL_RESPONSE = new HttpResponse<unknown>({ status: 204, statusText: 'No Content' });

/** The default of {@link DIAGNOSTIC_CONTEXT}, which no spec should ever observe. */
const CONTEXT_DEFAULT = 'the token default, which means the context was lost';

/** The value stored in the request context, expected to survive the header clone. */
const CARRIED_CONTEXT_VALUE = 'carried across the clone';

/**
 * An `HttpContext` token, used to prove that cloning for the header does not drop
 * the request context. Downstream interceptors carry per-request switches in the
 * context, so losing it would break them silently.
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
 * Invokes the interceptor directly with a stub `next` and records everything
 * observable about the call.
 *
 * The stub returns a synchronous `of(...)`, so the returned observable completes
 * before this function does and the recorded flags are final by the time the
 * caller asserts on them - no `fakeAsync`, no `tick`, no scheduler involved.
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
 * Builds a `GET` request that carries two unrelated headers and no correlation
 * identifier - the ordinary case the interceptor exists to handle.
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
 * Builds a fully populated `PUT` request: body, query parameters, request context,
 * response type and credentials flag all set to non-default values, so a clone
 * that dropped any one of them is detectable.
 *
 * @param headerName The spelling under which the correlation identifier is
 * supplied, or `undefined` to omit it entirely.
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
 * Builds a request carrying an arbitrary inbound identifier.
 *
 * `HttpHeaders` is constructed from an object literal rather than by cloning,
 * because a value that the interceptor is expected to REJECT must reach it exactly
 * as the caller wrote it — untrimmed, unnormalised and of whatever length the test
 * chose.
 *
 * @param value The value to place in the correlation header.
 * @returns A GET request carrying that value and nothing else of interest.
 */
function requestCarrying(value: string): HttpRequest<unknown> {
  // Set through `clone` rather than through the `HttpHeaders` constructor: the
  // constructor initialises lazily from an object literal, while `set` stores the
  // value eagerly and verbatim. For a value chosen precisely because it is
  // degenerate — empty, over-long, or carrying a control character — the eager path
  // is the one that provably preserves it.
  return new HttpRequest<unknown>('GET', PORTAL_LIST_URL).clone({
    setHeaders: { [CORRELATION_ID_HEADER]: value },
  });
}

/**
 * Builds a request whose correlation header arrives on **two** header lines.
 *
 * `append` is the only way to reach this state, and reaching it is the entire
 * reason this helper exists rather than reusing {@link requestCarrying}. Every
 * other route collapses the two values into one line and so cannot exercise the
 * clause under test: an object literal keyed by header name can hold one value per
 * key, and `setHeaders` - which is what `requestCarrying` uses - *replaces* the
 * named header rather than adding to it. Only `append` produces the two-element
 * array that `getAll` returns and that the interceptor's single-line clause
 * inspects.
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

/**
 * The longest inbound identifier the interceptor accepts, and the shortest it
 * refuses.
 *
 * Both are derived from one bound so the pair cannot drift apart, and the bound
 * itself is restated from `correlation-id.interceptor.ts` rather than imported,
 * because the interceptor deliberately keeps it private. Restating it is what makes
 * this specification fail if the two ever disagree — which is the point, since the
 * bound exists to mirror `CorrelationIdMiddleware` on the API side.
 */
const MAX_ACCEPTED_IDENTIFIER_LENGTH = 128;

/**
 * Builds a `crypto.getRandomValues` replacement that fills the buffer
 * deterministically.
 *
 * Written as a function declaration returning a generic function declaration
 * rather than as nested arrows, so the generic signature `lib.dom` declares -
 * `<T extends ArrayBufferView | null>(array: T): T` - is reproduced exactly and
 * the stub is assignable to the spy without a cast. The real primitive fills in
 * place and returns the same object; so does this.
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
   * The own descriptor found before shadowing, or `undefined` when the property
   * was inherited from a prototype and the object had no own descriptor at all.
   */
  readonly original: PropertyDescriptor | undefined;
}

/**
 * Every shim installed by the currently running spec. Emptied by the suite-wide
 * `afterEach`, which is what keeps randomised spec order safe.
 */
const installedShims: OwnPropertyShim[] = [];

/**
 * Makes an inherited platform primitive look absent to the code under test.
 *
 * The interceptor decides between its three generation sources with
 * `typeof crypto.randomUUID === 'function'`, and a Jasmine spy is itself a
 * function - so `spyOn` cannot express "not present". `crypto.randomUUID` and
 * `crypto.getRandomValues` live on `Crypto.prototype` rather than on the `crypto`
 * instance, so defining an own property whose value is `undefined` shadows the
 * inherited one for exactly as long as the shim stands.
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

/**
 * Removes every shim installed by {@link shadowAsAbsent}, most recent first.
 *
 * Where there was no own descriptor to begin with the shim is *deleted* rather
 * than overwritten, because assigning the prototype's function back onto the
 * instance would leave the object permanently altered - own where it used to be
 * inherited - and that residue would leak into every later spec.
 */
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
  // Suite-wide safety net. Jasmine unwinds `spyOn` on its own; the own-property
  // shims cannot be spies, so they are unwound here for every spec whether or not
  // that spec installed one. Randomised order makes this mandatory rather than
  // tidy: a shim surviving one spec would silently change which generation path a
  // later spec exercises.
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
      // The shadowing technique below relies on both living on `Crypto.prototype`.
      // If a future platform or polyfill moved them onto the instance the shims
      // would still restore correctly - `shadowAsAbsent` snapshots whatever it
      // finds - but the reader should be told which arrangement is in force.
      expect([
        Object.getOwnPropertyDescriptor(crypto, 'randomUUID'),
        Object.getOwnPropertyDescriptor(crypto, 'getRandomValues'),
      ]).toEqual([undefined, undefined]);
    });
  });

  describe('registration through withInterceptors', () => {
    let httpClient: HttpClient;
    let httpMock: HttpTestingController;

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          // The real client is provided first and the testing backend second, which
          // is the order Angular requires: `provideHttpClientTesting` replaces the
          // backend `provideHttpClient` installed, and a reversed order leaves the
          // live backend in place and sends the request at the network.
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

      // The identifier is attached on the way out, before any outcome is known, so
      // a failing request carries it exactly as a succeeding one does. That is what
      // lets a server-side error log line be joined to the call that provoked it.
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

      // The third verb completes the method-agnosticism claim. The interceptor
      // branches on the inbound header alone and never inspects the method, so a
      // regression that started keying off the verb - stamping only requests with a
      // body, say - would pass the GET and POST specs above and fail here.
      // A delete is also the request whose identifier matters most in a log: it is
      // the one whose effect cannot be re-read afterwards.
      expect(
        identifierOn(httpMock.expectOne(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).request),
      ).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('sends that request under the method the caller chose', () => {
      httpClient.delete(`${PORTAL_LIST_URL}/${DELETED_PORTAL_ID}`).subscribe();

      // Stamping the header must not disturb the verb: the clone carries the method
      // across, and a delete that arrived as anything else would be a different
      // operation entirely.
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
    // WHY THIS BLOCK EXISTS, AND WHAT IT DOES *NOT* CLAIM.
    //
    // `withInterceptors([A, B, C])` composes as `A(next = B(next = C(next =
    // backend)))`. On the REQUEST path that means A runs first, so the correlation
    // identifier is attached before the auth interceptor can add `Authorization`
    // and before anything downstream can short-circuit, retry or fail the request.
    // That is the claim this block makes observable, and it is correct.
    //
    // D-I1. The same composition makes the RESPONSE path the exact reverse -
    // `backend -> C -> B -> A` - so the outermost interceptor is the LAST to see a
    // response, not the first. The migration plan's stated rationale for putting
    // error translation last, that doing so lets it "observe the final response
    // after any 401 refresh-and-retry", is therefore inverted with respect to the
    // ordering it prescribes: registered third, the error interceptor sees a
    // response BEFORE the two interceptors registered ahead of it do. The
    // prescribed ORDER is nonetheless right for an independent reason - the auth
    // interceptor is the one that swallows a recovered 401, and it can only do that
    // from inside, which is where being registered second puts it.
    //
    // None of that is this file's business to fix. This interceptor touches no
    // response at all - a property asserted directly under "what it deliberately
    // does not do" - so the response-path ordering has no observable consequence
    // here. The correction belongs to `auth.interceptor.ts` and
    // `error.interceptor.ts`, and is recorded here only so that a reader who
    // arrives via the plan's rationale is not misled by it.
    let httpClient: HttpClient;
    let httpMock: HttpTestingController;
    let identifierSeenByProbe: (string | null)[];
    let authorizationSeenByProbe: (string | null)[];

    beforeEach(() => {
      identifierSeenByProbe = [];
      authorizationSeenByProbe = [];

      // A stand-in for the auth interceptor, registered *after* the one under test
      // exactly as `app.config.ts` registers the real one. It records what it can
      // see the moment it runs and only then adds its own header, which is what
      // turns the ordering claim into two independent observations: the correlation
      // identifier is already present when credential handling begins, and the
      // credential header is not yet present when the identifier is stamped.
      //
      // Deliberately LOCAL and NOT exported. It is a probe, not production API, and
      // it must never be mistaken for one - the real credential handling lives in
      // `auth.interceptor.ts` and does considerably more than this.
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
      httpMock.expectOne(PORTAL_LIST_URL).flush(null);

      expect(identifierSeenByProbe).toEqual([jasmine.stringMatching(CANONICAL_UUID_PATTERN)]);
    });

    it('runs before the credential header is attached', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      httpMock.expectOne(PORTAL_LIST_URL).flush(null);

      // The other half of the same ordering claim. Asserting only that the probe saw
      // the identifier would be satisfied by either order if some later change also
      // stamped the identifier late; asserting that the probe had not yet added its
      // own header pins the direction unambiguously.
      expect(authorizationSeenByProbe).toEqual([null]);
    });

    it('hands the later interceptor the identifier that reaches the backend', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(null);

      expect(identifierSeenByProbe).toEqual([identifierOn(pending.request)]);
    });

    it('lets the later interceptor add its header without disturbing the identifier', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(null);

      // Both headers arrive together. This is the shape a real authenticated call
      // has on the wire, and it proves the two interceptors compose rather than
      // overwrite one another's work.
      expect([
        identifierOn(pending.request),
        pending.request.headers.get(AUTHORIZATION_HEADER),
      ]).toEqual([jasmine.stringMatching(CANONICAL_UUID_PATTERN), FAKE_BEARER_CREDENTIAL]);
    });

    it('gives the first attempt and a downstream re-send the same identifier', () => {
      httpClient.get(PORTAL_LIST_URL).subscribe();
      const pending = httpMock.expectOne(PORTAL_LIST_URL);
      pending.flush(null);

      // The mechanism the preservation contract exists to serve, observed end to
      // end. `auth.interceptor.ts` recovers a 401 by re-sending the request object
      // it was handed - one this interceptor has already stamped - so the retry
      // carries the first attempt's identifier rather than a second one. Feeding the
      // request the probe forwarded back through the interceptor reproduces exactly
      // that re-entry, and the identifier must survive it unchanged.
      const reSent = runInterceptor(pending.request).forwarded[0];

      expect(identifierOn(reSent)).toBe(identifierOn(pending.request));
    });
  });

  describe('a request that already carries the header', () => {
    // WHY PRESERVATION IS THE CONTRACT, AND WHICH CONCRETE MECHANISM DEPENDS ON IT.
    //
    // The consumer is `auth.interceptor.ts`. When the API answers 401 it renews the
    // session and retries ONCE, and it builds that retry by cloning the ORIGINAL
    // request object it was handed - the very object this interceptor, sitting
    // outside it, has already stamped - adding only a refreshed bearer token. The
    // retry therefore re-enters the chain carrying an identifier that is already
    // present.
    //
    // If this interceptor overwrote a value it found, the retry would be issued
    // under a second identifier and one logical operation - "the caller asked for
    // this resource, was challenged, and was served after renewal" - would be split
    // across two unrelated identifiers in the server logs. The operator reading
    // those logs would see an unexplained 401 and an unexplained success with
    // nothing tying them together, which is precisely the failure correlation exists
    // to prevent. Preserving the value is what makes both attempts joinable as one.
    //
    // Preservation is conditional on the value being one the API will actually
    // honour; the clauses, and why forwarding an unusable value would preserve
    // nothing at all, are exercised in the block that follows this one.
    it('forwards the very same request object rather than a clone', () => {
      const original = fullyPopulatedRequest(CORRELATION_ID_HEADER);

      // Identity, not equality. A retry must re-send byte-identical headers so the
      // server recognises the second attempt as the same logical operation; an
      // equal-looking clone would satisfy `toEqual` while still being a different
      // object, and the contract is explicit that the original is forwarded.
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

      // The preserved value is deliberately not UUID-shaped. The interceptor checks
      // that an inbound identifier is USABLE — one header line, non-blank, within
      // the length bound and printable US-ASCII throughout — and it does not check
      // that the value was minted here. A retry legitimately re-sends whatever the
      // first attempt carried, so a well-formed value of any shape is forwarded
      // unchanged; the rejection clauses are exercised in their own block below.
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
    // WHY THIS BLOCK EXISTS. "Never overwrite a caller-supplied value" and "always
    // send an identifier the API will keep" are in tension, and the interceptor
    // resolves it by validating the inbound value against the same four clauses
    // `CorrelationIdMiddleware` applies: exactly one header line, non-blank, within
    // the length bound, printable US-ASCII throughout. A value failing any clause is
    // REPLACED rather than forwarded, because forwarding it would mean the request
    // and its log entry carried different identifiers — the one failure mode
    // correlation exists to prevent. Each clause is asserted separately so a
    // regression names the clause it broke.

    // THE SINGLE-LINE CLAUSE. Asserted first because it is the first clause the
    // comment above names, and separately from the three value-shape clauses below
    // because it is the only one that is not a property of a value at all: both
    // values here would be forwarded untouched on their own. What disqualifies the
    // request is the ambiguity of there being two of them, and `getAll` returning a
    // two-element array is the only way that state is observable.
    //
    // Every other test in this block reaches the interceptor through a single header
    // line, so without these three the `inbound.length === 1` comparison could be
    // relaxed to `>= 1` - or deleted along with the surrounding `null` check - and the
    // whole suite would still pass while the browser and the server silently
    // disagreed about the identifier for the same request.

    it('replaces a repeated header even though either value alone would be kept', () => {
      const forwarded = runInterceptor(
        requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID),
      ).forwarded[0];

      // The premise, asserted rather than assumed: each value on its own takes the
      // pass-through branch. That is what makes the replacement below attributable to
      // the repetition and to nothing else about either value.
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

      // The premise: the request really did arrive with two lines. Without this the
      // assertion below would also be satisfied by a helper that had quietly
      // collapsed them before the interceptor ever ran.
      expect(
        allIdentifiersOn(requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID)),
      ).toEqual([CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID]);

      // `setHeaders` replaces rather than appends, so the ambiguity the server refuses
      // is resolved here rather than forwarded. Appending would have produced three
      // lines and left the request refusable for the very same reason.
      expect(allIdentifiersOn(forwarded)?.length).toBe(1);
    });

    it('forwards neither inbound value, and leaves the caller\'s request untouched', () => {
      const original = requestCarryingTwoIdentifiers(CALLER_SUPPLIED_ID, SECOND_CALLER_SUPPLIED_ID);
      const forwarded = runInterceptor(original).forwarded[0];

      // Neither value survives. Keeping the first would be the tempting "repair" - it
      // is a usable identifier, after all - but the server discards the whole header
      // when it arrives more than once, so forwarding either value would leave the
      // browser holding an identifier that appears in no server log line.
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

      // Blankness is measured on the trimmed value while the length bound is
      // measured on the value as received. The value itself is never trimmed, so a
      // usable identifier keeps whatever padding the caller sent.
      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('accepts a value exactly at the length bound', () => {
      const atTheBound = 'a'.repeat(MAX_ACCEPTED_IDENTIFIER_LENGTH);
      const forwarded = runInterceptor(requestCarrying(atTheBound)).forwarded[0];

      expect(identifierOn(forwarded)).toBe(atTheBound);
    });

    it('replaces a value one character past the length bound', () => {
      const pastTheBound = 'a'.repeat(MAX_ACCEPTED_IDENTIFIER_LENGTH + 1);
      const forwarded = runInterceptor(requestCarrying(pastTheBound)).forwarded[0];

      expect(identifierOn(forwarded)).toMatch(CANONICAL_UUID_PATTERN);
    });

    it('accepts the printable boundaries themselves', () => {
      // 0x20 (space) and 0x7e (tilde) are the lowest and highest accepted codes, so
      // an off-by-one in either comparison shows up here rather than nowhere.
      const boundaries = ' visible~';
      const forwarded = runInterceptor(requestCarrying(boundaries)).forwarded[0];

      expect(identifierOn(forwarded)).toBe(boundaries);
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

      // `setHeaders` replaces the named header, so exactly one value must survive. An
      // append would produce two header lines, which is itself one of the four
      // clauses the API refuses.
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

      // `HttpHeaders` is immutable and `clone` is the only mechanism used, so the
      // caller's object must be observably unchanged afterwards. A mutation here
      // would make a retry of the *original* object carry a stale identifier.
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
      // `randomUUID` is exposed only in secure contexts, and this application is
      // served over plain HTTP on a published container port - so on any origin
      // other than `localhost` this is the *real* production path, not a
      // hypothetical one. Making the primitive absent is therefore reproducing a
      // deployment, not contriving a branch.
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
      // `Math.random()` is specified as `[0, 1)`, and `1 - Number.EPSILON / 2` is
      // exactly the largest double below 1. Scaling it by 256 yields the largest
      // double below 256, which floors to 255 - so the top of the generator's range
      // maps onto the top of the byte range with nothing left over. Were the
      // scaling one step coarser the byte would reach 256, and the interceptor's
      // reliance on `Uint8Array` truncation to keep the value in range would become
      // load-bearing instead of incidental.
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

      // The interceptor's own header concedes that this source is weaker than the
      // other two and explains why that is acceptable for a diagnostic label. What
      // it must still deliver is that two requests do not collide, which is what
      // this observes.
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
      // The interceptor's header states that a log call here would duplicate what
      // the server already records against the same identifier. Console output from
      // a per-request interceptor would also be the single noisiest thing this
      // application could do, so the silence is asserted rather than trusted.
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

      // Attaching credentials is the auth interceptor's job. This one is registered
      // ahead of it precisely so that the identifier exists before any credential
      // handling happens, and it must not stray into that territory.
      expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBeFalse();
    });

    it('reads no response and rewrites no event', () => {
      const interception = runInterceptor(requestWithoutIdentifier());

      expect(interception.events).toEqual([TERMINAL_RESPONSE]);
    });

    it('needs no injection context, matching its no-dependency claim', () => {
      // Every direct invocation in this suite already relies on this; stating it
      // once as an expectation makes the reliance deliberate. An interceptor that
      // called `inject()` outside an injection context would throw NG0203 here.
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
      // Jasmine unwinds `spyOn` itself; observing that here means a spec that
      // stubbed `Math.random` cannot silently poison a later one under randomised
      // order.
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
