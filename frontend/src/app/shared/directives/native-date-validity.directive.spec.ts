import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';

import {
  BAD_INPUT_ERROR,
  NativeDateValidityDirective,
  OUT_OF_RANGE_ERROR,
} from './native-date-validity.directive';

/**
 * Specifications for {@link NativeDateValidityDirective}.
 *
 * ⚠ EVERY FLAG ON `ValidityState` IS READ-ONLY AND CANNOT BE SET FROM A TEST. A browser raises
 * `badInput` from its own parsing of real keystrokes and `rangeUnderflow`/`rangeOverflow` from its
 * own comparison against `min`/`max`, and no API exposes any of them for writing. These cases
 * therefore substitute the property on the host element for the duration of one assertion, which is
 * honest about what is being proven: the directive's CONTRACT — that it reads the element's own
 * validity, reports it as a control error, and re-judges when that validity moves — rather than the
 * browser's parsing and comparison, which belong to the browser.
 *
 * Both states WERE measured in a real browser before being pinned here, and they behave differently
 * enough that conflating them hid one of them for a whole round of work: an unparseable entry blanks
 * the value and raises `badInput`, while `0001-01-01` against `min="1753-01-01"` KEEPS the value and
 * raises `rangeUnderflow` with `badInput` false.
 */
describe('NativeDateValidityDirective', () => {
  @Component({
    standalone: true,
    imports: [ReactiveFormsModule, NativeDateValidityDirective],
    template: `
      <form [formGroup]="form">
        <input id="when" type="date" appNativeDateValidity formControlName="when" />
      </form>
    `,
  })
  class HostComponent {
    readonly form = new FormGroup({
      when: new FormControl<string>('', { nonNullable: true }),
    });
  }

  let fixture: ComponentFixture<HostComponent>;

  /** The date input under test. */
  function input(): HTMLInputElement {
    // Narrowed through HTMLElement first: `fixture.nativeElement` is `any`, and a generic call on
    // an untyped value is refused outright under this workspace's compiler settings.
    const root = fixture.nativeElement as HTMLElement;
    const found = root.querySelector<HTMLInputElement>('#when');

    if (found === null) {
      throw new Error('the date input is not rendered');
    }

    return found;
  }

  /**
   * Replaces the host's validity with a stub reporting exactly the flags supplied.
   *
   * Every flag not named is absent and therefore falsy, which is what lets one case assert that a
   * range violation is reported even though `badInput` is NOT set — the asymmetry that was missed.
   *
   * @param state The validity flags the element should claim.
   */
  function reportValidity(state: Partial<ValidityState>): void {
    Object.defineProperty(input(), 'validity', {
      configurable: true,
      get: (): ValidityState => state as ValidityState,
    });
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  it('leaves an ordinary control valid and adds no error of its own', () => {
    const control = fixture.componentInstance.form.controls.when;

    expect(control.valid).toBeTrue();
    expect(control.errors).toBeNull();
  });

  it('reports the browser\'s unparseable entry as a control error', () => {
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ badInput: true });
    control.updateValueAndValidity();

    expect(control.hasError(BAD_INPUT_ERROR)).toBeTrue();
    expect(control.valid).toBeFalse();
  });

  it('surfaces it even though the value the control holds is a perfectly ordinary empty string', () => {
    // ⚠ THIS IS THE WHOLE DEFECT IN ONE ASSERTION. The browser BLANKS the value when it cannot
    // parse the entry, so the control holds '' — an entirely legitimate value for an optional date.
    // Any judgement made from the value alone therefore says "valid", which is why this directive
    // has to read the element instead.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ badInput: true });
    control.setValue('');

    expect(control.value).toBe('');
    expect(control.valid).withContext('invalid despite an unremarkable value').toBeFalse();
  });

  it('re-runs on the value-accessor path whenever the value itself changes', () => {
    // The path that always worked: setValue re-runs validation, so a transition into and out of the
    // bad state that comes WITH a value change needs nothing extra wired up. It is pinned because
    // the listeners added later must not be allowed to become the only working path.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ badInput: true });
    control.setValue('');
    expect(control.hasError(BAD_INPUT_ERROR)).toBeTrue();

    reportValidity({});
    control.setValue('2026-08-09');
    expect(control.hasError(BAD_INPUT_ERROR)).toBeFalse();
    expect(control.valid).toBeTrue();
  });

  it('reports a complete date below the minimum, which the browser KEEPS and does not flag as bad input', () => {
    // ⚠ THE STATE THE FIRST VERSION OF THIS DIRECTIVE ANSWERED `null` FOR. Measured in a real
    // browser: typing 0001-01-01 into a field with min="1753-01-01" gave badInput false,
    // rangeUnderflow true, and the value RETAINED — so the control stayed valid, nothing was
    // marked aria-invalid, no message existed anywhere, and the submit control stayed enabled over
    // a date the column cannot store.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ rangeUnderflow: true });
    control.setValue('0001-01-01');

    expect(control.value).withContext('the browser keeps an out-of-range date').toBe('0001-01-01');
    expect(control.hasError(OUT_OF_RANGE_ERROR)).toBeTrue();
    expect(control.hasError(BAD_INPUT_ERROR)).withContext('a different state').toBeFalse();
    expect(control.valid).toBeFalse();
  });

  it('reports a complete date above the maximum under the same key', () => {
    // One key for both directions, because the two are the same fact to a consumer and the bound
    // that was breached is already on the element.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ rangeOverflow: true });
    control.setValue('99999-01-01');

    expect(control.hasError(OUT_OF_RANGE_ERROR)).toBeTrue();
    expect(control.valid).toBeFalse();
  });

  it('prefers the unparseable verdict when the browser reports both', () => {
    // A discarded entry is the stronger statement: no range comparison against a value the browser
    // threw away would mean anything, so exactly one key is reported and it is that one.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ badInput: true, rangeUnderflow: true });
    control.updateValueAndValidity();

    expect(control.hasError(BAD_INPUT_ERROR)).toBeTrue();
    expect(control.hasError(OUT_OF_RANGE_ERROR)).toBeFalse();
  });

  it('re-judges from a host event when the validity moves WITHOUT the value changing', () => {
    // ⚠ THE LATCHED-INVALID DEFECT, PINNED. Measured with listeners attached to a real date input:
    // clearing the last remaining segment of a partial date produced 4 keydown events, 1 blur
    // event, and ZERO input and ZERO change events — because `value` went from '' to ''. A
    // validator that only ever re-runs from setValue therefore kept the stale error forever: the
    // field sat empty and natively valid while the control, the form and the submit button all
    // stayed invalid, with no message and nothing on screen to explain it.
    const control = fixture.componentInstance.form.controls.when;

    reportValidity({ badInput: true });
    control.setValue('');
    expect(control.hasError(BAD_INPUT_ERROR)).withContext('the stale state').toBeTrue();

    // The browser is content again, and the value is the empty string it already was, so nothing
    // on the value path can tell the form.
    reportValidity({});
    input().dispatchEvent(new Event('blur'));

    expect(control.value).withContext('unchanged throughout').toBe('');
    expect(control.hasError(BAD_INPUT_ERROR)).toBeFalse();
    expect(control.valid).withContext('the control is released').toBeTrue();
  });

  it('asks the form to re-validate only when the verdict has actually changed', () => {
    // The change test is what keeps a listener on keyup from pushing an updateValueAndValidity
    // through the control and its ancestors on every keystroke.
    const control = fixture.componentInstance.form.controls.when;
    const revalidate = spyOn(control, 'updateValueAndValidity').and.callThrough();

    input().dispatchEvent(new Event('keyup'));
    expect(revalidate).withContext('nothing moved').not.toHaveBeenCalled();

    reportValidity({ rangeUnderflow: true });
    input().dispatchEvent(new Event('keyup'));
    expect(revalidate).withContext('the verdict moved').toHaveBeenCalled();
    expect(control.hasError(OUT_OF_RANGE_ERROR)).toBeTrue();
  });

  it('does not throw when the host reports no validity at all', () => {
    // A validator that throws takes the whole form down with it, so the narrowing is pinned.
    const control = fixture.componentInstance.form.controls.when;

    Object.defineProperty(input(), 'validity', {
      configurable: true,
      get: (): ValidityState | undefined => undefined,
    });

    expect(() => {
      control.updateValueAndValidity();
    }).not.toThrow();
    expect(control.valid).toBeTrue();
  });
});
