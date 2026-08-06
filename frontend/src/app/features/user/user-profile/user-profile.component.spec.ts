import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { ApiResponse } from '../../../core/models/paged-result.model';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type UserProfile,
  type UserProfileValue,
} from '../../../core/models/profile.model';
import type { UserDetail } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { UserProfileComponent } from './user-profile.component';

/**
 * Specification for the dynamic profile editor.
 *
 * The cases below are chosen around the failure modes this screen actually has rather
 * than around its members. Four are worth naming because getting them wrong produces a
 * screen that looks correct and is not:
 *
 *   - A DECLARED LENGTH OF ZERO MEANS "NO MAXIMUM". The column is
 *     `Length int NOT NULL ... DEFAULT 0`, so zero is what every property gets when
 *     nobody chose a bound. A maximum-length validator built from it would mark every
 *     control invalid and no operator could save anything.
 *   - THE ROUTE INPUT MUST BE NAMED `userId`. The router binds a route parameter to an
 *     input by name, so a rename produces no compile error, no runtime error and no data.
 *   - "NEVER SET" IS NOT "SET TO EMPTY". The legacy accessor could not tell them apart;
 *     the API can, through `lastUpdatedDate`, and conflating them silently repopulates a
 *     value the operator deliberately cleared.
 *   - A STORED VALIDATION PATTERN IS UNTRUSTED. It is authored by an administrator and
 *     compiled by the browser, so one the browser rejects must degrade one field rather
 *     than throw and take the screen down.
 */
