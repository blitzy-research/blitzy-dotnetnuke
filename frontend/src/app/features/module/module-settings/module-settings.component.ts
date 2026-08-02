import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  MODULE_ALIGNMENT,
  MODULE_VISIBILITY,
  type ModuleSettings,
  type ModuleVisibility,
  type UpdateModuleRequest,
} from '../../../core/models/module.model';
import type { SelectOption } from '../../../core/models/select-option.model';

/**
 * The seven disclosure regions this screen presents, named after the legacy section heads they replace.
 *
 * Three are top level and four are nested one level beneath them, reproducing the two-level hierarchy of
 * `Website/admin/Modules/modulesettings.ascx`: dshModule (L11) holds dshDetails (L22) and dshSecurity (L54);
 * dshPage (L97) holds dshAppearance (L108) and dshOther (L178); dshSpecific (L200) stands alone.
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
 * The typed shape of this screen's form.
 *
 * Every control is declared `nonNullable`, so `getRawValue()` is fully typed rather than a `Partial`, and a
 * reset returns each control to its declared initial value instead of to `null`.
 */
interface ModuleSettingsFormModel {
  moduleTitle: FormControl<string>;
  paneName: FormControl<string>;
  moduleOrder: FormControl<number>;
  allTabs: FormControl<boolean>;
  inheritViewPermissions: FormControl<boolean>;
  header: FormControl<string>;
  footer: FormControl<string>;
  startDate: FormControl<string>;
  endDate: FormControl<string>;
  iconFile: FormControl<string>;
  alignment: FormControl<string>;
  color: FormControl<string>;
  border: FormControl<string>;
  visibility: FormControl<ModuleVisibility>;
  displayTitle: FormControl<boolean>;
  displayPrint: FormControl<boolean>;
  displaySyndicate: FormControl<boolean>;
  cacheTime: FormControl<number>;
  isDefaultModule: FormControl<boolean>;
  allModules: FormControl<boolean>;
}

/** The bound on `dbo.Modules.ModuleTitle` (`nvarchar(256) NULL`). */
const MODULE_TITLE_MAX_LENGTH = 256;

/** The bound on `dbo.TabModules.PaneName` (`nvarchar(50) NOT NULL`). */
const PANE_NAME_MAX_LENGTH = 50;

/** The bound on `dbo.TabModules.IconFile` (`nvarchar(100) NULL`). */
const ICON_FILE_MAX_LENGTH = 100;

/** The bound on `dbo.TabModules.Alignment` (`nvarchar(10) NULL`). */
const ALIGNMENT_MAX_LENGTH = 10;

/** The bound on `dbo.TabModules.Color` (`nvarchar(20) NULL`). */
const COLOR_MAX_LENGTH = 20;

/** The bound on `dbo.TabModules.Border` (`nvarchar(1) NULL`) — one character used as a flag. */
const BORDER_MAX_LENGTH = 1;

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
 * disagree in one place that matters — the markup calls the second nested region "Security Settings" while
 * `Security.Text` is 'Advanced Settings'. The resource wins, because that is what the legacy screen actually
 * rendered. Two regions therefore legitimately share the heading 'Basic Settings' and two share 'Advanced
 * Settings'; each is disambiguated for assistive technology by the region it sits in, not by its wording.
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
 * its closing parenthesis exactly as `ModuleSettingsHelp.Text` carries it — see the note on FIELD_HINTS for
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
  displayPrint: 'Allow Print?',
  displaySyndicate: 'Allow Syndicate?',
  cacheTime: 'Cache Time (secs):',
  isDefaultModule: 'Set As Default Settings?',
  allModules: 'Apply To All Modules?',
} as const;

/** The keys of {@link FIELD_HINTS} and {@link FIELD_LABELS}. */
export type ModuleSettingsField = keyof typeof FIELD_LABELS;

