/** Security-role creation and editing. ONE component serves BOTH routes. */
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { Signal, WritableSignal } from '@angular/core';
import { ListReturnStore } from '../../../core/state/list-return.store';
import {
  ROLE_LIST_GROUP_PARAM,
  ROLE_LIST_ROUTE,
} from '../../../core/config/app-routes.config';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { type Params, Router, RouterLink } from '@angular/router';
// `problemDetailsMessage` is deliberately NOT imported here any more: this screen no longer resolves a
// document's sentence for itself. The banner does it, from the document and the fallback it is given — see
// `reportFailure`.
import { problemDetailsFieldErrors } from '../../../core/models/problem-details.model';
import {
  failureCode,
  missingEntityMessage,
  missingEntityProblem,
  missingEntityRecoveryLabel,
} from '../../../core/utils/form-errors.util';
import {
  containedIconPathValidator,
  ICON_NOT_CONTAINED_ERROR,
  ICON_NOT_CONTAINED_MESSAGE,
} from '../../../core/utils/icon-reference.util';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import { BILLING_FREQUENCY_NAMES } from '../../../core/models/role.model';
import type {
  BillingFrequency,
  CreateRoleRequest,
  Role,
  RoleGroup,
  StoredBillingFrequency,
  UpdateRoleRequest,
} from '../../../core/models/role.model';
import { DeferredOutcomeService } from '../../../core/services/deferred-outcome.service';
import type { DeferredOutcome } from '../../../core/services/deferred-outcome.service';
import { NotificationService, type NotificationSeverity } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { RoleStore } from '../../../core/state/role.store';
import type { RoleStoreFailure, RoleStoreOperation } from '../../../core/state/role.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import {
  LoadingSpinnerComponent,
} from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import {
  FocusFirstInvalidDirective,
  INVALID_CONTROL_SELECTOR,
} from '../../../shared/directives/focus-first-invalid.directive';
import { BlockImplicitSubmitDirective } from '../../../shared/directives/block-implicit-submit.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { requiredText } from '../../../core/utils/required-text.validator';
import { parseRouteId } from '../../../core/utils/route-id.util';

// ---------------------------------------------------------------------------
// FORM SHAPE
// ---------------------------------------------------------------------------

export interface RoleFormModel {
  /** `txtRoleName` / `lblRoleName` — `editroles.ascx:L27-L29`. */
  roleName: FormControl<string>;
  /** `txtDescription` — `editroles.ascx:L39-L40`. */
  description: FormControl<string>;
  /** `cboRoleGroups` — `editroles.ascx:L47`. */
  roleGroupId: FormControl<number | null>;
  /** `chkIsPublic` — `editroles.ascx:L56`. */
  isPublic: FormControl<boolean>;
  /** `chkAutoAssignment` — `editroles.ascx:L64`. */
  autoAssignment: FormControl<boolean>;
  /** `txtServiceFee` — `editroles.ascx:L89-L90`. */
  serviceFee: FormControl<string>;
  /** `txtBillingPeriod` — `editroles.ascx:L104-L105`. */
  billingPeriod: FormControl<string>;
  /** `cboBillingFrequency` — `editroles.ascx:L106-L107`. */
  billingFrequency: FormControl<BillingFrequency>;
  /** `txtTrialFee` — `editroles.ascx:L122-L123`. */
  trialFee: FormControl<string>;
  /** `txtTrialPeriod` — `editroles.ascx:L136-L137`. */
  trialPeriod: FormControl<string>;
  /** `cboTrialFrequency` — `editroles.ascx:L138-L139`. */
  trialFrequency: FormControl<BillingFrequency>;
  /** `txtRSVPCode` — `editroles.ascx:L153`. */
  rsvpCode: FormControl<string>;
  /** `ctlIcon` — `editroles.ascx:L169-L170`, reduced to a plain path field. */
  iconFile: FormControl<string>;
}

/** One entry in a `select`, holding the persisted value and the caption shown for it. */
export interface RoleFormOption<TValue> {
  readonly value: TValue;
  readonly label: string;
}

// ---------------------------------------------------------------------------
// PERSISTED VOCABULARY
// ---------------------------------------------------------------------------

/**
 * The six billing-frequency codes, in the order the legacy `CodeFrequency` lookup seeded them. These are
 * load-bearing persisted data, not presentation.
 */
export const BILLING_FREQUENCY_OPTIONS: readonly RoleFormOption<BillingFrequency>[] =
  Object.freeze<readonly RoleFormOption<BillingFrequency>[]>([
    { value: 'N', label: BILLING_FREQUENCY_NAMES['N'] ?? 'None' },
    { value: 'O', label: BILLING_FREQUENCY_NAMES['O'] ?? 'One Time' },
    { value: 'D', label: BILLING_FREQUENCY_NAMES['D'] ?? 'Day' },
    { value: 'W', label: BILLING_FREQUENCY_NAMES['W'] ?? 'Week' },
    { value: 'M', label: BILLING_FREQUENCY_NAMES['M'] ?? 'Month' },
    { value: 'Y', label: BILLING_FREQUENCY_NAMES['Y'] ?? 'Year' },
  ]);

/**
 * The frequency that means "no recurring term". `EditRoles.ascx.vb:L119` and `:L125` both select `"N"` on
 * first load, and `:L214` and `:L224` both initialise the outgoing frequency to `"N"`.
 */
export const NO_FREQUENCY: BillingFrequency = 'N';

export const GLOBAL_ROLES_LABEL = '< Global Roles >';

/** The value bound to {@link GLOBAL_ROLES_LABEL}. `null`, NOT `-1`. */
export const UNGROUPED_ROLE_GROUP: number | null = null;

/**
 * The legacy ungrouped marker, recognised on the way IN only. A response that still carries `-1` — from a
 * producer that has not collapsed the sentinel — selects `< Global Roles >` just as `null` does, because
 * the two denote the same state.
 */
const LEGACY_UNGROUPED_ROLE_GROUP = -1;

/**
 * `Null.NullInteger`, which a period column uses to mean "absent".
 * `Library/Components/Shared/Null.vb:L41-L45`.
 */
const LEGACY_ABSENT_PERIOD = -1;

/**
 * `Null.NullSingle`, which a money column uses to mean "absent". `Library/Components/Shared/Null.vb`
 * returns `Single.MinValue`.
 */
const LEGACY_ABSENT_MONEY_THRESHOLD = -3.4e38;

// ---------------------------------------------------------------------------
// LENGTH LIMITS
// ---------------------------------------------------------------------------

/** `txtRoleName MaxLength="50"` — `editroles.ascx:L27`; `dbo.Roles.RoleName nvarchar(50) NOT NULL`. */
const ROLE_NAME_MAX_LENGTH = 50;

/** `txtDescription MaxLength="1000"` — `editroles.ascx:L39`; `dbo.Roles.Description nvarchar(1000)`. */
const DESCRIPTION_MAX_LENGTH = 1000;

/** `txtRSVPCode MaxLength="50"` — `editroles.ascx:L153`. */
const RSVP_CODE_MAX_LENGTH = 50;

/**
 * Shortest invitation code an AUTHORED or ROTATED value may be. Mirrors
 * `RoleTermsRules.RsvpCodeMinimumAuthoredLength`.
 */
const RSVP_CODE_MIN_AUTHORED_LENGTH = 12;

/**
 * Reports whether a code is strong enough to be AUTHORED, accepting an absent or empty value.
 *
 * ⚠ A FAITHFUL MIRROR OF `RoleTermsRules.IsStrongAuthoredRsvpCode`, NOT AN APPROXIMATION OF IT. The two
 * must agree exactly or the screen refuses a value the server would take, or takes one it would refuse.
 * `\p{L}` and `\p{Nd}` are used rather than `[A-Za-z]` and `[0-9]` because the server's `char.IsLetter` and
 * `char.IsDigit` are Unicode-aware and classify by category, so an accented letter or a non-Latin digit
 * counts there and has to count here.
 */
function isStrongAuthoredRsvpCode(code: string | null | undefined): boolean {
  if (code === null || code === undefined || code === '') {
    return true;
  }

  if (code.length < RSVP_CODE_MIN_AUTHORED_LENGTH) {
    return false;
  }

  // Three classes, of which at least two must appear. Anything that is neither a letter nor a digit -
  // punctuation, a symbol, a separator - is the third class, exactly as the server's `else` branch treats it.
  const hasLetter = /\p{L}/u.test(code);
  const hasDigit = /\p{Nd}/u.test(code);
  const hasOther = /[^\p{L}\p{Nd}]/u.test(code);

  return Number(hasLetter) + Number(hasDigit) + Number(hasOther) >= 2;
}

/**
 * Builds the invitation-code strength rule, held against the value being WRITTEN rather than against every
 * submission.
 *
 * ⚠ THE GRANDFATHERING IS THE POINT OF THIS FACTORY, and dropping it reintroduces the trap the server fix
 * removed: a role carrying a code from before the strength rule would become un-editable, because amending
 * its description posts the stored code back and a rule held against every submission refuses it. The only
 * escape would be rotating the code, which invalidates it for every member holding it. So the rule fires
 * only once the submitted value DIFFERS from the stored one, which is precisely when something is authored.
 *
 * The comparison is case-SENSITIVE, matching the server's `StringComparison.Ordinal`: the value is a shared
 * secret rather than a name, so altering only its casing produces a different secret and IS authoring.
 *
 * @param storedCode Reads the code currently held against the role, or `null` on the creation route where
 * nothing is stored and therefore every non-empty value is authored.
 * @returns A validator reporting `rsvpCodeTooWeak` when a newly authored code is too easily guessed.
 */
function authoredRsvpCodeValidator(storedCode: () => string | null): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const submitted = typeof control.value === 'string' ? control.value : '';
    const stored = storedCode() ?? '';

    if (submitted === stored) {
      return null;
    }

    return isStrongAuthoredRsvpCode(submitted) ? null : { rsvpCodeTooWeak: true };
  };
}

/** The icon path limit. */
const ICON_FILE_MAX_LENGTH = 100;

// ---------------------------------------------------------------------------
// MESSAGES — the wording a user actually saw
// ---------------------------------------------------------------------------

const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a role that can be read. Return to the role list and try again.';

/** `valRoleName.Text`, `<br>` stripped. */
const ROLE_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/**
 * Verbatim mirror of `RoleTermsRules.RsvpCodeTooWeakMessage`. Restated rather than derived because the two
 * codebases share no wording source, and a caller must read the SAME sentence whichever side refuses.
 */
const RSVP_CODE_TOO_WEAK_MESSAGE =
  'An RSVP Code must be at least 12 characters long and must mix letters with digits or punctuation.';

/** `valServiceFee1.Text`, `<br>` stripped. */
const SERVICE_FEE_INVALID_MESSAGE = 'Service Fee Value Entered Is Not Valid';

/** `valServiceFee2.Text`, `<br>` stripped. */
const SERVICE_FEE_NEGATIVE_MESSAGE = 'Service Fee Must Be Greater Than or Equal to Zero';

