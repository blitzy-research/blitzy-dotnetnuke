import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  inject,
  type OnInit,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { EMPTY, Subject, merge, switchMap, timer } from 'rxjs';
import { map } from 'rxjs/operators';

const DEFAULT_DEBOUNCE_MS = 300;

/** Default placeholder wording. */
const DEFAULT_PLACEHOLDER = 'Search';

/**
 * The greatest number of UTF-16 code units this control will ever emit as a term. MIGRATION: this bound
 * is a NET ADDITION. The legacy text box declared no `MaxLength` at all, so an arbitrarily long value
 * could be posted and concatenated straight into the query predicate.
 */
const MAX_TERM_LENGTH = 256;

/**
 * Matches every C0 and C1 control character. `\u0000`-`\u001F` is the C0 range (including tab, and the
 * carriage return and line feed that a single-line control should never carry), and `\u007F`-`\u009F`
 * covers delete plus the C1 range.
 */
const CONTROL_CHARACTERS = /[\u0000-\u001F\u007F-\u009F]/g;

/**
 * Applies the emission bounds to one term: strips control characters, then truncates to {@link
 * MAX_TERM_LENGTH}. A term that is already within both bounds is returned as the very same string, so for
 * every value a user can actually type this function is a strict no-op and the verbatim emission contract
 * on {@link SearchInputComponent.search} holds exactly.
 *
 * @param term The term as held by the control.
 * @returns The bounded term, ready to emit.
 */
