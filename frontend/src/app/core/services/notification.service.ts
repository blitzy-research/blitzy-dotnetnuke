import { HttpContext, HttpContextToken } from '@angular/common/http';
import { Injectable, signal, type Signal } from '@angular/core';

/**
 * Closed severity vocabulary for a queued notification.
 *
 * `'warning'` is a first-class member rather than a synonym for `'error'`, because an authorisation denial
 * is presented as a warning rather than as a failure. Callers choose the severity; nothing here derives one.
 */
export type NotificationSeverity = 'success' | 'info' | 'warning' | 'error';

/**
 * Marks a request whose failure is ALREADY presented by whoever issued it.
 *
 * A failed request has exactly ONE publisher, and this token is how the two candidate publishers agree which
 * of them it is:
 *
 * - a request carrying this marker is presented BY ITS CALLER. Every read and write issued through the
 *   services in `core/services/` is marked, because each one is dispatched either by a root signal store —
 *   which publishes the failure structurally on its own `failure`/`problem` slice, which a screen binds to
 *   the shared `error-banner` — or by a screen that binds `error-banner` itself. The global announcer must
 *   stay silent for these;
 * - a request carrying no marker has no local surface, so `error.interceptor.ts` announces it. That is the
 *   safety net, and it is what keeps a failure from going unreported when a future caller forgets to present
 *   one.
 *
 * - a request carrying this marker is presented BY ITS CALLER. Every read and write
 *   issued through the services in `core/services/` is marked — the credential operations
 *   in `auth.service.ts` included, and they were the last exception — because each one is
 *   dispatched either by a root signal store, which publishes the failure structurally on
 *   its own `failure`/`problem` slice for a screen to bind to the shared `error-banner`, or
 *   by a screen that binds `error-banner` itself, or, where an operation has no screen at
 *   all, by a store that announces it here in words. The global announcer must stay silent
 *   for these;
 * - a request carrying no marker has no local surface, so `error.interceptor.ts`
 *   announces it. That is the safety net, and it is what keeps a failure from going
 *   unreported when a future caller forgets to present one.
 *
 * Declared here, in the presentation surface, rather than in the interceptor: the rule is about who presents
 * a failure, both parties need to name it, and a service importing it from an interceptor module would read
 * as though transport depended on interception.
 *
 * The default is `false` — unmarked means announced — so forgetting the marker produces a duplicate
 * notification rather than a silent failure. That asymmetry is deliberate: the failure mode of the default
 * must be noisy, not invisible.
 */
export const PRESENTED_IN_CONTEXT = new HttpContextToken<boolean>(() => false);

/**
 * Builds, or extends, an {@link HttpContext} marked as presented by its caller.
 *
 * Takes an optional existing context so that a call site already carrying one — for a future token — sets
 * this marker on it rather than replacing it.
 *
 * @param context An existing context to extend, or none to start a fresh one.
 * @returns The context, carrying {@link PRESENTED_IN_CONTEXT}.
 */
export function presentedInContext(context: HttpContext = new HttpContext()): HttpContext {
  return context.set(PRESENTED_IN_CONTEXT, true);
}

/**
 * One queued notification.
 *
 * Named `AppNotification` rather than `Notification` to avoid shadowing the DOM global of that name, which
 * would silently change what `Notification` means in any file importing from here.
 */
export interface AppNotification {
  readonly id: number;

  readonly severity: NotificationSeverity;

  /**
   * The already-composed, display-ready message, treated strictly as plain text.
   *
   * Passed through verbatim - no trimming, no case change and no substitution. Two exceptions, both bounded
   * and both documented: a message longer than {@link MAX_MESSAGE_LENGTH} is retained only up to that bound,
   * and a {@link AppNotification.reference} supplied with it is appended AFTER that bound is applied. No
   * marker is added to signal a cut, because adding one would be the substitution this contract forbids and
   * would also be a display decision.
   *
   * Guaranteed to carry visible text: {@link NotificationService.notify} refuses a blank or whitespace-only
   * message outright, so no entry with an unreadable message can ever reach this queue. Consumers therefore
   * need no emptiness guard of their own before rendering.
   *
   * Consumers must bind it as text content.
   */
  readonly message: string;

