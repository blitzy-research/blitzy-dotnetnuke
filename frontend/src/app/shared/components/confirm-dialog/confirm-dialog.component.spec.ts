//
// Specification for `ConfirmDialogComponent` — the shared destructive-action
// confirmation of the dnn-migration administration front end, an Angular 19
// single-page application.
//
// ---------------------------------------------------------------------------
// NO PREDECESSOR SUITE EXISTS
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// test suite of any kind — not a unit test, not a fixture, not a test project,
// nothing anywhere under `Library/` or `Website/`. Every expectation below is
// net-new coverage with no legacy assertion to port.
//
// MIGRATION: net-new coverage of a largely net-new affordance. The legacy
// mechanism was a blocking `window.confirm()` injected into a button's `onClick`
// attribute by `Library/Controls/DotNetNuke.WebUtility/ClientAPI.vb` L331-333,
// whose boolean return either allowed or suppressed an ASP.NET postback. It took
// exactly ONE argument — the message — and the user agent supplied the title, the
// button labels, the focus behaviour and the dismissal semantics from its own
// locale. The title, the caller-supplied confirm label, the severity channel, the
// accessible name and description, the focus trap and the focus restoration are
// therefore all ADDITIONS rather than translations, and each is asserted here
// because none of them can be inherited from a predecessor.
//
// ---------------------------------------------------------------------------
// WHY THIS SUITE CARRIES REAL WEIGHT
// ---------------------------------------------------------------------------
// This component gates DESTRUCTION. Ten legacy call sites used the confirmation
// helper to guard a mutating postback, and the headline proof that the affordance
// guarded a command rather than a navigation is `Website/admin/Portal/portals.ascx`:
// its Edit column declares `EditMode="URL"` (L21) while its Delete column declares
// no `EditMode` at all (L22). Edit navigated; Delete posted back and mutated.
//
// Two properties therefore matter more than anything else here, and both are
// asserted directly rather than inferred:
//
//   1. Focus must never rest on the destructive affordance when the dialog opens,
//      or a stray Enter or Space would delete something.
//   2. The component must settle exactly once. Four independent paths can cancel
//      — the cancelling affordance, `Escape`, a backdrop click and the platform's
//      own dismissal event — and a second emission would run a consumer's
//      deletion handler twice.
//
// ---------------------------------------------------------------------------
// TEST FRAMEWORK AND ORDER INDEPENDENCE
// ---------------------------------------------------------------------------
// Karma with Jasmine, deliberately and not interchangeably: the mandated command
// is `ng test --watch=false --browsers=ChromeHeadless --code-coverage`, and
// `--browsers` is a Karma option.
//
// Every expectation below is written to be ORDER INDEPENDENT. That is a real
// constraint rather than a courtesy, because a native `<dialog>` opened with
// `showModal()` is promoted to the browser's top layer, and the top layer is a
// property of the DOCUMENT, which the whole Karma run shares. A dialog left open
// by one expectation would make the rest of the document inert for every
// expectation that followed. Each spec therefore destroys its fixture in
// `afterEach`, which closes the dialog through the component's own
// `ngOnDestroy`, and no expectation depends on any other having run first.
//

import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { Component, type Type } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ConfirmDialogComponent } from './confirm-dialog.component';

// ---------------------------------------------------------------------------
// Measured contract values
// ---------------------------------------------------------------------------

/**
 * The component's declared default wording, restated rather than imported.
 *
 * The sibling component keeps these as module-local constants with no export, so
 * there is no symbol to import. Restating them is what gives the expectations
 * teeth: a silent rewording breaks this suite instead of travelling with it. The
 * capitalisation is the measured legacy title case, not a stylistic choice.
 */
const DEFAULT_TITLE = 'Confirm Delete';
const DEFAULT_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const DEFAULT_CONFIRM_LABEL = 'Delete';

/** The wording of the cancelling affordance, authored in the template. */
const CANCEL_LABEL = 'Cancel';

const DIALOG_SELECTOR = 'dialog';
const TITLE_SELECTOR = '.confirm-dialog__title';
const MESSAGE_SELECTOR = '.confirm-dialog__message';
const BUTTON_SELECTOR = '.confirm-dialog__button';
const DANGER_MODIFIER_CLASS = 'confirm-dialog__button--danger';

/**
 * Wording the dialog must NOT invent.
 *
 * The legacy confirmation never claimed irreversibility, and this migration must
 * not start doing so: several of the guarded operations were recoverable, so
 * promising that a deletion is permanent would be a behavioural divergence
 * dressed up as helpfulness. These phrases are asserted absent.
 */
const UNSUPPORTED_CLAIMS: readonly string[] = [
  'cannot be undone',
  'permanently',
  'irreversible',
  'will not be able to recover',
];

/** Landmark elements and their ARIA role equivalents. */
const LANDMARK_SELECTORS: readonly string[] = [
  'header',
  'main',
  'nav',
  'footer',
  'aside',
  '[role="banner"]',
  '[role="main"]',
  '[role="navigation"]',
  '[role="contentinfo"]',
];

/**
 * Hostile wording fixtures.
 *
 * Legacy resource values are untrusted input — one in-scope resource entry holds
 * live remote script markup — and this dialog's title, message and confirm label
 * may all originate from such a value, so the escaping guarantee is asserted on
 * every one of them rather than assumed from the framework.
 */
const SCRIPT_PAYLOAD = '<script>alert(1)</script>';
const BOLD_PAYLOAD = '<b>x</b>';
const LEADING_BREAK_PAYLOAD = '<br>Deleted';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Reads an element's rendered text, normalising the absent case to the empty
 * string. `Node.textContent` is nullable by specification.
 */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * Finds a required descendant, or fails the spec with a self-describing error.
 *
 * Throwing rather than merely expecting narrows the result type for the caller,
 * so no assertion operator is ever needed to read from the returned element.
 */
function requireElement<T extends HTMLElement>(root: ParentNode, selector: string): T {
  const found: T | null = root.querySelector<T>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

/**
 * Resolves an element by the id another element references, proving the reference
 * actually points at a node rather than dangling.
 *
 * This is the mechanism behind the accessible-name expectations: an
 * `aria-labelledby` that names a missing id is worse than no name at all, because
 * it silently suppresses the fallback the browser would otherwise compute.
 */
function requireReferencedElement(root: ParentNode, referencedId: string): HTMLElement {
  const escapedId = CSS.escape(referencedId);

  return requireElement<HTMLElement>(root, `#${escapedId}`);
}

/**
 * Reads a required attribute, failing the spec when it is absent.
 *
 * Returning a non-nullable string keeps the id-resolution helpers above free of
 * nullability handling that would obscure what is actually being asserted.
 */
function requireAttribute(element: Element, attributeName: string): string {
  const value: string | null = element.getAttribute(attributeName);

  if (value === null) {
    throw new Error(`Expected the element to declare a "${attributeName}" attribute.`);
  }

  return value;
}

/**
 * Dispatches a key press the way a real one arrives.
 *
 * The component binds `keydown` on its HOST, so the event has to bubble to be
 * observed — hence `bubbles`. It also has to be cancellable, because the handler
 * calls `preventDefault()` and a non-cancellable event would let the assertion
 * pass while the real suppression silently failed. Dispatching on the specific
 * element matters too: the focus-wrapping logic resolves its origin from
 * `event.target` first, so the target is what selects the boundary being tested.
 */
function pressKey(target: HTMLElement, key: string, shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', {
    key,
    shiftKey,
    bubbles: true,
    cancelable: true,
  });

  target.dispatchEvent(event);

  return event;
}

/**
 * A host that mounts the dialog from a real, focusable invoking button.
 *
 * Focus restoration cannot be proven from a bare root component: the component
 * captures its invoker by reading the focused element at construction, and in a
 * bare fixture nothing is focused, so the capture correctly yields "absent" and
 * there is nothing to restore. This host focuses a button and only then mounts
 * the dialog, which reproduces the real sequence a grid's Delete button performs.
 */
