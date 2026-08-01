/**
 * @fileoverview Client-side notification surface for the `dnn-migration`
 * administration application.
 *
 * This service owns one small, in-memory, append-ordered queue of user-facing
 * notifications. Every entry is nothing more than a **severity** plus an
 * **already-composed, plain-text message**.
 *
 * ## Position in the architecture
 *
 * It is one of only two files under `core/services/` that is not an HTTP
 * wrapper - the other being `token-storage.service.ts`. It performs no network
 * I/O whatsoever and imports no transport package.
 *
 * ## Producers and consumers
 *
 * - **Producer** - `core/interceptors/error.interceptor.ts` (planned path) calls
 *   {@link NotificationService.notify} once it has *already* chosen a severity
 *   and *already* composed a display string.
 * - **Producer** - feature components report post-mutation outcomes
 *   (`POST` 201, `PUT` 200, `DELETE` 204) through the thin severity aliases.
 * - **Consumer** - the shared `error-banner` component reads
 *   {@link NotificationService.notifications} and renders each `message` as
 *   **text content**, never as markup.
 *
 * ## Deliberate non-responsibilities
 *
 * The governing directive for Angular services in this migration is that they
 * are restricted to API communication. This file is a bounded exception in
 * *kind* - a client-side UI surface rather than a transport wrapper - but it is
 * emphatically **not** an exception to the prohibition on logic. It therefore
 * holds, by design:
 *
 * - no business logic, no domain decisions and no derivation of any kind;
 * - no HTTP status interpretation - mapping a status code onto a severity is
 *   the error interceptor's job (see `MIGRATION` note 1 below);
 * - no RFC 7807 payload parsing;
 * - no presentation behaviour - no auto-dismiss timer, no animation, no
 *   positioning, no de-duplication window. Auto-dismiss, if it is ever wanted,
 *   belongs to the consuming component;
 * - no localisation (see `MIGRATION` note 4 below);
 * - no caching (see `MIGRATION` note 3 below).
 *
 * @see Website/admin/Security/AccessDenied.ascx.vb - the legacy,
 *      presentation-only denial page that establishes both the severity rule and
 *      the plain-text rule recorded in the `MIGRATION` notes below.
 */

import { Injectable, signal, type Signal } from '@angular/core';

// MIGRATION: an HTTP 403 surfaces at 'warning' severity, not at 'error'.
//
// Measured first-hand in Website/admin/Security/AccessDenied.ascx.vb: `Page_Load`
// (L41-L47) performs NO permission check at all - it only *presents* a denial
// that was already decided elsewhere. Both of its branches render at
// `ModuleMessage.ModuleMessageType.YellowWarning`:
//   * L43 for an inbound query-string message, and
//   * L45 for the localised "AccessDenied" fallback.
// (`YellowWarning` is the only member of that legacy enum observable from this
// file's source, so it is the only one cited.)
//
// The severity *decision* for a given status code is made upstream, in
// `core/interceptors/error.interceptor.ts`. This vocabulary exists only so that
// the decision can be expressed faithfully - which is why 'warning' is a
// first-class member below rather than a synonym for 'error'.

/**
 * The closed severity vocabulary a queued notification may carry.
 *
 * Exactly four members, and deliberately no more:
 *
 * - `'success'` and `'info'` carry post-mutation outcomes (`POST` 201,
 *   `PUT` 200, `DELETE` 204).
 * - `'warning'` carries the legacy `YellowWarning` case documented immediately
 *   above - most importantly an authorisation denial.
 * - `'error'` carries genuine failures.
 *
 * Declared as a string-literal union rather than an enum: the workspace enables
 * `isolatedModules`, under which an inlined constant enum is not legal, and a
 * plain enum would emit a runtime object for no benefit.
 *
 * @remarks
 * This type is declared here rather than under `core/models/` on purpose. That
 * folder holds exactly nine wire-contract models - portal, module, user, role,
 * permission, tab, auth, paged-result and problem details - and a notification
 * is a purely client-side concern that never crosses the wire, so it has no
 * place among them.
 */
export type NotificationSeverity = 'success' | 'info' | 'warning' | 'error';

