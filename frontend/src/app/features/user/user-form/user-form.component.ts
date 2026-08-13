/**
 * The create-and-edit account screen: routes `/users/new` and `/users/:userId`. This component is the
 * contract owner for its folder.
 */

import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
  ChangeDetectorRef,
  ElementRef,
} from '@angular/core';
import type { Signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import type { ProblemDetails } from '../../../core/models/problem-details.model';
import { UserCreateStatus } from '../../../core/models/user.model';
import type { CreateUserRequest, UpdateUserRequest, UserDetail } from '../../../core/models/user.model';
import { DeferredOutcomeService } from '../../../core/services/deferred-outcome.service';
import type { DeferredOutcome } from '../../../core/services/deferred-outcome.service';
import { NotificationService } from '../../../core/services/notification.service';
import type { NotificationSeverity } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { UserStore } from '../../../core/state/user.store';
import type { UserOperation } from '../../../core/state/user.store';
import { stripLegacyBreakTags, userCreateMessage } from '../../../core/utils/form-errors.util';
import type { ProblemSeverity } from '../../../core/utils/form-errors.util';
import { CREDENTIAL_MAX_LENGTH } from '../../../core/utils/credential-bounds.util';
import { parseRouteId, readRouteId } from '../../../core/utils/route-id.util';
import { requiredText } from '../../../core/utils/required-text.validator';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import {
  LoadingSpinnerComponent,
} from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';
import {
  FocusFirstInvalidDirective,
  INVALID_CONTROL_SELECTOR,
} from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// The password policy — preserved verbatim, deliberately not tightened

/**
 * The minimum password length the legacy installation enforced. Measured at `Website/release.config`,
 * `minRequiredPasswordLength="7"`.
 */
export const PASSWORD_MIN_LENGTH = 7;

/**
 * The number of non-alphanumeric characters the legacy installation required. Measured at
 * `Website/release.config`, `minRequiredNonalphanumericCharacters="0"`.
 */
export const PASSWORD_MIN_NON_ALPHANUMERIC = 0;

const NON_ALPHANUMERIC_PATTERN = /[^0-9a-zA-Z]/g;

/**
 * The pattern an electronic-mail address must match. this is the MEASURED legacy expression, explicitly
 * anchored.
 */
const EMAIL_PATTERN = /^[\w.-]+(\+[\w-]*)?@([\w-]+\.)+[\w-]+$/;

/**
 * The maximum length of each identity field, in characters. the authority is the column and the API rule,
 * because the markup has none.
 */
const IDENTITY_MAX_LENGTH = Object.freeze({
  /** `Username nvarchar(100) NOT NULL`; `CreateUserRequest` caps the member at the same figure. */
  username: 100,

  /** `FirstName nvarchar(50) NOT NULL`. */
  firstName: 50,

  /** `LastName nvarchar(50) NOT NULL`. */
  lastName: 50,

  /** `DisplayName nvarchar(128)`. */
  displayName: 128,

  /** `Email nvarchar(256)`. */
  email: 256,
});

// Measured wording — labels and titles
// MIGRATION: localisation is NOT ported.

/** `ControlTitle_edit.Text` — the screen title before a record has loaded. */
export const EDIT_MODE_TITLE = 'Edit User Accounts';

/** `AddUser.Text` — the screen title while creating. */
export const CREATE_MODE_TITLE = 'Add New User';

/** `UserTitle.Text` — the per-record title format. */
export const EDIT_RECORD_TITLE_FORMAT = 'Edit User - {0} (Id: {1})';

/** `MembershipTitle.Text` — the legend of the membership panel. */
export const MEMBERSHIP_PANEL_TITLE = 'Membership Information';

/** `Delete.Text` — the destructive action's label for another account. */
export const DELETE_LABEL = 'Delete';

export const UNREGISTER_LABEL = 'UnRegister';

/** `DeleteItem.Text`, from the shared resources — the confirmation for another account. */
export const CONFIRM_DELETE_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `ConfirmUnRegister.Text` — the confirmation for one's OWN account. */
export const CONFIRM_UNREGISTER_MESSAGE = 'Are you sure you want to un-register';

/** `PasswordHelpAdmin.Text` — the help line above the password fields. */
export const PASSWORD_HELP =
  'Optionally enter a password for this user, or allow the system to generate a random password';

/** `Required.Text`, from the shared resources. */
export const REQUIRED_LEGEND = ' All fields marked with a red arrow are required.';

/**
 * The element identifier of the password section's GROUP message container — #8. ⚠ IT HAS TO BE A STABLE,
 * KNOWN VALUE BECAUSE TWO CONTROLS POINT AT IT. The rule spans the password box and its confirmation, so
 * the message belongs to neither control and both must reference it through `aria-describedby` and
 * `aria-errormessage`.
 */
export const PASSWORD_GROUP_ERROR_ID = 'user-form-password-rule';

/**
 * The element identifier of the password box — #8. Declared as a constant rather than left as a template
 * literal because {@link UserFormComponent.onSubmit} now has to FIND that box in order to move focus to
 * it, so the value is read from two places and must not be able to drift between them.
 */
export const PASSWORD_CONTROL_ID = 'user-form-password';

// Measured wording — validation messages

/** `UserInfo_Username.Required`. */
export const USERNAME_REQUIRED_MESSAGE = 'User name is required';

/** `UserInfo_FirstName.Required`. */
export const FIRST_NAME_REQUIRED_MESSAGE = 'First name is required';

/** `UserInfo_LastName.Required`. */
export const LAST_NAME_REQUIRED_MESSAGE = 'Last name is required';

/** `UserInfo_DisplayName.Required`. */
export const DISPLAY_NAME_REQUIRED_MESSAGE = 'Display Name is required';

/** `UserInfo_Email.Required`. */
export const EMAIL_REQUIRED_MESSAGE = 'Email is required';

/** `UserInfo_Email.Validation`. */
export const EMAIL_PATTERN_MESSAGE = 'You must enter a valid email address';

export const PASSWORD_MISMATCH_MESSAGE = 'The Password and Confirmation Passwords do not match';

/**
 * The wording shown when a password fails the policy. the legacy message is `The password specified is
 * invalid.
 */
export const INVALID_PASSWORD_MESSAGE =
  'The password specified is invalid.  Please specify a valid password.  Passwords must be at ' +
  `least ${String(PASSWORD_MIN_LENGTH)} characters in length and contain at least ` +
  `${String(PASSWORD_MIN_NON_ALPHANUMERIC)} non-alphanumeric characters.`;

// Measured wording — outcomes and guards

/** `UserAuthorized.Text` — success wording after authorising. */
export const USER_AUTHORIZED_MESSAGE = 'User successfully Authorized';

/** `UserUnAuthorized.Text` — success wording after withdrawing authorisation. */
export const USER_UNAUTHORIZED_MESSAGE = 'User successfully Un-Authorized';

/** Success wording after releasing a locked-out account. DEFECT 5, annotated and NOT fixed. */
export const USER_UNLOCKED_MESSAGE = 'User successfully Unlocked';

/** Success wording after obliging an account to change its password. */
export const PASSWORD_CHANGE_REQUIRED_MESSAGE = 'This user must change their password at next login';

/**
 * Success wording after an account's details are written. AUTHORED. `User.ascx.vb` raises `OnUserUpdated`
 * and `OnUserUpdateCompleted` after a successful write and the container declares no handler for either,
 * so the legacy screen confirmed nothing at all.
 */
export const USER_UPDATED_MESSAGE = 'User account updated';

/**
 * Read to an operator when a save that outlived this screen was refused. ⚠ IT NAMES THE OPERATION AND NOT
 * THE FORM, because it is read on whatever screen the operator moved to after submitting. "The changes
 * could not be saved" beside a listing of portals says nothing; naming the account operation makes the
 * sentence self-contained.
 */
export const USER_UPDATE_FAILED_MESSAGE = 'User account could not be updated';

/** Read to an operator when a creation that outlived this screen was refused. */
export const USER_CREATE_FAILED_MESSAGE = 'User account could not be created';

/**
 * Success wording after an account is created — U-M9. ⚠ THE SCREEN CONFIRMED NOTHING AT ALL BEFORE THIS,
 * AND THE NAVIGATION IS WHAT MADE THAT SERIOUS. A successful create leaves this screen immediately for
 * the account listing — reproducing `Website/admin/Users/ManageUsers.ascx.vb`, which tests `If
 * e.CreateStatus = UserCreateStatus.Success` and answers with `Response.Redirect(ReturnUrl, True)` — so
 * the operator arrived at a listing of two hundred and fifty accounts with no statement that theirs had
 * been added and no reliable way to find it among them.
 */
export const USER_CREATED_MESSAGE = 'User account {name} created';

/** `NoUser.Text` — the account does not exist. */
export const NO_USER_MESSAGE = "This account doesn't exist";

export const SUPER_USER_MESSAGE =
  'This account represents a SuperUser.  You do not have the rights to edit a SuperUser Account.';

/** `InvalidUser.Text` — the account exists but is not a member of this tenant. */
export const INVALID_USER_MESSAGE = 'This account is not a User in the current Portal.';

/** `NotAuthorized.Text` — the caller may not edit this account. */
export const NOT_AUTHORIZED_MESSAGE = 'You are not authorized to edit this user.';

/** `UserLockedOut.Text` — repeated failed sign-ins have locked the account. */
export const USER_LOCKED_OUT_MESSAGE =
  'This account is currently locked out due to too many unsuccessful login attempts.';

/**
 * `EmailError.Text`. The DOUBLE SPACE after the first full stop is in the measured value and is
 * reproduced verbatim.
 */
export const EMAIL_CONFLICT_MESSAGE =
  'This portal requires a unique Email Address.  The Email Address you entered has already been used.';

/**
 * `ExceededUserQuota.Text` — the tenant's account allowance is reached. Surfaced only when the server
 * refuses.
 */
export const EXCEEDED_USER_QUOTA_MESSAGE =
  'Adding this user will exceed the User Quota for this site. Please contact your hosting provider ' +
  'for inquiries related to increasing your User Quota.';

// Authored advisories for the two contract gaps

/**
 * The sentence shown beside a generated password at the moment it is revealed. the legacy generated the
 * password on the SERVER — `User.ascx.vb` calls `UserController.GeneratePassword()` — and then e-mailed
 * it to the new account holder.
 */
export const RANDOM_PASSWORD_ADVISORY =
  'This password is shown once and is not stored anywhere in this screen. No notification e-mail can ' +
  'be sent, so give it to the account holder now \u2014 otherwise the only remedy is an ' +
  'administrative password reset.';

/**
 * The help text on the notify control, which states its unavailability BEFORE it is used. `plNotify` is a
 * measured control whose purpose was to e-mail the new account holder, and the creation contract carries
 * NO notify member.
 */
export const NOTIFY_UNAVAILABLE_ADVISORY =
  'Unavailable: this installation exposes no mail endpoint, so no notification e-mail can be sent.';

/**
 * Advisory raised after authorising an account. `ManageUsers.ascx.vb` shows that authorising ALSO sent
 * mail — `Mail.SendMail(User, MessageType.UserRegistrationPublic, PortalSettings)`.
 */
export const AUTHORIZE_MAIL_ADVISORY =
  'The account was authorised, but no welcome e-mail was sent: this installation exposes no mail ' +
  'endpoint.';

// The statuses this screen interprets

// DROP: the security-code field. The legacy challenge control is one of the excluded files under
// Library/Controls/**, so there is nothing to port.

// The creation outcome vocabulary, recorded by name

/** The member that means "the account was created". */
export const CREATE_SUCCEEDED: UserCreateStatus = UserCreateStatus.Success;

/**
 * The zero member, which is NOT an outcome. It is the initial "no error recorded yet" sentinel, proven by
 * `User.ascx.vb`, which detects failure by testing for any value OTHER than this one.
 */
export const CREATE_NOT_YET_ATTEMPTED: UserCreateStatus = UserCreateStatus.AddUser;

/** The refusal status. */
const FORBIDDEN_STATUS = 403;

/** The not-found status. */
const NOT_FOUND_STATUS = 404;

/** The state-conflict status. */
const CONFLICT_STATUS = 409;

/** The store commands this screen issues. */
const SCREEN_OPERATIONS: readonly UserOperation[] = Object.freeze<UserOperation[]>([
  'loadUser',
  'createUser',
  'updateUser',
  'deleteUser',
  'setApproval',
  'unlockUser',
  'requirePasswordChange',
]);

/** A membership action awaiting its outcome, together with what to say once it has one. */
interface AwaitedMembershipAction {
  /** The store operation whose settling reports this action. */
  readonly operation: UserOperation;

  /** The wording to announce at success severity once the action is confirmed. */
  readonly success: string;

  /**
   * A caveat to raise at warning severity alongside the success, or `null`. Only the authorising action
   * carries one: the legacy handler also sent mail, and there is no mail endpoint, so the reduction is
   * stated to the operator instead of dropped in silence.
   */
  readonly advisory: string | null;
}

/** The alphabet a generated password draws from. */
const RANDOM_PASSWORD_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';

/**
 * The length of a generated password. Comfortably above {@link PASSWORD_MIN_LENGTH}, so a generated
 * password cannot fail the policy the form has just been told to skip.
 */
const RANDOM_PASSWORD_LENGTH = 16;

// PURE HELPERS

/**
 * @param password The password to inspect.
 * @returns How many characters fall outside `0-9`, `a-z` and `A-Z`.
 */
function countNonAlphanumeric(password: string): number {
  const matches: RegExpMatchArray | null = password.match(NON_ALPHANUMERIC_PATTERN);

  if (matches === null) {
    return 0;
  }

  return matches.length;
}

/**
 * Whether a password satisfies the preserved policy. A faithful port of
 * `UserController.ValidatePassword`, which applied three checks in order: minimum length, minimum
 * non-alphanumeric count, and a strength expression applied ONLY when configured.
 *
 * @param password The password to test.
 * @returns True when the password may be submitted.
 */
function satisfiesPasswordPolicy(password: string): boolean {
  if (password.length < PASSWORD_MIN_LENGTH) {
    return false;
  }

  return countNonAlphanumeric(password) >= PASSWORD_MIN_NON_ALPHANUMERIC;
}

/**
 * Generates a password using the platform's cryptographic random source. generation MOVED from the server
 * to the browser, because the account-creation contract carries no field with which to ask the server to
 * generate one.
 *
 * @returns A password of {@link RANDOM_PASSWORD_LENGTH} characters that satisfies the preserved policy.
 */
function generateRandomPassword(): string {
  const draws = new Uint32Array(RANDOM_PASSWORD_LENGTH);
  crypto.getRandomValues(draws);

  let generated = '';

  for (const draw of draws) {
    generated += RANDOM_PASSWORD_ALPHABET.charAt(draw % RANDOM_PASSWORD_ALPHABET.length);
  }

  return generated;
}

/**
 * @param group The group holding the control.
 * @param name The control's name.
 * @returns The value when it is a string, otherwise the empty string.
 */
function readTextControl(group: AbstractControl, name: string): string {
  const control: AbstractControl | null = group.get(name);

  if (control === null) {
    return '';
  }

  const raw: unknown = control.value;

  if (typeof raw !== 'string') {
    return '';
  }

  return raw;
}

/**
 * @param group The group holding the control.
 * @param name The control's name.
 * @returns The value when it is a boolean, otherwise false.
 */
function readFlagControl(group: AbstractControl, name: string): boolean {
  const control: AbstractControl | null = group.get(name);

  if (control === null) {
    return false;
  }

  const raw: unknown = control.value;

  if (typeof raw !== 'boolean') {
    return false;
  }

  return raw;
}

// THE FORM MODEL

/** The screen's typed form. */
export interface UserFormModel {
  /** The sign-in name. */
  username: FormControl<string>;

  /** The given name. */
  firstName: FormControl<string>;

  /** The family name. */
  lastName: FormControl<string>;

  /** The name shown in place of the name parts. */
  displayName: FormControl<string>;

  /** The electronic-mail address. */
  email: FormControl<string>;

  /** Whether the new account may sign in immediately. `chkAuthorize`. */
  authorize: FormControl<boolean>;

  /** Whether to e-mail the new account holder. */
  notify: FormControl<boolean>;

  randomPassword: FormControl<boolean>;

  /** The password to set. `txtPassword`. */
  password: FormControl<string>;

  /** The password repeated. `txtConfirm`. */
  confirmPassword: FormControl<string>;
}

/**
 * The password rules, expressed as ONE group-level validator. this reproduces the legacy creation
 * screen's `Validate()` exactly, and the ORDER is the specification rather than an implementation detail.
 *
 * @param isCreateMode Reads whether the screen is creating rather than editing.
 * @returns A validator for the whole form group.
 */
export function passwordRulesValidator(isCreateMode: () => boolean): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    // Step 1. The password block does not exist while editing.
    if (!isCreateMode()) {
      return null;
    }

    // Step 3. Generation replaces both rules rather than relaxing them.
    if (readFlagControl(group, 'randomPassword')) {
      return null;
    }

    const password: string = readTextControl(group, 'password');
    const confirmPassword: string = readTextControl(group, 'confirmPassword');

    // Step 4. An ordinal comparison, exactly as the legacy `<>` performed it.
    if (password !== confirmPassword) {
      return { passwordMismatch: PASSWORD_MISMATCH_MESSAGE };
    }

    // Step 5 —, reachable only because step 4 passed. That is the short-circuit.
    if (!satisfiesPasswordPolicy(password)) {
      if (password.length === 0) {
        return { invalidPassword: INVALID_PASSWORD_MESSAGE, passwordRequired: true };
      }

      return { invalidPassword: INVALID_PASSWORD_MESSAGE };
    }

    return null;
  };
}

