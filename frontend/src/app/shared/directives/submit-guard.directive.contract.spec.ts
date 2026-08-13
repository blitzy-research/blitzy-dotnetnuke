import { Component, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { SubmitGuardDirective } from './submit-guard.directive';

/** A host shaped like the migrated forms, and deliberately defenceless. */
@Component({
  selector: 'app-submit-guard-host',
  standalone: true,
  imports: [ReactiveFormsModule, SubmitGuardDirective],
  template: `
    <form [formGroup]="form" (ngSubmit)="onSubmit()" novalidate>
      <input class="probe-name" type="text" formControlName="name" />
      <button class="probe-submit" type="submit"><span class="probe-glyph">Update</span></button>
      <input class="probe-submit-input" type="submit" value="Save" />
      <button class="probe-cancel" type="button" (click)="cancels = cancels + 1">Cancel</button>
    </form>
    <button class="probe-outside" type="submit">Outside</button>
  `,
})
class SubmitGuardHostComponent {
  /**
   * The guard instance the template applied, so a case can drive `ngDoCheck` without a full check. Read
   * from the template rather than injected, because the directive is applied to the `form` element and
   * this class is not that element's injector.
   */
  @ViewChild(SubmitGuardDirective) public guard?: SubmitGuardDirective;

  public readonly form = new FormGroup({
    name: new FormControl('Subscribers', { nonNullable: true, validators: [Validators.required] }),
  });

  /** How many times the form has posted. */
  public submits = 0;

  /** How many times the plain button was pressed, so its exemption is observable. */
  public cancels = 0;

  public onSubmit(): void {
    this.submits += 1;
  }
}

describe('SubmitGuardDirective', () => {
  let httpMock: HttpTestingController;
  let fixture: ComponentFixture<SubmitGuardHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SubmitGuardHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SubmitGuardHostComponent);
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

  /** How many times the host has posted. */
  function submits(): number {
    return fixture.componentInstance.submits;
  }

  it('lets a single press through, so the guard costs an ordinary submit nothing', () => {
    find<HTMLElement>('span.probe-glyph').click();

    expect(submits()).toBe(1);
  });

  it('refuses a SECOND press delivered in the same task, which is the whole defect', () => {
    // ⚠ NO `detectChanges()` BETWEEN THE TWO PRESSES, and that is the entire point of the case.
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    glyph.click();

    expect(submits()).withContext('one operator action, one post').toBe(1);
  });

  it('cancels the click\u2019s default action rather than touching the control\u2019s disabled state', () => {
    // ⚠ THE MECHANISM IS ASSERTED, NOT JUST THE OUTCOME, because two other mechanisms were designed and
    // rejected and neither must be reintroduced.
    const control = find<HTMLButtonElement>('button.probe-submit');

    control.click();

    const second = new MouseEvent('click', { bubbles: true, cancelable: true });
    const notCancelled: boolean = control.dispatchEvent(second);

    expect(notCancelled).withContext('the default action was cancelled').toBeFalse();
    expect(second.defaultPrevented).toBeTrue();
    expect(control.disabled).withContext('the directive never writes this property').toBeFalse();
    expect(submits()).toBe(1);
  });

  it('reads the press through the nearest submit control, not the element that was hit', () => {
    // The console's buttons wrap their glyphs, so the press lands on an inner `<span>`. A guard that
    // matched the event target itself would let every real press past and would have closed nothing.
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    glyph.click();
    glyph.click();

    expect(submits()).toBe(1);
  });

  it('guards an input[type="submit"] as well as a button, because the selector names both', () => {
    const input = find<HTMLInputElement>('input.probe-submit-input');

    input.click();
    input.click();

    expect(submits()).toBe(1);
  });

  it('releases the guard on the first check of the host view, which is the handover point', () => {
    // ⚠ RELEASED ON A CHECK RATHER THAN ON A TIMER, and the distinction is load bearing. `ngDoCheck` runs
    // on a directive during every check of its host view and at no other time, so it IS the pass in which
    // Angular writes the submit control's `[disabled]` binding and takes over the wider window.
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    glyph.click();
    expect(submits()).withContext('blocked with no check between').toBe(1);

    fixture.detectChanges();
    glyph.click();

    expect(submits()).withContext('admitted once the handover has happened').toBe(2);
  });

  it('admits a genuine retry after a rejected submit, which is what the timer got wrong', () => {
    // The behaviour fourteen sibling specifications depend on: a form that refuses a submit, has its entry
    // corrected, and is pressed again must post. The correction re-renders - which is the check that
    // releases the guard - so the second press is admitted.
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    expect(submits()).toBe(1);

    fixture.componentInstance.form.controls.name.setValue('Registered Users');
    fixture.detectChanges();

    glyph.click();

    expect(submits()).withContext('a corrected entry can be resubmitted').toBe(2);
  });

  it('releases the guard through ngDoCheck alone, with no other work in between', () => {
    const glyph = find<HTMLElement>('span.probe-glyph');
    const guard: SubmitGuardDirective | undefined = fixture.componentInstance.guard;

    expect(guard).withContext('the directive matched the form').not.toBeUndefined();

    glyph.click();
    glyph.click();
    expect(submits()).toBe(1);

    guard?.ngDoCheck();
    glyph.click();

    expect(submits()).toBe(2);
  });

  it('ignores a control that cannot submit, so Cancel stays pressable during a slow request', () => {
    const cancel = find<HTMLButtonElement>('button.probe-cancel');

    cancel.click();
    cancel.click();
    cancel.click();

    expect(fixture.componentInstance.cancels).withContext('never guarded').toBe(3);
    expect(submits()).withContext('and it cannot submit either').toBe(0);
  });

  it('ignores a submit control that does not belong to the guarded form', () => {
    const outside = find<HTMLButtonElement>('button.probe-outside');

    outside.click();
    expect(submits()).withContext('nothing submitted').toBe(0);

    find<HTMLElement>('span.probe-glyph').click();

    expect(submits()).withContext('the form\u2019s own press is unaffected').toBe(1);
  });

  it('removes its listener when the host is destroyed, so a detached form is not still guarded', () => {
    // ⚠ THE FORM'S OWN SUBMISSION IS STOPPED FIRST, and that is not incidental.
    const form = find<HTMLFormElement>('form');
    const control = find<HTMLButtonElement>('button.probe-submit');

    control.click();
    expect(submits()).toBe(1);

    fixture.destroy();

    form.addEventListener('submit', (event: Event) => event.preventDefault());

    const afterDestroy = new MouseEvent('click', { bubbles: true, cancelable: true });

    control.dispatchEvent(afterDestroy);

    expect(afterDestroy.defaultPrevented)
      .withContext('no guard listener remains to cancel it')
      .toBeFalse();
  });
});
