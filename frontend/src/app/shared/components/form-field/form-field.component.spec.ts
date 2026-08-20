import { Component, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { FormFieldComponent } from './form-field.component';

const LABEL_WITH_DECLARED_SUFFIX = 'Role Name:';

const LABEL_WITHOUT_SUFFIX = 'Role Name';

/**
 * `plRSVPCode.Text` verbatim. The colon is baked into the RESOURCE VALUE rather than declared as a suffix
 * - `editroles.ascx` declares `plRSVPCode` with no `Suffix` attribute at all - so this is the second of
 * the six legacy punctuation routes and must normalise identically to the first.
 */
const LABEL_WITH_BAKED_COLON = 'RSVP Code:';

const LABEL_WITH_BAKED_COLON_RESOLVED = 'RSVP Code';

/** `PublicRole.Text` verbatim. */
const LABEL_WITH_QUESTION_MARK = 'Public Role?';

const LABEL_WITH_QUESTION_MARK_SECOND = 'Auto Assignment?';

/** `BillingPeriod.Text` verbatim. */
const LABEL_WITH_PARENTHESES = 'Billing Period (Every)';

const LABEL_WITH_PARENTHESES_SECOND = 'Trial Period (Every)';

const LABEL_ALREADY_UNPUNCTUATED = 'Role Group';

const LABEL_WITH_INLINE_MARKUP_COLON = 'Icon:';

const HELP_TEXT = 'Enter the name of the role.';

const HELP_FOR_COMPOSITE_FIELD =
  'These two fields are used in conjunction to enter a Billing Period. e.g 2 weeks, or 1 month';

/** `valRoleName.Text` verbatim, break markup and all. */
const ERROR_WITH_LEADING_BREAK = '<br>You Must Enter a Valid Name';

const ERROR_WITHOUT_LEADING_BREAK = 'You Must Enter a Valid Name';

const ERROR_FIRST_OF_PAIR = 'Service Fee Value Entered Is Not Valid';

const ERROR_SECOND_OF_PAIR = 'Service Fee Must Be Greater Than or Equal to Zero';

const ERROR_WITH_BOLD_MARKUP = '<b>Warning:</b> You will need to configure the Payment Processor';

/** The shape of `ModuleHelp.Text`, which opens with a heading and a paragraph. */
const HELP_WITH_BLOCK_MARKUP = '<h1>About</h1><p>Body</p>';

/**
 * Help text carrying a script element, modelled on the single most dangerous entry found across the 37
 * in-scope resource files: the `Advertising.Text` entry of
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` holds a live remote advertising script
 * block.
 */
const HELP_WITH_SCRIPT = '<script src="pagead/show_ads.js"></script>Advertising';

const CONTROL_ID = 'txt-role-name';

const OTHER_CONTROL_ID = 'txt-billing-period';

// Each helper narrows without a non-null assertion and without a cast, and each fails with a message that
// names what was missing rather than throwing on a null property access.

function queryOrFail<T extends Element>(root: ParentNode, selector: string): T {
  const found = root.querySelector<T>(selector);

  if (found === null) {
    throw new Error(`Expected to find "${selector}"`);
  }

  return found;
}

/**
 * The identifiers one ARIA reference attribute names, split the way ARIA defines. Split rather than
 * compared as a string so that an expectation states WHICH regions are referenced and in what order,
 * without also asserting the exact spacing a consumer happened to write.
 */
function referencesOf(element: Element, attribute: string): readonly string[] {
  const value = element.getAttribute(attribute);

  if (value === null) {
    return [];
  }

  return value.split(/\s+/).filter((token) => token.length > 0);
}

function queryAll<T extends Element>(root: ParentNode, selector: string): readonly T[] {
  return Array.from(root.querySelectorAll<T>(selector));
}

function isAbsent(root: ParentNode, selector: string): boolean {
  return root.querySelector(selector) === null;
}

function collapsedText(element: Element): string {
  const content = element.textContent;

  return content === null ? '' : content.replace(/\s+/g, ' ').trim();
}

/**
 * An element's text exactly as the DOM holds it, for the cases where a newline is the thing under test
 * and collapsing would erase it.
 */
function rawText(element: Element): string {
  const content = element.textContent;

  return content === null ? '' : content;
}

function attributeOrFail(element: Element, name: string): string {
  const value = element.getAttribute(name);

  if (value === null) {
    throw new Error(`Expected "${name}" to be present on <${element.tagName.toLowerCase()}>`);
  }

  return value;
}

/**
 * Resolves an identifier reference within the fixture, through an attribute selector rather than an
 * identifier selector, so a value that would need escaping in a selector cannot turn a missing element
 * into a thrown syntax error and hide the real result.
 */
function resolveIdReference(root: ParentNode, id: string): Element | null {
  return root.querySelector(`[id="${id}"]`);
}

/**
 * Splits a space-separated reference list into its individual identifiers, tolerating the double spaces
 * and leading whitespace a template can produce: an assertion about references should fail on a DANGLING
 * reference and never on incidental spacing.
 */
function referenceList(value: string): readonly string[] {
  return value.split(/\s+/).filter((entry) => entry.length > 0);
}

function everyReferenceResolves(root: ParentNode, value: string): boolean {
  const references = referenceList(value);

  return (
    references.length > 0 &&
    references.every((reference) => resolveIdReference(root, reference) !== null)
  );
}

function resolvedReferenceText(root: ParentNode, value: string): string {
  return referenceList(value)
    .map((reference) => {
      const target = resolveIdReference(root, reference);

      return target === null ? '' : collapsedText(target);
    })
    .join(' ')
    .trim();
}

/** The component's inputs, as an optional bundle. */
interface FieldInputs {
  readonly label?: string;
  readonly for?: string;
  readonly required?: boolean;
  readonly help?: string;
  readonly error?: string | readonly string[] | null;
  readonly limit?: number | null;
}

/** Pushes inputs in through the component reference, then renders. */
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

  if (inputs.limit !== undefined) {
    fixture.componentRef.setInput('limit', inputs.limit);
  }

  fixture.detectChanges();
}

/** A host that projects real controls into the field. */
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
      [limit]="limit"
    >
      <input [attr.id]="controlId.length > 0 ? controlId : null" type="text" [disabled]="controlDisabled" />
      @if (showFrequency) {
        <select [attr.aria-label]="frequencyName">
          <option value="M">Month</option>
        </select>
      }
    </app-form-field>
  `,
})
class FormFieldHostComponent {
  public label = LABEL_WITH_PARENTHESES;

  public controlId = CONTROL_ID;

  public required = false;

  public help = '';

  public error: string | readonly string[] | null = null;

  public controlDisabled = false;

  public limit: number | null = null;

  public showFrequency = false;

  public frequencyName: string | null = null;

  /**
   * The field instance, reached without a cast. Used by exactly one specification, which needs to write
   * to an instance field in order to prove that writing to an instance field is NOT how a change reaches
   * the screen.
   */
  @ViewChild(FormFieldComponent) public field: FormFieldComponent | undefined = undefined;
}

/**
 * A second host that projects the OTHER control shapes the legacy screens used. `roles.ascx` is not a
 * text-box-and-drop-down field: it puts a drop-down list, an EDIT HYPERLINK WRAPPING AN IMAGE and an
 * IMAGE BUTTON under one label.
 */
@Component({
  selector: 'app-form-field-rich-host',
  standalone: true,
  imports: [FormFieldComponent],
  template: `
    <app-form-field [label]="label" [for]="controlId" [error]="error">
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
  public label = LABEL_ALREADY_UNPUNCTUATED;

  /** Drives the failure state, so the per-control wiring can be exercised on these control shapes. */
  public error: string | readonly string[] | null = null;

  public controlId = 'cbo-role-groups';

  public showEditLink = false;

  public linkText = 'Edit';

  public showImageButton = false;

  public imageAlt: string | null = 'Delete';

  public showSubmit = false;

  public submitValue: string | null = 'Update';

  public showLabelledCheckbox = false;

  public checkboxText = 'Public Role';

  public showTextarea = false;

  public showEditable = false;

  public showCommandButton = false;

  public commandText = 'Manage Users';

  public showSwitch = false;

  public switchText = 'Auto Assignment';
}

