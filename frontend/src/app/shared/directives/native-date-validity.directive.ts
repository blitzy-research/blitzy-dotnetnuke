import { Directive, ElementRef, inject } from '@angular/core';
import { AbstractControl, NG_VALIDATORS, ValidationErrors, Validator } from '@angular/forms';

/**
 * The error key reported for an entry the browser could not parse as a date.
 *
 * Exported so a consumer can test for it by name rather than by a re-typed string literal.
 */
export const BAD_INPUT_ERROR = 'badInput';

/**
 * The error key reported for a COMPLETE, parseable date that falls outside the host's own
 * `min`/`max` bounds.
 *
 * ⚠ A SEPARATE STATE FROM {@link BAD_INPUT_ERROR}, AND CONFLATING THEM WOULD HAVE HIDDEN IT. An
 * unparseable entry makes the browser blank the value; an out-of-range date is kept, and the browser
 * reports `rangeUnderflow` or `rangeOverflow` instead of `badInput`. So the two states look nothing
 * alike from the element, and a validator that read only `badInput` answered `null` for a date the
 * store cannot hold. Measured: a year of `0001` against `min="1753-01-01"` gave
 * `badInput: false, rangeUnderflow: true`, value retained as `0001-01-01`, and the form stayed
 * VALID with the submit control fully enabled.
 *
 * One key covers both directions, because the two are the same fact to every consumer - the date is
 * outside what can be stored - and the bound that was breached is already visible on the element.
 */
export const OUT_OF_RANGE_ERROR = 'dateOutOfRange';

