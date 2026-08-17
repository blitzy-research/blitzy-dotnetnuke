// WHAT THIS MODULE IS

import { isProblemDetails, problemDetailsFieldErrors } from '../models/problem-details.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
  ValidationProblemDetails,
} from '../models/problem-details.model';

// ---------------------------------------------------------------------------
// SEVERITY
// ---------------------------------------------------------------------------

/**
 * How forcefully a failure should be presented. The three spellings are a deliberate subset of the
 * notification vocabulary in `core/services/notification.service.ts`, so a value produced here can be
 * handed straight to that service without a translation step.
 */
export type ProblemSeverity = 'error' | 'warning' | 'info';

// ---------------------------------------------------------------------------
// RESULT SHAPES
// ---------------------------------------------------------------------------

/**
 * The messages the server reported against one named field. `field` is the key as it will be matched
 * against a form control - the shape produced by `problemDetailsFieldErrors`, whose first character is
 * lower-cased.
 */
export interface FieldMessages {
  /** The field the messages belong to. */
  readonly field: string;

  /** One or more plain-text messages. */
  readonly messages: readonly string[];
}

export interface ProblemSummary {
  /** How forcefully to present the failure. */
  readonly severity: ProblemSeverity;

  /** The short problem-type summary, or the empty string when the document carried none. */
  readonly title: string;

  /** The sentence to show a person. */
  readonly message: string;

  /** Per-field messages, in the order the document listed them. */
  readonly fieldMessages: readonly FieldMessages[];

  /** Messages the server reported against the request as a whole rather than against a field. */
  readonly formMessages: readonly string[];

  /** The identifier a person quotes when reporting this failure, or null when the document carried none. */
  readonly supportReference: string | null;

  /** The status code the document reported, or null when it carried none. */
  readonly status: number | null;

  /** Whether any per-field or form-level message was reported. */
  readonly hasFieldMessages: boolean;
}

// ---------------------------------------------------------------------------
// BREAK-TAG NORMALISATION
// ---------------------------------------------------------------------------

/**
 * One legacy line-break tag, in any spelling the sources actually contain. Matches `<br>`, `<br/>` and
 * `<br />` case-insensitively, tolerating whitespace inside the tag, so `<BR/>`, `<Br >` and `<br / >`
 * are all recognised.
 */
const BREAK_TAG_SOURCE = '<\\s*br\\s*\\/?\\s*>';

/** One or more break tags, with any surrounding whitespace, anchored at the start. */
const LEADING_BREAKS = new RegExp(`^(?:\\s*${BREAK_TAG_SOURCE})+\\s*`, 'i');

/** One or more break tags, with any surrounding whitespace, anchored at the end. */
const TRAILING_BREAKS = new RegExp(`(?:${BREAK_TAG_SOURCE}\\s*)+$`, 'i');

/** A break tag and the horizontal whitespace hugging it, anywhere in the text. */
const INTERIOR_BREAK = new RegExp(`[ \\t]*${BREAK_TAG_SOURCE}[ \\t]*`, 'gi');

/**
 * Converts legacy message text carrying embedded line-break markup into plain text. A leading break tag
 * is stripped, a trailing run of break tags is stripped, and every remaining break tag becomes a single
 * newline character.
 *
 * @param text Message text as the server sent it, or null when there is none.
 * @returns Plain text with break markup resolved.
 */
export function stripLegacyBreakTags(text: string | null | undefined): string {
  if (text === null || text === undefined || text.length === 0) {
    return '';
  }

  const withoutLeading = text.replace(LEADING_BREAKS, '');
  const withoutTrailing = withoutLeading.replace(TRAILING_BREAKS, '');

  return withoutTrailing.replace(INTERIOR_BREAK, '\n').trim();
}

// ---------------------------------------------------------------------------
// DISCRIMINATION
// ---------------------------------------------------------------------------

/**
 * @param value A parsed response body, a problem document, or anything else.
 * @returns True when the value carries a readable per-field dictionary.
 */
export function isValidationProblemDetails(value: unknown): value is ValidationProblemDetails {
  if (!isProblemDetails(value)) {
    return false;
  }

  const candidate: unknown = value.errors;

  return typeof candidate === 'object' && candidate !== null && !Array.isArray(candidate);
}

// ---------------------------------------------------------------------------
// SEVERITY DERIVATION
// ---------------------------------------------------------------------------

/**
 * Chooses how forcefully to present a failure, from its status code. Derivation happens here because
 * nowhere else does it.
 *
 * @param status The status code from the problem document or the failed response.
 * @returns The severity to present at.
 */
export function problemSeverity(status: number | null | undefined): ProblemSeverity {
  if (status === null || status === undefined) {
    return 'error';
  }

  switch (status) {
    case 401:
      // Either the session is being renewed behind the scenes or it is gone and
      // the sign-in screen is the message. Neither is a fault.
      return 'warning';
    case 403:
      return 'warning';
    case 404:
      return 'warning';
    case 429:
      return 'info';
    default:
      return 'error';
  }
}

/**
 * The statuses at which the server is REFUSING rather than FAILING. A different question from severity,
 * and answered separately for that reason: severity says how forcefully to present a failure, whereas
 * this says whether the failure is self-explanatory to the operator who provoked it.
 */
const REFUSAL_STATUSES: readonly number[] = Object.freeze([400, 403, 404, 409, 422, 429]);

/**
 * @param status The transport status of the failed response, or null when unreadable.
 * @returns True for a refusal, false for a fault or an unanticipated status.
 */
