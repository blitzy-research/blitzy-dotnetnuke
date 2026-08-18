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

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
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
  ProfilePropertyDefinitionPosition,
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

/** `cmdRefresh.Text`. */
const REFRESH_LABEL = 'Refresh Grid';

/**
 * The four per-row command labels. the two move commands take their wording from `MoveDown.Text` and
 * `MoveUp.Text`, which the legacy really did publish.
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

/** `Introduction_Add.Title` and `Introduction.Title` — resolved by `GetText`. */
const CREATE_HEADING = 'Add New Property Details';
const EDIT_HEADING = 'Edit Property Details';

/** `cmdCreate.Text`, `cmdUpdate.Text` and `cmdCancel.Text`. */
const CREATE_SUBMIT_LABEL = 'Create New Property';
const EDIT_SUBMIT_LABEL = 'Update Property';
const CANCEL_LABEL = 'Return to Profile Properties List';

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
    // ⚠ "dislayed" IS NOT A TYPO IN THIS FILE - IT IS THE LEGACY RESOURCE, REPRODUCED BYTE FOR BYTE.
    //
    // Website/admin/Users/App_LocalResources/EditProfileDefinition.ascx.resx:142 reads "...to be
    // grouped when dislayed to the user." QA raised the misspelling as a defect, and it was
    // considered and DELIBERATELY LEFT STANDING under AAP 0.9.1 domain-logic preservation: a defect
    // discovered in the legacy is annotated in place and not fixed unless it blocks delivery, and a
    // misspelling in help text blocks nothing. The same rule already governs two odder survivals on
    // neighbouring screens - the Add role-group form whose primary action reads "Update", and the
    // empty billing terms that coerce to 0/1/N rather than staying unknown - so correcting this one
    // would make the parity policy arbitrary rather than make the product better.
    //
    // DO NOT "FIX" THIS SPELLING. It is guarded by the spec named "preserves the legacy misspelling
    // in the category hint", which fails the moment the word is corrected.
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

const NAME_REQUIRED_MESSAGE = 'The Property Name is required';
const NAME_PATTERN_MESSAGE = 'The property name cannot contain spaces';
const CATEGORY_REQUIRED_MESSAGE = 'The Category is required';
const DATA_TYPE_REQUIRED_MESSAGE = 'The Data Type is required';

/** Authored, in the register of the two legacy sentences. */
const LENGTH_REQUIRED_MESSAGE = 'The Length is required';

/** Authored likewise. */
const VIEW_ORDER_REQUIRED_MESSAGE = 'The View Order is required';

const WHOLE_NUMBER_MESSAGE = 'Enter a whole number.';
const NAME_TOO_LONG_MESSAGE = 'Property Name must be 50 characters or fewer';
const CATEGORY_TOO_LONG_MESSAGE = 'Property Category must be 50 characters or fewer';
const EXPRESSION_TOO_LONG_MESSAGE = 'Validation Expression must be 512 characters or fewer';

/** Terminal column widths the API also enforces. */
const NAME_MAX_LENGTH = 50;
const CATEGORY_MAX_LENGTH = 50;
const EXPRESSION_MAX_LENGTH = 512;

/** `SharedResources.resxDeleteItem.Text` — the legacy confirm text. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * `DuplicateName.Text`, reproduced byte for byte including both double spaces. the legacy signalled this
 * outcome through a RETURN VALUE BELOW THE NULL SENTINEL — `Wizard_NextButtonClick` assigned
 * `AddPropertyDefinition`'s result into the identifier and then tested `If PropertyDefinitionID <
 * Null.NullInteger`, i.e. "less than minus one".
 */
const DUPLICATE_NAME_MESSAGE =
  'This Property already exists.  Property Names must be unique.  Please select a ' +
  'different name for this property.';

/**
 * Authored: a `409` on a removal that is none of the refusals this screen recognises by code.
 *
 * ⚠ THIS IS NOW THE FALLBACK AND NOT THE COMMON CASE, and the distinction matters because the sentence used
 * to be wrong. It was the only thing a `409` on a removal produced, and it asserted that the property was
 * "in use" and told the operator to "remove the recorded values first" — advice that names no way to do it,
 * for a condition it was guessing at. The two refusals the API actually issues are now recognised by their
 * codes above and each reports what the server said, so this is reached only by a genuine concurrency
 * conflict, which is what it now describes.
 */
const DEFINITION_IN_USE_MESSAGE =
  'That profile property was changed by someone else, so it was not deleted. Read the list ' +
  'again and retry.';

/** Authored: the `404` a removal or replacement draws when the declaration is already gone. */
const DEFINITION_GONE_MESSAGE =
  'That profile property no longer exists. The list has been refreshed.';

/**
 * Names the property a batch refusal belongs to, in front of the refusal's own sentence. ⚠ AUTHORED, WITH
 * NO LEGACY COUNTERPART, AND THE ABSENCE IS WHY IT IS NEEDED. Apply writes one row per edited
 * declaration, and the legacy screen could report at most one outcome for the whole batch — it held a
 * single message area and a single provider result — so it never had to say WHICH row a refusal belonged
 * to.
 *
 * @param propertyName The property the refused write addressed.
 * @param sentence The refusal's own wording.
 * @returns The message to announce.
 */
function batchRefusalMessage(propertyName: string, sentence: string): string {
  return `${propertyName}: ${sentence}`;
}

/**
 * What a batch refusal is attributed to when it belongs to the ATOMIC DISPLAY-ORDER WRITE rather than to
 * one declaration.
 *
 * ⚠ NOT A PROPERTY NAME, AND IT MUST NOT BE ONE. Positions are written as a single unit of work because a
 * move exchanges two of them, so naming either declaration would tell the operator that one row was at
 * fault when neither was — and would imply the other row's move succeeded, which it did not.
 */
const ORDER_REFUSAL_SUBJECT = 'Display order';

/** What is announced when the atomic order write is refused, so no field edit is attempted either. */
const ORDER_REFUSAL_SUFFIX =
  ' No display order was changed, and no other change was applied.';

const GRID_CAPTION = 'Profile properties declared for this site';

