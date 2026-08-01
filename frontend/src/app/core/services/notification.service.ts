import { Injectable, signal, type Signal } from '@angular/core';

/**
 * Closed severity vocabulary for a queued notification.
 *
 * `'warning'` is a first-class member rather than a synonym for `'error'`,
 * because an authorisation denial is presented as a warning rather than as a
 * failure. Callers choose the severity; nothing here derives one.
 */
export type NotificationSeverity = 'success' | 'info' | 'warning' | 'error';

/**
 * One queued notification.
 *
 * Named `AppNotification` rather than `Notification` to avoid shadowing the DOM
 * global of that name, which would silently change what `Notification` means in
 * any file importing from here.
 */
export interface AppNotification {
  readonly id: number;

  readonly severity: NotificationSeverity;

  /**
   * The already-composed, display-ready message, treated strictly as plain text.
   *
   * Passed through verbatim - no trimming, no case change, no substitution and
   * nothing appended. The single exception is length: a message longer than
   * {@link MAX_MESSAGE_LENGTH} is retained only up to that bound, for the reason
   * documented on the constant. No marker is added to signal the cut, because
   * adding one would be the substitution this contract forbids and would also be
   * a display decision.
   *
   * Guaranteed to carry visible text: {@link NotificationService.notify} refuses a
   * blank or whitespace-only message outright, so no entry with an unreadable
   * message can ever reach this queue. Consumers therefore need no emptiness guard
   * of their own before rendering.
   *
   * Consumers must bind it as text content.
   */
  readonly message: string;
}

/**
 * The single empty-queue instance, used both as the initial value and as the
 * value {@link NotificationService.clear} restores. Frozen because it is shared.
 */
const EMPTY_QUEUE: readonly AppNotification[] = Object.freeze([]);

/**
 * The greatest number of UTF-16 code units of a single message this service will
 * retain.
 *
 * A notification message reaches this service from two directions, and one of
 * them is not under the application's control: the error interceptor composes it
 * from a server `ProblemDetails` payload, whose `detail` and `errors` members are
 * remote input. Retaining such a string at whatever length it happens to arrive
 * makes the queue's memory footprint a function of a remote response rather than
 * of this application, which is the unbounded-input exposure this bound closes.
 *
 * The figure is measured, not chosen for roundness. Across the 40 in-scope
 * `App_LocalResources` and `App_GlobalResources` resource files - 1561 plain
 * `<data>` values, the authoritative corpus of legacy admin wording - value
 * length runs to a median of 22 characters, a 95th percentile of 166 and a 99th
 * percentile of 387. The only values beyond about 7000 characters are
 * `MESSAGE_PORTAL_TERMS` and `MESSAGE_PORTAL_PRIVACY`, which are long-form legal
 * *page content* rather than banner messages and would never be queued here.
 * 1024 is therefore roughly 2.6 times the 99th percentile and clears every
 * in-scope message value, so it cannot truncate legitimate wording.
 *
 * @see boundMessage - applies this bound.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * The greatest number of entries the queue will hold at once.
 *
 * Beyond this depth the oldest entry is dropped as the newest is appended, so
 * the queue behaves as a fixed-capacity window over the most recent
 * notifications.
 *
 * This bound exists for two reasons that reinforce each other:
 *
 * - **Retained bytes become finite.** Combined with
 *   {@link MAX_MESSAGE_LENGTH}, worst-case retained message text is a provable
 *   25 x 1024 code units - roughly 25 KB - instead of growing without limit for
 *   as long as a failing request is retried.
 * - **The append cost stops compounding.** Each append copies the queue to keep
 *   the replace-never-mutate discipline below, which is O(n) in the queue's
 *   depth. With the depth capped, n is a constant, so a run of appends is O(1)
 *   each and linear overall rather than quadratic.
 *
 * 25 is generous by an order of magnitude against the behaviour being replaced:
 * the legacy `AddModuleMessage` surface rendered a single module message per page
 * render, so no legacy screen ever displayed more than a handful at once.
 *
 * Dropping the oldest rather than refusing the newest is deliberate. The newest
 * notification is the one describing what just happened, so it is the one the
 * user needs; silently discarding it would hide a live failure, which is exactly
 * what the no-de-duplication rule above is there to prevent.
 */
