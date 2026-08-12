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
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
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
 * Specification for the account editor.
 *
 * One screen, two modes, and SEVEN write paths that reach four different endpoints — which is
 * why the cases below are grouped by path rather than by member. Six invariants are named up
 * front because each one, got wrong, produces a screen that looks right and is not:
 *
 *   - ⚠ ACCOUNT ZERO IS NOT "NO ACCOUNT". The route input arrives as a STRING and is parsed
 *     with an explicit presence test, never a truthiness test, because `'0'` parses to a
 *     legitimate identifier. Every identifier-shaped case here is driven through the input as
 *     text so the real transform runs.
 *   - ⚠ THE UPDATE CONTRACT IS FOUR MEMBERS, AND A PRISTINE FORM SENDS NOTHING. The sign-in
 *     name is read-only because there is no rename path; the authorisation flag, the lockout
 *     flag and the credential each have their own endpoint. That separation is what stops a
 *     routine details edit from silently carrying an authorisation change.
 *   - ⚠ SUCCESS IS THE HTTP STATUS, NEVER A NUMERIC OUTCOME. Creation succeeds at 201 and an
 *     update at 200. No contract on this boundary carries a legacy outcome ordinal, and the
 *     three legacy vocabularies disagree about which value means success — creation at
 *     THIRTEEN, sign-in at one, password at zero — so an assumption that zero means success
 *     would be wrong two times in three.
 *   - ⚠ SIX OF THE SEVEN ACTIONS MUST NOT VALIDATE. Only the submit command carried
 *     `causesvalidation="True"`; the removal and all four membership transitions carried
 *     `causesvalidation="False"`. Authorising an account has nothing to do with whether its
 *     display name is filled in.
 *   - ⚠ EVERY WRITE IS FOLLOWED BY A READ THE STORE OWNS. Create, update, delete and the two
 *     approval-shaped transitions all re-read the listing; the three membership transitions
 *     also re-read the account. A case answering only its write leaves requests outstanding
 *     and `verify` reports them.
 *   - ⚠ NO CREDENTIAL IS EVER RENDERED, ANNOUNCED OR LOGGED. The password inputs exist only
 *     in create mode, are typed `password`, and no value typed into them appears in the
 *     document text or in any notification.
 */
