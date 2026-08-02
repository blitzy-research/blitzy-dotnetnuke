//
// Specification for `ConfirmDialogComponent` - the shared destructive-action
// confirmation of the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// THERE IS NO PREDECESSOR SUITE
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// tests of any kind - not a unit test, not a fixture, not a test project, nothing
// anywhere beneath `Library/` or `Website/`. Every expectation below is net-new
// coverage with no legacy assertion to port and nothing to translate.
//
// MIGRATION: the affordance itself is largely net-new too. The legacy mechanism
// was a blocking `window.confirm()` injected into a button's `onClick` attribute
// by `Library/Controls/DotNetNuke.WebUtility/ClientAPI.vb` L331-333 (XML-doc
// L320-330, authored [Jon Henning] 2/17/2005), whose boolean return either
// allowed or suppressed an ASP.NET postback:
//
//     objButton.Attributes.Add("onClick",
//         "javascript:return confirm('" & GetSafeJSString(strText) & "');")
//
// It took exactly ONE argument - the message - and the user agent supplied the
// title, both button labels, the focus behaviour and the dismissal semantics from
// its own locale. The title, the caller-supplied confirm label, the severity
// channel, the accessible name and description, the focus trap, the `Escape`
// handling and the focus restoration are therefore all ADDITIONS rather than
// translations, and each is asserted here because none can be inherited from a
// predecessor. `Library/Controls/**` is an excluded tree and yields no target
// file; it was read for the mechanism only.
//
// ---------------------------------------------------------------------------
// WHY THIS SUITE CARRIES REAL WEIGHT
// ---------------------------------------------------------------------------
// This component gates DESTRUCTION, and the proof that it guards a command rather
// than a navigation is measurable in the legacy markup: in
// `Website/admin/Portal/portals.ascx` the Edit column declares `EditMode="URL"`
// (L21) while the Delete column declares no `EditMode` at all (L22). Edit
// navigated; Delete posted back and mutated. The same contrast appears at
// `Website/admin/Users/ProfileDefinitions.ascx` L17-18.
//
// Two properties therefore matter more than anything else here, and both are
// asserted directly rather than inferred:
//
//   1. Focus must never rest on the destructive affordance when the dialog opens,
//      or a reflex `Enter` would delete something.
//   2. The component must settle exactly ONCE. Four independent routes can cancel
//      - the cancelling affordance, `Escape`, a backdrop click and the platform's
//      own `cancel` event - and a second emission would run a consumer's deletion
//      handler twice. In a real browser one `Escape` press reaches BOTH the host
//      keydown handler AND the element's native `cancel` event, so the emit-once
//      guard is load-bearing rather than defensive and is asserted as a COUNT.
//
// ---------------------------------------------------------------------------
// FRAMEWORK, HARNESS AND ORDER INDEPENDENCE
// ---------------------------------------------------------------------------
// Karma with Jasmine, deliberately and not interchangeably: the mandated command
// is `ng test --watch=false --browsers=ChromeHeadless --code-coverage`, and
// `--browsers` is a Karma option, so a Jest suite could not satisfy it.
//
// `karma.conf.js` leaves Jasmine at its defaults, which means RANDOM spec order.
// Every expectation below is written to be order independent, and that is a real
// constraint rather than a courtesy: a native `<dialog>` opened with
// `showModal()` is promoted to the browser's top layer, and the top layer is a
// property of the DOCUMENT, which the whole Karma run shares. A dialog left open
// by one expectation would make the rest of the document inert for every
// expectation that followed. Every fixture is therefore destroyed in `afterEach`
// - which also runs the component's own `ngOnDestroy` and so exercises the
// close-and-restore path - and the teardown then asserts that no open dialog
// survives it. No expectation depends on another having run first.
//
// Verified facts about the installed packages, read from their type definitions
// rather than assumed:
//
//   * `@angular/core@19.2.25` declares `TestBed.flushEffects()`; it declares no
//     `TestBed.tick()`. Neither is called here, because the component under test
//     uses no `effect()` at all - its only reactive state is a template binding
//     over a plain boolean, and every path that writes it originates in a
//     listener bound by the component's own view, so `detectChanges()` is
//     sufficient and deterministic.
//   * `@angular/common/http/testing@19.2.25` declares `match()`, `expectOne()`,
//     `expectNone(match, description?)` and `verify(opts?)`. The "no request was
//     issued" assertions below use `expectNone(() => true, ...)` together with
//     `match(() => true)`, and `verify()` runs in `afterEach`. The two `provide*`
//     functions are registered directly, never the deprecated testing module.
//
// Strict typing applies here in full: no `any`, no non-null assertion, no
// compiler-suppression comment and no cast anywhere in this file. Every DOM
// lookup is narrowed at run time through the helpers below, which is why the
// specs read as assertions rather than as type gymnastics.
//

import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ChangeDetectionStrategy, Component, type Type } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ConfirmDialogComponent } from './confirm-dialog.component';

// ---------------------------------------------------------------------------
// Measured wording
// ---------------------------------------------------------------------------

/**
 * The defaults the component declares, restated so a silent change to any of them
 * fails a specification instead of only changing the rendered output.
 *
 * `DEFAULT_MESSAGE` is the measured legacy default:
 * `Website/App_GlobalResources/SharedResources.resx` L120-122 defines
 * `DeleteItem.Text` as exactly this string, and
 * `Website/admin/Users/User.ascx.vb` L255-260 shows it being passed to the legacy
 * confirmation helper - with a per-context override substituted when a user is
 * deleting their own account, which is why caller-supplied wording is a legacy
 * precedent rather than an invention.
 *
 * `DEFAULT_CONFIRM_LABEL` is the measured legacy label: `Delete.Text` is `Delete`
 * in both `Users.ascx.resx` L205-207 and `User.ascx.resx` L228-230, and
 * `ProfileDefinitions.ascx` L18 carries `Text="Delete"` beside its image.
 */
const DEFAULT_TITLE = 'Confirm Delete';
const DEFAULT_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const DEFAULT_CONFIRM_LABEL = 'Delete';

