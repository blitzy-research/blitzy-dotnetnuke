/** Wire contracts for the portal (tenant) resource, its settings projection and its host-name aliases. */

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeNumber,
  decodeString,
  nullable,
  objectOf,
  oneOfNumber,
  type Decoder,
} from '../utils/decode.util';

import type { PagedResult } from './paged-result.model';
import type { SelectOption } from './select-option.model';

/** How a portal admits new accounts. */
export enum UserRegistrationMode {
  /** No self-registration is offered. */
  NoRegistration = 0,
  /** An account is requested, then authorised by an administrator. Displayed as "Private". */
  PrivateRegistration = 1,
  /** An account becomes usable immediately. */
  PublicRegistration = 2,
  /** An account becomes usable once a mailed verification code is presented. Displayed as "Verified". */
  VerifiedRegistration = 3,
}

/** Where banner advertising is administered for a portal. */
export enum BannerAdvertisingMode {
  /** No banner advertising is shown. */
  None = 0,
  /** Banners are administered by the portal's own operators. */
  Site = 1,
  /** Banners are administered by the hosting provider. */
  Host = 2,
}

// MIGRATION: TENANT RESOLUTION NO LONGER MATCHES ON A SUBSTRING. The legacy resolution procedure compared
// an incoming host name against stored aliases with leading and trailing wildcards
// 01.00.00.SqlDataProvider:L4569-L4600 selects min(PortalID) from Portals where PortalAlias like '%' +
// @PortalAlias + '%' - so an alias that was a substring of another portal's alias could resolve a request
// to the WRONG TENANT, and the min() collapsed any ambiguity silently.
export interface PortalAlias {
  /**
   * Surrogate key of this alias row. The column is `PortalAlias.PortalAliasID int IDENTITY(1,1) NOT
   * NULL`, so the value is assigned by the database on insert and is never null on a response.
   */
  readonly portalAliasId: number;

  /**
   * The portal that owns this alias. Always populated on a response, because an alias row always belongs
   * to a portal.
   */
  readonly portalId: number;

  /** The host name, optionally with a port. Nullable because the contract declares it nullable. */
  readonly httpAlias: string | null;

  /**
   * Whether this is the alias the request that fetched the row resolved the tenant through — the row the
   * caller is, at this moment, standing on. A screen must withhold BOTH the rename and the unbind
   * affordance for the row this reports `true` for.
   */
  readonly isCurrent: boolean;
}

export interface PortalAdministrator {
  /**
   * The account key, which is the value the selector submits as {@link
   * UpdatePortalSettingsRequest.administratorId}.
   */
  readonly userId: number;

  /** The account's login name. NOT part of the legacy list item, which carried the display name alone. */
  readonly username: string;

  /** The account's display name, which is the text the legacy selector showed. */
  readonly displayName: string;
}

/** One row of the portal list, as returned by `GET /api/v1/portals`. */
export interface PortalListItem {
  /**
   * The portal's identifier. See the identity-seed note in this file's header: both `-1` and `0` are real
   * portal identifiers, so this value must never be tested for truthiness or compared against zero to
   * decide whether a portal exists.
   */
  readonly portalId: number;

  /** The portal's title. */
  readonly portalName: string;

  /**
   * The host names through which the portal is reached, as bare strings. Mandatory and never null: a
   * portal with no alias is a real, reportable state and is expressed by an empty array, so no caller
   * need guard before iterating.
   */
  readonly aliases: readonly string[];

  /** Number of accounts registered against the portal. Derived, not stored. */
  readonly users: number;

  /** Number of pages in the portal. */
  readonly pages: number;

  /** Disk-space allowance in megabytes, where nought means no imposed limit. Non-nullable here. */
  readonly hostSpace: number;

  /**
   * Recurring hosting fee, nought when none is charged. Non-nullable here, and reported unformatted: the
   * legacy grid's two-decimal format string is a presentation concern, and a preformatted string would
   * impose one culture's conventions on every caller and be useless for arithmetic.
   */
  readonly hostFee: number;

