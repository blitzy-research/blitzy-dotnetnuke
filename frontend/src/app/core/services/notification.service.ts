import { HttpContext, HttpContextToken } from '@angular/common/http';
import { Injectable, signal, type Signal } from '@angular/core';

/**
 * Closed severity vocabulary for a queued notification. `'warning'` is a first-class member rather than a
 * synonym for `'error'`, because an authorisation denial is presented as a warning rather than as a
 * failure.
 */
export type NotificationSeverity = 'success' | 'info' | 'warning' | 'error';

/** Marks a request whose failure is ALREADY presented by whoever issued it. */
export const PRESENTED_IN_CONTEXT = new HttpContextToken<boolean>(() => false);

/**
 * Builds, or extends, an {@link HttpContext} marked as presented by its caller. Takes an optional
 * existing context so that a call site already carrying one — for a future token — sets this marker on it
 * rather than replacing it.
 *
 * @param context An existing context to extend, or none to start a fresh one.
 * @returns The context, carrying {@link PRESENTED_IN_CONTEXT}.
 */
export function presentedInContext(context: HttpContext = new HttpContext()): HttpContext {
  return context.set(PRESENTED_IN_CONTEXT, true);
}

/** One queued notification. */
export interface AppNotification {
  readonly id: number;

  readonly severity: NotificationSeverity;

  /**
   * The already-composed, display-ready message, treated strictly as plain text. Passed through verbatim
   * - no trimming, no case change and no substitution.
   */
  readonly message: string;

  readonly reference: string | null;

  /**
   * Whether this entry is meant to outlive the next change of screen. ⚠ THE DEFAULT IS `false`, AND THAT
   * IS THE WHOLE POINT. A notification describes the screen it was raised on, so once the operator has
   * left that screen it is no longer describing anything they can see.
   */
  readonly survivesNavigation: boolean;

  /**
   * Whether this entry retires itself on the surface's countdown, or `null` to let the surface decide
   * from {@link AppNotification.severity} as it always has. ⚠ SEVERITY IS A PROXY FOR "DOES THIS NEED THE
   * READER TO DO SOMETHING", AND ON ONE NOTIFICATION IT IS THE WRONG PROXY. The surface retires the two
   * severities that require nothing of the reader and exempts `'warning'` and `'error'` on three stated
   * grounds: they report something that did NOT happen, they frequently carry the support reference an
   * operator has to quote, and removing them on a timer would destroy the only record of a failure.
   */
  readonly selfDismisses: boolean | null;
}

/**
 * The single empty-queue instance, used both as the initial value and as the value {@link
 * NotificationService.clear} restores.
 */
const EMPTY_QUEUE: readonly AppNotification[] = Object.freeze([]);

/**
 * The greatest number of UTF-16 code units of a single message this service will retain. One of the two
 * paths into this service is not under the application's control: the error interceptor composes its
 * message from a server `ProblemDetails` payload, whose `detail` and `errors` members are remote input.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * The greatest number of entries the queue will hold at once. Beyond this depth the oldest entry is
 * dropped as the newest is appended, so the queue behaves as a fixed-capacity window over the most recent
 * notifications.
 */
const MAX_QUEUED_NOTIFICATIONS = 25;

/**
 * Applies {@link MAX_MESSAGE_LENGTH} to one message.
 *
 * @param message The already-composed, display-ready plain-text message.
 * @returns The message unchanged, or its leading {@link MAX_MESSAGE_LENGTH} code units when it is longer.
 */
function boundMessage(message: string): string {
  return boundText(message, MAX_MESSAGE_LENGTH);
}

/** The greatest number of UTF-16 code units of a support reference this service will retain. */
const MAX_REFERENCE_LENGTH = 128;

/**
 * Introduces the support reference within a composed message. The wording matches the label
 * `error.interceptor.ts` used when it composed the suffix itself, so no rendered message changes as a
 * result of moving the composition here.
 */
const REFERENCE_LABEL = 'Reference:';

/**
 * Retains at most `limit` UTF-16 code units of `text`, without splitting a surrogate pair.
 *
 * @param text The text to bound.
 * @param limit The greatest number of code units to retain.
 * @returns The text unchanged, or its leading `limit` code units when it is longer.
 */
