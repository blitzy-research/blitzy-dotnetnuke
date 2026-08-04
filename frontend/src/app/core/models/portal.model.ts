/**
 * Wire contracts for the portal (tenant) resource, its settings projection and its
 * host-name aliases.
 *
 * Every type below mirrors one contract published by the API, property for
 * property and in the order that contract declares them:
 *
 * | This file              | API contract                              |
 * | --------------------- | ----------------------------------------- |
 * | `PortalListItem`      | `Dtos/Portal/PortalListItemDto.cs`        |
 * | `PortalDetail`        | `Dtos/Portal/PortalDetailDto.cs`          |
 * | `PortalSettings`      | `Dtos/Portal/PortalSettingsDto.cs`        |
 * | `CreatePortalRequest` | `Dtos/Portal/CreatePortalRequest.cs`      |
 * | `UpdatePortalRequest` | `Dtos/Portal/UpdatePortalRequest.cs`      |
 * | `PortalAlias`         | `Dtos/Portal/PortalAliasDto.cs`           |
 *
 * ## Member naming
 *
 * The API serialises with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
 * (`Api/Extensions/ServiceCollectionExtensions.cs:L314` and `L417`), so the C#
 * property `PortalName` arrives as `portalName`. The policy lower-cases the whole
 * *leading upper-case run*, which makes the identifier members worth stating
 * exactly: the contracts spell them with a single lower-case `d` — `PortalId`,
 * `PortalAliasId`, `AdministratorId`, `AdministratorRoleId`, `RegisteredRoleId`,
 * `AdminTabId`, `SuperTabId`, `SplashTabId`, `HomeTabId`, `LoginTabId`,
 * `UserTabId` — and therefore arrive as `portalId`, `portalAliasId` and so on. Had
 * a contract spelled one `PortalID`, the same policy would have produced
 * `portalID`, and reading the wrong one yields `undefined` at runtime with no
 * compile error to warn of it. The names below were taken from the contracts
 * themselves, never inferred from the legacy spelling, because the legacy entity
 * is internally inconsistent on exactly this point: `PortalInfo.vb:L85` spells
 * `PortalID` while `PortalInfo.vb:L141` spells `AdministratorId`.
 *
 * The route parameters follow the same convention — `portalId`, `moduleId`,
 * `userId`, `roleId` — and the server resolves the scope of an authorisation
 * check from route data, so a caller that names a route parameter `portalID`
 * does not merely read a stale value, it fails the authorisation check.
 *
 * ## Absent values
 *
 * Nothing here is optional in the `prop?:` sense, because nothing on the wire is
 * ever missing: the API sets `DefaultIgnoreCondition = JsonIgnoreCondition.Never`
 * (`Api/Extensions/ServiceCollectionExtensions.cs:L313` and `L408`). That choice
 * is load-bearing rather than stylistic. `WhenWritingDefault` would have dropped a
 * portal identifier of nought, a user quota of nought — which means *unlimited* —
 * and every `false`, all of which are meaningful values in this schema. A member
 * that can be absent is therefore typed `T | null`, never `T | undefined` and
 * never `prop?: T`, and the key is always present in the payload.
 *
 * A `null` here means the underlying column is null. It is never a rewriting of a
 * legacy sentinel. The legacy null contract in
 * `Library/Components/Shared/Null.vb` held no database nulls in memory at all: it
 * substituted a per-type sentinel on every read — `-1` for an absent integer
 * (`L41-L45`), the *empty string* rather than a null reference for an absent
 * string (`L71-L75`), `Date.MinValue` for an absent date (`L66-L70`), `False` for
 * an absent boolean, `255` for an absent byte and `Guid.Empty` for an absent
 * identifier. Its `IsNull` helper (`L207-L237`) answered true for `-1`, for the
 * empty string and for `False` alike, which is why **every boolean on this wire is
 * a plain non-nullable `boolean`** and never `boolean | null`: a legacy `False`
 * was indistinguishable from "no value", and collapsing that distinction into a
 * nullable type would invent information the source never carried.
 *
 * ## Identifiers are never tested by value
 *
 * No test of the form `if (id)` is sound against this schema, and neither is a
 * comparison against zero. The identity seeds, read from
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`, are
 * `Portals.PortalID IDENTITY(-1,1)` (`L77`), `Roles.RoleID IDENTITY(0,1)`
 * (`L115`), `Tabs.TabID IDENTITY(0,1)` (`L140`), `Modules.ModuleID IDENTITY(0,1)`
 * (`L221`) and `Users.UserID IDENTITY(1,1)` (`L98`). Zero is consequently a real
 * portal, role, tab and module identifier, and `-1` is a real *portal* identifier
 * as well as the legacy absent-integer sentinel. Presence is expressed only by
 * nullability, and a caller establishes it with an explicit `=== null` — never by
 * truthiness, never by a magnitude comparison, and never by coalescing to a
 * stand-in value.
 *
 * ## What this file is not
 *
 * It declares types and two integer code tables. There is no class, no
 * constructor, no decorator, no injectable, no helper that resolves a quota and
 * no arithmetic of any kind: a fee is clamped by the server
 * (`Library/Components/Portal/PortalController.vb:L395` and `L398` become a
 * server-side maximum-of-two), never by a client. The only JavaScript this module
 * emits at runtime is the two enumerations below; everything else erases
 * completely, and both imports are type-only for that reason. Because it holds no
 * behaviour, it has no paired spec — the compiler is the whole of its
 * verification.
 */

