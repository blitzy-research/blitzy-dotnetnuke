import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  Input,
  Output,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import { MODULE_VISIBILITY, ModuleVisibility } from '../../../core/models/module.model';
import type { ModuleDetail, ModuleSettingsBag, UpdateModuleRequest } from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { SelectOption } from '../../../core/models/select-option.model';
import type { TabListItem } from '../../../core/models/tab.model';
import { NotificationService } from '../../../core/services/notification.service';
import { PermissionService } from '../../../core/services/permission.service';
import { AuthStore } from '../../../core/state/auth.store';
import { ModuleStore } from '../../../core/state/module.store';
import {
  CONFLICT,
  NOT_FOUND,
  conflictMessage,
  problemSeverity,
  problemSupportReference,
  fieldErrorMessages,
} from '../../../core/utils/form-errors.util';
import {
  containedIconPathValidator,
  ICON_NOT_CONTAINED_ERROR,
  ICON_NOT_CONTAINED_MESSAGE,
} from '../../../core/utils/icon-reference.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

/**
 * The seven disclosure regions this screen presents, named after the legacy section heads they replace.
 * Three are top level and four are nested one level beneath them, reproducing the two-level hierarchy of
 * `Website/admin/Modules/modulesettings.ascx`: dshModule holds dshDetails and dshSecurity; dshPage holds
 * dshAppearance and dshOther; dshSpecific stands alone.
 */
export type ModuleSettingsSection =
  | 'moduleSettings'
  | 'details'
  | 'security'
  | 'pageSettings'
  | 'appearance'
  | 'other'
  | 'specificSettings';

export interface ModuleSettingsSeed {
  /**
   * The module being edited. `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so **0 is a real module** and is
   * never read as absent.
   */
  readonly moduleId: number;

  /** The placement being edited. */
  readonly tabModuleId: number;

  /** The page this placement sits on. `dbo.Tabs.TabID` is `IDENTITY(0, 1)`, so **0 is a real page**. */
  readonly tabId: number;

  /** The tenant this module belongs to, or `null` for a host-level module. */
  readonly portalId: number | null;

  readonly moduleDefId: number;

  /** The definition's own name, shown read-only. */
  readonly friendlyName: string | null;

  /** The operator-supplied heading, or `null` when none is recorded. */
  readonly moduleTitle: string | null;

  /** The module's position within its pane. */
  readonly moduleOrder: number;

  /** Whether the module appears on every page. */
  readonly allTabs: boolean;

  /** The soft-delete marker. */
  readonly isDeleted: boolean;

  /** Whether View permission is inherited from the page. */
  readonly inheritViewPermissions: boolean;

  /** Text rendered above the module's content. */
  readonly header: string | null;

  /** Text rendered below the module's content. */
  readonly footer: string | null;

  /** The display start instant, or `null`. May carry the legacy date sentinel. */
  readonly startDate: string | null;

  /** The display end instant, or `null`. May carry the legacy date sentinel. */
  readonly endDate: string | null;

  readonly cacheTime: number | null;

  /** The icon shown with the title. */
  readonly iconFile: string | null;

  /** How the placement is presented. */
  readonly visibility: ModuleVisibility;

  /** Whether the container chrome is displayed. */
  readonly displayTitle: boolean;
}

interface ModuleSettingsFormModel {
  /**
   * The page this placement is addressed on - the legacy "Move To Page:" picker (`cboTab`). THE MOVE IS
   * EXPRESSED AS A FIELD ON THE UPDATE, NEVER AS AN INVENTED ENDPOINT. The legacy relocation was a second
   * call, `MoveModule(ModuleId, TabId, newTabId, "")`, fired after the update at
   * `ModuleSettings.ascx.vb:L403-L408`.
   */
  tabId: FormControl<number>;

  /**
   * The operator-supplied heading (`txtTitle`). CARRIES NO VALIDATOR, AND THE ABSENCE IS THE REQUIREMENT.
   * A census of `Website/admin/Modules/` finds 0 `RequiredFieldValidator`, 0 `RegularExpressionValidator`
   * and 0 `RangeValidator`; the only validators in the whole feature are four `CompareValidator`s, and
   * none of them targets `txtTitle`.
   */
  moduleTitle: FormControl<string>;

  /** Round-tripped, not rendered: reordering has no endpoint. */
  moduleOrder: FormControl<number>;

  /** Round-tripped: the bulk "all pages" affordance has no endpoint, but the stored value must survive. */
  allTabs: FormControl<boolean>;

  /** Round-tripped: permission grants are a separate read-only resource here. */
  inheritViewPermissions: FormControl<boolean>;

  /** Text above the module's content (`txtHeader`, `TextMode="MultiLine"` rows=6). */
  header: FormControl<string>;

  /** Text below the module's content (`txtFooter`, `TextMode="MultiLine"` rows=6). */
  footer: FormControl<string>;

  /** Free text validated by a date data-type check (`txtStartDate`, `valtxtStartDate`). */
  startDate: FormControl<string>;

  /** Free text validated by a date data-type check (`txtEndDate`, `valtxtEndDate`). */
  endDate: FormControl<string>;

  /** Round-tripped: the legacy file picker and its file-system endpoint are both out of scope. */
  iconFile: FormControl<string>;

  /** One of exactly three codes (`cboVisibility`, values 0, 1 and 2). */
  visibility: FormControl<ModuleVisibility>;

  /** Whether the container chrome renders (`chkDisplayTitle`, labelled "Display Container?"). */
  displayTitle: FormControl<boolean>;

  /** Free text validated by an integer data-type check (`txtCacheTime`, `valCacheTime`). */
  cacheTime: FormControl<string>;

  /** An instruction, never echoed back from the module. */
  setAsDefaultSettings: FormControl<boolean>;

  /** An instruction, never echoed back from the module. */
  applyToAllModules: FormControl<boolean>;
}

/** The bound on `dbo.Modules.ModuleTitle` (`nvarchar(256) NULL`), advertised as an attribute only. */
const MODULE_TITLE_MAX_LENGTH = 256;

/** The bound on `dbo.TabModules.IconFile` (`nvarchar(100) NULL`), advertised as an attribute only. */
const ICON_FILE_MAX_LENGTH = 100;

/** `txtCacheTime` carries `maxlength="6"`; the bound is reproduced rather than widened. */
const CACHE_TIME_MAX_LENGTH = 6;

/** `txtStartDate` and `txtEndDate` both carry `maxlength="11"`. */
const DATE_MAX_LENGTH = 11;

/** The inclusive bounds of a 32-bit signed integer. */
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

/** The error key the two date data-type checks report. */
const DATE_TYPE_ERROR = 'dateDataType';

/** The error key the two integer data-type checks report. */
const INTEGER_TYPE_ERROR = 'integerDataType';

/** The regions that start closed. */
const INITIALLY_COLLAPSED: readonly ModuleSettingsSection[] = [
  'security',
  'pageSettings',
  'other',
  'specificSettings',
];

/**
 * Section headings, taken from `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`. the
 * resource file overrides the inline `title=` attribute the markup declares, and the two disagree in one
 * place that matters - the markup calls the second nested region "Security Settings" while
 * `Security.Text` is 'Advanced Settings'.
 */
const SECTION_HEADINGS: Readonly<Record<ModuleSettingsSection, string>> = {
  moduleSettings: 'Module Settings',
  details: 'Basic Settings',
  security: 'Advanced Settings',
  pageSettings: 'Page Settings',
  appearance: 'Basic Settings',
  other: 'Advanced Settings',
  specificSettings: 'Module Specific Settings',
};

/**
 * The three explanatory paragraphs, one per top-level region. reproduced character for character from the
 * resource file.
 */
const SECTION_INTROS: Readonly<Partial<Record<ModuleSettingsSection, string>>> = {
  moduleSettings:
    'In this section, you can define the settings that relate to the Module content and permissions '
    + '(ie. those settings that will be the same on all pages that the Module appears ).',
  pageSettings:
    'In this section, you can define settings specific to this particular occurrence of the Module for '
    + 'this Page.',
  specificSettings: 'In this section, you can set up settings that are specific for this module.',
};

