/**
 * The account-administration wire contract: users, credential changes and the per-tenant membership
 * settings that govern them. Every declaration here mirrors, member for member, a data-transfer object
 * the API already serialises, so that a change on either side of the boundary surfaces as a compilation
 * failure rather than as an `undefined` at run time.
 */

// The legacy application stored passwords REVERSIBLY. Its membership provider was registered with
// `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`, and the symmetric key that decrypted
// every stored password was committed to source control in the same file at `L89-L93`.

// The target reports the same information as separate advisory flags — a blocking must-change-password
// signal that absorbs both the expired and the change-required legacy members, a NON-blocking
// password-expiring signal, and a blocking profile-update signal.

// The target stores one row per declared property instead, keyed by the tenant's declaration, and the API
// projects the profile as a collection of those rows with each row's declaration embedded.

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeNumber,
  decodeString,
  nullable,
  objectOf,
  oneOf,
  type Decoder,
} from '../utils/decode.util';

import { decodeStoredFrequency } from './role.model';

import type { PagedRequest, PagedResult } from './paged-result.model';
import type { StoredBillingFrequency } from './role.model';

/**
 * One row of the account listing. Which columns a tenant actually displays is a per-tenant choice carried
 * by {@link MembershipSettings}; this shape is the union of what the listing can show, so the settings
 * decide presentation and the contract stays stable.
 */
export interface UserListItem {
  /**
   * The account's identifier. Compare against `null` or `undefined` rather than testing truthiness — see
   * the note on sentinels and identity values at the top of this file.
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
   * The name shown in place of the name parts. Typed `string` rather than `string | null` because the
   * column is declared not-null with an empty-string default, so an absent display name arrives as the
   * empty string.
   */
  readonly displayName: string;

  /**
   * The postal address, when the tenant declares one and the account recorded it. Projected from the
   * account's profile values rather than from the account row, which is why it is nullable here while the
   * name parts are not.
   */
  readonly address: string | null;

  /** The telephone number, projected from profile values on the same terms. */
  readonly telephone: string | null;

  /** The electronic mail address. Not unique and not a sign-in key — see the collapse note above. */
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

  /**
   * Whether the removal operation will accept this account. ADVISORY, FOR RENDERING ONLY. The server
   * re-checks on the request itself and refuses with a `403` regardless of what this flag said, so a
   * client that ignored it would be safe but would offer commands that cannot succeed.
   */
  readonly canDelete: boolean;
}

/** One account in full, as returned when a single account is read. */
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
   * The referring affiliate, when the account was recorded as arriving through one. Nullable because most
   * accounts have no affiliate.
   */
  readonly affiliateId: number | null;

  /** Whether the account has been authorised to sign in. */
  readonly isApproved: boolean;

  /** Whether repeated failures have locked the account out. */
  readonly isLockedOut: boolean;

  /** Whether the account is currently considered active. */
  readonly isOnline: boolean;

  readonly canDelete: boolean;

  /** Whether the account must change its password before it can proceed. Blocking. */
  readonly mustChangePassword: boolean;

  /** When the account was created, as an ISO 8601 instant, or null. */
  readonly createdDate: string | null;

  /** When the account last signed in. Read-only; never written by a client. */
  readonly lastLoginDate: string | null;

  /** When the account was last active. Read-only; never written by a client. */
  readonly lastActivityDate: string | null;

  /** When the account was last locked out. Read-only; never written by a client. */
  readonly lastLockoutDate: string | null;

  /** When the account's password was last changed. Read-only; never written by a client. */
  readonly lastPasswordChangeDate: string | null;

  /**
   * The names of the roles this account holds in the tenant it was read through. Always an array and
   * never null — the server projects an empty array when the account holds no role, matching the legacy
   * field, which hydrated itself on first read and returned an array either way.
   */
  readonly roles: readonly string[];
}

/**
 * The contract for creating an account. the legacy screen at `Website/admin/Users/User.ascx.vb` also
 * collected a password question and answer.
 */
export interface CreateUserRequest {
  /** The sign-in name. */
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
   * The password to set. The server enforces the policy; this contract states no length or complexity
   * bound of its own, and in particular does not carry the abandoned store's 20-character ceiling.
   */
  readonly password: string;

  /** The password repeated, so the server can reject a typing error. */
  readonly confirmPassword: string;

  /**
   * Whether the new account is authorised to sign in immediately. False leaves the account pending an
   * administrator's approval, which is how the legacy tenant-level registration setting expressed a
   * moderated sign-up.
   */
  readonly authorize: boolean;
}

