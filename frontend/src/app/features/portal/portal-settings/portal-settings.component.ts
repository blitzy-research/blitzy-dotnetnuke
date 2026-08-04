import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  inject,
  Input,
  Output,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import {
  BannerAdvertisingMode,
  UserRegistrationMode,
} from '../../../core/models/portal.model';
import type {
  PortalSettings,
  PortalSettingsLookups,
  UpdatePortalRequest,
} from '../../../core/models/portal.model';
import type { SelectOption } from '../../../core/models/select-option.model';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

/**
 * The two surviving top-level groups of the legacy screen, used as tab keys.
 *
 * `Website/admin/Portal/sitesettings.ascx` carries three `dnn:SectionHead`
 * controls with `IncludeRule="True"` — Basic Settings (L14), Advanced Settings
 * (L204) and Stylesheet Editor (L539). Skinning is out of scope, so the
 * stylesheet editor leaves with it and two groups remain.
 */
export type PortalSettingsTab = 'basic' | 'advanced';

/**
 * The eight nested sections that survive, in legacy source order.
 *
 * Two legacy sub-sections are absent from this list and their absence is
 * deliberate rather than an omission, because not one of their controls has a
 * counterpart on the update contract:
 *
 * - Usability Settings (L339-L383) held the inline-editor switch and the three
 *   control-panel settings. The control panel is Web Forms chrome and the inline
 *   editor is a rich-text provider; both are out of scope.
 * - SSL Settings (L494-L534) held four transport settings. Transport security
 *   terminates at the reverse proxy in the target topology, so the site does not
 *   administer it.
 */
export type PortalSettingsSection =
  | 'siteDetails'
  | 'marketing'
  | 'appearance'
  | 'security'
  | 'pages'
  | 'payment'
  | 'other'
  | 'host';

/** Shape of the settings form. One control per writable request property. */
interface PortalSettingsFormModel {
  portalName: FormControl<string>;
  description: FormControl<string>;
  keyWords: FormControl<string>;
  footerText: FormControl<string>;
  logoFile: FormControl<string>;
  backgroundFile: FormControl<string>;
  bannerAdvertising: FormControl<BannerAdvertisingMode>;
  userRegistration: FormControl<UserRegistrationMode>;
  splashTabId: FormControl<number | null>;
  homeTabId: FormControl<number | null>;
  loginTabId: FormControl<number | null>;
  userTabId: FormControl<number | null>;
  homeDirectory: FormControl<string>;
  currency: FormControl<string>;
  paymentProcessor: FormControl<string>;
  processorUserId: FormControl<string>;
  processorPassword: FormControl<string>;
  administratorId: FormControl<number | null>;
  defaultLanguage: FormControl<string>;
  timeZoneOffset: FormControl<number | null>;
  expiryDate: FormControl<string>;
  hostFee: FormControl<number | null>;
  hostSpace: FormControl<number | null>;
  pageQuota: FormControl<number | null>;
  userQuota: FormControl<number | null>;
  siteLogHistory: FormControl<number | null>;
}

/** A radio choice, carrying its own code so the template needs no lookup. */
interface RadioChoice<TValue> {
  readonly value: TValue;
  readonly label: string;
}

// -----------------------------------------------------------------------------
//  MEASURED LIMITS — mirrored from UpdatePortalRequestValidator, not invented
// -----------------------------------------------------------------------------
// Each constant restates a bound the server already enforces, so a submission the
// form accepts is a submission the API accepts. Where the server has NO rule, none
// is added here: the legacy screen declares no `RequiredFieldValidator` at all, so
// no control carries `Validators.required` and a blank field is a legitimate
// submission exactly as it was before.
const PORTAL_NAME_MAX = 128;
const METADATA_MAX = 500;
const FOOTER_TEXT_MAX = 100;
const FILE_NAME_MAX = 50;
const PROCESSOR_FIELD_MAX = 50;
const DEFAULT_LANGUAGE_MAX = 6;
const HOME_DIRECTORY_MAX = 100;
const CURRENCY_CODE_LENGTH = 3;

/** Length of the `YYYY-MM-DD` prefix of a serialised instant. */
const DATE_PREFIX_LENGTH = 10;

/**
 * The empty choice offered by every select.
 *
 * `SiteSettings.ascx.vb:L264` composes it as `"<" +
 * Localization.GetString("None_Specified") + ">"` against a resource whose value
 * is `None Specified`, and inserts it at position zero with an empty value. Both
 * the angle brackets and the position are reproduced.
 */
const NONE_SPECIFIED = '<None Specified>';

/**
 * Messages taken verbatim from `UpdatePortalRequestValidator`.
 *
 * Retyping a message is how a client and a server drift apart, so each of these is
 * a character-for-character copy of the constant the validator declares. The
 * operator sees the same sentence whether the form caught the mistake or the API
 * did.
 */
