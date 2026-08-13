// WHAT THIS FILE IS

import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  Input,
  computed,
  effect,
  inject,
  signal,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TabService } from '../../../core/services/tab.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import {
  conflictMessage,
  problemMessage,
  problemSupportReference,
  statusMessage,
  stripLegacyBreakTags,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import {
  LoadingSpinnerComponent,
} from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type { AbstractControl, ValidationErrors } from '@angular/forms';
import type {
  PortalAdministrator,
  PortalSettings,
  UpdatePortalSettingsRequest,
} from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type { PortalFailure } from '../../../core/state/portal.store';
import {
  FocusFirstInvalidDirective,
  INVALID_CONTROL_SELECTOR,
} from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// ---------------------------------------------------------------------------
// The two tabs, and the disclosures nested inside them
// ---------------------------------------------------------------------------

/**
 * The two surviving top-level section groups, in markup order. Deliberately NOT exported, as with every
 * other type declared here.
 */
type PortalSettingsTab = 'basic' | 'advanced';

/**
 * The nested disclosures that survive, in markup order within their tab. Six of the ten legacy nested
 * section heads are gone with their subject matter: appearance, payment, usability and SSL entirely, and
 * marketing and other-settings only partially — which is why those two appear here.
 */
type PortalSettingsSection =
  | 'siteDetails'
  | 'marketing'
  | 'security'
  | 'pages'
  | 'other'
  | 'host';

/** The tab strip, in the order the legacy section heads declared. */
const TAB_ORDER: readonly PortalSettingsTab[] = ['basic', 'advanced'];

/**
 * The disclosures that start closed. Measured from the markup one head at a time: `dshSite` and
 * `dshSecurity` and `dshPages` declare no `IsExpanded` and so default open, `dshMarketing` declares
 * `IsExpanded="True"`, and `dshOther` and `dshHost` declare `IsExpanded="False"`.
 */
const INITIALLY_COLLAPSED: readonly PortalSettingsSection[] = ['other', 'host'];

/** `txtPortalName MaxLength="128"`, matching `Portals.PortalName nvarchar(128)`. */
const PORTAL_NAME_MAX_LENGTH = 128;

/** `txtDescription` and `txtKeyWords`, both `MaxLength="475"`. */
const METADATA_MAX_LENGTH = 475;

/** `txtFooterText MaxLength="100"`. */
const FOOTER_TEXT_MAX_LENGTH = 100;

/** `txtExpiryDate MaxLength="15"` (and `Width="150"`). */
const EXPIRY_DATE_MAX_LENGTH = 15;

/** `txtHostFee maxlength="10"` — lower-case in the source. */
const HOST_FEE_MAX_LENGTH = 10;

/**
 * Terminal width of `Portals.Currency`: `[char] (3) NULL`. ⚠ MEASURED FROM THE COLUMN AND THE SERVER, NOT
 * FROM A LEGACY `MaxLength`, because the legacy control was a SELECTOR (`cboCurrency`) and carried none —
 * a selector cannot overflow its column.
 */
const CURRENCY_MAX_LENGTH = 3;

/**
 * Terminal width of `Portals.DefaultLanguage`: `nvarchar(10) NOT NULL`. Measured from the column and from
 * the server's own `MaximumLength(10)`, for the same reason as the currency above: the legacy control was
 * a culture selector and declared no length.
 */
const DEFAULT_LANGUAGE_MAX_LENGTH = 10;

/** `txtHostSpace`, `txtPageQuota` and `txtUserQuota`, all `MaxLength="6"`. */
const QUOTA_MAX_LENGTH = 6;

// ---------------------------------------------------------------------------
// The page-selector sentinel
// ---------------------------------------------------------------------------

const NO_PAGE_SELECTED = -1;

/**
 * The wording of that option: `"<" + None_Specified + ">"` where the shared resource value of
 * `None_Specified.Text` is `None Specified`.
 */
const NO_PAGE_SELECTED_LABEL = '<None Specified>';

/** The in-flight wording for a WRITE this screen issued. */
/** The wording of the link to this portal's host names. */
const ALIASES_LINK_LABEL = 'Portal Aliases';

const SAVING_LABEL = 'Saving…';

/**
 * The in-flight wording for a READ that refreshes a screen already on display. Matches the full-screen
 * indicator's wording for the FIRST read, so a refetch and an initial read describe themselves the same
 * way and no third phrasing enters the screen.
 */
const REFRESHING_LABEL = 'Loading site settings…';

const NO_ADMINISTRATOR_SELECTED = -1;

const INDENT_STEP = '...';

/** The deepest indent that will ever be produced. */
const MAX_INDENT_LEVELS = 127;

// MIGRATION: localisation itself is not ported. No translation runtime is present in the pinned dependency
// surface, so these strings are authored directly and the legacy resource files served as the reference for
// their wording only.

/** `ControlTitle_.Text`. */
const PAGE_TITLE = 'Site Settings';

/** The two tab labels: `BasicSettings.Text` and `AdvancedSettings.Text`. */
const TAB_LABEL: Readonly<Record<PortalSettingsTab, string>> = Object.freeze({
  basic: 'Basic Settings',
  advanced: 'Advanced Settings',
});

/** `BasicSettingsHelp.Text` and `AdvancedSettingsHelp.Text`. */
const TAB_HELP: Readonly<Record<PortalSettingsTab, string>> = Object.freeze({
  basic: 'In this section, you can set up the basic settings for your site.',
  advanced: 'In this section, you can set up more advanced settings for your site.',
});

/** The surviving nested section captions, from the resource file. */
const SECTION_LABEL: Readonly<Record<PortalSettingsSection, string>> = Object.freeze({
  siteDetails: 'Site Details',
  marketing: 'Site Marketing',
  security: 'Security Settings',
  pages: 'Page Management',
  other: 'Other Settings',
  host: 'Host Settings',
});

/** The labelled-field captions, keyed by the control each legacy label named. */
const FIELD_LABEL = Object.freeze({
  portalName: 'Title:',
  description: 'Description:',
  keyWords: 'Keywords:',
  footerText: 'Copyright:',
  guid: 'GUID:',
  bannerAdvertising: 'Banners:',
  userRegistration: 'User Registration:',
  splashTabId: 'Splash Page:',
  homeTabId: 'Home Page:',
  loginTabId: 'Login Page:',
  userTabId: 'User Page:',
  administratorId: 'Administrator:',
  timeZoneOffset: 'Portal TimeZone:',
  currency: 'Currency:',
  defaultLanguage: 'Default Language:',
  expiryDate: 'Expiry Date:',
  hostFee: 'Hosting Fee:',
  hostSpace: 'Disk Space:',
  pageQuota: 'Page Quota:',
  userQuota: 'User Quota:',
} as const);

/**
 * The help text behind each field's disclosure, taken from the matching `*.Help` resource value.
 * `portalName` carries a double space after its first sentence and `hostSpace` ends with its
 * parenthesised note; both are reproduced exactly, the second because it is the ONLY place the "zero
 * means unlimited" rule is stated — the input itself shows the stored number, including zero.
 */
