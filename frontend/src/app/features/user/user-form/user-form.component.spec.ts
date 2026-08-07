import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { type ComponentRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  CreateUserRequest,
  UpdateUserRequest,
  UserDetail,
  UserListItem,
} from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import {
  AUTHORIZE_MAIL_ADVISORY,
  NOTIFY_UNAVAILABLE_ADVISORY,
  UserFormComponent,
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

  const USER_AUTHORIZED_MESSAGE = 'User successfully Authorized';
  const USER_UNAUTHORIZED_MESSAGE = 'User successfully Un-Authorized';
  const USER_UNLOCKED_MESSAGE = 'User successfully Unlocked';
  const PASSWORD_CHANGE_REQUIRED_MESSAGE = 'This user must change their password at next login';
  const USER_UPDATED_MESSAGE = 'User account updated';
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
      correlationId: 'f0e7d1b2-3c45-4a6b-8c9d-0e1f2a3b4c5d',
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

    it('stays in creation mode for a route parameter that is not a whole number', () => {
      create('not-a-number');

      expect(httpMock.match(() => true)).withContext('nothing is read').toHaveSize(0);
      expect(query('app-page-header')?.textContent ?? '').toContain(CREATE_MODE_TITLE);
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
      expect(links).toContain('/roles');
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
      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users']);
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users']);
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
      arriveEditing(account(7));

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

    it('withholds all four while a refusal has withheld the form', () => {
      arriveEditing(account(7));

      type(CONTROL_ID.firstName, 'Augusta');
      press(UPDATE_SUBMIT_LABEL);
      expectRequest('PUT', userUrl(7)).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(button(AUTHORIZE_LABEL)?.disabled).toBeTrue();
      expect(button(UNAUTHORIZE_LABEL)?.disabled).toBeTrue();
      expect(button(UNLOCK_LABEL)?.disabled).toBeTrue();
      expect(button(FORCE_PASSWORD_LABEL)?.disabled).toBeTrue();
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(['/users']);
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
      arriveEditing(account(7, { isSuperUser: true }));

      // DELIBERATELY STRICTER THAN THE LEGACY, which withheld it only when the superuser was
      // also the caller. Erring towards withholding is the safe direction: the action is
      // destructive, the server refuses it anyway, and offering a button whose only possible
      // outcome is a refusal is worse than not offering it.
      expect(button(DELETE_LABEL)).withContext('withheld').toBeUndefined();
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
});
