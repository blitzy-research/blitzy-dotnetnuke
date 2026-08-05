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
 * - `ProblemDetails.correlationId` carries this exact value, and it is the member to
 *   quote in a support report. The server resolves it from the same place it resolves
 *   the header, so the two agree by construction.
 * - `ProblemDetails.traceId` is a DIFFERENT identifier and must not be quoted in its
 *   place. The server derives that member from the ambient trace identifier first and
 *   falls back to the request identifier - which the middleware aligns to this value -
 *   only when no ambient trace is running, so it matches this header by coincidence
 *   rather than by contract.
 *
 * The success-response envelope carries no correlation, trace or request member of
 * its own, so the header is the only place the identifier appears on a successful
 * response.
 *
 * ## A caller-supplied header is preserved when the server would honour it
 *
 * A request that already carries a *usable* identifier is forwarded untouched. That
 * is what lets a retry re-send the original request and be recognised as the same
 * logical operation as the first attempt rather than as a second, unrelated one.
 *
 * Presence alone is not enough to earn that pass-through, because presence alone
 * does not achieve it. The server keeps an inbound identifier only when it is a
 * single header line, non-blank, no longer than its length bound and printable
 * US-ASCII throughout; anything else it discards and replaces with one of its own.
 * Forwarding a value that fails those clauses would therefore not preserve the
 * caller's identifier at all - the caller would simply lose it further downstream,
 * and the browser would hold an identifier that appears in no server log line.
 * Validating here against the same clauses is what makes the pass-through mean what
 * it claims: an identifier that survives is one both sides will actually use.
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
 * The greatest number of characters the server accepts in an inbound identifier.
 *
 * Mirrors the bound the API's correlation-id middleware declares. Counting is by
 * UTF-16 code unit on both sides, so the two measurements agree exactly, including
 * for characters outside the basic multilingual plane, which each occupy two units.
 */
const MAX_CORRELATION_ID_LENGTH = 128;

/**
 * The lowest character code the server accepts, `0x20` (space).
 *
 * Anything below it is a C0 control character. Carriage return and line feed are
 * the two that matter most: an identifier carrying either could split one header
 * line into several, so excluding the whole range below space closes that class of
 * response-splitting and log-forging problem rather than naming its members.
 */
const LOWEST_ACCEPTED_CHARACTER_CODE = 0x20;

/**
 * The highest character code the server accepts, `0x7e` (tilde).
 *
 * Above it lie the C1 controls and the whole of non-ASCII. Header values have no
 * reliable encoding negotiation, so a non-ASCII identifier cannot be relied upon to
 * arrive as it left.
 */
const HIGHEST_ACCEPTED_CHARACTER_CODE = 0x7e;

/**
 * Reports whether an inbound identifier is one the server will keep.
 *
 * The three clauses and their order mirror the API's own validation exactly, so
 * that this side never forwards a value the other side would reject:
 *
 * 1. Not blank. A value of only whitespace is rejected even though space itself is
 *    an accepted character, because it names nothing.
 * 2. Within the length bound. Measured on the value as received, not on a trimmed
 *    copy, because the server measures it that way too.
 * 3. Printable US-ASCII throughout.
 *
 * The value is deliberately *not* trimmed or otherwise repaired. The server keeps a
 * usable value verbatim, so silently rewriting it here would make the browser and
 * the server disagree about the identifier for the same request - the precise
 * failure this whole mechanism exists to prevent.
 *
 * @param candidate The single inbound header value.
 * @returns `true` when every clause passes.
 */
function isUsableCorrelationId(candidate: string): boolean {
  if (candidate.trim().length === 0) {
    return false;
  }

  if (candidate.length > MAX_CORRELATION_ID_LENGTH) {
    return false;
  }

  for (let index = 0; index < candidate.length; index += 1) {
    const code = candidate.charCodeAt(index);

    if (code < LOWEST_ACCEPTED_CHARACTER_CODE || code > HIGHEST_ACCEPTED_CHARACTER_CODE) {
      return false;
    }
  }

  return true;
}

/**
 * Attaches {@link CORRELATION_ID_HEADER} to every outbound request that does not
 * already carry a usable identifier.
 *
 * Registered first in the interceptor chain. See the file header for why that
 * position is load-bearing and for how the identifier travels back.
 */
export const correlationIdInterceptor: HttpInterceptorFn = (req, next) => {
  // MIGRATION: a cross-cutting concern with no legacy predecessor, and the
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
  // response the value is also published in the body as `ProblemDetails.correlationId`,
  // which is what the error interceptor quotes; the success envelope has no
  // correlation, trace or request member at all, so on a successful response the
  // header is the only carrier.

  // MIGRATION: a caller-supplied header is preserved rather than replaced, which is
  // a deliberate divergence from the simpler "always stamp" reading. Overwriting it
  // would give a retried request a second identifier and split one logical
  // operation across two identifiers in the logs - defeating the purpose of having
  // the identifier at all. The original request object is forwarded rather than
  // cloned, so a retry re-sends byte-identical headers.
  //
  // Preservation is conditional on the value being one the server will honour, and
  // that condition is what makes the preservation real rather than nominal. Presence
  // is necessary but not sufficient: an identifier that is repeated across several
  // header lines, blank, over the length bound, or carrying a control or non-ASCII
  // character is discarded by the server and replaced with one of its own. Passing
  // such a value through would preserve nothing - it would leave the browser holding
  // an identifier that appears in no server log line, which is indistinguishable
  // from having no identifier at all and is strictly worse than stamping a fresh
  // usable one. Every value the pass-through is actually meant to protect - a single
  // printable identifier within the bound, such as the one a retry re-sends - still
  // takes this branch and is still forwarded on the original, un-cloned request.
  const inbound = req.headers.getAll(CORRELATION_ID_HEADER);

  if (inbound !== null && inbound.length === 1 && isUsableCorrelationId(inbound[0])) {
    return next(req);
  }

  // `HttpHeaders` is immutable, so the header is applied by cloning. `setHeaders`
  // adds this one name and copies everything else across untouched; the method,
  // URL, params and body are carried over unchanged.
  //
  // `setHeaders` *replaces* the named header rather than appending to it, which is
  // what the repeated-header case needs: however many lines arrived, exactly one
  // leaves. An append would have preserved the ambiguity the server rejects.
  const stamped = req.clone({
    setHeaders: { [CORRELATION_ID_HEADER]: newCorrelationId() },
  });

  return next(stamped);
};
