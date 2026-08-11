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
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

/**
 * The seven disclosure regions this screen presents, named after the legacy section heads they replace.
 *
 * Three are top level and four are nested one level beneath them, reproducing the two-level hierarchy of
 * `Website/admin/Modules/modulesettings.ascx`: dshModule (L11) holds dshDetails (L22) and dshSecurity (L54);
 * dshPage (L97) holds dshAppearance (L108) and dshOther (L178); dshSpecific (L200) stands alone.
 *
 * MIGRATION: `specificSettings` survives as a REGION rather than as a working surface. Its legacy body was
 * an .ascx loaded at run time by the excluded Web Forms module loader (`PortalModuleBase.vb`, 881 lines,
 * `PaWriter.vb`, 563, `EventMessageProcessor.vb`, 125), and no endpoint replaces that mechanism. The region
 * is retained so a definition-contributed surface has a declared home, and it renders no controls.
 */
export type ModuleSettingsSection =
  | 'moduleSettings'
  | 'details'
  | 'security'
  | 'pageSettings'
  | 'appearance'
  | 'other'
  | 'specificSettings';

/**
 * The minimum a caller must supply for this screen to seed itself.
 *
 * Declared structurally, and deliberately NARROWER than the module read contract, for two reasons. It names
 * exactly the facts this screen displays or edits, so a reader can see the screen's real dependency surface
 * without opening the wire contract; and because it is structural, the loaded `ModuleDetail` satisfies it
 * without a cast or an adapter.
 *
 * MIGRATION: the six placement columns the legacy screen edited - paneName, alignment, color, border,
 * displayPrint and displaySyndicate - are absent here BECAUSE THEY ARE ABSENT FROM THE WIRE CONTRACT. See
 * the note on {@link ModuleSettingsComponent} for the measurement and the consequence.
 */
export interface ModuleSettingsSeed {
  /**
   * The module being edited.
   *
   * `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so **0 is a real module** and is never read as absent.
   */
  readonly moduleId: number;

  /** The placement being edited. */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on.
   *
   * `dbo.Tabs.TabID` is `IDENTITY(0, 1)`, so **0 is a real page**.
   */
  readonly tabId: number;

  /** The tenant this module belongs to, or `null` for a host-level module. */
  readonly portalId: number | null;

  /** The definition this module instantiates. Used to resolve the definition's default cache period. */
  readonly moduleDefId: number;

  /** The definition's own name, shown read-only. */
  readonly friendlyName: string | null;

  /** The operator-supplied heading, or `null` when none is recorded. */
  readonly moduleTitle: string | null;

  /** The module's position within its pane. Round-tripped; this screen does not reorder. */
  readonly moduleOrder: number;

  /** Whether the module appears on every page. Round-tripped; the bulk affordance is not offered. */
  readonly allTabs: boolean;

  /** The soft-delete marker. Round-tripped deliberately - see {@link ModuleSettingsComponent}. */
  readonly isDeleted: boolean;

  /** Whether View permission is inherited from the page. Round-tripped; grants are not editable here. */
  readonly inheritViewPermissions: boolean;

  /** Text rendered above the module's content. */
  readonly header: string | null;

  /** Text rendered below the module's content. */
  readonly footer: string | null;

  /** The display start instant, or `null`. May carry the legacy date sentinel. */
  readonly startDate: string | null;

  /** The display end instant, or `null`. May carry the legacy date sentinel. */
  readonly endDate: string | null;

  /**
   * This placement's cache period in seconds. A DIFFERENT FACT from the definition's default.
   *
   * Accepts `null` defensively even though the read contract declares the member non-nullable, because a
   * host may seed this screen from a projection that does not carry it. `null` seeds the control as 0, which
   * is what the legacy `objModule.CacheTime.ToString` produced for a value hydrated through `Null.SetNull`.
   * `null` here means "no period was supplied to seed with"; a period OF 0 means "not cached". Neither is
   * the definition's -1, which means "this definition records no default at all".
   */
  readonly cacheTime: number | null;

  /** The icon shown with the title. Round-tripped; no file-selection endpoint exists. */
  readonly iconFile: string | null;

  /** How the placement is presented. `None` is a chosen value, never an absence. */
  readonly visibility: ModuleVisibility;

  /** Whether the container chrome is displayed. */
  readonly displayTitle: boolean;
}

/**
 * The typed shape of this screen's form.
 *
 * Every control is declared `nonNullable`, so `getRawValue()` is fully typed rather than a `Partial`, and a
 * reset returns each control to its declared initial value instead of to `null`.
 *
 * MIGRATION: THE THREE COERCED FIELDS ARE HELD AS TEXT, NOT AS NUMBERS OR DATES, AND THAT IS DELIBERATE.
 * `Website/release.config:L125` is verbatim `<compilation debug="false" strict="false">`, so the 39 admin
 * code-behinds compiled with Option Strict OFF while the class library compiled with
 * `<OptionStrict>On</OptionStrict>` (`Library/DotNetNuke.Library.vbproj:L24`). The legacy screen therefore
 * held `txtCacheTime`, `txtStartDate` and `txtEndDate` as free text and coerced them implicitly at save
 * time - `Int32.Parse` at `ModuleSettings.ascx.vb:L350` and `Convert.ToDateTime` at L367-L376, neither of
 * them a `TryParse`. Holding the same three as text here keeps the coercion visible and lets it be made
 * explicit with real failure handling, which is exactly what the four data-type validators exist for. A
 * numeric or date-typed control would hide the coercion inside the value accessor and silence the parse.
 */
interface ModuleSettingsFormModel {
  /**
   * The page this placement is addressed on - the legacy "Move To Page:" picker (`cboTab`).
   *
   * MIGRATION: THE MOVE IS EXPRESSED AS A FIELD ON THE UPDATE, NEVER AS AN INVENTED ENDPOINT. The legacy
   * relocation was a second call, `MoveModule(ModuleId, TabId, newTabId, "")`
   * (`ModuleController.vb:L1078`), fired after the update at `ModuleSettings.ascx.vb:L403-L408`. That
   * operation has no counterpart in the module API, and `POST /modules/{moduleId}/move` is not invented to
   * supply one. `UpdateModuleRequest.tabId` is a required, non-nullable member that the service uses to
   * select the exact placement being edited, so the page travels on the update the operator already
   * submits. Naming a page the module does not occupy is refused with `module.placement_not_found`.
   */
  tabId: FormControl<number>;

  /**
   * The operator-supplied heading (`txtTitle`).
   *
   * MIGRATION: CARRIES NO VALIDATOR, AND THE ABSENCE IS THE REQUIREMENT. A census of
   * `Website/admin/Modules/` finds 0 `RequiredFieldValidator`, 0 `RegularExpressionValidator` and 0
   * `RangeValidator`; the only validators in the whole feature are four `CompareValidator`s, and none of
   * them targets `txtTitle`. Adding a presence or length rule here would reject input the legacy screen
   * accepted. The column bound is advertised through the native `maxlength` attribute instead, which
   * truncates rather than invalidating and so adds no rule the legacy screen did not have.
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

  /** An instruction, never echoed back from the module. Seeded unset on every load. */
  setAsDefaultSettings: FormControl<boolean>;

