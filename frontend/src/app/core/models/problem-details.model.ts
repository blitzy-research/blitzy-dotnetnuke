/**
 * Client-side mirror of the RFC 7807 problem document the API returns for every unsuccessful request. The
 * server produces these from two places, and both shapes are covered here: - `GlobalExceptionHandler`
 * writes them through the framework's problem-details service for an unhandled exception, and the API's
 * `Result`-to-status translator writes them for an expected failure.
 */
export interface ProblemDetails {
  /** A URI reference identifying the problem type. */
  readonly type?: string;

  /**
   * A short, human-readable summary of the problem type. This is the member a person reads when no
   * `detail` is present, so it is the primary fallback in {@link problemDetailsMessage}.
   */
  readonly title?: string;

  /**
   * The HTTP status code, repeated in the body by RFC 7807 section 3.1. DELIBERATELY A PLAIN `number` AND
   * NOT A UNION OF THE STATUSES THIS API IS KNOWN TO RETURN. The observed set is `400`, `401`, `403`,
   * `404`, `409`, `422`, `429` and `500`, and that list is illustrative documentation only — it is
   * neither exhaustive nor enforced.
   */
  readonly status?: number;

  /** An explanation specific to this occurrence. */
  readonly detail?: string;

  /**
   * A URI reference identifying the specific occurrence. OPTIONAL, AND GENUINELY ABSENT RATHER THAN NULL
   * — and the difference from `meta` on the success envelope is worth stating, because the server's
   * serializer policy writes every declared member INCLUDING one holding null, which would ordinarily
   * make `"instance": null` appear here.
   */
  readonly instance?: string;

  readonly traceId?: string;

  /** The correlation identifier the server validated for this request. */
  readonly correlationId?: string;

  /**
   * Per-field validation failures, keyed by the field name. Present only on a validation failure, which
   * is why {@link ValidationProblemDetails} exists: prefer that type where a validation response is what
   * a function actually requires.
   */
  readonly errors?: ProblemDetailsErrors;
}

/**
 * The `errors` member of a validation problem document. A dictionary from field name to the list of
 * messages for that field.
 */
export type ProblemDetailsErrors = Readonly<Record<string, readonly string[]>>;

/** A problem document that is known to carry per-field validation failures. */
export interface ValidationProblemDetails extends ProblemDetails {
  /** Per-field validation failures, keyed by the field name. */
  readonly errors: ProblemDetailsErrors;
}

/**
 * The standard members of a problem document, paired with the test each must pass WHEN PRESENT. A problem
 * document is permitted to carry any subset of these, and RFC 7807 also permits arbitrary extension
 * members, so absence is never a failure and an unrecognised member is never inspected.
 */
const STANDARD_MEMBERS: readonly (readonly [string, (value: unknown) => boolean])[] =
  Object.freeze([
    ['type', isString],
    ['title', isString],
    ['status', isNumber],
    ['detail', isString],
    ['instance', isString],
    ['traceId', isString],
    ['correlationId', isString],
    ['errors', isErrorMap],
  ]);

/**
 * Narrows an unknown value to a {@link ProblemDetails}. Permissive about ABSENCE and strict about TYPE,
 * and the difference between those two is the whole design.
 *
 * @param value A parsed response body, or anything else.
 * @returns True when the value can be read as a problem document.
 */
export function isProblemDetails(value: unknown): value is ProblemDetails {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false;
  }

  const candidate = value as Record<string, unknown>;

  let recognised = 0;

  for (const [member, isValid] of STANDARD_MEMBERS) {
    const held: unknown = candidate[member];

    if (held === undefined) {
      continue;
    }

    if (!isValid(held)) {
      return false;
    }

    recognised += 1;
  }

  // Narrowed only now that the whole shape has passed. Without this last requirement an arbitrary
  // successful payload — an object with no problem member at all — would be mistaken for a failure when a
  // caller passes the wrong thing.
  return recognised > 0;
}

/**
 * Whether a present member is a string.
 *
 * @param value The member's value.
 * @returns True for a string.
 */
function isString(value: unknown): boolean {
  return typeof value === 'string';
}

/**
 * Whether a present member is a number.
 *
 * @param value The member's value.
 * @returns True for a number.
 */
function isNumber(value: unknown): boolean {
  return typeof value === 'number';
}

/**
 * Whether a present `errors` member is a per-field message dictionary.
 *
 * @param value The member's value.
 * @returns True for a dictionary of string arrays.
 */
function isErrorMap(value: unknown): boolean {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false;
  }

  return Object.values(value).every(
    (messages) => Array.isArray(messages) && messages.every(isString),
  );
}

/**
 * Resolves the single sentence to show a person for a problem document. The precedence is `detail`, then
 * `title`, then the supplied fallback, and it is ordered that way because `detail` describes this
 * occurrence while `title` describes the class of failure.
 *
 * @param problem The problem document, or null when the response carried none.
 * @param fallback The sentence to use when the document supplies no usable text.
 * @returns A non-blank sentence.
 */
export function problemDetailsMessage(
  problem: ProblemDetails | null | undefined,
  fallback: string,
): string {
  const detail = problem?.detail?.trim();

  if (detail !== undefined && detail.length > 0) {
    return detail;
  }

  const title = problem?.title?.trim();

  if (title !== undefined && title.length > 0) {
    return title;
  }

  return fallback;
}

/**
 * Extracts the per-field messages from a problem document, keyed for a client form. Two normalisations
 * are applied, both measured against what the API emits rather than assumed: 1.
 *
 * @param problem The problem document, or null.
 * @returns A dictionary of field name to messages; empty when there are none.
 */
export function problemDetailsFieldErrors(
  problem: ProblemDetails | null | undefined,
): ProblemDetailsErrors {
  const errors = problem?.errors;

  if (typeof errors !== 'object' || errors === null) {
    return {};
  }

  const normalised: Record<string, readonly string[]> = {};

  for (const [key, value] of Object.entries(errors)) {
    if (!Array.isArray(value)) {
      continue;
    }

    const messages = value.filter(
      (message): message is string => typeof message === 'string' && message.trim().length > 0,
    );

    if (messages.length === 0) {
      continue;
    }

    const clientKey = key.length === 0 ? key : key.charAt(0).toLowerCase() + key.slice(1);

    normalised[clientKey] = messages;
  }

  return normalised;
}
