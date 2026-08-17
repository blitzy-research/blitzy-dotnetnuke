import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  type OnInit,
  type Signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import type { AbstractControl, ValidationErrors } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type {
  MembershipSettings,
  MembershipSettingsUpdateResult,
} from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { TabService } from '../../../core/services/tab.service';
import type { UserOperation } from '../../../core/state/user.store';
import { UserStore } from '../../../core/state/user.store';
import { fieldErrorMessage } from '../../../core/utils/form-errors.util';
import { buildPageChoices, type PageOption } from '../../../core/utils/page-options.util';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import {
  MEMBER_SERVICE_OPERATIONS,
  MemberServicesComponent,
} from './member-services/member-services.component';

// THE LEGACY DISPLAY VOCABULARIES

/** How the account listing presents its rows, as the legacy display mode. */
export const DISPLAY_MODE = Object.freeze({
  /** Every account at once. */
  all: 0,
  /** Grouped behind an initial-letter selector. */
  firstLetter: 1,
  /** Nothing until a search is performed. */
  none: 2,
} as const);

/** How widely a profile value may be seen when the account has not chosen. */
export const PROFILE_VISIBILITY_MODE = Object.freeze({
  /** Visible to anyone who can view the profile. */
  allUsers: 0,
  /** Visible to members of the tenant. */
  membersOnly: 1,
  /** Visible to the account itself and to administrators. */
  adminOnly: 2,
} as const);

/**
 * Which control the role-management screen uses to choose an account. the legacy default for this one was
 * DATA-DEPENDENT - it chose the text box over the picker once a tenant held more than a thousand
 * accounts.
 */
export const USERS_CONTROL = Object.freeze({
  /** A picker listing the accounts. */
  combo: 0,
  /** A plain text box, for tenants too large to list. */
  textBox: 1,
} as const);

/** One choice in a selector: the integer the server stores and the words shown for it. */
export interface MembershipSettingsOption {
  /** The integer the server stores. */
  readonly value: number;

  /** The words shown for it. */
  readonly label: string;
}

// SELECTOR OPTIONS
// FOUR of the eight enumeration members these selectors offer have NO label in the legacy resource file,
// and two of the four are the shipped DEFAULTS. The file supplies "First Letter", "Combo Box", "Text Box"
// and "Members Only" and stops there: there is no entry for the display mode's `All` member, none for its
// `None` member, none for the visibility `AllUsers` member and none for the visibility `AdminOnly` member.

/** The account-listing display modes, in the order the legacy selector offered them. */
export const DISPLAY_MODE_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  // Authored: the legacy resource file has no entry for this member.
  { value: DISPLAY_MODE.all, label: 'All accounts' },
  { value: DISPLAY_MODE.firstLetter, label: 'First Letter' },
  // Authored: no entry either, and this is the shipped default.
  { value: DISPLAY_MODE.none, label: 'None until searched' },
]);

/** The profile visibility modes, least private first, as the legacy editor ordered them. */
export const PROFILE_VISIBILITY_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  // Authored: the legacy resource file has no entry for this member.
  { value: PROFILE_VISIBILITY_MODE.allUsers, label: 'All Users' },
  { value: PROFILE_VISIBILITY_MODE.membersOnly, label: 'Members Only' },
  // Authored: no entry either, and this is the shipped default.
  { value: PROFILE_VISIBILITY_MODE.adminOnly, label: 'Administrators Only' },
]);

/** The account-selection controls the role-management screen can use. */
export const USERS_CONTROL_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  { value: USERS_CONTROL.combo, label: 'Combo Box' },
  { value: USERS_CONTROL.textBox, label: 'Text Box' },
]);

// ---------------------------------------------------------------------------
// THE MEASURED LEGACY DEFAULTS
// ---------------------------------------------------------------------------

/**
 * The policy a tenant that has never saved one is treated as having. Every value is transcribed from
 * `Library/Components/Users/UserModuleBase.vb` L98-L190, which is the legacy routine that filled in a
 * missing key, and each one was checked against the server contract's own default: the two agree member
 * for member, so this constant restates a value the server already holds rather than inventing one.
 */
export const LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS: MembershipSettings = Object.freeze({
  // ⚠ #6 — FALSE, AND SAYING SO IS THE WHOLE POINT OF THIS CONSTANT. These values are defaults, not
  // decisions, and the flag is how a screen tells the two apart.
  isStored: false,
  columnFirstName: false,
  columnLastName: false,
  columnDisplayName: true,
  columnAddress: true,
  columnTelephone: true,
  // Hidden by default. Measured, not a transcription slip - see the note above.
  columnEmail: false,
  columnCreatedDate: true,
  columnLastLogin: false,
  columnAuthorized: true,
  displayMode: DISPLAY_MODE.none,
  displaySuppressPager: false,
  recordsPerPage: DEFAULT_PAGE_SIZE,
  profileDefaultVisibility: PROFILE_VISIBILITY_MODE.adminOnly,
  profileDisplayVisibility: true,
  profileManageServices: true,

  // ZERO IS A REAL PAGE. The page table's identity seeds at zero, which is exactly why the server's floor
  // is zero rather than one, so zero must never be read as "unset". Null is the only expression of "unset"
  // on this contract.
  redirectAfterLogin: null,
  redirectAfterRegistration: null,
  redirectAfterLogout: null,

  // The legacy default was a constant on the excluded globals module, so it cannot be transcribed and is
  // deliberately not guessed at. Empty here means "the server has not told us yet"; the server supplies its
  // own expression with the policy, and this seat is overwritten the moment it arrives.
  securityEmailValidation: '',

  securityRequireValidProfile: false,
  // True while the registration counterpart above is false. Measured, not a slip.
  securityRequireValidProfileAtLogin: true,
  // The picker, matching the server. The legacy thousand-account rule is not here.
  securityUsersControl: USERS_CONTROL.combo,
  securityDisplayNameFormat: '',
});

// RECOVERED WORDING
// Three legacy spellings are PRESERVED verbatim below and must not be tidied. The sign-in profile
// requirement reads "before be logged in", which is ungrammatical and is the shipped wording.

