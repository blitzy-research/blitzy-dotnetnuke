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

  /**
   * Whether this entry is meant to outlive the next change of screen.
   *
   * ⚠ THE DEFAULT IS `false`, AND THAT IS THE WHOLE POINT. A notification describes the screen it was
   * raised on, so once the operator has left that screen it is no longer describing anything they can
   * see. Two severities never expire on their own — {@link NotificationSeverity} `'warning'` and
   * `'error'` — which is correct while the operator is still on the screen the fault belongs to and
   * became a leak the moment they navigated away: a refusal raised on one screen sat over an unrelated
   * one indefinitely, still telling the operator to correct fields that no longer existed.
   *
   * MIGRATION: the leak is deliberately NOT fixed by giving `'warning'` and `'error'` an expiry. A fault
   * an operator has not acted on must not vanish from under them on a timer while they are reading it,
   * which is why those two severities are exempt from the countdown in the first place. Screen lifetime
   * and elapsed time are different questions, and this member answers the first without disturbing the
   * second.
   *
   * `true` buys exactly ONE change of screen and is then spent — see
   * {@link NotificationService.dismissStale}, which flips it rather than leaving it set. A permanent flag
   * would simply reintroduce the leak under a nicer name. Exactly two kinds of caller need it, and both
   * share one shape: they raise a statement and then deliberately navigate, intending it to be read at the
   * destination. A create confirmation raised immediately before redirecting to the listing is one; a
   * session-ended notice raised immediately before ejecting to the sign-in screen is the other.
   */
  readonly survivesNavigation: boolean;

  /**
   * Whether this entry retires itself on the surface's countdown, or `null` to let the surface decide from
   * {@link AppNotification.severity} as it always has.
   *
   * ⚠ SEVERITY IS A PROXY FOR "DOES THIS NEED THE READER TO DO SOMETHING", AND ON ONE NOTIFICATION IT IS
   * THE WRONG PROXY. The surface retires the two severities that require nothing of the reader and exempts
   * `'warning'` and `'error'` on three stated grounds: they report something that did NOT happen, they
   * frequently carry the support reference an operator has to quote, and removing them on a timer would
   * destroy the only record of a failure. Every one of those grounds is about a FAULT - something the
   * operator has to act on, or report to someone who can.
   *
   * The route guards' access refusal satisfies none of them. It carries no reference, because nothing
   * failed and there is nothing to look up; and it asks for nothing, because the remedy is a permission
   * the operator does not hold and cannot grant themselves. The blanket exemption applied to it anyway, on
   * the strength of its severity alone, and a browser audit measured the consequence: the refusal stood on
   * screen for four minutes and forty-two seconds and was cleared only by navigating away.
   *
   * Deliberately NOT fixed by giving `'warning'` a timer - the block above and the surface's own set both
   * refuse that with a measurement, and both are right - and deliberately not fixed by lowering the refusal
   * to `'info'` so that it slips past the set: severity is derived from the response status in ONE shared
   * place, and choosing a quieter word for a refusal in order to reach a timer would corrupt that decision
   * to buy a display behaviour. Lifetime is asked as its own question instead, of the only party that knows
   * the answer. This service already holds that callers choose the severity and nothing here derives one;
   * this member extends the same rule to expiry.
   *
   * `null` is the default and preserves today's behaviour exactly, so a caller with no opinion keeps the
   * severity-derived lifetime and nothing already on screen changes.
   */
  readonly selfDismisses: boolean | null;

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