const CURRENCY_LENGTH_MESSAGE = 'Currency must be a three letter code.';
const HOST_FEE_NEGATIVE_MESSAGE = 'Hosting Fee must be zero or greater.';
const HOST_SPACE_NEGATIVE_MESSAGE = 'Disk Space must be zero or greater.';
const PAGE_QUOTA_NEGATIVE_MESSAGE = 'Page Quota must be zero or greater.';
const USER_QUOTA_NEGATIVE_MESSAGE = 'User Quota must be zero or greater.';
const SITE_LOG_HISTORY_NEGATIVE_MESSAGE = 'Site Log History must be zero or greater.';

/**
 * Per-field help text, taken from
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx`.
 *
 * These live here, in the component, rather than as literal text in the template,
 * and the reason is measured rather than stylistic. Angular compiles templates
 * with `preserveWhitespaces` disabled by default, which collapses every run of
 * whitespace in a text node to a single space. The legacy resource strings put TWO
 * spaces after a sentence period — `plPortalName.Help` and `plKeyWords.Help` both
 * do — so writing them as template text silently rewrote them, and the operator
 * would have read subtly different wording from the one the old site showed.
 * Interpolated values are not whitespace-collapsed, so binding them preserves each
 * string character for character.
 *
 * Keyed by form-control name so the template addresses a hint the same way it
 * addresses the control, and so a field added without its help text fails to
 * compile rather than rendering blank.
 */
const FIELD_HINTS = {
  portalName:
    'This is the Title for your portal.  The text you enter will show up in the Title Bar.',
  description: 'Enter a description about your site here.',
  keyWords:
    'Enter some keywords for your site (separated by commas).  These keywords are used by search engines to help index your site.',
  footerText: 'If supported by the skin this Copyright text is displayed on your site.',
  guid: 'The globally unique identifier which can be used to identify this portal.',
  bannerAdvertising:
    'Indicate the type of Banner Advertising you wish to display on your site.',
  logoFile:
    'Depending on the skin chosen, this image will appear in the top left corner of the page.',
  backgroundFile:
    'Depending on the skin, if selected, an image will display in the background of all pages.',
  // No trailing period in the legacy string. Preserved rather than tidied.
  userRegistration: 'The type of user registration allowed for this site',
  splashTabId: 'The Splash Page for your site.',
  homeTabId: 'The Home Page for your site.',
  loginTabId: 'The Login Page for your site.',
  userTabId: 'The User Page for your site.',
  // Likewise no trailing period in the legacy string.
  homeDirectory: 'Enter the Home Directory for this site',
  currency: 'The Currency used on the site.',
  paymentProcessor: 'The Payment Processor used to handle payments on the site.',
  processorUserId: 'The UserId for the Payment Processor.',
  // MIGRATION: the second sentence has no legacy counterpart and is added because
  // the behaviour it describes has none either — the legacy screen bound the
  // stored password into the box, so "leave it blank" meant "clear it". Here the
  // response carries no password, so blank has to mean "unchanged", and an
  // operator who is not told that would reasonably expect the opposite.
  processorPassword:
    'User Password for the Payment Processor. Leave blank to keep the stored password.',
  administratorId: 'The Administrator User for the site.',
  defaultLanguage: 'The Default Language for the site.',
  timeZoneOffset: 'The TimeZone for the location of the site.',
  expiryDate: 'The Expiry Date is the date that the Hosting Contract for the portal expires.',
  hostFee: 'The Hosting Fee is the monthly charge for hosting this site.',
  hostSpace:
    'The amount of Disk Space in MB allowed for this site (enter 0 for unlimited space).',
  pageQuota: 'You can specify a maximum number of pages per portal.',
  userQuota: 'You can specify a maximum number of users per portal.',
  siteLogHistory: 'The number of days of site activity that is kept for this site.',
} as const;

/**
 * The advisory shown when banner advertising is held at host level.
 *
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` declares
 * `lblBanners.Text` as `<br>Banner option was set by the hostingprovider, and
 * cannot be changed`. The leading line break was layout, not wording, and is
 * dropped because the notice is its own block element here. The misspelling in
 * "hostingprovider" is preserved: it is text an existing operator recognises, and
 * silently correcting migrated wording is how a migration stops being verifiable.
 */
const BANNER_HOST_LOCK_NOTICE =
  'Banner option was set by the hostingprovider, and cannot be changed';

/**
 * The delete confirmation, from `DeleteMessage.Text` in the same resource file.
 *
 * Reproduced character for character, including the space before the question
 * mark, which is how the legacy string is written.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Portal ?';

/**
 * Rejects a value whose trimmed length is not exactly `length`, and passes a blank
 * value through.
 *
 * Mirrors the server rule, which is `Length(3)` guarded by
 * `When(request => !string.IsNullOrWhiteSpace(request.Currency))` — so a blank
 * currency is admitted and a one- or two-letter currency is not.
 */
function exactLengthWhenPresent(length: number, message: string): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw = control.value;
    if (typeof raw !== 'string' || raw.trim().length === 0) {
      return null;
    }

    return raw.trim().length === length ? null : { exactLength: message };
  };
}