/** The headings, help text and button labels this screen renders as chrome. */
export const MEMBERSHIP_SETTINGS_TEXT = Object.freeze({
  /** The screen title. */
  title: 'User Settings',

  /**
   * The subtitle. AUTHORED, and reported as such: the legacy screen had no subtitle, because the
   * DotNetNuke page chrome gave it none to render.
   */
  subtitle: 'Account administration settings for this site',

  /** The one surviving section heading. */
  sectionHeading: 'User Accounts Settings',

  /** The heading for the nine listing-column switches. */
  columnsHeading: 'Account Listing Columns',

  /** The submit button. */
  submitLabel: 'Update',

  /** The cancel button. */
  cancelLabel: 'Cancel',

  /** Announced while the policy is being read. */
  loadingLabel: 'Loading user settings…',

  /** Announced while the policy is being written. */
  savingLabel: 'Saving user settings…',

  /**
   * Shown once the policy has been written. AUTHORED. The legacy screen showed nothing at all on success
   * - it redirected straight back to the account listing - so there is no legacy wording to recover.
   */
  savedMessage: 'User settings saved.',

  /**
   * Shown once the policy has been written AND the new display-name format has been applied across the
   * tenant. AUTHORED, because the legacy screen reported nothing here either — and silence about a
   * tenant-wide rename is a gap rather than parity worth keeping.
   */
  savedWithRewriteMessage: 'User settings saved. {count} {accounts} renamed to match the new display name format.',

  /**
   * Shown when the display-name format changed but no account's name changed with it. ⚠ DISTINCT FROM
   * {@link savedMessage}, AND THE DISTINCTION IS THE POINT. "The sweep ran and found nothing to alter" is
   * a different answer from "no sweep ran", and an operator who has just changed the format is looking
   * for exactly that difference — being shown the plain confirmation would leave them unable to tell
   * whether the change took effect at all.
   */
  savedWithNoRewriteMessage:
    'User settings saved. No account names needed to change under the new display name format.',

  /**
   * Shown while the tenant DOES hold a stored policy, so the values are somebody's decision. ⚠ #6 — THIS
   * SENTENCE IS WHAT STOPS THE SCREEN LYING BY OMISSION. Every control below renders a value whether the
   * tenant stored one or not, and a rendered value looks identical either way: an operator reading
   * `Records Per Page 10` cannot otherwise tell whether somebody chose ten or whether ten is what this
   * platform falls back to.
   */
  storedNotice: 'These user settings are stored for this site.',

  /**
   * Said once, above the three redirect pickers, when the page listing could not be read. A reduced
   * affordance rather than a failure: the pickers still show whatever the tenant stored and every setting on
   * the screen can still be saved, so this is a note and not the error banner.
   */
  pagesUnavailableNotice:
    'The list of pages for this site could not be read, so the redirect choices below show only the ' +
    'pages these settings already point at. Every other setting can still be saved.',
} as const);

/**
 * Composes the confirmation for a written policy from what the write reported. Three sentences rather
 * than one, because the write has three genuinely different outcomes and collapsing them would withhold
 * from the operator the one fact they cannot obtain any other way: whether adopting a new display-name
 * format actually renamed anything.
 *
 * @param report What the write did beyond storing its values, or `null` when the server sent no report at
 * all — in which case the plain confirmation is the honest answer, because nothing is known about a
 * sweep.
 * @returns The sentence to show at the success severity.
 */
export function membershipSettingsSavedMessage(
  report: MembershipSettingsUpdateResult | null,
): string {
  if (report === null || !report.displayNameFormatChanged) {
    return MEMBERSHIP_SETTINGS_TEXT.savedMessage;
  }

  if (report.displayNamesRewritten === 0) {
    return MEMBERSHIP_SETTINGS_TEXT.savedWithNoRewriteMessage;
  }

  // ⚠ COMPARED AGAINST ONE RATHER THAN TESTED FOR TRUTHINESS. The zero case is handled above
  // and is a real answer, so the only question left here is singular against plural.
  return MEMBERSHIP_SETTINGS_TEXT.savedWithRewriteMessage
    .replace('{count}', String(report.displayNamesRewritten))
    .replace('{accounts}', report.displayNamesRewritten === 1 ? 'account was' : 'accounts were');
}

/** The label and help text for one field. */
export interface MembershipSettingsFieldText {
  /** The field label, colon and question mark exactly as the legacy file wrote them. */
  readonly label: string;

  /** The help text, or empty where the legacy file offered nothing worth showing. */
  readonly help: string;
}

export const MEMBERSHIP_SETTINGS_FIELD_TEXT: Readonly<
  Record<MembershipSettingsFieldName, MembershipSettingsFieldText>
> = Object.freeze({
  columnFirstName: { label: 'Show First Name Column:', help: '' },
  columnLastName: { label: 'Show Last Name Column:', help: '' },
  // Labelled "Name", not "Display Name", in the legacy file. Preserved.
  columnDisplayName: { label: 'Show Name Column:', help: '' },
  columnAddress: { label: 'Show Address Column:', help: '' },
  columnTelephone: { label: 'Show Telephone Column:', help: '' },
  columnEmail: { label: 'Show Email Column:', help: '' },
  columnCreatedDate: { label: 'Show Created Date Column:', help: '' },
  columnLastLogin: { label: 'Show Last Login Column:', help: '' },
  columnAuthorized: { label: 'Show Authorized Column:', help: '' },

  displayMode: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Default Display Mode',
    help: 'Select the default display mode for the Users Grid',
  },
  displaySuppressPager: {
    // The question mark is the legacy wording. Preserved.
    label: 'Suppress Pager?',
    help: 'Check to hide the pager if only one page of records',
  },
  recordsPerPage: {
    label: 'Users per Page:',
    help: 'Enter the number of users to display on a page',
  },

  profileDefaultVisibility: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Default Profile Visibility Mode',
    help: 'Select the default Profile Visibility Mode for the Users Profile',
  },
  profileDisplayVisibility: {
    label: 'Display Profile Visibility',
    help: 'Check to display the Profile Visibility control in the Users profile',
  },
  profileManageServices: {
    label: 'Display Manage Services',
    help: 'Check to display the Manage Services section in the Users profile',
  },

  redirectAfterLogin: {
    label: 'Redirect After Login:',
    help: 'You can select a page to redirect to after successful login',
  },
  redirectAfterRegistration: {
    label: 'Redirect After Registration:',
    help: 'You can select a page to redirect the user to, on successful registration.',
  },
  redirectAfterLogout: {
    label: 'Redirect After Logout:',
    help: 'You can select a page to redirect the user to, on logout.',
  },

  securityEmailValidation: {
    label: 'Email Address Validation:',
    help:
      'You can modify the provided Email Validation Expression, which is used to ' +
      'check the validity of email addresses provided.',
  },
  securityRequireValidProfile: {
    label: 'Require a valid Profile for Registration:',
    help: 'You can optionally require that a new user enters a valid profile during registration.',
  },
  securityRequireValidProfileAtLogin: {
    label: 'Require a valid Profile for Login:',
    // "before be logged in" is the legacy grammar. Preserved verbatim.
    help:
      'You can optionally require a user to update their Profile before be logged ' +
      'in, if their Profile is no longer Valid.',
  },
  securityUsersControl: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Users display mode in Manage Roles',
    help: 'Select the Users Control to use in the Manage Roles module control',
  },
  securityDisplayNameFormat: {
    label: 'Display Name Format:',
    // The legacy help text names bracketed substitution tokens, and they are described here rather than
    // made to work: token replacement is an excluded subsystem, so the value is stored and forwarded as
    // opaque text and this screen expands nothing.
    help:
      'You can optionally specify a format for the users display name. The format ' +
      'can include tokens for dynamic substitution such as [FIRSTNAME] [LASTNAME]. ' +
      'If a display name format is specified, the display name will no longer be ' +
      'editable through the user interface. Changing this value renames every ' +
      'account in this site to match it.',
  },
} as const);

