/**
 * The create-and-edit account screen: routes `/users/new` and `/users/:userId`.
 *
 * This component is the contract owner for its folder. The paired template, stylesheet and specification bind
 * to the member names declared below, so a rename here is a rename in four files.
 *
 * MIGRATION: the legacy tab strip is replaced by ROUTING, not reproduced. `cmdUser` becomes this screen,
 * `cmdProfile` becomes `/users/:userId/profile` and `cmdPassword` becomes `/users/:userId/password`; `cmdRoles`
 * becomes a link to `/roles`, because no per-user role route exists; and `pnlServices` and `pnlRegister` are
 * dropped outright, since there is no services, subscriptions or self-registration endpoint to call.
 *
 * MIGRATION: the membership panel belongs on THIS screen, and the proof is at source - the legacy container
 * placed the user control and the membership control side by side inside a single row. Membership is therefore
 * a PER-ACCOUNT panel rather than a per-tenant settings page, and the four account actions it carries live
 * here with it.
 *
 * Both legacy panels rendered their fields through a property-editor control that is one of the excluded files
 * under `Library/Controls/**`, so the field sets are RECOVERED from the localised resource files and
 * reconciled against the account contract. Wording is taken from the resource VALUE and never from a markup
 * attribute; the deliberate-drops block below records the measured case where trusting a markup attribute
 * would have shipped a second field captioned "Password:".
 *
 * What is deliberately absent: no permission check, because the route is already gated, that gate is ADVISORY
 * and the server's refusal is the only authority; and no security-code field, password question or answer,
 * online indicator or password retrieval. Each omission is annotated at the constant or member it would have
 * touched.
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
 * The minimum password length the legacy installation enforced.
 *
 * Measured at `Website/release.config`, `minRequiredPasswordLength="7"`.
 *
 * MIGRATION: this value is preserved EXACTLY and is deliberately not raised. Tightening a password policy
 * during a migration locks out every existing account whose password satisfied the old rule and not the new
 * one, which converts a technology change into an outage. Hardening is a separate, explicit decision that
 * belongs to whoever owns the accounts, not to the port.
 */
export const PASSWORD_MIN_LENGTH = 7;

/**
 * The number of non-alphanumeric characters the legacy installation required.
 *
 * Measured at `Website/release.config`, `minRequiredNonalphanumericCharacters="0"`.
 *
 * ZERO, which makes the rule vacuous as shipped: every password satisfies it, including a purely
 * alphanumeric one. The check is still implemented rather than elided, because it is a genuine
 * configuration-driven rule and eliding it would hide the fact that the legacy installation had switched it
 * off. It is a no-op at this value and would become load-bearing the moment the value changed.
 */
export const PASSWORD_MIN_NON_ALPHANUMERIC = 0;

/**
 * Characters that do not count as alphanumeric, as the legacy check defined them.
 *
 * `Library/Components/Users/UserController.vb` builds `New Regex("[^0-9a-zA-Z]")` and counts its matches.
 * The class is reproduced exactly, including the fact that it is ASCII-only: a letter outside `a-zA-Z`
 * counts as non-alphanumeric under this rule, and widening the class to Unicode letters would change which
 * passwords pass.
 */
const NON_ALPHANUMERIC_PATTERN = /[^0-9a-zA-Z]/g;

/**
 * The pattern an electronic-mail address must match.
 *
 * MIGRATION: this is the MEASURED legacy expression, explicitly anchored. Angular anchors a `pattern`
 * supplied as a STRING but leaves a `RegExp` exactly as written, so an unanchored expression would accept
 * any value merely CONTAINING an address. The anchors are therefore load-bearing rather than stylistic.
 *
 * `Validators.email` is deliberately NOT used. It applies a different expression, so substituting it would
 * change which addresses are accepted — a behaviour change dressed up as a simplification.
 *
 * `[\w.-]` is the measured `[\w\.-]`: inside a character class a full stop is already literal, so the escape
 * is redundant and is dropped without altering the language the expression matches.
 */
const EMAIL_PATTERN = /^[\w.-]+(\+[\w-]*)?@([\w-]+\.)+[\w-]+$/;

/**
 * The maximum length of each identity field, in characters.
 *
 * MIGRATION: the authority is the column and the API rule, because the markup has none. `User.ascx` declares
 * `maxlength` on exactly four boxes — the credential, its confirmation, the password question and its
 * answer, all at 20 — and NOTHING on the five identity boxes, so the legacy screen accepted text of any
 * length and let the write refuse it. Each figure below is therefore the terminal column width, which the
 * API's create and update rules bind to exactly:
 *
 * - `Username nvarchar(100) NOT NULL`
 * - `FirstName nvarchar(50) NOT NULL` and `LastName nvarchar(50) NOT NULL`
 * - `DisplayName nvarchar(128)`
 * - `Email nvarchar(256)`, widened from its original 100 during the upgrade chain
 *
 * Stating them here changes the MECHANISM by which an over-long value is refused, not WHETHER it is: a value
 * that used to travel and come back as a model-state failure is now refused beside the box that holds it.
 * Nothing accepted before is refused now, because the bounds are the server's own.
 */
const IDENTITY_MAX_LENGTH = Object.freeze({
  /**
   * `Username nvarchar(100) NOT NULL`; `CreateUserRequest` caps the member at the same figure.
   */
  username: 100,

  /**
   * `FirstName nvarchar(50) NOT NULL`.
   */
  firstName: 50,

  /**
   * `LastName nvarchar(50) NOT NULL`.
   */
  lastName: 50,

  /**
   * `DisplayName nvarchar(128)`.
   */
  displayName: 128,

  /**
   * `Email nvarchar(256)`.
   */
  email: 256,
});

// Measured wording — labels and titles
//
//  Every string below is the VALUE of a localised resource entry, quoted exactly, including the spacing
//  anomalies. Two of those anomalies are annotated defects and are preserved rather than tidied.
//
//  MIGRATION: localisation is NOT ported. The legacy mechanism is Web Forms specific, the resource files are
//  read for wording only, and NO translation runtime is added — the framework's localisation package is not a
//  dependency, its tagged-template marker is used nowhere, and no translation attribute appears in the paired
//  template. These are therefore plain constants, exported so the specification can assert the exact wording
//  rather than re-typing it.
//
//  MIGRATION: every one of these is rendered as PLAIN TEXT. The legacy code knew it:
//  `Website/admin/Security/AccessDenied.ascx.vb` encoded the message it had just decoded before showing it.

/**
 * `ControlTitle_edit.Text` — the screen title before a record has loaded.
 */
export const EDIT_MODE_TITLE = 'Edit User Accounts';

/**
 * `AddUser.Text` — the screen title while creating.
 */
export const CREATE_MODE_TITLE = 'Add New User';

/**
 * `UserTitle.Text` — the per-record title format.
 *
 * Two placeholders: the display name, then the identifier. The legacy substitution was
 * `UserInfo.UserID.ToString`; see the note on explicit coercions for why the identifier is converted
 * deliberately here rather than concatenated.
 */
export const EDIT_RECORD_TITLE_FORMAT = 'Edit User - {0} (Id: {1})';

/**
 * `MembershipTitle.Text` — the legend of the membership panel.
 */
export const MEMBERSHIP_PANEL_TITLE = 'Membership Information';

/**
 * `Delete.Text` — the destructive action's label for another account.
 */
export const DELETE_LABEL = 'Delete';

/**
 * `UnRegister.Text` — the destructive action's label for one's OWN account.
 *
 * `User.ascx.vb` selects between this and {@link DELETE_LABEL} on `IsUser`. That fact is not derivable from
 * this component's permitted dependency surface, so the label resolves to {@link DELETE_LABEL} and this
 * constant records the measured alternative. The gap is reported rather than guessed at.
 */
export const UNREGISTER_LABEL = 'UnRegister';

/**
 * `DeleteItem.Text`, from the shared resources — the confirmation for another account.
 */
export const CONFIRM_DELETE_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * `ConfirmUnRegister.Text` — the confirmation for one's OWN account.
 *
 * Selected by the same unevaluable `IsUser` test as {@link UNREGISTER_LABEL}, and recorded here for the same
 * reason.
 */
export const CONFIRM_UNREGISTER_MESSAGE = 'Are you sure you want to un-register';

/**
 * `PasswordHelpAdmin.Text` — the help line above the password fields.
 *
 * This screen IS the administrator path — self-registration is out of scope with `pnlRegister` — so only
 * this one is carried.
 */
export const PASSWORD_HELP =
  'Optionally enter a password for this user, or allow the system to generate a random password';

/**
 * `Required.Text`, from the shared resources.
 *
 * DEFECT 8, annotated and NOT fixed: the measured value begins with a LEADING SPACE. It is reproduced
 * verbatim because the value is the measurement; trimming it would make this constant disagree with the
 * resource file it is quoting. Any leading whitespace is inert once rendered, so preserving it costs
 * nothing.
 */
export const REQUIRED_LEGEND = ' All fields marked with a red arrow are required.';

/**
 * The element identifier of the password section's GROUP message container — #8.
 *
 * ⚠ IT HAS TO BE A STABLE, KNOWN VALUE BECAUSE TWO CONTROLS POINT AT IT. The rule spans the password
 * box and its confirmation, so the message belongs to neither control and both must reference it
 * through `aria-describedby` and `aria-errormessage`. The shared field component owns that wiring for
 * a message that belongs to ONE control and generates the identifier itself; a group message has no
 * field to own it, which is why this one is declared here and referenced from three places in the
 * template.
 *
 * Exported so a specification asserts against the same constant the template binds rather than
 * against a copy of the string.
 */
export const PASSWORD_GROUP_ERROR_ID = 'user-form-password-rule';

/**
 * The element identifier of the password box — #8.
 *
 * Declared as a constant rather than left as a template literal because {@link
 * UserFormComponent.onSubmit} now has to FIND that box in order to move focus to it, so the value is
 * read from two places and must not be able to drift between them.
 */
export const PASSWORD_CONTROL_ID = 'user-form-password';

// Measured wording — validation messages
//
//  MIGRATION, and this is the most consequential note in the file: every validator on this screen is
//  AUTHORED, because the legacy markup declared almost none.
//
//  The validator census across the five in-scope administration directories, measured case-insensitively on
//  opening tags: sixteen `asp:RequiredFieldValidator`, of which NONE is in the Users directory; three
//  `asp:RegularExpressionValidator`, all three in the out-of-scope `Users/bulkemail.ascx`, so effectively
//  zero in scope; nineteen `asp:CompareValidator`, none of them between a password and its confirmation; and
//  exactly ONE `asp:CustomValidator` — `valPassword` at `User.ascx`, declared with no `ControlToValidate` and
//  no `ErrorMessage`, which is to say an opaque server-side hook. There is no `asp:ValidationSummary`
//  anywhere in the entire `Website/` tree.
//
//  The Users screens were therefore validated IMPERATIVELY, in the code-behind. The requirement that
//  validation rules must match is satisfied by expressing those same rules DECLARATIVELY here, reproducing
//  the measured order and the measured messages exactly. That is a change of MECHANISM with UNCHANGED
//  BEHAVIOUR, and it is parity work rather than licence to invent a rule the legacy never applied.
//
//  It follows that the shared error banner has no legacy predecessor at all. It is NET-NEW, not a
//  translation, and nothing was ported into it.

/**
 * `UserInfo_Username.Required`.
 */
export const USERNAME_REQUIRED_MESSAGE = 'User name is required';

/**
 * `UserInfo_FirstName.Required`.
 */
export const FIRST_NAME_REQUIRED_MESSAGE = 'First name is required';

/**
 * `UserInfo_LastName.Required`.
 */
export const LAST_NAME_REQUIRED_MESSAGE = 'Last name is required';

/**
 * `UserInfo_DisplayName.Required`.
 */
export const DISPLAY_NAME_REQUIRED_MESSAGE = 'Display Name is required';

/**
 * `UserInfo_Email.Required`.
 */
export const EMAIL_REQUIRED_MESSAGE = 'Email is required';