import type { PagedResult } from './paged-result.model';
import type { SelectOption } from './select-option.model';

/**
 * How a portal admits new accounts.
 *
 * Mirrors the API enumeration of the same name member for member, so the four
 * ordinals below are the values that appear on the wire.
 */
// MIGRATION: this enumeration is a RENAME, and the ordinals are load-bearing data rather than
//   incidental numbering. The legacy type is declared with explicit values at
//   Library/Components/Shared/Globals.vb:L84-L89 under the name PortalRegistrationType, and the
//   modern contract renames it to UserRegistrationMode while preserving every value and every
//   member identifier. Two independent reasons make the ordinals immovable. First, the backing
//   column Portals.UserRegistration is terminal int NOT NULL (01.00.00.SqlDataProvider:L84,
//   tightened by 01.00.05.SqlDataProvider:L1372), so the stored data IS the ordinal. Second, the
//   legacy screen round-tripped the value BY LIST POSITION rather than by name -
//   Website/admin/Portal/SiteSettings.ascx.vb:L277 assigns the stored integer straight to the
//   radio group's selected index and L773 writes the selected index straight back - so renumbering
//   the members would silently re-point every existing portal at a different policy.
//
// MIGRATION: the member identifiers are deliberately NOT shortened to the four words the screen
//   displays. The legacy labels read "None", "Private", "Public" and "Verified", and the settings
//   screen still shows exactly those words, but the identifiers keep the legacy spelling so that a
//   reader moving between the legacy source, the API enumeration and this file sees one name for
//   one concept. A similarly-named membership-status enumeration exists in the legacy tree and is a
//   different, out-of-scope type; it is not merged into this one.
//
// MIGRATION: represented as integers on the wire, not as member names. The API registers no
//   blanket string-enumeration converter - Application/Serialization/DnnJsonConverters.cs:L31
//   records that decision and its converter list claims only the billing-frequency and
//   permission-key types - and neither this enumeration nor the advertising enumeration below
//   carries a converter attribute. A numeric enumeration with every ordinal written out is
//   therefore the faithful representation. It is an ordinary enumeration rather than a constant
//   one because `isolatedModules` is enabled, under which a constant enumeration is not a sound
//   declaration; the consequence is that this is one of the two declarations in the file that
//   survive into the emitted bundle.
export enum UserRegistrationMode {
  /** No self-registration is offered. Displayed as "None". */
  NoRegistration = 0,
  /** An account is requested, then authorised by an administrator. Displayed as "Private". */
  PrivateRegistration = 1,
  /** An account becomes usable immediately. Displayed as "Public". */
  PublicRegistration = 2,
  /**
   * An account becomes usable once a mailed verification code is presented.
   * Displayed as "Verified".
   *
   * This is the one mode under which the sign-in payload's verification code is
   * required; the authentication contract documents that dependency rather than
   * restating this enumeration, which is declared here and nowhere else.
   */
  VerifiedRegistration = 3,
}

/**
 * Where banner advertising is administered for a portal.
 *
 * Mirrors the API enumeration of the same name: `None = 0`, `Site = 1`,
 * `Host = 2`.
 */
// MIGRATION: no legacy enumeration exists for this value. Library/Components/Portal/PortalInfo.vb:L133
//   declares a bare Integer, and the authority for the three admitted values is the markup of the
//   legacy screen, Website/admin/Portal/sitesettings.ascx:L123-L125. Note the lower-case markup
//   filename beside its PascalCase code-behind - every markup file in that directory is lower-case
//   except one - so the two must not be assumed to share a spelling.
//
// MIGRATION: the value 2 carries meaning beyond its own identity and must not be renumbered or
//   folded into the site case. Website/admin/Portal/SiteSettings.ascx.vb:L295 reads it as a LOCK,
//   disabling the whole radio group precisely when the stored value is the host case, so a screen
//   derives that lock from the stored value rather than accepting it as a separate input that could
//   contradict the data. The backing column Portals.BannerAdvertising is terminal int NOT NULL.
export enum BannerAdvertisingMode {
  /** No banner advertising is shown. */
  None = 0,
  /** Banners are administered by the portal's own operators. */
  Site = 1,
  /**
   * Banners are administered by the hosting provider.
   *
   * Also the state in which the settings screen locks the choice; see the note
   * above.
   */
  Host = 2,
}

/**
 * One host name through which a portal is reached.
 *
 * Mirrors `Dtos/Portal/PortalAliasDto.cs`, which carries exactly three members —
 * the same three the legacy `PortalAliasInfo.vb` declared at `L37`, `L45` and
 * `L53`.
 *
 * Returned by `GET /api/v1/portals/{portalId}/aliases`, which is the authoritative
 * place to list, add and remove aliases.
 */
