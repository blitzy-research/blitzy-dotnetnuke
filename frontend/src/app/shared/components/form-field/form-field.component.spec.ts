import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { FormFieldComponent } from './form-field.component';

/**
 * A host that projects real controls, because the component's whole contract is about
 * content it does not own.
 *
 * The legacy tree contains no automated tests of any kind, so nothing here is a ported
 * harness: every expectation below is written against the measured legacy behaviour of
 * `Website/controls/labelcontrol.ascx` and the in-scope administration screens.
 */
@Component({
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
      <input [attr.id]="controlId" type="text" />
      @if (secondControl) {
        <select aria-label="Frequency">
          <option value="M">Month</option>
        </select>
      }
    </app-form-field>
  `,
})
class HostComponent {
  label = 'Role Name';
  controlId = 'role-name';
  required = false;
  help = '';
  error: string | readonly string[] | null = null;
  secondControl = false;
}

describe('FormFieldComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  function query(selector: string): HTMLElement | null {
    const node = fixture.debugElement.query(By.css(selector));

    return node === null ? null : (node.nativeElement as HTMLElement);
  }

  function queryAll(selector: string): readonly HTMLElement[] {
    return fixture.debugElement
      .queryAll(By.css(selector))
      .map((node) => node.nativeElement as HTMLElement);
  }

  function required(selector: string): HTMLElement {
    const element = query(selector);

    if (element === null) {
      throw new Error(`expected ${selector} to be rendered`);
    }

    return element;
  }

  function text(selector: string): string {
    return (required(selector).textContent ?? '').trim();
  }

  describe('the label', () => {
    it('renders the supplied text', () => {
      expect(text('.form-field__label')).toContain('Role Name');
    });

    it('is a real label element, so clicking it focuses the control', () => {
      expect(required('.form-field__label').tagName).toBe('LABEL');
    });

    it('associates with the projected control by id', () => {
      expect(required('.form-field__label').getAttribute('for')).toBe('role-name');
      expect(required('input').getAttribute('id'))
        .withContext('the host projected a control carrying the same id')
        .toBe('role-name');
    });

    it('follows a change of control id', () => {
      host.controlId = 'renamed';
      fixture.detectChanges();

      expect(required('.form-field__label').getAttribute('for')).toBe('renamed');
    });

    it('omits the for attribute entirely when no control id is supplied', () => {
      // Measured: 5 of the 186 legacy labels declare no `controlname` at all, and
      // `for=""` would be a reference that names nothing.
      host.controlId = '';
      fixture.detectChanges();

      expect(required('.form-field__label').hasAttribute('for')).toBeFalse();
    });

    it('escapes markup in the label rather than rendering it', () => {
      host.label = '<b>Name</b>';
      fixture.detectChanges();

      expect(required('.form-field__label').querySelector('b')).toBeNull();
      expect(text('.form-field__label')).toContain('<b>Name</b>');
    });

    it('strips one trailing colon, whatever its origin', () => {
      // `plRSVPCode.Text` is 'RSVP Code:' in the resource file and `plIcon` declares
      // `Text="Icon:"` in markup; 72% of legacy labels carry no punctuation at all,
      // so the component owns it and applies none.
      host.label = 'RSVP Code:';
      fixture.detectChanges();

      expect(text('.form-field__label')).toBe('RSVP Code');
    });

    it('preserves a question mark and parentheses, which carry meaning', () => {
      host.label = 'Public Role?';
      fixture.detectChanges();
      expect(text('.form-field__label')).toBe('Public Role?');

      host.label = 'Billing Period (Every)';
      fixture.detectChanges();
      expect(text('.form-field__label')).toBe('Billing Period (Every)');
    });

    it('renders unconditionally, as the legacy control did, even with no text', () => {
      host.label = '';
      fixture.detectChanges();

      expect(query('.form-field__label')).not.toBeNull();
    });
  });

  describe('the required marker', () => {
    it('is absent by default', () => {
      expect(query('.form-field__required')).toBeNull();
    });

    it('shows a glyph and a hidden word, so colour alone never carries the meaning', () => {
      host.required = true;
      fixture.detectChanges();

      expect(text('.form-field__required')).toContain('*');
      expect(text('.form-field__required-text')).toBe('required');
    });

    it('hides the glyph from assistive technology so it is not announced as punctuation', () => {
      host.required = true;
      fixture.detectChanges();

      expect(text('.form-field__required [aria-hidden="true"]')).toBe('*');
    });
  });

  describe('the projection group', () => {
    it('accepts more than one control, because a legacy field routinely held several', () => {
      host.secondControl = true;
      fixture.detectChanges();

      expect(query('.form-field__control input')).not.toBeNull();
      expect(query('.form-field__control select')).not.toBeNull();
    });

    it('is a group named from the label, so every control in it inherits the field name', () => {
      const group = required('.form-field__control');

      expect(group.getAttribute('role')).toBe('group');
      expect(group.getAttribute('aria-labelledby')).toBe(
        required('.form-field__label').getAttribute('id'),
      );
    });

    it('is not named when there is no label text to name it with', () => {
      host.label = '';
      fixture.detectChanges();

      expect(required('.form-field__control').hasAttribute('aria-labelledby')).toBeFalse();
    });

    it('describes nothing while there is no help or error region', () => {
      expect(required('.form-field__control').hasAttribute('aria-describedby')).toBeFalse();
    });
  });

  describe('help text', () => {
    it('renders no affordance when none is supplied', () => {
      expect(query('.form-field__help-toggle')).toBeNull();
      expect(query('.form-field__help')).toBeNull();
    });

    it('is treated as absent when blank, so no affordance appears', () => {
      host.help = '   ';
      fixture.detectChanges();

      expect(query('.form-field__help-toggle')).toBeNull();
    });

    it('offers a keyboard-reachable button, reversing the legacy tab-index removal', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      const toggle = required('.form-field__help-toggle');

      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle.getAttribute('type'))
        .withContext('the legacy affordance declared CausesValidation="False"')
        .toBe('button');
      expect(toggle.hasAttribute('tabindex'))
        .withContext('labelcontrol.ascx set tabindex="-1" on both the link and its image')
        .toBeFalse();
    });

    it('is a sibling of the label, never a descendant of it', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      expect(required('.form-field__label').querySelector('.form-field__help-toggle')).toBeNull();
    });

    it('starts collapsed and reveals the text when the affordance is activated', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      const toggle = required('.form-field__help-toggle');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(query('.form-field__help')).toBeNull();

      toggle.click();
      fixture.detectChanges();

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(text('.form-field__help')).toBe('Enter the name of the role.');
    });

    it('collapses again on a second activation', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      const toggle = required('.form-field__help-toggle');
      toggle.click();
      fixture.detectChanges();
      toggle.click();
      fixture.detectChanges();

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(query('.form-field__help')).toBeNull();
    });

    it('names the affordance from its own text and the field label together', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      const references = (
        required('.form-field__help-toggle').getAttribute('aria-labelledby') ?? ''
      ).split(' ');

      expect(references.length).toBe(2);
      expect(references[1]).toBe(required('.form-field__label').getAttribute('id') ?? '');
      expect((document.getElementById(references[0])?.textContent ?? '').trim()).toBe('Help');
    });

    it('references the revealed region only while it exists', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();

      const toggle = required('.form-field__help-toggle');
      expect(toggle.hasAttribute('aria-controls')).toBeFalse();

      toggle.click();
      fixture.detectChanges();

      expect(toggle.getAttribute('aria-controls')).toBe(
        required('.form-field__help').getAttribute('id'),
      );
    });

    it('describes the group once revealed', () => {
      host.help = 'Enter the name of the role.';
      fixture.detectChanges();
      required('.form-field__help-toggle').click();
      fixture.detectChanges();

      expect(required('.form-field__control').getAttribute('aria-describedby')).toBe(
        required('.form-field__help').getAttribute('id'),
      );
    });

    it('carries an id derived from the control id', () => {
      host.help = 'Help.';
      fixture.detectChanges();
      required('.form-field__help-toggle').click();
      fixture.detectChanges();

      expect(required('.form-field__help').getAttribute('id')).toBe('role-name-help');
    });

    it('shows a leading break tag as nothing rather than as characters', () => {
      host.help = '<br>These two fields are used in conjunction.';
      fixture.detectChanges();
      required('.form-field__help-toggle').click();
      fixture.detectChanges();

      expect(text('.form-field__help')).toBe('These two fields are used in conjunction.');
    });
  });

  describe('the validation messages', () => {
    it('are absent while the field is valid', () => {
      expect(query('.form-field__errors')).toBeNull();
    });

    it('render a single supplied message', () => {
      host.error = 'You Must Enter a Valid Name';
      fixture.detectChanges();

      expect(queryAll('.form-field__error').length).toBe(1);
      expect(text('.form-field__error')).toBe('You Must Enter a Valid Name');
    });

    it('render every message when several are outstanding at once', () => {
      // editroles.ascx puts two CompareValidators on one control four separate times,
      // and there is no ValidationSummary anywhere in scope to catch the remainder.
      host.error = [
        'Billing Period Value Entered Is Not Valid',
        'Billing Period Must Be Greater Than Zero',
      ];
      fixture.detectChanges();

      const messages = queryAll('.form-field__error').map((element) =>
        (element.textContent ?? '').trim(),
      );

      expect(messages).toEqual([
        'Billing Period Value Entered Is Not Valid',
        'Billing Period Must Be Greater Than Zero',
      ]);
    });

    it('render two identical messages without failing on a duplicate key', () => {
      host.error = ['Invalid character.', 'Invalid character.'];
      fixture.detectChanges();

      expect(queryAll('.form-field__error').length).toBe(2);
    });

    it('drop the legacy leading break tag from every message', () => {
      host.error = ['<br>You Must Enter a Valid Name', '<BR />Service Fee Is Not Valid'];
      fixture.detectChanges();

      const messages = queryAll('.form-field__error').map((element) =>
        (element.textContent ?? '').trim(),
      );

      expect(messages).toEqual([
        'You Must Enter a Valid Name',
        'Service Fee Is Not Valid',
      ]);
    });

    it('escape markup rather than rendering it', () => {
      host.error = '<b>Warning:</b> not valid';
      fixture.detectChanges();

      expect(required('.form-field__error').querySelector('b')).toBeNull();
      expect(text('.form-field__error')).toBe('<b>Warning:</b> not valid');
    });

    it('drop blank entries instead of rendering an empty message', () => {
      host.error = ['   ', 'Only this one'];
      fixture.detectChanges();

      expect(queryAll('.form-field__error').length).toBe(1);
      expect(text('.form-field__error')).toBe('Only this one');
    });

    it('are absent when the only supplied message is blank', () => {
      host.error = '  ';
      fixture.detectChanges();

      expect(query('.form-field__errors')).toBeNull();
    });

    it('are announced as one interruption, on the region rather than each message', () => {
      host.error = ['One', 'Two'];
      fixture.detectChanges();

      expect(required('.form-field__errors').getAttribute('role'))
        .withContext('a failure must be discoverable without moving focus')
        .toBe('alert');
      expect(queryAll('.form-field__error').every((element) => element.hasAttribute('role')))
        .withContext('a role on each message would announce the same failure twice')
        .toBeFalse();
    });

    it('carry a region id derived from the control id and describe the group with it', () => {
      host.error = 'Required.';
      fixture.detectChanges();

      expect(required('.form-field__errors').getAttribute('id')).toBe('role-name-error');
      expect(required('.form-field__control').getAttribute('aria-describedby')).toBe(
        'role-name-error',
      );
    });

    it('describe the group with the help region first and the error region second', () => {
      host.help = 'Help.';
      host.error = 'Required.';
      fixture.detectChanges();
      required('.form-field__help-toggle').click();
      fixture.detectChanges();

      expect(required('.form-field__control').getAttribute('aria-describedby')).toBe(
        'role-name-help role-name-error',
      );
    });

    it('are removed rather than hidden once the field becomes valid', () => {
      host.error = 'Required.';
      fixture.detectChanges();
      expect(query('.form-field__errors')).not.toBeNull();

      host.error = null;
      fixture.detectChanges();

      expect(query('.form-field__errors'))
        .withContext('a hidden message would remain in the accessibility tree')
        .toBeNull();
    });
  });

  describe('identifiers with no control association', () => {
    it('fall back to a per-instance stem rather than colliding or dangling', () => {
      host.controlId = '';
      host.help = 'Help.';
      host.error = 'Required.';
      fixture.detectChanges();
      required('.form-field__help-toggle').click();
      fixture.detectChanges();

      const helpId = required('.form-field__help').getAttribute('id') ?? '';
      const errorId = required('.form-field__errors').getAttribute('id') ?? '';

      // The numeric part is deliberately not asserted: it is an implementation
      // detail of a module-scoped counter that no consumer reads.
      expect(helpId).toMatch(/^app-form-field-\d+-help$/);
      expect(errorId).toMatch(/^app-form-field-\d+-error$/);
      expect(required('.form-field__control').getAttribute('aria-describedby')).toBe(
        `${helpId} ${errorId}`,
      );
    });
  });

  describe('the public wiring surface', () => {
    function component(): FormFieldComponent {
      return fixture.debugElement.query(By.directive(FormFieldComponent))
        .componentInstance as FormFieldComponent;
    }

    it('exposes the region ids so a caller can describe one projected control itself', () => {
      expect(component().labelId()).toBe('role-name-label');
      expect(component().helpId()).toBe('role-name-help');
      expect(component().errorId()).toBe('role-name-error');
    });

    it('reads back the text inputs it was given', () => {
      host.label = 'Service Fee:';
      host.help = 'Enter the fee charged for users in this role.';
      host.controlId = ' service-fee ';
      fixture.detectChanges();

      // The label reads back as supplied - punctuation is resolved for DISPLAY, not
      // on the way in, so nothing a caller passed is lost.
      expect(component().label).toBe('Service Fee:');
      expect(component().help).toBe('Enter the fee charged for users in this role.');
      // The control id is trimmed, because whitespace around it could never match an
      // element's id and would render a reference that resolves to nothing.
      expect(component().for).toBe('service-fee');
    });

    it('treats a value that arrives untyped as absent rather than rendering it', () => {
      // Reachable only from a path that bypasses the type system - a body parsed from
      // JSON and typed optimistically. The requirement is that no reader is ever shown
      // the word "null" or "undefined" as though it were content.
      const untyped = null as unknown as string;

      host.label = untyped;
      host.help = untyped;
      host.controlId = untyped;
      fixture.detectChanges();

      expect(text('.form-field__label')).toBe('');
      expect(query('.form-field__help-toggle')).toBeNull();
      expect(required('.form-field__label').hasAttribute('for')).toBeFalse();
    });

    it('names the help affordance from its own text alone when the field has no label', () => {
      host.label = '';
      host.help = 'Help.';
      fixture.detectChanges();

      const references = (
        required('.form-field__help-toggle').getAttribute('aria-labelledby') ?? ''
      ).split(' ');

      expect(references.length)
        .withContext('there is no label element to compose a name with')
        .toBe(1);
      expect(text('.form-field__help-toggle-text')).toBe('Help');
    });

    it('renders no error region when every supplied message is blank', () => {
      host.error = ['   ', ''];
      fixture.detectChanges();

      expect(query('.form-field__errors')).toBeNull();
      expect(required('.form-field__control').hasAttribute('aria-describedby')).toBeFalse();
    });

    it('reads back the messages in the one canonical shape, whatever was written', () => {
      host.error = '<br>Required.';
      fixture.detectChanges();
      expect(component().error).toEqual(['Required.']);

      host.error = null;
      fixture.detectChanges();
      expect(component().error).toEqual([]);
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (FormFieldComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });
});