describe('FormFieldComponent', () => {
  /**
   * The mock HTTP backend, asserted empty after every specification. Injected even though nothing here
   * issues a request, because that is the point: an unexpected request is the commonest way a false green
   * survives, and this component is required to make none.
   */
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // All three are supplied through `imports`, which is only possible because all three are standalone;
      // supplying a non-standalone component this way would fail at configuration.
      imports: [FormFieldComponent, FormFieldHostComponent, FormFieldRichHostComponent],
      // The real client MUST be provided first; the testing function then replaces the backend it installed.
      // Reversed, the real backend survives and `verify()` never sees anything.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Doubles as a positive assertion: this presentational component performs zero network access, which is
    // what the code-organisation rule requires of the shared layer.
    httpMock.verify();
  });

  function createField(inputs: FieldInputs = {}): ComponentFixture<FormFieldComponent> {
    const fixture = TestBed.createComponent(FormFieldComponent);

    applyInputs(fixture, inputs);

    return fixture;
  }

  function createHost(): ComponentFixture<FormFieldHostComponent> {
    const fixture = TestBed.createComponent(FormFieldHostComponent);

    fixture.detectChanges();

    return fixture;
  }

  function rootOf(fixture: ComponentFixture<FormFieldComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function hostRootOf(fixture: ComponentFixture<FormFieldHostComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function createRichHost(): ComponentFixture<FormFieldRichHostComponent> {
    const fixture = TestBed.createComponent(FormFieldRichHostComponent);

    fixture.detectChanges();

    return fixture;
  }

  function richRootOf(fixture: ComponentFixture<FormFieldRichHostComponent>): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** The field's caption, whichever element the component chose for it. */
  function captionOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__label');
  }

  function labelElementOf(root: ParentNode): HTMLLabelElement {
    return queryOrFail<HTMLLabelElement>(root, 'label.form-field__label');
  }

  /** Alias kept so the many tests that only read the caption's text or attributes read naturally. */
  const labelOf = captionOf;

  function slotOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__control');
  }

  function toggleOf(root: ParentNode): HTMLButtonElement {
    return queryOrFail<HTMLButtonElement>(root, 'button.form-field__help-toggle');
  }

  function helpRegionOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__help');
  }

  function errorRegionOf(root: ParentNode): HTMLElement {
    return queryOrFail<HTMLElement>(root, '.form-field__errors');
  }

  function errorTexts(root: ParentNode): readonly string[] {
    return queryAll<HTMLElement>(root, '.form-field__error').map((message) =>
      collapsedText(message),
    );
  }

  function clickToggle(fixture: ComponentFixture<FormFieldComponent>): void {
    toggleOf(rootOf(fixture)).click();
    fixture.detectChanges();
  }

  describe('the label', () => {
    it('renders a real label element when the caller names a control, which is how the legacy control associated wording', () => {
      const label = captionOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID })));

      expect(label.tagName.toLowerCase())
        .withContext('`labelcontrol.ascx:L2` is a native label wherever there is a control to name')
        .toBe('label');
      expect(label.getAttribute('for')).toBe(CONTROL_ID);
    });

    it('renders a SPAN when the caption names no control, because a label would label nothing', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX }));
      const caption = captionOf(root);

      expect(caption.tagName.toLowerCase()).toBe('span');

      // No label element at all, so there is nothing for the browser to report.
      expect(root.querySelector('label'))
        .withContext('no unassociated label is emitted')
        .toBeNull();

      // The caption is otherwise identical: the class is what the appearance rests on, and the `id`
      // is what every ARIA reference to it resolves through.
      expect(caption.classList.contains('form-field__label')).toBeTrue();
      expect(caption.id.length).toBeGreaterThan(0);
      expect(caption.hasAttribute('for')).toBeFalse();
      expect(caption.textContent ?? '').toContain(LABEL_WITHOUT_SUFFIX);
    });

    it('renders the caption as a span when the caller names NO control, so no label associates with nothing', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: '' }));
      const caption = captionOf(root);

      expect(caption.tagName.toLowerCase())
        .withContext('a caption with no control to point at must not be expressed as a label')
        .toBe('span');
      expect(isAbsent(root, 'label.form-field__label'))
        .withContext('no label element may survive anywhere in the field')
        .toBeTrue();
    });

    it('switches the caption element when a control identifier arrives after the first render', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: '' });

      expect(captionOf(rootOf(fixture)).tagName.toLowerCase()).toBe('span');

      applyInputs(fixture, { for: CONTROL_ID });

      const label = labelElementOf(rootOf(fixture));

      expect(label.tagName.toLowerCase()).toBe('label');
      expect(attributeOrFail(label, 'for')).toBe(CONTROL_ID);
    });

    it('carries the published caption identifier in BOTH element forms, so no reference dangles', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: '' });

      expect(attributeOrFail(captionOf(rootOf(fixture)), 'id'))
        .withContext('the span form must carry the published identifier')
        .toBe(fixture.componentInstance.labelId());

      applyInputs(fixture, { for: CONTROL_ID });

      expect(attributeOrFail(captionOf(rootOf(fixture)), 'id'))
        .withContext('and so must the label form, after the switch')
        .toBe(fixture.componentInstance.labelId());
    });

    it('renders the required marker in the span form as well as in the label form', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: '', required: true }));
      const marker = queryOrFail<HTMLElement>(root, '.form-field__required');

      expect(captionOf(root).contains(marker))
        .withContext('the marker joins the accessible name in both forms, or a required field loses it')
        .toBeTrue();
    });

    it('captions with a span INSTEAD when there is no control to name, so no label captions nothing', () => {
      // Nothing about the accessible name changes: the caption keeps the same class and the same id, the
      // control group still points at it, and the per-control fallback still points at it.
      const caption = captionOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX })));

      expect(caption.tagName.toLowerCase()).toBe('span');
      expect(caption.hasAttribute('for'))
        .withContext('a span cannot carry a control association, and must not pretend to')
        .toBeFalse();
      // The caption is still a caption: same wording, same identifier to be named from.
      expect(collapsedText(caption)).toBe(LABEL_WITHOUT_SUFFIX);
      expect((caption.getAttribute('id') ?? '').length).toBeGreaterThan(0);
    });

    it('switches between the two caption elements as the control identifier comes and goes', () => {
      // The discriminating test for the rule above: one field, driven both ways, so neither branch can
      // pass by accident of the fixture it happens to be given.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });

      expect(captionOf(rootOf(fixture)).tagName.toLowerCase()).toBe('label');

      applyInputs(fixture, { for: '' });

      expect(captionOf(rootOf(fixture)).tagName.toLowerCase()).toBe('span');

      applyInputs(fixture, { for: OTHER_CONTROL_ID });

      const restored = captionOf(rootOf(fixture));

      expect(restored.tagName.toLowerCase()).toBe('label');
      expect(restored.getAttribute('for')).toBe(OTHER_CONTROL_ID);
    });

    it('renders the supplied wording', () => {
      const label = captionOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX })));

      expect(collapsedText(label)).toBe(LABEL_WITHOUT_SUFFIX);
    });

    it('carries the projected control identifier as its `for`', () => {
      const label = labelElementOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID })));

      expect(attributeOrFail(label, 'for')).toBe(CONTROL_ID);
    });

    it('follows a change of control identifier', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });

      applyInputs(fixture, { for: OTHER_CONTROL_ID });

      expect(attributeOrFail(labelElementOf(rootOf(fixture)), 'for')).toBe(OTHER_CONTROL_ID);
    });

    it('emits NO `for` attribute at all when no control identifier is supplied', () => {
      const caption = captionOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: '' })));

      expect(caption.hasAttribute('for'))
        .withContext('an empty control identifier must remove the attribute, not blank it')
        .toBeFalse();
    });

    it('renders unconditionally, keeping the field shape when no wording is supplied', () => {
      // `labelcontrol.ascx` renders its label element with no condition attached, so a field with no wording
      // keeps the same shape rather than collapsing.
      const label = captionOf(rootOf(createField({ label: '' })));

      expect(collapsedText(label)).toBe('');
    });

    it('carries an identifier so other elements can name themselves from it', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID });
      const label = captionOf(rootOf(fixture));

      expect(attributeOrFail(label, 'id'))
        .withContext('the component publishes this identifier as `labelId`')
        .toBe(fixture.componentInstance.labelId());
    });
  });

  // MIGRATION: six legacy punctuation outcomes are normalised to one here. The component strips ONE trailing
  // colon whatever its origin and adds none of its own, which matches the dominant legacy majority.

  describe('label punctuation', () => {
    function renderedLabel(text: string): string {
      return collapsedText(captionOf(rootOf(createField({ label: text }))));
    }

    it('strips a colon that arrived through the suffix attribute', () => {
      // `editroles.ascx` declares `plRoleName` with `Suffix=":"` over the resource value `RoleName.Text` =
      // 'Role Name'.
      expect(renderedLabel(LABEL_WITH_DECLARED_SUFFIX)).toBe(LABEL_WITHOUT_SUFFIX);
    });

    it('strips a colon that was baked into the resource value', () => {
      // `plRSVPCode.Text` is 'RSVP Code:' and `editroles.ascx` declares no suffix, so the same visible
      // outcome arrives by a different route and must normalise the same way.
      expect(renderedLabel(LABEL_WITH_BAKED_COLON)).toBe(LABEL_WITH_BAKED_COLON_RESOLVED);
    });

    it('strips a colon that was baked into an inline markup attribute', () => {
      // `editroles.ascx` declares `plIcon` with `Text="Icon:"` and no resource key at all - the third route
      // to the same character.
      expect(renderedLabel(LABEL_WITH_INLINE_MARKUP_COLON)).toBe('Icon');
    });

    it('PRESERVES a question mark, which carries meaning rather than decoration', () => {
      expect(renderedLabel(LABEL_WITH_QUESTION_MARK)).toBe(LABEL_WITH_QUESTION_MARK);
      expect(renderedLabel(LABEL_WITH_QUESTION_MARK_SECOND)).toBe(LABEL_WITH_QUESTION_MARK_SECOND);
    });

    it('PRESERVES parentheses, which the field help proves are an instruction', () => {
      // `BillingPeriod.Help` reads 'These two fields are used in conjunction to enter a Billing Period', so
      // '(Every)' tells the reader how to fill the field in.
      expect(renderedLabel(LABEL_WITH_PARENTHESES)).toBe(LABEL_WITH_PARENTHESES);
      expect(renderedLabel(LABEL_WITH_PARENTHESES_SECOND)).toBe(LABEL_WITH_PARENTHESES_SECOND);
    });

    it('leaves an already unpunctuated label untouched', () => {
      // `plRoleGroups` is declared with `Suffix=""` at `editroles.ascx` over the resource value 'Role Group'
      // - the explicitly-empty case.
      expect(renderedLabel(LABEL_ALREADY_UNPUNCTUATED)).toBe(LABEL_ALREADY_UNPUNCTUATED);
    });

    it('strips only ONE trailing colon', () => {
      expect(renderedLabel('Role Name::')).toBe('Role Name:');
    });

    it('leaves an INTERIOR colon alone', () => {
      // Only a trailing colon is punctuation the control added; a colon inside the wording is wording.
      expect(renderedLabel('Fee: Currency')).toBe('Fee: Currency');
    });

    it('strips the whitespace that sat in front of a stripped colon', () => {
      expect(renderedLabel('Role Name :')).toBe(LABEL_WITHOUT_SUFFIX);
    });
  });

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
      // The real pair from `editroles.ascx`: a data-type check followed by a greater-than-or-equal check,
      // both on `txtServiceFee`.
      const root = rootOf(
        createField({
          label: 'Service Fee',
          error: [ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR],
        }),
      );

      expect(errorTexts(root)).toEqual([ERROR_FIRST_OF_PAIR, ERROR_SECOND_OF_PAIR]);
    });

    it('render two IDENTICAL messages without failing on a duplicated key', () => {
      // The template must track by index rather than by value. A server can genuinely report the same
      // failure twice, and tracking by value would raise a duplicate-key error at exactly that moment.
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
      // Set directly rather than through the bundle, because an absent bundle member and an explicit
      // `undefined` are different assertions and both must hold.
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
      // The read type is narrower than the write type on purpose, so nothing downstream has to repeat the
      // normalisation or guess which form it received.
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

  describe('legacy break markup', () => {
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
      // The markup in scope is inconsistent, so each of these is a shape that can genuinely arrive. All must
      // resolve to the same wording.
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
      // An interior break must not silently glue two sentences together. The component resolves it to a
      // newline character, which keeps the words separated in the accessible text and leaves the visual
      // treatment to the stylesheet.
      const rendered = renderedMessage(`${ERROR_FIRST_OF_PAIR}<br>${ERROR_SECOND_OF_PAIR}`);

      expect(rendered).toBe(`${ERROR_FIRST_OF_PAIR}\n${ERROR_SECOND_OF_PAIR}`);
      expect(rendered.includes(`Valid${ERROR_SECOND_OF_PAIR}`))
        .withContext('the two sentences must not be concatenated into one word')
        .toBeFalse();
    });

    it('is removed from the label as well as from a message', () => {
      const label = captionOf(
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
      const label = captionOf(rootOf(createField({ label: '<b>Role Name</b>' })));

      expect(label.querySelector('b')).toBeNull();
      expect(collapsedText(label)).toBe('<b>Role Name</b>');
    });
  });

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
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT }));
      const label = captionOf(root);
      const toggle = toggleOf(root);

      expect(label.querySelector('button'))
        .withContext('the legacy nesting must not be reproduced')
        .toBeNull();
      expect(label.contains(toggle))
        .withContext('the affordance must sit outside the label entirely')
        .toBeFalse();
    });

    it('is a button that can never submit a form', () => {
      // The faithful translation of `CausesValidation="False"` on `labelcontrol.ascx`.
      const toggle = toggleOf(rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT })));

      expect(toggle.tagName.toLowerCase()).toBe('button');
      expect(toggle.type).toBe('button');
    });

    it('does NOT carry a negative tab index', () => {
      // MIGRATION: the regression test for the second accessibility divergence.
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
      // The revealed block is REMOVED from the document when collapsed rather than kept and hidden, so a
      // reference to it would resolve to nothing. The expanded state is carried by the expansion attribute
      // either way.
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

  describe('the help region', () => {
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
      const label = captionOf(root);

      expect(label.querySelector('.form-field__help')).toBeNull();
      expect(label.contains(helpRegionOf(root))).toBeFalse();
    });

    it('sits AFTER the label in document order', () => {
      const root = revealedField();
      const position = captionOf(root).compareDocumentPosition(helpRegionOf(root));

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
      // A title attribute is a tool tip by another name: it appears on hover only, is unreachable by
      // keyboard and is announced inconsistently.
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
      // The rich host names a control, so its caption is the `label` branch. Asserted rather than
      // assumed, because the element a group is named FROM is the thing under test here.
      expect(target === null ? '' : target.tagName.toLowerCase()).toBe('label');
      expect(target === null ? '' : target.className).toContain('form-field__label');
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
      // MIGRATION: the regression test for the third accessibility divergence.
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
      // A composite field may legitimately want a more specific name on its second control, such as the
      // frequency of a billing period. An explicit decision wins.
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

  // The field-label fallback must be a LAST resort, and getting that wrong is worse than omitting it:
  // overwriting a control's own name replaces something specific with something generic.

  describe('projected controls that name themselves', () => {
    function nameReference(root: ParentNode, selector: string): string | null {
      return queryOrFail<HTMLElement>(root, selector).getAttribute('aria-labelledby');
    }

    it('leaves a HYPERLINK named by its own visible text alone', () => {
      // `roles.ascx` wraps an edit image in a hyperlink. An anchor takes its name from its contents, so it
      // is already named and must not be relabelled.
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
      // `roles.ascx` is an image button. An image control takes its name from its alternative text, which
      // the caller supplies.
      const fixture = createRichHost();

      fixture.componentInstance.showImageButton = true;
      fixture.detectChanges();

      expect(nameReference(richRootOf(fixture), 'input.probe-image')).toBeNull();
    });

    it('names an image button that carries NO alternative text', () => {
      // The legacy help image at `labelcontrol.ascx` carried none either, which is why this case matters
      // rather than being hypothetical.
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
      // 23 of the 28 in-scope check boxes depend entirely on the field label, but 5 carry their own wording.
      // Native association outranks the field-level fallback.
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
      // `editroles.ascx` puts four command buttons on the screen, and a button takes its name from its
      // contents, so it arrives already named.
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
      // The component recognises the roles that take a name from their contents, so a custom control built
      // to one of them is treated exactly like the native element it stands in for.
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
      const fixture = createRichHost();
      const root = richRootOf(fixture);
      const select = queryOrFail<HTMLSelectElement>(root, 'select');

      expect(attributeOrFail(select, 'aria-labelledby')).toBe(
        `${fixture.componentInstance.controlId}-label`,
      );

      select.setAttribute('aria-labelledby', 'probe-custom-name');

      // Any input change triggers the next content check, which is where the component would otherwise
      // reassert its own reference.
      fixture.componentInstance.label = LABEL_WITH_PARENTHESES;
      fixture.detectChanges();

      expect(attributeOrFail(select, 'aria-labelledby'))
        .withContext('an explicit decision must survive the next content check')
        .toBe('probe-custom-name');
      expect(everyReferenceResolves(root, 'probe-custom-name')).toBeTrue();
    });

    it('removes its own reference when the field loses its label wording', () => {
      // A fallback that outlived the element it points at would be the dangling reference the whole design
      // works to avoid.
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

  describe('the unavailable state', () => {
    it('marks the host unavailable when the projected control is disabled', () => {
      const fixture = createHost();

      expect(hostRootOf(fixture).querySelector('.form-field--disabled'))
        .withContext('an enabled field is not marked')
        .toBeNull();

      fixture.componentInstance.controlDisabled = true;
      fixture.detectChanges();

      expect(hostRootOf(fixture).querySelector('.form-field--disabled'))
        .withContext('a disabled field is marked, so the existing rule applies')
        .not.toBeNull();
    });

    it('stops marking it once the control becomes available again', () => {
      // Read on every content check rather than once, because a control can be enabled or disabled at
      // any time after projection - which is exactly what a cross-field rule does.
      const fixture = createHost();

      fixture.componentInstance.controlDisabled = true;
      fixture.detectChanges();
      fixture.componentInstance.controlDisabled = false;
      fixture.detectChanges();

      expect(hostRootOf(fixture).querySelector('.form-field--disabled')).toBeNull();
    });

    it('does NOT mark a field that projects no control at all', () => {
      // Several screens use this component to present a read-only value, projecting a paragraph rather
      // than a control. Nothing there is unavailable, so nothing may be muted.
      const fixture = TestBed.createComponent(FormFieldComponent);

      fixture.componentRef.setInput('label', 'Password Expires');
      fixture.detectChanges();

      expect(rootOf(fixture).classList.contains('form-field--disabled')).toBeFalse();
    });
  });

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

      expect(captionOf(root).contains(marker))
        .withContext('so the wording travels with the group and the control references')
        .toBeTrue();
    });

    it('does not disturb the label wording itself', () => {
      const root = rootOf(createField({ label: LABEL_WITH_DECLARED_SUFFIX, required: true }));

      expect(collapsedText(captionOf(root)).startsWith(LABEL_WITHOUT_SUFFIX)).toBeTrue();
    });
  });

  describe('the rendered structure', () => {
    /**
     * Renders the field with EVERY optional part present - label, required marker, two projected
     * controls, revealed help and two messages - so a prohibited element cannot hide behind an unrendered
     * branch.
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
      // Landmarks belong exclusively to the application shell. A field that declared one would duplicate a
      // landmark on every screen it appears on, which turns a navigation aid into noise.
      const root = hostRootOf(fullyRenderedHost());

      expect(isAbsent(root, 'header')).toBeTrue();
      expect(isAbsent(root, 'main')).toBeTrue();
      expect(isAbsent(root, 'nav')).toBeTrue();
      expect(isAbsent(root, 'footer')).toBeTrue();
    });

    it('emits NO break element', () => {
      // MIGRATION: `labelcontrol.ascx` used one as vertical spacing. Spacing here is the stylesheet spacing
      // scale, which is adjustable and cannot be collapsed away.
      const root = hostRootOf(fullyRenderedHost());

      expect(queryAll(root, 'br').length)
        .withContext('the legacy layout break is replaced by stylesheet spacing')
        .toBe(0);
    });

    it('emits NO non-breaking space', () => {
      const root = hostRootOf(fullyRenderedHost());

      expect(rawText(root).includes('\u00a0'))
        .withContext('inter-control spacing is not content')
        .toBeFalse();
    });

    it('declares NO field set or legend', () => {
      // Those belong to a group of fields and are owned globally by the form stylesheet. This component
      // wraps exactly one field.
      const root = hostRootOf(fullyRenderedHost());

      expect(isAbsent(root, 'fieldset')).toBeTrue();
      expect(isAbsent(root, 'legend')).toBeTrue();
    });

    it('carries NO inline style attribute on any element', () => {
      // Every presentational value lives in the paired stylesheet and resolves to a design token. An inline
      // style is a value that no token governs.
      const root = hostRootOf(fullyRenderedHost());

      expect(queryAll(root, '[style]').length).toBe(0);
    });

    it('leaves no identifier reference in the whole field pointing at nothing', () => {
      // One sweep over every naming and describing reference the field emits, so a future change cannot
      // introduce a dangling one anywhere.
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

  describe('the component contract', () => {
    it('is standalone, which is the only reason it can be supplied through imports', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX });

      expect(collapsedText(captionOf(rootOf(fixture)))).toBe(LABEL_WITHOUT_SUFFIX);
      expect(collapsedText(captionOf(hostRootOf(createHost())))).toBe(LABEL_WITH_PARENTHESES);
    });

    it('renders every input change pushed in through the component reference', () => {
      // The correct way to drive a component that declares the on-push strategy: an input write marks the
      // view for check. All five inputs in one pass.
      const fixture = createField();

      applyInputs(fixture, {
        label: LABEL_WITH_DECLARED_SUFFIX,
        for: CONTROL_ID,
        required: true,
        help: HELP_TEXT,
        error: ERROR_WITH_LEADING_BREAK,
      });

      const root = rootOf(fixture);

      expect(collapsedText(captionOf(root)).startsWith(LABEL_WITHOUT_SUFFIX)).toBeTrue();
      expect(attributeOrFail(captionOf(root), 'for')).toBe(CONTROL_ID);
      expect(isAbsent(root, '.form-field__required')).toBeFalse();
      expect(isAbsent(root, 'button.form-field__help-toggle')).toBeFalse();
      expect(errorTexts(root)).toEqual([ERROR_WITHOUT_LEADING_BREAK]);
    });

    it('is refreshed through its INPUTS and not by a write to an instance field', () => {
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
      // An identifier must never change underneath a reference already rendered into a description.
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, for: '' });
      const before = fixture.componentInstance.labelId();

      applyInputs(fixture, { label: LABEL_ALREADY_UNPUNCTUATED, required: true });

      expect(fixture.componentInstance.labelId()).toBe(before);
    });

    it('treats a value that arrives UNTYPED as absent rather than failing to render', () => {
      const fixture = createField({ label: LABEL_WITHOUT_SUFFIX, help: HELP_TEXT });

      fixture.componentRef.setInput('label', 42);
      fixture.componentRef.setInput('help', 42);
      fixture.detectChanges();

      const root = rootOf(fixture);

      expect(collapsedText(captionOf(root)))
        .withContext('an unreadable value renders as nothing, never as its own text')
        .toBe('');
      expect(isAbsent(root, 'button.form-field__help-toggle'))
        .withContext('and unreadable help counts as no help at all')
        .toBeTrue();
    });

    it('renders through the real template, which is how this folder gets type-checked', () => {
      const root = rootOf(createField({ label: LABEL_WITHOUT_SUFFIX, for: CONTROL_ID }));

      expect(isAbsent(root, '.form-field')).toBeFalse();
      expect(isAbsent(root, '.form-field__label-row')).toBeFalse();
      expect(isAbsent(root, '.form-field__control')).toBeFalse();
    });

    it('performs no network access, which the verification after every case confirms', () => {
      // Made explicit here as well as implicitly in `afterEach`: a presentational component holds no data
      // access, and every request path belongs behind a typed client service. `verify()` runs after this
      // case with nothing outstanding.
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

  // ---------------------------------------------------------------------------
  // THE FAILURE STATE ON THE CONTROL ITSELF
  // ---------------------------------------------------------------------------

  describe('the failure state written onto projected controls', () => {
    // ⚠ ARIA DESCRIPTIONS ARE NOT INHERITED. The composite group carries `aria-describedby` for the field
    // as a whole, and that reference is announced when the group is entered - not when the control inside
    // it is reached, which is the moment a person needs to be told what is wrong with the box they are
    // sitting in.

    it('marks the control invalid and points it at the message when a failure is reported', () => {
      const fixture = createHost();

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const control = queryOrFail<HTMLInputElement>(root, 'input[type="text"]');
      const region = errorRegionOf(root);

      expect(control.getAttribute('aria-invalid')).toBe('true');
      expect(control.getAttribute('aria-errormessage')).toBe(region.id);
      expect(referencesOf(control, 'aria-describedby')).toContain(region.id);
    });

    it('withdraws all three the moment the field becomes valid again', () => {
      const fixture = createHost();

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      fixture.componentInstance.error = null;
      fixture.detectChanges();

      const control = queryOrFail<HTMLInputElement>(hostRootOf(fixture), 'input[type="text"]');

      // Removed rather than set to "false": a control that is no longer in error is not making a statement
      // about its validity, and a dangling reference to a region that has left the document is worse than
      // no reference at all.
      expect(control.hasAttribute('aria-invalid')).toBeFalse();
      expect(control.hasAttribute('aria-errormessage')).toBeFalse();
      expect(control.hasAttribute('aria-describedby')).toBeFalse();
    });

    it('describes the control with the help region while the disclosure is open', () => {
      const fixture = createHost();

      fixture.componentInstance.help = HELP_TEXT;
      fixture.detectChanges();

      const control = queryOrFail<HTMLInputElement>(hostRootOf(fixture), 'input[type="text"]');

      expect(control.hasAttribute('aria-describedby'))
        .withContext('a closed disclosure is not in the document, so nothing may reference it')
        .toBeFalse();

      toggleOf(hostRootOf(fixture)).click();
      fixture.detectChanges();

      expect(referencesOf(control, 'aria-describedby')).toEqual([
        helpRegionOf(hostRootOf(fixture)).id,
      ]);
    });

    it('reads the error BEFORE the help, in the order they are seen', () => {
      const fixture = createHost();

      fixture.componentInstance.help = HELP_TEXT;
      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();
      toggleOf(hostRootOf(fixture)).click();
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const control = queryOrFail<HTMLInputElement>(root, 'input[type="text"]');

      // ⚠ THIS ASSERTION WAS THE OTHER WAY ROUND, AND IT ENCODED A DEFECT — QA-21 / QA-24. It required the
      // help sentence to be announced before the reason the value had just been refused, and the rendered
      // order agreed with it: measured on the portal settings screen, opening Help pushed the validation
      // message to 151.5px below the bottom edge of the box it described, because the panel was inserted
      // between them. Both orders were corrected together, and this states the one that must hold.
      expect(referencesOf(control, 'aria-describedby')).toEqual([
        errorRegionOf(root).id,
        helpRegionOf(root).id,
      ]);
    });

    it('renders the error above the help, so the two orders agree', () => {
      const fixture = createHost();

      fixture.componentInstance.help = HELP_TEXT;
      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();
      toggleOf(hostRootOf(fixture)).click();
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const children = Array.from(
        queryOrFail<HTMLElement>(root, '.form-field').children,
      ) as readonly Element[];
      const errorIndex = children.indexOf(errorRegionOf(root));
      const helpIndex = children.indexOf(helpRegionOf(root));

      // Asserted on POSITION rather than on geometry: the two are siblings in one flow, so document order
      // is what decides which of them a reader meets first, and it is what a headless run can state
      // without depending on a layout engine's line boxes.
      expect(errorIndex).withContext('the error is a direct child of the field').toBeGreaterThan(-1);
      expect(helpIndex).withContext('the help is a direct child of the field').toBeGreaterThan(-1);
      expect(errorIndex).toBeLessThan(helpIndex);
    });

    it('describes every control of a composite field, not just the first', () => {
      const fixture = createHost();

      fixture.componentInstance.showFrequency = true;
      fixture.componentInstance.frequencyName = 'Billing Frequency';
      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const region = errorRegionOf(root);

      for (const control of [
        queryOrFail<HTMLElement>(root, 'input[type="text"]'),
        queryOrFail<HTMLElement>(root, 'select'),
      ]) {
        expect(control.getAttribute('aria-invalid')).toBe('true');
        expect(referencesOf(control, 'aria-describedby')).toContain(region.id);
      }
    });

    it('leaves a link and a command button out of it entirely', () => {
      // `roles.ascx` L5-L16 projects a drop-down list, an Edit link and a Delete image button under one
      // label. The field's failure is about the list; describing Delete with it would be a lie, and
      // `aria-invalid` on a link means nothing at all.
      const fixture = createRichHost();

      fixture.componentInstance.showEditLink = true;
      fixture.componentInstance.showSubmit = true;
      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      const root = richRootOf(fixture);
      const link = queryOrFail<HTMLAnchorElement>(root, 'a.probe-link');

      expect(link.hasAttribute('aria-invalid')).toBeFalse();
      expect(link.hasAttribute('aria-describedby')).toBeFalse();

      // The submit input IS a form control and does carry the state - `input` is describable, and a
      // submit button inside the field slot is projected content the field genuinely owns.
      expect(queryOrFail<HTMLElement>(root, 'select').getAttribute('aria-invalid')).toBe('true');
    });

    it("keeps a consumer's own description and adds to it, then withdraws only its own", () => {
      const fixture = createRichHost();

      const control = queryOrFail<HTMLElement>(richRootOf(fixture), 'select');

      control.setAttribute('aria-describedby', 'probe-custom-name');

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      const region = errorRegionOf(richRootOf(fixture));

      expect(referencesOf(control, 'aria-describedby')).toEqual(['probe-custom-name', region.id]);

      fixture.componentInstance.error = null;
      fixture.detectChanges();

      expect(referencesOf(control, 'aria-describedby'))
        .withContext("the consumer's own reference is not collateral damage")
        .toEqual(['probe-custom-name']);
    });

    it("never overwrites a consumer that states validity itself, but still names the message", () => {
      const fixture = createRichHost();

      const control = queryOrFail<HTMLElement>(richRootOf(fixture), 'select');

      control.setAttribute('aria-invalid', 'false');

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      const region = errorRegionOf(richRootOf(fixture));

      // The consumer's statement stands - it may know something this component does not.
      expect(control.getAttribute('aria-invalid')).toBe('false');
      expect(control.getAttribute('aria-errormessage')).toBe(region.id);
      expect(referencesOf(control, 'aria-describedby')).toContain(region.id);
    });

    it("never withdraws a message reference the consumer wrote for a rule this field does not own", () => {
      const fixture = createRichHost();

      const control = queryOrFail<HTMLElement>(richRootOf(fixture), 'select');

      // What a consumer with a group rule does: name its own region, on a field whose own error is
      // empty because the rule is not about this one control.
      control.setAttribute('aria-errormessage', 'probe-group-rule');
      control.setAttribute('aria-invalid', 'true');

      fixture.componentInstance.error = null;
      fixture.detectChanges();

      expect(control.getAttribute('aria-errormessage'))
        .withContext('the reference this component never wrote is not this component to remove')
        .toBe('probe-group-rule');
      expect(control.getAttribute('aria-invalid'))
        .withContext("and the consumer's own state stands with it")
        .toBe('true');
    });

    it('still withdraws the reference it wrote itself once its own region has gone', () => {
      const fixture = createRichHost();

      const control = queryOrFail<HTMLElement>(richRootOf(fixture), 'select');

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      expect(control.getAttribute('aria-errormessage')).toBe(errorRegionOf(richRootOf(fixture)).id);

      fixture.componentInstance.error = null;
      fixture.detectChanges();

      // A dangling reference is worse than no reference, and this one names a region that has left
      // the document. Ownership is what distinguishes this case from the one above.
      expect(control.hasAttribute('aria-errormessage'))
        .withContext('a reference to a region that no longer exists is withdrawn')
        .toBeFalse();
    });

    it("leaves a consumer's group reference in place even when this field develops an error of its own", () => {
      const fixture = createRichHost();

      const control = queryOrFail<HTMLElement>(richRootOf(fixture), 'select');

      control.setAttribute('aria-errormessage', 'probe-group-rule');

      fixture.componentInstance.error = ERROR_WITHOUT_LEADING_BREAK;
      fixture.detectChanges();

      // `aria-errormessage` takes ONE identifier, so the two cannot both be named through it and a choice
      // has to be made. The consumer's stands, for the same reason its `aria-invalid` stands: it may know
      // something this component does not.
      expect(control.getAttribute('aria-errormessage')).toBe('probe-group-rule');
      expect(referencesOf(control, 'aria-describedby'))
        .withContext("this field's own message is still described")
        .toContain(errorRegionOf(richRootOf(fixture)).id);
    });
  });

  // ---------------------------------------------------------------------------
  // FOCUS ORDER BETWEEN THE CONTROL AND ITS HELP AFFORDANCE
  // ---------------------------------------------------------------------------

  describe('focus order between the control and its help affordance', () => {
    /**
     * The field's focusable elements in the order sequential focus will visit them. Reads DOM order and
     * excludes anything explicitly taken out of the order, which is what the Tab key does for elements
     * that carry no positive tabindex - and every focusable element this component renders or projects
     * carries none, so DOM order IS the tab order here.
     *
     * ⚠ THIS IS DELIBERATELY NOT AN ASSERTION ABOUT `compareDocumentPosition` BETWEEN TWO CHOSEN NODES.
     * The defect being guarded against is an ORDER a person traverses, so the measurement collects the
     * whole traversal and asks where each control falls within it.
     */
    function focusOrder(root: ParentNode): readonly HTMLElement[] {
      return Array.from(root.querySelectorAll('input, select, textarea, button, a[href]'))
        .filter((node): node is HTMLElement => node instanceof HTMLElement)
        .filter((node) => node.getAttribute('tabindex') !== '-1');
    }

    it('visits the projected control before the help affordance', () => {
      // ⚠ THE DEFECT THIS CLOSES: the affordance was the second child of the label row, so sequential focus
      // reached "Help" BEFORE the field it describes. On a screen carrying fourteen fields that doubled the
      // stops between one control and the next, and a person tabbing forward was interrupted by an
      // affordance for a field they had not arrived at yet.
      const fixture = createHost();

      fixture.componentInstance.help = 'What this field is for.';
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const order = focusOrder(root);
      const control = queryOrFail<HTMLInputElement>(root, 'input[type="text"]');
      const toggle = toggleOf(root);

      expect(order).withContext('both are focusable, so both are in the order').toContain(control);
      expect(order).toContain(toggle);
      expect(order.indexOf(toggle))
        .withContext('the affordance follows the control it describes')
        .toBeGreaterThan(order.indexOf(control));
    });

    it('visits every projected control before the help affordance, not just the first', () => {
      // The narrowing case, and it is the one that separates "after the FIRST control" from "after the
      // control GROUP". A field may project more than one control - this host projects a text box and a
      // frequency select - and an affordance placed between them would still satisfy the spec above while
      // interrupting the field exactly as before.
      const fixture = createHost();

      fixture.componentInstance.help = 'What this field is for.';
      fixture.componentInstance.showFrequency = true;
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const order = focusOrder(root);
      const toggle = toggleOf(root);
      const projected = order.filter((node) => node !== toggle);

      expect(projected.length).withContext('a text box and a select').toBe(2);
      expect(order.indexOf(toggle))
        .withContext('the affordance comes after ALL of them')
        .toBe(order.length - 1);
    });

    it('keeps the caption ahead of the control it names', () => {
      // ⚠ THE COST THE CHOSEN FIX HAD TO AVOID, asserted so a later attempt to move the whole label row
      // instead of the affordance alone cannot pass. Moving the row would have fixed the order above while
      // putting every field's NAME after its control in reading order, which is the worse defect of the two:
      // a screen reader working through the document linearly would meet the field before being told what
      // it is.
      const fixture = createHost();

      fixture.componentInstance.help = 'What this field is for.';
      fixture.detectChanges();

      const root = hostRootOf(fixture);
      const caption = labelElementOf(root);
      const control = queryOrFail<HTMLInputElement>(root, 'input[type="text"]');

      expect(caption.compareDocumentPosition(control) & Node.DOCUMENT_POSITION_FOLLOWING)
        .withContext('the caption still precedes its control in the document')
        .toBeGreaterThan(0);
    });

    it('places the affordance inside the group the caption names', () => {
      // Where it now lives, stated as a structural fact rather than left implicit: the affordance is part of
      // the field, so it belongs inside the wrapper the caption labels rather than trailing the whole block.
      // This is also what keeps it on the control's own row, which is why the move added no height.
      const fixture = createHost();

      fixture.componentInstance.help = 'What this field is for.';
      fixture.detectChanges();

      const root = hostRootOf(fixture);

      expect(slotOf(root).contains(toggleOf(root)))
        .withContext('inside the control wrapper')
        .toBeTrue();
      expect(queryOrFail<HTMLElement>(root, '.form-field__label-row').contains(toggleOf(root)))
        .withContext('and no longer in the caption row')
        .toBeFalse();
    });
  });
});

/**
 * A host that renders the field in its CHOICE arrangement - the `form-field--inline` variant, carrying a
 * checkbox - inside a container narrow enough to reproduce the grid cell the membership screen puts these
 * in. The container width and the label-track override are both writable, because the two findings this
 * arrangement answers were each reproduced by a specific combination of the two.
 */
@Component({
  selector: 'app-form-field-choice-host',
  standalone: true,
  imports: [FormFieldComponent],
  template: `
    <div class="probe-cell" [style.inline-size.px]="cellWidth" [style.--field-label-inline-size]="labelTrack">
      <app-form-field class="form-field--inline" [label]="label" [for]="controlId" [help]="help" [error]="error">
        <input [attr.id]="controlId" type="checkbox" />
      </app-form-field>
    </div>
  `,
})
class FormFieldChoiceHostComponent {
  /** `UseAuthProviders.Text` shape - one of the long membership captions that motivated the wide track. */
  public label = 'Require a Unique Display Name';

  public controlId = 'chk-unique-display-name';

  public help = '';

  public error: string | readonly string[] | null = null;

  /** Roughly the measured width of a cell in the membership screen's three-column switch grid. */
  public cellWidth = 350;

  /** The membership screen's own override, expressed as the token value it resolves to. */
  public labelTrack = '18.75rem';
}

/**
 * THE CHOICE ARRANGEMENT - QA-06 and QA-13.
 *
 * Both findings were geometric, so these are measurements rather than class assertions: a rule that names
 * the right selector but resolves to the wrong box is exactly the failure that was shipped, and only a
 * measured position can tell the two apart.
 */
describe('FormFieldComponent choice arrangement', () => {
  let fixture: ComponentFixture<FormFieldChoiceHostComponent>;
  let host: FormFieldChoiceHostComponent;

  /** The caption row, the projected box and the field wrapper, each narrowed or failed loudly. */
  function parts(): { labelRow: HTMLElement; box: HTMLElement; field: HTMLElement } {
    const root = fixture.nativeElement as HTMLElement;

    return {
      labelRow: queryOrFail<HTMLElement>(root, '.form-field__label-row'),
      box: queryOrFail<HTMLElement>(root, 'input[type="checkbox"]'),
      field: queryOrFail<HTMLElement>(root, '.form-field'),
    };
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FormFieldChoiceHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(FormFieldChoiceHostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('keeps the box on the caption\u2019s own row rather than below it', () => {
    const { labelRow, box } = parts();

    const captionBounds = labelRow.getBoundingClientRect();
    const boxBounds = box.getBoundingClientRect();

    // Vertical overlap is the claim, not equal tops: the caption is taller than the box, and the two are
    // baseline-aligned, so their tops legitimately differ.
    expect(boxBounds.top).toBeLessThan(captionBounds.bottom);
    expect(boxBounds.bottom).toBeGreaterThan(captionBounds.top);
  });

  it('sizes the caption track to the caption, so the box is not pushed away by a wide label track', () => {
    const { labelRow, box } = parts();

    const captionBounds = labelRow.getBoundingClientRect();
    const boxBounds = box.getBoundingClientRect();

    // THE MEASUREMENT THAT FAILED BEFORE. With the fixed 300px track inside a 350px cell the box sat
    // roughly 300px from its own caption and about 30px from the NEXT setting's - which is what made four
    // to six of the fourteen switches read as belonging to the wrong label. One spacing step is
    // `--space-2`; a generous ceiling is asserted rather than an exact gap so the token stays free to move.
    expect(boxBounds.left - captionBounds.right).toBeLessThan(24);
    expect(boxBounds.left).toBeGreaterThanOrEqual(captionBounds.right);
  });

  it('keeps the box beside its caption at a narrow width too', () => {
    host.cellWidth = 320;
    fixture.detectChanges();

    const { labelRow, box } = parts();

    const captionBounds = labelRow.getBoundingClientRect();
    const boxBounds = box.getBoundingClientRect();

    expect(boxBounds.left - captionBounds.right).toBeLessThan(24);
    expect(boxBounds.top).toBeLessThan(captionBounds.bottom);
  });

  it('keeps the box beside its caption when the screen does not override the label track', () => {
    host.labelTrack = '9.375rem';
    fixture.detectChanges();

    const { labelRow, box } = parts();

    expect(box.getBoundingClientRect().left - labelRow.getBoundingClientRect().right).toBeLessThan(24);
  });

  it('draws the help panel with a border that separates it from a filled section', () => {
    host.help = HELP_TEXT;
    fixture.detectChanges();

    const toggle = queryOrFail<HTMLButtonElement>(fixture.nativeElement as HTMLElement, '.form-field__help-toggle');
    toggle.click();
    fixture.detectChanges();

    const panel = queryOrFail<HTMLElement>(fixture.nativeElement as HTMLElement, '.form-field__help');
    const computed = getComputedStyle(panel);

    // #CCCCCC, the STRONG border token. The ordinary token was measured to be too close: this panel is filled
    // with the secondary surface and so is the role form's Advanced section, so inside that section the panel
    // was drawn on a fill identical to its own and read as barely raised.
    expect(computed.borderTopColor).toBe('rgb(118, 118, 118)');

    // And provably not inherited from the text colour, which is what it used to be.
    expect(computed.borderTopColor).not.toBe(computed.color);
  });

  it('lets the help panel and the failure list use the whole width rather than the box column', () => {
    host.help = HELP_TEXT;
    host.error = ERROR_WITHOUT_LEADING_BREAK;
    fixture.detectChanges();

    const toggle = queryOrFail<HTMLButtonElement>(fixture.nativeElement as HTMLElement, '.form-field__help-toggle');
    toggle.click();
    fixture.detectChanges();

    const { labelRow, field } = parts();
    const captionLeft = labelRow.getBoundingClientRect().left;

    // Starting at the caption's own inline edge is what proves the full-width span: indented to the control
    // column, each would have had a single checkbox's measure to render a sentence in.
    for (const selector of ['.form-field__help', '.form-field__errors']) {
      const region = queryOrFail<HTMLElement>(field, selector);
      expect(region.getBoundingClientRect().left).toBeCloseTo(captionLeft, 0);
      expect(region.getBoundingClientRect().width).toBeGreaterThan(100);
    }
  });

  // ⚠ QA-24 — OPENING THE EXPLANATION MOVED THE THING IT EXPLAINED. A grid item's automatic minimum size is
  // its own min-content contribution, and a SPANNING item distributes that contribution across the tracks it
  // spans — so the help panel, whose sentence is far longer than any switch caption, inflated this
  // arrangement's content-sized caption track. Measured on the membership screen: opening the help of
  // `Suppress Pager?` widened track 1 from 115.16px to 176.953px and jogged the checkbox and its own Help
  // button 61.79px to the RIGHT, so the control moved out from under the pointer that had just asked about
  // it. Geometric findings get geometric cases: a rule that names the right selector and resolves to the
  // wrong box is exactly the failure that was shipped.
  it('does not move the box sideways when its explanation opens', () => {
    const before = parts().box.getBoundingClientRect();

    fixture.componentInstance.help =
      'Check to hide the pager if only one page of records is available for this listing.';
    fixture.detectChanges();

    queryOrFail<HTMLButtonElement>(
      fixture.nativeElement as HTMLElement,
      '.form-field__help-toggle',
    ).click();
    fixture.detectChanges();

    const panel = queryOrFail<HTMLElement>(
      fixture.nativeElement as HTMLElement,
      '.form-field__help',
    );

    expect(panel.textContent ?? '')
      .withContext('the premise: the explanation is on screen')
      .toContain('Check to hide the pager');

    const after = parts().box.getBoundingClientRect();

    expect(after.left).withContext('the box has not moved').toBeCloseTo(before.left, 1);
    expect(getComputedStyle(panel).minInlineSize)
      .withContext('the panel contributes no minimum to the tracks it spans')
      .toBe('0px');
  });

  it('does not move the box sideways when a failure is reported either', () => {
    const before = parts().box.getBoundingClientRect();

    fixture.componentInstance.error =
      'This setting is required, and the value you entered could not be read as one.';
    fixture.detectChanges();

    const errors = queryOrFail<HTMLElement>(
      fixture.nativeElement as HTMLElement,
      '.form-field__errors',
    );

    expect(getComputedStyle(errors).minInlineSize).toBe('0px');
    expect(parts().box.getBoundingClientRect().left).toBeCloseTo(before.left, 1);
  });
});

/**
 * THE CAPTION ROW MUST NOT WRAP - QA-11.
 *
 * This reverses a deliberate earlier choice, so the specification states the reversal explicitly: the row
 * used to wrap so that a shortfall moved the help affordance to its own line, and measured that put Help on
 * a line of its own in 43 of 60 role cells, sometimes below the control it explained.
 */
describe('FormFieldComponent caption row', () => {
  let fixture: ComponentFixture<FormFieldHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FormFieldHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(FormFieldHostComponent);
    fixture.componentInstance.help = HELP_TEXT;
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('does not permit the help affordance to break onto its own line', () => {
    const labelRow = queryOrFail<HTMLElement>(fixture.nativeElement as HTMLElement, '.form-field__label-row');

    expect(getComputedStyle(labelRow).flexWrap).toBe('nowrap');
  });

  // A CASE PINNING THE AFFORDANCE TO THE CAPTION'S FIRST LINE STOOD HERE, AND THE AFFORDANCE IS NO LONGER IN
  // THE CAPTION ROW AT ALL. It was measured that placing Help as the caption row's second child put it in
  // sequential focus order BEFORE every field, so on a screen carrying fourteen fields the stops between one
  // control and the next doubled and a person tabbing to a field was interrupted by an affordance describing a
  // field they had not reached yet. The affordance therefore sits after the control it explains, inside the
  // baseline-aligned control row - see the template's own note - and so cannot displace the caption at any
  // measure, because it is no longer beside it.
  //
  // The property that case really protected survives in the case above: the caption row does not wrap, so a
  // shortfall in the label column is absorbed by the caption's own text rather than by moving anything onto a
  // line of its own. The affordance's placement beside its control is pinned by 'keeps the box beside its
  // caption when the screen does not override the label track' and by the help-panel cases below.

});

/**
 * Every CSS style rule reachable from the loaded stylesheets, flattened out of whatever `@media`,
 * `@supports` or `@layer` wrappers it sits inside.
 *
 * Cross-origin sheets throw on `cssRules` access and are skipped rather than allowed to fail the sweep;
 * under Karma every sheet is same-origin, so the guard is defensive only.
 */
function everyStyleRule(): readonly CSSStyleRule[] {
  const collected: CSSStyleRule[] = [];

  const walk = (rules: CSSRuleList): void => {
    for (const rule of Array.from(rules)) {
      if (rule instanceof CSSStyleRule) {
        collected.push(rule);
      }

      const nested: unknown = (rule as { cssRules?: CSSRuleList }).cssRules;

      if (nested instanceof CSSRuleList) {
        walk(nested);
      }
    }
  };

  for (const sheet of Array.from(document.styleSheets)) {
    try {
      walk(sheet.cssRules);
    } catch {
      continue;
    }
  }

  return collected;
}

/**
 * Attribute-selector quoting is not stable across the two builds this specification runs under - the
 * optimiser emits `input[type=checkbox]` where the source wrote `input[type='checkbox']` - so both the
 * selector and the fragment being looked for are stripped of quotes before they are compared. Matching on
 * the quoted spelling alone silently found nothing in the optimised bundle.
 */
function withoutQuotes(value: string): string {
  return value.replace(/['"]/g, '');
}

/** The value a selector-matching rule declares for one property, or `null` when no such rule exists. */
function declaredValue(selectorFragments: readonly string[], property: string): string | null {
  for (const rule of everyStyleRule()) {
    const selector = withoutQuotes(rule.selectorText);

    if (!selectorFragments.every((fragment) => selector.includes(withoutQuotes(fragment)))) {
      continue;
    }

    const value = rule.style.getPropertyValue(property);

    if (value.length > 0) {
      return value;
    }
  }

  return null;
}

/**
 * THE NATIVE CONTROL STATES - QA-34 and QA-18.
 *
 * These assert rules that live in the GLOBAL form stylesheet rather than in this component, and they are
 * asserted from here because this is the component that projects every native control kind and because the
 * global sheet is loaded into the test bundle. Two different techniques are used deliberately: a static
 * property is measured on a real rendered control, whereas a `:hover` or `:active` declaration cannot be
 * provoked from script and is therefore read out of the CSSOM.
 */
describe('Native control states', () => {
  let fixture: ComponentFixture<FormFieldRichHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FormFieldRichHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(FormFieldRichHostComponent);
    fixture.componentInstance.showLabelledCheckbox = true;
    fixture.componentInstance.showTextarea = true;
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('gives a choice control the compact target rather than leaving it at its native 13px', () => {
    const checkbox = queryOrFail<HTMLInputElement>(fixture.nativeElement as HTMLElement, 'input[type="checkbox"]');

    const bounds = checkbox.getBoundingClientRect();

    // 24px is the floor that applies to a control with no inline exemption. The 44px minimum is NOT
    // asserted here and must not be: a 13px tick centred in a 44px square reads as a rendering fault.
    expect(bounds.width).toBeGreaterThanOrEqual(24);
    expect(bounds.height).toBeGreaterThanOrEqual(24);
  });

  it('keeps every text-entry control at the full target minimum', () => {
    const root = fixture.nativeElement as HTMLElement;

    for (const selector of ['select', 'textarea']) {
      const control = queryOrFail<HTMLElement>(root, selector);
      expect(control.getBoundingClientRect().height)
        .withContext(selector)
        .toBeGreaterThanOrEqual(44);
    }
  });

  it('answers a press on a choice control, which previously produced no change whatever', () => {
    expect(declaredValue(["input[type='checkbox']", ':active'], 'box-shadow')).not.toBeNull();
    expect(declaredValue(["input[type='radio']", ':active'], 'box-shadow')).not.toBeNull();
  });

  it('answers a hover on a choice control', () => {
    expect(declaredValue(["input[type='checkbox']", ':hover'], 'box-shadow')).not.toBeNull();
  });

  it('answers a press on a text-entry control in the same vocabulary as the button', () => {
    // ⚠ A BACKGROUND TINT, NOT A `box-shadow` RING, AND THE DISTINCTION IS THE WHOLE POINT. The first
    // attempt was an inset ring; runtime measurement showed it was painted for exactly one frame and then
    // lost, because `select:active` and `textarea:active` weigh (0,1,1) — a TIE with their own
    // `:focus-visible` rule, which is declared later and therefore took the single `box-shadow` slot for the
    // rest of the press. `background-color` is uncontested by the focus ring, which claims only `outline` and
    // `box-shadow`, and it is the property and the token the button has always pressed with.
    expect(declaredValue(['textarea', ':active'], 'border-color')).not.toBeNull();
    expect(declaredValue(['textarea', ':active'], 'background-color')).not.toBeNull();
  });

  it('presses a text-entry control with the same token the button presses with', () => {
    const control = declaredValue(['textarea', ':active'], 'background-color') ?? '';
    const button = declaredValue(['button', ':active'], 'background-color') ?? '';

    expect(control).not.toBe('');
    expect(button).not.toBe('');
    expect(control).toBe(button);
  });

  it('does not answer a text-entry press through a property the focus ring already claims', () => {
    // A regression guard, stated as a prohibition because that is what the defect was: reintroducing a
    // `box-shadow` here would compile, would look right in a static review, and would silently stop being
    // visible on two of the three control kinds the moment the control took focus.
    expect(declaredValue(['textarea', ':active'], 'box-shadow')).toBeNull();
  });

  it('states every state declaration in a token rather than a literal length or colour', () => {
    const declarations = [
      declaredValue(["input[type='checkbox']", ':hover'], 'box-shadow'),
      declaredValue(["input[type='checkbox']", ':active'], 'box-shadow'),
      declaredValue(['textarea', ':active'], 'background-color'),
      declaredValue(['textarea', ':active'], 'border-color'),
    ];

    for (const declaration of declarations) {
      expect(declaration).not.toBeNull();
      expect(declaration ?? '').toContain('var(--');
    }
  });

  it('keeps a read-only control looking read-only when it is pressed', () => {
    // The guard is load-bearing: `input:not(...):active` outweighs `input[readonly]`, so without it a
    // read-only field would flash the pressed tint as though it were about to accept typing.
    //
    // ⚠ THE RULES THIS SWEEPS ARE THE ONES THAT PAINT, AND THAT NARROWING IS DELIBERATE. Sweeping every rule
    // whose selector merely mentions a pressed control also catches the reduced-motion press EXEMPTION, which
    // declares one property - `transition-duration: 0s` - and therefore cannot make a read-only field look
    // pressed. That rule has to name the bare compounds, because it mirrors the resting transition it exempts
    // and is asserted compound by compound in `global-interaction-states.spec.ts`. The guard belongs on the
    // declaration that produces the appearance, so that is what is asserted here.
    const painting = everyStyleRule()
      .filter((rule) => withoutQuotes(rule.selectorText).includes('textarea:active'))
      .filter((rule) => rule.style.getPropertyValue('background-color').trim().length > 0);

    expect(painting.length)
      .withContext('the pressed tint is declared at all, so this is not passing vacuously')
      .toBeGreaterThan(0);

    for (const rule of painting) {
      expect(withoutQuotes(rule.selectorText)).toContain('readonly');
    }
  });
});

/**
 * THE DISCLOSURE TARGET - QA-18. The rule is global, so it is asserted on a bare `summary` rendered here
 * rather than by loading one of the two screens that own a disclosure.
 */
@Component({
  selector: 'app-form-field-disclosure-host',
  standalone: true,
  template: `
    <details>
      <summary>Advanced Settings</summary>
      <p>Body</p>
    </details>
  `,
})
class DisclosureHostComponent {}

describe('Disclosure target size', () => {
  let fixture: ComponentFixture<DisclosureHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DisclosureHostComponent] }).compileComponents();

    fixture = TestBed.createComponent(DisclosureHostComponent);
    fixture.detectChanges();
  });

  it('meets the full target minimum', () => {
    const summary = queryOrFail<HTMLElement>(fixture.nativeElement as HTMLElement, 'summary');

    expect(summary.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
  });

  it('keeps the platform disclosure marker, which a flex spelling would have suppressed', () => {
    const summary = queryOrFail<HTMLElement>(fixture.nativeElement as HTMLElement, 'summary');

    const computed = getComputedStyle(summary);

    expect(computed.display).toBe('list-item');
    expect(computed.listStyleType).toBe('disclosure-closed');
  });
});

describe('FormFieldComponent — the typing bound', () => {
  // ⚠ MEASURED DEFECT: a native `maxlength` simply stops accepting keystrokes. Nothing is said, nothing is
  // marked invalid, and a reader pasting a longer value keeps only its head - measured on the portal
  // creation form, where several boxes truncate in silence. The bound is therefore STATED, and stated where
  // it is heard before anything is typed.

  /** A rendered field with the given inputs. */
  function render(inputs: FieldInputs): ComponentFixture<FormFieldComponent> {
    const fixture = TestBed.createComponent(FormFieldComponent);

    applyInputs(fixture, inputs);

    return fixture;
  }

  /** The bound region, or null when the field declares no bound. */
  function limitRegion(fixture: ComponentFixture<FormFieldComponent>): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector('.form-field__limit');
  }

  /**
   * A field with a REAL control projected into it. The bound's association with the control can only be
   * asserted against a control that exists, and the bare component projects nothing.
   */
  function renderProjected(limit: number | null): ComponentFixture<FormFieldHostComponent> {
    const fixture = TestBed.createComponent(FormFieldHostComponent);

    fixture.componentInstance.limit = limit;
    fixture.detectChanges();

    return fixture;
  }

  /** The projected text box. */
  function projectedControl(fixture: ComponentFixture<FormFieldHostComponent>): HTMLInputElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('input[type="text"]');
  }

  it('states the bound in a region the control is described by, and hides it from view', () => {
    const fixture = render({ label: 'Title:', for: 'title', limit: 100 });
    const region = limitRegion(fixture);
    const group = (fixture.nativeElement as HTMLElement).querySelector('.form-field__control');

    expect(region).not.toBeNull();
    expect((region?.textContent ?? '').trim()).toBe('At most 100 characters.');
    expect(region?.hasAttribute('data-visually-hidden'))
      .withContext('announced, not drawn: the help disclosure is where a sighted reader reads it')
      .toBeTrue();
    expect(group?.getAttribute('aria-describedby')).toContain(String(region?.id));
  });

  it('repeats the bound inside the help disclosure, after whatever help was supplied', () => {
    const fixture = render({ label: 'Title:', for: 'title', help: 'Give your site a title.', limit: 100 });
    const host = fixture.nativeElement as HTMLElement;

    host.querySelector<HTMLButtonElement>('.form-field__help-toggle')?.click();
    fixture.detectChanges();

    expect((host.querySelector('.form-field__help')?.textContent ?? '').trim()).toBe(
      'Give your site a title. At most 100 characters.',
    );
  });

  it('offers the disclosure for a bound alone, so a bound is visible as well as announced', () => {
    const fixture = render({ label: 'Title:', for: 'title', limit: 50 });
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('.form-field__help-toggle')).not.toBeNull();
  });

  it('describes the PROJECTED CONTROL by the bound, not only the group that wraps it', () => {
    // ⚠ THIS IS THE SPECIFICATION A RUNTIME ACCESSIBILITY READ CAUGHT AND FOUR GREEN FILES DID NOT. The
    // region was rendered correctly and the GROUP referenced it correctly, and every assertion here used
    // to stop at the group - but an ARIA description on a composite group is not inherited by the control
    // inside it, so Chrome computed NO accessible description for the box being typed into. The bound was
    // announced on entering the field and was silent at the only moment it can still save a keystroke.
    const fixture = renderProjected(128);
    const host = fixture.nativeElement as HTMLElement;
    const control = projectedControl(fixture);
    const region = host.querySelector('.form-field__limit');

    expect(region).not.toBeNull();
    expect((region?.textContent ?? '').trim()).toBe('At most 128 characters.');
    expect(referenceList(control?.getAttribute('aria-describedby') ?? ''))
      .withContext('the control itself carries the reference, because descriptions are not inherited')
      .toContain(String(region?.id));
  });

  it('keeps the bound on the control alongside the help and the error, the failure first', () => {
    // Order is asserted because it is the order the three are SPOKEN in. The bound comes first: it is the
    // one that has to be heard before typing rather than after being refused.
    const fixture = renderProjected(50);

    fixture.componentInstance.help = 'Give your site a title.';
    fixture.componentInstance.error = 'Title is required.';
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;

    host.querySelector<HTMLButtonElement>('.form-field__help-toggle')?.click();
    fixture.detectChanges();

    const control = projectedControl(fixture);
    const base = String(control?.id);
    const references = referenceList(control?.getAttribute('aria-describedby') ?? '');

    // ⚠ The error reference is the CONTAINER, `<base>-error`, not the individual message element
    // `<base>-error-0`: a field can carry several messages and a description points at all of them.
    // ⚠ AND THE REASON LEADS — QA-21 / QA-24. The order used to be bound, help, error, so a reader
    // arriving at a refused control heard the typing bound and the whole help sentence before hearing why
    // the value had been rejected. The bound keeps its place ahead of the help, for the reason its own
    // note gives; what changed is that the failure now precedes both.
    expect(references).toEqual([`${base}-error`, `${base}-limit`, `${base}-help`]);
    expect(host.querySelector('.form-field__limit')?.id).toBe(`${base}-limit`);
    expect(host.querySelector('.form-field__help')?.id).toBe(`${base}-help`);
  });

  it('drops the reference from the control as soon as the bound is withdrawn', () => {
    const fixture = renderProjected(25);

    fixture.componentInstance.limit = null;
    fixture.detectChanges();

    const control = projectedControl(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('.form-field__limit')).toBeNull();
    expect(control?.getAttribute('aria-describedby'))
      .withContext('no dangling reference survives the bound it pointed at')
      .toBeNull();
  });

  it('states nothing at all, and adds no description, when there is no bound', () => {
    const fixture = render({ label: 'Title:', for: 'title' });
    const group = (fixture.nativeElement as HTMLElement).querySelector('.form-field__control');

    expect(limitRegion(fixture)).toBeNull();
    expect(group?.getAttribute('aria-describedby')).toBeNull();
  });

  it('refuses a bound that could not be a bound, rather than stating nonsense', () => {
    for (const unusable of [0, -10, Number.NaN, null]) {
      const fixture = render({ label: 'Title:', for: 'title', limit: unusable });

      expect(limitRegion(fixture))
        .withContext(`${String(unusable)} is not a typing bound`)
        .toBeNull();

      fixture.destroy();
    }
  });
});