/** Wording of the two bulk toggles. */
const ALL_REQUIRED_LABEL = 'Required for every property';
const ALL_VISIBLE_LABEL = 'Visible for every property';

// Rules measured out of the legacy source

/**
 * The name rule, lifted verbatim from `ProfilePropertyDefinition.vb`. Anchored at both ends exactly as
 * the legacy declared it, and the hyphen is escaped in the same position, so the accepted set is
 * identical: letters, digits, dot, underscore, percent, hyphen, plus and apostrophe.
 */
const NAME_PATTERN = /^[a-zA-Z0-9._%\-+']+$/;

/**
 * The four declarations whose DELETE command the legacy screen hid. `grdProfileProperties_ItemDataBound`
 * reached into cell index 1 — the delete column — and set `delImage.Visible = False` for exactly these
 * four names, compared with `PropertyName.ToLower`.
 */
const UNDELETABLE_PROPERTY_NAMES: readonly string[] = Object.freeze([
  'lastname',
  'firstname',
  'timezone',
  'preferredlocale',
]);

/** The failure codes this screen recognises, spelled as {@link failureCode} normalises them. */
const DUPLICATE_NAME_CODE = 'profile_definition.duplicate_name';
const NOT_FOUND_CODE = 'profile_definition.not_found';

/**
 * The `409` refused because the declaration is one of the four the platform reserves. Terminal: there is no
 * parameter that performs it, so the screen reports the reason and offers nothing further.
 */
const PROTECTED_CODE = 'profile_definition.protected';

/**
 * The `409` refused because accounts hold answers that removal would destroy. RECOVERABLE, and the only
 * refusal on this screen that is: the server's detail reports how many answers are at stake, and repeating
 * the call with consent performs it.
 */
const VALUE_CASCADE_CODE = 'profile_definition.value_deletion_unacknowledged';

/** The three store operations this screen awaits the outcome of. */
type AwaitedWrite =
  | { readonly id: number; readonly kind: 'create' }
  | { readonly id: number; readonly kind: 'update'; readonly propertyDefinitionId: number }
  // The identifier is carried alongside the name because a removal can come BACK as a question - the server
  // refuses a cascade until it is consented to - and the retry addresses the declaration by id.
  | {
      readonly id: number;
      readonly kind: 'delete';
      readonly propertyDefinitionId: number;
      readonly propertyName: string;
    };

/**
 * The store operation each awaited write settles as. Declared as a lookup rather than a switch so that
 * the effect can compare the failure's own operation against the one it is waiting for.
 */
const AWAITED_OPERATION = {
  create: 'createProfileDefinition',
  update: 'updateProfileDefinition',
  delete: 'deleteProfileDefinition',

  /** The Apply batch, which the store writes as one command over many rows. */
  applyEdits: 'applyProfileDefinitionEdits',
} as const;

/**
 * The two store operations that populate THIS screen, and the only two whose failures belong in its
 * banner.
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

/** The typed shape of the inline create-and-edit form. */
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

/** Every control's messages, keyed by control name. */
type ProfileDefinitionFieldMessages = Readonly<
  Record<keyof ProfileDefinitionFormModel, readonly string[]>
>;

/**
 * The resolved value of {@link ProfileDefinitionFormModel}. Declared explicitly rather than inferred from
 * the group, because it is also the shape of {@link CREATE_DEFAULTS} and of the value {@link
 * ProfileDefinitionListComponent} hands to `reset`, and a single named contract is what keeps those three
 * from drifting apart.
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

type MutableProfileRow = {
  -readonly [K in keyof ProfilePropertyDefinition]: ProfilePropertyDefinition[K];
};

// SENTINELS
// propertyDefinitionId -1 never occurs. The column is `IDENTITY(1, 1)` in 04.00.04, so the first real
// declaration is 1 and absence is expressed as `null` in this file, tested with `=== null`. dataType -1 is
// the legacy field initialiser, i.e. "no type chosen yet".

/** `Null.NullInteger`. */
const NULL_INTEGER = -1;

const UNNAMED_DATA_TYPE_MARK = '\u2014';

/**
 * What precedes the stored reference when one IS stored. ⚠ THE STORED REFERENCE IS NOW PAINTED, AND THE
 * ANNOTATION ABOVE ARGUED AGAINST PAINTING IT. That argument was right about one thing and wrong about
 * another, and the prefix is what separates them.
 */
const DATA_TYPE_REFERENCE_PREFIX = '#';

/** The wording behind {@link UNNAMED_DATA_TYPE_MARK} when a type IS stored but cannot be named. */
const UNNAMEABLE_DATA_TYPE_PREFIX = 'data type reference ';
const UNNAMEABLE_DATA_TYPE_SUFFIX = ', name unavailable';

/**
 * The wording behind {@link UNNAMED_DATA_TYPE_MARK} for the `Null.NullInteger` sentinel. -1 is the legacy
 * field initialiser and means "no type chosen yet", which is a different fact from a stored type whose
 * name cannot be read. The two cases paint the same mark, exactly as the legacy rendered both as the
 * empty string, but they are described differently because they are not the same.
 */
const NO_DATA_TYPE_CHOSEN_DESCRIPTION = 'no data type chosen';

/**
 * The column note standing under the grid, explaining why the Data Type column carries a NUMBER.
 *
 * `ProfilePropertyDefinition.DataType` stores a `ListEntryID` into the legacy `Lists` table, and the
 * name behind that identifier is only readable through the DotNetNuke list subsystem. AAP 0.2.2.2
 * places `Library/Components/Lists/**` explicitly out of scope, so there is no catalogue in this
 * console to resolve the reference against and no in-scope place to add one - naming the type would
 * mean porting an excluded subsystem, which the AAP forbids. QA raised the bare `#349` as unhelpful,
 * and it is: what was missing was not the name but the REASON the name is absent. This states the
 * reason once for the whole column instead of leaving each row to look like a rendering fault.
 */
const DATA_TYPE_COLUMN_NOTE =
  'Data Type shows the stored reference number rather than a name, because the data type ' +
  'catalogue is not part of this console. A dash means no data type has been chosen yet.';

/**
 * The create-mode defaults, measured from the legacy field initialisers.
 *
 * ⚠ ONE DEPARTURE, AND IT IS THE DATA TYPE - QA-10. The legacy field initialiser is `Null.NullInteger`,
 * which is -1, and this used to seed the control with it. But the control is a `type="number"` box, so the
 * operator was presented with a required field already containing the literal text "-1" - a real number
 * that looks chosen - and the form's own `notNullInteger` validator then refused it with "The Data Type is
 * required". A field cannot both offer a value and reject it.
 *
 * `null` states the same fact honestly: -1 MEANS "nothing chosen yet", and an empty box is what "nothing
 * chosen" looks like. Nothing downstream changes, because -1 was never a submittable value: the validator
 * rejected it, and `apply()` already refuses a null data type before composing a request, so the sentinel
 * never reached the wire under either spelling. The stored sentinel is untouched - an EXISTING row that
 * holds -1 still loads it (see the edit-mode patch, which reads `definition.dataType` directly), and the
 * grid still paints it behind its own reference wording.
 */
const CREATE_DEFAULTS: ProfileDefinitionFormValue = {
  propertyName: '',
  dataType: null,
  propertyCategory: '',
  length: 0,
  defaultValue: '',
  validationExpression: '',
  required: false,
  visible: false,
  viewOrder: 0,
};

// VALIDATORS

/**
 * Requires a whole number, accepting zero and negative values alike. NO LOWER BOUND, deliberately.
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
 * Whether a validation error carries a sentence to show.
 *
 * @param detail The error detail to inspect.
 * @returns True when it carries a string `message`.
 */
function isMessageBearingError(detail: unknown): detail is { readonly message: string } {
  // The `in` test narrows the value to one carrying the member, so the member is read without an assertion
  // — the workspace forbids one, and here it would also be the wrong tool: the whole point is to establish
  // the shape rather than to claim it.
  return (
    typeof detail === 'object' && detail !== null && 'message' in detail
    && typeof detail.message === 'string'
  );
}

/**
 * Builds a validator that reports its own error key when a control holds no number at all.
 *
 * @param message The sentence to report.
 * @returns A validator reporting `requiredNumber` when the control holds no number at all.
 */
function requiredNumber(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    // Zero is emphatically NOT among them and must never be added: a first position of 0 and a length of 0
    // are both legitimate stored values, so a truthiness test here would refuse exactly the values the
    // legacy schema seeds.
    if (raw === null || raw === undefined || raw === '') {
      return { requiredNumber: { message } };
    }

    return null;
  };
}

