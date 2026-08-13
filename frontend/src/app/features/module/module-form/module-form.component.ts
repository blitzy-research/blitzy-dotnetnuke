/**
 * The module create/edit screen. Replaces `Website/admin/Modules/modulesettings.ascx` and its code-behind
 * `Website/admin/Modules/ModuleSettings.ascx.vb` for the two operations the module write surface actually
 * exposes: placing a new module on a page, and replacing an existing placement.
 */

import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type Signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { Router } from '@angular/router';

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { ModuleStore } from '../../../core/state/module.store';
import { fieldErrorMessage } from '../../../core/utils/form-errors.util';
import {
  containedIconPathValidator,
  ICON_NOT_CONTAINED_ERROR,
  ICON_NOT_CONTAINED_MESSAGE,
} from '../../../core/utils/icon-reference.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type {
  CreateModuleRequest,
  ModuleDefinition,
  ModuleDetail,
  UpdateModuleRequest,
} from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type { ModuleStoreOperation } from '../../../core/state/module.store';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// =====================================================================================================
// NUMERIC INSTRUCTIONS THAT LOOK LIKE SENTINELS AND ARE NOT
// =====================================================================================================

const MODULE_ORDER_APPEND = -1;

const DEFAULT_CACHE_TIME_NOT_APPLICABLE = -1;

const CACHE_TIME_WHEN_BLANK = 0;

/**
 * The date the legacy null contract used to represent an unrecorded date, as it appears at the start of
 * an ISO 8601 instant. THE LEGACY MARKER SURVIVES ON THE WIRE AND MUST NOT REACH A CONTROL.
 * `Library/Components/Shared/Null.vb:L66-L70` defines the absent date as the minimum date, and the legacy
 * screen guarded both date fields with a test against it so that the marker rendered as an EMPTY box.
 */
const UNRECORDED_DATE = '0001-01-01';

/** The number of characters at the start of an ISO 8601 instant that spell the calendar date. */
const ISO_DATE_LENGTH = 10;

// =====================================================================================================
// ROUTES THIS SCREEN NAVIGATES TO
// =====================================================================================================

/**
 * Where the screen returns after a save, a removal or a cancellation. THE LEGACY SCREEN LEFT ON ALL THREE
 * OUTCOMES. Its update handler ended with `Response.Redirect(NavigateURL(), True)`, and its cancel
 * (`:L281`) and removal (`:L307`) handlers did the same, each returning to the administration page the
 * operator came from.
 */
const MODULE_LIST_PATH = '/modules';

// WORDING
// Every string below is taken from the VALUE of a resource entry, never from a markup attribute.

/** The fields this screen renders, and the keys its wording maps are keyed by. */
export type ModuleFormField =
  | 'moduleDefId'
  | 'friendlyName'
  | 'moduleTitle'
  | 'inheritViewPermissions'
  | 'allTabs'
  | 'header'
  | 'footer'
  | 'startDate'
  | 'endDate'
  | 'iconFile'
  | 'visibility'
  | 'displayTitle'
  | 'cacheTime'
  | 'tabId'
  | 'setAsDefaultSettings'
  | 'applyToAllModules';

/**
 * The visible label for each field, from the `pl*.Text` entries of
 * `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`. The inherit label is the one entry
 * whose legacy value carried markup - bold tags around two of its words - and it is reproduced as PLAIN
 * TEXT. The emphasis is lost; the string cannot become an injection vector.
 */
const FIELD_LABELS: Readonly<Record<ModuleFormField, string>> = Object.freeze({
  moduleDefId: 'Module:',
  friendlyName: 'Module:',
  moduleTitle: 'Title:',
  inheritViewPermissions: 'Inherit View permissions from Page',
  allTabs: 'Display Module On All Pages?',
  header: 'Header:',
  footer: 'Footer:',
  startDate: 'Start Date:',
  endDate: 'End Date:',
  iconFile: 'Icon:',
  visibility: 'Visibility:',
  displayTitle: 'Display Container?',
  cacheTime: 'Cache Time (secs):',
  tabId: 'Move To Page:',
  setAsDefaultSettings: 'Set As Default Settings?',
  applyToAllModules: 'Apply To All Modules?',
});

/**
 * The supporting text for each field, from the `pl*.Help` entries of the same resource file. Two entries
 * are net additions and are marked as such at their declaration, because the legacy resource file has no
 * wording for a create-mode definition selector: it never had one.
 */
const FIELD_HINTS: Readonly<Record<ModuleFormField, string>> = Object.freeze({
  // NET ADDITION. `plFriendlyName.Help` reads 'Displays the name of the module.', which describes a
  // read-only display rather than a choice, so it cannot serve the create-mode selector. Recorded as a
  // documented addition rather than reused misleadingly.
  moduleDefId: 'Choose the module to place on the page. This cannot be changed afterwards.',
  friendlyName: 'Displays the name of the module.',
  moduleTitle:
    'Enter a title for the Module.  This will appear in the Title Bar of the Container for this '
    + 'Module, if supported by the Container.',
  // `plPermissions.Help`. The legacy grid this sentence also described is a separate resource with
  // its own screen and is not rendered here, so only the inheriting half of the sentence applies.
  inheritViewPermissions:
    'Select the View and Edit permissions by checking/unchecking the boxes in the grid.  The module '
    + 'can inherit its permissions from the Page.  To do this check the Inherit View Permissions '
    + 'checkbox.',
  allTabs: 'Select whether the module should appear in the same location on all pages of the site',
  header: 'Enter header text for this Module',
  footer: 'Enter footer text for this Module',
  startDate:
    'Enter the start date for displaying this module.  You may use the Calendar to pick a date.',
  endDate: 'Enter the end date for displaying this module.  You may use the Calendar to pick a date.',
  iconFile: 'Select an Icon for this Module to display in the Title Bar',
  visibility: 'Choose the default visibility for this Module',
  displayTitle: 'Select this option if you would like to display the Module container.',
  cacheTime: 'Enter the time this object is kept in the Cache',
  tabId: 'Move this module instance to another Page.',
  setAsDefaultSettings:
    'Select this option if you would like the Page Settings for this module to be used as the '
    + 'default settings when adding new modules.',
  applyToAllModules:
    'Select this option if you would like the Page Settings for this module to be applied to all '
    + 'existing modules in the site.',
});

/** The heading shown when an existing module is being edited, from `ModuleSettings.Text`. */
const EDIT_HEADING = 'Module Settings';

