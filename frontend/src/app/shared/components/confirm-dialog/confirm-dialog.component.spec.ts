// THERE IS NO PREDECESSOR SUITE
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated tests of any kind - not a
// unit test, not a fixture, not a test project, nothing anywhere beneath `Library/` or `Website/`. Every
// expectation below is net-new coverage with no legacy assertion to port and nothing to translate.

import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ChangeDetectionStrategy, Component, type Type } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ConfirmDialogComponent } from './confirm-dialog.component';

// ---------------------------------------------------------------------------
// Measured wording
// ---------------------------------------------------------------------------

/**
 * The defaults the component declares, restated so a silent change to any of them fails a specification
 * instead of only changing the rendered output.
 */
const DEFAULT_TITLE = 'Confirm Delete';
const DEFAULT_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const DEFAULT_CONFIRM_LABEL = 'Delete';

/**
 * The cancelling label, authored in the template rather than exposed as an input. Localisation is not
 * ported, so no resource lookup and no localisation attribute is emitted; the wording lives in the
 * component.
 */
const CANCEL_LABEL = 'Cancel';

// ---------------------------------------------------------------------------
// Selectors
// ---------------------------------------------------------------------------

const PANEL_SELECTOR = '.confirm-dialog__panel';
const TITLE_SELECTOR = '.confirm-dialog__title';
const MESSAGE_SELECTOR = '.confirm-dialog__message';
const ACTIONS_SELECTOR = '.confirm-dialog__actions';
const BUTTON_SELECTOR = '.confirm-dialog__button';
const DANGER_CLASS = 'confirm-dialog__button--danger';
const BLOCK_CLASS = 'confirm-dialog';
const DECORATIVE_SELECTOR = '[aria-hidden="true"]';

/**
 * Claims the dialog must never invent. The legacy confirmation promised nothing about permanence, and two
 * of the flows this dialog guards do not delete anything: removing a module is a soft delete that leaves
 * the row in place, and withdrawing a paid role assignment whose trial has been consumed expires the
 * assignment instead.
 */
const UNSUPPORTED_CLAIMS: readonly string[] = [
  'cannot be undone',
  'permanently',
  'irreversible',
  'will not be able to recover',
];

/**
 * Landmarks and their ARIA role equivalents, none of which this component emits. Landmarks belong
 * exclusively to the application shell under `layout/`.
 */
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

// Untrusted wording fixtures - measured, not invented

/** The shape of the one in-scope resource value that holds a live script block. */
const SCRIPT_PAYLOAD = '<script>alert(1)</script>';

/** A second script shape, closer to the measured remote-advertising entry. */
const REMOTE_SCRIPT_PAYLOAD = '<script type="text/javascript"><!--\nad_output = "textlink";\n//--></script>';

/** Inline emphasis, the shape of `EditRoles.ascx.resx` -> `ProcessorWarning.Text`. */
const BOLD_PAYLOAD = '<b>Warning:</b> configure the payment processor';

/** A single leading break, the commonest of the 31 measured leading-break values. */
const LEADING_BREAK_PAYLOAD = '<br>Deleted';

const ACCUMULATED_BREAK_PAYLOAD = '<br><br/><br><br/><br><br>Portal name contains invalid characters';

/**
 * Values that are present but carry no announceable name. Both forms have to be covered, and covering
 * only the empty one is the specific gap that made this a finding: a length check alone accepts `' '`,
 * which the accessible-name computation collapses to exactly the same nothing as `''`.
 */
const BLANK_NAMES: readonly string[] = ['', '   ', '\t', '\n', ' \t\n '];

// Narrowing helpers

/** Rendered text of an element, normalising the nullable `textContent`. */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * An element's OWN character data, ignoring descendant elements. `textContent` walks the whole subtree,
 * so on the confirming affordance it also picks up the decorative severity glyph that danger mode
 * renders.
 */
function directTextOf(element: Element): string {
  return Array.from(element.childNodes)
    .filter((node): node is Text => node.nodeType === Node.TEXT_NODE)
    .map((node: Text): string => node.textContent ?? '')
    .join('')
    .trim();
}

/** The narrowed root element of a fixture. */
function rootOf(fixture: ComponentFixture<unknown>): HTMLElement {
  const root: unknown = fixture.nativeElement;

  if (!(root instanceof HTMLElement)) {
    throw new Error('Expected the fixture to render an HTML root element.');
  }

  return root;
}

/** A required descendant, narrowed to `HTMLElement`. */
function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found = root.querySelector(selector);

  if (!(found instanceof HTMLElement)) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

function requireDialog(root: ParentNode): HTMLDialogElement {
  const found = root.querySelector('dialog');

  if (!(found instanceof HTMLDialogElement)) {
    throw new Error('Expected the component to render exactly one native dialog element.');
  }

  return found;
}

/** Every affordance beneath a root, in document order, narrowed element by element. */
function requireButtons(root: ParentNode): readonly HTMLButtonElement[] {
  const collected: HTMLButtonElement[] = [];

  root.querySelectorAll(BUTTON_SELECTOR).forEach((candidate: Element): void => {
    if (candidate instanceof HTMLButtonElement) {
      collected.push(candidate);
    }
  });

  if (collected.length !== 2) {
    throw new Error(`Expected exactly two affordances, found ${collected.length}.`);
  }

  return collected;
}

/**
 * The element another element references by id, proving the reference resolves. This is the mechanism
 * behind the accessible-name expectations.
 */
function requireReferencedElement(root: ParentNode, referencedId: string): HTMLElement {
  return requireElement(root, `#${CSS.escape(referencedId)}`);
}

/** A required attribute, narrowed to a non-nullable string. */
function requireAttribute(element: Element, attributeName: string): string {
  const value: string | null = element.getAttribute(attributeName);

  if (value === null) {
    throw new Error(`Expected the element to declare a "${attributeName}" attribute.`);
  }

  return value;
}

/**
 * Dispatches a keydown the way a real one arrives, and hands the event back. Three details are
 * load-bearing.
 */
function pressKey(target: EventTarget, key: string, shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true });
  target.dispatchEvent(event);

  return event;
}

/** Dispatches a bubbling click, for the paths a `.click()` call cannot reach. */
function dispatchClick(target: EventTarget): void {
  target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
}

/** Dispatches a bubbling click carrying viewport coordinates. */
function dispatchClickAt(target: EventTarget, clientX: number, clientY: number): void {
  target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX, clientY }));
}

// Consumer call sites
// Each host below reproduces a call-site SHAPE that a bare fixture cannot: an invoker that genuinely held
// focus, a static attribute rather than a property binding, a non-HTML focus holder, and a template that
// binds every input and both outputs.

/**
 * A grid-style call site: a real Delete button that mounts the dialog. Focus restoration cannot be proven
 * from a bare fixture, because the component captures its invoker by reading the focused element during
 * construction and in a bare fixture nothing is focused - so the capture correctly reports "absent" and
 * there is nothing to restore.
 */
@Component({
  selector: 'app-invoker-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <button type="button" id="invoker">Delete portal</button>
    @if (open) {
      <app-confirm-dialog />
    }
  `,
})
class InvokerHostComponent {
  public open = false;
}

/**
 * TWO dialogs mounted at once, from a single call site. Element ids must be unique across the whole
 * DOCUMENT, not merely within one component, because `aria-labelledby` resolves against the document.
 */
@Component({
  selector: 'app-paired-dialog-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <app-confirm-dialog />
    <app-confirm-dialog />
  `,
})
class PairedDialogHostComponent {}

