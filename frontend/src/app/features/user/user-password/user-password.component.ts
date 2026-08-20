import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { Signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';

import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  ChangePasswordOperation,
  ChangePasswordRequest,
  UserDetail,
} from '../../../core/models/user.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { UserStore } from '../../../core/state/user.store';
import type { UserOperation } from '../../../core/state/user.store';
import { CREDENTIAL_MAX_LENGTH } from '../../../core/utils/credential-bounds.util';
import { isRouteId, parseRouteId } from '../../../core/utils/route-id.util';
import {
  PASSWORD_UPDATE_MESSAGE,
  fieldErrorMessage,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { DateDisplayPipe, parseDisplayInstant } from '../../../shared/pipes/date-display.pipe';
import { AbsentValueComponent } from '../../../shared/components/absent-value/absent-value.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// ---------------------------------------------------------------------------
// THE POLICY, PRESERVED VERBATIM AND DELIBERATELY NOT TIGHTENED
// ---------------------------------------------------------------------------

/**
 * The minimum credential length, measured from `Website/release.config` L242
 * (`minRequiredPasswordLength="7"`) and confirmed against the API's own
 * `PasswordPolicy:MinRequiredPasswordLength`, which is 7.
 */
const MINIMUM_PASSWORD_LENGTH = 7;

/**
 * The number of non-alphanumeric characters the credential must contain, measured from
 * `Website/release.config` L243 (`minRequiredNonalphanumericCharacters="0"`). at zero the rule is
 * VACUOUS, so a purely alphanumeric credential is valid and NO composition validator is written.
 */
const MINIMUM_NON_ALPHANUMERIC_CHARACTERS = 0;

/**
 * The wording shown when the replacement credential breaches the policy. Measured from the
 * `InvalidPassword.Text` entry of `Website/App_GlobalResources/SharedResources.resx`, whose double spaces
 * after the two sentence periods are part of the original and are intentional.
 */
const PASSWORD_POLICY_MESSAGE =
  `The password specified is invalid.  Please specify a valid password.  Passwords ` +
  `must be at least ${MINIMUM_PASSWORD_LENGTH} characters in length and contain at ` +
  `least ${MINIMUM_NON_ALPHANUMERIC_CHARACTERS} non-alphanumeric characters.`;

// WORDING, MEASURED FROM THE LEGACY RESOURCE FILES
// WHERE THE MARKUP AND THE RESOURCE FILE DISAGREE, THE RESOURCE FILE WINS. The legacy label declarations
// carried a fallback `text` attribute that the resource file overrode at run time, and four of them
// disagree on this screen: `plLastChanged` reads 'Password last Changed:' in `Password.ascx` L15 against
// 'Password Last Changed:' in the resource file; `plOldPassword` reads 'Old Password:' in L34 against
// 'Current Password:'; `plNewConfirm` reads 'Confirm New Password:' in L42 against 'Confirm Password:'; and
// the question-and-answer section head reads 'Change Question And Answer' in L76 against 'Edit Question and
// Answer'.

/** `plLastChanged.Text`. */
const LAST_CHANGED_LABEL = 'Password Last Changed:';

/** `plLastChanged.Help`. */
const LAST_CHANGED_HELP = 'Date password was last changed';

/** `plExpires.Text`. */
const EXPIRES_LABEL = 'Password Expires:';

/** `plExpires.Help`. */
const EXPIRES_HELP = 'Date password will expire';

/** `plOldPassword.Text` — note that the markup fallback said 'Old Password:'. */
const CURRENT_PASSWORD_LABEL = 'Current Password:';

/**
 * The label on the read-only account field - #25.
 *
 * MIGRATION: a net addition. `Password.ascx` showed no account field at all, because a legacy postback form
 * had no password manager to inform. The wording is the one the user listing and the user editor already use
 * for the same fact, so one account is called one thing across the three screens that show it.
 */
const USERNAME_LABEL = 'User Name:';

/** `plOldPassword.Help`. */
const CURRENT_PASSWORD_HELP = 'Enter your current Password';

/** `plNewPassword.Text`. */
const NEW_PASSWORD_LABEL = 'New Password:';

/** `plNewPassword.Help`. */
const NEW_PASSWORD_HELP = 'Enter the new Password';

/** `plNewConfirm.Text` — note that the markup fallback said 'Confirm New Password:'. */
const CONFIRM_PASSWORD_LABEL = 'Confirm Password:';

/** `plNewConfirm.Help`. */
const CONFIRM_PASSWORD_HELP = 'Confirm the new Password';

/** `ChangePassword.Text`, used for both the section heading and its submit affordance. */
const CHANGE_PASSWORD_TEXT = 'Change Password';

/** `ResetPassword.Text`, used for the administrative section and its submit affordance. */
const RESET_PASSWORD_TEXT = 'Reset Password';

/** `UserChangeHelp.Text`, shown whenever the current credential is being asked for. */
const USER_CHANGE_HELP =
  'In order to change your password, you will need to provide your current password, ' +
  'as well as your new password and a confirmation of your new password.';

/** `AdminChangeHelp.Text`, shown when an administrator is acting on another account. */
const ADMIN_CHANGE_HELP =
  'To change a password for this user enter the new password and confirm the entry by ' +
  'typing it again.';

/**
 * The administrative reset help text, adapted from `AdminResetHelp.Text`. MIGRATION: THE SECOND SENTENCE
 * IS DELIBERATELY NOT REPRODUCED VERBATIM. The measured original reads 'You can reset the password for
 * this user.
 */
const ADMIN_RESET_HELP =
  'You can reset the password for this user.  The replacement password must be ' +
  'supplied rather than randomly generated.';

/** `NoExpiry.Text`. */
const NO_EXPIRY_TEXT = 'Password does not Expire';

/** `ForcedExpiry.Text`. */
const FORCED_EXPIRY_TEXT =
  'The Portal Administrator has required you to change your password, before you can ' +
  'log in.';

const PASSWORD_CHANGED_TEXT = 'The password has been reset.';

const MANAGE_PASSWORD_TITLE = 'Manage Password';

// ---------------------------------------------------------------------------
// THE TWO OPERATIONS, AND THE FAILURES THIS SCREEN OWNS
// ---------------------------------------------------------------------------

/**
 * The discriminator value that performs a change: the account holder supplies the credential in force
 * alongside the replacement. Declared as a constant of the imported union type so that a misspelling is a
 * compilation error rather than a request the server rejects as unrecognised.
 */
const OPERATION_CHANGE: ChangePasswordOperation = 'change';

/**
 * The discriminator value that performs an administrative reset: the credential in force is neither
 * supplied nor consulted. The API refuses a reset that carries a current credential, so the request this
 * component builds for a reset transmits null for that member rather than the empty string the legacy
 * code would have sent.
 */
const OPERATION_RESET: ChangePasswordOperation = 'reset';

/**
 * The store commands whose failures belong on this screen. The account store is provided at the
 * application root and holds ONE failure slot shared by every account command, so a failure recorded by a
 * command this screen never issued must not be rendered here.
 */
const OWNED_OPERATIONS: readonly UserOperation[] = Object.freeze([
  'loadUser',
  'changePassword',
  'resetPassword',
] as const);

/** The form control a resolved pre-flight failure belongs beside. */
export type CredentialField = 'currentPassword' | 'newPassword' | 'confirmPassword';

export interface CredentialFailure {
  /** The control the message belongs beside. */
  readonly field: CredentialField;

  /** The plain-text message. */
  readonly message: string;
}

// ---------------------------------------------------------------------------
// THE TYPED FORM
// ---------------------------------------------------------------------------

/** The credential form's shape. */
export interface ChangePasswordFormModel {
  /**
   * The credential in force. Rendered unless an administrator is acting on another account, and REQUIRED
   * only when the caller is not an administrator.
   */
  readonly currentPassword: FormControl<string>;

  /** The replacement credential. Subject to the length rule and to nothing else. */
  readonly newPassword: FormControl<string>;

  /** The replacement repeated, so a typing error is caught before the request. */
  readonly confirmPassword: FormControl<string>;
}

/**
 * The cross-field rules that cannot be expressed on a single control. A MECHANISM CHANGE WITH UNCHANGED
 * BEHAVIOUR. The legacy screen expressed both of these imperatively inside its submit handler — the
 * confirmation comparison at `Password.ascx.vb` L272 and the must-differ comparison at L290 — and the
 * legacy markup declared NO validator at all for either.
 *
 * @param requiresCurrentPassword Whether the current-credential rules apply, read fresh on every
 * evaluation.
 * @returns A validator reporting at most the two group-level codes.
 */
/**
 * Reports the confirming control invalid while it differs from the replacement.
 *
 * Reads its sibling through the parent rather than being handed both values, so it can live ON the control
 * whose box the reader has to be taken to. It mutates nothing - the group validator still owns the same
 * comparison for the message and its ordering - so the two can never disagree: they evaluate identical
 * expressions over identical values.
 *
 * @param control The confirming control.
 * @returns The mismatch code, or `null` while the two agree or the replacement is not yet readable.
 */
function confirmationMatchesValidator(control: AbstractControl): ValidationErrors | null {
  const parent: AbstractControl | null = control.parent;

  if (parent === null) {
    return null;
  }

  const replacement: string = readControlValue(parent, 'newPassword');
  const confirmation: string = typeof control.value === 'string' ? control.value : '';

  return replacement === confirmation ? null : { passwordMismatch: true };
}

function credentialGroupValidator(requiresCurrentPassword: () => boolean): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const currentPassword = readControlValue(group, 'currentPassword');
    const newPassword = readControlValue(group, 'newPassword');
    const confirmPassword = readControlValue(group, 'confirmPassword');

    // L272, and it is FIRST for the same reason it was first there: the arm that
    // reported it exited before the policy check could run.
    if (newPassword !== confirmPassword) {
      return { passwordMismatch: true };
    }

    if (
      requiresCurrentPassword() &&
      newPassword.length > 0 &&
      newPassword === currentPassword
    ) {
      return { passwordNotDifferent: true };
    }

    return null;
  };
}

