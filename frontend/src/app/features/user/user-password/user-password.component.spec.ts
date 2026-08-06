import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { type ComponentRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import type { AuthSession, CurrentUser } from '../../../core/models/auth.model';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ChangePasswordRequest, UserDetail } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { UserStore } from '../../../core/state/user.store';
import { UserPasswordComponent } from './user-password.component';

/**
 * Specification for the credential screen.
 *
 * ONE form and ONE command, which reach TWO different endpoints depending on who is looking at
 * the screen — and that branch is the whole difficulty. Six invariants are named up front
 * because each one, got wrong, produces a screen that looks right and is not:
 *
 *   - ⚠ THE OPERATION IS CHOSEN BY WHO THE CALLER IS, NOT BY A CONTROL. Acting on one's own
 *     account is a CHANGE, which proves the credential in force; acting on somebody else's is a
 *     RESET, which proves nothing about the old value and is therefore confirmed first. Two
 *     legacy buttons became one command that names its own consequence.
 *   - ⚠ THE CURRENT-CREDENTIAL RULE IS DYNAMIC. It is added to and removed from the control at
 *     run time from the caller's authority, so a case that seeds no identity is exercising the
 *     non-administrator arm whether it means to or not.
 *   - ⚠ THE PRE-FLIGHT ORDER IS LOAD-BEARING: mismatch, then policy, then the missing current
 *     credential, then the not-different rule — and exactly ONE message is published, because
 *     the legacy sequence exited at the first arm that matched.
 *   - ⚠ NO CREDENTIAL MAY REACH THE DOCUMENT, THE CONSOLE OR A NOTIFICATION. Every input is
 *     typed `password`, the form is cleared on success so no value outlives the request that
 *     used it, and the success wording names no value.
 *   - ⚠ IDENTIFIERS ZERO AND MINUS ONE ARE BOTH LEGITIMATE and are parsed rather than inspected.
 *     The route value arrives as TEXT and is rejected before parsing if it is not wholly
 *     numeric, so `'12abc'` is refused rather than read as twelve.
 *   - ⚠ A SUCCESSFUL WRITE RE-READS THE ACCOUNT AND NOTHING ELSE. The listing is untouched,
 *     because nothing on it changed.
 */
