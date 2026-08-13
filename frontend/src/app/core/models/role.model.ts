/**
 * Wire contract for the security-role resource, its groupings, and the user-to-role assignment that joins
 * an account to a role. Mirrors the API's Application-layer role, role-group, membership and assignment
 * contracts property for property.
 */

import {
  ContractViolationError,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeNumber,
  decodeString,
  nullable,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

/** How often a paid role is billed, and how long its trial period runs. */
export type BillingFrequency = 'N' | 'O' | 'D' | 'W' | 'M' | 'Y';

// The runtime table of the six codes is gone from this module, and its removal is part of the read/write
// split rather than a tidy-up.

export type StoredBillingFrequency = BillingFrequency | (string & {});

/**
 * Validates an untrusted value as a stored frequency code. Strict about the two things the contract
 * actually promises — that the value is a string and that it is exactly one character — and deliberately
 * silent about which character it is.
 */
export const decodeStoredFrequency: Decoder<StoredBillingFrequency> = (value, path) => {
  const text: string = decodeString(value, path);

  if (text.length !== 1) {
    // Reported as a length violation rather than as an unknown code, because the length is what the
    // contract fixes: `char(1)` cannot hold `"Monthly"`, and accepting it on its first character would read
    // it as the month code.
    throw new ContractViolationError(path, 'a one-character frequency code', text);
  }

  return text;
};

/**
 * The temporal state of one user-to-role assignment. Derived from the assignment's two date bounds, in
 * this order, which makes the classification total and mutually exclusive: 1.
 */
export type RoleStatus = 'Pending' | 'Active' | 'Expired';

/** One row of the role listing. Mirrors `RoleListItemDto`, which is the payload of `GET /api/v1/roles`. */
export interface RoleListItem {
  /** The role's identifier. */
  readonly roleId: number;

  /** The role's name, at most 50 characters. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The recurring fee, or `null` when the role carries none. */
  readonly serviceFee: number | null;

  /** How many {@link billingFrequency} units one billing cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The billing cycle's unit as STORED, or `null` when the role has no billing terms. */
  readonly billingFrequency: StoredBillingFrequency | null;

  /** The trial fee, or `null` when the role offers no trial. */
  readonly trialFee: number | null;

  /** How many {@link trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** The trial period's unit as STORED, or `null`. */
  readonly trialFrequency: StoredBillingFrequency | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;
}

/**
 * A single role in full. Mirrors `RoleDetailDto`, the payload of `GET /api/v1/roles/{roleId}` and the
 * body echoed back by the create and update verbs.
 */
export interface Role {
  /** The role's identifier. */
  readonly roleId: number;

  /**
   * The grouping this role belongs to, or `null` when it belongs to none. the legacy in-memory value for
   * "no group" was `-1`, and the API maps it to `null` here.
   */
  readonly roleGroupId: number | null;

  /** The role's name, at most 50 characters. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The billing cycle's unit as STORED, or `null` when the role has no billing terms. */
  readonly billingFrequency: StoredBillingFrequency | null;

  /** The recurring fee, or `null` when the role carries none. */
  readonly serviceFee: number | null;

  /** The trial period's unit as STORED, or `null`. */
  readonly trialFrequency: StoredBillingFrequency | null;

  /** How many {@link trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** How many {@link billingFrequency} units one billing cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The trial fee, or `null` when the role offers no trial. */
  readonly trialFee: number | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;

  /**
   * The invitation code that lets an account join the role unprompted, or `null`. The legacy property was
   * spelled `RSVPCode` and the API spells it `RsvpCode`, which the camel-case policy renders as
   * `rsvpCode`.
   */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;

  /**
   * Opaque marker of the revision this role was read at. ⚠ SEND IT BACK UNREAD AND UNMODIFIED. It is a
   * server-minted token whose internal form is not part of this contract; nothing on this side may parse
   * it, compare it for ordering, display it, or synthesise one.
   */
  readonly concurrencyToken: string;
}

export interface RoleGroup {
  /** The group's identifier. */
  readonly roleGroupId: number;

  /**
   * The tenant that owns the group. May legitimately be `-1`: `Portals.PortalID` is seeded `IDENTITY(-1,
   * 1)`, so the first tenant provisioned has identifier minus one, and a live group really does report
   * `"portalId": -1`.
   */
  readonly portalId: number;

  /** The group's name, at most 50 characters. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/**
 * One account's assignment to one role. Mirrors `RoleMembershipDto`, the row type of `GET
 * /api/v1/roles/{roleId}/users`, and carries the columns the legacy assignment grid displayed.
 */
export interface UserRole {
  /**
   * The assignment row's identifier. `UserRoles.UserRoleID` is seeded `IDENTITY(1, 1)`, so unlike a role
   * or a tenant identifier this one does begin at one.
   */
  readonly userRoleId: number;

  /** The assigned account's identifier. Seeded `IDENTITY(1, 1)`. */
  readonly userId: number;

  /** The assigned account's sign-in name. */
  readonly username: string;

  /** The assigned account's display name, as the grid labels the row. */
  readonly displayName: string;

  readonly roleId: number;

  /** The assigned role's name, denormalised so the grid needs no second request. */
  readonly roleName: string;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` when it has no start bound and is
   * in force immediately. the legacy code had no way to say "no date".
   */
  readonly effectiveDate: string | null;

  /**
   * When the assignment ceases as an ISO 8601 instant, or `null` when it does not expire. `null` is the
   * ordinary case for an unpaid role.
   */
  readonly expiryDate: string | null;
}

/** Body of `POST /api/v1/roles`. Mirrors `CreateRoleRequest`. */
export interface CreateRoleRequest {
  /** The role's name, at most 50 characters. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The recurring fee, or `null` for none. */
  readonly serviceFee: number | null;

  /** How many {@link CreateRoleRequest.billingFrequency} units a cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The billing cycle's unit, or `null`. */
  readonly billingFrequency: BillingFrequency | null;

  /** The trial fee, or `null` for none. */
  readonly trialFee: number | null;

  /** How many {@link CreateRoleRequest.trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** The trial period's unit, or `null`. */
  readonly trialFrequency: BillingFrequency | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;

  /** The grouping to file the role under, or `null` for none. */
  readonly roleGroupId: number | null;

  /** The invitation code, or `null`. */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;
}

/** Body of `PUT /api/v1/roles/{roleId}`. Mirrors `UpdateRoleRequest`. */
export interface UpdateRoleRequest {
  /** The role's name, at most 50 characters. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The grouping to file the role under, or `null` for none. */
  readonly roleGroupId: number | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;

  /** The recurring fee, or `null` for none. */
  readonly serviceFee: number | null;

  /** How many {@link UpdateRoleRequest.billingFrequency} units a cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The billing cycle's unit as its single character, or `null`. */
  readonly billingFrequency: BillingFrequency | null;

  /** The trial fee, or `null` for none. */
  readonly trialFee: number | null;

  /** How many {@link UpdateRoleRequest.trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** The trial period's unit as its single character, or `null`. */
  readonly trialFrequency: BillingFrequency | null;

  /** The invitation code, or `null`. */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;

  /**
   * The {@link Role.concurrencyToken} of the revision this update was composed against, or `null`.
   * Optional on the wire: omitting it asks the server to write unconditionally.
   */
  readonly concurrencyToken: string | null;
}

/**
 * Body of `POST /api/v1/roles/{roleId}/users`, which enrols one account in one role. Mirrors
 * `RoleAssignmentRequest`.
 */
export interface RoleAssignmentRequest {
  /** The account to enrol. Seeded `IDENTITY(1, 1)`, so never zero in practice. */
  readonly userId: number;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` for immediately. Send `null` for
   * "no start bound".
   */
  readonly effectiveDate: string | null;

  readonly expiryDate: string | null;

  /**
   * Whether the operator asked for the account to be notified. the flag survives on the contract because
   * it was a genuine choice the legacy screen offered, but the mail subsystem it drove is out of scope
   * for this migration.
   */
  readonly notifyUser: boolean;
}

/** Body of `POST /api/v1/role-groups`. Mirrors `CreateRoleGroupRequest`. */
export interface CreateRoleGroupRequest {
  /** The group's name, at most 50 characters. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/** Body of `PUT /api/v1/role-groups/{roleGroupId}`. Mirrors `UpdateRoleGroupRequest`. */
export interface UpdateRoleGroupRequest {
  /** The group's name, at most 50 characters. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/**
 * The words behind each stored billing-frequency character. ⚠ THE CODE IS THE DATA AND THE WORD IS
 * PRESENTATION. `dbo.Roles.BillingFrequency` and `dbo.Roles.TrialFrequency` are `char(1)` columns
 * constrained by `FK_Roles_CodeFrequency`, and
 * `Library/Components/Security/Roles/RoleController.vb:L540-L546` switches on the raw characters.
 */
export const BILLING_FREQUENCY_NAMES: Readonly<Record<string, string>> = Object.freeze({
  N: 'None',
  O: 'One Time',
  D: 'Day',
  W: 'Week',
  M: 'Month',
  Y: 'Year',
});

export const decodeRoleListItem: Decoder<RoleListItem> = objectOf<RoleListItem>({
  roleId: decodeInteger,
  roleName: decodeString,
  description: nullable(decodeString),
  serviceFee: nullable(decodeNumber),
  billingPeriod: nullable(decodeInteger),
  billingFrequency: nullable(decodeStoredFrequency),
  trialFee: nullable(decodeNumber),
  trialPeriod: nullable(decodeInteger),
  trialFrequency: nullable(decodeStoredFrequency),
  isPublic: decodeBoolean,
  autoAssignment: decodeBoolean,
});

/**
 * Decodes one role in full. `roleId` uses {@link decodeInteger} with no positivity test: the schema
 * declares `Roles.RoleID` as `IDENTITY(0, 1)`, so ZERO is the first role ever created and an ordinary
 * identifier.
 */
export const decodeRole: Decoder<Role> = objectOf<Role>({
  roleId: decodeInteger,
  roleGroupId: nullable(decodeInteger),
  roleName: decodeString,
  description: nullable(decodeString),
  billingFrequency: nullable(decodeStoredFrequency),
  serviceFee: nullable(decodeNumber),
  trialFrequency: nullable(decodeStoredFrequency),
  trialPeriod: nullable(decodeInteger),
  billingPeriod: nullable(decodeInteger),
  trialFee: nullable(decodeNumber),
  isPublic: decodeBoolean,
  autoAssignment: decodeBoolean,
  rsvpCode: nullable(decodeString),
  iconFile: nullable(decodeString),
  // ⚠ REQUIRED, NOT NULLABLE: the server sends the token on every read, and `nullable(...)` here would
  // admit a response that cannot support a guarded write.
  concurrencyToken: decodeString,
});

/** Decodes one role group. */
export const decodeRoleGroup: Decoder<RoleGroup> = objectOf<RoleGroup>({
  roleGroupId: decodeInteger,
  portalId: decodeInteger,
  roleGroupName: decodeString,
  description: nullable(decodeString),
});

/** Decodes one user-to-role assignment. */
export const decodeUserRole: Decoder<UserRole> = objectOf<UserRole>({
  userRoleId: decodeInteger,
  userId: decodeInteger,
  username: decodeString,
  displayName: decodeString,
  roleId: decodeInteger,
  roleName: decodeString,
  effectiveDate: nullable(decodeDateString),
  expiryDate: nullable(decodeDateString),
});
