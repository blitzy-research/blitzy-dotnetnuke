/**
 * ⚠ THE ONLY PRODUCTION DIRECTIVE IN THE WORKSPACE THAT HAD NO SPECIFICATION OF ITS OWN, AND THE
 * ONE WHOSE BEHAVIOUR IS HARDEST TO INFER FROM ITS CONSUMERS. Its effects were exercised INDIRECTLY,
 * through the fourteen feature specifications that press a submit control twice to prove a refusal does
 * not latch. That coverage is real but it is one-sided: it pins the behaviour the directive must NOT
 * break and says nothing about the behaviour it exists for.
 */
import { Component, DebugElement, ViewChild } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { SubmitGuardDirective } from './submit-guard.directive';

/** A host carrying every control shape the guard has to distinguish. */
@Component({
  standalone: true,
  imports: [ReactiveFormsModule, SubmitGuardDirective],
  template: `
    <form id="first" [formGroup]="form" (ngSubmit)="onSubmit()">
      <input id="text" type="text" formControlName="name" />
      <button id="submit-button" type="submit"><span id="submit-glyph">Save</span></button>
      <input id="submit-input" type="submit" value="Save too" />
      <button id="plain-button" type="button" (click)="onCancel()">Cancel</button>
      <input id="reset-input" type="reset" value="Reset" />
    </form>
    <form id="second" [formGroup]="other" (ngSubmit)="onOtherSubmit()">
      <button id="other-submit" type="submit">Save the other</button>
    </form>
  `,
})
class GuardHostComponent {
  /** The group the directive's selector requires. */
  readonly form = new FormGroup({
    name: new FormControl<string>('', { nonNullable: true }),
  });

  /** A second, entirely separate form, so the guard's scope can be asserted. */
  readonly other = new FormGroup({
    note: new FormControl<string>('', { nonNullable: true }),
  });

  /** Every submission the first form dispatched, counted. */
  submissions = 0;

  /** Every submission the second form dispatched, counted. */
  otherSubmissions = 0;

  /** Every press of the non-submitting control, counted. */
  cancellations = 0;

  /** Held so a case can drive the directive's own lifecycle hook directly. */
  @ViewChild(SubmitGuardDirective, { static: true })
  guard?: SubmitGuardDirective;

  onSubmit(): void {
    this.submissions += 1;
  }

  onOtherSubmit(): void {
    this.otherSubmissions += 1;
  }

  onCancel(): void {
    this.cancellations += 1;
  }
}