  /**
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null` when the column itself is
   * null. Genuinely nullable, unlike the two members above — this column really is `NULL` in the terminal
   * state and was never tightened.
   */
  readonly expiryDate: string | null;
}

export type PortalListPage = PagedResult<PortalListItem>;

// THE PORTAL IDENTIFIER ADMITS BOTH -1 AND 0 AS REAL VALUES, which makes every conventional emptiness test
// unsound against it.
export interface PortalDetail {
  /** The portal's identifier. */
  readonly portalId: number;

  /** The portal's title. */
  readonly portalName: string | null;

  /** Free-text description offered to search engines. */
  readonly description: string | null;

  /** Comma-separated search keywords. */
  readonly keyWords: string | null;

  /** Copyright line rendered in the page footer. */
  readonly footerText: string | null;

  /** Portal-relative path of the logo image. */
  readonly logoFile: string | null;

  /** Portal-relative path of the page background image. */
  readonly backgroundFile: string | null;

  /**
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null`. The legacy absent-date
   * sentinel `0001-01-01` is not rewritten into `null`; see {@link PortalListItem.expiryDate}.
   */
  readonly expiryDate: string | null;

  /** How the portal admits new accounts. */
  readonly userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. */
  readonly bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code; the column is fixed-width `char(3)`. */
  readonly currency: string | null;

  /** The account holding the portal administrator role. */
  readonly administratorId: number | null;

  /** The portal administrator's e-mail address. Not a `Portals` column. */
  readonly email: string | null;

  /** Recurring hosting fee. */
  readonly hostFee: number | null;

  /** Disk-space allowance in megabytes; nought means unlimited. */
  readonly hostSpace: number | null;

  /** Ceiling on pages; nought means unlimited, `-1` in legacy data meant not set. */
  readonly pageQuota: number | null;

  /** Ceiling on accounts. */
  readonly userQuota: number | null;

  /** Number of accounts. */
  readonly users: number;

  /** Number of pages. */
  readonly pages: number;

  /** The role granting portal administration. */
  readonly administratorRoleId: number | null;

  /** Display name of the administrator role. */
  readonly administratorRoleName: string | null;

  /** The role every registered account receives. */
  readonly registeredRoleId: number | null;

  /** Display name of the registered-users role. */
  readonly registeredRoleName: string | null;

  /**
   * Immutable identifier assigned when the portal is provisioned. The column is `uniqueidentifier NOT
   * NULL` with a `newid()` default, so this is non-nullable.
   */
  readonly guid: string;

  /** Name of the configured payment processor. */
  readonly paymentProcessor: string | null;

  /** Account identifier held with the payment processor. */
  readonly processorUserId: string | null;

  /** Days of portal-activity history retained; see the quota note. */
  readonly siteLogHistory: number | null;

  /** The page hosting the portal's administration menu. */
  readonly adminTabId: number | null;

  /**
   * The host-level root page. Read-only, and **identical for every portal**: the read view computes it
   * with an *uncorrelated* sub-select that names no portal — it selects the single page belonging to no
   * portal and having no parent — so the same value is reported for every portal.
   */
  readonly superTabId: number | null;

  /** The page shown to a first-time visitor ahead of the home page. See the page-reference note. */
  readonly splashTabId: number | null;

  /** The portal's home page. */
  readonly homeTabId: number | null;

  /** The page carrying the sign-in form. */
  readonly loginTabId: number | null;

  /** The page carrying the account screens. */
  readonly userTabId: number | null;

  /** Culture code, at most six characters. */
  readonly defaultLanguage: string | null;

  /** Offset from co-ordinated universal time, in minutes. */
  readonly timeZoneOffset: number | null;

  /** Portal-relative root of the portal's file storage. */
  readonly homeDirectory: string | null;

