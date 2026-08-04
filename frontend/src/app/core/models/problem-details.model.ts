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
 *   failure code such as `auth.unauthenticated`.
 * - The framework itself writes them for a status it maps without reaching an
 *   action — a `404` from a bare not-found result, or a model-state failure.
 *   Those carry a `type` pointing at the relevant section of the HTTP semantics
 *   specification and, for a validation failure, an `errors` member.
 *
 * Both shapes were observed on the wire against a running API, not inferred.
 *
 * WHY EVERY MEMBER IS OPTIONAL, AND WHAT THE SERVER NEVERTHELESS GUARANTEES.
 * These two facts are different and are stated separately on purpose.
 *
 * The server's guarantee is narrow and precise: `ValidationProblemDetailsFactory`
 * fills `status`, `title` and `detail` unconditionally, at every status code, so
 * those three are present on every payload the API itself produces. `type` is
 * deliberately NOT given a blanket fallback — that factory prefers to omit it
 * over publishing a URI that documents nothing — and `instance` is passed
 * through only when a caller supplies it and is never derived from the request,
 * so it is absent in practice. `traceId` is attached only when a trace
 * identifier is available.
 *
 * The typing here is nonetheless optional throughout, which is deliberate and
 * not laziness. RFC 7807 section 3.1 declares every member optional; a proxy or
 * gateway between the browser and the API can return an error body this
 * application never produced; and a member typed as required that turns out to
 * be absent yields `undefined` where the compiled type promised a value, which
 * is precisely the failure a strict compiler is supposed to prevent. Tolerating
 * absence costs a consumer one optional-chaining operator. Assuming presence
 * costs a runtime crash. Absence is therefore handled here, once, by
 * {@link problemDetailsMessage} and {@link problemDetailsFieldErrors}, rather
 * than at every call site.
 *
 * A member's absence is expressed by the member being MISSING, never by its
 * value being `null` — so these are `?:` and not `| null`. That is worth
 * writing down because it is the opposite of every other model in this folder,
 * where a nullable member is declared present-and-nullable. The API sets
 * `DefaultIgnoreCondition` to `Never`, so a null member of an ordinary response
 * IS written as `null`; the framework's own problem-details type, however,
 * annotates each of its optional members with a per-member null-omission
 * condition, and a per-member condition overrides the collection-wide one. The
 * server-side registration says so explicitly, and serialising the framework
 * type with `Never` set confirms it: `type` and `instance` disappear from the
 * JSON rather than appearing as `null`. This contract must not be "aligned"
 * with the others.
 *
 * MIGRATION: the legacy application had no error envelope at all. Failures
 * surfaced as markup rendered for a human — `AccessDenied.ascx.vb` added a
 * warning banner to the page, `Default.aspx.vb` appended an inline `<div>`
 * carrying the exception message and showed it only to administrators, and
 * `release.config` set `customErrors` to `RemoteOnly` so everything else became
 * a server-rendered error page. Searching those files for a machine-readable
 * error contract returns nothing, because there was none. This type therefore
 * describes new behaviour rather than a translation, and nobody should go
 * looking for the predecessor it was derived from.
 *
 * MIGRATION: the advertised media type is not uniform, and the split is
 * measured rather than assumed. The authorisation-refusal and rate-limiter
 * paths set `application/problem+json` explicitly, as RFC 7807 section 3
 * requires. The model-validation path answers with `application/json` instead,
 * because the framework's problem-details write path bypasses content-type
 * negotiation. The BODY is a conforming problem document in both cases, which
 * is what this type describes and what every consumer reads. Nothing in this
 * application inspects the media type, and deliberately so: branching on the
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
   *
   * Optional because the server refuses to invent one. Where a status code is in
   * neither of its problem-type tables the member is omitted rather than filled
   * with a link that documents nothing.
   */
  readonly type?: string;

  /**
   * A short, human-readable summary of the problem type.
   *
   * This is the member a person reads when no `detail` is present, so it is the
   * primary fallback in {@link problemDetailsMessage}. The API fills it
   * unconditionally — from its problem-type table, or failing that from the
   * platform's own reason phrase for the status code.
   */
  readonly title?: string;

  /**
   * The HTTP status code, repeated in the body by RFC 7807 section 3.1.
   *
   * DELIBERATELY A PLAIN `number` AND NOT A UNION OF THE STATUSES THIS API IS
   * KNOWN TO RETURN. The observed set is `400`, `401`, `403`, `404`, `409`,
   * `422`, `429` and `500`, and that list is illustrative documentation only —
   * it is neither exhaustive nor enforced.
   *
   * Narrowing it would be actively harmful. The status is chosen by the server,
   * so a status the union omitted would become a compile error at the consumer
   * and force a cast — strict typing producing exactly the unsafety it exists to
   * remove. `429` is the concrete case that proves the point: the credential
   * rate limiter refuses with `429` once its per-window permit count is
   * exceeded, which is easy to overlook when enumerating statuses by hand, and
   * it was confirmed on a running API rather than assumed. The retry hint that
   * accompanies it arrives in the standard `Retry-After` response header, which
   * is why this type carries no body member for it.
   */
  readonly status?: number;

  /**
   * An explanation specific to this occurrence.
   *
   * Preferred over `title` when present, because it describes what actually
   * happened rather than the class of failure. The API fills it unconditionally,
   * falling back to a fixed sentence per status code; the guarantee is that it
   * is always present, never that it is ever specific.
   *
   * Fixed server-authored text in every environment. It never carries an
   * exception message, a stack trace, a file path or a product version, so there
   * is no environment in which this member becomes more talkative and none in
   * which a consumer must suppress it.
   */
  readonly detail?: string;

  /**
   * A URI reference identifying the specific occurrence.
   *
   * Absent in practice. The API passes this through exactly as a caller supplies
   * it and never derives it from the request path, and no call site supplies
   * one, so it was missing from every response observed. Typed here because RFC
   * 7807 defines it and a proxy may add one; consumers should not depend on it.
   */
  readonly instance?: string;

  /**
   * The framework's activity identifier for the request.
   *
   * Distinct from the `X-Correlation-Id` header this application stamps on every
   * outbound request, and the distinction is real rather than theoretical: sent
   * a known correlation identifier, the API echoed that exact value back in the
   * response header while this member carried an unrelated W3C trace-context
   * value of the form `00-<trace>-<span>-00`. They are two independent
   * identifiers with two different formats, and neither is derived from the
   * other.
   *
   * Consequently the correlation identifier is the value to quote when joining a
   * browser-side report to a server-side log entry, and it travels in its own
   * response header — never as a member of this body, and never on a successful
   * response envelope, which has exactly one source of truth for it. Both
   * identifiers are diagnostic; neither is shown to a person.
   */
  readonly traceId?: string;

  /**
   * Per-field validation failures, keyed by the field name.
   *
   * Present only on a validation failure, which is why {@link
   * ValidationProblemDetails} exists: prefer that type where a validation
   * response is what a function actually requires.
   *
   * MUST BE READ WITH BRACKET ACCESS — `problem.errors['Email']`, never
   * `problem.errors.Email`. This is not a style preference. The member is typed
   * as an index signature and `noPropertyAccessFromIndexSignature` is enabled
   * for this workspace, so dot access on it is a compile error. The rule earns
   * its keep: a key is only ever known at runtime, and dot access would let a
   * typo compile as a silent `undefined`.
   *
   * The keys are the server's model-state keys, reproduced byte for byte. They
   * are Pascal-cased — `Username`, not `username` — because they name model
   * members rather than JSON members, and the camel-case naming policy that
   * governs body property names does not apply to dictionary keys. Two further
   * key shapes were observed and are not field names at all: `$`, used for a
   * malformed request body, and the empty string, used for a form-level error.
   * A consumer matching a key against a form control must account for all three;
   * see {@link problemDetailsFieldErrors}.
   */
  readonly errors?: ProblemDetailsErrors;
}