const FIELD_HELP = Object.freeze({
  portalName:
    'This is the Title for your portal.  The text you enter will show up in the Title Bar.',
  description: 'Enter a description about your site here.',
  keyWords:
    'Enter some keywords for your site (separated by commas).  These keywords are used by search engines to help index your site.',
  footerText: 'If supported by the skin this Copyright text is displayed on your site.',
  guid: 'The globally unique identifier which can be used to identify this portal.',
  bannerAdvertising:
    'Indicate the type of Banner Advertising you wish to display on your site.',
  userRegistration: 'The type of user registration allowed for this site',
  splashTabId: 'The Splash Page for your site.',
  homeTabId: 'The Home Page for your site.',
  loginTabId: 'The Login Page for your site.',
  userTabId: 'The User Page for your site.',
  administratorId: 'The Administrator User for the site.',
  timeZoneOffset: 'The TimeZone for the location of the site.',
  currency: 'The Currency used on the site.',
  defaultLanguage: 'The Default Language for the site.',
  expiryDate: 'The Expiry Date is the date that the Hosting Contract for the portal expires.',
  hostFee: 'The Hosting Fee is the monthly charge for hosting this site.',
  hostSpace:
    'The amount of Disk Space in MB allowed for this site (enter 0 for unlimited space).',
  pageQuota: 'You can specify a maximum number of pages per portal.',
  userQuota: 'You can specify a maximum number of users per portal.',
} as const);

/** `lblBanners.Text`, with its leading break markup removed. */
const BANNER_HOST_LOCK_NOTICE = stripLegacyBreakTags(
  '<br>Banner option was set by the hostingprovider, and cannot be changed',
);

/**
 * `valExpiryDate`'s inline `ErrorMessage`, cleaned the same way. This validator is the ONE measured
 * exception to the resource-first rule: it carries no resource key at all, so its markup attribute is the
 * only wording that exists.
 */
const EXPIRY_DATE_INVALID_MESSAGE = stripLegacyBreakTags('<br>Invalid expiry date!');

/** `valHostFee.Error` — this one carries no leading break, and none is invented. */
const HOST_FEE_INVALID_MESSAGE = 'Invalid fee, needs to be a currency value!';

/** The measured whole-number message for the three quota boxes. */
const WHOLE_NUMBER_INVALID_MESSAGE = 'Enter a whole number.';

/** The measured whole-number message for the time-zone offset. */
const TIME_ZONE_INVALID_MESSAGE = 'Enter the offset as a whole number of minutes.';

/**
 * `DeleteMessage.Text` from THIS screen's own resource file. The space before the question mark is in the
 * stored value and is reproduced verbatim.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Portal ?';

/** The confirmation dialog's own heading and its affirmative action, both shared wording. */
const DELETE_CONFIRM_TITLE = 'Delete';
const DELETE_CONFIRM_LABEL = 'Delete';

/** Shown once a save succeeds. */
const SAVE_SUCCEEDED_MESSAGE = 'The site settings were saved.';

/** Shown once a delete succeeds. */
const DELETE_SUCCEEDED_MESSAGE = 'The portal was deleted.';

/**
 * What a refusal of the host-owned fields says. This is the wording the `403` resolves to, and it names
 * the cause.
 */
const HOST_FIELDS_REFUSED_MESSAGE =
  'Only a host account may change the hosting fee, the disk space, the page quota, the user quota or the expiry date. Those fields were not saved.';

/** What a rejected submission says, before the field-level detail is shown. */
const FORM_INVALID_MESSAGE = 'Correct the highlighted fields and try again.';

/** Shown when the page list cannot be read, so the four selectors are knowingly thin. */
const PAGES_UNAVAILABLE_MESSAGE =
  'The list of pages could not be loaded, so the page selectors show only the pages already chosen.';

/**
 * Shown when the administrator candidates cannot be read, so the selector knowingly holds only the
 * account already designated.
 */
const ADMINISTRATORS_UNAVAILABLE_MESSAGE =
  'The list of eligible administrators could not be loaded, so the administrator cannot be changed here. Every other setting still saves.';

/** Shown while the administrator candidates are being read. */
const ADMINISTRATORS_LOADING_MESSAGE = 'Loading the accounts that may administer this site…';

/** The wording of the entry standing in for an administrator the candidate list does not hold. */
const RETAINED_ADMINISTRATOR_LABEL = 'Current administrator (account {0})';

/** Shown when the address carries no usable portal identifier. */
const PORTAL_ID_MISSING_MESSAGE = 'This address does not identify a portal to configure.';

/** Where cancelling, and a completed delete, navigate to. */
const PORTAL_LIST_PATH = '/portals';

/** The refusal status the host-only-field rule arrives as. */
const HTTP_FORBIDDEN = 403;

// ---------------------------------------------------------------------------
// Local shapes. None is exported: no other file needs them, and the page-option
// shape in particular must not become a second public tab contract.
// ---------------------------------------------------------------------------

/** One horizontal radio choice. */
interface RadioChoice<TValue> {
  readonly value: TValue;
  readonly label: string;
}

/** One option in a page selector. */
interface PageOption {
  readonly value: number;
  readonly label: string;
}

/** One option in the administrator selector. */
interface AdministratorOption {
  readonly value: number;
  readonly label: string;
}

/** The typed control set. */
interface PortalSettingsFormModel {
  // Site Details.
  portalName: FormControl<string>;
  description: FormControl<string>;
  keyWords: FormControl<string>;
  footerText: FormControl<string>;

  // Site Marketing.
  bannerAdvertising: FormControl<BannerAdvertisingMode>;

  // Security Settings.
  userRegistration: FormControl<UserRegistrationMode>;

  // Page Management. `NO_PAGE_SELECTED` rather than absence: a select must hold the
  // value of the option it shows, and the legacy option carried -1.
  splashTabId: FormControl<number>;
  homeTabId: FormControl<number>;
  loginTabId: FormControl<number>;
  userTabId: FormControl<number>;

  administratorId: FormControl<number>;
  timeZoneOffset: FormControl<string>;
  currency: FormControl<string>;
  defaultLanguage: FormControl<string>;

  // Host Settings — rendered only for a host account, always hydrated.
  expiryDate: FormControl<string>;
  hostFee: FormControl<string>;
  hostSpace: FormControl<string>;
  pageQuota: FormControl<string>;
  userQuota: FormControl<string>;
}

/** The members this screen preserves without showing. */
type PreservedMembers = Pick<
  PortalSettings,
  | 'backgroundFile'
  | 'homeDirectory'
  | 'logoFile'
  | 'paymentProcessor'
  | 'processorUserId'
  | 'siteLogHistory'
>;

// ---------------------------------------------------------------------------
// Conversions. Each is explicit, and none uses a truthiness test or a coalesced
// default, because on this screen `0`, `-1` and the empty string are all DATA.
// ---------------------------------------------------------------------------

/** A whole decimal integer, optionally signed, and nothing else. */
const INTEGER_PATTERN = /^[+-]?\d+$/;

/**
 * A currency amount as the legacy `Type="Currency"` check understood one: an optional sign, at least one
 * digit, and an optional fractional part.
 */
const CURRENCY_PATTERN = /^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/;

/** An ISO calendar date, which is what a native date input produces. */
const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/** How many characters of an ISO instant make up its date. */
const ISO_DATE_LENGTH = 10;

/**
 * Reads the portal identifier the router bound onto the input. A router path segment arrives as a STRING,
 * so a conversion is unavoidable — and it has to be an explicit one.
 *
 * @param value The bound value: a path segment, an already-numeric identifier, or nothing.
 * @returns The identifier, or `undefined` when the address carried none that is usable.
 */
