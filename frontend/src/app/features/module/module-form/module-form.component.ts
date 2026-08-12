/**
 * The module create/edit screen.
 *
 * Replaces `Website/admin/Modules/modulesettings.ascx` and its code-behind
 * `Website/admin/Modules/ModuleSettings.ascx.vb` for the two operations the module write surface
 * actually exposes: placing a new module on a page, and replacing an existing placement.
 *
 * ONE COMPONENT, TWO ROUTES. The screen is reached from `'new'` (create) and from `':moduleId'`
 * (edit). Which one is in play is decided STRUCTURALLY, from whether the router supplied the
 * parameter at all, and never from the value of an identifier.
 *
 * ---------------------------------------------------------------------------------------------------
 * MIGRATION REGISTER — every deliberate difference from the legacy screen, stated once here and
 * annotated again at the line that causes it. None of these is absorbed silently.
 * ---------------------------------------------------------------------------------------------------
 *
 * MIGRATION: CREATE-VERSUS-EDIT IS NOW STRUCTURAL, NOT SENTINEL-VALUED. The legacy screen carried
 *   `Private Shadows ModuleId As Integer = -1` (`ModuleSettings.ascx.vb:L68`) and
 *   `TabModuleId As Integer = -1` (`:L69`), and every branch tested `ModuleId <> -1` (`:L222`).
 *   That test is not portable: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`
 *   (`01.00.00.SqlDataProvider:L221`), so ZERO is an ordinary module, and the legacy null contract
 *   simultaneously used -1 to mean "absent" (`Library/Components/Shared/Null.vb:L41-L45`). Here the
 *   two states are two ROUTES, and the presence test is `=== undefined` on the route parameter. No
 *   expression in this file compares an identifier against -1, against 0, or for truthiness.
 *
 * MIGRATION: THE LEGACY SCREEN HAD NO CREATE PATH AT ALL, so `/modules/new` is a NET-NEW screen.
 *   `cmdUpdate_Click` calls `objModules.GetModule(ModuleId, TabId, False)` unconditionally at
 *   `ModuleSettings.ascx.vb:L341` and dereferences the result at `:L343`; with `ModuleId = -1` that
 *   read returns `Nothing` and the handler throws. Only three create-mode facts are therefore
 *   legacy-derived - the three defaults at `:L225-L227` - and everything else about create mode
 *   comes from the wire contract.
 *
 * MIGRATION: THE DEFINITION IS IMMUTABLE AFTER CREATION, and that is measured rather than assumed.
 *   `ModuleController.vb:L645` passes TEN values to the provider's add operation
 *   (portal, definition, title, all-pages, header, footer, start, end, inherit, deleted) while
 *   `:L1095` passes NINE to its update operation - the definition is the one that is missing. The
 *   update contract accordingly declares no definition member, so the selector is disabled once a
 *   module exists, which is exactly what the legacy `txtFriendlyName Enabled="False"`
 *   (`modulesettings.ascx:L28`) rendered.
 *
 * MIGRATION: THE UPDATE IS A WHOLE-ROW REPLACEMENT, exactly as the legacy postback was -
 *   `cmdUpdate_Click` assigned twenty-three properties unconditionally on every save. An omitted
 *   member is NOT "leave it alone": the server writes the absent value. The sharpest instance is the
 *   placement position, whose server default APPENDS the module at the bottom of its pane, so the
 *   edit path reads the stored position and sends it back explicitly, and refuses to submit at all
 *   until the module has been read.
 *
 * MIGRATION: THE RECYCLE-BIN FLAG IS NOW CLIENT-SUPPLIED RATHER THAN FORCED. `:L364` set
 *   `objModule.IsDeleted = False` on EVERY update, so saving any field silently restored a module
 *   from the bin. Here the stored value is read from the module and sent back unchanged.
 *
 * MIGRATION: THE REMOVAL IS SOFT AND THERE IS NO RESTORE PATH. The legacy affordance called
 *   `DeleteTabModule(TabId, ModuleId)` (`ModuleSettings.ascx.vb:L305`, implemented at
 *   `ModuleController.vb:L837`) - a per-page removal that soft-deletes the module only once its last
 *   placement is gone - and NOT `DeleteModule` (`:L819`). The endpoint answers `204` and the row
 *   survives. The legacy bin screen lived under `Website/admin/Tabs/` and is out of scope, so a
 *   removed module simply stops appearing in listings.
 *
 * MIGRATION: D-M4 - THE LEGACY REMOVAL COMMENT IS WRONG ON BOTH COUNTS. The documentation block at
 *   `:L288-L292` states that the handler "deletes the current portal form the Database" and "can
 *   only run in Host (SuperUser) mode". It deletes a module placement, and it runs for any portal or
 *   page administrator. The defect is recorded, not reproduced.
 *
 * MIGRATION: D-M1 - THE LEGACY LABEL ASSOCIATION WAS ORPHANED. `modulesettings.ascx:L27` declares
 *   `controlname="lblFriendlyName"` while the control it labels is `txtFriendlyName` (`:L28`), and
 *   `Website/controls/labelcontrol.ascx` renders the `<label for>` from that name - so the rendered
 *   reference named nothing. A second instance sits at `:L35-L36`, where `plPermissions` names
 *   `ctlPermissions` while the grid is `dgPermissions` (`:L42`). The shared form-field component owns
 *   correct association, so the defect is fixed by construction; it is recorded here rather than
 *   presented as a like-for-like port.
 *
 * MIGRATION: D11 - THE LEGACY HELP AFFORDANCE WAS KEYBOARD-UNREACHABLE. `labelcontrol.ascx` is
 *   eleven lines long and places its help button INSIDE the `<label>` with `tabindex="-1"` and
 *   `CausesValidation="False"`, wrapping an image that also carries `tabindex="-1"` and no
 *   alternative text - so the affordance could not be tabbed to, and clicking it also focused the
 *   labelled control. The shared form-field component owns that affordance and is not
 *   re-implemented here. Its legacy `cssClass="Help"` is not carried forward either.
 *
 * MIGRATION: D-M5 - THE QUERY-STRING SPELLING IS NORMALISED TO ONE IDENTIFIER. This screen read
 *   `Request.QueryString("ModuleId")` (`:L448-L449`) while the sibling transfer screens read
 *   `"moduleid"`; case-insensitive lookup made those the same key in Visual Basic and would make them
 *   two different keys here. The target has exactly one canonical route parameter, `moduleId`.
 *
 * MIGRATION: NO VALIDATOR IS ADDED THAT THE LEGACY SCREEN DID NOT HAVE, AND THE TITLE IS THE TEST
 *   CASE. `modulesettings.ascx` declares ZERO presence validators and exactly four comparison
 *   validators, and both write contracts declare the title as nullable and not required. Adding a
 *   presence rule or a length rule to the title would be a parity break in the opposite direction,
 *   so neither is added. The documented column bounds - 256 for the title, 100 for the icon - are
 *   documentation, and the legacy title box carries no length attribute of any kind (`:L32`).
 *
 * MIGRATION: ONE OF THE FOUR LEGACY VALIDATORS IS LOST WITH ITS FIELD. `valBorder` (`:L138`) guarded
 *   the border width, which no module contract transports, so the rule has nothing to guard. Three
 *   survive: a parseable start date, a parseable end date and an integral cache period.
 *
 * MIGRATION: THERE IS NO DATE-ORDER RULE, and none is invented. The legacy comparison validators
 *   declare `Operator="DataTypeCheck"` with no control to compare against, so an end date before a
 *   start date was accepted, and it still is.
 *
 * MIGRATION: EVERY IMPLICIT COERCION IS MADE EXPLICIT, because the legacy code-behind compiled with
 *   `<compilation debug="false" strict="false">` (`Website/release.config:L125`) - Option Strict OFF.
 *   Four sites in this screen relied on it and each is handled explicitly below, with failure
 *   handled rather than thrown: the visibility enumeration assigned straight into an integer index
 *   (`:L134`), an identifier silently stringified for a list lookup (`:L145`), `Int32.Parse` rather
 *   than a try-parse on the free-text cache box (`:L349-L353`), and the culture-sensitive
 *   `Convert.ToDateTime` on the free-text date boxes (`:L367-L376`). A fifth site,
 *   `cboAlign.Items.FindByValue(objModule.Alignment).Selected = True` (`:L144`), is an unguarded
 *   dereference and therefore a latent failure - it is recorded here and produces no target code at
 *   all, because the alignment field is not transported.
 *
 * MIGRATION: THE OPERATOR'S TEXT IS NOT TRIMMED. `:L344` assigned `txtTitle.Text` with no trim, no
 *   length check and no presence check. That is preserved: emptied text becomes `null` so the column
 *   clears, which is what an emptied legacy text box did, but text the operator entered is
 *   transported exactly as entered. This is a deliberate divergence from the sibling settings
 *   screen's adapter, which trims before testing for emptiness.
 *
 * MIGRATION: RESOURCE WORDING IS TREATED AS UNTRUSTED TEXT. Two entries in this screen's own
 *   resource file carry markup - the inherit label carries bold tags and the help topic carries a
 *   heading and a paragraph - and across the in-scope resource files seventy-six values contain a
 *   tag, four of them a live script element. Every string this file holds is plain text, is bound as
 *   text, and the markup the legacy strings carried is dropped rather than rendered.
 *
 * MIGRATION: LOCALISATION IS NOT PORTED. The legacy resource-provider mechanism is a Web Forms
 *   feature with no counterpart here and no translation runtime is installed, so wording is authored
 *   directly. The resource file is the AUTHORITY for that wording - it disagrees with the markup in
 *   five places on this screen and the resource value wins every time - and it is read for wording
 *   only.
 *
 * MIGRATION: FIVE LEGACY OPERATIONS HAVE NO ROUTE AND THEREFORE NO AFFORDANCE HERE. The legacy
 *   handler called a module move (`:L403-L408`) and a copy or a bulk removal across every page
 *   (`:L411-L418`) directly after saving. None of those operations is exposed by the module API, so
 *   there is no move affordance, no reorder affordance, no copy, no pane placement and no
 *   all-pages cascade in this screen. Re-parenting still happens, because the page identifier is an
 *   ordinary member of the update body - but as part of the ordinary replacement, not as a dedicated
 *   operation.
 *
 * MIGRATION: RICH TEXT IS REDUCED TO PLAIN MULTI-LINE TEXT. The editor provider the legacy header
 *   and footer fields used is excluded, so both are plain multi-line controls.
 *
 * MIGRATION: THE FOUR FAR-REACHING SETTINGS ARE NOT LOCKED CLIENT-SIDE. `Page_Load:L215-L220`
 *   disabled the all-pages switch, the two portal-wide instructions and the page picker for a caller
 *   who was not a portal administrator, and `cmdUpdate_Click:L333-L338` repeated the lock. Nothing
 *   inside this screen's declared dependencies can answer whether the caller is a portal
 *   administrator: the identity contract exposes a SUPER-USER flag and a role list, neither of which
 *   is that question, and no accessor for the signed-in identity is available to this screen. Rather
 *   than invent a client-side authorisation rule or compare a role name against a literal, the
 *   refusal is left to the server, which answers `403`, and the refusal is presented at WARNING
 *   severity because that is what the legacy denial page used in both of its branches.
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

/**
 * The placement position that means "append at the bottom of the pane".
 *
 * MIGRATION: THIS IS AN INSTRUCTION, NOT AN ABSENCE MARKER, and the distinction is the reason it is
 *   named rather than written inline. The legacy constructor initialised the position to -1
 *   (`ModuleInfo.vb:L729`) and the add operation branched on it explicitly - "position module at
 *   bottom of pane" when it was -1, "position module in pane" otherwise (`ModuleController.vb`, in
 *   `AddModule`). Both write contracts default the member to the same value for the same reason.
 *   Position ZERO is an ordinary position, so this value is never used to test whether a position is
 *   known.
 */
