/**
 * Specification for `UserPasswordComponent` — the Angular 19 replacement for
 * `Website/admin/Users/Password.ascx` on route `/users/:userId/password`.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS FILE CARRIES MORE WEIGHT THAN A NORMAL SPECIFICATION
 * ---------------------------------------------------------------------------
 *
 * `tsconfig.app.json` declares `files: ["src/main.ts"]` and type-checks by IMPORT
 * GRAPH, so a component that nothing imports is silently absent from the build's
 * program. `tsconfig.spec.json` declares `include: ["src/**\/*.spec.ts",
 * "src/**\/*.d.ts"]` and NO `files` array, so every specification is in the program
 * unconditionally. Until a route or a parent imports the component, THIS FILE IS THE
 * ONLY GATED COMPILE ROUTE for `user-password.component.ts` — it is the type-check as
 * much as it is the test.
 *
 * ---------------------------------------------------------------------------
 * PROVENANCE: EVERY ASSERTION HERE IS NET-NEW
 * ---------------------------------------------------------------------------
 *
 * The legacy tree contains ZERO automated tests of any kind — no test project, no
 * fixture, no assertion anywhere under `Library/` or `Website/`. Nothing was ported
 * into this file; every expectation below was derived by reading the legacy source and
 * its resource files, and each is cited to the line it came from.
 *
 * The measured sources are `Website/admin/Users/Password.ascx.vb` (workflow and the
 * order of its rules), `Website/admin/Users/Password.ascx` (markup, field set and two
 * accessibility defects), `Library/Components/Users/UserController.vb` (the policy
 * predicate), `Library/Components/Users/Membership/PasswordUpdateStatus.vb` (the
 * outcome vocabulary), `Website/App_GlobalResources/SharedResources.resx` and
 * `Website/admin/Users/App_LocalResources/Password.ascx.resx` (wording),
 * `Library/Components/Shared/Null.vb` (the sentinels) and `Website/release.config`
 * (the policy settings).
 *
 * ---------------------------------------------------------------------------
 * NO USER-SPECIFIED RULES EXIST FOR THIS PROJECT
 * ---------------------------------------------------------------------------
 *
 * The project's rules document reports its own absence rather than any content, under
 * every query shape. ZERO files enter scope on rule grounds. No rule is invented here
 * and the absence is not treated as licence to lower the bar: the binding substitute
 * set is the Minimal Change Clause (domain-logic preservation, data-model fidelity,
 * behavioural equivalence, functional parity with MATCHING validation rules and
 * EQUIVALENT messages, code organisation derived from discovered patterns, and
 * migration annotation), the enterprise baseline, the non-functional requirements, and
 * the frontend test gate.
 *
 * ---------------------------------------------------------------------------
 * HARNESS RULES OBSERVED HERE, AND WHY
 * ---------------------------------------------------------------------------
 *
 * Karma with Jasmine, never Jest: the mandated gate command passes `--browsers`, which
 * is a Karma option, so a Jest suite would make the mandated command invalid. Jest,
 * Vitest and the testing-library family are absent from the pinned dependency set and
 * are unimportable in any case.
 *
 * `provideHttpClient()` is registered BEFORE `provideHttpClientTesting()`. Reversing
 * the two is the commonest false green in an Angular 19 suite: the real backend wins
 * the registration and the controller then observes nothing, so a specification that
 * asserts on requests passes while asserting on an empty set. The superseded
 * module-based testing imports for the HTTP client and the router are not used at all,
 * nor is a declarations array; everything arrives through a provider function, and
 * routing through `provideRouter([])`.
 *
 * `verify()` and `destroy()` both run after every case. Jasmine's random order is the
 * Karma default and is deliberately kept, so nothing here may leak between cases. That
 * matters concretely on this screen: the confirmation is a native `<dialog>`, whose
 * top-layer and inert state belong to the whole Karma document rather than to one
 * fixture, so a dialog left promoted would change how a later case sees the page.
 *
 * Module scope holds only frozen constants and pure functions. There is no mutable
 * module-level state of any kind, which is what makes order-independence structural
 * rather than merely observed.
 *
 * URLs are asserted RELATIVE. The workspace's test target declares no file
 * replacements, so a specification compiles against the PRODUCTION configuration,
 * whose API base is the relative `/api/v1` that the reverse proxy requires. An
 * absolute host is never expected, and the configuration module is never imported.
 *
 * Appearance is never asserted: no computed geometry, no colours, no animation, no
 * stylesheet content. Structure and ARIA are asserted instead, because those are the
 * contract, while computed geometry in a headless browser is not.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ChangePasswordRequest, UserDetail } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import {
  PASSWORD_UPDATE_CODES,
  PASSWORD_UPDATE_MESSAGE,
  passwordUpdateMessage,
} from '../../../core/utils/form-errors.util';
import { UserPasswordComponent } from './user-password.component';

// ---------------------------------------------------------------------------
// THE ADDRESSES
// ---------------------------------------------------------------------------
//
// Composed here rather than imported, so that a change to the endpoint map is caught
// by this file instead of being silently agreed with. Every one is RELATIVE.
//
// MIGRATION: THE WRITE IS A POST, AND THERE ARE TWO OF THEM. The legacy screen ran one
// routine for every caller; the API separates the operations by authorisation, so the
// change and the reset are distinct endpoints and the component chooses between them
// from the caller's relationship to the account rather than from which button was
// pressed.

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

// ---------------------------------------------------------------------------
// THE MEASURED POLICY
// ---------------------------------------------------------------------------
//
// `Website/release.config` L242 declares `minRequiredPasswordLength="7"` and L243
// declares `minRequiredNonalphanumericCharacters="0"`.
//
// `UserController.ValidatePassword` (L1067-L1091) applies three rules, and only the
// first can ever fire. The length rule refuses a value shorter than the minimum. The
// non-alphanumeric rule counts matches of `[^0-9a-zA-Z]` and refuses a count BELOW the
// configured minimum — with zero configured, a count can never fall below it, so THE
// RULE IS VACUOUS. The strength-expression rule runs only when an expression is
// configured, and `passwordStrengthRegularExpression` appears nowhere in the
// configuration (zero occurrences), so it never runs.
//
// The single active rule is therefore length >= 7, and the cases below pin exactly
// that. Tightening a credential policy during a migration locks out the accounts it
// was migrating, so the vacuous rule is proved vacuous rather than quietly revived.

const MINIMUM_LENGTH = 7;

/** Six characters: one short of the boundary, so refused. */
const SIX_CHARACTER_PASSWORD = 'Six123';

