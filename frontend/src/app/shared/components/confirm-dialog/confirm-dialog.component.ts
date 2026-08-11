import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  NgZone,
  ViewChild,
  booleanAttribute,
  inject,
  type AfterViewInit,
  type OnDestroy,
} from '@angular/core';

/**
 * How long focus loss is watched for after the invoker is re-focused, in milliseconds.
 *
 * The window exists because the event that strips focus is NOT the dialog closing - it is
 * the response to the request the confirmation triggered. Measured on a real confirmed
 * alias deletion: the dialog tore down at t+18ms and returned focus correctly to its
 * still-connected opener, and the `204` landed at t+50ms and destroyed the row that opener
 * lived in. A single macrotask check scheduled at teardown resolved ~30ms too early, found
 * focus on a live element, and correctly declined to act - after which nothing looked again
 * and focus stayed on `<body>` indefinitely.
 *
 * 2000ms is chosen to cover a slow response by a wide margin - forty times the measured
 * gap - while staying short enough that the watch cannot outlive the interaction that
 * started it. A longer window would keep a document-wide observer alive across unrelated
 * work; a shorter one would reintroduce the defect on a loaded server.
 */
const FOCUS_RESCUE_WINDOW_MS = 2000;

/*
 * Default wording.
 *
 * No default may promise irreversibility. Two of the flows this dialog guards do
 * not delete anything: module removal is a soft delete that leaves the row in
 * place, and withdrawing a paid role assignment whose trial has been consumed
 * expires the assignment instead. Wording such as "cannot be undone" or
 * "permanently" would be factually wrong for them.
 *
 * MIGRATION: the wording is authored here rather than resolved per portal and
 * locale, so every consumer sees the same English text until a caller overrides
 * it through an input.
 */
const DEFAULT_TITLE = 'Confirm Delete';

const DEFAULT_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

const DEFAULT_CONFIRM_LABEL = 'Delete';

/**
 * Normalises a string that will be rendered as an accessible name.
 *
 * Trims first, then substitutes the supplied fallback when nothing survives, so
 * a value that is present but blank can never reach the DOM. Trimming is not
 * cosmetic here: the accessible-name computation already collapses surrounding
 * white space, so a value of `'   '` and a value of `''` are indistinguishable
 * to a screen reader and both must be treated as absent rather than only the
 * empty one.
 *
 * @param value The caller-supplied string.
 * @param fallback The guaranteed non-blank replacement.
 * @returns The trimmed value, or the fallback when the value is blank.
 */
function coerceNonBlank(value: string, fallback: string): string {
  // The parameter is typed `string`, but a JavaScript caller or an `any`-typed
  // binding can still deliver null or undefined, and `.trim()` on either would
  // throw a TypeError whose message names neither this component nor this
  // input. Coalescing first means a nullish value takes the fallback path
  // instead of failing.
  const normalised = (value ?? '').trim();

  return normalised.length === 0 ? fallback : normalised;
}

/**
 * Guarantees the dialog has an accessible name.
 *
 * The `<h2>` this value feeds is the target of the dialog's `aria-labelledby`,
 * so a blank value leaves an `alertdialog` that assistive technology announces
 * without ever saying what it is about — for a destructive confirmation, the one
 * piece of information the user most needs.
 *
 * A FALLBACK rather than a throw, and the difference from
 * `PageHeaderComponent.requireNonBlankTitle` is deliberate rather than an
 * inconsistency. That input is declared `required: true` with no default, so
 * absence is inexpressible and there is no value to fall back TO; throwing is
 * the only way it can refuse a blank. This input is optional and already owns a
 * measured default, so the safe outcome is available for free. Throwing here
 * would also fail at the worst possible moment: input assignment happens while
 * the consumer's control-flow block is already flipped, so the exception would
 * replace the confirmation with a broken view in the middle of a delete flow —
 * turning a naming defect into a functional one. Falling back yields a correctly
 * named dialog and lets the user complete or abandon the action.
 *
 * @param value The caller-supplied title.
 * @returns A non-blank title, never the empty string.
 */
export function coerceDialogTitle(value: string): string {
  return coerceNonBlank(value, DEFAULT_TITLE);
}

/**
 * Guarantees the confirming affordance has an accessible name.
 *
 * The label IS that button's accessible name: the severity glyph beside it is
 * `aria-hidden`, precisely so it cannot act as a naming source, which leaves a
 * blank label with nothing at all to fall back on. An unnamed destructive button
 * is the most consequential unnamed control this component could produce, so the
 * same guarantee applies here as to the title.
 *
 * @param value The caller-supplied label.
 * @returns A non-blank label, never the empty string.
 */
export function coerceConfirmLabel(value: string): string {
  return coerceNonBlank(value, DEFAULT_CONFIRM_LABEL);
}

/**
 * Elements that can plausibly receive keyboard focus.
 *
 * Deliberately broad, because the dialog body is author-supplied content whose
 * shape this component does not control; {@link isKeyboardFocusable} narrows the
 * matches. `input` carries no type filter because a selector-level
 * `:not([type="hidden"])` misses an input whose type was set as a property.
 */
const FOCUSABLE_CANDIDATE_SELECTOR: string = [
  'a[href]',
  'area[href]',
  'button',
  'input',
  'select',
  'textarea',
  'details > summary',
  'iframe',
  'audio[controls]',
  'video[controls]',
  '[contenteditable]',
  '[tabindex]',
].join(',');