/**
 * Surfaces a native date control's own "unparseable entry" state to the Angular form.
 *
 * ---------------------------------------------------------------------------
 * THE DEFECT THIS CLOSES
 * ---------------------------------------------------------------------------
 * A `<input type="date">` does not report a bad entry the way every other control reports a bad
 * value, and it has TWO distinct ways of being wrong that Angular cannot see:
 *
 *  1. AN UNPARSEABLE ENTRY. When what the operator has typed cannot be a date at all — a month
 *     segment left empty is the measured case — the browser sets `validity.badInput` and BLANKS
 *     `element.value`. The value accessor therefore hands Angular the empty string, which is a
 *     perfectly ordinary value for an optional date, so the control settles as `ng-valid` with no
 *     error of any kind. The form then believes it holds a deliberate "no date" — so the entry can
 *     be saved as an absence the operator never chose, and nothing anywhere says so.
 *  2. A COMPLETE DATE OUTSIDE THE STORED RANGE. A year of `0001` against `min="1753-01-01"` parses
 *     perfectly well, so the value is KEPT and `badInput` is FALSE; the browser reports
 *     `rangeUnderflow` instead. This case was missed by the first version of this directive and was
 *     found by driving the screen in a real browser: the value stayed `0001-01-01`, the control and
 *     the whole form stayed `ng-valid`, `aria-invalid` was absent, the border was the ordinary navy,
 *     no message existed anywhere in the document, and with an account chosen the submit control was
 *     FULLY ENABLED — offering to store a date the column cannot hold. The browser's own
 *     `validationMessage` said "Value must be 01/01/1753 or later." and nothing surfaced it, because
 *     the form carries `novalidate` and native bubbles never appear.
 *
 * ---------------------------------------------------------------------------
 * HOW IT WORKS, AND WHY READING THE DOM HERE IS CORRECT
 * ---------------------------------------------------------------------------
 * The directive registers as a validator and answers from the host element's own
 * `validity.badInput`. Reading the DOM inside a validator is normally a smell, and here it is
 * the only possible source: the invalid entry NEVER reaches the Angular control, because the
 * browser withheld it. The framework cannot judge a value it was never given.
 *
 * ⚠ IT MUST ALSO BE TOLD WHEN TO RE-RUN, AND ASSUMING OTHERWISE LEFT A CONTROL STUCK INVALID. A
 * value accessor calls `setValue` on every `input` event and `setValue` always re-runs validation, so
 * the first version of this directive relied on that alone. It is not sufficient: clearing the LAST
 * remaining segment of a partially-typed date changes `element.value` from the empty string to the
 * empty string, so the browser fires NO `input` event and NO `change` event. Measured with listeners
 * attached: clearing produced 4 keydown events, 1 blur event, and ZERO input or change events — so
 * the validator never re-ran, the stale `badInput` error survived a field that was now empty and
 * natively valid, and the form stayed `ng-invalid` with the submit control disabled, no message and
 * no explanation anywhere on screen. The only escape was to type a fresh complete date.
 *
 * So the directive listens on its own host for the events after which the native validity may differ
 * — `input`, `change`, `blur` and `keyup` — snapshots that validity, and asks Angular to re-run
 * validation only when the snapshot has actually changed. The request goes through the callback
 * registered by {@link registerOnValidatorChange}, which is the framework's own mechanism for a
 * validator whose verdict can change without the value changing; nothing here calls
 * `updateValueAndValidity` on a control it does not own. The change test is what keeps this from
 * being a re-validation on every keystroke.
 *
 * ---------------------------------------------------------------------------
 * IT REPORTS A STATE, IT DOES NOT AUTHOR A SENTENCE
 * ---------------------------------------------------------------------------
 * Deliberately no wording lives here. The screens that use this publish their own message for an
 * unusable date — the role-assignment screen renders the legacy's own `valEffectiveDate.Text` /
 * `valExpiryDate.Text` — so the sentence the operator reads is the measured legacy one and a message
 * authored here would be a second, invented voice for a rule the screens already describe.
 *
 * ⚠ BUT MAKING THE CONTROL INVALID IS NOT, BY ITSELF, ENOUGH, and assuming it was left this
 * directive's work invisible. A consuming screen gates its message on the error keys it knows about,
 * so a screen that tests only for its own parse-failure key will render nothing for the keys here.
 * A consumer of this directive must include {@link BAD_INPUT_ERROR} and {@link OUT_OF_RANGE_ERROR}
 * in whatever decides that a date field has something to say. Measured before that was done: the
 * control was correctly `ng-invalid` and the document still contained no message and no
 * `aria-invalid` anywhere.
 *
 * Applied by ATTRIBUTE PRESENCE rather than by a type selector, so a screen opts in. A
 * blanket `input[type=date]` selector would have reached every date control in the
 * application at once, including ones whose specifications assert today's behaviour, and a
 * shared validator that attaches itself uninvited is how one screen's fix becomes another
 * screen's surprise.
 */
@Directive({
  selector: 'input[type=date][appNativeDateValidity]',
  standalone: true,
  providers: [
    {
      provide: NG_VALIDATORS,
      useExisting: NativeDateValidityDirective,
      multi: true,
    },
  ],
  host: {
    // The events after which this host's NATIVE validity may differ from the verdict the form
    // currently holds. `input` and `change` are the ordinary ones and are largely redundant,
    // because a value change already re-runs validation through `setValue`; they are listened for
    // anyway so a browser that reports validity a beat later than it reports the value still
    // settles correctly. `keyup` and `blur` are the load-bearing ones: clearing the last remaining
    // segment of a partial date leaves `value` at the empty string it already was, so NO `input`
    // and NO `change` are fired and those two are the only signals that the state has moved. All
    // four funnel into one method that re-judges and then stays silent unless the verdict changed.
    '(input)': 'reassessNativeValidity()',
    '(change)': 'reassessNativeValidity()',
    '(keyup)': 'reassessNativeValidity()',
    '(blur)': 'reassessNativeValidity()',
  },
})
export class NativeDateValidityDirective implements Validator {
  /** The date input this directive is attached to. */
  private readonly host = inject<ElementRef<HTMLInputElement>>(ElementRef);

