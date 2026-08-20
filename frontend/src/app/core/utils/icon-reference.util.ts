import { AbstractControl, ValidationErrors } from '@angular/forms';

/**
 * The single client-side statement of the API's icon-containment rule. WHY THIS FILE EXISTS RATHER THAN A
 * SECOND COPY OF THE RULE The rule began life private to the role form, which is where the first screen
 * to replace the legacy picker with a text box needed it.
 */

/** The error key the containment rule reports, shared so a consumer can resolve its own wording. */
export const ICON_NOT_CONTAINED_ERROR = 'iconNotContained';

/**
 * The wording for an icon reference that escapes the portal's own folder. The API'S OWN SENTENCE,
 * reproduced verbatim from `IconReferenceRules.NotContainedMessage`.
 */
export const ICON_NOT_CONTAINED_MESSAGE =
  "Icon File must be a relative path within the portal's own folder.";

/**
 * Refuses an icon reference that is rooted, drive-qualified, or traverses upwards. Mirrors the API's
 * containment rule EXACTLY, condition for condition and in the same order, because a client that is
 * stricter refuses a reference the server would store and one that is laxer spends a round trip to learn
 * what was knowable.
 *
 * @param control The icon control.
 * @returns The containment error, or `null` when the reference is contained or absent.
 */
export function containedIconPathValidator(
  control: AbstractControl<string>,
): ValidationErrors | null {
  const value = control.value;

  if (value.length === 0) {
    return null;
  }

  if (value.includes('..')) {
    return { [ICON_NOT_CONTAINED_ERROR]: true };
  }

  const first = value.charAt(0);

  if (first === '/' || first === '\\' || value.includes(':')) {
    return { [ICON_NOT_CONTAINED_ERROR]: true };
  }

  return null;
}