/** `valBillingPeriod1.Text`, `<br>` stripped. */
const BILLING_PERIOD_INVALID_MESSAGE = 'Billing Period Value Entered Is Not Valid';

/** `valBillingPeriod2.Text`, `<br>` stripped. */
const BILLING_PERIOD_NOT_POSITIVE_MESSAGE = 'Billing Period Must Be Greater Than Zero';

/** `valTrialFee1.Text`, `<br>` stripped. */
const TRIAL_FEE_INVALID_MESSAGE = 'Trial Fee Value Entered Is Not Valid';

/** `valTrialFee2.Text`, `<br>` stripped. */
const TRIAL_FEE_NEGATIVE_MESSAGE = 'Trial Fee Must Be Greater Than or Equal to Zero';

/** `valTrialPeriod1.Text`, `<br>` stripped. */
const TRIAL_PERIOD_INVALID_MESSAGE = 'Trial Period Value Entered Is Not Valid';

/** `valTrialPeriod2.Text`, `<br>` stripped. */
const TRIAL_PERIOD_NOT_POSITIVE_MESSAGE = 'Trial Period Must Be Greater Than Zero';



/**
 * Reported when an amount falls outside what the `money` column can hold. MIGRATION: NET-NEW WORDING —
 * the legacy screen had no sentence for this because it had no rule.
 */
const AMOUNT_OUT_OF_RANGE_MESSAGE =
  'That amount is outside the range this site can store. Enter an amount between ' +
  '-922,337,203,685,477.58 and 922,337,203,685,477.58.';

/**
 * Reported when an amount would reach the column with different digits from the ones typed. MIGRATION:
 * NET-NEW WORDING, and net-new protection.
 */
const AMOUNT_LOSES_PRECISION_MESSAGE =
  'That amount has more digits than can be stored without rounding. Enter a shorter amount.';

/**
 * Reported when a period falls outside what the `int` column can hold. MIGRATION: NET-NEW WORDING.
 * Without it the submission reached the API, failed to bind, and came back as `"request": ["The request
 * field is required."]` — a message that names no field, points at nothing the person typed, and reads as
 * though the whole request were missing.
 */
const PERIOD_OUT_OF_RANGE_MESSAGE =
  'That number is outside the range this site can store. Enter a whole number between ' +
  '-2,147,483,648 and 2,147,483,647.';

/**
 * `DuplicateRole.Text`, verbatim. `EditRoles.ascx.vb:L256` raised it at `ModuleMessageType.RedError`
 * after its own lookup-then-insert check at `:L252` found a name collision.
 */
const DUPLICATE_ROLE_MESSAGE = 'A role with the same name already exists. The role was not added.';

/** The failure code the API publishes when the role changed between this screen's read and its write. */
const CONCURRENCY_CONFLICT_CODE = 'role.concurrency_conflict';

/**
 * Shown when a save is refused because someone else changed the role first. MIGRATION: NET-NEW, because
 * the situation itself is net-new — the legacy had no conflict to report.
 */
const CONCURRENCY_CONFLICT_MESSAGE =
  'This role was changed by someone else after you opened it, so nothing was saved.';

/**
 * The recovery sentence beside the reload command. ⚠ THE COST OF RELOADING IS STATED, not glossed.
 * Re-reading the role replaces every value on screen with the stored one, so unsaved edits are lost — and
 * a person needs to know that BEFORE pressing the button, not after.
 */
const CONCURRENCY_RECOVERY_MESSAGE =
  'Read the role again to see the stored values, then apply your change to them. ' +
  'Anything you have typed here and not saved will be replaced.';

/** Caption of the command that re-reads a role after a conflict. */
const CONCURRENCY_RELOAD_LABEL = 'Read this role again';

const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `AccessDenied.Text`, verbatim, from the sibling access-denied screen's resources. */
const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';

/** `ControlTitle_edit.Text`, verbatim. */
const EDIT_TITLE = 'Edit Security Roles';

/**
 * The create-mode heading. `EditRoles.ascx.resx` has NO `ControlTitle_add` key, so the legacy add form
 * showed the same "Edit Security Roles" heading as the edit form.
 */
const ADD_TITLE = 'Add New Role';

/**
 * The primary action's wording, one word per mode.
 *
 * ⚠ #22 — `EditRoles.ascx` declared a single `cmdUpdate` for both modes, so "Update" is the documented
 * legacy wording for EDIT and there is no legacy wording for create at all. Applying "Update" to a role that
 * does not exist yet is what made the four create screens read as three different vocabularies; the portal
 * and user forms already name their subject on create, and this follows them.
 */
const EDIT_SUBMIT_LABEL = 'Update';

const CREATE_SUBMIT_LABEL = 'Create Role';

/** Confirmation after a successful create. Legacy severity `GreenSuccess`. */
const ROLE_CREATED_MESSAGE = 'The role was created.';

/** Confirmation after a successful update. Legacy severity `GreenSuccess`. */
const ROLE_UPDATED_MESSAGE = 'The role was updated.';

/** Confirmation after a successful delete. Legacy severity `GreenSuccess`. */
const ROLE_DELETED_MESSAGE = 'The role was deleted.';

/**
 * Shown when the requested role does not exist. `EditRoles.ascx.vb:L170-L172` treated this as "a security
 * violation attempt to access item not related to this Module" and redirected to the Security Roles page
 * without telling the user anything.
 */
const ROLE_NOT_FOUND_MESSAGE = missingEntityMessage('role');

/** Where a reader is sent once the addressed role has gone, and the only action offered there. */
const RECOVERY_LABEL = missingEntityRecoveryLabel('Security Roles');

/** Fallback when a refusal carries neither a `detail` nor a `title`. */
const SAVE_FAILED_MESSAGE = 'The role could not be saved.';

/** Fallback when a delete refusal carries neither a `detail` nor a `title`. */
const DELETE_FAILED_MESSAGE = 'The role could not be deleted.';

/** Fallback when the role could not be read. */
const LOAD_FAILED_MESSAGE = 'The role could not be loaded.';

/** Last-resort per-field message, matching the sibling screens' wording. */
const GENERIC_FIELD_MESSAGE = 'Correct this field and try again.';


// ---------------------------------------------------------------------------
// HTTP STATUS CODES THIS SCREEN DISTINGUISHES
// ---------------------------------------------------------------------------

/** The caller is not authenticated. */
const UNAUTHORIZED = 401;

/** The caller is authenticated but not permitted. Surfaced as a WARNING, never an error. */
const FORBIDDEN = 403;

/** The role does not exist. */
const NOT_FOUND = 404;

/** A name collision. The legacy detected this itself, before saving. */
const CONFLICT = 409;

/** The three store commands this screen issues and then waits on. */
type AwaitedRoleMutation = Extract<RoleStoreOperation, 'createRole' | 'updateRole' | 'deleteRole'>;

/** What each mutation announces when it succeeds. */
const MUTATION_SUCCESS_MESSAGE: Readonly<Record<AwaitedRoleMutation, string>> = Object.freeze({
  createRole: ROLE_CREATED_MESSAGE,
  updateRole: ROLE_UPDATED_MESSAGE,
  deleteRole: ROLE_DELETED_MESSAGE,
});

const MUTATION_FAILURE_MESSAGE: Readonly<Record<AwaitedRoleMutation, string>> = Object.freeze({
  createRole: SAVE_FAILED_MESSAGE,
  updateRole: SAVE_FAILED_MESSAGE,
  deleteRole: DELETE_FAILED_MESSAGE,
});

// ---------------------------------------------------------------------------
// VALIDATORS — every one of them empty-tolerant
// ---------------------------------------------------------------------------

// `editroles.ascx` places TWO `CompareValidator`s on each of the four money and period fields. The first of
// each pair declares `Type="Currency"` or `Type="Integer"` with `Operator="DataTypeCheck"`.

/** True when a control holds nothing a validator should judge. */
function isBlank(value: unknown): boolean {
  return typeof value !== 'string' || value.trim().length === 0;
}

const MONEY_PATTERN = /^[+-]?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?$/;

/** Recognises what `Type="Integer"` accepted: an optional sign and digits, with no separators. */
const WHOLE_NUMBER_PATTERN = /^[+-]?\d+$/;

/** Bounds of the `int` columns behind the two period fields. */
const PERIOD_MINIMUM = -2_147_483_648;

/** Upper bound of the `int` columns behind the two period fields. */
const PERIOD_MAXIMUM = 2_147_483_647;

/**
 * Bounds of the `money` columns behind the two fee fields. `Roles.ServiceFee` and `Roles.TrialFee` are
 * `money NULL`.
 */
const MONEY_MINIMUM = -922_337_203_685_477.5808;

/** Upper bound of the `money` columns behind the two fee fields. */
const MONEY_MAXIMUM = 922_337_203_685_477.5807;

/** Parses a money field, returning `null` when it holds nothing parseable. */
function parseMoney(raw: string): number | null {
  const text = raw.trim();
  if (text.length === 0 || !MONEY_PATTERN.test(text)) {
    return null;
  }
  const parsed = Number.parseFloat(text.replace(/,/g, ''));
  return Number.isFinite(parsed) ? parsed : null;
}

/** Parses a period field, returning `null` when it holds nothing parseable. */
function parseWholeNumber(raw: string): number | null {
  const text = raw.trim();
  if (text.length === 0 || !WHOLE_NUMBER_PATTERN.test(text)) {
    return null;
  }
  const parsed = Number.parseInt(text, 10);
  return Number.isInteger(parsed) ? parsed : null;
}

/**
 * Reduces a decimal string to the digits it actually states, so two spellings of one value compare equal
 * and two different values never do. Grouping separators, a leading `+`, leading zeros on the whole part
 * and trailing zeros on the fraction all carry no information and are removed.
 *
 * @param text A decimal string that has already satisfied {@link MONEY_PATTERN}.
 * @returns The canonical digits, with a sign only when the value is non-zero.
 */
function canonicalDecimal(text: string): string {
  const trimmed = text.trim();
  const negative = trimmed.startsWith('-');
  const unsigned = trimmed.replace(/^[+-]/, '').replace(/,/g, '');
  const separator = unsigned.indexOf('.');
  const whole = separator === -1 ? unsigned : unsigned.slice(0, separator);
  const fraction = separator === -1 ? '' : unsigned.slice(separator + 1);

  // A whole part of "000" must reduce to "0", not to nothing, so the lookahead keeps a final digit.
  const significantWhole = whole.replace(/^0+(?=\d)/, '');
  const significantFraction = fraction.replace(/0+$/, '');
  const digits =
    significantFraction.length === 0
      ? significantWhole
      : `${significantWhole}.${significantFraction}`;

  return negative && /[1-9]/.test(digits) ? `-${digits}` : digits;
}