/**
 * The `errors` member of a validation problem document.
 *
 * A dictionary from field name to the list of messages for that field. The
 * server always writes an array, even for a single message.
 *
 * An index-signature type by design, so that bracket access is the only
 * syntactically available way to read a key under
 * `noPropertyAccessFromIndexSignature`. Declaring it as an object of named
 * properties would defeat that, and would also be a lie: the key set is
 * whichever fields happened to fail.
 */
export type ProblemDetailsErrors = Readonly<Record<string, readonly string[]>>;

/**
 * A problem document that is known to carry per-field validation failures.
 *
 * Identical to {@link ProblemDetails} in every respect but one: `errors` is
 * required rather than optional. That narrowing is the entire purpose of the
 * type. A function whose contract is "given a validation failure" can accept
 * this and read the dictionary without a presence test, instead of accepting the
 * looser type and re-checking a member the caller already established.
 *
 * The dictionary may still be empty. The server emits `errors` as `{}` when
 * model state carries no entries, so a non-empty dictionary is not guaranteed by
 * the presence of the member — only that the member is there to read.
 *
 * Every caveat on {@link ProblemDetails.errors} applies unchanged, in
 * particular that keys are read with bracket access and are Pascal-cased
 * model-state keys rather than camel-cased JSON member names.
 */