/**
 * @param group The control group under validation.
 * @param name The control to read.
 * @returns The value, or the empty string when the control is absent or holds a non-string.
 */
function readControlValue(group: AbstractControl, name: CredentialField): string {
  const control: AbstractControl | null = group.get(name);

  if (control === null) {
    return '';
  }

  const value: unknown = control.value;

  return typeof value === 'string' ? value : '';
}

/**
 * Converts the route's `userId` parameter into an account key.
 *
 * @param value The bound value: the route segment as a string, or a number when a template binds one
 * directly.
 * @returns The account key, or `Number.NaN` when the value names none.
 */
function parseRouteUserId(value: unknown): number {
  if (typeof value === 'number') {
    return isRouteId(value) ? value : Number.NaN;
  }
  if (typeof value !== 'string') {
    return Number.NaN;
  }
  return parseRouteId(value) ?? Number.NaN;
}

// ---------------------------------------------------------------------------
// THE COMPONENT
// ---------------------------------------------------------------------------

/**
 * Why the caller is on this screen when they did not choose to be.
 *
 * It does not speculate about the cause - an administrator may have required the change, or the credential
 * may be a shipped default - because the session carries the obligation without carrying its reason, and a
 * guess presented as a fact would be worse than none.
 */
export const CREDENTIAL_REMEDIATION_EXPLANATION =
  'Your password must be changed before you can use the rest of this site. Choose a new one and save;'
  + ' everything else becomes available straight away.';

/**
 * The Manage Password screen. ⚠ THE EXPORTED NAME IS PART OF THE ROUTING CONTRACT. The account feature's
 * route table reaches this class by name through a dynamic import, and a route that resolves to no export
 * renders nothing at all — a blank screen with no compilation error and no console message.
 */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE: exactly one per screen, stating that screen's
 * SCOPE - the record it acts on when the title does not already name it, otherwise what the screen is for
 * in one line - and never a status, a count or a progress readout.
 */
const PAGE_SUBTITLE =
  'Change the password on this account.';

