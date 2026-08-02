import type { SelectOption } from './select-option.model';

/**
 * Wire contract for the portal (tenant) resource.
 *
 * Mirrors `DnnMigration.Application.Dtos.Portal.PortalSettingsDto` and
 * `UpdatePortalRequest` property for property. Member names are camel-cased
 * because the API serialises with the framework's web defaults, which apply the
 * camel-case naming policy; the C# properties are `PortalName`, `LogoFile` and so
 * on, and they arrive here as `portalName`, `logoFile`.
 *
 * Every property is `readonly` on the response type and mutable on the request
 * type. That asymmetry is deliberate: a response is a snapshot of server state
 * that a component must not edit in place, whereas a request is a value the
 * component composes.
 *
 * This file declares types and one frozen code table. It contains no class, no
 * injectable and no function, so it has no paired spec — the compiler is the whole
 * of its verification. The code table is asserted indirectly by the screens that
 * consume it, which prove that a chosen option reaches the payload as a number.
 */

/**
 * How the site admits new accounts.
 *
 * The four codes are the `Value` attributes of the legacy `optUserRegistration`
 * radio group (`Website/admin/Portal/sitesettings.ascx:L222-L226`) and they match
 * `DnnMigration.Domain.Enums.UserRegistrationMode` exactly:
 * `NoRegistration = 0`, `PrivateRegistration = 1`, `PublicRegistration = 2`,
 * `VerifiedRegistration = 3`.
 *
 * Declared as a frozen object rather than a TypeScript `enum` because
 * `isolatedModules` is enabled and a `const enum` is not emittable under it,
 * while a plain `enum` emits a runtime object that cannot be tree-shaken. The
 * `as const` form gives the same exhaustive literal union with no runtime cost
 * beyond the table itself.
 */
export const USER_REGISTRATION_MODE = {
  /** No self-registration. The legacy list item labelled "None". */
  none: 0,
  /** Registration requires administrator approval. Legacy "Private". */
  private: 1,
  /** Registration is immediate. Legacy "Public". */
  public: 2,
  /** Registration requires e-mail verification. Legacy "Verified". */
  verified: 3,
} as const;

/** The four admitted registration codes, as a literal union. */
export type UserRegistrationMode =
  (typeof USER_REGISTRATION_MODE)[keyof typeof USER_REGISTRATION_MODE];

/**
 * Where banner advertising is administered for the site.
 *
 * The three codes are the `Value` attributes of the legacy `optBanners` radio
 * group (`Website/admin/Portal/sitesettings.ascx:L120-L122`) and they match
 * `DnnMigration.Domain.Enums.BannerAdvertisingMode`: `None = 0`, `Site = 1`,
 * `Host = 2`.
 *
 * The `host` code is load-bearing beyond its own value. The legacy screen read it
 * as a lock: `SiteSettings.ascx.vb:L295-L296` disables the radio group and shows
 * an advisory precisely when a non-host operator opens a site whose stored value
 * is `Host`. The settings screen reproduces that, deriving the lock from the
 * stored value rather than accepting it as a separate input that could contradict
 * the data.
 */
export const BANNER_ADVERTISING_MODE = {
  /** No banner advertising. */
  none: 0,
  /** Banners administered at site level. */
  site: 1,
  /** Banners administered by the hosting provider. */
  host: 2,
} as const;

/** The three admitted advertising codes, as a literal union. */
export type BannerAdvertisingMode =
  (typeof BANNER_ADVERTISING_MODE)[keyof typeof BANNER_ADVERTISING_MODE];

/**
 * A site's stored configuration, as returned by
 * `GET /api/v1/portals/{portalId}/settings`.
 *
 * Mirrors `PortalSettingsDto`. Two differences from the request type are worth
 * stating because neither is an oversight:
 *
 * - `guid` is present here and absent from the request. It is assigned when the
 *   site is provisioned and is never editable; the legacy screen rendered it as a
 *   read-only `asp:Label` (`sitesettings.ascx:L68-L69`).
 * - `processorPassword` is absent here and present on the request. The response
 *   deliberately does not carry it, so a stored payment-processor password is
 *   never returned to a browser. The field is therefore write-only across the
 *   boundary, and the settings form starts it blank on every load.
 */
export interface PortalSettings {
  /** The site's identifier. Zero and −1 are both real identifiers here. */
  readonly portalId: number;
  /** Site title. The backing column is `nvarchar(128)` and is not nullable. */
  readonly portalName: string | null;
  /** Free-text description used by search engines. `nvarchar(500)`. */
  readonly description: string | null;
  /** Comma-separated search keywords. `nvarchar(500)`. */
  readonly keyWords: string | null;
  /** Copyright line rendered by the skin. `nvarchar(100)`. */
  readonly footerText: string | null;
  /** Relative path of the site logo image. `nvarchar(50)`. */
  readonly logoFile: string | null;
  /** Relative path of the page background image. `nvarchar(50)`. */
  readonly backgroundFile: string | null;
  /**
   * When the hosting contract lapses, as an ISO 8601 instant, or `null` when the
   * site does not expire.
   */
  readonly expiryDate: string | null;
  /** How the site admits new accounts. */
  readonly userRegistration: UserRegistrationMode;
  /** Where banner advertising is administered. */
  readonly bannerAdvertising: BannerAdvertisingMode;
  /** Three-letter currency code. The column is `char(3)`, fixed width. */
  readonly currency: string | null;
  /** Identifier of the account holding the site administrator role. */
  readonly administratorId: number | null;
  /** Monthly hosting charge. Host-administered. */
  readonly hostFee: number | null;
  /** Disk allowance in megabytes; zero means unlimited. Host-administered. */
  readonly hostSpace: number | null;
  /** Maximum number of pages, or `null` for no ceiling. Host-administered. */
  readonly pageQuota: number | null;
  /** Maximum number of accounts, or `null` for no ceiling. Host-administered. */
  readonly userQuota: number | null;
  /** Name of the configured payment processor. `nvarchar(50)`. */
  readonly paymentProcessor: string | null;
  /** Account identifier held with the payment processor. `nvarchar(50)`. */
  readonly processorUserId: string | null;
  /** Days of site-activity history retained. Host-administered. */
  readonly siteLogHistory: number | null;
  /** Page shown before the site's home page, or `null` for none. */
  readonly splashTabId: number | null;
  /** The site's home page. */
  readonly homeTabId: number | null;
  /** The page carrying the sign-in form. */
  readonly loginTabId: number | null;
  /** The page carrying the account screens. */
  readonly userTabId: number | null;
  /** Culture code, at most six characters. Not nullable in the schema. */
  readonly defaultLanguage: string | null;
  /** Offset from UTC in minutes. Not nullable in the schema. */
  readonly timeZoneOffset: number | null;
  /** Root of the site's file storage. `varchar(100)`, not nullable. */
  readonly homeDirectory: string | null;
  /** Immutable identifier assigned at provisioning. Never editable. */
  readonly guid: string;
}