  /**
   * The portal's host names, when they were loaded for this response. **Three states, and the first two
   * must not be collapsed.** `null` means the aliases were not requested or not loaded and says nothing
   * about how many exist — the nullability lets a service skip the alias query on paths that do not need
   * it without the payload misreporting the result.
   */
  readonly aliases: readonly PortalAlias[] | null;

  /**
   * Opaque marker for the revision of the portal this response describes. **A screen reads it and sends
   * it back unchanged.** It is the server's own value, derived from the twenty-five columns a portal
   * write replaces, and it must not be parsed, compared for ordering, displayed, or constructed by a
   * client.
   */
  readonly concurrencyToken: string;
}

/**
 * A portal's editable configuration, as returned by `GET /api/v1/portals/{portalId}/settings`. Mirrors
 * `Dtos/Portal/PortalSettingsDto.cs` and its twenty-seven members.
 */
export interface PortalSettings {
  /** The portal's identifier. */
  readonly portalId: number;

  /** The portal's title. */
  readonly portalName: string | null;

  /** Free-text description offered to search engines. */
  readonly description: string | null;

  /** Comma-separated search keywords. */
  readonly keyWords: string | null;

  /** Copyright line rendered in the page footer. */
  readonly footerText: string | null;

  /** Portal-relative path of the logo image. */
  readonly logoFile: string | null;

  /** Portal-relative path of the page background image. */
  readonly backgroundFile: string | null;

  /**
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null`. The legacy absent-date
   * sentinel `0001-01-01` is preserved, never rewritten into `null`.
   */
  readonly expiryDate: string | null;

  /** How the portal admits new accounts. */
  readonly userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. */
  readonly bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code. */
  readonly currency: string | null;

  /** The account holding the portal administrator role. */
  readonly administratorId: number | null;

  /** Recurring hosting fee. */
  readonly hostFee: number | null;

  /** Disk-space allowance in megabytes; nought means unlimited. */
  readonly hostSpace: number | null;

  /** Ceiling on pages. */
  readonly pageQuota: number | null;

  /** Ceiling on accounts; nought means unlimited. */
  readonly userQuota: number | null;

  /** Name of the configured payment processor. */
  readonly paymentProcessor: string | null;

  /** Account identifier held with the payment processor. */
  readonly processorUserId: string | null;

  /** Days of portal-activity history retained. */
  readonly siteLogHistory: number | null;

  /** The page shown ahead of the home page, if any. */
  readonly splashTabId: number | null;

  /** The portal's home page. */
  readonly homeTabId: number | null;

  /** The page carrying the sign-in form. */
  readonly loginTabId: number | null;

  /** The page carrying the account screens. */
  readonly userTabId: number | null;

  /** Culture code, at most six characters. */
  readonly defaultLanguage: string | null;

  /** Offset from co-ordinated universal time in minutes; nought is a real offset. */
  readonly timeZoneOffset: number | null;

  /** Portal-relative root of the portal's file storage. */
  readonly homeDirectory: string | null;

  /** Immutable identifier assigned at provisioning. */
  readonly guid: string;

  /**
   * Opaque marker for the revision of the portal this projection describes. Identical in meaning,
   * nullability and handling to {@link PortalDetail.concurrencyToken} — the same server-side derivation
   * produces both, so the settings screen and the edit screen cannot disagree about which revision they
   * are looking at, and a token read here is honoured by either portal write.
   */
  readonly concurrencyToken: string;
}

/**
 * Body of `POST /api/v1/portals`, which provisions a new portal. Mirrors
 * `Dtos/Portal/CreatePortalRequest.cs` and its twelve members.
 */
export interface CreatePortalRequest {
  /** The new portal's title. */
  readonly portalName: string | null;

  /**
   * The first host name through which the portal will be reached. A single alias, not a collection:
   * further aliases are added afterwards through the alias sub-resource.
   */
  readonly portalAlias: string | null;

