/**
 * Security-role creation and editing.
 *
 * ONE component serves BOTH routes. `features/role/role.routes.ts` points `'new'` and
 * `':roleId'` at the same `loadComponent`, exactly as the legacy screen served both jobs from
 * one control: `Website/admin/Security/EditRoles.ascx.vb` initialises `RoleID = -1` (L42) and
 * branches on `If RoleID <> -1` (L131) to decide whether it is editing or adding.
 *
 * MIGRATION: mode is derived from the PRESENCE of the `roleId` route input, never from its
 * numeric value. `dbo.Roles.RoleID` is `IDENTITY (0, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115`), so `0` is a
 * REAL role id and must open the EDIT form. The legacy could use `-1` as its "adding" marker
 * because `-1` was simultaneously `Null.NullInteger`
 * (`Library/Components/Shared/Null.vb:L41-L45`) and outside the identity range; a route param is
 * either supplied or it is not, which is a cleaner discriminator and is the one used here.
 *
 * LEGACY SOURCES (read-only references; none is modified by this work)
 * - `Website/admin/Security/editroles.ascx` — the field set, the nine validators, the four
 *   command buttons and their `CausesValidation` flags.
 * - `Website/admin/Security/EditRoles.ascx.vb` — load, guard, parse, save, delete and cancel
 *   workflow.
 * - `Website/admin/Security/App_LocalResources/EditRoles.ascx.resx` — the authoritative label,
 *   help and validation wording.
 * - `Website/App_GlobalResources/SharedResources.resx` — `GlobalRoles`, `DeleteItem`, and the
 *   three command-button captions that carry no local key.
 * - `Website/admin/Security/Roles.ascx.vb` — the sibling list screen, whose `FormatPrice` and
 *   `FormatPeriod` establish that a sentinel renders as the empty string.
 * - `Website/admin/Security/AccessDenied.ascx.vb` — permission refusals are a WARNING.
 * - `Library/Components/Security/Roles/RoleInfo.vb` — the fifteen role properties and their
 *   types.
 * - `Library/Components/Security/Roles/RoleController.vb` — the six billing-frequency codes.
 * - `Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb` — the asymmetric
 *   `AddRole` / `UpdateRole` signatures that prove a role name is immutable.
 * - `Website/release.config` — `<compilation debug="false" strict="false">` at L125, the Option
 *   Strict asymmetry that is the root cause of the untyped-comparison defect below.
 *
 * THE FIVE MEASURED LEGACY DEFECTS, each annotated at its point of use rather than repaired
 * silently:
 *   1. Four `CompareValidator`s omit `Type=`, so they compared culture-sensitive STRINGS.
 *   2. Two inline `ErrorMessage` strings are stale and mutually transposed.
 *   3. The paid/free decision compares FORMATTED MONEY STRINGS.
 *   4. The protected-role test uses an unparenthesised, non-short-circuiting `Or`.
 *   5. The payment-processor warning contradicts its own explanatory comment.
 *
 * Business logic lives here, not in `RoleService`: the service is a typed transport and holds no
 * rules. The conditional-parse resolution, the mode logic and every validator are this
 * component's responsibility.
 */
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import type { Signal, WritableSignal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { Router } from '@angular/router';
import {
  isProblemDetails,
  problemDetailsFieldErrors,
  problemDetailsMessage,
} from '../../../core/models/problem-details.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  BillingFrequency,
  CreateRoleRequest,
  Role,
  RoleGroup,
  UpdateRoleRequest,
} from '../../../core/models/role.model';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleService } from '../../../core/services/role.service';
import { RoleStore } from '../../../core/state/role.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

// ---------------------------------------------------------------------------
// FORM SHAPE
// ---------------------------------------------------------------------------

/**
 * The typed form model.
 *
 * Every control is declared here so that `FormGroup<RoleFormModel>` yields a fully typed
 * `getRawValue()` rather than a `Partial<…>`, and so that `reset()` returns each control to its
 * declared initial value instead of to `null`. That is the whole reason every control below is
 * constructed with `{ nonNullable: true }`.
 *
 * MIGRATION: the four money and period controls are typed `string`, not `number | null`. The
 * legacy controls were `asp:TextBox`es holding raw text
 * (`editroles.ascx:L89-L90,L104-L105,L122-L123,L136-L137`), and an empty textbox is a legal,
 * VALID state for all four (see {@link currencyDataType}). A `string` control represents "empty"
 * natively as `''`, keeps the browser's `maxlength` truncation meaningful, and lets the raw text
 * be validated before it is parsed — which is exactly the legacy sequencing. A numeric control
 * would have to smuggle "empty" through `null`, defeating `nonNullable`. The single parse happens
 * once, at submit, through {@link parseMoney} and {@link parseWholeNumber}.
 */