// MIGRATION: all three member names are modernised away from the legacy all-capitals acronym
//   spelling, so none of them matches its legacy counterpart. HTTPAlias (PortalAliasInfo.vb:L53)
//   becomes HttpAlias on the contract and therefore httpAlias here; PortalAliasID (L45) and
//   PortalID (L37) become PortalAliasId and PortalId and therefore portalAliasId and portalId. A
//   consumer that reads a legacy spelling from a payload finds nothing. The stored column names are
//   untouched - the rename lives at the contract boundary only.
//
// MIGRATION: TENANT RESOLUTION NO LONGER MATCHES ON A SUBSTRING. The legacy resolution procedure
//   compared an incoming host name against stored aliases with leading and trailing wildcards -
//   01.00.00.SqlDataProvider:L4569-L4600 selects min(PortalID) from Portals where PortalAlias like
//   '%' + @PortalAlias + '%' - so an alias that was a substring of another portal's alias could
//   resolve a request to the WRONG TENANT, and the min() collapsed any ambiguity silently. The
//   replacement resolves an alias by exact match on host and port and refuses an ambiguous one
//   instead of collapsing candidates. This is a DELIBERATE behavioural difference that closes a
//   multi-tenant mis-resolution hazard, recorded as such rather than presented as a silent
//   improvement. Nothing about the matching rule is transported by this contract, which carries only
//   the alias value.
//
// MIGRATION: there is no wildcard member and no value of portalId carries a special meaning. The
//   legacy "every alias, across every portal" query was expressed by passing a negative integer
//   sentinel in place of a portal filter (PortalAliasController.vb:L86-L88 into a procedure that
//   admitted every row when given it). An unfiltered query is now expressed by omitting the
//   predicate, never by transmitting a magic number.
export interface PortalAlias {
  /**
   * Surrogate key of this alias row.
   *
   * The column is `PortalAlias.PortalAliasID int IDENTITY(1,1) NOT NULL`, so the
   * value is assigned by the database on insert and is never null on a response.
   */
  readonly portalAliasId: number;

  /**
   * The portal that owns this alias.
   *
   * Always populated on a response, because an alias row always belongs to a
   * portal. Every value it can hold is a meaningful portal identifier — see the
   * identity-seed note in this file's header — so absence must never be inferred
   * from a negative value or from zero.
   */
  readonly portalId: number;

  /**
   * The host name, optionally with a port.
   *
   * Nullable because the contract declares it nullable. The legacy absent-string
   * sentinel was the empty string rather than a null reference, so an empty string
   * and a `null` are distinct states here and neither is rewritten into the other.
   */
  readonly httpAlias: string | null;
}

/**
 * One row of the portal list, as returned by `GET /api/v1/portals`.
 *
 * Mirrors `Dtos/Portal/PortalListItemDto.cs`, whose eight members are the eight
 * data columns the legacy grid rendered (`Website/admin/Portal/portals.ascx`).
 * This is a deliberately narrower shape than {@link PortalDetail}: a list row
 * carries what the grid displayed and nothing more.
 */
// MIGRATION: three members are non-nullable here while their counterparts on the detail and
//   settings contracts are nullable, and the difference is real rather than an oversight.
//   portalName is initialised to an empty string on the list contract; hostSpace is int NOT NULL
//   with a zero default, tightened from the nullable baseline at 03.01.01.SqlDataProvider:L1119 and
//   L1131; and hostFee is likewise non-nullable after the same conversion. Absence is expressed on
//   this contract by the defaulted nought, and that vocabulary is preserved at the boundary rather
//   than reinterpreted - nought is NOT read as "unset".
export interface PortalListItem {
  /**
   * The portal's identifier.
   *
   * See the identity-seed note in this file's header: both `-1` and `0` are real
   * portal identifiers, so this value must never be tested for truthiness or
   * compared against zero to decide whether a portal exists.
   */
  readonly portalId: number;

  /**
   * The portal's title.
   *
   * Non-nullable on this contract, which initialises it to an empty string. The
   * counterpart on {@link PortalDetail} is nullable.
   */
  readonly portalName: string;

  /**
   * The host names through which the portal is reached, as bare strings.
   *
   * Mandatory and never null: a portal with no alias is a real, reportable state
   * and is expressed by an empty array, so no caller need guard before iterating.
   * Host names only — a caller administering aliases uses the alias sub-resource,
   * which additionally reports the identifiers as {@link PortalAlias}.
   */
  readonly aliases: readonly string[];

  /**
   * Number of accounts registered against the portal.
   *
   * Derived, not stored. The legacy property (`PortalInfo.vb:L309`) counted rows
   * on demand and no such column exists; it is reported for display and appears on
   * neither request contract.
   */
  readonly users: number;

  /**
   * Number of pages in the portal.
   *
   * Derived, not stored — the same counting arrangement as {@link users}
   * (`PortalInfo.vb:L320`).
   */
  readonly pages: number;

  /**
   * Disk-space allowance in megabytes, where nought means no imposed limit.
   *
   * Non-nullable here. An integer rather than a floating-point value: three legacy
   * sources disagreed — `PortalInfo.vb:L165` declared it `Integer` while the
   * twenty-seven-parameter setter declared it `Double` — and the terminal column,
   * `int NOT NULL`, decides. It is an allowance, not a measurement of space
   * consumed, and the legacy grid's "DiskSpace" caption is a display concern.
   */
  readonly hostSpace: number;

  /**
   * Recurring hosting fee, nought when none is charged.
   *
   * Non-nullable here, and reported unformatted: the legacy grid's two-decimal
   * format string is a presentation concern, and a preformatted string would
   * impose one culture's conventions on every caller and be useless for
   * arithmetic. The currency unit is not carried on the list contract because the
   * legacy grid did not render it; a caller needing it reads
   * {@link PortalDetail.currency} or {@link PortalSettings.currency}.
   */
  readonly hostFee: number;

