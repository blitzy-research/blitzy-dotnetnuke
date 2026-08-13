/**
 * Strict parsing of the numeric record identifiers this application reads out of the URL. This module is
 * the SINGLE place a route parameter becomes a number. Every routed screen that addresses a record by
 * identifier — a portal, a module, a page, an account, a role, a profile property — reaches that
 * identifier through {@link parseRouteId} and through nothing else.
 */

/** The grammar an identifier segment must match in full. */
const SIGNED_DECIMAL_INTEGER = /^-?\d+$/;

/**
 * The inclusive lower bound of a signed 32-bit integer. Every identifier column in this schema is a SQL
 * Server `int`, and the API binds each route identifier with `int.TryParse`.
 */
const MINIMUM_ROUTE_ID = -(2 ** 31);

/** The inclusive upper bound of a signed 32-bit integer. @see MINIMUM_ROUTE_ID. */
const MAXIMUM_ROUTE_ID = 2 ** 31 - 1;

/**
 * Parses a route parameter into a record identifier, or refuses it. THE ONLY SUPPORTED WAY TO TURN A
 * ROUTE PARAMETER INTO A NUMBER in this application.
 *
 * @param value The raw route parameter, or null or undefined when the route carries none.
 * @returns The identifier, or null when the value is absent or unusable.
 */
export function parseRouteId(value: unknown): number | null {
  // Absent and unusable collapse to the same answer, and the check comes first so that nothing below has to
  // consider a non-string.
  if (typeof value !== 'string') {
    return null;
  }

  if (SIGNED_DECIMAL_INTEGER.test(value) === false) {
    return null;
  }

  // `Number` rather than `Number.parseInt`, and only AFTER the grammar has been proved.
  const parsed = Number(value);

  // Magnitude before range. A value beyond the exactly-representable integers has already been rounded by
  // the conversion above, so the number in hand is not the number that was written; refusing it is the only
  // correct response, because the rounded neighbour names a different row.
  if (Number.isSafeInteger(parsed) === false) {
    return null;
  }

  if (parsed < MINIMUM_ROUTE_ID || parsed > MAXIMUM_ROUTE_ID) {
    return null;
  }

  return parsed;
}

/**
 * What a route parameter turned out to be, with ABSENT and UNREADABLE kept apart. ⚠ THE DISTINCTION THIS
 * TYPE CARRIES IS A SECURITY-ADJACENT ONE, and collapsing it is what {@link parseRouteId} does by design
 * — that function answers "what identifier is this?" and a single `null` is the right answer for both
 * "there is no parameter" and "the parameter is not an identifier".
 */
export type RouteIdReading =
  /** The matched route declares no such parameter, so the caller is not addressing a record. */
  | { readonly kind: 'absent' }
  /** A parameter was supplied but does not spell a usable identifier. */
  | { readonly kind: 'unreadable' }
  /** A usable identifier. `0` and `-1` reach here, because both are real in this schema. */
  | { readonly kind: 'identifier'; readonly id: number };

/**
 * Classifies a route parameter, distinguishing an absent one from an unusable one.
 *
 * @param value The raw route parameter, or null or undefined when the route carries none.
 * @returns Which of the three states the parameter is in.
 */
export function readRouteId(value: unknown): RouteIdReading {
  if (value === null || value === undefined) {
    return { kind: 'absent' };
  }

  const parsed = parseRouteId(value);

  return parsed === null ? { kind: 'unreadable' } : { kind: 'identifier', id: parsed };
}

/**
 * Whether a value is a usable record identifier. The predicate form of {@link parseRouteId}, for the call
 * sites that hold a number already — one bound through `withComponentInputBinding()` with a numeric
 * transform, or one read back off a loaded record — and need to know it is addressable before using it as
 * a key.
 *
 * @param value A candidate identifier from any source.
 * @returns True when the value is a safe integer within the signed 32-bit range.
 */
export function isRouteId(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isSafeInteger(value) &&
    value >= MINIMUM_ROUTE_ID &&
    value <= MAXIMUM_ROUTE_ID
  );
}