  /**
   * The framework's callback for "my verdict may have changed, please ask me again", registered
   * by {@link registerOnValidatorChange} when the control is set up.
   *
   * Null until then, and read through a guard, because the host events this directive listens for
   * can fire on a control that is not yet wired — an autofilled value settling during the same
   * change-detection pass that creates the form is the realistic case.
   */
  private onValidatorChange: (() => void) | null = null;

  /**
   * The verdict the Angular form currently holds for this host, as of the last time
   * {@link validate} ran.
   *
   * Kept so {@link reassessNativeValidity} can ask the framework to re-validate ONLY when the
   * native state has actually moved. Without the comparison, every keystroke in the field would
   * push an `updateValueAndValidity` through the control and its ancestors for a verdict that had
   * not changed.
   */
  private lastVerdict: string | null = null;

  /**
   * Reports whether the browser is holding something this control cannot pass to the store.
   *
   * @param _control The control being validated. Deliberately unused: for an unparseable entry the
   * offending text is precisely the thing that never reached the control, and for an out-of-range
   * date the bounds live on the element, so in both cases the answer can only come from the element.
   * @returns `{ badInput: true }` while the native control reports an entry it could not parse,
   * `{ dateOutOfRange: true }` while it holds a complete date outside its own `min`/`max`, otherwise
   * `null`.
   */
  public validate(_control: AbstractControl): ValidationErrors | null {
    const verdict: string | null = this.currentVerdict();

    this.lastVerdict = verdict;

    return verdict === null ? null : { [verdict]: true };
  }

  /**
   * Receives the framework's re-validation callback.
   *
   * Implementing this is what makes a validator whose answer depends on something other than the
   * control's value legitimate: it is the documented channel for asking the form to re-run
   * validation, and it means nothing here has to reach into a control it does not own and call
   * `updateValueAndValidity` on it.
   *
   * @param fn Callback supplied by the form directive, which re-runs validation for this control.
   */
  public registerOnValidatorChange(fn: () => void): void {
    this.onValidatorChange = fn;
  }

  /**
   * Re-judges the host's native validity after an event that could have changed it, and asks the
   * form to re-validate only if the verdict actually moved.
   *
   * ⚠ THIS IS THE CURE FOR A CONTROL THAT LATCHED INVALID. Clearing the last remaining segment of a
   * partially-entered date moves `value` from the empty string to the empty string, so the browser
   * fires no `input` and no `change` — measured as 4 keydown events, 1 blur event and zero of
   * either — and a validator that only ever re-runs from `setValue` keeps answering with the stale
   * verdict. The field then sat empty and natively valid while the control, the form and the submit
   * button all stayed invalid, with no message and nothing on screen to explain it.
   *
   * Bound to the host rather than to the document: the state being read belongs to this element, and
   * a host binding is torn down with the directive.
   */
  protected reassessNativeValidity(): void {
    if (this.onValidatorChange === null) {
      return;
    }

    if (this.currentVerdict() === this.lastVerdict) {
      return;
    }

    this.onValidatorChange();
  }

  /**
   * Reads the host element's own validity and reduces it to the single error key this directive
   * reports, or `null` when the browser is content.
   *
   * `badInput` is tested first because it is the stronger statement: the browser has thrown the
   * entry away, so no range comparison against it would mean anything. The two range flags collapse
   * into one key for the reason recorded on {@link OUT_OF_RANGE_ERROR}.
   *
   * @returns {@link BAD_INPUT_ERROR}, {@link OUT_OF_RANGE_ERROR}, or `null`.
   */
  private currentVerdict(): string | null {
    const element: HTMLInputElement = this.host.nativeElement;

    // `validity` is present on every form control in every browser this application supports,
    // but it is narrowed rather than assumed so that a host which is somehow not a validatable
    // element cannot throw inside a validator and take the whole form down with it.
    const validity: ValidityState | undefined = element.validity;

    if (validity === undefined) {
      return null;
    }

    if (validity.badInput) {
      return BAD_INPUT_ERROR;
    }

    if (validity.rangeUnderflow || validity.rangeOverflow) {
      return OUT_OF_RANGE_ERROR;
    }

    return null;
  }
}