describe('UserFormComponent', () => {
  let fixture: ComponentFixture<UserFormComponent>;
  let reference: ComponentRef<UserFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
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
   * ⚠ AUTHORED BECAUSE IT WAS ABSENT — NOT measured wording, and it must not be presented as
   * such.
   *
   * `ManageUsers.ascx.vb` L749 raises the resource key `"UserUnLocked"` on a successful release,
   * but **that key is defined in NO resource file**: it appears in neither
   * `Website/admin/Users/App_LocalResources/*.resx` nor `Website/App_GlobalResources/*.resx`. The
   * only unlock-related entry anywhere is `cmdUnLock.Text` = `Unlock Account` in
   * `Membership.ascx.resx`, which is the BUTTON LABEL and not a success sentence. So the legacy
   * screen asked for a string that did not exist and DotNetNuke's localisation fell back to
   * emitting the raw key.
   *
   * This sentence is therefore authored to match the wording of its two measured siblings above,
   * and the gap is recorded rather than dressed up as a measurement. Claiming measured wording
   * that does not exist would be the worse error.
   */
  const USER_UNLOCKED_MESSAGE = 'User successfully Unlocked';
  const PASSWORD_CHANGE_REQUIRED_MESSAGE = 'This user must change their password at next login';
  const USER_UPDATED_MESSAGE = 'User account updated';

  /**
   * The creation confirmation, with the placeholder the component substitutes.
   *
   * Restated here rather than imported for the reason every other sentence in this block is: the wording is
   * the subject of the assertion, so a case must fail when the component's copy changes rather than follow
   * it silently.
   */
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
   * The reason phrase the API publishes as a problem `title`, keyed by status.
   *
   * ⚠ NOT FREE TEXT. Every refusal reaches the wire through one shared problem factory that
   * fills the title from this status-keyed vocabulary.
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

    /**
   * The correlation identifier every refusal fixture below carries.
   *
   * Named rather than left inline, so an assertion about the reference an operator is handed can state
   * the identifier it expects instead of repeating the literal and hoping the two stay equal.
   */
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
   * One account as the server reports it.
   *
   * ⚠ THE DEFAULT IDENTIFIER IS ZERO. `Users.UserID` seeds at one in the legacy schema, but the
   * screen must not depend on that: the input is parsed rather than inspected, and zero is the
   * value that proves it.
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
      ...overrides,
    };
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /**
   * A page of accounts.
   *
   * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture spelling it
   * otherwise flushes successfully and unwraps to no rows at all.
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
   * Mounts the screen.
   *
   * ⚠ THE IDENTIFIER IS DELIVERED AS TEXT, because the router binds a route parameter as a
   * string. Passing a number would bypass the parse this screen performs and would leave the
   * `'0'` case untested.
   */
  function create(userId?: string): void {
    fixture = TestBed.createComponent(UserFormComponent);
    reference = fixture.componentRef;

    if (userId !== undefined) {
      reference.setInput('userId', userId);
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
   *
   * The identity is READ FROM THE STORED SESSION rather than fetched, so seating it is the whole of the
   * first half of the measured gate at `Membership.ascx.vb` L135 — `UserInfo.UserID = User.UserID`, over
   * the signed-in operator (`PortalModuleBase.vb` L319-L323) and the account under edit
   * (`UserModuleBase.vb` L439-L449). The expiry is a FIXED literal: reading the clock in a specification
   * would make it depend on when the specification runs.
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
   * Asserts that a write provoked NO listing request.
   *
   * ⚠ THE LISTING RE-READ IS CONDITIONAL ON A SEARCH HAVING BEEN CHOSEN. Every write command on
   * the account store ends by re-reading the listing, but that read declines to issue anything
   * while the search state is the opening no-query state - reproducing the legacy screen's
   * fall-through, which left its grid unbound rather than listing every account in the tenant.
   * This screen never chooses a search, so a write from here provokes no listing request at all.
   * A case that expected one would wait for a request that is never made.
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
   * Presses a button of the OPEN CONFIRMATION, scoped to the dialogue.
   *
   * ⚠ THE ROW COMMAND AND THE DIALOGUE COMMAND SHARE ONE WORDING — both read "Delete" — and the
   * row command comes first in document order, so an unscoped lookup re-opens the question
   * instead of answering it. The danger button additionally prefixes a warning glyph, so the
   * wording is matched with `includes`.
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
      /*
       * ⚠ THIS CASE USED TO REQUIRE `CREATE_MODE_TITLE` HERE, which put `Add New User` at the top of
       * a screen whose body says the account does not exist — and disagreed with the route's own
       * document title, `Edit User Accounts`. A real browser measured three labels on that one
       * screen. The heading's question is whether the address NAMES an account, not whether one
       * could be read from it.
       *
       * It also makes `/users/abc` and `/users/0` agree: unreadable and readable-but-absent both
       * name an account, neither can produce one, and both now carry this heading over the same
       * measured sentence. `ManageUsers.ascx.vb` L207/L221 is the authority — a missing account kept
       * the edit screen's title and paired the warning with `DisableForm()`.
       *
       * The withdrawal of the FORM is asserted separately, by "offers no form at all when the
       * parameter is not an identifier"; this case is only about the wording at the top.
       */
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

      // ⚠ THE ROLE ACTION MUST CARRY THE ACCOUNT. This assertion used to read `/roles`, which
      // encoded the defect rather than the requirement: an action captioned "Manage Roles for this
      // User" landed on the unfiltered listing of every role in the tenant. The account-keyed
      // address was available all along — the sibling account LISTING already links `/roles` with
      // `queryParams {userId}` — so the caption was promising something the link did not do.
      expect(links).toContain('/roles?userId=7');

      // And nothing still points at the bare listing, so the corrected address cannot sit beside a
      // leftover copy of the old one.
      expect(links).not.toContain('/roles');
    });

    it('says so and offers no form when the read is refused as not-found', () => {
      create('7');

      // MIGRATION: this case used to flush a `200` carrying nothing, on the reading that the
      //   account transport could report absence inside the envelope. It cannot: the API answers
      //   a not-found problem document as soon as a value-bearing outcome carries no value, so
      //   the state the legacy worded as `NoUser` arrives as a `404` and is asserted as one here.
      //   The wording, the hidden form and the single announcement are unchanged, which is the
      //   point — the presentation the operator sees is the measured one either way.
      expectRequest('GET', userUrl(7)).flush(
        problem('resource.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(host().textContent ?? '').toContain(NO_USER_MESSAGE);
      expect(query('form.user-form')).withContext('no form for an account that does not exist').toBeNull();

      // ANNOUNCED EXACTLY ONCE, by the shared refusal announcer, at the warning severity the
      // measured vocabulary gives a lookup outcome. The dedicated effect that used to announce
      // this state a second time is gone with the successful-null contract that produced it, so
      // the shared live region carries one sentence rather than two.
      expect(notifySpy.calls.allArgs().map((args) => [String(args[0]), String(args[1])])).toEqual([
        ['warning', 'The requested resource does not exist.'],
      ]);
      expect(warningSpy)
        .withContext('no second announcement of the same state')
        .not.toHaveBeenCalled();
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

    // ⚠ THE NEXT TWO GUARD A FOCUS MOVE NO OTHER MECHANISM CAN MAKE. The password rule is a GROUP
    // rule: it marks the form invalid and leaves both boxes individually valid, so neither carries
    // Angular's invalid class and the shared first-invalid directive correctly declines. Without the
    // component supplying the move, a submit refused only by this rule left focus on the submit
    // button — which on this screen was measured 388 units BELOW the fold at a 1280x900 viewport, so
    // the message appeared off-screen above somebody who had scrolled down to press it.
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

    // ⚠ THIS IS THE CASE THAT REGRESSED, AND IT REGRESSED SILENTLY. The first implementation deferred
    // the focus move to a resolved promise, on the assumption that a microtask settles after change
    // detection. It does not — a microtask queued inside the event handler runs BEFORE the scheduled
    // render, so the callback looked the box up, found it absent because the disclosure had not been
    // rendered open yet, and returned. Measured in a real browser across two trials, 30 polls over
    // 3007ms and 25 over 2500ms: the section DID open and the box DID exist, and focus never moved at
    // all. Nothing in this suite caught it, because no specification exercised the collapsed path.
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
      // going to tell them what happened. A browser audit measured it on the role form - `201`, never
      // aborted, record created, no confirmation anywhere - and this screen has the identical shape, so
      // fixing one and not the other would be a trap for whoever met the second.
      //
      // The hand-over announces and deliberately does NOT navigate: the operator chose to be elsewhere.
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
      // The other half, and deliberately silent: a refusal is a document whose home is the banner ON this
      // screen, and this screen is gone. There is no field for a field message to sit beside and no form to
      // correct. The store still holds the failure, so returning here presents it in full.
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
      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], { replaceUrl: true });
    });

    it('leaves the form settled at the instant it navigates, so the guard cannot question a stored account', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      create();
      fillCreationForm();

      // THE CONTROL. Without it a later `false` would be indistinguishable from a probe that was never
      // registered, or from a form that was never dirty. `isDirty()` is the guard's own public surface, so
      // this is asserted through the very call the guard makes.
      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is what the guard exists to catch')
        .toBeTrue();

      // ⚠ SAMPLED AT THE INSTANT OF NAVIGATION, NOT AFTERWARDS, because it is the navigation the
      // creation itself triggers that the guard would have refused - and refusing was worse than the prompt:
      // the operator would have been left on a creation form for an account the server had already stored,
      // and re-submitting it would have been answered with a conflict.
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], { replaceUrl: true });
      expect(dirtyAtNavigation)
        .withContext('the guard must see a settled form on the navigation the creation itself triggered')
        .toBeFalse();
    });

    // ⚠ MAJOR (CWE-316 cleartext storage) — WHAT HAPPENS TO THE TYPED CREDENTIAL AFTER THE ACCOUNT EXISTS.
    //
    // The password and its confirmation are the only secrets this form holds. Once the server has stored the
    // account this copy has no further purpose, yet settling the form does not remove it: the controls keep
    // their values and the two password inputs keep those values in the live document.
    //
    // This screen has TWO ways for a departure to fail to dispose of them, not one. The ordinary branch's
    // navigation can be refused by a guard, resolve `false`, or reject when a lazy chunk fails; and the
    // generated-credential branch does not navigate AT ALL - it deliberately stays here for as long as the
    // operator takes to write the generated password down. The cases below pin the clearing in each.

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

        // The navigation completed here, so the screen is gone from the router's point of view - but the
        // component is still mounted in this fixture, which is exactly the state a refused navigation leaves
        // a real operator in, and the controls are empty in both.
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
        // untouched along with its value. Otherwise the group's password rules would fire against the very
        // credential the screen deliberately removed, and the operator would be told to supply a password
        // for an account that already exists.
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
        // then chooses generation still leaves the typed value in the control behind the panel. The panel
        // stays until dismissed, so before this the typed credential sat there for as long as the operator
        // took, with no navigation pending to remove it.
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], { replaceUrl: true });
    });

    it('states the notification gap BESIDE the box, while there is still a decision to make', () => {
      create();

      // MIGRATION: the notify box has NO member on the creation contract, so its value cannot be
      // transmitted. The control is retained because it is part of the agreed member contract — but it
      // is rendered UNTICKED AND DISABLED, departing from the measured `checked="True"` at
      // `User.ascx:L25` deliberately, because a ticked box that does nothing is a false statement about
      // what will happen.
      const box = query<HTMLInputElement>(`#${CONTROL_ID.notify}`);

      expect(box).withContext('the control is still rendered').not.toBeNull();
      expect((box as HTMLInputElement).checked).withContext('unticked').toBeFalse();
      expect((box as HTMLInputElement).disabled).withContext('and not offered').toBeTrue();

      // ⚠ THE SENTENCE IS AVAILABLE BEFORE THE SUBMISSION, NOT AFTER IT, AND THAT IS THE WHOLE
      // REMEDIATION. The earlier arrangement kept the box ticked and raised a warning once the account
      // had ALREADY been created: the operator asked for a notification, was told the request
      // succeeded, and only then learned that nothing had been sent. A control that cannot act must say
      // so while the decision is still open.
      //
      // ⚠ IT IS REACHED THROUGH THE FIELD'S HELP AFFORDANCE RATHER THAN PRINTED INLINE, which is the
      // shared field's arrangement and not this screen's: it reproduces the legacy help BUTTON at
      // `Website/controls/helpbuttoncontrol.ascx`, which revealed its text on demand. Asserting the
      // sentence were present on arrival would be asserting a different design — so the affordance is
      // found beside THIS control and operated, exactly as an operator would.
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

      // ⚠ THE ADVISORY MUST NOT CARRY THE CREDENTIAL. Generation moved to the browser because
      // the contract has no field with which to request it, which makes the operator responsible
      // for conveying it — but the announcement channel is not where it may be conveyed.
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

      // ⚠ THE MEASURED CREATION VOCABULARY IS NOT REACHED ON THIS PATH, and the reason is
      // structural rather than accidental: the code-specific sentence is selected only where a
      // failure is worded for the transient channel, which happens for a warning-severity
      // refusal or for a failure that arrived without a document. A conflict is neither. The
      // measured sentence for a duplicate sign-in name is therefore recorded as UNREACHABLE for
      // a 409 rather than asserted as shown - asserting it would certify behaviour the screen
      // does not have, and inventing it here would put the wording in two places.
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

      // ⚠ FOUR MEMBERS AND NO MORE. No sign-in name, because there is no rename path; no
      // authorisation flag, no lockout flag and no credential, because each has its own
      // endpoint — which is what stops a details edit from carrying an authorisation change.
      expect(Object.keys(body as unknown as Record<string, unknown>).sort()).toEqual([
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

    it('announces the measured wording and stays on the screen', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(envelope(account(7, { firstName: 'Augusta' })));
      fixture.detectChanges();
      expectNoListingReRead();

      // No redirect: the legacy raised two completion events and the container handled neither,
      // so the legacy screen stayed exactly where it was.
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

      // ⚠ THE FORM IS NOT WITHHELD FOR A CONFLICT, and the refusal paragraph is rendered only
      // while it is - the withholding rule names the permission and not-found statuses alone.
      // A conflict therefore reaches the operator through the banner, in the server's own words,
      // and the measured address-conflict sentence is recorded as UNREACHABLE for a 409 rather
      // than asserted as shown.
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

      // MIGRATION — A DELIBERATE DIVERGENCE: the legacy routine named for disabling actually
      // HID all six panels. The form is kept visible and disabled so the operator can see what
      // the refusal refers to; withholding the context along with the affordance makes a refusal
      // unreadable.
      expect(host().textContent ?? '').toContain(NOT_AUTHORIZED_MESSAGE);
      expect(field<HTMLInputElement>(CONTROL_ID.firstName).disabled).withContext('withheld').toBeTrue();
      expect(button(UPDATE_SUBMIT_LABEL)?.disabled).toBeTrue();
    });

    it('QUOTES the support reference when a refusal is announced', () => {
      // ⚠ THE CHANNEL THAT CARRIES REFUSALS IS THE ONE THAT WAS DROPPING THE IDENTIFIER. The shared
      // classifier resolves 401, 403, 404 and 429 to WARNING, so every server refusal this screen reports
      // travels the warning channel - and the banner deliberately keeps only ERROR-severity failures, to
      // avoid saying the same thing twice. That makes this announcement the WHOLE report for a refusal, and
      // it was handing the operator a refused save with no quotable identifier anywhere on the screen.
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
      // The other half of the rule: the identifier is quoted because the answer HAD one, never as
      // decoration. Without this case the one above could be satisfied by inventing an identifier, which
      // would hand support a reference it cannot find.
      //
      // ⚠ A REFUSAL DOCUMENT WITHOUT A CORRELATION MEMBER, NOT A TRANSPORT FAILURE. Written first as
      // a dropped connection, which does not exercise this screen at all: a transport failure is announced
      // by the shared HTTP failure interceptor, not by this component, so the case measured zero
      // announcements and failed for a reason that had nothing to do with references. The refusal is
      // therefore expressed the way the rule is actually reached - a genuine `403` this screen announces
      // itself, whose document simply carries no identifier to pass on. The fixture builder always supplies
      // one, so the document is written out here rather than borrowed from it.
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

      // ⚠ ONE ENDPOINT CARRYING THE DESIRED STATE, not a pair of verb-shaped routes. Setting a
      // state an account already holds is reported as a conflict, an answer that is only
      // meaningful if the caller said which state it meant.
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

      // ⚠ NOTHING IS ANNOUNCED BEFORE THE SERVER HAS AGREED, and the ordering is the assertion. An
      // advisory raised at the moment of pressing would be a statement about an authorisation that had
      // not happened yet: a refusal one moment later would leave the operator told that an account was
      // authorised without mail when in fact it was not authorised at all.
      expect(warningSpy).withContext('not before the server has answered').not.toHaveBeenCalled();

      expectRequest('PUT', `${userUrl(7)}/approval`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7, { isApproved: true })));
      fixture.detectChanges();

      // MIGRATION: the legacy handler ALSO sent registration mail here —
      // `ManageUsers.ascx.vb:L708` called `Mail.SendMail(User, MessageType.UserRegistrationPublic,
      // PortalSettings)`. There is no mail endpoint, so that half is a documented functional reduction
      // and it is STATED rather than silently absent: an operator who authorises an account has every
      // reason to believe a welcome message went out, and would otherwise never learn that none did.
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

      // Asserted AFTER the transition has settled, which is the only place it means anything: the
      // advisory channel for the sibling is driven from exactly this point, so checking before the
      // answer arrived would pass for a screen that advised on every transition alike. Its two
      // siblings sent no mail — withdrawing authorisation at `ManageUsers.ascx.vb:L728-L730` and
      // releasing a lockout at L747-L751 both end at `BindMembership()` with no notification — so
      // there is nothing to reduce and nothing to say.
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
      // ⚠ THE FIXTURE MUST BE AN ACCOUNT THE RELEASE IS ACTUALLY OFFERED FOR. Each of the four is
      // rendered only when its own precondition holds — `cmdUnLock.Visible = Membership.LockedOut`
      // at `Membership.ascx.vb` L141 — and the default fixture is UNLOCKED, so pressing the release
      // against it would be pressing a control the measured screen does not show either. The point
      // this case makes is about VALIDATION, not about the gate, so it states the gate's precondition
      // explicitly and leaves the assertion below untouched.
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
      // ⚠ "ALL FOUR AT ONCE" IS UNREACHABLE BY CONSTRUCTION, which is why this case no longer claims
      // it. Authorize is offered for an account that may NOT sign in and UnAuthorize for one that may
      // (`Membership.ascx.vb` L142-L143), so the two are complements of a single fact and exactly one
      // of them is ever present. THREE is the most that can be offered together, and this fixture
      // reaches it: unapproved, locked out, and not yet obliged to change its password.
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

      // Each offered control is present AND disabled. `?.disabled` is deliberately not used here: on
      // an absent control it yields `undefined`, which no longer distinguishes "withheld by the
      // refusal" from "never offered at all" — and telling those two apart is the whole point.
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

    // The four gates themselves.
    //
    //  ⚠ THESE COVER THE DEFECT DIRECTLY: all four transitions were offered unconditionally, so an
    //  approved and unlocked account was invited to be authorised and unlocked. `Membership.ascx`
    //  L13-L28 declares all four with no condition, which is why a reading confined to the markup
    //  produced that behaviour — but `Membership.ascx.vb` L135-L145 assigns every one of their
    //  `Visible` properties on each data-bind, and those assignments are the contract.

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
      // `Membership.ascx.vb` L135-L139: when the signed-in operator IS the subject, all four are
      // withheld whatever the membership facts say. This fixture would otherwise offer three.
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], { replaceUrl: true });
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users'], { replaceUrl: true });
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
      // operation refuses a super user, so the contract reports `canDelete: false` for one - which is
      // why this fixture sets both. It is no longer this screen's business to infer the second value
      // from the first.
      arriveEditing(account(7, { isSuperUser: true, canDelete: false }));

      expect(button(DELETE_LABEL)).withContext('withheld').toBeUndefined();
    });

    it('withholds the removal from the tenant\u2019s designated administrator', () => {
      /*
       * ⚠ THE CASE THAT WAS THE DEFECT, AND IT IS NOT COVERED BY THE ONE ABOVE. The removal operation
       * refuses TWO kinds of account: a super user, and the account named by the tenant's
       * `Portals.AdministratorId`. This screen used to derive the affordance from `!isSuperUser`
       * alone, which is only the first clause - so for the tenant's own administrator, who is NOT a
       * super user, the account listing correctly withheld the removal while this screen offered it.
       * Two surfaces disagreeing about one permission, and the offered action's only possible outcome
       * was a refusal.
       *
       * The administrator is unknowable from anything else in this contract, which is precisely why
       * the capability is published rather than computed here. The fixture therefore states exactly
       * what such an account looks like on the wire: not a super user, and not removable.
       */
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

      // The banner emits nothing at all when there is no problem, so the screen binds it
      // unconditionally and lets it disappear rather than guarding it.
      expect(query('.error-banner__title')).toBeNull();
      expect(query('app-error-banner')).toBeNull();
    });

    it('records the required-field legend once, as measured', () => {
      create();

      expect(host().textContent ?? '').toContain('All fields marked with a red arrow are required.');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE MEASURED PASSWORD POLICY, PINNED TO ITS EXACT BOUNDARY
  // ---------------------------------------------------------------------------------------------------
  //
  // ⚠⚠ THIS POLICY IS PRESERVED VERBATIM AND MUST NEVER BE TIGHTENED. Measured at
  // `Website/release.config` L242 `minRequiredPasswordLength="7"` and L243
  // `minRequiredNonalphanumericCharacters="0"`. Raising either figure during a migration
  // locks out every existing account whose password satisfied the old rule, which turns a
  // technology change into an outage. The cases below therefore pin the BOUNDARY rather
  // than a comfortable interior value: six characters must fail and seven must pass, and a
  // password with no punctuation at all must pass because ZERO punctuation is required.
  //
  // The figures are read from the component's own exported constants rather than repeated
  // as literals, so a change to the policy cannot pass this suite silently.

  describe('the measured password policy', () => {
    /**
     * Reads one group-level validator message without widening anything to `any`.
     *
     * `ValidationErrors` is an index signature over `any`, so the value is taken through
     * `unknown` and narrowed explicitly. `noPropertyAccessFromIndexSignature` additionally
     * requires the bracket form.
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
      // The exact values from `Website/release.config` L242 and L243. Asserted against the
      // component's exported constants, which is what every rule and every rendered sentence
      // on this screen is derived from.
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
      // ⚠ MEASURED PRECEDENCE, NOT AN IMPLEMENTATION DETAIL. `User.ascx.vb` L152 sets
      // `PasswordMismatch`, and L156's policy check is GUARDED by
      // `If createStatus = UserCreateStatus.AddUser` — so once the mismatch has moved the
      // status off the zero sentinel the policy check cannot run. A value that is both too
      // short AND mismatched reported the MISMATCH.
      const errors = passwordRulesValidator(() => true)(credentialGroup('abc', 'abcd'));

      expect(groupMessage(errors, 'passwordMismatch')).toBe(PASSWORD_MISMATCH_MESSAGE);
      expect(groupMessage(errors, 'invalidPassword'))
        .withContext('the policy check was short-circuited')
        .toBeNull();
    });

    it('applies neither rule once generation is chosen', () => {
      // `User.ascx.vb` L150 `If Not chkRandom.Checked Then` — generation SKIPS both rules
      // entirely, because L164 supplied the value instead of the operator. A mismatched,
      // far-too-short pair is therefore valid.
      expect(passwordRulesValidator(() => true)(credentialGroup('a', 'zz', true))).toBeNull();
    });

    it('applies no credential rule at all while editing', () => {
      // `User.ascx.vb` L148 `If AddUser And ShowPassword Then` — the whole block is
      // CREATE-ONLY, so in edit mode it cannot gate submission.
      expect(passwordRulesValidator(() => false)(credentialGroup('a', 'zz'))).toBeNull();
    });

    it('interpolates both measured figures into the sentence and leaves no raw token behind', () => {
      // The legacy sentence carried `[PasswordLength]` and `[NoneAlphabet]`, replaced at run
      // time by a plain `String.Replace` from the membership provider's configuration
      // (`Library/Components/Users/UserController.vb` L608-L609).
      //
      // MIGRATION: the TokenReplace subsystem is OUT OF SCOPE, so the two measured configured
      // values are interpolated directly from the policy constants. That is precisely what the
      // legacy substitution produced at run time, and it keeps the sentence honest if either
      // constant ever changes.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — THE CREATION-OUTCOME VOCABULARY
  // ---------------------------------------------------------------------------------------------------
  //
  // ⚠⚠ SUCCESS IS THIRTEEN AND ZERO IS NEVER SUCCESS. Verified at
  // `Library/Components/Users/Membership/UserCreateStatus.vb` L24-L41: eighteen members with
  // EXPLICIT ordinals nought to seventeen, in which `AddUser = 0` is the "nothing recorded
  // yet" sentinel and `Success = 13`. The legacy detected failure at `User.ascx.vb` L185 by
  // testing for any value OTHER than the zero sentinel, never by testing for zero.
  //
  // Across the three legacy vocabularies `UserValidStatus.VALID = 0`,
  // `UserLoginStatus.LOGIN_SUCCESS = 1` and `UserCreateStatus.Success = 13`, so an assumption
  // that zero means success is wrong two times in three. Every case here uses a NAMED member.

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
      // ⚠ THREE MEMBERS, ONE SENTENCE. `UserController.GetUserCreateStatus`
      // (Library/Components/Users/UserController.vb L598-L626) combined
      // `UsernameAlreadyExists`, `UserAlreadyRegistered` and `DuplicateUserName` into a single
      // `Case` arm resolving to the `UserNameExists` wording. Sharing a message is NOT being
      // the same outcome: merging or aliasing them would lose which one the server reported.
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
      // `ProviderError` (12), `UnexpectedError` (14), `DuplicateProviderUserKey` (4) and
      // `InvalidProviderUserKey` (9) all reached the same `Case` arm, and the API reports them
      // under one code for that reason.
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

      // ⚠ MIGRATION — DEFECT PRESERVED, AND THIS IS THE ASSERTION THAT KEEPS IT PRESERVED.
      // The measured value of `RegError.Text` in
      // `Website/App_GlobalResources/SharedResources.resx` line 301 misspells "Further" as
      // "Futher". The Minimal Change Clause requires a discovered defect to be ANNOTATED
      // rather than corrected, and requires error messages to be EQUIVALENT to the legacy
      // ones — so the shipped sentence must be this one, character for character.
      //
      // The measured value is written out in full rather than derived from the shipped
      // constant by substitution. A comparison that spelled the repair — asserting that the
      // shipped text differs from the measurement by exactly one word — would BLESS the
      // divergence instead of detecting it, and would keep passing however far the wording
      // drifted from the resource file so long as that one word still differed. Written out
      // in full, any edit to the shipped sentence fails here.
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

      // ⚠ NO ORDINAL CROSSES THIS BOUNDARY IN EITHER DIRECTION. The request carries the eight
      // declared members and no status of any kind, so there is nothing for the screen to
      // misread as a zero-means-success signal.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 10 — COMPONENT IDENTITY AND THE SENTINEL DISCIPLINE
  // ---------------------------------------------------------------------------------------------------
  //
  // These are cheap and they catch a class of bug that produces no compilation error and no
  // run-time exception — only a screen that quietly does the wrong thing.

  describe('the component identity that other files depend on', () => {
    /**
     * The compiled declaration, read through the framework's own reflection API.
     *
     * ⚠ `fixture.nativeElement` is NOT the answer here, and measurement proved it: the testing
     * harness mounts a component under a generic `div` host of its own making, so the host tag
     * reports `div` no matter what the component declares. The selector must therefore be read
     * from the declaration, which is exactly what `reflectComponentType` is published for.
     */
    const mirror = reflectComponentType(UserFormComponent);

    it('is named and selected exactly as the route file imports it', () => {
      // ⚠ BOTH ARE EXTERNALLY FIXED. `features/user/user.routes.ts` maps BOTH `{ path: 'new' }`
      // and `{ path: ':userId' }` to
      // `import('./user-form/user-form.component').then((m) => m.UserFormComponent)`, so a
      // renamed export takes out the whole `/users/new` plus `/users/:userId` subtree with a
      // 404 and no other symptom.
      expect(UserFormComponent.name).toBe('UserFormComponent');

      expect(mirror).withContext('the class really is a component').not.toBeNull();
      expect(mirror?.selector).withContext('the selector other templates use').toBe('app-user-form');
      expect(mirror?.isStandalone).withContext('standalone, so it needs no module').toBeTrue();
    });

    it('declares the input as exactly `userId`, which the router binds by name', () => {
      // ⚠ `app.config.ts` enables `withComponentInputBinding()`, which matches a route
      // parameter to an input OF THE SAME NAME. Renaming this to `id` or `userID` breaks the
      // binding SILENTLY: no compilation error, no exception, just an input that stays
      // undefined so every visit looks like a create.
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
      // The compiled definition records the declared strategy as one boolean, computed by the
      // framework as `changeDetection === ChangeDetectionStrategy.OnPush`. Reading it asserts
      // the DECLARATION, which is what the requirement is about — a behavioural probe cannot
      // distinguish the two strategies on a view whose every binding is signal-driven, because
      // a signal read marks the view dirty under either one.
      const definition: unknown = (UserFormComponent as unknown as Record<string, unknown>)['ɵcmp'];

      expect(typeof definition).withContext('the class was compiled as a component').toBe('object');

      const declared: unknown = (definition as Record<string, unknown>)['onPush'];

      expect(declared).withContext('ChangeDetectionStrategy.OnPush is declared').toBeTrue();
    });
  });

  describe('identifiers, tested for presence and never for truth', () => {
    // ⚠⚠ MEASURED SEEDS, AND THEY COLLIDE WITH THE NULL MARKER.
    // `Users.UserID IDENTITY (1, 1)` · `Portals.PortalID IDENTITY (-1, 1)` ·
    // `Roles.RoleID`/`Tabs.TabID`/`Modules.ModuleID IDENTITY (0, 1)`
    // (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L77, L98,
    // L115) while `Library/Components/Shared/Null.vb` defines `NullInteger` with a body that is
    // literally `Return -1`. So `-1` names BOTH the first portal ever created AND "no integer",
    // and `0` is a legitimate identifier in three of the five tables.
    //
    // Consequently no identifier anywhere — not in the component, not in these helpers — is
    // subjected to a truthiness test, a positive-value test, a comparison against the sentinel
    // or a coalescing default.

    it('treats MINUS ONE as a real account rather than as an absence', () => {
      // The null marker's own value, arriving as a route parameter. The screen must read it as
      // an identifier, because a screen that treated it as "absent" would silently offer to
      // CREATE an account while the operator believed they were editing one.
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
      // ⚠ ZERO IS NOT NATURALLY OCCURRING FOR THIS TABLE. `Users.UserID` seeds at ONE, so no
      // real account carries nought and this is a DEFENSIVE test of the discipline rather than
      // a live scenario. It is implemented anyway, because the sibling tables DO seed at nought
      // and the screen must not encode an assumption that happens to hold only here.
      create('0');

      expectRequest('GET', userUrl(0), 'zero is an address, not an absence').flush(
        envelope(account(0)),
      );
      fixture.detectChanges();

      expect(button(UPDATE_SUBMIT_LABEL)).not.toBeUndefined();
      expectNoListingReRead();
    });

    it('offers no form at all when the parameter is not an identifier', () => {
      /*
       * ⚠ THE OPTION STRICT ASYMMETRY MADE EXPLICIT. The legacy admin code-behinds compiled with
       * `<compilation debug="false" strict="false">` (`Website/release.config` L125) — that is Option
       * Strict OFF — so a late-bound narrowing of a non-numeric string to an integer would have
       * yielded ZERO without complaint, and the page would have read a real row. Here the coercion is
       * explicit and no identifier is invented, which is the half of this rule that always held.
       *
       * ⚠ THIS CASE USED TO ASSERT THE OTHER HALF WRONGLY. It required the CREATE submit to be
       * present and called that "failing closed". It is the opposite: an address naming nothing was
       * answered with the live add-account form, nine of its ten controls enabled and Authorize
       * already ticked, so the operator was invited to create an account by a URL that looked like a
       * request to edit one. Reading nothing is necessary but not sufficient — it is precisely what
       * made the fall-through silent, since the server never got the chance to disagree.
       *
       * Absence of a parameter still selects creation; that is `/users/new` and is covered elsewhere.
       * This case is about a parameter that is present and unusable, which is a third state.
       */
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

      // ⚠ DEFECT, ANNOTATED AND NOT REPAIRED. The markup declares `chkRandom checked="True"` at
      // `User.ascx` L35 and the code-behind then assigns `chkRandom.Checked = False` on every
      // non-postback at `User.ascx.vb` L261. The two contradict each other and the code-behind
      // wins at run time, so the OBSERVABLE opening state is UNCHECKED. The behaviour is
      // reproduced; the contradiction is recorded rather than resolved in the markup's favour.
      expect(field<HTMLInputElement>(CONTROL_ID.randomPassword).checked)
        .withContext('generation opens UNCHECKED, matching the code-behind and not the markup')
        .toBeFalse();
    });

    it('offers the notification switch disabled rather than ticked', () => {
      create();

      const notify = field<HTMLInputElement>(CONTROL_ID.notify);

      // MIGRATION — DIVERGENCE FROM THE MEASURED MARKUP, WITH ITS CAUSE. `chkNotify` carried
      // `checked="True"` at `User.ascx` L25, but `CreateUserRequest` declares exactly EIGHT
      // members — username, firstName, lastName, displayName, email, password, confirmPassword,
      // authorize — and `notify` is NOT one of them. There is no field on the wire to carry the
      // choice, so a ticked box would be a false statement about what will happen. The control
      // is retained and rendered DISABLED and unticked instead.
      expect(notify.disabled).withContext('no wire field exists to carry the choice').toBeTrue();
      expect(notify.checked).toBeFalse();

      // ⚠ THE REASON IS NOT PRINTED INLINE, and measurement is what settled that: the shared
      // field reveals help on demand, reproducing the legacy help BUTTON at
      // `Website/controls/helpbuttoncontrol.ascx`. So the sentence is absent until the
      // affordance is operated, and the case that operates it lives with the creation cases
      // rather than being duplicated here.
      expect(host().textContent ?? '')
        .withContext('help is revealed on demand, exactly like every other field')
        .not.toContain(NOTIFY_UNAVAILABLE_ADVISORY);
      expect(notify.closest('.form-field')?.querySelector('.form-field__help-toggle'))
        .withContext('but the affordance that reveals it is offered')
        .not.toBeNull();
    });

    it('returns every control to its initial value on a reset, never to null', () => {
      // ⚠ THIS IS THE `nonNullable` PROOF, AND IT IS DRIVEN THROUGH THE PUBLIC INPUT. A control
      // built WITHOUT `nonNullable` resets to `null`; a checkbox bound to `null` renders
      // UNCHECKED. The component resets the form when the resolved identifier becomes
      // undefined, so withdrawing the input performs a real reset and the switches must come
      // back at TRUE and FALSE respectively rather than both at null-shaped unchecked.
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

      // ⚠⚠ AND IT IS NOT A VALIDATOR. The legacy declared no maximum-length VALIDATOR at all —
      // `Website/admin/Users` contains zero `asp:RequiredFieldValidator` and zero
      // `asp:RegularExpressionValidator`, and the only validator in this folder is the single
      // `asp:CustomValidator valPassword` at `User.ascx` L59-L61, declared with NO
      // `ControlToValidate` and NO `ErrorMessage`. The browser silently TRUNCATED over-long
      // input rather than reporting an error, so turning the ceiling into a validator would
      // convert a silent truncation into a visible rejection.
      //
      // Proven by behaviour: a credential far longer than the attribute allows still submits,
      // because nothing validates its length on this side.
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
      // The screen reads the account from the store's published selection. That signal is
      // exposed through `asReadonly()`, so a template or a child cannot write the selection
      // back — which is what stops a view from becoming a second source of truth.
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
      // ⚠ THE SENTINEL SURVIVES ON THE WIRE. `Library/Components/Shared/Null.vb` defines
      // `NullDate` as `DateTime.MinValue` and `NullString` as the EMPTY STRING rather than as
      // null, and the API serialises with its ignore condition set to never — so these members
      // arrive PRESENT AND EMPTY rather than omitted.
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

      // ⚠ PARITY, NOT AN IMPROVEMENT. The legacy `DisplayDate` already returned the empty string
      // for the sentinel, so an empty cell is what an operator saw. `01/01/0001` would be a
      // REGRESSION — a date nobody entered, presented as though somebody had.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 13 — WHAT THIS SCREEN MUST NOT DO
  // ---------------------------------------------------------------------------------------------------
  //
  // Negative assertions, each tied to a measured reason. Several record that a legacy
  // affordance was BEHAVIOUR-PRESERVINGLY dropped rather than reduced, which is a different
  // claim from "we left it out" and the distinction is the point.

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

      // The legacy declared `dnn:captchacontrol ctlCaptcha` inside the password table at
      // `User.ascx` L67. The control is one of the 102 excluded `Library/Controls` files, so
      // there is nothing to render and no field to post.
      //
      // ⚠ AND THE PROTECTION IS NOT SIMPLY GONE. The compensating control lives on the server
      // and it covers THIS form's own write: the global limiter classifies a request as
      // credential-bearing from the `[CredentialEndpoint]` marker on the action, and
      // `UsersController.CreateAsync` carries it, so account creation draws the credential
      // budget per calling address and is answered 429 once the allowance is spent.
      //
      // MIGRATION — DEFECT 8, ANNOTATED. The legacy label for that row read
      // `text="Password:"` at `User.ascx` L64, which was MASKED at run time because
      // `plCaptcha.Text` in the local resources reads `Security Code:` and a `dnn:label` with no
      // explicit resource key falls back on its own control identifier. The mislabelling was
      // therefore invisible in the running application. It is recorded, not carried.
      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('captcha');
      expect(text).not.toContain('security code');
      expect(controlNames().some((value) => value.toLowerCase().includes('captcha'))).toBeFalse();
      expect(queryAll('img')).withContext('no challenge image').toHaveSize(0);
    });

    it('offers no password question and no password answer', () => {
      create();

      // ⚠ THIS IS BEHAVIOUR-PRESERVING, NOT A REDUCTION, AND THE DISTINCTION IS MEASURED. Both
      // rows carried `visible="false"` at `User.ascx` L52 and L56 and were revealed only when
      // `MembershipProviderConfig.RequiresQuestionAndAnswer` was true — and
      // `Website/release.config` L241 sets `requiresQuestionAndAnswer="false"`. The rows
      // therefore NEVER RENDERED in the measured installation, and the legacy branch that read
      // them (`User.ascx.vb` L168) was unreachable. Omitting unreachable code changes nothing an
      // operator could observe. No endpoint carries either value either.
      const text = (host().textContent ?? '').toLowerCase();

      expect(text).not.toContain('password question');
      expect(text).not.toContain('password answer');

      const names = controlNames().map((value) => value.toLowerCase());

      expect(names.some((value) => value.includes('question'))).toBeFalse();
      expect(names.some((value) => value.includes('answer'))).toBeFalse();
    });

    it('offers no way to retrieve an existing password', () => {
      arriveEditing(account(7));

      // The legacy membership provider was registered with `enablePasswordRetrieval="true"` and
      // a reversible password format, which made every stored credential recoverable. The
      // successor stores a one-way hash, so retrieval is not withheld — it is IMPOSSIBLE.
      //
      // ⚠ RESET IS NOT RETRIEVAL and the two must not be conflated: `enablePasswordReset="true"`
      // IS carried forward, on the sibling credential screen. What is gone is reading a
      // credential back out.
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

      // The users-online subsystem is out of scope and no endpoint reports it, so the member
      // arrives on the contract and is deliberately not painted. Asserted with the flag SET, so
      // the case would fail if an indicator were added later.
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

      // ⚠ TWO CLOSED VOCABULARIES THAT MUST NOT BE CONFLATED: the authorisation POLICY names and
      // the persisted PERMISSION KEYS. Neither appears here. The route is already gated, that
      // gate is ADVISORY, and the server's 403 is authoritative — which is exactly why the
      // refusal cases route a 403 through the notification channel rather than deciding
      // anything locally.
      const markup = host().innerHTML;

      expect(markup).not.toContain('hasPermission');
      expect(markup).not.toContain('PortalAdministrator');

      const text = host().textContent ?? '';

      expect(text).not.toContain('VIEW');
      expect(text).not.toContain('EDIT');
    });

    it('renders no bare zero for an account allowance', () => {
      // ⚠ ZERO MEANS UNLIMITED AND MINUS ONE MEANS NOT SET for the tenant's account allowance,
      // so printing either as a number would tell an operator the opposite of the truth. This
      // screen states the allowance only as the measured refusal sentence, which carries no
      // figure at all.
      create();
      fillCreationForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', USERS_URL).flush(
        problem('user.quota_exceeded', 403, 'The account allowance for this site is spent.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const text = host().textContent ?? '';

      // The measured sentence is `ExceededUserQuota.Text` from
      // `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx`, which capitalises the
      // word; the comparison is case-insensitive so it cannot pass on the wrong casing either.
      expect(text.toLowerCase()).toContain(EXCEEDED_USER_QUOTA_MESSAGE_FRAGMENT.toLowerCase());
      expect(text).toContain('User Quota');

      // ⚠ AND NO FIGURE. The sentence names the allowance without printing it, so neither
      // sentinel can be mistaken for a limit.
      expect(text).not.toMatch(/quota[^.]*(^|\s)(0|-1)(\s|$)/i);
      expectNoListingReRead();
    });

    it('lays the screen out without a single table element', () => {
      arriveEditing(account(7));

      // The legacy `tblAddUser`, `tblPassword` and the `pnlUser` design table were all LAYOUT
      // tables rather than data grids — `tblPassword` even carried
      // `summary="Password Management"`, which is a layout summary and not a caption. Layout
      // tables are replaced by grid styling; a real data grid would still be a table.
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

      // ⚠ THE REGION BELONGS TO THE SHARED BANNER, AND THIS SUITE ASSERTS ITS PRESENCE RATHER
      // THAN ADDING A COMPETING ONE. Two live regions announcing the same text is worse than
      // one, because a screen reader reads both.
      const live = query<HTMLElement>('[aria-live]');

      expect(live).withContext('the banner mounted with a live region').not.toBeNull();
      expect(live?.getAttribute('aria-live')).toBe('assertive');
      expect(query('[role="alert"]')).not.toBeNull();
      expect(queryAll('[aria-live]')).withContext('exactly one live region').toHaveSize(1);
      expectNoListingReRead();
    });

    it('strips the legacy break prefix and announces plain text', () => {
      // ⚠ MEASURED DEFECT. `User.ascx.vb` L187 built its message as
      // `"<br/>" + UserController.GetUserCreateStatus(createStatus)`, prepending markup to what
      // was otherwise a sentence. The prefix is inconsistent across the legacy tree and exists
      // in both spellings, so the stripping is total rather than keyed to one form. It is owned
      // by `core/utils/form-errors.util.ts`; this case asserts the OUTCOME.
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
      // ⚠ RESOURCE AND SERVER TEXT IS UNTRUSTED MARKUP BY MEASUREMENT — 76 values across the
      // in-scope resource files contain a raw HTML tag, including a script tag four times. A
      // message is text and is bound as text; nothing here is bound as trusted markup and no
      // sanitiser is involved, because the framework's default interpolation already escapes.
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
      // `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx` reads
      // "...unique Email Address.  The Email Address you entered..." with TWO spaces after the
      // full stop, and it is asserted verbatim. Collapsing it would be an unrequested edit to
      // wording an operator recognises.
      expect(EMAIL_CONFLICT_MESSAGE).toContain('Email Address.  The Email Address');
      expect(EMAIL_CONFLICT_MESSAGE).not.toContain('Email Address. The Email Address');
      expect(EMAIL_CONFLICT_MESSAGE).toBe(
        'This portal requires a unique Email Address.  The Email Address you entered has ' +
          'already been used.',
      );
    });

    it('enforces no address uniqueness of its own and lets the server refuse', () => {
      arriveEditing(account(7));

      // ⚠ `requiresUniqueEmail="false"` at `Website/release.config` L244 — the legacy did NOT
      // enforce uniqueness, so authoring a client-side rule here would be a NEW rule rather
      // than parity. An address already in use therefore submits cleanly and is refused by the
      // server, which is the only authority on it.
      type(CONTROL_ID.email, 'taken@example.test');

      expect(fieldErrors().join(' ')).withContext('nothing was refused locally').not.toContain('already');

      press(UPDATE_SUBMIT_LABEL);

      const write = expectRequest('PUT', userUrl(7), 'the duplicate address was sent');

      write.flush(problem('user.create.duplicate_email', 409, 'That address is already in use.'), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      // ⚠ AND THE MEASURED SENTENCE IS NOT WHAT REACHES THE OPERATOR ON THIS PATH. The refusal
      // paragraph that carries it is rendered only while the form is WITHHELD, and the
      // withholding rule names the permission and not-found statuses alone — so a conflict is
      // reported by the banner in the server's own words and the form stays usable, which is
      // the right answer because a duplicate address is corrected right here. The measured
      // sentence is recorded as UNREACHABLE for a 409 rather than asserted as shown.
      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'That address is already in use.',
      );
      expect(host().textContent ?? '').not.toContain(EMAIL_CONFLICT_MESSAGE);
      expect(query(`#${CONTROL_ID.email}`)).withContext('the form stays usable').not.toBeNull();
      expectNoListingReRead();
    });
  });
});
