/**
 * Turns a run of sibling controls into ONE tab stop that arrow keys move within.
 *
 * ⚠ THE DEFECT THIS CLOSES, MEASURED RATHER THAN ASSUMED. Both alphabet filter strips in this application
 * render twenty-seven native buttons, every one of them individually in the sequential tab order, and neither
 * responded to an arrow key: a real ArrowRight press with the third letter focused left
 * `document.activeElement` on that same third letter, on both screens. A full tab-order walk of the account
 * listing put those twenty-seven at positions 7 to 33 of 82 stops, so a third of the screen's entire keyboard
 * traversal was one filter control, and the only skip affordance on the page bypasses the header and sidebar
 * rather than any of it.
 *
 * The pattern is the one the ARIA authoring practices prescribe for a toolbar: exactly one item carries
 * `tabindex="0"` and the rest carry `tabindex="-1"`, Tab enters and leaves the whole strip in one press, and
 * the arrow keys move between items once inside. That takes twenty-seven stops to one.
 *
 * ⚠ IT IS A DIRECTIVE RATHER THAN LOGIC IN EACH LISTING, and that is deliberate: the two strips are the same
 * affordance one screen apart, and they had already drifted - one named its entries for assistive technology
 * and the other left them as a single spoken character. A shared mechanism cannot drift.
 *
 * The host must also declare `role="toolbar"`, which is what tells assistive technology that arrow keys apply
 * here at all; a roving tabindex inside a plain group leaves a reader no way to know the strip is navigable.
 * The role is left to the template rather than forced from here so the element's semantics stay readable where
 * the element is written.
 */

import {
  AfterViewInit,
  DestroyRef,
  Directive,
  ElementRef,
  HostListener,
  Input,
  inject,
} from '@angular/core';

/** The item selector used when the host names none. */
const DEFAULT_ITEM_SELECTOR = 'button';

/**
 * Resolves the declared item selector, treating a blank one as absent.
 *
 * ⚠ THIS EXISTS BECAUSE A BARE ATTRIBUTE BINDS THE EMPTY STRING, NOT UNDEFINED, and a property
 * initialiser therefore never gets the chance to supply the default. Both alphabet strips are written
 * `role="toolbar" appRovingFocus` with no value, which Angular resolves to `''`, and `querySelectorAll('')`
 * throws a SyntaxError rather than returning nothing - so the directive would have crashed on every listing
 * that carries a letter strip. A specification caught it before a browser did; the narrowing is kept here in
 * a transform rather than in `items()` so the host's declaration is normalised once, at the boundary.
 *
 * @param declared The selector as written on the host, which may be blank.
 * @returns The selector to query with.
 */
function toItemSelector(declared: string | null | undefined): string {
  const trimmed = declared?.trim() ?? '';

  return trimmed.length === 0 ? DEFAULT_ITEM_SELECTOR : trimmed;
}

/** Keys that move focus forward through the strip. */
const FORWARD_KEYS: readonly string[] = ['ArrowRight', 'ArrowDown'];

/** Keys that move focus backward through the strip. */
const BACKWARD_KEYS: readonly string[] = ['ArrowLeft', 'ArrowUp'];

/** The key that jumps to the first item. */
const FIRST_KEY = 'Home';

/** The key that jumps to the last item. */
const LAST_KEY = 'End';

/** The `tabindex` of the single item Tab reaches. */
const REACHABLE = 0;

/** The `tabindex` of every other item: focusable by script, absent from the tab order. */
const PASSED_OVER = -1;

@Directive({
  selector: '[appRovingFocus]',
  standalone: true,
})
export class RovingFocusDirective implements AfterViewInit {
  /**
   * Selector matching the items to rove between, relative to the host. Defaults to every descendant button,
   * which is what both alphabet strips render, and a blank declaration resolves to that same default - see
   * {@link toItemSelector} for why that narrowing is load-bearing rather than defensive.
   */
  @Input({ transform: toItemSelector }) public appRovingFocus: string = DEFAULT_ITEM_SELECTOR;

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly destroyRef = inject(DestroyRef);

  /**
   * Whether the host has been torn down. Read before any DOM write, because reading elements from a detached
   * host is a silent no-op rather than an error, and a silent no-op is what leaves a strip with no reachable
   * entry at all.
   */
  private torndown = false;

  /**
   * Publishes the initial tab stop.
   *
   * ⚠ IT RUNS AFTER THE VIEW SO THE PRESSED STATE IS ALREADY BOUND, which is what lets a cold load with a
   * filter already applied put the tab stop on the applied entry rather than on the letter A.
   */
  public ngAfterViewInit(): void {
    this.publishTabStops(this.preferredItem());

    this.destroyRef.onDestroy(() => {
      this.torndown = true;
    });
  }