/**
 * The cancelling label, authored in the template rather than exposed as an input.
 *
 * Localisation is not ported, so no resource lookup and no localisation attribute
 * is emitted; the wording lives in the component.
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
 * Claims the dialog must never invent.
 *
 * The legacy confirmation promised nothing about permanence, and two of the flows
 * this dialog guards do not delete anything: removing a module is a soft delete
 * that leaves the row in place, and withdrawing a paid role assignment whose
 * trial has been consumed expires the assignment instead. Promising
 * irreversibility would therefore be a behavioural divergence dressed up as
 * helpfulness, so its absence is asserted rather than left to editorial taste.
 */
const UNSUPPORTED_CLAIMS: readonly string[] = [
  'cannot be undone',
  'permanently',
  'irreversible',
  'will not be able to recover',
];

/**
 * Landmarks and their ARIA role equivalents, none of which this component emits.
 *
 * Landmarks belong exclusively to the application shell under `layout/`. A shared
 * component that emitted one would give the page a second, competing outline
 * every time a feature screen mounted it.
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

// ---------------------------------------------------------------------------
// Untrusted wording fixtures - measured, not invented
// ---------------------------------------------------------------------------
//
// A first-hand census of the 37 in-scope `App_LocalResources/*.resx` files under
// `Website/admin/{Portal,Users,Security,Modules,Tabs}/` counted 1332 `<value>`
// nodes, of which 78 contain an HTML tag and 31 begin with a leading `<br>`. The
// opening-tag histogram is br 59, li 31, p 29, h1 21, a 19, strong 17, b 16,
// span 5, ul 5, h3 3, script 2, h4 2. Exactly one file holds a `<script>`:
// `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx`, whose
// `Advertising.Text` (L189 onward) stores TWO live remote advertising script
// blocks - invisible to a naive search because the tags are stored HTML-escaped.
// Its real publisher identifier is deliberately not reproduced here.
//
// Every fixture below is therefore a real input SHAPE rather than a contrived
// one, and each appears only as a string literal: this file renders no markup and
// injects none.

/** The shape of the one in-scope resource value that holds a live script block. */
const SCRIPT_PAYLOAD = '<script>alert(1)</script>';

/** A second script shape, closer to the measured remote-advertising entry. */
const REMOTE_SCRIPT_PAYLOAD = '<script type="text/javascript"><!--\nad_output = "textlink";\n//--></script>';

/** Inline emphasis, the shape of `EditRoles.ascx.resx` -> `ProcessorWarning.Text`. */
const BOLD_PAYLOAD = '<b>Warning:</b> configure the payment processor';

/** A single leading break, the commonest of the 31 measured leading-break values. */
const LEADING_BREAK_PAYLOAD = '<br>Deleted';

/**
 * Several leading breaks, in BOTH spellings, because they accumulate.
 *
 * `Website/admin/Portal/Signup.ascx.vb` appends `"<br>" & …` from INSIDE
 * per-character validation loops at L193 and L214, again for the password branch
 * at L221, and then wraps the whole accumulated string once more at L323
 * (`lblMessage.Text = "<br>" & strMessage & "<br><br>"`). A five-bad-character
 * portal name therefore arrives carrying six leading breaks. The `<br/>` spelling
 * is equally real: `Website/admin/Users/User.ascx.vb` L187 sets
 * `valPassword.ErrorMessage = "<br/>" + …`.
 */
const ACCUMULATED_BREAK_PAYLOAD = '<br><br/><br><br/><br><br>Portal name contains invalid characters';

/**
 * Values that are present but carry no announceable name.
 *
 * Both forms have to be covered, and covering only the empty one is the specific
 * gap that made this a finding: a length check alone accepts `'   '`, which the
 * accessible-name computation collapses to exactly the same nothing as `''`. The tab
 * and newline are included because resource-sourced wording arrives from `.resx`
 * values that can hold either.
 */
const BLANK_NAMES: readonly string[] = ['', '   ', '\t', '\n', ' \t\n '];

// ---------------------------------------------------------------------------
// Narrowing helpers
// ---------------------------------------------------------------------------
//
// A dialog specification is almost entirely DOM lookups, and every DOM lookup is
// nullable by specification. Each helper below therefore narrows at RUN TIME and
// throws a self-describing error on failure, so callers read from a non-nullable
// value without a single assertion operator. Absence, where it is the thing being
// asserted, is proved with an explicit `toBeNull()` rather than with a truthiness
// check - `expect(el).toBeTruthy()` would also pass for an element that exists but
// is the wrong one.

/** Rendered text of an element, normalising the nullable `textContent`. */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * An element's OWN character data, ignoring descendant elements.
 *
 * `textContent` walks the whole subtree, so on the confirming affordance it also
 * picks up the decorative severity glyph that danger mode renders. That glyph is
 * `aria-hidden` and is not wording any call site supplied, so an assertion about
 * caller-supplied wording has to exclude it or it asserts two things at once.
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

/** The single rendered `<dialog>`, narrowed to the interface that can open it. */
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
 * The element another element references by id, proving the reference resolves.
 *
 * This is the mechanism behind the accessible-name expectations. An
 * `aria-labelledby` naming a missing id is worse than no name at all, because it
 * suppresses the fallback the browser would otherwise compute - so asserting that
 * the attribute merely exists would assert nothing worth having.
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
 * Dispatches a keydown the way a real one arrives, and hands the event back.
 *
 * Three details are load-bearing. `bubbles` is required because the handler is
 * bound on the component HOST and the event has to travel up out of the
 * `<dialog>`. `cancelable` is required because the handler calls
 * `preventDefault()`, and on a non-cancellable event that call is silently
 * ignored - the assertion would pass while the real suppression failed. And the
 * dispatch TARGET matters, because the wrap logic resolves its origin from
 * `event.target` first, so the target is what selects the boundary under test.
 *
 * Keys are named by `key`, never by the deprecated numeric code.
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

/**
 * Dispatches a bubbling click carrying viewport coordinates.
 *
 * Coordinates are the only thing that can separate a backdrop click from a click on
 * the dialog's own frame, because the platform reports the SAME event target for
 * both. A test that omits them is really asserting against the coordinate defaults
 * of zero, which happens to sit outside a centred modal but says so only by
 * accident; these expectations state the position they mean.
 */
function dispatchClickAt(target: EventTarget, clientX: number, clientY: number): void {
  target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX, clientY }));
}

