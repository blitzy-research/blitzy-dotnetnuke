import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { API_ENDPOINTS } from '../../../core/config/api-endpoints';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type UserProfile,
  type UserProfileValue,
} from '../../../core/models/profile.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { MembershipSettings, UserDetail } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { NOT_SPECIFIED_OPTION_TEXT, UserProfileComponent } from './user-profile.component';

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
   * The address of one account, built from the shared endpoint table rather than spelled.
   *
   * ⚠️ BUILT, NOT SPELLED, SO A ROUTE-TEMPLATE CHANGE FAILS HERE INSTEAD OF DRIFTING. Every
   * literal in this file is additionally pinned against these builders by the endpoint block
   * below, so the two can never disagree: a segment rename breaks the pin, and the pin names
   * exactly what changed.
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
   * The address of the tenant's profile-declaration collection.
   *
   * ⚠️ PRESENT SO ITS ABSENCE FROM THE NETWORK CAN BE ASSERTED. This screen deliberately does
   * NOT read it: a declaration arrives embedded in each profile entry, so fetching the list as
   * well would be a redundant round trip for data already in hand. The endpoint is named here
   * from the shared table so that the "one request, not two" contract is pinned against the real
   * address rather than against a guess at it.
   */
  const profileDefinitionsUrl: string = API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection();

  /**
   * The tenant's account policy, which this screen reads once per mount.
   *
   * ⚠ READ ON EVERY MOUNT, WHATEVER THE ROUTE SUPPLIES, because the policy decides whether the
   * per-property visibility control is offered - `Profile.ascx.vb` L59-L63 computed that from
   * `Profile_DisplayVisibility` AND the viewer being the subject of the profile. It is not
   * account-scoped, so it is issued before any account is known, which is why the "dispatches
   * nothing" cases below count ACCOUNT reads rather than all requests.
   */
  const membershipSettingsUrl: string = API_ENDPOINTS.users.membershipSettings();

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
   * The tenant's account policy as this screen reads it.
   *
   * The one member that matters here is `profileDisplayVisibility`, and it defaults to TRUE because
   * that is the default the server publishes when the tenant has stored nothing
   * (`Library/Components/Users/UserModuleBase.vb` L143-L145). Every other member is stated so the
   * fixture is the shape the contract declares rather than a partial the decoder would refuse.
   */
  const POLICY: MembershipSettings = {
    columnFirstName: false,
    columnLastName: false,
    columnDisplayName: true,
    columnAddress: true,
    columnTelephone: true,
    columnEmail: false,
    columnCreatedDate: true,
    columnLastLogin: false,
    columnAuthorized: true,
    displayMode: 2,
    displaySuppressPager: false,
    recordsPerPage: 10,
    profileDefaultVisibility: 2,
    profileDisplayVisibility: true,
    profileManageServices: true,
    redirectAfterLogin: null,
    redirectAfterRegistration: null,
    redirectAfterLogout: null,
    securityEmailValidation: '',
    securityRequireValidProfile: false,
    securityRequireValidProfileAtLogin: true,
    securityUsersControl: 0,
    securityDisplayNameFormat: '',
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
      //
      // `provideRouter([])` supplies the router injectables with an EMPTY route table. The
      // deprecated router testing module is deliberately NOT used - it is removed in a later
      // major and its `ActivatedRoute` stub is a different object from the one the application
      // actually resolves. An empty table is correct rather than merely convenient: this screen
      // is reached by `loadComponent` and navigates nowhere, so any route declared here would be
      // a route the production configuration does not have. The provider is present because the
      // shared components this screen composes may inject router services, and a fixture that
      // omitted them would fail on an injection error that says nothing about the profile.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(UserProfileComponent);
    httpMock = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  /**
   * Answers the account-policy read with a stated policy.
   *
   * @param profileDisplayVisibility Whether the tenant offers the per-property visibility control.
   * @returns Whether a read was outstanding to answer.
   */
  function answerPolicy(profileDisplayVisibility: boolean): boolean {
    const pending = httpMock.match(membershipSettingsUrl);

    for (const request of pending) {
      request.flush({
        data: { ...POLICY, profileDisplayVisibility },
        meta: null,
      });
    }

    fixture.detectChanges();

    return pending.length > 0;
  }

  /**
   * Seats the caller's identity in the stored session.
   *
   * The identity is READ FROM THE STORED SESSION rather than fetched, so seating it is what decides
   * whether the caller is the subject of the profile - the second half of the legacy
   * `ShowVisibility` predicate (`Profile.ascx.vb` L58-L63, over `UserModuleBase.IsUser`
   * L399-L406). The expiry is a FIXED literal: reading the clock in a specification would make it
   * depend on when it runs.
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

    // The account-policy read is DRAINED rather than asserted here, so that no case has to describe
    // a read it is not about. The cases that are about it answer it themselves, with a stated
    // policy, through `answerPolicy` - and because they answer it first, nothing is left for this to
    // drain. Draining is not the same as ignoring: `verify` below still fails on any OTHER
    // outstanding request, which is the guarantee every case in this file depends on.
    for (const pending of httpMock.match(membershipSettingsUrl)) {
      pending.flush({ data: POLICY, meta: null });
    }

    httpMock.verify();
  });

  describe('construction', () => {
    it('creates', () => {
      fixture.detectChanges();

      expect(fixture.componentInstance).toBeTruthy();
    });

    // The on-push strategy, the selector and the exported class name are asserted together under
    // "the structural contract" below, because they are one contract: the route resolves the class
    // by name, the shell instantiates it by selector, and the change-detection strategy is what
    // makes the signal-driven rendering correct. Splitting them across two places invited one to be
    // updated without the others.

    it('dispatches nothing account-scoped until the route supplies an account', () => {
      fixture.detectChanges();

      // Asserted through `match` rather than `expectNone`, so the count is a real
      // expectation. `expectNone` throws on a match but registers no expectation, and a
      // spec with none silently passes if its subject stops doing anything at all.
      //
      // ⚠ COUNTS ACCOUNT-SCOPED READS, NOT ALL REQUESTS. The tenant's account policy is read on
      // every mount and is not account-scoped - it decides whether the visibility control is
      // offered, which is a property of the tenant and the caller rather than of the account being
      // edited. Counting it here would make this case assert something it is not about.
      expect(httpMock.match((request) => request.url !== membershipSettingsUrl).length).toBe(0);
      expect(httpMock.match(membershipSettingsUrl).length)
        .withContext('the policy read is issued once, regardless of the route')
        .toBe(1);
    });
  });

  // MIGRATION: THE ACCOUNT IS ADDRESSED BY A PLAIN ROUTE SEGMENT WHERE THE LEGACY VIEW PAGE USED AN
  // ENCRYPTED QUERY PARAMETER. `ViewProfile.ascx.vb` L61-L63 read `userticket` and passed it through
  // `UrlUtils.DecryptParameter` before `Int32.Parse`; `UrlUtils` is out of scope and the target uses
  // `users/:userId/profile`. The identifier is consequently VISIBLE in the address bar where it
  // previously was not - which is a deliberate, documented divergence and not a weakening of access
  // control. The access control is the server's 403 and always was: the legacy ticket was obfuscation,
  // never authorisation, and the cases below prove the plain segment is converted explicitly and that a
  // malformed one loads nothing rather than dispatching a request for a row that cannot exist.
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

      // The tenant's policy read is excluded: it is not account-scoped and is issued whatever the
      // route says, so it is not evidence that a malformed identifier was dispatched.
      expect(httpMock.match((request) => request.url !== membershipSettingsUrl).length).toBe(0);
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

    it('renders no group, no field and no update action', () => {
      // A tenant declaring nothing is a legitimate configuration rather than a failure, so the
      // affordances are ABSENT FROM THE DOCUMENT rather than hidden by a stylesheet: a control that
      // merely looks inert is still reachable by keyboard and still announced.
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
      // MIGRATION: the destination is named in PROSE and not as a router link. This component
      // declares no router-link directive, so a link would either navigate nowhere or force a full
      // document load - and a full load would discard the memory-held session and sign the operator
      // out mid-edit.
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
      // MIGRATION: THE LEGACY VISIBILITY FILTER COLLAPSES TO "SHOW EVERYTHING", and that is the legacy
      // OUTCOME rather than a relaxation of it. `Profile.ascx.vb` L162-L168 set `Visible = True` on every
      // property for an administrator immediately before binding, and this route is administrator-only.
      // The view page's own filter (`ViewProfile.ascx.vb` L84-L97) reaches the same conclusion here: it
      // narrows an `AdminOnly` property to `(IsAdmin Or IsUser)` and a `MembersOnly` one to
      // `Request.IsAuthenticated`, and on an administrator-only, authenticated route all three
      // `UserVisibilityMode` branches evaluate to visible. `UserVisibilityMode` is additionally NOT one
      // of the nine ported enumerations, so the ported model could not express the filter even if the
      // route did not collapse it. A client-side filter is therefore NOT implemented rather than
      // implemented and then always passing, and no filter is tested that the model cannot express.
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
      const carried = written.request.body as {
        readonly properties: readonly { readonly propertyValue: string }[];
      };
      expect(present(carried.properties[0], 'the submitted property').propertyValue).toBe('letters');

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
      // ⚠ A TENANT-AUTHORED NAME, DELIBERATELY. The seeded rich-text property gets a multi-line
      // control by NAME as well as by length, so naming it here would leave this case unable to
      // say which of the two rules produced the box. This name is in no measured table, so the
      // declared length is the only thing that can have decided it.
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
      // MIGRATION: THE LEGACY ADMINISTRATOR VALIDATION BYPASS IS DELIBERATELY NOT REPRODUCED.
      // `Profile.ascx.vb` L94-L104 read `If ProfileProperties.IsValid Or IsAdmin Then _IsValid = True`,
      // so an administrator was considered valid unconditionally and every declared rule - required,
      // length, pattern - was skipped for them. Because THIS route is administrator-only, reproducing
      // that would make client-side validation entirely vacuous on the only screen that has it, which
      // contradicts the requirement that validation rules must match. The declared rules are therefore
      // applied to every operator and an invalid form issues NO write. Nothing is weakened: the server
      // validates independently and answers 400 with a problem document either way, so the change makes
      // the client agree with the server rather than disagree with it.
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
    /*
     * MIGRATION: `Profile.ascx.vb` L58-L63 computed
     * `CType(UserModuleBase.GetSetting(PortalId, "Profile_DisplayVisibility"), Boolean) And IsUser` -
     * the tenant's policy AND the viewer being the subject of the profile
     * (`Library/Components/Users/UserModuleBase.vb` L399-L406). BOTH halves are resolved on the
     * routed path now: the policy from the account-policy read, the identity from the signed-in
     * session.
     *
     * ⚠ AN EARLIER REVISION TOOK THE AFFORDANCE FROM AN INPUT ALONE, which no route supplies - so
     * the routed screen never offered the control whatever the tenant had configured, and the
     * setting was stored, published and inert. That is the gap these cases close.
     *
     * No signed-in session is established in this fixture, so the identity half is false throughout
     * except where a case says otherwise, which is why the policy being enabled is not on its own
     * enough to render the control.
     */

    it('is not offered while the tenant policy is unresolved, whatever the caller is', () => {
      // The conservative posture: offering a control that then disappears is worse than offering it
      // a moment late, so an unresolved policy reads as "not offered" rather than as a stored false.
      load([entry(declaration())]);

      expect(host().querySelector('select')).toBeNull();
    });

    it('is not offered to a caller who is not the subject of the profile, even with the policy on', () => {
      /*
       * ⚠ THE SECOND HALF OF THE LEGACY PREDICATE, AND WHY IT MATTERS. Visibility is a choice the
       * account holder makes about their OWN data. An administrator editing somebody else's profile
       * could otherwise change who can see it without the holder knowing, so the legacy hid the
       * affordance for exactly that caller however the tenant had set the policy.
       */
      load([entry(declaration())]);
      answerPolicy(true);

      expect(host().querySelector('select'))
        .withContext('no session is signed in here, so the caller is not the subject')
        .toBeNull();
    });

    it('is not offered when the tenant switched the policy off', () => {
      load([entry(declaration())]);
      answerPolicy(false);

      expect(host().querySelector('select')).toBeNull();
    });

    it('is offered to the subject of the profile when the tenant enabled the policy', () => {
      // BOTH halves of the legacy predicate satisfied at once, which is the only combination the
      // legacy screen rendered the control for: the tenant's policy on, and the signed-in caller
      // being the account whose profile is on screen.
      seatIdentity(USER_ID);
      load([entry(declaration())]);
      answerPolicy(true);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('is withheld from a signed-in caller who is a different account', () => {
      // The same policy, a real session, a DIFFERENT account. This is the administrator case, and it
      // is the one the second half of the predicate exists for.
      seatIdentity(USER_ID + 1);
      load([entry(declaration())]);
      answerPolicy(true);

      expect(host().querySelector('select')).toBeNull();
    });

    it('is offered when a caller asks for it', () => {
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);

      expect(host().querySelector('select')).not.toBeNull();
    });

    it('lets the caller override force it on, and never lets a false override force it off', () => {
      // The input is an OVERRIDE for an embedding caller, not the routed behaviour: it can only turn
      // the affordance on. A false defers to the resolved answer rather than suppressing it, which is
      // what stops an embedding context silently overriding a tenant that enabled the policy.
      fixture.componentRef.setInput('manageVisibility', true);
      load([entry(declaration())]);
      answerPolicy(false);

      expect(host().querySelector('select'))
        .withContext('the override asserts the affordance even against a policy that is off')
        .not.toBeNull();
    });

    it('reads the account policy once per mount, whatever the route supplies', () => {
      // ⚠ ONCE, NOT ONCE PER PROPERTY. `Profile.ascx.vb` L60 read the setting inside a property
      // GETTER, so it was fetched on every render of every field. The policy is tenant-wide and does
      // not change as the route moves from one account to another, so it is read from the lifecycle
      // hook rather than from the account-scoped effect.
      load([entry(declaration()), entry(declaration({ propertyDefinitionId: 88, propertyName: 'City' }))]);

      expect(httpMock.match(membershipSettingsUrl).length).toBe(1);

      fixture.componentRef.setInput('userId', '8');
      fixture.detectChanges();

      const reads = httpMock.match((request) => request.url.startsWith('/api/v1/users/8'));

      expect(reads.length).withContext('the account and its profile, and nothing else').toBe(2);
      expect(httpMock.match(membershipSettingsUrl).length)
        .withContext('the tenant policy is not re-read for a different account')
        .toBe(0);

      reads[0]?.flush({ data: account, meta: null });
      reads[1]?.flush({ data: { userId: 8, properties: [] }, meta: null });
      fixture.detectChanges();
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
      // ⚠️ READ-ONLY, NOT DISABLED, AND NOT MERELY STYLED INERT. A disabled control is removed from
      // the accessibility tree AND from the form's value, so a screen-reader user would be told the
      // profile is empty; a control that only LOOKS inert invites an edit that cannot be saved. The
      // values are therefore plain text in a description list, which is read-only structurally.
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

  // ---------------------------------------------------------------------------
  // THE ADDRESSES THIS SCREEN USES
  // ---------------------------------------------------------------------------
  //
  // ⚠️⚠️ EVERY ADDRESS MUST BE RELATIVE, AND AN ABSOLUTE ONE WOULD PASS HERE WHILE BREAKING THE
  // DEPLOYED APPLICATION. The test builder declares no file replacement, so a spec compiles
  // against the PRODUCTION environment, whose base is the relative `/api/v1`; the container serves
  // the bundle from nginx, which forwards `/api/` to the API service. An absolute
  // absolute address naming the API service by its compose service name and port resolves only
  // inside the Docker network and never from a browser, and one naming the loopback host and the
  // API port turns every call into a cross-origin request. Neither failure is visible to a
  // compiler, and neither literal appears anywhere in this file - not even as an example - so the
  // rule is asserted below rather than merely described.
  describe('the addresses this screen uses', () => {
    it('addresses the profile relatively, through the shared endpoint table', () => {
      // Pinned against the literal every other case in this file spells, so the two can never
      // disagree: renaming a segment breaks this expectation and names what changed.
      expect(profileUrl(USER_ID)).toBe(`/api/v1/users/${USER_ID}/profile`);
      expect(accountUrl(USER_ID)).toBe(`/api/v1/users/${USER_ID}`);
    });

    it('addresses the declaration collection relatively and without a tenant parameter', () => {
      // MIGRATION: the tenant is resolved SERVER-SIDE from the request's host against the alias
      // table, exactly as the legacy `GetPortalSettings` procedure resolved it from the alias - so
      // no `portalId` travels in the query string and none is asserted here.
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
      // ONE REQUEST FOR THE FIELDS, NOT TWO. A declaration arrives embedded in each profile entry,
      // so the declaration collection is deliberately never read: it would be a second round trip
      // for data already in hand. The account is read as well, but for the heading rather than for
      // the fields.
      load([entry(declaration())]);

      // The tenant's policy read is answered here rather than excluded, because this case is about
      // the WHOLE set of addresses this screen uses - so the read is named, answered, and then the
      // set is asserted empty. That is a stronger claim than excluding it would be.
      expect(answerPolicy(true))
        .withContext('the policy is one of the addresses this screen uses')
        .toBeTrue();

      expect(httpMock.match(() => true).length)
        .withContext('every dispatched request has already been answered')
        .toBe(0);
    });

    it('never reads /api/v1/profile-definitions', () => {
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      // Asserted through `match` rather than `expectNone` so the count is a real expectation:
      // `expectNone` throws on a match but registers nothing when it finds none, which makes a
      // silently mis-wired fixture look like a pass.
      expect(httpMock.match((request) => request.url === profileDefinitionsUrl).length).toBe(0);
      expect(httpMock.match((request) => request.url.includes('profile-definitions')).length).toBe(0);
    });
  });

  // ---------------------------------------------------------------------------
  // THE STRUCTURAL CONTRACT THE ROUTE DEPENDS ON
  // ---------------------------------------------------------------------------
  //
  // `user.routes.ts` reaches this screen through `loadComponent`, so the exported class name is
  // load-bearing, and the selector is what the shell instantiates. Neither is checked by a
  // compiler at the point that matters, so both are pinned.
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

  // ---------------------------------------------------------------------------
  // THE FOUR CATEGORIES DOTNETNUKE SEEDED, AS THE INSTALLER SEEDS THEM
  // ---------------------------------------------------------------------------
  //
  // Grouping is a BEHAVIOURAL requirement rather than decoration: the legacy editor was declared
  // `groupByMode="Section"` with `groupHeaderIncludeRule="True"` (`Profile.ascx` L18 and L25), so a
  // legacy operator read this screen as four captioned groups and must still read it that way.
  //
  // ⚠️ NINETEEN PROPERTIES, NOT TWENTY. `Profile.ascx.resx` holds 67 `<data>` nodes, four of which
  // are the inert designer placeholders, leaving 63 keyed entries: 19 x 3 keys = 57, plus the four
  // `.Header` values, plus `ProfileProperties_Country.Not Specified`, plus `ProfileTitle.Text`.
  // Every figure below is read from `04.00.04.SqlDataProvider` L1307-L1332, which is the installer
  // procedure that seeds them, so no property, category, order or bound is invented here.
  describe('the four categories DotNetNuke seeds', () => {
    /**
     * One seeded declaration, exactly as `AddDefaultPropertyDefinitions` seeds it.
     *
     * ⚠️ THE DATA TYPE IS NOT REPRODUCED, AND THAT IS DELIBERATE. The installer resolves it BY
     * NAME out of the excluded `Lists` table - `SELECT EntryID ... WHERE ListName='DataType' AND
     * Value='Text'` - so the stored integer is DATABASE-ASSIGNED and differs between installations.
     * Writing one here would pin a value no installation guarantees. An arbitrary integer is used
     * instead and the text-control fallback is asserted separately.
     *
     * @param viewOrder The seeded display order, which doubles as the identifier here because the
     *   installer's orders are distinct.
     * @param category The seeded category.
     * @param propertyName The seeded property name.
     * @param length The seeded bound: 50 for a text property, 0 for one the installer gave a
     *   specialised data type, where 0 means NO bound rather than a bound of nothing.
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

      // ⚠️ "Contact Info" CONTAINS A SPACE. Its resource key is
      // `ProfileProperties_Contact Info.Header`, the only heading key in the file with one, and the
      // value is byte-identical to the category - which is why the category string is rendered
      // straight out of the declaration and no key is ever composed or parsed.
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
      // MIGRATION: A DELIBERATE, REPORTED FUNCTIONAL REDUCTION — rich text becomes a PLAIN
      // MULTI-LINE BOX, not a single-line one and not an editor. Biography is the one property the
      // installer seeds with the rich-text data type
      // (`04.00.04.SqlDataProvider` L1330), and it was edited through the excluded FCK provider.
      //
      // ⚠ IT IS RECOGNISED BY NAME, AND IT HAS TO BE. The installer also seeds it with
      // `@Length = 0`, which is the ABSENCE of a bound rather than a large one, so the
      // length-driven rule cannot reach it — a purely length-driven screen renders the one
      // property that is unambiguously prose in a one-line box. The declared DATA TYPE cannot
      // drive the choice either, because it is an unresolved database-assigned integer.
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
      expect(biography.getAttribute('rows')).withContext('a real multi-line box').toBe('4');

      // The label proves it is Biography that got the box rather than some other property.
      expect(
        (
          present(biography.closest('.form-field'), 'the biography field').querySelector(
            '.form-field__label',
          )?.textContent ?? ''
        ).trim(),
      ).toBe('Biography');

      // ⚠ AND NO RICH-TEXT AFFORDANCE IS SUBSTITUTED, which is the other half of the reduction.
      // A `textarea` cannot render markup, and nothing on this screen opts back into it: there
      // is no editable container, no editor toolbar, no framed editor document, and no element
      // whose content was written as trusted HTML. Asserting the reduction without asserting
      // this would leave the door open to a "small" editor being added later.
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
      // Measured as `ControlTitle_profile.Text` = "Manage Profile".
      //
      // ⚠️ THAT KEY IS NOT IN THIS SCREEN'S OWN RESOURCE FILE. It is in
      // `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx` L202-L203, because the
      // legacy title belonged to the container that HOSTED the editor rather than to the editor.
      // Reading only the local file would have found no title at all.
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
      // The legacy assigned the record heading inside `DataBind()` regardless of the editor mode,
      // so a legacy operator READING a profile also saw the word "Edit" in that row. Preserving
      // that is behavioural equivalence rather than an oversight.
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

  // ---------------------------------------------------------------------------
  // ⚠️⚠️ TEXT FROM THE API IS UNTRUSTED MARKUP, BY MEASUREMENT
  // ---------------------------------------------------------------------------
  //
  // Across the thirty-seven in-scope resource files - 1,111 keyed entries in 1,211 raw `<data>`
  // nodes - SEVENTY-SIX values carry a raw HTML tag and FOUR carry a live `script` element, stored
  // XML-escaped so a naive search clears them wrongly. This screen's own sixty-three values happen
  // to carry none, which is exactly why the rule must be asserted rather than assumed: the runtime
  // property list, its names, its categories and the server's per-field messages all arrive from
  // the API, not from that file.
  //
  // The legacy code knew this. `AccessDenied.ascx.vb` L43 re-ENCODED the message it had just decoded
  // before showing it, so escaping is preservation of a legacy safety property rather than a new
  // restriction.
  //
  // Every case below asserts the same two things: the text is present VERBATIM, brackets included,
  // and NO element was created from it. The first without the second would pass for a screen that
  // rendered the markup and then happened to contain the text.
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
          // ⚠️ THE KEYS ARE .NET MODEL-STATE NAMES AND ARE PASCAL-CASED. They name model members
          // rather than JSON members, so the camel-case body policy does not reach them, and the
          // document is read with a BRACKET because property access on an index signature is a
          // compile error in this workspace.
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
      // ⚠️ MEASURED AS INCONSISTENT: 28 of the 34 in-scope validator messages begin with a literal
      // `<br>`, which the legacy page emitted as MARKUP to push the message onto its own line. It is
      // layout expressed as content, and because nothing here treats a message as markup an operator
      // would otherwise literally read the characters `<br>` in front of every message. The break is
      // therefore RESOLVED to a line break rather than rendered, and the line break itself comes
      // from the stylesheet.
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
      // A recorded value reaches a control's `value` rather than its content, so it cannot create an
      // element even in principle - but the property is asserted because the reverse mistake, using
      // a raw-HTML binding to show a value, is exactly what this rule forbids.
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
      // MIGRATION: measured as `ProfileProperties_Country.Not Specified`, whose suffix is the ONLY
      // one in the entire thirty-seven-file resource census that contains a space - and therefore
      // the only key that is not a legal identifier. It labels the blank entry of a list-backed
      // control.
      //
      // ⚠️ NO LIST-BACKED CONTROL IS RENDERED FOR A PROFILE VALUE, and the reason is a reported gap
      // rather than an omission: the declared data type is an unresolved, database-assigned integer
      // (a foreign key into the excluded `Lists` table, resolved BY NAME), so nothing can decide
      // that a property is a country. The wording is published so it survives the migration and is
      // reachable the moment data-type resolution lands.
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
      // The wording table is a string-keyed record, so a name containing a space, a dot or a
      // bracket must miss cleanly and fall back to the name itself rather than throwing or
      // resolving something inherited from `Object.prototype`.
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
      // MIGRATION: the legacy affordance was `<dnn:commandbutton ... imageurl="~/images/save.gif">`
      // (`Profile.ascx` L33). The frontend ships one static asset - a favicon - so the icon is
      // neither reproduced nor substituted with a lookalike, and the action is text-only.
      load([entry(declaration())]);

      expect(host().querySelector('img')).toBeNull();
      expect(host().querySelector('svg[role="img"]')).toBeNull();
    });

    it('is worded "Update", which resolves from the global resource file', () => {
      // MIGRATION: `cmdUpdate.Text` is ABSENT from this screen's own resource file; the wording
      // resolves from `Website/App_GlobalResources/SharedResources.resx` through the two-level
      // fallback. Reading only the local file would have found no wording at all.
      load([entry(declaration())]);

      const submit = present(actions()[0], 'the update action');
      expect(submit.type).toBe('submit');
      expect((submit.textContent ?? '').trim()).toBe('Update');
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

      // ⚠️ `204` WITH A NULL BODY. The endpoint is declared
      // `[ProducesResponseType(StatusCodes.Status204NoContent)]` on `PUT {userId:int}/profile`, so a
      // fixture answering `200` would encode a contract this API does not have - and would remove
      // the very reason the store re-reads afterwards.
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
      expect(notifications.notifications().length).toBe(0);
    });
  });

  // ---------------------------------------------------------------------------
  // EVERY MEASURED LEGACY VALIDATION EXPRESSION, AGAINST THE SAME RULE
  // ---------------------------------------------------------------------------
  //
  // MIGRATION: THE TENANT'S VALIDATION EXPRESSION IS NOT EVALUATED IN THE BROWSER, AND THAT IS A
  // DELIBERATE DIVERGENCE FROM `Profile.ascx` L15, WHICH DECLARED `enableClientValidation="true"`.
  //
  // ⚠️⚠️ THESE CASES PIN THE ABSENCE OF A CLIENT-SIDE PATTERN RULE, WHICH IS NOT THE SAME THING AS
  // THE RULE BEING UNENFORCED. The endpoint enforces it, compiling the expression with a
  // fifty-millisecond match timeout, a length ceiling and a bounded compiled-expression cache, and
  // reports a refusal per field through its model-state document, which this screen already renders.
  //
  // The browser cannot make the same promise. A `ValidationExpression` is ADMINISTRATOR-AUTHORED
  // DATA, so it is untrusted input to whatever engine runs it, and a catastrophically backtracking
  // pattern takes exponential time on an ordinary input. Evaluated here it would run synchronously on
  // the UI thread on EVERY KEYSTROKE of a control whose length is frequently unbounded, because a
  // declared length of zero means no maximum - and the browser's engine exposes no match timeout of
  // any kind, so such an evaluation cannot be bounded, only avoided.
  //
  // The expressions below are the ones the legacy tree actually contains, each cited, so the case is
  // about measured data rather than about invented data:
  //   * `^[0-9]*$`                                    `Website/admin/Vendors/banneroptions.ascx` L36
  //   * `[\w\.-]+(\+[\w-]*)?@([\w-]+\.)+[\w-]+`       `Website/controls/user.ascx` L62
  //   * `\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*` `Website/admin/Users/bulkemail.ascx` L46
  //   * `^[a-zA-Z0-9._%\-+']+$`                       `ProfilePropertyDefinition.vb` L228
  describe('every measured legacy validation expression', () => {
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
      // A guard on the fixtures themselves: an expression mistranscribed here would make every case
      // below assert against a rule the legacy application never had. Each pair is checked with the
      // browser's own engine, which is the one place a pattern IS compiled - in a test, never in the
      // component.
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

    for (const measured of MEASURED) {
      it(`reports nothing beside a value the ${measured.what} expression refuses`, () => {
        load([entry(declaration({ validationExpression: measured.expression }))]);

        const control = present(controls()[0], 'the value control');
        control.value = measured.refused;
        control.dispatchEvent(new Event('input'));
        control.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(host().querySelector('.form-field__error')).toBeNull();
        expect(present(controls()[0], 'the value control').hasAttribute('aria-invalid')).toBeFalse();
      });

      it(`reports nothing beside a value the ${measured.what} expression accepts`, () => {
        load([entry(declaration({ validationExpression: measured.expression }))]);

        const control = present(controls()[0], 'the value control');
        control.value = measured.accepted;
        control.dispatchEvent(new Event('input'));
        control.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(host().querySelector('.form-field__error')).toBeNull();
      });
    }

    // `ValidationExpression nvarchar(100) NULL` permits null, and the installer seeds the EMPTY
    // STRING for all nineteen properties, so BOTH spellings of "no expression" occur in real data
    // and both must behave identically. One case each, so neither can be satisfied by the other.
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
      // ⚠️ THE EXPRESSION IS UNTRUSTED CALLER-SUPPLIED DATA, so an uncompilable one must degrade one
      // field rather than throw and take the screen down. Nothing compiles it any longer, which is
      // why this costs nothing - but the case is kept because it is the shape of stored data most
      // likely to be present, and it would fail loudly the moment anyone reintroduced a client-side
      // evaluation without guarding it.
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
      // MIGRATION: A REPORTED FUNCTIONAL REDUCTION, NOT A SHORTCUT. `DataType` is a foreign key into
      // the EXCLUDED `Lists` table, resolved BY NAME by the installer
      // (`SELECT EntryID ... WHERE ListName='DataType' AND Value='Text'`), so the stored integer is
      // DATABASE-ASSIGNED and differs between installations. A hardcoded integer-to-control map
      // would silently render the WRONG control wherever the list seeded in a different order, so
      // the thirteen legacy control kinds - unknown, text, integer, true/false, time zone, locale,
      // page, rich text, country, region, list, date, date-time - are not offered.
      //
      // ⚠️ THE INTEGERS BELOW ARE DELIBERATELY ARBITRARY AND CARRY NO MEANING. Choosing values that
      // looked like real `EntryID`s would suggest this screen knows what they mean, which is exactly
      // the mistake the reduction exists to avoid.
      load([
        entry(declaration({ propertyDefinitionId: 1, propertyName: 'FirstName', dataType: 101, viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, propertyName: 'Country', dataType: 202, viewOrder: 2 })),
        entry(declaration({ propertyDefinitionId: 3, propertyName: 'TimeZone', dataType: 303, viewOrder: 3 })),
        entry(declaration({ propertyDefinitionId: 4, propertyName: 'Biography', dataType: 404, viewOrder: 4 })),
      ]);

      // Four PLAIN TEXT controls for four arbitrary integers, and not one specialised control
      // among them. Three are single-line; the fourth is Biography's textarea, which is still a
      // plain text control and is chosen by the measured seeded-rich-text set rather than by its
      // integer — the proof being that FirstName, Country and TimeZone carry three DIFFERENT
      // arbitrary integers here and all three stay single-line.
      expect(controls().length).toBe(4);
      expect(host().querySelectorAll('input[type="text"]').length).toBe(3);
      expect(host().querySelectorAll('textarea').length).toBe(1);
      expect(host().querySelector('input[type="number"]')).toBeNull();
      expect(host().querySelector('input[type="date"]')).toBeNull();
      expect(host().querySelector('input[type="checkbox"]')).toBeNull();
      expect(host().querySelector('select')).toBeNull();
    });

    it('tolerates the legacy sentinel a declaration may carry as its data type', () => {
      // `ProfilePropertyDefinition.vb` L47 initialises `DataType` to `Null.NullInteger`, which is
      // `-1`. It is a legitimate stored value and must not be mistaken for an absent one.
      expect(() => load([entry(declaration({ dataType: -1 }))])).not.toThrow();

      expect(controls().length).toBe(1);
    });

    it('chooses a multi-line control on the declared LENGTH rather than the type', () => {
      // ⚠ NEITHER NAME IS IN THE MEASURED SEEDED-RICH-TEXT SET, and that is what isolates the
      // rule under test. Naming Biography here would give the multi-line control a second,
      // independent reason to appear and the case could no longer attribute it to the length.
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

      // The property with the LONGER declared bound gets the multi-line control, and the data types
      // are swapped relative to what a type-driven choice would need - so this can only pass if the
      // length decided it.
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
      // `04.03.03.SqlDataProvider` L77-L84 altered `ProfilePropertyDefinition.PortalID` to NULL and
      // ran `UPDATE ... SET PortalId = NULL WHERE PortalId = -1` - the ONE place the schema itself
      // migrated the `-1` sentinel to a real SQL NULL - so a null tenant means a GLOBAL, host-level
      // declaration that every tenant inherits.
      //
      // ⚠️ REPORTED GAP: the published client contract declares `portalId: number`, NOT
      // `number | null`, so a host-level declaration cannot be expressed on this side of the
      // boundary at all. A `portalId: null` fixture would not compile, and inventing one would
      // assert a contract that does not exist. What CAN be asserted is the consequence that
      // matters: the server decides which declarations a profile carries, and this screen renders
      // every entry it is given whatever tenant the declaration names.
      load([
        entry(declaration({ propertyDefinitionId: 1, portalId: 0, propertyName: 'FirstName', viewOrder: 1 })),
        entry(declaration({ propertyDefinitionId: 2, portalId: -1, propertyName: 'LastName', viewOrder: 2 })),
        entry(declaration({ propertyDefinitionId: 3, portalId: 42, propertyName: 'Suffix', viewOrder: 3 })),
      ]);

      expect(controls().length).toBe(3);
      expect(labels()).toEqual(['First Name', 'Last Name', 'Suffix']);
    });

    it('applies no client-side soft-delete filter', () => {
      // `Deleted bit NOT NULL` exists on the table but is ABSENT from the fifteen properties of the
      // legacy class, because `GetPropertyDefinitionsByPortal(portalId, True)` filtered it
      // SERVER-SIDE. Filtering again here would duplicate a decision this screen cannot see the
      // inputs to, so the contract carries no such member and none is asserted.
      load([entry(declaration({ propertyName: 'FirstName' }))]);

      expect(controls().length).toBe(1);
    });
  });

  // ---------------------------------------------------------------------------
  // ⚠️ THE OPTION STRICT ASYMMETRY, MADE EXPLICIT
  // ---------------------------------------------------------------------------
  //
  // The class library compiled with Option Strict ON, but `Website/release.config` L125 compiled the
  // admin pages with `<compilation debug="false" strict="false">` - Option Strict OFF - so the
  // thirty-nine admin code-behinds may legally contain late binding and implicit narrowing that
  // TypeScript rejects outright. Every such coercion has to be made EXPLICIT during translation, and
  // each one is a place the behaviour could quietly differ. The cases below pin the three that reach
  // this screen.
  describe('the coercions the legacy pages performed implicitly', () => {
    it('coerces the visibility setting the way a valueless attribute would', () => {
      // `Profile.ascx.vb` L58-L63 computed `CType(setting, Boolean) And IsUser`, where `setting` is
      // an `Object` returned by the settings accessor - a conversion legal only because the page
      // compiled with strict checking off. It is explicit here: the input is declared boolean and
      // transformed by `booleanAttribute`, so an attribute written WITHOUT a value still means true,
      // which is the HTML rule for a boolean attribute.
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
      // The route delivers a STRING and the conversion happens once, in one place. A loose conversion
      // would accept a hexadecimal literal, an exponent and a decimal point - `'0x10'` becomes 16 -
      // and would check neither the exactly-representable ceiling nor the API's 32-bit range. Each
      // form below must load NOTHING rather than dispatch a request for a row that does not exist.
      for (const loose of ['0x10', '7.0', '1e1', ' 7', '7 ', '+7', '', 'seven']) {
        fixture.componentRef.setInput('userId', loose);
        fixture.detectChanges();

        // Account-scoped reads only. The policy read is not account-scoped and would otherwise
        // register on the first iteration as though a malformed identifier had been dispatched.
        expect(httpMock.match((request) => request.url !== membershipSettingsUrl).length)
          .withContext(`"${loose}" must not be read as an account identifier`)
          .toBe(0);
      }
    });

    it('accepts the negative integer that is simultaneously a real key and a sentinel', () => {
      // ⚠️ `Null.NullInteger` IS `-1`, AND `Portals.PortalID` IS `IDENTITY(-1,1)`, so minus one is a
      // real row identifier as well as the legacy marker for "absent". `Roles.RoleID` seeds at zero
      // for the same reason. Presence is therefore tested EXPLICITLY - never by truthiness, never by
      // a comparison against zero, and never by coalescing to a sentinel - so a well-formed negative
      // identifier is dispatched rather than swallowed.
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
      // The template binds each option with `ngValue` rather than `value` precisely because the
      // control holds a NUMBER: a plain value binding writes the option back as a string, and the
      // submitted visibility would then stop matching the integer codes the API declares. This is the
      // integer-to-string coercion that Option Strict OFF would have performed silently, made
      // explicit and asserted at the boundary where it matters.
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
          data: { userId: USER_ID, properties: [] },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });

    it('submits every value as a string, whatever the declaration says it means', () => {
      // MIGRATION: `UserProfile.PropertyValue` is `nvarchar` and the legacy accessor returned a
      // STRING for every data type - a date, an integer and a boolean alike - so the wire form is a
      // string in every case and no local conversion is performed. A screen that parsed a value into
      // a number or a date would submit a differently formatted string than the legacy screen did.
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
          data: { userId: USER_ID, properties: [] },
          meta: null,
        } satisfies ApiResponse<UserProfile>);
      fixture.detectChanges();
    });
  });

});
