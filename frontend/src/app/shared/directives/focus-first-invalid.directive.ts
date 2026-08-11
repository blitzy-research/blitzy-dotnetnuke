import { Directive, ElementRef, HostListener, inject } from '@angular/core';
import { FormGroupDirective } from '@angular/forms';

/**
 * Controls that can hold focus AND can be reported invalid, already narrowed to the invalid ones.
 *
 * ⚠ THE `.ng-invalid` CLASS ALONE IS NOT ENOUGH, and that is why every entry pairs it with an element or a
 * role. Angular writes its validity classes onto EVERY element carrying a control directive, which includes
 * the `form` itself and every `formGroupName` and `formArrayName` container - so a bare `.ng-invalid` query
 * returns the form first, and focusing the form moves focus nowhere useful while looking like it worked.
 *
 * The set matches the describable set in the shared field wrapper: the three native form controls, an
 * editable region, and the ARIA widget roles that stand in for them. A link and a command button are
 * deliberately absent - neither can be invalid, and both are routinely projected beside a control
 * (`Website/admin/Security/roles.ascx` L5-L16 puts an Edit link and a Delete image button under one label).
 *
 * ⚠ EXPORTED, AND THE EXPORT IS DELIBERATE RATHER THAN INCIDENTAL — #8. A form whose only fault is a
 * GROUP rule has no invalid control at all, so this selector matches nothing and the directive
 * correctly declines to act. A component in that position has to supply the focus itself, and it must
 * decide whether the directive has declined using EXACTLY this selector — a hand-copied approximation
 * would drift, and the two would then either both act on one submit or neither would. Exporting it
 * keeps one definition and makes "has the directive declined" a question with one answer. A consumer
 * may READ it; nothing may extend or reorder it.
 *
 * A DISABLED control is excluded twice over: Angular removes disabled controls from validation entirely, so
 * one cannot be `.ng-invalid` in the first place, and a disabled element cannot take focus even if it were.
 * The exclusion is written anyway, because a consumer may disable a native control without telling the form
 * model, and a focus call that silently does nothing is the hardest kind of failure to notice.
 */
export const INVALID_CONTROL_SELECTOR = [
  'input.ng-invalid:not([type="hidden"]):not([disabled])',
  'select.ng-invalid:not([disabled])',
  'textarea.ng-invalid:not([disabled])',
  '[contenteditable="true"].ng-invalid',
  '[role="checkbox"].ng-invalid:not([disabled])',
  '[role="combobox"].ng-invalid:not([disabled])',
  '[role="listbox"].ng-invalid:not([disabled])',
  '[role="radio"].ng-invalid:not([disabled])',
  '[role="slider"].ng-invalid:not([disabled])',
  '[role="spinbutton"].ng-invalid:not([disabled])',
  '[role="switch"].ng-invalid:not([disabled])',
  '[role="textbox"].ng-invalid:not([disabled])',
].join(',');

/**
 * Moves focus to the first control a rejected submit is complaining about.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS EXISTS
 * ---------------------------------------------------------------------------
 *
 * Runtime testing of every administration form found the same defect on all of them: submitting an
 * incomplete form re-rendered the error messages and LEFT FOCUS EXACTLY WHERE IT WAS. On the account form
 * the submit control sits below the fold at most viewport widths, so a person who had scrolled down to press
 * it was told nothing at all - the messages appeared off-screen above them, focus stayed on a button that
 * had apparently done nothing, and the form read as silently unsubmittable. For a screen-reader user the
 * failure was total: the shared field wrapper announces its message region assertively, but with focus
 * parked on the submit button there was no route from "something is wrong" to WHICH control is wrong.
 *
 * Moving focus to the first offending control is the affordance that closes that gap, and it is the one
 * WCAG technique for the situation (G139, "Creating a mechanism that allows users to jump to errors"). It
 * costs nothing visually: the control's own focus ring is already part of the design system, and the browser
 * scrolls the control into view as a consequence of focusing it, which is exactly the correction a person
 * who pressed a below-the-fold submit needs.
 *
 * ---------------------------------------------------------------------------
 * WHY IT IS A DIRECTIVE ON THE FORM, AND NOT A CALL IN EACH COMPONENT
 * ---------------------------------------------------------------------------
 *
 * Eighteen forms across five features reject a submit the same way, each calling `markAllAsTouched()` in its
 * own handler. Repeating a focus call beside each of those is 18 opportunities for one to be forgotten - and
 * a missing one is invisible, because the form still works for anyone who can see the messages. Attaching
 * the behaviour to `form[formGroup]` makes it a property of BEING a reactive form in this application, so a
 * form authored later inherits it by construction rather than by remembering.
 *
 * The selector matches automatically and the directive takes no inputs, so nothing is added to any template.
 * Each standalone component that hosts a form imports it, which is the only registration a standalone
 * component permits, and which is also the record of where it applies.
 *
 * ---------------------------------------------------------------------------
 * WHY IT IS SYNCHRONOUS
 * ---------------------------------------------------------------------------
 *
 * The `submit` listener runs before or after the component's own `ngSubmit` handler depending on directive
 * registration order, and that ordering DOES NOT MATTER here: Angular computes validity from values, not
 * from touched state, so `.ng-invalid` is already on the offending control when the event fires and
 * `markAllAsTouched()` changes only whether the message is DISPLAYED. Focusing synchronously therefore needs
 * no deferral, no timer and no render hook - which also means no specification has to be asynchronous to
 * observe it, and there is no window in which an unrelated render could move focus instead.
 *
 * The message region catches up on its own: the shared field wrapper renders it with `role="alert"`, so it
 * is announced when it appears, and the same wrapper writes `aria-describedby`, `aria-errormessage` and
 * `aria-invalid` onto the control itself during that render - so from the next moment onwards the focused
 * control carries its own explanation.
 */
@Directive({
  selector: 'form[formGroup]',
  standalone: true,
})
export class FocusFirstInvalidDirective {
  /** The form element, whose subtree is the only place this directive ever looks. */
  private readonly host = inject<ElementRef<HTMLFormElement>>(ElementRef);

  /**
   * The reactive form on this same element.
   *
   * Read for its validity rather than inferred from the DOM, so the decision to act is the FORM MODEL'S
   * decision. `self` restricts the lookup to this element, so a nested form-like structure can never make
   * this directive act on another form's state; `optional` keeps the directive harmless if it is ever
   * applied to an element that has no form group, in which case it does nothing at all.
   */
  private readonly formGroup = inject(FormGroupDirective, { optional: true, self: true });

  /**
   * Focuses the first invalid control when a submit is rejected.
   *
   * Does nothing when the form is valid - the submit is on its way to the server and moving focus would
   * interrupt a person for no reason - and nothing when focus is ALREADY on the control that would be
   * chosen, so a repeated submit does not re-announce the same control or fight a person who has started
   * correcting it.
   */
  @HostListener('submit')
  protected onSubmit(): void {
    if (this.formGroup === null || this.formGroup.form.valid) {
      return;
    }

    const target = this.host.nativeElement.querySelector<HTMLElement>(INVALID_CONTROL_SELECTOR);

    if (target === null || target === this.host.nativeElement.ownerDocument.activeElement) {
      return;
    }

    target.focus();
  }
}