// ---------------------------------------------------------------------------
// Consumer call sites
// ---------------------------------------------------------------------------
//
// Each host below reproduces a call-site SHAPE that a bare fixture cannot: an
// invoker that genuinely held focus, a static attribute rather than a property
// binding, a non-HTML focus holder, and a template that binds every input and
// both outputs. They are standalone and are registered through `imports`, never
// through a declarations array.

/**
 * A grid-style call site: a real Delete button that mounts the dialog.
 *
 * Focus restoration cannot be proven from a bare fixture, because the component
 * captures its invoker by reading the focused element during construction and in a
 * bare fixture nothing is focused - so the capture correctly reports "absent" and
 * there is nothing to restore. Mounting behind `@if` reproduces the real sequence:
 * the clicked affordance still holds focus at the moment the block flips.
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
 * TWO dialogs mounted at once, from a single call site.
 *
 * Element ids must be unique across the whole DOCUMENT, not merely within one
 * component, because `aria-labelledby` resolves against the document. Two separate
 * fixtures cannot demonstrate that, for a reason measured rather than assumed:
 * `TestBed.createComponent` inserts its root through `DOMTestComponentRenderer`,
 * whose `insertRootElement` first removes every existing `[id^=root]` element - so
 * creating a second fixture DETACHES the first and the two are never in the
 * document together. One host rendering two siblings is the only shape that puts
 * both there at the same time, and it is also exactly what an administration
 * screen does when a grid row and a toolbar each own a confirmation.
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

/**
 * A focus holder that is NOT an `HTMLElement`.
 *
 * An `<svg>` carrying a tab index is focusable and is reported by
 * `document.activeElement`, yet it is an `SVGElement` and so has no `HTMLElement`
 * interface to call. Narrowing the captured invoker by interface - rather than
 * assuming everything focusable is an HTML element - is what keeps restoration
 * type-safe instead of failing at run time. The graphic is authored as template
 * markup precisely so that this file neither assigns raw HTML nor names a
 * namespace URI.
 */
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
 * A call site writing `danger` as a BARE ATTRIBUTE.
 *
 * A property binding cannot exercise this path: the bare form arrives at the input
 * as the empty string, and only the declared boolean coercion turns it into
 * `true`. Without that coercion this host would render non-destructive styling for
 * a call site that asked for destructive styling.
 */
@Component({
  selector: 'app-bare-danger-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `<app-confirm-dialog danger></app-confirm-dialog>`,
})
class BareDangerAttributeHostComponent {}

/**
 * A call site writing `title` as a STATIC TEMPLATE ATTRIBUTE.
 *
 * The framework copies a static template attribute onto the rendered element IN
 * ADDITION to assigning the matching input, so this is the exact shape that makes
 * the `title` input's collision with the global HTML `title` attribute observable.
 * A surviving attribute would give the host and its whole subtree a native
 * tooltip and would name the wrapper in the accessibility tree, competing with the
 * dialog's own accessible name.
 */
@Component({
  selector: 'app-static-title-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `<app-confirm-dialog title="Remove Role"></app-confirm-dialog>`,
})
class StaticTitleAttributeHostComponent {}

/**
 * A call site binding every input and both outputs through the template.
 *
 * Binding all four inputs from a host template is the COMPILE-TIME proof that all
 * four are public: `strictInputAccessModifiers` is enabled, so a private or
 * protected input would fail this file's own compilation - a stronger guarantee
 * than any run-time reflection check. The counters prove the outputs are reachable
 * from ordinary template bindings rather than only from a direct subscription.
 */
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


// ---------------------------------------------------------------------------
// Contract-violating templates
// ---------------------------------------------------------------------------
//
// Both classes below SUBCLASS the component under test and supply a template that
// breaks the published template contract in one specific way. That is the only way
// to reach the two fallbacks the component declares, and it is a faithful way
// rather than a contrived one, because those fallbacks exist for precisely this
// situation: `resolveInitialFocusTarget` documents that "a renamed reference
// yields nothing from the query, with no compile-time signal", which is a defect
// the framework cannot report and the compiler cannot catch. Removing the element
// from the rendered DOM would NOT do: `@ViewChild` resolves against the template's
// recorded nodes rather than by walking the document, so a detached button still
// satisfies the query.
//
// Subclassing is what makes the inherited view queries run against a different
// template while the lifecycle logic under test stays byte-identical. Angular
// copies inputs, outputs, host bindings and view queries from the superclass
// definition onto the subclass, so the only thing that differs is the markup.