/** The contract for updating an account's own details. Deliberately narrow. */
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
 * Which of the two credential operations a {@link ChangePasswordRequest} performs. The two values are the
 * closed set the server publishes as constants on its own contract, so they are narrowed to a union here
 * rather than left as an open string: a caller that cannot spell a third value cannot provoke a
 * rejection.
 */
export type ChangePasswordOperation = 'change' | 'reset';

/** The contract for changing or resetting a password. */
export interface ChangePasswordRequest {
  /** Which operation to perform. */
  readonly operation: ChangePasswordOperation | null;

  /** The password in force, proving the caller is the account holder. */
  readonly currentPassword: string | null;

  /** The password to set. */
  readonly newPassword: string | null;

  /** The new password repeated, so a typing error is rejected rather than stored. */
  readonly confirmPassword: string | null;
}

/**
 * A tenant's account-administration settings. the legacy application returned these as an untyped hash
 * table from `Library/Components/Users/UserController.vb:L656`, so every caller had to know both the key
 * spelling and the value type, and a mistake in either failed at run time.
 */
export interface MembershipSettings {
  readonly isStored: boolean;

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

  /** How the listing presents accounts, as the plain integer the server sends. */
  readonly displayMode: number;

  /** Whether the listing hides its pager. */
  readonly displaySuppressPager: boolean;

  /** How many accounts one page of the listing holds. */
  readonly recordsPerPage: number;

  /** The visibility applied to a profile value the account has not set explicitly. */
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
   * The expression an electronic-mail address must match. Applied as an additional client-side pattern,
   * never instead of the server's own check.
   */
  readonly securityEmailValidation: string;

  /** Whether a complete profile is required of an account. */
  readonly securityRequireValidProfile: boolean;

  /** Whether a complete profile is required before an account may sign in. */
  readonly securityRequireValidProfileAtLogin: boolean;

  /**
   * Which account-selection control the tenant presents, as a plain integer. Left as the server sends it,
   * for the same reason as the display mode above.
   */
  readonly securityUsersControl: number;

  /** The template that composes a display name from an account's parts. */
  readonly securityDisplayNameFormat: string;
}

/**
 * What a policy write actually did, beyond storing the values it was given. Adopting a new display-name
 * format has a TENANT-WIDE side effect the caller cannot predict from its own request: every account's
 * stored display name is recomposed from the new format.
 */
export interface MembershipSettingsUpdateResult {
  /** Whether the submitted display-name format differed from the one already stored. */
  readonly displayNameFormatChanged: boolean;

  /**
   * How many accounts had their stored display name rewritten. Counts names that CHANGED, not accounts
   * examined — so a tenant whose accounts already read the way the new format composes them reports zero
   * rather than its whole size.
   */
  readonly displayNamesRewritten: number;
}

/**
 * The fields the account listing may be ordered by. This is the exact set the server validates against,
 * narrowed to a union so that an unsupported field name is a compilation error rather than a rejected
 * request.
 */
export type UserSortField =
  | 'UserId'
  | 'Username'
  | 'FirstName'
  | 'LastName'
  | 'DisplayName'
  | 'Email'
  | 'IsSuperUser';

export interface UserListQuery extends PagedRequest {
  /** Order by this field. Narrows the inherited member to the set the account listing actually supports. */
  readonly sortBy?: UserSortField;

  /** Match accounts whose sign-in name starts with this text. */
  readonly userName?: string;

  /** Match accounts whose electronic-mail address starts with this text. */
  readonly email?: string;

  readonly profilePropertyName?: string;

  /** The text the named profile property must start with. */
  readonly profilePropertyValue?: string;

  /** Restrict to authorised or to unauthorised accounts. Omit to return both. */
  readonly isApproved?: boolean;
}

/** One page of the account listing: the rows plus the envelope carrying the total. */
export type PagedUserList = PagedResult<UserListItem>;

/**
 * One selectable account, as an account PICKER needs it. ⚠ THIS IS DELIBERATELY NARROWER THAN {@link
 * UserListItem} AND MUST STAY THAT WAY. A performance and privacy review measured the role-assignment
 * screen filling its account drop-down — and its account-count probe — from the account LISTING, whose
 * row carries a postal address, a telephone number, an electronic-mail address, a creation instant, a
 * last-login instant and four status flags.
 */
