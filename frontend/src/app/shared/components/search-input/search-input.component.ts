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
import { debounce, timer } from 'rxjs';

/**
 * Default debounce delay, in milliseconds.
 *
 * MIGRATION: debouncing changes *when* the query fires. The legacy screen queried
 * only when the submit control was clicked, so typing now triggers a request that
 * previously required an explicit action.
 */
const DEFAULT_DEBOUNCE_MS = 300;

/**
 * Default placeholder wording.
 *
 * Deliberately neutral, because this control is shared and the match semantics belong to
 * whichever endpoint the emitted term reaches. Those semantics differ: the general-purpose
 * `query` filter is a SUBSTRING match server-side, while the named username, email and
 * profile-value filters are PREFIX matches. A placeholder promising either one specifically
 * would be wrong for the other, so it promises neither.
 */
const DEFAULT_PLACEHOLDER = 'Search';

/**
 * The greatest number of UTF-16 code units this control will ever emit as a term.
 *
 * MIGRATION: this bound is a NET ADDITION. The legacy text box declared no
 * `MaxLength` at all (`Website/admin/Users/users.ascx` L8), so an arbitrarily long value
 * could be posted and concatenated straight into the query predicate.
 *
 * The figure is taken from the widest column any search predicate compares against: the
 * terminal `Email` column, `nvarchar(256)`. It is NOT the width of every searched column —
 * `Username` is `nvarchar(100)` and `PortalName` is `nvarchar(128)` — so 256 is the
 * ceiling of that set rather than a shared width.
 *
 * IT IS A POLICY CEILING, AND IT IS NOT BEHAVIOUR-PRESERVING. Do not describe it as the
 * point beyond which a longer term carries no additional meaning: that argument holds only
 * for a prefix predicate, and the general-purpose filter is a SUBSTRING predicate, where a
 * term longer than the column can still be meaningful and truncating it can produce a match
 * the untruncated term would not have produced. The bound exists to cap the size of an
 * unauthenticated request, and its cost is accepted rather than argued away.
 *
 * @see boundSearchTerm - applies this bound.
 */
const MAX_TERM_LENGTH = 256;

/**
 * Matches every C0 and C1 control character.
 *
 * `\u0000`-`\u001F` is the C0 range (including tab, and the carriage return and line feed
 * that a single-line control should never carry), and `\u007F`-`\u009F` covers delete plus
 * the C1 range. None of them is typeable as part of a meaningful search term. No claim is
 * made about the stored data: no schema constraint forbids a control character in any
 * searched column, so stripping them here is an input-hygiene rule for this control and not
 * a statement about what the columns contain.
 *
 * Declared at module scope rather than inside the function so the pattern is compiled once.
 * It carries no `g`-flag state hazard because it is used only with
 * `String.prototype.replace`, which resets `lastIndex` on each call.
 */
const CONTROL_CHARACTERS = /[\u0000-\u001F\u007F-\u009F]/g;

/**
 * Applies the emission bounds to one term: strips control characters, then truncates to
 * {@link MAX_TERM_LENGTH}.
 *
 * A term that is already within both bounds is returned as the very same string, so for
 * every value a user can actually type this function is a strict no-op and the verbatim
 * emission contract on {@link SearchInputComponent.search} holds exactly. Both bounds
 * exist because the control's value can also be set programmatically, through
 * `term.setValue(...)`, which is subject to neither the field's own `maxlength` attribute
 * nor the browser's value-sanitisation of a single-line control.
 *
 * Stripping happens before truncating, so the retained leading portion is 256 units of
 * *meaningful* text rather than 256 units that a run of control characters could have
 * consumed.
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

  // `String.prototype.length` counts UTF-16 code units, so a cut at exactly the bound can
  // fall between the two halves of a surrogate pair and leave a lone high surrogate.
  // Stepping back one unit in that case matters more here than it would for display text:
  // this term is destined for a query string, and `encodeURIComponent` throws a `URIError`
  // on a lone surrogate, which would turn a merely over-long term into a thrown error
  // inside the consuming feature's request composition.
  const lastRetainedUnit = withoutControlCharacters.charCodeAt(MAX_TERM_LENGTH - 1);
  const cutSplitsSurrogatePair = lastRetainedUnit >= 0xd800 && lastRetainedUnit <= 0xdbff;

  return withoutControlCharacters.slice(
    0,
    cutSplitsSurrogatePair ? MAX_TERM_LENGTH - 1 : MAX_TERM_LENGTH,
  );
}

/**
 * Monotonic instance counter backing {@link SearchInputComponent.fieldId}.
 *
 * A module-scoped counter keeps ids unique across every instance on a page. A
 * literal id would collide the moment a screen rendered two search inputs,
 * breaking the label association for both.
 */