export function isRefusalStatus(status: number | null | undefined): boolean {
  if (status === null || status === undefined) {
    return false;
  }

  return REFUSAL_STATUSES.includes(status);
}

// ---------------------------------------------------------------------------
// SUMMARY TEXT
// ---------------------------------------------------------------------------

/**
 * Resolves the one sentence to show a person for a problem document. The precedence is `detail`, then
 * `title`, then the supplied fallback, matching the rule the model file establishes: `detail` describes
 * this occurrence while `title` describes the class of failure.
 *
 * @param problem The problem document, or null when the response carried none.
 * @param fallback The sentence to use when the document supplies no usable text.
 * @returns A plain-text sentence.
 */
export function problemMessage(
  problem: ProblemDetails | null | undefined,
  fallback: string,
): string {
  const detail = stripLegacyBreakTags(problem?.detail);

  if (detail.length > 0) {
    return detail;
  }

  const title = stripLegacyBreakTags(problem?.title);

  if (title.length > 0) {
    return title;
  }

  return stripLegacyBreakTags(fallback);
}

/**
 * Reads the identifier a person should quote when reporting a failure. The correlation identifier is
 * preferred, and the preference is the whole point of this function rather than a detail of it: that
 * value is the one the server validated for the request, and it is what appears on the response header,
 * on the request envelope in the server's log and on every audit event the request produced.
 *
 * @param problem The problem document, or null.
 * @returns The identifier, or null when there is none to quote.
 */
export function problemSupportReference(
  problem: ProblemDetails | null | undefined,
): string | null {
  const correlationId = problem?.correlationId?.trim();

  if (correlationId !== undefined && correlationId.length > 0) {
    return correlationId;
  }

  const traceId = problem?.traceId?.trim();

  return traceId !== undefined && traceId.length > 0 ? traceId : null;
}

/**
 * Recovers the problem document from whatever a failed request handed to an `error` callback.
 *
 * ⚠ THIS EXISTS SO THAT AN `error` CALLBACK CANNOT SILENTLY DISCARD THE SUPPORT REFERENCE. `HttpClient`
 * delivers an `HttpErrorResponse` whose parsed body sits under `error`, so a callback written as
 * `error: () => ...` - which is how several of them were written - throws away the correlation identifier
 * the server took the trouble to send. Pairing this with {@link problemSupportReference} turns that
 * callback into one that can still quote a reference, without each call site re-deriving the unwrapping.
 *
 * It is deliberately tolerant of shape: the argument is typed `unknown` because a transport failure, a
 * parse failure and a thrown value all reach the same callback, and a wrong guess must yield `null` rather
 * than throw inside an error handler.
 *
 * @param cause Whatever the failing observable passed to its `error` callback.
 * @returns The problem document, or `null` when the value carries none.
 */
export function problemFrom(cause: unknown): ProblemDetails | null {
  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  // The `HttpErrorResponse.error` member holds the parsed body for a JSON error response.
  const body: unknown = (cause as Record<string, unknown>)['error'];

  if (isProblemDetails(body)) {
    return body;
  }

  // A document thrown directly, rather than wrapped in a transport error.
  return isProblemDetails(cause) ? cause : null;
}

/**
 * The support reference for whatever a failed request handed to an `error` callback.
 *
 * @param cause Whatever the failing observable passed to its `error` callback.
 * @returns The reference to quote, or `null` when the failure carries none.
 */
export function supportReferenceFor(cause: unknown): string | null {
  return problemSupportReference(problemFrom(cause));
}

// ---------------------------------------------------------------------------
// FIELD RESOLUTION
// ---------------------------------------------------------------------------

/** Keys the server uses for something that is not a field. */
const NON_FIELD_KEYS: readonly string[] = Object.freeze(['', '$']);

/**
 * Prefixes the server may put in front of a field name, which must be ignored when matching against a
 * form control.
 */
const FIELD_KEY_PREFIXES: readonly string[] = Object.freeze(['$.', 'request.']);

/**
 * Reduces a server field key to the form used for comparison.
 *
 * @param key A key from the per-field dictionary.
 * @returns The comparable form of the key.
 */
function comparableKey(key: string): string {
  const lowered = key.toLowerCase();

  for (const prefix of FIELD_KEY_PREFIXES) {
    if (lowered.startsWith(prefix)) {
      return lowered.slice(prefix.length);
    }
  }

  return lowered;
}

/**
 * The final segment of a dotted key, which is the field's own name. A validator reporting a nested member
 * produces a path such as `Alias.HttpAlias`, while the control on the form is named for the leaf alone.
 *
 * @param key The already-comparable form of a key.
 * @returns The last dotted segment, or the key itself when it has none.
 */
function leafKey(key: string): string {
  const separator = key.lastIndexOf('.');

  return separator === -1 ? key : key.slice(separator + 1);
}

/**
 * Extracts every per-field message from a problem document, in document order. Delegates the dictionary
 * normalisation to `problemDetailsFieldErrors`, which drops entries whose value is not a usable array of
 * messages and lower-cases the first character of each key.
 *
 * @param problem The problem document, or null.
 * @returns One entry per field that reported at least one message.
 */
export function fieldMessages(
  problem: ProblemDetails | null | undefined,
): readonly FieldMessages[] {
  const messagesByField: ProblemDetailsErrors = problemDetailsFieldErrors(problem);
  const collected: FieldMessages[] = [];

  for (const [field, rawMessages] of Object.entries(messagesByField)) {
    if (NON_FIELD_KEYS.includes(field)) {
      continue;
    }

    const messages = plainTextMessages(rawMessages);

    if (messages.length > 0) {
      collected.push({ field, messages });
    }
  }

  return collected;
}

