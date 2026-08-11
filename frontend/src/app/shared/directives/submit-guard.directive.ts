import { Directive, DestroyRef, DoCheck, ElementRef, inject } from '@angular/core';

/**
 * Selector matching the submit controls this guard protects.
 *
 * `type="submit"` is the only shape that can post a form by being pressed, so it is the only
 * shape that can post one TWICE. A `type="button"` control cannot submit at all — the alias
 * screen's own template records that its Cancel and Delete controls "are plain buttons that
 * cannot submit this form" — so widening this selector would reach controls that were never
 * part of the hazard, including the Cancel an operator may legitimately want during a slow
 * request.
 */
const SUBMIT_CONTROL_SELECTOR = 'button[type="submit"],input[type="submit"]';

/**
 * SYNCHRONOUS DOUBLE-SUBMIT GUARD FOR EVERY REACTIVE FORM IN THE CONSOLE.
 *
 * THE DEFECT THIS EXISTS TO CLOSE, measured rather than presumed. Pressing a form's submit
 * control twice with no rendering frame between the two presses posted the form TWICE. On the
 * portal-alias screen that produced two `POST /api/v1/portals/-1/aliases` for one operator
 * action; the first arm answered `409` and the second `201`, so the tenant was saved from a
 * duplicate row by a UNIQUENESS CONSTRAINT IN THE DATABASE rather than by anything on this
 * side of the wire. Every endpoint without such a constraint would have taken both writes.
 *
 * ⚠ WHY THE `[disabled]` BINDING THAT ALREADY EXISTS DID NOT STOP IT. Every submit control in
 * this application already binds `[disabled]` to an in-flight signal, and those bindings are
 * correct — but a binding reaches the DOM only when CHANGE DETECTION RUNS, and change
 * detection runs when the microtask queue drains, which is AFTER the current task finishes.
 * Two presses delivered inside one task therefore both read the pre-disabled DOM. The existing
 * bindings close every window WIDER than a frame; this directive closes the one NARROWER than
 * a frame. Neither is redundant and neither alone is sufficient.
 *
 * ⚠ WHY THIS CANCELS THE CLICK'S DEFAULT ACTION RATHER THAN TOUCHING `disabled`. Two other
 * mechanisms were designed, found unsound, and rejected — recorded here so neither is retried:
 *
 *   • DISABLING THE CONTROL from this directive fights the binding it is meant to complement.
 *     The flag has to be released once Angular can take over, which is a macrotask away; but
 *     by then the change detection pass for a request that IS in flight has already written
 *     `disabled = true`, so releasing would overrule it and reopen the wider window. Declining
 *     to release is worse: a property binding writes only when its VALUE CHANGES, so a submit
 *     that failed validation and never dispatched a request leaves the bound value unchanged,
 *     nothing rewrites the property, and the control stays disabled FOREVER.
 *
 *   • SWALLOWING THE SECOND `submit` EVENT with `stopImmediatePropagation` depends on winning
 *     a race that nothing here can settle: at the event's own target the DOM invokes listeners
 *     in REGISTRATION order and the `capture` flag carries no precedence there, so whether
 *     this directive's listener runs before Angular's own `(ngSubmit)` listener would follow
 *     from instantiation order rather than from anything stated in this file.
 *
 * Cancelling the click has neither problem. Form submission is the DEFAULT ACTION of a click
 * on a submit control, and a default action is performed only after the click event has
 * finished propagating — so `preventDefault()` on the click means NO `submit` event is ever
 * dispatched, and there is no listener ordering to win and no DOM property to contend over.
 * Angular's `[disabled]` binding remains the sole owner of the control's disabled state.
 *
 * ⚠ WHY THE FLAG IS RELEASED IN `ngDoCheck` AND NOT ON A TIMER. The guard's job ends at one
 * precise moment: the first change detection pass after this submit, because that is the pass
 * in which Angular writes the `[disabled]` binding and takes over the wider window. `ngDoCheck`
 * IS that moment — it runs on a directive during every check of its host view and at no other
 * time — so the handover is expressed directly rather than approximated.
 *
 * A `setTimeout(0)` was written first and replaced, and the reason is worth keeping. A timer
 * releases after a DURATION, and a duration is only a guess at when rendering happened; it also
 * has to be scheduled outside the Angular zone, because a pending zone timer keeps the
 * application permanently unstable and everything awaiting stability then waits for it. Worse,
 * a timer is unobservable to a synchronous caller: fourteen specs that press submit a second
 * time to prove a REFUSAL DOES NOT LATCH — that a corrected entry can be retried — failed
 * against the timer, because no macrotask elapses between two synchronous presses. Those specs
 * assert a real and required behaviour, so the guard was wrong to reject them, not the specs.
 * Releasing on the check that each of them already performs admits the legitimate retry while
 * still refusing two presses with NO check between them, which is exactly the hazard.
 *
 * Releasing the flag also means a form whose request has already finished is immediately
 * pressable again, so a genuine second save is never blocked; the in-flight `[disabled]`
 * binding remains the authority on whether a request is still outstanding.
 */
@Directive({
  selector: 'form[formGroup]',
  standalone: true,
})
export class SubmitGuardDirective implements DoCheck {
  private readonly host = inject<ElementRef<HTMLFormElement>>(ElementRef);

  /**
   * Whether a submit has already been dispatched in the current task.
   *
   * A plain field rather than a signal, deliberately. It is read and written inside one
   * synchronous event dispatch and must never participate in change detection: a signal would
   * schedule work here and could not be observed by the second press any sooner.
   */
  private dispatching = false;

  constructor() {
    const form: HTMLFormElement = this.host.nativeElement;

    const onClick = (event: Event): void => {
      const target = event.target;

      if (!(target instanceof Element)) {
        return;
      }

      // `closest` rather than a direct match: the press usually lands on a `<span>` inside the
      // control — the console's buttons wrap their glyphs — so testing the target itself would
      // miss the very presses that matter.
      const control: Element | null = target.closest(SUBMIT_CONTROL_SELECTOR);

      if (control === null || !form.contains(control)) {
        return;
      }

      if (this.dispatching) {
        // Cancels the default action, so no `submit` event is dispatched and the component's
        // handler is never re-entered. Nothing else about the click is altered.
        event.preventDefault();

        return;
      }

      this.dispatching = true;
    };

    // Capture phase so the flag is read and set as early in the dispatch as this directive can
    // act, and before any handler on the control itself can run.
    form.addEventListener('click', onClick, { capture: true });

    inject(DestroyRef).onDestroy(() => {
      form.removeEventListener('click', onClick, { capture: true });
    });
  }

  /**
   * Releases the guard at the handover point.
   *
   * Runs only when the host view is checked, which is the pass that applies the submit
   * control's own `[disabled]` binding. Two presses inside one task see no check between them
   * and the second stays blocked; a press, a check, and a later press — every real retry — is
   * admitted.
   */
  ngDoCheck(): void {
    this.dispatching = false;
  }
}