  /** An instruction, never echoed back from the module. Seeded unset on every load. */
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

/**
 * The inclusive bounds of a 32-bit signed integer.
 *
 * The legacy integer data-type check delegated to `Int32.Parse`, so a value outside this range failed the
 * check rather than silently wrapping. Reproducing the range keeps that behaviour.
 */
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

/** The error key the two date data-type checks report. */
const DATE_TYPE_ERROR = 'dateDataType';

/** The error key the two integer data-type checks report. */
const INTEGER_TYPE_ERROR = 'integerDataType';

/**
 * The regions that start closed.
 *
 * Taken from the legacy `isexpanded` attributes: dshSecurity, dshPage, dshOther and dshSpecific all declare
 * `isexpanded="False"`, while dshModule, dshDetails and dshAppearance open expanded. Exactly four of the
 * seven therefore start closed, which is what the accompanying stylesheet documents.
 */
const INITIALLY_COLLAPSED: readonly ModuleSettingsSection[] = [
  'security',
  'pageSettings',
  'other',
  'specificSettings',
];

/**
 * Section headings, taken from `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 *
 * MIGRATION: the resource file overrides the inline `title=` attribute the markup declares, and the two
 * disagree in one place that matters - the markup calls the second nested region "Security Settings" while
 * `Security.Text` is 'Advanced Settings'. The resource wins, because that is what the legacy screen actually
 * rendered. Two regions therefore legitimately share the heading 'Basic Settings' and two share 'Advanced
 * Settings'; each is disambiguated for assistive technology by the region it sits in, not by its wording.
 * Defect D-M10 and defect D13 - the two duplicate section-label pairs - are reproduced rather than
 * corrected, because renaming a region an operator recognises is not this migration's business.
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
 * The three explanatory paragraphs, one per top-level region.
 *
 * MIGRATION: reproduced character for character from the resource file. The first preserves the space before
 * its closing parenthesis exactly as `ModuleSettingsHelp.Text` carries it - see the note on FIELD_HINTS for
 * why every one of these strings is a bound constant rather than template text.
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

/**
 * Field labels, taken from the `pl*.Text` entries of the resource file.
 *
 * MIGRATION: `plDisplayTitle` is the trap here. The markup declares `text="Display Title?"` but
 * `plDisplayTitle.Text` is 'Display Container?', and the resource file is what the legacy screen rendered.
 * Taking the markup value would have relabelled a control that operators already know.
 */
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
 * `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 *
 * MIGRATION - WHY THESE ARE CONSTANTS AND NOT TEMPLATE TEXT: Angular compiles templates with
 * `preserveWhitespaces` disabled, which collapses every run of whitespace inside a text node to a single
 * space. Four of these strings put TWO spaces after a sentence period (`plStartDate.Help`, `plEndDate.Help`,
 * `plTitle.Help`, and `plPermissions.Help` twice), so writing them as template text would silently rewrite
 * wording an existing operator recognises. Interpolated values are not collapsed, so every migrated string
 * on this screen is declared here and bound.
 *
 * A second consequence, deliberate: because these are interpolated rather than parsed, the embedded bold
 * markup the legacy `InheritPermissions.Text` carried ('Inherit &lt;b&gt;View&lt;/b&gt; permissions from
 * &lt;b&gt;Page&lt;/b&gt;') renders as plain text. The emphasis is lost; the string cannot become an
 * injection vector. Localisation itself is NOT ported - the Angular localisation package sits outside the
 * pinned dependency set, so the 48 in-scope legacy localisation calls have no counterpart and these resource
 * values are read for their wording alone.
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

// =======================================================================================================
// THE FOUR DATA-TYPE VALIDATORS
// =======================================================================================================
//
// MIGRATION: THE ONLY VALIDATORS THIS SCREEN EVER HAD WERE FOUR `CompareValidator`s, AND ALL FOUR ARE
//   REPRODUCED AS REAL ANGULAR VALIDATORS. A case-insensitive census of `Website/admin/Modules/` returns
//   `asp:RequiredFieldValidator` 0, `asp:RegularExpressionValidator` 0, `asp:CompareValidator` 4,
//   `asp:CustomValidator` 0, `asp:RangeValidator` 0 and `asp:ValidationSummary` 0. The four are
//   `valtxtStartDate` (modulesettings.ascx L78, DataTypeCheck/Date), `valtxtEndDate` (L88,
//   DataTypeCheck/Date), `valBorder` (L138, DataTypeCheck/Integer) and `valCacheTime` (L172,
//   DataTypeCheck/Integer). A presence rule alone would NOT be parity, so none is used and all four
//   data-type checks are authored here. Two consequences are recorded rather than absorbed: the first two
//   carry an id/resource-key MISMATCH in the source - the ids read `valtxtStartDate`/`valtxtEndDate` while
//   the resource keys read `valStartDate.`/`valEndDate.` - which is reproduced as wording, not as a name;
//   and `asp:ValidationSummary` appears 0 times in the entire `Website/` tree, so the shared error banner
//   this screen renders is a NET-NEW affordance and not the migration of a legacy rendering path.
//
// MIGRATION: THE MESSAGES ARE AUTHORED WITHOUT THE LEADING BREAK TAG RATHER THAN STRIPPED AFTERWARDS.
//   Every one of the four resource values begins with a literal `<br>`: `valStartDate.ErrorMessage` is
//   '<br>Invalid Start Date', `valEndDate.ErrorMessage` is '<br>Invalid End Date',
//   `valCacheTime.ErrorMessage` is '<br>Invalid Cache Time' and `valBorder.ErrorMessage` is '<br>Invalid
//   Border (must be a number between 0 and 9)'. The tag was layout, not wording - it pushed the message
//   onto its own line inside a table cell, which a stylesheet now does. Emitting it and then removing it
//   would put markup through a text sink for no gain, so the wording is declared clean at source. Stripping
//   remains the job of `core/utils/form-errors.util.ts` for SERVER-supplied strings, which this file
//   delegates to rather than re-implementing.

/** `valStartDate.ErrorMessage`, without the layout break tag the resource carries. */
const START_DATE_INVALID_MESSAGE = 'Invalid Start Date';

/** `valEndDate.ErrorMessage`, without the layout break tag the resource carries. */
const END_DATE_INVALID_MESSAGE = 'Invalid End Date';

/** `valCacheTime.ErrorMessage`, without the layout break tag the resource carries. */
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';

/**
 * `valBorder.ErrorMessage`, without the layout break tag the resource carries.
 *
 * MIGRATION: THE FOURTH VALIDATOR HAS NO CONTROL TO GUARD, AND THE REASON IS A CONTRACT GAP RATHER THAN AN
 * OMISSION HERE. `valBorder` guarded `txtBorder`, one of six placement columns - paneName, alignment,
 * color, border, displayPrint and displaySyndicate - that `Dtos/Module/UpdateModuleRequest.cs` does not
 * project, as `core/models/module.model.ts` records at its own update contract. The border therefore cannot
 * be transported, so rendering an input for it could only ever produce an HTTP 400 under the API's
 * `JsonUnmappedMemberHandling.Disallow` setting. The rule and its exact wording are preserved here, and the
 * integer check below is the same rule `valBorder` declared, so the moment the server projects the column
 * the control can be added without re-deriving anything. Note the wording states a 0-9 range while the
 * declared validator was a plain integer data-type check - a legacy inconsistency, reproduced verbatim.
 */
const BORDER_INVALID_MESSAGE = 'Invalid Border (must be a number between 0 and 9)';

/**
 * Reproduces `asp:CompareValidator Operator="DataTypeCheck" Type="Integer"`.
 *
 * Blank passes, exactly as every ASP.NET validator did: an empty control was the `RequiredFieldValidator`'s
 * business and this screen declares none. A non-blank value must be an integer `Int32.Parse` would have
 * accepted, which means an optional sign, digits only, and a magnitude inside the 32-bit range.
 *
 * MIGRATION: A NEGATIVE VALUE PASSES, BECAUSE IT PASSED. `Type="Integer"` checks the TYPE and nothing else,
 * so the legacy screen accepted a cache period of -1 and stored it. That is a latent legacy defect and it is
 * annotated rather than corrected: adding a lower bound here would be exactly the opportunistic optimisation
 * the migration discipline forbids, and it would reject input the legacy screen accepted.
 *
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
 * Reproduces `asp:CompareValidator Operator="DataTypeCheck" Type="Date"`.
 *
 * Blank passes, for the reason given on {@link integerDataTypeCheck}. A non-blank value must be a date the
 * runtime can resolve. The `yyyy-mm-dd` form a date-capable control produces is checked for CALENDAR
 * correctness rather than merely for shape, because `Date.parse` accepts '2024-02-31' and silently rolls it
 * forward to the first of March - which would let an operator save a day that does not exist and see a
 * different one come back.
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

    // The legacy control was a free-text box, so a value the operator typed in any form the runtime could
    // resolve was accepted. `Number.isNaN` on the parsed instant is that same test.
    return Number.isNaN(Date.parse(text)) ? { [DATE_TYPE_ERROR]: message } : null;
  };
}

/**
 * Whether a `yyyy`, `mm`, `dd` triple names a day that exists.
 *
 * Built in UTC and compared component by component, so a rolled-over value such as 31 February fails
 * instead of being accepted as 2 or 3 March.
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
 * Whether an instant carries the legacy null-date sentinel.
 *
 * MIGRATION: THE COMPARISON IS ON THE UTC DATE PART ALONE, BECAUSE THAT IS WHAT THE LEGACY COMPARISON WAS.
 * `Library/Components/Shared/Null.vb` sets `NullDate` to `Date.MinValue` (L68) and its `IsNull` overload
 * for dates (L222-L224) compares `objDate.Date.Equals(NullDate.Date)` under the source's own comment about
 * avoiding "subtle time differences". Any instant whose calendar date is 0001-01-01 is therefore the
 * sentinel EVEN WITH A NON-ZERO TIME COMPONENT, which is why this is a three-component test and not a
 * timestamp equality: `getTime() === -62135596800000` would miss `0001-01-01T09:30:00Z`, and a year
 * threshold such as `year < 1900` would wrongly blank a genuinely stored early date.
 *
 * The upper sentinel has no counterpart. 9999-12-31 is a REAL value in this schema and must render.
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

  // MIGRATION: THE UTC DATE PART ALONE, NEVER THE TIMESTAMP. `Null.vb:L222-L224` compares
  // `objDate.Date.Equals(NullDate.Date)` with the standing comment that this "avoids subtle time
  // differences", so ANY instant whose calendar day is 0001-01-01 is the sentinel even with a non-zero time
  // component. A `getTime()` equality against `Date.MinValue` would miss exactly those rows, and a year
  // threshold would blank real dates. There is no date library in the pinned dependency surface, so the three
  // UTC accessors are the whole mechanism.
  return (
    parsed.getUTCFullYear() === 1 && parsed.getUTCMonth() === 0 && parsed.getUTCDate() === 1
  );
}

/**
 * Narrows an instant to the date a date-capable control expects.
 *
 * The value is SLICED, never parsed, once the sentinel test has passed. Constructing a `Date` and then
 * formatting it locally shifts the value into the browser's zone and moves the date by a day either side of
 * midnight, so a module scheduled to appear on the first of the month would be shown - and written back - as
 * the last day of the previous one.
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
 * MIGRATION: this applies to the NULLABLE TEXT members only - the heading, the header, the footer, the icon
 * and the two dates. It must never be generalised into "drop falsy values": the update body is a whole-row
 * replacement in which `false` and `0` are data, and the settings body is a pair of string maps in which the
 * empty string is a legitimate stored value.
 *
 * @param value The control's text.
 * @returns The trimmed text, or `null` when nothing was entered.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length > 0 ? trimmed : null;
}

/**
 * Whether two property maps hold different content.
 *
 * Compared in BOTH directions rather than by size and then by lookup: a map that lost one key and gained
 * another has the same size as the map it came from, so a one-way walk would report the pair identical. The
 * key count is checked first because it settles the commonest difference in one step, and then every key of
 * the reference is looked up in the candidate.
 *
 * An absent key and a key holding an empty string are DIFFERENT here, deliberately. The empty string is a
 * legitimate stored setting value, so `undefined` from a lookup cannot be treated as equal to it.
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
 * Resolves a route-supplied identifier to a number.
 *
 * MIGRATION: EVERY GUARD HERE IS `undefined`-BASED, NEVER TRUTH-BASED, AND THAT IS A SCHEMA CONSTRAINT.
 * `dbo.Modules.ModuleID` and `dbo.Tabs.TabID` are both `IDENTITY(0, 1)`
 * (01.00.00.SqlDataProvider L221 and L140), so **0 is the first real row of each table**. `if (id)`,
 * `id > 0`, `id ?? 0` and every relative of theirs would treat module 0 and page 0 as absent. Router inputs
 * arrive as strings, so the coercion is explicit and its failure is explicit too.
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

/**
 * The message shown when no module has resolved.
 *
 * The screen is addressed by identifier, so an unresolved identifier is a legitimate state rather than an
 * error: it is what a stale bookmark or an already-removed module produces.
 */
const NO_MODULE_MESSAGE = 'No module settings are available.';

/**
 * The caption for the advisory list of keys this module's definition declares.
 *
 * MIGRATION: THE LEGACY SCREEN SHOWED THE VOCABULARY AS GRID COLUMN HEADS, SO IT NEVER NEEDED A CAPTION.
 * `Website/admin/Modules/modulesettings.ascx` embedded a permission grid whose columns WERE the declared
 * keys, which is why its own hint text — reproduced verbatim on the permissions field above — instructs the
 * operator to "check/uncheck the boxes in the grid". That grid is a separate resource with its own screen
 * here, so without this line the inherit switch would sit alone under a "Permissions:" caption with nothing
 * naming what it inherits. Read-only, and phrased as a statement about the DEFINITION rather than about the
 * caller, because that is what the catalogue answers.
 */
const DEFINITION_PERMISSION_KEYS_LABEL = 'Permissions defined for this module type:';

/** Confirmation wording for a completed save, at success severity. */
const SAVED_MESSAGE = 'The module settings were saved.';

/** Confirmation wording for a completed removal, at success severity. */
const REMOVED_MESSAGE = 'The module was removed from this page.';

/**
 * The visibility choices, from `modulesettings.ascx:L144-L148`.
 *
 * MIGRATION: THE THREE ORDINALS ARE LOAD-BEARING AND `0` IS MEANINGFUL. `ModuleInfo.vb:L30-L34` declared a
 * public visibility enumeration with `Maximized`, `Minimized` and `None` and NO explicit values, so the
 * implicit 0, 1 and 2 are the stored codes and the markup's `value="0|1|2"` matches them exactly. The
 * enumeration is renamed to `ModuleVisibility` in the target and the legacy spelling appears nowhere.
 * `None` means "renders without its container chrome" - a rendering instruction the operator CHOSE - so it
 * is never treated as an absence and no expression of the form `visibility ?? Maximized` appears here.
 * Selection is by enumeration member, never by truthiness, which is also what structurally prevents the
 * legacy defect D-M2: `cboAlign.Items.FindByValue(...).Selected = True` at `ModuleSettings.ascx.vb:L144`
 * was unguarded and threw a `NullReferenceException` whenever the stored value was not one of the offered
 * items. A typed control cannot reach that state.
 */
const VISIBILITY_CHOICES: readonly SelectOption<ModuleVisibility>[] = [
  { value: MODULE_VISIBILITY.maximized, label: 'Maximized' },
  { value: MODULE_VISIBILITY.minimized, label: 'Minimized' },
  { value: MODULE_VISIBILITY.none, label: 'None' },
];

/**
 * The definition-level cache default that means "this definition records no default".
 *
 * `Null.vb:L43` returns -1 for `NullInteger`, and `ModuleSettings.ascx.vb:L138` compares the definition's
 * `DefaultCacheTime` against exactly that value.
 */
const NO_CACHE_DEFAULT = -1;

/**
 * The status a refusal arrives with.
 *
 * Surfaced at WARNING severity rather than error, on the legacy screen's own precedent: the whole of
 * `Website/admin/Security/AccessDenied.ascx.vb` is presentation, performs no permission check of its own,
 * and both branches of its `Page_Load` (L43 and L45) raise `ModuleMessage.ModuleMessageType.YellowWarning`.
 */
const FORBIDDEN_STATUS = 403;

/** The status an unresolved identifier arrives with. */
const NOT_FOUND_STATUS = 404;

/** The status a rejected write arrives with. */
const CONFLICT_STATUS = 409;

/** The route this screen returns to when the operator finishes or abandons. */
const MODULE_LIST_ROUTE = '/modules';

/**
 * The screen's heading when a caller supplies none.
 *
 * Taken from `ModuleSettings.ascx.resx`'s own `ModuleSettings.Text`, which is the wording the legacy screen
 * showed above its first section head.
 */
const DEFAULT_HEADING = 'Module Settings';

/**
 * One stored setting, as the module-specific section displays it.
 *
 * A view-model row rather than a slice of the wire contract: the scope is a LABEL here, resolved once from
 * which of the bag's two maps the entry came out of, so the template does not have to know that the
 * distinction exists or how it is spelled.
 */
interface ModuleSpecificSettingRow {
  /** The setting's name, bounded at 50 characters by its column. */
  readonly name: string;

  /** The stored value, bounded at 2000 characters by its column. Rendered as text and never as markup. */
  readonly value: string;

  /** Which scope the value belongs to, already worded for display. */
  readonly scope: string;
}

/**
 * How a setting recorded against the module itself is described.
 *
 * The wording states the CONSEQUENCE rather than naming the table, because that is what determines whether
 * an operator should care: a module-scoped value is the same wherever the module appears.
 */
/**
 * What is said when a module carries no stored settings of its own.
 *
 * AUTHORED, because the legacy screen had no way to say it: `pnlSpecific` was a placeholder that either
 * received a control or stayed silently empty, so an operator could not tell "this module has no settings"
 * apart from "the settings failed to load". Stating it closes that ambiguity.
 */
const NO_SPECIFIC_SETTINGS_MESSAGE = 'This module has no stored settings of its own.';

/**
 * The opening of the blank-heading disclosure, up to the name itself.
 *
 * Split from its closing half so the definition's own name is interpolated between them rather than
 * concatenated into a sentence fragment, which keeps the name a value and the wording a constant.
 */
const TITLE_FALLBACK_PREFIX = 'With no heading, this module is listed as “';

/** The close of the blank-heading disclosure, after the name. @see TITLE_FALLBACK_PREFIX */
const TITLE_FALLBACK_SUFFIX = '”, the name of its module definition.';

const MODULE_SCOPE_LABEL = 'this module, on every page';

/**
 * How a setting recorded against one placement is described.
 *
 * Worded to contrast with {@link MODULE_SCOPE_LABEL} on the one axis that separates them — a
 * placement-scoped value applies to this occurrence alone, so the same module elsewhere may differ.
 */
const PLACEMENT_SCOPE_LABEL = 'this placement only';

/**
 * The label used for the occupied page when the tenant's page list does not contain it.
 *
 * Reached when the module sits on a page the list omits - a host-level module, whose `portalId` is `null` and
 * for which no portal page list is read, or a page in the recycle bin. Naming it neutrally is honest: the
 * page's own name is genuinely unknown here, and inventing one would be worse than saying so.
 */
const CURRENT_PAGE_LABEL = 'This page';

/**
 * The module settings screen, replacing `Website/admin/Modules/modulesettings.ascx` and its code-behind.
 *
 * Mounted at `modules/:moduleId/settings`. The route parameter arrives on the {@link moduleId} input because
 * the application enables `withComponentInputBinding()`, which binds a parameter onto an input of the SAME
 * NAME - so this input's name is a contract with the route table and not a local choice.
 *
 * The screen owns its own orchestration. It reads and writes through `core/state/module.store.ts`, which is
 * the single place `ModuleService` and `TabService` are called from; no business logic lives in either
 * service, and this component never builds a URL, never sets a header - the correlation identifier is the
 * interceptor's job - and declares no provider of its own, because every provider is registered in
 * `app.config.ts`.
 *
 * DELIBERATE DIVERGENCES, RECORDED RATHER THAN ABSORBED
 *
 *  - MIGRATION: SIX PLACEMENT CONTROLS ARE NOT RENDERED, ON THE CONTRACT'S OWN INSTRUCTION. The legacy
 *    screen edited `paneName`, `alignment` (cboAlign), `color` (txtColor), `border` (txtBorder),
 *    `displayPrint` (chkDisplayPrint) and `displaySyndicate` (chkDisplaySyndicate). None is projected onto
 *    `Dtos/Module/UpdateModuleRequest.cs`, and `core/models/module.model.ts` states the consequence
 *    directly: the six "are neither rendered nor transported - the module-settings screen omits the controls
 *    and the stored columns are preserved by not projecting them through the update at all". The API sets
 *    `JsonUnmappedMemberHandling.Disallow`, so sending one is an HTTP 400 rather than a silent no-op, and
 *    rendering a control whose value can never persist would tell the operator their edit was saved when it
 *    was not. The alignment control is the sharpest loss: its fourth item carried `value=""`
 *    (modulesettings.ascx L126), which is `Null.NullString` and therefore LEGITIMATE TRANSMITTED DATA rather
 *    than "unset" - so were the column ever projected, the empty string would have to travel un-elided and
 *    could not be collapsed to `null` or detected by truthiness.
 *  - MIGRATION: THE PERMISSION GRID AND ITS INHERIT SWITCH ARE NOT EDITABLE HERE. `dgPermissions` (the real
 *    control id; the sibling label's `controlname="ctlPermissions"` is a legacy mismatch) and
 *    `chkInheritPermissions` posted grants back through a Web Forms control. Grant mutation has no endpoint
 *    - the permission resource is read-only - so the grid is not rendered. `inheritViewPermissions` IS a
 *    column on the update contract, so its stored value is round-tripped and never cleared by omission.
 *  - MIGRATION: `ctlModuleContainer` IS DROPPED because a container is a skin object and the skinning
 *    surface is excluded; `ctlIcon` is dropped because the legacy URL control is excluded and no
 *    file-selection endpoint exists. `iconFile` is a column on the contract, so it too is round-tripped.
 *  - MIGRATION: `chkAllTabs`, `chkDefault` AND `chkAllModules` ARE NOT OFFERED AS BULK AFFORDANCES.
 *    `chkAllTabs` lived in tblSecurity rather than tblOther and its `AllTabs` column (`ModuleInfo.vb:L248`)
 *    IS real persisted data, so the value is round-tripped even though the toggle is gone. `chkDefault` and
 *    `chkAllModules` were backed by `IsDefaultModule` (L590) and `AllModules` (L599), both carrying
 *    `<XmlIgnore()>`, which proves they were transient UI intents rather than stored facts; their contract
 *    counterparts are instructions the server acts on after the update, so both are seeded unset on every
 *    load and sent explicitly false. Echoing a previous instruction back onto the form would reapply it.
 *  - MIGRATION: `pnlSpecific` AND ITS HELP AFFORDANCES RENDER NOTHING. The legacy body was loaded at run
 *    time at L458-L476 by the excluded Web Forms module loader. The region is kept as a declared home; no
 *    control is loaded into it and no endpoint is invented to supply one.
 *  - MIGRATION: `cmdStartCalendar` AND `cmdEndCalendar` ARE DROPPED. Both were `asp:hyperlink` launchers
 *    wired at L196-L197 to `Common.Utilities.Calendar.InvokePopupCal`. The shared component library is
 *    closed at ten members and contains no date picker, so a date-capable native control carries the
 *    affordance and no eleventh shared member is introduced.
 *  - MIGRATION: RICH TEXT IS REDUCED TO PLAIN MULTI-LINE TEXT. The legacy editor provider is out of scope,
 *    so the header and footer are plain text areas and their values are bound as text, never as markup.
 *  - MIGRATION: THE REMOVAL IS SOFT AND THERE IS NO WAY BACK. `cmdDelete_Click` (L300-L312) called
 *    `DeleteTabModule(TabId, ModuleId)` (`ModuleController.vb:L837`) and NOT `DeleteModule` (L819): the
 *    placement row goes, the remaining placements are reordered, and only when the module is left on no page
 *    at all is it marked deleted. No recycle-bin restore or purge endpoint exists, so this screen offers no
 *    undo. Defect D-M4 is annotated here rather than reproduced: the legacy comment at L290-L292 claims the
 *    handler deletes a PORTAL when the caller is a super user, which is wrong on both counts - L305 calls
 *    `DeleteTabModule` and performs no super-user test whatsoever.
 *  - MIGRATION: THE COPY AND DELETE-ALL BRANCH IS DROPPED. L411-L418 called `CopyModule` and
 *    `DeleteAllModules`; neither has an endpoint, and neither is invented.
 *  - MIGRATION: `objModule.IsDeleted = False` AT L364 IS NOT REPRODUCED. Every legacy save silently
 *    UN-DELETED the module it was editing. The stored value is round-tripped instead, which is what the
 *    update contract asks for in terms - the member stays required "so a caller must preserve the loaded
 *    state deliberately rather than clearing it by omission". Restoring a module is a recycle-bin operation
 *    and this screen is not one; the legacy behaviour is recorded here as a defect rather than carried over.
 *  - MIGRATION: DEFECT D-M3 IS ANNOTATED AND NEITHER HALF IS REPRODUCED. `cboTab` was selected TWICE with
 *    contradictory values - a guarded set at L129-L131 and then an unguarded
 *    `cboTab.Items.FindByValue(CType(TabId, String)).Selected = True` at L145. The form is seeded once, from
 *    the loaded placement, and a page absent from the list cannot throw.
 *  - MIGRATION: THE TAB-ADMINISTRATOR DISABLING IS RETAINED BUT CANNOT BE DECIDED CLIENT-SIDE. L215-L220
 *    and L333-L338 disabled `chkAllTabs`, `chkDefault`, `chkAllModules` and `cboTab` for a caller who
 *    administered the page but not the portal. Three of those four controls are gone; the fourth survives,
 *    so {@link canManageAllPages} still locks the far-reaching controls when a caller supplies it. No role
 *    is inferred here - there is no endpoint that would answer the question - and the server remains
 *    authoritative, answering 403.
 *  - MIGRATION: TWO LEGACY SETTINGS SCOPES COLLAPSE INTO ONE WHOLE-OBJECT PUT. `GetModuleSettings`
 *    (`ModuleController.vb:L1237`) and `GetTabModuleSettings` (L1336) were distinct reads, and per-key
 *    mutation went through six separate members (L1283, L1306, L1318, L1373, L1395 and L1407). The target
 *    reads and replaces both maps in one document, `ModuleSettingsBag`, whose two members keep the module
 *    scope and the placement scope apart exactly as `dbo.ModuleSettings` and `dbo.TabModuleSettings` do. No
 *    per-key mutation is issued and no settings key is invented: this screen reads the bag so an operator's
 *    stored settings survive a save, and writes back only what it read.
 */
@Component({
  selector: 'app-module-settings',
  standalone: true,
  // ReactiveFormsModule for the typed form; the five shared components are the only presentational
  // primitives this screen needs. The shared library is closed at ten members, so the collapsible regions
  // are built from semantic markup in the template rather than from an eleventh shared component.
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ErrorBannerComponent,
    ConfirmDialogComponent,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './module-settings.component.html',
  styleUrl: './module-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleSettingsComponent {

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
    () => this.form.dirty && this.saving === false,
  );
  // -----------------------------------------------------------------------------------------------------
  // COLLABORATORS
  // -----------------------------------------------------------------------------------------------------
  // Resolved with `inject()` into private readonly fields rather than through constructor parameters, and
  // no `providers` array is declared: every provider this screen relies on is registered once in
  // `app.config.ts`, and a component-level provider would give this screen a private copy of shared state.

  /** The single place `ModuleService` and `TabService` are reached from. */
  private readonly store = inject(ModuleStore);

  /** The advisory surface. A refusal is surfaced here at warning severity, not as a danger banner. */
  private readonly notifications = inject(NotificationService);

  /** Used only to return to the listing, which is what the legacy redirect at L421 did. */
  private readonly router = inject(Router);

  /**
   * The catalogue transport, read for the advisory key list beside the inherit switch.
   *
   * Reached DIRECTLY rather than through a store, and deliberately so: the permission catalogue is a
   * read-only lookup with no client-side state to own, and `core/state/role.store.ts` records the same
   * decision in as many words — "permissions are a read-only catalogue reached through
   * `core/services/permission.service.ts`; there is deliberately no permission store".
   */
  private readonly permissions = inject(PermissionService);

  /**
   * The session, read only to decide whether the catalogue may be asked for at all.
   *
   * The `/permissions` endpoint is declared under the administrator policy, so a caller without tenant
   * administration receives a 403. Asking anyway would spend a request to be refused and would put a
   * refusal in the network log of an ordinary editor who has done nothing wrong, so the request is
   * withheld instead of recovered from.
   */
  private readonly authStore = inject(AuthStore);

  /** Bounds the catalogue subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  // -----------------------------------------------------------------------------------------------------
  // THE FORM
  // -----------------------------------------------------------------------------------------------------

  /**
   * The typed form backing every editable field on the screen.
   *
   * The three coerced fields are text controls; see {@link ModuleSettingsFormModel} for why. Only the four
   * measured data-type checks are attached, and nothing else: no presence rule, no length rule and no bound
   * appears on any control the legacy screen left unvalidated.
   */
  protected readonly form = new FormGroup<ModuleSettingsFormModel>({
    tabId: new FormControl(0, { nonNullable: true }),
    moduleTitle: new FormControl('', { nonNullable: true }),
    moduleOrder: new FormControl(0, { nonNullable: true }),
    allTabs: new FormControl(false, { nonNullable: true }),
    inheritViewPermissions: new FormControl(false, { nonNullable: true }),
    // MIGRATION: RICH TEXT IS REDUCED TO PLAIN TEXT, AND THE WORDING IS AUTHORED RATHER THAN LOCALISED. The
    // legacy `txtHeader` and `txtFooter` were `TextMode="MultiLine"` rows=6 entries whose content the excluded
    // FCK editor provider could dress up; the target renders a plain textarea, so markup typed here is stored
    // and returned as text and is never bound as HTML. Their labels and hints, like every string on this
    // screen, are taken verbatim from `ModuleSettings.ascx.resx` and written into the source: `@angular/
    // localize` is outside the pinned dependency surface, so none of the 48 in-scope legacy localisation calls
    // is reproduced and the resource files are read for WORDING ONLY.
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
    // SEC: the SHARED containment rule, and the only validator this control carries. `ctlIcon` on the
    // legacy screen was a `<portal:url>` PICKER over the portal's own files (`modulesettings.ascx:L116`),
    // so an arbitrary path could not be entered and there was nothing to validate; replacing the picker
    // with a text box is what makes the rule necessary. It therefore refuses only references the legacy
    // screen could never have produced. Measured before it existed: a traversal path submitted from this
    // screen reached the API and was stored verbatim.
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
   * The picker's options, derived from the tenant's page list and the occupied page.
   *
   * A `computed()` rather than a field an effect recomputes: the options are a pure function of the two
   * signals they read, so deriving them reactively means they can never fall out of step, and no effect can
   * overwrite a list a caller supplied.
   */
  private readonly derivedPages = computed<readonly SelectOption<number>[]>(() => {
    const detail = this.store.module();

    // MIGRATION: THE SELECTION IS MADE ONCE, FROM ONE SOURCE. `ModuleSettings.ascx.vb` selected `cboTab`
    // TWICE with contradictory values - a guarded set at L129-L131 and then an UNGUARDED
    // `cboTab.Items.FindByValue(CType(TabId, String)).Selected = True` at L145 that threw a null reference
    // whenever the occupied page was absent from the bound list. Neither the double set nor the unguarded
    // lookup is reproduced: the option list is derived here and the occupied page is guaranteed to be in it,
    // and the selection itself is the form control's value. The defect is annotated, not fixed in place.
    return buildPageOptions(this.store.tabs(), detail === null ? undefined : detail.tabId);
  });