export interface UserChoice {
  /**
   * The account's identifier, and the value an option submits. Compare against `null` or `undefined`
   * rather than testing truthiness — see the note on sentinels and identity values at the top of this
   * file.
   */
  readonly userId: number;

  /** The sign-in name, shown in brackets so two identical display names stay distinguishable. */
  readonly username: string;

  /**
   * The name the option is captioned with. Typed `string` rather than `string | null` because the column
   * is declared not-null with an empty-string default, so an account that never recorded one arrives with
   * the EMPTY STRING. That is a conforming value and not an absence: a caller captions such an option
   * with {@link UserChoice.username} rather than treating the row as unusable.
   */
  readonly displayName: string;
}

/**
 * One page of the account picker: the choices plus the envelope carrying the total. The total is what
 * makes a single-row request a COUNT PROBE — the cheapest way to ask how many accounts a tenant holds,
 * which is the question the legacy control's own threshold rule asked before it decided whether to offer
 * a drop-down at all.
 */
export type PagedUserChoiceList = PagedResult<UserChoice>;

export const MEMBER_SERVICE_ACTIONS = ['Subscribe', 'Unsubscribe', 'Renew'] as const;

/** The one subscription command a catalogue row offers. */
export type MemberServiceAction = (typeof MEMBER_SERVICE_ACTIONS)[number];

/**
 * One row of the account's member-services catalogue: a public role of the tenant, together with whatever
 * the account already holds against it. Replaces the seven-column `grdServices` data grid of
 * `Website/admin/Users/MemberServices.ascx`.
 */
export interface MemberService {
  /** The role this row offers. Zero is a real role — see the sentinel note above. */
  readonly roleId: number;

  /** The role's name, as the legacy `Name` column rendered it. */
  readonly roleName: string;

  /** The role's description, or `null` when none is recorded. */
  readonly description: string | null;

  /** The recurring fee, or `null` when none is recorded. */
  readonly serviceFee: number | null;

  /** How many {@link billingFrequency} units one billing cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The billing frequency code, or `null`. */
  readonly billingFrequency: StoredBillingFrequency | null;

  /** The trial fee, or `null` when none is recorded. */
  readonly trialFee: number | null;

  /** How many {@link trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** The trial frequency code, or `null`. */
  readonly trialFrequency: StoredBillingFrequency | null;

  /**
   * When the account's assignment takes effect, or `null` — either because the account holds no
   * assignment or because the assignment carries no start date, which is the ordinary case for one
   * created by a subscription.
   */
  readonly effectiveDate: string | null;

  /**
   * When the account's assignment lapses, or `null` for an assignment with no expiry. ⚠ `null` HERE IS
   * "NEVER EXPIRES", AND IS NOT INTERCHANGEABLE WITH AN EXPIRY IN THE PAST. {@link isExpired} carries the
   * lapsed test, which the server performs against its own clock.
   */
  readonly expiryDate: string | null;

  /** Whether the account holds an assignment to this role at all. */
  readonly isSubscribed: boolean;

  /**
   * Whether the assignment records the trial as already consumed. ⚠ NEVER SET BY THIS APPLICATION.
   * Neither terminal write procedure wrote the column and no in-scope legacy code assigned it, so it
   * reads as `false` for every assignment this application creates. It is honoured on the read exactly as
   * `ShowTrial` honoured it, so a row set out of band still suppresses the trial.
   */
  readonly isTrialUsed: boolean;

  /** Whether the account holds this service AND its expiry has already passed. */
  readonly isExpired: boolean;

  /** The one command this row offers, whether or not {@link subscriptionOffered} allows it. */
  readonly subscriptionAction: MemberServiceAction;

  /** Whether the subscription command is offered for this row. */
  readonly subscriptionOffered: boolean;

  readonly subscriptionRequiresPayment: boolean;

  /** Whether the trial command is offered for this row. */
  readonly trialOffered: boolean;
}

export interface RedeemServiceCodeRequest {
  /**
   * The code as typed. ⚠ SENT UNTRIMMED AND UNFOLDED. The legacy comparison was an ordinary VB string
   * equality against the stored `RSVPCode`, so leading space and case both mattered; trimming here would
   * admit codes the legacy application refused and would make this client's behaviour depend on which
   * screen a code was typed into.
   */
  readonly code: string;
}

/** One role an invitation code admitted the account to. */
export interface RedeemedService {
  /** The role joined. */
  readonly roleId: number;

  /** The role's name, for reporting what the code did. */
  readonly roleName: string;
}