  /** Free-text description offered to search engines. */
  readonly description: string | null;

  /** Comma-separated search keywords. */
  readonly keyWords: string | null;

  /** Portal-relative root for the new portal's file storage. */
  readonly homeDirectory: string | null;

  /** The portal template the new portal is built from. */
  readonly templateFile: string | null;

  /**
   * Whether the portal is reached through a path beneath an existing host name rather than through a host
   * name of its own.
   */
  readonly isChildPortal: boolean;

  /** Given name of the portal's first administrator. */
  readonly administratorFirstName: string | null;

  /** Family name of the portal's first administrator. */
  readonly administratorLastName: string | null;

  /** Sign-in name of the portal's first administrator. */
  readonly administratorUsername: string | null;

  /**
   * Initial password for the portal's first administrator. Write-only, like {@link
   * UpdatePortalRequest.processorCredentialReference}: it is accepted here and returned by nothing.
   */
  readonly administratorPassword: string | null;

  /** E-mail address of the portal's first administrator. */
  readonly administratorEmail: string | null;
}

/**
 * Body of `PUT /api/v1/portals/{portalId}`. Mirrors `Dtos/Portal/UpdatePortalRequest.cs` and its
 * twenty-seven members, in the order that contract declares them.
 */
// MIGRATION: TWELVE of the legacy entity's thirty-nine properties are deliberately absent, each for a
// measured reason: the two role identifiers and the two role names, the assigned provisioning identifier,
// the administrator's e-mail, the administration-menu page and the host root page, the two computed counts,
// the absolute filesystem path and the version string.
export interface UpdatePortalRequest {
  /** The portal being written. */
  readonly portalId: number;

  /** The portal's title, at most 128 characters. */
  readonly portalName: string | null;

  /** Portal-relative path of the logo image, at most 50 characters. */
  readonly logoFile: string | null;

  /** Copyright line, at most 100 characters. */
  readonly footerText: string | null;

  readonly expiryDate: string | null;

  /** How the portal admits new accounts. */
  readonly userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. */
  readonly bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code, or `null`. */
  readonly currency: string | null;

  /** The account to hold the portal administrator role. */
  readonly administratorId: number | null;

  /** Recurring hosting fee, denominated in {@link currency}. */
  readonly hostFee: number | null;

  /** Disk-space allowance in whole megabytes; nought means unlimited. */
  readonly hostSpace: number | null;

  /** Ceiling on pages. */
  readonly pageQuota: number | null;

  /** Ceiling on accounts; nought means unlimited. */
  readonly userQuota: number | null;

  /** Payment processor name, at most 50 characters. */
  readonly paymentProcessor: string | null;

  /** Payment processor account identifier, at most 50 characters. */
  readonly processorUserId: string | null;

  /**
   * Opaque managed-secret reference for the payment processor, at most 50 characters. **Write-only.**
   * `null` keeps the current reference, `''` clears it and a non-empty `secret://...` value replaces it.
   * The referenced credential never crosses this contract and no response shape echoes even the
   * reference.
   */
  readonly processorCredentialReference: string | null;

  /** Portal description, at most 500 characters. */
  readonly description: string | null;

  /** Search keywords, at most 500 characters. */
  readonly keyWords: string | null;

  /** Portal-relative path of the background image, at most 50 characters. */
  readonly backgroundFile: string | null;

  /** Days of activity history retained. */
  readonly siteLogHistory: number | null;

  /** The page shown ahead of the home page, if any. */
  readonly splashTabId: number | null;

  /** The portal's home page. */
  readonly homeTabId: number | null;

  /** The page carrying the sign-in form. */
  readonly loginTabId: number | null;

  /** The page carrying the account screens. */
  readonly userTabId: number | null;

  /** Culture code, at most six characters. */
  readonly defaultLanguage: string | null;

  /** Offset from co-ordinated universal time in minutes; nought is a real offset. */
  readonly timeZoneOffset: number | null;

