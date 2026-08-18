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
import { ListReturnStore } from '../../../core/state/list-return.store';
import { PORTAL_LIST_ROUTE } from '../../../core/config/app-routes.config';
import type { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TabService } from '../../../core/services/tab.service';
import {
  NO_PAGE_SELECTED,
  NO_PAGE_SELECTED_LABEL,
  buildPageChoices,
} from '../../../core/utils/page-options.util';

import type { PageOption } from '../../../core/utils/page-options.util';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import {
  conflictMessage,
  fieldErrorMessages,
  problemMessage,
  problemSupportReference,
  statusMessage,
  stripLegacyBreakTags,
  supportReferenceFor,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
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
  | 'payment'
  | 'other'
  | 'host';

/** The tab strip, in the order the legacy section heads declared. */
const TAB_ORDER: readonly PortalSettingsTab[] = ['basic', 'advanced'];

/**
 * Which tab owns each control.
 *
 * ⚠ THIS MAP EXISTS BECAUSE THE PANELS ARE CONDITIONAL RENDERS, NOT HIDDEN SIBLINGS. Only the selected
 * panel is in the document, so there is no way to ask the DOM whether the OTHER tab holds an invalid
 * control - and without an answer to that question a rejected save can name three fields while the screen
 * highlights one and gives no hint that the remaining two exist. Runtime verification reproduced exactly
 * that: a `400` naming `portalName`, `hostFee` and `expiryDate` left the operator on the Basic tab looking
 * at one error, with the other two on a tab that advertised nothing and whose panel was not even rendered.
 *
 * It is derived from the template's own panels and must be kept in step with them; the specs assert that
 * every control in the form appears here exactly once, so a control added to one panel and forgotten here
 * fails the suite rather than silently losing its error marker.
 */
const CONTROL_TAB: Readonly<Record<string, PortalSettingsTab>> = Object.freeze({
  portalName: 'basic',
  description: 'basic',
  keyWords: 'basic',
  footerText: 'basic',
  bannerAdvertising: 'basic',
  userRegistration: 'advanced',
  splashTabId: 'advanced',
  homeTabId: 'advanced',
  loginTabId: 'advanced',
  userTabId: 'advanced',
  administratorId: 'advanced',
  paymentProcessor: 'advanced',
  processorUserId: 'advanced',
  timeZoneOffset: 'advanced',
  currency: 'advanced',
  defaultLanguage: 'advanced',
  expiryDate: 'advanced',
  hostFee: 'advanced',
  hostSpace: 'advanced',
  pageQuota: 'advanced',
  userQuota: 'advanced',
});

/** What the marker on a tab holding rejected fields says to a screen reader. */
const TAB_HAS_ERRORS_LABEL = 'has fields needing attention';

/** No tab is flagged. Hoisted so the identity is stable and the computed does not churn. */
const EMPTY_TAB_SET: ReadonlySet<PortalSettingsTab> = Object.freeze(
  new Set<PortalSettingsTab>(),
) as ReadonlySet<PortalSettingsTab>;

/**
 * The disclosures that start closed. Measured from the markup one head at a time: `dshSite` and
 * `dshSecurity` and `dshPages` declare no `IsExpanded` and so default open, `dshMarketing` declares
 * `IsExpanded="True"`, and `dshOther` and `dshHost` declare `IsExpanded="False"`.
 */
// `payment` joins them because the legacy section head declared `IsExpanded="False"` at
// `Website/admin/Portal/sitesettings.ascx:L294-L295`, exactly as the two below did.
const INITIALLY_COLLAPSED: readonly PortalSettingsSection[] = ['payment', 'other', 'host'];

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

/**
 * The cap on both payment boxes, from the columns themselves: `PortalConfiguration` declares
 * `HasMaxLength(50)` for `PaymentProcessor` and for `ProcessorUserId`.
 *
 * ⚠ WHY THESE TWO CONTROLS EXIST AT ALL. Neither could be configured ANYWHERE in the application, while
 * the role screen still instructs an operator to configure a payment processor - an instruction pointing
 * at a control that did not exist. Both members were already carried by the detail resource, already
 * accepted by the update resource, and already preserved unchanged by this screen's own request composer,
 * so the ONLY thing missing was the affordance.
 *
 * TWO LEGACY AFFORDANCES ARE DELIBERATELY NOT RESTORED, and both omissions are recorded in
 * MIGRATION_NOTES.md rather than left to be discovered:
 *   - The processor was a DROPDOWN (`cboProcessor`, `sitesettings.ascx:L314`) filled from the legacy list
 *     subsystem, which AAP 0.2.2.2 excludes. A text box is what the remaining contract supports.
 *   - The processor PASSWORD box (`txtPassword`, `sitesettings.ascx:L330`) has no counterpart: the settings
 *     contract carries no credential member, so there is nothing to bind and nowhere to send it.
 */
const PROCESSOR_MAX_LENGTH = 50;

/**
 * The bounds of a portal time-zone offset, in minutes, MEASURED FROM THE LEGACY ZONE LIST rather than
 * chosen. `Website/App_GlobalResources/TimeZones.xml` is the file the legacy `cboTimeZone` selector was
 * filled from, and its 58 entries run from `key="-720"` (UTC -12:00) to `key="780"` (UTC +13:00).
 *
 * ⚠ THE DEFECT THIS CLOSES. Replacing a closed selector with a free-text box moved the legality of the
 * value from the LIST to the VALIDATOR - and no validator was added, so the box accepted any integer at
 * all. `99999` was storable, and a portal's whole notion of local time is derived from it.
 */
const TIME_ZONE_MIN_OFFSET = -720;

/** @see TIME_ZONE_MIN_OFFSET */
const TIME_ZONE_MAX_OFFSET = 780;

// THE PAGE-SELECTOR SENTINEL AND ITS WORDING ARE NO LONGER DECLARED HERE. Both are imported from
// `core/utils/page-options.util` above, so this screen and the membership settings screen cannot disagree
// about how "no page" is spelled or how it reads. Re-declaring them locally would shadow the shared
// vocabulary with a second copy that could drift.

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
  payment: 'Payment Settings',
  other: 'Other Settings',
  host: 'Host Settings',
});

