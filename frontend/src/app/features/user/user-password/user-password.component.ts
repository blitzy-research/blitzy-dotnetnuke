//
// The Manage Password screen — route `/users/:userId/password`.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE IS
// ---------------------------------------------------------------------------
// The route leaf that replaces `Website/admin/Users/Password.ascx.vb` (400 lines) and
// its paired markup `Website/admin/Users/Password.ascx` (101 lines). Two of the three
// legacy panels survive — changing a credential and resetting one administratively —
// and the third does not, for a reason recorded under `pnlQA` below.
//
// It is a CONTAINER rather than a presentational component: it reads the route
// parameter, asks the account store to load the account, holds the form, decides
// which of the two credential operations the caller is performing, and announces the
// outcome. It renders no wording of its own that was not measured from a legacy
// resource file, and it holds no state that the legacy screen round-tripped through
// view state.
//
// ---------------------------------------------------------------------------
// NO PROJECT RULES DOCUMENT EXISTS
// ---------------------------------------------------------------------------
// The engagement supplied no rules document. The rules review answers with a single
// line stating that none were provided, and it answers identically for a range that
// begins past the first line, which is what proves the answer is the whole document
// rather than its first line. Nothing in this file is therefore justified by a project
// rule, and nothing is relaxed by their absence either: the six Minimal Change Clause
// items, the enterprise baseline and the non-functional requirements of the action plan
// govern instead, and each is held to as if it had been written down as a rule.
//
// ---------------------------------------------------------------------------
// THE FIVE MEASURED FACTS THIS SCREEN IS BUILT ON
// ---------------------------------------------------------------------------
// 1. THE DISPLAY GATE AND THE ENFORCEMENT GATE ARE DIFFERENT PREDICATES, and the
//    difference is measured rather than inferred. `Password.ascx.vb` L150 hides the
//    current-credential row when `IsAdmin And Not IsUser`, while L284 and L290 skip
//    the two rules that read it when `Not IsAdmin` alone. For an administrator editing
//    THEIR OWN credential the row is therefore SHOWN and the rules are SKIPPED. That
//    inconsistency is reproduced rather than corrected — see the note on
//    {@link UserPasswordComponent.requiresCurrentPassword}.
// 2. THE FAILURE ORDER IS L272 → L278 → L284 → L290, each arm exiting immediately, so
//    the FIRST failure wins and the confirmation mismatch is reported BEFORE the
//    policy breach. Counter-intuitive, measured, reproduced.
// 3. THE POLICY IS LENGTH ALONE. `Website/release.config` L242-L243 registers the
//    provider with a minimum length of 7 and ZERO required non-alphanumeric
//    characters, and `UserController.vb` L1078-L1079 counts `[^0-9a-zA-Z]` against
//    that zero, which makes the composition rule vacuous. No strength expression was
//    ever configured, so L1084-L1087 is unreachable.
// 4. IDENTITY VALUES CANNOT BE TESTED FOR TRUTH. `Users.UserID` seeds at
//    `IDENTITY(1, 1)`, but `Portals.PortalID` seeds at `IDENTITY(-1, 1)` and role, page
//    and module keys seed at 0, while `Library/Components/Shared/Null.vb` L41-L45
//    simultaneously defines -1 as the marker for a missing integer. One vocabulary
//    cannot carry both meanings, so absence is expressed as null here and is tested for
//    as null — never with a truthiness test, a `> 0` test or a `?? -1` fallback.
// 5. EVERY FAILURE IS ALREADY ANNOUNCED ELSEWHERE. `core/interceptors/error.interceptor.ts`
//    notifies on every HTTP failure it sees and rethrows, so this component raises the
//    SUCCESS notification and nothing else. A second failure notification here would
//    show the same sentence twice, and for a permission refusal it would also show it
//    at the wrong severity — the shared summariser resolves a refusal to a warning, on
//    the evidence of `Website/admin/Security/AccessDenied.ascx.vb` L41-L47, whose two
//    branches both render at the warning type.
//
// ---------------------------------------------------------------------------
// NOTHING HERE EVER HOLDS, LOGS OR ECHOES A CREDENTIAL
// ---------------------------------------------------------------------------
// The three credential values live in the form's own controls and nowhere else: no
// signal holds one, no notification message carries one, no accessible name or
// indicator label mentions one, and there is no diagnostic statement anywhere in this
// file. The controls are cleared the moment a write succeeds, and the current-password
// control is cleared whenever the screen stops asking for it, so a value the caller
// cannot see can never be transmitted.
//
// MIGRATION: password RETRIEVAL is not carried forward, and no screen in this
// application can disclose a credential. The legacy provider was registered with a
// decryptable format and with retrieval switched on, and the symmetric key that
// reversed it was committed to source control in the clear, so anyone who could read
// the repository could read every stored credential. The replacement store is a
// one-way hash, which makes retrieval impossible rather than merely disabled. That key
// value is not reproduced here, in code or in a comment. There is deliberately no
// forgot-password, send-password or recover-password affordance and no endpoint to
// write one against.
//
// ---------------------------------------------------------------------------
// IMPORTS ARE RELATIVE AND BARREL-FREE
// ---------------------------------------------------------------------------
// The workspace declares no `paths` and no `baseUrl`, and there is no `index.ts`
// anywhere, so every import below is relative and names a real file. From this folder
// `..` is the account feature, `../..` is the feature root and `../../..` is the
// application root, which is why `core/` and `shared/` are reached at three levels.
//