function parsePortalId(value: string | number | null | undefined): number | undefined {
  if (typeof value === 'number') {
    return Number.isSafeInteger(value) ? value : undefined;
  }

  if (typeof value !== 'string') {
    return undefined;
  }

  const trimmed = value.trim();

  if (!INTEGER_PATTERN.test(trimmed)) {
    return undefined;
  }

  const parsed = Number.parseInt(trimmed, 10);

  return Number.isSafeInteger(parsed) ? parsed : undefined;
}

function numberToText(value: number | null): string {
  return value === null ? '' : String(value);
}

/** Reads a number back out of a text box, treating a blank box as zero. */
function textToNumberOrZero(value: string): number {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return 0;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Trims a text box and reports a blank one as absence. The legacy null contract spelled its absent string
 * as the EMPTY STRING and converted it back to a database null on the way out, so a blank box and a null
 * column were the same state.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}

/** Takes the date out of a stored instant so a native date input can show it. */
function instantToDateInput(instant: string | null): string {
  if (instant === null || instant.length < ISO_DATE_LENGTH) {
    return '';
  }

  const datePart = instant.slice(0, ISO_DATE_LENGTH);

  return ISO_DATE_PATTERN.test(datePart) ? datePart : '';
}

/**
 * Turns a date input's value back into the instant the API expects, or absence. AN ABSENT EXPIRY TRAVELS
 * AS ABSENCE, NOT AS THE LEGACY DATE SENTINEL. The legacy handler defaulted this to `Null.NullDate`,
 * which is `0001-01-01`.
 */
function dateInputToInstant(value: string): string | null {
  const trimmed = value.trim();

  return ISO_DATE_PATTERN.test(trimmed) ? trimmed : null;
}

/**
 * Maps a page selector's value onto the wire. THE "NONE SPECIFIED" SENTINEL STAYS IN THE FORM AND BECOMES
 * ABSENCE ON THE WIRE. The legacy handler sent `-1`, and `Null.GetNull` then rewrote it to a database
 * null exactly as it did for the expiry date, so `-1` never reached the column either.
 */
function selectedPageToWire(value: number): number | null {
  return value === NO_PAGE_SELECTED ? null : value;
}

/**
 * Maps a stored page reference onto the selector. Absence becomes the sentinel the "none specified"
 * option carries, so the option genuinely appears chosen rather than leaving the select on whatever
 * happened to be first.
 */
function wirePageToSelected(value: number | null): number {
  return value === null ? NO_PAGE_SELECTED : value;
}

/**
 * Maps the administrator selector's value onto the wire contract. The sentinel becomes absence, which the
 * server permits only for a portal that already designates no administrator — and the selector offers the
 * option carrying it only in that case, so the two rules agree by construction rather than by the
 * operator's restraint. ⚠ EVERY OTHER NUMBER IS SENT AS IT STANDS, including zero.
 */
function selectedAdministratorToWire(value: number): number | null {
  return value === NO_ADMINISTRATOR_SELECTED ? null : value;
}

/**
 * Maps a stored administrator reference onto the selector. Absence becomes the sentinel, so the empty
 * option genuinely appears chosen rather than leaving the select on whatever happened to be first — which
 * on this field would designate an administrator the operator never picked.
 */
function wireAdministratorToSelected(value: number | null): number {
  return value === null ? NO_ADMINISTRATOR_SELECTED : value;
}

// Validators
// * A presence rule must NOT be added. A comparison validator performing a data-type check PASSES on empty
// input, so leaving either box blank was entirely valid, and rejecting a blank now would turn a legitimate
// save into a rejected one. * A presence rule is not a substitute for the type check either.

/**
 * Builds a validator that mirrors a legacy data-type check.
 *
 * @param pattern What a well-formed value looks like.
 * @param errorKey The error key to report under.
 * @param message The measured wording to report.
 * @returns A validator function suitable for a non-nullable text control.
 */
function dataTypeCheck(
  pattern: RegExp,
  errorKey: string,
  message: string,
): (control: AbstractControl) => ValidationErrors | null {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    if (typeof raw !== 'string') {
      return null;
    }

    const trimmed = raw.trim();

    if (trimmed.length === 0) {
      return null;
    }

    return pattern.test(trimmed) ? null : { [errorKey]: message };
  };
}

/** `valExpiryDate`: `Operator="DataTypeCheck" Type="Date"`. */
const expiryDateTypeCheck = dataTypeCheck(
  ISO_DATE_PATTERN,
  'expiryDateType',
  EXPIRY_DATE_INVALID_MESSAGE,
);

/** `valHostFee`: `Operator="DataTypeCheck" Type="Currency"`. */
const hostFeeTypeCheck = dataTypeCheck(CURRENCY_PATTERN, 'hostFeeType', HOST_FEE_INVALID_MESSAGE);

/** The three quota boxes take a whole number. */
const wholeNumberCheck = dataTypeCheck(
  INTEGER_PATTERN,
  'wholeNumber',
  WHOLE_NUMBER_INVALID_MESSAGE,
);

/** The time-zone offset is a whole, signed number of minutes. */
const timeZoneOffsetCheck = dataTypeCheck(
  INTEGER_PATTERN,
  'timeZoneOffset',
  TIME_ZONE_INVALID_MESSAGE,
);

// The page options behind the four selectors
// * none-specified TRUE → prepend a synthetic option, `TabID = -1`, named `"<" + None_Specified + ">"`, and
// SELECTABLE; * hidden TRUE → invisible pages ARE included; * deleted FALSE → recycled pages are excluded;
// * URL FALSE → only pages whose type is Normal, and `GetURLType` returns Normal exactly when the page's
// URL is empty; * authorised FALSE → no role filtering is applied; * and unconditionally → administration
// pages are excluded.

/** Reports whether a page is of the legacy Normal type. */
function isNormalPage(row: TabListItem): boolean {
  return row.url === null || row.url.trim().length === 0;
}

/** Reports whether a page sits in the administration band. */
function isAdministrationPage(row: TabListItem, adminTabId: number | null): boolean {
  if (adminTabId === null) {
    return false;
  }

  return row.tabId === adminTabId || row.parentId === adminTabId;
}

/** Produces the indent prefix for a page at the given level. */
function indentFor(level: number): string {
  if (!Number.isFinite(level) || level <= 0) {
    return '';
  }

  const steps = Math.min(Math.trunc(level), MAX_INDENT_LEVELS);

  return INDENT_STEP.repeat(steps);
}

/**
 * Builds the shared option list for all four page selectors. The received order is preserved rather than
 * re-sorted: the listing already arrives in hierarchy order by page order, which is the sequence the
 * legacy iteration relied on, and re-sorting it here would put the indents out of step with their
 * parents.
 *
 * @param rows Every page of the portal, as received.
 * @param adminTabId The portal's administration page, or `null` when it is not yet known.
 * @param retain Page references the portal currently holds, so a stored choice that the filter would
 * otherwise hide still appears — a page that has since been recycled, made into a link, or moved under
 * administration must remain visible as the current value rather than silently reset the selector to
 * @returns The options, beginning with the selectable "none specified" entry.
 */
