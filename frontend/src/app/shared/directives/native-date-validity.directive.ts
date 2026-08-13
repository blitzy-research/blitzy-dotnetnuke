import { Directive, ElementRef, inject } from '@angular/core';
import { AbstractControl, NG_VALIDATORS, ValidationErrors, Validator } from '@angular/forms';

/** The error key reported for an entry the browser could not parse as a date. */
export const BAD_INPUT_ERROR = 'badInput';

/**
 * The error key reported for a COMPLETE, parseable date that falls outside the host's own `min`/`max`
 * bounds. ⚠ A SEPARATE STATE FROM {@link BAD_INPUT_ERROR}, AND CONFLATING THEM WOULD HAVE HIDDEN IT. An
 * unparseable entry makes the browser blank the value; an out-of-range date is kept, and the browser
 * reports `rangeUnderflow` or `rangeOverflow` instead of `badInput`.
 */
export const OUT_OF_RANGE_ERROR = 'dateOutOfRange';

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
    // The events after which this host's NATIVE validity may differ from the verdict the form currently
    // holds.
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
   * The framework's callback for "my verdict may have changed, please ask me again", registered by {@link
   * registerOnValidatorChange} when the control is set up.
   */
  private onValidatorChange: (() => void) | null = null;

  /**
   * The verdict the Angular form currently holds for this host, as of the last time {@link validate} ran.
   * Kept so {@link reassessNativeValidity} can ask the framework to re-validate ONLY when the native
   * state has actually moved.
   */
  private lastVerdict: string | null = null;

  /**
   * Reports whether the browser is holding something this control cannot pass to the store.
   *
   * @param _control The control being validated.
   * @returns `{ badInput: true }` while the native control reports an entry it could not parse, `{
   * dateOutOfRange: true }` while it holds a complete date outside its own `min`/`max`, otherwise `null`.
   */
  public validate(_control: AbstractControl): ValidationErrors | null {
    const verdict: string | null = this.currentVerdict();

    this.lastVerdict = verdict;

    return verdict === null ? null : { [verdict]: true };
  }

  /**
   * Receives the framework's re-validation callback.
   *
   * @param fn Callback supplied by the form directive, which re-runs validation for this control.
   */
  public registerOnValidatorChange(fn: () => void): void {
    this.onValidatorChange = fn;
  }

  /**
   * Re-judges the host's native validity after an event that could have changed it, and asks the form to
   * re-validate only if the verdict actually moved. ⚠ THIS IS THE CURE FOR A CONTROL THAT LATCHED
   * INVALID. Clearing the last remaining segment of a partially-entered date moves `value` from the empty
   * string to the empty string, so the browser fires no `input` and no `change` — measured as 4 keydown
   * events, 1 blur event and zero of either — and a validator that only ever re-runs from `setValue`
   * keeps answering with the stale verdict.
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
   * Reads the host element's own validity and reduces it to the single error key this directive reports,
   * or `null` when the browser is content. `badInput` is tested first because it is the stronger
   * statement: the browser has thrown the entry away, so no range comparison against it would mean
   * anything.
   *
   * @returns {@link BAD_INPUT_ERROR} , {@link OUT_OF_RANGE_ERROR}, or `null`.
   */
  private currentVerdict(): string | null {
    const element: HTMLInputElement = this.host.nativeElement;

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
