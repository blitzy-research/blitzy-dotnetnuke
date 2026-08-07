import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  TemplateRef,
  ViewChild,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { fieldErrorMessages, stripLegacyBreakTags } from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import type { OnInit, Signal } from '@angular/core';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  CreateProfilePropertyDefinitionRequest,
  ProfilePropertyDefinition,
  UpdateProfilePropertyDefinitionRequest,
} from '../../../core/models/profile.model';
import type { UserFailure } from '../../../core/state/user.store';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';

// ---------------------------------------------------------------------------
// WORDING
// ---------------------------------------------------------------------------
//
// STANDING RULE APPLIED THROUGHOUT: every string below is the VALUE from a legacy
// resource file, never a markup `HeaderText`, `Text` or `ErrorMessage` attribute. The
// two are not the same thing and they disagree in this screen more than once:
// `ProfileDefinitions.ascx.vb` L409-L411 ran `Localization.LocalizeDataGrid`, which
// REPLACED every declared `HeaderText` with the resource value before the grid was ever
// painted, so the markup's text was never what a reader saw.
//
// The double spaces are deliberate and are reproduced byte for byte. They are in the
// resource values themselves, and normalising them would silently reword the product.

/** `ControlTitle_manageprofile.Text`. */
const PAGE_TITLE = 'Manage Profile Properties';

/** `ProfilePropertiesHelp.Text`, double spaces included. */
const HELP_TEXT =
  'You can change the order of the profile fields, and whether they are Required or ' +
  'Visible on this screen.  Click on the "Apply Changes" button to save any changes you ' +
  'make.  To edit other properties of each Profile Property click the pencil icon in the ' +
  'first column of the grid.';

/** `AddContent.Action` — the legacy module action that opened the editor. */
const ADD_LABEL = 'Add New Profile Property';

/** `cmdApply.Text`. */
const APPLY_LABEL = 'Apply Changes';

/** `cmdRefresh.Text`. Discards uncommitted edits; see {@link ProfileDefinitionListComponent.refresh}. */
const REFRESH_LABEL = 'Refresh Grid';

/**
 * The four per-row command labels.
 *
 * MIGRATION (DL-9): the two move commands take their wording from `MoveDown.Text` and
 * `MoveUp.Text`, which the legacy really did publish. The edit and delete commands have
 * NO resource value at all — `Edit.Header`, `Del.Header`, `Dn.Header` and `Up.Header` are
 * all `<value />`, i.e. EMPTY — so the four column HEADINGS rendered blank and the markup's
 * `"Edit"`, `"Del"`, `"Dn"` and `"Up"` were never displayed. Worse, `Page_Init` L379
 * overwrote the delete button's own `Text="Delete"` with
 * `Localization.GetString("Delete", LocalResourceFile)` against a resource file that
 * declares no `Delete` key, so the delete command reached assistive technology with NO
 * ACCESSIBLE NAME (DL-13). The headings stay hidden here, which preserves the visual
 * parity, but every command carries a real name, which closes the accessibility defect at
 * zero visual cost.
 */
const EDIT_LABEL = 'Edit';
const DELETE_LABEL = 'Delete';
const MOVE_UP_LABEL = 'Move Up';
const MOVE_DOWN_LABEL = 'Move Down';

/** Column headings, from the eight non-empty `*.Header` values. */
const NAME_HEADING = 'Name';
const CATEGORY_HEADING = 'Category';
const DATA_TYPE_HEADING = 'DataType';
const LENGTH_HEADING = 'Length';
const DEFAULT_VALUE_HEADING = 'Default Value';
const VALIDATION_EXPRESSION_HEADING = 'Validation Expression';
const REQUIRED_HEADING = 'Required';
const VISIBLE_HEADING = 'Visible';

/** `Introduction_Add.Title` and `Introduction.Title` — resolved by `GetText` L204-L220. */
const CREATE_HEADING = 'Add New Property Details';
const EDIT_HEADING = 'Edit Property Details';

/** `cmdCreate.Text`, `cmdUpdate.Text` and `cmdCancel.Text`. */
const CREATE_SUBMIT_LABEL = 'Create New Property';
const EDIT_SUBMIT_LABEL = 'Update Property';
const CANCEL_LABEL = 'Return to Profile Properties List';

/**
 * The nine field labels and their help text, from the
 * `ProfilePropertyDefinition_<member>.Text` and `.Help` pairs.
 *
 * ⚠ THE TYPO IS DELIBERATE. `ProfilePropertyDefinition_PropertyCategory.Help` reads
 * "dislayed" in the legacy resource value. It is reproduced verbatim rather than
 * corrected, because the wording is the product's and correcting it here would be an
 * unrequested content change in a migration whose whole discipline is behavioural
 * equivalence. It is recorded as an observed defect instead.
 */
const FIELD_TEXT = {
  propertyName: {
    label: 'Property Name:',
    help: 'Enter a name for the property',
  },
  dataType: {
    label: 'Data Type:',
    help: 'Select the Data Type for this field',
  },
  propertyCategory: {
    label: 'Property Category:',
    help:
      'Enter the category for this property.  This will allow the related properties to ' +
      'be grouped when dislayed to the user.',
  },
  length: {
    label: 'Length:',
    help:
      'Enter the maximum length for this property.  This will only be applicable for ' +
      'specific data types.',
  },
  defaultValue: {
    label: 'Default Value:',
    help: 'You can provide a default value for this property',
  },
  validationExpression: {
    label: 'Validation Expression:',
    help:
      'You can provide a Regular Expression to validate the data entered for this ' +
      'property',
  },
  required: {
    label: 'Required:',
    help: 'Set whether this property is required.',
  },
  visible: {
    label: 'Visible:',
    help: 'You can optionally display properties to the user.',
  },
  viewOrder: {
    label: 'View Order:',
    help: 'Enter a View Order for this property',
  },
} as const;

/**
 * Validation messages.
 *
 * Three are legacy resource values, transcribed exactly:
 * `ProfilePropertyDefinition_PropertyName.Required`,
 * `ProfilePropertyDefinition_PropertyName.Validation` and
 * `ProfilePropertyDefinition_PropertyCategory.Required`.
 *
 * ⚠ DL-10 — `NAME_PATTERN_MESSAGE` is a legacy resource value that UNDERSTATES the rule it
 * describes, and it is reproduced anyway. `.Validation` reads "The property name cannot
 * contain spaces", but the rule actually enforced was
 * `RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")` on `ProfilePropertyDefinition.vb`
 * L228, which rejects far more than a space — every bracket, slash, comma, colon and
 * accented letter with it. The PATTERN is authoritative, because the Minimal Change Clause
 * requires validation RULES to match; the resource value is authoritative for the MESSAGE,
 * because the standing wording rule takes every string from the resource value. Rewording
 * it to describe the rule accurately would be an unrequested content change, so the
 * divergence is REPORTED instead. Note that the API publishes its own, more precise
 * sentence for the same rule — "Property Name may contain only letters, numbers and the
 * characters . _ % - + '" — which is what a reader sees if a request ever reaches the
 * server with a name this client would have refused.
 *
 * ⚠ DL-11 — FOUR members carry `Required(True)`, not two. `DataType` (L88-L91) and
 * `ViewOrder` (L300) are required as well, and NEITHER has a resource value. The sentence
 * for the data type is AUTHORED in the register of the two that exist ("The <Field> is
 * required") and is reported as authored wording rather than presented as recovered legacy
 * text. The view order needs no such sentence: its control is `nonNullable` over a number,
 * so presence is structural — which is the same reason the API's own validator declares no
 * rule for it.
 */
const NAME_REQUIRED_MESSAGE = 'The Property Name is required';
const NAME_PATTERN_MESSAGE = 'The property name cannot contain spaces';
const CATEGORY_REQUIRED_MESSAGE = 'The Category is required';
const DATA_TYPE_REQUIRED_MESSAGE = 'The Data Type is required';

/**
 * Authored wording for the rules the legacy declared no validator for.
 *
 * MIGRATION: the legacy editor delegated entry to the excluded property-editor control,
 * which coerced silently under Option Strict OFF rather than reporting anything — the
 * asymmetry Phase 8 of the migration plan requires to be made explicit. These refuse the
 * input instead of coercing it, and each one is recorded as authored wording.
 *
 * The two length limits are NOT authored: they are the API's own sentences, reproduced so
 * that the same rule reads the same way whichever side reports it. Their widths are the
 * terminal column widths — `PropertyName nvarchar(50)`, `PropertyCategory nvarchar(50)`,
 * and 512 for the expression as a deliberate narrowing of the terminal `nvarchar(2000)`.
 * `DefaultValue` carries no limit at all, because `04.05.00:L1593` widened its column to
 * `ntext`.
 */
const WHOLE_NUMBER_MESSAGE = 'Enter a whole number.';
const EXPRESSION_INVALID_MESSAGE =
  'That is not a valid regular expression, so it cannot be used to validate this property.';
const NAME_TOO_LONG_MESSAGE = 'Property Name must be 50 characters or fewer';
const CATEGORY_TOO_LONG_MESSAGE = 'Property Category must be 50 characters or fewer';
const EXPRESSION_TOO_LONG_MESSAGE = 'Validation Expression must be 512 characters or fewer';

/** Terminal column widths the API also enforces. */
const NAME_MAX_LENGTH = 50;
const CATEGORY_MAX_LENGTH = 50;
const EXPRESSION_MAX_LENGTH = 512;

/** `SharedResources.resx` L120-L122 `DeleteItem.Text` — the legacy confirm text. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** Heading of the removal dialog. Authored: the legacy affordance was a browser confirm. */
const DELETE_CONFIRM_TITLE = 'Delete Profile Property';

/**
 * `DuplicateName.Text`, reproduced byte for byte including both double spaces.
 *
 * MIGRATION: the legacy signalled this outcome through a RETURN VALUE BELOW THE NULL
 * SENTINEL — `Wizard_NextButtonClick` L451-L455 assigned `AddPropertyDefinition`'s result
 * into the identifier and then tested `If PropertyDefinitionID < Null.NullInteger`, i.e.
 * "less than minus one". The target receives it as `409` carrying
 * `profile-definition.duplicate-name`, and presents it at the ERROR severity the legacy
 * chose (`ModuleMessageType.RedError`, not the yellow warning).
 */
const DUPLICATE_NAME_MESSAGE =
  'This Property already exists.  Property Names must be unique.  Please select a ' +
  'different name for this property.';

/** Authored: the `409` a removal draws when values are recorded against the declaration. */
const DEFINITION_IN_USE_MESSAGE =
  'That profile property is in use, so it was not deleted. Remove the recorded values ' +
  'first.';

/** Authored: the `404` a removal or replacement draws when the declaration is already gone. */
const DEFINITION_GONE_MESSAGE =
  'That profile property no longer exists. The list has been refreshed.';