  /**
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null` when
   * the column itself is null.
   *
   * Genuinely nullable, unlike the two members above — this column really is
   * `NULL` in the terminal state and was never tightened.
   *
   * Legacy data may express "does not expire" as the minimum representable date,
   * `0001-01-01`, which was the absent-date sentinel
   * (`Library/Components/Shared/Null.vb:L66-L70`). That value is **not** rewritten
   * into `null`, so the two stored states stay distinguishable here. A consumer
   * rendering this value should treat the minimum date as "no expiry" and must not
   * assume `null` is the only way absence arrives.
   */
  readonly expiryDate: string | null;
}

/**
 * A page of portal list rows.
 *
 * `PagedResult` and the request envelope are owned by `./paged-result.model` and
 * are not redeclared here; this alias exists only to name the pairing.
 *
 * Two properties of the list endpoint are worth stating because both are easy to
 * get wrong from the client side. The legacy screen paged this list
 * (`Website/admin/Portal/Portals.ascx.vb:L142` passes a filter, a zero-based page
 * index and a page size, and receives a total), superseding the untyped
 * collection that `PortalController.vb:L1263` returned. And the name filter is a
 * **starts-with** match: the legacy call appended the wildcard itself, so the
 * server appends it and a caller must not add one to the query value.
 */
export type PortalListPage = PagedResult<PortalListItem>;


/**
 * A single portal in full, as returned by `GET /api/v1/portals/{portalId}`.
 *
 * Mirrors `Dtos/Portal/PortalDetailDto.cs` and its thirty-eight members.
 *
 * The legacy entity `Library/Components/Portal/PortalInfo.vb` declared thirty-nine
 * public properties; the differences are deliberate and are recorded in the notes
 * below rather than left to inference.
 */
// MIGRATION: THE PORTAL IDENTIFIER ADMITS BOTH -1 AND 0 AS REAL VALUES, which makes every
//   conventional emptiness test unsound against it. Portals.PortalID is declared
//   [int] IDENTITY (-1, 1) NOT NULL at 01.00.00.SqlDataProvider:L77, so the seed value is the very
//   same -1 that the legacy null contract used as its absent-integer sentinel
//   (Library/Components/Shared/Null.vb:L41-L45), and the first portal allocated after it is
//   numbered 0. That zero is a genuine, addressable portal is confirmed independently by the
//   installation's own seed data, which inserts a Registered Users role against PortalID = 0 at
//   01.00.00.SqlDataProvider:L7194. A consumer must therefore never write a truthiness test on this
//   value, never compare it against zero for magnitude, and never substitute -1 for a missing one:
//   presence is established with an explicit === null and by nothing else. The server carries the
//   same prohibition structurally - its portal identifier value object exists precisely to forbid
//   reading -1 as "absent", and its entity base is forbidden from offering any is-new or
//   default-comparison test for the same reason.
//
// MIGRATION: THE FOUR QUOTA-STYLE VALUES CARRY TWO DISTINCT FACTS IN ONE COLUMN AND MUST NEVER BE
//   COALESCED. Measured on userQuota: Library/Components/Portal/PortalController.vb:L87 hydrates a
//   database null into -1, while L355-L357 initialises the same value to 0 - so 0 means UNLIMITED
//   and -1 means NOT SET, and the two are not interchangeable. hostSpace (PortalInfo.vb:L165),
//   pageQuota (L173) and siteLogHistory (L277) follow the same discipline. Consequently nothing in
//   this file coalesces such a value to nought, substitutes an "unlimited" label for it, makes it an
//   optional property or defaults it in any other way: a consumer that wants to render "unlimited"
//   must branch on the exact value and must preserve whatever it received when it writes back.
//   These members are typed nullable here because the contract declares them nullable and the wire
//   therefore genuinely carries null; null means the column is null, and it is a THIRD state
//   alongside the two the legacy column encoded.
//
// MIGRATION: THE SIX PAGE-REFERENCE MEMBERS USE -1 FOR "NO SUCH PAGE IS CONFIGURED", AND ZERO IS A
//   REAL PAGE. adminTabId, superTabId, splashTabId, homeTabId, loginTabId and userTabId
//   (PortalInfo.vb:L293, L301, L332, L340, L348, L356) were all integers in which the legacy
//   absent-integer sentinel meant "unconfigured". The compounding hazard is that Tabs.TabID is
//   declared IDENTITY(0,1) at 01.00.00.SqlDataProvider:L140, so a truthiness test would classify the
//   FIRST REAL PAGE of an installation as unconfigured. None of the six is optional here, none is
//   coalesced, and none may be tested by magnitude.
//
// MIGRATION: TWELVE OF THE LEGACY ENTITY'S THIRTY-NINE PROPERTIES DO NOT REACH THIS CONTRACT
//   UNCHANGED, and two of them reach NO contract at all:
//     HomeDirectoryMapPath (PortalInfo.vb:L388) is DELIBERATELY NOT MODELLED. It is the single
//       read-only property on the legacy entity and it derives an ABSOLUTE SERVER FILESYSTEM PATH
//       from the static application-path utility that this migration excludes. Publishing a server
//       path to a browser leaks infrastructure detail, and accepting one back would be a
//       path-traversal hazard, so no member of that name exists on any contract. The relative
//       homeDirectory below is the portal-scoped directory name and is the writable member; the
//       absolute path is derived server-side and stays there.
//     Version (L395) is absent because it is not a Portals column at all - the only column of that
//       name in the schema chain belongs to the desktop-modules table - and it is absent from the
//       portal read view.
//   The remaining ten are present here but not updatable; the update contract's own notes record
//   which and why.
//
// MIGRATION: THE PAYMENT-PROCESSOR PASSWORD APPEARS ON NO RESPONSE CONTRACT. PortalInfo.vb:L261
//   declares ProcessorPassword, and it is carried ONLY by UpdatePortalRequest, verified at
//   Dtos/Portal/UpdatePortalRequest.cs:L819 and verified absent from PortalDetailDto,
//   PortalListItemDto, PortalSettingsDto and PortalAliasDto. It is therefore write-only across this
//   boundary: a stored processor password is never returned to a browser, and the settings form
//   starts that field blank on every load. Relatedly, the legacy configuration committed a
//   reversible three-key symmetric decryption key to source control alongside its password format;
//   no such key or key material is reproduced here in any form.
export interface PortalDetail {
  /**
   * The portal's identifier. Both `-1` and `0` are real values — see the note
   * above, which is the single most misread value in this domain.
   */
  readonly portalId: number;

