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
import { toSignal } from '@angular/core/rxjs-interop';
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
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import type {
  CreateProfilePropertyDefinitionRequest,
  ProfilePropertyDefinition,
  UpdateProfilePropertyDefinitionRequest,
} from '../../../core/models/profile.model';
import type {
  ProfileDefinitionEdit,
  UserFailure,
  UserMutation,
  UserOperation,
  ProfileDefinitionBatchRefusal,
} from '../../../core/state/user.store';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// WORDING
//
//  Standing rule applied throughout: every string below is the VALUE from a legacy resource file, never a
//  markup `HeaderText`, `Text` or `ErrorMessage` attribute. The two are not the same thing and they disagree
//  in this screen more than once: `ProfileDefinitions.ascx.vb` ran `Localization.LocalizeDataGrid`, which
//  REPLACED every declared `HeaderText` with the resource value before the grid was ever painted, so the
//  markup's text was never what a reader saw.
//
//  The double spaces are deliberate and are reproduced byte for byte. They are in the resource values
//  themselves, and normalising them would silently reword the product.

/**
 * `ControlTitle_manageprofile.Text`.
 */
const PAGE_TITLE = 'Manage Profile Properties';

/**
 * `ProfilePropertiesHelp.Text`, double spaces included.
 */
const HELP_TEXT =
  'You can change the order of the profile fields, and whether they are Required or ' +
  'Visible on this screen.  Click on the "Apply Changes" button to save any changes you ' +
  'make.  To edit other properties of each Profile Property click the pencil icon in the ' +
  'first column of the grid.';

/**
 * `AddContent.Action` — the legacy module action that opened the editor.
 */
const ADD_LABEL = 'Add New Profile Property';

/**
 * `cmdApply.Text`.
 */
const APPLY_LABEL = 'Apply Changes';

/**
 * `cmdRefresh.Text`. Discards uncommitted edits; see {@link ProfileDefinitionListComponent.refresh}.
 */
const REFRESH_LABEL = 'Refresh Grid';

/**
 * The four per-row command labels.
 *
 * MIGRATION: the two move commands take their wording from `MoveDown.Text` and `MoveUp.Text`, which
 * the legacy really did publish. The edit and delete commands have NO resource value at all — `Edit.Header`,
 * `Del.Header`, `Dn.Header` and `Up.Header` are all `<value />`, i.e. EMPTY — so the four column HEADINGS
 * rendered blank and the markup's `"Edit"`, `"Del"`, `"Dn"` and `"Up"` were never displayed. Worse,
 * `Page_Init` overwrote the delete button's own `Text="Delete"` with `Localization.GetString("Delete",
 * LocalResourceFile)` against a resource file that declares no `Delete` key, so the delete command reached
 * assistive technology with NO ACCESSIBLE NAME. The headings stay hidden here, which preserves the
 * visual parity, but every command carries a real name, which closes the accessibility defect at zero visual
 * cost.
 */
const EDIT_LABEL = 'Edit';
const DELETE_LABEL = 'Delete';
const MOVE_UP_LABEL = 'Move Up';
const MOVE_DOWN_LABEL = 'Move Down';

/**
 * Column headings, from the eight non-empty `*.Header` values.
 */
const NAME_HEADING = 'Name';
const CATEGORY_HEADING = 'Category';
const DATA_TYPE_HEADING = 'DataType';
const LENGTH_HEADING = 'Length';
const DEFAULT_VALUE_HEADING = 'Default Value';
const VALIDATION_EXPRESSION_HEADING = 'Validation Expression';
const REQUIRED_HEADING = 'Required';
const VISIBLE_HEADING = 'Visible';

/**
 * `Introduction_Add.Title` and `Introduction.Title` — resolved by `GetText`.
 */
const CREATE_HEADING = 'Add New Property Details';
const EDIT_HEADING = 'Edit Property Details';

/**
 * `cmdCreate.Text`, `cmdUpdate.Text` and `cmdCancel.Text`.
 */
const CREATE_SUBMIT_LABEL = 'Create New Property';
const EDIT_SUBMIT_LABEL = 'Update Property';
const CANCEL_LABEL = 'Return to Profile Properties List';

/**
 * The nine field labels and their help text, from the `ProfilePropertyDefinition_<member>.Text` and `.Help`
 * pairs.
 *
 * The typo is deliberate. `ProfilePropertyDefinition_PropertyCategory.Help` reads "dislayed" in the legacy
 * resource value. It is reproduced verbatim rather than corrected, because the wording is the product's and
 * correcting it here would be an unrequested content change in a migration whose whole discipline is
 * behavioural equivalence. It is recorded as an observed defect instead.
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
 * Three are legacy resource values, transcribed exactly: `ProfilePropertyDefinition_PropertyName.Required`,
 * `ProfilePropertyDefinition_PropertyName.Validation` and
 * `ProfilePropertyDefinition_PropertyCategory.Required`.
 *
 * `NAME_PATTERN_MESSAGE` is a legacy resource value that UNDERSTATES the rule it describes, and it
 * is reproduced anyway. `.Validation` reads "The property name cannot contain spaces", but the rule actually
 * enforced was `RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")` on `ProfilePropertyDefinition.vb`,
 * which rejects far more than a space — every bracket, slash, comma, colon and accented letter with it. The
 * PATTERN is authoritative, because the Minimal Change Clause requires validation RULES to match; the
 * resource value is authoritative for the MESSAGE, because the standing wording rule takes every string from
 * the resource value. Rewording it to describe the rule accurately would be an unrequested content change,
 * so the divergence is REPORTED instead. Note that the API publishes its own, more precise sentence for the
 * same rule — "Property Name may contain only letters, numbers and the characters. _ % - + '" — which is
 * what a reader sees if a request ever reaches the server with a name this client would have refused.
 *
 * ⚠ DL-11 — FOUR members carry `Required(True)`, not two. `DataType` (L88-L91) and
 * `ViewOrder` (L300) are required as well, and NEITHER has a resource value. The sentences for
 * them are AUTHORED in the register of the two that exist ("The <Field> is required") and are
 * reported as authored wording rather than presented as recovered legacy text.
 *
 * ⚠ THE VIEW ORDER DOES NEED A SENTENCE, AND THE REASONING THAT SAID OTHERWISE WAS WRONG. It read:
 * the control is `nonNullable` over a number, so presence is structural. `nonNullable` governs what
 * `reset()` returns a control to; it does not stop a `<input type="number">` writing `null` when its
 * box is cleared. So presence was not structural at all — the field was marked required in the
 * template and `aria-required` to assistive technology, accepted being left empty, and sent `null` to
 * a server integer declared non-nullable, which answered a generic 400 naming no field. The same
 * applies to the length. Both now carry a rule, and both need wording for it.
 */
const NAME_REQUIRED_MESSAGE = 'The Property Name is required';
const NAME_PATTERN_MESSAGE = 'The property name cannot contain spaces';
const CATEGORY_REQUIRED_MESSAGE = 'The Category is required';
const DATA_TYPE_REQUIRED_MESSAGE = 'The Data Type is required';

/** Authored, in the register of the two legacy sentences. The API declares `Length` non-nullable. */
const LENGTH_REQUIRED_MESSAGE = 'The Length is required';

/** Authored likewise. The template marks the field required; this is the rule that enforces it. */
const VIEW_ORDER_REQUIRED_MESSAGE = 'The View Order is required';

/**
 * Authored wording for the rules the legacy declared no validator for.
 *
 * MIGRATION: the legacy editor delegated entry to the excluded property-editor control, which coerced
 * silently under Option Strict OFF rather than reporting anything — the asymmetry Phase 8 of the migration
 * plan requires to be made explicit. These refuse the input instead of coercing it, and each one is recorded
 * as authored wording.
 *
 * The two length limits are NOT authored: they are the API's own sentences, reproduced so that the same rule
 * reads the same way whichever side reports it. Their widths are the terminal column widths — `PropertyName
 * nvarchar(50)`, `PropertyCategory nvarchar(50)`, and 512 for the expression as a deliberate narrowing of
 * the terminal `nvarchar(2000)`. `DefaultValue` carries no limit at all, because `04.05.00` widened its
 * column to `ntext`.
 */
const WHOLE_NUMBER_MESSAGE = 'Enter a whole number.';
const NAME_TOO_LONG_MESSAGE = 'Property Name must be 50 characters or fewer';
const CATEGORY_TOO_LONG_MESSAGE = 'Property Category must be 50 characters or fewer';
const EXPRESSION_TOO_LONG_MESSAGE = 'Validation Expression must be 512 characters or fewer';

/**
 * Terminal column widths the API also enforces.
 */
const NAME_MAX_LENGTH = 50;
const CATEGORY_MAX_LENGTH = 50;
const EXPRESSION_MAX_LENGTH = 512;

/**
 * `SharedResources.resxDeleteItem.Text` — the legacy confirm text.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * `DuplicateName.Text`, reproduced byte for byte including both double spaces.
 *
 * MIGRATION: the legacy signalled this outcome through a RETURN VALUE BELOW THE NULL SENTINEL —
 * `Wizard_NextButtonClick` assigned `AddPropertyDefinition`'s result into the identifier and then tested `If
 * PropertyDefinitionID < Null.NullInteger`, i.e. "less than minus one". The target receives it as `409`
 * carrying `profile-definition.duplicate-name`, and presents it at the ERROR severity the legacy chose
 * (`ModuleMessageType.RedError`, not the yellow warning).
 */
const DUPLICATE_NAME_MESSAGE =
  'This Property already exists.  Property Names must be unique.  Please select a ' +
  'different name for this property.';

/**
 * Authored: the `409` a removal draws when values are recorded against the declaration.
 */
const DEFINITION_IN_USE_MESSAGE =
  'That profile property is in use, so it was not deleted. Remove the recorded values ' +
  'first.';

/**
 * Authored: the `404` a removal or replacement draws when the declaration is already gone.
 */
const DEFINITION_GONE_MESSAGE =
  'That profile property no longer exists. The list has been refreshed.';

/**
 * Names the property a batch refusal belongs to, in front of the refusal's own sentence.
 *
 * ⚠ AUTHORED, WITH NO LEGACY COUNTERPART, AND THE ABSENCE IS WHY IT IS NEEDED. Apply writes one row
 * per edited declaration, and the legacy screen could report at most one outcome for the whole batch
 * — it held a single message area and a single provider result — so it never had to say WHICH row a
 * refusal belonged to. The rows are independent and the batch is not a transaction, so a five-row
 * apply with two refusals produces two messages, and a message that did not name its property would
 * leave the operator to guess which of the five to correct.
 *
 * @param propertyName The property the refused write addressed.
 * @param sentence The refusal's own wording.
 * @returns The message to announce.
 */
function batchRefusalMessage(propertyName: string, sentence: string): string {
  return `${propertyName}: ${sentence}`;
}

/** The caption `app-data-table` projects into its `<caption>` element. */
const GRID_CAPTION = 'Profile properties declared for this site';

/**
 * Wording of the two bulk toggles. See {@link ProfileDefinitionListComponent.setAllRequired}.
 */
const ALL_REQUIRED_LABEL = 'Required for every property';
const ALL_VISIBLE_LABEL = 'Visible for every property';

// Rules measured out of the legacy source

/**
 * The name rule, lifted verbatim from `ProfilePropertyDefinition.vb`.
 *
 * Anchored at both ends exactly as the legacy declared it, and the hyphen is escaped in the same position,
 * so the accepted set is identical: letters, digits, dot, underscore, percent, hyphen, plus and apostrophe.
 * `+` after the class means at least one character, which is why an empty name fails this rule as well as
 * the required rule.
 */
