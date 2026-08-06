//
// Error-presentation helpers for the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// WHAT THIS MODULE IS
// ---------------------------------------------------------------------------
// A set of pure functions that turn an RFC 7807 problem document into the exact
// shapes the presentation layer consumes: one summary sentence for a banner or a
// notification, one message per form control, a severity, and the diagnostic
// identifier an operator needs to find the matching server-side log entry.
//
// It is deliberately framework-free. There is no class, no decorator, no
// dependency lookup, no reactive primitive and no module-level mutable state.
// Every export is a function over immutable values or a frozen lookup table, so
// the whole module is exercisable with a bare specification file and no testing
// module. It also performs no request and unwraps no transport error: the
// transport layer is `core/interceptors/error.interceptor.ts`, which reads the
// failed response and hands over an ALREADY-PARSED body plus a plain numeric
// status. Keeping that boundary is what lets this file stay free of the browser
// and of the framework entirely.
//
// ---------------------------------------------------------------------------
// EVERY VALUE THIS MODULE RETURNS IS PLAIN TEXT. THIS IS A SECURITY BOUNDARY.
// ---------------------------------------------------------------------------
// Not a stylistic preference, and not a theoretical risk - it was measured. The
// 37 in-scope legacy resource files under Website/admin/*/App_LocalResources
// hold 1211 `<data>` entries, of which 1136 are plain strings and 1132 are
// non-empty. Unescaping the XML entities first (a naive search finds nothing,
// because the markup is stored escaped, and would wrongly conclude the risk is
// absent) shows 76 of those values carry an HTML tag. The tag histogram is
// `li 60 | p 58 | br 57 | h1 42 | a 38 | strong 34 | b 32 | span 10 | ul 10 |
// h3 6 | script 4 | h4 4` - four SCRIPT tags among them, one of which is a live
// javascript block in the `Advertising.Text` entry of
// Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx.
//
// That text is the source of the wording the API returns, so message text
// arriving from the server is untrusted markup. Consequently this module exposes
// no pre-escaped member, no trusted-markup member and no member whose name
// suggests either. It returns strings; the template escapes them. The framework's
// default text interpolation does that by construction, and the legacy
// application reached the same conclusion by hand:
// Website/admin/Security/AccessDenied.ascx.vb:L43 renders its untrusted
// query-string message through `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(..))`
// before display. A member offering "already-safe" markup would invite a consumer
// to bind it into a raw-markup sink and would move an escaping decision into a
// type declaration, where no reviewer would see it.
//
// ---------------------------------------------------------------------------
// LEGACY PROVENANCE
// ---------------------------------------------------------------------------
// The direct antecedent is `UserController.GetUserCreateStatus`
// (Library/Components/Users/UserController.vb:L598), a server-side translator
// from a status enumeration to a resource string, called at
// Website/admin/Users/User.ascx.vb:L187. The four code vocabularies below are
// ported from that function and its siblings, with the resource wording taken
// from Website/App_GlobalResources/SharedResources.resx - which is what
// `Localization.GetString(key)` with no resource-file argument resolves against.
//
// MIGRATION: localisation is NOT ported. The legacy mechanism is a Web Forms
// resource-provider feature with no counterpart here, so the English wording is
// authored inline with the resource files as the reference, and no translation
// runtime is introduced. Where a legacy string is reproduced it is reproduced
// verbatim, including its Title Case, so existing operators recognise it.
//
// MIGRATION: `GetUserCreateStatus` ends with a `Case Else` that THROWS
// `ArgumentException("Unknown UserCreateStatus value encountered")`. Throwing is
// not reproduced. A display adapter that throws on an input it does not
// recognise converts a cosmetic gap into a broken screen, and the three statuses
// that reach that branch - `AddUser`, `Success` and `AddUserToPortal` - are not
// failures at all. They resolve to null here, and every accessor is total.
//

import { isProblemDetails, problemDetailsFieldErrors } from '../models/problem-details.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
  ValidationProblemDetails,
} from '../models/problem-details.model';

// The legacy `UserCreateStatus` enumeration is deliberately NOT imported. This module
// once derived its creation vocabulary from it, which was correct while the vocabulary
// was legacy member NAMES - but the vocabulary is now the failure codes the API
// publishes, and those are not derivable from the enumeration's member names. The
// enumeration remains declared in `core/models/user.model.ts`, is cited by name in the
// commentary on that vocabulary, and its ordinals continue to be pinned by that model's
// own specification. Importing it here to leave it unused would only re-create the
// second-definition problem this module already removed once, for the login statuses.

// ---------------------------------------------------------------------------
// SEVERITY
// ---------------------------------------------------------------------------

/**
 * How forcefully a failure should be presented.
 *
 * The three spellings are a deliberate subset of the notification vocabulary in
 * `core/services/notification.service.ts`, so a value produced here can be
 * handed straight to that service without a translation step. `'success'` is
 * absent because a problem document never describes one.
 *
 * The distinction between `'warning'` and `'error'` is load-bearing rather than
 * decorative, and it is measured. Across the in-scope legacy administration
 * code-behinds the three message types are used 27 times as `RedError`, 21 times
 * as `YellowWarning` and 12 times as `GreenSuccess`, so the legacy application
 * genuinely distinguished a refusal from a fault, and so does this.
 */
export type ProblemSeverity = 'error' | 'warning' | 'info';

// ---------------------------------------------------------------------------
// RESULT SHAPES
// ---------------------------------------------------------------------------

/**
 * The messages the server reported against one named field.
 *
 * `field` is the key as it will be matched against a form control - the shape
 * produced by `problemDetailsFieldErrors`, whose first character is lower-cased.
 * `messages` is never empty; an entry with no usable message is dropped rather
 * than represented.
 */
export interface FieldMessages {
  /** The field the messages belong to. */
  readonly field: string;

  /** One or more plain-text messages. Never empty. */
  readonly messages: readonly string[];
}

/**
 * Everything the presentation layer needs about one failure, resolved once.
 *
 * Assembled to feed the shared components described by the migration plan
 * without further work at the call site: `error-banner` renders `title`,
 * `message` and `fieldMessages`; `form-field` takes a single string per control,
 * which {@link fieldErrorMessage} selects from the same data; a signal store
 * keeps `message` and `severity` for its own error slice.
 *
 * Every string member is plain text, break-tag normalised, and safe to bind as
 * text. No member is markup.
 */
export interface ProblemSummary {
  /** How forcefully to present the failure. */
  readonly severity: ProblemSeverity;

  /**
   * The short problem-type summary, or the empty string when the document
   * carried none.
   *
   * The empty string rather than null, so a template binds it without rendering
   * the word "null" when it is absent.
   */
  readonly title: string;

  /** The sentence to show a person. Never blank. */
  readonly message: string;

  /** Per-field messages, in the order the document listed them. */
  readonly fieldMessages: readonly FieldMessages[];

  /**
   * Messages the server reported against the request as a whole rather than
   * against a field.
   *
   * These arrive under a key that is not a field name - the empty string, which
   * the validation bridge uses when a rule reports no property, or `$`, which a
   * malformed request body produces. They belong beside the summary, because
   * there is no control to attach them to.
   */
  readonly formMessages: readonly string[];

