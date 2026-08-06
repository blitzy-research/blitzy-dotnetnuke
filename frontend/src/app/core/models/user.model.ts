/**
 * The account-administration wire contract: users, credential changes and the
 * per-tenant membership settings that govern them.
 *
 * Every declaration here mirrors, member for member, a data-transfer object the API
 * already serialises, so that a change on either side of the boundary surfaces as a
 * compilation failure rather than as an `undefined` at run time. The mirrored
 * contracts are `UserListItemDto`, `UserDetailDto`, `CreateUserRequest`,
 * `UpdateUserRequest`, `ChangePasswordRequest` and `MembershipSettingsDto` in the
 * application layer's `Dtos/User` folder.
 *
 * WHAT THIS FILE DELIBERATELY DOES NOT DECLARE
 * --------------------------------------------
 * The profile surface — a user's profile, its individual values, and the tenant's
 * profile-property declarations — is mirrored by the sibling `profile.model.ts` and
 * is NOT restated here. That file already carries an exact, complete mirror of
 * `UserProfileDto`, `UserProfileValueDto` and `ProfilePropertyDefinitionDto`, and it
 * is the file the profile screens already import from. Declaring a second copy of
 * the same three shapes in the same folder would create two sources of truth for one
 * wire contract, which is the failure mode a mirrored contract exists to prevent:
 * the copies would drift, and a caller would have no way to tell which one the
 * server actually honours. Import profile types from `./profile.model`.
 *
 * NAMING
 * ------
 * Member names are camel-cased because that is what the API emits: its serializer
 * applies the camel-case naming policy. Identity members carry a single lower-case
 * `d` — `userId`, `portalId`, `affiliateId` — because the server-side properties are
 * spelled `UserId`, `PortalId` and `AffiliateId`. This matters more than it looks:
 * the camel-case policy lower-cases a leading upper-case RUN, so a server property
 * spelled `UserID` would arrive as `userID` and a client member spelled `userId`
 * would silently read `undefined` with no compilation error to warn anyone. Every
 * identity property on the mirrored contracts was checked, and every one uses the
 * single-`d` form.
 *
 * PRESENT BUT POSSIBLY NULL, NEVER ABSENT
 * ---------------------------------------
 * The API serialises with its ignore condition set to never omit a member, so a
 * response carries every field declared below even when the value is null, zero, an
 * empty string or false. Members are therefore typed as `T | null` where the server
 * property is nullable, and as `T` where it is not — never as optional properties,
 * because an optional property would model an absence the wire never expresses.
 *
 * SENTINELS AND IDENTITY VALUES
 * -----------------------------
 * The legacy code represented absence with in-band sentinel values rather than with
 * null: `Library/Components/Shared/Null.vb` returns -1 for a missing integer, the
 * EMPTY STRING for a missing string, `Date.MinValue` for a missing date and false
 * for a missing boolean. Two consequences bind every consumer of this file.
 *
 * First, several of those sentinels collide with legitimate data. The tenant table
 * seeds its key with `IDENTITY(-1, 1)`, so -1 is a real `portalId` as well as the
 * legacy absent-marker, and the role, page and module tables seed at 0, so 0 is a
 * real identifier too. Identifiers must therefore be tested explicitly — compare
 * against `null` or `undefined` — and never with a truthiness test, a `> 0` test or
 * a `?? -1` fallback, each of which would misread a valid record as a missing one.
 * The account key itself seeds at `IDENTITY(1, 1)`, but the same discipline applies
 * so that one screen does not reason differently from the next.
 *
 * Second, because the empty string was the legacy absent-marker for text, "absent"
 * frequently arrives as `""` rather than as null. `displayName` is the clearest
 * case: its column is declared not-null with a default of the empty string, so the
 * sentinel is baked into the schema and the member is typed `string`, not
 * `string | null`.
 *
 * TYPES ONLY, WITH TWO DELIBERATE EXCEPTIONS
 * ------------------------------------------
 * This file declares no class, no constructor, no decorator and no function. Two
 * enumerations do emit run-time JavaScript, which is intended: they carry integer
 * ordinals that must survive as values, and the compile-time-inlined variety of
 * enumeration is unavailable because the project compiles with isolated modules,
 * which forbids it outright. Nothing else here exists at run time,
 * which is why the file has no paired specification — there is no behaviour to
 * assert, and the type checks that matter are performed over every consumer.
 */

