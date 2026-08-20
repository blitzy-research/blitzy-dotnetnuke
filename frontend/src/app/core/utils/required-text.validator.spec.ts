import { FormControl, ValidatorFn, Validators } from '@angular/forms';

import { requiredText } from './required-text.validator';

/** Specifications for {@link requiredText}. */
describe('requiredText', () => {
  /** Runs the validator against a value without going through a form. */
  function check(value: unknown): ReturnType<ValidatorFn> {
    return requiredText(new FormControl(value));
  }

  describe('the case the validator exists for', () => {
    it('rejects a single space, which Validators.required accepts', () => {
      expect(check(' ')).toEqual({ required: true });
      expect(Validators.required(new FormControl(' '))).toBeNull();
    });

    it('rejects whitespace the ASCII space test would miss', () => {
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
      expect(check([])).toEqual({ required: true });
      expect(Validators.required(new FormControl([]))).toEqual({ required: true });
      expect(check(['VIEW'])).toBeNull();
    });
  });

  describe('non-string values are not coerced', () => {
    it('accepts zero and false, matching Validators.required rather than a string test', () => {
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
