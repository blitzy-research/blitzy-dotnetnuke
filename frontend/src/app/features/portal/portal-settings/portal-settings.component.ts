//
// Portal settings screen — the Angular 19 replacement for the DotNetNuke 4.9.0
// "Site Settings" administration page.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE IS
// ---------------------------------------------------------------------------
// The component behind `/portals/:portalId/settings`. It is a ROUTED component:
// `app.routes.ts` reaches it with `loadComponent` and `app.config.ts` enables
// `withComponentInputBinding()`, so the only value the router can hand it is the
// `portalId` path segment. Everything else it needs, it asks the state layer for.
//
// It therefore CONSUMES `core/state/portal.store.ts` rather than accepting its data
// through inputs. That direction is not a preference: a directly-routed component
// whose data arrives by input has no supplier, so it would render an empty form for
// ever. The store already owns a portal-settings slice — the signal, its loading
// flag, its classified failure, and the load/save/delete commands — and the
// migration discipline confines Angular services to API communication, which is why
// the composition lives there and the screen only reads it.
//
// ---------------------------------------------------------------------------
// PROVENANCE
// ---------------------------------------------------------------------------
//   Website/admin/Portal/sitesettings.ascx            568 lines, 13 section heads,
//                                                     44 labelled fields, 2 validators
//   Website/admin/Portal/SiteSettings.ascx.vb         hydration, the host-field rule,
//                                                     the super-user gate, the delete
//   Website/admin/Portal/App_LocalResources/
//     SiteSettings.ascx.resx                          the authoritative wording
//   Website/App_GlobalResources/SharedResources.resx  the shared wording
//   Library/Components/Portal/PortalInfo.vb           the CLR types of every column
//   Library/Components/Portal/PortalController.vb     the replaced write contract
//   Library/Components/Shared/Globals.vb  L813-L849   GetPortalTabs, measured verbatim
//   Library/Components/Shared/Null.vb                 the sentinel contract, both sides
//   Website/release.config                            the Option Strict asymmetry
//
// MIGRATION: THE SITE WIZARD COLLAPSES INTO THIS TABBED SCREEN AND PRODUCES NO ROUTE.
//   `Website/admin/Portal/sitewizard.ascx` and `SiteWizard.ascx.vb` are reference
//   inputs only. There is no wizard route, no step navigator, no "next"/"back" pair
//   and no step state anywhere below: the two surviving section groups are presented
//   as two tabs a caller may visit in any order, which is what the legacy section
//   heads already allowed on this page. Nothing is deferred — the wizard's remaining
//   subject matter is these same portal columns.
//
// MIGRATION: THE PORTAL-TEMPLATE SCREEN PRODUCES NO TARGET FILE AND NO ENDPOINT.
//   `Website/admin/Portal/template.ascx` and `Template.ascx.vb` are reference inputs
//   only. No template selector, no export action and no import action appears here,
//   because the API publishes no portal-template resource to call.
//
// MIGRATION: THE MARKETING AND ADVERTISING FIELDS ARE DROPPED, AND ONE OF THEM IS THE
//   REASON NO RESOURCE VALUE IS EVER RENDERED AS MARKUP. `cboSearchEngine`,
//   `txtSiteMap`, `txtVerification` and their three submit handlers go with the
//   excluded search-provider family. `plAdvertising`/`lblAdvertising` go further:
//   `Advertising.Text` in this screen's own resource file holds a live third-party
//   advertising SCRIPT block, stored XML-escaped so a naive search clears it wrongly.
//   Every string this component surfaces is plain text, and the paired template
//   interpolates only — no raw-markup binding, no sanitiser bypass, no trusted-markup
//   wrapper. See also the note on re-authored help text below.
//
// MIGRATION: APPEARANCE, SKINNING AND THE STYLESHEET EDITOR ARE DROPPED. All six
//   appearance labels, `txtStyleSheet`, the save/restore stylesheet actions and both
//   upload controls belong to the skinning subsystem and the file system, neither of
//   which is in scope. `dshStylesheet` is consequently the one top-level section group
//   of the three that survives as nothing at all, which is why this screen has two
//   tabs and not three.
//
// MIGRATION: PAYMENT SETTINGS ARE DROPPED — no payment resource exists to call. The
//   processor credential is additionally never held, never logged and never displayed:
//   the settings projection does not carry it, and the update contract's credential
//   member is sent as absent, which the server reads as "leave the stored reference
//   alone" rather than as "clear it".
//
// MIGRATION: USABILITY AND SSL ARE NOT CARRIED FORWARD, AND THE REASON IS STRUCTURAL
//   RATHER THAN A JUDGEMENT ABOUT VALUE. The inline-editor flag, the three control-panel
//   modes, and the four SSL members are not columns of the portal at all. There is NO
//   portal-settings table: `Library/Components/Portal/PortalSettings.vb:L923` resolves
//   `GetSiteSettings(PortalId)` to `GetModuleSettings(...)` of the "Site Settings"
//   MODULE instance, and L970 writes through `UpdateModuleSetting`; a case-insensitive
//   search for a portal-settings table across all 88 upgrade scripts, in all four
//   object-naming forms, returns nothing. Those seven keys were module-setting rows.
//   No key/value settings screen is built here and no key/value request is composed —
//   this screen reads and writes the whole projected settings resource and nothing else.
//
// MIGRATION: DEFAULT LANGUAGE, SITE-LOG RETENTION, HOME DIRECTORY AND THE PREMIUM-MODULE
//   LIST ARE DROPPED — the localisation, logging-provider and file-system subsystems are
//   all out of scope. Four of those columns are nonetheless PRESERVED ON THE WIRE, for
//   the reason set out under the round-trip note on the request builder: the update
//   resource replaces every column it carries, so a member this screen does not show
//   must still be returned unchanged or showing it would be the only way to keep it.
//
// MIGRATION: AN OPTION-STRICT-OFF COERCION IS MADE EXPLICIT. The legacy pages compiled
//   with `strict="false"` (`Website/release.config:L125`), and `cmdUpdate_Click`
//   exploited it: `Dim intUserQuota As Double = 0` is assigned from `Integer.Parse` and
//   then passed as the `Integer` `UserQuota` argument. Neither narrowing was written
//   down. Here the user quota is an integer from its declaration through to the request,
//   and the blank-to-zero step is a named, tested conversion rather than a widening the
//   compiler performed silently.
//
// MIGRATION: THE 27-POSITIONAL WRITE CONTRACT IS REPLACED BY A REQUEST OBJECT.
//   `PortalController.vb:L1568` declared `UpdatePortalInfo` with twenty-seven ordered
//   parameters, so an argument could be transposed with its neighbour and still compile.
//   This screen composes a named request instead, and the legacy `ByRef` status
//   arguments — thirty such sites across the in-scope tree — are replaced by an HTTP
//   status plus a failure code, which is what the handlers below branch on.
//
// MIGRATION: THE WIDENED FEE AND SPACE PARAMETERS ARE NOT CARRIED FORWARD.
//   `UpdatePortalInfo` declared `HostFee As Double` and `HostSpace As Double` while
//   `PortalInfo.HostFee` is `Single` and `PortalInfo.HostSpace` is `Integer`. The wire
//   contract follows the ENTITY, not the widened signature: the fee is a decimal amount
//   and the disk space is a whole number of megabytes.
//
// MIGRATION: THE NON-SUPER-USER HOST-FIELD REFUSAL BECOMES AN HTTP 403.
//   `SiteSettings.ascx.vb:L771-L782` compared six host-owned members against the stored
//   portal and, on any difference, executed a bare `Throw New System.Exception` — an
//   unhandled fault, presented as a broken page. The server now answers `403`, and this
//   screen presents it as the policy outcome it is: the host-only fields cannot be
//   changed. It is never presented as an expired session and never redirects to a
//   sign-in screen.
//
// MIGRATION: THE SECTION TOGGLE'S NEGATIVE TAB INDEX IS REVERSED. The legacy section
//   head rendered its toggle with `tabIndex="-1"`, which put every collapsible group
//   beyond keyboard reach. The paired template uses real buttons carrying
//   `aria-expanded` and `aria-controls`. That is a faithful translation of a control
//   that already named its target element, and the negative index is treated as the
//   accessibility defect it was rather than reproduced.
//
// MIGRATION: RESOURCE HELP TEXT IS RE-AUTHORED AS TEMPLATE MARKUP WHERE IT NEEDS
//   STRUCTURE. Legacy help values are untrusted markup, so they are surfaced as plain
//   text through the shared field wrapper; where a sentence genuinely needed emphasis or
//   a list, the paired template expresses it in real elements instead of injecting the
//   stored string.
//
// MIGRATION: MULTI-LINE FIELDS USE A PLAIN TEXT AREA. The legacy rich-text provider is
//   excluded, so the description and keywords fields are plain multi-line inputs at
//   their measured widths.
//
// MIGRATION: DELETING A PORTAL NO LONGER TOUCHES THE FILE SYSTEM. The legacy handler
//   removed the portal's folders recursively before the comment that began its database
//   work. The file system is out of scope; the delete performed here removes database
//   references only.
//
// MIGRATION: THE TWO-LEVEL SECTION HIERARCHY BECOMES TWO TABS WITH NESTED DISCLOSURES.
//   Of the thirteen legacy section heads exactly three carry `IncludeRule="True"` and are
//   therefore top level: Basic Settings (L14), Advanced Settings (L204) and the
//   Stylesheet Editor (L539). The third is dropped with skinning, so two tabs remain,
//   and the ten nested heads become disclosures inside them, each keeping the
//   expanded-or-collapsed state the markup declared.
//
// MIGRATION: MARKETING SURVIVES WITH EXACTLY ONE FIELD. Banner advertising sits inside
//   the marketing block (`sitesettings.ascx:L118-L126`), so the section is retained
//   rather than dropped whole, carrying that single field and nothing else.
//
// MIGRATION: OTHER SETTINGS SURVIVES WITH EXACTLY TWO FIELDS. The administrator
//   selector (L391/L395) and the portal time zone (L411/L415) sit inside the other-settings
//   block, so it too is retained partially rather than dropped whole.
//
// MIGRATION: NO CACHE INVALIDATION HAPPENS ON THIS SIDE. The legacy update called
//   `DataCache.ClearPortalCache(PortalId, True)` — the portal controller alone accounts
//   for thirteen of the one hundred and sixteen in-scope cache call sites. Caching is a
//   server concern behind a named-key service with explicit invalidation, so there is no
//   cache map, no expiry and no staleness flag below.
//

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
 * The two surviving top-level section groups, in markup order.
 *
 * Deliberately NOT exported, as with every other type declared here. The component class
 * is this file's entire public surface: the screen is reached by lazy route load and
 * nothing outside it names a tab or a disclosure, so exporting these would widen the
 * surface without a consumer and invite a second screen to bind to a vocabulary that is
 * private to this one.
 */