function boundSearchTerm(term: string): string {
  const withoutControlCharacters =
    term.length === 0 ? term : term.replace(CONTROL_CHARACTERS, '');

  if (withoutControlCharacters.length <= MAX_TERM_LENGTH) {
    return withoutControlCharacters;
  }

  const lastRetainedUnit = withoutControlCharacters.charCodeAt(MAX_TERM_LENGTH - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return withoutControlCharacters.slice(
    0,
    cutSplitsSurrogatePair ? MAX_TERM_LENGTH - 1 : MAX_TERM_LENGTH,
  );
}

/**
 * Monotonic instance counter backing {@link SearchInputComponent.fieldId}. A module-scoped counter keeps
 * ids unique across every instance on a page.
 */
let searchInputInstanceCount = 0;

function nextSearchInputId(): string {
  searchInputInstanceCount += 1;
  return `app-search-input-${searchInputInstanceCount}`;
}

/**
 * Shared free-text filter control for the administration screens. It captures a term and emits it, and
 * does nothing else: no querying, no domain state and no knowledge of the resource being filtered.
 */
type SearchInputStreamEvent =
  | { readonly kind: 'typed'; readonly term: string }
  | { readonly kind: 'cancelled' };

@Component({
  selector: 'app-search-input',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './search-input.component.html',
  styleUrl: './search-input.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SearchInputComponent implements OnInit {
  /** Placeholder text rendered inside the control. */
  @Input() public placeholder: string = DEFAULT_PLACEHOLDER;

  /**
   * Debounce delay in milliseconds applied to typing before the term is emitted. `0` is a legitimate,
   * supported value and is honoured as written; it is never treated as "unset".
   */
  @Input() public debounceMs: number = DEFAULT_DEBOUNCE_MS;

  /**
   * Declares that the consumer has already given this control a visible, associated label, so this
   * component must not render one of its own. Default `false`: standing alone, the component labels
   * itself, and that label is mandatory rather than optional because a placeholder is a hint and never an
   * accessible name.
   */
  @Input() public labelledExternally: boolean = false;

  /**
   * Emits the search term, verbatim. The payload is the raw term exactly as typed - never trimmed,
   * case-folded, Unicode-normalised, URL-encoded or wildcard-suffixed, because every one of those would
   * change which rows match.
   */
  @Output() public readonly search = new EventEmitter<string>();

  public readonly term = new FormControl<string>('', { nonNullable: true });

  public readonly fieldId = nextSearchInputId();

  /**
   * The value of {@link MAX_TERM_LENGTH}, exposed so the template can put the same bound on the field
   * itself. This is a template-contract member alongside {@link term}, {@link fieldId} and {@link
   * isTermEmpty} — not a widening of this component's fixed input surface, which remains exactly
   * `placeholder` and `debounceMs`.
   */
  public readonly maxTermLength = MAX_TERM_LENGTH;

  /**
   * `DestroyRef` for the current instance, captured in a field initialiser because that runs inside an
   * injection context. It is handed to `takeUntilDestroyed` explicitly so the debounce subscription can
   * be built later, in `ngOnInit`, where the bound input values are available.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * This instance's own host element, injected for exactly one purpose: neutralising the native `search`
   * DOM event described on {@link suppressNativeSearchEvent}. Nothing else here reaches for the DOM.
   */
  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * Needed because this component is `OnPush` and its disabled state can be changed from OUTSIDE its own
   * binding graph.
   */
  private readonly changeDetectorRef: ChangeDetectorRef = inject(ChangeDetectorRef);

  /**
   * The most recently emitted term, or `null` when nothing has been emitted yet. `null` is used as the
   * "never emitted" marker precisely so that a *first* emission of `''` is not mistaken for a duplicate:
   * comparing `null` against any string is false, so the initial empty term is emitted correctly.
   */
  private lastEmittedTerm: string | null = null;

  private readonly pendingCancelled = new Subject<void>();

  public get isTermEmpty(): boolean {
    return this.term.value.length === 0;
  }

  public get isDisabled(): boolean {
    return this.term.disabled;
  }

  /** Wires the debounced typing path. */
  public ngOnInit(): void {
    const typed = this.term.valueChanges.pipe(
      map((value: string): SearchInputStreamEvent => ({ kind: 'typed', term: value })),
    );
    const cancelled = this.pendingCancelled.pipe(
      map((): SearchInputStreamEvent => ({ kind: 'cancelled' })),
    );

    merge(typed, cancelled)
      .pipe(
        switchMap((event: SearchInputStreamEvent) =>
          event.kind === 'cancelled'
            ? EMPTY
            : timer(this.resolveDebounceMs()).pipe(map((): string => event.term)),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((value: string): void => {
        this.emitTerm(value);
      });

    this.term.statusChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((): void => {
        this.changeDetectorRef.markForCheck();
      });

    this.suppressNativeSearchEvent();
  }

  private suppressNativeSearchEvent(): void {
    const host: HTMLElement = this.hostElement.nativeElement;
    const swallowNativeSearch = (event: Event): void => {
      event.stopImmediatePropagation();
    };

    host.addEventListener('search', swallowNativeSearch, { capture: true });
    this.destroyRef.onDestroy((): void => {
      host.removeEventListener('search', swallowNativeSearch, { capture: true });
    });
  }

  /**
   * Emits the current term immediately, bypassing any debounce still in flight, so a keyboard user is
   * never made to wait out the delay they just tried to skip. Bound to `Enter` on the control and to the
   * submit button's click, so a keyboard user is never made to wait out the debounce they just tried to
   * skip.
   *
   * @param event The originating keyboard event, when invoked from a key binding.
   */
  public submit(event?: Event): void {
    event?.preventDefault();

    // ⚠ FORCED, AND WITHOUT THIS THE BUTTON IS DEAD IN EVERY SCENARIO A USER WOULD REACH IT. The duplicate
    // guard in `emitTerm` exists for the debounced stream, and applying it to this path too made the two
    // mechanisms cancel each other out: the box already searches on a debounce, so by the time a hand has
    // travelled from the keyboard to the button the term has ALREADY been emitted and `lastEmittedTerm`
    // equals it - so the click emitted nothing, silently.
    this.emitTerm(this.term.value, true);
  }

  /**
   * Abandons any pending debounced emission, and optionally adopts a term without emitting it. ⚠ FOR A
   * CONSUMER THAT OFFERS A SECOND AFFORDANCE OVER THE SAME RESULT SET, AND IT CLOSES AN ORDERING DEFECT
   * THE CONSUMER COULD NOT REACH. The pending emission lives in this component's own stream, so a
   * consumer with an alphabet strip beside this box could be overtaken by its own user: typing "bl"
   * starts a delay here, pressing "C" a moment later dispatches a query for C, and the delay then elapses
   * and emits "bl" — the newer intent silently replaced by the older one, with the strip showing C over a
   * listing of B. Calling this at the moment the other affordance acts is what makes the newer intent
   * win. ⚠ THE ADOPTED TERM IS NOT EMITTED, AND THAT IS THE WHOLE POINT OF THE ARGUMENT. A consumer that
   * has just dispatched its own query wants the box to SHOW what is being filtered on without asking for
   * it a second time.
   *
   * @param adoptedTerm The term to display, or omitted to leave the box exactly as it is.
   */
  public cancelPendingSearch(adoptedTerm?: string): void {
    this.pendingCancelled.next();

    if (adoptedTerm === undefined) {
      return;
    }

    // `emitEvent: false` keeps this out of `valueChanges`, so writing the box neither starts a new
    // delay nor emits. `emitModelToViewChange` is left at its default so the rendered field updates.
    this.term.setValue(adoptedTerm, { emitEvent: false });
    this.lastEmittedTerm = boundSearchTerm(adoptedTerm);
    this.changeDetectorRef.markForCheck();
  }

  /**
   * The single emission funnel. Both the debounced path and the immediate submit path go through here,
   * which is what makes duplicate suppression uniform across them.
   *
   * @param term The term to emit, as held by the control.
   * @param force When true, the duplicate guard is bypassed but still updated.
   */
  private emitTerm(term: string, force = false): void {
    // A disabled control must not produce queries, and this is the only place that guarantee can be made
    // exhaustively. Blocking the button alone would not be enough, because there are three distinct routes
    // into this funnel and only one of them involves the button:
    if (this.term.disabled) {
      return;
    }

    const boundedTerm = boundSearchTerm(term);

    if (!force && this.lastEmittedTerm === boundedTerm) {
      return;
    }

    this.lastEmittedTerm = boundedTerm;
    this.search.emit(boundedTerm);
  }

  /**
   * Resolves the effective debounce delay: non-finite values fall back to the default, and finite values
   * are clamped to be non-negative. `0` passes through untouched because it is a legitimate setting —
   * "emit on every keystroke".
   */
  private resolveDebounceMs(): number {
    if (!Number.isFinite(this.debounceMs)) {
      return DEFAULT_DEBOUNCE_MS;
    }

    return Math.max(0, this.debounceMs);
  }
}