  /**
   * The identifier a person quotes when reporting this failure, or null when the
   * document carried none.
   *
   * Surfaced because it is the only join key between something a person saw in
   * the browser and the request as the server recorded it. It is the correlation
   * identifier the server validated for the request, which is the value that
   * appears on the response header, on the request envelope in the server's log
   * and on every audit event the request produced; the W3C trace identifier is
   * used only when no correlation identifier is present, because it appears in
   * none of those records. It is diagnostic: quote it in a report, do not present
   * it as an explanation.
   */
  readonly supportReference: string | null;

  /** The status code the document reported, or null when it carried none. */
  readonly status: number | null;

  /**
   * Whether any per-field or form-level message was reported.
   *
   * The marker a caller uses to decide whether a form is already showing the
   * failure beside its fields, in which case announcing it again says the same
   * thing twice.
   */
  readonly hasFieldMessages: boolean;
}

// ---------------------------------------------------------------------------
// BREAK-TAG NORMALISATION
// ---------------------------------------------------------------------------

/**
 * One legacy line-break tag, in any spelling the sources actually contain.
 *
 * Matches `<br>`, `<br/>` and `<br />` case-insensitively, tolerating whitespace
 * inside the tag, so `<BR/>`, `<Br >` and `<br  / >` are all recognised. It is
 * anchored tightly enough that it cannot match an unrelated element: the closing
 * angle bracket must follow `br` with nothing between the two but optional
 * whitespace and one optional solidus, so `<brochure>` and `<br-x>` do not
 * match.
 *
 * No markup parser is used and the document is never touched. A regular
 * expression is the whole mechanism, deliberately: parsing untrusted markup in
 * order to display it as text would be a larger attack surface than the problem
 * warrants.
 */
const BREAK_TAG_SOURCE = '<\\s*br\\s*\\/?\\s*>';

/** One or more break tags, with any surrounding whitespace, anchored at the start. */
const LEADING_BREAKS = new RegExp(`^(?:\\s*${BREAK_TAG_SOURCE})+\\s*`, 'i');

/** One or more break tags, with any surrounding whitespace, anchored at the end. */
const TRAILING_BREAKS = new RegExp(`(?:${BREAK_TAG_SOURCE}\\s*)+$`, 'i');

/**
 * A break tag and the horizontal whitespace hugging it, anywhere in the text.
 *
 * Horizontal whitespace only. Absorbing an existing newline as well would merge
 * two authored lines into one.
 */
const INTERIOR_BREAK = new RegExp(`[ \\t]*${BREAK_TAG_SOURCE}[ \\t]*`, 'gi');

/**
 * Converts legacy message text carrying embedded line-break markup into plain
 * text.
 *
 * A leading break tag is stripped, a trailing run of break tags is stripped, and
 * every remaining break tag becomes a single newline character. One tag yields
 * one newline, so `<br><br>` becomes a blank line exactly as it rendered before.
 * The result is trimmed.
 *
 * WHY THIS IS NEEDED, AND WHY IT IS THIS MODULE'S JOB. The legacy wording carries
 * the break markup inside the message itself, in both spellings:
 *
 * - `Website/admin/Portal/Signup.ascx.vb:L193` appends `"<br>"` plus a resource
 *   string, and it does so INSIDE A PER-CHARACTER LOOP over the portal name, so a
 *   single submission repeats the identical fragment once per invalid character.
 *   L214 is the same construct for the parent-portal branch and L221 appends
 *   another for a password mismatch. L318 then embeds the whole accumulation in a
 *   second resource template, and L323 assigns
 *   `"<br>" & strMessage & "<br><br>"` - a leading break and a trailing pair.
 * - `Website/admin/Users/User.ascx.vb:L187` assigns
 *   `"<br/>" + UserController.GetUserCreateStatus(createStatus)`, the other
 *   spelling.
 *
 * The markup is also in the resource DATA, not only in the code that reads it.
 * Across the 37 in-scope administration resource files the escaped forms occur as
 * `<br>` 73 times, `<br/>` 13 times and `<br />` twice, which is why the spaced
 * spelling is handled as a certainty rather than a precaution. Interior and
 * trailing runs are equally real: the `PortalType` and `Description` entries of
 * Signup.ascx.resx carry `<br><br>` both between paragraphs and at the very end.
 *
 * The API passes this text through untouched, by design and by documented
 * intent - its problem-details factory states that nothing there "trims,
 * re-cases, re-keys, encodes, deduplicates, reorders or otherwise edits a
 * message or a field name", and notes that whether the break prefix survives is
 * a display decision. This function is where that decision is taken.
 *
 * MIGRATION: the break tags are removed rather than rendered. Rendering them
 * would require binding message text as markup, which the module header rules
 * out on measured evidence. Presenting them literally would show a person
 * `<br>You Must Enter a Valid Name`. Neither is acceptable, so the markup is
 * translated into the plain-text equivalent it always meant.
 *
 * MIGRATION: only break tags are handled. A value that also carries `<b>`,
 * `<li>` or `<a>` keeps those characters as literal text. Stripping them would
 * mean parsing untrusted markup, which this file refuses to do, and the affected
 * values are long-form help text rather than failure messages. The tags are
 * therefore visible-but-inert rather than silently interpreted.
 *
 * MIGRATION: a null input and an empty input are treated identically, both
 * yielding the empty string. The legacy null contract makes them the same value:
 * `Library/Components/Shared/Null.vb:L71-L75` defines the string "absent" marker
 * with a body of literally `Return ""`, and Signup.ascx.vb tests the same
 * variable for emptiness two different ways in one file - `= ""` at L227 and
 * `= Null.NullString` at L315. Neither spelling is normalised into the other;
 * they simply take the same branch, as they always did.
 *
 * @param text Message text as the server sent it, or null when there is none.
 * @returns Plain text with break markup resolved. Empty when there is nothing to
 * show.
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
 * Narrows a value to a {@link ValidationProblemDetails} - a problem document that
 * is known to carry the per-field dictionary.
 *
 * This guard lives here rather than beside the type on purpose. The model file
 * declares the SHAPE of the error contract and deliberately leaves
 * discrimination to the two places that actually branch on it: the error
 * interceptor and this module. Declaring it twice would give one rule two
 * definitions that could drift apart.
 *
 * Accepts `unknown` so one function serves both callers. A caller holding a
 * parsed body of unknown shape gets the full check; a caller already holding a
 * {@link ProblemDetails} gets narrowing to the stricter type, because that type
 * is assignable to `unknown`.
 *
 * The test is: is this readable as a problem document at all, and is `errors` an
 * object rather than an array or null? The base check is delegated to
 * `isProblemDetails` so that the two guards cannot disagree about what a problem
 * document is.
 *
 * An EMPTY dictionary passes, and that is correct rather than an oversight. The
 * API emits `errors` as `{}` when model state carries no entries, so presence of
 * the member and presence of a message are different questions. This guard
 * answers the first; {@link ProblemSummary.hasFieldMessages} answers the second.
 *
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
 * Chooses how forcefully to present a failure, from its status code.
 *
 * Derivation happens here because nowhere else does it. The notification service
 * states plainly that callers choose the severity and that nothing there derives
 * one, and the presentation components take a severity as given. This function is
 * that missing step.
 *
 * MIGRATION: a permission refusal is a WARNING, not an error, and the legacy
 * application is the authority for that. `AccessDenied.ascx.vb` contains no
 * permission check at all - it only presents a denial - and BOTH of its branches
 * use `YellowWarning`: L43 for the message supplied through the `message`
 * query-string key, and L45 for the localised default. Presenting a refusal in
 * danger styling would tell a person something is broken when the system is
 * working exactly as configured.
 *
 * A rate-limit refusal is likewise a warning. Nothing has failed - the caller is
 * early - and {@link STATUS_MESSAGE} pairs it with wording that says so.
 *
 * A conflict is an error, and that too is measured rather than assumed: the
 * conflict wording in the legacy administration pages is surfaced through
 * `RedError` at every site that names a message type, across `DuplicateAlias`
 * (twice), `DuplicateRole`, `DuplicateRoleGroup`, `TabExists` and
 * `InvalidTabName`.
 *
 * The `default` arm is mandatory and not merely defensive. A status is a plain
 * number chosen by the server, the API's own status vocabulary covers thirteen
 * of them, and the workspace forbids an unhandled case. An unrecognised status -
 * including the zero the transport layer reports when no response arrived at all,
 * and an absent status - resolves to `'error'`, because a failure nobody
 * anticipated is the one most worth showing.
 *
 * ⚠ THIS IS THE ONLY PLACE A RESPONSE STATUS BECOMES A SEVERITY. It was not, and the
 * divergence was visible to an operator: the shared error banner used to intercept a
 * rate-limit refusal ahead of this function and paint it in its calmest band, so the
 * same 429 was announced as a warning by the interceptor and shown as a calm notice by
 * the banner on the same screen. Rather than delete the banner's intent, the intent is
 * moved HERE, where every surface can see it: a rate-limit refusal now resolves to
 * `'info'`, and each surface maps that member onto its own quietest band. No consumer
 * may re-derive or override the result - a surface that disagrees with this function
 * must change this function, so that the disagreement is resolved once for all of them.
 *
 * That also makes {@link ProblemSeverity} total: `'info'` was declared by the type and
 * returned by nothing, and the rate-limit case is precisely what it was declared for.
 * {@link STATUS_MESSAGE} already words that status as "Calm on purpose", so this aligns
 * the severity with wording that has always said what it wanted.
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
      // The quietest of the three, and quieter than the other refusals on purpose:
      // nothing was rejected on its merits and nothing is misconfigured - the caller is
      // simply early, and the only action is to wait.
      return 'info';
    default:
      return 'error';
  }
}

/**
 * The statuses at which the server is REFUSING rather than FAILING.
 *
 * A different question from severity, and answered separately for that reason: severity
 * says how forcefully to present a failure, whereas this says whether the failure is
 * self-explanatory to the operator who provoked it. A refusal is - a duplicate name, a
 * missing record, a permission they do not hold, a rate limit they have reached - and a
 * fault is not.
 *
 * The two answers are related but not derivable from one another. A validation refusal
 * and a conflict are both refusals here while resolving to `'error'` in
 * {@link problemSeverity}, on the measured legacy evidence recorded there, so deriving
 * one from the other would force one of the two rules to be wrong.
 *
 * 401 is absent deliberately. It is the one status no surface words at all: the session
 * is either being renewed behind the scenes or gone, and the sign-in screen is the
 * message.
 *
 * Typed `readonly number[]` rather than a literal tuple on purpose. A frozen tuple of
 * literal types would narrow `includes` to accept only those six literals and would
 * reject the plain `number` a server actually sends, which is strict typing producing
 * exactly the unsafety it exists to remove.
 */
