import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ChangePasswordRequest, UserDetail } from '../../../core/models/user.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import {
  PASSWORD_UPDATE_CODES,
  PASSWORD_UPDATE_MESSAGE,
  passwordUpdateMessage,
} from '../../../core/utils/form-errors.util';
import {
  CREDENTIAL_REMEDIATION_EXPLANATION,
  UserPasswordComponent,
} from './user-password.component';

// THE ADDRESSES

const USERS_PATH = '/api/v1/users';

function accountUrl(userId: number): string {
  return `${USERS_PATH}/${userId}`;
}

function changeUrl(userId: number): string {
  return `${accountUrl(userId)}/password`;
}

function resetUrl(userId: number): string {
  return `${accountUrl(userId)}/password-reset`;
}

// THE MEASURED POLICY
// `UserController.ValidatePassword` applies three rules, and only the first can ever fire. The length rule
// refuses a value shorter than the minimum.

const MINIMUM_LENGTH = 7;

/** Six characters: one short of the boundary, so refused. */
const SIX_CHARACTER_PASSWORD = 'Six123';

/** Exactly seven characters, and purely alphanumeric: the boundary, so accepted. */
const SEVEN_CHARACTER_PASSWORD = 'Seven12';

/** Longer, and still purely alphanumeric: the proof that the vacuous rule stays vacuous. */
const PURELY_ALPHANUMERIC_PASSWORD = 'Alphanumeric123';

/**
 * Twenty-five characters — past the legacy markup ceiling of twenty. MIGRATION: the legacy
 * `maxlength="20"` on `Password.ascx` L35, L39 and L43 mirrored the legacy STORAGE width and was never a
 * policy rule, so it is deliberately not reproduced as one.
 */
const TWENTY_FIVE_CHARACTER_PASSWORD = 'TwentyFiveCharacterPass25';

/** An ordinary acceptable replacement. Obviously synthetic; not a credential. */
const REPLACEMENT = 'Replace1';

/** A second acceptable replacement, for cases that need two distinct values. */
const OTHER_REPLACEMENT = 'Different2';

/** The credential in force on the self-service path. Obviously synthetic. */
const CREDENTIAL_IN_FORCE = 'InForce1';

// THE MEASURED WORDING
// The two properties that are easy to lose in transcription are called out because both are load-bearing:
// several of these strings carry NO trailing full stop, and two carry a DOUBLE space between sentences.

/** `SharedResources.resx` -> `PasswordMismatch.Text`. */
const LEGACY_MISMATCH = 'The Password and Confirmation Passwords do not match';

/** `SharedResources.resx` -> `PasswordMissing.Text`. */
const LEGACY_MISSING =
  'You must provide your current password in order to change the password.';

/** `SharedResources.resx` -> `PasswordNotDifferent.Text`. */
const LEGACY_NOT_DIFFERENT =
  'The new password is the same as the old password.  Please enter a different password';

/** `SharedResources.resx` -> `PasswordResetFailed.Text`. */
const LEGACY_RESET_FAILED =
  'There was an error setting the password. The password has not been changed.';

/** `SharedResources.resx` -> `PasswordInvalid.Text`. */
const LEGACY_PASSWORD_INVALID =
  'You must enter a valid password.  Please check with the Portal Administrator if you ' +
  'do not know the password requirements.';

/** `SharedResources.resx` -> `InvalidPasswordAnswer.Text`. */
const LEGACY_INVALID_ANSWER = 'Password Answer must be provided';

/** `SharedResources.resx` -> `InvalidPasswordQuestion.Text`. */
const LEGACY_INVALID_QUESTION = 'Password Question must be provided';

/** `SharedResources.resx` -> `PasswordChanged.Text`. */
const LEGACY_PASSWORD_CHANGED = 'The password has been reset.';

/**
 * `SharedResources.resx` -> `InvalidPassword.Text`, with its two replacement tokens resolved from the
 * measured configuration: `[PasswordLength]` becomes 7 and `[NoneAlphabet]` becomes 0.
 * `InvalidPassword.Text` and `PasswordInvalid.Text` are DISTINCT KEYS with distinct wording.
 */
const LEGACY_POLICY_STATEMENT =
  'The password specified is invalid.  Please specify a valid password.  Passwords ' +
  `must be at least ${MINIMUM_LENGTH} characters in length and contain at least 0 ` +
  'non-alphanumeric characters.';

/** `Password.ascx.resx` -> `NoExpiry.Text`. */
const LEGACY_NO_EXPIRY = 'Password does not Expire';

/** `Password.ascx.resx` -> `ForcedExpiry.Text`. */
const LEGACY_FORCED_EXPIRY =
  'The Portal Administrator has required you to change your password, before you can log in.';

/**
 * `Password.ascx.resx` -> `plLastChanged.Text`. The markup's fallback at `Password.ascx` L15 reads
 * 'Password last Changed:' with a lower-case 'last'; the resource file overrode it at run time, so the
 * resource capitalisation is what a person actually saw and is what is reproduced.
 */
const LAST_CHANGED_LABEL = 'Password Last Changed:';

/** `Password.ascx.resx` -> `plExpires.Text`. */
const EXPIRES_LABEL = 'Password Expires:';

/** The heading the change section and the confirmation both carry. */
const CHANGE_HEADING = 'Change Password';

/** The heading the reset section, the submit control and the confirmation carry. */
const RESET_HEADING = 'Reset Password';

/** The confirmation's dismissal label, which the shared dialog hardcodes. */
const DISMISS_LABEL = 'Cancel';

// THE SENTINELS

/** The date sentinel exactly as it arrives on the wire. */
const NULL_DATE_ON_THE_WIRE = '0001-01-01T00:00:00';

/** The integer sentinel, which on this screen is also a REAL account identifier. */
const NULL_INTEGER = -1;

// THE CONTROLS

const CONTROL_ID = Object.freeze({
  currentPassword: 'currentPassword',
  newPassword: 'newPassword',
  confirmPassword: 'confirmPassword',
});

/** Every credential control, in the order the screen presents them. */
const EVERY_CONTROL_ID: readonly string[] = Object.freeze([
  CONTROL_ID.currentPassword,
  CONTROL_ID.newPassword,
  CONTROL_ID.confirmPassword,
]);

/**
 * Claims a confirmation must never make. A confirmation justifies gating an action; it is not a licence
 * to overstate what the action does.
 */
const FORBIDDEN_OVERCLAIMS: readonly string[] = Object.freeze([
  'cannot be undone',
  'permanently',
  'irreversible',
  'this action is final',
  'you will not be able to recover',
]);

// NARROWING, WITHOUT ASSERTIONS

/**
 * Resolves a selector that MUST match, or throws naming it.
 *
 * @param root The node to search within.
 * @param selector The CSS selector to resolve.
 * @returns The matched element.
 */
function requireElement(root: ParentNode, selector: string): Element {
  const found = root.querySelector(selector);

  if (found === null) {
    throw new Error(`Expected to find "${selector}" in the rendered template.`);
  }

  return found;
}

/**
 * Resolves a selector that MUST match an input, narrowing by construction rather than by assertion.
 *
 * @param root The node to search within.
 * @param selector The CSS selector to resolve.
 * @returns The matched input.
 */
function requireInput(root: ParentNode, selector: string): HTMLInputElement {
  const found = requireElement(root, selector);

  if (!(found instanceof HTMLInputElement)) {
    throw new Error(`Expected "${selector}" to resolve to an input element.`);
  }

  return found;
}

/**
 * The trimmed text of a node, or the empty string when the node holds none.
 *
 * @param node The node to read.
 * @returns The trimmed text.
 */
function textOf(node: Element | null): string {
  return node === null ? '' : (node.textContent ?? '').trim();
}

/**
 * Collapses every run of whitespace to a single space. Used to compare authored wording with the measured
 * legacy wording.
 *
 * @param text The text to normalise.
 * @returns The text with every whitespace run reduced to one space.
 */