const MAX_QUEUED_NOTIFICATIONS = 25;

/**
 * Applies {@link MAX_MESSAGE_LENGTH} to one message.
 *
 * A message at or below the bound is returned as the very same string, so the
 * overwhelmingly common case allocates nothing and the verbatim contract on
 * {@link AppNotification.message} holds exactly.
 *
 * @param message The already-composed, display-ready plain-text message.
 * @returns The message unchanged, or its leading {@link MAX_MESSAGE_LENGTH} code
 *          units when it is longer.
 */
function boundMessage(message: string): string {
  if (message.length <= MAX_MESSAGE_LENGTH) {
    return message;
  }

  // `String.prototype.length` counts UTF-16 code units rather than characters, so
  // a cut at exactly the bound can land between the two halves of a surrogate
  // pair and leave a lone high surrogate as the final unit. Stepping back one unit
  // in that case keeps the retained text well-formed. It matters because the
  // consumer renders the message as text content, where a lone surrogate is not
  // representable and shows as U+FFFD - a visible mangling of the last character.
  //
  // The message is known to be longer than the bound here, so when the unit at
  // `MAX_MESSAGE_LENGTH - 1` is a high surrogate its partner really is being
  // dropped; this is not a speculative guard.
  const lastRetainedUnit = message.charCodeAt(MAX_MESSAGE_LENGTH - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return message.slice(0, cutSplitsSurrogatePair ? MAX_MESSAGE_LENGTH - 1 : MAX_MESSAGE_LENGTH);
}

// MIGRATION: the 116 in-scope legacy `DataCache` call sites are deliberately NOT
// reproduced on the client.
//
// Their source of behaviour is Library/Components/Providers/Caching/DataCache.vb
// (317 lines). Note the real path: the plan text cites
// Library/Components/Shared/DataCache.vb, which does not exist in this
// repository - recorded as discrepancy D4. (A second, unrelated 85-line
// DataCache.vb lives under Library/Controls/DotNetNuke.WebUtility/ and is out of
// scope.)
//
// Caching in the target architecture is a server-side concern only -
// `IMemoryCache` behind `ICacheService`. This queue therefore holds no cache,
// keeps no de-duplication window and expires nothing on a timer: it is a
// transient view-model slice and nothing more.
//
// The fixed-depth overflow guard at MAX_QUEUED_NOTIFICATIONS is not a cache
// eviction policy and must not be read as one. It is keyless and timeless: there
// is no key to look an entry up by, no expiry to reach and no re-population path,
// so nothing here can be a hit or a miss. It is purely a capacity ceiling on a
// display queue, and dropping an entry loses nothing that could be fetched again.

// MIGRATION: localisation is not ported.
//
// The Web Forms `App_LocalResources` mechanism used at AccessDenied.ascx.vb:L45
// (`Services.Localization.Localization.GetString("AccessDenied", ...)`) has no
// target equivalent. No Angular translation runtime is installed - the pinned
// frontend dependency surface is 21 packages and contains no i18n package - and
// user-facing strings are authored directly in Angular templates, with the .resx
// files read only as the authority for English wording.
//
// The practical consequence for this file: the `message` it receives is already
// in its final display form. It performs no lookup, no interpolation and no
// formatting.

/**
 * Holds an in-memory, append-ordered queue of user-facing notifications, each
 * one a severity plus an already-composed message.
 *
 * It deliberately owns no presentation behaviour - no auto-dismiss window, no
 * animation, no de-duplication - and no interpretation of transport failures:
 * whoever reports an outcome has already chosen the severity and composed the
 * text. Every operation replaces the queue rather than mutating it in place.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly _notifications = signal<readonly AppNotification[]>(EMPTY_QUEUE);

  /**
   * The queue, oldest entry first. Read-only at compile time: the `readonly`
   * element type and the read-only signal make an accidental write a compile
   * error, they do not harden the array at runtime.
   */
  readonly notifications: Signal<readonly AppNotification[]> = this._notifications.asReadonly();

  /**
   * A plain counter, never rewound - neither by a dismissal nor by
   * {@link NotificationService.clear} - so an id is unique for the whole service
   * lifetime and stays usable as a template track key.
   */
  private nextId = 1;

  /**
   * Appends one notification to the end of the queue, unless the message is blank.
   *
   * The severity is taken as given, and a message that carries any visible text is
   * stored **verbatim** - never trimmed, case-folded, escaped, decoded, normalised
   * or reformatted. Only its length is inspected, for the bound described below.
   *
   * ## Blank messages are refused rather than queued
   *
   * A message that is empty, or that consists only of whitespace, is discarded: no
   * entry is created, the queue is left untouched, and {@link nextId} does not
   * advance. The call is a no-op.
   *
   * A notification is not data a component may choose how to render; it is an
   * instruction to interrupt the user. Queueing a blank one produces a visible,
   * dismissible, screen-reader-announced alert carrying nothing to read - a defect
   * no consumer can render its way out of, because the only correct rendering of
   * "nothing to say" is not to appear at all. Pushing the check downstream would
   * also multiply it across every current and future consumer.
   *
   * The legacy evidence points the same way: `Library/Components/Shared/Null.vb`
   * L71-L75 returns `""` from `NullString`, so the empty string IS the legacy
   * model's representation of an ABSENT string. A caller arriving here with `''`
   * is reporting that it has no message, and the faithful response to "no message"
   * is to raise no notification. Whitespace-only input is absent in exactly the
   * same sense.
   *
   * `nextId` deliberately does not advance on a refusal. The counter's contract is
   * that it never reissues an id, not that it counts call attempts, and leaving it
   * still keeps the ids of real entries gapless.
   *
   * ## Two bounds apply
   *
   * Both exist because a message can originate in a remote `ProblemDetails`
   * payload, so neither its length nor how many times it arrives is under this
   * application's control:
   *
   * - a message longer than {@link MAX_MESSAGE_LENGTH} is retained only up to
   *   that bound - see {@link boundMessage};
   * - appending to a queue already holding {@link MAX_QUEUED_NOTIFICATIONS}
   *   entries drops the oldest, so depth never exceeds the cap.
   *
   * Neither bound is reachable by legitimate wording or by legitimate use, so for
   * every message this application composes itself the behaviour is identical to
   * an unbounded append.
   *
   * ## Behaviour that IS intentional pass-through
   *
   * - two identical `(severity, message)` pairs produce two distinct entries with
   *   distinct ids, because no de-duplication window exists here;
   * - interior whitespace, casing, tabs and line breaks in a nonblank message are
   *   all preserved exactly, including any leading or trailing padding, because
   *   reformatting a message that does have content would be a display decision.
   *
   * @param severity The already-decided severity to render at.
   * @param message The already-composed, display-ready plain-text message. A blank
   *   or whitespace-only value is refused and the call becomes a no-op.
   */
  notify(severity: NotificationSeverity, message: string): void {
    // The bound is applied BEFORE the emptiness test, so a message that is only
    // whitespace is still recognised as blank after truncation.
    const bounded = boundMessage(message);

    // Emptiness is tested on a trimmed COPY; the stored value is the bounded
    // original, so a message such as `' kept '` keeps its padding while `'   '` is
    // refused without consuming an id.
    if (bounded.trim().length === 0) {
      return;
    }

    const entry: AppNotification = { id: this.nextId++, severity, message: bounded };

    this._notifications.update((queue) => {
      // Written as a surplus count rather than a `length === cap` test so that it
      // is total: it collapses to a plain append for every depth below the cap and
      // still returns a correctly capped queue for any depth at or above it.
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

  error(message: string): void {
    this.notify('error', message);
  }

  /**
   * Removes the entry carrying `id`, if one is present.
   *
   * An id that was never issued, or that a clear has already discarded, is a
   * no-op rather than an error. Surviving entries keep their identity and order.
   *
   * @param id The {@link AppNotification.id} to remove.
   */
  dismiss(id: number): void {
    this._notifications.update((queue) => queue.filter((entry) => entry.id !== id));
  }

  /**
   * Empties the queue by restoring the shared empty instance, so clearing an
   * already-empty queue publishes an unchanged reference and notifies nobody.
   */
  clear(): void {
    this._notifications.set(EMPTY_QUEUE);
  }
}