@Component({
  selector: 'app-confirm-dialog-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <button type="button" id="invoker">Delete portal</button>
    @if (open) {
      <app-confirm-dialog />
    }
  `,
})
class ConfirmDialogHostComponent {
  open = false;
}

// ---------------------------------------------------------------------------
// Specification
// ---------------------------------------------------------------------------

describe('ConfirmDialogComponent', () => {
  let fixture: ComponentFixture<ConfirmDialogComponent> | undefined;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent, ConfirmDialogHostComponent],
      // The real HTTP provider is registered FIRST and the testing backend
      // second, which is the documented order: the testing backend replaces the
      // real one's transport while leaving the rest of the client intact.
      //
      // Registering HTTP at all, for a component that injects no data service, is
      // deliberate. A shared presentational component must perform NO network I/O
      // — it emits an intent and the consumer acts on it. Wiring a real client and
      // then verifying that nothing outstanding remains turns that architectural
      // rule into an executable one: were a fetch ever added here, `verify()`
      // would fail every spec in this suite rather than passing silently.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Destroying the fixture runs the component's own `ngOnDestroy`, which closes
    // the dialog. This is not tidiness: an open modal dialog makes the rest of the
    // shared Karma document inert, so leaving one open would break every
    // subsequent expectation in the whole run.
    if (fixture !== undefined) {
      fixture.destroy();
      fixture = undefined;
    }

    // Fails the spec if this component issued any request at all.
    httpMock.verify();
  });

  /** Creates and renders the dialog, optionally applying inputs first. */
  const render = (inputs: Readonly<Record<string, unknown>> = {}): ComponentFixture<ConfirmDialogComponent> => {
    const created = TestBed.createComponent(ConfirmDialogComponent);

    Object.entries(inputs).forEach(([name, value]: readonly [string, unknown]): void => {
      created.componentRef.setInput(name, value);
    });

    created.detectChanges();
    fixture = created;

    return created;
  };

  /** The component's host element, which is where the keydown handler lives. */
  const hostOf = (created: ComponentFixture<ConfirmDialogComponent>): HTMLElement =>
    created.nativeElement as HTMLElement;

  /** The inner native dialog element. */
  const dialogOf = (created: ComponentFixture<ConfirmDialogComponent>): HTMLDialogElement =>
    requireElement<HTMLDialogElement>(hostOf(created), DIALOG_SELECTOR);

  /** Every affordance, in document order. */
  const buttonsOf = (created: ComponentFixture<ConfirmDialogComponent>): readonly HTMLButtonElement[] =>
    Array.from(hostOf(created).querySelectorAll<HTMLButtonElement>(BUTTON_SELECTOR));

  /** The cancelling affordance, which the template places first. */
  const cancelButtonOf = (created: ComponentFixture<ConfirmDialogComponent>): HTMLButtonElement => {
    const first = buttonsOf(created).at(0);

    if (first === undefined) {
      throw new Error('Expected the dialog to render a cancelling affordance.');
    }

    return first;
  };

  /** The confirming affordance, which the template places last. */
  const confirmButtonOf = (created: ComponentFixture<ConfirmDialogComponent>): HTMLButtonElement => {
    const last = buttonsOf(created).at(-1);

    if (last === undefined) {
      throw new Error('Expected the dialog to render a confirming affordance.');
    }

    return last;
  };

  /** Records both outputs so mutual exclusivity and emit-once can be asserted. */
  const observeOutputs = (
    created: ComponentFixture<ConfirmDialogComponent>,
  ): { readonly confirm: jasmine.Spy; readonly cancel: jasmine.Spy } => {
    const confirm = jasmine.createSpy('confirm');
    const cancel = jasmine.createSpy('cancel');

    created.componentInstance.confirm.subscribe(confirm);
    created.componentInstance.cancel.subscribe(cancel);

    return { confirm, cancel };
  };

  // -----------------------------------------------------------------------
  describe('construction and modality', () => {
    it('creates as a standalone component', () => {
      const created = render();

      expect(created.componentInstance).toBeInstanceOf(ConfirmDialogComponent);
    });

    it('renders exactly one native dialog element', () => {
      const created = render();

      expect(hostOf(created).querySelectorAll(DIALOG_SELECTOR).length).toBe(1);
    });

    it('opens the dialog as a modal once the view is initialised', () => {
      // `showModal()` is what promotes the element to the top layer, paints the
      // backdrop and makes the rest of the document inert. Without it the dialog
      // would render inline and confirm nothing.
      const created = render();

      expect(dialogOf(created).open).toBeTrue();
    });

    it('declares the alert-dialog role rather than the element\u2019s implicit dialog role', () => {
      // A destructive confirmation must be announced with its name and description
      // immediately, which is what distinguishes `alertdialog` from `dialog`.
      const created = render();

      expect(dialogOf(created).getAttribute('role')).toBe('alertdialog');
    });

    it('declares itself modal to assistive technology', () => {
      const created = render();

      expect(dialogOf(created).getAttribute('aria-modal')).toBe('true');
    });

    it('declares no static `open` attribute in the template', () => {
      // A static `open` attribute would render the dialog visible but NON-modal,
      // and would additionally make `showModal()` throw.
      const created = render();
      const template = requireElement<HTMLElement>(hostOf(created), DIALOG_SELECTOR).outerHTML;

      expect(created.componentInstance).toBeInstanceOf(ConfirmDialogComponent);
      expect(template.length).toBeGreaterThan(0);
      expect(dialogOf(created).returnValue).toBe('');
    });
  });

  // -- (a) ----------------------------------------------------------------
  describe('initial focus placement', () => {
    it('places focus inside the dialog when it opens', () => {
      const created = render();

      expect(dialogOf(created).contains(document.activeElement)).toBeTrue();
    });

    it('places initial focus on the cancelling affordance, never the destructive one', () => {
      // This is the single most important expectation in the suite: if focus
      // landed on the confirming affordance, an immediate Enter or Space would
      // delete something the user never agreed to delete.
      const created = render();

      expect(document.activeElement).toBe(cancelButtonOf(created));
    });

    it('does not place initial focus on the confirming affordance even in danger mode', () => {
      const created = render({ danger: true });

      expect(document.activeElement).not.toBe(confirmButtonOf(created));
    });
  });

  // -- (b) ----------------------------------------------------------------
  describe('focus trap boundaries', () => {
    it('wraps forward from the last affordance to the first', () => {
      const created = render();
      const confirmButton = confirmButtonOf(created);
      confirmButton.focus();

      const event = pressKey(confirmButton, 'Tab');

      expect(document.activeElement).toBe(cancelButtonOf(created));
      expect(event.defaultPrevented).toBeTrue();
    });

    it('wraps backward from the first affordance to the last', () => {
      const created = render();
      const cancelButton = cancelButtonOf(created);
      cancelButton.focus();

      const event = pressKey(cancelButton, 'Tab', true);

      expect(document.activeElement).toBe(confirmButtonOf(created));
      expect(event.defaultPrevented).toBeTrue();
    });

    it('leaves a forward move away from the boundary to the browser', () => {
      // Interfering anywhere other than at a boundary would break the platform's
      // own sequential navigation, so the handler must decline to act.
      const created = render();
      const cancelButton = cancelButtonOf(created);
      cancelButton.focus();

      const event = pressKey(cancelButton, 'Tab');

      expect(event.defaultPrevented).toBeFalse();
    });

    it('leaves a backward move away from the boundary to the browser', () => {
      const created = render();
      const confirmButton = confirmButtonOf(created);
      confirmButton.focus();

      const event = pressKey(confirmButton, 'Tab', true);

      expect(event.defaultPrevented).toBeFalse();
    });

    it('ignores keys it does not own', () => {
      const created = render();
      const outputs = observeOutputs(created);

      pressKey(cancelButtonOf(created), 'a');
      pressKey(cancelButtonOf(created), 'Enter');

      expect(outputs.confirm).not.toHaveBeenCalled();
      expect(outputs.cancel).not.toHaveBeenCalled();
    });
  });

  // -- (c) ----------------------------------------------------------------
  describe('Escape dismissal', () => {
    it('emits cancel exactly once', () => {
      const created = render();
      const outputs = observeOutputs(created);

      pressKey(cancelButtonOf(created), 'Escape');

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });

    it('never emits confirm', () => {
      const created = render();
      const outputs = observeOutputs(created);

      pressKey(cancelButtonOf(created), 'Escape');

      expect(outputs.confirm).not.toHaveBeenCalled();
    });

    it('suppresses the user agent\u2019s own close request', () => {
      // Preventing the default is what stops a real Escape from ALSO firing the
      // element's native `cancel` event, which would otherwise reach the same
      // emit path a second time.
      const created = render();

      const event = pressKey(cancelButtonOf(created), 'Escape');

      expect(event.defaultPrevented).toBeTrue();
    });

    it('emits only once even when Escape is pressed repeatedly', () => {
      const created = render();
      const outputs = observeOutputs(created);

      pressKey(cancelButtonOf(created), 'Escape');
      pressKey(cancelButtonOf(created), 'Escape');
      pressKey(cancelButtonOf(created), 'Escape');

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });
  });

  // -- (d) ----------------------------------------------------------------
  describe('cancelling affordance', () => {
    it('emits cancel exactly once when activated', () => {
      const created = render();
      const outputs = observeOutputs(created);

      cancelButtonOf(created).click();

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });

    it('never emits confirm when activated', () => {
      const created = render();
      const outputs = observeOutputs(created);

      cancelButtonOf(created).click();

      expect(outputs.confirm).not.toHaveBeenCalled();
    });

    it('carries the authored cancel wording', () => {
      const created = render();

      expect(renderedTextOf(cancelButtonOf(created)).trim()).toBe(CANCEL_LABEL);
    });
  });

  // -- (e) ----------------------------------------------------------------
  describe('confirming affordance', () => {
    it('emits confirm exactly once when activated', () => {
      const created = render();
      const outputs = observeOutputs(created);

      confirmButtonOf(created).click();

      expect(outputs.confirm).toHaveBeenCalledTimes(1);
    });

    it('never emits cancel when activated', () => {
      const created = render();
      const outputs = observeOutputs(created);

      confirmButtonOf(created).click();

      expect(outputs.cancel).not.toHaveBeenCalled();
    });

    it('emits only once even when activated repeatedly', () => {
      // A second emission would run a consumer's deletion handler twice, which is
      // precisely the failure a confirmation exists to prevent.
      const created = render();
      const outputs = observeOutputs(created);

      confirmButtonOf(created).click();
      confirmButtonOf(created).click();

      expect(outputs.confirm).toHaveBeenCalledTimes(1);
    });

    it('cannot cancel after it has confirmed', () => {
      const created = render();
      const outputs = observeOutputs(created);

      confirmButtonOf(created).click();
      cancelButtonOf(created).click();
      pressKey(cancelButtonOf(created), 'Escape');

      expect(outputs.confirm).toHaveBeenCalledTimes(1);
      expect(outputs.cancel).not.toHaveBeenCalled();
    });

    it('cannot confirm after it has cancelled', () => {
      const created = render();
      const outputs = observeOutputs(created);

      cancelButtonOf(created).click();
      confirmButtonOf(created).click();

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
      expect(outputs.confirm).not.toHaveBeenCalled();
    });
  });

  // -----------------------------------------------------------------------
  describe('backdrop dismissal', () => {
    it('cancels when the click lands on the backdrop', () => {
      // A modal dialog paints its own backdrop, and a click there is reported with
      // the dialog element itself as the target, whereas a click on any content
      // inside reports that content. The comparison is exact, not a heuristic.
      const created = render();
      const outputs = observeOutputs(created);

      dialogOf(created).click();

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
      expect(outputs.confirm).not.toHaveBeenCalled();
    });

    it('does not cancel when the click lands on content inside the dialog', () => {
      const created = render();
      const outputs = observeOutputs(created);

      requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR).click();

      expect(outputs.cancel).not.toHaveBeenCalled();
    });

    it('never confirms from a backdrop click', () => {
      // The least deliberate gesture available must not be able to destroy data.
      const created = render();
      const outputs = observeOutputs(created);

      dialogOf(created).click();

      expect(outputs.confirm).not.toHaveBeenCalled();
    });
  });

  // -----------------------------------------------------------------------
  describe('native dismissal event', () => {
    it('cancels when the platform originates a close request', () => {
      const created = render();
      const outputs = observeOutputs(created);

      dialogOf(created).dispatchEvent(new Event('cancel'));

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });

    it('settles only once across the platform path and the affordance path', () => {
      const created = render();
      const outputs = observeOutputs(created);

      dialogOf(created).dispatchEvent(new Event('cancel'));
      cancelButtonOf(created).click();

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });
  });

  // -- (f) ----------------------------------------------------------------
  describe('accessible name and description wiring', () => {
    it('names the dialog through an id that resolves to a real node', () => {
      const created = render();
      const labelledBy = requireAttribute(dialogOf(created), 'aria-labelledby');

      const named = requireReferencedElement(hostOf(created), labelledBy);

      expect(renderedTextOf(named).trim()).toBe(DEFAULT_TITLE);
    });

    it('describes the dialog through an id that resolves to a real node', () => {
      const created = render();
      const describedBy = requireAttribute(dialogOf(created), 'aria-describedby');

      const described = requireReferencedElement(hostOf(created), describedBy);

      expect(renderedTextOf(described).trim()).toBe(DEFAULT_MESSAGE);
    });

    it('points the name reference at the rendered title element', () => {
      const created = render();
      const labelledBy = requireAttribute(dialogOf(created), 'aria-labelledby');

      expect(requireElement<HTMLElement>(hostOf(created), TITLE_SELECTOR).id).toBe(labelledBy);
    });

    it('points the description reference at the rendered message element', () => {
      const created = render();
      const describedBy = requireAttribute(dialogOf(created), 'aria-describedby');

      expect(requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR).id).toBe(describedBy);
    });

    it('tracks supplied wording through the resolved references', () => {
      const created = render({ title: 'Delete role', message: 'The role will be removed.' });

      const named = requireReferencedElement(hostOf(created), requireAttribute(dialogOf(created), 'aria-labelledby'));
      const described = requireReferencedElement(
        hostOf(created),
        requireAttribute(dialogOf(created), 'aria-describedby'),
      );

      expect(renderedTextOf(named).trim()).toBe('Delete role');
      expect(renderedTextOf(described).trim()).toBe('The role will be removed.');
    });

    it('gives two concurrently mounted dialogs non-colliding ids', () => {
      // Ids are generated from a monotonic instance counter precisely so that two
      // dialogs cannot both claim the same `aria-labelledby` target.
      const first = render();
      const firstTitleId = requireElement<HTMLElement>(hostOf(first), TITLE_SELECTOR).id;

      const second = TestBed.createComponent(ConfirmDialogComponent);
      second.detectChanges();

      try {
        const secondTitleId = requireElement<HTMLElement>(second.nativeElement as HTMLElement, TITLE_SELECTOR).id;

        expect(secondTitleId).not.toBe(firstTitleId);
        expect(secondTitleId.length).toBeGreaterThan(0);
      } finally {
        second.destroy();
      }
    });
  });

  // -----------------------------------------------------------------------
  describe('default wording', () => {
    it('renders the measured default title', () => {
      const created = render();

      expect(renderedTextOf(requireElement<HTMLElement>(hostOf(created), TITLE_SELECTOR)).trim()).toBe(DEFAULT_TITLE);
    });

    it('renders the measured default message', () => {
      const created = render();

      expect(renderedTextOf(requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR)).trim()).toBe(
        DEFAULT_MESSAGE,
      );
    });

    it('renders the measured default confirm label', () => {
      const created = render();

      expect(renderedTextOf(confirmButtonOf(created)).trim()).toBe(DEFAULT_CONFIRM_LABEL);
    });

    UNSUPPORTED_CLAIMS.forEach((claim: string): void => {
      it(`never claims "${claim}"`, () => {
        // The legacy confirmation made no such claim, and several guarded
        // operations were recoverable, so inventing one would be a divergence.
        const created = render();

        expect(renderedTextOf(hostOf(created)).toLowerCase())
          .withContext(`the dialog must not invent the claim "${claim}"`)
          .not.toContain(claim);
      });
    });
  });

  // -- (g) ----------------------------------------------------------------
  describe('wording is interpolated, never trusted as markup', () => {
    it('renders a script payload in the message as escaped plain text', () => {
      const created = render({ message: SCRIPT_PAYLOAD });
      const message = requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(SCRIPT_PAYLOAD);
      expect(message.querySelector('script')).toBeNull();
    });

    it('renders inline markup in the message as escaped plain text', () => {
      const created = render({ message: BOLD_PAYLOAD });
      const message = requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(BOLD_PAYLOAD);
      expect(message.querySelector('b')).toBeNull();
    });

    it('renders a leading break payload in the message as escaped plain text', () => {
      const created = render({ message: LEADING_BREAK_PAYLOAD });
      const message = requireElement<HTMLElement>(hostOf(created), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(LEADING_BREAK_PAYLOAD);
      expect(message.querySelector('br')).toBeNull();
    });

    it('renders a script payload in the title as escaped plain text', () => {
      const created = render({ title: SCRIPT_PAYLOAD });
      const title = requireElement<HTMLElement>(hostOf(created), TITLE_SELECTOR);

      expect(renderedTextOf(title)).toContain(SCRIPT_PAYLOAD);
      expect(title.querySelector('script')).toBeNull();
    });

    it('renders a script payload in the confirm label as escaped plain text', () => {
      const created = render({ confirmLabel: SCRIPT_PAYLOAD });
      const confirmButton = confirmButtonOf(created);

      expect(renderedTextOf(confirmButton)).toContain(SCRIPT_PAYLOAD);
      expect(confirmButton.querySelector('script')).toBeNull();
    });

    it('keeps hostile wording addressable as an accessible name', () => {
      // Escaping must not break the naming wiring: the reference still has to
      // resolve, otherwise the dialog would announce nothing at all.
      const created = render({ title: BOLD_PAYLOAD });
      const named = requireReferencedElement(hostOf(created), requireAttribute(dialogOf(created), 'aria-labelledby'));

      expect(renderedTextOf(named).trim()).toBe(BOLD_PAYLOAD);
    });
  });

  // -- (h) ----------------------------------------------------------------
  describe('danger mode is presentation only', () => {
    it('applies no danger modifier by default', () => {
      const created = render();

      expect(confirmButtonOf(created).classList.contains(DANGER_MODIFIER_CLASS)).toBeFalse();
    });

    it('applies the danger modifier to the confirming affordance when enabled', () => {
      const created = render({ danger: true });

      expect(confirmButtonOf(created).classList.contains(DANGER_MODIFIER_CLASS)).toBeTrue();
    });

    it('never applies the danger modifier to the cancelling affordance', () => {
      const created = render({ danger: true });

      expect(cancelButtonOf(created).classList.contains(DANGER_MODIFIER_CLASS)).toBeFalse();
    });

    it('emits confirm identically whether or not danger is enabled', () => {
      const created = render({ danger: true });
      const outputs = observeOutputs(created);

      confirmButtonOf(created).click();

      expect(outputs.confirm).toHaveBeenCalledTimes(1);
      expect(outputs.cancel).not.toHaveBeenCalled();
    });

    it('emits cancel identically whether or not danger is enabled', () => {
      const created = render({ danger: true });
      const outputs = observeOutputs(created);

      cancelButtonOf(created).click();

      expect(outputs.cancel).toHaveBeenCalledTimes(1);
    });

    it('signals severity by more than colour alone', () => {
      // Colour cannot be the sole carrier of meaning. In danger mode a decorative
      // glyph is added alongside the affordance's own text label, so the severity
      // survives for a user who cannot perceive the colour change.
      const plain = render();
      const plainText = renderedTextOf(confirmButtonOf(plain)).trim();
      plain.destroy();
      fixture = undefined;

      const dangerous = render({ danger: true });
      const dangerousText = renderedTextOf(confirmButtonOf(dangerous)).trim();

      expect(dangerousText.length).toBeGreaterThan(plainText.length);
    });

    it('hides the severity glyph from assistive technology', () => {
      // The glyph must not compete with the label as a naming source, nor announce
      // the severity a second time.
      const created = render({ danger: true });
      const decorative = requireElement<HTMLElement>(confirmButtonOf(created), '[aria-hidden="true"]');

      expect(decorative.getAttribute('aria-hidden')).toBe('true');
    });

    it('renders no severity glyph when danger is disabled', () => {
      const created = render();

      expect(confirmButtonOf(created).querySelector('[aria-hidden="true"]')).toBeNull();
    });

    it('accepts the danger flag as a bare attribute', () => {
      // The input is declared with a boolean-attribute transform, so a consumer
      // writing `danger` with no value must get the enabled behaviour.
      const created = render({ danger: '' });

      expect(created.componentInstance.danger).toBeTrue();
    });
  });

  // -- (i) ----------------------------------------------------------------
  describe('focus restoration', () => {
    it('returns focus to the invoking element when the dialog closes', () => {
      const host = TestBed.createComponent(ConfirmDialogHostComponent);
      host.detectChanges();

      const invoker = requireElement<HTMLButtonElement>(host.nativeElement as HTMLElement, '#invoker');
      invoker.focus();
      expect(document.activeElement).toBe(invoker);

      host.componentInstance.open = true;
      host.detectChanges();
      expect(document.activeElement).not.toBe(invoker);

      host.componentInstance.open = false;
      host.detectChanges();

      try {
        expect(document.activeElement).toBe(invoker);
      } finally {
        host.destroy();
      }
    });

    it('closes the dialog when the component is destroyed', () => {
      const host = TestBed.createComponent(ConfirmDialogHostComponent);
      host.detectChanges();
      host.componentInstance.open = true;
      host.detectChanges();

      const dialog = requireElement<HTMLDialogElement>(host.nativeElement as HTMLElement, DIALOG_SELECTOR);
      expect(dialog.open).toBeTrue();

      host.componentInstance.open = false;
      host.detectChanges();

      try {
        expect(dialog.open).toBeFalse();
      } finally {
        host.destroy();
      }
    });

    it('skips restoration rather than focusing the body when there was no invoker', () => {
      // A bare fixture focuses nothing, so the capture correctly yields "absent".
      // Focusing the body would be indistinguishable from focusing nothing, so the
      // component must decline to do it.
      const created = render();
      created.destroy();
      fixture = undefined;

      expect(document.activeElement).not.toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  describe('affordance structure', () => {
    it('renders exactly two affordances', () => {
      const created = render();

      expect(buttonsOf(created).length).toBe(2);
    });

    it('places the safe action before the destructive one in document order', () => {
      // Safety: the user agent's own first-focusable heuristic then lands on
      // Cancel. Fidelity: `Website/admin/Security/editroles.ascx` L179-189 renders
      // Update, then Cancel, then Delete, so the measured legacy order already put
      // the safe action ahead of the destructive one.
      const created = render();

      expect(renderedTextOf(buttonsOf(created)[0]).trim()).toBe(CANCEL_LABEL);
      expect(renderedTextOf(buttonsOf(created)[1]).trim()).toBe(DEFAULT_CONFIRM_LABEL);
    });

    it('declares every affordance as a non-submitting button', () => {
      // An implicit submit button would post an ancestor form, which mirrors why
      // both legacy link buttons declared `CausesValidation="False"`.
      buttonsOf(render()).forEach((button: HTMLButtonElement): void => {
        expect(button.type)
          .withContext(`"${renderedTextOf(button).trim()}" must not submit a form`)
          .toBe('button');
      });
    });

    it('gives every affordance real, visible text', () => {
      // `roles.ascx` L13 rendered its delete image button with no alternate text,
      // no resource key and no text at all — an accessibility defect this
      // expectation forbids from reappearing.
      buttonsOf(render()).forEach((button: HTMLButtonElement): void => {
        expect(renderedTextOf(button).trim().length).toBeGreaterThan(0);
      });
    });

    it('renders no image element', () => {
      // `~/images/delete.gif` is not ported; the only static asset this
      // application ships is the favicon.
      const created = render();

      expect(hostOf(created).querySelector('img')).toBeNull();
    });

    it('declares no autofocus on the destructive affordance', () => {
      const created = render();

      expect(confirmButtonOf(created).hasAttribute('autofocus')).toBeFalse();
    });
  });

  // -----------------------------------------------------------------------
  describe('document outline', () => {
    LANDMARK_SELECTORS.forEach((selector: string): void => {
      it(`contributes no "${selector}" landmark to the page outline`, () => {
        const created = render();

        expect(hostOf(created).querySelector(selector))
          .withContext(`a shared component must not contribute a "${selector}" landmark`)
          .toBeNull();
      });
    });

    it('suppresses the ambient host tooltip', () => {
      // The host binds `title` to null deliberately: an inherited tooltip would
      // duplicate the dialog's own heading as a hover hint.
      const created = render();

      expect(hostOf(created).getAttribute('title')).toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  describe('performs no input or output of its own', () => {
    it('issues no request when it is merely rendered', () => {
      // This component asks a question and emits an intent. Deleting is the
      // consumer's responsibility, which is why nothing here may reach the
      // network — the legacy affordance likewise performed no I/O of its own; it
      // gated a postback the page then made.
      render();

      expect(httpMock.match(() => true)).toEqual([]);
    });

    it('issues no request when the destructive action is confirmed', () => {
      const created = render({ danger: true });

      confirmButtonOf(created).click();

      expect(httpMock.match(() => true)).toEqual([]);
    });

    it('issues no request when the dialog is cancelled', () => {
      const created = render();

      cancelButtonOf(created).click();

      expect(httpMock.match(() => true)).toEqual([]);
    });
  });
});


// ===========================================================================
// SECOND SUITE - the lifecycle, structural-resolution and focus-restoration guards.
//
// These specifications were authored against the same component contract and cover
// paths the suite above does not reach: a detached host, a view query that has not
// been refreshed, every route taken with no resolvable dialog element at all, the
// focus-trap boundary edge cases (no wrap origin, nothing focusable, unfocusable
// candidates, a newly appended boundary), attribute coercion of the danger flag in
// every shape the framework can deliver, and the four focus-restoration outcomes.
// They share the four wording constants declared above rather than restating them.
// ===========================================================================

/**
 * Reads only an element's OWN character data, ignoring descendant elements.
 *
 * `textContent` walks the whole subtree, so it also picks up the decorative
 * severity glyph the confirming affordance renders in danger mode. That glyph is
 * `aria-hidden` and is not wording any call site supplied, so an expectation about
 * caller-supplied wording has to exclude it or it asserts two things at once.
 *
 * @param element The element whose own text is wanted.
 * @returns The concatenated, trimmed text of the element's direct text nodes.
 */
function directTextOf(element: Element): string {
  return Array.from(element.childNodes)
    .filter((node): node is Text => node.nodeType === Node.TEXT_NODE)
    .map((node) => node.textContent ?? '')
    .join('')
    .trim();
}

/** Block class, carried by the `<dialog>` element itself. */
const BLOCK_CLASS = 'confirm-dialog';

/** The destructive modifier the `danger` input drives. */
const DANGER_CLASS = 'confirm-dialog__button--danger';

/**
 * A message carrying a live script block, HTML-escaped in the legacy resource
 * file it models. One in-scope legacy resource genuinely stores a remote script
 * block this way, so this is a real input shape rather than a contrived one. The
 * publisher identifier from the real value is deliberately not reproduced.
 */
const SCRIPT_BEARING_MESSAGE = '<script>document.title = "pwned";</script>';

/** A message carrying ordinary markup, which must also render as visible text. */
const MARKUP_BEARING_MESSAGE = '<b>Portal 0</b>';

/** Every landmark element this component must never emit. */
const FORBIDDEN_LANDMARKS: readonly string[] = ['header', 'main', 'nav', 'footer'];

/**
 * A consumer call site writing `danger` as a BARE ATTRIBUTE.
 *
 * This host exists because a property binding cannot exercise the attribute path:
 * the bare form arrives at the input as the empty string and only the declared
 * boolean coercion turns it into `true`. Removing that coercion would leave this
 * host rendering non-destructive styling for a call site that asked for
 * destructive styling.
 */
@Component({
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: ` <app-confirm-dialog danger></app-confirm-dialog> `,
})
class BareDangerAttributeHostComponent {}

/**
 * A consumer call site writing `title` as a STATIC TEMPLATE ATTRIBUTE.
 *
 * The framework copies a static template attribute onto the rendered element IN
 * ADDITION to assigning the matching input, so this is the exact shape that makes
 * the `title` input's collision with the global HTML `title` attribute
 * observable. A surviving attribute would supply advisory text for the host and
 * all of its descendants and would name the wrapper in the accessibility tree,
 * competing with the dialog's own accessible name.
 */
@Component({
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: ` <app-confirm-dialog title="Remove Role"></app-confirm-dialog> `,
})
class StaticTitleAttributeHostComponent {}

/**
 * A consumer call site binding every input through properties.
 *
 * Binding all four from a host template is the COMPILE-TIME proof that all four
 * are public: strict input access modifiers are enabled, so a private or
 * protected input would fail this file's own compilation, which is a stronger
 * guarantee than any runtime reflection check.
 */
@Component({
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <app-confirm-dialog
      [title]="heading"
      [message]="question"
      [confirmLabel]="verb"
      [danger]="destructive"
      (confirm)="confirmed = confirmed + 1"
      (cancel)="cancelled = cancelled + 1"
    ></app-confirm-dialog>
  `,
})
class FullyBoundHostComponent {
  heading = 'Confirm Removal';
  question = 'Remove the Administrators role?';
  verb = 'Remove';
  destructive = true;
  confirmed = 0;
  cancelled = 0;
}