function collapseWhitespace(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/**
 * Builds a problem document of the shape the API emits.
 *
 * @param status The transport status.
 * @param detail The human-readable detail.
 * @param code The machine-readable failure code, or null when the document carries none.
 * @param errors The per-field messages, keyed as the server keys them.
 * @returns The problem document.
 */
function problemDocument(
  status: number,
  detail: string,
  code: string | null = null,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const document: ProblemDetails = {
    type: code === null ? 'about:blank' : `https://dnn.example.test/problems/${code}`,
    title: 'The request could not be completed.',
    status,
    detail,
    instance: `${USERS_PATH}/7/password`,
    traceId: '00-3f2a1b4c5d6e7f8a9b0c1d2e3f4a5b6c-1a2b3c4d5e6f7a8b-01',
    correlationId: 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
  };

  return errors === undefined ? document : { ...document, errors };
}

/**
 * Builds an account of the shape the read endpoint returns.
 *
 * @param userId The account identifier.
 * @param overrides The fields this case cares about.
 * @returns The account.
 */
function account(userId: number, overrides: Partial<UserDetail> = {}): UserDetail {
  const base: UserDetail = {
    userId,
    portalId: NULL_INTEGER,
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
  };

  return { ...base, ...overrides };
}

describe('UserPasswordComponent', () => {
  let fixture: ComponentFixture<UserPasswordComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UserPasswordComponent],
      // The real client FIRST, then the testing backend that displaces it. Reversing
      // these two is the commonest false green in an Angular 19 suite.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // The stored session outlives a single injector, so it is cleared before every case as well as after
    // one. Without this, a case that seated an administrator could hand that identity to whichever case
    // Jasmine happens to run next.
    TestBed.inject(TokenStorageService).clear();

    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // Created but NOT rendered: the account identifier is a REQUIRED signal input, and rendering before it
    // is bound would throw rather than answer. Each case binds it through `arrive`, which is also where the
    // read is satisfied.
    fixture = TestBed.createComponent(UserPasswordComponent);
  });

  afterEach(() => {
    httpMock.verify();

    fixture.destroy();
    TestBed.inject(TokenStorageService).clear();
  });

  // -------------------------------------------------------------------------
  // READING THE RENDERED DOCUMENT
  // -------------------------------------------------------------------------

  function host(): HTMLElement {
    // Assignment rather than assertion: `nativeElement` is loosely typed, so naming
    // the target type here is enough and no cast is needed.
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function query(selector: string): Element | null {
    return host().querySelector(selector);
  }

  function queryAll(selector: string): readonly Element[] {
    return Array.from(host().querySelectorAll(selector));
  }

  function control(controlId: string): HTMLInputElement {
    return requireInput(host(), `#${controlId}`);
  }

  /** Enters a value the way a person does, then lets the screen settle. */
  function enter(controlId: string, value: string): void {
    const element = control(controlId);

    element.value = value;
    element.dispatchEvent(new Event('input'));
    element.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  function buttons(): readonly HTMLButtonElement[] {
    // `querySelectorAll('button')` is already typed to the button interface by the tag
    // name, so this needs neither a generic argument nor a cast.
    return Array.from(host().querySelectorAll('button'));
  }

  function buttonLabelled(label: string): HTMLButtonElement | null {
    return buttons().find((candidate) => textOf(candidate) === label) ?? null;
  }

  function press(label: string): void {
    const target = buttonLabelled(label);

    if (target === null) {
      throw new Error(`Expected a control labelled "${label}" to be offered.`);
    }

    target.click();
    fixture.detectChanges();
  }

  /**
   * A control offered by the confirmation, matched by CONTAINMENT rather than by equality. The shared
   * confirmation renders a severity glyph inside its agreeing control, so that control's text is the
   * glyph followed by the label.
   */
  function dialogButtonLabelled(label: string): HTMLButtonElement | null {
    const found = buttons().find(
      (candidate) =>
        candidate.classList.contains('confirm-dialog__button') && textOf(candidate).includes(label),
    );

    return found ?? null;
  }

  function answerDialog(label: string): void {
    const target = dialogButtonLabelled(label);

    if (target === null) {
      throw new Error(`Expected the confirmation to offer "${label}".`);
    }

    target.click();
    fixture.detectChanges();
  }

  /** Every inline message currently shown beside a control. */
  function inlineMessages(): readonly string[] {
    return queryAll('.form-field__error').map((node) => textOf(node));
  }

  /**
   * The messages a screen reader would announce FOR ONE CONTROL, resolved the way assistive technology
   * resolves them: follow the control's own `aria-describedby` to the elements it names and read those.
   * This is what makes 'the length rule is stated beside the replacement, and the mismatch beside the
   * confirmation' an observable claim rather than an assumption about internal ordering.
   */
  function messagesFor(controlId: string): readonly string[] {
    const described = control(controlId).getAttribute('aria-describedby');

    if (described === null || described.trim() === '') {
      return [];
    }

    return described
      .trim()
      .split(/\s+/)
      .flatMap((id) => Array.from(host().querySelectorAll(`#${id} .form-field__error`)))
      .map((node) => textOf(node));
  }

  /** Every announcement raised, in order, as severity and message. */
  function announcements(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /** The number of requests still outstanding, whatever they are. */
  function outstandingRequestCount(): number {
    return httpMock.match(() => true).length;
  }

  // -------------------------------------------------------------------------
  // DRIVING THE SCREEN
  // -------------------------------------------------------------------------

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
   * Seats the caller's identity. The identity is read from the stored session rather than fetched, so
   * seating it is what decides `isAdmin` and `isSelf` — and therefore which of the two operations the
   * screen resolves to.
   *
   * @param userId The caller's own account key.
   * @param roles The caller's role names, which decide nothing here.
   * @param administersPortal Whether the API derives tenant administration for this caller.
   */
  function seatIdentity(userId: number, roles: readonly string[], administersPortal = false): void {
    TestBed.inject(TokenStorageService).store({
      accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
      expiresAtUtc: '2099-12-31T23:59:59.000Z',
      refreshToken: 'not-a-real-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: {
        userId,
        portalId: NULL_INTEGER,
        portalName: 'Baseline Portal',
        username: 'caller',
        displayName: 'The Caller',
        email: 'caller@example.test',
        isSuperUser: false,
        isPortalAdministrator: administersPortal,
        roles,
        permissions: [],
      },
    });
  }

  /** Binds the account identifier and satisfies the read the screen issues on arrival. */
  function arrive(held: UserDetail, routeValue?: string): void {
    fixture.componentRef.setInput('userId', routeValue ?? String(held.userId));
    fixture.detectChanges();

    expectRequest('GET', accountUrl(held.userId), 'the account read').flush({
      data: held,
      meta: null,
    });
    fixture.detectChanges();
  }

  /** The account holder managing their own credential: the CHANGE operation. */
  function arriveAsSelf(held: UserDetail = account(7)): void {
    seatIdentity(held.userId, ['Registered Users']);
    arrive(held);
  }

  /** An administrator acting on somebody else's account: the RESET operation. */
  function arriveAsAdministrator(held: UserDetail = account(7)): void {
    seatIdentity(99, ['Administrators'], true);
    arrive(held);
  }

  /** An administrator acting on their OWN account: the legacy inconsistency's case. */
  function arriveAsAdministratorOfOwnAccount(held: UserDetail = account(7)): void {
    seatIdentity(held.userId, ['Administrators'], true);
    arrive(held);
  }

  /**
   * Satisfies the re-read the screen issues after a successful write. A successful write is followed by a
   * fresh read, so that the dates the screen shows reflect what the write did rather than what was true
   * before it.
   */
  function settleAfterWrite(userId: number): void {
    expectRequest('GET', accountUrl(userId), 'the account re-read').flush({
      data: account(userId),
      meta: null,
    });
    fixture.detectChanges();
  }

  /** Fills the change form with a replacement that satisfies every rule. */
  function fillValidChange(replacement: string = REPLACEMENT): void {
    enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
    enter(CONTROL_ID.newPassword, replacement);
    enter(CONTROL_ID.confirmPassword, replacement);
  }

  // =========================================================================
  // A. THE CREDENTIAL POLICY: LENGTH >= 7, AND NOTHING ELSE
  // =========================================================================

  describe('the unsaved-entry probe this screen registers', () => {
    it('reports a typed credential, so a reload cannot discard it in silence', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arriveAsSelf();

      // THE CONTROL: an untouched form must not warn, or a later `true` proves nothing at all.
      expect(tracker.isDirty())
        .withContext('an untouched credential form is not unsaved entry')
        .toBeFalse();

      enter(CONTROL_ID.newPassword, REPLACEMENT);

      expect(tracker.isDirty())
        .withContext('a typed credential with no write in flight is what the guard exists to catch')
        .toBeTrue();
    });
  });

  describe('the credential policy', () => {
    it('refuses a replacement one character short of the boundary', () => {
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SIX_CHARACTER_PASSWORD);

      expect(SIX_CHARACTER_PASSWORD.length)
        .withContext('the fixture really is one short')
        .toBe(MINIMUM_LENGTH - 1);
      expect(inlineMessages())
        .withContext('the policy is stated beside the replacement')
        .toContain(LEGACY_POLICY_STATEMENT);

      press(CHANGE_HEADING);

      expect(outstandingRequestCount()).withContext('nothing is sent').toBe(0);
    });

    it('accepts a replacement of exactly the boundary length', () => {
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, SEVEN_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SEVEN_CHARACTER_PASSWORD);

      expect(SEVEN_CHARACTER_PASSWORD.length)
        .withContext('the fixture really is the boundary')
        .toBe(MINIMUM_LENGTH);
      expect(inlineMessages())
        .withContext('the boundary is inclusive, so nothing is objected to')
        .toEqual([]);

      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('accepts a purely alphanumeric replacement, because that rule cannot fire', () => {
      // THE VACUOUS-RULE PROOF, and the assertion that stops a later reader "helpfully" reviving a rule the
      // legacy system never enforced. The legacy predicate counted matches of `[^0-9a-zA-Z]` and refused a
      // count BELOW the configured minimum of zero, which no count can be.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, PURELY_ALPHANUMERIC_PASSWORD);
      enter(CONTROL_ID.confirmPassword, PURELY_ALPHANUMERIC_PASSWORD);

      expect(/^[0-9a-zA-Z]+$/.test(PURELY_ALPHANUMERIC_PASSWORD))
        .withContext('the fixture really carries no non-alphanumeric character')
        .toBeTrue();
      expect(inlineMessages()).withContext('and it is accepted anyway').toEqual([]);

      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('declares a typing ceiling on every control but enforces no maximum length', () => {
      arriveAsSelf();

      for (const controlId of EVERY_CONTROL_ID) {
        const declared = control(controlId).getAttribute('maxlength');

        expect(declared)
          .withContext(`#${controlId} declares a typing ceiling`)
          .not.toBeNull();
        expect(Number(declared))
          .withContext(`#${controlId} admits a value past the legacy markup ceiling`)
          .toBeGreaterThan(TWENTY_FIVE_CHARACTER_PASSWORD.length);
      }

      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, TWENTY_FIVE_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, TWENTY_FIVE_CHARACTER_PASSWORD);

      expect(inlineMessages())
        .withContext('a long replacement is not objected to by any rule')
        .toEqual([]);

      press(CHANGE_HEADING);
      const write = expectRequest('POST', changeUrl(7));
      const body: ChangePasswordRequest | null = write.request.body;

      expect(body?.newPassword)
        .withContext('and it reaches the server in full, untruncated')
        .toBe(TWENTY_FIVE_CHARACTER_PASSWORD);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);
    });

    it('states the policy in the measured wording, with its tokens resolved', () => {
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SIX_CHARACTER_PASSWORD);

      const stated = inlineMessages();

      expect(stated).toContain(LEGACY_POLICY_STATEMENT);
      expect(stated)
        .withContext('the policy statement is NOT the outcome vocabulary entry')
        .not.toContain(PASSWORD_UPDATE_MESSAGE['user.password.invalid']);
      expect(collapseWhitespace(LEGACY_POLICY_STATEMENT))
        .withContext('the two resource keys really do carry different wording')
        .not.toBe(collapseWhitespace(LEGACY_PASSWORD_INVALID));

      press(CHANGE_HEADING);

      expect(outstandingRequestCount()).toBe(0);
    });
  });

  // =========================================================================
  // B. THE CONFIRMATION COMPARISON
  // =========================================================================

  describe('the confirmation comparison', () => {
    it('accepts a replacement that matches its confirmation', () => {
      arriveAsSelf();
      fillValidChange();

      expect(inlineMessages()).toEqual([]);

      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('refuses a replacement that does not match, in the measured wording', () => {
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);

      expect(inlineMessages()).toContain(LEGACY_MISMATCH);
      // Measured from `SharedResources.resx`: this sentence carries NO trailing full
      // stop, and losing one is the kind of drift only an assertion catches.
      expect(LEGACY_MISMATCH.endsWith('.')).withContext('no trailing full stop').toBeFalse();

      press(CHANGE_HEADING);

      expect(outstandingRequestCount()).withContext('nothing is sent').toBe(0);
    });

    it('compares exactly, neither trimming nor folding case', () => {
      // A comparison that normalised its operands would accept a pair the server then
      // rejects, so the legacy exact-equality comparison is preserved as it was.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, `${REPLACEMENT} `);

      expect(inlineMessages())
        .withContext('a trailing space is a genuine difference')
        .toContain(LEGACY_MISMATCH);

      enter(CONTROL_ID.confirmPassword, REPLACEMENT.toUpperCase());

      expect(inlineMessages())
        .withContext('a change of case is a genuine difference')
        .toContain(LEGACY_MISMATCH);

      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(inlineMessages()).withContext('and an exact match is accepted').toEqual([]);
    });
  });

  // C. THE ADMINISTRATOR GATE ON THE CREDENTIAL IN FORCE.

  describe('the administrator gate on the credential in force', () => {
    it('hides the credential in force from an administrator acting on another account', () => {
      arriveAsAdministrator(account(7));

      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('L150: display is gated on IsAdmin And Not IsUser')
        .toBeNull();
      expect(query(`#${CONTROL_ID.newPassword}`))
        .withContext('but the replacement is still offered')
        .not.toBeNull();
    });

    it('lets that administrator submit without one', () => {
      arriveAsAdministrator(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(inlineMessages())
        .withContext('a control that is not rendered cannot be required')
        .toEqual([]);

      press(RESET_HEADING);
      answerDialog(RESET_HEADING);

      const write = expectRequest('POST', resetUrl(7));
      const body: ChangePasswordRequest | null = write.request.body;

      expect(body?.currentPassword)
        .withContext('the reset contract requires the credential in force to be ABSENT')
        .toBeNull();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);
    });

    it('shows the credential in force to the account holder and requires it', () => {
      arriveAsSelf(account(7));

      const inForce = control(CONTROL_ID.currentPassword);

      expect(inForce.getAttribute('aria-required'))
        .withContext('and announces that it is required')
        .toBe('true');

      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(CHANGE_HEADING);

      expect(inlineMessages()).toContain(LEGACY_MISSING);
      expect(outstandingRequestCount()).withContext('nothing is sent').toBe(0);
    });

    it('shows the credential in force to an administrator acting on their own account', () => {
      arriveAsAdministratorOfOwnAccount(account(7));

      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('L150 leaves the row visible for this caller')
        .not.toBeNull();
    });

    it('still enforces the credential in force for that administrator', () => {
      // The divergence is annotated in the component and reported, never absorbed silently, and the
      // assertion below pins the behaviour that actually ships rather than a legacy branch that can no
      // longer be reached.
      arriveAsAdministratorOfOwnAccount(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(CHANGE_HEADING);

      expect(inlineMessages())
        .withContext('the operation is a change, so the rule applies')
        .toContain(LEGACY_MISSING);
      expect(outstandingRequestCount())
        .withContext('and the request the server would refuse is never made')
        .toBe(0);
    });

    it('clears and untouches the credential in force when the gate turns off', () => {
      // The two operations differ in whether the credential in force may be sent at
      // all, so a value typed while the rule was on must not survive into a reset.
      arriveAsSelf(account(7));
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);

      expect(control(CONTROL_ID.currentPassword).value).toBe(CREDENTIAL_IN_FORCE);

      seatIdentity(99, ['Administrators'], true);
      fixture.detectChanges();

      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('the control leaves the document with the gate')
        .toBeNull();
      expect(inlineMessages())
        .withContext('and takes any objection about itself with it')
        .toEqual([]);
    });
  });

  // D. THE MEASURED ORDER OF THE PRE-FLIGHT RULES.

  describe('the operation gate the route\u2019s union policy makes necessary', () => {
    it('offers the change to the account holder, who needs no administration for it', () => {
      arriveAsSelf(account(7));
      fillValidChange();

      expect(buttonLabelled(CHANGE_HEADING)?.disabled)
        .withContext('the change is chosen BY ownership, so ownership is all it requires')
        .toBeFalse();
    });

    it('offers the reset to an administrator acting on another account', () => {
      arriveAsAdministrator(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(buttonLabelled(RESET_HEADING)?.disabled)
        .withContext('the administrator arm of the route reaches the operation it exists for')
        .toBeFalse();
    });

    it('withholds the reset from a caller who is neither the holder nor an administrator', () => {
      // ⚠ THE COMBINATION THE ROUTE'S GUARD ALREADY REFUSES, ASSERTED HERE ANYWAY. This case is unreachable
      // through the guarded route, and that is exactly why it is pinned: the gate must answer for every
      // input rather than only for the inputs the router happens to deliver, so that a future route change
      // cannot make the screen offer a credential replacement the server is certain to refuse.
      seatIdentity(4, ['Registered Users']);
      arrive(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(buttonLabelled(RESET_HEADING)?.disabled)
        .withContext('a reset without tenant administration is withheld rather than attempted')
        .toBeTrue();
      expect(outstandingRequestCount())
        .withContext('and nothing is sent')
        .toBe(0);
    });

    it('withholds the reset while the caller\u2019s identity is unresolved', () => {
      // FAIL-CLOSED RATHER THAN OPTIMISTIC. No session is seated, so `administersCurrentPortal` reads false
      // and the planned operation is a reset - the same withholding as above, reached for a different
      // reason.
      arrive(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(buttonLabelled(RESET_HEADING)?.disabled)
        .withContext('an unresolved identity administers nothing')
        .toBeTrue();
    });

    it('does not withhold the change from an administrator acting on their own account', () => {
      // The gate keys on the OPERATION, not on the caller's role, so the administrator who is also the
      // account holder plans a CHANGE and is offered it - which is what the change endpoint's own ownership
      // policy admits them to.
      arriveAsAdministratorOfOwnAccount(account(7));
      fillValidChange();

      expect(buttonLabelled(CHANGE_HEADING)?.disabled).toBeFalse();
    });
  });

  describe('the order of the pre-flight rules', () => {
    it('LEADS with the mismatch but ALSO states the policy when a value breaks both', () => {
      // THE ASSERTION THAT PINS THE ORDER, AND THE ONE THAT PINS COMPLETE DISCLOSURE. A six-character value
      // that also fails to match its confirmation breaks L272 and L278 at once.
      //
      // ⚠ THIS BLOCK PREVIOUSLY ASSERTED THAT THE POLICY STATEMENT WAS ABSENT, AND THAT WAS THE DEFECT
      // RATHER THAN THE REQUIREMENT. Legacy hid it only because `Exit Sub` ran before the length check
      // could - an artifact of early exit, not a validation rule. The consequence was that an operator who
      // corrected the mismatch was rejected a second time for a rule that had never been stated. What the
      // legacy order genuinely governs is which failure LEADS, and that is still asserted below: the
      // mismatch is `firstFailure()`, exactly as before.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);

      const stated = inlineMessages();

      expect(stated).withContext('L272 still leads').toContain(LEGACY_MISMATCH);
      expect(stated)
        .withContext('L278 is now STATED rather than hidden until the mismatch is fixed')
        .toContain(LEGACY_POLICY_STATEMENT);
      expect(stated).withContext('both rules, each beside its own control').toHaveSize(2);

      // AND EACH MESSAGE SITS BESIDE THE CONTROL IT CONCERNS, resolved through the control's own
      // `aria-describedby` - so the operator reading the confirmation field is told about the mismatch, and
      // the operator reading the replacement field is told the length requirement.
      expect(messagesFor(CONTROL_ID.confirmPassword)).toEqual([LEGACY_MISMATCH]);
      expect(messagesFor(CONTROL_ID.newPassword)).toEqual([LEGACY_POLICY_STATEMENT]);
      expect(messagesFor(CONTROL_ID.currentPassword))
        .withContext('supplied, so nothing to say about it')
        .toEqual([]);

      press(CHANGE_HEADING);

      // ⚠ THE BEHAVIOUR-PRESERVATION ASSERTION. Disclosing more must not accept more: the same input the
      // legacy screen refused is still refused, and nothing is sent.
      expect(outstandingRequestCount()).withContext('still refused, as legacy refused it').toBe(0);
    });

    it('reports the policy before the missing credential in force', () => {
      // L278 precedes L284: a short replacement is objected to even though the
      // credential in force is also absent.
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SIX_CHARACTER_PASSWORD);
      press(CHANGE_HEADING);

      const stated = inlineMessages();

      expect(stated).withContext('L278 still leads').toContain(LEGACY_POLICY_STATEMENT);
      // Both rules are unmet and each concerns a DIFFERENT control, so both are stated. The order still
      // decides which leads.
      expect(stated).withContext('L284 is stated too, beside its own control').toContain(LEGACY_MISSING);
      expect(stated).toHaveSize(2);
      expect(messagesFor(CONTROL_ID.newPassword)).toEqual([LEGACY_POLICY_STATEMENT]);
      expect(messagesFor(CONTROL_ID.currentPassword)).toEqual([LEGACY_MISSING]);
    });

    it('reports the missing credential in force before the must-differ rule', () => {
      // L284 precedes L290. With the credential in force absent, the must-differ
      // comparison cannot meaningfully run, and the legacy code never let it.
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(CHANGE_HEADING);

      const stated = inlineMessages();

      expect(stated).withContext('L284 wins').toContain(LEGACY_MISSING);
      expect(stated)
        .withContext('L290 never runs')
        .not.toContain(PASSWORD_UPDATE_MESSAGE['user.password.not_different']);
      expect(stated).toHaveSize(1);
      expect(LEGACY_MISSING)
        .withContext('measured wording, trailing full stop included')
        .toBe('You must provide your current password in order to change the password.');
    });

    it('refuses a replacement identical to the credential in force', () => {
      // L290, on the self-service path where it was gated to run.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, REPLACEMENT);
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(CHANGE_HEADING);

      const shipped = PASSWORD_UPDATE_MESSAGE['user.password.not_different'];

      expect(inlineMessages()).toContain(shipped);
      expect(outstandingRequestCount()).toBe(0);

      // The measured original separates its two sentences with a DOUBLE space and carries NO trailing full
      // stop.
      expect(LEGACY_NOT_DIFFERENT).toContain('old password.  Please');
      expect(collapseWhitespace(shipped)).toBe(collapseWhitespace(LEGACY_NOT_DIFFERENT));
      expect(shipped.endsWith('.')).withContext('no trailing stop, as measured').toBeFalse();
    });

    it('does not apply the must-differ rule to an administrative reset', () => {
      // L290 was gated, and the gate follows the operation: a reset carries no credential in force to
      // differ from, so the rule is off for exactly the caller whose control is not rendered.
      arriveAsAdministrator(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(inlineMessages()).toEqual([]);

      press(RESET_HEADING);
      answerDialog(RESET_HEADING);
      expectRequest('POST', resetUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('states EVERY unmet rule at once, so fixing one cannot reveal an unshown one', () => {
      // ⚠ THIS BLOCK ONCE ASSERTED THE OPPOSITE ('one message at a time, never a list'). That assertion
      // encoded the legacy early-exit artifact as though it were a requirement, and it is precisely the
      // behaviour that made this screen reject a corrected value for a rule it had never stated. The rule
      // set, the wording of every message and the accept/reject decision are all unchanged; only the
      // completeness of the disclosure is.
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);
      press(CHANGE_HEADING);

      const stated = inlineMessages();

      // Three rules are unmet at once here: the confirmation differs, the replacement is too short, and the
      // credential in force was never supplied. All three are stated.
      expect(stated).toContain(LEGACY_MISMATCH);
      expect(stated).toContain(LEGACY_POLICY_STATEMENT);
      expect(stated).toContain(LEGACY_MISSING);
      expect(stated).withContext('all three, none withheld').toHaveSize(3);

      // Each beside its own control, and still refused.
      expect(messagesFor(CONTROL_ID.confirmPassword)).toEqual([LEGACY_MISMATCH]);
      expect(messagesFor(CONTROL_ID.newPassword)).toEqual([LEGACY_POLICY_STATEMENT]);
      expect(messagesFor(CONTROL_ID.currentPassword)).toEqual([LEGACY_MISSING]);
      expect(outstandingRequestCount()).toBe(0);
    });

    it('withholds the must-differ rule while the policy is breached, as legacy could never show both', () => {
      // The one deliberate suppression. A replacement equal to the credential in force AND too short breaks
      // L278 and L290 together; legacy exited at L278 so L290 was unreachable. Telling an operator that a
      // value must change to satisfy the policy AND must change to differ is one instruction twice, so the
      // must-differ rule is withheld. This cannot turn a rejection into an acceptance, because L278 has
      // already contributed a rule.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SIX_CHARACTER_PASSWORD);
      press(CHANGE_HEADING);

      const stated = inlineMessages();

      expect(stated).toContain(LEGACY_POLICY_STATEMENT);
      expect(stated)
        .withContext('withheld: the replacement must change either way')
        .not.toContain(PASSWORD_UPDATE_MESSAGE['user.password.not_different']);
      expect(messagesFor(CONTROL_ID.newPassword))
        .withContext('one instruction, not the same instruction twice')
        .toEqual([LEGACY_POLICY_STATEMENT]);
      expect(outstandingRequestCount()).withContext('still refused').toBe(0);
    });
  });

  // =========================================================================
  // E. THE WRITE, AND ITS EMPTY SUCCESS RESPONSE
  // =========================================================================

  describe('the write', () => {
    it('sends exactly one request, to a relative address, and settles on an empty 204', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);

      const write = expectRequest('POST', changeUrl(7), 'the credential change');

      expect(write.request.url).toBe('/api/v1/users/7/password');
      expect(write.request.url.startsWith('/'))
        .withContext('no scheme, no host, no port')
        .toBeTrue();
      expect(write.request.url).not.toContain('//');

      const body: ChangePasswordRequest | null = write.request.body;

      expect(body).not.toBeNull();
      expect(body?.operation).toBe('change');
      expect(body?.currentPassword).toBe(CREDENTIAL_IN_FORCE);
      expect(body?.newPassword).toBe(REPLACEMENT);
      expect(body?.confirmPassword).toBe(REPLACEMENT);

      // The request carries the four agreed members and NOTHING ELSE. A body that quietly grew a member
      // would be a contract change nobody asked for, and on a credential endpoint it would be a disclosure
      // risk as well.
      expect(Object.keys(body ?? {}).sort()).toEqual([
        'confirmPassword',
        'currentPassword',
        'newPassword',
        'operation',
      ]);

      // The only endpoint in the surface that answers with no content at all.
      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
      expect(query('app-error-banner .error-banner__title'))
        .withContext('a success is not also reported as a failure')
        .toBeNull();
    });

    it('clears every credential control once the write has succeeded', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      for (const controlId of EVERY_CONTROL_ID) {
        expect(control(controlId).value)
          .withContext(`#${controlId} does not keep the value after a success`)
          .toBe('');
      }
    });

    it('never puts a credential into the rendered document', () => {
      arriveAsSelf(account(7));
      fillValidChange();

      const rendered = host();
      const spoken = `${rendered.textContent ?? ''} ${rendered.outerHTML}`;

      for (const secret of [CREDENTIAL_IN_FORCE, REPLACEMENT]) {
        expect(spoken)
          .withContext('no credential reaches the document text or any attribute')
          .not.toContain(secret);
      }

      // Not reflected as an attribute either, which is the route by which a value
      // would otherwise survive into markup a person can read or a tool can scrape.
      for (const controlId of EVERY_CONTROL_ID) {
        expect(control(controlId).getAttribute('value'))
          .withContext(`#${controlId} keeps its value off the attribute`)
          .toBeNull();
      }
    });

    it('never puts a credential into an announcement', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      const spoken = announcements()
        .map((entry) => `${entry.severity} ${entry.message}`)
        .join(' | ');

      for (const secret of [CREDENTIAL_IN_FORCE, REPLACEMENT]) {
        expect(spoken).not.toContain(secret);
      }
    });

    it('never puts a credential into the address', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);

      const write = expectRequest('POST', changeUrl(7));

      expect(write.request.urlWithParams)
        .withContext('a credential belongs in the body, never in a query parameter')
        .toBe('/api/v1/users/7/password');

      for (const secret of [CREDENTIAL_IN_FORCE, REPLACEMENT]) {
        expect(write.request.urlWithParams).not.toContain(secret);
      }

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);
    });
  });

  // The legacy outcome vocabulary lands here as `user.password.missing`, `.not_different`,
  // `.reset_failed`, `.invalid` and `.mismatch`. `Success` is not a failure and carries the component's
  // own wording, and the two password-question outcomes are unreachable and absent by design. The
  // vocabulary type is deliberately NOT declared in the user model, so nothing here imports a type that
  // does not exist.

  describe('the outcome vocabulary', () => {
    it('carries the measured wording for every reachable failure', () => {
      expect(PASSWORD_UPDATE_MESSAGE['user.password.missing']).toBe(LEGACY_MISSING);
      expect(PASSWORD_UPDATE_MESSAGE['user.password.reset_failed']).toBe(LEGACY_RESET_FAILED);
      expect(PASSWORD_UPDATE_MESSAGE['user.password.mismatch']).toBe(LEGACY_MISMATCH);
    });

    it('keeps the wording of the two double-spaced entries equivalent to the measured text', () => {
      // MIGRATION: A WHITESPACE-ONLY DIVERGENCE, REPORTED RATHER THAN ABSORBED. Two of the measured
      // resource strings separate their sentences with a DOUBLE space, and the authored vocabulary uses a
      // single space.
      expect(collapseWhitespace(PASSWORD_UPDATE_MESSAGE['user.password.not_different'])).toBe(
        collapseWhitespace(LEGACY_NOT_DIFFERENT),
      );
      expect(collapseWhitespace(PASSWORD_UPDATE_MESSAGE['user.password.invalid'])).toBe(
        collapseWhitespace(LEGACY_PASSWORD_INVALID),
      );

      expect(PASSWORD_UPDATE_MESSAGE['user.password.not_different'].endsWith('.'))
        .withContext('the absent trailing full stop is preserved')
        .toBeFalse();
      expect(PASSWORD_UPDATE_MESSAGE['user.password.mismatch'].endsWith('.'))
        .withContext('and here too')
        .toBeFalse();
    });

    it('resolves a code by its string, case-insensitively', () => {
      expect(passwordUpdateMessage('user.password.mismatch')).toBe(LEGACY_MISMATCH);
      expect(passwordUpdateMessage('USER.PASSWORD.MISMATCH'))
        .withContext('a server that shouts is still understood')
        .toBe(LEGACY_MISMATCH);
    });

    it('refuses to resolve an ordinal as a code', () => {
      // The whole point of keying on strings. Were an ordinal accepted, inserting a
      // member into the legacy declaration would silently re-point every message.
      for (const ordinal of ['0', '1', '2', '3', '4', '5', '6', '7']) {
        expect(passwordUpdateMessage(ordinal))
          .withContext(`the ordinal ${ordinal} is not a key`)
          .toBeNull();
      }

      expect(passwordUpdateMessage('PasswordMismatch'))
        .withContext('nor is the legacy member name')
        .toBeNull();
      expect(passwordUpdateMessage(null)).toBeNull();
      expect(passwordUpdateMessage(undefined)).toBeNull();
    });

    it('treats success as an outcome rather than as a failure code', () => {
      expect(passwordUpdateMessage('user.password.success')).toBeNull();

      // Widened to strings deliberately.
      expect(PASSWORD_UPDATE_CODES.map((code) => String(code))).not.toContain(
        'user.password.success',
      );
    });

    it('declares no code for the question-and-answer outcomes, because they are unreachable', () => {
      // ASSERTED BY ABSENCE, which is the only honest way to assert unreachability without inventing a path
      // to it. `InvalidPasswordAnswer` and `InvalidPasswordQuestion` were raised by the legacy
      // question-and-answer panel.
      const everyMessage = PASSWORD_UPDATE_CODES.map((code) => PASSWORD_UPDATE_MESSAGE[code]);

      expect(everyMessage).not.toContain(LEGACY_INVALID_ANSWER);
      expect(everyMessage).not.toContain(LEGACY_INVALID_QUESTION);

      for (const code of PASSWORD_UPDATE_CODES) {
        expect(code)
          .withContext('no code speaks of an answer')
          .not.toContain('answer');
        expect(code)
          .withContext('no code speaks of a question')
          .not.toContain('question');
      }
    });

    it('surfaces a failure the server reports by code', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(
        problemDocument(
          500,
          'An unexpected error occurred while processing the request.',
          'user.password.reset_failed',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(query('app-error-banner')).withContext('reported').not.toBeNull();
      expect(announcements()).withContext('and nothing succeeded').toEqual([]);
    });
  });

  // =========================================================================
  // G. THE SENTINELS
  // =========================================================================

  /**
   * The measured label as the shared field actually renders it. MIGRATION: THE LEGACY TRAILING COLON IS
   * DROPPED, AND THAT IS THE SHARED FIELD'S DOING RATHER THAN THIS SCREEN'S. Every legacy label carried
   * one — `plLastChanged` is 'Password Last Changed:' in the resource file — because the legacy control
   * emitted the caption and the colon together as literal text.
   */
  function renderedLabel(measured: string): string {
    return measured.replace(/\s*:$/, '');
  }

  /** The value shown by the field carrying a given label. */
  function statedValue(label: string): string {
    const caption = renderedLabel(label);

    for (const field of queryAll('app-form-field')) {
      if (textOf(field.querySelector('.form-field__label')).includes(caption)) {
        return textOf(field.querySelector('.user-password__value'));
      }
    }

    throw new Error(`Expected a field labelled "${caption}" in the rendered template.`);
  }

  describe('the sentinels', () => {
    it('shows nothing at all for the null-date sentinel', () => {
      // For LAST CHANGED this is PARITY rather than a divergence: the legacy display helper already
      // answered with the empty string for the null date, so an empty field is exactly what a person saw.
      // It is asserted, and it is NOT reported as a behavioural difference, because it is not one.
      arrive(account(7, { lastPasswordChangeDate: NULL_DATE_ON_THE_WIRE }));

      const stated = statedValue(LAST_CHANGED_LABEL);

      expect(stated).withContext('empty, not a date in the year one').toBe('');
      expect(stated).not.toContain('0001');
      expect(stated).not.toContain('01/01/0001');
    });

    it('shows nothing for an explicitly null date', () => {
      arrive(account(7, { lastPasswordChangeDate: null }));

      expect(statedValue(LAST_CHANGED_LABEL)).toBe('');
    });

    it('refuses a payload whose date member is missing entirely', () => {
      // THE "PRESENT BUT POSSIBLY NULL, NEVER OMITTED" GUARANTEE, ENFORCED RATHER THAN ASSUMED. The API
      // serialises with its ignore condition set to never, so every member is always on the wire and a null
      // is expressed as a null.
      arrive(account(7, { lastPasswordChangeDate: undefined }));

      expect(query('.user-password__summary'))
        .withContext('a payload that breaks the contract is not presented')
        .toBeNull();
      expect(query(`#${CONTROL_ID.newPassword}`))
        .withContext('and no form is offered over an account that did not decode')
        .toBeNull();
    });

    it('refuses a date it cannot read, so the words Invalid Date are unreachable', () => {
      arrive(account(7, { lastPasswordChangeDate: 'not-a-date-at-all' }));

      const rendered = textOf(host());

      expect(rendered).not.toContain('Invalid Date');
      expect(rendered).not.toContain('NaN');
      expect(rendered)
        .withContext('and the unreadable value itself is not echoed either')
        .not.toContain('not-a-date-at-all');
      expect(query('.user-password__summary')).toBeNull();
    });

    it('shows the measured no-expiry wording rather than a bare zero', () => {
      arrive(account(7, { mustChangePassword: false }));

      const stated = statedValue(EXPIRES_LABEL);

      expect(stated).toBe(LEGACY_NO_EXPIRY);
      expect(stated).withContext('never a bare zero').not.toBe('0');
    });

    it('shows the measured forced-change wording when a change is required', () => {
      arrive(account(7, { mustChangePassword: true }));

      expect(statedValue(EXPIRES_LABEL)).toBe(LEGACY_FORCED_EXPIRY);
    });

    it('treats a zero account identifier as a real address', () => {
      // DEFENSIVE, AND THE NUANCE IS WORTH STATING. `Users.UserID` is `IDENTITY(1,1)`, so zero is not a
      // naturally occurring account identifier in this schema.
      seatIdentity(99, ['Administrators'], true);
      arrive(account(0));

      expect(query('app-page-header')).withContext('the screen renders').not.toBeNull();

      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(RESET_HEADING);
      answerDialog(RESET_HEADING);

      const write = expectRequest('POST', resetUrl(0));

      expect(write.request.url)
        .withContext('zero is addressed, not discarded and not defaulted')
        .toBe('/api/v1/users/0/password-reset');
      expect(write.request.url).not.toContain('/users/-1/');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(0);
    });

    it('treats the negative integer sentinel as a real address too', () => {
      // -1 is simultaneously `NullInteger` and a legitimate identifier, so it must be
      // addressed rather than read as "absent".
      seatIdentity(99, ['Administrators'], true);
      arrive(account(NULL_INTEGER));

      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(RESET_HEADING);
      answerDialog(RESET_HEADING);

      const write = expectRequest('POST', resetUrl(NULL_INTEGER));

      expect(write.request.url).toBe('/api/v1/users/-1/password-reset');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(NULL_INTEGER);
    });

    it('makes no request at all for an unreadable route value', () => {
      fixture.componentRef.setInput('userId', 'not-a-number');
      fixture.detectChanges();

      expect(outstandingRequestCount())
        .withContext('an address that cannot be resolved is not guessed at')
        .toBe(0);
      expect(query(`#${CONTROL_ID.newPassword}`))
        .withContext('and no form is offered for an account that was never identified')
        .toBeNull();
    });
  });

  describe('the confirmation that gates an administrative reset', () => {
    function requestReset(held: UserDetail = account(7)): void {
      arriveAsAdministrator(held);
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(RESET_HEADING);
    }

    it('is absent until the reset is asked for', () => {
      arriveAsAdministrator(account(7));

      expect(query('app-confirm-dialog'))
        .withContext('presence is open, so it must not be mounted early')
        .toBeNull();
      expect(query('.confirm-dialog')).toBeNull();

      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);

      expect(query('app-confirm-dialog'))
        .withContext('typing is not asking')
        .toBeNull();

      press(RESET_HEADING);

      expect(query('app-confirm-dialog')).withContext('and now it is asked').not.toBeNull();
      expect(outstandingRequestCount())
        .withContext('asking is not doing')
        .toBe(0);

      // Dismissed before leaving, deliberately. The confirmation promotes a NATIVE dialog, whose top-layer
      // and inert state belong to the Karma document rather than to this fixture, so a case that walked
      // away from an open one would leave the whole page modal for whichever case Jasmine ran next.
      answerDialog(DISMISS_LABEL);

      expect(query('app-confirm-dialog')).withContext('left closed').toBeNull();
    });

    it('names the account it is about, and claims nothing beyond that', () => {
      requestReset(account(7, { username: 'ada.lovelace' }));

      const message = textOf(query('.confirm-dialog__message'));

      expect(message).withContext('the account is named').toContain('ada.lovelace');

      // A confirmation justifies gating an action; it does not license overstating it. Resetting a
      // credential is reversible — another reset replaces it — and the only legacy wording promising
      // irreversibility belongs to the recycle bin, which is not ported.
      for (const overclaim of FORBIDDEN_OVERCLAIMS) {
        expect(message.toLowerCase())
          .withContext(`the confirmation does not claim "${overclaim}"`)
          .not.toContain(overclaim);
      }

      answerDialog(DISMISS_LABEL);
    });

    it('sends exactly one request when it is agreed to', () => {
      requestReset();
      answerDialog(RESET_HEADING);

      const write = expectRequest('POST', resetUrl(7), 'the reset');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements())
        .withContext('one agreement, one write, one announcement')
        .toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('sends nothing when it is dismissed, and keeps what was typed', () => {
      requestReset();
      answerDialog(DISMISS_LABEL);

      expect(query('app-confirm-dialog')).withContext('it closes').toBeNull();
      // Teardown verifies there is no outstanding traffic, so a request leaked here
      // would fail this case even without the explicit count below.
      expect(outstandingRequestCount()).withContext('nothing is sent').toBe(0);
      expect(control(CONTROL_ID.newPassword).value)
        .withContext('and the entry survives, so it need not be retyped')
        .toBe(REPLACEMENT);
      expect(announcements()).toEqual([]);
    });

    it('can be asked again after being dismissed', () => {
      requestReset();
      answerDialog(DISMISS_LABEL);

      expect(query('app-confirm-dialog')).toBeNull();

      press(RESET_HEADING);

      expect(query('app-confirm-dialog')).withContext('asked a second time').not.toBeNull();

      answerDialog(RESET_HEADING);
      expectRequest('POST', resetUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('offers only non-submitting controls, so neither can post the form', () => {
      requestReset();

      const offered = buttons().filter((candidate) =>
        candidate.classList.contains('confirm-dialog__button'),
      );

      expect(offered.length).withContext('two controls are offered').toBe(2);

      for (const candidate of offered) {
        expect(candidate.getAttribute('type'))
          .withContext(`"${textOf(candidate)}" cannot submit anything`)
          .toBe('button');
      }

      answerDialog(DISMISS_LABEL);
    });

    it('re-checks the rules when it is agreed to, not only when it is asked', () => {
      // The value can change while the question is on screen, so agreeing re-evaluates
      // rather than trusting the check made when the dialog opened.
      requestReset();
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);
      answerDialog(RESET_HEADING);

      expect(outstandingRequestCount()).withContext('nothing is sent').toBe(0);
      expect(inlineMessages()).toContain(LEGACY_MISMATCH);
    });

    it('is never raised for the account holder, whose write needs no confirmation', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);

      expect(query('app-confirm-dialog'))
        .withContext('a change of ones own credential is authorised by the credential itself')
        .toBeNull();

      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);
    });
  });

  // =========================================================================
  // I. STRUCTURE AND ACCESSIBILITY
  // =========================================================================

  describe('structure and accessibility', () => {
    it('wraps every credential control in a field and associates its label', () => {
      arriveAsSelf(account(7));

      for (const controlId of EVERY_CONTROL_ID) {
        const element = control(controlId);
        const enclosingField = element.closest('app-form-field');

        expect(enclosingField)
          .withContext(`#${controlId} is presented through the shared field`)
          .not.toBeNull();

        const label = requireElement(host(), `label[for="${controlId}"]`);

        expect(textOf(label))
          .withContext(`the label for #${controlId} says something`)
          .not.toBe('');
        expect(host().querySelector(`#${label.getAttribute('for') ?? ''}`))
          .withContext(`the association from the label for #${controlId} resolves`)
          .not.toBeNull();
      }
    });

    it('leaves no label associated with a control that does not exist', () => {
      // The general form of the same defect: a dangling association anywhere on the
      // screen, not only on the three controls above.
      arriveAsSelf(account(7));

      for (const label of queryAll('label')) {
        const associated = label.getAttribute('for');

        if (associated === null) {
          continue;
        }

        expect(associated).withContext('an empty association is a dangling one').not.toBe('');
        expect(host().querySelector(`#${associated}`))
          .withContext(`the label associating with "${associated}" resolves to a control`)
          .not.toBeNull();
      }
    });

    it('tells the browser which credential each control holds', () => {
      // Without these a password manager offers the wrong value, and a person retypes a
      // credential by hand — which is both a usability and a security regression.
      arriveAsSelf(account(7));

      expect(control(CONTROL_ID.currentPassword).getAttribute('autocomplete')).toBe(
        'current-password',
      );
      expect(control(CONTROL_ID.newPassword).getAttribute('autocomplete')).toBe('new-password');
      expect(control(CONTROL_ID.confirmPassword).getAttribute('autocomplete'))
        .withContext('the confirmation is a new credential too, not a current one')
        .toBe('new-password');
    });

    it('captions each read-only fact with the measured wording, less the legacy colon', () => {
      // The wording is the resource file's; the trailing colon is not carried across, because the caption
      // contributes to a control's accessible name and punctuation announced as part of a name is noise.
      // The divergence is punctuation only, and it is pinned here so it stays punctuation only.
      arriveAsSelf(account(7));

      const captions = queryAll('.form-field__label').map((node) => textOf(node));

      for (const measured of [LAST_CHANGED_LABEL, EXPIRES_LABEL]) {
        expect(measured.endsWith(':'))
          .withContext('the measured resource value really did carry a colon')
          .toBeTrue();
        expect(captions.some((caption) => caption.includes(renderedLabel(measured))))
          .withContext(`"${renderedLabel(measured)}" is captioned`)
          .toBeTrue();
        expect(captions)
          .withContext('and not with the legacy punctuation still attached')
          .not.toContain(measured);
      }
    });

    it('masks every credential control', () => {
      arriveAsSelf(account(7));

      for (const controlId of EVERY_CONTROL_ID) {
        expect(control(controlId).getAttribute('type'))
          .withContext(`#${controlId} is masked`)
          .toBe('password');
      }
    });

    it('captions its one section with the operation the submit will actually perform', () => {
      arriveAsAdministrator(account(7));

      const adminGroups = queryAll('.user-password__group');

      expect(adminGroups.length)
        .withContext('one section, holding the boxes and the command together')
        .toBe(1);

      const adminCaptions = queryAll('legend').map((node) => textOf(node));

      expect(adminCaptions).toEqual([RESET_HEADING]);
      expect(adminCaptions)
        .withContext('an operation this caller is not authorised for is not captioned')
        .not.toContain(CHANGE_HEADING);

      // The command lives INSIDE that section, which is where the legacy put it. A command outside
      // every section is what left a captioned section holding no control.
      expect(adminGroups[0]?.querySelector('button[type="submit"]'))
        .withContext('the command is inside the section its caption names')
        .not.toBeNull();
      expect(textOf(adminGroups[0]?.querySelector('button[type="submit"]') ?? null))
        .withContext('and it names the same operation as the caption')
        .toBe(RESET_HEADING);

      // Every grouping element is captioned, which is the accessibility half of the original test.
      for (const group of queryAll('fieldset')) {
        expect(textOf(group.querySelector('legend')))
          .withContext('every group is captioned')
          .not.toBe('');
      }
    });

    it('captions the section for the account holder with the change operation instead', () => {
      arriveAsSelf(account(7));

      const captions = queryAll('legend').map((node) => textOf(node));

      expect(captions).toEqual([CHANGE_HEADING]);
      expect(captions)
        .withContext('the account holder is not offered an administrative reset')
        .not.toContain(RESET_HEADING);
      expect(queryAll('.user-password__group').length).toBe(1);
    });

    it('puts no interactive control out of the keyboard order', () => {
      // REVERSING THE SECOND LEGACY DEFECT. The legacy section head placed its expand and collapse
      // affordance in `sectionheadcontrol.ascx` L3 with `tabIndex="-1"`, so the only control that could
      // reveal a collapsed section was unreachable by keyboard. Nothing on this screen may repeat that.
      arriveAsAdministrator(account(7));

      for (const element of queryAll(
        'button, input, a, summary, select, textarea, [role="button"][tabindex], [contenteditable="true"]',
      )) {
        expect(element.getAttribute('tabindex'))
          .withContext(`<${element.tagName.toLowerCase()}> stays in the keyboard order`)
          .not.toBe('-1');
      }
    });

    it('gives the submit control an accessible name that says what it will do', () => {
      arriveAsSelf(account(7));

      expect(textOf(buttonLabelled(CHANGE_HEADING)))
        .withContext('the account holder is offered a change')
        .toBe(CHANGE_HEADING);
      expect(buttonLabelled(RESET_HEADING))
        .withContext('and is not offered a reset')
        .toBeNull();
    });

    it('does not duplicate the ARIA the shared components already own', () => {
      arriveAsAdministrator(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(RESET_HEADING);

      const dialog = requireElement(host(), 'app-confirm-dialog');

      expect(dialog.getAttribute('role'))
        .withContext('the host element adds no role of its own')
        .toBeNull();
      expect(dialog.getAttribute('aria-live')).toBeNull();
      expect(dialog.getAttribute('aria-busy')).toBeNull();

      // The role the confirmation itself declares is present, one level in.
      expect(query('.confirm-dialog')?.getAttribute('role')).toBe('alertdialog');

      answerDialog(DISMISS_LABEL);
    });
  });

  // =========================================================================
  // J. THE FAILURE AND PROGRESS SURFACES
  // =========================================================================

  describe('the failure and progress surfaces', () => {
    /** Submits a valid change and refuses it with the given document. */
    function refuse(
      status: number,
      statusText: string,
      detail: string,
      code: string | null = null,
      errors?: Readonly<Record<string, readonly string[]>>,
    ): void {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(problemDocument(status, detail, code, errors), {
        status,
        statusText,
      });
      fixture.detectChanges();
    }

    it('reports a refusal of authority through the shared error surface', () => {
      refuse(
        403,
        'Forbidden',
        'The authenticated caller is not permitted to perform this operation.',
        'user.password.change_self_only_forbidden',
      );

      const banner = requireElement(host(), 'app-error-banner');

      expect(textOf(banner)).toContain(
        'The authenticated caller is not permitted to perform this operation.',
      );
    });

    it('lets the error surface decide how loudly to speak, and does not overrule it', () => {
      refuse(
        403,
        'Forbidden',
        'The authenticated caller is not permitted to perform this operation.',
        'user.password.change_self_only_forbidden',
      );

      expect(textOf(query('.error-banner__severity'))).toBe('Warning');
    });

    it('raises no announcement for a refusal the surface has already reported', () => {
      // One failure, one report. An announcement in addition to the banner would say the
      // same thing twice, in two places, for one event.
      refuse(
        403,
        'Forbidden',
        'The authenticated caller is not permitted to perform this operation.',
        'user.password.change_self_only_forbidden',
      );

      expect(announcements())
        .withContext('the banner is the report; nothing is announced alongside it')
        .toEqual([]);
    });

    it('pins a per-field refusal to the control the server named', () => {
      // The keys are .NET model-state names, so they arrive in PascalCase rather than camelCase. Bracket
      // access is used because index-signature access by dot notation is disallowed by this workspace's
      // compiler settings.
      const errors: Readonly<Record<string, readonly string[]>> = {
        NewPassword: ['That replacement does not meet this site policy.'],
      };

      refuse(
        400,
        'Bad Request',
        'The request could not be processed as submitted.',
        'user.password.invalid',
        errors,
      );

      expect(errors['NewPassword'])
        .withContext('the fixture really is keyed the way the server keys it')
        .toEqual(['That replacement does not meet this site policy.']);
      expect(inlineMessages()).toContain('That replacement does not meet this site policy.');
      expect(control(CONTROL_ID.newPassword).getAttribute('aria-invalid')).toBe('true');
    });

    it('matches a server field name without depending on its casing', () => {
      const errors: Readonly<Record<string, readonly string[]>> = {
        newPassword: ['A camel-cased key is understood too.'],
      };

      refuse(
        400,
        'Bad Request',
        'The request could not be processed as submitted.',
        'user.password.invalid',
        errors,
      );

      expect(inlineMessages()).toContain('A camel-cased key is understood too.');
    });

    it('shows a per-field message beside its control and not also in the summary', () => {
      // The contract between the two surfaces is explicit: the banner summarises, and
      // the field states. A message rendered in both places is one event reported twice.
      const message = 'That replacement does not meet this site policy.';
      const errors: Readonly<Record<string, readonly string[]>> = { NewPassword: [message] };

      refuse(
        400,
        'Bad Request',
        'The request could not be processed as submitted.',
        'user.password.invalid',
        errors,
      );

      expect(inlineMessages()).withContext('stated beside the control').toContain(message);

      const summarised = queryAll('.error-banner__field-message').map((node) => textOf(node));

      expect(summarised)
        .withContext('and not repeated as its own summary line')
        .not.toContain(message);
    });

    it('shows no progress indicator before anything has been submitted', () => {
      arriveAsSelf(account(7));
      fillValidChange();

      expect(query('app-loading-spinner'))
        .withContext('nothing is in flight, so nothing indicates progress')
        .toBeNull();
    });

    it('shows a progress indicator while the write is in flight, and withdraws it after', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);

      const write = expectRequest('POST', changeUrl(7));

      fixture.detectChanges();

      expect(query('app-loading-spinner'))
        .withContext('the write is outstanding, so progress is indicated')
        .not.toBeNull();
      expect(buttonLabelled(CHANGE_HEADING)?.disabled)
        .withContext('and the control cannot be pressed twice')
        .toBeTrue();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(query('app-loading-spinner'))
        .withContext('and it is withdrawn once the write has settled')
        .toBeNull();
    });

    it('releases the form after a refusal so the entry can be corrected and retried', () => {
      refuse(
        500,
        'Internal Server Error',
        'An unexpected error occurred while processing the request.',
        'user.password.reset_failed',
      );

      expect(buttonLabelled(CHANGE_HEADING)?.disabled)
        .withContext('the control is released')
        .toBeFalse();
      expect(control(CONTROL_ID.newPassword).value)
        .withContext('and nothing typed was thrown away')
        .toBe(REPLACEMENT);

      enter(CONTROL_ID.newPassword, OTHER_REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7), 'the retry').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
    });

    it('announces a success once, at the success severity, in the measured wording', () => {
      arriveAsSelf(account(7));
      fillValidChange();
      press(CHANGE_HEADING);
      expectRequest('POST', changeUrl(7)).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterWrite(7);

      expect(announcements()).toEqual([{ severity: 'success', message: LEGACY_PASSWORD_CHANGED }]);
      expect(LEGACY_PASSWORD_CHANGED)
        .withContext('PasswordChanged.Text, because no Success.Text key exists')
        .toBe('The password has been reset.');
    });

    it('reports the shared reference a support request needs', () => {
      refuse(
        500,
        'Internal Server Error',
        'An unexpected error occurred while processing the request.',
        'user.password.reset_failed',
      );

      expect(textOf(query('.error-banner__trace'))).toContain(
        'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
      );
    });

    it('reports no failure at all before anything has failed', () => {
      arriveAsSelf(account(7));

      expect(query('app-error-banner .error-banner__title'))
        .withContext('an untouched screen reports nothing')
        .toBeNull();
      expect(inlineMessages()).toEqual([]);
      expect(announcements()).toEqual([]);
    });
  });

  describe('the mandatory-remediation visit', () => {
    /** Seats a caller who owes a mandatory credential change on their own account. */
    function seatRemediatingIdentity(userId: number): void {
      TestBed.inject(TokenStorageService).store({
        accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
        expiresAtUtc: '2099-12-31T23:59:59.000Z',
        refreshToken: 'not-a-real-refresh-token',
        mustChangePassword: true,
        mustUpdateProfile: false,
        passwordExpiring: false,
        user: {
          userId,
          portalId: NULL_INTEGER,
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
     * Arrives on the screen as a remediating caller.
     *
     * @param userId The account, which is also the caller.
     */
    function arriveRemediating(userId = 7): void {
      seatRemediatingIdentity(userId);
      fixture.componentRef.setInput('userId', String(userId));
      fixture.detectChanges();
    }

    // -------------------------------------------------------------------------------------------------
    // THE LANDING IS EXPLAINED
    // -------------------------------------------------------------------------------------------------

    // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. A caller carrying a mandatory credential change was moved
    // onto this screen from wherever they asked to go, and the screen presented itself as an ordinary
    // Manage Password form. Nothing on it said the rest of the site was waiting on this one action.

    it('explains the landing when the caller was moved here to satisfy an obligation', () => {
      arriveRemediating(7);

      const explanation = query('.user-password__remediation');

      expect(explanation)
        .withContext('the reason for the landing is stated on the screen the caller was sent to')
        .not.toBeNull();
      expect(textOf(explanation))
        .withContext('and it is the authored sentence, not a paraphrase assembled in the template')
        .toBe(CREDENTIAL_REMEDIATION_EXPLANATION);
      expect(CREDENTIAL_REMEDIATION_EXPLANATION)
        .withContext('which names what to do rather than describing a permanent condition')
        .toContain('save');
    });

    it('does not announce the explanation, because the redirect was announced once already', () => {
      arriveRemediating(7);

      const explanation = query('.user-password__remediation');

      expect(explanation?.getAttribute('role'))
        .withContext('a standing explanation, marked as such and not as a live status')
        .toBe('note');
      expect(explanation?.getAttribute('aria-live'))
        .withContext('and it carries no politeness setting of its own')
        .toBeNull();
      expect(announcements())
        .withContext('and the screen announces nothing of its own on arrival')
        .toEqual([]);
    });

    it('says nothing about an obligation to a caller who has none', () => {
      arriveAsSelf(account(7));

      expect(query('.user-password__remediation'))
        .withContext('an operator changing their own password by choice is told nothing about a requirement')
        .toBeNull();
    });

    it('issues NO account read, because the API refuses that read in this state', () => {
      arriveRemediating(7);

      // Counted rather than asserted through `expectNone`, which throws and therefore records no
      // expectation of its own: the emptiness of what `match` returns IS the claim. The predicate names the
      // one address the API refuses in this state, so the teardown's `verify()` still guards the rest.
      expect(httpMock.match(accountUrl(7)))
        .withContext('the account read the API refuses in this state is never attempted')
        .toEqual([]);
    });

    it('offers the credential form even though no account details were read', () => {
      arriveRemediating(7);

      expect(query(`#${CONTROL_ID.newPassword}`))
        .withContext('the form is what the screen is FOR, and the write needs no account read')
        .not.toBeNull();
      expect(query(`#${CONTROL_ID.confirmPassword}`)).not.toBeNull();
      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('a self-service change presents the credential in force')
        .not.toBeNull();
    });

    it('omits the two summary rows the refused read would have populated', () => {
      arriveRemediating(7);

      // Rendering them from an absent account would present the empty string and the
      // never-changed sentinel as though they were facts about the credential.
      expect(query('.user-password__summary')).toBeNull();
    });

    it('leaves the submit affordance available', () => {
      arriveRemediating(7);

      const submit = host().querySelector('button[type="submit"]');

      expect(submit)
        .withContext('a mandatory screen must offer the action it exists to perform')
        .not.toBeNull();
      expect((submit as HTMLButtonElement).disabled)
        .withContext('this was permanently true while the affordance waited on a refused read')
        .toBe(false);
    });

    it('still withholds the form when a read was issued and did not decode', () => {
      arrive(account(7, { lastPasswordChangeDate: undefined }));

      expect(query(`#${CONTROL_ID.newPassword}`)).toBeNull();
      expect(query('.user-password__summary')).toBeNull();
    });

    it('clears the advisory locally and returns to the root once the change is written', async () => {
      const router = TestBed.inject(Router);
      const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);

      arriveRemediating(7);
      fillValidChange();
      press(CHANGE_HEADING);

      expectRequest('POST', changeUrl(7), 'the credential change').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      await fixture.whenStable();

      httpMock.expectNone('/api/v1/auth/refresh');

      // ⚠ AND NOW THE DECLINED READ IS ISSUED, which is the other half of the same rule. The advisory is
      // gone from the held session, so the condition that suppressed the account read no longer holds and
      // the screen asks for the details it had been refused.
      expectRequest('GET', accountUrl(7), 'the read the screen had been declining').flush({
        data: account(7),
        meta: null,
      });
      fixture.detectChanges();

      expect(query('.user-password__summary'))
        .withContext('and the two summary rows appear as soon as they are readable')
        .not.toBeNull();

      // THE ROOT, NOT A SCREEN. Naming a screen here would put the both-advisories-outstanding precedence
      // in a second place; the root redirect owns that decision alone. ⚠ REPLACES: a completed credential
      // change must not sit in BACK history, and the unsaved-changes gate reads this flag to recognise an
      // application-initiated departure.
      expect(navigate)
        .withContext('the caller is handed to the root, which decides where remediation leads next')
        .toHaveBeenCalledWith('/', { replaceUrl: true });
    });

    // ⚠ THE SAFE EXIT, WHICH THIS SCREEN DID NOT HAVE EITHER. The legacy credential page declared three saving
    // commands - change, reset and the question-and-answer save - and no cancel, so an operator who opened it by
    // mistake, or who typed a credential they then thought better of, had only the browser's own controls. Added
    // rather than ported, and recorded as such.
    it('offers a way out, and where it leads depends on who is on the screen', () => {
      const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);

      arriveAsAdministrator();

      const cancel = buttonLabelled('Cancel');

      expect(cancel).withContext('the screen offers an abandon command').not.toBeNull();
      expect(cancel?.type)
        .withContext('it cannot submit, so no validator runs on the way out')
        .toBe('button');

      press('Cancel');

      // An administrator reached this form from the account editor, so that is where leaving returns them - and
      // WITHOUT replacing the address, so the unsaved-entry gate sees a departure it is entitled to question.
      expect(navigate).toHaveBeenCalledOnceWith('/users/7');
      expect(httpMock.match(() => true))
        .withContext('and nothing is written on the way out')
        .toHaveSize(0);
    });

    it('hands a caller changing their own credential back to the root', () => {
      const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);

      arriveAsSelf();

      press('Cancel');

      expect(navigate).toHaveBeenCalledOnceWith('/');
    });

    it('marks the saving command as the primary one, as the sibling editors do', () => {
      arriveAsAdministrator();

      const submit = buttons().find((candidate) => candidate.type === 'submit');

      expect(submit?.classList)
        .withContext('the shared primary treatment')
        .toContain('form-action--primary');
      expect(buttonLabelled('Cancel')?.classList)
        .withContext('and the safe exit is deliberately not primary')
        .not.toContain('form-action--primary');
    });

    it('leaves a profile completion outstanding when the account owes both', async () => {
      const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
      const storage = TestBed.inject(TokenStorageService);

      seatRemediatingIdentity(7);
      const held = storage.session();
      storage.store({ ...held!, mustUpdateProfile: true });
      fixture.componentRef.setInput('userId', '7');
      fixture.detectChanges();

      fillValidChange();
      press(CHANGE_HEADING);

      expectRequest('POST', changeUrl(7), 'the credential change').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      await fixture.whenStable();

      const renewed = storage.session();

      expect(renewed?.mustChangePassword)
        .withContext('the satisfied advisory is cleared')
        .toBe(false);
      expect(renewed?.mustUpdateProfile)
        .withContext('and the one the caller still owes is untouched')
        .toBe(true);
      expect(navigate).toHaveBeenCalledWith('/', { replaceUrl: true });

      // The session is still restricted by the remaining advisory, so the account read stays
      // withheld and nothing further is issued.
      httpMock.expectNone(accountUrl(7));
    });

    it('concludes nothing after an ADMINISTRATIVE reset, which clears no advisory of the caller\u2019s own', async () => {
      // An administrator who owes their own credential change cannot reach this path anyway — the server's
      // allowance requires the route's account to BE the caller — but the guard is stated at this end too,
      // because clearing an advisory here would assert something the server never reported about the
      // administrator's own account.
      const router = TestBed.inject(Router);
      const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);

      arriveAsAdministrator(account(7));
      enter(CONTROL_ID.newPassword, REPLACEMENT);
      enter(CONTROL_ID.confirmPassword, REPLACEMENT);
      press(RESET_HEADING);
      answerDialog(RESET_HEADING);

      expectRequest('POST', resetUrl(7), 'the administrative reset').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      settleAfterWrite(7);
      await fixture.whenStable();

      httpMock.expectNone('/api/v1/auth/refresh');
      expect(navigate).not.toHaveBeenCalled();
    });
  });

  // ==========================================================================
  //  #25 — WHAT A CREDENTIAL FORM OWES A PASSWORD MANAGER AND A KEYBOARD
  // ==========================================================================
  describe('the account the credential belongs to, and where a refusal takes the reader', () => {
    it('carries a read-only account field a password manager can attribute the credential to', () => {
      arriveAsAdministrator(account(7));

      const field = query('#user-password-username');

      expect(field).withContext('the account is stated in the form, not only in the heading').not.toBeNull();

      const input = field as HTMLInputElement;

      expect(input.getAttribute('autocomplete'))
        .withContext('the token a manager reads to attribute what it saves')
        .toBe('username');
      expect(input.readOnly)
        .withContext('read-only, so it informs a manager without inviting an edit')
        .toBeTrue();
      expect(input.value).toBe(account(7).username);
      expect(input.getAttribute('formcontrolname'))
        .withContext('and it is not part of the form model, so the request shape is unchanged')
        .toBeNull();
    });

    it('marks the confirming control invalid when it differs, so the refusal is reachable', () => {
      arriveAsSelf(account(7));

      enter('currentPassword', 'Existing-1');
      enter('newPassword', REPLACEMENT);
      enter('confirmPassword', `${REPLACEMENT}-different`);

      press('Change Password');

      const confirmation = control('confirmPassword');

      expect(confirmation.classList.contains('ng-invalid'))
        .withContext('the control itself is invalid, not only the group around it')
        .toBeTrue();
      expect(confirmation.getAttribute('aria-invalid'))
        .withContext('so assistive technology hears it on the control the reader must correct')
        .toBe('true');
      expect(document.activeElement)
        .withContext('and focus is taken there rather than left on the submit')
        .toBe(confirmation);
    });

    it('clears the mismatch when the replacement is corrected to agree with the confirmation', () => {
      arriveAsSelf(account(7));

      enter('currentPassword', 'Existing-1');
      enter('newPassword', REPLACEMENT);
      enter('confirmPassword', `${REPLACEMENT}x`);

      expect(control('confirmPassword').classList.contains('ng-invalid')).toBeTrue();

      // Correcting the OTHER field is what used to leave the message stranded: a control's validators run
      // when that control changes and at no other time.
      enter('newPassword', `${REPLACEMENT}x`);

      expect(control('confirmPassword').classList.contains('ng-invalid'))
        .withContext('the refusal follows the values rather than the keystrokes')
        .toBeFalse();
    });

    it('reports an empty confirmation as the omission it is', () => {
      arriveAsSelf(account(7));

      enter('currentPassword', 'Existing-1');
      enter('newPassword', REPLACEMENT);

      press('Change Password');

      const confirmation = control('confirmPassword');

      expect(confirmation.value).toBe('');
      expect(confirmation.classList.contains('ng-invalid'))
        .withContext('an empty confirmation is a refusal on the confirming control')
        .toBeTrue();
      expect(document.activeElement).toBe(confirmation);
    });
  });
});