export interface ValidationProblemDetails extends ProblemDetails {
  /**
   * Per-field validation failures, keyed by the field name. Always present on
   * this type; possibly empty. Read with bracket access —
   * `problem.errors['Username']`.
   */
  readonly errors: ProblemDetailsErrors;
}

// MIGRATION: nothing below this line reads or renders message text as markup,
// and that is a deliberate boundary rather than an omission. Legacy message
// wording came from resource files in which raw HTML is commonplace — anchors,
// list items, paragraphs, line breaks and, in a handful of values, script tags —
// and the legacy pages rendered it unescaped, which is exactly what
// AccessDenied.ascx.vb did with its localised string while escaping the
// query-string message beside it. That text is therefore untrusted. This file
// declares the SHAPE of the error contract and extracts plain strings from it;
// it deliberately exposes no pre-sanitised or "safe HTML" member, because a
// member of that name would invite a consumer to bind it into a raw-markup sink
// and would move an escaping decision into a type declaration, where it cannot
// be reviewed. Escaping is the renderer's job: Angular's default text
// interpolation escapes by construction, and every consumer here binds these
// values as text.

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
 * Treating the empty string as absent is a display decision confined to this
 * function, not a rewriting of the wire value. The legacy null contract encoded
 * absent text AS the empty string, so a blank message is the legacy spelling of
 * "nothing to say" and falling back for it reproduces the legacy outcome. The
 * document itself is never mutated, and no other member is coerced.
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
 *    model property names (`Username`) while an Angular form control is named in
 *    the client's own casing (`username`). Only the first character is changed:
 *    lower-casing the whole key would turn `PageSize` into `pagesize` and stop it
 *    matching anything.
 * 2. Entries whose value is not a non-empty array of strings are dropped, so a
 *    malformed document cannot put a non-renderable value in front of a person.
 *
 * The first normalisation is a CLIENT-SIDE adaptation and is confined to this
 * function. The server performs no such mapping — its factory re-cases, re-keys
 * and edits nothing — so the keys on the wire remain Pascal-cased and anything
 * reading {@link ProblemDetails.errors} directly still sees them that way.
 *
 * Keys that are not field names survive untouched and are handled by the
 * caller rather than dropped. The empty-string key that a model-state failure
 * uses for a form-level error has no first character to lower-case and no
 * control to match; `$`, which a malformed request body produces, lower-cases to
 * itself. Consumers surface both alongside the summary rather than beside a
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