function boundText(text: string, limit: number): string {
  if (text.length <= limit) {
    return text;
  }

  // `String.prototype.length` counts UTF-16 code units rather than characters, so a cut at exactly the
  // bound can land between the two halves of a surrogate pair and leave a lone high surrogate as the final
  // unit. Stepping back one unit in that case keeps the retained text well-formed.
  const lastRetainedUnit = text.charCodeAt(limit - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return text.slice(0, cutSplitsSurrogatePair ? limit - 1 : limit);
}

/**
 * Bounds a support reference and refuses a blank one. A reference that carries no visible text is treated
 * as absent rather than quoted, because quoting it would leave a dangling `Reference:` label with nothing
 * after it — an instruction to report an identifier that was never issued.
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

// MIGRATION: localisation is not ported.

/**
 * Holds an in-memory, append-ordered queue of user-facing notifications, each one a severity plus an
 * already-composed message.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly _notifications = signal<readonly AppNotification[]>(EMPTY_QUEUE);

  /** The entries exempted from the next navigation sweep, by identifier. */
  private readonly retained = new Set<number>();

  /**
   * The queue, oldest entry first. Read-only at compile time: the `readonly` element type and the
   * read-only signal make an accidental write a compile error, they do not harden the array at runtime.
   */
  readonly notifications: Signal<readonly AppNotification[]> = this._notifications.asReadonly();

  /**
   * A plain counter, never rewound - neither by a dismissal nor by {@link NotificationService.clear} - so
   * an id is unique for the whole service lifetime and stays usable as a template track key.
   */
  private nextId = 1;

  /**
   * Appends one notification to the end of the queue, unless the message is blank. The severity is taken
   * as given, and a message that carries any visible text is stored **verbatim** - never trimmed,
   * case-folded, escaped, decoded, normalised or reformatted, since reformatting a message that does have
   * content would be a display decision.
   *
   * @param severity The already-decided severity to render at.
   * @param message The already-composed, display-ready plain-text message.
   * @param reference The support reference to quote, or `null` when the outcome has none.
   */
  notify(
    severity: NotificationSeverity,
    message: string,
    reference: string | null = null,
    survivesNavigation = false,
    selfDismisses: boolean | null = null,
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
      survivesNavigation,
      selfDismisses,
    };

    this._notifications.update((queue) => {
      // ⚠ THE LIFETIME OPINION IS PART OF THE COMPARISON. Two entries wording the same sentence at the same
      // severity are still different reports when one of them expires and the other does not, and
      // collapsing them would silently impose the FIRST one's lifetime on the second - so a refusal raised
      // where it is meant to retire itself could be held permanently by an identical earlier entry that was
      // not.
      const newest = queue.at(-1);
      const repeats =
        newest !== undefined &&
        newest.severity === entry.severity &&
        newest.message === entry.message &&
        newest.reference === entry.reference &&
        newest.survivesNavigation === entry.survivesNavigation &&
        newest.selfDismisses === entry.selfDismisses;
      const retained = repeats ? queue.slice(0, -1) : queue;

      const surplus = retained.length + 1 - MAX_QUEUED_NOTIFICATIONS;

      if (surplus > 0) {
        return [...retained.slice(surplus), entry];
      }

      return [...retained, entry];
    });
  }

  /**
   * Queues a confirmation.
   *
   * @param message The statement to present.
   * @param survivesNavigation Whether the statement must outlive the next change of screen.
   */
  success(message: string, survivesNavigation = false): void {
    this.notify('success', message, null, survivesNavigation);
  }

  /**
   * Queues a neutral statement.
   *
   * @param message The statement to present.
   * @param survivesNavigation Whether the statement must outlive the next change of screen.
   */
  info(message: string, survivesNavigation = false): void {
    this.notify('info', message, null, survivesNavigation);
  }

  /**
   * Queues a warning. ⚠ DO NOT USE THIS FOR A SERVER REFUSAL - CALL {@link NotificationService.notify}
   * INSTEAD. This helper cannot carry a support reference, and warning is exactly the severity a server
   * refusal resolves to: the shared classifier maps 401, 403, 404 and 429 to `'warning'`, and every one
   * of those answers arrives with a correlation identifier in its problem document.
   *
   * @param message The statement to present.
   * @param survivesNavigation Whether the statement must outlive the next change of screen.
   */
  warning(message: string, survivesNavigation = false): void {
    this.notify('warning', message, null, survivesNavigation);
  }

  /**
   * Queues a failure. The one convenience method that forwards a support reference. ⚠ IT IS NOT THE ONLY
   * OUTCOME THAT HAS ONE TO QUOTE, WHICH THIS NOTE USED TO CLAIM, and the claim was the reason references
   * went missing.
   *
   * @param message The already-composed, display-ready plain-text message.
   * @param reference The support reference to quote, or `null` when there is none.
   */
  error(message: string, reference: string | null = null): void {
    this.notify('error', message, reference);
  }

  /**
   * Removes the entry carrying `id`, if one is present. An id that was never issued, or that a clear has
   * already discarded, is a no-op rather than an error.
   *
   * @param id The {@link AppNotification.id} to remove.
   */
  dismiss(id: number): void {
    this._notifications.update((queue) => queue.filter((entry) => entry.id !== id));
  }

  /**
   * Discards every notification that a change of screen has made stale. ⚠ THE ONE THING THIS QUEUE HAD NO
   * WAY TO DO, AND THE MEASUREMENT IS THE ARGUMENT. The queue is root-scoped while the surface that
   * renders it lives in the persistent shell, so an entry survived every route change and every screen:
   * an empty-submit warning raised on the module creation screen was still on screen, word for word,
   * after seven in-application navigations across five different feature areas, and nothing but the
   * dismiss control could clear it.
   */
  clearOnNavigation(): void {
    this.sweep();
  }

  /**
   * Exempts the entry raised most recently from the NEXT navigation sweep. TWO CLASSES OF CALLER NEED
   * THIS, and the second was discovered by measurement rather than reasoned about in advance: 1.
   */
  retainAcrossNavigation(): void {
    const queue = this._notifications();
    const latest = queue.at(-1);

    if (latest !== undefined) {
      this.retained.add(latest.id);
    }
  }

  /**
   * Empties the queue by restoring the shared empty instance, so clearing an already-empty queue
   * publishes an unchanged reference and notifies nobody.
   */
  clear(): void {
    this.retained.clear();
    this._notifications.set(EMPTY_QUEUE);
  }

  /** Discards the entries that described the screen the operator has just left. */
  dismissStale(): void {
    this.sweep();
  }

  /**
   * Discards the previous screen's entries, keeping the ones exempted from this sweep. ⚠ THERE ARE TWO
   * WAYS TO CLAIM AN EXEMPTION AND THIS HONOURS BOTH, which is the whole reason the sweep is written once
   * here instead of twice at the two public names above.
   */
  private sweep(): void {
    this._notifications.update((queue) => {
      if (queue.length === 0) {
        return queue;
      }

      const survivors = queue.filter(
        (entry) => entry.survivesNavigation || this.retained.has(entry.id),
      );

      if (survivors.length === 0) {
        return EMPTY_QUEUE;
      }

      return Object.freeze(survivors.map((entry) => ({ ...entry, survivesNavigation: false })));
    });

    this.retained.clear();
  }
}