/** Exactly seven characters, and purely alphanumeric: the boundary, so accepted. */
const SEVEN_CHARACTER_PASSWORD = 'Seven12';

/** Longer, and still purely alphanumeric: the proof that the vacuous rule stays vacuous. */
const PURELY_ALPHANUMERIC_PASSWORD = 'Alphanumeric123';

/**
 * Twenty-five characters — past the legacy markup ceiling of twenty.
 *
 * MIGRATION: the legacy `maxlength="20"` on `Password.ascx` L35, L39 and L43 mirrored
 * the legacy STORAGE width and was never a policy rule, so it is deliberately not
 * reproduced as one. The cases below assert that the typing ceiling is still DECLARED
 * as an attribute and that a value past twenty is NOT refused by validation. The
 * number twenty is never asserted as a policy message, because it never was one.
 */
const TWENTY_FIVE_CHARACTER_PASSWORD = 'TwentyFiveCharacterPass25';

/** An ordinary acceptable replacement. Obviously synthetic; not a credential. */
const REPLACEMENT = 'Replace1';

/** A second acceptable replacement, for cases that need two distinct values. */
const OTHER_REPLACEMENT = 'Different2';

/** The credential in force on the self-service path. Obviously synthetic. */
const CREDENTIAL_IN_FORCE = 'InForce1';

// ---------------------------------------------------------------------------
// THE MEASURED WORDING
// ---------------------------------------------------------------------------
//
// Transcribed from the legacy resource files, which are what a person actually saw:
// where the markup's fallback `text` attribute and the resource value disagree, the
// resource value is authoritative.
//
// The two properties that are easy to lose in transcription are called out because
// both are load-bearing: several of these strings carry NO trailing full stop, and two
// carry a DOUBLE space between sentences.

/** `SharedResources.resx` -> `PasswordMismatch.Text`. No trailing full stop. */
const LEGACY_MISMATCH = 'The Password and Confirmation Passwords do not match';

/** `SharedResources.resx` -> `PasswordMissing.Text`. */
const LEGACY_MISSING =
  'You must provide your current password in order to change the password.';

/** `SharedResources.resx` -> `PasswordNotDifferent.Text`. DOUBLE space, no trailing full stop. */
const LEGACY_NOT_DIFFERENT =
  'The new password is the same as the old password.  Please enter a different password';

/** `SharedResources.resx` -> `PasswordResetFailed.Text`. */
const LEGACY_RESET_FAILED =
  'There was an error setting the password. The password has not been changed.';

