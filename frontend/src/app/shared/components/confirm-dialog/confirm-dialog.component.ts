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
 * How long focus loss is watched for after the invoker is re-focused, in milliseconds. The window exists
 * because the event that strips focus is NOT the dialog closing - it is the response to the request the
 * confirmation triggered.
 */
const FOCUS_RESCUE_WINDOW_MS = 2000;

const DEFAULT_TITLE = 'Confirm Delete';

const DEFAULT_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

const DEFAULT_CONFIRM_LABEL = 'Delete';

/**
 * Normalises a string that will be rendered as an accessible name.
 *
 * @param value The caller-supplied string.
 * @param fallback The guaranteed non-blank replacement.
 * @returns The trimmed value, or the fallback when the value is blank.
 */
function coerceNonBlank(value: string, fallback: string): string {
  // The parameter is typed `string`, but a JavaScript caller or an `any`-typed binding can still deliver
  // null or undefined, and `.trim()` on either would throw a TypeError whose message names neither this
  // component nor this input.
  const normalised = (value ?? '').trim();

  return normalised.length === 0 ? fallback : normalised;
}

/**
 * @param value The caller-supplied title.
 * @returns A non-blank title, never the empty string.
 */
export function coerceDialogTitle(value: string): string {
  return coerceNonBlank(value, DEFAULT_TITLE);
}

/**
 * Guarantees the confirming affordance has an accessible name. The label IS that button's accessible
 * name: the severity glyph beside it is `aria-hidden`, precisely so it cannot act as a naming source,
 * which leaves a blank label with nothing at all to fall back on.
 *
 * @param value The caller-supplied label.
 * @returns A non-blank label, never the empty string.
 */
export function coerceConfirmLabel(value: string): string {
  return coerceNonBlank(value, DEFAULT_CONFIRM_LABEL);
}

/**
 * Elements that can plausibly receive keyboard focus. Deliberately broad, because the dialog body is
 * author-supplied content whose shape this component does not control; {@link isKeyboardFocusable}
 * narrows the matches.
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
 * Backs the element ids this component publishes. Two dialogs mounted at once must not publish the same
 * ids: duplicates break the `aria-labelledby` and `aria-describedby` references that give each dialog its
 * accessible name and description.
 */
let confirmDialogInstanceCount = 0;

function nextConfirmDialogId(): string {
  confirmDialogInstanceCount += 1;
  return `app-confirm-dialog-${confirmDialogInstanceCount}`;
}

/**
 * Reads the focused element, treating "nothing in particular" as absent.
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
 * The class that hides the root element's overflow while a modal dialog is open. Declared in
 * `styles/_reset.scss` alongside the `scrollbar-gutter` reservation that stops the lock shifting the page
 * sideways.
 */
const SCROLL_LOCK_CLASS = 'dnn-scroll-locked';

/**
 * How many dialogs currently hold the background scroll lock. ⚠ A COUNTER RATHER THAN A BOOLEAN, so that
 * the FIRST dialog to close cannot release a lock the SECOND one still needs.
 */
let scrollLockDepth = 0;