/**
 * Body of `PUT /api/v1/portals/{portalId}`.
 *
 * Mirrors `UpdatePortalRequest`. This is a WHOLE-ROW REPLACEMENT, which has a
 * consequence the settings form must respect: omitting a numeric term does not
 * mean "leave it alone". The server-side mapper substitutes zero for an omitted
 * numeric because the backing columns cannot hold null, so a request that leaves
 * the hosting charge out is a request to waive it. The form therefore always
 * sends every field it holds, and the host-administered group is populated from
 * the loaded settings even when the operator is not permitted to edit it.
 */
export interface UpdatePortalRequest {
  /** The site being written. Must match the route. */
  portalId: number;
  /** Site title, at most 128 characters. */
  portalName: string | null;
  /** Relative path of the site logo image, at most 50 characters. */
  logoFile: string | null;
  /** Copyright line, at most 100 characters. */
  footerText: string | null;
  /** Contract expiry as an ISO 8601 date, or `null` for no expiry. */
  expiryDate: string | null;
  /** How the site admits new accounts. */
  userRegistration: UserRegistrationMode;
  /** Where banner advertising is administered. */
  bannerAdvertising: BannerAdvertisingMode;
  /** Three-letter currency code, or `null`. */
  currency: string | null;
  /** Identifier of the site administrator account. */
  administratorId: number | null;
  /** Monthly hosting charge; must be zero or greater. */
  hostFee: number | null;
  /** Disk allowance in megabytes; must be zero or greater. */
  hostSpace: number | null;
  /** Page ceiling; must be zero or greater. */
  pageQuota: number | null;
  /** Account ceiling; must be zero or greater. */
  userQuota: number | null;
  /** Payment processor name, at most 50 characters. */
  paymentProcessor: string | null;
  /** Payment processor account identifier, at most 50 characters. */
  processorUserId: string | null;
  /**
   * Payment processor password, at most 50 characters.
   *
   * Write-only: the response type carries no counterpart, so this is the one
   * field the form cannot pre-populate. A blank submission is sent as `null`,
   * which the server reads as "leave the stored secret alone".
   */
  processorPassword: string | null;
  /** Site description, at most 500 characters. */
  description: string | null;
  /** Search keywords, at most 500 characters. */
  keyWords: string | null;
  /** Relative path of the background image, at most 50 characters. */
  backgroundFile: string | null;
  /** Days of activity history retained; must be zero or greater. */
  siteLogHistory: number | null;
  /** Splash page identifier, or `null` for none. */
  splashTabId: number | null;
  /** Home page identifier. */
  homeTabId: number | null;
  /** Sign-in page identifier. */
  loginTabId: number | null;
  /** Account-screens page identifier. */
  userTabId: number | null;
  /** Culture code, at most six characters. */
  defaultLanguage: string | null;
  /** Offset from UTC in minutes. */
  timeZoneOffset: number | null;
  /** File-storage root, at most 100 characters. */
  homeDirectory: string | null;
}

/**
 * The lookup lists the settings screen needs in order to offer the same choices
 * the legacy screen offered.
 *
 * Every member corresponds to one `asp:DropDownList` on
 * `Website/admin/Portal/sitesettings.ascx`, and every member defaults to an empty
 * array at the point of use. An empty list is a legitimate state, not an error:
 * the screen still renders the control, still shows the currently-held value, and
 * still round-trips it. That is a deliberate improvement over the legacy
 * behaviour, in which a bound list that did not contain the stored value silently
 * reset the field on the next post.
 */
export interface PortalSettingsLookups {
  /** Pages within the site, for the four page selectors. `cboSplashTabId` etc. */
  readonly pages: readonly SelectOption<number>[];
  /** Accounts eligible to administer the site. `cboAdministratorId`. */
  readonly administrators: readonly SelectOption<number>[];
  /** Currency codes. `cboCurrency`. */
  readonly currencies: readonly SelectOption<string>[];
  /** Installed payment processors. `cboProcessor`. */
  readonly paymentProcessors: readonly SelectOption<string>[];
  /** Installed cultures. `cboDefaultLanguage`. */
  readonly languages: readonly SelectOption<string>[];
  /** Time zones, valued in minutes from UTC. `cboTimeZone`. */
  readonly timeZones: readonly SelectOption<number>[];
}