/**
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

// PURE HELPERS

/**
 * Whether a set of staged grid edits differs from what a declaration currently holds. Compared member by
 * member with strict equality, never by serialising the two and comparing the text: `false` and `0` are
 * DATA on these members, and a comparison that went through a truthiness test or a loose equality would
 * treat a legitimate `false` as "unset" and a legitimate `0` as equal to it.
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
 * Copies the nine writable members of a declaration into a replace request. Every member must be carried,
 * not just the one being changed.
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
 * The presence message for one control. Three of the four required members have a sentence; the fourth,
 * the view order, cannot fail presence at all because its control is a non-nullable number — so its arm
 * exists only to keep the switch exhaustive and returns the shared numeric sentence.
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
 * The over-length message for one control. Only three members carry a length limit, and each limit is the
 * terminal column width the API also enforces.
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
 * @param pending What was requested.
 * @returns The confirmation to announce.
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
 * Wording for a refused write. Three sources, in order of specificity.
 *
 * @param pending What was requested.
 * @param failure The failure the store recorded.
 * @returns The message to announce.
 */
function refusalMessage(pending: AwaitedWrite, failure: UserFailure): string {
  if (failure.code === DUPLICATE_NAME_CODE) {
    return DUPLICATE_NAME_MESSAGE;
  }

  if (failure.code === NOT_FOUND_CODE || failure.problem?.status === 404) {
    return DEFINITION_GONE_MESSAGE;
  }

  // ⚠ THE SERVER'S OWN SENTENCE WINS FOR BOTH PROTECTIONS ON A REMOVAL, and neither may fall through to the
  // generic conflict wording below. One reports WHICH reserved property was refused and what to do instead;
  // the other reports HOW MANY recorded answers are at stake, that the loss is permanent, and where to take a
  // backup first. Replacing either with a fixed sentence authored here would discard the count — the single
  // fact that makes the decision an informed one.
  if (failure.code === PROTECTED_CODE || failure.code === VALUE_CASCADE_CODE) {
    return stripLegacyBreakTags(failure.summary.message);
  }

  // Any OTHER conflict on a removal is a concurrent writer, which is a different refusal from a duplicate
  // name and is worded as its own thing rather than left to the generic sentence.
  if (pending.kind === 'delete' && failure.problem?.status === 409) {
    return DEFINITION_IN_USE_MESSAGE;
  }

  return stripLegacyBreakTags(failure.summary.message);
}

/**
 * The profile-property catalogue for the tenant: `/settings/profile-definitions`. this one screen
 * replaces two legacy pages and a three-step wizard.
 */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE. Every screen's header carries exactly one
 * subtitle stating that screen's SCOPE: the record it acts on when the title does not already name it,
 * and otherwise what the screen is for, in one line. It never carries a status, a count or a progress
 * readout - those belong to the live region that owns them, and a count in two places is two owners of
 * one fact. Measured finding: subtitles appeared on ten of the twenty screens and carried three
 * different kinds of thing, so a reader could not tell what the slot was for.
 */
