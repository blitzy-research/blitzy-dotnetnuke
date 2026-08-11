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
  computed,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { Signal, WritableSignal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { Router } from '@angular/router';
import { problemDetailsFieldErrors, problemDetailsMessage } from '../../../core/models/problem-details.model';
import { failureCode } from '../../../core/utils/form-errors.util';
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
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { requiredText } from '../../../core/utils/required-text.validator';
import { parseRouteId } from '../../../core/utils/route-id.util';

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
    // ⚠ THE ORDER IS THIS SCREEN'S AND THE WORDS ARE THE CONTRACT'S. The sequence below is the order
    // `EditRoles.ascx.vb:L116-L125` bound, and it is declared here because a select's option order is
    // presentation. The CAPTIONS come from `BILLING_FREQUENCY_NAMES` beside the contract, because the
    // role listing needs the same words and two copies of user-facing wording is how two screens start
    // disagreeing about what `M` is called. Each caption is read by key rather than spelled again.
    { value: 'N', label: BILLING_FREQUENCY_NAMES['N'] ?? 'None' },
    { value: 'O', label: BILLING_FREQUENCY_NAMES['O'] ?? 'One Time' },
    { value: 'D', label: BILLING_FREQUENCY_NAMES['D'] ?? 'Day' },
    { value: 'W', label: BILLING_FREQUENCY_NAMES['W'] ?? 'Week' },
    { value: 'M', label: BILLING_FREQUENCY_NAMES['M'] ?? 'Month' },
    { value: 'Y', label: BILLING_FREQUENCY_NAMES['Y'] ?? 'Year' },
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

// The icon containment rule, its error key and its wording now live in `core/utils/icon-reference.util`
// and are imported above rather than declared here. They were moved because the module form needed the
// SAME rule, and a second private copy is exactly the failure the API side already suffered and recorded:
// while its containment rule was private to one validator, the paths that lacked it accepted references
// that one refused, and the weaker path defined the application's actual behaviour. One rule, one place.

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

/**
 * The sentence shown when the address does not name a readable role.
 *
 * MIGRATION: AUTHORED, because the legacy screen had no such state to word. `EditRoles.ascx.vb`
 * read the identifier with `Int32.Parse(Request.QueryString("RoleID"))` and no guard
 * (`:L100-L101`), so a mistyped address raised a format exception that the page's own handler
 * absorbed into its generic module error surface. Naming the outcome plainly is the same result
 * without the exception, and it is emphatically NOT the same as treating the parameter as absent -
 * which is what this screen used to do, and which offered to create a role instead.
 *
 * Worded to say what is wrong and what to do next, and deliberately not as a fault: following a
 * stale link is an ordinary thing to do and nothing has broken.
 */
const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a role that can be read. Return to the role list and try again.';

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
 * The accessible name of the billing-frequency select.
 *
 * MIGRATION: AUTHORED, because the legacy had none to recover, and authored as an accessible name
 * only - it changes not one rendered pixel. `editroles.ascx` L100-L101 declares ONE label,
 * `plBillingPeriod` with `ControlName="txtBillingPeriod"`, and that label names the period TEXT BOX
 * alone; `cboBillingFrequency` at L106 is named by nothing at all, and there is no `BillingFrequency`
 * entry anywhere in `EditRoles.ascx.resx`. So the legacy screen presented an unnamed combo box.
 *
 * The shared field component improves on that by lending its caption to any projected control that
 * has no name of its own - which is right in general and produced a new problem here, because this
 * field projects TWO controls: both the period box and the frequency select came out named "Billing
 * Period (Every)", so a screen reader announced two different controls identically and neither could
 * be told from the other. That was reported as a defect and it is one.
 *
 * The name therefore keeps the measured caption, so the control is still announced as part of the
 * billing period the operator sees, and adds the one word that says which half of it this control
 * is. "Unit" is the field's own help text speaking: "These two fields are used in conjunction to
 * enter a Billing Period. e.g 2 weeks, or 1 month" - the box holds the count and this holds the
 * unit. The component leaves a consumer-supplied name alone, so supplying it here is what stops the
 * caption being lent.
 */
const BILLING_FREQUENCY_ACCESSIBLE_NAME = 'Billing Period (Every) — unit';

/**
 * The accessible name of the trial-frequency select.
 *
 * Same reasoning as {@link BILLING_FREQUENCY_ACCESSIBLE_NAME}: `plTrialPeriod` names
 * `txtTrialPeriod` only, `cboTrialFrequency` is named by nothing, and both controls were coming out
 * as "Trial Period (Every)".
 */
const TRIAL_FREQUENCY_ACCESSIBLE_NAME = 'Trial Period (Every) — unit';

/**
 * Reported when an amount falls outside what the `money` column can hold.
 *
 * MIGRATION: NET-NEW WORDING — the legacy screen had no sentence for this because it had no rule.
 * `Single.Parse` (`EditRoles.ascx.vb:L217,L227`) accepted any magnitude a `Single` could represent
 * and the provider then failed on the insert, inside the blanket `Catch exc As Exception` at
 * `:L271`, which showed a generic failure naming no field. The bound is the column's own, stated as
 * such: REPRESENTABILITY, NOT A PRICE CEILING. Inventing a business maximum would refuse a fee some
 * installation legitimately charges, and the API's own rule for these two members says the same
 * thing in the same terms.
 */
const AMOUNT_OUT_OF_RANGE_MESSAGE =
  'That amount is outside the range this site can store. Enter an amount between ' +
  '-922,337,203,685,477.58 and 922,337,203,685,477.58.';

/**
 * Reported when an amount would reach the column with different digits from the ones typed.
 *
 * MIGRATION: NET-NEW WORDING, and net-new protection. The value travels as a JSON number, so an
 * amount with more significant digits than a double carries is silently rewritten in transit —
 * `123456789012345678901` was accepted, stored as `123456789012345680000`, and the substitution was
 * reported nowhere. The legacy screen could not have had this defect in this form because its
 * postback carried text to a `Single.Parse` on the server, but it had the same class of defect for
 * the same reason: `Single` holds about seven significant digits, so `1234567.89` already lost the
 * last place. Refusing the value is the only outcome that keeps the stored amount equal to the
 * intended one.
 */
const AMOUNT_LOSES_PRECISION_MESSAGE =
  'That amount has more digits than can be stored without rounding. Enter a shorter amount.';

/**
 * Reported when a period falls outside what the `int` column can hold.
 *
 * MIGRATION: NET-NEW WORDING. Without it the submission reached the API, failed to bind, and came
 * back as `"request": ["The request field is required."]` — a message that names no field, points at
 * nothing the person typed, and reads as though the whole request were missing. The legacy reached a
 * comparable dead end through `Integer.Parse` throwing into `Catch exc As Exception` (`:L271`). The
 * ceiling quoted is the column's, not a business rule: `RoleService`'s own date arithmetic clamps
 * every offset it derives, so nothing beyond this bound is left unprotected by refusing it here.
 */
const PERIOD_OUT_OF_RANGE_MESSAGE =
  'That number is outside the range this site can store. Enter a whole number between ' +
  '-2,147,483,648 and 2,147,483,647.';

/**
 * `DuplicateRole.Text`, verbatim.
 *
 * `EditRoles.ascx.vb:L256` raised it at `ModuleMessageType.RedError` after its own
 * lookup-then-insert check at `:L252` found a name collision.
 */
const DUPLICATE_ROLE_MESSAGE = 'A role with the same name already exists. The role was not added.';

/**
 * The failure code the API publishes when the role changed between this screen's read and its write.
 *
 * Read through the shared {@link failureCode} reader rather than compared against `problem.type`
 * directly, because the code arrives INSIDE the type URI and the reader owns that one parse. Keying on
 * the code is what separates this conflict from the other `409` this screen can receive — a duplicate
 * name — which needs a different response entirely: a duplicate is corrected by editing a field,
 * whereas a stale read is corrected by reading again.
 */
const CONCURRENCY_CONFLICT_CODE = 'role.concurrency_conflict';

/**
 * Shown when a save is refused because someone else changed the role first.
 *
 * MIGRATION: NET-NEW, because the situation itself is net-new — the legacy had no conflict to report.
 * `UpdateRole` carried no revision marker, so two administrators editing one role both saved their own
 * complete snapshot and the later save silently discarded every change the earlier one had made. Used
 * only as a fallback: the API's own `detail` names the role and states the remedy, and
 * {@link problemDetailsMessage} prefers it.
 */
const CONCURRENCY_CONFLICT_MESSAGE =
  'This role was changed by someone else after you opened it, so nothing was saved.';

/**
 * The recovery sentence beside the reload command.
 *
 * ⚠ THE COST OF RELOADING IS STATED, not glossed. Re-reading the role replaces every value on screen
 * with the stored one, so unsaved edits are lost — and a person needs to know that BEFORE pressing the
 * button, not after. It is offered anyway because the alternative is worse in a way that is not
 * obvious: reloading the BROWSER would end the session outright, since the access token is held in
 * memory only, and the person would be returned to the sign-in screen having lost the same edits.
 */
const CONCURRENCY_RECOVERY_MESSAGE =
  'Read the role again to see the stored values, then apply your change to them. ' +
  'Anything you have typed here and not saved will be replaced.';

/** Caption of the command that re-reads a role after a conflict. */
const CONCURRENCY_RELOAD_LABEL = 'Read this role again';

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

/**
 * The three store commands this screen issues and then waits on.
 *
 * Named as a union of the store's own operation identifiers rather than as strings of this
 * screen's invention, so the outcome bridge can match the store's recorded failure against the
 * command it is waiting for and a rename on either side is a compile error.
 */
type AwaitedRoleMutation = Extract<RoleStoreOperation, 'createRole' | 'updateRole' | 'deleteRole'>;

/** What each mutation announces when it succeeds. */
const MUTATION_SUCCESS_MESSAGE: Readonly<Record<AwaitedRoleMutation, string>> = Object.freeze({
  createRole: ROLE_CREATED_MESSAGE,
  updateRole: ROLE_UPDATED_MESSAGE,
  deleteRole: ROLE_DELETED_MESSAGE,
});

/**
 * What each mutation says when it fails and the server explained nothing usable.
 *
 * A creation and an update share one sentence because the legacy did: `EditRoles.ascx.vb` reached
 * both from the same handler and presented the same message on either.
 */
const MUTATION_FAILURE_MESSAGE: Readonly<Record<AwaitedRoleMutation, string>> = Object.freeze({
  createRole: SAVE_FAILED_MESSAGE,
  updateRole: SAVE_FAILED_MESSAGE,
  deleteRole: DELETE_FAILED_MESSAGE,
});

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
 * untouched form invalid.
 *
 * ⚠ A GROUP IS EXACTLY THREE DIGITS, AND THAT PRECISION IS THE WHOLE POINT. This pattern
 * previously read `\d{1,3}(?:,\d+)*` — a group of ANY length — which accepted `1,5`. The separator
 * was then stripped and the value became `15`: a TEN-FOLD monetary error, reported as valid,
 * persisted, and never shown to the person who typed it. `1,5` is not a number in any locale this
 * screen writes; `#,##0.00` emits groups of three and nothing else, so accepting only groups of
 * three still admits every value the screen itself produces while refusing the mistyped form
 * outright. The refusal surfaces through `valServiceFee1`/`valTrialFee1`'s own resource wording —
 * "Value Entered Is Not Valid" — so no new sentence is invented for a case the legacy already had
 * words for.
 *
 * MIGRATION: this is stricter than the legacy `Type="Currency"` check, which delegated to
 * `Decimal.TryParse` with `NumberStyles.Currency` and was equally lenient about group length. The
 * divergence is deliberate and follows the precedent set for the portal alias substring match: a
 * legacy accident that silently corrupts persisted money is corrected rather than reproduced, and
 * the correction is recorded in `MIGRATION_NOTES.md`.
 */
const MONEY_PATTERN = /^[+-]?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?$/;

/** Recognises what `Type="Integer"` accepted: an optional sign and digits, with no separators. */
const WHOLE_NUMBER_PATTERN = /^[+-]?\d+$/;

/**
 * Bounds of the `int` columns behind the two period fields.
 *
 * `Roles.BillingPeriod` and `Roles.TrialPeriod` are `int NULL`, and the API's own request members
 * are `int?`, so a value beyond this range cannot be bound at all: it is refused during
 * deserialisation, BEFORE any validator runs, and the response names no field — it carries the
 * bare `"request": ["The request field is required."]` that gives a person nothing to act on.
 * Judging the range here turns that dead end into a message beside the field that caused it.
 */
const PERIOD_MINIMUM = -2_147_483_648;

/** Upper bound of the `int` columns behind the two period fields. See {@link PERIOD_MINIMUM}. */
const PERIOD_MAXIMUM = 2_147_483_647;

/**
 * Bounds of the `money` columns behind the two fee fields.
 *
 * `Roles.ServiceFee` and `Roles.TrialFee` are `money NULL`. The API states the identical rule as
 * `SqlServerRange.CanStore`, and the wording below deliberately echoes the concept its message
 * carries: what is refused is an amount the column cannot hold at all, never a business maximum
 * the legacy screen never declared.
 */
const MONEY_MINIMUM = -922_337_203_685_477.5808;

/** Upper bound of the `money` columns behind the two fee fields. See {@link MONEY_MINIMUM}. */
const MONEY_MAXIMUM = 922_337_203_685_477.5807;

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
 * Reduces a decimal string to the digits it actually states, so two spellings of one value compare
 * equal and two different values never do.
 *
 * Grouping separators, a leading `+`, leading zeros on the whole part and trailing zeros on the
 * fraction all carry no information and are removed. A sign is kept only when some digit is
 * non-zero, so `-0.00` and `0` agree — they are the same amount.
 *
 * This exists to answer ONE question truthfully: does the number that will be SENT still state
 * every digit that was TYPED? A form control holds text; the wire carries a JSON number, which is
 * an IEEE-754 double on the way out. `123456789012345678901` typed becomes
 * `123456789012345680000` sent, and nothing in the value stream reveals the substitution. Comparing
 * the canonical form of the typed text against the canonical form of the parsed number's own
 * shortest round-trip rendering detects exactly that class of loss, with no arbitrary digit limit to
 * justify and no locale assumption.
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
 * True when an amount reaches the `money` column holding every digit that was typed.
 *
 * Two distinct failures are reported separately, because they call for different corrections: an
 * amount OUTSIDE the column's range needs a smaller one, whereas an amount inside the range that
 * cannot be carried exactly needs fewer digits. The range is tested first so that the round-trip
 * comparison never has to reason about exponential notation, which `Number.prototype.toString`
 * only reaches at 1e21 — far above {@link MONEY_MAXIMUM}.
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

/**
 * Refuses an amount the `money` column cannot hold, or cannot hold with the digits that were typed.
 *
 * NET-NEW RULE with no legacy counterpart, added because both failures were previously silent: one
 * surfaced as a server fault naming no field, the other did not surface at all and corrupted the
 * stored amount. An unparseable value yields `null` here and defers to the data-type validator on
 * the same control, so exactly one message is shown — the same single-message discipline the four
 * ported validators already keep.
 */
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

/**
 * Refuses a period the `int` column cannot hold.
 *
 * NET-NEW RULE with no legacy counterpart. `Number.isSafeInteger` is tested as well as the column
 * bounds because `Number.parseInt` accepts a digit string of any length and answers the nearest
 * double — `Number.isInteger` says `true` for it — so a twenty-one-digit period passed every earlier
 * check and then failed to bind. Both conditions are stated rather than relying on the range test
 * alone, so the guarantee does not depend on the bounds happening to sit inside the safe range.
 */
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
 * Narrows an incoming STORED frequency code to the closed write vocabulary, falling back to `'N'`.
 *
 * The legacy did this by lookup: `cboBillingFrequency.Items.FindByValue(...)` guarded by
 * `If Not … Is Nothing` (`EditRoles.ascx.vb:L149-L152,L157-L160`), so a value with no matching
 * option left the select on its `'N'` default. That behaviour is reproduced exactly.
 *
 * MIGRATION: THE PARAMETER IS THE READ VOCABULARY AND THE RETURN IS THE WRITE VOCABULARY, and
 *   this function is the crossing point between them. The API carries a stored frequency
 *   character through losslessly — shipped DotNetNuke data contains two roles whose characters
 *   fall outside the published six — while a request may only carry one of the six. So a role
 *   holding an unsupported code opens in this form with its frequency select on `'N'`, exactly as
 *   the legacy lookup left it, and the operator's own choice is what is sent back. Nothing here
 *   invents a meaning for an unsupported code, and nothing sends one.
 *
 * @param value The frequency as the API reported it, possibly absent, possibly a code outside the
 * supported six.
 * @returns A code that certainly exists among the options.
 */
function coerceFrequency(value: StoredBillingFrequency | null | undefined): BillingFrequency {
  if (value === null || value === undefined) {
    return NO_FREQUENCY;
  }

  // The code is taken FROM THE OPTION LIST rather than passed through, which is what makes the
  // crossing from the read vocabulary to the write vocabulary a real narrowing instead of an
  // assertion — and it is also the literal shape of the legacy `Items.FindByValue(...)` lookup.
  const matched = BILLING_FREQUENCY_OPTIONS.find((option) => option.value === value);

  return matched === undefined ? NO_FREQUENCY : matched.value;
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

/*
 * MIGRATION: two local error readers - one for the transport status, one for the problem document -
 * used to live here, because every command on this screen subscribed to the transport itself and had
 * to unpick whatever the rejection carried. Both are gone. The store records the status and the
 * document as separate members of one failure, so there is nothing left to unpick and no second,
 * divergent reader of a shape the shared model already describes.
 */

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
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
    ConfirmDialogComponent,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './role-form.component.html',
  styleUrl: './role-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleFormComponent {

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
    () => this.form.dirty && this.saving() === false,
  );
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

  /*
   * THE TENANT'S PROTECTED ROLE KEYS AND ITS PROCESSOR STATE ARE READ, NOT AWAITED.
   *
   * MIGRATION: `EditRoles.ascx.vb:L174-L182` guards three things on the identity of two
   * PORTAL-level roles, and nothing on the role itself discriminates them — `RoleInfo.vb` has no
   * `IsSystem`, `SystemRole` or `IsAdmin` member of any kind, and the target's `Role` contract
   * likewise carries no such flag:
   *
   *   L174-L178  `If RoleID = PortalSettings.AdministratorRoleId Or RoleID = PortalSettings.RegisteredRoleId`
   *              then hide Delete, hide Update, and `ActivateControls(False)`.
   *   L180-L182  `If RoleID = PortalSettings.RegisteredRoleId` then additionally hide Manage.
   *
   * ⚠ THESE WERE OPTIONAL INPUTS THAT NOTHING SUPPLIED, AND THAT IS WHY THEY ARE GONE. The three
   * facts used to be declared as inputs on the reasoning that "a portal-settings call is outside
   * this screen's endpoint boundary, and the portal model is not among its dependencies", with the
   * documented consequence that "when it does not — which is the case today — the form stays fully
   * editable and the API's own refusal governs". Neither half of that reasoning survives scrutiny.
   *
   * The boundary claim was wrong about the architecture. {@link PortalStore} is CORE state, and
   * every feature may inject core state — the account listing already reads exactly these facts
   * from it to protect its own removal command, and nothing here imports from another FEATURE.
   * `GET /api/v1/portals/{portalId}` is declared under the same `PortalAdministrator` policy that
   * both of this screen's routes declare, so any caller who can reach this form can read the
   * record; and the tenant key needs no route segment, because the identity projection carries the
   * caller's own `portalId`.
   *
   * The fallback claim was wrong about the cost. Leaving the guards permanently disarmed did not
   * merely defer to the server: it OFFERED Update and Delete on the two roles that hold the tenant
   * together, let an administrator fill in a form for them, and only then reported a refusal —
   * having also left the payment-processor warning permanently visible, since the warning shows
   * when the processor is NOT configured and an unread portal reads as unconfigured. Reproducing
   * a guard the legacy had is not scope creep; withholding it was the divergence.
   *
   * ⚠ AN UNRESOLVED READ STILL DISARMS THE GUARDS, and that direction is deliberate. Until the
   * record arrives each key is `null`, every comparison below is `false`, the form is editable and
   * the API's refusal governs — which is exactly the behaviour that shipped, so nothing regresses
   * while the request is outstanding. Disabling the form until the read completed would instead
   * take a capability away from every role for the duration of a request. The processor warning is
   * the one exception worth naming: it is suppressed until the record resolves, because showing
   * "configure a payment processor" about a portal nobody has read yet is an assertion rather than
   * a default.
   *
   * No role id is hardcoded, no role NAME is consulted, and no endpoint is invented.
   */

  /**
   * The tenant's administrator role key, or `null` until its record resolves.
   *
   * `Roles.RoleID` is `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L114`), so nought is a real role
   * key and the administrator role in the seeded tenant genuinely holds it. Every comparison
   * against this value is an explicit equality test against `null`, never a truthiness test.
   */
  protected readonly administratorRoleId: Signal<number | null> = computed(() =>
    this.portals.administratorRoleId(),
  );

  /**
   * The tenant's registered-users role key, or `null` until its record resolves.
   *
   * The stricter of the two: it is protected from deletion and update AND its membership screen is
   * unreachable, because every authenticated user holds it.
   */
  protected readonly registeredRoleId: Signal<number | null> = computed(() =>
    this.portals.registeredRoleId(),
  );

  /**
   * Whether the tenant has a configured payment processor.
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
   * being tidied away. Note that the legacy's own first clause, `objPortalInfo Is Nothing`, warned
   * when the portal could not be read at all; the target withholds the warning in that case
   * instead, for the reason given above.
   */
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
   * The shared role state, and THE ONLY ROUTE TO THE API FROM THIS SCREEN.
   *
   * ⚠ THE TRANSPORT IS DELIBERATELY NOT INJECTED. Every read and every write this screen performs
   * goes through the store, so there is exactly one copy of the role, one loading flag per slice and
   * one failure slot in the application. When this screen subscribed to `RoleService` directly it
   * held its own copy of all three, and the store's listing could be left showing a role that had
   * just been renamed or removed - which is precisely what the review recorded.
   */
  /**
   * This component's own element, read only to find controls a rejected submit is complaining about.
   *
   * Needed because the fee and period fields live inside a `details` element that starts CLOSED, and
   * a control inside closed disclosure content cannot take focus — `focus()` on it succeeds as a call
   * and does nothing observable. See {@link revealFirstInvalidControl}.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly roleStore = inject(RoleStore);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);

  /**
   * The identity, read for ONE fact: which tenant the caller belongs to.
   *
   * The tenant is taken from the caller rather than from a route segment, because this screen
   * addresses no portal and must never be able to protect one tenant's roles using another
   * tenant's keys.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The tenant's own record, for the two protected role keys and the processor state.
   *
   * CORE state, which every feature may inject — this is not a reach into the portal FEATURE, and
   * the account listing already reads the same slice to protect its removal command. The store
   * owns the request, de-duplicates it across screens and discards one tenant's facts the moment
   * another tenant is asked for.
   */
  private readonly portals = inject(PortalStore);

  /*
   * MIGRATION: a `DestroyRef` used to be held here to bound every request to this screen's lifetime,
   * because a response arriving after the operator had left would have navigated them back. The
   * store now owns every request and cancels them on its own terms, and the two outcome bridges are
   * effects created in the injection context - so they are retired with the component and there is
   * nothing left for this screen to bound.
   */

  // -------------------------------------------------------------------------
  // LOCAL STATE
  // -------------------------------------------------------------------------


  /** The role currently loaded, or `null` in creation mode and before the first response. */
  private readonly loadedRole: WritableSignal<Role | null> = signal<Role | null>(null);

  /**
   * The role key whose read this screen is waiting for, or `null` when it is waiting for none.
   *
   * ⚠ A MARKER OF OUR OWN RATHER THAN THE STORE'S SHARED FLAG. The store raises one loading
   * flag per slice for the whole application, so a read started by another screen would
   * otherwise settle this one - applying a role this screen never asked for, or reporting a
   * failure that belongs to somebody else. Set when the read is dispatched and cleared by the
   * read bridge when it settles.
   */
  private readonly awaitedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /** Which mutation this screen is waiting for, or `null` when it is waiting for none. */
  private readonly awaitedMutation: WritableSignal<AwaitedRoleMutation | null> =
    signal<AwaitedRoleMutation | null>(null);

  /**
   * The identifier the store issued for {@link RoleFormComponent.awaitedMutation}.
   *
   * ⚠ WITHOUT THIS, THE OPERATION NAME ALONE DECIDED WHOSE WRITE HAD SETTLED, AND IT CANNOT.
   * `RoleStore` is provided at the application root, so the listing screen and this form share one
   * instance and their writes overlap. The bridge below waited for the store's aggregate flag to fall
   * and then announced success and NAVIGATED AWAY — so an unrelated role write settling first took
   * this operator off a form whose own save was still in the air, told them it had worked, and left
   * nowhere for the real answer to be reported. If that unrelated write had FAILED, the shared failure
   * slot made this form report somebody else's refusal as its own.
   *
   * Zero means "no write of ours is outstanding", which is safe rather than a sentinel collision: the
   * store pre-increments, so 1 is the first identifier it ever issues and no real write holds 0.
   */
  private readonly awaitedMutationId: WritableSignal<number> = signal<number>(0);

  /**
   * The role key already applied to the form.
   *
   * Stops a second arrival of the SAME role overwriting what the operator has typed. The store
   * replaces its selected role on a successful update, so without this the form would be reset
   * from the server's echo the moment a save succeeded.
   */
  private readonly appliedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /** The last refusal, as an RFC 7807 document, for the shared error banner. */
  private readonly failure: WritableSignal<ProblemDetails | null> = signal<ProblemDetails | null>(
    null,
  );

  /**
   * True once a save has been refused because the role changed after this screen read it.
   *
   * Held separately from {@link failure} because the two are answered differently. A refusal document
   * is a message; THIS is a state the screen cannot leave by editing a field — every further save from
   * the same snapshot carries the same stale marker and is refused identically, so without a way to
   * re-read the role the form becomes permanently unsavable. The flag is what puts that way on screen.
   */
  private readonly staleRead: WritableSignal<boolean> = signal<boolean>(false);

  /** True once the user has asked to delete and before the dialog is settled. */
  private readonly deletePending: WritableSignal<boolean> = signal<boolean>(false);

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The role form.
   *
   * `roleName` carries the presence rule here because that is the CREATION state, which is
   * what the legacy rendered when it had no role id. {@link applyMode} removes the rule in edit
   * mode, mirroring `valRoleName.Enabled = False` at `EditRoles.ascx.vb:L134`.
   *
   * ⚠ THE PRESENCE RULE IS THE SHARED TRIM-AWARE ONE, NOT `Validators.required`, and the two are
   * not interchangeable. `Validators.required` rejects only the empty string, so three spaces
   * satisfy it — whereas an ASP.NET `RequiredFieldValidator` trimmed the value before comparing it
   * to its initial value and refused exactly that entry, and `CreateRoleRequestValidator` declares
   * `NotEmpty`, which treats a whitespace-only string as empty. Under `Validators.required` alone
   * this screen therefore declared valid what both the legacy screen and this API refuse. The
   * shared rule reports under the framework's own `required` key, so one message is outstanding
   * rather than two, and it is the SAME rule the sibling role-group form carries — the two screens
   * refuse a whitespace-only name at the same moment and in the same words.
   *
   * `description` carries `Validators.maxLength` and NOTHING ELSE, because `txtDescription`
   * (`editroles.ascx:L39-L40`) carried no validator at all — only the browser-side `MaxLength`.
   *
   * ⚠ NO CHARACTER RULE ON THE NAME, AND THAT IS MEASURED RATHER THAN OVERLOOKED. `editroles.ascx`
   * declares nine validators (L29-L31, L90-L96, L108-L114, L123-L128, L140-L146) and not one of
   * them matches a pattern; no in-scope administration screen declares a pattern-matching validator
   * of any kind. A character restriction here would refuse names the legacy accepted and that may
   * already be stored — and, because `UpdateRole` cannot change a name, such a role could then
   * never be saved again from this screen at all. It would also disagree with
   * `CreateRoleRequestValidator`, which declares presence and a length bound and nothing else.
   * Markup-bearing text is safe by a different mechanism: every value this application renders goes
   * through Angular interpolation, which escapes it, so a name containing angle brackets is
   * displayed as text and is never parsed as markup.
   *
   * The portal alias field is not a counter-example, and the difference is not that one screen was
   * given more care. NO legacy administration screen validated an alias by pattern either; the
   * alias vocabulary is measured from the help text the legacy printed beside that field
   * (`Website/admin/Portal/App_LocalResources/EditPortalAlias.ascx.resx:L124`), which names the four
   * forms it accepts because an alias is A HOST NAME resolved by the request pipeline — a value
   * whose legal characters are defined outside this application. A role name is free text a person
   * chose, and nothing outside this application constrains it.
   *
   * Each `maxLength` rule is declared here as well as being expressed as the template's
   * `maxlength` attribute, because the legacy `MaxLength` truncated in the browser and a rule is
   * needed for a value that arrives by any other route.
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
      validators: [Validators.maxLength(RSVP_CODE_MAX_LENGTH)],
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
   * The role id as a number, or `null` when this is the creation route.
   *
   * ⚠ THE SHARED PARSER, NOT A LOCAL ONE, AND REPLACING THE LOCAL ONE CLOSED TWO REAL DEFECTS.
   * This computed used to parse inline, and the inline rules differed from the parser every other
   * feature uses in exactly the two ways that matter:
   *
   *  * NO RANGE CHECK. `2147483648` is a well-formed integer that round-trips as text, so it was
   *    accepted and TRANSMITTED - `GET /api/v1/roles/2147483648` - where the API's own `int`
   *    binding cannot represent it. Every identifier column in this schema is a SQL Server `int`,
   *    so a value outside that range cannot name a record and the round trip was guaranteed to
   *    fail. The portal and user screens refused it without a request; this one did not.
   *  * A ROUND-TRIP TEST THAT REFUSED LEADING ZEROS. `String(parsed) !== text` rejects `"00001"`,
   *    which is a well-formed decimal integer naming role 1 and which the API's own `int.TryParse`
   *    accepts. The refusal was not even visible as a refusal: a null key means "creation route",
   *    so `/roles/00001` silently rendered the CREATE form under an Edit heading. The portal and
   *    user screens normalise it to 1.
   *
   * The shared parser refuses on grammar, on exactly-representable magnitude and on signed 32-bit
   * range, and accepts a leading zero. It accepts `-1` and `0`, which are real identifiers in this
   * schema - `Roles.RoleID` is seeded `IDENTITY(0, 1)` - so nothing here tests the value for
   * truthiness or for being positive.
   *
   * ⚠ ONE TOLERANCE IS DELIBERATELY GIVEN UP: SURROUNDING WHITESPACE. The legacy read the id with
   * `Int32.Parse(Request.QueryString("RoleID"))` (`EditRoles.ascx.vb:L100-L101`) under
   * `NumberStyles.Integer`, which admits leading and trailing white space, so `" 1 "` was role 1 to
   * the legacy screen; a URL segment carrying `%20` decodes to that. The shared parser refuses it,
   * for a reason that applies to this screen as much as to the others: tolerating it makes several
   * distinct addresses name one record, which multiplies the addresses a cache key, a browser
   * history entry and a current-page comparison all have to treat as equal. The legacy identifier
   * was query-string state on one page rather than a path segment, so no legacy address is being
   * rejected here - and the divergence is now recorded once, in the shared module, rather than
   * decided differently on each screen.
   */
  protected readonly roleKey: Signal<number | null> = computed(() => parseRouteId(this.roleId()));

  /**
   * True when this is the edit form.
   *
   * DERIVED FROM PRESENCE, NEVER FROM VALUE — see the note on {@link roleId}. `roleKey` is `null`
   * only when the route supplied no parameter or supplied one that is not an integer; a parsed `0`
   * makes this `true`.
   */
  protected readonly isEditMode: Signal<boolean> = computed(() => this.roleKey() !== null);

  /**
   * Whether the address carries something that is not a role identifier.
   *
   * ⚠ THE STATE THIS SCREEN PREVIOUSLY COULD NOT SEE, and the note on {@link isEditMode} above
   * recorded the conflation without recognising it: `roleKey` is null when the route supplied no
   * parameter OR supplied one that is not an integer, and `isEditMode` tested only that null. So
   * `/roles/abc` reported itself as CREATE mode and rendered the whole role form with every control
   * enabled. It was the most deceptive of the four screens that behaved this way, because the address
   * and the submit command disagreed with each other: together they told the operator they were
   * editing a role called `abc`, while submitting would have created one.
   *
   * Presence is tested on the RAW input and readability on the parsed key, so the two questions stay
   * separate. Absent still means create - that is the legitimate `/roles/new` path - and unreadable
   * now means the address names nothing, which the template reports instead of offering a form.
   *
   * ⚠ DELIBERATELY NOT USING THE SHARED ROUTE-IDENTIFIER READER, whose grammar refuses surrounding
   * whitespace. This screen tolerates it for a legacy reason recorded on {@link roleKey}:
   * `Int32.Parse` uses `NumberStyles.Integer`, which admits white space, so `" 1 "` was role 1 to the
   * legacy screen, and a URL segment can carry `%20`. Adopting the shared grammar here would refuse
   * an address the legacy served, so the local grammar is kept and only the CONFLATION is fixed.
   */
  protected readonly addressUnreadable: Signal<boolean> = computed(
    () => this.roleId() !== undefined && this.roleKey() === null,
  );

  /** The sentence shown when the address does not name a readable role. */
  protected readonly unreadableAddressMessage = UNREADABLE_ADDRESS_MESSAGE;

  /**
   * The heading, which differs by mode.
   *
   * ⚠ AN UNREADABLE ADDRESS TAKES THE EDIT HEADING. Without the second term, `/roles/abc` showed
   * `Add New Role` above the sentence saying the address names no role, while the route's own
   * document title said `Edit Security Roles` — three labels disagreeing on one screen, measured in
   * a real browser. The heading's question is whether the address NAMES a role, not whether that
   * role could be resolved: `/roles/abc` names one and fails, `/roles/new` names none.
   */
  protected readonly heading: Signal<string> = computed(() =>
    this.isEditMode() || this.addressUnreadable() ? EDIT_TITLE : ADD_TITLE,
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
  /** The accessible name of the billing-frequency select. */
  protected readonly billingFrequencyAccessibleName = BILLING_FREQUENCY_ACCESSIBLE_NAME;

  /** The accessible name of the trial-frequency select. */
  protected readonly trialFrequencyAccessibleName = TRIAL_FREQUENCY_ACCESSIBLE_NAME;

  protected readonly frequencyOptions: readonly RoleFormOption<BillingFrequency>[] =
    BILLING_FREQUENCY_OPTIONS;

  /** True while either the role or the role-group list is still arriving. */
  protected readonly loading: Signal<boolean> = computed(
    () => this.awaitedRoleKey() !== null || this.roleStore.roleGroupsLoading(),
  );

  /**
   * True while a mutation issued by THIS screen is in flight; the template disables its
   * commands on this.
   *
   * Derived from the marker rather than from `roleStore.saving()`, so a write started on
   * another screen cannot disable this form, and so the commands stay disabled until the
   * outcome bridge has actually reported - not merely until the request returned.
   */
  protected readonly saving: Signal<boolean> = computed(() => this.awaitedMutation() !== null);

  /** The refusal to render in the shared error banner, or `null`. */
  protected readonly problem: Signal<ProblemDetails | null> = this.failure.asReadonly();

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
   * Qualifier appended to the accessible name of the COUNT half of a period pair.
   *
   * MIGRATION: NET-NEW WORDING, and the smallest addition that resolves a real ambiguity. The
   * legacy screen put `txtBillingPeriod` and `cboBillingFrequency` under the SINGLE label
   * `plBillingPeriod` (`editroles.ascx` L98-L115), and repeated the arrangement for the trial pair
   * at L130-L147, so the second control of each pair carried no name of its own at all. The shared
   * field reproduces the composite faithfully and names every otherwise-unnamed projected control
   * from the field's visible label — which is correct as far as it goes and leaves BOTH members of
   * a pair announcing the identical string. Runtime testing measured the consequence: eight
   * controls on this form resolved to six distinct names.
   *
   * The qualifier is CLIPPED, so it is announced and never drawn: the visible label stays exactly
   * the wording the resource file declares, which is what keeps this a naming change and not a
   * visual one. It is APPENDED to the visible label rather than replacing it, so the visible text
   * remains a prefix of the accessible name and WCAG 2.5.3 Label in Name still holds.
   *
   * The two words are drawn from the field's own help sentence — 'e.g 2 weeks, or 1 month' — which
   * states the pair as a count followed by a unit. No new vocabulary is invented beyond naming the
   * two halves that sentence already describes.
   */
  protected readonly periodCountQualifier = 'count';

  /** Qualifier appended to the accessible name of the UNIT half of a period pair. See above. */
  protected readonly periodUnitQualifier = 'unit';

  /**
   * The notice shown while the role name is holding as many characters as the column can store.
   *
   * ⚠ THIS EXISTS BECAUSE THE TRUNCATION IS OTHERWISE INVISIBLE. `maxlength` is declared on the
   * control — faithfully, because `editroles.ascx:L31` declares `MaxLength="50"` and the legacy
   * browser enforced it the same way — and a browser enforcing it DISCARDS the surplus characters
   * silently. Runtime testing pasted a fifty-one character name, watched it become a fifty
   * character name with no indication of any kind, and then received a duplicate-name refusal
   * naming a role the operator had never typed: the shortened name collided with an existing one.
   * The refusal was truthful and unintelligible at the same time, because the value it described
   * was not the value that had been entered.
   *
   * The notice states the limit and that it has been reached. It is announced politely rather than
   * assertively: reaching a limit is not an error — the value is valid — and interrupting on every
   * keystroke at the boundary would be worse than saying nothing.
   *
   * MIGRATION: a NET ADDITION with no legacy counterpart. The legacy screen's only length rule was
   * the `MaxLength` attribute itself, which reports nothing by design.
   */
  protected readonly nameAtLimitNotice =
    `Maximum length reached. A role name may hold ${ROLE_NAME_MAX_LENGTH} characters, ` +
    `and any further characters are not accepted.`;



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
   * The comparison is against a numeric id, and it is `false` while the tenant's record is still
   * outstanding — the fail-safe direction recorded on {@link administratorRoleId}: the form stays
   * editable and the API's refusal governs for the duration of one request, rather than a
   * capability being withheld from every role until a read completes.
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
   * configured, which is what the code did rather than what its comment claimed.
   *
   * ⚠ WITHHELD UNTIL THE TENANT'S RECORD RESOLVES, which is the one place this screen departs
   * from the legacy expression. The legacy's first clause, `objPortalInfo Is Nothing`, warned when
   * the portal could not be read at all; here an unread portal shows nothing, because telling an
   * administrator to configure a payment processor on the strength of a request that has not
   * answered is an assertion rather than a default. Once the record arrives the reproduction is
   * exact.
   */
  protected readonly showProcessorWarning: Signal<boolean> = computed(
    () => this.protectedFactsResolved() && !this.paymentProcessorConfigured(),
  );


  // -------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------

  public constructor() {
    // `BindGroups()` at `EditRoles.ascx.vb:L127`, hoisted to the shared store so that the list is
    // fetched once per session rather than once per visit to this screen. The store owns the
    // request and its loading flag; this screen only reads the result.
    this.roleStore.loadRoleGroups();

    // The tenant's own record, for the two protected role keys and the processor state. Read from
    // the CALLER'S identity and never from a route, and idempotent in the store — several screens
    // asking on initialisation issue one request between them. Presence is tested explicitly
    // because `Portals.PortalID` is `IDENTITY(-1, 1)`, so -1 and 0 are both real tenants and a
    // truthiness test would silently skip the request for either.
    const portalId: number | undefined = this.auth.currentUser()?.portalId;

    if (portalId !== undefined) {
      this.portals.loadCurrentPortalContext(portalId);
    }

    // Reacts to the route parameter. Reading `roleKey()` is the ONLY dependency taken here, so the
    // role is re-read when the route moves from one role to another — which happens without the
    // component being recreated, because both edit visits resolve to the same route
    // configuration — and is not re-read when anything else in the screen changes.
    //
    // ⚠ THE BODY IS `untracked` AND THAT IS LOAD-BEARING, NOT TIDINESS. Everything below the first
    // line is imperative work that reaches into the store, and a store command reads store state on
    // its way — clearing the held failure begins by testing whether there is one. Left tracked, this
    // effect would therefore depend on that failure, and the sequence was demonstrable: a refused
    // write recorded a failure, this effect re-ran, its role read cleared the failure it had just
    // reacted to, and the write bridge then found no failure and announced the write as a SUCCESS.
    // A refusal reported as a success, plus a second role read nobody asked for.
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

    /*
     * THE READ BRIDGE.
     *
     * The completion point the store's `void`-returning read does not provide. It acts only while
     * this screen is waiting for a read of its own - the marker - and only once that read has
     * settled, which is what stops a read dispatched by another screen applying a role here or
     * reporting somebody else's failure.
     *
     * The failure is matched on the operation as well, because the store holds ONE failure slot for
     * every command: a write that failed while a read was outstanding would otherwise be reported
     * as an unreadable role and bounce the operator off a form they were still filling in.
     */
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
          // `:L170-L172` treated an unreadable role as an attempt to reach an item outside the
          // module and bounced to the Security Roles page. A missing role does the same here.
          //
          // ⚠ AND IT IS DELIBERATELY NOT UNIFIED WITH THE PORTAL AND USER SCREENS, WHICH KEEP THE
          // ADDRESS AND EXPLAIN IN PLACE. The three screens behave differently because the three
          // legacy screens behaved differently, and each difference is in the source:
          //
          //   * ROLES redirect. `EditRoles.ascx.vb:L170-L172` is `Response.Redirect(NavigateURL(
          //     "Security Roles"))`, under a comment naming it a security violation - an attempt to
          //     reach an item outside the module. The address is discarded BECAUSE it was treated as
          //     an address the operator should not have been at.
          //   * USERS stay and warn. `ManageUsers.ascx.vb:L207,L214,L221,L273` call
          //     `AddModuleMessage("NoUser"/"InvalidUser", YellowWarning, True)` and then
          //     `DisableForm()`, leaving the operator where they were with the form inert.
          //
          // Making all three alike would therefore mean overriding measured legacy behaviour on at
          // least one screen, which the migration discipline's behavioural-equivalence rule forbids
          // without cause. The one thing added here is the MESSAGE: the legacy bounce was silent, and
          // a silent redirect is indistinguishable from a broken link.
          if (failure.status === NOT_FOUND) {
            // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS MESSAGE IS NEVER SEEN. The
            // shell retires notifications on a completed navigation, and this one is raised in the
            // same task as the navigation below - so it was raised and swept before it could be
            // painted. Measured in a real browser: the destination's live region stayed empty and
            // 226 consecutive frames after the listing painted were pixel-identical, so nothing
            // appeared and nothing was dismissed. The message must survive exactly one navigation,
            // which is what this marks.
            this.notifications.notify('warning', ROLE_NOT_FOUND_MESSAGE, null, true);
            this.navigateToList(true);

            return;
          }

          this.reportFailure(failure, LOAD_FAILED_MESSAGE);

          return;
        }

        // Compared on identity with a strict equality, never on truthiness: the role table is
        // seeded `IDENTITY(0, 1)`, so a role key of zero is a real role and a falsy test would
        // refuse to apply the tenant's first role to the form.
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

    /*
     * THE WRITE BRIDGE.
     *
     * Announces and navigates ONLY once the write has actually settled, which is the defect the
     * review recorded: the previous code announced success and left the screen from inside the
     * subscription's `next`, which is correct, but it also held a second copy of the outcome that
     * the store knew nothing about. Three conditions decide - the marker proves the write was ours,
     * the flag falling proves it settled, and the operation-matched failure proves which way.
     */
    effect(() => {
      const awaited: AwaitedRoleMutation | null = this.awaitedMutation();
      const awaitedId: number = this.awaitedMutationId();
      const settled = this.roleStore.mutation();

      // ⚠ SETTLED ON THE IDENTIFIER THE STORE HANDED BACK AT DISPATCH, not on the aggregate flag
      // falling. A published result whose identifier is not ours belongs to another screen's write and
      // is ignored — which is a total test, needing no knowledge of what else is in flight.
      if (awaited === null || settled === null || settled.id !== awaitedId) {
        return;
      }

      untracked(() => {
        this.awaitedMutation.set(null);
        this.awaitedMutationId.set(0);

        // The failure travels ON the settled result. Reading the store's shared slot instead — as this
        // bridge used to — could find a concurrent write's refusal, or find nothing where our own had
        // been, because every dispatch clears that slot.
        const failure: RoleStoreFailure | null = settled.failure;

        if (failure !== null && settled.operation === awaited) {
          this.reportFailure(failure, MUTATION_FAILURE_MESSAGE[awaited]);

          return;
        }

        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS
        // ALREADY SAVED - AND DESTROYS THE CONFIRMATION BELOW WHILE IT ASKS. The probe registered on
        // this class reads `dirty && saving() === false`, and `saving()` is `awaitedMutation() !== null`,
        // which the first line of this very block has just set to null - so from here on the probe sees
        // a dirty form with no write in flight, and `afterMutation()` on the last line is a navigation
        // it can refuse. Marking the form settled is the honest statement of what happened: every
        // control's value is now what the server holds.
        //
        // The knock-on was worse than the prompt itself and is why this is not merely cosmetic.
        // `window.confirm` blocks the JavaScript thread, so the auto-dismiss timer attached to the
        // notification raised on the next line became due WHILE the dialog stood and fired the instant
        // it was accepted - measured in a real browser with a MutationObserver, which recorded the
        // success notice being emitted and then removed without ever having been painted. The
        // confirmation on the delete path, which registers no such prompt, stayed visible throughout.
        // So the guard was not just asking a redundant question, it was swallowing the answer to it.
        //
        // Marked for EVERY settled operation rather than only the two writes: after a deletion the
        // role is gone, so entry still standing in the controls is work that can no longer be saved,
        // and an operator who typed into the form and then deleted the role was asked the same
        // redundant question. Marking an already-pristine form is a no-op, so one unconditional pair
        // here is both narrower and safer than a per-operation test.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        // Exempted from the navigation sweep for the reason recorded on the not-found path above:
        // `afterMutation` navigates in this same task, and the shell's sweep would otherwise discard
        // this confirmation before it could be painted at the destination - which is exactly where
        // the legacy showed it, since its update and delete handlers announced and then redirected.
        this.notifications.notify('success', MUTATION_SUCCESS_MESSAGE[awaited]);
        this.notifications.retainAcrossNavigation();
        this.afterMutation();
      });
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
      this.revealFirstInvalidControl();
      return;
    }

    // ⚠ THE NAME IS TIDIED INTO ITS OWN CONTROL, NOT ON THE WAY INTO THE REQUEST, so the value
    // that was VALIDATED and the value that is SENT are the same string. Tidying into the request
    // made them different, and the difference was observable: the server refused a name the form
    // had just declared valid.
    //
    // WHAT REACHES HERE HAS NARROWED. The presence rule on this control is trim-aware, so a
    // whitespace-only entry is already invalid and was refused by the check above without ever
    // reaching this line. What survives to be tidied is a name that is genuinely present and
    // merely PADDED, so the tidy can no longer change whether the value is acceptable - only what
    // is stored.
    //
    // MIGRATION: trimming the padding is a deliberate divergence. The legacy stored what was
    // posted, spaces and all, and its `RequiredFieldValidator` never rewrote the box. It is made
    // because A ROLE NAME CANNOT BE CHANGED AFTERWARDS: `UpdateRole` has no `RoleName` parameter
    // at all (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L242-L243`),
    // so a name stored with invisible padding at creation could never be corrected through any
    // screen. The same reasoning is applied identically on the sibling role-group form.
    this.normaliseRoleName();

    // Retained as defence in depth rather than as a live branch. Trimming can only shorten, so
    // `maxLength` cannot be newly breached, and emptiness was settled above by a rule that
    // already trimmed - so nothing should be able to fail here. It stays because that argument
    // rests entirely on the presence rule remaining trim-aware, and a future change to the rule
    // set must not be able to let an unvalidated value through in silence.
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.revealFirstInvalidControl();

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
   * Trims the padding off the role name, in its own control.
   *
   * `emitEvent: false` because this is a DISPLAY CORRECTION rather than an operator edit: it must
   * not be able to start a cascade through any listener on this form — and this form has two, the
   * role-group reconciler and the protected-role lock, either of which reacting to a tidy-up would
   * be a side effect nobody asked for. `setValue` re-runs the control's validators regardless of
   * that flag, which is what lets the caller re-read validity immediately afterwards and find it
   * reflecting the tidied value.
   *
   * A control whose value is already trimmed is left completely alone, so an unnecessary write
   * cannot mark a pristine form dirty.
   *
   * ⚠ ONLY THE NAME. `description`, `rsvpCode` and `iconFile` pass through `textOrNull`, which
   * already collapses a blank entry to `null` for a member the contract declares nullable — a
   * different rule for a different obligation — and the two fee and period members are parsed
   * numerically. The name is the one member whose emptiness the server refuses outright.
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
   * Reads the role again after a refused save, so the screen can hold a current revision.
   *
   * ⚠ WITHOUT THIS COMMAND A CONFLICT IS A DEAD END. The marker sent with an update comes from the
   * role this screen read; a refusal does not change that role, so pressing Update again sends the
   * same refused marker and is refused again, indefinitely. Nothing the person can type reaches the
   * state that would let the save through.
   *
   * The obvious escape — reloading the browser — is worse than it looks on this application: the
   * access token is held in memory only, so a document reload ends the session and returns the person
   * to the sign-in screen, having lost the same unsaved edits AND their place. This command performs
   * the re-read within the running application instead.
   *
   * `appliedRoleKey` MUST be cleared first, and that is the whole mechanism rather than a detail. It
   * exists to stop a second arrival of the same role overwriting what someone has typed — which is
   * exactly right for the store's echo after a successful save, and exactly wrong here, where
   * replacing what has been typed is the entire purpose of the command. Clearing it converts the
   * arrival from "ignore, already applied" into "apply".
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
   * Opens the disclosure holding the first offending control, then focuses that control.
   *
   * ⚠ WITHOUT THIS, A REJECTED SUBMIT ON THIS SCREEN COULD REPORT NOTHING AT ALL. Four of the
   * form's twelve controls — both fees and both periods — live inside the Advanced Settings
   * `details`, which starts CLOSED. When one of them is the reason a submit is refused,
   * `markAllAsTouched()` renders its message into content that is not being displayed, and the
   * shared focus directive's `focus()` call lands on an element inside closed disclosure content,
   * where it succeeds as a call and moves focus nowhere. The person is left on a button that has
   * apparently done nothing, with the explanation sealed inside a section they were given no reason
   * to open. A reader gets even less: the `role="alert"` region is inside the same hidden subtree.
   *
   * This is not a hypothetical. A role whose stored terms already break these rules loads straight
   * into that state — `QA010 Negative Fee Role` carries a negative fee and negative periods, and
   * every portal created through the API is seeded with three roles holding a zero billing period
   * against a monthly frequency, faithfully reproducing what the legacy creation path wrote
   * (`PortalService.BuildStockRole`). Editing any of those roles — even to change only the
   * description — is refused by fields the person never touched and could not see.
   *
   * The disclosure is opened rather than the rule relaxed. The rules are `valServiceFee2`,
   * `valBillingPeriod2`, `valTrialFee2` and `valTrialPeriod2` reproduced from
   * `editroles.ascx:L96,L114,L128,L146`, and the API states each of them independently, so a stored
   * value that breaks one genuinely must be corrected before the role can be saved. What was wrong
   * was never the refusal — it was refusing in silence.
   *
   * ORDER-INDEPENDENT BY CONSTRUCTION. The shared directive listens on the same `submit` event as
   * this handler and Angular does not guarantee which runs first. Either order reaches the same
   * state: if the directive goes first its `focus()` is a no-op on hidden content and this method
   * then opens the section and focuses; if this method goes first the directive finds its target is
   * already the active element and declines. Nothing here duplicates a focus move or fights one.
   *
   * The selector is IMPORTED from the directive rather than restated, so the control this reveals is
   * by definition the control the directive would have chosen. A hand-copied approximation would
   * drift and the two would act on different elements.
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
   * Shows a typed number back in the form this screen stores it in, once the person leaves the box.
   *
   * ⚠ THIS EXISTS TO MAKE NORMALISATION VISIBLE, NOT TO PERFORM IT. Normalisation was already
   * happening and was already invisible: `007` was sent as `7`, `1234.5` as `1234.5` against a
   * screen that displays `1,234.50`, and nothing told the person their entry had been reinterpreted.
   * A value substituted behind someone's back is the same defect whether the substitution is
   * harmless or not, because there is no way to tell which it was without being shown. Writing the
   * canonical form into the control makes the entry and the submission the same string.
   *
   * MIGRATION: the legacy reached this outcome by round trip. Every postback re-rendered the fee
   * boxes through `Format(fee, "#,##0.00")` (`EditRoles.ascx.vb:L147,L155`), so a person who typed
   * `1234.5` and pressed any button saw `1,234.50` come back. That behaviour is reproduced here on
   * blur rather than on postback, because there is no postback to carry it.
   *
   * Two conditions bound the rewrite, and both matter:
   *
   * - The value must PARSE. Text that fails its data-type rule is left exactly as typed, because
   *   the person needs to see what they entered in order to correct it, and because there is no
   *   canonical form of something that is not a number.
   * - The control must be DIRTY. A loaded role's stored text is left alone when someone merely tabs
   *   through it. Rewriting a pristine control would change the form's value without anyone editing
   *   it, which would mark a form dirty that nobody has touched and would then have the
   *   unsaved-changes guard challenge an exit no one initiated.
   *
   * ⚠ RANGE IS DELIBERATELY NOT A THIRD CONDITION, AND THE ASYMMETRY IS THE POINT. Typing `-5` and
   * leaving the box yields `-5.00` while {@link moneyNotNegative} is simultaneously refusing it.
   * That looks like the form reformatting something it has just rejected, and it was examined as a
   * defect. It is not one, for four reasons:
   *
   * - The bound is that the value must PARSE, meaning it must be a NUMBER. {@link MONEY_PATTERN}
   *   admits a leading sign precisely so that the range rule can speak for itself with its own
   *   wording; folding range back into the data-type gate would collapse two questions that were
   *   separated on purpose, and would leave one of the two messages unreachable.
   * - `-5.00` is not a SUBSTITUTION. It is `-5` spelled the way this site stores money. The one
   *   precedent for a third condition is the {@link amountLoss} guard above, and that guard exists
   *   because its rewrite would change the DIGITS the person typed — a 21-digit entry reformatted
   *   through a double shows different numbers. Range does not make a rewrite wrong, so
   *   `amountLoss` is not a precedent for bounding on it.
   * - THE LOAD PATH ALREADY FORMATS UNCONDITIONALLY. `formatMoney` is applied to the stored fee
   *   when a role is patched in, with no range test, and negative fees EXIST in this database
   *   because the legacy accepted them through the collation accident documented above. A stored
   *   `-5` therefore already renders as `-5.00`. Bounding only the typed path on range would show
   *   one value two different ways depending on how it arrived, which is a new inconsistency
   *   bought for nothing.
   * - NOTHING IS CONCEALED. The refusal is on screen, the control carries `aria-invalid`, the
   *   submit is blocked, and the API compares numerically as well, so a canonically displayed
   *   out-of-range amount cannot be mistaken for an accepted one by the person or by the server.
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

    // An amount this site cannot store is left as typed as well. Reformatting it would replace the
    // digits the person entered with the ones a double happens to hold, which is the very
    // substitution `moneyStorable` refuses to let through.
    if (money && amountLoss(current, parsed) !== null) {
      return;
    }

    const canonical = money ? formatMoney(parsed) : String(parsed);
    if (canonical.length === 0 || canonical === current) {
      return;
    }

    control.setValue(canonical, { emitEvent: false });
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
  /**
   * True while the role name control holds as many characters as the column can store.
   *
   * A METHOD rather than a computed signal, for the same reason {@link messageFor} is one: nothing
   * on this form bridges `valueChanges` into a signal, and introducing a subscription for one
   * notice would put a second, independently-updated copy of the control's text beside the control
   * itself. Reading the control on each change-detection pass cannot disagree with what is drawn.
   *
   * `>=` rather than `===` because a value can arrive by routes the `maxlength` attribute does not
   * govern — a load from the API, or a programmatic write — and a notice that appeared exactly AT
   * the limit but fell silent beyond it would be precisely backwards.
   *
   * @returns Whether the length notice should be rendered.
   */
  protected nameAtLimit(): boolean {
    return this.form.controls.roleName.value.length >= ROLE_NAME_MAX_LENGTH;
  }

  protected messageFor(field: keyof RoleFormModel): string | null {
    const control = this.form.controls[field];
    if (!control.invalid || !(control.dirty || control.touched)) {
      return null;
    }

    const errors: ValidationErrors | null = control.errors;
    if (errors === null) {
      return null;
    }

    // The four ported validators first, in the order the legacy declared them: the data-type check
    // precedes the comparison on every one of the four numeric fields. THE TWO STORABILITY RULES COME
    // AFTER BOTH, deliberately — where a value breaks a legacy rule as well as a storability one, the
    // legacy sentence is the one shown, because it is the wording this screen has always used and it
    // names the correction the person must make. The storability messages therefore appear only when
    // nothing the legacy declared applies, which is exactly the band that used to fail silently or
    // fail on the server naming no field.
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
   * The presence rule MUST be lifted in edit mode. Leaving it in place on a control the user cannot
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
        : [requiredText, Validators.maxLength(ROLE_NAME_MAX_LENGTH)],
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
    // Routed through the store, which owns the request, its handle and its loading flag, and which
    // supersedes any earlier read of this slice before issuing this one - so moving from one role
    // to another cannot let the first answer land on the second form. The outcome, including the
    // not-found bounce `:L170-L172` performed, reaches this screen through the read bridge in the
    // constructor rather than through a subscription here.
    this.failure.set(null);
    this.awaitedRoleKey.set(key);
    this.roleStore.selectRole(key);
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
   * MIGRATION: THE FOUR PAID-TERM BOXES ARE BLANK FOR AN UNPRICED ROLE ON PURPOSE, AND THAT IS
   * EXACT LEGACY PARITY RATHER THAN A FALSY-CHECK DEFECT. It was reported as data loss - "stored
   * numeric zeros render as blank on a savable form, so a save can write null over a stored 0" - and
   * every part of that was checked against the legacy source and the running API before being
   * declined. `EditRoles.ascx.vb:L146-L156` is the legacy bind:
   *
   *   If Format(objRoleInfo.ServiceFee, "#,##0.00") <> "0.00" Then
   *       txtServiceFee.Text = ...  :  txtBillingPeriod.Text = ...  :  (billing frequency)
   *   End If
   *   If objRoleInfo.TrialFrequency <> "N" Then
   *       txtTrialFee.Text = ...    :  txtTrialPeriod.Text = ...
   *   End If
   *
   * So the legacy left Service Fee and Billing Period EMPTY whenever the formatted fee was "0.00",
   * and Trial Fee and Trial Period EMPTY whenever the trial frequency was "N" - gating the trial on
   * the FREQUENCY, not on the fee. The `priced` and `onTrial` predicates above are those two tests.
   * A role with a zero fee and no trial frequency therefore rendered all four boxes blank in the
   * legacy screen exactly as it does here.
   *
   * NO NULL CAN BE WRITTEN, and that is structural rather than incidental. When the boxes are blank
   * the submit resolves through {@link SUPPRESSED_TERMS} = `{ fee: 0, period: 1, frequency: 'N' }`,
   * which is `EditRoles.ascx.vb:L212-L214` and `:L223-L225` reproduced literally - the legacy
   * declared `sglServiceFee As Single = 0`, `intBillingPeriod As Integer = 1` and `"N"` as its own
   * defaults. Numbers are sent, never nulls.
   *
   * Verified on the wire rather than by reading alone: the API REFUSES a zero billing period outright
   * (`PUT /api/v1/roles/2` with `billingPeriod: 0` answers `400`, "Billing Period Must Be Greater
   * Than Zero"), so even a hand-crafted request cannot round-trip that stored zero back. The one real
   * consequence is that saving such a role moves its stored `billingPeriod` from 0 to 1 - the legacy's
   * own default, and the only value the write contract accepts. That is recorded in MIGRATION_NOTES as
   * a deliberate difference, not hidden here.
   *
   * @param role The role as the API reported it.
   */
  private applyRole(role: Role): void {
    this.loadedRole.set(role);

    // A role has just arrived from the server, so whatever snapshot was refused is no longer the one
    // on screen. Cleared here rather than in the reload command because this is the single point every
    // arrival passes through, including the first one.
    this.staleRead.set(false);

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
    this.awaitedMutation.set('createRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.createRole(this.toCreateRequest()));
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
    this.awaitedMutation.set('updateRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.updateRole(key, this.toUpdateRequest()));
  }

  /**
   * Deletes the role — `objUser.DeleteRole(RoleID, PortalSettings.PortalId)` at
   * `EditRoles.ascx.vb:L291`.
   *
   * `objEventLog.AddLog("RoleID", …, EventLogType.ROLE_DELETED)` at `:L293` is written by the API,
   * for the reason given on {@link createRole}.
   *
   * The listing is NOT re-read from here. This screen departs for the listing as soon as the delete
   * settles, and the listing reads itself from its own address on arrival; asking for a second,
   * identical read issued one that the route teardown then cancelled mid-flight. The listing's own
   * row-level delete passes `true` instead, because nothing carries it anywhere.
   *
   * @param key The role id being deleted, which may legitimately be `0`.
   */
  private deleteRole(key: number): void {
    this.failure.set(null);
    this.awaitedMutation.set('deleteRole');
    // The identifier is captured from the command's own return value, so the bridge waits on the very
    // write dispatched here rather than on "a write of this kind, from anywhere".
    this.awaitedMutationId.set(this.roleStore.deleteRole(key, { thenReadListing: false }));
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
    // ⚠ NO RE-READ IS ASKED FOR HERE ANY MORE. The store refreshes the listing itself after a
    // creation and after a deletion, and patches the changed row immutably after an update, so a
    // read requested here would be a second identical request racing the store's own - and
    // whichever answered last would decide what the listing showed.
  /*
   * ⚠ THE ADDRESS IS REPLACED, NOT PUSHED, because the work this screen existed for is finished.
   * A create route left in back history sends BACK to a form for a record that now exists - one
   * review measured exactly that, landing on a completely empty form with the create confirmation
   * still on screen above it - and a delete route sends BACK to a screen for a record that no
   * longer does. Neither is a place the operator can return to, so neither may occupy an entry.
   * The cancel paths on this screen deliberately keep pushing: an operator who changed their mind
   * may reasonably change it back.
   */
    this.navigateToList(true);
  }

  /** Returns to the role list, the destination of every `NavigateURL()` on this screen. */
  private navigateToList(replaceEntry = false): void {
    /*
     * ⚠ THE CALL IS MADE TWO DIFFERENT WAYS ON PURPOSE, rather than always passing an options
     * object with a computed flag. A pushed departure keeps the exact call it always made, so the
     * behaviour of the cancel paths - and the specifications that pin them - is untouched by the
     * addition; only a REPLACING departure carries options, which is the case whose behaviour
     * genuinely changed. Written this way, the diff says what changed and nothing else.
     */
    if (replaceEntry) {
      void this.router.navigate([ROLE_LIST_ROUTE], { replaceUrl: true });

      return;
    }

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
      // Already tidied INTO the control by `onSubmit`, and re-judged there, so the value read here
      // is the value the form declared valid. Trimming again would be harmless but would also
      // restore the impression that this is where tidying belongs.
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

      // ⚠ THE TOKEN FROM THE READ THIS EDIT WAS COMPOSED AGAINST, CARRIED THROUGH UNTOUCHED.
      //
      // This is the whole of the conflict check on this side, and its correctness rests on WHERE the
      // value comes from: `loadedRole()` holds the role exactly as it was served, so the token sent is
      // the token of the revision the person actually saw. It is never re-read, refreshed or re-fetched
      // before a save — doing so would obtain the CURRENT revision and the check would then always
      // pass, defeating itself while looking watertight.
      //
      // `null` in create mode, where there is no prior revision to be in conflict with, and `null` when
      // the API served no token — in which case the update is unconditional, exactly as it was before
      // this member existed. Nothing is invented to fill the gap.
      concurrencyToken: loaded?.concurrencyToken ?? null,
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
   * MIGRATION: NO CROSS-FIELD VALIDATION IS ADDED BETWEEN FREQUENCY AND PERIOD, and the request for
   * one is declined here rather than left unanswered. It was reported that choosing "Month" with an
   * EMPTY Billing Period leaves the form valid with Update enabled, "so a fee-bearing role with a
   * recurrence unit but no interval would post". It would not: that combination fails the
   * `period === null` test immediately above and resolves to the suppressed defaults, so what posts
   * is fee `0`, period `1`, frequency `'N'` - a free role with no recurrence at all. The state the
   * finding warns about cannot be reached through this method.
   *
   * And a refusal would be a REGRESSION rather than a hardening. `EditRoles.ascx.vb:L216` gates all
   * three values on one conjunction and, failing it, silently substitutes the defaults - it raises no
   * validator, marks no control and reports nothing. Refusing the submission would therefore reject
   * input the legacy accepted, which is the opposite of parity; the sibling
   * {@link resolveTrialTerms} records the same decision for the same reason. The cost is a silent
   * resolution, and it is the legacy's own silence, carried across deliberately.
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
   * ⚠ THE SEVERITY IS THE STORE SUMMARY'S, AND THIS METHOD DECIDES NONE OF IT.
   *
   * MIGRATION: a second severity table used to live here — 401 and 403 to warning, 409 to error,
   * everything else to error — and it DISAGREED with the shared classifier the same screen's banner
   * uses, on two statuses that this API really emits:
   *
   *   * a `404` resolves to WARNING in `problemSeverity` and was reported as an ERROR here, so
   *     deleting a role another operator had already removed painted a calm banner beside an alarming
   *     notification, on the same screen, about the same response;
   *   * a `429` resolves to INFO — quieter than the refusals on purpose, because nothing was rejected
   *     on its merits and the only action is to wait — and was likewise reported as an ERROR.
   *
   * The intent behind the old table was right and its placement was wrong, which is the same
   * correction `error-banner.component.ts` records for its own former rate-limit special case. The
   * classification belongs to the shared function, which owns the rule and states its legacy evidence:
   * `Website/admin/Security/AccessDenied.ascx.vb` raises `ModuleMessageType.YellowWarning` on BOTH of
   * its branches (`:L43`, `:L45`) and never `RedError`, so being told one lacks permission is the
   * ANSWER to a request rather than a failure of it. A severity this screen wants for a status is now a
   * change to that function, never a table here.
   *
   * WHAT THIS METHOD STILL OWNS IS THE WORDING, and only where the legacy supplied some. A conflict on
   * the creation path is the duplicate-name refusal the legacy detected for itself and carries the
   * legacy sentence as its fallback; a permission refusal carries the access-denied sentence. Both are
   * FALLBACKS: the server's own `detail` wins when it sent one, which is what
   * {@link ProblemSummary.message} already resolves.
   *
   * The document is stored whole, so `traceId` and `correlationId` survive into the banner for
   * support to quote.
   *
   * @param failure The store's record of the refusal, whose summary carries the severity.
   * @param fallback The wording to use when the document says nothing useful.
   */
  private reportFailure(failure: RoleStoreFailure, fallback: string): void {
    // ⚠ THE STATUS IS READ FROM THE STORE'S RECORD, NOT FROM THE DOCUMENT. The two go missing
    // independently - a transport failure has a status and no document - so deriving one from the
    // other would collapse the distinction the wording branches below exist to make.
    const problem: ProblemDetails | null = failure.problem;
    const status: number | null = failure.status;

    this.failure.set(problem);

    if (problem !== null) {
      this.applyFieldErrors(problem);
    }

    // The one classification, resolved once by the shared function and consumed here.
    const severity: NotificationSeverity = failure.summary.severity;

    if (status === UNAUTHORIZED || status === FORBIDDEN) {
      this.notifications.notify(severity, problemDetailsMessage(problem, ACCESS_DENIED_MESSAGE));
      return;
    }

    // ⚠ THE TWO `409`s ARE NOT THE SAME FAILURE and must not share a response. A duplicate name is
    // corrected in a field; a stale read cannot be corrected in the form at all, because every
    // subsequent save carries the same refused marker. Keyed on the published failure code, so the
    // branch cannot be selected by a coincidence of status.
    if (status === CONFLICT && failureCode(problem) === CONCURRENCY_CONFLICT_CODE) {
      this.staleRead.set(true);
      this.notifications.notify(
        severity,
        problemDetailsMessage(problem, CONCURRENCY_CONFLICT_MESSAGE),
      );

      return;
    }

    if (status === CONFLICT) {
      this.notifications.notify(severity, problemDetailsMessage(problem, DUPLICATE_ROLE_MESSAGE));
      return;
    }

    this.notifications.notify(severity, problemDetailsMessage(problem, fallback));
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