/**
 * `UserInfo_Email.Validation`.
 *
 * One of only two `.Validation`-suffixed entries in the whole thirty-seven-file resource census, which is
 * why it is quoted rather than composed.
 */
export const EMAIL_PATTERN_MESSAGE = 'You must enter a valid email address';

/**
 * `PasswordMismatch.Text`, from the shared resources.
 *
 * MIGRATION: the legacy comparison was imperative — `User.ascx.vb` reads `If txtPassword.Text <>
 * txtConfirm.Text`. Expressing it as a group validator is a mechanism change with unchanged behaviour,
 * including the ORDINAL comparison: the legacy `<>` on two strings compared them exactly, so `!==` is the
 * faithful operator and no case folding or trimming is introduced.
 */
export const PASSWORD_MISMATCH_MESSAGE = 'The Password and Confirmation Passwords do not match';

/**
 * The wording shown when a password fails the policy.
 *
 * MIGRATION: the legacy message is `The password specified is invalid. Please specify a valid password.
 * Passwords must be at least [PasswordLength] characters in length and contain at least [NoneAlphabet]
 * non-alphanumeric characters.`, and the two bracketed tokens were substituted by a plain string replacement
 * at `Library/Components/Users/UserController.vb` from the membership provider's configuration. The
 * token-replacement subsystem is out of scope, so the MEASURED configured values are interpolated directly
 * from the two policy constants above — which is exactly what the legacy substitution produced at run time,
 * and which keeps the sentence honest if either constant ever changes.
 */
export const INVALID_PASSWORD_MESSAGE =
  'The password specified is invalid.  Please specify a valid password.  Passwords must be at ' +
  `least ${String(PASSWORD_MIN_LENGTH)} characters in length and contain at least ` +
  `${String(PASSWORD_MIN_NON_ALPHANUMERIC)} non-alphanumeric characters.`;

// Measured wording — outcomes and guards

/**
 * `UserAuthorized.Text` — success wording after authorising.
 */
export const USER_AUTHORIZED_MESSAGE = 'User successfully Authorized';

/**
 * `UserUnAuthorized.Text` — success wording after withdrawing authorisation.
 */
export const USER_UNAUTHORIZED_MESSAGE = 'User successfully Un-Authorized';

/**
 * Success wording after releasing a locked-out account.
 *
 * DEFECT 5, annotated and NOT fixed. `Website/admin/Users/ManageUsers.ascx.vb` calls
 * `AddModuleMessage("UserUnLocked", ModuleMessageType.GreenSuccess, True)`, but the key `UserUnLocked` is
 * defined in NO resource file — verified by searching both `Website/admin/Users/App_LocalResources/` and
 * `Website/App_GlobalResources/`, which return no match. The legacy screen therefore rendered its own key
 * name, or nothing, where a sentence belonged.
 *
 * This wording is AUTHORED-BECAUSE-ABSENT, following the measured pattern of its two siblings above. It is
 * NOT measured wording and is not presented as such.
 */
export const USER_UNLOCKED_MESSAGE = 'User successfully Unlocked';

/**
 * Success wording after obliging an account to change its password.
 */
export const PASSWORD_CHANGE_REQUIRED_MESSAGE = 'This user must change their password at next login';

/**
 * Success wording after an account's details are written.
 *
 * AUTHORED. `User.ascx.vb` raises `OnUserUpdated` and `OnUserUpdateCompleted` after a successful write and
 * the container declares no handler for either, so the legacy screen confirmed nothing at all. Silence after
 * a destructive-looking action is a defect rather than a behaviour worth preserving, and confirming a write
 * neither adds nor removes a workflow.
 */
export const USER_UPDATED_MESSAGE = 'User account updated';

/**
 * Success wording after an account is created — U-M9.
 *
 * ⚠ THE SCREEN CONFIRMED NOTHING AT ALL BEFORE THIS, AND THE NAVIGATION IS WHAT MADE THAT SERIOUS.
 * A successful create leaves this screen immediately for the account listing — reproducing
 * `Website/admin/Users/ManageUsers.ascx.vb`, which tests `If e.CreateStatus = UserCreateStatus.Success`
 * and answers with `Response.Redirect(ReturnUrl, True)` — so the operator arrived at a listing of two
 * hundred and fifty accounts with no statement that theirs had been added and no reliable way to find
 * it among them. Measured: the notification surface was EMPTY after a successful create, while the
 * sibling role and module creates both confirmed theirs.
 *
 * AUTHORED, and on exactly the same footing as {@link USER_UPDATED_MESSAGE} beside it: the legacy
 * redirected without confirming, so there is no wording to recover, and confirming a write neither adds
 * nor removes a workflow. The account's own sign-in name is substituted into it, because the one thing
 * an operator needs on arriving at a long listing is which row is theirs.
 *
 * `{name}` is substituted with the sign-in name the server confirmed — never with the value typed into
 * the form, which may differ from what was stored.
 */
export const USER_CREATED_MESSAGE = 'User account {name} created';

/**
 * `NoUser.Text` — the account does not exist.
 */
export const NO_USER_MESSAGE = "This account doesn't exist";

/**
 * `SuperUser.Text`.
 *
 * DEFECT 11, annotated and NOT fixed: this entry is DEFINED but UNREFERENCED on the guard path it reads as
 * though it belongs to. The measured guard at `ManageUsers.ascx.vb` is `If User.IsSuperUser And Not
 * Me.UserInfo.IsSuperUser` and it emits `NoUser`, not `SuperUser`. No use is invented for it here; it is
 * recorded so the measurement is not lost, and the guard's actual wording is {@link NO_USER_MESSAGE}.
 */
export const SUPER_USER_MESSAGE =
  'This account represents a SuperUser.  You do not have the rights to edit a SuperUser Account.';

/**
 * `InvalidUser.Text` — the account exists but is not a member of this tenant.
 */
export const INVALID_USER_MESSAGE = 'This account is not a User in the current Portal.';

/**
 * `NotAuthorized.Text` — the caller may not edit this account.
 */
export const NOT_AUTHORIZED_MESSAGE = 'You are not authorized to edit this user.';

/**
 * `UserLockedOut.Text` — repeated failed sign-ins have locked the account.
 */
export const USER_LOCKED_OUT_MESSAGE =
  'This account is currently locked out due to too many unsuccessful login attempts.';

/**
 * `EmailError.Text`.
 *
 * The DOUBLE SPACE after the first full stop is in the measured value and is reproduced verbatim.
 *
 * MIGRATION: `requiresUniqueEmail="false"` at `Website/release.config`, so address uniqueness is NOT
 * enforced on this side and two accounts in one tenant may legitimately share an address. This wording is
 * used only when the SERVER refuses, which is the sole authority on the rule.
 *
 * MIGRATION: the legacy screen attributed EVERY exception from a write to this one cause — `User.ascx.vb`
 * catches any exception and constructs `New UserUpdateErrorArgs(User.UserID, User.Username, "EmailError")`.
 * That misattribution is not reproduced: a refusal surfaces the server's own problem document, and this
 * wording is reserved for the refusal it actually describes.
 */
export const EMAIL_CONFLICT_MESSAGE =
  'This portal requires a unique Email Address.  The Email Address you entered has already been used.';

/**
 * `ExceededUserQuota.Text` — the tenant's account allowance is reached.
 *
 * Surfaced only when the server refuses. The allowance is NOT pre-checked here: doing so would cost an extra
 * request, would race every other administrator, and would still have to handle the refusal it was trying to
 * predict.
 *
 * MIGRATION: the underlying quota value is never rendered by this screen, and that is deliberate.
 * `Library/Components/Portal/PortalController.vb` maps a database null to -1 while initialises the value to
 * 0, so ZERO means UNLIMITED and MINUS ONE means NOT SET. A bare number would therefore be actively
 * misleading, and coalescing either sentinel away would destroy the distinction.
 */
export const EXCEEDED_USER_QUOTA_MESSAGE =
  'Adding this user will exceed the User Quota for this site. Please contact your hosting provider ' +
  'for inquiries related to increasing your User Quota.';

// Authored advisories for the two contract gaps

/**
 * The sentence shown beside a generated password at the moment it is revealed.
 *
 * MIGRATION: the legacy generated the password on the SERVER — `User.ascx.vb` calls
 * `UserController.GeneratePassword()` — and then e-mailed it to the new account holder. The creation
 * contract carries no generate-a-password field and there is no mail endpoint, so BOTH halves of that had to
 * be answered differently: the value is generated in the browser, and it is handed to the operator on screen
 * instead of being posted.
 *
 * The earlier arrangement generated a credential and then discarded it, which left the operator with an
 * account nobody on earth could sign in to and no indication of that until after the account existed.
 * Revealing it once to the administrator who just created the account grants no authority they did not
 * already hold — that same administrator can reset the credential at will — and it is what makes the created
 * account usable.
 *
 * The reveal is deliberately bounded: it happens only after the server has CONFIRMED the creation, it
 * happens on this screen rather than through any announcement channel, the value is never written to
 * storage, a URL, a log or a notification, and dismissing the panel discards it and completes the redirect
 * the screen would otherwise have made.
 */
export const RANDOM_PASSWORD_ADVISORY =
  'This password is shown once and is not stored anywhere in this screen. No notification e-mail can ' +
  'be sent, so give it to the account holder now \u2014 otherwise the only remedy is an ' +
  'administrative password reset.';

/**
 * The help text on the notify control, which states its unavailability BEFORE it is used.
 *
 * MIGRATION: `plNotify` is a measured control whose purpose was to e-mail the new account holder, and the
 * creation contract carries NO notify member. The control is retained because it is part of the agreed
 * member contract that the paired template and specification bind to, but its value cannot be transmitted.
 *
 * It is rendered disabled and unticked, and the sentence is shown beside it rather than after a submission.
 * The earlier arrangement kept the measured initial state — ticked — and raised a warning once the account
 * had already been created, so the operator ticked a box that asserted an action the installation cannot
 * perform, submitted, and only then learned that no notification went out. A control that cannot act must
 * say so while there is still a decision to make. The initial value therefore departs from the measured
 * `checked="True"`, deliberately: a ticked box that does nothing is a false statement about what will
 * happen.
 */
export const NOTIFY_UNAVAILABLE_ADVISORY =
  'Unavailable: this installation exposes no mail endpoint, so no notification e-mail can be sent.';

/**
 * Advisory raised after authorising an account.
 *
 * MIGRATION: `ManageUsers.ascx.vb` shows that authorising ALSO sent mail — `Mail.SendMail(User,
 * MessageType.UserRegistrationPublic, PortalSettings)`. There is no mail endpoint, so this is a documented
 * functional reduction.
 */
export const AUTHORIZE_MAIL_ADVISORY =
  'The account was authorised, but no welcome e-mail was sent: this installation exposes no mail ' +
  'endpoint.';

// The statuses this screen interprets

// Deliberate drops. Nothing in this block declares a value; it exists so that a control this screen does NOT
// render is not indistinguishable from one nobody looked for.
//
// DROP: the security-code field. The legacy challenge control is one of the excluded files under
// Library/Controls/**, so there is nothing to port. The COMPENSATING CONTROL is the backend rate limiter, and
// it covers this screen's own write: the limiter classifies an endpoint as credential-bearing from its
// METADATA rather than from its path, and UsersController.CreateAsync carries [CredentialEndpoint], so account
// creation sits in the credential partition and a rejection is answered 429 Too Many Requests. It is named
// here because dropping a human-verification challenge without naming what replaced it would be a silent
// reduction in defence.
//
// DROP: the password question and answer. This drop is BEHAVIOUR-PRESERVING rather than a reduction. Both rows
// were declared `visible="false"` and the code-behind revealed them only when the membership provider required
// a question and answer, which the legacy configuration set to FALSE, so the fields never rendered and the
// validation branch that read them never executed. No endpoint accepts them either.
//
// DELEGATED: the help affordance belongs to the shared form-field component and the creation-failure wording
// to the shared failure-code table, so neither is re-implemented here.
//
// The standing rule for this file: every string is taken from the resource VALUE and never from a markup
// attribute. The legacy markup captioned the security-code box "Password:" while its resource value read
// "Security Code:", and a label with no explicit resource key resolves its key from the CONTROL ID, so the
// resource wins at run time and the attribute is dead. Trusting the attribute would have shipped a second
// field captioned "Password:".