/**
 * Returns the option list, with the currently-held value appended when the list
 * does not already offer it.
 *
 * MIGRATION: the legacy screen bound each `asp:DropDownList` to a server-side
 * collection and then called `FindByValue` to select the stored entry. When the
 * collection did not contain that entry — a page since deleted, a currency since
 * withdrawn — nothing was selected, and the next post wrote the list's first entry
 * over the stored value. The target refuses to lose data that way: a held value
 * absent from the list is offered as its own option, so opening the screen and
 * pressing Update cannot silently change a field the operator never touched.
 */
function withHeldValue<TValue extends string | number>(
  options: readonly SelectOption<TValue>[],
  held: TValue | null,
): readonly SelectOption<TValue>[] {
  if (held === null) {
    return options;
  }

  if (options.some((option) => option.value === held)) {
    return options;
  }

  return [...options, { value: held, label: String(held) }];
}

/**
 * Narrows a serialised instant to the `YYYY-MM-DD` value a date input accepts.
 *
 * Deliberately a string slice rather than `new Date(iso)` followed by a local
 * formatting call. Parsing shifts the instant into the browser's zone, which moves
 * the date by a day either side of midnight — so a contract expiring on the first
 * of the month would be shown, and then written back, as the last day of the
 * previous one. The stored value is a date, not a moment, and slicing preserves it.
 */
function toDateInputValue(instant: string | null | undefined): string {
  if (typeof instant !== 'string' || instant.length < DATE_PREFIX_LENGTH) {
    return '';
  }

  return instant.slice(0, DATE_PREFIX_LENGTH);
}