  /** The resolved heading. Never blank, because the shared page header refuses a blank title. */
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
   *
   * `undefined` means "nothing seeded yet". It is NEVER compared against 0 or -1, because a placement
   * identity of 0 would be a legitimate row.
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
   * The permission keys this module's DEFINITION declares, or `null` when the list is not available.
   *
   * ⚠ `null` AND THE EMPTY ARRAY MEAN DIFFERENT THINGS, and the template distinguishes them. `null` is
   * "not read" — the caller does not administer the tenant, the read has not returned yet, or it failed —
   * and renders nothing at all. An empty array is an ANSWER: this definition declares no keys, which is
   * ordinary for a definition whose access is governed entirely by its page. Collapsing the two would
   * report "no permissions are declared" on the strength of a request that never happened.
   */
  private readonly declaredPermissionKeys = signal<readonly string[] | null>(null);

  /** The definition the catalogue read was last issued for, so it is issued once per definition. */
  private permissionsRequested: number | undefined = undefined;

  /** The failure already surfaced, so one refusal produces one advisory. */
  private failureSurfaced: ProblemDetails | null = null;

  /**
   * The settings bag exactly as the server last reported it, or `null` before one has been read.
   *
   * The reference {@link settingsBagDiffersFromRead} compares against, which is what lets a save send the
   * property maps only when this screen has actually altered them. Held as a plain field rather than a signal
   * because nothing renders it and no derivation depends on it; it is written from the read observer and read
   * once per submission.
   */
  private settingsAsRead: ModuleSettingsBag | null = null;