/** The labelled-field captions, keyed by the control each legacy label named. */
/**
 * The validation-error key under which a control carries what the SERVER rejected about it.
 *
 * ⚠ THE VALUE IS THE WHOLE MESSAGE LIST, NOT A FLAG, and that is what lets the messages expire by
 * themselves. Angular re-runs a control's validators on every value change and REPLACES its error object
 * with the result, so the moment an operator edits a field the server's complaint about it disappears
 * without anything having to remember to clear it. A flag plus a lookup held elsewhere would survive the
 * edit and go on quoting a rejection of a value that is no longer in the box.
 *
 * The key is deliberately not one of the client rule names: a client rule describes what this screen
 * refuses to send, and this describes what the server refused to accept, which are different claims and
 * must not overwrite one another.
 */
const SERVER_REJECTED_KEY = 'serverRejected';

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
  homeDirectory: 'Home Directory:',
  paymentProcessor: 'Payment Processor:',
  processorUserId: 'Processor UserId:',
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
/*
 * ─────────────────────────────────────────────────────────────────────────────────────────────────
 * DELIBERATELY NOT DONE: restating the metadata character allowance in this screen's help text - QA-9.
 *
 * The register asked for the silent `maxlength` truncation to be disclosed, and a sentence was written,
 * shipped and MEASURED - after which it was removed, because the measurement showed the disclosure already
 * existed. The shared field component authors the bound sentence itself, once, for every bounded field in
 * the application: "At most 475 characters." rendered from the control's own `maxlength`. The added sentence
 * therefore appeared directly beside it, saying the same number in different words, and the shared
 * component's own comment gives the reason that is wrong - the sentence is authored once precisely "so
 * every bounded field in the application states its bound the same way".
 *
 * WHAT THE MEASUREMENT DID FIND, and what is NOT a portal concern: the bound is only PAINTED once the
 * "ⓘ Help" disclosure is expanded; while collapsed it exists solely as a 1×1px visually-hidden paragraph, so
 * a sighted operator sees no allowance hint at all until they open Help. That affects every bounded field on
 * every screen, so it belongs to the shared field component, not to a per-screen string.
 *
 * The two numbers themselves - 475 here, 500 on the create screen, for the same nvarchar(500) columns - are
 * a legacy inconsistency preserved on purpose under AAP 0.9.1: `sitesettings.ascx` declares MaxLength="475"
 * and `signup.ascx` declares maxlength="500". Each screen states the bound it actually enforces, which was
 * verified at runtime on all four fields.
 * ─────────────────────────────────────────────────────────────────────────────────────────────────
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
  homeDirectory: 'Enter the Home Directory for this site',
  paymentProcessor: 'The Payment Processor used to handle payments on the site.',
  processorUserId: 'The UserId for the Payment Processor.',
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
 * What an offset outside the legacy zone range is told. Names BOTH bounds, so the rule is stated once and
 * in full rather than revealed a bound at a time. @see TIME_ZONE_MIN_OFFSET
 */