const REFUSAL_STATUSES: readonly number[] = Object.freeze([400, 403, 404, 409, 422, 429]);

/**
 * Whether a status means the server refused the request rather than failed it.
 *
 * Declared here, beside {@link problemSeverity}, because both classify a response status
 * and a reader deciding how to treat a failure needs to see the two rules together. It
 * previously lived privately inside the error interceptor, which made it invisible to
 * every other surface and made the interceptor look like a second classification
 * authority; only the response path uses it today, but where it LIVES is what decides
 * whether the next surface that needs it reaches for this one or writes a third.
 *
 * An absent status resolves to false, matching {@link problemSeverity}'s treatment of the
 * same input: a failure whose status cannot be read cannot be shown to be self-explanatory,
 * and the safe answer is to treat it as a fault worth diagnosing.
 *
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
 * Resolves the one sentence to show a person for a problem document.
 *
 * The precedence is `detail`, then `title`, then the supplied fallback, matching
 * the rule the model file establishes: `detail` describes this occurrence while
 * `title` describes the class of failure. Blank is treated as absent, so a server
 * that writes an empty string does not produce a message-less banner.
 *
 * The precedence is re-expressed here rather than delegated to
 * `problemDetailsMessage`, and the reason is specific. That function tests
 * blankness on the RAW member, whereas break-tag removal has to happen FIRST:
 * a `detail` of `"<br>"` is not blank raw, so delegating would select it, strip
 * it to nothing, and discard a perfectly usable `title`. Stripping before the
 * blank test is the only ordering that cannot lose text.
 *
 * Per-field messages are deliberately not folded in. A field message belongs
 * beside its field; concatenating them here would produce an unreadable sentence
 * and repeat what the form already shows.
 *
 * @param problem The problem document, or null when the response carried none.
 * @param fallback The sentence to use when the document supplies no usable text.
 * @returns A plain-text sentence. Never blank, provided the fallback is not.
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
 * Reads the identifier a person should quote when reporting a failure.
 *
 * The correlation identifier is preferred, and the preference is the whole point of
 * this function rather than a detail of it: that value is the one the server
 * validated for the request, and it is what appears on the response header, on the
 * request envelope in the server's log and on every audit event the request
 * produced. The W3C trace identifier is a fallback only. It is taken from whatever
 * diagnostic activity happened to be current, so it appears in none of those
 * records, and quoting it produced a reference an operator could not find - which
 * is what this function did before the server published the correlation identifier
 * in the body.
 *
 * Absence of both is ordinary and not a fault: RFC 7807 makes every member
 * optional, and a proxy between the browser and the API can return a document this
 * application never produced. A blank value is reported as absent, because a blank
 * identifier joins nothing to nothing.
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

// ---------------------------------------------------------------------------
// FIELD RESOLUTION
// ---------------------------------------------------------------------------

/**
 * Keys the server uses for something that is not a field.
 *
 * `''` is what the validation bridge writes when a rule reports no property
 * name - it calls the model-state collector with `failure.PropertyName ?? ""`,
 * so a rule about the request as a whole lands under the empty key. `'$'` is
 * what the JSON body binder writes when the payload itself could not be read.
 * Neither has a control to sit beside.
 */
const NON_FIELD_KEYS: readonly string[] = Object.freeze(['', '$']);

/**
 * Prefixes the server may put in front of a field name, which must be ignored
 * when matching against a form control.
 *
 * `$.` is the JSON path the body binder produces, and `request.` is the binder
 * prefix for a parameter named `request`. Neither is part of the field's name,
 * and a form control is never named with either.
 */