/**
 * A template whose cancelling affordance has LOST its `#cancelButton` reference.
 *
 * The `@ViewChild` query therefore resolves to nothing and
 * `resolveInitialFocusTarget` falls through to the first keyboard-focusable
 * descendant. Cancel is still rendered FIRST, which is the condition the component
 * documents as making that fallback safe - so this proves the degradation is
 * graceful rather than merely non-fatal.
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
 * A template offering NO keyboard-focusable content at all.
 *
 * The reference query resolves to nothing and the structural fallback finds
 * nothing either, because the only candidate is `disabled` and so is rejected by
 * the tabbability filter. `resolveInitialFocusTarget` therefore returns
 * `undefined`, which is the one lifecycle path where the component opens the dialog
 * and then deliberately places no focus.
 *
 * A disabled affordance is used rather than an empty dialog because it leaves
 * something concrete to spy on: the assertion becomes "this element was never
 * focused" rather than the unfalsifiable "nothing happened".
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
  /**
   * Every fixture created during a specification, torn down afterwards.
   *
   * Teardown is mandatory rather than tidy, for the top-layer reason set out in
   * the file header, and it is registered at CREATION rather than at the end of a
   * specification so that an early failure can never leak an open modal into the
   * expectations that follow.
   */
  let fixtures: ComponentFixture<unknown>[] = [];

  /**
   * Focusable elements appended straight to the document, removed afterwards.
   *
   * Restoration can only be observed against an element that is genuinely
   * CONNECTED, and it cannot be observed against a second fixture: creating one
   * detaches the first, for the `insertRootElement` reason recorded above. A holder
   * appended directly to the body sidesteps that completely - it carries no `root`
   * id, so the renderer never removes it, and it stays connected across every
   * fixture the specification goes on to create.
   */
  let focusHolders: HTMLButtonElement[] = [];

  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Standalone components and standalone hosts are registered through
      // `imports`. A declarations array is neither used nor available for them,
      // and a host omitted here would be silently unresolvable when created.
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
      // The real client is registered FIRST and the testing backend SECOND, which
      // is the documented order: the testing backend replaces the real backend's
      // transport while leaving the rest of the client intact. Reversing the two
      // leaves the real backend in place, which is the commonest silent
      // false-green in an Angular suite.
      //
      // Registering HTTP at all, for a component that injects no data service, is
      // deliberate. A shared presentational component must perform NO network
      // input or output: it asks a question, emits an intent, and the consumer
      // acts. Wiring a real client and then proving nothing was ever sent turns
      // that architectural rule into an executable one - were a request ever added
      // here, `verify()` would fail every specification in this suite rather than
      // passing silently.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixtures = [];
    focusHolders = [];
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const fixture of fixtures) {
      // Destroying runs the component's own `ngOnDestroy`, which closes the dialog
      // and restores focus. `ComponentFixture.destroy()` is idempotent, so a
      // specification that destroys its own fixture to observe teardown is not
      // penalised for it here.
      fixture.destroy();
    }
    fixtures = [];

    // Removed AFTER the fixtures, so that a component's own restoration has
    // already been observed against a connected holder, and removed at all so that
    // no later specification inherits a focused element from this one.
    for (const holder of focusHolders) {
      holder.remove();
    }
    focusHolders = [];

    // The order-independence guard, asserted rather than assumed: an open modal
    // left behind would make the shared Karma document inert and would corrupt
    // every later focus expectation in the whole run.
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
   * Creates and renders the dialog, applying any inputs before the first pass.
   *
   * Inputs are applied through `setInput` before the initial `detectChanges()`, so
   * the component opens with the values a real call site would already have bound.
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

  /** The inner `<dialog>` of a fixture. */
  const dialogOf = (fixture: ComponentFixture<unknown>): HTMLDialogElement => requireDialog(rootOf(fixture));

  /** Both affordances of a fixture, in document order. */
  const buttonsOf = (fixture: ComponentFixture<unknown>): readonly HTMLButtonElement[] =>
    requireButtons(rootOf(fixture));

  /** The cancelling affordance, which the template renders first. */
  const cancelButtonOf = (fixture: ComponentFixture<unknown>): HTMLButtonElement => buttonsOf(fixture)[0];

  /** The confirming affordance, which the template renders second. */
  const confirmButtonOf = (fixture: ComponentFixture<unknown>): HTMLButtonElement => buttonsOf(fixture)[1];

  /**
   * How many times each output has fired.
   *
   * Counters rather than booleans, because every interesting invariant here is
   * about COUNTS: "emitted at most once" cannot be distinguished from "emitted" by
   * a boolean, and a double emission is precisely the defect the emit-once guard
   * exists to prevent.
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
      // `showModal()` rather than `show()`: a confirmation that does not block is
      // worse than no confirmation at all, because the user can act on the record
      // behind it while the question is still on screen. `:modal` is the platform's
      // own answer to "is this in the top layer", which is stronger evidence than
      // the `open` attribute alone.
      const fixture = createDialog();
      const dialog = dialogOf(fixture);

      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
    });

    it('declares no static open attribute in the template', () => {
      // A template-declared `open` would render the dialog visible but NON-modal,
      // and `showModal()` throws on an already-open dialog - so the attribute would
      // both defeat modality and break opening.
      const fixture = createUninitialisedDialog();
      const root = rootOf(fixture);
      const beforeInitialisation = requireDialog(root);

      expect(beforeInitialisation.hasAttribute('open')).toBeFalse();
    });

    it('declares the alert-dialog role on the dialog rather than on the host', () => {
      // `alertdialog` deliberately overrides the element's implicit `dialog` role
      // so that assistive technology announces the name AND the description at
      // once, which is the behaviour a destructive question needs. Putting any
      // dialog role on the host would announce a second, empty dialog wrapped
      // around the real one.
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
      // Two independent reasons, so this is a requirement rather than a
      // preference. Safety: the user agent's own first-focusable heuristic then
      // lands on Cancel. Fidelity: `Website/admin/Security/editroles.ascx`
      // L179-189 renders Update, then Cancel (L182-183), then Delete (L185-186),
      // so the measured legacy order already put the safe action first.
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
      // An implicit submit button would post an ancestor form, which is exactly
      // why both legacy link buttons carried `CausesValidation="False"`
      // (`editroles.ascx` L183 and L186).
      for (const button of buttonsOf(createDialog())) {
        expect(button.type)
          .withContext(`"${directTextOf(button)}" must not submit a form`)
          .toBe('button');
      }
    });

    it('gives every affordance real, visible, readable text', () => {
      // `Website/admin/Security/roles.ascx` L13 rendered its delete image button
      // with no alternate text, no resource key and no text at all - two lines
      // below an edit image at L11 carrying BOTH `AlternateText` and
      // `resourcekey`. That defect is proven by contrast and forbidden from
      // reappearing here; `ProfileDefinitions.ascx` L18 is the legacy precedent
      // being honoured.
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
      // Focus placement is the component's own decision, made in a lifecycle hook
      // where the cancelling affordance can be chosen deliberately. An `autofocus`
      // attribute would hand that decision to the user agent's document order, and
      // an author-supplied tab index would reorder the trap unpredictably.
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

      // Asserting BOTH halves is what distinguishes a working strip from an input
      // that never bound at all: the value must have reached the heading, and the
      // attribute must be gone from the host.
      expect(host.hasAttribute('title')).toBeFalse();
      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Remove Role');
    });

    it('gives two concurrently mounted dialogs non-colliding element identifiers', () => {
      // The ids come from a monotonic instance counter precisely so that two
      // dialogs cannot both claim the same `aria-labelledby` target - duplicate ids
      // would break the accessible name of both. Both dialogs are mounted from ONE
      // host, because that is the only arrangement in which both are in the
      // document simultaneously, and uniqueness is a property of the document.
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
      // THE SAFETY GUARANTEE. If focus rested on the destructive affordance, an
      // immediate `Enter` or `Space` - the reflex of a user who did not expect a
      // dialog - would delete the record. The negative is asserted alongside the
      // positive on purpose: a change that focused the wrong button, and one that
      // focused nothing and left focus on the body, must both fail.
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

  // =========================================================================
  //  3. (b) THE FOCUS TRAP WRAPS AT BOTH BOUNDARIES
  //
  //  `showModal()` already confines focus natively, but that native path is
  //  unreachable from a synthetic event: a dispatched `KeyboardEvent` neither
  //  moves focus nor engages the user agent's own trap. The component therefore
  //  implements the boundary wrap explicitly, which is what makes it observable
  //  here. Interior tabbing is deliberately left to the browser, in the user's own
  //  platform order, and that restraint is asserted too.
  // =========================================================================
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
      // The discriminating half of the trap. A handler that wrapped
      // unconditionally would pass both boundary expectations above and would
      // still be wrong, because it would fight the browser on every interior
      // keystroke.
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
      // A synthetic event may be aimed at the dialog while focus genuinely rests on
      // a button, which is the documented fallback path. Without it a dispatched
      // event would resolve no origin at all and the wrap would never fire.
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

      // HOW THIS STATE IS REACHED, because the obvious route does not work. A modal
      // open makes everything outside the dialog inert, so focus cannot be parked
      // on an unrelated element to create this case - the attempt is silently
      // ignored and focus stays inside, which would make this expectation pass for
      // entirely the wrong reason. Blurring genuinely surrenders focus to the
      // document body, which the component reports as "nothing meaningful is
      // focused".
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

      // Every element below matches the component's focusable-candidate selector,
      // and every one is appended AFTER the action row - so if any were counted,
      // the confirming affordance would no longer be the last focusable element and
      // the forward wrap at the end of this specification would not fire. Each
      // exclusion is therefore a real, load-bearing one rather than a defensive
      // guess.
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
      // POSITIVE CONTROL for the exclusions above. Without it, that expectation
      // would also pass against an implementation that ignored appended elements
      // altogether, or whose collector returned nothing at all.
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

      // `Esc` and `escape` are included deliberately: keys are compared by their
      // modern `key` value, so neither the legacy spelling nor a differently-cased
      // one may settle the dialog.
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
      // This is what stops a REAL `Escape` press from also firing the element's
      // native `cancel` event, which is the browser behaviour that would otherwise
      // produce a second emission.
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
      // THE DEFECT THIS EXISTS TO CATCH. In a real browser a single `Escape` press
      // reaches BOTH the component's keydown handler AND the element's own `cancel`
      // event, so an implementation without the emit-once guard would cancel twice
      // while a synthetic-keydown-only expectation observed one emission and passed.
      // Driving both paths in sequence reproduces the real sequence and holds the
      // total at one.
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
      // The cancelling label is a template literal precisely so that no consumer can
      // turn the escape hatch into something else. This host binds every input it
      // can, so the three caller-supplied strings all change while this one must not.
      const fixture = createHost(FullyBoundHostComponent);
      const root = rootOf(fixture);
      const [cancelButton, confirmButton] = buttonsOf(fixture);

      expect(directTextOf(cancelButton)).toBe(CANCEL_LABEL);
      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Confirm Removal');
      expect(renderedTextOf(requireElement(root, MESSAGE_SELECTOR)).trim()).toBe('Remove the Administrators role?');

      // Read through the element's own character data, because this host enables
      // danger mode and `textContent` would flatten the decorative glyph into the
      // comparison and fail for a reason unrelated to relabelling.
      expect(directTextOf(confirmButton)).toBe('Remove');
    });

    it('goes inert once it has cancelled, so a second click cannot reach the handler', () => {
      // The disabled state is REAL rather than merely guarded: the native attribute
      // closes the pointer, `Enter` and `Space` paths at once, which is a stronger
      // guarantee than the emit-once guard alone.
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
      // Settlement is an INTENT, not a result: nothing has been deleted when the
      // output fires. The consumer unmounts the dialog in response, which is also
      // what makes the single-outcome guarantee safe to state so absolutely.
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

  // =========================================================================
  //  7. SINGLE SETTLEMENT AND MUTUAL EXCLUSIVITY
  //
  //  The two outcomes are mutually exclusive, which is a safety property rather
  //  than a tidiness one: a late click on the opposite affordance must not be able
  //  to turn a cancellation into a deletion.
  // =========================================================================
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
      // A modal `<dialog>` paints its own backdrop, and a click there reports the
      // DIALOG as the target. Comparing the target is therefore NECESSARY but not
      // SUFFICIENT: it rejects every click on a descendant, yet a click on the
      // dialog's own frame reports the very same target as the backdrop does, so the
      // pointer position is what actually separates the two. A backdrop click can
      // only ever cancel: a destructive action must not follow the least deliberate
      // gesture available.
      const fixture = createDialog();
      const outcomes = observeOutcomes(fixture);
      const dialog = dialogOf(fixture);
      const bounds = dialog.getBoundingClientRect();
      const outsideX = bounds.left - 5;
      const outsideY = bounds.top - 5;

      // Self-validating. A dialog that measured zero would make every point "outside"
      // and this expectation would pass for the wrong reason, so the real box is
      // asserted first and the point is then checked against all four edges.
      expect(bounds.width).toBeGreaterThan(0);
      expect(bounds.height).toBeGreaterThan(0);
      expect(outsideX < bounds.left || outsideX > bounds.right).toBeTrue();
      expect(outsideY < bounds.top || outsideY > bounds.bottom).toBeTrue();

      dispatchClickAt(dialog, outsideX, outsideY);

      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('never settles for a click on the dialog frame inside its own bounds', () => {
      // THE F1 REGRESSION GUARD. This is the case a target-only test cannot see. The
      // platform attributes a click on the dialog's own box - its border, and any
      // inset that were left on it - to the DIALOG, exactly as it attributes a
      // backdrop click. Under a target-only test both look identical, so clicking
      // beside the title dismissed a destructive confirmation. Two independent
      // safeguards now prevent it: the panel wrapper covers the dialog's content box
      // so there is no inset to hit, and this geometry check keeps anything within
      // the dialog's bounds on the non-dismissing side.
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
      // The structural half of the F1 fix, asserted rather than assumed. The dialog
      // must have exactly ONE element child, and the three content regions must live
      // inside it - otherwise the inset is back on the dialog and back to reporting
      // the dialog as a click target.
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

      // Each of these bubbles to the dialog's own click handler, so a handler that
      // failed to compare the target would cancel on any click inside the panel -
      // including a click that merely selected the message text.
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
      // THE POINT THIS EXISTS TO MAKE. The user agent's default action for `cancel`
      // is to CLOSE the element, and the class's settlement contract is that
      // neither output closes the dialog - teardown belongs to the consumer. So
      // emitting `cancel` is only half of what the handler owes: it must also stop
      // the browser acting on the same event. Counting outputs cannot see that, and
      // a handler that emitted correctly while letting the default run would leave
      // the element closed underneath a component that was still mounted.
      //
      // The event is RETAINED rather than dispatched inline, because
      // `defaultPrevented` is the only observable the suppression leaves behind.
      const fixture = createDialog();
      const cancelRequest = new Event('cancel', { bubbles: false, cancelable: true });

      dialogOf(fixture).dispatchEvent(cancelRequest);

      expect(cancelRequest.defaultPrevented).toBeTrue();
    });

    it('stays open after the element raises its own cancel event', () => {
      // The other half of the same contract, asserted independently of the flag: a
      // dismissal the platform originates announces the user's intent to this
      // component and changes nothing else. The consumer unmounts in response to
      // the output, and that unmount is what closes the dialog.
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
      // The emit-once guard returns early on a second dismissal, so suppression has
      // to happen BEFORE that guard is consulted. Were the order reversed, a second
      // `Escape` on a settled dialog would fall through to the browser and close an
      // element whose own affordances had already gone inert - the one state the
      // class documents as unrecoverable.
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
      // The sibling suppression, recorded here beside the one above so the pair is
      // visible in one place: `Escape` is the user agent's other route to closing a
      // modal `<dialog>`, and both routes have to be closed for the settlement
      // contract to hold.
      const fixture = createDialog();

      const escape = pressKey(cancelButtonOf(fixture), 'Escape');

      expect(escape.defaultPrevented).toBeTrue();
      expect(dialogOf(fixture).open).toBeTrue();
    });
  });


  // =========================================================================
  //  9. (f) ACCESSIBLE NAME AND DESCRIPTION
  //
  //  Every expectation here RESOLVES the referenced id to a real node and compares
  //  its text. Asserting only that the attributes are present would assert nothing
  //  worth having: an `aria-labelledby` naming a missing id is worse than no name at
  //  all, because it suppresses the fallback the browser would otherwise compute.
  // =========================================================================
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
      // The page's single `<h1>` belongs to the shared page-header primitive that
      // the feature screen behind this dialog already renders, so a second one would
      // give one document two competing outlines.
      const fixture = createDialog();
      const root = rootOf(fixture);

      expect(root.querySelector('h1')).toBeNull();
      expect(requireElement(root, TITLE_SELECTOR).tagName).toBe('H2');
    });

    it('keeps the description reference resolvable even for an explicitly empty message', () => {
      // SENTINEL DISCIPLINE. The legacy null-string sentinel IS the empty string, so
      // `''` is a legitimate caller-supplied value and must never be quietly swapped
      // for the default. The paragraph is still emitted, so the reference cannot
      // dangle - an empty description is a different thing from a broken one.
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

    // THE F2 GUARDS. A resolvable reference to an EMPTY node is worse than no
    // reference at all, because it suppresses the fallback the browser would
    // otherwise compute and leaves the dialog announced as nothing. Both of these
    // inputs are accessible names, so blank is never a legitimate value for either -
    // unlike `message`, which is a description and may legitimately be empty, as the
    // sentinel expectation above asserts.
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
        // The severity glyph beside the label is `aria-hidden`, precisely so it
        // cannot act as a naming source - which leaves a blank label with nothing at
        // all to fall back on. An unnamed destructive button is the worst outcome
        // this component could produce.
        const fixture = createDialog({ confirmLabel: blank, danger: true });

        expect(directTextOf(confirmButtonOf(fixture))).toBe(DEFAULT_CONFIRM_LABEL);
        expect(renderedTextOf(confirmButtonOf(fixture)).trim().length).toBeGreaterThan(0);
      });
    });

    it('trims surrounding whitespace from both accessible names without altering the words', () => {
      // Trimming is not cosmetic: the accessible-name computation already collapses
      // surrounding white space, so a padded value and a trimmed one are announced
      // identically. Normalising on the way in is what makes `'   '` and `''`
      // indistinguishable to the guard above, as they already are to a screen reader.
      const fixture = createDialog({ title: '  Delete role  ', confirmLabel: '  Delete  ' });
      const root = rootOf(fixture);

      expect(renderedTextOf(requireElement(root, TITLE_SELECTOR)).trim()).toBe('Delete role');
      expect(directTextOf(confirmButtonOf(fixture))).toBe('Delete');
    });
  });

  // =========================================================================
  // 10. (g) CALLER WORDING IS INTERPOLATED, NEVER TRUSTED AS MARKUP
  //
  //  Rendering any of these payloads as markup would be script injection, because
  //  the wording source is untrusted: one in-scope legacy resource value genuinely
  //  stores live remote script blocks. Interpolation escapes them, which is the safe
  //  choice and, as it happens, the faithful one too -
  //  `Website/admin/Security/AccessDenied.ascx.vb` L43 wraps its message in
  //  `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))` and `Default.aspx.vb` L232
  //  uses `Server.HtmlEncode`, while the legacy confirm helper's own
  //  `GetSafeJSString` escaped only for JavaScript-string-literal safety and was
  //  never HTML sanitisation.
  //
  //  These expectations assert ESCAPING, never stripping. The component removes
  //  nothing: leading breaks are the canonical concern of the shared form-error
  //  utility, not of this dialog, so they must appear here as literal characters.
  // =========================================================================
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
      // Modelled on the measured `Advertising.Text` value, which stores two live
      // advertising script blocks HTML-escaped. The real publisher identifier is
      // deliberately not reproduced.
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
      // The accumulation is real: `Signup.ascx.vb` appends a break per invalid
      // character (L193, L214), again for the password branch (L221), and wraps the
      // result once more at L323 - so a five-bad-character portal name arrives with
      // six leading breaks. `User.ascx.vb` L187 contributes the `<br/>` spelling.
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


  // =========================================================================
  // 11. (h) THE DANGER MODIFIER CHANGES PRESENTATION ONLY
  //
  //  No code path in the component reads this input - the template is the single
  //  place it is consumed - so it cannot change what confirmation MEANS or when it
  //  fires. That separation is asserted rather than trusted, because coupling
  //  styling to semantics through one flag is an easy and dangerous mistake.
  // =========================================================================
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
      // The glyph must not compete with the label as a naming source, nor announce
      // the severity a second time. It is also why severity never rests on colour
      // alone.
      const fixture = createDialog({ danger: true });
      const glyph = requireElement(confirmButtonOf(fixture), DECORATIVE_SELECTOR);

      expect(requireAttribute(glyph, 'aria-hidden')).toBe('true');
    });

    it('keeps the outcome in words as well as in colour and glyph', () => {
      // The paired stylesheet records that the danger ink measures below the
      // contrast minimum for normal text and is implemented exactly as measured,
      // flagged for designer review. Its stated mitigation is that severity never
      // depends on colour alone - so the label carrying the outcome in words is a
      // load-bearing part of that mitigation and is asserted here, alongside the
      // glyph that carries the other half.
      const fixture = createDialog({ danger: true, confirmLabel: 'Delete Portal' });
      const confirmButton = confirmButtonOf(fixture);

      expect(directTextOf(confirmButton)).toBe('Delete Portal');
      expect(confirmButton.classList.contains(DANGER_CLASS)).toBeTrue();
      expect(confirmButton.querySelector(DECORATIVE_SELECTOR)).not.toBeNull();
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

        // A nullish expression must coerce to the PRESENTATION-SAFE default rather
        // than styling a destructive action from an absent value, and the modifier
        // must never reach the cancelling affordance whatever is supplied.
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

      // Torn down before the comparison case is built. Each dialog is therefore
      // activated while it is the live one, rather than one of them being activated
      // after `insertRootElement` has detached it from the document.
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

  // =========================================================================
  // 12. (i) FOCUS RETURNS TO THE INVOKING ELEMENT WHEN THE DIALOG CLOSES
  //
  //  Without this, a keyboard user loses their place in the grid entirely: focus
  //  lands on the document body and the next `Tab` starts again from the top of the
  //  page.
  // =========================================================================
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
      // THE ISOLATION EXPECTATION. Two mechanisms could produce a restored focus:
      // the component's own explicit call, and the user agent's restoration when a
      // modal closes. They are deliberately pointed at DIFFERENT elements - the
      // component captures its invoker during CONSTRUCTION, while the user agent
      // records whatever holds focus at the moment the modal OPENS - so only the
      // component's own behaviour can produce the expected result, and a change that
      // deleted it would land on the other element instead.
      //
      // Both holders live directly in the document rather than in a host fixture,
      // because the dialog fixture created below would otherwise detach them - see
      // `appendFocusHolder`. A detached holder cannot take focus at all, which would
      // make this isolation impossible to set up in the first place.
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
      // A grid row's Delete button disappears with its row once the deletion
      // succeeds, so the captured invoker may well be detached by the time the
      // dialog is torn down. Focusing a detached element silently moves focus to the
      // body, so restoration is skipped rather than attempted.
      //
      // The removal has to be the specification's own deliberate act to mean
      // anything, which is why the invoker is a document-level holder: a holder
      // inside a host fixture would already have been detached by the dialog fixture
      // created below, and the assertion would then hold for the wrong reason.
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

    it('declines an invoker that is not an HTML element, without throwing', () => {
      // An `<svg>` carrying a tab index is focusable and is reported by
      // `document.activeElement`, yet it has no `HTMLElement` interface to call.
      // Narrowing the captured invoker by interface is what keeps this type-safe
      // instead of failing at run time.
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

      // MEASURED, NOT ASSUMED. Chrome 151 does NOT hand focus back to a previously
      // focused `<svg>` when a modal closes, so `document.activeElement` afterwards
      // is the body and reveals nothing about this component either way. The spy is
      // installed only once the graphic has genuinely held focus, so it measures
      // exactly one thing: what TEARDOWN attempts. The contract is that it attempts
      // nothing at all, because narrowing by interface reported this invoker absent.
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
      // `document.activeElement` reports the body when nothing in particular is
      // focused, and the body is not a useful restoration target - focusing it is
      // indistinguishable from focusing nothing. That case is reported as absent so
      // restoration is skipped rather than performed as a misleading no-op.
      //
      // THE SPY IS THE POINT, AND IT IS INSTALLED BEFORE CONSTRUCTION. The invoker
      // is captured in a field initialiser, so construction is the moment the
      // decision is taken; a spy installed afterwards would observe teardown
      // without ever having been able to influence what teardown had to work with.
      // Without it this specification would pass unchanged against a component that
      // captured the body and dutifully focused it on the way out - no throw, and
      // the dialog still closes - which is exactly the misleading no-op the
      // component documents itself as refusing to perform.
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
      // `SharedResources.resx` L120-122 defines `DeleteItem.Text` as exactly this
      // string, and `User.ascx.vb` L255-260 shows it being handed to the legacy
      // confirmation helper.
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
        // A REGRESSION GUARD WITH TEETH. The legacy confirmation claimed nothing
        // about permanence, and the backend it now fronts does not justify such a
        // claim: removing a module is a soft delete that answers 204 with the row
        // still present and no recycle-bin endpoint in scope, and withdrawing a paid
        // role assignment whose trial has been consumed expires the assignment
        // rather than deleting it - also 204, row surviving. A future contributor
        // "improving" this copy would break this expectation, which is exactly the
        // point.
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
      // The host binds the global `title` attribute to null deliberately: an
      // inherited tooltip would repeat the dialog's own heading as a hover hint and
      // would name the wrapper in the accessibility tree.
      const fixture = createDialog({ title: 'Delete portal' });

      expect(rootOf(fixture).getAttribute('title')).toBeNull();
    });
  });

  // =========================================================================
  // 15. GUARDS AROUND THE NORMAL LIFECYCLE
  //
  //  Both element lookups in the component return an optional under strict null
  //  checking, so every caller has to handle absence in order to compile at all.
  //  These expectations prove the handling is real behaviour rather than dead code
  //  written to satisfy the compiler.
  // =========================================================================
  describe('guards around the normal lifecycle', () => {
    it('renders nothing and throws nothing when its host is detached', () => {
      const fixture = createUninitialisedDialog();
      const root = rootOf(fixture);

      // Detached BEFORE the view initialises, which is the only way to reach the
      // connectivity guard. `showModal()` raises `InvalidStateError` on an element
      // that is not in a document, so without the guard this would be an uncaught
      // exception during change detection rather than a quiet no-op.
      root.remove();

      expect((): void => {
        fixture.detectChanges();
      }).not.toThrow();

      // A closed `<dialog>` is not displayed, so nothing is shown. A non-modal
      // `show()` fallback is deliberately not offered.
      expect(requireDialog(root).open).toBeFalse();
    });

    it('finds its dialog structurally when the view query has not been refreshed', () => {
      // A MEASURED PROPERTY OF THE FRAMEWORK. Creating a component builds its
      // template's elements immediately, but view queries are only populated by the
      // first change-detection pass - so in this window the `<dialog>` and the
      // cancelling affordance are both in the DOM while both view references are
      // still undefined. That is precisely the window the structural lookup and the
      // first-focusable fallback exist for, and it is asserted positively here
      // rather than merely survived.
      const fixture = createUninitialisedDialog();
      const dialog = dialogOf(fixture);
      const cancelButton = cancelButtonOf(fixture);

      fixture.componentInstance.ngAfterViewInit();

      // Both fallbacks did their job: the dialog was located and opened modally, and
      // focus still reached the cancelling affordance rather than the destructive
      // one, so the safety guarantee holds even here.
      expect(dialog.open).toBeTrue();
      expect(dialog.matches(':modal')).toBeTrue();
      expect(document.activeElement).toBe(cancelButton);
    });

    it('treats every route as a quiet no-op when no dialog element can be found at all', () => {
      const fixture = createUninitialisedDialog();
      const component = fixture.componentInstance;
      const outcomes = observeOutcomes(fixture);

      // With the element removed while the view query is still unpopulated, NEITHER
      // lookup can succeed. This is the only state in which the dialog resolves to
      // nothing, and it is what every optional guard in the component is written to
      // survive: each must return quietly instead of dereferencing nothing.
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

      // THE COMPLEMENT that stops the expectation above from being vacuous.
      // `Escape` is an unambiguous instruction from the user and depends on locating
      // no element at all, so it must still cancel. If every route were inert
      // whenever the dialog could not be found, those no-op assertions would prove
      // nothing about target comparison or origin resolution.
      const event = pressKey(root, 'Escape');

      expect(event.defaultPrevented).toBeTrue();
      expect(outcomes.cancelled()).toBe(1);
      expect(outcomes.confirmed()).toBe(0);
    });

    it('does not try to reopen a dialog that is already open', () => {
      // `showModal()` raises `InvalidStateError` on an element that is already open,
      // which is why the component tests `open` before calling it. In production
      // that state can only arise from a template that violates the contract by
      // carrying a static `open` attribute - a defect another specification in this
      // suite guards against directly - but the value of the check is that it holds
      // whatever the cause, so the state is produced here directly.
      //
      // The spy deliberately does NOT call through: were the guard broken, the
      // failure should be reported as an unexpected call rather than as an
      // `InvalidStateError` raised somewhere inside change detection.
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
      // The complement of the expectation above, and the reason the component skips
      // only the OPENING rather than returning early: focus placement is what keeps
      // a stray `Enter` from deleting anything, so it has to run on this path too.
      // Focus is deliberately parked on the DESTRUCTIVE affordance first - the
      // browser's own dialog focusing steps would otherwise have already left it on
      // the cancelling one, and the expectation would prove nothing.
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
      // The one lifecycle path where the component opens the dialog and then places
      // no focus at all, reached through a template whose only affordance is
      // `disabled` and is therefore rejected by the tabbability filter. Opening must
      // still succeed: a confirmation that failed to appear because it had nothing
      // to focus would be strictly worse than one nobody can tab into.
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
      // A renamed template reference yields nothing from the view query with no
      // compile-time signal, which is the defect the structural fallback exists for.
      // Here the reference is gone while the markup is otherwise intact, so the
      // fallback runs and has real candidates to choose from.
      const fixture = TestBed.createComponent(ReferenceFreeDialogComponent);
      fixtures.push(fixture);
      const dialog = requireDialog(rootOf(fixture));

      fixture.detectChanges();

      expect(dialog.open).toBeTrue();
      expect(document.activeElement).toBe(requireElement(dialog, '#unreferenced-cancel'));
    });

    it('never lands on the destructive affordance when that fallback runs', () => {
      // The safety guarantee restated for the degraded path, because this is the
      // only route on which the component chooses a focus target by POSITION rather
      // than by name. Document order is what makes that choice safe, and the
      // template contract's cancel-before-confirm requirement is what guarantees the
      // order - so a template that reversed the two would surface here.
      const fixture = TestBed.createComponent(ReferenceFreeDialogComponent);
      fixtures.push(fixture);
      const dialog = requireDialog(rootOf(fixture));

      fixture.detectChanges();

      expect(document.activeElement).not.toBe(requireElement(dialog, '#unreferenced-confirm'));
    });
  });

  // =========================================================================
  // 16. THE COMPONENT PERFORMS NO NETWORK INPUT OR OUTPUT OF ITS OWN
  //
  //  The architectural rule turned into an executable one. This component asks a
  //  question and emits an intent; deleting is the consumer's responsibility, which
  //  is why nothing here may reach the network. The legacy affordance behaved the
  //  same way - it gated a postback that the PAGE then made.
  //
  //  `expectNone` fails if any matching request was issued; `match` returns them so
  //  the count can be shown; and `verify()` in `afterEach` catches anything either
  //  of them somehow missed.
  // =========================================================================
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
});
