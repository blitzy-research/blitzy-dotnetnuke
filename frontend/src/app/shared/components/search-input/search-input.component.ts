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
  signal,
  computed,
  type Signal,
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
 * Matches every character that occupies no width, so a term made of them looks like an empty box.
 *
 * ⚠ WHY THESE MUST GO, AND WHY THE USER CANNOT DEAL WITH THEM THEMSELVES. Pasting from a word processor,
 * a spreadsheet or a web page routinely carries these along. A term of three zero-width spaces was
 * measured reaching the server verbatim and matching nothing, while the box appeared empty and the screen
 * announced `Filtered: user name begins with “”` - quotes touching, naming nothing. The operator is shown
 * an empty search, an empty result, and a filter claim about a value they cannot see, and no amount of
 * looking at the box will reveal the cause. There is nothing to preserve by keeping them: none is part of
 * any account name, and none can be perceived.
 *
 * U+00AD soft hyphen, U+200B-U+200D zero-width space/non-joiner/joiner, U+200E-U+200F and U+202A-U+202E
 * the bidirectional controls, U+2060 word joiner, U+FEFF zero-width no-break space (the byte-order mark,
 * which a copied file fragment carries at its head).
 */
const INVISIBLE_CHARACTERS = /[\u00AD\u200B-\u200F\u202A-\u202E\u2060\uFEFF]/g;

/** What, if anything, had to be changed about a term before it could be emitted. */
export type SearchTermAdjustment = 'none' | 'invisible-removed' | 'truncated' | 'blank-ignored';

/** A bounded term together with the reason it differs from what was typed. */
export interface BoundedSearchTerm {
  /** The term to emit. */
  readonly term: string;

  /** Why it differs from the value held by the control, or `'none'`. */
  readonly adjustment: SearchTermAdjustment;
}

/**
 * The wording shown when a term was cut to fit.
 *
 * MIGRATION: announcing this is a NET ADDITION, adopted from the pattern the role screen already uses for
 * its own length bounds - a bound that reports is a bound a person can work with, and a bound that
 * silently discards is one they cannot. The measured behaviour before this was that 300 typed characters
 * became 256 with no message anywhere, and the screen then reported no matches for a query nobody had
 * entered.
 */
export const TERM_TRUNCATED_MESSAGE =
  `Only the first ${MAX_TERM_LENGTH} characters of the search were used.`;

/** The wording shown when characters that occupy no width were removed. */
export const TERM_INVISIBLE_REMOVED_MESSAGE =
  'Invisible characters were removed from the search.';

/** The wording shown when a term of nothing but spaces was treated as no search at all. */
export const TERM_BLANK_IGNORED_MESSAGE =
  'A search of only spaces matches every record, so no filter was applied.';

/**
 * Applies the emission bounds to one term: strips control characters, then truncates to {@link
 * MAX_TERM_LENGTH}. A term that is already within both bounds is returned as the very same string, so for
 * every value a user can actually type this function is a strict no-op and the verbatim emission contract
 * on {@link SearchInputComponent.search} holds exactly.
 *
 * @param term The term as held by the control.
 * @returns The bounded term, ready to emit.
 */