  /** The portal's title. Nullable here, unlike on {@link PortalListItem}. */
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
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null`.
   *
   * The legacy absent-date sentinel `0001-01-01` is not rewritten into `null`; see
   * {@link PortalListItem.expiryDate}.
   */
  readonly expiryDate: string | null;

  /** How the portal admits new accounts. Non-nullable. */
  readonly userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. Non-nullable. */
  readonly bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code; the column is fixed-width `char(3)`. */
  readonly currency: string | null;

  /** The account holding the portal administrator role. */
  readonly administratorId: number | null;

  /**
   * The portal administrator's e-mail address.
   *
   * Not a `Portals` column. It reaches this contract through the portal read
   * view's outer join to the users table on the administrator identifier, so it is
   * the *administrator's* address rather than a property of the portal — which is
   * why changing it is a user operation and why it appears on no portal request
   * contract.
   */
  readonly email: string | null;

  /** Recurring hosting fee. Host-administered. Reported unformatted. */
  readonly hostFee: number | null;

  /** Disk-space allowance in megabytes; nought means unlimited. See the quota note. */
  readonly hostSpace: number | null;

  /** Ceiling on pages; nought means unlimited, `-1` in legacy data meant not set. */
  readonly pageQuota: number | null;

  /**
   * Ceiling on accounts.
   *
   * The canonical instance of the quota hazard: nought means **unlimited** and
   * `-1` meant **not set**. Never coalesced, never defaulted.
   */
  readonly userQuota: number | null;

  /** Number of accounts. Derived on read, never stored, never submitted. */
  readonly users: number;

  /** Number of pages. Derived on read, never stored, never submitted. */
  readonly pages: number;

  /** The role granting portal administration. Zero is a legitimate role identifier. */
  readonly administratorRoleId: number | null;

  /** Display name of the administrator role. Computed by the read view; read-only. */
  readonly administratorRoleName: string | null;

  /** The role every registered account receives. Zero is a legitimate role identifier. */
  readonly registeredRoleId: number | null;

  /** Display name of the registered-users role. Computed by the read view; read-only. */
  readonly registeredRoleName: string | null;

  /**
   * Immutable identifier assigned when the portal is provisioned.
   *
   * The column is `uniqueidentifier NOT NULL` with a `newid()` default, so this is
   * non-nullable. The legacy absent-identifier sentinel was the all-zero value
   * `00000000-0000-0000-0000-000000000000`; that value means "unset" and is **not**
   * converted into `null`. Never client-writable, so it appears on no request
   * contract.
   */
  readonly guid: string;

  /** Name of the configured payment processor. */
  readonly paymentProcessor: string | null;

  /** Account identifier held with the payment processor. */
  readonly processorUserId: string | null;

  /** Days of portal-activity history retained; see the quota note. */
  readonly siteLogHistory: number | null;

  /**
   * The page hosting the portal's administration menu.
   *
   * A page reference: `-1` meant "no such page is configured" and zero is a real
   * page. Established at provisioning and thereafter maintained by page
   * administration, so it appears on no request contract.
   */
  readonly adminTabId: number | null;

  /**
   * The host-level root page.
   *
   * Read-only, and **identical for every portal**: the read view computes it with
   * an *uncorrelated* sub-select that names no portal — it selects the single page
   * belonging to no portal and having no parent — so the same value is reported for
   * every portal. Carried for parity with the legacy object and must not be
   * mistaken for something a portal owns or an administrator can change.
   */
  readonly superTabId: number | null;

  /** The page shown to a first-time visitor ahead of the home page. See the page-reference note. */
  readonly splashTabId: number | null;

  /** The portal's home page. See the page-reference note. */
  readonly homeTabId: number | null;

  /** The page carrying the sign-in form. See the page-reference note. */
  readonly loginTabId: number | null;

  /** The page carrying the account screens. See the page-reference note. */
  readonly userTabId: number | null;

  /** Culture code, at most six characters. */
  readonly defaultLanguage: string | null;

  /**
   * Offset from co-ordinated universal time, in minutes.
   *
   * Nought is a meaningful offset rather than a missing value — it is the United
   * Kingdom's — so this member is subject to the same prohibition on truthiness
   * tests as the identifiers.
   */
  readonly timeZoneOffset: number | null;

  /** Portal-relative root of the portal's file storage. */
  readonly homeDirectory: string | null;

  /**
   * The portal's host names, when they were loaded for this response.
   *
   * **Three states, and the first two must not be collapsed.** `null` means the
   * aliases were not requested or not loaded and says nothing about how many exist
   * — the nullability lets a service skip the alias query on paths that do not need
   * it without the payload misreporting the result. An empty array means they were
   * loaded and there are none. A populated array means they were loaded and these
   * are they. When `null`, a caller obtains them from
   * `GET /api/v1/portals/{portalId}/aliases`.
   *
   * Deliberately **not** a paged envelope: aliases are returned in full.
   */
  readonly aliases: readonly PortalAlias[] | null;
}


/**
 * A portal's editable configuration, as returned by
 * `GET /api/v1/portals/{portalId}/settings`.
 *
 * Mirrors `Dtos/Portal/PortalSettingsDto.cs` and its twenty-seven members. It is
 * the read counterpart of {@link UpdatePortalRequest}, and the two differ in
 * exactly two places, neither accidental:
 *
 * - `guid` is present here and absent from the request, because it is assigned at
 *   provisioning and is never editable.
 * - `processorPassword` is present on the request and absent here, because a
 *   stored payment-processor password is never returned to a browser.
 *
 * It also carries fewer members than {@link PortalDetail}: the computed counts,
 * the administrator's e-mail, the role identifiers and names, and the two
 * provisioning-owned page references are all reportable but not settings, so they
 * appear on the detail contract only.
 */
// MIGRATION: THERE IS NO PORTAL-SETTING KEY/VALUE ENTITY, AND THIS TYPE IS NOT A SETTINGS BAG.
//   Four independent findings establish it. The legacy abstract data surface declares no such member
//   among its 269 abstract methods; the concrete provider invokes no such procedure among its 245;
//   no table of that name appears anywhere in the eighty-eight-script schema chain, which contains
//   only module-settings, host-settings, tab-module-settings and schedule-item-settings tables plus
//   one transient staging table; and positively,
//   Library/Components/Portal/PortalController.vb:L1209-L1210 shows the legacy accessor returning an
//   object pulled out of the per-request item collection - a PER-REQUEST AMBIENT COMPOSITE, not a
//   persisted aggregate.
//   Portal configuration therefore lives as COLUMNS on the Portals table, and the contract this type
//   mirrors is a flat projection from those columns. Accordingly there is no dictionary member here,
//   no string-keyed map of settings and no row type for an individual setting - and there is
//   deliberately no mirror of the legacy ambient composite either. That object became a scoped,
//   immutable server-side portal context which never crosses the wire; its eight members are the
//   portal identifier and name, the alias, the administrator identifier, and the two role
//   identifiers with their two names - and, being immutable, it exposes no settable page pointer of
//   the kind the legacy composite carried at PortalSettings.vb:L398 and L548. That mutable ambient
//   state is exactly what the target architecture eliminates, so nothing corresponding to it is
//   declared here. Where these facts overlap the signed-in caller's shape in the authentication
//   contract, that contract is the place they are described for the caller; this one describes the
//   portal.
export interface PortalSettings {
  /** The portal's identifier. Both `-1` and `0` are real values; see {@link PortalDetail}. */
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
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null`.
   *
   * The legacy absent-date sentinel `0001-01-01` is preserved, never rewritten into
   * `null`.
   */
  readonly expiryDate: string | null;