  /**
   * Whether a submission raised FROM THIS SCREEN is still outstanding.
   *
   * The store is provided at the root, so its write flags settle for reasons this screen did not cause. This
   * latch is what makes {@link concludeSubmission} act on THIS screen's own save and on nothing else. It is a
   * signal rather than a plain field so that the observer's dependency on it is explicit and tracked.
   */
  private readonly submissionPending = signal(false);

  /**
   * Whether a removal raised FROM THIS SCREEN is still awaiting its answer.
   *
   * The removal counterpart of {@link submissionPending}, and separate from it because the two settle on
   * different flags and conclude differently: a save waits on both write flags and reports a save, a removal
   * waits on the module write flag alone and reports a removal.
   *
   * Not to be confused with `removalPending`, which reports whether the CONFIRMATION is on screen. This one
   * reports whether the confirmed command is on the wire.
   */
  private readonly removalOutstanding = signal(false);

  // -----------------------------------------------------------------------------------------------------
  // ROUTE INPUTS
  // -----------------------------------------------------------------------------------------------------

  /**
   * The module to edit, supplied by the `:moduleId` route parameter.
   *
   * THE NAME IS A CONTRACT. `app.config.ts` enables `withComponentInputBinding()`, which binds a route
   * parameter onto an input of the same name; renaming this input would break the binding SILENTLY, with no
   * compile error and no runtime message - the screen would simply never resolve a module. The server side
   * agrees on the spelling too: the authorisation handler resolves module scope by looking for `moduleId`
   * and then `id`.
   *
   * Router inputs arrive as STRINGS, so the value is coerced explicitly and a value that is not an integer
   * resolves to `undefined` rather than to a number that happens to parse.
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
   * Omitting it addresses the module itself, which is a materially different request from naming one of its
   * placements, so no default is supplied.
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

  // -----------------------------------------------------------------------------------------------------
  // PRESENTATION INPUTS
  // -----------------------------------------------------------------------------------------------------
  //
  // ⚠ EVERY SETTER BELOW ACCEPTS `undefined`, AND THAT IS NOT DEFENSIVE PADDING - IT IS REQUIRED FOR THE
  //   SCREEN TO RENDER AT ALL. `withComponentInputBinding()` does not bind only the inputs a route happens
  //   to name. On every activation it reflects the component's inputs and calls
  //   `setInput(templateName, data[templateName])` for EVERY ONE of them, where `data` is the merged query
  //   parameters, path parameters and route data. An input whose name is not a key of that object is
  //   therefore assigned `undefined` - not left alone, not left at its field initialiser, and not skipped.
  //
  //   This route is `modules/:moduleId/settings`, so `moduleId` is the ONLY name the router can supply.
  //   `heading`, `settings`, `loading`, `saving`, `canDelete`, `pages` and `canManageAllPages` are all
  //   overwritten with `undefined` the moment the route activates. TypeScript cannot see it: the assignment
  //   happens through the framework's reflection, so a setter declared to take `ModuleSettingsSeed | null`
  //   compiles perfectly and still receives `undefined` at run time.
  //
  //   Measured consequence when the setters did NOT accept it: `set settings(undefined)` passed its
  //   `!== null` guard, dereferenced `undefined.tabModuleId` and threw out of `activateRoutes`; the template
  //   then dereferenced `undefined.friendlyName` and threw on EVERY change-detection pass, which aborts the
  //   update function part-way and leaves the DOM half-built - no control ids, no labels, no options in the
  //   page picker, no visibility radios; and the blank `heading` tripped the shared page header's own
  //   non-blank-title guard. The screen rendered an empty form skeleton with nine uncaught errors and no
  //   visible failure of any kind. Unit tests cannot catch this, because `TestBed` never runs the router's
  //   input binder. Hence the explicit `undefined` handling and the regression guard in the spec.
  //
  // EACH OF THESE READS THE STORE DIRECTLY UNLESS A CALLER HAS ASSIGNED IT, AND THAT SHAPE IS DELIBERATE.
  // The obvious alternative - an `effect()` that copies the store's signals onto plain fields - renders a
  // frame behind: a component effect runs as part of the component's own refresh, so the template can be
  // evaluated against the PREVIOUS value and needs a second change-detection pass to catch up. In a browser
  // the extra pass always arrives, which is exactly what makes the fault so unpleasant - it is invisible
  // until something renders once and never again, and a screen stuck behind its own loading indicator is
  // indistinguishable from a screen whose data never arrived. Reading the signal inside the getter removes
  // the ordering question altogether: the template sees the current value in the pass that asked for it.
  //
  // Assigning any of them pins it, so a host or a test still drives the screen exactly as before.

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

  /** The screen's heading. Never blank. */
  public get heading(): string {
    return this.headingText;
  }

  /**
   * The module and placement being edited, or `null` when none has resolved.
   *
   * Assigning re-seeds the form, so the screen never shows a value the loaded state does not hold.
   *
   * @param value The state to show.
   */
  @Input()
  public set settings(value: ModuleSettingsSeed | null | undefined) {
    // `undefined` means "the router had nothing of this name", which is NOT the same as a caller clearing
    // the screen with an explicit `null`. It must leave the screen following the store, so it is normalised
    // to `null` and no seeding is attempted.
    const supplied = value ?? null;

    this.seeded.set(supplied);

    if (supplied !== null) {
      this.seededPlacement = supplied.tabModuleId;
      this.seed(supplied);
    }
  }

  /**
   * The module and placement being edited, or the loaded module, or `null`.
   *
   * NEVER `undefined`: the template dereferences this value, so the absent case must be exactly one thing.
   */
  public get settings(): ModuleSettingsSeed | null {
    const explicit = this.seeded();

    return explicit !== null ? explicit : this.store.module();
  }