// The creation outcome vocabulary, recorded by name
//
//  MIGRATION: no contract on this boundary carries a numeric creation outcome. Creation reports its result as
//  an HTTP status with an RFC 7807 problem document, so this screen keys success on the STATUS and keys
//  wording on the failure-code STRING. The two members below are recorded because the vocabulary's values are
//  counter-intuitive and because the specification asserts them by NAME rather than by magic integer — which
//  is the whole point of importing the enumeration rather than writing 13 down.

/**
 * The member that means "the account was created".
 *
 * THIRTEEN, not zero. Recorded by name so no reader has to trust a literal.
 */
export const CREATE_SUCCEEDED: UserCreateStatus = UserCreateStatus.Success;

/**
 * The zero member, which is NOT an outcome.
 *
 * It is the initial "no error recorded yet" sentinel, proven by `User.ascx.vb`, which detects failure by
 * testing for any value OTHER than this one. Treating zero as success would invert that test — and across
 * the three legacy vocabularies a zero-means-success assumption is wrong two times in three, because the
 * sign-in vocabulary succeeds at one and this one at thirteen.
 */
export const CREATE_NOT_YET_ATTEMPTED: UserCreateStatus = UserCreateStatus.AddUser;

/**
 * The refusal status. Named rather than written inline so the intent is legible.
 */
const FORBIDDEN_STATUS = 403;

/**
 * The not-found status.
 */
const NOT_FOUND_STATUS = 404;

/**
 * The state-conflict status.
 */
const CONFLICT_STATUS = 409;

/**
 * The store commands this screen issues.
 *
 * The store is provided at the root and is shared with the listing and the profile screens, so its failure
 * slot can hold a failure this screen did not cause. Filtering on the operation is what stops a listing
 * failure from disabling this form or appearing in its banner.
 *
 * A frozen array rather than a `Set`, so the membership test is a plain comparison against a literal union
 * and a misspelled operation is a compilation error.
 */
const SCREEN_OPERATIONS: readonly UserOperation[] = Object.freeze<UserOperation[]>([
  'loadUser',
  'createUser',
  'updateUser',
  'deleteUser',
  'setApproval',
  'unlockUser',
  'requirePasswordChange',
]);

/**
 * A membership action awaiting its outcome, together with what to say once it has one.
 *
 * The store's failure record names the OPERATION that failed, which is what identifies the outcome as this
 * action's rather than a sibling command's. It does not carry the argument the command was given, so the
 * wording cannot be re-derived from it: `onAuthorize` and `onUnauthorize` both issue `setApproval` and
 * differ only in the state they asked for.
 */
interface AwaitedMembershipAction {
  /**
   * The store operation whose settling reports this action.
   */
  readonly operation: UserOperation;

  /**
   * The wording to announce at success severity once the action is confirmed.
   */
  readonly success: string;

  /**
   * A caveat to raise at warning severity alongside the success, or `null`.
   *
   * Only the authorising action carries one: the legacy handler also sent mail, and there is no mail
   * endpoint, so the reduction is stated to the operator instead of dropped in silence.
   */
  readonly advisory: string | null;
}

/**
 * The alphabet a generated password draws from.
 *
 * Purely alphanumeric, which satisfies {@link PASSWORD_MIN_NON_ALPHANUMERIC} at its measured value of zero.
 * The visually ambiguous glyphs — capital I, capital O, lower case l, zero and one — are omitted, because a
 * generated password that has to be read aloud or retyped is worthless if its characters cannot be told
 * apart.
 */
const RANDOM_PASSWORD_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';

/**
 * The length of a generated password.
 *
 * Comfortably above {@link PASSWORD_MIN_LENGTH}, so a generated password cannot fail the policy the form has
 * just been told to skip.
 */
const RANDOM_PASSWORD_LENGTH = 16;

// PURE HELPERS

/**
 * Counts the non-alphanumeric characters in a password.
 *
 * A faithful port of `UserController.vb`, which counted regular-expression matches. `String.prototype.match`
 * with a global expression returns null rather than an empty array when nothing matches, so the null is
 * converted to zero explicitly instead of being coalesced — the count is data, and zero is a real count.
 *
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
 * Whether a password satisfies the preserved policy.
 *
 * A faithful port of `UserController.ValidatePassword`, which applied three checks in order: minimum length,
 * minimum non-alphanumeric count, and a strength expression applied ONLY when configured. The strength
 * expression was never configured in the measured installation — `Website/release.config` declares no
 * `passwordStrengthRegularExpression` on the membership provider at all — so the third check is not
 * reproduced, and its absence is recorded here rather than left implicit.
 *
 * MIGRATION: the legacy routine evaluated both surviving checks and only then returned, so it could not
 * short-circuit. This one returns early, which is observationally identical because both checks are pure and
 * neither has a side effect.
 *
 * @param password The password to test. Never trimmed and never normalised.
 * @returns True when the password may be submitted.
 */
function satisfiesPasswordPolicy(password: string): boolean {
  if (password.length < PASSWORD_MIN_LENGTH) {
    return false;
  }

  return countNonAlphanumeric(password) >= PASSWORD_MIN_NON_ALPHANUMERIC;
}

/**
 * Generates a password using the platform's cryptographic random source.
 *
 * MIGRATION: generation MOVED from the server to the browser, because the account-creation contract carries
 * no field with which to ask the server to generate one. The transport is unchanged — a typed password and a
 * generated one travel in the same request body over the same channel — so relocating the generator
 * introduces no new exposure. What it does change is that nobody learns the result, which {@link
 * RANDOM_PASSWORD_ADVISORY} states plainly.
 *
 * The modulo reduction over a 32-bit draw is very slightly biased towards the first few characters of the
 * alphabet. The bias is on the order of one part in fifty million for a fifty-six character alphabet and is
 * irrelevant for a value that exists only to be replaced by an administrative reset.
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
 * Reads a control's value as a string without trusting its declared type.
 *
 * MIGRATION: this is one of the explicit coercions the Option Strict asymmetry forces. The thirty-nine
 * administration code-behinds compiled with `<compilation debug="false" strict="false">`, which is to say
 * Option Strict OFF, so they could read a control's value and use it as a string with no conversion written
 * down. A group validator receives `AbstractControl`, whose `value` is untyped, so the narrowing that the
 * legacy compiler performed silently is written out here. A non-string reads as the empty string rather than
 * being coerced with `String(...)`, because the empty string is the legacy absent-marker for text and is
 * therefore the faithful stand-in for "nothing was supplied".
 *
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
 * Reads a control's value as a boolean without trusting its declared type.
 *
 * The companion to {@link readTextControl}, and the same Option Strict note applies. A non-boolean reads as
 * false, which is the legacy absent-marker for a boolean — `Library/Components/Shared/Null.vb` returns
 * `False` for a missing boolean, so "not supplied" and "no" were already the same value there and nothing is
 * lost.
 *
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

/**
 * The screen's typed form.
 *
 * Every control is declared with a non-nullable value type, and every control is CONSTRUCTED with
 * `nonNullable: true`. Both halves matter: the type parameter makes `getRawValue()` yield a fully typed
 * object rather than a partial one, and the construction option makes `reset()` return a control to its
 * initial value rather than to null. Without the option a reset would put null into a control the type says
 * can never hold null.
 *
 * The ten members are the union of two measured panels. Five are the recovered `UserInfo_` fields, all of
 * them required. Three are the measured checkboxes on `pnlAddUser` (`User.ascx` and). Two are the measured
 * password inputs.
 */
export interface UserFormModel {
  /**
   * The sign-in name. `UserInfo_Username`.
   *
   * MIGRATION: fixed for the life of the account. The legacy source marked the field read-only and offered
   * no rename, and the update contract carries no name member, so this control is DISABLED while editing
   * rather than merely ignored — a disabled control cannot be dirtied, which is what stops a rename that
   * could never be saved.
   */
  username: FormControl<string>;

  /**
   * The given name. `UserInfo_FirstName`.
   */
  firstName: FormControl<string>;

  /**
   * The family name. `UserInfo_LastName`.
   */
  lastName: FormControl<string>;

  /**
   * The name shown in place of the name parts. `UserInfo_DisplayName`.
   */
  displayName: FormControl<string>;

  /**
   * The electronic-mail address. `UserInfo_Email`.
   */
  email: FormControl<string>;

  /**
   * Whether the new account may sign in immediately. `chkAuthorize`.
   *
   * Initial value TRUE, from the measured markup `checked="True"` at `User.ascx`, which the code-behind does
   * not override. Create-only: in edit mode authorisation is changed through the membership action, which is
   * a separate endpoint.
   */
  authorize: FormControl<boolean>;

  /**
   * Whether to e-mail the new account holder. `chkNotify`.
   *
   * Initial value TRUE, from `checked="True"` at `User.ascx`. The value cannot be transmitted — see {@link
   * NOTIFY_UNAVAILABLE_ADVISORY}.
   */
  notify: FormControl<boolean>;

  /**
   * Whether to generate the password rather than type it. `chkRandom`.
   *
   * DEFECT 6, annotated and NOT fixed: the markup declares `checked="True"` at `User.ascx`, and
   * `User.ascx.vb` then assigns `chkRandom.Checked = False` on every non-postback. The two contradict each
   * other and the code-behind wins at run time, so the observable initial state is UNCHECKED. The initial
   * value here is therefore FALSE, matching the behaviour rather than the markup, and the contradiction is
   * recorded rather than resolved in the markup's favour.
   *
   * Create-only, and administrator-create-only: `User.ascx.vb` hides the row entirely on the
   * self-registration path.
   */
  randomPassword: FormControl<boolean>;

  /**
   * The password to set. `txtPassword`.
   *
   * MIGRATION: the measured markup carries `maxlength="20"`, mirroring the legacy column `[Password]
   * [nvarchar](20)`. That ceiling is an HTML INPUT ATTRIBUTE for visual parity and belongs in the template.
   * It is deliberately NOT a `Validators.maxLength(20)` here: the legacy had no maximum-length VALIDATOR, so
   * the browser silently truncated over-long input rather than reporting an error, and adding a validator
   * would turn a silent truncation into a visible rejection. It is likewise not a type or contract
   * constraint — the abandoned twenty-character store is gone, and the account contract states no length
   * bound of its own.
   */
  password: FormControl<string>;

  /**
   * The password repeated. `txtConfirm`.
   *
   * DEFECT 9, annotated and ELIMINATED rather than ported: `User.ascx.vb` calls
   * `txtConfirm.Attributes.Add("value", txtConfirm.Text)` and the same for the password box, a Web Forms
   * postback hack that re-emitted a typed password into the rendered markup so it would survive a round
   * trip. There is no postback and no round trip, so the hack has no counterpart and none is invented.
   */
  confirmPassword: FormControl<string>;
}