  /**
   * Adopts whichever item received focus as the strip's single tab stop.
   *
   * This is what makes a POINTER press and a keyboard press agree: clicking an entry focuses it, so leaving
   * and re-entering the strip afterwards returns to the entry the person last used rather than to wherever
   * the tab stop happened to start.
   *
   * @param event The focus event observed on the host.
   */
  @HostListener('focusin', ['$event'])
  public onFocusIn(event: FocusEvent): void {
    const items = this.items();
    const target = event.target;

    if (!(target instanceof HTMLElement)) {
      return;
    }

    const focused = items.find((item) => item === target || item.contains(target));

    if (focused === undefined) {
      return;
    }

    this.publishTabStops(focused);
  }

  /**
   * Moves focus within the strip for an arrow, Home or End press.
   *
   * Wrapping is deliberate at both ends: the strip is a closed set of filter entries rather than a sequence
   * with a meaningful start and finish, so stopping at Z would make the clearing entry beyond it harder to
   * reach from the left than from the right. The authoring practices leave wrapping optional for a toolbar,
   * so this is a choice rather than a requirement, and it is stated here as one.
   *
   * @param event The keydown event observed on the host.
   */
  @HostListener('keydown', ['$event'])
  public onKeydown(event: KeyboardEvent): void {
    const items = this.items();

    if (items.length === 0) {
      return;
    }

    // ⚠ A MODIFIED PRESS IS LEFT ALONE. Arrow keys with a modifier belong to the browser and the platform -
    // Home and End with Control scroll the document, and taking that away would cost more than it gives.
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
      return;
    }

    const current = this.currentIndex(items, event.target);

    if (current === -1) {
      return;
    }

    const target = this.targetIndex(event.key, current, items.length);

    if (target === null) {
      return;
    }

    const destination = items[target];

    if (destination === undefined) {
      return;
    }

    // The default is suppressed only once a move is certain, so a key this directive does not handle keeps
    // its native behaviour - which is what leaves the page scrollable from inside the strip.
    event.preventDefault();

    this.publishTabStops(destination);
    destination.focus();
  }

  /**
   * Resolves the item a key press should move to.
   *
   * @param key The key that was pressed.
   * @param current The index of the item holding focus.
   * @param count How many items the strip holds.
   * @returns The destination index, or null when the key is not one this directive handles.
   */
  private targetIndex(key: string, current: number, count: number): number | null {
    if (FORWARD_KEYS.includes(key)) {
      return (current + 1) % count;
    }

    if (BACKWARD_KEYS.includes(key)) {
      return (current - 1 + count) % count;
    }

    if (key === FIRST_KEY) {
      return 0;
    }

    if (key === LAST_KEY) {
      return count - 1;
    }

    return null;
  }

  /**
   * The index of the item holding focus, or minus one when the event came from somewhere else inside the
   * host - a search box sharing the container, for instance, whose arrow keys move a text caret and must not
   * be taken over.
   *
   * @param items The strip's items.
   * @param target The event's target.
   * @returns The index, or minus one.
   */
  private currentIndex(items: readonly HTMLElement[], target: EventTarget | null): number {
    if (!(target instanceof HTMLElement)) {
      return -1;
    }

    return items.findIndex((item) => item === target || item.contains(target));
  }

  /**
   * The item that should hold the tab stop before anyone has focused one: the pressed entry when there is
   * one, and otherwise the first.
   *
   * @returns The item, or undefined when the strip is empty.
   */
  private preferredItem(): HTMLElement | undefined {
    const items = this.items();

    return items.find((item) => item.getAttribute('aria-pressed') === 'true') ?? items[0];
  }

  /**
   * Gives exactly one item the tab stop and takes it from every other.
   *
   * ⚠ EXACTLY ONE, AND THE FALLBACK IS WHY THIS IS NOT A LOOP OVER A FLAG. If the named item were ever
   * absent from the strip, every entry would be left at minus one and the whole filter would drop out of the
   * keyboard entirely - a strictly worse outcome than the twenty-seven stops this replaces.
   *
   * @param active The item to make reachable.
   */
  private publishTabStops(active: HTMLElement | undefined): void {
    if (this.torndown) {
      return;
    }

    const items = this.items();

    if (items.length === 0) {
      return;
    }

    const reachable: HTMLElement =
      active !== undefined && items.includes(active) ? active : items[0];

    items.forEach((item) => {
      item.tabIndex = item === reachable ? REACHABLE : PASSED_OVER;
    });
  }

  /** The strip's items, in document order. */
  private items(): readonly HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll(this.appRovingFocus)).filter(
      (node): node is HTMLElement => node instanceof HTMLElement,
    );
  }
}