const MODULE_ORDER_APPEND = -1;

/**
 * The definition-level cache period that means "caching does not apply to this definition".
 *
 * MIGRATION: A REAL STORED VALUE, AND A DIFFERENT FACT FROM A PLACEMENT'S OWN CACHE PERIOD. One
 *   legacy constructor initialised the two to different values on purpose - `_CacheTime = 0`
 *   (`ModuleInfo.vb:L731`) against `_DefaultCacheTime = -1` (`:L759`) - so zero on a placement means
 *   "do not cache this placement" while this value on a definition means "no default was declared".
 *   The legacy screen read the definition's value and, when it equalled the legacy integer marker,
 *   HID the entire cache row (`ModuleSettings.ascx.vb:L136-L142`). That behaviour is reproduced. The
 *   two values are never coalesced, merged or read as one another's fallback.
 */
const DEFAULT_CACHE_TIME_NOT_APPLICABLE = -1;

/**
 * The cache period a submission carries when the operator left the box empty.
 *
 * MIGRATION: EMPTY MEANT ZERO, NOT "UNSET". `ModuleSettings.ascx.vb:L349-L353` wrote zero when the
 *   box was empty and parsed the text otherwise. Zero is transported, and it means "do not cache".
 */
const CACHE_TIME_WHEN_BLANK = 0;

/**
 * The date the legacy null contract used to represent an unrecorded date, as it appears at the
 * start of an ISO 8601 instant.
 *
 * MIGRATION: THE LEGACY MARKER SURVIVES ON THE WIRE AND MUST NOT REACH A CONTROL.
 *   `Library/Components/Shared/Null.vb:L66-L70` defines the absent date as the minimum date, and the
 *   legacy screen guarded both date fields with a test against it so that the marker rendered as an
 *   EMPTY box (`ModuleSettings.ascx.vb:L152-L157`). Seeding blanks exactly this date and nothing
 *   else: the maximum date `9999-12-31` is an ordinary schedule value and is preserved.
 */
const UNRECORDED_DATE = '0001-01-01';

/** The number of characters at the start of an ISO 8601 instant that spell the calendar date. */
const ISO_DATE_LENGTH = 10;

// =====================================================================================================
// ROUTES THIS SCREEN NAVIGATES TO
// =====================================================================================================

/**
 * Where the screen returns after a save, a removal or a cancellation.
 *
 * MIGRATION: THE LEGACY SCREEN LEFT ON ALL THREE OUTCOMES. Its update handler ended with
 *   `Response.Redirect(NavigateURL(), True)` (`ModuleSettings.ascx.vb:L421`), and its cancel (`:L281`)
 *   and removal (`:L307`) handlers did the same, each returning to the administration page the
 *   operator came from. The module listing is that page's counterpart, so all three outcomes lead
 *   here. A literal path is used rather than an import from a sibling feature, because a feature
 *   folder never reaches into another one.
 */
const MODULE_LIST_PATH = '/modules';

