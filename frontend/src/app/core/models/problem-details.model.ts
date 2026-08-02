/**
 * Client-side mirror of the RFC 7807 problem document the API returns for every
 * unsuccessful request.
 *
 * The server produces these from two places, and both shapes are covered here:
 *
 * - `GlobalExceptionHandler` writes them through the framework's problem-details
 *   service for an unhandled exception, and the API's `Result`-to-status
 *   translator writes them for an expected failure. Those carry a `type` of the
 *   form `urn:dnnmigration:error:<code>`, where `<code>` is the normalised
 *   failure code such as `portal.not_found`.
 * - The framework itself writes them for a status it maps without reaching an
 *   action — a `404` from a bare not-found result, or a model-state failure.
 *   Those carry a `type` pointing at the relevant section of the HTTP semantics
 *   specification and, for a validation failure, an `errors` member.
 *
 * Every member is optional. That is not defensiveness for its own sake: RFC 7807
 * section 3.1 declares each member optional, the framework omits members whose
 * value is null because the API is configured to skip nulls when writing JSON,
 * and a proxy or gateway between the browser and the API can return an error
 * body this application never produced. Typing a member as required would be a
 * claim the wire does not support, and would push every consumer into a
 * non-null assertion.
 *
 * MIGRATION: the legacy application had no error envelope at all. Failures
 * surfaced as a validation summary rendered into the page, an error page, or a
 * provider-specific message, so there is no legacy contract to preserve here —
 * this type describes new behaviour rather than a translation.
 *
 * MIGRATION: the advertised media type of these documents is
 * `application/json` rather than the `application/problem+json` that RFC 7807
 * section 3 specifies, because the framework's problem-details write path
 * bypasses content-type negotiation. The BODY is a conforming problem document,
 * which is what this type describes and what every consumer reads. Nothing in
 * this application inspects the media type, and deliberately so: pinning the
 * value the framework currently emits would cement the deviation and make a
 * later correction look like a regression.
 */
export interface ProblemDetails {
  /**
   * A URI reference identifying the problem type.
   *
   * Either `urn:dnnmigration:error:<normalised-code>` for a failure the
   * application recognised, or a URL into the HTTP semantics specification for a
   * status the framework mapped on its own. Treated as an opaque identifier: it
   * is never fetched, and never rendered to a person.
   */
  readonly type?: string;

  /**
   * A short, human-readable summary of the problem type.
   *
   * This is the member a person reads when no `detail` is present, so it is the
   * primary fallback in {@link problemDetailsMessage}.
   */
  readonly title?: string;

  /** The HTTP status code, repeated in the body by RFC 7807 section 3.1. */
  readonly status?: number;

  /**
   * An explanation specific to this occurrence.
   *
   * Preferred over `title` when present, because it describes what actually
   * happened rather than the class of failure.
   */
  readonly detail?: string;

  /** A URI reference identifying the specific occurrence. */
  readonly instance?: string;

  /**
   * The framework's activity identifier for the request.
   *
   * Distinct from the `X-Correlation-Id` header this application stamps on every
   * outbound request: the two coincide only when the API adopts the supplied
   * value as its activity identifier. Both are diagnostic and neither is shown
   * to a person.
   */
  readonly traceId?: string;

  /**
   * Per-field validation failures, keyed by the field name.
   *
   * Present only on a validation failure. The keys are the server's property
   * names, which are Pascal-cased — `PageSize`, not `pageSize` — because they
   * name model members rather than JSON members. Consumers matching a key
   * against a form control name must account for that; see
   * {@link problemDetailsFieldErrors}.
   */
  readonly errors?: ProblemDetailsErrors;
}

/**
 * The `errors` member of a validation problem document.
 *
 * A dictionary from field name to the list of messages for that field. The
 * server always writes an array, even for a single message.
 */
export type ProblemDetailsErrors = Readonly<Record<string, readonly string[]>>;

/**
 * Narrows an unknown value to a {@link ProblemDetails}.
 *
 * Deliberately permissive, and the reason is specific. A strict test — requiring
 * `type`, `title` and `status` together — would reject documents the API
 * genuinely emits, because a problem document is permitted to carry any subset
 * of the members. So the test asks the only question a consumer actually needs
 * answered: is this a non-null object that is not an array, carrying at least
 * one member a problem document would carry?
 *
 * The array exclusion matters because `typeof [] === 'object'`, so without it a
 * JSON array body would pass. The "at least one recognised member" requirement
 * is what stops an arbitrary successful payload from being mistaken for a
 * failure when a caller passes the wrong thing.
 *
 * @param value A parsed response body, or anything else.
 * @returns True when the value can be read as a problem document.
 */
export function isProblemDetails(value: unknown): value is ProblemDetails {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false;
  }

  // Indexed reads rather than property access, because the parameter is `object`
  // at this point and `noPropertyAccessFromIndexSignature` is enabled for this
  // workspace.
  const candidate = value as Record<string, unknown>;

  return (
    typeof candidate['type'] === 'string' ||
    typeof candidate['title'] === 'string' ||
    typeof candidate['status'] === 'number' ||
    typeof candidate['detail'] === 'string' ||
    typeof candidate['errors'] === 'object'
  );
}

/**
 * Resolves the single sentence to show a person for a problem document.
 *
 * The precedence is `detail`, then `title`, then the supplied fallback, and it is
 * ordered that way because `detail` describes this occurrence while `title`
 * describes the class of failure. A blank or whitespace-only member is treated as
 * absent, so a server that writes an empty string does not produce a message-less
 * notification.
 *
 * Per-field validation messages are deliberately NOT folded in here. A field
 * message belongs beside its field, which is what {@link problemDetailsFieldErrors}
 * and the shared form-field and error-banner components are for; concatenating
 * them into one sentence would produce an unreadable notification and would
 * duplicate text the form already shows.
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
 * Extracts the per-field messages from a problem document, keyed for a client
 * form.
 *
 * Two normalisations are applied, both measured against what the API emits
 * rather than assumed:
 *
 * 1. Keys are lower-cased on their first character, because the server writes
 *    model property names (`PageSize`) while an Angular form control is named in
 *    the client's own casing (`pageSize`). Only the first character is changed:
 *    lower-casing the whole key would turn `PageSize` into `pagesize` and stop it
 *    matching anything.
 * 2. Entries whose value is not a non-empty array of strings are dropped, so a
 *    malformed document cannot put a non-renderable value in front of a person.
 *
 * The empty-string key that a model-state failure uses for a form-level error is
 * preserved as-is, since it has no first character to lower-case and no control
 * to match — consumers surface it alongside the summary rather than beside a
 * field.
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
