//
// Specification for the rejected-submit focus affordance.
//
//  NOTHING here is a ported test: the legacy tree contains zero automated tests of any kind. Every
//  expectation below was derived from what runtime testing measured on the migrated administration forms -
//  a rejected submit re-rendered its messages and left focus exactly where it was, which on the account
//  form meant focus stayed on a submit control below the fold while the messages appeared off-screen above
//  it. The behaviour asserted here is the WCAG G139 technique for that situation.
//
//  The directive is exercised through a real host component and a real `formGroup`, never in isolation:
//  its whole contract is "when a reactive form on this element rejects a submit, focus the first control
//  the form is complaining about", and neither half of that is observable without both.
//
//  `provideHttpClient()` is registered BEFORE `provideHttpClientTesting()` because the testing function
//  replaces the backend the first one installed. `verify()` in `afterEach` doubles as a positive assertion
//  that this directive performs no network access.
//

import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { FocusFirstInvalidDirective } from './focus-first-invalid.directive';

/**
 * A host whose shape mirrors the migrated forms: several controls, a projected link that can never be
 * invalid, and a submit control that sits AFTER every field - which is the arrangement that made the
 * defect invisible to a person who had scrolled to press it.
 */
@Component({
  selector: 'app-focus-host',
  standalone: true,
  imports: [ReactiveFormsModule, FocusFirstInvalidDirective],
  template: `
    <form [formGroup]="form" (ngSubmit)="onSubmit()" novalidate>
      <a class="probe-link" href="#help">Help</a>
      <select class="probe-select" formControlName="frequency">
        <option value="">Choose</option>
        <option value="M">Month</option>
      </select>
      <input class="probe-name" type="text" formControlName="name" />
      <textarea class="probe-description" formControlName="description"></textarea>
      <button class="probe-submit" type="submit">Update</button>
    </form>
  `,
})
class FocusHostComponent {
  public readonly form = new FormGroup({
    frequency: new FormControl('M', { nonNullable: true }),
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  public submissions = 0;

  public onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    this.submissions += 1;
  }
}

describe('FocusFirstInvalidDirective', () => {
  let httpMock: HttpTestingController;
  let fixture: ComponentFixture<FocusHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FocusHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(FocusHostComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  function root(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function find<T extends Element>(selector: string): T {
    const found = root().querySelector<T>(selector);

    if (found === null) {
      throw new Error(`Expected an element matching "${selector}", but none was rendered.`);
    }

    return found;
  }

  function submit(): void {
    find<HTMLButtonElement>('button.probe-submit').click();
    fixture.detectChanges();
  }

  it('focuses the first invalid control when the submit is rejected', () => {
    const submitButton = find<HTMLButtonElement>('button.probe-submit');

    submitButton.focus();
    expect(document.activeElement).toBe(submitButton);

    submit();

    // The name box is the FIRST invalid control in document order. The select above it is valid, and the
    // description below it is invalid too - so choosing the first is what proves the order matters.
    expect(document.activeElement).toBe(find('input.probe-name'));
  });

  it('leaves focus alone when the form is valid, because the submit is on its way', () => {
    fixture.componentInstance.form.setValue({
      frequency: 'M',
      name: 'Subscribers',
      description: 'Anyone may join',
    });
    fixture.detectChanges();

    const submitButton = find<HTMLButtonElement>('button.probe-submit');

    submitButton.focus();
    submit();

    expect(fixture.componentInstance.submissions).withContext('the submit went through').toBe(1);
    expect(document.activeElement).withContext('nothing interrupted').toBe(submitButton);
  });

  it('never chooses a link, which cannot be invalid and is routinely projected beside a control', () => {
    // `Website/admin/Security/roles.ascx` L5-L16 puts an Edit link and a Delete image button under one
    // label, so a form's first focusable element is frequently not a control at all.
    submit();

    expect(document.activeElement).not.toBe(find('a.probe-link'));
    expect(document.activeElement).toBe(find('input.probe-name'));
  });

  it('never chooses the form itself, which is also marked invalid by Angular', () => {
    // Angular writes its validity classes onto every element carrying a control directive, the `form`
    // included, so a bare `.ng-invalid` query would return the form first and focus nothing useful.
    submit();

    expect(find('form').classList.contains('ng-invalid')).withContext('the trap is real').toBeTrue();
    expect(document.activeElement).not.toBe(find('form'));
  });

  it('does not fight a person who is already in the control it would choose', () => {
    const name = find<HTMLInputElement>('input.probe-name');

    name.focus();
    submit();

    // Focus is where it should be already; re-focusing would re-announce the control to a screen reader
    // for no reason.
    expect(document.activeElement).toBe(name);
  });

  it('skips a control that cannot take focus and chooses the next one that can', () => {
    const name = find<HTMLInputElement>('input.probe-name');

    // A consumer disabling the native element without telling the form model: the control is still
    // `ng-invalid`, and focusing it would silently do nothing.
    name.disabled = true;
    submit();

    expect(document.activeElement).toBe(find('textarea.probe-description'));
  });

  it('re-chooses the next offender once the first is corrected', () => {
    submit();
    expect(document.activeElement).toBe(find('input.probe-name'));

    fixture.componentInstance.form.controls.name.setValue('Subscribers');
    fixture.detectChanges();
    submit();

    expect(document.activeElement).toBe(find('textarea.probe-description'));
  });
});