/**
 * A single queued, user-facing notification.
 *
 * Every member is `readonly`: entries are immutable value objects, and the queue
 * that holds them is replaced wholesale rather than mutated in place.
 *
 * @remarks
 * Named `AppNotification`, never bare `Notification`. The workspace compiles
 * against the DOM library, which already declares a global `Notification` (the
 * Web Notifications API); an unprefixed name would shadow it and invite a silent
 * mix-up between an in-page banner and an operating-system notification.
 *
 * @example
 * ```ts
 * // Produced internally by NotificationService.notify('warning', 'Access denied.')
 * const entry: AppNotification = { id: 1, severity: 'warning', message: 'Access denied.' };
 * ```
 */
export interface AppNotification {
  /**
   * Stable, deterministic identifier, unique for the lifetime of the service
   * instance. Monotonically increasing from 1, and never reused - not even after
   * {@link NotificationService.clear}. Consumers use it both as the argument to
   * {@link NotificationService.dismiss} and as the `@for` track key.
   */
  readonly id: number;

  /** The already-decided severity. This service never derives it. */
  readonly severity: NotificationSeverity;

  // MIGRATION: `message` is PLAIN TEXT and is rendered as text content, never as
  // markup - because legacy message wording is untrusted HTML.
  //
  // Measured across the 37 in-scope
  // Website/admin/{Portal,Users,Security,Modules,Tabs}/App_LocalResources/*.resx
  // files (1211 <data> entries in total): 76 values carry at least one raw HTML
  // tag. One of them - `Advertising.Text` in
  // Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx - embeds a
  // live script element (type="text/javascript"), accounting for all four
  // script-tag occurrences in the corpus.
  //
  // The legacy code itself encodes rather than renders, which is the precedent
  // followed here: AccessDenied.ascx.vb:L43 wraps the inbound value in
  // HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...)) before displaying it.
  //
  // Consequently this service never treats a message as markup, so there is
  // nothing here to escape, strip or filter, and no trusted-HTML bypass is
  // offered to any consumer. Angular's default text interpolation is the whole
  // of the defence, and it is sufficient precisely because markup is never
  // honoured.
  /**
   * The already-composed, display-ready message, treated strictly as plain text.
   *
   * Passed through verbatim - no trimming, no truncation, no case change, no
   * substitution. Consumers must bind it as text content.
   */
  readonly message: string;
}

/**
 * The canonical empty queue.
 *
 * Frozen so the base state cannot be mutated even accidentally, and shared by
 * both the initial signal value and {@link NotificationService.clear} so that
 * clearing an already-empty queue is reference-stable and therefore a true
 * no-op under the signal's default `Object.is` equality check.
 */