/**
 * The migrated help text, one entry per field, taken from the `pl*.Help` entries of
 * `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 *
 * MIGRATION — WHY THESE ARE CONSTANTS AND NOT TEMPLATE TEXT: Angular compiles templates with
 * `preserveWhitespaces` disabled, which collapses every run of whitespace inside a text node to a single
 * space. Four of these strings put TWO spaces after a sentence period (`plStartDate.Help`, `plEndDate.Help`,
 * `plTitle.Help`, and `plPermissions.Help` twice), so writing them as template text would silently rewrite
 * wording an existing operator recognises. Interpolated values are not collapsed, so every migrated string
 * on this screen is declared here and bound.
 *
 * A second consequence, deliberate: because these are interpolated rather than parsed, the embedded bold
 * markup the legacy `InheritPermissions.Text` carried ('Inherit &lt;b&gt;View&lt;/b&gt; permissions from
 * &lt;b&gt;Page&lt;/b&gt;') renders as plain text. The emphasis is lost; the string cannot become an
 * injection vector.
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
  allTabs: 'Select whether the module should appear in the same location on all pages of the site',
  header: 'Enter header text for this Module',
  footer: 'Enter footer text for this Module',
  startDate: 'Enter the start date for displaying this module.  You may use the Calendar to pick a date.',
  endDate: 'Enter the end date for displaying this module.  You may use the Calendar to pick a date.',
  iconFile: 'Select an Icon for this Module to display in the Title Bar',
  alignment: 'Select the Alignment of the Module',
  color: 'Enter a Color for the module',
  border: 'Enter a Border width for the Module',
  visibility: 'Choose the default visibility for this Module',
  displayTitle: 'Select this option if you would like to display the Module container.',
  displayPrint: 'Select this option if you would like to enable printing on this module',
  displaySyndicate: 'Select this option if you would like to enable RSS on this module',
  cacheTime: 'Enter the time this object is kept in the Cache',
  isDefaultModule:
    'Select this option if you would like the Page Settings for this module to be used as the default '
    + 'settings when adding new modules.',
  allModules:
    'Select this option if you would like the Page Settings for this module to be applied to all existing '
    + 'modules in the site.',
};

/**
 * The confirmation the legacy screen raised before a deletion.
 *
 * MIGRATION: `ModuleSettings.ascx.vb` wired its delete affordance to a client-side confirmation. The legacy
 * resource carries no module-specific wording, so the shared confirmation phrasing is used and the module's
 * own name is interpolated to make the target unambiguous — which the legacy prompt did not do.
 */
const DELETE_CONFIRM_TITLE = 'Delete Module';

/** The label on the destructive confirmation's accept affordance. */
const DELETE_CONFIRM_LABEL = 'Delete';

/**
 * The message shown when no module has been supplied.
 *
 * The screen is addressed by identifier, so an unresolved identifier is a legitimate state rather than an
 * error: it is what a stale bookmark or a deleted module produces.
 */
const NO_MODULE_MESSAGE = 'No module settings are available.';

/**
 * The validation message for the border flag.
 *
 * MIGRATION: reproduced from `ModuleSettings.ascx.resx` valBorder.ErrorMessage, whose leading `<br>` was
 * markup for the inline validator summary and is dropped. This is the one appearance field the legacy screen
 * genuinely validated — `modulesettings.ascx:L135` declares a CompareValidator with
 * `Operator="DataTypeCheck" Type="Integer"` against a `MaxLength="1"` text box, so a legal value is exactly
 * one digit. The same rule is enforced server side by `UpdateModuleRequestValidator`.
 */
const BORDER_INVALID_MESSAGE = 'Invalid Border (must be a number between 0 and 9)';

/** The validation message for a negative cache period, from `ModuleSettings.ascx.resx` valCacheTime. */
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';

/** The validation message for a title longer than its column. */
const MODULE_TITLE_TOO_LONG_MESSAGE = 'Title must be 256 characters or fewer.';