// ---------------------------------------------------------------------------
// THE FORM
// ---------------------------------------------------------------------------

/**
 * The form behind this screen, one control per member of the account policy. this interface is what
 * REPLACES the legacy contract, and the replacement is the point of the exercise.
 */
export interface MembershipSettingsFormModel {
  readonly columnFirstName: FormControl<boolean>;
  readonly columnLastName: FormControl<boolean>;
  readonly columnDisplayName: FormControl<boolean>;
  readonly columnAddress: FormControl<boolean>;
  readonly columnTelephone: FormControl<boolean>;
  readonly columnEmail: FormControl<boolean>;
  readonly columnCreatedDate: FormControl<boolean>;
  readonly columnLastLogin: FormControl<boolean>;
  readonly columnAuthorized: FormControl<boolean>;
  readonly displayMode: FormControl<number>;
  readonly displaySuppressPager: FormControl<boolean>;
  readonly recordsPerPage: FormControl<number | null>;
  readonly profileDefaultVisibility: FormControl<number>;
  readonly profileDisplayVisibility: FormControl<boolean>;
  readonly profileManageServices: FormControl<boolean>;
  readonly redirectAfterLogin: FormControl<number | null>;
  readonly redirectAfterRegistration: FormControl<number | null>;
  readonly redirectAfterLogout: FormControl<number | null>;
  readonly securityEmailValidation: FormControl<string>;
  readonly securityRequireValidProfile: FormControl<boolean>;
  readonly securityRequireValidProfileAtLogin: FormControl<boolean>;
  readonly securityUsersControl: FormControl<number>;
  readonly securityDisplayNameFormat: FormControl<string>;
}

/** The name of one control on this screen's form. */
export type MembershipSettingsFieldName = keyof MembershipSettingsFormModel;

/** The name of one of the nine listing-column switches. */
export type ColumnToggleFieldName = Extract<
  MembershipSettingsFieldName,
  | 'columnFirstName'
  | 'columnLastName'
  | 'columnDisplayName'
  | 'columnAddress'
  | 'columnTelephone'
  | 'columnEmail'
  | 'columnCreatedDate'
  | 'columnLastLogin'
  | 'columnAuthorized'
>;

/**
 * The nine listing-column switches, in the order the legacy routine declared them. Published as a list so
 * the template renders them in one pass instead of repeating a near-identical block nine times, and
 * ordered as `Library/Components/Users/UserModuleBase.vb` L98-L124 ordered them so that an administrator
 * finds the switches where they have always been.
 */
export const COLUMN_TOGGLE_FIELDS: readonly ColumnToggleFieldName[] = Object.freeze([
  'columnFirstName',
  'columnLastName',
  'columnDisplayName',
  'columnAddress',
  'columnTelephone',
  'columnEmail',
  'columnCreatedDate',
  'columnLastLogin',
  'columnAuthorized',
] as const);

export const REDIRECT_FIELDS: readonly MembershipSettingsFieldName[] = Object.freeze([
  'redirectAfterLogin',
  'redirectAfterRegistration',
  'redirectAfterLogout',
] as const);

/** The form's value with every member present, as the group itself types it. */
export type MembershipSettingsFormValue = ReturnType<
  FormGroup<MembershipSettingsFormModel>['getRawValue']
>;

// BOUNDS AND DESTINATIONS

/** The smallest page size the server accepts. */
const MINIMUM_RECORDS_PER_PAGE = 1;

/** The smallest page identifier that can exist. The page table's identity seeds at zero. */
const MINIMUM_PAGE_IDENTIFIER = 0;

/** The longest a stored setting's text may be. */
const MAXIMUM_SETTING_LENGTH = 2000;

/**
 * Where both buttons go. MIGRATION: the legacy screen redirected on BOTH outcomes - the cancel handler at
 * `Website/admin/Users/UserSettings.ascx.vb` L140-L151 and the update handler at L163-L192 both ended in
 * a redirect back to the account listing - so leaving the reader on this screen after either would be the
 * divergence, not the navigation.
 */
const ACCOUNT_LISTING_PATH = '/users';

const PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions';

/** Shown when a required field has been left empty. */
const REQUIRED_MESSAGE = 'This setting is required.';

/**
 * The wording of the option that stores no redirect at all. NOT the legacy `<None Specified>` used by the
 * portal page selectors: for those, absence means the portal has no such page, whereas here it means the
 * account is left wherever it already is, and saying so is worth more than matching a sibling screen's
 * wording for a different question.
 */
const NO_REDIRECT_LABEL = 'No redirect (stay on the current page)';

/** Shown beneath a picker that currently stores no page. */
const NO_STORED_PAGE_NOTE = 'Stored value: no redirect.';

/**
 * Shown when the submitted address expression cannot be compiled at all. The server's own wording for the
 * same refusal, so the operator is told the same thing whichever layer catches it.
 */
const EXPRESSION_UNUSABLE_MESSAGE =
  'This expression could not be compiled as a regular expression, so it would refuse every address it ' +
  'was applied to. Enter a valid expression, or clear the field to restore the default.';

/**
 * Refuses an address-validation expression that is not a compilable regular expression.
 *
 * ⚠ COMPILABILITY IS THE ONLY QUESTION ASKED HERE, AND THAT IS DELIBERATE. Measured on this screen,
 * replacing the expression with `[a-z` left the field `ng-valid` with no message, no `aria-invalid` and no
 * banner, so a site-wide setting that would then refuse EVERY address could be typed and saved without a
 * word of warning. The server does refuse it - its own rule compiles the pattern and applies it to a probe
 * address - but a refusal that only arrives after a save is a refusal the operator has to discover.
 *
 * What this must NOT do is ask whether the expression is a WISE one. The browser never executes this value:
 * it is stored text, and the server is what applies it. Two of the four address expressions this platform
 * itself shipped are quadratic under backtracking, so screening for that here would refuse values the
 * server accepts and lock an operator out of a setting they are entitled to keep - a worse defect than the
 * one being fixed. The screen that DOES reject those patterns guards the profile screen, where the browser
 * really does run them against what the operator types.
 *
 * @param control The control holding the submitted expression.
 * @returns A validation error keyed `expressionUnusable`, or null when the expression compiles.
 */