/** Trims a form value and reports a blank one as absent. */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Site settings screen — route `/portals/:portalId/settings`.
 *
 * Replaces `Website/admin/Portal/sitesettings.ascx` (568 lines) and its
 * code-behind `SiteSettings.ascx.vb`. The legacy screen's module title is
 * `Site Settings`, recorded in the seed data at
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L7062`,
 * and that is the default heading here.
 *
 * WHAT THIS COMPONENT IS
 *
 * Presentational. Settings arrive through `settings`, lookup lists through
 * `lookups`, and intent leaves through `save`, `cancel` and `remove`. It performs
 * no HTTP and holds no injected service, because the frontend service inventory is
 * closed and the API services this screen would need do not exist yet. Adding one
 * without its paired spec would create exactly the orphan the review flagged, so
 * the seam is an input and an output instead — which is also what makes every
 * branch below directly testable.
 *
 * THE TWENTY-SIX FIELDS
 *
 * `UpdatePortalRequest` declares twenty-seven properties. One of them, `portalId`,
 * is carried rather than edited. The remaining twenty-six each appear exactly once
 * below, distributed across the eight surviving sections in legacy source order.
 * The count is not a coincidence and it is worth stating: it is the arithmetic
 * that proves no field was dropped in the move from three groups of layout tables
 * to two tabs.
 *
 * WHOLE-ROW REPLACEMENT
 *
 * The update endpoint replaces the row. An omitted numeric term is therefore not
 * "leave it alone" — the server-side mapper substitutes zero, because the backing
 * columns cannot hold null. Two consequences are designed for rather than
 * discovered:
 *
 * - The form always submits every field it holds, including the host-administered
 *   group, and populates that group from the loaded settings even when the
 *   operator may not edit it. Omitting it would waive the hosting charge and lift
 *   every quota.
 * - The payment-processor password is the sole exception. The response type
 *   carries no counterpart, so the form cannot pre-populate it; a blank
 *   submission is sent as `null`, which the server reads as "leave the stored
 *   secret alone".
 */
@Component({
  selector: 'app-portal-settings',
  standalone: true,
  // ReactiveFormsModule for the typed form; four shared components for the
  // screen title, the fetching and empty affordances, and the delete
  // confirmation. Nothing else is imported — the tab strip and the collapsible
  // section head have no shared component, which is why the paired stylesheet
  // owns them, and every control below is a bare element that the global form
  // partial already styles.
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './portal-settings.component.html',
  styleUrl: './portal-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalSettingsComponent {
  /**
   * The host element, used only to move focus between tabs during keyboard
   * navigation.
   *
   * A query rather than a `@ViewChildren` list because the tab buttons carry
   * stable ids and are always present, so a lookup by id is exact and needs no
   * change-detection round trip to become available.
   */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /** The typed form. Every control is `nonNullable`, so `value` is never partial. */
  protected readonly form = new FormGroup<PortalSettingsFormModel>({
    portalName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PORTAL_NAME_MAX)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX)],
    }),
    keyWords: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX)],
    }),
    footerText: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(FOOTER_TEXT_MAX)],
    }),
    logoFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(FILE_NAME_MAX)],
    }),
    backgroundFile: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(FILE_NAME_MAX)],
    }),
    bannerAdvertising: new FormControl<BannerAdvertisingMode>(BannerAdvertisingMode.None, {
      nonNullable: true,
    }),
    userRegistration: new FormControl<UserRegistrationMode>(UserRegistrationMode.NoRegistration, {
      nonNullable: true,
    }),
    splashTabId: new FormControl<number | null>(null, { nonNullable: true }),
    homeTabId: new FormControl<number | null>(null, { nonNullable: true }),
    loginTabId: new FormControl<number | null>(null, { nonNullable: true }),
    userTabId: new FormControl<number | null>(null, { nonNullable: true }),
    homeDirectory: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(HOME_DIRECTORY_MAX)],
    }),
    currency: new FormControl('', {
      nonNullable: true,
      validators: [exactLengthWhenPresent(CURRENCY_CODE_LENGTH, CURRENCY_LENGTH_MESSAGE)],
    }),
    paymentProcessor: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PROCESSOR_FIELD_MAX)],
    }),
    processorUserId: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PROCESSOR_FIELD_MAX)],
    }),
    processorPassword: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PROCESSOR_FIELD_MAX)],
    }),
    administratorId: new FormControl<number | null>(null, { nonNullable: true }),
    defaultLanguage: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(DEFAULT_LANGUAGE_MAX)],
    }),
    timeZoneOffset: new FormControl<number | null>(null, { nonNullable: true }),
    expiryDate: new FormControl('', { nonNullable: true }),
    hostFee: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
    hostSpace: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
    pageQuota: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
    userQuota: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
    siteLogHistory: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
  });

  /**
   * Banner advertising choices — legacy `optBanners`
   * (`sitesettings.ascx:L120-L122`), wording from `None.Text`, `Site.Text` and
   * `Host.Text`.
   */
  protected readonly bannerChoices: readonly RadioChoice<BannerAdvertisingMode>[] = [
    { value: BannerAdvertisingMode.None, label: 'None' },
    { value: BannerAdvertisingMode.Site, label: 'Site' },
    { value: BannerAdvertisingMode.Host, label: 'Host' },
  ];

  /**
   * Registration choices — legacy `optUserRegistration`
   * (`sitesettings.ascx:L222-L226`), wording from `None.Text`, `Private.Text`,
   * `Public.Text` and `Verified.Text`.
   */
  protected readonly registrationChoices: readonly RadioChoice<UserRegistrationMode>[] = [
    { value: UserRegistrationMode.NoRegistration, label: 'None' },
    { value: UserRegistrationMode.PrivateRegistration, label: 'Private' },
    { value: UserRegistrationMode.PublicRegistration, label: 'Public' },
    { value: UserRegistrationMode.VerifiedRegistration, label: 'Verified' },
  ];

  /**
   * Field-level `maxlength` values the template puts on the text controls.
   *
   * MIGRATION: the legacy numeric fields carried `MaxLength` too — 10 on the
   * hosting fee and 6 on each of the three quotas — but those are absent here and
   * their absence is deliberate. `maxlength` has no effect on
   * `<input type="number">`, and the digit caps were an artefact of the fields
   * having been text boxes rather than a stored constraint: the schema holds
   * integers and money, and the server rule is only "zero or greater". Inventing
   * a numeric ceiling to imitate a character count would refuse a value the API
   * accepts.
   */
  protected readonly limits = {
    portalName: PORTAL_NAME_MAX,
    metadata: METADATA_MAX,
    footerText: FOOTER_TEXT_MAX,
    fileName: FILE_NAME_MAX,
    processorField: PROCESSOR_FIELD_MAX,
    defaultLanguage: DEFAULT_LANGUAGE_MAX,
    homeDirectory: HOME_DIRECTORY_MAX,
    currency: CURRENCY_CODE_LENGTH,
  } as const;

  /** The two tabs, in legacy source order. */
  protected readonly tabs: readonly { key: PortalSettingsTab; label: string }[] = [
    { key: 'basic', label: 'Basic Settings' },
    { key: 'advanced', label: 'Advanced Settings' },
  ];

  /** The empty choice every select offers. */
  protected readonly noneSpecified = NONE_SPECIFIED;

  /** Per-field help text, bound rather than written as template text. */
  protected readonly hints = FIELD_HINTS;

  /** The host-lock advisory text, exposed so the template holds no wording. */
  protected readonly bannerLockNotice = BANNER_HOST_LOCK_NOTICE;

  /** The delete confirmation text. */
  protected readonly deleteConfirmMessage = DELETE_CONFIRM_MESSAGE;

  /**
   * DOM id of the read-only identifier field.
   *
   * Named separately from `controlId` because the identifier is not a form
   * control — it is displayed, never submitted — so it has no key on the form
   * model and cannot be addressed through the keyed helper.
   */
  protected readonly guidControlId = 'portal-settings-guid';

  /** DOM id of the identifier field's help text. */
  protected readonly guidHintId = 'portal-settings-guid-hint';

  /** Which of the two groups is showing. Basic first, as in the legacy source. */
  protected selectedTab: PortalSettingsTab = 'basic';

  /** True while the delete confirmation is on screen. */
  protected removalPending = false;

  /**
   * Sections currently collapsed.
   *
   * Seeded from the legacy `IsExpanded` attributes so the screen opens looking as
   * it did: Site Details, Site Marketing (`IsExpanded="True"`), Security Settings
   * and Page Management are open; Appearance, Payment, Other and Host are closed
   * (`IsExpanded="False"` at L132, L295, L386 and L421 respectively).
   */
  private readonly collapsed = new Set<PortalSettingsSection>([
    'appearance',
    'payment',
    'other',
    'host',
  ]);

  /** The settings last supplied, or `undefined` before the first load. */
  private held: PortalSettings | undefined;

  /** Backing field for {@link canEditHostFields}. */
  private hostFieldsEditable = false;

  /** The lookup lists last supplied. */
  private suppliedLookups: PortalSettingsLookups = {
    pages: [],
    administrators: [],
    currencies: [],
    paymentProcessors: [],
    languages: [],
    timeZones: [],
  };

  /** Page options for the four page selectors, including any held value. */
  protected pageOptions: readonly SelectOption<number>[] = [];

  /** Administrator options, including any held value. */
  protected administratorOptions: readonly SelectOption<number>[] = [];

  /** Currency options, including any held value. */
  protected currencyOptions: readonly SelectOption<string>[] = [];

  /** Payment-processor options, including any held value. */
  protected processorOptions: readonly SelectOption<string>[] = [];

  /** Language options, including any held value. */
  protected languageOptions: readonly SelectOption<string>[] = [];

  /** Time-zone options, including any held value. */
  protected timeZoneOptions: readonly SelectOption<number>[] = [];

  /**
   * The site's stored settings.
   *
   * Assigning rebuilds every control from the supplied values, which is the
   * correct behaviour for a whole-row form: a fresh load discards a half-finished
   * edit rather than merging into it, because merging would produce a request
   * carrying a mixture of two revisions.
   */
  @Input()
  public set settings(value: PortalSettings | undefined) {
    this.held = value;
    this.applySettings();
  }

  public get settings(): PortalSettings | undefined {
    return this.held;
  }

  /** The lookup lists backing the six select controls. */
  @Input()
  public set lookups(value: PortalSettingsLookups) {
    this.suppliedLookups = value;
    this.rebuildOptions();
  }

  public get lookups(): PortalSettingsLookups {
    return this.suppliedLookups;
  }

  /** Screen title. Defaults to the legacy module title. */
  @Input() public heading = 'Site Settings';

  /** True while the settings are being fetched. */
  @Input({ transform: booleanAttribute }) public loading = false;

  /** True while a submission is in flight. Disables both action buttons. */
  @Input({ transform: booleanAttribute }) public saving = false;

  /**
   * Whether the operator may administer the host-level group.
   *
   * Reproduces `SiteSettings.ascx.vb:L498-L514`, which sets `dshHost.Visible` and
   * `tblHost.Visible` to `True` only inside the `If UserInfo.IsSuperUser` branch
   * and to `False` otherwise. Defaults to `false`, so the privileged group is
   * hidden unless a caller states otherwise — the safe direction for a flag that
   * gates a hosting charge and four quotas.
   *
   * A setter rather than a plain field because the banner lock is derived partly
   * from it, and the derivation reaches into a form control's enabled state, which
   * has to be re-applied when either input changes.
   *
   * Not a security boundary. The server refuses a host-only change from an
   * unprivileged caller regardless of what the browser sends; this input decides
   * only what is offered.
   */
  @Input({ transform: booleanAttribute })
  public set canEditHostFields(value: boolean) {
    this.hostFieldsEditable = value;
    this.applyBannerLock();
  }

  public get canEditHostFields(): boolean {
    return this.hostFieldsEditable;
  }

  /**
   * Whether the Delete affordance is offered.
   *
   * Reproduces `SiteSettings.ascx.vb:L503`, `cmdDelete.Visible = (intPortalId <>
   * PortalId)` — the button appeared only when the site being edited was not the
   * one serving the request, so an operator could not delete the site they were
   * standing on. A component cannot know which site is serving the request, so the
   * caller states it. Defaults to `false`, the conservative direction.
   */
  @Input({ transform: booleanAttribute }) public canDelete = false;

  /** Emits the composed whole-row request when the form is submitted. */
  @Output() public readonly save = new EventEmitter<UpdatePortalRequest>();

  /** Emits when the operator abandons the edit. Legacy `cmdCancel`. */
  @Output() public readonly cancel = new EventEmitter<void>();

  /** Emits when a delete is confirmed. Legacy `cmdDelete`, after its confirm. */
  @Output() public readonly remove = new EventEmitter<void>();

  /** True once settings have been supplied. */
  protected get hasSettings(): boolean {
    return this.held !== undefined;
  }

  /**
   * Whether banner advertising is held at host level and therefore locked.
   *
   * Derived rather than accepted as an input, which removes a way for a caller to
   * state something the data contradicts. Reproduces
   * `SiteSettings.ascx.vb:L292-L296` exactly: a host operator sees no advisory and
   * an enabled control (`lblBanners.Visible = False`), while any other operator
   * gets `optBanners.Enabled = objPortal.BannerAdvertising <> 2` and
   * `lblBanners.Visible = objPortal.BannerAdvertising = 2` — that is, the group is
   * locked and the advisory shown precisely when the stored value is `Host`.
   */
  protected get bannerLockedByHost(): boolean {
    if (this.canEditHostFields) {
      return false;
    }

    return this.held?.bannerAdvertising === BannerAdvertisingMode.Host;
  }

  /** The site's immutable identifier, for the read-only Site Details row. */
  protected get portalGuid(): string {
    return this.held?.guid ?? '';
  }

  /** Whether the given section is currently collapsed. */
  protected isCollapsed(section: PortalSettingsSection): boolean {
    return this.collapsed.has(section);
  }

  /** Whether the given tab is the one on screen. */
  protected isSelected(tab: PortalSettingsTab): boolean {
    return this.selectedTab === tab;
  }

  /** Stable DOM id for a tab button, so the panel can be labelled by it. */
  protected tabId(tab: PortalSettingsTab): string {
    return `portal-settings-tab-${tab}`;
  }

  /**
   * Stable DOM id of the single panel region.
   *
   * There is one panel element and the tabs change its contents, so every tab's
   * `aria-controls` points here and none of them can dangle. Rendering a second,
   * removed panel would be the alternative, and its `aria-controls` reference
   * would point at an element that is not in the document — the collapse has to be
   * a removal, because the paired stylesheet declares `display: grid` on the panel
   * and the user agent's `[hidden]` rule loses to any author rule.
   */
  protected readonly panelId = 'portal-settings-panel';

  /** Stable DOM id for a field's control, for label association. */
  protected controlId(field: keyof PortalSettingsFormModel): string {
    return `portal-settings-${field}`;
  }

  /** Stable DOM id for a field's help text. */
  protected hintId(field: keyof PortalSettingsFormModel): string {
    return `${this.controlId(field)}-hint`;
  }

  /**
   * Stable DOM id for a field's visible name.
   *
   * Used only by the two radio groups, whose accessible name comes from
   * `aria-labelledby` on the group rather than from a `for` association — a
   * `role="radiogroup"` is not a labelable element, so `for` cannot reach it.
   */
  protected labelId(field: keyof PortalSettingsFormModel): string {
    return `${this.controlId(field)}-label`;
  }

  /** Stable DOM id for a field's validation message. */
  protected messageId(field: keyof PortalSettingsFormModel): string {
    return `${this.controlId(field)}-message`;
  }

  /**
   * The ids a control should point `aria-describedby` at.
   *
   * The hint is always present; the message id joins it only while a message is
   * showing, so the attribute never references a removed element.
   */
  protected describedBy(field: keyof PortalSettingsFormModel): string {
    const ids = [this.hintId(field)];
    if (this.messageFor(field) !== null) {
      ids.push(this.messageId(field));
    }

    return ids.join(' ');
  }

  /** Selects a tab. */
  protected selectTab(tab: PortalSettingsTab): void {
    this.selectedTab = tab;
  }

  /**
   * Moves selection with the keyboard, per the ARIA tabs pattern.
   *
   * Left and right arrows step through the strip and wrap; Home and End jump to
   * its ends. Focus follows selection, which is the automatic-activation form of
   * the pattern and the right one here because switching a panel costs nothing —
   * no request is issued and no state is discarded.
   */
  protected onTabKeydown(event: KeyboardEvent): void {
    const order: readonly PortalSettingsTab[] = ['basic', 'advanced'];
    const current = order.indexOf(this.selectedTab);
    let next = current;

    switch (event.key) {
      case 'ArrowRight':
        next = (current + 1) % order.length;
        break;
      case 'ArrowLeft':
        next = (current - 1 + order.length) % order.length;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = order.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    const target = order[next];
    this.selectedTab = target;
    this.focusTab(target);
  }

  /** Expands or collapses a section. */
  protected toggleSection(section: PortalSettingsSection): void {
    if (this.collapsed.has(section)) {
      this.collapsed.delete(section);
      return;
    }

    this.collapsed.add(section);
  }

  /**
   * The validation message for a field, or `null` when there is nothing to say.
   *
   * Nothing is reported until the control has been touched or edited, so a form
   * opened and not yet used shows no errors. The order of the checks matters: the
   * bespoke messages are the ones copied from the server validator, so they are
   * returned before the generic length and range sentences.
   */
  protected messageFor(field: keyof PortalSettingsFormModel): string | null {
    const control = this.form.controls[field];
    if (!control.invalid || !(control.dirty || control.touched)) {
      return null;
    }

    const errors = control.errors;
    if (errors === null) {
      return null;
    }

    const exact = errors['exactLength'];
    if (typeof exact === 'string') {
      return exact;
    }

    if (errors['min'] !== undefined) {
      return this.minimumMessage(field);
    }

    const maxLength = errors['maxlength'];
    if (maxLength !== null && typeof maxLength === 'object') {
      const requested = (maxLength as { requiredLength?: number }).requiredLength;
      if (typeof requested === 'number') {
        return `Enter at most ${requested} characters.`;
      }
    }

    return 'Correct this field and try again.';
  }

  /** Submits the whole row. */
  protected onSubmit(): void {
    if (this.saving || this.held === undefined || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.save.emit(this.toRequest(this.held.portalId));
  }

  /** Abandons the edit. Carries no validation, as the legacy button did not. */
  protected onCancel(): void {
    this.cancel.emit();
  }

  /** Opens the delete confirmation. */
  protected requestRemoval(): void {
    this.removalPending = true;
  }

  /** Confirms the delete. */
  protected onRemovalConfirmed(): void {
    this.removalPending = false;
    this.remove.emit();
  }

  /** Dismisses the delete confirmation without acting. */
  protected onRemovalCancelled(): void {
    this.removalPending = false;
  }

  /**
   * Composes the request from the form.
   *
   * Every field is present, because the endpoint replaces the row. Text fields are
   * trimmed and a blank one becomes `null`; numeric fields pass through as they
   * are, so a deliberate zero survives and is not mistaken for an absent value.
   */
  private toRequest(portalId: number): UpdatePortalRequest {
    const value = this.form.getRawValue();

    return {
      portalId,
      portalName: textOrNull(value.portalName),
      description: textOrNull(value.description),
      keyWords: textOrNull(value.keyWords),
      footerText: textOrNull(value.footerText),
      logoFile: textOrNull(value.logoFile),
      backgroundFile: textOrNull(value.backgroundFile),
      bannerAdvertising: value.bannerAdvertising,
      userRegistration: value.userRegistration,
      splashTabId: value.splashTabId,
      homeTabId: value.homeTabId,
      loginTabId: value.loginTabId,
      userTabId: value.userTabId,
      homeDirectory: textOrNull(value.homeDirectory),
      currency: textOrNull(value.currency),
      paymentProcessor: textOrNull(value.paymentProcessor),
      processorUserId: textOrNull(value.processorUserId),
      processorPassword: textOrNull(value.processorPassword),
      administratorId: value.administratorId,
      defaultLanguage: textOrNull(value.defaultLanguage),
      timeZoneOffset: value.timeZoneOffset,
      expiryDate: textOrNull(value.expiryDate),
      hostFee: value.hostFee,
      hostSpace: value.hostSpace,
      pageQuota: value.pageQuota,
      userQuota: value.userQuota,
      siteLogHistory: value.siteLogHistory,
    };
  }

  /** Writes the held settings into the form and refreshes the option lists. */
  private applySettings(): void {
    const source = this.held;
    if (source === undefined) {
      this.form.reset();
      this.rebuildOptions();
      return;
    }

    this.form.setValue({
      portalName: source.portalName ?? '',
      description: source.description ?? '',
      keyWords: source.keyWords ?? '',
      footerText: source.footerText ?? '',
      logoFile: source.logoFile ?? '',
      backgroundFile: source.backgroundFile ?? '',
      bannerAdvertising: source.bannerAdvertising,
      userRegistration: source.userRegistration,
      splashTabId: source.splashTabId,
      homeTabId: source.homeTabId,
      loginTabId: source.loginTabId,
      userTabId: source.userTabId,
      homeDirectory: source.homeDirectory ?? '',
      currency: source.currency ?? '',
      paymentProcessor: source.paymentProcessor ?? '',
      processorUserId: source.processorUserId ?? '',
      // Never pre-populated: the response type carries no counterpart, so there
      // is nothing to write, and a blank submission leaves the stored secret
      // alone.
      processorPassword: '',
      administratorId: source.administratorId,
      defaultLanguage: source.defaultLanguage ?? '',
      timeZoneOffset: source.timeZoneOffset,
      expiryDate: toDateInputValue(source.expiryDate),
      hostFee: source.hostFee,
      hostSpace: source.hostSpace,
      pageQuota: source.pageQuota,
      userQuota: source.userQuota,
      siteLogHistory: source.siteLogHistory,
    });
    this.form.markAsPristine();
    this.form.markAsUntouched();
    this.rebuildOptions();
    this.applyBannerLock();
  }

  /**
   * Enables or disables the banner control to match the derived lock.
   *
   * Uses the reactive-forms enabled state rather than a `disabled` attribute
   * binding, which is the documented way to disable a control a form directive
   * owns — binding the attribute instead fights the directive for control of the
   * property. The whole-row submission is unaffected because the request is
   * composed from `getRawValue()`, which includes disabled controls; a disabled
   * banner setting is therefore still written back unchanged rather than silently
   * cleared, which is exactly what the legacy screen did when it rendered the
   * group as read-only.
   */
  private applyBannerLock(): void {
    const control = this.form.controls.bannerAdvertising;
    if (this.bannerLockedByHost) {
      if (control.enabled) {
        control.disable({ emitEvent: false });
      }

      return;
    }

    if (control.disabled) {
      control.enable({ emitEvent: false });
    }
  }

  /**
   * Recomputes the six option lists.
   *
   * Called from both setters, because either one changing can change the answer:
   * a new list may now offer a held value, and a new held value may be absent from
   * the existing list. Computing once per change rather than once per
   * change-detection pass also stops the template handing `@for` a freshly
   * allocated array on every pass.
   */
  private rebuildOptions(): void {
    const lookups = this.suppliedLookups;
    const settings = this.held;

    // The four page selectors share one list, so each held page identifier is
    // folded in turn. A page still referenced by a setting stays selectable even
    // after it has left the site's page list.
    let pages = lookups.pages;
    for (const held of [
      settings?.splashTabId ?? null,
      settings?.homeTabId ?? null,
      settings?.loginTabId ?? null,
      settings?.userTabId ?? null,
    ]) {
      pages = withHeldValue(pages, held);
    }

    this.pageOptions = pages;
    this.administratorOptions = withHeldValue(
      lookups.administrators,
      settings?.administratorId ?? null,
    );
    this.currencyOptions = withHeldValue(lookups.currencies, settings?.currency ?? null);
    this.processorOptions = withHeldValue(
      lookups.paymentProcessors,
      settings?.paymentProcessor ?? null,
    );
    this.languageOptions = withHeldValue(lookups.languages, settings?.defaultLanguage ?? null);
    this.timeZoneOptions = withHeldValue(lookups.timeZones, settings?.timeZoneOffset ?? null);
  }

  /** Moves focus onto a tab button, following keyboard selection. */
  private focusTab(tab: PortalSettingsTab): void {
    const button = this.hostElement.nativeElement.querySelector<HTMLButtonElement>(
      `#${this.tabId(tab)}`,
    );
    button?.focus();
  }

  /**
   * The range message for a numeric field.
   *
   * Each sentence is the constant the server validator declares, so the operator
   * reads the same words whichever side catches the mistake.
   */
  private minimumMessage(field: keyof PortalSettingsFormModel): string {
    switch (field) {
      case 'hostFee':
        return HOST_FEE_NEGATIVE_MESSAGE;
      case 'hostSpace':
        return HOST_SPACE_NEGATIVE_MESSAGE;
      case 'pageQuota':
        return PAGE_QUOTA_NEGATIVE_MESSAGE;
      case 'userQuota':
        return USER_QUOTA_NEGATIVE_MESSAGE;
      case 'siteLogHistory':
        return SITE_LOG_HISTORY_NEGATIVE_MESSAGE;
      default:
        return 'Enter zero or greater.';
    }
  }
}

