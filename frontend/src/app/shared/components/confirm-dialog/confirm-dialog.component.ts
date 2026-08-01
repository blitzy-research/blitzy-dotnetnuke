import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  ViewChild,
  booleanAttribute,
  inject,
  type AfterViewInit,
  type OnDestroy,
} from '@angular/core';

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
 *    the captured invoker when that element is still in the document.
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
 * - bind `(cancel)="onDialogCancel()"` and `(click)="onBackdropClick($event)"` on
 *   that same `<dialog>`;
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
   */
  @Input() public title: string = DEFAULT_TITLE;

  /**
   * The confirmation question put to the user. Plain text.
   *
   * An explicitly supplied empty string is honoured and renders no question. The
   * legacy null-string sentinel is itself the empty string, so `''` is a
   * legitimate caller-supplied value and must never be swapped for the default;
   * the default applies only when the input is not bound at all.
   */
  @Input() public message: string = DEFAULT_MESSAGE;

  /**
   * Label for the confirming affordance. Plain text.
   *
   * Prefer a verb naming the outcome — 'Delete', 'Remove', 'Unregister' — over a
   * generic 'OK', so the label itself carries the consequence.
   */
  @Input() public confirmLabel: string = DEFAULT_CONFIRM_LABEL;

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
    const initialFocus = this.resolveInitialFocusTarget(dialog);
    if (initialFocus !== undefined) {
      // Focus goes to the cancelling affordance, never the destructive one, so
      // an immediate `Enter` or `Space` cannot delete anything.
      initialFocus.focus();
    }
  }

  /**
   * Closes the dialog if it is still open and returns focus to the invoker.
   *
   * Restoration is guarded on the invoker still being in the document, because a
   * grid row's Delete button disappears with its row; focusing a detached
   * element would silently move focus to the body instead.
   */
  public ngOnDestroy(): void {
    const dialog = this.resolveDialogElement();
    if (dialog !== undefined && dialog.open) {
      dialog.close();
    }
    const invoker = this.invoker;
    if (invoker !== undefined && invoker.isConnected) {
      invoker.focus();
    }
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
   */
  public onDialogCancel(): void {
    this.emitCancel();
  }

  /**
   * Cancels when a click landed on the backdrop rather than inside the dialog.
   *
   * A modal `<dialog>` paints its own backdrop, and a click there reports the
   * dialog element as the target while a click on its content reports that
   * content, so comparing the two is an exact test rather than a heuristic.
   *
   * @param event The click event observed on the inner `<dialog>`.
   */
  public onBackdropClick(event: MouseEvent): void {
    const dialog = this.resolveDialogElement();
    if (dialog === undefined) {
      return;
    }
    if (event.target === dialog) {
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
