import type { HttpInterceptorFn } from '@angular/common/http';

import { isApiRequest } from '../config/api-endpoints';

/**
 * Stamps every outbound request TO THIS APPLICATION'S OWN API with a correlation
 * identifier, so that one identifier spans the browser, the API and every log line
 * either of them writes.
 *
 * ## Position in the chain
 *
 * `withInterceptors([A, B, C])` composes as `A(next = B(next = C(next =
 * backend)))`, so on the request path the order is `A -> B -> C -> backend`. This
 * interceptor is registered as `A`, the outermost one, and that placement is
 * behaviour rather than style: the identifier is attached before the auth
 * interceptor adds `Authorization` and before anything downstream can
 * short-circuit, retry or fail the request. Every API request that leaves this
 * application therefore carries an identifier, including the ones that never
 * reach the network.
 *
 * ## Which requests are stamped, and why the boundary is drawn here
 *
 * ⚠ THE API'S OWN ADDRESSES ONLY, plus the three health probes. The boundary is decided
 * by {@link isApiRequest} — the same predicate the bearer interceptor gates the
 * `Authorization` header on — so the two headers this application adds share ONE
 * definition of "our API" rather than holding two that can disagree.
 *
 * MIGRATION: this interceptor previously stamped, or preserved, the header on EVERY
 * request `HttpClient` issued, and that was wrong in two distinct ways rather than
 * merely broad:
 *
 * - It LEAKED. A caller-supplied identifier was forwarded to whatever host the request
 *   addressed, so a value accepted from this application's own caller could be carried
 *   to a foreign origin. Nothing in the loop this header exists to close needs that:
 *   only this API reads the header, echoes it, and joins it to a log scope.
 * - It BROKE REQUESTS IT HAD NOTHING TO DO WITH. `X-Correlation-Id` is not a
 *   CORS-safelisted request header, so adding it turns an otherwise simple cross-origin
 *   `GET` into one requiring a preflight — and a third-party endpoint that does not list
 *   the name in `Access-Control-Allow-Headers` then fails that preflight outright.
 *   Stamping a header is meant to be an observability act with no bearing on whether the
 *   request succeeds; unscoped, it was not.
 *
 * The exclusion is a PASS-THROUGH rather than a strip: a non-API request is forwarded on
 * the original, un-cloned request object carrying whatever headers its caller set. This
 * interceptor removes nothing it did not add.
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
 * single header line in one of two CANONICAL, NON-SEMANTIC shapes - 32 hexadecimal
 * characters, or the hyphenated 36-character `8-4-4-4-12` form; anything else it
 * discards and replaces with one of its own. Forwarding a value that fails that test
 * would therefore not preserve the caller's identifier at all - the caller would
 * simply lose it further downstream, and the browser would hold an identifier that
 * appears in no server log line. Validating here against the same rule is what makes
 * the pass-through mean what it claims: an identifier that survives is one every
 * layer will actually use. See {@link isCanonicalCorrelationId} for why the shape,
 * rather than merely the character range, is the security control.
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
export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

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
 * The output is also one of the two CANONICAL shapes the server accepts - see
 * {@link isCanonicalCorrelationId} - so a value generated here is kept by the server
 * rather than discarded and replaced. That is a hard requirement rather than a
 * nicety: an identifier the server refuses is one the browser holds alone.
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
 * The length of the unhyphenated canonical form: 32 hexadecimal characters.
 *
 * This is the form the API generates — a .NET `Guid` rendered with `"N"` — and the
 * form the reverse proxy substitutes when it has to mint one, so a value that
 * originated on either of them takes this branch on the way back.
 */
const COMPACT_FORM_LENGTH = 32;

/**
 * The length of the hyphenated canonical form: the RFC 4122 `8-4-4-4-12` rendering.
 *
 * This is the form {@link newCorrelationId} produces.
 */
const HYPHENATED_FORM_LENGTH = 36;

/**
 * The character positions of the four hyphens in the hyphenated canonical form.
 *
 * Declared rather than derived so the shape test reads as the specification it
 * enforces. Every other position must hold a hexadecimal digit.
 */
const HYPHEN_POSITIONS: readonly number[] = Object.freeze([8, 13, 18, 23]);