const FIELD_KEY_PREFIXES: readonly string[] = Object.freeze(['$.', 'request.']);

/**
 * Reduces a server field key to the form used for comparison.
 *
 * Lower-cases, and removes one leading binder prefix if present. Case is folded
 * because the server writes model property names in their own casing and does not
 * re-case them - its factory is explicit that nothing there re-cases or re-keys a
 * field name - while a form control is named in the client's casing. Matching
 * exactly would therefore fail for the majority of fields.
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
 * The final segment of a dotted key, which is the field's own name.
 *
 * A validator reporting a nested member produces a path such as
 * `Alias.HttpAlias`, while the control on the form is named for the leaf alone.
 * Used only as a lower-precedence fallback, because two different paths can share
 * a leaf.
 *
 * @param key The already-comparable form of a key.
 * @returns The last dotted segment, or the key itself when it has none.
 */
function leafKey(key: string): string {
  const separator = key.lastIndexOf('.');

  return separator === -1 ? key : key.slice(separator + 1);
}

/**
 * Extracts every per-field message from a problem document, in document order.
 *
 * Delegates the dictionary normalisation to `problemDetailsFieldErrors`, which
 * drops entries whose value is not a usable array of messages and lower-cases the
 * first character of each key. Each surviving message is then break-tag
 * normalised, and an entry left with no message is dropped.
 *
 * Keys that are not field names are excluded - they are reported separately by
 * {@link formLevelMessages}, because they have no control to sit beside.
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
 * Messages the server reported against the request rather than against a field.
 *
 * Read from the keys listed in {@link NON_FIELD_KEYS}, using bracket access
 * because the dictionary is an index-signature type and the workspace forbids
 * property access on one.
 *
 * @param problem The problem document, or null.
 * @returns Plain-text messages, in the order the keys are listed. Possibly empty.
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
 * @returns Break-tag normalised, non-blank messages.
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
 * Every message the server reported for one form control.
 *
 * Matching is case-insensitive and tolerant of the prefixes and nested paths the
 * server can produce, applied in strict precedence so the result is
 * deterministic:
 *
 * 1. the whole key, prefix removed, compared case-insensitively;
 * 2. failing that, the final segment of a dotted key.
 *
 * Only one tier contributes. Falling through to the leaf tier while an exact
 * match exists would let `Portal.Name` add its messages to a control that
 * `Name` already answered for.
 *
 * @param problem The problem document, or null.
 * @param controlName The form control's name.
 * @returns Plain-text messages for that control. Empty when it has none.
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
 * The single message to show beside one form control.
 *
 * The shared form-field component takes one string, so where the server reported
 * several this returns the FIRST. Concatenating them would overflow the space a
 * field label leaves, and the remainder stay available through
 * {@link fieldErrorMessages} for a caller that can show more.
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

  // The length test is the presence proof, so no second fallback is needed and
  // none is written: an unreachable `?? null` would be dead code carrying a branch
  // that can never be exercised.
  return messages.length > 0 ? messages[0] : null;
}


// ---------------------------------------------------------------------------
// STATUS WORDING
// ---------------------------------------------------------------------------
//
// Used only when the document carries neither a `detail` nor a `title`. The API
// fills both unconditionally at every status code, so in practice these appear
// for a body this application did not produce - a proxy or gateway answering on
// its own behalf. They are authored as named constants rather than written inline
// so that a specification can assert the exact wording, and so that punctuation
// and spacing survive being bound into a template.
//
// The wording for the statuses that `core/interceptors/error.interceptor.ts`
// already words is reproduced from it verbatim, deliberately: the same situation
// must not be described two different ways depending on which layer noticed it.
// The statuses it does not word are added here.

/** Shown for 400, and for any status with no more specific wording. */
export const REQUEST_REJECTED = 'The request could not be completed.';

/**
 * Shown for 401.
 *
 * Reproduced from the legacy `AccessDenied.Text` entry of
 * Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx, which covers
 * exactly this case in the legacy wording: "not currently logged in".
 *
 * MIGRATION: deliberately NON-DISCLOSING, and this is a security decision rather
 * than a stylistic one. The legacy resource file also contains a
 * `UsernameDoesNotExist`-style message reading "Username Does Not Exist", which
 * tells an attacker which half of a credential pair was wrong. It is NOT carried
 * forward. The generic legacy wording is, and no message authored here
 * distinguishes an unknown account from an incorrect password or echoes back
 * anything the caller submitted.
 */
export const NOT_AUTHENTICATED =
  'Either you are not currently logged in, or you do not have access to this content.';

/** Shown for 403, where the caller is known and the operation is refused. */
export const FORBIDDEN = 'You do not have permission to perform this action.';

/** Shown for 404. */
export const NOT_FOUND = 'The requested item could not be found.';

/** Shown for 409, where the record changed underneath the caller. */
export const CONFLICT =
  'This item was changed by someone else. Reload it and apply your changes again.';

/** Shown for 422, where the values submitted were understood but refused. */
export const VALIDATION_REJECTED =
  'Some of the values supplied are not valid. Review the highlighted fields and try again.';

/**
 * Shown for 429.
 *
 * Calm on purpose. A rate-limit refusal means the caller is early, not that
 * anything failed, and it reaches this application from one place only - the
 * credential endpoints, whose limiter admits a fixed number of attempts per
 * window per address. Alarming wording here would suggest a fault where there is
 * none, which is why {@link problemSeverity} pairs it with a warning rather than
 * an error.
 */
export const TOO_MANY_ATTEMPTS = 'Too many attempts. Wait a moment and try again.';

/** Shown for any status at or above 500. */
export const SERVER_ERROR = 'The server could not complete the request. Try again shortly.';

/**
 * The sentence to show for a status code when the document carries no text.
 *
 * The `default` arm is required by the workspace and is genuinely reachable: the
 * status is a plain number, and the API's own vocabulary covers thirteen of them
 * including 405, 406, 415, 501 and 503. Anything unrecognised below 500 is
 * described as a rejected request, which is exactly what the server's own
 * fallback detail says.
 *
 * @param status The status code, or null when the document carried none.
 * @returns A non-blank sentence.
 */