  /**
   * Whether the module is still being fetched.
   *
   * Both reads matter: the settings bag is fetched alongside the module and a save replaces it, so showing
   * the form before it has arrived would let a save empty settings the screen never displayed.
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
   * Whether a removal affordance should be offered.
   *
   * A module that has resolved can be removed - the removal endpoint exists and the whole route is already
   * gated on the module-edit policy - so the affordance follows the module rather than being defaulted off.
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
   * Locking is applied through the reactive forms API rather than through a `disabled` attribute binding:
   * binding the attribute on a control a `formControlName` owns contests the directive for the property and
   * raises Angular's reactive-forms warning. `getRawValue()` still carries a locked control's value, so a
   * caller without the privilege submits the stored value unchanged instead of clearing it.
   *
   * @param value Whether the far-reaching controls are editable.
   */
  @Input()
  public set canManageAllPages(value: boolean | null | undefined) {
    // Only a REAL boolean counts as an assignment. Treating the router's `undefined` as one would both pin
    // the value to a falsy default and suppress the store-driven default, locking the page picker shut on
    // every routed visit - the exact opposite of the legacy behaviour, which locked it only for a caller who
    // administered the page but not the portal.
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

  // -----------------------------------------------------------------------------------------------------
  // OUTPUTS
  // -----------------------------------------------------------------------------------------------------
  // The screen completes each intent itself. These report what it did, so a host can react without having
  // to observe the store, and so the submitted document can be asserted directly.

  /** Emits the submitted document when the operator accepts the form. */
  @Output() public readonly save = new EventEmitter<UpdateModuleRequest>();

  /** Emits when the operator abandons the form. */
  @Output() public readonly cancel = new EventEmitter<void>();

  /** Emits when the operator confirms a removal. */
  @Output() public readonly remove = new EventEmitter<void>();

  // -----------------------------------------------------------------------------------------------------
  // ORCHESTRATION
  // -----------------------------------------------------------------------------------------------------
  // `effect()` is used ONLY for genuine side effects - issuing a fetch, seeding a form, raising an advisory
  // - and never to derive a value that a `computed()` could express.

  /**
   * Issues the reads for the addressed module.
   *
   * Both the module and its settings are fetched. The settings bag is read so that this screen can tell
   * whether it has anything to save at all — see {@link persistSettings} — and, were an editor ever added
   * here, so that a whole-object replacement would carry the keys the editor did not touch rather than
   * emptying them.
   */
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

  /**
   * Records the settings bag as read, which is the reference a save compares against.
   *
   * A genuine side effect — it writes a field outside the reactive graph — and the only place that field is
   * assigned, so the reference can never drift from the response it came from.
   */
  private readonly recordSettingsAsRead = effect(() => {
    const bag = this.store.settings();

    this.settingsAsRead = bag;
  });

  /**
   * Seeds the form from the loaded module.
   *
   * This one genuinely IS a side effect - a form group is not reactive state and has to be written to - which
   * is why it is an effect while the read-only presentation values are getters. The seeding is guarded on the
   * PLACEMENT IDENTITY rather than on object identity, so a store refresh that returns an equal-but-distinct
   * object does not discard an edit in progress.
   */
  private readonly adoptLoadedModule = effect(() => {
    const detail = this.store.module();

    if (detail === null) {
      return;
    }

    // The legacy screen locked the far-reaching controls for a page-scoped administrator. No endpoint answers
    // that question, so the controls stay editable unless a caller says otherwise and the server adjudicates.
    // Assigning the input suppresses this default.
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
   * The loaded module, but ONLY once it is the module this screen's address names.
   *
   * ⚠ WITHOUT THIS GUARD, EVERY MOVE BETWEEN TWO MODULES READ THE PREVIOUS ONE'S DEFINITION, AND RUNTIME
   *   TESTING MEASURED IT. The store's module slot still holds the module left behind until the new
   *   address's detail read answers, so an effect keyed on that slot fires once with the OLD
   *   `moduleDefId`. Opening module 7's settings straight after module 2's issued
   *   `GET /module-definitions/4` - module 2's definition - which answers `404` because the tenant
   *   catalogue publishes no administrative definition, and the screen then raised "The requested item
   *   could not be found." over a module whose own three reads had all succeeded. The reverse direction
   *   issued module 7's definition and cancelled it a moment later. The per-identifier memo those effects
   *   already keep could not prevent it: it suppresses a REPEAT of the same identifier, and a stale
   *   identifier is a different one.
   *
   *   The guard is applied to the definition and declared-permission reads, which is where the defect was
   *   measured, and deliberately NOT to the seeding effect: seeding is idempotent and re-runs when the
   *   correct detail lands, and it is reached by callers that supply state directly with no address at all.
   *
   * `undefined` for the address is not a failure - it is how a caller-seeded screen presents itself, and in
   * that case the loaded module IS the one this screen means, so it passes through.
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

  /**
   * Fetches the definition that owns the loaded module, which is the only source of the cache default.
   *
   * MIGRATION: THE DEFINITION IS FETCHED BECAUSE THE CACHE FIELD'S VISIBILITY DEPENDS ON IT AND ON NOTHING
   * ELSE. `ModuleSettings.ascx.vb:L136-L142` read the definition and hid `rowCache` outright when
   * `DefaultCacheTime` equalled `Null.NullInteger`. That fact lives on the definition, not on the module, so
   * skipping this read would make the three-state rule unimplementable.
   */
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
   * Fetches the tenant's pages for the "Move To Page" picker.
   *
   * The tenant is taken from the loaded module rather than from the address, because the route does not carry
   * it. A host-level module reports `portalId` as `null`, in which case there is no portal page list to read
   * and the picker offers only the page the module already sits on.
   *
   * The portal-scoped page list is UNPAGED, so no page coordinate, ordering or filter is sent.
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
   * beside the inherit switch.
   *
   * ⚠ THIS IS AN ADVISORY READ AND ITS FAILURE IS NOT THIS SCREEN'S FAILURE. The keys explain what the
   * inherit switch is choosing between — the legacy screen sat directly above a permission grid, and
   * without any indication of the vocabulary in play the switch reads as a bare boolean with no subject.
   * Nothing on the form depends on the answer, no control is enabled or disabled by it and no submission
   * consults it, so a refusal or an outage leaves the region simply absent rather than raising a banner
   * over a screen that is otherwise working. `PermissionService.list` already marks its request as
   * presented by its caller, so the error interceptor stays silent and the `error` arm below has only to
   * leave the signal at `null`.
   *
   * ⚠ THE READ IS WITHHELD, NOT RECOVERED FROM, when the caller does not administer the tenant. The
   * endpoint is declared under the administrator policy and would answer 403; issuing it anyway would
   * write a refusal into the network log of an editor who is entitled to be on this screen and has done
   * nothing wrong.
   *
   * ⚠ THE FILTER IS THE DEFINITION, NOT THE MODULE. `moduleDefinitionId` selects the keys declared for
   * the definition this placement instantiates, which is what governs the grants a module of this kind
   * can carry. The unfiltered listing answers from the API's closed key enumeration and touches no store
   * at all, so it would report the same four keys for every module ever loaded and would say nothing
   * about this one.
   *
   * Issued once per definition, guarded exactly as the definition and page lookups above are, because an
   * effect re-runs on every dependency change and an unguarded fetch here would re-issue the request on
   * each keystroke-driven form update.
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

  /**
   * Surfaces a store failure once, at the severity the legacy screen used.
   *
   * MIGRATION: A REFUSAL IS AN ADVISORY, NOT AN ERROR. The legacy access-denied screen raised
   * `YellowWarning` on both of its branches, so a 403 is announced through the advisory service at warning
   * severity and is not dressed as a danger banner. Field-level rejections are handed to the banner, which
   * renders the problem document the utility already resolved; the utility owns both the break-tag stripping
   * and the severity mapping, and neither is re-implemented here.
   */
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
   * Concludes a submission once BOTH writes have settled, then reports and leaves.
   *
   * MIGRATION: the legacy redirect at `ModuleSettings.ascx.vb:L421` -
   * `Response.Redirect(NavigateURL(), True)`, commented "Navigate back to admin page" - sat INSIDE the
   * `If Page.IsValid Then` / `Try` block after `UpdateModule` had returned, so a postback that threw fell
   * through to `Catch` and never redirected. The faithful port therefore leaves ONLY on success, and the
   * announcement is raised here rather than at the point of submission for the same reason: the legacy
   * postback was synchronous, so "saved" was never claimed before the write had actually happened. Up to two
   * independent writes may be in flight - the module replacement always, and the settings bag only when this
   * screen has actually changed it (see {@link persistSettings}) - so both write flags must be clear before
   * either outcome is known. Watching both remains correct when only one was issued: the unused flag is
   * already clear.
   *
   * A rejected write keeps the operator on the screen, because {@link surfaceFailure} has put the per-field
   * messages on the fields and navigating away would discard them.
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
    // `returnToListing()` on the last line is a navigation it can refuse. `window.confirm` blocks the
    // JavaScript thread, so the auto-dismiss timer on the notification below becomes due while the dialog
    // stands and fires the instant it is accepted: the confirmation is queued, exempted, and then never
    // seen. Marking the form settled is the honest statement of what happened - every control's value is
    // now what the server holds.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // MIGRATION: the announcement and the departure are raised HERE, not at the point of submission, because
    // `Response.Redirect(NavigateURL(), True)` at L421 ran after `UpdateModule` had returned and a throwing
    // postback never reached it. A synchronous postback could not claim "saved" before saving; nor may this.
    this.notifications.notify('success', SAVED_MESSAGE, null, true);

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
    // retires notifications on a completed navigation, and this one is raised in the same task as the
    // departure below - so it was swept before it could be painted. The legacy announced and then
    // redirected, so the listing is where this message belongs.
    this.returnToListing(true);
  });

  /**
   * Concludes a removal once the write has settled, then reports, notifies the host and leaves.
   *
   * The same reasoning as {@link concludeSubmission}, applied to the destructive command: the legacy
   * postback at `ModuleSettings.ascx.vb:L300-L312` removed the placement and only then redirected, so a
   * throwing call left the operator on the screen with the explanation in front of them. One write flag is
   * watched rather than two, because a removal issues one command.
   *
   * A refusal keeps the operator here and announces nothing extra: {@link surfaceFailure} has already
   * described it, and the departure is what is withheld.
   */
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

    // ⚠ SETTLED BEFORE LEAVING FOR THE REASON RECORDED ON THE SAVE PATH ABOVE, and it applies to a
    // removal too: an operator who typed into the form and then removed the placement would be asked to
    // confirm discarding edits to a placement that no longer exists. There is nothing left to save, so
    // pristine is the honest state, and the prompt would swallow the confirmation below exactly as it
    // does on the save path.
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

  /**
   * @see ICON_NOT_CONTAINED_MESSAGE — the API's own sentence, shared rather than restated, so one rule
   * reads the same way whether it is caught before the request or reported by the response.
   */
  protected readonly iconNotContainedMessage = ICON_NOT_CONTAINED_MESSAGE;

  /** @see NO_SPECIFIC_SETTINGS_MESSAGE */
  protected readonly noSpecificSettingsMessage = NO_SPECIFIC_SETTINGS_MESSAGE;

  /**
   * What the module will be listed as while its heading is blank, or `null` when the heading is set.
   *
   * THE HEADING IS OPTIONAL AND STAYS OPTIONAL. Three tiers agree: `modulesettings.ascx` declares no
   * presence validator on `txtTitle`, both `CreateModuleRequestValidator` and `UpdateModuleRequestValidator`
   * gate their ONLY title rule on the value being non-empty, and `dbo.Modules.ModuleTitle` is nullable. A
   * module in the measured data stores the empty string, so adding a required rule would not merely refuse
   * new input — it would make an existing record unsavable, blocking an operator who opened it to change
   * something else entirely. The minimal-change discipline requires validation rules to MATCH, and this one
   * matches by staying absent.
   *
   * What was genuinely missing is the CONSEQUENCE. A blank heading is not nothing: the module is listed
   * under its definition's name instead, which is exactly what `ControlPanelBase.vb:192-196` did on finding
   * `title = ""`. That was invisible here, so an operator clearing the heading could not tell whether the
   * module would appear nameless or under some other name. Stating it is the same treatment the reversed
   * schedule gets on the sibling screen: describe the outcome, accept the value.
   *
   * Returns `null` — and so renders nothing — when the heading carries anything at all, including
   * whitespace, because whitespace is a value the legacy screen would have stored and the fallback would
   * not have applied to. The definition name is likewise only offered when there is one to offer; with
   * neither a heading nor a definition name there is nothing truthful to say.
   */
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
   * The stored settings this module carries, as rows for the module-specific section to display.
   *
   * ⚠ WHY THIS EXISTS: THE SECTION WAS DISCARDING REAL DATA IN SILENCE. The screen already reads the
   * settings bag — `loadSettings` runs on arrival and {@link persistSettings} writes both maps back
   * whole — but the module-specific region rendered nothing except a projection slot no routed use ever
   * fills. Measured on a module carrying two module-scoped settings, one with a 750-character value: the
   * request was made, the response was held, and the section still rendered zero controls and zero text.
   * The operator was shown an empty panel over stored configuration that the application had in hand.
   *
   * READ-ONLY, AND DELIBERATELY SO. The legacy `pnlSpecific` placeholder hosted the MODULE'S OWN
   * settings control, loaded dynamically by the Web Forms control loader; that loader is out of scope, so
   * there is no bespoke editor to present and no way to know what a given key means, what its permitted
   * values are, or how it should be rendered. Inventing a generic editor over keys a module defines would
   * let an operator write values no module ever validates. Disclosing what is stored is the honest
   * position: it converts a silent discard into something a reader can see, and both maps continue to
   * round-trip untouched through the save, exactly as before.
   *
   * BOTH SCOPES ARE SHOWN, each labelled with its own, because the contract documents them as genuinely
   * different things — `moduleSettings` is identical on every page the module appears on while a value in
   * `tabModuleSettings` belongs to one occurrence on one page — and collapsing them would misreport which
   * is which. Both were being discarded, so both are disclosed.
   *
   * The bag is guarded against the addressed module for the same reason {@link persistSettings} guards
   * it: the store is provided at the root and may still hold the bag read for a neighbouring module, and
   * presenting one module's settings under another's address would be worse than presenting none. The
   * comparison is exact and never a truth test — module 0 is a real module.
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
   * Whether this module carries any stored settings to disclose.
   *
   * Read by the template to choose between the disclosure and the explicit "none recorded" statement. The
   * section itself is still rendered either way: an operator being able to see that a module has no
   * settings of its own is information the legacy screen could not convey, and that was a deliberate
   * choice here before this data was surfaced.
   */
  protected get hasSpecificSettings(): boolean {
    return this.specificSettingRows.length > 0;
  }

  /** `valStartDate`'s wording, for the template's message slot. */
  protected readonly startDateInvalidMessage = START_DATE_INVALID_MESSAGE;

  /** `valEndDate`'s wording, for the template's message slot. */
  protected readonly endDateInvalidMessage = END_DATE_INVALID_MESSAGE;

  /**
   * `valBorder`'s wording.
   *
   * Retained with its rule even though the border column is not transported, so the pair survives together;
   * see {@link BORDER_INVALID_MESSAGE}.
   */
  protected readonly borderInvalidMessage = BORDER_INVALID_MESSAGE;

  /**
   * The wording the previous revision of this screen used for an over-long heading.
   *
   * MIGRATION: NO LENGTH RULE IS ENFORCED, because `txtTitle` carried no validator. The column bound is
   * advertised through {@link limits} as a native `maxlength` attribute, which truncates rather than
   * invalidating, and the server's own rule adjudicates anything that gets past it. The wording is kept so a
   * template slot that still references it renders text rather than nothing.
   */
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

  /** The regions currently closed. Seeded from the legacy `isexpanded` attributes. */
  protected readonly collapsed = new Set<ModuleSettingsSection>(INITIALLY_COLLAPSED);

  /** Whether the destructive confirmation is showing. */
  protected get removalPending(): boolean {
    return this.removalOpen();
  }

  /**
   * The problem document the banner renders, or `null` when there is nothing to report.
   *
   * An outstanding SETTINGS refusal takes precedence, because it is the refusal that decides whether this
   * screen has anything to offer: while one is outstanding the form is withheld, so no write can be in
   * flight and there is no rejected-write document competing for the banner. Without the precedence the
   * banner showed whichever read failed LAST — measurably the definition `404`, whose own branch in
   * {@link announce} clears the banner outright, leaving the operator with no statement of the refusal at
   * all.
   */
  protected readonly problem = computed<ProblemDetails | null>(
    () => this.settingsRefusal() ?? this.currentProblem(),
  );

  /**
   * The refusal this screen's own SETTINGS read carried, or `null` when that read did not fail.
   *
   * Read from the store's dedicated settings slot rather than from its shared one, and DERIVED rather than
   * captured. Both choices are load-bearing, and each answers a defect the other does not:
   *
   *   - The shared slot is owned by whichever of this screen's three reads finished LAST, measurably the
   *     definition `404` on an administrative module, so it cannot answer "was I allowed to read the
   *     settings". The dedicated slot is written by the settings handler itself and answers exactly that.
   *   - Deriving means there is no moment at which this value has to be observed. An earlier revision
   *     captured the shared value inside {@link surfaceFailure}, and whether the capture saw the refusal at
   *     all depended on whether the definition read was issued in the same reactive flush — the same code
   *     therefore held against a slow network and lost against a fast one, and a specification driving the
   *     reads synchronously proved it lost.
   *
   * A NOT-FOUND is deliberately excluded and left to {@link notFound}: `404` answers the question of
   * EXISTENCE rather than of permission, and the shared not-found affordance is the surface that describes
   * it. Folding it in here would replace that affordance with a refusal banner for a module that simply is
   * not there.
   */
  private readonly settingsRefusal = computed<ProblemDetails | null>(() => {
    const failure = this.store.settingsFailure();

    if (failure === null || failure.summary.status === NOT_FOUND_STATUS) {
      return null;
    }

    return failure.problem;
  });

  /**
   * Whether this screen's own settings read was refused and has not been superseded.
   *
   * A separate predicate from {@link readRefused} because the two answer different questions from different
   * evidence: that one reads the store's current failure, this one reads how the read this screen DEPENDS on
   * ended. Either is sufficient to withhold the form.
   *
   * Note that it is not derived from {@link settingsRefusal} being non-null: a refusal carrying no document
   * at all is still a refusal, and treating it as none would reopen the form on the one response shape least
   * likely to be exercised — a bodiless `403`.
   */
  protected readonly settingsRefused = computed<boolean>(() => {
    const failure = this.store.settingsFailure();

    return failure !== null && failure.summary.status !== NOT_FOUND_STATUS;
  });

  /**
   * Whether the cache period field renders at all.
   *
   * MIGRATION: THE THREE-STATE CACHE RULE, PRESERVED WITHOUT COALESCING. `ModuleSettings.ascx.vb:L136-L142`
   * reads verbatim `If objModuleDef.DefaultCacheTime = Null.NullInteger Then rowCache.Visible = False Else
   * txtCacheTime.Text = objModule.CacheTime.ToString`, and the three states it distinguishes are all
   * preserved:
   *
   *   1. the definition's `defaultCacheTime` is -1 - the definition records no default, so the field is
   *      HIDDEN outright and the save writes 0, exactly as L349-L353 did for an empty control;
   *   2. the definition's `defaultCacheTime` is 0 - caching IS supported with a zero-second default, so the
   *      field is SHOWN;
   *   3. this placement's `cacheTime` is 0 - a legitimate stored value meaning "not cached".
   *
   * `defaultCacheTime` of -1 and `cacheTime` of 0 are DIFFERENT FACTS on DIFFERENT contracts. They are never
   * coalesced, no `cacheTime ?? defaultCacheTime` is written, no single "effective" period is derived, and
   * falsiness is never used to tell them apart - `0` is falsy and is one of the two values that must survive.
   *
   * While the definition has not arrived the field is shown, because hiding a field that is about to be
   * needed loses an edit, whereas showing one that turns out to be unsupported costs nothing: the save
   * writes 0 for a hidden field either way.
   */
  protected readonly showCacheField = computed<boolean>(() => {
    const definition = this.store.definition();

    if (definition === null) {
      return true;
    }

    // MIGRATION: `!== NO_CACHE_DEFAULT` and NEVER a truth test. `ModuleSettings.ascx.vb:L136-L142` hid
    // `rowCache` when and only when `objModuleDef.DefaultCacheTime = Null.NullInteger`, that is -1. A default
    // of 0 means caching IS supported with a zero-second default and the field must SHOW, so `!definition
    // .defaultCacheTime` would collapse the two distinct states the legacy screen kept apart.
    return definition.defaultCacheTime !== NO_CACHE_DEFAULT;
  });

  /**
   * Whether the addressed module resolved to nothing.
   *
   * Distinguished from "still loading" so the template can offer a not-found affordance rather than an
   * indefinite spinner. An unresolved identifier is a legitimate state: it is what a stale bookmark produces.
   */
  protected readonly notFound = computed<boolean>(
    () =>
      this.addressedModuleId() !== undefined
      && this.store.module() === null
      && !this.store.moduleLoading()
      && !this.readRefused(),
  );

  /**
   * Whether one of this screen's two reads FAILED for a reason other than the module being absent.
   *
   * A REFUSAL IS NOT AN ABSENCE. The server answers 403 when the caller may not see the module and 404
   * when there is no such module; both leave this screen holding nothing, so a test for "nothing in
   * hand" cannot tell them apart. Reporting a refusal through the not-found affordance states something
   * untrue, and states it beside the accurate sentence the banner is already showing.
   *
   * The predicate is "any failed read except an absence" rather than "a 403", because every other
   * status carries the same defect for the same reason: a read that failed with a fault did not answer
   * the question of existence either. The one status that DOES answer it is 404, which is left to
   * {@link notFound} and its shared sentence.
   *
   * Both reads are named, because this screen issues both together ({@link loadAddressedModule}) and a
   * refusal may be raised by either. Writes are excluded: a rejected save must keep the form on screen,
   * which is what {@link concludeSubmission} depends on.
   *
   * MIGRATION: the presentation of a refusal is the shared banner and nothing else, at warning
   *   severity. `Website/admin/Security/AccessDenied.ascx.vb:L41-L45` raised
   *   `ModuleMessage.ModuleMessageType.YellowWarning` on BOTH of its branches - a module message
   *   rendered IN the page rather than a transient advisory - and 403 is exactly the status
   *   `core/utils/form-errors.util.ts` resolves to warning severity. Holding the document rather than
   *   reducing it to a sentence is also what retains the trace identifier, the only join key between
   *   what the operator saw and what the server logged.
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

  /**
   * The permission keys this module's definition declares, or `null` when the list is not available.
   *
   * Exposed read-only for the advisory line beside the inherit switch. The template renders that line
   * only when this is a non-`null`, non-empty list, so the three unavailable cases — not administered,
   * not yet returned, and failed — are indistinguishable to the reader, which is correct: in every one of
   * them this client has nothing to say about the definition's keys.
   *
   * ⚠ NOT A PERMISSION CHECK, AND NOTHING IS GATED ON IT. These are the keys the DEFINITION declares,
   * not the keys the CALLER holds, so testing this list to decide what an operator may do would confuse a
   * vocabulary with a grant. Element-level gating is the shared `hasPermission` directive's job and it
   * reads the session, not this list.
   */
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
   * Opens a closed region or closes an open one.
   *
   * MIGRATION: the legacy toggle was reachable by pointer only - `sectionheadcontrol.ascx` rendered its
   * toggle with `tabIndex="-1"` (defect D-M12) and `labelcontrol.ascx`'s help toggle was likewise unreachable
   * from the keyboard (defect D11). Both are annotated as defects; the replacement is a real `button`, which
   * is keyboard-operable by construction. That is an accessibility gain with no visual cost, not a change to
   * the screen's behaviour.
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
   * The element identifier for a field's help text, referenced by its control's `aria-describedby`.
   *
   * @param field The field.
   * @returns A stable identifier scoped to this screen.
   */
  protected hintId(field: ModuleSettingsField): string {
    return `module-settings-${field}-hint`;
  }

  /**
   * The element identifier for a field's validation message.
   *
   * @param field The field.
   * @returns A stable identifier scoped to this screen.
   */
  protected messageId(field: ModuleSettingsField): string {
    return `module-settings-${field}-message`;
  }

  /**
   * The element identifier for a choice group's visible name.
   *
   * MIGRATION: a radio group's name is carried by a `label` with no `for`, referenced through
   * `aria-labelledby`. Pointing `for` at the first radio would name the group by side effect and make
   * clicking its title select an option - which is stronger than the legacy screen managed, since its label
   * pointed at the table ASP.NET rendered the group as and therefore named nothing.
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
   * The DOM id of one section's BODY, for the toggle's `aria-controls`.
   *
   * Distinct from {@link ModuleSettingsComponent.sectionHeadingId}, which names the toggle itself; a control cannot
   * point `aria-controls` at its own id and expect assistive technology to find the region.
   * The reference is published ONLY while the section is open — the body is removed from the
   * document when collapsed, so a constant attribute would leave a dangling IDREF that an
   * auditing tool reports as an error. Binding it to `null` in the closed state removes the
   * attribute outright, so the id is asserted exactly when it resolves.
   *
   * @param section The section whose body is being named.
   * @returns A stable id, unique within the screen.
   */
  protected sectionBodyId(section: ModuleSettingsSection): string {
    return `module-settings-body-${section}`;
  }

  /**
   * The server-supplied messages for one control, if any.
   *
   * MIGRATION: the per-field dictionary is an INDEX SIGNATURE and `noPropertyAccessFromIndexSignature` is
   * enabled, so an entry is read with an index expression and never with a property access. The keys are
   * .NET model-state keys and are NOT camel-cased, which is precisely why matching them is delegated to
   * `core/utils/form-errors.util.ts` rather than attempted here.
   *
   * @param field The control to report on.
   * @returns The messages, or an empty list.
   */
  protected serverMessages(field: ModuleSettingsField): readonly string[] {
    return fieldErrorMessages(this.currentProblem(), field);
  }

  /**
   * The client-side validation message for one control, when one is currently reportable.
   *
   * ⚠ THIS EXISTS SO THAT ONE FIELD HAS EXACTLY ONE MESSAGE REGION, WITH EXACTLY ONE IDENTIFIER. The client
   * message and the server messages were previously two sibling elements that both bound `messageId(field)`,
   * which is a duplicated identifier for the four controls that carry a client rule — invalid markup, and an
   * `aria-describedby` that resolves to whichever element the browser happens to find first. Reporting both
   * kinds through a single region removes the collision at its source rather than papering over it with a
   * second identifier, and it means a control's description names one element whose content is the complete
   * set of reasons the value was refused, in the order they were produced: locally first, then by the server.
   *
   * Only four of this screen's fields have a client rule at all — the legacy screen declared exactly four
   * validators (`valtxtStartDate`, `valtxtEndDate`, `valCacheTime` and the title's length bound) — so every
   * other field returns `null` here and its region carries server messages alone.
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
   * Whether a field currently has anything to report, from either side.
   *
   * This is the single condition that both the message region's presence and the control's
   * `aria-describedby` are derived from, so the two cannot drift apart: a named region always exists, and an
   * existing region is always named.
   *
   * @param field The field to report on.
   * @returns `true` when a client or server message is on screen for the field.
   */
  protected hasMessages(field: ModuleSettingsField): boolean {
    return this.clientMessage(field) !== null || this.serverMessages(field).length > 0;
  }

  /**
   * The identifiers a control's `aria-describedby` should name: its own hint, and its message region
   * when there is something to report.
   *
   * ⚠ THE MESSAGE REGION HAS TO BE NAMED HERE, AND THE EARLIER REASONING FOR OMITTING IT WAS WRONG. A
   * `role="alert"` region is announced ONCE, at the moment it appears, and never again — so a person who
   * hears it, moves to the control to correct the value and then returns is told nothing, and has no way
   * to reach the message from the control at all. That is the ordinary case rather than an edge one: the
   * whole purpose of a per-field message is that the person goes to that field.
   *
   * The message is also not transient in the sense the earlier note assumed. It stays on screen until the
   * next save answers, which is exactly as long as the hint does, so for the time it exists it IS part of
   * the control's description.
   *
   * The hint is named first so it is announced first, which keeps the order the same whether or not a
   * message is present. The region's identifier is omitted entirely when there is no message, rather than
   * named and empty: naming an element that does not exist leaves a dangling reference.
   *
   * @param hintField The field whose hint describes the control.
   * @param errorField The field messages are reported under, when it differs from the hint's — the
   * permission switch is described by its region's hint but reports under its own control name.
   * @returns A space-separated identifier list for `aria-describedby`.
   */
  protected describedBy(
    hintField: ModuleSettingsField,
    errorField: ModuleSettingsField = hintField,
  ): string {
    const hint = this.hintId(hintField);

    return this.hasMessages(errorField) ? `${hint} ${this.messageId(errorField)}` : hint;
  }

  /**
   * Names the region holding a control's failures, for `aria-errormessage`.
   *
   * ⚠ THIS IS A SEPARATE ASSOCIATION FROM THE DESCRIPTION, NOT A DUPLICATE OF IT, and this screen
   * was missing it. `aria-describedby` says "this text describes the control" and is announced
   * whenever the control is reached; `aria-errormessage` says "this text is the ERROR", and assistive
   * technology is free to treat the two differently - announcing the failure with its own wording, or
   * offering a command to jump to it. The shared field component publishes both, so nearly every form
   * in the application does; this screen predates that component and published only the description.
   * Runtime measurement caught the gap directly: on the module form the icon control reported
   * `aria-errormessage="module-form-icon-file-error"`, and on this screen the same rule, refused on
   * the same control for the same reason, reported none.
   *
   * Returns `null` rather than an empty string when there is nothing to name, because Angular removes
   * an attribute bound to `null` and an `aria-errormessage` pointing at nothing is worse than its
   * absence - it is a dangling reference the control asserts is an error message.
   *
   * The identifier is the SAME region `describedBy` names, and deliberately so: there is one message
   * region per control, holding the client failure and any server messages together, so both
   * associations point at it and neither invents a second element.
   *
   * @param field The field whose failures are reported. Pass the field messages are reported UNDER,
   * which for the permission switch differs from the field whose hint describes it.
   * @returns The message region's identifier, or `null` when the control has nothing to report.
   */
  protected errorMessageId(field: ModuleSettingsField): string | null {
    return this.hasMessages(field) ? this.messageId(field) : null;
  }

  // -----------------------------------------------------------------------------------------------------
  // INTENTS
  // -----------------------------------------------------------------------------------------------------

  /**
   * Opens the destructive confirmation.
   *
   * Reached from an affordance the legacy markup declared with `CausesValidation="False"` (L221-L225), so no
   * validation is triggered and an incomplete form does not block a removal.
   */
  protected requestRemoval(): void {
    this.removalOpen.set(true);
  }

  /** Closes the destructive confirmation without acting. */
  protected abandonRemoval(): void {
    this.removalOpen.set(false);
  }

  /**
   * Closes the confirmation and removes the placement.
   *
   * MIGRATION: the removal is the SOFT one described on this class - the placement row goes and the module is
   * marked deleted only once it is left on no page. There is no restore path, because no recycle-bin endpoint
   * exists, so the advisory says what happened rather than implying it can be undone.
   */
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

    // MIGRATION: THE REMOVAL IS SOFT AND THERE IS NO WAY BACK. `cmdDelete_Click` (L300-L312) called
    // `DeleteTabModule(TabId, ModuleId)` (L837), NOT `DeleteModule` (L819): the placement row goes, the order
    // is rebuilt, and only once the module is left on no page at all does L837-L860 set `TabID =
    // Null.NullInteger; IsDeleted = True`. No recycle-bin restore or purge endpoint exists, so the advisory
    // states what happened rather than implying it can be undone. The comment at L290-L292 claiming this
    // deletes a PORTAL in SuperUser mode is wrong on both counts - L305 performs no SuperUser check - and is
    // annotated here rather than reproduced.
    // MIGRATION: the announcement, the notification of the host and the departure all wait for the SERVER,
    // and previously did not. The legacy handler was a synchronous postback: L305 called `DeleteTabModule`
    // and only the statement AFTER it returned redirected, so a throwing call fell through to `Catch` and
    // the operator stayed on a screen showing an error. Announcing at the point of dispatch claimed a
    // removal that a refusal - a caller without edit rights on the module, or a placement already gone -
    // would then contradict, and left the operator on the listing with no way back to the screen holding
    // the explanation.
    this.removalOutstanding.set(true);
    this.store.deleteModule(id, this.addressedTabModuleId());
  }

