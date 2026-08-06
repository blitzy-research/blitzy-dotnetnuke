/**
 * Wire contract for the security-role resource, its groupings, and the
 * user-to-role assignment that joins an account to a role.
 *
 * Mirrors the API's Application-layer role contracts property for property:
 * `RoleListItemDto`, `RoleDetailDto`, `CreateRoleRequest`, `UpdateRoleRequest`,
 * `RoleGroupDto`, `CreateRoleGroupRequest`, `UpdateRoleGroupRequest`,
 * `RoleMembershipDto` and `RoleAssignmentRequest`. Member names are camel-cased
 * because the API serialises with `JsonNamingPolicy.CamelCase`; the C# properties
 * are `RoleId`, `RoleGroupName`, `RsvpCode` and so on, and they arrive here as
 * `roleId`, `roleGroupName`, `rsvpCode`.
 *
 * Every member is `readonly` on a response type and mutable on a request type.
 * That asymmetry is deliberate: a response is a snapshot of server state that a
 * component must not edit in place, whereas a request is a value the component
 * composes.
 *
 * This file declares types and nothing else. It contains no class, no function,
 * no decorator and no code table. It declares one RUNTIME DECODER per contract that
 * is read, and nothing else that executes.
 *
 * ## Why the decoders exist
 *
 * A TypeScript interface is erased at compile time, so `http.get<Role>(…)` compiles
 * to `http.get(…)`: nothing inspects the body, and the value is trusted purely
 * because a developer wrote a type where a value was expected. That is especially
 * dangerous for this contract, because its most consequential member is a SINGLE
 * CHARACTER. A `billingFrequency` of `"m"`, `"Monthly"` or the number `2` would
 * every one of them read as a `BillingFrequency` to the compiler and then fall
 * through the fee-schedule switch to its default branch, quietly charging a paid
 * role on the wrong cycle. Each contract that is read therefore carries a decoder
 * declared FROM its interface — forgetting a member is a compile error — and the
 * transport refuses a response that does not match. Refusals name the member and
 * the expected type, never the value.
 *
 * The write contracts carry no decoder: this client composes them, so there is
 * nothing untrusted to check.
 *
 * ## An absent value is a member present and `null`, never a missing member
 *
 * The API sets `JsonIgnoreCondition.Never` on both of its serialisation surfaces —
 * the controller formatters and the problem-details writer — so a null member is
 * written as `"roleGroupId": null` rather than dropped from the object. Every
 * optional value below is therefore typed `T | null` and none is declared with
 * `?`. The difference is not cosmetic: a member the body omits reads back as
 * `undefined` while a member the body carries as null reads back as `null`, and a
 * consumer testing `=== null` takes the wrong branch for the first. A role with no
 * group therefore publishes `"roleGroupId": null, "rsvpCode": null,
 * "iconFile": null`, and a membership with no date bounds publishes
 * `"effectiveDate": null, "expiryDate": null`.
 *
 * Read absence explicitly, with `=== null` or `== null`. Never infer it from
 * truthiness or from the sign of a number, for the reason set out next.
 *
 * ## Zero and minus one are legitimate identifiers here
 *
 * `Roles.RoleID` is seeded `IDENTITY(0, 1)` and `Portals.PortalID` is seeded
 * `IDENTITY(-1, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`
 * L115 and L77), and the shipped data exercises both: the baseline installer
 * seeds a `Registered Users` role at `PortalID = 0` (L7194), and a live listing of
 * roles really does return a role whose `roleId` is `0` while a live role group
 * really does report `"portalId": -1`. So never write `if (roleId)`, never
 * `roleId > 0`, never `roleId <= 0` and never `roleId ?? -1`. `RoleGroups.RoleGroupID`
 * is seeded `IDENTITY(0, 1)` as well, so a `roleGroupId` of `0` is a real group and
 * not an absent one.
 *
 * ## How a collection of these types arrives
 *
 * The role listing is paged: `GET /api/v1/roles` publishes
 * `RoleListItem` rows inside the shared paged envelope, as does the membership
 * listing `GET /api/v1/roles/{roleId}/users` for `UserRole`.
 * Role groups are NOT paged — `GET /api/v1/role-groups`
 * publishes a plain array inside the success envelope. Neither envelope is
 * redeclared here. There is exactly one declaration of each, in
 * `./paged-result.model`, and the service layer composes them over the item types
 * below; a second copy of an envelope would be indistinguishable to a type checker
 * from the first and would drift from it silently.
 */

