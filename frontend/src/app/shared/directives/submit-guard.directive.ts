import { Directive, DestroyRef, DoCheck, ElementRef, inject } from '@angular/core';

/** Selector matching the submit controls this guard protects. */
const SUBMIT_CONTROL_SELECTOR = 'button[type="submit"],input[type="submit"]';

/**
 * SYNCHRONOUS DOUBLE-SUBMIT GUARD FOR EVERY REACTIVE FORM IN THE CONSOLE. THE DEFECT THIS EXISTS TO
 * CLOSE, measured rather than presumed.
 */
@Directive({
  selector: 'form[formGroup]',
  standalone: true,
})
export class SubmitGuardDirective implements DoCheck {
  private readonly host = inject<ElementRef<HTMLFormElement>>(ElementRef);

  /** Whether a submit has already been dispatched in the current task. */
  private dispatching = false;

  constructor() {
    const form: HTMLFormElement = this.host.nativeElement;

    const onClick = (event: Event): void => {
      const target = event.target;

      if (!(target instanceof Element)) {
        return;
      }

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

  /** Releases the guard at the handover point. */
  ngDoCheck(): void {
    this.dispatching = false;
  }
}