let searchInputInstanceCount = 0;

function nextSearchInputId(): string {
  searchInputInstanceCount += 1;
  return `app-search-input-${searchInputInstanceCount}`;
}

/**
 * Shared free-text filter control for the administration screens.
 *
 * It captures a term and emits it, and does nothing else: no querying, no domain
 * state and no knowledge of the resource being filtered. The consuming feature
 * owns the request, the page index and the choice of field to search - as it does
 * the field-chooser and alphabet-strip affordances, which are screen-specific and
 * are not part of this component.
 *
 * The paired template binds `term` with `[formControl]`, `fieldId` to both the
 * control's `id` and its label's `for`, and calls `submit()` from Enter and from
 * the submit control's click. Those members are per-instance internals rather than
 * part of the public input/output surface, and `isTermEmpty` is presentational
 * only - emission must never be gated on it.
 *
 * The control must stay an `<input type="search">`: that type supplies the
 * implicit `searchbox` role, so no explicit `role` may be added, and the native
 * `search` DOM event it fires is neutralised here rather than at the call site.
 * See {@link SearchInputComponent.suppressNativeSearchEvent}.
 */
@Component({
  selector: 'app-search-input',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './search-input.component.html',
  styleUrl: './search-input.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SearchInputComponent implements OnInit {
  /**
   * Placeholder text rendered inside the control.
   *
   * Public by design: strict input access modifiers are enabled, so a non-public
   * input would be a compile error at every consumer.
   *
   * Treated as plain text. It is rendered through an ordinary property binding,
   * which escapes its content, so no caller-supplied string is ever trusted as
   * markup here. An explicitly supplied empty string is honoured exactly, giving a
   * control with no placeholder, and is never quietly swapped for the default.
   */
  @Input() public placeholder: string = DEFAULT_PLACEHOLDER;

  /**
   * Debounce delay in milliseconds applied to typing before the term is emitted.
   *
   * `0` is a legitimate, supported value and is honoured as written; it is never
   * treated as "unset". It still emits **asynchronously**, because a zero-duration
   * timer is scheduled rather than run inline, so a test must drive the clock
   * instead of asserting synchronously after a value change.
   *
   * The delay is read at each keystroke, so rebinding it mid-life takes effect
   * immediately rather than being frozen at initialisation.
   */
  @Input() public debounceMs: number = DEFAULT_DEBOUNCE_MS;

  /**
   * Emits the search term, verbatim.
   *
   * The payload is the raw term exactly as typed - never trimmed, case-folded,
   * Unicode-normalised, URL-encoded or wildcard-suffixed, because every one of
   * those would change which rows match. This control states NOTHING about the
   * predicate: the endpoint the term reaches decides whether it is matched as a
   * substring or as a prefix, composing any wildcard belongs to the repository that
   * owns that predicate, and query-string composition belongs to the shared HTTP
   * parameter utility.
   *
   * Two bounds are applied, and they are the only respects in which the payload can differ
   * from the control's value: control characters are removed, and the term is truncated to
   * {@link SearchInputComponent.maxTermLength} code units. Neither is reachable by ordinary
   * typing. The length bound is also on the field itself, so it cannot be typed or pasted
   * past; and the value-sanitisation algorithm for a single-line control already discards
   * carriage returns and line feeds, so those never arrive. A tab can still arrive by paste,
   * and `term.setValue(...)` can set anything at all, which is why both bounds are enforced
   * here rather than left to the field. See {@link boundSearchTerm} for why truncating at
   * that length cannot change which rows match.
   *
   * An emission of `''` is meaningful and means *omit the query parameter*, yielding the
   * unfiltered page. It does not mean "search for the empty string".
   *
   * Consecutive duplicate terms are suppressed, so a subscriber never sees the
   * same term twice in a row regardless of which input path produced it.
   *
   * Because a new term invalidates the current position in the result set, a
   * consumer must reset its page index to `0` when it receives one.
   */
  @Output() public readonly search = new EventEmitter<string>();

  public readonly term = new FormControl<string>('', { nonNullable: true });

  public readonly fieldId = nextSearchInputId();

  /**
   * The value of {@link MAX_TERM_LENGTH}, exposed so the template can put the same bound on
   * the field itself.
   *
   * This is a template-contract member alongside {@link term}, {@link fieldId} and
   * {@link isTermEmpty} — not a widening of this component's fixed input surface, which
   * remains exactly `placeholder` and `debounceMs`. A caller cannot change the bound, and
   * is not meant to: it is derived from the schema the search predicate runs against, so it
   * is a property of the data rather than a presentational preference.
   *
   * Binding it in the template is defence in depth rather than the enforcement point. The
   * field's own bound stops a user typing or pasting past it, giving immediate feedback in
   * the control instead of a silent truncation later; {@link boundSearchTerm} is what
   * actually guarantees the emitted term is bounded, because a programmatic
   * `term.setValue(...)` ignores the attribute entirely.
   */
  public readonly maxTermLength = MAX_TERM_LENGTH;

  /**
   * `DestroyRef` for the current instance, captured in a field initialiser because that
   * runs inside an injection context. It is handed to `takeUntilDestroyed` explicitly so
   * the debounce subscription can be built later, in `ngOnInit`, where the bound input
   * values are available.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * This instance's own host element, injected for exactly one purpose:
   * neutralising the native `search` DOM event described on
   * {@link suppressNativeSearchEvent}. Nothing else here reaches for the DOM.
   */
  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * Needed because this component is `OnPush` and its disabled state can be changed from
   * OUTSIDE its own binding graph.
   *
   * A consumer disables this control by calling `disable()` on the exposed {@link term}.
   * That call originates in consumer code, not in a template binding on this component, so
   * it marks nothing dirty here — and under `OnPush` the `[disabled]` binding on the submit
   * button would keep its stale value until some unrelated event happened to trigger a
   * check. The status subscription in {@link ngOnInit} uses this to mark the view
   * explicitly, which is what makes the disabled state observable in the DOM rather than
   * merely true in the model.
   */
  private readonly changeDetectorRef: ChangeDetectorRef = inject(ChangeDetectorRef);

  /**
   * The most recently emitted term, or `null` when nothing has been emitted yet.
   *
   * `null` is used as the "never emitted" marker precisely so that a *first* emission of
   * `''` is not mistaken for a duplicate: comparing `null` against any string is false, so
   * the initial empty term is emitted correctly. A plain `''` initial value would have
   * swallowed it.
   */
  private lastEmittedTerm: string | null = null;

  public get isTermEmpty(): boolean {
    return this.term.value.length === 0;
  }

  /**
   * Whether this search control is currently unavailable.
   *
   * THE SINGLE SOURCE OF DISABLED STATE for the whole component. It is derived from the
   * exposed {@link term} rather than from a separate input, deliberately: the reactive
   * control already owns availability, `disable()` and `enable()` are its documented API,
   * and the fixed shared-component contract admits no further inputs. A parallel
   * `@Input() disabled` would be a second source of the same truth, free to disagree with
   * the control it is meant to describe.
   *
   * Three things read this one value, which is what closes the defect it was written for:
   *
   *   1. The native control disables itself, because reactive forms writes the `disabled`
   *      attribute for it. That part always worked.
   *   2. The submit button binds `[disabled]` to this getter. Previously it had no disabled
   *      binding at all, so it stayed clickable while the field beside it was inert — and
   *      the stylesheet's submit-disabled rule was consequently unreachable, styling a
   *      state no code could produce.
   *   3. {@link emitTerm} refuses to emit. Without that, the two paths that reach it could
   *      both still fire: a click on the still-enabled button, and — less obviously — the
   *      debounced stream itself, because `disable()` emits on `valueChanges` unless told
   *      otherwise, so the act of disabling the control could push a value through the
   *      debounce and emit a query.
   *
   * Guarding the funnel rather than the individual call sites is what makes that
   * exhaustive; see {@link emitTerm}.
   */
  public get isDisabled(): boolean {
    return this.term.disabled;
  }

  /**
   * Wires the debounced typing path.
   *
   * Built here rather than in a field initialiser because the bound input values
   * are not yet assigned when field initialisers run, and the delay derives from
   * one of them. Resolving it through a duration selector is what lets a rebound
   * delay take effect on the next keystroke.
   *
   * Teardown is deterministic: the stream completes when this instance is
   * destroyed, so there is no unsubscribe bookkeeping and no timer handle to leak.
   */
  public ngOnInit(): void {
    this.term.valueChanges
      .pipe(
        debounce(() => timer(this.resolveDebounceMs())),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((value: string): void => {
        this.emitTerm(value);
      });

    // Availability can change from outside this component's binding graph, so the view is
    // marked explicitly when it does. See {@link changeDetectorRef} for why `OnPush` makes
    // this necessary rather than merely tidy. Emitted values are ignored: the template
    // reads the control's own state through {@link isDisabled}, so the notification alone
    // is the signal.
    this.term.statusChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((): void => {
        this.changeDetectorRef.markForCheck();
      });

    this.suppressNativeSearchEvent();
  }

  /**
   * Guards the {@link search} output against a genuine browser and framework
   * collision.
   *
   * Blink and WebKit fire a native, bubbling DOM event literally named `search` on
   * an `<input type="search">` when Enter is pressed, and in some builds when the
   * browser's own clear affordance is used. That event bubbles to this component's
   * host - and when a consumer writes `(search)="..."`, the framework registers a
   * native DOM listener on the host *in addition to* subscribing to the matching
   * output. The bubbling native event therefore reaches the consumer's handler as
   * a raw `Event` rather than a term, and because it never passes through
   * {@link emitTerm} it also defeats the duplicate guard, firing on every Enter
   * press including repeats.
   *
   * The output name is fixed by the shared-component contract and the control must
   * keep its `search` type, so the defence belongs in the component that owns the
   * output, where a caller can neither forget nor undo it.
   *
   * The listener is registered in the capture phase, so it runs while the event
   * descends and therefore strictly before any bubble-phase listener on the same
   * host - an ordering the DOM specification guarantees, rather than one that
   * depends on registration order. `stopImmediatePropagation` then halts the event
   * outright. The Enter keystroke itself is untouched, because `keydown` is a
   * separate event that has already invoked {@link submit} by this point.
   */
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
   * Emits the current term immediately, bypassing any debounce still in flight, so
   * a keyboard user is never made to wait out the delay they just tried to skip.
   *
   * Bound to `Enter` on the control and to the submit button's click, so a keyboard user
   * is never made to wait out the debounce they just tried to skip.
   *
   * "Bypassing" is achieved by emitting now and letting the duplicate guard absorb the
   * debounced tail: the pending emission still arrives a moment later carrying the same
   * term, and {@link emitTerm} discards it. This is exactly why both paths must funnel
   * through one guard — a guard placed on the stream alone would never observe this
   * immediate emission, and the tail would emit the same term a second time.
   *
   * The event parameter exists to suppress the browser's own default action, and it is
   * needed on exactly one of the two call paths. Pressing Enter inside a text control that
   * sits anywhere within a `<form>` triggers that form's IMPLICIT SUBMISSION, which in a
   * single-page application means a full page navigation — the search would appear to work
   * for an instant and then the application would reload. Nothing in this component can
   * detect that it has been placed inside a form, and a shared component cannot dictate
   * where a consumer mounts it, so the default action is cancelled unconditionally
   * instead. Cancelling it costs nothing when no ancestor form exists.
   *
   * The submit button does NOT pass an event, and does not need to: it is declared
   * `type="button"`, which has no default action to cancel. The parameter is therefore
   * optional rather than required, and the click path continues to call this with no
   * argument.
   *
   * This does not replace {@link suppressNativeSearchEvent}, which remains necessary. That
   * guard catches the native `search` event, which a `type="search"` control also fires
   * when the user clicks the browser's own clear affordance — a path that involves no
   * keystroke and so reaches nothing here.
   *
   * @param event The originating keyboard event, when invoked from a key binding.
   */
  public submit(event?: Event): void {
    event?.preventDefault();
    this.emitTerm(this.term.value);
  }

  /**
   * The single emission funnel. Both the debounced path and the immediate submit
   * path go through here, which is what makes duplicate suppression uniform across
   * them.
   *
   * The term is bounded here, and only here, by {@link boundSearchTerm}: control characters
   * are removed and the result is truncated to {@link MAX_TERM_LENGTH}. Bounding sits at
   * this single funnel deliberately — placing it on the stream would leave the immediate
   * submit path unbounded, and placing it at each call site would let one be forgotten.
   *
   * It is applied *before* the duplicate guard, not after, so the guard compares the values
   * that are actually emitted. Bounding after the guard would let two different over-long
   * terms sharing the same first 256 units each pass the guard and emit the identical term
   * twice, reintroducing the duplicate query this funnel exists to prevent.
   *
   * For every term a user can type the bound is a strict no-op, so the verbatim contract on
   * {@link SearchInputComponent.search} is unaffected: nothing is trimmed, case-folded,
   * encoded or wildcarded, and no character is ever added.
   *
   * @param term The term to emit, as held by the control.
   */
  private emitTerm(term: string): void {
    // A disabled control must not produce queries, and this is the only place that
    // guarantee can be made exhaustively. Blocking the button alone would not be enough,
    // because there are three distinct routes into this funnel and only one of them
    // involves the button:
    //
    //   1. The debounced `valueChanges` stream. `AbstractControl.disable()` emits on
    //      `valueChanges` by default, so the act of disabling the control pushes a value
    //      into the debounce. Without this guard, calling `term.disable()` could itself
    //      fire a search a debounce-interval later — the control would be visibly
    //      disabled while a query it initiated was still in flight.
    //   2. `submit()` from the button click. Now blocked at the source too by the
    //      button's own `[disabled]` binding, which is the correct affordance, but a
    //      programmatic `submit()` call would still arrive here.
    //   3. `submit()` from the Enter key. A disabled native control cannot receive
    //      keystrokes at all, so this route closes on its own — but only while the
    //      `[disabled]` attribute is actually rendered, which is a template concern
    //      rather than an invariant of this class.
    //
    // Guarding the funnel makes the behaviour independent of which routes happen to be
    // reachable, so no future template or API change can reopen the hole.
    if (this.term.disabled) {
      return;
    }

    const boundedTerm = boundSearchTerm(term);

    // Comparing against `null` when nothing has been emitted yet is always false, so a
    // first emission of the empty term is correctly allowed through.
    if (this.lastEmittedTerm === boundedTerm) {
      return;
    }

    this.lastEmittedTerm = boundedTerm;
    this.search.emit(boundedTerm);
  }

  /**
   * Resolves the effective debounce delay: non-finite values fall back to the default,
   * and finite values are clamped to be non-negative.
   *
   * `0` passes through untouched because it is a legitimate setting — "emit on every
   * keystroke". A negative delay is clamped rather than rejected, so a mis-bound value
   * degrades to "emit as soon as possible" instead of throwing in a presentational
   * component.
   *
   * The finite check has to come FIRST, and cannot be folded into the clamp, because
   * `Math.max` does not filter non-finite input — it propagates it. `debounceMs` is typed
   * `number`, but a template binding is only as trustworthy as its source: a parsed
   * configuration value, a division, or an arithmetic expression over an absent field all
   * produce a `number` that is not a usable delay. Measured behaviour of the two cases
   * `Math.max` lets through:
   *
   *   • `Math.max(0, NaN)` is `NaN`, and `timer(NaN)` fires after roughly 1ms. Every
   *     keystroke emits immediately, so the debounce is silently gone and the consumer
   *     issues one request per character.
   *   • `Math.max(0, Infinity)` is `Infinity`, which does not fit a 32-bit signed integer
   *     and so is clamped by the timer implementation to 1ms. A caller who wrote
   *     `Infinity` meaning "never emit automatically" gets the exact opposite: emission on
   *     every keystroke, as fast as possible.
   *
   * Both failures are silent and both invert the caller's intent, which is why they are
   * corrected to {@link DEFAULT_DEBOUNCE_MS} — a value known to be a working delay —
   * rather than clamped to `0`, which would keep the "no debounce at all" behaviour that
   * makes them harmful. `-Infinity` is caught here too and receives the same default; it
   * would otherwise clamp to `0` and be indistinguishable from a deliberate `0`.
   */
  private resolveDebounceMs(): number {
    if (!Number.isFinite(this.debounceMs)) {
      return DEFAULT_DEBOUNCE_MS;
    }

    return Math.max(0, this.debounceMs);
  }
}