/*
 * MIGRATION: the credential store changed shape, and no member of it appears on any
 * contract in this file.
 *
 * The legacy application stored passwords REVERSIBLY. Its membership provider was
 * registered with `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`
 * (`Website/release.config:L245` and `L239`), and the symmetric key that decrypted
 * every stored password was committed to source control in the same file at
 * `L89-L93`. That key is not reproduced here, in any form, and neither is any hash,
 * salt or other credential material: the target hashes passwords one-way with
 * BCrypt, so there is nothing to retrieve and nothing to transport.
 *
 * The consequences for these contracts are exact. The legacy `Password`,
 * `PasswordAnswer` and `PasswordQuestion` members of
 * `Library/Components/Users/Membership/UserMembership.vb` (L263, L283 and L303) have
 * no counterpart on any shape below, and the plaintext-password-email flow of
 * `Website/admin/Security/SendPassword.ascx.vb` is abolished rather than ported.
 * Password retrieval is gone; password RESET remains, because the legacy provider
 * enabled both independently (`release.config:L240`) and only retrieval is unsafe.
 * A new password travels in one direction only, on the request shapes that exist to
 * carry it, and never comes back on a response.
 *
 * Existing stored credentials cannot be verified at all: the target holds no legacy
 * verifier and maps no legacy credential column, so no submitted password can be
 * checked against a value written under the legacy reversible scheme. A first
 * successful sign-in against such a value is therefore impossible, and the migration
 * path is administrative RESET for every pre-existing account, without exception.
 * Cost upgrading is a separate, narrower mechanism that applies only to BCrypt hashes
 * this target produced: once such a hash verifies, it may be re-hashed at the current
 * work factor. It is not, and never was, a legacy-credential detector.
 *
 * The legacy column was `[Password] [nvarchar](20) NOT NULL`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L98-L111`).
 * That 20-character storage ceiling is a property of the abandoned store and is
 * deliberately NOT carried onto any contract here as a length constraint.
 */

/*
 * MIGRATION: the legacy password-change outcome enumeration is deliberately not
 * declared, and its measured ordinals are recorded here so the measurement is not
 * lost with it.
 *
 * `Library/Components/Users/Membership/PasswordUpdateStatus.vb:L23-L32` declares
 * eight members and assigns NO explicit values, so declaration order IS the ordinal.
 * As measured, in source order: Success is 0, PasswordMissing is 1,
 * PasswordNotDifferent is 2, PasswordResetFailed is 3, PasswordInvalid is 4,
 * PasswordMismatch is 5, InvalidPasswordAnswer is 6 and InvalidPasswordQuestion
 * is 7.
 *
 * An earlier specification listed the same eight-member SET in a different order,
 * beginning Success, PasswordMismatch, PasswordInvalid, PasswordMissing,
 * PasswordNotDifferent, PasswordResetFailed. Because the enumeration has no explicit
 * values, that ordering would assign different ordinals to positions 1 through 5 and
 * would therefore be wrong on the wire. The measured source order above is
 * authoritative.
 *
 * No type is declared for it because no server-side peer exists that could serialise
 * one: the enumeration is not among the enumerations ported to the domain layer, and
 * the result type that carries expected failures does so generically, as a code and
 * a message. A failed password change therefore reaches this application as an
 * RFC 7807 problem document, which `problem-details.model.ts` already models, and a
 * client-side enumeration here would imply a closed outcome set the API never sends.
 */

/*
 * MIGRATION: one legacy account-status enumeration became three independent
 * booleans, and the difference is behavioural rather than cosmetic.
 *
 * `Library/Components/Users/Membership/UserValidStatus.vb:L23-L29` declares five
 * members with explicit values 0 through 4: valid is 0, password-expired is 1,
 * password-expiring is 2, profile-update-required is 3 and password-change-required
 * is 4. It is a SINGLE-VALUED field resolved by precedence, so it could report
 * exactly one condition at a time even when several held at once.
 *
 * The target reports the same information as separate advisory flags — a blocking
 * must-change-password signal that absorbs both the expired and the change-required
 * legacy members, a NON-blocking password-expiring signal, and a blocking
 * profile-update signal. Because the flags are independent, they can express
 * combinations the legacy field could not, such as an expired password on an account
 * that also owes a profile update. That is a real divergence from legacy behaviour
 * and is annotated rather than absorbed.
 *
 * `mustChangePassword` is spelled identically here, on the sign-in response modelled
 * by `auth.model.ts`, and on the two server-side contracts behind them. The
 * agreement is load-bearing: were the two names to drift, one screen would stop
 * learning that the account it just loaded must change its password. The
 * profile-update advisory is not modelled in this file because the account contract
 * does not carry it; the sign-in contract owns that signal.
 *
 * The legacy enumeration itself is NOT declared, for the same reason as the
 * enumeration noted above: nothing serialises it.
 */

