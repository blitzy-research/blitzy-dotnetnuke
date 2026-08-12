// INTEGRATION NOTE: this specification and `submit-guard.directive.spec.ts` were written independently
// against the same finding — the guard shipped with no specification of its own. Both were kept because
// each asserts cases the other does not: this file exercises the directive on a host that refuses nothing
// and pins the `ngDoCheck` handover and the foreign-form control, while its sibling pins implicit
// submission, the reset control, per-form independence and the rejected `submit`-swallowing mechanism.
//
// Specification for the synchronous double-submit guard.
//
//  NOTHING here is a ported test: the legacy tree contains zero automated tests of any kind. Every
//  expectation below was derived from what runtime testing measured on the migrated administration forms -
//  pressing a submit control twice with no rendering frame between the two presses posted the form TWICE,
//  which on the portal-alias screen produced two `POST /api/v1/portals/-1/aliases` for one operator action.
//  The first arm answered `409` and the second `201`, so the tenant was saved from a duplicate row by a
//  uniqueness constraint in the database rather than by anything on this side of the wire.
//
//  ⚠ WHY THIS FILE HAD TO EXIST, STATED PLAINLY. The directive shipped with no specification of its own,
//  and two component specifications LOOKED like coverage without being any: `ModuleImportComponent` and
//  `RoleFormComponent` each refuse re-entry through their OWN busy state, so their double-press cases pass
//  with this directive deleted. Every listener, the same-task flag, the cancelled default action, the
//  `ngDoCheck` handover and the destroy cleanup could have been removed with the suite green. Each is
//  asserted here, against the directive itself, on a host that has NO guard of its own.
//
//  ⚠ THE HOST DELIBERATELY COUNTS SUBMITS AND REFUSES NOTHING. `onSubmit` increments and returns. If it
//  held a busy flag, a pending-request signal or a disabled binding, this file would be measuring that flag
//  rather than the directive - which is exactly the mistake that left the guard unprotected.
//
//  ⚠ THE SUBMIT CONTROL WRAPS ITS LABEL IN A `<span>`, because the console's buttons do and because that is
//  where the directive's `closest` lookup earns its keep: a press usually lands on the inner element, so a
//  guard that tested the event target itself would miss the very presses that matter.
//
//  `provideHttpClient()` is registered BEFORE `provideHttpClientTesting()` because the testing function
//  replaces the backend the first one installed. `verify()` in `afterEach` doubles as a positive assertion
//  that this directive performs no network access.
//