/**
 * The heading shown when a module is being placed for the first time. MIGRATION: A NET ADDITION, because
 * the legacy resource file supplies no create-mode title.
 */
const CREATE_HEADING = 'Add Module';

/**
 * The explanatory sentence beneath the heading, from `ModuleSettingsHelp.Text`, reproduced verbatim
 * including its irregular spacing before the closing bracket.
 */
const EDIT_SUBHEADING =
  'In this section, you can define the settings that relate to the Module content and permissions '
  + '(ie. those settings that will be the same on all pages that the Module appears ).';

/** The explanatory sentence for create mode. A net addition for the same reason as {@link CREATE_HEADING}. */
const CREATE_SUBHEADING =
  'Choose a module and the page to place it on. The remaining settings can be changed afterwards.';

/**
 * The supporting text shown in place of the all-pages hint while that switch is withheld. A NET ADDITION,
 * and it exists because a disabled control carrying its ordinary help text explains what the control does
 * and not why it cannot be used.
 */
const ALL_TABS_WITHHELD_HINT =
  'Placing a module on every page reaches beyond the page in front of you, so it is available only to '
  + 'an administrator of this site. The module can still be placed on the page selected above.';

/** The validation message for an unparseable start date, from `valStartDate.ErrorMessage`. */
const START_DATE_INVALID_MESSAGE = 'Invalid Start Date';

/** The validation message for an unparseable end date, from `valEndDate.ErrorMessage`. */
const END_DATE_INVALID_MESSAGE = 'Invalid End Date';

/** The validation message for a non-integral cache period, from `valCacheTime.ErrorMessage`. */
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';

/** The longest module heading the contract accepts, in characters. */
const MODULE_TITLE_MAX_LENGTH = 256;

/**
 * The longest icon path the contract accepts, in characters. `IconFile nvarchar(100)`, again capped
 * identically by the create and update rules, and again unconstrained by the legacy markup: the legacy
 * affordance was a file-and-folder picker rather than a text box, so it had no length attribute to
 * reproduce.
 */
const MODULE_ICON_MAX_LENGTH = 100;

/** The opening of the blank-heading disclosure, up to the name itself. */
const TITLE_FALLBACK_PREFIX = 'With no heading, this module is listed as “';

/** The close of the blank-heading disclosure, after the name. @see TITLE_FALLBACK_PREFIX. */
const TITLE_FALLBACK_SUFFIX = '”, the name of its module definition.';

/**
 * The earliest instant SQL Server's `datetime` can store, as a calendar date. The two schedule bounds
 * land in `datetime` columns, whose domain begins on the first of January 1753 — a date that is not a
 * limitation of this application but of the type, and the API refuses anything outside it.
 */
const SQL_DATETIME_MINIMUM_DATE = '1753-01-01';

/**
 * The last instant SQL Server's `datetime` can store, as a calendar date. The type's domain ends at
 * 9999-12-31 23:59:59.997.
 */
const SQL_DATETIME_MAXIMUM_DATE = '9999-12-31';

/** The error key the schedule-bound representability rule reports. */
const UNSTORABLE_DATE_ERROR = 'unstorableDate';

const DATE_OUT_OF_RANGE_MESSAGE = `Enter a date between ${SQL_DATETIME_MINIMUM_DATE} and ${SQL_DATETIME_MAXIMUM_DATE}.`;

/** The label on the save affordance, from `cmdUpdate.Text` in the shared resource file. */
const SAVE_LABEL = 'Update';

/** The label on the abandon affordance, from `cmdCancel.Text`. */
const CANCEL_LABEL = 'Cancel';

/** The label on the removal affordance, from `cmdDelete.Text`. */
const DELETE_LABEL = 'Delete';

const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * The sentence shown when the operator submits a form that still carries a validation message. A net
 * addition.
 */
const FORM_INVALID_MESSAGE = 'Correct the highlighted fields and try again.';

/** The sentence shown when a submission names no module definition. */
const DEFINITION_REQUIRED_MESSAGE = 'Choose a module before saving.';

/** The sentence shown when a submission names no page. */
const PAGE_REQUIRED_MESSAGE = 'Choose a page before saving.';

/**
 * The sentence shown when an update is attempted before the module has been read. Load-bearing rather
 * than defensive: the update replaces the whole row, so submitting before the stored position and the
 * stored recycle-bin flag are known would move the module to the bottom of its pane and clear the flag as
 * a side effect of saving an unrelated field.
 */
const NOT_LOADED_MESSAGE = 'The module has not finished loading. Wait a moment and try again.';

const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a module that can be read. Return to the module list and try again.';

/** The sentence shown when the addressed module could not be found. */
const NOT_FOUND_MESSAGE = 'The module could not be found. It may have been removed.';

/** The one status that answers the question of existence in the negative. */
const NOT_FOUND_STATUS = 404;

/** The sentence shown after a module has been placed. */
const CREATED_MESSAGE = 'The module was added.';

/** The sentence shown after a module has been saved. */
const UPDATED_MESSAGE = 'The module settings were saved.';

/** The sentence shown after a module has been removed from its page. */
const DELETED_MESSAGE = 'The module was removed from the page.';

/**
 * The sentence shown when leaving the screen fails. A net addition with no legacy counterpart: the legacy
 * screen left by way of a server-side redirect, which either happened or replaced the response entirely,
 * so there was no failure to report.
 */
const NAVIGATION_FAILED_MESSAGE = 'The module list could not be opened.';

// =====================================================================================================
// THE VISIBILITY CHOICES
// =====================================================================================================

/** One choice offered by the visibility control. */
export interface ModuleVisibilityChoice {
  /** The code written into the bound control. */
  readonly value: ModuleVisibility;

  /** The text shown to the operator. */
  readonly label: string;
}

/** The three visibility choices, in the order the legacy radio group listed them. */
const VISIBILITY_CHOICES: readonly ModuleVisibilityChoice[] = Object.freeze([
  { value: ModuleVisibility.Maximized, label: 'Maximized' },
  { value: ModuleVisibility.Minimized, label: 'Minimized' },
  { value: ModuleVisibility.None, label: 'None' },
]);

// =====================================================================================================
// THE FORM SHAPE
// =====================================================================================================

/** The typed shape of this screen's form. Declared LOCALLY and never shared. */
export interface ModuleFormModel {
  /** The definition to instantiate. */
  moduleDefId: FormControl<number | null>;