  /** Portal-relative root of the portal's file storage, at most 100 characters. */
  readonly homeDirectory: string | null;

  /**
   * The {@link PortalDetail.concurrencyToken} of the revision this update was composed against, or
   * `null`. **Round-tripped verbatim from the read that populated the form.** The server compares it
   * against the portal as it now stands and answers `409 portal.concurrency_conflict` when the two
   * differ, so this member is the only thing standing between a whole-record replacement and the silent
   * destruction of another administrator's committed edit.
   */
  readonly concurrencyToken: string | null;
}

/**
 * Body of `PUT /api/v1/portals/{portalId}/settings`. Mirrors
 * `Dtos/Portal/UpdatePortalSettingsRequest.cs`: the same twenty-six editable values as {@link
 * UpdatePortalRequest} plus the shared {@link UpdatePortalRequest.concurrencyToken}, without the latter's
 * legacy body-level `portalId`.
 */
export type UpdatePortalSettingsRequest = Omit<UpdatePortalRequest, 'portalId'>;

/**
 * Body of `POST /api/v1/portals/{portalId}/aliases`. Mirrors `Dtos/Portal/CreatePortalAliasRequest.cs`,
 * which carries exactly one member.
 */
export interface CreatePortalAliasRequest {
  /**
   * The host name by which the portal is to be reached. A host name, an IP address or a server name,
   * optionally followed by a port and a child path, with no protocol prefix.
   */
  readonly httpAlias: string;
}

export interface UpdatePortalAliasRequest {
  readonly httpAlias: string;
}

/**
 * The lookup lists a settings screen needs in order to offer the choices the legacy screen offered. Each
 * member corresponds to one drop-down on `Website/admin/Portal/sitesettings.ascx`, and each defaults to
 * an empty array at the point of use.
 */
export interface PortalSettingsLookups {
  /** Pages within the portal, for the four page selectors. */
  readonly pages: readonly SelectOption<number>[];
  /** Accounts eligible to administer the portal. */
  readonly administrators: readonly SelectOption<number>[];
  /** Currency codes. */
  readonly currencies: readonly SelectOption<string>[];
  /** Installed payment processors. */
  readonly paymentProcessors: readonly SelectOption<string>[];
  /** Installed cultures. */
  readonly languages: readonly SelectOption<string>[];
  /** Time zones, valued in minutes from co-ordinated universal time. */
  readonly timeZones: readonly SelectOption<number>[];
}

/** Decodes one host-name alias as the API publishes it for reading. */
export const decodePortalAlias: Decoder<PortalAlias> = objectOf<PortalAlias>({
  portalAliasId: decodeInteger,
  portalId: decodeInteger,
  httpAlias: nullable(decodeString),
  // A COMPUTED projection, not a stored column: the server compares each alias against the one the request
  // arrived on.
  isCurrent: decodeBoolean,
});

/**
 * Decodes one account the settings screen may designate as the portal's administrator. Every member is
 * required and non-nullable, because the server projects all three from a materialised account row and
 * refuses to publish an entry whose account did not materialise.
 */
export const decodePortalAdministrator: Decoder<PortalAdministrator> =
  objectOf<PortalAdministrator>({
    userId: decodeInteger,
    username: decodeString,
    displayName: decodeString,
  });

export const decodePortalListItem: Decoder<PortalListItem> = objectOf<PortalListItem>({
  portalId: decodeInteger,
  portalName: decodeString,
  aliases: arrayOf(decodeString),
  users: decodeInteger,
  pages: decodeInteger,
  hostSpace: decodeInteger,
  hostFee: decodeNumber,
  expiryDate: nullable(decodeDateString),
});