const TIME_ZONE_OUT_OF_RANGE_MESSAGE =
  'Enter an offset between \u2212720 and 780 minutes, which is UTC \u221212:00 to UTC +13:00.';

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
 *
 * ⚠ #20 — It opens with the shared denial stem, "You do not have permission to", for the reason recorded on
 * the portal form's own copy of this refusal: one vocabulary across every denial this application authors.
 */
const HOST_FIELDS_REFUSED_MESSAGE =
  'You do not have permission to change the hosting fee, the disk space, the page quota, the user quota '
  + 'or the expiry date, which only a host account may change. Those fields were not saved.';

/**
 * What a refused READ of the settings says.
 *
 * ⚠ IT NAMES NO FIELDS AND NO SAVE. {@link HOST_FIELDS_REFUSED_MESSAGE} was announced for every `403` on
 * this screen, including the one that comes back from simply opening a portal the caller may not see - so
 * an operator who had submitted nothing was told that the hosting fee, disk space, page quota, user quota
 * and expiry date "were not saved". Every clause of that was false: no save was attempted, those fields
 * were never rendered, and the cause was not the host-only-field rule.
 */
const SETTINGS_READ_REFUSED_MESSAGE =
  'You are not permitted to view this portal’s settings, so nothing could be loaded.';

// A PAGE-LEVEL SENTENCE FOR A REJECTED SUBMISSION USED TO BE DECLARED HERE, AND IT IS GONE ON PURPOSE.
// Measured across the eighteen forms in this application, sixteen answered a client-blocked submit by
// marking their controls touched and moving focus to the first offender, so what a reader hears is the
// specific, actionable field message. This screen additionally raised a summary toast AND rendered the same
// sentence into a `role="alert"` paragraph of its own - a second assertive owner for news the focused field
// already carries. The contract is now the majority one, with no page-level restatement, so there is
// nothing left to word. See `onSubmit` below.

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

  // ⚠ RESTORED. See {@link PROCESSOR_MAX_LENGTH}.
  paymentProcessor: FormControl<string>;
  processorUserId: FormControl<string>;

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
  'backgroundFile' | 'homeDirectory' | 'logoFile' | 'siteLogHistory'
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

/**
 * Bounds the offset to the range the legacy selector offered. @see TIME_ZONE_MIN_OFFSET
 *
 * Runs only once the SHAPE holds: reporting a range for something that is not a number at all would state
 * the second rule before the first is satisfied, which is the sequencing defect this project has already
 * had to correct on the alias field.
 *
 * @param control The offset control.
 * @returns The failure, or null when the offset is within range or not yet a number.
 */
function timeZoneRangeCheck(control: AbstractControl): ValidationErrors | null {
  const raw: unknown = control.value;

  if (typeof raw !== 'string') {
    return null;
  }

  const trimmed = raw.trim();

  if (trimmed.length === 0 || INTEGER_PATTERN.test(trimmed) === false) {
    return null;
  }

  const minutes = Number(trimmed);

  return minutes < TIME_ZONE_MIN_OFFSET || minutes > TIME_ZONE_MAX_OFFSET
    ? { timeZoneRange: TIME_ZONE_OUT_OF_RANGE_MESSAGE }
    : null;
}