export function statusMessage(status: number | null | undefined): string {
  if (status === null || status === undefined) {
    return REQUEST_REJECTED;
  }

  switch (status) {
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

/**
 * Resolves everything the presentation layer needs about one failure.
 *
 * The single entry point a banner, a notification or a signal store should use.
 * Every string it returns is plain text with break markup already resolved, every
 * collection is in document order, and nothing throws: a null document, an
 * unrecognised status and a malformed dictionary all produce a usable summary.
 *
 * @param problem The problem document, or null when the response carried none.
 * @param fallback Optional wording to prefer over the status-derived sentence when
 * the document carries no text of its own. Ignored when blank.
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

// ---------------------------------------------------------------------------
// THE FAILURE CODE, AND WHERE IT ACTUALLY TRAVELS
// ---------------------------------------------------------------------------
//
// The four vocabularies below are keyed on the failure code the API publishes.
// That code arrives in exactly ONE place, and it is not a member of its own: the
// server writes it into the problem document's `type` as
// `urn:dnnmigration:error:<code>`, built by `GlobalExceptionHandler.BuildProblemType`
// and reached from three call sites - the result translator, the tenant-resolution
// middleware and the authorisation-refusal handler. There is no `code` extension
// member on the document, so `type` is the whole channel and a consumer that does not
// parse it cannot key on a code at all.
//
// MIGRATION: THE VOCABULARIES ARE SPELLED AS THE SERVER SPELLS THEM, NOT AS THE
//   LEGACY ENUMERATIONS DID. Every table below previously used the legacy PascalCase
//   member names - `DuplicateRole`, `EnterCode`, `NotValidXml` and so on - and not one
//   of those values could ever match a value taken off the wire, so every lookup
//   returned null and the tables were unreachable in practice. The wording is what the
//   migration has to preserve, and it is preserved verbatim; the KEY has to be what the
//   server sends. The legacy member each entry descends from is named in a comment so
//   the parity claim stays checkable.
//
// MIGRATION: a legacy outcome with no emitted code gets NO ENTRY, rather than an entry
//   nothing can select. Each omission is named where it belongs, with the reason.
//   Symmetrically, an emitted code with no legacy wording gets no entry either: the
//   server's own `detail` is what {@link problemMessage} shows for it, which is both
//   correct and better than wording invented here.

/** The scheme and namespace the API puts in front of every failure code it publishes. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * Reduces a failure code to the form the server publishes.
 *
 * Mirrors the server's own reduction, which lower-cases and folds a hyphen onto an
 * underscore, because the services disagree about which separator they use inside a
 * reason token and the server declines to keep two spellings of one code. Applying it
 * again here is harmless - the operation is idempotent - and it means a caller may
 * pass either a code read off the wire or one quoted from a service.
 *
 * @param code A failure code in any of its spellings.
 * @returns The comparable form.
 */
function normaliseFailureCode(code: string): string {
  return code.trim().toLowerCase().replace(/-/g, '_');
}

/**
 * Reads the application failure code out of a problem document.
 *
 * This is the function that makes the four vocabularies below usable at all. Without
 * it a caller holds a `type` such as `urn:dnnmigration:error:role.name_duplicate` and
 * every table expects `role.name_duplicate`, so no lookup can ever succeed.
 *
 * Returns null rather than a best guess in three cases, and each is a real one:
 *
 * - the document carries no `type`, which the server allows and does deliberately
 *   rather than inventing a URI that documents nothing;
 * - the `type` is a URL into the HTTP semantics specification, which is what the
 *   framework writes for a status it mapped without reaching an action - a `404` from
 *   a bare not-found result, or a model-state failure. That is not an application
 *   failure code and must not be treated as one;
 * - the prefix is present but nothing follows it, which no producer emits and which
 *   would otherwise yield the empty string as though it were a code.
 *
 * The prefix test is case-insensitive on the prefix only. A URI scheme is
 * case-insensitive by specification, so a gateway that re-cased `URN:` must still be
 * recognised; the code that follows is compared in its normalised form.
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
 * The verification-failure codes the sign-in flow can report.
 *
 * EXACTLY THREE, and the list is closed, because the legacy flow had exactly three:
 * Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L168-L185 assigns
 * only `"EnterCode"` (L175 and L180), `"InvalidCode"` (L178) and
 * `"UserNotAuthorized"` (L184) to its outgoing message.
 *
 * MIGRATION: the three legacy names map one-for-one onto three codes the API really
 * emits, so nothing is lost by respelling them - `EnterCode` becomes
 * `auth.verification_required`, `InvalidCode` becomes
 * `auth.verification_code_invalid` and `UserNotAuthorized` becomes
 * `auth.account_not_approved`. The last of the three is the one worth naming
 * carefully: the legacy condition it reported was `LOGIN_USERNOTAPPROVED`, and the
 * target code says the same thing in the target's own vocabulary.
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
 * Wording for each verification-failure code.
 *
 * Reproduced verbatim, Title Case included, from
 * Website/admin/Authentication/App_LocalResources/Login.ascx.resx - `EnterCode.Text`
 * at L163, `InvalidCode.Text` at L166 and `UserNotAuthorized.Text` at L223.
 *
 * That path matters. The resource file sitting beside the login control itself,
 * under Website/DesktopModules/AuthenticationServices/DNN/App_LocalResources,
 * holds only `cmdLogin.Text`, `plVerification.Help`, `plVerification.Text` and
 * `Title.Text` - none of these three - so reading the nearer file would find
 * nothing and invite invented wording.
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

/**
 * The caller-held state the verification ladder advances.
 *
 * Held by the caller - a login component's signal store - because this module
 * keeps no state of its own. The legacy equivalent was the visibility of two
 * control rows on a posted-back page.
 */
export interface VerificationPromptState {
  /**
   * Whether the verification field is already on screen.
   *
   * The legacy test is `If Not rowVerification1.Visible` at
   * Login.ascx.vb:L171, which is what makes the ladder progressive: the first
   * refusal reveals the field, and only a subsequent refusal can judge what was
   * typed into it.
   */
  readonly verificationVisible: boolean;

  /**
   * What the person typed into the verification field, if anything.
   *
   * Null and the empty string mean the same thing here. The legacy test at
   * Login.ascx.vb:L177 is `If txtVerification.Text <> ""`, and the legacy null
   * contract defines the string "absent" marker AS the empty string
   * (Null.vb:L71-L75), so the two were always one branch.
   */
  readonly verificationCode: string | null;

  /**
   * Whether the portal requires registration to be verified by code.
   *
   * The legacy test is
   * `PortalSettings.UserRegistration = PortalRegistrationType.VerifiedRegistration`
   * at Login.ascx.vb:L170. When it does not, no amount of code entry is relevant
   * and the refusal is final.
   */
  readonly verifiedRegistration: boolean;
}

/** The outcome of one turn of the verification ladder. */
export interface VerificationPrompt {
  /** Which of the three codes applies. */
  readonly code: AuthFailureCode;

  /** The wording for that code. */
  readonly message: string;

  /**
   * Whether the caller should now reveal the verification field.
   *
   * True only on the turn that first reveals it, mirroring the legacy assignment
   * of `rowVerification1.Visible = True` and `rowVerification2.Visible = True` at
   * Login.ascx.vb:L173-L174.
   */
  readonly revealVerification: boolean;

  /** How forcefully to present it. A refusal is a warning, never an error. */
  readonly severity: ProblemSeverity;
}

/**
 * Advances the legacy verification ladder by one turn.
 *
 * A faithful port of Login.ascx.vb:L168-L185. The ladder is PROGRESSIVE and
 * STATEFUL, and a flat mapping from code to string would lose it:
 *
 * - Registration is not verified by code, so the refusal is final:
 *   `auth.account_not_approved`, legacy `UserNotAuthorized`.
 * - Registration is verified and the field is not yet on screen: reveal it and
 *   ask for the code - `auth.verification_required`, legacy `EnterCode`.
 * - The field is on screen and something was typed: what was typed was wrong -
 *   `auth.verification_code_invalid`, legacy `InvalidCode`.
 * - The field is on screen and nothing was typed: ask again -
 *   `auth.verification_required` again.
 *
 * The emptiness test is NOT trimmed, and that is deliberate rather than an
 * oversight. The legacy comparison `<> ""` is untrimmed, so a code of spaces was
 * non-empty and produced `InvalidCode`. Trimming here would silently reroute that
 * input to `EnterCode` and change an outcome the migration is required to
 * preserve.
 *
 * MIGRATION: the ladder is reached only when the server refuses a sign-in for an
 * unapproved account. In the legacy flow that condition was carried out of
 * `UserController.ValidateUser` through a `ByRef` status argument
 * (Login.ascx.vb:L164) and compared against `LOGIN_USERNOTAPPROVED` at L168. No
 * status crosses the wire now - a refusal arrives as a problem document - so the
 * caller decides from the response that the ladder applies and supplies the state
 * it holds.
 *
 * MIGRATION: the legacy code-behind guards this whole block with a CAPTCHA test
 * at Login.ascx.vb:L162, `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not
 * UseCaptcha)`. The CAPTCHA control is out of scope and no equivalent is
 * introduced; the compensating control is the server-side rate limiter on the
 * credential endpoints, whose refusal is worded by {@link TOO_MANY_ATTEMPTS}.
 *
 * MIGRATION: Login.ascx.vb:L187 carries a DEFECT, recorded here and deliberately
 * NOT fixed. It reads `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`,
 * and because `LOGIN_USERLOCKEDOUT` is 3 while `LOGIN_FAILURE` is 0, a locked-out
 * account satisfies the test and is treated as authenticated. The migration
 * discipline is to annotate a discovered defect rather than repair it, and
 * repairing it here would be pointless in any case: the condition is structurally
 * impossible in the target, where a lockout is a refusal carrying its own status
 * and never reaches a success path.
 *
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
 * Named for what it returns rather than for the dialog primitive of the same
 * shorter name, so nothing here shadows a browser global.
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
    // A refusal is presented as a warning, on the legacy precedent recorded on
    // {@link problemSeverity}: both branches of AccessDenied.ascx.vb use the
    // warning message type.
    severity: 'warning',
  };
}


// ---------------------------------------------------------------------------
// A NOTE ON THE WORDING REPRODUCED BELOW
// ---------------------------------------------------------------------------
//
// The three vocabularies that follow reproduce the legacy resource wording from
// Website/App_GlobalResources/SharedResources.resx and the administration
// resource files, because `Localization.GetString(key)` with no resource-file
// argument - the form the legacy translators use - resolves against the global
// file. Reproducing it keeps the messages recognisable to operators who used the
// legacy screens, which is what functional parity of error messages means.
//
// MIGRATION: runs of whitespace are reduced to a single space. Several legacy
// values separate sentences with two spaces. The legacy pages rendered them as
// markup, where a browser collapses the run to one space, so a single space is
// what a person actually saw. Reproducing the second space byte-for-byte would
// make the migrated screen differ VISIBLY from the legacy one, which is the
// opposite of the intent.
//
// MIGRATION: two legacy misspellings are corrected - "Futher" for "Further" in
// the registration-error wording, and "heirarchy" for "hierarchy" in the page-name
// conflict wording. Both are typographic rather than behavioural, both are in text
// this migration authors afresh rather than in a stored value, and neither
// correction changes which message is selected for which outcome.

// ---------------------------------------------------------------------------
// CODE VOCABULARY 2 - PASSWORD CHANGE
// ---------------------------------------------------------------------------

/**
 * The password-change failure codes that carry legacy wording.
 *
 * FIVE, and the count is the interesting part. The legacy enumeration
 * Library/Components/Users/Membership/PasswordUpdateStatus.vb:L23-L32 declared eight
 * members with no explicit values, so declaration order was the ordinal: `Success` 0
 * (L24), `PasswordMissing` 1, `PasswordNotDifferent` 2, `PasswordResetFailed` 3,
 * `PasswordInvalid` 4, `PasswordMismatch` 5, `InvalidPasswordAnswer` 6,
 * `InvalidPasswordQuestion` 7 (L31). Three of those eight have no code here, for three
 * different and individually sound reasons:
 *
 * - `Success` is not a failure. No code is emitted for a write that worked, and an
 *   entry mapping success to a failure message would be a category error.
 * - `InvalidPasswordAnswer` and `InvalidPasswordQuestion` have no counterpart because
 *   the password question-and-answer requirement is NOT carried forward: the legacy
 *   store held credentials reversibly so that a password could be recovered by
 *   answering a question, and the target hashes them one way, so there is no question
 *   to be wrong about. Their wording ("Password Answer must be provided" and "Password
 *   Question must be provided") is therefore not reproduced under any key, rather than
 *   parked under a key nothing can select.
 *
 * MIGRATION: no ordinal survives, and nothing is lost by that. The status never crossed
 * the wire even in the legacy application - it was a return value read in-process - and
 * the target reports the outcome as a stable code instead. A numeric value passed here
 * resolves to null, which the specification pins so that a caller cannot start relying
 * on an ordinal that no longer exists.
 *
 * MIGRATION: the service emits several password codes with NO legacy antecedent -
 * a current credential that is wrong, a reset that is not enabled, an operation the
 * store does not support, a change already required, and two self-service refusals.
 * None appears here. {@link problemMessage} shows the server's own `detail` for them,
 * which says the right thing in the target's own words; inventing legacy-styled wording
 * for an outcome the legacy application never had would be fabrication.
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

/**
 * Wording for each password-change failure code.
 *
 * From the global resource file, reproduced verbatim against the legacy member each
 * code descends from.
 *
 * The mismatch entry uses the `PasswordMismatch.Text` value. The same file also
 * carries a second, differently-keyed variant reading "Password Values Entered Do Not
 * Match."; the unsuffixed key is the unambiguous one and is the one reproduced.
 */
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

/**
 * The account-creation failure codes.
 *
 * TEN, which is what the creation path actually emits. The legacy enumeration
 * `UserCreateStatus` declared eighteen members with explicit values 0 to 17, and it is
 * still declared on the client in `core/models/user.model.ts` because it is a legacy
 * contract worth recording - but it never crossed the wire, in the legacy application
 * or in the target. It was a return value read in-process, and the target reports the
 * outcome as a stable code instead, so this vocabulary is keyed on the code and the
 * enumeration is cited for provenance rather than derived from.
 *
 * WHY EIGHTEEN LEGACY MEMBERS BECOME TEN CODES, member by member:
 *
 * - `AddUser` (0) and `Success` (13) are not failures. `AddUser` was the "no error yet"
 *   seed - Website/admin/Users/User.ascx.vb L175 and L185 both read
 *   `If createStatus <> UserCreateStatus.AddUser`, treating any other value as a
 *   failure - so a "zero means success" reading was wrong even in the legacy code, and
 *   wrong differently in each neighbouring enumeration: `UserLoginStatus.Success` is 1
 *   while the legacy `UserValidStatus.VALID` is 0. Three enumerations, three
 *   conventions, and none of them a code.
 * - `ProviderError` (12), `UnexpectedError` (14), `DuplicateProviderUserKey` (4) and
 *   `InvalidProviderUserKey` (9) become the ONE code `user.create.provider-error`. That
 *   is not a loss of fidelity: the legacy translator already collapsed all four into a
 *   single `Case` arm resolving to one message, so the four were indistinguishable to a
 *   person before this migration too.
 * - `AddUserToPortal` (17) becomes `user.create.portal-assignment-failed`. The legacy
 *   value was an operation marker that reached the throwing `Case Else`; the target
 *   turns the failure of that step into a reportable outcome, which is strictly more
 *   information than a thrown `ArgumentException`.
 * - `InvalidAnswer` (6) and `InvalidQuestion` (10) have no code, because the password
 *   question-and-answer requirement is not carried forward - see the note on
 *   {@link PASSWORD_UPDATE_CODES}.
 * - `UserRejected` (15) has no code on the creation path. The refusal it described is
 *   an approval state, and the target reports that at sign-in, as
 *   `auth.account_not_approved`.
 * - The remaining eight map one-for-one, as annotated below.
 *
 * The codes are spelled with the hyphen the service uses. The server folds a hyphen
 * onto an underscore before publishing, and {@link failureCode} folds it the same way,
 * so both spellings resolve here.
 */
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

/**
 * Wording for each account-creation failure code.
 *
 * A faithful port of `UserController.GetUserCreateStatus`
 * (Library/Components/Users/UserController.vb:L598), including the fact that the
 * legacy translator maps SEVERAL DISTINCT OUTCOMES ONTO ONE MESSAGE:
 *
 * - the two "name is taken" outcomes and the duplicate-name outcome all resolve to the
 *   `UserNameExists` wording, in a single combined `Case` arm;
 * - the four provider-fault outcomes all resolve to the `RegError` wording, likewise -
 *   and here they are additionally one code, because the server does not distinguish
 *   them either;
 * - the duplicate-address outcome resolves to `UserEmailExists`, a resource key that
 *   does not match its member name.
 *
 * THE THREE NAME OUTCOMES REMAIN THREE SEPARATE ENTRIES. Sharing wording is not the
 * same as being the same outcome: the server emits three distinct codes for them, the
 * invalid-name outcome has wording of its own, and keeping the keys apart means a
 * future divergence in wording needs no restructuring.
 */
const USER_NAME_EXISTS =
  'A User Already Exists For the Username Specified. Please Register Again Using A ' +
  'Different Username.';

const USER_EMAIL_EXISTS =
  'A user already exists for the email address specified. Please login using the ' +
  'registered account of that email address.';

const REGISTRATION_ERROR =
  'An Unexpected Error Occurred During Registration. Please Contact The Portal ' +
  'Administrator For Further Information.';

export const USER_CREATE_MESSAGE: Readonly<Record<UserCreateCode, string>> = Object.freeze({
  // These three share one message because the legacy translator combined them in a
  // single Case arm. Referencing one constant rather than repeating the text makes
  // that grouping visible in the source and impossible to break by editing one
  // copy of three.
  'user.create.username_already_exists': USER_NAME_EXISTS,
  'user.create.user_already_registered': USER_NAME_EXISTS,
  'user.create.duplicate_username': USER_NAME_EXISTS,
  'user.create.duplicate_email': USER_EMAIL_EXISTS,
  'user.create.invalid_email':
    'The email address specified is invalid. Please specify a valid email address.',
  // MIGRATION: the legacy wording continues "Passwords must be at least
  // [PasswordLength] characters in length and contain at least [NoneAlphabet]
  // non-alphanumeric characters.", with both tokens replaced at run time from the
  // membership provider's configuration. That tail is dropped here rather than
  // reproduced with invented numbers: the password policy is decided server-side,
  // this module re-decides nothing, and the server's own message carries the
  // configured values when it has them.
  'user.create.invalid_password':
    'The password specified is invalid. Please specify a valid password.',
  'user.create.invalid_username':
    'The username specified is invalid. Please specify a valid username.',
  // One code for four legacy provider-fault members, which the legacy translator had
  // already reduced to one message.
  'user.create.provider_error': REGISTRATION_ERROR,
  'user.create.password_mismatch': 'The Password and Confirmation Passwords do not match',
  // MIGRATION: the legacy member behind this reached the throwing Case Else and so had
  // no wording of its own. The registration-fault wording is used, because that is what
  // the legacy application showed for every other fault in the same sequence and
  // authoring a new sentence here would introduce wording no operator has seen.
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
 * Total, and never throws. The legacy translator ended with a `Case Else` that threw
 * `ArgumentException`; a display adapter that throws on an input it does not recognise
 * converts a cosmetic gap into a broken screen, so an unrecognised code resolves to
 * null and the caller falls back on the server's own message.
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

/**
 * The state refusals this application words for itself.
 *
 * TEN. "Conflict" is the historical name of this vocabulary and it is retained, but the
 * set is not one status: the server derives a status from a reason token, so the
 * duplicate, active-alias and last-remaining codes arrive as `409`, the
 * protected-assignment code as `403` (its token is `protected`), and the page-name and
 * module-content codes as `400`. What the ten have in common is not a status but a
 * shape - each is a refusal the person can act on, by renaming the thing, choosing a
 * different file, picking another page or reaching the portal by another address -
 * which is why {@link problemSeverity} presents the statuses that carry them as errors
 * rather than warnings, matching the red message type the legacy administration pages
 * used at every site that named one.
 *
 * NINE OF THE TEN CARRY LEGACY WORDING and one does not: the active-alias refusal had
 * no legacy sentence because the legacy screen HID the affordance instead of refusing
 * the request. Its wording is authored, and the reason is recorded beside it in
 * {@link CONFLICT_MESSAGE}.
 *
 * Each code carries a full stop as part of the code itself. It is ONE string, not a
 * path into a nested structure, and it must not be split.
 *
 * MIGRATION: two legacy keys are deliberately absent, and neither is an oversight.
 *
 * - `DuplicateAlias` ("The Portal Alias already exists.") collapses onto the same
 *   `portal.alias_duplicate` code as `DuplicatePortalAlias`, because the server reports
 *   one code for the collision however it was reached. The more actionable of the two
 *   legacy wordings is the one kept - it names the field and says what to do about it -
 *   and the terser variant is not reproduced under a second key nothing could select.
 * - `TabExists` ("The Page Name you chose is already being used…") has NO code, because
 *   no request can elicit it. The legacy guard sat behind
 *   `If String.IsNullOrEmpty(strAction)` at ManageTabs.ascx.vb:L279 while the edit
 *   branch was entered at L304 under `If strAction = "edit"`, so it belonged to the
 *   create path - and this API deliberately exposes no page create. The page service
 *   declares no conflict reason code, and the page update action declares no `409`, so
 *   wording it here would describe a refusal that cannot happen.
 *
 * MIGRATION: `portal.last_remaining` has no verbatim antecedent as a code. The legacy
 * resource key is `LastPortal`, held in the GLOBAL file
 * Website/App_GlobalResources/SharedResources.resx and read at
 * Library/Components/Portal/PortalController.vb:L200 through
 * `Localization.GetString("LastPortal")` with no local resource file. The wording is
 * taken from that key; the code is what the server publishes.
 *
 * MIGRATION: several emitted refusals have no legacy wording at all - a duplicate portal
 * administrator, an approval state already set, a store-level write conflict. Those do not
 * appear here; {@link problemMessage} shows the server's own `detail` for them.
 *
 * MIGRATION: `role_group.in_use` IS listed, and its wording is AUTHORED rather than
 * measured, which makes it the one exception to the rule above. The legacy screen never
 * produced this refusal - `EditGroups.ascx.vb` deleted a group without consulting the roles
 * classified by it - so there is no legacy resource key to quote and none was invented as
 * one. It is listed because the code is recognisable and actionable: the server publishes
 * `role_group.in_use` on `DELETE /api/v1/role-groups/{roleGroupId}` (the `in_use` token is
 * what the shared status translator reads to answer `409`), and the operator's next step -
 * move or delete the roles first - is not something the server's own detail states. Leaving
 * it out had a measurable cost: `RoleStoreFailure.conflict` resolved to null for the
 * refusal, so the role-listing screen carried a private copy of the wording keyed off the
 * bare status instead, which is a second vocabulary in a feature folder.
 */
export const CONFLICT_CODES = Object.freeze([
  /** Legacy `DuplicatePortalAlias`, and `DuplicateAlias` with it. */
  'portal.alias_duplicate',
  /**
   * The alias the current request resolved the tenant through, refused for a rename
   * and for an unbinding alike. NO LEGACY RESOURCE KEY, for the reason on
   * {@link CONFLICT_MESSAGE}.
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
  /** Legacy `RoleRemoveError`. Arrives as `403`, not `409`. */
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
 * Wording for each state-refusal code, from the legacy resource files.
 *
 * The page-name wording corrects a misspelling that is in the legacy string itself:
 * the legacy text reads "page heirarchy". Only the reserved-name refusal is reachable
 * through this API, so the corrected duplicate-path sentence has no key here - see the
 * note on {@link CONFLICT_CODES}.
 */
export const CONFLICT_MESSAGE: Readonly<Record<ConflictCode, string>> = Object.freeze({
  'portal.alias_duplicate':
    'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
  // MIGRATION: AUTHORED WORDING, because no legacy sentence exists to reproduce. The legacy screen
  //   did not refuse this - it HID the affordance, at
  //   Website/admin/Portal/PortalAlias.ascx.vb:L51-L60, so no resource key was ever needed for a
  //   refusal that could not be reached from the console. The rule is now enforced server-side as
  //   well, so a refusal IS reachable - by a crafted call, or by a stale row set - and it needs a
  //   sentence. It names the recovery, because the refusal is actionable: the same change succeeds
  //   from a request that arrived through another of the portal's host names.
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
// Exported so that no screen has to write a code literal of its own. The portal-alias
// screen presents this refusal in the terser wording its own legacy control used rather
// than the shared sentence, so it must be able to TELL THE TWO ALIAS REFUSALS APART -
// and until this predicate existed the only way to do that was a string comparison in
// the component, duplicating the vocabulary this file owns and free to drift from it.
export function isDuplicateAliasCode(code: ConflictCode | null): boolean {
  return code === 'portal.alias_duplicate';
}

/**
 * Whether a narrowed refusal code is the refusal to write the alias the current request
 * resolved the tenant through.
 *
 * @param code A refusal code a store has already narrowed, or null.
 * @returns True for the active-alias refusal and false for anything else.
 */
// Both alias endpoints can now answer `409` for TWO different reasons, so a status is no
// longer diagnostic on that screen and the code has to be read. See
// {@link isDuplicateAliasCode} for why the comparison lives here.
export function isAliasInUseCode(code: ConflictCode | null): boolean {
  return code === 'portal.alias_in_use.conflict';
}

// ---------------------------------------------------------------------------
// ADVISORY WORDING
// ---------------------------------------------------------------------------

/**
 * Advisories that interrupt a sign-in without refusing it.
 *
 * The legacy `UserValidStatus` enumeration
 * (Library/Components/Users/Membership/UserValidStatus.vb) carried these as five
 * members - `VALID`, `PASSWORDEXPIRED`, `PASSWORDEXPIRING`, `UPDATEPROFILE` and
 * `UPDATEPASSWORD` - with `VALID` at 0.
 *
 * MIGRATION: that enumeration is not declared on the client and does not cross the
 * wire; the migrated contract carries the same information as advisory flags. What
 * survives is the wording, kept here so the flags have something recognisable to
 * say. Taken from Website/admin/Authentication/App_LocalResources/Login.ascx.resx.
 *
 * The two expiry entries carry a `{0}` placeholder for a date, substituted by
 * {@link advisoryMessage}. Formatting the date itself belongs to a display pipe,
 * not here, so the substitution takes an already-formatted string.
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
 * Ignored by the other two. When it is missing for an advisory that names a date,
 * the undated advisory is returned instead, so no person is ever shown the
 * characters `{0}` or a sentence with a gap where a date should be.
 * @returns The message. Never blank, and never carrying an unresolved placeholder.
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

// ---------------------------------------------------------------------------
// LEGACY LOGIN STATUS - WHERE IT LIVES, AND WHY IT IS NOT DECLARED HERE
// ---------------------------------------------------------------------------
//
// The login outcomes are declared once, as `UserLoginStatus` in
// `core/models/auth.model.ts`. This module previously restated them as a frozen
// array of the legacy SCREAMING_CASE spellings, exported, and then never read them:
// nothing in this file or anywhere in the application decoded a login status, because
// the sign-in response carries no status member and a refusal arrives as a problem
// document instead. An exported vocabulary with no reader is a second definition of
// something that already had one, and the two spellings could drift apart with
// nothing to detect it. The array and its member union are therefore removed, and the
// authoritative enumeration is cited instead.
//
// MIGRATION: THE TWO SPELLINGS ARE NOT INTERCHANGEABLE, which is the one fact worth
//   carrying here rather than leaving to a reader to rediscover. The legacy member
//   names are SCREAMING_CASE with a `LOGIN_` prefix -
//   Library/Components/Users/Membership/UserLoginStatus.vb:L23-L31 declares
//   LOGIN_FAILURE 0, LOGIN_SUCCESS 1, LOGIN_SUPERUSER 2, LOGIN_USERLOCKEDOUT 3,
//   LOGIN_USERNOTAPPROVED 4, LOGIN_INSECUREADMINPASSWORD 5 and
//   LOGIN_INSECUREHOSTPASSWORD 6. The target enumeration keeps every numeric value
//   and renames every member to PascalCase: Failure, Success, SuperUser,
//   UserLockedOut, UserNotApproved, InsecureAdminPassword, InsecureHostPassword. A
//   citation of the LEGACY enumeration must quote the `LOGIN_` form; a reference to
//   the TARGET enumeration must not.
//
// MIGRATION: the ordinals are why the legacy control flow reads the way it does, and
//   the reason is preserved because the defect annotated on
//   `resolveVerificationPrompt` depends on it: the value seeded at
//   Login.ascx.vb:L163 is LOGIN_FAILURE, a fail-closed default, and the lockout
//   defect is only visible once one knows that the locked-out outcome is 3 while the
//   failure outcome is 0. Both values are unchanged by the rename.