/** The caption `app-data-table` projects into its `<caption>` element. */
const GRID_CAPTION = 'Profile properties declared for this site';

/** Wording of the two bulk toggles. See {@link ProfileDefinitionListComponent.setAllRequired}. */
const ALL_REQUIRED_LABEL = 'Required for every property';
const ALL_VISIBLE_LABEL = 'Visible for every property';

// ---------------------------------------------------------------------------
// RULES MEASURED OUT OF THE LEGACY SOURCE
// ---------------------------------------------------------------------------

/**
 * The name rule, lifted verbatim from `ProfilePropertyDefinition.vb` L228.
 *
 * Anchored at both ends exactly as the legacy declared it, and the hyphen is escaped in
 * the same position, so the accepted set is identical: letters, digits, dot, underscore,
 * percent, hyphen, plus and apostrophe. `+` after the class means at least one character,
 * which is why an empty name fails this rule as well as the required rule.
 */
const NAME_PATTERN = /^[a-zA-Z0-9._%\-+']+$/;

/**
 * The four declarations whose DELETE command the legacy screen hid.
 *
 * MIGRATION (DL-7): `grdProfileProperties_ItemDataBound` L570-L591 reached into cell index
 * 1 — the delete column — and set `delImage.Visible = False` for exactly these four names,
 * compared with `PropertyName.ToLower`. They are the properties the platform itself
 * depends on, so removing one would break the product rather than the tenant's data.
 *
 * Held as a frozen array of lower-case names and compared after lower-casing the row's
 * name, which reproduces the legacy comparison exactly. A `const enum` would be the
 * obvious shape for a closed set like this and is deliberately NOT used: this workspace
 * compiles with `isolatedModules`, under which a `const enum` cannot be erased safely.
 */
const UNDELETABLE_PROPERTY_NAMES: readonly string[] = Object.freeze([
  'lastname',
  'firstname',
  'timezone',
  'preferredlocale',
]);

/**
 * The failure codes this screen recognises, spelled as {@link failureCode} normalises them.
 *
 * ⚠ GAP: the shared conflict vocabulary in `core/utils/form-errors.util.ts` carries no
 * profile-definition code at all — its eleven members cover portals, roles, pages and
 * modules only — so the two sentences these codes select cannot be obtained from it and
 * are declared here instead. The gap is REPORTED rather than closed, because that utility
 * belongs to another agent. The three codes themselves are the ones the API really emits:
 * `profile-definition.duplicate-name`, `profile-definition.not-found` and
 * `profile-definition.validation-expression-invalid`, normalised from hyphens to
 * underscores by the shared reader.
 */
const DUPLICATE_NAME_CODE = 'profile_definition.duplicate_name';
const NOT_FOUND_CODE = 'profile_definition.not_found';

/** The three store operations this screen awaits the outcome of. */
type AwaitedWrite =
  | { readonly kind: 'create' }
  | { readonly kind: 'update'; readonly propertyDefinitionId: number }
  | { readonly kind: 'delete'; readonly propertyName: string };

/**
 * The store operation each awaited write settles as.
 *
 * Declared as a lookup rather than a switch so that the effect can compare the failure's
 * own operation against the one it is waiting for. A failure belonging to some OTHER
 * operation is not this write's — a successful create triggers a re-read, and a re-read
 * that then failed must not be announced as a failed create.
 */
const AWAITED_OPERATION = {
  create: 'createProfileDefinition',
  update: 'updateProfileDefinition',
  delete: 'deleteProfileDefinition',
} as const;

/**
 * The typed shape of the inline create-and-edit form.
 *
 * Every control is `nonNullable`, which buys two properties this screen depends on:
 * `form.value` is the whole model rather than a `Partial`, so a submission cannot be
 * assembled from members the compiler believes may be missing; and `reset(value)` returns
 * each control to a real value instead of to `null`, which matters because ONE form
 * instance serves creation and the editing of every row in turn.
 *
 * The members are in the order `SortOrder` declared them on
 * `Library/Components/Users/Profile/ProfilePropertyDefinition.vb`: name 0, data type 1,
 * category 2, length 3, default value 4, expression 5, required 6, visible 7, view order
 * 8. That is the order the legacy property editor painted, so it is the order the template
 * paints, and it is NOT alphabetical or contract order.
 *
 * There is deliberately no `visibility` control and no `moduleDefId` control. Both are
 * `Browsable(False)` on the legacy definition (L336 and L159), so the legacy editor never
 * offered either, and the replace contract accepts neither.
 */
interface ProfileDefinitionFormModel {
  propertyName: FormControl<string>;
  dataType: FormControl<number>;
  propertyCategory: FormControl<string>;
  length: FormControl<number>;
  defaultValue: FormControl<string>;
  validationExpression: FormControl<string>;
  required: FormControl<boolean>;
  visible: FormControl<boolean>;
  viewOrder: FormControl<number>;
}

/**
 * The resolved value of {@link ProfileDefinitionFormModel}.
 *
 * Declared explicitly rather than inferred from the group, because it is also the shape of
 * {@link CREATE_DEFAULTS} and of the value {@link ProfileDefinitionListComponent} hands to
 * `reset`, and a single named contract is what keeps those three from drifting apart. It
 * differs from the API's write contract in exactly one way: the two optional strings are
 * `string` here and `string | null` there, because `Null.NullString` is the empty string
 * and a text control has no way to hold `null`.
 */
interface ProfileDefinitionFormValue {
  readonly propertyName: string;
  readonly dataType: number;
  readonly propertyCategory: string;
  readonly length: number;
  readonly defaultValue: string;
  readonly validationExpression: string;
  readonly required: boolean;
  readonly visible: boolean;
  readonly viewOrder: number;
}

/** The members of a declaration this grid may change without opening the form. */
interface GridEdits {
  readonly required: boolean;
  readonly visible: boolean;
  readonly viewOrder: number;
}

// ---------------------------------------------------------------------------
// SENTINELS
// ---------------------------------------------------------------------------
//
// `Library/Components/Shared/Null.vb` L41-L45 declares `NullInteger` as -1 and
// `NullString` as the EMPTY STRING rather than as null. Both survive on the wire, because
// the API serialises with `DefaultIgnoreCondition = Never`, so an unset default value or
// expression arrives as `""` and not as an omitted member.
//
// ⚠ -1 IS NOT A UNIVERSAL "ABSENT" MARKER IN THIS SCREEN. It means three different things
// on three different members, and collapsing them would be a real defect:
//
//   propertyDefinitionId  -1 never occurs. The column is `IDENTITY(1, 1)`
//                         (04.00.04:L1109), so the first real declaration is 1 and absence
//                         is expressed as `null` in this file, tested with `=== null`.
//   dataType              -1 is the legacy field initialiser
//                         (`ProfilePropertyDefinition.vb:L47`), i.e. "no type chosen yet".
//                         The member is `Required(True)`, so -1 must be REFUSED on submit.
//   viewOrder             -1 is an INSTRUCTION, not an absence. The terminal create
//                         procedure branches on `IF @vieworder = -1` (04.06.00:L1112) and
//                         substitutes the current maximum order plus one, so -1 is the only
//                         way a caller can say "append to the end". It must NOT be refused.
//
// And 0 is REAL on every numeric member here. `ProcessPostBack` L325-L327 assigned
// `ViewOrder = i` from a zero-based index, so the first row genuinely holds 0; the legacy
// field initialiser for `ViewOrder` is 0 as well; and a `Length` of 0 means "no declared
// bound" rather than "unset". Nothing in this file tests any of them for truthiness.

/** `Null.NullInteger`. Named so that no comparison in this file spells a bare -1. */
const NULL_INTEGER = -1;

/**
 * The create-mode defaults, measured from the legacy field initialisers.
 *
 * MIGRATION: `EditProfileDefinition.ascx.vb` L104-L107 built a create form from
 * `New ProfilePropertyDefinition`, so the legacy blank form showed exactly the class's own
 * field initialisers (`ProfilePropertyDefinition.vb` L47-L60): `DataType` seeded with the
 * null-integer sentinel, `Length` and `ViewOrder` at 0, `Required` AND `Visible` both
 * FALSE, and the three strings unset. `Visible` defaulting to false is easy to get wrong by
 * assuming a new property ought to be shown; the legacy default is false and it is
 * reproduced.
 */
const CREATE_DEFAULTS: ProfileDefinitionFormValue = {
  propertyName: '',
  dataType: NULL_INTEGER,
  propertyCategory: '',
  length: 0,
  defaultValue: '',
  validationExpression: '',
  required: false,
  visible: false,
  viewOrder: 0,
};

// ---------------------------------------------------------------------------
// VALIDATORS
// ---------------------------------------------------------------------------
//
// ⚠ ALL LEGACY VALIDATION WAS IMPERATIVE — a guard clause inside a postback handler, or a
// server control the property editor added at run time. Every rule below is DECLARATIVE
// instead, attached to the control it governs, so a rule cannot be skipped by a code path
// that forgot to call it. That is a mechanism change and is annotated as one.

/**
 * Requires a whole number, accepting zero and negative values alike.
 *
 * NO LOWER BOUND, deliberately. A bound would make this client refuse input the API
 * accepts: its own validator declares no rule for either numeric member, and for the view
 * order it explains why a bound would be actively wrong — -1 is the append instruction. The
 * rule this validator DOES enforce is the column's integral type, which the legacy never
 * checked because Option Strict OFF coerced whatever was typed.
 *
 * Emptiness is not this validator's business: an empty numeric control yields `null` at run
 * time whatever the declared type says, and presence is `Validators.required`'s question.
 * Reporting both would stack two messages on one omission.
 *
 * @returns A validator reporting `wholeNumber` for a fractional or non-numeric entry.
 */
function wholeNumber(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    if (raw === null || raw === undefined || raw === '') {
      return null;
    }

    const parsed: number = typeof raw === 'number' ? raw : Number(String(raw).trim());

    return Number.isInteger(parsed) ? null : { wholeNumber: { message: WHOLE_NUMBER_MESSAGE } };
  };
}

/**
 * Refuses the null-integer sentinel on a member the legacy declared `Required(True)`.
 *
 * MIGRATION: this is how `Required(True)` on `DataType` is reproduced. The legacy paired
 * that attribute with a field seeded to the sentinel, so "required" meant "not still the
 * sentinel" — and `Validators.required` cannot express that, because -1 is a perfectly
 * present value. The comparison is EXPLICIT against the named sentinel, never a truthiness
 * test, and it deliberately does not refuse 0: zero is a legitimate key on other members of
 * this contract and nothing in the excluded lookup subsystem says it is not one here.
 *
 * @param message The sentence to report.
 * @returns A validator reporting `sentinel` when the control still holds -1.
 */
function notNullInteger(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;
    const parsed: number = typeof raw === 'number' ? raw : Number(String(raw ?? '').trim());

    return parsed === NULL_INTEGER ? { sentinel: { message } } : null;
  };
}