export interface RoleFormModel {
  /** `txtRoleName` / `lblRoleName` — `editroles.ascx:L27-L29`. Immutable once created. */
  roleName: FormControl<string>;
  /** `txtDescription` — `editroles.ascx:L39-L40`. Carries NO legacy validator. */
  description: FormControl<string>;
  /** `cboRoleGroups` — `editroles.ascx:L47`. `null` is the ungrouped, "global" role. */
  roleGroupId: FormControl<number | null>;
  /** `chkIsPublic` — `editroles.ascx:L56`. */
  isPublic: FormControl<boolean>;
  /** `chkAutoAssignment` — `editroles.ascx:L64`. */
  autoAssignment: FormControl<boolean>;
  /** `txtServiceFee` — `editroles.ascx:L89-L90`. Optional. */
  serviceFee: FormControl<string>;
  /** `txtBillingPeriod` — `editroles.ascx:L104-L105`. Optional. */
  billingPeriod: FormControl<string>;
  /** `cboBillingFrequency` — `editroles.ascx:L106-L107`. Defaults to `'N'`. */
  billingFrequency: FormControl<BillingFrequency>;
  /** `txtTrialFee` — `editroles.ascx:L122-L123`. Optional. */
  trialFee: FormControl<string>;
  /** `txtTrialPeriod` — `editroles.ascx:L136-L137`. Optional. */
  trialPeriod: FormControl<string>;
  /** `cboTrialFrequency` — `editroles.ascx:L138-L139`. Defaults to `'N'`. */
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
 * The six billing-frequency codes, in the order the legacy `CodeFrequency` lookup seeded them.
 *
 * These are load-bearing persisted data, not presentation. `dbo.Roles.BillingFrequency` and
 * `dbo.Roles.TrialFrequency` are `char(1)` columns constrained by `FK_Roles_CodeFrequency`, and
 * `Library/Components/Security/Roles/RoleController.vb:L540-L546` switches on the raw characters:
 * `'N'` leaves the expiry unbounded, `'O'` sets 9999-12-31, and `'D'`, `'W'`, `'M'`, `'Y'` add
 * days, weeks, months and years. The code is never renamed, case-folded, aliased or turned into
 * an integer; only the caption beside it is presentation.
 *
 * MIGRATION: the legacy filled both frequency selects FROM THE DATABASE —
 * `EditRoles.ascx.vb:L116-L125` calls
 * `ListController.GetListEntryInfoCollection("Frequency", "")` and data-binds the result. The
 * `Library/Components/Lists` subsystem is out of scope, so no frequency lookup endpoint exists
 * and none is invented. The vocabulary is closed and fixed by a foreign key, so declaring it
 * locally loses nothing; the captions are the ones the API's own refusal message names.
 */
export const BILLING_FREQUENCY_OPTIONS: readonly RoleFormOption<BillingFrequency>[] =
  Object.freeze<readonly RoleFormOption<BillingFrequency>[]>([
    { value: 'N', label: 'None' },
    { value: 'O', label: 'One Time' },
    { value: 'D', label: 'Day' },
    { value: 'W', label: 'Week' },
    { value: 'M', label: 'Month' },
    { value: 'Y', label: 'Year' },
  ]);

/**
 * The frequency that means "no recurring term".
 *
 * `EditRoles.ascx.vb:L119` and `:L125` both select `"N"` on first load, and `:L214` and `:L224`
 * both initialise the outgoing frequency to `"N"`. It is also the third conjunct of each
 * submission gate at `:L216` and `:L226`: a frequency still sitting at `'N'` suppresses the whole
 * group.
 */
export const NO_FREQUENCY: BillingFrequency = 'N';

/**
 * The caption for the ungrouped choice in the role-group select.
 *
 * Measured, not paraphrased: `EditRoles.ascx.vb:L75` calls the one-argument
 * `Localization.GetString("GlobalRoles")`, which resolves from global resources, and
 * `Website/App_GlobalResources/SharedResources.resx` holds `GlobalRoles.Text` as
 * `< Global Roles >` — angle brackets and the spaces inside them included.
 */
export const GLOBAL_ROLES_LABEL = '< Global Roles >';

/**
 * The value bound to {@link GLOBAL_ROLES_LABEL}.
 *
 * MIGRATION: `null`, NOT `-1`. The legacy select carried the string `"-1"`
 * (`EditRoles.ascx.vb:L75`) and the membership provider then wrapped the argument in
 * `GetNull(RoleGroupId)` (`SqlDataProvider.vb:L235,L243`) so that `-1` reached SQL Server as
 * `NULL`. The target does that collapse once, at the contract boundary: `RoleGroupId` is `int?`
 * on both request objects and the mapper records that `-1`, "Global Roles" and SQL `NULL` are one
 * value of which `null` is the honest representation. Sending `-1` instead would bypass the
 * legacy `GetNull` translation, which the target does not reproduce, and reach the foreign key as
 * a literal `-1`.
 *
 * This is the one place where `-1` means "absent". It emphatically does NOT mean that elsewhere:
 * `dbo.RoleGroups.RoleGroupID` is `IDENTITY (0, 1)`, so group `0` is a real group, and a role id
 * of `0` is a real role.
 */
export const UNGROUPED_ROLE_GROUP: number | null = null;

/**
 * The legacy ungrouped marker, recognised on the way IN only.
 *
 * A response that still carries `-1` — from a producer that has not collapsed the sentinel —
 * selects `< Global Roles >` just as `null` does, because the two denote the same state. Nothing
 * ever writes this value back out.
 */
const LEGACY_UNGROUPED_ROLE_GROUP = -1;

/**
 * `Null.NullInteger`, which a period column uses to mean "absent".
 *
 * `Library/Components/Shared/Null.vb:L41-L45`. `Roles.ascx.vb:L152-L161` proves the display rule:
 * `FormatPeriod` starts at `Null.NullString` and returns it unchanged when the period equals this
 * sentinel, so an absent period renders as the EMPTY STRING and never as `-1`.
 */
const LEGACY_ABSENT_PERIOD = -1;

/**
 * `Null.NullSingle`, which a money column uses to mean "absent".
 *
 * `Library/Components/Shared/Null.vb` returns `Single.MinValue`. `Roles.ascx.vb:L175-L184` proves
 * the display rule: `FormatPrice` starts at `Null.NullString` and returns it unchanged when the
 * price equals this sentinel, so an absent fee renders as the EMPTY STRING and never as `0`. The
 * comparison is a threshold rather than an equality because a single-precision sentinel widened
 * to a double and round-tripped through JSON need not compare exactly equal.
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
 * The icon path limit.
 *
 * MIGRATION: `ctlIcon` is a `dnn:Url` picker (`editroles.ascx:L169-L170`) and carried NO length
 * limit and NO validator of any kind, so this rule has no legacy counterpart. It is added because
 * the API refuses a longer value outright, and refusing it here reaches the same outcome without a
 * round trip. It cannot reject anything the system as a whole would have accepted.
 */
const ICON_FILE_MAX_LENGTH = 100;

// ---------------------------------------------------------------------------
// MESSAGES — the wording a user actually saw
// ---------------------------------------------------------------------------

/*
 * MIGRATION — DEFECT 2, and the mechanical proof that settles it.
 *
 * Every message below is taken from the `.Text` VALUE in
 * `Website/admin/Security/App_LocalResources/EditRoles.ascx.resx`, never from the inline
 * `ErrorMessage` attribute in `editroles.ascx`. The reason is not preference, it is how ASP.NET
 * rendered them: a validator's `Text` renders INLINE beside the field, while its `ErrorMessage`
 * renders ONLY inside an `<asp:ValidationSummary>` — and there is not one `<asp:ValidationSummary>`
 * anywhere in the in-scope admin markup. The nine local resource keys are all `val*.Text`.
 *
 * It follows that THE INLINE `ErrorMessage` VALUES WERE NEVER SHOWN TO A USER AT ALL, and two of
 * them had silently rotted into each other — a single copy-paste transposition:
 *
 *   valBillingPeriod2  resx `.Text` "…Must Be Greater Than Zero"            operator GreaterThan      -> resx agrees
 *                      inline       "…Must Be Greater Than or Equal to Zero"                          -> stale
 *   valTrialFee2       resx `.Text` "…Must Be Greater Than or Equal to Zero" operator GreaterThanEqual -> resx agrees
 *                      inline       "…Must Be Greater Than Zero"                                      -> stale
 *
 * The `Operator` arbitrates, and in both cases it vindicates the resource file. The refined
 * standing rule is therefore narrower and more useful than "never trust inline text": never trust
 * an inline `Text` / `ErrorMessage` WHERE A LOCAL RESX KEY EXISTS. `cmdUpdate`, `cmdCancel` and
 * `cmdDelete` have no local key, fall back to the global resources, and their captions there are
 * identical to the inline ones — so those are not stale and are used as they stand.
 *
 * A leading `<br>` is stripped from every value. Resource text is untrusted markup — one value in
 * this admin tree is a live remote `<script>` block — so no message is ever rendered as HTML.
 */

/** `valRoleName.Text`, `<br>` stripped. */
const ROLE_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/** `valServiceFee1.Text`, `<br>` stripped. The `Type="Currency"` data-type refusal. */
const SERVICE_FEE_INVALID_MESSAGE = 'Service Fee Value Entered Is Not Valid';

/** `valServiceFee2.Text`, `<br>` stripped. Resource and inline agree here. */
const SERVICE_FEE_NEGATIVE_MESSAGE = 'Service Fee Must Be Greater Than or Equal to Zero';

/** `valBillingPeriod1.Text`, `<br>` stripped. The `Type="Integer"` data-type refusal. */
const BILLING_PERIOD_INVALID_MESSAGE = 'Billing Period Value Entered Is Not Valid';

/**
 * `valBillingPeriod2.Text`, `<br>` stripped.
 *
 * MIGRATION: the inline attribute reads "…Greater Than or Equal to Zero". It is STALE and was
 * never displayed. `Operator="GreaterThan"` (`editroles.ascx:L114`) confirms the resource wording.
 * Note that the API's own refusal for this field carries the stale sentence instead; a message
 * that arrives from the server is surfaced verbatim rather than rewritten, so the two coexist
 * without either being falsified.
 */
const BILLING_PERIOD_NOT_POSITIVE_MESSAGE = 'Billing Period Must Be Greater Than Zero';

/** `valTrialFee1.Text`, `<br>` stripped. */
const TRIAL_FEE_INVALID_MESSAGE = 'Trial Fee Value Entered Is Not Valid';

/**
 * `valTrialFee2.Text`, `<br>` stripped.
 *
 * MIGRATION: the inline attribute reads "…Greater Than Zero". It is STALE and was never
 * displayed — the transposed twin of the billing-period message above.
 * `Operator="GreaterThanEqual"` (`editroles.ascx:L128`) confirms the resource wording, and a zero
 * trial fee is a real, free trial.
 */
const TRIAL_FEE_NEGATIVE_MESSAGE = 'Trial Fee Must Be Greater Than or Equal to Zero';

/** `valTrialPeriod1.Text`, `<br>` stripped. */
const TRIAL_PERIOD_INVALID_MESSAGE = 'Trial Period Value Entered Is Not Valid';

/** `valTrialPeriod2.Text`, `<br>` stripped. Resource and inline agree here. */
const TRIAL_PERIOD_NOT_POSITIVE_MESSAGE = 'Trial Period Must Be Greater Than Zero';

/**
 * `DuplicateRole.Text`, verbatim.
 *
 * `EditRoles.ascx.vb:L256` raised it at `ModuleMessageType.RedError` after its own
 * lookup-then-insert check at `:L252` found a name collision.
 */
const DUPLICATE_ROLE_MESSAGE = 'A role with the same name already exists. The role was not added.';

/**
 * `DeleteItem.Text` from the global resources, verbatim.
 *
 * `EditRoles.ascx.vb:L112` attached it to the delete button with
 * `ClientAPI.AddButtonConfirm(cmdDelete, Localization.GetString("DeleteItem"))`.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `AccessDenied.Text`, verbatim, from the sibling access-denied screen's resources. */
const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';

/** `ControlTitle_edit.Text`, verbatim. */
const EDIT_TITLE = 'Edit Security Roles';

/**
 * The create-mode heading.
 *
 * MIGRATION: `EditRoles.ascx.resx` has NO `ControlTitle_add` key, so the legacy add form showed
 * the same "Edit Security Roles" heading as the edit form. The caption is taken instead from the
 * list screen's own action that reaches this form — `Roles.ascx.resx` `AddContent.Action` = "Add
 * New Role" — because a heading that says "Edit" above a blank creation form is a defect, and the
 * substituted wording is the site's own, not invented.
 */
const ADD_TITLE = 'Add New Role';

/** Confirmation after a successful create. Legacy severity `GreenSuccess`. */
const ROLE_CREATED_MESSAGE = 'The role was created.';

/** Confirmation after a successful update. Legacy severity `GreenSuccess`. */
const ROLE_UPDATED_MESSAGE = 'The role was updated.';

/** Confirmation after a successful delete. Legacy severity `GreenSuccess`. */
const ROLE_DELETED_MESSAGE = 'The role was deleted.';

/**
 * Shown when the requested role does not exist.
 *
 * `EditRoles.ascx.vb:L170-L172` treated this as "a security violation attempt to access item not
 * related to this Module" and redirected to the Security Roles page without telling the user
 * anything. The redirect is preserved; a message is added because a silent bounce is
 * indistinguishable from a broken link.
 */
const ROLE_NOT_FOUND_MESSAGE = 'That role could not be found.';

/** Fallback when a refusal carries neither a `detail` nor a `title`. */
const SAVE_FAILED_MESSAGE = 'The role could not be saved.';

/** Fallback when a delete refusal carries neither a `detail` nor a `title`. */
const DELETE_FAILED_MESSAGE = 'The role could not be deleted.';

/** Fallback when the role could not be read. */
const LOAD_FAILED_MESSAGE = 'The role could not be loaded.';

/** Last-resort per-field message, matching the sibling screens' wording. */
const GENERIC_FIELD_MESSAGE = 'Correct this field and try again.';

/** The route this screen returns to, mirroring `Response.Redirect(NavigateURL())`. */
const ROLE_LIST_ROUTE = '/roles';

// ---------------------------------------------------------------------------
// HTTP STATUS CODES THIS SCREEN DISTINGUISHES
// ---------------------------------------------------------------------------

/** The caller is not authenticated. */
const UNAUTHORIZED = 401;

/** The caller is authenticated but not permitted. Surfaced as a WARNING, never an error. */
const FORBIDDEN = 403;

/** The role does not exist. Handled exactly as the legacy "item not related" branch was. */
const NOT_FOUND = 404;

/** A name collision. The legacy detected this itself, before saving. */
const CONFLICT = 409;

// ---------------------------------------------------------------------------
// VALIDATORS — every one of them empty-tolerant
// ---------------------------------------------------------------------------

/*
 * MIGRATION — DEFECT 1: the four untyped comparisons, corrected from lexical to numeric.
 *
 * `editroles.ascx` places TWO `CompareValidator`s on each of the four money and period fields.
 * The first of each pair declares `Type="Currency"` or `Type="Integer"` with
 * `Operator="DataTypeCheck"`. The SECOND of each pair — `valServiceFee2` (L93-L96),
 * `valBillingPeriod2` (L111-L114), `valTrialFee2` (L125-L128) and `valTrialPeriod2` (L143-L146) —
 * OMITS `Type=` entirely, and the ASP.NET default is `Type="String"`.
 *
 * The comparison was therefore a culture-sensitive STRING comparison, not a numeric one, and it
 * gave wrong answers in both directions. Culture-sensitive collation treats the hyphen as
 * ignorable punctuation, so "-5" sorts ABOVE "0" and a NEGATIVE FEE PASSED a "greater than or
 * equal to zero" test. Ordinary lexical ordering does the rest of the damage: "9" sorts above
 * "10", so a smaller period passed a "greater than zero" test for entirely the wrong reason,
 * while a legitimate "10" would have been judged against "0" one character at a time.
 *
 * ROOT CAUSE: the Option Strict asymmetry. The class library compiled with Option Strict ON, but
 * `Website/release.config:L125` declares `<compilation debug="false" strict="false">`, so the
 * admin pages compiled with Option Strict OFF. A comparison between a numeric-looking string and
 * the literal "0" raised nothing at compile time.
 *
 * THE TARGET COMPARES NUMBERS. That is a deliberate, documented divergence from measured legacy
 * behaviour — it rejects the negative fees the legacy accepted — and it is made because carrying a
 * collation accident into new code would corrupt persisted money. It also aligns the client with
 * the API, which already compares numerically.
 *
 * CRITICAL AND SEPARATE FROM THE DEFECT: an ASP.NET `CompareValidator` SUCCEEDS ON AN EMPTY
 * INPUT. That is precisely why every genuinely required field also carried a
 * `RequiredFieldValidator`, and `txtRoleName` is the ONLY field on this screen that has one.
 * ALL FOUR MONEY AND PERIOD FIELDS ARE THEREFORE OPTIONAL. Adding `Validators.required` to any of
 * them would break functional parity outright, and adding `Validators.min` would report a
 * different error key and a different message. Every validator below returns `null` for an empty
 * or whitespace-only value, which is why each is written by hand rather than composed from the
 * built-ins. The API agrees independently: each of its four numeric rules is guarded by
 * `.When(request => request.X.HasValue)`.
 */

/** True when a control holds nothing a validator should judge. */
function isBlank(value: unknown): boolean {
  return typeof value !== 'string' || value.trim().length === 0;
}

/**
 * Recognises the money forms `Type="Currency"` accepted, including the grouped form this screen
 * itself writes.
 *
 * Grouping matters: the legacy populated the field with `Format(fee, "#,##0.00")`
 * (`EditRoles.ascx.vb:L147,L155`), which emits a thousands separator, so a loaded paid role showed
 * text like `1,234.56`. A validator that rejected the separator would mark a freshly loaded,
 * untouched form invalid. Groups are accepted in any position rather than strictly every three
 * digits, matching `Currency`'s own tolerance.
 */
const MONEY_PATTERN = /^[+-]?(?:\d+|\d{1,3}(?:,\d+)*)(?:\.\d{1,2})?$/;

/** Recognises what `Type="Integer"` accepted: an optional sign and digits, with no separators. */
const WHOLE_NUMBER_PATTERN = /^[+-]?\d+$/;

/**
 * Parses a money field, returning `null` when it holds nothing parseable.
 *
 * MIGRATION: replaces `Single.Parse(txtServiceFee.Text)` and `Single.Parse(txtTrialFee.Text)`
 * (`EditRoles.ascx.vb:L217,L227`). The legacy used `Parse`, not `TryParse`, so a value that slipped
 * past the defective validators threw, and the only thing standing between a user and an
 * unhandled exception was `Catch exc As Exception` at `:L271`. Returning `null` instead removes
 * that path entirely; the caller decides what an unparseable value means, and no arithmetic ever
 * runs on a coerced zero.
 */
function parseMoney(raw: string): number | null {
  const text = raw.trim();
  if (text.length === 0 || !MONEY_PATTERN.test(text)) {
    return null;
  }
  const parsed = Number.parseFloat(text.replace(/,/g, ''));
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * Parses a period field, returning `null` when it holds nothing parseable.
 *
 * MIGRATION: replaces `Integer.Parse(txtBillingPeriod.Text)` and
 * `Integer.Parse(txtTrialPeriod.Text)` (`EditRoles.ascx.vb:L218,L228`), with the same removal of
 * the throwing path. The radix is explicit and the result is checked with `Number.isInteger`, so
 * neither a leading zero nor a fractional tail can be silently absorbed.
 */
function parseWholeNumber(raw: string): number | null {
  const text = raw.trim();
  if (text.length === 0 || !WHOLE_NUMBER_PATTERN.test(text)) {
    return null;
  }
  const parsed = Number.parseInt(text, 10);
  return Number.isInteger(parsed) ? parsed : null;
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
 * `valTrialFee2`.
 *
 * A value that will not parse yields `null` here, deferring to the data-type validator on the same
 * control so that exactly one message is shown rather than two contradictory ones. The legacy
 * reached the same single-message outcome by accident, because its lexical comparison happened to
 * pass for most non-numeric text.
 *
 * Zero is ADMITTED. A zero service fee is a real, free role, and
 * `Library/Components/Security/Roles/RoleController.vb:L494` uses `userRole.ServiceFee > 0.0` as
 * the paid discriminator, so zero must survive as itself and must never be coalesced away.
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
 * Strictly positive: zero is REFUSED, which is what `GreaterThan` meant, and which is why the
 * resource wording for the billing period says "Greater Than Zero" while its stale inline twin
 * said "or Equal to".
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


// ---------------------------------------------------------------------------
// DISPLAY AND SUBMISSION HELPERS
// ---------------------------------------------------------------------------

/**
 * Formats money the way this screen formatted it.
 *
 * MIGRATION: two different money formats coexist across the two role screens, and each is kept
 * faithful to its own screen rather than harmonised. THIS screen used `Format(fee, "#,##0.00")`
 * (`EditRoles.ascx.vb:L146,L147,L155`) — WITH a thousands separator. The sibling list screen's
 * `FormatPrice` used `price.ToString("##0.00")` (`Roles.ascx.vb:L182`) — WITHOUT one. The grouped
 * form is reproduced here.
 *
 * MIGRATION: the legacy resolved `#,##0.00` against the SERVER's current culture, so the
 * separators it emitted varied with deployment. A fixed locale is used instead, because the value
 * written here is read straight back by {@link parseMoney} on submit and a locale that swapped the
 * roles of `.` and `,` would make that round trip lossy.
 *
 * SENTINEL: an absent fee renders as the EMPTY STRING, never as `0` and never as the raw
 * `Single.MinValue`. `Roles.ascx.vb:L175-L184` establishes the rule.
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
 * Formats a period the way the legacy formatted it: `objRoleInfo.BillingPeriod.ToString`
 * (`EditRoles.ascx.vb:L148,L156`) — a bare integer with no separators.
 *
 * SENTINEL: an absent period renders as the EMPTY STRING, matching `FormatPeriod`
 * (`Roles.ascx.vb:L152-L161`), which returns `Null.NullString` when the period is
 * `Null.NullInteger`.
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
 * Narrows an incoming frequency to the closed vocabulary, falling back to `'N'`.
 *
 * The legacy did this by lookup: `cboBillingFrequency.Items.FindByValue(...)` guarded by
 * `If Not … Is Nothing` (`EditRoles.ascx.vb:L149-L152,L157-L160`), so a value with no matching
 * option left the select on its `'N'` default. That behaviour is reproduced exactly.
 *
 * @param value The frequency as the API reported it, possibly absent.
 * @returns A code that certainly exists among the options.
 */
function coerceFrequency(value: BillingFrequency | null | undefined): BillingFrequency {
  if (value === null || value === undefined) {
    return NO_FREQUENCY;
  }
  return BILLING_FREQUENCY_OPTIONS.some((option) => option.value === value)
    ? value
    : NO_FREQUENCY;
}

/**
 * Narrows an incoming role-group id to a value the select can hold.
 *
 * Reproduces `EditRoles.ascx.vb:L141-L144`: select the option whose value matches, and when no
 * option matches leave the select on `< Global Roles >`.
 *
 * MIGRATION: `null` AND the legacy `-1` both mean ungrouped and both resolve to
 * {@link UNGROUPED_ROLE_GROUP}. Group `0` is a REAL group — `dbo.RoleGroups.RoleGroupID` is
 * `IDENTITY (0, 1)` — so it is matched like any other and never treated as absent.
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
 * The request contracts type every string member as `string | null`, and `Null.NullString` is the
 * empty string, so "" and absent are the same legacy value. `null` is the contract's
 * representation of it.
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

/**
 * Reads an HTTP status from a rejected request without asserting a shape.
 *
 * @param error Whatever the transport rejected with.
 * @returns The status, or `null` when the rejection carries none.
 */
function statusOf(error: unknown): number | null {
  if (typeof error !== 'object' || error === null || !('status' in error)) {
    return null;
  }
  const held: unknown = (error as { status: unknown }).status;
  return typeof held === 'number' ? held : null;
}

/**
 * Extracts an RFC 7807 document from a rejected request.
 *
 * The API answers every refusal with `application/problem+json`, and the error interceptor leaves
 * the parsed body on `error`. Anything that is not a problem document yields `null` so that the
 * caller falls back to its own wording rather than rendering a transport object.
 *
 * @param error Whatever the transport rejected with.
 * @returns The problem document, or `null`.
 */
function problemOf(error: unknown): ProblemDetails | null {
  if (typeof error !== 'object' || error === null || !('error' in error)) {
    return null;
  }
  const body: unknown = (error as { error: unknown }).error;
  return isProblemDetails(body) ? body : null;
}

/** The resolved outcome of the billing group, or of the trial group. */
interface ResolvedTerms {
  readonly fee: number;
  readonly period: number;
  readonly frequency: BillingFrequency;
}

/**
 * The values a suppressed group submits.
 *
 * MIGRATION: measured at `EditRoles.ascx.vb:L212-L214` and `:L222-L224`. THE PERIOD DEFAULT IS
 * `1`, NOT `0` — a detail that is easy to lose and that the API's strictly-positive period rule
 * would reject if it were lost. The fee default is `0` and the frequency default is `'N'`.
 */
const SUPPRESSED_TERMS: ResolvedTerms = Object.freeze({
  fee: 0,
  period: 1,
  frequency: NO_FREQUENCY,
});


// ---------------------------------------------------------------------------
// COMPONENT
// ---------------------------------------------------------------------------

@Component({
  selector: 'app-role-form',
  standalone: true,
  imports: [
    ReactiveFormsModule,
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
  // -------------------------------------------------------------------------
  // ROUTE INPUTS
  // -------------------------------------------------------------------------

  /**
   * The role being edited, or `undefined` on the creation route.
   *
   * The spelling is load-bearing and is exactly `roleId`, with a single lower-case `d`.
   * `app.config.ts` enables `withComponentInputBinding()`, which binds a route parameter onto a
   * component input OF THE SAME NAME, and `features/role/role.routes.ts` declares the parameter as
   * `:roleId`. Any other spelling would bind nothing, yield `undefined`, compile without complaint
   * and fail only at run time.
   *
   * The value arrives as a STRING, because that is what a URL segment is. It is converted exactly
   * once, by {@link roleKey}, through a guarded parse.
   *
   * No input on this component is named `permission`. `withComponentInputBinding()` also binds
   * route `data` keys onto same-named inputs, and both of this component's routes carry
   * `data: { permission: 'PortalAdministrator' }` for the route guard, so such an input would be
   * silently overwritten with the guard's policy string.
   */
  public readonly roleId = input<string | undefined>(undefined);

  /**
   * The portal's administrator role id, when a caller can supply it.
   *
   * MIGRATION — THE MEASURED GAP, stated plainly. `EditRoles.ascx.vb:L174-L182` guards three
   * things on the identity of two portal-level roles:
   *
   *   L174-L178  `If RoleID = PortalSettings.AdministratorRoleId Or RoleID = PortalSettings.RegisteredRoleId`
   *              then hide Delete, hide Update, and `ActivateControls(False)`.
   *   L180-L182  `If RoleID = PortalSettings.RegisteredRoleId` then additionally hide Manage.
   *
   * Nothing on the role itself discriminates a protected role: `RoleInfo.vb` has no `IsSystem`,
   * `SystemRole` or `IsAdmin` member of any kind, and the target's `Role` contract likewise
   * carries no such flag. `AdministratorRoleId` and `RegisteredRoleId` are PORTAL-scoped, and the
   * only place the target exposes them is the portal detail contract, which this screen may not
   * read — a portal-settings call is outside its endpoint boundary, and the portal model is not
   * among its dependencies.
   *
   * The guards are therefore expressed as optional inputs rather than dropped. When a caller
   * supplies them the measured behaviour is reproduced exactly; when it does not — which is the
   * case today — the form stays fully editable and the API's own refusal governs, arriving as a
   * `403` that is surfaced as a warning. No role id is hardcoded, no role NAME is consulted, and
   * no endpoint is invented.
   */
  public readonly administratorRoleId = input<number | null>(null);

  /**
   * The portal's registered-users role id, when a caller can supply it.
   *
   * See {@link administratorRoleId} for why this is an input. This role is the stricter of the
   * two: it is protected from deletion and update AND its membership screen is unreachable,
   * because every authenticated user holds it.
   */
  public readonly registeredRoleId = input<number | null>(null);

  /**
   * Whether the portal has a configured payment processor.
   *
   * MIGRATION — DEFECT 5, reproduced rather than repaired. `EditRoles.ascx.vb:L104-L109` runs
   * OUTSIDE the `If Page.IsPostBack = False` block, so it re-evaluates on every load, and reads:
   *
   *   `If (objPortalInfo Is Nothing OrElse String.IsNullOrEmpty(objPortalInfo.ProcessorUserId)) Then`
   *   `    'Warn users about fee based roles if we have a Processor Id`
   *   `    lblProcessorWarning.Visible = True`
   *
   * The comment says the warning appears when a processor IS configured; the code shows it when
   * the processor is NOT configured. The CODE is the behaviour and the code is also the sensible
   * reading — the warning tells an administrator to configure a processor before charging for a
   * role — so the code is what is reproduced, and the contradiction is recorded here instead of
   * being tidied away.
   *
   * `ProcessorUserId` is a PORTAL property and is unobtainable from this screen's endpoints, so
   * this input defaults to `false`, matching the markup's own `visible="false"`
   * (`editroles.ascx:L80`). No portal-settings call is invented to populate it.
   */
  public readonly paymentProcessorConfigured = input<boolean>(false);

  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------

  private readonly roleService = inject(RoleService);
  private readonly roleStore = inject(RoleStore);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);

  /**
   * Bounds every request this screen starts to this screen's own lifetime.
   *
   * Not a stylistic preference. Each of the four requests below ends by navigating, and a response
   * that arrives after the user has already left would yank them back — a save confirmed against a
   * screen that no longer exists would redirect them to the role list from wherever they had gone.
   * The legacy could not have this problem because a postback was synchronous and the page was
   * being replaced anyway.
   */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------
  // LOCAL STATE
  // -------------------------------------------------------------------------

  /** The role currently loaded, or `null` in creation mode and before the first response. */
  private readonly loadedRole: WritableSignal<Role | null> = signal<Role | null>(null);

  /** True while the role is being read. */
  private readonly loadingRole: WritableSignal<boolean> = signal<boolean>(false);

  /** True while a create, update or delete is in flight. */
  private readonly savingRole: WritableSignal<boolean> = signal<boolean>(false);

  /** The last refusal, as an RFC 7807 document, for the shared error banner. */
  private readonly failure: WritableSignal<ProblemDetails | null> = signal<ProblemDetails | null>(
    null,
  );

  /** True once the user has asked to delete and before the dialog is settled. */
  private readonly deletePending: WritableSignal<boolean> = signal<boolean>(false);

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The role form.
   *
   * `roleName` carries `Validators.required` here because that is the CREATION state, which is
   * what the legacy rendered when it had no role id. {@link applyMode} removes the rule in edit
   * mode, mirroring `valRoleName.Enabled = False` at `EditRoles.ascx.vb:L134`.
   *
   * `description` carries `Validators.maxLength` and NOTHING ELSE, because `txtDescription`
   * (`editroles.ascx:L39-L40`) carried no validator at all — only the browser-side `MaxLength`.
   *
   * Each `maxLength` rule is declared here as well as being expressed as the template's
   * `maxlength` attribute, because the legacy `MaxLength` truncated in the browser and a rule is
   * needed for a value that arrives by any other route.
   */
  protected readonly form = new FormGroup<RoleFormModel>({
    roleName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(ROLE_NAME_MAX_LENGTH)],
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
      ],
    }),
    billingPeriod: new FormControl('', {
      nonNullable: true,
      validators: [
        wholeNumberDataType(BILLING_PERIOD_INVALID_MESSAGE),
        wholeNumberPositive(BILLING_PERIOD_NOT_POSITIVE_MESSAGE),
      ],
    }),
    billingFrequency: new FormControl<BillingFrequency>(NO_FREQUENCY, { nonNullable: true }),
    trialFee: new FormControl('', {
      nonNullable: true,
      validators: [
        currencyDataType(TRIAL_FEE_INVALID_MESSAGE),
        moneyNotNegative(TRIAL_FEE_NEGATIVE_MESSAGE),
      ],
    }),
    trialPeriod: new FormControl('', {
      nonNullable: true,
      validators: [
        wholeNumberDataType(TRIAL_PERIOD_INVALID_MESSAGE),
        wholeNumberPositive(TRIAL_PERIOD_NOT_POSITIVE_MESSAGE),
      ],
    }),
    trialFrequency: new FormControl<BillingFrequency>(NO_FREQUENCY, { nonNullable: true }),
    rsvpCode: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(RSVP_CODE_MAX_LENGTH)],
    }),
    iconFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(ICON_FILE_MAX_LENGTH)],
    }),
  });

  // -------------------------------------------------------------------------
  // DERIVED STATE
  // -------------------------------------------------------------------------

  /**
   * The role id as a number, or `null` when this is the creation route.
   *
   * The parse is guarded end to end: a radix is given explicitly, the result is checked with
   * `Number.isInteger`, and the round trip back to text must match, so `"1abc"`, `"1.0"`, `"0x1"`
   * and `"NaN"` are all REJECTED rather than silently truncated to a plausible-looking id. There is
   * no `!`, no cast and no unary `+`.
   *
   * Surrounding whitespace IS tolerated, and deliberately so. The legacy read the id with
   * `Int32.Parse(Request.QueryString("RoleID"))` (`EditRoles.ascx.vb:L100-L101`), and
   * `Int32.Parse` uses `NumberStyles.Integer`, which admits leading and trailing white space — so
   * `" 1 "` was role 1 to the legacy screen. A URL segment can carry `%20`, which the router
   * decodes to a space, so refusing it here would reject a request the legacy served. The trim
   * reproduces that tolerance exactly and nothing else about the text is forgiven.
   *
   * A successful parse of `"0"` yields `0`, which is a REAL role id and must be treated as
   * present. Nothing here tests the value for truthiness or for being positive.
   */
  protected readonly roleKey: Signal<number | null> = computed(() => {
    const raw = this.roleId();
    if (raw === undefined) {
      return null;
    }
    const text = raw.trim();
    if (text.length === 0) {
      return null;
    }
    const parsed = Number.parseInt(text, 10);
    if (!Number.isInteger(parsed) || String(parsed) !== text) {
      return null;
    }
    return parsed;
  });

  /**
   * True when this is the edit form.
   *
   * DERIVED FROM PRESENCE, NEVER FROM VALUE — see the note on {@link roleId}. `roleKey` is `null`
   * only when the route supplied no parameter or supplied one that is not an integer; a parsed `0`
   * makes this `true`.
   */
  protected readonly isEditMode: Signal<boolean> = computed(() => this.roleKey() !== null);

  /** The heading, which differs by mode. */
  protected readonly heading: Signal<string> = computed(() =>
    this.isEditMode() ? EDIT_TITLE : ADD_TITLE,
  );

  /**
   * The role name shown as read-only text in edit mode.
   *
   * This is the `lblRoleName` twin at `editroles.ascx:L28-L29`, which the legacy revealed while
   * hiding the textbox. It never renders `null` or `undefined`.
   */
  protected readonly displayRoleName: Signal<string> = computed(() =>
    textOrEmpty(this.loadedRole()?.roleName),
  );

  /** The role groups offered by the select, from the shared store. */
  protected readonly roleGroups: Signal<readonly RoleGroup[]> = this.roleStore.roleGroups;

  /**
   * The role-group select's options: the ungrouped choice first, then the real groups.
   *
   * MIGRATION: exactly what `BindGroups()` built at `EditRoles.ascx.vb:L71-L81` — one
   * `< Global Roles >` entry followed by every group, in the order the API returned them. The
   * `< All Roles >` filter sentinel that the LIST screen adds (`Roles.ascx.vb:L112`, value `-2`)
   * has no place in a form and does not appear here.
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
    () => this.loadingRole() || this.roleStore.roleGroupsLoading(),
  );

  /** True while a mutation is in flight; the template disables its commands on this. */
  protected readonly saving: Signal<boolean> = this.savingRole.asReadonly();

  /** The refusal to render in the shared error banner, or `null`. */
  protected readonly problem: Signal<ProblemDetails | null> = this.failure.asReadonly();

  /** True when the delete confirmation dialog should be shown. */
  protected readonly confirmingDelete: Signal<boolean> = this.deletePending.asReadonly();

  /** The verbatim legacy confirmation wording, for the dialog. */
  protected readonly deleteConfirmMessage = DELETE_CONFIRM_MESSAGE;

  /** The heading for the delete dialog, taken from the global `cmdDelete` caption. */
  protected readonly deleteConfirmLabel = 'Delete';

  /** The `cmdManage` caption, which IS locally keyed: `cmdManage.Text`. */
  protected readonly manageUsersLabel = 'Manage Users in this Role';

  /**
   * True when this role is one of the two the portal protects.
   *
   * MIGRATION — DEFECT 4. The legacy test is `If RoleID = PortalSettings.AdministratorRoleId Or
   * RoleID = PortalSettings.RegisteredRoleId Then` (`EditRoles.ascx.vb:L174`), written with an
   * unparenthesised `Or`. VB's `Or` is a NON-SHORT-CIRCUITING operator — unlike `OrElse` — so the
   * legacy evaluated both comparisons every time. `||` here DOES short-circuit. The two are
   * semantically identical in this instance because both operands are side-effect-free integer
   * comparisons, and the change of operator class is recorded rather than made silently.
   *
   * The comparison is against a numeric id and is `false` whenever the id is unknown, which is the
   * gap described on {@link administratorRoleId}.
   */
  protected readonly isProtectedRole: Signal<boolean> = computed(() => {
    const key = this.roleKey();
    if (key === null) {
      return false;
    }
    return key === this.administratorRoleId() || key === this.registeredRoleId();
  });

  /**
   * True when the whole form is read-only.
   *
   * MIGRATION: `ActivateControls(False)` (`EditRoles.ascx.vb:L47-L59`) disabled ELEVEN named
   * controls — description, role group, both check boxes, both fees, both periods, both frequency
   * selects and the RSVP code — and conspicuously did NOT disable `ctlIcon` or `txtRSVPLink`. That
   * omission is a measured legacy inconsistency, not a rule: leaving an icon picker live on a form
   * whose Update button has been hidden serves no purpose. The whole form is disabled here, which
   * additionally covers the icon path field that replaces the picker. The read-only RSVP link is
   * not carried forward at all.
   */
  protected readonly readOnly: Signal<boolean> = computed(() => this.isProtectedRole());

  /**
   * True when the Update command is rendered.
   *
   * `cmdUpdate` is hidden for a protected role (`EditRoles.ascx.vb:L176`) and shown otherwise —
   * including in creation mode, where `:L183-L188` hides only Delete and Manage.
   */
  protected readonly canSave: Signal<boolean> = computed(() => !this.isProtectedRole());

  /**
   * True when the Delete command is rendered.
   *
   * Hidden in creation mode (`EditRoles.ascx.vb:L184`) and hidden for a protected role (`:L175`).
   */
  protected readonly canDelete: Signal<boolean> = computed(
    () => this.isEditMode() && !this.isProtectedRole(),
  );

  /**
   * True when the Manage Users command is rendered.
   *
   * Hidden in creation mode (`EditRoles.ascx.vb:L185`) and hidden for the registered-users role
   * (`:L180-L182`). Note the asymmetry with Delete, which is faithfully preserved: the
   * ADMINISTRATOR role keeps its Manage command, because the legacy's second guard names only the
   * registered-users role. Its membership is genuinely manageable; the registered-users role's is
   * not, because every authenticated user holds it.
   */
  protected readonly canManageUsers: Signal<boolean> = computed(() => {
    const key = this.roleKey();
    if (key === null) {
      return false;
    }
    return key !== this.registeredRoleId();
  });

  /**
   * True when the payment-processor warning is shown.
   *
   * See {@link paymentProcessorConfigured}: the warning appears when the processor is NOT
   * configured, which is what the code did, and it stays hidden until something can genuinely
   * report the portal's processor state.
   */
  protected readonly showProcessorWarning: Signal<boolean> = computed(
    () => !this.paymentProcessorConfigured(),
  );


  // -------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------

  public constructor() {
    // `BindGroups()` at `EditRoles.ascx.vb:L127`, hoisted to the shared store so that the list is
    // fetched once per session rather than once per visit to this screen. The store owns the
    // request and its loading flag; this screen only reads the result.
    this.roleStore.loadRoleGroups();

    // Reacts to the route parameter. Reading `roleKey()` is the ONLY dependency taken here, so the
    // role is re-read when the route moves from one role to another — which happens without the
    // component being recreated, because both edit visits resolve to the same route
    // configuration — and is not re-read when anything else in the screen changes.
    effect(() => {
      const key = this.roleKey();
      this.applyMode(key !== null);
      if (key === null) {
        this.resetToCreateDefaults();
        return;
      }
      this.loadRole(key);
    });

    // Resolves the role-group selection once BOTH the role and the group list have arrived.
    //
    // The legacy had no race to manage: `BindGroups()` ran synchronously at `:L127`, before the
    // role was read at `:L136`, so the options always existed by the time the selection was made.
    // Here the two responses are independent, so the selection is resolved whenever either lands,
    // and only while the control is still pristine — a choice the user has already made is never
    // overwritten.
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
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * `cmdUpdate_Click` — `EditRoles.ascx.vb:L208-L274`.
   *
   * This is the ONLY command that validates. `cmdUpdate` is the one button in
   * `editroles.ascx:L179-L180` that omits `CausesValidation`, and the attribute defaults to
   * `True`; the legacy handler then opened with `If Page.IsValid Then` at `:L210`.
   */
  protected onSubmit(): void {
    if (this.saving() || this.readOnly()) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.failure.set(null);
    const key = this.roleKey();

    if (key === null) {
      this.createRole();
      return;
    }

    this.updateRole(key);
  }

  /**
   * `cmdCancel_Click` — `EditRoles.ascx.vb:L316-L323`, which did nothing but
   * `Response.Redirect(NavigateURL())`.
   *
   * `CausesValidation="False"` (`editroles.ascx:L183`) is reproduced exactly: nothing is
   * validated, nothing is marked touched, and an invalid form does not block the exit.
   */
  protected onCancel(): void {
    this.navigateToList();
  }

  /**
   * Opens the delete confirmation.
   *
   * `EditRoles.ascx.vb:L112` attached a client-side confirmation to the delete button with the
   * global `DeleteItem` wording. The shared dialog replaces the browser `confirm()` and adds a
   * focus trap and `Escape` handling that the legacy did not have.
   *
   * `CausesValidation="False"` (`editroles.ascx:L186`) is reproduced: no validation runs, and the
   * legacy handler at `:L287-L303` contains no `Page.IsValid` check of any kind.
   */
  protected requestRemoval(): void {
    if (this.saving() || !this.canDelete()) {
      return;
    }
    this.deletePending.set(true);
  }

  /** The user confirmed the deletion — `cmdDelete_Click`, `EditRoles.ascx.vb:L287-L303`. */
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

  /**
   * `cmdManage_Click` — `EditRoles.ascx.vb:L336-L342`.
   *
   * `CausesValidation="False"` (`editroles.ascx:L189`) is reproduced.
   *
   * MIGRATION: the legacy navigated with `NavigateURL(Me.TabId, "User Roles", "RoleId=" & RoleID)`,
   * and there is a measured case inconsistency in the legacy itself — `:L338` WRITES the query key
   * as `RoleId` while `:L100-L101` READS it as `RoleID`. ASP.NET's query-string collection is
   * case-insensitive, so the mismatch was harmless. The target route parameter is `roleId`, which
   * agrees with the writing side, and Angular's route matching is case-sensitive, so the ambiguity
   * is gone.
   *
   * The destination lives under `features/role/`, a sibling of this folder, and is reached by
   * navigating — never by importing. Features do not import one another.
   */
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
   * The message to show beneath one field, or `null` when it has nothing to say.
   *
   * A message appears only once the control is both invalid and either dirty or touched, which is
   * how `Display="Dynamic"` behaved: the legacy validators rendered nothing until a postback had
   * exercised them.
   *
   * Every custom validator on this form stores its message AS the error value, so the message a
   * user sees is the resource wording itself rather than a key translated at the point of display.
   * `errors` is an index-signature type, so every read is by bracket — `noPropertyAccessFromIndexSignature`
   * is enabled and dot access would not compile.
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

    // The four hand-written validators, in the order the legacy declared them: the data-type check
    // precedes the comparison on every one of the four numeric fields.
    for (const key of [
      'currencyDataType',
      'wholeNumberDataType',
      'moneyNotNegative',
      'wholeNumberPositive',
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

  /**
   * Applies the mode-dependent rule on the role name.
   *
   * MIGRATION: A ROLE NAME IS IMMUTABLE ONCE CREATED, and this is design rather than oversight.
   * `EditRoles.ascx.vb:L131-L134` reveals the read-only `lblRoleName`, hides `txtRoleName` and sets
   * `valRoleName.Enabled = False` for ANY role once it has an id. The membership provider proves
   * why: `AddRole` takes FOURTEEN parameters and INCLUDES `RoleName`
   * (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L234-L235`), while
   * `UpdateRole` takes THIRTEEN and has NO `RoleName` parameter at all (`:L242-L243`). The stored
   * procedure simply cannot change a name.
   *
   * That asymmetry also dissolves an apparent defect: `:L237` assigns
   * `objRoleInfo.RoleName = txtRoleName.Text`, which is the EMPTY STRING in edit mode because the
   * textbox is hidden — and it never mattered, because `UpdateRole` ignores the member.
   *
   * The required rule MUST be lifted in edit mode. Leaving it in place on a control the user cannot
   * edit would leave the edit form permanently invalid and its Update button permanently inert,
   * which is a parity break rather than a safety measure. `{ emitEvent: false }` keeps the change
   * out of the value stream, because the rule set changed and the value did not.
   *
   * @param editing True when a role id is present.
   */
  private applyMode(editing: boolean): void {
    const control = this.form.controls.roleName;
    control.setValidators(
      editing
        ? [Validators.maxLength(ROLE_NAME_MAX_LENGTH)]
        : [Validators.required, Validators.maxLength(ROLE_NAME_MAX_LENGTH)],
    );
    control.updateValueAndValidity({ emitEvent: false });
  }

  /**
   * Returns the form to the state the legacy creation form opened in.
   *
   * `EditRoles.ascx.vb:L183-L188` shows the textbox, hides the label and hides both the Delete and
   * Manage commands; `:L119` and `:L125` leave both frequency selects on `'N'`; `BindGroups()`
   * leaves the group select on `< Global Roles >`. Because every control is `nonNullable`, `reset`
   * returns each one to its declared initial value rather than to `null`, so those defaults are
   * restored by construction.
   */
  private resetToCreateDefaults(): void {
    this.loadedRole.set(null);
    this.failure.set(null);
    this.form.reset();
    this.form.markAsPristine();
    this.form.markAsUntouched();
  }


  // -------------------------------------------------------------------------
  // TRANSPORT
  // -------------------------------------------------------------------------

  /**
   * Reads the role — `objUser.GetRole(RoleID, PortalSettings.PortalId)` at
   * `EditRoles.ascx.vb:L136`.
   *
   * The portal is resolved by the API from the request, so no portal id is passed; the service is a
   * transport and owns the address.
   *
   * @param key The role id, which may legitimately be `0`.
   */
  private loadRole(key: number): void {
    this.loadingRole.set(true);
    this.failure.set(null);

    this.roleService
      .getRole(key)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.loadingRole.set(false);
          this.applyRole(response.data);
        },
        error: (error: unknown) => {
          this.loadingRole.set(false);
          // `:L170-L172` treated an unreadable role as an attempt to reach an item outside the
          // module and bounced to the Security Roles page. A missing role does the same here.
          if (statusOf(error) === NOT_FOUND) {
            this.notifications.notify('warning', ROLE_NOT_FOUND_MESSAGE);
            this.navigateToList();
            return;
          }
          this.reportFailure(error, LOAD_FAILED_MESSAGE);
        },
      });
  }

  /**
   * Populates the form from a loaded role — `EditRoles.ascx.vb:L139-L169`.
   *
   * MIGRATION — DEFECT 3, the string money comparison. `:L146` decides whether the role is priced
   * with `If Format(objRoleInfo.ServiceFee, "#,##0.00") <> "0.00" Then` — it FORMATS the fee and
   * compares the resulting TEXT. That is fragile in a way the culture makes obvious: under a
   * culture whose decimal separator is a comma the formatted zero is `"0,00"`, which is not equal to
   * `"0.00"`, and every free role would have been treated as priced. The target compares the NUMBER.
   *
   * MIGRATION: `:L154` gates the TRIAL fields on `If objRoleInfo.TrialFrequency <> "N"` — on the
   * FREQUENCY, not on the trial fee, and so asymmetrically with the billing gate one line group
   * above. The asymmetry is reproduced exactly as measured, because a trial with a zero fee and a
   * real frequency is a legitimate free trial and testing the fee would hide it.
   *
   * MIGRATION: `txtRSVPLink` (`editroles.ascx:L161`, populated at `:L165-L168` from
   * `AddHTTP(GetDomainName(Request)) & "/" & glbDefaultPage & "?rsvp=" & code`) is DROPPED
   * unconditionally. It addressed a self-service subscribe flow that the target does not implement,
   * so a link to it would be a link to nothing.
   *
   * MIGRATION: `ctlIcon` (`editroles.ascx:L169-L170`) was a `dnn:Url` file-and-URL picker with
   * `FileFilter = glbImageFileTypes` (`:L129`). It is reduced to a plain text field holding the
   * stored path, because the target exposes no filesystem, upload or file-listing endpoint. The
   * path itself round-trips unchanged; only the means of choosing it is lost.
   *
   * The role-group selection is deliberately NOT set here — the dedicated effect in the constructor
   * owns it, because it must wait for the group list.
   *
   * @param role The role as the API reported it.
   */
  private applyRole(role: Role): void {
    this.loadedRole.set(role);

    const fee = role.serviceFee;
    const priced =
      typeof fee === 'number' && Number.isFinite(fee) && fee > LEGACY_ABSENT_MONEY_THRESHOLD
        ? fee !== 0
        : false;

    const trialFrequency = coerceFrequency(role.trialFrequency);
    const onTrial = trialFrequency !== NO_FREQUENCY;

    this.form.setValue({
      roleName: textOrEmpty(role.roleName),
      description: textOrEmpty(role.description),
      roleGroupId: this.form.controls.roleGroupId.value,
      isPublic: role.isPublic,
      autoAssignment: role.autoAssignment,
      serviceFee: priced ? formatMoney(fee) : '',
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
   * Creates the role — `objRoleController.AddRole(objRoleInfo)` at `EditRoles.ascx.vb:L253`.
   *
   * MIGRATION: the legacy guarded the insert with its own lookup,
   * `If objRoleController.GetRoleByName(PortalId, objRoleInfo.RoleName) Is Nothing Then` (`:L252`),
   * and showed the `DuplicateRole` message at `RedError` when it found a match (`:L256`). That is a
   * read-then-write race, and the target replaces it with the API's own refusal — a `409` — which
   * the database's unique index makes authoritative. The wording the user sees is unchanged.
   *
   * MIGRATION: `objEventLog.AddLog(…, EventLogType.ROLE_CREATED)` at `:L254` is not reproduced
   * here. The audit trail is written by the API, on the server side of the call, which is the only
   * place that can record it truthfully.
   */
  private createRole(): void {
    this.savingRole.set(true);
    this.roleService
      .createRole(this.toCreateRequest())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.savingRole.set(false);
          this.notifications.notify('success', ROLE_CREATED_MESSAGE);
          this.afterMutation();
        },
        error: (error: unknown) => {
          this.savingRole.set(false);
          this.reportFailure(error, SAVE_FAILED_MESSAGE);
        },
      });
  }

  /**
   * Updates the role — `objRoleController.UpdateRole(objRoleInfo)` at `EditRoles.ascx.vb:L260`.
   *
   * MIGRATION: the legacy ran NO duplicate check on this path — `:L259-L262` updates
   * unconditionally — which it could afford because `UpdateRole` has no `RoleName` parameter and so
   * could not create a collision. The target's `UpdateRoleRequest` DOES carry `roleName`, so a
   * collision is possible in principle and the API refuses it with a `409`. That refusal is handled
   * like any other rather than assumed unreachable.
   *
   * @param key The role id being updated, which may legitimately be `0`.
   */
  private updateRole(key: number): void {
    this.savingRole.set(true);
    this.roleService
      .updateRole(key, this.toUpdateRequest())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.savingRole.set(false);
          this.notifications.notify('success', ROLE_UPDATED_MESSAGE);
          this.afterMutation();
        },
        error: (error: unknown) => {
          this.savingRole.set(false);
          this.reportFailure(error, SAVE_FAILED_MESSAGE);
        },
      });
  }

  /**
   * Deletes the role — `objUser.DeleteRole(RoleID, PortalSettings.PortalId)` at
   * `EditRoles.ascx.vb:L291`.
   *
   * `objEventLog.AddLog("RoleID", …, EventLogType.ROLE_DELETED)` at `:L293` is written by the API,
   * for the reason given on {@link createRole}.
   *
   * @param key The role id being deleted, which may legitimately be `0`.
   */
  private deleteRole(key: number): void {
    this.savingRole.set(true);
    this.failure.set(null);
    this.roleService
      .deleteRole(key)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.savingRole.set(false);
          this.notifications.notify('success', ROLE_DELETED_MESSAGE);
          this.afterMutation();
        },
        error: (error: unknown) => {
          this.savingRole.set(false);
          this.reportFailure(error, DELETE_FAILED_MESSAGE);
        },
      });
  }

  /**
   * Runs after any successful mutation.
   *
   * MIGRATION: `DataCache.RemoveCache("GetRoles")` (`EditRoles.ascx.vb:L265` and `:L296`) has NO
   * client equivalent — it evicted a server-side cache entry, and no endpoint exposes that. The
   * store's own list is refreshed instead, so the list screen shows the change immediately, and no
   * cache-invalidation endpoint is invented.
   *
   * The navigation reproduces `Response.Redirect(NavigateURL())`, which both the update and the
   * delete handlers ended with.
   */
  private afterMutation(): void {
    this.roleStore.loadRoles();
    this.navigateToList();
  }

  /** Returns to the role list, the destination of every `NavigateURL()` on this screen. */
  private navigateToList(): void {
    void this.router.navigate([ROLE_LIST_ROUTE]);
  }

  // -------------------------------------------------------------------------
  // REQUEST CONSTRUCTION
  // -------------------------------------------------------------------------

  /**
   * Builds the creation request.
   *
   * MIGRATION: the legacy built a positional call — `AddRole` took FOURTEEN ordered parameters —
   * and the target posts a named contract instead. No `ByRef` status parameter has an analogue:
   * the legacy reported outcomes by mutating an argument, and the outcome here is the HTTP response.
   */
  private toCreateRequest(): CreateRoleRequest {
    const value = this.form.getRawValue();
    const billing = this.resolveBillingTerms();
    const trial = this.resolveTrialTerms(billing);

    return {
      roleName: value.roleName.trim(),
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
   * Builds the update request.
   *
   * MIGRATION: `UpdateRoleRequest` declares `roleName`, whereas the legacy `UpdateRole` stored
   * procedure had no such parameter. The contract replaces the whole role rather than patching it,
   * so the name must be present or the API would read it as missing. THE LOADED NAME IS SENT
   * UNCHANGED — the form's own `roleName` control is never editable in this mode and is not
   * consulted, so the name a user sees is the name that is sent, and immutability is preserved in
   * substance even though the wire contract now carries the member.
   *
   * This also avoids reproducing the legacy's `:L237` behaviour of assigning the hidden textbox's
   * empty string, which was harmless only because the procedure discarded it and would now
   * overwrite the name with nothing.
   */
  private toUpdateRequest(): UpdateRoleRequest {
    const value = this.form.getRawValue();
    const billing = this.resolveBillingTerms();
    const trial = this.resolveTrialTerms(billing);
    const loaded = this.loadedRole();

    return {
      roleName: loaded === null ? value.roleName.trim() : loaded.roleName,
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
    };
  }

  /**
   * Resolves the billing group — `EditRoles.ascx.vb:L212-L220`.
   *
   * MIGRATION: A THREE-PART GATE, reproduced conjunct for conjunct. `:L216` reads
   *
   *   `If txtServiceFee.Text <> "" And txtBillingPeriod.Text <> ""`
   *   `        And cboBillingFrequency.SelectedItem.Value <> "N" Then`
   *
   * so the entered values are submitted ONLY when the fee is non-empty AND the period is non-empty
   * AND the frequency is not `'N'`. Fail any one of the three and ALL THREE defaults are submitted
   * instead: fee `0`, period `1` — NOT `0` — and frequency `'N'`.
   *
   * The parse is safe. The legacy called `Single.Parse` and `Integer.Parse` at `:L217-L218`, so a
   * value that slipped past the defective comparison validators threw; here an unparseable value is
   * indistinguishable from an absent one and yields the defaults, which is the same outcome the
   * legacy reached whenever its validators worked. Nothing is coerced through a zero.
   *
   * @returns The resolved fee, period and frequency.
   */
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
   * Resolves the trial group — `EditRoles.ascx.vb:L222-L230`.
   *
   * MIGRATION: A TRIAL IS ONLY STORED WHEN A SERVICE FEE EXISTS. `:L226` reads
   *
   *   `If sglServiceFee <> 0 And txtTrialFee.Text <> "" And txtTrialPeriod.Text <> ""`
   *   `        And cboTrialFrequency.SelectedItem.Value <> "N" Then`
   *
   * and the FIRST conjunct is the already-resolved service fee, not a trial field at all. So this
   * is a genuine cross-field rule: trial values entered against a free role resolve SILENTLY to the
   * trial defaults.
   *
   * It is deliberately NOT surfaced as a validation error, because the legacy surfaced nothing —
   * the values were simply discarded. Turning a silent resolution into a refusal would reject input
   * the legacy accepted, which is the opposite of parity.
   *
   * Note that the first conjunct tests the RESOLVED fee, so a role whose billing group was itself
   * suppressed also loses its trial, transitively. That cascade is measured behaviour and is
   * preserved.
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

  /**
   * Turns a refusal into something the user can act on.
   *
   * The API answers every refusal with an RFC 7807 document, so the document is the contract and is
   * read rather than guessed at. Field-level messages are placed on the matching controls and the
   * remainder is handed to the shared error banner, which renders the title, the detail and the
   * support reference.
   *
   * A `403` is a WARNING, not an error. `Website/admin/Security/AccessDenied.ascx.vb` is
   * unambiguous: BOTH of its branches — `:L43` and `:L45` — raise
   * `ModuleMessageType.YellowWarning`, never `RedError`. Being told one lacks permission is not a
   * failure of the request, it is the answer to it.
   *
   * A `409` on the creation path is the duplicate-name refusal the legacy detected for itself, and
   * it carries the legacy wording as its fallback. A `429` is a real status this API emits and is
   * left to the banner, which styles rate limiting distinctly from an error.
   *
   * The document is stored whole, so `traceId` and `correlationId` survive into the banner for
   * support to quote.
   *
   * @param error Whatever the transport rejected with.
   * @param fallback The wording to use when the document says nothing useful.
   */
  private reportFailure(error: unknown, fallback: string): void {
    const problem = problemOf(error);
    const status = statusOf(error);

    this.failure.set(problem);

    if (problem !== null) {
      this.applyFieldErrors(problem);
    }

    if (status === UNAUTHORIZED || status === FORBIDDEN) {
      this.notifications.notify('warning', problemDetailsMessage(problem, ACCESS_DENIED_MESSAGE));
      return;
    }

    if (status === CONFLICT) {
      this.notifications.notify('error', problemDetailsMessage(problem, DUPLICATE_ROLE_MESSAGE));
      return;
    }

    this.notifications.notify('error', problemDetailsMessage(problem, fallback));
  }

  /**
   * Places server-reported field messages on the controls they belong to.
   *
   * The keys are the API's own member names. `problemDetailsFieldErrors` lower-cases the first
   * character of each, which lands every one of the thirteen role members exactly on the control of
   * the same name, and the resulting object is an index-signature type so every read is by bracket.
   *
   * A server message is applied VERBATIM. It is never rewritten to match the client's own wording,
   * even where the two differ — the API's refusal for the billing period, for instance, carries the
   * legacy's stale inline sentence rather than the resource sentence this form uses for its own
   * rule. Reporting what the server actually said is more useful than harmonising it.
   *
   * The message is merged into the control rather than replacing its errors, so a rule that is
   * still failing locally is not erased by a server response.
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