/**
 * True when an amount reaches the `money` column holding every digit that was typed. Two distinct
 * failures are reported separately, because they call for different corrections: an amount OUTSIDE the
 * column's range needs a smaller one, whereas an amount inside the range that cannot be carried exactly
 * needs fewer digits.
 *
 * @param text The typed text, already known to satisfy {@link MONEY_PATTERN}.
 * @param parsed The value {@link parseMoney} produced from it.
 * @returns `'range'` or `'precision'` naming the failure, or `null` when the amount survives.
 */
function amountLoss(text: string, parsed: number): 'range' | 'precision' | null {
  if (parsed < MONEY_MINIMUM || parsed > MONEY_MAXIMUM) {
    return 'range';
  }
  return canonicalDecimal(text) === canonicalDecimal(parsed.toString()) ? null : 'precision';
}

/**
 * `Operator="DataTypeCheck"` with `Type="Currency"` — `valServiceFee1`, `valTrialFee1`.
 *
 * @param message The resource wording for this field.
 */
function currencyDataType(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    return parseMoney(String(raw)) === null ? { currencyDataType: message } : null;
  };
}

/**
 * `Operator="DataTypeCheck"` with `Type="Integer"` — `valBillingPeriod1`, `valTrialPeriod1`.
 *
 * @param message The resource wording for this field.
 */
function wholeNumberDataType(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    return parseWholeNumber(String(raw)) === null ? { wholeNumberDataType: message } : null;
  };
}

/**
 * `Operator="GreaterThanEqual" ValueToCompare="0"`, compared NUMERICALLY — `valServiceFee2`,
 * `valTrialFee2`. A value that will not parse yields `null` here, deferring to the data-type validator on
 * the same control so that exactly one message is shown rather than two contradictory ones.
 *
 * @param message The resource wording for this field.
 */
function moneyNotNegative(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    const parsed = parseMoney(String(raw));
    if (parsed === null) {
      return null;
    }
    return parsed < 0 ? { moneyNotNegative: message } : null;
  };
}

/**
 * `Operator="GreaterThan" ValueToCompare="0"`, compared NUMERICALLY — `valBillingPeriod2`,
 * `valTrialPeriod2`.
 *
 * @param message The resource wording for this field.
 */
function wholeNumberPositive(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    const parsed = parseWholeNumber(String(raw));
    if (parsed === null) {
      return null;
    }
    return parsed <= 0 ? { wholeNumberPositive: message } : null;
  };
}

function moneyStorable(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    const text = String(raw);
    const parsed = parseMoney(text);
    if (parsed === null) {
      return null;
    }
    const loss = amountLoss(text, parsed);
    if (loss === null) {
      return null;
    }
    return {
      moneyStorable:
        loss === 'range' ? AMOUNT_OUT_OF_RANGE_MESSAGE : AMOUNT_LOSES_PRECISION_MESSAGE,
    };
  };
}

/** Refuses a period the `int` column cannot hold. NET-NEW RULE with no legacy counterpart. */
function wholeNumberStorable(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    if (isBlank(raw)) {
      return null;
    }
    const parsed = parseWholeNumber(String(raw));
    if (parsed === null) {
      return null;
    }
    const storable =
      Number.isSafeInteger(parsed) && parsed >= PERIOD_MINIMUM && parsed <= PERIOD_MAXIMUM;
    return storable ? null : { wholeNumberStorable: PERIOD_OUT_OF_RANGE_MESSAGE };
  };
}

// ---------------------------------------------------------------------------
// DISPLAY AND SUBMISSION HELPERS
// ---------------------------------------------------------------------------

/**
 * Formats money the way this screen formatted it. two different money formats coexist across the two role
 * screens, and each is kept faithful to its own screen rather than harmonised.
 *
 * @param value The fee as the API reported it, possibly absent.
 * @returns The grouped, two-decimal text, or the empty string when the fee is absent.
 */
function formatMoney(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return '';
  }
  if (value <= LEGACY_ABSENT_MONEY_THRESHOLD) {
    return '';
  }
  return new Intl.NumberFormat('en-US', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
    useGrouping: true,
  }).format(value);
}

/**
 * Formats a period the way the legacy formatted it: `objRoleInfo.BillingPeriod.ToString` — a bare integer
 * with no separators. SENTINEL: an absent period renders as the EMPTY STRING, matching `FormatPeriod`,
 * which returns `Null.NullString` when the period is `Null.NullInteger`.
 *
 * @param value The period as the API reported it, possibly absent.
 * @returns The integer text, or the empty string when the period is absent.
 */
function formatPeriod(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    return '';
  }
  if (value === LEGACY_ABSENT_PERIOD) {
    return '';
  }
  return String(value);
}

/**
 * Narrows an incoming STORED frequency code to the closed write vocabulary, falling back to `'N'`.
 *
 * @param value The frequency as the API reported it, possibly absent, possibly a code outside the
 * supported six.
 * @returns A code that certainly exists among the options.
 */
function coerceFrequency(value: StoredBillingFrequency | null | undefined): BillingFrequency {
  if (value === null || value === undefined) {
    return NO_FREQUENCY;
  }

  const matched = BILLING_FREQUENCY_OPTIONS.find((option) => option.value === value);

  return matched === undefined ? NO_FREQUENCY : matched.value;
}

/**
 * The caption beside a frequency code, taken from the one list that owns those words.
 *
 * @param value A code from the closed write vocabulary.
 * @returns The caption the select shows for it.
 */
function frequencyCaption(value: BillingFrequency): string {
  return BILLING_FREQUENCY_OPTIONS.find((option) => option.value === value)?.label ?? '';
}

/**
 * Whether the legacy bind fills the BILLING group's three boxes from the record. `EditRoles.ascx.vb:L146`
 * gates all three billing controls on one test — `If Format(objRoleInfo.ServiceFee, "#,##0.00") <> "0.00"
 * Then` — so a fee that FORMATS to zero leaves the fee, the period and the frequency exactly as they
 * opened.
 *
 * @param role The role as the API reported it.
 * @returns True when the billing boxes are filled from the record.
 */
function isRolePriced(role: Role): boolean {
  const fee = role.serviceFee;
  if (typeof fee !== 'number' || !Number.isFinite(fee) || fee <= LEGACY_ABSENT_MONEY_THRESHOLD) {
    return false;
  }
  return fee !== 0;
}

/**
 * @param role The role as the API reported it.
 * @returns True when the trial boxes are filled from the record.
 */
function isRoleOnTrial(role: Role): boolean {
  return coerceFrequency(role.trialFrequency) !== NO_FREQUENCY;
}

/**
 * Whether a stored frequency holds a code this console cannot set.
 *
 * Absence is NOT unnameable: a column holding nothing loses nothing when the write records `'N'`, so only a
 * code that is present and outside the closed six qualifies.
 *
 * @param value The frequency exactly as the column holds it.
 * @returns True when a real code is stored that the select cannot represent.
 */
function isUnnameableFrequency(value: StoredBillingFrequency | null | undefined): boolean {
  if (value === null || value === undefined || String(value).trim() === '') {
    return false;
  }

  return coerceFrequency(value) === NO_FREQUENCY && value !== NO_FREQUENCY;
}

/**
 * How a stored frequency reads inside the withheld-values notice.
 *
 * A code the select offers reads as its caption, which is the word the operator would have seen in the box.
 * A code it does not offer reads as the raw character, because that character IS the stored value and the
 * caption for it does not exist. Absence reads as the empty string and is therefore never stated.
 *
 * @param value The frequency exactly as the column holds it.
 * @returns The text to state, or the empty string when there is nothing stored.
 */
function storedFrequencyText(value: StoredBillingFrequency | null | undefined): string {
  if (value === null || value === undefined || String(value).trim() === '') {
    return '';
  }

  if (isUnnameableFrequency(value)) {
    return String(value);
  }

  const named = coerceFrequency(value);

  // `'N'` means "no recurring term", so stating it as a value held would report an absence as a setting.
  return named === NO_FREQUENCY ? '' : frequencyCaption(named);
}

/**
 * Joins captioned values into the tail of one English sentence.
 *
 * @param parts The phrases, already in the order they should be read.
 * @returns `"a"`, `"a and b"`, or `"a, b and c"`.
 */
function joinPhrases(parts: readonly string[]): string {
  if (parts.length === 0) {
    return '';
  }
  const last = parts[parts.length - 1] ?? '';
  if (parts.length === 1) {
    return last;
  }
  return `${parts.slice(0, -1).join(', ')} and ${last}`;
}

/**
 * Narrows an incoming role-group id to a value the select can hold. Reproduces
 * `EditRoles.ascx.vb:L141-L144`: select the option whose value matches, and when no option matches leave
 * the select on `< Global Roles >`.
 *
 * @param value The group id as the API reported it.
 * @param groups The groups the select is currently offering.
 * @returns The id to select, or `null` for the ungrouped choice.
 */
function coerceRoleGroupId(
  value: number | null | undefined,
  groups: readonly RoleGroup[],
): number | null {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    return UNGROUPED_ROLE_GROUP;
  }
  if (value === LEGACY_UNGROUPED_ROLE_GROUP) {
    return UNGROUPED_ROLE_GROUP;
  }
  return groups.some((group) => group.roleGroupId === value) ? value : UNGROUPED_ROLE_GROUP;
}

/**
 * Trims a text control and reports the empty result as `null`.
 *
 * @param value The raw control text.
 * @returns The trimmed text, or `null` when nothing is left.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Renders a stored value for read-only display without ever printing `null` or `undefined`.
 *
 * @param value The value as the API reported it.
 * @returns The value, or the empty string when it is absent.
 */
function textOrEmpty(value: string | null | undefined): string {
  return typeof value === 'string' ? value : '';
}

/** The resolved outcome of the billing group, or of the trial group. */
interface ResolvedTerms {
  readonly fee: number;
  readonly period: number;
  readonly frequency: BillingFrequency;
}

const SUPPRESSED_TERMS: ResolvedTerms = Object.freeze({
  fee: 0,
  period: 1,
  frequency: NO_FREQUENCY,
});

/** A stored paid-membership value the legacy bind leaves out of its box, and the box it belongs to. */
interface WithheldTerm {
  /** The field the value belongs to, in that field's own words. */
  readonly caption: string;
  /** The value as the role LISTING renders it, so both screens read the record identically. */
  readonly value: string;
}

/** The answer when nothing is being withheld, shared so the signal's identity is stable. */
const NO_WITHHELD_TERMS: readonly WithheldTerm[] = Object.freeze<readonly WithheldTerm[]>([]);

/** The captions the withheld-terms notice names its values by. */
const WITHHELD_TERM_CAPTIONS = Object.freeze({
  serviceFee: 'Service Fee',
  billingPeriod: 'Billing Period',
  billingFrequency: 'Billing Frequency',
  trialFee: 'Trial Fee',
  trialPeriod: 'Trial Period',
  trialFrequency: 'Trial Frequency',
});

// ---------------------------------------------------------------------------
// COMPONENT
// ---------------------------------------------------------------------------

