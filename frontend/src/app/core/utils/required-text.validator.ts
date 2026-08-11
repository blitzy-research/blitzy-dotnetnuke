import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
 * A required-value validator for TEXT controls that treats an all-whitespace entry as absent.
 *
 * This module exists to close a measured disagreement between the browser and the server it
 * talks to. `Validators.required` — Angular's own — rejects only `null`, `undefined` and the
 * empty string. A single space satisfies it. The server does not agree.
 *
 * ---------------------------------------------------------------------------
 * THE SERVER OWNS THE RULE, AND IT REJECTS WHITESPACE
 * ---------------------------------------------------------------------------
 * Every required text field on the account screens is declared with FluentValidation's
 * `NotEmpty()`, whose string predicate is `string.IsNullOrWhiteSpace`. An all-whitespace
 * value therefore FAILS server-side:
 *
 *     CreateUserRequestValidator  → Username, FirstName, LastName, DisplayName, Email
 *     UpdateUserRequestValidator  → FirstName, LastName, DisplayName, Email
 *
 * This was verified on the wire rather than inferred from the rule, by submitting a single
 * space as the display name of an existing account:
 *
 *     PUT /api/v1/users/2   {"displayName":" ", ...}
 *       → HTTP 400  urn:dnnmigration:error:request.invalid
 *         errors.DisplayName = ["Display Name is required"]
 *
 * Without this validator the browser calls that form valid, enables its submit, spends a
 * round trip, and reports the refusal as a whole-request failure — for a value the client
 * already had everything it needed to reject locally. The message the server returns is
 * word-for-word the message the client already holds for `required`, which is the clearest
 * possible evidence that the two sides mean the same rule and only the browser was lax.
 *
 * ---------------------------------------------------------------------------
 * WHY IT REPORTS UNDER THE `required` KEY AND NOT A NEW ONE
 * ---------------------------------------------------------------------------
 * It returns `{ required: true }` — byte-identical to what `Validators.required` returns.
 * That is deliberate and is the whole reason this is a safe substitution:
 *
 *   * every message resolver on these screens selects its wording with the exact test
 *     `errors['required'] === true`, so the measured per-control sentence ("Display Name is
 *     required") continues to be chosen with no change at the point of display;
 *   * `form-field` and the error-summary machinery key off the same error, so the inline
 *     message, the `aria-describedby` wiring and the focus-first-invalid operator all keep
 *     working untouched.
 *
 * A new key such as `blank` would have been more descriptive and strictly worse: it would
 * have silently produced a control that is invalid but has NO message, on every screen that
 * adopted it. The predicate is what needed tightening, not the vocabulary.
 *
 * ---------------------------------------------------------------------------
 * SCOPE — TEXT ONLY, AND IT DOES NOT INVENT A REQUIREMENT
 * ---------------------------------------------------------------------------
 * This is for controls whose value is a string the user types. It is NOT for selects,
 * numbers, dates or checkboxes: those cannot hold whitespace, so `Validators.required` is
 * already exact for them and swapping it would add indirection for no behavioural gain.
 *
 * Non-string values are delegated to the plain empty test rather than coerced, so attaching
 * this to a control that is later re-typed cannot change that control's outcome. The
 * validator adds no requirement of its own beyond presence — length, pattern and every
 * other rule stay where they are declared.
 *
 * A credential is deliberately NOT in scope. The change-password screen keeps
 * `Validators.required` on its password controls: a password is opaque bytes the user chose,
 * not prose to be judged for blankness, and this application never trims one. The server's
 * own refusal of an all-whitespace credential still arrives keyed to the field, so that
 * screen reports it beside the box without the client second-guessing what a password may
 * contain.
 *
 * ---------------------------------------------------------------------------
 * ⚠ EXPORTED AS A STABLE CONST, NOT A FACTORY — REMOVAL DEPENDS ON IT
 * ---------------------------------------------------------------------------
 * `AbstractControl.addValidators` and `removeValidators` compare by REFERENCE. A factory
 * returning a fresh closure per call would make `removeValidators(requiredText())` a silent
 * no-op — it would remove nothing and leave the control permanently required — and repeated
 * `addValidators` would accumulate duplicates. Screens on this feature do add and remove a
 * required rule dynamically as the operation changes, so this module exports ONE frozen
 * function value that is safe to add and remove any number of times.
 */

/**
 * Rejects an absent or all-whitespace value on a text control.
 *
 * @param control The control to inspect.
 * @returns `{ required: true }` when the value is absent or all whitespace, otherwise `null`.
 *
 * @example
 * ```ts
 * displayName: new FormControl<string>('', {
 *   nonNullable: true,
 *   validators: [requiredText, Validators.maxLength(128)],
 * })
 * ```
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

  // The case this module exists for. `trim` removes every Unicode whitespace character,
  // which is the same class `string.IsNullOrWhiteSpace` removes on the server, so the two
  // sides agree on non-breaking spaces and tabs and not merely on the ASCII space.
  if (typeof value === 'string') {
    return value.trim().length === 0 ? { required: true } : null;
  }

  // Not a string. Deliberately NOT coerced: `String(0)` is `'0'` and `String(false)` is
  // `'false'`, both of which are non-blank, so coercion here would quietly hand a number or
  // boolean control a different answer than `Validators.required` gives it.
  //
  // An empty ARRAY is treated as absent, matching `Validators.required` exactly — its own
  // emptiness test covers `null`, the empty string AND the empty array. Diverging here would
  // have been a trap for whoever later attached this to a multi-valued control: it would have
  // been quietly LESS strict than the validator it advertises itself as a stricter version of.
  // With this branch the contract is a clean superset — everything `Validators.required`
  // rejects, plus all-whitespace text — which is the whole reason it is safe to substitute.
  if (Array.isArray(value)) {
    return value.length === 0 ? { required: true } : null;
  }

  return value === '' ? { required: true } : null;
};