// =============================================================================
//  WHERE THE COUNTDOWN LIVES, AND WHY IT IS NOT HERE
// =============================================================================
//  THIS SERVICE OWNS THE QUEUE AND NOT THE CLOCK. Timed dismissal is armed by the surface that
//  renders the queue, and that placement is forced rather than chosen: the countdown has to stop
//  while a person is reading or operating the region, and whether a pointer is over the region or
//  focus is inside it are facts only the surface can observe. A service holding the timer cannot
//  see either, so its timer fires straight through the pause - measured, as six failing cases -
//  and no amount of care in the service can recover the information it does not have.
//
//  The surface therefore declares the interval, the severities that retire themselves, and the
//  pause. It arms its timers FROM this queue, so an entry removed here - dismissed, displaced by
//  the depth cap, replaced by a repeat, or swept on a change of screen - takes its countdown with
//  it, and nothing has to be released alongside it.
//
//  ⚠ A WARNING AND AN ERROR ARE NOT RETIRED ON A TIMER, and an earlier revision here did retire a
//  warning after a longer interval. The measurement that argued for it was a warning found sitting
//  on screen for eighty-seven seconds, on a screen it had followed the reader to, complaining about
//  a request that screen had never made. That defect is real and it is now answered by
//  {@link NotificationService.clearOnNavigation} and the sweep the surface drives on a change of
//  path, which is a narrower instrument than a clock: it removes the stale entry precisely because
//  it is stale, and leaves a warning that is still about the screen in hand alone. Those two
//  severities frequently carry the support reference an operator has to quote, so on the screen
//  that raised them they persist until they are dismissed.

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
// so this queue holds no cache and expires nothing on a timer: it is a transient view-model slice and
// nothing more. The collapsing of an immediate repetition in `notify` is NOT a cache either, and must not be
// read as one - it is keyless, it looks only at the single newest entry, and it retains the NEW occurrence
// rather than serving the old one back.
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
 * It deliberately owns no presentation behaviour - no auto-dismiss window and no animation, both of which
 * belong to the surface that renders the queue - and no interpretation of transport failures: whoever
 * reports an outcome has already chosen the severity and composed the text. Every operation replaces the
 * queue rather than mutating it in place.
 *
 * It DOES collapse an immediate repetition of the newest entry; see {@link NotificationService.notify}.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly _notifications = signal<readonly AppNotification[]>(EMPTY_QUEUE);

  /**
   * The entries exempted from the next navigation sweep, by identifier.
   *
   * Populated by {@link retainAcrossNavigation} and emptied by every sweep, so an exemption lasts
   * for exactly one navigation and cannot accumulate.
   */
  private readonly retained = new Set<number>();

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
   * ⚠ AN IMMEDIATE REPETITION OF THE NEWEST ENTRY IS COLLAPSED RATHER THAN STACKED. When the entry
   * currently at the end of the queue carries the same severity, the same composed message and the same
   * reference, it is REPLACED by the new one - removed and re-appended with a fresh id - so the queue never
   * holds two adjacent rows a person cannot tell apart, while the newest occurrence is still the one on
   * screen and is still announced.
   *
   * Runtime testing is what forced this. One fault was routinely reported twice at once, and the role
   * membership screen raised THREE near-identical warnings for a single fault, so an operator was asked to
   * read the same sentence three times to discover it said the same thing three times. Refusing the repeat
   * outright was rejected: a person who performs the same action twice - deleting two roles in a row, each
   * answering with the same confirmation - must get feedback for the second one, and re-appending with a new
   * id is what re-announces it in a live region whose entries are announced individually.
   *
   * Only the NEWEST entry is compared. A repetition further back is left alone, because the entries between
   * them are evidence that something else happened in between and collapsing across that would reorder the
   * record of what the operator did.
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
      // The newest entry, and whether this one repeats it. Compared on EVERY stored member other than the
      // identifier, so a second occurrence differing in any respect an operator could observe is a
      // genuinely different report and survives as its own row.
      //
      // ⚠ THE LIFETIME OPINION IS PART OF THE COMPARISON. Two entries wording the same sentence at the same
      // severity are still different reports when one of them expires and the other does not, and collapsing
      // them would silently impose the FIRST one's lifetime on the second - so a refusal raised where it is
      // meant to retire itself could be held permanently by an identical earlier entry that was not.
      //
      // ⚠ AND SO IS THE NAVIGATION OPINION, WHICH WAS MISSING. `survivesNavigation` was excluded from this
      // test even though it is stored on the entry and governs whether the sweep discards it - so an entry
      // raised to be READ AFTER A REDIRECT could be collapsed into an identical earlier one that had not
      // claimed the exemption, and then swept away by the very navigation it existed to survive. The
      // operator was redirected and the explanation was gone: the same class of defect the lifetime
      // comparison beside it exists to prevent, on the member that decides survival rather than duration.
      // It is not hypothetical - a session ending un-asked-for raises its sentence with the exemption while
      // ordinary refusals raise the same wording without it, so the two shapes genuinely meet in one queue.
      const newest = queue.at(-1);
      const repeats =
        newest !== undefined &&
        newest.severity === entry.severity &&
        newest.message === entry.message &&
        newest.reference === entry.reference &&
        newest.survivesNavigation === entry.survivesNavigation &&
        newest.selfDismisses === entry.selfDismisses;
      const retained = repeats ? queue.slice(0, -1) : queue;

      // Written as a surplus count rather than a `length === cap` test so that it is total: it collapses to
      // a plain append for every depth below the cap and still returns a correctly capped queue for any
      // depth at or above it.
      const surplus = retained.length + 1 - MAX_QUEUED_NOTIFICATIONS;

      // A REPEAT REPLACES THE ENTRY IT REPEATS, so the queue never holds the same sentence twice in a
      // row. Nothing has to be released alongside it: the countdown belongs to the SURFACE, which arms
      // its timers from this queue, so an entry that leaves the queue takes its countdown with it.
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
   * @param survivesNavigation Whether the statement must outlive the next change of screen. Pass `true`
   *   ONLY when this call is immediately followed by a deliberate navigation whose destination is where the
   *   statement is meant to be read; see {@link AppNotification.survivesNavigation}.
   */
  success(message: string, survivesNavigation = false): void {
    this.notify('success', message, null, survivesNavigation);
  }

  /**
   * Queues a neutral statement.
   *
   * @param message The statement to present.
   * @param survivesNavigation Whether the statement must outlive the next change of screen. Pass `true`
   *   ONLY when this call is immediately followed by a deliberate navigation - or by an action that makes
   *   one inevitable, such as discarding the session - whose destination is where the statement is meant to
   *   be read.
   */
  info(message: string, survivesNavigation = false): void {
    this.notify('info', message, null, survivesNavigation);
  }

  /**
   * Queues a warning.
   *
   * ⚠ DO NOT USE THIS FOR A SERVER REFUSAL - CALL {@link NotificationService.notify} INSTEAD. This
   * helper cannot carry a support reference, and warning is exactly the severity a server refusal resolves
   * to: the shared classifier maps 401, 403, 404 and 429 to `'warning'`, and every one of those answers
   * arrives with a correlation identifier in its problem document. Reaching for this helper because the
   * severity matched silently discarded that identifier at three separate call sites, one of which left a
   * refused save with nothing quotable anywhere on the screen. The signature is deliberately left as it is,
   * because the many genuine client-side warnings below it - an incomplete form, a bound that cannot be
   * met - have no reference to quote and should not be made to pretend otherwise.
   *
   * @param message The statement to present.
   * @param survivesNavigation Whether the statement must outlive the next change of screen. Pass `true`
   *   ONLY when this call is immediately followed by a deliberate navigation whose destination is where the
   *   statement is meant to be read - a session-ended notice raised just before ejecting to the sign-in
   *   screen being the case this exists for.
   */
  warning(message: string, survivesNavigation = false): void {
    this.notify('warning', message, null, survivesNavigation);
  }

  /**
   * Queues a failure.
   *
   * The one convenience method that forwards a support reference.
   *
   * ⚠ IT IS NOT THE ONLY OUTCOME THAT HAS ONE TO QUOTE, WHICH THIS NOTE USED TO CLAIM, and the claim
   * was the reason references went missing. A refusal answered `403` or `404` also carries a correlation
   * identifier, and the shared classifier resolves both to `'warning'` - so the belief that only an
   * error-severity outcome has an identifier is contradicted by the very function that assigns the
   * severity. What is true is narrower: a SUCCESS has nothing to quote, because nothing failed. Any
   * outcome derived from a problem document has an identifier regardless of the severity it resolves to,
   * and must reach {@link NotificationService.notify} so it can pass one.
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
   * Discards every notification that a change of screen has made stale.
   *
   * ⚠ THE ONE THING THIS QUEUE HAD NO WAY TO DO, AND THE MEASUREMENT IS THE ARGUMENT. The queue
   * is root-scoped while the surface that renders it lives in the persistent shell, so an entry
   * survived every route change and every screen: an empty-submit warning raised on the module
   * creation screen was still on screen, word for word, after seven in-application navigations
   * across five different feature areas, and nothing but the dismiss control could clear it. It
   * was not cosmetic either — on the portal listing it occupied a full-width band that pushed the
   * page heading down by 42px and displaced the whole grid.
   *
   * A notification states the outcome of an action taken on a SCREEN, so leaving that screen
   * retires it. This is called from the shell on a completed navigation, which is the one event
   * that means the operator has genuinely arrived somewhere else — a navigation a guard is about
   * to refuse has not moved them, so its entries must survive.
   *
   * ⚠ AN ENTRY RAISED BY THE ARRIVAL ITSELF MUST NOT BE SWEPT AWAY BY THE ARRIVAL. A guard that
   * refuses a destination announces WHY, and a session that has expired announces THAT, and both
   * happen as part of the navigation the caller is reporting here. Such an entry is therefore
   * exempted for the remainder of the current task, which is what {@link retainAcrossNavigation}
   * marks and what the arrival-time announcements use.
   */
  clearOnNavigation(): void {
    this.sweep();
  }

  /**
   * Exempts the entry raised most recently from the NEXT navigation sweep.
   *
   * TWO CLASSES OF CALLER NEED THIS, and the second was discovered by measurement rather than
   * reasoned about in advance:
   *
   *  1. An announcement that is itself the result of ARRIVING somewhere — a guard's refusal, a
   *     forced sign-out. Without the exemption the message is raised and swept within one task and
   *     the operator is moved with no explanation at all.
   *  2. An outcome announced by a screen that then LEAVES. A save, a delete, or a record that
   *     turned out not to exist: the screen states the outcome and navigates in the same task, and
   *     the whole point of the message is to be read at the destination — which is where the legacy
   *     showed it, since its handlers announced and then redirected. Without the exemption the
   *     sweep discards it before it can be painted, and the effect is total silence: an update that
   *     succeeded, a role that was deleted, and a record that could not be found all look
   *     identical to an operator who is simply moved back to a listing.
   *
   *     ⚠ MEASURED, NOT SUSPECTED. Saving a role and then following an unknown role id were both
   *     driven in a real browser: the destination's live region stayed empty, and 226 consecutive
   *     video frames after the listing painted were pixel-identical to the final frame, so nothing
   *     appeared and nothing was dismissed. The queue was working; the sweep was faster.
   *
   * A caller in the second class must call this immediately after raising the entry and before
   * requesting the navigation, so the exemption is in place when the sweep runs.
   *
   * ⚠ IF YOU EVER RE-CENSUS THE SECOND CLASS, SEARCH FOR THE CONVENIENCE METHODS TOO. The first
   * survey looked for `notify(` and found six sites; a second pass that also looked for `success(`,
   * `warning(`, `error(` and `info(` found four more, including a settings screen whose own comment
   * asserted that its confirmation 'outlives the route change' - the exact claim this sweep had
   * falsified. A saved-then-left screen is just as likely to announce through a convenience method as
   * through the general one.
   *
   * Returns the caller nothing, and deliberately takes nothing: an announcement is exempted by
   * being the latest, so a caller cannot exempt an unrelated entry by holding on to an old
   * identifier.
   */
  retainAcrossNavigation(): void {
    const queue = this._notifications();
    const latest = queue.at(-1);

    if (latest !== undefined) {
      this.retained.add(latest.id);
    }
  }

  /**
   * Empties the queue by restoring the shared empty instance, so clearing an already-empty queue publishes
   * an unchanged reference and notifies nobody.
   */
  clear(): void {
    this.retained.clear();
    this._notifications.set(EMPTY_QUEUE);
  }

  /**
   * Discards the entries that described the screen the operator has just left.
   *
   * Called by the surface that renders this queue, once per change of SCREEN. It removes every entry whose
   * {@link AppNotification.survivesNavigation} is `false`, and spends the flag on those where it is `true`
   * by rewriting them with it cleared — so a reprieve is worth exactly one change of screen and the entry
   * is an ordinary one thereafter.
   *
   * ⚠ "CHANGE OF SCREEN" DELIBERATELY MEANS A CHANGE OF PATH, NOT OF ADDRESS, and the caller is what
   * enforces that. Paging, filtering and sorting are all carried in the query string, so treating any
   * address change as a change of screen would clear a refusal the operator is still reading the moment
   * they turned a page — and would make the fix indistinguishable from the fault it replaces, since in
   * both cases a message the operator wanted vanishes without being read.
   *
   * Order and identity are preserved for survivors: a spent entry keeps its {@link AppNotification.id}, so
   * a dismissal timer already armed against it still finds it, and keeps its position, so a queue of two
   * does not reorder as one is spent.
   *
   * Publishes the shared empty instance when nothing survives, matching {@link NotificationService.clear},
   * and returns the queue UNTOUCHED when nothing would change — so the common case, navigating with an
   * empty queue or with nothing but already-spent entries, notifies no dependent and schedules no work.
   */
  dismissStale(): void {
    this.sweep();
  }

  /**
   * Discards the previous screen's entries, keeping the ones exempted from this sweep.
   *
   * ⚠ THERE ARE TWO WAYS TO CLAIM AN EXEMPTION AND THIS HONOURS BOTH, which is the whole reason the
   * sweep is written once here instead of twice at the two public names above. A caller states the
   * claim either when the entry is raised - {@link NotificationService.notify}'s survival argument,
   * which records a flag ON the entry - or afterwards, through
   * {@link NotificationService.retainAcrossNavigation}, which records the entry's identifier in a set.
   * The two forms exist because the two kinds of caller differ: a screen announcing its own departure
   * knows at the moment it speaks, whereas an interceptor, a guard or a session teardown learns only
   * once the navigation it is reacting to is already under way. A sweep that consulted one form and not
   * the other silently destroyed every message raised through the other - measured, as a confirmation
   * that was announced, retained and then swept before it could be painted.
   *
   * AN EXEMPTION LASTS FOR EXACTLY ONE SWEEP, in both forms: a survivor is rewritten with its flag
   * lowered and the identifier set is emptied, so nothing can accumulate a permanent immunity.
   *
   * The reference identity is deliberate. An empty queue is republished as itself so no dependent
   * re-runs for a sweep with nothing to do; a queue swept clean publishes the shared empty instance;
   * and a queue with survivors publishes a new array, because every survivor's flag genuinely changed
   * and a consumer testing it must see the new value.
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