/**
 * Messages the server reported against the request rather than against a field. Read from the keys listed
 * in {@link NON_FIELD_KEYS}, using bracket access because the dictionary is an index-signature type and
 * the workspace forbids property access on one.
 *
 * @param problem The problem document, or null.
 * @returns Plain text messages, in the order the keys are listed.
 */
export function formLevelMessages(problem: ProblemDetails | null | undefined): readonly string[] {
  const messagesByField: ProblemDetailsErrors = problemDetailsFieldErrors(problem);
  const collected: string[] = [];

  for (const key of NON_FIELD_KEYS) {
    collected.push(...plainTextMessages(messagesByField[key]));
  }

  return collected;
}

/**
 * Normalises a raw message list into plain text, discarding what cannot be shown.
 *
 * @param rawMessages Messages as the server wrote them, or undefined.
 * @returns Break tag normalised, non-blank messages.
 */
function plainTextMessages(rawMessages: readonly string[] | undefined): readonly string[] {
  if (rawMessages === undefined) {
    return [];
  }

  const collected: string[] = [];

  for (const rawMessage of rawMessages) {
    const message = stripLegacyBreakTags(rawMessage);

    if (message.length > 0) {
      collected.push(message);
    }
  }

  return collected;
}

/**
 * Every message the server reported for one form control. Matching is case-insensitive and tolerant of
 * the prefixes and nested paths the server can produce, applied in strict precedence so the result is
 * deterministic: 1. the whole key, prefix removed, compared case-insensitively; 2. failing that, the
 * final segment of a dotted key.
 *
 * @param problem The problem document, or null.
 * @param controlName The form control's name.
 * @returns Plain text messages for that control.
 */
export function fieldErrorMessages(
  problem: ProblemDetails | null | undefined,
  controlName: string | null | undefined,
): readonly string[] {
  if (controlName === null || controlName === undefined || controlName.length === 0) {
    return [];
  }

  const wanted = comparableKey(controlName);
  const exact: string[] = [];
  const byLeaf: string[] = [];

  for (const entry of fieldMessages(problem)) {
    const candidate = comparableKey(entry.field);

    if (candidate === wanted) {
      exact.push(...entry.messages);
    } else if (leafKey(candidate) === wanted) {
      byLeaf.push(...entry.messages);
    }
  }

  return exact.length > 0 ? exact : byLeaf;
}

/**
 * The single message to show beside one form control. The shared form-field component takes one string,
 * so where the server reported several this returns the FIRST. Concatenating them would overflow the
 * space a field label leaves, and the remainder stay available through {@link fieldErrorMessages} for a
 * caller that can show more.
 *
 * @param problem The problem document, or null.
 * @param controlName The form control's name.
 * @returns The message, or null when the control has none.
 */
export function fieldErrorMessage(
  problem: ProblemDetails | null | undefined,
  controlName: string | null | undefined,
): string | null {
  const messages = fieldErrorMessages(problem, controlName);

  return messages.length > 0 ? messages[0] : null;
}

// STATUS WORDING
// The wording for the statuses that `core/interceptors/error.interceptor.ts` already words is reproduced
// from it verbatim, deliberately: the same situation must not be described two different ways depending on
// which layer noticed it. The statuses it does not word are added here.

/** Shown for 400, and for any status with no more specific wording. */
export const REQUEST_REJECTED = 'The request could not be completed.';

/**
 * Shown for 401. Reproduced from the legacy `AccessDenied.Text` entry of
 * Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx, which covers exactly this case in the
 * legacy wording: "not currently logged in".
 */
export const NOT_AUTHENTICATED =
  'Either you are not currently logged in, or you do not have access to this content.';

/** Shown for 403, where the caller is known and the operation is refused. */
export const FORBIDDEN = 'You do not have permission to perform this action.';

/** Shown for 404. */
export const NOT_FOUND = 'The requested item could not be found.';

/** The status a record that is not on the server is reported at. */
const MISSING_RECORD_STATUS = 404;

/**
 * THE ONE SENTENCE SHAPE EVERY DETAIL SCREEN USES FOR AN ADDRESSED RECORD THAT IS NOT ON THE SERVER, and
 * authored here rather than four times because four copies is exactly how the four screens came to word
 * the same outcome four different ways. `entity` is the noun as a reader would say it - "portal",
 * "module", "role" - never an identifier and never a type name.
 *
 * The second clause is not padding: a reader who followed a bookmark needs to know the address was
 * understood and the record is gone, which is a different situation from an address that was never a
 * record identifier at all, and the two must not read alike.
 *
 * @param entity The record's noun, lower case.
 * @returns The shared sentence for a record that is not there.
 */
export function missingEntityMessage(entity: string): string {
  return `The ${entity} could not be found. It may have been removed.`;
}

/**
 * A SYNTHESISED 404 DOCUMENT CARRYING `message`, so a missing record is stated by the shared banner -
 * the application's single assertive owner for a failure - rather than by a paragraph the screen
 * authors for itself. ⚠ THE DISTINCTION IS AUDIBLE, NOT COSMETIC. A paragraph that appears inside a
 * branch is inserted into the document at the moment it first has something to say, and a region
 * inserted with its first message is announced inconsistently; the banner's region is already there and
 * only its contents change.
 *
 * Deliberately carries NO support reference. A record that is not there is a legitimate state rather
 * than a fault, so there is no occurrence for anyone to look up, and a diagnostic quoted against one
 * would send a reader to support for an answer support cannot give.
 *
 * @param message The sentence to state, usually from {@link missingEntityMessage}.
 * @returns A problem document the shared banner will render as a refusal.
 */
