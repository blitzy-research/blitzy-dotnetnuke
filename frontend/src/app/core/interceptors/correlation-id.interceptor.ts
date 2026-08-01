import type { HttpInterceptorFn } from '@angular/common/http';

/**
 * Stamps every outbound request with a correlation identifier so that one
 * identifier spans the browser, the API and every log line either of them writes.
 *
 * ## Position in the chain
 *
 * `withInterceptors([A, B, C])` composes as `A(next = B(next = C(next =
 * backend)))`, so on the request path the order is `A -> B -> C -> backend`. This
 * interceptor is registered as `A`, the outermost one, and that placement is
 * behaviour rather than style: the identifier is attached before the auth
 * interceptor adds `Authorization` and before anything downstream can
 * short-circuit, retry or fail the request. Every request that leaves this
 * application therefore carries an identifier, including the ones that never
 * reach the network.
 *
 * ## The other half of the loop
 *
 * The API's correlation-id middleware reads this header, keeps the value when it
 * passes validation, and echoes it back on the response under the same header
 * name. It runs immediately after the global exception handler and immediately
 * before request logging, so the identifier reaches the log sink and the response
 * whatever the outcome - a success, a downstream `4xx`, or an RFC 7807
 * `ProblemDetails` body written by the exception handler.
 *
 * Two consequences worth knowing when reading logs:
 *
 * - The response header is the dependable carrier. It is registered before the
 *   server pipeline continues, so it survives every outcome.
 * - `ProblemDetails.traceId` carries this same value only sometimes. The server
 *   derives that member from the ambient trace identifier first and falls back to
 *   the request identifier - which the middleware aligns to this value - only when
 *   no ambient trace is running. So `traceId` matching this header is the common
 *   case, not a guarantee; the header is.
 *
 * The success-response envelope carries no correlation, trace or request member of
 * its own, so the header is the only place the identifier appears on a successful
 * response.
 *
 * ## A caller-supplied header is never overwritten
 *
 * A request that already carries the header is forwarded untouched. That is what
 * lets a retry re-send the original request and be recognised as the same logical
 * operation as the first attempt rather than as a second, unrelated one.
 *
 * ## What it deliberately does not do
 *
 * Nothing beyond attaching the header. It takes no dependency injection, reads no
 * token, touches no response, mutates no request body, and logs nothing - the
 * value it handles is diagnostic, and a log call here would duplicate what the
 * server already records against the same identifier.
 */

/**
 * The header that carries the correlation identifier.
 *
 * The spelling is contractual: the API's correlation-id middleware declares the
 * identical literal, and the cross-origin policy exposes that name to the
 * browser. HTTP compares header names case-insensitively, so the casing is
 * cosmetic, but the spelling is not - a different one would break the loop
 * silently, each side looking for a header the other never sends.
 */
const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/**
 * The number of random bytes a UUID is built from.
 */
const UUID_BYTE_LENGTH = 16;

/**
 * The number of distinct values a single byte can hold, used to scale a
 * `Math.random()` result into byte range.
 */
const BYTE_VALUE_COUNT = 256;

/**
 * Renders 16 bytes as a canonical RFC 4122 version 4 UUID string.
 *
 * The version and variant nibbles are overwritten in place so that the result is
 * indistinguishable in shape from a `crypto.randomUUID()` value: 36 characters of
 * lowercase hexadecimal and hyphens. Producing one shape rather than two matters
 * downstream, because a log query that recognises the identifier must not have to
 * know which code path below produced it.
 *
 * The output also satisfies every clause the server validates an inbound
 * identifier against - a single header line, non-blank, well inside the 128
 * character bound, and printable US-ASCII throughout - so a value generated here
 * is kept by the server rather than discarded and replaced.
 *
 * @param bytes Exactly {@link UUID_BYTE_LENGTH} bytes of randomness. Mutated in
 * place, which is safe because every caller passes a freshly allocated array.
 * @returns The canonical hyphenated representation.
 */
function formatAsUuidV4(bytes: Uint8Array): string {
  // Byte 6 high nibble carries the version (4); byte 8 high bits carry the
  // RFC 4122 variant (binary 10). Both are fixed by the specification.
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');

  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join('-');
}