describe('SubmitGuardDirective', () => {
  let fixture: ComponentFixture<GuardHostComponent>;
  let component: GuardHostComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GuardHostComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(GuardHostComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  /** The host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * One element of the host, narrowed by a real check.
   *
   * @param selector The selector to find.
   * @returns The element.
   */
  function element<E extends HTMLElement>(selector: string): E {
    const found: E | null = host().querySelector<E>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}"`);
    }

    return found;
  }

  /**
   * Presses a control the way a pointer does, and reports whether the default action survived. ⚠ THE
   * RETURN VALUE IS THE WHOLE POINT OF DISPATCHING BY HAND rather than calling `.click()`.
   *
   * @param selector The control to press.
   * @returns True when the default action was NOT cancelled.
   */
  function press(selector: string): boolean {
    return element(selector).dispatchEvent(
      new MouseEvent('click', { bubbles: true, cancelable: true }),
    );
  }

  /**
   * Raises a `submit` event on the first form directly. ⚠ NOT A STAND-IN FOR A PRESS, AND THE DISTINCTION
   * IS ONE OF THE THINGS UNDER TEST. A press's default action already submits — the browser performs it
   * for a dispatched click just as it does for a real one — so this is used only where a case needs to
   * raise the `submit` event WITHOUT a click, which is how it proves the guard intercepts clicks and
   * never the submit event itself.
   */
  function raiseSubmitDirectly(): void {
    element<HTMLFormElement>('#first').dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
  }

  describe('construction', () => {
    it('attaches to a reactive form without being named in its template', () => {
      const directive: DebugElement | null = fixture.debugElement.query(
        By.directive(SubmitGuardDirective),
      );

      expect(directive).withContext('the guard instantiated on the form element').not.toBeNull();
      expect(component.guard).withContext('and the host can reach it').toBeDefined();
    });
  });

  describe('the first press of a task', () => {
    it('is allowed through, and the form submits', () => {
      const survived: boolean = press('#submit-button');

      expect(survived)
        .withContext('the default action is untouched, so the browser submits')
        .toBeTrue();

      fixture.detectChanges();

      expect(component.submissions).toBe(1);
    });

    it('is allowed through when the press lands on a CHILD of the control', () => {
      const survived: boolean = press('#submit-glyph');

      expect(survived).toBeTrue();
      fixture.detectChanges();

      expect(component.submissions).toBe(1);
    });

    it('is allowed through on a submit INPUT as well as a submit button', () => {
      // Both shapes can post a form by being pressed, so both are in the hazard and both are in
      // the selector.
      expect(press('#submit-input')).toBeTrue();
      fixture.detectChanges();

      expect(component.submissions).toBe(1);
    });
  });

  describe('a second press inside the SAME task', () => {
    it('is refused, and no second submission is dispatched', () => {
      // ⚠ THE MEASURED DEFECT. Two presses with no rendering frame between them posted the form twice: on
      // the portal-alias screen that produced two creations for one operator action, and the tenant was
      // saved from a duplicate row by a database constraint rather than by anything on this side of the
      // wire.
      expect(press('#submit-button')).withContext('the first press is allowed').toBeTrue();
      expect(component.submissions)
        .withContext('and its default action submitted the form')
        .toBe(1);

      expect(press('#submit-button'))
        .withContext('the second press is refused by cancelling its default action')
        .toBeFalse();

      expect(component.submissions)
        .withContext('and no second submission reaches the component')
        .toBe(1);
    });

    it('is refused however many times it is repeated', () => {
      expect(press('#submit-button')).toBeTrue();

      for (let attempt = 0; attempt < 5; attempt += 1) {
        expect(press('#submit-button'))
          .withContext(`repeat press ${attempt + 1} is refused`)
          .toBeFalse();
      }
    });

    it('is refused across the two submit shapes, because the form is what is guarded', () => {
      // The guard protects a FORM, not a control, so pressing one submit control and then the other
      // is still two submissions of one form.
      expect(press('#submit-button')).toBeTrue();
      expect(press('#submit-input'))
        .withContext('a different submit control does not reset the guard')
        .toBeFalse();
    });

    it('refuses by cancelling the click and NOT by disabling the control', () => {
      const control = element<HTMLButtonElement>('#submit-button');

      expect(control.disabled).withContext('not disabled before').toBeFalse();

      press('#submit-button');
      press('#submit-button');

      expect(control.disabled)
        .withContext('and not disabled after: the guard never touches the property')
        .toBeFalse();
      expect(control.hasAttribute('disabled'))
        .withContext('nor the attribute')
        .toBeFalse();
    });

    it('refuses without swallowing the submit event, which is the other rejected mechanism', () => {
      // The second alternative the directive's notes reject: `stopImmediatePropagation` on the `submit`
      // event depends on winning a listener-registration race this file cannot settle.
      press('#submit-button');
      expect(press('#submit-button')).toBeFalse();

      expect(component.submissions).withContext('one submission so far').toBe(1);

      raiseSubmitDirectly();

      expect(component.submissions)
        .withContext('a submit event raised without a click still reaches the component')
        .toBe(2);
    });
  });

  describe('the handover to change detection', () => {
    it('admits a later press once the host view has been checked', () => {
      // ⚠ THE BEHAVIOUR FOURTEEN FEATURE SPECIFICATIONS DEPEND ON, asserted here against the directive
      // itself rather than as a side-effect of a feature. Those cases press submit, see a refusal reported,
      // correct the entry and press again — a real retry, which must not latch.
      expect(press('#submit-button')).toBeTrue();
      expect(press('#submit-button')).withContext('still the same task').toBeFalse();

      fixture.detectChanges();

      expect(press('#submit-button'))
        .withContext('a check is the handover point, so the retry is admitted')
        .toBeTrue();
      fixture.detectChanges();

      expect(component.submissions)
        .withContext('two presses, two checks, two submissions')
        .toBe(2);
    });

    it('releases on the directive lifecycle hook, and on no timer', () => {
      // The release is expressed as `ngDoCheck` precisely because it names the moment Angular writes the
      // `[disabled]` binding and takes over the wider window.
      expect(press('#submit-button')).toBeTrue();
      expect(press('#submit-button')).toBeFalse();

      component.guard?.ngDoCheck();

      expect(press('#submit-button'))
        .withContext('the hook alone releases the guard')
        .toBeTrue();
    });

    it('is released for every form independently, so one form never blocks another', () => {
      expect(press('#submit-button')).withContext('the first form submits').toBeTrue();
      expect(press('#submit-button')).withContext('and refuses its own second press').toBeFalse();

      expect(press('#other-submit'))
        .withContext('the second form is untouched by the first form\u2019s guard')
        .toBeTrue();
      expect(component.otherSubmissions).toBe(1);

      // And the second form's own guard is in force, which proves it has one rather than none.
      expect(press('#other-submit'))
        .withContext('the second form refuses its own second press too')
        .toBeFalse();
      expect(component.otherSubmissions).toBe(1);
    });
  });

  describe('what the guard leaves alone', () => {
    it('never intercepts a control that cannot submit', () => {
      press('#submit-button');

      expect(press('#plain-button'))
        .withContext('the cancel is never cancelled')
        .toBeTrue();
      expect(component.cancellations).toBe(1);

      // And pressing it a second time is equally untouched.
      expect(press('#plain-button')).toBeTrue();
      expect(component.cancellations).toBe(2);
    });

    it('never intercepts a reset control', () => {
      press('#submit-button');

      expect(press('#reset-input'))
        .withContext('a reset posts nothing, so it is outside the hazard')
        .toBeTrue();
    });

    it('never intercepts a press on the form background', () => {
      press('#submit-button');

      expect(
        element<HTMLFormElement>('#first').dispatchEvent(
          new MouseEvent('click', { bubbles: true, cancelable: true }),
        ),
      )
        .withContext('a click resolving to no submit control is not the guard\u2019s business')
        .toBeTrue();
    });

    it('does not govern implicit submission, which dispatches no click', () => {
      element<HTMLInputElement>('#text').dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
      );

      raiseSubmitDirectly();

      expect(component.submissions).withContext('the implicit submission is served').toBe(1);

      // And it has not consumed the guard either: the next pointer press is a first press.
      expect(press('#submit-button')).toBeTrue();
    });
  });

  describe('teardown', () => {
    it('removes its listener when the host is destroyed', () => {
      // The listener is added imperatively in the constructor, so its removal is not automatic.
      const form = element<HTMLFormElement>('#first');

      press('#submit-button');
      expect(press('#submit-button')).withContext('the guard is active while mounted').toBeFalse();

      fixture.destroy();

      // ⚠ THE FORM'S OWN SUBMIT HANDLING GOES WITH THE COMPONENT, so a press after destruction would
      // perform the browser's REAL default action and reload the test page — which fails the whole run for
      // a reason that has nothing to do with the subject.
      const swallowSubmit = (event: Event): void => {
        event.preventDefault();
      };

      form.addEventListener('submit', swallowSubmit);

      try {
        // The element survives destruction in this fixture, so it can still be pressed — and with the
        // guard's listener gone, the refusal is gone with it.
        expect(
          form.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true })),
        )
          .withContext('a background press is unaffected, as it always was')
          .toBeTrue();

        const control = form.querySelector<HTMLButtonElement>('#submit-button');

        expect(control).not.toBeNull();

        // TWO presses, because one proves nothing: the guard refuses only the SECOND press of a task,
        // so a single press would have been allowed through even with the listener still attached.
        control?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

        expect(
          control?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true })),
        )
          .withContext('no listener remains to cancel the second press either')
          .toBeTrue();
      } finally {
        form.removeEventListener('submit', swallowSubmit);
      }
    });
  });
});
