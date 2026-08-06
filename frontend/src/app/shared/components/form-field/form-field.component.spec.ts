//
// Specification for the labelled-field wrapper.
//
// ---------------------------------------------------------------------------
// PROVENANCE - STATED HONESTLY, BECAUSE IT WOULD BE EASY TO IMPLY OTHERWISE
// ---------------------------------------------------------------------------
// NOTHING here is a ported test. The legacy tree contains ZERO automated tests of
// any kind - no test project, no test runner, no assertion library - so there is no
// predecessor suite to translate and no legacy expectation to preserve. Every
// expectation below was derived by READING the legacy markup and resource files and
// asserting the behaviour the migration plan requires of the replacement.
//
// The five files the expectations are drawn from, all read as unmodified reference
// inputs and none of them edited:
//   * `Website/controls/labelcontrol.ascx`          - the structure being replaced
//   * `Website/controls/helpbuttoncontrol.ascx`     - the same structure, un-nested
//   * `Website/admin/Security/editroles.ascx`       - how the control was consumed
//   * `Website/admin/Security/App_LocalResources/EditRoles.ascx.resx`
//                                                   - the REAL wording, used verbatim
//   * `Website/admin/Security/roles.ascx`           - a three-control field
//
// Every string fixture below is a VERBATIM resource value or markup attribute from
// those files. No placeholder wording is invented anywhere in this file: a test that
// asserts punctuation handling against invented text proves nothing about the screens
// actually being migrated.
//
// ---------------------------------------------------------------------------
// WHY THIS FILE CARRIES MORE WEIGHT THAN A SPECIFICATION USUALLY DOES
// ---------------------------------------------------------------------------
// `tsconfig.app.json` declares `files: ["src/main.ts"]`, so the production build
// type-checks BY IMPORT GRAPH. Measured in this checkout: the application graph
// reaches four of the shared components, and `FormFieldComponent` is NOT one of them -
// nothing outside its own folder imports it yet. `tsconfig.spec.json` has no `files`
// array and includes `src/**/*.spec.ts`, so THIS FILE is the only route by which
// `form-field.component.ts` AND its template are compiled under `strictTemplates` at
// all. That is why the suite renders the real template through the real component
// rather than asserting against the class in isolation: skip the render and the whole
// folder goes unverified.
//
// ---------------------------------------------------------------------------
// HARNESS DECISIONS, EACH ONE DELIBERATE
// ---------------------------------------------------------------------------
// * Karma with Jasmine, per the migration plan's own resolution of the runner
//   question: the acceptance command is
//   `ng test --watch=false --browsers=ChromeHeadless --code-coverage`, which is a
//   Karma invocation. A different runner would make that command invalid.
// * The component is standalone, so it is supplied through `imports`. The migration
//   plan states verbatim that there are no module declaration blocks anywhere in the
//   target, and this file adds none.
// * `provideHttpClient()` is registered BEFORE `provideHttpClientTesting()`. The order
//   is load-bearing: the testing function replaces the backend the first one
//   installed, so reversing them leaves the real backend in place.
// * `verify()` runs in `afterEach` and does double duty here. This component performs
//   no request, injects no service and reads no route, so a passing `verify()` is a
//   POSITIVE assertion of exactly that - the presentational layer holds no data
//   access, which is what the migration plan's code-organisation rule requires.
// * No coverage floor is asserted. `karma.conf.js` deliberately declares no
//   threshold, and inventing one here would fail the gate on grounds nobody set.
// * The polyfills are `zone.js` and `zone.js/testing`, so this harness is zone-based.
//   `TestBed.flushEffects()` was confirmed present on the INSTALLED
//   `@angular/core@19.2.25` (`core/testing/index.d.ts`), and `TestBed.tick()` was
//   confirmed ABSENT from that version. Neither is used: the component under test
//   declares no `effect()`, so every assertion below is synchronous and no
//   asynchronous scheduling primitive is needed.
//
// ---------------------------------------------------------------------------
// TYPE DISCIPLINE
// ---------------------------------------------------------------------------
// `querySelector` returns `Element | null`, and that null is narrowed by the small
// throwing helpers below rather than asserted away. Nothing in this file uses a
// non-null assertion, a cast to a loose type, or a compiler suppression comment, and
// nothing reaches for a path alias - the workspace declares none, so the component is
// imported as the sibling it is.
//