const PAGE_SUBTITLE =
  'The profile properties every account on this site can hold.';

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
  ],
  templateUrl: './profile-definition-list.component.html',
  styleUrl: './profile-definition-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfileDefinitionListComponent implements OnInit {
  private readonly store = inject(UserStore);

  private readonly notifications = inject(NotificationService);

  private readonly injector = inject(Injector);

  // CELL TEMPLATES
  // Static queries, so they are resolved before `ngOnInit` and the column set can be assembled there. Each
  // `ng-template` belongs to THIS component's view even though `app-data-table` is what renders it, which
  // is what makes a projected cell template work at all.

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
   * The screen's primary action button, held as the focus anchor for a confirmed delete. Why a view query
   * on a button that is never read for its value.
   */
  @ViewChild('createTrigger', { static: true })
  private createTrigger?: ElementRef<HTMLButtonElement>;

  // State owned by this screen

  /** Backing store of {@link columns}; populated once, in `ngOnInit`. */
  private readonly columnSet = signal<readonly DataTableColumn<ProfilePropertyDefinition>[]>([]);

  /** The grid edits made but not yet applied, keyed by declaration identifier. */
  private readonly draft = signal<ReadonlyMap<number, GridEdits>>(new Map<number, GridEdits>());

  /**
   * The stable row instance for each declaration, keyed by `propertyDefinitionId`. NOT a signal, and
   * deliberately so: it is an identity cache rather than state.
   */
  private readonly rowInstances = new Map<number, MutableProfileRow>();

  /** Whether the inline form is on screen. */
  private readonly formOpen = signal(false);

  /** The declaration being replaced, or `null` when the form is creating. Presence, never falsiness. */
  private readonly editing = signal<number | null>(null);

  /** The declaration awaiting confirmation of removal, or `null` when none is. */
  private readonly pendingRemoval = signal<ProfilePropertyDefinition | null>(null);

  /**
   * The declaration whose recorded answers the server has asked the operator to consent to destroying,
   * together with the sentence it used to ask, or `null` when no such consent is outstanding.
   *
   * The message is the SERVER'S, carried rather than re-authored, because it states the number of answers at
   * stake — a fact this screen does not otherwise hold and must not invent.
   */
  private readonly pendingCascade = signal<{
    readonly definition: ProfilePropertyDefinition;
    readonly message: string;
  } | null>(null);

  /** The single write whose outcome is being awaited, or `null`. */
  private readonly awaited = signal<AwaitedWrite | null>(null);

  /** The identifier of the Apply batch that is outstanding, or `null` when none is. */
  private readonly batchWrite = signal<number | null>(null);

  /** How many rows the outstanding Apply batch dispatched. */
  private readonly batchRows = signal<number>(0);

  /**
   * What to announce about the batch that has just settled, or the empty string when there is nothing. ⚠
   * THIS EXISTS FOR A NON-VISUAL READER AND CHANGES NOTHING ON SCREEN. The legacy announced no success
   * sentence either — `cmdUpdate_Click` L444-L452 called `UpdateProperties` then `RefreshGrid` and set no
   * message label, and `ProfileDefinitions.ascx` declares none to set — so raising a toast here would be
   * a visible divergence, and for a five-row apply a repetitive one.
   */
  private readonly batchApplied = signal<string>('');

  private readonly submitAttempted = signal(false);

  // STORE-DERIVED SURFACE

  /** Whether the catalogue is still being fetched. */
  protected readonly loading: Signal<boolean> = this.store.profileDefinitionsLoading;

  /** Whether a write is in flight. */
  protected readonly saving: Signal<boolean> = this.store.saving;

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
   * Whether the CATALOGUE READ failed, so an empty grid means "nothing is known" rather than "this site
   * declares no profile properties". Measured before this existed: a `403` on an account's own profile
   * rendered as that very sentence while thirteen properties were declared.
   */
  protected readonly listFailed: Signal<boolean> = computed<boolean>(() => {
    const failure: UserFailure | null = this.store.failure();

    return failure !== null && isCatalogueRead(failure.operation);
  });

  // A FALLBACK SENTENCE USED TO BE COMPOSED HERE, AND IT IS GONE BECAUSE THE FAILURE IT COVERED CANNOT
  // OCCUR ANY MORE. It existed for the one class of failure that carried no problem document: a response
  // this client could not decode, which reaches a subscriber as a plain error with no status and no body.
  // The store now synthesises a document for exactly that case - `contractProblem`, titled "Unexpected
  // response" - so `problem` above is never null and the banner has real wording, a real severity and a
  // real support reference to render. Keeping the fallback would have left a computed that can only ever
  // return null.

  /** The document from the most recent write of THIS screen's that was refused, or `null`. */
  private readonly writeProblem = signal<ProblemDetails | null>(null);

  // -------------------------------------------------------------------------
  // THE GRID

  /** The twelve columns, assembled in `ngOnInit` once the cell templates exist. */
  protected readonly columns = this.columnSet.asReadonly();

  /**
   * The rows to render: the server's catalogue with the operator's pending edits applied, ordered by
   * position. the local re-sort is required for parity, not a decoration.
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

    // ⚠ A NEW ARRAY EVERY TIME, HOLDING THE SAME ROW INSTANCES, and both halves of that are load-bearing.
    // The array is what the grid's row input reads, and a signal compares by reference — so a new array is
    // what makes the grid re-project every cell and pick up the staged value.
    return projected.sort(
      (left, right) =>
        left.viewOrder - right.viewOrder ||
        left.propertyDefinitionId - right.propertyDefinitionId,
    );
  });

  /** How many declarations carry an edit that has not been applied. */
  protected readonly dirtyCount = computed<number>(() => this.pendingRows().length);

  /** Whether "Apply Changes" has anything to do. */
  protected readonly hasPendingChanges = computed<boolean>(() => this.pendingRows().length > 0);

  protected readonly applyAnnouncement = computed<string>(() =>
    this.hasPendingChanges()
      ? `${this.dirtyCount()} unapplied change(s).`
      : this.batchApplied(),
  );

  /**
   * Whether every declaration is already required, and likewise for visible. The select-all affordance
   * had to move out of the table, and the gap is reported rather than papered over.
   */
  protected readonly allRequired = computed<boolean>(() =>
    this.rows().every((definition) => definition.required),
  );

  protected readonly allVisible = computed<boolean>(() =>
    this.rows().every((definition) => definition.visible),
  );

  /** Whether the catalogue holds anything, which is what gates the bulk region. */
  protected readonly hasRows = computed<boolean>(() => this.rows().length > 0);

  // THE INLINE FORM

  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   *
   * ⚠ THE PROBE USED TO SEE ONLY THE INLINE FORM, AND THAT MADE THE REGISTRATION LOOK LIKE PROTECTION IT WAS
   * NOT PROVIDING. This screen has two independent kinds of unsaved edit: the create-and-edit form, whose
   * state `form.dirty` reports, and the per-row bulk toggles, which live in signals and never touch the form
   * at all. Because only the first was asked, toggling rows until Apply Changes lit up and then leaving through
   * a sidebar link or the browser's Back button departed immediately and lost the edit — measured as the sole
   * unguarded screen of the seven editing routes tested, even though the route has always declared the gate and
   * this component has always registered a probe. `hasPendingChanges()` is the same computed the Apply Changes
   * button is enabled from, so the question the gate asks is now exactly the question the screen already
   * answers on its own face: if that button is live, there is something to lose.
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
    () => this.form.dirty || this.hasPendingChanges(),
  );

  /**
   * The create-and-edit form, in the order `SortOrder` declared the members. Every control is
   * `nonNullable`, which is what makes ONE group safe to reuse across creation and the editing of every
   * row: `reset(value)` returns each control to a real value rather than to `null`, and `getRawValue()`
   * is fully typed rather than a `Partial`.
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
    validationExpression: new FormControl(CREATE_DEFAULTS.validationExpression, {
      nonNullable: true,
      validators: [Validators.maxLength(EXPRESSION_MAX_LENGTH)],
    }),
    required: new FormControl(CREATE_DEFAULTS.required, { nonNullable: true }),
    visible: new FormControl(CREATE_DEFAULTS.visible, { nonNullable: true }),
    // ⚠ THE TEMPLATE MARKS THIS FIELD REQUIRED AND `aria-required`, SO A RULE MUST ENFORCE IT. It had none:
    // the field was announced as required to every assistive technology and to every sighted reader by its
    // indicator, and then accepted being left empty — which reached a non-nullable server integer as `null`
    // and drew a `400` naming no field.
    viewOrder: new FormControl(CREATE_DEFAULTS.viewOrder, {
      nonNullable: true,
      validators: [requiredNumber(VIEW_ORDER_REQUIRED_MESSAGE), wholeNumber()],
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

  /**
   * The form's own event stream, as a signal, so a derivation can depend on the form. ⚠ A BRIDGE, NOT
   * STATE. A reactive form is not a signal: its validity changes as the operator types, and nothing about
   * that is observable to `computed()`.
   */
  private readonly formEvent = toSignal(this.form.events, { initialValue: null });

  /**
   * Every control's messages, computed ONCE per change and read as a map. ⚠ MEMOISED BECAUSE THE TEMPLATE
   * IS A HOT PATH. Each field binds its messages twice — to the shared field's `error` input and to the
   * control's own `aria-invalid` — so nine controls make fourteen reads per change-detection pass on this
   * screen alone.
   */
  protected readonly fieldMessages: Signal<ProfileDefinitionFieldMessages> =
    computed<ProfileDefinitionFieldMessages>(() => {
      this.formEvent();

      const attempted: boolean = this.submitAttempted();

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

  /** The declaration awaiting confirmation, or `null`. */
  protected readonly removalTarget = this.pendingRemoval.asReadonly();

  /** The outstanding cascade consent, or `null`. Its presence is what opens the second dialog. */
  protected readonly cascadeTarget = this.pendingCascade.asReadonly();

  // Wording exposed to the template

  /** The one-line scope statement shown beneath the title. */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

  protected readonly pageTitle = PAGE_TITLE;
  protected readonly helpText = HELP_TEXT;
  protected readonly addLabel = ADD_LABEL;
  protected readonly applyLabel = APPLY_LABEL;
  protected readonly refreshLabel = REFRESH_LABEL;
  protected readonly cancelLabel = CANCEL_LABEL;
  protected readonly editLabel = EDIT_LABEL;
  protected readonly deleteLabel = DELETE_LABEL;

  /**
   * The confirm label on the cascade dialog. Distinct from {@link deleteLabel} on purpose: the
   * operator has already pressed Delete once, so repeating the same word would make the second ask
   * look like the first rather than like the escalation it is.
   */
  protected readonly deletePermanentlyLabel = 'Delete permanently';
  protected readonly moveUpLabel = MOVE_UP_LABEL;
  protected readonly moveDownLabel = MOVE_DOWN_LABEL;

  /**
   * Composes the accessible name of one of the two state check boxes in a grid row. ⚠ THE COLUMN WORD IS
   * THE WHOLE POINT, AND ITS ABSENCE WAS A REAL DEFECT. Each cell used to name its box with the row's
   * property name ALONE, so the two boxes in a row both announced "City" - and a reader using assistive
   * technology could not tell which of them made the property required and which made it visible.
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
  protected readonly dataTypeColumnNote = DATA_TYPE_COLUMN_NOTE;
  protected readonly allRequiredLabel = ALL_REQUIRED_LABEL;
  protected readonly allVisibleLabel = ALL_VISIBLE_LABEL;

  /**
   * The confirmation body: the legacy question verbatim, then WHICH record it means.
   *
   * ⚠ THE MEASURED DEFECT. The dialog read only "Are You Sure You Wish To Delete This Item?" and named nothing at
   * all - searched against every identifier on the page it matched none of them - while being a real modal
   * that PHYSICALLY COVERS the grid behind it. Measured with the sixth row targeted, it overlaid the three
   * rows above it and the top of the target itself, so an operator had no way to check what was about to be
   * destroyed: the record's identity existed only on the triggering control's accessible name, which is
   * unreachable once the modal holds focus.
   *
   * The wording is APPENDED rather than rewritten, so the measured legacy sentence survives unchanged and
   * this reads as the same question with the answer to "which one" added. A property definition is named by its property name, which is the identifier the grid shows and the
   * one the endpoint keys on.
   */
  protected readonly removalMessage: Signal<string> = computed<string>(() => {
    const target: ProfilePropertyDefinition | null = this.removalTarget();

    if (target === null) {
      return DELETE_CONFIRM_MESSAGE;
    }

    const named: string = target.propertyName.trim();

    return named.length === 0 ? DELETE_CONFIRM_MESSAGE : `${DELETE_CONFIRM_MESSAGE} ${named}`;
  });
  protected readonly fieldText = FIELD_TEXT;

  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them. ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT.
   * The grid's own fallback is the row OBJECT, which is a correct key only while the same objects stay in
   * play; every read from the server decodes fresh objects, so without this a refetch of the same page
   * presents entirely new keys and the whole body is rebuilt to display records that never changed.
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
   * Assembles the columns and reads the catalogue. The cell templates are captured by STATIC view
   * queries, so they are resolved by the time this runs and the column set can be built here.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
    this.store.loadProfileDefinitions();
  }

  // GRID READ HELPERS

  /**
   * Whether a declaration may be removed. `grdProfileProperties_ItemDataBound` reached into the delete
   * column and set `delImage.Visible = False` for four names, matched on `PropertyName.ToLower`.
   *
   * @param definition The row being rendered.
   * @returns False for the four platform properties, true for everything else.
   */
  protected canDelete(definition: ProfilePropertyDefinition): boolean {
    return !UNDELETABLE_PROPERTY_NAMES.includes(definition.propertyName.toLowerCase());
  }

  /**
   * The text painted in the data-type cell for one row. Two shapes, because the column carries two
   * different facts: a stored type whose NAME cannot be read paints its reference behind {@link
   * DATA_TYPE_REFERENCE_PREFIX}, and the `Null.NullInteger` sentinel - "no type chosen yet" - paints
   * {@link UNNAMED_DATA_TYPE_MARK}.
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
   * Applies every staged edit as ONE BATCH, in two steps whose difference matters.
   *
   * ## Positions first, together, as one unit of work
   *
   * ⚠ THE POSITIONS ARE NO LONGER SENT AS INDEPENDENT REPLACEMENTS, AND THAT IS THE FIX. A move
   * EXCHANGES the stored positions of two declarations, so the two writes are only correct together: land
   * one and lose the other and both rows claim the same position, which is neither the order the operator
   * started from nor the one they asked for. The whole position set therefore goes to
   * `PUT /api/v1/profile-definitions/order`, which commits it once — and if that is refused, no field write
   * is attempted either, because applying flags over an order the server declined would report a partial
   * success that did not happen.
   *
   * ## Field edits stay one write per declaration
   *
   * A required or visible flag is a fact about ONE declaration and holds or fails on its own, so a refused
   * row simply keeps the flag it had; there is no relation between rows to corrupt. Keeping them per-row is
   * what lets a five-row apply report WHICH three declarations were refused rather than reporting only that
   * something was.
   */
  protected applyChanges(): void {
    const positions: readonly ProfilePropertyDefinitionPosition[] = this.pendingPositions();
    const edits: readonly ProfileDefinitionEdit[] = this.pendingFieldEdits();

    // A confirmation of the PREVIOUS batch must not still be readable while this one is in flight.
    this.batchApplied.set('');

    // The count the operator is shown is the number of DECLARATIONS they changed, which is what
    // `pendingRows` reports — not the number of requests the two steps happen to need. A reorder of two
    // neighbours is two changes to an operator and one request on the wire, and the confirmation must speak
    // the operator's terms.
    this.batchRows.set(this.pendingRows().length);

    this.batchWrite.set(this.store.applyProfileDefinitionEdits(edits, positions));
  }

  /** Discards every staged edit and re-reads the catalogue. */
  protected refresh(): void {
    this.draft.set(new Map<number, GridEdits>());
    this.store.refreshProfileDefinitions();
  }

  // THE INLINE FORM

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
   * Opens the form to replace an existing declaration. `Page_Init` built `EditUrl("PropertyDefinitionID",
   * "KEYFIELD", "EditProfileProperty")` and set the command column's `EditMode="URL"`, so Edit was a
   * NAVIGATION to a second page carrying the identifier in the query string.
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

  /** Closes the form without writing anything. */
  protected cancelForm(): void {
    this.formOpen.set(false);
    this.editing.set(null);
    this.submitAttempted.set(false);
    this.form.reset(CREATE_DEFAULTS);
    // Abandoning the form abandons the complaints it drew, so re-opening it starts clean.
    this.writeProblem.set(null);
  }

  /**
   * Writes the form. `Wizard_NextButtonClick` saved on "Next" from step zero, guarded by `If
   * Properties.IsDirty And Properties.IsValid Then`, and chose between add and update by comparing the
   * identifier against `Null.NullInteger`.
   */
  protected submitForm(): void {
    this.submitAttempted.set(true);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // ⚠ THE TWO KEYED STRINGS ARE TRIMMED INTO THEIR CONTROLS AND THE FORM IS THEN RE-JUDGED, rather than
    // trimmed on the way into the request. The order is the whole point.
    this.normaliseKeyedStrings();

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    const raw: ProfileDefinitionFormValue = this.form.getRawValue();
    const editingId: number | null = this.editing();

    // ⚠ NARROWED BEFORE THE REQUEST IS ASSEMBLED, NOT AT THE BOUNDARY AND NOT BY A CAST. The three numeric
    // controls can hold `null`, and the request contract declares all three as non-nullable integers, so
    // the absence has to be removed here or it reaches the server.
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

    const members: UpdateProfilePropertyDefinitionRequest = {
      propertyName: value.propertyName,
      propertyCategory: value.propertyCategory,
      dataType: value.dataType,
      // The empty string is sent, not `null`. `Null.NullString` IS the empty string, and the API serialises
      // with `DefaultIgnoreCondition = Never`, so the sentinel survives the round trip exactly as the
      // legacy stored it.
      defaultValue: value.defaultValue,
      length: value.length,
      required: value.required,
      validationExpression: value.validationExpression,
      viewOrder: value.viewOrder,
      visible: value.visible,
    };

    if (editingId === null) {
      const request: CreateProfilePropertyDefinitionRequest = { ...members, moduleDefId: null };

      // The marker is set FROM the dispatch rather than before it, because the identifier it must carry is
      // what the dispatch returns. The command dispatches synchronously and cannot settle within its own
      // call, so no answer can arrive before the marker exists.
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
   * Trims the two keyed strings into their own controls. `emitEvent: false` because this is a DISPLAY
   * CORRECTION rather than an operator edit: it must not be able to start a cascade through any listener
   * on this form.
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
   * The messages to show beneath one control. Two sources, merged: this screen's own rules, and whatever
   * the server reported against the same member.
   *
   * @param control The control's name, which is also the member name the server reports.
   * @returns The messages, in rule order then server order.
   */
  protected messagesFor(control: keyof ProfileDefinitionFormModel): readonly string[] {
    return this.fieldMessages()[control];
  }

  // REMOVAL

  /**
   * Asks for confirmation before removing a declaration. `DeleteProperty` removed immediately and then
   * refreshed — removal was never part of the batch — and the only guard was a browser `confirm` whose
   * text came from `Localization.GetString("DeleteItem")`, i.e. `SharedResources.resx`.
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

  /** Removes the confirmed declaration. */
  protected confirmRemoval(): void {
    const target: ProfilePropertyDefinition | null = this.pendingRemoval();

    if (target === null) {
      return;
    }

    this.pendingRemoval.set(null);
    this.awaited.set({
      id: this.store.deleteProfileDefinition(target.propertyDefinitionId),
      kind: 'delete',
      propertyDefinitionId: target.propertyDefinitionId,
      propertyName: target.propertyName,
    });
  }

  /** Abandons a removal the server asked a second time about, leaving the declaration and its answers. */
  protected cancelCascade(): void {
    this.pendingCascade.set(null);
    this.restoreFocusAfterRemoval();
  }

  /**
   * Repeats a removal WITH consent to destroying the recorded answers, which is the only call in this screen
   * that passes the flag. It is reachable solely from the dialog the server's own refusal opened, so consent
   * cannot be given by a caller who was never shown the count.
   */
  protected confirmCascade(): void {
    const outstanding = this.pendingCascade();

    if (outstanding === null) {
      return;
    }

    this.pendingCascade.set(null);
    this.awaited.set({
      id: this.store.deleteProfileDefinition(
        outstanding.definition.propertyDefinitionId,
        true,
      ),
      kind: 'delete',
      propertyDefinitionId: outstanding.definition.propertyDefinitionId,
      propertyName: outstanding.definition.propertyName,
    });
  }

  // Private — derived state

  /**
   * The declarations whose staged edits genuinely differ from what the server holds. This is derived, not
   * orchestrated, and that is what makes a partial failure recoverable. {@link applyChanges} never clears
   * the staged edits; instead a staged edit stops counting the moment the server reports the same value.
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

  /**
   * The staged POSITION changes, which are written as one unit of work.
   *
   * ⚠ SPLIT FROM THE FIELD EDITS ON PURPOSE, AND THE SPLIT IS THE FIX. A move EXCHANGES the stored
   * positions of two declarations, so the two writes are only correct together: land one and lose the other
   * and both rows claim the same position, which is neither the order the operator started from nor the one
   * they asked for. Sending them as independent replacements — which is what this screen used to do — cannot
   * express that however precisely it reports which row failed.
   *
   * A row is included only when its position ACTUALLY differs from what the server reported, so toggling a
   * flag does not drag an unchanged position into the order write.
   */
  private readonly pendingPositions = computed<readonly ProfilePropertyDefinitionPosition[]>(() => {
    const edits: ReadonlyMap<number, GridEdits> = this.draft();

    if (edits.size === 0) {
      return [];
    }

    const positions: ProfilePropertyDefinitionPosition[] = [];

    for (const definition of this.store.profileDefinitions()) {
      const staged: GridEdits | undefined = edits.get(definition.propertyDefinitionId);

      if (staged !== undefined && staged.viewOrder !== definition.viewOrder) {
        positions.push({
          propertyDefinitionId: definition.propertyDefinitionId,
          viewOrder: staged.viewOrder,
        });
      }
    }

    return positions;
  });

  /**
   * The staged FIELD changes — required and visible — as complete replacement requests.
   *
   * These stay one request per declaration, and that is not an oversight. A flag is a fact about ONE
   * declaration and holds or fails on its own, so a refused row simply keeps the flag it had and there is no
   * relation between rows to corrupt. Keeping them per-row is what lets a five-row apply report WHICH three
   * declarations were refused rather than reporting only that something was.
   *
   * A row whose position also moved carries the moved position in its body, which is the value the order
   * write has already stored by the time this runs — so the two agree rather than fighting.
   */
  private readonly pendingFieldEdits = computed<readonly ProfileDefinitionEdit[]>(() => {
    const edits: ReadonlyMap<number, GridEdits> = this.draft();

    if (edits.size === 0) {
      return [];
    }

    const staged: ProfileDefinitionEdit[] = [];

    for (const definition of this.store.profileDefinitions()) {
      const pending: GridEdits | undefined = edits.get(definition.propertyDefinitionId);

      if (pending === undefined) {
        continue;
      }

      if (pending.required !== definition.required || pending.visible !== definition.visible) {
        staged.push({
          propertyDefinitionId: definition.propertyDefinitionId,
          request: toUpdateRequest(this.withDraft(definition, edits)),
        });
      }
    }

    return staged;
  });

  // PRIVATE — HELPERS

  /**
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
   * Forgets the row instances for declarations the server no longer reports. ⚠ WITHOUT THIS THE CACHE IS
   * A LEAK AND A CORRECTNESS HAZARD, in that order of visibility but the reverse order of importance.
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
   * Overlays a declaration's staged edits, if it has any. A NEW object is returned rather than the held
   * one mutated, because the contract's members are `readonly` and the held instance belongs to a store
   * several screens read. ⚠ THIS IS NOT THE GRID'S PROJECTION and must not be conflated with it.
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
   * Stages one declaration's grid-editable members. The map is REPLACED rather than mutated, so the
   * signal genuinely notifies; mutating the held map in place would leave every reader on the same
   * reference and nothing would recompute.
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
   * Exchanges a declaration's position with its neighbour's. this is `MoveProperty` exactly — read the
   * neighbour's position, SWAP the two, re-sort, and DO NOT SAVE. One move therefore stages TWO rows and
   * Apply issues TWO writes, which is why there is no reorder endpoint to call and none is invented:
   * position is a field on the declaration, so moving a row is a replace of two declarations.
   *
   * @param definition The declaration to move.
   * @param offset 1 to move earlier, 1 to move later.
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
   * This screen's own validation messages for one control. Order is rule order, and presence comes first
   * so that an omitted name reports "required" alone rather than stacking a format complaint on top of
   * it.
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

    // ⚠ ABSENCE ON A NUMERIC FIELD SHORT-CIRCUITS, EXACTLY AS TEXTUAL ABSENCE DOES ABOVE. Reporting "the
    // value is required" alongside "the value must be a whole number" for one empty box says the same thing
    // twice and buries the actionable half.
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

    return messages;
  }

  /**
   * Reports a settled single write, and closes the form when it succeeded. A failure whose operation is
   * not the one that was awaited is NOT this write's: a successful create triggers a re-read, and a
   * re-read that then failed must not be announced as a failed create.
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

    this.writeProblem.set(recorded.problem);

    // A CASCADE REFUSAL IS A QUESTION, NOT A FAILURE, so it re-opens the confirmation carrying the server's
    // count instead of being announced and left. The operator sees exactly what the server said and answers
    // it; declining leaves the declaration untouched, which is the same outcome as never having asked.
    //
    // The identity comes from the AWAITED write rather than from the dialog, which has already closed by the
    // time this runs, and it is matched against the list so the second attempt addresses a declaration the
    // screen can still see.
    if (recorded.code === VALUE_CASCADE_CODE && pending.kind === 'delete') {
      const target: ProfilePropertyDefinition | undefined = this.store
        .profileDefinitions()
        .find((candidate) => candidate.propertyDefinitionId === pending.propertyDefinitionId);

      if (target !== undefined) {
        this.pendingCascade.set({
          definition: target,
          message: stripLegacyBreakTags(recorded.summary.message),
        });

        // Deliberately NOT announced as a refusal as well. The dialog is modal and takes focus, so a
        // simultaneous notification would state the same sentence twice in two places and read as two
        // separate events.
        return;
      }
    }

    this.notifications.notify(
      recorded.summary.severity,
      refusalMessage(pending, recorded),
      recorded.summary.supportReference,
    );
  }

  /**
   * Reports one batch write's published outcome, naming every row the batch refused.
   *
   * @param settled The store's published result for the batch.
   */
  private reportBatchOutcome(settled: UserMutation): void {
    if (settled.operation !== AWAITED_OPERATION.applyEdits) {
      return;
    }

    // ⚠ ONE MESSAGE PER REFUSED ROW, EACH NAMING ITS OWN PROPERTY, AND THE LIST IS READ RATHER THAN THE
    // SETTLED FAILURE. The batch is one command with one settled result, which is what stops this screen
    // mistaking a sibling's outcome for its own — but a batch can refuse SEVERAL rows, and the settled
    // result carries only the first.
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
      // A refusal carrying no identifier is the ATOMIC ORDER WRITE's, so it is attributed to the order
      // itself and says plainly that nothing was applied — including the field edits, which are abandoned
      // rather than written over an order the server declined.
      if (refused.propertyDefinitionId === null) {
        this.notifications.notify(
          refused.failure.summary.severity,
          batchRefusalMessage(
            ORDER_REFUSAL_SUBJECT,
            `${stripLegacyBreakTags(refused.failure.summary.message)}${ORDER_REFUSAL_SUFFIX}`,
          ),
          refused.failure.summary.supportReference,
        );
        continue;
      }

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
   * Moves focus to the screen's primary action after a row has been removed. This fixes a defect measured
   * in a browser.
   */
  private restoreFocusAfterRemoval(): void {
    const anchor: ElementRef<HTMLButtonElement> | undefined = this.createTrigger;

    if (anchor === undefined) {
      return;
    }

    afterNextRender(
      () => {
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
   * Assembles the twelve columns, in the legacy grid's own order. The order is theirs: four commands
   * first, then eight data columns.
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

      // 2-3. `MoveDown` then `MoveUp`, in that order.
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
        // ⚠ ATOMIC BECAUSE A PROPERTY NAME IS NOT A PHRASE, AND WRAPPING ONE FRACTURES IT. The shared stylesheet
        // lets any cell break inside a word so a narrow column can never overflow, which is right for prose
        // and wrong for a value read as a single token. Measured before this line: `PostalCode` painted as `PostalCod` + `e` at a 768 viewport.
        // This grid declares no percentage anywhere and the four command columns ask for `min-content`, which
        // wins nothing under a fixed table layout - so all twelve columns collapse to an even one-twelfth share,
        // 80px at 768 and 86.5px at 1280, and no column here can ever be wider than that. The nine-character
        // names survive 80px; the ten-character one does not.
        // Marked atomic the value stays on one line and a column too narrow to hold it ellipsises instead, so
        // what shows is a recognisable prefix rather than two fragments that read as corruption. The whole
        // value stays in the accessibility tree and in the DOM either way, so this shortens what is painted
        // and hides nothing. No width changes - see the note on the width above for why rebalancing is not
        // the remedy here.
        atomic: true,
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

      // 6. `asp:TemplateColumn HeaderText="DataType"`.
      {
        key: 'dataType',
        label: DATA_TYPE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.dataTypeCellTemplate, 'dataTypeCell'),
      },

      {
        key: 'length',
        label: LENGTH_HEADING,
        headerAlign: 'center',
        bodyAlign: 'end',
        field: 'length',
      },

      // Template columns, for two reasons. Both members are nullable, and the shared bound column renders
      // text only, so a `null` would need formatting anyway.
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

      // 10-11. `dnn:checkboxcolumn DataField="Required"` and `="Visible"`, both `AutoPostBack="True"` — the
      // two columns the operator edits IN PLACE, and the reason this screen has a batch to apply at all.
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
   * Resolves a captured cell template, or fails loudly. The shared table declares `cellTemplate` as
   * REQUIRED on both the template and the actions kinds, so an absent one cannot be passed.
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