  /** How the portal admits new accounts. Non-nullable. */
  readonly userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. Non-nullable. */
  readonly bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code. */
  readonly currency: string | null;

  /** The account holding the portal administrator role. */
  readonly administratorId: number | null;

  /** Recurring hosting fee. Host-administered; see the host-only note on the request type. */
  readonly hostFee: number | null;

  /** Disk-space allowance in megabytes; nought means unlimited. Host-administered. */
  readonly hostSpace: number | null;

  /** Ceiling on pages. Host-administered. Never coalesced; see the quota note. */
  readonly pageQuota: number | null;

  /** Ceiling on accounts; nought means unlimited. Host-administered. Never coalesced. */
  readonly userQuota: number | null;

  /** Name of the configured payment processor. */
  readonly paymentProcessor: string | null;

  /** Account identifier held with the payment processor. */
  readonly processorUserId: string | null;

  /** Days of portal-activity history retained. Host-administered. */
  readonly siteLogHistory: number | null;

  /** The page shown ahead of the home page, if any. See the page-reference note. */
  readonly splashTabId: number | null;

  /** The portal's home page. See the page-reference note. */
  readonly homeTabId: number | null;

  /** The page carrying the sign-in form. See the page-reference note. */
  readonly loginTabId: number | null;

  /** The page carrying the account screens. See the page-reference note. */
  readonly userTabId: number | null;

  /** Culture code, at most six characters. */
  readonly defaultLanguage: string | null;

  /** Offset from co-ordinated universal time in minutes; nought is a real offset. */
  readonly timeZoneOffset: number | null;

  /**
   * Portal-relative root of the portal's file storage.
   *
   * The portal-scoped directory name only. The absolute server path derived from it
   * is deliberately not published; see the note on {@link PortalDetail}.
   */
  readonly homeDirectory: string | null;

