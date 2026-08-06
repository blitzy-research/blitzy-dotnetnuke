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
import type { ProblemDetails } from '../../../core/models/problem-details.model';
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
   * The reason phrase the API publishes as the problem `title`, keyed by status.
   *
   * ⚠️ NOT FREE TEXT. A refusal reaches the wire through one shared problem factory that fills the
   * title from a status-keyed vocabulary and fills an unspecified type from the same vocabulary's
   * default code - so a document carrying a bespoke title such as `'Server error'`, or carrying no
   * `type` at all, describes no response this server can produce. Worse, such a fixture silently
   * exercises the message-precedence rule (detail, then title, then a fallback) and the failure-code
   * reader against values no operator will ever see.
   */
  const PROBLEM_TITLE: Readonly<Record<number, string>> = Object.freeze({
    400: 'Bad Request',
    401: 'Unauthorized',
    403: 'Forbidden',
    404: 'Not Found',
    409: 'Conflict',
    500: 'Internal Server Error',
  });

  /**
   * A problem document as this API publishes one: complete, coherent and emittable.
   *
   * There is deliberately no `instance` member - every call site supplies null for it and the
   * framework's problem type omits a null one per member - and both identifiers are present, because
   * the pipeline attaches both.
   *
   * @param status The status the server answered with.
   * @param code The failure code, carried behind the URN prefix.
   * @param detail The authored explanation.
   * @returns The document.
   */
  function problemOf(status: number, code: string, detail: string): ProblemDetails {
    return {
      type: `urn:dnnmigration:error:${code}`,
      title: PROBLEM_TITLE[status] ?? 'Error',
      status,
      detail,
      traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      correlationId: '6e2b0c94-3f57-4a81-b2d6-84c1f9e50a7b',
    };
  }

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
  // Declared WITHOUT a type assertion, so the compiler requires every member the contract
  // declares. The previous `as UserDetail` cast admitted an object missing eight of them,
  // which made this fixture a less demanding stand-in for the server than the server is: the
  // transport now decodes each response against the published contract, and a body missing
  // `roles`, `isOnline`, `mustChangePassword` or any of the audit instants is refused at the
  // boundary exactly as a drifted server response would be.
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
    isOnline: false,
    mustChangePassword: false,
    createdDate: '2024-01-05T09:15:00Z',
    lastLoginDate: '2024-03-02T11:40:00Z',
    lastActivityDate: '2024-03-02T11:52:00Z',
    lastLockoutDate: null,
    lastPasswordChangeDate: '2024-01-05T09:20:00Z',
    roles: ['Registered Users'],
  };

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

      // ⚠️ A 404, NOT A 200 CARRYING `data: null`. The shared result translator turns a SUCCESSFUL
      // outcome carrying no value into a `404` under `resource.not_found` - with a detail naming
      // neither the identifier nor the resource kind, so an unauthorised caller cannot tell "this
      // exists but is not yours" from "this does not exist" - so a 200 with a null payload cannot
      // leave this API for ANY single-resource route. An earlier revision modelled one, which proved
      // the screen copes with a shape nothing sends while leaving the shape it does send untested.
      httpMock
        .expectOne('/api/v1/users/7')
        .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
      httpMock
        .expectOne('/api/v1/users/7/profile')
        .flush(problemOf(404, 'resource.not_found', 'The requested resource does not exist.'), {
          status: 404,
          statusText: 'Not Found',
        });
      fixture.detectChanges();

      // The account identifier still reached the wire as the number the route's string names, which is
      // what this case exists to prove - the refusal that followed does not change that.
      expect(host().querySelector('app-error-banner')).not.toBeNull();
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
      // A declared-nothing profile is an EMPTY property list, not a null payload. An account with no
      // values still HAS a profile resource; the only way this API can answer null is by answering
      // 404, which would mean the resource does not exist at all - a different fact, and one the
      // screen renders differently.
      reads[1]?.flush({
        data: { userId: 0, properties: [] },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
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
      reads[1]?.flush({
        data: { userId: 8, properties: [] },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
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

  // ---------------------------------------------------------------------------
  // THE TENANT'S VALIDATION EXPRESSION IS NOT RUN HERE
  // ---------------------------------------------------------------------------
  //
  // ⚠ THIS BLOCK ASSERTED THE OPPOSITE, AND THE ASSERTION WAS THE VULNERABILITY. The expression is
  // administrator-authored data, so it is untrusted input to whatever engine runs it, and it was being
  // compiled and executed synchronously on the UI thread on every keystroke — on controls whose length is
  // frequently unbounded, because a declared length of zero means no maximum. A catastrophically
  // backtracking pattern therefore froze the browser tab with no way out, and the tenant who authored the
  // declaration is not necessarily the operator who suffers it.
  //
  // The server is not merely a second authority here, it is the only party that can run these safely: it
  // compiles with a fifty-millisecond match timeout, a length ceiling and a bounded cache. The browser's
  // engine exposes no timeout at all, so a client-side evaluation cannot be bounded — only avoided.
  //
  // These cases therefore pin the ABSENCE of the rule, which is deliberately not the same thing as the
  // rule being unenforced: it is enforced by the endpoint, and reported per field through the server's own
  // model-state message, which this screen already renders.
  describe('the declared validation pattern', () => {
    it('does not evaluate a stored expression in the browser', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'letters';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      // A value the expression plainly refuses, and nothing is reported beside the box: the rule is the
      // server's, and the operator learns of it from the server's answer.
      expect(host().querySelector('.form-field__error')).toBeNull();
    });

    it('is not blocked from submitting by a value the stored expression would refuse', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'letters';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      // The value travels, and the endpoint decides. A client-side rule here would have to be exactly as
      // strict as a .NET expression evaluated by a different engine, which is not something a browser can
      // promise — and being stricter would refuse values the server accepts.
      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === `/api/v1/users/${USER_ID}/profile`,
      );
      expect((written.request.body as { properties: readonly { propertyValue: string }[] }).properties[0].propertyValue).toBe('letters');

      written.flush(null);
      httpMock
        .match(() => true)
        .forEach((outstanding) =>
          outstanding.flush({ data: { userId: USER_ID, properties: [] } }),
        );
      fixture.detectChanges();
    });

    it('renders the screen for an expression no engine could compile', () => {
      // Nothing compiles it any longer, so an uncompilable one costs nothing at all — but the case is kept
      // because it is the shape of stored data most likely to be present.
      expect(() =>
        load([entry(declaration({ validationExpression: '([unclosed' }))]),
      ).not.toThrow();

      expect(controls().length).toBe(1);
    });

    it('accepts any value whatever the stored expression says', () => {
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

      // ⚠️ `204` WITH A NULL BODY, STATED EXPLICITLY. Letting the status default to 200 encoded the
      // wrong contract and still passed: `PUT /api/v1/users/{userId}/profile` returns an outcome that
      // carries no value, and the shared translator answers such an outcome with `204` - which HTTP
      // forbids from having a body at all. That is precisely WHY the store re-reads the profile
      // afterwards, so a fixture that answered 200 was quietly removing the reason for the follow-up
      // it then went on to expect.
      request.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne(`/api/v1/users/${USER_ID}/profile`)
        .flush({
          data: { userId: USER_ID, properties: [] },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
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

      request.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne(`/api/v1/users/${USER_ID}/profile`)
        .flush({
          data: { userId: USER_ID, properties: [] },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
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

      httpMock
        .expectOne(`/api/v1/users/${USER_ID}`)
        .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
      // The live document, complete: an unhandled fault leaves the type unspecified so the shared
      // factory fills it from the status - `server.unexpected_failure` - and the title is the reason
      // phrase for 500, not the bespoke `'Server error'` the previous fixture invented.
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        problemOf(
          500,
          'server.unexpected_failure',
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // The BANNER carries the server's own sentence, which is what the message-precedence rule
      // resolves to when a detail is present - and a fixture without one could never have shown that.
      expect(present(host().querySelector('app-error-banner'), 'the banner').textContent).toContain(
        'An unexpected error occurred while processing the request.',
      );
      // And the form is not rendered, because there is no profile to edit.
      expect(host().querySelector('form')).toBeNull();
    });

    it('announces a refusal at warning severity rather than as an error', () => {
      // `AccessDenied.ascx.vb` rendered at the warning message type in BOTH branches of
      // its load handler, so a permission refusal is a warning here too.
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock
        .expectOne(`/api/v1/users/${USER_ID}`)
        .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
      // `auth.not_permitted` is the code the authorisation result handler publishes for every refused
      // policy in this API, with exactly this detail.
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        problemOf(
          403,
          'auth.not_permitted',
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const raised = notifications.notifications();
      expect(raised.length).toBe(1);
      expect(present(raised[0], 'the notification').severity)
        .withContext('403 is one of the four statuses that soften to a warning')
        .toBe('warning');
      expect(present(raised[0], 'the notification').message).toContain(
        'not permitted to perform this operation',
      );
    });

    it('shows the server\'s per-field message beside the field it names', () => {
      load([entry(declaration({ propertyName: 'FirstName', required: true }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'John';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      // A model-state refusal, complete. `request.invalid` is the status vocabulary's own default code
      // for a 400 and is what the model-binding path publishes; the title is the framework's fixed
      // sentence for this one document shape; and the per-field map is Pascal-cased because its keys
      // name model members rather than JSON members, so the camel-case body policy does not reach them.
      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush(
        {
          type: 'urn:dnnmigration:error:request.invalid',
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The request could not be processed as submitted.',
          traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
          correlationId: '6e2b0c94-3f57-4a81-b2d6-84c1f9e50a7b',
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

  // ---------------------------------------------------------------------------
  // A VALUE THAT IS PRESENT IS NEVER REPLACED BY A DEFAULT
  // ---------------------------------------------------------------------------
  //
  // The seeding rule decided on the audit timestamp alone: no timestamp meant "never recorded", so the
  // declaration's default was seeded. This screen submits EVERY property it renders, so a property that
  // arrived carrying content but no timestamp was rendered as the default and the operator's next save
  // wrote that default straight over the content — silent loss, on a screen that looked as though it had
  // loaded correctly.
  //
  // The current projection cannot produce that combination: the entity's column is `NOT NULL` and the
  // mapper emits `stored?.LastUpdatedDate`, so a null timestamp means precisely "no row". These cases are
  // therefore about the ORDER OF EVIDENCE — a value present outweighs metadata about it — which costs
  // nothing today and forecloses an unrecoverable failure if the contract ever changes shape.
  describe('seeding when the audit metadata disagrees with the value', () => {
    it('keeps a supplied value even with no recorded timestamp', () => {
      load([
        entry(declaration({ defaultValue: 'Ms.' }), {
          propertyValue: 'Dr.',
          lastUpdatedDate: null,
        }),
      ]);

      // ⚠ 'Dr.', NOT 'Ms.'. Seeding the default here is what the operator's next save would then persist.
      expect(present(controls()[0], 'the value control').value).toBe('Dr.');
    });

    it('still seeds the default when nothing at all was supplied', () => {
      load([
        entry(declaration({ defaultValue: 'Ms.' }), { propertyValue: '', lastUpdatedDate: null }),
      ]);

      // No row and no value: there is nothing to lose, and the legacy behaviour is to offer the default.
      expect(present(controls()[0], 'the value control').value).toBe('Ms.');
    });

    it('keeps a deliberately cleared value that carries a timestamp', () => {
      load([
        entry(declaration({ defaultValue: 'Ms.' }), {
          propertyValue: '',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      // A stored empty string is a value. Repopulating it would undo the operator's own clearing every
      // time the screen was opened.
      expect(present(controls()[0], 'the value control').value).toBe('');
    });
  });

  // ---------------------------------------------------------------------------
  // THE WRITE'S OWN PROPERTY LIMIT
  // ---------------------------------------------------------------------------
  //
  // The endpoint refuses a profile write carrying more than sixty-four properties, and this screen submits
  // every declared property on every save because the write replaces rather than merges. So for a tenant
  // declaring more than that, NO save on this screen can succeed — and the operator used to discover it by
  // filling the form in and being refused whole, with nothing to say which end was at fault.
  describe('a tenant declaring more properties than one write may carry', () => {
    /** One entry per declaration, each with its own identifier and view order. */
    function manyProperties(count: number): readonly UserProfileValue[] {
      return Array.from({ length: count }, (unused, index) =>
        entry(
          declaration({
            propertyDefinitionId: index + 1,
            propertyName: `Property${String(index + 1)}`,
            viewOrder: index,
          }),
        ),
      );
    }

    it('withholds the form and says which numbers are in play', () => {
      load(manyProperties(65));

      // No controls at all: offering them would invite typing into something that cannot be saved.
      expect(controls().length).toBe(0);

      const notice = host().querySelector('app-empty-state')?.textContent ?? '';
      expect(notice).toContain('65');
      expect(notice).toContain('64');
      expect(notice).toContain('Profile Properties');
    });

    it('renders the form at exactly the bound, because the bound is inclusive', () => {
      load(manyProperties(64));

      // A client limit stricter than the endpoint's would withhold a form that saves perfectly well.
      expect(controls().length).toBe(64);
    });

    it('refuses to write even if a submission is raised', () => {
      load(manyProperties(65));

      const form = host().querySelector('form');

      // The branch withholds the form, so there is nothing to submit through — which is the primary
      // protection. The component's own guard is the second, for a submission raised while a re-read is in
      // flight, and it is asserted by the absence of any write here.
      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      expect(httpMock.match((request) => request.method === 'PUT').length).toBe(0);
    });
  });

});