function expressionCompiles(control: AbstractControl): ValidationErrors | null {
  const value: unknown = control.value;

  // An empty value is not an unusable expression - it is the absence of one, which restores the default and
  // is a legitimate answer. It is also what the framework's own validators skip, so the two agree.
  if (typeof value !== 'string' || value.trim().length === 0) {
    return null;
  }

  try {
    // Constructed AND applied, because construction alone does not prove usability: a pattern can compile
    // and still throw on use. The probe is an address, since an address is what this expression exists to
    // judge.
    const expression = new RegExp(value);

    expression.test('probe.address@example.com');

    return null;
  } catch {
    return { expressionUnusable: true };
  }
}

/**
 * The tenant's account-administration settings screen. the class name and this file's location are a
 * RUNTIME contract, not a convention.
 */
/**
 * The tokens `UserService.FormatDisplayName` substitutes. Anything else in square brackets survives into
 * every account's display name verbatim, which is what makes an unrecognised token a silent defect rather
 * than a harmless one.
 */
// THE NO-REDIRECT WORDING IS DECLARED ONCE, ABOVE. A terser 'No redirect' was written here as well; the
// fuller sentence is kept because it says what the setting DOES rather than only what it omits, and two
// constants of the same name cannot both stand.

/**
 * The wording for a stored identifier this site's page list does not carry - a page since moved to the
 * recycle bin, or one belonging to another tenant. It is OFFERED rather than dropped, so saving the form
 * cannot clear a setting the operator never touched.
 */
const UNLISTED_PAGE_PREFIX = 'Page ';

const DISPLAY_NAME_FORMAT_TOKENS: readonly string[] = Object.freeze([
  '[USERID]',
  '[FIRSTNAME]',
  '[LASTNAME]',
  '[USERNAME]',
]);

/** `Users.DisplayName` is declared `nvarchar(128)`, so a longer composition cannot be stored. */
const DISPLAY_NAME_MAXIMUM_LENGTH = 128;

/** The message shown when the format names no substitution at all. */
export const DISPLAY_NAME_FORMAT_NEEDS_A_TOKEN_MESSAGE =
  'The format must use at least one of [USERID], [FIRSTNAME], [LASTNAME] or [USERNAME]; without one, every'
  + ' account would be renamed to the same text.';

/** The message shown when the format names something that will never be substituted. */
export const DISPLAY_NAME_FORMAT_UNKNOWN_TOKEN_MESSAGE =
  'This site substitutes only [USERID], [FIRSTNAME], [LASTNAME] and [USERNAME]. Anything else in square'
  + ' brackets is stored exactly as typed.';

/** The message shown when the format's own fixed text cannot fit the stored column. */
export const DISPLAY_NAME_FORMAT_TOO_LONG_MESSAGE =
  `A display name is stored in ${DISPLAY_NAME_MAXIMUM_LENGTH} characters, and this format's own fixed text`
  + ' already exceeds that before any account values are substituted.';

/**
 * Validates the format that RENAMES EVERY ACCOUNT IN THE TENANT - U16. The screen already warns that it
 * does; what it did not do was check the value at all, so a format naming no token would have given every
 * account the same literal display name, and one naming a token this port does not substitute would have
 * stored the brackets verbatim. `UserService.FormatDisplayName` performs four ordinal replacements and
 * nothing else, and `UserInfo.vb` L362-L363 did the same, so the recognised set is closed.
 *
 * All three rules are reported together rather than one at a time, for the reason recorded on the
 * credential screen: revealing a second rule only once the first is satisfied is the same defect elsewhere.
 *
 * @param control The format control.
 * @returns The validation errors, or `null` when the format is usable.
 */
function displayNameFormatRules(control: AbstractControl): ValidationErrors | null {
  const format = typeof control.value === 'string' ? control.value : '';

  // An empty format is the tenant declining to compose display names at all, which is the stored default.
  if (format.trim().length === 0) {
    return null;
  }

  const failures: string[] = [];
  const upper = format.toUpperCase();

  if (!DISPLAY_NAME_FORMAT_TOKENS.some((token: string) => upper.includes(token))) {
    failures.push(DISPLAY_NAME_FORMAT_NEEDS_A_TOKEN_MESSAGE);
  }

  const bracketed = upper.match(/\[[^\]]*\]/g) ?? [];

  if (bracketed.some((token: string) => !DISPLAY_NAME_FORMAT_TOKENS.includes(token))) {
    failures.push(DISPLAY_NAME_FORMAT_UNKNOWN_TOKEN_MESSAGE);
  }

  // The fixed text is whatever remains once every substitution is removed: the part that cannot shrink.
  let fixed = format;

  for (const token of DISPLAY_NAME_FORMAT_TOKENS) {
    fixed = fixed.split(token).join('');
    fixed = fixed.split(token.toLowerCase()).join('');
  }

  if (fixed.length > DISPLAY_NAME_MAXIMUM_LENGTH) {
    failures.push(DISPLAY_NAME_FORMAT_TOO_LONG_MESSAGE);
  }

  return failures.length === 0 ? null : { displayNameFormat: failures };
}