describe('UserProfileComponent', () => {
  let fixture: ComponentFixture<UserProfileComponent>;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;

  /** The account the fixtures belong to. */
  const USER_ID = 7;

  /**
   * Narrows a value the DOM or a form reports as possibly absent.
   *
   * A helper rather than a non-null assertion, so a fixture that stops matching fails
   * with a sentence naming what was missing instead of a `TypeError` deep inside an
   * expectation.
   *
   * @param value The value to narrow.
   * @param what What was expected, named in the failure.
   * @returns The value, guaranteed present.
   */
  function present<T>(value: T | null | undefined, what: string): T {
    if (value === null || value === undefined) {
      throw new Error(`Expected ${what} to be present.`);
    }

    return value;
  }

  /**
   * Builds a property declaration, defaulting every member so a case states only what it
   * is about.
   *
   * @param overrides The members this case cares about.
   * @returns A declaration.
   */
  function declaration(
    overrides: Partial<ProfilePropertyDefinition> = {},
  ): ProfilePropertyDefinition {
    return {
      propertyDefinitionId: 1,
      portalId: 0,
      moduleDefId: null,
      dataType: 1,
      defaultValue: null,
      propertyCategory: 'Name',
      propertyName: 'FirstName',
      length: 0,
      required: false,
      validationExpression: null,
      viewOrder: 0,
      visible: true,
      visibility: PROFILE_VISIBILITY.allUsers,
      ...overrides,
    };
  }

  /**
   * Builds one profile entry.
   *
   * @param definition The declaration.
   * @param overrides The entry members this case cares about.
   * @returns A profile entry.
   */
  function entry(
    definition: ProfilePropertyDefinition,
    overrides: Partial<Omit<UserProfileValue, 'definition'>> = {},
  ): UserProfileValue {
    return {
      propertyDefinitionId: definition.propertyDefinitionId,
      propertyValue: '',
      visibility: definition.visibility,
      lastUpdatedDate: null,
      definition,
      ...overrides,
    };
  }

  /** The account read for the heading. */
  const account: UserDetail = {
    userId: USER_ID,
    portalId: 0,
    username: 'jsmith',
    firstName: 'John',
    lastName: 'Smith',
    displayName: 'John Smith',
    email: 'jsmith@example.com',
    isSuperUser: false,
    affiliateId: null,
    isApproved: true,
    isLockedOut: false,
  } as UserDetail;

  /**
   * Answers both reads the screen dispatches and renders the result.
   *
   * The account read is answered as well as the profile read because the screen issues
   * both; leaving one outstanding would make the verification at teardown fail for a
   * reason unrelated to the case.
   *
   * @param properties The profile entries to return.
   * @param userId The account being read.
   */
  function respond(properties: readonly UserProfileValue[], userId: number = USER_ID): void {
    const profile: UserProfile = { userId, properties };

    // `meta` is stated rather than omitted: the envelope declares it as present-and-nullable
    // for every response, paged or not, so a fixture that left it out would not be the shape
    // the client actually receives.
    httpMock
      .expectOne(`/api/v1/users/${userId}`)
      .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
    httpMock
      .expectOne(`/api/v1/users/${userId}/profile`)
      .flush({ data: profile, meta: null } satisfies ApiResponse<UserProfile>);

    fixture.detectChanges();
  }

  /**
   * Points the screen at an account and answers its reads.
   *
   * @param properties The profile entries to return.
   * @param userId The identifier to supply, as the route would.
   */
  function load(properties: readonly UserProfileValue[], userId: number = USER_ID): void {
    fixture.componentRef.setInput('userId', String(userId));
    fixture.detectChanges();
    respond(properties, userId);
  }

  /** The host element, typed once so no case repeats the cast. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * The rendered value controls, in document order.
   *
   * @returns The controls.
   */
  function controls(): readonly HTMLInputElement[] {
    return Array.from(
      host().querySelectorAll<HTMLInputElement>('input[type="text"], textarea'),
    );
  }

  /**
   * The text of every rendered group legend, in document order.
   *
   * @returns The headings.
   */
  function headings(): readonly string[] {
    return Array.from(host().querySelectorAll('legend')).map((legend) =>
      (legend.textContent ?? '').trim(),
    );
  }

  /**
   * The text of every rendered field label, in document order.
   *
   * @returns The labels.
   */
  function labels(): readonly string[] {
    return Array.from(host().querySelectorAll('.form-field__label')).map((label) =>
      (label.textContent ?? '').replace(/\s+/g, ' ').trim(),
    );
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UserProfileComponent],
      // The real client first, then the testing backend that displaces it. Reversing the
      // order leaves the live backend in place and every expectation times out against a
      // request nothing intercepted. No interceptor is registered: the correlation
      // identifier, the bearer token and the problem-document translation are three
      // separately specified units, and running them here would assert several at once.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(UserProfileComponent);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('construction', () => {
    it('creates', () => {
      fixture.detectChanges();

      expect(fixture.componentInstance).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (UserProfileComponent as Type<UserProfileComponent> & {
        readonly ɵcmp?: { readonly onPush?: boolean };
      }).ɵcmp;

      expect(present(definition, 'the compiled component definition').onPush).toBeTrue();
    });

    it('dispatches nothing until the route supplies an account', () => {
      fixture.detectChanges();

      // Asserted through `match` rather than `expectNone`, so the count is a real
      // expectation. `expectNone` throws on a match but registers no expectation, and a
      // spec with none silently passes if its subject stops doing anything at all.
      expect(httpMock.match(() => true).length).toBe(0);
    });
  });

  describe('the route contract', () => {
    it('accepts the account through an input named exactly userId', () => {
      // The router binds a route parameter to an input BY NAME. If this input were named
      // anything else this call would throw, which is the only compile-time-free proof
      // available that the binding the router performs will land.
      expect(() => fixture.componentRef.setInput('userId', '7')).not.toThrow();
    });

    it('declares no input named permission, which route data would otherwise bind', () => {
      // Component input binding also binds route `data` keys. An input named `permission`
      // would silently receive the policy name and be mistaken for a real setting.
      expect(() => fixture.componentRef.setInput('permission', 'PortalAdministrator')).toThrow();
    });

    it('converts the string a route parameter always is, and reads that account', () => {
      fixture.componentRef.setInput('userId', '7');
      fixture.detectChanges();

      httpMock.expectOne('/api/v1/users/7').flush({ data: account });
      httpMock.expectOne('/api/v1/users/7/profile').flush({ data: null });
      fixture.detectChanges();

      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('treats zero as a real identifier rather than as an absent one', () => {
      // Accounts seed at one, so zero does not occur naturally - but neighbouring tables
      // seed at zero and at minus one, and any code that treats a particular integer as
      // "absent" makes a real row unreachable. Handled defensively.
      fixture.componentRef.setInput('userId', '0');
      fixture.detectChanges();

      const reads = httpMock.match(
        (request) => request.url === '/api/v1/users/0' || request.url === '/api/v1/users/0/profile',
      );

      expect(reads.map((request) => request.request.url)).toEqual([
        '/api/v1/users/0',
        '/api/v1/users/0/profile',
      ]);

      reads[0]?.flush({ data: account, meta: null });
      reads[1]?.flush({ data: null, meta: null });
      fixture.detectChanges();
    });

    it('dispatches nothing for a value that is not a whole number', () => {
      fixture.componentRef.setInput('userId', 'not-an-id');
      fixture.detectChanges();

      expect(httpMock.match(() => true).length).toBe(0);
    });

    it('reads the second account when the route moves to it', () => {
      load([entry(declaration())]);

      fixture.componentRef.setInput('userId', '8');
      fixture.detectChanges();

      const reads = httpMock.match((request) => request.url.startsWith('/api/v1/users/8'));

      expect(reads.map((request) => request.request.url)).toEqual([
        '/api/v1/users/8',
        '/api/v1/users/8/profile',
      ]);

      reads[0]?.flush({ data: account, meta: null });
      reads[1]?.flush({ data: null, meta: null });
      fixture.detectChanges();
    });
  });

  describe('the heading', () => {
    it('renders the shared page header rather than an ad-hoc heading', () => {
      fixture.detectChanges();

      expect(host().querySelector('app-page-header')).not.toBeNull();
    });

    it('names the account in the legacy title format', () => {
      load([entry(declaration())]);

      expect(present(host().querySelector('app-page-header'), 'the page header').textContent).toContain(
        'Edit Profile - jsmith (Id: 7)',
      );
    });

    it('is never blank while the account is still being read', () => {
      fixture.detectChanges();

      expect(
        (present(host().querySelector('app-page-header'), 'the page header').textContent ?? '').trim()
          .length,
      ).toBeGreaterThan(0);
    });
  });

  describe('when there is nothing to show', () => {
    it('renders the empty state when the tenant declares no property', () => {
      load([]);

      expect(host().querySelector('app-empty-state')).not.toBeNull();
      expect(host().querySelector('form')).toBeNull();
    });
  });

  describe('ordering and grouping', () => {
    it('orders properties by the declared view order, not by arrival order', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'LastName', viewOrder: 3 })),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'FirstName', viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 3, propertyName: 'Prefix', viewOrder: 2 })),
      ]);

      expect(labels()).toEqual(['First Name', 'Prefix', 'Last Name']);
    });

    it('groups by declared category and heads each group with the category', () => {
      load([
        entry(
          declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', propertyCategory: 'Name', viewOrder: 1 }),
        ),
        entry(
          declaration({ propertyDefinitionId: 2, propertyName: 'City', propertyCategory: 'Address', viewOrder: 2 }),
        ),
        entry(
          declaration({ propertyDefinitionId: 3, propertyName: 'LastName', propertyCategory: 'Name', viewOrder: 3 }),
        ),
      ]);

      expect(headings()).toEqual(['Name', 'Address']);
      expect(labels()).toEqual(['First Name', 'Last Name', 'City']);
    });

    it('names a group whose category is blank rather than leaving the legend empty', () => {
      load([entry(declaration({ propertyCategory: '   ' }))]);

      expect(headings()).toEqual(['General']);
    });

    it('renders every property, including one the tenant marked not visible', () => {
      // `Profile.ascx.vb` L162-L168 set `Visible = True` on every property for an
      // administrator immediately before binding, and this route is administrator-only.
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', visible: true, viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'LastName', visible: false, viewOrder: 2 })),
      ]);

      expect(controls().length).toBe(2);
      expect(labels()).toEqual(['First Name', 'Last Name']);
    });
  });

  describe('the declared length bound', () => {
    it('applies NO maximum-length rule when the declared length is zero', () => {
      // THE MOST CONSEQUENTIAL CASE IN THIS FILE. Zero is the column default and means
      // "no bound"; treating it as a bound of nothing invalidates every control on every
      // screen and no operator can save anything at all.
      load([entry(declaration({ length: 0 }))]);

      const control = controls()[0];
      present(control, 'the value control').value = 'a value considerably longer than nothing';
      present(control, 'the value control').dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.form-field__error')).toBeNull();
    });

    it('publishes no maxlength attribute when the declared length is zero', () => {
      load([entry(declaration({ length: 0 }))]);

      expect(present(controls()[0], 'the value control').hasAttribute('maxlength')).toBeFalse();
    });

    it('applies the maximum-length rule when a positive length is declared', () => {
      load([entry(declaration({ length: 4 }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'far too long';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(
        (present(host().querySelector('.form-field__error'), 'the error message').textContent ?? ''),
      ).toContain('4 characters or fewer');
    });

    it('publishes a declared positive length to the browser as well as to the validator', () => {
      load([entry(declaration({ length: 40 }))]);

      expect(present(controls()[0], 'the value control').getAttribute('maxlength')).toBe('40');
    });
  });

  describe('the declared validation pattern', () => {
    it('applies a pattern the browser can compile', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'letters';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(
        present(host().querySelector('.form-field__error'), 'the error message').textContent ?? '',
      ).toContain('not in the expected format');
    });

    it('renders the screen rather than throwing when a stored pattern cannot be compiled', () => {
      // Authored by an administrator and stored in the database, so it is untrusted input.
      // An uncompilable pattern must cost one rule, not the whole screen.
      expect(() =>
        load([entry(declaration({ validationExpression: '([unclosed' }))]),
      ).not.toThrow();

      expect(controls().length).toBe(1);
    });

    it('accepts any value for a property whose stored pattern was skipped', () => {
      load([entry(declaration({ validationExpression: '([unclosed' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'anything at all';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.form-field__error')).toBeNull();
    });
  });

  describe('seeding a control', () => {
    it('seeds from the declared default when nothing has ever been recorded', () => {
      load([entry(declaration({ defaultValue: 'Ms.' }), { lastUpdatedDate: null, propertyValue: '' })]);

      expect(present(controls()[0], 'the value control').value).toBe('Ms.');
    });

    it('keeps a recorded empty value empty rather than repopulating the default', () => {
      // The distinction the legacy accessor could not make: `GetPropertyValue` returned the
      // empty string both for a missing property and for an empty one.
      load([
        entry(declaration({ defaultValue: 'Ms.' }), {
          lastUpdatedDate: '2024-03-01T10:00:00Z',
          propertyValue: '',
        }),
      ]);

      expect(present(controls()[0], 'the value control').value).toBe('');
    });

    it('seeds from the recorded value when there is one', () => {
      load([
        entry(declaration({ defaultValue: 'Ms.' }), {
          lastUpdatedDate: '2024-03-01T10:00:00Z',
          propertyValue: 'Dr.',
        }),
      ]);

      expect(present(controls()[0], 'the value control').value).toBe('Dr.');
    });

    it('renders the legacy null-date sentinel as nothing rather than as a date', () => {
      load([
        entry(declaration({ propertyName: 'Birthday' }), {
          lastUpdatedDate: '2024-03-01T10:00:00Z',
          propertyValue: '0001-01-01T00:00:00',
        }),
      ]);

      expect(present(controls()[0], 'the value control').value).toBe('');
    });

    it('renders a property whose declared length is large with a multi-line control', () => {
      load([entry(declaration({ propertyName: 'Biography', length: 3750 }))]);

      expect(host().querySelector('textarea')).not.toBeNull();
      expect(host().querySelector('input[type="text"]')).toBeNull();
    });
  });

  describe('the legacy wording', () => {
    it('labels a seeded property with the wording the operator already knows', () => {
      load([entry(declaration({ propertyName: 'PostalCode' }))]);

      expect(labels()).toEqual(['Postal Code']);
    });

    it('falls back to the property name for a property the tenant added', () => {
      load([entry(declaration({ propertyName: 'FavouriteColour' }))]);

      expect(labels()).toEqual(['FavouriteColour']);
    });

    it('preserves the help text that duplicates its own label, defect and all', () => {
      load([entry(declaration({ propertyName: 'MiddleName' }))]);

      present(host().querySelector<HTMLButtonElement>('.form-field__help-toggle'), 'the help toggle').click();
      fixture.detectChanges();

      expect(present(host().querySelector('.form-field__help'), 'the help text').textContent).toBe(
        'Middle Name:',
      );
    });

    it('preserves the legacy misspelling in the unit help text', () => {
      load([entry(declaration({ propertyName: 'Unit' }))]);

      present(host().querySelector<HTMLButtonElement>('.form-field__help-toggle'), 'the help toggle').click();
      fixture.detectChanges();

      expect(present(host().querySelector('.form-field__help'), 'the help text').textContent).toContain(
        'appartment',
      );
    });

    it('uses the legacy required message even where it disagrees with the label', () => {
      load([entry(declaration({ propertyName: 'Cell', required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = '';
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      expect(present(host().querySelector('.form-field__error'), 'the error message').textContent).toBe(
        'Cell Phone is required',
      );
    });

    it('uses the legacy fax message, which names a fax number rather than a fax', () => {
      load([entry(declaration({ propertyName: 'Fax', required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = '';
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      expect(present(host().querySelector('.form-field__error'), 'the error message').textContent).toBe(
        'Fax number is required',
      );
    });

    it('uses the legacy locale message, whose capitalisation differs from its label', () => {
      load([entry(declaration({ propertyName: 'PreferredLocale', required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = '';
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      expect(present(host().querySelector('.form-field__error'), 'the error message').textContent).toBe(
        'Preferred locale is required',
      );
    });
  });

  describe('required properties', () => {
    it('wraps every control in the shared form field rather than a bare label', () => {
      load([entry(declaration({ required: true }))]);

      expect(host().querySelectorAll('app-form-field').length).toBe(1);
    });

    it('marks the field as required through the shared component', () => {
      load([entry(declaration({ required: true }))]);

      expect(host().querySelector('.form-field__required')).not.toBeNull();
    });

    it('shows no message before the operator has touched the control', () => {
      load([entry(declaration({ required: true }))]);

      expect(host().querySelector('.form-field__error')).toBeNull();
    });

    it('marks the control itself invalid, not merely the message region', () => {
      // The shared field owns the error region and gives it `role="alert"`, but it cannot
      // mark a projected control. Without `aria-invalid` a screen-reader user hears the
      // message and finds nothing on the field identifying it as the one at fault.
      load([entry(declaration({ required: true }))]);

      const control = present(controls()[0], 'the value control');
      expect(control.getAttribute('aria-invalid')).toBeNull();

      control.value = '';
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      expect(present(controls()[0], 'the value control').getAttribute('aria-invalid')).toBe('true');
    });

    it('rejects a value of nothing but white space', () => {
      load([entry(declaration({ required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = '   ';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.form-field__error')).not.toBeNull();
    });
  });

  describe('submitting', () => {
    /** Submits the rendered form. */
    function submit(): void {
      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();
    }

    it('writes every declared property, not merely the changed ones', () => {
      // The write REPLACES rather than merges, so a property left out of the payload is a
      // property cleared.
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'LastName', viewOrder: 2 }), {
          propertyValue: 'Smith',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      submit();

      const request = httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`);
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({
        userId: USER_ID,
        properties: [
          { propertyDefinitionId: 1, propertyValue: 'John', visibility: PROFILE_VISIBILITY.allUsers },
          { propertyDefinitionId: 2, propertyValue: 'Smith', visibility: PROFILE_VISIBILITY.allUsers },
        ],
      });

      request.flush(null);
      // The store re-reads the profile after a write, because the response carries no body.
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush({ data: { userId: USER_ID, properties: [] } });
      fixture.detectChanges();
    });

    it('carries the visibility a value already had, even with the control not offered', () => {
      load([
        entry(declaration({ visibility: PROFILE_VISIBILITY.adminOnly }), {
          visibility: PROFILE_VISIBILITY.membersOnly,
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      submit();

      const request = httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`);
      expect(request.request.body).toEqual({
        userId: USER_ID,
        properties: [
          {
            propertyDefinitionId: 1,
            propertyValue: 'John',
            visibility: PROFILE_VISIBILITY.membersOnly,
          },
        ],
      });

      request.flush(null);
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush({ data: { userId: USER_ID, properties: [] } });
      fixture.detectChanges();
    });

    it('does NOT submit an invalid form, so the administrator bypass is not reproduced', () => {
      // `Profile.ascx.vb` L94-L104 read `If ProfileProperties.IsValid Or IsAdmin`, so an
      // administrator skipped every declared rule. On an administrator-only route that
      // would make client validation entirely vacuous, which contradicts the requirement
      // that validation rules must match. Deliberately not carried across.
      load([entry(declaration({ required: true }))]);

      submit();

      expect(httpMock.match(`/api/v1/users/${USER_ID}/profile`).length).toBe(0);
    });

    it('reveals the messages for controls the operator never visited', () => {
      load([entry(declaration({ required: true }))]);

      submit();

      expect(host().querySelector('.form-field__error')).not.toBeNull();
    });

    it('restores the values the profile arrived with when the operator cancels', () => {
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      const control = present(controls()[0], 'the value control');
      control.value = 'edited';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      const buttons = Array.from(host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'));
      present(buttons[1], 'the cancel action').click();
      fixture.detectChanges();

      // Possible in one call only because every control is non-nullable: resetting one
      // returns it to its construction value rather than to null.
      expect(present(controls()[0], 'the value control').value).toBe('John');
    });
  });

  describe('the visibility affordance', () => {
    it('is not offered by default, matching the legacy condition on this route', () => {
      // `ShowVisibility` was the tenant setting AND the viewer being the profile's subject.
      // An administrator editing another account failed the second half.
      load([entry(declaration())]);

      expect(host().querySelector('select')).toBeNull();
    });

    it('is offered when a caller asks for it', () => {
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('offers the three legacy choices', () => {
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);

      expect(Array.from(host().querySelectorAll('option')).map((o) => (o.textContent ?? '').trim())).toEqual([
        'All users',
        'Members only',
        'Administrators only',
      ]);
    });
  });

  describe('view mode', () => {
    beforeEach(() => {
      fixture.componentRef.setInput('mode', 'view');
    });

    it('renders no form and no action row, which is what ShowUpdate="False" asked for', () => {
      load([entry(declaration())]);

      expect(host().querySelector('form')).toBeNull();
      expect(host().querySelector('.user-profile__actions')).toBeNull();
    });

    it('renders each property as a term and its value', () => {
      load([
        entry(declaration({ propertyName: 'FirstName' }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      expect(present(host().querySelector('dt'), 'the term').textContent).toContain('First Name');
      expect(present(host().querySelector('dd'), 'the value').textContent).toContain('John');
    });

    it('renders the null-date sentinel as nothing', () => {
      load([
        entry(declaration({ propertyName: 'Birthday' }), {
          propertyValue: '0001-01-01',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      expect((present(host().querySelector('dd'), 'the value').textContent ?? '').trim()).toBe('');
    });
  });

  describe('collapsing a group', () => {
    it('hides a group\'s fields and reports the state on the toggle', () => {
      load([entry(declaration())]);

      const toggle = present(
        host().querySelector<HTMLButtonElement>('.user-profile__toggle'),
        'the toggle',
      );
      expect(toggle.getAttribute('aria-expanded')).toBe('true');

      toggle.click();
      fixture.detectChanges();

      expect(
        present(host().querySelector<HTMLButtonElement>('.user-profile__toggle'), 'the toggle').getAttribute(
          'aria-expanded',
        ),
      ).toBe('false');
      expect(controls().length).toBe(0);
    });

    it('is a real button, reversing the legacy negative tab index', () => {
      load([entry(declaration())]);

      const toggle = present(
        host().querySelector<HTMLButtonElement>('.user-profile__toggle'),
        'the toggle',
      );

      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle.hasAttribute('tabindex')).toBeFalse();
    });
  });

  describe('failures', () => {
    it('renders the shared error banner', () => {
      fixture.detectChanges();

      expect(host().querySelector('app-error-banner')).not.toBeNull();
    });

    it('surfaces a failed profile read through the banner', () => {
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock.expectOne(`/api/v1/users/${USER_ID}`).flush({ data: account });
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        { title: 'Server error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
      fixture.detectChanges();

      expect(
        (present(host().querySelector('app-error-banner'), 'the banner').textContent ?? '').trim().length,
      ).toBeGreaterThan(0);
    });

    it('announces a refusal at warning severity rather than as an error', () => {
      // `AccessDenied.ascx.vb` rendered at the warning message type in BOTH branches of
      // its load handler, so a permission refusal is a warning here too.
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock.expectOne(`/api/v1/users/${USER_ID}`).flush({ data: account });
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        { title: 'Forbidden', status: 403 },
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const raised = notifications.notifications();
      expect(raised.length).toBe(1);
      expect(present(raised[0], 'the notification').severity).toBe('warning');
    });

    it('shows the server\'s per-field message beside the field it names', () => {
      load([entry(declaration({ propertyName: 'FirstName', required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'John';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          // A .NET model-state key is Pascal-cased on the wire. Both spellings are probed.
          errors: { FirstName: ['That name is already taken.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(
        present(host().querySelector('.form-field__error'), 'the error message').textContent,
      ).toContain('That name is already taken.');
    });
  });
});