type PortalSettingsTab = 'basic' | 'advanced';

/**
 * The nested disclosures that survive, in markup order within their tab.
 *
 * Six of the ten legacy nested section heads are gone with their subject matter:
 * appearance, payment, usability and SSL entirely, and marketing and other-settings
 * only partially — which is why those two appear here.
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
 * The disclosures that start closed.
 *
 * Measured from the markup one head at a time: `dshSite` and `dshSecurity` and
 * `dshPages` declare no `IsExpanded` and so default open, `dshMarketing` declares
 * `IsExpanded="True"`, and `dshOther` and `dshHost` declare `IsExpanded="False"`.
 * Only the last two are listed, because a set of the closed ones is the smaller and
 * more obviously correct statement of the same fact.
 */
const INITIALLY_COLLAPSED: readonly PortalSettingsSection[] = ['other', 'host'];

// ---------------------------------------------------------------------------
// Field widths, measured attribute by attribute from sitesettings.ascx
// ---------------------------------------------------------------------------
//
// Every one of these is a legacy `MaxLength` on the control named beside it. They are
// reproduced because Minimal Change Clause item 4 requires validation rules to match,
// and because a client that accepts more than the column can hold turns a typing
// mistake into a server round trip. Casing in the source is inconsistent — the fee box
// spells `maxlength` and `width` in lower case while its neighbours do not — which is
// why each was read individually rather than pattern-matched.

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
 * Terminal width of `Portals.Currency`: `[char] (3) NULL`.
 *
 * ⚠ MEASURED FROM THE COLUMN AND THE SERVER, NOT FROM A LEGACY `MaxLength`, because the legacy
 * control was a SELECTOR (`cboCurrency`) and carried none — a selector cannot overflow its column.
 * `UpdatePortalRequestValidator` declares `MaximumLength(3)` on this member, so this is the same
 * three the server enforces, restated here so a typing mistake is a message beside the field rather
 * than a round trip.
 */
const CURRENCY_MAX_LENGTH = 3;

/**
 * Terminal width of `Portals.DefaultLanguage`: `nvarchar(10) NOT NULL`.
 *
 * Measured from the column and from the server's own `MaximumLength(10)`, for the same reason as
 * the currency above: the legacy control was a culture selector and declared no length.
 */
const DEFAULT_LANGUAGE_MAX_LENGTH = 10;

/** `txtHostSpace`, `txtPageQuota` and `txtUserQuota`, all `MaxLength="6"`. */
const QUOTA_MAX_LENGTH = 6;

// ---------------------------------------------------------------------------
// The page-selector sentinel
// ---------------------------------------------------------------------------

/**
 * The identifier the legacy synthetic "none specified" option carried.
 *
 * `Globals.vb:L816-L821` builds that option with `TabID = -1`, and it is SELECTABLE
 * rather than a disabled prompt, because choosing it is how an operator says the portal
 * has no splash, home, login or user page.
 *
 * This value stays inside the form. It is NOT what travels: see the note on the request
 * builder for why the wire carries absence instead.
 */
const NO_PAGE_SELECTED = -1;

/**
 * The wording of that option: `"<" + None_Specified + ">"` where the shared resource
 * value of `None_Specified.Text` is `None Specified`. The angle brackets are part of
 * the legacy display string, not markup.
 */
const NO_PAGE_SELECTED_LABEL = '<None Specified>';

/**
 * The in-flight wording for a WRITE this screen issued.
 *
 * The action row's own measured wording, unchanged - it is only the CONDITION under which it shows
 * that changed. See {@link PortalSettingsComponent.busyLabel}.
 */
/**
 * The wording of the link to this portal's host names.
 *
 * `ControlTitle_.Text` in `PortalAlias.ascx.resx` is "Portal Aliases", which is also the title
 * the route declares — so the link and the screen it opens are named identically.
 */
const ALIASES_LINK_LABEL = 'Portal Aliases';

const SAVING_LABEL = 'Saving…';

/**
 * The in-flight wording for a READ that refreshes a screen already on display.
 *
 * Matches the full-screen indicator's wording for the FIRST read, so a refetch and an initial read
 * describe themselves the same way and no third phrasing enters the screen.
 */
const REFRESHING_LABEL = 'Loading site settings…';

/**
 * The administrator selector's value when no account is chosen.
 *
 * ⚠ NOT A LEGACY SENTINEL, AND NOT A LEGAL ACCOUNT KEY. `Users.UserID` seeds
 * `IDENTITY(1, 1)` (`01.00.00.SqlDataProvider:L98`), so no real account can ever carry
 * minus one and no stored administrator can be mistaken for "none". A select must hold the
 * value of the option it shows, and the wire contract's absence is `null`, so a sentinel is
 * needed to stand in for it inside a `FormControl<number>`; minus one is chosen to match the
 * page selector directly above rather than to mean anything of its own.
 *
 * MIGRATION: the legacy selector had NO empty entry - `SiteSettings.ascx.vb:L331-L336` added
 * one item per role member and nothing else - so the option carrying this value is offered
 * only when the portal designates no administrator at all. That mirrors the server's own
 * rule, which permits absence on a portal that already has none and refuses an update that
 * would clear a designation.
 */
const NO_ADMINISTRATOR_SELECTED = -1;

/** One indent step. `Globals.vb:L835` appends exactly this, once per level. */
const INDENT_STEP = '...';

/**
 * The deepest indent that will ever be produced.
 *
 * The server refuses to store a hierarchy deeper than this, so a `level` beyond it
 * cannot describe real data and is treated as corrupt rather than trusted into a
 * repeat count. Without the clamp a single bad row could ask for an unbounded string.
 */
const MAX_INDENT_LEVELS = 127;

// ---------------------------------------------------------------------------
// Wording. Every string is measured; the resource VALUE wins over markup text.
// ---------------------------------------------------------------------------
//
// Two entries on this screen prove why the resource file rather than the markup is the
// authority: the marketing head's markup says `Marketing` while `Marketing.Text` says
// `Site Marketing`, and the keywords label's markup says `Key Words:` while
// `plKeyWords.Text` says `Keywords:`. The resource wording is used in both cases.
//
// MIGRATION: localisation itself is not ported. No translation runtime is present in the
//   pinned dependency surface, so these strings are authored directly and the legacy
//   resource files served as the reference for their wording only.

/** `ControlTitle_.Text`. Titles were mode-dependent; this is the settings mode. */
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

/**
 * The labelled-field captions, keyed by the control each legacy label named.
 *
 * `plPortalName.Text` is `Title:` — the semantic inversion worth flagging, because this
 * control is the portal's NAME and not its host-name alias.
 */
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
 * The help text behind each field's disclosure, taken from the matching `*.Help`
 * resource value.
 *
 * `portalName` carries a double space after its first sentence and `hostSpace` ends with
 * its parenthesised note; both are reproduced exactly, the second because it is the ONLY
 * place the "zero means unlimited" rule is stated — the input itself shows the stored
 * number, including zero.
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

