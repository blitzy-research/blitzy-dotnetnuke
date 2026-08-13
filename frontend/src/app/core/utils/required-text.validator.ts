import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
 * A required-value validator for TEXT controls that treats an all-whitespace entry as absent. This module
 * exists to close a measured disagreement between the browser and the server it talks to.
 */

/**
 * Rejects an absent or all-whitespace value on a text control.
 *
 * @param control The control to inspect.
 * @returns `{ required: true }` when the value is absent or all whitespace, otherwise `null`.
 */
export const requiredText: ValidatorFn = (
  control: AbstractControl,
): ValidationErrors | null => {
  const value: unknown = control.value;

  // Absent in the plainest sense. Checked first so the string branch below can be about
  // whitespace and nothing else.
  if (value === null || value === undefined) {
    return { required: true };
  }

  if (typeof value === 'string') {
    return value.trim().length === 0 ? { required: true } : null;
  }

  if (Array.isArray(value)) {
    return value.length === 0 ? { required: true } : null;
  }

  return value === '' ? { required: true } : null;
};
