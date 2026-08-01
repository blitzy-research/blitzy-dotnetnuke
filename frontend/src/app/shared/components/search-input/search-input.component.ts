/**
 * Shared `search-input` component — the free-text filter control that backs every
 * paged `GET .../?query=` endpoint across the administration screens.
 *
 * ## Responsibility
 *
 * This component does exactly one thing: it captures a term and emits it. It performs
 * no querying, holds no domain state and knows nothing about the resource being
 * filtered. The consuming feature owns the request, the page index and the choice of
 * field to search. That split is deliberate — business logic belongs in the
 * Application layer, and an Angular service is restricted to API communication.
 *
 * ## Match semantics — STARTS-WITH
 *
 * The emitted term feeds a `LIKE 'term%'` predicate, so the match is **starts-with**.
 * It is *not* a contains match, not a substring match and not a fuzzy match. The SQL
 * wildcard is composed server-side, inside the repository that owns the predicate;
 * this component emits the raw term and nothing else.
 *
 * The term is emitted **exactly as the user typed it**. It is never trimmed, never
 * case-folded, never Unicode-normalised and never URL-encoded here. Every one of
 * those mutations would change which rows match. Query-string composition is the
 * responsibility of the shared HTTP parameter utility, not of this component.
 *
 * ## The empty term is meaningful
 *
 * An empty term is emitted, and it means *omit the query parameter entirely* — it does
 * **not** mean "search for the empty string". The legacy screen expressed the same idea
 * by simply not appending `filter=` to the query string when the box was blank
 * (`Website/admin/Users/Users.ascx.vb` L254-L255). Consumers must honour that: receiving
 * `''` means drop the parameter and request the unfiltered page.
 *
 * ## Template contract
 *
 * The sibling template binds to the members below. They are per-instance internals, not
 * part of the component's public input/output surface:
 *
 * - `term` — the typed reactive control. Bind with `[formControl]="term"`.
 * - `fieldId` — a per-instance unique id. Bind `[id]="fieldId"` on the control and
 *   `[attr.for]="fieldId"` on its label so the two are programmatically associated.
 * - `submit()` — call from `(keydown.enter)` on the control and from `(click)` on the
 *   submit button. Both paths emit immediately.
 * - `isTermEmpty` — presentational only. Never gate emission on it.
 *
 * The submit affordance must carry a real accessible name (visually hidden text is
 * sufficient); the legacy control had none at all. It must also be a real
 * `<button type="button">`, so that Space and Enter both activate it natively.
 *
 * `type="search"` on the control is correct and safe: it supplies the implicit `searchbox`
 * role, and the native `search` DOM event it fires — which would otherwise leak into this
 * component's identically named output — is neutralised internally. See
 * {@link SearchInputComponent.suppressNativeSearchEvent}. Do not change it to
 * `type="text"`, and do not add a redundant `role` attribute.
 *
 * One measured note for the stylesheet: a field pinned to a `14rem` minimum plus the gap
 * and the submit button needs roughly 308px, which exceeds the content box of a padded
 * panel at a 375px viewport. Nothing clips, because the row overflows visibly into the
 * panel's own padding, but the row should be allowed to wrap at the narrow breakpoint
 * rather than relying on that slack.
 *
 * @example
 * ```html
 * <app-search-input
 *   placeholder="Search users"
 *   [debounceMs]="300"
 *   (search)="onSearch($event)" />
 * ```
 */