/** Decodes one portal in full. Three details are deliberate. */
export const decodePortalDetail: Decoder<PortalDetail> = objectOf<PortalDetail>({
  portalId: decodeInteger,
  portalName: nullable(decodeString),
  description: nullable(decodeString),
  keyWords: nullable(decodeString),
  footerText: nullable(decodeString),
  logoFile: nullable(decodeString),
  backgroundFile: nullable(decodeString),
  expiryDate: nullable(decodeDateString),
  userRegistration: oneOfNumber([
    UserRegistrationMode.NoRegistration,
    UserRegistrationMode.PrivateRegistration,
    UserRegistrationMode.PublicRegistration,
    UserRegistrationMode.VerifiedRegistration,
  ]),
  bannerAdvertising: oneOfNumber([
    BannerAdvertisingMode.None,
    BannerAdvertisingMode.Site,
    BannerAdvertisingMode.Host,
  ]),
  currency: nullable(decodeString),
  administratorId: nullable(decodeInteger),
  email: nullable(decodeString),
  hostFee: nullable(decodeNumber),
  hostSpace: nullable(decodeInteger),
  pageQuota: nullable(decodeInteger),
  userQuota: nullable(decodeInteger),
  users: decodeInteger,
  pages: decodeInteger,
  administratorRoleId: nullable(decodeInteger),
  administratorRoleName: nullable(decodeString),
  registeredRoleId: nullable(decodeInteger),
  registeredRoleName: nullable(decodeString),
  guid: decodeString,
  paymentProcessor: nullable(decodeString),
  processorUserId: nullable(decodeString),
  siteLogHistory: nullable(decodeInteger),
  adminTabId: nullable(decodeInteger),
  superTabId: nullable(decodeInteger),
  splashTabId: nullable(decodeInteger),
  homeTabId: nullable(decodeInteger),
  loginTabId: nullable(decodeInteger),
  userTabId: nullable(decodeInteger),
  defaultLanguage: nullable(decodeString),
  timeZoneOffset: nullable(decodeInteger),
  homeDirectory: nullable(decodeString),
  aliases: nullable(arrayOf(decodePortalAlias)),
  // ⚠ REQUIRED, NOT NULLABLE: the server sends the token on every read, and `nullable(...)` here would
  // admit a response that cannot support a guarded write.
  concurrencyToken: decodeString,
});

/** Decodes the settings projection. */
export const decodePortalSettings: Decoder<PortalSettings> = objectOf<PortalSettings>({
  portalId: decodeInteger,
  portalName: nullable(decodeString),
  description: nullable(decodeString),
  keyWords: nullable(decodeString),
  footerText: nullable(decodeString),
  logoFile: nullable(decodeString),
  backgroundFile: nullable(decodeString),
  expiryDate: nullable(decodeDateString),
  userRegistration: oneOfNumber([
    UserRegistrationMode.NoRegistration,
    UserRegistrationMode.PrivateRegistration,
    UserRegistrationMode.PublicRegistration,
    UserRegistrationMode.VerifiedRegistration,
  ]),
  bannerAdvertising: oneOfNumber([
    BannerAdvertisingMode.None,
    BannerAdvertisingMode.Site,
    BannerAdvertisingMode.Host,
  ]),
  currency: nullable(decodeString),
  administratorId: nullable(decodeInteger),
  hostFee: nullable(decodeNumber),
  hostSpace: nullable(decodeInteger),
  pageQuota: nullable(decodeInteger),
  userQuota: nullable(decodeInteger),
  paymentProcessor: nullable(decodeString),
  processorUserId: nullable(decodeString),
  siteLogHistory: nullable(decodeInteger),
  splashTabId: nullable(decodeInteger),
  homeTabId: nullable(decodeInteger),
  loginTabId: nullable(decodeInteger),
  userTabId: nullable(decodeInteger),
  defaultLanguage: nullable(decodeString),
  timeZoneOffset: nullable(decodeInteger),
  homeDirectory: nullable(decodeString),
  guid: decodeString,
  // Required for the same reason as its detail counterpart, and from the same server derivation.
  concurrencyToken: decodeString,
});