  /**
   * Immutable identifier assigned at provisioning.
   *
   * Non-nullable, and absent from {@link UpdatePortalRequest} because it is never
   * editable. The all-zero value means "unset" and is never converted to `null`.
   */
  readonly guid: string;
}


/**
 * Body of `POST /api/v1/portals`, which provisions a new portal.
 *
 * Mirrors `Dtos/Portal/CreatePortalRequest.cs` and its twelve members.
 *
 * Provisioning is not the inverse of {@link UpdatePortalRequest}. It supplies the
 * portal's identity, its first host alias, the template to build it from and the
 * particulars of its first administrator account; everything else — the role
 * identifiers, the page references, the assigned identifier and the
 * host-administered terms — is established by the server as a consequence.
 */
// MIGRATION: this contract replaces the FIFTEEN POSITIONAL ARGUMENTS of the legacy provisioning
//   call. Named fields rather than an ordered tuple is the whole point: a positional list of
//   fifteen, of which several were adjacent strings, could be mis-ordered without any diagnostic,
//   whereas a mis-named field here is a compile error.
//
// MIGRATION: no computed value appears on this contract. The legacy entity's account and page counts
//   (PortalInfo.vb:L309 and L320) are derived on read and are meaningless as input, so they are
//   absent - as is the assigned identifier, which the database allocates, and the provisioning
//   identifier, which carries a database-side default.
export interface CreatePortalRequest {
  /** The new portal's title. */
  portalName: string | null;

  /**
   * The first host name through which the portal will be reached.
   *
   * A single alias, not a collection: further aliases are added afterwards through
   * the alias sub-resource. Matched later by exact host and port, never by
   * substring — see the note on {@link PortalAlias}.
   */
  portalAlias: string | null;

  /** Free-text description offered to search engines. */
  description: string | null;

  /** Comma-separated search keywords. */
  keyWords: string | null;

  /** Portal-relative root for the new portal's file storage. */
  homeDirectory: string | null;

  /** The portal template the new portal is built from. */
  templateFile: string | null;

  /**
   * Whether the portal is reached through a path beneath an existing host name
   * rather than through a host name of its own.
   *
   * A plain `boolean`, never `boolean | null`: the legacy null contract treated
   * `False` and "no value" as the same thing, so a nullable boolean here would
   * invent a state the source never distinguished.
   */
  isChildPortal: boolean;

  /** Given name of the portal's first administrator. */
  administratorFirstName: string | null;

  /** Family name of the portal's first administrator. */
  administratorLastName: string | null;

  /** Sign-in name of the portal's first administrator. */
  administratorUsername: string | null;

  /**
   * Initial password for the portal's first administrator.
   *
   * Write-only, like {@link UpdatePortalRequest.processorPassword}: it is accepted
   * here and returned by nothing. The server stores a one-way hash, which is a
   * documented departure from the legacy store — that used a reversible format with
   * retrieval enabled, and password retrieval is deliberately not carried forward.
   */
  administratorPassword: string | null;

  /** E-mail address of the portal's first administrator. */
  administratorEmail: string | null;
}

/**
 * Body of `PUT /api/v1/portals/{portalId}`.
 *
 * Mirrors `Dtos/Portal/UpdatePortalRequest.cs` and its twenty-seven members, in the
 * order that contract declares them.
 *
 * This is a **whole-row replacement**, which the settings form must respect:
 * omitting a numeric term does not mean "leave it alone". The server substitutes
 * nought for an omitted numeric because the backing columns cannot hold null, so a
 * request that leaves the hosting fee out is a request to waive it. A form
 * therefore sends every field it holds, and populates the host-administered group
 * from the loaded settings even when the operator may not edit it.
 */
// MIGRATION: this contract replaces the TWENTY-SEVEN POSITIONAL ARGUMENTS of
//   Library/Components/Portal/PortalController.vb:L1568. Every one of those arguments appears below
//   as a named field, in the declared order, with nothing added and nothing removed. The legacy
//   member was a Sub returning nothing at all: a caller could not learn whether the write had
//   succeeded, which portal had been written, or why it had failed. The outcome now travels in the
//   service's result object, so no member below encodes a status, an outcome, an error or a
//   concurrency token - an update request describes only the desired state.
//
// MIGRATION: TWELVE of the legacy entity's thirty-nine properties are deliberately absent, each for
//   a measured reason: the two role identifiers and the two role names, the assigned provisioning
//   identifier, the administrator's e-mail, the administration-menu page and the host root page, the
//   two computed counts, the absolute filesystem path and the version string. The role names and the
//   host root page are computed by the read view and can never be written; the e-mail belongs to the
//   joined administrator account; the version string is not a Portals column; the counts are not
//   persisted; the assigned identifier is immutable; the remaining page and role identifiers are
//   established at provisioning and thereafter maintained by page and role administration; and the
//   absolute filesystem path is withheld for the reasons given on PortalDetail.
//
// MIGRATION: SIX OF THESE MEMBERS ARE HOST-ONLY, AND THE RULE LIVES IN THE SERVICE RATHER THAN IN
//   THIS SHAPE. Website/admin/Portal/SiteSettings.ascx.vb:L760-L770 compared the submitted hosting
//   fee, disk-space allowance, page quota, account quota, activity-history retention and expiry date
//   against the stored portal and rejected the ENTIRE save when a caller who was not a host operator
//   had altered any of them. That is a live authorisation rule over this payload's contents; it is
//   reproduced server-side and mirrored by the settings form, and it is deliberately not expressible
//   here, because a request shape validates nothing and authorises nothing.
//
// MIGRATION: THE LEGACY SAVE PATH COMPILED WITHOUT STRICT TYPING AND RELIED ON COERCIONS THAT THE
//   MODERN STACK REJECTS. The web pages were compiled with strict typing disabled while the class
//   library was not, so the code-behind was permitted narrowing conversions that C# and TypeScript
//   both refuse. Two of them changed the stored value rather than merely the syntax: the disk-space
//   local was declared as a floating-point number even though the column is an integer, so a
//   fractional entry parsed, travelled and was silently truncated by the database; and the account
//   quota was likewise floating-point despite its name, then passed to an integer parameter. Both are
//   integers here. The remaining coercions concerned blank inputs, which variously left a local at
//   nought or at the legacy absent-integer sentinel; none of that defaulting happens in this file,
//   which performs no parse, no clamp and no substitution of any kind.
export interface UpdatePortalRequest {
  /**
   * The portal being written. Must agree with the `{portalId}` route segment.
   *
   * Its presence does not make the body a second authority: the route remains the
   * sole subject of the write, and the server refuses a disagreement with a 400
   * naming this field. Both `-1` and `0` are real identifiers, so no lower bound
   * and no emptiness test applies.
   */
  portalId: number;