/**
 * Requires that a tenant-authored regular expression actually compiles.
 *
 * ⚠ THE CONSTRUCTOR CALL IS THE ONLY THROWING EXPRESSION IN THIS FILE AND IT IS CAUGHT. An
 * uncaught `SyntaxError` here would propagate out of change detection and take the whole
 * screen down while the operator was still typing, which is the failure mode this validator
 * exists to prevent. The caught error is discarded rather than surfaced: its text is a
 * JavaScript engine diagnostic, not a sentence for an operator.
 *
 * ⚠ THE EMPTY STRING IS "NO EXPRESSION", NOT "AN EXPRESSION THAT MATCHES NOTHING", and the
 * two are told apart explicitly. `Null.NullString` is the empty string, so an unset
 * expression arrives as `""`; `new RegExp('')` would compile happily and match everywhere,
 * so treating `""` as an expression would silently attach a rule the tenant never declared.
 * An expression that genuinely matches nothing — `(?!)` — is a real expression, compiles,
 * and is accepted.
 *
 * @returns A validator reporting `expression` when the value cannot be compiled.
 */
function compilableExpression(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    if (typeof raw !== 'string' || raw.length === 0) {
      return null;
    }

    try {
      new RegExp(raw);
    } catch {
      return { expression: { message: EXPRESSION_INVALID_MESSAGE } };
    }

    return null;
  };
}

// ---------------------------------------------------------------------------
// PURE HELPERS
// ---------------------------------------------------------------------------

/**
 * Whether a set of staged grid edits differs from what a declaration currently holds.
 *
 * Compared member by member with strict equality, never by serialising the two and comparing
 * the text: `false` and `0` are DATA on these members, and a comparison that went through a
 * truthiness test or a loose equality would treat a legitimate `false` as "unset" and a
 * legitimate `0` as equal to it.
 *
 * @param staged The operator's pending values.
 * @param held The declaration as the server last reported it.
 * @returns True when at least one member differs.
 */
function differs(staged: GridEdits, held: ProfilePropertyDefinition): boolean {
  return (
    staged.required !== held.required ||
    staged.visible !== held.visible ||
    staged.viewOrder !== held.viewOrder
  );
}

/**
 * Copies the nine writable members of a declaration into a replace request.
 *
 * ⚠ EVERY MEMBER MUST BE CARRIED, not just the one being changed. The endpoint REPLACES the
 * declaration rather than patching it, so a member omitted from the request is a member
 * cleared in the database. That is why moving a row one place rewrites the whole declaration:
 * there is no narrower verb to use.
 *
 * The nine are named explicitly rather than spread from the row and pruned, so that a change
 * to either contract is a compile error here instead of a rejected request at run time. The
 * three members deliberately absent are the identity and the tenant, which the server decides,
 * and the module association, which only a create may set.
 *
 * @param definition The declaration, with any staged edits already merged in.
 * @returns A complete replace request.
 */
function toUpdateRequest(
  definition: ProfilePropertyDefinition,
): UpdateProfilePropertyDefinitionRequest {
  return {
    propertyName: definition.propertyName,
    propertyCategory: definition.propertyCategory,
    dataType: definition.dataType,
    defaultValue: definition.defaultValue,
    length: definition.length,
    required: definition.required,
    validationExpression: definition.validationExpression,
    viewOrder: definition.viewOrder,
    visible: definition.visible,
  };
}

/**
 * The presence message for one control.
 *
 * Three of the four required members have a sentence; the fourth, the view order, cannot fail
 * presence at all because its control is a non-nullable number — so its arm exists only to
 * keep the switch exhaustive and returns the shared numeric sentence.
 *
 * @param control The control that failed the presence rule.
 * @returns The sentence to show.
 */
function requiredMessageFor(control: keyof ProfileDefinitionFormModel): string {
  switch (control) {
    case 'propertyName':
      return NAME_REQUIRED_MESSAGE;
    case 'propertyCategory':
      return CATEGORY_REQUIRED_MESSAGE;
    case 'dataType':
      return DATA_TYPE_REQUIRED_MESSAGE;
    default:
      return WHOLE_NUMBER_MESSAGE;
  }
}

/**
 * The over-length message for one control.
 *
 * Only three members carry a length limit, and each limit is the terminal column width the API
 * also enforces. The default arm is unreachable in practice and returns the name's sentence
 * rather than an empty string, so a future control that gains a limit without gaining a
 * sentence still says something actionable.
 *
 * @param control The control that exceeded its limit.
 * @returns The sentence to show.
 */
function tooLongMessageFor(control: keyof ProfileDefinitionFormModel): string {
  switch (control) {
    case 'propertyCategory':
      return CATEGORY_TOO_LONG_MESSAGE;
    case 'validationExpression':
      return EXPRESSION_TOO_LONG_MESSAGE;
    default:
      return NAME_TOO_LONG_MESSAGE;
  }
}

/**
 * Wording for a settled, successful write.
 *
 * MIGRATION: THE LEGACY ANNOUNCED NOTHING ON SUCCESS — neither `cmdUpdate_Click` L444-L452 nor
 * `DeleteProperty` L157-L164 nor the wizard's save at L438-L467 raised a module message unless
 * something went wrong. A confirmation is added here for a SINGLE write only, because an inline
 * form that simply closes gives the operator no evidence that anything was written. The BATCH
 * deliberately announces nothing, for the reason given on {@link
 * ProfileDefinitionListComponent.applyChanges}.
 *
 * @param pending What was requested.
 * @returns The confirmation to announce. Never empty.
 */
function successMessage(pending: AwaitedWrite): string {
  switch (pending.kind) {
    case 'create':
      return 'The profile property was created.';
    case 'update':
      return 'The profile property was updated.';
    default:
      return `Profile property "${pending.propertyName}" was deleted.`;
  }
}

/**
 * Wording for a refused write.
 *
 * Three sources, in order of specificity.
 *
 * ⚠ THE DUPLICATE-NAME SENTENCE IS THE LEGACY'S OWN, reproduced byte for byte from
 * `DuplicateName.Text`, and it is selected by the server's CODE rather than by the bare status.
 * Recognising it by `409` alone would be the weaker test, because a removal draws the same
 * status for a completely different reason — a declaration that is still in use — and only the
 * ordering of the branches would keep the two apart.
 *
 * ⚠ THE SEVERITY IS THE STORE'S, NOT THIS FILE'S. The shared summariser answers `warning` for a
 * refusal of permission and `error` for the rest, which is the legacy's own reading: the
 * access-denied control raised its message as a yellow warning on both branches, while the
 * duplicate-name refusal was raised as `ModuleMessageType.RedError`. Re-deciding it here would
 * let the two disagree.
 *
 * @param pending What was requested.
 * @param failure The failure the store recorded.
 * @returns The message to announce. Never empty.
 */
function refusalMessage(pending: AwaitedWrite, failure: UserFailure): string {
  if (failure.code === DUPLICATE_NAME_CODE) {
    return DUPLICATE_NAME_MESSAGE;
  }

  // A declaration that has already been removed by someone else. Recognised by the code the API
  // publishes AND by the bare status, because a not-found that never reached an action carries no
  // application code at all — the framework writes a specification URI for it, which the shared
  // reader correctly refuses to treat as a failure code.
  if (failure.code === NOT_FOUND_CODE || failure.problem?.status === 404) {
    return DEFINITION_GONE_MESSAGE;
  }

  // A conflict on a REMOVAL is the declaration being in use, which is a different refusal from
  // a duplicate name and is worded as its own thing rather than left to the generic sentence.
  if (pending.kind === 'delete' && failure.problem?.status === 409) {
    return DEFINITION_IN_USE_MESSAGE;
  }

  return stripLegacyBreakTags(failure.summary.message);
}

/**
 * The profile-property catalogue for the tenant: `/settings/profile-definitions`.
 *
 * MIGRATION: THIS ONE SCREEN REPLACES TWO LEGACY PAGES AND A THREE-STEP WIZARD.
 * `Website/admin/Users/ProfileDefinitions.ascx` rendered the grid with four per-row
 * commands, and `Website/admin/Users/EditProfileDefinition.ascx` was a separate page the
 * edit command NAVIGATED to — `Page_Init` L380-L386 built an `EditUrl(...)` and set the
 * command column's `EditMode="URL"`. The target route table declares no
 * `/settings/profile-definitions/:id` address, so the editor is INLINE here: pressing Edit
 * opens the form pre-populated and the operator never loses their place in the list.
 *
 * MIGRATION: the wizard's second and third steps are DROPPED, not ported. Step two managed
 * list entries for a `List`-typed property, which needs the excluded `Library/Components/Lists`
 * subsystem; step three managed localised text for the property's name, help and category
 * (`BindLanguages` L171-L173, `cmdSaveKeys_Click` L312), and localisation is out of scope for
 * the migration as a whole. Only step one — the property's own details — has a target.
 *
 * ## WHY THIS COMPONENT IS ROUTED AND SELF-SUFFICIENT
 *
 * `app.routes.ts` loads THIS class directly as a standalone `loadComponent` leaf. A
 * presentational shape taking the catalogue as an input therefore cannot work at this
 * address: nothing would supply the input and nothing would listen to the outputs, so the
 * grid would render permanently empty and every affordance on it would be a no-op that
 * looked like a working button. The screen consequently owns its own arrival: it injects the
 * account store, loads on entry, and turns each affordance into a store command.
 *
 * ## STATE
 *
 * Signals only. The server's catalogue lives in the store — one copy for the whole
 * application — and this screen holds exactly one thing of its own: a DRAFT of the grid
 * edits the operator has made but not yet applied. {@link rows} merges the two, so the grid
 * always shows either the server's truth or the operator's pending change and never a third
 * thing.
 *
 * MIGRATION: view state is GONE, and it was load-bearing in both legacy pages.
 * `EditProfileDefinition.ascx.vb` L117-L128 kept the identifier being edited in
 * `ViewState("PropertyDefinitionID")` and parsed it back out of a string on every read;
 * `ProfileDefinitions.ascx.vb` relied on the fetched collection being a shared cached
 * instance so that its in-place mutations survived to the next postback. Neither
 * round-trip exists here.
 *
 * ⚠ LEGACY DEFECT REPORTED, NOT REPRODUCED: `m_objProperties` (L119) is assigned `Nothing`
 * by `RefreshGrid` (L279) and NEVER READ by `GetProperties` (L132-L134), which goes straight
 * to the controller. The field is dead, so "refresh" only worked because the controller's
 * cache was dropped elsewhere. The draft signal here is the explicit replacement.
 *
 * ⚠ LEGACY DEFECT REPORTED, NOT REPRODUCED: `ProcessPostBack` L330-L332 catches an
 * exception and rethrows it with `Throw ex`, which RESETS the stack trace to that line and
 * destroys the origin of the fault. Nothing in this file rethrows.
 *
 * ## THE FIVE WRITES, AND WHAT IS DELIBERATELY ABSENT
 *
 * `POST` creates, `PUT` replaces, `DELETE` removes. There is NO reorder endpoint and NO
 * bulk endpoint, and neither is invented: position is a FIELD, so moving a row is a `PUT`,
 * and a bulk flag change is one `PUT` per affected row. That is not a downgrade from the
 * legacy — `UpdateProperties` L291-L298 already issued one update call per dirty row.
 */