/*
 * MIGRATION: two legacy declarations of the electronic-mail address, and two of the
 * user name, collapse to one member each.
 *
 * `Library/Components/Users/UserInfo.vb:L123` declared an address whose setter, at
 * `L127-L134`, assigned its own backing field and then WROTE THROUGH to the address
 * on the nested membership object at
 * `Library/Components/Users/Membership/UserMembership.vb:L344`. The two were never
 * independent, so `email` is declared exactly once below. The same collapse applies
 * to the user name, declared at `UserInfo.vb:L301` and again at
 * `UserMembership.vb:L356`, which is likewise declared once.
 *
 * The address is NOT an identifier and must never be used as a sign-in key: the
 * legacy provider was registered with `requiresUniqueEmail="false"`
 * (`release.config:L244`), so two accounts in one tenant may legitimately share one
 * address. Sign-in is by user name.
 *
 * The user name is additionally marked read-only at `UserInfo.vb:L301`, which is why
 * it appears on the creation contract and on the responses but NOT on the update
 * contract: the legacy application had no way to rename an account, and neither does
 * this one.
 */

/*
 * MIGRATION: `portalId` does not come from the account row.
 *
 * `UserInfo.vb:L219` exposed a tenant identifier as though it were an account
 * property, but `[dbo].[Users]` has no such column — its definition at
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L97-L111`
 * lists the key, the name parts, the address parts, the credential, the electronic
 * mail address and two dates, and nothing else. An account is global; its MEMBERSHIP
 * of a tenant lives on the separate `UserPortals` association, which carries the
 * account key, the tenant key and an authorisation flag.
 *
 * The member below therefore reports the tenant the account was READ THROUGH, which
 * is the tenant in the request path, and not a property intrinsic to the account.
 * Every endpoint behind these contracts is nested under a tenant for that reason.
 */

/*
 * MIGRATION: the profile collapsed from named fields to declared properties, which is
 * why no profile member appears on the account contracts below.
 *
 * `Library/Components/Users/Profile/UserProfile.vb` declared nineteen members, of
 * which seventeen were HARDCODED named fields — city, country, street, unit, postal
 * code, region, telephone, facsimile, mobile, instant-messaging handle, web address,
 * preferred locale, time zone and the rest. A tenant could declare its own profile
 * properties, but the class could not represent them, so anything a tenant added was
 * reachable only through a separate untyped collection.
 *
 * The target stores one row per declared property instead, keyed by the tenant's
 * declaration, and the API projects the profile as a collection of those rows with
 * each row's declaration embedded. That is what makes the profile screen able to
 * render a tenant's OWN properties: a screen cannot render dynamic properties from
 * fixed fields. The seventeen named fields are therefore NOT reproduced anywhere —
 * neither here nor in the sibling that owns the profile contract.
 *
 * Two smaller corrections travel with that change. The legacy definition class
 * carried a `PropertyValue` member at
 * `Library/Components/Users/Profile/ProfilePropertyDefinition.vb:L246`, conflating a
 * tenant's DECLARATION of a property with one account's VALUE for it; the two are
 * separate shapes now, and the declaration carries no value. And the legacy
 * visibility member at `L336` was typed as an enumeration that was not carried over,
 * so visibility travels as the plain integer the server sends; the codes the legacy
 * editor offered are published by `profile.model.ts`.
 *
 * The listing's postal-address and telephone members below are the one place a
 * profile value reaches an account contract, and they arrive already projected — the
 * legacy grid offered them as optional columns, so the listing preserves that rather
 * than forcing a second request to render a column.
 */

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeString,
  nullable,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

import type { PagedRequest, PagedResult } from './paged-result.model';

/**
 * One row of the account listing.
 *
 * Which columns a tenant actually displays is a per-tenant choice carried by
 * {@link MembershipSettings}; this shape is the union of what the listing can show,
 * so the settings decide presentation and the contract stays stable.
 */
export interface UserListItem {
  /**
   * The account's identifier.
   *
   * Compare against `null` or `undefined` rather than testing truthiness — see the
   * note on sentinels and identity values at the top of this file.
   */
  readonly userId: number;

  /** The tenant the account was read through, not a property of the account. */
  readonly portalId: number;

  /** The sign-in name, which is fixed for the life of the account. */
  readonly username: string;

  /** The given name. */
  readonly firstName: string;

  /** The family name. */
  readonly lastName: string;

  /**
   * The name shown in place of the name parts.
   *
   * Typed `string` rather than `string | null` because the column is declared
   * not-null with an empty-string default, so an absent display name arrives as the
   * empty string.
   */
  readonly displayName: string;

  /**
   * The postal address, when the tenant declares one and the account recorded it.
   *
   * Projected from the account's profile values rather than from the account row,
   * which is why it is nullable here while the name parts are not. It appears on the
   * listing because the legacy grid offered it as an optional column; the full
   * profile is fetched separately and is modelled by `profile.model.ts`.
   */
  readonly address: string | null;

