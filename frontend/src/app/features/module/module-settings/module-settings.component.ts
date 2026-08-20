import {
  computed,
  effect,
  inject,
  signal,
  untracked,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  MEMBERSHIP_SETTINGS_ROUTE,
  MODULE_LIST_ROUTE,
} from '../../../core/config/app-routes.config';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import type { Params } from '@angular/router';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import {
  MODULE_VISIBILITY,
  ModuleVisibility,
  UNPUBLISHED_VISIBILITY_ERROR,
  UNPUBLISHED_VISIBILITY_MESSAGE,
  isPublishedModuleVisibility,
} from '../../../core/models/module.model';
import type {
  ModuleDetail,
  ModulePermissionCell,
  ModulePermissionGrantInput,
  ModulePermissionGrid,
  ModulePermissionReplacement,
  ModulePermissionRoleRow,
  ModulePermissionUserRow,
  ModuleSettingsBag,
  UpdateModuleRequest,
} from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { SelectOption } from '../../../core/models/select-option.model';
import type { TabListItem } from '../../../core/models/tab.model';
import { NotificationService } from '../../../core/services/notification.service';
import { buildPageOptions } from '../../../core/utils/page-options.util';
import { AuthStore } from '../../../core/state/auth.store';
import { ModuleStore } from '../../../core/state/module.store';
import type { ModuleStoreFailure } from '../../../core/state/module.store';
import {
  CONFLICT,
  NOT_FOUND,
  conflictMessage,
  problemFrom,
  problemSeverity,
  problemSupportReference,
  stripLegacyBreakTags,
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

  /** How the placement is aligned within its pane, or `null` when none is stored. */
  readonly alignment: string | null;

  /** The container background colour, or `null` when none is stored. */
  readonly color: string | null;

  /** The container border width, one digit, or `null` when none is stored. */
  readonly border: string | null;

  /** How the placement is presented. */
  readonly visibility: ModuleVisibility;

  /** Whether the container chrome is displayed. */
  readonly displayTitle: boolean;

  /**
   * Whether the module was created from an ADMINISTRATION package, or `null` when the package could not be
   * resolved and nothing is claimed.
   *
   * Carried on the seed as well as on the wire contract because a parent that pins this screen's state is
   * pinning the whole state, and this member decides whether the form may be offered at all.
   */
  readonly isAdmin: boolean | null;
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

  /**
   * The container alignment (`cboAlign`, a horizontal radio list with the values `left`, `center`, `right`
   * and the empty string).
   *
   * ⚠ THESE THREE CONTROLS WERE ABSENT ALTOGETHER, and the columns behind them were unreachable: an
   * installation's stored container appearance could be neither seen nor changed, while the settings
   * section on this same screen asserted the module had no stored settings of its own.
   */
  alignment: FormControl<string>;

  /** The container colour (`txtColor`, free text with no legacy validator). */
  color: FormControl<string>;

  /** The container border width (`txtBorder`, `MaxLength="1"` with an integer data-type check). */
  border: FormControl<string>;

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
  alignment: 'Alignment:',
  color: 'Color:',
  border: 'Border:',
  visibility: 'Visibility:',
  displayTitle: 'Display Container?',
  cacheTime: 'Cache Time (secs):',
  setAsDefaultSettings: 'Set As Default Settings?',
  applyToAllModules: 'Apply To All Modules?',
} as const;

/** The keys of {@link FIELD_HINTS} and {@link FIELD_LABELS}. */
/**
 * Refuses a visibility code this screen cannot offer, so the operator is told rather than the API.
 *
 * @param control The visibility control, whose value is the stored code until the operator changes it.
 * @returns The single error, or `null` when the code is one of the published three.
 */
function publishedVisibilityValidator(control: AbstractControl<number>): ValidationErrors | null {
  return isPublishedModuleVisibility(control.value)
    ? null
    : { [UNPUBLISHED_VISIBILITY_ERROR]: true };
}

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
  alignment: 'Choose the alignment of this Module within its Pane.',
  color: 'Enter a background color for this Module\u2019s Container, or leave it blank for none.',
  border: 'Enter a border width for this Module\u2019s Container, from 0 to 9.',
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

/** The bound on `dbo.TabModules.Color` (`nvarchar(20) NULL`). The legacy box declared none. */
const COLOR_MAX_LENGTH = 20;

/** `txtBorder` carries `MaxLength="1"`, which is also the column's width. */
const BORDER_MAX_LENGTH = 1;

/**
 * The alignment options, in the legacy radio list's own order with its own values
 * (`modulesettings.ascx:L122-L127`). The empty value is the "Not Specified" item, and it is LAST there.
 */
const ALIGNMENT_OPTIONS: readonly { readonly value: string; readonly label: string }[] = [
  { value: 'left', label: 'Left' },
  { value: 'center', label: 'Center' },
  { value: 'right', label: 'Right' },
  { value: '', label: 'Not Specified' },
];

/**
 * Reproduces `valBorder`: a single decimal digit, or nothing at all.
 *
 * The legacy control paired `MaxLength="1"` with an integer data-type check, so the two rules together
 * admitted exactly one digit. Both are reproduced here rather than only the length, because a `maxlength`
 * attribute bounds typing and not a pasted or programmatic value.
 *
 * @param message The wording to report, taken verbatim from the resource file.
 * @returns A validator reporting {@link INTEGER_TYPE_ERROR} with that wording.
 */
function singleDigitCheck(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    if (typeof raw !== 'string' || raw === '') {
      return null;
    }

    return raw.length === 1 && raw >= '0' && raw <= '9'
      ? null
      : { [INTEGER_TYPE_ERROR]: message };
  };
}

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
 * A cache period may not be negative. ⚠ MIGRATION - A DELIBERATE, DOCUMENTED DIVERGENCE FROM THE LEGACY
 * RULE, WHICH ADMITTED IT. `modulesettings.ascx:L172` declares exactly one validator on this box, a
 * `CompareValidator` with `Operator="DataTypeCheck" Type="Integer"`, and the code-behind stored whatever
 * parsed - so `-1` was accepted and written. That is an omission rather than a decision: `plCacheTime.Help`
 * calls the value "the time this object is kept in the Cache", and a duration cannot run backwards.
 *
 * Reported in the resource file's own wording, so one rule is not described two ways. The bound is enforced
 * on the server as well; NO UPPER BOUND IS IMPOSED, because neither the legacy validator, the `int` column
 * nor the server declares one.
 *
 * @param message The wording to report, taken verbatim from the resource file.
 * @returns A validator reporting {@link INTEGER_TYPE_ERROR} with that wording for a negative value.
 */