import { Component, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { FormFieldComponent } from './form-field.component';

// ---------------------------------------------------------------------------
// FIXTURES - EVERY ONE A MEASURED LEGACY VALUE
// ---------------------------------------------------------------------------

/** `RoleName.Text` with the `Suffix=":"` declared at `editroles.ascx:L23` applied. */
const LABEL_WITH_DECLARED_SUFFIX = 'Role Name:';

/** `RoleName.Text` verbatim - what the label above must render as. */
const LABEL_WITHOUT_SUFFIX = 'Role Name';

/**
 * `plRSVPCode.Text` verbatim.
 *
 * The colon is baked into the RESOURCE VALUE here, not declared as a suffix -
 * `editroles.ascx:L150` declares `plRSVPCode` with no `Suffix` attribute at all - so
 * this is the second of the six legacy punctuation routes and must normalise
 * identically to the first.
 */
const LABEL_WITH_BAKED_COLON = 'RSVP Code:';

/** `plRSVPCode.Text` with its punctuation resolved. */
const LABEL_WITH_BAKED_COLON_RESOLVED = 'RSVP Code';

/**
 * `PublicRole.Text` verbatim.
 *
 * `editroles.ascx:L52` declares `plIsPublic` with NO `Suffix` attribute, so the
 * question mark is part of the wording rather than punctuation the control added. It
 * carries meaning and is preserved.
 */
const LABEL_WITH_QUESTION_MARK = 'Public Role?';

/** `AutoAssignment.Text` verbatim - the same case at `editroles.ascx:L60`. */
const LABEL_WITH_QUESTION_MARK_SECOND = 'Auto Assignment?';

/**
 * `BillingPeriod.Text` verbatim.
 *
 * The parenthetical is an instruction rather than decoration, which the field's own
 * help text confirms: `BillingPeriod.Help` reads 'These two fields are used in
 * conjunction to enter a Billing Period. e.g 2 weeks, or 1 month'.
 */
const LABEL_WITH_PARENTHESES = 'Billing Period (Every)';

/** `TrialPeriod.Text` verbatim - the same shape for the trial period field. */
const LABEL_WITH_PARENTHESES_SECOND = 'Trial Period (Every)';

/** `plRoleGroups.Text` verbatim, declared with `Suffix=""` at `editroles.ascx:L44`. */
const LABEL_ALREADY_UNPUNCTUATED = 'Role Group';

/** `plIcon` declares `Text="Icon:"` inline at `editroles.ascx:L166`. */
const LABEL_WITH_INLINE_MARKUP_COLON = 'Icon:';

/** `RoleName.Help` verbatim. */
const HELP_TEXT = 'Enter the name of the role.';

/** `BillingPeriod.Help` verbatim - the help that documents a two-control field. */
const HELP_FOR_COMPOSITE_FIELD =
  'These two fields are used in conjunction to enter a Billing Period. e.g 2 weeks, or 1 month';

/**
 * `valRoleName.Text` verbatim, break markup and all.
 *
 * All NINE validator entries in `EditRoles.ascx.resx` open exactly like this, and 21
 * of the 33 `ErrorMessage` attributes across the in-scope markup do too. Interpolated
 * without cleaning, a person would literally read the characters `<br>`.
 */
const ERROR_WITH_LEADING_BREAK = '<br>You Must Enter a Valid Name';

/** What the message above must render as. */
const ERROR_WITHOUT_LEADING_BREAK = 'You Must Enter a Valid Name';

/** `valServiceFee1.Text` with its leading break resolved. */
const ERROR_FIRST_OF_PAIR = 'Service Fee Value Entered Is Not Valid';

/** `valServiceFee2.Text` with its leading break resolved. */
const ERROR_SECOND_OF_PAIR = 'Service Fee Must Be Greater Than or Equal to Zero';

/**
 * `ProcessorWarning.Text` verbatim, truncated at a sentence boundary.
 *
 * The bold markup is real: this entry sits in the very resource file this folder's
 * legacy screen uses. It is the fixture that proves markup arriving in a message is
 * shown as characters rather than rendered.
 */
const ERROR_WITH_BOLD_MARKUP = '<b>Warning:</b> You will need to configure the Payment Processor';

/**
 * The shape of `ModuleHelp.Text`, which opens with a heading and a paragraph.
 *
 * Reduced to its structure because the assertion is about the TAGS, not the prose.
 */
const HELP_WITH_BLOCK_MARKUP = '<h1>About</h1><p>Body</p>';

/**
 * Help text carrying a script element.
 *
 * Modelled on the single most dangerous entry found across the 37 in-scope resource
 * files: the `Advertising.Text` entry of
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` holds a live
 * remote advertising script block. Its tags are stored escaped in the resource file,
 * so a naive search of the sources reports none and would wrongly clear the risk. The
 * remote address is deliberately not reproduced here - the element itself is what is
 * under test.
 */
const HELP_WITH_SCRIPT = '<script src="pagead/show_ads.js"></script>Advertising';

/** The `id` of the primary projected control, as `editroles.ascx:L23` names one. */
const CONTROL_ID = 'txt-role-name';

/** A second `id`, for proving the association follows a change. */
const OTHER_CONTROL_ID = 'txt-billing-period';

// ---------------------------------------------------------------------------
// TYPED QUERY HELPERS
// ---------------------------------------------------------------------------
// Each narrows without a non-null assertion and without a cast, and each fails with a
// message that names what was missing rather than throwing on a null property access.

/** Finds one element, failing with a readable message when it is absent. */
function queryOrFail<T extends Element>(root: ParentNode, selector: string): T {
  const found = root.querySelector<T>(selector);

  if (found === null) {
    throw new Error(`Expected to find "${selector}"`);
  }

  return found;
}

/** Finds every matching element, in document order. */
function queryAll<T extends Element>(root: ParentNode, selector: string): readonly T[] {
  return Array.from(root.querySelectorAll<T>(selector));
}

/** Whether nothing in the subtree matches the selector. */
function isAbsent(root: ParentNode, selector: string): boolean {
  return root.querySelector(selector) === null;
}

/** An element's text with runs of whitespace collapsed, for comparing rendered words. */
function collapsedText(element: Element): string {
  const content = element.textContent;

  return content === null ? '' : content.replace(/\s+/g, ' ').trim();
}

/**
 * An element's text exactly as the DOM holds it.
 *
 * Used where a newline is the thing under test, since collapsing would erase it.
 */
function rawText(element: Element): string {
  const content = element.textContent;

  return content === null ? '' : content;
}

/** Reads an attribute that the assertion requires to be present. */
function attributeOrFail(element: Element, name: string): string {
  const value = element.getAttribute(name);

  if (value === null) {
    throw new Error(`Expected "${name}" to be present on <${element.tagName.toLowerCase()}>`);
  }

  return value;
}

/**
 * Resolves an identifier reference within the fixture.
 *
 * An attribute selector rather than an identifier selector, so a value that would
 * need escaping in a selector cannot turn a missing element into a thrown syntax
 * error and hide the real result.
 */
function resolveIdReference(root: ParentNode, id: string): Element | null {
  return root.querySelector(`[id="${id}"]`);
}

/**
 * Splits a space-separated reference list into its individual identifiers.
 *
 * Tolerates the double spaces and leading whitespace a template can produce, because
 * an assertion about references should fail on a DANGLING reference and never on
 * incidental spacing.
 */
function referenceList(value: string): readonly string[] {
  return value.split(/\s+/).filter((entry) => entry.length > 0);
}

/** Whether every identifier in a reference list resolves to a real element. */
function everyReferenceResolves(root: ParentNode, value: string): boolean {
  const references = referenceList(value);

  return (
    references.length > 0 &&
    references.every((reference) => resolveIdReference(root, reference) !== null)
  );
}

/** The text of every element a reference list points at, joined as a reader hears it. */
function resolvedReferenceText(root: ParentNode, value: string): string {
  return referenceList(value)
    .map((reference) => {
      const target = resolveIdReference(root, reference);

      return target === null ? '' : collapsedText(target);
    })
    .join(' ')
    .trim();
}

// ---------------------------------------------------------------------------
// INPUT APPLICATION
// ---------------------------------------------------------------------------

/**
 * The component's five inputs, as an optional bundle.
 *
 * Mirrors the component's own declared types exactly - notably that `error` accepts a
 * single string OR a read-only list - so a widening of one and not the other cannot
 * pass unnoticed.
 */
interface FieldInputs {
  readonly label?: string;
  readonly for?: string;
  readonly required?: boolean;
  readonly help?: string;
  readonly error?: string | readonly string[] | null;
}

/**
 * Pushes inputs in through the component reference, then renders.
 *
 * This is the correct way to drive a component that declares the on-push strategy: an
 * input write marks the view for check, whereas assigning to an instance field does
 * not. Every specification below changes state this way or through the host's own
 * bindings, and none assigns to an instance field expecting a re-render.
 */
function applyInputs(fixture: ComponentFixture<FormFieldComponent>, inputs: FieldInputs): void {
  if (inputs.label !== undefined) {
    fixture.componentRef.setInput('label', inputs.label);
  }

  if (inputs.for !== undefined) {
    fixture.componentRef.setInput('for', inputs.for);
  }

  if (inputs.required !== undefined) {
    fixture.componentRef.setInput('required', inputs.required);
  }

  if (inputs.help !== undefined) {
    fixture.componentRef.setInput('help', inputs.help);
  }

  if (inputs.error !== undefined) {
    fixture.componentRef.setInput('error', inputs.error);
  }

  fixture.detectChanges();
}

// ---------------------------------------------------------------------------
// THE PROJECTION HOST
// ---------------------------------------------------------------------------

/**
 * A host that projects real controls into the field.
 *
 * It lives in this file rather than a separate one because it is test scaffolding and
 * not a component of the application. It exists because the slot is the part of the
 * contract that cannot be exercised any other way: the component renders no control
 * of its own, and the legacy screens routinely put SEVERAL controls under one label -
 * `editroles.ascx:L98-L115` places a text box, a literal pair of non-breaking spaces
 * and a drop-down list under the single `plBillingPeriod` label, `L130-L147` repeats
 * the shape for the trial period, and `roles.ascx:L5-L16` places a drop-down list, an
 * edit link and a delete image button under one label.
 *
 * It deliberately uses the DEFAULT change-detection strategy. That is what makes it a
 * valid probe of an on-push child: a change here propagates into the child only
 * through the input bindings, which is precisely the path a real feature template
 * uses.
 */
@Component({
  selector: 'app-form-field-host',
  standalone: true,
  imports: [FormFieldComponent],
  template: `
    <app-form-field
      [label]="label"
      [for]="controlId"
      [required]="required"
      [help]="help"
      [error]="error"
    >
      <input [attr.id]="controlId.length > 0 ? controlId : null" type="text" />
      @if (showFrequency) {
        <select [attr.aria-label]="frequencyName">
          <option value="M">Month</option>
        </select>
      }
    </app-form-field>
  `,
})
class FormFieldHostComponent {
  /** The field label, defaulting to the composite field the resource file documents. */
  public label = LABEL_WITH_PARENTHESES;

  /** The primary control's identifier; an empty value renders no `id` and no `for`. */
  public controlId = CONTROL_ID;

  /** Whether the field advertises itself as required. */
  public required = false;

  /** The help text; blank means the field has none. */
  public help = '';

  /** The validation messages, in any of the shapes the input accepts. */
  public error: string | readonly string[] | null = null;

  /** Whether the second control of a composite field is projected. */
  public showFrequency = false;

  /** A consumer-supplied accessible name for the second control, or null for none. */
  public frequencyName: string | null = null;

  /**
   * The field instance, reached without a cast.
   *
   * Used by exactly one specification, which needs to write to an instance field in
   * order to prove that writing to an instance field is NOT how a change reaches the
   * screen.
   */
  @ViewChild(FormFieldComponent) public field: FormFieldComponent | undefined = undefined;
}

/**
 * A second host that projects the OTHER control shapes the legacy screens used.
 *
 * It exists because `roles.ascx:L5-L16` is not a text-box-and-drop-down field: it puts a
 * drop-down list, an EDIT HYPERLINK WRAPPING AN IMAGE and an IMAGE BUTTON under one
 * label. Those three shapes name themselves in three different ways - an anchor from its
 * contents, an image button from its alternative text, a drop-down list not at all - and
 * the component has to tell them apart before deciding whether to supply a fallback
 * name. A host that only ever projects a text box cannot exercise any of that.
 *
 * Every part is behind its own flag so each specification renders exactly the one shape
 * it is about. The visible span exists so that a consumer-supplied reference in these
 * specifications points at something real, exactly as a production caller would.
 */
@Component({
  selector: 'app-form-field-rich-host',
  standalone: true,
  imports: [FormFieldComponent],
  template: `
    <app-form-field [label]="label" [for]="controlId">
      <select [attr.id]="controlId"></select>
      <span id="probe-custom-name">Frequency</span>
      @if (showEditLink) {
        <a class="probe-link" href="#edit">{{ linkText }}</a>
      }
      @if (showImageButton) {
        <input class="probe-image" type="image" [attr.alt]="imageAlt" />
      }
      @if (showSubmit) {
        <input class="probe-submit" type="submit" [attr.value]="submitValue" />
      }
      @if (showLabelledCheckbox) {
        <input class="probe-checkbox" id="probe-checkbox" type="checkbox" />
        <label for="probe-checkbox">{{ checkboxText }}</label>
      }
      @if (showTextarea) {
        <textarea class="probe-textarea"></textarea>
      }
      @if (showEditable) {
        <div class="probe-editable" contenteditable="true"></div>
      }
      @if (showCommandButton) {
        <button class="probe-button" type="button">{{ commandText }}</button>
      }
      @if (showSwitch) {
        <div class="probe-switch" role="switch">{{ switchText }}</div>
      }
    </app-form-field>
  `,
})
class FormFieldRichHostComponent {
  /** `plRoleGroups.Text`, the label `roles.ascx:L7` puts over this whole group. */
  public label = LABEL_ALREADY_UNPUNCTUATED;

  /** The drop-down list identifier, matching `roles.ascx:L9`. */
  public controlId = 'cbo-role-groups';

  /** Whether the edit hyperlink of `roles.ascx:L10-L12` is projected. */
  public showEditLink = false;

  /** The hyperlink text; blank leaves the anchor with nothing to name it. */
  public linkText = 'Edit';

  /** Whether the delete image button of `roles.ascx:L13` is projected. */
  public showImageButton = false;

  /** Its alternative text; null leaves the button unnamed. */
  public imageAlt: string | null = 'Delete';

  /** Whether a submit button is projected. */
  public showSubmit = false;

  /** Its value, which is what names a submit button. */
  public submitValue: string | null = 'Update';

  /** Whether a check box carrying its own native label is projected. */
  public showLabelledCheckbox = false;

  /** The check-box label wording. */
  public checkboxText = 'Public Role';

  /** Whether a multi-line text control is projected. */
  public showTextarea = false;

  /** Whether an editable region is projected. */
  public showEditable = false;

  /** Whether a command button is projected, as the four at `editroles.ascx:L179-L189`. */
  public showCommandButton = false;

  /** Its visible wording, which is what names a button. */
  public commandText = 'Manage Users';

  /** Whether a control named only by an accessibility role is projected. */
  public showSwitch = false;

  /** Its visible wording. */
  public switchText = 'Auto Assignment';
}


describe('FormFieldComponent', () => {
  /**
   * The mock HTTP backend, asserted empty after every specification.
   *
   * Injected even though nothing here issues a request, because that is the point:
   * an unexpected request is the commonest way a false green survives, and this
   * component is required to make none.
   */
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // All three are supplied through `imports`, which is only possible because all
      // three are standalone. The migration plan forbids module declaration blocks
      // outright, and supplying a non-standalone component this way would fail at
      // configuration.
      imports: [FormFieldComponent, FormFieldHostComponent, FormFieldRichHostComponent],
      // The real client MUST be provided first; the testing function then replaces the
      // backend it installed. Reversed, the real backend survives and `verify()` can
      // never see anything.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Doubles as a positive assertion: this presentational component performs zero
    // network access, which is what the migration plan's code-organisation rule
    // requires of the shared layer.
    httpMock.verify();
  });

  /** Renders the component on its own, for the inputs that need no projected control. */
  function createField(inputs: FieldInputs = {}): ComponentFixture<FormFieldComponent> {
    const fixture = TestBed.createComponent(FormFieldComponent);

    applyInputs(fixture, inputs);

    return fixture;
  }

  /** Renders the component inside the projection host. */
  function createHost(): ComponentFixture<FormFieldHostComponent> {
    const fixture = TestBed.createComponent(FormFieldHostComponent);

    fixture.detectChanges();

    return fixture;
  }

  /** The component's own host element, obtained by assignment rather than by cast. */
  function rootOf(fixture: ComponentFixture<FormFieldComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** The projection host's element, obtained the same way. */
  function hostRootOf(fixture: ComponentFixture<FormFieldHostComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** Renders the component inside the host that projects the richer control shapes. */
  function createRichHost(): ComponentFixture<FormFieldRichHostComponent> {
    const fixture = TestBed.createComponent(FormFieldRichHostComponent);

    fixture.detectChanges();

    return fixture;
  }

  /** The rich host's element, obtained by assignment rather than by cast. */
  function richRootOf(fixture: ComponentFixture<FormFieldRichHostComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** The rendered label element. */
  function labelOf(root: ParentNode): HTMLLabelElement {
    return queryOrFail<HTMLLabelElement>(root, 'label.form-field__label');
  }

  /** The projection wrapper that carries the composite group semantics. */
  function slotOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__control');
  }

  /** The help disclosure button. */
  function toggleOf(root: ParentNode): HTMLButtonElement {
    return queryOrFail<HTMLButtonElement>(root, 'button.form-field__help-toggle');
  }

  /** The revealed help region. */
  function helpRegionOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__help');
  }

  /** The validation-message region. */
  function errorRegionOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__errors');
  }

  /** Every rendered validation message, in document order. */
  function errorTexts(root: ParentNode): readonly string[] {
    return queryAll<HTMLElement>(root, '.form-field__error').map((message) =>
      collapsedText(message),
    );
  }

  /** Activates the help disclosure with a real click and re-renders. */
  function clickToggle(fixture: ComponentFixture<FormFieldComponent>): void {
    toggleOf(rootOf(fixture)).click();
    fixture.detectChanges();
  }

  // =========================================================================
  // 4.1  THE LABEL AND ITS NATIVE ASSOCIATION
  // =========================================================================
  // `labelcontrol.ascx:L2` is a real `<label>` element, which is the whole mechanism
  // by which the legacy control associated wording with a control. Nothing else in the
  // legacy file does that job, so the replacement must keep it.

  describe('the label', () => {
    it('renders a real label element, which is how the legacy control associated wording', () => {
      const label = labelOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX })));

      expect(label.tagName.toLowerCase())
        .withContext('`labelcontrol.ascx:L2` is a native label, not a styled span')
        .toBe('label');
    });

    it('renders the supplied wording', () => {
      const label = labelOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX })));

      expect(collapsedText(label)).toBe(LABEL_WITHOUT_SUFFIX);
    });

    it('carries the projected control identifier as its `for`', () => {
      const label = labelOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID })));

      expect(attributeOrFail(label, 'for')).toBe(CONTROL_ID);
    });

    it('follows a change of control identifier', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });

      applyInputs(fixture, { for: OTHER_CONTROL_ID });

      expect(attributeOrFail(labelOf(rootOf(fixture)), 'for')).toBe(OTHER_CONTROL_ID);
    });

    it('omits the `for` attribute ENTIRELY when no control identifier is supplied', () => {
      // A measured legacy shape rather than a defensive nicety: `controlname` is absent
      // on 5 of the 186 in-scope `<dnn:label>` declarations, and `plRSVPCode` at
      // `editroles.ascx:L150` is one of the labels that carries no resource key either.
      // Rendering `for=""` would turn each of those into a reference that names nothing
      // and reports as an error to any auditing tool, so the attribute must be ABSENT
      // and not merely empty.
      const label = labelOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: '' })));

      expect(label.hasAttribute('for'))
        .withContext('an empty control identifier must remove the attribute, not blank it')
        .toBeFalse();
    });

    it('renders unconditionally, keeping the field shape when no wording is supplied', () => {
      // `labelcontrol.ascx:L2` renders its label element with no condition attached, so
      // a field with no wording keeps the same shape rather than collapsing.
      const label = labelOf(rootOf(createField({ label: '' })));

      expect(collapsedText(label)).toBe('');
    });

    it('carries an identifier so other elements can name themselves from it', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });
      const label = labelOf(rootOf(fixture));

      expect(attributeOrFail(label, 'id'))
        .withContext('the component publishes this identifier as `labelId`')
        .toBe(fixture.componentInstance.labelId());
    });
  });

  // =========================================================================
  // 4.2  PUNCTUATION - SIX LEGACY OUTCOMES NORMALISED TO ONE
  // =========================================================================
  // MIGRATION: six legacy punctuation outcomes are normalised to one here. Measured
  // across all 186 in-scope `<dnn:label>` instances - `Suffix=":"` 40 times,
  // the attribute ABSENT 133 times (72%, and no punctuation at all), `Suffix=""`
  // explicitly empty 12 times, `suffix="?"` once, plus punctuation baked into a
  // resource value and punctuation baked into an inline `Text=` attribute. The
  // component strips ONE trailing colon whatever its origin and adds none of its own,
  // which matches the dominant legacy majority.

  describe('label punctuation', () => {
    /** Renders a label and returns the words a reader sees. */
    function renderedLabel(text: string): string {
      return collapsedText(labelOf(rootOf(createField({ label: text }))));
    }

    it('strips a colon that arrived through the suffix attribute', () => {
      // `editroles.ascx:L23` declares `plRoleName` with `Suffix=":"` over the resource
      // value `RoleName.Text` = 'Role Name'.
      expect(renderedLabel(LABEL_WITH_DECLARED_SUFFIX)).toBe(LABEL_WITHOUT_SUFFIX);
    });

    it('strips a colon that was baked into the resource value', () => {
      // `plRSVPCode.Text` is 'RSVP Code:' and `editroles.ascx:L150` declares no suffix,
      // so the same visible outcome arrives by a different route and must normalise the
      // same way.
      expect(renderedLabel(LABEL_WITH_BAKED_COLON)).toBe(LABEL_WITH_BAKED_COLON_RESOLVED);
    });

    it('strips a colon that was baked into an inline markup attribute', () => {
      // `editroles.ascx:L166` declares `plIcon` with `Text="Icon:"` and no resource key
      // at all - the third route to the same character.
      expect(renderedLabel(LABEL_WITH_INLINE_MARKUP_COLON)).toBe('Icon');
    });

    it('PRESERVES a question mark, which carries meaning rather than decoration', () => {
      expect(renderedLabel(LABEL_WITH_QUESTION_MARK)).toBe(LABEL_WITH_QUESTION_MARK);
      expect(renderedLabel(LABEL_WITH_QUESTION_MARK_SECOND)).toBe(LABEL_WITH_QUESTION_MARK_SECOND);
    });

    it('PRESERVES parentheses, which the field help proves are an instruction', () => {
      // `BillingPeriod.Help` reads 'These two fields are used in conjunction to enter a
      // Billing Period', so '(Every)' tells the reader how to fill the field in.
      expect(renderedLabel(LABEL_WITH_PARENTHESES)).toBe(LABEL_WITH_PARENTHESES);
      expect(renderedLabel(LABEL_WITH_PARENTHESES_SECOND)).toBe(LABEL_WITH_PARENTHESES_SECOND);
    });

    it('leaves an already unpunctuated label untouched', () => {
      // `plRoleGroups` is declared with `Suffix=""` at `editroles.ascx:L44` over the
      // resource value 'Role Group' - the explicitly-empty case.
      expect(renderedLabel(LABEL_ALREADY_UNPUNCTUATED)).toBe(LABEL_ALREADY_UNPUNCTUATED);
    });

    it('strips only ONE trailing colon', () => {
      expect(renderedLabel('Role Name::')).toBe('Role Name:');
    });

    it('leaves an INTERIOR colon alone', () => {
      // Only a trailing colon is punctuation the control added; a colon inside the
      // wording is wording.
      expect(renderedLabel('Fee: Currency')).toBe('Fee: Currency');
    });

    it('strips the whitespace that sat in front of a stripped colon', () => {
      expect(renderedLabel('Role Name :')).toBe(LABEL_WITHOUT_SUFFIX);
    });
  });


  // =========================================================================
  // 4.3  THE MESSAGE INPUT ACCEPTS ONE STRING OR A LIST
  // =========================================================================
  // MIGRATION: the shape is WIDENED from a single string, and the widening is forced by
  // measurement rather than chosen for convenience. `editroles.ascx` puts TWO
  // `CompareValidator`s on a single control FOUR separate times - ServiceFee at
  // L89-L96, BillingPeriod at L104-L114, TrialFee at L122-L128 and TrialPeriod at
  // L136-L146 - so two messages can be outstanding on one field at once and a single
  // string cannot represent them. There is also no validation summary anywhere in the
  // in-scope screens, so this region is the only error surface the ported screens have:
  // a message dropped here is a message nobody sees.

  describe('the validation messages', () => {
    it('render nothing at all while the field is valid', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX }));

      expect(isAbsent(root, '.form-field__errors'))
        .withContext('no region, no empty element and no reserved space')
        .toBeTrue();
    });

    it('render exactly one message from a single string', () => {
      const root = rootOf(
        createField({ label: LABEL_WITHOUT_SUFFIX, error: ERROR_WITH_LEADING_BREAK }),
      );

      expect(errorTexts(root)).toEqual([ERROR_WITHOUT_LEADING_BREAK]);
    });

    it('render BOTH messages of a list, in the order supplied', () => {
      // The real pair from `editroles.ascx:L89-L96`: a data-type check followed by a
      // greater-than-or-equal check, both on `txtServiceFee`.
      const root = rootOf(
        createField({
          label: 'Service Fee',
          error: [ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR],
        }),
      );

      expect(errorTexts(root)).toEqual([ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR]);
    });

    it('render two IDENTICAL messages without failing on a duplicated key', () => {
      // The template must track by index rather than by value. A server can genuinely
      // report the same failure twice, and tracking by value would raise a duplicate-key
      // error at exactly that moment.
      const root = rootOf(
        createField({
          label: 'Service Fee',
          error: [ERROR_FIRST_OF_PAIR, ERROR_FIRST_OF_PAIR],
        }),
      );

      expect(errorTexts(root)).toEqual([ERROR_FIRST_OF_PAIR, ERROR_FIRST_OF_PAIR]);
    });

    it('render no region for null', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, error: null }));

      expect(isAbsent(root, '.form-field__errors')).toBeTrue();
    });

    it('render no region for undefined', () => {
      // Set directly rather than through the bundle, because an absent bundle member and
      // an explicit `undefined` are different assertions and both must hold.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, error: ERROR_FIRST_OF_PAIR });

      fixture.componentRef.setInput('error', undefined);
      fixture.detectChanges();

      expect(isAbsent(rootOf(fixture), '.form-field__errors')).toBeTrue();
    });

    it('render no region for an empty string', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, error: '' }));

      expect(isAbsent(root, '.form-field__errors')).toBeTrue();
    });

    it('render no region for a string of only whitespace', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, error: '   ' }));

      expect(isAbsent(root, '.form-field__errors'))
        .withContext('an empty message is not a message')
        .toBeTrue();
    });

    it('drop blank entries from a list rather than rendering empty messages', () => {
      const root = rootOf(
        createField({
          label: 'Service Fee',
          error: ['', ERROR_FIRST_OF_PAIR, '   ', ERROR_SECOND_OF_PAIR, ''],
        }),
      );

      expect(errorTexts(root)).toEqual([ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR]);
    });

    it('render no region when EVERY entry of a list is blank', () => {
      const root = rootOf(createField({ label: 'Service Fee', error: ['', '   '] }));

      expect(isAbsent(root, '.form-field__errors')).toBeTrue();
    });

    it('read back in one canonical shape whatever was written', () => {
      // The read type is narrower than the write type on purpose, so nothing downstream
      // has to repeat the normalisation or guess which form it received.
      const fixture = createField({ label: 'Service Fee', error: ERROR_WITH_LEADING_BREAK });

      expect(fixture.componentInstance.error).toEqual([ERROR_WITHOUT_LEADING_BREAK]);
    });

    it('are removed rather than emptied once the field becomes valid', () => {
      const fixture = createField({ label: 'Service Fee', error: ERROR_FIRST_OF_PAIR });

      expect(isAbsent(rootOf(fixture), '.form-field__errors')).toBeFalse();

      applyInputs(fixture, { error: null });

      expect(isAbsent(rootOf(fixture), '.form-field__errors'))
        .withContext('a hidden region still occupies the accessibility tree')
        .toBeTrue();
    });

    it('are announced as ONE interruption, on the region rather than on each message', () => {
      const root = rootOf(
        createField({
          label: 'Service Fee',
          error: [ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR],
        }),
      );
      const region = errorRegionOf(root);

      expect(attributeOrFail(region, 'role'))
        .withContext('a validation failure results from what the person just did')
        .toBe('alert');
      expect(
        queryAll<HTMLElement>(root, '.form-field__error').every(
          (message) => message.hasAttribute('role') === false,
        ),
      )
        .withContext('two messages arriving together must not announce twice')
        .toBeTrue();
    });

    it('carry a region identifier the component publishes', () => {
      const fixture = createField({
        label: 'Service Fee',
        for: CONTROL_ID,
        error: ERROR_FIRST_OF_PAIR,
      });

      expect(attributeOrFail(errorRegionOf(rootOf(fixture)), 'id')).toBe(
        fixture.componentInstance.errorId(),
      );
    });
  });

  // =========================================================================
  // 4.4  LEADING BREAK MARKUP - THE MEASURED HAZARD
  // =========================================================================
  // `valRoleName.Text` in the resource file for this screen is literally
  // '<br>You Must Enter a Valid Name'. All NINE validator entries in that file open the
  // same way, and 21 of the 33 `ErrorMessage` attributes across the in-scope markup do
  // too. The legacy control interpreted those characters as markup; a component that
  // renders text must remove them, or a person reads them.

  describe('legacy break markup', () => {
    /** Renders one message and returns the text a reader sees, uncollapsed. */
    function renderedMessage(message: string): string {
      return rawText(
        queryOrFail<HTMLElement>(
          rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, error: message })),
          '.form-field__error',
        ),
      );
    }

    it('is removed from the front of a message, leaving the wording intact', () => {
      const rendered = renderedMessage(ERROR_WITH_LEADING_BREAK);

      expect(rendered).toBe(ERROR_WITHOUT_LEADING_BREAK);
      expect(rendered.includes('<br'))
        .withContext('no reader may ever see the characters of a break tag')
        .toBeFalse();
    });

    it('is removed in every spelling the legacy sources use, case-insensitively', () => {
      // The markup in scope is inconsistent, so each of these is a shape that can
      // genuinely arrive. All must resolve to the same wording.
      const spellings: readonly string[] = [
        '<br>',
        '<br/>',
        '<br />',
        '<br  />',
        '<BR/>',
        '<Br >',
        '< br >',
      ];

      for (const spelling of spellings) {
        expect(renderedMessage(`${spelling}${ERROR_WITHOUT_LEADING_BREAK}`))
          .withContext(`the spelling ${spelling} must be removed`)
          .toBe(ERROR_WITHOUT_LEADING_BREAK);
      }
    });

    it('is removed when REPEATED at the front', () => {
      expect(renderedMessage(`<br><br>${ERROR_WITHOUT_LEADING_BREAK}`)).toBe(
        ERROR_WITHOUT_LEADING_BREAK,
      );
    });

    it('is removed when mixed spellings repeat at the front', () => {
      expect(renderedMessage(`<br /><BR>${ERROR_WITHOUT_LEADING_BREAK}`)).toBe(
        ERROR_WITHOUT_LEADING_BREAK,
      );
    });

    it('is removed from the END of a message as well', () => {
      expect(renderedMessage(`${ERROR_WITHOUT_LEADING_BREAK}<br>`)).toBe(
        ERROR_WITHOUT_LEADING_BREAK,
      );
    });

    it('becomes a READABLE BOUNDARY when it sits inside the wording', () => {
      // An interior break must not silently glue two sentences together. The component
      // resolves it to a newline character, which keeps the words separated in the
      // accessible text and leaves the visual treatment to the stylesheet.
      const rendered = renderedMessage(`${ERROR_FIRST_OF_PAIR}<br>${ERROR_SECOND_OF_PAIR}`);

      expect(rendered).toBe(`${ERROR_FIRST_OF_PAIR}\n${ERROR_SECOND_OF_PAIR}`);
      expect(rendered.includes(`Valid${ERROR_SECOND_OF_PAIR}`))
        .withContext('the two sentences must not be concatenated into one word')
        .toBeFalse();
    });

    it('is removed from the label as well as from a message', () => {
      const label = labelOf(
        rootOf(createField({ label: `<br>${LABEL_WITH_DECLARED_SUFFIX}` })),
      );

      expect(collapsedText(label)).toBe(LABEL_WITHOUT_SUFFIX);
    });

    it('is removed from the help text as well', () => {
      const fixture = createField({
        label: LABEL_WITHOUT_SUFFIX,
        help: `<br>${HELP_TEXT}`,
      });

      clickToggle(fixture);

      expect(rawText(helpRegionOf(rootOf(fixture)))).toBe(HELP_TEXT);
    });

    it('leaves a message that carries none of it completely unchanged', () => {
      expect(renderedMessage(ERROR_FIRST_OF_PAIR)).toBe(ERROR_FIRST_OF_PAIR);
    });
  });


  // =========================================================================
  // 4.5  PLAIN TEXT ONLY - THE UNTRUSTED-MARKUP PROHIBITION, ASSERTED
  // =========================================================================
  // Measured across the 37 in-scope resource files: 76 values carry an HTML tag and 29
  // open with break markup. The decisive one is the `Advertising.Text` entry of
  // `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx`, which holds a live
  // remote advertising script block; the tags are stored escaped, so a naive search of
  // the sources returns nothing and would wrongly clear the risk. The resource file for
  // this screen holds two more: `ProcessorWarning.Text` opens with bold markup and
  // `ModuleHelp.Text` opens with a heading and a paragraph.
  //
  // MIGRATION: rendering every string as plain text is a security boundary rather than a
  // stylistic preference. The legacy application reached the same conclusion by hand:
  // `Website/admin/Security/AccessDenied.ascx.vb:L43` renders its untrusted query-string
  // message through an HTML encode of a URL decode before display, and both of its
  // branches raise the same warning severity.
  //
  // These are the regression tests that stop anyone reintroducing a trusted-markup
  // binding on any of the three text surfaces.

  describe('untrusted markup', () => {
    it('shows bold markup in a MESSAGE as characters and creates no element', () => {
      const root = rootOf(
        createField({ label: 'Service Fee', error: ERROR_WITH_BOLD_MARKUP }),
      );
      const region = errorRegionOf(root);

      expect(region.querySelector('b'))
        .withContext('a trusted-markup binding here would render the resource value as HTML')
        .toBeNull();
      expect(rawText(region).includes('<b>'))
        .withContext('the characters themselves must reach the reader')
        .toBeTrue();
      expect(rawText(queryOrFail<HTMLElement>(region, '.form-field__error'))).toBe(
        ERROR_WITH_BOLD_MARKUP,
      );
    });

    it('shows a SCRIPT element in help text as characters and executes nothing', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_WITH_SCRIPT });

      clickToggle(fixture);

      const root = rootOf(fixture);

      expect(isAbsent(root, 'script'))
        .withContext('no script element may be created anywhere in the fixture')
        .toBeTrue();
      expect(rawText(helpRegionOf(root))).toBe(HELP_WITH_SCRIPT);
    });

    it('shows block markup in help text as characters and creates no heading', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_WITH_BLOCK_MARKUP });

      clickToggle(fixture);

      const region = helpRegionOf(rootOf(fixture));

      expect(region.querySelector('h1')).toBeNull();
      expect(region.querySelector('p'))
        .withContext('the region is itself a paragraph; the value must add no nested one')
        .toBeNull();
      expect(rawText(region)).toBe(HELP_WITH_BLOCK_MARKUP);
    });

    it('shows markup in a LABEL as characters and creates no element', () => {
      const label = labelOf(rootOf(createField({ label: '<b>Role Name</b>' })));

      expect(label.querySelector('b')).toBeNull();
      expect(collapsedText(label)).toBe('<b>Role Name</b>');
    });
  });

  // =========================================================================
  // 4.6  THE HELP AFFORDANCE - A SIBLING, AND KEYBOARD-REACHABLE
  // =========================================================================

  describe('the help affordance', () => {
    it('is absent when no help text is supplied', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: '' }));

      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeTrue();
    });

    it('is absent when the supplied help text is only whitespace', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: '   ' }));

      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeTrue();
    });

    it('is NOT a descendant of the label element', () => {
      // MIGRATION: the primary regression test for the first accessibility divergence.
      // `labelcontrol.ascx` nests the affordance INSIDE the label - L3 to L5 sit between
      // the opening tag on L2 and the closing tag on L7 - which is invalid, because a
      // button may only be a label descendant when it IS the labelled control, and
      // ambiguous, because a click anywhere in a label is forwarded to the control it
      // names. The un-nested arrangement is not invented to fix that: the legacy codebase
      // already ships it in `Website/controls/helpbuttoncontrol.ascx`, which is
      // `labelcontrol.ascx` L3 to L11 with the label wrapper removed.
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT }));
      const label = labelOf(root);
      const toggle = toggleOf(root);

      expect(label.querySelector('button'))
        .withContext('the legacy nesting must not be reproduced')
        .toBeNull();
      expect(label.contains(toggle))
        .withContext('the affordance must sit outside the label entirely')
        .toBeFalse();
    });

    it('is a button that can never submit a form', () => {
      // The faithful translation of `CausesValidation="False"` on `labelcontrol.ascx:L3`.
      const toggle = toggleOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT })));

      expect(toggle.tagName.toLowerCase()).toBe('button');
      expect(toggle.type).toBe('button');
    });

    it('does NOT carry a negative tab index', () => {
      // MIGRATION: the regression test for the second accessibility divergence. The legacy
      // control withdraws the affordance from the tab order TWICE OVER - on the link button at
      // `labelcontrol.ascx:L3` and again on the image nested inside it at L4 - and repeats
      // the defect at `helpbuttoncontrol.ascx` L2 and L3 and at
      // `Website/controls/sectionheadcontrol.ascx`, five declarations in all. The legacy
      // help was therefore operable by pointer only.
      const toggle = toggleOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT })));

      expect(toggle.getAttribute('tabindex'))
        .withContext('a negative tab index would reproduce the legacy keyboard defect')
        .not.toBe('-1');
      expect(toggle.tabIndex)
        .withContext('a native button with no tab index override is reachable by Tab')
        .toBe(0);
    });

    it('has a non-empty accessible name composed from real text on the page', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT }));
      const toggle = toggleOf(root);
      const reference = attributeOrFail(toggle, 'aria-labelledby');

      expect(everyReferenceResolves(root, reference))
        .withContext('every identifier in the name reference must resolve')
        .toBeTrue();

      const name = resolvedReferenceText(root, reference);

      expect(name.length)
        .withContext('the visible content is an icon, so the name must come from text')
        .toBeGreaterThan(0);
      expect(name)
        .withContext('composed so twenty help buttons on one screen are distinguishable')
        .toBe(`Help ${LABEL_WITHOUT_SUFFIX}`);
    });

    it('names itself from its own text alone when the field carries no label', () => {
      const root = rootOf(createField({ label: '', help: HELP_TEXT }));
      const reference = attributeOrFail(toggleOf(root), 'aria-labelledby');

      expect(referenceList(reference).length)
        .withContext('an empty label must not be pointed at')
        .toBe(1);
      expect(resolvedReferenceText(root, reference)).toBe('Help');
    });

    it('reports itself collapsed before it is activated', () => {
      const toggle = toggleOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT })));

      expect(attributeOrFail(toggle, 'aria-expanded'))
        .withContext('the legacy panel started hidden, so this starts collapsed')
        .toBe('false');
    });

    it('reports itself expanded after a real click, and collapsed again after a second', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      clickToggle(fixture);

      expect(attributeOrFail(toggleOf(rootOf(fixture)), 'aria-expanded')).toBe('true');

      clickToggle(fixture);

      expect(attributeOrFail(toggleOf(rootOf(fixture)), 'aria-expanded')).toBe('false');
    });

    it('omits the control reference while there is nothing to control', () => {
      // The revealed block is REMOVED from the document when collapsed rather than kept
      // and hidden, so a reference to it would resolve to nothing. The expanded state is
      // carried by the expansion attribute either way.
      const toggle = toggleOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT })));

      expect(toggle.hasAttribute('aria-controls')).toBeFalse();
    });

    it('points its control reference at an element that EXISTS once revealed', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      clickToggle(fixture);

      const root = rootOf(fixture);
      const reference = attributeOrFail(toggleOf(root), 'aria-controls');

      expect(resolveIdReference(root, reference))
        .withContext('no dangling reference, in either direction')
        .not.toBeNull();
      expect(reference).toBe(fixture.componentInstance.helpId());
    });

    it('disappears together with its region when the help text is cleared while open', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      clickToggle(fixture);
      applyInputs(fixture, { help: '' });

      const root = rootOf(fixture);

      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeTrue();
      expect(isAbsent(root, '.form-field__help'))
        .withContext('the announced state can never contradict what is on the screen')
        .toBeTrue();
    });

    it('uses NO raster image anywhere', () => {
      // `labelcontrol.ascx:L4` points at a 344-byte help image. The only static binary in
      // scope is the favicon, so the glyph is an inline vector instead. Nothing is lost:
      // the legacy image carried no alternative text, so it named nothing to begin with.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      clickToggle(fixture);

      expect(isAbsent(rootOf(fixture), 'img'))
        .withContext('no image element may be created')
        .toBeTrue();
    });

    it('hides its glyph from assistive technology so it cannot name the button', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT }));
      const glyph = queryOrFail<SVGElement>(root, 'svg.form-field__help-icon');

      expect(glyph.getAttribute('aria-hidden')).toBe('true');
      expect(glyph.getAttribute('focusable'))
        .withContext('an unfocusable glyph cannot become a stray tab stop')
        .toBe('false');
    });
  });


  // =========================================================================
  // 4.7  THE HELP REGION IS A BLOCK BELOW, IN NORMAL FLOW
  // =========================================================================
  // Two independent proofs in the legacy sources. `labelcontrol.ascx:L8` places a literal
  // break element between the closing label tag on L7 and the panel on L9, so the panel
  // was always below the label in normal flow; and the legacy stylesheet gives the
  // corresponding class a one-pixel border on all four sides over a filled background -
  // a BOX. So this is a disclosure and never a tool tip, a pop-up or anything positioned
  // out of flow.

  describe('the help region', () => {
    /** Renders the field with help revealed and returns its element. */
    function revealedField(): HTMLElement {
      const fixture = createField({
        label: LABEL_WITHOUT_SUFFIX,
        for: CONTROL_ID,
        help: HELP_TEXT,
      });

      clickToggle(fixture);

      return rootOf(fixture);
    }

    it('sits OUTSIDE the label rather than inside it', () => {
      const root = revealedField();
      const label = labelOf(root);

      expect(label.querySelector('.form-field__help')).toBeNull();
      expect(label.contains(helpRegionOf(root))).toBeFalse();
    });

    it('sits AFTER the label in document order', () => {
      // The precise relationship is uncle-level: the label lives in the label row and the
      // region is that row's own next sibling, which is the faithful translation of the
      // legacy file where the panel on L9 follows the closing label tag on L7. What
      // matters is the order a reader meets them in.
      const root = revealedField();
      const position = labelOf(root).compareDocumentPosition(helpRegionOf(root));

      expect(position & Node.DOCUMENT_POSITION_FOLLOWING)
        .withContext('help must be met after the label it explains')
        .toBeGreaterThan(0);
      expect(position & Node.DOCUMENT_POSITION_CONTAINED_BY)
        .withContext('and must not be contained by it')
        .toBe(0);
    });

    it('is a block in normal flow rather than a tool tip', () => {
      const region = helpRegionOf(revealedField());

      expect(region.getAttribute('role'))
        .withContext('a tool tip role would contradict the legacy bordered box')
        .not.toBe('tooltip');
    });

    it('does not smuggle the help text into a title attribute', () => {
      // A title attribute is a tool tip by another name: it appears on hover only, is
      // unreachable by keyboard and is announced inconsistently.
      const root = revealedField();

      expect(queryAll(root, '[title]').length)
        .withContext('no element may stand a title attribute in for the help region')
        .toBe(0);
    });

    it('carries the identifier the component publishes', () => {
      const fixture = createField({
        label: LABEL_WITHOUT_SUFFIX,
        for: CONTROL_ID,
        help: HELP_TEXT,
      });

      clickToggle(fixture);

      expect(attributeOrFail(helpRegionOf(rootOf(fixture)), 'id')).toBe(
        fixture.componentInstance.helpId(),
      );
    });

    it('is not rendered at all before the affordance is activated', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT }));

      expect(isAbsent(root, '.form-field__help'))
        .withContext('collapsed help is not content a reader should be able to reach')
        .toBeTrue();
    });
  });

  // =========================================================================
  // 4.8  SEVERAL PROJECTED CONTROLS UNDER ONE LABEL
  // =========================================================================
  // Measured in two files. `editroles.ascx:L98-L115` puts a text box, a literal pair of
  // non-breaking spaces, a drop-down list and TWO compare validators under the single
  // `plBillingPeriod` label, and `L130-L147` repeats the shape for the trial period; the
  // resource file states the intent outright - `BillingPeriod.Help` reads 'These two
  // fields are used in conjunction to enter a Billing Period'. `roles.ascx:L5-L16` puts a
  // drop-down list, an edit link and a delete image button under one label.

  describe('projected content', () => {
    it('renders a single projected control, the common case', () => {
      const host = createHost();
      const slot = slotOf(hostRootOf(host));

      expect(slot.querySelector('input')).not.toBeNull();
    });

    it('renders BOTH controls of a composite field', () => {
      const fixture = createHost();

      fixture.componentInstance.showFrequency = true;
      fixture.detectChanges();

      const slot = slotOf(hostRootOf(fixture));

      expect(slot.querySelector('input')).not.toBeNull();
      expect(slot.querySelector('select'))
        .withContext('nothing here counts, wraps or reorders the projected children')
        .not.toBeNull();
    });

    it('declares the slot a named group, so the composite keeps its context', () => {
      const fixture = createHost();
      const root = hostRootOf(fixture);
      const slot = slotOf(root);

      expect(attributeOrFail(slot, 'role')).toBe('group');

      const reference = attributeOrFail(slot, 'aria-labelledby');
      const target = resolveIdReference(root, reference);

      expect(target)
        .withContext('the group name must resolve to a real element')
        .not.toBeNull();
      expect(target === null ? '' : collapsedText(target))
        .withContext('and that element must be the visible label')
        .toBe(LABEL_WITH_PARENTHESES);
      expect(target === null ? '' : target.tagName.toLowerCase()).toBe('label');
    });

    it('is not named when there is no label wording to name it with', () => {
      const fixture = createHost();

      fixture.componentInstance.label = '';
      fixture.detectChanges();

      expect(slotOf(hostRootOf(fixture)).hasAttribute('aria-labelledby'))
        .withContext('an empty element is not a name, so nothing points at it')
        .toBeFalse();
    });

    it('gives the SECOND control its own name reference, which a group cannot supply', () => {
      // MIGRATION: the regression test for the third accessibility divergence. The legacy
      // `ControlName` attribute names exactly one control, and a label `for` can only
      // point at one element, so the second control of every composite field above was
      // UNLABELLED. A named group supplies useful context but does not name its
      // descendants, so the component applies the visible label reference directly.
      const fixture = createHost();

      fixture.componentInstance.showFrequency = true;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const select = queryOrFail<HTMLSelectElement>(root, 'select');
      const reference = attributeOrFail(select, 'aria-labelledby');
      const target = resolveIdReference(root, reference);

      expect(target).not.toBeNull();
      expect(target === null ? '' : collapsedText(target)).toBe(LABEL_WITH_PARENTHESES);
    });

    it('leaves a consumer-supplied accessible name in place', () => {
      // A composite field may legitimately want a more specific name on its second
      // control, such as the frequency of a billing period. An explicit decision wins.
      const fixture = createHost();

      fixture.componentInstance.showFrequency = true;
      fixture.componentInstance.frequencyName = 'Frequency';
      fixture.detectChanges();

      const select = queryOrFail<HTMLSelectElement>(hostRootOf(fixture), 'select');

      expect(select.hasAttribute('aria-labelledby'))
        .withContext('a caller-supplied name must not be overwritten by the fallback')
        .toBeFalse();
      expect(select.getAttribute('aria-label')).toBe('Frequency');
    });

    it('follows the field label when the association changes', () => {
      const fixture = createHost();

      fixture.componentInstance.showFrequency = true;
      fixture.detectChanges();

      fixture.componentInstance.label = LABEL_ALREADY_UNPUNCTUATED;
      fixture.componentInstance.controlId = OTHER_CONTROL_ID;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const select = queryOrFail<HTMLSelectElement>(root, 'select');
      const reference = attributeOrFail(select, 'aria-labelledby');
      const target = resolveIdReference(root, reference);

      expect(target === null ? '' : collapsedText(target)).toBe(LABEL_ALREADY_UNPUNCTUATED);
    });

    it('describes nothing while neither help nor a message is rendered', () => {
      const slot = slotOf(hostRootOf(createHost()));

      expect(slot.hasAttribute('aria-describedby'))
        .withContext('a description that points at nothing is worse than none')
        .toBeFalse();
    });

    it('describes the group with the MESSAGE region once a message exists', () => {
      const fixture = createHost();

      fixture.componentInstance.error = ERROR_WITH_LEADING_BREAK;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const reference = attributeOrFail(slotOf(root), 'aria-describedby');

      expect(everyReferenceResolves(root, reference)).toBeTrue();
      expect(resolvedReferenceText(root, reference)).toBe(ERROR_WITHOUT_LEADING_BREAK);
    });

    it('describes the group with HELP FIRST and the message second, in document order', () => {
      const fixture = createHost();

      fixture.componentInstance.help = HELP_FOR_COMPOSITE_FIELD;
      fixture.componentInstance.error = ERROR_WITH_LEADING_BREAK;
      fixture.detectChanges();

      const root = hostRootOf(fixture);

      toggleOf(root).click();
      fixture.detectChanges();

      const reference = attributeOrFail(slotOf(root), 'aria-describedby');
      const references = referenceList(reference);

      expect(references.length).toBe(2);
      expect(everyReferenceResolves(root, reference))
        .withContext('EVERY identifier must resolve - no dangling reference, ever')
        .toBeTrue();
      expect(resolvedReferenceText(root, reference))
        .withContext('read in the sequence it is seen')
        .toBe(`${HELP_FOR_COMPOSITE_FIELD} ${ERROR_WITHOUT_LEADING_BREAK}`);
    });

    it('stops referencing the help region when it is collapsed again', () => {
      const fixture = createHost();

      fixture.componentInstance.help = HELP_TEXT;
      fixture.detectChanges();

      const root = hostRootOf(fixture);

      toggleOf(root).click();
      fixture.detectChanges();

      expect(slotOf(root).hasAttribute('aria-describedby')).toBeTrue();

      toggleOf(root).click();
      fixture.detectChanges();

      expect(slotOf(root).hasAttribute('aria-describedby'))
        .withContext('the reference exists exactly when its target does')
        .toBeFalse();
    });
  });


  // =========================================================================
  // 4.8 (CONTINUED)  CONTROLS THAT ALREADY NAME THEMSELVES
  // =========================================================================
  // The field-label fallback must be a LAST resort, and getting that wrong is worse than
  // omitting it: overwriting a control's own name replaces something specific with
  // something generic. The shapes below are the ones `roles.ascx:L5-L16` actually
  // projects under a single label - a drop-down list, an edit hyperlink wrapping an image,
  // and a delete image button - plus the check box that carries its own wording, which 5
  // of the 28 in-scope check-box declarations do.

  describe('projected controls that name themselves', () => {
    /** The `aria-labelledby` value on a projected probe, or null when there is none. */
    function nameReference(root: ParentNode, selector: string): string | null {
      return queryOrFail<HTMLElement>(root, selector).getAttribute('aria-labelledby');
    }

    it('leaves a HYPERLINK named by its own visible text alone', () => {
      // `roles.ascx:L10-L12` wraps an edit image in a hyperlink. An anchor takes its name
      // from its contents, so it is already named and must not be relabelled.
      const fixture = createRichHost();

      fixture.componentInstance.showEditLink = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'a.probe-link'))
        .withContext('an anchor with text names itself')
        .toBeNull();
    });

    it('names a hyperlink that has NO text of its own', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showEditLink = true;
      fixture.componentInstance.linkText = '';
      fixture.detectChanges();

      const root = richRootOf(fixture);
      const reference = nameReference(root, 'a.probe-link');

      expect(reference).not.toBeNull();
      expect(reference === null ? false : everyReferenceResolves(root, reference)).toBeTrue();
    });

    it('leaves an IMAGE BUTTON named by its alternative text alone', () => {
      // `roles.ascx:L13` is an image button. An image control takes its name from its
      // alternative text, which the caller supplies.
      const fixture = createRichHost();

      fixture.componentInstance.showImageButton = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-image')).toBeNull();
    });

    it('names an image button that carries NO alternative text', () => {
      // The legacy help image at `labelcontrol.ascx:L4` carried none either, which is why
      // this case matters rather than being hypothetical.
      const fixture = createRichHost();

      fixture.componentInstance.showImageButton = true;
      fixture.componentInstance.imageAlt = null;
      fixture.detectChanges();

      const root = richRootOf(fixture);
      const reference = nameReference(root, 'input.probe-image');

      expect(reference).not.toBeNull();
      expect(reference === null ? false : everyReferenceResolves(root, reference)).toBeTrue();
    });

    it('leaves a SUBMIT button named by its value alone', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showSubmit = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-submit')).toBeNull();
    });

    it('names a submit button that carries no value', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showSubmit = true;
      fixture.componentInstance.submitValue = null;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-submit')).not.toBeNull();
    });

    it('leaves a control named by its OWN native label alone', () => {
      // 23 of the 28 in-scope check boxes depend entirely on the field label, but 5 carry
      // their own wording. Native association outranks the field-level fallback.
      const fixture = createRichHost();

      fixture.componentInstance.showLabelledCheckbox = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-checkbox'))
        .withContext('a native label already names it')
        .toBeNull();
    });

    it('names a control whose own label is empty', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showLabelledCheckbox = true;
      fixture.componentInstance.checkboxText = '';
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-checkbox')).not.toBeNull();
    });

    it('names a MULTI-LINE text control, which can never name itself', () => {
      // MIGRATION: rich-text editing is a documented functional reduction - the legacy
      // editor provider is out of scope - so each of the 19 in-scope multi-line
      // declarations becomes a plain multi-line control projected by the caller.
      const fixture = createRichHost();

      fixture.componentInstance.showTextarea = true;
      fixture.detectChanges();

      const root = richRootOf(fixture);
      const reference = nameReference(root, 'textarea.probe-textarea');

      expect(reference).not.toBeNull();
      expect(reference === null ? false : everyReferenceResolves(root, reference)).toBeTrue();
    });

    it('names an EDITABLE REGION, which can never name itself either', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showEditable = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'div.probe-editable')).not.toBeNull();
    });

    it('leaves a projected BUTTON named by its own wording alone', () => {
      // `editroles.ascx:L179-L189` puts four command buttons on the screen, and a button
      // takes its name from its contents, so it arrives already named.
      const fixture = createRichHost();

      fixture.componentInstance.showCommandButton = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'button.probe-button')).toBeNull();
    });

    it('names a projected button that carries no wording', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showCommandButton = true;
      fixture.componentInstance.commandText = '';
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'button.probe-button')).not.toBeNull();
    });

    it('leaves a control named by its ROLE and its wording alone', () => {
      // The component recognises the roles that take a name from their contents, so a
      // custom control built to one of them is treated exactly like the native element it
      // stands in for.
      const fixture = createRichHost();

      fixture.componentInstance.showSwitch = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'div.probe-switch')).toBeNull();
    });

    it('names a role-based control that carries no wording', () => {
      const fixture = createRichHost();

      fixture.componentInstance.showSwitch = true;
      fixture.componentInstance.switchText = '';
      fixture.detectChanges();

      const root = richRootOf(fixture);
      const reference = nameReference(root, 'div.probe-switch');

      expect(reference).not.toBeNull();
      expect(reference === null ? false : everyReferenceResolves(root, reference)).toBeTrue();
    });

    it('yields ownership when something else rewrites the reference after projection', () => {
      // Simulates a directive or a caller writing the attribute itself once the control is
      // in the document. From that point the explicit decision owns the control, so the
      // component must stop reconciling it - otherwise the more specific name would be
      // overwritten on the next content check.
      const fixture = createRichHost();
      const root = richRootOf(fixture);
      const select = queryOrFail<HTMLSelectElement>(root, 'select');

      expect(attributeOrFail(select, 'aria-labelledby')).toBe(
        `${fixture.componentInstance.controlId}-label`,
      );

      select.setAttribute('aria-labelledby', 'probe-custom-name');

      // Any input change triggers the next content check, which is where the component
      // would otherwise reassert its own reference.
      fixture.componentInstance.label = LABEL_WITH_PARENTHESES;
      fixture.detectChanges();

      expect(attributeOrFail(select, 'aria-labelledby'))
        .withContext('an explicit decision must survive the next content check')
        .toBe('probe-custom-name');
      expect(everyReferenceResolves(root, 'probe-custom-name')).toBeTrue();
    });

    it('removes its own reference when the field loses its label wording', () => {
      // A fallback that outlived the element it points at would be the dangling reference
      // the whole design works to avoid.
      const fixture = createRichHost();
      const root = richRootOf(fixture);
      const select = queryOrFail<HTMLSelectElement>(root, 'select');

      expect(select.hasAttribute('aria-labelledby')).toBeTrue();

      fixture.componentInstance.label = '';
      fixture.detectChanges();

      expect(select.hasAttribute('aria-labelledby'))
        .withContext('nothing may point at an empty label')
        .toBeFalse();
    });
  });

  // =========================================================================
  // 4.9  THE REQUIRED MARKER - A NET ADDITION, STATED AS ONE
  // =========================================================================
  // MIGRATION: there is nothing to port here. The legacy screens expressed requiredness
  // ONLY through `asp:RequiredFieldValidator` - 16 declarations across the in-scope
  // screens, one of them `valRoleName` at `editroles.ascx:L29-L31` - which rendered
  // nothing at all until a postback failed. No visual required-marker convention was
  // found anywhere in the legacy label markup, so this marker is new. Being new, it must
  // meet the current standard rather than the legacy one: the meaning cannot rest on a
  // glyph and a colour alone.

  describe('the required marker', () => {
    it('is absent by default', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX }));

      expect(isAbsent(root, '.form-field__required')).toBeTrue();
    });

    it('is absent when requiredness is explicitly false', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, required: false }));

      expect(isAbsent(root, '.form-field__required')).toBeTrue();
    });

    it('is rendered when the field is required', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, required: true }));

      expect(isAbsent(root, '.form-field__required')).toBeFalse();
    });

    it('carries accessible WORDING, so colour alone never carries the meaning', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, required: true }));
      const wording = queryOrFail<HTMLElement>(root, '.form-field__required-text');

      expect(collapsedText(wording).length)
        .withContext('a marker glyph in the error colour is inaudible to everyone else')
        .toBeGreaterThan(0);
      expect(collapsedText(wording)).toBe('required');
    });

    it('hides its glyph from assistive technology so it is not read as punctuation', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, required: true }));
      const marker = queryOrFail<HTMLElement>(root, '.form-field__required');
      const glyph = queryOrFail<HTMLElement>(marker, '[aria-hidden="true"]');

      expect(collapsedText(glyph).length)
        .withContext('the glyph is visible but silent; the wording beside it speaks')
        .toBeGreaterThan(0);
    });

    it('stays inside the label, so the wording joins the accessible name of the field', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, required: true }));
      const marker = queryOrFail<HTMLElement>(root, '.form-field__required');

      expect(labelOf(root).contains(marker))
        .withContext('so the wording travels with the group and the control references')
        .toBeTrue();
    });

    it('does not disturb the label wording itself', () => {
      const root = rootOf(createField({ label: LABEL_WITH_DECLARED_SUFFIX, required: true }));

      expect(collapsedText(labelOf(root)).startsWith(LABEL_WITHOUT_SUFFIX)).toBeTrue();
    });
  });

  // =========================================================================
  // 4.10  STRUCTURAL PROHIBITIONS, ASSERTED RATHER THAN ASSUMED
  // =========================================================================

  describe('the rendered structure', () => {
    /**
     * Renders the field with EVERY optional part present.
     *
     * Label, required marker, two projected controls, revealed help and two messages -
     * the largest surface the component can produce, so a prohibited element cannot hide
     * behind an unrendered branch.
     */
    function fullyRenderedHost(): ComponentFixture<FormFieldHostComponent> {
      const fixture = createHost();
      const instance = fixture.componentInstance;

      instance.label = LABEL_WITH_PARENTHESES;
      instance.required = true;
      instance.help = HELP_FOR_COMPOSITE_FIELD;
      instance.error = [ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR];
      instance.showFrequency = true;
      fixture.detectChanges();

      toggleOf(hostRootOf(fixture)).click();
      fixture.detectChanges();

      return fixture;
    }

    it('renders every optional part at once, so the prohibitions below are meaningful', () => {
      const root = hostRootOf(fullyRenderedHost());

      expect(isAbsent(root, '.form-field__required')).toBeFalse();
      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeFalse();
      expect(isAbsent(root, '.form-field__help')).toBeFalse();
      expect(errorTexts(root).length).toBe(2);
      expect(queryAll(root, 'select').length).toBe(1);
    });

    it('declares NO landmark element', () => {
      // Landmarks belong exclusively to the application shell. A field that declared one
      // would duplicate a landmark on every screen it appears on, which turns a
      // navigation aid into noise.
      const root = hostRootOf(fullyRenderedHost());

      expect(isAbsent(root, 'header')).toBeTrue();
      expect(isAbsent(root, 'main')).toBeTrue();
      expect(isAbsent(root, 'nav')).toBeTrue();
      expect(isAbsent(root, 'footer')).toBeTrue();
    });

    it('emits NO break element', () => {
      // MIGRATION: `labelcontrol.ascx:L8` used one as vertical spacing. Spacing here is the
      // stylesheet spacing scale, which is adjustable and cannot be collapsed away.
      const root = hostRootOf(fullyRenderedHost());

      expect(queryAll(root, 'br').length)
        .withContext('the legacy layout break is replaced by stylesheet spacing')
        .toBe(0);
    });

    it('emits NO non-breaking space', () => {
      // MIGRATION: 70 literal non-breaking-space entities appear across the in-scope admin
      // markup - a pair of them between the text box and the drop-down list at
      // `editroles.ascx:L105` and another pair at L137. All are replaced by the
      // stylesheet gap, which is adjustable and cannot be collapsed away.
      const root = hostRootOf(fullyRenderedHost());

      expect(rawText(root).includes('\u00a0'))
        .withContext('inter-control spacing is not content')
        .toBeFalse();
    });

    it('declares NO field set or legend', () => {
      // Those belong to a group of fields and are owned globally by the form stylesheet.
      // This component wraps exactly one field.
      const root = hostRootOf(fullyRenderedHost());

      expect(isAbsent(root, 'fieldset')).toBeTrue();
      expect(isAbsent(root, 'legend')).toBeTrue();
    });

    it('carries NO inline style attribute on any element', () => {
      // Every presentational value lives in the paired stylesheet and resolves to a design
      // token. An inline style is a value that no token governs.
      const root = hostRootOf(fullyRenderedHost());

      expect(queryAll(root, '[style]').length).toBe(0);
    });

    it('leaves no identifier reference in the whole field pointing at nothing', () => {
      // One sweep over every naming and describing reference the field emits, so a future
      // change cannot introduce a dangling one anywhere.
      const root = hostRootOf(fullyRenderedHost());
      const attributes: readonly string[] = ['aria-labelledby', 'aria-describedby', 'aria-controls'];

      for (const attribute of attributes) {
        for (const element of queryAll<HTMLElement>(root, `[${attribute}]`)) {
          const value = attributeOrFail(element, attribute);

          expect(everyReferenceResolves(root, value))
            .withContext(`${attribute} on <${element.tagName.toLowerCase()}> must resolve`)
            .toBeTrue();
        }
      }
    });
  });

  // =========================================================================
  // 4.11  THE COMPONENT CONTRACT
  // =========================================================================

  describe('the component contract', () => {
    it('is standalone, which is the only reason it can be supplied through imports', () => {
      // Both the testing module and the host above list it in `imports`. A component that
      // was not standalone could not be supplied that way at all, so a successful render
      // through either route is the proof - no metadata is inspected to obtain it.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX });

      expect(collapsedText(labelOf(rootOf(fixture)))).toBe(LABEL_WITHOUT_SUFFIX);
      expect(collapsedText(labelOf(hostRootOf(createHost())))).toBe(LABEL_WITH_PARENTHESES);
    });

    it('renders every input change pushed in through the component reference', () => {
      // The correct way to drive a component that declares the on-push strategy: an input
      // write marks the view for check. All five inputs in one pass.
      const fixture = createField();

      applyInputs(fixture, {
        label: LABEL_WITH_DECLARED_SUFFIX,
        for: CONTROL_ID,
        required: true,
        help: HELP_TEXT,
        error: ERROR_WITH_LEADING_BREAK,
      });

      const root = rootOf(fixture);

      expect(collapsedText(labelOf(root)).startsWith(LABEL_WITHOUT_SUFFIX)).toBeTrue();
      expect(attributeOrFail(labelOf(root), 'for')).toBe(CONTROL_ID);
      expect(isAbsent(root, '.form-field__required')).toBeFalse();
      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeFalse();
      expect(errorTexts(root)).toEqual([ERROR_WITHOUT_LEADING_BREAK]);
    });

    it('is refreshed through its INPUTS and not by a write to an instance field', () => {
      // This is what declaring the on-push strategy means, asserted behaviourally rather
      // than by reading compiler metadata. A write straight to the instance marks nothing
      // for check, so the view is skipped on the next pass; the same value arriving
      // through the binding marks it and the marker appears.
      const fixture = createHost();
      const field = fixture.componentInstance.field;

      if (field === undefined) {
        throw new Error('Expected the host to hold a reference to the field');
      }

      field.required = true;
      fixture.detectChanges();

      expect(isAbsent(hostRootOf(fixture), '.form-field__required'))
        .withContext('an instance write must not reach the screen under the on-push strategy')
        .toBeTrue();

      fixture.componentInstance.required = true;
      fixture.detectChanges();

      expect(isAbsent(hostRootOf(fixture), '.form-field__required'))
        .withContext('the same value through the binding does reach it')
        .toBeFalse();
    });

    it('reads back the text it was given, before punctuation is resolved for display', () => {
      const fixture = createField({
        label: LABEL_WITH_DECLARED_SUFFIX,
        for: `  ${CONTROL_ID}  `,
        help: HELP_TEXT,
      });
      const component = fixture.componentInstance;

      expect(component.label).toBe(LABEL_WITH_DECLARED_SUFFIX);
      expect(component.help).toBe(HELP_TEXT);
      expect(component.for)
        .withContext('trimmed on the way in, because whitespace cannot match an element id')
        .toBe(CONTROL_ID);
    });

    it('publishes the three region identifiers a caller may describe a control with', () => {
      // A caller may prefer to describe ONE projected control rather than the whole group.
      // The identifiers are derived from the control identifier, so they are predictable
      // from a value the caller already holds. The generated NUMBER of a field with no
      // control association is deliberately not asserted - only that the references
      // resolve, which is the property that matters.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });
      const component = fixture.componentInstance;

      expect(component.labelId()).toBe(`${CONTROL_ID}-label`);
      expect(component.helpId()).toBe(`${CONTROL_ID}-help`);
      expect(component.errorId()).toBe(`${CONTROL_ID}-error`);
    });

    it('gives a field with NO control association unique, resolvable identifiers', () => {
      const first = createField({ label: LABEL_WITHOUT_SUFFIX, for: '', error: ERROR_FIRST_OF_PAIR });
      const second = createField({
        label: LABEL_ALREADY_UNPUNCTUATED,
        for: '',
        error: ERROR_SECOND_OF_PAIR,
      });

      expect(first.componentInstance.labelId())
        .withContext('two labels may legitimately carry the same wording')
        .not.toBe(second.componentInstance.labelId());

      const firstRoot = rootOf(first);
      const reference = attributeOrFail(slotOf(firstRoot), 'aria-describedby');

      expect(everyReferenceResolves(firstRoot, reference)).toBeTrue();
    });

    it('keeps a rendered identifier stable across an unrelated input change', () => {
      // An identifier must never change underneath a reference already rendered into a
      // description.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: '' });
      const before = fixture.componentInstance.labelId();

      applyInputs(fixture, { label: LABEL_ALREADY_UNPUNCTUATED, required: true });

      expect(fixture.componentInstance.labelId()).toBe(before);
    });

    it('treats a value that arrives UNTYPED as absent rather than failing to render', () => {
      // Every input is typed, so this cannot happen from a type-checked template. It can
      // happen through the two paths that bypass the type system: a value that arrived as
      // JSON and was typed optimistically, and a caller compiled against a different
      // declaration. Rendering must continue, because a failure inside a template
      // expression takes out the whole view rather than one field. `setInput` accepts an
      // unknown value by declaration, so the number below needs no cast to pass in.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      fixture.componentRef.setInput('label', 42);
      fixture.componentRef.setInput('help', 42);
      fixture.detectChanges();

      const root = rootOf(fixture);

      expect(collapsedText(labelOf(root)))
        .withContext('an unreadable value renders as nothing, never as its own text')
        .toBe('');
      expect(isAbsent(root, 'button.form-field__help-toggle'))
        .withContext('and unreadable help counts as no help at all')
        .toBeTrue();
    });

    it('renders through the real template, which is how this folder gets type-checked', () => {
      // Stated as an assertion because it is easy to lose: nothing outside this folder
      // imports the component, so the production build does not type-check it or its
      // template. This suite compiles both under strict template checking, and it does so
      // only because it renders them.
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID }));

      expect(isAbsent(root, '.form-field')).toBeFalse();
      expect(isAbsent(root, '.form-field__label-row')).toBeFalse();
      expect(isAbsent(root, '.form-field__control')).toBeFalse();
    });

    it('performs no network access, which the verification after every case confirms', () => {
      // Made explicit here as well as implicitly in `afterEach`: a presentational
      // component holds no data access, and every request path belongs behind a typed
      // client service. `verify()` runs after this case with nothing outstanding.
      const fixture = createField({
        label: LABEL_WITHOUT_SUFFIX,
        for: CONTROL_ID,
        help: HELP_TEXT,
        error: ERROR_WITH_LEADING_BREAK,
      });

      clickToggle(fixture);

      expect(httpMock.match(() => true).length)
        .withContext('the shared presentational layer issues no request of any kind')
        .toBe(0);
    });
  });
});