export function missingEntityProblem(message: string): ProblemDetails {
  return { status: MISSING_RECORD_STATUS, detail: message };
}

/**
 * The caption of the one way out a detail screen offers once its record has gone. `listing` is the
 * destination as the navigation names it, so the caption and the sidebar agree.
 *
 * @param listing The listing's own name.
 * @returns The recovery caption.
 */
export function missingEntityRecoveryLabel(listing: string): string {
  return `Back to ${listing}`;
}

/**
 * Introduces the server's identifier for a failed request, and it EXPLAINS the identifier rather than
 * merely labelling it. Measured finding: the bare label `Reference:` was shown on six user-facing
 * errors, where it reads as an unexplained opaque value - it means nothing without the server's logs,
 * which is exactly why it is safe to show, and equally why a reader has to be told what it is for.
 */
export const SUPPORT_REFERENCE_LEAD = 'If you report this, quote reference';

/** Shown for 409, where the record changed underneath the caller. */
export const CONFLICT =
  'This item was changed by someone else. Reload it and apply your changes again.';

/** Shown for 422, where the values submitted were understood but refused. */
export const VALIDATION_REJECTED =
  'Some of the values supplied are not valid. Review the highlighted fields and try again.';

/** Shown for 429. Calm on purpose. */
export const TOO_MANY_ATTEMPTS = 'Too many attempts. Wait a moment and try again.';

/** Shown for any status at or above 500. */
export const SERVER_ERROR = 'The server could not complete the request. Try again shortly.';

/**
 * The sentence to show for a status code when the document carries no text.
 *
 * @param status The status code, or null when the document carried none.
 * @returns A non-blank sentence.
 */
export function statusMessage(status: number | null | undefined): string {
  if (status === null || status === undefined) {
    return REQUEST_REJECTED;
  }

  switch (status) {
    // ZERO IS NOT A STATUS THE SERVER SENT. The framework reports it when no response arrived at all -
    // offline, DNS failure, a blocked cross-origin call, a cancelled navigation - so the sentence has to
    // point at the connection rather than at the request, which is the one thing the operator can act on.
    case 0:
      return NETWORK_UNREACHABLE;
    case 401:
      return NOT_AUTHENTICATED;
    case 403:
      return FORBIDDEN;
    case 404:
      return NOT_FOUND;
    case 409:
      return CONFLICT;
    case 422:
      return VALIDATION_REJECTED;
    case 429:
      return TOO_MANY_ATTEMPTS;
    default:
      return status >= 500 ? SERVER_ERROR : REQUEST_REJECTED;
  }
}

// ---------------------------------------------------------------------------
// SUMMARY
// ---------------------------------------------------------------------------

/** The sentence shown for a failure that never reached the server at all. */
export const NETWORK_UNREACHABLE =
  'The server could not be reached. Check your connection and try again.';

/** The title shown for a failure that carried no problem document. */
export const TRANSPORT_FAILURE_TITLE = 'Request failed';

/** The title shown for a failure that could not be reached at all. */
export const NETWORK_FAILURE_TITLE = 'Network error';

/**
 * Builds a well-formed problem document for a failure that carried none.
 *
 * @param status The transport status, `0` or `null` when the request never completed.
 * @param supportReference The correlation identifier, when one is known.
 * @returns A document with the same shape the server publishes.
 */
export function transportProblem(
  status: number | null | undefined,
  supportReference: string | null = null,
): ProblemDetails {
  const unreachable: boolean = status === null || status === undefined || status === 0;

  const document: ProblemDetails = {
    type: 'about:blank',
    title: unreachable ? NETWORK_FAILURE_TITLE : TRANSPORT_FAILURE_TITLE,
    status: unreachable ? 0 : (status ?? 0),
    detail: unreachable ? NETWORK_UNREACHABLE : statusMessage(status),
  };

  return supportReference === null
    ? document
    : { ...document, correlationId: supportReference };
}

/**
 * The `type` published on a contract failure. Namespaced like the server's own codes so that a consumer
 * switching on {@link failureCode} can recognise it, and so it can never collide with one of them.
 */
export const CONTRACT_FAILURE_TYPE = 'urn:dnnmigration:client:response.unreadable';

/** The {@link failureCode} form of {@link CONTRACT_FAILURE_TYPE}. */
export const CONTRACT_FAILURE_CODE = 'response.unreadable';

/** The title shown when the server answered, but in a shape this client cannot read. */
export const CONTRACT_FAILURE_TITLE = 'Unexpected response';

/**
 * The sentence shown when the server answered, but in a shape this client cannot read.
 *
 * ⚠ DELIBERATELY NOT {@link NETWORK_UNREACHABLE}, AND THE DISTINCTION IS THE WHOLE REASON THIS EXISTS. A
 * response that arrived and decoded badly and a request that never arrived at all are opposite faults with
 * opposite remedies — one is a version or contract mismatch between this bundle and the API, the other is
 * connectivity — and reporting both as "the server could not be reached" made the connectivity sentence
 * untrustworthy, because it no longer meant connectivity.
 */
export const CONTRACT_UNREADABLE =
  'The server answered in a form this application could not read, so nothing on this screen can be ' +
  'trusted. Reload the screen; if it happens again, report the details shown here.';