function nonNegativeCheck(message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const text = typeof control.value === 'string' ? control.value.trim() : '';

    if (text.length === 0 || !/^[+-]?\d+$/.test(text)) {
      // Integrality is another validator's business, and reporting the same box twice for one keystroke
      // would put two sentences under it.
      return null;
    }

    return Number(text) < 0 ? { [INTEGER_TYPE_ERROR]: message } : null;
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

/**
 * The failure code the server publishes when this screen is asked for an administrative module's settings.
 *
 * Matched in its {@link failureCode} form - the reduced token, without the `urn:dnnmigration:error:` prefix -
 * because that is what the store records.
 */
const SETTINGS_PROTECTED_CODE = 'module.settings_protected';

/**
 * The definition name of the administrative package whose settings ARE the portal's membership settings,
 * spelled exactly as the server spells it in `MembershipSettingsDto.UserAccountsModuleDefinitionName`.
 */
const USER_ACCOUNTS_DEFINITION_NAME = 'User Accounts';

/** Where an administrative module's settings are actually administered, keyed by definition name. */
const ADMINISTERED_ELSEWHERE: Readonly<Record<string, { readonly route: string; readonly label: string }>> =
  Object.freeze({
    [USER_ACCOUNTS_DEFINITION_NAME]: Object.freeze({
      route: MEMBERSHIP_SETTINGS_ROUTE,

      // The sidebar's own wording for that destination, so the sentence names the screen the operator will
      // actually see rather than a synonym for it.
      label: 'User Settings',
    }),
  });

/**
 * What this screen says instead of a refusal when the module is administrative.
 *
 * ⚠ THIS REPLACES A DEAD END. Measured on the running application: `/modules/7/settings` answered 403
 * `module.settings_protected` and the screen rendered a heading, a Forbidden banner reading "Administrative
 * module settings are available only through their typed privileged endpoint", and nothing else - a sentence
 * about an endpoint, addressed to an operator, naming no screen they could reach and offering no way on. The
 * refusal is correct and deliberate: such a module's settings belong to the typed screen that owns them. What
 * was wrong was presenting a by-design boundary as a failure and leaving the operator stranded at it.
 */
const ADMINISTRATIVE_MODULE_MESSAGE =
  'This module is part of the site\u2019s administration, so its settings are not edited here.';

/** The sentence that names the screen those settings belong to, when this console has one. */
const ADMINISTERED_ELSEWHERE_MESSAGE = 'They are administered on the';

/** The sentence used when no screen in this console administers the package. */
const ADMINISTERED_NOWHERE_MESSAGE =
  'They are administered by the feature that owns this module type, which this console does not include.';

/** The affordance that leaves a screen whose settings cannot be edited here. */
const RETURN_TO_MODULES_LABEL = 'Back to modules';

/**
 * The label on the support reference carried beside the administrative explanation.
 *
 * Spelled exactly as the shared error banner spells it, so the value an operator quotes is introduced the
 * same way wherever it appears. It is carried here even though this is not presented as a failure, because a
 * refusal DID happen server-side and an operator who disputes the boundary needs the reference to raise it.
 */
const SUPPORT_REFERENCE_LABEL = 'Reference:';

/** The caption for the advisory list of keys this module's definition declares. */
const DEFINITION_PERMISSION_KEYS_LABEL = 'Permissions defined for this module type:';

/**
 * The state of the declared-permission-key read, as four distinguishable situations rather than one
 * nullable list. See {@link ModuleSettingsComponent.declaredPermissionKeysState} for why the collapse of
 * these into `null` was a defect rather than a simplification.
 */
type DeclaredKeysState =
  /** Not applicable: this caller does not administer the tenant, so the read is never issued. */
  | { readonly kind: 'idle' }
  /** Issued and outstanding. */
  | { readonly kind: 'loading' }
  /**
   * Answered. An EMPTY definition list here is a real answer - this definition declares no keys.
   *
   * ⚠ THE GRID AND THE ADVISORY KEY LIST ARE ONE READ, DELIBERATELY. They were two: the key list came from
   * the catalogue listing filtered by module definition, and the grid did not exist at all. Two reads of
   * the same vocabulary can disagree, and an operator deciding inheritance against one list while ticking
   * boxes in another is exactly the disagreement that matters - so the grid's own `definitions` is now the
   * single source of both.
   */
  | { readonly kind: 'ready'; readonly grid: ModulePermissionGrid }
  /** Refused or failed. The vocabulary is UNKNOWN, which is not the same as empty. */
  | { readonly kind: 'failed'; readonly problem: ProblemDetails | null };

/** The state before the read is issued, hoisted so the identity is stable across change detection. */
const IDLE_DECLARED_KEYS: DeclaredKeysState = Object.freeze({ kind: 'idle' });

/** The state while the read is outstanding. */
const LOADING_DECLARED_KEYS: DeclaredKeysState = Object.freeze({ kind: 'loading' });

/** Shown while the declared-permission vocabulary is being read. */
const DECLARED_KEYS_LOADING_LABEL = 'Reading the permissions defined for this module type…';

/** The wording of the control that re-issues a failed declared-permission read. */
const DECLARED_KEYS_RETRY_LABEL = 'Try again';

/**
 * Shown when that read failed.
 *
 * ⚠ IT SAYS "COULD NOT BE READ", NEVER "NONE". The whole point of separating the failed state is that this
 * screen must stop implying an empty permission vocabulary it never managed to fetch - the switch beside
 * it is choosing between exactly these keys, so a silent blank misrepresents what the operator is deciding.
 */
const DECLARED_KEYS_FAILED_LABEL =
  'The permissions defined for this module type could not be read, so they are not listed here.';

/** The caption of the grant grid, which names the table for a screen reader as well as for the eye. */
const PERMISSION_GRID_CAPTION = 'Module permissions by role';

/**
 * The wording of the grid's row-header column.
 *
 * MIGRATION: the legacy grid's first column carried the header text `&nbsp;`
 * (`PermissionsGrid.vb:L490`) - a blank that named nothing. A column of role names needs a name, so this
 * one is given the name the column actually holds.
 */
const PERMISSION_GRID_ROLE_HEADING = 'Role';

/** Announced when the grid has no rows at all, which only happens if the definition declares no keys. */
const PERMISSION_GRID_EMPTY_MESSAGE =
  'This module type declares no permissions, so there is nothing to grant.';

/** Explains why the administrator row cannot be changed, rather than leaving a dead checkbox unexplained. */
const PERMISSION_GRID_ADMINISTRATOR_HINT =
  'Portal administrators always hold every module permission, so this row cannot be changed.';

/** Explains why the view column is locked while inheritance is on. */
const PERMISSION_GRID_INHERITED_HINT =
  'View access is being inherited from the page, so the View column cannot be granted here. '
  + 'Saving with inheritance on withdraws any View grants this module holds.';

/**
 * Explains a grant the SERVER withheld for a reason this client does not model.
 *
 * The administrator rule and the inheritance rule each have their own sentence above, and each is only
 * reached when this client can positively establish that rule applies. Anything else the server locks is
 * genuinely unexplained here, so the sentence says that rather than asserting a reason that may be false:
 * describing every withheld grant as an administrator row was the defect this constant exists to end.
 */
const PERMISSION_GRID_WITHHELD_HINT =
  'This grant is managed outside this screen and cannot be changed here.';

/**
 * The slack allowed before the permission scroller is reported as clipping.
 *
 * `scrollWidth` and `clientWidth` are INTEGER properties, so a table a fraction of a pixel wider than its
 * container rounds to a difference of one. Reporting that as hidden content would put a focus stop and a
 * landmark on a grid with nothing to scroll. The shared grid uses the same value for the same reason.
 */
const PERMISSION_SCROLLER_TOLERANCE_PX = 1;

/**
 * Why one grant cannot be changed. A CLOSED set, because each member has to carry its own true sentence:
 * collapsing them into one string is what let every withheld grant be described as an administrator row.
 */
type PermissionLockKind = 'administrator' | 'inherited' | 'withheld';

/**
 * The sentence for each lock, in the order the legend presents them.
 *
 * ⚠ ONE ELEMENT PER REASON, NOT ONE PER CELL. A grid this wide can hold hundreds of locked boxes, and
 * repeating the sentence into each of them would put the same paragraph in the document hundreds of times.
 * Every cell bearing a reason points at the single element that states it.
 */
const PERMISSION_LOCK_SENTENCES: readonly { readonly kind: PermissionLockKind; readonly sentence: string }[] = [
  { kind: 'administrator', sentence: PERMISSION_GRID_ADMINISTRATOR_HINT },
  { kind: 'inherited', sentence: PERMISSION_GRID_INHERITED_HINT },
  { kind: 'withheld', sentence: PERMISSION_GRID_WITHHELD_HINT },
];

/** One entry of the grid's lock legend: a reason actually in play, and the element that states it. */
interface PermissionLockLegendEntry {
  /** The identifier every cell bearing this reason points at. */
  readonly id: string;

  /** The sentence. */
  readonly sentence: string;
}

/**
 * One rendered cell of the grant grid: the server's facts, reconciled with the operator's unsaved edits
 * and with the live state of the inheritance switch.
 */
interface PermissionCellView {
  /** The column. */
  readonly permissionId: number;

  /** The column's key, used for the cell's accessible name. */
  readonly permissionKey: string;

  /** Whether the box is ticked. */
  readonly allowAccess: boolean;

  /** Whether the box may be ticked. */
  readonly editable: boolean;

  /**
   * Why it may not, or `null` when it may.
   *
   * A KIND rather than a sentence, so the reason can be stated once in the legend and pointed at, and so a
   * cell can never bear a reason that does not apply to it.
   */
  readonly lock: PermissionLockKind | null;
}

/** One rendered row of the grant grid, for a role or for an individually named account. */
interface PermissionRowView {
  /**
   * A stable identity for the row, used both as the `@for` track expression and as the prefix of every
   * cell's control identifier. Prefixed by kind, because a role and an account can bear the same number.
   */
  readonly key: string;

  /** The role or account identifier. */
  readonly principalId: number;

  /** Whether the row is a role or an individually named account. */
  readonly kind: 'role' | 'user';

  /** The row's visible name. */
  readonly name: string;

  /** Whether the row is the portal's administrator role. */
  readonly isAdministrator: boolean;

  /** The cells, in column order. */
  readonly cells: readonly PermissionCellView[];
}

/** One rendered column header of the grant grid. */
interface ModulePermissionColumnView {
  /** The definition. */
  readonly permissionId: number;

  /** The key, which is what the cells' accessible names are built from. */
  readonly permissionKey: string;

  /** The definition's display name, which is what the header shows. */
  readonly permissionName: string;

  /** Whether this column is currently collapsed by the inheritance switch. */
  readonly inherited: boolean;
}

/** The empty edit map, hoisted so a reset does not allocate and the identity is stable. */
const NO_PERMISSION_EDITS: ReadonlyMap<string, boolean> = new Map<string, boolean>();

/** The empty row list, hoisted so an unread grid returns a stable identity across change detection. */
const EMPTY_PERMISSION_ROWS: readonly PermissionRowView[] = Object.freeze([]);

/** The empty column list, hoisted for the same reason. */
const EMPTY_PERMISSION_COLUMNS: readonly ModulePermissionColumnView[] = Object.freeze([]);

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
/**
 * Names the operation a refusal is an answer to, so a later answer supersedes an earlier one.
 *
 * ⚠ QA-26 — THIS SCREEN REPORTS THROUGH THREE DIFFERENT BRANCHES AND THEY ALL ANSWER ONE QUESTION. A
 * not-found, a conflict and an unattributable failure of the SAME operation word themselves differently, so
 * the queue's identical-neighbour collapse never saw them as related; running one operation twice therefore
 * left two differently-worded refusals on screen at once, next to this screen's own banner. Scoped per
 * operation rather than per screen, because a refused save and a refused delete really are two things the
 * operator needs to see together.
 *
 * @param operation The operation being reported on.
 * @returns The scope key for that operation.
 */
function operationScope(operation: string): string {
  return `module-settings:${operation}`;
}

const NOT_FOUND_STATUS = 404;

/** The status a rejected write arrives with. */
const CONFLICT_STATUS = 409;


/**
 * The screen's heading when a caller supplies none. Taken from `ModuleSettings.ascx.resx`'s own
 * `ControlTitle_module.Text` - the resource key DotNetNuke uses for the control's own title.
 *
 * ⚠ IT WAS `ModuleSettings.Text`, WHICH IS THE WORDING THE LEGACY SCREEN SHOWED ABOVE ITS FIRST SECTION
 * HEAD - and this screen's first section head shows it still, so the heading and the section beneath it
 * read identically and the reader was told the same thing twice. Both are legacy-verbatim; which level
 * carries which is what changed, and `module-form` carries the same correction.
 */
const DEFAULT_HEADING = 'Module';

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
const NO_SPECIFIC_SETTINGS_MESSAGE =
  'This module type stores no named settings of its own. The fields above are stored on the module and '
  + 'its placement.';

/** The opening of the blank-heading disclosure, up to the name itself. */
const TITLE_FALLBACK_PREFIX = 'With no heading, this module is listed as “';

/** The close of the blank-heading disclosure, after the name. @see TITLE_FALLBACK_PREFIX. */
const TITLE_FALLBACK_SUFFIX = '”, the name of its module definition.';

const MODULE_SCOPE_LABEL = 'this module, on every page';

/** How a setting recorded against one placement is described. */
const PLACEMENT_SCOPE_LABEL = 'this placement only';


/**
 * The module settings screen, replacing `Website/admin/Modules/modulesettings.ascx` and its code-behind.
 * Mounted at `modules/:moduleId/settings`.
 */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE: exactly one per screen, stating that screen's
 * SCOPE - the record it acts on when the title does not already name it, otherwise what the screen is for
 * in one line - and never a status, a count or a progress readout.
 */
const PAGE_SUBTITLE =
  "This module's own settings, and the settings for the page it sits on.";

@Component({
  selector: 'app-module-settings',
  standalone: true,
  imports: [
    RouterLink,
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

  /** Where the listing stands, so a return lands on the page, ordering and search it was showing. */
  private readonly listReturn = inject(ListReturnStore);

    /**
   * The session, read only to decide whether the catalogue may be asked for at all. The `/permissions`
   * endpoint is declared under the administrator policy, so a caller without tenant administration
   * receives a 403.
   */
  private readonly authStore = inject(AuthStore);

  /** Bounds the catalogue subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /** This screen's own element, so the permission scroller can be measured. */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

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

    // The legacy radio list's fourth item was "Not Specified" with the value `''`, which is the initial
    // value here: an empty alignment is a real stored state, not an absence.
    alignment: new FormControl('', { nonNullable: true }),
    color: new FormControl('', { nonNullable: true }),

    // ⚠ THE VALIDATOR IS THE LEGACY ONE, WORD FOR WORD. `valBorder` was an integer DataTypeCheck whose
    // message read "Invalid Border (must be a number between 0 and 9)" - and the message constant already
    // existed in this file, unused, because the column it belonged to was never transported.
    border: new FormControl('', {
      nonNullable: true,
      validators: [singleDigitCheck(BORDER_INVALID_MESSAGE)],
    }),
    visibility: new FormControl<ModuleVisibility>(MODULE_VISIBILITY.maximized, {
      nonNullable: true,
      // ⚠ NOT A GUARD AGAINST A CONTROL THIS SCREEN OWNS - the radio group can only ever produce one of the
      // three. It guards the value SEEDED FROM THE SERVER: `TabModules.Visibility` is `int` with no check
      // constraint, so a stored code outside the published three reaches this control, matches no radio and
      // used to be sent straight back on the next save of any other field.
      validators: [publishedVisibilityValidator],
    }),
    displayTitle: new FormControl(true, { nonNullable: true }),
    cacheTime: new FormControl('', {
      nonNullable: true,
      validators: [
        integerDataTypeCheck(CACHE_TIME_INVALID_MESSAGE),
        nonNegativeCheck(CACHE_TIME_INVALID_MESSAGE),
      ],
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
   * The state of the DEFINITION's declared-permission-key read.
   *
   * ⚠ A FOUR-STATE VALUE REPLACING A NULLABLE ARRAY, AND THE COLLAPSE WAS THE DEFECT. `null` was made to
   * carry four different situations at once - not applicable, not yet returned, returned nothing, and
   * FAILED - and the template rendered all of them identically, as nothing at all. So a `500` from the
   * catalogue was pixel-for-pixel indistinguishable from a definition that genuinely declares no keys: the
   * screen quietly asserted an empty vocabulary it had never successfully read. Separating the states is
   * what lets a failure say it failed and a slow read say it is still going.
   */
  private readonly declaredPermissionKeysState = computed<DeclaredKeysState>(() => {
    if (this.store.permissionsLoading()) {
      return LOADING_DECLARED_KEYS;
    }

    const failure = this.store.permissionsFailure();

    if (failure !== null) {
      return { kind: 'failed', problem: failure.problem };
    }

    const grid = this.store.permissionGrid();

    // ⚠ THE GRID MUST BE THIS MODULE'S. The store is provided at the root, so a grid read for a different
    // module would otherwise be rendered as this one's - and every identifier in it is one a save would
    // then withdraw. `=== undefined` and an exact comparison, because module 0 is a real module.
    const addressed = this.addressedModuleId();

    if (grid === null || addressed === undefined || grid.moduleId !== addressed) {
      return IDLE_DECLARED_KEYS;
    }

    return { kind: 'ready', grid };
  });

  /**
   * The operator's unsaved cell edits, keyed `<kind>:<principalId>:<permissionId>`. Held apart from the
   * server's grid rather than mutated into it, so a re-read replaces the facts without discarding the
   * edits and a cancel discards the edits without needing to re-read.
   */
  private readonly permissionEdits = signal<ReadonlyMap<string, boolean>>(NO_PERMISSION_EDITS);

  /**
   * The live state of the inheritance switch, mirrored out of the reactive form.
   *
   * ⚠ A REACTIVE FORM PUBLISHES ITS VALUE THROUGH AN OBSERVABLE, NOT A SIGNAL, so a computed that read
   * `this.form.value` would never recompute. The legacy switch carried `autopostback="true"`
   * (`modulesettings.ascx:L46`) precisely because the grid had to be redrawn the moment it changed; this
   * mirror is what lets the view column collapse without a round trip.
   */
  private readonly inheritViewLive = signal<boolean>(false);

  /** Whether the grant replacement is in flight, folded into this screen's own submission conclusion. */
  private readonly permissionsSaving = signal<boolean>(false);

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
    return (
      this.pinnedSaving
      ?? (this.store.saving() || this.store.settingsSaving() || this.store.permissionsSaving())
    );
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
   * Adopts the module type's DECLARED cache period once the definition arrives, for a placement that holds
   * none of its own.
   *
   * ⚠ AN EFFECT, BECAUSE THE DEFAULT ARRIVES AFTER THE SEED, AND THAT ORDERING IS THE WHOLE DEFECT. The
   * definition is requested only once the module has resolved and disclosed its `moduleDefId`, so at the
   * moment the form is seeded the declared period is not yet known - and the seed fell back to a literal 0.
   * The screen therefore read `defaultCacheTime` from the server on every entry (it needs it to decide whether
   * to offer the field at all) and then discarded it, so an operator saving an untouched form wrote 0 over the
   * period the module type declares. `ModuleSettings.ascx.vb:L122-L123` seeded the box from the definition's
   * default for exactly this case.
   *
   * Three guards, each load-bearing: a placement period of its own always wins; a definition that declares no
   * default at all is left alone, because its sentinel must never reach a field; and a control the operator
   * has touched is never overwritten. The write leaves the control PRISTINE, so it cannot make the
   * unsaved-changes guard question a form nobody edited.
   */
  private readonly adoptDeclaredCachePeriod = effect(() => {
    const declared: number | undefined = this.store.definition()?.defaultCacheTime;
    const placement: ModuleSettingsSeed | null = this.resolvedSeed();

    if (declared === undefined || declared === NO_CACHE_DEFAULT || placement === null) {
      return;
    }

    const stored: number | null = placement.cacheTime;

    if (stored !== null && stored !== NO_CACHE_DEFAULT) {
      return;
    }

    untracked(() => {
      const control = this.form.controls.cacheTime;

      if (control.dirty) {
        return;
      }

      const wanted: string = String(declared);

      if (control.value !== wanted) {
        control.setValue(wanted);
      }
    });
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

    // ⚠ WITHHELD FOR A MODULE THIS SCREEN DOES NOT ADMINISTER, AND RUNTIME TESTING MEASURED WHY. The only
    // reason this screen reads the definition is to decide whether to offer the cache field; the
    // administered-elsewhere branch offers no field at all, so the read has nothing to inform.
    //
    // Worse than merely useless, it is guaranteed to fail: the definition catalogue withholds
    // administrative packages under the SAME policy that refuses these settings, so asking for one answers
    // 404. Measured on `/modules/7/settings`, once per render: a `resource.not_found` refusal surfacing as
    // a "The requested item could not be found." warning beside an explanation that had just told the
    // operator their settings live on another screen. A screen must not raise an alarm about something it
    // should never have asked for.
    if (this.administeredElsewhere()) {
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

    // KEYED BY THE MODULE, NOT BY ITS DEFINITION. The grid's rows are the module's own grants, so two
    // modules built from the same definition have different grids; guarding on the definition would show
    // the first module's grants on the second.
    if (this.permissionsRequested === detail.moduleId) {
      return;
    }

    this.requestDeclaredPermissionKeys(detail.moduleId);
  });

  /**
   * Issues the declared-permission read for one definition.
   *
   * Extracted from the effect that first triggers it so the retry control can re-issue exactly the same
   * request without the effect's already-requested guard refusing it.
   *
   * @param moduleDefinitionId The definition whose declared keys to read.
   */
  private requestDeclaredPermissionKeys(moduleId: number): void {
    this.permissionsRequested = moduleId;

    // ⚠ UNSAVED EDITS ARE DISCARDED BY A RE-READ, ON PURPOSE. An edit is a delta against a specific set of
    // rows; carrying it across a re-read would apply it to rows that may no longer exist, and the save is a
    // REPLACE, so a stale delta would withdraw real grants.
    this.permissionEdits.set(NO_PERMISSION_EDITS);

    // ⚠ STILL ADVISORY, AND ITS FAILURE STILL DOES NOT TAKE THE SCREEN DOWN. The store holds the refusal in
    // a slot of its own rather than the shared one, so the document reaches the inline region with its
    // reference and its retry, and never the page banner.
    this.store.loadPermissions(moduleId);
  }

  /**
   * Re-issues a declared-permission read that failed.
   *
   * The advisory nature of the read is exactly why a retry is worth offering: the operator is not blocked
   * from the rest of the screen, so a second attempt costs them nothing and is the only way to lift the
   * lock this failure puts on the permission controls.
   */
  protected onRetryDeclaredKeys(): void {
    const detail = this.addressedDetail();

    if (detail === null) {
      return;
    }

    this.requestDeclaredPermissionKeys(detail.moduleId);
  }

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

    // ⚠ ALL THREE WRITES, NOT TWO. A submission may replace the module, its settings bag and its grant
    // grid; concluding while any one of them is still in flight reports success before the last has
    // answered, and marks the form pristine while an edit is still unsaved.
    const writing =
      this.store.saving() || this.store.settingsSaving() || this.store.permissionsSaving();

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
  /** The one-line scope statement shown beneath the title. */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

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
   * `valBorder`'s wording, now bound to the control it belongs to. It sat in this file UNUSED while the
   * border column had no transport at all, which is exactly the gap this closes.
   */
  protected readonly borderInvalidMessage = BORDER_INVALID_MESSAGE;

  /** The alignment radio group's options, in the legacy order. */
  protected readonly alignmentOptions = ALIGNMENT_OPTIONS;

  protected readonly moduleTitleTooLongMessage = 'Title must be 256 characters or fewer.';

  /** The column and control bounds the template advertises through `maxlength`. */
  protected readonly limits = {
    moduleTitle: MODULE_TITLE_MAX_LENGTH,
    iconFile: ICON_FILE_MAX_LENGTH,
    cacheTime: CACHE_TIME_MAX_LENGTH,
    color: COLOR_MAX_LENGTH,
    border: BORDER_MAX_LENGTH,
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
  protected readonly problem = computed<ProblemDetails | null>(() => {
    // ⚠ WITHHELD FOR A BOUNDARY THIS SCREEN EXPLAINS ITSELF. An administrative module's settings are refused
    // by design, and the explanation below states that in words an operator can act on. Rendering the problem
    // document as well would make one fact two statements, one of which talks about an endpoint.
    if (this.administeredElsewhere()) {
      return null;
    }

    return this.settingsRefusal() ?? this.currentProblem();
  });

  /**
   * Whether this module's settings belong to a different screen entirely, because its package is
   * administrative.
   *
   * TWO INDEPENDENT SIGNALS, AND EITHER IS SUFFICIENT. The module read reports the package's own
   * administrative flag, which is available before the settings read answers and is the signal the listing
   * uses to decide whether to offer this screen at all. The settings read's own refusal code is the second,
   * and it covers the case where the flag could not be resolved - the server refused, and the reason it gave
   * is the reason this screen exists to explain.
   */
  /**
   * The module this screen is showing, whether it came from a read or was pinned by a parent.
   *
   * The `settings` accessor resolves the same pair, but an accessor cannot be depended upon by a `computed`
   * without being called - which is fine, and is what this does once rather than at every call site.
   */
  private readonly resolvedSeed = computed<ModuleSettingsSeed | null>(
    () => this.seeded() ?? this.store.module(),
  );

  protected readonly administeredElsewhere = computed<boolean>(() => {
    // The RESOLVED state, so a parent that pins this screen's module is honoured exactly as a read is. Both
    // sources are signals, so reading them here registers the dependency either way.
    if (this.resolvedSeed()?.isAdmin === true) {
      return true;
    }

    const failure: ModuleStoreFailure | null = this.store.settingsFailure();

    return failure !== null && failure.code === SETTINGS_PROTECTED_CODE;
  });

  /**
   * The screen those settings ARE administered on, or `null` when this console administers none.
   *
   * Resolved from the DEFINITION name, which is the same key the server's own membership-settings lookup
   * matches on, so the destination is the screen that genuinely owns the settings rather than a guess.
   */
  protected readonly administeringScreen = computed<{ readonly route: string; readonly label: string } | null>(
    () => {
      const definition: string = (this.resolvedSeed()?.friendlyName ?? '').trim();

      return ADMINISTERED_ELSEWHERE[definition] ?? null;
    },
  );

  /** The wording that opens the administrative explanation. */
  protected readonly administrativeMessage = ADMINISTRATIVE_MODULE_MESSAGE;

  /** The wording that introduces the owning screen. */
  protected readonly administeredElsewhereMessage = ADMINISTERED_ELSEWHERE_MESSAGE;

  /** The wording used when no screen in this console owns the settings. */
  protected readonly administeredNowhereMessage = ADMINISTERED_NOWHERE_MESSAGE;

  /** The affordance that returns to the listing. */
  protected readonly returnToModulesLabel = RETURN_TO_MODULES_LABEL;

  /** The listing address, for the affordance above. */
  protected readonly moduleListRoute = MODULE_LIST_ROUTE;

  /** The wording that introduces the support reference. */
  protected readonly supportReferenceLabel = SUPPORT_REFERENCE_LABEL;

  /**
   * The identifier an operator quotes when disputing this boundary, or `null` when no server refusal was
   * involved - which is the case when the module read alone disclosed the package as administrative.
   */
  protected readonly administrativeReference = computed<string | null>(
    () => this.store.settingsFailure()?.summary.supportReference ?? null,
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

  /**
   * Resolves the cache period to seed the field with.
   *
   * @param stored The placement's own stored period, or `null` when it holds none.
   * @returns The value to place in the field.
   */
  private seedCacheTime(stored: number | null): string {
    if (stored !== null && stored !== NO_CACHE_DEFAULT) {
      return String(stored);
    }

    const declared: number | undefined = this.store.definition()?.defaultCacheTime;

    // The definition's own sentinel means it declares no default, in which case the field is not offered at
    // all - but the seed still has to be something, and zero is what the legacy save wrote for a blank box.
    return declared !== undefined && declared !== NO_CACHE_DEFAULT ? String(declared) : '0';
  }

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
  /**
   * The declared keys once they have actually been read, or `null` in every other state. Deliberately NOT
   * a substitute for {@link declaredKeysUnread} - a caller reading only this one still cannot tell an
   * empty vocabulary from an unread one, which is the fault being closed.
   */
  protected readonly definitionPermissionKeys = computed<readonly string[] | null>(() => {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind === 'ready') {
      return state.grid.definitions.map((definition) => definition.permissionKey);
    }

    return null;
  });

  /** Whether the declared-permission read is outstanding, so the region can say so and mark itself busy. */
  protected readonly declaredKeysLoading = computed<boolean>(
    () => this.declaredPermissionKeysState().kind === 'loading',
  );

  /** Whether that read failed, so the region can say the vocabulary is unknown rather than empty. */
  protected readonly declaredKeysUnread = computed<boolean>(
    () => this.declaredPermissionKeysState().kind === 'failed',
  );

  /**
   * The server's own sentence about why the vocabulary could not be read, appended to the standing
   * advisory when the refusal carried one. A `403` and a `500` are different situations and an operator
   * who can act on the first should not be shown the wording for the second.
   */
  protected readonly declaredKeysFailureDetail = computed<string | null>(() => {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind !== 'failed') {
      return null;
    }

    const detail: string = stripLegacyBreakTags(state.problem?.detail ?? '');

    return detail.length > 0 ? detail : null;
  });

  /**
   * The support reference for a failed declared-permission read.
   *
   * ⚠ AN INLINE FAILURE NEEDS ONE JUST AS MUCH AS A BANNER DOES. This read fails without ever reaching the
   * page-level banner - correctly, since it is a sub-request and must not interrupt the whole screen - so
   * without this the only failure on screen was the one failure an operator could not quote to support.
   */
  protected readonly declaredKeysFailureReference = computed<string | null>(() => {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    return state.kind === 'failed' ? problemSupportReference(state.problem) : null;
  });

  /**
   * Whether the permission controls must be withheld: the vocabulary is either still being read or could
   * not be read at all.
   *
   * ⚠ THIS IS SCOPED TO THE PERMISSION CONTROLS, NOT THE WHOLE FORM, AND THE NARROWNESS IS THE POINT.
   * Inheritance decides which permission set applies, so choosing it against a vocabulary that was never
   * retrieved is the one decision this screen genuinely cannot support - runtime verification found the
   * checkbox live throughout a six-second read and still live after it failed. Everything else on the
   * screen - the title, the cache settings, the placement - is unaffected by that read and stays editable,
   * because refusing an entire settings screen over an advisory lookup would be the larger fault.
   */
  protected readonly permissionControlsWithheld = computed<boolean>(
    () => this.declaredKeysLoading() || this.declaredKeysUnread(),
  );

  protected readonly declaredKeysLoadingLabel = DECLARED_KEYS_LOADING_LABEL;

  protected readonly declaredKeysFailedLabel = DECLARED_KEYS_FAILED_LABEL;

  /** The wording of the control that re-issues the declared-permission read. */
  protected readonly declaredKeysRetryLabel = DECLARED_KEYS_RETRY_LABEL;

  /** The caption for the advisory key line. */
  protected readonly definitionPermissionKeysLabel = DEFINITION_PERMISSION_KEYS_LABEL;

  /** The grant grid's caption. */
  protected readonly permissionGridCaption = PERMISSION_GRID_CAPTION;

  /** The name of the grid's row-header column. */
  protected readonly permissionGridRoleHeading = PERMISSION_GRID_ROLE_HEADING;

  /** What the grid says when the definition declares no permissions at all. */
  protected readonly permissionGridEmptyMessage = PERMISSION_GRID_EMPTY_MESSAGE;

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
      case 'border':
        // The legacy `valBorder` message, reported once the operator has touched the field, matching every
        // other data-type check on this screen.
        return this.form.controls.border.touched
          && this.form.controls.border.hasError(INTEGER_TYPE_ERROR)
          ? this.borderInvalidMessage
          : null;
      case 'visibility':
        // ⚠ REPORTED WITHOUT WAITING FOR `touched`, UNLIKE EVERY ARM ABOVE IT, and the asymmetry is the
        // point: this failure describes the value the SERVER supplied, not one the operator typed, so
        // waiting for them to touch the control would withhold the explanation until after they had
        // already been surprised by it.
        return this.form.controls.visibility.hasError(UNPUBLISHED_VISIBILITY_ERROR)
          ? UNPUBLISHED_VISIBILITY_MESSAGE
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

    // ⚠ THE GRANTS ARE WRITTEN ALONGSIDE THE MODULE, NOT INSTEAD OF IT. `ModuleSettings.ascx.vb:L378-L379`
    // assigned `objModule.ModulePermissions` and `objModule.InheritViewPermissions` onto the same object
    // before one `UpdateModule`, so both reached the store together. Here they are two requests, and both
    // carry the same inheritance value, so whichever lands second stores what the operator chose.
    this.persistPermissions(id);

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

      // ⚠ `textOrNull` IS DELIBERATELY NOT USED ON ALIGNMENT. The empty string is a REAL stored alignment -
      // the legacy list's "Not Specified" item carried exactly that value - so collapsing it to null would
      // change what the column holds rather than clearing a field the operator left blank.
      alignment: raw.alignment,
      color: textOrNull(raw.color),
      border: textOrNull(raw.border),

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
      alignment: value.alignment ?? '',
      color: value.color ?? '',
      border: value.border ?? '',
      // Bound by enumeration member. `None` is a chosen value and 0 is meaningful, so no fallback applies.
      visibility: value.visibility,
      displayTitle: value.displayTitle,
      // ⚠ THE DEFINITION'S DEFAULT IS USED, WHERE IT PREVIOUSLY WAS NOT. `defaultCacheTime` was fetched -
      // this screen reads the definition to decide whether to offer the field at all - and then ignored, so
      // a placement with no stored period showed a literal 0 rather than the period its module type
      // declares. The legacy screen seeded from `objModule.CacheTime` (L141), which for an unset column
      // printed the -1 sentinel; neither the sentinel nor a wrong zero is shown here.
      cacheTime: this.seedCacheTime(value.cacheTime),
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
   * Locks or releases the inherit switch according to whether the permission vocabulary is known.
   *
   * ⚠ APPLIED HERE RATHER THAN IN THE TEMPLATE, AND THE REASON IS A FRAMEWORK CONSTRAINT RATHER THAN A
   * PREFERENCE. Angular's reactive-form directive owns the disabled state of a control it is bound to and
   * discards an `[attr.disabled]` binding on that element - measured, with `aria-disabled` applied and the
   * DOM property still false, which is the worst of both outcomes: announced as disabled and fully
   * operable. The lock therefore goes through the control. `emitEvent: false` keeps it from registering as
   * an edit, and the submission reads `getRawValue()`, which includes disabled controls, so the operator's
   * choice still reaches the server.
   */
  private readonly mirrorInheritSwitch = effect((onCleanup) => {
    // Seeded before subscribing, because the control already holds a value by the time this runs - the form
    // is patched from the loaded module - and a subscription alone would not report a value it did not see
    // change.
    const control = this.form.controls.inheritViewPermissions;

    untracked(() => {
      this.inheritViewLive.set(control.value);
    });

    const subscription = control.valueChanges.subscribe((value: boolean) => {
      this.inheritViewLive.set(value);
    });

    onCleanup(() => {
      subscription.unsubscribe();
    });
  });

  private readonly lockPermissionControls = effect(() => {
    const withheld: boolean = this.permissionControlsWithheld();

    untracked(() => {
      const control = this.form.controls.inheritViewPermissions;

      if (withheld === control.disabled) {
        return;
      }

      if (withheld) {
        control.disable({ emitEvent: false });
      } else {
        control.enable({ emitEvent: false });
      }
    });
  });

  /**
   * Re-measures the permission scroller whenever the grid's geometry can have changed.
   *
   * Reads the row and column counts rather than the grid object, because those are what change the table's
   * width: a column set arriving, or the grid being re-read after a save. The viewport half is covered
   * separately by {@link observePermissionScroller}, since a window resize changes the answer without
   * changing any of these.
   */
  private readonly measureGridOnChange = effect(() => {
    const present: boolean = this.permissionGridPresent();
    const columns: number = this.permissionColumns().length;
    const rows: number = this.permissionRows().length;

    untracked(() => {
      if (!present || columns === 0 || rows === 0) {
        this.permissionScrollerClipsSignal.set(false);

        return;
      }

      this.schedulePermissionScrollerMeasurement();
    });
  });

  /**
   * Watches the viewport and the scroller's own box for changes the effect above cannot see.
   *
   * ⚠ BOUND ONCE, IN A FIELD INITIALISER, and observing THIS SCREEN'S ELEMENT rather than the scroller. The
   * scroller does not exist until the grid has been read, so an observer bound to it would have to be
   * rebound on every read and unbound on every failure; the host is present for the component's whole life
   * and its subtree resizes whenever the grid inside it does.
   */
  private readonly observePermissionScroller: void = ((): void => {
    if (typeof window === 'undefined') {
      return;
    }

    const onViewportChange = (): void => {
      this.schedulePermissionScrollerMeasurement();
    };

    window.addEventListener('resize', onViewportChange, { passive: true });

    this.destroyRef.onDestroy(() => {
      window.removeEventListener('resize', onViewportChange);
    });

    if (typeof ResizeObserver === 'undefined') {
      return;
    }

    const observer = new ResizeObserver(() => {
      this.schedulePermissionScrollerMeasurement();
    });

    observer.observe(this.host.nativeElement);

    this.destroyRef.onDestroy(() => {
      observer.disconnect();
    });
  })();

  /**
   * The grant grid as it should be rendered: the server's rows and cells, reconciled with the operator's
   * unsaved edits and with the LIVE state of the inheritance switch.
   *
   * MIGRATION: this is `ModulePermissionsGrid.GetPermission` and `GetEnabled` (`ModulePermissionsGrid.vb`
   * L237-L340) evaluated on the client, and only for the one input the server cannot know yet - the
   * unsaved state of the switch. Everything else the server already decided: which row is the
   * administrator's, and what each stored grant is. The legacy switch carried `autopostback="true"`
   * precisely so the server could re-decide the view column; here the column re-decides itself.
   */
  protected readonly permissionRows = computed<readonly PermissionRowView[]>(() => {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind !== 'ready') {
      return EMPTY_PERMISSION_ROWS;
    }

    const grid: ModulePermissionGrid = state.grid;
    const edits: ReadonlyMap<string, boolean> = this.permissionEdits();
    const inheriting: boolean = this.inheritViewLive();
    const inheritedKey: string | null = grid.inheritedPermissionKey;

    const roleRows: PermissionRowView[] = grid.roles.map((row: ModulePermissionRoleRow) =>
      this.projectPermissionRow(
        'role',
        row.roleId,
        row.roleName,
        row.isAdministrator,
        row.cells,
        edits,
        inheriting,
        inheritedKey,
      ),
    );

    const userRows: PermissionRowView[] = grid.users.map((row: ModulePermissionUserRow) =>
      this.projectPermissionRow(
        'user',
        row.userId,
        row.displayName,
        false,
        row.cells,
        edits,
        inheriting,
        inheritedKey,
      ),
    );

    return [...roleRows, ...userRows];
  });

  /** The grid's column headers, or an empty list until the grid has been read. */
  protected readonly permissionColumns = computed<readonly ModulePermissionColumnView[]>(() => {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind !== 'ready') {
      return EMPTY_PERMISSION_COLUMNS;
    }

    const inheriting: boolean = this.inheritViewLive();

    return state.grid.definitions.map((definition) => ({
      permissionId: definition.permissionId,
      permissionKey: definition.permissionKey,
      permissionName: definition.permissionName,
      inherited: inheriting && definition.permissionKey === state.grid.inheritedPermissionKey,
    }));
  });

  /**
   * Whether the grid has been read and has at least one column, so the table is worth rendering at all.
   */
  protected readonly permissionGridPresent = computed<boolean>(
    () => this.permissionColumns().length > 0,
  );

  /**
   * Whether the grid was read and reports no columns - a real answer, and the only case in which the
   * table is deliberately replaced by a sentence.
   */
  protected readonly permissionGridEmpty = computed<boolean>(
    () => this.declaredPermissionKeysState().kind === 'ready' && this.permissionColumns().length === 0,
  );

  /** Whether the permission scroller is actually clipping its table. */
  private readonly permissionScrollerClipsSignal = signal(false);

  /**
   * Whether the permission scroller is actually clipping, which is what decides whether it declares itself
   * a region, takes a tab stop and borrows the table's name.
   *
   * ⚠ CONDITIONAL, EXACTLY AS THE SHARED GRID'S REGION IS. A grant grid that fits contributes no landmark
   * and no focus stop, because naming a region that cannot scroll puts an unusable stop on the page. This
   * matches `data-table.component`'s `isHorizontallyScrollable`, and deliberately so: the two scrollers now
   * behave identically, which is the whole point of reusing the published `[data-table-scroll]` contract
   * rather than leaving this one a bare overflow box reachable by pointer alone.
   */
  protected readonly permissionScrollerClips = this.permissionScrollerClipsSignal.asReadonly();

  /** The identifier of the grid's caption, which names the scrolling region as well as the table. */
  protected readonly permissionGridCaptionId = `${this.controlId('permissions')}-grid-caption`;

  /** Set while a measurement is already queued, so a burst of changes takes one reading rather than many. */
  private permissionScrollerMeasurementQueued = false;

  /**
   * Queues a scroller measurement for the next frame.
   *
   * Measured after paint rather than during projection, because the answer is geometry: the table's width
   * against its container's, neither of which exists until the rows the operator just revealed are laid out.
   */
  private schedulePermissionScrollerMeasurement(): void {
    if (this.permissionScrollerMeasurementQueued || typeof requestAnimationFrame === 'undefined') {
      return;
    }

    this.permissionScrollerMeasurementQueued = true;

    requestAnimationFrame(() => {
      this.permissionScrollerMeasurementQueued = false;
      this.measurePermissionScroller();
    });
  }

  /** Recomputes whether the permission scroller clips, and publishes the answer. */
  private measurePermissionScroller(): void {
    const container = this.host.nativeElement.querySelector<HTMLElement>(
      '.module-settings__permission-grid-scroll',
    );

    if (container === null) {
      this.permissionScrollerClipsSignal.set(false);

      return;
    }

    // The same tolerance the shared grid applies, and for the same reason: these are integer properties, so
    // a sub-pixel difference between a table and its container must not be reported as hidden content.
    this.permissionScrollerClipsSignal.set(
      container.scrollWidth - container.clientWidth > PERMISSION_SCROLLER_TOLERANCE_PX,
    );
  }

  /**
   * The reasons actually in play, each with the element that states it.
   *
   * ⚠ DERIVED FROM THE RENDERED CELLS, NOT DECLARED. A reason no cell bears must not be stated, or the
   * legend tells the operator that something is locked when nothing is: turning the inheritance switch off
   * has to remove the inherited sentence from the page, not merely stop pointing at it. Because the source
   * is `permissionRows()`, which already tracks the live switch, that happens without a second mechanism.
   */
  protected readonly permissionLockLegend = computed<readonly PermissionLockLegendEntry[]>(() => {
    const inPlay = new Set<PermissionLockKind>();

    for (const row of this.permissionRows()) {
      for (const cell of row.cells) {
        if (cell.lock !== null) {
          inPlay.add(cell.lock);
        }
      }
    }

    // Filtered from the constant rather than from the set, so the legend's order is the declared one and
    // does not shift with whichever row happened to be projected first.
    return PERMISSION_LOCK_SENTENCES.filter((entry) => inPlay.has(entry.kind)).map(
      (entry): PermissionLockLegendEntry => ({
        id: this.permissionLockId(entry.kind),
        sentence: entry.sentence,
      }),
    );
  });

  /**
   * The identifier of the element stating one reason.
   *
   * @param kind The reason.
   * @returns A document-unique identifier.
   */
  private permissionLockId(kind: PermissionLockKind): string {
    // The same instance prefix the cell identifiers use, so two of this screen on one document cannot
    // collide on the legend either.
    return `${this.controlId('permissions')}-lock-${kind}`;
  }

  /**
   * The element a locked cell's description points at, or `null` when the cell is operable.
   *
   * @param cell The cell.
   * @returns The identifier, or `null`.
   */
  protected permissionCellDescribedBy(cell: PermissionCellView): string | null {
    return cell.lock === null ? null : this.permissionLockId(cell.lock);
  }

  /**
   * Projects one row of the server's grid into its rendered form.
   *
   * @param kind Whether the row names a role or an individually named account.
   * @param principalId The role or account identifier.
   * @param name The row's visible name.
   * @param isAdministrator Whether the row is the portal's administrator role.
   * @param cells The server's cells for the row.
   * @param edits The operator's unsaved edits.
   * @param inheriting The LIVE state of the inheritance switch.
   * @param inheritedKey The key the switch collapses, or `null` when the definition declares none.
   * @returns The rendered row.
   */
  private projectPermissionRow(
    kind: 'role' | 'user',
    principalId: number,
    name: string,
    isAdministrator: boolean,
    cells: readonly ModulePermissionCell[],
    edits: ReadonlyMap<string, boolean>,
    inheriting: boolean,
    inheritedKey: string | null,
  ): PermissionRowView {
    const key = `${kind}:${principalId}`;

    return {
      key,
      principalId,
      kind,
      name,
      isAdministrator,
      cells: cells.map((cell: ModulePermissionCell): PermissionCellView => {
        const inheritedColumn: boolean = cell.permissionKey === inheritedKey;
        const collapsed: boolean = inheriting && inheritedColumn;

        if (collapsed) {
          // The switch wins over both the stored grant and any edit, exactly as the legacy override did:
          // it tested inheritance FIRST and returned before consulting the administrator rule or the row.
          //
          // ⚠ AND THAT PRECEDENCE GOVERNS THE EXPLANATION AS WELL AS THE STATE, INCLUDING ON THE
          // ADMINISTRATOR ROW. It is tempting to argue the other way - the administrator lock is permanent
          // while inheritance is transient, so it looks like the more specific reason - and it is wrong,
          // because the sentence has to explain the box a reader is actually looking at. While inheritance
          // is on, the administrator row's view box is rendered UNCHECKED, exactly as the legacy grid
          // rendered it. Putting the administrator sentence there would place "portal administrators always
          // hold every module permission" beside a visibly withheld permission - a statement the screen
          // itself contradicts - and it would resume over-applying the very sentence whose over-application
          // is the defect this projection was rewritten to fix.
          //
          // The invariant to preserve is therefore narrower and checkable: A LOCK SENTENCE NEVER CONTRADICTS
          // ITS OWN BOX. Measured across all three reachable states of that one cell, it holds - inheritance
          // on and unchecked cites inheritance; inheritance on, edit column and checked cites the
          // administrator rule; inheritance off and checked cites the administrator rule. A spec pins it.
          return {
            permissionId: cell.permissionId,
            permissionKey: cell.permissionKey,
            allowAccess: false,
            editable: false,
            lock: 'inherited',
          };
        }

        if (isAdministrator) {
          return {
            permissionId: cell.permissionId,
            permissionKey: cell.permissionKey,
            allowAccess: true,
            editable: false,
            lock: 'administrator',
          };
        }

        // ⚠ THE SERVER'S OWN `editable` IS HONOURED for every column EXCEPT the one the switch governs.
        // It carries reasons this client does not model, and a cell the server locked for one of those
        // reasons must never be offered, because a submission it produced would be refused or dropped.
        //
        // The inherited column is the one exception, and it has to be: the server computes that column's
        // `editable` from the STORED inheritance state, so while the module is stored as inheriting EVERY
        // cell in it arrives locked - see `PermissionService.BuildRoleRow`, where the inherited column
        // short-circuits ahead of the administrator rule, and `BuildPermissionUserRowsAsync`, where
        // `Editable` is simply `!inheritedColumn`. That flag therefore describes the state the operator is
        // in the act of leaving, and says nothing about the state they have moved to. Deferring to it left
        // the whole column dead after the switch was cleared: turning inheritance off could never unlock
        // anything, because the only fact consulted had been computed under inheritance being on.
        //
        // With the switch off this client can establish the column's editability itself, from the same two
        // rules the server applies: the administrator row is handled above and has already returned, and
        // every other row may be granted. Nothing is assumed about the OTHER columns, whose server flags
        // are independent of the switch and remain authoritative.
        const editable: boolean = inheritedColumn ? true : cell.editable;
        const edited: boolean | undefined = edits.get(`${key}:${cell.permissionId}`);

        return {
          permissionId: cell.permissionId,
          permissionKey: cell.permissionKey,
          // The stored grant is not recoverable here: the server clears the inherited column's
          // `allowAccess` rather than reporting what the module holds, so an un-inherited box opens
          // unticked. That matches the legacy grid, which also cleared the column, and the operator now
          // has an editable box to tick.
          allowAccess: edited ?? cell.allowAccess,
          editable,
          lock: editable ? null : 'withheld',
        };
      }),
    };
  }

  /**
   * Records one cell edit.
   *
   * @param row The row the cell belongs to.
   * @param cell The cell being changed.
   * @param granted Whether the box is now ticked.
   */
  protected onPermissionCellToggle(
    row: PermissionRowView,
    cell: PermissionCellView,
    granted: boolean,
  ): void {
    if (!cell.editable) {
      return;
    }

    const next = new Map(this.permissionEdits());
    next.set(`${row.key}:${cell.permissionId}`, granted);
    this.permissionEdits.set(next);

    // The grid is part of this form's unsaved work, so the guard that asks before leaving must see it.
    this.form.markAsDirty();
  }

  /**
   * The identifier of one cell's checkbox, so its label can address it.
   *
   * @param row The row the cell belongs to.
   * @param cell The cell.
   * @returns A document-unique identifier.
   */
  protected permissionCellId(row: PermissionRowView, cell: PermissionCellView): string {
    // Built from the same instance prefix `controlId` uses, so two of this screen on one document cannot
    // collide, but NOT through `controlId` itself - that member's parameter is the union of the form's own
    // control names, and a grid cell is not one of them.
    return `${this.controlId('permissions')}-cell-${row.key.replace(':', '-')}-${cell.permissionId}`;
  }

  /**
   * The accessible name of one cell, which must name BOTH the row and the column.
   *
   * A bare checkbox in a grid announces only its own state, so without this a screen-reader user hears
   * "checkbox, checked" twenty times with nothing to distinguish one from another.
   *
   * @param row The row the cell belongs to.
   * @param cell The cell.
   * @returns The name.
   */
  protected permissionCellLabel(row: PermissionRowView, cell: PermissionCellView): string {
    return `${cell.permissionKey} permission for ${row.name}`;
  }

  /**
   * Builds the grant replacement this screen would submit.
   *
   * ⚠ ONLY GRANTED, EDITABLE CELLS TRAVEL, and that is the legacy contract rather than an economy. The
   * legacy grid's collection held exactly the ticked boxes - `UpdatePermission` removed an entry the moment
   * its box was cleared, "as we only keep AllowAccess permissions" - and the administrator row was never in
   * it at all, because its grants are implicit. A collapsed view cell contributes nothing for the same
   * reason it did there.
   *
   * @returns The replacement, or `null` when the grid has not been read and so must not be replaced.
   */
  private toPermissionReplacement(): ModulePermissionReplacement | null {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind !== 'ready') {
      return null;
    }

    const grants: ModulePermissionGrantInput[] = [];

    for (const row of this.permissionRows()) {
      for (const cell of row.cells) {
        if (!cell.editable || !cell.allowAccess) {
          continue;
        }

        grants.push({
          permissionId: cell.permissionId,
          roleId: row.kind === 'role' ? row.principalId : null,
          userId: row.kind === 'user' ? row.principalId : null,
          allowAccess: true,
        });
      }
    }

    return {
      inheritViewPermissions: this.form.controls.inheritViewPermissions.value,
      grants,
    };
  }

  /**
   * Writes the grant grid, but ONLY when this screen actually holds one and the operator changed it or the
   * inheritance switch.
   *
   * A replace that nobody asked for is not free: it deletes and re-inserts every row, and it would do so on
   * every settings save of every module, including ones whose grid the operator never opened.
   *
   * @param moduleId The module whose grants to write.
   */
  private persistPermissions(moduleId: number): void {
    const state: DeclaredKeysState = this.declaredPermissionKeysState();

    if (state.kind !== 'ready') {
      return;
    }

    const inheritChanged: boolean =
      state.grid.inheritViewPermissions !== this.form.controls.inheritViewPermissions.value;

    if (this.permissionEdits().size === 0 && !inheritChanged) {
      return;
    }

    const replacement = this.toPermissionReplacement();

    if (replacement === null) {
      return;
    }

    this.store.savePermissions(moduleId, replacement);
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
        false,
        null,
        operationScope(operation),
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
        false,
        null,
        operationScope(operation),
      );
      return;
    }

    // MIGRATION: THE BANNER IS A NET-NEW AFFORDANCE, NOT A TRANSLATION. Everything else - a rejected write
    // above all - goes to it with the problem document intact, so the per-field messages reach the fields
    // they belong to.
    this.currentProblem.set(problem);

    if (problem === null) {
      this.notifications.notify(
        'error',
        `The ${operation} request could not be completed.`,
        null,
        false,
        null,
        operationScope(operation),
      );
    }
  }

  /**
   * Returns to the listing, which is what the legacy redirect at L421 did.
   *
   * ⚠ THE LISTING'S OWN COORDINATE TRAVELS WITH THE NAVIGATION, and the measured defect this closes is that
   * it did not. The listing keeps its page, ordering and search in the ADDRESS, so navigating to the bare
   * route dropped all three: runtime testing sorted the grid by title descending and searched for "QA" - 7
   * rows - then opened a module's settings, pressed Cancel and landed on an unsorted, unfiltered 8-row page
   * one, with the search box emptied and the module they had just been looking at somewhere else on screen.
   *
   * @param replaceEntry Whether to replace the current history entry rather than adding one.
   */
  private returnToListing(replaceEntry = false): void {
    const coordinate: Params = this.listReturn.coordinateFor(MODULE_LIST_ROUTE);

    if (replaceEntry) {
      void this.router.navigate([MODULE_LIST_ROUTE], { queryParams: coordinate, replaceUrl: true });

      return;
    }

    void this.router.navigate([MODULE_LIST_ROUTE], { queryParams: coordinate });
  }
}