/**
 * Reports whether one character is a hexadecimal digit, in either register.
 *
 * Written against character codes rather than a regular expression so that the test
 * is a comparison per character with nothing to compile and no locale to consult.
 *
 * @param code A UTF-16 code unit.
 * @returns `true` for `0`–`9`, `a`–`f` and `A`–`F`.
 */
function isHexDigitCode(code: number): boolean {
  return (
    (code >= 0x30 && code <= 0x39) ||
    (code >= 0x61 && code <= 0x66) ||
    (code >= 0x41 && code <= 0x46)
  );
}

/**
 * Reports whether an identifier is in one of the two canonical, non-semantic shapes.
 *
 * ⚠ THIS IS A SECURITY CONTROL AND NOT A FORMAT PREFERENCE, and it is the whole of the
 * fix for a critical finding that spanned this file and the API's correlation-id
 * middleware. Both sides previously accepted ANY non-blank printable US-ASCII value up
 * to 128 characters. That closed log forging and response splitting, because it excluded
 * every control character — and it did nothing at all about SEMANTIC content. A caller
 * could send a password, a bearer token, an e-mail address or an API key as its
 * correlation header, and the value was then forwarded here, published to the server's
 * logging scope, written into every request, exception and audit entry for that request,
 * echoed on the response, published as the RFC 7807 `correlationId` member and finally
 * shown to an operator as the support reference to quote. A secret in a retained log is a
 * disclosure however it arrived.
 *
 * Two shapes are accepted, both pure hexadecimal and therefore incapable of carrying a
 * word, a delimiter, an at-sign, a dot or a slash:
 *
 * 1. 32 hexadecimal characters — what the API and the proxy generate.
 * 2. The hyphenated 36-character `8-4-4-4-12` form — what this file generates.
 *
 * Case is accepted in either register and is neither required nor rewritten. There is no
 * separate length bound because each form has exactly one length.
 *
 * ⚠ THE THREE LAYERS MUST AGREE, and this predicate is one of the three copies of one
 * rule: the API's `Api/Middleware/CorrelationIdMiddleware.cs` applies it to what arrives,
 * and `docker/nginx.conf` applies it a third time before forwarding. A value one layer
 * keeps and another replaces leaves the browser holding an identifier that appears in no
 * server log line, which is indistinguishable from having none.
 *
 * The value is deliberately *not* trimmed, re-cased or otherwise repaired. A repaired
 * value is one the caller never saw, so it would be the server's identifier and not the
 * caller's — the precise failure this whole mechanism exists to prevent. It is either
 * usable exactly as it arrived or it is replaced outright.
 *
 * Exported so that `core/interceptors/error.interceptor.ts` can apply the identical test
 * to a correlation identifier read from a RESPONSE header, which is the only place a
 * gateway-authored identifier can be recovered from. Sharing the predicate is what keeps
 * that surface from admitting a shape this one refuses.
 *
 * @param candidate The identifier to test.
 * @returns `true` when the value is in a canonical shape.
 */
export function isCanonicalCorrelationId(candidate: string): boolean {
  if (candidate.length !== COMPACT_FORM_LENGTH && candidate.length !== HYPHENATED_FORM_LENGTH) {
    return false;
  }

  const hyphenated = candidate.length === HYPHENATED_FORM_LENGTH;

  for (let index = 0; index < candidate.length; index += 1) {
    if (hyphenated && HYPHEN_POSITIONS.includes(index)) {
      if (candidate.charCodeAt(index) !== 0x2d) {
        return false;
      }

      continue;
    }

    if (!isHexDigitCode(candidate.charCodeAt(index))) {
      return false;
    }
  }

  return true;
}

/**
 * The health-probe paths, which this application's API publishes at the HOST ROOT rather
 * than beneath its versioned prefix.
 *
 * THE ONE EXPLICIT EXCEPTION to the API-address test, and it is stated here rather than
 * inherited. All three are this API's own endpoints and the server's correlation middleware
 * runs for them exactly as it does for everything else, so a probe SHOULD carry an
 * identifier — but they sit outside the configured base path, so {@link isApiRequest}
 * answers false for them and the endpoint catalogue cannot describe them either, because it
 * composes paths beneath that base.
 *
 * ⚠ THE LIST IS DELIBERATELY DUPLICATED from `core/interceptors/auth.interceptor.ts` rather
 * than shared, and the two lists mean OPPOSITE things: that interceptor excludes these paths
 * so a probe never carries a credential or spends a rate-limit budget, whereas this one
 * INCLUDES them so a probe is still traceable. Sharing one constant between an exclusion and
 * an inclusion would invite a future editor to change the membership for one purpose and
 * silently change it for the other.
 *
 * Compared against the resolved path with any single trailing separator removed, so
 * `/health/` is recognised as `/health`.
 */