@Component({
  selector: 'app-profile-definition-list',
  standalone: true,
  imports: [
    // Backs the inline create-and-edit form.
    ReactiveFormsModule,
    // Page heading plus the projected action bar.
    PageHeaderComponent,
    // The twelve-column grid. It renders its own spinner and empty state, so neither the
    // shared spinner nor the shared empty state is declared here.
    DataTableComponent,
    // Every control of the inline form, and the two bulk toggles.
    FormFieldComponent,
    // The removal confirmation. Its presence in the DOM is what "open" means.
    ConfirmDialogComponent,
    // Surfaces a refused or failed request, as `role="alert"`.
    ErrorBannerComponent,
    // Renders the two boolean columns as announced text in the read-only cells.
    YesNoPipe,
  ],
  templateUrl: './profile-definition-list.component.html',
  styleUrl: './profile-definition-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfileDefinitionListComponent implements OnInit {
  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------
  //
  // `inject()` rather than constructor parameters, matching every sibling screen, and
  // nothing is provided here: the migration plan wires `provideHttpClient`, the router and
  // the interceptor chain once in `app.config.ts`, and a component-level provider would
  // give this screen a private copy of whatever it declared.
  //
  // ⚠ THE STORE IS THE ONLY TRANSPORT. `user.service.ts` is not injected as well, even
  // though it is what the store ultimately calls: injecting both would put a second copy of
  // the catalogue in the screen, and the whole point of reading through the store is that
  // there is exactly one. No `HttpClient` is injected anywhere in this file.

  private readonly store = inject(UserStore);

  private readonly notifications = inject(NotificationService);

  // Held solely so `afterNextRender` can be reached from outside a constructor. See
  // `restoreFocusAfterRemoval`, which is the only consumer and explains why the deferral is
  // unavoidable there.
  private readonly injector = inject(Injector);

  // -------------------------------------------------------------------------
  // CELL TEMPLATES
  // -------------------------------------------------------------------------
  //
  // Static queries, so they are resolved before `ngOnInit` and the column set can be
  // assembled there. Each `ng-template` belongs to THIS component's view even though
  // `app-data-table` is what renders it, which is what makes a projected cell template work
  // at all.

  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('moveDownCommand', { static: true })
  private moveDownCommandTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('moveUpCommand', { static: true })
  private moveUpCommandTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('dataTypeCell', { static: true })
  private dataTypeCellTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('defaultValueCell', { static: true })
  private defaultValueCellTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('expressionCell', { static: true })
  private expressionCellTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('requiredCell', { static: true })
  private requiredCellTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  @ViewChild('visibleCell', { static: true })
  private visibleCellTemplate?: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>>;

  /**
   * The screen's primary action button, held as the focus anchor for a confirmed delete.
   *
   * ⚠ WHY A VIEW QUERY ON A BUTTON THAT IS NEVER READ FOR ITS VALUE. Deleting a row removes the
   * command button that opened the confirmation dialog, so the dialog's own focus-return has
   * nothing to return to and focus collapses to `body` — measured in a browser, not surmised.
   * This button is outside the grid and is never removed, so it is a valid anchor. The query is
   * STATIC because the button sits outside every control-flow block, so it is resolved before
   * the first change detection and is available whenever a delete settles.
   *
   * It is optional for the same reason every template query here is: a view query cannot be
   * proven non-null by the compiler, and the alternative is a non-null assertion, which is
   * barred. A missing anchor degrades to today's behaviour rather than throwing.
   */
  @ViewChild('createTrigger', { static: true })
  private createTrigger?: ElementRef<HTMLButtonElement>;

  // -------------------------------------------------------------------------
  // STATE OWNED BY THIS SCREEN
  // -------------------------------------------------------------------------

  /** Backing store of {@link columns}; populated once, in `ngOnInit`. */
  private readonly columnSet = signal<readonly DataTableColumn<ProfilePropertyDefinition>[]>([]);

  /**
   * The grid edits made but not yet applied, keyed by declaration identifier.
   *
   * MIGRATION: this is the target's `IsDirty`. The legacy tracked dirtiness ON THE ENTITY —
   * every setter on `ProfilePropertyDefinition` compared before assigning and raised a
   * private flag (L96, L114, L146, L198, L233, L269, L287, L305, L323) — and
   * `UpdateProperties` then wrote only the rows whose flag was up. The entity here is
   * `readonly` and comes from a shared store, so dirtiness cannot live on it; it lives
   * beside it, in a map this screen owns.
   *
   * A `ReadonlyMap` replaced wholesale on every change rather than a mutated `Map`, so that
   * the signal genuinely notifies. Mutating a held map in place would leave every reader
   * looking at the same reference and nothing would recompute.
   */
  private readonly draft = signal<ReadonlyMap<number, GridEdits>>(new Map<number, GridEdits>());

  /** Whether the inline form is on screen. */
  private readonly formOpen = signal(false);

  /**
   * The declaration being replaced, or `null` when the form is creating.
   *
   * ⚠ PRESENCE, NEVER FALSINESS. Create and edit are told apart by `=== null`, and never by
   * `if (id)`, `id > 0`, `!id` or `id ?? -1`. That is the legacy discriminator translated
   * faithfully: `EditProfileDefinition.ascx.vb` L250-L258 compared EXPLICITLY against
   * `Null.NullInteger`, and L296, L449 and L453 do the same. A truthiness test would be a
   * real defect on a key column, and although `ProfilePropertyDefinition.PropertyDefinitionID`
   * happens to be `IDENTITY(1, 1)` so zero never occurs, three sibling tables in this schema
   * are seeded `IDENTITY(0, 1)` and the tenant table at -1. The habit, not the luck, is what
   * keeps this correct.
   */
  private readonly editing = signal<number | null>(null);

  /** The declaration awaiting confirmation of removal, or `null` when none is. */
  private readonly pendingRemoval = signal<ProfilePropertyDefinition | null>(null);

  /**
   * The single write whose outcome is being awaited, or `null`.
   *
   * Set for a create, a replace from the form, and a removal — the three writes that have an
   * outcome worth reporting. Deliberately NOT set by {@link applyChanges}: see the note
   * there.
   */
  private readonly awaited = signal<AwaitedWrite | null>(null);

  /**
   * Whether a submission has been attempted since the form was opened.
   *
   * MIGRATION: LEGACY DEFECT CORRECTED. `Wizard_NextButtonClick` L442 guarded the save with
   * `If Properties.IsDirty And Properties.IsValid Then` and had NO `Else`, so an invalid form
   * silently saved nothing AND THE WIZARD STILL ADVANCED — the operator was moved to the next
   * step with their changes discarded and no message anywhere. This flag is what replaces
   * that silence: messages stay hidden until the operator submits, and then every failing
   * rule is named. The Minimal Change Clause protects business logic, not a defect that lost
   * an operator's work.
   */
  private readonly submitAttempted = signal(false);

  // -------------------------------------------------------------------------
  // STORE-DERIVED SURFACE
  // -------------------------------------------------------------------------
  //
  // These are the store's own signals under template-facing names. They are NOT copies:
  // assigning the signal shares it, so nothing here can drift from the store.

  /** Whether the catalogue is still being fetched. */
  protected readonly loading: Signal<boolean> = this.store.profileDefinitionsLoading;

  /** Whether a write is in flight. Every affordance that would start another is disabled. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * The failure document to surface, or `null`.
   *
   * Derived rather than stored, so this screen cannot go on showing a message the store has
   * already cleared. Only the DOCUMENT is bound: the shared banner renders its title, its
   * detail and its per-field messages, and binding the store's summary as well would report
   * the same failure twice.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed<ProblemDetails | null>(
    () => this.store.failure()?.problem ?? null,
  );

  // -------------------------------------------------------------------------
  // THE GRID
  // -------------------------------------------------------------------------

  /** The twelve columns, assembled in `ngOnInit` once the cell templates exist. */
  protected readonly columns = this.columnSet.asReadonly();

  /**
   * The rows to render: the server's catalogue with the operator's pending edits applied,
   * ordered by position.
   *
   * MIGRATION: THE LOCAL RE-SORT IS REQUIRED FOR PARITY, not a decoration. `MoveProperty`
   * L176-L193 swapped two positions and then called `profileProperties.Sort()` followed by
   * `BindGrid()` WITHOUT saving, so the row visibly moved immediately and the write happened
   * later, on Apply. Leaving the order to the server would make Move Up appear to do nothing
   * until the batch was committed. The comparer is the legacy one:
   * `ProfilePropertyDefinitionCollection.vb` L299-L306 sorts by `ViewOrder`, and the API
   * orders by the same column, so the two agree.
   *
   * The identifier breaks a tie. It has to: the position column carries no uniqueness
   * constraint, so two declarations may legitimately hold the same position, and without a
   * deterministic tie-break the grid would reshuffle equal rows on every recomputation.
   */
  protected readonly rows = computed<readonly ProfilePropertyDefinition[]>(() => {
    const held: readonly ProfilePropertyDefinition[] = this.store.profileDefinitions();
    const edits: ReadonlyMap<number, GridEdits> = this.draft();

    return held
      .map((definition) => this.withDraft(definition, edits))
      .sort(
        (left, right) =>
          left.viewOrder - right.viewOrder ||
          left.propertyDefinitionId - right.propertyDefinitionId,
      );
  });

  /**
   * How many declarations carry an edit that has not been applied.
   *
   * ⚠ DERIVED FROM THE DIFFERENCE, NOT FROM THE SIZE OF THE STAGED MAP. A staged entry that the
   * server has since caught up with counts for nothing, which is what lets a partially refused
   * batch report exactly the work still outstanding. Counting the map instead would keep reporting
   * every row the operator ever touched, including the ones already written.
   */
  protected readonly dirtyCount = computed<number>(() => this.pendingRows().length);

  /** Whether "Apply Changes" has anything to do. */
  protected readonly hasPendingChanges = computed<boolean>(() => this.pendingRows().length > 0);

  /**
   * Whether every declaration is already required, and likewise for visible.
   *
   * MIGRATION (DL-6): `BindGrid` L236-L264 computed exactly this — seeded true, cleared by
   * the first row that was false — and wrote it onto the two check-box COLUMN HEADERS, whose
   * click then wrote the value to every row (`ItemCheckedChanged` L474-L483, branching on
   * `e.IsAll`).
   *
   * ⚠ THE SELECT-ALL AFFORDANCE HAD TO MOVE OUT OF THE TABLE, and the gap is reported rather
   * than papered over. `app-data-table` renders its headings from the column descriptors and
   * exposes no header-cell template; its inputs are closed at five. Putting a control in a
   * heading would mean adding a sixth input to a component nine other screens share, which
   * this screen may not do on its own. The two toggles therefore sit in their own region
   * above the grid, where they reach the same rows and read the same all-true state.
   *
   * The empty catalogue is the one place this deliberately diverges: seeded true, the legacy
   * expression reported "all required" for a list with no rows at all, and the toggles are
   * simply not rendered when there is nothing to apply them to.
   */
  protected readonly allRequired = computed<boolean>(() =>
    this.rows().every((definition) => definition.required),
  );

  protected readonly allVisible = computed<boolean>(() =>
    this.rows().every((definition) => definition.visible),
  );

  /** Whether the catalogue holds anything, which is what gates the bulk region. */
  protected readonly hasRows = computed<boolean>(() => this.rows().length > 0);

  // -------------------------------------------------------------------------
  // THE INLINE FORM
  // -------------------------------------------------------------------------

  /**
   * The create-and-edit form, in the order `SortOrder` declared the members.
   *
   * Every control is `nonNullable`, which is what makes ONE group safe to reuse across
   * creation and the editing of every row: `reset(value)` returns each control to a real
   * value rather than to `null`, and `getRawValue()` is fully typed rather than a `Partial`.
   *
   * MIGRATION: the rules are the ones the legacy declared as ATTRIBUTES on
   * `ProfilePropertyDefinition.vb`, which the excluded property editor turned into server
   * validators at run time. Four members carry `Required(True)` — name L228, data type L91,
   * category L193 and view order L300 — and the name additionally carries the pattern at
   * L228. The view order needs no `Validators.required`: its control is a non-nullable
   * number, so presence is structural, and the API's own validator declines the rule for
   * exactly that reason.
   *
   * ⚠ `Validators.required` ACCEPTS ZERO, and this screen depends on that. Its emptiness
   * test is `value == null || ((string | array) && length === 0)`, so a numeric 0 passes.
   * Nothing here hand-rolls required-ness as a truthiness test, which would have refused the
   * first row's legitimate position of 0.
   */
  protected readonly form = new FormGroup<ProfileDefinitionFormModel>({
    propertyName: new FormControl(CREATE_DEFAULTS.propertyName, {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(NAME_MAX_LENGTH),
        Validators.pattern(NAME_PATTERN),
      ],
    }),
    dataType: new FormControl(CREATE_DEFAULTS.dataType, {
      nonNullable: true,
      validators: [
        Validators.required,
        notNullInteger(DATA_TYPE_REQUIRED_MESSAGE),
        wholeNumber(),
      ],
    }),
    propertyCategory: new FormControl(CREATE_DEFAULTS.propertyCategory, {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(CATEGORY_MAX_LENGTH)],
    }),
    length: new FormControl(CREATE_DEFAULTS.length, {
      nonNullable: true,
      validators: [wholeNumber()],
    }),
    defaultValue: new FormControl(CREATE_DEFAULTS.defaultValue, { nonNullable: true }),
    validationExpression: new FormControl(CREATE_DEFAULTS.validationExpression, {
      nonNullable: true,
      validators: [Validators.maxLength(EXPRESSION_MAX_LENGTH), compilableExpression()],
    }),
    required: new FormControl(CREATE_DEFAULTS.required, { nonNullable: true }),
    visible: new FormControl(CREATE_DEFAULTS.visible, { nonNullable: true }),
    viewOrder: new FormControl(CREATE_DEFAULTS.viewOrder, {
      nonNullable: true,
      validators: [wholeNumber()],
    }),
  });

  /** Whether the inline form is on screen. */
  protected readonly editorOpen = this.formOpen.asReadonly();

  /** Whether the open form is replacing an existing declaration rather than creating one. */
  protected readonly isEditMode = computed<boolean>(() => this.editing() !== null);

  /** `Introduction_Add.Title` while creating, `Introduction.Title` while editing. */
  protected readonly formHeading = computed<string>(() =>
    this.editing() === null ? CREATE_HEADING : EDIT_HEADING,
  );

  /** `cmdCreate.Text` while creating, `cmdUpdate.Text` while editing. */
  protected readonly submitLabel = computed<string>(() =>
    this.editing() === null ? CREATE_SUBMIT_LABEL : EDIT_SUBMIT_LABEL,
  );

  /** Whether the messages beneath the controls are shown yet. */
  protected readonly showMessages = this.submitAttempted.asReadonly();

  // -------------------------------------------------------------------------
  // REMOVAL
  // -------------------------------------------------------------------------

  /** The declaration awaiting confirmation, or `null`. Its presence opens the dialog. */
  protected readonly removalTarget = this.pendingRemoval.asReadonly();

  // -------------------------------------------------------------------------
  // WORDING EXPOSED TO THE TEMPLATE
  // -------------------------------------------------------------------------
  //
  // Bound rather than written inline so that a specification can assert the rendered text
  // against the same constant the template renders, instead of restating it and letting the
  // two drift apart.

  protected readonly pageTitle = PAGE_TITLE;
  protected readonly helpText = HELP_TEXT;
  protected readonly addLabel = ADD_LABEL;
  protected readonly applyLabel = APPLY_LABEL;
  protected readonly refreshLabel = REFRESH_LABEL;
  protected readonly cancelLabel = CANCEL_LABEL;
  protected readonly editLabel = EDIT_LABEL;
  protected readonly deleteLabel = DELETE_LABEL;
  protected readonly moveUpLabel = MOVE_UP_LABEL;
  protected readonly moveDownLabel = MOVE_DOWN_LABEL;
  protected readonly gridCaption = GRID_CAPTION;
  protected readonly allRequiredLabel = ALL_REQUIRED_LABEL;
  protected readonly allVisibleLabel = ALL_VISIBLE_LABEL;
  protected readonly removalTitle = DELETE_CONFIRM_TITLE;
  protected readonly removalMessage = DELETE_CONFIRM_MESSAGE;
  protected readonly fieldText = FIELD_TEXT;

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  constructor() {
    // An effect, because reporting an outcome and closing a form are genuine side effects
    // and that is the only kind of work an effect is for. Created in the injection context,
    // so it is destroyed with the component and there is nothing to unsubscribe.
    //
    // ⚠ IT MUST WAIT FOR THE WRITE TO SETTLE. Every store command sets `saving` before it
    // dispatches and clears it in both callbacks, so "awaiting something and not saving"
    // is the only moment at which the outcome is known. Reporting earlier would announce
    // the result of the previous request.
    effect(() => {
      const pending: AwaitedWrite | null = this.awaited();
      const inFlight: boolean = this.store.saving();
      const failure: UserFailure | null = this.store.failure();

      if (pending === null || inFlight) {
        return;
      }

      // `untracked`, because the report writes signals this effect reads. Without it the
      // clearing of `awaited` would schedule the effect again.
      untracked(() => {
        this.awaited.set(null);
        this.report(pending, failure);
      });
    });
  }

  /**
   * Assembles the columns and reads the catalogue.
   *
   * The cell templates are captured by STATIC view queries, so they are resolved by the time
   * this runs and the column set can be built here. Data is loaded from a lifecycle hook
   * rather than from an effect: an effect that fetched would fire again on every unrelated
   * signal it happened to read.
   *
   * MIGRATION: this is `Page_Load` L407-L418 minus its postback branch. The legacy split its
   * behaviour on `Page.IsPostBack` — bind on the first render, `ProcessPostBack` on every
   * subsequent one — because the grid's edits arrived back as form fields that had to be
   * read out of the control tree. There are no postbacks here, so the branch has no target
   * and the `SupportsRichClient` dual code path (L144-L146, L539-L557) collapses to one.
   *
   * ⚠ NO TENANT ARGUMENT IS PASSED, and that is not an omission. The API resolves the tenant
   * from the request itself, so `GET /api/v1/profile-definitions` carries no query parameter
   * at all. The legacy `UsersPortalId` (L106-L114) substituted the null-integer sentinel when
   * the page sat under the host tab, which selected the SUPER-USER property set;
   * host administration is out of scope for this migration, so that branch is dropped rather
   * than translated.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
    this.store.loadProfileDefinitions();
  }

  // -------------------------------------------------------------------------
  // GRID READ HELPERS
  // -------------------------------------------------------------------------

  /**
   * Whether a declaration may be removed.
   *
   * MIGRATION (DL-7): `grdProfileProperties_ItemDataBound` L570-L591 reached into the delete
   * column and set `delImage.Visible = False` for four names, matched on
   * `PropertyName.ToLower`. The comparison here is the same one, and the command is HIDDEN
   * rather than disabled — a disabled control still announces itself as an action that
   * cannot be taken, whereas the legacy offered no action at all.
   *
   * @param definition The row being rendered.
   * @returns False for the four platform properties, true for everything else.
   */
  protected canDelete(definition: ProfilePropertyDefinition): boolean {
    return !UNDELETABLE_PROPERTY_NAMES.includes(definition.propertyName.toLowerCase());
  }

  /**
   * What to render in the data-type cell.
   *
   * ⚠ GAP REPORTED (DL-8). `DisplayDataType` L339-L351 resolved the integer through
   * `ListController.GetListEntryInfo(...).Value` and started from `Null.NullString`, so an
   * unresolved type legitimately rendered EMPTY. `Library/Components/Lists` is excluded from
   * this migration and the contract carries only the integer — the API's own validator says
   * as much, declining to check the member because "that subsystem is excluded ... so there
   * is no set to check membership of" — so there is NO in-scope source for the display name.
   * The integer is rendered, and the null-integer sentinel renders empty exactly as the
   * legacy did for a type it could not resolve.
   *
   * MIGRATION: the legacy template cast `CType(Container.DataItem, ProfilePropertyDefinition)`
   * under Option Strict OFF — a late-bound cast of an untyped data item. This is that cast
   * made explicit: the row arrives typed, so there is nothing to coerce.
   *
   * @param definition The row being rendered.
   * @returns The type key as text, or the empty string for the sentinel.
   */
  protected dataTypeLabel(definition: ProfilePropertyDefinition): string {
    return definition.dataType === NULL_INTEGER ? '' : String(definition.dataType);
  }

  /**
   * The text of a nullable string cell.
   *
   * ⚠ NEVER RENDERS "null" OR "undefined". `Null.NullString` is the empty string, so an unset
   * default value or expression arrives as `""` rather than as `null` — but the contract
   * admits `null` as well, and interpolating it directly would paint the word.
   *
   * @param value The member as the API reported it.
   * @returns The value, or the empty string when there is none.
   */
  protected cellText(value: string | null): string {
    return value ?? '';
  }

  // -------------------------------------------------------------------------
  // GRID EDITS — STAGED LOCALLY, WRITTEN ON APPLY
  // -------------------------------------------------------------------------

  /**
   * Sets one declaration's required flag.
   *
   * @param definition The row, as merged for display.
   * @param value The flag's new value.
   */
  protected toggleRequired(definition: ProfilePropertyDefinition, value: boolean): void {
    this.stage(definition.propertyDefinitionId, {
      required: value,
      visible: definition.visible,
      viewOrder: definition.viewOrder,
    });
  }

  /**
   * Sets one declaration's visible flag.
   *
   * @param definition The row, as merged for display.
   * @param value The flag's new value.
   */
  protected toggleVisible(definition: ProfilePropertyDefinition, value: boolean): void {
    this.stage(definition.propertyDefinitionId, {
      required: definition.required,
      visible: value,
      viewOrder: definition.viewOrder,
    });
  }

  /**
   * Sets the required flag across every declaration that does not already hold the value.
   *
   * MIGRATION (DL-6): `ItemCheckedChanged` L474-L483 branched on `e.IsAll` and wrote the
   * value to every property in the collection, deferring the writes to Apply exactly as this
   * does. Rows that already hold the value are skipped, which is not merely an optimisation:
   * staging them would enable Apply and then spend a request to change nothing.
   *
   * @param value The value to set everywhere.
   */
  protected setAllRequired(value: boolean): void {
    for (const definition of this.rows()) {
      if (definition.required !== value) {
        this.toggleRequired(definition, value);
      }
    }
  }

  /**
   * Sets the visible flag across every declaration that does not already hold the value.
   *
   * @param value The value to set everywhere.
   */
  protected setAllVisible(value: boolean): void {
    for (const definition of this.rows()) {
      if (definition.visible !== value) {
        this.toggleVisible(definition, value);
      }
    }
  }

  /**
   * Moves a declaration one place earlier in the display order.
   *
   * @param definition The row to move.
   */
  protected moveUp(definition: ProfilePropertyDefinition): void {
    this.move(definition, -1);
  }

  /**
   * Moves a declaration one place later in the display order.
   *
   * @param definition The row to move.
   */
  protected moveDown(definition: ProfilePropertyDefinition): void {
    this.move(definition, 1);
  }

  /**
   * Applies every staged edit, as one write per changed declaration.
   *
   * MIGRATION: THIS IS A TRANSPORT CHANGE ONLY, not a behavioural one. `UpdateProperties`
   * L291-L298 walked the collection and called `UpdatePropertyDefinition` for each row whose
   * dirty flag was up — one call per dirty row, exactly as here. There is no bulk endpoint and
   * none is invented.
   *
   * ⚠ THE WRITES ARE INDEPENDENT AND THE BATCH IS NOT ATOMIC. They address different
   * declarations, so the server applies each on its own merits, and a refusal of one leaves
   * the others applied. That is reported honestly rather than hidden: no "everything was
   * saved" message is announced — the legacy announced nothing here either — the refusal
   * reaches the shared banner, and the pending set is DERIVED from the difference between the
   * staged values and the server's, so the rows that landed drop out of it while the rows
   * that did not stay in it and can simply be applied again.
   *
   * The staged edits are deliberately NOT cleared here. Clearing them would make the grid
   * snap back to the server's old values for as long as the writes were in flight, and the
   * derivation above means a stale entry that now matches the server counts for nothing.
   */
  protected applyChanges(): void {
    for (const definition of this.pendingRows()) {
      this.store.updateProfileDefinition(
        definition.propertyDefinitionId,
        toUpdateRequest(definition),
      );
    }
  }

  /**
   * Discards every staged edit and re-reads the catalogue.
   *
   * MIGRATION: `cmdRefresh_Click` L430-L432 called `RefreshGrid()` and nothing else, and
   * `RefreshGrid` L278-L281 dropped the held collection before re-binding. Refresh was
   * therefore the legacy's REVERT affordance — it threw away every uncommitted grid edit —
   * and that is preserved. The wording is the legacy's own: `cmdRefresh.Text` is
   * "Refresh Grid", not "Refresh".
   */
  protected refresh(): void {
    this.draft.set(new Map<number, GridEdits>());
    this.store.loadProfileDefinitions();
  }

  // -------------------------------------------------------------------------
  // THE INLINE FORM
  // -------------------------------------------------------------------------

  /**
   * Opens the form to declare a new property.
   *
   * MIGRATION: the legacy equivalent was the module action `AddContent.Action`
   * ("Add New Profile Property", L599), which navigated to `EditProfileProperty` — a whole
   * page away. The form opens in place instead.
   *
   * The controls are reset to the legacy field initialisers rather than merely emptied, which
   * is why {@link CREATE_DEFAULTS} is measured rather than chosen.
   */
  protected openCreate(): void {
    this.editing.set(null);
    this.submitAttempted.set(false);
    this.form.reset(CREATE_DEFAULTS);
    this.formOpen.set(true);
    this.store.clearFailure();
  }

  /**
   * Opens the form to replace an existing declaration.
   *
   * MIGRATION: `Page_Init` L380-L386 built `EditUrl("PropertyDefinitionID", "KEYFIELD",
   * "EditProfileProperty")` and set the command column's `EditMode="URL"`, so Edit was a
   * NAVIGATION to a second page carrying the identifier in the query string. The target route
   * table declares no such address, so Edit fills this form instead. The identifier is held in
   * a signal rather than in view state, and it never reaches a URL.
   *
   * The row passed in is the MERGED row, so a position the operator has already changed in the
   * grid is what the form shows — the grid and the form cannot disagree about the same
   * declaration.
   *
   * @param definition The declaration to edit.
   */
  protected openEdit(definition: ProfilePropertyDefinition): void {
    this.editing.set(definition.propertyDefinitionId);
    this.submitAttempted.set(false);
    this.form.reset({
      propertyName: definition.propertyName,
      dataType: definition.dataType,
      propertyCategory: definition.propertyCategory,
      length: definition.length,
      defaultValue: definition.defaultValue ?? '',
      validationExpression: definition.validationExpression ?? '',
      required: definition.required,
      visible: definition.visible,
      viewOrder: definition.viewOrder,
    });
    this.formOpen.set(true);
    this.store.clearFailure();
  }

  /**
   * Closes the form without writing anything.
   *
   * MIGRATION: it does NOT navigate, even though the label says so. Both
   * `Wizard_CancelButtonClick` L399-L406 and `Wizard_FinishButtonClick` L418-L425 did nothing
   * but `Response.Redirect` back to the listing, which is precisely what closing an inline form
   * on the listing achieves. The wording is the legacy's own — `cmdCancel.Text` is "Return to
   * Profile Properties List" — and it stays accurate: the reader is returned to the list.
   */
  protected cancelForm(): void {
    this.formOpen.set(false);
    this.editing.set(null);
    this.submitAttempted.set(false);
    this.form.reset(CREATE_DEFAULTS);
  }

  /**
   * Writes the form.
   *
   * MIGRATION: `Wizard_NextButtonClick` L438-L467 saved on "Next" from step zero, guarded by
   * `If Properties.IsDirty And Properties.IsValid Then`, and chose between add and update by
   * comparing the identifier against `Null.NullInteger`. Three things change deliberately:
   *
   *   * The dirty half of that guard is dropped. Submitting an unchanged form here issues a
   *     replace that writes the same values, which is harmless; the legacy's dirty flag existed
   *     to spare a round trip in a page that round-tripped for everything anyway.
   *   * An INVALID form now reports why. The legacy silently did nothing and advanced the
   *     wizard regardless, losing the operator's work with no message — see
   *     {@link submitAttempted}.
   *   * ⚠ A FAILED CREATE DOES NOT SET THE EDIT IDENTIFIER. The legacy assigned
   *     `AddPropertyDefinition`'s return value INTO `PropertyDefinitionID` at L451 and only
   *     then tested it at L453, so after a duplicate-name refusal the view state held a
   *     nonsense identifier and the NEXT save took the update branch with it. Here
   *     {@link editing} is written only by {@link openEdit}, so a refused create leaves the
   *     form in create mode and a second attempt is still a create.
   *
   * ⚠ `getRawValue()`, NOT `value`. A disabled control is EXCLUDED from `FormGroup.value`, so
   * reading `value` would silently drop any member this screen ever chooses to disable. The
   * read-only members are currently expressed with the `readonly` ATTRIBUTE, which keeps them
   * in `value` — but reading the raw value means that remains true if that ever changes.
   */
  protected submitForm(): void {
    this.submitAttempted.set(true);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const value: ProfileDefinitionFormValue = this.form.getRawValue();
    const editingId: number | null = this.editing();

    // The two keyed strings are trimmed and the two free-text members are not. The API refuses
    // a blank name or category outright — its `NotEmpty` rule treats a whitespace-only string
    // as empty — and the name pattern forbids an internal space anyway, so trimming only
    // removes what would be refused. A default value or an expression, by contrast, may
    // legitimately begin or end with a space, so neither is touched.
    const members: UpdateProfilePropertyDefinitionRequest = {
      propertyName: value.propertyName.trim(),
      propertyCategory: value.propertyCategory.trim(),
      dataType: value.dataType,
      // ⚠ THE EMPTY STRING IS SENT, NOT `null`. `Null.NullString` IS the empty string, and the
      // API serialises with `DefaultIgnoreCondition = Never`, so the sentinel survives the
      // round trip exactly as the legacy stored it. Substituting `null` would change the
      // stored value for every property the operator merely re-saved.
      defaultValue: value.defaultValue,
      length: value.length,
      required: value.required,
      validationExpression: value.validationExpression,
      viewOrder: value.viewOrder,
      visible: value.visible,
    };

    if (editingId === null) {
      this.awaited.set({ kind: 'create' });
      // `moduleDefId` is `null` because only a create may set it, the legacy editor never
      // offered it — the member is `Browsable(False)` at `ProfilePropertyDefinition.vb:L159` —
      // and there is nothing on this screen to fill it in from.
      const request: CreateProfilePropertyDefinitionRequest = { ...members, moduleDefId: null };
      this.store.createProfileDefinition(request);
      return;
    }

    this.awaited.set({ kind: 'update', propertyDefinitionId: editingId });
    this.store.updateProfileDefinition(editingId, members);
  }

  /**
   * The messages to show beneath one control.
   *
   * Two sources, merged: this screen's own rules, and whatever the server reported against the
   * same member. The server's are read through the shared reader, which lower-cases the first
   * character of each model-state key so that it matches the control name, and which reads its
   * per-field dictionary with BRACKET access — property access on an index-signature type is a
   * compilation error in this workspace, deliberately.
   *
   * Nothing is shown until a submission has been attempted, so a form opened afresh is not
   * covered in complaints about fields the operator has not reached yet.
   *
   * @param control The control's name, which is also the member name the server reports.
   * @returns The messages, in rule order then server order. Empty when there are none.
   */
  protected messagesFor(control: keyof ProfileDefinitionFormModel): readonly string[] {
    const fromServer: readonly string[] = fieldErrorMessages(this.problem(), control).map(
      (message) => stripLegacyBreakTags(message),
    );

    if (!this.submitAttempted()) {
      return fromServer;
    }

    return [...this.clientMessages(control), ...fromServer];
  }

  // -------------------------------------------------------------------------
  // REMOVAL
  // -------------------------------------------------------------------------

  /**
   * Asks for confirmation before removing a declaration.
   *
   * MIGRATION: `DeleteProperty` L157-L164 removed immediately and then refreshed — removal was
   * never part of the batch — and the only guard was a browser `confirm` whose text came from
   * `Localization.GetString("DeleteItem")`, i.e. `SharedResources.resx` L120-L122. That text is
   * reproduced verbatim in the shared dialog, which adds a focus trap and `Escape` handling a
   * browser `confirm` never had.
   *
   * @param definition The declaration to remove.
   */
  protected requestRemoval(definition: ProfilePropertyDefinition): void {
    this.pendingRemoval.set(definition);
  }

  /** Abandons a removal. */
  protected cancelRemoval(): void {
    this.pendingRemoval.set(null);
  }

  /**
   * Removes the confirmed declaration.
   *
   * The guard is a presence test on the awaited target, not a truthiness test, and it exists
   * because the dialog's confirm output carries no payload.
   */
  protected confirmRemoval(): void {
    const target: ProfilePropertyDefinition | null = this.pendingRemoval();

    if (target === null) {
      return;
    }

    this.pendingRemoval.set(null);
    this.awaited.set({ kind: 'delete', propertyName: target.propertyName });
    this.store.deleteProfileDefinition(target.propertyDefinitionId);
  }

  // -------------------------------------------------------------------------
  // PRIVATE — DERIVED STATE
  // -------------------------------------------------------------------------

  /**
   * The declarations whose staged edits genuinely differ from what the server holds.
   *
   * ⚠ THIS IS DERIVED, NOT ORCHESTRATED, AND THAT IS WHAT MAKES A PARTIAL FAILURE
   * RECOVERABLE. {@link applyChanges} never clears the staged edits; instead a staged edit
   * stops counting the moment the server reports the same value. So after a batch in which
   * some writes landed and one was refused, the rows that landed silently drop out of this
   * set and the row that did not remains in it — Apply stays enabled for exactly the work
   * that is left, and the banner says why it was refused. An imperative "clear the draft on
   * success" would have thrown away the failed row's edit along with the successful ones.
   */
  private readonly pendingRows = computed<readonly ProfilePropertyDefinition[]>(() => {
    const edits: ReadonlyMap<number, GridEdits> = this.draft();

    if (edits.size === 0) {
      return [];
    }

    return this.store
      .profileDefinitions()
      .filter((definition) => {
        const staged: GridEdits | undefined = edits.get(definition.propertyDefinitionId);

        return staged !== undefined && differs(staged, definition);
      })
      .map((definition) => this.withDraft(definition, edits));
  });

  // -------------------------------------------------------------------------
  // PRIVATE — HELPERS
  // -------------------------------------------------------------------------

  /**
   * Overlays a declaration's staged edits, if it has any.
   *
   * A NEW object is returned rather than the held one mutated, because the contract's members
   * are `readonly` and the held instance belongs to a store several screens read.
   *
   * @param definition The declaration as the server reported it.
   * @param edits Every staged edit, keyed by identifier.
   * @returns The declaration as it should be displayed.
   */
  private withDraft(
    definition: ProfilePropertyDefinition,
    edits: ReadonlyMap<number, GridEdits>,
  ): ProfilePropertyDefinition {
    const staged: GridEdits | undefined = edits.get(definition.propertyDefinitionId);

    if (staged === undefined) {
      return definition;
    }

    return {
      ...definition,
      required: staged.required,
      visible: staged.visible,
      viewOrder: staged.viewOrder,
    };
  }

  /**
   * Stages one declaration's grid-editable members.
   *
   * The map is REPLACED rather than mutated, so the signal genuinely notifies; mutating the
   * held map in place would leave every reader on the same reference and nothing would
   * recompute.
   *
   * An entry whose values match what the server already holds is REMOVED rather than kept.
   * MIGRATION: the legacy dirty flag was one-way — `If _Required <> Value Then _IsDirty = True`
   * never cleared — so toggling a box on and then off left the row scheduled for a write that
   * would change nothing. Pruning it instead has an identical net effect on the database and
   * keeps Apply honest about whether there is anything to apply.
   *
   * @param propertyDefinitionId The declaration being edited.
   * @param next Its staged values.
   */
  private stage(propertyDefinitionId: number, next: GridEdits): void {
    const held: ProfilePropertyDefinition | undefined = this.store
      .profileDefinitions()
      .find((definition) => definition.propertyDefinitionId === propertyDefinitionId);
    const updated = new Map<number, GridEdits>(this.draft());

    if (held !== undefined && !differs(next, held)) {
      updated.delete(propertyDefinitionId);
    } else {
      updated.set(propertyDefinitionId, next);
    }

    this.draft.set(updated);
  }

  /**
   * Exchanges a declaration's position with its neighbour's.
   *
   * MIGRATION (DL-2): this is `MoveProperty` L176-L193 exactly — read the neighbour's position,
   * SWAP the two, re-sort, and DO NOT SAVE. One move therefore stages TWO rows and Apply issues
   * TWO writes, which is why there is no reorder endpoint to call and none is invented: position
   * is a field on the declaration, so moving a row is a replace of two declarations.
   *
   * ⚠ EQUAL POSITIONS ARE A CAVEAT INHERITED, NOT INTRODUCED. The position column carries no
   * uniqueness constraint, so two declarations may hold the same position, and exchanging equal
   * positions changes nothing — the row does not appear to move. The legacy had the identical
   * outcome for the identical reason. Renumbering the whole set instead would rewrite rows the
   * operator never touched, so the faithful behaviour is kept and recorded.
   *
   * A move off either end is DISCARDED rather than clamped: there is no neighbour to exchange
   * with, and inventing a position would move a row the operator did not name. The rendered
   * command is hidden at the ends anyway, so this guard is the backstop rather than the rule.
   *
   * @param definition The declaration to move.
   * @param offset -1 to move earlier, 1 to move later.
   */
  private move(definition: ProfilePropertyDefinition, offset: -1 | 1): void {
    const ordered: readonly ProfilePropertyDefinition[] = this.rows();
    const index: number = ordered.findIndex(
      (candidate) => candidate.propertyDefinitionId === definition.propertyDefinitionId,
    );

    if (index < 0) {
      return;
    }

    const neighbourIndex: number = index + offset;

    // The negative index is tested BEFORE `at` is called: `at(-1)` returns the LAST element,
    // so moving the first row up would otherwise exchange it with the last one.
    if (neighbourIndex < 0) {
      return;
    }

    const moved: ProfilePropertyDefinition | undefined = ordered.at(index);
    const neighbour: ProfilePropertyDefinition | undefined = ordered.at(neighbourIndex);

    if (moved === undefined || neighbour === undefined) {
      return;
    }

    this.stage(moved.propertyDefinitionId, {
      required: moved.required,
      visible: moved.visible,
      viewOrder: neighbour.viewOrder,
    });
    this.stage(neighbour.propertyDefinitionId, {
      required: neighbour.required,
      visible: neighbour.visible,
      viewOrder: moved.viewOrder,
    });
  }

  /**
   * Whether a row is the first in the displayed order, which is when Move Up is withheld.
   *
   * @param definition The row being rendered.
   * @returns True when there is no earlier neighbour.
   */
  protected isFirst(definition: ProfilePropertyDefinition): boolean {
    return this.rows().at(0)?.propertyDefinitionId === definition.propertyDefinitionId;
  }

  /**
   * Whether a row is the last in the displayed order, which is when Move Down is withheld.
   *
   * @param definition The row being rendered.
   * @returns True when there is no later neighbour.
   */
  protected isLast(definition: ProfilePropertyDefinition): boolean {
    return this.rows().at(-1)?.propertyDefinitionId === definition.propertyDefinitionId;
  }

  /**
   * This screen's own validation messages for one control.
   *
   * Order is rule order, and presence comes first so that an omitted name reports "required"
   * alone rather than stacking a format complaint on top of it. That is deliberate parity: an
   * ASP.NET `RegularExpressionValidator` SUCCEEDS against an empty control by design, and the
   * API reproduces the same cascade.
   *
   * @param control The control to describe.
   * @returns The messages, or an empty array when the control is valid.
   */
  private clientMessages(control: keyof ProfileDefinitionFormModel): readonly string[] {
    const errors: ValidationErrors | null = this.form.controls[control].errors;

    if (errors === null) {
      return [];
    }

    const messages: string[] = [];

    if (errors['required'] !== undefined) {
      messages.push(requiredMessageFor(control));
      return messages;
    }

    if (errors['sentinel'] !== undefined) {
      messages.push(DATA_TYPE_REQUIRED_MESSAGE);
    }

    if (errors['maxlength'] !== undefined) {
      messages.push(tooLongMessageFor(control));
    }

    if (errors['pattern'] !== undefined) {
      messages.push(NAME_PATTERN_MESSAGE);
    }

    if (errors['wholeNumber'] !== undefined) {
      messages.push(WHOLE_NUMBER_MESSAGE);
    }

    if (errors['expression'] !== undefined) {
      messages.push(EXPRESSION_INVALID_MESSAGE);
    }

    return messages;
  }

  /**
   * Reports a settled single write, and closes the form when it succeeded.
   *
   * A failure whose operation is not the one that was awaited is NOT this write's: a successful
   * create triggers a re-read, and a re-read that then failed must not be announced as a failed
   * create. Such a failure reaches the reader through the inline banner instead, which is where
   * a failed listing belongs.
   *
   * ⚠ THE FORM STAYS OPEN ON A REFUSAL, which is the whole point of distinguishing the two.
   * A duplicate name is corrected in the form the operator is already looking at; closing it
   * would discard eight other fields they had just filled in.
   *
   * @param pending What was requested.
   * @param failure The failure the store recorded, or null when nothing failed.
   */
  private report(pending: AwaitedWrite, failure: UserFailure | null): void {
    const mine: boolean =
      failure !== null && failure.operation === AWAITED_OPERATION[pending.kind];

    if (!mine) {
      if (pending.kind === 'delete') {
        this.restoreFocusAfterRemoval();
      } else {
        this.formOpen.set(false);
        this.editing.set(null);
        this.submitAttempted.set(false);
        this.form.reset(CREATE_DEFAULTS);
      }

      this.notifications.notify('success', successMessage(pending));
      return;
    }

    // `failure` is re-read from the narrowing above rather than asserted: `mine` is only true
    // when it is non-null, and the compiler needs the test in a position it can follow.
    const recorded: UserFailure | null = failure;

    if (recorded === null) {
      return;
    }

    this.notifications.notify(
      recorded.summary.severity,
      refusalMessage(pending, recorded),
      recorded.summary.supportReference,
    );
  }

  /**
   * Moves focus to the screen's primary action after a row has been removed.
   *
   * ⚠ THIS FIXES A REAL DEFECT MEASURED IN A BROWSER, NOT A HYPOTHETICAL ONE. The shared confirm
   * dialog returns focus to whichever control opened it, which is exactly right when the operator
   * CANCELS — focus was observed landing back on that row's own Delete button. A CONFIRMED delete
   * is the one case that logic cannot serve, because the row it deletes carries the invoking
   * button, so by the time the dialog closes the element to return to no longer exists. Focus
   * collapsed to `body`, which drops a keyboard or screen-reader user back to the very start of
   * the document after every delete and obliges them to tab through the whole shell again. That
   * is a WCAG 2.4.3 focus-order failure, and it is this screen's to fix rather than the dialog's:
   * the dialog behaved correctly, and it is this screen that destroyed the anchor.
   *
   * The anchor chosen is the create button. Three properties make it the right one: it is never
   * removed, so it cannot repeat the defect; it sits immediately beside the heading, so focus
   * lands at the top of the working area rather than somewhere arbitrary mid-grid; and it is this
   * screen's primary action, so the operator is left somewhere useful. Restoring to a surviving
   * row's command was considered and rejected — the rows are rendered by the shared table from
   * projected templates, so no stable per-row handle exists to address from here, and picking
   * "the next row" would move focus to a DIFFERENT property's destructive control, which is a
   * worse outcome than a neutral one.
   *
   * The outcome itself is not carried by the focus move: it is announced independently in the
   * polite live region by the caller, so a reader is told what happened as well as where they are.
   *
   * ⚠⚠ THE DEFERRAL IS MANDATORY, AND THE REASON IS SUBTLE ENOUGH TO HAVE COST A DEBUGGING PASS.
   * Every control on this screen is disabled while a write is in flight, the anchor included.
   * This method is reached from the reporting effect, and an effect is flushed BEFORE the view
   * that reads the same signals is refreshed. So at the instant this runs, `saving()` has already
   * returned false — that is the effect's own guard — while the DOM still carries the `disabled`
   * attribute from the previous render. A disabled element cannot take focus, so a direct call
   * here fails SILENTLY: no error, no exception, focus simply stays on the body. It was measured
   * doing exactly that, and the trap is that by the time any assertion or manual retry runs the
   * refresh has happened and the very same call succeeds — so the naive version looks correct
   * everywhere except in production.
   *
   * `afterNextRender` is therefore not defensive scheduling but the correct tool: it runs the
   * move after the render that clears `disabled`, which is the first moment the anchor can
   * actually accept focus. It needs an injector because this is not a constructor.
   *
   * `preventScroll` is deliberately NOT passed. The anchor is at the top of the screen and the
   * grid may have been scrolled far down it, so bringing the anchor into view is the point — a
   * focused control the operator cannot see is its own accessibility failure.
   */
  private restoreFocusAfterRemoval(): void {
    // A presence test on the resolved element, never a truthiness test on a DOM node, and no
    // non-null assertion: an unresolved query degrades to the browser's own fallback rather
    // than throwing inside an effect, where a throw would tear down the reporting for every
    // later write as well.
    const anchor: ElementRef<HTMLButtonElement> | undefined = this.createTrigger;

    if (anchor === undefined) {
      return;
    }

    afterNextRender(
      () => {
        // Re-checked rather than trusted: a render can happen between the request and the
        // callback, and focusing a control that is still disabled or has left the document
        // would be a silent no-op that hid a regression.
        const element: HTMLButtonElement = anchor.nativeElement;

        if (element.disabled || !element.isConnected) {
          return;
        }

        element.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * Assembles the twelve columns, in the legacy grid's own order.
   *
   * MIGRATION (DL-1): TWELVE, not ten, and the count is triple-proven — a tag census of
   * `ProfileDefinitions.ascx` L17-L33 yields twelve, enumerating those lines yields twelve, and
   * the code-behind's own zero-based cell constants reach eleven (`COLUMN_REQUIRED = 10`,
   * `COLUMN_VISIBLE = 11` at L47-L48), which is only consistent with twelve columns. The order
   * is theirs: four commands first, then eight data columns.
   *
   * MIGRATION (DL-9): the four command columns HIDE their headings. `Page_Load` L409-L411 ran
   * `Localization.LocalizeDataGrid`, which replaced each declared `HeaderText` with the resource
   * value, and `Edit.Header`, `Del.Header`, `Dn.Header` and `Up.Header` are all `<value />` —
   * EMPTY. The markup's "Edit", "Del", "Dn" and "Up" were therefore never painted. Hiding the
   * heading reproduces that exactly while the `label` keeps the column NAMED in the
   * accessibility tree, so a cell is still announced with its column name.
   *
   * ⚠ NO COLUMN IS SORTABLE, and that is measured rather than assumed: the legacy grid declared
   * no `AllowSorting` and no `SortExpression` anywhere. The shared table EMITS a sort intent and
   * never performs one, so marking a column sortable would oblige this screen to reorder the
   * rows itself and to drive `aria-sort` from its own state. There is nothing to reproduce, so
   * nothing is declared, and `sortChange` is left unbound.
   *
   * ⚠ NO WIDTHS. The legacy declared `Width="100px"` on five columns; the shared table's width
   * contract admits only a percentage, `min-content`, `max-content` or a custom property, and
   * rejects anything else when the set is bound. A pixel width cannot be expressed, so the
   * columns size intrinsically. Reported as a divergence in presentation with no behavioural
   * consequence.
   *
   * ⚠ `viewOrder` IS NOT A THIRTEENTH COLUMN. The legacy grid never displayed it — the two move
   * commands were its only visible expression — and the inline form is where its value is read
   * and written. Adding a column for it would pad the grid with something the legacy did not
   * show.
   *
   * @returns The twelve columns, in legacy order.
   */
  private buildColumns(): readonly DataTableColumn<ProfilePropertyDefinition>[] {
    return [
      // 0. `dnn:imagecommandcolumn CommandName="Edit"`. The actions kind also suppresses row
      //    activation, so pressing Edit never doubles as selecting the row.
      {
        key: 'edit',
        label: EDIT_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },

      // 1. `dnn:imagecommandcolumn CommandName="Delete"` — cell index 1, the index
      //    `ItemDataBound` reached into to hide the command for the four platform properties.
      {
        key: 'delete',
        label: DELETE_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },

      // 2-3. `MoveDown` then `MoveUp`, in that order. The order is the markup's (L19-L20) and
      //      the constants confirm it (`COLUMN_MOVE_DOWN = 2`, `COLUMN_MOVE_UP = 3`), which is
      //      worth stating because down-before-up reads backwards.
      {
        key: 'moveDown',
        label: MOVE_DOWN_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.moveDownCommandTemplate, 'moveDownCommand'),
      },
      {
        key: 'moveUp',
        label: MOVE_UP_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.moveUpCommandTemplate, 'moveUpCommand'),
      },

      // 4. `dnn:textcolumn DataField="PropertyName"`, heading `Name.Header`. A bound column:
      //    the member is a non-nullable string, so there is nothing to format.
      {
        key: 'propertyName',
        label: NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'propertyName',
      },

      // 5. `dnn:textcolumn DataField="PropertyCategory"`, heading `Category.Header`.
      {
        key: 'propertyCategory',
        label: CATEGORY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'propertyCategory',
      },

      // 6. `asp:TemplateColumn HeaderText="DataType"`. A template column because the legacy cell
      //    was itself a template that called `DisplayDataType`; see {@link dataTypeLabel} for the
      //    reported gap that leaves it rendering the integer.
      {
        key: 'dataType',
        label: DATA_TYPE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.dataTypeCellTemplate, 'dataTypeCell'),
      },

      // 7. `dnn:textcolumn DataField="Length"`, heading `Length.Header`. Aligned to the END of
      //    the cell because it is a quantity; the HEADING keeps the grid's centred alignment,
      //    which is why the two members are set independently and neither is derived from the
      //    other.
      {
        key: 'length',
        label: LENGTH_HEADING,
        headerAlign: 'center',
        bodyAlign: 'end',
        field: 'length',
      },

      // 8-9. `DefaultValue` and `ValidationExpression`. Heading text from `DefaultValue.Header`
      //      and `ValidationExpression.Header` — "Default Value" and "Validation Expression",
      //      SPACED, which is where the resource values beat the markup's unspaced attributes.
      //
      //      Template columns, for two reasons. Both members are nullable, and the shared bound
      //      column renders text only, so a `null` would need formatting anyway. More
      //      importantly both hold TENANT-AUTHORED text of unknown shape — one of them is a
      //      regular expression — so each is rendered as monospaced, INTERPOLATED text. Nothing
      //      in this file binds `innerHTML` or reaches for a sanitiser, because nothing here is
      //      treated as markup at all.
      {
        key: 'defaultValue',
        label: DEFAULT_VALUE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.defaultValueCellTemplate, 'defaultValueCell'),
      },
      {
        key: 'validationExpression',
        label: VALIDATION_EXPRESSION_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.expressionCellTemplate, 'expressionCell'),
      },

      // 10-11. `dnn:checkboxcolumn DataField="Required"` and `="Visible"`, both
      //        `AutoPostBack="True"` — the two columns the operator edits IN PLACE, and the
      //        reason this screen has a batch to apply at all. Template columns, because a
      //        check box is a control and the shared bound column renders text.
      //
      //        MIGRATION: the legacy check box posted back on every click (or, on a browser it
      //        judged capable, deferred to a client script that suppressed the post-back —
      //        `ItemCreated` L539-L557). Both paths ended in the same place: the value was held
      //        and written on Apply. That is what happens here, with no round trip and no second
      //        code path.
      {
        key: 'required',
        label: REQUIRED_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.requiredCellTemplate, 'requiredCell'),
      },
      {
        key: 'visible',
        label: VISIBLE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.visibleCellTemplate, 'visibleCell'),
      },
    ];
  }

  /**
   * Resolves a captured cell template, or fails loudly.
   *
   * The shared table declares `cellTemplate` as REQUIRED on both the template and the actions
   * kinds, so an absent one cannot be passed. A missing `ng-template` is a template authoring
   * mistake, and failing here names the reference that is missing instead of rendering nine
   * columns and a blank.
   *
   * @param captured The result of the static view query.
   * @param reference The `ng-template` reference the query looked for.
   * @returns The template.
   * @throws Error when the template is not in the view.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<ProfilePropertyDefinition>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<ProfilePropertyDefinition>> {
    if (captured === undefined) {
      throw new Error(
        `profile-definition-list.component.html must declare an ng-template named ` +
          `"#${reference}" at the top level of the template, outside any control-flow block.`,
      );
    }

    return captured;
  }
}