  /** The page the placement should sit on: where it is going, not where it is. */
  tabId: FormControl<number | null>;

  /** The operator's title for this module. */
  moduleTitle: FormControl<string>;

  /** Whether the module appears in the same position on every page of the portal. */
  allTabs: FormControl<boolean>;

  /** Plain text rendered above the module's content. Rich text is not carried forward. */
  header: FormControl<string>;

  /** Plain text rendered below the module's content. Rich text is not carried forward. */
  footer: FormControl<string>;

  /** The calendar date from which the module is displayed, or the empty string for no restriction. */
  startDate: FormControl<string>;

  /** The calendar date until which the module is displayed, or the empty string for no restriction. */
  endDate: FormControl<string>;

  /** Whether the module takes its view permission from its page. */
  inheritViewPermissions: FormControl<boolean>;

  /** The placement's position within its pane. */
  moduleOrder: FormControl<number>;

  /** How long the placement's output may be cached, in seconds, as entered. */
  cacheTime: FormControl<string>;

  /** The icon shown with the module's title. */
  iconFile: FormControl<string>;

  /** How the placement is presented on its page. */
  visibility: FormControl<ModuleVisibility>;

  /** Whether the placement's container is displayed. */
  displayTitle: FormControl<boolean>;

  /** The instruction to adopt these settings as the portal's defaults for newly added modules. */
  setAsDefaultSettings: FormControl<boolean>;

  /** The instruction to copy this placement's appearance to every module in the portal. */
  applyToAllModules: FormControl<boolean>;
}

/** The form's value with disabled controls included. */
type ModuleFormValue = ReturnType<FormGroup<ModuleFormModel>['getRawValue']>;

/** The three store commands this screen dispatches. */
type ModuleFormOperation = Extract<
  ModuleStoreOperation,
  'createModule' | 'updateModule' | 'deleteModule'
>;

// PURE HELPERS

/** An optional sign followed by one or more digits, and nothing else. */
const INTEGER_PATTERN = /^[+-]?\d+$/;

/** A four-digit year, a two-digit month and a two-digit day, at the start of the value. */
const ISO_DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/;

/** How many characters the leading `yyyy-mm-dd` of an ISO 8601 value occupies. */
const CALENDAR_DATE_LENGTH = 10;

/**
 * Reads the module identifier the router supplied. THE LEGACY PARSE THREW. `ModuleSettings.ascx.vb:L449`
 * called `Int32.Parse` on the query-string value with no guard, so a mistyped address raised an exception
 * that the page's handler absorbed.
 *
 * @param supplied The route parameter as the router delivered it, or `undefined` when the matched route
 * declares none.
 * @returns The identifier, or `undefined` when none was supplied or the value could not be read.
 */
function parseModuleId(supplied: string | undefined): number | undefined {
  if (supplied === undefined) {
    return undefined;
  }

  const parsed = parseIntegerText(supplied);

  return parsed === null ? undefined : parsed;
}

/**
 * @param text The text to read.
 * @returns The integer, or `null` when the text does not spell one exactly.
 */
function parseIntegerText(text: string): number | null {
  const trimmed = text.trim();

  if (INTEGER_PATTERN.test(trimmed) === false) {
    return null;
  }

  const parsed = Number.parseInt(trimmed, 10);

  return Number.isSafeInteger(parsed) ? parsed : null;
}

/**
 * Whether text begins with a calendar date that actually exists. THE LEGACY CHECK WAS CULTURE-SENSITIVE,
 * TWICE OVER. The markup declared a comparison validator with `Operator="DataTypeCheck" Type="Date"`,
 * which resolved against the request's culture, and the handler then called `Convert.ToDateTime`, which
 * resolved against the server's.
 *
 * @param text The text to inspect.
 * @returns `true` when the leading ten characters spell a real calendar date.
 */
function startsWithCalendarDate(text: string): boolean {
  const matched = ISO_DATE_PATTERN.exec(text);

  if (matched === null) {
    return false;
  }

  const year = Number.parseInt(matched[1], 10);
  const month = Number.parseInt(matched[2], 10);
  const day = Number.parseInt(matched[3], 10);

  if (Number.isInteger(year) === false || Number.isInteger(month) === false) {
    return false;
  }

  if (Number.isInteger(day) === false) {
    return false;
  }

  const candidate = new Date(Date.UTC(2000, month - 1, day));
  candidate.setUTCFullYear(year);

  return (
    candidate.getUTCFullYear() === year
    && candidate.getUTCMonth() === month - 1
    && candidate.getUTCDate() === day
  );
}

/**
 * Narrows a stored instant to the calendar date a date control holds. The value is SLICED, never parsed
 * into an instant and reformatted.
 *
 * @param instant A stored ISO 8601 instant, or `null` when none is recorded.
 * @returns The leading calendar date, or the empty string when nothing should be shown.
 */
function toDateControlValue(instant: string | null): string {
  if (instant === null) {
    return '';
  }

  const calendarDate = instant.slice(0, ISO_DATE_LENGTH);

  return calendarDate === UNRECORDED_DATE ? '' : calendarDate;
}

/**
 * Projects a text control's value onto a nullable wire member. EMPTIED TEXT CLEARS THE COLUMN, AND
 * ENTERED TEXT IS TRANSPORTED EXACTLY AS ENTERED. The legacy null contract encoded absent text AS the
 * empty string, and an emptied legacy text box posted an empty value that cleared its column; the wire
 * member is nullable, so the empty string becomes `null` and clears the same column.
 *
 * @param value A text control's value.
 * @returns The value unchanged, or `null` when it is empty.
 */
function textOrNull(value: string): string | null {
  return value.length > 0 ? value : null;
}

/**
 * Rejects a date control's value when it does not spell a real calendar date.
 *
 * @param control The control to inspect.
 * @returns An error map when the value cannot be read as a date, or `null` when it can.
 */
function calendarDateValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value;

  if (value.length === 0) {
    return null;
  }

  if (!startsWithCalendarDate(value)) {
    return { invalidDate: true };
  }

  // Compared as TEXT rather than as instants, which is exact here and avoids a conversion: the leading ten
  // characters are a zero-padded `yyyy-mm-dd`, and that form sorts lexicographically in chronological
  // order.
  const date = value.slice(0, CALENDAR_DATE_LENGTH);

  if (date < SQL_DATETIME_MINIMUM_DATE || date > SQL_DATETIME_MAXIMUM_DATE) {
    return { [UNSTORABLE_DATE_ERROR]: true };
  }

  return null;
}

