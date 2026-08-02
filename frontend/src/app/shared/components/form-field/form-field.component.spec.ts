import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { FormFieldComponent } from './form-field.component';

/**
 * A host that projects a real control, because the component's whole contract is about
 * content it does not own.
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
    </app-form-field>
  `,
})
class HostComponent {
  label = 'Portal Name';
  controlId = 'portal-name';
  required = false;
  help?: string;
  error?: string;
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

  function labelElement(): HTMLLabelElement {
    return fixture.debugElement.query(By.css('.form-field__label')).nativeElement as HTMLLabelElement;
  }

  function query(selector: string): HTMLElement | null {
    const node = fixture.debugElement.query(By.css(selector));

    return node === null ? null : (node.nativeElement as HTMLElement);
  }

  describe('the label', () => {
    it('renders the supplied text', () => {
      expect(labelElement().textContent!.trim()).toContain('Portal Name');
    });

    it('is a real label element, so clicking it focuses the control', () => {
      expect(labelElement().tagName).toBe('LABEL');
    });

    it('associates with the projected control by id', () => {
      // The association is the one thing the component cannot do for the consumer and
      // cannot verify, which is why the input is required.
      expect(labelElement().getAttribute('for')).toBe('portal-name');

      const input = query('input');
      expect(input!.getAttribute('id'))
        .withContext('the host projected a control carrying the same id')
        .toBe('portal-name');
    });

    it('follows a change of control id', () => {
      host.controlId = 'renamed';
      fixture.detectChanges();

      expect(labelElement().getAttribute('for')).toBe('renamed');
    });

    it('escapes markup in the label rather than rendering it', () => {
      host.label = '<b>Name</b>';
      fixture.detectChanges();

      expect(labelElement().querySelector('b')).toBeNull();
      expect(labelElement().textContent).toContain('<b>Name</b>');
    });
  });

  describe('the required marker', () => {
    it('is absent by default', () => {
      expect(query('.form-field__required')).toBeNull();
    });

    it('appears when the field is marked required', () => {
      host.required = true;
      fixture.detectChanges();

      expect(query('.form-field__required')!.textContent!.trim()).toBe('*');
    });

    it('is hidden from assistive technology, because the control announces its own required state', () => {
      host.required = true;
      fixture.detectChanges();

      expect(query('.form-field__required')!.getAttribute('aria-hidden'))
        .withContext('announcing it as well would say "required" twice')
        .toBe('true');
    });
  });

  describe('help text', () => {
    it('is absent when none is supplied', () => {
      expect(query('.form-field__help')).toBeNull();
    });

    it('renders beneath the control when supplied', () => {
      host.help = 'The name shown in the browser title bar.';
      fixture.detectChanges();

      expect(query('.form-field__help')!.textContent!.trim()).toBe(
        'The name shown in the browser title bar.',
      );
    });

    it('is treated as absent when blank, so no empty paragraph is rendered', () => {
      host.help = '   ';
      fixture.detectChanges();

      expect(query('.form-field__help')).toBeNull();
    });

    it('carries an id derived from the control id, so a consumer can describe the control with it', () => {
      host.help = 'Help.';
      fixture.detectChanges();

      expect(query('.form-field__help')!.getAttribute('id')).toBe('portal-name-help');
    });
  });

  describe('the validation message', () => {
    it('is absent while the field is valid', () => {
      expect(query('.form-field__error')).toBeNull();
    });

    it('renders when supplied', () => {
      host.error = 'Portal name is required.';
      fixture.detectChanges();

      expect(query('.form-field__error')!.textContent!.trim()).toBe('Portal name is required.');
    });

    it('is announced when it appears', () => {
      host.error = 'Portal name is required.';
      fixture.detectChanges();

      expect(query('.form-field__error')!.getAttribute('role'))
        .withContext('a validation failure must be discoverable without moving focus')
        .toBe('alert');
    });

    it('carries an id derived from the control id', () => {
      host.error = 'Required.';
      fixture.detectChanges();

      expect(query('.form-field__error')!.getAttribute('id')).toBe('portal-name-error');
    });

    it('is treated as absent when blank', () => {
      host.error = '  ';
      fixture.detectChanges();

      expect(query('.form-field__error')).toBeNull();
    });

    it('is removed rather than hidden once the field becomes valid', () => {
      host.error = 'Required.';
      fixture.detectChanges();
      expect(query('.form-field__error')).not.toBeNull();

      host.error = undefined;
      fixture.detectChanges();

      expect(query('.form-field__error'))
        .withContext('a hidden message would remain in the accessibility tree')
        .toBeNull();
    });
  });

  describe('projection', () => {
    it('renders the projected control inside the field', () => {
      // Rendering the control here would mean modelling every control type the
      // administration screens use and proxying the reactive-forms binding through it.
      expect(query('.form-field__control input')).not.toBeNull();
    });

    it('renders help and error alongside a projected control without replacing it', () => {
      host.help = 'Help.';
      host.error = 'Required.';
      fixture.detectChanges();

      expect(query('.form-field__control input')).not.toBeNull();
      expect(query('.form-field__help')).not.toBeNull();
      expect(query('.form-field__error')).not.toBeNull();
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (FormFieldComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });
});