/**
 * Builds a well-formed problem document for a response this client could not decode.
 *
 * The document deliberately carries NO `status`. The transport succeeded, so no transport status describes
 * the fault; and `0` is already spoken for by {@link transportProblem} as "never arrived". An absent status
 * resolves to `error` severity through {@link problemSeverity} and to a non-refusal through
 * {@link isRefusalStatus}, which are both correct here.
 *
 * @param detail Wording naming what could not be read, appended to the standing sentence when supplied.
 * @param supportReference The correlation identifier, when one is known.
 * @returns A document with the same shape the server publishes.
 */
export function contractProblem(
  detail: string | null = null,
  supportReference: string | null = null,
): ProblemDetails {
  const suffix = stripLegacyBreakTags(detail);

  const document: ProblemDetails = {
    type: CONTRACT_FAILURE_TYPE,
    title: CONTRACT_FAILURE_TITLE,
    detail: suffix.length > 0 ? `${CONTRACT_UNREADABLE} (${suffix})` : CONTRACT_UNREADABLE,
  };

  return supportReference === null ? document : { ...document, correlationId: supportReference };
}

/**
 * Whether a problem document describes a response this client could not read, rather than anything the
 * server refused or failed.
 *
 * @param problem The problem document, or null.
 * @returns True for a contract failure.
 */
export function isContractProblem(problem: ProblemDetails | null | undefined): boolean {
  return problem?.type === CONTRACT_FAILURE_TYPE;
}

/**
 * Resolves everything the presentation layer needs about one failure. The single entry point a banner, a
 * notification or a signal store should use.
 *
 * ⚠ IT DOES NOT SUBSTITUTE {@link CONFLICT_MESSAGE} FOR THE DOCUMENT'S OWN SENTENCE, AND THAT RESTRAINT IS
 * DELIBERATE - IT WAS TRIED HERE AND IS WRONG. Wording every recognised conflict code from the shared
 * vocabulary at this level looks like the tidy way to make the surfaces consistent, and it breaks three
 * things, because a code's MEANING is screen-dependent while its wording is not. `module.not_portable`
 * arrives on both the export and the import screen and the legacy resources word it differently on each
 * ("exporting of content" against "importing of content"), so a central substitution silently gives one of
 * them the other's sentence. It also discards the server's own text where a screen is specifically
 * responsible for proving that text is rendered inertly, and it captures documents whose status says the
 * response never arrived. A screen that wants the legacy sentence asks {@link conflictMessage} for it, and
 * therefore chooses which of its meanings applies.
 *
 * @param problem The problem document, or null when the response carried none.
 * @param fallback Optional wording to prefer over the status-derived sentence when the document carries
 * no text of its own.
 * @returns The resolved summary.
 */
export function summarizeProblem(
  problem: ProblemDetails | null | undefined,
  fallback?: string | null,
): ProblemSummary {
  const status = typeof problem?.status === 'number' ? problem.status : null;
  const suppliedFallback = stripLegacyBreakTags(fallback);
  const resolvedFallback = suppliedFallback.length > 0 ? suppliedFallback : statusMessage(status);
  const perField = fieldMessages(problem);
  const formMessages = formLevelMessages(problem);

  return {
    severity: problemSeverity(status),
    title: stripLegacyBreakTags(problem?.title),
    message: problemMessage(problem, resolvedFallback),
    fieldMessages: perField,
    formMessages,
    supportReference: problemSupportReference(problem),
    status,
    hasFieldMessages: perField.length > 0 || formMessages.length > 0,
  };
}

/** The scheme and namespace the API puts in front of every failure code it publishes. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * Reduces a failure code to the form the server publishes. Mirrors the server's own reduction, which
 * lower-cases and folds a hyphen onto an underscore, because the services disagree about which separator
 * they use inside a reason token and the server declines to keep two spellings of one code.
 *
 * @param code A failure code in any of its spellings.
 * @returns The comparable form.
 */
function normaliseFailureCode(code: string): string {
  return code.trim().toLowerCase().replace(/-/g, '_');
}

/**
 * Reads the application failure code out of a problem document. This is the function that makes the four
 * vocabularies below usable at all.
 *
 * @param problem The problem document, or null.
 * @returns The normalised failure code, or null when the document carries none.
 */
export function failureCode(problem: ProblemDetails | null | undefined): string | null {
  const type = problem?.type?.trim();

  if (type === undefined || type.length <= FAILURE_TYPE_PREFIX.length) {
    return null;
  }

  if (!type.toLowerCase().startsWith(FAILURE_TYPE_PREFIX)) {
    return null;
  }

  const code = normaliseFailureCode(type.slice(FAILURE_TYPE_PREFIX.length));

  return code.length > 0 ? code : null;
}

// ---------------------------------------------------------------------------
// CODE VOCABULARY 1 - LOGIN AND VERIFICATION
// ---------------------------------------------------------------------------

/**
 * The verification-failure codes the sign-in flow can report. EXACTLY THREE, and the list is closed,
 * because the legacy flow had exactly three:
 * Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L168-L185 assigns only `"EnterCode"`,
 * `"InvalidCode"` and `"UserNotAuthorized"` to its outgoing message.
 */
export const AUTH_FAILURE_CODES = Object.freeze([
  /** Legacy `EnterCode`. */
  'auth.verification_required',
  /** Legacy `InvalidCode`. */
  'auth.verification_code_invalid',
  /** Legacy `UserNotAuthorized`. */
  'auth.account_not_approved',
] as const);