import {
  ChangeDetectionStrategy,
  Component,
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
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// ---------------------------------------------------------------------------
// THE POLICY, PRESERVED VERBATIM AND DELIBERATELY NOT TIGHTENED
// ---------------------------------------------------------------------------

/**
 * The minimum credential length, measured from `Website/release.config` L242
 * (`minRequiredPasswordLength="7"`) and confirmed against the API's own
 * `PasswordPolicy:MinRequiredPasswordLength`, which is 7.
 *
 * MIGRATION: THE POLICY IS NOT TIGHTENED. Raising it during a migration would lock out
 * every existing account that satisfies the old rule and not the new one, so the rule
 * is carried across unchanged. This is the ONLY active client-side credential rule.
 */
const MINIMUM_PASSWORD_LENGTH = 7;

/**
 * The number of non-alphanumeric characters the credential must contain, measured from
 * `Website/release.config` L243 (`minRequiredNonalphanumericCharacters="0"`).
 *
 * MIGRATION: at zero the rule is VACUOUS, so a purely alphanumeric credential is
 * valid and NO composition validator is written. The value is declared because it is
 * substituted into the policy wording below, and declaring it makes the vacuity
 * visible instead of leaving a reader to infer it from the absence of a check.
 *
 * MIGRATION: `UserController.vb` L1084-L1087 contains a latent defect — the strength
 * branch ASSIGNS to the validity flag rather than combining with it, so a configured
 * strength expression would have discarded both preceding results. It is not
 * reproduced. It is also not reported as fixed: `passwordStrengthRegularExpression`
 * appears nowhere in the legacy configuration and the API's own policy leaves it
 * empty, so the branch could never fire and there was no behaviour to preserve.
 */
const MINIMUM_NON_ALPHANUMERIC_CHARACTERS = 0;

/**
 * The wording shown when the replacement credential breaches the policy.
 *
 * Measured from the `InvalidPassword.Text` entry of
 * `Website/App_GlobalResources/SharedResources.resx`, whose double spaces after the
 * two sentence periods are part of the original and are intentional. The legacy
 * application resolved its two substitution tokens at run time from the configured
 * policy (`UserController.vb` L607-L609); the token-replacement subsystem is out of
 * scope, so the two numbers are substituted here from the constants above, which is
 * the same result reached by the same arithmetic.
 *
 * MIGRATION: `InvalidPassword.Text` and `PasswordInvalid.Text` are DISTINCT resource
 * keys and only one of them carries the tokens. The legacy screen resolved the
 * token-free `PasswordInvalid.Text` because it keyed the message on the enumeration
 * member name; that wording is not restated here, because the shared failure
 * vocabulary already owns it and this component asks that vocabulary for it. This
 * constant is the CLIENT-SIDE pre-flight wording, which is the token-carrying key,
 * because a message that states the requirement is more use before a request than
 * after one.
 *
 * MIGRATION: the abandoned store's 20-character ceiling is NOT mentioned and NOT
 * enforced. The legacy markup carried `maxlength="20"` on all five credential boxes
 * and the original column was `nvarchar(20)`, but that was a storage limit on a
 * reversibly encrypted value rather than a policy, and the replacement store hashes to
 * a fixed width. No maximum-length validator is written and no ceiling is surfaced.
 */
const PASSWORD_POLICY_MESSAGE =
  `The password specified is invalid.  Please specify a valid password.  Passwords ` +
  `must be at least ${MINIMUM_PASSWORD_LENGTH} characters in length and contain at ` +
  `least ${MINIMUM_NON_ALPHANUMERIC_CHARACTERS} non-alphanumeric characters.`;

// ---------------------------------------------------------------------------
// WORDING, MEASURED FROM THE LEGACY RESOURCE FILES
// ---------------------------------------------------------------------------
//
// MIGRATION: WHERE THE MARKUP AND THE RESOURCE FILE DISAGREE, THE RESOURCE FILE WINS.
// The legacy label declarations carried a fallback `text` attribute that the resource
// file overrode at run time, and four of them disagree on this screen:
// `plLastChanged` reads 'Password last Changed:' in `Password.ascx` L15 against
// 'Password Last Changed:' in the resource file; `plOldPassword` reads 'Old Password:'
// in L34 against 'Current Password:'; `plNewConfirm` reads 'Confirm New Password:' in
// L42 against 'Confirm Password:'; and the question-and-answer section head reads
// 'Change Question And Answer' in L76 against 'Edit Question and Answer'. The resource
// value is what a person actually saw, so it is what is reproduced. The fourth belongs
// to the dropped panel and is recorded only for completeness.
//
// MIGRATION: localisation itself is NOT carried forward. The legacy mechanism was Web
// Forms specific and no translation runtime is introduced, so these strings are
// authored here with the resource values as the authority for their wording, which is
// what keeps the surface recognisable to an existing operator.

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
 * The administrative reset help text, adapted from `AdminResetHelp.Text`.
 *
 * MIGRATION: THE SECOND SENTENCE IS DELIBERATELY NOT REPRODUCED VERBATIM. The measured
 * original reads 'You can reset the password for this user.  The password will be
 * randomly generated.' Random generation is not carried forward — the legacy reset
 * generated a credential server-side and mailed it to the account holder, which
 * required a store that could reproduce it — so the second sentence would be a false
 * statement on the screen. The first sentence is verbatim; the second states what the
 * target actually does. Displaying wording that instructs a person to expect something
 * the system will not do is a defect, not fidelity, so the divergence is made here and
 * recorded rather than absorbed.
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

/**
 * The success wording, measured from the `PasswordChanged.Text` entry of
 * `SharedResources.resx`.
 *
 * MIGRATION: there is NO `Success.Text` key in that file. The legacy screen resolved
 * every failure by the enumeration member's own name but resolved success through this
 * separately named key (`ManageUsers.ascx.vb` L797), and it used the same key for a
 * change and for a reset, so one string serves both operations here as well.
 */
const PASSWORD_CHANGED_TEXT = 'The password has been reset.';

/**
 * The screen title, from `PasswordTitle.Text` — 'Manage Password - {0} (Id: {1})'.
 *
 * MIGRATION: the legacy screen hid its title row entirely for a non-administrator
 * (`Password.ascx.vb` L126). The shared page header emits the page's single top-level
 * heading unconditionally and rejects a blank title, so a heading is always rendered;
 * the neutral form is the same wording with the account clause omitted rather than
 * newly invented text. The account clause is still restricted to an administrator, so
 * no account name or key is disclosed to a caller the legacy screen withheld it from.
 */
const MANAGE_PASSWORD_TITLE = 'Manage Password';

// ---------------------------------------------------------------------------
// THE TWO OPERATIONS, AND THE FAILURES THIS SCREEN OWNS
// ---------------------------------------------------------------------------

/**
 * The discriminator value that performs a change: the account holder supplies the
 * credential in force alongside the replacement.
 *
 * Declared as a constant of the imported union type so that a misspelling is a
 * compilation error rather than a request the server rejects as unrecognised.
 */
const OPERATION_CHANGE: ChangePasswordOperation = 'change';

/**
 * The discriminator value that performs an administrative reset: the credential in
 * force is neither supplied nor consulted.
 *
 * The API refuses a reset that carries a current credential, so the request this
 * component builds for a reset transmits null for that member rather than the empty
 * string the legacy code would have sent.
 */
const OPERATION_RESET: ChangePasswordOperation = 'reset';

/**
 * The store commands whose failures belong on this screen.
 *
 * The account store is provided at the application root and holds ONE failure slot
 * shared by every account command, so a failure recorded by a command this screen never
 * issued must not be rendered here. Every command clears the slot before dispatching,
 * so on arrival the slot is already clean; this list closes the remaining window in
 * which another screen's write could still be recorded.
 *
 * Reading the account is included deliberately: a request for an account that does not
 * exist answers 404, and that refusal is exactly what this screen must show instead of
 * an empty form.
 */
const OWNED_OPERATIONS: readonly UserOperation[] = Object.freeze([
  'loadUser',
  'changePassword',
  'resetPassword',
] as const);

/**
 * The form control a resolved pre-flight failure belongs beside.
 *
 * A union rather than a plain string, so a failure can only ever be attributed to a
 * control that exists.
 */
export type CredentialField = 'currentPassword' | 'newPassword' | 'confirmPassword';

/**
 * One resolved pre-flight failure: which control it belongs to and what to say.
 *
 * MIGRATION: the legacy screen surfaced exactly ONE message per postback, because each
 * arm of `cmdUpdate_Click` exited immediately. That single-message behaviour is
 * preserved by resolving to one of these rather than to a list, and the attribution to
 * a control is what lets the message sit beside the offending field instead of in a
 * page-level block — the legacy application had no page-level block to sit in, as the
 * note on the error banner below records.
 */
export interface CredentialFailure {
  /** The control the message belongs beside. */
  readonly field: CredentialField;

  /** The plain-text message. Never blank. */
  readonly message: string;
}

// ---------------------------------------------------------------------------
// THE TYPED FORM
// ---------------------------------------------------------------------------

/**
 * The credential form's shape.
 *
 * An explicit model interface rather than an inferred one, and every control is
 * constructed non-nullable, which buys two guarantees that matter here: the raw value
 * is fully typed rather than a partial, so a member cannot be read as possibly
 * undefined at the point the request is built; and resetting returns each control to
 * the empty string it started as rather than to null, so a cleared credential field is
 * genuinely empty rather than nullish.
 */
export interface ChangePasswordFormModel {
  /**
   * The credential in force.
   *
   * Rendered unless an administrator is acting on another account, and REQUIRED only
   * when the caller is not an administrator. Those are two different predicates on
   * purpose — see {@link UserPasswordComponent.requiresCurrentPassword}.
   */
  readonly currentPassword: FormControl<string>;

  /** The replacement credential. Subject to the length rule and to nothing else. */
  readonly newPassword: FormControl<string>;

  /** The replacement repeated, so a typing error is caught before the request. */
  readonly confirmPassword: FormControl<string>;
}

/**
 * The cross-field rules that cannot be expressed on a single control.
 *
 * MIGRATION: A MECHANISM CHANGE WITH UNCHANGED BEHAVIOUR. The legacy screen expressed
 * both of these imperatively inside its submit handler — the confirmation comparison at
 * `Password.ascx.vb` L272 and the must-differ comparison at L290 — and the legacy
 * markup declared NO validator at all for either. They are declarative here, which
 * means the same two comparisons are evaluated on every value change instead of once
 * per postback, and the outcome for any given pair of values is identical. The
 * comparisons themselves are unchanged: both are exact string equality, neither trims,
 * and neither is case-insensitive, because a credential comparison that normalised its
 * operands would accept a credential the server rejects.
 *
 * The must-differ rule is gated on the same predicate the legacy code gated it on, and
 * that predicate is supplied as an accessor rather than captured, so the gate is read
 * at validation time rather than at construction time. A form-level validator is not
 * re-run when a predicate outside the form changes, which is why the component
 * re-validates explicitly when the gate flips.
 *
 * @param requiresCurrentPassword Whether the current-credential rules apply, read fresh
 * on every evaluation.
 * @returns A validator reporting at most the two group-level codes.
 */
function credentialGroupValidator(requiresCurrentPassword: () => boolean): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    // Read through the abstract API rather than through the typed group, because a
    // validator is handed an `AbstractControl` and narrowing it with a cast would assert
    // a shape the framework does not guarantee at this call site.
    const currentPassword = readControlValue(group, 'currentPassword');
    const newPassword = readControlValue(group, 'newPassword');
    const confirmPassword = readControlValue(group, 'confirmPassword');

    // L272, and it is FIRST for the same reason it was first there: the arm that
    // reported it exited before the policy check could run.
    if (newPassword !== confirmPassword) {
      return { passwordMismatch: true };
    }

    // L290. Gated on the same predicate as the required rule — see
    // {@link UserPasswordComponent.requiresCurrentPassword} for why that predicate is the
    // OPERATION rather than the caller's role. The empty comparison is excluded because two
    // empty values are not a failure to DIFFER — they are a failure to be SUPPLIED, which the
    // required rule and the length rule already report, and reporting both would contradict
    // the single-message behaviour.
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
 * Reads one control's value from a group as a string.
 *
 * Written because a validator receives an `AbstractControl` rather than the typed
 * group, and because the value of a control the group does not contain must be the
 * empty string rather than undefined: the empty string is what the legacy code compared
 * against, and it is also the legacy absent-marker for text, so the two agree.
 *
 * MIGRATION: an OPTION STRICT COERCION MADE EXPLICIT. The 39 legacy administration
 * code-behinds compiled with `strict="false"` (`Website/release.config` L125), which
 * permitted implicit narrowing and late binding that this language rejects. Here the
 * coercion is the one at `Password.ascx.vb` L284: reading `.Text` off a text box yields
 * the empty string both when nothing was typed and when the control was never rendered,
 * so 'not supplied' and 'supplied empty' were indistinguishable. That conflation is
 * preserved — the control is cleared when it is not rendered, so both cases reach the
 * comparison as the empty string, exactly as they did.
 *
 * @param group The control group under validation.
 * @param name The control to read.
 * @returns The value, or the empty string when the control is absent or holds a
 * non-string.
 */
function readControlValue(group: AbstractControl, name: CredentialField): string {
  const control: AbstractControl | null = group.get(name);

  if (control === null) {
    return '';
  }

  // `typeof` rather than a nullish comparison, and deliberately so: a control's value
  // is typed only by the generic parameter, which is erased at run time, and a
  // validator can be attached to a group whose controls were built elsewhere. A
  // non-string value reaching the comparisons below would compare unequal to every
  // string and silently report a mismatch.
  const value: unknown = control.value;

  return typeof value === 'string' ? value : '';
}

/**
 * Converts the route's `userId` parameter into an account key.
 *
 * ROUTE PARAMETERS ARRIVE AS STRINGS. Component input binding assigns the raw segment,
 * so the conversion happens here rather than being assumed away, and it is explicit:
 * base ten is stated, and the result is admitted only when it is a finite number.
 *
 * ⚠ NO TRUTHINESS TEST, NO POSITIVITY TEST, NO SENTINEL FALLBACK. Zero is returned as
 * zero and minus one is returned as minus one, because in this schema both are real
 * keys somewhere — role, page and module keys seed at zero and tenant keys seed at
 * minus one — while minus one is simultaneously the legacy marker for a missing
 * integer. A `> 0` test is therefore a defect class rather than a style preference.
 * Account keys themselves seed at `IDENTITY(1, 1)`, so zero does not occur naturally
 * for an account; it is nevertheless carried through as a real key, DEFENSIVELY, so
 * that this screen does not reason about identifiers differently from the next one.
 *
 * An unusable value becomes `Number.NaN`, which the component resolves to an explicit
 * absence and renders as an unresolved-route state. `Number.NaN` is used rather than
 * null because the input's read type is a number, and widening it would push the
 * absence check into every consumer of the signal instead of into one place.
 *
 * @param value The bound value: the route segment as a string, or a number when a
 * template binds one directly.
 * @returns The account key, or `Number.NaN` when the value names none.
 */
function parseRouteUserId(value: unknown): number {
  // DELEGATES TO core/utils/route-id.util.ts RATHER THAN RESTATING THE GRAMMAR. The five screens
  // that parse a route identifier each carried their own version and they disagreed with one
  // another; the grammar now lives in one place, and it bounds the result to the range the schema
  // columns permit as well as refusing every spelling that is not a plain signed decimal integer.
  //
  // The rejection that used to be written out here is preserved by that parser and then some: it
  // refuses a numeric prefix with a tail (the reason this test existed), and also the surrounding
  // whitespace and the leading plus this body used to tolerate — two spellings of one key are two
  // ways to name one record, which is what a single grammar exists to prevent.
  //
  // A NUMBER is still accepted so a programmatic binding need not stringify one, but it is
  // VALIDATED against the same bounds rather than merely tested for finiteness.
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
 * The Manage Password screen.
 *
 * ⚠ THE EXPORTED NAME IS PART OF THE ROUTING CONTRACT. The account feature's route
 * table reaches this class by name through a dynamic import, and a route that resolves
 * to no export renders nothing at all — a blank screen with no compilation error and no
 * console message. The class name, the file name and the selector are therefore fixed.
 *
 * ⚠ THE INPUT NAME IS ALSO PART OF THE ROUTING CONTRACT. The router is configured with
 * component input binding, which assigns each matched route parameter to an input of the
 * SAME NAME, so `userId` is spelled exactly as the route segment `:userId` spells it.
 * A rename breaks the binding silently, again with no compilation error.
 *
 * ⚠ AND SO IS THE ABSENCE OF ONE. Component input binding assigns route DATA as well as
 * route parameters, and the route that reaches this screen carries a `permission` entry
 * in its data. No input named `permission` is declared here, because declaring one would
 * silently adopt a routing detail as component state.
 */
@Component({
  selector: 'app-user-password',
  standalone: true,
  // ⚠ EVERY SELECTOR AND PIPE THE PAIRED TEMPLATE USES MUST APPEAR HERE. Strict template
  // checking is enabled, so an element matching an unlisted component is not a silent
  // fall-through to an unknown element — it is a compilation error. The reactive-forms
  // module is listed because the template binds a form group; the five shared components
  // and the one pipe are the whole of the shared surface this screen renders.
  imports: [
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
  // MANDATORY. Every value this screen renders is either a signal or a member of the
  // form's own model, both of which mark the view for checking on change, so the default
  // strategy would only add whole-tree traversals that can change nothing.
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserPasswordComponent {
  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------
  //
  // Injected through the function form rather than through constructor parameters, which
  // is what lets the field initialisers below read them. There are NO component-level
  // providers: all three are provided at the application root, and a provider here would
  // hand this screen a private copy of state the rest of the application cannot see.

  /**
   * The account store: this screen's only route to the API.
   *
   * MIGRATION: the store is consumed rather than bypassed, and the decision was made by
   * reading it rather than by assuming. It already publishes everything this screen needs
   * — the selected account, a read-in-flight flag, a write-in-flight flag, a single
   * failure slot with the failure's own command recorded on it, and the two credential
   * commands — so calling the transport directly would duplicate the sequencing and would
   * leave the account this screen just modified stale in the store that the listing and
   * the editor read from. The store re-reads the account after a successful write for
   * exactly that reason.
   *
   * ⚠ NO HTTP CLIENT, NO URL, NO HEADER, NO ENVIRONMENT MODULE reaches this file. The
   * route templates are declared once in the endpoint module and the configured base is
   * relative in production, which is what keeps the containerised application behind its
   * reverse proxy; an absolute host assembled here would bypass the proxy and fail the
   * end-to-end gate while every unit test still passed.
   */
  private readonly store = inject(UserStore);

  /**
   * The authentication store, read for the CALLER's identity.
   *
   * Required because the two gates this screen turns on — which fields to render and
   * which rules to enforce — are predicates about the caller rather than about the
   * account being administered. `UserModuleBase.IsAdmin` (L287-L291) asks whether the
   * caller holds the administrator role or is a host account, and `IsUser` (L399-L406)
   * asks whether the caller IS the account on the screen. Neither question can be
   * answered from the account contract, which describes the subject and not the actor.
   *
   * ⚠ READ FOR RENDERING ONLY. Nothing here reaches an access verdict: the server
   * re-authorises every request against stored state and answers 403, and the route guard
   * that admits a caller to this screen is advisory for the same reason.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The notification queue, used for the SUCCESS announcement and for nothing else.
   *
   * The HTTP failure interceptor already announces every failure it sees, so a failure
   * announcement here would show the same sentence twice and, for a permission refusal,
   * would show it at the wrong severity.
   */
  private readonly notifications = inject(NotificationService);

  /**
   * The router, used for exactly one navigation: leaving this screen once a MANDATORY
   * credential change has been made.
   *
   * Nothing else on this screen navigates. An ordinary caller changing their own password
   * stays where they are, which is what the legacy screen did.
   */
  private readonly router = inject(Router);

  // -------------------------------------------------------------------------
  // THE ROUTE INPUT
  // -------------------------------------------------------------------------

  /**
   * The account to administer, bound from the route segment `:userId`.
   *
   * A SIGNAL input rather than a decorated field, and the choice is load-bearing rather
   * than stylistic: {@link isSelf}, {@link showCurrentPassword} and the account
   * projection are all derived values over this one, and a derivation over a plain
   * decorated field cannot know when to recompute — it would resolve once against the
   * first value bound and then ignore every later one, which under this change-detection
   * strategy means navigating from one account to another would leave the previous
   * account's gates in force. A signal input is reactive by construction, so the
   * derivations are correct without a setter writing a private backing signal, and it
   * satisfies the workspace's strict input access rule for free because it cannot be
   * anything but public.
   *
   * REQUIRED, because the route cannot match without the segment. The transform converts
   * the string the router binds into a number and reports an unusable value as
   * `Number.NaN`, which {@link accountKey} turns into an explicit absence.
   */
  readonly userId = input.required<number, unknown>({ transform: parseRouteUserId });

  // -------------------------------------------------------------------------
  // LOCAL STATE
  // -------------------------------------------------------------------------
  //
  // Writable signals, private, each exposed through a read-only projection or a
  // derivation. MIGRATION: view state and session state are eliminated entirely. The
  // legacy screen round-tripped its control state to the server on every interaction;
  // none of that survives, and the four values below live only in the browser.
  //
  // ⚠ NONE OF THESE HOLDS A CREDENTIAL. The three credential values live in the form's
  // own controls and are cleared as soon as a write succeeds.

  /** Whether the administrative-reset confirmation is mounted. */
  private readonly _resetConfirmOpen = signal(false);

  /**
   * Which operation is awaiting its answer, or null when none is.
   *
   * The store's commands return nothing — they record their own outcome in shared slices
   * — so this is how the screen knows that the write which has just settled was its own,
   * and which of the two it was. It is cleared as soon as the outcome is resolved.
   */
  private readonly _pendingOperation = signal<ChangePasswordOperation | null>(null);

  /**
   * Whether a submission has been attempted.
   *
   * MIGRATION: the legacy screen showed no validation message until a postback, because
   * it had no client-side validation at all to show one from. That restraint is
   * preserved: a message appears once the caller has either submitted or visited the
   * control, and never on first render.
   */
  private readonly _submitAttempted = signal(false);

  /**
   * A monotonic counter bumped whenever the validity of the form may have changed for a
   * reason the template cannot observe.
   *
   * The form model is not a signal, so a derivation over it would not recompute; the
   * template re-evaluates the resolution methods below on every check, which covers every
   * change a person makes. This counter covers the remaining case — the gate flipping
   * when the caller's identity resolves — by giving that transition something a template
   * expression can depend on. It is deliberately not a value: only its change matters.
   */
  private readonly _validityRevision = signal(0);

  // -------------------------------------------------------------------------
  // THE CALLER'S IDENTITY, AND THE TWO GATES IT TURNS
  // -------------------------------------------------------------------------
  //
  // ⚠ DECLARED BEFORE THE FORM ON PURPOSE. Class field initialisers run in declaration
  // order, and the form's group validator reads {@link requiresCurrentPassword} the
  // moment the group is constructed, because a control group validates itself on
  // construction. Declaring the form first would evaluate that accessor against an
  // uninitialised member.

  /**
   * The account key the route named, or null when it named none.
   *
   * ⚠ Tested for absence as null, never for truth. Zero passes through as a real key and
   * so does minus one, for the reasons recorded on {@link parseRouteUserId}.
   */
  readonly accountKey: Signal<number | null> = computed(() => {
    const bound = this.userId();

    return Number.isFinite(bound) ? bound : null;
  });

  /**
   * Whether the CALLER administers this tenant or is a host account.
   *
   * ⚠ READ FROM THE ONE AUTHORITY, NEVER FROM A ROLE NAME.
   * `AuthStore.administersCurrentPortal` answers this for the whole application from the
   * fact the SERVER derived — the tenant's own `Portals.AdministratorRoleId` designation
   * evaluated against the caller's live role assignments — plus the host-account arm the
   * enforcing policy also takes.
   *
   * MIGRATION: the legacy predicate `UserModuleBase.IsAdmin` (L287-L291) was
   * `UserInfo.IsInRole(PortalSettings.AdministratorRoleName) Or UserInfo.IsSuperUser`, and
   * this screen previously reproduced it by comparing the caller's role list against the
   * literal name 'Administrators'. That is NOT what the legacy did, and the difference is
   * the defect: the legacy read the tenant's own `AdministratorRoleName`, so it followed
   * the designation, whereas a hardcoded name follows nothing. An administrator of a tenant
   * whose administrator role carries any other name — the name is an ordinary updatable
   * column — was shown the self-service wording and asked for a credential they do not
   * hold. The role name no longer appears in this file.
   *
   * ⚠ THIS IS NOT REDUNDANT WITH THE ROUTE GUARD, AND AN EARLIER NOTE HERE CLAIMED IT WAS.
   * The route that reaches this screen declares `AccountOwner` — matching
   * `UsersController.cs:L576`, the change endpoint's own policy, which has no
   * administrator arm at all — so the caller who gets here is the ACCOUNT HOLDER and this
   * predicate is normally FALSE, not "true in practice". The earlier note described a
   * route declaring tenant administration, which was itself the defect: it refused every
   * account holder the one screen this predicate's self-service arm exists to serve.
   *
   * It remains derived rather than assumed either way, because the guard is advisory — the
   * server is the authority and answers 403 — and because both arms of the gates below have
   * to be reachable for either to be testable.
   *
   * Defaults to FALSE while the caller's identity is unresolved, which is the safe
   * posture: an unresolved caller is asked for the credential in force rather than
   * excused from it.
   */
  readonly isAdmin: Signal<boolean> = this.auth.administersCurrentPortal;

  /**
   * Whether the CALLER is the account on the screen.
   *
   * Reproduces `UserModuleBase.IsUser` (L399-L406), including its behaviour for an
   * unauthenticated caller: the legacy property returned false without comparing, and an
   * absent current identity resolves to false here for the same reason.
   *
   * ⚠ Compared with strict equality against an explicitly resolved key. A truthiness
   * test would report false for the account key zero, and a `?? -1` fallback would make
   * an absent route match a real tenant key.
   */
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
   * The operation a submission would perform.
   *
   * MIGRATION: THE ONE LEGACY BUTTON BECOMES ONE OF TWO ENDPOINTS, CHOSEN BY WHO THE
   * CALLER IS. The legacy screen called a single routine for every caller and passed the
   * empty string as the credential in force when an administrator was acting on another
   * account. The API separates the two, and it separates them by AUTHORISATION rather than
   * by convenience: the change endpoint is restricted to the account holder and requires
   * the credential in force, while the reset endpoint is restricted to a tenant
   * administrator and refuses a request that carries one. A single endpoint could not have
   * been authorised correctly for both.
   *
   * The predicate is `isSelf`, and it is `isSelf` rather than the caller's role on purpose. An
   * administrator changing their OWN credential took the legacy change path — the reset panel
   * was hidden for them, as the note on {@link resetHelpText} records — so they take the
   * change path here too, which is also the only path the change endpoint's own authorisation
   * admits them to. Because this predicate now also drives
   * {@link UserPasswordComponent.requiresCurrentPassword}, that caller IS asked for the
   * credential in force: the change contract requires it of every caller, so asking beside the
   * box is the only way the request can succeed at all.
   *
   * A caller who is neither the account holder nor an administrator resolves to the reset
   * operation and is refused by the server with a permission status. That path is
   * unreachable through the guarded route and is not relied on; it exists because a
   * predicate must answer for every input.
   */
  readonly plannedOperation: Signal<ChangePasswordOperation> = computed(() =>
    this.isSelf() ? OPERATION_CHANGE : OPERATION_RESET,
  );

  /**
   * Whether the server would authorise the operation this caller resolves to.
   *
   * ⚠ THIS EXISTS BECAUSE THE ROUTE NOW ADMITS TWO DIFFERENT CALLERS FOR TWO DIFFERENT
   * OPERATIONS, and admitting a union means one of them can arrive at the operation that is not
   * theirs. `/users/{userId}/password` declares `AccountOwnerOrPortalAdministrator`, which it must
   * in order for a tenant administrator to reach the reset at all — the previous ownership-only
   * declaration locked them out of the screen entirely. The server did not change and does not
   * need to: `POST {userId}/password` admits the account holder alone and
   * `POST {userId}/password-reset` a tenant administrator alone.
   *
   * A CHANGE is always permitted here, because the operation is chosen BY ownership: it is
   * resolved only when the caller is the account on screen, which is exactly what that endpoint
   * requires. A RESET requires tenant administration, and a caller who is neither the holder nor
   * an administrator resolves to it — so that is the one combination to withhold.
   *
   * Fail-closed rather than optimistic: while the caller's identity is unresolved
   * `administersCurrentPortal` reads false, so a reset is withheld until the identity says
   * otherwise. Offering an action that comes back refused is worse than offering it a moment
   * late, and this is a credential replacement.
   */
  readonly operationPermitted: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_CHANGE || this.isAdmin(),
  );

  // ⚠ DECLARED ABOVE THE FORM, AND THE POSITION IS LOAD-BEARING. Class fields initialise in
  // declaration order, and the form's group validator below closes over
  // {@link UserPasswordComponent.requiresCurrentPassword}, which reads this member — the
  // `FormGroup` constructor runs its validators immediately, so a declaration after the form
  // left this member undefined at that moment and construction threw. Moving it here is what
  // makes the screen constructible; a later reader who returns it to the section it used to sit
  // in will break every case in this screen's specification.

  /**
   * Whether the current-credential rules APPLY.
   *
   * ⚠ GATED ON THE OPERATION, NOT ON THE CALLER'S ROLE, AND THE CHANGE IS DELIBERATE. The
   * legacy screen gated DISPLAY on `IsAdmin And Not IsUser` (`Password.ascx.vb:L150`) but
   * gated both ENFORCEMENT rules on `Not IsAdmin` alone (`:L284` and `:L290`), so an
   * administrator changing their OWN credential saw the control and was excused the rules.
   *
   * That excusal cannot be reproduced, because the endpoint it would reach refuses it. The
   * legacy screen called ONE routine for every caller; the API separates the two operations
   * by authorisation, and its change rule requires the credential in force whenever the
   * operation is a change — with no exception for the caller's role. An administrator
   * changing their own credential therefore performs a CHANGE, and a change with a blank
   * credential in force is refused by the server every single time.
   *
   * So the legacy behaviour here is unreachable rather than merely inadvisable: excusing the
   * rule does not let that caller through, it only moves the refusal from beside the box to
   * a round trip away, with the field message arriving from the server instead. Gating on
   * the operation reproduces the OUTCOME the system as a whole produces, which is what
   * behavioural equivalence means when one layer's rule has become unreachable.
   *
   * The gate remains a subset of display, which is what keeps the validator wiring sound: a
   * caller who is not the account holder resolves to the RESET operation, so the rule is off
   * for exactly the caller whose control the template does not render — and the reset
   * contract additionally requires the credential in force to be ABSENT, which the dispatch
   * guarantees by sending null.
   */
  readonly requiresCurrentPassword: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_CHANGE,
  );

  /**
   * Whether the current-credential control is RENDERED.
   *
   * The negation of `IsAdmin And Not IsUser` from `Password.ascx.vb` L150-L152, where the
   * administrator arm hid the row and switched the help text.
   *
   * MIGRATION: `Password.ascx.vb` L144 is NOT reproduced. It hid the whole change panel
   * from an administrator whenever credential retrieval was unavailable, on the reasoning
   * that an administrator who could not read the existing credential had to reset rather
   * than change. Retrieval is not carried forward at all, so the condition has no
   * meaning in the target: reproducing it literally would hide the panel from every
   * administrator permanently, which is the opposite of the intent. The administrative
   * path is served by the reset operation instead, which the API authorises for exactly
   * this caller.
   */
  readonly showCurrentPassword: Signal<boolean> = computed(
    () => !(this.isAdmin() && !this.isSelf()),
  );

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The typing ceiling emitted on all three credential inputs.
   *
   * Shared from `core/utils/credential-bounds.util.ts`, which holds the API's own bound
   * and the reasoning behind it, so this screen cannot drift away from the server rule.
   *
   * MIGRATION: THE LEGACY CEILING OF 20 IS DELIBERATELY NOT PRESERVED, and this screen is
   * where reproducing it did the most damage. `admin/Users/Password.ascx` L35, L39 and L43
   * each declare `maxlength="20"`, mirroring the legacy `Password nvarchar(20)` storage
   * width. Applied to the CURRENT credential, that ceiling means an account whose password
   * is longer than twenty characters — which the API's 256-byte bound permits — can never
   * type its existing credential in full, and so can never change its own password. That
   * is a lockout rather than a typing affordance.
   */
  readonly credentialMaxLength = CREDENTIAL_MAX_LENGTH;

  /**
   * Whether the current-credential rules apply, as a PLAIN FIELD the group validator can read.
   *
   * ⚠ A MIRROR OF {@link UserPasswordComponent.requiresCurrentPassword}, AND IT EXISTS FOR ONE
   * CONCRETE REASON: the `FormGroup` constructor runs its validators immediately, while the class
   * is still initialising its fields. The gate resolves through the addressed account key, which
   * comes from a REQUIRED input, and a required input read before Angular has bound it throws
   * rather than answering — so a group validator that consulted the signal directly made this
   * screen impossible to construct. A plain field answers at construction and is written by the
   * one effect that already owns keeping the gate in step, so there is exactly one writer.
   *
   * FALSE is the correct value for the construction moment: the rule it feeds is the
   * must-differ comparison, nothing has been submitted yet, and the effect below has set the
   * truth before the form can be interacted with. The REQUIRED rule is unaffected — it is
   * attached and detached by that same effect.
   */
  private currentCredentialRuleApplies = false;

  /**
   * Reports this screen's unsaved entry to the tracker that guards both ways of leaving it.
   *
   * ⚠ THE ROUTE DECLARES `unsavedChangesGuard` AND THIS SCREEN USED TO REGISTER NOTHING, so the gate
   * was answered by a reflective sweep over this component's fields. The sweep is gone — it pulled
   * `@angular/forms` into the eagerly loaded bundle for an application whose every form is lazily
   * loaded — and this registration replaces it. Without it the declaration on `users/:userId/password`
   * would be inert.
   *
   * ⚠ THIS SCREEN'S ENTRY IS THE ONE KIND NO BROWSER WILL OFFER BACK. Every control here is a
   * credential field, so it is excluded from form restoration and from any password manager's
   * autofill of a previous value: entry lost on this screen is lost outright, with not even a partial
   * recovery available, and the operator has to re-type all three boxes. That makes the warning worth
   * more here than on a screen whose fields a reload would repopulate.
   *
   * ⚠ NO CREDENTIAL IS READ, ONLY WHETHER ONE WAS TYPED. `dirty` is a flag the framework raises on
   * interaction; the probe touches no control value, so nothing secret is reachable through the
   * tracker, and the browser's own unload prompt carries no author-supplied text at all.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.saving() === false,
  );

  /**
   * The credential form.
   *
   * Every control is non-nullable, so the raw value is fully typed and resetting returns
   * each control to the empty string rather than to null.
   *
   * MIGRATION: THE LEGACY POSITIONAL CONTRACTS ARE NOT REPRODUCED. The legacy call was
   * `ChangePassword(user, oldPassword, newPassword)` and its outcome came back through a
   * status the caller had to interpret, one of thirty in-scope sites that reported an
   * outcome by mutating an argument. The request is a named object here and the outcome is
   * an HTTP status with a machine-readable code, so no argument is mutated and no ordinal
   * crosses the boundary.
   *
   * MIGRATION: the new-versus-confirm rule is a GROUP validator, which is a mechanism
   * change with unchanged behaviour; see {@link credentialGroupValidator}.
   *
   * The required rule on the current credential is attached and detached as the gate
   * moves rather than declared here, because a required rule left on a control the
   * template does not render would make the form permanently invalid for an
   * administrator — a screen that cannot be submitted and says nothing about why.
   */
  readonly form = new FormGroup<ChangePasswordFormModel>(
    {
      currentPassword: new FormControl('', { nonNullable: true }),
      newPassword: new FormControl('', {
        nonNullable: true,
        // Both rules resolve to the SAME message, and that is the measured behaviour
        // rather than a convenience: the legacy policy check was a single call whose
        // length comparison failed identically for an empty credential and for a short
        // one, so there was one outcome and one message. `Validators.minLength` ignores an
        // empty value by design, which is why the required rule is present at all.
        validators: [Validators.required, Validators.minLength(MINIMUM_PASSWORD_LENGTH)],
      }),
      // No validator of its own. Its only rule is the comparison with the replacement,
      // which needs both values and therefore belongs to the group.
      confirmPassword: new FormControl('', { nonNullable: true }),
    },
    { validators: [credentialGroupValidator(() => this.currentCredentialRuleApplies)] },
  );

  // -------------------------------------------------------------------------
  // READ-ONLY PROJECTIONS OF STORE STATE
  // -------------------------------------------------------------------------

  /**
   * The account being administered, or null when none has been read for THIS route.
   *
   * The store's selection is filtered against the route's own key rather than trusted
   * blindly. The store clears its held account when the selection changes, so the window
   * is small, but it is not empty: navigating from one account to another would otherwise
   * render one account's name above another account's form for as long as the second read
   * is outstanding, and on this screen that would mean showing an operator the wrong
   * account name beside a credential field.
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
   * Whether a write is in flight.
   *
   * The store's flag is shared by every account write, which is the correct thing to
   * disable a submit affordance on: a second write dispatched while the first is
   * outstanding would race it, and the store cancels rather than queues.
   *
   * MIGRATION: an in-flight indication is NET-NEW, and it is honestly net-new rather than
   * a translation. The legacy screen was a pure full-page postback — there is no partial
   * update panel, no script manager and no asynchronous postback anywhere in the in-scope
   * markup — so the browser's own page-load indication was the only feedback a person
   * received, and an in-page indicator had no ancestor to be ported from.
   */
  readonly saving: Signal<boolean> = computed(() => this.store.saving());

  /**
   * The failure to render, or null when there is none this screen owns.
   *
   * Handed straight to the shared error banner, which resolves the wording and the
   * severity itself. In particular a permission refusal resolves to a WARNING there, not
   * to a danger treatment, and this component neither overrides that nor raises a second
   * notification for the same refusal — the measured legacy behaviour is
   * `Website/admin/Security/AccessDenied.ascx.vb` L41-L47, whose two branches both render
   * a denial at the warning type.
   *
   * MIGRATION: the error banner itself is NET-NEW with NO ancestor reachable from in-scope
   * code. There is not one validation-summary control in the 39 in-scope administration
   * screens, nor anywhere in the legacy web tree, so this is an added affordance rather
   * than a ported one — the same family of finding as the migration's vacuous exclusions,
   * and reported as such rather than attributed to a predecessor that does not exist.
   */
  readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null) {
      return null;
    }

    return OWNED_OPERATIONS.includes(failure.operation) ? failure.problem : null;
  });

  /**
   * The sentence to show when a failure this screen owns carried NO problem document.
   *
   * ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT, AND ON THIS SCREEN IT LEFT THE FORM
   * UNUSABLE. The runtime decoders that check each response against its published contract run inside
   * the service's own mapping, which is DOWNSTREAM of the interceptor's error handling — so a `200`
   * whose body does not match its contract throws a plain error with no document, no status and no
   * support reference. {@link problem} is `null` for it, so the banner rendered nothing while the
   * account read had not committed, and the operator was left looking at a credential form that would
   * not submit and gave no reason.
   *
   * The store's own authored summary is read out rather than a second sentence being invented here.
   * Null whenever a document IS present, so the server's own explanation always wins.
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

  /**
   * Whether the route named an account this screen cannot resolve.
   *
   * A distinct state from "not found": the route segment itself was not a key, so no
   * request was ever issued and there is nothing for the API to have refused.
   */
  readonly routeUnresolved: Signal<boolean> = computed(() => this.accountKey() === null);

  /**
   * Whether the account is unavailable — read, and not there.
   *
   * True only once the read has settled, so the empty state does not flash before the
   * first response. A failure carries its own rendering through the banner; this covers
   * the answer that succeeded and carried no account.
   */
  readonly accountUnavailable: Signal<boolean> = computed(
    () => !this.routeUnresolved() && !this.loading() && this.user() === null,
  );

  /**
   * Whether the account's details are absent BECAUSE THEY WERE NEVER ASKED FOR.
   *
   * A third state, distinct from both of the two above: the route named an account and the
   * read did not fail, because no read was issued. While the caller owes mandatory
   * remediation the API refuses `GET api/v1/users/{id}` — measured, `403
   * auth.remediation_required` — so asking would produce nothing but a refusal banner on
   * the one screen the server is requiring the caller to use. This screen therefore does
   * not ask, and says so here rather than letting an absence of details look like a
   * failure.
   *
   * The two summary rows are the only thing that absence costs: the last-changed instant
   * and the expiry wording are read from the account, and nothing else on the screen is.
   */
  readonly accountDetailWithheld: Signal<boolean> = computed(() => this.auth.sessionRestricted());

  /**
   * Whether the credential form can be presented.
   *
   * True once the route names an account AND the screen is not waiting on a read it
   * actually issued. The write needs the route's key and the caller's own typing; it does
   * not need the account's details, so their absence-by-design does not withhold the form.
   *
   * ⚠ THIS IS WHAT THE TEMPLATE GATES ON, IN PLACE OF THE ACCOUNT ITSELF. Gating the
   * populated view on `user() !== null` meant a remediating caller saw a heading, a refusal
   * banner and nothing to act on — the screen rendered its own unusability. The form is
   * what the screen is FOR, and it is available whenever a well-formed address names an
   * account to write to.
   */
  readonly credentialFormAvailable: Signal<boolean> = computed(
    () => this.accountKey() !== null && (this.user() !== null || this.accountDetailWithheld()),
  );

  // -------------------------------------------------------------------------
  // DERIVED WORDING
  // -------------------------------------------------------------------------

  /**
   * The page heading.
   *
   * Reproduces `PasswordTitle.Text` — 'Manage Password - {0} (Id: {1})' — and reproduces
   * its restriction: the account name and key are disclosed only to an administrator,
   * because `Password.ascx.vb` L123-L127 formatted the title inside an `IsAdmin` arm and
   * hid the row entirely otherwise.
   *
   * MIGRATION: a heading is always rendered where the legacy screen sometimes rendered
   * none. The shared page header emits the page's single top-level heading unconditionally
   * and refuses a blank title, and a page with no heading is a page an assistive
   * technology cannot summarise. The neutral form is the same measured wording with the
   * account clause omitted rather than newly invented text.
   */
  readonly pageTitle: Signal<string> = computed(() => {
    const account = this.user();

    if (!this.isAdmin() || account === null) {
      return MANAGE_PASSWORD_TITLE;
    }

    return `${MANAGE_PASSWORD_TITLE} - ${account.username} (Id: ${account.userId})`;
  });

  /**
   * The help text above the credential fields.
   *
   * Exactly the two arms of `Password.ascx.vb` L150-L155: the administrator acting on
   * another account is told to enter and confirm a replacement, and everyone else is told
   * that the credential in force is required as well.
   */
  readonly changeHelpText: Signal<string> = computed(() =>
    this.showCurrentPassword() ? USER_CHANGE_HELP : ADMIN_CHANGE_HELP,
  );

  /**
   * The help text for the administrative reset, or the empty string when the caller is not
   * performing one.
   *
   * Reproduces the visibility rule of `Password.ascx.vb` L160-L184 as that rule resolves
   * under the shipped configuration. Read literally, the legacy code showed the reset
   * panel when credential reset was enabled — it was — and then took the administrator arm
   * only for `IsAdmin And Not IsUser`, while the other arm required a question-and-answer
   * pair that was configured OFF and therefore hid the panel. So in the configuration
   * that shipped, the reset panel appeared for an administrator acting on another account
   * and for nobody else, which is exactly this predicate.
   *
   * MIGRATION: `InvalidPasswordAnswer` is UNREACHABLE on the reset path. Two independent
   * reasons, either of which alone would be sufficient: the provider was registered with
   * `requiresQuestionAndAnswer="false"`, so `Password.ascx.vb` L240 could not be entered;
   * and the question-and-answer requirement is not carried forward at all, because it
   * existed to support recovering a credential from a reversible store. Neither the empty
   * answer arm at L241-L243 nor the wrong-answer arm at L251-L252 has a counterpart here,
   * and the shared failure vocabulary correspondingly words no code for either.
   */
  readonly resetHelpText: Signal<string> = computed(() =>
    this.isAdmin() && !this.isSelf() ? ADMIN_RESET_HELP : '',
  );

  /**
   * The label for the submit affordance.
   *
   * `ChangePassword.Text` or `ResetPassword.Text`, chosen by the operation the submit will
   * actually perform, so the affordance names its own consequence.
   */
  readonly submitLabel: Signal<string> = computed(() =>
    this.plannedOperation() === OPERATION_RESET ? RESET_PASSWORD_TEXT : CHANGE_PASSWORD_TEXT,
  );

  /**
   * The caption of the one credential section.
   *
   * ⚠ THE SAME SIGNAL AS THE SUBMIT LABEL, ALIASED RATHER THAN RECOMPUTED, so the caption and the
   * command it contains cannot come to name different operations. That is not a convenience: the
   * defect this replaces was precisely a caption naming one operation above the boxes belonging to
   * another, with the command naming a third possibility at the foot of the form.
   *
   * MIGRATION: sharing one source reproduces what the legacy markup did per panel. `Password.ascx`
   * L30 captioned the change panel with `resourcekey="ChangePassword"` and its button at L46 carried
   * the SAME key; the reset section head at L53 and its button at L69 likewise shared
   * `ResourceKey="ResetPassword"`. One key per panel, caption and command together.
   */
  readonly sectionHeading: Signal<string> = this.submitLabel;

  /**
   * Every help sentence that applies to the operation being performed, in reading order.
   *
   * A LIST RATHER THAN A STRING, because a reset legitimately has two things to say and both are
   * measured resource wording: what the operation does, and how to supply the replacement. Merging
   * them into one paragraph or dropping one to fit a single slot would lose text the legacy screen
   * showed - the legacy stated them in two different panels, which is exactly why there were two.
   *
   * ⚠ ORDER IS WHAT MAKES IT READ CORRECTLY: the operation is described before the instruction for
   * carrying it out.
   *
   * MIGRATION: for a change there is one sentence, and which one is itself measured -
   * `Password.ascx.vb` L150-L155 chose between telling the account holder that the credential in
   * force is needed as well, and telling an administrator merely to enter and confirm a replacement.
   * That choice is preserved by {@link changeHelpText}; this member only decides how many sentences
   * apply, never rewrites one.
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
   * The instant the credential was last changed, for the template to render through the
   * `dateDisplay` pipe. Null when no account has been read.
   *
   * MIGRATION: THE LEGACY NULL-DATE SENTINEL RENDERS AS NOTHING. `Null.vb` defines the
   * absent date as the minimum date, and `Password.ascx.vb` L129 formatted the value with
   * no guard at all, so an account whose credential had never been changed displayed a
   * year-one long date. The shared pipe renders both an absent value and that sentinel as
   * empty text, which is the parity the legacy screen's own date formatter offered
   * elsewhere and which L129 itself failed to apply. The unguarded original is recorded
   * here rather than silently improved upon.
   */
  readonly lastChangedDisplay: Signal<string | null> = computed(() => {
    const account = this.user();

    return account === null ? null : account.lastPasswordChangeDate;
  });

  /**
   * The resolved expiry wording.
   *
   * MIGRATION: ONE OF THE THREE LEGACY BRANCHES IS NOT REPRESENTABLE, AND THE OMISSION IS
   * DELIBERATE RATHER THAN OVERSIGHT. `Password.ascx.vb` L132-L140 chose between three
   * outcomes: a forced change, an expiry date computed as the last change plus a
   * configured number of days, and 'Password does not Expire' when that number was zero.
   * The first maps exactly onto the account contract's own blocking flag. The second
   * cannot be computed, because NO expiry-days value crosses the API boundary — the
   * account contract carries no such member, the tenant's account-policy contract
   * deliberately carries no policy numbers at all, and the server's credential policy
   * publishes a minimum length and a composition count but no expiry. The third is the
   * shipped default for that setting, so it is what remains. Inventing a number to
   * compute a date with would present a fabricated date as fact, which is worse than
   * presenting the default the installation actually ran with.
   *
   * Empty while no account has been read, so nothing is asserted before there is an
   * account to assert it about.
   */
  readonly expiryDisplay: Signal<string> = computed(() => {
    const account = this.user();

    if (account === null) {
      return '';
    }

    // `mustChangePassword` is the successor of the legacy update-credential flag, and
    // false is DATA here rather than an absence: the contract declares it a plain boolean
    // precisely because the legacy absent-marker for a boolean WAS false, so admitting a
    // third state would invent a distinction the source data cannot make.
    return account.mustChangePassword ? FORCED_EXPIRY_TEXT : NO_EXPIRY_TEXT;
  });

  /**
   * The policy wording, shown as the replacement credential's help text.
   *
   * A signal rather than a bare constant so that the template reads every piece of wording
   * the same way, and so that the requirement is stated where a person can act on it
   * rather than only after a rejection.
   */
  readonly policyMessage: Signal<string> = computed(() => PASSWORD_POLICY_MESSAGE);

  // -------------------------------------------------------------------------
  // STATIC WORDING THE TEMPLATE BINDS
  // -------------------------------------------------------------------------
  //
  // Exposed as members rather than left in the template so that the resource-derived
  // wording lives in one place, and so that a specification can assert the exact strings —
  // including the double spaces and the missing trailing periods that the measured
  // originals carry.

  /** `plLastChanged.Text`. */
  readonly lastChangedLabel = LAST_CHANGED_LABEL;

  /** `plLastChanged.Help`. */
  readonly lastChangedHelp = LAST_CHANGED_HELP;

  /** `plExpires.Text`. */
  readonly expiresLabel = EXPIRES_LABEL;

  /** `plExpires.Help`. */
  readonly expiresHelp = EXPIRES_HELP;

  /**
   * `plOldPassword.Text`.
   *
   * MIGRATION: D9 — THE ONE LEGACY DEFECT THIS SCREEN CORRECTS is a label association,
   * and it is corrected because accessibility parity demands a working label. The
   * question-and-answer panel's question label declared its control association as
   * `lblQuetxtEditQuestionstion` (`Password.ascx` L86), a mangling of two control
   * identifiers that named nothing, so that label pointed at no control and a screen
   * reader announced the field unnamed. Every label on this screen is associated through
   * the shared labelled-field component, which takes the control's own identifier and
   * emits a real label element, so no association can be mangled by hand. The mangled
   * declaration itself belonged to the dropped panel; the correction is recorded because
   * the same defect class is what the component prevents everywhere else.
   */
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

  /**
   * `ResetPassword.Text`, for the confirmation dialog's title and its confirming control.
   *
   * NOT the section caption: that is {@link sectionHeading}, which resolves from the operation being
   * performed so the caption and the command always agree. This member names the dialog alone.
   */
  readonly resetSectionHeading = RESET_PASSWORD_TEXT;

  /**
   * Whether a submission needs the administrative-reset confirmation first.
   *
   * MIGRATION: the confirmation is NET-NEW, and it is scoped to the case that warrants it.
   * The legacy screen confirmed nothing, because a full-page postback made every action
   * deliberate by construction. Resetting another person's credential locks them out until
   * they are told the replacement, and it is the one action on this screen that a caller
   * cannot undo for themselves, so it is confirmed. Changing one's own credential is not
   * confirmed, which matches the legacy screen exactly.
   */
  readonly requiresResetConfirmation: Signal<boolean> = computed(
    () => this.plannedOperation() === OPERATION_RESET,
  );

  /**
   * The confirmation question.
   *
   * Names the account so that an operator with several tabs open can see which account the
   * dialog is about. It names the account's SIGN-IN NAME and nothing else: no credential,
   * no address and no key appears in a dialog message.
   */
  readonly resetConfirmMessage: Signal<string> = computed(() => {
    const account = this.user();

    if (account === null) {
      return 'Reset this account\u2019s password to the replacement supplied?';
    }

    return `Reset the password for ${account.username} to the replacement supplied?`;
  });

  // -------------------------------------------------------------------------
  // PRE-FLIGHT FAILURE RESOLUTION
  // -------------------------------------------------------------------------
  //
  // ⚠ THESE ARE METHODS RATHER THAN DERIVATIONS, DELIBERATELY. Their inputs are the form
  // model's own validity, and a control group is not a signal, so a derivation over it
  // would compute once and never again. A template expression, by contrast, is
  // re-evaluated on every check of this view, and every change to a control's value
  // originates in an event inside this view — which is exactly what marks the view for
  // checking under this change-detection strategy. The remaining input that is NOT a form
  // value is the enforcement gate, which is a signal; it is read through
  // {@link revisionDependentGate} so that the template's own reactive tracking observes it
  // and the two mechanisms compose instead of competing.

  /**
   * Reads the enforcement gate in a way a template expression can depend on.
   *
   * Reads the revision counter as well as the gate. The counter changes when the gate
   * flips, which is what re-validates the group, and reading it here is what makes the
   * template's reactive consumer notice that the resolved messages may now differ.
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
   * The single pre-flight failure, resolved in the LEGACY ORDER.
   *
   * ⚠ THE ORDER IS L272 → L278 → L284 → L290 AND IS REPRODUCED EXACTLY. Each arm of
   * `cmdUpdate_Click` exited immediately, so the first failure won and the later checks
   * never ran. The confirmation mismatch therefore fires BEFORE the policy breach, which
   * is counter-intuitive — a person who mistypes the confirmation of a four-character
   * credential is told about the confirmation, not about the length — and it is measured,
   * so it is reproduced rather than rationalised.
   *
   * ⚠ THE MESSAGES COME FROM THE SHARED FAILURE VOCABULARY, NOT FROM COPIES DECLARED HERE.
   * That vocabulary already carries the measured resource wording for four of the five
   * legacy outcomes, keyed by the machine-readable code the server reports, so the
   * pre-flight message and the message for the same failure reported by the server are
   * THE SAME STRING and cannot drift apart. The fifth outcome — a write the server
   * refused — has no pre-flight form, because only the server can reach it.
   *
   * @returns The failure, or null when nothing is wrong.
   */
  firstFailure(): CredentialFailure | null {
    const controls = this.form.controls;
    const requiresCurrent = this.revisionDependentGate();

    // 1. L272 — the replacement and its confirmation must match.
    if (this.form.hasError('passwordMismatch')) {
      return {
        field: 'confirmPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.mismatch'],
      };
    }

    // 2. L278 — the replacement must satisfy the policy. Both the required rule and the
    //    length rule resolve here, because the legacy single call failed identically for an
    //    absent credential and for a short one.
    if (controls.newPassword.invalid) {
      return { field: 'newPassword', message: this.policyMessage() };
    }

    // 3. L284 — the credential in force must be supplied, when the gate says it applies.
    //    The comparison is against the empty string exactly as the legacy comparison was,
    //    so a value of nothing but spaces counts as supplied — see the note on
    //    {@link readControlValue} for why that conflation is preserved.
    if (requiresCurrent && controls.currentPassword.value === '') {
      return {
        field: 'currentPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.missing'],
      };
    }

    // 4. L290 — the replacement must differ from the credential in force.
    if (this.form.hasError('passwordNotDifferent')) {
      return {
        field: 'newPassword',
        message: PASSWORD_UPDATE_MESSAGE['user.password.not_different'],
      };
    }

    return null;
  }

  /**
   * The message to show beside one control.
   *
   * Two sources, in order. A pre-flight failure wins, because it is the more specific
   * statement and because it is what the caller can act on without a round trip. Failing
   * that, the server's own per-field message for the control is shown, narrowed and
   * selected by the shared resolver rather than read out of the problem document here —
   * the dictionary's keys are the server's model-state names rather than camel-cased
   * members, they are matched case-insensitively, and the workspace forbids property
   * access on an index-signature map, all of which that resolver already handles.
   *
   * Nothing is shown before the caller has either submitted or visited the control, which
   * preserves the legacy screen's silence on first render.
   *
   * @param field The control to resolve a message for.
   * @returns The message, or null when the control has none to show.
   */
  fieldError(field: CredentialField): string | null {
    const control: AbstractControl = this.form.controls[field];

    if (this._submitAttempted() || control.touched) {
      const failure = this.firstFailure();

      if (failure !== null && failure.field === field) {
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
   * Whether the submit affordance is unavailable.
   *
   * Unavailable while a write is outstanding and while the route names no account to write
   * to. NOT disabled merely because the form is invalid: a submit that cannot be pressed
   * cannot report why, and the legacy screen let the caller submit and then told them what
   * was wrong. Pressing it with an invalid form resolves a message and sends nothing.
   *
   * ⚠ THE TEST IS THE ROUTE'S ACCOUNT KEY, NOT THE FETCHED ACCOUNT, AND THAT DISTINCTION
   * IS A FIX RATHER THAN A SIMPLIFICATION. This used to require `user() !== null`, which
   * made the affordance depend on a read this screen does not need in order to submit: the
   * write is addressed by the route's key and carries only credentials, so the fetched
   * detail contributes nothing to it. That coupling turned the screen off in exactly the
   * situation where it is MANDATORY. A caller with an outstanding credential change is
   * refused `GET api/v1/users/{id}` — measured, `403 auth.remediation_required` — so
   * `user()` stayed null, the affordance stayed disabled, and the one screen the server was
   * insisting they use was the one screen they could not use. The route key is present
   * whenever the address is well formed, which is the honest precondition for a write
   * addressed by that key.
   *
   * ⚠ AND IT FAILS CLOSED ON THE OPERATION THE CALLER IS NOT ENTITLED TO. The two operations
   * this screen performs are authorised separately by the server — a change is restricted to
   * the account holder, a reset to a tenant administrator — and the route now admits the union
   * of those two callers so that an administrator can reach the reset at all. Admitting the
   * union means one of them can arrive at an operation the server will refuse: a caller who is
   * neither the holder nor an administrator resolves to the RESET operation, because the
   * operation is chosen by ownership. Offering the action to them would send a credential
   * replacement that comes back refused. It is withheld instead, and the server remains the
   * authority either way.
   *
   * @returns Whether to disable the affordance.
   */
  submitDisabled(): boolean {
    return this.saving() || this.accountKey() === null || !this.operationPermitted();
  }

  // -------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------

  /**
   * Wires the three reactions this screen needs.
   *
   * ⚠ EACH IS A GENUINE SIDE EFFECT, WHICH IS THE ONLY THING AN EFFECT IS FOR. One issues
   * a request, one reconfigures the form, and one announces an outcome; none of them
   * derives a value, because a derived value is a derivation and every derivation on this
   * screen is one.
   *
   * ⚠ EVERY WRITE IS PERFORMED UNTRACKED. Without that, an effect would take a dependency
   * on whatever the code it calls happens to read, and the first of the three would take a
   * dependency on the very selection it establishes — which re-runs it, which re-issues
   * the request, which is an unbounded loop that no test with a stubbed transport would
   * ever reveal.
   */
  constructor() {
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
        // ⚠ THE READ IS SKIPPED WHILE THE CALLER OWES MANDATORY REMEDIATION, AND SKIPPING IT
        // IS THE FIX. The API refuses GET api/v1/users/{id} outright in that state -
        // measured, 403 auth.remediation_required - so issuing it could only ever produce a
        // refusal, and that refusal reached the shared failure slot and put an error banner
        // across the one screen the server was insisting the caller use. Not asking is both
        // the correct request count and the correct rendering: the two summary rows the
        // answer would have populated are the only thing withheld, and the template omits
        // them rather than showing them empty.
        //
        // This is a genuine dependency and not an incidental read. When the advisory clears -
        // which the successful change below causes - this effect re-runs and issues the read
        // it previously declined, so the details appear as soon as they are readable.
        return;
      }

      untracked(() => {
        // The store clears its own failure slot and drops the previously held account when
        // the selection changes, so arriving on this screen never shows the previous
        // screen's failure and never shows the previous account's details.
        this.store.selectUser(key);
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

    // 3. THE SETTLING OF A WRITE DRIVES THE ANNOUNCEMENT. The store's commands return
    //    nothing and record their outcome in shared slices, so the outcome is observed
    //    rather than returned.
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
          // The failure renders through the banner and has already been announced by the
          // HTTP failure interceptor. Nothing is announced here, and the form is left
          // populated so the caller can correct it rather than retype it — except for the
          // credential values, which are the caller's own to retype and which no code path
          // here reads.
          return;
        }

        this.announceSuccess();
      });
    });
  }

  /**
   * Attaches or detaches the required rule on the credential in force.
   *
   * MIGRATION: the rule is moved rather than declared once, and the reason is the measured
   * gate asymmetry. `Password.ascx.vb` enforced the rule only for a non-administrator
   * (L284), and the control it reads is not rendered for an administrator acting on another
   * account (L150-L152). A required rule left attached to a control the template does not
   * render would make the form permanently and silently invalid for that caller.
   *
   * The value is cleared when the rule is detached, and that is a contract requirement
   * rather than tidiness: the API refuses a reset request that carries a credential in
   * force, so a value the caller can no longer see must not survive to be transmitted. It
   * also matches the legacy behaviour exactly — a text box the legacy screen did not render
   * posted back the empty string.
   *
   * @param requiresCurrent Whether the rule applies.
   */
  private applyCurrentPasswordRule(requiresCurrent: boolean): void {
    const control = this.form.controls.currentPassword;

    if (requiresCurrent) {
      // Idempotent: the framework compares by reference before adding, and
      // `Validators.required` is a stable reference, so repeated application cannot
      // accumulate duplicates.
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
   * Announces a successful write and clears the form.
   *
   * MIGRATION: the wording is `PasswordChanged.Text` and the severity is the successor of
   * the legacy green success type, both measured from `ManageUsers.ascx.vb` L793-L797,
   * where the legacy screen resolved success through that separately named key rather than
   * through the enumeration member's name. The same key served a change and a reset there,
   * and one message serves both here.
   *
   * MIGRATION: THE EMAIL REMINDER IS NOT CARRIED FORWARD, so `PasswordMailError` is
   * unreachable from this file. The legacy handler sent the account holder a reminder
   * immediately after a successful write and, when that send threw, replaced the success
   * message with a warning-severity 'the password has been reset, but there was an error
   * sending the Email reminder message'. Mail is a server-side concern in the target and no
   * client code can observe its outcome, so that third outcome has no client-side form. Its
   * severity is recorded because it is the one place the legacy screen reported a partial
   * success as a WARNING rather than as an error, and because a future server-side notice
   * must inherit that severity rather than be reported as a fault.
   *
   * MIGRATION: THE TWO LEGACY EVENTS ARE SUBSUMED HERE. `Password.ascx.vb` L52-L53 declared
   * `PasswordUpdated` and `PasswordQuestionAnswerUpdated`, raised them with a status-bearing
   * argument object (L360-L394), and a parent control translated each status into a page
   * message. This screen is a ROUTE LEAF with no parent to notify, so it declares no
   * outputs at all: the notification queue is the successor of that message channel, and
   * the second event has no successor because the panel that raised it is dropped. No
   * server-side event bus is introduced, and none was needed — the seven account-lifecycle
   * events in the legacy base control were in-process notifications between controls.
   */
  private announceSuccess(): void {
    /*
     * ⚠ WHETHER THIS SCREEN IS ABOUT TO BE LEFT IS DECIDED ONCE, HERE, and used for both the
     * statement and the departure below. The two must agree: a confirmation that outlives a change
     * of screen it never makes would sit over an unrelated screen, and one that does NOT outlive a
     * departure it does make would be erased before it could be read. Computing the condition twice
     * would leave them free to disagree.
     */
    const leavingForRemediation = this.accountDetailWithheld() && this.isSelf();

    this.notifications.notify('success', PASSWORD_CHANGED_TEXT, null, leavingForRemediation);

    // Returns every control to the empty string rather than to null, which is the whole
    // point of constructing them non-nullable, and returns the group to pristine and
    // untouched in the same call. Clearing is both the faithful behaviour — a
    // password-mode text box never re-rendered its value across a postback — and the
    // correct one: no credential outlives the request that used it.
    this.form.reset();
    this._submitAttempted.set(false);
    this._resetConfirmOpen.set(false);

    if (leavingForRemediation) {
      this.concludeRemediation();
    }
  }

  /**
   * Concludes a MANDATORY credential change and hands the caller onward.
   *
   * ⚠ WITHOUT THIS THE JOURNEY NEVER ENDS, and that is the whole reason it exists. The
   * advisory the caller has just satisfied is carried in the HELD SESSION rather than
   * recomputed by the client, so the server stops refusing the moment the credential changes
   * while this client goes on believing remediation is outstanding — and the root redirect goes
   * on resolving back to this screen, indefinitely.
   *
   * ⚠ THE ADVISORY IS CLEARED LOCALLY AND THE SESSION IS DELIBERATELY *NOT* RENEWED. Renewing
   * was the first thing tried here and it is unfixably wrong: the change endpoint revokes every
   * refresh token the account holds, by design and for a stated reason, so the renewal answers
   * `401`, the store treats a refused renewal as a session that is over, and the caller is
   * signed out seconds after correctly doing what the server demanded. Observed end to end in a
   * browser. The reasoning, and why asserting this locally is sound, is set out in full on
   * {@link AuthStore.noteCredentialRemediated}.
   *
   * THE DESTINATION IS THE APPLICATION ROOT, NOT A SCREEN. Naming a screen here would put the
   * both-advisories-outstanding precedence in a second place, and the two would drift. The root
   * redirect owns that decision: it sends a caller who ALSO owes a profile completion to the
   * profile screen, and everyone else to the landing their authority admits.
   */
  private concludeRemediation(): void {
    this.auth.noteCredentialRemediated();

    // Not awaited, and its rejection absorbed, matching how every other screen in this
    // application navigates: a navigation the router refuses is not something this screen can
    // act on, and an unhandled rejection would be reported as an application fault.
    //
    // ⚠ REPLACES: a completed credential change must not sit in BACK history, and
    // `core/guards/unsaved-changes.guard.ts` reads this flag to recognise a departure the
    // application initiated. Pushing would make the gate offer to discard a password change that
    // had already succeeded.
    void this.router.navigateByUrl('/', { replaceUrl: true }).catch(() => false);
  }

  // -------------------------------------------------------------------------
  // ACTIONS
  // -------------------------------------------------------------------------

  /**
   * Handles the form's submission.
   *
   * Reproduces `cmdUpdate_Click` (`Password.ascx.vb` L269-L307): the four pre-flight rules
   * are resolved in their measured order and the FIRST failure stops the submission, which
   * is what each `Exit Sub` did. Nothing is transmitted when a rule fails.
   *
   * MIGRATION: the pre-flight resolution is CLIENT-SIDE where the legacy screen resolved it
   * on the server after a full page postback, which is a mechanism change with unchanged
   * outcomes — the same four comparisons over the same four values produce the same
   * verdict. THE SERVER REMAINS THE AUTHORITY: every rule here is also enforced by the API's
   * own validator, and a request that slipped past this method would be refused there.
   *
   * MIGRATION: the commented-out block at L296-L299, which would have suppressed the
   * credential in force for a caller who was not the account holder, is NOT ported. It was
   * dead in the original, and the concern it was reaching for is now settled by the
   * endpoint split rather than by a conditional argument.
   *
   * An administrative reset is routed through its confirmation instead of being dispatched
   * immediately; see {@link requiresResetConfirmation}.
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

  /**
   * Confirms the administrative reset.
   *
   * The pre-flight rules are resolved again rather than trusted from the moment the dialog
   * opened, because the dialog does not freeze the form and a value can change underneath
   * it.
   *
   * ⚠ THE DIALOG IS CLOSED ON THIS PATH AS WELL AS ON THE CANCEL PATH. The shared dialog
   * emits at most once for its own lifetime, so a flag left set would leave a dialog
   * mounted that can no longer be dismissed and can no longer confirm.
   */
  confirmReset(): void {
    this._resetConfirmOpen.set(false);

    if (this.submitDisabled() || this.firstFailure() !== null) {
      return;
    }

    this.dispatch(OPERATION_RESET);
  }

  /**
   * Declines the administrative reset.
   *
   * Closes the dialog and issues nothing. The form keeps its values, so declining costs the
   * caller nothing beyond the decision.
   */
  cancelReset(): void {
    this._resetConfirmOpen.set(false);
  }

  /**
   * Builds the request and hands it to the store.
   *
   * ⚠ THE CREDENTIAL VALUES ARE READ HERE AND NOWHERE ELSE, and they are read straight into
   * the request object. They are not trimmed, normalised, case-folded, measured or logged:
   * a credential comparison that normalised its operands would accept a credential the
   * server rejects, and the legacy code read its text boxes verbatim for the same reason.
   *
   * ⚠ A RESET TRANSMITS NULL FOR THE CREDENTIAL IN FORCE. The API refuses a reset that
   * carries one, and null states the absence unambiguously where the empty string the legacy
   * code would have sent merely implies it.
   *
   * The pending operation is recorded BEFORE the command is issued, so the effect that
   * observes the outcome cannot miss a write that settles synchronously.
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
      // Transmitted for both operations. The API compares it with the replacement only for
      // a change, but sending it unconditionally keeps one request shape and lets the
      // server tighten that rule without a client change.
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