const HEALTH_PROBE_PATHS: readonly string[] = Object.freeze([
  '/health',
  '/health/ready',
  '/health/live',
]);

/**
 * Whether a request addresses one of THIS ORIGIN'S root-published health probes.
 *
 * Resolved against the document base so that a relative path and the equivalent absolute
 * URL on this origin are classified alike, and FAILS CLOSED — a value that cannot be parsed
 * as a URL is simply not a probe, which is the same reading the API-address test applies.
 *
 * ⚠ THE ORIGIN IS COMPARED AS WELL AS THE PATH, and that clause is load-bearing here in a
 * way it is not in the bearer interceptor's own probe test. There, the list is an EXCLUSION:
 * matching a foreign `/health` merely declines to attach a credential, which is harmless.
 * Here the list is an INCLUSION, so a path-only test would stamp the correlation header on
 * `https://third-party.example/health` — reintroducing, for three paths, exactly the
 * cross-origin leak and the preflight breakage that scoping this interceptor exists to
 * close. A specification asserts that case directly.
 *
 * @param url The request target, relative or absolute.
 * @returns True when the request is one of the three probes on this origin.
 */
function isHealthProbe(url: string): boolean {
  let resolved: URL;
  let base: URL;

  try {
    base = new URL(document.baseURI);
    resolved = new URL(url, base);
  } catch {
    return false;
  }

  if (resolved.origin !== base.origin) {
    return false;
  }

  const path = resolved.pathname;
  const normalised = path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;

  return HEALTH_PROBE_PATHS.includes(normalised);
}

/**
 * Attaches {@link CORRELATION_ID_HEADER} to every outbound request TO THIS API that does
 * not already carry a usable identifier, and forwards every other request untouched.
 *
 * Registered first in the interceptor chain. See the file header for why that
 * position is load-bearing, which requests are in scope, and how the identifier travels
 * back.
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
  // is necessary but not sufficient: an identifier repeated across several header
  // lines, or one whose shape is not canonical, is discarded by the server and
  // replaced with one of its own. Passing such a value through would preserve
  // nothing - it would leave the browser holding an identifier that appears in no
  // server log line, which is indistinguishable from having no identifier at all and
  // is strictly worse than stamping a fresh usable one. Every value the pass-through
  // is actually meant to protect - the canonical identifier a retry re-sends, which
  // this application generated in the first place - still takes this branch and is
  // still forwarded on the original, un-cloned request.
  //
  // ⚠ AND THE CONDITION IS NOW A SHAPE RATHER THAN A CHARACTER RANGE, which is the
  // fix for a critical finding: a caller-chosen value of arbitrary printable text
  // could carry a secret into this application's logs, its response headers, its
  // problem documents and its operator-facing support references. See
  // isCanonicalCorrelationId.

  // ⚠ THE SCOPE TEST COMES FIRST, BEFORE THE HEADER IS EVEN READ, and it is the whole of
  // the fix for the unscoped stamping described in the file header. A request that does not
  // address this API is forwarded EXACTLY as it arrived: not stamped, and not stripped
  // either - whatever the caller set is that caller's business.
  //
  // The probe test is asked FIRST among the two, deliberately, for the same reason the
  // bearer interceptor asks its own probe test first: a probe path is not beneath the
  // configured API base, so the address test alone would exclude it - but only for as long
  // as that remains true. Asking the probe question in its own right means the guarantee
  // survives a future widening of the base rather than depending on a test that exists for
  // a different purpose.
  if (!isHealthProbe(req.url) && !isApiRequest(req.url)) {
    return next(req);
  }

  const inbound = req.headers.getAll(CORRELATION_ID_HEADER);

  if (inbound !== null && inbound.length === 1 && isCanonicalCorrelationId(inbound[0])) {
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