@Component({
  selector: 'app-user-password',
  standalone: true,
  imports: [
    AbsentValueComponent,
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
    ConfirmDialogComponent,
    DateDisplayPipe,
  ],
  templateUrl: './user-password.component.html',
  // Singular, which is the current spelling. The plural form is the legacy one and is
  // deprecated; a component declares one stylesheet here and the singular member says so.
  styleUrl: './user-password.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserPasswordComponent {

  /**
   * Whether a wire instant names a real moment, so a value and its absent affordance can never both be
   * withheld. The display pipe's OWN parser is asked, so "paint the date" and "paint the absent mark" come
   * from one implementation and cannot disagree; it is also what makes the legacy null-date sentinel count as
   * absent here, since that is exactly how the pipe treats it.
   *
   * @param instant The value as it arrived on the wire.
   * @returns True when the value names a real moment.
   */
  protected hasInstant(instant: string | null | undefined): boolean {
    return parseDisplayInstant(instant) !== null;
  }
  // COLLABORATORS
  // Injected through the function form rather than through constructor parameters, which is what lets the
  // field initialisers below read them.

  /**
   * The account store: this screen's only route to the API. the store is consumed rather than bypassed,
   * and the decision was made by reading it rather than by assuming.
   */
  private readonly store = inject(UserStore);

  /**
   * This component's own element. Used for exactly one thing: deciding whether the element that currently
   * holds focus belongs to THIS screen, which is what {@link resetEntryState} needs before it may blur it.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * The authentication store, read for the CALLER's identity. Required because the two gates this screen
   * turns on — which fields to render and which rules to enforce — are predicates about the caller rather
   * than about the account being administered.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The notification queue, used for the SUCCESS announcement and for nothing else. The HTTP failure
   * interceptor already announces every failure it sees, so a failure announcement here would show the
   * same sentence twice and, for a permission refusal, would show it at the wrong severity.
   */
  private readonly notifications = inject(NotificationService);

  /**
   * The router, used for exactly one navigation: leaving this screen once a MANDATORY credential change
   * has been made. Nothing else on this screen navigates.
   */
  private readonly router = inject(Router);

  // -------------------------------------------------------------------------
  // THE ROUTE INPUT
  // -------------------------------------------------------------------------

  /** The account to administer, bound from the route segment `:userId`. */
  readonly userId = input.required<number, unknown>({ transform: parseRouteUserId });

  // LOCAL STATE
  // ⚠ NONE OF THESE HOLDS A CREDENTIAL. The three credential values live in the form's own controls and are
  // cleared as soon as a write succeeds.

  /** Whether the administrative-reset confirmation is mounted. */
  private readonly _resetConfirmOpen = signal(false);

  /** Which operation is awaiting its answer, or null when none is. */
  private readonly _pendingOperation = signal<ChangePasswordOperation | null>(null);

  /**
   * Whether a submission has been attempted. the legacy screen showed no validation message until a
   * postback, because it had no client-side validation at all to show one from.
   */
  private readonly _submitAttempted = signal(false);

  /**
   * A monotonic counter bumped whenever the validity of the form may have changed for a reason the
   * template cannot observe. The form model is not a signal, so a derivation over it would not recompute;
   * the template re-evaluates the resolution methods below on every check, which covers every change a
   * person makes.
   */
  private readonly _validityRevision = signal(0);

  /**
   * The account this screen has most recently been set up for, or `undefined` before the first resolution.
   *
   * A THREE-STATE FIELD, and the third state is load-bearing: `undefined` means "no account has been
   * observed yet", which must not be treated as a change, while `null` is the real observed state of an
   * address that names no account. Collapsing the two would reset the form on first render for no reason.
   */
  private administeredKey: number | null | undefined = undefined;

  // THE CALLER'S IDENTITY, AND THE TWO GATES IT TURNS
  // ⚠ DECLARED BEFORE THE FORM ON PURPOSE. Class field initialisers run in declaration order, and the
  // form's group validator reads {@link requiresCurrentPassword} the moment the group is constructed,
  // because a control group validates itself on construction.

  /**
   * The account key the route named, or null when it named none. ⚠ Tested for absence as null, never for
   * truth.
   */
  readonly accountKey: Signal<number | null> = computed(() => {
    const bound = this.userId();

    return Number.isFinite(bound) ? bound : null;
  });

  readonly isAdmin: Signal<boolean> = this.auth.administersCurrentPortal;

  /**
   * Whether the CALLER is the account on the screen. Reproduces `UserModuleBase.IsUser`, including its
   * behaviour for an unauthenticated caller: the legacy property returned false without comparing, and an
   * absent current identity resolves to false here for the same reason. ⚠ Compared with strict equality
   * against an explicitly resolved key.
   */
  /**
   * Whether this screen is being shown BECAUSE the caller's own session cannot proceed without it.
   *
   * ⚠ THE SAME SILENT LANDING AS ITS PROFILE COUNTERPART, and with one extra consequence: this obligation
   * was measured to be UNENFORCED as well as unexplained - an in-page navigation probe survived a click away
   * from this screen. The enforcement now lives in the session gate, which sends a restricted caller back
   * here from any other address; what belongs on the screen is the reason.
   */
  protected readonly landedForRemediation: Signal<boolean> = computed(
    () => this.auth.mustChangePassword() && this.isSelf(),
  );

  /** The sentence that explains the landing. */
  protected readonly remediationExplanation = CREDENTIAL_REMEDIATION_EXPLANATION;

  readonly isSelf: Signal<boolean> = computed(() => {
    const key = this.accountKey();
    const caller = this.auth.currentUser();

    if (key === null || caller === null) {
      return false;
    }

    return caller.userId === key;
  });

  // -------------------------------------------------------------------------
  // WHICH OPERATION A SUBMISSION PERFORMS
  // -------------------------------------------------------------------------

  /**
   * The operation a submission would perform. THE ONE LEGACY BUTTON BECOMES ONE OF TWO ENDPOINTS, CHOSEN
   * BY WHO THE CALLER IS. The legacy screen called a single routine for every caller and passed the empty
   * string as the credential in force when an administrator was acting on another account.
   */
  readonly plannedOperation: Signal<ChangePasswordOperation> = computed(() =>
    this.isSelf() ? OPERATION_CHANGE : OPERATION_RESET,
  );

  /**
   * Whether the server would authorise the operation this caller resolves to. ⚠ THIS EXISTS BECAUSE THE
   * ROUTE NOW ADMITS TWO DIFFERENT CALLERS FOR TWO DIFFERENT OPERATIONS, and admitting a union means one
   * of them can arrive at the operation that is not theirs.
   */
  readonly operationPermitted: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_CHANGE || this.isAdmin(),
  );

  /**
   * Whether the current-credential rules APPLY. ⚠ GATED ON THE OPERATION, NOT ON THE CALLER'S ROLE, AND
   * THE CHANGE IS DELIBERATE. The legacy screen gated DISPLAY on `IsAdmin And Not IsUser` but gated both
   * ENFORCEMENT rules on `Not IsAdmin` alone (`:L284` and `:L290`), so an administrator changing their
   * OWN credential saw the control and was excused the rules.
   */
  readonly requiresCurrentPassword: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_CHANGE,
  );

  /**
   * Whether the current-credential control is RENDERED. The negation of `IsAdmin And Not IsUser` from
   * `Password.ascx.vb` L150-L152, where the administrator arm hid the row and switched the help text.
   * MIGRATION: `Password.ascx.vb` L144 is NOT reproduced.
   */
  readonly showCurrentPassword: Signal<boolean> = computed(
    () => !(this.isAdmin() && !this.isSelf()),
  );

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The typing ceiling emitted on all three credential inputs. Shared from
   * `core/utils/credential-bounds.util.ts`, which holds the API's own bound and the reasoning behind it,
   * so this screen cannot drift away from the server rule.
   */
  readonly credentialMaxLength = CREDENTIAL_MAX_LENGTH;

  /**
   * Whether the current-credential rules apply, as a PLAIN FIELD the group validator can read. ⚠ A MIRROR
   * OF {@link UserPasswordComponent.requiresCurrentPassword}, AND IT EXISTS FOR ONE CONCRETE REASON: the
   * `FormGroup` constructor runs its validators immediately, while the class is still initialising its
   * fields.
   */
  private currentCredentialRuleApplies = false;

  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   *
   * ⚠ THE BUSY EXCLUSION WAS REMOVED, AND ITS REMOVAL CLOSES A MEASURED HOLE. This predicate used to read
   * `dirty && busy === false`, which reported the screen CLEAN for exactly as long as a write was in flight -
   * so navigating away mid-save was admitted in silence, the departure destroyed the component, and
   * `takeUntilDestroyed` cancelled the request. The operator lost the write and was told nothing. A form
   * holding an unfinished write is the LEAST safe moment to leave, not the safest.
   *
   * The exclusion was written to stop the application's OWN post-save navigation being challenged, and that
   * case is already covered properly: every success path replaces the address imperatively, which
   * `unsavedChangesGuard` admits explicitly. Nothing here has to approximate it a second time.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty,
  );

  /**
   * The credential form. Every control is non-nullable, so the raw value is fully typed and resetting
   * returns each control to the empty string rather than to null.
   */
  readonly form = new FormGroup<ChangePasswordFormModel>(
    {
      currentPassword: new FormControl('', { nonNullable: true }),
      newPassword: new FormControl('', {
        nonNullable: true,
        // Both rules resolve to the SAME message, and that is the measured behaviour rather than a
        // convenience: the legacy policy check was a single call whose length comparison failed identically
        // for an empty credential and for a short one, so there was one outcome and one message.
        validators: [Validators.required, Validators.minLength(MINIMUM_PASSWORD_LENGTH)],
      }),
      // ⚠ #25 — IT CARRIES ITS OWN RULES, AND THAT IS WHAT MAKES THE REFUSAL REACHABLE. This control used to
      // declare no validator at all, on the reasoning that the comparison needs both values and therefore
      // belongs to the group - true of where the comparison is EVALUATED, and wrong about where its outcome
      // has to appear. A group-only error leaves every control valid, so the confirming box was never
      // `.ng-invalid`: it got no `aria-invalid`, the shared "focus the first invalid control" directive found
      // nothing to focus, and a mismatch left focus sitting on the submit button while the message it had
      // just produced was rendered somewhere the reader had not been taken to. A required rule of its own
      // additionally makes an EMPTY confirmation report as the omission it is.
      confirmPassword: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, confirmationMatchesValidator],
      }),
    },
    { validators: [credentialGroupValidator(() => this.currentCredentialRuleApplies)] },
  );

  // -------------------------------------------------------------------------
  // READ-ONLY PROJECTIONS OF STORE STATE
  // -------------------------------------------------------------------------

  /**
   * The account being administered, or null when none has been read for THIS route. The store's selection
   * is filtered against the route's own key rather than trusted blindly.
   */
  readonly user: Signal<UserDetail | null> = computed(() => {
    const held = this.store.selectedUser();
    const key = this.accountKey();

    if (held === null || key === null) {
      return null;
    }

    return held.userId === key ? held : null;
  });

  /** Whether the account is being read. */
  readonly loading: Signal<boolean> = computed(() => this.store.selectedUserLoading());

  /**
   * Whether a write is in flight. The store's flag is shared by every account write, which is the correct
   * thing to disable a submit affordance on: a second write dispatched while the first is outstanding
   * would race it, and the store cancels rather than queues.
   */
  readonly saving: Signal<boolean> = computed(() => this.store.saving());

  /**
   * The failure to render, or null when there is none this screen owns. Handed straight to the shared
   * error banner, which resolves the wording and the severity itself.
   */
  readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null) {
      return null;
    }

    return OWNED_OPERATIONS.includes(failure.operation) ? failure.problem : null;
  });

  /**
   * The sentence to show when a failure this screen owns carried NO problem document. ⚠ THE FAILURE THIS
   * MAKES VISIBLE WAS COMPLETELY SILENT, AND ON THIS SCREEN IT LEFT THE FORM UNUSABLE. The runtime
   * decoders that check each response against its published contract run inside the service's own
   * mapping, which is DOWNSTREAM of the interceptor's error handling — so a `200` whose body does not
   * match its contract throws a plain error with no document, no status and no support reference. {@link
   * problem} is `null` for it, so the banner rendered nothing while the account read had not committed,
   * and the operator was left looking at a credential form that would not submit and gave no reason.
   */
  readonly failureSummary: Signal<string | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null || failure.problem !== null) {
      return null;
    }

    return OWNED_OPERATIONS.includes(failure.operation) ? failure.summary.message : null;
  });

  /** Whether the administrative-reset confirmation is mounted. */
  readonly resetConfirmOpen: Signal<boolean> = this._resetConfirmOpen.asReadonly();

  /** Whether a submission has been attempted, which is what un-hides pre-flight messages. */
  readonly submitAttempted: Signal<boolean> = this._submitAttempted.asReadonly();

  /** Whether the route named an account this screen cannot resolve. */
  readonly routeUnresolved: Signal<boolean> = computed(() => this.accountKey() === null);

  /**
   * Whether the account is unavailable — read, and not there. True only once the read has settled, so the
   * empty state does not flash before the first response.
   */
  readonly accountUnavailable: Signal<boolean> = computed(
    () => !this.routeUnresolved() && !this.loading() && this.user() === null,
  );

  readonly accountDetailWithheld: Signal<boolean> = computed(() => this.auth.sessionRestricted());

  /**
   * Whether the credential form can be presented. True once the route names an account AND the screen is
   * not waiting on a read it actually issued.
   */
  readonly credentialFormAvailable: Signal<boolean> = computed(
    () => this.accountKey() !== null && (this.user() !== null || this.accountDetailWithheld()),
  );

  // -------------------------------------------------------------------------
  // DERIVED WORDING
  // -------------------------------------------------------------------------

  /** The one-line scope statement shown beneath the title. */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

  readonly pageTitle: Signal<string> = computed(() => {
    const account = this.user();

    if (!this.isAdmin() || account === null) {
      return MANAGE_PASSWORD_TITLE;
    }

    return `${MANAGE_PASSWORD_TITLE} - ${account.username} (Id: ${account.userId})`;
  });

  /**
   * The help text above the credential fields. Exactly the two arms of `Password.ascx.vb` L150-L155: the
   * administrator acting on another account is told to enter and confirm a replacement, and everyone else
   * is told that the credential in force is required as well.
   */
  readonly changeHelpText: Signal<string> = computed(() =>
    this.showCurrentPassword() ? USER_CHANGE_HELP : ADMIN_CHANGE_HELP,
  );

  /**
   * The help text for the administrative reset, or the empty string when the caller is not performing
   * one. Reproduces the visibility rule of `Password.ascx.vb` L160-L184 as that rule resolves under the
   * shipped configuration.
   */
  readonly resetHelpText: Signal<string> = computed(() =>
    this.isAdmin() && !this.isSelf() ? ADMIN_RESET_HELP : '',
  );

  /**
   * The wording of the safe exit.
   *
   * ⚠ MIGRATION: AN ADDED AFFORDANCE, NOT A PORTED ONE. `Website/admin/Users/Password.ascx` declares
   * `cmdUpdate`, `cmdReset` and `cmdUpdateQA` and no cancel, while the portal, signup and role editors all
   * declare `cmdCancel` - so this screen was reproduced faithfully and ended up as one of the two that an
   * operator could not back out of. Recorded in MIGRATION_NOTES.md.
   */
  readonly cancelLabel: string = 'Cancel';

  /** The label for the submit affordance. */
  readonly submitLabel: Signal<string> = computed(() =>
    this.plannedOperation() === OPERATION_RESET ? RESET_PASSWORD_TEXT : CHANGE_PASSWORD_TEXT,
  );

  /**
   * The caption of the one credential section. ⚠ THE SAME SIGNAL AS THE SUBMIT LABEL, ALIASED RATHER THAN
   * RECOMPUTED, so the caption and the command it contains cannot come to name different operations. That
   * is not a convenience: the defect this replaces was precisely a caption naming one operation above the
   * boxes belonging to another, with the command naming a third possibility at the foot of the form.
   */
  readonly sectionHeading: Signal<string> = this.submitLabel;

  /**
   * Every help sentence that applies to the operation being performed, in reading order. A LIST RATHER
   * THAN A STRING, because a reset legitimately has two things to say and both are measured resource
   * wording: what the operation does, and how to supply the replacement.
   */
  readonly sectionHelp: Signal<readonly string[]> = computed(() => {
    const change: string = this.changeHelpText();

    if (this.plannedOperation() !== OPERATION_RESET) {
      return [change];
    }

    const reset: string = this.resetHelpText();

    // The reset sentence is empty for a caller the reset panel was hidden from, and an empty
    // paragraph would render as a blank line rather than as nothing.
    return reset === '' ? [change] : [reset, change];
  });

  /**
   * The instant the credential was last changed, for the template to render through the `dateDisplay`
   * pipe. Null when no account has been read.
   */
  readonly lastChangedDisplay: Signal<string | null> = computed(() => {
    const account = this.user();

    return account === null ? null : account.lastPasswordChangeDate;
  });

  /**
   * The resolved expiry wording. ONE OF THE THREE LEGACY BRANCHES IS NOT REPRESENTABLE, AND THE OMISSION
   * IS DELIBERATE RATHER THAN OVERSIGHT. `Password.ascx.vb` L132-L140 chose between three outcomes: a
   * forced change, an expiry date computed as the last change plus a configured number of days, and
   * 'Password does not Expire' when that number was zero.
   */
  readonly expiryDisplay: Signal<string> = computed(() => {
    const account = this.user();

    if (account === null) {
      return '';
    }

    // `mustChangePassword` is the successor of the legacy update-credential flag, and false is DATA here
    // rather than an absence: the contract declares it a plain boolean precisely because the legacy
    // absent-marker for a boolean WAS false, so admitting a third state would invent a distinction the
    // source data cannot make.
    return account.mustChangePassword ? FORCED_EXPIRY_TEXT : NO_EXPIRY_TEXT;
  });

  /**
   * The policy wording, shown as the replacement credential's help text. A signal rather than a bare
   * constant so that the template reads every piece of wording the same way, and so that the requirement
   * is stated where a person can act on it rather than only after a rejection.
   */
  readonly policyMessage: Signal<string> = computed(() => PASSWORD_POLICY_MESSAGE);

  // STATIC WORDING THE TEMPLATE BINDS

  /** `plLastChanged.Text`. */
  readonly lastChangedLabel = LAST_CHANGED_LABEL;

  /** `plLastChanged.Help`. */
  readonly lastChangedHelp = LAST_CHANGED_HELP;

  /** `plExpires.Text`. */
  readonly expiresLabel = EXPIRES_LABEL;

  /** `plExpires.Help`. */
  readonly expiresHelp = EXPIRES_HELP;

  /**
   * `plOldPassword.Text`. D9 — THE ONE LEGACY DEFECT THIS SCREEN CORRECTS is a label association, and it
   * is corrected because accessibility parity demands a working label.
   */
  readonly usernameLabel = USERNAME_LABEL;

  readonly currentPasswordLabel = CURRENT_PASSWORD_LABEL;

  /** `plOldPassword.Help`. */
  readonly currentPasswordHelp = CURRENT_PASSWORD_HELP;

  /** `plNewPassword.Text`. */
  readonly newPasswordLabel = NEW_PASSWORD_LABEL;

  /** `plNewPassword.Help`. */
  readonly newPasswordHelp = NEW_PASSWORD_HELP;

  /** `plNewConfirm.Text`. */
  readonly confirmPasswordLabel = CONFIRM_PASSWORD_LABEL;

  /** `plNewConfirm.Help`. */
  readonly confirmPasswordHelp = CONFIRM_PASSWORD_HELP;

  /** `ResetPassword.Text`, for the confirmation dialog's title and its confirming control. */
  readonly resetSectionHeading = RESET_PASSWORD_TEXT;

  /**
   * Whether a submission needs the administrative-reset confirmation first. MIGRATION: the confirmation
   * is NET-NEW, and it is scoped to the case that warrants it.
   */
  readonly requiresResetConfirmation: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_RESET,
  );

  /**
   * The confirmation question. Names the account so that an operator with several tabs open can see which
   * account the dialog is about.
   */
  readonly resetConfirmMessage: Signal<string> = computed(() => {
    const account = this.user();

    if (account === null) {
      return 'Reset this account\u2019s password to the replacement supplied?';
    }

    return `Reset the password for ${account.username} to the replacement supplied?`;
  });

  // PRE-FLIGHT FAILURE RESOLUTION

  /**
   * Reads the enforcement gate in a way a template expression can depend on.
   *
   * @returns Whether the current-credential rules apply.
   */
  private revisionDependentGate(): boolean {
    // The counter is read for its dependency, not for its value; the void expression states
    // that explicitly so that a reader does not look for the number being used.
    void this._validityRevision();

    return this.requiresCurrentPassword();
  }

  /**
   * EVERY unmet pre-flight rule, in the LEGACY ORDER (L272 -> L278 -> L284 -> L290).
   *
   * ⚠ THE LEGACY ORDER IS STILL REPRODUCED EXACTLY, AND STILL DECIDES THE OUTCOME. What is no longer
   * reproduced is the legacy screen's ONE-AT-A-TIME DISCLOSURE, and the distinction matters. Legacy
   * `cmdUpdate_Click` was a chain of `If ... Exit Sub` arms, so the first unmet rule was the only one a
   * person ever saw. That was an ARTIFACT OF EARLY EXIT, not a validation rule: a six-character
   * replacement that also failed to match its confirmation was rejected for the mismatch, the operator
   * corrected the mismatch, and the screen then rejected it again for a length requirement it had never
   * stated. Disclosing a rule only once another is satisfied is the same defect class as reporting a
   * weaker limit before the binding one, and it is fixed here by stating every unmet rule at once.
   *
   * WHAT IS PRESERVED, PROVABLY:
   * - `firstFailure()` is `unmetRules()[0]`, so the PRIMARY message is byte-identical to the message the
   *   legacy chain would have chosen for the same input.
   * - This list is non-empty for exactly the inputs on which the legacy chain exited, so the ACCEPT/REJECT
   *   decision is unchanged. The one suppression below cannot alter that, because it only withholds a rule
   *   in a state where an earlier rule has already contributed an entry.
   * - Every message is the measured legacy wording, unchanged.
   *
   * Each rule belongs to its own control, so the messages render beside the field each concerns rather
   * than accumulating in one list.
   *
   * @returns Every unmet rule, legacy-ordered; empty when the form may be submitted.
   */
  unmetRules(): readonly CredentialFailure[] {
    const controls = this.form.controls;
    const requiresCurrent = this.revisionDependentGate();
    const failures: CredentialFailure[] = [];

    // Read once: it both contributes a rule and gates the must-differ rule below.
    const policyBreached = controls.newPassword.invalid;

    // 1. L272 - the replacement and its confirmation must match.
    if (this.form.hasError('passwordMismatch')) {
      failures.push({
        field: 'confirmPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.mismatch'],
      });
    }

    // 2. L278 - the replacement must satisfy the policy. THIS IS THE RULE THAT USED TO BE HIDDEN
    //    whenever the confirmation also failed to match.
    if (policyBreached) {
      failures.push({ field: 'newPassword', message: this.policyMessage() });
    }

    // 3. L284 - the credential in force must be supplied, when the gate says it applies.
    if (requiresCurrent && controls.currentPassword.value === '') {
      failures.push({
        field: 'currentPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.missing'],
      });
    }

    // 4. L290 - the replacement must differ from the credential in force. ⚠ WITHHELD WHILE THE POLICY IS
    //    BREACHED, for two reasons: legacy could never surface both (L278 exited first), and a replacement
    //    that must change to satisfy the policy cannot usefully also be told it must change to differ.
    //    Because L278 has already contributed an entry in that state, withholding this one cannot turn a
    //    rejection into an acceptance, and it cannot change which message leads.
    if (!policyBreached && this.form.hasError('passwordNotDifferent')) {
      failures.push({
        field: 'newPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.not_different'],
      });
    }

    return failures;
  }

  /**
   * The pre-flight failure that DECIDES the outcome, and the one a reader should treat as primary.
   *
   * Derived from {@link unmetRules} rather than resolved separately, so the two can never disagree about
   * whether the form may be submitted.
   *
   * @returns The leading failure, or null when nothing is wrong.
   */
  firstFailure(): CredentialFailure | null {
    return this.unmetRules()[0] ?? null;
  }

  /**
   * The message to show beside one control. Two sources, in order.
   *
   * @param field The control to resolve a message for.
   * @returns The message, or null when the control has none to show.
   */
  fieldError(field: CredentialField): string | null {
    const control: AbstractControl = this.form.controls[field];

    if (this._submitAttempted() || control.touched) {
      // The rule for THIS control, drawn from the full set rather than from the leading failure alone -
      // which is what lets a length requirement and a mismatch be stated at the same time, each beside
      // the field it concerns. At most one rule can resolve per control.
      const failure = this.unmetRules().find((candidate) => candidate.field === field);

      if (failure !== undefined) {
        return failure.message;
      }
    }

    return fieldErrorMessage(this.problem(), field);
  }

  /**
   * The message for the credential in force.
   *
   * @returns The message, or null when there is none.
   */
  currentPasswordError(): string | null {
    return this.fieldError('currentPassword');
  }

  /**
   * The message for the replacement credential.
   *
   * @returns The message, or null when there is none.
   */
  newPasswordError(): string | null {
    return this.fieldError('newPassword');
  }

  /**
   * The message for the confirmation.
   *
   * @returns The message, or null when there is none.
   */
  confirmPasswordError(): string | null {
    return this.fieldError('confirmPassword');
  }

  /**
   * The account the credential belongs to, or `null` before it has been read.
   *
   * ⚠ #25 — Rendered as a read-only field so a password manager can attribute what it saves. @see the
   * template note beside the field.
   */
  accountUsername(): string | null {
    return this.user()?.username ?? null;
  }

  /** @returns Whether to disable the affordance. */
  submitDisabled(): boolean {
    return this.saving() || this.accountKey() === null || !this.operationPermitted();
  }

  // -------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------

  /**
   * Wires the three reactions this screen needs. ⚠ EACH IS A GENUINE SIDE EFFECT, WHICH IS THE ONLY THING
   * AN EFFECT IS FOR. One issues a request, one reconfigures the form, and one announces an outcome; none
   * of them derives a value, because a derived value is a derivation and every derivation on this screen
   * is one. ⚠ EVERY WRITE IS PERFORMED UNTRACKED. Without that, an effect would take a dependency on
   * whatever the code it calls happens to read, and the first of the three would take a dependency on the
   * very selection it establishes — which re-runs it, which re-issues the request, which is an unbounded
   * loop that no test with a stubbed transport would ever reveal.
   */
  constructor() {
    // 0. ⚠ #25 — THE CONFIRMING CONTROL IS RE-EVALUATED WHEN THE REPLACEMENT CHANGES. A control's validators
    //    run when THAT control changes and at no other time, so without this the confirming box would keep a
    //    mismatch error after the replacement had been corrected to agree with it - the reader fixes the
    //    field the message pointed at and the message stays. `emitEvent: false` stops the re-evaluation from
    //    raising a further change event, which is what keeps this from feeding itself.
    this.form.controls.newPassword.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this.form.controls.confirmPassword.updateValueAndValidity({ emitEvent: false });
      });

    // 1. THE ROUTE DRIVES THE READ. Re-runs when the route names a different account,
    //    which is what makes an in-place navigation from one account to another correct.
    effect(() => {
      const key = this.accountKey();

      if (key === null) {
        // Nothing to read. The unresolved-route state renders instead, and no request is
        // issued for a key that does not exist.
        return;
      }

      if (this.accountDetailWithheld()) {
        return;
      }

      untracked(() => {
        this.store.selectUser(key);
      });
    });

    // 1b. AND THE ROUTE ALSO CLEARS WHAT THE PREVIOUS ACCOUNT ACCUMULATED.
    //
    // ⚠ THIS SCREEN IS REUSED IN PLACE BETWEEN SIBLING ACCOUNTS, AND EVERYTHING IT REMEMBERS IS ABOUT THE
    // ACCOUNT IT REMEMBERED IT FOR. The router reuses one component instance across `/users/3/password` and
    // `/users/1/password` - it is the same route with a different parameter - so no constructor runs, no
    // form is rebuilt, and nothing above resets. Measured: submit the form empty on one account, then move
    // in-app to another, and the new account's Current and New Password fields arrive already touched and
    // already invalid, with assertive messages about a submission made against somebody else's record. A
    // FRESH mount of the same screen is clean, which is what makes it a leak rather than a policy.
    //
    // A dedicated effect with exactly ONE dependency, and that narrowness is deliberate: folding this into
    // the read effect above would make the reset fire whenever the withheld gate flips too, which can happen
    // while an operator is mid-entry and would discard what they had typed. Guarded on the key having
    // actually CHANGED - a first resolution has nothing to clear, and re-resolving the same key must leave
    // the form alone.
    effect(() => {
      const key = this.accountKey();

      untracked(() => {
        const previous: number | null | undefined = this.administeredKey;

        this.administeredKey = key;

        if (previous === undefined || previous === key) {
          return;
        }

        this.resetEntryState();
      });
    });

    // 2. THE ENFORCEMENT GATE DRIVES THE REQUIRED RULE. Re-runs only when the gate itself
    //    flips, which happens once, when the caller's identity resolves.
    effect(() => {
      const requiresCurrent = this.requiresCurrentPassword();

      untracked(() => {
        // Written FIRST, because the re-validation the next line performs is what evaluates the
        // group rule that reads it.
        this.currentCredentialRuleApplies = requiresCurrent;
        this.applyCurrentPasswordRule(requiresCurrent);
      });
    });

    effect(() => {
      const pending = this._pendingOperation();

      if (pending === null || this.saving()) {
        // Either this screen has nothing outstanding, or the write has not settled yet.
        return;
      }

      // Read INSIDE the settled branch so that the failure slot is a dependency only while
      // a write of this screen's own is awaiting its outcome.
      const failure = this.store.failure();
      const failed =
        failure !== null &&
        (failure.operation === 'changePassword' || failure.operation === 'resetPassword');

      untracked(() => {
        this._pendingOperation.set(null);

        if (failed) {
          // The failure renders through the banner and has already been announced by the HTTP failure
          // interceptor.
          return;
        }

        this.announceSuccess();
      });
    });
  }

  /**
   * Returns this screen to the state a fresh mount would give it.
   *
   * ⚠ THE STORE'S FAILURE IS CLEARED TOO, AND LEAVING IT WAS HALF THE LEAK. {@link problem} narrows the
   * shared failure slot by OPERATION but not by account, so a refused credential change against one record
   * went on being rendered in the banner - complete with its support reference - above a form addressing a
   * different one. An operator would have read somebody else's refusal as their own.
   *
   * Everything else here is the recipe {@link UserPasswordComponent.announceSuccess} already uses after a
   * successful write, which is the same requirement stated the other way round: this screen has one notion
   * of "clean" and both paths reach it.
   */
  private resetEntryState(): void {
    // ⚠ FOCUS IS SURRENDERED FIRST, AND THE ORDER IS THE WHOLE OF WHY THIS WORKS. A refused submission
    // leaves focus on the first failing control, and Angular marks a control touched when it BLURS. The
    // form is unmounted the moment the account key changes - it waits on the new account's read - so the
    // focused input is destroyed, and destroying a focused element fires a blur. A reset performed before
    // that blur is silently undone by it: measured, the replacement box arrived untouched and was marked
    // touched again a moment later, and its message came straight back. Blurring here means the touch
    // happens BEFORE the reset rather than after it, and nothing is left pending that could re-mark the
    // control. It costs no orientation: the element holding focus is about to be destroyed either way.
    const focused: Element | null = this.host.nativeElement.ownerDocument.activeElement;

    if (focused instanceof HTMLElement && this.host.nativeElement.contains(focused)) {
      focused.blur();
    }

    // Returns every control to the empty string rather than to null - the point of constructing them
    // non-nullable - and returns the group to pristine and untouched in the same call.
    this.form.reset();
    this._submitAttempted.set(false);
    this._resetConfirmOpen.set(false);
    this._pendingOperation.set(null);
    this.store.clearFailure();

    // The rule set attached to `currentPassword` belongs to the CALLER rather than to the account being
    // administered, so it is left in force; the revision is bumped so the field messages recompute against
    // the newly pristine controls.
    this._validityRevision.update((revision) => revision + 1);
  }

  /**
   * Attaches or detaches the required rule on the credential in force. the rule is moved rather than
   * declared once, and the reason is the measured gate asymmetry.
   *
   * @param requiresCurrent Whether the rule applies.
   */
  private applyCurrentPasswordRule(requiresCurrent: boolean): void {
    const control = this.form.controls.currentPassword;

    if (requiresCurrent) {
      // Idempotent: the framework compares by reference before adding, and `Validators.required` is a
      // stable reference, so repeated application cannot accumulate duplicates.
      control.addValidators(Validators.required);
    } else {
      control.removeValidators(Validators.required);
      control.setValue('', { emitEvent: false });
      // Untouched as well as empty, so the field does not carry a message about a value the
      // caller is no longer being asked for.
      control.markAsUntouched();
    }

    control.updateValueAndValidity({ emitEvent: false });
    // The GROUP is re-validated too, because its must-differ rule reads the same gate and a
    // group validator is not re-run by a change outside the form.
    this.form.updateValueAndValidity({ emitEvent: false });
    this._validityRevision.update((revision) => revision + 1);
  }

  /**
   * Announces a successful write and clears the form. the wording is `PasswordChanged.Text` and the
   * severity is the successor of the legacy green success type, both measured from `ManageUsers.ascx.vb`
   * L793-L797, where the legacy screen resolved success through that separately named key rather than
   * through the enumeration member's name.
   */
  private announceSuccess(): void {
    // ⚠ WHETHER THIS SCREEN IS ABOUT TO BE LEFT IS DECIDED ONCE, HERE, and used for both the statement and
    // the departure below.
    const leavingForRemediation = this.accountDetailWithheld() && this.isSelf();

    this.notifications.notify('success', PASSWORD_CHANGED_TEXT, null, leavingForRemediation);

    // Returns every control to the empty string rather than to null, which is the whole point of
    // constructing them non-nullable, and returns the group to pristine and untouched in the same call.
    this.form.reset();
    this._submitAttempted.set(false);
    this._resetConfirmOpen.set(false);

    if (leavingForRemediation) {
      this.concludeRemediation();
    }
  }

  private concludeRemediation(): void {
    this.auth.noteCredentialRemediated();

    // ⚠ REPLACES: a completed credential change must not sit in BACK history, and
    // `core/guards/unsaved-changes.guard.ts` reads this flag to recognise a departure the application
    // initiated. Pushing would make the gate offer to discard a password change that had already succeeded.
    void this.router.navigateByUrl('/', { replaceUrl: true }).catch(() => false);
  }

  // -------------------------------------------------------------------------
  // ACTIONS
  // -------------------------------------------------------------------------

  /**
   * Abandons the credential form and returns to the account this screen administers.
   *
   * ⚠ WHERE IT GOES DEPENDS ON WHO IS HERE, and both destinations are the screen the operator came from. An
   * administrator reached this form from the account editor, so that is where cancelling returns them; a
   * caller changing their own credential reached it from the application root. The departure runs through the
   * router WITHOUT replacing the address, so the unsaved-entry gate sees it and asks before anything typed is
   * discarded.
   */
  onCancel(): void {
    const destination = this.isSelf() ? '/' : `/users/${String(this.userId())}`;

    void this.router.navigateByUrl(destination).catch(() => false);
  }

  /**
   * Handles the form's submission. Reproduces `cmdUpdate_Click`: the four pre-flight rules are resolved
   * in their measured order and the FIRST failure stops the submission, which is what each `Exit Sub`
   * did.
   */
  submit(): void {
    this._submitAttempted.set(true);

    if (this.submitDisabled()) {
      return;
    }

    // Marked before the pre-flight resolution so that whichever message resolves is
    // actually rendered — a message attached to an untouched control is withheld.
    this.form.markAllAsTouched();

    if (this.firstFailure() !== null) {
      return;
    }

    if (this.requiresResetConfirmation()) {
      this._resetConfirmOpen.set(true);

      return;
    }

    this.dispatch(OPERATION_CHANGE);
  }

  /** Confirms the administrative reset. */
  confirmReset(): void {
    this._resetConfirmOpen.set(false);

    if (this.submitDisabled() || this.firstFailure() !== null) {
      return;
    }

    this.dispatch(OPERATION_RESET);
  }

  /** Declines the administrative reset. */
  cancelReset(): void {
    this._resetConfirmOpen.set(false);
  }

  /**
   * Builds the request and hands it to the store. ⚠ THE CREDENTIAL VALUES ARE READ HERE AND NOWHERE ELSE,
   * and they are read straight into the request object.
   *
   * @param operation Which of the two credential operations to perform.
   */
  private dispatch(operation: ChangePasswordOperation): void {
    const key = this.accountKey();

    if (key === null) {
      // Unreachable through the affordance, which is disabled without an account, and
      // stated anyway because the key must be narrowed before it can be passed on.
      return;
    }

    const values = this.form.getRawValue();
    const isReset = operation === OPERATION_RESET;

    const request: ChangePasswordRequest = {
      operation,
      currentPassword: isReset ? null : values.currentPassword,
      newPassword: values.newPassword,
      confirmPassword: values.confirmPassword,
    };

    this._pendingOperation.set(operation);

    if (isReset) {
      this.store.resetPassword(key, request);

      return;
    }

    this.store.changePassword(key, request);
  }
}