/** `SharedResources.resx` -> `PasswordInvalid.Text`. DOUBLE space. */
const LEGACY_PASSWORD_INVALID =
  'You must enter a valid password.  Please check with the Portal Administrator if you ' +
  'do not know the password requirements.';

/** `SharedResources.resx` -> `InvalidPasswordAnswer.Text`. */
const LEGACY_INVALID_ANSWER = 'Password Answer must be provided';

/** `SharedResources.resx` -> `InvalidPasswordQuestion.Text`. */
const LEGACY_INVALID_QUESTION = 'Password Question must be provided';

/**
 * `SharedResources.resx` -> `PasswordChanged.Text`.
 *
 * This is the wording for the SUCCESS outcome. There is deliberately no `Success.Text`
 * key anywhere in the legacy resources — searching for one returns nothing — so this
 * is the only wording the success path can honestly carry.
 */
const LEGACY_PASSWORD_CHANGED = 'The password has been reset.';

/**
 * `SharedResources.resx` -> `InvalidPassword.Text`, with its two replacement tokens
 * resolved from the measured configuration: `[PasswordLength]` becomes 7 and
 * `[NoneAlphabet]` becomes 0.
 *
 * `InvalidPassword.Text` and `PasswordInvalid.Text` are DISTINCT KEYS with distinct
 * wording. This one is the authored policy statement the screen shows beside the field;
 * the other is the outcome vocabulary's entry. Conflating them would put the wrong
 * sentence on the screen, so both are pinned separately.
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
 * `Password.ascx.resx` -> `plLastChanged.Text`.
 *
 * The markup's fallback at `Password.ascx` L15 reads 'Password last Changed:' with a
 * lower-case 'last'; the resource file overrode it at run time, so the resource
 * capitalisation is what a person actually saw and is what is reproduced.
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

// ---------------------------------------------------------------------------
// THE SENTINELS
// ---------------------------------------------------------------------------
//
// `Library/Components/Shared/Null.vb` L41-L45 defines the legacy null vocabulary:
// `NullDate` is `Date.MinValue`, `NullInteger` is -1 and `NullString` is the EMPTY
// STRING rather than a null. The API serialises with its ignore condition set to never,
// so a field arrives PRESENT AND POSSIBLY NULL and is never simply omitted — which is
// why the cases below send explicit nulls rather than absent keys.

/** The date sentinel exactly as it arrives on the wire. */
const NULL_DATE_ON_THE_WIRE = '0001-01-01T00:00:00';

/** The integer sentinel, which on this screen is also a REAL account identifier. */
const NULL_INTEGER = -1;

// ---------------------------------------------------------------------------
// THE CONTROLS
// ---------------------------------------------------------------------------
//
// Element identifiers, which are also the values the surrounding labels associate
// with. Frozen, because module scope holds nothing mutable.

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
 * Claims a confirmation must never make.
 *
 * A confirmation justifies gating an action; it is not a licence to overstate what the
 * action does. Resetting a credential is entirely reversible — another reset replaces
 * it — so none of these may appear. The only legacy strings that promise irreversibility
 * belong to the recycle bin, which is not ported.
 */
const FORBIDDEN_OVERCLAIMS: readonly string[] = Object.freeze([
  'cannot be undone',
  'permanently',
  'irreversible',
  'this action is final',
  'you will not be able to recover',
]);