// THE COMPONENT

@Component({
  selector: 'app-user-form',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    FormFieldComponent,
    ConfirmDialogComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
    DateDisplayPipe,
  ],
  templateUrl: './user-form.component.html',
  styleUrl: './user-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserFormComponent {
  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.store.saving() === false,
  );
  /**
   * The account identifier taken from the route, as a string, or undefined. The name is EXTERNALLY FIXED
   * and must stay exactly `userId`.
   */
  readonly userId = input<string | undefined>(undefined);

  /** The account state this screen reads and commands. */
  private readonly store = inject(UserStore);

  /** The transient success and advisory channel. */
  private readonly notifications = inject(NotificationService);

  /**
   * Reports a write that settles after this screen has gone; see {@link
   * UserFormComponent.handOverPendingWrite}.
   */
  private readonly deferredOutcome = inject(DeferredOutcomeService);

  /** This screen's lifetime, held for the one hand-over below and nothing else. */
  private readonly destroyRef = inject(DestroyRef);

  /** Used only for the two measured redirects and the password cross-link. */
  private readonly router = inject(Router);

  /**
   * The signed-in operator, read ONLY to decide whether this screen is editing their own account. This is
   * not an authorisation source and nothing here is enforced from it — the server owns every membership
   * decision.
   */
  private readonly auth = inject(AuthStore);

  /**
   * This screen's own root element, used for exactly one purpose — #8. ⚠ READ ONLY TO ANSWER "IS THERE AN
   * INVALID CONTROL FOR THE SHARED FOCUS DIRECTIVE TO FIND", and never to read or write a value.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /**
   * This view's change detector, used for exactly one purpose — #8. ⚠ READ THE MEASUREMENT BEFORE
   * REMOVING THIS. The password box lives behind a disclosure, and when a submit is refused by the group
   * rule while that disclosure is CLOSED the component has to open it before anything can be focused.
   */
  private readonly changeDetector = inject(ChangeDetectorRef);

  // Mode — sentinel-safe identifier resolution

  /**
   * The route identifier as a number, or undefined when the route carries none. and this is the
   * highest-risk correctness rule on the screen.
   */
  protected readonly resolvedUserId: Signal<number | undefined> = computed<number | undefined>(
    () => {
      return parseRouteId(this.userId()) ?? undefined;
    },
  );

  /** Whether the screen is creating rather than editing. */
  protected readonly isCreateMode: Signal<boolean> = computed<boolean>(
    () => this.resolvedUserId() === undefined,
  );

  /** Whether the screen is editing rather than creating. */
  protected readonly isEditMode: Signal<boolean> = computed<boolean>(() => !this.isCreateMode());

  /**
   * Whether the address carries something that is not an account identifier. ⚠ THE DISTINCTION {@link
   * isCreateMode} CANNOT MAKE, and could not make. It tests {@link resolvedUserId} for `undefined`, and
   * the shared parser answers `null` - coalesced to `undefined` there - both for an ABSENT parameter and
   * for one it refuses.
   */
  protected readonly addressUnreadable: Signal<boolean> = computed<boolean>(
    () => readRouteId(this.userId()).kind === 'unreadable',
  );

  // STORE PROJECTIONS

  /** The account being edited, or null while creating or before the read returns. */
  /**
   * A generated credential awaiting the server's answer, or null. A plain field rather than a signal, and
   * deliberately not exposed: nothing may render it while the creation is still outstanding, because a
   * credential shown for an account that was then refused is a credential shown for no account at all.
   */
  private heldCredential: string | null = null;

  /**
   * The generated credential currently on screen, or null. Written exactly once per creation, from the
   * settled-outcome effect, and cleared by {@link UserFormComponent.dismissCredential}.
   */
  private readonly _revealedCredential = signal<string | null>(null);

  protected readonly selectedUser: Signal<UserDetail | null> = this.store.selectedUser;

  /**
   * The generated credential to hand over, or null when there is none on screen. Exposed read-only; only
   * the settled-outcome effect and {@link UserFormComponent.dismissCredential} write it.
   */
  protected readonly revealedCredential: Signal<string | null> = this._revealedCredential.asReadonly();

  protected readonly credentialAdvisory = RANDOM_PASSWORD_ADVISORY;

  protected readonly notifyUnavailableHelp = NOTIFY_UNAVAILABLE_ADVISORY;

  /** Whether a read or a write for this screen is outstanding. */
  protected readonly loading: Signal<boolean> = computed<boolean>(
    () => this.store.selectedUserLoading() || this.store.saving(),
  );

  /** The problem document to show in the banner, or null when there is nothing to show. */
  protected readonly problem: Signal<ProblemDetails | null> = computed<ProblemDetails | null>(
    () => {
      const failure = this.store.failure();

      if (failure === null) {
        return null;
      }

      if (!SCREEN_OPERATIONS.includes(failure.operation)) {
        return null;
      }

      if (failure.summary.severity !== 'error') {
        return null;
      }

      return failure.problem;
    },
  );

  // VIEW-LOCAL STATE

  /** Whether a submission has been attempted, so messages appear only after one. */
  /**
   * The typing ceiling emitted on the credential inputs. Shared from
   * `core/utils/credential-bounds.util.ts`, which holds the API's own bound and the reasoning behind it,
   * so this screen cannot drift away from the server rule.
   */
  protected readonly credentialMaxLength = CREDENTIAL_MAX_LENGTH;

  protected readonly submitAttempted = signal(false);

  /** Whether the destructive-action confirmation is open. */
  protected readonly deleteDialogOpen = signal(false);

  /** Whether the credentials section is expanded. */
  protected readonly credentialsExpanded = signal(true);

  /** Whether the password section is expanded. */
  protected readonly passwordExpanded = signal(true);

  /** Whether the membership section is expanded. */
  protected readonly membershipExpanded = signal(true);

  /**
   * Whether the new-account options section is expanded. this group had NO toggle while its three
   * siblings each had one, and the justification recorded beside it - that "the measured markup gave
   * these two rows no section head" - does not survive checking, because it is equally true of all four
   * groups: `User.ascx` registers the collapsible-section control at L4 and then uses it ZERO times,
   * which the note at the top of this file and DEFECT 10 beside the toggle handlers both already record.
   */
  protected readonly newAccountOptionsExpanded = signal(true);

  /** Set while a create is in flight, so its outcome can be acted on exactly once. */
  private readonly createSubmitted = signal(false);

  /** Set while an update is in flight. */
  private readonly updateSubmitted = signal(false);

  /** Set while a removal is in flight. */
  private readonly deleteSubmitted = signal(false);

  private readonly awaitedMembershipAction = signal<AwaitedMembershipAction | null>(null);

  /**
   * The account whose details were last hydrated into the form. Guards the hydration effect so that
   * re-reading the same account — which every membership action causes, because the store reconciles the
   * selection afterwards — does not overwrite edits the operator has in progress.
   */
  private hydratedUserId: number | undefined = undefined;

  // THE FORM

  /** The screen's typed reactive form. */
  // The five identity fields use `requiredText` from `core/utils`, NOT `Validators.required`, because
  // Angular's own validator accepts a value of one space and the server does not.
  protected readonly form: FormGroup<UserFormModel> = new FormGroup<UserFormModel>(
    {
      username: new FormControl<string>('', {
        nonNullable: true,
        validators: [requiredText, Validators.maxLength(IDENTITY_MAX_LENGTH.username)],
      }),
      firstName: new FormControl<string>('', {
        nonNullable: true,
        validators: [requiredText, Validators.maxLength(IDENTITY_MAX_LENGTH.firstName)],
      }),
      lastName: new FormControl<string>('', {
        nonNullable: true,
        validators: [requiredText, Validators.maxLength(IDENTITY_MAX_LENGTH.lastName)],
      }),
      displayName: new FormControl<string>('', {
        nonNullable: true,
        validators: [requiredText, Validators.maxLength(IDENTITY_MAX_LENGTH.displayName)],
      }),
      email: new FormControl<string>('', {
        nonNullable: true,
        validators: [
          requiredText,
          Validators.maxLength(IDENTITY_MAX_LENGTH.email),
          Validators.pattern(EMAIL_PATTERN),
        ],
      }),
      // Initial TRUE, from the measured markup, which the code-behind does not override.
      authorize: new FormControl<boolean>(true, { nonNullable: true }),
      notify: new FormControl<boolean>({ value: false, disabled: true }, { nonNullable: true }),
      randomPassword: new FormControl<boolean>(false, { nonNullable: true }),
      // No required validator and no maximum-length validator: the password rules live in the group
      // validator, in the measured order, and the twenty-character ceiling is a template attribute rather
      // than a rule. See {@link UserFormModel.password}.
      password: new FormControl<string>('', { nonNullable: true }),
      confirmPassword: new FormControl<string>('', { nonNullable: true }),
    },
    { validators: [passwordRulesValidator(() => this.isCreateMode())] },
  );

  // PAGE CHROME

  /**
   * The screen title, which the measured resources prove is MODE-DEPENDENT. Three cases, all measured.
   * Creating uses `AddUser.Text`.
   */
  protected readonly pageTitle: Signal<string> = computed<string>(() => {
    if (this.isCreateMode() && !this.addressUnreadable()) {
      return CREATE_MODE_TITLE;
    }

    const held: UserDetail | null = this.selectedUser();

    if (held === null) {
      return EDIT_MODE_TITLE;
    }

    const shown: string = held.displayName.length === 0 ? held.username : held.displayName;

    return EDIT_RECORD_TITLE_FORMAT.replace('{0}', shown).replace('{1}', String(held.userId));
  });

  /** The help line above the password section. */
  protected readonly passwordHelp: string = PASSWORD_HELP;

  /** {@link PASSWORD_GROUP_ERROR_ID} — #8. */
  protected readonly passwordGroupErrorId: string = PASSWORD_GROUP_ERROR_ID;

  protected readonly passwordControlId: string = PASSWORD_CONTROL_ID;

  protected readonly maxLengths = IDENTITY_MAX_LENGTH;

  /** The legend recording that the marked fields are required. */
  protected readonly requiredLegend: string = REQUIRED_LEGEND;

  /** The legend of the membership panel. */
  protected readonly membershipTitle: string = MEMBERSHIP_PANEL_TITLE;

  /**
   * The destructive action's label. `User.ascx.vb` chose between `UnRegister` and `Delete` on `IsUser` —
   * whether the account being edited IS the signed-in caller.
   */
  protected readonly deleteLabel: string = DELETE_LABEL;

  /** The confirmation shown before removal. Same unevaluable branch as the label. */
  protected readonly confirmDeleteMessage: string = CONFIRM_DELETE_MESSAGE;

  /**
   * Whether the destructive action is offered. The measured rule, from `User.ascx.vb`: ``` If AddUser
   * Then cmdDelete.Visible = False Else cmdDelete.Visible = Not (User.UserID =
   * PortalSettings.AdministratorId) AndAlso Not (IsUser And User.IsSuperUser) ``` The first clause is
   * reproduced exactly: never offered while creating.
   */
  protected readonly canDelete: Signal<boolean> = computed<boolean>(() => {
    if (this.isCreateMode()) {
      return false;
    }

    const held: UserDetail | null = this.selectedUser();

    if (held === null) {
      return false;
    }

    // ⚠ THE SERVER'S CAPABILITY, NOT A RULE RESTATED HERE. A local restatement reads
    // `!held.isSuperUser`, which is only the FIRST half of the rule the removal operation enforces: it also
    // refuses the account named by the tenant's `Portals.AdministratorId`.
    return held.canDelete;
  });

  // The membership panel — read-only
  // MIGRATION: the ninth field, `UserMembership_IsOnLine` ('User Is On Line:'), is DROPPED along with the
  // `imgOnline` indicator on `pnlUser`. Users-online is out of scope and no endpoint reports it.

  /** `UserMembership_Approved` — whether the account may sign in. */
  protected readonly isApproved: Signal<boolean | undefined> = computed<boolean | undefined>(
    () => this.selectedUser()?.isApproved,
  );

  /** `UserMembership_LockedOut` — whether failed sign-ins have locked the account. */
  protected readonly isLockedOut: Signal<boolean | undefined> = computed<boolean | undefined>(
    () => this.selectedUser()?.isLockedOut,
  );

  /** `UserMembership_CreatedDate`. */
  protected readonly createdDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.createdDate,
  );

  /** `UserMembership_LastActivityDate`. */
  protected readonly lastActivityDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastActivityDate,
  );

  /** `UserMembership_LastLoginDate`. */
  protected readonly lastLoginDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastLoginDate,
  );

  /** `UserMembership_LastPasswordChangeDate`. */
  protected readonly lastPasswordChangeDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastPasswordChangeDate,
  );

  /** `UserMembership_LastLockoutDate`. */
  protected readonly lastLockoutDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastLockoutDate,
  );

  /**
   * `UserMembership_UpdatePassword` — whether the account must change its password. The account contract
   * spells this `mustChangePassword`, and the sign-in contract spells it identically.
   */
  protected readonly mustChangePassword: Signal<boolean | undefined> =
    this.store.selectedUserMustChangePassword;

  // The four per-account membership actions — offered only when they would change something

  /**
   * Whether the operator is editing their own account. Derived exactly as the sibling profile screen
   * derives it, against the same two identities the measured code compared. ⚠ STRICT EQUALITY AGAINST AN
   * EXPLICITLY RESOLVED KEY. A truthiness test would misread the account key zero, and a fallback default
   * would make an unresolved route parameter match a real key.
   */
  protected readonly isSelf: Signal<boolean> = computed<boolean>(() => {
    const subject: number | undefined = this.resolvedUserId();
    const caller = this.auth.currentUser();

    if (subject === undefined || caller === null) {
      return false;
    }

    return caller.userId === subject;
  });

  /** `cmdAuthorize.Visible = Not Membership.Approved` — offered only for an account that may not sign in. */
  protected readonly canAuthorize: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.isApproved() === false,
  );

  /** `cmdUnAuthorize.Visible = Membership.Approved` — offered only for an account that may sign in. */
  protected readonly canUnauthorize: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.isApproved() === true,
  );

  /** `cmdUnLock.Visible = Membership.LockedOut` — offered only for an account failed sign-ins have locked. */
  protected readonly canUnlock: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.isLockedOut() === true,
  );

  /**
   * `cmdPassword.Visible = Not Membership.UpdatePassword` — offered only while the obligation is not
   * already recorded.
   */
  protected readonly canForcePasswordChange: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.mustChangePassword() === false,
  );

  /** Whether any of the four is offered at all. */
  protected readonly hasMembershipActions: Signal<boolean> = computed<boolean>(
    () =>
      this.canAuthorize() ||
      this.canUnauthorize() ||
      this.canUnlock() ||
      this.canForcePasswordChange(),
  );

  // Guard behaviour — disabled and warned, never redirected

  /**
   * Whether the form is withheld because the server refused or found nothing. the measured guard sequence
   * at `ManageUsers.ascx.vb` runs four checks — superuser-add, tenant-membership, superuser-edit,
   * administrator-rights — and every failure does the SAME two things: `AddModuleMessage(...,
   * YellowWarning, True)` followed by `DisableForm()`.
   */
  protected readonly formDisabled: Signal<boolean> = computed<boolean>(() => {
    const failure = this.store.failure();

    if (failure === null) {
      return false;
    }

    if (!SCREEN_OPERATIONS.includes(failure.operation)) {
      return false;
    }

    const status: number | null = failure.summary.status;

    return status === FORBIDDEN_STATUS || status === NOT_FOUND_STATUS;
  });

  protected readonly userMissing: Signal<boolean> = computed<boolean>(() => {
    if (this.isCreateMode()) {
      return false;
    }

    if (this.store.selectedUserLoading()) {
      return false;
    }

    const failure = this.store.failure();

    if (failure === null) {
      return false;
    }

    return failure.operation === 'loadUser' && failure.summary.status === NOT_FOUND_STATUS;
  });

  /** Whether the submit action should be offered as available. */
  protected readonly canSubmit: Signal<boolean> = computed<boolean>(
    () => !this.formDisabled() && !this.loading(),
  );

  // WIRING

  constructor() {
    effect(() => {
      const id: number | undefined = this.resolvedUserId();

      untracked(() => {
        this.store.clearFailure();
        this.submitAttempted.set(false);
        this.createSubmitted.set(false);
        this.updateSubmitted.set(false);
        this.deleteSubmitted.set(false);
        this.awaitedMembershipAction.set(null);

        if (id === undefined) {
          this.hydratedUserId = undefined;
          this.store.clearSelectedUser();
          this.form.reset();
          this.applyControlAvailability();

          return;
        }

        this.store.selectUser(id);
      });
    });

    // Fills the form once per account read. Guarded on the identifier rather than on the record's identity,
    // because every membership action makes the store re-read the same account and a second hydration would
    // discard edits in progress.
    effect(() => {
      const held: UserDetail | null = this.selectedUser();
      const editing: boolean = this.isEditMode();

      untracked(() => {
        if (!editing || held === null) {
          return;
        }

        if (this.hydratedUserId === held.userId) {
          return;
        }

        this.hydratedUserId = held.userId;
        this.hydrate(held);
      });
    });

    // Keeps the enabled set in step with the mode and with a refusal.
    effect(() => {
      const editing: boolean = this.isEditMode();
      const withheld: boolean = this.formDisabled();

      untracked(() => {
        this.applyControlAvailability(editing, withheld);
      });
    });

    // Announces a warning-severity refusal, and an error that arrived without a document.
    effect(() => {
      const failure = this.store.failure();

      if (failure === null) {
        return;
      }

      if (!SCREEN_OPERATIONS.includes(failure.operation)) {
        return;
      }

      untracked(() => {
        this.announceFailure(
          failure.summary.severity,
          failure.summary.message,
          failure.code,
          failure.summary.supportReference,
        );
      });
    });

    effect(() => {
      const submitted: boolean = this.createSubmitted();
      const saving: boolean = this.store.saving();
      const failure = this.store.failure();
      const created: UserDetail | null = this.selectedUser();

      untracked(() => {
        if (!submitted || saving) {
          return;
        }

        this.createSubmitted.set(false);

        if (failure !== null || created === null) {
          // A refused creation discloses nothing. The held credential belongs to an account that does not
          // exist, so it is discarded rather than shown.
          this.heldCredential = null;

          return;
        }

        // Marked HERE rather than beside each departure because the credential hand-over defers its
        // navigation to `dismissCredential()`: the creation has still succeeded, so the entry is no longer
        // unsaved from this point on regardless of which of the two paths carries the operator away.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        // `reset` rather than a value assignment, so the value, the dirty flag and the touched flag go
        // together and no complaint about a credential that no longer exists is left on the screen - which
        // matters here because the group validator reports the password rules and would otherwise fire
        // against the very credential this line removed.
        this.form.controls.password.reset('');
        this.form.controls.confirmPassword.reset('');

        // ⚠ U-M9 — ANNOUNCED BEFORE EITHER OUTCOME BRANCH, so both are confirmed by one statement.
        this.notifications.success(USER_CREATED_MESSAGE.replace('{name}', created.username), true);

        const generated: string | null = this.heldCredential;
        this.heldCredential = null;

        if (generated !== null) {
          // The redirect is deferred, not dropped. This screen is the only place the credential can be
          // handed over, and navigating now would destroy it before it had been read.
          this._revealedCredential.set(generated);

          return;
        }

        // Replaced, not pushed: the work is done, so BACK must not return to a form for a record that now
        // exists. See the note on the sign-in screen's departure for the same rule stated in full.
        void this.router.navigate(['/users'], { replaceUrl: true }).catch(() => false);
      });
    });

    effect(() => {
      const submitted: boolean = this.updateSubmitted();
      const saving: boolean = this.store.saving();
      const failure = this.store.failure();

      untracked(() => {
        if (!submitted || saving) {
          return;
        }

        this.updateSubmitted.set(false);

        if (failure !== null) {
          return;
        }

        this.submitAttempted.set(false);
        this.form.markAsPristine();
        this.notifications.success(USER_UPDATED_MESSAGE);
      });
    });

    effect(() => {
      const submitted: boolean = this.deleteSubmitted();
      const saving: boolean = this.store.saving();
      const failure = this.store.failure();

      untracked(() => {
        if (!submitted || saving) {
          return;
        }

        this.deleteSubmitted.set(false);

        if (failure !== null) {
          return;
        }

        // ⚠ SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS THE OPERATOR TO CONFIRM DISCARDING
        // EDITS TO AN ACCOUNT THAT NO LONGER EXISTS. The probe reads `dirty && store.saving() === false`,
        // and a removal is not a save, so an operator who typed something and then removed the account was
        // prompted about the typing on the way out.
        this.form.markAsPristine();
        this.form.markAsUntouched();

    // Replaced, not pushed: the work is done, so BACK must not return to a form for a record that has just
    // been written - and the unsaved-entry gate reads a replacement as a departure the application itself
    // initiated, so it does not question it.
    void this.router.navigate(['/users'], { replaceUrl: true });
      });
    });

    effect(() => {
      const awaited: AwaitedMembershipAction | null = this.awaitedMembershipAction();
      const saving: boolean = this.store.saving();
      const failure = this.store.failure();

      if (awaited === null || saving) {
        return;
      }

      untracked(() => {
        this.awaitedMembershipAction.set(null);

        if (failure !== null && failure.operation === awaited.operation) {
          return;
        }

        this.notifications.success(awaited.success);

        if (awaited.advisory !== null) {
          this.notifications.warning(awaited.advisory);
        }
      });
    });

    this.destroyRef.onDestroy(() => this.handOverPendingWrite());
  }

  /**
   * Hands an outstanding create or update over to be reported after this screen has gone. ⚠ THE TWO WRITE
   * BRIDGES ABOVE ARE EFFECTS IN THIS COMPONENT'S INJECTION CONTEXT, SO THEY DIE WITH THIS COMPONENT. An
   * operator who submits and then immediately goes somewhere else destroys the only party that was going
   * to tell them what happened: the store's request completes and the account really is created or
   * changed, and nothing says so.
   */
  private handOverPendingWrite(): void {
    const created: boolean = this.createSubmitted();
    const updated: boolean = this.updateSubmitted();

    if (!created && !updated) {
      return;
    }

    // The store publishes one in-flight flag and one failure slot for the account commands, which is all
    // either bridge above reads, so the verdict is resolved from exactly the facts they use.
    const verdict: Signal<DeferredOutcome> = computed<DeferredOutcome>(() => {
      if (this.store.saving()) {
        return 'pending';
      }

      return this.store.failure() !== null ? 'failed' : 'succeeded';
    });

    this.deferredOutcome.announceWhenSettled(
      verdict,
      () => {
        if (updated) {
          return USER_UPDATED_MESSAGE;
        }

        const stored = this.selectedUser();

        return stored === null ? null : USER_CREATED_MESSAGE.replace('{name}', stored.username);
      },
      () => ({
        message: updated ? USER_UPDATE_FAILED_MESSAGE : USER_CREATE_FAILED_MESSAGE,
        reference: this.store.failure()?.summary.supportReference ?? null,
      }),
    );
  }

  // COMMANDS — SUBMIT

  /**
   * Writes the form: creates while in create mode, updates while editing. This is the ONLY action on the
   * screen that validates, mirroring the measured `causesvalidation="True"` on `cmdUpdate`.
   */
  onSubmit(): void {
    this.submitAttempted.set(true);
    this.form.markAllAsTouched();

    if (this.formDisabled() || this.loading()) {
      return;
    }

    if (this.form.invalid) {
      this.revealAndFocusPasswordRule();

      return;
    }

    if (this.isCreateMode()) {
      this.submitCreate();

      return;
    }

    this.submitUpdate();
  }

  /**
   * Brings the password rule and its box into view when the rule is the reason a submit was refused and
   * nothing else on the form can answer for it — #8. ⚠ THIS EXISTS BECAUSE THE PASSWORD RULE IS A GROUP
   * RULE, AND A GROUP RULE HAS NO CONTROL. {@link passwordRulesValidator} runs on the whole form and
   * reports its failure through `form.errors`, deliberately, because the rule spans two boxes and neither
   * of them is individually wrong — a matching pair of weak passwords is two perfectly valid values in an
   * invalid combination.
   */
  private revealAndFocusPasswordRule(): void {
    if (this.passwordMessage().length === 0) {
      return;
    }

    // The directive's own selector, asked as a question. Any match means it will act and this must not.
    if (this.host.nativeElement.querySelector(INVALID_CONTROL_SELECTOR) !== null) {
      return;
    }

    if (!this.passwordExpanded()) {
      this.passwordExpanded.set(true);

      this.changeDetector.detectChanges();
    }

    const box = this.host.nativeElement.ownerDocument.getElementById(PASSWORD_CONTROL_ID);

    // Absent whenever a random password was requested, which withholds both boxes by design — and in
    // that state the rule cannot fire either, so this is defence rather than a reachable branch.
    if (box === null || box === this.host.nativeElement.ownerDocument.activeElement) {
      return;
    }

    box.focus();
  }

  // COMMANDS — REMOVAL

  /** Opens the removal confirmation. Does NOT validate. */
  onDeleteRequested(): void {
    this.deleteDialogOpen.set(false);

    if (!this.canDelete()) {
      return;
    }

    this.deleteDialogOpen.set(true);
  }

  /**
   * Removes the account. MIGRATION: removal is PER ACCOUNT. The legacy carried an unbounded bulk deletion
   * that destroyed every unauthorised account from a single click with no per-row confirmation, and it is
   * not carried forward anywhere.
   */
  onDeleteConfirmed(): void {
    this.deleteDialogOpen.set(false);

    const id: number | undefined = this.resolvedUserId();

    // An explicit presence test. Zero and minus one would both be legitimate values.
    if (id === undefined) {
      return;
    }

    this.deleteSubmitted.set(true);
    this.store.deleteUser(id);
  }

  /** Dismisses the removal confirmation without removing anything. */
  onDeleteCancelled(): void {
    this.deleteDialogOpen.set(false);
  }

  // Commands — the four membership actions
  // All four are measured with `causesvalidation="False"`, so none of them validates, marks controls as
  // touched, records a submission attempt or is gated on the form's validity. Reproducing that exactly is
  // the point: authorising an account has nothing to do with whether its display name is filled in.

  /** Authorises the account. `cmdAuthorize`, labelled `Authorize User`. */
  onAuthorize(): void {
    const id: number | undefined = this.resolvedUserId();

    if (id === undefined || this.awaitedMembershipAction() !== null) {
      return;
    }

    this.awaitedMembershipAction.set({
      operation: 'setApproval',
      success: USER_AUTHORIZED_MESSAGE,
      advisory: AUTHORIZE_MAIL_ADVISORY,
    });
    this.store.setApproval(id, true);
  }

  /** Withdraws authorisation. `cmdUnAuthorize`, labelled `UnAuthorize User`. */
  onUnauthorize(): void {
    const id: number | undefined = this.resolvedUserId();

    if (id === undefined || this.awaitedMembershipAction() !== null) {
      return;
    }

    this.awaitedMembershipAction.set({
      operation: 'setApproval',
      success: USER_UNAUTHORIZED_MESSAGE,
      advisory: null,
    });
    this.store.setApproval(id, false);
  }

  /** Releases a locked-out account. */
  onUnlock(): void {
    const id: number | undefined = this.resolvedUserId();

    if (id === undefined || this.awaitedMembershipAction() !== null) {
      return;
    }

    this.awaitedMembershipAction.set({
      operation: 'unlockUser',
      success: USER_UNLOCKED_MESSAGE,
      advisory: null,
    });
    this.store.unlockUser(id);
  }

  /** Obliges the account to change its password. `cmdPassword`, labelled `Force Password Change`. */
  onForcePasswordChange(): void {
    const id: number | undefined = this.resolvedUserId();

    if (id === undefined || this.awaitedMembershipAction() !== null) {
      return;
    }

    this.awaitedMembershipAction.set({
      operation: 'requirePasswordChange',
      success: PASSWORD_CHANGE_REQUIRED_MESSAGE,
      advisory: null,
    });
    this.store.requirePasswordChange(id);
  }

  // Commands — the collapsible sections
  // MIGRATION: the legacy collapsible section head has no counterpart among the shared components, and
  // none is added for it. The template expresses each section as a native grouping element
  // with a keyboard-reachable control carrying the expanded state, styled from the shared partials.

  /** Expands or collapses the credentials section. */
  toggleCredentials(): void {
    this.credentialsExpanded.update((expanded) => !expanded);
  }

  /** Expands or collapses the password section. */
  togglePassword(): void {
    this.passwordExpanded.update((expanded) => !expanded);
  }

  /** Expands or collapses the new-account options section. */
  toggleNewAccountOptions(): void {
    this.newAccountOptionsExpanded.update((expanded) => !expanded);
  }

  /** Expands or collapses the membership section. */
  toggleMembership(): void {
    this.membershipExpanded.update((expanded) => !expanded);
  }

  /**
   * Takes the revealed credential off the screen and completes the redirect. The redirect lives here
   * because it was deferred, not because dismissal navigates by nature.
   */
  dismissCredential(): void {
    this._revealedCredential.set(null);

    // ⚠ MAJOR (CWE-316) — NOTHING IS CLEARED HERE, AND THAT IS NOT AN OMISSION. The two credential CONTROLS
    // were emptied when the creation settled, before this panel was ever shown, so by the time an operator
    // dismisses it there is no typed credential left to remove; the generated value is dropped on the line
    // above and is held nowhere else.
    void this.router.navigate(['/users'], { replaceUrl: true }).catch(() => false);
  }

  // TEMPLATE HELPERS

  /**
   * The message to show beside one control, or the empty string when there is none. Two sources, in
   * order.
   *
   * @param controlName The control's name in the form model.
   * @returns A plain-text message, or the empty string.
   */
  protected messageFor(controlName: keyof UserFormModel): string {
    const control: AbstractControl = this.form.controls[controlName];

    if (control.invalid && (control.touched || this.submitAttempted())) {
      const declared: string | null = this.firstValidatorMessage(control);

      if (declared !== null) {
        return declared;
      }
    }

    const reported: string | null = this.serverMessageFor(controlName);

    if (reported !== null) {
      return stripLegacyBreakTags(reported);
    }

    return '';
  }

  /** @returns A plain-text message, or the empty string. */
  protected passwordMessage(): string {
    // The two controls the group rule spans. Touching either one is what licenses the rule to speak,
    // exactly as touching a single control licenses its own message in `messageFor`.
    const engaged: boolean =
      this.form.controls.password.touched || this.form.controls.confirmPassword.touched;

    if (!this.submitAttempted() && !engaged) {
      return '';
    }

    const errors: ValidationErrors | null = this.form.errors;

    if (errors === null) {
      return '';
    }

    const mismatch: unknown = errors['passwordMismatch'];

    if (typeof mismatch === 'string') {
      return mismatch;
    }

    const invalid: unknown = errors['invalidPassword'];

    if (typeof invalid === 'string') {
      return invalid;
    }

    return '';
  }

  /**
   * Whether the password section is missing a value rather than holding a weak one.
   *
   * @returns True when the password is empty and the rules apply.
   */
  protected get passwordRequired(): boolean {
    const errors: ValidationErrors | null = this.form.errors;

    if (errors === null) {
      return false;
    }

    return errors['passwordRequired'] === true;
  }

  // PRIVATE — SUBMISSION

  /**
   * Builds and issues the creation request. no value is trimmed, upper-cased or otherwise normalised on
   * its way out.
   */
  private submitCreate(): void {
    const raw = this.form.getRawValue();
    const generate: boolean = raw.randomPassword;

    // MIGRATION: generation moved from the server to the browser because the creation contract has no field
    // with which to request it. See {@link generateRandomPassword}.
    const password: string = generate ? generateRandomPassword() : raw.password;

    const request: CreateUserRequest = {
      username: raw.username,
      firstName: raw.firstName,
      lastName: raw.lastName,
      displayName: raw.displayName,
      email: raw.email,
      password,
      // A generated password confirms itself. The server checks the pair as well, because a check performed
      // only on the client is not a check.
      confirmPassword: generate ? password : raw.confirmPassword,
      authorize: raw.authorize,
    };

    // Held, not announced, and not yet shown. The credential is disclosed only once the server has
    // confirmed that the account exists; the settled-outcome effect below moves it onto the screen or
    // discards it.
    this.heldCredential = generate ? password : null;

    this.createSubmitted.set(true);
    this.store.createUser(request);
  }

  /**
   * Builds and issues the update request. the update contract is deliberately narrow — the given name,
   * the family name, the display name and the address, and nothing else.
   */
  private submitUpdate(): void {
    const id: number | undefined = this.resolvedUserId();

    if (id === undefined) {
      return;
    }

    if (this.form.pristine) {
      return;
    }

    const raw = this.form.getRawValue();

    const request: UpdateUserRequest = {
      firstName: raw.firstName,
      lastName: raw.lastName,
      displayName: raw.displayName,
      email: raw.email,
    };

    this.updateSubmitted.set(true);
    this.store.updateUser(id, request);
  }

  // Private — form maintenance

  /**
   * Copies a loaded account into the form. Only the four editable members plus the read-only sign-in name
   * are written.
   *
   * @param held The account as the server reported it.
   */
  private hydrate(held: UserDetail): void {
    this.form.patchValue({
      username: held.username,
      firstName: held.firstName,
      lastName: held.lastName,
      displayName: held.displayName,
      email: held.email,
    });

    this.form.markAsPristine();
    this.form.markAsUntouched();
    this.submitAttempted.set(false);
    this.applyControlAvailability();
  }

  /**
   * Enables exactly the controls the current mode and state allow. the enabled set is the measured
   * visible set.
   *
   * @param editing Whether the screen is editing.
   * @param withheld Whether a refusal has withheld the form.
   */
  private applyControlAvailability(editing?: boolean, withheld?: boolean): void {
    const isEditing: boolean = editing === undefined ? this.isEditMode() : editing;
    const isWithheld: boolean = withheld === undefined ? this.formDisabled() : withheld;

    const options = { emitEvent: false } as const;
    const createOnly: readonly (keyof UserFormModel)[] = [
      'authorize',
      'notify',
      'randomPassword',
      'password',
      'confirmPassword',
    ];

    if (isWithheld) {
      this.form.disable(options);

      return;
    }

    this.form.enable(options);

    // Re-applied after every blanket enable, and that is why this line exists at all. The notify control is
    // disabled for the whole life of the screen rather than by mode — there is no mail endpoint, so it can
    // never act — and `enable()` on the group re-enables every descendant indiscriminately.
    this.form.controls.notify.disable(options);

    if (!isEditing) {
      return;
    }

    // Editing: the sign-in name is fixed and the create-only controls do not apply.
    this.form.controls.username.disable(options);

    for (const name of createOnly) {
      // Widened to the base type deliberately: the indexed access yields a union of two differently
      // parameterised controls, and the operation is declared on the base.
      const control: AbstractControl = this.form.controls[name];
      control.disable(options);
    }
  }

  // Private — message resolution

  /**
   * The message a client-side validator attached to one control. The validators on this screen store
   * their wording as the error VALUE, so the measured sentence travels with the failure instead of being
   * looked up by key at the point of display.
   *
   * @param control The control to inspect.
   * @returns The message, or null when the control has no message-bearing error.
   */
  private firstValidatorMessage(control: AbstractControl): string | null {
    const errors: ValidationErrors | null = control.errors;

    if (errors === null) {
      return null;
    }

    if (errors['required'] === true) {
      return this.requiredMessageFor(control);
    }

    // The measured address pattern. Angular stores its own diagnostic object here, so the measured wording
    // is supplied rather than read out of it.
    if (errors['pattern'] !== undefined) {
      return EMAIL_PATTERN_MESSAGE;
    }

    // The column bound. Composed from the length the framework REPORTS rather than from a table looked up
    // by control, so the sentence and the rule can never name different numbers.
    const overlong: unknown = errors['maxlength'];

    if (typeof overlong === 'object' && overlong !== null) {
      const bound: unknown = (overlong as { requiredLength?: number }).requiredLength;

      if (typeof bound === 'number') {
        return `Enter at most ${String(bound)} characters.`;
      }
    }

    return null;
  }

  /**
   * The measured required-field wording for one control.
   *
   * @param control The control to word.
   * @returns The measured message, or null.
   */
  private requiredMessageFor(control: AbstractControl): string | null {
    const controls = this.form.controls;

    if (control === controls.username) {
      return USERNAME_REQUIRED_MESSAGE;
    }

    if (control === controls.firstName) {
      return FIRST_NAME_REQUIRED_MESSAGE;
    }

    if (control === controls.lastName) {
      return LAST_NAME_REQUIRED_MESSAGE;
    }

    if (control === controls.displayName) {
      return DISPLAY_NAME_REQUIRED_MESSAGE;
    }

    if (control === controls.email) {
      return EMAIL_REQUIRED_MESSAGE;
    }

    return null;
  }

  /**
   * A per-field message the server reported for one control. Matched case-insensitively because the
   * server's model-state keys are not camel-cased.
   *
   * @param controlName The control's name in the form model.
   * @returns The first message for that field, or null.
   */
  private serverMessageFor(controlName: keyof UserFormModel): string | null {
    const wanted: string = controlName.toLowerCase();

    for (const group of this.store.failureFieldMessages()) {
      if (group.field.toLowerCase() !== wanted) {
        continue;
      }

      const first: string | undefined = group.messages.at(0);

      if (first !== undefined) {
        return first;
      }
    }

    return null;
  }

  /**
   * Announces a failure through the channel its severity calls for.
   *
   * @param severity The severity the shared summariser resolved.
   * @param message The summariser's own sentence, used when nothing more specific applies.
   * @param code The machine-readable failure code, or null.
   * @param reference The support reference the server recorded this answer under, or null.
   */
  private announceFailure(
    severity: ProblemSeverity,
    message: string,
    code: string | null,
    reference: string | null,
  ): void {
    if (severity === 'error') {
      if (this.problem() === null) {
        this.notifications.error(this.wordFailure(message, code));
      }

      return;
    }

    const channel: NotificationSeverity = severity === 'warning' ? 'warning' : 'info';

    this.notifications.notify(channel, this.wordFailure(message, code), reference);
  }

  /**
   * Selects the measured wording for a failure, falling back to the server's own. keyed on the failure
   * code STRING and never on a numeric ordinal, because the legacy ordinals disagree with one another and
   * none of them crosses this boundary anyway.
   *
   * @param message The summariser's sentence.
   * @param code The failure code, or null.
   * @returns Plain text, never blank.
   */
  private wordFailure(message: string, code: string | null): string {
    const created: string | null = userCreateMessage(code);

    if (created !== null) {
      return created;
    }

    return stripLegacyBreakTags(message);
  }

  /**
   * The measured refusal wording this screen can offer, exposed for the template. the four guard outcomes
   * the legacy worded are recorded as constants and are shown only when the SERVER refuses, because the
   * server owns every one of the decisions behind them.
   *
   * @returns The measured sentence for the current refusal, or the empty string.
   */
  protected refusalMessage(): string {
    const failure = this.store.failure();

    if (failure === null) {
      return '';
    }

    if (!SCREEN_OPERATIONS.includes(failure.operation)) {
      return '';
    }

    switch (failure.summary.status) {
      case FORBIDDEN_STATUS:
        // Both refusal paths are permission outcomes rather than faults: creating may be refused on the
        // tenant's account allowance, and updating may be refused when an installation administrator is the
        // target.
        return failure.operation === 'createUser'
          ? EXCEEDED_USER_QUOTA_MESSAGE
          : NOT_AUTHORIZED_MESSAGE;
      case NOT_FOUND_STATUS:
        return NO_USER_MESSAGE;
      case CONFLICT_STATUS:
        return EMAIL_CONFLICT_MESSAGE;
      default:
        // Exhaustive by construction: the status is a plain number, so a default is required and returning
        // the empty string keeps the member total.
        return '';
    }
  }
}