/**
 * The password rules, expressed as ONE group-level validator.
 *
 * MIGRATION: this reproduces the legacy creation screen's `Validate()` exactly, and the ORDER is the
 * specification rather than an implementation detail. The measured sequence:
 *
 * 1. the ENTIRE password block is CREATE-ONLY - nothing below it applies while editing;
 * 2. the status begins on `UserCreateStatus.AddUser`, whose zero member is the "no error recorded yet"
 *    SENTINEL rather than an outcome, which is why failure is detected by testing for any OTHER value;
 * 3. when random generation is requested the password and confirmation rules are SKIPPED ENTIRELY, because
 *    the generated value replaces the operator's;
 * 4. the mismatch check;
 * 5. the policy check, GUARDED on the status still being that sentinel.
 *
 * Step 5's guard is the subtle part: because the mismatch at step 4 has already moved the status off the
 * sentinel, a mismatch SHORT-CIRCUITS the policy check, so a password that both fails the policy and
 * disagrees with its confirmation reported the MISMATCH. That precedence is preserved literally by returning
 * at the first failure.
 *
 * The legacy question-and-answer branch is not reproduced: it was gated on the membership provider requiring
 * a question and answer, which the legacy configuration set to FALSE, so the branch could never execute.
 * Omitting dead code is behaviour-preserving.
 *
 * Expressing all of this as a single group validator, rather than as imperative `setValidators()` calls driven
 * by the mode and the generate flag, is deliberate: an effect is for a genuine side effect, and retuning a
 * form's validators from one would make validity depend on the order in which effects happened to run.
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
      // The empty case is flagged as well as worded, so the template and the specification can tell "nothing
      // typed" from "too weak" while the MESSAGE stays the one the legacy showed. The legacy reported both
      // as an invalid password: an empty string has length zero, which fails the minimum-length check.
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
  // ReactiveFormsModule for the typed form; RouterLink for the three cross-screen actions the legacy tab
  // strip used to provide; five shared components and one pipe. Nothing else is imported, and in particular
  // the permission directive is NOT: see the note on authorisation in the class body.
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
    FocusFirstInvalidDirective,
  ],
  templateUrl: './user-form.component.html',
  styleUrl: './user-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserFormComponent {

  /**
   * Registers this screen's unsaved-entry probe with the application's tracker.
   *
   * ⚠ WHY A REGISTRATION RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and
   * only one of them is a router navigation: Cancel, an in-application link and the browser's Back
   * button are navigations a route guard can refuse, while closing or reloading the tab is not, and
   * only the browser's own unload prompt covers that - which needs the dirty state at an arbitrary
   * moment rather than at a navigation. One tracker holding probes answers both, and the probe is
   * released automatically when this screen is destroyed, so a screen that has gone can never hold
   * a navigation up. Measured before this existed: a dirty form was discarded in silence by all
   * four exits, with instrumented `confirm`, `alert` and `beforeunload` recording nothing at all.
   *
   * A form that is being SAVED is not dirty in the sense that matters here - the entry is on its
   * way to the server, and prompting about it would ask the operator to confirm discarding work
   * they have already committed.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.store.saving() === false,
  );
  /**
   * The account identifier taken from the route, as a string, or undefined.
   *
   * The name is EXTERNALLY FIXED and must stay exactly `userId`. The application configures the router with
   * component input binding, which matches a route parameter to an input OF THE SAME NAME; renaming this to
   * `id` or `userID` would break the binding silently, with no compilation error and no run-time exception —
   * the input would simply stay undefined and every visit would look like a create.
   *
   * On the `new` route the parameter is ABSENT, and that absence is the whole of the create-versus-edit
   * decision. Component input binding assigns undefined for a parameter the active route does not carry, so
   * the default matches what the router actually delivers.
   */
  readonly userId = input<string | undefined>(undefined);

  /**
   * The account state this screen reads and commands. Never a writable signal.
   */
  private readonly store = inject(UserStore);

  /**
   * The transient success and advisory channel.
   */
  private readonly notifications = inject(NotificationService);

  /** Reports a write that settles after this screen has gone; see {@link UserFormComponent.handOverPendingWrite}. */
  private readonly deferredOutcome = inject(DeferredOutcomeService);

  /** This screen's lifetime, held for the one hand-over below and nothing else. */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Used only for the two measured redirects and the password cross-link.
   */
  private readonly router = inject(Router);

  /**
   * The signed-in operator, read ONLY to decide whether this screen is editing their own account.
   *
   * This is not an authorisation source and nothing here is enforced from it — the server owns every
   * membership decision. It answers one question the measured screen asked of the same two identities:
   * whether the operator and the subject are the same person.
   */
  private readonly auth = inject(AuthStore);

  /**
   * This screen's own root element, used for exactly one purpose — #8.
   *
   * ⚠ READ ONLY TO ANSWER "IS THERE AN INVALID CONTROL FOR THE SHARED FOCUS DIRECTIVE TO FIND", and
   * never to read or write a value. The password rule is a GROUP rule, so it marks the form group
   * invalid and leaves both password boxes valid — which means the shared directive's invalid-control
   * query matches nothing and it correctly does nothing. Something still has to take focus, and this
   * is how the component establishes that the directive has declined, so the two can never both act
   * on one submit.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /**
   * This view's change detector, used for exactly one purpose — #8.
   * ⚠ READ THE MEASUREMENT BEFORE REMOVING THIS. The password box lives behind a disclosure, and when
   * a submit is refused by the group rule while that disclosure is CLOSED the component has to open it
   * before anything can be focused. Opening it sets a signal; the box only enters the document when
   * that signal is RENDERED. The first implementation deferred the focus move to a resolved promise on
   * the assumption that a microtask settles after change detection. It does not: a microtask queued
   * inside the event handler runs before the scheduled render, so the callback looked the box up,
   * found `null`, and returned. Measured across two independent trials — 30 polls over 3007ms and 25
   * polls over 2500ms — focus never left the element it started on, in one case the submit button
   * itself, even though the section HAD expanded and the box DID exist by the time polling began.
   * Rendering synchronously removes the timing question rather than moving it: the box exists on the
   * next statement, so the focus call is ordinary code and a specification needs neither a fake clock
   * nor a settle. Only this view is checked, and the disclosure it renders belongs to this view.
   */
  private readonly changeDetector = inject(ChangeDetectorRef);

  // Mode — sentinel-safe identifier resolution

  /**
   * The route identifier as a number, or undefined when the route carries none.
   *
   * MIGRATION, and this is the highest-risk correctness rule on the screen. The measured identity seeds and
   * the legacy absent-marker COLLIDE: `Portals.PortalID` is `IDENTITY(-1, 1)` and `Roles.RoleID` is
   * `IDENTITY(0, 1)`, while `Library/Components/Shared/Null.vb` returns -1 for a missing integer. Minus one
   * and zero are therefore both REAL identifiers somewhere in this schema as well as markers for absence.
   *
   * Presence is consequently tested EXPLICITLY and never by falsiness. A truthiness test would read zero as
   * absent, a `> 0` test would reject both zero and minus one, and a `?? -1` fallback would manufacture the
   * very sentinel that causes the collision. `Number.isInteger` accepts zero and minus one — both are
   * integers — and rejects `NaN`, which is what a non-numeric segment and the empty string both parse to, so
   * a malformed segment falls through as absent rather than as identifier zero.
   *
   * `Users.UserID` is `IDENTITY(1, 1)`, so an account identifier of zero does not occur naturally in this
   * table. Handling it correctly is therefore DEFENSIVE rather than load-bearing here — but the discipline
   * is applied uniformly so that no screen reasons about identifiers differently from the next one.
   *
   * MIGRATION: the legacy contained the forbidden idiom itself. `ManageUsers.ascx.vb` reads `If (User.UserID
   * > Null.NullInteger)`, which is a `> -1` test standing in for a presence test. It is replaced by the
   * explicit check below rather than transliterated. 's `If User.PortalID <> Null.NullInteger And
   * User.PortalID <> PortalId` is a different case and a legitimate one: there, -1 genuinely means "not
   * scoped to a tenant" and the comparison is deliberate.
   */
  protected readonly resolvedUserId: Signal<number | undefined> = computed<number | undefined>(
    () => {
      // MIGRATION: an explicit coercion the Option Strict asymmetry forces — the legacy read this from the
      // request collection and used it as a number with no conversion written down — and it is delegated to
      // the one parser in the workspace that makes it. This screen previously used `Number.parseInt` with no
      // shape test of its own, which read '12abc' as 12 and dispatched a request against an account the
      // operator never named; the shared parser refuses a partial parse outright. It also applies the API's
      // 32-bit range and the safe-integer ceiling, neither of which was checked here before.
      //
      // Absence is `undefined` on this screen because that is what the optional route input carries, and
      // `isCreateMode` tests exactly that. The nullish coalescing is safe for a sentinel: it fires only on
      // `null`, so identifier `0` — and `-1` — survive it untouched.
      return parseRouteId(this.userId()) ?? undefined;
    },
  );

  /**
   * Whether the screen is creating rather than editing.
   */
  protected readonly isCreateMode: Signal<boolean> = computed<boolean>(
    () => this.resolvedUserId() === undefined,
  );

  /**
   * Whether the screen is editing rather than creating.
   */
  protected readonly isEditMode: Signal<boolean> = computed<boolean>(() => !this.isCreateMode());

  /**
   * Whether the address carries something that is not an account identifier.
   *
   * ⚠ THE DISTINCTION {@link isCreateMode} CANNOT MAKE, and could not make. It tests
   * {@link resolvedUserId} for `undefined`, and the shared parser answers `null` - coalesced to
   * `undefined` there - both for an ABSENT parameter and for one it refuses. So `/users/abc` reported
   * itself as create mode and rendered the add-account form with nine of its ten controls enabled and
   * Authorize already ticked, having asked the server nothing at all.
   *
   * Read through the shared three-way reader rather than by testing the parsed value again, so that
   * the grammar and the bounds are the SAME ones {@link resolvedUserId} applies - a second, local
   * test would be free to disagree with the first, and the disagreement would be silent.
   */
  protected readonly addressUnreadable: Signal<boolean> = computed<boolean>(
    () => readRouteId(this.userId()).kind === 'unreadable',
  );

  // STORE PROJECTIONS

  /**
   * The account being edited, or null while creating or before the read returns.
   */
  /**
   * A generated credential awaiting the server's answer, or null.
   *
   * A plain field rather than a signal, and deliberately not exposed: nothing may render it while the
   * creation is still outstanding, because a credential shown for an account that was then refused is a
   * credential shown for no account at all. It is cleared on either outcome — moved to {@link
   * revealedCredential} on a confirmed creation, discarded on a refusal.
   */
  private heldCredential: string | null = null;

  /**
   * The generated credential currently on screen, or null.
   *
   * Written exactly once per creation, from the settled-outcome effect, and cleared by {@link
   * UserFormComponent.dismissCredential}. It is never persisted, never placed in a URL, never logged and
   * never passed to the announcement channel — see {@link RANDOM_PASSWORD_ADVISORY}.
   */
  private readonly _revealedCredential = signal<string | null>(null);

  protected readonly selectedUser: Signal<UserDetail | null> = this.store.selectedUser;

  /**
   * The generated credential to hand over, or null when there is none on screen.
   *
   * Exposed read-only; only the settled-outcome effect and {@link UserFormComponent.dismissCredential} write
   * it.
   */
  protected readonly revealedCredential: Signal<string | null> = this._revealedCredential.asReadonly();

  /**
   * @see RANDOM_PASSWORD_ADVISORY — the sentence shown beside the revealed credential.
   */
  protected readonly credentialAdvisory = RANDOM_PASSWORD_ADVISORY;

  /**
   * @see NOTIFY_UNAVAILABLE_ADVISORY — the help text stating why the notify control cannot act.
   */
  protected readonly notifyUnavailableHelp = NOTIFY_UNAVAILABLE_ADVISORY;

  /**
   * Whether a read or a write for this screen is outstanding.
   *
   * Both halves are needed: the read populates the membership panel and the write drives the submit button,
   * and a screen that spun for only one of them would look finished while a request was still in flight.
   */
  protected readonly loading: Signal<boolean> = computed<boolean>(
    () => this.store.selectedUserLoading() || this.store.saving(),
  );

  /**
   * The problem document to show in the banner, or null when there is nothing to show.
   *
   * MIGRATION: the measured severity vocabulary routes a PERMISSION REFUSAL as a WARNING rather than as an
   * error, and the banner is reserved for errors.
   *
   * So this projection keeps only ERROR-severity failures, and warning-severity ones are announced through
   * the notification channel instead. Presenting a refusal in both places would say the same thing twice;
   * presenting it in the danger banner would misrepresent a rule as a fault. Severity itself is not
   * re-decided here — it is read from the shared summariser through the store, which is the one place that
   * rule lives.
   *
   * Failures belonging to another screen's command are excluded, because the store is shared at the root.
   */
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
  //
  //  MIGRATION: the legacy round-tripped screen state through view state and the session. None of that
  //  survives. Measured: `Session(` appears at zero sites in the in-scope trees, and the only view-state
  //  reads in the library trees are four `"UserId"` reads in `UserModuleBase.vb`. State lives in the browser
  //  as signals, so nothing about the form's presentation travels to the server and back.

  /**
   * Whether a submission has been attempted, so messages appear only after one.
   */
  /**
   * The typing ceiling emitted on the credential inputs.
   *
   * Shared from `core/utils/credential-bounds.util.ts`, which holds the API's own bound and the reasoning
   * behind it, so this screen cannot drift away from the server rule.
   *
   * MIGRATION: the legacy ceiling of 20 IS DELIBERATELY NOT PRESERVED. `User.ascx` declares
   * `maxlength="20"`, mirroring the legacy `Password nvarchar(20)` storage width rather than any rule the
   * legacy applied to a credential; the measured legacy policy declares no maximum at all, and the successor
   * stores a one-way hash.
   *
   * IT IS EMITTED AS `[attr.maxlength]`, NEVER AS A LITERAL `maxlength` ATTRIBUTE, and the difference is
   * load-bearing. A literal attribute matches the selector of the framework's own maximum-length validator
   * directive, `[maxlength][formControlName]`, and its value binds that directive's input — which silently
   * attaches a length validator this component declares nowhere. An over-long credential would then make the
   * control invalid while `firstValidatorMessage` returns null for the `maxlength` key, so the form would
   * refuse to submit and display no reason. The attribute binding sets the DOM attribute without matching
   * that selector, which was confirmed by measurement rather than assumed.
   */
  protected readonly credentialMaxLength = CREDENTIAL_MAX_LENGTH;

  protected readonly submitAttempted = signal(false);

  /**
   * Whether the destructive-action confirmation is open.
   */
  protected readonly deleteDialogOpen = signal(false);

  /**
   * Whether the credentials section is expanded.
   */
  protected readonly credentialsExpanded = signal(true);

  /**
   * Whether the password section is expanded.
   */
  protected readonly passwordExpanded = signal(true);

  /**
   * Whether the membership section is expanded.
   */
  protected readonly membershipExpanded = signal(true);

  /**
   * Whether the new-account options section is expanded.
   *
   * MIGRATION: this group had NO toggle while its three siblings each had one, and the
   * justification recorded beside it - that "the measured markup gave these two rows no section
   * head" - does not survive checking, because it is equally true of all four groups: `User.ascx`
   * registers the collapsible-section control at L4 and then uses it ZERO times, which the note at
   * the top of this file and DEFECT 10 beside the toggle handlers both already record. The legacy
   * therefore supplies no authority for singling this group out; the disclosure pattern is entirely
   * this screen's own addition, and applying it to three of four sibling groups was arbitrary.
   * Removing all three was the alternative and was rejected: it would strip a working affordance
   * from operators and undo the `aria-controls` wiring these toggles were deliberately given.
   */
  protected readonly newAccountOptionsExpanded = signal(true);

  /**
   * Set while a create is in flight, so its outcome can be acted on exactly once.
   */
  private readonly createSubmitted = signal(false);


  /**
   * Set while an update is in flight.
   */
  private readonly updateSubmitted = signal(false);

  /**
   * Set while a removal is in flight.
   */
  private readonly deleteSubmitted = signal(false);

  /**
   * The membership action in flight, or `null` when none is.
   *
   * Held so that the four membership actions announce only what the server actually did. Each one previously
   * announced the moment it dispatched, which reports a success the server may be about to refuse — and on
   * this screen a refusal is genuinely expected: the state-setting endpoint answers a conflict when the
   * account already holds the state that was asked for.
   *
   * The wording travels with the marker rather than being re-derived on settling, because the two
   * authorisation actions share ONE store operation and are told apart only by the argument they sent, which
   * the recorded failure does not echo back.
   */
  private readonly awaitedMembershipAction = signal<AwaitedMembershipAction | null>(null);

  /**
   * The account whose details were last hydrated into the form.
   *
   * Guards the hydration effect so that re-reading the same account — which every membership action causes,
   * because the store reconciles the selection afterwards — does not overwrite edits the operator has in
   * progress.
   */
  private hydratedUserId: number | undefined = undefined;

  // THE FORM

  /**
   * The screen's typed reactive form.
   *
   * Every control is constructed with an explicit type argument and `nonNullable: true`; see {@link
   * UserFormModel} for why both are required. Required validators are attached where the recovered resource
   * declares a `.Required` message, and the address additionally carries the measured pattern.
   *
   * The group-level password validator carries the conditional rules. It is passed a reader for the mode
   * rather than a boolean, so it re-evaluates when the route changes without anything having to reattach it.
   */
  // MIGRATION: the five identity fields use `requiredText` from `core/utils`, NOT
  // `Validators.required`, because Angular's own validator accepts a value of one space and the
  // server does not. Every one of these five is declared with FluentValidation's `NotEmpty()`,
  // whose string predicate is `string.IsNullOrWhiteSpace`, so an all-whitespace entry was
  // refused server-side after a round trip. Measured on the wire before changing anything:
  // `PUT /api/v1/users/2` with `"displayName":" "` answered HTTP 400 with
  // `errors.DisplayName = ["Display Name is required"]` — word for word the sentence this
  // screen already holds for `required`, which is what proves the two sides always meant the
  // same rule and only the browser was lax. The validator reports under the `required` key, so
  // {@link firstValidatorMessage} selects that same measured sentence with no change.
  //
  // The password controls below deliberately keep `Validators.required`: a credential is opaque
  // bytes the user chose rather than prose to be judged for blankness, and this application
  // never trims one.
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
      // Disabled and unticked, departing from the measured initial state on purpose. See {@link
      // NOTIFY_UNAVAILABLE_ADVISORY}: there is no mail endpoint, so a control that could be ticked would
      // promise something the installation cannot do. It is retained rather than removed because it is part
      // of the agreed member contract, and a disabled control still reports its value through `getRawValue`,
      // so nothing downstream changes shape.
      notify: new FormControl<boolean>({ value: false, disabled: true }, { nonNullable: true }),
      // Initial FALSE. DEFECT 6: the markup says checked and `User.ascx.vb` unchecks it on every
      // non-postback, so the behaviour is unchecked.
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
   * The screen title, which the measured resources prove is MODE-DEPENDENT.
   *
   * Three cases, all measured. Creating uses `AddUser.Text`. Editing a loaded account uses the
   * `UserTitle.Text` format with the display name and the identifier substituted. Editing before the read
   * returns uses `ControlTitle_edit.Text`, because a title with an empty name substituted into it would read
   * as a defect.
   *
   * MIGRATION: two explicit coercions the Option Strict asymmetry forces. The identifier is converted with
   * `String(...)` rather than concatenated, mirroring the legacy `UserInfo.UserID.ToString` that Option
   * Strict off would have let it omit. And the display name is tested for EMPTINESS explicitly: its column
   * is declared not-null with an empty-string default, so an unset display name arrives as `''` rather than
   * as null, and the sign-in name is the measured stand-in. Neither `null` nor `undefined` can reach the
   * rendered title.
   */
  protected readonly pageTitle: Signal<string> = computed<string>(() => {
    /*
     * ⚠ AN UNREADABLE ADDRESS IS NOT CREATE MODE FOR HEADING PURPOSES, and the extra term is a
     * correction. `isCreateMode()` is false on `/users/abc` — no account can be read from it — but
     * this computed used to fall through to the create branch anyway, putting `Add New User` above
     * the sentence saying the account does not exist, while the route's own document title said
     * `Edit User Accounts`. A real browser measured all three labels on that one screen.
     *
     * Falling to `EDIT_MODE_TITLE` below also makes `/users/abc` and `/users/0` agree: one address
     * is unreadable and the other readable-but-absent, both name an account and neither can offer
     * one, so both now carry the same heading over the same measured legacy sentence. That matches
     * `ManageUsers.ascx.vb` L207/L221, where a missing account kept the edit screen's own title and
     * paired the warning with `DisableForm()`.
     */
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

  /**
   * The help line above the password section. Administrator wording only.
   */
  protected readonly passwordHelp: string = PASSWORD_HELP;

  /** {@link PASSWORD_GROUP_ERROR_ID} — #8. Bound to the container and to both controls that cite it. */
  protected readonly passwordGroupErrorId: string = PASSWORD_GROUP_ERROR_ID;

  /** {@link PASSWORD_CONTROL_ID} — #8. Bound to the password box, and used to find it on a refusal. */
  protected readonly passwordControlId: string = PASSWORD_CONTROL_ID;

  /**
   * @see IDENTITY_MAX_LENGTH — bound to each identity box's native attribute.
   *
   * The attribute is an ASSIST and never the rule: a value pasted past it, or delivered by an autofill that
   * ignores it, is still refused by the validator and again by the server. Stating the bound in the markup
   * simply stops the overflow happening rather than describing it afterwards.
   */
  protected readonly maxLengths = IDENTITY_MAX_LENGTH;

  /**
   * The legend recording that the marked fields are required.
   */
  protected readonly requiredLegend: string = REQUIRED_LEGEND;

  /**
   * The legend of the membership panel.
   */
  protected readonly membershipTitle: string = MEMBERSHIP_PANEL_TITLE;

  /**
   * The destructive action's label.
   *
   * MIGRATION: `User.ascx.vb` chose between `UnRegister` and `Delete` on `IsUser` — whether the account
   * being edited IS the signed-in caller. That fact is NOT derivable from this component's permitted
   * dependency surface: the account contract carries no such flag, and the current identity belongs to a
   * store this screen does not depend on. The label therefore resolves to the measured other-account
   * wording, {@link DELETE_LABEL}, and the self-account alternative is recorded at {@link UNREGISTER_LABEL}.
   * The gap is REPORTED rather than guessed at, and no permission check is invented to stand in for it.
   */
  protected readonly deleteLabel: string = DELETE_LABEL;

  /**
   * The confirmation shown before removal. Same unevaluable branch as the label.
   */
  protected readonly confirmDeleteMessage: string = CONFIRM_DELETE_MESSAGE;

  /**
   * Whether the destructive action is offered.
   *
   * The measured rule, from `User.ascx.vb`:
   *
   * ```
   * If AddUser Then cmdDelete.Visible = False
   * Else cmdDelete.Visible = Not (User.UserID = PortalSettings.AdministratorId)
   *                          AndAlso Not (IsUser And User.IsSuperUser)
   * ```
   *
   * The first clause is reproduced exactly: never offered while creating. Of the other two,
   * `PortalSettings.AdministratorId` and `IsUser` are both unavailable from the permitted dependency
   * surface, so the rule cannot be evaluated in full.
   *
   * What IS evaluable is the account's own superuser flag, and this implementation withholds the action for
   * any superuser. That is deliberately STRICTER than the legacy, which withheld it only when the superuser
   * was also the caller — a divergence, annotated as one. Erring towards withholding is the safe direction:
   * the action is destructive, the server refuses it with a permission status anyway, and offering a button
   * whose only possible outcome is a refusal is worse than not offering it.
   *
   * Comparisons are explicit throughout. There is no falsiness test on an identifier anywhere in this
   * member.
   */
  protected readonly canDelete: Signal<boolean> = computed<boolean>(() => {
    if (this.isCreateMode()) {
      return false;
    }

    const held: UserDetail | null = this.selectedUser();

    if (held === null) {
      return false;
    }

    // ⚠ THE SERVER'S CAPABILITY, NOT A RULE RESTATED HERE, and restating it was the defect. This read
    // `!held.isSuperUser`, which is only the FIRST half of the rule the removal operation enforces: it
    // also refuses the account named by the tenant's `Portals.AdministratorId`. Nothing in the detail
    // contract revealed who that was, so the second half was unreachable and was simply omitted - and
    // the two screens then disagreed about one permission for one account, the listing correctly
    // withholding the affordance for the tenant's administrator while this screen offered it.
    //
    // The contract now publishes the capability, computed by the same member that computes it for the
    // listing, so there is one rule in one place and no client-side approximation of it anywhere.
    return held.canDelete;
  });

  // The membership panel — read-only
  //
  //  Eight of the nine measured `UserMembership_` fields, projected individually so the template binds a
  //  value rather than reaching into a possibly-null record.
  //
  //  MIGRATION: the ninth field, `UserMembership_IsOnLine` ('User Is On Line:'), is DROPPED along with the
  //  `imgOnline` indicator on `pnlUser`. Users-online is out of scope and no endpoint reports it. The account
  //  contract does carry an `isOnline` member, but a value the API cannot keep current is worse than an
  //  absent one.
  //
  //  MIGRATION: the five dates are rendered through the shared date pipe, which already renders the legacy
  //  null-date sentinel as EMPTY. That is PARITY rather than an improvement: the legacy display helper
  //  returned the empty string for `Date.MinValue`, so a bare `01/01/0001` was never shown. The sentinel
  //  survives on the wire — the API never omits a member and serialises a minimum-value date as a real
  //  instant — so the erasure is confined to the display layer, which is where the legacy put it too.

  /**
   * `UserMembership_Approved` — whether the account may sign in.
   */
  protected readonly isApproved: Signal<boolean | undefined> = computed<boolean | undefined>(
    () => this.selectedUser()?.isApproved,
  );

  /**
   * `UserMembership_LockedOut` — whether failed sign-ins have locked the account.
   */
  protected readonly isLockedOut: Signal<boolean | undefined> = computed<boolean | undefined>(
    () => this.selectedUser()?.isLockedOut,
  );

  /**
   * `UserMembership_CreatedDate`.
   */
  protected readonly createdDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.createdDate,
  );

  /**
   * `UserMembership_LastActivityDate`.
   */
  protected readonly lastActivityDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastActivityDate,
  );

  /**
   * `UserMembership_LastLoginDate`.
   */
  protected readonly lastLoginDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastLoginDate,
  );

  /**
   * `UserMembership_LastPasswordChangeDate`.
   */
  protected readonly lastPasswordChangeDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastPasswordChangeDate,
  );

  /**
   * `UserMembership_LastLockoutDate`.
   */
  protected readonly lastLockoutDate: Signal<string | null | undefined> = computed(
    () => this.selectedUser()?.lastLockoutDate,
  );

  /**
   * `UserMembership_UpdatePassword` — whether the account must change its password.
   *
   * The account contract spells this `mustChangePassword`, and the sign-in contract spells it identically.
   * That agreement is load-bearing: were the two names to drift, one screen would stop learning that the
   * account it just loaded owes a password change. Delegated to the store's own projection rather than
   * re-derived, so there is one reading of it.
   *
   * MIGRATION: the legacy expressed this as one member of a five-value status field resolved BY PRECEDENCE,
   * so it could report exactly one condition at a time even when several held at once. The successor is a
   * set of INDEPENDENT flags, which can express combinations the legacy field could not. That is a real
   * divergence, annotated here rather than absorbed.
   */
  protected readonly mustChangePassword: Signal<boolean | undefined> =
    this.store.selectedUserMustChangePassword;

  // The four per-account membership actions — offered only when they would change something
  //
  //  ⚠ THESE FOUR GATES WERE MISSING, AND THE OMISSION CAME FROM READING ONLY HALF THE LEGACY SCREEN.
  //  `Membership.ascx` L13-L28 declares all four commands unconditionally, so a reading confined to the
  //  markup concludes they are always offered. They are not: `Membership.ascx.vb` L135-L145 assigns every
  //  one of their `Visible` properties on each data-bind, and the measured assignments are
  //
  //      If UserInfo.UserID = User.UserID Then     ' operator editing their OWN account
  //          cmdAuthorize.Visible = False
  //          cmdUnAuthorize.Visible = False
  //          cmdUnLock.Visible = False
  //          cmdPassword.Visible = False
  //      Else
  //          cmdUnLock.Visible      = Membership.LockedOut
  //          cmdUnAuthorize.Visible = Membership.Approved
  //          cmdAuthorize.Visible   = Not Membership.Approved
  //          cmdPassword.Visible    = Not Membership.UpdatePassword
  //      End If
  //
  //  where `UserInfo` is the SIGNED-IN operator (`PortalModuleBase.vb` L319-L323 returns
  //  `UserController.GetCurrentUserInfo`) and `User` is the account under edit
  //  (`UserModuleBase.vb` L439-L449 reads it with `UserController.GetUser(PortalId, UserId, False)`).
  //
  //  So every one of the four is offered ONLY when its own precondition holds, and the four are mutually
  //  informative rather than independent: an approved account is offered UnAuthorize and NOT Authorize.
  //  Offering both at once — which is what this screen did — presents one of them as an action whose only
  //  possible outcome is no change at all.
  //
  //  ⚠ THE MECHANISM IS `Visible`, NOT `Enabled`, so the faithful counterpart is CONDITIONAL RENDERING
  //  rather than a disabled control. That is also the better of the two: these preconditions are facts
  //  about the account, not about this form's state, so a disabled button here could never become enabled
  //  by anything the operator typed. A permanently dead control teaches nothing.
  //
  //  ⚠ EVERY TEST IS A STRICT EQUALITY AGAINST `true` OR `false`, NEVER A TRUTHINESS TEST OR A NEGATION.
  //  These three signals are `boolean | undefined`, and `undefined` means no account is in hand. The
  //  legacy always held a Membership object, so `Not Membership.Approved` could only ever read a real
  //  Boolean — but `!undefined` is `true`, so translating that negation literally would OFFER Authorize
  //  for an account whose approval state is unknown. Unknown is not the same as false, and only a
  //  measured `false` may offer the action.

  /**
   * Whether the operator is editing their own account.
   *
   * Derived exactly as the sibling profile screen derives it, against the same two identities the measured
   * code compared.
   *
   * ⚠ STRICT EQUALITY AGAINST AN EXPLICITLY RESOLVED KEY. A truthiness test would misread the account key
   * zero, and a fallback default would make an unresolved route parameter match a real key. An unresolved
   * identity on either side resolves to `false`, which withholds nothing — the four gates below then rest
   * on the membership facts alone, exactly as they do for any other account.
   */
  protected readonly isSelf: Signal<boolean> = computed<boolean>(() => {
    const subject: number | undefined = this.resolvedUserId();
    const caller = this.auth.currentUser();

    if (subject === undefined || caller === null) {
      return false;
    }

    return caller.userId === subject;
  });

  /**
   * `cmdAuthorize.Visible = Not Membership.Approved` — offered only for an account that may not sign in.
   */
  protected readonly canAuthorize: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.isApproved() === false,
  );

  /**
   * `cmdUnAuthorize.Visible = Membership.Approved` — offered only for an account that may sign in.
   */
  protected readonly canUnauthorize: Signal<boolean> = computed<boolean>(
    () => !this.isSelf() && this.isApproved() === true,
  );

  /**
   * `cmdUnLock.Visible = Membership.LockedOut` — offered only for an account failed sign-ins have locked.
   */
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

  /**
   * Whether any of the four is offered at all.
   *
   * Exists so the template can omit the action row itself rather than render an empty one. Both cases are
   * reachable and neither is exceptional: an operator editing their own account is offered none of the
   * four, and so is an account that is approved, unlocked and already obliged to change its password —
   * the second being the ordinary state of a healthy administrator account.
   */
  protected readonly hasMembershipActions: Signal<boolean> = computed<boolean>(
    () =>
      this.canAuthorize() ||
      this.canUnauthorize() ||
      this.canUnlock() ||
      this.canForcePasswordChange(),
  );

  // Guard behaviour — disabled and warned, never redirected

  /**
   * Whether the form is withheld because the server refused or found nothing.
   *
   * MIGRATION: the measured guard sequence at `ManageUsers.ascx.vb` runs four checks — superuser-add,
   * tenant-membership, superuser-edit, administrator-rights — and every failure does the SAME two things:
   * `AddModuleMessage(..., YellowWarning, True)` followed by `DisableForm()`. The screen is DISABLED and a
   * warning is shown. It is not hidden, not emptied and not redirected, and that is reproduced literally.
   *
   * The four checks themselves are NOT re-implemented on this side. They are authorisation decisions, the
   * server owns them, and it answers a refusal with a permission status. The legacy check embedded in a page
   * property getter at `Library/Components/Users/UserModuleBase.vb` is deliberately not ported: a
   * client-side permission test decides nothing, and duplicating one here would create a second rule free to
   * disagree with the enforced one.
   *
   * The permission directive is likewise NOT used on this screen, and the route's own permission gate is
   * ADVISORY. Two closed permission vocabularies exist — the policy names the API authorises against, and
   * the persisted permission keys — and the directive consumes the persisted keys, none of which describes
   * account administration. Passing a policy name to it would conflate the two vocabularies.
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

  /**
   * Whether the requested account could not be found.
   *
   * MIGRATION: this is the read's not-found refusal, not a successful empty answer. The projection used to
   * read "the read succeeded and reported nothing", because the account transport was declared as admitting
   * a successful null. It does not, and the server cannot send one: every value-bearing outcome is
   * translated through one helper on the API, and that helper answers a not-found problem document the
   * moment the value is absent. So the state this projection names — the one the legacy worded as `NoUser` —
   * arrives as a `404` on the READ, and is recognised as such here.
   *
   * Keyed on the read operation alone, deliberately. A `404` from a write means the account went away
   * underneath an edit, which is a refusal of that edit rather than a screen with no record to show: the
   * form stays on screen, disabled, carrying the same measured wording through {@link
   * UserFormComponent.refusalMessage}.
   */
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

  /**
   * Whether the submit action should be offered as available.
   */
  protected readonly canSubmit: Signal<boolean> = computed<boolean>(
    () => !this.formDisabled() && !this.loading(),
  );

  // WIRING

  constructor() {
    // Each effect below performs a GENUINE SIDE EFFECT — issuing a read, writing into a form, moving the
    // browser, announcing a message. None of them derives a value; every derivation on this screen is a
    // `computed`. Every write is wrapped in `untracked` so that writing cannot register a dependency and
    // re-trigger the effect that wrote it.

    // Reacts to the route. Entering create mode discards any selection the listing left behind, so a
    // previously viewed account cannot appear in an empty create form.
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

    // Settles a create. MIGRATION: the measured outcome is a REDIRECT to the return address —
    // `ManageUsers.ascx.vb` tests `If e.CreateStatus = UserCreateStatus.Success` and answers with
    // `Response.Redirect(ReturnUrl, True)`. The equivalent here is a navigation to the listing.
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

        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT AN ACCOUNT THAT HAS
        // ALREADY BEEN CREATED - AND REFUSING THAT PROMPT IS WORSE THAN THE PROMPT. This effect fires on
        // the transition OUT of `store.saving()`, so the probe registered at the top of this class - which
        // reads `dirty && store.saving() === false` - sees a dirty form with no write outstanding, and both
        // departures below are navigations it can refuse. An operator who declined would be left sitting on
        // a creation form for an account the server had just stored, and re-submitting it would be answered
        // with a conflict. Marking the form settled is the honest statement of what happened.
        //
        // Marked HERE rather than beside each departure because the credential hand-over defers its
        // navigation to `dismissCredential()`: the creation has still succeeded, so the entry is no longer
        // unsaved from this point on regardless of which of the two paths carries the operator away.
        this.form.markAsPristine();
        this.form.markAsUntouched();
        // ⚠ U-M9 — ANNOUNCED BEFORE EITHER OUTCOME BRANCH, so both are confirmed by one statement. The
        // credential branch below does not navigate and the ordinary branch does, and a confirmation
        // written into only one of them would leave the other silent — which is the defect, since the
        // ordinary branch is the common case. The notification surface is root-scoped and a success
        // notice survives the navigation deliberately, so the statement is still on screen when the
        // operator arrives at the listing.
        // The name comes from the account the SERVER confirmed, never from the form: the two can differ,
        // and a confirmation naming something that was not stored is worse than none.
        // `true`: the confirmation is raised immediately before a deliberate redirect and is meant to be read at the destination - the listing below. Without it the redirect on the
        // next line would discard the confirmation before the operator could read it.
        this.notifications.success(USER_CREATED_MESSAGE.replace('{name}', created.username), true);

        const generated: string | null = this.heldCredential;
        this.heldCredential = null;

        if (generated !== null) {
          // The redirect is deferred, not dropped. This screen is the only place the credential can be
          // handed over, and navigating now would destroy it before it had been read. Dismissing the panel
          // discards the value and performs the redirect — see {@link UserFormComponent.dismissCredential}.
          this._revealedCredential.set(generated);

          return;
        }

        // Replaced, not pushed: the work is done, so BACK must not return to a form for a record that
        // now exists. See the note on the sign-in screen's departure for the same rule stated in full.
        void this.router.navigate(['/users'], { replaceUrl: true });
      });
    });

    // Settles an update. No redirect: `User.ascx.vb` raises its two completion events and the container
    // declares a handler for neither, so the legacy screen stayed exactly where it was.
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

    // Settles a removal. MIGRATION: measured as a redirect to the return address at `ManageUsers.ascx.vb`.
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
        // prompted about the typing on the way out. There is nothing left to save, so pristine is the
        // honest state - the same correction the portal settings screen already carries on its own
        // removal path.
        this.form.markAsPristine();
        this.form.markAsUntouched();

    // Replaced, not pushed: the work is done, so BACK must not return to a form for a record that
    // has just been written - and the unsaved-entry gate reads a replacement as a departure the
    // application itself initiated, so it does not question it.
    void this.router.navigate(['/users'], { replaceUrl: true });
      });
    });

    // Settles a membership action. MIGRATION: the announcement now waits for the SERVER, which it previously
    // did not — the four actions announced success the moment they dispatched, so a refusal produced a green
    // "user authorized" beside a yellow refusal describing the opposite. A refusal is not hypothetical here:
    // the state-setting endpoint answers a conflict when the account already holds the state that was asked
    // for, which is exactly what a double click produces.
    //
    // Nothing is announced on the failure path. The refusal announcement is already made by the failure
    // effect above, which covers every operation this screen issues, so announcing here as well would say it
    // twice.
    //
    // The re-read the panel needs remains DELEGATED to the store, which reconciles the selected account
    // after each of the three transition commands.
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

    // MIGRATION: the second announcement of the missing account is gone, and its removal is part of the same
    // correction. It existed because the read was believed able to succeed carrying nothing, which produced
    // no failure record and therefore nothing for the refusal announcer above to say. A missing account is
    // now a `404`, so that announcer already words it — through the measured vocabulary and at the warning
    // severity the legacy guard sites used — and a second effect here would announce the same sentence twice
    // into a shared live region.

    this.destroyRef.onDestroy(() => this.handOverPendingWrite());
  }

  /**
   * Hands an outstanding create or update over to be reported after this screen has gone.
   *
   * ⚠ THE TWO WRITE BRIDGES ABOVE ARE EFFECTS IN THIS COMPONENT'S INJECTION CONTEXT, SO THEY DIE WITH
   * THIS COMPONENT. An operator who submits and then immediately goes somewhere else destroys the only
   * party that was going to tell them what happened: the store's request completes and the account really
   * is created or changed, and nothing says so. The identical defect was measured on the sibling role form
   * - `201`, never aborted, record created, no confirmation - and is fixed the same way on both, because
   * fixing one of two screens that share a mechanism is a trap for whoever meets the other.
   *
   * Registered ONLY for a write still outstanding. Each bridge lowers its own submitted marker as its
   * first act on settling, so a marker still raised at teardown proves that bridge has not fired and
   * cannot - which is what keeps exactly one party speaking for any one write.
   *
   * ⚠ THE CREATE PATH DELIBERATELY DOES NOT RELAY A GENERATED CREDENTIAL. Where one was generated, the
   * successful create hands it over ON this screen and defers its own redirect precisely so it can be
   * read; a credential cannot follow an operator to another screen, and this screen is gone. The account
   * still exists, so the creation is confirmed exactly as any other, and a credential nobody read is
   * recoverable the way any forgotten credential is. Confirming the create while the credential is lost is
   * strictly better than the previous behaviour, which lost both.
   *
   * The delete path is not relayed: it redirects to the return address, and an operator who has already
   * left has had the departure it was going to give them.
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

    // Named from the account the SERVER confirmed rather than from the form, for the reason the create
    // bridge records: the two can differ, and a confirmation naming something that was not stored is worse
    // than none. Evaluated at announce time, so the record has arrived by the time it is read.
    this.deferredOutcome.announceWhenSettled(verdict, () => {
      if (updated) {
        return USER_UPDATED_MESSAGE;
      }

      const stored = this.selectedUser();

      return stored === null ? null : USER_CREATED_MESSAGE.replace('{name}', stored.username);
    });
  }

  // COMMANDS — SUBMIT

  /**
   * Writes the form: creates while in create mode, updates while editing.
   *
   * This is the ONLY action on the screen that validates, mirroring the measured `causesvalidation="True"`
   * on `cmdUpdate`. Every other action — the removal and all four membership transitions — carries
   * `causesvalidation="False"` and must not trigger validation; see the note on each.
   *
   * MIGRATION: the legacy positional contracts are not reproduced. The request travels as a named object,
   * and the `ByRef` status out-parameters that the legacy used to smuggle an outcome back out of a call are
   * gone — thirty of them across the in-scope trees, four on the account controller alone. An outcome now
   * arrives as an HTTP status with a problem document carrying a failure-code STRING.
   *
   * MIGRATION: success is keyed on the HTTP status the transport reports — created for a create, ok for an
   * update — and NEVER on a numeric outcome value. No contract on this boundary carries one. That matters
   * because the legacy vocabularies disagree about which value means success: account creation succeeds at
   * THIRTEEN ({@link UserCreateStatus.Success}), whose zero member ({@link UserCreateStatus.AddUser}) is not
   * an outcome at all but the initial no-error-recorded-yet marker, while the sign-in vocabulary succeeds at
   * one and the password vocabulary at zero. An assumption that zero means success would be wrong two times
   * in three, so no such assumption is made anywhere on this screen.
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
   * Brings the password rule and its box into view when the rule is the reason a submit was refused
   * and nothing else on the form can answer for it — #8.
   *
   * ⚠ THIS EXISTS BECAUSE THE PASSWORD RULE IS A GROUP RULE, AND A GROUP RULE HAS NO CONTROL.
   * {@link passwordRulesValidator} runs on the whole form and reports its failure through
   * `form.errors`, deliberately, because the rule spans two boxes and neither of them is individually
   * wrong — a matching pair of weak passwords is two perfectly valid values in an invalid
   * combination. The consequence is that neither box carries Angular's invalid class, so the shared
   * focus directive's invalid-control query matches NOTHING and it returns without acting. Measured
   * result before this method existed: pressing Create on a form whose only fault was the password
   * rule left focus on the submit button, and the submit button on this screen sits below the fold at
   * most viewport widths — so the message appeared off-screen above a person who had scrolled down to
   * press it, and the form read as silently unsubmittable. For a screen-reader user there was no
   * route at all from "something is wrong" to which control to correct.
   *
   * ⚠ IT ACTS ONLY WHERE THE DIRECTIVE HAS PROVABLY DECLINED, which is what keeps the two from
   * fighting over one submit. The guard is the directive's own query: if any control in this screen's
   * subtree carries the invalid class, the directive will focus the first of them and this method
   * returns untouched — whichever order the two listeners happen to run in, because the two
   * conditions are mutually exclusive rather than merely ordered.
   *
   * ⚠ THE SECTION IS EXPANDED FIRST, AND THAT IS NOT A CONVENIENCE. The password block is behind a
   * disclosure, and the rule can be violated while the disclosure is CLOSED — in which case the box
   * does not exist in the document, focus has nowhere to go, and an operator is shown an assertive
   * message about a field they cannot see. Expanding is what makes the message actionable. Nothing is
   * collapsed by this method: a section already open stays open.
   *
   * Expanding the section is what puts the box in the document, so the view is rendered before the box
   * is looked up — see {@link changeDetector} for the measurement that forced this to be synchronous
   * rather than deferred. Reading the document through the injected host element rather than through a
   * global keeps this testable in a component harness.
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

      // ⚠ NOT OPTIONAL AND NOT A TIDINESS CALL. `passwordExpanded` gates an `@if` in this view's own
      // template, and the box it contains does not exist until that `@if` has been rendered. Checking
      // this view here is what makes the next statement able to find it; deferring instead is what the
      // measurement in `changeDetector` records as never focusing anything at all.
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

  /**
   * Opens the removal confirmation.
   *
   * Does NOT validate. `cmdDelete` carries `causesvalidation="False"`, so it neither marks controls as
   * touched, nor records a submission attempt, nor consults the form's validity. Removing an account has
   * nothing to do with whether its name is well formed.
   *
   * Every removal goes through the shared confirmation dialog, which supplies the focus trap and the
   * dismissal key.
   */
  onDeleteRequested(): void {
    this.deleteDialogOpen.set(false);

    if (!this.canDelete()) {
      return;
    }

    this.deleteDialogOpen.set(true);
  }

  /**
   * Removes the account.
   *
   * MIGRATION: removal is PER ACCOUNT. The legacy carried an unbounded bulk deletion that destroyed every
   * unauthorised account from a single click with no per-row confirmation, and it is not carried forward
   * anywhere.
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

  /**
   * Dismisses the removal confirmation without removing anything.
   */
  onDeleteCancelled(): void {
    this.deleteDialogOpen.set(false);
  }

  // Commands — the four membership actions
  //
  //  All four are measured with `causesvalidation="False"`, so none of them validates, marks controls as
  //  touched, records a submission attempt or is gated on the form's validity. Reproducing that exactly is
  //  the point: authorising an account has nothing to do with whether its display name is filled in.
  //
  //  The store already does exactly that: each of its three transition commands reconciles the selected
  //  account after the response. The re-read is therefore DELEGATED and deliberately NOT repeated here;
  //  issuing a second one would double the requests and race the first.
  //
  //  MIGRATION: the two verb-shaped routes the plan anticipated for authorising and withdrawing authorisation
  //  do not exist. The transport exposes ONE state-setting operation taking the desired state, because
  //  setting a state an account already holds is reported as a conflict — an answer that is only meaningful
  //  if the caller said which state it meant. Both actions below therefore call the same command with
  //  opposite arguments, and false is transmitted as DATA rather than treated as an absence.
  //
  //  MIGRATION: all four refuse to overlap, and that is parity rather than caution. Each was a postback that
  //  replaced the whole page, so a second could not be raised while the first was in flight. Here they are
  //  four ordinary controls on a live document, none of which validates or is disabled, so two can be pressed
  //  in succession — and the outcome the store reports carries no argument distinguishing them, which is
  //  precisely why the wording travels on the marker. Allowing an overlap would let the second action's
  //  marker replace the first's, so the first action's outcome would be announced with the second's wording
  //  and one of the two would be reported as something it was not.

  /**
   * Authorises the account. `cmdAuthorize`, labelled `Authorize User`.
   *
   * MIGRATION: the legacy ALSO sent mail here — `ManageUsers.ascx.vb` calls `Mail.SendMail(User,
   * MessageType.UserRegistrationPublic, PortalSettings)`. There is no mail endpoint, so that half is a
   * documented functional reduction and is stated to the operator rather than dropped in silence. Neither
   * sibling action sent mail.
   */
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

  /**
   * Withdraws authorisation. `cmdUnAuthorize`, labelled `UnAuthorize User`.
   */
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

  /**
   * Releases a locked-out account. `cmdUnLock`, labelled `Unlock Account`.
   *
   * The success wording is AUTHORED rather than measured — see the note on {@link USER_UNLOCKED_MESSAGE} for
   * the missing resource entry (DEFECT 5).
   */
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

  /**
   * Obliges the account to change its password. `cmdPassword`, labelled `Force Password Change`.
   *
   * Sets the obligation only. It does not choose, generate, transmit or return a password: the credential
   * endpoint belongs to the sibling password screen, and no field on this screen is wired to it.
   */
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
  //
  //  MIGRATION: the legacy collapsible section head is a control with no counterpart in the closed shared
  //  component library, and none is added. The template expresses each section as a native grouping element
  //  with a keyboard-reachable control carrying the expanded state, styled from the shared partials. These
  //  three toggles and their three signals are the whole of this component's part in that.
  //
  //  DEFECT 10, annotated and NOT fixed: `User.ascx` registers the section-head control and the file never
  //  uses it — verified, zero usages in the markup. The registration is dead and is not carried forward.

  /**
   * Expands or collapses the credentials section.
   */
  toggleCredentials(): void {
    this.credentialsExpanded.update((expanded) => !expanded);
  }

  /**
   * Expands or collapses the password section.
   */
  togglePassword(): void {
    this.passwordExpanded.update((expanded) => !expanded);
  }

  /**
   * Expands or collapses the new-account options section.
   */
  toggleNewAccountOptions(): void {
    this.newAccountOptionsExpanded.update((expanded) => !expanded);
  }

  /**
   * Expands or collapses the membership section.
   */
  toggleMembership(): void {
    this.membershipExpanded.update((expanded) => !expanded);
  }

  /**
   * Takes the revealed credential off the screen and completes the redirect.
   *
   * The redirect lives here because it was deferred, not because dismissal navigates by nature. A confirmed
   * creation redirects to the account listing; when a credential has to be handed over, that redirect waits
   * until the operator says they have taken it, so the two steps together are exactly the one step the
   * screen made before — with the credential disclosed in between.
   *
   * The value is dropped before the navigation is requested, so nothing carries it onwards.
   */
  dismissCredential(): void {
    this._revealedCredential.set(null);

    // Replaced, not pushed. This is the tail of a CREATE - the panel exists only because an account
    // was just made and its generated credential had to be handed over - so returning here would
    // offer a form for a record that now exists, and would do so with the credential already gone.
    void this.router.navigate(['/users'], { replaceUrl: true });
  }

  // TEMPLATE HELPERS

  /**
   * The message to show beside one control, or the empty string when there is none.
   *
   * Two sources, in order. A client-side validator message is preferred, because it is the one the operator
   * can act on immediately. Failing that, a per-field message the server reported is used, matched
   * case-insensitively — the server's model-state keys are not camel-cased, so `Email` and `email` name the
   * same control.
   *
   * MIGRATION: the returned text is plain and is stripped of a LEADING break tag. `User.ascx.vb` prepends
   * one explicitly — `valPassword.ErrorMessage = "<br/>" + UserController.GetUserCreateStatus(createStatus)`
   * — and the same habit appears in both spellings elsewhere in the tree, on twenty-eight of thirty-four
   * measured messages. DEFECT 4, annotated and NOT fixed at source. The stripping is DELEGATED to the shared
   * helper that owns it; it is not re-implemented here and it is not defeated.
   *
   * Never returns null or undefined, so the template cannot render either word.
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

  /**
   * The password section's message, which belongs to the GROUP rather than to a control.
   *
   * The legacy equivalent is `valPassword`, the single custom validator on the screen, declared with no
   * control to validate precisely because the rule spans two inputs.
   *
   * ⚠ THE GATE TRACKS THE PASSWORD SECTION, NOT THE WHOLE FORM, AND THAT DISTINCTION IS THE FIX.
   * This previously read `!this.submitAttempted() && this.form.pristine`, which is FORM-level: typing a
   * single character into User Name made the form dirty, opened this gate, and raised an assertive
   * "the password specified is invalid" alert over two password boxes nobody had been near. Measured at
   * runtime, both controls reported `ng-untouched ng-pristine ng-valid` while this method was returning a
   * message and the template was stamping `aria-invalid="true"` on them from it — the form was announcing a
   * failure its own validators did not agree with, on fields the operator had not reached.
   *
   * Two reasons the section-level gate is the correct one:
   *
   * - IT MATCHES THE DISCIPLINE ALREADY USED FOR EVERY OTHER MESSAGE ON THIS SCREEN. {@link messageFor}
   *   gates on `control.touched || submitAttempted()`, so a field speaks only once its own control has been
   *   left or the form has been submitted. A group rule is the same contract widened to the controls the
   *   rule spans, which is what this now reads.
   * - IT MATCHES THE LEGACY. `valPassword` is an `asp:CustomValidator` declared with no
   *   `ControlToValidate`, and such a validator evaluates on POSTBACK. The legacy screen therefore showed
   *   this message on submit and at no other time; the original intent recorded beside the template — that
   *   a form nobody has typed into is never marked as failing — is preserved, just scoped to the section
   *   the rule belongs to instead of to every control on the screen.
   *
   * The converse case was measured too and is deliberately unchanged: clearing User Name leaves it
   * `ng-dirty ng-invalid` with no message while focus is still inside it, because `touched` is set on blur.
   * Refusing to interrupt someone mid-edit is the intended behaviour, not an inverted one.
   *
   * @returns A plain-text message, or the empty string.
   */
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
   * Reads the companion flag the group validator sets, so the template can mark the control as missing while
   * still showing the measured invalid-password wording.
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
   * Builds and issues the creation request.
   *
   * MIGRATION: no value is trimmed, upper-cased or otherwise normalised on its way out. The legacy submitted
   * exactly what was typed, and silently transforming a value would be a data change disguised as tidiness.
   * It matters most for the password, where a trim would alter the credential itself.
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

    // Held, not announced, and not yet shown. The credential is disclosed only once the server has confirmed
    // that the account exists; the settled-outcome effect below moves it onto the screen or discards it.
    // Announcing here would describe an account that may never have been created.
    this.heldCredential = generate ? password : null;

    this.createSubmitted.set(true);
    this.store.createUser(request);
  }

  /**
   * Builds and issues the update request.
   *
   * MIGRATION: the update contract is deliberately narrow — the given name, the family name, the display
   * name and the address, and nothing else. It carries no sign-in name, because the legacy marked that field
   * read-only and offered no rename. It carries no authorisation flag, no lockout flag and no credential,
   * because each of those is changed through its own endpoint, which is what stops a routine details edit
   * from silently carrying an authorisation change.
   *
   * MIGRATION: a pristine form issues NO request, reproducing `User.ascx.vb`, which guards the write on
   * `UserEditor.IsDirty`.
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
   * Copies a loaded account into the form.
   *
   * Only the four editable members plus the read-only sign-in name are written. The create-only controls are
   * left at their initial values because they are disabled while editing and their values are never read in
   * that mode.
   *
   * MIGRATION: the display name is written as it ARRIVES, including the empty string. Its column is declared
   * not-null with an empty-string default, so `''` is the schema's own absent-marker rather than a missing
   * value, and substituting anything for it would put a value into the form that the server never sent.
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
   * Enables exactly the controls the current mode and state allow.
   *
   * MIGRATION: the enabled set is the measured visible set. `pnlAddUser` is declared `visible="False"` and
   * shown only while adding, and it holds the authorise box, the notify box, the generate box and both
   * password inputs — so all five are CREATE-ONLY. The sign-in name is read-only while editing, because
   * there is no rename path. A refusal disables everything, which is the measured `DisableForm()` behaviour.
   *
   * Disabling rather than merely hiding is deliberate: a disabled control contributes nothing to the group's
   * validity and cannot be dirtied, so a control the mode does not use cannot block a submission or
   * manufacture an unsaveable edit. `getRawValue()` still reports every control, disabled or not, which is
   * why the request builders read from it rather than from the partial value.
   *
   * Idempotent, so an effect may run it as often as it likes. `emitEvent: false` keeps a mode change from
   * being mistaken for an edit.
   *
   * @param editing Whether the screen is editing. Read from the signal when omitted.
   * @param withheld Whether a refusal has withheld the form. Read from the signal when omitted.
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
    // never act — and `enable()` on the group re-enables every descendant indiscriminately. Without this,
    // the create route handed the operator a tickable box that promises an e-mail the installation cannot
    // send. It is the one control whose availability is not a function of the mode. See {@link
    // NOTIFY_UNAVAILABLE_ADVISORY}.
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
   * The message a client-side validator attached to one control.
   *
   * The validators on this screen store their wording as the error VALUE, so the measured sentence travels
   * with the failure instead of being looked up by key at the point of display. The required validator is
   * Angular's own and stores `true`, so its wording is selected from the measured table below.
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

    // The column bound. Composed from the length the framework REPORTS rather than from a table looked up by
    // control, so the sentence and the rule can never name different numbers. The payload is narrowed rather
    // than trusted, because a shape change must not put `undefined` into a sentence.
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
   * Five entries, each the VALUE of a `UserInfo_*.Required` resource entry. A control outside the five
   * returns null rather than a composed sentence: inventing wording is how a migration acquires text no
   * operator has ever seen.
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
   * A per-field message the server reported for one control.
   *
   * Matched case-insensitively because the server's model-state keys are not camel-cased. The store already
   * exposes these as plain, break-tag-normalised strings in the order the document listed them, so nothing
   * is parsed here.
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
   * MIGRATION: this is where the measured severity vocabulary is honoured. A warning — which is what the
   * shared summariser returns for a refusal, a not-found and a rate-limit — goes to the transient channel,
   * matching the `YellowWarning` type the legacy used at every guard site. An error goes to the banner
   * instead, and reaches this method only when it arrived WITHOUT a problem document, in which case the
   * banner has nothing to render and staying silent would leave a failed action unreported.
   *
   * A refusal is worded from the measured vocabulary where the failure code or the status identifies one, so
   * an operator sees the sentence the legacy showed rather than a generic one.
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
      // The banner owns errors. It can only render one when a document arrived - so this branch is
      // reached only by a TRANSPORT failure, which never carried a problem document and therefore has
      // no correlation identifier to quote. The omission here is the absence of a reference, not the
      // discarding of one; the branch below is the one that had an identifier and dropped it.
      if (this.problem() === null) {
        this.notifications.error(this.wordFailure(message, code));
      }

      return;
    }

    const channel: NotificationSeverity = severity === 'warning' ? 'warning' : 'info';

    // ⚠ THE REFERENCE TRAVELS WITH THE REFUSAL, and this is the channel that carries refusals. The
    // shared classifier resolves 401, 403, 404 and 429 to WARNING, so every server refusal this screen
    // reports arrives HERE rather than in the error branch above - and each one carried a correlation
    // identifier in its problem document. This notification is also the whole report for them, because the
    // banner deliberately keeps only error-severity failures to avoid saying the same thing twice, so
    // dropping the identifier here left a refused save with no quotable reference anywhere on the screen.
    this.notifications.notify(channel, this.wordFailure(message, code), reference);
  }

  /**
   * Selects the measured wording for a failure, falling back to the server's own.
   *
   * MIGRATION: keyed on the failure code STRING and never on a numeric ordinal, because the legacy ordinals
   * disagree with one another and none of them crosses this boundary anyway.
   *
   * MIGRATION: the account-creation vocabulary is worded by the shared helper that owns it, not by a second
   * table here. That helper is a faithful port of `UserController.GetUserCreateStatus`, including the fact
   * that three distinct name outcomes share one sentence and four distinct provider faults share another.
   * Duplicating the table in this file would create a second copy free to drift from the one every other
   * screen uses, which is the exact failure a shared table exists to prevent.
   *
   * MIGRATION: the legacy translator ended in a `Case Else` that THREW `ArgumentException`, reachable by
   * {@link UserCreateStatus.AddUser}, {@link UserCreateStatus.Success} and {@link
   * UserCreateStatus.AddUserToPortal}. This one is total and never throws: an unrecognised code falls
   * through to the server's own sentence. A display adapter that throws on an input it does not recognise
   * turns a cosmetic gap into a blank screen, and a user interface must not throw. The divergence is
   * deliberate.
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
   * The measured refusal wording this screen can offer, exposed for the template.
   *
   * MIGRATION: the four guard outcomes the legacy worded are recorded as constants and are shown only when
   * the SERVER refuses, because the server owns every one of the decisions behind them. The state-conflict
   * status is included because both write paths can answer with it: a duplicate name, and a duplicate
   * address on a tenant that requires uniqueness.
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