  /** The telephone number, projected from profile values on the same terms. */
  readonly telephone: string | null;

  /**
   * The electronic mail address.
   *
   * Not unique and not a sign-in key — see the collapse note above.
   */
  readonly email: string;

  /** When the account was created, as an ISO 8601 instant, or null. */
  readonly createdDate: string | null;

  /** When the account last signed in, as an ISO 8601 instant, or null. */
  readonly lastLoginDate: string | null;

  /** Whether the account has been authorised to sign in. */
  readonly isApproved: boolean;

  /** Whether the account is currently considered active. */
  readonly isOnline: boolean;

  /** Whether the account administers the installation rather than one tenant. */
  readonly isSuperUser: boolean;

  /** Whether repeated failures have locked the account out. */
  readonly isLockedOut: boolean;
}

/**
 * One account in full, as returned when a single account is read.
 *
 * The legacy shape nested a membership object and a profile object inside the
 * account. Neither is reproduced: the membership members that survive are flattened
 * onto this shape, the ones that carried credentials are gone, and the profile is a
 * separate contract fetched from its own endpoint and modelled by
 * `profile.model.ts`.
 */
export interface UserDetail {
  /** The account's identifier. */
  readonly userId: number;

  /** The tenant the account was read through. */
  readonly portalId: number;

  /** The sign-in name, fixed for the life of the account. */
  readonly username: string;

  /** The given name. */
  readonly firstName: string;

  /** The family name. */
  readonly lastName: string;

  /** The name shown in place of the name parts; empty rather than null when unset. */
  readonly displayName: string;

  /** The electronic mail address, which is neither unique nor a sign-in key. */
  readonly email: string;

  /** Whether the account administers the installation rather than one tenant. */
  readonly isSuperUser: boolean;

  /**
   * The referring affiliate, when the account was recorded as arriving through one.
   *
   * Nullable because most accounts have no affiliate. The legacy field used -1 to
   * mean the same thing; that sentinel is translated to null at the boundary, so a
   * consumer tests for null and never for -1.
   */
  readonly affiliateId: number | null;

  /** Whether the account has been authorised to sign in. */
  readonly isApproved: boolean;

  /** Whether repeated failures have locked the account out. */
  readonly isLockedOut: boolean;

  /** Whether the account is currently considered active. */
  readonly isOnline: boolean;

  /**
   * Whether the account must change its password before it can proceed.
   *
   * Blocking. Spelled identically on the sign-in response so that both paths report
   * the same signal by the same name — see the advisory-flags note above. Declared
   * as a plain boolean rather than as a nullable or optional one, deliberately: the
   * legacy absent-marker for a boolean WAS false, so an unknown state and a negative
   * state were indistinguishable, and admitting a third state here would invent a
   * distinction the data cannot support.
   */
  readonly mustChangePassword: boolean;

  /**
   * When the account was created, as an ISO 8601 instant, or null.
   *
   * Read-only, as it was in the legacy source. It never appears on a write contract.
   */
  readonly createdDate: string | null;

  /** When the account last signed in. Read-only; never written by a client. */
  readonly lastLoginDate: string | null;

  /** When the account was last active. Read-only; never written by a client. */
  readonly lastActivityDate: string | null;

  /** When the account was last locked out. Read-only; never written by a client. */
  readonly lastLockoutDate: string | null;

  /**
   * When the account's password was last changed. Read-only; never written by a
   * client.
   *
   * There is no corresponding modified-at member on this contract, and none is
   * invented: the legacy account row records a creation date and these four event
   * dates, and nothing that tracks a last-modified instant or the identity that
   * performed a modification.
   */
  readonly lastPasswordChangeDate: string | null;

  /**
   * The names of the roles this account holds in the tenant it was read through.
   *
   * Always an array and never null — the server projects an empty array when the
   * account holds no role, matching the legacy field, which hydrated itself on first
   * read and returned an array either way.
   *
   * These are the roles of the account BEING ADMINISTERED, which is a different
   * subject from the roles of the signed-in caller. The caller's own roles, and the
   * caller's permissions, belong to the current-identity shape in `auth.model.ts`
   * and are not duplicated here. This contract carries no permission list at all,
   * because the server does not send one: authorisation is decided by the API.
   */
  readonly roles: readonly string[];
}

/**
 * The contract for creating an account.
 *
 * MIGRATION: the legacy screen at `Website/admin/Users/User.ascx.vb` also collected a
 * password question and answer. Both are gone, because the legacy provider did not
 * require them (`release.config:L241`) and because they existed to serve password
 * retrieval, which is abolished.
 *
 * The new password travels on this shape and on {@link ChangePasswordRequest} only.
 * It is write-only in the strict sense: no response contract in this file, or in any
 * sibling, carries a password, a hash or a salt.
 */