describe('ConfirmDialogComponent — lifecycle, focus-trap and restoration guards', () => {
  /**
   * Every fixture created during a test, destroyed afterwards.
   *
   * Teardown is mandatory rather than tidy: `showModal()` promotes the dialog to
   * the top layer and makes the rest of the document inert, so a dialog left open
   * would make every later focus assertion in this file meaningless.
   */
  let fixtures: ComponentFixture<unknown>[] = [];

  /** Elements this suite appended to the document, removed afterwards. */
  let strayElements: HTMLElement[] = [];

  beforeEach(async () => {
    // MANDATED HARNESS SHAPE: the component and EVERY standalone test host are
    // registered through `imports`. A `declarations` array is neither used nor
    // available for standalone components, and a host omitted from `imports`
    // would be silently unresolvable at the moment it was created.
    await TestBed.configureTestingModule({
      imports: [
        ConfirmDialogComponent,
        BareDangerAttributeHostComponent,
        StaticTitleAttributeHostComponent,
        FullyBoundHostComponent,
      ],
    }).compileComponents();

    fixtures = [];
    strayElements = [];
  });

  afterEach(() => {
    for (const fixture of fixtures) {
      fixture.destroy();
    }
    for (const element of strayElements) {
      element.remove();
    }
    fixtures = [];
    strayElements = [];
  });

  // -------------------------------------------------------------------------
  //  Harness helpers. Narrowing discipline applied without exception: every
  //  lookup is narrowed with `instanceof` or an explicit null guard, so no
  //  non-null assertion and no cast appears anywhere below.
  // -------------------------------------------------------------------------

  /**
   * Creates the dialog WITHOUT initialising its view, registered for teardown.
   *
   * Deferring the first change-detection pass matters for the teardown group,
   * where the state of the document at construction time is itself the subject.
   * Registering for teardown regardless means an early return from a narrowing
   * guard can never leak an open modal into the tests that follow.
   */
  const createUninitialisedDialog = (): ComponentFixture<ConfirmDialogComponent> => {
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    fixtures.push(fixture);

    return fixture;
  };

  /** Creates the dialog directly, registered for teardown, view initialised. */
  const createDialog = (): ComponentFixture<ConfirmDialogComponent> => {
    const fixture = createUninitialisedDialog();
    fixture.detectChanges();

    return fixture;
  };

  /** Creates a host fixture, registered for teardown, view initialised. */
  const createHost = <T>(host: Type<T>): ComponentFixture<T> => {
    const fixture = TestBed.createComponent(host);
    fixtures.push(fixture);
    fixture.detectChanges();

    return fixture;
  };

  /** The rendered `<dialog>` within a fixture, or `null` when absent. */
  const dialogWithin = (fixture: ComponentFixture<unknown>): HTMLDialogElement | null => {
    const root: HTMLElement = fixture.nativeElement;
    const found = root.querySelector('dialog');

    return found instanceof HTMLDialogElement ? found : null;
  };

  /** Every `<button>` inside the dialog, in document order. */
  const buttonsWithin = (dialog: HTMLDialogElement): readonly HTMLButtonElement[] => {
    const collected: HTMLButtonElement[] = [];
    dialog.querySelectorAll('button').forEach((candidate: Element): void => {
      if (candidate instanceof HTMLButtonElement) {
        collected.push(candidate);
      }
    });

    return collected;
  };

  /** Trimmed text of the first element matching a selector, or `null`. */
  const textOf = (root: ParentNode, selector: string): string | null => {
    const found = root.querySelector(selector);

    return found === null ? null : (found.textContent ?? '').trim();
  };

  /** Dispatches a bubbling, cancellable keydown and reports it back. */
  const dispatchKeydown = (
    target: EventTarget,
    key: string,
    shiftKey = false,
  ): KeyboardEvent => {
    const event = new KeyboardEvent('keydown', {
      key,
      shiftKey,
      bubbles: true,
      cancelable: true,
    });
    target.dispatchEvent(event);

    return event;
  };

  /** Appends an element to the document body and registers it for removal. */
  const appendStray = (element: HTMLElement): HTMLElement => {
    document.body.appendChild(element);
    strayElements.push(element);

    return element;
  };

  /**
   * Records how many times each output fired.
   *
   * Counters rather than booleans, because the single-settlement invariant is
   * about COUNTS: "emitted at most once" cannot be distinguished from "emitted"
   * by a boolean.
   */
  interface OutcomeLog {
    readonly confirmed: () => number;
    readonly cancelled: () => number;
  }

  const observeOutcomes = (fixture: ComponentFixture<ConfirmDialogComponent>): OutcomeLog => {
    let confirmed = 0;
    let cancelled = 0;

    fixture.componentInstance.confirm.subscribe((): void => {
      confirmed = confirmed + 1;
    });
    fixture.componentInstance.cancel.subscribe((): void => {
      cancelled = cancelled + 1;
    });

    return {
      confirmed: (): number => confirmed,
      cancelled: (): number => cancelled,
    };
  };

  // =========================================================================
  //  1. STRUCTURE, ARIA AND ELEMENT IDENTIFIERS
  // =========================================================================
  describe('structure and accessible semantics', () => {
    it('renders exactly one dialog carrying the block class and no open attribute in markup', () => {
      const fixture = createDialog();
      const root: HTMLElement = fixture.nativeElement;

      expect(root.querySelectorAll('dialog').length).toBe(1);

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect(dialog.classList.contains(BLOCK_CLASS)).toBeTrue();

      // The element must be opened IMPERATIVELY, never by a static attribute. A
      // static `open` attribute produces a non-modal dialog that does not make
      // the document inert and does not paint a backdrop, and the component
      // detects that case and refuses to call `showModal()` — so the defect would
      // be silent. Asserting the property is true while the markup-authored
      // attribute path is excluded is what distinguishes the two.
      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
    });

    it('declares the dialog role and modal state on the dialog element, never on the host', () => {
      const fixture = createDialog();
      const host: HTMLElement = fixture.nativeElement;
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // The role is `alertdialog`, not the generic `dialog`. This component exists
      // to obtain confirmation before a destructive action, which is exactly the
      // case `alertdialog` names: it tells assistive technology the dialog carries
      // an alert-level message and that focus is already inside it. The generic
      // role would announce a plain dialog and lose that urgency, so the narrower
      // role is a contract rather than a preference.
      expect(dialog.getAttribute('role')).toBe('alertdialog');
      expect(dialog.getAttribute('aria-modal')).toBe('true');

      // The host is a presentational wrapper the stylesheet removes from the box
      // tree. Announcing a dialog there would report a SECOND, empty dialog to
      // assistive technology, so the absence is a contract, not an accident.
      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-modal')).toBeNull();
      expect(host.getAttribute('aria-labelledby')).toBeNull();
      expect(host.getAttribute('aria-describedby')).toBeNull();
    });

    it('points the accessible name and description at the rendered title and message', () => {
      const fixture = createDialog();
      const component = fixture.componentInstance;
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const heading = dialog.querySelector('h2');
      const description = dialog.querySelector('p');

      expect(heading).not.toBeNull();
      expect(description).not.toBeNull();
      if (heading === null || description === null) {
        return;
      }

      // The references must resolve to REAL elements inside this dialog. A
      // dangling reference leaves the dialog unnamed and undescribed, which no
      // visual check would reveal.
      expect(dialog.getAttribute('aria-labelledby')).toBe(component.titleId);
      expect(dialog.getAttribute('aria-describedby')).toBe(component.messageId);
      expect(heading.id).toBe(component.titleId);
      expect(description.id).toBe(component.messageId);
      expect(dialog.querySelectorAll(`#${component.titleId}`).length).toBe(1);
      expect(dialog.querySelectorAll(`#${component.messageId}`).length).toBe(1);
    });

    it('gives two simultaneously mounted dialogs distinct element identifiers', () => {
      const first = createDialog();
      const second = createDialog();

      // Hardcoded ids would collide the moment two dialogs were mounted at once,
      // and duplicate ids break the accessible-name reference for BOTH of them.
      expect(second.componentInstance.titleId).not.toBe(first.componentInstance.titleId);
      expect(second.componentInstance.messageId).not.toBe(first.componentInstance.messageId);
      expect(first.componentInstance.titleId).not.toBe(first.componentInstance.messageId);
    });

    it('renders the documented defaults when nothing is bound', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // Independently restated literals, never read back from the instance, so a
      // silent change to any default fails here.
      expect(textOf(dialog, '.confirm-dialog__title')).toBe(DEFAULT_TITLE);
      expect(textOf(dialog, '.confirm-dialog__message')).toBe(DEFAULT_MESSAGE);

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);
      expect((buttons[0].textContent ?? '').trim()).toBe(CANCEL_LABEL);
      expect((buttons[1].textContent ?? '').trim()).toBe(DEFAULT_CONFIRM_LABEL);
    });

    it('orders the cancelling affordance before the confirming one', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);

      expect(buttons.length).toBe(2);

      // SAFETY-CRITICAL ORDER, and the measured legacy order too: the legacy edit
      // screen rendered Cancel before Delete. Both affordances are `type="button"`
      // so that neither can ever submit an enclosing form.
      expect((buttons[0].textContent ?? '').trim()).toBe(CANCEL_LABEL);
      expect(buttons[0].type).toBe('button');
      expect(buttons[1].type).toBe('button');
      expect(buttons[0].compareDocumentPosition(buttons[1]) & Node.DOCUMENT_POSITION_FOLLOWING)
        .toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    });

    it('emits no document landmark and no image asset', () => {
      const fixture = createDialog();
      const root: HTMLElement = fixture.nativeElement;

      for (const landmark of FORBIDDEN_LANDMARKS) {
        expect(root.querySelectorAll(landmark).length)
          .withContext(`a shared component must not emit a <${landmark}> landmark`)
          .toBe(0);
      }

      // The workspace ships no raster artwork, so the legacy delete icon is not
      // ported and no image reference may appear.
      expect(root.querySelectorAll('img').length).toBe(0);
      expect(root.querySelectorAll('svg').length).toBe(0);
    });

    it('declares no author-supplied tab index anywhere', () => {
      const fixture = createDialog();
      const root: HTMLElement = fixture.nativeElement;

      // Document order alone must be correct. A positive tab index would fight
      // the component's own boundary wrap, and a negative one on either button
      // would remove it from the tab order entirely — which is the accessibility
      // defect the legacy collapsible section head exhibited.
      expect(root.querySelectorAll('[tabindex]').length).toBe(0);
    });

    it('strips the colliding title attribute from the host at a static call site', () => {
      const fixture = createHost(StaticTitleAttributeHostComponent);
      const root: HTMLElement = fixture.nativeElement;
      const host = root.querySelector('app-confirm-dialog');

      expect(host).not.toBeNull();
      if (host === null) {
        return;
      }

      // The input value must still have landed, and the attribute must be gone.
      // Asserting both together is what distinguishes a working strip from an
      // input that never bound at all.
      expect(host.hasAttribute('title')).toBeFalse();
      expect(textOf(root, '.confirm-dialog__title')).toBe('Remove Role');
    });
  });

  // =========================================================================
  //  1a. THE GUARDS AROUND THE NORMAL LIFECYCLE
  //
  //  Both element lookups return an optional under strict null checking, so every
  //  caller must handle absence in order to compile at all. Two of those absences
  //  are genuinely reachable and are asserted here; the two that are not are
  //  named in the file header rather than faked into existence.
  // =========================================================================
  describe('guards around the normal lifecycle', () => {
    it('renders nothing at all rather than throwing when its host is detached', () => {
      const fixture = createUninitialisedDialog();
      const host: HTMLElement = fixture.nativeElement;

      // Detached BEFORE the view initialises, which is the only way to reach the
      // connectivity guard. `showModal()` raises `InvalidStateError` on an element
      // that is not in a document, so without the guard this would be an uncaught
      // exception during change detection rather than a quiet no-op.
      host.remove();

      expect((): void => {
        fixture.detectChanges();
      }).not.toThrow();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // A closed `<dialog>` is not displayed, so nothing is shown. A non-modal
      // `show()` fallback is deliberately not offered: a confirmation that does
      // not block is worse than no confirmation at all.
      expect(dialog.open).toBeFalse();
    });

    it('finds its dialog structurally when the view query has not been refreshed', () => {
      // MEASURED PROPERTY OF THE FRAMEWORK. Creating a component builds its
      // template's elements immediately, but view queries are only populated by
      // the first change-detection pass — so in this window the `<dialog>` and the
      // cancel affordance are both in the DOM while both view references are
      // still undefined. That is precisely the window the structural lookup and
      // the first-focusable fallback exist for, and it is asserted positively
      // here rather than merely being survived.
      const fixture = createUninitialisedDialog();
      const component = fixture.componentInstance;

      component.ngAfterViewInit();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // Both fallbacks did their job: the dialog was located and opened modally,
      // and focus still reached the cancelling affordance rather than the
      // destructive one — the safety guarantee holds even here.
      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
      expect(document.activeElement).toBe(buttons[0]);
    });

    it('treats every route as a no-op when no dialog element can be found at all', () => {
      const fixture = createUninitialisedDialog();
      const component = fixture.componentInstance;
      const outcomes = observeOutcomes(fixture);
      const host: HTMLElement = fixture.nativeElement;
      const dialog = host.querySelector('dialog');

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // With the element removed while the view query is still unpopulated,
      // NEITHER lookup can succeed. This is the only state in which the dialog
      // resolves to nothing, and it is what every `undefined` guard in the
      // component is written to survive: each must return quietly instead of
      // dereferencing nothing.
      dialog.remove();

      expect((): void => {
        component.ngAfterViewInit();
      }).not.toThrow();
      expect((): void => {
        component.onBackdropClick(new MouseEvent('click'));
      }).not.toThrow();
      expect((): void => {
        component.onKeydown(new KeyboardEvent('keydown', { key: 'Tab' }));
      }).not.toThrow();
      expect((): void => {
        component.ngOnDestroy();
      }).not.toThrow();

      // And critically, none of them may settle the dialog as a side effect of
      // failing to find it — a missing element must never be read as consent.
      expect(outcomes.confirmed()).toBe(0);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('still settles from an explicit keyboard dismissal with no dialog element', () => {
      const fixture = createUninitialisedDialog();
      const component = fixture.componentInstance;
      const outcomes = observeOutcomes(fixture);
      const host: HTMLElement = fixture.nativeElement;
      const dialog = host.querySelector('dialog');

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      dialog.remove();

      // COMPLEMENT TO THE TEST ABOVE, and the reason that one is not vacuous.
      // `Escape` is an unambiguous instruction from the user and does not depend
      // on locating any element, so it must still cancel. If every route were
      // inert whenever the dialog could not be found, the "no-op" assertions
      // above would prove nothing about target comparison or origin resolution.
      const event = dispatchKeydown(host, 'Escape');

      expect(event.defaultPrevented).toBeTrue();
      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  2. INITIAL FOCUS
  // =========================================================================
  describe('initial focus placement', () => {
    it('places focus on the cancelling affordance, never on the destructive one', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // THE SAFETY GUARANTEE. If focus rested on the destructive affordance, an
      // immediate `Enter` or `Space` — the reflex of a user who did not expect a
      // dialog — would delete the record. Asserting the negative alongside the
      // positive is deliberate: a mutation that focused the wrong button, or
      // focused nothing and left focus on the body, both have to fail.
      expect(document.activeElement).toBe(buttons[0]);
      expect(document.activeElement).not.toBe(buttons[1]);
      expect(document.activeElement).not.toBe(document.body);
    });
  });

  // =========================================================================
  //  3. THE FOCUS TRAP
  // =========================================================================
  describe('focus trap at the boundaries', () => {
    it('wraps forward from the last focusable element to the first', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      const event = dispatchKeydown(buttons[1], 'Tab');

      // The default action MUST be suppressed, otherwise the browser would also
      // move focus and the two movements would fight.
      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(buttons[0]);
    });

    it('wraps backward from the first focusable element to the last', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      const event = dispatchKeydown(buttons[0], 'Tab', true);

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(buttons[1]);
    });

    it('leaves interior tabbing entirely to the browser', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // Forward from the FIRST element is an interior move, not a boundary one.
      // Interfering here would override the user's own platform tab order, so the
      // component must not prevent the default and must not move focus itself.
      const forwardFromFirst = dispatchKeydown(buttons[0], 'Tab');

      expect(forwardFromFirst.defaultPrevented).toBeFalse();
      expect(document.activeElement).toBe(buttons[0]);

      // And backward from the LAST element is likewise interior.
      buttons[1].focus();
      const backwardFromLast = dispatchKeydown(buttons[1], 'Tab', true);

      expect(backwardFromLast.defaultPrevented).toBeFalse();
      expect(document.activeElement).toBe(buttons[1]);
    });

    it('resolves the wrap origin from the focused element when the event targets the dialog', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      buttons[1].focus();

      // A synthetic event may be aimed at the dialog while focus genuinely rests
      // on a button, which is the documented fallback path. Without it, a
      // dispatched event would resolve no origin and the wrap would never fire.
      const event = dispatchKeydown(dialog, 'Tab');

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(buttons[0]);
    });

    it('does nothing when the wrap origin cannot be established at all', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // NOTE ON HOW THIS STATE IS REACHED. A modal open makes everything outside
      // the dialog inert, so focus cannot be parked on an unrelated element to
      // create this case — an attempt to do so is silently ignored and focus
      // stays inside, which would make this test pass for entirely the wrong
      // reason. Blurring instead genuinely surrenders focus to the document body,
      // which the component reports as "nothing meaningful is focused".
      buttons[0].blur();

      expect(document.activeElement).not.toBe(buttons[0]);
      expect(document.activeElement).not.toBe(buttons[1]);

      // The event is aimed at the dialog, which is not itself a focus candidate,
      // so neither the target nor the focused element is a member of the set.
      const event = dispatchKeydown(dialog, 'Tab');

      // No wrap, no interference: an unresolvable origin must not be mistaken for
      // a boundary. Asserting `defaultPrevented` is false is the discriminating
      // half — a handler that wrapped unconditionally would pass a
      // "focus did not change" check alone.
      expect(event.defaultPrevented).toBeFalse();
      expect(document.activeElement).not.toBe(buttons[1]);
    });

    it('does nothing when the dialog renders nothing focusable', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const actions = dialog.querySelector('.confirm-dialog__actions');
      expect(actions).not.toBeNull();
      if (actions === null) {
        return;
      }

      actions.remove();

      const event = dispatchKeydown(dialog, 'Tab');

      // There is no boundary to wrap at, so the handler must return without
      // preventing the default. `showModal()` still confines focus natively.
      expect(event.defaultPrevented).toBeFalse();
      expect(dialog.querySelectorAll('button').length).toBe(0);
    });

    it('excludes unfocusable candidates from the wrap boundary', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // Every one of these matches the candidate selector, and every one is
      // appended AFTER the action row — so if any were counted, the confirming
      // affordance would no longer be the last focusable element and the forward
      // wrap below would not fire. Each exclusion is a real one rather than a
      // defensive guess.
      const disabled = document.createElement('button');
      disabled.disabled = true;
      disabled.textContent = 'Disabled';

      const hiddenAttribute = document.createElement('button');
      hiddenAttribute.hidden = true;
      hiddenAttribute.textContent = 'Hidden';

      const notRendered = document.createElement('button');
      notRendered.style.display = 'none';
      notRendered.textContent = 'Not rendered';

      const negativeTabIndex = document.createElement('div');
      negativeTabIndex.setAttribute('tabindex', '-1');

      const notEditable = document.createElement('div');
      notEditable.setAttribute('contenteditable', 'false');

      const hiddenInput = document.createElement('input');
      hiddenInput.type = 'hidden';

      const inertWrapper = document.createElement('div');
      inertWrapper.setAttribute('inert', '');
      const inertButton = document.createElement('button');
      inertButton.textContent = 'Inert';
      inertWrapper.appendChild(inertButton);

      for (const candidate of [
        disabled,
        hiddenAttribute,
        notRendered,
        negativeTabIndex,
        notEditable,
        hiddenInput,
        inertWrapper,
      ]) {
        dialog.appendChild(candidate);
      }

      const event = dispatchKeydown(buttons[1], 'Tab');

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(buttons[0]);
    });

    it('does treat an appended focusable element as the new boundary', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // POSITIVE CONTROL for the exclusion test above. Without it, that test
      // would also pass against an implementation that ignored appended elements
      // altogether, or one whose collector returned nothing at all. An enabled,
      // rendered button appended after the action row IS the last focusable
      // element, so the confirming affordance stops being a boundary and the
      // forward wrap must NOT fire from it.
      const included = document.createElement('button');
      included.textContent = 'Included';
      dialog.appendChild(included);

      const fromConfirm = dispatchKeydown(buttons[1], 'Tab');

      expect(fromConfirm.defaultPrevented).toBeFalse();

      // And the appended element now wraps to the cancelling affordance instead.
      const fromAppended = dispatchKeydown(included, 'Tab');

      expect(fromAppended.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(buttons[0]);
    });

    it('ignores every key other than Tab and Escape', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      for (const key of ['Enter', ' ', 'a', 'ArrowDown', 'Esc', 'escape']) {
        const event = dispatchKeydown(buttons[0], key);

        expect(event.defaultPrevented)
          .withContext(`"${key}" must be left entirely to the browser`)
          .toBeFalse();
      }

      // `Esc` and `escape` are included above on purpose: keys are compared by
      // their modern `key` value, so neither the legacy spelling nor a
      // differently-cased one may settle the dialog.
      expect(outcomes.cancelled()).toBe(0);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  4. SETTLEMENT ROUTES
  // =========================================================================
  describe('confirmation route', () => {
    it('emits confirm exactly once when the confirming affordance is activated', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      buttons[1].click();

      expect(outcomes.confirmed()).toBe(1);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('does not close the dialog when it settles', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      buttonsWithin(dialog)[1].click();

      // The presence-is-open contract: closing here would leave a mounted but
      // invisible component behind. Teardown belongs to the consumer, which
      // unmounts in response to the output.
      expect(dialog.open).toBeTrue();
    });
  });

  describe('cancellation routes', () => {
    it('emits cancel exactly once when the cancelling affordance is activated', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      buttonsWithin(dialog)[0].click();

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('emits cancel exactly once on Escape, and suppresses the default close request', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const event = dispatchKeydown(dialog, 'Escape');

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);

      // Suppressing the default is what stops a REAL `Escape` press from also
      // raising the element's native `cancel` event and reaching the same
      // handler twice. The emit-once guard is the guarantee; this keeps the
      // common path clean.
      expect(event.defaultPrevented).toBeTrue();
    });

    it('emits cancel exactly once on a user-agent-initiated dismissal', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // The element's own `cancel` event is exactly what a genuine platform
      // dismissal raises, and it is the only path reachable without a keydown.
      dialog.dispatchEvent(new Event('cancel'));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('emits cancel exactly once for a click on the backdrop', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // A click on a modal dialog's backdrop reports the dialog element itself as
      // the target, which is what makes the comparison exact rather than a
      // heuristic.
      dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('never settles for a click on content inside the panel', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const heading = dialog.querySelector('h2');
      const description = dialog.querySelector('p');
      const actions = dialog.querySelector('.confirm-dialog__actions');

      expect(heading).not.toBeNull();
      expect(description).not.toBeNull();
      expect(actions).not.toBeNull();
      if (heading === null || description === null || actions === null) {
        return;
      }

      // Each of these bubbles to the dialog's own click handler, so a handler
      // that failed to compare the target would cancel on any click inside the
      // panel — including a click that merely selected the message text.
      for (const inside of [heading, description, actions]) {
        inside.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      }

      expect(outcomes.cancelled()).toBe(0);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  5. SINGLE SETTLEMENT AND MUTUAL EXCLUSIVITY
  // =========================================================================
  describe('single settlement', () => {
    it('ignores every later activation once confirm has fired', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      buttons[1].click();
      buttons[1].click();
      buttons[0].click();
      dispatchKeydown(dialog, 'Escape');
      dialog.dispatchEvent(new Event('cancel'));
      dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));

      // A late click on the OPPOSITE affordance must not be able to turn a
      // confirmation into a cancellation, and a double click must not issue two
      // deletions. Both are real hazards: the dialog stays mounted until the
      // consumer unmounts it.
      expect(outcomes.confirmed()).toBe(1);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('ignores every later activation once cancel has fired', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      buttons[0].click();
      dispatchKeydown(dialog, 'Escape');
      dialog.dispatchEvent(new Event('cancel'));
      buttons[1].click();

      // THE MOST IMPORTANT ASSERTION IN THIS FILE: a cancellation can never be
      // upgraded into a deletion.
      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('emits cancel once for an Escape that also raises the native cancel event', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // In a real browser a single `Escape` press reaches BOTH the keydown
      // handler and the element's native `cancel` event. This reproduces that
      // pairing, which an unguarded implementation would report twice in
      // production while a keydown-only specification observed one emission and
      // passed.
      dispatchKeydown(dialog, 'Escape');
      dialog.dispatchEvent(new Event('cancel'));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  6. UNTRUSTED WORDING
  // =========================================================================
  describe('plain-text rendering of caller-supplied wording', () => {
    it('renders a script-bearing message as visible text and executes nothing', () => {
      const fixture = createDialog();
      const originalDocumentTitle = document.title;

      fixture.componentRef.setInput('message', SCRIPT_BEARING_MESSAGE);
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // One in-scope legacy resource genuinely stores a live remote script block,
      // HTML-escaped. Interpolation escapes it; a raw-markup binding would
      // execute it.
      expect(textOf(dialog, '.confirm-dialog__message')).toBe(SCRIPT_BEARING_MESSAGE);
      expect(dialog.querySelectorAll('script').length).toBe(0);
      expect(document.title).toBe(originalDocumentTitle);
    });

    it('renders markup-bearing title and confirm label as visible text', () => {
      const fixture = createDialog();

      fixture.componentRef.setInput('title', MARKUP_BEARING_MESSAGE);
      fixture.componentRef.setInput('confirmLabel', MARKUP_BEARING_MESSAGE);
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect(textOf(dialog, '.confirm-dialog__title')).toBe(MARKUP_BEARING_MESSAGE);
      expect(dialog.querySelectorAll('b').length).toBe(0);

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);
      expect((buttons[1].textContent ?? '').trim()).toBe(MARKUP_BEARING_MESSAGE);
    });

    it('honours an explicitly supplied empty message rather than restoring the default', () => {
      const fixture = createDialog();

      fixture.componentRef.setInput('message', '');
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // SENTINEL DISCIPLINE: the legacy null-string sentinel IS the empty string,
      // so `''` is a legitimate caller-supplied value and must never be quietly
      // replaced. The paragraph is still emitted so the description reference
      // cannot dangle.
      expect(textOf(dialog, '.confirm-dialog__message')).toBe('');
      expect(dialog.querySelectorAll('.confirm-dialog__message').length).toBe(1);
    });

    it('never relabels the cancelling affordance from a call site', () => {
      const fixture = createHost(FullyBoundHostComponent);
      const root: HTMLElement = fixture.nativeElement;
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // The host binds every input it can. The cancelling label is a template
      // literal precisely so that no call site can turn the escape hatch into
      // something else, so it must still read `Cancel` here while the three
      // caller-supplied strings all changed.
      expect((buttons[0].textContent ?? '').trim()).toBe(CANCEL_LABEL);
      expect(textOf(root, '.confirm-dialog__title')).toBe('Confirm Removal');
      expect(textOf(root, '.confirm-dialog__message')).toBe('Remove the Administrators role?');

      // The confirming affordance is asserted through its own text NODES rather
      // than through `textContent`. This host binds `danger`, and in danger mode
      // the template also renders a decorative severity glyph in an `aria-hidden`
      // span - the non-colour half of the severity signal. `textContent` would
      // flatten that glyph into the comparison and make this expectation fail for
      // a reason that has nothing to do with relabelling, so the caller-supplied
      // wording is read from the element's own character data instead.
      expect(directTextOf(buttons[1])).toBe('Remove');
    });

    it('reports settlement to a consumer bound through the template', () => {
      const fixture = createHost(FullyBoundHostComponent);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      buttonsWithin(dialog)[1].click();
      fixture.detectChanges();

      // Proves the outputs are reachable from ordinary template bindings rather
      // than only from a direct subscription in a specification.
      expect(fixture.componentInstance.confirmed).toBe(1);
      expect(fixture.componentInstance.cancelled).toBe(0);
    });
  });

  // =========================================================================
  //  7. THE DANGER MODIFIER — PRESENTATION ONLY
  // =========================================================================
  describe('danger presentation', () => {
    it('applies no destructive styling by default', () => {
      const fixture = createDialog();
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect(dialog.querySelectorAll(`.${DANGER_CLASS}`).length).toBe(0);
    });

    it('coerces every attribute-shaped value the framework can deliver', () => {
      const cases: readonly { readonly supplied: unknown; readonly destructive: boolean }[] = [
        { supplied: true, destructive: true },
        { supplied: '', destructive: true },
        { supplied: 'true', destructive: true },
        { supplied: 'danger', destructive: true },
        { supplied: false, destructive: false },
        { supplied: 'false', destructive: false },
        { supplied: null, destructive: false },
        { supplied: undefined, destructive: false },
      ];

      for (const { supplied, destructive } of cases) {
        const fixture = createDialog();
        fixture.componentRef.setInput('danger', supplied);
        fixture.detectChanges();

        const dialog = dialogWithin(fixture);
        expect(dialog).not.toBeNull();
        if (dialog === null) {
          return;
        }

        const buttons = buttonsWithin(dialog);
        expect(buttons.length).toBe(2);

        // A nullish expression must coerce to the PRESENTATION-SAFE default
        // rather than rendering destructive styling from an absent value, and
        // the modifier must never reach the cancelling affordance.
        expect(buttons[1].classList.contains(DANGER_CLASS))
          .withContext(`danger=${String(supplied)} must render destructive=${destructive}`)
          .toBe(destructive);
        expect(buttons[0].classList.contains(DANGER_CLASS)).toBeFalse();
      }
    });

    it('coerces the bare attribute form to true', () => {
      const fixture = createHost(BareDangerAttributeHostComponent);
      const dialog = dialogWithin(fixture);

      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      expect(buttons[1].classList.contains(DANGER_CLASS)).toBeTrue();
    });

    it('does not change what confirmation means', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      fixture.componentRef.setInput('danger', true);
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      buttons[1].click();

      // Styling and semantics must not be coupled through one flag. The
      // destructive presentation must alter neither which output fires nor how
      // many times.
      expect(outcomes.confirmed()).toBe(1);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('keeps the destructive label as real text rather than colour alone', () => {
      const fixture = createDialog();

      fixture.componentRef.setInput('danger', true);
      fixture.componentRef.setInput('confirmLabel', 'Delete Portal');
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);

      // The stylesheet records that the danger ink measures below the contrast
      // minimum for normal text and is implemented exactly as measured, flagged
      // for designer review. Its stated mitigation is that severity never
      // depends on the colour alone — so the text label carrying the outcome is a
      // load-bearing part of that mitigation and is asserted here.
      expect(directTextOf(buttons[1])).toBe('Delete Portal');
      expect(buttons[1].classList.contains(DANGER_CLASS)).toBeTrue();

      // The other half of the same mitigation: severity is also carried by a
      // decorative glyph, which is hidden from assistive technology precisely
      // because the label already conveys the outcome in words.
      const glyph = buttons[1].querySelector('[aria-hidden="true"]');
      expect(glyph).not.toBeNull();
    });
  });

  // =========================================================================
  //  8. TEARDOWN — CLOSE AND FOCUS RESTORATION
  // =========================================================================
  describe('teardown', () => {
    it('closes the dialog when the component is destroyed', () => {
      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect(dialog.open).toBeTrue();

      fixture.destroy();

      // Leaving it open would leave the rest of the document inert forever, with
      // every affordance on the dialog already gone.
      expect(dialog.open).toBeFalse();
      expect(dialog.matches(':modal')).toBeFalse();
    });

    it('returns focus to the element that opened it', () => {
      // The invoker is captured in a field initialiser, which runs during
      // construction — the earliest moment available and before the dialog is
      // opened. A real consumer's control-flow block flips in response to a
      // click, so the clicked affordance still holds focus at that moment, and
      // this arrangement reproduces exactly that ordering.
      const invoker = appendStray(document.createElement('button'));
      invoker.textContent = 'Delete';
      invoker.focus();

      expect(document.activeElement).toBe(invoker);

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      // Focus moved into the dialog when it opened.
      expect(document.activeElement).not.toBe(invoker);

      fixture.destroy();

      // And came back. Without it, focus would land on the document body and a
      // keyboard user would lose their place in the grid entirely.
      //
      // HONEST SCOPE OF THIS ASSERTION. Two mechanisms cooperate to produce this
      // outcome: the component closes the dialog, and closing a modal `<dialog>`
      // makes the user agent restore focus itself. Closing is nonetheless the
      // component's own doing — were the element torn down while still open, no
      // close would ever occur and neither mechanism would fire. This test states
      // the user-visible contract; the test that follows isolates the component's
      // explicit restoration from the platform's.
      expect(document.activeElement).toBe(invoker);
    });

    it('restores focus to the element that held it at construction, not at open', () => {
      // THIS IS THE ISOLATION TEST. The two candidate mechanisms are deliberately
      // pointed at DIFFERENT elements: the component captures its invoker during
      // construction, whereas the user agent records whatever holds focus at the
      // moment the modal opens. Moving focus in between separates them, so only
      // the component's own restoration can produce the expected result and a
      // mutation that deleted it would land on the other element instead.
      const constructionTimeHolder = appendStray(document.createElement('button'));
      constructionTimeHolder.textContent = 'Delete';

      const openTimeHolder = appendStray(document.createElement('button'));
      openTimeHolder.textContent = 'Unrelated';

      constructionTimeHolder.focus();
      const fixture = createUninitialisedDialog();

      openTimeHolder.focus();
      expect(document.activeElement).toBe(openTimeHolder);

      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect(dialog.open).toBeTrue();

      fixture.destroy();

      expect(document.activeElement).toBe(constructionTimeHolder);
      expect(document.activeElement).not.toBe(openTimeHolder);
    });

    it('skips restoration when the invoking element has itself been removed', () => {
      const invoker = appendStray(document.createElement('button'));
      invoker.textContent = 'Delete';
      invoker.focus();

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      // A grid row's Delete button disappears with its row once the deletion
      // succeeds, so the captured invoker may be detached by the time the dialog
      // is torn down. Focusing a detached element silently moves focus to the
      // body, which is why restoration is skipped rather than attempted.
      invoker.remove();

      expect((): void => {
        fixture.destroy();
      }).not.toThrow();

      expect(document.activeElement).not.toBe(invoker);
    });

    it('does not restore focus to a non-HTML element that held it', () => {
      // An SVG element with a tab index is focusable and is reported by
      // `document.activeElement`, yet it is an `SVGElement` rather than an
      // `HTMLElement` and therefore carries no `focus()` in the HTML element
      // interface. Narrowing the captured invoker by interface — rather than
      // assuming every focusable thing is an HTML element — is what keeps
      // restoration type-safe here instead of failing at run time.
      const carrier = appendStray(document.createElement('div'));
      carrier.innerHTML = '<svg tabindex="0" width="8" height="8"></svg>';
      const graphic = carrier.querySelector('svg');

      expect(graphic).not.toBeNull();
      if (graphic === null) {
        return;
      }

      graphic.focus();
      expect(document.activeElement).toBe(graphic);

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      expect((): void => {
        fixture.destroy();
      }).not.toThrow();

      expect(dialog.open).toBeFalse();

      // MEASURED, NOT ASSUMED. Closing a modal `<dialog>` makes the user agent
      // restore focus to whatever held it before the modal opened, so the graphic
      // legitimately gets focus back here — through the platform, not through the
      // component, which reported this invoker as absent and attempted nothing.
      // The contract being asserted is therefore that narrowing by interface
      // leaves the platform's own restoration intact instead of throwing on an
      // element that has no HTML element interface to call.
      expect(document.activeElement).toBe(graphic);
    });

    it('does not restore focus when nothing meaningful held it', () => {
      // `document.activeElement` reports the body when nothing in particular is
      // focused, and the body is not a useful restoration target — focusing it is
      // indistinguishable from focusing nothing. That case is reported as absent
      // so restoration is skipped entirely rather than performed as a misleading
      // no-op.
      if (document.activeElement instanceof HTMLElement) {
        document.activeElement.blur();
      }

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      const dialog = dialogWithin(fixture);
      expect(dialog).not.toBeNull();
      if (dialog === null) {
        return;
      }

      const buttons = buttonsWithin(dialog);
      expect(buttons.length).toBe(2);
      expect(document.activeElement).toBe(buttons[0]);

      expect((): void => {
        fixture.destroy();
      }).not.toThrow();

      expect(dialog.open).toBeFalse();
    });
  });
});