/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE: exactly one per screen, stating that screen's
 * SCOPE - the record it acts on when the title does not already name it, otherwise what the screen is for
 * in one line - and never a status, a count or a progress readout.
 */
const CREATE_SUBTITLE =
  'A role groups accounts together so permissions can be granted to all of them at once.';

@Component({
  selector: 'app-role-form',
  standalone: true,
  imports: [
    BlockImplicitSubmitDirective,
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './role-form.component.html',
  styleUrl: './role-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleFormComponent {
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
  // -------------------------------------------------------------------------
  // ROUTE INPUTS
  // -------------------------------------------------------------------------

  /**
   * The role being edited, or `undefined` on the creation route. The spelling is load-bearing and is
   * exactly `roleId`, with a single lower-case `d`.
   */
  public readonly roleId = input<string | undefined>(undefined);

  /**
   * The tenant's administrator role key, or `null` until its record resolves. `Roles.RoleID` is
   * `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L114`), so nought is a real role key and the
   * administrator role in the seeded tenant genuinely holds it.
   */
  protected readonly administratorRoleId: Signal<number | null> = computed(() =>
    this.portals.administratorRoleId(),
  );

  /**
   * The tenant's registered-users role key, or `null` until its record resolves. The stricter of the two:
   * it is protected from deletion and update AND its membership screen is unreachable, because every
   * authenticated user holds it.
   */
  protected readonly registeredRoleId: Signal<number | null> = computed(() =>
    this.portals.registeredRoleId(),
  );

  /** Whether the tenant has a configured payment processor. DEFECT 5, reproduced rather than repaired. */
  protected readonly paymentProcessorConfigured: Signal<boolean> = computed(() =>
    this.portals.paymentProcessorConfigured(),
  );

  /** Whether the tenant's record has arrived, which is what arms the guards above. */
  protected readonly protectedFactsResolved: Signal<boolean> = computed(() =>
    this.portals.contextResolved(),
  );

  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------

  /**
   * The shared role state, and THE ONLY ROUTE TO THE API FROM THIS SCREEN. ⚠ THE TRANSPORT IS
   * DELIBERATELY NOT INJECTED. Every read and every write this screen performs goes through the store, so
   * there is exactly one copy of the role, one loading flag per slice and one failure slot in the
   * application.
   */
  /**
   * This component's own element, read only to find controls a rejected submit is complaining about.
   * Needed because the fee and period fields live inside a `details` element that starts CLOSED, and a
   * control inside closed disclosure content cannot take focus — `focus()` on it succeeds as a call and
   * does nothing observable.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly roleStore = inject(RoleStore);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);

  /** Where the listing stood when the operator left it. */
  private readonly listReturn = inject(ListReturnStore);

  /**
   * Reports a write that settles after this screen has gone; see {@link
   * RoleFormComponent.handOverPendingWrite}.
   */
  private readonly deferredOutcome = inject(DeferredOutcomeService);

  /** This screen's lifetime, held for the one hand-over below and nothing else. */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The identity, read for ONE fact: which tenant the caller belongs to. The tenant is taken from the
   * caller rather than from a route segment, because this screen addresses no portal and must never be
   * able to protect one tenant's roles using another tenant's keys.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The tenant's own record, for the two protected role keys and the processor state. CORE state, which
   * every feature may inject — this is not a reach into the portal FEATURE, and the account listing
   * already reads the same slice to protect its removal command.
   */
  private readonly portals = inject(PortalStore);

  // -------------------------------------------------------------------------
  // LOCAL STATE
  // -------------------------------------------------------------------------

  /** The role currently loaded, or `null` in creation mode and before the first response. */
  private readonly loadedRole: WritableSignal<Role | null> = signal<Role | null>(null);

  /**
   * The role key whose read this screen is waiting for, or `null` when it is waiting for none. ⚠ A MARKER
   * OF OUR OWN RATHER THAN THE STORE'S SHARED FLAG. The store raises one loading flag per slice for the
   * whole application, so a read started by another screen would otherwise settle this one - applying a
   * role this screen never asked for, or reporting a failure that belongs to somebody else.
   */
  private readonly awaitedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /** Which mutation this screen is waiting for, or `null` when it is waiting for none. */
  private readonly awaitedMutation: WritableSignal<AwaitedRoleMutation | null> =
    signal<AwaitedRoleMutation | null>(null);

  /**
   * The identifier the store issued for {@link RoleFormComponent.awaitedMutation}. ⚠ WITHOUT THIS, THE
   * OPERATION NAME ALONE DECIDED WHOSE WRITE HAD SETTLED, AND IT CANNOT. `RoleStore` is provided at the
   * application root, so the listing screen and this form share one instance and their writes overlap.
   */
  private readonly awaitedMutationId: WritableSignal<number> = signal<number>(0);

  /** The role key already applied to the form. */
  private readonly appliedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /** The last refusal, as an RFC 7807 document, for the shared error banner. */
  private readonly failure: WritableSignal<ProblemDetails | null> = signal<ProblemDetails | null>(
    null,
  );

  /**
   * The sentence the banner falls back to when {@link failure}'s document says nothing useful. Held
   * beside the document rather than derived from it because the choice of sentence depends on WHICH
   * refusal arrived — a permission denial, a duplicate name, a stale read — and that is known only at the
   * moment the store's record is read.
   */
  private readonly failureFallbackMessage: WritableSignal<string> = signal<string>('');

  /**
   * True once a save has been refused because the role changed after this screen read it. Held separately
   * from {@link failure} because the two are answered differently.
   */
  private readonly staleRead: WritableSignal<boolean> = signal<boolean>(false);

  /** True once the user has asked to delete and before the dialog is settled. */
  private readonly deletePending: WritableSignal<boolean> = signal<boolean>(false);

  /**
   * True once the addressed role has been read and answered 404. Held rather than derived because the
   * store's failure is consumed and cleared by the arrival effect, so by render time there is nothing left
   * to derive from.
   */
  private readonly roleMissingSignal: WritableSignal<boolean> = signal<boolean>(false);

  /**
   * The invitation code as the store last reported it, or `null` on the creation route.
   *
   * ⚠ DECLARED BEFORE THE FORM DELIBERATELY. `new FormControl` validates on construction, so the strength
   * validator's closure runs while the form field initialiser is still executing; a field declared after the
   * form would be `undefined` at that moment. Held as a plain property rather than a signal because it is
   * read from inside a validator, and reading a signal there would enrol the validator in reactive tracking
   * for a value that must only be consulted at the moment validation runs.
   */
  private storedRsvpCode: string | null = null;

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The role form. `roleName` carries the presence rule here because that is the CREATION state, which is
   * what the legacy rendered when it had no role id. {@link applyMode} removes the rule in edit mode,
   * mirroring `valRoleName.Enabled = False` at `EditRoles.ascx.vb:L134`. ⚠ THE PRESENCE RULE IS THE
   * SHARED TRIM-AWARE ONE, NOT `Validators.required`, and the two are not interchangeable.
   */
  protected readonly form = new FormGroup<RoleFormModel>({
    roleName: new FormControl('', {
      nonNullable: true,
      validators: [requiredText, Validators.maxLength(ROLE_NAME_MAX_LENGTH)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(DESCRIPTION_MAX_LENGTH)],
    }),
    roleGroupId: new FormControl<number | null>(UNGROUPED_ROLE_GROUP, { nonNullable: true }),
    isPublic: new FormControl(false, { nonNullable: true }),
    autoAssignment: new FormControl(false, { nonNullable: true }),
    serviceFee: new FormControl('', {
      nonNullable: true,
      validators: [
        currencyDataType(SERVICE_FEE_INVALID_MESSAGE),
        moneyNotNegative(SERVICE_FEE_NEGATIVE_MESSAGE),
        moneyStorable(),
      ],
    }),
    billingPeriod: new FormControl('', {
      nonNullable: true,
      validators: [
        wholeNumberDataType(BILLING_PERIOD_INVALID_MESSAGE),
        wholeNumberPositive(BILLING_PERIOD_NOT_POSITIVE_MESSAGE),
        wholeNumberStorable(),
      ],
    }),
    billingFrequency: new FormControl<BillingFrequency>(NO_FREQUENCY, { nonNullable: true }),
    trialFee: new FormControl('', {
      nonNullable: true,
      validators: [
        currencyDataType(TRIAL_FEE_INVALID_MESSAGE),
        moneyNotNegative(TRIAL_FEE_NEGATIVE_MESSAGE),
        moneyStorable(),
      ],
    }),
    trialPeriod: new FormControl('', {
      nonNullable: true,
      validators: [
        wholeNumberDataType(TRIAL_PERIOD_INVALID_MESSAGE),
        wholeNumberPositive(TRIAL_PERIOD_NOT_POSITIVE_MESSAGE),
        wholeNumberStorable(),
      ],
    }),
    trialFrequency: new FormControl<BillingFrequency>(NO_FREQUENCY, { nonNullable: true }),
    rsvpCode: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.maxLength(RSVP_CODE_MAX_LENGTH),
        // The strength rule the server holds against an AUTHORED value, mirrored here so a caller learns of
        // it while typing instead of through a 400 after saving. `() => this.storedRsvpCode` defers the read
        // to validation time, which is what lets one validator serve both the creation route (nothing
        // stored, so every value is authored) and the edit route (the stored value grandfathered).
        authoredRsvpCodeValidator(() => this.storedRsvpCode),
      ],
    }),
    iconFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(ICON_FILE_MAX_LENGTH), containedIconPathValidator],
    }),
  });

  // -------------------------------------------------------------------------
  // DERIVED STATE
  // -------------------------------------------------------------------------

  /**
   * The role id as a number, or `null` when this is the creation route. ⚠ THE SHARED PARSER, NOT A LOCAL
   * ONE, AND REPLACING THE LOCAL ONE CLOSED TWO REAL DEFECTS. This computed used to parse inline, and the
   * inline rules differed from the parser every other feature uses in exactly the two ways that matter: *
   * NO RANGE CHECK. `2147483648` is a well-formed integer that round-trips as text, so it was accepted
   * and TRANSMITTED - `GET /api/v1/roles/2147483648` - where the API's own `int` binding cannot represent
   * it.
   */
  protected readonly roleKey: Signal<number | null> = computed(() => parseRouteId(this.roleId()));

  /** True when this is the edit form. */
  protected readonly isEditMode: Signal<boolean> = computed(() => this.roleKey() !== null);

  /** @see EDIT_SUBMIT_LABEL */
  protected readonly submitLabel: Signal<string> = computed(() =>
    this.isEditMode() ? EDIT_SUBMIT_LABEL : CREATE_SUBMIT_LABEL,
  );

  protected readonly addressUnreadable: Signal<boolean> = computed(
    () => this.roleId() !== undefined && this.roleKey() === null,
  );

  /** The sentence shown when the address does not name a readable role. */
  /** The caption of the one way out offered once the addressed role cannot be shown. */
  protected readonly recoveryLabel = RECOVERY_LABEL;

  /** True once the addressed role has been read and is not there. */
  protected readonly roleMissing: Signal<boolean> = this.roleMissingSignal.asReadonly();

  /** Whether this screen can show nothing but a way out: the address is unusable, or the role is gone. */
  protected readonly nothingToShow: Signal<boolean> = computed(
    () => this.addressUnreadable() || this.roleMissing(),
  );

  /**
   * The heading, which differs by mode. ⚠ AN UNREADABLE ADDRESS TAKES THE EDIT HEADING. Without the
   * second term, `/roles/abc` showed `Add New Role` above the sentence saying the address names no role,
   * while the route's own document title said `Edit Security Roles` — three labels disagreeing on one
   * screen, measured in a real browser.
   */
  protected readonly heading: Signal<string> = computed(() =>
    this.isEditMode() || this.addressUnreadable() ? EDIT_TITLE : ADD_TITLE,
  );

  /**
   * The one-line scope statement shown beneath the title. In edit mode it NAMES THE ROLE, because the
   * heading cannot - this screen's resource file declares no create-mode title key, so both modes show the
   * same "Edit Security Roles" heading and only the subtitle can say which role is on screen.
   */
  protected readonly pageSubtitle: Signal<string> = computed(() => {
    if (this.isEditMode() === false) {
      return CREATE_SUBTITLE;
    }

    const named: string = this.displayRoleName();

    return named.length > 0 ? named : CREATE_SUBTITLE;
  });

  /** The role name shown as read-only text in edit mode. */
  protected readonly displayRoleName: Signal<string> = computed(() =>
    textOrEmpty(this.loadedRole()?.roleName),
  );

  /** The role groups offered by the select, from the shared store. */
  protected readonly roleGroups: Signal<readonly RoleGroup[]> = this.roleStore.roleGroups;

  /**
   * The role-group select's options: the ungrouped choice first, then the real groups. exactly what
   * `BindGroups()` built at `EditRoles.ascx.vb:L71-L81` — one `< Global Roles >` entry followed by every
   * group, in the order the API returned them.
   */
  protected readonly roleGroupOptions: Signal<readonly RoleFormOption<number | null>[]> = computed(
    () => [
      { value: UNGROUPED_ROLE_GROUP, label: GLOBAL_ROLES_LABEL },
      ...this.roleGroups().map((group) => ({
        value: group.roleGroupId,
        label: group.roleGroupName,
      })),
    ],
  );

  /** The six frequency codes, shared by both frequency selects. */


  protected readonly frequencyOptions: readonly RoleFormOption<BillingFrequency>[] =
    BILLING_FREQUENCY_OPTIONS;

  /** True while either the role or the role-group list is still arriving. */
  protected readonly loading: Signal<boolean> = computed(
    () => this.awaitedRoleKey() !== null || this.roleStore.roleGroupsLoading(),
  );

  /** True while a mutation issued by THIS screen is in flight; the template disables its commands on this. */
  protected readonly saving: Signal<boolean> = computed(() => this.awaitedMutation() !== null);

  /** The refusal to render in the shared error banner, or `null`. */
  protected readonly problem: Signal<ProblemDetails | null> = computed<ProblemDetails | null>(() =>
    // An address that does not name a role is stated by the banner as well, and in the same place a
    // missing role is stated. It used to be a paragraph of this screen's own, which meant the one region
    // a reader's software watches said nothing at all about it.
    this.addressUnreadable() ? missingEntityProblem(UNREADABLE_ADDRESS_MESSAGE) : this.failure(),
  );

  /** The banner's fallback sentence for the current failure; empty when there is nothing to add. */
  protected readonly failureFallback: Signal<string> = this.failureFallbackMessage.asReadonly();

  /** True while this screen is holding a snapshot the server has already refused. */
  protected readonly conflicted: Signal<boolean> = this.staleRead.asReadonly();

  /** The recovery sentence shown beside the re-read command. */
  protected readonly conflictRecoveryMessage = CONCURRENCY_RECOVERY_MESSAGE;

  /** Caption of the re-read command. */
  protected readonly conflictReloadLabel = CONCURRENCY_RELOAD_LABEL;

  /** True when the delete confirmation dialog should be shown. */
  protected readonly confirmingDelete: Signal<boolean> = this.deletePending.asReadonly();

  /** The verbatim legacy confirmation wording, for the dialog. */
  protected readonly deleteConfirmMessage = DELETE_CONFIRM_MESSAGE;

  /** The heading for the delete dialog, taken from the global `cmdDelete` caption. */
  protected readonly deleteConfirmLabel = 'Delete';

  /** The `cmdManage` caption, which IS locally keyed: `cmdManage.Text`. */
  protected readonly manageUsersLabel = 'Manage Users in this Role';

  /**
   * Qualifier appended to the accessible name of the COUNT half of a period pair. MIGRATION: NET-NEW
   * WORDING, and the smallest addition that resolves a real ambiguity.
   */
  protected readonly periodCountQualifier = 'count';

  /** Qualifier appended to the accessible name of the UNIT half of a period pair. See above. */
  protected readonly periodUnitQualifier = 'unit';

  /**
   * The notice shown while the role name is holding as many characters as the column can store. ⚠ THIS
   * EXISTS BECAUSE THE TRUNCATION IS OTHERWISE INVISIBLE. `maxlength` is declared on the control —
   * faithfully, because `editroles.ascx:L31` declares `MaxLength="50"` and the legacy browser enforced it
   * the same way — and a browser enforcing it DISCARDS the surplus characters silently.
   */
  protected readonly nameAtLimitNotice =
    `Maximum length reached. A role name may hold ${ROLE_NAME_MAX_LENGTH} characters, ` +
    `and any further characters are not accepted.`;

  /**
   * The stored paid-membership values the legacy bind withholds from their boxes. ⚠ THE BOXES STAY EMPTY
   * AND THE SCREEN SAYS SO. That is the whole of this member, and the reason it exists rather than the
   * boxes simply being filled is recorded on {@link applyRole}: filling them would break the legacy
   * validator and the legacy write gate, both of which this screen is required to match.
   */
  protected readonly withheldTerms: Signal<readonly WithheldTerm[]> = computed(() => {
    const role = this.loadedRole();
    if (role === null) {
      return NO_WITHHELD_TERMS;
    }

    const terms: WithheldTerm[] = [];
    const state = (caption: string, value: string): void => {
      if (value !== '') {
        terms.push({ caption, value });
      }
    };

    if (!isRolePriced(role)) {
      state(WITHHELD_TERM_CAPTIONS.serviceFee, formatMoney(role.serviceFee));
      state(WITHHELD_TERM_CAPTIONS.billingPeriod, formatPeriod(role.billingPeriod));
      state(WITHHELD_TERM_CAPTIONS.billingFrequency, storedFrequencyText(role.billingFrequency));
    }

    if (!isRoleOnTrial(role)) {
      state(WITHHELD_TERM_CAPTIONS.trialFee, formatMoney(role.trialFee));
      state(WITHHELD_TERM_CAPTIONS.trialPeriod, formatPeriod(role.trialPeriod));
      state(WITHHELD_TERM_CAPTIONS.trialFrequency, storedFrequencyText(role.trialFrequency));
    }

    return terms;
  });

  /**
   * The stored frequency codes this console cannot set, each with the caption of the box that holds it.
   *
   * ⚠ THE GAP THIS CLOSES. `coerceFrequency` maps any code outside the six the select offers onto `'N'`,
   * and the outgoing write initialises the frequency to `'N'` as well (`EditRoles.ascx.vb:L214`, `:L224`),
   * so a role storing `'Q'` has that `'Q'` REPLACED the moment this form is saved. The withheld-values
   * notice previously skipped such a code entirely - it only stated a frequency it could name - so the one
   * stored value whose loss was certain was the one nothing mentioned.
   */
  protected readonly rewrittenFrequencies: Signal<readonly WithheldTerm[]> = computed(() => {
    const role = this.loadedRole();
    if (role === null) {
      return NO_WITHHELD_TERMS;
    }

    const rewritten: WithheldTerm[] = [];

    // ⚠ GATED ON REPRESENTABILITY ALONE, AND NOT ON WHETHER THE BOXES WERE WITHHELD. This condition was
    // originally `!isRolePriced(role) && ...`, and that was WRONG for a reason runtime testing exposed: the
    // rewrite does not depend on the bind gate at all. `coerceFrequency` maps any code outside the six the
    // select offers onto `'N'`, so the select shows "None", and the outgoing write initialises the
    // frequency to `'N'` (`EditRoles.ascx.vb:L214`, `:L224`) - both regardless of whether the fee boxes were
    // populated. A role priced at 249.50 and storing `'Q'` therefore had its boxes filled, failed the old
    // gate, and so received NO disclosure whatsoever of a loss that was certain. Measured in the browser on
    // two roles: one lost `'Q'` in silence, the other disclosed its trial `'X'` while dropping its billing
    // `'Z'` from the same sentence.
    if (isUnnameableFrequency(role.billingFrequency)) {
      rewritten.push({
        caption: WITHHELD_TERM_CAPTIONS.billingFrequency,
        value: String(role.billingFrequency),
      });
    }

    if (isUnnameableFrequency(role.trialFrequency)) {
      rewritten.push({
        caption: WITHHELD_TERM_CAPTIONS.trialFrequency,
        value: String(role.trialFrequency),
      });
    }

    return rewritten;
  });

  /**
   * The sentence the advanced section prints above the paid-membership boxes, or nothing. The lead names
   * WHICH boxes are empty and why, because "some values are not shown" would leave a reader hunting; the
   * tail states them.
   */
  protected readonly withheldTermsNotice: Signal<string> = computed(() => {
    const terms = this.withheldTerms();
    const rewritten = this.rewrittenFrequencies();

    // ⚠ EITHER CONDITION IS ENOUGH TO SPEAK. Returning early on an empty `terms` list was the second half
    // of the same defect: a role whose boxes were all filled had no withheld terms, so the notice was
    // suppressed outright and its unnameable code went unmentioned even though saving would destroy it.
    if (terms.length === 0 && rewritten.length === 0) {
      return '';
    }

    const role = this.loadedRole();
    if (role === null) {
      return '';
    }

    const billingWithheld = !isRolePriced(role);
    const trialWithheld = !isRoleOnTrial(role);

    // ⚠ THE LEAD NAMES THE CONDITION, NOT AN ABSENCE, AND THAT IS THE CORRECTION. It previously opened
    // "This role has no trial" and then went on to state "the values held for it are Trial Fee 5.00 and
    // Trial Period 2" - a sentence that denies in its first clause what it reports in its second. The
    // premise was drawn from the legacy BIND GATE, which is a narrower fact than the absence it was
    // phrased as: `EditRoles.ascx.vb:L146` fills the billing boxes only when the service fee formats to
    // something other than "0.00", and the trial boxes only for a trial frequency among the six the
    // select offers. A role can therefore hold trial values and still fail that gate, which is exactly
    // the state the notice was misdescribing.
    let lead: string;
    if (billingWithheld && trialWithheld) {
      lead =
        'The paid-membership boxes below stay empty. The legacy editor filled the billing boxes only for '
        + 'a role with a service fee to charge, and the trial boxes only for a trial frequency it '
        + 'recognised, and this role meets neither condition.';
    } else if (billingWithheld) {
      // Phrased as failing the CONDITION rather than as an absence, matching the both-groups lead above.
      // "...and this role has none" asserted a fact about the record while the sentence that followed
      // listed the record's own stored values, which is the shape of wording this finding is about.
      lead =
        'The billing boxes below stay empty. The legacy editor filled them only for a role with a service '
        + 'fee to charge, and this role does not meet that condition.';
    } else {
      lead =
        'The trial boxes below stay empty. The legacy editor filled them only for a trial frequency it '
        + 'recognises, and this role stores none it can name.';
    }

    const stated = joinPhrases(terms.map((term) => `${term.caption} ${term.value}`));

    // ⚠ THE CONSEQUENCE IS STATED, BECAUSE IT IS CERTAIN RATHER THAN HYPOTHETICAL. Saving replaces every
    // value listed here, whether or not the operator typed anything, and that overwrite is deliberate
    // parity with `EditRoles.ascx.vb:L212-L231`. Listing values while leaving their fate unsaid is what
    // made the notice read as reassurance.
    const consequence = `The values stored for it are ${stated}, and saving this form replaces them.`;

    if (rewritten.length === 0) {
      return `${lead} ${consequence}`;
    }

    // Named individually rather than counted, because the operator's only route to recovering the value is
    // knowing what it was.
    const codes = joinPhrases(rewritten.map((term) => `${term.caption} ${term.value}`));
    const plural = rewritten.length > 1;
    const rewriteSentence =
      `${plural ? 'The stored codes' : 'The stored code'} ${codes} `
      + `${plural ? 'are' : 'is'} not among the frequencies this console can set, so saving records `
      + `${frequencyCaption(NO_FREQUENCY)} in ${plural ? 'their' : 'its'} place.`;

    // ⚠ THE REWRITE CAN BE THE WHOLE NOTICE. A role whose boxes are all filled withholds nothing, so
    // there is no "these boxes stay empty" lead to give and no withheld list to state - but its unnameable
    // code is still destroyed on save, and that is the entire reason this branch exists. It opens by
    // explaining the "None" the operator can see in the box, so the disclosure is anchored to something on
    // screen rather than arriving unattached.
    if (terms.length === 0) {
      return (
        `${plural ? 'The frequency boxes' : 'The frequency box'} below cannot show `
        + `${plural ? 'the values' : 'the value'} this role stores. ${rewriteSentence}`
      );
    }

    return `${lead} ${consequence} ${rewriteSentence}`;
  });

  /** True when this role is one of the two the portal protects. DEFECT 4. */
  protected readonly isProtectedRole: Signal<boolean> = computed(() => {
    const key = this.roleKey();
    if (key === null) {
      return false;
    }
    return key === this.administratorRoleId() || key === this.registeredRoleId();
  });

  /**
   * True when the whole form is read-only. `ActivateControls(False)` disabled ELEVEN named controls —
   * description, role group, both check boxes, both fees, both periods, both frequency selects and the
   * RSVP code — and conspicuously did NOT disable `ctlIcon` or `txtRSVPLink`.
   */
  protected readonly readOnly: Signal<boolean> = computed(() => this.isProtectedRole());

  /** True when the Update command is rendered. */
  protected readonly canSave: Signal<boolean> = computed(() => !this.isProtectedRole());

  /** True when the Delete command is rendered. */
  protected readonly canDelete: Signal<boolean> = computed(
    () => this.isEditMode() && !this.isProtectedRole(),
  );

  /**
   * True when the Manage Users command is rendered. Hidden in creation mode and hidden for the
   * registered-users role (`:L180-L182`).
   */
  protected readonly canManageUsers: Signal<boolean> = computed(() => {
    const key = this.roleKey();
    if (key === null) {
      return false;
    }
    return key !== this.registeredRoleId();
  });

  /**
   * True when the payment-processor warning is shown. See {@link paymentProcessorConfigured}: the warning
   * appears when the processor is NOT configured, which is what the code did rather than what its comment
   * claimed. ⚠ WITHHELD UNTIL THE TENANT'S RECORD RESOLVES, which is the one place this screen departs
   * from the legacy expression.
   */
  protected readonly showProcessorWarning: Signal<boolean> = computed(
    () => this.protectedFactsResolved() && !this.paymentProcessorConfigured(),
  );

  // -------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------

  public constructor() {
    this.roleStore.loadRoleGroups();

    // The tenant's own record, for the two protected role keys and the processor state. Read from the
    // CALLER'S identity and never from a route, and idempotent in the store — several screens asking on
    // initialisation issue one request between them.
    const portalId: number | undefined = this.auth.currentUser()?.portalId;

    if (portalId !== undefined) {
      this.portals.loadCurrentPortalContext(portalId);
    }

    // ⚠ THE BODY IS `untracked` AND THAT IS LOAD-BEARING, NOT TIDINESS. Everything below the first line is
    // imperative work that reaches into the store, and a store command reads store state on its way —
    // clearing the held failure begins by testing whether there is one.
    effect(() => {
      const key = this.roleKey();

      untracked(() => {
        this.applyMode(key !== null);

        if (key === null) {
          this.resetToCreateDefaults();

          return;
        }

        this.loadRole(key);
      });
    });

    effect(() => {
      const awaited: number | null = this.awaitedRoleKey();
      const loading: boolean = this.roleStore.selectedRoleLoading();
      const role: Role | null = this.roleStore.selectedRole();
      const failure: RoleStoreFailure | null = this.roleStore.failure();

      if (awaited === null || loading) {
        return;
      }

      untracked(() => {
        this.awaitedRoleKey.set(null);

        if (failure !== null && failure.operation === 'loadRole') {
          if (failure.status === NOT_FOUND) {
            // ⚠ MIGRATION - THE LEGACY BOUNCE IS GONE, DELIBERATELY. `:L170-L172` treated an unreadable
            // role as an attempt to reach an item outside the module and redirected to the Security Roles
            // page, which this screen reproduced with a surviving toast and a replaced history entry. It
            // was the only one of the four detail screens that moved the reader, it needed an explicit
            // exemption from the shell's navigation sweep for its own explanation to survive the
            // navigation it caused, and it threw away the address a reader had followed. A role that is
            // not there is now stated where it was asked for, in the shared wording, with the same one
            // way out the other three offer.
            this.roleMissingSignal.set(true);
            this.failure.set(missingEntityProblem(ROLE_NOT_FOUND_MESSAGE));

            return;
          }

          this.reportFailure(failure, LOAD_FAILED_MESSAGE);

          return;
        }

        // Compared on identity with a strict equality, never on truthiness: the role table is seeded
        // `IDENTITY(0, 1)`, so a role key of zero is a real role and a falsy test would refuse to apply the
        // tenant's first role to the form.
        if (role === null || role.roleId !== awaited) {
          return;
        }

        if (this.appliedRoleKey() === awaited) {
          return;
        }

        this.appliedRoleKey.set(awaited);
        this.applyRole(role);
      });
    });

    effect(() => {
      const awaited: AwaitedRoleMutation | null = this.awaitedMutation();
      const awaitedId: number = this.awaitedMutationId();
      const settled = this.roleStore.mutation();

      if (awaited === null || settled === null || settled.id !== awaitedId) {
        return;
      }

      untracked(() => {
        this.awaitedMutation.set(null);
        this.awaitedMutationId.set(0);

        const failure: RoleStoreFailure | null = settled.failure;

        if (failure !== null && settled.operation === awaited) {
          this.reportFailure(failure, MUTATION_FAILURE_MESSAGE[awaited]);

          return;
        }

        // Marked for EVERY settled operation rather than only the two writes: after a deletion the role is
        // gone, so entry still standing in the controls is work that can no longer be saved, and an
        // operator who typed into the form and then deleted the role was asked the same redundant question.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        // Exempted from the navigation sweep for the reason recorded on the not-found path above:
        // `afterMutation` navigates in this same task, and the shell's sweep would otherwise discard this
        // confirmation before it could be painted at the destination - which is exactly where the legacy
        // showed it, since its update and delete handlers announced and then redirected.
        this.notifications.notify('success', MUTATION_SUCCESS_MESSAGE[awaited]);
        this.notifications.retainAcrossNavigation();
        this.afterMutation();
      });
    });

    // The legacy had no race to manage: `BindGroups()` ran synchronously at `:L127`, before the role was
    // read at `:L136`, so the options always existed by the time the selection was made.
    effect(() => {
      const role = this.loadedRole();
      const groups = this.roleGroups();
      if (role === null) {
        return;
      }
      const control = this.form.controls.roleGroupId;
      if (control.dirty || control.touched) {
        return;
      }
      control.setValue(coerceRoleGroupId(role.roleGroupId, groups), { emitEvent: false });
    });

    // Keeps the form's enabled state in step with the protected-role guard.
    effect(() => {
      const locked = this.readOnly();
      if (locked && this.form.enabled) {
        this.form.disable({ emitEvent: false });
        return;
      }
      if (!locked && this.form.disabled) {
        this.form.enable({ emitEvent: false });
      }
    });

    this.destroyRef.onDestroy(() => this.handOverPendingWrite());
  }

  /**
   * Hands an outstanding write over to be reported after this screen has gone. ⚠ THE WRITE BRIDGE ABOVE
   * IS AN EFFECT IN THIS COMPONENT'S INJECTION CONTEXT, SO IT DIES WITH THIS COMPONENT - and an operator
   * who submits and then immediately goes somewhere else destroys the only party that was going to tell
   * them what happened.
   */
  private handOverPendingWrite(): void {
    const awaited = this.awaitedMutation();
    const awaitedId = this.awaitedMutationId();

    if (awaited === null) {
      return;
    }

    const verdict: Signal<DeferredOutcome> = computed<DeferredOutcome>(() => {
      const settled = this.roleStore.mutation();

      if (settled === null || settled.id !== awaitedId) {
        return 'pending';
      }

      return settled.failure !== null && settled.operation === awaited ? 'failed' : 'succeeded';
    });

    this.deferredOutcome.announceWhenSettled(
      verdict,
      () => MUTATION_SUCCESS_MESSAGE[awaited],
      () => {
        const settled = this.roleStore.mutation();

        return {
          message: MUTATION_FAILURE_MESSAGE[awaited],
          reference: settled?.failure?.summary.supportReference ?? null,
        };
      },
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  protected onSubmit(): void {
    if (this.saving() || this.readOnly()) {
      return;
    }

    // ⚠ THE SERVER'S LAST WORD IS RETIRED THE MOMENT UPDATE IS PRESSED AGAIN, and it is retired HERE -
    // above the validity gate - rather than after it, which is where the clear used to sit.
    this.clearFailure();

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.revealFirstInvalidControl();
      return;
    }

    // MIGRATION: trimming the padding is a deliberate divergence. The legacy stored what was posted, spaces
    // and all, and its `RequiredFieldValidator` never rewrote the box.
    this.normaliseRoleName();

    // Retained as defence in depth rather than as a live branch. Trimming can only shorten, so `maxLength`
    // cannot be newly breached, and emptiness was settled above by a rule that already trimmed - so nothing
    // should be able to fail here.
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.revealFirstInvalidControl();

      return;
    }

    const key = this.roleKey();

    if (key === null) {
      this.createRole();
      return;
    }

    this.updateRole(key);
  }

  /**
   * Trims the padding off the role name, in its own control. `emitEvent: false` because this is a DISPLAY
   * CORRECTION rather than an operator edit: it must not be able to start a cascade through any listener
   * on this form — and this form has two, the role-group reconciler and the protected-role lock, either
   * of which reacting to a tidy-up would be a side effect nobody asked for.
   */
  private normaliseRoleName(): void {
    const control = this.form.controls.roleName;
    const trimmed: string = control.value.trim();

    if (trimmed === control.value) {
      return;
    }

    control.setValue(trimmed, { emitEvent: false });
  }

  /**
   * Reads the role again after a refused save, so the screen can hold a current revision. ⚠ WITHOUT THIS
   * COMMAND A CONFLICT IS A DEAD END. The marker sent with an update comes from the role this screen
   * read; a refusal does not change that role, so pressing Update again sends the same refused marker and
   * is refused again, indefinitely.
   */
  protected onReloadAfterConflict(): void {
    const key = this.roleKey();
    if (key === null) {
      return;
    }

    this.appliedRoleKey.set(null);
    this.loadRole(key);
  }

  /**
   * Opens the disclosure holding the first offending control, then focuses that control. ⚠ WITHOUT THIS,
   * A REJECTED SUBMIT ON THIS SCREEN COULD REPORT NOTHING AT ALL. Four of the form's twelve controls —
   * both fees and both periods — live inside the Advanced Settings `details`, which starts CLOSED. When
   * one of them is the reason a submit is refused, `markAllAsTouched()` renders its message into content
   * that is not being displayed, and the shared focus directive's `focus()` call lands on an element
   * inside closed disclosure content, where it succeeds as a call and moves focus nowhere.
   */
  private revealFirstInvalidControl(): void {
    const target = this.host.nativeElement.querySelector<HTMLElement>(INVALID_CONTROL_SELECTOR);
    if (target === null) {
      return;
    }

    // `closest` walks every ancestor, so a control nested in more than one disclosure is reached by
    // repeating the step rather than by assuming a single level.
    for (
      let section = target.closest('details');
      section !== null;
      section = section.parentElement?.closest('details') ?? null
    ) {
      if (!section.open) {
        section.open = true;
      }
    }

    if (target !== target.ownerDocument.activeElement) {
      target.focus();
    }
  }

  /**
   * Shows a typed number back in the form this screen stores it in, once the person leaves the box. ⚠
   * THIS EXISTS TO MAKE NORMALISATION VISIBLE, NOT TO PERFORM IT. Normalisation was already happening and
   * was already invisible: `007` was sent as `7`, `1234.5` as `1234.5` against a screen that displays
   * `1,234.50`, and nothing told the person their entry had been reinterpreted.
   *
   * @param field The numeric control that has just lost focus.
   */
  protected onNumericCommit(
    field: 'serviceFee' | 'billingPeriod' | 'trialFee' | 'trialPeriod',
  ): void {
    const control = this.form.controls[field];
    if (control.pristine || control.disabled) {
      return;
    }

    const current: string = control.value;
    const money = field === 'serviceFee' || field === 'trialFee';
    const parsed = money ? parseMoney(current) : parseWholeNumber(current);
    if (parsed === null) {
      return;
    }

    if (money && amountLoss(current, parsed) !== null) {
      return;
    }

    const canonical = money ? formatMoney(parsed) : String(parsed);
    if (canonical.length === 0 || canonical === current) {
      return;
    }

    control.setValue(canonical, { emitEvent: false });
  }

  protected onCancel(): void {
    this.navigateToList();
  }

  /**
   * Opens the delete confirmation. `EditRoles.ascx.vb:L112` attached a client-side confirmation to the
   * delete button with the global `DeleteItem` wording.
   */
  protected requestRemoval(): void {
    if (this.saving() || !this.canDelete()) {
      return;
    }
    this.deletePending.set(true);
  }

  protected onRemovalConfirmed(): void {
    this.deletePending.set(false);
    const key = this.roleKey();
    if (key === null || this.saving() || !this.canDelete()) {
      return;
    }
    this.deleteRole(key);
  }

  /** The user dismissed the deletion. */
  protected onRemovalCancelled(): void {
    this.deletePending.set(false);
  }

  protected onManageUsers(): void {
    const key = this.roleKey();
    if (key === null || !this.canManageUsers()) {
      return;
    }
    void this.router.navigate([ROLE_LIST_ROUTE, key, 'users']);
  }

  // -------------------------------------------------------------------------
  // TEMPLATE SUPPORT
  // -------------------------------------------------------------------------

  /**
   * True while the role name control holds as many characters as the column can store. A METHOD rather
   * than a computed signal, for the same reason {@link messageFor} is one: nothing on this form bridges
   * `valueChanges` into a signal, and introducing a subscription for one notice would put a second,
   * independently-updated copy of the control's text beside the control itself.
   *
   * @returns Whether the length notice should be rendered.
   */
  protected nameAtLimit(): boolean {
    return this.form.controls.roleName.value.length >= ROLE_NAME_MAX_LENGTH;
  }

  /**
   * The message to show beneath one field, or `null` when it has nothing to say. A message appears only
   * once the control is both invalid and either dirty or touched, which is how `Display="Dynamic"`
   * behaved: the legacy validators rendered nothing until a postback had exercised them.
   *
   * @param field The control to report on.
   * @returns The message, or `null`.
   */
  protected messageFor(field: keyof RoleFormModel): string | null {
    const control = this.form.controls[field];
    if (!control.invalid || !(control.dirty || control.touched)) {
      return null;
    }

    const errors: ValidationErrors | null = control.errors;
    if (errors === null) {
      return null;
    }

    for (const key of [
      'currencyDataType',
      'wholeNumberDataType',
      'moneyNotNegative',
      'wholeNumberPositive',
      'moneyStorable',
      'wholeNumberStorable',
      'serverError',
    ]) {
      const held: unknown = errors[key];
      if (typeof held === 'string' && held.length > 0) {
        return held;
      }
    }

    if (errors['required'] !== undefined) {
      return ROLE_NAME_REQUIRED_MESSAGE;
    }

    // Reported before the length rule, because a reference can break both and the containment failure is
    // the one the person must act on: shortening an absolute path does not make it relative.
    if (errors[ICON_NOT_CONTAINED_ERROR] !== undefined) {
      return ICON_NOT_CONTAINED_MESSAGE;
    }

    if (errors['rsvpCodeTooWeak'] !== undefined) {
      return RSVP_CODE_TOO_WEAK_MESSAGE;
    }

    const maxLength: unknown = errors['maxlength'];
    if (typeof maxLength === 'object' && maxLength !== null) {
      const requested: unknown = (maxLength as { requiredLength?: unknown }).requiredLength;
      if (typeof requested === 'number') {
        return `Enter at most ${requested} characters.`;
      }
    }

    return GENERIC_FIELD_MESSAGE;
  }

  // -------------------------------------------------------------------------
  // MODE
  // -------------------------------------------------------------------------

  /** @param editing True when a role id is present. */
  private applyMode(editing: boolean): void {
    const control = this.form.controls.roleName;
    control.setValidators(
      editing
        ? [Validators.maxLength(ROLE_NAME_MAX_LENGTH)]
        : [requiredText, Validators.maxLength(ROLE_NAME_MAX_LENGTH)],
    );
    control.updateValueAndValidity({ emitEvent: false });
  }

  private resetToCreateDefaults(): void {
    this.loadedRole.set(null);
    this.clearFailure();
    // Nothing is stored on the creation route, so there is nothing to grandfather and every non-empty code
    // submitted here is authored. Cleared before `reset` for the same ordering reason the arrival path
    // records it before `setValue`.
    this.storedRsvpCode = null;
    this.form.reset();
    this.form.markAsPristine();
    this.form.markAsUntouched();
  }

  // -------------------------------------------------------------------------
  // TRANSPORT
  // -------------------------------------------------------------------------

  /** @param key The role id, which may legitimately be `0`. */
  private loadRole(key: number): void {
    // Whatever was missing, a new read has been asked for and the answer is not known yet.
    this.roleMissingSignal.set(false);
    this.clearFailure();
    this.awaitedRoleKey.set(key);
    this.roleStore.selectRole(key);
  }

  /**
   * Populates the form from a loaded role — `EditRoles.ascx.vb:L139-L169`. DEFECT 3, the string money
   * comparison.
   *
   * @param role The role as the API reported it.
   */
  private applyRole(role: Role): void {
    this.loadedRole.set(role);

    // A role has just arrived from the server, so whatever snapshot was refused is no longer the one on
    // screen. Cleared here rather than in the reload command because this is the single point every arrival
    // passes through, including the first one.
    this.staleRead.set(false);

    const priced = isRolePriced(role);

    const trialFrequency = coerceFrequency(role.trialFrequency);
    const onTrial = isRoleOnTrial(role);

    // ⚠ RECORDED BEFORE `setValue`, NOT AFTER. `setValue` revalidates, so the strength validator runs during
    // the call below; recording the stored code afterwards would let a legacy short code be marked invalid on
    // arrival and only clear on the next keystroke. Normalised through the same `textOrEmpty` the control is
    // filled with, so the comparison is between two values of the same shape - a null code and the empty box
    // that renders it are the same state.
    this.storedRsvpCode = textOrEmpty(role.rsvpCode);

    this.form.setValue({
      roleName: textOrEmpty(role.roleName),
      description: textOrEmpty(role.description),
      roleGroupId: this.form.controls.roleGroupId.value,
      isPublic: role.isPublic,
      autoAssignment: role.autoAssignment,
      serviceFee: priced ? formatMoney(role.serviceFee) : '',
      billingPeriod: priced ? formatPeriod(role.billingPeriod) : '',
      billingFrequency: priced ? coerceFrequency(role.billingFrequency) : NO_FREQUENCY,
      trialFee: onTrial ? formatMoney(role.trialFee) : '',
      trialPeriod: onTrial ? formatPeriod(role.trialPeriod) : '',
      trialFrequency: onTrial ? trialFrequency : NO_FREQUENCY,
      rsvpCode: textOrEmpty(role.rsvpCode),
      iconFile: textOrEmpty(role.iconFile),
    });

    this.form.markAsPristine();
    this.form.markAsUntouched();
  }

  /**
   * Creates the role — `objRoleController.AddRole(objRoleInfo)` at `EditRoles.ascx.vb:L253`. the legacy
   * guarded the insert with its own lookup, `If objRoleController.GetRoleByName(PortalId,
   * objRoleInfo.RoleName) Is Nothing Then` (`:L252`), and showed the `DuplicateRole` message at
   * `RedError` when it found a match (`:L256`).
   */
  private createRole(): void {
    this.awaitedMutation.set('createRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.createRole(this.toCreateRequest()));
  }

  /**
   * Updates the role — `objRoleController.UpdateRole(objRoleInfo)` at `EditRoles.ascx.vb:L260`. the
   * legacy ran NO duplicate check on this path — `:L259-L262` updates unconditionally — which it could
   * afford because `UpdateRole` has no `RoleName` parameter and so could not create a collision.
   *
   * @param key The role id being updated, which may legitimately be `0`.
   */
  private updateRole(key: number): void {
    this.awaitedMutation.set('updateRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.updateRole(key, this.toUpdateRequest()));
  }

  /** @param key The role id being deleted, which may legitimately be `0`. */
  private deleteRole(key: number): void {
    this.clearFailure();
    this.awaitedMutation.set('deleteRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.deleteRole(key, { thenReadListing: false }));
  }

  /**
   * Runs after any successful mutation. `DataCache.RemoveCache("GetRoles")` has NO client equivalent — it
   * evicted a server-side cache entry, and no endpoint exposes that.
   */
  private afterMutation(): void {
    // ⚠ NO RE-READ IS ASKED FOR HERE ANY MORE. The store refreshes the listing itself after a creation and
    // after a deletion, and patches the changed row immutably after an update, so a read requested here
    // would be a second identical request racing the store's own - and whichever answered last would decide
    // what the listing showed.
  // ⚠ THE ADDRESS IS REPLACED, NOT PUSHED, because the work this screen existed for is finished.
    this.navigateToList(true);
  }

  /** Returns to the role list, the destination of every `NavigateURL()` on this screen. */
  private navigateToList(replaceEntry = false): void {
    if (replaceEntry) {
      void this.router.navigate([ROLE_LIST_ROUTE], {
        // The listing's own place, so a save does not cost the operator the page they were working on.
        queryParams: this.listedCoordinate(),
        replaceUrl: true,
      });

      return;
    }

    void this.router.navigate([ROLE_LIST_ROUTE], {
      queryParams: this.listedCoordinate(),
    });
  }

  /**
   * The listing coordinate to return to, with a group narrowing dropped when it names a group that no longer
   * exists.
   *
   * ⚠ THE REMEMBERED COORDINATE CAN NAME A GROUP THAT HAS SINCE BEEN DELETED, AND SENDING THE OPERATOR BACK
   * TO IT COST THEM THE RECORD THEY HAD JUST CREATED. The listing remembers where the operator was so a save
   * does not lose their place, which is right - but "where they were" is remembered as an address, and an
   * address naming a deleted group is a request the server answers 404 to. A save that succeeded then landed
   * on a listing reporting failure with the new role nowhere in it.
   *
   * The listing itself now heals such a narrowing when it meets one, so this is not the only guard; it is
   * the earlier and cheaper one. Validating here means the failed read is never issued at all, rather than
   * issued, recognised and withdrawn. Anything the group set does not contain is dropped, which covers a
   * group deleted from this session and one deleted by somebody else.
   *
   * ⚠ IT ASKS WHETHER THE GROUP READ SETTLED, NOT WHETHER THE SET HAS MEMBERS. "Not read yet" and "read and
   * genuinely empty" both hold an empty array, and conflating them breaks this in both directions: treating
   * unread as empty discards a narrowing that may be perfectly valid, while treating a genuinely empty set
   * as unread sends the operator back to an address that can only be refused. Unsettled is left exactly as
   * remembered rather than discarded on a guess.
   *
   * @returns The parameters to navigate with.
   */
  private listedCoordinate(): Params {
    const remembered: Params = this.listReturn.coordinateFor(ROLE_LIST_ROUTE);
    const narrowing: unknown = remembered[ROLE_LIST_GROUP_PARAM];

    if (typeof narrowing !== 'string' || !this.roleStore.roleGroupsSettled()) {
      return remembered;
    }

    const groups: readonly RoleGroup[] = this.roleGroups();

    const key = Number(narrowing);

    // A non-numeric value is one of the listing's own intent tokens rather than a key, so there is no group
    // to look up and nothing to invalidate.
    if (!Number.isInteger(key)) {
      return remembered;
    }

    if (groups.some((group: RoleGroup): boolean => group.roleGroupId === key)) {
      return remembered;
    }

    return { ...remembered, [ROLE_LIST_GROUP_PARAM]: null };
  }

  // -------------------------------------------------------------------------
  // REQUEST CONSTRUCTION
  // -------------------------------------------------------------------------

  private toCreateRequest(): CreateRoleRequest {
    const value = this.form.getRawValue();
    const billing = this.resolveBillingTerms();
    const trial = this.resolveTrialTerms(billing);

    return {
      roleName: value.roleName,
      description: textOrNull(value.description),
      serviceFee: billing.fee,
      billingPeriod: billing.period,
      billingFrequency: billing.frequency,
      trialFee: trial.fee,
      trialPeriod: trial.period,
      trialFrequency: trial.frequency,
      isPublic: value.isPublic,
      autoAssignment: value.autoAssignment,
      roleGroupId: value.roleGroupId,
      rsvpCode: textOrNull(value.rsvpCode),
      iconFile: textOrNull(value.iconFile),
    };
  }

  /**
   * Builds the update request. MIGRATION: `UpdateRoleRequest` declares `roleName`, whereas the legacy
   * `UpdateRole` stored procedure had no such parameter.
   */
  private toUpdateRequest(): UpdateRoleRequest {
    const value = this.form.getRawValue();
    const billing = this.resolveBillingTerms();
    const trial = this.resolveTrialTerms(billing);
    const loaded = this.loadedRole();

    return {
      // The loaded name wins in edit mode; the control's value is reached only when no role has
      // been loaded, and it has already been tidied and re-judged by `onSubmit`.
      roleName: loaded === null ? value.roleName : loaded.roleName,
      description: textOrNull(value.description),
      roleGroupId: value.roleGroupId,
      isPublic: value.isPublic,
      autoAssignment: value.autoAssignment,
      serviceFee: billing.fee,
      billingPeriod: billing.period,
      billingFrequency: billing.frequency,
      trialFee: trial.fee,
      trialPeriod: trial.period,
      trialFrequency: trial.frequency,
      rsvpCode: textOrNull(value.rsvpCode),
      iconFile: textOrNull(value.iconFile),

      concurrencyToken: loaded?.concurrencyToken ?? null,
    };
  }

  /** @returns The resolved fee, period and frequency. */
  private resolveBillingTerms(): ResolvedTerms {
    const value = this.form.getRawValue();

    if (value.billingFrequency === NO_FREQUENCY) {
      return SUPPRESSED_TERMS;
    }

    const fee = parseMoney(value.serviceFee);
    const period = parseWholeNumber(value.billingPeriod);

    if (fee === null || period === null) {
      return SUPPRESSED_TERMS;
    }

    return { fee, period, frequency: value.billingFrequency };
  }

  /**
   * Resolves the trial group — `EditRoles.ascx.vb:L222-L230`. A TRIAL IS ONLY STORED WHEN A SERVICE FEE
   * EXISTS. `:L226` reads `If sglServiceFee <> 0 And txtTrialFee.Text <> "" And txtTrialPeriod.Text <>
   * ""` ` And cboTrialFrequency.SelectedItem.Value <> "N" Then` and the FIRST conjunct is the
   * already-resolved service fee, not a trial field at all.
   *
   * @param billing The already-resolved billing terms, whose fee gates this group.
   * @returns The resolved trial fee, period and frequency.
   */
  private resolveTrialTerms(billing: ResolvedTerms): ResolvedTerms {
    if (billing.fee === 0) {
      return SUPPRESSED_TERMS;
    }

    const value = this.form.getRawValue();

    if (value.trialFrequency === NO_FREQUENCY) {
      return SUPPRESSED_TERMS;
    }

    const fee = parseMoney(value.trialFee);
    const period = parseWholeNumber(value.trialPeriod);

    if (fee === null || period === null) {
      return SUPPRESSED_TERMS;
    }

    return { fee, period, frequency: value.trialFrequency };
  }

  // -------------------------------------------------------------------------
  // FAILURE HANDLING
  // -------------------------------------------------------------------------

  private clearFailure(): void {
    this.failure.set(null);
    this.failureFallbackMessage.set('');
  }

  /**
   * Turns a refusal into something the user can act on. The API answers every refusal with an RFC 7807
   * document, so the document is the contract and is read rather than guessed at.
   *
   * @param failure The store's record of the refusal, whose summary carries the severity.
   * @param outcome This screen's own sentence for what did not happen.
   */
  private reportFailure(failure: RoleStoreFailure, outcome: string): void {
    const problem: ProblemDetails | null = failure.problem;
    const status: number | null = failure.status;

    this.failure.set(problem);

    if (problem !== null) {
      this.applyFieldErrors(problem);
    }

    // The one classification, resolved once by the shared function and consumed here.
    const severity: NotificationSeverity = failure.summary.severity;

    // ⚠ THE TWO `409`s ARE NOT THE SAME FAILURE and must not share a response. A duplicate name is
    // corrected in a field; a stale read cannot be corrected in the form at all, because every subsequent
    // save carries the same refused marker.
    const staleRead: boolean =
      status === CONFLICT && failureCode(problem) === CONCURRENCY_CONFLICT_CODE;

    if (staleRead) {
      this.staleRead.set(true);
    }

    let bannerFallback: string;
    if (status === UNAUTHORIZED || status === FORBIDDEN) {
      bannerFallback = ACCESS_DENIED_MESSAGE;
    } else if (staleRead) {
      bannerFallback = CONCURRENCY_CONFLICT_MESSAGE;
    } else if (status === CONFLICT) {
      bannerFallback = DUPLICATE_ROLE_MESSAGE;
    } else {
      bannerFallback = outcome;
    }

    this.failureFallbackMessage.set(bannerFallback);

    this.notifications.notify(severity, outcome, failure.summary.supportReference);
  }

  /**
   * Places server-reported field messages on the controls they belong to. The keys are the API's own
   * member names.
   *
   * @param problem The refusal document.
   */
  private applyFieldErrors(problem: ProblemDetails): void {
    const fieldErrors = problemDetailsFieldErrors(problem);
    const controls = this.form.controls;

    for (const field of Object.keys(controls)) {
      const messages: readonly string[] | undefined = fieldErrors[field];
      if (messages === undefined || messages.length === 0) {
        continue;
      }
      const first: string | undefined = messages[0];
      if (first === undefined) {
        continue;
      }
      const control = controls[field as keyof RoleFormModel];
      control.setErrors({ ...(control.errors ?? {}), serverError: first });
      control.markAsTouched();
    }
  }
}