/**
 * `lblBanners.Text`, with its leading break markup removed.
 *
 * The stored value opens with `<br>`. Twenty-eight of the thirty-four legacy message
 * values do, in both spellings, so the removal goes through the workspace's shared
 * cleaner rather than a local slice — the same function the rest of the application
 * uses, so the rule cannot drift here.
 */
const BANNER_HOST_LOCK_NOTICE = stripLegacyBreakTags(
  '<br>Banner option was set by the hostingprovider, and cannot be changed',
);

/**
 * `valExpiryDate`'s inline `ErrorMessage`, cleaned the same way.
 *
 * This validator is the ONE measured exception to the resource-first rule: it carries no
 * resource key at all, so its markup attribute is the only wording that exists.
 */
const EXPIRY_DATE_INVALID_MESSAGE = stripLegacyBreakTags('<br>Invalid expiry date!');

/** `valHostFee.Error` — this one carries no leading break, and none is invented. */
const HOST_FEE_INVALID_MESSAGE = 'Invalid fee, needs to be a currency value!';

/** The measured whole-number message for the three quota boxes. */
const WHOLE_NUMBER_INVALID_MESSAGE = 'Enter a whole number.';

/** The measured whole-number message for the time-zone offset. */
const TIME_ZONE_INVALID_MESSAGE = 'Enter the offset as a whole number of minutes.';

/**
 * `DeleteMessage.Text` from THIS screen's own resource file.
 *
 * The space before the question mark is in the stored value and is reproduced verbatim.
 * It is deliberately not the shared `DeleteItem.Text` — "…Delete This Item?", no space —
 * which the portal LIST screen uses; the two screens ask different questions.
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
 * What a refusal of the host-owned fields says.
 *
 * This is the wording the `403` resolves to, and it names the cause. It is never phrased
 * as a sign-in problem: the caller is authenticated and the request was understood, and
 * six named fields are simply not theirs to change.
 */
const HOST_FIELDS_REFUSED_MESSAGE =
  'Only a host account may change the hosting fee, the disk space, the page quota, the user quota or the expiry date. Those fields were not saved.';

/** What a rejected submission says, before the field-level detail is shown. */
const FORM_INVALID_MESSAGE = 'Correct the highlighted fields and try again.';

/** Shown when the page list cannot be read, so the four selectors are knowingly thin. */
const PAGES_UNAVAILABLE_MESSAGE =
  'The list of pages could not be loaded, so the page selectors show only the pages already chosen.';

/**
 * Shown when the administrator candidates cannot be read, so the selector knowingly holds only
 * the account already designated.
 *
 * Says what the operator has LOST rather than merely that something failed: without naming the
 * consequence, a selector holding one entry looks like a site with one eligible administrator.
 */
const ADMINISTRATORS_UNAVAILABLE_MESSAGE =
  'The list of eligible administrators could not be loaded, so the administrator cannot be changed here. Every other setting still saves.';

/** Shown while the administrator candidates are being read. */
const ADMINISTRATORS_LOADING_MESSAGE = 'Loading the accounts that may administer this site…';

/**
 * The wording of the entry standing in for an administrator the candidate list does not hold.
 *
 * `{0}` is the account key, which is the only thing this screen knows about them: the settings
 * projection publishes the identifier and no name, and the candidate read — which is where the
 * names come from — is precisely the read that did not return this account.
 *
 * It exists so that a designated administrator who has since been removed from the administrator
 * role can be RETAINED. Dropping them from the selector would silently reassign the portal on the
 * next save, which is the one outcome an operator could not have intended.
 */
const RETAINED_ADMINISTRATOR_LABEL = 'Current administrator (account {0})';

/** Shown when the address carries no usable portal identifier. */
const PORTAL_ID_MISSING_MESSAGE = 'This address does not identify a portal to configure.';

/** Where cancelling, and a completed delete, navigate to. */
const PORTAL_LIST_PATH = '/portals';

/**
 * The refusal status the host-only-field rule arrives as.
 *
 * Named because the number alone at a comparison site says nothing about which of the
 * several things a refusal can mean is being tested for.
 */
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

/**
 * One option in a page selector.
 *
 * Declared here and NOT exported. It is deliberately not the workspace's shared select-option
 * contract: this shape is consumed only by this screen's own template, and the legacy indent
 * that distinguishes it is a projection this screen owns rather than a general one. Keeping it
 * local also keeps this component's imports to the files it genuinely depends on.
 */
interface PageOption {
  readonly value: number;
  readonly label: string;
}

/**
 * One option in the administrator selector.
 *
 * Structurally identical to {@link PageOption} and deliberately declared separately, for the
 * reason that shape gives for not being the shared contract: the two lists are composed by
 * different rules — one carries a legacy indent and a filter on the administration band, the
 * other carries a login name for disambiguation and may include an entry for an account the
 * candidate read did not return — and one name for both would invite one composer for both.
 */
interface AdministratorOption {
  readonly value: number;
  readonly label: string;
}

/**
 * The typed control set.
 *
 * It carries MORE members than the screen renders, and that is deliberate rather than
 * leftover: see the round-trip note on {@link PortalSettingsComponent.toRequest}. The
 * unrendered members are hydrated from the loaded resource and returned unchanged.
 */
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

  // Other Settings. The time zone is an offset in whole minutes; the administrator holds an
  // account key, with `NO_ADMINISTRATOR_SELECTED` standing in for the wire contract's absence.
  // The currency and the default language are free text rather than selectors — see the two
  // MIGRATION notes on their controls in the template for why, and for why the currency appears in
  // this group at all when the legacy screen kept it in a section that has no successor.
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
 * A currency amount as the legacy `Type="Currency"` check understood one: an optional
 * sign, at least one digit, and an optional fractional part.
 *
 * Group separators are deliberately not accepted. The legacy check was culture-aware and
 * this one is not, which is a real narrowing — but the alternative is a locale-parsing
 * rule this component cannot express without a dependency the pinned surface excludes,
 * and the server applies the authoritative precision-and-range rule regardless.
 */
const CURRENCY_PATTERN = /^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/;

/** An ISO calendar date, which is what a native date input produces. */
const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/** How many characters of an ISO instant make up its date. */
const ISO_DATE_LENGTH = 10;

/**
 * Reads the portal identifier the router bound onto the input.
 *
 * A router path segment arrives as a STRING, so a conversion is unavoidable — and it has
 * to be an explicit one. `Number('0')` is `0`, which is falsy, while the string `'0'` is
 * truthy, so any shortcut that leans on truthiness disagrees with itself depending on
 * which side of the conversion it sits.
 *
 * Both `0` and `-1` are legitimate portal identifiers here: `Portals.PortalID` is declared
 * `IDENTITY (-1, 1)`, so the first portal ever created is `-1` and the second is `0`. That
 * `-1` is simultaneously the legacy absent-integer sentinel is a collision to be survived,
 * not a test to be written — which is why absence is reported as `undefined` and never as
 * a magic number.
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

/**
 * Renders a stored value into a text box the way the legacy screen did: with
 * `.ToString()`, and with no special case for zero.
 *
 * The three quota boxes were hydrated exactly this way, so a stored `0` appeared in the
 * box as `0`. Substituting the word "unlimited" for it, or blanking the box, would both
 * hide a real value — the "zero means unlimited" rule is stated in the disk-space help
 * text and belongs there alone.
 *
 * Absence is a different state from zero and renders as an empty box.
 */
function numberToText(value: number | null): string {
  return value === null ? '' : String(value);
}

/**
 * Reads a number back out of a text box, treating a blank box as zero.
 *
 * That default is measured, not chosen: the legacy handler declared each of the fee, the
 * disk space, the page quota and the user quota as a local initialised to `0` and parsed
 * over it only when the box was non-empty. A blank box therefore SAVED ZERO, and sending
 * absence instead would be a different write.
 */