/**
 * Backs the element ids this component publishes.
 *
 * Two dialogs mounted at once must not publish the same ids: duplicates break
 * the `aria-labelledby` and `aria-describedby` references that give each dialog
 * its accessible name and description.
 */
let confirmDialogInstanceCount = 0;

function nextConfirmDialogId(): string {
  confirmDialogInstanceCount += 1;
  return `app-confirm-dialog-${confirmDialogInstanceCount}`;
}

/**
 * Reads the focused element, treating "nothing in particular" as absent.
 *
 * `document.activeElement` reports the body when no element is focused, and
 * focusing the body later is indistinguishable from focusing nothing, so that
 * case is reported as absent and restoration is skipped rather than faked.
 *
 * @returns The focused element, or `undefined` when focus is nowhere meaningful.
 */
function resolveFocusedElement(): HTMLElement | undefined {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement)) {
    return undefined;
  }
  if (active === document.body) {
    return undefined;
  }
  return active;
}

/**
 * The class that hides the root element's overflow while a modal dialog is open.
 *
 * Declared in `styles/_reset.scss` alongside the `scrollbar-gutter` reservation that stops the
 * lock shifting the page sideways. The two halves must stay together: without the gutter the lock
 * removes the scrollbar and lurches the content, and without the lock the page scrolls behind the
 * confirmation.
 */
const SCROLL_LOCK_CLASS = 'dnn-scroll-locked';

/**
 * How many dialogs currently hold the background scroll lock.
 *
 * ⚠ A COUNTER RATHER THAN A BOOLEAN, so that the FIRST dialog to close cannot release a lock the
 * SECOND one still needs. Two confirmations can legitimately overlap for a moment - a screen
 * whose delete confirmation is destroyed in the same turn that another is created re-uses the
 * component, and Angular constructs the new instance before destroying the old - and a boolean
 * would leave the page scrollable underneath the surviving dialog.
 *
 * Module-scoped because the lock is a property of the DOCUMENT, not of any one dialog, and no
 * instance can see another. It is only ever adjusted by the two functions below.
 */
let scrollLockDepth = 0;

/**
 * Hides background scrolling while a modal confirmation is open.
 *
 * ⚠ A NATIVE MODAL DIALOG DOES NOT DO THIS BY ITSELF. `showModal()` makes the rest of the page
 * inert to pointer interaction and lifts the dialog into the top layer, but the page keeps
 * scrolling for the keyboard and the wheel: runtime testing measured a real Page Down moving the
 * page from 364 to 891 pixels with a confirmation open, carrying the row being deleted out of
 * view and leaving the dialog over unrelated content.
 *
 * @param root The document's root element, or undefined when the dialog is not in a document.
 */
function lockBackgroundScroll(root: HTMLElement | undefined): void {
  if (root === undefined) {
    return;
  }

  scrollLockDepth += 1;

  if (scrollLockDepth === 1) {
    root.classList.add(SCROLL_LOCK_CLASS);
  }
}

/**
 * Releases this dialog's claim on the background scroll lock.
 *
 * The class is removed only when the LAST holder releases it. The depth is floored at zero so
 * that a release without a matching lock - a dialog destroyed before it ever opened, which is
 * what happens when its host is removed in the same turn it was created - cannot drive the
 * counter negative and strand the page unscrollable.
 *
 * @param root The document's root element, or undefined when the dialog was never in a document.
 */
function releaseBackgroundScroll(root: HTMLElement | undefined): void {
  if (root === undefined || scrollLockDepth === 0) {
    return;
  }

  scrollLockDepth -= 1;

  if (scrollLockDepth === 0) {
    root.classList.remove(SCROLL_LOCK_CLASS);
  }
}

/**
 * Decides whether a matched candidate is actually in the tab order.
 *
 * Every rejection below matches the candidate selector yet cannot be tabbed to.
 * `tabindex="-1"` is the subtle one: it marks an element as programmatically
 * focusable but deliberately not tabbable, so it must never become a wrap
 * boundary. Zero client rectangles is the only reliable test for an element that
 * is not rendered at all, whatever the cause.
 *
 * @param element Candidate matched by {@link FOCUSABLE_CANDIDATE_SELECTOR}.
 * @returns `true` when the element belongs in the tab order.
 */
function isKeyboardFocusable(element: HTMLElement): boolean {
  if (element.hasAttribute('hidden') || element.closest('[inert]') !== null) {
    return false;
  }
  if (element.getAttribute('tabindex') === '-1') {
    return false;
  }
  if (element.getAttribute('contenteditable') === 'false') {
    return false;
  }
  if (element.matches(':disabled')) {
    return false;
  }
  if (element instanceof HTMLInputElement && element.type === 'hidden') {
    return false;
  }
  return element.getClientRects().length > 0;
}

function collectFocusable(container: HTMLElement): readonly HTMLElement[] {
  const focusable: HTMLElement[] = [];
  container.querySelectorAll(FOCUSABLE_CANDIDATE_SELECTOR).forEach((candidate) => {
    if (candidate instanceof HTMLElement && isKeyboardFocusable(candidate)) {
      focusable.push(candidate);
    }
  });
  return focusable;
}