const FIELD_LABELS = {
  friendlyName: 'Module:',
  moduleTitle: 'Title:',
  permissions: 'Permissions:',
  inheritViewPermissions: 'Inherit View permissions from Page',
  tabId: 'Move To Page:',
  allTabs: 'Display Module On All Pages?',
  header: 'Header:',
  footer: 'Footer:',
  startDate: 'Start Date:',
  endDate: 'End Date:',
  iconFile: 'Icon:',
  visibility: 'Visibility:',
  displayTitle: 'Display Container?',
  cacheTime: 'Cache Time (secs):',
  setAsDefaultSettings: 'Set As Default Settings?',
  applyToAllModules: 'Apply To All Modules?',
} as const;

/** The keys of {@link FIELD_HINTS} and {@link FIELD_LABELS}. */
export type ModuleSettingsField = keyof typeof FIELD_LABELS;

/**
 * The migrated help text, one entry per field, taken from the `pl*.Help` entries of
 * `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`. WHY THESE ARE CONSTANTS AND NOT
 * TEMPLATE TEXT: Angular compiles templates with `preserveWhitespaces` disabled, which collapses every
 * run of whitespace inside a text node to a single space.
 */
const FIELD_HINTS: Readonly<Record<ModuleSettingsField, string>> = {
  friendlyName: 'Displays the name of the module.',
  moduleTitle:
    'Enter a title for the Module.  This will appear in the Title Bar of the Container for this Module, '
    + 'if supported by the Container.',
  permissions:
    'Select the View and Edit permissions by checking/unchecking the boxes in the grid.  The module can '
    + 'inherit its permissions from the Page.  To do this check the Inherit View Permissions checkbox.',
  inheritViewPermissions:
    'Select the View and Edit permissions by checking/unchecking the boxes in the grid.  The module can '
    + 'inherit its permissions from the Page.  To do this check the Inherit View Permissions checkbox.',
  tabId: 'Move this module instance to another Page.',
  allTabs: 'Select whether the module should appear in the same location on all pages of the site',
  header: 'Enter header text for this Module',
  footer: 'Enter footer text for this Module',
  startDate: 'Enter the start date for displaying this module.  You may use the Calendar to pick a date.',
  endDate: 'Enter the end date for displaying this module.  You may use the Calendar to pick a date.',
  iconFile: 'Select an Icon for this Module to display in the Title Bar',
  visibility: 'Choose the default visibility for this Module',
  displayTitle: 'Select this option if you would like to display the Module container.',
  cacheTime: 'Enter the time this object is kept in the Cache',
  setAsDefaultSettings:
    'Select this option if you would like the Page Settings for this module to be used as the default '
    + 'settings when adding new modules.',
  applyToAllModules:
    'Select this option if you would like the Page Settings for this module to be applied to all existing '
    + 'modules in the site.',
};

// THE FOUR DATA-TYPE VALIDATORS
// THE ONLY VALIDATORS THIS SCREEN EVER HAD WERE FOUR `CompareValidator`s, AND ALL FOUR ARE REPRODUCED AS
// REAL ANGULAR VALIDATORS. A case-insensitive census of `Website/admin/Modules/` returns
// `asp:RequiredFieldValidator` 0, `asp:RegularExpressionValidator` 0, `asp:CompareValidator` 4,
// `asp:CustomValidator` 0, `asp:RangeValidator` 0 and `asp:ValidationSummary` 0.

/** `valStartDate.ErrorMessage`, without the layout break tag the resource carries. */
const START_DATE_INVALID_MESSAGE = 'Invalid Start Date';

/** `valEndDate.ErrorMessage`, without the layout break tag the resource carries. */
const END_DATE_INVALID_MESSAGE = 'Invalid End Date';

/** `valCacheTime.ErrorMessage`, without the layout break tag the resource carries. */
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';

/** `valBorder.ErrorMessage`, without the layout break tag the resource carries. */
const BORDER_INVALID_MESSAGE = 'Invalid Border (must be a number between 0 and 9)';

/**
 * @param message The wording to report, taken verbatim from the resource file.
 * @returns A validator reporting {@link INTEGER_TYPE_ERROR} with that wording.
 */
function integerDataTypeCheck(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const text = typeof control.value === 'string' ? control.value.trim() : '';

    if (text.length === 0) {
      return null;
    }

    if (!/^[+-]?\d+$/.test(text)) {
      return { [INTEGER_TYPE_ERROR]: message };
    }

    const parsed = Number(text);

    // Number() on a digits-only string is exact up to 2^53, well beyond the 32-bit range being tested, so
    // the range test below is the whole of what `Int32.Parse` would have rejected.
    return parsed >= INT32_MIN && parsed <= INT32_MAX ? null : { [INTEGER_TYPE_ERROR]: message };
  };
}

/**
 * Reproduces `asp:CompareValidator Operator="DataTypeCheck" Type="Date"`. Blank passes, for the reason
 * given on {@link integerDataTypeCheck}.
 *
 * @param message The wording to report, taken verbatim from the resource file.
 * @returns A validator reporting {@link DATE_TYPE_ERROR} with that wording.
 */
function dateDataTypeCheck(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const text = typeof control.value === 'string' ? control.value.trim() : '';

    if (text.length === 0) {
      return null;
    }

    const parts = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);

    if (parts !== null) {
      return isCalendarDate(parts[1], parts[2], parts[3]) ? null : { [DATE_TYPE_ERROR]: message };
    }

    return Number.isNaN(Date.parse(text)) ? { [DATE_TYPE_ERROR]: message } : null;
  };
}

/**
 * Whether a `yyyy`, `mm`, `dd` triple names a day that exists.
 *
 * @param year The four-digit year.
 * @param month The two-digit month, 1-based as written.
 * @param day The two-digit day.
 * @returns `true` when the triple round-trips unchanged.
 */
function isCalendarDate(year: string, month: string, day: string): boolean {
  const y = Number(year);
  const m = Number(month);
  const d = Number(day);

  if (m < 1 || m > 12 || d < 1 || d > 31) {
    return false;
  }

  const probe = new Date(Date.UTC(y, m - 1, d));

  return (
    probe.getUTCFullYear() === y && probe.getUTCMonth() === m - 1 && probe.getUTCDate() === d
  );
}

/**
 * Whether an instant carries the legacy null-date sentinel. THE COMPARISON IS ON THE UTC DATE PART ALONE,
 * BECAUSE THAT IS WHAT THE LEGACY COMPARISON WAS. `Library/Components/Shared/Null.vb` sets `NullDate` to
 * `Date.MinValue` and its `IsNull` overload for dates compares `objDate.Date.Equals(NullDate.Date)` under
 * the source's own comment about avoiding "subtle time differences".
 *
 * @param instant An ISO 8601 instant, or `null`.
 * @returns `true` when the value is absent or is the sentinel.
 */
function isNullDate(instant: string | null): boolean {
  if (instant === null || instant.trim().length === 0) {
    return true;
  }

  const parsed = new Date(instant);

  if (Number.isNaN(parsed.getTime())) {
    return true;
  }

  return (
    parsed.getUTCFullYear() === 1 && parsed.getUTCMonth() === 0 && parsed.getUTCDate() === 1
  );
}

/**
 * Narrows an instant to the date a date-capable control expects. The value is SLICED, never parsed, once
 * the sentinel test has passed.
 *
 * @param instant An ISO 8601 instant, or `null`.
 * @returns The leading `yyyy-mm-dd`, or an empty string for an absent or sentinel value.
 */
function toDateInputValue(instant: string | null): string {
  if (isNullDate(instant) || instant === null) {
    return '';
  }

  return instant.slice(0, 10);
}

/**
 * Collapses emptied text to the wire's `null`.
 *
 * @param value The control's text.
 * @returns The trimmed text, or `null` when nothing was entered.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length > 0 ? trimmed : null;
}

/**
 * Whether two property maps hold different content. Compared in BOTH directions rather than by size and
 * then by lookup: a map that lost one key and gained another has the same size as the map it came from,
 * so a one-way walk would report the pair identical.
 *
 * @param read The map as the server reported it.
 * @param candidate The map that would be written.
 * @returns `true` when the two differ in any key or any value.
 */
