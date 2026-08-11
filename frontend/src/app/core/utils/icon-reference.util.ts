import { AbstractControl, ValidationErrors } from '@angular/forms';

/**
 * The single client-side statement of the API's icon-containment rule.
 *
 * WHY THIS FILE EXISTS RATHER THAN A SECOND COPY OF THE RULE
 * ---------------------------------------------------------
 * The rule began life private to the role form, which is where the first screen to replace the legacy
 * picker with a text box needed it. When the module form needed the same rule, copying it would have
 * produced two independent statements of one constraint, free to drift apart - and the server has
 * already been through exactly that: `CreateRoleRequestValidator` records that its containment rule was
 * hoisted into a shared helper precisely because, while it was private to one class, the paths that
 * lacked it "accepted references this one refused, and the weaker path defined the application's actual
 * behaviour". That is not a hypothetical. It was measured on the module path: `../../../etc/passwd`,
 * `../../bad`, `..\\..\\bad` and `/etc/passwd` were every one of them accepted and stored verbatim by
 * module create and module update, while the role and page paths refused all four.
 *
 * So this rule is stated ONCE on the client, mirroring the API's own `IconReferenceRules`, and every
 * screen that offers an icon text box consumes it from here.
 *
 * WHY THE RULE IS NEEDED AT ALL, GIVEN NO LEGACY SCREEN VALIDATED THIS FIELD
 * -------------------------------------------------------------------------
 * Because no legacy screen NEEDED to. Both `editroles.ascx` and `modulesettings.ascx` declared the icon
 * as a `dnn:Url` / `portal:url` PICKER over the portal's own files - `modulesettings.ascx` line 116 is
 * `<portal:url id="ctlIcon" showurls="False" showtabs="False" ...>` - and the code-behind stored whatever
 * the picker yielded. An arbitrary path was unreachable BY CONSTRUCTION, so there was nothing to
 * validate. The legacy control library is out of scope here, so the field is a plain text box, and the
 * constraint the picker enforced structurally has to be enforced by a rule instead. Adding it therefore
 * PRESERVES the legacy outcome rather than narrowing it: it refuses only values the legacy screen could
 * never have produced.
 */

/** The error key the containment rule reports, shared so a consumer can resolve its own wording. */
export const ICON_NOT_CONTAINED_ERROR = 'iconNotContained';

/**
 * The wording for an icon reference that escapes the portal's own folder.
 *
 * The API'S OWN SENTENCE, reproduced verbatim from `IconReferenceRules.NotContainedMessage`. There is no
 * legacy wording to preserve - the picker had nothing to refuse - so reproducing the server's sentence
 * is what keeps one rule described one way whether it is caught before the request or after it.
 */
export const ICON_NOT_CONTAINED_MESSAGE =
  "Icon File must be a relative path within the portal's own folder.";

/**
 * Refuses an icon reference that is rooted, drive-qualified, or traverses upwards.
 *
 * Mirrors the API's containment rule EXACTLY, condition for condition and in the same order, because a
 * client that is stricter refuses a reference the server would store and one that is laxer spends a
 * round trip to learn what was knowable. The three conditions are:
 *
 * - a parent-directory segment ANYWHERE in the value, tested as a substring rather than per segment,
 *   which is what the server tests - so `a..b` is refused too, deliberately and identically;
 * - a leading separator of either kind, which makes the reference absolute on either platform;
 * - a colon anywhere, which turns the value into a drive-qualified path or a URI.
 *
 * The empty value is VALID and means "no icon chosen", which is what the legacy screens stored and what
 * the preserved sentinel convention represents as absence. The value is deliberately NOT trimmed: the
 * server tests what it is given, and trimming here would accept a reference whose stored form the server
 * would then refuse.
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