const NAME_PATTERN = /^[a-zA-Z0-9._%\-+']+$/;

/**
 * The four declarations whose DELETE command the legacy screen hid.
 *
 * MIGRATION: `grdProfileProperties_ItemDataBound` reached into cell index 1 — the delete column — and
 * set `delImage.Visible = False` for exactly these four names, compared with `PropertyName.ToLower`. They
 * are the properties the platform itself depends on, so removing one would break the product rather than the
 * tenant's data.
 *
 * Held as a frozen array of lower-case names and compared after lower-casing the row's name, which
 * reproduces the legacy comparison exactly. A `const enum` would be the obvious shape for a closed set like
 * this and is deliberately NOT used: this workspace compiles with `isolatedModules`, under which a `const
 * enum` cannot be erased safely.
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
 * GAP: the shared conflict vocabulary in `core/utils/form-errors.util.ts` carries no profile-definition code
 * at all — its eleven members cover portals, roles, pages and modules only — so the two sentences these
 * codes select cannot be obtained from it and are declared here instead. The gap is REPORTED rather than
 * closed, because that utility belongs to another agent. The three codes themselves are the ones the API
 * really emits: `profile-definition.duplicate-name`, `profile-definition.not-found` and
 * `profile-definition.validation-expression-invalid`, normalised from hyphens to underscores by the shared
 * reader.
 */
const DUPLICATE_NAME_CODE = 'profile_definition.duplicate_name';
const NOT_FOUND_CODE = 'profile_definition.not_found';

/**
 * The three store operations this screen awaits the outcome of.
 */
type AwaitedWrite =
  | { readonly id: number; readonly kind: 'create' }
  | { readonly id: number; readonly kind: 'update'; readonly propertyDefinitionId: number }
  | { readonly id: number; readonly kind: 'delete'; readonly propertyName: string };

/**
 * The store operation each awaited write settles as.
 *
 * Declared as a lookup rather than a switch so that the effect can compare the failure's own operation
 * against the one it is waiting for. A failure belonging to some OTHER operation is not this write's — a
 * successful create triggers a re-read, and a re-read that then failed must not be announced as a failed
 * create.
 */
const AWAITED_OPERATION = {
  create: 'createProfileDefinition',
  update: 'updateProfileDefinition',
  delete: 'deleteProfileDefinition',

  /**
   * The Apply batch, which the store writes as one command over many rows.
   *
   * Not addressable through {@link AwaitedWrite}: the three entries above are keyed by the kind of
   * single write the form and the removal dialogue dispatch, whereas the batch is settled from its
   * own register. It is named here so the batch's settled result is matched against a constant
   * rather than a repeated literal.
   */
  applyEdits: 'applyProfileDefinitionEdits',
} as const;

/**
 * The two store operations that populate THIS screen, and the only two whose failures belong in
 * its banner.
 *
 * Declared as a set rather than tested inline so that the membership is stated once and a reader
 * can see the whole of it. The catalogue read is what fills the grid; the single-declaration read
 * is what the inline form is opened from.
 */
const CATALOGUE_READS: ReadonlySet<UserOperation> = new Set<UserOperation>([
  'loadProfileDefinitions',
  'loadProfileDefinition',
]);

/**
 * Whether an operation is one of this screen's own reads.
 *
 * @param operation The operation that failed.
 * @returns True when its failure belongs in this screen's banner.
 */
function isCatalogueRead(operation: UserOperation): boolean {
  return CATALOGUE_READS.has(operation);
}

/**
 * The typed shape of the inline create-and-edit form.
 *
 * Every control is `nonNullable`, which buys two properties this screen depends on: `form.value` is the
 * whole model rather than a `Partial`, so a submission cannot be assembled from members the compiler
 * believes may be missing; and `reset(value)` returns each control to a real value instead of to `null`,
 * which matters because ONE form instance serves creation and the editing of every row in turn.
 *
 * The members are in the order `SortOrder` declared them on
 * `Library/Components/Users/Profile/ProfilePropertyDefinition.vb`: name 0, data type 1, category 2,
 * length 3, default value 4, expression 5, required 6, visible 7, view order 8. That is the order the
 * legacy property editor painted, so it is the order the template paints, and it is NOT alphabetical or
 * contract order.
 *
 * There is deliberately no `visibility` control and no `moduleDefId` control. Both are
 * `Browsable(False)` on the legacy definition (L336 and L159), so the legacy editor never
 * offered either, and the replace contract accepts neither.
 *
 * ⚠ THE THREE NUMERIC CONTROLS ARE TYPED `number | null`, AND THAT IS A CORRECTION RATHER THAN A
 * LOOSENING. They were typed `number`, which the browser then contradicted: a `<input type="number">`
 * whose value is cleared writes `null` into its control, whatever the declared type says. So the
 * declared type asserted something untrue, and the consequences ran all the way to the server:
 * `wholeNumber()` returns valid for `null` and the sentinel check coerced `null` to 0 and passed it,
 * so a cleared field produced a form the screen believed valid, `null` was assembled into a request
 * whose contract declares three NON-NULLABLE integers, and the server answered a generic `400` that
 * named no field. The operator saw a refusal with nothing to correct.
 *
 * Admitting `null` in the type is what makes the emptiness expressible, so it can be REFUSED here,
 * beside the control, with a sentence naming the field — and narrowed away before the request is
 * assembled, so the non-nullable contract cannot receive an absence.
 */
interface ProfileDefinitionFormModel {
  propertyName: FormControl<string>;
  dataType: FormControl<number | null>;
  propertyCategory: FormControl<string>;
  length: FormControl<number | null>;
  defaultValue: FormControl<string>;
  validationExpression: FormControl<string>;
  required: FormControl<boolean>;
  visible: FormControl<boolean>;
  viewOrder: FormControl<number | null>;
}

/**
 * Every control's messages, keyed by control name.
 *
 * The shape {@link ProfileDefinitionListComponent.fieldMessages} publishes, so the template reads
 * one memoised map instead of calling a function per binding. Keyed by
 * `keyof ProfileDefinitionFormModel` rather than by `string`, which is what makes a mistyped
 * control name a compilation error rather than an empty message list at run time.
 */
type ProfileDefinitionFieldMessages = Readonly<
  Record<keyof ProfileDefinitionFormModel, readonly string[]>
>;

/**
 * The resolved value of {@link ProfileDefinitionFormModel}.
 *
 * Declared explicitly rather than inferred from the group, because it is also the shape of {@link
 * CREATE_DEFAULTS} and of the value {@link ProfileDefinitionListComponent} hands to `reset`, and a single
 * named contract is what keeps those three from drifting apart. It differs from the API's write contract in
 * exactly one way: the two optional strings are `string` here and `string | null` there, because
 * `Null.NullString` is the empty string and a text control has no way to hold `null`.
 */
interface ProfileDefinitionFormValue {
  readonly propertyName: string;
  readonly dataType: number | null;
  readonly propertyCategory: string;
  readonly length: number | null;
  readonly defaultValue: string;
  readonly validationExpression: string;
  readonly required: boolean;
  readonly visible: boolean;
  readonly viewOrder: number | null;
}

/**
 * {@link ProfileDefinitionFormValue} with the three numeric members proved present.
 *
 * ⚠ THE TYPE THAT MAKES THE NARROWING A COMPILE-TIME FACT. The request contracts declare
 * `DataType`, `Length` and `ViewOrder` as non-nullable integers, and the form's controls can each
 * hold `null` because a cleared number input writes one. Assembling a request straight from the raw
 * form value therefore used to type-check while being able to send three absences — which is how
 * `null` reached the server and drew a `400` that named no field at all.
 *
 * Derived by an override rather than restated, so a member added to the form appears here
 * automatically and cannot be forgotten.
 */
type NarrowedProfileDefinitionFormValue = Omit<
  ProfileDefinitionFormValue,
  'dataType' | 'length' | 'viewOrder'
> & {
  readonly dataType: number;
  readonly length: number;
  readonly viewOrder: number;
};

/** The members of a declaration this grid may change without opening the form. */
interface GridEdits {
  readonly required: boolean;
  readonly visible: boolean;
  readonly viewOrder: number;
}

/**
 * One grid row instance, whose members may be refreshed in place.
 *
 * ⚠ THIS TYPE EXISTS SO THAT A ROW'S OBJECT IDENTITY CAN OUTLIVE ITS CONTENTS, which is the
 * whole of the fix it belongs to. The shared table tracks its rows by OBJECT REFERENCE — it
 * reads no identifier member and cannot be asked to, the column descriptor names no key field
 * and adding one would widen a closed component surface — so replacing a row object destroys
 * that row's `<tr>` and rebuilds it. Every control inside it goes with it, including the one
 * the operator is holding: staging an edit on the required or visible checkbox re-projected
 * that row, so the checkbox the operator had just pressed was removed from the document and
 * focus fell back to the body. The reordering commands were worse, because a move restages
 * TWO rows and the button pressed was on one of them, so the reader lost their place in the
 * grid at the exact moment they were working through it.
 *
 * Derived from the contract with `-readonly` rather than declared by hand, so it cannot drift
 * from it: a member added to the contract appears here automatically and with the same type,
 * and no cast is needed anywhere — a mutable object is assignable to its readonly counterpart,
 * so these instances satisfy `ProfilePropertyDefinition` wherever one is expected.
 *
 * ⚠ THE MUTATION IS CONFINED TO INSTANCES THIS COMPONENT OWNS. Nothing held by the store is
 * ever written to: each instance starts as a COPY of the declaration the store reported, and
 * the store's own objects stay untouched, which matters because several screens read them.
 */
type MutableProfileRow = {
  -readonly [K in keyof ProfilePropertyDefinition]: ProfilePropertyDefinition[K];
};

// ---------------------------------------------------------------------------
// SENTINELS
//
//  `Library/Components/Shared/Null.vb` declares `NullInteger` as -1 and `NullString` as the EMPTY STRING
//  rather than as null. Both survive on the wire, because the API serialises with `DefaultIgnoreCondition =
//  Never`, so an unset default value or expression arrives as `""` and not as an omitted member.
//
//  -1 IS NOT A UNIVERSAL "ABSENT" MARKER IN THIS SCREEN. It means three different things on three different
//  members, and collapsing them would be a real defect:
//
//   propertyDefinitionId  -1 never occurs. The column is `IDENTITY(1, 1)` in 04.00.04, so the first real
//                         declaration is 1 and absence is expressed as `null` in this file, tested with
//                         `=== null`.
//   dataType              -1 is the legacy field initialiser, i.e. "no type chosen yet". The member is
//                         `Required(True)`, so -1 must be REFUSED on submit.
//   viewOrder             -1 is an INSTRUCTION, not an absence. The terminal create procedure branches on
//                         `IF @vieworder = -1` in 04.06.00 and substitutes the current maximum order plus
//                         one, so -1 is the only way a caller can say "append to the end". It must NOT be
//                         refused.
//
//  And 0 is REAL on every numeric member here. `ProcessPostBack` assigned `ViewOrder = i` from a zero-based
//  index, so the first row genuinely holds 0; the legacy field initialiser for `ViewOrder` is 0 as well; and
//  a `Length` of 0 means "no declared bound" rather than "unset". Nothing in this file tests any of them for
//  truthiness.

/**
 * `Null.NullInteger`. Named so that no comparison in this file spells a bare -1.
 */
const NULL_INTEGER = -1;

/**
 * What the data-type cell paints. U+2014 EM DASH.
 *
 * WHY THE STORED INTEGER IS NOT PAINTED. `DisplayDataType` L339-L351 is four statements long and it
 * never returns a number:
 *
 *     Dim retValue As String = Null.NullString
 *     Dim definitionEntry As ListEntryInfo = objListController.GetListEntryInfo(definition.DataType)
 *     If Not definitionEntry Is Nothing Then retValue = definitionEntry.Value
 *     Return retValue
 *
 * It returns the resolved list-entry VALUE, or `Null.NullString` when the entry cannot be found. So
 * painting `349` was a divergence from the legacy screen, not a faithful rendering of it, and the raw
 * foreign key reached the user in a column headed `DataType` where it reads as a type name.
 *
 * WHY THE NAME CANNOT BE RESOLVED, ON THREE INDEPENDENT GROUNDS. The name lives in the shared `Lists`
 * lookup table, filtered to the entries whose list name is `DataType`. First, `Library/Components/Lists`
 * is one of the twenty-three sub-trees the migration plan excludes by name, so building a resolver is
 * outside this work. Second, there is no `Lists` entity, configuration, repository or endpoint anywhere
 * in the backend — the API's own validators decline to range-check the member for exactly this reason,
 * recording that "that subsystem is excluded ... so there is no set to check membership of". Third, and
 * decisively, `dbo.Lists` is not one of the tables this application's schema declares at all; it is
 * created only by the legacy upgrade chain. There is therefore nothing to read even in principle, which
 * is precisely the branch `DisplayDataType` handled by returning the empty string.
 *
 * WHY A MARK AND NOT A BLANK. A strictly literal reading of `Null.NullString` would paint nothing. Two
 * things argue against that. The mark is this application's settled vocabulary for "no value to show" —
 * it is used for absent portal tallies, absent user profile values, absent alias host names, absent role
 * periods and fees, and absent module titles, in every case paired with wording carried to the
 * accessibility tree. A silent cell here would be the only absent-value treatment in the application
 * that says nothing. More importantly, this blank would not BE the datum, it would CONCEAL one: the row
 * really does store 349. That is the distinction between this cell and the role description column,
 * where a blank was left blank because `Null.NullString` genuinely is the stored value. So the mark is
 * painted, the reference travels in the accessibility tree and in a `title`, and nothing is destroyed.
 */
const UNNAMED_DATA_TYPE_MARK = '\u2014';

/**
 * What precedes the stored reference when one IS stored.
 *
 * ⚠ THE STORED REFERENCE IS NOW PAINTED, AND THE ANNOTATION ABOVE ARGUED AGAINST PAINTING IT. That
 *   argument was right about one thing and wrong about another, and the prefix is what separates them.
 *   It was right that a BARE `349` in a column headed `DataType` reads as though it were the type's name,
 *   which is a divergence from a legacy screen that rendered a name or nothing at all. It was wrong to
 *   conclude that the reference must therefore stay out of sight: runtime testing measured the mark alone
 *   on 100% of rows, and a sighted operator reading it learns "this property has no data type", which is
 *   FALSE - every row stores one. The wording that corrects them lived only in a `title` and in a
 *   screen-reader-only span, so a sighted reader had to hover a cell to discover that the cell was
 *   understating its own row.
 *
 *   Prefixing defeats the original objection directly: `#349` cannot be read as a type name, only as a
 *   reference to one, which is exactly what it is. Nothing is invented - no name is guessed at, no list
 *   vocabulary is faked, and the three grounds above for why the name is unresolvable are all untouched.
 *   The mark is now reserved for the one row shape that genuinely has nothing stored, the
 *   `Null.NullInteger` sentinel, so the two facts the accessible wording has always distinguished are
 *   finally distinguished on screen as well.
 */
const DATA_TYPE_REFERENCE_PREFIX = '#';

/**
 * The wording behind {@link UNNAMED_DATA_TYPE_MARK} when a type IS stored but cannot be named.
 *
 * Split around the reference so the number is interpolated between two fixed strings rather than
 * assembled by a template, which keeps the sentence in one place and lets the `title` attribute and the
 * clipped span be generated from a single accessor. One rule, one sentence.
 */
const UNNAMEABLE_DATA_TYPE_PREFIX = 'data type reference ';
const UNNAMEABLE_DATA_TYPE_SUFFIX = ', name unavailable';

/**
 * The wording behind {@link UNNAMED_DATA_TYPE_MARK} for the `Null.NullInteger` sentinel.
 *
 * -1 is the legacy field initialiser and means "no type chosen yet", which is a different fact from a
 * stored type whose name cannot be read. The two cases paint the same mark, exactly as the legacy
 * rendered both as the empty string, but they are described differently because they are not the same.
 */
const NO_DATA_TYPE_CHOSEN_DESCRIPTION = 'no data type chosen';

/**
 * The create-mode defaults, measured from the legacy field initialisers.
 *
 * `Visible` defaulting to false is easy to get wrong by assuming a new property ought to be shown; the
 * legacy default is false and it is reproduced.
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

// VALIDATORS
//
//  All legacy validation was imperative — a guard clause inside a postback handler, or a server control the
//  property editor added at run time. Every rule below is DECLARATIVE instead, attached to the control it
//  governs, so a rule cannot be skipped by a code path that forgot to call it. That is a mechanism change and
//  is annotated as one.

/**
 * Requires a whole number, accepting zero and negative values alike.
 *
 * NO LOWER BOUND, deliberately. A bound would make this client refuse input the API accepts: its own
 * validator declares no rule for either numeric member, and for the view order it explains why a bound would
 * be actively wrong — -1 is the append instruction. The rule this validator DOES enforce is the column's
 * integral type, which the legacy never checked because Option Strict OFF coerced whatever was typed.
 *
 * Emptiness is not this validator's business: an empty numeric control yields `null` at run time whatever
 * the declared type says, and presence is `Validators.required`'s question. Reporting both would stack two
 * messages on one omission.
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
 * Refuses ABSENCE on a numeric member the request contract declares non-nullable.
 *
 * ⚠ DISTINCT FROM {@link Validators.required}, WHICH CANNOT BE USED HERE. That validator refuses a
 * value it considers empty, and it considers 0 to be present — which is correct — but it is applied
 * to a control the browser can write `null` into whatever its declared type says, and the two
 * numeric members this is applied to previously had NO absence rule at all: `wholeNumber()` returns
 * valid for `null` and the sentinel check coerced `null` to 0 and passed it. A cleared box therefore
 * produced a form the screen believed valid, and `null` reached a server integer declared
 * non-nullable, which answered a generic `400` naming no field. The operator saw a refusal with
 * nothing to correct.
 *
 * `Validators.required` would in fact serve for two of the three, but is deliberately not used: it
 * reports the framework's own error key with no message, and every other rule on this screen reports
 * a sentence through the same `message` member so that one wording table serves the whole form.
 *
 * @param message The sentence to report.
 * @returns A validator reporting `requiredNumber` when the control holds no number at all.
 */
/**
 * Whether a validation error carries a sentence to show.
 *
 * A validator's error detail is typed `unknown` by the framework's own contract, so the shape has to
 * be established rather than asserted. Narrowing by test instead of by cast is what makes the read
 * below safe against a validator that is ever changed to report something else.
 *
 * @param detail The error detail to inspect.
 * @returns True when it carries a string `message`.
 */
function isMessageBearingError(detail: unknown): detail is { readonly message: string } {
  // The `in` test narrows the value to one carrying the member, so the member is read without an
  // assertion — the workspace forbids one, and here it would also be the wrong tool: the whole point
  // is to establish the shape rather than to claim it.
  return (
    typeof detail === 'object' && detail !== null && 'message' in detail
    && typeof detail.message === 'string'
  );
}

function requiredNumber(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    // ⚠ THE THREE VALUES A CLEARED NUMERIC INPUT CAN PRODUCE, AND ALL THREE ARE ABSENCE. A browser
    // writes `null` when the box is emptied; it writes the empty string when the box holds text it
    // cannot parse as a number; and `undefined` is what a control reset without a value holds. None
    // of them is a number, and every one of them used to reach a non-nullable server integer.
    //
    // Zero is emphatically NOT among them and must never be added: a first position of 0 and a
    // length of 0 are both legitimate stored values, so a truthiness test here would refuse exactly
    // the values the legacy schema seeds.
    if (raw === null || raw === undefined || raw === '') {
      return { requiredNumber: { message } };
    }

    return null;
  };
}