@Component({
  selector: 'app-membership-settings',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    ErrorBannerComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    PageHeaderComponent,
    // The consolidated self-service subscription panel, rendered as this screen's last section. Listed here
    // because a standalone component absent from this array cannot be rendered - strict template checking
    // turns its element into a compilation error rather than a silent unknown tag.
    MemberServicesComponent,
  ],
  templateUrl: './membership-settings.component.html',
  styleUrl: './membership-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MembershipSettingsComponent implements OnInit {
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
  /**
   * The account store, injected rather than the transport service. the store, not the service, is the
   * correct collaborator here, and the reason is that the page size this screen edits is read by the
   * account listing too.
   */
  private readonly store = inject(UserStore);

  /**
   * The session, read for ONE question: which account the caller holds. That answer is the subject
   * account of the subscription panel this screen mounts, and it is the only thing this screen reads the
   * session for.
   */
  private readonly auth = inject(AuthStore);

  /** Cancels the page read when this screen goes away. */
  private readonly destroyRef = inject(DestroyRef);

  /** The confirmation channel. */
  private readonly notifications = inject(NotificationService);

  /**
   * The page listing, read for ONE purpose: to turn the three redirect destinations from numbers the
   * operator has to know into pages they can choose. This screen never writes a page.
   */
  private readonly pages = inject(TabService);

  private readonly router = inject(Router);

  /**
   * This screen's own element, used for exactly one thing: finding the outcome surface to reveal. Scoped
   * to the host rather than reaching for the document, so the element found can only ever be this
   * screen's banner - a query against the document would find whichever banner happened to render first.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Chrome wording, for the template. */
  protected readonly text = MEMBERSHIP_SETTINGS_TEXT;

  /** Per-field wording, for the template. */
  protected readonly fieldText = MEMBERSHIP_SETTINGS_FIELD_TEXT;

  /** The nine listing-column switches, in legacy order. */
  protected readonly columnFields = COLUMN_TOGGLE_FIELDS;

  protected readonly redirectFields = REDIRECT_FIELDS;

  /** The display-mode choices. */
  protected readonly displayModeOptions = DISPLAY_MODE_OPTIONS;

  /** The profile-visibility choices. */
  protected readonly profileVisibilityOptions = PROFILE_VISIBILITY_OPTIONS;

  /** The account-selection-control choices. */
  protected readonly usersControlOptions = USERS_CONTROL_OPTIONS;

  /** The page-size floor, so the template's own input can advertise it. */
  protected readonly minimumRecordsPerPage = MINIMUM_RECORDS_PER_PAGE;

  /** The page-size ceiling, from the shared constant. */
  protected readonly maximumRecordsPerPage = MAX_PAGE_SIZE;

  // THE PAGE-IDENTIFIER FLOOR IS NO LONGER EXPOSED TO THE TEMPLATE. It advertised a `min` on a bare number
  // box, and the three redirect destinations are pickers now, so nothing can be typed that needs a floor. The
  // constant itself still guards the form controls below, for the values arriving from the server.

  /** The wording of the no-redirect option, for the template. */
  protected readonly noRedirectLabel = NO_REDIRECT_LABEL;

  /** The stored-text ceiling. */
  protected readonly maximumSettingLength = MAXIMUM_SETTING_LENGTH;

  /** Where the cancel button and the post-save navigation go. */
  protected readonly accountListingPath = ACCOUNT_LISTING_PATH;

  /** Where the companion action link goes. */
  protected readonly profileDefinitionsPath = PROFILE_DEFINITIONS_PATH;

  /**
   * The form, seated with the measured legacy defaults and replaced when the policy arrives. Seating it
   * rather than leaving it empty is what makes the screen render a coherent set of controls on first
   * paint instead of a grid of blanks, and the values are the ones the legacy routine would have filled
   * in for a tenant that had saved nothing.
   */
  protected readonly form = new FormGroup<MembershipSettingsFormModel>({
    columnFirstName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnFirstName, {
      nonNullable: true,
    }),
    columnLastName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastName, {
      nonNullable: true,
    }),
    columnDisplayName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnDisplayName, {
      nonNullable: true,
    }),
    columnAddress: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAddress, {
      nonNullable: true,
    }),
    columnTelephone: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnTelephone, {
      nonNullable: true,
    }),
    columnEmail: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnEmail, {
      nonNullable: true,
    }),
    columnCreatedDate: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnCreatedDate, {
      nonNullable: true,
    }),
    columnLastLogin: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastLogin, {
      nonNullable: true,
    }),
    columnAuthorized: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAuthorized, {
      nonNullable: true,
    }),

    displayMode: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displayMode, {
      nonNullable: true,
    }),
    displaySuppressPager: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displaySuppressPager,
      { nonNullable: true },
    ),

    recordsPerPage: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage,
      {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.min(MINIMUM_RECORDS_PER_PAGE),
          Validators.max(MAX_PAGE_SIZE),
        ],
      },
    ),

    profileDefaultVisibility: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDefaultVisibility,
      { nonNullable: true },
    ),
    profileDisplayVisibility: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDisplayVisibility,
      { nonNullable: true },
    ),
    profileManageServices: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileManageServices,
      { nonNullable: true },
    ),

    // Not required: empty means no redirect, which is a legitimate policy and the shipped one. The floor
    // rule mirrors the server, whose own rule applies only when a value is supplied - and the framework's
    // range validators skip an empty value, so the two agree without a condition being written.
    redirectAfterLogin: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterLogin,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),
    redirectAfterRegistration: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterRegistration,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),
    redirectAfterLogout: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterLogout,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),

    // Two rules: how long the value may be, and whether it is a regular expression at all. The value is
    // still never APPLIED in the browser - `expressionCompiles` runs it against a fixed probe address and
    // nothing else - so no address the operator types is ever judged by a pattern this screen holds.
    securityEmailValidation: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityEmailValidation,
      {
        nonNullable: true,
        validators: [Validators.maxLength(MAXIMUM_SETTING_LENGTH), expressionCompiles],
      },
    ),

    securityRequireValidProfile: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfile,
      { nonNullable: true },
    ),
    securityRequireValidProfileAtLogin: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfileAtLogin,
      { nonNullable: true },
    ),
    securityUsersControl: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityUsersControl,
      { nonNullable: true },
    ),

    securityDisplayNameFormat: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityDisplayNameFormat,
      {
        nonNullable: true,
        validators: [Validators.maxLength(MAXIMUM_SETTING_LENGTH), displayNameFormatRules],
      },
    ),
  });

  /** The portal's pages, as received. Empty until the read lands, and empty again if it fails. */
  private readonly pageRows = signal<readonly TabListItem[]>([]);

  /**
   * The portal whose pages have already been asked for. A plain field rather than a signal, deliberately:
   * it is read and written inside the reader below and nothing renders it, so making it reactive would only
   * let the reader retrigger itself.
   */
  private pagesReadFor: number | null = null;

  /** Whether the page read failed, so the selectors are knowingly thin and say so. */
  private readonly pagesFailed = signal(false);

  // PROJECTED STATE

  /** Whether the policy is being read. */
  protected readonly loading: Signal<boolean> = this.store.membershipSettingsLoading;

  /** Whether the policy is being written. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * Which of the two provenances the values on screen have, or `null` while that is not yet known. ⚠ #6 —
   * THE ONE FACT THAT MAKES THE VALUES ON THIS SCREEN READABLE. `'stored'` means the tenant holds a
   * settings store of its own and every value below is somebody's decision.
   */
  protected readonly valueProvenance: Signal<'stored' | 'defaults' | null> = computed(() => {
    const settings = this.store.membershipSettings();

    if (settings === null) {
      return null;
    }

    return settings.isStored ? 'stored' : 'defaults';
  });

  protected readonly provenanceNotice: Signal<string | null> = computed(() => {
    switch (this.valueProvenance()) {
      case 'stored':
        return MEMBERSHIP_SETTINGS_TEXT.storedNotice;
      default:
        return null;
    }
  });

  /**
   * The account whose subscriptions the mounted panel manages: the caller's own, or `null` while the
   * session has not resolved one. ⚠ NULL IS PASSED THROUGH RATHER THAN SUBSTITUTED FOR. The session
   * resolves asynchronously, so "not yet known" is a real state on arrival; a sentinel would be
   * indistinguishable from a genuine key - zero and minus one are both real account identifiers in this
   * schema - and defaulting to any other account would point the panel at somebody else's subscriptions.
   */
  protected readonly ownAccountId: Signal<number | null> = computed(
    () => this.auth.currentUser()?.userId ?? null,
  );

  /**
   * The choices behind the three redirect destinations.
   *
   * ⚠ WHY A PICKER AT ALL. Measured on this screen, all three destinations were bare numeric spinners -
   * "Redirect After Login" invited a raw page identifier - directly beneath help text promising the operator
   * "can select a page to redirect to". Nothing on the screen said which numbers were legal, so the setting
   * was only usable by somebody who already knew the page table.
   *
   * The admission rules, the hierarchy indent and the retained-choice handling are the shared ones the portal
   * settings screen uses for its four page selectors, so the two screens cannot disagree about what a
   * choosable page is. The stored values are RETAINED even when the filter would hide them, which is what
   * stops saving anything else on this screen from silently discarding a redirect that points at a page since
   * recycled.
   */
  protected readonly pageOptions: Signal<readonly PageOption[]> = computed(() => {
    const settings = this.store.membershipSettings();

    const retain: number[] = [];

    if (settings !== null) {
      for (const reference of [
        settings.redirectAfterLogin,
        settings.redirectAfterRegistration,
        settings.redirectAfterLogout,
      ]) {
        if (reference !== null) {
          retain.push(reference);
        }
      }
    }

    // The administration band is excluded by identifier, and this screen does not read the portal detail
    // that carries it, so nothing is excluded on that ground here. A redirect INTO administration is a
    // legitimate choice for an administrator anyway, which is the opposite of the portal home page the
    // shared rule was written for.
    return buildPageChoices(this.pageRows(), null, retain);
  });

  /** Whether the page listing could not be read, so the pickers are knowingly thin. */
  protected readonly pagesUnavailable: Signal<boolean> = this.pagesFailed.asReadonly();

  /** The failure to show, or null. */
  protected readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null || MEMBER_SERVICE_OPERATIONS.includes(failure.operation)) {
      return null;
    }

    return failure.problem;
  });

    // A TRANSPORT-ONLY SENTENCE USED TO BE COMPOSED HERE, AND IT IS GONE BECAUSE THE FAILURE IT COVERED
  // CANNOT OCCUR ANY MORE. It existed for the one class of failure that carried no problem document: a
  // response this client could not decode, which reaches a subscriber as a plain error with no status and no
  // body. The account store now synthesises a document for exactly that case - `contractProblem`, titled
  // "Unexpected response" - so the shared banner above always has real wording, a real severity and a real
  // support reference to render, and a second surface saying the same thing in weaker words would be one
  // announcement too many.

  /**
   * Whether the form may be submitted. Refused while the policy is being read or written, and the refusal
   * is a correctness measure rather than a courtesy: until the server's policy has been applied, the form
   * still holds the seated defaults, and submitting those would overwrite a live policy with values
   * nobody chose.
   */
  protected readonly canSubmit: Signal<boolean> = computed(() => {
    if (this.loading() || this.saving()) {
      return false;
    }

    // ⚠ WITHHELD FOR A SECOND, DIFFERENT REASON: this tenant has nowhere to store a policy. The reasoning
    // above is about not overwriting a live policy with seated defaults, and it does not apply here because
    // there IS no store to overwrite.
    if (this.unconfigured()) {
      return false;
    }

    const failure = this.store.failure();

    return failure === null || failure.operation !== 'loadMembershipSettings';
  });

  /**
   * Whether this tenant stores no account policy at all, so none can be read or written. Distinct from a
   * read that failed: a failure is transient and retryable and keeps its banner, whereas this state is
   * stable and has nothing to retry.
   */
  protected readonly unconfigured: Signal<boolean> = computed(() =>
    this.store.membershipSettingsUnconfigured(),
  );

  /** The submission awaiting an outcome. */
  private pendingSubmit = false;

  /**
   * The policy already applied to the form, held by identity. The guard that makes seating the form
   * IDEMPOTENT. The store publishes a new object on every read, so comparing identity is what
   * distinguishes a genuinely new policy from a redraw provoked by something else.
   */
  private appliedSettings: MembershipSettings | null = null;

  constructor() {
    // Seat the form from the policy, once per policy.
    effect(() => {
      const settings = this.store.membershipSettings();

      if (settings === this.appliedSettings) {
        // Nothing has changed. Acting here would fight the person typing.
        return;
      }

      this.appliedSettings = settings;

      if (settings === null) {
        // The tenant has no policy recorded. The seated legacy defaults stand, which is exactly what the
        // legacy routine did for a missing key, so there is nothing to apply.
        return;
      }

      if (this.form.dirty) {
        // Edits in hand outrank a late arrival. Overwriting them would discard work
        // the person can see, and the policy is applied on the next clean load.
        return;
      }

      this.form.setValue(this.toFormValue(settings));
    });

    // Settle a submission exactly once, on the transition out of saving.
    effect(() => {
      // Both slices are read unconditionally so that this effect depends on both
      // however the guards below fall.
      const saving = this.store.saving();
      const failure = this.store.failure();

      if (this.pendingSubmit === false) {
        return;
      }

      if (saving) {
        // Still in flight.
        return;
      }

      // Settled. Cleared before acting, so a redraw cannot settle it twice.
      this.pendingSubmit = false;

      if (failure === null) {
        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS ALREADY
        // SAVED. This effect fires on the transition OUT of saving, so `store.saving()` is already false by
        // the time these lines run, while the controls are still dirty from the operator's typing - and the
        // probe registered at the top of this class reads exactly `dirty && saving() === false`.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        // ⚠ THE REPORT IS READ HERE RATHER THAN RENDERED ON THIS SCREEN, because this screen is about to
        // leave. The legacy handler redirected to the account listing on success and that navigation is
        // reproduced below, so a panel raised here would be destroyed before it could be read.
        this.notifications.success(
          membershipSettingsSavedMessage(this.store.lastSettingsWrite()),
          true,
        );
        this.notifications.retainAcrossNavigation();

        // Discarded once reported, so returning to this screen does not re-announce a sweep
        // that happened during a previous visit.
        this.store.clearSettingsWriteReport();
        // Replaced, not pushed: the settings are saved, so BACK must not return to the form.
        void this.router.navigate([ACCOUNT_LISTING_PATH], { replaceUrl: true });

        // Nothing below applies to a success: the screen is leaving, and there is no banner to reveal.
        // Returning says so, rather than relying on the reveal finding no element to act on.
        return;
      }

      this.revealOutcome();
    });
  }

  /**
   * Brings this screen's outcome surface into view and puts focus on it. SCROLLED AND FOCUSED, not one or
   * the other, because the two serve different readers and neither substitutes for the other.
   */
  private revealOutcome(): void {
    const banner = this.host.nativeElement.querySelector<HTMLElement>('.error-banner-live');

    if (banner === null) {
      return;
    }

    banner.scrollIntoView({ block: 'nearest', behavior: 'auto' });
    banner.focus({ preventScroll: true });
  }

  /**
   * Reads the policy on arrival. read unconditionally, as the legacy page did - its load handler fetched
   * the settings on every request, including a postback.
   */
  ngOnInit(): void {
    this.store.loadMembershipSettings();
  }

  /**
   * Reads the portal's pages once per portal, for all three redirect pickers to share.
   *
   * ⚠ REACTIVE RATHER THAN READ ONCE ON ARRIVAL, AND MEASURED BEFORE IT WAS. The session resolves
   * asynchronously, so "which portal is this?" is genuinely unanswered for the first moments of the screen's
   * life - and a single read taken in the initialiser therefore found no portal, returned, and left all three
   * pickers permanently thin however quickly the session then arrived. Keyed on the portal so a session that
   * resolves late is picked up, and guarded on the portal already read so a redraw cannot re-read it.
   *
   * ⚠ A FAILED READ IS NOT A FAILED SCREEN. The pages are an affordance over a value the operator can
   * already hold, so losing them must not stop the other twenty-two settings being edited: the pickers fall
   * back to whatever the tenant has stored, a note says so, and nothing is refused.
   */
  private readonly pageReader = effect(() => {
    const portalId = this.auth.portalId();

    if (portalId === null || portalId === this.pagesReadFor) {
      return;
    }

    this.pagesReadFor = portalId;

    this.pages
      .getByPortal(portalId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (rows: readonly TabListItem[]) => {
          this.pageRows.set(rows);
          this.pagesFailed.set(false);
        },
        error: () => {
          this.pageRows.set([]);
          this.pagesFailed.set(true);
        },
      });
  });

  // -------------------------------------------------------------------------
  // TEMPLATE ACCESSORS
  // -------------------------------------------------------------------------

  /**
   * The element identifier for one field's control. Exists so the shared field component can be given
   * something to point its label at, which is what associates the two for a screen reader.
   *
   * @param field The control's name.
   * @returns A stable, unique element identifier.
   */
  protected controlId(field: MembershipSettingsFieldName): string {
    return `membership-setting-${field}`;
  }

  /**
   * The stored page identifier for one redirect setting, as the secondary fact beneath its picker.
   *
   * Read from the FORM rather than from the stored policy, so it tracks the operator's current choice
   * rather than describing a value they have already replaced on screen.
   *
   * @param field The redirect setting's control name.
   * @returns The sentence to show beneath the picker.
   */
  protected storedPageNote(field: MembershipSettingsFieldName): string {
    const value: unknown = this.form.controls[field].value;

    if (typeof value !== 'number' || Number.isFinite(value) === false) {
      return NO_STORED_PAGE_NOTE;
    }

    return `Stored value: page ${value}.`;
  }

  /**
   * The label for one field, as the legacy resource file wrote it.
   *
   * @param field The control's name.
   * @returns The label, colon and question mark included where the legacy file had them.
   */
  protected labelFor(field: MembershipSettingsFieldName): string {
    return MEMBERSHIP_SETTINGS_FIELD_TEXT[field].label;
  }

  /**
   * The help text for one field, or empty where there is none worth showing. Empty for the nine
   * listing-column switches, whose legacy help repeated their labels.
   *
   * @param field The control's name.
   * @returns The help text, possibly empty.
   */
  protected helpFor(field: MembershipSettingsFieldName): string {
    return MEMBERSHIP_SETTINGS_FIELD_TEXT[field].help;
  }

  /**
   * Whether one field must be supplied.
   *
   * @param field The control's name.
   * @returns Whether the field is required.
   */
  protected isRequired(field: MembershipSettingsFieldName): boolean {
    return this.form.controls[field].hasValidator(Validators.required);
  }

  /**
   * The message to show beside one field, or null when it has nothing to say.
   *
   * @param field The control's name.
   * @returns The message, or null.
   */
  protected errorFor(field: MembershipSettingsFieldName): string | null {
    const fromServer = fieldErrorMessage(this.problem(), field);

    if (fromServer !== null) {
      return fromServer;
    }

    const control = this.form.controls[field];

    if (control.valid) {
      return null;
    }

    // Measured before this split: typing 101 into "Users per Page" left the field with no message, no red
    // border and no `aria-invalid` until focus moved away, so the operator was told the value was unusable
    // only after they had stopped looking at it.
    if (control.hasError('required')) {
      // ⚠ `pristine` AND `untouched`, NOT `untouched` ALONE. Measured on this screen, EMPTYING a required
      // box reported nothing at all until focus moved away: clearing it makes the control dirty but leaves
      // it untouched, so the message the operator most needed - the one saying the value they just deleted
      // is required - was the one message withheld. A field the operator has not been near still stays
      // silent, which is the whole point of gating it.
      return control.untouched && control.pristine ? null : REQUIRED_MESSAGE;
    }

    if (control.hasError('min') || control.hasError('max')) {
      return this.rangeMessage(field);
    }

    if (control.hasError('maxlength')) {
      return `This setting may not exceed ${MAXIMUM_SETTING_LENGTH} characters.`;
    }

    if (control.hasError('expressionUnusable')) {
      return EXPRESSION_UNUSABLE_MESSAGE;
    }

    // U16 - EVERY UNMET RULE, TOGETHER. This one setting renames every account in the tenant, and the
    // screen already says so; reporting one broken rule at a time would mean fixing it revealed another.
    const formatFailures = control.getError('displayNameFormat') as readonly string[] | undefined;

    if (formatFailures !== undefined && formatFailures.length > 0) {
      return formatFailures.join(' ');
    }

    return null;
  }

  /**
   * The wording for a value outside its permitted range.
   *
   * @param field The control's name.
   * @returns The sentence to show.
   */
  private rangeMessage(field: MembershipSettingsFieldName): string {
    if (field === 'recordsPerPage') {
      return (
        `The number of accounts per page must be between ${MINIMUM_RECORDS_PER_PAGE} ` +
        `and ${MAX_PAGE_SIZE}.`
      );
    }

    return 'A page identifier may not be negative. Leave the field empty for no redirect.';
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Writes the policy, then leaves for the account listing. the whole policy is sent, not just what
   * changed.
   */
  protected submit(): void {
    if (this.canSubmit() === false) {
      return;
    }

    this.form.markAllAsTouched();

    if (this.form.invalid) {
      return;
    }

    this.pendingSubmit = true;
    this.store.saveMembershipSettings(this.buildRequest());
  }

  /**
   * Leaves for the account listing without writing anything. this must NOT provoke validation, and the
   * legacy markup is explicit about it - the cancel button carried `causesvalidation="False"`.
   */
  protected cancel(): void {
    this.store.clearFailure();
    void this.router.navigate([ACCOUNT_LISTING_PATH]);
  }

  // MAPPING
  // * the untyped table reads throughout `UserModuleBase.GetSettings`, where a missing key was read as
  // nothing and then used as a zero or an empty string; * the three casts that routine performed on values
  // it had just read back out of its own table, at `Library/Components/Users/UserModuleBase.vb` L129
  // (display mode), L141 (profile visibility) and L185 (account-selection control) - all three are handled
  // here by the numeric conversion below, which is why that conversion exists at all; * the coercion of
  // every stored value to text at `Website/admin/Users/UserSettings.ascx.vb` L174, replaced by sending each
  // member as its own type; * the conditionally sized array at the same file's L141, whose size came from a
  // query-string test - it has no counterpart because the query-string carrying is not reproduced; and *
  // the cast of a stored setting straight to an integer at `Website/admin/Users/Users.ascx.vb` L117,
  // replaced by the numeric conversion below, which answers with the measured default rather than throwing
  // when the value is not a finite number.

  /**
   * The policy as the form holds it.
   *
   * @param settings The policy the server sent.
   * @returns Every control's value, ready for the form.
   */
  private toFormValue(settings: MembershipSettings): MembershipSettingsFormValue {
    return {
      columnFirstName: this.asFlag(
        settings.columnFirstName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnFirstName,
      ),
      columnLastName: this.asFlag(
        settings.columnLastName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastName,
      ),
      columnDisplayName: this.asFlag(
        settings.columnDisplayName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnDisplayName,
      ),
      columnAddress: this.asFlag(
        settings.columnAddress,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAddress,
      ),
      columnTelephone: this.asFlag(
        settings.columnTelephone,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnTelephone,
      ),
      columnEmail: this.asFlag(
        settings.columnEmail,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnEmail,
      ),
      columnCreatedDate: this.asFlag(
        settings.columnCreatedDate,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnCreatedDate,
      ),
      columnLastLogin: this.asFlag(
        settings.columnLastLogin,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastLogin,
      ),
      columnAuthorized: this.asFlag(
        settings.columnAuthorized,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAuthorized,
      ),

      displayMode: this.asNumber(
        settings.displayMode,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displayMode,
      ),
      displaySuppressPager: this.asFlag(
        settings.displaySuppressPager,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displaySuppressPager,
      ),
      recordsPerPage: this.asNumber(
        settings.recordsPerPage,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage,
      ),

      profileDefaultVisibility: this.asNumber(
        settings.profileDefaultVisibility,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDefaultVisibility,
      ),
      profileDisplayVisibility: this.asFlag(
        settings.profileDisplayVisibility,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDisplayVisibility,
      ),
      profileManageServices: this.asFlag(
        settings.profileManageServices,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileManageServices,
      ),

      // Passed through as sent. Null means no redirect; zero is a page like any
      // other. Neither is substituted for the other.
      redirectAfterLogin: this.asPageIdentifier(settings.redirectAfterLogin),
      redirectAfterRegistration: this.asPageIdentifier(settings.redirectAfterRegistration),
      redirectAfterLogout: this.asPageIdentifier(settings.redirectAfterLogout),

      securityEmailValidation: this.asText(settings.securityEmailValidation),
      securityRequireValidProfile: this.asFlag(
        settings.securityRequireValidProfile,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfile,
      ),
      securityRequireValidProfileAtLogin: this.asFlag(
        settings.securityRequireValidProfileAtLogin,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfileAtLogin,
      ),
      securityUsersControl: this.asNumber(
        settings.securityUsersControl,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityUsersControl,
      ),
      securityDisplayNameFormat: this.asText(settings.securityDisplayNameFormat),
    };
  }

  /**
   * The policy as the form now holds it, ready to send. Every member is listed, and listing them is the
   * safeguard: a spread of the raw value would send whatever the form happened to contain, whereas naming
   * all twenty-three makes a member added to the model without being handled here a compilation failure
   * rather than a silently absent field on the wire.
   *
   * @returns The complete policy.
   */
  private buildRequest(): MembershipSettings {
    const raw = this.form.getRawValue();

    return {
      // ⚠ #6 — SENT, AND SENT AS RECEIVED. This is not a form value and the operator cannot change it: it
      // is the server's statement about where the policy it served came from, and it travels back untouched
      // because the API binds request bodies with unmapped-member handling set to disallow, so a member
      // present on the read and absent from the write would make every save `400`.
      isStored:
        this.store.membershipSettings()?.isStored ?? LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.isStored,

      columnFirstName: raw.columnFirstName,
      columnLastName: raw.columnLastName,
      columnDisplayName: raw.columnDisplayName,
      columnAddress: raw.columnAddress,
      columnTelephone: raw.columnTelephone,
      columnEmail: raw.columnEmail,
      columnCreatedDate: raw.columnCreatedDate,
      columnLastLogin: raw.columnLastLogin,
      columnAuthorized: raw.columnAuthorized,

      displayMode: raw.displayMode,
      displaySuppressPager: raw.displaySuppressPager,

      // The contract requires a number here.
      recordsPerPage:
        raw.recordsPerPage === null
          ? LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage
          : raw.recordsPerPage,

      profileDefaultVisibility: raw.profileDefaultVisibility,
      profileDisplayVisibility: raw.profileDisplayVisibility,
      profileManageServices: raw.profileManageServices,

      // Sent exactly as held, null included. Null is the contract's own expression of "no redirect", and
      // the server refuses a negative identifier - so substituting the legacy minus-one marker here would
      // turn a valid policy into a rejection.
      redirectAfterLogin: raw.redirectAfterLogin,
      redirectAfterRegistration: raw.redirectAfterRegistration,
      redirectAfterLogout: raw.redirectAfterLogout,

      securityEmailValidation: raw.securityEmailValidation,
      securityRequireValidProfile: raw.securityRequireValidProfile,
      securityRequireValidProfileAtLogin: raw.securityRequireValidProfileAtLogin,
      securityUsersControl: raw.securityUsersControl,
      securityDisplayNameFormat: raw.securityDisplayNameFormat,
    };
  }

  /**
   * A switch as sent, or the measured legacy default when it was sent as nothing. Both states are tested
   * for explicitly.
   *
   * @param value The value as sent.
   * @param fallback The measured legacy default for this switch.
   * @returns The switch's value.
   */
  private asFlag(value: boolean | null | undefined, fallback: boolean): boolean {
    if (value === true) {
      return true;
    }

    if (value === false) {
      return false;
    }

    return fallback;
  }

  /**
   * A number as sent, or the measured legacy default when it was sent as nothing or as something that is
   * not a finite number.
   *
   * @param value The value as sent.
   * @param fallback The measured legacy default for this member.
   * @returns The member's value.
   */
  private asNumber(value: number | null | undefined, fallback: number): number {
    if (value === null || value === undefined) {
      return fallback;
    }

    return Number.isFinite(value) ? value : fallback;
  }

  /**
   * A page identifier as sent, unchanged. Null stays null, because null is how this contract says "no
   * redirect".
   *
   * @param value The identifier as sent.
   * @returns The identifier, or null for no redirect.
   */
  private asPageIdentifier(value: number | null | undefined): number | null {
    if (value === null || value === undefined) {
      return null;
    }

    return Number.isFinite(value) ? value : null;
  }

  /**
   * Text as sent, with nothing normalised to empty.
   *
   * @param value The text as sent.
   * @returns The text, or empty.
   */
  private asText(value: string | null | undefined): string {
    if (value === null || value === undefined) {
      return '';
    }

    return value;
  }
}