/** The validation message for a missing pane, matching the server's PaneRequiredMessage. */
const PANE_REQUIRED_MESSAGE = 'A pane must be selected.';

/**
 * The alignment choices, declared inline in the legacy markup rather than fetched.
 *
 * MIGRATION: the order is the legacy order, which puts "Not Specified" LAST rather than first. That is
 * deliberate: `modulesettings.ascx:L122-L127` lists Left, Center, Right, Not Specified in that sequence, and
 * a horizontal radio group's reading order is its meaning. The empty value is a real choice, not an absence.
 */
const ALIGNMENT_CHOICES: readonly SelectOption<string>[] = [
  { value: MODULE_ALIGNMENT.left, label: 'Left' },
  { value: MODULE_ALIGNMENT.center, label: 'Center' },
  { value: MODULE_ALIGNMENT.right, label: 'Right' },
  { value: MODULE_ALIGNMENT.notSpecified, label: 'Not Specified' },
];

/** The visibility choices, from `modulesettings.ascx:L144-L148`. */
const VISIBILITY_CHOICES: readonly SelectOption<ModuleVisibility>[] = [
  { value: MODULE_VISIBILITY.maximized, label: 'Maximized' },
  { value: MODULE_VISIBILITY.minimized, label: 'Minimized' },
  { value: MODULE_VISIBILITY.none, label: 'None' },
];

/**
 * Narrows an instant to the date a `<input type="date">` expects.
 *
 * The value is SLICED, never parsed. Constructing a `Date` from the instant and then formatting it locally
 * shifts the value into the browser's zone and moves the date by a day either side of midnight, so a module
 * scheduled to appear on the first of the month would be shown — and written back — as the last day of the
 * previous one.
 *
 * @param instant An ISO 8601 instant, or `null`.
 * @returns The leading `yyyy-mm-dd`, or an empty string when there is no instant.
 */
function toDateInputValue(instant: string | null): string {
  return instant === null ? '' : instant.slice(0, 10);
}

/**
 * Collapses a blank string to `null` for transmission.
 *
 * The columns behind these fields are nullable, and the server's projection is a whole-row replacement, so
 * an emptied control must be sent as an absent value for the column to be cleared.
 *
 * @param value The control's value.
 * @returns The trimmed value, or `null` when it holds nothing but whitespace.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Sends an alignment choice without collapsing the legacy "Not Specified" selection.
 *
 * MIGRATION: this is why {@link textOrNull} is not used for alignment. An empty string here means the
 * operator explicitly chose "Not Specified", which the legacy screen stored as an empty string, so it is sent
 * as one. Collapsing it to `null` would be indistinguishable from a column that was never written.
 *
 * @param value The control's value.
 * @returns The value unchanged.
 */
function alignmentValue(value: string): string {
  return value;
}

/**
 * The module settings screen, replacing `Website/admin/Modules/modulesettings.ascx` and its code-behind.
 *
 * The screen is purely presentational: it accepts the module's stored state and the lookup lists it needs,
 * and it emits the operator's intent. It performs no data access of its own, which is what keeps the
 * nine-service inventory closed and keeps this component testable without a transport.
 *
 * DELIBERATE DIVERGENCES, RECORDED RATHER THAN ABSORBED
 *
 *  - The "Move To Page" affordance (cboTab, `plTab.Text` 'Move To Page:') is NOT carried across. The agreed
 *    API inventory in the action plan enumerates this resource's operations explicitly and contains no move
 *    operation; adding one would be a new endpoint, service member and repository member outside that
 *    enumeration. Recorded as an out-of-scope divergence rather than silently dropped.
 *  - The module container selector (ctlModuleContainer) is NOT carried across, because a container is a skin
 *    object and skinning is excluded. The stored container value is preserved untouched by the server on
 *    every update rather than being cleared, so nothing is lost by not editing it here.
 *  - The two date fields paired a text box with a pop-up calendar link. A native date control provides the
 *    same affordance, so no calendar component is introduced.
 *  - The permission grid is described but not rendered here: permissions are their own resource with their
 *    own screen, and the legacy grid posted back through a control that has no counterpart. The inherit
 *    switch, which IS a column on the module, is kept.
 *  - The legacy inherit label carried embedded bold markup; it renders as plain text for the reason given on
 *    {@link FIELD_HINTS}.
 *  - Rich text editing is not carried forward: the header and footer are plain multi-line controls.
 */