export interface CreateUserRequest {
  /**
   * The sign-in name.
   *
   * Fixed once created — there is no rename path, here or in the legacy application.
   */
  readonly username: string;

  /** The given name. */
  readonly firstName: string;

  /** The family name. */
  readonly lastName: string;

  /** The name to show in place of the name parts. */
  readonly displayName: string;

  /** The electronic mail address. Not required to be unique within the tenant. */
  readonly email: string;

  /**
   * The password to set.
   *
   * The server enforces the policy; this contract states no length or complexity
   * bound of its own, and in particular does not carry the abandoned store's
   * 20-character ceiling. Hashed with BCrypt on arrival and never returned.
   */
  readonly password: string;

  /**
   * The password repeated, so the server can reject a typing error.
   *
   * Checked server-side as well as in the form, because a client-side check alone is
   * not a check.
   */
  readonly confirmPassword: string;

  /**
   * Whether the new account is authorised to sign in immediately.
   *
   * False leaves the account pending an administrator's approval, which is how the
   * legacy tenant-level registration setting expressed a moderated sign-up.
   */
  readonly authorize: boolean;
}

/**
 * The contract for updating an account's own details.
 *
 * Deliberately narrow. It carries no user name, because the legacy source marked that
 * field read-only and offered no rename. It carries no credential, no approval flag
 * and no lockout flag, because each of those is changed through its own endpoint
 * rather than by a general update — which keeps a routine profile edit from silently
 * carrying an authorisation change. It carries none of the event dates, because all
 * of them are read-only facts recorded by the server.
 */
export interface UpdateUserRequest {
  /** The given name. */
  readonly firstName: string;

  /** The family name. */
  readonly lastName: string;

  /** The name to show in place of the name parts. */
  readonly displayName: string;

  /** The electronic mail address. */
  readonly email: string;
}

/**
 * Which of the two credential operations a {@link ChangePasswordRequest} performs.
 *
 * The two values are the closed set the server publishes as constants on its own
 * contract, so they are narrowed to a union here rather than left as an open string:
 * a caller that cannot spell a third value cannot provoke a rejection.
 *
 * A change is performed BY the account holder and requires the current password. A
 * reset is performed FOR the account by an administrator and does not. Reset survives
 * the migration; retrieval does not — the legacy provider enabled the two
 * independently (`release.config:L239-L240`) and only retrieval required a reversible
 * store.
 */
export type ChangePasswordOperation = 'change' | 'reset';

/**
 * The contract for changing or resetting a password.
 *
 * Every member is nullable because which of them is required depends on the
 * operation, and the server decides that: a change requires the current password, a
 * reset does not. Reporting a missing member as a validation failure server-side
 * keeps one authority over the rule instead of two that can disagree.
 *
 * Nothing on this shape is ever echoed back. The response to a successful call
 * carries no body beyond its status.
 */
export interface ChangePasswordRequest {
  /** Which operation to perform. */
  readonly operation: ChangePasswordOperation | null;

  /**
   * The password in force, proving the caller is the account holder.
   *
   * Required for a change, ignored for an administrative reset.
   */
  readonly currentPassword: string | null;

  /** The password to set. */
  readonly newPassword: string | null;

  /** The new password repeated, so a typing error is rejected rather than stored. */
  readonly confirmPassword: string | null;
}

/**
 * A tenant's account-administration settings.
 *
 * MIGRATION: the legacy application returned these as an untyped hash table from
 * `Library/Components/Users/UserController.vb:L656`, so every caller had to know both
 * the key spelling and the value type, and a mistake in either failed at run time.
 * They are a typed contract here, and deliberately not an index-signature bag.
 *
 * The password POLICY is not part of this shape. Minimum length, the
 * non-alphanumeric requirement and the address-uniqueness rule are server-side
 * options that never cross the boundary, and restating their values here would create
 * a second copy free to drift from the one that is actually enforced. For context
 * only: as shipped, the legacy provider set a minimum length of 7, required 0
 * non-alphanumeric characters and did not require a unique address
 * (`release.config:L242-L244`), which means the only rule with any effect was the
 * length — the non-alphanumeric rule was vacuous, and no strength expression was
 * ever configured.
 */
export interface MembershipSettings {
  /** Whether the listing shows the given-name column. */
  readonly columnFirstName: boolean;

  /** Whether the listing shows the family-name column. */
  readonly columnLastName: boolean;

  /** Whether the listing shows the display-name column. */
  readonly columnDisplayName: boolean;

  /** Whether the listing shows the postal-address column. */
  readonly columnAddress: boolean;

  /** Whether the listing shows the telephone column. */
  readonly columnTelephone: boolean;