  /**
   * The support reference an operator quotes when reporting the incident, or `null` when the outcome has
   * none to quote.
   *
   * Carried as its own member as well as being appended to {@link message}, and the duplication is the point
   * rather than an oversight: the member is what makes the reference SURVIVE, and the appended copy is what
   * keeps the existing single-string display contract intact for consumers that render only the message.
   *
   * MIGRATION: the reference used to be appended by the caller and could then be truncated away.
   * `error.interceptor.ts` concatenated `'Reference: <id>'` onto the end of a message composed from a remote
   * `ProblemDetails`, and this service then bounded the ALREADY-CONCATENATED string at {@link
   * MAX_MESSAGE_LENGTH}. Truncation removes a suffix, so on exactly the failures whose `detail` is long —
   * the unexpected ones, which are the failures worth reporting — the correlation reference was the first
   * thing discarded, leaving an operator with an unbounded server sentence and no identifier to look it up
   * by. Bounding now applies to the caller's message only, and the reference is appended afterwards, so it
   * cannot be reached by the cut.
   *
   * Bounded independently at {@link MAX_REFERENCE_LENGTH}: it is remote input like the message, so it may
   * not be retained at whatever length it arrives.
   */
  readonly reference: string | null;
}

/**
 * The single empty-queue instance, used both as the initial value and as the value {@link
 * NotificationService.clear} restores. Frozen because it is shared.
 */
const EMPTY_QUEUE: readonly AppNotification[] = Object.freeze([]);

/**
 * The greatest number of UTF-16 code units of a single message this service will retain.
 *
 * One of the two paths into this service is not under the application's control: the error interceptor
 * composes its message from a server `ProblemDetails` payload, whose `detail` and `errors` members are
 * remote input. Retaining such a string at whatever length it arrives makes the queue's memory footprint a
 * function of a remote response rather than of this application, which is the unbounded-input exposure this
 * bound closes.
 *
 * The figure is measured against the legacy wording corpus - the in-scope `App_LocalResources` and
 * `App_GlobalResources` `<data>` values - and clears every message value in it by a wide margin, so it
 * cannot truncate legitimate wording. The only legacy values that exceed it are long-form legal *page
 * content* rather than banner messages and would never be queued here.
 *
 * @see boundMessage - applies this bound.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * The greatest number of entries the queue will hold at once.
 *
 * Beyond this depth the oldest entry is dropped as the newest is appended, so the queue behaves as a
 * fixed-capacity window over the most recent notifications. The bound exists for two reinforcing reasons.
 * Combined with {@link MAX_MESSAGE_LENGTH} it makes retained message text finite - a worst case of 25 x 1024
 * code units, roughly 25 KB - instead of growing without limit for as long as a failing request is retried.
 * It also stops the append cost compounding: each append copies the queue to keep the replace-never-mutate
 * discipline below, which is O(n) in the depth, so capping the depth makes a run of appends linear rather
 * than quadratic. The depth is generous against the behaviour being replaced, where the legacy
 * `AddModuleMessage` surface rendered a single module message per page render.
 *
 * Dropping the oldest rather than refusing the newest is deliberate: the newest notification describes what
 * just happened, so it is the one the user needs, and silently discarding it would hide a live failure.
 */
const MAX_QUEUED_NOTIFICATIONS = 25;

/**
 * Applies {@link MAX_MESSAGE_LENGTH} to one message.
 *
 * A message at or below the bound is returned as the very same string, so the overwhelmingly common case
 * allocates nothing and the verbatim contract on {@link AppNotification.message} holds exactly.
 *
 * @param message The already-composed, display-ready plain-text message.
 * @returns The message unchanged, or its leading {@link MAX_MESSAGE_LENGTH} code units when it is longer.
 */
function boundMessage(message: string): string {
  return boundText(message, MAX_MESSAGE_LENGTH);
}

/**
 * The greatest number of UTF-16 code units of a support reference this service will retain.
 *
 * The reference is remote input on the same footing as the message: it is read from a response body's
 * `correlationId`, falling back to `traceId`, and a proxy or a misconfigured gateway can put anything there.
 * The value this application's own server writes is a 32-character identifier, and the longest legitimate
 * alternative — an activity trace parent — is 55 characters, so this bound clears every real value by more
 * than a factor of two while keeping the retained text finite.
 *
 * Bounding it separately from {@link MAX_MESSAGE_LENGTH} is what makes the reference immune to the message's
 * truncation: the two are applied to two different strings and concatenated afterwards.
 */
const MAX_REFERENCE_LENGTH = 128;

/**
 * Introduces the support reference within a composed message.
 *
 * The wording matches the label `error.interceptor.ts` used when it composed the suffix itself, so no
 * rendered message changes as a result of moving the composition here.
 */
