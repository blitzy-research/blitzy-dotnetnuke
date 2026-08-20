import type { HttpInterceptorFn } from '@angular/common/http';

import { isApiRequest } from '../config/api-endpoints';

/**
 * The header that carries the correlation identifier. The spelling is contractual: the API's
 * correlation-id middleware declares the identical literal, and the cross-origin policy exposes that name
 * to the browser.
 */
export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/** The number of random bytes a UUID is built from. */
const UUID_BYTE_LENGTH = 16;

const BYTE_VALUE_COUNT = 256;

/**
 * Renders 16 bytes as a canonical RFC 4122 version 4 UUID string. The version and variant nibbles are
 * overwritten in place so that the result is indistinguishable in shape from a `crypto.randomUUID()`
 * value: 36 characters of lowercase hexadecimal and hyphens.
 *
 * @param bytes Exactly {@link UUID_BYTE_LENGTH} bytes of randomness.
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
 * Produces a fresh correlation identifier. Three platform-native sources are tried in order of
 * preference.
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

/** The length of the unhyphenated canonical form: 32 hexadecimal characters. */
const COMPACT_FORM_LENGTH = 32;

/** The length of the hyphenated canonical form: the RFC 4122 `8-4-4-4-12` rendering. */
const HYPHENATED_FORM_LENGTH = 36;

/** The character positions of the four hyphens in the hyphenated canonical form. */
const HYPHEN_POSITIONS: readonly number[] = Object.freeze([8, 13, 18, 23]);

/**
 * Reports whether one character is a hexadecimal digit, in either register.
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
 * The health-probe paths, which this application's API publishes at the HOST ROOT rather than beneath its
 * versioned prefix. THE ONE EXPLICIT EXCEPTION to the API-address test, and it is stated here rather than
 * inherited.
 */
const HEALTH_PROBE_PATHS: readonly string[] = Object.freeze([
  '/health',
  '/health/ready',
  '/health/live',
]);

/**
 * Whether a request addresses one of THIS ORIGIN'S root-published health probes.
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
 * Attaches {@link CORRELATION_ID_HEADER} to every outbound request TO THIS API that does not already
 * carry a usable identifier, and forwards every other request untouched. Registered first in the
 * interceptor chain.
 */
export const correlationIdInterceptor: HttpInterceptorFn = (req, next) => {
  // A cross-cutting concern with no legacy predecessor, and the absence is measured rather than assumed.

  // MIGRATION: this closes a loop the legacy application could not have closed.

  // MIGRATION: a caller-supplied header is preserved rather than replaced, which is a deliberate divergence
  // from the simpler "always stamp" reading.

  // ⚠ THE SCOPE TEST COMES FIRST, BEFORE THE HEADER IS EVEN READ, and it is the whole of the fix for the
  // unscoped stamping described in the file header.
  if (!isHealthProbe(req.url) && !isApiRequest(req.url)) {
    return next(req);
  }

  const inbound = req.headers.getAll(CORRELATION_ID_HEADER);

  if (inbound !== null && inbound.length === 1 && isCanonicalCorrelationId(inbound[0])) {
    return next(req);
  }

  const stamped = req.clone({
    setHeaders: { [CORRELATION_ID_HEADER]: newCorrelationId() },
  });

  return next(stamped);
};