/**
 * Hides background scrolling while a modal confirmation is open. ⚠ A NATIVE MODAL DIALOG DOES NOT DO THIS
 * BY ITSELF. `showModal()` makes the rest of the page inert to pointer interaction and lifts the dialog
 * into the top layer, but the page keeps scrolling for the keyboard and the wheel: runtime testing
 * measured a real Page Down moving the page from 364 to 891 pixels with a confirmation open, carrying the
 * row being deleted out of view and leaving the dialog over unrelated content.
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
 * Releases this dialog's claim on the background scroll lock. The class is removed only when the LAST
 * holder releases it.
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
 * Decides whether a matched candidate is actually in the tab order. Every rejection below matches the
 * candidate selector yet cannot be tabbed to.
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
 * Identifies the element a `Tab` keystroke is moving away from. The event target is preferred because in
 * a browser the keydown target is the focused element.
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
 * Destructive-action confirmation dialog. Asks for consent and does nothing else: no request, no injected
 * service, no state beyond its own settlement flag.
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
    '(keydown)': 'onKeydown($event)',
  },
})
export class ConfirmDialogComponent implements AfterViewInit, OnDestroy {
  /** Dialog title, rendered as the dialog's accessible name. Plain text. */
  @Input({ transform: coerceDialogTitle }) public title: string = DEFAULT_TITLE;

  /** The confirmation question put to the user. Plain text. */
  @Input() public message: string = DEFAULT_MESSAGE;

  /** Label for the confirming affordance. Plain text. */
  @Input({ transform: coerceConfirmLabel }) public confirmLabel: string = DEFAULT_CONFIRM_LABEL;

  /** Whether to present the confirming affordance as destructive. */
  @Input({ transform: booleanAttribute }) public danger = false;

  /** Emitted when the user confirms the destructive action. */
  @Output() public readonly confirm = new EventEmitter<void>();

  /** Emitted when the user declines, or when the dialog is dismissed. */
  @Output() public readonly cancel = new EventEmitter<void>();

  private readonly instanceId: string = nextConfirmDialogId();

  public readonly titleId: string = `${this.instanceId}-title`;

  public readonly messageId: string = `${this.instanceId}-message`;

  @ViewChild('dialogElement') private dialogElementRef?: ElementRef<HTMLDialogElement>;

  @ViewChild('cancelButton') private cancelButtonRef?: ElementRef<HTMLElement>;

  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * The zone, used to keep the post-teardown focus watch out of change detection. The watch in {@link
   * ConfirmDialogComponent.rescueFocusIfInvokerDisappears} observes document-wide mutations for a bounded
   * window.
   */
  private readonly zone: NgZone = inject(NgZone);

  /**
   * The element that held focus when this dialog was created. Captured in a field initialiser because
   * construction is the last moment the invoker still holds focus: the consumer's control-flow block
   * flips in response to a click, and reading this after `showModal()` would capture a button inside the
   * dialog instead.
   */
  private readonly invoker: HTMLElement | undefined = resolveFocusedElement();

  /** Whether this dialog has already produced an outcome. */
  protected settled = false;

  /**
   * Whether THIS instance currently holds the background scroll lock. Tracked per instance so that the
   * release in `ngOnDestroy` is paired with an acquire that actually happened.
   */
  private holdsScrollLock = false;

  /**
   * Opens the dialog and places focus on the cancelling affordance. The view children exist by the time
   * this hook runs, which is exactly when `showModal()` becomes legal.
   */
  public ngAfterViewInit(): void {
    const dialog = this.resolveDialogElement();
    if (dialog === undefined) {
      return;
    }
    if (!dialog.isConnected) {
      return;
    }
    // It also throws when the element is already open, which can only happen if a template declares a
    // static `open` attribute. Opening is skipped in that case, but focus placement below still runs.
    if (!dialog.open) {
      dialog.showModal();
    }
    // Taken AFTER the open succeeds, so a dialog that could not open leaves the page as it found it.
    // `documentElement` is read through the dialog's own document rather than the global, so the lock lands
    // on the document this component is actually rendered in.
    lockBackgroundScroll(dialog.ownerDocument.documentElement);
    this.holdsScrollLock = true;
    const initialFocus = this.resolveInitialFocusTarget(dialog);
    if (initialFocus !== undefined) {
      // Focus goes to the cancelling affordance, never the destructive one, so
      // an immediate `Enter` or `Space` cannot delete anything.
      initialFocus.focus();
    }
  }

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
   * Re-homes focus if the element it was just returned to is removed immediately afterwards. ⚠ THE THIRD
   * TEARDOWN BRANCH, AND IT WAS MEASURED FAILING WHILE THE OTHER TWO PASSED. Cancelling the dialog
   * returns focus to the opener, which survives; navigating away finds the opener detached and falls back
   * to the main region.
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
   * Handles keyboard interaction for the whole dialog. `Escape` cancels, `Tab` is wrapped at the focus
   * boundaries, and every other key is left entirely to the browser.
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
   * @param event The element's own `cancel` event, whose default action is suppressed so that closure
   * stays the consumer's decision.
   */
  public onDialogCancel(event: Event): void {
    event.preventDefault();
    this.emitCancel();
  }

  /**
   * Cancels when a click landed on the backdrop rather than on the dialog itself. Two conditions must
   * both hold, because neither is sufficient alone.
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
   * Emits {@link cancel} unless this dialog has already settled. The guard is required rather than
   * defensive: in a browser a single `Escape` reaches both the host keydown handler and the element's
   * native `cancel` event, so without it a real press would cancel twice while a synthetic-only test
   * observed one emission and passed.
   */
  private emitCancel(): void {
    if (this.settled) {
      return;
    }
    this.settled = true;
    this.cancel.emit();
  }

  /**
   * Wraps focus when `Tab` is pressed at either end of the focusable set. `showModal()` already confines
   * focus natively, but that path is unreachable from a synthetic event, so the boundary wrap is handled
   * explicitly to make it observable.
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

  /** @returns The dialog element, or `undefined` when the template has none. */
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
   * Resolves the element that should receive focus when the dialog opens. The cancelling affordance is
   * preferred, by template reference.
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
