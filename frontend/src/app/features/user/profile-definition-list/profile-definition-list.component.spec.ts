import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type ProfilePropertyDefinitionSubmission,
} from '../../../core/models/profile.model';
import {
  ProfileDefinitionListComponent,
  type ProfileDefinitionBulkFlag,
  type ProfileDefinitionReorder,
  type ProfileDefinitionUpdate,
} from './profile-definition-list.component';

/**
 * Builds a declaration, defaulted so each expectation states only what it depends on.
 *
 * @param overrides The members to replace.
 * @returns A declaration.
 */
function defn(overrides: Partial<ProfilePropertyDefinition> = {}): ProfilePropertyDefinition {
  return {
    propertyDefinitionId: 1,
    portalId: 0,
    moduleDefId: null,
    dataType: 349,
    defaultValue: null,
    propertyCategory: 'Name',
    propertyName: 'First Name',
    length: 50,
    required: false,
    validationExpression: null,
    viewOrder: 0,
    visible: true,
    visibility: PROFILE_VISIBILITY.adminOnly,
    ...overrides,
  };
}

describe('ProfileDefinitionListComponent', () => {
  let fixture: ComponentFixture<ProfileDefinitionListComponent>;
  let component: ProfileDefinitionListComponent;
  let created: ProfilePropertyDefinitionSubmission[];
  let updated: ProfileDefinitionUpdate[];
  let removed: ProfilePropertyDefinition[];
  let reordered: ProfileDefinitionReorder[];
  let flagged: ProfileDefinitionBulkFlag[];

  /**
   * Sets one of the component's inputs and re-renders.
   *
   * @param name The input to set.
   * @param value The value to set.
   */
  function setInput(name: 'definitions' | 'heading' | 'loading' | 'saving', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Returns the component's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Returns every rendered body row.
   */
  function rows(): HTMLTableRowElement[] {
    return Array.from(host().querySelectorAll<HTMLTableRowElement>('tbody tr'));
  }

  /**
   * Returns the four row-action controls of one row.
   *
   * @param index The row's position.
   */
  function rowActions(index: number): HTMLButtonElement[] {
    return Array.from(
      rows()[index].querySelectorAll<HTMLButtonElement>('button.profile-definitions__row-action'),
    );
  }

  /**
   * Returns the bulk flag checkboxes, required first.
   */
  function bulkBoxes(): HTMLInputElement[] {
    return Array.from(
      host().querySelectorAll<HTMLInputElement>('.profile-definitions__bulk input[type="checkbox"]'),
    );
  }

  /**
   * Returns the editor disclosure control.
   */
  function disclosure(): HTMLButtonElement | null {
    return host().querySelector('button.profile-definitions__form-toggle');
  }

  /**
   * Returns the grid-level create control.
   */
  function addButton(): HTMLButtonElement | null {
    return host().querySelector('.profile-definitions__grid-actions button');
  }

  /**
   * Returns the inline editor, when it is open.
   */
  function editor(): HTMLElement | null {
    return host().querySelector('fieldset.profile-definitions__form');
  }

  /**
   * Returns one of the editor's controls.
   *
   * @param id The control's identifier.
   */
  function field<T extends HTMLElement>(id: string): T | null {
    return host().querySelector<T>(`#${id}`);
  }

  /**
   * Sets a text control's value the way a user would.
   *
   * @param element The control.
   * @param next The text to enter.
   */
  function type(element: HTMLInputElement, next: string): void {
    element.value = next;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Submits the inline editor.
   */
  function submit(): void {
    host().querySelector('form')?.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  /**
   * Returns the confirmation dialog, when one is open.
   */
  function dialog(): HTMLElement | null {
    return host().querySelector('app-confirm-dialog');
  }

  /**
   * Returns the dialog's two controls, cancel first.
   */
  function dialogButtons(): HTMLButtonElement[] {
    return Array.from(
      host().querySelectorAll<HTMLButtonElement>('button.confirm-dialog__button'),
    );
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProfileDefinitionListComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ProfileDefinitionListComponent);
    component = fixture.componentInstance;

    created = [];
    updated = [];
    removed = [];
    reordered = [];
    flagged = [];
    component.create.subscribe((value) => created.push(value));
    component.update.subscribe((value) => updated.push(value));
    component.remove.subscribe((value) => removed.push(value));
    component.reorder.subscribe((value) => reordered.push(value));
    component.bulkFlag.subscribe((value) => flagged.push(value));

    fixture.detectChanges();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const meta = (ProfileDefinitionListComponent as unknown as { ɵcmp?: { onPush?: boolean } })
        .ɵcmp;

      expect(meta?.onPush).toBeTrue();
    });

    it('is standalone, so the route can load it directly', () => {
      const meta = (
        ProfileDefinitionListComponent as unknown as { ɵcmp?: { standalone?: boolean } }
      ).ɵcmp;

      expect(meta?.standalone).toBeTrue();
    });

    it('renders the shared page header with a default title', () => {
      expect(host().querySelector('app-page-header h1')?.textContent?.trim()).toBe(
        'Profile Properties',
      );
    });

    it('restores safe defaults when route input binding supplies no data', () => {
      expect(() => {
        setInput('definitions', undefined);
        setInput('heading', undefined);
      }).not.toThrow();

      expect(component.definitions).toEqual([]);
      expect(host().querySelector('app-page-header h1')?.textContent?.trim()).toBe(
        'Profile Properties',
      );
      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('opens with the help paragraph, which the stylesheet owns', () => {
      expect(host().querySelector('p.profile-definitions__help')).not.toBeNull();
    });

    it('opens with the editor closed', () => {
      expect(editor()).toBeNull();
      expect(disclosure()?.getAttribute('aria-expanded')).toBe('false');
    });
  });

  describe('while the catalogue is being fetched', () => {
    beforeEach(() => {
      setInput('loading', true);
    });

    it('renders the shared progress indicator', () => {
      expect(host().querySelector('app-loading-spinner')).not.toBeNull();
    });

    it('renders neither the table nor the editor', () => {
      expect(host().querySelector('table')).toBeNull();
      expect(disclosure()).toBeNull();
    });
  });

  describe('when the catalogue is empty', () => {
    it('renders the empty state', () => {
      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('renders no table and no bulk region', () => {
      expect(host().querySelector('table')).toBeNull();
      expect(host().querySelector('.profile-definitions__bulk')).toBeNull();
    });

    it('still offers the create affordance, because an empty catalogue is where one starts', () => {
      expect(addButton()).not.toBeNull();
      expect(disclosure()).not.toBeNull();
    });
  });

  describe('the catalogue table', () => {
    beforeEach(() => {
      setInput('definitions', [
        defn({
          propertyDefinitionId: 1,
          propertyName: 'First Name',
          propertyCategory: 'Name',
          length: 50,
          required: true,
          visible: true,
          visibility: PROFILE_VISIBILITY.allUsers,
          defaultValue: 'Unknown',
          validationExpression: '^[A-Za-z]+$',
        }),
        defn({
          propertyDefinitionId: 2,
          propertyName: 'Street',
          propertyCategory: 'Address',
          length: 0,
          required: false,
          visible: false,
          visibility: PROFILE_VISIBILITY.adminOnly,
        }),
      ]);
    });

    it('names the table for assistive technology without showing the caption', () => {
      const caption = host().querySelector('caption');

      expect(caption).not.toBeNull();
      expect(caption?.hasAttribute('data-visually-hidden')).toBeTrue();
    });

    it('renders one row per declaration, in the order supplied', () => {
      expect(rows().length).toBe(2);
      expect(rows().map((row) => row.querySelector('th')?.textContent?.trim())).toEqual([
        'First Name',
        'Street',
      ]);
    });

    it('makes each name a row-scoped header, so every cell is announced with it', () => {
      expect(rows()[0].querySelector('th')?.getAttribute('scope')).toBe('row');
    });

    it('declares every column header with a column scope', () => {
      const headers = Array.from(host().querySelectorAll('thead th'));

      expect(headers.length).toBe(9);
      expect(headers.every((header) => header.getAttribute('scope') === 'col')).toBeTrue();
    });

    it('renders the two flags as words rather than as raw booleans', () => {
      const cells = Array.from(rows()[0].querySelectorAll('td')).map((cell) =>
        cell.textContent?.trim(),
      );

      expect(cells[2]).toBe('Yes');
      expect(cells[3]).toBe('Yes');
    });

    it('renders a cleared flag as the negative word, not as a blank cell', () => {
      const cells = Array.from(rows()[1].querySelectorAll('td')).map((cell) =>
        cell.textContent?.trim(),
      );

      expect(cells[2]).toBe('No');
      expect(cells[3]).toBe('No');
    });

    it('renders the visibility as wording rather than as its stored code', () => {
      expect(rows()[0].querySelectorAll('td')[4].textContent?.trim()).toBe('All users');
      expect(rows()[1].querySelectorAll('td')[4].textContent?.trim()).toBe(
        'Administrators only',
      );
    });

    it('reports an unrecognised visibility code as itself rather than guessing', () => {
      setInput('definitions', [defn({ visibility: 97 })]);

      expect(rows()[0].querySelectorAll('td')[4].textContent?.trim()).toBe('Code 97');
    });

    it('presents a default value and an expression in the monospaced style', () => {
      const codes = Array.from(rows()[0].querySelectorAll('code.profile-definitions__code'));

      expect(codes.map((code) => code.textContent?.trim())).toEqual([
        'Unknown',
        '^[A-Za-z]+$',
      ]);
    });

    it('renders no monospaced element when neither a default nor an expression exists', () => {
      expect(rows()[1].querySelectorAll('code.profile-definitions__code').length).toBe(0);
    });
  });

  describe('bulk flags', () => {
    it('shows both flags as set when every declaration carries them', () => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 1, required: true, visible: true }),
        defn({ propertyDefinitionId: 2, required: true, visible: true }),
      ]);

      expect(bulkBoxes().map((box) => box.checked)).toEqual([true, true]);
      expect(bulkBoxes().map((box) => box.indeterminate)).toEqual([false, false]);
    });

    it('shows both flags as clear when no declaration carries them', () => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 1, required: false, visible: false }),
        defn({ propertyDefinitionId: 2, required: false, visible: false }),
      ]);

      expect(bulkBoxes().map((box) => box.checked)).toEqual([false, false]);
      expect(bulkBoxes().map((box) => box.indeterminate)).toEqual([false, false]);
    });

    it('shows a mixed flag as neither set nor clear', () => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 1, required: true, visible: true }),
        defn({ propertyDefinitionId: 2, required: false, visible: true }),
      ]);

      // A mixed set is not "off": presenting it as off would invite an operator to
      // switch it on and believe nothing changed for half the catalogue.
      expect(bulkBoxes()[0].indeterminate).toBeTrue();
      expect(bulkBoxes()[0].checked).toBeFalse();
      expect(bulkBoxes()[1].indeterminate).toBeFalse();
      expect(bulkBoxes()[1].checked).toBeTrue();
    });

    it('emits which flag was set and what it was set to', () => {
      setInput('definitions', [defn({ required: false })]);

      const box = bulkBoxes()[0];
      box.checked = true;
      box.dispatchEvent(new Event('change'));

      expect(flagged).toEqual([{ flag: 'required', value: true }]);
    });

    it('emits a clearing gesture as well as a setting one', () => {
      setInput('definitions', [defn({ visible: true })]);

      const box = bulkBoxes()[1];
      box.checked = false;
      box.dispatchEvent(new Event('change'));

      expect(flagged).toEqual([{ flag: 'visible', value: false }]);
    });

    it('is disabled while a mutation is in flight', () => {
      setInput('definitions', [defn()]);
      setInput('saving', true);

      expect(bulkBoxes().every((box) => box.disabled)).toBeTrue();
    });

    it('associates each checkbox with a label, so the pair is one control', () => {
      setInput('definitions', [defn()]);

      const labels = Array.from(
        host().querySelectorAll('.profile-definitions__bulk label.profile-definitions__bulk-item'),
      );

      expect(labels.length).toBe(2);
      expect(labels.every((label) => label.querySelector('input[type="checkbox"]') !== null)).toBeTrue();
    });
  });

  describe('row actions', () => {
    beforeEach(() => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 1, propertyName: 'First Name' }),
        defn({ propertyDefinitionId: 2, propertyName: 'Street' }),
        defn({ propertyDefinitionId: 3, propertyName: 'City' }),
      ]);
    });

    it('renders the four affordances the legacy grid offered', () => {
      expect(rowActions(0).length).toBe(4);
    });

    it('cannot move the first declaration earlier', () => {
      expect(rowActions(0)[2].disabled).toBeTrue();
      expect(rowActions(0)[3].disabled).toBeFalse();
    });

    it('cannot move the last declaration later', () => {
      expect(rowActions(2)[2].disabled).toBeFalse();
      expect(rowActions(2)[3].disabled).toBeTrue();
    });

    it('can move a middle declaration either way', () => {
      expect(rowActions(1)[2].disabled).toBeFalse();
      expect(rowActions(1)[3].disabled).toBeFalse();
    });

    it('names each direction affordance by the declaration it moves', () => {
      expect(rowActions(1)[2].getAttribute('aria-label')).toBe('Move Street up');
      expect(rowActions(1)[3].getAttribute('aria-label')).toBe('Move Street down');
    });

    it('hides the direction glyph from assistive technology, so the name is not doubled', () => {
      expect(rowActions(1)[2].querySelector('span')?.getAttribute('aria-hidden')).toBe('true');
    });

    it('emits the declaration and the direction', () => {
      rowActions(1)[2].click();
      rowActions(1)[3].click();

      expect(reordered).toEqual([
        { propertyDefinitionId: 2, direction: 'up' },
        { propertyDefinitionId: 2, direction: 'down' },
      ]);
    });

    it('refuses a move past the boundary even when asked directly', () => {
      // The disabled attribute stops a click; the guard stops a synthetic or
      // programmatic call, so the boundary is a property of the component rather than
      // of its markup.
      const internals = component as unknown as {
        onReorder(definition: ProfilePropertyDefinition, direction: 'up' | 'down'): void;
      };

      internals.onReorder(defn({ propertyDefinitionId: 1 }), 'up');
      internals.onReorder(defn({ propertyDefinitionId: 3 }), 'down');

      expect(reordered).toEqual([]);
    });

    it('refuses a move for a declaration the screen does not hold', () => {
      const internals = component as unknown as {
        onReorder(definition: ProfilePropertyDefinition, direction: 'up' | 'down'): void;
      };

      internals.onReorder(defn({ propertyDefinitionId: 99 }), 'up');
      internals.onReorder(defn({ propertyDefinitionId: 99 }), 'down');

      expect(reordered).toEqual([]);
    });

    it('disables every affordance while a mutation is in flight', () => {
      setInput('saving', true);

      expect(rowActions(1).every((button) => button.disabled)).toBeTrue();
    });
  });

  describe('creating a declaration', () => {
    beforeEach(() => {
      setInput('definitions', [defn({ propertyDefinitionId: 1 }), defn({ propertyDefinitionId: 2 })]);
      addButton()!.click();
      fixture.detectChanges();
    });

    it('opens the editor', () => {
      expect(editor()).not.toBeNull();
      expect(disclosure()?.getAttribute('aria-expanded')).toBe('true');
    });

    it('legends the editor as an addition', () => {
      expect(
        host().querySelector('legend.profile-definitions__form-legend')?.textContent?.trim(),
      ).toBe('Add a profile property');
    });

    it('starts the display order one past the current last, so nothing collides', () => {
      expect(field<HTMLInputElement>('definition-order')?.value).toBe('2');
    });

    it('starts visible and not required, matching the shipped defaults', () => {
      expect(field<HTMLInputElement>('definition-visible')).toBeNull();
      const checkboxes = Array.from(
        editor()!.querySelectorAll<HTMLInputElement>('.profile-definitions__form-grid input[type="checkbox"]'),
      );

      expect(checkboxes.map((box) => box.checked)).toEqual([false, true]);
    });

    it('labels the submit affordance as an addition', () => {
      expect(host().querySelector('button[type="submit"]')?.textContent?.trim()).toBe('Add');
    });

    it('emits a creation carrying the entered shape', () => {
      type(field<HTMLInputElement>('definition-name')!, '  Nickname  ');
      type(field<HTMLInputElement>('definition-category')!, '  Name  ');
      submit();

      expect(created.length).toBe(1);
      expect(updated).toEqual([]);
      // Trimmed: a name with stray white space would collide with itself on the next
      // duplicate check and would render with a visible gap.
      expect(created[0].propertyName).toBe('Nickname');
      expect(created[0].propertyCategory).toBe('Name');
    });

    it('reports an absent default and an absent expression as nothing, not as blank text', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Nickname');
      submit();

      expect(created[0].defaultValue).toBeNull();
      expect(created[0].validationExpression).toBeNull();
    });

    it('carries a supplied default and expression verbatim', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Telephone');
      type(field<HTMLInputElement>('definition-default')!, '000');
      type(field<HTMLInputElement>('definition-validation')!, '^[0-9]+$');
      submit();

      expect(created[0].defaultValue).toBe('000');
      expect(created[0].validationExpression).toBe('^[0-9]+$');
    });

    it('carries the numeric fields as numbers rather than as text', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Nickname');
      type(field<HTMLInputElement>('definition-length')!, '120');
      submit();

      expect(created[0].length).toBe(120);
      expect(typeof created[0].length).toBe('number');
    });

    it('carries the chosen visibility as a number', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Nickname');

      const select = field<HTMLSelectElement>('definition-visibility')!;
      select.selectedIndex = PROFILE_VISIBILITY.membersOnly;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      submit();

      expect(created[0].visibility).toBe(PROFILE_VISIBILITY.membersOnly);
      expect(typeof created[0].visibility).toBe('number');
    });
  });

  describe('editing a declaration', () => {
    beforeEach(() => {
      setInput('definitions', [
        defn({
          propertyDefinitionId: 5,
          propertyName: 'Street',
          propertyCategory: 'Address',
          length: 120,
          required: true,
          visible: false,
          viewOrder: 3,
          defaultValue: 'Unknown',
          validationExpression: '^.+$',
          visibility: PROFILE_VISIBILITY.membersOnly,
        }),
      ]);
      rowActions(0)[0].click();
      fixture.detectChanges();
    });

    it('opens the editor legended as an amendment', () => {
      expect(
        host().querySelector('legend.profile-definitions__form-legend')?.textContent?.trim(),
      ).toBe('Edit profile property');
    });

    it('populates every field from the declaration', () => {
      expect(field<HTMLInputElement>('definition-name')?.value).toBe('Street');
      expect(field<HTMLInputElement>('definition-category')?.value).toBe('Address');
      expect(field<HTMLInputElement>('definition-length')?.value).toBe('120');
      expect(field<HTMLInputElement>('definition-order')?.value).toBe('3');
      expect(field<HTMLInputElement>('definition-default')?.value).toBe('Unknown');
      expect(field<HTMLInputElement>('definition-validation')?.value).toBe('^.+$');
      expect(field<HTMLSelectElement>('definition-visibility')?.selectedIndex).toBe(
        PROFILE_VISIBILITY.membersOnly,
      );
    });

    it('populates a null default and a null expression as blank controls', () => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 6, defaultValue: null, validationExpression: null }),
      ]);
      rowActions(0)[0].click();
      fixture.detectChanges();

      expect(field<HTMLInputElement>('definition-default')?.value).toBe('');
      expect(field<HTMLInputElement>('definition-validation')?.value).toBe('');
    });

    it('labels the submit affordance as an amendment', () => {
      expect(host().querySelector('button[type="submit"]')?.textContent?.trim()).toBe('Update');
    });

    it('emits an amendment carrying the declaration identifier', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Street Address');
      submit();

      expect(created).toEqual([]);
      expect(updated.length).toBe(1);
      expect(updated[0].propertyDefinitionId).toBe(5);
      expect(updated[0].submission.propertyName).toBe('Street Address');
    });

    it('decides create-or-amend from how the editor was opened, not from the payload', () => {
      // Closing and reopening through the disclosure control must start a NEW property
      // even though the previous target is still in the catalogue.
      disclosure()!.click();
      fixture.detectChanges();
      disclosure()!.click();
      fixture.detectChanges();

      type(field<HTMLInputElement>('definition-name')!, 'Something New');
      submit();

      expect(updated).toEqual([]);
      expect(created.length).toBe(1);
    });

    it('keeps the editor open when the catalogue refreshes with the same declaration', () => {
      setInput('definitions', [defn({ propertyDefinitionId: 5, propertyName: 'Street (revised)' })]);

      expect(editor()).not.toBeNull();
      expect(
        host().querySelector('legend.profile-definitions__form-legend')?.textContent?.trim(),
      ).toBe('Edit profile property');
    });

    it('closes the editor when the declaration it was editing disappears', () => {
      // Leaving the editor open on a row that no longer exists would let an amendment be
      // applied to something that has been deleted by another operator.
      setInput('definitions', [defn({ propertyDefinitionId: 7 })]);

      expect(editor()).toBeNull();
      expect(disclosure()?.getAttribute('aria-expanded')).toBe('false');
    });
  });

  describe('editor validation', () => {
    beforeEach(() => {
      setInput('definitions', []);
      addButton()!.click();
      fixture.detectChanges();
    });

    it('shows no message before the operator has touched the name', () => {
      expect(host().querySelector('#definition-name-message')).toBeNull();
    });

    it('marks the name as required in the label and on the control', () => {
      expect(editor()?.querySelector('.form-required')?.getAttribute('aria-hidden')).toBe('true');
      expect(field<HTMLInputElement>('definition-name')?.getAttribute('aria-required')).toBe(
        'true',
      );
    });

    it('reports a blank name once the operator has cleared it', () => {
      type(field<HTMLInputElement>('definition-name')!, 'x');
      type(field<HTMLInputElement>('definition-name')!, '');

      expect(host().querySelector('#definition-name-message')?.textContent?.trim()).toBe(
        'A property name is required.',
      );
      expect(field<HTMLInputElement>('definition-name')?.getAttribute('aria-invalid')).toBe('true');
    });

    it('emits nothing and reveals the message when an invalid editor is submitted', () => {
      submit();

      expect(created).toEqual([]);
      expect(host().querySelector('#definition-name-message')).not.toBeNull();
    });

    it('emits nothing while a mutation is already in flight', () => {
      type(field<HTMLInputElement>('definition-name')!, 'Nickname');
      setInput('saving', true);

      submit();

      expect(created).toEqual([]);
    });

    it('disables both editor actions while a mutation is in flight', () => {
      setInput('saving', true);

      const buttons = Array.from(
        host().querySelectorAll<HTMLButtonElement>('.profile-definitions__form-actions button'),
      );

      expect(buttons.length).toBe(2);
      expect(buttons.every((button) => button.disabled)).toBeTrue();
    });

    it('closes the editor when the operator cancels', () => {
      const cancel = Array.from(
        host().querySelectorAll<HTMLButtonElement>('.profile-definitions__form-actions button'),
      )[1];

      cancel.click();
      fixture.detectChanges();

      expect(editor()).toBeNull();
    });
  });

  describe('the editor disclosure', () => {
    it('reports the editor state through aria-expanded', () => {
      expect(disclosure()?.getAttribute('aria-expanded')).toBe('false');

      disclosure()!.click();
      fixture.detectChanges();

      expect(disclosure()?.getAttribute('aria-expanded')).toBe('true');
    });

    it('changes its own wording with the state', () => {
      expect(disclosure()?.textContent?.trim()).toBe('Show the property editor');

      disclosure()!.click();
      fixture.detectChanges();

      expect(disclosure()?.textContent?.trim()).toBe('Hide the property editor');
    });

    it('closes an open editor', () => {
      disclosure()!.click();
      fixture.detectChanges();
      disclosure()!.click();
      fixture.detectChanges();

      expect(editor()).toBeNull();
    });

    it('is disabled while a mutation is in flight', () => {
      setInput('saving', true);

      expect(disclosure()?.disabled).toBeTrue();
    });
  });

  describe('removing a declaration', () => {
    beforeEach(() => {
      setInput('definitions', [
        defn({ propertyDefinitionId: 4, propertyName: 'Street' }),
        defn({ propertyDefinitionId: 5, propertyName: 'City' }),
      ]);
    });

    it('opens no dialog until removal is requested', () => {
      expect(dialog()).toBeNull();
    });

    it('opens the shared confirmation naming the declaration and its consequence', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();

      expect(dialog()).not.toBeNull();
      expect(host().querySelector('.confirm-dialog__message')?.textContent).toContain('Street');
      expect(host().querySelector('.confirm-dialog__message')?.textContent).toContain(
        'every value recorded against it',
      );
    });

    it('emits nothing merely by asking', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();

      expect(removed).toEqual([]);
    });

    it('emits the declaration once removal is confirmed', () => {
      rowActions(1)[1].click();
      fixture.detectChanges();

      dialogButtons()[1].click();
      fixture.detectChanges();

      expect(removed.length).toBe(1);
      expect(removed[0].propertyDefinitionId).toBe(5);
    });

    it('closes the dialog once removal is confirmed', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();
      dialogButtons()[1].click();
      fixture.detectChanges();

      expect(dialog()).toBeNull();
    });

    it('emits nothing when the operator declines', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();

      dialogButtons()[0].click();
      fixture.detectChanges();

      expect(removed).toEqual([]);
      expect(dialog()).toBeNull();
    });

    it('emits at most once even if the confirmation is somehow repeated', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();

      const internals = component as unknown as { onRemovalConfirmed(): void };
      internals.onRemovalConfirmed();
      internals.onRemovalConfirmed();
      fixture.detectChanges();

      expect(removed.length).toBe(1);
    });

    it('abandons the confirmation when the declaration disappears from the catalogue', () => {
      rowActions(0)[1].click();
      fixture.detectChanges();

      setInput('definitions', [defn({ propertyDefinitionId: 5 })]);

      expect(dialog()).toBeNull();
    });
  });
});