function buildPageOptions(
  rows: readonly TabListItem[],
  adminTabId: number | null,
  retain: readonly number[],
): readonly PageOption[] {
  const options: PageOption[] = [
    { value: NO_PAGE_SELECTED, label: NO_PAGE_SELECTED_LABEL },
  ];

  // Guards against a duplicated identifier in the response. Two options sharing a value
  // would make a native select ambiguous about which one is chosen.
  const seen = new Set<number>([NO_PAGE_SELECTED]);
  const wanted = new Set<number>(retain);

  for (const row of rows) {
    if (seen.has(row.tabId)) {
      continue;
    }

    const held = wanted.has(row.tabId);
    const admitted =
      row.isDeleted === false && isNormalPage(row) && !isAdministrationPage(row, adminTabId);

    if (!admitted && !held) {
      continue;
    }

    seen.add(row.tabId);
    options.push({ value: row.tabId, label: indentFor(row.level) + row.tabName });
  }

  for (const tabId of retain) {
    if (!seen.has(tabId)) {
      seen.add(tabId);
      options.push({ value: tabId, label: String(tabId) });
    }
  }

  return options;
}

/** The portal settings screen. */
@Component({
  selector: 'app-portal-settings',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    // For the host-name listing link in the page header.
    RouterLink,
    PageHeaderComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    ErrorBannerComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './portal-settings.component.html',
  styleUrl: './portal-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalSettingsComponent {
  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    // `busy` is declared further down the class; the arrow body is only evaluated when the
    // tracker asks, so the ordering is irrelevant at construction time.
    () => this.form.dirty && this.busy() === false,
  );
  // Collaborators. Injected as fields rather than through the constructor, which is this workspace's
  // convention, and every one of them is a state or presentation concern: no transport type is reachable
  // from here.

  private readonly portals = inject(PortalStore);
  private readonly identity = inject(AuthStore);
  private readonly pages = inject(TabService);
  private readonly notifications = inject(NotificationService);

  /**
   * This screen's own element, searched for the first control a refused submit is standing on. Scoped to
   * the host and never to the document, so the search cannot reach a control belonging to another screen
   * still in the DOM during a route transition.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Used to render the tab and section changes a refusal makes BEFORE focus is moved. A control that is
   * not in the document cannot take focus, and this screen keeps only the active tab's panel in the DOM,
   * so revealing a control and focusing it are two steps that must be separated by a render.
   */
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------
  // Local state
  // -------------------------------------------------------------------------

  private readonly _portalId = signal<number | undefined>(undefined);
  private readonly _pageRows = signal<readonly TabListItem[]>([]);
  private readonly _pagesFailed = signal(false);
  private readonly _activeTab = signal<PortalSettingsTab>('basic');
  private readonly _collapsed = signal<ReadonlySet<PortalSettingsSection>>(
    new Set(INITIALLY_COLLAPSED),
  );
  private readonly _confirmingDelete = signal(false);
  private readonly _submitRejected = signal(false);

  /**
   * The tab controls, in the order the strip renders them. Queried rather than reached through a selector
   * so that keyboard movement never depends on an element identifier matching a string built here, and so
   * that a second instance of this screen on one page could not steal the focus.
   */
  private readonly tabControls = viewChildren<ElementRef<HTMLElement>>('tabControl');

  /** The settings object the form currently reflects. */
  private hydratedFrom: PortalSettings | null = null;

  /**
   * The page listing read in flight, held so that a NEW read can cancel the one it replaces.
   * Destruction-time cleanup alone was not enough.
   */
  private pagesRequest: Subscription | null = null;

  /**
   * The failures already announced, held by identity so the same one is not announced twice. Plain fields
   * rather than signals: they exist only to make the announcing effect idempotent, and nothing renders
   * them.
   */
  private announcedSettingsFailure: PortalFailure | null = null;
  private announcedDetailFailure: PortalFailure | null = null;

  // -------------------------------------------------------------------------
  // The form
  // -------------------------------------------------------------------------

  /**
   * Every control is non-nullable, which is what makes the raw value fully typed instead of partial, and
   * what makes a reset return each control to its declared initial value rather than to null.
   */
  protected readonly form = new FormGroup<PortalSettingsFormModel>({
    portalName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PORTAL_NAME_MAX_LENGTH)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
    }),
    keyWords: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
    }),
    footerText: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(FOOTER_TEXT_MAX_LENGTH)],
    }),
    bannerAdvertising: new FormControl<BannerAdvertisingMode>(BannerAdvertisingMode.None, {
      nonNullable: true,
    }),
    userRegistration: new FormControl<UserRegistrationMode>(UserRegistrationMode.NoRegistration, {
      nonNullable: true,
    }),
    splashTabId: new FormControl(NO_PAGE_SELECTED, { nonNullable: true }),
    homeTabId: new FormControl(NO_PAGE_SELECTED, { nonNullable: true }),
    loginTabId: new FormControl(NO_PAGE_SELECTED, { nonNullable: true }),
    userTabId: new FormControl(NO_PAGE_SELECTED, { nonNullable: true }),

    // NO VALIDATOR, and that is measured rather than assumed: a full case-insensitive sweep of the 568-line
    // legacy markup finds exactly two validators on the whole screen, both data-type comparisons, and
    // neither is on this field.
    administratorId: new FormControl(NO_ADMINISTRATOR_SELECTED, { nonNullable: true }),
    timeZoneOffset: new FormControl('', {
      nonNullable: true,
      validators: [timeZoneOffsetCheck],
    }),

    // LENGTH ONLY, AND NO CLOSED VALUE SET. The legacy controls were selectors, so their legality came from
    // the list they were filled from rather than from a validator: the currency list came from the excluded
    // list subsystem and the culture list from the excluded localisation subsystem.
    currency: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(CURRENCY_MAX_LENGTH)],
    }),
    defaultLanguage: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(DEFAULT_LANGUAGE_MAX_LENGTH)],
    }),
    expiryDate: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(EXPIRY_DATE_MAX_LENGTH), expiryDateTypeCheck],
    }),
    hostFee: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(HOST_FEE_MAX_LENGTH), hostFeeTypeCheck],
    }),
    hostSpace: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(QUOTA_MAX_LENGTH), wholeNumberCheck],
    }),
    pageQuota: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(QUOTA_MAX_LENGTH), wholeNumberCheck],
    }),
    userQuota: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(QUOTA_MAX_LENGTH), wholeNumberCheck],
    }),
  });

  // -------------------------------------------------------------------------
  // Static wording and choices, surfaced for the template
  // -------------------------------------------------------------------------

  protected readonly pageTitle = PAGE_TITLE;
  protected readonly tabOrder = TAB_ORDER;
  protected readonly tabLabel = TAB_LABEL;
  protected readonly tabHelp = TAB_HELP;
  protected readonly sectionLabel = SECTION_LABEL;
  protected readonly fieldLabel = FIELD_LABEL;
  protected readonly fieldHelp = FIELD_HELP;
  protected readonly bannerHostLockNotice = BANNER_HOST_LOCK_NOTICE;
  protected readonly deleteConfirmTitle = DELETE_CONFIRM_TITLE;
  /**
   * The confirmation body: the measured question, then WHICH portal it will destroy. ⚠ THE PROMPT NAMED A
   * TYPE AND NOT AN INSTANCE. "Are You Sure You Wish To Delete This Portal ?" is the legacy wording and
   * is kept verbatim, but the dialog is modal and covers the screen it was raised from - including the
   * heading that was the only thing on the page saying which tenant is open.
   */
  protected readonly deleteConfirmMessage = computed<string>(() => {
    const name: string | undefined = this.portalName();

    return name === undefined ? DELETE_CONFIRM_MESSAGE : `${DELETE_CONFIRM_MESSAGE} ${name}`;
  });
  protected readonly deleteConfirmLabel = DELETE_CONFIRM_LABEL;
  protected readonly formInvalidMessage = FORM_INVALID_MESSAGE;
  protected readonly pagesUnavailableMessage = PAGES_UNAVAILABLE_MESSAGE;

  /** The wording shown when the administrator candidates could not be read. */
  protected readonly administratorsUnavailableMessage = ADMINISTRATORS_UNAVAILABLE_MESSAGE;

  /** The wording shown while the administrator candidates are being read. */
  protected readonly administratorsLoadingMessage = ADMINISTRATORS_LOADING_MESSAGE;
  protected readonly portalIdMissingMessage = PORTAL_ID_MISSING_MESSAGE;

  protected readonly noPageSelectedLabel = NO_PAGE_SELECTED_LABEL;

  /** The measured character limits, so the template need not restate a number. */
  protected readonly maxLength = Object.freeze({
    portalName: PORTAL_NAME_MAX_LENGTH,
    description: METADATA_MAX_LENGTH,
    keyWords: METADATA_MAX_LENGTH,
    footerText: FOOTER_TEXT_MAX_LENGTH,
    currency: CURRENCY_MAX_LENGTH,
    defaultLanguage: DEFAULT_LANGUAGE_MAX_LENGTH,
    expiryDate: EXPIRY_DATE_MAX_LENGTH,
    hostFee: HOST_FEE_MAX_LENGTH,
    hostSpace: QUOTA_MAX_LENGTH,
    pageQuota: QUOTA_MAX_LENGTH,
    userQuota: QUOTA_MAX_LENGTH,
  });

  /** `optBanners`, horizontal, three items. */
  protected readonly bannerChoices: readonly RadioChoice<BannerAdvertisingMode>[] = Object.freeze([
    { value: BannerAdvertisingMode.None, label: 'None' },
    { value: BannerAdvertisingMode.Site, label: 'Site' },
    { value: BannerAdvertisingMode.Host, label: 'Host' },
  ]);

  /** `optUserRegistration`, horizontal, four items. */
  protected readonly registrationChoices: readonly RadioChoice<UserRegistrationMode>[] =
    Object.freeze([
      { value: UserRegistrationMode.NoRegistration, label: 'None' },
      { value: UserRegistrationMode.PrivateRegistration, label: 'Private' },
      { value: UserRegistrationMode.PublicRegistration, label: 'Public' },
      { value: UserRegistrationMode.VerifiedRegistration, label: 'Verified' },
    ]);

  // -------------------------------------------------------------------------
  // The route input
  // -------------------------------------------------------------------------

  /**
   * The portal to configure, bound from the `:portalId` path segment. The NAME is load-bearing.
   *
   * @param value The bound path segment, an already-numeric identifier, or nothing.
   */
  @Input()
  public set portalId(value: string | number | null | undefined) {
    const resolved = parsePortalId(value);

    if (this._portalId() === resolved) {
      return;
    }

    this._portalId.set(resolved);
    this.hydratedFrom = null;

    if (resolved === undefined) {
      return;
    }

    this.portals.loadSettings(resolved);
    this.portals.loadPortal(resolved);
    this.portals.loadAdministrators(resolved);
    this.loadPages(resolved);
  }

  /** The resolved identifier, or `undefined` when the address carried none usable. */
  public get portalId(): number | undefined {
    return this._portalId();
  }

  // -------------------------------------------------------------------------
  // Construction
  // -------------------------------------------------------------------------

  public constructor() {
    effect(() => {
      const received = this.portals.settings();

      if (received === null || received === this.hydratedFrom) {
        return;
      }

      this.hydrate(received);
    });

    effect(() => {
      const failure = this.portals.settingsFailure();

      if (failure === this.announcedSettingsFailure) {
        return;
      }

      this.announcedSettingsFailure = failure;

      if (failure !== null) {
        this.notifications.notify(
          failure.severity,
          this.describeSaveFailure(failure),
          problemSupportReference(failure.problem),
        );
      }
    });

    effect(() => {
      const failure = this.portals.detailFailure();

      if (failure === this.announcedDetailFailure) {
        return;
      }

      this.announcedDetailFailure = failure;

      if (failure !== null) {
        this.notifications.notify(
          failure.severity,
          this.describeDeleteFailure(failure),
          problemSupportReference(failure.problem),
        );
      }
    });

    // The banner lock is its OWN effect rather than a step of hydration, and the difference matters. The
    // lock depends on two things — the stored choice and whether the caller holds the host account — and
    // the identity is not guaranteed to have resolved by the time the settings arrive.
    effect(() => {
      const locked = this.bannerLockedByHost();
      const control = this.form.controls.bannerAdvertising;

      if (locked) {
        if (control.enabled) {
          control.disable({ emitEvent: false });
        }

        return;
      }

      if (control.disabled) {
        control.enable({ emitEvent: false });
      }
    });

    // ⚠ WITHDRAW THE REFUSAL NOTICE ONCE IT STOPS BEING TRUE. The notice reads "correct the highlighted
    // fields and try again", and runtime measurement found it still saying so after the one offending field
    // had been corrected and the form had returned to valid, with zero field errors left on screen — an
    // assertion about the present tense that was no longer about anything.
    this.form.statusChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      if (this.form.valid && this._submitRejected()) {
        this._submitRejected.set(false);
      }
    });
  }

  // -------------------------------------------------------------------------
  // Derived state
  // -------------------------------------------------------------------------

  /** True once an identifier was resolved from the address. */
  protected readonly hasPortalId = computed<boolean>(() => this._portalId() !== undefined);

  /**
   * The address of this portal's host names, or `null` when the route named no portal. ⚠ THIS IS THE
   * APPLICATION'S ONLY LINK TO THAT SCREEN. Every anchor the console renders was enumerated and none
   * addressed `:portalId/aliases`; the listing's single row command targets this screen, and nothing led
   * onwards from it.
   */
  protected readonly aliasesLink = computed<(string | number)[] | null>(() => {
    const id: number | undefined = this._portalId();

    return id === undefined ? null : ['/portals', id, 'aliases'];
  });

  /**
   * The wording of that link. ⚠ LEGACY-VERBATIM.
   * `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx` declares `ControlTitle_.Text` as
   * `Portal Aliases`, which is what the legacy module titled itself.
   */
  protected readonly aliasesLinkLabel: string = ALIASES_LINK_LABEL;

  /** The settings resource on screen, or `null` before the first read completes. */
  protected readonly settings = this.portals.settings;

  /** True while either read is outstanding and nothing is on screen yet. */
  protected readonly loading = computed<boolean>(
    () => this.portals.settingsLoading() && this.portals.settings() === null,
  );

  protected readonly busy = computed<boolean>(
    () => this.portals.settingsLoading() || this.portals.detailLoading(),
  );

  /**
   * Whether the outstanding request is a WRITE this screen issued, rather than a read. ⚠ THE AFFORDANCE
   * WAS MISLABELLED WITHOUT IT. The action row renders a progress indicator whenever {@link
   * PortalSettingsComponent.busy} holds, labelled "Saving…" - and that label was measured appearing on a
   * plain REVISIT to this screen, where the store already holds settings so the form renders immediately
   * and the refetch merely flips the same flag.
   */
  private readonly writing = signal(false);

  /** The wording for the in-flight indicator, chosen by what is actually in flight. */
  protected readonly busyLabel = computed<string>(() =>
    this.writing() ? SAVING_LABEL : REFRESHING_LABEL,
  );

  /**
   * The problem document to present, if any. Both slices are consulted because the two operations this
   * screen performs report through different ones: saving settings fails into the settings slice, and
   * deleting the portal fails into the detail slice.
   */
  protected readonly problem = computed<ProblemDetails | null>(() => {
    const settingsFailure = this.portals.settingsFailure();

    if (settingsFailure !== null) {
      return settingsFailure.problem;
    }

    const detailFailure = this.portals.detailFailure();

    return detailFailure === null ? null : detailFailure.problem;
  });

  /**
   * The portal's own name, shown beside the page title so an operator editing one of many portals can see
   * which one.
   */
  protected readonly portalName = computed<string | undefined>(() => {
    const detail = this.portals.selectedPortal();

    if (detail === null) {
      return undefined;
    }

    const name = detail.portalName;

    return name === null || name.trim().length === 0 ? undefined : name;
  });

  /**
   * Whether the caller holds the host account. This gates the host-settings disclosure, the delete action
   * and the banner lock.
   */
  protected readonly isSuperUser = this.identity.isSuperUser;

  /**
   * Whether the banner choice is locked by the hosting provider. Measured: the legacy screen hid the
   * notice outright for a host account and, for everybody else, disabled the list and showed the notice
   * exactly when the STORED value was Host.
   */
  protected readonly bannerLockedByHost = computed<boolean>(() => {
    if (this.identity.isSuperUser()) {
      return false;
    }

    const held = this.portals.settings();

    return held !== null && held.bannerAdvertising === BannerAdvertisingMode.Host;
  });

  /** Whether the delete action is offered. */
  protected readonly canDelete = computed<boolean>(() => {
    if (!this.identity.isSuperUser()) {
      return false;
    }

    const target = this._portalId();
    const browsing = this.identity.portalId();

    if (target === undefined || browsing === null) {
      return false;
    }

    return target !== browsing;
  });

  /** True when the page listing could not be read, so the selectors are knowingly thin. */
  protected readonly pagesUnavailable = this._pagesFailed.asReadonly();

  protected readonly pageOptions = computed<readonly PageOption[]>(() => {
    const detail = this.portals.selectedPortal();
    const adminTabId = detail === null ? null : detail.adminTabId;
    const held = this.portals.settings();

    const retain: number[] = [];

    if (held !== null) {
      for (const reference of [held.splashTabId, held.homeTabId, held.loginTabId, held.userTabId]) {
        if (reference !== null) {
          retain.push(reference);
        }
      }
    }

    return buildPageOptions(this._pageRows(), adminTabId, retain);
  });

  protected readonly administratorOptions = computed<readonly AdministratorOption[]>(() => {
    const stored: number | null = this.portals.settings()?.administratorId ?? null;
    const options: AdministratorOption[] = [];
    const seen = new Set<number>();

    if (stored === null) {
      options.push({
        value: NO_ADMINISTRATOR_SELECTED,
        label: NO_PAGE_SELECTED_LABEL,
      });
      seen.add(NO_ADMINISTRATOR_SELECTED);
    }

    for (const candidate of this.administratorCandidates()) {
      if (seen.has(candidate.userId)) {
        continue;
      }

      seen.add(candidate.userId);
      options.push({
        value: candidate.userId,
        // Both names, because the display name is the one account field a tenant may compose from a format
        // string and two administrators can therefore legitimately share one. The login name is unique
        // within a portal, so the pair is always distinguishable.
        label: `${candidate.displayName} (${candidate.username})`,
      });
    }

    if (stored !== null && !seen.has(stored)) {
      options.push({
        value: stored,
        label: RETAINED_ADMINISTRATOR_LABEL.replace('{0}', String(stored)),
      });
    }

    return options;
  });

  /**
   * The candidate accounts, but only when they belong to the portal this screen is showing. The gate is
   * the whole value of the store recording which portal it read for: without it, the held list is simply
   * "the last list read", which during a move between portals is the wrong one and is indistinguishable
   * from the right one.
   */
  private readonly administratorCandidates = computed<readonly PortalAdministrator[]>(() => {
    const wanted = this._portalId();
    if (wanted === undefined || this.portals.administratorsPortalId() !== wanted) {
      return [];
    }

    return this.portals.administrators() ?? [];
  });

  /** Whether the candidate read is in flight, so the template can say the list is coming. */
  protected readonly administratorsLoading = computed<boolean>(() =>
    this.portals.administratorsLoading(),
  );

  /** Whether the candidate read failed, so the template can say why the list is short. */
  protected readonly administratorsFailed = computed<boolean>(() => {
    if (this.portals.administratorsFailure() === null) {
      return false;
    }

    return this.portals.administratorsLoading() === false;
  });

  /**
   * The portal's globally unique identifier, upper-cased and read-only. The legacy screen rendered it
   * through `.ToString.ToUpper` into a label, never into an input, and the column carries no setter on
   * this screen.
   */
  protected readonly portalGuid = computed<string>(() => {
    const held = this.portals.settings();

    return held === null ? '' : held.guid.toUpperCase();
  });

  /** True once a submission was rejected, so the form-level message may be shown. */
  protected readonly submitRejected = this._submitRejected.asReadonly();

  /** True while the delete confirmation is open. */
  protected readonly confirmingDelete = this._confirmingDelete.asReadonly();

  // -------------------------------------------------------------------------
  // The tab strip
  // -------------------------------------------------------------------------

  /** Which tab is showing. */
  protected readonly activeTab = this._activeTab.asReadonly();

  /** Whether the named tab is the one showing. */
  protected isTabActive(tab: PortalSettingsTab): boolean {
    return this._activeTab() === tab;
  }

  /** The element identifier of a tab's control, for the panel's labelling reference. */
  protected tabControlId(tab: PortalSettingsTab): string {
    return `portal-settings-tab-${tab}`;
  }

  /** The element identifier of a tab's panel. */
  protected tabPanelId(tab: PortalSettingsTab): string {
    return `portal-settings-panel-${tab}`;
  }

  /** Shows the named tab. */
  protected selectTab(tab: PortalSettingsTab): void {
    this._activeTab.set(tab);
  }

  /**
   * Moves between tabs with the keyboard. The arrow, home and end behaviour is what a tab strip is
   * expected to implement once it declares the roles that promise it; a strip that declares them without
   * implementing them is worse than one that declares neither.
   *
   * @param event The originating key event.
   */
  protected onTabKeydown(event: KeyboardEvent): void {
    const current = TAB_ORDER.indexOf(this._activeTab());

    if (current < 0) {
      return;
    }

    const last = TAB_ORDER.length - 1;
    let wanted: number;

    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        wanted = current === last ? 0 : current + 1;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        wanted = current === 0 ? last : current - 1;
        break;
      case 'Home':
        wanted = 0;
        break;
      case 'End':
        wanted = last;
        break;
      default:
        return;
    }

    const target = TAB_ORDER[wanted];

    if (target === undefined) {
      return;
    }

    event.preventDefault();
    this._activeTab.set(target);
    this.focusTab(wanted);
  }

  /**
   * Moves keyboard focus onto the tab control at the given position. Focus follows selection in this
   * strip, which is the expected behaviour for a strip whose panels are already loaded: the panel changes
   * as the caller moves, with no second keystroke needed to activate it.
   *
   * @param index The position in the declared tab order.
   */
  private focusTab(index: number): void {
    this.tabControls().at(index)?.nativeElement.focus();
  }

  // -------------------------------------------------------------------------
  // The nested disclosures
  // -------------------------------------------------------------------------

  /** Whether the named section is open. */
  protected isSectionOpen(section: PortalSettingsSection): boolean {
    return !this._collapsed().has(section);
  }

  /** The element identifier of a section's body, for its control's `aria-controls`. */
  protected sectionBodyId(section: PortalSettingsSection): string {
    return `portal-settings-section-${section}`;
  }

  /** Opens or closes the named section. */
  protected toggleSection(section: PortalSettingsSection): void {
    this._collapsed.update((held: ReadonlySet<PortalSettingsSection>) => {
      const next = new Set(held);

      if (next.has(section)) {
        next.delete(section);
      } else {
        next.add(section);
      }

      return next;
    });
  }

  // -------------------------------------------------------------------------
  // Element identifiers
  // -------------------------------------------------------------------------

  /**
   * A stable element identifier for one field's control.
   *
   * @param name The control, or one of the two read-only rows that have no control.
   * @returns The identifier.
   */
  protected controlId(name: keyof PortalSettingsFormModel | 'guid'): string {
    return `portal-settings-${name}`;
  }

  // -------------------------------------------------------------------------
  // Field messages
  // -------------------------------------------------------------------------

  /**
   * The validation messages for one control, or nothing when it has none to show. A message appears only
   * once the operator has touched or changed the control, which is what the legacy dynamic display did —
   * an untouched form showed no complaints.
   *
   * @param name The control to report on.
   * @returns The messages to show, newest rule first, or an empty list.
   */
  protected messagesFor(name: keyof PortalSettingsFormModel): readonly string[] {
    const control = this.form.controls[name];

    if (control.valid || !(control.dirty || control.touched)) {
      return [];
    }

    const errors = control.errors;

    if (errors === null) {
      return [];
    }

    const messages: string[] = [];

    // ⚠ NO REQUIRED RULE IS REPORTED, BECAUSE NO CONTROL ON THIS SCREEN DECLARES ONE. A branch here
    // reported `"Site Title is required."` against a `Validators.required` on the title, mirroring a
    // `NotEmpty()` the update contract carried; both are withdrawn as a parity break.
    for (const key of ['expiryDateType', 'hostFeeType', 'wholeNumber', 'timeZoneOffset']) {
      const held: unknown = errors[key];

      if (typeof held === 'string') {
        messages.push(held);
      }
    }

    const tooLong: unknown = errors['maxlength'];

    if (typeof tooLong === 'object' && tooLong !== null) {
      const limit: unknown = (tooLong as Record<string, unknown>)['requiredLength'];

      if (typeof limit === 'number') {
        messages.push(`Enter at most ${limit} characters.`);
      }
    }

    return messages;
  }

  // -------------------------------------------------------------------------
  // Actions
  // -------------------------------------------------------------------------

  /** Saves the settings. A rejected form is not sent. */
  protected onSubmit(): void {
    const target = this._portalId();

    if (target === undefined || this.hydratedFrom === null || this.portals.settingsLoading()) {
      return;
    }

    // ⚠ NORMALISE THE TITLE BEFORE JUDGING IT, so one resource does not trim where another does.
    const titleControl = this.form.controls.portalName;
    const enteredTitle = titleControl.value;
    const normalisedTitle = enteredTitle.trim();

    if (normalisedTitle !== enteredTitle) {
      titleControl.setValue(normalisedTitle);
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this._submitRejected.set(true);
      this.notifications.warning(FORM_INVALID_MESSAGE);
      this.revealFirstInvalidControl();

      return;
    }

    this._submitRejected.set(false);
    this.portals.clearFailures();
    this.writing.set(true);

    this.portals
      .saveSettings(target, this.toRequest(this.hydratedFrom))
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => {
          this.writing.set(false);
        }),
      )
      .subscribe((stored: PortalSettings) => {
        this.hydratedFrom = stored;

        // Two things went wrong while the flag survived, and the second is the serious one. A saved form
        // kept advertising unsaved work, so the screen contradicted the success notification beside it.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        this.notifications.success(SAVE_SUCCEEDED_MESSAGE);
      });
  }

  /**
   * Opens whatever is hiding the first offending control, then focuses it. ⚠ WITHOUT THIS, A REFUSED
   * SUBMIT ON THIS SCREEN LEAVES THE OPERATOR ON THE BUTTON. The shared focus directive cannot always
   * help here, and the reason is specific rather than incidental: it refuses to act when the form is
   * VALID at the moment the submit event fires, and this handler can change a control's validity AFTER
   * that moment - it normalises the title before judging the form, so a value whose validity depends on
   * its trimmed form is settled inside the handler.
   */
  private revealFirstInvalidControl(): void {
    this._collapsed.set(new Set<PortalSettingsSection>());

    const startedOn = this._activeTab();
    const order: readonly PortalSettingsTab[] = [
      startedOn,
      ...TAB_ORDER.filter((tab) => tab !== startedOn),
    ];

    for (const tab of order) {
      this._activeTab.set(tab);
      this.changeDetector.detectChanges();

      const target = this.host.nativeElement.querySelector<HTMLElement>(INVALID_CONTROL_SELECTOR);

      if (target !== null) {
        if (target !== target.ownerDocument.activeElement) {
          target.focus();
        }

        return;
      }
    }

    this._activeTab.set(startedOn);
  }

  protected onCancel(): void {
    this.portals.clearFailures();
    void this.router.navigateByUrl(PORTAL_LIST_PATH);
  }

  /** Opens the delete confirmation. */
  protected onDeleteRequested(): void {
    if (!this.canDelete()) {
      return;
    }

    this._confirmingDelete.set(true);
  }

  /** Dismisses the delete confirmation without deleting. */
  protected onDeleteCancelled(): void {
    this._confirmingDelete.set(false);
  }

  /**
   * Deletes the portal once confirmed. The refusal worth naming is the last-remaining-portal one, which
   * arrives as a state conflict carrying a published code.
   */
  protected onDeleteConfirmed(): void {
    this._confirmingDelete.set(false);

    const target = this._portalId();

    if (target === undefined || !this.canDelete()) {
      return;
    }

    this.portals.clearFailures();

    this.portals
      .deletePortal(target)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS THE OPERATOR TO CONFIRM
        // DISCARDING EDITS TO A PORTAL THAT NO LONGER EXISTS. The probe reads `dirty && saving() ===
        // false`, and a delete is not a save, so an operator who typed something and then deleted the
        // portal would be prompted about the typing on the way out.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        this.notifications.success(DELETE_SUCCEEDED_MESSAGE, true);

        // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
        // retires notifications on a completed navigation, and this one is raised in the same task as the
        // navigation below, so it was swept before it could be painted.
        this.notifications.retainAcrossNavigation();

        // ⚠ THE ADDRESS IS REPLACED RATHER THAN PUSHED. The screen being left describes a record that no
        // longer exists, so leaving a history entry for it would offer the browser’s Back button as a route
        // to a settings form for a deleted portal - and the unsaved-entry gate reads the replacement as an
        // application-initiated departure, so it does not question a navigation nobody chose.
        void this.router.navigateByUrl(PORTAL_LIST_PATH, { replaceUrl: true });
      });
  }

  // -------------------------------------------------------------------------
  // Presenting a failure
  // -------------------------------------------------------------------------

  /**
   * Describes a failure of the settings read or write. A `403` HERE IS THE HOST-ONLY-FIELD RULE, NOT A
   * SESSION PROBLEM. The legacy screen compared six host-owned members — the hosting fee, the disk space,
   * the page quota, the user quota, the site-log retention and the expiry date — against the stored
   * portal for any caller without the host account, and threw a bare exception on the first difference.
   *
   * @param failure The classified failure.
   * @returns The sentence to announce.
   */
  private describeSaveFailure(failure: PortalFailure): string {
    if (failure.status === HTTP_FORBIDDEN) {
      return HOST_FIELDS_REFUSED_MESSAGE;
    }

    const conflict = conflictMessage(failure.conflictCode);

    if (conflict !== null) {
      return conflict;
    }

    return problemMessage(failure.problem, statusMessage(failure.status));
  }

  /**
   * Describes a failure of the delete. The refusal this must name is the last-remaining-portal one, which
   * the server publishes as a state conflict with a code; its wording comes from the shared conflict
   * vocabulary so that it reads identically wherever it is reported.
   *
   * @param failure The classified failure.
   * @returns The sentence to announce.
   */
  private describeDeleteFailure(failure: PortalFailure): string {
    const conflict = conflictMessage(failure.conflictCode);

    if (conflict !== null) {
      return conflict;
    }

    return problemMessage(failure.problem, statusMessage(failure.status));
  }

  // -------------------------------------------------------------------------
  // Hydration and the request
  // -------------------------------------------------------------------------

  /**
   * Moves a settings resource into the form. Every conversion here is one of the measured hydration
   * rules, and each is named at its own helper.
   *
   * @param source The resource as received.
   */
  private hydrate(source: PortalSettings): void {
    this.hydratedFrom = source;

    this.form.setValue({
      portalName: source.portalName === null ? '' : source.portalName,
      description: source.description === null ? '' : source.description,
      keyWords: source.keyWords === null ? '' : source.keyWords,
      footerText: source.footerText === null ? '' : source.footerText,
      bannerAdvertising: source.bannerAdvertising,
      userRegistration: source.userRegistration,
      splashTabId: wirePageToSelected(source.splashTabId),
      homeTabId: wirePageToSelected(source.homeTabId),
      loginTabId: wirePageToSelected(source.loginTabId),
      userTabId: wirePageToSelected(source.userTabId),
      administratorId: wireAdministratorToSelected(source.administratorId),
      timeZoneOffset: numberToText(source.timeZoneOffset),
      currency: source.currency === null ? '' : source.currency,
      defaultLanguage: source.defaultLanguage === null ? '' : source.defaultLanguage,
      expiryDate: instantToDateInput(source.expiryDate),
      hostFee: numberToText(source.hostFee),
      hostSpace: numberToText(source.hostSpace),
      pageQuota: numberToText(source.pageQuota),
      userQuota: numberToText(source.userQuota),
    });

    this.form.markAsPristine();
    this.form.markAsUntouched();
    this._submitRejected.set(false);
  }

  /**
   * Composes the update request. THE SIX MEMBERS THIS SCREEN DOES NOT SHOW ARE RETURNED UNCHANGED, AND
   * THAT IS REQUIRED RATHER THAN TIDY. The update resource carries the portal's whole editable state and
   * REPLACES every column it names, so a member sent as absent is a member cleared.
   *
   * @param source The resource the form was hydrated from, and the source of every preserved member.
   * @returns The complete request.
   */
  private toRequest(source: PortalSettings): UpdatePortalSettingsRequest {
    const edited = this.form.getRawValue();
    const preserved: PreservedMembers = {
      backgroundFile: source.backgroundFile,
      homeDirectory: source.homeDirectory,
      logoFile: source.logoFile,
      paymentProcessor: source.paymentProcessor,
      processorUserId: source.processorUserId,
      siteLogHistory: source.siteLogHistory,
    };

    return {
      // Site Details.
      portalName: textOrNull(edited.portalName),
      description: textOrNull(edited.description),
      keyWords: textOrNull(edited.keyWords),
      footerText: textOrNull(edited.footerText),

      // Site Marketing, and Security Settings.
      bannerAdvertising: edited.bannerAdvertising,
      userRegistration: edited.userRegistration,

      // Page Management. The selector's sentinel becomes absence on the wire.
      splashTabId: selectedPageToWire(edited.splashTabId),
      homeTabId: selectedPageToWire(edited.homeTabId),
      loginTabId: selectedPageToWire(edited.loginTabId),
      userTabId: selectedPageToWire(edited.userTabId),

      // Other Settings. The offset is a whole number of minutes; a blank box means the portal keeps no
      // explicit offset.
      administratorId: selectedAdministratorToWire(edited.administratorId),
      timeZoneOffset: this.optionalWholeNumber(edited.timeZoneOffset),

      currency: textOrNull(edited.currency),
      defaultLanguage: textOrNull(edited.defaultLanguage),

      // Host Settings. A blank box saves ZERO for the fee and the three quotas, which is
      // the measured legacy default, and absence for the expiry date.
      expiryDate: dateInputToInstant(edited.expiryDate),
      hostFee: textToNumberOrZero(edited.hostFee),
      hostSpace: textToNumberOrZero(edited.hostSpace),
      pageQuota: textToNumberOrZero(edited.pageQuota),
      userQuota: textToNumberOrZero(edited.userQuota),

      // Shown nowhere, returned unchanged.
      ...preserved,

      // Never held, and therefore never returned.
      processorCredentialReference: null,

      // ⚠ THE REVISION THIS SUBMISSION WAS COMPOSED AGAINST, ROUND-TRIPPED VERBATIM, AND THE ONLY MEMBER
      // HERE THAT DESCRIBES NO SETTING. It comes from `source` - the same resource every preserved member
      // above comes from - so it describes exactly the snapshot this payload reconstructs.
      concurrencyToken: source.concurrencyToken,
    };
  }

  private optionalWholeNumber(value: string): number | null {
    const trimmed = value.trim();

    if (trimmed.length === 0) {
      return null;
    }

    const parsed = Number.parseInt(trimmed, 10);

    return Number.isSafeInteger(parsed) ? parsed : null;
  }

  // -------------------------------------------------------------------------
  // Reading the page listing
  // -------------------------------------------------------------------------

  /**
   * Reads the portal's pages once, for all four selectors to share.
   *
   * @param portalId The portal whose pages to read.
   */
  private loadPages(portalId: number): void {
    this.pagesRequest?.unsubscribe();
    this._pageRows.set([]);
    this._pagesFailed.set(false);

    this.pagesRequest = this.pages
      .getByPortal(portalId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (rows: readonly TabListItem[]) => {
          if (this.portalChanged(portalId)) {
            return;
          }
          this.pagesRequest = null;
          this._pageRows.set(rows);
          this._pagesFailed.set(false);
        },
        error: () => {
          if (this.portalChanged(portalId)) {
            return;
          }
          this.pagesRequest = null;
          this._pageRows.set([]);
          this._pagesFailed.set(true);
          this.notifications.warning(PAGES_UNAVAILABLE_MESSAGE);
        },
      });
  }

  /**
   * @param portalId The portal the read was issued for.
   * @returns `true` when the answer must be ignored.
   */
  private portalChanged(portalId: number): boolean {
    return this._portalId() !== portalId;
  }
}