@Component({
  selector: 'app-module-settings',
  standalone: true,
  // ReactiveFormsModule for the typed form; the four shared components are the only presentational
  // primitives this screen needs. There is deliberately no form-field or data-table import, because neither
  // exists in the shared inventory — the global style layer styles bare form elements directly.
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './module-settings.component.html',
  styleUrl: './module-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleSettingsComponent {
  /** The typed form backing every editable field on the screen. */
  protected readonly form = new FormGroup<ModuleSettingsFormModel>({
    moduleTitle: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MODULE_TITLE_MAX_LENGTH)],
    }),
    paneName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(PANE_NAME_MAX_LENGTH)],
    }),
    moduleOrder: new FormControl(0, { nonNullable: true }),
    allTabs: new FormControl(false, { nonNullable: true }),
    inheritViewPermissions: new FormControl(true, { nonNullable: true }),
    header: new FormControl('', { nonNullable: true }),
    footer: new FormControl('', { nonNullable: true }),
    startDate: new FormControl('', { nonNullable: true }),
    endDate: new FormControl('', { nonNullable: true }),
    iconFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(ICON_FILE_MAX_LENGTH)],
    }),
    alignment: new FormControl<string>(MODULE_ALIGNMENT.notSpecified, {
      nonNullable: true,
      validators: [Validators.maxLength(ALIGNMENT_MAX_LENGTH)],
    }),
    color: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(COLOR_MAX_LENGTH)],
    }),
    // The legacy CompareValidator required an integer in a one-character box, so exactly one digit.
    border: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(BORDER_MAX_LENGTH), Validators.pattern(/^[0-9]?$/)],
    }),
    visibility: new FormControl<ModuleVisibility>(MODULE_VISIBILITY.maximized, { nonNullable: true }),
    displayTitle: new FormControl(true, { nonNullable: true }),
    displayPrint: new FormControl(true, { nonNullable: true }),
    displaySyndicate: new FormControl(true, { nonNullable: true }),
    cacheTime: new FormControl(0, { nonNullable: true, validators: [Validators.min(0)] }),
    isDefaultModule: new FormControl(false, { nonNullable: true }),
    allModules: new FormControl(false, { nonNullable: true }),
  });

  /** The regions currently closed. Seeded from the legacy `isexpanded` attributes. */
  protected readonly collapsed = new Set<ModuleSettingsSection>(INITIALLY_COLLAPSED);

  /** Whether the destructive confirmation is showing. */
  protected removalPending = false;

  /** The section headings, exposed for binding. */
  protected readonly headings = SECTION_HEADINGS;

  /** The three explanatory paragraphs, exposed for binding. */
  protected readonly intros = SECTION_INTROS;

  /** The field labels, exposed for binding. */
  protected readonly labels = FIELD_LABELS;

  /** The migrated help text, exposed for binding. */
  protected readonly hints = FIELD_HINTS;

  /** The alignment choices. */
  protected readonly alignmentChoices = ALIGNMENT_CHOICES;

  /** The visibility choices. */
  protected readonly visibilityChoices = VISIBILITY_CHOICES;

  /** The heading of the destructive confirmation. */
  protected readonly deleteConfirmTitle = DELETE_CONFIRM_TITLE;

  /** The label on the destructive confirmation's accept affordance. */
  protected readonly deleteConfirmLabel = DELETE_CONFIRM_LABEL;

  /** The message shown when no module has been supplied. */
  protected readonly noModuleMessage = NO_MODULE_MESSAGE;

  /** The border validation message. */
  protected readonly borderInvalidMessage = BORDER_INVALID_MESSAGE;

  /** The cache period validation message. */
  protected readonly cacheTimeInvalidMessage = CACHE_TIME_INVALID_MESSAGE;

  /** The title-length validation message. */
  protected readonly moduleTitleTooLongMessage = MODULE_TITLE_TOO_LONG_MESSAGE;

  /** The missing-pane validation message. */
  protected readonly paneRequiredMessage = PANE_REQUIRED_MESSAGE;

  /** The column bounds the template advertises through `maxlength`. */
  protected readonly limits = {
    moduleTitle: MODULE_TITLE_MAX_LENGTH,
    paneName: PANE_NAME_MAX_LENGTH,
    iconFile: ICON_FILE_MAX_LENGTH,
    color: COLOR_MAX_LENGTH,
    border: BORDER_MAX_LENGTH,
  } as const;

  /** The backing field for {@link settings}. */
  private module: ModuleSettings | null = null;

  /** The backing field for {@link canManageAllPages}. */
  private allPagesManageable = false;

  /** The screen's heading. Defaults to the legacy module definition's own name. */
  @Input() heading = 'Module Settings';

  /** Whether the module is still being fetched. */
  @Input() loading = false;

  /** Whether a submission is in flight. */
  @Input() saving = false;

  /** Whether a deletion affordance should be offered. */
  @Input() canDelete = false;

  /**
   * The module and placement being edited, or `null` when none has resolved.
   *
   * Assigning re-seeds the form from the supplied state, so the screen never shows a value the store does
   * not hold.
   */
  @Input()
  set settings(value: ModuleSettings | null) {
    this.module = value;
    if (value !== null) {
      this.seed(value);
    }
  }

  /** The module and placement being edited, or `null`. */
  get settings(): ModuleSettings | null {
    return this.module;
  }

  /**
   * Whether the caller may change the three settings that reach beyond this page.
   *
   * MIGRATION: `ModuleSettings.ascx.vb:L333-L338` disabled chkAllTabs, chkDefault, chkAllModules and cboTab
   * for a caller who was not a portal administrator, because each of those settings alters pages the caller
   * does not administer. The three that survive are locked the same way — through the reactive forms API, so
   * that `getRawValue()` still carries the stored value unchanged and a locked field cannot be zeroed by
   * being absent from the submission.
   */
  @Input()
  set canManageAllPages(value: boolean) {
    this.allPagesManageable = value;
    this.applyPrivilegeLocks();
  }

  /** Whether the caller may change the three far-reaching settings. */
  get canManageAllPages(): boolean {
    return this.allPagesManageable;
  }

  /** Emits the submitted state when the operator accepts the form. */
  @Output() readonly save = new EventEmitter<UpdateModuleRequest>();

  /** Emits when the operator abandons the form. */
  @Output() readonly cancel = new EventEmitter<void>();

  /** Emits when the operator confirms a deletion. */
  @Output() readonly remove = new EventEmitter<void>();

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
   * MIGRATION: a radio group's name is carried by a `<label>` with no `for`, referenced through
   * `aria-labelledby`. Pointing `for` at the first radio would name the group by side effect and make
   * clicking its title select an option — which is stronger than the legacy screen managed, since its label
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

  /** Opens the destructive confirmation. */
  protected requestRemoval(): void {
    this.removalPending = true;
  }

  /** Closes the destructive confirmation without acting. */
  protected abandonRemoval(): void {
    this.removalPending = false;
  }

  /** Closes the destructive confirmation and emits the removal. */
  protected confirmRemoval(): void {
    this.removalPending = false;
    this.remove.emit();
  }

  /** Emits the abandonment. */
  protected onCancel(): void {
    this.cancel.emit();
  }

  /**
   * The message shown on the destructive confirmation.
   *
   * The module's own name is interpolated so the target is unambiguous, which the legacy prompt did not do.
   *
   * @returns The confirmation message.
   */
  protected deleteConfirmMessage(): string {
    const name = this.module?.moduleTitle ?? this.module?.friendlyName ?? '';
    return name.length === 0
      ? 'Are you sure you wish to delete this module?'
      : `Are you sure you wish to delete the module "${name}"?`;
  }

  /**
   * Validates and emits the form.
   *
   * An invalid form is marked touched rather than submitted, so every field-level message becomes visible at
   * once instead of the operator discovering them one at a time.
   */
  protected onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // getRawValue rather than value: it includes the controls locked by applyPrivilegeLocks, so a caller
    // without the privilege submits their stored values unchanged instead of clearing them.
    const raw = this.form.getRawValue();

    this.save.emit({
      moduleTitle: textOrNull(raw.moduleTitle),
      paneName: raw.paneName,
      moduleOrder: raw.moduleOrder,
      allTabs: raw.allTabs,
      inheritViewPermissions: raw.inheritViewPermissions,
      alignment: alignmentValue(raw.alignment),
      color: textOrNull(raw.color),
      border: textOrNull(raw.border),
      visibility: raw.visibility,
      displayTitle: raw.displayTitle,
      displayPrint: raw.displayPrint,
      displaySyndicate: raw.displaySyndicate,
      cacheTime: raw.cacheTime,
      iconFile: textOrNull(raw.iconFile),
      startDate: textOrNull(raw.startDate),
      endDate: textOrNull(raw.endDate),
      header: textOrNull(raw.header),
      footer: textOrNull(raw.footer),
      isDefaultModule: raw.isDefaultModule,
      allModules: raw.allModules,
    });
  }

  /**
   * Re-seeds every control from the supplied state.
   *
   * MIGRATION: the two intent flags are seeded to unset rather than from the module, because neither is a
   * column on it — each describes work the server performs after the update, and echoing a previous
   * instruction back onto the form would reapply it on the next submission.
   *
   * @param value The state to show.
   */
  private seed(value: ModuleSettings): void {
    this.form.reset({
      moduleTitle: value.moduleTitle ?? '',
      paneName: value.paneName,
      moduleOrder: value.moduleOrder,
      allTabs: value.allTabs,
      inheritViewPermissions: value.inheritViewPermissions ?? true,
      header: value.header ?? '',
      footer: value.footer ?? '',
      startDate: toDateInputValue(value.startDate),
      endDate: toDateInputValue(value.endDate),
      iconFile: value.iconFile ?? '',
      alignment: value.alignment ?? MODULE_ALIGNMENT.notSpecified,
      color: value.color ?? '',
      border: value.border ?? '',
      visibility: value.visibility,
      displayTitle: value.displayTitle,
      displayPrint: value.displayPrint,
      displaySyndicate: value.displaySyndicate,
      cacheTime: value.cacheTime ?? 0,
      isDefaultModule: false,
      allModules: false,
    });

    // reset() re-enables every control, so the privilege locks must be reapplied after it.
    this.applyPrivilegeLocks();
  }

  /**
   * Locks or releases the three settings that reach beyond the page being edited.
   *
   * The reactive forms API is used rather than a `disabled` attribute binding: binding the attribute on a
   * control a `formControlName` owns contests the directive for the property and raises Angular's reactive
   * forms warning. `emitEvent: false` keeps the lock from looking like an edit.
   */
  private applyPrivilegeLocks(): void {
    const gated = [this.form.controls.allTabs, this.form.controls.isDefaultModule, this.form.controls.allModules];

    for (const control of gated) {
      if (this.allPagesManageable) {
        control.enable({ emitEvent: false });
      } else {
        control.disable({ emitEvent: false });
      }
    }
  }
}