  /**
   * Abandons the form.
   *
   * The legacy affordance carried `CausesValidation="False"`, so nothing is validated on the way out.
   */
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
   * Validates the form and submits it.
   *
   * MIGRATION: THE MEASURED READ-MODIFY-WRITE, WITH EVERY OPTION-STRICT COERCION MADE EXPLICIT.
   * `ModuleSettings.ascx.vb:L326-L427` guarded on `Page.IsValid` (L328), re-read the module (L341-L343),
   * overwrote each property from the form and committed with `UpdateModule` (L385). The coercions it
   * performed implicitly - because `Website/release.config:L125` compiled the admin code-behinds with
   * `strict="false"` - are performed explicitly here:
   *
   *   - `Border = txtBorder.Text` (L347) assigned a RAW STRING into a property that really is declared
   *     `As String` (`ModuleInfo.vb:L230`) despite being integer-validated. The column is not transported, so
   *     there is nothing to assign; the inconsistency is recorded rather than reproduced.
   *   - `CacheTime` (L349-L353) used `Int32.Parse`, NOT `TryParse`, so a value the validator had let through
   *     could still throw. Here the parse is explicit and its failure is handled: an unparsable or hidden
   *     field yields 0, which is exactly what the legacy empty-field branch wrote.
   *   - both dates (L367-L376) used `Convert.ToDateTime` on free text and fell back to `Null.NullDate`. Here
   *     an unparsable date becomes `null` on the wire rather than a sentinel instant, because the contract's
   *     members are nullable and `null` is how it expresses "no restriction".
   *   - `Select Case Int32.Parse(cboVisibility.SelectedItem.Value)` (L359-L363) had NO `Case Else`, so an
   *     out-of-range code silently left the previous value in place. The typed control cannot hold an
   *     out-of-range code, so the gap cannot arise.
   *
   * An invalid form is marked touched rather than submitted, so every field-level message becomes visible at
   * once instead of the operator discovering them one at a time.
   */
  protected onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // `this.settings` and NOT the raw signal: the resolution order - a caller's seed first, otherwise the
    // loaded module - lives in one place, and reading the signal directly here would make a route-driven
    // submission impossible, because the route path seeds the FORM from the store without ever assigning the
    // input. Read the resolved value, exactly as the template does.
    const current = this.settings;