  /** Whether the listing shows the electronic-mail column. */
  readonly columnEmail: boolean;

  /** Whether the listing shows the creation-date column. */
  readonly columnCreatedDate: boolean;

  /** Whether the listing shows the last-sign-in column. */
  readonly columnLastLogin: boolean;

  /** Whether the listing shows the authorisation column. */
  readonly columnAuthorized: boolean;

  /**
   * How the listing presents accounts, as the plain integer the server sends.
   *
   * Not narrowed to a union because the server does not narrow it either; a client
   * enumeration would imply a closed set the API does not validate against.
   */
  readonly displayMode: number;

  /** Whether the listing hides its pager. */
  readonly displaySuppressPager: boolean;

  /** How many accounts one page of the listing holds. */
  readonly recordsPerPage: number;

  /**
   * The visibility applied to a profile value the account has not set explicitly.
   *
   * The same integer vocabulary the profile contract uses; see `profile.model.ts`,
   * which publishes the three codes the legacy editor offered.
   */
  readonly profileDefaultVisibility: number;

  /** Whether the profile editor lets an account choose each value's visibility. */
  readonly profileDisplayVisibility: boolean;

  /** Whether the account may manage its own subscribed services. */
  readonly profileManageServices: boolean;

  /** The page to land on after signing in, or null to use the default. */
  readonly redirectAfterLogin: number | null;

  /** The page to land on after registering, or null to use the default. */
  readonly redirectAfterRegistration: number | null;

  /** The page to land on after signing out, or null to use the default. */
  readonly redirectAfterLogout: number | null;

  /**
   * The expression an electronic-mail address must match.
   *
   * Applied as an additional client-side pattern, never instead of the server's own
   * check. The server supplies its default when the tenant has configured none, so
   * the member is a plain string rather than a nullable one.
   */
  readonly securityEmailValidation: string;

  /** Whether a complete profile is required of an account. */
  readonly securityRequireValidProfile: boolean;

  /** Whether a complete profile is required before an account may sign in. */
  readonly securityRequireValidProfileAtLogin: boolean;

  /**
   * Which account-selection control the tenant presents, as a plain integer.
   *
   * Left as the server sends it, for the same reason as the display mode above.
   */
  readonly securityUsersControl: number;

  /** The template that composes a display name from an account's parts. */
  readonly securityDisplayNameFormat: string;
}

/**
 * The fields the account listing may be ordered by.
 *
 * This is the exact set the server validates against, narrowed to a union so that an
 * unsupported field name is a compilation error rather than a rejected request. The
 * server compares case-insensitively, so the casing below is a convention rather than
 * a requirement — but keeping it identical to the server's own spelling keeps the two
 * lists diffable by eye.
 *
 * A field is absent from this set when the listing cannot order by it, which is the
 * case for every projected profile value and for every event date.
 */
export type UserSortField =
  | 'UserId'
  | 'Username'
  | 'FirstName'
  | 'LastName'
  | 'DisplayName'
  | 'Email'
  | 'IsSuperUser';

/**
 * The query behind one page of the account listing.
 *
 * MIGRATION: the legacy listing reported its total through an argument passed by
 * reference — `GetUsers(portalId, ..., pageIndex, pageSize, ByRef totalRecords)` — so
 * the count arrived through a side effect on a caller's variable. The total now
 * travels inside the paged envelope alongside the rows, which is what
 * {@link PagedUserList} carries.
 *
 * MIGRATION: the filters below match with a PREFIX, not a substring. The legacy
 * screen appended a single trailing wildcard to whatever was typed
 * (`Website/admin/Users/Users.ascx.vb:L269`, `L271` and `L274` each pass the search
 * text followed by `%`), making every search a starts-with. The server reproduces
 * that, INCLUDING the trailing wildcard, so a caller passes the bare text: appending
 * a wildcard here would produce a doubled pattern, and leading with one would change
 * a starts-with into a contains and quietly diverge from legacy behaviour.
 */
export interface UserListQuery extends PagedRequest {
  /**
   * Order by this field.
   *
   * Narrows the inherited member to the set the account listing actually supports.
   */
  readonly sortBy?: UserSortField;

  /** Match accounts whose sign-in name starts with this text. */
  readonly userName?: string;

  /** Match accounts whose electronic-mail address starts with this text. */
  readonly email?: string;

  /**
   * Match on a profile property rather than on an account field.
   *
   * Names the property to match; supply {@link UserListQuery.profilePropertyValue}
   * alongside it. This is the third search axis the legacy screen offered, beside the
   * name and the address.
   */
  readonly profilePropertyName?: string;

  /** The text the named profile property must start with. */
  readonly profilePropertyValue?: string;