import {
  ChangeDetectionStrategy,
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
 * MIGRATION: 1 of 13 — `debounceMs` and this default are both NET ADDITIONS. The legacy
 * screen had no debounce whatsoever: the query fired only when the user clicked the
 * `btnSearch` image button (`Website/admin/Users/users.ascx` L9). Debouncing therefore
 * changes *when* the query fires, which is a behavioural difference rather than a
 * refactor, and is recorded here because it must not be silently absorbed.
 */
const DEFAULT_DEBOUNCE_MS = 300;

/**
 * Default placeholder wording.
 *
 * MIGRATION: 12 of 13 — localisation is not ported, so user-facing strings are authored
 * inline in English. The legacy resource files remain the authority for wording:
 * `Website/admin/Users/App_LocalResources/Users.ascx.resx` defines `Search.Text` as
 * `Search:` for the label. The legacy text box carried no placeholder at all, so this
 * default is itself a net addition. Deliberately neutral wording — never "contains",
 * "anywhere" or "fuzzy", because the match is starts-with.
 */
const DEFAULT_PLACEHOLDER = 'Search';

/**
 * Monotonic instance counter backing {@link SearchInputComponent.fieldId}.
 *
 * A module-scoped counter keeps ids unique and deterministic across every instance on a
 * page. A hardcoded literal id would collide the moment a screen rendered two search
 * inputs, breaking the label association for both.
 */
let searchInputInstanceCount = 0;

/**
 * Produces the next unique element id for a search input instance.
 *
 * @returns An id that is unique among all instances created in this document.
 */
function nextSearchInputId(): string {
  searchInputInstanceCount += 1;
  return `app-search-input-${searchInputInstanceCount}`;
}

/*
 * ---------------------------------------------------------------------------------------
 * Migration ledger — behavioural differences from the legacy search affordance that are
 * decided at this component's boundary. Recorded inline so none of them is absorbed
 * silently. Annotations 1, 2, 3 and 12 sit next to the code they govern.
 * ---------------------------------------------------------------------------------------
 *
 * MIGRATION: 4 of 13 — the legacy screens keyed BEHAVIOUR ON LOCALISED TEXT. The filter
 * value arriving from the letter strip was compared against translated strings to select a
 * query branch: `Localization.GetString("Unauthorized")`, `("OnLine")` and `("All")` in
 * `Website/admin/Users/Users.ascx.vb` L258, L261 and L264, plus
 * `Localization.GetString("Expired", LocalResourceFile)` in
 * `Website/admin/Portal/Portals.ascx.vb` L138 — four localised branch keys in total — and
 * against a hard-coded `"None"` sentinel at `Users.ascx.vb` L266. The target replaces all
 * five with typed discriminators owned by the consuming feature, and expresses "no filter"
 * as OMISSION of the query parameter. Neither this component nor its consumers ever emit
 * one of those literals as a search term.
 *
 * MIGRATION: 5 of 13 — LATENT DEFECT, ANNOTATED AND DELIBERATELY NOT FIXED. Legacy
 * business rules are preserved as found; a discovered defect is recorded rather than
 * repaired, so this note documents it instead of correcting the legacy behaviour.
 * `Website/App_GlobalResources/SharedResources.resx` really does define a localisable
 * `None.Text` entry (value `None`), yet `Users.ascx.vb` L266 compares the incoming filter
 * against the hard-coded English literal `"None"`. In any translated portal the resource
 * would resolve to translated text, the L266 comparison would fail, and execution would
 * fall through into the profile-property branch and query for the translated word. A
 * companion inconsistency proves the same carelessness: L266 compares case-SENSITIVELY
 * while L589 compares the same sentinel case-INSENSITIVELY via `Filter.ToUpper()`, and
 * L589 additionally uses the non-short-circuiting `And` rather than `AndAlso`. This is the
 * decisive evidence that keying behaviour on translated text is unsafe, and it is the
 * strongest justification for the typed discriminators described in annotation 4. The same
 * resource file also spells one key inconsistently with its own value: `OnLine.Text`
 * resolves to `Online`.
 *
 * MIGRATION: 6 of 13 — three legacy search MODES ARE DROPPED because no target endpoint
 * exists for them. `Unauthorized` (`Users.ascx.vb` L258-L260) and `OnLine`
 * (L261-L263) both returned UNPAGED result sets and hid the pager; users-online is out of
 * scope entirely. The portal-side `Expired` mode (`Portals.ascx.vb` L138-L140) was
 * likewise unpaged and has no target endpoint. Everything this component emits is a term
 * against a paged, filterable collection.
 *
 * MIGRATION: 7 of 13 — the legacy field selector was an OPEN, DATA-DRIVEN SET with an
 * unvalidated fall-through. `Users.ascx.vb` L267-L276 switched on the raw string literals
 * `"Email"` and `"Username"`, and its `Case Else` (L272-L274) passed whatever arbitrary
 * profile-property name happened to be selected straight into the query. The dropdown was
 * populated from the portal's profile-property definitions at L577-L582, so the set was
 * unbounded at compile time. The target replaces it with a validated, exhaustive
 * discriminator owned by the consuming feature. Worth recording: the legacy code had
 * already half-invented this. `AddSearchItem` (L205-L217) built each entry as
 * `New ListItem(text, name)` — localised display text paired with a STABLE, non-localised
 * value — so the FIELD selector was already a proper discriminator while the MODE selector
 * was compared against translated text. The legacy got one right and the other wrong.
 * (A minor ordering inconsistency in the same file: the dropdown was populated user-name
 * first, but the branch chain dispatched email first.)
 *
 * MIGRATION: 8 of 13 — PAGE INDEX BASE CHANGES, so a new term must reset the consumer's
 * page index to `0`. The legacy UI page number was ONE-based (`Users.ascx.vb` L51
 * initialises it to `1`) while the value handed to the data layer was zero-based (every
 * call subtracts one). The target wire contract is zero-based throughout. Relatedly, the
 * legacy helper that rebuilt the filter URL typed its page number as a STRING —
 * `Portals.ascx.vb` L215 declares `FilterURL(ByVal Filter As String, ByVal CurrentPage As
 * String)`, spanning L215-L232 — an artefact of the admin pages compiling with strict
 * type checking disabled. The target types a page index as a number, making the conversion
 * explicit. Paging itself is not this component's concern; it is recorded here because the
 * reset obligation travels with the emitted term.
 *
 * MIGRATION: 9 of 13 — DEEP-LINK TERM RESTORATION HAS NO TARGET EQUIVALENT. The legacy
 * screen repopulated the box from the query string on refresh or deep link:
 * `Users.ascx.vb` L589-L591 restored both the term (`txtSearch.Text = Filter`) and the
 * selected field. The fixed input surface for this component is placeholder and debounce
 * delay only, so a consumer has NO channel to seed an initial term. Widening the surface
 * to add one is deliberately declined here, because the shared component inventory is
 * closed and this component's contract is fixed. The gap is recorded rather than patched,
 * for whoever revisits the contract.
 *
 * MIGRATION: 10 of 13 — THE FIELD SELECTOR AND THE LETTER STRIP ARE NOT PART OF THIS
 * COMPONENT. The legacy free-text box and its adjacent field dropdown existed in exactly
 * one file in the whole in-scope surface (`Website/admin/Users/users.ascx` L7 and L8);
 * `Website/admin/Portal/portals.ascx` has no search box at all, only the letter strip. The
 * strip cannot be shared either, because it is ONE control whose extra entries differ per
 * screen: `CreateLetterSearch` appends the pseudo-filters to the same 26-letter list that
 * feeds the same repeater, giving the users screen 29 entries (26 letters plus `All`,
 * `OnLine`, `Unauthorized`, at `Users.ascx.vb` L304-L316) and the portals screen 28 (26
 * letters plus `All`, `Expired`, at `Portals.ascx.vb` L170-L179). The strip and the
 * localised branch keys of annotation 4 are the same control, so no shared component
 * could host it. Both affordances therefore belong to the consuming feature.
 *
 * MIGRATION: 11 of 13 — a legacy QUERY-KEY SPELLING INCONSISTENCY does not survive the
 * move. One logical key was spelled three ways in a single file: written camel-cased at
 * `Users.ascx.vb` L275, read camel-cased at L207-L208, and written entirely lower-cased at
 * L170 and L173. It worked only because the legacy request collection matched keys
 * case-INSENSITIVELY. HTTP query parameters are case-sensitive in the target, so a single
 * canonical spelling is mandatory. Query-string composition is owned by the shared HTTP
 * parameter utility, which this component neither imports nor duplicates. (For the record,
 * L170, L172, L173 and L721 each wrap an inline conditional, and L721 guards against the
 * dropdown itself being absent, which shows the control was not reliably present on every
 * render path.)
 *
 * MIGRATION: 13 of 13 — the legacy submit control rendered a RASTER ICON,
 * `~/images/icon_search_16px.gif` (`Website/admin/Users/users.ascx` L9). No legacy raster
 * asset is in scope, so the submit affordance is expressed with text, an inline vector or
 * pure CSS in the sibling template instead. That template must also give the control a
 * real accessible name: the legacy image button declared no text, tool tip or alternate
 * text, and no corresponding wording entry exists in any resource file, so it had no
 * accessible name whatsoever. Supplying one is a net addition and an accessibility fix.
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
   * Public by design: strict input access modifiers are enabled, so a non-public input
   * would be a compile error at every consumer.
   *
   * Treated as **plain text**. It is rendered through an ordinary property binding, which
   * escapes its content. Markup is never interpreted — the legacy resource files that
   * supply comparable wording include entries carrying live `<script>` blocks, so no
   * caller-supplied string is ever trusted as markup here.
   *
   * An explicitly supplied empty string is honoured exactly, producing a control with no
   * placeholder. That is sentinel discipline: the legacy null-string sentinel *is* the
   * empty string, so `''` is a legitimate value and must never be quietly replaced by a
   * fallback.
   */
  @Input() public placeholder: string = DEFAULT_PLACEHOLDER;

  /**
   * Debounce delay in milliseconds applied to typing before the term is emitted.
   *
   * Public by design, for the same strict-input-access reason as {@link placeholder}.
   *
   * `0` is a legitimate, supported value and is honoured as written; it is never treated
   * as "unset". Negative values are clamped to `0` rather than rejected, mirroring how the
   * legacy inline conditional clamps were carried across as an explicit `Math.max`.
   *
   * A delay of `0` still emits **asynchronously** — a zero-duration timer is scheduled
   * rather than run inline. Tests must therefore drive the clock (for example with
   * `fakeAsync` and `tick`) instead of asserting synchronously after a value change.
   *
   * The delay is read at each keystroke, so rebinding it mid-life takes effect
   * immediately rather than being frozen at initialisation.
   */
  @Input() public debounceMs: number = DEFAULT_DEBOUNCE_MS;

  /**
   * Emits the search term, verbatim.
   *
   * The payload is the raw term exactly as typed — no trimming, no case folding, no
   * encoding and no appended SQL wildcard. Match semantics are starts-with
   * (`LIKE 'term%'`).
   *
   * An emission of `''` is meaningful and means *omit the query parameter*, yielding the
   * unfiltered page. It does not mean "search for the empty string".
   *
   * Consecutive duplicate terms are suppressed, so a subscriber never sees the same term
   * twice in a row regardless of which input path produced it.
   *
   * Because a new term invalidates the current position in the result set, a consumer
   * must reset its page index to `0` when it receives one.
   */
  @Output() public readonly search = new EventEmitter<string>();

  /**
   * The typed reactive control holding the current term.
   *
   * `nonNullable` makes `value` a plain `string` rather than `string | null`, and makes
   * `reset()` return to `''` instead of `null` — which is what keeps the empty-term
   * contract expressible without introducing a second "absent" representation.
   */
  public readonly term = new FormControl<string>('', { nonNullable: true });

  /**
   * Per-instance unique element id, for label association in the template.
   *
   * Bind it to both the control's `id` and its label's `for`, so the two are
   * programmatically associated. A hardcoded literal would collide across instances.
   */
  public readonly fieldId = nextSearchInputId();

  /**
   * `DestroyRef` for the current instance, captured in a field initialiser because that
   * runs inside an injection context. It is handed to `takeUntilDestroyed` explicitly so
   * the debounce subscription can be built later, in `ngOnInit`, where the bound input
   * values are available.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * This instance's own host element.
   *
   * Injected for exactly one purpose — neutralising the native `search` DOM event
   * described on {@link suppressNativeSearchEvent}. Nothing else in this component
   * reaches for the DOM directly; all rendering is declarative.
   */
  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * The most recently emitted term, or `null` when nothing has been emitted yet.
   *
   * `null` is used as the "never emitted" marker precisely so that a *first* emission of
   * `''` is not mistaken for a duplicate: comparing `null` against any string is false, so
   * the initial empty term is emitted correctly. A plain `''` initial value would have
   * swallowed it.
   */
  private lastEmittedTerm: string | null = null;

  /**
   * Whether the control is currently empty.
   *
   * Presentational only — provided so the template can reflect emptiness visually. It
   * must never be used to gate emission, because an empty term is a meaningful value that
   * maps to omission of the query parameter.
   *
   * Emptiness is tested by length, never by truthiness, because `''` is a legitimate
   * value in this domain rather than an absence.
   */
  public get isTermEmpty(): boolean {
    return this.term.value.length === 0;
  }

  /**
   * Wires the debounced typing path.
   *
   * Built here rather than in a field initialiser because the bound input values are not
   * yet assigned when field initialisers run, and the delay is derived from one of them.
   *
   * The delay is resolved per keystroke through a duration selector, so a consumer that
   * rebinds the delay sees the new value take effect on the next keystroke instead of
   * being stuck with whatever was bound at initialisation.
   *
   * Teardown is deterministic: the stream completes when this instance is destroyed. No
   * manual unsubscribe bookkeeping, and no timer handles to leak.
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

    this.suppressNativeSearchEvent();
  }

  /**
   * Guards the {@link search} output against a genuine browser/framework collision.
   *
   * THE COLLISION. This component's control is an `<input type="search">`, which is the
   * correct, most specific native semantic for a filter field — it is what gives the
   * control its implicit `searchbox` role for assistive technology. Blink and WebKit,
   * however, fire a NATIVE, BUBBLING DOM event literally named `search` on such an input
   * when the user presses Enter (and, in some builds, when the browser's own clear
   * affordance is used). That event bubbles up to this component's host element.
   *
   * WHY THAT BREAKS THE CONTRACT. When a consumer writes `(search)="onSearch($event)"` on
   * this component, Angular registers a NATIVE DOM listener on the host element IN
   * ADDITION TO subscribing to the matching component output — both, not one or the other. The
   * bubbling native event therefore reaches the consumer's handler as a raw DOM `Event`
   * object, so a consumer doing `$event.length` or `$event.trim()` gets `undefined` or a
   * `TypeError`. Worse, that path never passes through {@link emitTerm}, so it defeats the
   * duplicate guard entirely and fires on EVERY Enter press, including duplicates — the
   * exact redundant-query behaviour this component exists to prevent. Verified in real
   * headless Chrome: three Enter presses on unchanged text produced three spurious
   * payloads, while submit-button clicks and debounced typing produced none.
   *
   * WHY THE FIX IS HERE AND NOT ELSEWHERE. The output name is fixed at `search` by the
   * shared-component specification and must not be renamed, and the control's markup lives
   * in the template. Neither `type="text"` (which would forfeit the `searchbox` role) nor a
   * template-side handler is an acceptable answer, so the defence belongs in the component
   * that owns the output — it cannot be forgotten or undone by a caller.
   *
   * HOW IT WORKS. The listener is registered on the host in the CAPTURE phase, so it runs
   * while the event descends toward the control, strictly before any bubble-phase listener
   * on the same host — an ordering guaranteed by the DOM specification rather than by
   * registration order, which is why this is preferred over a host-binding listener.
   * `stopImmediatePropagation` then halts the event outright, so it can never reach the
   * consumer. The Enter keystroke itself is untouched: `keydown` is a separate event that
   * has already invoked {@link submit} by this point, so the immediate-emission path keeps
   * working exactly as documented.
   *
   * Teardown is deterministic, removing the listener when this instance is destroyed.
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
   * Emits the current term immediately, bypassing any debounce still in flight.
   *
   * Bound to `Enter` on the control and to the submit button's click, so a keyboard user
   * is never made to wait out the debounce they just tried to skip.
   *
   * "Bypassing" is achieved by emitting now and letting the duplicate guard absorb the
   * debounced tail: the pending emission still arrives a moment later carrying the same
   * term, and {@link emitTerm} discards it. This is exactly why both paths must funnel
   * through one guard — a guard placed on the stream alone would never observe this
   * immediate emission, and the tail would emit the same term a second time.
   */
  public submit(): void {
    this.emitTerm(this.term.value);
  }

  /**
   * The single emission funnel. Both the debounced path and the immediate submit path go
   * through here, which is what makes duplicate suppression uniform across them.
   *
   * MIGRATION: 2 of 13 — the SQL wildcard is deliberately NOT appended here. The legacy
   * screens concatenated it onto the term at the call site, immediately before handing the
   * value to the data layer: `Website/admin/Users/Users.ascx.vb` L269 (by email), L271 (by
   * user name) and L274 (by profile property), and
   * `Website/admin/Portal/Portals.ascx.vb` L142 (portals by name). In the target,
   * composing that predicate is the repository's job, because all data access goes through
   * repository interfaces. Emitting a pre-wildcarded term from the client would
   * double-wildcard the predicate and silently change which rows match.
   *
   * MIGRATION: 3 of 13 — restating the match semantics at the point of emission so no
   * downstream consumer mislabels them: the predicate is `LIKE 'term%'`, which is
   * starts-with. It is not a contains match. Any consumer, parameter name, comment or
   * label that calls this "contains", "anywhere" or "fuzzy" is wrong.
   *
   * @param term The term to emit, exactly as held by the control.
   */
  private emitTerm(term: string): void {
    // Comparing against `null` when nothing has been emitted yet is always false, so a
    // first emission of the empty term is correctly allowed through.
    if (this.lastEmittedTerm === term) {
      return;
    }

    this.lastEmittedTerm = term;
    this.search.emit(term);
  }

  /**
   * Resolves the effective debounce delay, clamped to a non-negative value.
   *
   * `0` passes through untouched because it is a legitimate setting. Only a negative
   * delay is corrected, and it is clamped rather than rejected so a mis-bound value
   * degrades to "emit as soon as possible" instead of throwing in a presentational
   * component.
   */
  private resolveDebounceMs(): number {
    return Math.max(0, this.debounceMs);
  }
}