    // A submission with nothing seeded cannot be composed: `tabId` and the round-tripped columns come from
    // the loaded state, and 0 is a legitimate page, so no value could stand in for one that was never read.
    // The template only renders the form once a module has resolved, so this guard is unreachable through the
    // interface; it is here because a required wire member must never be defaulted.
    if (current === null) {
      return;
    }

    const request = this.toUpdateRequest(current);

    // THE PREVIOUS REFUSAL IS DISCARDED before this one goes out, matching the portal settings, portal
    // alias, user form, user list, membership settings and profile definition screens. Without it a
    // refusal from an earlier attempt stayed on display through the next one, describing a response that
    // had already been superseded — and after a corrected submission succeeded, the old failure was still
    // the most prominent thing on the screen.
    //
    // Cleared here rather than on each keystroke for the reason those screens clear it here: the banner
    // reports the server's last answer and holds the correlation reference, both of which stay true until
    // a new answer arrives.
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
    // observer cannot mistake a not-yet-started submission for a finished one. The module replacement is
    // always issued, so there is always one raised flag to wait on even when the settings write is withheld.
    // The announcement and the return to the listing are raised there rather than here; see
    // concludeSubmission for why.
    this.submissionPending.set(true);
  }

  // -----------------------------------------------------------------------------------------------------
  // PRIVATE
  // -----------------------------------------------------------------------------------------------------

  /**
   * Projects the form onto the update contract.
   *
   * Exactly the seventeen members the server declares, and nothing else. An eighteenth member would be an
   * HTTP 400 under `JsonUnmappedMemberHandling.Disallow`, and an omitted nullable member would CLEAR its
   * column, because the request is a whole-row replacement - which is what the legacy postback was too, where
   * an emptied text box posted an empty value. The relocation member is the one exception to the
   * whole-row reading: it names no column and carries an instruction, so `null` there means "do not move"
   * rather than "clear something".
   *
   * @param seed The loaded state the round-tripped columns come from.
   * @returns The document to submit.
   */
  private toUpdateRequest(seed: ModuleSettingsSeed): UpdateModuleRequest {
    const raw = this.form.getRawValue();

    // WHICH PLACEMENT, AND WHERE IT IS GOING, ARE TWO SEPARATE ANSWERS. The picker on this screen is
    // labelled "Move To Page:", so its value is a DESTINATION, never a selector. The page being edited is
    // the page the module was loaded from, which is the seed's - and the seed is the only trustworthy source
    // for it, because the picker's value changes the moment the operator touches it.
    //
    // Sending the picker's value as `tabId` is what made the control unusable: the server selects the
    // placement by that member, a page the module does not occupy has no placement, and so choosing any page
    // other than the current one produced `module.placement_not_found` and moved nothing. The control was
    // labelled with an action that could not succeed.
    //
    // MIGRATION: `ModuleSettings.ascx.vb:L403-L408` performed the relocation as a separate
    // `MoveModule(ModuleId, TabId, newTabId, "")` call after the update had committed, guarded by
    // `If TabId <> newTabId`. That guard is reproduced here rather than on the server alone, so an ordinary
    // save carries no relocation instruction at all instead of one that happens to be a no-op.
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
      // MIGRATION: PRESERVED, NOT FORCED FALSE. `ModuleSettings.ascx.vb:L364` assigned
      // `objModule.IsDeleted = False` unconditionally, so every save silently UN-DELETED a module that the
      // recycle bin held. That is a defect rather than an intention, and the clause C-1 discipline is to
      // annotate it and carry the stored value through rather than reproduce it.
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
   * Implements the write half of the three-state rule documented on {@link showCacheField}: a hidden field
   * and an empty field both write 0, matching L349-L353, and a present value is parsed explicitly. The parse
   * cannot normally fail because the integer data-type check has already passed, but it is handled anyway
   * rather than assumed - which is the whole point of replacing `Int32.Parse` with something explicit.
   *
   * @param text The control's text.
   * @returns The period in seconds.
   */
  private resolveCacheTime(text: string): number {
    // MIGRATION: a hidden field writes 0, exactly as `ModuleSettings.ascx.vb:L349-L353` did. The zero written
    // here is a real saved cache period meaning "do not cache this instance"; it is NOT the definition's -1,
    // which means "this definition does not support caching at all". The two are never conflated.
    if (!this.showCacheField()) {
      return 0;
    }

    const trimmed = text.trim();

    if (trimmed.length === 0) {
      return 0;
    }

    // MIGRATION: the Option-Strict coercion made EXPLICIT, with the failure handled. The administration
    // code-behinds compiled with `strict="false"` (`Website/release.config:L125`), so L349-L353's
    // `Int32.Parse(txtCacheTime.Text)` on free text was legal and threw on bad input. The integer data-type
    // check should already have rejected anything unparseable, but the outcome is decided here rather than
    // assumed - which is the entire reason `Int32.Parse` is not carried across as-is.
    const parsed = Number(trimmed);

    return Number.isInteger(parsed) ? parsed : 0;
  }

  /**
   * Writes the settings maps, but ONLY when this screen has actually changed them.
   *
   * MIGRATION: THE UNCHANGED BAG IS NO LONGER RE-SENT, AND THAT IS A CORRECTION RATHER THAN AN
   * OPTIMISATION. This screen edits columns on the module and its placement; it renders no control over a
   * property-bag entry, so the bag it would send is the bag it read, byte for byte. Re-sending it turned
   * every save of an unrelated field into a whole-object replacement of both property maps computed from a
   * possibly stale read — so a key another operator, another screen or a background job had written between
   * this screen's read and its save was silently reverted to the value this screen happened to be holding.
   * A lost update, caused by a request that could not change anything even when it won.
   *
   * The suppression is expressed as a genuine comparison against the bag as read rather than as a removed
   * call, for two reasons. It states the rule — send what changed — instead of encoding today's field set as
   * an assumption. And it self-arms: the day a settings editor is added here, the comparison starts
   * reporting a difference and the write resumes with no further change.
   *
   * The two legacy scopes still collapse into one whole-object PUT — `ModuleController.vb` exposed
   * `GetModuleSettings(ModuleId)` at L1237 and `GetTabModuleSettings(TabModuleId)` at L1336 as DISTINCT
   * scopes, each mutated one key at a time through L1283 / L1306 / L1318 / L1373 / L1395 / L1407 — so when a
   * write IS warranted it carries both maps whole, because omitting a map would empty it.
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
   * Whether the bag about to be written differs from the bag that was read.
   *
   * A structural comparison over every member the contract declares: the two identifiers and both property
   * maps, key by key in both directions so that an added, removed or re-valued key is all reported. Nothing
   * is compared by reference, because a bag rebuilt from an identical response would fail that test and
   * produce exactly the needless write this guard exists to prevent.
   *
   * Reports `true` when no read has been recorded. That is the safe direction: without a reference there is
   * nothing to prove the bag unchanged, and withholding a write on an unproven assumption would lose an edit.
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
   * Re-seeds every control from the supplied state.
   *
   * MIGRATION: the two instruction flags are seeded UNSET rather than from the module, because neither is a
   * column on it - each describes work the server performs after the update, and echoing a previous
   * instruction back onto the form would reapply it on the next submission.
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
      // '0' is a legitimate stored period meaning "not cached" and must render AS 0 rather than as an empty
      // box - the empty box means "no value was entered", which writes 0 for a different reason. The two are
      // kept distinguishable, so `String()` is applied to a checked number and never to `null`, which would
      // put the text 'null' into the control.
      cacheTime: value.cacheTime === null ? '0' : String(value.cacheTime),
      setAsDefaultSettings: false,
      applyToAllModules: false,
    });

    // reset() re-enables every control, so the privilege locks must be reapplied after it.
    this.applyPrivilegeLocks();
  }

  /**
   * Locks or releases the controls that reach beyond the page being edited.
   *
   * `emitEvent: false` keeps the lock from looking like an edit.
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
   * Reports a failure at the severity the legacy screen used for it.
   *
   * A rejected write keeps its problem document so the banner can show the per-field messages; a refusal, a
   * missing module and a conflict are announced as advisories, at warning severity for the refusal and at
   * error severity for the rest. The 429 status is deliberately not handled: the server's global limiter
   * classifies a request from endpoint metadata - the `[CredentialEndpoint]` marker - falling back to a whole
   * credential path segment, and the module actions carry no marker and no such segment, so it cannot arise
   * on this screen. Verify the marker before adding a branch, because that is what would make it reachable.
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
      // A REFUSAL GOES TO THE BANNER, NOT TO THE ADVISORY QUEUE, and that is a correction rather than a
      // preference. It used to be announced as a warning notification with the document discarded, on the
      // grounds that `Website/admin/Security/AccessDenied.ascx.vb:L41-L45` used `YellowWarning`. The
      // severity reading was right and the surface was wrong: `AddModuleMessage` inserted the message
      // INTO the page, and the shared banner is what ports that - it resolves 403 to the warning band
      // through `core/utils/form-errors.util.ts` and paints it in place, so the legacy severity survives
      // either way. Three things did not survive the notification: the document's trace identifier, which
      // is the only join key between what the operator saw and what the server logged; the problem title,
      // which names the class of failure; and permanence, because a transient advisory expires while the
      // condition it describes does not. The sibling module screens present the same status through the
      // same banner, so one backend condition now has one presentation across the feature.
      //
      // NO NULL-DOCUMENT FALLBACK IS NEEDED HERE, and one was written and then removed as provably dead.
      // The status above is read as `problem?.status ?? null`, so reaching this branch REQUIRES a
      // non-null document - and the store never produces a bodiless refusal in any case: a 403 whose
      // response body was empty is synthesised as `{ status: 403 }` by `problemFromCause`, precisely so
      // that severity and wording still resolve. The banner therefore always has something to render.
      this.currentProblem.set(problem);

      return;
    }

    // ⚠ THE SEVERITY IS DERIVED AND THE REFERENCE IS CARRIED, AND NEITHER WAS TRUE OF THE TWO BRANCHES
    // BELOW. Both announced at a hardcoded `'error'` and both dropped the document on the floor, which broke
    // two rules this application states elsewhere and had one measurable consequence each.
    //
    // On severity, `core/utils/form-errors.util.ts` is explicit that it is the ONE place a response status
    // becomes a severity and that no consumer may re-derive or override its answer - a surface that
    // disagrees must change that function so the disagreement is settled for every surface at once. These
    // two branches quietly disagreed. It was visible: on an administrative module this screen issues three
    // reads, and a browser audit found the settings refusal painted in the banner's WARNING band beside a
    // toast for the concurrent definition lookup shouting ERROR - one screen, two refusals, two different
    // severities for statuses the shared rule words alike. Deriving it changes the 404 to a warning and
    // leaves the 409 exactly as it was, because that is what the shared rule already returns for a conflict.
    //
    // On the reference, the document is right here in hand and was being discarded. The identifier it
    // carries is the only join key between what an operator saw in the browser and the request as the
    // server recorded it, and the same audit measured the asymmetry it produced: a conflict presented in
    // the banner carried `Reference: <correlationId>`, while these toasts carried nothing an operator could
    // quote. The notification surface has held a first-class `reference` member all along, appended after
    // its own message bound precisely so a long server sentence cannot truncate the identifier away.
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
    // above all - goes to it with the problem document intact, so the per-field messages reach the fields they
    // belong to. There is no predecessor to port: a case-insensitive census of `asp:ValidationSummary` across
    // `Website/` returns ZERO occurrences, in this feature and tree-wide, which puts the cited source in the
    // same vacuous family as the Telerik and COM-interop exclusions. Reported as such rather than invented.
    this.currentProblem.set(problem);

    if (problem === null) {
      this.notifications.notify('error', `The ${operation} request could not be completed.`);
    }
  }

  /** Returns to the listing, which is what the legacy redirect at L421 did. */
  private returnToListing(replaceEntry = false): void {
    /*
     * ⚠ THE CALL IS MADE TWO DIFFERENT WAYS ON PURPOSE, rather than always passing an options
     * object with a computed flag. A pushed departure keeps the exact call it always made, so the
     * behaviour of the cancel paths - and the specifications that pin them - is untouched by the
     * addition; only a REPLACING departure carries options, which is the case whose behaviour
     * genuinely changed. Written this way, the diff says what changed and nothing else.
     */
    if (replaceEntry) {
      void this.router.navigate([MODULE_LIST_ROUTE], { replaceUrl: true });

      return;
    }

    void this.router.navigate([MODULE_LIST_ROUTE]);
  }
}

/**
 * Builds the "Move To Page" options from the tenant's page list.
 *
 * The page the module currently occupies is guaranteed present, prepended when the list does not contain it,
 * so the picker always shows where the module actually is. Deleted pages are excluded, because moving a
 * module onto a page in the recycle bin is not an outcome an operator can want; the currently occupied page
 * survives that filter, because showing where the module is takes precedence over hiding a removed page.
 *
 * MIGRATION: `parentId` of -1 marks a ROOT-LEVEL page and is not an absence, and `tabId` of 0 is a REAL page
 * because `dbo.Tabs.TabID` is `IDENTITY(0, 1)`. Neither value is filtered, defaulted or tested for truth.
 * Depth is expressed by indenting the label with the page's own `level`, which is the flat list's own
 * hierarchy fact, rather than by rebuilding a tree this picker does not need.
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