function mapsDiffer(
  read: Readonly<Record<string, string>>,
  candidate: Readonly<Record<string, string>>,
): boolean {
  const readKeys = Object.keys(read);

  if (readKeys.length !== Object.keys(candidate).length) {
    return true;
  }

  for (const key of readKeys) {
    if (!Object.prototype.hasOwnProperty.call(candidate, key)) {
      return true;
    }

    if (read[key] !== candidate[key]) {
      return true;
    }
  }

  return false;
}

/**
 * Resolves a route-supplied identifier to a number. EVERY GUARD HERE IS `undefined`-BASED, NEVER
 * TRUTH-BASED, AND THAT IS A SCHEMA CONSTRAINT. `dbo.Modules.ModuleID` and `dbo.Tabs.TabID` are both
 * `IDENTITY(0, 1)` (01.00.00.SqlDataProvider L221 and L140), so **0 is the first real row of each
 * table**.
 *
 * @param value A route parameter, a query parameter, or a number a caller assigned directly.
 * @returns The identifier, or `undefined` when none was supplied or the value was not an integer.
 */
function parseIdentifier(value: number | string | null | undefined): number | undefined {
  if (value === null || value === undefined) {
    return undefined;
  }

  if (typeof value === 'number') {
    return Number.isInteger(value) ? value : undefined;
  }

  const text = value.trim();

  if (!/^[+-]?\d+$/.test(text)) {
    return undefined;
  }

  const parsed = Number(text);

  return parsed >= INT32_MIN && parsed <= INT32_MAX ? parsed : undefined;
}

/** The label on the destructive confirmation's accept affordance. */
const DELETE_CONFIRM_LABEL = 'Delete';

/** The shared wording, reproduced verbatim, used when the module carries no name to interpolate. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** The message shown when no module has resolved. */
const NO_MODULE_MESSAGE = 'No module settings are available.';

/** The caption for the advisory list of keys this module's definition declares. */
const DEFINITION_PERMISSION_KEYS_LABEL = 'Permissions defined for this module type:';

/** Confirmation wording for a completed save, at success severity. */
const SAVED_MESSAGE = 'The module settings were saved.';

/** Confirmation wording for a completed removal, at success severity. */
const REMOVED_MESSAGE = 'The module was removed from this page.';

/**
 * The visibility choices, from `modulesettings.ascx:L144-L148`. THE THREE ORDINALS ARE LOAD-BEARING AND
 * `0` IS MEANINGFUL. `ModuleInfo.vb:L30-L34` declared a public visibility enumeration with `Maximized`,
 * `Minimized` and `None` and NO explicit values, so the implicit 0, 1 and 2 are the stored codes and the
 * markup's `value="0|1|2"` matches them exactly.
 */
const VISIBILITY_CHOICES: readonly SelectOption<ModuleVisibility>[] = [
  { value: MODULE_VISIBILITY.maximized, label: 'Maximized' },
  { value: MODULE_VISIBILITY.minimized, label: 'Minimized' },
  { value: MODULE_VISIBILITY.none, label: 'None' },
];

const NO_CACHE_DEFAULT = -1;

/**
 * The status a refusal arrives with. Surfaced at WARNING severity rather than error, on the legacy
 * screen's own precedent: the whole of `Website/admin/Security/AccessDenied.ascx.vb` is presentation,
 * performs no permission check of its own, and both branches of its `Page_Load` raise
 * `ModuleMessage.ModuleMessageType.YellowWarning`.
 */
const FORBIDDEN_STATUS = 403;

/** The status an unresolved identifier arrives with. */
const NOT_FOUND_STATUS = 404;

/** The status a rejected write arrives with. */
const CONFLICT_STATUS = 409;

/** The route this screen returns to when the operator finishes or abandons. */
const MODULE_LIST_ROUTE = '/modules';

/**
 * The screen's heading when a caller supplies none. Taken from `ModuleSettings.ascx.resx`'s own
 * `ModuleSettings.Text`, which is the wording the legacy screen showed above its first section head.
 */
const DEFAULT_HEADING = 'Module Settings';

/** One stored setting, as the module-specific section displays it. */
interface ModuleSpecificSettingRow {
  /** The setting's name, bounded at 50 characters by its column. */
  readonly name: string;

  /** The stored value, bounded at 2000 characters by its column. Rendered as text and never as markup. */
  readonly value: string;

  /** Which scope the value belongs to, already worded for display. */
  readonly scope: string;
}

/**
 * How a setting recorded against the module itself is described. The wording states the CONSEQUENCE
 * rather than naming the table, because that is what determines whether an operator should care: a
 * module-scoped value is the same wherever the module appears.
 */
const NO_SPECIFIC_SETTINGS_MESSAGE = 'This module has no stored settings of its own.';

/** The opening of the blank-heading disclosure, up to the name itself. */
const TITLE_FALLBACK_PREFIX = 'With no heading, this module is listed as “';

/** The close of the blank-heading disclosure, after the name. @see TITLE_FALLBACK_PREFIX. */
const TITLE_FALLBACK_SUFFIX = '”, the name of its module definition.';

const MODULE_SCOPE_LABEL = 'this module, on every page';

/** How a setting recorded against one placement is described. */
const PLACEMENT_SCOPE_LABEL = 'this placement only';

/**
 * The label used for the occupied page when the tenant's page list does not contain it. Reached when the
 * module sits on a page the list omits - a host-level module, whose `portalId` is `null` and for which no
 * portal page list is read, or a page in the recycle bin.
 */
const CURRENT_PAGE_LABEL = 'This page';

/**
 * The module settings screen, replacing `Website/admin/Modules/modulesettings.ascx` and its code-behind.
 * Mounted at `modules/:moduleId/settings`.
 */
