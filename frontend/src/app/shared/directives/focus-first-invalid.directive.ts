import { Directive, ElementRef, HostListener, inject } from '@angular/core';
import { FormGroupDirective } from '@angular/forms';

/**
 * Controls that can hold focus AND can be reported invalid, already narrowed to the invalid ones. ⚠ THE
 * `.ng-invalid` CLASS ALONE IS NOT ENOUGH, and that is why every entry pairs it with an element or a
 * role.
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

/** Moves focus to the first control a rejected submit is complaining about. */
@Directive({
  selector: 'form[formGroup]',
  standalone: true,
})
export class FocusFirstInvalidDirective {
  /** The form element, whose subtree is the only place this directive ever looks. */
  private readonly host = inject<ElementRef<HTMLFormElement>>(ElementRef);

  /** The reactive form on this same element. */
  private readonly formGroup = inject(FormGroupDirective, { optional: true, self: true });

  /**
   * Focuses the first invalid control when a submit is rejected. Does nothing when the form is valid -
   * the submit is on its way to the server and moving focus would interrupt a person for no reason - and
   * nothing when focus is ALREADY on the control that would be chosen, so a repeated submit does not
   * re-announce the same control or fight a person who has started correcting it.
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