// The page options behind the four selectors
// * none-specified TRUE → prepend a synthetic option, `TabID = -1`, named `"<" + None_Specified + ">"`, and
// SELECTABLE; * hidden TRUE → invisible pages ARE included; * deleted FALSE → recycled pages are excluded;
// * URL FALSE → only pages whose type is Normal, and `GetURLType` returns Normal exactly when the page's
// URL is empty; * authorised FALSE → no role filtering is applied; * and unconditionally → administration
// pages are excluded.

/**
 * Builds the shared option list for all four page selectors, by prepending this screen's own "none
 * specified" option to the shared page choices. The admission rules, the ordering and the indent live in
 * `core/utils/page-options.util.ts`, which the membership settings screen reads the same way.
 *
 * @param rows Every page of the portal, as received.
 * @param adminTabId The portal's administration page, or `null` when it is not yet known.
 * @param retain Page references the portal currently holds, so a stored choice that the filter would
 * otherwise hide still appears.
 * @returns The options, beginning with the selectable "none specified" entry.
 */
function buildPageOptions(
  rows: readonly TabListItem[],
  adminTabId: number | null,
  retain: readonly number[],
): readonly PageOption[] {
  return [
    { value: NO_PAGE_SELECTED, label: NO_PAGE_SELECTED_LABEL },
    ...buildPageChoices(rows, adminTabId, retain),
  ];
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
    // For the branch that says the settings could not be read. @see settingsUnreadable
    EmptyStateComponent,
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

  /** Where the listing stood when the operator left it. */
  private readonly listReturn = inject(ListReturnStore);
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
   * A counter bumped on every form status change, so a computed can depend on validity.
   *
   * The value itself is meaningless; only its changing matters. See the `statusChanges` subscription.
   */
  private readonly _formRevision = signal(0);

  /**
   * Whether the settings failure now being reported belongs to a SAVE this screen requested, rather than
   * to the read that populates it.
   *
   * ⚠ A PLAIN FIELD RATHER THAN A SIGNAL, DELIBERATELY, and the reason is timing. The store keeps one
   * failure slice for both operations, so the operation has to be recorded by whoever asked. It cannot be
   * derived from {@link writing} because that is lowered in `finalize`, which runs before the announcing
   * effect does - by the time the effect looks, a refused save is indistinguishable from a refused read.
   * It is raised at the submit and lowered wherever a read is requested, which are the only two places
   * that know.
   */
  private settingsFailureFromWrite = false;

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

    // LENGTH ONLY. The legacy processor control was a SELECTOR filled from the excluded list subsystem, so
    // there is no closed value set to validate against - the same position the currency and language boxes
    // are already in, and for the same reason.
    paymentProcessor: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PROCESSOR_MAX_LENGTH)],
    }),
    processorUserId: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PROCESSOR_MAX_LENGTH)],
    }),

    timeZoneOffset: new FormControl('', {
      nonNullable: true,
      validators: [timeZoneOffsetCheck, timeZoneRangeCheck],
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
  protected readonly tabHasErrorsLabel = TAB_HAS_ERRORS_LABEL;

  /**
   * Which tabs currently hold an invalid control, so a tab can advertise errors that are not on screen.
   *
   * Recomputed from the form's status stream rather than from a signal the form does not expose, and only
   * while a submit has actually been rejected - marking tabs before the operator has submitted anything
   * would flag every mandatory empty field on a form they have not filled in yet.
   */
  protected readonly tabsWithErrors = computed<ReadonlySet<PortalSettingsTab>>(() => {
    if (!this._submitRejected()) {
      return EMPTY_TAB_SET;
    }

    // Depend on the status revision so this recomputes as validity changes.
    this._formRevision();

    const flagged = new Set<PortalSettingsTab>();

    for (const [name, tab] of Object.entries(CONTROL_TAB)) {
      if (this.form.get(name)?.invalid === true) {
        flagged.add(tab);
      }
    }

    return flagged;
  });

  /**
   * Whether `tab` holds an invalid control.
   *
   * @param tab The tab to test.
   * @returns True when at least one control on that tab is invalid.
   */
  protected tabHasErrors(tab: PortalSettingsTab): boolean {
    return this.tabsWithErrors().has(tab);
  }

  /**
   * The control-to-tab map, exposed so the suite can prove it covers every control in the form.
   *
   * The map is hand-maintained against the template's panels - it has to be, because the unselected panel
   * is not in the document to be inspected - so the completeness check is the only thing standing between a
   * newly added control and a silently missing error marker.
   */
  private readonly controlTabForTesting = CONTROL_TAB;
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
    processor: PROCESSOR_MAX_LENGTH,
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
    // Lowered here because the next thing that can fail is a READ. See the member's own note for why the
    // distinction is not cosmetic.
    this.settingsFailureFromWrite = false;

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

      // ⚠ THE BRIDGE FROM THE FORM'S OBSERVABLE VALIDITY TO THE SIGNAL GRAPH. A reactive form publishes
      // status through an observable and not through a signal, so a computed that reads `control.invalid`
      // has nothing to depend on and would never recompute. Bumping a counter here is what makes the
      // per-tab error markers track validity as the operator types.
      this._formRevision.update((revision) => revision + 1);
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

  /**
   * The portal's home directory, for the read-only display. @see homeDirectoryNotice
   *
   * An unread or absent path renders as the empty string rather than as a marker: the box is a text input,
   * so a dash inside it would read as a stored VALUE of one character.
   */
  protected readonly homeDirectoryText = computed<string>(() => {
    const held = this.portals.settings();

    if (held === null || held.homeDirectory === null) {
      return '';
    }

    return held.homeDirectory;
  });

  /**
   * Whether the settings read has been attempted and left nothing to show.
   *
   * ⚠ THE MEASURED DEFECT THIS CLOSES. The branch chain in the template ran
   * `no portal id -> loading -> settings present` and had NO final alternative, so a read that failed left
   * the body of the page completely empty: the page header, the error banner and then a measured 656px of
   * nothing, with no statement of what had happened and no way to try again. The banner alone is not enough
   * - it sits above the fold of an empty region and reads as a transient complaint rather than as the
   * reason the screen is blank.
   *
   * Requires the portal identifier to be usable and the read to be settled, so this never claims a failure
   * during the first load or on an address that named no portal - each of those has its own branch.
   */
  protected readonly settingsUnreadable = computed<boolean>(
    () =>
      this.hasPortalId() &&
      this.loading() === false &&
      this.portals.settings() === null,
  );

  /** What the unreadable state says. The REASON stays in the banner, which owns the support reference. */
  protected readonly settingsUnreadableMessage =
    'The settings for this site could not be read, so there is nothing to edit here yet.';

  /** The way out of the unreadable state. */
  protected readonly settingsRetryLabel = 'Try again';

  /** Re-reads the settings after a failed read. */
  protected retrySettingsRead(): void {
    const portalId: number | undefined = this._portalId();

    if (portalId === undefined) {
      return;
    }

    // Only the settings read is retried. The other three reads this screen issues on arrival have their own
    // failure surfaces and their own recovery, and re-issuing them here would turn one retry into four
    // requests, three of which may already have succeeded.
    this.portals.clearFailures();
    this.portals.loadSettings(portalId);
  }

  /** Why the path above cannot be changed here. */
  protected readonly homeDirectoryNotice =
    'The home directory is fixed once a site is created and cannot be changed here.';

  /**
   * Why no processor credential is offered. Stated on the screen rather than left as an absence, because an
   * operator who knows the legacy screen had a password box needs to know where it went.
   */
  protected readonly processorCredentialNotice =
    'The processor password is not held or changed here. Set it with your payment provider.';

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
  protected controlId(
    // `homeDirectory` is neither a form control nor the identifier display: it is the read-only path
    // box, which needs an id to be label-associated exactly as every real control does.
    name: keyof PortalSettingsFormModel | 'guid' | 'homeDirectory',
  ): string {
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

    // ⚠ THE SERVER'S COMPLAINT LEADS, because it is the one that just happened. A client rule would have
    // blocked the submit before it was sent, so if a server message is present the operator has already
    // passed every local rule and the server is telling them something local validation could not know.
    const rejectedByServer: unknown = errors[SERVER_REJECTED_KEY];

    if (Array.isArray(rejectedByServer)) {
      for (const entry of rejectedByServer) {
        if (typeof entry === 'string' && entry.length > 0) {
          messages.push(entry);
        }
      }
    }

    // ⚠ NO REQUIRED RULE IS REPORTED, BECAUSE NO CONTROL ON THIS SCREEN DECLARES ONE. A branch here
    // reported `"Site Title is required."` against a `Validators.required` on the title, mirroring a
    // `NotEmpty()` the update contract carried; both are withdrawn as a parity break.
    for (const key of [
      'expiryDateType',
      'hostFeeType',
      'wholeNumber',
      'timeZoneOffset',
      'timeZoneRange',
    ]) {
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
      // ⚠ THE ONE VALIDATION-SUMMARY CONTRACT, AND THIS SCREEN USED TO BREAK IT TWICE. Measured across the
      // eighteen forms, sixteen answered a client-blocked submit by marking their controls touched and
      // letting focus land on the first invalid one, so the statement a reader hears is the specific,
      // actionable field message. Two forms additionally raised a page-level summary toast, and this one
      // also rendered the same sentence into a `role="alert"` paragraph of its own - a second assertive
      // owner beside the shared banner, for news the field being focused already carries. The summary is
      // therefore gone from both: the contract is touched controls, focus on the first offender, and no
      // page-level restatement.
      this.form.markAllAsTouched();
      this._submitRejected.set(true);
      this.revealFirstInvalidControl();

      return;
    }

    this._submitRejected.set(false);
    this.clearServerFieldErrors();
    this.portals.clearFailures();
    this.settingsFailureFromWrite = true;
    this.writing.set(true);

    // ⚠ THE STORE COMPLETES WITHOUT EMITTING WHEN THE SAVE IS REFUSED, so `next` firing is the only
    // signal that it succeeded. Read in `complete` rather than in `finalize`, because a teardown also runs
    // on destroy and this must not act on a screen that is going away.
    let succeeded = false;

    this.portals
      .saveSettings(target, this.toRequest(this.hydratedFrom))
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => {
          this.writing.set(false);
        }),
      )
      .subscribe({
        next: (stored: PortalSettings) => {
          succeeded = true;
          this.hydratedFrom = stored;

          // Two things went wrong while the flag survived, and the second is the serious one. A saved form
          // kept advertising unsaved work, so the screen contradicted the success notification beside it.
          this.form.markAsPristine();
          this.form.markAsUntouched();

          this.notifications.success(SAVE_SUCCEEDED_MESSAGE);
        },
        complete: () => {
          if (!succeeded) {
            this.presentRefusedSave();
          }
        },
      });
  }

  /**
   * Routes a refused save onto the controls it was refused about, then brings the first of them into view
   * and into focus.
   *
   * ⚠ THIS CLOSES THREE FAULTS AT ONCE, AND THEY SHARE ONE CAUSE: the server's per-field messages reached
   * the banner and stopped there. No control was marked invalid, so nothing carried `aria-invalid` and no
   * message appeared beside any field; two of the three fields a settings refusal typically names live in
   * a disclosure on the OTHER tab, so they were not even rendered and the operator was told about fields
   * they could not see; and focus stayed on the submit button. Making the form genuinely invalid fixes all
   * three through machinery this screen already owns - the shared field component derives `aria-invalid`
   * and `aria-describedby` from its messages, and {@link revealFirstInvalidControl} already opens every
   * disclosure and walks both tabs looking for `.ng-invalid`.
   *
   * A refusal that names no field at all - a `403`, a `409`, a `500` - leaves the form untouched and is
   * reported by the banner alone, which is correct: there is nothing to point at.
   */
  private presentRefusedSave(): void {
    const failure: PortalFailure | null = this.portals.settingsFailure();

    if (failure === null) {
      return;
    }

    if (!this.applyServerFieldErrors(failure.problem)) {
      return;
    }

    this._submitRejected.set(true);
    this.revealFirstInvalidControl();
  }

  /**
   * Marks every control the server named as invalid, carrying that field's messages.
   *
   * @param problem The refusal document, or null when the failure carried none.
   * @returns True when at least one control was named, so the caller knows whether to reveal anything.
   */
  private applyServerFieldErrors(problem: ProblemDetails | null): boolean {
    let named = false;

    for (const controlName of Object.keys(this.form.controls)) {
      const messages: readonly string[] = fieldErrorMessages(problem, controlName);

      if (messages.length === 0) {
        continue;
      }

      const control: AbstractControl | null = this.form.get(controlName);

      if (control === null) {
        continue;
      }

      // Spread rather than replace: a control can be reporting a client rule as well, and dropping it
      // would let a locally invalid value look acceptable the moment the server's message expires.
      control.setErrors({ ...(control.errors ?? {}), [SERVER_REJECTED_KEY]: messages });
      // Touched is what makes `messagesFor` willing to speak: an operator who never visited the field is
      // still entitled to see why the server rejected what was sent on their behalf.
      control.markAsTouched();
      named = true;
    }

    return named;
  }

  /**
   * Drops every server-supplied error before a fresh submit, so a field the server no longer objects to
   * does not stay marked invalid from the previous attempt.
   */
  private clearServerFieldErrors(): void {
    for (const controlName of Object.keys(this.form.controls)) {
      const control: AbstractControl | null = this.form.get(controlName);
      const errors: ValidationErrors | null = control?.errors ?? null;

      if (control === null || errors === null || !(SERVER_REJECTED_KEY in errors)) {
        continue;
      }

      const remaining: ValidationErrors = { ...errors };
      delete remaining[SERVER_REJECTED_KEY];

      control.setErrors(Object.keys(remaining).length > 0 ? remaining : null);
    }
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
    // `navigate` rather than `navigateByUrl`, because only the former accepts the listing coordinate this
    // screen must hand back - see ListReturnStore.
    void this.router.navigate([PORTAL_LIST_ROUTE], {
      queryParams: this.listReturn.coordinateFor(PORTAL_LIST_ROUTE),
    });
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
        void this.router.navigate([PORTAL_LIST_ROUTE], {
          queryParams: this.listReturn.coordinateFor(PORTAL_LIST_ROUTE),
          replaceUrl: true,
        });
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
      // ⚠ ONLY A REFUSED SAVE MAY BE DESCRIBED AS ONE. `HOST_FIELDS_REFUSED_MESSAGE` ends "Those fields
      // were not saved.", and this arm was reached by a refused READ as well - so simply opening a portal
      // this caller may not see announced that five named fields had failed to save, on a screen where
      // nothing had been submitted and, on a refused read, nothing had even been rendered to submit.
      return this.settingsFailureFromWrite
        ? HOST_FIELDS_REFUSED_MESSAGE
        : SETTINGS_READ_REFUSED_MESSAGE;
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
      paymentProcessor: source.paymentProcessor === null ? '' : source.paymentProcessor,
      processorUserId: source.processorUserId === null ? '' : source.processorUserId,
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
   * Composes the update request. THE FOUR MEMBERS THIS SCREEN DOES NOT SHOW ARE RETURNED UNCHANGED, AND
   * THAT IS REQUIRED RATHER THAN TIDY. There were six; the two payment members are now edited on this
   * screen and so are composed from the form like every other shown member. The update resource carries the portal's whole editable state and
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

      // Payment Settings, RESTORED and therefore edited rather than preserved. @see PROCESSOR_MAX_LENGTH
      paymentProcessor: textOrNull(edited.paymentProcessor),
      processorUserId: textOrNull(edited.processorUserId),

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
        error: (cause: unknown) => {
          if (this.portalChanged(portalId)) {
            return;
          }
          this.pagesRequest = null;
          this._pageRows.set([]);
          this._pagesFailed.set(true);

          // The cause is taken rather than ignored, so this refusal can still quote its support reference.
          // Written as `error: () => ...` it discarded the correlation identifier the server sent, and
          // `warning()` cannot carry one either - hence `notify()`.
          this.notifications.notify(
            'warning',
            PAGES_UNAVAILABLE_MESSAGE,
            supportReferenceFor(cause),
          );
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