function boundSearchTerm(term: string): BoundedSearchTerm {
  if (term.length === 0) {
    return { term, adjustment: 'none' };
  }

  // Control characters first, then the zero-width set. Both are removed rather than rejected: a term is
  // something a person typed or pasted, and the useful response to an unusable character is to proceed
  // without it and say so, not to refuse the search.
  const withoutControlCharacters = term.replace(CONTROL_CHARACTERS, '');
  const withoutInvisibles = withoutControlCharacters.replace(INVISIBLE_CHARACTERS, '');
  const invisiblesRemoved = withoutInvisibles !== withoutControlCharacters;

  // ⚠ A TERM OF NOTHING BUT SPACES IS NO SEARCH, AND SAYING SO IS THE WHOLE POINT. The server already
  // discards a blank filter and answers with every record, so emitting the spaces produced a listing that
  // was unfiltered while the screen asserted a filter was in force. Emitting the empty term instead makes
  // the claim and the listing agree - there is no filter, and none is claimed.
  if (withoutInvisibles.trim().length === 0) {
    return {
      term: '',
      // Which explanation applies depends on what the operator can see. If the box held invisible
      // characters, that is the fact they cannot discover for themselves and the one worth stating.
      adjustment: invisiblesRemoved ? 'invisible-removed' : 'blank-ignored',
    };
  }

  if (withoutInvisibles.length <= MAX_TERM_LENGTH) {
    return { term: withoutInvisibles, adjustment: invisiblesRemoved ? 'invisible-removed' : 'none' };
  }

  const lastRetainedUnit = withoutInvisibles.charCodeAt(MAX_TERM_LENGTH - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return {
    term: withoutInvisibles.slice(0, cutSplitsSurrogatePair ? MAX_TERM_LENGTH - 1 : MAX_TERM_LENGTH),
    // Truncation is reported in preference to invisible removal when both happened: the operator loses
    // meaningful characters to the cut, and only decoration to the strip.
    adjustment: 'truncated',
  };
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
   * Emits the search term. The payload is never case-folded, Unicode-normalised, URL-encoded or
   * wildcard-suffixed, because every one of those would change which rows match.
   *
   * ⚠ TWO NARROW EXCEPTIONS, AND BOTH EXIST BECAUSE THE ALTERNATIVE WAS A SCREEN THAT LIED. The term is
   * stripped of characters that occupy no width, and a term consisting of nothing but whitespace is
   * emitted as the empty term. Neither can change which rows match: the server discards a blank filter
   * outright, so the spaces were already producing an unfiltered listing - the only thing they changed was
   * that the SCREEN went on claiming a filter was in force over a listing that had every record in it.
   * Emitting the empty term makes the claim and the listing agree.
   *
   * Whenever the emitted term differs from the value held by the control, the control says so in its own
   * advisory rather than leaving the difference to be discovered.
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
   * The sentence stating the field's typing bound, announced when the box takes focus. ⚠ MEASURED
   * BEHAVIOUR IT ANSWERS: a 300-character term was accepted to 256 characters and the remainder was
   * dropped in silence - no counter, no message, and no way for a reader to know a term had been cut. The
   * wording matches the shared field's bound sentence, so a bound reads the same wherever it appears.
   */
  public readonly limitDescription = `At most ${String(MAX_TERM_LENGTH)} characters.`;

  /** The identifier of the region carrying {@link limitDescription}. */
  public readonly limitId = `${this.fieldId}-limit`;

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

  /** The most recent adjustment, driving the advisory this control renders. */
  private readonly _adjustment = signal<SearchTermAdjustment>('none');

  /**
   * The advisory to show beside the field, or `null` when the term was emitted exactly as held.
   *
   * Rendered in a polite live region AND painted, because the two audiences need it for different reasons:
   * a sighted operator has to be told why a box they can see does not match the listing they can see, and
   * a screen-reader user has to be told at all.
   */
  protected readonly adjustmentMessage: Signal<string | null> = computed<string | null>(() => {
    switch (this._adjustment()) {
      case 'truncated':
        return TERM_TRUNCATED_MESSAGE;
      case 'invisible-removed':
        return TERM_INVISIBLE_REMOVED_MESSAGE;
      case 'blank-ignored':
        return TERM_BLANK_IGNORED_MESSAGE;
      default:
        return null;
    }
  });

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
    // Only the term is taken, deliberately NOT the adjustment: this is a programmatic adoption by the
    // consuming screen, not something the operator typed, so there is nothing to advise them about. An
    // advisory raised here would appear on a screen the operator had merely navigated to.
    this.lastEmittedTerm = boundSearchTerm(adoptedTerm).term;
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

    const bounded = boundSearchTerm(term);

    // ⚠ THE ADJUSTMENT IS RECORDED BEFORE THE DUPLICATE GUARD, NOT AFTER. Typing past the cap emits the
    // same 256 characters on every further keystroke, so the guard correctly suppresses the query - but the
    // operator is still losing characters, and suppressing the EXPLANATION along with the query is exactly
    // the silence this reports on.
    this._adjustment.set(bounded.adjustment);

    if (!force && this.lastEmittedTerm === bounded.term) {
      return;
    }

    this.lastEmittedTerm = bounded.term;
    this.search.emit(bounded.term);
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