  /**
   * Restrict to authorised or to unauthorised accounts.
   *
   * Omit to return both. Nullable rather than defaulted so that "either" stays
   * distinct from "authorised", which a plain boolean could not express.
   */
  readonly isApproved?: boolean;
}

/**
 * One page of the account listing: the rows plus the envelope carrying the total.
 */
export type PagedUserList = PagedResult<UserListItem>;

/**
 * The outcome vocabulary of the legacy account-creation routine.
 *
 * NON-WIRE REFERENCE. No contract in the application layer's `Dtos` folder carries
 * this enumeration in either direction — creation reports its outcome as an HTTP
 * status with an RFC 7807 problem document, which `problem-details.model.ts` models.
 * It is published here as the vocabulary a reader needs in order to interpret the
 * legacy source, and because its ordinals are persisted integers rather than names.
 * That is also why it is a numeric enumeration with every ordinal written out: the
 * project compiles with isolated modules, which forbids the compile-time-inlined
 * variety of enumeration outright, and relying on implicit declaration order would
 * put the values one edit away from silently shifting.
 *
 * MIGRATION: read the values carefully, because two of them are counter-intuitive.
 *
 * {@link UserCreateStatus.Success} is 13, NOT 0. The zero member is
 * {@link UserCreateStatus.AddUser}, which is not an outcome at all but the initial
 * "no error recorded yet" state — proven by `Website/admin/Users/User.ascx.vb:L185`,
 * which treats any value OTHER than that member as a failure. Assuming a zero success
 * value here would invert the test. Nothing in this folder should assume one anywhere:
 * the sign-in outcome enumeration succeeds at 1, and this one at 13.
 *
 * {@link UserCreateStatus.AddUserToPortal} is likewise an operation marker rather
 * than an error.
 *
 * Three members describe distinct name failures — {@link
 * UserCreateStatus.UsernameAlreadyExists}, {@link UserCreateStatus.DuplicateUserName}
 * and {@link UserCreateStatus.InvalidUserName}. They look redundant and are not:
 * merging or renaming any of them would change the integers the legacy data records.
 */
export enum UserCreateStatus {
  /** No error recorded yet. The initial state, and not a success. */
  AddUser = 0,

  /** The name is taken within the tenant. */
  UsernameAlreadyExists = 1,

  /** The account already holds membership of this tenant. */
  UserAlreadyRegistered = 2,

  /** The electronic-mail address is already recorded against another account. */
  DuplicateEmail = 3,

  /** The provider's own key for the account is already in use. */
  DuplicateProviderUserKey = 4,

  /** The name is already in use, as reported by the membership provider. */
  DuplicateUserName = 5,

  /** The supplied password answer did not match. */
  InvalidAnswer = 6,

  /** The electronic-mail address is malformed. */
  InvalidEmail = 7,

  /** The password did not satisfy the configured policy. */
  InvalidPassword = 8,

  /** The provider's own key for the account is malformed. */
  InvalidProviderUserKey = 9,

  /** The supplied password question is malformed. */
  InvalidQuestion = 10,

  /** The name is malformed. */
  InvalidUserName = 11,

  /** The membership provider failed for a reason it did not classify. */
  ProviderError = 12,

  /** The account was created. Note the value: success is 13. */
  Success = 13,

  /** Creation failed for a reason the legacy code could not classify. */
  UnexpectedError = 14,

  /** Creation was refused by a rule outside the membership provider. */
  UserRejected = 15,

  /** The password and its confirmation did not match. */
  PasswordMismatch = 16,

  /** Grant the created account membership of the tenant. An operation, not an error. */
  AddUserToPortal = 17,
}

/**
 * How a stored password is represented.
 *
 * NON-WIRE REFERENCE. No contract in the application layer's `Dtos` folder carries
 * this enumeration; it survives only where a legacy credential record is read, and it
 * is published here so a reader can interpret the value that record holds. A numeric
 * enumeration with explicit ordinals, because the stored value is an integer.
 *
 * MIGRATION: the legacy installation ran with the reversible representation — its
 * provider was registered `passwordFormat="Encrypted"` (`release.config:L245`) with
 * retrieval enabled and a symmetric key committed to source control. The target does
 * not use any member of this enumeration for new credentials: it hashes with BCrypt,
 * one-way. {@link PasswordFormat.Encrypted} is retained for fidelity when reading a
 * legacy record and must never be selected as a setting for a new one. Neither must
 * {@link PasswordFormat.Clear}, which stored the password verbatim.
 */
export enum PasswordFormat {
  /** Stored verbatim. Never to be used. */
  Clear = 0,

  /** Stored as a one-way digest. */
  Hashed = 1,

  /** Stored reversibly under a symmetric key. Never to be used for a new credential. */
  Encrypted = 2,
}