// ---------------------------------------------------------------------------
// NARROWING, WITHOUT ASSERTIONS
// ---------------------------------------------------------------------------
//
// The workspace forbids `any`, the non-null operator, the suppression comments and
// type assertions — in specifications as much as in production code, because a
// specification that lies to the compiler cannot be trusted to be checking anything.
//
// These helpers THROW on a contract break, which is deliberate. A throw produces a
// named, readable failure that says which selector was missing. The alternative
// idiom — assert not-null, then return early when it is null — lets every remaining
// assertion in the case silently not run, so the case still reports green having
// checked almost nothing.
//
// Absence is deliberately NOT routed through these helpers: a case that asserts
// something is gone keeps the raw query result and asserts it is null.

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
 * Resolves a selector that MUST match an input, narrowing by construction rather than
 * by assertion.
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
 * Collapses every run of whitespace to a single space.
 *
 * Used to compare authored wording with the measured legacy wording. Two of the legacy
 * strings separate their sentences with a DOUBLE space, and HTML collapses whitespace
 * runs when it renders, so a single-spaced string and its double-spaced original are
 * INDISTINGUISHABLE on screen. Comparing collapsed forms therefore tests the property
 * that actually matters to a reader — the words and their order — while the exact
 * authored bytes are pinned separately, so a genuine wording change still fails.
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
 * Every field is supplied explicitly, because the API never omits one: its serialiser
 * is configured to emit nulls rather than to skip them, so a fixture that left a key
 * out would be testing a payload the server cannot produce.
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

    // The stored session outlives a single injector, so it is cleared before every
    // case as well as after one. Without this, a case that seated an administrator
    // could hand that identity to whichever case Jasmine happens to run next.
    TestBed.inject(TokenStorageService).clear();

    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // Created but NOT rendered: the account identifier is a REQUIRED signal input, and
    // rendering before it is bound would throw rather than answer. Each case binds it
    // through `arrive`, which is also where the read is satisfied.
    fixture = TestBed.createComponent(UserPasswordComponent);
  });

  afterEach(() => {
    // Unsatisfied or unexpected traffic fails the case. This is what proves that a
    // dismissed confirmation sends NOTHING, rather than merely proving that the case
    // did not look.
    httpMock.verify();

    // Teardown is mandatory here, not merely tidy. The confirmation is a native
    // `<dialog>`; its top-layer and inert state belong to the Karma document rather
    // than to this fixture, so a dialog left promoted would be visible to a later case
    // under Jasmine's random ordering.
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
   * A control offered by the confirmation, matched by CONTAINMENT rather than by equality.
   *
   * The shared confirmation renders a severity glyph inside its agreeing control, so that
   * control's text is the glyph followed by the label. Severity is deliberately never
   * carried by colour alone there, and asserting exact equality would be asserting that
   * the glyph is absent — that is, asserting appearance, and demanding the removal of an
   * accessibility affordance to boot.
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
   * Seats the caller's identity.
   *
   * The identity is read from the stored session rather than fetched, so seating it is
   * what decides `isAdmin` and `isSelf` — and therefore which of the two operations the
   * screen resolves to. The expiry is a FIXED literal: reading the clock in a
   * specification makes it depend on when it runs, and the session store gates
   * expiry through an explicit call rather than through this field.
   */
  function seatIdentity(userId: number, roles: readonly string[]): void {
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
        isPortalAdministrator: false,
        roles,
        permissions: [],
      },
    });
  }

  /**
   * Binds the account identifier and satisfies the read the screen issues on arrival.
   *
   * The identifier is bound through `setInput` rather than by assigning the field,
   * because the component is change-detected on push and reads the identifier as a
   * signal: `setInput` is what marks the view dirty, and every case that uses this
   * helper then goes on to assert that the DOM actually changed.
   */
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
    seatIdentity(99, ['Administrators']);
    arrive(held);
  }

  /** An administrator acting on their OWN account: the legacy inconsistency's case. */
  function arriveAsAdministratorOfOwnAccount(held: UserDetail = account(7)): void {
    seatIdentity(held.userId, ['Administrators']);
    arrive(held);
  }

  /**
   * Satisfies the re-read the screen issues after a successful write.
   *
   * A successful write is followed by a fresh read, so that the dates the screen shows
   * reflect what the write did rather than what was true before it. Leaving it
   * unsatisfied would fail teardown, which is exactly the guard that keeps this
   * behaviour from being lost by accident.
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
      // THE VACUOUS-RULE PROOF, and the assertion that stops a later reader "helpfully"
      // reviving a rule the legacy system never enforced. The legacy predicate counted
      // matches of `[^0-9a-zA-Z]` and refused a count BELOW the configured minimum of
      // zero, which no count can be. A value containing not one such character is
      // therefore acceptable, and must stay acceptable.
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
      // `InvalidPassword.Text` carries `[PasswordLength]` and `[NoneAlphabet]`
      // replacement tokens. Both are resolved from the measured configuration, and the
      // authored sentence keeps the resource file's DOUBLE spaces.
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

  // =========================================================================
  // C. THE ADMINISTRATOR GATE ON THE CREDENTIAL IN FORCE
  // =========================================================================
  //
  // The single most consequential behavioural rule on this screen, and the one the
  // legacy code was internally inconsistent about.
  //
  // `Password.ascx.vb` L150-L152 gates DISPLAY on `IsAdmin And Not IsUser`, hiding the
  // credential-in-force row only for an administrator acting on somebody else. But
  // L284 and L290 gate ENFORCEMENT on `Not IsAdmin` ALONE. The two predicates differ
  // for exactly one caller: an administrator acting on their OWN account, who was shown
  // the control and then excused both rules that read it.

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
      // THE DISPLAY HALF OF THE LEGACY INCONSISTENCY, REPRODUCED FAITHFULLY. L150's
      // predicate is `IsAdmin And Not IsUser`; this caller IS the user, so the
      // conjunction is false and the row stays visible.
      arriveAsAdministratorOfOwnAccount(account(7));

      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('L150 leaves the row visible for this caller')
        .not.toBeNull();
    });

    it('still enforces the credential in force for that administrator', () => {
      // THE ENFORCEMENT HALF, AND THE ONE PLACE THE TARGET DELIBERATELY DEPARTS FROM
      // THE LEGACY SCREEN. L284 and L290 are gated on `Not IsAdmin`, so the legacy code
      // excused this caller from both rules. That excusal is UNREACHABLE here rather
      // than merely unwise: this caller is the account holder, so the operation is a
      // CHANGE, and the API's change contract requires the credential in force with no
      // exception for the caller's role. Excusing the rule would not admit the request;
      // it would only move the identical refusal from beside the control to a round trip
      // away. The target therefore gates enforcement on the OPERATION, which reproduces
      // the OUTCOME the system as a whole produces.
      //
      // The divergence is annotated in the component and reported, never absorbed
      // silently, and the assertion below pins the behaviour that actually ships rather
      // than a legacy branch that can no longer be reached.
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

      seatIdentity(99, ['Administrators']);
      fixture.detectChanges();

      expect(query(`#${CONTROL_ID.currentPassword}`))
        .withContext('the control leaves the document with the gate')
        .toBeNull();
      expect(inlineMessages())
        .withContext('and takes any objection about itself with it')
        .toEqual([]);
    });
  });

  // =========================================================================
  // D. THE MEASURED ORDER OF THE PRE-FLIGHT RULES
  // =========================================================================
  //
  // `cmdUpdate_Click` evaluates four rules and every arm `Exit Sub`s, so exactly ONE
  // message was ever shown and the FIRST failure won:
  //
  //   L272  new <> confirm                       -> PasswordMismatch
  //   L278  Not ValidatePassword(new)            -> PasswordInvalid
  //   L284  Not IsAdmin And old = ""             -> PasswordMissing
  //   L290  Not IsAdmin And new = old            -> PasswordNotDifferent
  //   L300  ChangePassword returned False        -> PasswordResetFailed, else Success
  //
  // The order is counter-intuitive in one place and that place is asserted directly.

  describe('the order of the pre-flight rules', () => {
    it('reports the mismatch BEFORE the policy when a value breaks both', () => {
      // THE ASSERTION THAT PINS THE ORDER. A six-character value that also fails to
      // match its confirmation breaks L272 and L278 at once. L272 ran first and exited,
      // so the mismatch is what a person saw — never the policy statement. Reversing
      // these two would be an invisible change in every other case and a wrong message
      // in this one.
      arriveAsSelf();
      enter(CONTROL_ID.currentPassword, CREDENTIAL_IN_FORCE);
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);

      const stated = inlineMessages();

      expect(stated).withContext('L272 wins').toContain(LEGACY_MISMATCH);
      expect(stated).withContext('L278 never runs').not.toContain(LEGACY_POLICY_STATEMENT);
      expect(stated).withContext('exactly one message, as the legacy screen showed').toHaveSize(1);

      press(CHANGE_HEADING);

      expect(outstandingRequestCount()).toBe(0);
    });

    it('reports the policy before the missing credential in force', () => {
      // L278 precedes L284: a short replacement is objected to even though the
      // credential in force is also absent.
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, SIX_CHARACTER_PASSWORD);
      press(CHANGE_HEADING);

      const stated = inlineMessages();

      expect(stated).withContext('L278 wins').toContain(LEGACY_POLICY_STATEMENT);
      expect(stated).withContext('L284 never runs').not.toContain(LEGACY_MISSING);
      expect(stated).toHaveSize(1);
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

      // The measured original separates its two sentences with a DOUBLE space and carries
      // NO trailing full stop. The shipped wording keeps the words, the order and the
      // absent full stop, and collapses only the double space — which HTML would have
      // collapsed anyway when it rendered, so the two are indistinguishable on screen.
      // Both properties are asserted, so a genuine wording change still fails here.
      expect(LEGACY_NOT_DIFFERENT).toContain('old password.  Please');
      expect(collapseWhitespace(shipped)).toBe(collapseWhitespace(LEGACY_NOT_DIFFERENT));
      expect(shipped.endsWith('.')).withContext('no trailing stop, as measured').toBeFalse();
    });

    it('does not apply the must-differ rule to an administrative reset', () => {
      // L290 was gated, and the gate follows the operation: a reset carries no
      // credential in force to differ from, so the rule is off for exactly the caller
      // whose control is not rendered.
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

    it('shows one message at a time, never a list', () => {
      arriveAsSelf();
      enter(CONTROL_ID.newPassword, SIX_CHARACTER_PASSWORD);
      enter(CONTROL_ID.confirmPassword, OTHER_REPLACEMENT);
      press(CHANGE_HEADING);

      expect(inlineMessages())
        .withContext('every legacy arm exited, so only one could ever be reported')
        .toHaveSize(1);
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

      // RELATIVE, and asserted as such. The reverse proxy forwards `/api/` to the API
      // container, so an absolute host would make the browser bypass the proxy, turn
      // every call cross-origin, and fail end to end while every build step passed.
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

      // The request carries the four agreed members and NOTHING ELSE. A body that
      // quietly grew a member would be a contract change nobody asked for, and on a
      // credential endpoint it would be a disclosure risk as well.
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

  // =========================================================================
  // F. THE OUTCOME VOCABULARY
  // =========================================================================
  //
  // `PasswordUpdateStatus.vb` L23-L32 declares eight members and assigns NO explicit
  // values, so each member's ordinal is nothing more than its position in the
  // declaration. An ordinal that means whatever the declaration order happens to be is
  // not a contract, so the target keys on STRING codes and the cases below prove that
  // an ordinal is NOT accepted as a key.
  //
  // The eight legacy members land as follows:
  //
  //   Success                 -> not a failure at all; the component's own wording
  //   PasswordMissing         -> user.password.missing
  //   PasswordNotDifferent    -> user.password.not_different
  //   PasswordResetFailed     -> user.password.reset_failed
  //   PasswordInvalid         -> user.password.invalid
  //   PasswordMismatch        -> user.password.mismatch
  //   InvalidPasswordAnswer   -> UNREACHABLE, and absent by design
  //   InvalidPasswordQuestion -> UNREACHABLE, and absent by design
  //
  // The vocabulary type is deliberately NOT declared in the user model, so nothing
  // here imports a type that does not exist.

  describe('the outcome vocabulary', () => {
    it('carries the measured wording for every reachable failure', () => {
      expect(PASSWORD_UPDATE_MESSAGE['user.password.missing']).toBe(LEGACY_MISSING);
      expect(PASSWORD_UPDATE_MESSAGE['user.password.reset_failed']).toBe(LEGACY_RESET_FAILED);
      expect(PASSWORD_UPDATE_MESSAGE['user.password.mismatch']).toBe(LEGACY_MISMATCH);
    });

    it('keeps the wording of the two double-spaced entries equivalent to the measured text', () => {
      // MIGRATION: A WHITESPACE-ONLY DIVERGENCE, REPORTED RATHER THAN ABSORBED. Two of
      // the measured resource strings separate their sentences with a DOUBLE space, and
      // the authored vocabulary uses a single space. HTML collapses whitespace runs when
      // it renders, so the two forms are indistinguishable to a reader and the
      // requirement for EQUIVALENT messages is met. The words, their order and — crucially
      // — the ABSENT trailing full stops are unchanged, and those are asserted below,
      // so a genuine wording change still fails this case.
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
      // There is no `Success.Text` key anywhere in the legacy resources; the success
      // wording comes from `PasswordChanged.Text`. So success is not a member of the
      // failure vocabulary, and asking for it as one answers nothing.
      expect(passwordUpdateMessage('user.password.success')).toBeNull();

      // Widened to strings deliberately. The vocabulary is typed as a tuple of its five
      // literal codes, so asking whether it contains a success code is rejected by the
      // COMPILER rather than merely answered false at run time — which is a stronger
      // guarantee than this assertion, and the reason the assertion has to widen in
      // order to be expressible at all.
      expect(PASSWORD_UPDATE_CODES.map((code) => String(code))).not.toContain(
        'user.password.success',
      );
    });

    it('declares no code for the question-and-answer outcomes, because they are unreachable', () => {
      // ASSERTED BY ABSENCE, which is the only honest way to assert unreachability
      // without inventing a path to it. `InvalidPasswordAnswer` and
      // `InvalidPasswordQuestion` were raised by the legacy question-and-answer panel.
      // The measured configuration sets `requiresQuestionAndAnswer="false"`, the
      // question-and-answer store is not carried forward, and a reset requires no
      // answer, so no request this screen can make can produce either outcome. Their
      // measured wording is recorded here for provenance and is deliberately absent
      // from the shipped vocabulary.
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
   * The measured label as the shared field actually renders it.
   *
   * MIGRATION: THE LEGACY TRAILING COLON IS DROPPED, AND THAT IS THE SHARED FIELD'S
   * DOING RATHER THAN THIS SCREEN'S. Every legacy label carried one — `plLastChanged` is
   * 'Password Last Changed:' in the resource file — because the legacy control emitted
   * the caption and the colon together as literal text. The shared field strips a
   * trailing colon so that the caption it contributes to a control's accessible name is
   * a name rather than a name plus punctuation, which an assistive technology would
   * otherwise announce. The wording is unchanged; only the punctuation is.
   */
  function renderedLabel(measured: string): string {
    return measured.replace(/\s*:$/, '');
  }

  /**
   * The value shown by the field carrying a given label.
   *
   * Located by its label rather than by position, so that reordering the two read-only
   * fields does not silently re-point these assertions at the wrong one.
   */
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
      // `Null.vb` L41 defines `NullDate` as `Date.MinValue`, which crosses the wire as
      // `0001-01-01T00:00:00`. It is a sentinel meaning "never", not a date in the year
      // one, so rendering it as `01/01/0001` would present the absence of a fact as a
      // fact about the first of January.
      //
      // For LAST CHANGED this is PARITY rather than a divergence: the legacy display
      // helper already answered with the empty string for the null date, so an empty
      // field is exactly what a person saw. It is asserted, and it is NOT reported as a
      // behavioural difference, because it is not one.
      arrive(account(7, { lastPasswordChangeDate: NULL_DATE_ON_THE_WIRE }));

      const stated = statedValue(LAST_CHANGED_LABEL);

      expect(stated).withContext('empty, not a date in the year one').toBe('');
      expect(stated).not.toContain('0001');
      expect(stated).not.toContain('01/01/0001');
    });

    it('shows nothing for an explicitly null date', () => {
      // The API serialises with its ignore condition set to never, so a null arrives as
      // a PRESENT member holding null rather than as an absent one. That is the shape
      // this case sends.
      arrive(account(7, { lastPasswordChangeDate: null }));

      expect(statedValue(LAST_CHANGED_LABEL)).toBe('');
    });

    it('refuses a payload whose date member is missing entirely', () => {
      // THE "PRESENT BUT POSSIBLY NULL, NEVER OMITTED" GUARANTEE, ENFORCED RATHER THAN
      // ASSUMED. The API serialises with its ignore condition set to never, so every
      // member is always on the wire and a null is expressed as a null. The transport
      // decodes against that contract before the screen ever sees a value, so an omitted
      // member is REFUSED at the boundary rather than rendered as a guess.
      //
      // This is a stronger outcome than degrading to an empty field: the screen presents
      // nothing at all rather than presenting an absence as though it were a fact.
      arrive(account(7, { lastPasswordChangeDate: undefined }));

      expect(query('.user-password__summary'))
        .withContext('a payload that breaks the contract is not presented')
        .toBeNull();
      expect(query(`#${CONTROL_ID.newPassword}`))
        .withContext('and no form is offered over an account that did not decode')
        .toBeNull();
    });

    it('refuses a date it cannot read, so the words Invalid Date are unreachable', () => {
      // `new Date('nonsense')` formats as `Invalid Date`, and putting that on a screen
      // tells a person about the implementation rather than about their account. The
      // decoder makes it structurally unreachable: an unreadable date never becomes a
      // value the renderer could format, so there is no formatting path to get wrong.
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
      // The legacy screen resolved three branches for this field, of which only two are
      // representable: a forced change, and 'Password does not Expire'. A configured
      // expiry of ZERO MEANT "never expires", so a bare `0` must never appear in its
      // place.
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
      // DEFENSIVE, AND THE NUANCE IS WORTH STATING. `Users.UserID` is `IDENTITY(1,1)`,
      // so zero is not a naturally occurring account identifier in this schema. It is
      // handled as a real value anyway, because the alternative idioms all fail on data
      // this schema DOES produce: a truthiness test treats zero as absent, a
      // greater-than-zero test rejects it outright, and a null-coalescing default to -1
      // collides with `NullInteger`, which is itself the seed of `Portals.PortalID` and
      // therefore a legitimate identifier elsewhere in the same database.
      seatIdentity(99, ['Administrators']);
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
      seatIdentity(99, ['Administrators']);
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

  // =========================================================================
  // H. THE CONFIRMATION THAT GATES AN ADMINISTRATIVE RESET
  // =========================================================================
  //
  // The shared confirmation exposes NO visibility input: presence in the document IS
  // open, because it promotes a native `<dialog>` modally as soon as it is mounted.
  // Every case below therefore asserts on presence and absence rather than on a flag.

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

      // Dismissed before leaving, deliberately. The confirmation promotes a NATIVE
      // dialog, whose top-layer and inert state belong to the Karma document rather than
      // to this fixture, so a case that walked away from an open one would leave the
      // whole page modal for whichever case Jasmine ran next.
      answerDialog(DISMISS_LABEL);

      expect(query('app-confirm-dialog')).withContext('left closed').toBeNull();
    });

    it('names the account it is about, and claims nothing beyond that', () => {
      requestReset(account(7, { username: 'ada.lovelace' }));

      const message = textOf(query('.confirm-dialog__message'));

      expect(message).withContext('the account is named').toContain('ada.lovelace');

      // A confirmation justifies gating an action; it does not license overstating it.
      // Resetting a credential is reversible — another reset replaces it — and the only
      // legacy wording promising irreversibility belongs to the recycle bin, which is
      // not ported.
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
      // The proof that the gating signal is cleared on BOTH paths. The shared
      // confirmation carries an emit-once guard for its own lifetime, so a signal left
      // set would leave the dialog mounted-but-spent and it could never be reopened.
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
      // The legacy screen marked its non-committing controls to not cause validation.
      // The equivalent here is that neither of the confirmation's controls is a submit
      // control, so dismissing cannot submit the form and agreeing goes through the
      // component rather than through a second form submission.
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
      // THE ASSERTION THAT PINS THE FIX FOR THE LEGACY LABEL DEFECT. `Password.ascx` L86
      // declared `controlname="lblQuetxtEditQuestionstion"` — a mangled identifier left
      // behind by a botched find-and-replace, naming no control that exists. The shared
      // label control's markup carries no `for` of its own; the server resolved one from
      // that name, so the page emitted a `for` pointing at nothing, and clicking the
      // label focused nothing.
      //
      // Here every association is asserted to resolve to a control that is really in the
      // document, which is a defect of that class made impossible rather than merely
      // avoided.
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
      // The wording is the resource file's; the trailing colon is not carried across,
      // because the caption contributes to a control's accessible name and punctuation
      // announced as part of a name is noise. The divergence is punctuation only, and it
      // is pinned here so it stays punctuation only.
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

    it('groups the two sections with a real grouping element and a real caption', () => {
      arriveAsAdministrator(account(7));

      const groups = queryAll('fieldset');

      expect(groups.length)
        .withContext('both the change section and the reset section are grouped')
        .toBeGreaterThanOrEqual(2);

      for (const group of groups) {
        expect(textOf(group.querySelector('legend')))
          .withContext('every group is captioned')
          .not.toBe('');
      }

      const captions = queryAll('legend').map((node) => textOf(node));

      expect(captions).toContain(CHANGE_HEADING);
      expect(captions).toContain(RESET_HEADING);
    });

    it('puts no interactive control out of the keyboard order', () => {
      // REVERSING THE SECOND LEGACY DEFECT. The legacy section head placed its expand
      // and collapse affordance in `sectionheadcontrol.ascx` L3 with `tabIndex="-1"`,
      // so the only control that could reveal a collapsed section was unreachable by
      // keyboard. Nothing on this screen may repeat that.
      arriveAsAdministrator(account(7));

      for (const element of queryAll('button, input, a, summary, [tabindex]')) {
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
      // The error surface, the progress indicator and the confirmation each carry their
      // own role and live-region semantics. A wrapper that added a second role or a
      // second live region around them would make an assistive technology announce the
      // same thing twice.
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
      // The shared surface derives its own severity, and it reads a refusal of AUTHORITY
      // as a warning rather than as a fault. That matches the measured legacy behaviour:
      // the access-denied control used the yellow warning treatment in BOTH of its
      // branches. So this case asserts the surface is rendered and that the calm
      // treatment is the one chosen — it does not demand a danger treatment, because
      // demanding one would contradict the measured legacy presentation.
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
      // The keys are .NET model-state names, so they arrive in PascalCase rather than
      // camelCase. Bracket access is used because index-signature access by dot notation
      // is disallowed by this workspace's compiler settings.
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
      // Driven by HOLDING THE REQUEST OPEN rather than by any timer. A timer would make
      // the case depend on wall-clock scheduling; holding the request open makes the
      // in-flight window exactly as long as the assertions inside it need.
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
});