@Component({
  selector: 'app-graphic-invoker-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <svg id="graphic-invoker" tabindex="0" width="8" height="8"></svg>
    @if (open) {
      <app-confirm-dialog />
    }
  `,
})
class GraphicInvokerHostComponent {
  public open = false;
}

/**
 * A call site writing `danger` as a BARE ATTRIBUTE. A property binding cannot exercise this path: the
 * bare form arrives at the input as the empty string, and only the declared boolean coercion turns it
 * into `true`.
 */
@Component({
  selector: 'app-bare-danger-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `<app-confirm-dialog danger></app-confirm-dialog>`,
})
class BareDangerAttributeHostComponent {}

/**
 * A call site writing `title` as a STATIC TEMPLATE ATTRIBUTE. The framework copies a static template
 * attribute onto the rendered element IN ADDITION to assigning the matching input, so this is the exact
 * shape that makes the `title` input's collision with the global HTML `title` attribute observable.
 */
@Component({
  selector: 'app-static-title-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `<app-confirm-dialog title="Remove Role"></app-confirm-dialog>`,
})
class StaticTitleAttributeHostComponent {}

/** A call site binding every input and both outputs through the template. */
@Component({
  selector: 'app-fully-bound-host',
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
  public heading = 'Confirm Removal';
  public question = 'Remove the Administrators role?';
  public verb = 'Remove';
  public destructive = true;
  public confirmed = 0;
  public cancelled = 0;
}

// Contract-violating templates

/**
 * A template whose cancelling affordance has LOST its `#cancelButton` reference. The `@ViewChild` query
 * therefore resolves to nothing and `resolveInitialFocusTarget` falls through to the first
 * keyboard-focusable descendant.
 */
@Component({
  selector: 'app-reference-free-dialog',
  standalone: true,
  imports: [],
  template: `
    <dialog #dialogElement class="confirm-dialog">
      <button id="unreferenced-cancel" type="button">Cancel</button>
      <button id="unreferenced-confirm" type="button">Delete</button>
    </dialog>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ReferenceFreeDialogComponent extends ConfirmDialogComponent {}

/**
 * A template offering NO keyboard-focusable content at all. The reference query resolves to nothing and
 * the structural fallback finds nothing either, because the only candidate is `disabled` and so is
 * rejected by the tabbability filter.
 */
@Component({
  selector: 'app-focus-target-free-dialog',
  standalone: true,
  imports: [],
  template: `
    <dialog #dialogElement class="confirm-dialog">
      <p>No affordance here can take focus.</p>
      <button id="inert-affordance" type="button" disabled>Delete</button>
    </dialog>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class FocusTargetFreeDialogComponent extends ConfirmDialogComponent {}

// ---------------------------------------------------------------------------
// Specification
// ---------------------------------------------------------------------------

describe('ConfirmDialogComponent', () => {
  /** Every fixture created during a specification, torn down afterwards. */
  let fixtures: ComponentFixture<unknown>[] = [];

  /**
   * Focusable elements appended straight to the document, removed afterwards. Restoration can only be
   * observed against an element that is genuinely CONNECTED, and it cannot be observed against a second
   * fixture: creating one detaches the first, for the `insertRootElement` reason recorded above.
   */
  let focusHolders: HTMLButtonElement[] = [];

  /**
   * Main landmarks appended by a specification, removed after every one. The teardown fallback resolves
   * the region by document lookup, so a landmark left behind would leak into every later focus
   * expectation in the shared Karma document.
   */
  let mainRegions: HTMLElement[] = [];

  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        ConfirmDialogComponent,
        InvokerHostComponent,
        PairedDialogHostComponent,
        GraphicInvokerHostComponent,
        BareDangerAttributeHostComponent,
        StaticTitleAttributeHostComponent,
        FullyBoundHostComponent,
        ReferenceFreeDialogComponent,
        FocusTargetFreeDialogComponent,
      ],
      // The real client is registered FIRST and the testing backend SECOND, which is the documented order:
      // the testing backend replaces the real backend's transport while leaving the rest of the client
      // intact.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixtures = [];
    focusHolders = [];
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const fixture of fixtures) {
      // Destroying runs the component's own `ngOnDestroy`, which closes the dialog and restores focus.
      // `ComponentFixture.destroy()` is idempotent, so a specification that destroys its own fixture to
      // observe teardown is not penalised for it here.
      fixture.destroy();
    }
    fixtures = [];

    // Removed AFTER the fixtures, so that a component's own restoration has already been observed against a
    // connected holder, and removed at all so that no later specification inherits a focused element from
    // this one.
    for (const holder of focusHolders) {
      holder.remove();
    }
    focusHolders = [];

    // Removed after the fixtures for the same reason as the holders above: the teardown fallback has to
    // find a CONNECTED region for the assertion to mean anything, and no later specification may inherit
    // one.
    for (const region of mainRegions) {
      region.remove();
    }
    mainRegions = [];

    // The order-independence guard, asserted rather than assumed: an open modal left behind would make the
    // shared Karma document inert and would corrupt every later focus expectation in the whole run.
    expect(document.querySelectorAll('dialog[open]').length)
      .withContext('a specification left a modal dialog open in the shared document')
      .toBe(0);

    // Fails the specification if this component issued any request at all.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // Harness
  // -------------------------------------------------------------------------

  /** Creates the dialog WITHOUT initialising its view, registered for teardown. */
  const createUninitialisedDialog = (): ComponentFixture<ConfirmDialogComponent> => {
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    fixtures.push(fixture);

    return fixture;
  };

  /**
   * Creates and renders the dialog, applying any inputs before the first pass. Inputs are applied through
   * `setInput` before the initial `detectChanges()`, so the component opens with the values a real call
   * site would already have bound.
   */
  const createDialog = (inputs: Readonly<Record<string, unknown>> = {}): ComponentFixture<ConfirmDialogComponent> => {
    const fixture = createUninitialisedDialog();

    Object.entries(inputs).forEach(([name, value]: readonly [string, unknown]): void => {
      fixture.componentRef.setInput(name, value);
    });

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

  /** A connected, focusable element outside every fixture root, tracked for removal. */
  const appendFocusHolder = (label: string): HTMLButtonElement => {
    const holder = document.createElement('button');
    holder.type = 'button';
    holder.textContent = label;
    document.body.appendChild(holder);
    focusHolders.push(holder);

    return holder;
  };

  /**
   * A connected main landmark, focusable but untabbable exactly as the shell renders it. Tracked for
   * removal.
   */
  const appendMainRegion = (): HTMLElement => {
    const region = document.createElement('main');
    region.tabIndex = -1;
    document.body.appendChild(region);
    mainRegions.push(region);

    return region;
  };

  const dialogOf = (fixture: ComponentFixture<unknown>): HTMLDialogElement => requireDialog(rootOf(fixture));

  /** Both affordances of a fixture, in document order. */
  const buttonsOf = (fixture: ComponentFixture<unknown>): readonly HTMLButtonElement[] =>
    requireButtons(rootOf(fixture));

  /** The cancelling affordance, which the template renders first. */
  const cancelButtonOf = (fixture: ComponentFixture<unknown>): HTMLButtonElement => buttonsOf(fixture)[0];

  /** The confirming affordance, which the template renders second. */
  const confirmButtonOf = (fixture: ComponentFixture<unknown>): HTMLButtonElement => buttonsOf(fixture)[1];

  /**
   * How many times each output has fired. Counters rather than booleans, because every interesting
   * invariant here is about COUNTS: "emitted at most once" cannot be distinguished from "emitted" by a
   * boolean, and a double emission is precisely the defect the emit-once guard exists to prevent.
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
  //  1. CREATION, MODALITY AND STRUCTURE
  // =========================================================================
  describe('creation, modality and structure', () => {
    it('creates as a standalone component with no module of any kind', () => {
      const fixture = createDialog();

      expect(fixture.componentInstance).toBeInstanceOf(ConfirmDialogComponent);
    });

    it('renders exactly one native dialog element carrying the block class', () => {
      const fixture = createDialog();
      const root = rootOf(fixture);

      expect(root.querySelectorAll('dialog').length).toBe(1);
      expect(dialogOf(fixture).classList.contains(BLOCK_CLASS)).toBeTrue();
    });

    it('opens the dialog modally, so the rest of the document is inert', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);

      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
    });

    it('declares no static open attribute in the template', () => {
      const fixture = createUninitialisedDialog();
      const root = rootOf(fixture);
      const beforeInitialisation = requireDialog(root);

      expect(beforeInitialisation.hasAttribute('open')).toBeFalse();
    });

    it('declares the alert-dialog role on the dialog rather than on the host', () => {
      const fixture = createDialog();

      expect(requireAttribute(dialogOf(fixture), 'role')).toBe('alertdialog');
      expect(rootOf(fixture).getAttribute('role')).toBeNull();
    });

    it('declares itself modal to assistive technology', () => {
      const fixture = createDialog();

      expect(requireAttribute(dialogOf(fixture), 'aria-modal')).toBe('true');
      expect(rootOf(fixture).getAttribute('aria-modal')).toBeNull();
    });

    it('renders exactly two affordances and no third', () => {
      const fixture = createDialog();

      expect(rootOf(fixture).querySelectorAll('button').length).toBe(2);
    });

    it('places the safe action before the destructive one in document order', () => {
      // Two independent reasons, so this is a requirement rather than a preference. Safety: the user
      // agent's own first-focusable heuristic then lands on Cancel.
      const fixture = createDialog();
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(directTextOf(cancelButton)).toBe(CANCEL_LABEL);
      expect(directTextOf(confirmButton)).toBe(DEFAULT_CONFIRM_LABEL);

      const relationship = cancelButton.compareDocumentPosition(confirmButton);

      expect(relationship & Node.DOCUMENT_POSITION_FOLLOWING)
        .withContext('the cancelling affordance must precede the confirming one')
        .toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    });

    it('declares every affordance as a non-submitting button', () => {
      for (const button of buttonsOf(createDialog())) {
        expect(button.type)
          .withContext(`"${directTextOf(button)}" must not submit a form`)
          .toBe('button');
      }
    });

    it('gives every affordance real, visible, readable text', () => {
      // `Website/admin/Security/roles.ascx` L13 rendered its delete image button with no alternate text, no
      // resource key and no text at all - two lines below an edit image at L11 carrying BOTH
      // `AlternateText` and `resourcekey`.
      for (const button of buttonsOf(createDialog())) {
        expect(directTextOf(button).length)
          .withContext('an affordance with no text is unusable by a screen reader')
          .toBeGreaterThan(0);
      }
    });

    it('renders no image element', () => {
      // `~/images/delete.gif` is not ported; the only static asset this
      // application ships is the favicon, so the severity signal has to be text.
      const fixture = createDialog({ danger: true });

      expect(rootOf(fixture).querySelector('img')).toBeNull();
    });

    it('declares no autofocus and no author-supplied tab index anywhere', () => {
      // Focus placement is the component's own decision, made in a lifecycle hook where the cancelling
      // affordance can be chosen deliberately. An `autofocus` attribute would hand that decision to the
      // user agent's document order, and an author-supplied tab index would reorder the trap unpredictably.
      const fixture = createDialog({ danger: true });
      const root = rootOf(fixture);

      expect(root.querySelector('[autofocus]')).toBeNull();
      expect(root.querySelector('[tabindex]')).toBeNull();
    });

    it('strips the colliding title attribute from the host at a static call site', () => {
      const fixture = createHost(StaticTitleAttributeHostComponent);
      const root = rootOf(fixture);
      const host = root.querySelector('app-confirm-dialog');

      expect(host).not.toBeNull();
      if (host === null) {
        return;
      }

      expect(host.hasAttribute('title')).toBeFalse();
      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Remove Role');
    });

    it('gives two concurrently mounted dialogs non-colliding element identifiers', () => {
      // The ids come from a monotonic instance counter precisely so that two dialogs cannot both claim the
      // same `aria-labelledby` target - duplicate ids would break the accessible name of both.
      const fixture = createHost(PairedDialogHostComponent);
      const mounted = Array.from(rootOf(fixture).querySelectorAll('app-confirm-dialog'));

      expect(mounted.length).toBe(2);
      const [first, second] = mounted;
      const firstTitleId = requireElement(first, TITLE_SELECTOR).id;
      const secondTitleId = requireElement(second, TITLE_SELECTOR).id;

      expect(firstTitleId.length).toBeGreaterThan(0);
      expect(secondTitleId).not.toBe(firstTitleId);
      expect(requireElement(second, MESSAGE_SELECTOR).id).not.toBe(requireElement(first, MESSAGE_SELECTOR).id);

      // The consequence that matters, asserted directly: each identifier resolves
      // to exactly one node in the whole document.
      expect(document.querySelectorAll(`#${CSS.escape(firstTitleId)}`).length).toBe(1);
      expect(document.querySelectorAll(`#${CSS.escape(secondTitleId)}`).length).toBe(1);
    });
  });

  // =========================================================================
  //  2. (a) INITIAL FOCUS IS INSIDE THE DIALOG, NEVER ON THE DESTRUCTIVE ACTION
  // =========================================================================
  describe('(a) initial focus placement', () => {
    it('places focus inside the dialog when it opens', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const focused = document.activeElement;

      expect(focused).not.toBeNull();
      if (focused === null) {
        return;
      }

      expect(dialog.contains(focused))
        .withContext('focus must be inside the dialog, not left behind it')
        .toBeTrue();
      expect(focused).not.toBe(document.body);
    });

    it('places initial focus on the cancelling affordance, never the destructive one', () => {
      // THE SAFETY GUARANTEE. If focus rested on the destructive affordance, an immediate `Enter` or
      // `Space` - the reflex of a user who did not expect a dialog - would delete the record.
      const fixture = createDialog();
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(document.activeElement).toBe(cancelButton);
      expect(document.activeElement).not.toBe(confirmButton);
      expect(document.activeElement).not.toBe(document.body);
    });

    it('still avoids the destructive affordance when danger is enabled', () => {
      // Severity styling must not tempt anyone into focusing the destructive
      // action "because it is the primary one".
      const fixture = createDialog({ danger: true });

      expect(document.activeElement).toBe(cancelButtonOf(fixture));
      expect(document.activeElement).not.toBe(confirmButtonOf(fixture));
    });
  });

  // 3. (b) THE FOCUS TRAP WRAPS AT BOTH BOUNDARIES.
  describe('(b) focus trap boundaries', () => {
    it('wraps forward from the last focusable element to the first', () => {
      const fixture = createDialog();
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      confirmButton.focus();
      expect(document.activeElement).toBe(confirmButton);

      const event = pressKey(confirmButton, 'Tab');

      // Suppressing the default is half the behaviour: without it the browser
      // would also move focus and the two movements would fight.
      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('wraps backward from the first focusable element to the last', () => {
      const fixture = createDialog();
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      cancelButton.focus();
      expect(document.activeElement).toBe(cancelButton);

      const event = pressKey(cancelButton, 'Tab', true);

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(confirmButton);
    });

    it('leaves a forward move away from the boundary entirely to the browser', () => {
      const fixture = createDialog();
      const cancelButton = cancelButtonOf(fixture);

      cancelButton.focus();

      const event = pressKey(cancelButton, 'Tab');

      expect(event.defaultPrevented).toBeFalse();
    });

    it('leaves a backward move away from the boundary entirely to the browser', () => {
      const fixture = createDialog();
      const confirmButton = confirmButtonOf(fixture);

      confirmButton.focus();

      const event = pressKey(confirmButton, 'Tab', true);

      expect(event.defaultPrevented).toBeFalse();
    });

    it('resolves the wrap origin from the focused element when the event targets the dialog', () => {
      // A synthetic event may be aimed at the dialog while focus genuinely rests on a button, which is the
      // documented fallback path. Without it a dispatched event would resolve no origin at all and the wrap
      // would never fire.
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      confirmButton.focus();

      const event = pressKey(dialog, 'Tab');

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('does nothing at all when no wrap origin can be established', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      // HOW THIS STATE IS REACHED, because the obvious route does not work.
      cancelButton.blur();

      expect(document.activeElement).not.toBe(cancelButton);
      expect(document.activeElement).not.toBe(confirmButton);

      // Aimed at the dialog, which is not itself a focus candidate, so neither the
      // target nor the focused element is a member of the focusable set.
      const event = pressKey(dialog, 'Tab');

      // Asserting that the default was NOT prevented is the discriminating half:
      // an unresolvable origin must not be mistaken for a boundary.
      expect(event.defaultPrevented).toBeFalse();
      expect(document.activeElement).not.toBe(confirmButton);
    });

    it('does nothing when the dialog renders nothing focusable', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);

      requireElement(dialog, ACTIONS_SELECTOR).remove();

      const event = pressKey(dialog, 'Tab');

      // No boundary exists, so there is nothing to wrap at and no reason to
      // interfere; the native confinement from `showModal()` still applies.
      expect(event.defaultPrevented).toBeFalse();
      expect(dialog.querySelectorAll('button').length).toBe(0);
    });

    it('excludes candidates that match the selector but cannot be tabbed to', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      // Every element below matches the component's focusable-candidate selector, and every one is appended
      // AFTER the action row - so if any were counted, the confirming affordance would no longer be the
      // last focusable element and the forward wrap at the end of this specification would not fire.
      const disabled = document.createElement('button');
      disabled.disabled = true;
      disabled.textContent = 'Disabled';

      const hiddenAttribute = document.createElement('button');
      hiddenAttribute.hidden = true;
      hiddenAttribute.textContent = 'Hidden';

      // Not rendered at all, which is the only reliable test for "no box",
      // whatever the cause.
      const notRendered = document.createElement('button');
      notRendered.style.display = 'none';
      notRendered.textContent = 'Not rendered';

      // Programmatically focusable but deliberately not tabbable, so it must never
      // become a wrap boundary.
      const negativeTabIndex = document.createElement('div');
      negativeTabIndex.setAttribute('tabindex', '-1');
      negativeTabIndex.textContent = 'Not tabbable';

      const notEditable = document.createElement('div');
      notEditable.setAttribute('contenteditable', 'false');
      notEditable.textContent = 'Not editable';

      // Matches the bare `input` candidate, because a selector-level type filter
      // would miss an input whose type was set as a property.
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

      confirmButton.focus();

      const event = pressKey(confirmButton, 'Tab');

      expect(event.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('does treat a genuinely focusable appended element as the new boundary', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      const included = document.createElement('button');
      included.textContent = 'Included';
      dialog.appendChild(included);

      confirmButton.focus();
      const fromConfirm = pressKey(confirmButton, 'Tab');

      // The confirming affordance is no longer the last focusable element, so it is
      // no longer a boundary and the wrap must NOT fire from it.
      expect(fromConfirm.defaultPrevented).toBeFalse();

      included.focus();
      const fromAppended = pressKey(included, 'Tab');

      expect(fromAppended.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('leaves every key other than Tab and Escape entirely to the browser', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const cancelButton = cancelButtonOf(fixture);

      for (const key of ['Enter', ' ', 'a', 'ArrowDown', 'Esc', 'escape', 'Escapee']) {
        const event = pressKey(cancelButton, key);

        expect(event.defaultPrevented)
          .withContext(`"${key}" must be left entirely to the browser`)
          .toBeFalse();
      }

      expect(outcomes.cancelled()).toBe(0);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  4. (c) ESCAPE CANCELS EXACTLY ONCE AND NEVER CONFIRMS
  // =========================================================================
  describe('(c) Escape dismissal', () => {
    it('emits cancel exactly once and never emits confirm', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      pressKey(cancelButtonOf(fixture), 'Escape');

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('suppresses the user agent’s own close request', () => {
      const fixture = createDialog();

      const event = pressKey(cancelButtonOf(fixture), 'Escape');

      expect(event.defaultPrevented).toBeTrue();
    });

    it('stays at exactly one emission when Escape is pressed repeatedly', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const cancelButton = cancelButtonOf(fixture);

      pressKey(cancelButton, 'Escape');
      pressKey(cancelButton, 'Escape');
      pressKey(cancelButton, 'Escape');

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('stays at exactly one emission when Escape also raises the native cancel event', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);

      pressKey(cancelButtonOf(fixture), 'Escape');
      dialog.dispatchEvent(new Event('cancel', { bubbles: false, cancelable: true }));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('accepts Escape from anywhere inside the dialog, because the handler is on the host', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      // The message paragraph is not focusable and is not an affordance; the event
      // still has to reach the host by bubbling.
      pressKey(requireElement(dialogOf(fixture), MESSAGE_SELECTOR), 'Escape');

      expect(outcomes.cancelled()).toBe(1);
    });
  });

  // =========================================================================
  //  5. (d) THE CANCELLING AFFORDANCE
  // =========================================================================
  describe('(d) the cancelling affordance', () => {
    it('emits cancel exactly once when activated', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      cancelButtonOf(fixture).click();

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('carries the authored cancel wording', () => {
      const fixture = createDialog();

      expect(directTextOf(cancelButtonOf(fixture))).toBe(CANCEL_LABEL);
    });

    it('cannot be relabelled from a call site', () => {
      // The cancelling label is a template literal precisely so that no consumer can turn the escape hatch
      // into something else. This host binds every input it can, so the three caller-supplied strings all
      // change while this one must not.
      const fixture = createHost(FullyBoundHostComponent);
      const root = rootOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(directTextOf(cancelButton)).toBe(CANCEL_LABEL);
      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Confirm Removal');
      expect(renderedTextOf(requireElement(root, MESSAGE_SELECTOR)).trim()).toBe('Remove the Administrators role?');

      expect(directTextOf(confirmButton)).toBe('Remove');
    });

    it('goes inert once it has cancelled, so a second click cannot reach the handler', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(cancelButton.disabled).toBeFalse();
      expect(confirmButton.disabled).toBeFalse();

      cancelButton.click();
      fixture.detectChanges();

      expect(cancelButton.disabled).toBeTrue();
      expect(confirmButton.disabled).toBeTrue();
      expect(outcomes.cancelled()).toBe(1);
    });
  });

  // =========================================================================
  //  6. (e) THE CONFIRMING AFFORDANCE
  // =========================================================================
  describe('(e) the confirming affordance', () => {
    it('emits confirm exactly once when activated, and never cancels', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      confirmButtonOf(fixture).click();

      expect(outcomes.confirmed()).toBe(1);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('stays at exactly one emission when activated repeatedly', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const confirmButton = confirmButtonOf(fixture);

      confirmButton.click();
      confirmButton.click();
      confirmButton.click();

      expect(outcomes.confirmed()).toBe(1);
    });

    it('does not close the dialog when it settles, because teardown is the consumer’s', () => {
      const fixture = createDialog();

      confirmButtonOf(fixture).click();
      fixture.detectChanges();

      expect(dialogOf(fixture).open).toBeTrue();
    });

    it('reports settlement to a consumer bound through the template', () => {
      // Proves the outputs are reachable from ordinary template bindings rather
      // than only from a direct subscription inside a specification.
      const fixture = createHost(FullyBoundHostComponent);

      confirmButtonOf(fixture).click();
      fixture.detectChanges();

      expect(fixture.componentInstance.confirmed).toBe(1);
      expect(fixture.componentInstance.cancelled).toBe(0);
    });
  });

  // The two outcomes are mutually exclusive, which is a safety property rather than a tidiness one: a late
  // click on the opposite affordance must not be able to turn a cancellation into a deletion.
  describe('single settlement across every route', () => {
    it('ignores a later cancellation once confirm has fired', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      confirmButton.click();
      cancelButton.click();
      pressKey(cancelButton, 'Escape');
      dispatchClick(dialogOf(fixture));

      expect(outcomes.confirmed()).toBe(1);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('ignores a later confirmation once cancel has fired', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      cancelButton.click();
      confirmButton.click();

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });
  });

  // =========================================================================
  //  8. DISMISSALS THE PLATFORM ORIGINATES
  // =========================================================================
  describe('dismissals the platform originates', () => {
    it('cancels when a click lands on the backdrop', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);
      const bounds = dialog.getBoundingClientRect();
      const outsideX = bounds.left - 5;
      const outsideY = bounds.top - 5;

      // Self-validating. A dialog that measured zero would make every point "outside" and this expectation
      // would pass for the wrong reason, so the real box is asserted first and the point is then checked
      // against all four edges.
      expect(bounds.width).toBeGreaterThan(0);
      expect(bounds.height).toBeGreaterThan(0);
      expect(outsideX < bounds.left || outsideX > bounds.right).toBeTrue();
      expect(outsideY < bounds.top || outsideY > bounds.bottom).toBeTrue();

      dispatchClickAt(dialog, outsideX, outsideY);

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('never settles for a click on the dialog frame inside its own bounds', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);
      const bounds = dialog.getBoundingClientRect();

      // A real, laid-out modal, or the comparison below would be vacuous.
      expect(bounds.width).toBeGreaterThan(0);
      expect(bounds.height).toBeGreaterThan(0);

      // Points just inside each edge, and the centre - all reported as the dialog.
      const insidePoints: readonly (readonly [number, number])[] = [
        [bounds.left + 1, bounds.top + 1],
        [bounds.right - 1, bounds.top + 1],
        [bounds.left + 1, bounds.bottom - 1],
        [bounds.right - 1, bounds.bottom - 1],
        [bounds.left + bounds.width / 2, bounds.top + bounds.height / 2],
      ];

      for (const [clientX, clientY] of insidePoints) {
        dispatchClickAt(dialog, clientX, clientY);
      }

      expect(outcomes.cancelled()).toBe(0);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('wraps the dialog content in a panel that carries the inset', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);
      const panel = requireElement(dialog, PANEL_SELECTOR);

      expect(dialog.children.length).toBe(1);
      expect(dialog.children[0]).toBe(panel);

      for (const selector of [TITLE_SELECTOR, MESSAGE_SELECTOR, ACTIONS_SELECTOR]) {
        expect(panel.contains(requireElement(dialog, selector)))
          .withContext(`${selector} must sit inside the panel, not beside it`)
          .toBeTrue();
      }
    });

    it('never settles for a click on content inside the panel', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);

      for (const selector of [TITLE_SELECTOR, MESSAGE_SELECTOR, ACTIONS_SELECTOR]) {
        dispatchClick(requireElement(dialog, selector));
      }

      expect(outcomes.cancelled()).toBe(0);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('cancels when the element raises its own cancel event', () => {
      // The route for a dismissal the platform originates, where no keydown reaches
      // the component at all.
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);

      dialogOf(fixture).dispatchEvent(new Event('cancel', { bubbles: false, cancelable: true }));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('suppresses the default action of the element\u2019s own cancel event', () => {
      const fixture = createDialog();
      const cancelRequest = new Event('cancel', { bubbles: false, cancelable: true });

      dialogOf(fixture).dispatchEvent(cancelRequest);

      expect(cancelRequest.defaultPrevented).toBeTrue();
    });

    it('stays open after the element raises its own cancel event', () => {
      const fixture = createDialog();
      const dialog = dialogOf(fixture);

      dialog.dispatchEvent(new Event('cancel', { bubbles: false, cancelable: true }));

      expect(dialog.open).toBeTrue();
    });

    it('emits exactly once for a single cancel event and never confirms', () => {
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);

      dialog.dispatchEvent(new Event('cancel', { bubbles: false, cancelable: true }));

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
      expect(dialog.open).toBeTrue();
    });

    it('suppresses the default action even after it has already settled', () => {
      // The emit-once guard returns early on a second dismissal, so suppression has to happen BEFORE that
      // guard is consulted.
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);

      dialog.dispatchEvent(new Event('cancel', { bubbles: false, cancelable: true }));
      const secondRequest = new Event('cancel', { bubbles: false, cancelable: true });
      dialog.dispatchEvent(secondRequest);

      expect(secondRequest.defaultPrevented).toBeTrue();
      expect(outcomes.cancelled()).toBe(1);
      expect(dialog.open).toBeTrue();
    });

    it('suppresses the default action of a real Escape press as well', () => {
      const fixture = createDialog();

      const escape = pressKey(cancelButtonOf(fixture), 'Escape');

      expect(escape.defaultPrevented).toBeTrue();
      expect(dialogOf(fixture).open).toBeTrue();
    });
  });

  // 9. (f) ACCESSIBLE NAME AND DESCRIPTION.
  describe('(f) accessible name and description', () => {
    it('names the dialog through an id that resolves to a node carrying the title', () => {
      const fixture = createDialog();
      const root = rootOf(fixture);
      const labelledBy = requireAttribute(dialogOf(fixture), 'aria-labelledby');

      const named = requireReferencedElement(root, labelledBy);

      expect(renderedTextOf(named).trim()).toBe(DEFAULT_TITLE);
      expect(named).toBe(requireElement(root, TITLE_SELECTOR));
    });

    it('describes the dialog through an id that resolves to a node carrying the message', () => {
      const fixture = createDialog();
      const root = rootOf(fixture);
      const describedBy = requireAttribute(dialogOf(fixture), 'aria-describedby');

      const described = requireReferencedElement(root, describedBy);

      expect(renderedTextOf(described).trim()).toBe(DEFAULT_MESSAGE);
      expect(described).toBe(requireElement(root, MESSAGE_SELECTOR));
    });

    it('tracks caller-supplied wording through both resolved references', () => {
      const fixture = createDialog({ title: 'Delete role', message: 'The role will be removed.' });
      const root = rootOf(fixture);
      const dialog = dialogOf(fixture);

      const named = requireReferencedElement(root, requireAttribute(dialog, 'aria-labelledby'));
      const described = requireReferencedElement(root, requireAttribute(dialog, 'aria-describedby'));

      expect(renderedTextOf(named).trim()).toBe('Delete role');
      expect(renderedTextOf(described).trim()).toBe('The role will be removed.');
    });

    it('names the dialog with a first-level heading nowhere in sight', () => {
      const fixture = createDialog();
      const root = rootOf(fixture);

      expect(root.querySelector('h1')).toBeNull();
      expect(requireElement(root, TITLE_SELECTOR).tagName).toBe('H2');
    });

    it('keeps the description reference resolvable even for an explicitly empty message', () => {
      // SENTINEL DISCIPLINE. The legacy null-string sentinel IS the empty string, so `''` is a legitimate
      // caller-supplied value and must never be quietly swapped for the default.
      const fixture = createDialog({ message: '' });
      const root = rootOf(fixture);
      const describedBy = requireAttribute(dialogOf(fixture), 'aria-describedby');

      const described = requireReferencedElement(root, describedBy);

      expect(renderedTextOf(described).trim()).toBe('');
      expect(root.querySelectorAll(MESSAGE_SELECTOR).length).toBe(1);
    });

    it('keeps hostile wording addressable as an accessible name', () => {
      // Escaping must not break the naming wiring: the reference still has to
      // resolve, or the dialog would announce nothing at all.
      const fixture = createDialog({ title: SCRIPT_PAYLOAD });
      const root = rootOf(fixture);

      const named = requireReferencedElement(root, requireAttribute(dialogOf(fixture), 'aria-labelledby'));

      expect(renderedTextOf(named)).toContain(SCRIPT_PAYLOAD);
      expect(named.querySelector('script')).toBeNull();
    });

    // THE F2 GUARDS. A resolvable reference to an EMPTY node is worse than no reference at all, because it
    // suppresses the fallback the browser would otherwise compute and leaves the dialog announced as
    // nothing.
    BLANK_NAMES.forEach((blank: string): void => {
      // Rendered into each name so a failure identifies WHICH blank form broke,
      // and so the five cases cannot collide into one repeated spec name.
      const described = blank.length === 0 ? 'the empty string' : `the blank value ${JSON.stringify(blank)}`;

      it(`falls back to the default title for ${described}`, () => {
        const fixture = createDialog({ title: blank });
        const root = rootOf(fixture);
        const named = requireReferencedElement(root, requireAttribute(dialogOf(fixture), 'aria-labelledby'));

        expect(renderedTextOf(named).trim()).toBe(DEFAULT_TITLE);
        expect(renderedTextOf(named).trim().length).toBeGreaterThan(0);
      });

      it(`falls back to the default confirm label for ${described}`, () => {
        // The severity glyph beside the label is `aria-hidden`, precisely so it cannot act as a naming
        // source - which leaves a blank label with nothing at all to fall back on. An unnamed destructive
        // button is the worst outcome this component could produce.
        const fixture = createDialog({ confirmLabel: blank, danger: true });

        expect(directTextOf(confirmButtonOf(fixture))).toBe(DEFAULT_CONFIRM_LABEL);
        expect(renderedTextOf(confirmButtonOf(fixture)).trim().length).toBeGreaterThan(0);
      });
    });

    it('trims surrounding whitespace from both accessible names without altering the words', () => {
      // Trimming is not cosmetic: the accessible-name computation already collapses surrounding white
      // space, so a padded value and a trimmed one are announced identically.
      const fixture = createDialog({ title: '  Delete role  ', confirmLabel: '  Delete  ' });
      const root = rootOf(fixture);

      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Delete role');
      expect(directTextOf(confirmButtonOf(fixture))).toBe('Delete');
    });
  });

  describe('(g) caller wording is escaped, never rendered as markup', () => {
    it('renders inline markup in the message as literal text and creates no element', () => {
      const fixture = createDialog({ message: BOLD_PAYLOAD });
      const message = requireElement(rootOf(fixture), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(BOLD_PAYLOAD);
      expect(message.querySelector('b')).toBeNull();
      expect(message.children.length).toBe(0);
    });

    it('renders a script payload in the message as literal text and creates no element', () => {
      const fixture = createDialog({ message: SCRIPT_PAYLOAD });
      const message = requireElement(rootOf(fixture), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(SCRIPT_PAYLOAD);
      expect(message.querySelector('script')).toBeNull();
    });

    it('renders a remote-script payload in the message as literal text and creates no element', () => {
      // Modelled on the measured `Advertising.Text` value, which stores two live advertising script blocks
      // HTML-escaped. The real publisher identifier is deliberately not reproduced.
      const fixture = createDialog({ message: REMOTE_SCRIPT_PAYLOAD });
      const message = requireElement(rootOf(fixture), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain('ad_output = "textlink";');
      expect(message.querySelector('script')).toBeNull();
      expect(message.children.length).toBe(0);
    });

    it('renders a leading break in the message as literal text and creates no element', () => {
      const fixture = createDialog({ message: LEADING_BREAK_PAYLOAD });
      const message = requireElement(rootOf(fixture), MESSAGE_SELECTOR);

      expect(renderedTextOf(message)).toContain(LEADING_BREAK_PAYLOAD);
      expect(message.querySelector('br')).toBeNull();
    });

    it('renders several accumulated breaks, in both spellings, as literal text', () => {
      const fixture = createDialog({ message: ACCUMULATED_BREAK_PAYLOAD });
      const message = requireElement(rootOf(fixture), MESSAGE_SELECTOR);
      const rendered = renderedTextOf(message);

      expect(rendered).toContain(ACCUMULATED_BREAK_PAYLOAD);
      expect(rendered).toContain('<br>');
      expect(rendered).toContain('<br/>');
      expect(message.querySelector('br')).toBeNull();
      expect(message.children.length).toBe(0);
    });

    it('renders a script payload in the title as literal text and creates no element', () => {
      const fixture = createDialog({ title: SCRIPT_PAYLOAD });
      const title = requireElement(rootOf(fixture), TITLE_SELECTOR);

      expect(renderedTextOf(title)).toContain(SCRIPT_PAYLOAD);
      expect(title.querySelector('script')).toBeNull();
      expect(title.children.length).toBe(0);
    });

    it('renders a script payload in the confirm label as literal text and creates no element', () => {
      const fixture = createDialog({ confirmLabel: SCRIPT_PAYLOAD });
      const confirmButton = confirmButtonOf(fixture);

      expect(renderedTextOf(confirmButton)).toContain(SCRIPT_PAYLOAD);
      expect(confirmButton.querySelector('script')).toBeNull();
    });

    it('adds no script element anywhere in the rendered output', () => {
      // The whole-subtree form of the guarantee: whichever input carries the
      // payload, the component must never contribute an executable node.
      const fixture = createDialog({
        title: SCRIPT_PAYLOAD,
        message: REMOTE_SCRIPT_PAYLOAD,
        confirmLabel: BOLD_PAYLOAD,
        danger: true,
      });
      const root = rootOf(fixture);

      expect(root.querySelectorAll('script').length).toBe(0);
      expect(root.querySelectorAll('b').length).toBe(0);
      expect(root.querySelectorAll('br').length).toBe(0);
    });
  });

  describe('(h) the danger modifier is presentation only', () => {
    it('applies no destructive styling and renders no glyph by default', () => {
      const fixture = createDialog();
      const root = rootOf(fixture);

      expect(root.querySelectorAll(`.${DANGER_CLASS}`).length).toBe(0);
      expect(root.querySelector(DECORATIVE_SELECTOR)).toBeNull();
    });

    it('applies the destructive modifier to the confirming affordance alone', () => {
      const fixture = createDialog({ danger: true });
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(confirmButton.classList.contains(DANGER_CLASS)).toBeTrue();
      expect(cancelButton.classList.contains(DANGER_CLASS)).toBeFalse();
    });

    it('hides the decorative severity glyph from assistive technology', () => {
      // The glyph must not compete with the label as a naming source, nor announce the severity a second
      // time. It is also why severity never rests on colour alone.
      const fixture = createDialog({ danger: true });
      const glyph = requireElement(confirmButtonOf(fixture), DECORATIVE_SELECTOR);

      expect(requireAttribute(glyph, 'aria-hidden')).toBe('true');
    });

    it('keeps the outcome in words as well as in colour and glyph', () => {
      // The paired stylesheet records that the danger ink measures below the contrast minimum for normal
      // text and is implemented exactly as measured, flagged for designer review.
      const fixture = createDialog({ danger: true, confirmLabel: 'Delete Portal' });
      const confirmButton = confirmButtonOf(fixture);

      expect(directTextOf(confirmButton)).toBe('Delete Portal');
      expect(confirmButton.classList.contains(DANGER_CLASS)).toBeTrue();
      expect(confirmButton.querySelector(DECORATIVE_SELECTOR)).not.toBeNull();
    });

    it('inks the destructive label with the DANGER token and not the hover token', () => {
      // ⚠ THE MEASURED DEFECT. The rule named `--color-primary-hover` for its ink, in all three states,
      // while naming `--color-danger` for its border - so the destructive button rendered as a red-bordered
      // box with an ORDINARY BLUE LABEL, and the resting state was identical to the neighbouring Cancel
      // button's HOVER state.
      const fixture = createDialog({ danger: true, confirmLabel: 'Delete' });
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      const danger = getComputedStyle(confirmButton).color;
      const ordinary = getComputedStyle(cancelButton).color;

      // ⚠ THE LABEL AND THE BORDER READ IN ONE TOKEN, AND ONE IS ALL THE VOCABULARY HAS. A darkened sibling
      // for danger TEXT was declared briefly and is withdrawn: the colour vocabulary is closed at the nine
      // values the design specification enumerates, design-system compliance is the first precedence rule,
      // and accessibility is the third and is asked for "with zero visual change" - so a new hue is
      // precisely what may not be admitted on accessibility grounds.
      expect(danger).withContext('#FF0000, the vocabulary\'s only danger value').toBe('rgb(255, 0, 0)');
      expect(danger)
        .withContext('and NOT #25569A, which is the hover token this rule used to name')
        .not.toBe('rgb(37, 86, 154)');
      expect(danger)
        .withContext('so the two buttons cannot be mistaken for one another')
        .not.toBe(ordinary);
      expect(getComputedStyle(confirmButton).borderTopColor).toBe('rgb(255, 0, 0)');
    });

    it('coerces the bare attribute form to destructive', () => {
      const fixture = createHost(BareDangerAttributeHostComponent);

      expect(confirmButtonOf(fixture).classList.contains(DANGER_CLASS)).toBeTrue();
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
        const fixture = createDialog({ danger: supplied });
        const [cancelButton, confirmButton] = buttonsOf(fixture);

        expect(confirmButton.classList.contains(DANGER_CLASS))
          .withContext(`danger=${String(supplied)} must render destructive=${String(destructive)}`)
          .toBe(destructive);
        expect(cancelButton.classList.contains(DANGER_CLASS)).toBeFalse();
      }
    });

    it('emits confirm identically whether or not danger is enabled', () => {
      const plain = createDialog();
      const plainOutcomes = observeOutcomes(plain);
      confirmButtonOf(plain).click();

      // Torn down before the comparison case is built. Each dialog is therefore activated while it is the
      // live one, rather than one of them being activated after `insertRootElement` has detached it from
      // the document.
      plain.destroy();

      const destructive = createDialog({ danger: true });
      const destructiveOutcomes = observeOutcomes(destructive);
      confirmButtonOf(destructive).click();

      // Same output, same single emission, same absence of the opposite one.
      expect(destructiveOutcomes.confirmed()).toBe(plainOutcomes.confirmed());
      expect(destructiveOutcomes.confirmed()).toBe(1);
      expect(destructiveOutcomes.cancelled()).toBe(plainOutcomes.cancelled());
      expect(destructiveOutcomes.cancelled()).toBe(0);
    });

    it('emits cancel identically whether or not danger is enabled', () => {
      const plain = createDialog();
      const plainOutcomes = observeOutcomes(plain);
      cancelButtonOf(plain).click();
      plain.destroy();

      const destructive = createDialog({ danger: true });
      const destructiveOutcomes = observeOutcomes(destructive);
      cancelButtonOf(destructive).click();

      expect(destructiveOutcomes.cancelled()).toBe(plainOutcomes.cancelled());
      expect(destructiveOutcomes.cancelled()).toBe(1);
      expect(destructiveOutcomes.confirmed()).toBe(plainOutcomes.confirmed());
      expect(destructiveOutcomes.confirmed()).toBe(0);
    });
  });

  // 12. (i) FOCUS RETURNS TO THE INVOKING ELEMENT WHEN THE DIALOG CLOSES.
  describe('(i) focus restoration on close', () => {
    it('returns focus to the invoking element when the dialog closes', () => {
      const fixture = createHost(InvokerHostComponent);
      const invoker = requireElement(rootOf(fixture), '#invoker');

      invoker.focus();
      expect(document.activeElement).toBe(invoker);

      fixture.componentInstance.open = true;
      fixture.detectChanges();

      // Focus moved into the dialog when it opened, so the restoration below is a
      // real movement rather than focus simply never having left.
      expect(document.activeElement).not.toBe(invoker);

      fixture.componentInstance.open = false;
      fixture.detectChanges();

      expect(document.activeElement).toBe(invoker);
    });

    it('closes the dialog it opened when the component is destroyed', () => {
      const fixture = createHost(InvokerHostComponent);
      fixture.componentInstance.open = true;
      fixture.detectChanges();

      const dialog = dialogOf(fixture);
      expect(dialog.open).toBeTrue();

      fixture.componentInstance.open = false;
      fixture.detectChanges();

      expect(dialog.open).toBeFalse();
    });

    it('restores the element that held focus at construction, not the one at open', () => {
      // THE ISOLATION EXPECTATION. Two mechanisms could produce a restored focus: the component's own
      // explicit call, and the user agent's restoration when a modal closes.
      const constructionTimeHolder = appendFocusHolder('Delete');
      const openTimeHolder = appendFocusHolder('Unrelated');

      constructionTimeHolder.focus();
      expect(document.activeElement).toBe(constructionTimeHolder);

      const fixture = createUninitialisedDialog();

      // Focus moves on only AFTER construction, so the component's captured invoker
      // and the user agent's own record now point at different elements.
      openTimeHolder.focus();
      expect(document.activeElement).toBe(openTimeHolder);

      fixture.detectChanges();
      expect(dialogOf(fixture).open).toBeTrue();

      fixture.destroy();

      expect(document.activeElement).toBe(constructionTimeHolder);
      expect(document.activeElement).not.toBe(openTimeHolder);
    });

    it('skips restoration when the invoking element has itself been removed', () => {
      // A grid row's Delete button disappears with its row once the deletion succeeds, so the captured
      // invoker may well be detached by the time the dialog is torn down. Focusing a detached element
      // silently moves focus to the body, so restoration is skipped rather than attempted.
      const invoker = appendFocusHolder('Delete');

      invoker.focus();
      expect(document.activeElement).toBe(invoker);

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      invoker.remove();
      expect(invoker.isConnected).toBeFalse();

      const restoreAttempt = spyOn(invoker, 'focus');

      expect((): void => {
        fixture.destroy();
      }).not.toThrow();
      expect(restoreAttempt).not.toHaveBeenCalled();
      expect(document.activeElement).not.toBe(invoker);
    });

    it('re-homes focus when the invoker is removed immediately after restoration', async () => {
      // ⚠ THE THIRD TEARDOWN BRANCH. Cancelling keeps the opener and navigating away finds it already
      // detached; CONFIRMING A DELETION does neither - the opener is still connected when the hook runs, so
      // focus is correctly returned to it, and the deletion the confirmation caused then destroys the row
      // it belonged to.
      const region = appendMainRegion();
      const invoker = appendFocusHolder('Delete');

      invoker.focus();

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      // Destroyed while the invoker is STILL connected, which is what makes this branch distinct.
      fixture.destroy();
      expect(document.activeElement).withContext('restored to the opener first').toBe(invoker);

      // The consequence of the outcome: the opener goes away a moment later.
      invoker.remove();
      expect(document.activeElement)
        .withContext('the browser gives focus to the document when the focused element is removed')
        .toBe(document.body);

      // A REAL macrotask, not a mocked clock: the component schedules its rescue with the real timer during
      // `destroy()`, which a clock installed afterwards could not have captured, and installing one
      // beforehand would mock timers across Angular's own teardown for no benefit.
      await new Promise<void>((resolve) => {
        setTimeout(resolve);
      });

      expect(document.activeElement)
        .withContext('focus is re-homed to the main region rather than left on the document')
        .toBe(region);
    });

    it('re-homes focus when the invoker survives several macrotasks before being removed', async () => {
      // ⚠ THE REGRESSION THIS PINS DOWN, AND IT WAS MEASURED IN A BROWSER RATHER THAN IMAGINED. The first
      // version of the rescue scheduled ONE macrotask at teardown, which anchors it to the wrong event: the
      // invoker is destroyed by the response to the request the confirmation triggered, not by the dialog
      // closing.
      const region = appendMainRegion();
      const invoker = appendFocusHolder('Delete');

      invoker.focus();

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();
      fixture.destroy();

      expect(document.activeElement).withContext('restored to the opener first').toBe(invoker);

      // Three macrotasks, comfortably past the point a single scheduled check would have fired.
      for (let turn = 0; turn < 3; turn += 1) {
        await new Promise<void>((resolve) => {
          setTimeout(resolve);
        });
      }

      expect(document.activeElement).withContext('still on the opener, which is still present').toBe(invoker);

      // Now the deletion renders.
      invoker.remove();
      expect(document.activeElement).toBe(document.body);

      await new Promise<void>((resolve) => {
        setTimeout(resolve);
      });

      expect(document.activeElement)
        .withContext('the watch is still live and re-homes focus whenever the removal happens')
        .toBe(region);
    });

    it('leaves focus untouched when the invoker disappears while something else holds it', async () => {
      // The watch may only act when focus is NOWHERE. If the consumer moved focus somewhere deliberate
      // after the outcome - or the reader simply moved on - then stealing it back to the main region would
      // be worse than the defect being fixed.
      const region = appendMainRegion();
      const invoker = appendFocusHolder('Delete');
      const elsewhere = appendFocusHolder('Somewhere deliberate');

      invoker.focus();

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();
      fixture.destroy();

      elsewhere.focus();
      invoker.remove();

      await new Promise<void>((resolve) => {
        setTimeout(resolve);
      });

      expect(document.activeElement).withContext('focus is never taken from a live element').toBe(elsewhere);
      expect(document.activeElement).not.toBe(region);
    });

    it('falls back to the main region when the invoking element has been removed', () => {
      // ⚠ THE DEFECT: skipping restoration is not the same as restoring somewhere.
      const region = appendMainRegion();
      const invoker = appendFocusHolder('Delete');

      invoker.focus();
      expect(document.activeElement).toBe(invoker);

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      invoker.remove();
      expect(invoker.isConnected).toBeFalse();

      fixture.destroy();

      expect(document.activeElement).toBe(region);
    });

    it('moves focus to the main region without scrolling the viewport to it', () => {
      // A reader who has scrolled a long grid and confirmed a deletion has the main region far above the
      // viewport.
      const region = appendMainRegion();
      const invoker = appendFocusHolder('Delete');

      invoker.focus();

      const fixture = createUninitialisedDialog();
      fixture.detectChanges();

      invoker.remove();

      const fallbackFocus = spyOn(region, 'focus');

      fixture.destroy();

      expect(fallbackFocus).toHaveBeenCalledOnceWith({ preventScroll: true });
    });

    it('declines an invoker that is not an HTML element, without throwing', () => {
      const fixture = createHost(GraphicInvokerHostComponent);
      const graphic = rootOf(fixture).querySelector('#graphic-invoker');

      expect(graphic).not.toBeNull();
      if (!(graphic instanceof SVGElement)) {
        throw new Error('Expected the host to render an SVG focus holder.');
      }

      graphic.focus();
      expect(document.activeElement).toBe(graphic);

      fixture.componentInstance.open = true;
      fixture.detectChanges();

      const dialog = dialogOf(fixture);
      expect(dialog.open).toBeTrue();

      const restoreAttempt = spyOn(graphic, 'focus');

      expect((): void => {
        fixture.componentInstance.open = false;
        fixture.detectChanges();
      }).not.toThrow();

      expect(dialog.open).toBeFalse();
      expect(restoreAttempt).not.toHaveBeenCalled();

      // Whatever the platform decides, focus is not left stranded inside the
      // subtree that has just been removed from the document.
      expect(dialog.contains(document.activeElement)).toBeFalse();
    });

    it('skips restoration when nothing meaningful held focus', () => {
      // THE SPY IS THE POINT, AND IT IS INSTALLED BEFORE CONSTRUCTION. The invoker is captured in a field
      // initialiser, so construction is the moment the decision is taken; a spy installed afterwards would
      // observe teardown without ever having been able to influence what teardown had to work with.
      const focusedBeforehand = document.activeElement;
      if (focusedBeforehand instanceof HTMLElement) {
        focusedBeforehand.blur();
      }
      expect(document.activeElement).toBe(document.body);

      const bodyRestoreAttempt = spyOn(document.body, 'focus');

      const fixture = createDialog();
      const dialog = dialogOf(fixture);

      expect(document.activeElement).toBe(cancelButtonOf(fixture));

      expect((): void => {
        fixture.destroy();
      }).not.toThrow();
      expect(dialog.open).toBeFalse();
      expect(bodyRestoreAttempt).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // 13. DEFAULT WORDING
  // =========================================================================
  describe('default wording', () => {
    it('renders the measured default title', () => {
      const fixture = createDialog();

      expect(renderedTextOf(requireElement(rootOf(fixture), TITLE_SELECTOR)).trim()).toBe(DEFAULT_TITLE);
    });

    it('renders the measured default message', () => {
      const fixture = createDialog();

      expect(renderedTextOf(requireElement(rootOf(fixture), MESSAGE_SELECTOR)).trim()).toBe(DEFAULT_MESSAGE);
    });

    it('renders the measured default confirm label', () => {
      const fixture = createDialog();

      expect(directTextOf(confirmButtonOf(fixture))).toBe(DEFAULT_CONFIRM_LABEL);
    });

    it('honours an explicitly supplied empty message rather than restoring the default', () => {
      const fixture = createDialog({ message: '' });

      expect(renderedTextOf(requireElement(rootOf(fixture), MESSAGE_SELECTOR)).trim()).toBe('');
    });

    UNSUPPORTED_CLAIMS.forEach((claim: string): void => {
      it(`never claims "${claim}"`, () => {
        // A REGRESSION GUARD WITH TEETH. The legacy confirmation claimed nothing about permanence, and the
        // backend it now fronts does not justify such a claim: removing a module is a soft delete that
        // answers 204 with the row still present and no recycle-bin endpoint in scope, and withdrawing a
        // paid role assignment whose trial has been consumed expires the assignment rather than deleting it
        // - also 204, row surviving.
        const fixture = createDialog();

        expect(renderedTextOf(rootOf(fixture)).toLowerCase())
          .withContext(`the dialog must not invent the claim "${claim}"`)
          .not.toContain(claim);
      });
    });
  });

  // =========================================================================
  // 14. THE DOCUMENT OUTLINE IS THE SHELL'S, NOT THIS COMPONENT'S
  // =========================================================================
  describe('document outline', () => {
    LANDMARK_SELECTORS.forEach((selector: string): void => {
      it(`contributes no "${selector}" landmark to the page outline`, () => {
        const fixture = createDialog({ danger: true });

        expect(rootOf(fixture).querySelector(selector))
          .withContext(`a shared component must not contribute a "${selector}" landmark`)
          .toBeNull();
      });
    });

    it('suppresses the ambient host tooltip', () => {
      // The host binds the global `title` attribute to null deliberately: an inherited tooltip would repeat
      // the dialog's own heading as a hover hint and would name the wrapper in the accessibility tree.
      const fixture = createDialog({ title: 'Delete portal' });

      expect(rootOf(fixture).getAttribute('title')).toBeNull();
    });
  });

  describe('guards around the normal lifecycle', () => {
    it('renders nothing and throws nothing when its host is detached', () => {
      const fixture = createUninitialisedDialog();
      const root = rootOf(fixture);

      // Detached BEFORE the view initialises, which is the only way to reach the connectivity guard.
      // `showModal()` raises `InvalidStateError` on an element that is not in a document, so without the
      // guard this would be an uncaught exception during change detection rather than a quiet no-op.
      root.remove();

      expect((): void => {
        fixture.detectChanges();
      }).not.toThrow();

      // A closed `<dialog>` is not displayed, so nothing is shown. A non-modal
      // `show()` fallback is deliberately not offered.
      expect(requireDialog(root).open).toBeFalse();
    });

    it('finds its dialog structurally when the view query has not been refreshed', () => {
      const fixture = createUninitialisedDialog();
      const dialog = dialogOf(fixture);
      const cancelButton = cancelButtonOf(fixture);

      fixture.componentInstance.ngAfterViewInit();

      // Both fallbacks did their job: the dialog was located and opened modally, and focus still reached
      // the cancelling affordance rather than the destructive one, so the safety guarantee holds even here.
      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('treats every route as a quiet no-op when no dialog element can be found at all', () => {
      const fixture = createUninitialisedDialog();
      const component = fixture.componentInstance;
      const outcomes = observeOutcomes(fixture);

      requireDialog(rootOf(fixture)).remove();

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

      // And critically: none of them may settle as a side effect of failing to find
      // the element. A missing dialog must never be read as consent.
      expect(outcomes.confirmed()).toBe(0);
      expect(outcomes.cancelled()).toBe(0);
    });

    it('still cancels from an explicit keyboard dismissal with no dialog element', () => {
      const fixture = createUninitialisedDialog();
      const root = rootOf(fixture);
      const outcomes = observeOutcomes(fixture);

      requireDialog(root).remove();

      const event = pressKey(root, 'Escape');

      expect(event.defaultPrevented).toBeTrue();
      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('does not try to reopen a dialog that is already open', () => {
      // `showModal()` raises `InvalidStateError` on an element that is already open, which is why the
      // component tests `open` before calling it.
      const fixture = createUninitialisedDialog();
      const dialog = dialogOf(fixture);

      dialog.showModal();
      expect(dialog.open).toBeTrue();

      const reopen = spyOn(dialog, 'showModal');

      expect((): void => {
        fixture.detectChanges();
      }).not.toThrow();

      expect(reopen).not.toHaveBeenCalled();
      expect(dialog.open).toBeTrue();
    });

    it('still places focus on the cancelling affordance when the dialog was already open', () => {
      // The complement of the expectation above, and the reason the component skips only the OPENING rather
      // than returning early: focus placement is what keeps a stray `Enter` from deleting anything, so it
      // has to run on this path too.
      const fixture = createUninitialisedDialog();
      const dialog = dialogOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      dialog.showModal();
      confirmButton.focus();
      expect(document.activeElement).toBe(confirmButton);

      fixture.detectChanges();

      expect(document.activeElement).toBe(cancelButton);
    });

    it('opens and places no focus when the template offers nothing focusable', () => {
      // The one lifecycle path where the component opens the dialog and then places no focus at all,
      // reached through a template whose only affordance is `disabled` and is therefore rejected by the
      // tabbability filter.
      const fixture = TestBed.createComponent(FocusTargetFreeDialogComponent);
      fixtures.push(fixture);
      const dialog = requireDialog(rootOf(fixture));
      const inertAffordance = requireElement(dialog, '#inert-affordance');

      const focusAttempt = spyOn(inertAffordance, 'focus');

      expect((): void => {
        fixture.detectChanges();
      }).not.toThrow();

      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
      // A disabled affordance is not a legitimate focus target, and the component
      // must not reach for it merely because it is the only candidate present.
      expect(focusAttempt).not.toHaveBeenCalled();
    });

    it('falls back to the first focusable element when the cancel reference is missing', () => {
      const fixture = TestBed.createComponent(ReferenceFreeDialogComponent);
      fixtures.push(fixture);
      const dialog = requireDialog(rootOf(fixture));

      fixture.detectChanges();

      expect(dialog.open).toBeTrue();
      expect(document.activeElement).toBe(requireElement(dialog, '#unreferenced-cancel'));
    });

    it('never lands on the destructive affordance when that fallback runs', () => {
      // The safety guarantee restated for the degraded path, because this is the only route on which the
      // component chooses a focus target by POSITION rather than by name.
      const fixture = TestBed.createComponent(ReferenceFreeDialogComponent);
      fixtures.push(fixture);
      const dialog = requireDialog(rootOf(fixture));

      fixture.detectChanges();

      expect(document.activeElement).not.toBe(requireElement(dialog, '#unreferenced-confirm'));
    });
  });

  describe('performs no network input or output', () => {
    it('issues no request when it is merely rendered', () => {
      createDialog({ title: 'Delete portal', message: 'Portal 0 will be removed.', danger: true });

      httpMock.expectNone((): boolean => true, 'rendering the dialog must not reach the network');
      expect(httpMock.match((): boolean => true)).toEqual([]);
    });

    it('issues no request across a full open-and-confirm cycle', () => {
      const fixture = createDialog({ danger: true });

      confirmButtonOf(fixture).click();
      fixture.detectChanges();

      httpMock.expectNone((): boolean => true, 'confirming must not reach the network');
      expect(httpMock.match((): boolean => true)).toEqual([]);
    });

    it('issues no request across a full open-and-cancel cycle', () => {
      const fixture = createDialog();

      cancelButtonOf(fixture).click();
      pressKey(cancelButtonOf(fixture), 'Escape');
      dispatchClick(dialogOf(fixture));
      fixture.detectChanges();

      httpMock.expectNone((): boolean => true, 'cancelling must not reach the network');
      expect(httpMock.match((): boolean => true)).toEqual([]);
    });
  });

  // =========================================================================
  //  BACKGROUND SCROLL LOCK
  // =========================================================================
  describe('the background scroll lock', () => {
    // ⚠ A NATIVE MODAL DIALOG DOES NOT LOCK SCROLL. `showModal()` makes the page inert to the POINTER and
    // lifts the dialog into the top layer, and it is easy to conclude from that that the page is frozen.

    const LOCK_CLASS = 'dnn-scroll-locked';

    // No `afterEach` guard is registered here, and the omission is deliberate: Jasmine runs an inner
    // `afterEach` BEFORE the outer one, so a guard here would run before the suite-level teardown that
    // destroys the fixtures and would fail on a dialog that is still legitimately open.

    it('locks the root element while the dialog is open, and only until it closes', () => {
      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeFalse();

      const fixture = createDialog();

      expect(dialogOf(fixture).open).withContext('the dialog really opened').toBeTrue();
      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeTrue();

      fixture.destroy();

      expect(document.documentElement.classList.contains(LOCK_CLASS))
        .withContext('the lock is not left behind for the rest of the session')
        .toBeFalse();
    });

    it('releases the lock when the dialog is destroyed', () => {
      const fixture = createDialog();

      fixture.destroy();

      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeFalse();
    });

    it('keeps the lock while a second dialog still needs it', () => {
      // Two confirmations can overlap for a moment - Angular constructs a replacement before destroying the
      // instance it replaces - and a boolean flag would release the lock on the first teardown, leaving the
      // page scrollable underneath the surviving dialog.
      const first = createDialog();
      const second = createDialog();

      first.destroy();

      expect(document.documentElement.classList.contains(LOCK_CLASS))
        .withContext('the second dialog is still open')
        .toBeTrue();

      second.destroy();

      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeFalse();
    });

    it('takes no lock at all when the dialog never opens', () => {
      const fixture = createUninitialisedDialog();

      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeFalse();

      fixture.destroy();

      expect(document.documentElement.classList.contains(LOCK_CLASS)).toBeFalse();
    });
  });
});