describe('UserPasswordComponent', () => {
  let fixture: ComponentFixture<UserPasswordComponent>;
  let reference: ComponentRef<UserPasswordComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let successSpy: jasmine.Spy;
  let warningSpy: jasmine.Spy;
  let errorSpy: jasmine.Spy;
  let consoleSpies: readonly jasmine.Spy[];

  // ---------------------------------------------------------------------------------------------------
  // ADDRESSES
  // ---------------------------------------------------------------------------------------------------

  const USERS_URL = '/api/v1/users';

  function userUrl(userId: number): string {
    return `${USERS_URL}/${userId}`;
  }

  function changeUrl(userId: number): string {
    return `${userUrl(userId)}/password`;
  }

  function resetUrl(userId: number): string {
    return `${userUrl(userId)}/password-reset`;
  }

  // ---------------------------------------------------------------------------------------------------
  // MEASURED WORDING
  // ---------------------------------------------------------------------------------------------------

  const MANAGE_PASSWORD_TITLE = 'Manage Password';
  const CHANGE_PASSWORD_TEXT = 'Change Password';
  const RESET_PASSWORD_TEXT = 'Reset Password';
  const PASSWORD_CHANGED_TEXT = 'The password has been reset.';
  const NO_EXPIRY_TEXT = 'Password does not Expire';
  const FORCED_EXPIRY_TEXT =
    'The Portal Administrator has required you to change your password, before you can log in.';
  const LAST_CHANGED_LABEL = 'Password Last Changed';
  const EXPIRES_LABEL = 'Password Expires';
  const CURRENT_PASSWORD_LABEL = 'Current Password';
  const NEW_PASSWORD_LABEL = 'New Password';
  const CONFIRM_PASSWORD_LABEL = 'Confirm Password';

  const USER_CHANGE_HELP =
    'In order to change your password, you will need to provide your current password, ' +
    'as well as your new password and a confirmation of your new password.';
  const ADMIN_CHANGE_HELP =
    'To change a password for this user enter the new password and confirm the entry by ' +
    'typing it again.';
  const ADMIN_RESET_HELP =
    'You can reset the password for this user.  The replacement password must be ' +
    'supplied rather than randomly generated.';

  /** The measured policy sentence, with the double spaces the resource file carries. */
  const POLICY_MESSAGE =
    'The password specified is invalid.  Please specify a valid password.  Passwords ' +
    'must be at least 7 characters in length and contain at least 0 non-alphanumeric characters.';

  const MISMATCH_MESSAGE = 'The Password and Confirmation Passwords do not match';
  const MISSING_CURRENT_MESSAGE =
    'You must provide your current password in order to change the password.';
  const NOT_DIFFERENT_MESSAGE =
    'The new password is the same as the old password. Please enter a different password';

  /** Control identifiers, exactly as the template writes them. */
  const CONTROL_ID = Object.freeze({
    currentPassword: 'currentPassword',
    newPassword: 'newPassword',
    confirmPassword: 'confirmPassword',
  });

  /** A credential long enough to clear the measured minimum. */
  const VALID_PASSWORD = 'Repl4cement';

  /** A second one, for the not-different rule. */
  const OTHER_PASSWORD = 'An0therOne';

  /**
   * The reason phrase the API publishes as a problem `title`, keyed by status.
   *
   * ⚠ NOT FREE TEXT. Every refusal reaches the wire through one shared problem factory.
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
      correlationId: 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
    };

    return errors === undefined ? document : { ...document, errors };
  }

  // ---------------------------------------------------------------------------------------------------
  // FIXTURES
  // ---------------------------------------------------------------------------------------------------

  /**
   * One account as the server reports it.
   *
   * ⚠ THE DEFAULT IDENTIFIER IS ZERO, so that the screen's parse — rather than a truthiness
   * test — is what every case exercises.
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

  function caller(userId: number, roles: readonly string[], isSuperUser = false): CurrentUser {
    return {
      userId,
      portalId: -1,
      portalName: 'Baseline Portal',
      username: 'caller',
      displayName: 'The Caller',
      email: 'caller@example.test',
      isSuperUser,
      isPortalAdministrator: false,
      roles,
      permissions: [],
    };
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /**
   * Seats an identity for the caller.
   *
   * ⚠ SEEDED THROUGH THE TOKEN STORE RATHER THAN THROUGH A SIGN-IN, because the authority store
   * falls back to the stored session when it has fetched no identity of its own. Driving a
   * sign-in here would add two requests to every case and would specify the sign-in flow, which
   * has its own specification.
   */
  function seatIdentity(user: CurrentUser): void {
    const session: AuthSession = {
      accessToken: 'header.payload.signature',
      expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(),
      refreshToken: 'refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user,
    };

    TestBed.inject(TokenStorageService).store(session);
  }

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    await TestBed.configureTestingModule({
      imports: [UserPasswordComponent],
      // The account store is listed so each case gets its own instance; it is declared
      // `providedIn: 'root'`, so without this every case would share one selection.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), UserStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(TokenStorageService).clear();

    const notifications = TestBed.inject(NotificationService);

    notifySpy = spyOn(notifications, 'notify').and.callThrough();
    successSpy = spyOn(notifications, 'success').and.callThrough();
    warningSpy = spyOn(notifications, 'warning').and.callThrough();
    errorSpy = spyOn(notifications, 'error').and.callThrough();

    // ⚠ THE CONSOLE IS WATCHED, NOT SILENCED. A credential must not reach it, and the only way to
    // assert that is to record everything written to it.
    consoleSpies = [
      spyOn(console, 'log').and.stub(),
      spyOn(console, 'info').and.stub(),
      spyOn(console, 'warn').and.stub(),
      spyOn(console, 'error').and.stub(),
      spyOn(console, 'debug').and.stub(),
    ];
  });

  afterEach(() => {
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
   * string and the screen's own transform is what turns it into a key. Passing a number would
   * take a different arm of that transform and would leave the real path untested.
   */
  function create(userId: string): void {
    fixture = TestBed.createComponent(UserPasswordComponent);
    reference = fixture.componentRef;
    reference.setInput('userId', userId);
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

  /** Mounts and answers the account read. */
  function arrive(held: UserDetail = account()): void {
    create(String(held.userId));
    expectRequest('GET', userUrl(held.userId), 'the account read').flush(envelope(held));
    fixture.detectChanges();
  }

  /** Mounts as the account holder themselves, which is the CHANGE path. */
  function arriveAsSelf(held: UserDetail = account(7)): void {
    seatIdentity(caller(held.userId, ['Registered Users']));
    arrive(held);
  }

  /** Mounts as a tenant administrator acting on somebody else, which is the RESET path. */
  function arriveAsAdministrator(held: UserDetail = account(7)): void {
    seatIdentity(caller(99, ['Administrators']));
    arrive(held);
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

  /** Types into a credential control. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    control.dispatchEvent(new Event('blur'));
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
   * ⚠ THE FORM COMMAND AND THE DIALOGUE COMMAND SHARE ONE WORDING — both read "Reset Password" —
   * and the form's comes first in document order, so an unscoped lookup re-opens the question
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

  /** Every string handed to any notification channel or to the console. */
  function everythingSpoken(): string {
    const spoken: string[] = [
      ...successSpy.calls.allArgs().map((args) => String(args[0])),
      ...warningSpy.calls.allArgs().map((args) => String(args[0])),
      ...errorSpy.calls.allArgs().map((args) => String(args[0])),
      ...notifySpy.calls.allArgs().map((args) => args.map((value) => String(value)).join(' ')),
    ];

    for (const spy of consoleSpies) {
      for (const call of spy.calls.allArgs()) {
        spoken.push(call.map((value) => String(value)).join(' '));
      }
    }

    return spoken.join(' | ');
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL AND THE ROUTE KEY
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the account named by the route, from its own relative address', () => {
      create('7');

      const read = expectRequest('GET', userUrl(7), 'the account read');

      expect(read.request.params.keys()).withContext('no query on a single-resource read').toHaveSize(0);
      expect(read.request.body).toBeNull();

      read.flush(envelope(account(7)));
      fixture.detectChanges();

      expect(query('.user-password')).withContext('the screen is drawn').not.toBeNull();
    });

    it('reads account zero, which is a legitimate key and not an absence', () => {
      create('0');

      // ⚠ NEVER A TRUTHINESS TEST. `'0'` parses to zero, and zero is a real account key.
      const read = expectRequest('GET', userUrl(0));

      expect(read.request.url).toBe('/api/v1/users/0');

      read.flush(envelope(account(0)));
      fixture.detectChanges();
    });

    it('reads account minus one, because a negative key stays expressible', () => {
      create('-1');

      // The legacy codebase used minus one as its marker for a missing integer AND seeded a real
      // table's identity there, so this screen refuses to interpret the value at all.
      const read = expectRequest('GET', userUrl(-1));

      expect(read.request.url).toBe('/api/v1/users/-1');

      read.flush(envelope(account(-1)));
      fixture.detectChanges();
    });

    it('reads nothing for a route value that is not wholly numeric', () => {
      create('12abc');

      // ⚠ REJECTED BEFORE PARSING RATHER THAN AFTER: the parser stops at the first character it
      // cannot use and would have read this as twelve — an account nobody asked for.
      expect(httpMock.match(() => true)).withContext('nothing is read').toHaveSize(0);
      expect(query('.user-password')).withContext('and nothing is drawn').toBeNull();
    });

    it('reads nothing for an empty or blank route value', () => {
      create('   ');

      expect(httpMock.match(() => true)).toHaveSize(0);
      expect(query('.user-password')).toBeNull();
    });

    it('draws nothing at all while the read is outstanding, so no empty state can flash', () => {
      create('7');

      const read = expectRequest('GET', userUrl(7));

      expect(query('.user-password')).withContext('nothing yet').toBeNull();
      expect(query('app-loading-spinner')).withContext('the wait is drawn').not.toBeNull();

      read.flush(envelope(account(7)));
      fixture.detectChanges();

      expect(query('app-loading-spinner')).withContext('and replaced').toBeNull();
    });

    it('draws no form and invents no wording when the read reports no such account', () => {
      create('7');

      expectRequest('GET', userUrl(7)).flush(envelope(null));
      fixture.detectChanges();

      // NO WORDING IS INVENTED FOR THIS STATE: the resource files carry none for this screen and
      // the shared empty-state affordance is deliberately not among its imports.
      expect(query('.user-password')).toBeNull();
      expect(query('form.user-password__form')).toBeNull();
    });

    it('re-reads when the route names a different account', () => {
      arrive(account(7));

      reference.setInput('userId', '8');
      fixture.detectChanges();

      expectRequest('GET', userUrl(8), 'the second account read').flush(envelope(account(8)));
      fixture.detectChanges();

      expect(query('app-page-header')?.textContent ?? '').toContain(MANAGE_PASSWORD_TITLE);
    });

    it('paints the two read-only rows from the record', () => {
      arriveAsSelf(account(7, { lastPasswordChangeDate: '2024-02-01T10:00:00Z' }));

      const markup = host().textContent ?? '';

      expect(markup).toContain(LAST_CHANGED_LABEL);
      expect(markup).toContain(EXPIRES_LABEL);
      // False is DATA here rather than an absence: the contract declares the flag a plain
      // boolean precisely because the legacy absent-marker for a boolean WAS false.
      expect(markup).toContain(NO_EXPIRY_TEXT);
      expect(markup).not.toContain('null');
      expect(markup).not.toContain('undefined');
    });

    it('states the forced expiry when the account must change its credential', () => {
      arriveAsSelf(account(7, { mustChangePassword: true }));

      expect(host().textContent ?? '').toContain(FORCED_EXPIRY_TEXT);
      expect(host().textContent ?? '').not.toContain(NO_EXPIRY_TEXT);
    });

    it('renders an absent last-changed date as nothing rather than as a marker', () => {
      arriveAsSelf(account(7, { lastPasswordChangeDate: null }));

      const value = queryAll<Element>('.user-password__value')[0];

      expect((value?.textContent ?? '').trim()).withContext('empty, not "null"').toBe('');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — WHICH OPERATION THE SCREEN PLANS
  // ---------------------------------------------------------------------------------------------------

  describe('choosing between changing and resetting', () => {
    it('plans a change, and asks for the credential in force, for the account holder', () => {
      arriveAsSelf(account(7));

      // ⚠ THE OPERATION IS CHOSEN BY WHO IS LOOKING, NOT BY A CONTROL.
      expect(button(CHANGE_PASSWORD_TEXT)).withContext('the change command').not.toBeUndefined();
      expect(button(RESET_PASSWORD_TEXT)).withContext('and not the reset command').toBeUndefined();
      expect(query(`#${CONTROL_ID.currentPassword}`)).withContext('the credential in force').not.toBeNull();
      expect(host().textContent ?? '').toContain(USER_CHANGE_HELP);
      // The reset guidance belongs to an administrator acting on somebody else.
      expect(host().textContent ?? '').not.toContain(ADMIN_RESET_HELP);
    });

    it('plans a reset, and asks for no credential in force, for an administrator', () => {
      arriveAsAdministrator(account(7));

      expect(button(RESET_PASSWORD_TEXT)).withContext('the reset command').not.toBeUndefined();
      expect(button(CHANGE_PASSWORD_TEXT)).toBeUndefined();
      // A reset proves nothing about the old value, so there is nothing to ask for.
      expect(query(`#${CONTROL_ID.currentPassword}`)).withContext('withheld').toBeNull();
      expect(host().textContent ?? '').toContain(ADMIN_CHANGE_HELP);
      expect(host().textContent ?? '').toContain(ADMIN_RESET_HELP);
    });

    it('plans a change for an administrator editing their OWN account', () => {
      const held = account(7);

      seatIdentity(caller(held.userId, ['Administrators']));
      arrive(held);

      // Authority does not exempt somebody from proving their own credential.
      expect(button(CHANGE_PASSWORD_TEXT)).not.toBeUndefined();
      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('shown, because the caller is the holder')
        .not.toBeNull();
      expect(host().textContent ?? '').not.toContain(ADMIN_RESET_HELP);
    });

    it('treats an installation superuser as an administrator', () => {
      seatIdentity(caller(99, [], true));
      arrive(account(7));

      expect(button(RESET_PASSWORD_TEXT)).not.toBeUndefined();
      expect(query(`#${CONTROL_ID.currentPassword}`)).toBeNull();
    });

    it('claims no authority and asks for the credential in force when no identity is seated', () => {
      arrive(account(7));

      // ⚠ THE TWO DECISIONS ARE DRAWN FROM DIFFERENT FACTS AND DIVERGE HERE, deliberately.
      // Nobody is known, so the caller is not the holder and the operation planned is the
      // RESET - while the current-credential rule is keyed on authority NOT being proven, so it
      // still applies and the control is still shown. Erring towards demanding more is the safe
      // direction; the server is the authority and refuses a reset the caller may not perform.
      expect(button(RESET_PASSWORD_TEXT)).withContext('nobody is the holder').not.toBeUndefined();
      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('authority is not proven either')
        .not.toBeNull();
      expect(host().textContent ?? '').toContain(USER_CHANGE_HELP);
      // No reset guidance, because that arm is keyed on proven authority.
      expect(host().textContent ?? '').not.toContain(ADMIN_RESET_HELP);
    });

    it('titles the screen with the account only for an administrator', () => {
      arriveAsAdministrator(account(7, { username: 'ada.lovelace' }));

      expect(query('app-page-header')?.textContent ?? '').toContain(
        `${MANAGE_PASSWORD_TITLE} - ada.lovelace (Id: 7)`,
      );

      fixture.destroy();
      TestBed.inject(TokenStorageService).clear();
      arriveAsSelf(account(7));

      // An account holder is looking at their own credential and does not need to be told whose.
      expect(query('app-page-header')?.textContent?.trim()).toBe(MANAGE_PASSWORD_TITLE);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE PRE-FLIGHT RULES, AND THEIR ORDER
  // ---------------------------------------------------------------------------------------------------

  describe('the pre-flight rules', () => {
    it('reports the mismatch first, before it judges the policy', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, 'short');
      type(CONTROL_ID.confirmPassword, 'shorter');
      press(CHANGE_PASSWORD_TEXT);

      // ⚠ EXACTLY ONE MESSAGE. The legacy sequence exited at the first arm that matched, so a
      // mismatch of two values that are both too short reports the mismatch and nothing else.
      expect(fieldErrors()).toEqual([MISMATCH_MESSAGE]);
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
    });

    it('reports the policy when the pair matches but is too short', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, 'short1');
      type(CONTROL_ID.confirmPassword, 'short1');
      press(CHANGE_PASSWORD_TEXT);

      // Six characters against a measured minimum of seven. Both the required rule and the
      // length rule resolve to this one sentence, because the legacy single call failed
      // identically for an absent credential and for a short one.
      expect(fieldErrors()).toEqual([POLICY_MESSAGE]);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('reports the policy for an empty replacement, not a separate required sentence', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      press(CHANGE_PASSWORD_TEXT);

      expect(fieldErrors()).toEqual([POLICY_MESSAGE]);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('accepts a replacement of exactly the minimum length with no punctuation at all', () => {
      arriveAsSelf(account(7));

      // Zero non-alphanumeric characters are required, so a purely alphanumeric value passes.
      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, 'Abcde12');
      type(CONTROL_ID.confirmPassword, 'Abcde12');
      press(CHANGE_PASSWORD_TEXT);

      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
    });

    it('demands the credential in force for the account holder and sends nothing without it', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      expect(fieldErrors()).toEqual([MISSING_CURRENT_MESSAGE]);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('refuses a replacement identical to the credential in force', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, VALID_PASSWORD);
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      // Fourth in the order, and reachable only because the three before it passed.
      expect(fieldErrors()).toEqual([NOT_DIFFERENT_MESSAGE]);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('applies neither the current-credential rule nor the not-different rule for an administrator', () => {
      arriveAsAdministrator(account(7));

      // ⚠ THE RULE IS DYNAMIC AND HAS BEEN REMOVED FROM THE CONTROL. An administrator supplies no
      // credential in force, so demanding one — or comparing against it — would be unmeetable.
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);

      expect(fieldErrors()).withContext('nothing is refused').toHaveSize(0);
      pressDialogue(RESET_PASSWORD_TEXT);
      expectRequest('POST', resetUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
    });

    it('says nothing about validity before a person has acted', () => {
      arriveAsSelf(account(7));

      expect(fieldErrors()).toHaveSize(0);
      expect(queryAll('[aria-invalid="true"]')).toHaveSize(0);
    });

    it('withholds the command entirely while there is no account to act on', () => {
      seatIdentity(caller(7, ['Registered Users']));
      create('7');

      const read = expectRequest('GET', userUrl(7));

      // Nothing is drawn at all while the read is outstanding, so the command cannot be pressed.
      expect(button(CHANGE_PASSWORD_TEXT)).toBeUndefined();
      expect(button(RESET_PASSWORD_TEXT)).toBeUndefined();

      read.flush(envelope(account(7)));
      fixture.detectChanges();

      expect(button(CHANGE_PASSWORD_TEXT)?.disabled).withContext('offered now').toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — CHANGING ONE'S OWN CREDENTIAL
  // ---------------------------------------------------------------------------------------------------

  describe('changing ones own credential', () => {
    it('posts the four declared members to the change endpoint and answers 204', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      const write = expectRequest('POST', changeUrl(7), 'the change');
      const body = write.request.body as ChangePasswordRequest;

      expect(write.request.url).toBe('/api/v1/users/7/password');
      expect(Object.keys(body as unknown as Record<string, unknown>).sort()).toEqual([
        'confirmPassword',
        'currentPassword',
        'newPassword',
        'operation',
      ]);
      expect(body.operation).toBe('change');
      expect(body.currentPassword).toBe('InForce1');
      expect(body.newPassword).toBe(VALID_PASSWORD);
      // Transmitted for both operations, so one request shape serves both and the server may
      // tighten its own comparison without a client change.
      expect(body.confirmPassword).toBe(VALID_PASSWORD);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7), 'the account re-read').flush(envelope(account(7)));
      fixture.detectChanges();
    });

    it('asks no question first, because a holder proving their own credential needs none', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      expect(query('.confirm-dialog')).withContext('no confirmation on this path').toBeNull();

      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
    });

    it('announces the measured wording, clears every control and re-reads the account only', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7), 'the account re-read').flush(envelope(account(7)));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'success', message: PASSWORD_CHANGED_TEXT }]);
      // ⚠ NO CREDENTIAL OUTLIVES THE REQUEST THAT USED IT. Clearing is both the faithful
      // behaviour — a password box never re-rendered its value across a postback — and the
      // correct one.
      expect(field<HTMLInputElement>(CONTROL_ID.currentPassword).value).toBe('');
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).value).toBe('');
      expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).value).toBe('');
      // The listing is untouched, because nothing on it changed.
      expect(httpMock.match(() => true)).withContext('nothing further').toHaveSize(0);
    });

    it('addresses account zero untouched', () => {
      const held = account(0);

      seatIdentity(caller(0, ['Registered Users']));
      arrive(held);

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      const write = expectRequest('POST', changeUrl(0));

      expect(write.request.url).toBe('/api/v1/users/0/password');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(0)).flush(envelope(account(0)));
      fixture.detectChanges();
    });

    it('withholds the command while a write is outstanding', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      const write = expectRequest('POST', changeUrl(7));

      fixture.detectChanges();

      expect(button(CHANGE_PASSWORD_TEXT)?.disabled).withContext('withheld while saving').toBeTrue();
      expect(query('app-loading-spinner')).withContext('the write is announced').not.toBeNull();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — RESETTING SOMEBODY ELSE'S CREDENTIAL
  // ---------------------------------------------------------------------------------------------------

  describe('resetting somebody elses credential', () => {
    it('asks first, naming the account, then posts to the reset endpoint and answers 204', () => {
      arriveAsAdministrator(account(7, { username: 'ada.lovelace' }));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);

      // ⚠ A RESET PROVES NOTHING ABOUT THE OLD VALUE, so it is confirmed before it is performed.
      const dialogue = query('.confirm-dialog');

      expect(dialogue).withContext('the question is asked').not.toBeNull();
      expect(dialogue?.getAttribute('role')).toBe('alertdialog');
      expect(dialogue?.getAttribute('aria-modal')).toBe('true');
      expect(query('.confirm-dialog__message')?.textContent ?? '').toContain('ada.lovelace');
      expect(httpMock.match(() => true)).withContext('nothing yet').toHaveSize(0);

      pressDialogue(RESET_PASSWORD_TEXT);

      const write = expectRequest('POST', resetUrl(7), 'the reset');
      const body = write.request.body as ChangePasswordRequest;

      expect(write.request.url).toBe('/api/v1/users/7/password-reset');
      expect(body.operation).toBe('reset');
      // ⚠ NULL, NOT AN EMPTY STRING. A reset carries no credential in force, and sending an empty
      // one would assert a value the operator never supplied.
      expect(body.currentPassword).toBeNull();
      expect(body.newPassword).toBe(VALID_PASSWORD);
      expect(body.confirmPassword).toBe(VALID_PASSWORD);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7), 'the account re-read').flush(envelope(account(7)));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'success', message: PASSWORD_CHANGED_TEXT }]);
    });

    it('sends nothing when the question is dismissed, and keeps the entry', () => {
      arriveAsAdministrator(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);
      pressDialogue('Cancel');

      expect(query('.confirm-dialog')).withContext('the question is closed').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      // Dismissing the question is not abandoning the entry.
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).value).toBe(VALID_PASSWORD);
      expect(notifications()).toHaveSize(0);
    });

    it('re-checks the rules when the question is answered, not only when it is asked', () => {
      arriveAsAdministrator(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);

      expect(query('.confirm-dialog')).not.toBeNull();

      // The entry is spoiled while the question stands open. Answering it must not send a
      // replacement that no longer satisfies the rules.
      type(CONTROL_ID.confirmPassword, OTHER_PASSWORD);
      pressDialogue(RESET_PASSWORD_TEXT);

      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      expect(fieldErrors()).toContain(MISMATCH_MESSAGE);
    });

    it('addresses account minus one untouched', () => {
      seatIdentity(caller(99, ['Administrators']));
      arrive(account(-1));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);
      pressDialogue(RESET_PASSWORD_TEXT);

      const write = expectRequest('POST', resetUrl(-1));

      expect(write.request.url).toBe('/api/v1/users/-1/password-reset');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(-1)).flush(envelope(account(-1)));
      fixture.detectChanges();
    });

    it('clears every control after a reset as well', () => {
      arriveAsAdministrator(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);
      pressDialogue(RESET_PASSWORD_TEXT);
      expectRequest('POST', resetUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();

      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).value).toBe('');
      expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).value).toBe('');
      expect(query('.confirm-dialog')).withContext('and the question is closed').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — A REFUSED WRITE
  // ---------------------------------------------------------------------------------------------------

  describe('a refused write', () => {
    /** Drives a change to the point of a refusal with the supplied document and status. */
    function refuseChange(document: ProblemDetails, status: number, statusText: string): void {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);
      expectRequest('POST', changeUrl(7)).flush(document, { status, statusText });
      fixture.detectChanges();
    }

    it('shows an incorrect current credential in the banner and keeps the entry', () => {
      refuseChange(
        problem(
          'user.password.current_incorrect',
          400,
          'The request could not be processed as submitted.',
        ),
        400,
        'Bad Request',
      );

      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'The request could not be processed as submitted.',
      );
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
      // ⚠ NOTHING IS CLEARED ON A REFUSAL, so the operator can correct one field rather than
      // retype all three — and no follow-up read is issued.
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).value).toBe(VALID_PASSWORD);
      expect(httpMock.match(() => true)).withContext('no re-read').toHaveSize(0);
      expect(notifications()).withContext('nothing succeeded').toHaveSize(0);
    });

    it('pins a per-field refusal to the control the server named', () => {
      refuseChange(
        problem('user.password.invalid', 400, 'The request could not be processed as submitted.', {
          NewPassword: ['That replacement does not meet this site policy.'],
        }),
        400,
        'Bad Request',
      );

      // ⚠ THE SERVER'S KEY IS ITS OWN MODEL-STATE SPELLING, NOT CAMEL-CASED, so the match is
      // case-insensitive; spelling it exactly would silently find nothing.
      expect(fieldErrors()).toContain('That replacement does not meet this site policy.');
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).getAttribute('aria-invalid')).toBe('true');
    });

    it('reads a refusal of authority as a warning rather than as a fault', () => {
      refuseChange(
        problem(
          'user.password.change_self_only_forbidden',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        403,
        'Forbidden',
      );

      // The legacy denial page rendered at its yellow warning style, and the shared banner
      // resolves a refusal to a warning. Nothing on this screen classifies it.
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Warning');
    });

    it('reads a rate-limit refusal at the banner own calm band', () => {
      refuseChange(
        problem(
          'request.rate_limited',
          429,
          'Too many requests have been submitted. Retry after a short delay.',
        ),
        429,
        'Too Many Requests',
      );

      // ⚠ THE BANNER INTERCEPTS 429 BEFORE THE DOMAIN RULE, so the word shown is "Please wait".
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Please wait');
    });

    it('reads a credential store outage as a fault, at 503', () => {
      refuseChange(
        problem(
          'user.credential.removal_store_unavailable',
          503,
          'The service is temporarily unavailable. Retry after a short delay.',
        ),
        503,
        'Service Unavailable',
      );

      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
    });

    it('releases the command after a refusal so the entry can be corrected and retried', () => {
      refuseChange(
        problem('user.password.reset_failed', 500, 'An unexpected error occurred while processing the request.'),
        500,
        'Internal Server Error',
      );

      expect(button(CHANGE_PASSWORD_TEXT)?.disabled).withContext('released').toBeFalse();

      type(CONTROL_ID.newPassword, OTHER_PASSWORD);
      type(CONTROL_ID.confirmPassword, OTHER_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      expectRequest('POST', changeUrl(7), 'the retry').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'success', message: PASSWORD_CHANGED_TEXT }]);
    });

    it('reports a refused reset without announcing a success', () => {
      arriveAsAdministrator(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);
      pressDialogue(RESET_PASSWORD_TEXT);
      expectRequest('POST', resetUrl(7)).flush(
        problem(
          'user.password.reset_forbidden',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).withContext('nothing succeeded').toHaveSize(0);
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).value)
        .withContext('the entry survives')
        .toBe(VALID_PASSWORD);
      expect(httpMock.match(() => true)).withContext('no re-read').toHaveSize(0);
    });

    it('ignores a failure recorded by another screen entirely', () => {
      arriveAsSelf(account(7));

      // Keying on the operation is what stops another screen's failure from appearing here.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', '/api/v1/users/settings').flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('app-error-banner .error-banner__title'))
        .withContext('not this screen failure')
        .toBeNull();
    });

    it('reports the shared reference from the correlation identifier', () => {
      refuseChange(
        problem('user.password.reset_failed', 500, 'An unexpected error occurred while processing the request.'),
        500,
        'Internal Server Error',
      );

      // Every live document carries both identifiers, and the shared reader prefers this one.
      expect(query('.error-banner__trace')?.textContent ?? '').toContain(
        'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
      );
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — NO CREDENTIAL ESCAPES
  // ---------------------------------------------------------------------------------------------------

  describe('credential confinement', () => {
    it('declares every credential control as a credential control', () => {
      arriveAsSelf(account(7));

      for (const controlId of Object.values(CONTROL_ID)) {
        expect(field<HTMLInputElement>(controlId).getAttribute('type'))
          .withContext(`#${controlId} is masked`)
          .toBe('password');
      }
    });

    it('never puts a typed credential into the document text', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);

      // ⚠ THE VALUE LIVES IN THE CONTROL, NOT IN THE DOCUMENT. It must not be interpolated into
      // a text node, a title, a help sentence or a label — and the confirmation names the
      // account rather than the replacement.
      expect(host().textContent ?? '').not.toContain(VALID_PASSWORD);
      expect(host().textContent ?? '').not.toContain('InForce1');
    });

    it('never puts a credential into the confirmation it raises', () => {
      arriveAsAdministrator(account(7));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);

      const dialogue = query('.confirm-dialog');

      expect(dialogue?.textContent ?? '').not.toContain(VALID_PASSWORD);
      // It names the consequence and the account, which is what a person needs to decide.
      expect(dialogue?.textContent ?? '').toContain('ada.lovelace');

      pressDialogue('Cancel');
    });

    it('never announces or logs a credential, on success or on refusal', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();

      const spoken: string = everythingSpoken();

      expect(spoken).withContext('no credential is spoken').not.toContain(VALID_PASSWORD);
      expect(spoken).not.toContain('InForce1');
      // The success wording names no value at all.
      expect(notifications()).toEqual([{ severity: 'success', message: PASSWORD_CHANGED_TEXT }]);
    });

    it('never announces or logs a credential when the server refuses', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);
      expectRequest('POST', changeUrl(7)).flush(
        problem('user.password.current_incorrect', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const spoken: string = everythingSpoken();

      expect(spoken).not.toContain(VALID_PASSWORD);
      expect(spoken).not.toContain('InForce1');
      expect(host().textContent ?? '').not.toContain(VALID_PASSWORD);
    });

    it('sends a credential only in the request body, never in the address or the query', () => {
      arriveAsSelf(account(7));

      type(CONTROL_ID.currentPassword, 'InForce1');
      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(CHANGE_PASSWORD_TEXT);

      const write = expectRequest('POST', changeUrl(7));

      // ⚠ AN ADDRESS IS LOGGED BY EVERY PROXY IN THE PATH. A credential belongs in a body.
      expect(write.request.urlWithParams).not.toContain(VALID_PASSWORD);
      expect(write.request.params.keys()).toHaveSize(0);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expectRequest('GET', userUrl(7)).flush(envelope(account(7)));
      fixture.detectChanges();
    });

    it('offers no retrieval affordance of any kind', () => {
      arriveAsAdministrator(account(7));

      // Credentials are one-way hashed in the target, so a stored value cannot be produced on
      // request by anybody — and no screen offers to. Administrative RESET survives; retrieval
      // does not.
      const markup = (host().textContent ?? '').toLowerCase();

      expect(markup).not.toContain('retrieve');
      expect(markup).not.toContain('send password');
      expect(markup).not.toContain('email password');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark, because the shell owns them', () => {
      arriveAsSelf(account(7));

      expect(queryAll('main')).toHaveSize(0);
      expect(queryAll('nav')).toHaveSize(0);
    });

    it('names every credential control with a real label pointing at it', () => {
      arriveAsSelf(account(7));

      // ⚠ THE TWO READ-ONLY ROWS ARE LABELLED WITHOUT A TARGET, deliberately: each labels a
      // paragraph rather than a control, and pointing a label at a non-control would be a
      // false association. Only the labels that DO name a target are checked against one.
      const targeted = queryAll<HTMLLabelElement>('label.form-field__label').filter(
        (label) => label.getAttribute('for') !== null,
      );

      expect(targeted.length).withContext('a label per credential control').toBeGreaterThanOrEqual(3);

      for (const label of targeted) {
        const target: string = String(label.getAttribute('for'));

        expect(query(`#${target}`))
          .withContext(`the control ${target} exists`)
          .not.toBeNull();
      }

      // And every credential control is named by exactly one of them.
      for (const controlId of Object.values(CONTROL_ID)) {
        expect(targeted.filter((label) => label.getAttribute('for') === controlId))
          .withContext(`#${controlId} is named once`)
          .toHaveSize(1);
      }
    });

    it('renders the measured labels verbatim, including their punctuation', () => {
      arriveAsSelf(account(7));

      const markup = host().textContent ?? '';

      // ⚠ WHERE THE MARKUP AND THE RESOURCE FILE DISAGREED, THE RESOURCE FILE WON. The measured
      // labels read "Current Password:" and "Confirm Password:", not the markup's "Old Password:"
      // and "Confirm New Password:".
      expect(markup).toContain(CURRENT_PASSWORD_LABEL);
      expect(markup).toContain(NEW_PASSWORD_LABEL);
      expect(markup).toContain(CONFIRM_PASSWORD_LABEL);
      expect(markup).not.toContain('Old Password');
      expect(markup).not.toContain('Confirm New Password');
    });

    it('announces the required state of every control it applies to', () => {
      arriveAsSelf(account(7));

      expect(field<HTMLInputElement>(CONTROL_ID.currentPassword).getAttribute('aria-required')).toBe(
        'true',
      );
      expect(field<HTMLInputElement>(CONTROL_ID.newPassword).getAttribute('aria-required')).toBe('true');
      expect(field<HTMLInputElement>(CONTROL_ID.confirmPassword).getAttribute('aria-required')).toBe(
        'true',
      );
    });

    it('declares exactly one submit command, so nothing else can submit by accident', () => {
      arriveAsSelf(account(7));

      const submits = queryAll<HTMLButtonElement>('button').filter(
        (candidate) => candidate.getAttribute('type') === 'submit',
      );

      expect(submits).toHaveSize(1);
      expect((submits[0]?.textContent ?? '').trim()).toBe(CHANGE_PASSWORD_TEXT);
    });

    it('drops the question-and-answer section entirely, because the contract carries none', () => {
      arriveAsSelf(account(7));

      // The legacy screen offered a security question and answer. No member on this contract
      // carries either, so there is nothing for a control to bind to.
      const markup = (host().textContent ?? '').toLowerCase();

      expect(markup).not.toContain('question');
      expect(markup).not.toContain('answer');
    });

    it('keeps no banner mounted when there is nothing to report', () => {
      arriveAsSelf(account(7));

      // The banner emits nothing at all when there is no problem, so the screen binds it
      // unconditionally and lets it disappear rather than guarding it with its own test.
      expect(query('.error-banner__title')).toBeNull();
    });

    it('renders a hostile sign-in name as text in the confirmation, with nothing parsed out of it', () => {
      const hostile = '<img src=x onerror="window.__dnnPasswordSentinel = true">';

      arriveAsAdministrator(account(7, { username: hostile }));

      type(CONTROL_ID.newPassword, VALID_PASSWORD);
      type(CONTROL_ID.confirmPassword, VALID_PASSWORD);
      press(RESET_PASSWORD_TEXT);

      const dialogue = query('.confirm-dialog');

      expect(dialogue?.textContent ?? '').withContext('carried as text').toContain(hostile);
      expect(host().querySelectorAll('img')).withContext('nothing was parsed').toHaveSize(0);
      expect(
        (window as unknown as Record<string, unknown>)['__dnnPasswordSentinel'],
      ).toBeUndefined();

      pressDialogue('Cancel');
    });
  });
});
