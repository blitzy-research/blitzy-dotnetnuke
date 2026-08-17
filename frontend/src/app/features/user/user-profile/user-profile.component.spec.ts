import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { API_ENDPOINTS } from '../../../core/config/api-endpoints';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type UserProfile,
  type UserProfileValue,
} from '../../../core/models/profile.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { UserDetail } from '../../../core/models/user.model';
import {
  DISCARD_CHANGES_PROMPT,
  UnsavedChangesTracker,
} from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { compileTenantPattern } from '../../../core/utils/tenant-pattern.util';
import {
  NOT_SPECIFIED_OPTION_TEXT,
  PROFILE_REMEDIATION_EXPLANATION,
  UserProfileComponent,
} from './user-profile.component';

/**
 * Specification for the dynamic profile editor. The cases below are chosen around the failure modes this
 * screen actually has rather than around its members.
 */
describe('UserProfileComponent', () => {
  let fixture: ComponentFixture<UserProfileComponent>;
  let httpMock: HttpTestingController;
  let notifications: NotificationService;

  /** The account the fixtures belong to. */
  const USER_ID = 7;

  /**
   * The address of one account, built from the shared endpoint table rather than spelled. ⚠️ BUILT, NOT
   * SPELLED, SO A ROUTE-TEMPLATE CHANGE FAILS HERE INSTEAD OF DRIFTING. Every literal in this file is
   * additionally pinned against these builders by the endpoint block below, so the two can never
   * disagree: a segment rename breaks the pin, and the pin names exactly what changed.
   *
   * @param userId The account.
   * @returns The relative address.
   */
  function accountUrl(userId: number): string {
    return API_ENDPOINTS.users.byId(userId);
  }

  /**
   * The address of one account's profile.
   *
   * @param userId The account.
   * @returns The relative address.
   */
  function profileUrl(userId: number): string {
    return API_ENDPOINTS.users.profile(userId);
  }

  /**
   * The address of the tenant's profile-declaration collection. ⚠️ PRESENT SO ITS ABSENCE FROM THE
   * NETWORK CAN BE ASSERTED. This screen deliberately does NOT read it: a declaration arrives embedded in
   * each profile entry, so fetching the list as well would be a redundant round trip for data already in
   * hand.
   */
  const profileDefinitionsUrl: string = API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection();

  /**
   * The tenant's account policy - an address this screen must NEVER reach. ⚠ HELD ONLY SO THE CASES CAN
   * PROVE IT IS UNUSED. The screen once read this address to learn whether the per-property visibility
   * control is offered, because `Profile.ascx.vb` L59-L63 computed that from `Profile_DisplayVisibility`.
   */
  const membershipSettingsUrl: string = API_ENDPOINTS.users.membershipSettings();

  /**
   * The reason phrase the API publishes as the problem `title`, keyed by status. ⚠️ NOT FREE TEXT. A
   * refusal reaches the wire through one shared problem factory that fills the title from a status-keyed
   * vocabulary and fills an unspecified type from the same vocabulary's default code - so a document
   * carrying a bespoke title such as `'Server error'`, or carrying no `type` at all, describes no
   * response this server can produce.
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
   * A problem document as this API publishes one: complete, coherent and emittable. There is deliberately
   * no `instance` member - every call site supplies null for it and the framework's problem type omits a
   * null one per member - and both identifiers are present, because the pipeline attaches both.
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
   * Builds a property declaration, defaulting every member so a case states only what it is about.
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
    isOnline: false,
    mustChangePassword: false,
    createdDate: '2024-01-05T09:15:00Z',
    lastLoginDate: '2024-03-02T11:40:00Z',
    lastActivityDate: '2024-03-02T11:52:00Z',
    lastLockoutDate: null,
    lastPasswordChangeDate: '2024-01-05T09:20:00Z',
    roles: ['Registered Users'],
    canDelete: true,
  };

  /**
   * Answers both reads the screen dispatches and renders the result.
   *
   * @param properties The profile entries to return.
   * @param userId The account being read.
   */
  function respond(
    properties: readonly UserProfileValue[],
    userId: number = USER_ID,
    displayVisibilityEnabled = true,
  ): void {
    const profile: UserProfile = { userId, properties, displayVisibilityEnabled };

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
  function load(
    properties: readonly UserProfileValue[],
    userId: number = USER_ID,
    displayVisibilityEnabled = true,
  ): void {
    fixture.componentRef.setInput('userId', String(userId));
    fixture.detectChanges();
    respond(properties, userId, displayVisibilityEnabled);
  }

  /**
   * The help text rendered for one named property. `app-form-field` paints `.form-field__help` only while
   * its disclosure is expanded, so the toggle is pressed first.
   *
   * @param propertyName The declared property name.
   * @returns The help text, trimmed, or the empty string when no affordance is offered.
   */
  function helpFor(propertyName: string): string {
    const fields = Array.from(host().querySelectorAll<HTMLElement>('app-form-field'));
    const field = fields.find((candidate) => candidate.textContent?.includes(propertyName) === true)
      ?? fields[0];
    const toggle = field?.querySelector<HTMLButtonElement>('.form-field__help-toggle');

    if (toggle === null || toggle === undefined) {
      return '';
    }

    toggle.click();
    fixture.detectChanges();

    return field?.querySelector<HTMLElement>('.form-field__help')?.textContent?.trim() ?? '';
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
      // The real client first, then the testing backend that displaces it. Reversing the order leaves the
      // live backend in place and every expectation times out against a request nothing intercepted.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(UserProfileComponent);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  /**
   * Seats the caller's identity in the stored session. The identity is READ FROM THE STORED SESSION
   * rather than fetched, so seating it is what decides whether the caller is the subject of the profile -
   * the second half of the legacy `ShowVisibility` predicate.
   *
   * @param userId The account the caller is signed in as.
   */
  function seatIdentity(userId: number): void {
    TestBed.inject(TokenStorageService).store({
      accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
      expiresAtUtc: '2099-12-31T23:59:59.000Z',
      refreshToken: 'not-a-real-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: {
        userId,
        portalId: 0,
        portalName: 'Baseline Portal',
        username: 'caller',
        displayName: 'The Caller',
        email: 'caller@example.test',
        isSuperUser: false,
        isPortalAdministrator: false,
        roles: ['Registered Users'],
        permissions: [],
      },
    });
  }

  afterEach(() => {
    // The session is cleared so one case's signed-in caller cannot decide another's affordances.
    TestBed.inject(TokenStorageService).clear();

    httpMock.verify();
  });

  describe('construction', () => {
    it('creates', () => {
      fixture.detectChanges();

      expect(fixture.componentInstance).toBeTruthy();
    });

    it('dispatches nothing account-scoped until the route supplies an account', () => {
      fixture.detectChanges();

      expect(httpMock.match(() => true).length).toBe(0);
    });
  });

  describe('the route contract', () => {
    it('accepts the account through an input named exactly userId', () => {
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
      reads[1]?.flush({
        data: { userId: 0, properties: [], displayVisibilityEnabled: true },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });

    it('dispatches nothing for a value that is not a whole number', () => {
      fixture.componentRef.setInput('userId', 'not-an-id');
      fixture.detectChanges();

      // Every request is counted. Nothing this screen dispatches is route-independent, so a
      // malformed identifier must leave the queue completely empty.
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
        data: { userId: 8, properties: [], displayVisibilityEnabled: true },
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

    /**
     * `ManageUsers.ascx.vb` L259-L262 chose the heading on `IsUser And IsProfile`, and `UserModuleBase.vb`
     * L350-L371 shows `IsProfile` already implies `IsUser`, so the rule reduces to: THE PROFILE SCREEN, SEEN
     * BY ITS OWN OWNER, RENDERED NO TITLE ROW AND THEREFORE NO IDENTIFIER. We keep the heading, because a
     * routed screen needs an accessible name, and withhold only the identifier.
     *
     * The pair of cases below is what gives this coverage teeth: dropping the identifier unconditionally
     * would satisfy the first and break the second, and appending it unconditionally does the reverse.
     */
    it("withholds the record identifier from the account's own owner", () => {
      seatIdentity(USER_ID);

      load([entry(declaration())]);

      const header = present(host().querySelector('app-page-header'), 'the page header').textContent ?? '';
      expect(header).toContain('Edit Profile - jsmith');
      expect(header).not.toContain('(Id:');
      expect(header).not.toContain('Id: 7');
    });

    it('still discloses the identifier to an administrator viewing somebody else', () => {
      // A DIFFERENT caller: the identifier is the administrative detail that separates two accounts sharing
      // a display name, so it is disclosed to the administrator who needs it.
      seatIdentity(USER_ID + 1);

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

    it('renders no group, no field and no update action', () => {
      // A tenant declaring nothing is a legitimate configuration rather than a failure, so the affordances
      // are ABSENT FROM THE DOCUMENT rather than hidden by a stylesheet: a control that merely looks inert
      // is still reachable by keyboard and still announced.
      load([]);

      expect(host().querySelector('fieldset')).toBeNull();
      expect(host().querySelector('legend')).toBeNull();
      expect(controls().length).toBe(0);
      expect(host().querySelector('.user-profile__actions')).toBeNull();
      expect(host().querySelector('button[type="submit"]')).toBeNull();
      // Not an error, so no banner content and nothing announced.
      expect((present(host().querySelector('app-error-banner'), 'the banner').textContent ?? '').trim())
        .toBe('');
      expect(notifications.notifications().length).toBe(0);
    });

    it('names the screen that resolves it, in prose rather than as a link', () => {
      load([]);

      const message = present(host().querySelector('app-empty-state'), 'the empty state').textContent ?? '';
      expect(message).toContain('Profile Properties');
      expect(present(host().querySelector('app-empty-state'), 'the empty state').querySelector('a')).toBeNull();
    });

    it('renders the empty state in view mode too, where there is likewise nothing to read', () => {
      fixture.componentRef.setInput('mode', 'view');
      load([]);

      expect(host().querySelector('app-empty-state')).not.toBeNull();
      expect(host().querySelector('dl')).toBeNull();
    });
  });

  describe('when the address names no readable account', () => {
    const UNREADABLE_IDENTIFIER = 'abc';

    const NO_USER_SENTENCE = "This account doesn't exist";
    const NO_PROPERTIES_OPENING = 'This site declares no profile properties';

    /** Puts an unparseable identifier on the route and renders. */
    function arriveAtUnreadableAddress(): void {
      fixture.componentRef.setInput('userId', UNREADABLE_IDENTIFIER);
      fixture.detectChanges();
    }

    it('states that the account does not exist', () => {
      arriveAtUnreadableAddress();

      expect(
        (present(host().querySelector('.user-profile__notice'), 'the notice').textContent ?? '').trim(),
      ).toBe(NO_USER_SENTENCE);
    });

    it('does not claim the tenant declares no profile property', () => {
      // THE DEFECT, ASSERTED NEGATIVELY. Scoped to the whole rendered document rather than to the
      // empty-state element, so the claim cannot reappear anywhere else on the screen.
      arriveAtUnreadableAddress();

      expect(host().textContent ?? '').not.toContain(NO_PROPERTIES_OPENING);
      expect(host().querySelector('app-empty-state')).toBeNull();
    });

    it('offers no spinner, because no request is ever going to be made', () => {
      // The branch sits above the loading branch on purpose. A spinner would promise a request that
      // cannot be issued, leaving the screen waiting forever on an address that is already answered.
      arriveAtUnreadableAddress();

      expect(host().querySelector('app-loading-spinner')).toBeNull();
      expect(httpMock.match(() => true).length).toBe(0);
    });

    it('offers no form, no control and no submit action', () => {
      // ABSENT FROM THE DOCUMENT rather than disabled: a control that merely looks inert is still
      // reachable by keyboard and still announced, and there is no account here for it to write to.
      arriveAtUnreadableAddress();

      expect(host().querySelector('form')).toBeNull();
      expect(controls().length).toBe(0);
      expect(host().querySelector('button[type="submit"]')).toBeNull();
      expect(host().querySelector('.user-profile__actions')).toBeNull();
    });

    it('says it once, and not also as a banner or a toast', () => {
      // One fault, one presentation. An unreadable address is a statement about the address and not a
      // transport failure, so the banner stays empty and nothing is announced.
      arriveAtUnreadableAddress();

      expect((present(host().querySelector('app-error-banner'), 'the banner').textContent ?? '').trim())
        .toBe('');
      expect(notifications.notifications().length).toBe(0);
    });

    it('still gives the declared-nothing tenant its OWN sentence, not the account one', () => {
      // NEGATIVE CONTROL. Without this, every assertion above would also pass if the new branch had
      // swallowed the empty-properties case as well - trading one wrong sentence for another. A READABLE
      // identifier whose tenant declares nothing must still be told about the tenant.
      load([]);

      const emptyState = present(host().querySelector('app-empty-state'), 'the empty state');
      expect(emptyState.textContent ?? '').toContain(NO_PROPERTIES_OPENING);
      expect(host().textContent ?? '').not.toContain(NO_USER_SENTENCE);
      expect(host().querySelector('.user-profile__notice')).toBeNull();
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

    // ⚠ THIS BLOCK PREVIOUSLY ASSERTED THAT A `visible: false` PROPERTY IS RENDERED TO EVERYONE, and
    // that claim was wrong about legacy in one direction only. `Profile.ascx.vb` L164-L168 is
    // `For Each ... If IsAdmin Then profProperty.Visible = True` with NO else arm, and the untouched
    // declaration then reached `FieldEditorControl.Visible` (L963) - an ASP.NET server control property,
    // so the field rendered NOTHING for the account's own owner. The intent worth keeping is that an
    // ADMINISTRATOR sees everything; what is corrected is who else does.
    /** Seats a caller who administers the tenant, which is the legacy `IsAdmin` predicate. */
    function seatAdministrator(): void {
      TestBed.inject(TokenStorageService).store({
        accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
        expiresAtUtc: '2099-12-31T23:59:59.000Z',
        refreshToken: 'not-a-real-refresh-token',
        mustChangePassword: false,
        mustUpdateProfile: false,
        passwordExpiring: false,
        user: {
          userId: 999,
          portalId: 0,
          portalName: 'Baseline Portal',
          username: 'administrator',
          displayName: 'The Administrator',
          email: 'admin@example.test',
          isSuperUser: true,
          isPortalAdministrator: true,
          roles: ['Administrators'],
          permissions: [],
        },
      });
    }

    /** The two declarations every case in this group shares. */
    function visibleAndHidden(overrides: Partial<ProfilePropertyDefinition> = {}): readonly UserProfileValue[] {
      return [
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', visible: true, viewOrder: 1 })),
        entry(
          declaration({ propertyDefinitionId: 2, propertyName: 'LastName', visible: false, viewOrder: 2, ...overrides }),
          overrides.required === true
            ? { propertyValue: '', lastUpdatedDate: null }
            : { propertyValue: 'Smith' },
        ),
      ];
    }

    it('renders every property to an administrator, including one marked not visible', () => {
      seatAdministrator();
      load(visibleAndHidden());

      expect(controls().length).toBe(2);
      expect(labels()).toEqual(['First Name', 'Last Name']);
    });

    it("withholds a property marked not visible from the account's own owner", () => {
      load(visibleAndHidden());

      expect(controls().length).toBe(1);
      expect(labels()).toEqual(['First Name']);
    });

    it('says how many properties it is withholding, so the form does not read as the whole profile', () => {
      load(visibleAndHidden());

      const note = host().querySelector<HTMLElement>('.user-profile__withheld');

      expect(note).not.toBeNull();
      expect(note?.textContent).toContain('1 further profile detail');
      expect(note?.textContent).toContain('unchanged');
    });

    // ⚠ THE DEADLOCK GUARD. `ProfileController.ValidateProfile` (L305-L319) and the port's
    // `RequiresProfileCompletionAsync` BOTH gate on `Required` with no reference to `Visible`, so a
    // property declared required and not visible made the gate unsatisfiable for ever while the editor
    // rendered no control to satisfy it. Honouring the declaration without this exception reproduces a
    // deadlock legacy shipped.
    it('shows a property marked not visible when it is required and unmet, because the site gates on it', () => {
      load(visibleAndHidden({ required: true }));

      expect(controls().length).toBe(2);
      expect(labels()).toEqual(['First Name', 'Last Name *required']);
    });

    // ⚠ THE DATA-LOSS GUARD, AND THE REASON WITHHOLDING IS NOT THE SAME AS OMITTING.
    // `UserService.UpdateProfileAsync` replaces the whole answer set and writes `Cleared(value)` for every
    // stored value the submission omits, so a withheld property left out of the payload is DESTROYED.
    it('carries a withheld property verbatim in the write, so saving does not destroy it', () => {
      load(visibleAndHidden());

      const first = controls()[0];
      first.value = 'Jane';
      first.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === profileUrl(USER_ID),
      );
      const body = written.request.body as { properties: readonly { propertyDefinitionId: number; propertyValue: string }[] };

      expect(body.properties.length).toBe(2);
      expect(body.properties.find((property) => property.propertyDefinitionId === 2)?.propertyValue).toBe('Smith');

      written.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // U12, U14 AND U15 - WHAT THE FORM TELLS THE OPERATOR BEFORE IT REFUSES THEM
  // ---------------------------------------------------------------------------------------------------

  describe('what the declaration discloses', () => {
    /** The reach entries, in document order. */
    function outstanding(): readonly HTMLButtonElement[] {
      return Array.from(host().querySelectorAll<HTMLButtonElement>('.user-profile__outstanding-link'));
    }

    // ⚠ THE MEASURED DEFECT. A mandatory property declared last rendered as the final control roughly
    // 2400px below the fold, and NOTHING above the fold said it was outstanding. Focus already moves to
    // the first invalid control on a refused submit; what was missing was any way to reach it before
    // submitting, which is the moment the operator needs it.
    it('lists an unmet required property so it can be reached without hunting for it', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'Jane',
        }),
        entry(
          declaration({ propertyDefinitionId: 2, propertyName: 'Telephone', required: true, viewOrder: 99 }),
        ),
      ]);

      expect(outstanding().length).toBe(1);
      expect(outstanding()[0].textContent?.trim()).toBe('Telephone');
    });

    it('moves focus to the control the reach entry names', () => {
      load([
        entry(
          declaration({ propertyDefinitionId: 2, propertyName: 'Telephone', required: true, viewOrder: 99 }),
        ),
      ]);

      const control = controls()[0];
      outstanding()[0].click();
      fixture.detectChanges();

      expect(document.activeElement).toBe(control);
    });

    it('withdraws the reach entry once the requirement is met', () => {
      load([
        entry(
          declaration({ propertyDefinitionId: 2, propertyName: 'Telephone', required: true, viewOrder: 99 }),
        ),
      ]);

      const control = controls()[0];
      control.value = '+1 555 0100';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(outstanding().length).toBe(0);
    });

    // ⚠ U15 - THE RULE IS NOT DEAD, IT WAS MERELY NEVER STATED. `UserService.ValidateProfileValue`
    // enforces the declared expression server-side through a cached, length-bounded matcher with a 50ms
    // timeout, so a value breaking it IS refused - the operator just had no way to know the rule existed
    // until the refusal arrived. Reporting the binding limit first is the same principle this workspace
    // already applies to the credential rules and to the search advisory.
    it('states the declared format requirement before a save can be refused for breaking it', () => {
      load([
        entry(
          declaration({
            propertyDefinitionId: 9,
            propertyName: 'CustomCode',
            length: 20,
            validationExpression: '^[0-9]{5}$',
          }),
        ),
      ]);

      const help = helpFor('CustomCode');

      expect(help).toContain('at most 20 characters');
      expect(help).toContain('a specific format this site requires');
    });

    // ⚠ U12b - A TENANT-DECLARED PROPERTY CARRIES NO CURATED WORDING, so it previously rendered no help
    // affordance whatsoever even though its declaration bounded its length.
    it('gives a property with no curated wording a help affordance drawn from its declaration', () => {
      load([entry(declaration({ propertyDefinitionId: 9, propertyName: 'RequiredHiddenProp', length: 50 }))]);

      expect(helpFor('RequiredHiddenProp')).toBe('Accepts at most 50 characters.');
    });

    it('bounds nothing and says nothing when the declaration bounds nothing', () => {
      load([
        entry(
          declaration({ propertyDefinitionId: 9, propertyName: 'Unbounded', length: 0, validationExpression: null }),
        ),
      ]);

      expect(host().querySelector('.form-field__help-toggle')).toBeNull();
    });

    // ⚠ U14 - Two accounts whose stored answer serialises identically as "" DID render differently,
    // because only a row that was never written is seeded from the declared default. The distinction is
    // correct; what was missing was any way to see which of the two you were looking at.
    it('marks a value seeded from the declared default as not yet the account\'s own', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'Website', defaultValue: 'https://example.test' })),
      ]);

      expect(controls()[0].value).toBe('https://example.test');
      expect(host().querySelector('.user-profile__seeded')?.textContent).toContain('Suggested by this site');
    });

    it('does not mark a stored blank as seeded, because that answer IS the account\'s own', () => {
      load([
        entry(
          declaration({ propertyDefinitionId: 1, propertyName: 'Website', defaultValue: 'https://example.test' }),
          { propertyValue: '', lastUpdatedDate: '2024-05-01T10:00:00Z' },
        ),
      ]);

      expect(controls()[0].value).toBe('');
      expect(host().querySelector('.user-profile__seeded')).toBeNull();
    });

    it('withdraws the seeded remark once the operator edits the value', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'Website', defaultValue: 'https://example.test' })),
      ]);

      const control = controls()[0];
      control.value = 'https://mine.test';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.user-profile__seeded')).toBeNull();
    });
  });

  describe('the declared length bound', () => {
    it('applies NO maximum-length rule when the declared length is zero', () => {
      // THE MOST CONSEQUENTIAL CASE IN THIS FILE. Zero is the column default and means "no bound"; treating
      // it as a bound of nothing invalidates every control on every screen and no operator can save
      // anything at all.
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

  // THE TENANT'S VALIDATION EXPRESSION, RUN HERE WHEN IT IS SAFE TO RUN
  //
  // ⚠ THIS BLOCK HAS NOW ASSERTED BOTH ANSWERS, AND NEITHER OF THE FIRST TWO WAS RIGHT. It originally required
  // the expression to be compiled and executed on the UI thread on every keystroke, which is a real
  // vulnerability: the expression is administrator-authored data, `RegExp` cannot be given a time limit, and a
  // declared length of zero means the input it runs against is unbounded. It was then changed to require that
  // the expression never be evaluated in the browser at all — which removed the vulnerability and, measured at
  // runtime, removed the rule with it, because the server's refusal was published as a flat problem document
  // that no control could be attached to.
  //
  // The contract asserted below is the one that holds both properties at once: the expression IS evaluated
  // here when a static screen shows it cannot backtrack catastrophically, and is left to the server — which
  // has a linear-time engine and a real timeout — when it cannot.
  describe('the declared validation pattern', () => {
    it('evaluates a safe stored expression in the browser', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'letters';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      // A value the expression plainly refuses, reported beside the box rather than a round trip later.
      expect((present(host().querySelector('.form-field__error'), 'the message').textContent ?? '').trim())
        .toContain('does not match the format it requires');
    });

    it('blocks a submit carrying a value a safe stored expression refuses', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'letters';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectNone((request) => request.method === 'PUT');
    });

    it('submits a value a safe stored expression accepts', () => {
      load([entry(declaration({ validationExpression: '^[0-9]*$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = '42';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === `/api/v1/users/${USER_ID}/profile`,
      );
      const carried = written.request.body as {
        readonly properties: readonly { readonly propertyValue: string }[];
      };
      expect(present(carried.properties[0], 'the submitted property').propertyValue).toBe('42');

      written.flush(null);
      httpMock
        .match(() => true)
        .forEach((outstanding) =>
          outstanding.flush({ data: { userId: USER_ID, properties: [] } }),
        );
      fixture.detectChanges();
    });

    it('renders the screen for an expression no engine could compile', () => {
      // An uncompilable expression is a handover to the server rather than a client-side error, so it must not
      // throw and must not stop the screen rendering. This is the shape of stored data most likely to be
      // present, which is why the case is kept.
      expect(() =>
        load([entry(declaration({ validationExpression: '([unclosed' }))]),
      ).not.toThrow();

      expect(controls().length).toBe(1);
    });

    it('accepts any value when the stored expression could not be compiled', () => {
      load([entry(declaration({ validationExpression: '([unclosed' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'anything at all';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.form-field__error')).toBeNull();
    });

    it('does not run an expression that could backtrack catastrophically', () => {
      // ⚠ THE ONE CASE THAT KEEPS THE SAFETY PROPERTY HONEST. `^(a+)+$` is an ordinary thing for an
      // administrator to type and takes exponential time on a non-matching input. Silence here is correct
      // behaviour, not the regression it would be for `^[0-9]*$`, and the value must still be submittable so
      // that the server gets its chance to refuse under a timeout.
      load([entry(declaration({ validationExpression: '^(a+)+$' }))]);

      const control = present(controls()[0], 'the value control');
      control.value = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(host().querySelector('.form-field__error')).toBeNull();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock
        .expectOne((request) => request.method === 'PUT')
        .flush(null, { status: 204, statusText: 'No Content' });

      httpMock
        .match(() => true)
        .forEach((outstanding) =>
          outstanding.flush({ data: { userId: USER_ID, properties: [] } }),
        );
      fixture.detectChanges();
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
      // ⚠ A TENANT-AUTHORED NAME, DELIBERATELY. The seeded rich-text property gets a multi-line control by
      // NAME as well as by length, so naming it here would leave this case unable to say which of the two
      // rules produced the box.
      load([entry(declaration({ propertyName: 'ProjectSummary', length: 3750 }))]);

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
      // The shared field owns the error region and gives it `role="alert"`, but it cannot mark a projected
      // control. Without `aria-invalid` a screen-reader user hears the message and finds nothing on the
      // field identifying it as the one at fault.
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

      request.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne(`/api/v1/users/${USER_ID}/profile`)
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
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
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });

    it('does NOT submit an invalid form, so the administrator bypass is not reproduced', () => {
      // MIGRATION: THE LEGACY ADMINISTRATOR VALIDATION BYPASS IS DELIBERATELY NOT REPRODUCED.
      // `Profile.ascx.vb` L94-L104 read `If ProfileProperties.IsValid Or IsAdmin Then _IsValid = True`, so
      // an administrator was considered valid unconditionally and every declared rule - required, length,
      // pattern - was skipped for them.
      load([entry(declaration({ required: true }))]);

      submit();

      expect(httpMock.match(`/api/v1/users/${USER_ID}/profile`).length).toBe(0);
    });

    it('reveals the messages for controls the operator never visited', () => {
      load([entry(declaration({ required: true }))]);

      submit();

      expect(host().querySelector('.form-field__error')).not.toBeNull();
    });

    // ⚠ THESE THREE REPLACE ONE SPEC THAT ASSERTED THE OPPOSITE, AND THE EARLIER ANSWER WAS WRONG RATHER
    // THAN MERELY DIFFERENT. It required Cancel to restore the arrival values in silence, which is exactly
    // the behaviour measured as a defect: the sidebar and the browser's Back button both refused to leave
    // this screen until the operator confirmed, while the Cancel button sitting between them discarded the
    // same unsaved entry without asking. A control cannot be the quiet exception to a promise the two
    // controls beside it keep.
    //
    // The question is put through `globalThis.confirm`, which is what the departure gate has always used, so
    // it is stubbed here for a second reason beyond observing it: an unstubbed native dialog blocks the
    // renderer, and a blocked renderer cannot answer the runner's pings - the whole file died mid-run on a
    // ping timeout until this was stubbed.
    it('asks before discarding, and restores the arrival values once the operator accepts', () => {
      const asked = spyOn(globalThis, 'confirm').and.returnValue(true);
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      const control = present(controls()[0], 'the value control');
      control.value = 'edited';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      cancel();
      fixture.detectChanges();

      // The SAME sentence the departure gate puts, read from the export rather than restated, so the two
      // ways out of this screen cannot drift into asking differently for the same thing.
      expect(asked).toHaveBeenCalledWith(DISCARD_CHANGES_PROMPT);
      // Possible in one call only because every control is non-nullable: resetting one
      // returns it to its construction value rather than to null.
      expect(present(controls()[0], 'the value control').value).toBe('John');
    });

    it('keeps the typed value when the operator refuses to discard it', () => {
      spyOn(globalThis, 'confirm').and.returnValue(false);
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      const control = present(controls()[0], 'the value control');
      control.value = 'edited';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      cancel();
      fixture.detectChanges();

      expect(present(controls()[0], 'the value control').value).toBe('edited');
    });

    it('does not put the question at all when there is nothing to discard', () => {
      const asked = spyOn(globalThis, 'confirm').and.returnValue(true);
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      cancel();
      fixture.detectChanges();

      expect(asked).not.toHaveBeenCalled();
      expect(present(controls()[0], 'the value control').value).toBe('John');
    });

    /** Presses the in-form Cancel control, named here so the three specs above read as one contract. */
    function cancel(): void {
      const buttons = Array.from(
        host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'),
      );

      present(buttons[1], 'the cancel action').click();
    }

  });

  describe('the visibility affordance', () => {
    // ⚠ TWO SUCCESSIVE REVISIONS LEFT THIS AFFORDANCE INERT, AND EACH FAILED DIFFERENTLY. The first took it
    // from an input alone, which no route supplies, so the routed screen never offered the control whatever
    // the tenant had configured.

    it('is not offered while the tenant policy is unresolved, whatever the caller is', () => {
      // The conservative posture: offering a control that then disappears is worse than offering it a
      // moment late, so an unresolved policy reads as "not offered" rather than as a stored false.
      seatIdentity(USER_ID);
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      expect(host().querySelector('select'))
        .withContext('the policy arrives with the profile, so nothing is known of it yet')
        .toBeNull();

      // The reads are answered rather than left outstanding, both so teardown verifies an empty
      // queue and so this case proves the resolution as well as the posture before it.
      respond([entry(declaration())]);

      expect(host().querySelector('select'))
        .withContext('once the profile arrives the resolved policy is honoured')
        .not.toBeNull();
    });

    it('is not offered to a caller who is not the subject of the profile, even with the policy on', () => {
      // ⚠ THE SECOND HALF OF THE LEGACY PREDICATE, AND WHY IT MATTERS. Visibility is a choice the account
      // holder makes about their OWN data.
      load([entry(declaration())], USER_ID, true);

      expect(host().querySelector('select'))
        .withContext('no session is signed in here, so the caller is not the subject')
        .toBeNull();
    });

    it('is not offered when the tenant switched the policy off', () => {
      seatIdentity(USER_ID);
      load([entry(declaration())], USER_ID, false);

      expect(host().querySelector('select'))
        .withContext('the tenant policy alone withholds the control from the subject')
        .toBeNull();
    });

    it('is offered to the subject of the profile when the tenant enabled the policy', () => {
      seatIdentity(USER_ID);
      load([entry(declaration())], USER_ID, true);

      expect(host().querySelector('select'))
        .withContext('the subject reads the policy from its own profile, needing no admin endpoint')
        .not.toBeNull();
    });

    it('is withheld from a signed-in caller who is a different account', () => {
      // The same policy, a real session, a DIFFERENT account. This is the administrator case, and it
      // is the one the second half of the predicate exists for.
      seatIdentity(USER_ID + 1);
      load([entry(declaration())], USER_ID, true);

      expect(host().querySelector('select')).toBeNull();
    });

    it('is offered when a caller asks for it', () => {
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('lets the caller override force it on, and never lets a false override force it off', () => {
      // The input is an OVERRIDE for an embedding caller, not the routed behaviour: it can only turn the
      // affordance on. A false defers to the resolved answer rather than suppressing it, which is what
      // stops an embedding context silently overriding a tenant that enabled the policy.
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())], USER_ID, false);

      expect(host().querySelector('select'))
        .withContext('the override asserts the affordance even against a policy that is off')
        .not.toBeNull();
    });

    it('costs no request of its own, and never the administrator-only account settings', () => {
      seatIdentity(USER_ID);
      load([entry(declaration()), entry(declaration({ propertyDefinitionId: 88, propertyName: 'City' }))]);

      expect(host().querySelector('select'))
        .withContext('the affordance is offered on the strength of the profile alone')
        .not.toBeNull();
      expect(httpMock.match(membershipSettingsUrl).length)
        .withContext('the administrator-only account settings are not read')
        .toBe(0);

      fixture.componentRef.setInput('userId', '8');
      fixture.detectChanges();

      // ⚠ ONE `match` FOR THE WHOLE QUEUE, NOT A FILTERED ONE FOLLOWED BY A TOTAL. `match` REMOVES what it
      // returns, so a filtered call followed by a broader one would inspect an already drained queue and
      // pass on an empty result.
      const reads = httpMock.match(() => true);

      expect(reads.map((request) => request.request.url))
        .withContext('the account and its profile, and nothing else - no tenant policy read')
        .toEqual(['/api/v1/users/8', '/api/v1/users/8/profile']);

      reads[0]?.flush({ data: account, meta: null });
      reads[1]?.flush({ data: { userId: 8, properties: [], displayVisibilityEnabled: true }, meta: null });
      fixture.detectChanges();
    });

    it('withholds the control for the second account when that profile says the policy is off', () => {
      // ⚠ THE POLICY IS RE-STATED BY EVERY PROFILE, so a route move re-resolves it rather than carrying the
      // first account's answer forward.
      seatIdentity(USER_ID);
      load([entry(declaration())], USER_ID, true);

      expect(host().querySelector('select')).not.toBeNull();

      fixture.componentRef.setInput('userId', '8');
      fixture.detectChanges();

      const reads = httpMock.match((request) => request.url.startsWith('/api/v1/users/8'));

      reads[0]?.flush({ data: account, meta: null });
      reads[1]?.flush({
        data: {
          userId: 8,
          properties: [entry(declaration())],
          displayVisibilityEnabled: false,
        },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();

      expect(host().querySelector('select'))
        .withContext('the second profile states the policy is off, so the control is withheld')
        .toBeNull();
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

    it('renders values as read-only text rather than as inert controls', () => {
      // ⚠️ READ-ONLY, NOT DISABLED, AND NOT MERELY STYLED INERT. A disabled control is removed from the
      // accessibility tree AND from the form's value, so a screen-reader user would be told the profile is
      // empty; a control that only LOOKS inert invites an edit that cannot be saved.
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

      expect(host().querySelector('input')).toBeNull();
      expect(host().querySelector('textarea')).toBeNull();
      expect(host().querySelector('select')).toBeNull();
      expect(host().querySelector('[disabled]')).toBeNull();
      expect(host().querySelector('[aria-disabled="true"]')).toBeNull();

      // The association between a name and its value is structural, so it survives with no styling
      // at all - which is what makes it readable by a screen reader.
      expect(Array.from(host().querySelectorAll('dt')).map((term) => (term.textContent ?? '').trim()))
        .toEqual(['First Name:', 'Last Name:']);
      expect(
        Array.from(host().querySelectorAll('dd')).map((value) => (value.textContent ?? '').trim()),
      ).toEqual(['John', 'Smith']);
    });

    it('offers no update action even when a caller asks for the visibility control', () => {
      // MIGRATION: `ShowUpdate="False"` is honoured STRUCTURALLY - the whole form element is absent -
      // rather than by disabling a button that is still in the document.
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);

      expect(host().querySelector('form')).toBeNull();
      expect(host().querySelector('button[type="submit"]')).toBeNull();
      expect(host().querySelector('select')).toBeNull();
    });

    it('still groups by category and reports each group\'s disclosure state', () => {
      load([
        entry(
          declaration({
            propertyDefinitionId: 1,
            propertyCategory: 'Name',
            propertyName: 'FirstName',
            viewOrder: 1,
          }),
        ),
        entry(
          declaration({
            propertyDefinitionId: 2,
            propertyCategory: 'Contact Info',
            propertyName: 'Telephone',
            viewOrder: 2,
          }),
        ),
      ]);

      expect(headings()).toEqual(['Name', 'Contact Info']);
      expect(
        Array.from(host().querySelectorAll<HTMLButtonElement>('.user-profile__toggle')).map((toggle) =>
          toggle.getAttribute('aria-expanded'),
        ),
      ).toEqual(['true', 'true']);
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

      // A model-state refusal, complete.
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

  // A VALUE THAT IS PRESENT IS NEVER REPLACED BY A DEFAULT
  // The current projection cannot produce that combination: the entity's column is `NOT NULL` and the
  // mapper emits `stored?.LastUpdatedDate`, so a null timestamp means precisely "no row".
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

  // THE WRITE'S OWN PROPERTY LIMIT
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

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      expect(httpMock.match((request) => request.method === 'PUT').length).toBe(0);
    });
  });

  // THE ADDRESSES THIS SCREEN USES
  describe('the addresses this screen uses', () => {
    it('addresses the profile relatively, through the shared endpoint table', () => {
      // Pinned against the literal every other case in this file spells, so the two can never
      // disagree: renaming a segment breaks this expectation and names what changed.
      expect(profileUrl(USER_ID)).toBe(`/api/v1/users/${USER_ID}/profile`);
      expect(accountUrl(USER_ID)).toBe(`/api/v1/users/${USER_ID}`);
    });

    it('addresses the declaration collection relatively and without a tenant parameter', () => {
      // The tenant is resolved SERVER-SIDE from the request's host against the alias table, exactly as the
      // legacy `GetPortalSettings` procedure resolved it from the alias - so no `portalId` travels in the
      // query string and none is asserted here.
      expect(profileDefinitionsUrl).toBe('/api/v1/profile-definitions');
    });

    it('carries no scheme and no host on any address it uses', () => {
      for (const address of [accountUrl(USER_ID), profileUrl(USER_ID), profileDefinitionsUrl]) {
        expect(address.startsWith('/'))
          .withContext(`${address} must be relative so the proxy can forward it`)
          .toBeTrue();
        expect(/^[a-z][a-z0-9+.-]*:/i.test(address))
          .withContext(`${address} must carry no scheme`)
          .toBeFalse();
      }
    });

    it('reads the profile and the account, and nothing else', () => {
      load([entry(declaration())]);

      expect(httpMock.match(membershipSettingsUrl).length)
        .withContext('the administrator-only account settings must not be read by this screen')
        .toBe(0);

      expect(httpMock.match(() => true).length)
        .withContext('every dispatched request has already been answered')
        .toBe(0);
    });

    it('never reads /api/v1/profile-definitions', () => {
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      expect(httpMock.match((request) => request.url === profileDefinitionsUrl).length).toBe(0);
      expect(httpMock.match((request) => request.url.includes('profile-definitions')).length).toBe(0);
    });
  });

  // THE STRUCTURAL CONTRACT THE ROUTE DEPENDS ON
  // `user.routes.ts` reaches this screen through `loadComponent`, so the exported class name is
  // load-bearing, and the selector is what the shell instantiates. Neither is checked by a compiler at the
  // point that matters, so both are pinned.
  describe('the structural contract', () => {
    /** The compiled component definition, read once. */
    function componentDef(): { readonly selectors?: readonly unknown[]; readonly onPush?: boolean } {
      const compiled = (
        UserProfileComponent as Type<UserProfileComponent> & {
          readonly ɵcmp?: { readonly selectors?: readonly unknown[]; readonly onPush?: boolean };
        }
      ).ɵcmp;

      return present(compiled, 'the compiled component definition');
    }

    it('declares the selector the shell instantiates', () => {
      expect(JSON.stringify(componentDef().selectors)).toContain('app-user-profile');
    });

    it('is exported under the name the lazy route imports', () => {
      // `loadComponent: () => import(...).then((m) => m.UserProfileComponent)` resolves BY NAME, so
      // renaming the class produces a route that loads nothing with no compile error at the route.
      expect(UserProfileComponent.name).toBe('UserProfileComponent');
    });

    it('declares the on-push strategy the non-functional requirements mandate', () => {
      expect(componentDef().onPush).toBeTrue();
    });
  });

  // THE FOUR CATEGORIES DOTNETNUKE SEEDED, AS THE INSTALLER SEEDS THEM
  describe('the four categories DotNetNuke seeds', () => {
    /**
     * One seeded declaration, exactly as `AddDefaultPropertyDefinitions` seeds it. ⚠️ THE DATA TYPE IS
     * NOT REPRODUCED, AND THAT IS DELIBERATE. The installer resolves it BY NAME out of the excluded
     * `Lists` table - `SELECT EntryID ... WHERE ListName='DataType' AND Value='Text'` - so the stored
     * integer is DATABASE-ASSIGNED and differs between installations.
     *
     * @param viewOrder The seeded display order, which doubles as the identifier here because the
     * installer's orders are distinct.
     * @param category The seeded category.
     * @param propertyName The seeded property name.
     * @param length The seeded bound: 50 for a text property, 0 for one the installer gave a specialised
     * data type, where 0 means NO bound rather than a bound of nothing.
     * @returns The profile entry, with no value recorded against it.
     */
    function seeded(
      viewOrder: number,
      category: string,
      propertyName: string,
      length: number,
    ): UserProfileValue {
      return entry(
        declaration({
          propertyDefinitionId: viewOrder,
          propertyCategory: category,
          propertyName,
          viewOrder,
          length,
          // Seeded `Required = 0` and `ValidationExpression = ''` for all nineteen.
          required: false,
          validationExpression: '',
          visible: true,
        }),
      );
    }

    /** The nineteen seeded declarations, in the installer's own order. */
    function seededProperties(): readonly UserProfileValue[] {
      return [
        seeded(1, 'Name', 'Prefix', 50),
        seeded(3, 'Name', 'FirstName', 50),
        seeded(5, 'Name', 'MiddleName', 50),
        seeded(7, 'Name', 'LastName', 50),
        seeded(9, 'Name', 'Suffix', 50),
        seeded(11, 'Address', 'Unit', 50),
        seeded(13, 'Address', 'Street', 50),
        seeded(15, 'Address', 'City', 50),
        seeded(17, 'Address', 'Region', 0),
        seeded(19, 'Address', 'Country', 0),
        seeded(21, 'Address', 'PostalCode', 50),
        seeded(23, 'Contact Info', 'Telephone', 50),
        seeded(25, 'Contact Info', 'Cell', 50),
        seeded(27, 'Contact Info', 'Fax', 50),
        seeded(29, 'Contact Info', 'Website', 50),
        seeded(31, 'Contact Info', 'IM', 50),
        // ⚠️ BIOGRAPHY IS SEEDED AT 33, AHEAD of TimeZone at 35 and PreferredLocale at 37.
        seeded(33, 'Preferences', 'Biography', 0),
        seeded(35, 'Preferences', 'TimeZone', 0),
        seeded(37, 'Preferences', 'PreferredLocale', 0),
      ];
    }

    it('seeds exactly nineteen properties, which is what the resource file accounts for', () => {
      expect(seededProperties().length).toBe(19);
    });

    it('renders one captioned group per seeded category, in the seeded order', () => {
      load(seededProperties());

      expect(headings()).toEqual(['Name', 'Address', 'Contact Info', 'Preferences']);
    });

    it('renders each group as a field set with a legend, so every group is named', () => {
      load(seededProperties());

      const groups = Array.from(host().querySelectorAll('fieldset.user-profile__group'));
      expect(groups.length).toBe(4);

      for (const group of groups) {
        // A field set whose legend is empty is announced as a group and then says nothing about
        // itself, which is worse than no grouping at all.
        expect((present(group.querySelector('legend'), 'the group legend').textContent ?? '').trim().length)
          .toBeGreaterThan(0);
      }
    });

    it('reports the disclosure state of every group, not merely the first', () => {
      load(seededProperties());

      const toggles = Array.from(
        host().querySelectorAll<HTMLButtonElement>('.user-profile__toggle'),
      );
      expect(toggles.length).toBe(4);
      expect(toggles.map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
        'true',
        'true',
        'true',
        'true',
      ]);
    });

    it('renders all nineteen fields, in the seeded display order within each group', () => {
      load(seededProperties());

      expect(controls().length).toBe(19);
      expect(labels()).toEqual([
        'Prefix',
        'First Name',
        'Middle Name',
        'Last Name',
        'Suffix',
        'Unit',
        'Street',
        'City',
        'Region',
        'Country',
        'Postal Code',
        'Telephone',
        'Cell/Mobile',
        'Fax',
        'Website',
        'IM',
        // ⚠️ MEASURED, NOT ASSUMED: Biography is seeded at view order 33 and therefore precedes
        // TimeZone (35) and PreferredLocale (37). Any other order would be an invention.
        'Biography',
        'Time Zone',
        'Preferred Locale',
      ]);
    });

    it('groups the seeded properties in the counts the installer seeds', () => {
      load(seededProperties());

      const counts = Array.from(host().querySelectorAll('fieldset.user-profile__group')).map(
        (group) => group.querySelectorAll('input[type="text"], textarea').length,
      );

      // Name 5, Address 6, Contact Info 5, Preferences 3 - and 5 + 6 + 5 + 3 = 19.
      expect(counts).toEqual([5, 6, 5, 3]);
    });

    it('marks none of the seeded properties required, because the installer marks none', () => {
      load(seededProperties());

      // Every seeded row passes `@Required = 0`. A screen that demanded any of them would refuse a
      // profile the legacy application saved without complaint.
      expect(host().querySelectorAll('.form-field__required').length).toBe(0);
      expect(host().querySelector('.form-field__error')).toBeNull();
    });

    it('bounds a seeded text property at fifty and leaves a specialised one unbounded', () => {
      load(seededProperties());

      const byLabel = new Map(
        Array.from(host().querySelectorAll('.form-field')).map((field) => [
          (field.querySelector('.form-field__label')?.textContent ?? '').replace(/\s+/g, ' ').trim(),
          field.querySelector('input[type="text"], textarea'),
        ]),
      );

      expect(present(byLabel.get('First Name'), 'the first-name control')?.getAttribute('maxlength')).toBe(
        '50',
      );
      // Seeded with a specialised data type and therefore `@Length = 0`, which is the ABSENCE of a
      // bound. Publishing `maxlength="0"` here would stop the operator typing at all.
      expect(
        present(byLabel.get('Biography'), 'the biography control')?.hasAttribute('maxlength'),
      ).toBeFalse();
    });

    it('renders the seeded rich-text property as a plain textarea, a reported reduction', () => {
      load(seededProperties());

      const biography = present(
        host().querySelector<HTMLTextAreaElement>('textarea'),
        'the biography control',
      );

      // Eighteen single-line controls and this one multi-line control: nineteen in total, so
      // the textarea REPLACES an input rather than being added beside one.
      expect(host().querySelectorAll('textarea').length).toBe(1);
      expect(host().querySelectorAll('input[type="text"]').length).toBe(18);
      expect(controls().length).toBe(19);
      expect(biography.getAttribute('rows')).withContext('a real multi-line box').toBe('12');

      // The label proves it is Biography that got the box rather than some other property.
      expect(
        (
          present(biography.closest('.form-field'), 'the biography field').querySelector(
            '.form-field__label',
          )?.textContent ?? ''
        ).trim(),
      ).toBe('Biography');

      expect(host().querySelector('[contenteditable]')).toBeNull();
      expect(host().querySelector('iframe')).toBeNull();
      expect(host().querySelector('[role="toolbar"]')).toBeNull();
      expect(host().querySelector('.ql-editor, .cke, .fck, .rich-text-editor')).toBeNull();
    });
  });

  // ---------------------------------------------------------------------------
  // THE TWO CONTROL TITLES, AND THE TWO RESOURCE FILES THEY COME FROM
  // ---------------------------------------------------------------------------
  describe('the mode-dependent control title', () => {
    it('titles the editable screen as the legacy edit page was titled', () => {
      load([entry(declaration())]);

      expect(present(host().querySelector('app-page-header'), 'the page header').textContent).toContain(
        'Manage Profile',
      );
    });

    it('titles the read-only screen as the legacy view page was titled', () => {
      // Measured as `ControlTitle_viewprofile.Text` = "View Profile" - the SINGLE entry in
      // `ViewProfile.ascx.resx`, which is a five-line page whose whole body hosts this same editor.
      fixture.componentRef.setInput('mode', 'view');
      load([entry(declaration())]);

      const header = present(host().querySelector('app-page-header'), 'the page header').textContent ?? '';
      expect(header).toContain('View Profile');
      expect(header).not.toContain('Manage Profile');
    });

    it('renders the per-record heading in both modes, as the legacy page did', () => {
      fixture.componentRef.setInput('mode', 'view');
      load([entry(declaration())]);

      expect(present(host().querySelector('app-page-header'), 'the page header').textContent).toContain(
        'Edit Profile - jsmith (Id: 7)',
      );
    });

    it('lets a caller supply the supporting line instead', () => {
      fixture.componentRef.setInput('subheading', 'Supplied by the caller');
      load([entry(declaration())]);

      const header = present(host().querySelector('app-page-header'), 'the page header').textContent ?? '';
      expect(header).toContain('Supplied by the caller');
      expect(header).not.toContain('Manage Profile');
    });
  });

  // ⚠️⚠️ TEXT FROM THE API IS UNTRUSTED MARKUP, BY MEASUREMENT
  describe('text that arrives from the API is never treated as markup', () => {
    /** A property name carrying a live script element, as four measured resource values do. */
    const SCRIPT_NAME = '<script>window.__pwned = true;</script>';

    /** A property name carrying emphasis markup, as thirty-two measured resource values do. */
    const BOLD_NAME = '<b>Bold</b>';

    it('renders a property name carrying a script element as escaped text', () => {
      load([entry(declaration({ propertyName: SCRIPT_NAME }))]);

      // The label falls back to the property name for anything the wording table does not hold, so
      // the property NAME is the untrusted path into the label.
      expect(labels()).toEqual([SCRIPT_NAME]);
      expect(host().querySelector('script')).toBeNull();
      expect(
        (host().ownerDocument as Document | null)?.querySelector('script[data-profile]') ?? null,
      ).toBeNull();
    });

    it('creates no element from a property name carrying markup', () => {
      load([entry(declaration({ propertyName: BOLD_NAME }))]);

      expect(host().querySelector('b')).toBeNull();
      // The brackets survive, which is the proof that the string was interpolated rather than parsed.
      expect(present(host().querySelector('.form-field__label'), 'the label').textContent).toContain(
        '<b>Bold</b>',
      );
    });

    it('renders a category carrying markup as an escaped legend', () => {
      load([entry(declaration({ propertyCategory: BOLD_NAME }))]);

      expect(headings()).toEqual([BOLD_NAME]);
      expect(present(host().querySelector('legend'), 'the legend').querySelector('b')).toBeNull();
    });

    it('renders a server per-field message carrying markup as escaped text', () => {
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectOne((request) => request.method === 'PUT' && request.url === profileUrl(USER_ID)).flush(
        {
          type: 'urn:dnnmigration:error:request.invalid',
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The request could not be processed as submitted.',
          traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
          correlationId: '6e2b0c94-3f57-4a81-b2d6-84c1f9e50a7b',
          // ⚠️ THE KEYS ARE .NET MODEL-STATE NAMES AND ARE PASCAL-CASED. They name model members rather
          // than JSON members, so the camel-case body policy does not reach them, and the document is read
          // with a BRACKET because property access on an index signature is a compile error in this
          // workspace.
          errors: { FirstName: ['<b>Rejected</b> by the server.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const message = present(host().querySelector('.form-field__error'), 'the error message');
      expect(message.textContent).toContain('<b>Rejected</b>');
      expect(message.querySelector('b')).toBeNull();
    });

    it('strips the leading break markup the legacy validator messages carried', () => {
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectOne((request) => request.method === 'PUT' && request.url === profileUrl(USER_ID)).flush(
        {
          type: 'urn:dnnmigration:error:request.invalid',
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The request could not be processed as submitted.',
          traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
          correlationId: '6e2b0c94-3f57-4a81-b2d6-84c1f9e50a7b',
          errors: { FirstName: ['<br>You Must Enter a Valid Name'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const message = present(host().querySelector('.form-field__error'), 'the error message');
      expect((message.textContent ?? '').trim()).toBe('You Must Enter a Valid Name');
      expect(message.textContent).not.toContain('<br>');
      expect(message.querySelector('br')).toBeNull();
    });

    it('renders a problem detail carrying markup as escaped banner text', () => {
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock
        .expectOne(accountUrl(USER_ID))
        .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
      httpMock
        .expectOne(profileUrl(USER_ID))
        .flush(
          problemOf(500, 'server.unexpected_failure', 'Failed: <script>alert(1)</script>'),
          { status: 500, statusText: 'Internal Server Error' },
        );
      fixture.detectChanges();

      const banner = present(host().querySelector('app-error-banner'), 'the banner');
      expect(banner.textContent).toContain('<script>alert(1)</script>');
      expect(banner.querySelector('script')).toBeNull();
    });

    it('never writes a recorded value into the document as markup', () => {
      load([
        entry(declaration(), {
          propertyValue: BOLD_NAME,
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      expect(present(controls()[0], 'the value control').value).toBe(BOLD_NAME);
      expect(host().querySelector('b')).toBeNull();
    });
  });

  // ---------------------------------------------------------------------------
  // THE WORDING THAT SURVIVES WITHOUT A CONTROL TO CARRY IT
  // ---------------------------------------------------------------------------
  describe('the list-backed empty option', () => {
    it('publishes the legacy empty-option wording verbatim', () => {
      // ⚠️ NO LIST-BACKED CONTROL IS RENDERED FOR A PROFILE VALUE, and the reason is a reported gap rather
      // than an omission: the declared data type is an unresolved, database-assigned integer (a foreign key
      // into the excluded `Lists` table, resolved BY NAME), so nothing can decide that a property is a
      // country.
      expect(NOT_SPECIFIED_OPTION_TEXT).toBe('Not Specified');
    });

    it('renders no list-backed control for a profile value, which is the reported gap', () => {
      load([entry(declaration({ propertyName: 'Country' }))]);

      // The country property is rendered as text. A `select` appears on this screen only for the
      // visibility affordance, which is off by default.
      expect(host().querySelector('select')).toBeNull();
      expect(controls().length).toBe(1);
      expect(labels()).toEqual(['Country']);
    });

    it('tolerates a property name that is not a legal identifier', () => {
      // The wording table is a string-keyed record, so a name containing a space, a dot or a bracket must
      // miss cleanly and fall back to the name itself rather than throwing or resolving something inherited
      // from `Object.prototype`.
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'Contact Info', viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'constructor', viewOrder: 2 })),
        entry(declaration({ propertyDefinitionId: 3, propertyName: 'toString', viewOrder: 3 })),
      ]);

      expect(labels()).toEqual(['Contact Info', 'constructor', 'toString']);
    });
  });

  // ---------------------------------------------------------------------------
  // THE UPDATE AFFORDANCE
  // ---------------------------------------------------------------------------
  describe('the update action', () => {
    /** The action row's buttons, in document order. */
    function actions(): readonly HTMLButtonElement[] {
      return Array.from(
        host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'),
      );
    }

    it('is text-only, because no legacy raster asset is carried across', () => {
      load([entry(declaration())]);

      expect(host().querySelector('img')).toBeNull();
      expect(host().querySelector('svg[role="img"]')).toBeNull();
    });

    it('is worded "Update", which resolves from the global resource file', () => {
      load([entry(declaration())]);

      const submit = present(actions()[0], 'the update action');
      expect(submit.type).toBe('submit');
      expect((submit.textContent ?? '').trim()).toBe('Update');
    });

    it('leaves the form settled the moment the write settles, without waiting for the re-read', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      const control = present(
        host().querySelector<HTMLInputElement>('input[type="text"]'),
        'the value control',
      );
      control.value = 'Edited';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is what the guard exists to catch')
        .toBeTrue();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock
        .expectOne((request) => request.method === 'PUT' && request.url === profileUrl(USER_ID))
        .flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(tracker.isDirty())
        .withContext('a saved profile must not advertise unsaved work before its re-read has landed')
        .toBeFalse();

      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: {
            userId: USER_ID,
            properties: [
              entry(declaration(), {
                propertyValue: 'Edited',
                lastUpdatedDate: '2024-02-02T00:00:00Z',
              }),
            ],
            displayVisibilityEnabled: false,
          },
        });
      fixture.detectChanges();

      expect(tracker.isDirty())
        .withContext('and it still must not once the rebuilt form has replaced it')
        .toBeFalse();
    });

    it('reports that a write is in flight and refuses a second one', () => {
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === profileUrl(USER_ID),
      );

      // Both actions are disabled while the write is outstanding, and the wording says so.
      expect((present(actions()[0], 'the update action').textContent ?? '').trim()).toBe('Saving…');
      expect(present(actions()[0], 'the update action').disabled).toBeTrue();
      expect(present(actions()[1], 'the cancel action').disabled).toBeTrue();

      // A second submission raised while the first is in flight writes nothing.
      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      expect(httpMock.match((request) => request.method === 'PUT').length).toBe(0);

      written.flush(null, { status: 204, statusText: 'No Content' });

      // The success path re-reads the profile rather than assuming it: the server records the
      // instant each value was last written, and a locally assembled profile would carry none.
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: {
            userId: USER_ID,
            properties: [
              entry(declaration(), {
                propertyValue: 'John',
                lastUpdatedDate: '2024-06-01T12:00:00Z',
              }),
            ],
            displayVisibilityEnabled: true,
          },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();

      // Settled: the wording is back, both actions are live again, and no banner appeared.
      expect((present(actions()[0], 'the update action').textContent ?? '').trim()).toBe('Update');
      expect(present(actions()[0], 'the update action').disabled).toBeFalse();
      expect(present(actions()[1], 'the cancel action').disabled).toBeFalse();
      expect((present(host().querySelector('app-error-banner'), 'the banner').textContent ?? '').trim())
        .toBe('');

      expect(notifications.notifications().length)
        .withContext('a save that kept the values says so')
        .toBe(1);
      expect(notifications.notifications()[0]?.severity).toBe('success');
      expect(notifications.notifications()[0]?.message).toBe('The profile was saved.');
    });
  });

  // EVERY MEASURED LEGACY VALIDATION EXPRESSION, AGAINST THE SAME RULE
  //
  // ⚠ THIS GROUP USED TO ASSERT THE OPPOSITE, AND THE ASSERTION WAS WRONG RATHER THAN MERELY OUTDATED. It
  // encoded a deliberate divergence from `Profile.ascx` L15's `enableClientValidation="true"`: the tenant's
  // expression was not evaluated in the browser, and the justification recorded beside the omission was that
  // the rule was "still reported per field through the server's model-state message". Measured at runtime, it
  // was not — the server published its refusal as a FLAT problem document with no `errors` member, so nothing
  // could be attached to a control, no message appeared, no control was marked invalid and focus stayed on
  // `BODY`. The declared client behaviour and the actual server behaviour were each relying on the other.
  //
  // Both are now fixed, and the browser evaluates the expression whenever it is safe to do so — which is what
  // restores the legacy `enableClientValidation` behaviour these expressions were measured from.
  describe('every measured legacy validation expression', () => {
    /** The action row's buttons, in document order. Restated locally, as each group in this file does. */
    function actions(): readonly HTMLButtonElement[] {
      return Array.from(
        host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'),
      );
    }

    /** One measured expression, with a value it plainly refuses and one it plainly accepts. */
    interface MeasuredExpression {
      /** What the expression is for, named in the case title. */
      readonly what: string;
      /** The expression exactly as the legacy markup declares it. */
      readonly expression: string;
      /** A value the expression refuses. */
      readonly refused: string;
      /** A value the expression accepts. */
      readonly accepted: string;
    }

    const MEASURED: readonly MeasuredExpression[] = Object.freeze([
      {
        what: 'a whole number',
        expression: '^[0-9]*$',
        refused: 'letters',
        accepted: '42',
      },
      {
        what: 'an address with a plus-tagged local part',
        expression: '[\\w\\.-]+(\\+[\\w-]*)?@([\\w-]+\\.)+[\\w-]+',
        refused: 'not-an-address',
        accepted: 'jane.doe+tag@example.co.uk',
      },
      {
        what: 'an address as the bulk-mail screen declared it',
        expression: '\\w+([-+.]\\w+)*@\\w+([-.]\\w+)*\\.\\w+([-.]\\w+)*',
        refused: 'jane@',
        accepted: 'jane@example.com',
      },
      {
        what: 'a declaration name',
        expression: "^[a-zA-Z0-9._%\\-+']+$",
        refused: 'has a space',
        accepted: "o'brien-1",
      },
    ]);

    it('declares each expression exactly as the legacy markup declares it', () => {
      for (const measured of MEASURED) {
        const compiled = new RegExp(measured.expression);

        expect(compiled.test(measured.accepted))
          .withContext(`${measured.expression} must accept ${measured.accepted}`)
          .toBeTrue();
        expect(compiled.test(measured.refused))
          .withContext(`${measured.expression} must refuse ${measured.refused}`)
          .toBeFalse();
      }
    });

    // ⚠ EACH EXPECTATION IS DERIVED FROM THE SHIPPED SAFETY SCREEN, NOT ASSERTED INDEPENDENTLY OF IT, and that
    // is the only honest way to write this. TWO OF THESE FOUR MEASURED LEGACY EXPRESSIONS ARE THEMSELVES
    // REDOS-PRONE — both address expressions quantify a group that already contains a quantifier, which is the
    // textbook catastrophic-backtracking shape — so the browser declines to run them and the server, which has
    // a linear-time engine and a match timeout, enforces them instead. Hardcoding "all four are checked in the
    // browser" would assert a behaviour that must never be true; hardcoding which two are safe would silently
    // rot the day the screen is retuned. Asking `compileTenantPattern` keeps the specification and the shipped
    // rule the same statement.
    for (const measured of MEASURED) {
      const runsInBrowser = compileTenantPattern(measured.expression) !== null;
      const disposition = runsInBrowser ? 'is checked here' : 'is left to the server';

      it(`declares whether the ${measured.what} expression ${disposition}`, () => {
        // Present so the disposition of every measured expression is recorded as a fact of the suite rather
        // than only implied by the branches below.
        expect(compileTenantPattern(measured.expression) === null).toBe(!runsInBrowser);
      });

      it(`${runsInBrowser ? 'reports' : 'stays silent about'} a value the ${measured.what} expression refuses`, () => {
        load([entry(declaration({ validationExpression: measured.expression }))]);

        const control = present(controls()[0], 'the value control');
        control.value = measured.refused;
        control.dispatchEvent(new Event('input'));
        control.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        if (!runsInBrowser) {
          // Silence is CORRECT for these two, not the regression it would be for the others: an expression
          // that could hang the tab is not run here at all.
          expect(host().querySelector('.form-field__error')).toBeNull();
          expect(present(controls()[0], 'the value control').hasAttribute('aria-invalid')).toBeFalse();

          return;
        }

        const message = present(host().querySelector('.form-field__error'), 'the failure message');

        // The wording matches the server's refusal for the same rule, so tripping it in the browser and
        // tripping it on the server do not read as two different problems.
        expect((message.textContent ?? '').trim()).toContain('does not match the format it requires');
        expect(present(controls()[0], 'the value control').getAttribute('aria-invalid')).toBe('true');
      });

      it(`${runsInBrowser ? 'blocks' : 'permits'} a submit carrying a value the ${measured.what} expression refuses`, () => {
        load([entry(declaration({ validationExpression: measured.expression }))]);

        const control = present(controls()[0], 'the value control');
        control.value = measured.refused;
        control.dispatchEvent(new Event('input'));
        fixture.detectChanges();

        present(actions()[0], 'the update action').click();
        fixture.detectChanges();

        if (!runsInBrowser) {
          // It MUST reach the transport, or the rule would not be enforced anywhere at all.
          httpMock
            .expectOne((candidate) => candidate.method === 'PUT')
            .flush(null, { status: 204, statusText: 'No Content' });

          httpMock
            .match(() => true)
            .forEach((outstanding) => outstanding.flush({ data: { userId: USER_ID, properties: [] } }));
          fixture.detectChanges();

          return;
        }

        // Nothing may reach the transport: the point of checking in the browser is that a refusal the browser
        // can already see does not cost a round trip.
        httpMock.expectNone((candidate) => candidate.method === 'PUT');
      });

      it(`reports nothing beside a value the ${measured.what} expression accepts`, () => {
        load([entry(declaration({ validationExpression: measured.expression }))]);

        const control = present(controls()[0], 'the value control');
        control.value = measured.accepted;
        control.dispatchEvent(new Event('input'));
        control.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(host().querySelector('.form-field__error')).toBeNull();
        expect(present(controls()[0], 'the value control').hasAttribute('aria-invalid')).toBeFalse();
      });
    }

    it('leaves at least one measured legacy expression to the server, which is why the screen exists', () => {
      const unsafe = MEASURED.filter((measured) => compileTenantPattern(measured.expression) === null);

      expect(unsafe.length)
        .withContext('the measured set must keep covering the unsafe case')
        .toBeGreaterThan(0);
    });

    // `ValidationExpression nvarchar(100) NULL` permits null, and the installer seeds the EMPTY STRING for
    // all nineteen properties, so BOTH spellings of "no expression" occur in real data and both must behave
    // identically. One case each, so neither can be satisfied by the other.
    for (const absent of [null, ''] as const) {
      it(`adds no pattern rule for the ${absent === null ? 'null' : 'empty'} expression`, () => {
        expect(() => load([entry(declaration({ validationExpression: absent }))])).not.toThrow();

        const control = present(controls()[0], 'the value control');
        control.value = 'anything at all';
        control.dispatchEvent(new Event('input'));
        control.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(host().querySelector('.form-field__error')).toBeNull();
      });
    }

    it('renders every other property when one expression could never compile', () => {
      expect(() =>
        load([
          entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 })),
          entry(
            declaration({
              propertyDefinitionId: 2,
              propertyName: 'LastName',
              viewOrder: 2,
              validationExpression: '((',
            }),
          ),
          entry(
            declaration({
              propertyDefinitionId: 3,
              propertyName: 'Suffix',
              viewOrder: 3,
              validationExpression: '[unclosed',
            }),
          ),
        ]),
      ).not.toThrow();

      // All three render, and the two uncompilable declarations carry no rule.
      expect(controls().length).toBe(3);
      expect(labels()).toEqual(['First Name', 'Last Name', 'Suffix']);
      expect(host().querySelector('.form-field__error')).toBeNull();
    });
  });

  // ---------------------------------------------------------------------------
  // WHAT THE DECLARED DATA TYPE CANNOT DO
  // ---------------------------------------------------------------------------
  describe('the declared data type', () => {
    it('renders a text control whatever integer the declaration carries', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', dataType: 101, viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'Country', dataType: 202, viewOrder: 2 })),
        entry(declaration({ propertyDefinitionId: 3, propertyName: 'TimeZone', dataType: 303, viewOrder: 3 })),
        entry(declaration({ propertyDefinitionId: 4, propertyName: 'Biography', dataType: 404, viewOrder: 4 })),
      ]);

      expect(controls().length).toBe(4);
      expect(host().querySelectorAll('input[type="text"]').length).toBe(3);
      expect(host().querySelectorAll('textarea').length).toBe(1);
      expect(host().querySelector('input[type="number"]')).toBeNull();
      expect(host().querySelector('input[type="date"]')).toBeNull();
      expect(host().querySelector('input[type="checkbox"]')).toBeNull();
      expect(host().querySelector('select')).toBeNull();
    });

    it('tolerates the legacy sentinel a declaration may carry as its data type', () => {
      expect(() => load([entry(declaration({ dataType: -1 }))])).not.toThrow();

      expect(controls().length).toBe(1);
    });

    it('chooses a multi-line control on the declared LENGTH rather than the type', () => {
      // ⚠ NEITHER NAME IS IN THE MEASURED SEEDED-RICH-TEXT SET, and that is what isolates the rule under
      // test. Naming Biography here would give the multi-line control a second, independent reason to
      // appear and the case could no longer attribute it to the length.
      load([
        entry(
          declaration({
            propertyDefinitionId: 1,
            propertyName: 'FirstName',
            dataType: 404,
            length: 50,
            viewOrder: 1,
          }),
        ),
        entry(
          declaration({
            propertyDefinitionId: 2,
            propertyName: 'ProjectSummary',
            dataType: 101,
            length: 3750,
            viewOrder: 2,
          }),
        ),
      ]);

      expect(host().querySelectorAll('textarea').length).toBe(1);
      expect(host().querySelectorAll('input[type="text"]').length).toBe(1);
      expect(present(host().querySelector('textarea'), 'the multi-line control').getAttribute('maxlength'))
        .toBe('3750');
    });
  });

  // ---------------------------------------------------------------------------
  // A DECLARATION THAT DOES NOT BELONG TO THIS ACCOUNT'S TENANT
  // ---------------------------------------------------------------------------
  describe('a declaration scoped outside this tenant', () => {
    it('renders a declaration whose tenant differs from the account\'s', () => {
      // `04.03.03.SqlDataProvider` L77-L84 altered `ProfilePropertyDefinition.PortalID` to NULL and ran
      // `UPDATE ...
      load([
        entry(declaration({ propertyDefinitionId: 1, portalId: 0, propertyName: 'FirstName', viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, portalId: -1, propertyName: 'LastName', viewOrder: 2 })),
        entry(declaration({ propertyDefinitionId: 3, portalId: 42, propertyName: 'Suffix', viewOrder: 3 })),
      ]);

      expect(controls().length).toBe(3);
      expect(labels()).toEqual(['First Name', 'Last Name', 'Suffix']);
    });

    it('applies no client-side soft-delete filter', () => {
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      expect(controls().length).toBe(1);
    });
  });

  describe('the coercions the legacy pages performed implicitly', () => {
    it('coerces the visibility setting the way a valueless attribute would', () => {
      fixture.componentRef.setInput('manageVisibility', '');
      load([entry(declaration())]);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('reads the string "false" as false rather than as a non-empty truth', () => {
      // The trap the transform exists to close: a bare truthiness test would read the four-character
      // string `'false'` as TRUE and offer an affordance the tenant switched off.
      fixture.componentRef.setInput('manageVisibility', 'false');
      load([entry(declaration())]);

      expect(host().querySelector('select')).toBeNull();
    });

    it('reads the string "true" as true', () => {
      fixture.componentRef.setInput('manageVisibility', 'true');
      load([entry(declaration())]);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('converts the route parameter explicitly, refusing the forms a loose parse would accept', () => {
      for (const loose of ['0x10', '7.0', '1e1', ' 7', '7 ', '+7', '', 'seven']) {
        fixture.componentRef.setInput('userId', loose);
        fixture.detectChanges();

        // Every request is counted: no read this screen issues is route-independent, so each
        // malformed form must leave the queue empty.
        expect(httpMock.match(() => true).length)
          .withContext(`"${loose}" must not be read as an account identifier`)
          .toBe(0);
      }
    });

    it('accepts the negative integer that is simultaneously a real key and a sentinel', () => {
      // ⚠️ `Null.NullInteger` IS `-1`, AND `Portals.PortalID` IS `IDENTITY(-1,1)`, so minus one is a real
      // row identifier as well as the legacy marker for "absent". `Roles.RoleID` seeds at zero for the same
      // reason.
      fixture.componentRef.setInput('userId', '-1');
      fixture.detectChanges();

      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === accountUrl(-1))
        .flush({ data: null, meta: null } satisfies ApiResponse<UserDetail | null>);
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(-1))
        .flush({ data: null, meta: null } satisfies ApiResponse<UserProfile | null>);
      fixture.detectChanges();

      expect(host().querySelector('app-empty-state')).not.toBeNull();
    });

    it('submits a visibility as a number, never as the string an option value would be', () => {
      fixture.componentRef.setInput('manageVisibility', true);
      load([
        entry(declaration(), { propertyValue: 'John', lastUpdatedDate: '2024-01-01T00:00:00Z' }),
      ]);

      const select = present(host().querySelector<HTMLSelectElement>('select'), 'the visibility control');
      select.selectedIndex = 2;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === profileUrl(USER_ID),
      );
      const body = written.request.body as {
        readonly properties: readonly { readonly visibility: unknown }[];
      };
      const submitted = present(body.properties[0], 'the submitted property').visibility;

      expect(typeof submitted).toBe('number');
      expect(submitted).toBe(PROFILE_VISIBILITY.adminOnly);

      written.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });

    it('submits every value as a string, whatever the declaration says it means', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'Telephone', viewOrder: 1 }), {
          propertyValue: '0123',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'Birthday', viewOrder: 2 }), {
          propertyValue: '2024-03-01',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
        entry(declaration({ propertyDefinitionId: 3, propertyName: 'Subscribed', viewOrder: 3 }), {
          propertyValue: 'True',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      const written = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === profileUrl(USER_ID),
      );
      const body = written.request.body as {
        readonly properties: readonly { readonly propertyValue: unknown }[];
      };

      // The leading zero survives, the date is not reformatted, and the legacy boolean spelling
      // `True` is carried through with its capital letter rather than being lower-cased.
      expect(body.properties.map((property) => property.propertyValue)).toEqual([
        '0123',
        '2024-03-01',
        'True',
      ]);
      for (const property of body.properties) {
        expect(typeof property.propertyValue).toBe('string');
      }

      written.flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });
  });

  describe('the mandatory-remediation visit', () => {
    /** Seats a caller who owes a mandatory profile completion on their own account. */
    function seatRemediatingIdentity(userId: number): void {
      TestBed.inject(TokenStorageService).store({
        accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
        expiresAtUtc: '2099-12-31T23:59:59.000Z',
        refreshToken: 'not-a-real-refresh-token',
        mustChangePassword: false,
        mustUpdateProfile: true,
        passwordExpiring: false,
        user: {
          userId,
          portalId: 0,
          portalName: 'Baseline Portal',
          username: 'caller',
          displayName: 'The Caller',
          email: 'caller@example.test',
          isSuperUser: false,
          isPortalAdministrator: false,
          roles: ['Registered Users'],
          permissions: [],
        },
      });
    }

    /**
     * Arrives as a remediating caller and answers ONLY the profile read.
     *
     * @param properties The profile entries to return.
     */
    function loadRemediating(properties: readonly UserProfileValue[]): void {
      seatRemediatingIdentity(USER_ID);
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock
        .expectOne(profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties, displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    }

    // -------------------------------------------------------------------------------------------------
    // THE LANDING IS EXPLAINED
    // -------------------------------------------------------------------------------------------------

    // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. A caller who signed in correctly was moved off the screen
    // they asked for onto this one, and the screen said nothing whatsoever about why - it rendered as an
    // ordinary profile edit. The obligation was legible only from the fact that everything else refused.

    it('explains the landing when the caller was moved here to satisfy an obligation', () => {
      loadRemediating([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: '',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      const explanation = host().querySelector('.user-profile__remediation');

      expect(explanation)
        .withContext('the reason for the landing is stated on the screen the caller was sent to')
        .not.toBeNull();
      expect(explanation?.textContent?.trim())
        .withContext('and it is the authored sentence, not a paraphrase assembled in the template')
        .toBe(PROFILE_REMEDIATION_EXPLANATION);
      expect(PROFILE_REMEDIATION_EXPLANATION)
        .withContext('which names what to do rather than describing a permanent condition')
        .toContain('save');
    });

    it('does not announce the explanation, because the redirect was announced once already', () => {
      loadRemediating([]);

      const explanation = host().querySelector('.user-profile__remediation');

      // The guard that performed the redirect emits the single announcement for the action. A second live
      // region carrying the same fact is the double-announcement defect corrected on the sign-in screen.
      expect(explanation?.getAttribute('role'))
        .withContext('a standing explanation, marked as such and not as a live status')
        .toBe('note');
      expect(explanation?.getAttribute('aria-live'))
        .withContext('and it carries no politeness setting of its own')
        .toBeNull();
    });

    it('says nothing about an obligation to a caller who has none', () => {
      seatIdentity(USER_ID);
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      expect(host().querySelector('.user-profile__remediation'))
        .withContext('an operator editing their own profile by choice is told nothing about a requirement')
        .toBeNull();
    });

    it('says nothing to an administrator editing somebody else, whose obligation is not theirs', () => {
      // ⚠ THE OBLIGATION IS THE CALLER'S, NOT THE SUBJECT'S. A held advisory says the SIGNED-IN caller owes
      // a completion; rendering it while they edit another account would assert it about the wrong person.
      seatRemediatingIdentity(USER_ID + 1);
      fixture.componentRef.setInput('userId', String(USER_ID));
      fixture.detectChanges();

      httpMock
        .expectOne(profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();

      // ⚠ AND THE ACCOUNT READ IS STILL WITHHELD, which is a second fact worth recording here. The
      // suppression is keyed on the HELD ADVISORY rather than on whose account is open, because the server
      // refuses every non-exempted endpoint while an obligation stands - whoever the subject is.
      expect(httpMock.match((request) => request.url === `/api/v1/users/${USER_ID}`))
        .withContext('a restricted session reads no account, not even somebody else’s')
        .toEqual([]);

      expect(host().querySelector('.user-profile__remediation'))
        .withContext('the advisory is about the caller, so it is withheld on somebody else’s account')
        .toBeNull();
    });

    it('reads the profile but NOT the account, because only one of the two is exempted', () => {
      loadRemediating([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      httpMock.expectNone(`/api/v1/users/${USER_ID}`);

      expect(controls().length)
        .withContext('and the fields, which are what the screen is for, load normally')
        .toBe(1);
    });

    it('clears the advisory locally and returns to the root once the completion is written', async () => {
      // ⚠ AND NO RENEWAL IS ATTEMPTED, WHICH IS THE SUBSTANCE OF THIS CASE. A caller can owe a credential
      // change as well, in which case that change came first and has already revoked every refresh token
      // the account holds; a renewal here would answer 401 and sign the caller out at the end of a journey
      // they had just completed.
      const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
      const storage = TestBed.inject(TokenStorageService);

      loadRemediating([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock
        .expectOne((request) => request.method === 'PUT' && request.url === profileUrl(USER_ID))
        .flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // A successful write re-reads the profile, which is why the settled outcome is read from
      // the write's own identifier rather than from the shared failure slot.
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
      await fixture.whenStable();

      httpMock.expectNone('/api/v1/auth/refresh');
      expect(storage.session()?.mustUpdateProfile)
        .withContext('the satisfied advisory is cleared on the held session')
        .toBe(false);

      // ⚠ AND NOW THE WITHHELD ACCOUNT READ IS ISSUED, which is the other half of the same rule: the
      // suppression is a reaction to the server's current answer, not a permanent state.
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === `/api/v1/users/${USER_ID}`)
        .flush({ data: account, meta: null } satisfies ApiResponse<UserDetail>);
      fixture.detectChanges();

      expect(navigate)
        .withContext('the root decides where a remediated caller goes, so it is asked')
        .toHaveBeenCalledWith('/', { replaceUrl: true });
    });

    it('does NOT renew after an ordinary save by a caller who owes nothing', () => {
      const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);

      seatIdentity(USER_ID);
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock
        .expectOne((request) => request.method === 'PUT' && request.url === profileUrl(USER_ID))
        .flush(null, { status: 204, statusText: 'No Content' });
      httpMock
        .expectOne((request) => request.method === 'GET' && request.url === profileUrl(USER_ID))
        .flush({
          data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();

      httpMock.expectNone('/api/v1/auth/refresh');
      expect(TestBed.inject(TokenStorageService).session()?.mustUpdateProfile)
        .withContext('and nothing is asserted about an advisory that was never outstanding')
        .toBe(false);
      expect(navigate).not.toHaveBeenCalled();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // THE FORM SURVIVES ITS OWN SAVE
  // ---------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. A successful save RE-READS the profile, and the waiting
  // indicator was the screen's FIRST content branch - so every save replaced the entire form, every label,
  // every value the operator had just typed and both actions, with "Loading profile…" until the confirming
  // read landed. Reported as the profile form blanking entirely during save.
  describe('the form is not blanked while a save is in flight', () => {
    /** Submits the rendered form. */
    function submitForm(): void {
      present(host().querySelector('form'), 'the form').dispatchEvent(new Event('submit'));
      fixture.detectChanges();
    }

    it('keeps every field and its typed value on screen for the whole write and its confirming read', () => {
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

      expect(controls().length).toBe(2);

      submitForm();

      // THE WRITE IS OUTSTANDING.
      const written = httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`);
      expect(written.request.method).toBe('PUT');

      expect(host().querySelector('app-loading-spinner[label="Loading profile…"]'))
        .withContext('the whole-screen indicator must not replace a form that is on screen')
        .toBeNull();
      expect(controls().map((control) => control.value))
        .withContext('the values the operator submitted are still in front of them')
        .toEqual(['John', 'Smith']);
      expect(present(host().querySelector('form'), 'the form').getAttribute('aria-busy'))
        .withContext('and the form reports itself working instead of disappearing')
        .toBe('true');

      written.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // THE CONFIRMING RE-READ IS OUTSTANDING - the window the blanking was measured in.
      const confirmed = httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`);

      expect(host().querySelector('app-loading-spinner[label="Loading profile…"]')).toBeNull();
      expect(controls().map((control) => control.value)).toEqual(['John', 'Smith']);
      expect(present(host().querySelector('form'), 'the form').getAttribute('aria-busy')).toBe('true');

      confirmed.flush({
        data: {
          userId: USER_ID,
          properties: [
            entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
              propertyValue: 'John',
              lastUpdatedDate: '2024-01-01T00:00:00Z',
            }),
            entry(declaration({ propertyDefinitionId: 2, propertyName: 'LastName', viewOrder: 2 }), {
              propertyValue: 'Smith',
              lastUpdatedDate: '2024-01-01T00:00:00Z',
            }),
          ],
          displayVisibilityEnabled: true,
        },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();

      expect(present(host().querySelector('form'), 'the form').getAttribute('aria-busy'))
        .withContext('and it stops reporting itself working once the read has answered')
        .toBeNull();
      expect(host().querySelector('.user-profile__pending')).toBeNull();
    });

    it('states what it is doing ONCE, in ONE live region, and withholds both actions while it does', () => {
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', viewOrder: 1 }), {
          propertyValue: 'John',
          lastUpdatedDate: '2024-01-01T00:00:00Z',
        }),
      ]);

      submitForm();
      const written = httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`);

      const pending = host().querySelectorAll('.user-profile__pending');
      expect(pending.length)
        .withContext('one sentence, not one per state')
        .toBe(1);
      expect(present(pending[0], 'the pending line').getAttribute('role')).toBe('status');
      // ⚠ NO NESTED LIVE REGION. The shared indicator claims one of its own when given a label, and a
      // region inside a region is how a single transition came to be announced twice.
      expect(present(pending[0], 'the pending line').querySelectorAll('[role="status"]').length).toBe(0);

      const both = Array.from(host().querySelectorAll<HTMLButtonElement>('.user-profile__actions button'));
      expect(both.length).toBe(2);
      for (const action of both) {
        expect(action.disabled)
          .withContext(`"${(action.textContent ?? '').trim()}" must not be operable while the save is in flight`)
          .toBeTrue();
      }

      written.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // Still exactly one sentence during the confirming read, with different wording.
      expect(host().querySelectorAll('.user-profile__pending').length).toBe(1);

      httpMock.expectOne(`/api/v1/users/${USER_ID}/profile`).flush({
        data: { userId: USER_ID, properties: [], displayVisibilityEnabled: true },
        meta: null,
      } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });
  });

});