@Component({
  selector: 'app-module-settings',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ErrorBannerComponent,
    ConfirmDialogComponent,
    FormFieldComponent,
  ],
  templateUrl: './module-settings.component.html',
  styleUrl: './module-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleSettingsComponent {
  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.saving === false,
  );
  // COLLABORATORS
  // Resolved with `inject()` into private readonly fields rather than through constructor parameters, and
  // no `providers` array is declared: every provider this screen relies on is registered once in
  // `app.config.ts`, and a component-level provider would give this screen a private copy of shared state.

  /** The single place `ModuleService` and `TabService` are reached from. */
  private readonly store = inject(ModuleStore);

  /** The advisory surface. */
  private readonly notifications = inject(NotificationService);

  /** Used only to return to the listing, which is what the legacy redirect at L421 did. */
  private readonly router = inject(Router);

  /** The catalogue transport, read for the advisory key list beside the inherit switch. */
  private readonly permissions = inject(PermissionService);

  /**
   * The session, read only to decide whether the catalogue may be asked for at all. The `/permissions`
   * endpoint is declared under the administrator policy, so a caller without tenant administration
   * receives a 403.
   */
  private readonly authStore = inject(AuthStore);

  /** Bounds the catalogue subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  // -----------------------------------------------------------------------------------------------------
  // THE FORM
  // -----------------------------------------------------------------------------------------------------

  protected readonly form = new FormGroup<ModuleSettingsFormModel>({
    tabId: new FormControl(0, { nonNullable: true }),
    moduleTitle: new FormControl('', { nonNullable: true }),
    moduleOrder: new FormControl(0, { nonNullable: true }),
    allTabs: new FormControl(false, { nonNullable: true }),
    inheritViewPermissions: new FormControl(false, { nonNullable: true }),
    header: new FormControl('', { nonNullable: true }),
    footer: new FormControl('', { nonNullable: true }),
    startDate: new FormControl('', {
      nonNullable: true,
      validators: [dateDataTypeCheck(START_DATE_INVALID_MESSAGE)],
    }),
    endDate: new FormControl('', {
      nonNullable: true,
      validators: [dateDataTypeCheck(END_DATE_INVALID_MESSAGE)],
    }),
    iconFile: new FormControl('', {
      nonNullable: true,
      validators: [containedIconPathValidator],
    }),
    visibility: new FormControl<ModuleVisibility>(MODULE_VISIBILITY.maximized, {
      nonNullable: true,
    }),
    displayTitle: new FormControl(true, { nonNullable: true }),
    cacheTime: new FormControl('', {
      nonNullable: true,
      validators: [integerDataTypeCheck(CACHE_TIME_INVALID_MESSAGE)],
    }),
    setAsDefaultSettings: new FormControl(false, { nonNullable: true }),
    applyToAllModules: new FormControl(false, { nonNullable: true }),
  });

  // -----------------------------------------------------------------------------------------------------
  // LOCAL STATE
  // -----------------------------------------------------------------------------------------------------

  /** The module addressed by the route, or `undefined` before the parameter resolves. */
  private readonly addressedModuleId = signal<number | undefined>(undefined);

  /** The placement addressed by a query parameter, or `undefined` to address the module itself. */
  private readonly addressedTabModuleId = signal<number | undefined>(undefined);

  /** The state a caller seeded the form from, or `null` to follow the loaded module. */
  private readonly seeded = signal<ModuleSettingsSeed | null>(null);

  /**
   * The picker's options, derived from the tenant's page list and the occupied page. A `computed()`
   * rather than a field an effect recomputes: the options are a pure function of the two signals they
   * read, so deriving them reactively means they can never fall out of step, and no effect can overwrite
   * a list a caller supplied.
   */
  private readonly derivedPages = computed<readonly SelectOption<number>[]>(() => {
    const detail = this.store.module();

    return buildPageOptions(this.store.tabs(), detail === null ? undefined : detail.tabId);
  });

  /** The resolved heading. */
  private headingText: string = DEFAULT_HEADING;

  /** A caller-pinned loading flag, or `null` to follow the store. */
  private pinnedLoading: boolean | null = null;

  /** A caller-pinned saving flag, or `null` to follow the store. */
  private pinnedSaving: boolean | null = null;

  /** A caller-pinned removal affordance, or `null` to follow the loaded module. */
  private pinnedCanDelete: boolean | null = null;

  /** A caller-pinned page list, or `null` to follow the tenant's pages. */
  private pinnedPages: readonly SelectOption<number>[] | null = null;

  /**
   * The placement the form was last seeded for, so a store refresh does not discard an edit in progress.
   * `undefined` means "nothing seeded yet".
   */
  private seededPlacement: number | undefined = undefined;

  /** Whether the destructive confirmation is showing. */
  private readonly removalOpen = signal(false);

  /** The problem document to surface in the banner, or `null`. */
  private readonly currentProblem = signal<ProblemDetails | null>(null);

  /** Whether a caller has assigned {@link canManageAllPages} explicitly. */
  private privilegeAssigned = false;

  /** The backing field for {@link canManageAllPages}. */
  private allPagesManageable = false;

  /** The identity the definition lookup was last issued for, so it is issued once per definition. */
  private definitionRequested: number | undefined = undefined;

  /** The tenant the page lookup was last issued for, so it is issued once per tenant. */
  private tabsRequested: number | undefined = undefined;

  /**
   * The permission keys this module's DEFINITION declares, or `null` when the list is not available. ⚠
   * `null` AND THE EMPTY ARRAY MEAN DIFFERENT THINGS, and the template distinguishes them. `null` is "not
   * read" — the caller does not administer the tenant, the read has not returned yet, or it failed — and
   * renders nothing at all.
   */
  private readonly declaredPermissionKeys = signal<readonly string[] | null>(null);

  /** The definition the catalogue read was last issued for, so it is issued once per definition. */
  private permissionsRequested: number | undefined = undefined;

  /** The failure already surfaced, so one refusal produces one advisory. */
  private failureSurfaced: ProblemDetails | null = null;

  /**
   * The settings bag exactly as the server last reported it, or `null` before one has been read. The
   * reference {@link settingsBagDiffersFromRead} compares against, which is what lets a save send the
   * property maps only when this screen has actually altered them.
   */
  private settingsAsRead: ModuleSettingsBag | null = null;

  /** Whether a submission raised FROM THIS SCREEN is still outstanding. */
  private readonly submissionPending = signal(false);

  /** Whether a removal raised FROM THIS SCREEN is still awaiting its answer. */
  private readonly removalOutstanding = signal(false);

  // -----------------------------------------------------------------------------------------------------
  // ROUTE INPUTS
  // -----------------------------------------------------------------------------------------------------

  /**
   * The module to edit, supplied by the `:moduleId` route parameter. THE NAME IS A CONTRACT.
   * `app.config.ts` enables `withComponentInputBinding()`, which binds a route parameter onto an input of
   * the same name; renaming this input would break the binding SILENTLY, with no compile error and no
   * runtime message - the screen would simply never resolve a module.
   *
   * @param value The route parameter, or a number a caller assigns directly.
   */
  @Input()
  public set moduleId(value: number | string | null | undefined) {
    this.addressedModuleId.set(parseIdentifier(value));
  }

  /** The module being edited, or `undefined` before the route parameter resolves. */
  public get moduleId(): number | undefined {
    return this.addressedModuleId();
  }

  /**
   * The single placement to address, supplied by an optional `tabModuleId` query parameter.
   *
   * @param value The query parameter, or a number a caller assigns directly.
   */
  @Input()
  public set tabModuleId(value: number | string | null | undefined) {
    this.addressedTabModuleId.set(parseIdentifier(value));
  }

  /** The placement being addressed, or `undefined` when the module itself is. */
  public get tabModuleId(): number | undefined {
    return this.addressedTabModuleId();
  }

  // PRESENTATION INPUTS

  /**
   * The screen's heading.
   *
   * @param value The heading to show, or `null`/`undefined`/blank to take the default.
   */
  @Input()
  public set heading(value: string | null | undefined) {
    const candidate = typeof value === 'string' ? value.trim() : '';

    // The shared page header REFUSES a blank title - it is rendered as the page's single h1 and an unnamed
    // heading is an accessibility defect - so a blank must resolve to the default here rather than reach it.
    this.headingText = candidate.length > 0 ? candidate : DEFAULT_HEADING;
  }

  /** The screen's heading. */
  public get heading(): string {
    return this.headingText;
  }

  /**
   * The module and placement being edited, or `null` when none has resolved.
   *
   * @param value The state to show.
   */
  @Input()
  public set settings(value: ModuleSettingsSeed | null | undefined) {
    const supplied = value ?? null;

    this.seeded.set(supplied);

    if (supplied !== null) {
      this.seededPlacement = supplied.tabModuleId;
      this.seed(supplied);
    }
  }

  /** The module and placement being edited, or the loaded module, or `null`. */
  public get settings(): ModuleSettingsSeed | null {
    const explicit = this.seeded();

    return explicit !== null ? explicit : this.store.module();
  }

  /**
   * Whether the module is still being fetched. Both reads matter: the settings bag is fetched alongside
   * the module and a save replaces it, so showing the form before it has arrived would let a save empty
   * settings the screen never displayed.
   *
   * @param value Whether to report the screen as loading, pinning the value.
   */
  @Input()
  public set loading(value: boolean | null | undefined) {
    this.pinnedLoading = typeof value === 'boolean' ? value : null;
  }

  /** Whether the module is still being fetched. */
  public get loading(): boolean {
    return this.pinnedLoading ?? (this.store.moduleLoading() || this.store.settingsLoading());
  }

  /**
   * Whether a submission is in flight.
   *
   * @param value Whether to report the screen as saving, pinning the value.
   */
  @Input()
  public set saving(value: boolean | null | undefined) {
    this.pinnedSaving = typeof value === 'boolean' ? value : null;
  }

  /** Whether a submission is in flight. */
  public get saving(): boolean {
    return this.pinnedSaving ?? (this.store.saving() || this.store.settingsSaving());
  }

  /**
   * Whether a removal affordance should be offered. A module that has resolved can be removed - the
   * removal endpoint exists and the whole route is already gated on the module-edit policy - so the
   * affordance follows the module rather than being defaulted off.
   *
   * @param value Whether to offer removal, pinning the value.
   */
  @Input()
  public set canDelete(value: boolean | null | undefined) {
    this.pinnedCanDelete = typeof value === 'boolean' ? value : null;
  }

  /** Whether a removal affordance should be offered. */
  public get canDelete(): boolean {
    return this.pinnedCanDelete ?? (this.store.module() !== null);
  }

  /**
   * The portal pages offered by the "Move To Page" picker.
   *
   * @param value The pages to offer, pinning the list.
   */
  @Input()
  public set pages(value: readonly SelectOption<number>[] | null | undefined) {
    this.pinnedPages = Array.isArray(value) ? value : null;
  }

  /** The pages offered by the picker: the caller's list when one was supplied, otherwise the tenant's. */
  public get pages(): readonly SelectOption<number>[] {
    return this.pinnedPages ?? this.derivedPages();
  }

  /**
   * Whether the caller may change the settings that reach beyond the page being edited.
   *
   * @param value Whether the far-reaching controls are editable.
   */
  @Input()
  public set canManageAllPages(value: boolean | null | undefined) {
    if (typeof value !== 'boolean') {
      return;
    }

    this.privilegeAssigned = true;
    this.allPagesManageable = value;
    this.applyPrivilegeLocks();
  }

  /** Whether the caller may change the far-reaching controls. */
  public get canManageAllPages(): boolean {
    return this.allPagesManageable;
  }

  // OUTPUTS

  /** Emits the submitted document when the operator accepts the form. */
  @Output() public readonly save = new EventEmitter<UpdateModuleRequest>();

  /** Emits when the operator abandons the form. */
  @Output() public readonly cancel = new EventEmitter<void>();

  /** Emits when the operator confirms a removal. */
  @Output() public readonly remove = new EventEmitter<void>();

  // ORCHESTRATION

  /** Issues the reads for the addressed module. */
  private readonly loadAddressedModule = effect(() => {
    const id = this.addressedModuleId();

    // `=== undefined` and never a truth test: module 0 is a real module.
    if (id === undefined) {
      return;
    }

    const placement = this.addressedTabModuleId();

    this.store.loadModule(id, placement);
    this.store.loadSettings(id, placement);
  });

  /** Records the settings bag as read, which is the reference a save compares against. */
  private readonly recordSettingsAsRead = effect(() => {
    const bag = this.store.settings();

    this.settingsAsRead = bag;
  });

  /**
   * Seeds the form from the loaded module. This one genuinely IS a side effect - a form group is not
   * reactive state and has to be written to - which is why it is an effect while the read-only
   * presentation values are getters.
   */
  private readonly adoptLoadedModule = effect(() => {
    const detail = this.store.module();

    if (detail === null) {
      return;
    }

    if (!this.privilegeAssigned) {
      this.allPagesManageable = true;
      this.applyPrivilegeLocks();
    }

    if (this.seededPlacement !== detail.tabModuleId) {
      this.seededPlacement = detail.tabModuleId;
      this.seed(detail);
    }
  });

  /**
   * The loaded module, but ONLY once it is the module this screen's address names. ⚠ WITHOUT THIS GUARD,
   * EVERY MOVE BETWEEN TWO MODULES READ THE PREVIOUS ONE'S DEFINITION, AND RUNTIME TESTING MEASURED IT.
   * The store's module slot still holds the module left behind until the new address's detail read
   * answers, so an effect keyed on that slot fires once with the OLD `moduleDefId`.
   */
  private readonly addressedDetail = computed<ModuleDetail | null>(() => {
    const detail = this.store.module();

    if (detail === null) {
      return null;
    }

    const addressed = this.addressedModuleId();

    // `=== undefined` and never a truth test: module 0 is a real module.
    if (addressed === undefined) {
      return detail;
    }

    return detail.moduleId === addressed ? detail : null;
  });

  private readonly loadOwningDefinition = effect(() => {
    const detail = this.addressedDetail();

    if (detail === null) {
      return;
    }

    if (this.definitionRequested === detail.moduleDefId) {
      return;
    }

    this.definitionRequested = detail.moduleDefId;
    this.store.loadDefinition(detail.moduleDefId);
  });

  /**
   * Fetches the tenant's pages for the "Move To Page" picker. The tenant is taken from the loaded module
   * rather than from the address, because the route does not carry it.
   */
  private readonly loadTenantPages = effect(() => {
    const detail = this.store.module();

    if (detail === null || detail.portalId === null) {
      return;
    }

    if (this.tabsRequested === detail.portalId) {
      return;
    }

    this.tabsRequested = detail.portalId;
    this.store.loadTabs(detail.portalId);
  });

  /**
   * Reads the permission keys the loaded module's DEFINITION declares, for the advisory list rendered
   * beside the inherit switch. ⚠ THIS IS AN ADVISORY READ AND ITS FAILURE IS NOT THIS SCREEN'S FAILURE.
   * The keys explain what the inherit switch is choosing between — the legacy screen sat directly above a
   * permission grid, and without any indication of the vocabulary in play the switch reads as a bare
   * boolean with no subject.
   */
  private readonly loadDeclaredPermissionKeys = effect(() => {
    const detail = this.addressedDetail();

    if (detail === null || !this.authStore.holdsPortalAdministration()) {
      return;
    }

    if (this.permissionsRequested === detail.moduleDefId) {
      return;
    }

    this.permissionsRequested = detail.moduleDefId;

    this.permissions
      .list({ moduleDefinitionId: detail.moduleDefId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.declaredPermissionKeys.set(response.data);
        },
        error: () => {
          // Deliberately silent, and deliberately not a re-throw: see the advisory note above. The
          // signal stays `null`, so the region renders nothing and the screen is unaffected.
          this.declaredPermissionKeys.set(null);
        },
      });
  });

  private readonly surfaceFailure = effect(() => {
    const failure = this.store.failure();

    if (failure === null) {
      this.failureSurfaced = null;
      this.currentProblem.set(null);
      return;
    }

    const problem = failure.problem;

    if (problem !== null && problem === this.failureSurfaced) {
      return;
    }

    this.failureSurfaced = problem;
    this.announce(failure.operation, problem, failure.code);
  });

  /**
   * Concludes a submission once BOTH writes have settled, then reports and leaves. the legacy redirect at
   * `ModuleSettings.ascx.vb:L421` - `Response.Redirect(NavigateURL(), True)`, commented "Navigate back to
   * admin page" - sat INSIDE the `If Page.IsValid Then` / `Try` block after `UpdateModule` had returned,
   * so a postback that threw fell through to `Catch` and never redirected.
   */
  private readonly concludeSubmission = effect(() => {
    const pending = this.submissionPending();
    const writing = this.store.saving() || this.store.settingsSaving();

    if (!pending || writing) {
      return;
    }

    // Both commands clear the failure as they begin, so a document present now belongs to this submission.
    const failed = this.store.failure() !== null;

    this.submissionPending.set(false);

    if (failed) {
      return;
    }

    // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS ALREADY
    // SAVED - AND DESTROYS THE CONFIRMATION BELOW WHILE IT ASKS. The probe registered on this class reads
    // `dirty && saving === false`, and the `writing` test above has already established that the write is
    // no longer in flight, so from here on the probe sees a dirty form with no write outstanding - and
    // `returnToListing()` on the last line is a navigation it can refuse.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // The announcement and the departure are raised HERE, not at the point of submission, because
    // `Response.Redirect(NavigateURL(), True)` at L421 ran after `UpdateModule` had returned and a throwing
    // postback never reached it.
    this.notifications.notify('success', SAVED_MESSAGE, null, true);

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
    // retires notifications on a completed navigation, and this one is raised in the same task as the
    // departure below - so it was swept before it could be painted.
    this.returnToListing(true);
  });

  private readonly concludeRemoval = effect(() => {
    const pending = this.removalOutstanding();
    const writing = this.store.saving();

    if (!pending || writing) {
      return;
    }

    const failure = this.store.failure();

    this.removalOutstanding.set(false);

    // Matched on the OPERATION, not on mere presence: the store re-reads the listing after a successful
    // removal, and that re-read's failure must not be reported as the removal's.
    if (failure !== null && failure.operation === 'deleteModule') {
      return;
    }

    // ⚠ SETTLED BEFORE LEAVING FOR THE REASON RECORDED ON THE SAVE PATH ABOVE, and it applies to a removal
    // too: an operator who typed into the form and then removed the placement would be asked to confirm
    // discarding edits to a placement that no longer exists.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    this.notifications.notify('success', REMOVED_MESSAGE, null, true);

    // Exempted from the navigation sweep, for the reason recorded on the save path above.
    this.remove.emit();
    this.returnToListing(true);
  });

  // -----------------------------------------------------------------------------------------------------
  // BOUND CONSTANTS
  // -----------------------------------------------------------------------------------------------------

  /** The section headings, exposed for binding. */
  protected readonly headings = SECTION_HEADINGS;

  /** The three explanatory paragraphs, exposed for binding. */
  protected readonly intros = SECTION_INTROS;

  /** The field labels, exposed for binding. */
  protected readonly labels = FIELD_LABELS;

  /** The migrated help text, exposed for binding. */
  protected readonly hints = FIELD_HINTS;

  /** The visibility choices. */
  protected readonly visibilityChoices = VISIBILITY_CHOICES;

  /** The label on the destructive confirmation's accept affordance. */
  protected readonly deleteConfirmLabel = DELETE_CONFIRM_LABEL;

  /** The message shown when no module has resolved. */
  protected readonly noModuleMessage = NO_MODULE_MESSAGE;

  /** `valCacheTime`'s wording, for the template's message slot. */
  protected readonly cacheTimeInvalidMessage = CACHE_TIME_INVALID_MESSAGE;

  protected readonly iconNotContainedMessage = ICON_NOT_CONTAINED_MESSAGE;

  protected readonly noSpecificSettingsMessage = NO_SPECIFIC_SETTINGS_MESSAGE;

  protected get titleFallbackNotice(): string | null {
    if (this.form.controls.moduleTitle.value.length > 0) {
      return null;
    }

    // The RESOLVED view model, for the same reason `deleteConfirmMessage` reads it that way: the routed
    // path never assigns the input, so reading the input directly would find nothing on every routed visit.
    const current = this.settings;
    const fallback = current === null ? null : current.friendlyName;

    if (fallback === null || fallback.length === 0) {
      return null;
    }

    return `${TITLE_FALLBACK_PREFIX}${fallback}${TITLE_FALLBACK_SUFFIX}`;
  }

  /**
   * The stored settings this module carries, as rows for the module-specific section to display. ⚠ WHY
   * THIS EXISTS: THE SECTION WAS DISCARDING REAL DATA IN SILENCE. The screen already reads the settings
   * bag — `loadSettings` runs on arrival and {@link persistSettings} writes both maps back whole — but
   * the module-specific region rendered nothing except a projection slot no routed use ever fills.
   */
  protected get specificSettingRows(): readonly ModuleSpecificSettingRow[] {
    const bag = this.store.settings();
    const addressed = this.addressedModuleId();

    if (bag === null || addressed === undefined || bag.moduleId !== addressed) {
      return [];
    }

    // Sorted by name within each scope so the order is stable between reads. A map's enumeration order
    // follows insertion, which follows whatever order the response happened to arrive in.
    const moduleScoped: ModuleSpecificSettingRow[] = Object.keys(bag.moduleSettings)
      .sort((left, right) => left.localeCompare(right))
      .map((name) => ({
        name,
        value: bag.moduleSettings[name] ?? '',
        scope: MODULE_SCOPE_LABEL,
      }));

    const placementScoped: ModuleSpecificSettingRow[] = Object.keys(bag.tabModuleSettings)
      .sort((left, right) => left.localeCompare(right))
      .map((name) => ({
        name,
        value: bag.tabModuleSettings[name] ?? '',
        scope: PLACEMENT_SCOPE_LABEL,
      }));

    return [...moduleScoped, ...placementScoped];
  }

  /**
   * Whether this module carries any stored settings to disclose. Read by the template to choose between
   * the disclosure and the explicit "none recorded" statement.
   */
  protected get hasSpecificSettings(): boolean {
    return this.specificSettingRows.length > 0;
  }

  /** `valStartDate`'s wording, for the template's message slot. */
  protected readonly startDateInvalidMessage = START_DATE_INVALID_MESSAGE;

  /** `valEndDate`'s wording, for the template's message slot. */
  protected readonly endDateInvalidMessage = END_DATE_INVALID_MESSAGE;

  /**
   * `valBorder`'s wording. Retained with its rule even though the border column is not transported, so
   * the pair survives together; see {@link BORDER_INVALID_MESSAGE}.
   */
  protected readonly borderInvalidMessage = BORDER_INVALID_MESSAGE;

  protected readonly moduleTitleTooLongMessage = 'Title must be 256 characters or fewer.';

  /** The column and control bounds the template advertises through `maxlength`. */
  protected readonly limits = {
    moduleTitle: MODULE_TITLE_MAX_LENGTH,
    iconFile: ICON_FILE_MAX_LENGTH,
    cacheTime: CACHE_TIME_MAX_LENGTH,
    startDate: DATE_MAX_LENGTH,
    endDate: DATE_MAX_LENGTH,
  } as const;

  // -----------------------------------------------------------------------------------------------------
  // DERIVED VIEW STATE
  // -----------------------------------------------------------------------------------------------------

  /** The regions currently closed. */
  protected readonly collapsed = new Set<ModuleSettingsSection>(INITIALLY_COLLAPSED);

  /** Whether the destructive confirmation is showing. */
  protected get removalPending(): boolean {
    return this.removalOpen();
  }

  /**
   * The problem document the banner renders, or `null` when there is nothing to report. An outstanding
   * SETTINGS refusal takes precedence, because it is the refusal that decides whether this screen has
   * anything to offer: while one is outstanding the form is withheld, so no write can be in flight and
   * there is no rejected-write document competing for the banner.
   */
  protected readonly problem = computed<ProblemDetails | null>(
    () => this.settingsRefusal() ?? this.currentProblem(),
  );

  private readonly settingsRefusal = computed<ProblemDetails | null>(() => {
    const failure = this.store.settingsFailure();

    if (failure === null || failure.summary.status === NOT_FOUND_STATUS) {
      return null;
    }

    return failure.problem;
  });

  protected readonly settingsRefused = computed<boolean>(() => {
    const failure = this.store.settingsFailure();

    return failure !== null && failure.summary.status !== NOT_FOUND_STATUS;
  });

  protected readonly showCacheField = computed<boolean>(() => {
    const definition = this.store.definition();

    if (definition === null) {
      return true;
    }

    return definition.defaultCacheTime !== NO_CACHE_DEFAULT;
  });

  /** Whether the addressed module resolved to nothing. */
  protected readonly notFound = computed<boolean>(
    () =>
      this.addressedModuleId() !== undefined
      && this.store.module() === null
      && !this.store.moduleLoading()
      && !this.readRefused(),
  );

  /**
   * Whether one of this screen's two reads FAILED for a reason other than the module being absent. A
   * REFUSAL IS NOT AN ABSENCE. The server answers 403 when the caller may not see the module and 404 when
   * there is no such module; both leave this screen holding nothing, so a test for "nothing in hand"
   * cannot tell them apart.
   */
  protected readonly readRefused = computed<boolean>(() => {
    if (this.addressedModuleId() === undefined) {
      return false;
    }

    const failure = this.store.failure();

    if (failure === null) {
      return false;
    }

    if (failure.operation !== 'loadModule' && failure.operation !== 'loadSettings') {
      return false;
    }

    return failure.summary.status !== NOT_FOUND_STATUS;
  });

  /** The wording for a not-found module, from the shared vocabulary. */
  protected readonly notFoundMessage = NOT_FOUND;

  /** The permission keys this module's definition declares, or `null` when the list is not available. */
  protected readonly definitionPermissionKeys = this.declaredPermissionKeys.asReadonly();

  /** The caption for the advisory key line. */
  protected readonly definitionPermissionKeysLabel = DEFINITION_PERMISSION_KEYS_LABEL;

  // -----------------------------------------------------------------------------------------------------
  // TEMPLATE HELPERS
  // -----------------------------------------------------------------------------------------------------

  /**
   * Whether a region is currently open.
   *
   * @param section The region.
   * @returns `true` when the region's body should render.
   */
  protected isExpanded(section: ModuleSettingsSection): boolean {
    return !this.collapsed.has(section);
  }

  /**
   * Opens a closed region or closes an open one. the legacy toggle was reachable by pointer only -
   * `sectionheadcontrol.ascx` rendered its toggle with `tabIndex="-1"` (defect D-M12) and
   * `labelcontrol.ascx`'s help toggle was likewise unreachable from the keyboard (defect D11).
   *
   * @param section The region to toggle.
   */
  protected toggle(section: ModuleSettingsSection): void {
    if (this.collapsed.has(section)) {
      this.collapsed.delete(section);
    } else {
      this.collapsed.add(section);
    }
  }

  /**
   * The element identifier for a field's control.
   *
   * @param field The field.
   * @returns A stable identifier scoped to this screen.
   */
  protected controlId(field: ModuleSettingsField): string {
    return `module-settings-${field}`;
  }

  /**
   * The element identifier for a choice group's visible name. a radio group's name is carried by a
   * caption with no `for`, referenced through `aria-labelledby`.
   *
   * @param field The choice group's field.
   * @returns A stable identifier scoped to this screen.
   */
  protected labelId(field: ModuleSettingsField): string {
    return `module-settings-${field}-label`;
  }

  /**
   * The element identifier for a single radio within a choice group.
   *
   * @param field The choice group's field.
   * @param index The choice's position in the group.
   * @returns A stable identifier scoped to this screen.
   */
  protected choiceId(field: ModuleSettingsField, index: number): string {
    return `module-settings-${field}-${index}`;
  }

  /**
   * The element identifier of a region's heading, referenced by its body's `aria-labelledby`.
   *
   * @param section The region.
   * @returns A stable identifier scoped to this screen.
   */
  protected sectionHeadingId(section: ModuleSettingsSection): string {
    return `module-settings-section-${section}`;
  }

  /**
   * The DOM id of one section's BODY, for the toggle's `aria-controls`. Distinct from {@link
   * ModuleSettingsComponent.sectionHeadingId}, which names the toggle itself; a control cannot point
   * `aria-controls` at its own id and expect assistive technology to find the region.
   *
   * @param section The section whose body is being named.
   * @returns A stable id, unique within the screen.
   */
  protected sectionBodyId(section: ModuleSettingsSection): string {
    return `module-settings-body-${section}`;
  }

  /**
   * The server-supplied messages for one control, if any. the per-field dictionary is an INDEX SIGNATURE
   * and `noPropertyAccessFromIndexSignature` is enabled, so an entry is read with an index expression and
   * never with a property access.
   *
   * @param field The control to report on.
   * @returns The messages, or an empty list.
   */
  protected serverMessages(field: ModuleSettingsField): readonly string[] {
    return fieldErrorMessages(this.currentProblem(), field);
  }

  /**
   * The client-side validation message for one control, when one is currently reportable. ⚠ REPORTED
   * THROUGH THE SAME SINGLE REGION AS THE SERVER'S MESSAGES, and the history is worth keeping because it
   * is the defect this shape exists to prevent.
   *
   * @param field The field to report on.
   * @returns The message, or `null` when the control has no reportable client failure.
   */
  protected clientMessage(field: ModuleSettingsField): string | null {
    switch (field) {
      case 'moduleTitle':
        return this.form.controls.moduleTitle.touched
          && this.form.controls.moduleTitle.hasError('maxlength')
          ? this.moduleTitleTooLongMessage
          : null;
      case 'startDate':
        return this.form.controls.startDate.touched
          && this.form.controls.startDate.hasError('dateDataType')
          ? this.startDateInvalidMessage
          : null;
      case 'endDate':
        return this.form.controls.endDate.touched
          && this.form.controls.endDate.hasError('dateDataType')
          ? this.endDateInvalidMessage
          : null;
      case 'cacheTime':
        return this.form.controls.cacheTime.touched
          && this.form.controls.cacheTime.hasError('integerDataType')
          ? this.cacheTimeInvalidMessage
          : null;
      case 'iconFile':
        return this.form.controls.iconFile.touched
          && this.form.controls.iconFile.hasError(ICON_NOT_CONTAINED_ERROR)
          ? this.iconNotContainedMessage
          : null;
      default:
        return null;
    }
  }

  /**
   * Every message a field currently has to report, client failure first and server messages after. ⚠ ONE
   * LIST, HANDED WHOLE TO THE SHARED FIELD COMPONENT, which is what replaced this screen's own field
   * machinery.
   *
   * @param field The field to report on.
   * @returns The messages, in production order.
   */
  protected messagesFor(field: ModuleSettingsField): readonly string[] {
    const client: string | null = this.clientMessage(field);
    const server: readonly string[] = this.serverMessages(field);

    return client === null ? server : [client, ...server];
  }

  // -----------------------------------------------------------------------------------------------------
  // INTENTS
  // -----------------------------------------------------------------------------------------------------

  /** Opens the destructive confirmation. */
  protected requestRemoval(): void {
    this.removalOpen.set(true);
  }

  /** Closes the destructive confirmation without acting. */
  protected abandonRemoval(): void {
    this.removalOpen.set(false);
  }

  /** Closes the confirmation and removes the placement. */
  protected confirmRemoval(): void {
    this.removalOpen.set(false);

    const id = this.addressedModuleId();

    if (id === undefined) {
      this.remove.emit();
      return;
    }

    if (this.removalOutstanding()) {
      return;
    }

    this.removalOutstanding.set(true);
    this.store.deleteModule(id, this.addressedTabModuleId());
  }

  /** Abandons the form. */
  protected onCancel(): void {
    this.cancel.emit();
    this.returnToListing();
  }

  /**
   * The message shown on the destructive confirmation.
   *
   * @returns The shared wording, with the module's own name interpolated when it has one.
   */
  protected deleteConfirmMessage(): string {
    // The resolved value, for the same reason `onSubmit` reads it: the route path never assigns the input.
    const current = this.settings;

    if (current === null) {
      return DELETE_CONFIRM_MESSAGE;
    }

    const name = current.moduleTitle ?? current.friendlyName ?? '';

    return name.length === 0
      ? DELETE_CONFIRM_MESSAGE
      : `Are You Sure You Wish To Delete "${name}"?`;
  }

  /**
   * Validates the form and submits it. THE MEASURED READ-MODIFY-WRITE, WITH EVERY OPTION-STRICT COERCION
   * MADE EXPLICIT. `ModuleSettings.ascx.vb:L326-L427` guarded on `Page.IsValid`, re-read the module,
   * overwrote each property from the form and committed with `UpdateModule`.
   */
  protected onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // `this.settings` and NOT the raw signal: the resolution order - a caller's seed first, otherwise the
    // loaded module - lives in one place, and reading the signal directly here would make a route-driven
    // submission impossible, because the route path seeds the FORM from the store without ever assigning
    // the input.
    const current = this.settings;

    if (current === null) {
      return;
    }

    const request = this.toUpdateRequest(current);

    this.store.clearFailure();

    this.save.emit(request);

    const id = this.addressedModuleId();

    if (id === undefined) {
      return;
    }

    const placement = this.addressedTabModuleId();

    this.store.updateModule(id, request, placement);
    this.persistSettings(placement);

    // Armed AFTER the commands, so the write flag the module replacement raises is already set and the
    // observer cannot mistake a not-yet-started submission for a finished one.
    this.submissionPending.set(true);
  }

  // -----------------------------------------------------------------------------------------------------
  // PRIVATE
  // -----------------------------------------------------------------------------------------------------

  /**
   * Projects the form onto the update contract. Exactly the seventeen members the server declares, and
   * nothing else.
   *
   * @param seed The loaded state the round-tripped columns come from.
   * @returns The document to submit.
   */
  private toUpdateRequest(seed: ModuleSettingsSeed): UpdateModuleRequest {
    const raw = this.form.getRawValue();

    const movingTo: number | null = raw.tabId === seed.tabId ? null : raw.tabId;

    return {
      tabId: seed.tabId,
      moveToTabId: movingTo,
      moduleTitle: textOrNull(raw.moduleTitle),
      // Round-tripped: the toggle is not offered, but the stored value must survive the replacement.
      allTabs: raw.allTabs,
      header: textOrNull(raw.header),
      footer: textOrNull(raw.footer),
      startDate: textOrNull(raw.startDate),
      endDate: textOrNull(raw.endDate),
      inheritViewPermissions: raw.inheritViewPermissions,
      isDeleted: seed.isDeleted,
      moduleOrder: raw.moduleOrder,
      cacheTime: this.resolveCacheTime(raw.cacheTime),
      iconFile: textOrNull(raw.iconFile),
      visibility: raw.visibility,
      displayTitle: raw.displayTitle,
      // Instructions rather than state: sent explicitly rather than omitted, so the intent is unambiguous.
      setAsDefaultSettings: raw.setAsDefaultSettings,
      applyToAllModules: raw.applyToAllModules,
    };
  }

  /**
   * Resolves the cache period the update carries.
   *
   * @param text The control's text.
   * @returns The period in seconds.
   */
  private resolveCacheTime(text: string): number {
    if (!this.showCacheField()) {
      return 0;
    }

    const trimmed = text.trim();

    if (trimmed.length === 0) {
      return 0;
    }

    // The Option-Strict coercion made EXPLICIT, with the failure handled. The administration code-behinds
    // compiled with `strict="false"`, so L349-L353's `Int32.Parse(txtCacheTime.Text)` on free text was
    // legal and threw on bad input.
    const parsed = Number(trimmed);

    return Number.isInteger(parsed) ? parsed : 0;
  }

  /**
   * Writes the settings maps, but ONLY when this screen has actually changed them. MIGRATION: THE
   * UNCHANGED BAG IS NO LONGER RE-SENT, AND THAT IS A CORRECTION RATHER THAN AN OPTIMISATION. This screen
   * edits columns on the module and its placement; it renders no control over a property-bag entry, so
   * the bag it would send is the bag it read, byte for byte.
   *
   * @param placement The placement addressed, or `undefined` for the module itself.
   */
  private persistSettings(placement: number | undefined): void {
    const bag = this.store.settings();

    if (bag === null) {
      return;
    }

    // The store is provided at the root, so the bag it holds may have been read for a DIFFERENT module.
    // `=== undefined` and never a truth test, and an exact comparison: module 0 is a real module.
    const addressed = this.addressedModuleId();

    if (addressed === undefined || bag.moduleId !== addressed) {
      return;
    }

    if (!this.settingsBagDiffersFromRead(bag)) {
      return;
    }

    this.store.saveSettings(bag, placement);
  }

  /**
   * Whether the bag about to be written differs from the bag that was read. A structural comparison over
   * every member the contract declares: the two identifiers and both property maps, key by key in both
   * directions so that an added, removed or re-valued key is all reported.
   *
   * @param bag The bag that would be written.
   * @returns `true` when a write is warranted.
   */
  private settingsBagDiffersFromRead(bag: ModuleSettingsBag): boolean {
    const read = this.settingsAsRead;

    if (read === null) {
      return true;
    }

    if (read.moduleId !== bag.moduleId || read.tabModuleId !== bag.tabModuleId) {
      return true;
    }

    return (
      mapsDiffer(read.moduleSettings, bag.moduleSettings) ||
      mapsDiffer(read.tabModuleSettings, bag.tabModuleSettings)
    );
  }

  /**
   * Re-seeds every control from the supplied state. the two instruction flags are seeded UNSET rather
   * than from the module, because neither is a column on it - each describes work the server performs
   * after the update, and echoing a previous instruction back onto the form would reapply it on the next
   * submission.
   *
   * @param value The state to show.
   */
  private seed(value: ModuleSettingsSeed): void {
    this.form.reset({
      tabId: value.tabId,
      moduleTitle: value.moduleTitle ?? '',
      moduleOrder: value.moduleOrder,
      allTabs: value.allTabs,
      inheritViewPermissions: value.inheritViewPermissions,
      header: value.header ?? '',
      footer: value.footer ?? '',
      // Blank for the sentinel, exactly as L152-L157 left the box empty when `Null.IsNull` was true.
      startDate: toDateInputValue(value.startDate),
      endDate: toDateInputValue(value.endDate),
      iconFile: value.iconFile ?? '',
      // Bound by enumeration member. `None` is a chosen value and 0 is meaningful, so no fallback applies.
      visibility: value.visibility,
      displayTitle: value.displayTitle,
      cacheTime: value.cacheTime === null ? '0' : String(value.cacheTime),
      setAsDefaultSettings: false,
      applyToAllModules: false,
    });

    // reset() re-enables every control, so the privilege locks must be reapplied after it.
    this.applyPrivilegeLocks();
  }

  /**
   * Locks or releases the controls that reach beyond the page being edited. `emitEvent: false` keeps the
   * lock from looking like an edit.
   */
  private applyPrivilegeLocks(): void {
    const gated = [
      this.form.controls.tabId,
      this.form.controls.allTabs,
      this.form.controls.setAsDefaultSettings,
      this.form.controls.applyToAllModules,
    ];

    for (const control of gated) {
      if (this.allPagesManageable) {
        control.enable({ emitEvent: false });
      } else {
        control.disable({ emitEvent: false });
      }
    }
  }

  /**
   * Reports a failure at the severity the legacy screen used for it. A rejected write keeps its problem
   * document so the banner can show the per-field messages; a refusal, a missing module and a conflict
   * are announced as advisories, at warning severity for the refusal and at error severity for the rest.
   *
   * @param operation The command that failed.
   * @param problem The problem document, or `null` when the response carried none.
   * @param code The failure code the server published, or `null`.
   */
  private announce(
    operation: string,
    problem: ProblemDetails | null,
    code: string | null,
  ): void {
    const status = problem?.status ?? null;

    if (status === FORBIDDEN_STATUS) {
      this.currentProblem.set(problem);

      return;
    }

    if (status === NOT_FOUND_STATUS) {
      this.currentProblem.set(null);
      this.notifications.notify(
        problemSeverity(status),
        NOT_FOUND,
        problemSupportReference(problem),
      );
      return;
    }

    if (status === CONFLICT_STATUS) {
      this.currentProblem.set(null);
      // The published code is surfaced verbatim when the shared vocabulary has no wording for it, so a
      // refusal this screen has never seen is still reported exactly as the server named it.
      this.notifications.notify(
        problemSeverity(status),
        conflictMessage(code) ?? code ?? CONFLICT,
        problemSupportReference(problem),
      );
      return;
    }

    // MIGRATION: THE BANNER IS A NET-NEW AFFORDANCE, NOT A TRANSLATION. Everything else - a rejected write
    // above all - goes to it with the problem document intact, so the per-field messages reach the fields
    // they belong to.
    this.currentProblem.set(problem);

    if (problem === null) {
      this.notifications.notify('error', `The ${operation} request could not be completed.`);
    }
  }

  /** Returns to the listing, which is what the legacy redirect at L421 did. */
  private returnToListing(replaceEntry = false): void {
    if (replaceEntry) {
      void this.router.navigate([MODULE_LIST_ROUTE], { replaceUrl: true });

      return;
    }

    void this.router.navigate([MODULE_LIST_ROUTE]);
  }
}

/**
 * Builds the "Move To Page" options from the tenant's page list. The page the module currently occupies
 * is guaranteed present, prepended when the list does not contain it, so the picker always shows where
 * the module actually is.
 *
 * @param tabs The tenant's pages, unpaged.
 * @param currentTabId The page the module occupies, or `undefined` when none has resolved.
 * @returns The options, in list order.
 */
function buildPageOptions(
  tabs: readonly TabListItem[],
  currentTabId: number | undefined,
): readonly SelectOption<number>[] {
  const options: SelectOption<number>[] = [];
  let currentPresent = false;

  for (const tab of tabs) {
    const isCurrent = currentTabId !== undefined && tab.tabId === currentTabId;

    if (isCurrent) {
      currentPresent = true;
    } else if (tab.isDeleted) {
      continue;
    }

    options.push({
      value: tab.tabId,
      // `level` is a non-negative depth on the list contract; repeat() on 0 yields an empty prefix.
      label: `${'\u00a0\u00a0'.repeat(Math.max(tab.level, 0))}${tab.tabName}`,
    });
  }

  if (currentTabId !== undefined && !currentPresent) {
    options.unshift({ value: currentTabId, label: CURRENT_PAGE_LABEL });
  }

  return options;
}
