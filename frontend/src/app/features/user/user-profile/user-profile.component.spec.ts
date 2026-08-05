import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type UserProfile,
  type UserProfileSubmission,
  type UserProfileValue,
} from '../../../core/models/profile.model';
import { UserProfileComponent } from './user-profile.component';

/**
 * Builds a property declaration, defaulted to the least demanding shape so that each
 * expectation states only what it depends on.
 *
 * @param overrides The members to replace.
 * @returns A declaration.
 */
function definition(
  overrides: Partial<ProfilePropertyDefinition> = {},
): ProfilePropertyDefinition {
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

/**
 * Builds a profile value together with its declaration.
 *
 * @param declaration The declaration to attach.
 * @param overrides The value members to replace.
 * @returns A profile value.
 */
function value(
  declaration: ProfilePropertyDefinition,
  overrides: Partial<Omit<UserProfileValue, 'definition'>> = {},
): UserProfileValue {
  return {
    propertyDefinitionId: declaration.propertyDefinitionId,
    propertyValue: '',
    visibility: declaration.visibility,
    lastUpdatedDate: null,
    definition: declaration,
    ...overrides,
  };
}

/**
 * Builds a profile from a list of values.
 *
 * @param values The values to include.
 * @returns A profile for account 7.
 */
function profileOf(values: readonly UserProfileValue[]): UserProfile {
  return { userId: 7, properties: values };
}

describe('UserProfileComponent', () => {
  let fixture: ComponentFixture<UserProfileComponent>;
  let component: UserProfileComponent;

  /**
   * Sets one of the component's inputs and re-renders.
   *
   * Assignment to the instance field would not re-render: the component declares
   * on-push change detection, so it must be marked dirty. `setInput` does that and runs
   * the declared transform.
   *
   * @param name The input to set.
   * @param inputValue The value to set.
   */
  function setInput(
    name: 'profile' | 'mode' | 'heading' | 'loading' | 'saving' | 'manageVisibility',
    inputValue: unknown,
  ): void {
    fixture.componentRef.setInput(name, inputValue);
    fixture.detectChanges();
  }

  /**
   * Returns the component's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Returns every rendered category group.
   */
  function groups(): HTMLElement[] {
    return Array.from(host().querySelectorAll<HTMLElement>('fieldset.user-profile__group'));
  }

  /**
   * Returns every rendered collapse toggle.
   */
  function toggles(): HTMLButtonElement[] {
    return Array.from(host().querySelectorAll<HTMLButtonElement>('button.user-profile__toggle'));
  }

  /**
   * Returns the single-line and multi-line controls the editor rendered, in order.
   */
  function controls(): (HTMLInputElement | HTMLTextAreaElement)[] {
    return Array.from(
      host().querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(
        '.user-profile__fields input, .user-profile__fields textarea',
      ),
    );
  }

  /**
   * Returns the control rendered for one declaration.
   *
   * @param definitionId The declaration identifier.
   */
  function controlFor(definitionId: number): HTMLInputElement | HTMLTextAreaElement | null {
    return host().querySelector(`#profile-property-${definitionId}`);
  }

  /**
   * Returns the validation message rendered for one declaration.
   *
   * @param definitionId The declaration identifier.
   */
  function messageFor(definitionId: number): HTMLElement | null {
    return host().querySelector(`#profile-property-${definitionId}-message`);
  }

  /**
   * Sets a control's value the way a user would, so the control becomes dirty.
   *
   * @param element The control.
   * @param next The text to enter.
   */
  function type(element: HTMLInputElement | HTMLTextAreaElement, next: string): void {
    element.value = next;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Submits the editor form.
   */
  function submit(): void {
    const form = host().querySelector('form');
    form?.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [UserProfileComponent] }).compileComponents();

    fixture = TestBed.createComponent(UserProfileComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const meta = (UserProfileComponent as unknown as { ɵcmp?: { onPush?: boolean } }).ɵcmp;

      expect(meta?.onPush).toBeTrue();
    });

    it('is standalone, so the route can load it directly', () => {
      const meta = (UserProfileComponent as unknown as { ɵcmp?: { standalone?: boolean } }).ɵcmp;

      expect(meta?.standalone).toBeTrue();
    });

    it('defaults to the editing mode', () => {
      expect(component.mode).toBe('edit');
    });

    it('restores safe defaults when route input binding supplies no data', () => {
      expect(() => {
        setInput('profile', undefined);
        setInput('mode', undefined);
        setInput('heading', undefined);
      }).not.toThrow();

      expect(component.profile).toBeNull();
      expect(component.mode).toBe('edit');
      expect(host().querySelector('app-page-header h1')?.textContent?.trim()).toBe('Profile');
      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('defaults to offering the visibility control', () => {
      expect(component.manageVisibility).toBeTrue();
    });
  });

  describe('screen title', () => {
    it('renders the shared page header rather than an ad-hoc heading', () => {
      // The stylesheet names the shared page header as the owner of the title and
      // explicitly disclaims styling it, so the title must come from that component.
      expect(host().querySelector('app-page-header')).not.toBeNull();
    });

    it('renders a default title, so the screen is never untitled', () => {
      expect(host().querySelector('app-page-header h1')?.textContent?.trim()).toBe('Profile');
    });

    it('renders a supplied title', () => {
      fixture.componentRef.setInput('heading', 'Edit Profile');
      fixture.detectChanges();

      expect(host().querySelector('app-page-header h1')?.textContent?.trim()).toBe('Edit Profile');
    });

    it('renders the title while the profile is still being fetched', () => {
      setInput('loading', true);

      expect(host().querySelector('app-page-header')).not.toBeNull();
      expect(host().querySelector('app-loading-spinner')).not.toBeNull();
    });
  });

  describe('while the profile is being fetched', () => {
    beforeEach(() => {
      setInput('loading', true);
    });

    it('renders the shared progress indicator', () => {
      expect(host().querySelector('app-loading-spinner')).not.toBeNull();
    });

    it('renders no groups, so a half-built form is never shown', () => {
      expect(groups().length).toBe(0);
    });

    it('renders the progress indicator even when a profile is already held', () => {
      setInput('profile', profileOf([value(definition())]));
      setInput('loading', true);

      expect(host().querySelector('app-loading-spinner')).not.toBeNull();
      expect(groups().length).toBe(0);
    });
  });

  describe('when there is nothing to edit', () => {
    it('renders the empty state with no profile at all', () => {
      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('renders the empty state when the profile carries no properties', () => {
      setInput('profile', profileOf([]));

      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('renders the empty state when every declared property is hidden', () => {
      setInput('profile', profileOf([value(definition({ visible: false }))]));

      expect(host().querySelector('app-empty-state')).not.toBeNull();
      expect(groups().length).toBe(0);
    });
  });

  describe('grouping', () => {
    it('renders one group per category, in the order the properties arrive', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' })),
          value(definition({ propertyDefinitionId: 2, propertyCategory: 'Address' })),
          value(definition({ propertyDefinitionId: 3, propertyCategory: 'Name' })),
        ]),
      );

      expect(groups().length).toBe(2);
      expect(toggles().map((toggle) => toggle.textContent?.trim())).toEqual(['Name', 'Address']);
    });

    it('keeps every property of a category together, even when they are not adjacent', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' })),
          value(definition({ propertyDefinitionId: 2, propertyCategory: 'Address' })),
          value(definition({ propertyDefinitionId: 3, propertyCategory: 'Name' })),
        ]),
      );

      const firstGroupControls = groups()[0].querySelectorAll('input, textarea');

      expect(firstGroupControls.length).toBe(2);
    });

    it('gives a property with no category a named group rather than a nameless one', () => {
      setInput('profile', profileOf([value(definition({ propertyCategory: '   ' }))]));

      expect(toggles()[0].textContent?.trim()).toBe('General');
    });

    it('omits a hidden property from a group it shares with a visible one', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1 })),
          value(definition({ propertyDefinitionId: 2, visible: false })),
        ]),
      );

      expect(groups().length).toBe(1);
      expect(controlFor(1)).not.toBeNull();
      expect(controlFor(2)).toBeNull();
    });

    it('omits a category left with no visible property, rather than rendering an empty box', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' })),
          value(
            definition({ propertyDefinitionId: 2, propertyCategory: 'Address', visible: false }),
          ),
        ]),
      );

      expect(groups().length).toBe(1);
    });
  });

  describe('editing', () => {
    beforeEach(() => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyName: 'First Name' }), {
            propertyValue: 'Grace',
            lastUpdatedDate: '2026-01-01T00:00:00Z',
          }),
          value(
            definition({
              propertyDefinitionId: 2,
              propertyName: 'Biography',
              propertyCategory: 'Name',
              length: 3750,
            }),
          ),
        ]),
      );
    });

    it('renders one control per visible property', () => {
      expect(controls().length).toBe(2);
    });

    it('associates each label with its control', () => {
      const label = host().querySelector<HTMLLabelElement>('label[for="profile-property-1"]');

      expect(label).not.toBeNull();
      expect(label?.textContent?.trim().startsWith('First Name')).toBeTrue();
    });

    it('starts a control at the value the account already recorded', () => {
      expect((controlFor(1) as HTMLInputElement).value).toBe('Grace');
    });

    it('renders a single-line control for a short property', () => {
      expect(controlFor(1)?.tagName.toLowerCase()).toBe('input');
    });

    it('renders a multi-line control for a property with a large declared length', () => {
      expect(controlFor(2)?.tagName.toLowerCase()).toBe('textarea');
    });

    it('publishes the declared length to the browser as well as to the validator', () => {
      expect(controlFor(1)?.getAttribute('maxlength')).toBe('50');
    });

    it('publishes no length bound when the tenant declared none', () => {
      setInput('profile', profileOf([value(definition({ length: 0 }))]));

      expect(controlFor(1)?.hasAttribute('maxlength')).toBeFalse();
    });

    it('renders the action row', () => {
      expect(host().querySelector('.user-profile__actions')).not.toBeNull();
      expect(host().querySelector('button[type="submit"]')).not.toBeNull();
    });

    it('disables both actions while a submission is in flight', () => {
      setInput('saving', true);

      const buttons = Array.from(
        host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'),
      );

      expect(buttons.length).toBe(2);
      expect(buttons.every((button) => button.disabled)).toBeTrue();
    });
  });

  describe('required properties', () => {
    beforeEach(() => {
      setInput(
        'profile',
        profileOf([value(definition({ propertyName: 'First Name', required: true }))]),
      );
    });

    it('marks the label, and hides the marker from assistive technology', () => {
      const marker = host().querySelector('.form-required');

      expect(marker).not.toBeNull();
      expect(marker?.getAttribute('aria-hidden')).toBe('true');
    });

    it('announces the requirement on the control itself', () => {
      expect(controlFor(1)?.getAttribute('aria-required')).toBe('true');
    });

    it('shows no message before the operator has touched the control', () => {
      expect(messageFor(1)).toBeNull();
    });

    it('shows a message once a required value is cleared', () => {
      type(controlFor(1)!, '');

      expect(messageFor(1)?.textContent?.trim()).toBe('First Name is required.');
    });

    it('refuses a value consisting only of white space, which the API also refuses', () => {
      type(controlFor(1)!, '    ');

      expect(messageFor(1)?.textContent?.trim()).toBe('First Name is required.');
    });

    it('marks the control invalid for assistive technology at the same time', () => {
      type(controlFor(1)!, '');

      expect(controlFor(1)?.getAttribute('aria-invalid')).toBe('true');
    });

    it('points the control at its message', () => {
      type(controlFor(1)!, '');

      expect(controlFor(1)?.getAttribute('aria-describedby')).toBe('profile-property-1-message');
    });

    it('clears the message once a value is supplied', () => {
      type(controlFor(1)!, '');
      type(controlFor(1)!, 'Grace');

      expect(messageFor(1)).toBeNull();
      expect(controlFor(1)?.hasAttribute('aria-invalid')).toBeFalse();
    });
  });

  describe('declared bounds and patterns', () => {
    it('reports a value longer than the declared length', () => {
      setInput(
        'profile',
        profileOf([value(definition({ propertyName: 'Initials', length: 3 }))]),
      );

      // The browser's own `maxlength` stops typing, but a paste or a programmatic set
      // can still exceed it, and the validator is what the submission gate reads.
      type(controlFor(1)!, 'ABCD');

      expect(messageFor(1)?.textContent?.trim()).toBe('Initials must be 3 characters or fewer.');
    });

    it('reports a value that does not match a declared pattern', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyName: 'Telephone', validationExpression: '^[0-9]+$' })),
        ]),
      );

      type(controlFor(1)!, 'not-a-number');

      expect(messageFor(1)?.textContent?.trim()).toBe('Telephone is not in the expected format.');
    });

    it('accepts a value that matches a declared pattern', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyName: 'Telephone', validationExpression: '^[0-9]+$' })),
        ]),
      );

      type(controlFor(1)!, '5551234');

      expect(messageFor(1)).toBeNull();
    });

    it('ignores a stored pattern this engine cannot compile, leaving the server to judge', () => {
      // A stored expression is operator-authored and was evaluated by a different
      // regular-expression engine. An unusable one must not throw and must not refuse
      // every value; it is skipped, and the API still enforces it.
      setInput(
        'profile',
        profileOf([value(definition({ validationExpression: '(?<unterminated' }))]),
      );

      type(controlFor(1)!, 'anything at all');

      expect(messageFor(1)).toBeNull();
    });

    it('ignores a blank stored pattern', () => {
      setInput('profile', profileOf([value(definition({ validationExpression: '   ' }))]));

      type(controlFor(1)!, 'anything at all');

      expect(messageFor(1)).toBeNull();
    });
  });

  describe('per-property visibility', () => {
    beforeEach(() => {
      setInput(
        'profile',
        profileOf([
          value(definition(), { visibility: PROFILE_VISIBILITY.membersOnly }),
        ]),
      );
    });

    it('renders a visibility row per property', () => {
      expect(host().querySelectorAll('.user-profile__visibility').length).toBe(1);
    });

    it('offers the three choices the legacy editor offered', () => {
      const options = Array.from(
        host().querySelectorAll<HTMLOptionElement>('#profile-property-1-visibility option'),
      );

      expect(options.map((option) => option.textContent?.trim())).toEqual([
        'All users',
        'Members only',
        'Administrators only',
      ]);
    });

    it('starts at the visibility the value already carried', () => {
      const select = host().querySelector<HTMLSelectElement>('#profile-property-1-visibility');

      expect(select?.selectedIndex).toBe(PROFILE_VISIBILITY.membersOnly);
    });

    it('associates the visibility label with its control', () => {
      const label = host().querySelector<HTMLLabelElement>(
        'label[for="profile-property-1-visibility"]',
      );

      expect(label?.textContent?.trim()).toBe('Visible to');
    });

    it('renders no visibility row when the tenant has switched the affordance off', () => {
      setInput('manageVisibility', false);

      expect(host().querySelector('.user-profile__visibility')).toBeNull();
    });
  });

  describe('collapsing a category', () => {
    beforeEach(() => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' })),
          value(definition({ propertyDefinitionId: 2, propertyCategory: 'Address' })),
        ]),
      );
    });

    it('starts every category expanded', () => {
      expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
        'true',
        'true',
      ]);
    });

    it('removes the field stack rather than hiding it', () => {
      toggles()[0].click();
      fixture.detectChanges();

      // Removal rather than `hidden`, because `.user-profile__fields` declares
      // `display: grid` and any author rule outranks the user agent's hidden rule.
      expect(groups()[0].querySelector('.user-profile__fields')).toBeNull();
      expect(controlFor(1)).toBeNull();
    });

    it('announces the collapsed state on the toggle', () => {
      toggles()[0].click();
      fixture.detectChanges();

      expect(toggles()[0].getAttribute('aria-expanded')).toBe('false');
    });

    it('leaves the other categories alone', () => {
      toggles()[0].click();
      fixture.detectChanges();

      expect(toggles()[1].getAttribute('aria-expanded')).toBe('true');
      expect(controlFor(2)).not.toBeNull();
    });

    it('expands again when activated a second time', () => {
      toggles()[0].click();
      fixture.detectChanges();
      toggles()[0].click();
      fixture.detectChanges();

      expect(toggles()[0].getAttribute('aria-expanded')).toBe('true');
      expect(controlFor(1)).not.toBeNull();
    });

    it('resets the collapse state when a different profile arrives', () => {
      toggles()[0].click();
      fixture.detectChanges();

      setInput(
        'profile',
        profileOf([value(definition({ propertyDefinitionId: 9, propertyCategory: 'Name' }))]),
      );

      expect(toggles()[0].getAttribute('aria-expanded')).toBe('true');
    });
  });

  describe('submission', () => {
    let submitted: UserProfileSubmission[];

    beforeEach(() => {
      submitted = [];
      component.save.subscribe((payload) => {
        submitted.push(payload);
      });
    });

    it('carries the account identifier and every property', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' }), {
            propertyValue: 'Grace',
            lastUpdatedDate: '2026-01-01T00:00:00Z',
          }),
          value(definition({ propertyDefinitionId: 2, propertyCategory: 'Address' })),
        ]),
      );

      submit();

      expect(submitted.length).toBe(1);
      expect(submitted[0].userId).toBe(7);
      expect(submitted[0].properties.map((entry) => entry.propertyDefinitionId)).toEqual([1, 2]);
    });

    it('names the collection exactly as the request body declares it', () => {
      // The replace verb binds the same contract the read returns, and that contract
      // spells the collection `properties`. The API refuses an undeclared member rather
      // than discarding it, so a payload spelling this `values` is answered 400 naming
      // the member and no profile is written. Nothing in a compiler catches a
      // cross-language name divergence, so it is pinned here by key rather than by type.
      setInput('profile', profileOf([value(definition())]));

      submit();

      expect(Object.keys(submitted[0]).sort()).toEqual(['properties', 'userId']);
      expect('values' in submitted[0]).toBeFalse();
    });

    it('carries the values the operator entered', () => {
      setInput('profile', profileOf([value(definition())]));

      type(controlFor(1)!, 'Hopper');
      submit();

      expect(submitted[0].properties[0].propertyValue).toBe('Hopper');
    });

    it('carries the visibility the operator chose', () => {
      setInput('profile', profileOf([value(definition())]));

      const select = host().querySelector<HTMLSelectElement>('#profile-property-1-visibility')!;
      select.selectedIndex = PROFILE_VISIBILITY.allUsers;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      submit();

      // A number, not the string the browser reports: the option is bound with
      // `ngValue`, which preserves the declared type all the way to the payload.
      expect(submitted[0].properties[0].visibility).toBe(PROFILE_VISIBILITY.allUsers);
      expect(typeof submitted[0].properties[0].visibility).toBe('number');
    });

    it('carries a collapsed category too, because a write replaces the whole profile', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyCategory: 'Name' })),
          value(definition({ propertyDefinitionId: 2, propertyCategory: 'Address' })),
        ]),
      );

      toggles()[1].click();
      fixture.detectChanges();
      submit();

      // Omitting a collapsed group would clear every value in it, because the API
      // replaces rather than merges.
      expect(submitted[0].properties.map((entry) => entry.propertyDefinitionId)).toEqual([1, 2]);
    });

    it('emits nothing while the form is invalid', () => {
      setInput('profile', profileOf([value(definition({ required: true }))]));

      submit();

      expect(submitted.length).toBe(0);
    });

    it('reveals every message when an invalid form is submitted', () => {
      setInput('profile', profileOf([value(definition({ required: true }))]));

      expect(messageFor(1)).toBeNull();

      submit();

      // Marking every control touched is what makes the message appear for a control
      // the operator never visited.
      expect(messageFor(1)).not.toBeNull();
    });

    it('emits nothing while a submission is already in flight', () => {
      setInput('profile', profileOf([value(definition())]));
      setInput('saving', true);

      submit();

      expect(submitted.length).toBe(0);
    });

    it('emits again once the previous submission completes', () => {
      setInput('profile', profileOf([value(definition())]));
      setInput('saving', true);
      submit();
      setInput('saving', false);
      submit();

      expect(submitted.length).toBe(1);
    });
  });

  describe('cancelling', () => {
    it('restores every control to the value the profile arrived with', () => {
      setInput(
        'profile',
        profileOf([
          value(definition(), {
            propertyValue: 'Grace',
            lastUpdatedDate: '2026-01-01T00:00:00Z',
          }),
        ]),
      );

      type(controlFor(1)!, 'Edited');
      expect((controlFor(1) as HTMLInputElement).value).toBe('Edited');

      host()
        .querySelectorAll<HTMLButtonElement>('.user-profile__actions button')[1]
        .click();
      fixture.detectChanges();

      expect((controlFor(1) as HTMLInputElement).value).toBe('Grace');
    });
  });

  describe('initial values', () => {
    it('keeps a value the account deliberately cleared, rather than reapplying the default', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ defaultValue: 'Unknown' }), {
            propertyValue: '',
            lastUpdatedDate: '2026-01-01T00:00:00Z',
          }),
        ]),
      );

      expect((controlFor(1) as HTMLInputElement).value).toBe('');
    });

    it('applies the declared default when the account has never recorded anything', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ defaultValue: 'Unknown' }), {
            propertyValue: '',
            lastUpdatedDate: null,
          }),
        ]),
      );

      expect((controlFor(1) as HTMLInputElement).value).toBe('Unknown');
    });

    it('prefers a recorded value over the declared default', () => {
      setInput(
        'profile',
        profileOf([
          value(definition({ defaultValue: 'Unknown' }), {
            propertyValue: 'Grace',
            lastUpdatedDate: null,
          }),
        ]),
      );

      expect((controlFor(1) as HTMLInputElement).value).toBe('Grace');
    });
  });

  describe('viewing', () => {
    beforeEach(() => {
      setInput(
        'profile',
        profileOf([
          value(definition({ propertyDefinitionId: 1, propertyName: 'First Name' }), {
            propertyValue: 'Grace',
          }),
          value(
            definition({
              propertyDefinitionId: 2,
              propertyName: 'Nickname',
              propertyCategory: 'Name',
              defaultValue: 'Amazing Grace',
            }),
          ),
          value(
            definition({
              propertyDefinitionId: 3,
              propertyName: 'Middle Name',
              propertyCategory: 'Name',
            }),
          ),
        ]),
      );
      setInput('mode', 'view');
    });

    it('renders a description list rather than controls', () => {
      expect(host().querySelector('dl.user-profile__values')).not.toBeNull();
      expect(controls().length).toBe(0);
    });

    it('pairs every property name with its value', () => {
      const terms = Array.from(host().querySelectorAll('dt')).map((term) =>
        term.textContent?.trim(),
      );

      expect(terms).toEqual(['First Name', 'Nickname', 'Middle Name']);
    });

    it('shows the recorded value', () => {
      const values = Array.from(host().querySelectorAll('dd.user-profile__value'));

      expect(values[0].textContent?.trim()).toBe('Grace');
    });

    it('shows the declared default when nothing was recorded', () => {
      const values = Array.from(host().querySelectorAll('dd.user-profile__value'));

      expect(values[1].textContent?.trim()).toBe('Amazing Grace');
    });

    it('shows nothing at all when neither a value nor a default exists', () => {
      const values = Array.from(host().querySelectorAll('dd.user-profile__value'));

      // Not a placeholder glyph: an em dash would be announced as content, and the
      // absence of a value is not content.
      expect(values[2].textContent?.trim()).toBe('');
    });

    it('renders no action row, which is what the legacy view page asked for', () => {
      expect(host().querySelector('.user-profile__actions')).toBeNull();
    });

    it('renders no form element, so nothing here can be submitted', () => {
      expect(host().querySelector('form')).toBeNull();
    });

    it('still allows a category to be collapsed', () => {
      toggles()[0].click();
      fixture.detectChanges();

      expect(toggles()[0].getAttribute('aria-expanded')).toBe('false');
      expect(groups()[0].querySelector('dl')).toBeNull();
    });
  });
});