/**
 * @param control The control to inspect.
 * @returns An error map when the value is not integral, or `null` when it is.
 */
function integerValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value;

  if (value.trim().length === 0) {
    return null;
  }

  return parseIntegerText(value) === null ? { invalidInteger: true } : null;
}

/** The message each field shows for its own local rule, keyed by control name. */
const LOCAL_VALIDATION_MESSAGES: Readonly<Partial<Record<keyof ModuleFormModel, string>>> =
  Object.freeze({
    // The two required choices. Deliberately the SAME two sentences the imperative guards already report,
    // rather than field-specific rewordings: one rule must not be described two ways depending on which
    // mechanism happens to notice it first.
    moduleDefId: DEFINITION_REQUIRED_MESSAGE,
    tabId: PAGE_REQUIRED_MESSAGE,
    startDate: START_DATE_INVALID_MESSAGE,
    endDate: END_DATE_INVALID_MESSAGE,
    cacheTime: CACHE_TIME_INVALID_MESSAGE,
  });

/** The sentence to report for each completed command. */
const SUCCESS_MESSAGES: Readonly<Record<ModuleFormOperation, string>> = Object.freeze({
  createModule: CREATED_MESSAGE,
  updateModule: UPDATED_MESSAGE,
  deleteModule: DELETED_MESSAGE,
});

// =====================================================================================================
// THE SCREEN
// =====================================================================================================