  /** The portal's title, at most 128 characters. */
  portalName: string | null;

  /** Portal-relative path of the logo image, at most 50 characters. */
  logoFile: string | null;

  /** Copyright line, at most 100 characters. */
  footerText: string | null;

  /**
   * When the hosting subscription lapses, as an ISO 8601 instant, or `null`.
   *
   * Host-only. The legacy screen carried a data-type validator on this field and
   * nothing stronger.
   */
  expiryDate: string | null;

  /** How the portal admits new accounts. Round-trips by ordinal; see the enumeration. */
  userRegistration: UserRegistrationMode;

  /** Where banner advertising is administered. */
  bannerAdvertising: BannerAdvertisingMode;

  /** Three-letter currency code, or `null`. */
  currency: string | null;

  /** The account to hold the portal administrator role. */
  administratorId: number | null;

  /**
   * Recurring hosting fee, denominated in {@link currency}.
   *
   * Host-only. Submitted unformatted and unclamped: the legacy guard that raised a
   * negative fee to nought is applied by the server, so a client neither clamps nor
   * rounds.
   */
  hostFee: number | null;

  /** Disk-space allowance in whole megabytes; nought means unlimited. Host-only. */
  hostSpace: number | null;

  /** Ceiling on pages. Host-only. Sent back exactly as received; never coalesced. */
  pageQuota: number | null;

  /**
   * Ceiling on accounts; nought means unlimited.
   *
   * Host-only. The canonical quota hazard — see the note on {@link PortalDetail}.
   * Whatever value was loaded is sent back unchanged unless the operator edited it.
   */
  userQuota: number | null;

  /** Payment processor name, at most 50 characters. */
  paymentProcessor: string | null;

  /** Payment processor account identifier, at most 50 characters. */
  processorUserId: string | null;

  /**
   * Payment processor password, at most 50 characters.
   *
   * **Write-only.** This is the one field on this contract that no response
   * carries, so a form cannot pre-populate it: it is present here and on no
   * response shape at all. A blank submission is sent as `null`, which the server
   * reads as "leave the stored secret alone".
   */
  processorPassword: string | null;

  /** Portal description, at most 500 characters. */
  description: string | null;

  /** Search keywords, at most 500 characters. */
  keyWords: string | null;

  /** Portal-relative path of the background image, at most 50 characters. */
  backgroundFile: string | null;

  /** Days of activity history retained. Host-only. Never coalesced; see the quota note. */
  siteLogHistory: number | null;

  /** The page shown ahead of the home page, if any. See the page-reference note. */
  splashTabId: number | null;

  /** The portal's home page. See the page-reference note. */
  homeTabId: number | null;

  /** The page carrying the sign-in form. See the page-reference note. */
  loginTabId: number | null;

  /** The page carrying the account screens. See the page-reference note. */
  userTabId: number | null;

  /** Culture code, at most six characters. */
  defaultLanguage: string | null;

  /** Offset from co-ordinated universal time in minutes; nought is a real offset. */
  timeZoneOffset: number | null;

  /**
   * Portal-relative root of the portal's file storage, at most 100 characters.
   *
   * The relative directory name only. No absolute server path is accepted from a
   * client; see the note on {@link PortalDetail}.
   */
  homeDirectory: string | null;
}

/**
 * The lookup lists a settings screen needs in order to offer the choices the legacy
 * screen offered.
 *
 * Each member corresponds to one drop-down on
 * `Website/admin/Portal/sitesettings.ascx`, and each defaults to an empty array at
 * the point of use. An empty list is a legitimate state rather than an error: the
 * screen still renders the control, still shows the value currently held and still
 * round-trips it. That is a deliberate improvement on the legacy behaviour, in
 * which a bound list not containing the stored value silently reset the field on
 * the next post.
 *
 * This is a presentation-support shape rather than a wire contract — no endpoint
 * returns it — which is why it composes {@link SelectOption} instead of mirroring a
 * published payload.
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