// =============================================================================
//  DELIBERATE DIVERGENCES, RECORDED RATHER THAN ABSORBED
// =============================================================================
//
// MIGRATION: three legacy top-level groups become two tabs. Stylesheet Editor
// (sitesettings.ascx:L539-L556) edited the portal stylesheet, which leaves with
// skinning, so it is not carried forward.
//
// MIGRATION: two legacy sub-sections are dropped whole because not one of their
// controls has a counterpart on the update contract. Usability Settings held the
// inline-editor switch and three control-panel settings — the control panel is Web
// Forms chrome and the inline editor is the excluded rich-text provider. SSL
// Settings held four transport settings; transport security terminates at the
// reverse proxy in the target topology, so the site no longer administers it.
//
// MIGRATION: the Site Marketing section keeps only its Banners control. The search
// engine submission, site map and verification affordances (L75-L112) each posted
// to a third-party service through code paths that are out of scope, and none of
// them corresponds to a stored setting on the update contract.
//
// MIGRATION: the Appearance section keeps only Logo and Body Background. Its four
// skin and container selectors leave with skinning.
//
// MIGRATION: the Host Settings section keeps six of its seven rows. Premium
// Modules (`plDesktopModules`, L484) administered per-site module availability
// through a two-list picker that is a separate resource, not a portal column.
//
// MIGRATION: the legacy screen declares no `RequiredFieldValidator` anywhere — its
// only two validators are type checks on Expiry Date and Hosting Fee. No control
// here carries `Validators.required`, so the form refuses nothing the legacy screen
// accepted. The two type checks are enforced by the native `date` and `number`
// input types, which is why neither has a bespoke validator: a browser will not
// hand a non-date to a date input in the first place.
//
// MIGRATION: `<option [ngValue]>` is used for every select whose control holds a
// number, never `[value]`. `SelectControlValueAccessor` writes `[value]` back as
// text, so a page identifier bound that way would reach the payload as a string
// and stop matching the numeric contract. The radio groups use `[value]`, which is
// correct there: `RadioControlValueAccessor` writes the bound value through
// unchanged and so preserves the numeric code.
//
// MIGRATION: the legacy Delete confirmation was a client-side `confirm()` injected
// by `ClientAPI.AddButtonConfirm` (SiteSettings.ascx.vb:L252). It becomes the
// shared confirmation dialog, which is focus-trapped, dismissible with Escape and
// announced as a modal — a genuine accessibility repair carrying the same wording.
// =============================================================================