/**
 * Decodes one listed account row.
 *
 * `username`, `email` and the three name members are non-nullable, and the distinction from
 * the two nullable ones matters. An account always has a login name and an address; its
 * postal address and telephone are profile values projected onto the listing because the
 * legacy grid offered them as optional columns, and either may genuinely be absent.
 *
 * ⚠ THE NAME MEMBERS ARE DECODED AS PLAIN STRINGS, SO THE EMPTY STRING PASSES. That is the
 * legacy spelling of an absent string — `Library/Components/Shared/Null.vb:L71-L75` returns
 * `""` literally — so an operator who never supplied a first name has one that is empty, not
 * missing, and refusing it here would refuse a conforming account.
 */
export const decodeUserListItem: Decoder<UserListItem> = objectOf<UserListItem>({
  userId: decodeInteger,
  portalId: decodeInteger,
  username: decodeString,
  firstName: decodeString,
  lastName: decodeString,
  displayName: decodeString,
  address: nullable(decodeString),
  telephone: nullable(decodeString),
  email: decodeString,
  createdDate: nullable(decodeDateString),
  lastLoginDate: nullable(decodeDateString),
  isApproved: decodeBoolean,
  isOnline: decodeBoolean,
  isSuperUser: decodeBoolean,
  isLockedOut: decodeBoolean,
});

/**
 * Decodes one account in full.
 *
 * Every one of the five audit instants is nullable, and each is nullable for its own real
 * reason: an account that has never signed in has no last-login instant, one that has never
 * been locked out has no lockout instant, and one whose password predates the migration has
 * no recorded change. None is coerced to an epoch — a date this client invented would be
 * rendered as fact.
 *
 * `roles` is required and is decoded as an array of plain strings. An account with no roles
 * has an EMPTY array, not an absent member, so an absence is contract drift rather than an
 * unroled account and is refused as such.
 */
export const decodeUserDetail: Decoder<UserDetail> = objectOf<UserDetail>({
  userId: decodeInteger,
  portalId: decodeInteger,
  username: decodeString,
  firstName: decodeString,
  lastName: decodeString,
  displayName: decodeString,
  email: decodeString,
  isSuperUser: decodeBoolean,
  affiliateId: nullable(decodeInteger),
  isApproved: decodeBoolean,
  isLockedOut: decodeBoolean,
  isOnline: decodeBoolean,
  mustChangePassword: decodeBoolean,
  createdDate: nullable(decodeDateString),
  lastLoginDate: nullable(decodeDateString),
  lastActivityDate: nullable(decodeDateString),
  lastLockoutDate: nullable(decodeDateString),
  lastPasswordChangeDate: nullable(decodeDateString),
  roles: arrayOf(decodeString),
});

/**
 * Decodes the portal-wide membership settings.
 *
 * ⚠ THE THREE REDIRECT MEMBERS ARE NULLABLE PAGE IDENTIFIERS AND ARE DECODED AS INTEGERS
 * WITH NO POSITIVITY TEST. Zero is an ordinary page here — the schema seeds `Tabs.TabID` at
 * zero — so `null` is the only expression of "no redirect", and a guard on the value being
 * positive would silently discard a redirect to the first page ever created.
 *
 * `displayMode`, `securityUsersControl` and `profileDefaultVisibility` are integer
 * discriminators that the legacy screens offered as fixed choices, but they are decoded as
 * plain integers rather than closed code tables: the server publishes no closed set for
 * them, and inventing one here would refuse a value a later server release adds.
 */
export const decodeMembershipSettings: Decoder<MembershipSettings> =
  objectOf<MembershipSettings>({
    columnFirstName: decodeBoolean,
    columnLastName: decodeBoolean,
    columnDisplayName: decodeBoolean,
    columnAddress: decodeBoolean,
    columnTelephone: decodeBoolean,
    columnEmail: decodeBoolean,
    columnCreatedDate: decodeBoolean,
    columnLastLogin: decodeBoolean,
    columnAuthorized: decodeBoolean,
    displayMode: decodeInteger,
    displaySuppressPager: decodeBoolean,
    recordsPerPage: decodeInteger,
    profileDefaultVisibility: decodeInteger,
    profileDisplayVisibility: decodeBoolean,
    profileManageServices: decodeBoolean,
    redirectAfterLogin: nullable(decodeInteger),
    redirectAfterRegistration: nullable(decodeInteger),
    redirectAfterLogout: nullable(decodeInteger),
    securityEmailValidation: decodeString,
    securityRequireValidProfile: decodeBoolean,
    securityRequireValidProfileAtLogin: decodeBoolean,
    securityUsersControl: decodeInteger,
    securityDisplayNameFormat: decodeString,
  });