@Component({
  selector: 'app-module-form',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    ErrorBannerComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './module-form.component.html',
  styleUrl: './module-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleFormComponent {
  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.saving() === false,
  );
  // DEPENDENCIES
  // Injected, never provided: this component declares no provider of its own, because every provider in the
  // application is registered once at application configuration.

  /** The module feature's signal store: the single source of every value this screen renders. */
  private readonly store = inject(ModuleStore);

  /**
   * The signed-in session, read for ONE thing: which tenant a newly placed module belongs to. ⚠ THE
   * CREATE ROUTE HAS NO OTHER SOURCE FOR IT, and that is why this dependency exists. The page picker's
   * options are portal-scoped, and on the edit route the portal is named by the module that was read.
   */
  private readonly session = inject(AuthStore);

  /** The transient message queue, used for command outcomes. */
  private readonly notifications = inject(NotificationService);

  private readonly router = inject(Router);

  // ---------------------------------------------------------------------------------------------------
  // THE ROUTE PARAMETER
  // ---------------------------------------------------------------------------------------------------

  /**
   * The module this screen addresses, or `undefined` on the create route. ⚠ THE NAME IS LOAD-BEARING AND
   * MUST NOT BE RENAMED. Route parameters are delivered into component inputs BY NAME, so a rename severs
   * the binding silently: the screen would compile, bundle and render, and would simply always behave as
   * though no module had been addressed.
   */
  readonly moduleId = input<string | undefined>(undefined);

  // THE FORM
  // Declared before every member that derives from it: class fields initialise in declaration order, so a
  // derivation placed above this one would read an uninitialised form.

  /**
   * The typed form backing every editable field. The initial value of each control IS the create-mode
   * default, which is why create mode needs no seeding step beyond a reset.
   */
  protected readonly form = new FormGroup<ModuleFormModel>({
    // `null` is "not chosen". It is NOT a sentinel: page zero and module zero are real, so no numeric value
    // could have carried this meaning.
    moduleDefId: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.required],
    }),
    tabId: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.required],
    }),
    moduleTitle: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MODULE_TITLE_MAX_LENGTH)],
    }),
    allTabs: new FormControl(false, { nonNullable: true }),
    header: new FormControl('', { nonNullable: true }),
    footer: new FormControl('', { nonNullable: true }),
    startDate: new FormControl('', { nonNullable: true, validators: [calendarDateValidator] }),
    endDate: new FormControl('', { nonNullable: true, validators: [calendarDateValidator] }),
    inheritViewPermissions: new FormControl(false, { nonNullable: true }),
    moduleOrder: new FormControl(MODULE_ORDER_APPEND, { nonNullable: true }),
    cacheTime: new FormControl('', { nonNullable: true, validators: [integerValidator] }),
    iconFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MODULE_ICON_MAX_LENGTH), containedIconPathValidator],
    }),
    visibility: new FormControl<ModuleVisibility>(ModuleVisibility.Maximized, {
      nonNullable: true,
    }),
    displayTitle: new FormControl(true, { nonNullable: true }),
    setAsDefaultSettings: new FormControl(false, { nonNullable: true }),
    applyToAllModules: new FormControl(false, { nonNullable: true }),
  });

  // ---------------------------------------------------------------------------------------------------
  // LOCAL STATE
  // ---------------------------------------------------------------------------------------------------

  /** Whether the operator has tried to save. */
  private readonly submitAttempted = signal(false);

  /** Whether the destructive confirmation is showing. */
  private readonly removalPending = signal(false);

  /** Whether a read of the addressed module has been dispatched. */
  private readonly loadRequested = signal(false);

  /**
   * The command awaiting its outcome, or `null` when none is in flight. A PLAIN FIELD rather than a
   * signal, deliberately: it is read inside the outcome effect, and making it reactive would re-run that
   * effect when it is cleared, which is the one moment it must not run again.
   */
  private pendingOperation: ModuleFormOperation | null = null;

  /** The module identifier a read has already been dispatched for. */
  private dispatchedKey: number | undefined = undefined;

  /** Whether {@link dispatchedKey} holds a dispatched value, as opposed to never having been set. */
  private hasDispatchedKey = false;

  /** The portal a page list has already been requested for. */
  private requestedTabPortalId: number | undefined = undefined;

  // ---------------------------------------------------------------------------------------------------
  // MODE, DERIVED STRUCTURALLY
  // ---------------------------------------------------------------------------------------------------

  /**
   * Whether the screen is editing an existing module. DERIVED FROM WHETHER THE ADDRESS CARRIES A MODULE
   * AT ALL, and never from the value it carries.
   */
  protected readonly isEditMode = computed<boolean>(() => this.moduleId() !== undefined);

  /** Whether the screen is placing a new module. */
  protected readonly isCreateMode = computed<boolean>(() => this.moduleId() === undefined);

  /**
   * The addressed module's identifier, or `undefined` when the address names none or names one that
   * cannot be read. The two reasons for `undefined` are told apart by {@link addressUnreadable}, and
   * every consumer that could act destructively on the difference consults that first.
   */
  protected readonly moduleKey = computed<number | undefined>(() => parseModuleId(this.moduleId()));

  /** Whether the address carries a module identifier that could not be read. */
  protected readonly addressUnreadable = computed<boolean>(
    () => this.moduleId() !== undefined && this.moduleKey() === undefined,
  );

  // ---------------------------------------------------------------------------------------------------
  // STORE PROJECTIONS
  // ---------------------------------------------------------------------------------------------------

  /** Whether the addressed module is being read. */
  protected readonly loading: Signal<boolean> = this.store.moduleLoading;

  /** Whether a create, replace or remove is in flight. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /** Whether the definition catalogue is being read. */
  protected readonly definitionsLoading: Signal<boolean> = this.store.definitionsLoading;

  /** Every definition the tenant may instantiate. A read-only catalogue with no write half. */
  protected readonly definitions: Signal<readonly ModuleDefinition[]> = this.store.definitions;

  /** The portal's pages, flat and unpaged, as the page picker's options. */
  protected readonly tabs: Signal<readonly TabListItem[]> = this.store.tabs;

  /** Whether the page list is being read. */
  protected readonly tabsLoading: Signal<boolean> = this.store.tabsLoading;

  /**
   * The portal whose pages the picker should offer, or null when no portal is known yet. ONE MEMBER, TWO
   * ANSWERS, AND THE ROUTE DECIDES WHICH. On the edit route the answer is the portal the addressed module
   * belongs to, because re-parenting must offer that portal's pages and no other.
   */
  /**
   * Whether the session reports that the caller administers the tenant it is signed in to. Read from the
   * session store's single answer to that question, which folds the host-account arm into the tenant's
   * own administrator designation exactly as the API's handler does.
   */
  protected readonly administersPortal: Signal<boolean> = this.session.administersCurrentPortal;

  private readonly tabSourcePortalId: Signal<number | null> = computed(() => {
    if (this.isEditMode()) {
      return this.loadedModule()?.portalId ?? null;
    }

    return this.session.portalId();
  });

  /** The problem document behind the current failure, or `null` when there is none. */
  protected readonly problem = computed<ProblemDetails | null>(() => {
    const failure = this.store.failure();

    return failure === null ? null : failure.problem;
  });

  /** The module this screen addresses, once it has been read. */
  protected readonly loadedModule = computed<ModuleDetail | null>(() => {
    const key = this.moduleKey();

    if (key === undefined) {
      return null;
    }

    const detail = this.store.module();

    if (detail === null) {
      return null;
    }

    return detail.moduleId === key ? detail : null;
  });

  /**
   * Whether the read of the addressed module FAILED for a reason other than the module being absent. A
   * REFUSAL IS NOT AN ABSENCE, and that distinction is the whole point of this member.
   */
  protected readonly readRefused = computed<boolean>(() => {
    if (this.isEditMode() === false || this.addressUnreadable()) {
      return false;
    }

    const failure = this.store.failure();

    if (failure === null || failure.operation !== 'loadModule') {
      return false;
    }

    return failure.summary.status !== NOT_FOUND_STATUS;
  });

  /** Whether the addressed module could not be found. */
  protected readonly moduleMissing = computed<boolean>(() => {
    if (this.isEditMode() === false || this.addressUnreadable()) {
      return false;
    }

    if (this.readRefused()) {
      return false;
    }

    if (this.loadRequested() === false || this.loading()) {
      return false;
    }

    return this.loadedModule() === null;
  });

  // ---------------------------------------------------------------------------------------------------
  // THE DEFINITION, AND THE CACHE ROW IT GOVERNS
  // ---------------------------------------------------------------------------------------------------

  /** The definition currently selected, tracked through the control rather than through a handler. */
  private readonly selectedDefinitionId: Signal<number | null> = toSignal(
    this.form.controls.moduleDefId.valueChanges,
    { initialValue: null },
  );

  /**
   * The selected definition, resolved from the catalogue or from a single read of it. `null` when nothing
   * is selected or the selection is not among the definitions held.
   */
  protected readonly selectedDefinition = computed<ModuleDefinition | null>(() => {
    const definitionId = this.selectedDefinitionId();

    if (definitionId === null) {
      return null;
    }

    for (const definition of this.definitions()) {
      if (definition.moduleDefId === definitionId) {
        return definition;
      }
    }

    const single = this.store.definition();

    return single !== null && single.moduleDefId === definitionId ? single : null;
  });

  protected readonly cacheTimeOffered = computed<boolean>(() => {
    const definition = this.selectedDefinition();

    return definition === null || definition.defaultCacheTime !== DEFAULT_CACHE_TIME_NOT_APPLICABLE;
  });

  /**
   * The definition's display name, shown read-only. THREE DIFFERENT NAMES EXIST AND THIS IS THE DISPLAY
   * ONE. The legacy class carried the definition's programmatic name, the definition's display name
   * (`:L374`) and the instance title the operator types (`:L194`), and the legacy screen bound the
   * DISPLAY name into the disabled box and the INSTANCE title into the editable one, in that order.
   */
  protected readonly definitionName = computed<string>(() => {
    const detail = this.loadedModule();

    if (detail !== null) {
      return detail.friendlyName === null ? '' : detail.friendlyName;
    }

    const definition = this.selectedDefinition();

    return definition === null ? '' : definition.friendlyName;
  });

  // ---------------------------------------------------------------------------------------------------
  // PRESENTATION
  // ---------------------------------------------------------------------------------------------------

  /** The screen's heading, chosen by mode. */
  protected readonly heading = computed<string>(() =>
    this.isEditMode() ? EDIT_HEADING : CREATE_HEADING,
  );

  /** The explanatory sentence beneath the heading, chosen by mode. */
  protected readonly subheading = computed<string>(() =>
    this.isEditMode() ? EDIT_SUBHEADING : CREATE_SUBHEADING,
  );

  protected readonly definitionSelectable = computed<boolean>(() => this.isCreateMode());

  protected readonly canDelete = computed<boolean>(() => this.loadedModule() !== null);

  /** Whether the destructive confirmation is showing. */
  protected readonly removalConfirmVisible = computed<boolean>(() => this.removalPending());

  /** Whether any request is in flight, which is what suppresses a second submission. */
  protected readonly busy = computed<boolean>(() => this.saving() || this.loading());

  /** The visible label for each field. */
  protected readonly labels = FIELD_LABELS;

  /** The supporting text for each field. */
  protected readonly hints = FIELD_HINTS;

  /** The sentence shown beside the all-pages switch while that switch is withheld. */
  protected readonly allTabsWithheldHint = ALL_TABS_WITHHELD_HINT;

  protected readonly titleMaxLength = MODULE_TITLE_MAX_LENGTH;

  protected readonly iconMaxLength = MODULE_ICON_MAX_LENGTH;

  protected readonly dateMinimum = SQL_DATETIME_MINIMUM_DATE;

  protected readonly dateMaximum = SQL_DATETIME_MAXIMUM_DATE;

  /**
   * What the operator is told when the end of the schedule precedes its start. AUTHORED, because the
   * legacy screen had nothing to say about this state — it could not detect it.
   */
  protected readonly reversedScheduleNotice =
    'The end of this schedule is earlier than its start, so the module will not be shown at any ' +
    'time. This is saved as entered.';

  /** What the module will be listed as while its heading is blank, or `null` when the heading is set. */
  protected titleFallbackNotice(): string | null {
    if (this.form.controls.moduleTitle.value.length > 0) {
      return null;
    }

    // The catalogue name of whichever definition applies — the loaded module's own when editing, the
    // picked one when creating. Empty means there is nothing truthful to offer, so nothing is said.
    const fallback = this.definitionName();

    if (fallback.length === 0) {
      return null;
    }

    return `${TITLE_FALLBACK_PREFIX}${fallback}${TITLE_FALLBACK_SUFFIX}`;
  }

  /**
   * Whether the entered schedule ends before it begins. A METHOD rather than a `computed`, deliberately,
   * and the reason is that this form's state lives in a `FormGroup` and not in signals: nothing here
   * bridges `valueChanges` into a signal, so a `computed` would have nothing reactive to depend on and
   * would latch its first answer forever.
   *
   * @returns `true` only when both bounds are real dates and the end precedes the start.
   */
  protected scheduleReversed(): boolean {
    const start: string = this.form.controls.startDate.value;
    const end: string = this.form.controls.endDate.value;

    if (start.length === 0 || end.length === 0) {
      return false;
    }

    const startAt: number = Date.parse(start);
    const endAt: number = Date.parse(end);

    if (Number.isNaN(startAt) || Number.isNaN(endAt)) {
      return false;
    }

    return endAt < startAt;
  }

  /** The three visibility choices. */
  protected readonly visibilityChoices = VISIBILITY_CHOICES;

  /** The label on the save affordance. */
  protected readonly saveLabel = SAVE_LABEL;

  /** The label on the abandon affordance. */
  protected readonly cancelLabel = CANCEL_LABEL;

  /** The label on the removal affordance. */
  protected readonly deleteLabel = DELETE_LABEL;

  /** The destructive confirmation's message. */
  protected readonly deleteConfirmMessage = DELETE_CONFIRM_MESSAGE;

  /** The sentence shown when the address names a module that cannot be read. */
  protected readonly unreadableAddressMessage = UNREADABLE_ADDRESS_MESSAGE;

  /** The sentence shown when the addressed module could not be found. */
  protected readonly notFoundMessage = NOT_FOUND_MESSAGE;

  // ---------------------------------------------------------------------------------------------------
  // WIRING
  // ---------------------------------------------------------------------------------------------------

  constructor() {
    // The definition catalogue backs the create-mode selector AND the cache-row rule in both modes, so it
    // is read once on arrival.
    this.store.loadDefinitions();

    effect(() => {
      const editing = this.isEditMode();
      const key = this.moduleKey();

      if (editing === false) {
        untracked(() => this.enterCreateMode());

        return;
      }

      if (key === undefined) {
        return;
      }

      if (this.hasDispatchedKey && this.dispatchedKey === key) {
        return;
      }

      this.hasDispatchedKey = true;
      this.dispatchedKey = key;

      untracked(() => {
        this.loadRequested.set(true);
        // EDIT MODE READS BEFORE IT WRITES, AND THAT IS MANDATORY RATHER THAN TIDY. The replacement is a
        // whole-row one, so the stored placement position and the stored recycle-bin flag have to be in
        // hand before any submission: omitting the position appends the module to the bottom of its pane
        // and omitting the flag clears it.
        this.store.loadModule(key);
      });
    });

    // ---------------------------------------------------------------------------------------------
    // Seeding the form from the module that was read.
    // ---------------------------------------------------------------------------------------------
    effect(() => {
      const detail = this.loadedModule();

      if (detail === null) {
        return;
      }

      untracked(() => this.seedFrom(detail));
    });

    effect(() => {
      const mayPlaceOnEveryPage = this.administersPortal();
      const control = this.form.controls.allTabs;

      untracked(() => {
        if (mayPlaceOnEveryPage) {
          if (control.disabled) {
            control.enable({ emitEvent: false });
          }

          return;
        }

        if (control.enabled) {
          control.disable({ emitEvent: false });
        }
      });
    });

    effect(() => {
      const portalId = this.tabSourcePortalId();

      if (portalId === null) {
        return;
      }

      if (this.requestedTabPortalId === portalId) {
        return;
      }

      this.requestedTabPortalId = portalId;

      untracked(() => this.store.loadTabs(portalId));
    });

    effect(() => {
      const inFlight = this.saving();
      const pending = this.pendingOperation;

      if (pending === null || inFlight) {
        return;
      }

      this.pendingOperation = null;

      const failure = untracked(() => this.store.failure());

      untracked(() => {
        if (failure === null) {
          this.reportSuccess(pending);

          return;
        }

        // THE SEVERITY IS THE SHARED UTILITY'S DECISION, NOT THIS SCREEN'S, and it is the measured one: a
        // refusal is a WARNING rather than an error, because the legacy denial page presented one with a
        // yellow warning in both of its branches, and presenting a refusal in danger styling would say
        // something is broken when the system is working as configured.
        this.notifications.notify(
          failure.summary.severity,
          failure.summary.message,
          failure.summary.supportReference,
        );
      });
    });
  }

  // ---------------------------------------------------------------------------------------------------
  // VALIDATION MESSAGES
  // ---------------------------------------------------------------------------------------------------

  /**
   * The messages to show beside one control: its own rule first, then the server's. A method rather than
   * a derivation because a reactive form's validity is not a signal; it is re-evaluated when the view is
   * checked, which under this change-detection strategy happens on the events the operator generates in
   * this view.
   *
   * @param controlName The control to report for.
   * @returns The messages, in order.
   */
  protected errorsFor(controlName: keyof ModuleFormModel): readonly string[] {
    const control: AbstractControl = this.form.controls[controlName];
    const messages: string[] = [];
    // ⚠ CHOSEN BY WHICH RULE FAILED, not by the control being invalid.
    const localMessage = this.localMessageFor(controlName, control);

    if (
      localMessage !== undefined
      && control.invalid
      && (control.touched || this.submitAttempted())
    ) {
      messages.push(localMessage);
    }

    const serverMessage = fieldErrorMessage(this.problem(), controlName);

    if (serverMessage !== null) {
      messages.push(serverMessage);
    }

    return messages;
  }

  /**
   * The wording for whichever local rule the control has broken. Ordered so that the more specific rule
   * wins: an unreadable value is described as unreadable even though it is also unstorable, because that
   * is the mistake the person actually made.
   *
   * @param controlName The control being reported for.
   * @param control That control, already resolved.
   * @returns The message, or `undefined` when the control has no local rule broken.
   */
  private localMessageFor(
    controlName: keyof ModuleFormModel,
    control: AbstractControl,
  ): string | undefined {
    if (control.hasError(UNSTORABLE_DATE_ERROR)) {
      return DATE_OUT_OF_RANGE_MESSAGE;
    }

    // Ordered before the length arm so the more specific rule wins. A rooted or upward-traversing reference
    // that also happens to be over-long is described as escaping the folder, because that is the mistake
    // that was actually made - shortening it would not make it acceptable.
    if (control.hasError(ICON_NOT_CONTAINED_ERROR)) {
      return ICON_NOT_CONTAINED_MESSAGE;
    }

    const overlong: unknown = control.errors?.['maxlength'];

    if (typeof overlong === 'object' && overlong !== null) {
      const bound: unknown = (overlong as { requiredLength?: number }).requiredLength;

      if (typeof bound === 'number') {
        return `Enter at most ${String(bound)} characters.`;
      }
    }

    return LOCAL_VALIDATION_MESSAGES[controlName];
  }

  // ---------------------------------------------------------------------------------------------------
  // COMMANDS
  // ---------------------------------------------------------------------------------------------------

  /** Saves the form: places a new module, or replaces the addressed one. */
  protected submit(): void {
    this.submitAttempted.set(true);

    if (this.busy()) {
      // A second submission while the first is outstanding would duplicate a placement, and the
      // endpoint answers a conflict rather than de-duplicating.
      return;
    }

    if (this.addressUnreadable()) {
      this.notifications.notify('warning', UNREADABLE_ADDRESS_MESSAGE);

      return;
    }

    this.form.markAllAsTouched();

    if (this.form.invalid) {
      this.notifications.notify('warning', FORM_INVALID_MESSAGE);

      return;
    }

    const raw: ModuleFormValue = this.form.getRawValue();
    const tabId = raw.tabId;

    // AN EXISTENCE TEST, NEVER A BOUND. `dbo.Tabs.TabID` is `IDENTITY(0, 1)`
    // (`01.00.00.SqlDataProvider:L140`), so page zero is an ordinary page and no comparison against zero,
    // against -1 or against truthiness could distinguish it from an unmade choice.
    if (tabId === null) {
      this.notifications.notify('warning', PAGE_REQUIRED_MESSAGE);

      return;
    }

    const cacheTime = this.resolveCacheTime(raw.cacheTime);

    if (cacheTime === null) {
      this.notifications.notify('warning', CACHE_TIME_INVALID_MESSAGE);

      return;
    }

    this.store.clearFailure();

    if (this.isCreateMode()) {
      this.createModule(raw, tabId, cacheTime);

      return;
    }

    this.replaceModule(raw, tabId, cacheTime);
  }

  /** Abandons the form and returns to the module list, as the legacy cancel handler did (`:L281`). */
  protected cancel(): void {
    this.returnToListing();
  }

  /**
   * Opens the destructive confirmation. the legacy screen attached a client-side confirmation to the
   * affordance itself (`:L205`), so the operator saw the shared prompt before the postback.
   */
  protected requestRemoval(): void {
    this.removalPending.set(true);
  }

  /** Dismisses the destructive confirmation without removing anything. */
  protected cancelRemoval(): void {
    this.removalPending.set(false);
  }

  /**
   * Removes this placement of the module. A PER-PAGE REMOVAL, WHICH IS WHY THE PLACEMENT IS NAMED. The
   * legacy affordance called `DeleteTabModule(TabId, ModuleId)`, which removes the module from ONE page
   * and soft-deletes the module itself only once its last placement is gone - not `DeleteModule`
   * (`:L819`), which removes it outright.
   */
  protected confirmRemoval(): void {
    this.removalPending.set(false);

    const detail = this.loadedModule();

    if (detail === null) {
      // The affordance is not offered in this state; the guard keeps a template that renders it anyway
      // from dispatching a removal for a module the screen has not read.
      this.notifications.notify('warning', NOT_LOADED_MESSAGE);

      return;
    }

    this.pendingOperation = 'deleteModule';
    this.store.deleteModule(detail.moduleId, detail.tabModuleId);
  }

  // ---------------------------------------------------------------------------------------------------
  // SUBMISSION, THE TWO SHAPES
  // ---------------------------------------------------------------------------------------------------

  /**
   * Places a new module, sending all fourteen members of the create contract.
   *
   * @param raw The form's value, disabled controls included.
   * @param tabId The page to place the module on, already proven present.
   * @param cacheTime The resolved cache period.
   */
  private createModule(raw: ModuleFormValue, tabId: number, cacheTime: number): void {
    const moduleDefId = raw.moduleDefId;

    // The same existence test as the page: `dbo.ModuleDefinitions.ModuleDefID` is `IDENTITY(1, 1)`, so no
    // legal value coincides with a legacy marker - but it is an opaque key and is still not compared
    // against a bound.
    if (moduleDefId === null) {
      this.notifications.notify('warning', DEFINITION_REQUIRED_MESSAGE);

      return;
    }

    const request: CreateModuleRequest = {
      moduleDefId,
      tabId,
      moduleTitle: textOrNull(raw.moduleTitle),
      allTabs: raw.allTabs,
      header: textOrNull(raw.header),
      footer: textOrNull(raw.footer),
      startDate: textOrNull(raw.startDate),
      endDate: textOrNull(raw.endDate),
      inheritViewPermissions: raw.inheritViewPermissions,
      // The declared initial value, which appends the module at the bottom of its pane. There is no
      // reorder affordance, so nothing else can have written it.
      moduleOrder: raw.moduleOrder,
      cacheTime,
      iconFile: textOrNull(raw.iconFile),
      visibility: raw.visibility,
      displayTitle: raw.displayTitle,
    };

    this.pendingOperation = 'createModule';
    this.store.createModule(request);
  }

  /**
   * Replaces the addressed module, sending all sixteen members of the replacement contract. THE MODULE
   * MUST HAVE BEEN READ FIRST, AND THE SUBMISSION IS REFUSED OTHERWISE. Two members come from the stored
   * row rather than from the operator - the placement position and the recycle-bin flag - and the
   * replacement writes every member it is given.
   *
   * @param raw The form's value, disabled controls included.
   * @param tabId The page whose placement is being replaced, already proven present.
   * @param cacheTime The resolved cache period.
   */
  private replaceModule(raw: ModuleFormValue, tabId: number, cacheTime: number): void {
    const detail = this.loadedModule();

    if (detail === null) {
      this.notifications.notify('warning', NOT_LOADED_MESSAGE);

      return;
    }

    const request: UpdateModuleRequest = {
      tabId: detail.tabId,
      moveToTabId: tabId === detail.tabId ? null : tabId,
      moduleTitle: textOrNull(raw.moduleTitle),
      allTabs: raw.allTabs,
      header: textOrNull(raw.header),
      footer: textOrNull(raw.footer),
      startDate: textOrNull(raw.startDate),
      endDate: textOrNull(raw.endDate),
      inheritViewPermissions: raw.inheritViewPermissions,
      isDeleted: detail.isDeleted,
      // MIGRATION: THE STORED POSITION, SENT BACK EXPLICITLY. The contract's default appends the module
      //   at the bottom of its pane, so omitting this would move a module every time its title changed.
      moduleOrder: raw.moduleOrder,
      cacheTime,
      iconFile: textOrNull(raw.iconFile),
      visibility: raw.visibility,
      displayTitle: raw.displayTitle,
      // Instructions rather than state. They are seeded unset on every visit and read from the form, so
      // an instruction the operator gave once is never reapplied by a later save.
      setAsDefaultSettings: raw.setAsDefaultSettings,
      applyToAllModules: raw.applyToAllModules,
    };

    this.pendingOperation = 'updateModule';
    // The placement is named so the store can replace the listed row rather than re-read the listing.
    // It is NOT transmitted: the page the replacement addresses is the request's own page member.
    this.store.updateModule(detail.moduleId, request, detail.tabModuleId);
  }

  // ---------------------------------------------------------------------------------------------------
  // PRIVATE HELPERS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Resolves the cache period a submission carries.
   *
   * @param entered The cache-period control's value.
   * @returns The period, or `null` when the text was entered but cannot be read as an integer.
   */
  private resolveCacheTime(entered: string): number | null {
    if (entered.trim().length === 0) {
      return CACHE_TIME_WHEN_BLANK;
    }

    return parseIntegerText(entered);
  }

  /**
   * Prepares the form for placing a new module. Resetting is what applies the create defaults, because
   * every control's declared initial value IS its create default.
   */
  private enterCreateMode(): void {
    this.form.controls.moduleDefId.enable({ emitEvent: false });
    this.form.reset();
    this.submitAttempted.set(false);
    this.removalPending.set(false);
  }

  /**
   * Seeds the form from a module that has been read. the legacy binding order is preserved where it is
   * observable - the definition's display name into the read-only field and the instance title into the
   * editable one - and the visibility is bound THROUGH the enumeration rather than into a list position,
   * which is what `:L134` did by assigning the stored code straight into a selected index.
   *
   * @param detail The module that was read.
   */
  private seedFrom(detail: ModuleDetail): void {
    // Written before the control is disabled, so the definition derivations see the value. A disabled
    // control is still reported by the raw read, and the definition is never sent on a replacement.
    this.form.controls.moduleDefId.setValue(detail.moduleDefId);
    this.form.controls.moduleDefId.disable({ emitEvent: false });

    this.form.patchValue({
      tabId: detail.tabId,
      moduleTitle: detail.moduleTitle === null ? '' : detail.moduleTitle,
      allTabs: detail.allTabs,
      header: detail.header === null ? '' : detail.header,
      footer: detail.footer === null ? '' : detail.footer,
      startDate: toDateControlValue(detail.startDate),
      endDate: toDateControlValue(detail.endDate),
      inheritViewPermissions: detail.inheritViewPermissions,
      moduleOrder: detail.moduleOrder,
      cacheTime: detail.cacheTime.toString(10),
      iconFile: detail.iconFile === null ? '' : detail.iconFile,
      visibility: detail.visibility,
      displayTitle: detail.displayTitle,
      // Instructions are seeded UNSET rather than echoed back, because echoing one would reapply a
      // portal-wide action the operator asked for once on every subsequent save.
      setAsDefaultSettings: false,
      applyToAllModules: false,
    });

    this.form.markAsPristine();
    this.form.markAsUntouched();
    this.submitAttempted.set(false);
  }

  /** @param operation The command that completed. */
  private reportSuccess(operation: ModuleFormOperation): void {
    // Marked for EVERY completed operation rather than only the write: after a removal the placement is
    // gone, so entry still standing in the controls is work that can no longer be saved. Marking an
    // already-pristine form is a no-op, so one unconditional pair is narrower than a per-operation test.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // The measured legacy severity vocabulary had exactly three levels and a completed operation used
    // the affirmative one.
    this.notifications.notify('success', SUCCESS_MESSAGES[operation], null, true);

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
    // retires notifications on a completed navigation and `returnToListing` navigates in this same task, so
    // the message was raised and swept before it could be painted.
    this.returnToListing(true);
  }

  /** Navigates to the module listing. */
  private returnToListing(replaceEntry = false): void {
    const departure = replaceEntry
      ? this.router.navigateByUrl(MODULE_LIST_PATH, { replaceUrl: true })
      : this.router.navigateByUrl(MODULE_LIST_PATH);

    departure.catch(() => {
      this.notifications.notify('error', NAVIGATION_FAILED_MESSAGE);
    });
  }
}