/**
 * Identifies the element a `Tab` keystroke is moving away from.
 *
 * The event target is preferred because in a browser the keydown target is the
 * focused element. A synthetic event, however, may be aimed at the dialog while
 * focus genuinely rests on a button, so the focused element is consulted as a
 * fallback. Both readings must belong to the supplied set, which stops an
 * unrelated target from being mistaken for a wrap boundary.
 *
 * @param event The `Tab` keydown event being handled.
 * @param focusable The focusable set, in document order.
 * @returns The originating element, or `undefined` when it cannot be established.
 */
function resolveTabOrigin(event: KeyboardEvent, focusable: readonly HTMLElement[]): HTMLElement | undefined {
  const target = event.target;
  if (target instanceof HTMLElement && focusable.includes(target)) {
    return target;
  }
  const focused = resolveFocusedElement();
  if (focused !== undefined && focusable.includes(focused)) {
    return focused;
  }
  return undefined;
}

/**
 * Destructive-action confirmation dialog.
 *
 * Asks for consent and does nothing else: no request, no injected service, no
 * state beyond its own settlement flag. The caller owns the outcome.
 *
 * Presence in the DOM is "open". There is no `open` or `visible` input — the
 * dialog opens itself on creation and settles exactly once, so a consumer opens
 * it by mounting it and must unmount it in response to either output. Settlement
 * is permanent and the dialog never dismisses itself, so one left mounted stays
 * modal with every one of its own affordances already inert.
 *
 * There is deliberately no `open`, `visible` or `show` input. The component opens
 * itself when it is created and settles exactly once, so the consumer controls
 * visibility purely by mounting and unmounting it with built-in control flow.
 *
 * A consumer MUST unmount it in response to either output. This is an obligation,
 * not a suggestion: settlement is permanent and the dialog never dismisses itself,
 * so a component left mounted after it has emitted stays open AND stays modal - the
 * rest of the document therefore stays inert while every affordance on the dialog
 * has already gone inert too. The two requirements that produce that outcome are
 * both deliberate and neither can be relaxed, so the obligation is stated here
 * rather than worked around by widening the public API.
 *
 * @example
 * ```html
 * @if (pendingRemoval()) {
 *   <app-confirm-dialog
 *     [title]="'Confirm Delete'"
 *     [message]="'Are you sure you want to remove this role?'"
 *     [confirmLabel]="'Delete'"
 *     [danger]="true"
 *     (confirm)="onConfirmRemoval()"
 *     (cancel)="onCancelRemoval()" />
 * }
 * ```
 *
 * ## Lifecycle
 *
 * 1. On construction the element that currently holds focus is captured as the
 *    invoker. Because the consumer's control-flow block flips in response to a
 *    click, the clicked affordance still holds focus at that moment.
 * 2. After the view initialises, the inner `<dialog>` is opened with
 *    `showModal()` - never `show()`, and never a static `open` attribute - which
 *    is what promotes it to the browser's top layer and makes the rest of the
 *    document inert.
 * 3. Focus is then moved explicitly to the CANCELLING affordance, never to the
 *    destructive one, so that a stray `Enter` cannot delete anything.
 * 4. On destruction the dialog is closed if it is still open and focus returns to
 *    the captured invoker when that element is still in the document. When it is
 *    NOT - the two real cases being a confirmed deletion that removed the row the
 *    invoker belonged to, and a navigation that tore the whole screen down - focus
 *    falls back to the main region rather than being left on the document. A third
 *    case is WATCHED FOR rather than checked: an invoker that was connected at
 *    teardown and is removed once the confirmed request resolves, which is what a
 *    confirmed deletion produces and what no single check at teardown can catch.
 *
 * ## Template contract
 *
 * The contract is stated here explicitly, and `confirm-dialog.component.html`
 * satisfies it exactly. The paired template MUST:
 * - render a single `<dialog>` carrying the template reference `#dialogElement`,
 *   NOT wrapped in any control-flow block and NOT carrying an `open` attribute;
 * - place all dialog ARIA on that `<dialog>` - `aria-modal="true"`,
 *   `aria-labelledby="titleId"` and `aria-describedby="messageId"`, using the
 *   {@link ConfirmDialogComponent.titleId} and
 *   {@link ConfirmDialogComponent.messageId} values published below - and never on
 *   the host element;
 * - bind `(cancel)="onDialogCancel($event)"` and
 *   `(click)="onBackdropClick($event)"` on that same `<dialog>`. The `cancel`
 *   event object is REQUIRED, not decorative: the handler suppresses that event's
 *   default action, which is the only thing standing between a platform-originated
 *   dismissal and the element closing itself in defiance of the settlement
 *   contract below;
 * - give that `<dialog>` exactly ONE element child, a plain
 *   `<div class="confirm-dialog__panel">`, and place the title, the message and the
 *   action row inside it. This is a load-bearing structural requirement, not
 *   decoration: it is what lets the stylesheet move the visible inset off the
 *   `<dialog>` and onto a child, and any inset left on the `<dialog>` itself is a
 *   region that reports the dialog as a click target and is therefore
 *   indistinguishable from the `::backdrop`. See
 *   {@link ConfirmDialogComponent.onBackdropClick} for why the two safeguards -
 *   this wrapper and the pointer-geometry test - are independent rather than
 *   redundant;
 * - render the title as an `<h2>` bearing `titleId`, never as an `<h1>`: the page's
 *   single first-level heading belongs to the shared page-header primitive that the
 *   feature screen behind this dialog already renders, so a second one would give
 *   one document two competing outlines;
 * - render the message as a `<p>` bearing `messageId`, unconditionally, so the
 *   `aria-describedby` reference always resolves - an explicitly empty message is a
 *   legitimate value and yields an empty paragraph rather than a dangling reference;
 * - render the CANCELLING affordance FIRST in document order, as
 *   `<button type="button" #cancelButton (click)="onCancelClick()">Cancel</button>`,
 *   whose label is a template literal rather than an input;
 * - render the CONFIRMING affordance SECOND, as
 *   `<button type="button" (click)="onConfirmClick()">{{ confirmLabel }}</button>`;
 * - bind `[disabled]="settled"` on BOTH affordances, so that the single-outcome
 *   guarantee below is enforced natively - closing the pointer, `Enter` and `Space`
 *   paths at once - rather than only being absorbed by the guard;
 * - bind the destructive modifier class from {@link ConfirmDialogComponent.danger},
 *   which is the one and only place that input is read;
 * - interpolate `title`, `message` and `confirmLabel` as plain text only;
 * - emit no `<header>`, `<main>`, `<nav>` or `<footer>` landmark - landmarks belong
 *   exclusively to the application shell under `layout/`.
 *
 * Cancel-before-confirm is both the safe order and the faithful one, which is why
 * it is a requirement rather than a preference: `editroles.ascx` L179-189 renders
 * Update, then Cancel at L182-183, then Delete at L185-186, so the measured legacy
 * order already put the safe action first.
 *
 * ## Untrusted wording
 *
 * `title`, `message` and `confirmLabel` are PLAIN TEXT and are never treated as
 * markup. The legacy wording source cannot be trusted:
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` defines
 * `Advertising.Text` as a live Google AdSense `<script type="text/javascript">`
 * block with a remote `src` and a real `google_ad_client` publisher identifier -
 * a value invisible to a naive search because the tags are stored HTML-escaped -
 * and other in-scope values open with `<h1>`, `<b>` or a leading `<br>`. The
 * identifier itself is deliberately not reproduced here. Rendering any of that as
 * markup would be script injection. Interpolation escapes it instead, which is the
 * safe choice and, as it happens, the faithful one too:
 * `Website/admin/Security/AccessDenied.ascx.vb` L43 wraps its message in
 * `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))`, and the legacy confirm
 * helper's own `GetSafeJSString` (`ClientAPI.vb` L497-504) escaped only `'`, `"`
 * and `\` for JavaScript-string-literal safety - it was never HTML sanitisation.
 * Accordingly this file imports no sanitiser, produces no trusted-HTML value and
 * exposes nothing intended for a raw-HTML binding. A confirmation needing emphasis
 * or a list is re-authored by the CONSUMER as real template markup.
 *
 * ## Settlement
 *
 * `confirm` and `cancel` are mutually exclusive and each emits at most once.
 * `cancel` also covers `Escape`, a backdrop click and any dismissal the user
 * agent originates; a backdrop click can only ever cancel, because a destructive
 * action must not follow the least deliberate gesture available. Neither output
 * closes the dialog — teardown belongs to the consumer.
 *
 * That last sentence is enforced rather than merely intended, and it takes two
 * suppressions to hold, because the platform offers two independent routes to
 * closure that this component does not initiate. `Escape` is suppressed in
 * {@link onKeydown} and the element's own `cancel` is suppressed in
 * {@link onDialogCancel}. Without both, a dismissal the user agent originates
 * would close the element while the component stayed mounted, which is a state no
 * consumer can observe and none can recover from.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    // Removes the `title` attribute from the rendered host, for the reason
    // recorded on the `title` input below.
    '[attr.title]': 'null',
    // Component-scoped rather than a document listener: promotion to the top
    // layer changes painting and stacking rather than the DOM tree, so keydown
    // raised inside the `<dialog>` still bubbles here, and this listener is torn
    // down with the component. No dialog ARIA is bound on the host — that would
    // announce a second, empty dialog around the real one.
    '(keydown)': 'onKeydown($event)',
  },
})
export class ConfirmDialogComponent implements AfterViewInit, OnDestroy {
  /**
   * Dialog title, rendered as the dialog's accessible name. Plain text.
   *
   * Public because the strict input access check rejects a non-public input at
   * every consumer site.
   *
   * The member name collides with the global HTML `title` attribute, so the host
   * metadata above strips that attribute unconditionally. Never writing the value
   * is not sufficient on its own: the framework copies a static template
   * attribute onto the rendered element in addition to assigning the matching
   * input, so a call site written as `title="…"` would leave a live `title` on
   * the host — a native tooltip, and a competing accessible name for the whole
   * subtree. Consumers should still prefer the property form.
   *
   * A blank binding cannot reach the DOM: {@link coerceDialogTitle} trims the
   * value and substitutes the default when nothing survives, so the dialog always
   * has an accessible name. The transform runs only when the input is actually
   * bound; leaving it unbound takes the field initialiser instead, and both routes
   * land on the same non-blank string.
   */
  @Input({ transform: coerceDialogTitle }) public title: string = DEFAULT_TITLE;

  /**
   * The confirmation question put to the user. Plain text.
   *
   * An explicitly supplied empty string is honoured and renders no question. The
   * legacy null-string sentinel is itself the empty string, so `''` is a
   * legitimate caller-supplied value and must never be swapped for the default;
   * the default applies only when the input is not bound at all.
   *
   * Deliberately carries NO blank-coercing transform, unlike {@link title} and
   * {@link confirmLabel}. Those two are accessible names, where blank means an
   * unnamed control and there is no such thing as a caller legitimately wanting
   * one. This is descriptive body text referenced by `aria-describedby`, and a
   * description is genuinely optional — a dialog whose title already states the
   * whole question needs no second sentence. Coercing this input would overwrite
   * a deliberate `''` with wording the caller explicitly declined.
   */
  @Input() public message: string = DEFAULT_MESSAGE;

  /**
   * Label for the confirming affordance. Plain text.
   *
   * Prefer a verb naming the outcome — 'Delete', 'Remove', 'Unregister' — over a
   * generic 'OK', so the label itself carries the consequence.
   *
   * A blank binding cannot reach the DOM: {@link coerceConfirmLabel} trims the
   * value and substitutes the default when nothing survives. This matters more
   * here than for any other input, because the severity glyph beside the label is
   * `aria-hidden` and therefore contributes nothing to the name — a blank label
   * would leave the destructive button with no accessible name whatsoever.
   */
  @Input({ transform: coerceConfirmLabel }) public confirmLabel: string = DEFAULT_CONFIRM_LABEL;

  /**
   * Whether to present the confirming affordance as destructive.
   *
   * Presentation only: no code path in this class reads it, so it cannot change
   * what {@link confirm} means or when it fires. The boolean coercion makes the
   * bare attribute form valid and turns a nullish expression into `false` rather
   * than styling a destructive action from an absent value.
   */
  @Input({ transform: booleanAttribute }) public danger = false;

  /**
   * Emitted when the user confirms the destructive action.
   *
   * An intent rather than a result: nothing has been deleted when it fires. The
   * payload is `void` because the dialog knows nothing about the subject — the
   * consumer already holds the identifier and issues the request.
   */
  @Output() public readonly confirm = new EventEmitter<void>();

  /**
   * Emitted when the user declines, or when the dialog is dismissed.
   *
   * Handled by unmounting the dialog and restoring the prior view state. No
   * request should be issued.
   */
  @Output() public readonly cancel = new EventEmitter<void>();

  private readonly instanceId: string = nextConfirmDialogId();

  public readonly titleId: string = `${this.instanceId}-title`;

  public readonly messageId: string = `${this.instanceId}-message`;

  @ViewChild('dialogElement') private dialogElementRef?: ElementRef<HTMLDialogElement>;

  @ViewChild('cancelButton') private cancelButtonRef?: ElementRef<HTMLElement>;

  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * The zone, used to keep the post-teardown focus watch out of change detection.
   *
   * The watch in {@link ConfirmDialogComponent.rescueFocusIfInvokerDisappears} observes
   * document-wide mutations for a bounded window. Zone.js patches `MutationObserver`, so
   * left inside the Angular zone every batch of DOM changes anywhere in the application
   * would re-enter it and schedule a change-detection pass - for a watch that reads two
   * properties and never touches a binding. It is registered outside the zone instead. The
   * focus call it may make needs no change detection: focus is browser state, not view
   * state, and nothing in this component's view depends on it.
   */
  private readonly zone: NgZone = inject(NgZone);

  /**
   * The element that held focus when this dialog was created.
   *
   * Captured in a field initialiser because construction is the last moment the
   * invoker still holds focus: the consumer's control-flow block flips in
   * response to a click, and reading this after `showModal()` would capture a
   * button inside the dialog instead.
   */
  private readonly invoker: HTMLElement | undefined = resolveFocusedElement();

  /**
   * Whether this dialog has already produced an outcome.
   *
   * A plain boolean, deliberately not reactive state: a signal would add a dependency
   * graph where a template binding already suffices. See migration annotation 9 for why
   * the guard is mandatory rather than defensive.
   *
   * `protected` rather than `private` because the paired template binds it to the
   * `disabled` property of both affordances, which is what makes the inert state REAL
   * rather than merely guarded - the native attribute closes the pointer, Enter and Space
   * paths at once - and what makes the disabled treatment in the paired stylesheet a state
   * this component can actually reach. It is deliberately NOT public: it is an
   * implementation detail of this component's own view, never part of its API.
   *
   * On-push change detection stays correct without any manual notification, because every
   * path that sets this flag originates in a listener bound by this component's own view:
   * the two click handlers and the native `cancel` handler in the paired template, and the
   * `keydown` handler in this class's host metadata. Angular marks the view dirty for each
   * of them, so the bindings above are re-evaluated in the same change-detection turn.
   */
  protected settled = false;

  /**
   * Whether THIS instance currently holds the background scroll lock.
   *
   * Tracked per instance so that the release in `ngOnDestroy` is paired with an acquire that
   * actually happened. A dialog that never opened - because it was not connected to a document,
   * which is the case its own open path already guards - must not release a lock it never took.
   */
  private holdsScrollLock = false;

  /**
   * Opens the dialog and places focus on the cancelling affordance.
   *
   * The view children exist by the time this hook runs, which is exactly when
   * `showModal()` becomes legal. Nothing is deferred to a timer or an animation
   * frame, so the open stays observable synchronously.
   */
  public ngAfterViewInit(): void {
    const dialog = this.resolveDialogElement();
    if (dialog === undefined) {
      return;
    }
    // `showModal()` throws when the element is not in a document; the condition
    // is checked rather than caught. A non-modal `show()` fallback is
    // deliberately not offered, because a confirmation that does not block is
    // worse than none.
    if (!dialog.isConnected) {
      return;
    }
    // It also throws when the element is already open, which can only happen if
    // a template declares a static `open` attribute. Opening is skipped in that
    // case, but focus placement below still runs.
    if (!dialog.open) {
      dialog.showModal();
    }
    // Taken AFTER the open succeeds, so a dialog that could not open leaves the page as it found
    // it. `documentElement` is read through the dialog's own document rather than the global, so
    // the lock lands on the document this component is actually rendered in.
    lockBackgroundScroll(dialog.ownerDocument.documentElement);
    this.holdsScrollLock = true;
    const initialFocus = this.resolveInitialFocusTarget(dialog);
    if (initialFocus !== undefined) {
      // Focus goes to the cancelling affordance, never the destructive one, so
      // an immediate `Enter` or `Space` cannot delete anything.
      initialFocus.focus();
    }
  }

  /**
   * Closes the dialog if it is still open and returns focus somewhere deliberate.
   *
   * Restoration is guarded on the invoker still being in the document, because a
   * grid row's Delete button disappears with its row; focusing a detached
   * element would silently move focus to the body instead.
   *
   * ⚠ THE DEFECT THE FALLBACK CLOSES: the guard above was correct and INCOMPLETE.
   * It stopped a detached invoker being focused, but it then did nothing at all,
   * which leaves focus exactly where a detached invoker would have left it - on
   * `<body>`. For a keyboard reader that means the next Tab restarts at the top of
   * the document, and a screen reader announces nothing, so the outcome of the
   * confirmation is silent. Both situations that reach it are ordinary rather than
   * exotic: confirming a deletion destroys the row the Delete button lived in, and
   * navigating away destroys the whole screen the invoker belonged to.
   *
   * THE FALLBACK IS THE MAIN REGION, which is the same target the skip link uses
   * and the reason the shell gives `<main>` a negative tab index. It is resolved by
   * ANCESTRY FIRST and by document lookup second, and the second step is not
   * redundant: on a navigation teardown this component's host may already be
   * detached by the time the hook runs, and `closest` on a detached node cannot
   * reach the shell. The shell mounts exactly one main region and one outlet, so
   * the document lookup is unambiguous; where no region exists at all - a component
   * mounted in isolation - both steps yield nothing and the hook does no more than
   * it did before, which is what keeps this safe outside the application shell.
   *
   * `preventScroll` matters here. A reader who has scrolled a long grid and
   * confirmed a deletion has the main region far above the viewport, and focusing it
   * without this option would yank the page back to the top - trading a focus defect
   * for a scroll-position defect. Focus moves; the viewport does not.
   */
  public ngOnDestroy(): void {
    const dialog = this.resolveDialogElement();
    if (dialog !== undefined && dialog.open) {
      dialog.close();
    }
    if (this.holdsScrollLock) {
      releaseBackgroundScroll(dialog?.ownerDocument.documentElement);
      this.holdsScrollLock = false;
    }
    const invoker = this.invoker;
    if (invoker !== undefined && invoker.isConnected) {
      invoker.focus();
      this.rescueFocusIfInvokerDisappears(invoker);

      return;
    }
    this.resolveMainRegion()?.focus({ preventScroll: true });
  }

  /**
   * Re-homes focus if the element it was just returned to is removed immediately afterwards.
   *
   * ⚠ THE THIRD TEARDOWN BRANCH, AND IT WAS MEASURED FAILING WHILE THE OTHER TWO PASSED.
   * Cancelling the dialog returns focus to the opener, which survives; navigating away finds
   * the opener detached and falls back to the main region. CONFIRMING A DELETION does neither:
   * the opener is still connected when this hook runs, so focus is correctly returned to it,
   * and the successful deletion then destroys the row or panel that owned it - at which point
   * the browser gives focus to the document. Measured `document.activeElement` after a
   * confirmed delete: `BODY`. For a keyboard reader that means the next Tab restarts at the top
   * of the page, and a screen reader announces nothing, so the outcome of the deletion they just
   * confirmed is silent.
   *
   * ⚠ WHY THIS WATCHES FOR THE REMOVAL RATHER THAN CHECKING ONCE, AND THE MEASUREMENT THAT
   * FORCED THE CHANGE. The first version of this method scheduled a single macrotask at
   * teardown. That is anchored to the WRONG EVENT: the invoker is not destroyed by the dialog
   * closing, it is destroyed by the response to the request the confirmation triggered, which
   * arrives an unbounded network latency later. An instrumented confirmed deletion measured
   * the whole sequence - dialog torn down and focus correctly returned to the still-connected
   * opener at t+18ms, the single check resolving immediately afterwards and correctly
   * declining because focus was on a live element, and the `204` landing at t+50ms and
   * destroying the row the opener lived in. Focus then sat on `<body>` at t+100ms, t+600ms,
   * t+1000ms and t+3500ms. The check was ~30ms too early and there was no second look.
   *
   * So the trigger is the removal itself. A `MutationObserver` is the only reliable observer
   * of it: `blur` and `focusout` are NOT dispatched by Chrome when the focused element is
   * removed from the document, so no event on the invoker can be listened for. The observer
   * watches `document.body` with `subtree`, because the node actually removed is typically an
   * ancestor several levels above the invoker - a confirmed row deletion removes the whole row
   * and the inline panel inside it, not the button - and only a document-wide subtree
   * observation sees every shape of that.
   *
   * ⚠ IT ONLY ACTS WHEN FOCUS IS NOWHERE. The condition is that the document body itself holds
   * focus, which is the browser's way of saying no element does. If the consumer moved focus
   * somewhere deliberate, or the reader has already moved on, then `activeElement` is not the
   * body and this does nothing at all - so it can never take focus away from anything.
   *
   * ⚠ IT IS BOUNDED TWICE, and both bounds are load-bearing. The watch stops the first time it
   * finds the invoker gone, whether or not it moves focus, so the ordinary case costs one
   * observation of a handful of mutation batches. It also stops unconditionally after
   * {@link FOCUS_RESCUE_WINDOW_MS}, so a confirmation whose request never resolves cannot
   * leave a document-wide observer alive behind it. Neither bound is cancelled on destruction,
   * because the component is ALREADY destroyed when they are established - which is exactly
   * why they have to be self-limiting.
   *
   * @param invoker The still-connected element focus was just returned to.
   */
  private rescueFocusIfInvokerDisappears(invoker: HTMLElement): void {
    this.zone.runOutsideAngular((): void => {
      let deadline = 0;
      let observer: MutationObserver | undefined;

      const stopWatching = (): void => {
        observer?.disconnect();
        window.clearTimeout(deadline);
      };

      observer = new MutationObserver((): void => {
        // Still there: the mutation was unrelated - the dialog's own teardown, a
        // notification arriving, the consumer re-rendering - so keep watching.
        if (invoker.isConnected) {
          return;
        }
        stopWatching();
        if (document.activeElement !== document.body) {
          return;
        }
        this.resolveMainRegion()?.focus({ preventScroll: true });
      });

      observer.observe(document.body, { childList: true, subtree: true });
      deadline = window.setTimeout(stopWatching, FOCUS_RESCUE_WINDOW_MS);
    });
  }

  /**
   * Resolves the main region focus can fall back to, or `undefined` when there is none.
   *
   * @returns The main landmark element, or `undefined` outside the application shell.
   */
  private resolveMainRegion(): HTMLElement | undefined {
    const host = this.hostElement.nativeElement;

    return host.closest('main') ?? document.querySelector('main') ?? undefined;
  }

  /**
   * Handles keyboard interaction for the whole dialog.
   *
   * `Escape` cancels, `Tab` is wrapped at the focus boundaries, and every other
   * key is left entirely to the browser. Keys are compared by `key`, never by
   * the deprecated numeric code.
   *
   * @param event The keydown event, as it bubbles out of the dialog.
   */
  public onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      // Suppresses the user agent's own close request, which is what stops a
      // real `Escape` press from also firing the element's native `cancel`.
      event.preventDefault();
      this.emitCancel();
      return;
    }
    if (event.key === 'Tab') {
      this.wrapFocusAtBoundary(event);
    }
  }

  public onConfirmClick(): void {
    this.emitConfirm();
  }

  public onCancelClick(): void {
    this.emitCancel();
  }

  /**
   * Handles the inner `<dialog>`'s own `cancel` event.
   *
   * Named for its source because a method called `cancel()` would collide with
   * the output field of that name. This is the path for a dismissal the platform
   * originates, where no keydown reaches this component at all.
   *
   * The default action is suppressed first, for exactly the reason the `Escape`
   * branch of {@link onKeydown} suppresses it. The user agent's default action for
   * `cancel` is to CLOSE the element, and this class's settlement contract is that
   * neither output closes the dialog - teardown belongs to the consumer. Letting
   * the default run would close the element while leaving this component mounted,
   * so the DOM and the consumer's own control-flow block would disagree about
   * whether the dialog is open: a consumer that deliberately keeps it mounted
   * after `cancel` - to overlay a spinner while it unwinds, say - would find the
   * question already gone, and one that unmounts on `cancel` would be closing an
   * element the browser had closed a moment earlier. Suppressing the default makes
   * closure a single decision taken in a single place.
   *
   * Suppression happens BEFORE the emit-once guard is consulted, so a second
   * dismissal of an already-settled dialog is still prevented from closing it.
   *
   * @param event The element's own `cancel` event, whose default action is
   * suppressed so that closure stays the consumer's decision.
   */
  public onDialogCancel(event: Event): void {
    event.preventDefault();
    this.emitCancel();
  }

  /**
   * Cancels when a click landed on the backdrop rather than on the dialog itself.
   *
   * Two conditions must both hold, because neither is sufficient alone.
   *
   * The target check comes first and is a cheap, exact rejection of everything
   * inside the panel: a click on the title, the message or either button reports
   * that descendant, so it exits here without any geometry being read.
   *
   * The geometry check is what the target check cannot do. A modal `<dialog>`
   * paints its `::backdrop` as a pseudo-element of the dialog, and the platform
   * attributes a click there to the dialog itself — but a click anywhere on the
   * dialog's OWN box that is not over a child does exactly the same. Border and
   * any padding that survives on the dialog element therefore report an identical
   * target to the backdrop, and no amount of target comparison can separate them.
   * Only the pointer position can. Comparing the pointer against the dialog's
   * border box splits the two cases precisely: inside the box is the dialog, and
   * outside it is the backdrop, which by construction occupies the whole viewport
   * around the box.
   *
   * The comparison uses the DIALOG's rectangle, not the panel's, and the choice is
   * deliberate. The panel wrapper already removes padding from the dialog element,
   * so the panel's own edges are not a dismissal hazard — but the dialog keeps its
   * border, and a click on that border is a click on the dialog, not past it.
   * Testing against the outer box keeps the border on the non-dismissing side. The
   * wrapper and this test are independent safeguards, not two halves of one.
   *
   * @param event The click event observed on the inner `<dialog>`.
   */
  public onBackdropClick(event: MouseEvent): void {
    const dialog = this.resolveDialogElement();
    if (dialog === undefined) {
      return;
    }

    // Anything reporting a descendant is content, and content never dismisses.
    if (event.target !== dialog) {
      return;
    }

    const bounds = dialog.getBoundingClientRect();

    // A dialog that is closed, detached or display:none measures zero. There is no
    // rectangle to be outside of, so every point would read as a backdrop click
    // and a synthetic or stray event would settle a dialog the user cannot see.
    if (bounds.width === 0 || bounds.height === 0) {
      return;
    }

    const outsideHorizontally = event.clientX < bounds.left || event.clientX > bounds.right;
    const outsideVertically = event.clientY < bounds.top || event.clientY > bounds.bottom;

    if (outsideHorizontally || outsideVertically) {
      this.emitCancel();
    }
  }

  /** Emits {@link confirm} unless this dialog has already settled. */
  private emitConfirm(): void {
    if (this.settled) {
      return;
    }
    this.settled = true;
    this.confirm.emit();
  }

  /**
   * Emits {@link cancel} unless this dialog has already settled.
   *
   * The guard is required rather than defensive: in a browser a single `Escape`
   * reaches both the host keydown handler and the element's native `cancel`
   * event, so without it a real press would cancel twice while a synthetic-only
   * test observed one emission and passed. Settling here also makes the two
   * outcomes mutually exclusive, so a late click on the opposite affordance
   * cannot turn a cancellation into a deletion.
   */
  private emitCancel(): void {
    if (this.settled) {
      return;
    }
    this.settled = true;
    this.cancel.emit();
  }

  /**
   * Wraps focus when `Tab` is pressed at either end of the focusable set.
   *
   * `showModal()` already confines focus natively, but that path is unreachable
   * from a synthetic event, so the boundary wrap is handled explicitly to make
   * it observable. Only the boundaries are touched; interior tabbing stays with
   * the browser, in the user's own platform order.
   *
   * @param event The `Tab` keydown event being handled.
   */
  private wrapFocusAtBoundary(event: KeyboardEvent): void {
    const dialog = this.resolveDialogElement();
    if (dialog === undefined) {
      return;
    }
    const focusable = collectFocusable(dialog);
    const first = focusable.at(0);
    const last = focusable.at(-1);
    if (first === undefined || last === undefined) {
      // Nothing focusable is rendered, so there is no boundary to wrap at and no
      // reason to interfere. `showModal()` still confines focus natively.
      return;
    }
    const origin = resolveTabOrigin(event, focusable);
    if (origin === undefined) {
      return;
    }
    if (event.shiftKey) {
      if (origin === first) {
        event.preventDefault();
        last.focus();
      }
      return;
    }
    if (origin === last) {
      event.preventDefault();
      first.focus();
    }
  }

  /**
   * Resolves the inner `<dialog>`, preferring the template reference.
   *
   * The structural fallback exists because a renamed reference yields nothing
   * from the query, with no compile-time signal, and the dialog would then never
   * open. The lookup is bounded to this component's own host, so it cannot reach
   * another component's dialog. Both paths narrow with `instanceof`, so a
   * mismatched element is reported as absent instead of failing later.
   *
   * @returns The dialog element, or `undefined` when the template has none.
   */
  private resolveDialogElement(): HTMLDialogElement | undefined {
    const queried = this.dialogElementRef;
    if (queried !== undefined && queried.nativeElement instanceof HTMLDialogElement) {
      return queried.nativeElement;
    }
    const found = this.hostElement.nativeElement.querySelector('dialog');
    if (found instanceof HTMLDialogElement) {
      return found;
    }
    return undefined;
  }

  /**
   * Resolves the element that should receive focus when the dialog opens.
   *
   * The cancelling affordance is preferred, by template reference. Falling back
   * to the first focusable element is only safe while the cancelling affordance
   * is rendered first in document order, which the paired template must
   * guarantee.
   *
   * @param dialog The open dialog element to search.
   * @returns The element to focus, or `undefined` when nothing is focusable.
   */
  private resolveInitialFocusTarget(dialog: HTMLDialogElement): HTMLElement | undefined {
    const queried = this.cancelButtonRef;
    if (queried !== undefined && queried.nativeElement instanceof HTMLElement) {
      return queried.nativeElement;
    }
    return collectFocusable(dialog).at(0);
  }
}