import {
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

/**
 * How often a paid role is billed, and how long its trial period runs.
 *
 * Six single-character codes, and the characters ARE the contract. They are the
 * literal bytes stored in the `BillingFrequency` and `TrialFrequency` columns of
 * `dbo.Roles`, both `char(1) NULL`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.05.SqlDataProvider`
 * L2753 and L2755), so renaming one, folding its case, expanding it to a word or
 * turning it into a number would not fail a compilation — it would silently
 * mis-read live rows.
 *
 * | Code | Lookup description | Effect on the expiry bound                |
 * | ---- | ------------------ | ----------------------------------------- |
 * | `N`  | None               | No expiry at all                          |
 * | `O`  | One-time Fee       | Perpetual; the far-future date 9999-12-31 |
 * | `D`  | Day(s)             | Advances by `period` days                 |
 * | `W`  | Week(s)            | Advances by `period * 7` days             |
 * | `M`  | Month(s)           | Advances by `period` months               |
 * | `Y`  | Year(s)            | Advances by `period` years                |
 *
 * The effects are the legacy `Select Case` at
 * `Library/Components/Security/Roles/RoleController.vb` L540-L546, reproduced here
 * only as documentation. This module performs no date arithmetic and offers no
 * helper that does: the server derives every bound from an injected clock, and a
 * second implementation on the client would be a second answer to the same
 * question.
 *
 * `N` is a real code and not an unset marker. It carries a second, independent
 * duty as the no-trial guard: the legacy assignment path tests
 * `role.TrialFrequency.ToString <> "N"` to decide whether the trial terms or the
 * billing terms govern expiry (`RoleController.vb` L521). Absence of a frequency
 * is `null`, which is a different fact from `N`.
 *
 * Declared as a string-literal union rather than a TypeScript `enum`. A constant
 * enumeration is not emittable under the workspace's `isolatedModules` setting, and
 * a plain `enum` would emit a runtime object for a value that is already a string on
 * the wire; the union costs nothing at runtime and still gives an exhaustive check.
 *
 * MIGRATION: these are SIX codes, not four. The legacy properties were plain
 * `System.String` carrying XML serialisation attributes
 * (`Library/Components/Security/Roles/RoleInfo.vb` L149 and L188), and their own
 * documentation comments at L140-L145 and L179-L184 enumerate all six. Five
 * further authorities agree: the six-branch `Select Case` at
 * `RoleController.vb` L540-L546; the `<> "N"` guard at L521; the since-removed
 * `FK_Roles_CodeFrequency` constraint, which pointed `Roles.BillingFrequency` at
 * `CodeFrequency([Code])` (`01.00.00.SqlDataProvider` L582-L588); the terminal seed
 * rows inserted at
 * `01.00.08.SqlDataProvider` L6840, L6849, L6858, L6867, L6876 and L6885, which
 * are exactly `N`, `O`, `D`, `W`, `M` and `Y`; and the deletion of the superseded
 * numeric codes `'0'` through `'5'` alongside them at L6846, L6855, L6864, L6873,
 * L6882 and L6891.
 *
 * MIGRATION: ONE vocabulary serves BOTH columns, so `trialFrequency` is typed with
 * this same union and there is deliberately no second type for it. A single role
 * row joins the same frequency lookup twice, once per column, in both the early and
 * the terminal schema (`01.00.08.SqlDataProvider` L7032-L7033). The API declares no
 * separate trial type either.
 *
 * MIGRATION: the wire form is the CHARACTER, and pinning that down took measuring
 * rather than reading. The API declares this as a C# enumeration backed by
 * `ushort` whose members take the code points of the six characters, so the default
 * treatment of an enumeration would put the number 78 on the wire for `N`, and the
 * general string-enumeration converter would put the member name `"None"` there.
 * Neither is a value this vocabulary contains. An explicit per-type converter is
 * registered instead, and no blanket enumeration policy is, which was confirmed
 * against a running API on all three counts: a response carries
 * `"billingFrequency": "N"`, a request body carrying `"M"` round-trips unchanged as
 * `"M"`, and a request body carrying the number `77` is refused with HTTP 400
 * rather than quietly accepted. Read or write the character; never the number and
 * never the name.
 */
export type BillingFrequency = 'N' | 'O' | 'D' | 'W' | 'M' | 'Y';

/**
 * The six published billing codes, as an array the decoders close over.
 *
 * Spelled out rather than derived, because a union type does not exist at runtime. The
 * codes are load-bearing DATA — `Roles.BillingFrequency` is `char(1)` — so they are
 * preserved verbatim and never translated to a name or an ordinal.
 */
const BILLING_CODES: readonly BillingFrequency[] = ['N', 'O', 'D', 'W', 'M', 'Y'];

/**
 * The temporal state of one user-to-role assignment.
 *
 * Derived from the assignment's two date bounds, in this order, which makes the
 * classification total and mutually exclusive:
 *
 * 1. `Expired` — the expiry bound is set and strictly before now. Expiry is
 *    terminal and outranks a start that has not yet arrived, a combination that is
 *    genuinely reachable because the legacy cancel path back-dates the expiry bound
 *    without touching the effective one.
 * 2. `Pending` — otherwise, the effective bound is set and strictly after now.
 * 3. `Active` — otherwise. This is also the answer when both bounds are unset,
 *    which is the ordinary case for an unpaid role.
 *
 * A bound falling exactly on the current instant counts as in force.
 *
 * The derivation is documented here and deliberately NOT implemented here. "Now"
 * is the value the server reads from its injected clock, so the server is the sole
 * authority; a client that recomputed the classification from its own wall clock
 * would disagree with the server across a clock skew, and neither answer would be
 * reproducible in a test.
 *
 * A note on the state that looks like it should be missing: an assignment for a
 * paid role whose trial has been used is EXPIRED rather than deleted. The legacy
 * path back-dates its expiry bound by one day instead of removing the row
 * (`Library/Components/Security/Roles/RoleController.vb` L494-L496) precisely so
 * that the trial-usage record survives. `Expired` therefore describes a retained
 * row, not a vanished one.
 *
 * MIGRATION: this classification has no legacy ancestor of any kind. There is no
 * VB.NET enumeration of the name, no column on `Roles` or `UserRoles` that stores
 * it, and no discriminator anywhere in the shipped schema. It replaces the inline
 * date comparisons at `RoleController.vb` L531 and the cancel-to-expiry path at
 * L494-L496 with one named state, and it is computed on read and never persisted.
 *
 * MIGRATION: it is a STRING union with exactly three members. The API declares the
 * matching enumeration — `Domain/Enums/RoleStatus.cs` — with no explicit numeric
 * values specifically so that no significance can be read into the ordering; the
 * ordinal is meaningless and must never be sent, stored or compared by magnitude.
 *
 * WHAT MAKES THE NAMES TRAVEL, AND WHY IT IS NOT IN PLACE YET. This is stated
 * precisely because the obvious reading of the paragraph above is wrong. Nothing about
 * declaring an enumeration without numeric values makes it serialise by name: the
 * platform's default for an enumeration is the INTEGER, and this API registers no
 * blanket string-enum converter. It registers exactly two per-type converters, in
 * `Application/Serialization/DnnJsonConverters.cs`, and they exist for the two values
 * that do cross the wire — the billing frequency, pinned to its one-character code,
 * and the permission key, pinned to its member name. So if this classification were
 * published TODAY, without a third converter, it would arrive as `0`, `1` or `2` and
 * this union would be wrong.
 *
 * That is a statement about the future rather than a defect, because the value is not
 * published at all: the role listing documents the classification as absent by design,
 * the Domain enumeration is consumed only server-side by the portal-administration
 * evaluator, and no member of any contract in this file carries the type. The union is
 * therefore the CLIENT's vocabulary for a classification it derives, and it is
 * documented rather than removed so that the vocabulary has one spelling. The
 * obligation this note records is on whoever publishes it: register a per-type string
 * converter for the enumeration at the same time, exactly as was done for the other
 * two, or the names above will not be what arrives.
 *
 * MIGRATION: no member is added for a used trial, for a withdrawn assignment, for a
 * deletion or for a suspension. `IsTrialUsed` is an orthogonal `bit NULL` column on
 * `UserRoles` — an assignment can be in force and have used its trial at the same
 * time — so it is not a state at all, and folding it in would invent a lifecycle the
 * data model does not have. It is in any case not published on the membership
 * contract; see the note on {@link UserRole}.
 *
 * No role or membership contract published by the API carries this value today, so
 * it is the client's vocabulary for the classification rather than a member to be
 * read off a payload. Should a contract ever carry it, these three names are what it
 * must be made to send — see the converter note above for what that requires.
 */
export type RoleStatus = 'Pending' | 'Active' | 'Expired';

/**
 * One row of the role listing.
 *
 * Mirrors `RoleListItemDto`, which is the payload of
 * `GET /api/v1/roles`. It is a deliberate SUBSET of
 * {@link Role} and carries eleven members: the grid's columns and the paid-membership
 * terms it displays, and nothing more. Three of the detail contract's members are
 * absent by design and a consumer must not reach for them here — `roleGroupId`,
 * because the listing filters by group rather than reporting one per row, and
 * `rsvpCode` and `iconFile`, which belong to the edit form. `portalId` is absent too,
 * as it is from the detail contract, because the tenant is already in the caller's
 * own route. Reading any of them off this shape yields `undefined` at run time, so
 * they are omitted from the type rather than typed as nullable, and the compiler
 * refuses the mistake.
 */
export interface RoleListItem {
  /**
   * The role's identifier.
   *
   * May legitimately be `0`: `Roles.RoleID` is seeded `IDENTITY(0, 1)`. Never test
   * this member for truthiness or sign.
   */
  readonly roleId: number;

  /** The role's name, at most 50 characters. Never null. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The recurring fee, or `null` when the role carries none. */
  readonly serviceFee: number | null;

  /**
   * How many {@link billingFrequency} units one billing cycle spans, or `null`.
   *
   * Meaningless on its own — the unit is the frequency code, so a period of `2`
   * with a frequency of `M` is two months and the same `2` with `W` is two weeks.
   */
  readonly billingPeriod: number | null;

  /** The billing cycle's unit, or `null` when the role has no billing terms. */
  readonly billingFrequency: BillingFrequency | null;

  /** The trial fee, or `null` when the role offers no trial. */
  readonly trialFee: number | null;

  /** How many {@link trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /**
   * The trial period's unit, or `null`.
   *
   * Typed with {@link BillingFrequency} because one vocabulary serves both columns.
   * A code of `N` here means the role offers no trial and the billing terms govern
   * expiry, which is a different fact from `null`.
   */
  readonly trialFrequency: BillingFrequency | null;

  /** Whether accounts may subscribe to the role themselves. Never null. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. Never null. */
  readonly autoAssignment: boolean;
}

/**
 * A single role in full.
 *
 * Mirrors `RoleDetailDto`, the payload of
 * `GET /api/v1/roles/{roleId}` and the body echoed back by the
 * create and update verbs. Fourteen members: the eleven of {@link RoleListItem}
 * plus `roleGroupId`, `rsvpCode` and `iconFile`.
 *
 * There is no `portalId`. The legacy entity carried one
 * (`Library/Components/Security/Roles/RoleInfo.vb` L80) but the API addresses the
 * tenant in the route instead, so the fact is the caller's own and is not repeated
 * in the body. Two copies of one fact on a single response give a consumer two
 * sources of truth and no way to choose between them when they disagree.
 */
export interface Role {
  /**
   * The role's identifier.
   *
   * May legitimately be `0`, as on {@link RoleListItem}.
   *
   * Not narrowed to exclude negatives, and deliberately so. `Globals.vb` L95-L98
   * reserves four values as pseudo-role identifiers — `-1` all users, `-2`
   * superuser, `-3` unauthenticated users, `-4` nothing — and declares all four as
   * `String` constants rather than integers. None of them is a row in `dbo.Roles`,
   * so none should ever arrive on this contract; but narrowing the member to
   * exclude them would trade a real type for a guess about data this endpoint does
   * not own. Note also that only three have a matching display-name constant at
   * L100-L102; the fourth has none.
   */
  readonly roleId: number;

  /**
   * The grouping this role belongs to, or `null` when it belongs to none.
   *
   * MIGRATION: the legacy in-memory value for "no group" was `-1`, and the API maps
   * it to `null` here. That is not the sentinel-to-null collapse this migration
   * forbids elsewhere; it is the correct reading of one specific column.
   * `RoleGroups.RoleGroupID` is `IDENTITY(0, 1) NOT NULL` and `Roles.RoleGroupID`
   * carries a foreign key referencing it, so `-1` could never have been a stored
   * value; the legacy reader turned database `NULL` into `-1` on the way out and the
   * provider turned `-1` back into `NULL` on the way in. For this column, `-1`,
   * "Global Roles" and SQL `NULL` are one value, `null` is its honest form, and an
   * ungrouped role publishes `"roleGroupId": null`.
   *
   * Three consequences, each a real defect if ignored:
   *
   * - `0` IS A REAL GROUP. The group table is seeded `IDENTITY(0, 1)`, so the first
   *   group created has identifier zero. Test `=== null`, never truthiness, and
   *   never `roleGroupId > 0`.
   * - `-2` MUST NEVER APPEAR HERE. The legacy listing screen adds a group filter
   *   entry valued `-2` meaning "do not filter by group", against `-1` meaning
   *   "global roles only" (`Website/admin/Security/Roles.ascx.vb` L112 and L114,
   *   selected by the `RoleGroupId < -1` test at L72). Both are query-side values
   *   belonging to a listing request, and only `-1` was ever persisted. Neither
   *   belongs on this response.
   * - DO NOT CONFLATE THIS WITH THE PERMISSION PSEUDO-PRINCIPALS. In the module and
   *   page permission contracts a role identifier of `-1`, `-2` or `-3` names a real
   *   principal and must never become `null`. That rule governs permission-bearing
   *   contracts; this one governs `Roles.RoleGroupID` alone. Both are correct at the
   *   same time, and collapsing them into one rule is a live bug.
   */
  readonly roleGroupId: number | null;

  /** The role's name, at most 50 characters. Never null. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The billing cycle's unit, or `null` when the role has no billing terms. */
  readonly billingFrequency: BillingFrequency | null;

  /** The recurring fee, or `null` when the role carries none. */
  readonly serviceFee: number | null;

  /** The trial period's unit, or `null`. One vocabulary serves both columns. */
  readonly trialFrequency: BillingFrequency | null;

  /** How many {@link trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** How many {@link billingFrequency} units one billing cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The trial fee, or `null` when the role offers no trial. */
  readonly trialFee: number | null;

  /** Whether accounts may subscribe to the role themselves. Never null. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. Never null. */
  readonly autoAssignment: boolean;

  /**
   * The invitation code that lets an account join the role unprompted, or `null`.
   *
   * The legacy property was spelled `RSVPCode`
   * (`Library/Components/Security/Roles/RoleInfo.vb` L278) and the API spells it
   * `RsvpCode`, which the camel-case policy renders as `rsvpCode`. Both spellings
   * happen to camel-case identically, so the wire key is `rsvpCode` either way.
   */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;
}

/**
 * A grouping of roles.
 *
 * Mirrors `RoleGroupDto`, the payload of the role-group endpoints under
 * `/api/v1/role-groups`. Exactly four members, matching the four
 * of the legacy entity (`Library/Components/Security/Roles/RoleGroupInfo.vb` L53,
 * L68, L83 and L98).
 *
 * Unlike {@link Role}, this contract DOES carry its tenant. The asymmetry is the
 * API's, not an oversight to be smoothed over.
 */
export interface RoleGroup {
  /**
   * The group's identifier.
   *
   * May legitimately be `0`: `RoleGroups.RoleGroupID` is seeded `IDENTITY(0, 1)`.
   */
  readonly roleGroupId: number;

  /**
   * The tenant that owns the group.
   *
   * May legitimately be `-1`: `Portals.PortalID` is seeded `IDENTITY(-1, 1)`, so the
   * first tenant provisioned has identifier minus one, and a live group really does
   * report `"portalId": -1`. Never treat a negative or zero tenant identifier as
   * absent.
   */
  readonly portalId: number;

  /** The group's name, at most 50 characters. Never null. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/**
 * One account's assignment to one role.
 *
 * Mirrors `RoleMembershipDto`, the row type of
 * `GET /api/v1/roles/{roleId}/users`, and carries the columns the
 * legacy assignment grid displayed.
 *
 * MIGRATION: the legacy class INHERITED its role facts rather than referencing
 * them. `Library/Components/Users/UserRoleInfo.vb` L42-L43 is
 * `Public Class UserRoleInfo` / `Inherits RoleInfo`, so it declares eight members of
 * its own and reaches the other fifteen — `RoleID` among them — through the base
 * class, giving twenty-three effective members and no `RoleID` of its own anywhere
 * in the file. TypeScript composition replaces that inheritance, which means the
 * identifier of the assigned role has to be DECLARED HERE EXPLICITLY. It is, below.
 * Omitting it on the strength of "the legacy class does not declare one" would
 * produce a join shape with no way to say which role was assigned.
 *
 * MIGRATION: the API narrows the join to eight members rather than re-exposing the
 * twenty-three. The role's own terms are not repeated on an assignment row, because
 * they belong to the role and are fetched from {@link Role}; and two legacy members
 * are absent from this contract altogether — `IsTrialUsed` and `Subscribed`. A
 * consumer needing either must not read it from here.
 *
 * MIGRATION: the two identity members the legacy grid showed were `FullName` and
 * `Email` (`UserRoleInfo.vb` L71 and L80). This contract publishes `username` and
 * `displayName` instead. The substitution is the API's: it keeps the human label the
 * grid needs while declining to broadcast an address on a listing that exists to
 * administer role membership.
 */
export interface UserRole {
  /**
   * The assignment row's identifier.
   *
   * `UserRoles.UserRoleID` is seeded `IDENTITY(1, 1)`
   * (`01.00.00.SqlDataProvider` L239), so unlike a role or a tenant identifier this
   * one does begin at one. That is a fact about this column and not a licence to
   * test any identifier for truthiness.
   */
  readonly userRoleId: number;

  /** The assigned account's identifier. Seeded `IDENTITY(1, 1)`. */
  readonly userId: number;

  /** The assigned account's sign-in name. Never null. */
  readonly username: string;

  /** The assigned account's display name, as the grid labels the row. Never null. */
  readonly displayName: string;

  /**
   * The assigned role's identifier.
   *
   * Declared explicitly because the legacy class inherited it rather than declaring
   * it — see the note on this interface. May legitimately be `0`.
   */
  readonly roleId: number;

  /** The assigned role's name, denormalised so the grid needs no second request. */
  readonly roleName: string;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` when it has
   * no start bound and is in force immediately.
   *
   * MIGRATION: the legacy code had no way to say "no date". It wrote the
   * `Null.NullDate` sentinel — `Date.MinValue`, that is 0001-01-01 — into the
   * in-memory property instead, clamping a past start back to it
   * (`Library/Components/Security/Roles/RoleController.vb` L531), and its emptiness
   * test compared only the date part. That sentinel is the legacy spelling of "unset"
   * and it must NEVER be read as a real date, nor written back as one. It does not
   * reach this contract: the column is SQL Server `datetime`, whose range starts at
   * 1753-01-01 and which refuses 0001-01-01 outright, so absence is a genuine `null`
   * on the wire and `null` is what this member carries. Should a minimum-value
   * instant ever appear in a payload, treat it as "no date set" and not as the first
   * day of the first year.
   */
  readonly effectiveDate: string | null;

  /**
   * When the assignment ceases as an ISO 8601 instant, or `null` when it does not
   * expire.
   *
   * `null` is the ordinary case for an unpaid role. A far-future instant of
   * 9999-12-31 is not an error either: it is what the `O` billing code means, and it
   * reaches the wire exactly as stored.
   *
   * The same sentinel rule applies as for {@link UserRole.effectiveDate} — a
   * minimum-value instant means "no expiry" and never a date in the year one.
   */
  readonly expiryDate: string | null;
}

/**
 * Body of `POST /api/v1/roles`.
 *
 * Mirrors `CreateRoleRequest`. Thirteen members: the fourteen of {@link Role} less
 * `roleId`, which the server assigns. The tenant is not carried either — it is in
 * the route.
 *
 * This is a WHOLE-ROW WRITE, so send every member the form holds. A member left out
 * is a member sent as `null`, which is a request to store no value rather than a
 * request to leave a value alone.
 *
 * MIGRATION: this replaces a positional call. The legacy controller took the role's
 * facts as an ordered argument list, so adding, removing or reordering one silently
 * changed the meaning of every argument after it. Naming them removes that hazard
 * entirely, and no `out` or `ref` parameter survives anywhere in the contract: a
 * failed write is reported by the response status and a problem-details body, not by
 * a mutated argument.
 */
export interface CreateRoleRequest {
  /** The role's name, at most 50 characters. Required. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The recurring fee, or `null` for none. Zero or greater. */
  readonly serviceFee: number | null;

  /** How many {@link CreateRoleRequest.billingFrequency} units a cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /**
   * The billing cycle's unit, or `null`.
   *
   * Send the CHARACTER, never the member name and never a number. A numeric value is
   * refused with HTTP 400 by the converter that owns this member, which was verified
   * against a running API; `"Month"` is not a value the vocabulary contains either.
   */
  readonly billingFrequency: BillingFrequency | null;

  /** The trial fee, or `null` for none. Zero or greater. */
  readonly trialFee: number | null;

  /** How many {@link CreateRoleRequest.trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /**
   * The trial period's unit, or `null`.
   *
   * The same six-character vocabulary as the billing unit, by design. Sending `N`
   * declares that the role offers no trial and that the billing terms govern expiry.
   */
  readonly trialFrequency: BillingFrequency | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;

  /**
   * The grouping to file the role under, or `null` for none.
   *
   * Send `null` for "no group", never `-1`; and never `-2`, which is a listing
   * filter's value and has no meaning on a write. `0` is a valid group identifier.
   */
  readonly roleGroupId: number | null;

  /** The invitation code, or `null`. */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;
}

/**
 * Body of `PUT /api/v1/roles/{roleId}`.
 *
 * Mirrors `UpdateRoleRequest`. The same thirteen members as
 * {@link CreateRoleRequest} — the role being written is identified by the route, so
 * no identifier appears in the body and none may be added to it.
 *
 * A WHOLE-ROW REPLACEMENT with the same consequence: omitting a term does not mean
 * "leave it alone", so the form sends every member it holds on every write.
 *
 * Declared separately from {@link CreateRoleRequest} rather than aliased to it. The
 * API binds two distinct contracts behind the two verbs, and an alias would make a
 * later divergence between them invisible on this side.
 */
export interface UpdateRoleRequest {
  /** The role's name, at most 50 characters. Required. */
  readonly roleName: string;

  /** The role's description, at most 1000 characters, or `null`. */
  readonly description: string | null;

  /** The grouping to file the role under, or `null` for none. Never `-1`, never `-2`. */
  readonly roleGroupId: number | null;

  /** Whether accounts may subscribe to the role themselves. */
  readonly isPublic: boolean;

  /** Whether new accounts are enrolled in the role automatically. */
  readonly autoAssignment: boolean;

  /** The recurring fee, or `null` for none. Zero or greater. */
  readonly serviceFee: number | null;

  /** How many {@link UpdateRoleRequest.billingFrequency} units a cycle spans, or `null`. */
  readonly billingPeriod: number | null;

  /** The billing cycle's unit as its single character, or `null`. */
  readonly billingFrequency: BillingFrequency | null;

  /** The trial fee, or `null` for none. Zero or greater. */
  readonly trialFee: number | null;

  /** How many {@link UpdateRoleRequest.trialFrequency} units the trial spans, or `null`. */
  readonly trialPeriod: number | null;

  /** The trial period's unit as its single character, or `null`. */
  readonly trialFrequency: BillingFrequency | null;

  /** The invitation code, or `null`. */
  readonly rsvpCode: string | null;

  /** Relative path of the role's icon image, or `null`. */
  readonly iconFile: string | null;
}

/**
 * Body of `POST /api/v1/roles/{roleId}/users`, which enrols one
 * account in one role.
 *
 * Mirrors `RoleAssignmentRequest`. The role is in the route, so the body names only
 * the account and the terms of the assignment. A successful call answers 204 with no
 * payload; read the membership back from
 * `GET /api/v1/roles/{roleId}/users` if the screen needs the
 * stored row.
 *
 * MIGRATION: this replaces a seven-argument positional call. Two of those arguments
 * do not appear here at all — the ambient per-request settings composite, which
 * becomes the tenant in the route, and the operating account's own identifier, which
 * the server reads from the presented credential so that no request body can assert
 * who is acting.
 *
 * MIGRATION: absent means absent on the way in. The legacy screen substituted the
 * `Null.NullDate` sentinel for an empty date box; here an unset bound is `null` and
 * stays `null`. What the server does with an unset expiry is unchanged in effect: it
 * derives one from the role's billing or trial terms using the six-character
 * vocabulary above, and the code `O` still yields the far-future 9999-12-31. No date
 * arithmetic is performed on this side.
 */
export interface RoleAssignmentRequest {
  /** The account to enrol. Seeded `IDENTITY(1, 1)`, so never zero in practice. */
  readonly userId: number;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` for
   * immediately.
   *
   * Send `null` for "no start bound". Never send a minimum-value instant to mean it:
   * that was the legacy in-memory spelling and the column cannot hold it.
   */
  readonly effectiveDate: string | null;

  /**
   * When the assignment ceases as an ISO 8601 instant, or `null` to let the server
   * derive one from the role's terms.
   *
   * The legacy screen validated this as strictly later than the effective bound, and
   * the same rule applies here.
   */
  readonly expiryDate: string | null;

  /**
   * Whether the operator asked for the account to be notified.
   *
   * MIGRATION: the flag survives on the contract because it was a genuine choice the
   * legacy screen offered, but the mail subsystem it drove is out of scope for this
   * migration. No notification is sent, and a successful response must not be read as
   * implying one was. A deliberate functional reduction, recorded rather than
   * absorbed.
   */
  readonly notifyUser: boolean;
}

/**
 * Body of `POST /api/v1/role-groups`.
 *
 * Mirrors `CreateRoleGroupRequest`. Two members only: the group's own two editable
 * facts. Its identifier is assigned by the server and its tenant comes from the
 * route, so neither appears here even though both are published on
 * {@link RoleGroup}.
 */
export interface CreateRoleGroupRequest {
  /** The group's name, at most 50 characters. Required. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/**
 * Body of `PUT /api/v1/role-groups/{roleGroupId}`.
 *
 * Mirrors `UpdateRoleGroupRequest`. The same two members as
 * {@link CreateRoleGroupRequest}, and declared separately for the same reason: the
 * API binds a distinct contract to each verb, and aliasing them here would hide a
 * later divergence.
 */
export interface UpdateRoleGroupRequest {
  /** The group's name, at most 50 characters. Required. */
  readonly roleGroupName: string;

  /** The group's description, at most 1000 characters, or `null`. */
  readonly description: string | null;
}

/**
 * Decodes one listed role row.
 *
 * ⚠ THE TWO FREQUENCY MEMBERS ARE REFUSED WHEN THE CODE IS UNRECOGNISED. They are single
 * characters, so `"m"`, `"Monthly"` and the number `2` would all pass a type assertion and
 * then fall through the fee-schedule switch to its default branch — charging a paid role on
 * the wrong cycle, or on none. Refusing is the only outcome that cannot be mistaken for a
 * correct answer.
 *
 * Every fee and period is nullable because an unpaid role has none, and the fees are decoded
 * as numbers rather than integers: a service fee is a money amount and carries a fraction.
 */
export const decodeRoleListItem: Decoder<RoleListItem> = objectOf<RoleListItem>({
  roleId: decodeInteger,
  roleName: decodeString,
  description: nullable(decodeString),
  serviceFee: nullable(decodeNumber),
  billingPeriod: nullable(decodeInteger),
  billingFrequency: nullable(oneOf(BILLING_CODES)),
  trialFee: nullable(decodeNumber),
  trialPeriod: nullable(decodeInteger),
  trialFrequency: nullable(oneOf(BILLING_CODES)),
  isPublic: decodeBoolean,
  autoAssignment: decodeBoolean,
});

/**
 * Decodes one role in full.
 *
 * `roleId` uses {@link decodeInteger} with no positivity test: the schema declares
 * `Roles.RoleID` as `IDENTITY(0, 1)`, so ZERO is the first role ever created and an ordinary
 * identifier. `roleGroupId` is nullable because a role need not belong to a group, and that
 * null is the only expression of "ungrouped" — it is never coalesced to zero, which would
 * silently move the role into the first group.
 */
export const decodeRole: Decoder<Role> = objectOf<Role>({
  roleId: decodeInteger,
  roleGroupId: nullable(decodeInteger),
  roleName: decodeString,
  description: nullable(decodeString),
  billingFrequency: nullable(oneOf(BILLING_CODES)),
  serviceFee: nullable(decodeNumber),
  trialFrequency: nullable(oneOf(BILLING_CODES)),
  trialPeriod: nullable(decodeInteger),
  billingPeriod: nullable(decodeInteger),
  trialFee: nullable(decodeNumber),
  isPublic: decodeBoolean,
  autoAssignment: decodeBoolean,
  rsvpCode: nullable(decodeString),
  iconFile: nullable(decodeString),
});

/** Decodes one role group. */
export const decodeRoleGroup: Decoder<RoleGroup> = objectOf<RoleGroup>({
  roleGroupId: decodeInteger,
  portalId: decodeInteger,
  roleGroupName: decodeString,
  description: nullable(decodeString),
});

/**
 * Decodes one user-to-role assignment.
 *
 * ⚠ BOTH DATE BOUNDS ARE NULLABLE, AND THE STATUS IS DERIVED FROM THEM. An assignment with
 * no bounds is permanently active; one with an expiry in the past is expired. A malformed
 * date reaching that derivation would classify the assignment by comparing against
 * `Invalid Date`, whose every comparison is false — so an expired membership would be
 * presented as active, and the person would keep an entitlement they had lost.
 */
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