import { Component, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { SubmitGuardDirective } from './submit-guard.directive';

/**
 * A host shaped like the migrated forms, and deliberately defenceless.
 *
 * Four press targets, each proving a different part of the directive's selector and containment rules:
 * the real submit control with its label wrapped in a span, a `type="button"` control that cannot submit
 * at all, an `input[type="submit"]` because the selector names both element shapes, and - outside the form
 * entirely - a second submit control belonging to nothing.
 */
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
   * The guard instance the template applied, so a case can drive `ngDoCheck` without a full check.
   *
   * Read from the template rather than injected, because the directive is applied to the `form` element
   * and this class is not that element's injector.
   */
  @ViewChild(SubmitGuardDirective) public guard?: SubmitGuardDirective;

  public readonly form = new FormGroup({
    name: new FormControl('Subscribers', { nonNullable: true, validators: [Validators.required] }),
  });

  /** How many times the form has posted. The only thing this host does. */
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
    /*
     * ⚠ NO `detectChanges()` BETWEEN THE TWO PRESSES, and that is the entire point of the case. The
     * `[disabled]` bindings every submit control in this application already carries reach the DOM only
     * when change detection runs, and change detection runs when the microtask queue drains - which is
     * AFTER the current task finishes. Two presses delivered inside one task therefore both read the
     * pre-disabled DOM, which is the window narrower than a frame that this directive closes and those
     * bindings cannot.
     */
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    glyph.click();

    expect(submits()).withContext('one operator action, one post').toBe(1);
  });

  it('cancels the click\u2019s default action rather than touching the control\u2019s disabled state', () => {
    /*
     * ⚠ THE MECHANISM IS ASSERTED, NOT JUST THE OUTCOME, because two other mechanisms were designed and
     * rejected and neither must be reintroduced. Form submission is the DEFAULT ACTION of a click on a
     * submit control, so `preventDefault()` means no `submit` event is dispatched at all - there is no
     * listener ordering to win and no DOM property to contend over. Angular's own `[disabled]` binding
     * remains the sole owner of the control's disabled state, which is what this asserts: a guard that
     * disabled the control would leave a form that failed validation disabled FOREVER, because a property
     * binding writes only when its value changes.
     */
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
    /*
     * ⚠ RELEASED ON A CHECK RATHER THAN ON A TIMER, and the distinction is load bearing. `ngDoCheck` runs
     * on a directive during every check of its host view and at no other time, so it IS the pass in which
     * Angular writes the submit control's `[disabled]` binding and takes over the wider window. A
     * `setTimeout(0)` was written first and replaced: a timer releases after a DURATION, has to be
     * scheduled outside the Angular zone or the application never stabilises, and is unobservable to a
     * synchronous caller - which broke every specification that presses submit a second time to prove a
     * refusal does not latch.
     */
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    glyph.click();
    expect(submits()).withContext('blocked with no check between').toBe(1);

    fixture.detectChanges();
    glyph.click();

    expect(submits()).withContext('admitted once the handover has happened').toBe(2);
  });

  it('admits a genuine retry after a rejected submit, which is what the timer got wrong', () => {
    /*
     * The behaviour fourteen sibling specifications depend on: a form that refuses a submit, has its entry
     * corrected, and is pressed again must post. The correction re-renders - which is the check that
     * releases the guard - so the second press is admitted. Nothing about the first press latches.
     */
    const glyph = find<HTMLElement>('span.probe-glyph');

    glyph.click();
    expect(submits()).toBe(1);

    fixture.componentInstance.form.controls.name.setValue('Registered Users');
    fixture.detectChanges();

    glyph.click();

    expect(submits()).withContext('a corrected entry can be resubmitted').toBe(2);
  });

  it('releases the guard through ngDoCheck alone, with no other work in between', () => {
    // Asserted directly as well as through `detectChanges()`, so that removing the lifecycle member -
    // rather than breaking the wider check - is also a failure. The directive is reached through the
    // template query because it is applied to the `form` element.
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
    /*
     * ⚠ THE SELECTOR IS NARROW ON PURPOSE. A `type="button"` control cannot post a form, so it was never
     * part of the hazard - and widening the guard to reach it would take away the Cancel an operator may
     * legitimately want while a request is outstanding. The alias screen's own template records that its
     * Cancel and Delete controls "are plain buttons that cannot submit this form".
     */
    const cancel = find<HTMLButtonElement>('button.probe-cancel');

    cancel.click();
    cancel.click();
    cancel.click();

    expect(fixture.componentInstance.cancels).withContext('never guarded').toBe(3);
    expect(submits()).withContext('and it cannot submit either').toBe(0);
  });

  it('ignores a submit control that does not belong to the guarded form', () => {
    // The listener is attached to the form, so a press outside it should not even reach the flag. Asserted
    // because a guard that raised the flag for an unrelated control would silently swallow the NEXT
    // genuine press on the form.
    const outside = find<HTMLButtonElement>('button.probe-outside');

    outside.click();
    expect(submits()).withContext('nothing submitted').toBe(0);

    find<HTMLElement>('span.probe-glyph').click();

    expect(submits()).withContext('the form\u2019s own press is unaffected').toBe(1);
  });

  it('removes its listener when the host is destroyed, so a detached form is not still guarded', () => {
    /*
     * The cleanup is registered through `DestroyRef`, and its absence would leave one listener per mounted
     * form for the life of the application. Observed the only way a removed listener can be observed from
     * outside: the element survives destruction, a cancellable click is dispatched at it, and the CLICK's
     * own cancelled state is read back. A live guard would cancel that click — it has had no check since
     * the press below, so its flag is still raised — while a removed one leaves the event untouched.
     *
     * ⚠ THE FORM'S OWN SUBMISSION IS STOPPED FIRST, and that is not incidental. Destroying the fixture
     * takes Angular's `(submit)` handler with it, and that handler is the only thing preventing the
     * default action of a submit on a form with no `action` — so an unguarded click here would navigate
     * the Karma page and abandon the whole run. A native listener that cancels the SUBMIT leaves the
     * CLICK's cancelled state untouched, which is the signal this case actually reads.
     */
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