/**
 * Produces a fresh correlation identifier.
 *
 * Three platform-native sources are tried in order of preference. No package
 * provides any of them: the workspace dependency surface is closed, and a
 * dedicated identifier library would add a runtime dependency for something the
 * platform already supplies.
 *
 * 1. `crypto.randomUUID()` - the direct primitive, preferred whenever present.
 * 2. `crypto.getRandomValues()` - the fallback that actually earns its place.
 *    `randomUUID` is exposed **only in secure contexts**, and this application is
 *    served over plain HTTP on a published container port, so on any origin other
 *    than `localhost` the first source is absent. Calling it unguarded there would
 *    throw on *every* request the application makes. `getRandomValues` carries no
 *    secure-context restriction, so it closes that gap with equal randomness
 *    quality.
 * 3. `Math.random()` - last resort for an environment offering neither. Its output
 *    comes from an ordinary pseudo-random generator, so its effective entropy is
 *    bounded by that generator's state rather than by the 122 random bit positions
 *    a version 4 UUID nominally carries. That weakening is acceptable here and
 *    only here: the value is a diagnostic label, never a secret, never a token and
 *    never used to authorise anything, so the sole requirement on it is that two
 *    concurrent requests are unlikely to collide - which this comfortably meets.
 *    Nothing in this application derives a security decision from it.
 *
 * @returns A 36 character canonical UUID string, unique per call.
 */
function newCorrelationId(): string {
  if (typeof crypto === 'object' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(UUID_BYTE_LENGTH);

  if (typeof crypto === 'object' && typeof crypto.getRandomValues === 'function') {
    crypto.getRandomValues(bytes);

    return formatAsUuidV4(bytes);
  }

  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = Math.floor(Math.random() * BYTE_VALUE_COUNT);
  }

  return formatAsUuidV4(bytes);
}

/**
 * Attaches {@link CORRELATION_ID_HEADER} to every outbound request that does not
 * already carry it.
 *
 * Registered first in the interceptor chain. See the file header for why that
 * position is load-bearing and for how the identifier travels back.
 */
export const correlationIdInterceptor: HttpInterceptorFn = (req, next) => {
  // MIGRATION: net-new cross-cutting concern with no legacy predecessor, and the
  // absence is measured rather than assumed. A case-insensitive search for
  // `correlation`, `x-request-id` and `requestid` across the five in-scope
  // `Library/Components` domain trees, `Website/admin` and the legacy web
  // configuration matched 0 files; widened to `correlation` across the entire
  // legacy `Library` and `Website` trees it still matched 0. The legacy request
  // pipeline - the Web Forms default page plus the 8 HTTP modules and 7 handlers
  // registered in that configuration - carried no request-scoped identifier of any
  // kind, so nothing here is a translation of prior behaviour and no legacy
  // outcome changes by adding it.

  // MIGRATION: this closes a loop the legacy application could not have closed.
  // The identifier is consumed by the API's correlation-id middleware, echoed on
  // the response under the same header, and pushed onto the server's structured
  // logging scope, which is what joins a browser-side observation to the server
  // log lines for the same request. The legacy stack had neither a machine-readable
  // error body to carry such a value nor structured logs to correlate. On an error
  // response the value also commonly surfaces as `ProblemDetails.traceId`; the
  // success envelope has no correlation, trace or request member at all, so the
  // header is the only carrier that is always present.

  // MIGRATION: a caller-supplied header is preserved rather than replaced, which is
  // a deliberate divergence from the simpler "always stamp" reading. Overwriting it
  // would give a retried request a second identifier and split one logical
  // operation across two identifiers in the logs - defeating the purpose of having
  // the identifier at all. The original request object is forwarded rather than
  // cloned, so a retry re-sends byte-identical headers.
  if (req.headers.has(CORRELATION_ID_HEADER)) {
    return next(req);
  }

  // `HttpHeaders` is immutable, so the header is applied by cloning. `setHeaders`
  // adds this one name and copies everything else across untouched; the method,
  // URL, params and body are carried over unchanged.
  const stamped = req.clone({
    setHeaders: { [CORRELATION_ID_HEADER]: newCorrelationId() },
  });

  return next(stamped);
};