const REFERENCE_LABEL = 'Reference:';

/**
 * Retains at most `limit` UTF-16 code units of `text`, without splitting a surrogate pair.
 *
 * Text at or below the bound is returned as the very same string, so the overwhelmingly common case
 * allocates nothing and the verbatim contract on {@link AppNotification.message} holds exactly.
 *
 * @param text The text to bound.
 * @param limit The greatest number of code units to retain.
 * @returns The text unchanged, or its leading `limit` code units when it is longer.
 */
function boundText(text: string, limit: number): string {
  if (text.length <= limit) {
    return text;
  }

  // `String.prototype.length` counts UTF-16 code units rather than characters, so a cut at exactly the bound
  // can land between the two halves of a surrogate pair and leave a lone high surrogate as the final unit.
  // Stepping back one unit in that case keeps the retained text well-formed. It matters because the consumer
  // renders the message as text content, where a lone surrogate is not representable and shows as U+FFFD - a
  // visible mangling of the last character.
  //
  // The text is known to be longer than the bound here, so when the unit at `limit - 1` is a high surrogate
  // its partner really is being dropped; this is not a speculative guard.
  const lastRetainedUnit = text.charCodeAt(limit - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return text.slice(0, cutSplitsSurrogatePair ? limit - 1 : limit);
}

/**
 * Bounds a support reference and refuses a blank one.
 *
 * A reference that carries no visible text is treated as absent rather than quoted, because quoting it would
 * leave a dangling `Reference:` label with nothing after it — an instruction to report an identifier that
 * was never issued.
 *
 * @param reference The reference as supplied, or `null` when none applies.
 * @returns The bounded reference, or `null` when there is nothing to quote.
 */
function boundReference(reference: string | null): string | null {
  if (reference === null) {
    return null;
  }

  const bounded = boundText(reference, MAX_REFERENCE_LENGTH);

  return bounded.trim().length === 0 ? null : bounded;
}

// MIGRATION: the legacy `DataCache` call sites are deliberately NOT reproduced on the client. Their source
// of behaviour is Library/Components/Providers/Caching/DataCache.vb - note the path, since a second,
// unrelated 85-line DataCache.vb lives under Library/Controls/DotNetNuke.WebUtility/ and is out of scope.
// Caching in the target architecture is a server-side concern only, `IMemoryCache` behind `ICacheService`,
// so this queue holds no cache, keeps no de-duplication window and expires nothing on a timer: it is a
// transient view-model slice and nothing more.
//
// The fixed-depth overflow guard at MAX_QUEUED_NOTIFICATIONS is NOT a cache eviction policy and must not be
// read as one. It is keyless and timeless - no key to look an entry up by, no expiry to reach, no
// re-population path - so nothing here can be a hit or a miss. It is purely a capacity ceiling on a display
// queue, and dropping an entry loses nothing that could be fetched again.

// MIGRATION: localisation is not ported.
//
// The Web Forms `App_LocalResources` mechanism that `AccessDenied.ascx.vb` used - a
// `Services.Localization.Localization.GetString` lookup against the screen's own resource file - has no
// target equivalent. No Angular translation runtime is installed - the pinned frontend dependency surface is
// 21 packages and contains no i18n package - and user-facing strings are authored directly in Angular
// templates, with the .resx files read only as the authority for English wording.
//
//  The practical consequence for this file: the `message` it receives is already in its final display form.
//  It performs no lookup, no interpolation and no formatting.

/**
 * Holds an in-memory, append-ordered queue of user-facing notifications, each one a severity plus an
 * already-composed message.
 *
 * It deliberately owns no presentation behaviour - no auto-dismiss window, no animation, no de-duplication -
 * and no interpretation of transport failures: whoever reports an outcome has already chosen the severity
 * and composed the text. Every operation replaces the queue rather than mutating it in place.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly _notifications = signal<readonly AppNotification[]>(EMPTY_QUEUE);

  /**
   * The queue, oldest entry first. Read-only at compile time: the `readonly` element type and the read-only
   * signal make an accidental write a compile error, they do not harden the array at runtime.
   */
  readonly notifications: Signal<readonly AppNotification[]> = this._notifications.asReadonly();

  /**
   * A plain counter, never rewound - neither by a dismissal nor by {@link NotificationService.clear} - so an
   * id is unique for the whole service lifetime and stays usable as a template track key.
   */
  private nextId = 1;

  /**
   * Appends one notification to the end of the queue, unless the message is blank.
   *
   * The severity is taken as given, and a message that carries any visible text is stored **verbatim** - never
   * trimmed, case-folded, escaped, decoded, normalised or reformatted, since reformatting a message that does
   * have content would be a display decision. Only its length is inspected.
   *
   * A message that is empty, or that consists only of whitespace, is discarded: no entry is created, the queue
   * is left untouched, and {@link nextId} does not advance, so the call is a no-op. A notification is an
   * instruction to interrupt the user rather than data a component may choose how to render, so queueing a
   * blank one would produce a visible, dismissible, screen-reader-announced alert carrying nothing to read.
   * Holding {@link nextId} still on a refusal keeps the ids of real entries gapless; the counter's contract is
   * that it never reissues an id, not that it counts call attempts.
   *
   * Two bounds exist because a message can originate in a remote `ProblemDetails` payload, so neither its
   * length nor how many times it arrives is under this application's control:
   *
   * - a message longer than {@link MAX_MESSAGE_LENGTH} is retained only up to that bound - see
   *   {@link boundMessage};
   * - appending to a queue already holding {@link MAX_QUEUED_NOTIFICATIONS} entries drops the oldest, so depth
   *   never exceeds the cap.
   *
   * Two identical `(severity, message)` pairs produce two distinct entries with distinct ids, because no
   * de-duplication window exists here.
   *
   * A caller that has a support reference passes it as the third argument rather than concatenating it into
   * the message, and the ordering inside this method is the whole reason the argument exists: the caller's
   * message is bounded first, the reference is bounded separately, and only then are the two joined, so
   * truncation can never reach the reference. A blank reference is treated as absent, so no dangling label is
   * ever left behind.
   *
   * @param severity The already-decided severity to render at.
   * @param message The already-composed, display-ready plain-text message. A blank or whitespace-only value
   *   is refused and the call becomes a no-op, whether or not a reference accompanies it: a notification
   *   reading only `Reference: …` tells a person nothing about what happened.
   * @param reference The support reference to quote, or `null` when the outcome has none.
   */
  notify(
    severity: NotificationSeverity,
    message: string,
    reference: string | null = null,
  ): void {
    // The bound is applied BEFORE the emptiness test, so a message that is only whitespace is still
    // recognised as blank after truncation.
    const bounded = boundMessage(message);

    // Emptiness is tested on a trimmed COPY; the stored value is the bounded original, so a message such as
    // `' kept '` keeps its padding while `'   '` is refused without consuming an id.
    if (bounded.trim().length === 0) {
      return;
    }

    const quoted = boundReference(reference);
    const composed = quoted === null ? bounded : `${bounded} ${REFERENCE_LABEL} ${quoted}`;

    const entry: AppNotification = {
      id: this.nextId++,
      severity,
      message: composed,
      reference: quoted,
    };

    this._notifications.update((queue) => {
      // Written as a surplus count rather than a `length === cap` test so that it is total: it collapses to
      // a plain append for every depth below the cap and still returns a correctly capped queue for any
      // depth at or above it.
      const surplus = queue.length + 1 - MAX_QUEUED_NOTIFICATIONS;

      return surplus > 0 ? [...queue.slice(surplus), entry] : [...queue, entry];
    });
  }

  success(message: string): void {
    this.notify('success', message);
  }

  info(message: string): void {
    this.notify('info', message);
  }

  warning(message: string): void {
    this.notify('warning', message);
  }

  /**
   * Queues a failure.
   *
   * The one convenience method that forwards a support reference, because a failure is the only outcome that
   * has one to quote.
   *
   * @param message The already-composed, display-ready plain-text message.
   * @param reference The support reference to quote, or `null` when there is none.
   */
  error(message: string, reference: string | null = null): void {
    this.notify('error', message, reference);
  }

  /**
   * Removes the entry carrying `id`, if one is present.
   *
   * An id that was never issued, or that a clear has already discarded, is a no-op rather than an error.
   * Surviving entries keep their identity and order.
   *
   * @param id The {@link AppNotification.id} to remove.
   */
  dismiss(id: number): void {
    this._notifications.update((queue) => queue.filter((entry) => entry.id !== id));
  }

  /**
   * Empties the queue by restoring the shared empty instance, so clearing an already-empty queue publishes
   * an unchanged reference and notifies nobody.
   */
  clear(): void {
    this._notifications.set(EMPTY_QUEUE);
  }
}