/**
 * Refuses the null-integer sentinel on a member the legacy declared `Required(True)`.
 *
 * MIGRATION: this is how `Required(True)` on `DataType` is reproduced. The legacy paired that attribute with
 * a field seeded to the sentinel, so "required" meant "not still the sentinel" — and `Validators.required`
 * cannot express that, because -1 is a perfectly present value. The comparison is EXPLICIT against the named
 * sentinel, never a truthiness test, and it deliberately does not refuse 0: zero is a legitimate key on
 * other members of this contract and nothing in the excluded lookup subsystem says it is not one here.
 *
 * ⚠ IT DOES NOT REFUSE ABSENCE, AND MUST NOT BE MADE TO. Absence and the sentinel are different
 * values needing different sentences — "you have not chosen one" against "the value you chose means
 * unset" — so {@link requiredNumber} is applied alongside it rather than folded into it. Note that
 * this validator previously PASSED `null`, because coercing it produced 0 and 0 is not the sentinel;
 * that is exactly the hole the neighbouring rule closes.
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

/*
 * ⚠ THERE IS DELIBERATELY NO CLIENT-SIDE SYNTAX RULE FOR THE VALIDATION EXPRESSION, AND
 * THE ABSENCE IS THE CORRECTION OF A REAL DEFECT RATHER THAN A GAP.
 *
 * A previous revision declared a validator that compiled the operator's text with the
 * browser's own `new RegExp` and refused the form when the constructor threw. It was wrong
 * because THE TWO ENGINES ARE NOT THE SAME LANGUAGE, and the authority is not this one. The
 * stored expression is compiled and evaluated by .NET — `Application/Services/UserService.cs`
 * builds it with `RegexOptions.NonBacktracking`, falls back to the backtracking engine for
 * the constructs the linear-time one refuses, and bounds every evaluation with a 50 ms
 * timeout — so what matters is whether .NET accepts the pattern, not whether V8 does.
 *
 * The dialects diverge in both directions, and the direction that bit is the one that
 * REFUSES VALID INPUT: `(?i)^abc$` is an ordinary .NET inline-options expression and is a
 * `SyntaxError` in JavaScript ("Invalid group"), so the browser rule rejected a legitimate
 * pattern before any request was made and left the operator with no way to store it. The
 * opposite case exists too — a pattern JavaScript accepts and .NET does not — which is why
 * the browser could not be trusted as a pre-filter even in the permissive direction.
 *
 * What replaces it is not "nothing". The API validates the pattern on the authoritative
 * engine and answers a bad one with a problem document carrying its own sentence, and this
 * screen already surfaces that both ways: `messagesFor` merges the server's per-field
 * messages beneath the control, and `report` announces the summary through the notification
 * service. The length rule STAYS on the client, because a character count is a fact both
 * sides agree on and the API applies the identical 512-character limit.
 *
 * MIGRATION: the legacy screen validated nothing here either. `EditProfileDefinition.ascx`
 * delegated the field to the excluded property-editor control and stored whatever was
 * typed, so a client-side syntax rule was never legacy behaviour being preserved — and the
 * behaviour that IS preserved is that an operator may store any expression the platform
 * will actually run.
 */

// PURE HELPERS

/**
 * Whether a set of staged grid edits differs from what a declaration currently holds.
 *
 * Compared member by member with strict equality, never by serialising the two and comparing the text:
 * `false` and `0` are DATA on these members, and a comparison that went through a truthiness test or a loose
 * equality would treat a legitimate `false` as "unset" and a legitimate `0` as equal to it.
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
 * Every member must be carried, not just the one being changed. The endpoint REPLACES the declaration rather
 * than patching it, so a member omitted from the request is a member cleared in the database. That is why
 * moving a row one place rewrites the whole declaration: there is no narrower verb to use.
 *
 * The nine are named explicitly rather than spread from the row and pruned, so that a change to either
 * contract is a compile error here instead of a rejected request at run time. The three members deliberately
 * absent are the identity and the tenant, which the server decides, and the module association, which only a
 * create may set.
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
 * Three of the four required members have a sentence; the fourth, the view order, cannot fail presence at
 * all because its control is a non-nullable number — so its arm exists only to keep the switch exhaustive
 * and returns the shared numeric sentence.
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
 * Only three members carry a length limit, and each limit is the terminal column width the API also
 * enforces. The default arm is unreachable in practice and returns the name's sentence rather than an empty
 * string, so a future control that gains a limit without gaining a sentence still says something actionable.
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
 * A confirmation is added here for a SINGLE write only, because an inline form that simply closes gives the
 * operator no evidence that anything was written. The BATCH deliberately announces nothing, for the reason
 * given on {@link ProfileDefinitionListComponent.applyChanges}.
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
 * The duplicate-name sentence is the legacy's own, reproduced byte for byte from `DuplicateName.Text`, and
 * it is selected by the server's CODE rather than by the bare status. Recognising it by `409` alone would be
 * the weaker test, because a removal draws the same status for a completely different reason — a declaration
 * that is still in use — and only the ordering of the branches would keep the two apart.
 *
 * The severity is the store's, not this file's. The shared summariser answers `warning` for a refusal of
 * permission and `error` for the rest, which is the legacy's own reading: the access-denied control raised
 * its message as a yellow warning on both branches, while the duplicate-name refusal was raised as
 * `ModuleMessageType.RedError`. Re-deciding it here would let the two disagree.
 *
 * @param pending What was requested.
 * @param failure The failure the store recorded.
 * @returns The message to announce. Never empty.
 */
function refusalMessage(pending: AwaitedWrite, failure: UserFailure): string {
  if (failure.code === DUPLICATE_NAME_CODE) {
    return DUPLICATE_NAME_MESSAGE;
  }

  // A declaration that has already been removed by someone else. Recognised by the code the API publishes
  // AND by the bare status, because a not-found that never reached an action carries no application code at
  // all — the framework writes a specification URI for it, which the shared reader correctly refuses to
  // treat as a failure code.
  if (failure.code === NOT_FOUND_CODE || failure.problem?.status === 404) {
    return DEFINITION_GONE_MESSAGE;
  }

  // A conflict on a REMOVAL is the declaration being in use, which is a different refusal from a duplicate
  // name and is worded as its own thing rather than left to the generic sentence.
  if (pending.kind === 'delete' && failure.problem?.status === 409) {
    return DEFINITION_IN_USE_MESSAGE;
  }

  return stripLegacyBreakTags(failure.summary.message);
}

/**
 * The profile-property catalogue for the tenant: `/settings/profile-definitions`.
 *
 * MIGRATION: this one screen replaces two legacy pages and a three-step wizard. The legacy edit command
 * NAVIGATED to a separate page, but the target route table declares no `/settings/profile-definitions/:id`
 * address, so the editor is INLINE here: pressing Edit opens the form pre-populated and the operator never
 * loses their place in the list. The wizard's second and third steps are DROPPED, not ported - only step one,
 * the property's own details, has a target.
 *
 * `app.routes.ts` loads THIS class directly as a standalone `loadComponent` leaf. A presentational shape
 * taking the catalogue as an input therefore cannot work at this address: nothing would supply the input and
 * nothing would listen to the outputs, so the grid would render permanently empty and every affordance on it
 * would be a no-op that looked like a working button. The screen consequently owns its own arrival - it
 * injects the account store, loads on entry, and turns each affordance into a store command.
 *
 * Signals only. The server's catalogue lives in the store, one copy for the whole application, and this screen
 * holds exactly one thing of its own: a DRAFT of the grid edits the operator has made but not yet applied.
 * {@link rows} merges the two, so the grid always shows either the server's truth or the operator's pending
 * change and never a third thing.
 *
 * MIGRATION: view state is GONE, and it was load-bearing in both legacy pages - one kept the identifier being
 * edited in it and parsed it back out of a string on every read, and the other relied on the fetched
 * collection being a shared cached instance so that its in-place mutations survived to the next postback. The
 * draft signal is the explicit replacement.
 *
 * `POST` creates, `PUT` replaces, `DELETE` removes. There is NO reorder endpoint and NO bulk endpoint, and
 * neither is invented: position is a FIELD, so moving a row is a `PUT`, and a bulk flag change is one `PUT`
 * per affected row. That is not a downgrade from the legacy, which already issued one update call per dirty
 * row.
 */