const EMPTY_QUEUE: readonly AppNotification[] = Object.freeze([]);

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
// keeps no de-duplication window, expires nothing on a timer and evicts nothing:
// it is a transient view-model slice and nothing more.

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
 * Holds the transient queue of user-facing notifications and exposes it as a
 * read-only Angular signal.
 *
 * Registered with `providedIn: 'root'`, which makes it a tree-shakeable
 * application-singleton provider. No component anywhere in this application
 * declares it in a `providers` array. It injects nothing, so it intentionally
 * declares no constructor.
 *
 * State is held in a signal rather than a stream: signals are the v19-native
 * reactive primitive, they compose directly with `computed()` in consuming
 * stores, and they need no subscription management or teardown in templates.
 *
 * The queue is always replaced, never mutated in place, so every emission hands
 * consumers a fresh immutable array and `ChangeDetectionStrategy.OnPush`
 * components refresh reliably.
 *
 * @example
 * ```ts
 * private readonly notificationService = inject(NotificationService);
 *
 * // Read (e.g. in an error-banner component):
 * protected readonly entries = this.notificationService.notifications;
 *
 * // Write:
 * this.notificationService.success('Portal saved.');
 * this.notificationService.warning('Access denied.');
 * ```
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  /**
   * The single writable state slice. Private so that the queue can only ever be
   * changed through this class's own vocabulary.
   *
   * Declared before {@link NotificationService.notifications} deliberately:
   * class field initialisers run in declaration order, so the read-only
   * projection below is guaranteed to see an initialised signal.
   */
  private readonly _notifications = signal<readonly AppNotification[]>(EMPTY_QUEUE);

  /**
   * The queue, in insertion order, as a read-only signal.
   *
   * Backed by `asReadonly()`, so consumers can read and derive from it but have
   * no `set` or `update` to call. Combined with the `readonly` members of
   * {@link AppNotification} and the replace-never-mutate discipline below, the
   * queue cannot be altered from outside this class.
   *
   * @returns The current entries; an empty array when nothing is queued.
   */
  readonly notifications: Signal<readonly AppNotification[]> = this._notifications.asReadonly();

  /**
   * Source of the next {@link AppNotification.id}.
   *
   * A plain monotonic counter, deliberately not a random or time-derived value:
   * ids stay deterministic, which keeps specs exact and makes `@for` track keys
   * stable. It is never reset - not even by {@link NotificationService.clear} -
   * so an id captured from a dismissed entry can never collide with a future
   * one.
   */
  private nextId = 1;

  /**
   * Appends one notification to the end of the queue.
   *
   * Both arguments are taken as given: the severity is already decided and the
   * message is already composed. Nothing is inspected, reinterpreted or
   * reformatted.
   *
   * Edge cases, all intentional pass-through behaviour rather than oversight:
   * - an empty `message` is queued as-is - suppressing it would be a display
   *   decision, which belongs to the consuming component;
   * - two identical `(severity, message)` pairs produce two distinct entries
   *   with distinct ids, because no de-duplication window exists here;
   * - the queue is unbounded, because trimming it would be a presentation
   *   policy.
   *
   * @param severity The already-decided severity to render at.
   * @param message The already-composed, display-ready plain-text message.
   */
  notify(severity: NotificationSeverity, message: string): void {
    const entry: AppNotification = { id: this.nextId++, severity, message };
    this._notifications.update((queue) => [...queue, entry]);
  }

  /**
   * Queues a `'success'` notification. A thin alias for
   * {@link NotificationService.notify} with a fixed severity, carrying no extra
   * behaviour of its own.
   *
   * @param message The already-composed, display-ready plain-text message.
   */
  success(message: string): void {
    this.notify('success', message);
  }

  /**
   * Queues an `'info'` notification. A thin alias for
   * {@link NotificationService.notify} with a fixed severity, carrying no extra
   * behaviour of its own.
   *
   * @param message The already-composed, display-ready plain-text message.
   */
  info(message: string): void {
    this.notify('info', message);
  }

  /**
   * Queues a `'warning'` notification - the severity the legacy denial page
   * used for an authorisation failure, per `MIGRATION` note 1 above. A thin
   * alias for {@link NotificationService.notify} with a fixed severity, carrying
   * no extra behaviour of its own.
   *
   * @param message The already-composed, display-ready plain-text message.
   */
  warning(message: string): void {
    this.notify('warning', message);
  }

  /**
   * Queues an `'error'` notification. A thin alias for
   * {@link NotificationService.notify} with a fixed severity, carrying no extra
   * behaviour of its own.
   *
   * @param message The already-composed, display-ready plain-text message.
   */
  error(message: string): void {
    this.notify('error', message);
  }

  /**
   * Removes the entry carrying the given id, if one is present.
   *
   * Filtering is total, so an id that is absent - already dismissed, cleared, or
   * never issued - is simply a no-op rather than an error: a consumer racing a
   * dismiss against a clear must not be punished for it. Every surviving entry
   * keeps its identity and its relative order.
   *
   * @param id The {@link AppNotification.id} to remove.
   */
  dismiss(id: number): void {
    this._notifications.update((queue) => queue.filter((entry) => entry.id !== id));
  }

  /**
   * Empties the queue.
   *
   * Restores the shared frozen {@link EMPTY_QUEUE}, so calling this on an
   * already-empty queue changes no reference and therefore notifies no
   * dependent. {@link NotificationService.nextId} is intentionally left
   * untouched, keeping ids unique across the whole service lifetime.
   */
  clear(): void {
    this._notifications.set(EMPTY_QUEUE);
  }
}