function textToNumberOrZero(value: string): number {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return 0;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Trims a text box and reports a blank one as absence.
 *
 * The legacy null contract spelled its absent string as the EMPTY STRING and converted it
 * back to a database null on the way out, so a blank box and a null column were the same
 * state. Absence is therefore the faithful value to send.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Takes the date out of a stored instant so a native date input can show it.
 *
 * An absent expiry renders as an EMPTY box. The legacy hydration guarded the assignment
 * with its own absence test, so a portal with no expiry showed nothing — never a
 * placeholder date. Any instant that does not begin with an ISO date is treated as
 * unrenderable rather than sliced blindly.
 */
function instantToDateInput(instant: string | null): string {
  if (instant === null || instant.length < ISO_DATE_LENGTH) {
    return '';
  }

  const datePart = instant.slice(0, ISO_DATE_LENGTH);

  return ISO_DATE_PATTERN.test(datePart) ? datePart : '';
}

/**
 * Turns a date input's value back into the instant the API expects, or absence.
 *
 * MIGRATION: AN ABSENT EXPIRY TRAVELS AS ABSENCE, NOT AS THE LEGACY DATE SENTINEL.
 *   The legacy handler defaulted this to `Null.NullDate`, which is `0001-01-01`. That
 *   value cannot be sent: the column is `datetime`, whose earliest representable instant
 *   is 1753-01-01, and the server refuses an earlier one on sight — so sending the
 *   sentinel would fail every save that left the box empty.
 *
 *   Absence is also what the legacy actually STORED. `Null.GetNull` converted a date
 *   equal to the sentinel back to a database null before it reached the stored procedure,
 *   so the sentinel never existed in the database and only ever lived in memory. The
 *   sentinel is preserved where it was observable — in the blank box — and absence is
 *   preserved where it was stored.
 */
function dateInputToInstant(value: string): string | null {
  const trimmed = value.trim();

  return ISO_DATE_PATTERN.test(trimmed) ? trimmed : null;
}

/**
 * Maps a page selector's value onto the wire.
 *
 * MIGRATION: THE "NONE SPECIFIED" SENTINEL STAYS IN THE FORM AND BECOMES ABSENCE ON THE
 *   WIRE. The legacy handler sent `-1`, and `Null.GetNull` then rewrote it to a database
 *   null exactly as it did for the expiry date, so `-1` never reached the column either.
 *   Sending it now would be worse than redundant: the general portal write verifies every
 *   page reference against the portal's own pages, and no page bears the identifier `-1`.
 *
 *   `0` is NOT absence here and is never treated as such. `Tabs.TabID` is declared
 *   `IDENTITY (0, 1)`, so zero is a perfectly ordinary page.
 */
function selectedPageToWire(value: number): number | null {
  return value === NO_PAGE_SELECTED ? null : value;
}

/**
 * Maps a stored page reference onto the selector.
 *
 * Absence becomes the sentinel the "none specified" option carries, so the option
 * genuinely appears chosen rather than leaving the select on whatever happened to be
 * first.
 */
function wirePageToSelected(value: number | null): number {
  return value === null ? NO_PAGE_SELECTED : value;
}

/**
 * Maps the administrator selector's value onto the wire contract.
 *
 * The sentinel becomes absence, which the server permits only for a portal that already
 * designates no administrator — and the selector offers the option carrying it only in that
 * case, so the two rules agree by construction rather than by the operator's restraint.
 *
 * ⚠ EVERY OTHER NUMBER IS SENT AS IT STANDS, including zero. Zero is not a legal
 * `Users.UserID` today, since the column seeds `IDENTITY(1, 1)`, but a magnitude test here
 * would be a second rule about which account keys are real — and this screen's four page
 * selectors sit directly above, where zero IS an ordinary identifier. One rule, applied to
 * one sentinel value, is what keeps the two from drifting apart.
 */
function selectedAdministratorToWire(value: number): number | null {
  return value === NO_ADMINISTRATOR_SELECTED ? null : value;
}

/**
 * Maps a stored administrator reference onto the selector.
 *
 * Absence becomes the sentinel, so the empty option genuinely appears chosen rather than
 * leaving the select on whatever happened to be first — which on this field would designate
 * an administrator the operator never picked.
 */
function wireAdministratorToSelected(value: number | null): number {
  return value === null ? NO_ADMINISTRATOR_SELECTED : value;
}

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------
//
// A full case-insensitive sweep of the 568-line legacy markup finds exactly TWO
// validators on the whole screen, both comparison validators performing a data-type
// check: one on the expiry date and one on the hosting fee. There is no required-field
// validator, no regular-expression validator, no range validator and no validation
// summary anywhere on it.
//
// Two consequences follow, and they pull in opposite directions:
//
//   * A presence rule must NOT be added. A comparison validator performing a data-type
//     check PASSES on empty input, so leaving either box blank was entirely valid, and
//     rejecting a blank now would turn a legitimate save into a rejected one.
//   * A presence rule is not a substitute for the type check either. Reproducing these
//     two rules as "required" would accept `not a date` and reject a blank — precisely
//     inverting both of them.

/**
 * Builds a validator that mirrors a legacy data-type check.
 *
 * The shared behaviour is the part worth naming: a blank value PASSES, because that is
 * what the legacy validator did, and only a non-blank value that fails to match is
 * refused.
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

/**
 * The three quota boxes take a whole number.
 *
 * The legacy markup declared no validator on them, but its handler called
 * `Integer.Parse` on whatever they held, so a non-integer became an unhandled parse
 * fault presented as a broken page. This reports the same rejection as a field message
 * instead. It is the narrowest rule that does so: it adds NO lower bound, because the fee
 * and the quotas carried none and the neighbouring roles screen shows the contrast was
 * deliberate — its own fee validators DO declare a zero floor.
 */
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

// ---------------------------------------------------------------------------
// The page options behind the four selectors
// ---------------------------------------------------------------------------
//
// All four legacy selectors were filled from ONE call, made four times with identical
// arguments: `GetPortalTabs(intPortalId, True, True, False, False, False)`. The five flags
// are, in order, none-specified, hidden, deleted, URL and check-authorised, and the body
// at `Globals.vb:L813-L849` reads:
//
//   * none-specified TRUE   → prepend a synthetic option, `TabID = -1`, named
//                             `"<" + None_Specified + ">"`, and SELECTABLE;
//   * hidden TRUE           → invisible pages ARE included;
//   * deleted FALSE         → recycled pages are excluded;
//   * URL FALSE             → only pages whose type is Normal, and `GetURLType` returns
//                             Normal exactly when the page's URL is empty;
//   * authorised FALSE      → no role filtering is applied;
//   * and unconditionally   → administration pages are excluded.
//
// The indent is the only hierarchy cue the screen has: `"..."` repeated once per level,
// prefixed to the name.
//
// This projection is the CLIENT'S job here, and that is the server's own published
// position: the portal page listing returns every page of the portal, recycled ones
// included, and states that filtering them is a client-side projection over a complete
// answer. Nothing is being worked around.

/**
 * Reports whether a page is of the legacy Normal type.
 *
 * `GetURLType` returns Normal for an empty URL and one of four other types otherwise, so
 * with the URL flag off the surviving predicate is simply "carries no URL". A
 * whitespace-only value is treated as empty, which the legacy comparison against `""`
 * would not have done — a difference that can only ever admit a page the legacy hid, and
 * only for data that is malformed anyway.
 */
function isNormalPage(row: TabListItem): boolean {
  return row.url === null || row.url.trim().length === 0;
}

/**
 * Reports whether a page sits in the administration band.
 *
 * The legacy `IsAdminTab` is true for the administration page itself and for any direct
 * child of it, and the server expresses the same rule the same way when it renumbers a
 * hierarchy. When the portal's administration page is not known the band cannot be
 * computed, and nothing is excluded on a guess.
 *
 * `parentId` is compared for EQUALITY against a real identifier, so a page whose parent is
 * `0` — an ordinary parent, since page identifiers start at zero — is never mistaken for
 * one with no parent, and a root page's absent parent never matches.
 */
function isAdministrationPage(row: TabListItem, adminTabId: number | null): boolean {
  if (adminTabId === null) {
    return false;
  }

  return row.tabId === adminTabId || row.parentId === adminTabId;
}

/**
 * Produces the indent prefix for a page at the given level.
 *
 * Root pages are at level zero and take no indent. A level outside the storable range
 * describes no real hierarchy, so it is clamped rather than trusted into a repeat count.
 */
function indentFor(level: number): string {
  if (!Number.isFinite(level) || level <= 0) {
    return '';
  }

  const steps = Math.min(Math.trunc(level), MAX_INDENT_LEVELS);

  return INDENT_STEP.repeat(steps);
}

/**
 * Builds the shared option list for all four page selectors.
 *
 * The received order is preserved rather than re-sorted: the listing already arrives in
 * hierarchy order by page order, which is the sequence the legacy iteration relied on, and
 * re-sorting it here would put the indents out of step with their parents.
 *
 * @param rows Every page of the portal, as received.
 * @param adminTabId The portal's administration page, or `null` when it is not yet known.
 * @param retain Page references the portal currently holds, so a stored choice that the
 *   filter would otherwise hide still appears — a page that has since been recycled, made
 *   into a link, or moved under administration must remain visible as the current value
 *   rather than silently reset the selector to "none specified".
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

  // A reference the portal holds that the listing did not return at all — a page in
  // another portal, or one removed between the two reads. It is surfaced by identifier
  // rather than dropped, because dropping it would silently rewrite the stored value as
  // soon as the operator saved anything else.
  for (const tabId of retain) {
    if (!seen.has(tabId)) {
      seen.add(tabId);
      options.push({ value: tabId, label: String(tabId) });
    }
  }

  return options;
}

/**
 * The portal settings screen.
 *
 * @remarks
 * The tab strip and the collapsible sections are authored in the paired template and
 * stylesheet. They are deliberately NOT a shared component: the shared inventory is closed
 * at ten members and this screen is the only consumer of either affordance, so adding an
 * eleventh would widen a settled contract for one caller.
 */
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
    // `busy` is declared further down the class; the arrow body is only evaluated when the
    // tracker asks, so the ordering is irrelevant at construction time.
    () => this.form.dirty && this.busy() === false,
  );
  // -------------------------------------------------------------------------
  // Collaborators. Injected as fields rather than through the constructor, which is
  // this workspace's convention, and every one of them is a state or presentation
  // concern: no transport type is reachable from here.
  // -------------------------------------------------------------------------

  private readonly portals = inject(PortalStore);
  private readonly identity = inject(AuthStore);
  private readonly pages = inject(TabService);
  private readonly notifications = inject(NotificationService);

  /**
   * This screen's own element, searched for the first control a refused submit is standing on.
   *
   * Scoped to the host and never to the document, so the search cannot reach a control belonging to
   * another screen still in the DOM during a route transition.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Used to render the tab and section changes a refusal makes BEFORE focus is moved.
   *
   * A control that is not in the document cannot take focus, and this screen keeps only the active
   * tab's panel in the DOM, so revealing a control and focusing it are two steps that must be
   * separated by a render. Under on-push nothing renders until change detection runs, and by the time
   * it would run on its own the focus call has already been made and has already failed silently.
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
   * The tab controls, in the order the strip renders them.
   *
   * Queried rather than reached through a selector so that keyboard movement never depends
   * on an element identifier matching a string built here, and so that a second instance of
   * this screen on one page could not steal the focus. The order matches the declared tab
   * order because the template iterates that same list.
   */
  private readonly tabControls = viewChildren<ElementRef<HTMLElement>>('tabControl');

  /**
   * The settings object the form currently reflects.
   *
   * Held so a refreshed store slice can be told apart from the one already on screen, and
   * so the members this screen preserves without showing can be returned unchanged. It is
   * an ordinary field rather than a signal because nothing renders it.
   */
  private hydratedFrom: PortalSettings | null = null;

  /**
   * The page listing read in flight, held so that a NEW read can cancel the one it replaces.
   *
   * Destruction-time cleanup alone was not enough. The portal identifier arrives as a route input, so
   * moving from one portal's settings to another's re-runs the setter WITHOUT the component being
   * recreated - the case `takeUntilDestroyed` cannot cover. A slow answer for the portal just left would
   * then fill all four page selectors with pages that belong to a different tenant, which is worse than
   * a stale list: a page identifier means nothing outside its own portal, so saving afterwards would
   * write a reference the new portal cannot resolve. Cancelling the previous read first makes that
   * impossible.
   *
   * The two store-owned reads this setter also starts - the settings resource and the portal detail -
   * already replace on the same terms inside the store, so this handle completes the boundary rather
   * than duplicating it.
   */
  private pagesRequest: Subscription | null = null;

  /**
   * The failures already announced, held by identity so the same one is not announced
   * twice.
   *
   * Plain fields rather than signals: they exist only to make the announcing effect
   * idempotent, and nothing renders them. The store replaces a failure object on every
   * fresh outcome, so identity is a sufficient and cheap test.
   */
  private announcedSettingsFailure: PortalFailure | null = null;
  private announcedDetailFailure: PortalFailure | null = null;

  // -------------------------------------------------------------------------
  // The form
  // -------------------------------------------------------------------------

  /**
   * Every control is non-nullable, which is what makes the raw value fully typed instead
   * of partial, and what makes a reset return each control to its declared initial value
   * rather than to null. No control is read through a name lookup and none is asserted
   * non-null: they are reached as properties of this group.
   */
  protected readonly form = new FormGroup<PortalSettingsFormModel>({
    // ⚠ NOT REQUIRED, MIRRORING THE LEGACY MARKUP AND THE LEGACY WRITE PATH BOTH.
    //
    // A `Validators.required` was declared here, mirroring a `NotEmpty()` the update contract
    // carried on `PortalName`. Both are withdrawn, because the rule refused an input the legacy
    // application accepted:
    //
    //   * A case-insensitive sweep of `Website/admin/Portal/sitesettings.ascx` finds exactly two
    //     validators on its 568 lines, both `CompareValidator`s, and NO `RequiredFieldValidator`
    //     anywhere — `txtPortalName` carries `MaxLength="128"` and nothing else.
    //   * The write path stored the blank rather than refusing it.
    //     `Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb:L632` passes
    //     `PortalName` RAW while wrapping fourteen of its twenty-seven sibling arguments in
    //     `GetNull`; `PortalController.vb:L1568-L1570` forwards the parameter untouched; and
    //     `SiteSettings.ascx.vb:L772` passes `txtPortalName.Text` as typed. So the empty string
    //     reached `[PortalName] [nvarchar] (128) NOT NULL`, which accepts it.
    //
    // Minimal Change Clause item 3 requires identical inputs to produce identical outcomes, and
    // that is what settles it. The width bound is the markup's own `MaxLength="128"` and stays.
    // The submit path still normalises this field, for the reason recorded there: what reaches the
    // wire is the trimmed value, so a title of three spaces is stored as the empty string rather
    // than as three spaces.
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

    // NO VALIDATOR, and that is measured rather than assumed: a full case-insensitive sweep of
    // the 568-line legacy markup finds exactly two validators on the whole screen, both
    // data-type comparisons, and neither is on this field. The invariant that a portal keeps an
    // administrator is the SERVER's, enforced on both write paths, and reproducing it as a
    // client rule would put a second authority on it that could disagree.
    administratorId: new FormControl(NO_ADMINISTRATOR_SELECTED, { nonNullable: true }),
    timeZoneOffset: new FormControl('', {
      nonNullable: true,
      validators: [timeZoneOffsetCheck],
    }),

    // LENGTH ONLY, AND NO CLOSED VALUE SET. The legacy controls were selectors, so their legality
    // came from the list they were filled from rather than from a validator: the currency list came
    // from the excluded list subsystem and the culture list from the excluded localisation
    // subsystem. Neither list has a source here, and INVENTING one would refuse values the server
    // accepts and the column holds — including whatever the installation already stores. The length
    // is the one rule that IS the server's, so it is the one rule reproduced.
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
   * The confirmation body: the measured question, then WHICH portal it will destroy.
   *
   * ⚠ THE PROMPT NAMED A TYPE AND NOT AN INSTANCE. "Are You Sure You Wish To Delete This
   * Portal ?" is the legacy wording and is kept verbatim, but the dialog is modal and covers the
   * screen it was raised from - including the heading that was the only thing on the page saying
   * which tenant is open. So at the moment of the single irreversible action in this feature, the
   * name was hidden by the very prompt asking about it.
   *
   * The name is appended only when it is known: the shared header suppresses an absent or blank
   * name, and this does the same rather than rendering a dangling separator or the word
   * "undefined" inside a destructive confirmation.
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

  /**
   * `optBanners`, horizontal, three items.
   *
   * The values are the enumeration's own and match the legacy list item for item. `None`
   * is the value zero and is a REAL choice — "display no banners" — never an absence.
   */
  protected readonly bannerChoices: readonly RadioChoice<BannerAdvertisingMode>[] = Object.freeze([
    { value: BannerAdvertisingMode.None, label: 'None' },
    { value: BannerAdvertisingMode.Site, label: 'Site' },
    { value: BannerAdvertisingMode.Host, label: 'Host' },
  ]);

  /**
   * `optUserRegistration`, horizontal, four items.
   *
   * `None` is again the value zero and a real choice — "registration is closed".
   */
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
   * The portal to configure, bound from the `:portalId` path segment.
   *
   * The NAME is load-bearing. Router input binding matches a path parameter to an input of
   * the SAME name, so renaming this severs the binding with no compile error and no
   * runtime complaint — the screen would simply never receive a portal.
   *
   * The write type admits a string because that is what a path segment is. The conversion
   * is explicit and total: a segment that is not a whole number yields absence, which the
   * screen reports, rather than a silently wrong identifier.
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

    // Two reads, and the second is not redundant. The settings resource is what this
    // screen edits; the portal detail supplies the administration page identifier, without
    // which the administration band cannot be excluded from the four page selectors. The
    // legacy screen had the whole portal in hand for exactly the same reason.
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
    // The one genuine side effect on this screen: moving a newly arrived settings resource
    // into the form. It reacts to the store's slice rather than to a callback so that a
    // refresh performed elsewhere — a save confirming itself, for instance — is reflected
    // without this screen having to know it happened.
    //
    // Nothing is written to a signal from here. Hydration touches the form and one plain
    // field, which keeps this effect free of the read-then-write cycles that make reactive
    // graphs hard to reason about.
    effect(() => {
      const received = this.portals.settings();

      if (received === null || received === this.hydratedFrom) {
        return;
      }

      this.hydrate(received);
    });

    // Announcing a failure is the second genuine side effect. It watches the store's two
    // classified failure slices rather than the outcome of a call, because the store's
    // commands report by state rather than by return value, and because a failure raised by
    // the initial read deserves the same announcement as one raised by a save.
    effect(() => {
      const failure = this.portals.settingsFailure();

      if (failure === this.announcedSettingsFailure) {
        return;
      }

      this.announcedSettingsFailure = failure;

      if (failure !== null) {
        // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT, and it was being dropped. The failure record carries the
        // problem document, so the correlation identifier the server validated for the request is right here -
        // the only join key between what an operator saw in the browser and the request as the server logged
        // it. A browser audit measured the asymmetry it left: the same refusal read `Reference: <id>` when the
        // shared banner presented it and nothing quotable when a notification did. A document carrying none
        // resolves to null and is simply not quoted.
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
        // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT, and it was being dropped. The failure record carries the
        // problem document, so the correlation identifier the server validated for the request is right here -
        // the only join key between what an operator saw in the browser and the request as the server logged
        // it. A browser audit measured the asymmetry it left: the same refusal read `Reference: <id>` when the
        // shared banner presented it and nothing quotable when a notification did. A document carrying none
        // resolves to null and is simply not quoted.
        this.notifications.notify(
          failure.severity,
          this.describeDeleteFailure(failure),
          problemSupportReference(failure.problem),
        );
      }
    });

    // The banner lock is its OWN effect rather than a step of hydration, and the difference
    // matters. The lock depends on two things — the stored choice and whether the caller
    // holds the host account — and the identity is not guaranteed to have resolved by the
    // time the settings arrive. Applying it during hydration would therefore leave the
    // control in whatever state it had when the resource landed, and a later identity would
    // never correct it. Declaring it separately also keeps hydration depending on the
    // settings resource alone, which is what makes its short-circuit on an unchanged
    // resource safe.
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

    // ⚠ WITHDRAW THE REFUSAL NOTICE ONCE IT STOPS BEING TRUE. The notice reads "correct the
    // highlighted fields and try again", and runtime measurement found it still saying so after the
    // one offending field had been corrected and the form had returned to valid, with zero field
    // errors left on screen — an assertion about the present tense that was no longer about anything.
    // The field-level messages cleared reactively because they are bound to their own controls; only
    // this screen-level one was latched, set on refusal and cleared on the next submit or on hydration.
    //
    // Watched through the form's own status stream rather than recomputed, because a `FormGroup` is not
    // a signal and its validity cannot be derived reactively. Only the transition INTO validity clears
    // the marker: an edit that leaves the form invalid must not withdraw a notice that still holds.
    //
    // The Warning toast raised alongside it is deliberately left to stand. A toast records that
    // something HAPPENED — a submit was refused, which remains true however the form looks now — and
    // this application's notification surface deliberately keeps warnings and errors on screen until
    // they are dismissed or the reader leaves the screen. The notice is a statement about the form's
    // CURRENT state and is the only one of the two that can go stale.
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
   * The address of this portal's host names, or `null` when the route named no portal.
   *
   * ⚠ THIS IS THE APPLICATION'S ONLY LINK TO THAT SCREEN. Every anchor the console renders was
   * enumerated and none addressed `:portalId/aliases`; the listing's single row command targets
   * this screen, and nothing led onwards from it. So a portal's host names — which every action
   * of the alias resource grants a tenant administrator by right — could be managed only by
   * typing an address, and the migration's parity requirement does not treat a workflow reachable
   * solely by typing as reachable.
   *
   * An array rather than an interpolated string, so the router composes the segments: the
   * identifier can be `0` or `-1` and both are real tenants.
   */
  protected readonly aliasesLink = computed<(string | number)[] | null>(() => {
    const id: number | undefined = this._portalId();

    return id === undefined ? null : ['/portals', id, 'aliases'];
  });

  /**
 * The wording of that link.
 *
 * ⚠ LEGACY-VERBATIM. `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx` declares
 * `ControlTitle_.Text` as `Portal Aliases`, which is what the legacy module titled itself. Using the
 * destination's own title as the link text is what lets an operator who knew the legacy application
 * recognise where it goes.
 */
  protected readonly aliasesLinkLabel: string = ALIASES_LINK_LABEL;

  /** The settings resource on screen, or `null` before the first read completes. */
  protected readonly settings = this.portals.settings;

  /** True while either read is outstanding and nothing is on screen yet. */
  protected readonly loading = computed<boolean>(
    () => this.portals.settingsLoading() && this.portals.settings() === null,
  );

  /**
   * True while ANY request this screen depends on is outstanding - a save, a delete or a re-read.
   *
   * The doc comment used to say "a save or a delete", and the expression never matched it: the
   * store raises `settingsLoading` for its READ of the settings projection as well as for its
   * write, and `detailLoading` is a read slice outright. The wider meaning is the correct one for
   * what this signal is FOR - both actions are disabled while it holds, and submitting over an
   * outstanding re-read would post a projection the screen is about to replace. Only the name and
   * the description were wrong.
   */
  protected readonly busy = computed<boolean>(
    () => this.portals.settingsLoading() || this.portals.detailLoading(),
  );

  /**
   * Whether the outstanding request is a WRITE this screen issued, rather than a read.
   *
   * ⚠ THE AFFORDANCE WAS MISLABELLED WITHOUT IT. The action row renders a progress indicator
   * whenever {@link PortalSettingsComponent.busy} holds, labelled "Saving…" - and that label was
   * measured appearing on a plain REVISIT to this screen, where the store already holds settings
   * so the form renders immediately and the refetch merely flips the same flag. The operator was
   * told their work was being saved when nothing had been submitted at all. A progress affordance
   * that misreports WHICH operation is in flight is worse than none, because it cannot be
   * disbelieved selectively.
   *
   * Written by this component rather than derived from the store, because the store cannot answer
   * it: one flag covers both directions of the settings slice. It is set immediately before the
   * write is issued and cleared when the outcome ticket completes, whichever way it completes.
   */
  private readonly writing = signal(false);

  /**
   * The wording for the in-flight indicator, chosen by what is actually in flight.
   *
   * Both strings are the ones already in use on this screen - the full-screen indicator's own
   * wording for a read, and the action row's for a write - so no new vocabulary is introduced.
   */
  protected readonly busyLabel = computed<string>(() =>
    this.writing() ? SAVING_LABEL : REFRESHING_LABEL,
  );

  /**
   * The problem document to present, if any.
   *
   * Both slices are consulted because the two operations this screen performs report
   * through different ones: saving settings fails into the settings slice, and deleting the
   * portal fails into the detail slice. The settings slice is preferred when both carry a
   * failure, because it is the one the operator's own last action produced.
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
   * The portal's own name, shown beside the page title so an operator editing one of many
   * portals can see which one. Absent until the detail read completes.
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
   * Whether the caller holds the host account.
   *
   * This gates the host-settings disclosure, the delete action and the banner lock. It is
   * read from the identity store rather than expressed as a permission, because the legacy
   * gate was a super-user test and the permission vocabulary has no member for it — a
   * permission-based gate would fail closed and hide the section from everybody.
   *
   * The gate is ADVISORY. The server enforces the same rule and answers `403`, and that
   * answer is presented rather than pre-empted.
   */
  protected readonly isSuperUser = this.identity.isSuperUser;

  /**
   * Whether the banner choice is locked by the hosting provider.
   *
   * Measured: the legacy screen hid the notice outright for a host account and, for
   * everybody else, disabled the list and showed the notice exactly when the STORED value
   * was Host. The stored value is what matters, not the value currently in the form —
   * otherwise choosing Host would lock the control mid-edit.
   */
  protected readonly bannerLockedByHost = computed<boolean>(() => {
    if (this.identity.isSuperUser()) {
      return false;
    }

    const held = this.portals.settings();

    return held !== null && held.bannerAdvertising === BannerAdvertisingMode.Host;
  });

  /**
   * Whether the delete action is offered.
   *
   * Two conditions, both measured. It is a host-account action, and it is withheld when the
   * target IS the portal currently being browsed — a portal cannot delete itself out from
   * under the session viewing it.
   *
   * When the browsing portal is unknown the action is withheld. A destructive action whose
   * guard cannot be evaluated is not offered.
   */
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

  /**
   * The option list every page selector shares.
   *
   * One derivation, one underlying read, four consumers. The legacy screen issued the same
   * query four times; doing that here would be four identical requests for one answer.
   */
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

  /**
   * The accounts the administrator selector offers.
   *
   * MIGRATION: reproduces `Website/admin/Portal/SiteSettings.ascx.vb:L329-L339`. The legacy
   * screen listed the members of the portal's own administrator role and pre-selected the entry
   * matching the stored `AdministratorId`; the chosen value became argument nine of the portal
   * update at `:L775`. This screen previously showed the stored identifier read-only, because no
   * read existed that could enumerate the administrators of the portal the ROUTE names — every
   * role read resolves its tenant from the caller's own context. That read now exists on the
   * portal resource, so the affordance is restored rather than approximated.
   *
   * Three rules compose the list, and each closes a way the selector could otherwise mislead:
   *
   * - THE CANDIDATES ARE GATED ON THE PORTAL THEY WERE READ FOR. A screen moved to another
   *   portal would otherwise offer the previous portal's accounts for as long as the new read
   *   took — long enough to submit one, which the server would refuse for a reason the operator
   *   could not see.
   * - THE STORED ADMINISTRATOR IS ALWAYS PRESENT, even when the candidate read has not arrived
   *   or does not contain them. An account removed from the administrator role while still
   *   designated is a real state, and a selector that dropped them would silently reassign the
   *   portal on the next save. Its wording says what it is rather than pretending to be a name
   *   this screen does not hold.
   * - THE EMPTY OPTION IS OFFERED ONLY WHEN THE PORTAL DESIGNATES NOBODY. The legacy selector
   *   had no empty entry at all, and the server refuses an update that would clear a
   *   designation, so offering it otherwise would be an option whose only outcome is a refusal.
   */
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
        // Both names, because the display name is the one account field a tenant may compose
        // from a format string and two administrators can therefore legitimately share one. The
        // login name is unique within a portal, so the pair is always distinguishable.
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
   * The candidate accounts, but only when they belong to the portal this screen is showing.
   *
   * The gate is the whole value of the store recording which portal it read for: without it, the
   * held list is simply "the last list read", which during a move between portals is the wrong
   * one and is indistinguishable from the right one.
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

  /**
   * Whether the candidate read failed, so the template can say why the list is short.
   *
   * Reported rather than escalated, on the same terms as the page listing beside it: the field
   * still holds the stored administrator and still round-trips it, and the other seventeen
   * fields still save. What is lost is the ability to REASSIGN, and saying so is what stops that
   * looking like a screen that simply has one option.
   */
  protected readonly administratorsFailed = computed<boolean>(() => {
    if (this.portals.administratorsFailure() === null) {
      return false;
    }

    return this.portals.administratorsLoading() === false;
  });

  /**
   * The portal's globally unique identifier, upper-cased and read-only.
   *
   * The legacy screen rendered it through `.ToString.ToUpper` into a label, never into an
   * input, and the column carries no setter on this screen. The casing is reproduced.
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
   * Moves between tabs with the keyboard.
   *
   * The arrow, home and end behaviour is what a tab strip is expected to implement once it
   * declares the roles that promise it; a strip that declares them without implementing
   * them is worse than one that declares neither. Keys this strip does not handle are left
   * alone so the browser's own behaviour survives.
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
   * Moves keyboard focus onto the tab control at the given position.
   *
   * Focus follows selection in this strip, which is the expected behaviour for a strip whose
   * panels are already loaded: the panel changes as the caller moves, with no second
   * keystroke needed to activate it.
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

  /**
   * Opens or closes the named section.
   *
   * The set is replaced rather than mutated. An in-place change to a held collection is
   * invisible to a signal, so a consumer using the default change-detection contract of
   * this workspace would not re-render.
   */
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
   * Passed to the shared field wrapper as well as set on the control itself, which is what
   * associates the visible label with the thing it labels. The names are the control names,
   * so an identifier cannot drift from the field it belongs to.
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
   * The validation messages for one control, or nothing when it has none to show.
   *
   * A message appears only once the operator has touched or changed the control, which is
   * what the legacy dynamic display did — an untouched form showed no complaints.
   *
   * Error entries are read with index access because the error map is an index signature;
   * property access on one does not compile under this workspace's settings.
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

    // ⚠ NO REQUIRED RULE IS REPORTED, BECAUSE NO CONTROL ON THIS SCREEN DECLARES ONE. A branch
    // here reported `"Site Title is required."` against a `Validators.required` on the title,
    // mirroring a `NotEmpty()` the update contract carried; both are withdrawn as a parity break.
    // The legacy screen declares no `RequiredFieldValidator` on its 568 lines, and its write path
    // stored a blank title as the empty string in a column that accepts it, so refusing one here
    // refused an input both legacy tiers took. See the title control's own note for the citations.
    //
    // The two measured data-type checks and the two derived ones all report their own
    // measured wording as the error value, so it is surfaced directly.
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

  /**
   * Saves the settings.
   *
   * A rejected form is not sent. Every control is marked touched first so the messages the
   * operator needs are all visible at once rather than appearing one at a time, and the
   * form-level message names the outcome for a screen reader that never saw the fields.
   */
  protected onSubmit(): void {
    const target = this._portalId();

    if (target === undefined || this.hydratedFrom === null || this.portals.settingsLoading()) {
      return;
    }

    // ⚠ NORMALISE THE TITLE BEFORE JUDGING IT, so one resource does not trim where another does.
    // The sibling portal record screen trims its own title, and runtime testing measured
    // twenty-three characters typed becoming seventeen sent on one screen and twenty-three on the
    // other for the same class of field. Writing the trimmed value back into the control - rather
    // than trimming only on the way out - is what makes the value the operator sees the value that
    // will be sent. Neither tier refuses a blank title, so this normalisation decides only what a
    // whitespace-only entry is STORED as: the empty string, not three spaces.
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
        // Cleared on EVERY termination, not in the success handler: the ticket completes without
        // emitting when the write is refused, and a flag left set would label a later re-read as a
        // save for the rest of the screen's life.
        finalize(() => {
          this.writing.set(false);
        }),
      )
      .subscribe((stored: PortalSettings) => {
        // The store has already replaced its slice with the stored resource, and the
        // hydration effect will move it into the form. Recording it here as well keeps the
        // preserved-member source in step even if the effect has not run yet.
        this.hydratedFrom = stored;

        // ⚠ THE FORM IS RETURNED TO PRISTINE HERE, EXPLICITLY, AND NOT LEFT TO THE EFFECT.
        // The comment above is right that hydration resets the control state when it runs -
        // `hydrate` ends in `markAsPristine`/`markAsUntouched` - but the form was MEASURED
        // still carrying `ng-dirty` after a successful save while the profile screen reset
        // correctly, so that path is not reliably reached: the effect observes the store's
        // slice, and a save that stores exactly what was already held gives it nothing to
        // react to. Depending on a value CHANGING to clear a flag means an idempotent save
        // never clears it.
        //
        // Two things went wrong while the flag survived, and the second is the serious one.
        // A saved form kept advertising unsaved work, so the screen contradicted the success
        // notification beside it. And the unsaved-changes guard declared at the top of this
        // class reads `form.dirty` once a write is no longer in flight, so navigating away
        // after a SUCCESSFUL save raised a confirmation asking the operator to discard work
        // they had just committed - a prompt that teaches operators to dismiss the prompt.
        //
        // Marking untouched as well as pristine matches `hydrate`, so submit-time validation
        // messages do not persist over an entry that has since been stored.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        this.notifications.success(SAVE_SUCCEEDED_MESSAGE);
      });
  }

  /**
   * Opens whatever is hiding the first offending control, then focuses it.
   *
   * ⚠ WITHOUT THIS, A REFUSED SUBMIT ON THIS SCREEN LEAVES THE OPERATOR ON THE BUTTON. The shared
   * focus directive cannot always help here, and the reason is specific rather than incidental: it
   * refuses to act when the form is VALID at the moment the submit event fires, and this handler can
   * change a control's validity AFTER that moment - it normalises the title before judging the form,
   * so a value whose validity depends on its trimmed form is settled inside the handler. Both the
   * directive and this handler listen to the same event and Angular does not order them, so the
   * directive is as likely as not to look before the normalisation and find nothing to do.
   *
   * Runtime measurement recorded the consequence exactly, by object identity: after a refused submit
   * `document.activeElement` was the Update button and not the offending input, with the error the
   * operator had to fix roughly six hundred pixels above them, and the button carrying no visible
   * focus ring because the submit came from a pointer.
   *
   * ⚠ THE SEARCH HAS TO CROSS TWO KINDS OF HIDING, WHICH IS WHY THIS IS NOT ONE `querySelector`.
   * Every collapsed section is opened first: a refusal must not seal its own explanation inside a
   * region the operator closed. Then, because this screen keeps only the ACTIVE tab's panel in the
   * document, the tabs are tried in order until one of them contains an offending control - so a
   * negative fee on Advanced Settings is reached from a submit pressed on Basic Settings without this
   * method holding a map from control to tab, which would be a second place for the truth to live.
   * The active tab is tried first by construction, since the loop starts from the tab already showing.
   *
   * A render is forced between revealing and focusing. Under on-push a control revealed this instant
   * is not in the document yet, and `focus()` on an element that is not there succeeds as a call and
   * moves focus nowhere - which is the same silent failure this method exists to remove.
   *
   * The selector is IMPORTED from the shared directive rather than restated, so the control this
   * reveals is by definition the one the directive would have chosen; a hand-copied approximation
   * would drift and the two would act on different elements.
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

    // Nothing was found on any tab. The tab the operator was on is restored rather than left wherever
    // the search ended, because moving somebody to a different tab and then giving them no reason for
    // it is worse than the refusal they already have.
    this._activeTab.set(startedOn);
  }

  /**
   * Leaves the screen without saving.
   *
   * MIGRATION: RETURN NAVIGATION IS THE ROUTER'S CONCERN AND IS NOT HELD AS STATE.
   *   The legacy screen decided whether to offer this action, and where it went, from a
   *   referring address it stashed in the serialised control tree. The router already knows
   *   where a screen was reached from, so keeping a second copy here would give the
   *   application two answers to one question. The action is always offered and always
   *   returns to the portal listing.
   */
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
   * Deletes the portal once confirmed.
   *
   * The refusal worth naming is the last-remaining-portal one, which arrives as a state
   * conflict carrying a published code. The wording for it comes from the shared conflict
   * vocabulary rather than being composed here, so this screen and every other report the
   * same sentence for the same refusal.
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
        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS THE OPERATOR TO
        // CONFIRM DISCARDING EDITS TO A PORTAL THAT NO LONGER EXISTS. The probe reads
        // `dirty && saving() === false`, and a delete is not a save, so an operator who typed
        // something and then deleted the portal would be prompted about the typing on the way out.
        // There is nothing left to save, so pristine is the honest state.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        this.notifications.success(DELETE_SUCCEEDED_MESSAGE, true);

        // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The
        // shell retires notifications on a completed navigation, and this one is raised in the same
        // task as the navigation below, so it was swept before it could be painted. The deleted
        // portal's own settings screen cannot show the confirmation - the record it described is
        // gone - so the listing is the only place it can be read.
        this.notifications.retainAcrossNavigation();

        // ⚠ THE ADDRESS IS REPLACED RATHER THAN PUSHED. The screen being left describes a record that
        // no longer exists, so leaving a history entry for it would offer the browser’s Back button as a
        // route to a settings form for a deleted portal - and the unsaved-entry gate reads the replacement
        // as an application-initiated departure, so it does not question a navigation nobody chose.
        void this.router.navigateByUrl(PORTAL_LIST_PATH, { replaceUrl: true });
      });
  }

  // -------------------------------------------------------------------------
  // Presenting a failure
  // -------------------------------------------------------------------------

  /**
   * Describes a failure of the settings read or write.
   *
   * MIGRATION: A `403` HERE IS THE HOST-ONLY-FIELD RULE, NOT A SESSION PROBLEM.
   *   The legacy screen compared six host-owned members — the hosting fee, the disk space,
   *   the page quota, the user quota, the site-log retention and the expiry date — against
   *   the stored portal for any caller without the host account, and threw a bare exception
   *   on the first difference. The server now answers `403` for the same reason, so that is
   *   what this message says. It is never phrased as an expired session, and it never
   *   redirects to a sign-in screen: the caller is authenticated and the request was
   *   understood.
   *
   *   The severity comes from the store's classification, which resolves a refusal to a
   *   WARNING rather than an error — matching the legacy denial screen, which presented its
   *   own refusals as warnings. A system behaving exactly as configured is not a fault.
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
   * Describes a failure of the delete.
   *
   * The refusal this must name is the last-remaining-portal one, which the server publishes
   * as a state conflict with a code; its wording comes from the shared conflict vocabulary
   * so that it reads identically wherever it is reported. A `403` here is an ordinary
   * permission refusal rather than the host-field rule, so the generic wording is correct
   * and the host-field sentence would be misleading.
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
   * Moves a settings resource into the form.
   *
   * Every conversion here is one of the measured hydration rules, and each is named at its
   * own helper. The form is left pristine and untouched afterwards so that no message
   * appears before the operator has done anything.
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
   * Composes the update request.
   *
   * MIGRATION: THE SIX MEMBERS THIS SCREEN DOES NOT SHOW ARE RETURNED UNCHANGED, AND THAT
   *   IS REQUIRED RATHER THAN TIDY. The update resource carries the portal's whole editable
   *   state and REPLACES every column it names, so a member sent as absent is a member
   *   cleared. Sending absence for the logo, the background, the payment processor and its
   *   account, the site-log retention or the home directory would erase settings this screen
   *   never offered to change.
   *
   *   ⚠ IT WAS EIGHT, AND TWO OF THEM SHOULD NEVER HAVE BEEN AMONG THEM. The currency and the
   *   default language were returned unchanged and shown nowhere, so values the API publishes -
   *   `USD` and `en-US` on the measured installation - appeared on no tab and in no section
   *   while being re-submitted on every save: invisible, uneditable, and a legacy workflow with
   *   no successor. Both are now edited fields in Other Settings. The remaining six are unshown
   *   because their SUBJECT MATTER is out of scope - file management for the three file
   *   references, the dropped payment section for the processor pair, and the host-owned log
   *   retention - not merely because no control was built for them.
   *
   *   Two of the six would do worse than erase. The host-owned comparison the server performs
   *   for a non-host caller includes the site-log retention and the expiry date, so
   *   returning absence for a retention value that is actually set would refuse the whole
   *   save with a `403` naming fields the operator never touched. And the administrator must
   *   be returned because the server refuses an update that would leave the portal without
   *   one.
   *
   *   This is what the legacy screen did too, and by the same mechanism: a hidden Web Forms
   *   control kept its value in the serialised control tree, so the postback carried the
   *   untouched values and the comparison passed. Preserving them is the faithful
   *   translation of that, not a compensation for it.
   *
   * MIGRATION: THE PROCESSOR CREDENTIAL IS SENT AS ABSENT, WHICH THE SERVER READS AS "LEAVE
   *   THE STORED REFERENCE ALONE". It is deliberately the one member NOT round-tripped: the
   *   settings projection does not publish it, so there is nothing to return, and this
   *   screen never holds, logs or displays it.
   *
   * @param source The resource the form was hydrated from, and the source of every
   *   preserved member.
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

      // Other Settings. The offset is a whole number of minutes; a blank box means the
      // portal keeps no explicit offset. The administrator's sentinel becomes absence on the
      // wire, which the server permits only for a portal that already designates none.
      administratorId: selectedAdministratorToWire(edited.administratorId),
      timeZoneOffset: this.optionalWholeNumber(edited.timeZoneOffset),

      // Now EDITED rather than preserved. Both were previously returned unchanged from the loaded
      // snapshot, which meant a non-null value the API published — `USD` and `en-US` on the measured
      // installation — appeared on no tab and in no section while still being re-submitted on every
      // save: invisible, uneditable, and a workflow the legacy screen offered that had no successor.
      // An emptied box sends absence, matching every other optional text field on this screen.
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

      // ⚠ THE REVISION THIS SUBMISSION WAS COMPOSED AGAINST, ROUND-TRIPPED VERBATIM, AND THE ONLY
      // MEMBER HERE THAT DESCRIBES NO SETTING. It comes from `source` - the same resource every
      // preserved member above comes from - so it describes exactly the snapshot this payload
      // reconstructs. That matters most on THIS request precisely because of the note above: the
      // payload replaces every column, and nine of the values it carries are ones this screen never
      // shows, so a second administrator saving an older snapshot destroyed the first
      // administrator's committed edits to fields NEITHER of them had opened. Sending the token makes
      // the server refuse that stale save with `409 portal.concurrency_conflict` instead of applying
      // it.
      //
      // Never fabricated: a read that served no token yields `null` here, which the server treats as
      // an opt-out and applies - the legacy last-writer-wins behaviour rather than a refusal.
      concurrencyToken: source.concurrencyToken,
    };
  }

  /**
   * Reads an optional whole number out of a text box.
   *
   * Unlike the fee and the quotas this one has no measured blank-to-zero default, because
   * the legacy control was a selector that always had something chosen. A blank box is
   * therefore absence, and zero — a genuine offset — is never confused with it.
   */
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
   * The subscription is tied to this screen's lifetime rather than tracked by hand. There is
   * no page store to defer to, and the shape this screen needs — the legacy indent, the
   * legacy filter and the selectable absent option — is a projection for this screen only,
   * which is why the transport is consulted directly and the projection stays local.
   *
   * A failure is recorded rather than escalated: the four selectors still work, showing the
   * pages already chosen, and the screen says so instead of blocking a save of the other
   * seventeen fields on a listing that is not needed to write them.
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
   * Whether the address has moved to a different portal since a page read was started.
   *
   * The fence is KEYED on the portal rather than counted, because the portal IS the identity of the
   * answer: a page list is adopted when it belongs to the portal now addressed and dropped when it does
   * not. Cancelling the superseded read already stops it arriving; this is the second fence, and it is
   * the one that holds for anything that arrives regardless.
   *
   * Compared with an exact `!==` against a value that may legitimately be MINUS ONE or ZERO, since
   * `dbo.Portals.PortalID` is declared `IDENTITY (-1, 1)` and both are real portals. A truthiness test
   * would read the first two portals of an installation as no portal at all.
   *
   * @param portalId The portal the read was issued for.
   * @returns `true` when the answer must be ignored.
   */
  private portalChanged(portalId: number): boolean {
    return this._portalId() !== portalId;
  }
}