@Component({
  selector: 'app-profile-definition-list',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    // Backs the inline create-and-edit form.
    ReactiveFormsModule,
    // Page heading plus the projected action bar.
    PageHeaderComponent,
    // The twelve-column grid. It renders its own spinner and empty state, so neither the shared spinner nor
    // the shared empty state is declared here.
    DataTableComponent,
    // Every control of the inline form, and the two bulk toggles.
    FormFieldComponent,
    // The removal confirmation. Its presence in the DOM is what "open" means.
    ConfirmDialogComponent,
    // Surfaces a refused or failed request, as `role="alert"`.
    ErrorBannerComponent,
    // Renders the two boolean columns as announced text in the read-only cells.
    YesNoPipe,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './profile-definition-list.component.html',
  styleUrl: './profile-definition-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfileDefinitionListComponent implements OnInit {
  // COLLABORATORS
  //
  //  `inject()` rather than constructor parameters, matching every sibling screen, and nothing is provided
  //  here: the migration plan wires `provideHttpClient`, the router and the interceptor chain once in
  //  `app.config.ts`, and a component-level provider would give this screen a private copy of whatever it
  //  declared.
  //
  //  The store is the only transport. `user.service.ts` is not injected as well, even though it is what the
  //  store ultimately calls: injecting both would put a second copy of the catalogue in the screen, and the
  //  whole point of reading through the store is that there is exactly one. No `HttpClient` is injected
  //  anywhere in this file.

  private readonly store = inject(UserStore);

  private readonly notifications = inject(NotificationService);

  // Held solely so `afterNextRender` can be reached from outside a constructor. See
  // `restoreFocusAfterRemoval`, which is the only consumer and explains why the deferral is unavoidable
  // there.
  private readonly injector = inject(Injector);

  // CELL TEMPLATES
  //
  //  Static queries, so they are resolved before `ngOnInit` and the column set can be assembled there. Each
  //  `ng-template` belongs to THIS component's view even though `app-data-table` is what renders it, which is
  //  what makes a projected cell template work at all.

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
   * Why a view query on a button that is never read for its value. Deleting a row removes the command button
   * that opened the confirmation dialog, so the dialog's own focus-return has nothing to return to and focus
   * collapses to `body` — measured in a browser, not surmised. This button is outside the grid and is never
   * removed, so it is a valid anchor. The query is STATIC because the button sits outside every control-flow
   * block, so it is resolved before the first change detection and is available whenever a delete settles.
   *
   * It is optional for the same reason every template query here is: a view query cannot be proven non-null
   * by the compiler, and the alternative is a non-null assertion, which is barred. A missing anchor degrades
   * to today's behaviour rather than throwing.
   */
  @ViewChild('createTrigger', { static: true })
  private createTrigger?: ElementRef<HTMLButtonElement>;

  // State owned by this screen

  /**
   * Backing store of {@link columns}; populated once, in `ngOnInit`.
   */
  private readonly columnSet = signal<readonly DataTableColumn<ProfilePropertyDefinition>[]>([]);

  /**
   * The grid edits made but not yet applied, keyed by declaration identifier.
   *
   * MIGRATION: this is the target's `IsDirty`. The entity here is `readonly` and comes from a shared store,
   * so dirtiness cannot live on it; it lives beside it, in a map this screen owns.
   *
   * A `ReadonlyMap` replaced wholesale on every change rather than a mutated `Map`, so that the signal
   * genuinely notifies. Mutating a held map in place would leave every reader looking at the same reference
   * and nothing would recompute.
   */
  private readonly draft = signal<ReadonlyMap<number, GridEdits>>(new Map<number, GridEdits>());

  /**
   * The stable row instance for each declaration, keyed by `propertyDefinitionId`.
   *
   * NOT a signal, and deliberately so: it is an identity cache rather than state. Nothing reads
   * it to decide what to render — {@link ProfileDefinitionListComponent.rows} decides that from
   * the store's catalogue and the staged edits, both of which ARE signals — and making it
   * reactive would put a second, redundant dependency in the graph. It is written only from
   * inside that computed, whose inputs fully determine its contents.
   *
   * See {@link MutableProfileRow} for why the instances must be stable and
   * {@link ProfileDefinitionListComponent.discardRowsNoLongerReported} for how the cache is
   * kept from outliving the records it describes.
   */
  private readonly rowInstances = new Map<number, MutableProfileRow>();

  /** Whether the inline form is on screen. */
  private readonly formOpen = signal(false);

  /**
   * The declaration being replaced, or `null` when the form is creating.
   *
   * Presence, never falsiness. Create and edit are told apart by `=== null`, and never by `if (id)`, `id >
   * 0`, `!id` or `id ?? -1`. A truthiness test would be a real defect on a key column, and although
   * `ProfilePropertyDefinition.PropertyDefinitionID` happens to be `IDENTITY(1, 1)` so zero never occurs,
   * three sibling tables in this schema are seeded `IDENTITY(0, 1)` and the tenant table at -1. The habit,
   * not the luck, is what keeps this correct.
   */
  private readonly editing = signal<number | null>(null);

  /**
   * The declaration awaiting confirmation of removal, or `null` when none is.
   */
  private readonly pendingRemoval = signal<ProfilePropertyDefinition | null>(null);

  /**
   * The single write whose outcome is being awaited, or `null`.
   *
   * Set for a create, a replace from the form, and a removal — the three writes that have an outcome worth
   * reporting. Deliberately NOT set by {@link applyChanges}: see the note there.
   */
  private readonly awaited = signal<AwaitedWrite | null>(null);

  /**
   * The identifier of the Apply batch that is outstanding, or `null` when none is.
   *
   * The store writes the staged rows one at a time behind a single command and publishes ONE settled
   * result for the whole batch, so one identifier is all this screen needs. It is compared against
   * the store's published result so that a write dispatched by another screen — the store is provided
   * at the application root — can never be mistaken for this batch settling.
   */
  private readonly batchWrite = signal<number | null>(null);

  /**
   * How many rows the outstanding Apply batch dispatched.
   *
   * Captured at dispatch because the settled result cannot supply it: the store publishes one result
   * for the whole command and the staged set has already been overtaken by the re-read by the time it
   * arrives. Only {@link applyAnnouncement} reads it, and only to state a number.
   */
  private readonly batchRows = signal<number>(0);

  /**
   * What to announce about the batch that has just settled, or the empty string when there is nothing.
   *
   * ⚠ THIS EXISTS FOR A NON-VISUAL READER AND CHANGES NOTHING ON SCREEN. The legacy announced no
   * success sentence either — `cmdUpdate_Click` L444-L452 called `UpdateProperties` then `RefreshGrid`
   * and set no message label, and `ProfileDefinitions.ascx` declares none to set — so raising a toast
   * here would be a visible divergence, and for a five-row apply a repetitive one. But the legacy's
   * `RefreshGrid` ran inside a full post-back, and a screen reader announces a document load; that
   * signal is what the single-page rewrite removed, and this restores it in the register the legacy
   * used rather than in a louder one.
   *
   * Cleared when the next batch is dispatched, so a stale confirmation cannot be read out over a
   * batch still in flight, and left empty when any row was refused because the refusal is then the
   * announcement.
   */
  private readonly batchApplied = signal<string>('');

  /**
   * Whether a submission has been attempted since the form was opened.
   *
   * MIGRATION: legacy defect corrected. `Wizard_NextButtonClick` guarded the save with `If
   * Properties.IsDirty And Properties.IsValid Then` and had NO `Else`, so an invalid form silently saved
   * nothing AND THE WIZARD STILL ADVANCED — the operator was moved to the next step with their changes
   * discarded and no message anywhere. This flag is what replaces that silence: messages stay hidden until
   * the operator submits, and then every failing rule is named. The Minimal Change Clause protects business
   * logic, not a defect that lost an operator's work.
   */
  private readonly submitAttempted = signal(false);

  // STORE-DERIVED SURFACE
  //
  //  These are the store's own signals under template-facing names. They are NOT copies: assigning the signal
  //  shares it, so nothing here can drift from the store.

  /**
   * Whether the catalogue is still being fetched.
   */
  protected readonly loading: Signal<boolean> = this.store.profileDefinitionsLoading;

  /**
   * Whether a write is in flight. Every affordance that would start another is disabled.
   */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * The failure document the BANNER surfaces, or `null`.
   *
   * Derived rather than stored, so this screen cannot go on showing a message the store has
   * already cleared. Only the DOCUMENT is bound: the shared banner renders its title, its
   * detail and its per-field messages, and binding the store's summary as well would report
   * the same failure twice.
   *
   * ⚠ NARROWED TO THIS SCREEN'S OWN READ FAILURES, AND BOTH HALVES OF THAT NARROWING FIX A REAL
   * DEFECT. This used to be the store's failure slot verbatim, and the store is provided at the
   * application root — so the banner reported failures of operations this screen does not perform
   * and cannot explain: a credential change refused on a different screen, an account listing that
   * failed behind a sibling pane. And a WRITE refusal appeared here AS WELL AS in a queued
   * notification, so one refusal was reported twice in two different registers, once as a standing
   * banner and once as a transient announcement.
   *
   * A write refusal is therefore reported by the notification alone — that is the surface that can
   * name which property was refused, which a shared banner cannot — and the banner is reserved for
   * the read that populates this screen. The per-field messages a write refusal carries are still
   * shown, beside the controls they name, from {@link writeProblem}.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed<ProblemDetails | null>(
    () => {
      const failure: UserFailure | null = this.store.failure();

      if (failure === null || !isCatalogueRead(failure.operation)) {
        return null;
      }

      return failure.problem;
    },
  );

  /**
   * The sentence to show when a READ of this screen's failed without a problem document.
   *
   * ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT. The runtime decoders that check each
   * response against its published contract run inside the service's own mapping, which is DOWNSTREAM
   * of the interceptor's error handling — so a `200` whose body does not match its contract throws a
   * plain error with no document, no status and no support reference. {@link problem} is therefore
   * `null` for it, and this screen rendered NOTHING: the grid stayed empty, every command that needs a
   * declaration stayed unusable, and no surface said why.
   *
   * The store's own authored summary is read out rather than a second sentence being invented here —
   * the shared summariser already words a failure that carries no document. The retry path is the
   * screen's existing Refresh Grid command, which re-dispatches the read and is never disabled by a
   * failed read.
   *
   * Null whenever a document IS present, so the server's own explanation always wins.
   */
  protected readonly readFailureSummary: Signal<string | null> = computed<string | null>(() => {
    const failure: UserFailure | null = this.store.failure();

    if (failure === null || failure.problem !== null || !isCatalogueRead(failure.operation)) {
      return null;
    }

    return failure.summary.message;
  });

  /**
   * The document from the most recent write of THIS screen's that was refused, or `null`.
   *
   * Held rather than derived, because it must survive past the moment the store's shared slot is
   * cleared by whatever is dispatched next — the per-field messages beside the form's controls are
   * read while the operator corrects them, which is exactly when other reads are in flight.
   *
   * Cleared when a write of this screen's succeeds and when the form is opened afresh, so a
   * corrected field cannot go on showing the complaint that the previous attempt provoked.
   */
  private readonly writeProblem = signal<ProblemDetails | null>(null);

  // -------------------------------------------------------------------------
  // THE GRID

  /**
   * The twelve columns, assembled in `ngOnInit` once the cell templates exist.
   */
  protected readonly columns = this.columnSet.asReadonly();

  /**
   * The rows to render: the server's catalogue with the operator's pending edits applied, ordered by
   * position.
   *
   * MIGRATION: the local re-sort is required for parity, not a decoration. `MoveProperty` swapped two
   * positions and then called `profileProperties.Sort()` followed by `BindGrid()` WITHOUT saving, so the row
   * visibly moved immediately and the write happened later, on Apply. Leaving the order to the server would
   * make Move Up appear to do nothing until the batch was committed. The comparer is the legacy one:
   * `ProfilePropertyDefinitionCollection.vb` sorts by `ViewOrder`, and the API orders by the same column, so
   * the two agree.
   *
   * The identifier breaks a tie. It has to: the position column carries no uniqueness constraint, so two
   * declarations may legitimately hold the same position, and without a deterministic tie-break the grid
   * would reshuffle equal rows on every recomputation.
   */
  protected readonly rows = computed<readonly ProfilePropertyDefinition[]>(() => {
    const held: readonly ProfilePropertyDefinition[] = this.store.profileDefinitions();
    const edits: ReadonlyMap<number, GridEdits> = this.draft();
    const projected: MutableProfileRow[] = [];
    const reported = new Set<number>();

    for (const definition of held) {
      reported.add(definition.propertyDefinitionId);
      projected.push(this.reconcileRow(definition, edits.get(definition.propertyDefinitionId)));
    }

    this.discardRowsNoLongerReported(reported);

    // ⚠ A NEW ARRAY EVERY TIME, HOLDING THE SAME ROW INSTANCES, and both halves of that are
    // load-bearing. The array is what the grid's row input reads, and a signal compares by
    // reference — so a new array is what makes the grid re-project every cell and pick up the
    // staged value. The instances inside it are stable, which is what makes the `<tr>` and the
    // control the operator is holding survive that re-projection. Returning the SAME array
    // would leave the grid showing stale text; returning new instances would eject focus.
    return projected.sort(
      (left, right) =>
        left.viewOrder - right.viewOrder ||
        left.propertyDefinitionId - right.propertyDefinitionId,
    );
  });

  /**
   * How many declarations carry an edit that has not been applied.
   *
   * Derived from the difference, not from the size of the staged map. a staged entry that the server has
   * since caught up with counts for nothing, which is what lets a partially refused batch report exactly the
   * work still outstanding. Counting the map instead would keep reporting every row the operator ever
   * touched, including the ones already written.
   */
  protected readonly dirtyCount = computed<number>(() => this.pendingRows().length);

  /**
   * Whether "Apply Changes" has anything to do.
   */
  protected readonly hasPendingChanges = computed<boolean>(() => this.pendingRows().length > 0);

  /**
   * The sentence the screen's polite status region carries.
   *
   * ⚠ THE LIVE REGION IS MOUNTED PERMANENTLY AND ONLY ITS TEXT CHANGES, WHICH IS THE POINT. The
   * staged-edit count used to carry `aria-live` on the visible hint, and that hint is rendered only
   * while something is staged — so the region was CREATED already holding its text, which assistive
   * technology is not obliged to announce, and it was DESTROYED when the batch landed, and the removal
   * of a live region announces nothing at all. Whichever way the operator got there, they heard
   * nothing. A region that is always present and whose text is swapped is the pattern that works.
   *
   * While work is outstanding this reports the count, so staging and un-staging rows is audible. Once
   * the batch lands the count is gone by construction and the settled sentence takes its place.
   */
  protected readonly applyAnnouncement = computed<string>(() =>
    this.hasPendingChanges()
      ? `${this.dirtyCount()} unapplied change(s).`
      : this.batchApplied(),
  );

  /**
   * Whether every declaration is already required, and likewise for visible.
   *
   * The select-all affordance had to move out of the table, and the gap is reported rather than papered
   * over. `app-data-table` renders its headings from the column descriptors and exposes no header-cell
   * template; its inputs are closed at five. Putting a control in a heading would mean adding a sixth input
   * to a component nine other screens share, which this screen may not do on its own. The two toggles
   * therefore sit in their own region above the grid, where they reach the same rows and read the same
   * all-true state.
   *
   * The empty catalogue is the one place this deliberately diverges: seeded true, the legacy expression
   * reported "all required" for a list with no rows at all, and the toggles are simply not rendered when
   * there is nothing to apply them to.
   */
  protected readonly allRequired = computed<boolean>(() =>
    this.rows().every((definition) => definition.required),
  );

  protected readonly allVisible = computed<boolean>(() =>
    this.rows().every((definition) => definition.visible),
  );

  /**
   * Whether the catalogue holds anything, which is what gates the bulk region.
   */
  protected readonly hasRows = computed<boolean>(() => this.rows().length > 0);

  // THE INLINE FORM

  /**
   * The create-and-edit form, in the order `SortOrder` declared the members.
   *
   * Every control is `nonNullable`, which is what makes ONE group safe to reuse across creation and the
   * editing of every row: `reset(value)` returns each control to a real value rather than to `null`, and
   * `getRawValue()` is fully typed rather than a `Partial`.
   *
   * MIGRATION: the rules are the ones the legacy declared as ATTRIBUTES on `ProfilePropertyDefinition.vb`,
   * which the excluded property editor turned into server validators at run time. The view order needs no
   * `Validators.required`: its control is a non-nullable number, so presence is structural, and the API's
   * own validator declines the rule for exactly that reason.
   *
   * `Validators.required` ACCEPTS ZERO, and this screen depends on that. Its emptiness test is `value ==
   * null || ((string | array) && length === 0)`, so a numeric 0 passes. Nothing here hand-rolls
   * required-ness as a truthiness test, which would have refused the first row's legitimate position of 0.
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
    // ⚠ `nonNullable` IS RETAINED ON ALL THREE NUMERIC CONTROLS, AND IT IS NOT THE SAME CLAIM AS
    // THE DECLARED TYPE. It governs what `reset()` returns the control to — the seed rather than
    // `null` — which is what lets ONE form instance serve creation and every row's edit in turn.
    // It does NOT stop a `<input type="number">` writing `null` when its box is cleared, which is
    // exactly what the declared `number | null` now admits and what the absence rules refuse.
    dataType: new FormControl(CREATE_DEFAULTS.dataType, {
      nonNullable: true,
      validators: [
        requiredNumber(DATA_TYPE_REQUIRED_MESSAGE),
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
      validators: [requiredNumber(LENGTH_REQUIRED_MESSAGE), wholeNumber()],
    }),
    defaultValue: new FormControl(CREATE_DEFAULTS.defaultValue, { nonNullable: true }),
    // Length only. The expression's SYNTAX is the server's to judge, on the engine that will
    // actually run it — see the note above the pure helpers, which records why compiling the
    // pattern here refused valid .NET expressions.
    validationExpression: new FormControl(CREATE_DEFAULTS.validationExpression, {
      nonNullable: true,
      validators: [Validators.maxLength(EXPRESSION_MAX_LENGTH)],
    }),
    required: new FormControl(CREATE_DEFAULTS.required, { nonNullable: true }),
    visible: new FormControl(CREATE_DEFAULTS.visible, { nonNullable: true }),
    // ⚠ THE TEMPLATE MARKS THIS FIELD REQUIRED AND `aria-required`, SO A RULE MUST ENFORCE IT.
    // It had none: the field was announced as required to every assistive technology and to every
    // sighted reader by its indicator, and then accepted being left empty — which reached a
    // non-nullable server integer as `null` and drew a `400` naming no field.
    viewOrder: new FormControl(CREATE_DEFAULTS.viewOrder, {
      nonNullable: true,
      validators: [requiredNumber(VIEW_ORDER_REQUIRED_MESSAGE), wholeNumber()],
    }),
  });

  /**
   * Whether the inline form is on screen.
   */
  protected readonly editorOpen = this.formOpen.asReadonly();

  /**
   * Whether the open form is replacing an existing declaration rather than creating one.
   */
  protected readonly isEditMode = computed<boolean>(() => this.editing() !== null);

  /**
   * `Introduction_Add.Title` while creating, `Introduction.Title` while editing.
   */
  protected readonly formHeading = computed<string>(() =>
    this.editing() === null ? CREATE_HEADING : EDIT_HEADING,
  );

  /**
   * `cmdCreate.Text` while creating, `cmdUpdate.Text` while editing.
   */
  protected readonly submitLabel = computed<string>(() =>
    this.editing() === null ? CREATE_SUBMIT_LABEL : EDIT_SUBMIT_LABEL,
  );

  /**
   * Whether the messages beneath the controls are shown yet.
   */
  protected readonly showMessages = this.submitAttempted.asReadonly();

  /**
   * The form's own event stream, as a signal, so a derivation can depend on the form.
   *
   * ⚠ A BRIDGE, NOT STATE. A reactive form is not a signal: its validity changes as the operator
   * types, and nothing about that is observable to `computed()`. Every event the group publishes —
   * a value change, a status change, a control being touched, a reset — arrives here as a new
   * object, which is what invalidates {@link fieldMessages} at exactly the moments its answer can
   * have changed. The event itself is never read; only its arrival matters.
   *
   * `form.events` covers all four kinds in one subscription, and the bridge is torn down with the
   * component because it is created in an injection context.
   */
  private readonly formEvent = toSignal(this.form.events, { initialValue: null });

  /**
   * Every control's messages, computed ONCE per change and read as a map.
   *
   * ⚠ MEMOISED BECAUSE THE TEMPLATE IS A HOT PATH. Each field binds its messages twice — to the
   * shared field's `error` input and to the control's own `aria-invalid` — so nine controls make
   * fourteen reads per change-detection pass on this screen alone. Computing the list inside the
   * accessor allocated two arrays and re-normalised every server message on each of those reads,
   * which turned typing in one field into repeated work for all nine. The derivation now depends on
   * exactly three things — the form's events, whether a submission has been attempted, and the
   * problem document — so it recomputes when one of them changes and not once per binding.
   *
   * The ORDER within each list is unchanged: this screen's own rules first, in rule order, then
   * whatever the server reported against the same member. Nothing is shown before a submission has
   * been attempted, so a form opened afresh is not covered in complaints about fields the operator
   * has not reached yet — but a server message IS shown, because a refusal is about a submission
   * that has already happened.
   */
  protected readonly fieldMessages: Signal<ProfileDefinitionFieldMessages> =
    computed<ProfileDefinitionFieldMessages>(() => {
      // Read for its dependency alone: the form is mutable and imperative, and this is what ties
      // this derivation to it. Without the read, a message list would go stale the moment the
      // operator corrected a field.
      this.formEvent();

      const attempted: boolean = this.submitAttempted();

      // ⚠ THE WRITE'S OWN PROBLEM DOCUMENT, NOT THE BANNER'S. {@link problem} is scoped to a
      // CATALOGUE READ failure, so reading it here would never surface a field error at all: a refused
      // write's per-field sentences live on the result that write published, and the banner is
      // deliberately reserved for the read that populates the grid.
      const problem: ProblemDetails | null = this.writeProblem();

      const messagesFor = (control: keyof ProfileDefinitionFormModel): readonly string[] => {
        const fromServer: readonly string[] = fieldErrorMessages(problem, control).map((message) =>
          stripLegacyBreakTags(message),
        );

        return attempted ? [...this.clientMessages(control), ...fromServer] : fromServer;
      };

      return {
        propertyName: messagesFor('propertyName'),
        dataType: messagesFor('dataType'),
        propertyCategory: messagesFor('propertyCategory'),
        length: messagesFor('length'),
        defaultValue: messagesFor('defaultValue'),
        validationExpression: messagesFor('validationExpression'),
        required: messagesFor('required'),
        visible: messagesFor('visible'),
        viewOrder: messagesFor('viewOrder'),
      };
    });

  // -------------------------------------------------------------------------
  // REMOVAL

  /**
   * The declaration awaiting confirmation, or `null`. Its presence opens the dialog.
   */
  protected readonly removalTarget = this.pendingRemoval.asReadonly();

  // Wording exposed to the template
  //
  //  Bound rather than written inline so that a specification can assert the rendered text against the same
  //  constant the template renders, instead of restating it and letting the two drift apart.

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

  /**
   * Composes the accessible name of one of the two state check boxes in a grid row.
   *
   * ⚠ THE COLUMN WORD IS THE WHOLE POINT, AND ITS ABSENCE WAS A REAL DEFECT. Each cell used to
   * name its box with the row's property name ALONE, so the two boxes in a row both announced
   * "City" - and a reader using assistive technology could not tell which of them made the property
   * required and which made it visible. Measured across two rows, all four boxes reduced to two
   * names. The row name still has to be there for the mirror-image reason: "Required" alone in a
   * grid of many rows does not say WHICH property it is about.
   *
   * The column word is the grid's own heading rather than an authored synonym, so the name a reader
   * hears is the word they can see at the top of the column. The separator is an em dash, matching
   * every other composed accessible name in this application.
   *
   * @param heading The column's heading, exactly as the grid renders it.
   * @param propertyName The row's property name.
   * @returns The composed name, for a visually hidden span inside the box's label.
   */
  protected stateCheckboxName(heading: string, propertyName: string): string {
    return `${heading} — ${propertyName}`;
  }

  /** The Required column's heading, for composing a check box's accessible name. */
  protected readonly requiredHeading = REQUIRED_HEADING;

  /** The Visible column's heading, for composing a check box's accessible name. */
  protected readonly visibleHeading = VISIBLE_HEADING;
  protected readonly gridCaption = GRID_CAPTION;
  protected readonly allRequiredLabel = ALL_REQUIRED_LABEL;
  protected readonly allVisibleLabel = ALL_VISIBLE_LABEL;

  protected readonly removalMessage = DELETE_CONFIRM_MESSAGE;
  protected readonly fieldText = FIELD_TEXT;

  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them.
   *
   * ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's own fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch of the same page presents entirely new keys and the
   * whole body is rebuilt to display records that never changed. `propertyDefinitionId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly definitionRowKey = (row: ProfilePropertyDefinition): number => row.propertyDefinitionId;

  // LIFECYCLE

  constructor() {
    // An effect, because reporting an outcome and closing a form are genuine side effects and that is the
    // only kind of work an effect is for. Created in the injection context, so it is destroyed with the
    // component and there is nothing to unsubscribe.
    //
    // ⚠ IT SETTLES ON THE STORE'S PUBLISHED RESULT, BY IDENTIFIER, AND NOT ON ITS AGGREGATE FLAG
    // FALLING. The store is provided at the application root, so the flag falls when the FIRST write
    // anywhere in the application finishes: watching it let an unrelated save on another screen close
    // this screen's form and announce an outcome for a write that was still in the air. And the
    // failure is taken from the RESULT rather than from the store's shared slot, which every dispatch
    // clears and which therefore holds whatever failed most recently — possibly another screen's
    // refusal, possibly nothing at all by the time this ran.
    //
    // Two registers are settled here, in order: the single write the form or the removal dialogue
    // dispatched, and the Apply batch. A settled result can belong to at most one of them, because
    // every identifier comes from one counter in the store.
    effect(() => {
      const settled: UserMutation | null = this.store.mutation();

      if (settled === null) {
        return;
      }

      const pending: AwaitedWrite | null = this.awaited();
      const batched: number | null = this.batchWrite();

      // `untracked`, because the reports write signals this effect reads. Without it the
      // clearing of `awaited` would schedule the effect again.
      untracked(() => {
        if (pending !== null && pending.id === settled.id) {
          this.awaited.set(null);
          this.report(pending, settled.failure);
          return;
        }

        if (batched !== null && batched === settled.id) {
          this.batchWrite.set(null);
          this.reportBatchOutcome(settled);
        }
      });
    });
  }

  /**
   * Assembles the columns and reads the catalogue.
   *
   * The cell templates are captured by STATIC view queries, so they are resolved by the time this runs and
   * the column set can be built here. Data is loaded from a lifecycle hook rather than from an effect: an
   * effect that fetched would fire again on every unrelated signal it happened to read.
   *
   * MIGRATION: this is `Page_Load` minus its postback branch. The legacy split its behaviour on
   * `Page.IsPostBack` — bind on the first render, `ProcessPostBack` on every subsequent one — because the
   * grid's edits arrived back as form fields that had to be read out of the control tree.
   *
   * No tenant argument is passed, and that is not an omission. The API resolves the tenant from the request
   * itself, so `GET /api/v1/profile-definitions` carries no query parameter at all. The legacy
   * `UsersPortalId` substituted the null-integer sentinel when the page sat under the host tab, which
   * selected the SUPER-USER property set; host administration is out of scope for this migration, so that
   * branch is dropped rather than translated.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
    this.store.loadProfileDefinitions();
  }

  // GRID READ HELPERS

  /**
   * Whether a declaration may be removed.
   *
   * MIGRATION: `grdProfileProperties_ItemDataBound` reached into the delete column and set
   * `delImage.Visible = False` for four names, matched on `PropertyName.ToLower`. The comparison here is the
   * same one, and the command is HIDDEN rather than disabled — a disabled control still announces itself as
   * an action that cannot be taken, whereas the legacy offered no action at all.
   *
   * @param definition The row being rendered.
   * @returns False for the four platform properties, true for everything else.
   */
  protected canDelete(definition: ProfilePropertyDefinition): boolean {
    return !UNDELETABLE_PROPERTY_NAMES.includes(definition.propertyName.toLowerCase());
  }

  /**
   * The mark painted in the data-type cell.
   *
   * Every row paints it, because no row's type can be named: see {@link UNNAMED_DATA_TYPE_MARK} for the
   * three independent reasons the `Lists` vocabulary is unreachable, and for why a mark is painted rather
   * than the blank a strictly literal reading of `Null.NullString` would give.
   *
   * MIGRATION: the legacy template cast `CType(Container.DataItem, ProfilePropertyDefinition)` under Option
   * Strict OFF — a late-bound cast of an untyped data item. This is that cast made explicit: the row arrives
   * typed, so there is nothing to coerce.
   */
  protected readonly unnamedDataTypeMark = UNNAMED_DATA_TYPE_MARK;

  /**
   * The text painted in the data-type cell for one row.
   *
   * Two shapes, because the column carries two different facts: a stored type whose NAME cannot be read
   * paints its reference behind {@link DATA_TYPE_REFERENCE_PREFIX}, and the `Null.NullInteger` sentinel -
   * "no type chosen yet" - paints {@link UNNAMED_DATA_TYPE_MARK}. Both are accompanied by the sentence
   * {@link ProfileDefinitionListComponent.dataTypeDescription} generates, so the visible text and the
   * announced text describe the same row by construction rather than by two authors agreeing.
   *
   * @param definition The row being rendered.
   * @returns The reference to paint, or the absent-value mark when nothing is stored.
   */
  protected dataTypeMark(definition: ProfilePropertyDefinition): string {
    return definition.dataType === NULL_INTEGER
      ? UNNAMED_DATA_TYPE_MARK
      : `${DATA_TYPE_REFERENCE_PREFIX}${definition.dataType}`;
  }

  /**
   * The words behind {@link UNNAMED_DATA_TYPE_MARK} for one row.
   *
   * Used for BOTH the clipped span and the cell's `title`, so a sighted reader hovering and a screen
   * reader announcing receive the same sentence by construction. Phase 9 of this remediation found two
   * renderings of one icon rule that had drifted into seventeen differing computed properties; generating
   * both surfaces from a single accessor is the cheap way not to repeat that.
   *
   * @param definition The row being described.
   * @returns Wording naming the stored reference, or naming the absence when nothing has been chosen.
   */
  protected dataTypeDescription(definition: ProfilePropertyDefinition): string {
    return definition.dataType === NULL_INTEGER
      ? NO_DATA_TYPE_CHOSEN_DESCRIPTION
      : `${UNNAMEABLE_DATA_TYPE_PREFIX}${definition.dataType}${UNNAMEABLE_DATA_TYPE_SUFFIX}`;
  }

  /**
   * The text of a nullable string cell.
   *
   * NEVER RENDERS "null" OR "undefined". `Null.NullString` is the empty string, so an unset default value or
   * expression arrives as `""` rather than as `null` — but the contract admits `null` as well, and
   * interpolating it directly would paint the word.
   *
   * @param value The member as the API reported it.
   * @returns The value, or the empty string when there is none.
   */
  protected cellText(value: string | null): string {
    return value ?? '';
  }

  // Grid edits — staged locally, written on apply

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
   * MIGRATION: `ItemCheckedChanged` branched on `e.IsAll` and wrote the value to every property in
   * the collection, deferring the writes to Apply exactly as this does. Rows that already hold the value are
   * skipped, which is not merely an optimisation: staging them would enable Apply and then spend a request
   * to change nothing.
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
   * Applies every staged edit as ONE BATCH: one write per changed declaration, in turn.
   *
   * MIGRATION: THIS IS A TRANSPORT CHANGE ONLY, not a behavioural one. `UpdateProperties`
   * L291-L298 walked the collection and called `UpdatePropertyDefinition` for each row whose
   * dirty flag was up — one call per dirty row, in order, inside one post-back — and the Apply
   * handler then rebound the grid exactly once (L446-L448). There is no bulk endpoint and none is
   * invented; what the store's batch command adds is the SEQUENCING and the single re-read that
   * a synchronous `For Each` gave the legacy for free.
   *
   * ⚠ ONE COMMAND, NOT N. This screen used to issue the per-row command once per staged row,
   * which started N concurrent writes and, because each of those refreshed the whole catalogue on
   * its own completion, up to N full catalogue reads — while the store's shared saving flag fell
   * on the first write to land, leaving Apply pressable again on top of a batch still running.
   * The bound belongs in the store, because that is where the writes are issued and where the
   * catalogue is read; see `UserStore.applyProfileDefinitionEdits`.
   *
   * ⚠ THE WRITES ARE INDEPENDENT AND THE BATCH IS NOT ATOMIC. They address different
   * declarations, so the server applies each on its own merits, and a refusal of one leaves
   * the others applied. That is reported honestly rather than hidden: no "everything was
   * saved" message is announced — the legacy announced nothing here either — and the pending set
   * is DERIVED from the difference between the staged values and the server's, so the rows that
   * landed drop out of it while the rows that did not stay in it and can simply be applied again.
   * A refusal part-way through does NOT stop the batch: the remaining rows are unrelated
   * declarations that the operator asked to change, and abandoning them because a different row
   * was refused would silently discard work.
   *
   * ⚠ WHICH ROW WAS REFUSED IS CARRIED BY THE PENDING SET, NOT BY A MESSAGE PER ROW, and that is
   * a deliberate consequence of the batch being ONE store command. The store holds the FIRST
   * refusal and publishes it as the batch's single outcome, so exactly one sentence is announced
   * however many rows were refused — announcing one per row would mean re-reading a shared failure
   * slot that every dispatch clears, which is the defect the single settled result exists to
   * remove. Attribution survives where the operator actually needs it: a refused row's staged edit
   * still differs from the server's value, so it stays in {@link pendingRows}, stays visible in the
   * grid as an unapplied change, and can be applied again on its own.
   *
   * The staged edits are deliberately NOT cleared here. Clearing them would make the grid
   * snap back to the server's old values for as long as the writes were in flight, and the
   * derivation above means a stale entry that now matches the server counts for nothing.
   *
   * ⚠ THE BATCH IS OWNED FROM DISPATCH UNTIL EVERY WRITE IN IT HAS SETTLED, AND NOTHING ELSE MAY
   * BE DISPATCHED INSIDE IT. That is what the guard below asserts, and it is the second half of
   * the concurrency fix; the first half is the store counting its outstanding writes rather than
   * holding one boolean. Without the count, the first response of a batch reported the store idle
   * while the rest were still on the wire, which re-enabled every control on this screen and let a
   * single write — a create, a replace from the form, a removal — be issued into the middle of the
   * batch. That write's outcome is reported by the effect in the constructor on the condition
   * "something is awaited and nothing is saving", so the batch's NEXT response settled it:
   * announced as a success before its own request had answered, form closed, and the record of
   * what was awaited discarded, leaving the eventual refusal with nothing to attribute it to.
   *
   * The guard is therefore not defensive tidiness — it is the invariant the report depends on,
   * stated where a reader of this method can see it. It is also deliberately NOT the only
   * mechanism: every control that can start a write on this screen is disabled while
   * `store.saving()` is true, so with the count in place the template already prevents this. A
   * template binding is the wrong single place for a correctness invariant, because a future
   * markup change would remove it silently, so the rule is stated in code as well.
   *
   * ⚠ THE WRITES ARE SEQUENTIAL, AND THAT IS THE OTHER HALF OF THE FIX RATHER THAN A COST OF IT.
   * The store concatenates them — `UserStore.applyProfileDefinitionEdits` maps each edit through
   * `concatMap`, so the next request is not even composed until the previous one has settled and a
   * batch of any length is one request in flight. Dispatching them all at once is the alternative
   * this race admits and it was rejected on two independent grounds. The store publishes ONE
   * settled result at a time, so two rows answered in the same turn would coalesce and the earlier
   * outcome would never reach the reporting effect at all — the identifier would be right and the
   * effect would simply never be handed it. And every row of a reorder writes the SAME display
   * position column, so simultaneous writes renumber against each other and the order the operator
   * sees afterwards depends on which request the server happened to finish last.
   *
   * Serialising costs round trips and nothing else: it is still one call per dirty row, which is
   * exactly what `UpdateProperties` L291-L298 issued from a single post-back, and the legacy loop
   * was itself strictly sequential because it was synchronous. So this is the legacy transport
   * pattern reproduced rather than departed from.
   *
   * NO NOTIFICATION IS RAISED WHEN THE BATCH WHOLLY SUCCEEDS, exactly as the legacy behaved — the
   * unapplied-change count falls by itself, because it is derived. A success IS stated in the
   * screen's polite status region, which is invisible and which restores the signal the legacy's
   * post-back carried for a non-visual reader; {@link batchApplied} sets out the whole argument.
   * A batch that carried a refusal is announced ONCE, by {@link reportBatchOutcome}, through the
   * notification service and
   * deliberately NOT through the shared banner as well: the banner on this screen is reserved for
   * the read that populates it, so reporting a write in both places said the same thing twice and
   * left the less useful of the two standing on screen afterwards.
   */
  protected applyChanges(): void {
    const edits: readonly ProfileDefinitionEdit[] = this.pendingRows().map((definition) => ({
      propertyDefinitionId: definition.propertyDefinitionId,
      request: toUpdateRequest(definition),
    }));

    // A confirmation of the PREVIOUS batch must not still be readable while this one is in flight.
    this.batchApplied.set('');
    this.batchRows.set(edits.length);

    // ⚠ THE IDENTIFIER IS KEPT, NOT DISCARDED. The store is provided at the application root, so a
    // settled result only belongs to this batch when its identifier matches; zero comes back when
    // nothing was dispatched, and zero matches no result.
    this.batchWrite.set(this.store.applyProfileDefinitionEdits(edits));
  }

  /**
   * Discards every staged edit and re-reads the catalogue.
   *
   * Refresh was therefore the legacy's REVERT affordance — it threw away every uncommitted grid edit — and
   * that is preserved. The wording is the legacy's own: `cmdRefresh.Text` is "Refresh Grid", not "Refresh".
   */
  protected refresh(): void {
    this.draft.set(new Map<number, GridEdits>());
    this.store.loadProfileDefinitions();
  }

  // THE INLINE FORM

  /**
   * Opens the form to declare a new property.
   *
   * MIGRATION: the legacy equivalent was the module action `AddContent.Action` ("Add New Profile Property"),
   * which navigated to `EditProfileProperty` — a whole page away. The form opens in place instead.
   */
  protected openCreate(): void {
    this.editing.set(null);
    this.submitAttempted.set(false);
    this.form.reset(CREATE_DEFAULTS);
    this.formOpen.set(true);
    // A form opened afresh carries no complaint from a previous attempt.
    this.writeProblem.set(null);
    this.store.clearFailure();
  }

  /**
   * Opens the form to replace an existing declaration.
   *
   * MIGRATION: `Page_Init` built `EditUrl("PropertyDefinitionID", "KEYFIELD", "EditProfileProperty")` and
   * set the command column's `EditMode="URL"`, so Edit was a NAVIGATION to a second page carrying the
   * identifier in the query string. The target route table declares no such address, so Edit fills this form
   * instead. The identifier is held in a signal rather than in view state, and it never reaches a URL.
   *
   * The row passed in is the MERGED row, so a position the operator has already changed in the grid is what
   * the form shows — the grid and the form cannot disagree about the same declaration.
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
    // A form opened afresh carries no complaint from a previous attempt.
    this.writeProblem.set(null);
    this.store.clearFailure();
  }

  /**
   * Closes the form without writing anything.
   *
   * MIGRATION: it does NOT navigate, even though the label says so. The wording is the legacy's own —
   * `cmdCancel.Text` is "Return to Profile Properties List" — and it stays accurate: the reader is returned
   * to the list.
   */
  protected cancelForm(): void {
    this.formOpen.set(false);
    this.editing.set(null);
    this.submitAttempted.set(false);
    this.form.reset(CREATE_DEFAULTS);
    // Abandoning the form abandons the complaints it drew, so re-opening it starts clean.
    this.writeProblem.set(null);
  }

  /**
   * Writes the form.
   *
   * MIGRATION: `Wizard_NextButtonClick` saved on "Next" from step zero, guarded by `If Properties.IsDirty
   * And Properties.IsValid Then`, and chose between add and update by comparing the identifier against
   * `Null.NullInteger`. Three things change deliberately:
   *
   * * The dirty half of that guard is dropped. Submitting an unchanged form here issues a replace that
   *   writes the same values, which is harmless; the legacy's dirty flag existed to spare a round trip in a
   *   page that round-tripped for everything anyway.
   * * An INVALID form now reports why. The legacy silently did nothing and advanced the wizard regardless,
   *   losing the operator's work with no message — see {@link submitAttempted}.
   * * A FAILED CREATE DOES NOT SET THE EDIT IDENTIFIER. Here {@link editing} is written only by {@link
   *   openEdit}, so a refused create leaves the form in create mode and a second attempt is still a create.
   *
   * `getRawValue()`, NOT `value`. A disabled control is EXCLUDED from `FormGroup.value`, so reading `value`
   * would silently drop any member this screen ever chooses to disable. The read-only members are currently
   * expressed with the `readonly` ATTRIBUTE, which keeps them in `value` — but reading the raw value means
   * that remains true if that ever changes.
   */
  protected submitForm(): void {
    this.submitAttempted.set(true);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // ⚠ THE TWO KEYED STRINGS ARE TRIMMED INTO THEIR CONTROLS AND THE FORM IS THEN RE-JUDGED,
    // rather than trimmed on the way into the request. The order is the whole point.
    //
    // Trimming into the request meant the value that was VALIDATED and the value that was SENT
    // were different strings, and `propertyCategory` is where that bites: its rules are
    // `required` and `maxLength` alone, so a whitespace-only entry is a non-empty string that
    // satisfies both — and then trimmed to the empty string on its way out. The request went to
    // an endpoint whose `NotEmpty` rule treats a whitespace-only string as empty, so the server
    // refused what the screen had just declared valid, and the operator was shown a server
    // rejection for a field the form had raised no complaint about.
    //
    // `propertyName` is not reachable by that path — its pattern is anchored and admits no
    // whitespace at all, so an entry needing a trim has already been refused by the first gate —
    // but it is normalised here too, because "what is judged is what is sent" is easier to keep
    // true as a rule than as an exception, and a later relaxation of the pattern would otherwise
    // reopen the hole silently.
    //
    // The two free-text members are deliberately NOT touched: a default value or a validation
    // expression may legitimately begin or end with a space.
    this.normaliseKeyedStrings();

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    const raw: ProfileDefinitionFormValue = this.form.getRawValue();
    const editingId: number | null = this.editing();

    // ⚠ NARROWED BEFORE THE REQUEST IS ASSEMBLED, NOT AT THE BOUNDARY AND NOT BY A CAST. The three
    // numeric controls can hold `null`, and the request contract declares all three as non-nullable
    // integers, so the absence has to be removed here or it reaches the server. The validators above
    // already refuse it, so this narrowing is unreachable in practice — and it is written all the
    // same, because "unreachable" rests on a validator staying attached, and a compiler-enforced
    // narrowing does not. Returning without dispatching is the safe branch: the form is marked
    // touched, so the sentences the rules produced are already on screen beside their fields.
    if (raw.dataType === null || raw.length === null || raw.viewOrder === null) {
      this.form.markAllAsTouched();
      return;
    }

    const value: NarrowedProfileDefinitionFormValue = {
      ...raw,
      dataType: raw.dataType,
      length: raw.length,
      viewOrder: raw.viewOrder,
    };

    // The two keyed strings are trimmed and the two free-text members are not. The API refuses
    // a blank name or category outright — its `NotEmpty` rule treats a whitespace-only string
    // as empty — and the name pattern forbids an internal space anyway, so trimming only
    // removes what would be refused. A default value or an expression, by contrast, may
    // legitimately begin or end with a space, so neither is touched.
    const members: UpdateProfilePropertyDefinitionRequest = {
      propertyName: value.propertyName,
      propertyCategory: value.propertyCategory,
      dataType: value.dataType,
      // The empty string is sent, not `null`. `Null.NullString` IS the empty string, and the API serialises
      // with `DefaultIgnoreCondition = Never`, so the sentinel survives the round trip exactly as the legacy
      // stored it. Substituting `null` would change the stored value for every property the operator merely
      // re-saved.
      defaultValue: value.defaultValue,
      length: value.length,
      required: value.required,
      validationExpression: value.validationExpression,
      viewOrder: value.viewOrder,
      visible: value.visible,
    };

    if (editingId === null) {
      // `moduleDefId` is `null` because only a create may set it, the legacy editor never
      // offered it — the member is `Browsable(False)` at `ProfilePropertyDefinition.vb:L159` —
      // and there is nothing on this screen to fill it in from.
      const request: CreateProfilePropertyDefinitionRequest = { ...members, moduleDefId: null };

      // The marker is set FROM the dispatch rather than before it, because the identifier it must
      // carry is what the dispatch returns. The command dispatches synchronously and cannot settle
      // within its own call, so no answer can arrive before the marker exists.
      this.awaited.set({ id: this.store.createProfileDefinition(request), kind: 'create' });
      return;
    }

    this.awaited.set({
      id: this.store.updateProfileDefinition(editingId, members),
      kind: 'update',
      propertyDefinitionId: editingId,
    });
  }

  /**
   * Trims the two keyed strings into their own controls.
   *
   * `emitEvent: false` because this is a DISPLAY CORRECTION rather than an operator edit: it must
   * not be able to start a cascade through any listener on this form. `setValue` re-runs the
   * control's validators regardless of that flag, which is what lets the caller re-read validity
   * immediately afterwards and find it reflecting the normalised value.
   *
   * A control whose value is already trimmed is left completely alone, so an unnecessary write
   * cannot mark a pristine form dirty.
   */
  private normaliseKeyedStrings(): void {
    for (const control of [this.form.controls.propertyName, this.form.controls.propertyCategory]) {
      const trimmed: string = control.value.trim();

      if (trimmed === control.value) {
        continue;
      }

      control.setValue(trimmed, { emitEvent: false });
    }
  }

  /**
   * The messages to show beneath one control.
   *
   * Two sources, merged: this screen's own rules, and whatever the server reported against the same member.
   * The server's are read through the shared reader, which lower-cases the first character of each
   * model-state key so that it matches the control name, and which reads its per-field dictionary with
   * BRACKET access — property access on an index-signature type is a compilation error in this workspace,
   * deliberately.
   *
   * Nothing is shown until a submission has been attempted, so a form opened afresh is not covered in
   * complaints about fields the operator has not reached yet.
   *
   * ⚠ A LOOKUP, NOT A COMPUTATION. The list itself is derived once per change by
   * {@link fieldMessages}; this reads the entry for one control out of that map, so a template
   * binding costs a property read rather than two array allocations and a re-normalisation of every
   * server message. It is retained as a method because a specification and a template both address
   * one field at a time.
   *
   * @param control The control's name, which is also the member name the server reports.
   * @returns The messages, in rule order then server order. Empty when there are none.
   */
  protected messagesFor(control: keyof ProfileDefinitionFormModel): readonly string[] {
    return this.fieldMessages()[control];
  }

  // REMOVAL

  /**
   * Asks for confirmation before removing a declaration.
   *
   * MIGRATION: `DeleteProperty` removed immediately and then refreshed — removal was never part of the batch
   * — and the only guard was a browser `confirm` whose text came from
   * `Localization.GetString("DeleteItem")`, i.e. `SharedResources.resx`. That text is reproduced verbatim in
   * the shared dialog, which adds a focus trap and `Escape` handling a browser `confirm` never had.
   *
   * @param definition The declaration to remove.
   */
  protected requestRemoval(definition: ProfilePropertyDefinition): void {
    this.pendingRemoval.set(definition);
  }

  /**
   * Abandons a removal.
   */
  protected cancelRemoval(): void {
    this.pendingRemoval.set(null);
  }

  /**
   * Removes the confirmed declaration.
   *
   * The guard is a presence test on the awaited target, not a truthiness test, and it exists because the
   * dialog's confirm output carries no payload.
   */
  protected confirmRemoval(): void {
    const target: ProfilePropertyDefinition | null = this.pendingRemoval();

    if (target === null) {
      return;
    }

    this.pendingRemoval.set(null);
    this.awaited.set({
      id: this.store.deleteProfileDefinition(target.propertyDefinitionId),
      kind: 'delete',
      propertyName: target.propertyName,
    });
  }

  // Private — derived state

  /**
   * The declarations whose staged edits genuinely differ from what the server holds.
   *
   * This is derived, not orchestrated, and that is what makes a partial failure recoverable. {@link
   * applyChanges} never clears the staged edits; instead a staged edit stops counting the moment the server
   * reports the same value. So after a batch in which some writes landed and one was refused, the rows that
   * landed silently drop out of this set and the row that did not remains in it — Apply stays enabled for
   * exactly the work that is left, and the banner says why it was refused. An imperative "clear the draft on
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

  // PRIVATE — HELPERS

  /**
   * Returns this declaration's STABLE row instance, refreshed to what should be displayed.
   *
   * ⚠ THE INSTANCE IS REUSED ACROSS RECOMPUTATIONS, and that is the point rather than a
   * saving. See {@link MutableProfileRow}: the shared table tracks rows by object reference,
   * so handing it a new object for a row whose values changed destroys that row's `<tr>` and
   * every control in it — including the checkbox or reordering button the operator had just
   * pressed, which is how focus was being ejected on every staged edit and on every move.
   *
   * Identity is keyed by `propertyDefinitionId`, which is the only member that identifies a
   * declaration and the only one that never changes. Keying by position would defeat the
   * purpose exactly when it matters most: a Move Up swaps two positions, so a
   * position-keyed identity would hand each of the two rows the OTHER row's instance and the
   * reader's focus would follow the position rather than the record they moved.
   *
   * ⚠ SENTINEL DISCIPLINE. The key is used exactly as received and is never tested for
   * truthiness or against minus one. `ProfilePropertyDefinition.PropertyDefinitionID` is an
   * identity column, and in this schema minus one is simultaneously a legitimate key and the
   * legacy marker for a missing integer, so a truthiness test is wrong somewhere.
   *
   * ⚠ THE SERVER'S TRUTH IS COPIED WHOLESALE, then the staged edits are overlaid on top. Doing
   * it in that order is what makes a re-read authoritative: a value the server changed appears
   * even on a row that carries an unrelated staged edit, and the three staged members are the
   * only ones the operator can have touched. The copy uses a whole-object assignment rather
   * than thirteen named ones deliberately — a member added to the contract is then carried
   * automatically, where a hand-written list would silently leave the new member frozen at
   * whatever it held when the instance was created.
   *
   * @param definition The declaration as the server reported it.
   * @param staged The operator's pending values for it, or undefined when it has none.
   * @returns The stable row instance for this declaration.
   */
  private reconcileRow(
    definition: ProfilePropertyDefinition,
    staged: GridEdits | undefined,
  ): MutableProfileRow {
    const key: number = definition.propertyDefinitionId;
    const existing: MutableProfileRow | undefined = this.rowInstances.get(key);
    const row: MutableProfileRow = existing ?? { ...definition };

    if (existing === undefined) {
      this.rowInstances.set(key, row);
    }

    Object.assign(row, definition);

    if (staged !== undefined) {
      row.required = staged.required;
      row.visible = staged.visible;
      row.viewOrder = staged.viewOrder;
    }

    return row;
  }

  /**
   * Forgets the row instances for declarations the server no longer reports.
   *
   * ⚠ WITHOUT THIS THE CACHE IS A LEAK AND A CORRECTNESS HAZARD, in that order of visibility
   * but the reverse order of importance. A deleted declaration's instance would be retained
   * for the lifetime of the screen; worse, were a later read to report a DIFFERENT declaration
   * under a recycled identifier, the stale instance would be adopted and its members refreshed
   * from the new record — which is harmless in outcome but leaves the reader's focus attached
   * to a row that now describes something else entirely.
   *
   * @param reported The identifiers the most recent read carried.
   */
  private discardRowsNoLongerReported(reported: ReadonlySet<number>): void {
    for (const key of Array.from(this.rowInstances.keys())) {
      if (reported.has(key) === false) {
        this.rowInstances.delete(key);
      }
    }
  }

  /**
   * Overlays a declaration's staged edits, if it has any.
   *
   * A NEW object is returned rather than the held one mutated, because the contract's members are `readonly`
   * and the held instance belongs to a store several screens read.
   *
   * ⚠ THIS IS NOT THE GRID'S PROJECTION and must not be conflated with it. It serves
   * {@link ProfileDefinitionListComponent.pendingRows}, which composes the batch WRITE
   * payload, so a fresh detached object is exactly right there: the payload must not alias a
   * row instance the grid is rendering and the reader may still be editing.
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
   * The map is REPLACED rather than mutated, so the signal genuinely notifies; mutating the held map in
   * place would leave every reader on the same reference and nothing would recompute.
   *
   * An entry whose values match what the server already holds is REMOVED rather than kept. MIGRATION: the
   * legacy dirty flag was one-way — `If _Required <> Value Then _IsDirty = True` never cleared — so toggling
   * a box on and then off left the row scheduled for a write that would change nothing. Pruning it instead
   * has an identical net effect on the database and keeps Apply honest about whether there is anything to
   * apply.
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
   * MIGRATION: this is `MoveProperty` exactly — read the neighbour's position, SWAP the two, re-sort,
   * and DO NOT SAVE. One move therefore stages TWO rows and Apply issues TWO writes, which is why there is
   * no reorder endpoint to call and none is invented: position is a field on the declaration, so moving a
   * row is a replace of two declarations.
   *
   * Equal positions are a caveat inherited, not introduced. The position column carries no uniqueness
   * constraint, so two declarations may hold the same position, and exchanging equal positions changes
   * nothing — the row does not appear to move. The legacy had the identical outcome for the identical
   * reason. Renumbering the whole set instead would rewrite rows the operator never touched, so the faithful
   * behaviour is kept and recorded.
   *
   * A move off either end is DISCARDED rather than clamped: there is no neighbour to exchange with, and
   * inventing a position would move a row the operator did not name. The rendered command is hidden at the
   * ends anyway, so this guard is the backstop rather than the rule.
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

    // The negative index is tested BEFORE `at` is called: `at(-1)` returns the LAST element, so moving the
    // first row up would otherwise exchange it with the last one.
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
   * Order is rule order, and presence comes first so that an omitted name reports "required" alone rather
   * than stacking a format complaint on top of it. That is deliberate parity: an ASP.NET
   * `RegularExpressionValidator` SUCCEEDS against an empty control by design, and the API reproduces the
   * same cascade.
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

    // ⚠ ABSENCE ON A NUMERIC FIELD SHORT-CIRCUITS, EXACTLY AS TEXTUAL ABSENCE DOES ABOVE. Reporting
    // "the value is required" alongside "the value must be a whole number" for one empty box says
    // the same thing twice and buries the actionable half. The sentence travels ON the error rather
    // than being looked up here, so one wording table serves the three fields that carry this rule.
    const absent: unknown = errors['requiredNumber'];

    if (isMessageBearingError(absent)) {
      messages.push(absent.message);
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

    // There is no `expression` branch, and there is no client rule that could raise one: a
    // regular expression's syntax is judged by the engine that will run it, and the server's
    // sentence reaches the operator through the per-field messages `messagesFor` merges in.
    return messages;
  }

  /**
   * Reports a settled single write, and closes the form when it succeeded.
   *
   * A failure whose operation is not the one that was awaited is NOT this write's: a successful create
   * triggers a re-read, and a re-read that then failed must not be announced as a failed create. Such a
   * failure reaches the reader through the inline banner instead, which is where a failed listing belongs.
   *
   * The form stays open on a refusal, which is the whole point of distinguishing the two. A duplicate name
   * is corrected in the form the operator is already looking at; closing it would discard eight other fields
   * they had just filled in.
   *
   * @param pending What was requested.
   * @param failure The failure the store recorded, or null when nothing failed.
   */
  private report(pending: AwaitedWrite, failure: UserFailure | null): void {
    const mine: boolean =
      failure !== null && failure.operation === AWAITED_OPERATION[pending.kind];

    if (!mine) {
      // The document is dropped on success, so a corrected field cannot go on showing the complaint
      // the previous attempt provoked.
      this.writeProblem.set(null);

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

    // `failure` is re-read from the narrowing above rather than asserted: `mine` is only true when it is
    // non-null, and the compiler needs the test in a position it can follow.
    const recorded: UserFailure | null = failure;

    if (recorded === null) {
      return;
    }

    // Retained so the per-field messages the document names stay beside their controls while the
    // operator corrects them, which is precisely when the store's shared slot is being cleared by
    // whatever else the application reads next.
    this.writeProblem.set(recorded.problem);

    this.notifications.notify(
      recorded.summary.severity,
      refusalMessage(pending, recorded),
      recorded.summary.supportReference,
    );
  }

    /**
   * Reports the outcome of ONE write from the Apply batch.
   *
   * ⚠ A REFUSAL IS ANNOUNCED PER ROW AND NAMES ITS OWN PROPERTY. Applying five rows of which two are
   * refused now produces two messages, each naming the property the server refused, because each
   * write's outcome is attributed to the write it belongs to AND the batch is written one row at a
   * time so no outcome can be coalesced away. Previously the store held one failure slot for the
   * whole batch, so at most one refusal survived — and whether even that one was still there when
   * the screen looked depended on the order the answers happened to arrive in.
   *
   * ⚠ SUCCESS RAISES NO NOTIFICATION, DELIBERATELY. The legacy Apply set no message label on success
   * either, and the pending set from which the grid's dirty rows are derived is computed as the
   * DIFFERENCE between the staged values and the server's — so a row that landed drops out of it by
   * itself and the operator can see the batch shrink. A per-row toast would additionally mean five
   * announcements for a five-row apply that worked.
   *
   * ⚠ IT IS NOT SILENT, THOUGH, AND THE DISTINCTION MATTERS. A browser measurement of this screen
   * found a successful Apply produced no observable feedback of any kind — no toast, and nothing in
   * any live region — which for a non-visual reader is indistinguishable from the button having done
   * nothing. Watching the visible count fall is not available to them. The all-accepted branch below
   * therefore states the outcome ONCE in the polite status region, which costs no pixels; see
   * {@link batchApplied}.
   *
   * The notification is the SINGLE surface for a write refusal on this screen. The banner is reserved
   * for the read that populates the grid; see {@link ProfileDefinitionListComponent.problem} for why
   * reporting one refusal in both registers was a defect rather than thoroughness.
   *
   * @param propertyName The property this write addressed, captured at dispatch.
   * @param settled The store's published result for it.
   */
  private reportBatchOutcome(settled: UserMutation): void {
    if (settled.operation !== AWAITED_OPERATION.applyEdits) {
      return;
    }

    // ⚠ ONE MESSAGE PER REFUSED ROW, EACH NAMING ITS OWN PROPERTY, AND THE LIST IS READ RATHER THAN
    // THE SETTLED FAILURE. The batch is one command with one settled result, which is what stops this
    // screen mistaking a sibling's outcome for its own — but a batch can refuse SEVERAL rows, and the
    // settled result carries only the first. An operator told once that "something was refused" cannot
    // tell which of five declarations to correct, so the store keeps every refused row with the row it
    // belongs to and this announces them in the order they arrived.
    //
    // A wholly accepted batch leaves the list empty and therefore announces nothing, which is the
    // legacy behaviour: Apply reported no success message, and the unapplied-change count falls by
    // itself because it is derived.
    const refusals: readonly ProfileDefinitionBatchRefusal[] = this.store.profileDefinitionBatchRefusals();

    if (refusals.length === 0) {
      // Announced in the polite status region and NOWHERE ELSE — see {@link batchApplied} for why a
      // toast would be the wrong register here, and why silence was nevertheless a regression.
      const applied: number = this.batchRows();
      this.batchApplied.set(`${applied} change(s) applied.`);
      return;
    }

    // The name is resolved from the catalogue rather than carried on the refusal, because the server's
    // problem document describes the REQUEST and has no reason to echo a name the client already holds.
    const namesById = new Map<number, string>(
      this.store.profileDefinitions().map((row) => [row.propertyDefinitionId, row.propertyName]),
    );

    for (const refused of refusals) {
      // The refusal's own wording is composed by the SHARED chooser, so a duplicate name, a vanished
      // declaration and a generic refusal read identically here and in the single-write path.
      const sentence: string = refusalMessage(
        { id: settled.id, kind: 'update', propertyDefinitionId: refused.propertyDefinitionId },
        refused.failure,
      );

      this.notifications.notify(
        refused.failure.summary.severity,
        batchRefusalMessage(
          namesById.get(refused.propertyDefinitionId) ?? String(refused.propertyDefinitionId),
          sentence,
        ),
        refused.failure.summary.supportReference,
      );
    }
  }

  /**
   * Moves focus to the screen's primary action after a row has been removed.
   *
   * This fixes a defect measured in a browser. The shared confirm dialog returns focus to whichever control
   * opened it, which is right when the operator CANCELS but impossible on a CONFIRMED delete, because the row
   * it deletes carries the invoking button and by the time the dialog closes that element no longer exists.
   * Focus collapsed to `body`, dropping a keyboard or screen-reader user back to the start of the document
   * after every delete - a WCAG 2.4.3 focus-order failure, and this screen's to fix rather than the dialog's,
   * since the dialog behaved correctly and it is this screen that destroyed the anchor.
   *
   * The anchor chosen is the create button: it is never removed, so it cannot repeat the defect; it sits
   * immediately beside the heading, so focus lands at the top of the working area; and it is the primary
   * action. Restoring to a surviving row's command was rejected - the rows are rendered by the shared table
   * from projected templates, so no stable per-row handle exists to address from here, and picking "the next
   * row" would move focus to a DIFFERENT property's destructive control.
   *
   * The deferral is mandatory. Every control on this screen is disabled while a write is in flight, the anchor
   * included, and this method is reached from the reporting effect - which is flushed BEFORE the view that
   * reads the same signals is refreshed. So at the instant this runs `saving()` has already returned false
   * while the DOM still carries the `disabled` attribute from the previous render, and a disabled element
   * cannot take focus, so a direct call fails SILENTLY: no error, no exception, focus simply stays on the body.
   * The trap is that by the time any assertion or manual retry runs the refresh has happened and the same call
   * succeeds, so the naive version looks correct everywhere except in production. `afterNextRender` runs the
   * move after the render that clears `disabled`, which is the first moment the anchor can accept focus, and
   * it needs an injector because this is not a constructor.
   *
   * `preventScroll` is deliberately NOT passed: the anchor is at the top of the screen and the grid may have
   * been scrolled far down it, so bringing the anchor into view is the point.
   */
  private restoreFocusAfterRemoval(): void {
    // A presence test on the resolved element, never a truthiness test on a DOM node, and no non-null
    // assertion: an unresolved query degrades to the browser's own fallback rather than throwing inside an
    // effect, where a throw would tear down the reporting for every later write as well.
    const anchor: ElementRef<HTMLButtonElement> | undefined = this.createTrigger;

    if (anchor === undefined) {
      return;
    }

    afterNextRender(
      () => {
        // Re-checked rather than trusted: a render can happen between the request and the callback, and
        // focusing a control that is still disabled or has left the document would be a silent no-op that
        // hid a regression.
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
   * The order is theirs: four commands first, then eight data columns.
   *
   * MIGRATION: the four command columns HIDE their headings. `Page_Load` ran
   * `Localization.LocalizeDataGrid`, which replaced each declared `HeaderText` with the resource value, and
   * `Edit.Header`, `Del.Header`, `Dn.Header` and `Up.Header` are all `<value />` — EMPTY. The markup's
   * "Edit", "Del", "Dn" and "Up" were therefore never painted. Hiding the heading reproduces that exactly
   * while the `label` keeps the column NAMED in the accessibility tree, so a cell is still announced with
   * its column name.
   *
   * NO COLUMN IS SORTABLE, BECAUSE THE ENDPOINT ACCEPTS NO ORDERING. `ProfileDefinitionsController
   * .ListAsync` takes a cancellation token and nothing else and answers an unpaged `IReadOnlyList`, so
   * there is no sort parameter to send and `SortableFields.cs` declares no permitted set for this
   * collection at all. Marking a column sortable would additionally oblige this screen to reorder the rows
   * itself and to drive `aria-sort` from its own state, since the shared table emits a sort intent and never
   * performs one. Nothing is declared and `sortChange` is left unbound. Where the endpoint does accept an
   * ordering the affordance IS offered - see the portal, account, role, membership and module listings - so
   * this absence is a property of the collection rather than a gap.
   *
   * NO WIDTHS. The legacy declared `Width="100px"` on five columns; the shared table's width contract admits
   * only a percentage, `min-content`, `max-content` or a custom property, and rejects anything else when the
   * set is bound. A pixel width cannot be expressed, so the columns size intrinsically. Reported as a
   * divergence in presentation with no behavioural consequence.
   *
   * `viewOrder` IS NOT A THIRTEENTH COLUMN. The legacy grid never displayed it — the two move commands were
   * its only visible expression — and the inline form is where its value is read and written. Adding a
   * column for it would pad the grid with something the legacy did not show.
   *
   * @returns The twelve columns, in legacy order.
   */
  private buildColumns(): readonly DataTableColumn<ProfilePropertyDefinition>[] {
    return [
      // 0. `dnn:imagecommandcolumn CommandName="Edit"`. The actions kind also suppresses row activation, so
      //   pressing Edit never doubles as selecting the row.
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

      // 2-3. `MoveDown` then `MoveUp`, in that order. The order is the markup's and the constants confirm it
      // (`COLUMN_MOVE_DOWN = 2`, `COLUMN_MOVE_UP = 3`), which is worth stating because down-before-up reads
      // backwards.
      //
      // ⚠ TWO SEPARATE COLUMNS IS THE LEGACY LAYOUT AND IT IS KEPT, INCLUDING THE ROW-TO-ROW OFFSET IT
      // PRODUCES. `ProfileDefinitions.ascx:L19-L20` declares two `dnn:imagecommandcolumn` entries -
      // `commandname="MoveDown" headertext="Dn"` and `commandname="MoveUp" headertext="Up"` - so each
      // direction has a fixed column of its own. The consequence is that the FIRST row, which cannot move up,
      // leaves its Up cell empty, and the LAST row, which cannot move down, leaves its Down cell empty; the
      // arrows therefore appear in different columns from one row to the next. That was reported as a
      // misalignment defect and it is declined here, because merging the two into one column would move a
      // control out of the column the legacy grid gave it - a visual divergence from the only visual
      // reference this migration has, the legacy screen itself. Nothing is broken: each direction is always
      // in its own column and each row's affordances are exactly the ones that row can act on.
      //
      // If the offset is ever judged unacceptable it is a DESIGN decision that needs a design source, not a
      // defect to be fixed here; the two-column shape is what the source declares.
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

      // 4. `dnn:textcolumn DataField="PropertyName"`, heading `Name.Header`. A bound column: the member is a
      //   non-nullable string, so there is nothing to format.
      {
        key: 'propertyName',
        // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
        // each cell belongs to - without it, traversing a row gives the column name and the value
        // and never the record's identity. This column is the one a person would read aloud to say
        // which row they mean. No visual change: the shared stylesheet restores a body row
        // header's normal weight.
        rowHeader: true,
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

      // 6. `asp:TemplateColumn HeaderText="DataType"`. A template column because the legacy cell was itself
      //   a template that called `DisplayDataType`; see {@link UNNAMED_DATA_TYPE_MARK} for why the cell
      //   paints the absent-value mark rather than the stored foreign key.
      {
        key: 'dataType',
        label: DATA_TYPE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.dataTypeCellTemplate, 'dataTypeCell'),
      },

      // 7. `dnn:textcolumn DataField="Length"`, heading `Length.Header`. Aligned to the END of the cell
      //   because it is a quantity; the HEADING keeps the grid's centred alignment, which is why the two
      //   members are set independently and neither is derived from the other.
      {
        key: 'length',
        label: LENGTH_HEADING,
        headerAlign: 'center',
        bodyAlign: 'end',
        field: 'length',
      },

      // 8-9. `DefaultValue` and `ValidationExpression`. Heading text from `DefaultValue.Header` and
      // `ValidationExpression.Header` — "Default Value" and "Validation Expression", SPACED, which is where
      // the resource values beat the markup's unspaced attributes.
      //
      //      Template columns, for two reasons. Both members are nullable, and the shared bound column
      //      renders text only, so a `null` would need formatting anyway. More importantly both hold
      //      TENANT-AUTHORED text of unknown shape — one of them is a regular expression — so each is
      //      rendered as monospaced, INTERPOLATED text. Nothing in this file binds `innerHTML` or reaches
      //      for a sanitiser, because nothing here is treated as markup at all.
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
      //         `AutoPostBack="True"` — the two columns the operator edits IN PLACE, and the reason this
      //         screen has a batch to apply at all. Template columns, because a check box is a control and
      //         the shared bound column renders text.
      //
      //         MIGRATION: the legacy check box posted back on every click (or, on a browser it judged
      //         capable, deferred to a client script that suppressed the post-back — `ItemCreated`). Both
      //         paths ended in the same place: the value was held and written on Apply. That is what happens
      //         here, with no round trip and no second code path.
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
   * The shared table declares `cellTemplate` as REQUIRED on both the template and the actions kinds, so an
   * absent one cannot be passed. A missing `ng-template` is a template authoring mistake, and failing here
   * names the reference that is missing instead of rendering nine columns and a blank.
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