// =====================================================================================================
// WORDING
// =====================================================================================================
//
// Every string below is taken from the VALUE of a resource entry, never from a markup attribute. The
// two disagree in five places on this screen and the resource file is the authority in all five:
// the advanced-settings heading reads 'Advanced Settings' where the markup reads 'Security Settings';
// the page picker reads 'Move To Page:' where the markup reads 'Move To Tab:'; the cache label reads
// 'Cache Time (secs):' where the markup reads 'Cache Timeout (seconds):'; the container switch reads
// 'Display Container?' where the markup reads 'Display Title?'; and the appearance heading duplicates
// the basic-settings heading rather than reading 'Appearance'.
//
// They are declared as constants rather than written into the template because Angular compiles
// templates with whitespace preservation disabled, which collapses every run of whitespace inside a
// text node to a single space. Four of the strings below deliberately carry TWO spaces after a
// sentence period, so authoring them as template text would silently rewrite wording an existing
// operator recognises. Interpolated values are not collapsed.

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
 * `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 *
 * The inherit label is the one entry whose legacy value carried markup - bold tags around two of its
 * words - and it is reproduced as PLAIN TEXT. The emphasis is lost; the string cannot become an
 * injection vector.
 */
const FIELD_LABELS: Readonly<Record<ModuleFormField, string>> = Object.freeze({
  // `plFriendlyName.Text`. Used for the create-mode definition selector as well, because the legacy
  // screen showed the definition under exactly this caption.
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
 * The supporting text for each field, from the `pl*.Help` entries of the same resource file.
 *
 * Two entries are net additions and are marked as such at their declaration, because the legacy
 * resource file has no wording for a create-mode definition selector: it never had one.
 */
const FIELD_HINTS: Readonly<Record<ModuleFormField, string>> = Object.freeze({
  // NET ADDITION. `plFriendlyName.Help` reads 'Displays the name of the module.', which describes a
  // read-only display rather than a choice, so it cannot serve the create-mode selector. Recorded as
  // a documented addition rather than reused misleadingly.
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

/**
 * The heading shown when an existing module is being edited, from `ModuleSettings.Text`.
 */
const EDIT_HEADING = 'Module Settings';

/**
 * The heading shown when a module is being placed for the first time.
 *
 * MIGRATION: A NET ADDITION, because the legacy resource file supplies no create-mode title. It
 *   carries mode-dependent titles - `ControlTitle_module.Text` reads 'Module' and the sibling
 *   transfer screens read 'Export Module' and 'Import Module' - but no create wording exists to
 *   carry over, the legacy screen having had no create path.
 */
const CREATE_HEADING = 'Add Module';

/**
 * The explanatory sentence beneath the heading, from `ModuleSettingsHelp.Text`, reproduced verbatim
 * including its irregular spacing before the closing bracket.
 */
const EDIT_SUBHEADING =
  'In this section, you can define the settings that relate to the Module content and permissions '
  + '(ie. those settings that will be the same on all pages that the Module appears ).';

/**
 * The explanatory sentence for create mode.
 *
 * A net addition for the same reason as {@link CREATE_HEADING}.
 */
const CREATE_SUBHEADING =
  'Choose a module and the page to place it on. The remaining settings can be changed afterwards.';

/** The validation message for an unparseable start date, from `valStartDate.ErrorMessage`. */
const START_DATE_INVALID_MESSAGE = 'Invalid Start Date';

/** The validation message for an unparseable end date, from `valEndDate.ErrorMessage`. */
const END_DATE_INVALID_MESSAGE = 'Invalid End Date';

/** The validation message for a non-integral cache period, from `valCacheTime.ErrorMessage`. */
const CACHE_TIME_INVALID_MESSAGE = 'Invalid Cache Time';

/**
 * The longest module heading the contract accepts, in characters.
 *
 * MIGRATION: THE AUTHORITY IS THE COLUMN AND THE API RULE, BECAUSE THE MARKUP HAS NONE.
 * `modulesettings.ascx` declares `maxlength` on the cache-period box and on the two date boxes and
 * NOTHING on the heading, so the legacy screen accepted text of any length and let the write refuse
 * it — and the terminal column is `ModuleTitle nvarchar(256)`, which the create and update rules cap
 * at the same figure. Refusing the overflow beside the box changes the mechanism, not the outcome.
 */
const MODULE_TITLE_MAX_LENGTH = 256;

/**
 * The longest icon path the contract accepts, in characters.
 *
 * `IconFile nvarchar(100)`, again capped identically by the create and update rules, and again
 * unconstrained by the legacy markup: the legacy affordance was a file-and-folder picker rather than a
 * text box, so it had no length attribute to reproduce.
 */
const MODULE_ICON_MAX_LENGTH = 100;

/**
 * The opening of the blank-heading disclosure, up to the name itself.
 *
 * Split from its closing half so the definition's own name is interpolated between them rather than
 * concatenated into a sentence fragment, which keeps the name a value and the wording a constant. The same
 * two constants word the same disclosure on the settings screen.
 */
const TITLE_FALLBACK_PREFIX = 'With no heading, this module is listed as “';

/** The close of the blank-heading disclosure, after the name. @see TITLE_FALLBACK_PREFIX */
const TITLE_FALLBACK_SUFFIX = '”, the name of its module definition.';

/**
 * The earliest instant SQL Server's `datetime` can store, as a calendar date.
 *
 * The two schedule bounds land in `datetime` columns, whose domain begins on the first of January 1753
 * — a date that is not a limitation of this application but of the type, and the API refuses anything
 * outside it. Stating it here refuses an unstorable date beside the box rather than after a round trip,
 * which matters because a date control makes the mistake easy: a mistyped year is one keystroke.
 */
const SQL_DATETIME_MINIMUM_DATE = '1753-01-01';

/**
 * The last instant SQL Server's `datetime` can store, as a calendar date.
 *
 * The type's domain ends at 9999-12-31 23:59:59.997. Only the DATE is compared here, because these two
 * controls produce a bare calendar date and the final day is representable in full.
 */
const SQL_DATETIME_MAXIMUM_DATE = '9999-12-31';

/** The error key the schedule-bound representability rule reports. */
const UNSTORABLE_DATE_ERROR = 'unstorableDate';

/**
 * The wording for a date outside the storable range.
 *
 * AUTHORED rather than measured, because the legacy screen had no such rule and therefore no such
 * message: its comparison validator declared a type check alone, so an unstorable year passed the
 * screen and failed inside the write. The sentence names the boundary rather than the type, since the
 * type is not something an operator can be expected to know.
 */
const DATE_OUT_OF_RANGE_MESSAGE = `Enter a date between ${SQL_DATETIME_MINIMUM_DATE} and ${SQL_DATETIME_MAXIMUM_DATE}.`;

/** The label on the save affordance, from `cmdUpdate.Text` in the shared resource file. */
const SAVE_LABEL = 'Update';

/** The label on the abandon affordance, from `cmdCancel.Text`. */
const CANCEL_LABEL = 'Cancel';

/** The label on the removal affordance, from `cmdDelete.Text`. */
const DELETE_LABEL = 'Delete';

/**
 * The destructive confirmation's message, from `DeleteItem.Text` in the shared resource file.
 *
 * This is the exact string the legacy screen resolved: `ModuleSettings.ascx.vb:L205` passed the
 * lookup key `"DeleteItem"` - without the property suffix - to the shared resource reader and wired
 * the result to the removal affordance as a client-side confirmation.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * The sentence shown when the operator submits a form that still carries a validation message.
 *
 * A net addition. The legacy screen guarded its handler with `If Page.IsValid Then`
 * (`ModuleSettings.ascx.vb:L328`) and, when validation had failed, simply did nothing - the
 * per-field messages were the only feedback and there was no summary anywhere in the thirty-nine
 * administration screens. A silent non-response is worse than a sentence, so one is added.
 */
const FORM_INVALID_MESSAGE = 'Correct the highlighted fields and try again.';

/** The sentence shown when a submission names no module definition. */
const DEFINITION_REQUIRED_MESSAGE = 'Choose a module before saving.';

/** The sentence shown when a submission names no page. */
const PAGE_REQUIRED_MESSAGE = 'Choose a page before saving.';

/**
 * The sentence shown when an update is attempted before the module has been read.
 *
 * Load-bearing rather than defensive: the update replaces the whole row, so submitting before the
 * stored position and the stored recycle-bin flag are known would move the module to the bottom of
 * its pane and clear the flag as a side effect of saving an unrelated field.
 */
const NOT_LOADED_MESSAGE = 'The module has not finished loading. Wait a moment and try again.';

/**
 * The sentence shown when the address carries a module identifier that cannot be read.
 *
 * MIGRATION: the legacy screen called `Int32.Parse` on the query-string value (`:L449`), which THREW
 *   for anything non-numeric and surfaced through the page's exception handler. Parsing explicitly
 *   and reporting the address is the same outcome without the exception, and it is emphatically not
 *   the same as treating the parameter as absent: an unreadable identifier must never fall through to
 *   the create screen and post a new module.
 */
const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a module that can be read. Return to the module list and try again.';

/** The sentence shown when the addressed module could not be found. */
const NOT_FOUND_MESSAGE = 'The module could not be found. It may have been removed.';

/**
 * The one status that answers the question of existence in the negative.
 *
 * Every other failed read leaves existence unanswered, which is why this constant is compared against
 * rather than a refusal status being listed: see {@link ModuleFormComponent.readRefused}.
 */
const NOT_FOUND_STATUS = 404;

/** The sentence shown after a module has been placed. */
const CREATED_MESSAGE = 'The module was added.';

/** The sentence shown after a module has been saved. */
const UPDATED_MESSAGE = 'The module settings were saved.';

/** The sentence shown after a module has been removed from its page. */
const DELETED_MESSAGE = 'The module was removed from the page.';

/**
 * The sentence shown when leaving the screen fails.
 *
 * A net addition with no legacy counterpart: the legacy screen left by way of a server-side redirect,
 * which either happened or replaced the response entirely, so there was no failure to report.
 */
const NAVIGATION_FAILED_MESSAGE = 'The module list could not be opened.';

// =====================================================================================================
// THE VISIBILITY CHOICES
// =====================================================================================================

/**
 * One choice offered by the visibility control.
 *
 * Declared HERE rather than imported. The shared option type in the model folder is not among this
 * screen's declared dependencies, and a feature never reaches into a sibling feature for a type, so
 * the two members this control needs are declared locally.
 */
export interface ModuleVisibilityChoice {
  /** The code written into the bound control. Bound by value, never by position. */
  readonly value: ModuleVisibility;

  /** The text shown to the operator. */
  readonly label: string;
}

/**
 * The three visibility choices, in the order the legacy radio group listed them.
 *
 * MIGRATION: BOUND THROUGH THE ENUMERATION, NEVER THROUGH A POSITION. The legacy screen assigned the
 *   stored code straight into the group's SELECTED INDEX (`ModuleSettings.ascx.vb:L134`), which
 *   worked only because the three list items happened to be declared in the same order as the
 *   enumeration members - and the legacy enumeration declared no explicit values at all
 *   (`ModuleInfo.vb:L30-L34`), so its numbering came from declaration order too. Two coincidences
 *   stacked on one another. Here each choice carries its code explicitly, the codes are written out
 *   on the enumeration, and nothing depends on the order of this array.
 *
 * MIGRATION: `None` IS A CHOICE, NOT AN ABSENCE. It means the placement renders without its
 *   container chrome. The wording of all three comes from the shared resource file.
 */
const VISIBILITY_CHOICES: readonly ModuleVisibilityChoice[] = Object.freeze([
  { value: ModuleVisibility.Maximized, label: 'Maximized' },
  { value: ModuleVisibility.Minimized, label: 'Minimized' },
  { value: ModuleVisibility.None, label: 'None' },
]);

// =====================================================================================================
// THE FORM SHAPE
// =====================================================================================================

/**
 * The typed shape of this screen's form.
 *
 * Declared LOCALLY and never shared. Each screen in this feature declares its own form model, because
 * a screen's shape is not a wire shape and two screens that edit overlapping fields still answer to
 * different routes, different modes and different affordances. Nothing here is imported from a
 * sibling screen and nothing here is exported to one.
 *
 * Sixteen controls: the fourteen members of the create contract, plus the two portal-wide
 * instructions that only the update contract carries. The recycle-bin flag is deliberately NOT a
 * control - it is state the operator never chooses, so it is read from the loaded module at
 * submission time rather than round-tripped through a form the operator can reach.
 *
 * THE TEXT-VALUED NUMERIC CONTROLS ARE DELIBERATE. The cache period is declared as text rather than
 * as a number so that "nothing entered" survives as the empty string, which is the state the legacy
 * screen translated to zero (`ModuleSettings.ascx.vb:L349-L353`); a numeric control cannot hold that
 * state, because an emptied numeric input reports `null` and would break a control declared
 * non-nullable over a number. The two dates are text for the same reason, and because the wire
 * carries them as ISO 8601 strings rather than as instants.
 *
 * THE TWO IDENTIFIER CONTROLS ARE NULLABLE, AND THAT IS THE ONLY HONEST SPELLING. Both are required
 * structurally, and the check has to be EXISTENCE rather than a bound: `dbo.Tabs.TabID` is
 * `IDENTITY(0, 1)`, so page zero is a legitimate page, and no comparison against zero, against -1 or
 * against truthiness can tell a real identifier apart from an unmade choice. `null` is the unmade
 * choice, and it is the only value in this file that means "not chosen".
 *
 * Every control is constructed non-nullable, so the group's value is fully typed rather than a
 * partial, and resetting a control returns it to its declared initial value instead of to `null`.
 */
export interface ModuleFormModel {
  /**
   * The definition to instantiate.
   *
   * Editable while placing a module and DISABLED once one exists, because the update contract carries
   * no definition member: a module's definition is fixed at creation.
   */
  moduleDefId: FormControl<number | null>;

  /**
   * The page the placement should sit on: where it is going, not where it is.
   *
   * The legacy caption was a move affordance and it still is one. The update body carries TWO page
   * identifiers - `tabId` selects the placement being replaced, `moveToTabId` names the destination -
   * so this control feeds the destination and the page the module was loaded from feeds the selector.
   * Creating a module the two are the same value, because a new placement's destination IS its page.
   */
  tabId: FormControl<number | null>;

  /** The operator's title for this module. Not required, and not length-checked. */
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

  /** Whether the module takes its view permission from its page. A flag only. */
  inheritViewPermissions: FormControl<boolean>;

  /**
   * The placement's position within its pane.
   *
   * Carried rather than edited: there is no reorder affordance, and the update replaces the whole
   * row, so the stored position must be sent back or the module is appended to the bottom of its
   * pane.
   */
  moduleOrder: FormControl<number>;

  /** How long the placement's output may be cached, in seconds, as entered. */
  cacheTime: FormControl<string>;

  /** The icon shown with the module's title. Not required, and not length-checked. */
  iconFile: FormControl<string>;

  /** How the placement is presented on its page. */
  visibility: FormControl<ModuleVisibility>;

  /** Whether the placement's container is displayed. Governs the container, not the title alone. */
  displayTitle: FormControl<boolean>;

  /**
   * The instruction to adopt these settings as the portal's defaults for newly added modules.
   *
   * An instruction rather than state, so it is seeded UNSET on every visit and never echoed back from
   * the loaded module: echoing a previous instruction would reapply it on the next submission.
   */
  setAsDefaultSettings: FormControl<boolean>;

  /**
   * The instruction to copy this placement's appearance to every module in the portal.
   *
   * The most far-reaching member of the module write surface. Seeded unset for the same reason as
   * {@link ModuleFormModel.setAsDefaultSettings}.
   */
  applyToAllModules: FormControl<boolean>;
}

/**
 * The form's value with disabled controls included.
 *
 * Derived FROM the form's own declared shape rather than restated, so adding a control without
 * deciding whether it is transported is a compile error at both submission sites instead of a value
 * that is silently never sent. The raw shape is the right one: an ordinary read omits disabled
 * controls, and the definition control is disabled whenever a module is loaded.
 */
type ModuleFormValue = ReturnType<FormGroup<ModuleFormModel>['getRawValue']>;

/**
 * The three store commands this screen dispatches.
 *
 * Narrowed FROM the store's own operation union rather than spelled independently, so a rename on the
 * store is a compile error here instead of an outcome that is silently never recognised.
 */
type ModuleFormOperation = Extract<
  ModuleStoreOperation,
  'createModule' | 'updateModule' | 'deleteModule'
>;

// =====================================================================================================
// PURE HELPERS
// =====================================================================================================
//
// Free functions rather than methods: none reads a signal or touches the form, so keeping them outside
// the class states that fact structurally and lets each be reasoned about on its own. Every one of them
// replaces a legacy coercion that the Option-Strict-off compiler performed implicitly.

/** An optional sign followed by one or more digits, and nothing else. */
const INTEGER_PATTERN = /^[+-]?\d+$/;

/** A four-digit year, a two-digit month and a two-digit day, at the start of the value. */
const ISO_DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/;

/** How many characters the leading `yyyy-mm-dd` of an ISO 8601 value occupies. */
const CALENDAR_DATE_LENGTH = 10;

/**
 * Reads the module identifier the router supplied.
 *
 * MIGRATION: THE LEGACY PARSE THREW. `ModuleSettings.ascx.vb:L449` called `Int32.Parse` on the
 *   query-string value with no guard, so a mistyped address raised an exception that the page's
 *   handler absorbed. This reads the value explicitly and reports failure as `undefined`, which the
 *   caller distinguishes from an ABSENT parameter by testing the raw input separately - the two
 *   states must never merge, because an unreadable identifier that read as "absent" would present
 *   the create screen and place a new module.
 *
 * The grammar is strict on purpose: an optional sign followed by digits, and nothing else. A value
 * such as `7px`, `7.0` or `0x7` is not an identifier, and `Number.parseInt` would happily return 7
 * for the first two of those.
 *
 * No bound is applied. Module zero is real and the value is opaque, so the only question asked is
 * whether the text spells an integer this runtime can hold exactly.
 *
 * @param supplied The route parameter as the router delivered it, or `undefined` when the matched
 * route declares none.
 * @returns The identifier, or `undefined` when none was supplied or the value could not be read.
 */
function parseModuleId(supplied: string | undefined): number | undefined {
  if (supplied === undefined) {
    return undefined;
  }

  const parsed = parseIntegerText(supplied);

  // The two spellings of "nothing" are converted explicitly rather than coalesced, because the
  // difference matters one level up: `null` here means the text could not be read, and the caller
  // distinguishes that from an absent parameter by testing the raw input separately.
  return parsed === null ? undefined : parsed;
}

/**
 * Reads an integer from text, reporting failure rather than raising it.
 *
 * MIGRATION: THE LEGACY CACHE PARSE THREW TOO. `ModuleSettings.ascx.vb:L350` called `Int32.Parse` on
 *   a free-text box whose only guard was a client-side comparison validator, so a submission that
 *   reached the server with unparseable text raised an exception. Failure is a return value here.
 *
 * @param text The text to read. Surrounding whitespace is tolerated because a form control reports
 * exactly what was typed.
 * @returns The integer, or `null` when the text does not spell one exactly.
 */
function parseIntegerText(text: string): number | null {
  const trimmed = text.trim();

  if (INTEGER_PATTERN.test(trimmed) === false) {
    return null;
  }

  const parsed = Number.parseInt(trimmed, 10);

  // `parseInt` returns a value for text longer than this runtime can represent exactly, so the
  // result is checked rather than trusted. An identifier that lost precision is a different
  // identifier.
  return Number.isSafeInteger(parsed) ? parsed : null;
}

/**
 * Whether text begins with a calendar date that actually exists.
 *
 * MIGRATION: THE LEGACY CHECK WAS CULTURE-SENSITIVE, TWICE OVER. The markup declared a comparison
 *   validator with `Operator="DataTypeCheck" Type="Date"` (`modulesettings.ascx:L78` and `:L88`),
 *   which resolved against the request's culture, and the handler then called
 *   `Convert.ToDateTime` (`ModuleSettings.ascx.vb:L368` and `:L373`), which resolved against the
 *   server's. The two could disagree about the same text. This reads one unambiguous form - the
 *   calendar date at the head of an ISO 8601 value, which is what a date control produces and what
 *   the wire carries - so there is no culture to disagree about.
 *
 * The components are validated by ROUND TRIP rather than by range, so that the thirtieth of February
 * is rejected rather than rolled forward into March. The year is applied through the four-digit
 * setter because the two-argument date constructor maps years below one hundred into the twentieth
 * century, which would silently accept `0001-01-01` as `1901-01-01`.
 *
 * Everything after the date is ignored: a full instant is a legitimate value for these fields and the
 * time portion is not this screen's business.
 *
 * @param text The text to inspect.
 * @returns `true` when the leading ten characters spell a real calendar date.
 */
function startsWithCalendarDate(text: string): boolean {
  const matched = ISO_DATE_PATTERN.exec(text);

  if (matched === null) {
    return false;
  }

  // Every group is present whenever the pattern matched, because all three are mandatory in it, so
  // the indexed reads below are total. Their results are still checked for integrality rather than
  // assumed, which is what keeps this function free of an assertion.
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
 * Narrows a stored instant to the calendar date a date control holds.
 *
 * The value is SLICED, never parsed into an instant and reformatted. Constructing a date from the
 * stored value and formatting it locally shifts it into the browser's zone and moves it by a day
 * either side of midnight, so a module scheduled to appear on the first of a month would be shown -
 * and written back - as the last day of the previous one.
 *
 * MIGRATION: THE LEGACY ABSENT-DATE MARKER RENDERS AS AN EMPTY CONTROL, which is what
 *   `ModuleSettings.ascx.vb:L152-L157` achieved by guarding each assignment with a test against it.
 *   Exactly that one date is blanked. The maximum date is an ordinary schedule value and survives.
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
 * Projects a text control's value onto a nullable wire member.
 *
 * MIGRATION: EMPTIED TEXT CLEARS THE COLUMN, AND ENTERED TEXT IS TRANSPORTED EXACTLY AS ENTERED. The
 *   legacy null contract encoded absent text AS the empty string
 *   (`Library/Components/Shared/Null.vb:L71-L75` returns a bare pair of quotes), and an emptied legacy
 *   text box posted an empty value that cleared its column; the wire member is nullable, so the empty
 *   string becomes `null` and clears the same column.
 *
 * MIGRATION: NOTHING IS TRIMMED, and that is a deliberate divergence from the sibling settings
 *   screen's adapter. `ModuleSettings.ascx.vb:L344` assigned `txtTitle.Text` with no trim, no length
 *   check and no presence check, so whitespace the operator entered was stored. Trimming here would
 *   change what is stored for a value the legacy screen accepted.
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
 * The empty value is VALID: no date means no schedule restriction, and the legacy comparison
 * validators likewise passed an empty box - they declared a type check, not a presence check, and the
 * screen declares no presence validator anywhere.
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

  // Representability, reported separately from readability so that each condition gets its own
  // sentence: "invalid" and "outside the storable range" are different mistakes and a person who typed
  // the year 1066 correctly has not typed something unreadable.
  //
  // Compared as TEXT rather than as instants, which is exact here and avoids a conversion: the leading
  // ten characters are a zero-padded `yyyy-mm-dd`, and that form sorts lexicographically in
  // chronological order. Constructing dates to compare them would reintroduce the zone shift the
  // reader above exists to avoid.
  const date = value.slice(0, CALENDAR_DATE_LENGTH);

  if (date < SQL_DATETIME_MINIMUM_DATE || date > SQL_DATETIME_MAXIMUM_DATE) {
    return { [UNSTORABLE_DATE_ERROR]: true };
  }

  return null;
}

/**
 * Rejects a cache-period control's value when it does not spell an integer.
 *
 * The empty value is VALID and means zero, which is what `ModuleSettings.ascx.vb:L349-L353` wrote.
 * The legacy rule was a type check with no bound - no minimum, no maximum - so none is imposed here
 * either, and in particular no non-negative rule is added: the server applies its own.
 *
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

/**
 * The message each field shows for its own local rule, keyed by control name.
 *
 * Only the three fields that carry a rule appear. The wording is the resource value with its leading
 * break markup dropped, because these strings are rendered as text: twenty-eight of the thirty-four
 * genuine legacy validator messages were prefixed with a break tag so that they wrapped beneath the
 * control they belonged to, which is layout expressed as content and is a stylesheet's job here.
 */
const LOCAL_VALIDATION_MESSAGES: Readonly<Partial<Record<keyof ModuleFormModel, string>>> =
  Object.freeze({
    // The two required choices. Deliberately the SAME two sentences the imperative guards already
    // report, rather than field-specific rewordings: one rule must not be described two ways depending
    // on which mechanism happens to notice it first.
    moduleDefId: DEFINITION_REQUIRED_MESSAGE,
    tabId: PAGE_REQUIRED_MESSAGE,
    startDate: START_DATE_INVALID_MESSAGE,
    endDate: END_DATE_INVALID_MESSAGE,
    cacheTime: CACHE_TIME_INVALID_MESSAGE,
  });

/**
 * The sentence to report for each completed command.
 *
 * Keyed by the store's own operation names, so the three entries cannot drift from the three commands
 * this screen dispatches.
 */
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
  // The typed form, plus the five shared primitives the paired template needs: the page heading, a
  // labelled region for every control, the indicator shown while the module is read, the problem
  // surface for a refusal, and the destructive confirmation. No sixth is added and none is authored:
  // the shared inventory is closed.
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
  // ---------------------------------------------------------------------------------------------------
  // DEPENDENCIES
  // ---------------------------------------------------------------------------------------------------
  //
  // Injected, never provided: this component declares no provider of its own, because every provider in
  // the application is registered once at application configuration.
  //
  // The transport layer is reached ONLY through the store. Sequencing one request after another is
  // composition and belongs there - the transport is confined to one request per method and performs no
  // orchestration - so nothing here holds an HTTP client, builds a path, assembles a query string or
  // sets a header. The correlation identifier every request carries is applied by an interceptor.

  /** The module feature's signal store: the single source of every value this screen renders. */
  private readonly store = inject(ModuleStore);

  /**
   * The signed-in session, read for ONE thing: which tenant a newly placed module belongs to.
   *
   * ⚠ THE CREATE ROUTE HAS NO OTHER SOURCE FOR IT, and that is why this dependency exists. The page
   * picker's options are portal-scoped, and on the edit route the portal is named by the module that was
   * read. On the create route there is no module to read, so before this was injected the picker stayed
   * empty for ever, the required page could never be chosen, and every attempt to place a module was
   * refused by this screen's own guard — creation was unreachable through the user interface.
   *
   * The session is the correct source rather than a convenience: the API resolves the tenant of a
   * request from the caller's own context, so the portal the caller is signed in to IS the portal a
   * created module will belong to. Reading it here makes the picker agree with the server instead of
   * guessing.
   */
  private readonly session = inject(AuthStore);

  /** The transient message queue, used for command outcomes. */
  private readonly notifications = inject(NotificationService);

  /** Used to leave the screen, exactly as the legacy handlers redirected. */
  private readonly router = inject(Router);

  // ---------------------------------------------------------------------------------------------------
  // THE ROUTE PARAMETER
  // ---------------------------------------------------------------------------------------------------

  /**
   * The module this screen addresses, or `undefined` on the create route.
   *
   * ⚠ THE NAME IS LOAD-BEARING AND MUST NOT BE RENAMED. Route parameters are delivered into component
   * inputs BY NAME, so a rename severs the binding silently: the screen would compile, bundle and
   * render, and would simply always behave as though no module had been addressed. The same spelling is
   * what the server's authorisation reads out of the route when it resolves the module a policy applies
   * to.
   *
   * OPTIONAL, because the create route declares no parameter at all and the router therefore never
   * writes this input. The initial value is what create mode is recognised by.
   *
   * DECLARED AS THE ROUTER'S OWN TYPE, which is text, and converted explicitly further down rather
   * than through a transform. That choice is deliberate and it is what keeps the two failure modes
   * apart: a transform to a number can report only ONE kind of nothing, so an address naming an
   * unreadable identifier would arrive indistinguishable from an address naming none - and would then
   * present the create screen and place a new module. Holding the raw value means presence and
   * readability are both derivable, and the explicit conversion is exactly what the legacy code's
   * Option-Strict-off coercion hid.
   */
  readonly moduleId = input<string | undefined>(undefined);

  // ---------------------------------------------------------------------------------------------------
  // THE FORM
  // ---------------------------------------------------------------------------------------------------
  //
  // Declared before every member that derives from it: class fields initialise in declaration order, so
  // a derivation placed above this one would read an uninitialised form.

  /**
   * The typed form backing every editable field.
   *
   * The initial value of each control IS the create-mode default, which is why create mode needs no
   * seeding step beyond a reset. Three of them are measured legacy defaults - the visibility is the
   * maximised state, the all-pages switch is off and the schedule is empty
   * (`ModuleSettings.ascx.vb:L225-L227`) - and two are the write contracts' own initialisers, which
   * exist precisely where the underlying value type's default differs from the legacy default: the
   * placement position appends, and the container is displayed.
   */
  protected readonly form = new FormGroup<ModuleFormModel>({
    // `null` is "not chosen". It is NOT a sentinel: page zero and module zero are real, so no numeric
    // value could have carried this meaning.
    //
    // ⚠ BOTH RULES WERE ALREADY BEING ENFORCED, IMPERATIVELY, AND THAT WAS THE DEFECT. `submit` tested
    // `tabId === null` and reported a page-level sentence, and `createModule` tested
    // `moduleDefId === null` and reported another - so `form.invalid` was FALSE with two required
    // choices unmade, no control carried `aria-invalid`, not one of the thirteen fields showed a
    // message, and an empty submission produced a single polite warning naming only the page. The
    // module was never mentioned at all, because its test sat downstream of three earlier guards and
    // was never reached.
    //
    // Declaring the rules here is a change of REPORTING, not a new rule: the requirement already
    // existed and already blocked the write. Once declared, the screen's existing machinery does the
    // rest - `markAllAsTouched` plus `errorsFor` renders a sentence against each control, the template
    // binds `aria-invalid` from the same source, and the single `form.invalid` branch reports the
    // summary. The imperative guards below stay as defence in depth, exactly as the cache-time guard
    // does.
    //
    // MIGRATION: the legacy screen declared no required rule on `cboTab` - `modulesettings.ascx`
    // carries four validators and all four are `CompareValidator`s for date and integer types - but it
    // could not need one: `ModuleSettings.ascx.vb:L129-L130` pre-selected the module's own tab,
    // `:L145` pre-selected a supplied one, and `:L211` INSERTED the active tab at position zero, so an
    // unchosen page was unreachable. There was also no legacy create screen at all. The situation is
    // reachable here, so the rule has to be stated where the rest of the rules live.
    //
    // `Validators.required` is safe on a numeric control: its emptiness test is `value == null ||
    // value.length === 0`, and `0 == null` is false while `(0).length` is undefined - so page zero and
    // module zero, both real identities, pass. Only `null` fails, which is precisely "not chosen".
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
    // SEC: the containment rule joins the length bound, and it is the SHARED rule rather than a local
    // copy. `modulesettings.ascx:L116` declared this field as `<portal:url id="ctlIcon" ...>`, a picker
    // over the portal's own files, so an arbitrary path was unreachable by construction and the legacy
    // screen had nothing to validate. This screen offers a text box instead, so the constraint the picker
    // enforced structurally is enforced by a rule - which refuses only values the legacy could not have
    // produced. Measured before it was added: `../../../etc/passwd` reached the API and was stored
    // VERBATIM, because the module validators were the one path still missing the rule.
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

  /**
   * Whether the operator has tried to save.
   *
   * Gates the per-field messages, because the legacy validators reported on the postback rather than
   * as the operator typed. A touched control also reports, which is a strictly earlier moment and not
   * a later one.
   */
  private readonly submitAttempted = signal(false);

  /** Whether the destructive confirmation is showing. */
  private readonly removalPending = signal(false);

  /** Whether a read of the addressed module has been dispatched. */
  private readonly loadRequested = signal(false);

  /**
   * The command awaiting its outcome, or `null` when none is in flight.
   *
   * A PLAIN FIELD rather than a signal, deliberately: it is read inside the outcome effect, and making
   * it reactive would re-run that effect when it is cleared, which is the one moment it must not run
   * again.
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
   * Whether the screen is editing an existing module.
   *
   * MIGRATION: DERIVED FROM WHETHER THE ADDRESS CARRIES A MODULE AT ALL, and never from the value it
   *   carries. The legacy test was `If ModuleId <> -1` (`ModuleSettings.ascx.vb:L222`) against a field
   *   initialised to -1 (`:L68`), which cannot survive the migration for two independent reasons: -1 is
   *   the legacy integer absence marker, and `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so zero is an
   *   ordinary module. This expression tests presence and nothing else.
   */
  protected readonly isEditMode = computed<boolean>(() => this.moduleId() !== undefined);

  /** Whether the screen is placing a new module. The exact complement of {@link isEditMode}. */
  protected readonly isCreateMode = computed<boolean>(() => this.moduleId() === undefined);

  /**
   * The addressed module's identifier, or `undefined` when the address names none or names one that
   * cannot be read.
   *
   * The two reasons for `undefined` are told apart by {@link addressUnreadable}, and every consumer
   * that could act destructively on the difference consults that first.
   */
  protected readonly moduleKey = computed<number | undefined>(() => parseModuleId(this.moduleId()));

  /**
   * Whether the address carries a module identifier that could not be read.
   *
   * MIGRATION: reported rather than thrown, and reported rather than ignored. The legacy parse raised
   *   an exception the page absorbed (`:L449`); falling through to create mode instead would be far
   *   worse than either, because a mistyped address would place a new module.
   */
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
   * The portal whose pages the picker should offer, or null when no portal is known yet.
   *
   * ONE MEMBER, TWO ANSWERS, AND THE ROUTE DECIDES WHICH. On the edit route the answer is the portal the
   * addressed module belongs to, because re-parenting must offer that portal's pages and no other. On the
   * create route there is no module to ask, so the answer is the portal the CALLER is signed in to —
   * which is the same portal the API will resolve the create request into, so the options the operator
   * chooses from are the options the server will accept.
   *
   * Null is returned rather than a fallback in three cases, each deliberate: an edit route whose module
   * has not been read yet, a host-owned module that belongs to no portal, and a session whose identity is
   * unresolved. A fallback would request the pages of a portal nobody named.
   *
   * ⚠ NO TRUTHINESS TEST APPEARS HERE OR IN THE EFFECT THAT READS IT. `dbo.Portals.PortalID` is
   * `IDENTITY(-1, 1)`, so both -1 and 0 are ordinary portal identifiers, and `if (portalId)` would
   * discard the tenant the measured baseline actually uses.
   */
  private readonly tabSourcePortalId: Signal<number | null> = computed(() => {
    if (this.isEditMode()) {
      return this.loadedModule()?.portalId ?? null;
    }

    return this.session.portalId();
  });

  /**
   * The problem document behind the current failure, or `null` when there is none.
   *
   * Passed WHOLE to the shared problem surface rather than reduced to a sentence here. That is what
   * retains the trace identifier, which the server derives from the ambient activity or the request
   * identifier and which the correlation value this application sends round-trips into: it is the only
   * join key between what a person saw and what the server logged, so it is never discarded. The
   * per-field dictionary travels with it, and the field wording is resolved through the shared
   * utility, which already owns the case-insensitive match against the server's own model-state
   * spelling and the removal of the legacy leading break markup.
   */
  protected readonly problem = computed<ProblemDetails | null>(() => {
    const failure = this.store.failure();

    return failure === null ? null : failure.problem;
  });

  /**
   * The module this screen addresses, once it has been read.
   *
   * MATCHED AGAINST THE ADDRESS, because the store is application-scoped and may still hold the module
   * a neighbouring screen read. Seeding a form from the wrong module would present one module's
   * settings under another's address, and - since the replacement is a whole-row one - would then write
   * them.
   */
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
   * Whether the read of the addressed module FAILED for a reason other than the module being absent.
   *
   * A REFUSAL IS NOT AN ABSENCE, and that distinction is the whole point of this member. The server
   * answers `GET /api/v1/modules/{id}` with 403 when the caller may not see the module, and with 404
   * when there is no such module; both leave this screen holding no module, so a test for "no module
   * in hand" cannot tell them apart. Reporting a refusal as "could not be found" states something
   * that is not true, and states it ALONGSIDE the banner's accurate sentence, so the screen
   * contradicts itself.
   *
   * The predicate is deliberately "any failed read except an absence" rather than "a 403", because
   * every other status carries the same defect for the same reason: a read that failed with a fault
   * did not answer the question of existence either. The one status that DOES answer it is 404, which
   * is left to {@link moduleMissing} and its ported sentence.
   *
   * Narrowed to the read, because the store holds one failure slot shared by every module command: a
   * rejected save must not make the form disappear.
   *
   * MIGRATION: the presentation of a refusal is the shared banner and nothing else, at warning
   *   severity. That is the faithful port of `Website/admin/Security/AccessDenied.ascx.vb:L41-L45`,
   *   which raised `ModuleMessage.ModuleMessageType.YellowWarning` on BOTH of its branches - a
   *   module message rendered in the page, not a transient one - and it is the severity
   *   `core/utils/form-errors.util.ts` already resolves 403 to. Keeping the document rather than
   *   reducing it to a sentence is what retains the trace identifier, which is the only join key
   *   between what the operator saw and what the server logged.
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

  /**
   * Whether the addressed module could not be found.
   *
   * Distinguished from "still reading" by the dispatch flag and the loading flag, so the message
   * appears only once a read has actually completed without producing the module - and distinguished
   * from "the read was refused" by {@link readRefused}, so the ported sentence is reserved for the one
   * condition it actually describes.
   */
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

  /**
   * The definition currently selected, tracked through the control rather than through a handler.
   *
   * Driven by the control's own value stream so that the derivations below hold however the paired
   * template binds it - a change the template made without calling a method on this class would leave a
   * hand-maintained mirror stale, and the cache row would then be governed by a definition the operator
   * had already replaced.
   */
  private readonly selectedDefinitionId: Signal<number | null> = toSignal(
    this.form.controls.moduleDefId.valueChanges,
    { initialValue: null },
  );

  /**
   * The selected definition, resolved from the catalogue or from a single read of it.
   *
   * `null` when nothing is selected or the selection is not among the definitions held. The catalogue
   * is consulted first because it is what the selector offers; the single-definition slice is the
   * fallback for a module whose definition is not in the tenant's catalogue.
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

  /**
   * Whether the cache-period control is offered.
   *
   * MIGRATION: REPRODUCES A HIDDEN ROW, NOT A DISABLED ONE. `ModuleSettings.ascx.vb:L136-L142` read the
   *   selected definition and, when its default cache period equalled the legacy integer absence
   *   marker, set `rowCache.Visible = False` - the whole row disappeared. The same rule is expressed
   *   here, against the definition's own value and never against the placement's: the two are separate
   *   facts and are not coalesced anywhere in this file.
   *
   * When no definition has resolved the control IS offered, which is the legacy outcome as well: the
   * legacy code reached the definition unconditionally and hid the row only on the explicit match.
   */
  protected readonly cacheTimeOffered = computed<boolean>(() => {
    const definition = this.selectedDefinition();

    return definition === null || definition.defaultCacheTime !== DEFAULT_CACHE_TIME_NOT_APPLICABLE;
  });

  /**
   * The definition's display name, shown read-only.
   *
   * MIGRATION: THREE DIFFERENT NAMES EXIST AND THIS IS THE DISPLAY ONE. The legacy class carried the
   *   definition's programmatic name (`ModuleInfo.vb:L437`), the definition's display name (`:L374`)
   *   and the instance title the operator types (`:L194`), and the legacy screen bound the DISPLAY name
   *   into the disabled box and the INSTANCE title into the editable one, in that order
   *   (`ModuleSettings.ascx.vb:L125-L126`). None of the three is interchangeable with another, and the
   *   programmatic name is never shown here.
   *
   * The loaded module's own projection wins over the catalogue's, exactly as the legacy assignment did,
   * so an empty stored name renders empty rather than being replaced by the catalogue's.
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

  /**
   * Whether the definition selector is offered.
   *
   * MIGRATION: OFFERED ONLY WHILE PLACING A MODULE. The replacement contract carries no definition
   *   member - measured at `ModuleController.vb:L645` against `:L1095`, ten provider values against
   *   nine, the definition being the one that is absent - so a module's definition is fixed at
   *   creation. The control is also DISABLED on the form when a module is loaded, so the value can
   *   never reach a submission even if a template rendered it anyway.
   */
  protected readonly definitionSelectable = computed<boolean>(() => this.isCreateMode());

  /**
   * Whether a removal affordance is offered.
   *
   * MIGRATION: NOT OFFERED WHILE PLACING A MODULE, which is `cmdDelete.Visible = False` at
   *   `ModuleSettings.ascx.vb:L227`. It also requires the module to have been read, because removing a
   *   single placement needs to name that placement.
   */
  protected readonly canDelete = computed<boolean>(() => this.loadedModule() !== null);

  /** Whether the destructive confirmation is showing. */
  protected readonly removalConfirmVisible = computed<boolean>(() => this.removalPending());

  /** Whether any request is in flight, which is what suppresses a second submission. */
  protected readonly busy = computed<boolean>(() => this.saving() || this.loading());

  /** The visible label for each field. */
  protected readonly labels = FIELD_LABELS;

  /** The supporting text for each field. */
  protected readonly hints = FIELD_HINTS;

  /** @see MODULE_TITLE_MAX_LENGTH — bound to the heading box's native attribute. */
  protected readonly titleMaxLength = MODULE_TITLE_MAX_LENGTH;

  /** @see MODULE_ICON_MAX_LENGTH — bound to the icon box's native attribute. */
  protected readonly iconMaxLength = MODULE_ICON_MAX_LENGTH;

  /** @see SQL_DATETIME_MINIMUM_DATE — bound to both date pickers' native lower bound. */
  protected readonly dateMinimum = SQL_DATETIME_MINIMUM_DATE;

  /** @see SQL_DATETIME_MAXIMUM_DATE — bound to both date pickers' native upper bound. */
  protected readonly dateMaximum = SQL_DATETIME_MAXIMUM_DATE;

  /**
   * What the operator is told when the end of the schedule precedes its start.
   *
   * MIGRATION: AUTHORED, because the legacy screen had nothing to say about this state — it could not
   * detect it. `Website/admin/Modules/modulesettings.ascx` guards each date with an independent
   * `DataTypeCheck` comparison validator naming NO control to compare against, so the relationship
   * between the two fields was never expressed on either tier.
   *
   * The wording states the consequence rather than scolding, because the value is ACCEPTED: it says
   * what the module will do, which is the fact the operator needs in order to decide whether they
   * meant it.
   */
  protected readonly reversedScheduleNotice =
    'The end of this schedule is earlier than its start, so the module will not be shown at any ' +
    'time. This is saved as entered.';

  /**
   * What the module will be listed as while its heading is blank, or `null` when the heading is set.
   *
   * A `computed` here rather than a method — unlike {@link scheduleReversed}, which must read a form
   * control — because its one changing dependency, the definition's name, is already a signal. It still
   * has to read the heading control, so it is written as a method-free getter over that signal and the
   * control together; the control is read inside the same change-detection pass that renders it, which is
   * the same arrangement the reversed-schedule notice relies on and which was verified at runtime there.
   *
   * @see titleFallbackNotice on the settings screen for the full sourcing: three tiers agree that the
   * heading is optional, and this states the consequence rather than refusing the value.
   */
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
   * Whether the entered schedule ends before it begins.
   *
   * A METHOD rather than a `computed`, deliberately, and the reason is that this form's state lives
   * in a `FormGroup` and not in signals: nothing here bridges `valueChanges` into a signal, so a
   * `computed` would have nothing reactive to depend on and would latch its first answer forever.
   * Reading the controls directly is also what guarantees the notice describes the two values that
   * are actually drawn rather than a second copy of them that could disagree.
   *
   * This re-evaluates on every change-detection pass for this component, which is exactly when it
   * needs to: the component is `OnPush`, and an `input` event raised by a control inside this
   * template marks the view dirty, so typing in either date box re-runs the check. The same
   * arrangement backs the role form's at-limit notice and was verified at runtime there rather than
   * assumed to work.
   *
   * The controls are native `type="date"` inputs, so their values are ISO `yyyy-mm-dd` strings, which
   * would compare correctly as strings for equal-length values — but they are compared as DATES here
   * anyway, because a blank or partially typed value must not be mistaken for an ordering.
   *
   * Both bounds must be present and valid for the comparison to mean anything: a schedule with only
   * one bound is open-ended and ordinary, and is never remarked on.
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
    // The definition catalogue backs the create-mode selector AND the cache-row rule in both modes, so
    // it is read once on arrival. The store owns the request; nothing is cached here, because the
    // catalogue endpoint takes no parameter and the target's caching is a server concern behind an
    // injected abstraction rather than the 317-line client-side cache the legacy code reached from 116
    // sites. This call also clears any failure a previous screen left in the shared store.
    this.store.loadDefinitions();

    // ---------------------------------------------------------------------------------------------
    // Reacting to the address.
    // ---------------------------------------------------------------------------------------------
    // A genuine side effect - it dispatches a request - which is the only thing an effect is used for
    // in this file. Signal writes inside an effect are permitted by the installed framework version;
    // the store commands below write their own loading slices.
    effect(() => {
      const editing = this.isEditMode();
      const key = this.moduleKey();

      if (editing === false) {
        // MIGRATION: the create defaults are the form's declared initial values, so entering create
        //   mode is a reset rather than an assignment sequence. The legacy equivalent is
        //   `ModuleSettings.ascx.vb:L225-L227`: the visibility list selected its first entry, the
        //   all-pages switch was cleared and the removal affordance was hidden.
        untracked(() => this.enterCreateMode());

        return;
      }

      if (key === undefined) {
        // The address names something that is not an identifier. Nothing is dispatched and nothing is
        // reset: the view reports the address instead. Falling through to either branch would be wrong
        // - a read would be a request for a module that cannot exist, and a reset would offer to
        // create one.
        return;
      }

      if (this.hasDispatchedKey && this.dispatchedKey === key) {
        return;
      }

      this.hasDispatchedKey = true;
      this.dispatchedKey = key;

      untracked(() => {
        this.loadRequested.set(true);
        // MIGRATION: EDIT MODE READS BEFORE IT WRITES, AND THAT IS MANDATORY RATHER THAN TIDY. The
        //   replacement is a whole-row one, so the stored placement position and the stored
        //   recycle-bin flag have to be in hand before any submission: omitting the position appends
        //   the module to the bottom of its pane and omitting the flag clears it.
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

    // ---------------------------------------------------------------------------------------------
    // Reading the portal's pages, from whichever source names the portal.
    // ---------------------------------------------------------------------------------------------
    // The page picker's options are a portal-scoped list, and WHICH portal is answered differently by
    // the two routes:
    //
    //   - on the edit route, by the module that was read, because a module names the portal it belongs
    //     to and re-parenting must offer that portal's pages;
    //   - on the create route, by the SIGNED-IN SESSION, because there is no module yet and the API
    //     resolves the tenant of a create request from the caller's own context — so the caller's
    //     portal is the portal the new module will belong to.
    //
    // ⚠ THE CREATE ARM IS THE FIX FOR A SCREEN THAT COULD NOT COMPLETE ITS OWN PURPOSE. This effect
    // previously returned as soon as the module read was empty, which on the create route is always, so
    // the picker had no options, the required page could never be chosen and this screen's own guard
    // refused every submission. Nothing about that was visible in a type or in a build.
    //
    // A host-owned module reports no portal and an unresolved session answers null; in both cases no
    // list is requested, rather than one being requested for a guessed tenant.
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

    // ---------------------------------------------------------------------------------------------
    // Resolving the outcome of a dispatched command.
    // ---------------------------------------------------------------------------------------------
    // The store's commands report through signals rather than by returning anything, so completion is
    // observed as the in-flight flag falling while a command of this screen's is outstanding. The
    // failure slice is read WITHOUT subscribing to it, so that clearing the outstanding command cannot
    // re-enter this effect.
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

        // MIGRATION: THE SEVERITY IS THE SHARED UTILITY'S DECISION, NOT THIS SCREEN'S, and it is the
        //   measured one: a refusal is a WARNING rather than an error, because the legacy denial page
        //   presented one with a yellow warning in both of its branches, and presenting a refusal in
        //   danger styling would say something is broken when the system is working as configured. A
        //   conflict and a server fault remain errors. No status is examined here.
        // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT. The summary has carried a `supportReference` member all
        // along, and dropping it here threw away the only join key between what an operator saw in the
        // browser and the request as the server recorded it - the correlation identifier the server
        // validated, which is what appears on the response header, on the request envelope in its log and on
        // every audit event the request produced. A browser audit measured the asymmetry: a refusal presented
        // through the shared banner read `Reference: <id>`, while the same class of refusal presented as a
        // notification read nothing an operator could quote. The notification surface appends it AFTER its own
        // message bound, so a long server sentence cannot truncate the identifier away, and a document that
        // carried none resolves to null and is simply not quoted.
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
   * The messages to show beside one control: its own rule first, then the server's.
   *
   * A method rather than a derivation because a reactive form's validity is not a signal; it is
   * re-evaluated when the view is checked, which under this change-detection strategy happens on the
   * events the operator generates in this view.
   *
   * The local message appears only once the control has been touched or a save has been tried, which
   * matches the legacy timing: the comparison validators reported on the postback.
   *
   * The server's message is resolved through the shared utility, which owns the case-insensitive match
   * against the server's own model-state key - the server writes its .NET property names, so a control
   * named for the wire member matches without either side re-casing - the removal of the binder's path
   * prefixes, and the removal of the legacy leading break markup. None of that is repeated here.
   *
   * @param controlName The control to report for.
   * @returns The messages, in order. Empty when the control has none.
   */
  protected errorsFor(controlName: keyof ModuleFormModel): readonly string[] {
    const control: AbstractControl = this.form.controls[controlName];
    const messages: string[] = [];
    // ⚠ CHOSEN BY WHICH RULE FAILED, not by the control being invalid. Three controls now carry more
    // than one rule apiece - each date box is both readable and storable, and two text boxes have a
    // column bound - so a single sentence per control would describe an unstorable year as unreadable
    // and an over-long heading as nothing at all.
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
   * The wording for whichever local rule the control has broken.
   *
   * Ordered so that the more specific rule wins: an unreadable value is described as unreadable even
   * though it is also unstorable, because that is the mistake the person actually made.
   *
   * The length sentence is composed from the bound the framework REPORTS rather than from a table keyed
   * by control, so the sentence and the rule cannot name different numbers.
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

    // Ordered before the length arm so the more specific rule wins. A rooted or upward-traversing
    // reference that also happens to be over-long is described as escaping the folder, because that is
    // the mistake that was actually made - shortening it would not make it acceptable.
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

  /**
   * Saves the form: places a new module, or replaces the addressed one.
   *
   * Which of the two happens is decided by the ADDRESS and never by a value on the form, so a screen
   * reached without a module can only ever create and a screen reached with one can only ever replace.
   */
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
      // MIGRATION: the legacy handler was wrapped in `If Page.IsValid Then` (`:L328`) and did nothing
      //   at all when validation had failed. A sentence is reported instead; the per-field messages are
      //   unchanged.
      this.notifications.notify('warning', FORM_INVALID_MESSAGE);

      return;
    }

    const raw: ModuleFormValue = this.form.getRawValue();
    const tabId = raw.tabId;

    // MIGRATION: AN EXISTENCE TEST, NEVER A BOUND. `dbo.Tabs.TabID` is `IDENTITY(0, 1)`
    //   (`01.00.00.SqlDataProvider:L140`), so page zero is an ordinary page and no comparison against
    //   zero, against -1 or against truthiness could distinguish it from an unmade choice.
    if (tabId === null) {
      this.notifications.notify('warning', PAGE_REQUIRED_MESSAGE);

      return;
    }

    // MIGRATION: the legacy screen parsed this box with `Int32.Parse` (`:L350`), which threw, and wrote
    //   zero when the box was empty (`:L352`). The blank case is reproduced exactly; the parse reports
    //   failure instead of raising it. The control's own rule has already rejected unparseable text, so
    //   this branch is a second line of defence rather than the first.
    const cacheTime = this.resolveCacheTime(raw.cacheTime);

    if (cacheTime === null) {
      this.notifications.notify('warning', CACHE_TIME_INVALID_MESSAGE);

      return;
    }

    // THE PREVIOUS REFUSAL IS DISCARDED HERE, at the last point before a request is dispatched and after
    // every local guard has passed. This is the pattern the portal settings, portal alias, user form,
    // user list, membership settings and profile definition screens all already follow, and this screen
    // was the one that did not - so a refusal from an earlier attempt stayed on display through the next
    // one, describing a response that had been superseded. Measured: a 400 on the icon reference left its
    // banner, its correlation reference and its per-field message standing while the corrected submission
    // was in flight and after it had succeeded.
    //
    // Cleared HERE rather than on every keystroke, deliberately and for the same reason those screens do
    // it here: the banner reports the last answer the server gave and stays true until a new one arrives,
    // and it carries the correlation reference a reader may still be writing down. Wiping it the moment a
    // character is typed would take that away mid-sentence.
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
   * Opens the destructive confirmation.
   *
   * MIGRATION: the legacy screen attached a client-side confirmation to the affordance itself
   *   (`:L205`), so the operator saw the shared prompt before the postback. The shared dialog is that
   *   prompt, and it brings a focus trap and dismissal on the escape key with it - neither of which the
   *   legacy browser dialog offered through the page.
   */
  protected requestRemoval(): void {
    this.removalPending.set(true);
  }

  /** Dismisses the destructive confirmation without removing anything. */
  protected cancelRemoval(): void {
    this.removalPending.set(false);
  }

  /**
   * Removes this placement of the module.
   *
   * MIGRATION: A PER-PAGE REMOVAL, WHICH IS WHY THE PLACEMENT IS NAMED. The legacy affordance called
   *   `DeleteTabModule(TabId, ModuleId)` (`:L305`, implemented at `ModuleController.vb:L837`), which
   *   removes the module from ONE page and soft-deletes the module itself only once its last placement
   *   is gone - not `DeleteModule` (`:L819`), which removes it outright. Naming the placement is what
   *   reproduces that; omitting it would address the module everywhere it appears.
   *
   * MIGRATION: THE REMOVAL IS SOFT AND CANNOT BE REVERSED FROM HERE. The row survives with its
   *   recycle-bin flag set, the endpoint answers with no content, and no restore or purge route exists -
   *   the legacy bin screen lived under `Website/admin/Tabs/` and is out of scope. The listing is
   *   re-read by the store rather than spliced locally, because whether a removed module still appears
   *   is the listing's decision.
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

    // The same existence test as the page: `dbo.ModuleDefinitions.ModuleDefID` is `IDENTITY(1, 1)`, so
    // no legal value coincides with a legacy marker - but it is an opaque key and is still not compared
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
   * Replaces the addressed module, sending all sixteen members of the replacement contract.
   *
   * MIGRATION: THE MODULE MUST HAVE BEEN READ FIRST, AND THE SUBMISSION IS REFUSED OTHERWISE. Two
   *   members come from the stored row rather than from the operator - the placement position and the
   *   recycle-bin flag - and the replacement writes every member it is given. Submitting without them
   *   would append the module to the bottom of its pane and restore it from the bin as side effects of
   *   saving an unrelated field.
   *
   * MIGRATION: THE DEFINITION IS NOT SENT. The contract carries no definition member, so no expression
   *   here writes one, and the control it would have come from is disabled while a module is loaded.
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

    // THE PICKER IS A DESTINATION ON THIS PATH, NOT A SELECTOR, and the two roles have to be separated
    // because the same control serves both modes of this screen. Creating a module, the chosen page IS the
    // page the new placement goes on. Replacing one, the placement being replaced is the page the module was
    // LOADED from, and the chosen page is where the operator wants it to end up - so passing the choice as
    // the selector asks the server to update a placement on a page the module does not occupy, which it
    // answers `module.placement_not_found` while moving nothing.
    //
    // MIGRATION: the guard mirrors `ModuleSettings.ascx.vb:L405`, `If TabId <> newTabId`, so a save that
    // leaves the picker alone carries no relocation instruction rather than a self-cancelling one.
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
      // MIGRATION: THE STORED FLAG, NOT A FORCED `false`. `:L364` assigned `objModule.IsDeleted = False`
      //   on every single update, so saving any field silently restored a module from the recycle bin.
      //   The stored value is round-tripped instead.
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
   * Prepares the form for placing a new module.
   *
   * Resetting is what applies the create defaults, because every control's declared initial value IS
   * its create default. The definition control is enabled first: resetting a disabled control leaves it
   * disabled, and a module placed without a definition cannot be created.
   *
   * The store's loaded-module slice is deliberately NOT cleared. It is application-scoped state that a
   * neighbouring screen may be showing, and nothing here needs it cleared: every read of it in this
   * file is matched against the address first, so a module loaded elsewhere cannot leak into this form.
   */
  private enterCreateMode(): void {
    this.form.controls.moduleDefId.enable({ emitEvent: false });
    this.form.reset();
    this.submitAttempted.set(false);
    this.removalPending.set(false);
  }

  /**
   * Seeds the form from a module that has been read.
   *
   * MIGRATION: the legacy binding order is preserved where it is observable - the definition's display
   *   name into the read-only field and the instance title into the editable one
   *   (`ModuleSettings.ascx.vb:L125-L126`) - and the visibility is bound THROUGH the enumeration rather
   *   than into a list position, which is what `:L134` did by assigning the stored code straight into a
   *   selected index.
   *
   * MIGRATION: nullable text arrives as `null` and is shown as an empty control, which is the same
   *   thing the legacy code showed for its empty-string absence marker. Nullable dates are narrowed to
   *   the calendar date they carry, and the legacy absent-date marker is blanked.
   *
   * MIGRATION: the cache period is seeded from the STORED value even when the cache control is not
   *   offered, and that is a deliberate divergence. The legacy screen left the box empty whenever the
   *   definition declared no default cache period (`:L136-L142`) and its handler then wrote zero for an
   *   empty box (`:L349-L353`), so every save silently zeroed the stored period of any such module.
   *   Since the replacement writes every member, reproducing that would destroy stored data on each
   *   save; the stored value is round-tripped instead and the defect is recorded rather than repeated.
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

  /**
   * Reports a completed command and leaves the screen.
   *
   * MIGRATION: THE LEGACY SCREEN LEFT ON EVERY SUCCESSFUL OUTCOME - its update handler redirected at
   *   `:L421`, its removal handler at `:L307` and its cancel handler at `:L281`, each returning to the
   *   administration page the operator came from. The module listing is that page's counterpart.
   *
   * @param operation The command that completed.
   */
  private reportSuccess(operation: ModuleFormOperation): void {
    // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS ALREADY
    // SAVED - AND DESTROYS THE CONFIRMATION BELOW WHILE IT ASKS. The probe registered on this class reads
    // `dirty && saving() === false`, and the only caller of this method is the outcome effect, which runs
    // precisely on the transition OUT of `saving()` - so the flag is already false here while the controls
    // are still dirty from the operator's typing, and `returnToListing()` on the last line is a navigation
    // the guard can refuse. `window.confirm` blocks the JavaScript thread, so the auto-dismiss timer on
    // the notification below becomes due while the dialog stands and fires the instant it is accepted,
    // which is how a redundant prompt swallows the answer to itself.
    //
    // Marked for EVERY completed operation rather than only the write: after a removal the placement is
    // gone, so entry still standing in the controls is work that can no longer be saved. Marking an
    // already-pristine form is a no-op, so one unconditional pair is narrower than a per-operation test.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // The measured legacy severity vocabulary had exactly three levels and a completed operation used
    // the affirmative one.
    this.notifications.notify('success', SUCCESS_MESSAGES[operation], null, true);

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
    // retires notifications on a completed navigation and `returnToListing` navigates in this same
    // task, so the message was raised and swept before it could be painted. The legacy announced and
    // then redirected, so the listing is where this belongs.
    this.returnToListing(true);
  }

  /**
   * Navigates to the module listing.
   *
   * The rejection is handled rather than left floating: an unhandled navigation rejection surfaces only
   * in the console, where an operator never sees it, and the screen would appear to have done nothing.
   * A literal path is used because a feature folder never imports from another one.
   */
  private returnToListing(replaceEntry = false): void {
    /*
     * ⚠ THE CALL IS MADE TWO DIFFERENT WAYS ON PURPOSE, rather than always passing an options
     * object with a computed flag. A pushed departure keeps the exact call it always made, so the
     * behaviour of the cancel paths - and the specifications that pin them - is untouched by the
     * addition; only a REPLACING departure carries options, which is the case whose behaviour
     * genuinely changed. Written this way, the diff says what changed and nothing else.
     */
    const departure = replaceEntry
      ? this.router.navigateByUrl(MODULE_LIST_PATH, { replaceUrl: true })
      : this.router.navigateByUrl(MODULE_LIST_PATH);

    departure.catch(() => {
      this.notifications.notify('error', NAVIGATION_FAILED_MESSAGE);
    });
  }
}
