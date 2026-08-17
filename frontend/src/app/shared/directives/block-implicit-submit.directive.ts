import { Directive, DestroyRef, ElementRef, inject } from '@angular/core';

/**
 * The input types HTML calls "fields that block implicit submission" — the ones for which pressing Enter
 * asks the form to submit. Anything else (a checkbox, a radio, a button, a file chooser) has its own Enter
 * behaviour and is left entirely alone.
 *
 * `textarea` is deliberately absent: Enter inserts a newline there and never submits, so there is nothing
 * to prevent and preventing it would break typing.
 */
const IMPLICIT_SUBMIT_FIELD_SELECTOR =
  'input:not([type]),'
  + 'input[type="text"],'
  + 'input[type="search"],'
  + 'input[type="url"],'
  + 'input[type="tel"],'
  + 'input[type="email"],'
  + 'input[type="password"],'
  + 'input[type="number"],'
  + 'input[type="date"],'
  + 'input[type="month"],'
  + 'input[type="week"],'
  + 'input[type="time"],'
  + 'input[type="datetime-local"]';

/**
 * OPT-IN SUPPRESSION OF A FORM'S IMPLICIT SUBMISSION, SO THAT ENTER IN A VALUE BOX COMMITS NOTHING.
 *
 * ⚠ THIS RESTORES A LEGACY OUTCOME RATHER THAN INVENTING A NEW ONE, and the mechanism it restores is
 * structural rather than scripted. The legacy role editor's four commands — `cmdUpdate`, `cmdCancel`,
 * `cmdDelete` and `cmdManage` at `Website/admin/Security/editroles.ascx` L179-L188 — are every one of them
 * an `asp:LinkButton`, which renders as an anchor calling `__doPostBack`, NOT as a submit control. The same
 * markup declares eight `asp:TextBox` controls. HTML's implicit-submission rule is then decisive: a form
 * with no submit button submits on Enter only when it holds exactly ONE field that blocks implicit
 * submission, and otherwise does nothing at all. So in the legacy editor, Enter in a fee box committed
 * nothing — and even had the surrounding page contributed a submit control, the button it activated would
 * have been the first one in tree order somewhere in the skin, never this editor's Update.
 *
 * ⚠ WHY THIS MATTERS MORE ON THIS FORM THAN ON ANY OTHER, and why it is a data-safety fix rather than a
 * keyboard nicety. The port renders a real `button[type="submit"]`, so Enter in any of the six fee, period
 * and trial boxes dispatched a genuine submit whose `defaultPrevented` was false, and the save ran. That
 * save is the one this console deliberately preserves as legacy-faithful even though it OVERWRITES stored
 * paid-membership values the form does not render — `EditRoles.ascx.vb:L212-L231` — so an accidental Enter
 * destroyed values the operator was never shown and never chose to change. The destructive save stays
 * exactly as it is, because it is parity; what changes is that a keystroke can no longer trigger it.
 *
 * ⚠ IT IS OPT-IN, BY ATTRIBUTE, RATHER THAN MATCHING EVERY REACTIVE FORM. Enter-to-submit is the expected
 * and wanted behaviour on a single-purpose form such as signing in, so a selector matching `form[formGroup]`
 * would have changed screens no finding was raised against. The attribute is applied where the hazard was
 * measured.
 *
 * What is NOT suppressed: Enter on a button, which the browser turns into a click, so both commands remain
 * fully keyboard-operable and the explicit Update is unaffected. Enter in a textarea, which inserts a
 * newline. And Enter anywhere outside this form.
 */
@Directive({
  selector: 'form[appBlockImplicitSubmit]',
  standalone: true,
})
export class BlockImplicitSubmitDirective {
  private readonly host = inject<ElementRef<HTMLFormElement>>(ElementRef);

  constructor() {
    const form: HTMLFormElement = this.host.nativeElement;

    const onKeydown = (event: KeyboardEvent): void => {
      // `key` rather than a code or a legacy numeric value: this is the documented identifier and it is
      // what a keyboard layout cannot change out from under the check.
      if (event.key !== 'Enter') {
        return;
      }

      // A composition session ends with Enter on many input methods, and swallowing that Enter would stop
      // an operator committing the characters they are typing. IME users are why this clause exists.
      if (event.isComposing) {
        return;
      }

      const target = event.target;

      if (!(target instanceof Element)) {
        return;
      }

      // Scoped to the form this directive is on, so a nested form or a portalled overlay cannot have its
      // own Enter handling suppressed from here.
      if (!form.contains(target)) {
        return;
      }

      if (!target.matches(IMPLICIT_SUBMIT_FIELD_SELECTOR)) {
        return;
      }

      // Cancels ONLY the implicit submission. The keystroke still reaches every other listener, so nothing
      // that legitimately answers Enter in a value box - a typeahead, a date picker - stops working.
      event.preventDefault();
    };

    // Capture phase, matching the sibling double-submit guard, so the default action is cancelled before a
    // handler on the field itself can observe a submission already under way.
    form.addEventListener('keydown', onKeydown, { capture: true });

    inject(DestroyRef).onDestroy(() => {
      form.removeEventListener('keydown', onKeydown, { capture: true });
    });
  }
}