export interface RedeemServiceCodeResult {
  /** The roles the code admitted the account to. */
  readonly roles: readonly RedeemedService[];
}

/**
 * The outcome vocabulary of the legacy account-creation routine. NON-WIRE REFERENCE. No contract in the
 * application layer's `Dtos` folder carries this enumeration in either direction — creation reports its
 * outcome as an HTTP status with an RFC 7807 problem document, which `problem-details.model.ts` models.
 */
export enum UserCreateStatus {
  /** No error recorded yet. */
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

  /** The account was created. */
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
 * How a stored password is represented. NON-WIRE REFERENCE. No contract in the application layer's `Dtos`
 * folder carries this enumeration; it survives only where a legacy credential record is read, and it is
 * published here so a reader can interpret the value that record holds.
 */
export enum PasswordFormat {
  /** Stored verbatim. */
  Clear = 0,

  /** Stored as a one-way digest. */
  Hashed = 1,

  /** Stored reversibly under a symmetric key. Never to be used for a new credential. */
  Encrypted = 2,
}

export const decodeUserChoice: Decoder<UserChoice> = objectOf<UserChoice>({
  userId: decodeInteger,
  username: decodeString,
  displayName: decodeString,
});

/**
 * Decodes one listed account row. `username`, `email` and the three name members are non-nullable, and
 * the distinction from the two nullable ones matters.
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
  canDelete: decodeBoolean,
});

/**
 * Decodes one account in full. Every one of the five audit instants is nullable, and each is nullable for
 * its own real reason: an account that has never signed in has no last-login instant, one that has never
 * been locked out has no lockout instant, and one whose password predates the migration has no recorded
 * change.
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
  // Decoded exactly as the list projection decodes it: one capability, one spelling, one rule.
  canDelete: decodeBoolean,
});

/**
 * Decodes the portal-wide membership settings. ⚠ THE THREE REDIRECT MEMBERS ARE NULLABLE PAGE IDENTIFIERS
 * AND ARE DECODED AS INTEGERS WITH NO POSITIVITY TEST. Zero is an ordinary page here — the schema seeds
 * `Tabs.TabID` at zero — so `null` is the only expression of "no redirect", and a guard on the value
 * being positive would silently discard a redirect to the first page ever created.
 */
export const decodeMembershipSettings: Decoder<MembershipSettings> =
  objectOf<MembershipSettings>({
    // ⚠ #5/#6 — REQUIRED, not tolerated as absent.
    isStored: decodeBoolean,
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

/** Decodes the report a policy write answers with. Both members are REQUIRED rather than optional. */
export const decodeMembershipSettingsUpdateResult: Decoder<MembershipSettingsUpdateResult> =
  objectOf<MembershipSettingsUpdateResult>({
    displayNameFormatChanged: decodeBoolean,
    displayNamesRewritten: decodeInteger,
  });

/**
 * Decodes one row of the account's member-services catalogue. The nine role-term members are nullable and
 * the six flags are not, and the split is the contract's own: a role may record no fee, no period and no
 * frequency, but the server always decides each of the four presentation questions and always names one
 * command.
 */
export const decodeMemberService: Decoder<MemberService> = objectOf<MemberService>({
  roleId: decodeInteger,
  roleName: decodeString,
  description: nullable(decodeString),
  serviceFee: nullable(decodeNumber),
  billingPeriod: nullable(decodeInteger),
  billingFrequency: nullable(decodeStoredFrequency),
  trialFee: nullable(decodeNumber),
  trialPeriod: nullable(decodeInteger),
  trialFrequency: nullable(decodeStoredFrequency),
  effectiveDate: nullable(decodeDateString),
  expiryDate: nullable(decodeDateString),
  isSubscribed: decodeBoolean,
  isTrialUsed: decodeBoolean,
  isExpired: decodeBoolean,
  subscriptionAction: oneOf(MEMBER_SERVICE_ACTIONS),
  subscriptionOffered: decodeBoolean,
  subscriptionRequiresPayment: decodeBoolean,
  trialOffered: decodeBoolean,
});

/** Decodes one role an invitation code admitted the account to. */
export const decodeRedeemedService: Decoder<RedeemedService> = objectOf<RedeemedService>({
  roleId: decodeInteger,
  roleName: decodeString,
});

/** Decodes what an invitation code admitted the account to. */
export const decodeRedeemServiceCodeResult: Decoder<RedeemServiceCodeResult> =
  objectOf<RedeemServiceCodeResult>({
    roles: arrayOf(decodeRedeemedService),
  });