/** One of the three verification-failure codes. */
export type AuthFailureCode = (typeof AUTH_FAILURE_CODES)[number];

/**
 * Wording for each verification-failure code. Reproduced verbatim, Title Case included, from
 * Website/admin/Authentication/App_LocalResources/Login.ascx.resx - `EnterCode.Text` at L163,
 * `InvalidCode.Text` at L166 and `UserNotAuthorized.Text` at L223.
 */
export const AUTH_FAILURE_MESSAGE: Readonly<Record<AuthFailureCode, string>> = Object.freeze({
  'auth.verification_required': 'Enter Your Verification Code',
  'auth.verification_code_invalid': 'Invalid Verification Code',
  'auth.account_not_approved': 'You are not currently authorized to login to this site.',
});

/**
 * Narrows a string to one of the three verification-failure codes.
 *
 * @param value A code from the server, or anything else.
 * @returns True when the value is one of the three.
 */
export function isAuthFailureCode(value: string | null | undefined): value is AuthFailureCode {
  return (
    typeof value === 'string' &&
    AUTH_FAILURE_CODES.some((code) => code === normaliseFailureCode(value))
  );
}

/**
 * Wording for a verification-failure code.
 *
 * @param code The code the server reported.
 * @returns The message, or null when the code is not one of the three.
 */
export function authFailureMessage(code: string | null | undefined): string | null {
  if (typeof code !== 'string') {
    return null;
  }

  const normalised = normaliseFailureCode(code);

  return isAuthFailureCode(normalised) ? AUTH_FAILURE_MESSAGE[normalised] : null;
}

export interface VerificationPromptState {
  /**
   * Whether the verification field is already on screen. The legacy test is `If Not
   * rowVerification1.Visible` at Login.ascx.vb:L171, which is what makes the ladder progressive: the
   * first refusal reveals the field, and only a subsequent refusal can judge what was typed into it.
   */
  readonly verificationVisible: boolean;

  readonly verificationCode: string | null;

  readonly verifiedRegistration: boolean;
}

/** The outcome of one turn of the verification ladder. */
export interface VerificationPrompt {
  /** Which of the three codes applies. */
  readonly code: AuthFailureCode;

  /** The wording for that code. */
  readonly message: string;

  /**
   * Whether the caller should now reveal the verification field. True only on the turn that first reveals
   * it, mirroring the legacy assignment of `rowVerification1.Visible = True` and
   * `rowVerification2.Visible = True` at Login.ascx.vb:L173-L174.
   */
  readonly revealVerification: boolean;

  /** How forcefully to present it. */
  readonly severity: ProblemSeverity;
}

/**
 * @param state The caller's current ladder state.
 * @returns The code, wording, reveal instruction and severity for this turn.
 */
export function resolveVerificationPrompt(state: VerificationPromptState): VerificationPrompt {
  if (!state.verifiedRegistration) {
    return ladderOutcome('auth.account_not_approved', false);
  }

  if (!state.verificationVisible) {
    return ladderOutcome('auth.verification_required', true);
  }

  // Untrimmed, matching the legacy `<> ""`. Null and the empty string take the
  // same branch, and neither is rewritten into the other.
  const submitted = state.verificationCode ?? '';

  return ladderOutcome(
    submitted === '' ? 'auth.verification_required' : 'auth.verification_code_invalid',
    false,
  );
}

/**
 * Builds a ladder outcome for a code.
 *
 * @param code The code that applies.
 * @param revealVerification Whether the caller should reveal the field.
 * @returns The outcome.
 */
function ladderOutcome(code: AuthFailureCode, revealVerification: boolean): VerificationPrompt {
  return {
    code,
    message: AUTH_FAILURE_MESSAGE[code],
    revealVerification,
    severity: 'warning',
  };
}

// ---------------------------------------------------------------------------
// CODE VOCABULARY 2 - PASSWORD CHANGE
// ---------------------------------------------------------------------------

/**
 * The password-change failure codes that carry legacy wording. FIVE, and the count is the interesting
 * part.
 */
export const PASSWORD_UPDATE_CODES = Object.freeze([
  /** Legacy `PasswordMissing` (1). */
  'user.password.missing',
  /** Legacy `PasswordNotDifferent` (2). */
  'user.password.not_different',
  /** Legacy `PasswordResetFailed` (3). */
  'user.password.reset_failed',
  /** Legacy `PasswordInvalid` (4). */
  'user.password.invalid',
  /** Legacy `PasswordMismatch` (5). */
  'user.password.mismatch',
] as const);

/** One password-change failure code. */
export type PasswordUpdateCode = (typeof PASSWORD_UPDATE_CODES)[number];

export const PASSWORD_UPDATE_MESSAGE: Readonly<Record<PasswordUpdateCode, string>> =
  Object.freeze({
    'user.password.missing':
      'You must provide your current password in order to change the password.',
    'user.password.not_different':
      'The new password is the same as the old password. Please enter a different password',
    'user.password.reset_failed':
      'There was an error setting the password. The password has not been changed.',
    'user.password.invalid':
      'You must enter a valid password. Please check with the Portal Administrator if you ' +
      'do not know the password requirements.',
    'user.password.mismatch': 'The Password and Confirmation Passwords do not match',
  });

/**
 * Narrows a string to a password-change failure code.
 *
 * @param value A code from the server, or anything else.
 * @returns True when the value names one of the five codes.
 */
export function isPasswordUpdateCode(
  value: string | null | undefined,
): value is PasswordUpdateCode {
  return (
    typeof value === 'string' &&
    PASSWORD_UPDATE_CODES.some((code) => code === normaliseFailureCode(value))
  );
}

