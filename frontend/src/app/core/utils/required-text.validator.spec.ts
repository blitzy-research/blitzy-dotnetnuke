import { FormControl, ValidatorFn, Validators } from '@angular/forms';

import { requiredText } from './required-text.validator';

/**
 * Specifications for {@link requiredText}.
 *
 * The validator exists to close a measured browser/server disagreement, so these cases are
 * written against the two things that make it a SAFE substitution for `Validators.required`:
 * the error it reports must be indistinguishable, and its reference must be stable enough to
 * remove again.
 */
describe('requiredText', () => {
  /** Runs the validator against a value without going through a form. */
  function check(value: unknown): ReturnType<ValidatorFn> {
    return requiredText(new FormControl(value));
  }

  describe('the case the validator exists for', () => {
    it('rejects a single space, which Validators.required accepts', () => {
      // Both halves are asserted in one place on purpose: the point is not merely that
      // `requiredText` rejects a space, it is that it disagrees with the validator it
      // replaces. This is the exact value that was measured returning HTTP 400 from
      // `PUT /api/v1/users/2`.
      expect(check(' ')).toEqual({ required: true });
      expect(Validators.required(new FormControl(' '))).toBeNull();
    });

    it('rejects whitespace the ASCII space test would miss', () => {
      // Tabs, newlines and a non-breaking space. The server strips the same Unicode class
      // via string.IsNullOrWhiteSpace, so agreeing here is what keeps the two sides aligned
      // on more than the space bar.
      expect(check('\t')).toEqual({ required: true });
      expect(check('\n')).toEqual({ required: true });
      expect(check('   \t\n  ')).toEqual({ required: true });
      expect(check('\u00a0')).toEqual({ required: true });
    });

    it('accepts a value that merely has whitespace around it, without altering it', () => {
      const control = new FormControl(' Runtime Member ');

      expect(requiredText(control)).toBeNull();
      // The validator judges; it must never edit. A trimming validator that wrote back would
      // fight the user's cursor on every keystroke.
      expect(control.value).toBe(' Runtime Member ');
    });
  });

  describe('agreement with the validator it replaces', () => {
    it('reports the identical error object, so existing message resolution is unchanged', () => {
      // Every message resolver on these screens selects wording with the exact test
      // errors['required'] === true. A different key would leave a control invalid and
      // silent, which is why this equality is pinned rather than merely the truthiness.
      expect(check('')).toEqual({ required: true });
      expect(check('')).toEqual(Validators.required(new FormControl('')) as object);
    });

    it('agrees with Validators.required on absence', () => {
      expect(check(null)).toEqual({ required: true });
      expect(check(undefined)).toEqual({ required: true });
    });

    it('accepts ordinary text', () => {
      expect(check('Runtime Member')).toBeNull();
      expect(check('a')).toBeNull();
    });

    it('rejects an empty array, so the contract is a strict superset and never less strict', () => {
      // Validators.required's own emptiness test covers null, the empty string AND the empty
      // array. An earlier draft of this validator accepted [] and was therefore quietly LESS
      // strict than the validator it replaces — a trap for whoever next attached it to a
      // multi-valued control. Pinned in both directions so that cannot come back.
      expect(check([])).toEqual({ required: true });
      expect(Validators.required(new FormControl([]))).toEqual({ required: true });
      expect(check(['VIEW'])).toBeNull();
    });
  });

  describe('non-string values are not coerced', () => {
    it('accepts zero and false, matching Validators.required rather than a string test', () => {
      // String(0) is '0' and String(false) is 'false', both non-blank. Coercing would hand a
      // numeric or boolean control a different answer than the validator being replaced, so
      // these are delegated to the empty test instead. Both halves are pinned so the agreement
      // is asserted, not assumed.
      expect(check(0)).toBeNull();
      expect(check(false)).toBeNull();
      expect(Validators.required(new FormControl(0))).toBeNull();
      expect(Validators.required(new FormControl(false))).toBeNull();
    });

    it('accepts a populated object', () => {
      expect(check({ id: 1 })).toBeNull();
    });
  });

  describe('reference stability', () => {
    it('can be added and removed, because it is one frozen function value', () => {
      // The change-password screen adds and removes its required rule as the operation
      // changes, and add/removeValidators compare BY REFERENCE. A factory returning a fresh
      // closure would make the removal a silent no-op and leave the control permanently
      // required, so that failure mode is pinned here at the source.
      const control = new FormControl('');

      control.addValidators(requiredText);
      control.updateValueAndValidity();
      expect(control.hasError('required')).toBeTrue();

      control.removeValidators(requiredText);
      control.updateValueAndValidity();
      expect(control.hasError('required')).toBeFalse();
      expect(control.valid).toBeTrue();
    });

    it('does not accumulate when applied twice', () => {
      const control = new FormControl(' ');

      control.addValidators(requiredText);
      control.addValidators(requiredText);
      control.updateValueAndValidity();
      expect(control.errors).toEqual({ required: true });

      // One removal is enough precisely because the framework de-duplicated by reference.
      control.removeValidators(requiredText);
      control.updateValueAndValidity();
      expect(control.errors).toBeNull();
    });
  });
});
