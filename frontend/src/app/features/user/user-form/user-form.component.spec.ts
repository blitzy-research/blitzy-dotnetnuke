import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { reflectComponentType, type ComponentRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import type { ValidationErrors } from '@angular/forms';
import { Router, provideRouter } from '@angular/router';

import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import { PasswordFormat, UserCreateStatus } from '../../../core/models/user.model';
import type {
  CreateUserRequest,
  UpdateUserRequest,
  UserDetail,
  UserListItem,
} from '../../../core/models/user.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { SessionTeardownService } from '../../../core/state/session-teardown.service';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { USER_LIST_ROUTE } from '../../../core/config/app-routes.config';
import { USER_DELETED_MESSAGE } from '../user-messages';
import { UserStore } from '../../../core/state/user.store';
import { USER_CREATE_MESSAGE, stripLegacyBreakTags } from '../../../core/utils/form-errors.util';
import {
  AUTHORIZE_MAIL_ADVISORY,
  CREATE_NOT_YET_ATTEMPTED,
  CREATE_SUCCEEDED,
  INVALID_PASSWORD_MESSAGE as COMPONENT_INVALID_PASSWORD_MESSAGE,
  NOTIFY_UNAVAILABLE_ADVISORY,
  PASSWORD_MIN_LENGTH,
  PASSWORD_MIN_NON_ALPHANUMERIC,
  UserFormComponent,
  passwordRulesValidator,
} from './user-form.component';

/**
 * Specification for the account editor. One screen, two modes, and SEVEN write paths that reach four
 * different endpoints — which is why the cases below are grouped by path rather than by member.
 */
/**
 * The tenant policy this screen reads on arrival. The default composes NO display name, which is the
 * ordinary case and the one every pre-existing case in this suite was written against.
 */
const TENANT_POLICY = Object.freeze({
  isStored: true,
  columnFirstName: false,
  columnLastName: false,
  columnDisplayName: true,
  columnAddress: true,
  columnTelephone: true,
  columnEmail: false,
  columnCreatedDate: true,
  columnLastLogin: false,
  columnAuthorized: true,
  displayMode: 0,
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
  securityRequireValidProfileAtLogin: false,
  securityUsersControl: 0,
  securityDisplayNameFormat: '',
  displayNameFormatChanged: false,
  displayNamesRewritten: 0,
});

describe('UserFormComponent', () => {
  let fixture: ComponentFixture<UserFormComponent>;
  let reference: ComponentRef<UserFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let infoSpy: jasmine.Spy;
  let successSpy: jasmine.Spy;
  let warningSpy: jasmine.Spy;
  let errorSpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  // ---------------------------------------------------------------------------------------------------
  // ADDRESSES
  // ---------------------------------------------------------------------------------------------------

  const USERS_URL = '/api/v1/users';

  function userUrl(userId: number): string {
    return `${USERS_URL}/${userId}`;
  }

  // ---------------------------------------------------------------------------------------------------
  // MEASURED WORDING
  // ---------------------------------------------------------------------------------------------------

  const CREATE_MODE_TITLE = 'Add New User';
  const EDIT_MODE_TITLE = 'Edit User Accounts';
  const CREATE_SUBMIT_LABEL = 'Create User';
  const UPDATE_SUBMIT_LABEL = 'Update';
  const DELETE_LABEL = 'Delete';
  const CONFIRM_DELETE_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
  const AUTHORIZE_LABEL = 'Authorize User';
  const UNAUTHORIZE_LABEL = 'UnAuthorize User';
  const UNLOCK_LABEL = 'Unlock Account';
  const FORCE_PASSWORD_LABEL = 'Force Password Change';

  const USERNAME_REQUIRED_MESSAGE = 'User name is required';
  const FIRST_NAME_REQUIRED_MESSAGE = 'First name is required';
  const LAST_NAME_REQUIRED_MESSAGE = 'Last name is required';
  const DISPLAY_NAME_REQUIRED_MESSAGE = 'Display Name is required';
  const EMAIL_REQUIRED_MESSAGE = 'Email is required';
  const EMAIL_PATTERN_MESSAGE = 'You must enter a valid email address';
  const PASSWORD_MISMATCH_MESSAGE = 'The Password and Confirmation Passwords do not match';
  const INVALID_PASSWORD_MESSAGE =
    'The password specified is invalid.  Please specify a valid password.  Passwords must be at ' +
    'least 7 characters in length and contain at least 0 non-alphanumeric characters.';

  /** `UserAuthorized.Text`, measured verbatim in `ManageUsers.ascx.resx`. */
  const USER_AUTHORIZED_MESSAGE = 'User successfully Authorized';

  /** `UserUnAuthorized.Text`, measured verbatim in `ManageUsers.ascx.resx`. */
  const USER_UNAUTHORIZED_MESSAGE = 'User successfully Un-Authorized';

  /**
   * ⚠ AUTHORED BECAUSE IT WAS ABSENT — NOT measured wording, and it must not be presented as such.
   * `ManageUsers.ascx.vb` L749 raises the resource key `"UserUnLocked"` on a successful release, but
   * **that key is defined in NO resource file**: it appears in neither
   * `Website/admin/Users/App_LocalResources/*.resx` nor `Website/App_GlobalResources/*.resx`.
   */
  const USER_UNLOCKED_MESSAGE = 'User successfully Unlocked';
  const USER_LOCKED_OUT_MESSAGE =
    'This account is currently locked out due to too many unsuccessful login attempts.';
  const PASSWORD_CHANGE_REQUIRED_MESSAGE = 'This user must change their password at next login';
  const USER_UPDATED_MESSAGE = 'User account updated';
  // Stated as a literal rather than imported, on this file's own convention: the wording IS the assertion.
  const NO_CHANGES_MESSAGE = 'There are no changes to save.';

  /** The creation confirmation, with the placeholder the component substitutes. */
  const USER_CREATED_MESSAGE = 'User account {name} created';
  const NO_USER_MESSAGE = "This account doesn't exist";
  const NOT_AUTHORIZED_MESSAGE = 'You are not authorized to edit this user.';
  const EMAIL_CONFLICT_MESSAGE =
    'This portal requires a unique Email Address.  The Email Address you entered has already been used.';
  const EXCEEDED_USER_QUOTA_MESSAGE_FRAGMENT = 'quota';
  const USER_NAME_EXISTS =
    'A User Already Exists For the Username Specified. Please Register Again Using A ' +
    'Different Username.';

  /** Control identifiers, exactly as the template composes them. */
  const CONTROL_ID = Object.freeze({
    username: 'user-form-username',
    firstName: 'user-form-first-name',
    lastName: 'user-form-last-name',
    displayName: 'user-form-display-name',
    email: 'user-form-email',
    authorize: 'user-form-authorize',
    notify: 'user-form-notify',
    randomPassword: 'user-form-random-password',
    password: 'user-form-password',
    confirmPassword: 'user-form-confirm-password',
  });

  /**
   * The reason phrase the API publishes as a problem `title`, keyed by status. ⚠ NOT FREE TEXT. Every
   * refusal reaches the wire through one shared problem factory that fills the title from this
   * status-keyed vocabulary.
   */
  const PROBLEM_TITLE: Readonly<Record<number, string>> = Object.freeze({
    400: 'Bad Request',
    401: 'Unauthorized',
    403: 'Forbidden',
    404: 'Not Found',
    409: 'Conflict',
    429: 'Too Many Requests',
    500: 'Internal Server Error',
    503: 'Service Unavailable',
  });

    /** The correlation identifier every refusal fixture below carries. */
  const FIXTURE_CORRELATION_ID = 'f0e7d1b2-3c45-4a6b-8c9d-0e1f2a3b4c5d';

  /** A live problem document, complete in every member the API emits. */
  function problem(
    code: string,
    status: number,
    detail: string,
    errors?: Readonly<Record<string, readonly string[]>>,
  ): ProblemDetails {
    const document: ProblemDetails = {
      type: `urn:dnnmigration:error:${code}`,
      title: PROBLEM_TITLE[status] ?? 'Bad Request',
      status,
      detail,
      traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      correlationId: FIXTURE_CORRELATION_ID,
    };

    return errors === undefined ? document : { ...document, errors };
  }

  // ---------------------------------------------------------------------------------------------------
  // FIXTURES
  // ---------------------------------------------------------------------------------------------------

  /**
   * One account as the server reports it. ⚠ THE DEFAULT IDENTIFIER IS ZERO. `Users.UserID` seeds at one
   * in the legacy schema, but the screen must not depend on that: the input is parsed rather than
   * inspected, and zero is the value that proves it.
   */
  function account(userId = 0, overrides: Partial<UserDetail> = {}): UserDetail {
    return {
      userId,
      portalId: -1,
      username: 'ada.lovelace',
      firstName: 'Ada',
      lastName: 'Lovelace',
      displayName: 'Ada Lovelace',
      email: 'ada@example.test',
      isSuperUser: false,
      affiliateId: null,
      isApproved: true,
      isLockedOut: false,
      isOnline: false,
      mustChangePassword: false,
      createdDate: '2024-01-05T09:00:00Z',
      lastLoginDate: '2024-06-01T08:30:00Z',
      lastActivityDate: '2024-06-01T08:45:00Z',
      lastLockoutDate: null,
      lastPasswordChangeDate: '2024-02-01T10:00:00Z',
      roles: ['Registered Users'],
      canDelete: true,
      // Opaque and never interpreted here: a fixture only has to carry one for the round trip to close.
      concurrencyToken: 'account-revision-token',
      ...overrides,
    };
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /**
   * A page of accounts. ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture
   * spelling it otherwise flushes successfully and unwraps to no rows at all.
   */
  function emptyPage(): PagedResponse<UserListItem> {
    return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 } };
  }

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    await TestBed.configureTestingModule({
      imports: [UserFormComponent],
      // The store is listed so each case gets its own instance; it is declared
      // `providedIn: 'root'`, so without this every case would share one selection.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), UserStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    const notifications = TestBed.inject(NotificationService);

    notifySpy = spyOn(notifications, 'notify').and.callThrough();
    successSpy = spyOn(notifications, 'success').and.callThrough();
    infoSpy = spyOn(notifications, 'info').and.callThrough();
    warningSpy = spyOn(notifications, 'warning').and.callThrough();
    errorSpy = spyOn(notifications, 'error').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    // The session is cleared so one case's signed-in operator cannot decide another's affordances.
    TestBed.inject(TokenStorageService).clear();

    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Mounts the screen. ⚠ THE IDENTIFIER IS DELIVERED AS TEXT, because the router binds a route parameter
   * as a string.
   */
  function create(userId?: string, policy: Record<string, unknown> = {}): void {
    fixture = TestBed.createComponent(UserFormComponent);
    reference = fixture.componentRef;

    if (userId !== undefined) {
      reference.setInput('userId', userId);
    }

    fixture.detectChanges();
    // ⚠ ANSWERED HERE, WITH THE OVERRIDE, AND NOT LATER. The component reads the policy once on arrival and
    // guards against reading it again, so a policy supplied after this point has no request left to answer
    // and is silently ignored - which is how a case asking for a composed display name would pass while
    // testing the default one.
    answerTenantPolicy(policy);
  }

  /**
   * Answers the tenant-policy read this screen issues on arrival.
   *
   * ⚠ MATCHED RATHER THAN EXPECTED, AND THE DIFFERENCE MATTERS HERE. `expectOne` fails when the request is
   * absent, and it is legitimately absent whenever the policy is already held - which is exactly what the
   * component's own guard arranges. Matching leaves those cases alone instead of failing them for the wrong
   * reason.
   *
   * The default policy composes NO display name, so every pre-existing case keeps the behaviour it was
   * written against; the cases that need a composed name state it themselves.
   *
   * @param overrides Members to vary from the default policy.
   */
  function answerTenantPolicy(overrides: Record<string, unknown> = {}): void {
    for (const pending of httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/users/settings',
    )) {
      pending.flush({ data: { ...TENANT_POLICY, ...overrides }, meta: null });
    }

    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(
    method: string,
    url: string,
    description?: string,
  ): ReturnType<HttpTestingController['expectOne']> {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /**
   * Seats the signed-in operator, which is what decides whether this screen is editing its own subject.
   * The identity is READ FROM THE STORED SESSION rather than fetched, so seating it is the whole of the
   * first half of the measured gate at `Membership.ascx.vb` L135 — `UserInfo.UserID = User.UserID`, over
   * the signed-in operator and the account under edit.
   *
   * @param userId The account the operator is signed in as.
   */
  function signedInAs(userId: number): void {
    TestBed.inject(TokenStorageService).store({
      accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
      expiresAtUtc: '2099-12-31T23:59:59.000Z',
      refreshToken: 'not-a-real-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: {
        userId,
        portalId: -1,
        portalName: 'Baseline Portal',
        username: 'caller',
        displayName: 'The Caller',
        email: 'caller@example.test',
        isSuperUser: false,
        isPortalAdministrator: false,
        mustChangePassword: false,
        mustUpdateProfile: false,
        roles: ['Registered Users'],
        permissions: [],
      },
    });
  }

  /** Mounts in edit mode and answers the account read. */
  function arriveEditing(held: UserDetail = account()): void {
    create(String(held.userId));
    expectRequest('GET', userUrl(held.userId), 'the account read').flush(envelope(held));
    fixture.detectChanges();
  }

  /**
   * Asserts that a write provoked NO listing request. ⚠ THE LISTING RE-READ IS CONDITIONAL ON A SEARCH
   * HAVING BEEN CHOSEN. Every write command on the account store ends by re-reading the listing, but that
   * read declines to issue anything while the search state is the opening no-query state - reproducing
   * the legacy screen's fall-through, which left its grid unbound rather than listing every account in
   * the tenant.
   */
  function expectNoListingReRead(): void {
    expect(
      httpMock.match((candidate) => candidate.method === 'GET' && candidate.url === USERS_URL),
    )
      .withContext('no listing read while no search has been chosen')
      .toHaveSize(0);
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(controlId: string): E {
    const element = query<E>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Types into a text control. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    control.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  /** Sets a switch, only dispatching when the state actually changes. */
  function toggle(controlId: string, checked: boolean): void {
    const control = field<HTMLInputElement>(controlId);

    if (control.checked === checked) {
      return;
    }

    control.checked = checked;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** A button by its rendered wording. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** Presses a button by its rendered wording. */
  function press(label: string): void {
    const control = button(label);

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /**
   * Presses a button of the OPEN CONFIRMATION, scoped to the dialogue. ⚠ THE ROW COMMAND AND THE DIALOGUE
   * COMMAND SHARE ONE WORDING — both read "Delete" — and the row command comes first in document order,
   * so an unscoped lookup re-opens the question instead of answering it. The danger button additionally
   * prefixes a warning glyph, so the wording is matched with `includes`.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(control)
      .withContext(`the "${label}" button of the confirmation is offered`)
      .not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** Fills the whole creation form with values that satisfy every rule. */
  function fillCreationForm(password = 'Str0ngPass'): void {
    type(CONTROL_ID.username, 'grace.hopper');
    type(CONTROL_ID.firstName, 'Grace');
    type(CONTROL_ID.lastName, 'Hopper');
    type(CONTROL_ID.displayName, 'Grace Hopper');
    type(CONTROL_ID.email, 'grace@example.test');
    type(CONTROL_ID.password, password);
    type(CONTROL_ID.confirmPassword, password);
  }

  /** Every message the shared field component is rendering, in document order. */
  function fieldErrors(): readonly string[] {
    return queryAll<Element>('.form-field__error').map((node) => (node.textContent ?? '').trim());
  }

  /** The announcements requested through the generic channel, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /** The support reference each generic announcement quoted, oldest first. */
  function announcementReferences(): readonly (string | null)[] {
    return notifySpy.calls
      .allArgs()
      .map((args) => (typeof args[2] === 'string' && args[2].length > 0 ? args[2] : null));
  }

  /** Every string handed to any notification channel. */
  function allAnnouncements(): readonly string[] {
    return [
      ...successSpy.calls.allArgs().map((args) => String(args[0])),
      ...warningSpy.calls.allArgs().map((args) => String(args[0])),
      ...errorSpy.calls.allArgs().map((args) => String(args[0])),
      ...notifySpy.calls.allArgs().map((args) => String(args[1])),
    ];
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — CHOOSING BETWEEN THE TWO MODES
  // ---------------------------------------------------------------------------------------------------

  describe('choosing between the two modes', () => {
    it('reads nothing at all when the address names no account', () => {
      create();

      // Creation has no record to read, so a request here would be a read of somebody else's
      // account provoked by opening an empty form.
      expect(httpMock.match(() => true)).withContext('nothing is read').toHaveSize(0);
      expect(query('app-page-header')?.textContent ?? '').toContain(CREATE_MODE_TITLE);
      expect(button(CREATE_SUBMIT_LABEL)).withContext('the creation command').not.toBeUndefined();
    });

    it('reads account zero, which is a legitimate identifier and not an absence', () => {
      create('0');

      // ⚠ AN EXPLICIT PRESENCE TEST, NEVER A TRUTHINESS TEST. `'0'` parses to zero, and a
      // truthiness test would have opened an empty creation form instead.
      const read = expectRequest('GET', userUrl(0), 'the account read');

      expect(read.request.params.keys()).withContext('no query on a single-resource read').toHaveSize(0);

      read.flush(envelope(account(0)));
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.username).value).toBe('ada.lovelace');
    });

    it('titles an unusable parameter as an edit address, not as a creation', () => {
      create('not-a-number');

      expect(httpMock.match(() => true)).withContext('nothing is read').toHaveSize(0);
      expect(query('app-page-header')?.textContent ?? '').toContain(EDIT_MODE_TITLE);
      expect(query('app-page-header')?.textContent ?? '')
        .withContext('and it must not also claim to be a creation')
        .not.toContain(CREATE_MODE_TITLE);
    });

    it('titles the screen from the record once it arrives, and plainly before it does', () => {
      create('7');

      const read = expectRequest('GET', userUrl(7));

      // A title with an empty name substituted into it would read as a defect, so the plain
      // edit title stands until the record is in hand.
      expect(query('app-page-header')?.textContent ?? '').toContain(EDIT_MODE_TITLE);

      read.flush(envelope(account(7, { displayName: 'Ada Lovelace' })));
      fixture.detectChanges();

      expect(query('app-page-header')?.textContent ?? '').toContain('Edit User - Ada Lovelace (Id: 7)');
    });

    it('falls back to the sign-in name in the title when the display name is empty', () => {
      // The display-name column is declared not-null with an empty-string default, so `''` is
      // the schema's own absent-marker rather than a missing value.
      arriveEditing(account(7, { displayName: '' }));

      expect(query('app-page-header')?.textContent ?? '').toContain('Edit User - ada.lovelace (Id: 7)');
      expect(field<HTMLInputElement>(CONTROL_ID.displayName).value)
        .withContext('written as it arrives, empty string included')
        .toBe('');
    });

    it('offers the three cross-screen actions while editing and none of them while creating', () => {
      create();

      expect(queryAll<HTMLAnchorElement>('app-page-header a')).withContext('none while creating').toHaveSize(0);

      fixture.destroy();
      arriveEditing(account(7));

      const links = queryAll<HTMLAnchorElement>('app-page-header a').map((node) =>
        node.getAttribute('href'),
      );

      expect(links).toContain('/users/7/profile');
      expect(links).toContain('/users/7/password');

      expect(links).toContain('/roles?userId=7');

      // And nothing still points at the bare listing, so the corrected address cannot sit beside a
      // leftover copy of the old one.
      expect(links).not.toContain('/roles');
    });

    it('says so and offers no form when the read is refused as not-found', () => {
      create('7');

      expectRequest('GET', userUrl(7)).flush(
        problem('resource.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // ⚠ STATED BY THE BANNER, AND ONLY BY THE BANNER. Measured at runtime after the banner was given this
      // state: the banner said "This account doesn't exist" while a persistent warning TOAST beside it said
      // the SERVER's own "The requested resource does not exist." and quoted the request's correlation
      // identifier - two owners for one piece of news, in two politeness levels, one of them sending the
      // reader to support for an occurrence support cannot look up.
      expect((query('.error-banner')?.textContent ?? '')).toContain(NO_USER_MESSAGE);
      expect(query('form.user-form')).withContext('no form for an account that does not exist').toBeNull();

      expect(notifySpy.calls.allArgs().map((args) => [String(args[0]), String(args[1])]))
        .withContext('no toast at all for a record that was never there')
        .toEqual([]);
      expect(warningSpy)
        .withContext('no second announcement of the same state')
        .not.toHaveBeenCalled();

      // And the one way out, in the slot the three withheld actions vacated.
      const headerActions = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('app-page-header a.page-action'),
      );

      expect(headerActions.map((anchor) => anchor.textContent?.trim())).toEqual([
        'Back to User Accounts',
      ]);
    });

    it('withholds the create-only controls while editing and offers them while creating', () => {
      arriveEditing(account(7));

      // The legacy panel holding all five was declared invisible and shown only while adding.
      expect(query(`#${CONTROL_ID.authorize}`)).toBeNull();
      expect(query(`#${CONTROL_ID.notify}`)).toBeNull();
      expect(query(`#${CONTROL_ID.randomPassword}`)).toBeNull();
      expect(query(`#${CONTROL_ID.password}`)).toBeNull();
      // The sign-in name is read-only while editing, because there is no rename path.
      expect(field<HTMLInputElement>(CONTROL_ID.username).disabled).toBeTrue();

      fixture.destroy();
      create();

      expect(query(`#${CONTROL_ID.authorize}`)).not.toBeNull();
      expect(query(`#${CONTROL_ID.password}`)).not.toBeNull();
      expect(field<HTMLInputElement>(CONTROL_ID.username).disabled)
        .withContext('a new account is named')
        .toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE ENTRY RULES
  // ---------------------------------------------------------------------------------------------------

  describe('the entry rules', () => {
    it('requires all five identity fields in their measured wording and sends nothing', () => {
      create();

      press(CREATE_SUBMIT_LABEL);

      const messages: readonly string[] = fieldErrors();

      expect(messages).toContain(USERNAME_REQUIRED_MESSAGE);
      expect(messages).toContain(FIRST_NAME_REQUIRED_MESSAGE);
      expect(messages).toContain(LAST_NAME_REQUIRED_MESSAGE);
      expect(messages).toContain(DISPLAY_NAME_REQUIRED_MESSAGE);
      expect(messages).toContain(EMAIL_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
    });

    it('refuses an address that does not match the measured expression', () => {
      create();

      fillCreationForm();
      type(CONTROL_ID.email, 'not-an-address');
      press(CREATE_SUBMIT_LABEL);

      // The framework's own address validator applies a DIFFERENT expression, so the measured
      // legacy one is used and its measured wording travels with the failure.
      expect(fieldErrors()).toContain(EMAIL_PATTERN_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('refuses the addresses the SERVER refuses, from one shared grammar', () => {
      // ⚠ MEASURED DEFECT: this screen and the portal form each declared an address pattern of their own and
      // the two disagreed - with each other and with the server. This screen's pattern admitted a final
      // domain label of ONE character, admitted digits and hyphens in it, and imposed no length bound, so
      // `a@b.c` and `a@b.c1` passed here and were then refused by the server. Both screens now use the one
      // client grammar in `core/utils/email-grammar.util.ts`, which mirrors the server's single definition
      // in `Domain/ValueObjects/EmailAddress` class for class.
      for (const refused of ['a@b.c', 'grace@example.c1', 'grace@example', 'grace@.test']) {
        create();
        fillCreationForm();
        type(CONTROL_ID.email, refused);
        press(CREATE_SUBMIT_LABEL);

        expect(fieldErrors())
          .withContext(`${refused} is refused by the server, so it must be refused here`)
          .toContain(EMAIL_PATTERN_MESSAGE);
        expect(httpMock.match(() => true)).toHaveSize(0);
      }
    });

    it('accepts an address carrying a plus-addressed mailbox', () => {
      create();

      fillCreationForm();
      type(CONTROL_ID.email, 'grace+admin@sub.example.test');
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);

      expect((write.request.body as CreateUserRequest).email).toBe('grace+admin@sub.example.test');

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('refuses a mismatched confirmation before it judges the policy', () => {
      create();

      fillCreationForm();
      type(CONTROL_ID.password, 'Str0ngPass');
      type(CONTROL_ID.confirmPassword, 'Str0ngPassX');
      press(CREATE_SUBMIT_LABEL);

      // ⚠ THE ORDER IS LOAD-BEARING. The legacy compared the pair first and judged the policy
      // only if they matched, so a mismatch of two weak values reports the mismatch.
      expect(host().textContent ?? '').toContain(PASSWORD_MISMATCH_MESSAGE);
      expect(host().textContent ?? '').not.toContain(INVALID_PASSWORD_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('refuses a credential below the measured minimum length', () => {
      create();

      fillCreationForm('Str0ng');

      press(CREATE_SUBMIT_LABEL);

      // Seven characters is the measured configured minimum, and zero non-alphanumeric ones.
      // The sentence interpolates both, exactly as the legacy substitution produced at run time.
      expect(host().textContent ?? '').toContain(INVALID_PASSWORD_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    // ⚠ THE NEXT TWO GUARD A FOCUS MOVE NO OTHER MECHANISM CAN MAKE. The password rule is a GROUP rule: it
    // marks the form invalid and leaves both boxes individually valid, so neither carries Angular's invalid
    // class and the shared first-invalid directive correctly declines.
    it('moves focus to the password box when the rule is the only reason a submit was refused', () => {
      create();

      fillCreationForm('Str0ng');
      press(CREATE_SUBMIT_LABEL);

      const box = query<HTMLInputElement>(`#${CONTROL_ID.password}`);

      expect(host().textContent ?? '').toContain(INVALID_PASSWORD_MESSAGE);
      expect(document.activeElement)
        .withContext('the group rule names no control, so the component supplies the focus')
        .toBe(box);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    // ⚠ THIS IS THE CASE THAT REGRESSED, AND IT REGRESSED SILENTLY. The first implementation deferred the
    // focus move to a resolved promise, on the assumption that a microtask settles after change detection.
    it('opens the collapsed password disclosure and still lands focus inside it', () => {
      create();

      const collapse = queryAll<HTMLButtonElement>('button.user-form__section-toggle').find(
        (candidate) => (candidate.textContent ?? '').toLowerCase().includes('password'),
      );

      expect(collapse).withContext('the password section is collapsible').toBeDefined();
      (collapse as HTMLButtonElement).click();
      fixture.detectChanges();

      // The disclosure REMOVES its body rather than hiding it, which is precisely why the focus move
      // cannot be made before the reopened section has rendered.
      expect(query(`#${CONTROL_ID.password}`))
        .withContext('collapsing takes the box out of the document')
        .toBeNull();

      // Every other field valid, and the password pair never touched — so the group rule is the sole
      // fault, exactly as it is for an operator who never opened the section.
      type(CONTROL_ID.username, 'grace.hopper');
      type(CONTROL_ID.firstName, 'Grace');
      type(CONTROL_ID.lastName, 'Hopper');
      type(CONTROL_ID.displayName, 'Grace Hopper');
      type(CONTROL_ID.email, 'grace@example.test');

      press(CREATE_SUBMIT_LABEL);

      const reopened = queryAll<HTMLButtonElement>('button.user-form__section-toggle').find(
        (candidate) => (candidate.textContent ?? '').toLowerCase().includes('password'),
      );
      const box = query<HTMLInputElement>(`#${CONTROL_ID.password}`);

      expect(reopened?.getAttribute('aria-expanded'))
        .withContext('a message about a field nobody can see is not actionable')
        .toBe('true');
      expect(box).withContext('and the box is back in the document').not.toBeNull();
      expect(document.activeElement)
        .withContext('no settle, no fake clock: the render is synchronous and so is the focus')
        .toBe(box);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('accepts a credential of exactly the minimum length with no punctuation at all', () => {
      create();

      // Zero non-alphanumeric characters are required, so a purely alphanumeric value passes.
      fillCreationForm('Str0ng7');
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('withdraws both credential fields and both rules once generation is chosen', () => {
      create();

      type(CONTROL_ID.username, 'grace.hopper');
      type(CONTROL_ID.firstName, 'Grace');
      type(CONTROL_ID.lastName, 'Hopper');
      type(CONTROL_ID.displayName, 'Grace Hopper');
      type(CONTROL_ID.email, 'grace@example.test');
      toggle(CONTROL_ID.randomPassword, true);

      // Generation REPLACES both rules rather than relaxing them, so the two inputs go away.
      expect(query(`#${CONTROL_ID.password}`)).withContext('no credential to type').toBeNull();
      expect(query(`#${CONTROL_ID.confirmPassword}`)).toBeNull();

      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);
      const body = write.request.body as CreateUserRequest;

      expect(body.password.length)
        .withContext('a generated credential comfortably clears the minimum')
        .toBeGreaterThanOrEqual(7);
      expect(body.confirmPassword)
        .withContext('a generated credential confirms itself')
        .toBe(body.password);

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('says nothing about validity before a person has acted', () => {
      create();

      expect(fieldErrors()).toHaveSize(0);
      expect(queryAll('[aria-invalid="true"]')).toHaveSize(0);
    });

    it('applies no credential rule at all while editing, because there is no credential field', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      // The password block does not exist while editing, so its group validator must stand down
      // rather than block a details edit over two controls nobody can see.
      const write = expectRequest('PUT', userUrl(7));

      expect(host().textContent ?? '').not.toContain(INVALID_PASSWORD_MESSAGE);

      write.flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — CREATING AN ACCOUNT
  // ---------------------------------------------------------------------------------------------------

  describe('creating an account', () => {
    it('still announces a creation that settles AFTER the operator has left the screen', () => {
      // ⚠ THE MEASURED DEFECT, SHARED WITH THE SIBLING ROLE FORM AND FIXED THE SAME WAY ON BOTH. The write
      // bridges here are effects in this component's injection context, so they die WITH the component: an
      // operator who submits and then immediately clicks somewhere else destroys the only party that was
      // going to tell them what happened.
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL, 'the creation');

      // Destroying the fixture is exactly what a route change does, and it is what tore the bridge down.
      fixture.destroy();
      successSpy.calls.reset();

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      TestBed.flushEffects();

      expect(successSpy)
        .withContext('named from the account the SERVER stored, by the party that outlived the screen')
        .toHaveBeenCalledWith(USER_CREATED_MESSAGE.replace('{name}', 'ada.lovelace'));
    });

    it('says NOTHING when a creation that outlived the screen was refused', () => {
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL, 'the creation');

      fixture.destroy();
      successSpy.calls.reset();

      write.flush(
        { type: 'urn:test', title: 'Conflict', status: 409, detail: 'That account name is taken.' },
        { status: 409, statusText: 'Conflict' },
      );
      TestBed.flushEffects();

      expect(successSpy).not.toHaveBeenCalled();
    });

    it('posts the eight declared members and answers 201', () => {
      create();

      fillCreationForm();
      toggle(CONTROL_ID.authorize, true);
      toggle(CONTROL_ID.notify, false);
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL, 'the creation');
      const body = write.request.body as CreateUserRequest;

      expect(Object.keys(body as unknown as Record<string, unknown>).sort()).toEqual([
        'authorize',
        'confirmPassword',
        'displayName',
        'email',
        'firstName',
        'lastName',
        'password',
        'username',
      ]);
      // MIGRATION: nothing is trimmed, upper-cased or otherwise normalised on the way out. It
      // matters most for the credential, where a trim would alter the value itself.
      expect(body.username).toBe('grace.hopper');
      expect(body.firstName).toBe('Grace');
      expect(body.lastName).toBe('Hopper');
      expect(body.displayName).toBe('Grace Hopper');
      expect(body.email).toBe('grace@example.test');
      expect(body.password).toBe('Str0ngPass');
      expect(body.confirmPassword).toBe('Str0ngPass');
      expect(body.authorize).toBeTrue();

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('sends the authorisation choice as false when it is cleared, never as an omission', () => {
      create();

      fillCreationForm();
      toggle(CONTROL_ID.authorize, false);
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);
      const body = write.request.body as CreateUserRequest;

      expect('authorize' in (body as unknown as Record<string, unknown>)).toBeTrue();
      expect(body.authorize).withContext('false is data, not an absence').toBeFalse();

      write.flush(envelope(account(9, { isApproved: false })), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('leaves for the listing once the account exists', () => {
      create();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      expectNoListingReRead();

      // Measured as a redirect to the return address, whose equivalent here is a navigation.
      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    // ⚠ THE SAFE EXIT, WHICH THIS SCREEN DID NOT HAVE. Measured across the four record editors: the portal, the
    // signup and the role editors all offered a way to abandon a half-filled form and this one did not, so the
    // only way out was the browser's own controls. It is an ADDED affordance rather than a ported one - the
    // legacy command panel carried remove and save and nothing else - and it is recorded as such.
    it('offers a way to abandon the form, and it departs rather than resetting', () => {
      create();

      fillCreationForm();

      const cancel = button('Cancel');

      expect(cancel).withContext('the screen offers an abandon command').not.toBeUndefined();
      expect(cancel?.type)
        .withContext('it cannot submit, so no validator runs and an invalid form cannot disable the way out')
        .toBe('button');

      press('Cancel');

      // A DEPARTURE, and specifically not a replacing one: only a navigation the application itself makes
      // after a successful write is exempt from the unsaved-entry question, and abandoning a form is the
      // operator's decision. The gate is what asks; this screen only has to leave through the router so the
      // gate can see it.
      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users']);
      expect(httpMock.match(() => true))
        .withContext('and nothing is written on the way out')
        .toHaveSize(0);
    });

    // ⚠ #22 — THE SHARED PLACEMENT. This bar centred its commands with a wider gap while the portal, module,
    // role and password forms all start-align theirs with `--space-2`, so the primary action moved
    // horizontally as an operator walked between screens - one of the three placements the review counted
    // across four create screens.
    it('places its commands where every sibling form places them', () => {
      create();

      const bar = query('.user-form__actions');

      expect(bar).withContext('the commands sit in a bar of their own').not.toBeNull();

      const style = getComputedStyle(bar as HTMLElement);

      expect(style.display).withContext('a flex row, as the siblings are').toBe('flex');
      expect(style.flexWrap).withContext('wrapping rather than overflowing').toBe('wrap');
      expect(['normal', 'flex-start', 'start'])
        .withContext('start-aligned, not centred')
        .toContain(style.justifyContent);
      // The gap is compared against the token itself rather than a restated number, so the assertion moves
      // with the vocabulary instead of having to be kept in step with it by hand.
      const root = getComputedStyle(document.documentElement);
      const step = `${Number.parseFloat(root.getPropertyValue('--space-2')) * Number.parseFloat(root.fontSize)}px`;

      expect(style.columnGap)
        .withContext('and spaced by the same step the siblings use')
        .toBe(step);
    });

    it('marks the saving command as the primary one, as its three sibling editors do', () => {
      create();

      const submit = button(CREATE_SUBMIT_LABEL);

      expect(submit?.classList)
        .withContext('the shared primary treatment, so the operator can see which command saves')
        .toContain('form-action--primary');
      expect(button('Cancel')?.classList)
        .withContext('and the safe exit is deliberately not primary')
        .not.toContain('form-action--primary');
    });

    it('leaves the form settled at the instant it navigates, so the guard cannot question a stored account', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      create();
      fillCreationForm();

      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is what the guard exists to catch')
        .toBeTrue();

      let dirtyAtNavigation: boolean | null = null;
      navigateSpy.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      expectNoListingReRead();

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], {
        queryParams: {},
        replaceUrl: true,
      });
      expect(dirtyAtNavigation)
        .withContext('the guard must see a settled form on the navigation the creation itself triggered')
        .toBeFalse();
    });

    // ⚠ MAJOR (CWE-316 cleartext storage) — WHAT HAPPENS TO THE TYPED CREDENTIAL AFTER THE ACCOUNT EXISTS.

    describe('discarding the typed credential once the account exists', () => {
      it('empties both credential controls and both boxes before navigating away', () => {
        create();
        fillCreationForm('Str0ngPass');

        // On the screen before the write, which is what makes the assertion after it mean something.
        expect(field<HTMLInputElement>(CONTROL_ID.password).value).toBe('Str0ngPass');
        expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).value).toBe('Str0ngPass');

        press(CREATE_SUBMIT_LABEL);
        expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
          status: 201,
          statusText: 'Created',
        });
        fixture.detectChanges();
        expectNoListingReRead();

        expect(query<HTMLInputElement>(`#${CONTROL_ID.password}`)?.value ?? '').toBe('');
        expect(query<HTMLInputElement>(`#${CONTROL_ID.confirmPassword}`)?.value ?? '').toBe('');
        expect(host().outerHTML)
          .withContext('the credential appears nowhere in the document')
          .not.toContain('Str0ngPass');
      });

      it('empties them even when the navigation is REFUSED, which is the case that matters', () => {
        // A guard answering false is the ordinary way this happens. The account already exists, so the
        // credential is stored and useless here - and the screen stays mounted, which is why relying on the
        // navigation to dispose of it was not enough.
        navigateSpy.and.resolveTo(false);

        create();
        fillCreationForm('Str0ngPass');
        press(CREATE_SUBMIT_LABEL);
        expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
          status: 201,
          statusText: 'Created',
        });
        fixture.detectChanges();
        expectNoListingReRead();

        expect(field<HTMLInputElement>(CONTROL_ID.password).value).toBe('');
        expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).value).toBe('');
        expect(host().outerHTML).not.toContain('Str0ngPass');
      });

      it('empties them even when the navigation REJECTS', () => {
        // The other way a navigation fails to complete: a rejected promise, which a failed lazy chunk
        // produces. The clearing happens before the call, so the rejection cannot affect it - and the
        // rejection itself is handled rather than left unhandled.
        navigateSpy.and.rejectWith(new Error('a chunk failed to load'));

        create();
        fillCreationForm('Str0ngPass');
        press(CREATE_SUBMIT_LABEL);
        expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
          status: 201,
          statusText: 'Created',
        });
        fixture.detectChanges();
        expectNoListingReRead();

        expect(field<HTMLInputElement>(CONTROL_ID.password).value).toBe('');
        expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).value).toBe('');
        expect(host().outerHTML).not.toContain('Str0ngPass');
      });

      it('leaves the entry an operator can re-read alone, so a refused trip is not lost work', () => {
        // The counterpart, and the reason the whole form is not wiped. Only the two credential controls are
        // secrets; clearing the name or the address would turn a refused navigation into lost work for no
        // security gain, since every one of those values is on the screen to be read.
        navigateSpy.and.resolveTo(false);

        create();
        fillCreationForm('Str0ngPass');
        press(CREATE_SUBMIT_LABEL);
        expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
          status: 201,
          statusText: 'Created',
        });
        fixture.detectChanges();
        expectNoListingReRead();

        expect(field<HTMLInputElement>(CONTROL_ID.username).value).toBe('grace.hopper');
        expect(field<HTMLInputElement>(CONTROL_ID.firstName).value).toBe('Grace');
        expect(field<HTMLInputElement>(CONTROL_ID.email).value).toBe('grace@example.test');
      });

      it('shows no complaint about the credential it has just discarded', () => {
        // Cleared with `reset`, not with a value assignment, so each control returns to pristine and
        // untouched along with its value.
        navigateSpy.and.resolveTo(false);

        create();
        fillCreationForm('Str0ngPass');
        press(CREATE_SUBMIT_LABEL);
        expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
          status: 201,
          statusText: 'Created',
        });
        fixture.detectChanges();
        expectNoListingReRead();

        expect(fieldErrors())
          .withContext('no message about a credential that is gone on purpose')
          .toEqual([]);
      });

      it('empties them on the GENERATED path too, which does not navigate at all', () => {
        // ⚠ THE WORST CASE OF THE THREE, AND THE ONE NO NAVIGATION COULD EVER HAVE COVERED. When the screen
        // generates the credential it holds the account open so the operator can write the value down, and
        // there is no field for a typed credential in that state - but a person who TYPES a password and
        // then chooses generation still leaves the typed value in the control behind the panel.
        create();

        type(CONTROL_ID.username, 'grace.hopper');
        type(CONTROL_ID.firstName, 'Grace');
        type(CONTROL_ID.lastName, 'Hopper');
        type(CONTROL_ID.displayName, 'Grace Hopper');
        type(CONTROL_ID.email, 'grace@example.test');
        type(CONTROL_ID.password, 'Str0ngPass');
        type(CONTROL_ID.confirmPassword, 'Str0ngPass');
        toggle(CONTROL_ID.randomPassword, true);
        press(CREATE_SUBMIT_LABEL);

        const write = expectRequest('POST', USERS_URL);
        const generated: string = (write.request.body as CreateUserRequest).password;

        expect(generated)
          .withContext('the credential sent is the generated one, not the typed one')
          .not.toBe('Str0ngPass');

        write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
        fixture.detectChanges();
        expectNoListingReRead();

        // The screen deliberately stays: no navigation has been requested, because the hand-over is
        // pending.
        expect(navigateSpy).withContext('the redirect is deferred, not dropped').not.toHaveBeenCalled();

        // And the TYPED credential is gone even though nothing navigated.
        expect(host().outerHTML)
          .withContext('the typed credential is discarded even with the panel open')
          .not.toContain('Str0ngPass');

        // The GENERATED one is deliberately still disclosed - that is the hand-over this panel exists for,
        // and it is the one credential this screen is supposed to be showing.
        expect(host().textContent ?? '')
          .withContext('the one-time hand-over is unaffected')
          .toContain(generated);
      });
    });

    it('re-reads the listing after a write once a search HAS been chosen', () => {
      create();

      // The store declines the listing read while the search is the opening no-query state, so
      // the search is chosen first - which is what a person arriving from the listing has done.
      const store = TestBed.inject(UserStore);

      store.showAllAccounts();
      fixture.detectChanges();
      expectRequest('GET', USERS_URL, 'the chosen listing').flush(emptyPage());
      fixture.detectChanges();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      // NOW the re-read happens: the page in hand is stale the moment an account is added to it.
      expectRequest('GET', USERS_URL, 'the listing re-read').flush(emptyPage());
      fixture.detectChanges();

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('states the notification gap BESIDE the box, while there is still a decision to make', () => {
      create();

      const box = query<HTMLInputElement>(`#${CONTROL_ID.notify}`);

      expect(box).withContext('the control is still rendered').not.toBeNull();
      expect((box as HTMLInputElement).checked).withContext('unticked').toBeFalse();
      expect((box as HTMLInputElement).disabled).withContext('and not offered').toBeTrue();

      const notifyField: HTMLElement | null =
        (box as HTMLInputElement).closest('.form-field') ?? null;

      expect(notifyField).withContext('the control sits in a labelled field').not.toBeNull();

      const help: HTMLButtonElement | null =
        (notifyField as HTMLElement).querySelector<HTMLButtonElement>('.form-field__help-toggle');

      expect(help).withContext('the notify field offers a help affordance').not.toBeNull();
      expect((notifyField as HTMLElement).textContent ?? '')
        .withContext('and says nothing until asked, like every other field')
        .not.toContain(NOTIFY_UNAVAILABLE_ADVISORY);

      (help as HTMLButtonElement).click();
      fixture.detectChanges();

      expect((notifyField as HTMLElement).textContent ?? '')
        .withContext('the reason the box cannot act, stated where the box is')
        .toContain(NOTIFY_UNAVAILABLE_ADVISORY);

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', USERS_URL).flush(envelope(account(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      // And nothing is said afterwards about a choice the operator was never offered.
      expect(warningSpy.calls.allArgs().map((args) => String(args[0])))
        .withContext('no post-hoc advisory about the notification gap')
        .not.toContain(NOTIFY_UNAVAILABLE_ADVISORY);

      expectNoListingReRead();
    });

    it('advises the operator when it generated the credential itself', () => {
      create();

      type(CONTROL_ID.username, 'grace.hopper');
      type(CONTROL_ID.firstName, 'Grace');
      type(CONTROL_ID.lastName, 'Hopper');
      type(CONTROL_ID.displayName, 'Grace Hopper');
      type(CONTROL_ID.email, 'grace@example.test');
      toggle(CONTROL_ID.randomPassword, true);
      toggle(CONTROL_ID.notify, false);
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);
      const generated: string = (write.request.body as CreateUserRequest).password;

      // ⚠ THE ADVISORY MUST NOT CARRY THE CREDENTIAL. Generation moved to the browser because the contract
      // has no field with which to request it, which makes the operator responsible for conveying it — but
      // the announcement channel is not where it may be conveyed.
      expect(warningSpy.calls.allArgs().map((args) => String(args[0])).join(' ')).not.toContain(
        generated,
      );

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('words a duplicate sign-in name from the failure code rather than from the status', () => {
      create();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(
        problem(
          'user.create.username_already_exists',
          409,
          'The request conflicts with the current state of the resource.',
        ),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // ⚠ A CONFLICT IS AN ERROR-SEVERITY REFUSAL, SO THE BANNER OWNS IT AND THE TRANSIENT
      // CHANNEL STAYS SILENT. The server's own sentence is what the operator reads.
      const banner = query('app-error-banner');

      expect(banner?.textContent ?? '').toContain(
        'The request conflicts with the current state of the resource.',
      );
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
      expect(errorSpy).withContext('the banner carries it, not a transient').not.toHaveBeenCalled();

      expect(host().textContent ?? '').not.toContain(USER_NAME_EXISTS);

      expect(navigateSpy).withContext('the operator stays put').not.toHaveBeenCalled();
      expect(httpMock.match(() => true)).withContext('no follow-up read').toHaveSize(0);
    });

    it('words a coded refusal from the vocabulary when it arrives as a warning', () => {
      create();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      // A 401 resolves to warning severity, which is the path on which the code-specific
      // sentence IS selected - so this is where the vocabulary can be proved at all.
      expectRequest('POST', USERS_URL).flush(
        problem('user.create.invalid_password', 401, 'Authentication is required to reach this resource.'),
        { status: 401, statusText: 'Unauthorized' },
      );
      fixture.detectChanges();

      expect(notifications().map((entry) => entry.message)).toContain(
        'The password specified is invalid. Please specify a valid password.',
      );
      expect(notifications().map((entry) => entry.severity)).toContain('warning');
    });

    it('words the tenant allowance from the refused operation, not from the status alone', () => {
      create();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // The same status means the allowance while creating and a permission refusal while
      // editing, so the refused OPERATION is what selects the sentence.
      expect((host().textContent ?? '').toLowerCase()).toContain(EXCEEDED_USER_QUOTA_MESSAGE_FRAGMENT);
      expect(host().textContent ?? '').not.toContain(NOT_AUTHORIZED_MESSAGE);
    });

    it('pins a per-field refusal to the control the server named', () => {
      create();

      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);
      expectRequest('POST', USERS_URL).flush(
        problem('user.create.invalid_username', 400, 'The request could not be processed as submitted.', {
          Username: ['That sign-in name is not permitted.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // ⚠ THE SERVER'S KEY IS ITS OWN MODEL-STATE SPELLING, NOT CAMEL-CASED, so the match is
      // case-insensitive. Spelling it exactly would silently find nothing.
      expect(fieldErrors()).toContain('That sign-in name is not permitted.');
      expect(field<HTMLInputElement>(CONTROL_ID.username).getAttribute('aria-invalid')).toBe('true');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — EDITING AN ACCOUNT
  // ---------------------------------------------------------------------------------------------------

  describe('editing an account', () => {
    it('puts exactly the four editable members and answers 200', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      type(CONTROL_ID.lastName, 'King');
      type(CONTROL_ID.displayName, 'Augusta King');
      type(CONTROL_ID.email, 'augusta@example.test');
      press(UPDATE_SUBMIT_LABEL);

      const write = expectRequest('PUT', userUrl(7), 'the update');
      const body = write.request.body as UpdateUserRequest;

      // ⚠ FOUR EDITABLE MEMBERS AND NO MORE, PLUS THE REVISION MARKER. No sign-in name, because there is
      // no rename path; no authorisation flag, no lockout flag and no credential, because each has its own
      // endpoint — which is what stops a details edit from carrying an authorisation change.
      //
      // The fifth member is not a field. `concurrencyToken` is the revision the submission was composed
      // against, echoed back verbatim so the server can answer `409` rather than silently overwriting a
      // colleague's concurrent edit, which is what this screen used to do.
      expect(Object.keys(body as unknown as Record<string, unknown>).sort()).toEqual([
        'concurrencyToken',
        'displayName',
        'email',
        'firstName',
        'lastName',
      ]);
      expect(body).toEqual({
        firstName: 'Augusta',
        lastName: 'King',
        displayName: 'Augusta King',
        email: 'augusta@example.test',
        concurrencyToken: 'account-revision-token',
      });

      write.flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('addresses account zero untouched', () => {
      arriveEditing(account(0));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      const write = expectRequest('PUT', userUrl(0), 'the update of account zero');

      expect(write.request.url).toBe('/api/v1/users/0');

      write.flush(envelope(account(0, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('sends nothing at all for a pristine form', () => {
      arriveEditing(account(7));

      press(UPDATE_SUBMIT_LABEL);

      // Reproduces the legacy guard on the editor's own dirty flag. A save that changed nothing
      // still cost a round trip and still touched the record's audit trail.
      expect(httpMock.match(() => true)).withContext('nothing to write').toHaveSize(0);
    });

    it('ANSWERS a pristine press instead of ignoring it', () => {
      // ⚠ THE MEASURED DEFECT THIS CLOSES. Skipping the write is right; doing it in silence is not. Runtime
      // testing pressed Update on an untouched form and got no request, no navigation, no message and no
      // change anywhere on screen - indistinguishable from a broken button. The legacy screen had a reload to
      // stand in for the acknowledgement (`ManageUsers.ascx.vb` L918 redirected to the same address); a
      // single-page application has to say it.
      arriveEditing(account(7));

      press(UPDATE_SUBMIT_LABEL);

      expect(httpMock.match(() => true)).withContext('still no write').toHaveSize(0);
      expect(infoSpy).toHaveBeenCalledWith(NO_CHANGES_MESSAGE);
      expect(navigateSpy).withContext('and the operator is not moved anywhere').not.toHaveBeenCalled();
    });

    it('announces the measured wording and stays on the screen', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();

      expect(successSpy).toHaveBeenCalledWith(USER_UPDATED_MESSAGE);
      expect(navigateSpy).withContext('the operator stays put').not.toHaveBeenCalled();
    });

    it('returns the form to pristine after a successful update, so a second press sends nothing', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();

      press(UPDATE_SUBMIT_LABEL);

      expect(httpMock.match(() => true)).withContext('nothing further to write').toHaveSize(0);
    });

    it('words a duplicate address refusal from the measured conflict sentence', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.email, 'taken@example.test');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('user.create.duplicate_email', 409, 'The request conflicts with the current state of the resource.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(host().textContent ?? '').not.toContain(EMAIL_CONFLICT_MESSAGE);
      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'The request conflicts with the current state of the resource.',
      );
      // The form stays usable, which is the right answer: a duplicate address is corrected here.
      expect(field<HTMLInputElement>(CONTROL_ID.email).disabled).toBeFalse();
      expect(field<HTMLInputElement>(CONTROL_ID.email).value)
        .withContext('the entry survives for correction')
        .toBe('taken@example.test');
      expect(httpMock.match(() => true)).withContext('no follow-up read').toHaveSize(0);
    });

    it('withholds every control and states why when the server refuses authority', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // MIGRATION — A DELIBERATE DIVERGENCE: the legacy routine named for disabling actually HID all six
      // panels. The form is kept visible and disabled so the operator can see what the refusal refers to;
      // withholding the context along with the affordance makes a refusal unreadable.
      expect(host().textContent ?? '').toContain(NOT_AUTHORIZED_MESSAGE);
      expect(field<HTMLInputElement>(CONTROL_ID.firstName).disabled).withContext('withheld').toBeTrue();
      expect(button(UPDATE_SUBMIT_LABEL)?.disabled).toBeTrue();
    });

    it('QUOTES the support reference when a refusal is announced', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications().map((entry) => entry.severity))
        .withContext('a refusal is a warning, not a fault')
        .toContain('warning');
      expect(announcementReferences())
        .withContext('and quotes the identifier the server recorded the refusal under')
        .toContain(FIXTURE_CORRELATION_ID);
    });

    it('quotes NO reference when the refusal carried none to quote', () => {
      // ⚠ A REFUSAL DOCUMENT WITHOUT A CORRELATION MEMBER, NOT A TRANSPORT FAILURE. Written first as a
      // dropped connection, which does not exercise this screen at all: a transport failure is announced by
      // the shared HTTP failure interceptor, not by this component, so the case measured zero announcements
      // and failed for a reason that had nothing to do with references.
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        {
          type: 'urn:dnnmigration:error:auth.not_permitted',
          title: 'Forbidden',
          status: 403,
          detail: 'The authenticated caller is not permitted to perform this operation.',
        },
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications().map((entry) => entry.severity))
        .withContext('the refusal is still reported, and still as a warning')
        .toContain('warning');
      expect(announcementReferences().filter((quoted) => quoted !== null))
        .withContext('with nothing quoted, because there was nothing to quote')
        .toEqual([]);
    });

    it('withholds the submit command while a write is outstanding', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      const write = expectRequest('PUT', userUrl(7));

      fixture.detectChanges();

      expect(button(UPDATE_SUBMIT_LABEL)?.disabled).withContext('withheld while saving').toBeTrue();

      write.flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — THE FOUR MEMBERSHIP TRANSITIONS
  // ---------------------------------------------------------------------------------------------------

  describe('the four membership transitions', () => {
    it('sets approval to true through one state-carrying endpoint and answers 204', () => {
      arriveEditing(account(7, { isApproved: false }));

      press(AUTHORIZE_LABEL);

      const write = expectRequest('PUT', `${userUrl(7)}/approval`, 'the approval');

      expect(write.request.params.get('isApproved')).toBe('true');
      expect(write.request.body).withContext('the state travels in the query').toBeNull();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // Parity requires the panel to reflect the new state, so the store re-reads the account
      // AND the listing. The re-read is delegated; a second one here would race the first.
      expectRequest('GET', userUrl(7), 'the account re-read').flush(
        envelope(account(7, { isApproved: true })),
      );
      fixture.detectChanges();
      expectNoListingReRead();

      expect(successSpy).toHaveBeenCalledWith(USER_AUTHORIZED_MESSAGE);
    });

    it('sets approval to false with false transmitted as data', () => {
      arriveEditing(account(7, { isApproved: true }));

      press(UNAUTHORIZE_LABEL);

      const write = expectRequest('PUT', `${userUrl(7)}/approval`);

      expect(write.request.params.get('isApproved'))
        .withContext('false is transmitted, never omitted')
        .toBe('false');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7, { isApproved: false })));
      fixture.detectChanges();
      expectNoListingReRead();

      expect(successSpy).toHaveBeenCalledWith(USER_UNAUTHORIZED_MESSAGE);
    });

    it('states the mail reduction when it authorises, because that half has no endpoint', () => {
      arriveEditing(account(7, { isApproved: false }));

      press(AUTHORIZE_LABEL);

      // ⚠ NOTHING IS ANNOUNCED BEFORE THE SERVER HAS AGREED, and the ordering is the assertion.
      expect(warningSpy).withContext('not before the server has answered').not.toHaveBeenCalled();

      expectRequest('PUT', `${userUrl(7)}/approval`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7, { isApproved: true })));
      fixture.detectChanges();

      expect(warningSpy).withContext('the operator is told, once it has actually happened')
        .toHaveBeenCalledOnceWith(AUTHORIZE_MAIL_ADVISORY);

      expectNoListingReRead();
    });

    it('states no mail reduction for the sibling transitions, because none of them sent mail', () => {
      arriveEditing(account(7, { isApproved: true }));

      press(UNAUTHORIZE_LABEL);

      expectRequest('PUT', `${userUrl(7)}/approval`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7, { isApproved: false })));
      fixture.detectChanges();

      expect(warningSpy).withContext('nothing to reduce').not.toHaveBeenCalled();

      expectNoListingReRead();
    });

    it('releases a locked-out account with a bodyless post and answers 204', () => {
      arriveEditing(account(7, { isLockedOut: true }));

      press(UNLOCK_LABEL);

      const write = expectRequest('POST', `${userUrl(7)}/unlock`, 'the release');

      expect(write.request.body).withContext('no body: the address is the whole request').toBeNull();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7, { isLockedOut: false })));
      fixture.detectChanges();
      expectNoListingReRead();

      expect(successSpy).toHaveBeenCalledWith(USER_UNLOCKED_MESSAGE);
    });

    it('obliges a credential change without choosing, generating or disclosing one', () => {
      arriveEditing(account(7, { mustChangePassword: false }));

      press(FORCE_PASSWORD_LABEL);

      const write = expectRequest('POST', `${userUrl(7)}/require-password-change`, 'the obligation');

      expect(write.request.body).toBeNull();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ THIS TRANSITION RE-READS THE ACCOUNT ONLY. Nothing about the listing changed, so the
      // store does not re-read it — and a case expecting one here would time out.
      expectRequest('GET', userUrl(7), 'the account re-read').flush(
        envelope(account(7, { mustChangePassword: true })),
      );
      fixture.detectChanges();

      expect(successSpy).toHaveBeenCalledWith(PASSWORD_CHANGE_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('the listing is untouched').toHaveSize(0);
    });

    it('validates nothing for any of the four, so an incomplete form cannot block one', () => {
      arriveEditing(account(7, { isLockedOut: true }));

      // Empty a required field. All four transitions carried `causesvalidation="False"`, so
      // authorising an account has nothing to do with whether its display name is filled in.
      type(CONTROL_ID.displayName, '');

      press(UNLOCK_LABEL);

      expectRequest('POST', `${userUrl(7)}/unlock`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('withholds every offered transition while a refusal has withheld the form', () => {
      // ⚠ "ALL FOUR AT ONCE" IS UNREACHABLE BY CONSTRUCTION, which is why this case no longer claims it.
      // Authorize is offered for an account that may NOT sign in and UnAuthorize for one that may, so the
      // two are complements of a single fact and exactly one of them is ever present.
      arriveEditing(
        account(7, { isApproved: false, isLockedOut: true, mustChangePassword: false }),
      );

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // Each offered control is present AND disabled. `?.disabled` is deliberately not used here: on an
      // absent control it yields `undefined`, which no longer distinguishes "withheld by the refusal" from
      // "never offered at all" — and telling those two apart is the whole point.
      for (const label of [AUTHORIZE_LABEL, UNLOCK_LABEL, FORCE_PASSWORD_LABEL]) {
        const control = button(label);

        expect(control).withContext(`"${label}" is offered for this account`).not.toBeUndefined();
        expect((control as HTMLButtonElement).disabled)
          .withContext(`"${label}" is withheld by the refusal`)
          .toBeTrue();
      }

      expect(button(UNAUTHORIZE_LABEL))
        .withContext('the complement of Authorize is not offered alongside it')
        .toBeUndefined();
    });

    it('offers UnAuthorize and not Authorize for an account that may sign in', () => {
      arriveEditing(account(7, { isApproved: true }));

      expect(button(UNAUTHORIZE_LABEL))
        .withContext('cmdUnAuthorize.Visible = Membership.Approved')
        .not.toBeUndefined();
      expect(button(AUTHORIZE_LABEL))
        .withContext('cmdAuthorize.Visible = Not Membership.Approved')
        .toBeUndefined();
    });

    it('offers Authorize and not UnAuthorize for an account that may not sign in', () => {
      // The complement of the case above. Asserted separately rather than by re-reading the account
      // inside one case, so that a failure names WHICH direction broke.
      arriveEditing(account(7, { isApproved: false }));

      expect(button(AUTHORIZE_LABEL)).not.toBeUndefined();
      expect(button(UNAUTHORIZE_LABEL)).toBeUndefined();
    });

    it('offers Unlock only for a locked-out account', () => {
      arriveEditing(account(7, { isLockedOut: false }));

      expect(button(UNLOCK_LABEL))
        .withContext('cmdUnLock.Visible = Membership.LockedOut')
        .toBeUndefined();
    });

    it('offers Force Password Change only while the obligation is not already recorded', () => {
      arriveEditing(account(7, { mustChangePassword: true }));

      expect(button(FORCE_PASSWORD_LABEL))
        .withContext('cmdPassword.Visible = Not Membership.UpdatePassword')
        .toBeUndefined();
    });

    it('offers none of the four to an operator editing their own account', () => {
      signedInAs(7);
      arriveEditing(
        account(7, { isApproved: false, isLockedOut: true, mustChangePassword: false }),
      );

      expect(button(AUTHORIZE_LABEL)).toBeUndefined();
      expect(button(UNAUTHORIZE_LABEL)).toBeUndefined();
      expect(button(UNLOCK_LABEL)).toBeUndefined();
      expect(button(FORCE_PASSWORD_LABEL)).toBeUndefined();
    });

    it('offers them to an operator editing somebody else, on the same membership facts', () => {
      // The control for the case above: same account state, different operator. Without this the
      // self-check could withhold the four for the wrong reason and still look correct.
      signedInAs(99);
      arriveEditing(
        account(7, { isApproved: false, isLockedOut: true, mustChangePassword: false }),
      );

      expect(button(AUTHORIZE_LABEL)).not.toBeUndefined();
      expect(button(UNLOCK_LABEL)).not.toBeUndefined();
      expect(button(FORCE_PASSWORD_LABEL)).not.toBeUndefined();
    });

    /**
     * ⚠ THE LEGACY SENTENCE, WHICH SHIPPED IN THE BUNDLE AND RENDERED NOWHERE. `UserLockedOut.Text` was
     * carried across with its three siblings from the same resource family and then never bound: it appeared
     * exactly once in the whole source tree, as an export nothing imported. Dead copy reads like a delivered
     * feature while being unexercisable, which is how it survived unnoticed.
     *
     * It is bound HERE, next to the command that clears the condition, rather than on the sign-in screen -
     * this is the administrator's statement of the account's state, and the advisory the locked-out person
     * reads is produced by the server, where the installation's real unlock window is known.
     */
    it('states the locked-out condition in words, beside the command that clears it', () => {
      arriveEditing(account(7, { isLockedOut: true }));

      const stated: HTMLElement | null = fixture.nativeElement.querySelector('.user-form__locked-out');

      expect(stated).withContext('the condition is stated, not merely tabulated as Yes').not.toBeNull();
      expect((stated?.textContent ?? '').replace(/\s+/g, ' ').trim()).toBe(USER_LOCKED_OUT_MESSAGE);
      expect(stated?.getAttribute('role'))
        .withContext('polite: it describes a standing condition rather than an outcome just produced')
        .toBe('status');
      expect(button(UNLOCK_LABEL))
        .withContext('read together with its remedy')
        .not.toBeUndefined();
    });

    it('states nothing about locking for an account that is not locked', () => {
      arriveEditing(account(7, { isLockedOut: false }));

      expect(fixture.nativeElement.querySelector('.user-form__locked-out'))
        .withContext('no condition, no sentence')
        .toBeNull();
      expect(button(UNLOCK_LABEL)).toBeUndefined();
    });

    it('offers none of the four while creating, because there is no account to act on', () => {
      create();

      expect(button(AUTHORIZE_LABEL)).toBeUndefined();
      expect(button(UNLOCK_LABEL)).toBeUndefined();
      expect(button(FORCE_PASSWORD_LABEL)).toBeUndefined();
    });

    it('paints the read-only membership panel from the record', () => {
      arriveEditing(
        account(7, {
          isApproved: true,
          isLockedOut: false,
          mustChangePassword: true,
          lastLockoutDate: null,
        }),
      );

      const panel = query('.user-form__membership');

      expect(panel).withContext('the panel is drawn').not.toBeNull();

      const terms: readonly string[] = queryAll<Element>('.user-form__membership-term').map((node) =>
        (node.textContent ?? '').trim(),
      );

      expect(terms).toContain('Authorized:');
      expect(terms).toContain('Locked Out:');
      expect(terms).toContain('Last Lock-out Date:');
      // The absent date renders as nothing rather than as the words null or undefined.
      expect(panel?.textContent ?? '').not.toContain('null');
      expect(panel?.textContent ?? '').not.toContain('undefined');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — REMOVING AN ACCOUNT
  // ---------------------------------------------------------------------------------------------------

  describe('removing an account', () => {
    it('asks first, then removes with a 204, re-reads the listing and leaves', () => {
      arriveEditing(account(7));

      press(DELETE_LABEL);

      const dialogue = query('.confirm-dialog');

      expect(dialogue).withContext('the question is asked').not.toBeNull();
      expect(dialogue?.getAttribute('role')).toBe('alertdialog');
      expect(dialogue?.getAttribute('aria-modal')).toBe('true');
      expect(query('.confirm-dialog__message')?.textContent?.trim()).toBe(CONFIRM_DELETE_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing yet').toHaveSize(0);

      pressDialogue(DELETE_LABEL);

      const write = expectRequest('DELETE', userUrl(7), 'the removal');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectNoListingReRead();

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('leaves the form settled at the instant a removal navigates, because deleted entry cannot be saved', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arriveEditing(account(7));
      type(CONTROL_ID.lastName, 'Typed, then deleted');

      expect(tracker.isDirty()).withContext('the control condition for this assertion').toBeTrue();

      let dirtyAtNavigation: boolean | null = null;
      navigateSpy.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7), 'the removal').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectNoListingReRead();

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], {
        queryParams: {},
        replaceUrl: true,
      });
      expect(dirtyAtNavigation)
        .withContext('there is nothing left to save once the account is gone')
        .toBeFalse();
    });

    it('sends nothing when the question is dismissed', () => {
      arriveEditing(account(7));

      press(DELETE_LABEL);
      pressDialogue('Cancel');

      expect(query('.confirm-dialog')).withContext('the question is closed').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('validates nothing, because removing an account is not an edit', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.email, '');
      press(DELETE_LABEL);

      // `causesvalidation="False"`: the confirmation stands between the trigger and the request,
      // and the form's validity has nothing to do with either.
      expect(query('.confirm-dialog')).withContext('the question is still asked').not.toBeNull();

      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('withholds the removal from an installation superuser', () => {
      // ⚠ THE CAPABILITY IS THE SERVER'S AND THE FIXTURE STATES IT AS THE SERVER WOULD. The removal
      // operation refuses a super user, so the contract reports `canDelete: false` for one - which is why
      // this fixture sets both.
      arriveEditing(account(7, { isSuperUser: true, canDelete: false }));

      expect(button(DELETE_LABEL)).withContext('withheld').toBeUndefined();
    });

    it('withholds the removal from the tenant\u2019s designated administrator', () => {
      arriveEditing(account(7, { isSuperUser: false, canDelete: false }));

      expect(button(DELETE_LABEL))
        .withContext('withheld for the administrator, matching the listing')
        .toBeUndefined();
    });

    it('withholds the removal while creating, because there is nothing to remove', () => {
      create();

      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('reports a refused removal and stays put', () => {
      arriveEditing(account(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7)).flush(
        problem(
          'user.delete.administrator_protected',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(navigateSpy).withContext('nobody is sent anywhere').not.toHaveBeenCalled();
      expect(httpMock.match(() => true)).withContext('no follow-up read').toHaveSize(0);
      expect(host().textContent ?? '').toContain(NOT_AUTHORIZED_MESSAGE);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — FAILURE SCOPE AND SEVERITY
  // ---------------------------------------------------------------------------------------------------

  describe('which failures this screen owns', () => {
    it('ignores a failure recorded by another screen entirely', () => {
      arriveEditing(account(7));

      // A listing failure belongs to the listing. Keying on the operation is what stops it from
      // disabling this form or appearing in its banner.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', '/api/v1/users/settings').flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.firstName).disabled)
        .withContext('another screen cannot withhold this form')
        .toBeFalse();
      expect(query('app-error-banner .error-banner__title')).toBeNull();
    });

    // ⚠ THE FAILED-RE-READ CASE, WHICH HAD NO TREATMENT AT ALL. Only `403` and `404` withheld the form, so
    // a read that failed any other way - a dropped connection, an aborted request, a `500` - left the
    // PREVIOUS read's values on screen with Update, Delete, UnAuthorize and Force Password Change all live,
    // and offered no way to re-read and no way forward.
    it('withholds every action and offers recovery when the account could not be re-read', () => {
      arriveEditing(account(7));

      // Provoke a fresh read of the same account, then fail it at the transport - no status, no document.
      // ⚠ THE FORCED RE-READ, BECAUSE SELECTION IS IDEMPOTENT. `selectUser` treats an account already held as
      // nothing left to do - three screens select the same account from their own route effects and the detail
      // read was measured being issued twice - so provoking a re-read through it would send no request and this
      // case would be measuring a screen that had never re-read anything.
      TestBed.inject(UserStore).rereadUser(7);
      fixture.detectChanges();
      expectRequest('GET', userUrl(7), 'the re-read').error(new ProgressEvent('error'));
      fixture.detectChanges();

      const notice: HTMLElement | null = query<HTMLElement>('.user-form__unconfirmed');

      expect(notice).withContext('the screen says the record is unconfirmed').not.toBeNull();
      expect(notice?.getAttribute('role')).toBe('status');
      expect(notice?.textContent ?? '')
        .withContext('and says so as a RE-read, because values are still on screen')
        .toContain('could not be re-read');

      const recovery: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        '.user-form__recovery',
      );

      expect(recovery.map((button) => (button.textContent ?? '').trim())).toEqual([
        'Try again',
        'Dismiss',
      ]);
      expect(recovery.every((button) => !button.disabled))
        .withContext('the way out must itself be reachable')
        .toBeTrue();

      // Every mutation is withheld: a stale record is not a basis for a write.
      const commands: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>('button').filter(
        (button) => !button.classList.contains('user-form__recovery'),
      );

      expect(commands.some((button) => button.disabled))
        .withContext('the account actions are unavailable')
        .toBeTrue();
    });

    // ⚠ THE DEFECT THIS PAIR OF SPECS EXISTS TO PREVENT IS A SILENT OVERWRITE, and it was introduced by the
    // very fix that added the recovery controls. Runtime verification found that dismissing a FIRST-read
    // failure removed the banner, removed the disclaimer, removed the retry, re-enabled four of five inputs
    // and enabled Update - with no network traffic and therefore no re-read. The end state was an empty but
    // fully editable account form with a live submit, so an operator who filled the required fields would
    // overwrite stored values they had never seen. Dismissing is legitimate only when a record is on screen
    // to proceed with.
    it('does not offer to proceed when the FIRST read failed and there is nothing on screen', () => {
      create('7');
      expectRequest('GET', userUrl(7), 'the first account read').error(new ProgressEvent('error'));
      fixture.detectChanges();

      const notice: HTMLElement | null = query<HTMLElement>('.user-form__unconfirmed');

      expect(notice).withContext('the screen still says the record is unread').not.toBeNull();
      expect(notice?.textContent ?? '')
        .withContext('worded as a first read, not a re-read - nothing is on screen')
        .toContain('could not be read');

      const labels: readonly string[] = queryAll<HTMLElement>('.user-form__recovery').map((node) =>
        (node.textContent ?? '').trim(),
      );

      expect(labels)
        .withContext('retry and a way out, but NOT an offer to proceed without reading')
        .toEqual(['Try again', 'Back to user accounts']);
      expect(labels).not.toContain('Dismiss');
    });

    it('refuses to clear the write lock while nothing has been read', () => {
      create('7');
      expectRequest('GET', userUrl(7), 'the first account read').error(new ProgressEvent('error'));
      fixture.detectChanges();

      const submitBefore: HTMLButtonElement | null = query<HTMLButtonElement>(
        'button[type="submit"]',
      );

      expect(submitBefore?.disabled).withContext('the submit starts withheld').toBeTrue();

      // Reach past the template and call the handler directly, so the guard is proven in the method rather
      // than only in the markup that currently hides its trigger. A future template change that re-exposed
      // the control must not be able to resurrect the overwrite.
      (
        fixture.componentInstance as unknown as { onDismissDetailFailure(): void }
      ).onDismissDetailFailure();
      fixture.detectChanges();

      expect(query('.user-form__unconfirmed'))
        .withContext('the disclaimer survives')
        .not.toBeNull();
      expect(query<HTMLButtonElement>('button[type="submit"]')?.disabled)
        .withContext('and so does the write lock')
        .toBeTrue();
      expect(httpMock.match(() => true))
        .withContext('nothing was re-read, so nothing may be trusted')
        .toHaveSize(0);
    });

    it('re-issues the account read from the recovery control', () => {
      arriveEditing(account(7));

      TestBed.inject(UserStore).rereadUser(7);
      fixture.detectChanges();
      expectRequest('GET', userUrl(7), 'the re-read').error(new ProgressEvent('error'));
      fixture.detectChanges();

      const retry: HTMLButtonElement | null = query<HTMLButtonElement>('.user-form__recovery');
      retry?.click();
      fixture.detectChanges();

      expectRequest('GET', userUrl(7), 'the retried read').flush(envelope(account(7)));
      fixture.detectChanges();

      expect(query('.user-form__unconfirmed'))
        .withContext('a successful re-read clears the notice')
        .toBeNull();
    });

    it('shows an error-severity refusal in the banner rather than announcing it twice', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('server.unexpected_failure', 500, 'An unexpected error occurred while processing the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      const banner = query('app-error-banner');

      expect(banner?.textContent ?? '').toContain('An unexpected error occurred while processing the request.');
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
      // The banner OWNS errors, so the transient channel is not used as well.
      expect(errorSpy).withContext('not announced twice').not.toHaveBeenCalled();
    });

    it('announces a warning-severity refusal, because the banner will not carry one', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('app-error-banner .error-banner__title')).withContext('the banner stays silent').toBeNull();
      expect(notifications().map((entry) => entry.severity)).toContain('warning');
    });

    it('reports the shared reference from the correlation identifier, not the trace identifier', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('server.unexpected_failure', 500, 'An unexpected error occurred while processing the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // Every live document carries both, and the shared reader prefers the correlation one.
      expect(query('.error-banner__trace')?.textContent ?? '').toContain(
        'f0e7d1b2-3c45-4a6b-8c9d-0e1f2a3b4c5d',
      );
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark, because the shell owns them', () => {
      arriveEditing(account(7));

      expect(queryAll('main')).toHaveSize(0);
      expect(queryAll('nav')).toHaveSize(0);
    });

    it('names every control with a real label pointing at it', () => {
      create();

      const labels = queryAll<HTMLLabelElement>('label.form-field__label');

      expect(labels.length).withContext('a label per field').toBeGreaterThanOrEqual(8);

      for (const label of labels) {
        const target: string | null = label.getAttribute('for');

        expect(target).withContext('every label points somewhere').not.toBeNull();
        expect(query(`#${target}`))
          .withContext(`the control ${String(target)} exists`)
          .not.toBeNull();
      }
    });

    it('announces each collapsible section state and keeps it in step with the toggle', () => {
      arriveEditing(account(7));

      const toggles = queryAll<HTMLButtonElement>('button.user-form__section-toggle');

      expect(toggles.length).withContext('sections are collapsible').toBeGreaterThan(0);

      for (const control of toggles) {
        // `aria-controls` is deliberately not declared: the body is REMOVED rather than hidden,
        // so it would point at a node that does not exist. The expanded state alone is accurate.
        expect(control.getAttribute('aria-expanded')).toBe('true');
        expect(control.getAttribute('type')).toBe('button');
        expect(control.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
      }

      (toggles[0] as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(
        queryAll<HTMLButtonElement>('button.user-form__section-toggle')[0]?.getAttribute(
          'aria-expanded',
        ),
      ).toBe('false');
    });

    it('declares the type of every command so none can submit by accident', () => {
      arriveEditing(account(7));

      const submits = queryAll<HTMLButtonElement>('button').filter(
        (candidate) => candidate.getAttribute('type') === 'submit',
      );

      // Exactly one submit: only the update command carried `causesvalidation="True"`.
      expect(submits).toHaveSize(1);
      expect((submits[0]?.textContent ?? '').trim()).toBe(UPDATE_SUBMIT_LABEL);

      for (const candidate of queryAll<HTMLButtonElement>('button')) {
        expect(candidate.getAttribute('type'))
          .withContext(`"${(candidate.textContent ?? '').trim()}" declares its type`)
          .not.toBeNull();
      }
    });

    it('never puts a credential into the document text', () => {
      create();

      fillCreationForm('Str0ngPass');

      // ⚠ THE VALUE LIVES IN THE CONTROL, NOT IN THE DOCUMENT. It must not be interpolated into
      // any text node, any title, any advisory or any label.
      expect(host().textContent ?? '').not.toContain('Str0ngPass');
      expect(field<HTMLInputElement>(CONTROL_ID.password).getAttribute('type')).toBe('password');
      expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).getAttribute('type')).toBe('password');
    });

    it('renders a hostile display name as text, with no element parsed out of it', () => {
      const hostile = '<img src=x onerror="window.__dnnSentinel = true">';

      arriveEditing(account(7, { displayName: hostile }));

      // ⚠ LEGACY DATA IS UNTRUSTED MARKUP BY MEASUREMENT. Nothing here is bound as trusted
      // markup and no sanitiser is involved.
      expect(field<HTMLInputElement>(CONTROL_ID.displayName).value).toBe(hostile);
      expect(host().querySelectorAll('img')).withContext('nothing was parsed').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__dnnSentinel']).toBeUndefined();
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      arriveEditing(account(7));

      expect(query('.error-banner__title')).toBeNull();
      expect(query('app-error-banner'))
        .withContext('the region is in the document before anything fails')
        .not.toBeNull();
      expect(query('app-error-banner [role="alert"]'))
        .withContext('carrying its announcement semantics already')
        .not.toBeNull();
      expect(query('app-error-banner .error-banner'))
        .withContext('and painting nothing')
        .toBeNull();
    });

    // ⚠ #20 — THE LEGEND DESCRIBES THE MARKER THIS BUILD DRAWS. The legacy sentence said "red arrow", which was
    // accurate against a skin that drew the marker as a red arrow image; no image asset ships here, the shared
    // field draws an asterisk, and a legend naming the wrong marker sends a reader looking for something that
    // does not exist. Both halves are asserted: the new wording is present AND the old wording is gone, so the
    // sentence cannot quietly revert.
    it('describes the required marker the form actually draws', () => {
      create();

      expect(host().textContent ?? '').toContain('All fields marked with an asterisk are required.');
      expect(host().textContent ?? '').not.toContain('red arrow');

      const marker = host().querySelector('.form-field__required');

      expect(marker).withContext('and the marker it describes is on the page').not.toBeNull();
      expect((marker?.textContent ?? '').trim())
        .withContext('as an asterisk, which is what the legend now names')
        .toContain('*');
    });
  });

  // PROOF 8 — THE MEASURED PASSWORD POLICY, PINNED TO ITS EXACT BOUNDARY
  // ⚠⚠ THIS POLICY IS PRESERVED VERBATIM AND MUST NEVER BE TIGHTENED. Measured at `Website/release.config`
  // L242 `minRequiredPasswordLength="7"` and L243 `minRequiredNonalphanumericCharacters="0"`.

  describe('the measured password policy', () => {
    /**
     * Reads one group-level validator message without widening anything to `any`. `ValidationErrors` is
     * an index signature over `any`, so the value is taken through `unknown` and narrowed explicitly.
     */
    function groupMessage(errors: ValidationErrors | null, key: string): string | null {
      if (errors === null) {
        return null;
      }

      const raw: unknown = errors[key];

      return typeof raw === 'string' ? raw : null;
    }

    /** A minimal group carrying only the three members the exported validator reads. */
    function credentialGroup(
      password: string,
      confirmPassword: string,
      randomPassword = false,
    ): FormGroup {
      return new FormGroup({
        password: new FormControl<string>(password, { nonNullable: true }),
        confirmPassword: new FormControl<string>(confirmPassword, { nonNullable: true }),
        randomPassword: new FormControl<boolean>(randomPassword, { nonNullable: true }),
      });
    }

    it('publishes the two measured figures as SEVEN and ZERO', () => {
      expect(PASSWORD_MIN_LENGTH).withContext('minRequiredPasswordLength').toBe(7);
      expect(PASSWORD_MIN_NON_ALPHANUMERIC)
        .withContext('minRequiredNonalphanumericCharacters')
        .toBe(0);
    });

    it('refuses one character below the minimum and accepts the minimum exactly', () => {
      const short = 'a'.repeat(PASSWORD_MIN_LENGTH - 1);
      const exact = 'a'.repeat(PASSWORD_MIN_LENGTH);

      expect(short.length).toBe(6);
      expect(exact.length).toBe(7);

      // Below the boundary: refused.
      expect(groupMessage(passwordRulesValidator(() => true)(credentialGroup(short, short)), 'invalidPassword'))
        .withContext('six characters is below the measured minimum')
        .toBe(COMPONENT_INVALID_PASSWORD_MESSAGE);

      // AT the boundary: accepted. `<` and not `<=` is the measured comparison.
      expect(passwordRulesValidator(() => true)(credentialGroup(exact, exact)))
        .withContext('seven characters is the measured minimum and passes')
        .toBeNull();
    });

    it('accepts a purely alphanumeric credential, because ZERO punctuation is required', () => {
      // The rule is vacuous as shipped rather than absent, and that distinction matters: it is
      // a real configuration-driven check whose configured value happens to be zero.
      const alphanumeric = 'Str0ng7';

      expect(/^[a-zA-Z0-9]+$/.test(alphanumeric))
        .withContext('the fixture really contains no punctuation')
        .toBeTrue();
      expect(passwordRulesValidator(() => true)(credentialGroup(alphanumeric, alphanumeric))).toBeNull();
    });

    it('reports the mismatch, NOT the policy, when a credential fails both', () => {
      const errors = passwordRulesValidator(() => true)(credentialGroup('abc', 'abcd'));

      expect(groupMessage(errors, 'passwordMismatch')).toBe(PASSWORD_MISMATCH_MESSAGE);
      expect(groupMessage(errors, 'invalidPassword'))
        .withContext('the policy check was short-circuited')
        .toBeNull();
    });

    it('applies neither rule once generation is chosen', () => {
      expect(passwordRulesValidator(() => true)(credentialGroup('a', 'zz', true))).toBeNull();
    });

    it('applies no credential rule at all while editing', () => {
      expect(passwordRulesValidator(() => false)(credentialGroup('a', 'zz'))).toBeNull();
    });

    it('interpolates both measured figures into the sentence and leaves no raw token behind', () => {
      // The TokenReplace subsystem is OUT OF SCOPE, so the two measured configured values are interpolated
      // directly from the policy constants. That is precisely what the legacy substitution produced at run
      // time, and it keeps the sentence honest if either constant ever changes.
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).toContain(`least ${String(PASSWORD_MIN_LENGTH)} characters`);
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).toContain(
        `least ${String(PASSWORD_MIN_NON_ALPHANUMERIC)} non-alphanumeric`,
      );
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).toContain('least 7 characters');
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).toContain('least 0 non-alphanumeric');

      // ⚠ NO UNREPLACED TOKEN MAY SURVIVE. An operator seeing `[PasswordLength]` on screen is
      // reading a template, not a message.
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).not.toContain('[PasswordLength]');
      expect(COMPONENT_INVALID_PASSWORD_MESSAGE).not.toContain('[NoneAlphabet]');
    });

    it('renders the interpolated sentence, with no raw token reaching the document', () => {
      create();

      fillCreationForm('Str0ng');
      press(CREATE_SUBMIT_LABEL);

      const text = host().textContent ?? '';

      expect(text).toContain('least 7 characters');
      expect(text).toContain('least 0 non-alphanumeric');
      expect(text).not.toContain('[PasswordLength]');
      expect(text).not.toContain('[NoneAlphabet]');
      expect(httpMock.match(() => true)).toHaveSize(0);
    });
  });

  // PROOF 9 — THE CREATION-OUTCOME VOCABULARY

  describe('the creation-outcome vocabulary', () => {
    it('numbers all eighteen members exactly as measured', () => {
      // Pinned member by member. A silent renumbering — an inserted member, an alphabetical
      // re-sort, a dropped explicit ordinal — would change the meaning of stored data.
      expect(UserCreateStatus.AddUser).toBe(0);
      expect(UserCreateStatus.UsernameAlreadyExists).toBe(1);
      expect(UserCreateStatus.UserAlreadyRegistered).toBe(2);
      expect(UserCreateStatus.DuplicateEmail).toBe(3);
      expect(UserCreateStatus.DuplicateProviderUserKey).toBe(4);
      expect(UserCreateStatus.DuplicateUserName).toBe(5);
      expect(UserCreateStatus.InvalidAnswer).toBe(6);
      expect(UserCreateStatus.InvalidEmail).toBe(7);
      expect(UserCreateStatus.InvalidPassword).toBe(8);
      expect(UserCreateStatus.InvalidProviderUserKey).toBe(9);
      expect(UserCreateStatus.InvalidQuestion).toBe(10);
      expect(UserCreateStatus.InvalidUserName).toBe(11);
      expect(UserCreateStatus.ProviderError).toBe(12);
      expect(UserCreateStatus.Success).toBe(13);
      expect(UserCreateStatus.UnexpectedError).toBe(14);
      expect(UserCreateStatus.UserRejected).toBe(15);
      expect(UserCreateStatus.PasswordMismatch).toBe(16);
      expect(UserCreateStatus.AddUserToPortal).toBe(17);
    });

    it('names THIRTEEN as success and ZERO as not-yet-attempted', () => {
      expect(UserCreateStatus.Success).withContext('Success is thirteen').toBe(13);
      expect(UserCreateStatus.AddUser).withContext('AddUser is the zero sentinel').toBe(0);

      // The component's own anchors, so nothing on this screen can drift from the vocabulary.
      expect(CREATE_SUCCEEDED).toBe(UserCreateStatus.Success);
      expect(CREATE_NOT_YET_ATTEMPTED).toBe(UserCreateStatus.AddUser);

      // ⚠ THE ZERO SENTINEL IS NOT AN OUTCOME. Stated as an inequality because that is the
      // mistake being guarded against, not as a tautology about two different numbers.
      expect(CREATE_NOT_YET_ATTEMPTED).not.toBe(CREATE_SUCCEEDED);
      expect(UserCreateStatus.AddUser).not.toBe(UserCreateStatus.Success);
    });

    it('keeps the three name-collision members distinct despite sharing one message', () => {
      const collisions: readonly UserCreateStatus[] = [
        UserCreateStatus.UsernameAlreadyExists,
        UserCreateStatus.DuplicateUserName,
        UserCreateStatus.InvalidUserName,
      ];

      expect(collisions).toEqual([1, 5, 11]);
      expect(new Set<UserCreateStatus>(collisions).size)
        .withContext('three distinct members')
        .toBe(3);
      expect(UserCreateStatus.UserAlreadyRegistered).toBe(2);

      // One wording for the three the legacy combined; the fourth is worded separately, exactly
      // as the legacy `Case` arms divided them.
      expect(USER_CREATE_MESSAGE['user.create.username_already_exists']).toBe(USER_NAME_EXISTS);
      expect(USER_CREATE_MESSAGE['user.create.user_already_registered']).toBe(USER_NAME_EXISTS);
      expect(USER_CREATE_MESSAGE['user.create.duplicate_username']).toBe(USER_NAME_EXISTS);
      expect(USER_CREATE_MESSAGE['user.create.invalid_username']).not.toBe(USER_NAME_EXISTS);
    });

    it('collapses all four provider-fault members onto one registration sentence', () => {
      const shipped = USER_CREATE_MESSAGE['user.create.provider_error'];

      expect(shipped).toBe(USER_CREATE_MESSAGE['user.create.portal_assignment_failed']);
      expect(new Set<UserCreateStatus>([
        UserCreateStatus.ProviderError,
        UserCreateStatus.UnexpectedError,
        UserCreateStatus.DuplicateProviderUserKey,
        UserCreateStatus.InvalidProviderUserKey,
      ]).size)
        .withContext('four distinct members behind one sentence')
        .toBe(4);

      const measuredRegError =
        'An Unexpected Error Occurred During Registration. Please Contact The Portal ' +
        'Administrator For Futher Information.';

      expect(shipped)
        .withContext('the measured wording, misspelling and all')
        .toBe(measuredRegError);
      expect(shipped)
        .withContext('and specifically NOT the repaired spelling')
        .not.toContain('Further');
    });

    it('keys success on the HTTP status and never on an outcome ordinal', () => {
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL);

      const sent: unknown = write.request.body;

      expect(sent).withContext('a body was sent').not.toBeNull();
      expect(Object.keys(sent as Record<string, unknown>).sort()).toEqual([
        'authorize',
        'confirmPassword',
        'displayName',
        'email',
        'firstName',
        'lastName',
        'password',
        'username',
      ]);

      // The answer is 201 CREATED and the body is the account, carrying no outcome member.
      const created: UserDetail = account(9);

      write.flush(envelope(created), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();

      expect(Object.keys(created)).not.toContain('status');
      expect(Object.keys(created)).not.toContain('createStatus');

      // Treated as success: the screen left for the listing.
      expect(navigateSpy).toHaveBeenCalled();
      expect(errorSpy).not.toHaveBeenCalled();
    });

    it('names the three password storage formats rather than numbering them', () => {
      // Data-model fidelity. `Clear`, `Hashed` and `Encrypted` are persisted discriminators, so
      // the ordinals are load-bearing data and the members must stay named.
      expect(PasswordFormat.Clear).toBe(0);
      expect(PasswordFormat.Hashed).toBe(1);
      expect(PasswordFormat.Encrypted).toBe(2);
    });
  });

  // PROOF 10 — COMPONENT IDENTITY AND THE SENTINEL DISCIPLINE

  describe('the component identity that other files depend on', () => {
    /**
     * The compiled declaration, read through the framework's own reflection API. ⚠
     * `fixture.nativeElement` is NOT the answer here, and measurement proved it: the testing harness
     * mounts a component under a generic `div` host of its own making, so the host tag reports `div` no
     * matter what the component declares.
     */
    const mirror = reflectComponentType(UserFormComponent);

    it('is named and selected exactly as the route file imports it', () => {
      expect(UserFormComponent.name).toBe('UserFormComponent');

      expect(mirror).withContext('the class really is a component').not.toBeNull();
      expect(mirror?.selector).withContext('the selector other templates use').toBe('app-user-form');
      expect(mirror?.isStandalone).withContext('standalone, so it needs no module').toBeTrue();
    });

    it('declares the input as exactly `userId`, which the router binds by name', () => {
      const bound = (mirror?.inputs ?? []).map((entry) => entry.templateName);

      expect(bound).withContext('the name the router will look for').toContain('userId');
      expect(bound).not.toContain('id');
      expect(bound).not.toContain('userID');

      // And it is a signal input, which is what makes a later arrival of the parameter
      // observable rather than a one-shot construction argument.
      expect((mirror?.inputs ?? []).find((entry) => entry.templateName === 'userId')?.isSignal)
        .withContext('a signal input, so a late parameter still lands')
        .toBeTrue();

      // Proven behaviourally as well as by reflection: setting the input under this exact name
      // is what moves the screen out of creation mode.
      create();

      expect(button(CREATE_SUBMIT_LABEL))
        .withContext('creating before the input is set')
        .not.toBeUndefined();

      reference.setInput('userId', '42');
      fixture.detectChanges();

      expectRequest('GET', userUrl(42), 'the input under the bound name took effect').flush(
        envelope(account(42)),
      );
      fixture.detectChanges();

      expect(button(UPDATE_SUBMIT_LABEL)).not.toBeUndefined();
      expectNoListingReRead();
    });

    it('declares the checked-change strategy the requirements mandate', () => {
      const definition: unknown = (UserFormComponent as unknown as Record<string, unknown>)['ɵcmp'];

      expect(typeof definition).withContext('the class was compiled as a component').toBe('object');

      const declared: unknown = (definition as Record<string, unknown>)['onPush'];

      expect(declared).withContext('ChangeDetectionStrategy.OnPush is declared').toBeTrue();
    });
  });

  describe('identifiers, tested for presence and never for truth', () => {
    // Consequently no identifier anywhere — not in the component, not in these helpers — is subjected to a
    // truthiness test, a positive-value test, a comparison against the sentinel or a coalescing default.

    it('treats MINUS ONE as a real account rather than as an absence', () => {
      create('-1');

      expectRequest('GET', userUrl(-1), 'minus one is an address, not an absence').flush(
        envelope(account(-1)),
      );
      fixture.detectChanges();

      expect(button(UPDATE_SUBMIT_LABEL)).withContext('editing, not creating').not.toBeUndefined();
      expect(button(CREATE_SUBMIT_LABEL)).toBeUndefined();
      expectNoListingReRead();
    });

    it('treats ZERO as a real account, which is the defensive half of the rule', () => {
      // ⚠ ZERO IS NOT NATURALLY OCCURRING FOR THIS TABLE. `Users.UserID` seeds at ONE, so no real account
      // carries nought and this is a DEFENSIVE test of the discipline rather than a live scenario.
      create('0');

      expectRequest('GET', userUrl(0), 'zero is an address, not an absence').flush(
        envelope(account(0)),
      );
      fixture.detectChanges();

      expect(button(UPDATE_SUBMIT_LABEL)).not.toBeUndefined();
      expectNoListingReRead();
    });

    it('offers no form at all when the parameter is not an identifier', () => {
      create('not-a-number');

      expect(button(CREATE_SUBMIT_LABEL))
        .withContext('an address that names nothing must not offer to create something')
        .toBeUndefined();
      expect(button(UPDATE_SUBMIT_LABEL))
        .withContext('nor to update it, which is what the address appears to ask for')
        .toBeUndefined();
      expect(query('form')).withContext('no form is offered in either mode').toBeNull();
      expect(host().textContent ?? '')
        .withContext('the outcome is stated in the measured wording instead')
        .toContain(NO_USER_MESSAGE);
      expect(httpMock.match(() => true))
        .withContext('and no identifier was invented, so nothing was read')
        .toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 11 — HOW THE TYPED FORM IS CONSTRUCTED
  // ---------------------------------------------------------------------------------------------------

  describe('the typed form, as constructed', () => {
    it('opens the two create-time switches at their measured states', () => {
      create();

      // `chkAuthorize checked="True"` at `User.ascx` L21, which the code-behind never overrides.
      expect(field<HTMLInputElement>(CONTROL_ID.authorize).checked)
        .withContext('authorise opens checked, as measured')
        .toBeTrue();

      expect(field<HTMLInputElement>(CONTROL_ID.randomPassword).checked)
        .withContext('generation opens UNCHECKED, matching the code-behind and not the markup')
        .toBeFalse();
    });

    it('offers the notification switch disabled rather than ticked', () => {
      create();

      const notify = field<HTMLInputElement>(CONTROL_ID.notify);

      // MIGRATION — DIVERGENCE FROM THE MEASURED MARKUP, WITH ITS CAUSE. `chkNotify` carried
      // `checked="True"` at `User.ascx` L25, but `CreateUserRequest` declares exactly EIGHT members —
      // username, firstName, lastName, displayName, email, password, confirmPassword, authorize — and
      // `notify` is NOT one of them.
      expect(notify.disabled).withContext('no wire field exists to carry the choice').toBeTrue();
      expect(notify.checked).toBeFalse();

      expect(host().textContent ?? '')
        .withContext('help is revealed on demand, exactly like every other field')
        .not.toContain(NOTIFY_UNAVAILABLE_ADVISORY);
      expect(notify.closest('.form-field')?.querySelector('.form-field__help-toggle'))
        .withContext('but the affordance that reveals it is offered')
        .not.toBeNull();
    });

    it('returns every control to its initial value on a reset, never to null', () => {
      arriveEditing(account(7));

      // Dirty the form so the reset has something to undo.
      type(CONTROL_ID.firstName, 'Augusta');
      expect(field<HTMLInputElement>(CONTROL_ID.firstName).value).toBe('Augusta');

      reference.setInput('userId', undefined);
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.authorize).checked)
        .withContext('a nullable control would have come back unchecked')
        .toBeTrue();
      expect(field<HTMLInputElement>(CONTROL_ID.randomPassword).checked).toBeFalse();

      // And the text controls came back at their initial empty string rather than rendering the
      // word "null", which is what a nullable control interpolates.
      expect(field<HTMLInputElement>(CONTROL_ID.firstName).value).toBe('');
      expect(host().textContent ?? '').not.toContain('null');
      expect(httpMock.match(() => true)).withContext('a reset reads nothing').toHaveSize(0);
    });

    it('bounds the credential boxes with an attribute and with no length validator', () => {
      create();

      const password = field<HTMLInputElement>(CONTROL_ID.password);
      const confirm = field<HTMLInputElement>(CONTROL_ID.confirmPassword);

      // The ceiling reaches the DOM as a real attribute, so over-typing is stopped in the
      // browser exactly as the legacy `maxlength` stopped it.
      for (const box of [password, confirm]) {
        const bound: string | null = box.getAttribute('maxlength');

        expect(bound).withContext('the ceiling is an HTML attribute').not.toBeNull();
        expect(Number(bound)).toBeGreaterThan(0);
      }

      // Proven by behaviour: a credential far longer than the attribute allows still submits, because
      // nothing validates its length on this side.
      const overlong = 'A1'.repeat(400);

      fillCreationForm();
      type(CONTROL_ID.password, overlong);
      type(CONTROL_ID.confirmPassword, overlong);
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', USERS_URL, 'no client-side length rule blocked it');

      expect(host().textContent ?? '')
        .withContext('and no length message was rendered')
        .not.toContain('maxlength');

      write.flush(envelope(account(9)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      expectNoListingReRead();
    });

    it('asks a password manager to generate rather than to fill', () => {
      create();

      // `autocomplete="new-password"` on BOTH boxes: this is an account being created, so a
      // remembered credential is the wrong offer.
      expect(field<HTMLInputElement>(CONTROL_ID.password).getAttribute('autocomplete')).toBe(
        'new-password',
      );
      expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).getAttribute('autocomplete')).toBe(
        'new-password',
      );
    });

    it('projects the selection as a signal a view cannot write to', () => {
      const store = TestBed.inject(UserStore);

      expect('set' in store.selectedUser).withContext('no set on a published signal').toBeFalse();
      expect('update' in store.selectedUser).toBeFalse();
      expect('set' in store.failure).toBeFalse();
      expect('update' in store.failure).toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 12 — THE NULL-DATE SENTINEL RENDERS AS AN EMPTY CELL
  // ---------------------------------------------------------------------------------------------------

  describe('the membership panel and the null-date sentinel', () => {
    /** The rendered value cells of the read-only membership panel, in document order. */
    function membershipValues(): readonly string[] {
      return queryAll<Element>('dd.user-form__membership-value').map((node) =>
        (node.textContent ?? '').trim(),
      );
    }

    it('renders every sentinel date as an empty cell, never as a first-century date', () => {
      const sentinel = '0001-01-01T00:00:00';

      arriveEditing(
        account(7, {
          createdDate: sentinel,
          lastLoginDate: sentinel,
          lastActivityDate: sentinel,
          lastPasswordChangeDate: sentinel,
          lastLockoutDate: sentinel,
        }),
      );

      const rendered = membershipValues();

      expect(rendered.length).withContext('the panel is painted').toBeGreaterThanOrEqual(5);

      // ⚠ PARITY, NOT AN IMPROVEMENT. The legacy `DisplayDate` already returned the empty string for the
      // sentinel, so an empty cell is what an operator saw. `01/01/0001` would be a REGRESSION — a date
      // nobody entered, presented as though somebody had.
      for (const value of rendered) {
        expect(value).not.toContain('0001');
        expect(value).not.toContain('01/01/0001');
      }

      const text = host().textContent ?? '';

      expect(text).not.toContain('0001-01-01');
      expect(text).not.toContain('01/01/0001');
    });

    it('still renders a genuine date, so the blanking is the sentinel and not the panel', () => {
      // Without this the case above would also pass on a panel that rendered nothing at all.
      arriveEditing(account(7, { createdDate: '2024-01-05T09:00:00Z', lastLockoutDate: null }));

      expect(membershipValues().some((value) => value.length > 0))
        .withContext('a real date does reach the panel')
        .toBeTrue();
    });
  });

  // PROOF 13 — WHAT THIS SCREEN MUST NOT DO
  // Negative assertions, each tied to a measured reason. Several record that a legacy affordance was
  // BEHAVIOUR-PRESERVINGLY dropped rather than reduced, which is a different claim from "we left it out"
  // and the distinction is the point.

  describe('what this screen must not do', () => {
    /** Every attribute value that could name a control, across the whole rendered document. */
    function controlNames(): readonly string[] {
      return queryAll<Element>('*').flatMap((node) =>
        ['id', 'name', 'formcontrolname', 'placeholder', 'aria-label', 'autocomplete']
          .map((attribute) => node.getAttribute(attribute))
          .filter((value): value is string => value !== null),
      );
    }

    it('offers no security-code affordance', () => {
      create();

      // The legacy declared `dnn:captchacontrol ctlCaptcha` inside the password table at `User.ascx` L67.
      // The control is one of the 102 excluded `Library/Controls` files, so there is nothing to render and
      // no field to post.
      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('captcha');
      expect(text).not.toContain('security code');
      expect(controlNames().some((value) => value.toLowerCase().includes('captcha'))).toBeFalse();
      expect(queryAll('img')).withContext('no challenge image').toHaveSize(0);
    });

    it('offers no password question and no password answer', () => {
      create();

      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('password question');
      expect(text).not.toContain('password answer');

      const names = controlNames().map((value) => value.toLowerCase());

      expect(names.some((value) => value.includes('question'))).toBeFalse();
      expect(names.some((value) => value.includes('answer'))).toBeFalse();
    });

    it('offers no way to retrieve an existing password', () => {
      arriveEditing(account(7));

      // The legacy membership provider was registered with `enablePasswordRetrieval="true"` and a
      // reversible password format, which made every stored credential recoverable. The successor stores a
      // one-way hash, so retrieval is not withheld — it is IMPOSSIBLE.
      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('retrieve');
      expect(text).not.toContain('recover password');
      expect(text).not.toContain('send password');

      // No value-bearing credential control exists at all while editing.
      expect(query(`#${CONTROL_ID.password}`)).toBeNull();
      expect(query(`#${CONTROL_ID.confirmPassword}`)).toBeNull();
      expect(queryAll('input[type="password"]')).toHaveSize(0);
    });

    it('shows no presence indicator and no membership-services tab', () => {
      arriveEditing(account(7, { isOnline: true }));

      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('online');
      expect(text).not.toContain('on-line');

      // No services, roles or subscriptions tab: there is no
      // `/users/{id}/services|roles|subscriptions` endpoint to drive one.
      expect(text).not.toContain('manage services');
      expect(text).not.toContain('subscription');
    });

    it('decides nothing from a permission key, and leaves the refusal to the server', () => {
      arriveEditing(account(7));

      // ⚠ TWO CLOSED VOCABULARIES THAT MUST NOT BE CONFLATED: the authorisation POLICY names and the
      // persisted PERMISSION KEYS. Neither appears here.
      const markup = host().innerHTML;

      expect(markup).not.toContain('hasPermission');
      expect(markup).not.toContain('PortalAdministrator');

      const text = host().textContent ?? '';

      expect(text).not.toContain('VIEW');
      expect(text).not.toContain('EDIT');
    });

    it('renders no bare zero for an account allowance', () => {
      // ⚠ ZERO MEANS UNLIMITED AND MINUS ONE MEANS NOT SET for the tenant's account allowance, so printing
      // either as a number would tell an operator the opposite of the truth. This screen states the
      // allowance only as the measured refusal sentence, which carries no figure at all.
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', USERS_URL).flush(
        problem('user.quota_exceeded', 403, 'The account allowance for this site is spent.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const text = host().textContent ?? '';

      expect(text.toLowerCase()).toContain(EXCEEDED_USER_QUOTA_MESSAGE_FRAGMENT.toLowerCase());
      expect(text).toContain('User Quota');

      // ⚠ AND NO FIGURE. The sentence names the allowance without printing it, so neither
      // sentinel can be mistaken for a limit.
      expect(text).not.toMatch(/quota[^.]*(^|\s)(0|-1)(\s|$)/i);
      expectNoListingReRead();
    });

    it('lays the screen out without a single table element', () => {
      arriveEditing(account(7));

      // The legacy `tblAddUser`, `tblPassword` and the `pnlUser` design table were all LAYOUT tables rather
      // than data grids — `tblPassword` even carried `summary="Password Management"`, which is a layout
      // summary and not a caption.
      expect(queryAll('table')).withContext('no layout table survives').toHaveSize(0);
      expect(queryAll('td')).toHaveSize(0);
      expect(queryAll('tr')).toHaveSize(0);
    });

    it('emits no semantic landmark of any kind', () => {
      arriveEditing(account(7));

      // Each landmark is owned exactly once by the application shell. A second one here would
      // give a screen-reader user two mains or two navigations to choose between.
      expect(queryAll('header')).toHaveSize(0);
      expect(queryAll('main')).toHaveSize(0);
      expect(queryAll('nav')).toHaveSize(0);
      expect(queryAll('footer')).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 14 — THE ANNOUNCING REGION, AND HOW UNTRUSTED WORDING REACHES IT
  // ---------------------------------------------------------------------------------------------------

  describe('the announcing region', () => {
    it('carries a live region once there is something to announce', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      expectRequest('PUT', userUrl(7)).flush(
        problem('validation_failed', 400, 'One or more members are invalid.', {
          firstName: ['First name is required'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // ⚠ THE REGION BELONGS TO THE SHARED BANNER, AND THIS SUITE ASSERTS ITS PRESENCE RATHER THAN ADDING A
      // COMPETING ONE. Two live regions announcing the same text is worse than one, because a screen reader
      // reads both.
      const live = query<HTMLElement>('[aria-live]');

      expect(live).withContext('the banner mounted with a live region').not.toBeNull();
      expect(live?.getAttribute('aria-live')).toBe('assertive');
      expect(query('[role="alert"]')).not.toBeNull();
      expect(queryAll('[aria-live]')).withContext('exactly one live region').toHaveSize(1);
      expectNoListingReRead();
    });

    it('strips the legacy break prefix and announces plain text', () => {
      expect(stripLegacyBreakTags('<br/>A sentence.')).toBe('A sentence.');
      expect(stripLegacyBreakTags('<br>A sentence.')).toBe('A sentence.');

      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', USERS_URL).flush(
        problem('user.unknown_reason', 400, '<br/>Something the vocabulary does not word.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const text = host().textContent ?? '';

      expect(text).not.toContain('<br');
      expect(text).not.toContain('&lt;br');
      expect(queryAll('br')).withContext('no break element was parsed out of a message').toHaveSize(0);
      expectNoListingReRead();
    });

    it('escapes hostile wording arriving in a server message', () => {
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', USERS_URL).flush(
        problem(
          'user.unknown_reason',
          400,
          '<script>window.__accountFormSentinel = true;</script>Refused.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(queryAll('script')).withContext('no script element was created').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__accountFormSentinel']).toBeUndefined();

      // The angle brackets survive as TEXT, which is the proof that they were escaped rather
      // than dropped or executed.
      expect(host().textContent ?? '').toContain('<script>');
      expectNoListingReRead();
    });

    it('preserves the measured double space in the address-conflict sentence', () => {
      // ⚠ THE DOUBLE SPACE IS IN THE MEASURED VALUE. `EmailError.Text` in
      // `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx` reads "...unique Email Address. The
      // Email Address you entered..." with TWO spaces after the full stop, and it is asserted verbatim.
      expect(EMAIL_CONFLICT_MESSAGE).toContain('Email Address.  The Email Address');
      expect(EMAIL_CONFLICT_MESSAGE).not.toContain('Email Address. The Email Address');
      expect(EMAIL_CONFLICT_MESSAGE).toBe(
        'This portal requires a unique Email Address.  The Email Address you entered has ' +
          'already been used.',
      );
    });

    it('enforces no address uniqueness of its own and lets the server refuse', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.email, 'taken@example.test');

      expect(fieldErrors().join(' ')).withContext('nothing was refused locally').not.toContain('already');

      press(UPDATE_SUBMIT_LABEL);

      const write = expectRequest('PUT', userUrl(7), 'the duplicate address was sent');

      write.flush(problem('user.create.duplicate_email', 409, 'That address is already in use.'), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'That address is already in use.',
      );
      expect(host().textContent ?? '').not.toContain(EMAIL_CONFLICT_MESSAGE);
      expect(query(`#${CONTROL_ID.email}`)).withContext('the form stays usable').not.toBeNull();
      expectNoListingReRead();
    });
  });
  // =========================================================================
  // THE RECORD IDENTIFIER IN THE HEADING
  // =========================================================================

  describe('the record identifier in the heading', () => {
    // `ManageUsers.ascx.vb` chose its heading in three arms, and the middle one was
    // `If IsUser And IsProfile Then trTitle.Visible = False`: when the caller WAS the account owner the
    // legacy screen hid the whole title row, so a member was never shown the internal record key. Only the
    // administrative arm reached `String.Format(UserTitle, User.Username, User.UserID)`.

    it('discloses the identifier to an administrator editing someone else', () => {
      signedInAs(1);
      arriveEditing(account(16, { displayName: 'Ada Lovelace' }));

      const heading = query<HTMLHeadingElement>('h1');

      expect(heading?.textContent?.trim()).toBe('Edit User - Ada Lovelace (Id: 16)');
    });

    it('WITHHOLDS the identifier from the account\u2019s own owner', () => {
      // The caller and the subject are the same account, which is the legacy `IsUser` predicate.
      signedInAs(16);
      arriveEditing(account(16, { displayName: 'Ada Lovelace' }));

      const heading = query<HTMLHeadingElement>('h1');
      const shown = heading?.textContent?.trim() ?? '';

      expect(shown).toBe('Edit User - Ada Lovelace');
      expect(shown).withContext('no identifier disclosed to the owner').not.toContain('Id:');
      expect(shown).withContext('no bare record number anywhere in the heading').not.toMatch(/\d/);
    });

    it('still renders a heading for the owner rather than removing it', () => {
      // The one detail deliberately NOT reproduced. Legacy hid the entire title row; a routed screen with
      // no `h1` leaves its main region with no accessible name, so the heading stays and only the
      // identifier goes.
      signedInAs(16);
      arriveEditing(account(16, { displayName: 'Ada Lovelace' }));

      expect(queryAll('h1')).withContext('exactly one heading, still present').toHaveSize(1);
      expect(query<HTMLHeadingElement>('h1')?.textContent?.trim().length ?? 0).toBeGreaterThan(0);
    });

    it('falls back to the user name when the owner has no display name, still without the identifier', () => {
      signedInAs(16);
      arriveEditing(account(16, { displayName: '', username: 'ada.lovelace' }));

      const shown = query<HTMLHeadingElement>('h1')?.textContent?.trim() ?? '';

      expect(shown).toBe('Edit User - ada.lovelace');
      expect(shown).not.toContain('Id:');
    });

    it('does not withhold the identifier merely because the subject id is zero', () => {
      // Account key zero is a real key. A truthiness test on the resolved id would make the owner check
      // misfire here and hide the identifier from an administrator.
      signedInAs(1);
      arriveEditing(account(0, { displayName: 'Ada Lovelace' }));

      expect(query<HTMLHeadingElement>('h1')?.textContent?.trim()).toBe(
        'Edit User - Ada Lovelace (Id: 0)',
      );
    });
  });

  // =========================================================================
  // THE TIME COMPONENT OF THE MEMBERSHIP DATES
  // =========================================================================

  describe('the membership dates', () => {
    // ⚠ THE LEGACY AUTHORITY, TRACED RATHER THAN ASSUMED, BECAUSE IT DECIDES WHETHER THIS IS A FIX OR A
    // REGRESSION. `Membership.ascx` bound a `dnn:propertyeditorcontrol` in `editmode="View"` to
    // `UserMembership`, whose date members are declared plainly `As Date` with no `Editor` attribute - so
    // the editor was chosen by TYPE. `EditControlFactory.CreateEditControl` L62-L64 maps
    // `System.DateTime` to `DateTimeEditControl`, and that control's `DefaultFormat` L69-L73 returns `"g"`,
    // the general date-and-time pattern. THE LEGACY SCREEN SHOWED THE TIME.
    //
    // Rendering date-only was therefore a divergence, and a consequential one: a password reset performed
    // the same day appeared not to have happened, because the only evidence of it was a date that had not
    // changed. The listing already showed a time for the same class of value, so the application also
    // disagreed with itself.

    it('shows the TIME for every membership date the server supplies one for', () => {
      arriveEditing(
        account(16, {
          createdDate: '2026-08-14T16:56:10.183',
          lastActivityDate: '2026-08-14T16:56:10.183',
          lastLoginDate: '2026-08-14T17:30:00.000',
          lastPasswordChangeDate: '2026-08-14T16:56:10.183',
          lastLockoutDate: '2026-08-14T18:05:42.000',
        }),
      );

      const panel = host().textContent ?? '';

      // A clock time must appear for each. Asserted as a count of time-bearing renderings rather than by
      // hunting individual cells, because the cells are a definition list and the assertion should not
      // depend on their order.
      const times = panel.match(/\d{1,2}:\d{2}:\d{2}\s?(AM|PM)/g) ?? [];

      expect(times.length)
        .withContext('five dates supplied, five times rendered')
        .toBeGreaterThanOrEqual(5);
      expect(panel).toContain('4:56:10 PM');
      expect(panel).toContain('5:30:00 PM');
      expect(panel).toContain('6:05:42 PM');
    });

    it('distinguishes two same-day password changes, which date-only rendering could not', () => {
      // THE DEFECT RESTATED AS A TEST. Two resets on the same day differ only in their time, so a date-only
      // rendering makes a reset that DID happen look like one that did not.
      arriveEditing(account(16, { lastPasswordChangeDate: '2026-08-14T09:15:00.000' }));

      expect(host().textContent).toContain('9:15:00 AM');
      expect(host().textContent).not.toContain('8/14/2026 8/14/2026');
    });

    it('still renders an absent date as empty rather than inventing a time', () => {
      // The server genuinely sends null for an account that has never signed in or been locked out. An
      // absent value must stay absent - adding a time to nothing would be worse than showing nothing.
      arriveEditing(account(16, { lastLoginDate: null, lastLockoutDate: null }));

      const panel = host().textContent ?? '';

      expect(panel).not.toContain('1/1/1');
      expect(panel).not.toContain('12:00:00 AM');
    });
  });

  // =========================================================================
  // A TENANT THAT COMPOSES DISPLAY NAMES ITSELF
  // =========================================================================

  // ---------------------------------------------------------------------------------------------------
  // THE REVISION MARKER, AND THE TWO `409`s THAT ARE NOT THE SAME FAILURE
  // ---------------------------------------------------------------------------------------------------

  describe('the revision marker on a save', () => {
    it('echoes the token the account read published, verbatim', () => {
      arriveEditing(account(7, { concurrencyToken: 'revision-from-the-server' }));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      const body = expectRequest('PUT', userUrl(7), 'the update').request.body as UpdateUserRequest;

      expect(body.concurrencyToken)
        .withContext('the marker is the server\'s and is round-tripped unread')
        .toBe('revision-from-the-server');
    });

    it('reports a stale read as staleness and offers a way out of it', () => {
      // ⚠ WITHOUT THE WAY OUT THIS IS A DEAD END. The marker comes from the account this screen READ, and a
      // refusal does not change that account - so pressing Update again sends the same refused marker.
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      expectRequest('PUT', userUrl(7), 'the update').flush(
        problem('user.concurrency_conflict', 409, 'The account was changed by another request.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const text: string = host().textContent ?? '';

      expect(text)
        .withContext('the stale read is worded as staleness, not as a duplicate address')
        .toContain('This account was changed by someone else after you opened it');
      expect(text).not.toContain('requires a unique Email Address');
      expect(text).toContain('Read this account again');
    });

    it('re-reads the account on the recovery command, replacing the refused edits', () => {
      arriveEditing(account(7, { firstName: 'Ada' }));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      expectRequest('PUT', userUrl(7), 'the update').flush(
        problem('user.concurrency_conflict', 409, 'The account was changed by another request.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      press('Read this account again');

      // The re-read is what puts a CURRENT marker in the screen's hands; without it every later save
      // carries the refused one.
      expectRequest('GET', userUrl(7), 'the re-read').flush(
        envelope(account(7, { firstName: 'Grace', concurrencyToken: 'a-newer-revision' })),
      );
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.firstName).value)
        .withContext('the stored values replace what was typed, which the recovery wording says plainly')
        .toBe('Grace');

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      const retried = expectRequest('PUT', userUrl(7), 'the retry').request.body as UpdateUserRequest;

      expect(retried.concurrencyToken)
        .withContext('the retry carries the NEWER marker, which is what makes the recovery a recovery')
        .toBe('a-newer-revision');
    });

    it('does not treat a duplicate-address 409 as a stale read', () => {
      // ⚠ THE TWO `409`s ARE NOT THE SAME FAILURE. A duplicate address is corrected in a field and the
      // submission can be retried as it stands; a stale read cannot be corrected in the form at all, because
      // every later save carries the same refused marker. Only the second earns the recovery affordance.
      arriveEditing(account(7));

      type(CONTROL_ID.email, 'taken@example.test');
      press(UPDATE_SUBMIT_LABEL);

      expectRequest('PUT', userUrl(7), 'the update').flush(
        problem('user.update.duplicate_email', 409, 'That address is already used.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const text: string = host().textContent ?? '';

      // The shared banner states it, which is the application's one assertive owner for a refusal.
      expect(text).toContain('That address is already used.');
      expect(text).not.toContain('This account was changed by someone else');
      expect(text)
        .withContext('a duplicate address is not a dead end, so no re-read command is offered')
        .not.toContain('Read this account again');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // THE TENANT'S OWN ADDRESS EXPRESSION — A SECOND RULE, SURFACED AS AN ADVISORY
  // ---------------------------------------------------------------------------------------------------

  describe("the tenant's own address expression", () => {
    /** DotNetNuke's own default, verbatim: a final label of two to four letters. */
    const LEGACY_DEFAULT = {
      securityEmailValidation: "\\b[a-zA-Z0-9._%\\-+']+@[a-zA-Z0-9.\\-]+\\.[a-zA-Z]{2,4}\\b",
    };

    const ADVISORY = 'This site requires email addresses to match its own configured pattern';

    /** Mounts in edit mode with the tenant's legacy-default expression in force. */
    function arriveEditingUnderLegacyDefault(held: UserDetail): void {
      create(String(held.userId), LEGACY_DEFAULT);
      expectRequest('GET', userUrl(held.userId), 'the account read').flush(envelope(held));
      fixture.detectChanges();
    }

    it('says nothing about an address that is already stored, however the expression judges it', () => {
      // ⚠ THE CLIENT MIRRORS THE SERVER'S GRANDFATHERING. `.local` has a five-letter final label, so the
      // tenant's expression refuses it - but it is already in the column, and no submission could satisfy
      // the rule short of altering data the operator never came to change. Warning here would tell them a
      // surname edit was about to be refused when it was not.
      arriveEditingUnderLegacyDefault(account(7, { email: 'member@setup.local' }));

      expect(host().textContent ?? '')
        .withContext('an unchanged address is admitted by being there')
        .not.toContain(ADVISORY);

      type(CONTROL_ID.firstName, 'Augusta');

      expect(host().textContent ?? '')
        .withContext('and editing an unrelated field does not change that')
        .not.toContain(ADVISORY);
    });

    it('warns about a NEW address the expression refuses, without blocking the save', () => {
      arriveEditingUnderLegacyDefault(account(7, { email: 'member@setup.local' }));

      type(CONTROL_ID.email, 'someone.else@setup.local');

      expect(host().textContent ?? '')
        .withContext('a changed address is put to the tenant rule, and a mismatch is said out loud')
        .toContain(ADVISORY);

      // ⚠ AN ADVISORY, NOT A VALIDATOR. Blocking here would recreate the very refusal the server side of
      // this pair was fixed for.
      expect(button(UPDATE_SUBMIT_LABEL)?.disabled)
        .withContext('the operator may still save; the server decides')
        .toBeFalse();

      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7), 'the update should still be issued');
    });

    it('says nothing about a new address the expression accepts', () => {
      arriveEditingUnderLegacyDefault(account(7, { email: 'member@setup.local' }));

      type(CONTROL_ID.email, 'someone.else@example.com');

      expect(host().textContent ?? '').not.toContain(ADVISORY);
    });

    it('stays silent when the tenant publishes an expression this browser cannot compile', () => {
      // Operator-authored text from the database can be any string at all, and .NET and ECMAScript
      // regular-expression syntax differ - so an uncompilable value must leave the notice silent rather
      // than throw inside a projection and take the form down with it.
      create('7', { securityEmailValidation: '([unclosed' });
      expectRequest('GET', userUrl(7), 'the account read').flush(envelope(account(7)));
      fixture.detectChanges();

      type(CONTROL_ID.email, 'anything@example.test');

      expect(host().textContent ?? '').not.toContain(ADVISORY);
      expect(button(UPDATE_SUBMIT_LABEL)?.disabled).toBeFalse();
    });

    it('stays silent when the tenant publishes no expression at all', () => {
      arriveEditing(account(7, { email: 'member@setup.local' }));

      type(CONTROL_ID.email, 'other@setup.local');

      expect(host().textContent ?? '').not.toContain(ADVISORY);
    });
  });

  describe('a tenant that composes display names itself', () => {
    // ⚠ THE LEGACY RULE HAS TWO ARMS AND THEY DIFFER. `UserEditorCreated` (`User.ascx.vb` L397-L406):
    //   Case "displayname"
    //     setting = GetSetting(UserPortalID, "Security_DisplayNameFormat")
    //     If setting is not Nothing AndAlso not empty Then
    //       If AddUser Then e.Editor.Visible = False        <- HIDDEN while creating
    //       Else e.Editor.EditMode = PropertyEditorMode.View <- READ-ONLY while editing
    //
    // Before this, the field was fully editable in both cases while the help line told the operator to
    // "Provide a Display Name" - so a name they typed was silently replaced by the composed one on save.

    const COMPOSED = { securityDisplayNameFormat: '[FIRSTNAME] [LASTNAME]' };

    /** Mounts in edit mode with a tenant policy that composes display names. */
    function arriveEditingComposed(held: UserDetail = account(7)): void {
      create(String(held.userId), COMPOSED);
      expectRequest('GET', userUrl(held.userId), 'the account read').flush(envelope(held));
      fixture.detectChanges();
    }

    /**
     * Reveals a field's help line, which the shared field control renders only while its disclosure is
     * expanded, and returns the text.
     */
    function helpTextFor(controlId: string): string {
      const wrapper = field<HTMLElement>(controlId).closest('app-form-field');
      const toggle = wrapper?.querySelector<HTMLButtonElement>('.form-field__help-toggle');

      toggle?.click();
      fixture.detectChanges();

      return (wrapper?.querySelector('.form-field__help')?.textContent ?? '').trim();
    }

    it('presents the display name for READING ONLY while editing', () => {
      arriveEditingComposed();

      const input = field<HTMLInputElement>(CONTROL_ID.displayName);

      expect(input.readOnly).withContext('read-only, so the composed value is visible').toBeTrue();
      expect(input.getAttribute('aria-readonly')).toBe('true');
    });

    it('keeps the read-only field in the tab order rather than disabling it', () => {
      // `readonly` rather than `disabled` deliberately: a disabled control leaves the tab order and is
      // skipped by assistive technology, so a keyboard operator would never learn the value exists.
      arriveEditingComposed();

      const input = field<HTMLInputElement>(CONTROL_ID.displayName);

      expect(input.disabled).withContext('not disabled').toBeFalse();
      expect(input.tabIndex).withContext('still reachable by Tab').toBe(0);
    });

    it('replaces the help line that contradicted a field the operator cannot change', () => {
      arriveEditingComposed();

      const shown = helpTextFor(CONTROL_ID.displayName);

      expect(shown).toBe(
        'This site composes display names from a set format, so this value cannot be changed here.',
      );
      expect(shown)
        .withContext('the instruction to supply one is withdrawn')
        .not.toContain('Provide a Display Name');
    });

    it('WITHHOLDS the field entirely while creating', () => {
      create(undefined, COMPOSED);

      expect(query(`#${CONTROL_ID.displayName}`))
        .withContext('nothing to show: the name will be composed from parts not yet supplied')
        .toBeNull();
    });

    it('still allows the creation form to be submitted with the field withheld', () => {
      // ⚠ THE TRAP THIS PINS. A required control that is not rendered leaves the form invalid with nothing
      // on screen to correct, which would be a worse defect than the one being fixed. Dropping the rule is
      // safe because the server composes the value in exactly this case.
      create(undefined, COMPOSED);

      // Filled field by field rather than through the shared helper, because the helper fills the display
      // name and this case is precisely the one where that field is not rendered.
      type(CONTROL_ID.username, 'ada.lovelace');
      type(CONTROL_ID.firstName, 'Ada');
      type(CONTROL_ID.lastName, 'Lovelace');
      type(CONTROL_ID.email, 'ada@example.test');
      type(CONTROL_ID.password, 'Str0ngPass');
      type(CONTROL_ID.confirmPassword, 'Str0ngPass');
      press(CREATE_SUBMIT_LABEL);

      const written = httpMock.match(
        (candidate) => candidate.method === 'POST' && candidate.url === USERS_URL,
      );

      expect(written).withContext('the form was submitted, not blocked').toHaveSize(1);
      written[0]?.flush(envelope(account(21)));
      fixture.detectChanges();
    });

    it('leaves the field editable and required for a tenant that composes nothing', () => {
      // The negative control. The rule must apply only when a format is actually stored - a tenant that has
      // never configured one stores the empty string, and treating that as configured would lock the field
      // on every ordinary site.
      arriveEditing(account(7));

      const input = field<HTMLInputElement>(CONTROL_ID.displayName);

      expect(input.readOnly).toBeFalse();
      expect(input.disabled).toBeFalse();
      expect(helpTextFor(CONTROL_ID.displayName)).toBe('Provide a Display Name');
    });

    it('treats a format of only whitespace as no format at all', () => {
      create(String(7), { securityDisplayNameFormat: '   ' });
      expectRequest('GET', userUrl(7), 'the account read').flush(envelope(account(7)));
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.displayName).readOnly).toBeFalse();
    });
  });

  // =========================================================================
  // CONFIRMING A REMOVAL, AND RETURNING WHERE THE OPERATOR CAME FROM
  // =========================================================================

  describe('removing an account', () => {
    it('CONFIRMS the removal, which was the only mutation this screen performed silently', () => {
      // Creating announced, updating announced, authorising and forcing a credential change announced - and
      // the single most destructive action said nothing at all. The operator was returned to the listing and
      // left to infer from an absence that the account was gone, which is indistinguishable from a removal
      // that silently failed.
      arriveEditing(account(7));
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7), 'the removal').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();

      expect(notifications()).toContain(
        jasmine.objectContaining({ severity: 'success', message: 'User Deleted Successfully' }),
      );
    });

    it('publishes the confirmation so that it OUTLIVES the departure this same handler performs', () => {
      // ⚠ THIS CASE EXISTS BECAUSE THE PREVIOUS ONE PASSED WHILE THE BROWSER SHOWED NOTHING. Asserting that
      // `notify` was CALLED cannot distinguish an announcement that renders from one that is published and
      // then destroyed: `survivesNavigation` defaults to false, the handler navigates immediately, and the
      // router's `clearOnNavigation()` sweep discards every unflagged entry before a frame is painted. The
      // navigation is stubbed in this suite, so the sweep is invoked explicitly here - that call is exactly
      // what the router would have done, and it is the input that DISCRIMINATES between the two
      // implementations.
      const service = TestBed.inject(NotificationService);

      arriveEditing(account(7));
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7), 'the removal').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();

      service.clearOnNavigation();

      expect(service.notifications().map((entry) => entry.message)).toContain(USER_DELETED_MESSAGE);
    });

    it('uses the SAME sentence the listing uses for the same event', () => {
      // Imported from the shared wording module rather than restated, so the confirmation cannot depend on
      // which screen the operator started from.
      expect(USER_DELETED_MESSAGE).toBe('User Deleted Successfully');
    });

    it('says nothing when the removal was refused', () => {
      // A confirmation announced unconditionally would be worse than none: it would assert that a record was
      // removed when it is still there.
      arriveEditing(account(7));
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7), 'the removal').flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).not.toContain(
        jasmine.objectContaining({ message: 'User Deleted Successfully' }),
      );
    });

    it('returns to the listing state the operator came from, not the bare address', () => {
      // U8. The listing opens on no query at all, so returning to `/users` with nothing attached resets the
      // operator to an empty screen and hides the result of their own action. The legacy screen returned to
      // `NavigateURL(TabId, "", UserFilter)` - the FILTERED listing - which is what the shared return-state
      // store now supplies.
      const remembered = TestBed.inject(ListReturnStore);
      remembered.remember(USER_LIST_ROUTE, { searchby: 'all', currentpage: '2' });

      arriveEditing(account(7));
      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);
      expectRequest('DELETE', userUrl(7), 'the removal').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();

      const navigated = TestBed.inject(Router).navigate as jasmine.Spy;

      expect(navigated).toHaveBeenCalled();
      const [commands, options] = navigated.calls.mostRecent().args as [
        readonly unknown[],
        { queryParams?: Record<string, unknown>; replaceUrl?: boolean },
      ];

      expect(commands).toEqual([USER_LIST_ROUTE]);
      expect(options.queryParams)
        .withContext('the remembered coordinate travels with the return')
        .toEqual(jasmine.objectContaining({ searchby: 'all', currentpage: '2' }));
      expect(options.replaceUrl)
        .withContext('BACK must not return to a form for a record that no longer exists')
        .toBeTrue();
    });
  });


  // ---------------------------------------------------------------------------------------------------
  // A WRITE WHOSE SESSION ENDED UNDERNEATH IT
  // ---------------------------------------------------------------------------------------------------

  describe('a write the session outlived', () => {
    /**
     * Ends the session the way a refused token renewal does, and drains whatever the cancellation left
     * outstanding so the shared `httpMock.verify()` still speaks for the case that follows.
     */
    function endSessionUnderneathTheWrite(): void {
      TestBed.inject(SessionTeardownService).purge('renewalRefused');
      fixture.detectChanges();
      httpMock.match(() => true);
    }

    it('does not announce a successful UPDATE when the session ended before the write settled', () => {
      // ⚠ THE MEASURED DEFECT. Runtime testing injected a 401 on `PUT /api/v1/users/16`; the renewal that
      // followed was refused, so the session was purged - and a purge empties the store's failure slot AND
      // lowers its in-flight count. Every settle path here read `failure === null` as success, so the
      // operator was told "User account updated" for a write that never reached the API, and a fresh read
      // showed the old value. An outcome nobody can establish must not be asserted.
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);

      expectRequest('PUT', userUrl(7), 'the write that will be abandoned');

      endSessionUnderneathTheWrite();

      expect(successSpy)
        .withContext('the write was never settled by the server, so nothing may be confirmed')
        .not.toHaveBeenCalledWith(USER_UPDATED_MESSAGE);
    });

    it('does not announce a successful MEMBERSHIP command when the session ended before it settled', () => {
      arriveEditing(account(7, { isLockedOut: true }));

      press(UNLOCK_LABEL);

      expectRequest('POST', `${userUrl(7)}/unlock`, 'the release that will be abandoned');

      endSessionUnderneathTheWrite();

      expect(successSpy)
        .withContext('the same emptied slot decides the four membership commands')
        .not.toHaveBeenCalledWith(USER_UNLOCKED_MESSAGE);
    });

    it('still announces an update that DID settle, so the guard has not silenced the success path', () => {
      // The discriminating control. A guard that simply stopped announcing would pass both cases above.
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();

      expect(successSpy).toHaveBeenCalledWith(USER_UPDATED_MESSAGE);
    });
  });

});