/**
 * Wording for a password-change failure code.
 *
 * @param code The code the server reported, as {@link failureCode} returns it.
 * @returns The message, or null for a code this vocabulary does not word.
 */
export function passwordUpdateMessage(code: string | null | undefined): string | null {
  if (typeof code !== 'string') {
    return null;
  }

  const normalised = normaliseFailureCode(code);

  return isPasswordUpdateCode(normalised) ? PASSWORD_UPDATE_MESSAGE[normalised] : null;
}

// ---------------------------------------------------------------------------
// CODE VOCABULARY 3 - USER CREATION
// ---------------------------------------------------------------------------

/** The account-creation failure codes. TEN, which is what the creation path actually emits. */
export const USER_CREATE_CODES = Object.freeze([
  /** Legacy `UsernameAlreadyExists` (1). */
  'user.create.username_already_exists',
  /** Legacy `UserAlreadyRegistered` (2). */
  'user.create.user_already_registered',
  /** Legacy `DuplicateEmail` (3). */
  'user.create.duplicate_email',
  /** Legacy `DuplicateUserName` (5). */
  'user.create.duplicate_username',
  /** Legacy `InvalidEmail` (7). */
  'user.create.invalid_email',
  /** Legacy `InvalidPassword` (8). */
  'user.create.invalid_password',
  /** Legacy `InvalidUserName` (11). */
  'user.create.invalid_username',
  /** Legacy `ProviderError` (12), `UnexpectedError` (14) and the two provider-key members. */
  'user.create.provider_error',
  /** Legacy `PasswordMismatch` (16). */
  'user.create.password_mismatch',
  /** Legacy `AddUserToPortal` (17), whose step can now fail reportably. */
  'user.create.portal_assignment_failed',
] as const);

/** One account-creation failure code. */
export type UserCreateCode = (typeof USER_CREATE_CODES)[number];

/** Wording for each account-creation failure code. */
const USER_NAME_EXISTS =
  'A User Already Exists For the Username Specified. Please Register Again Using A ' +
  'Different Username.';

const USER_EMAIL_EXISTS =
  'A user already exists for the email address specified. Please login using the ' +
  'registered account of that email address.';

const REGISTRATION_ERROR =
  'An Unexpected Error Occurred During Registration. Please Contact The Portal ' +
  'Administrator For Futher Information.';

export const USER_CREATE_MESSAGE: Readonly<Record<UserCreateCode, string>> = Object.freeze({
  'user.create.username_already_exists': USER_NAME_EXISTS,
  'user.create.user_already_registered': USER_NAME_EXISTS,
  'user.create.duplicate_username': USER_NAME_EXISTS,
  'user.create.duplicate_email': USER_EMAIL_EXISTS,
  'user.create.invalid_email':
    'The email address specified is invalid. Please specify a valid email address.',
  // MIGRATION: the legacy wording continues "Passwords must be at least [PasswordLength] characters in
  // length and contain at least [NoneAlphabet] non-alphanumeric characters.", with both tokens replaced at
  // run time from the membership provider's configuration.
  'user.create.invalid_password':
    'The password specified is invalid. Please specify a valid password.',
  'user.create.invalid_username':
    'The username specified is invalid. Please specify a valid username.',
  // One code for four legacy provider-fault members, which the legacy translator had
  // already reduced to one message.
  'user.create.provider_error': REGISTRATION_ERROR,
  'user.create.password_mismatch': 'The Password and Confirmation Passwords do not match',
  'user.create.portal_assignment_failed': REGISTRATION_ERROR,
});

/**
 * Narrows a string to an account-creation failure code.
 *
 * @param value A code from the server, or anything else.
 * @returns True when the value names one of the ten codes.
 */
export function isUserCreateCode(value: string | null | undefined): value is UserCreateCode {
  return (
    typeof value === 'string' &&
    USER_CREATE_CODES.some((code) => code === normaliseFailureCode(value))
  );
}

/**
 * Wording for an account-creation failure code.
 *
 * @param code The code the server reported, as {@link failureCode} returns it.
 * @returns The message, or null for a code this vocabulary does not word.
 */
export function userCreateMessage(code: string | null | undefined): string | null {
  if (typeof code !== 'string') {
    return null;
  }

  const normalised = normaliseFailureCode(code);

  return isUserCreateCode(normalised) ? USER_CREATE_MESSAGE[normalised] : null;
}

// ---------------------------------------------------------------------------
// CODE VOCABULARY 4 - CONFLICTS
// ---------------------------------------------------------------------------

export const CONFLICT_CODES = Object.freeze([
  /** Legacy `DuplicatePortalAlias`, and `DuplicateAlias` with it. */
  'portal.alias_duplicate',
  /**
   * The alias the current request resolved the tenant through, refused for a rename and for an unbinding
   * alike. NO LEGACY RESOURCE KEY, for the reason on {@link CONFLICT_MESSAGE}.
   */
  'portal.alias_in_use.conflict',
  /** Legacy `LastPortal`. */
  'portal.last_remaining',
  /** Legacy `DuplicateRole`. */
  'role.name_duplicate',
  /** Legacy `DuplicateRoleGroup`. */
  'role_group.name_duplicate',
  /** No legacy antecedent; authored. Arrives as `409` from the `in_use` token. */
  'role_group.in_use',
  /** Legacy `RoleRemoveError`. */
  'role_assignment.protected',
  /** Legacy `InvalidTabName`, the reserved-device-name refusal. */
  'tab.name_reserved',
  /** Legacy `NotValidXml`. */
  'module.content_invalid',
  /** Legacy `NotCorrectType`. */
  'module.content_type_mismatch',
  /** Legacy `ImportNotSupported`. */
  'module.not_portable',
] as const);

/** One state-refusal code. */
export type ConflictCode = (typeof CONFLICT_CODES)[number];

/**
 * Wording for each state-refusal code, from the legacy resource files. The legacy page-name conflict
 * sentence misspells "hierarchy" as "heirarchy".
 */
export const CONFLICT_MESSAGE: Readonly<Record<ConflictCode, string>> = Object.freeze({
  'portal.alias_duplicate':
    'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
  // MIGRATION: AUTHORED WORDING, because no legacy sentence exists to reproduce. The legacy screen did not
  // refuse this - it HID the affordance, at Website/admin/Portal/PortalAlias.ascx.vb:L51-L60, so no
  // resource key was ever needed for a refusal that could not be reached from the console.
  'portal.alias_in_use.conflict':
    'This is the host name your request reached this portal through, so it cannot be changed or ' +
    'removed. Reach the portal through one of its other host names and try again.',
  'portal.last_remaining': 'You Can Not Delete The Last Portal In Your Database',
  'role.name_duplicate': 'A role with the same name already exists. The role was not added.',
  'role_group.name_duplicate':
    'A role group with the same name already exists. The new group was not added.',
  'role_group.in_use':
    'That role group still contains roles, so it was not removed. Move or delete its roles first.',
  'role_assignment.protected':
    'You Can Not Remove The Portal Administrator Or The Registered Users Role',
  'tab.name_reserved': 'This is an invalid Page Name',
  'module.content_invalid': 'The file you selected does not contain a valid XML structure',
  'module.content_type_mismatch':
    'The import file specified is not the correct type for this module',
  'module.not_portable': 'The module selected does not support the importing of content',
});

/**
 * Narrows a string to a state-refusal code.
 *
 * @param value A code from the server, or anything else.
 * @returns True when the value names one of the ten codes.
 */
export function isConflictCode(value: string | null | undefined): value is ConflictCode {
  return (
    typeof value === 'string' &&
    CONFLICT_CODES.some((code) => code === normaliseFailureCode(value))
  );
}

/**
 * Wording for a state-refusal code.
 *
 * @param code The code the server reported, as {@link failureCode} returns it.
 * @returns The message, or null when the code is not one of the ten.
 */
export function conflictMessage(code: string | null | undefined): string | null {
  if (typeof code !== 'string') {
    return null;
  }

  const normalised = normaliseFailureCode(code);

  return isConflictCode(normalised) ? CONFLICT_MESSAGE[normalised] : null;
}

/**
 * Whether a narrowed refusal code is the duplicate-host-name refusal.
 *
 * @param code A refusal code a store has already narrowed, or null.
 * @returns True for the duplicate-host-name refusal and false for anything else.
 */
export function isDuplicateAliasCode(code: ConflictCode | null): boolean {
  return code === 'portal.alias_duplicate';
}

/**
 * Whether a narrowed refusal code is the refusal to write the alias the current request resolved the
 * tenant through.
 *
 * @param code A refusal code a store has already narrowed, or null.
 * @returns True for the active-alias refusal and false for anything else.
 */
// Both alias endpoints can now answer `409` for TWO different reasons, so a status is no longer diagnostic
// on that screen and the code has to be read. See {@link isDuplicateAliasCode} for why the comparison lives
// here.
export function isAliasInUseCode(code: ConflictCode | null): boolean {
  return code === 'portal.alias_in_use.conflict';
}

// ---------------------------------------------------------------------------
// ADVISORY WORDING
// ---------------------------------------------------------------------------

/**
 * Advisories that interrupt a sign-in without refusing it. The legacy `UserValidStatus` enumeration
 * carried these as five members - `VALID`, `PASSWORDEXPIRED`, `PASSWORDEXPIRING`, `UPDATEPROFILE` and
 * `UPDATEPASSWORD` - with `VALID` at 0.
 */
export const ADVISORY_MESSAGE = Object.freeze({
  PasswordExpired: 'Your password expired on {0}. Please update your password before continuing.',
  PasswordExpiring:
    'Your password will expire on {0}. Please update your password before continuing.',
  PasswordUpdate: 'Please update your password before continuing.',
  ProfileUpdate: 'Please update your profile before continuing.',
});

/** One advisory. */
export type AdvisoryCode = keyof typeof ADVISORY_MESSAGE;

/**
 * Wording for an advisory, with the date placeholder resolved.
 *
 * @param code The advisory to word.
 * @param formattedDate An already-formatted date for the two expiry advisories.
 * @returns The message.
 */
export function advisoryMessage(code: AdvisoryCode, formattedDate?: string | null): string {
  const template = ADVISORY_MESSAGE[code];

  if (!template.includes(DATE_PLACEHOLDER)) {
    return template;
  }

  const date = formattedDate === null || formattedDate === undefined ? '' : formattedDate.trim();

  if (date.length === 0) {
    // Both dated advisories concern the password, so the undated password
    // advisory is the correct thing to say when there is no date to name.
    return ADVISORY_MESSAGE.PasswordUpdate;
  }

  return template.replace(DATE_PLACEHOLDER, date);
}

/** The substitution token the legacy expiry wording carries. */
const DATE_PLACEHOLDER = '{0}';
