/**
 * Wire contract for the security-role resource, its groupings, and the user-to-role assignment that joins an
 * account to a role.
 *
 * Mirrors the API's Application-layer role, role-group, membership and assignment contracts property for
 * property. Member names are camel-cased because the API serialises with `JsonNamingPolicy.CamelCase`, so the
 * C# `RoleId`, `RoleGroupName` and `RsvpCode` arrive here as `roleId`, `roleGroupName` and `rsvpCode`. Every
 * member is `readonly` on a response type and mutable on a request type: a response is a snapshot of server
 * state that a component must not edit in place, whereas a request is a value the component composes.
 *
 * Beyond those interfaces the file declares one RUNTIME DECODER per contract that is read, and nothing else
 * that executes. The decoders are not defensive habit. A TypeScript interface is erased at compile time, so
 * `http.get<Role>(…)` compiles to `http.get(…)` and nothing inspects the body - which is especially dangerous
 * here, because the most consequential member of this contract is a SINGLE CHARACTER. A `billingFrequency` of
 * `"Monthly"` or the number `2` would read as a frequency to the compiler and then fall through the
 * fee-schedule switch to its default branch, quietly charging a paid role on the wrong cycle. Each read
 * contract therefore carries a decoder declared FROM its interface, so forgetting a member is a compile error,
 * and the transport refuses a response that does not match; refusals name the member and the expected type,
 * never the value. The write contracts carry no decoder, because this client composes them.
 *
 * The frequency decoder checks the shape, not the vocabulary, and the admitted sets differ by direction. A
 * READ admits any single stored character - see {@link StoredBillingFrequency} for the measured reason, which
 * is shipped data rather than tolerance - while a WRITE is closed to the six published codes.
 *
 * The API sets `JsonIgnoreCondition.Never` on both of its serialisation surfaces, so a null member is written
 * as `"roleGroupId": null` rather than dropped. Every optional value below is therefore typed `T | null` and
 * none is declared with `?`: a member the body omits reads back as `undefined` while a member the body carries
 * as null reads back as `null`, and a consumer testing `=== null` takes the wrong branch for the first. Read
 * absence explicitly, with `=== null` or `== null`, and never from truthiness or from the sign of a number -
 * so never `if (roleId)`, `roleId > 0`, `roleId <= 0` or `roleId ?? -1`. `RoleGroups.RoleGroupID` is seeded
 * `IDENTITY(0, 1)` as well, so a `roleGroupId` of `0` is a real group and not an absent one.
 *
 * The role listing and the membership listing are paged inside the shared envelope; role groups are NOT paged
 * and publish a plain array inside the success envelope. Neither envelope is redeclared here - there is
 * exactly one declaration of each, in `./paged-result.model`, because a second copy would be
 * indistinguishable to a type checker from the first and would drift from it silently.
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

/**
 * How often a paid role is billed, and how long its trial period runs.
 *
 * Six single-character codes, and the characters ARE the contract:
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
 * Those effects are the legacy billing switch, reproduced here only as documentation. This module performs no
 * date arithmetic and offers no helper that does: the server derives every bound from an injected clock, and a
 * second implementation on the client would be a second answer to the same question.
 *
 * `N` is a real code and not an unset marker. It carries a second, independent duty as the no-trial guard -
 * the legacy assignment path tests the trial frequency against `"N"` to decide whether the trial terms or the
 * billing terms govern expiry - so absence of a frequency is `null`, a different fact from `N`.
 *
 * Declared as a string-literal union rather than a TypeScript `enum`: a constant enumeration is not emittable
 * under the workspace's `isolatedModules` setting, and a plain `enum` would emit a runtime object for a value
 * that is already a string on the wire.
 *
 * MIGRATION: these are SIX codes, not four.
 *
 * MIGRATION: ONE vocabulary serves BOTH COLUMNS, so `trialFrequency` is typed exactly as `billingFrequency`
 * is - this union where the value is written, and {@link StoredBillingFrequency} where it is read - and there
 * is deliberately no second type for the trial. A single role row joins the same frequency lookup twice, once
 * per column, in both the early and the terminal schema, and the API declares no separate trial type either.
 * The split that DOES exist is by DIRECTION rather than by column.
 *
 * MIGRATION: the wire form is the CHARACTER, and pinning that down took measuring rather than reading. The API
 * declares this as a C# enumeration backed by `ushort` whose members take the code points of the six
 * characters, so the default treatment would put the number 78 on the wire for `N` and the general
 * string-enumeration converter would put the member name `"None"` there - neither a value this vocabulary
 * contains. An explicit per-type converter is registered instead and no blanket enumeration policy is, which
 * was confirmed against a running API on all three counts: a response carries `"billingFrequency": "N"`, a
 * request carrying `"M"` round-trips unchanged, and a request carrying the number `77` is refused with HTTP
 * 400. Read or write the character; never the number and never the name.
 */
export type BillingFrequency = 'N' | 'O' | 'D' | 'W' | 'M' | 'Y';

/*
 * MIGRATION: the runtime table of the six codes is gone from this module, and its removal is part of the
 * read/write split rather than a tidy-up. It existed for one purpose — closing the READ decoders over the
 * published vocabulary — and that was the defect: the API sends stored characters the six do not contain, so
 * a read closed over them refused valid responses. Reads are now checked for shape by {@link
 * decodeStoredFrequency}, and the WRITE vocabulary is enforced where a write is actually composed: the role
 * form declares the six as its option list, with the caption beside each, and narrows a stored code by
 * looking it up there — which is both the crossing point between the two vocabularies and the shape of the
 * legacy `Items.FindByValue(...)` lookup it reproduces. A second runtime copy here would have no consumer
 * and would be free to drift from the one that does.
 */

/**
 * A frequency code as the DATABASE HOLDS IT: one character, whatever character that is.
 *
 * This is the read vocabulary, and it is deliberately wider than the write vocabulary. {@link
 * BillingFrequency} is what a caller MAY SEND; this is what a caller MAY RECEIVE. The two are different sets
 * because the API is deliberately asymmetric, and the asymmetry is the server's own documented behaviour
 * rather than an accident:
 *
 * - OUTBOUND, the converter is LOSSLESS. It writes whatever single character the row holds, including one no
 *   vocabulary declares, precisely so that an unrelated edit cannot rewrite a stored byte.
 * - INBOUND, the converter is STRICT. It refuses anything that is not one of the six, up-cases the character
 *   it accepts, and the request validators constrain the same property independently — so the closed
 *   vocabulary is enforced on the only side where a caller can widen it.
 *
 * MIGRATION: the two directions carry DIFFERENT vocabularies. A closed union serving both would let a single
 * shipped legacy row - one holding a character from a superseded code set - make the role listing and that
 * role's detail read permanently unavailable, deterministically, because the data itself is the cause.
 *
 * The union with `string & {}` keeps the six declared codes visible to a reader and to an editor's
 * completion list while still admitting any string, which is the honest description of the wire contract:
 * TypeScript cannot express "exactly one character", so the LENGTH is enforced at run time by {@link
 * decodeStoredFrequency} while the TYPE records the vocabulary. It also keeps the two directions from being
 * mistaken for each other - a stored code is not assignable to {@link BillingFrequency}, so anything that
 * means to send one back must narrow it deliberately.
 *
 * A code outside the six carries NO meaning this client may invent. It is rendered as the character it is,
 * classified as unsupported where a classification is needed, and never silently mapped onto one of the six.
 */
export type StoredBillingFrequency = BillingFrequency | (string & {});

/**
 * Validates an untrusted value as a stored frequency code.
 *
 * Strict about the two things the contract actually promises — that the value is a string and that it is
 * exactly one character — and deliberately silent about which character it is. That split is the whole
 * point: a `"Monthly"` or an empty string is drift worth refusing, because the column is `char(1)` and the
 * converter writes exactly one character; a `"4"` is DATA.
 *
 * Case is preserved. The server's inbound path up-cases what a caller sends, but its persistence
 * read deliberately does not, because the legacy application compared stored codes
 * case-sensitively and up-casing a stored `'m'` would change how an existing row reads and would
 * rewrite its byte on the next update. Folding case here would reintroduce exactly that.
 *
 * Exported because the account contract carries the SAME four frequency members — the member
 * services catalogue projects `Roles.BillingFrequency` and `Roles.TrialFrequency` through an
 * account-scoped shape (`MemberServiceDto`), so it reads the identical `char(1)` column by a
 * different route. Sharing this decoder rather than restating it is what stops one contract
 * accepting a value the other refuses.
 */
export const decodeStoredFrequency: Decoder<StoredBillingFrequency> = (value, path) => {
  const text: string = decodeString(value, path);

  if (text.length !== 1) {
    // Reported as a length violation rather than as an unknown code, because the length is what the contract
    // fixes: `char(1)` cannot hold `"Monthly"`, and accepting it on its first character would read it as the
    // month code.
    throw new ContractViolationError(path, 'a one-character frequency code', text);
  }

  return text;
};

/**
 * The temporal state of one user-to-role assignment.
 *
 * Derived from the assignment's two date bounds, in this order, which makes the classification total and
 * mutually exclusive:
 *
 * 1. `Expired` — the expiry bound is set and strictly before now. Expiry is terminal and outranks a start that
 *    has not yet arrived, a combination that is genuinely reachable because the legacy cancel path back-dates
 *    the expiry bound without touching the effective one.
 * 2. `Pending` — otherwise, the effective bound is set and strictly after now.
 * 3. `Active` — otherwise. This is also the answer when both bounds are unset, the ordinary case for an unpaid
 *    role.
 *
 * A bound falling exactly on the current instant counts as in force.
 *
 * The derivation is documented here and deliberately NOT implemented here. "Now" is the value the server reads
 * from its injected clock, so the server is the sole authority; a client recomputing the classification from
 * its own wall clock would disagree across a clock skew, and neither answer would be reproducible in a test.
 *
 * An assignment for a paid role whose trial has been used is EXPIRED rather than deleted: the legacy path
 * back-dates its expiry bound by one day instead of removing the row, precisely so that the trial-usage record
 * survives. `Expired` therefore describes a retained row, not a vanished one.
 *
 * MIGRATION: this classification has no legacy ancestor of any kind - no VB.NET enumeration of the name, no
 * column on `Roles` or `UserRoles` that stores it, and no discriminator anywhere in the shipped schema. It is
 * a STRING union of exactly three members, and the API declares the matching enumeration with no explicit
 * numeric values specifically so that no significance can be read into the ordering: the ordinal is
 * meaningless and must never be sent, stored or compared by magnitude. Declaring an enumeration without
 * numeric values does not by itself make it serialise by name - the platform's default is the INTEGER and this
 * API registers no blanket string-enum converter - so should a contract ever carry this classification, a
 * per-type converter must be registered at the same time or the value will arrive as `0`, `1` or `2`.
 *
 * MIGRATION: no member is added for a used trial, a withdrawn assignment, a deletion or a suspension.
 * `IsTrialUsed` is an orthogonal `bit NULL` column on `UserRoles` - an assignment can be in force and have
 * used its trial at the same time - so it is not a state at all, and folding it in would invent a lifecycle
 * the data model does not have.
 */
export type RoleStatus = 'Pending' | 'Active' | 'Expired';

/**
 * One row of the role listing.
 *
 * Mirrors `RoleListItemDto`, which is the payload of `GET /api/v1/roles`. It is a deliberate SUBSET of
 * {@link Role} and carries eleven members: the grid's columns and the paid-membership terms it displays, and
 * nothing more. Three of the detail contract's members are absent by design and a consumer must not reach
 * for them here — `roleGroupId`, because the listing filters by group rather than reporting one per row, and
 * `rsvpCode` and `iconFile`, which belong to the edit form. `portalId` is absent too, as it is from the
 * detail contract, because the tenant is already in the caller's own route. Reading any of them off this
 * shape yields `undefined` at run time, so they are omitted from the type rather than typed as nullable, and
 * the compiler refuses the mistake.
 */
export interface RoleListItem {
  /**
   * The role's identifier.
   */
  readonly roleId: number;

  /**
   * The role's name, at most 50 characters. Never null.
   */
  readonly roleName: string;

  /**
   * The role's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;

  /**
   * The recurring fee, or `null` when the role carries none.
   */
  readonly serviceFee: number | null;

  /**
   * How many {@link billingFrequency} units one billing cycle spans, or `null`.
   *
   * Meaningless on its own — the unit is the frequency code, so a period of `2` with a frequency of `M` is
   * two months and the same `2` with `W` is two weeks.
   */
  readonly billingPeriod: number | null;

  /**
   * The billing cycle's unit as STORED, or `null` when the role has no billing terms.
   *
   * A read, so {@link StoredBillingFrequency} rather than {@link BillingFrequency}.
   */
  readonly billingFrequency: StoredBillingFrequency | null;

  /**
   * The trial fee, or `null` when the role offers no trial.
   */
  readonly trialFee: number | null;

  /**
   * How many {@link trialFrequency} units the trial spans, or `null`.
   */
  readonly trialPeriod: number | null;

  /**
   * The trial period's unit as STORED, or `null`.
   *
   * A read, so {@link StoredBillingFrequency}. A code of `N` means the role offers no trial and the billing
   * terms govern expiry, which is a different fact from `null`.
   */
  readonly trialFrequency: StoredBillingFrequency | null;

  /**
   * Whether accounts may subscribe to the role themselves. Never null.
   */
  readonly isPublic: boolean;

  /**
   * Whether new accounts are enrolled in the role automatically. Never null.
   */
  readonly autoAssignment: boolean;
}

/**
 * A single role in full.
 *
 * Mirrors `RoleDetailDto`, the payload of `GET /api/v1/roles/{roleId}` and the body echoed back by the
 * create and update verbs. Fourteen members: the eleven of {@link RoleListItem} plus `roleGroupId`,
 * `rsvpCode` and `iconFile`.
 *
 * There is no `portalId`. The legacy entity carried one but the API addresses the tenant in the route
 * instead, so the fact is the caller's own and is not repeated in the body. Two copies of one fact on a
 * single response give a consumer two sources of truth and no way to choose between them when they disagree.
 */
export interface Role {
  /**
   * The role's identifier.
   *
   * Not narrowed to exclude negatives, deliberately: the legacy pseudo-role identifiers `-1`, `-2`, `-3` and
   * `-4` are not rows in `dbo.Roles` and should never arrive here, but narrowing the member would trade a
   * real type for a guess about data this endpoint does not own.
   */
  readonly roleId: number;

  /**
   * The grouping this role belongs to, or `null` when it belongs to none.
   *
   * MIGRATION: the legacy in-memory value for "no group" was `-1`, and the API maps it to `null` here. That
   * is not the sentinel-to-null collapse this migration forbids elsewhere; it is the correct reading of one
   * specific column. `RoleGroups.RoleGroupID` is `IDENTITY(0, 1) NOT NULL` and `Roles.RoleGroupID` carries a
   * foreign key referencing it, so `-1` could never have been a stored value; the legacy reader turned
   * database `NULL` into `-1` on the way out and the provider turned `-1` back into `NULL` on the way in.
   * For this column, `-1`, "Global Roles" and SQL `NULL` are one value, `null` is its honest form, and an
   * ungrouped role publishes `"roleGroupId": null`.
   *
   * Two consequences: neither `-1` nor `-2` belongs on this response, both being query-side values of a
   * listing request; and this rule must NOT be conflated with the permission pseudo-principals, where a role
   * identifier of `-1`, `-2` or `-3` names a real principal and must never become `null`. That rule governs
   * permission-bearing contracts, this one governs `Roles.RoleGroupID` alone, and both hold at once.
   */
  readonly roleGroupId: number | null;

  /**
   * The role's name, at most 50 characters. Never null.
   */
  readonly roleName: string;

  /**
   * The role's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;

  /**
   * The billing cycle's unit as STORED, or `null` when the role has no billing terms.
   *
   * A read, so {@link StoredBillingFrequency}.
   */
  readonly billingFrequency: StoredBillingFrequency | null;

  /**
   * The recurring fee, or `null` when the role carries none.
   */
  readonly serviceFee: number | null;

  /**
   * The trial period's unit as STORED, or `null`. One vocabulary serves both columns.
   */
  readonly trialFrequency: StoredBillingFrequency | null;

  /**
   * How many {@link trialFrequency} units the trial spans, or `null`.
   */
  readonly trialPeriod: number | null;

  /**
   * How many {@link billingFrequency} units one billing cycle spans, or `null`.
   */
  readonly billingPeriod: number | null;

  /**
   * The trial fee, or `null` when the role offers no trial.
   */
  readonly trialFee: number | null;

  /**
   * Whether accounts may subscribe to the role themselves. Never null.
   */
  readonly isPublic: boolean;

  /**
   * Whether new accounts are enrolled in the role automatically. Never null.
   */
  readonly autoAssignment: boolean;

  /**
   * The invitation code that lets an account join the role unprompted, or `null`.
   *
   * The legacy property was spelled `RSVPCode` and the API spells it `RsvpCode`, which the camel-case policy
   * renders as `rsvpCode`. Both spellings happen to camel-case identically, so the wire key is `rsvpCode`
   * either way.
   */
  readonly rsvpCode: string | null;

  /**
   * Relative path of the role's icon image, or `null`.
   */
  readonly iconFile: string | null;

  /**
   * Opaque marker of the revision this role was read at.
   *
   * ⚠ SEND IT BACK UNREAD AND UNMODIFIED. It is a server-minted token whose internal form is not part
   * of this contract; nothing on this side may parse it, compare it for ordering, display it, or
   * synthesise one. Its only correct use is to be carried from a read into the matching
   * {@link UpdateRoleRequest} so the server can tell whether the role changed in between.
   *
   * ⚠ REQUIRED ON THE WAY IN, nullable on the way out, and the asymmetry is the server's.
   * `RoleDetailDto.ConcurrencyToken` is declared `public string ... = string.Empty` — a non-nullable
   * member that `RoleMappings.ConcurrencyTokenFor` always populates — so a response omitting it or
   * serving a null is a malformed response and the decoder says so. Widening it to `string | null`
   * here was the defect: the null decoded silently, flowed into
   * {@link UpdateRoleRequest.concurrencyToken}, which the server permits as a last-writer-wins update,
   * and the optimistic check the marker exists to perform was skipped with nothing anywhere reporting
   * that it had been. The REQUEST member stays nullable, which is what the server actually permits.
   *
   * The empty string is the server's own unset spelling and is carried as received. A screen must not
   * invent or substitute a token, because a wrong token is refused and a fabricated one that happens
   * to match would defeat the very check it appears to satisfy.
   *
   * MIGRATION: NO LEGACY COUNTERPART. `UpdateRole`
   * (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L242-L243`) took thirteen
   * positional arguments and no revision marker, so the legacy screen overwrote whatever it found:
   * two administrators editing one role each saved the whole entity from their own stale snapshot and
   * the later save won silently, losing every field the earlier one had changed rather than only the
   * field they disagreed about. The token is the target's answer to that, and it is recorded as an
   * addition in `MIGRATION_NOTES.md`.
   */
  readonly concurrencyToken: string;
}

/**
 * A grouping of roles.
 *
 * Mirrors `RoleGroupDto`, the payload of the role-group endpoints under `/api/v1/role-groups`.
 *
 * Unlike {@link Role}, this contract DOES carry its tenant. The asymmetry is the API's, not an oversight to
 * be smoothed over.
 */
export interface RoleGroup {
  /**
   * The group's identifier.
   */
  readonly roleGroupId: number;

  /**
   * The tenant that owns the group.
   *
   * May legitimately be `-1`: `Portals.PortalID` is seeded `IDENTITY(-1, 1)`, so the first tenant
   * provisioned has identifier minus one, and a live group really does report `"portalId": -1`. Never treat
   * a negative or zero tenant identifier as absent.
   */
  readonly portalId: number;

  /**
   * The group's name, at most 50 characters. Never null.
   */
  readonly roleGroupName: string;

  /**
   * The group's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;
}

/**
 * One account's assignment to one role.
 *
 * Mirrors `RoleMembershipDto`, the row type of `GET /api/v1/roles/{roleId}/users`, and carries the columns
 * the legacy assignment grid displayed.
 *
 * MIGRATION: the legacy class INHERITED its role facts rather than referencing them.
 * `Library/Components/Users/UserRoleInfo.vb` is `Public Class UserRoleInfo` / `Inherits RoleInfo`, so it
 * declares eight members of its own and reaches the other fifteen — `RoleID` among them — through the base
 * class, giving twenty-three effective members and no `RoleID` of its own anywhere in the file. TypeScript
 * composition replaces that inheritance, which means the identifier of the assigned role has to be DECLARED
 * HERE EXPLICITLY. It is, below. Omitting it on the strength of "the legacy class does not declare one"
 * would produce a join shape with no way to say which role was assigned.
 *
 * MIGRATION: the API narrows the join to eight members rather than re-exposing the twenty-three. The role's
 * own terms are not repeated on an assignment row, because they belong to the role and are fetched from
 * {@link Role}; and two legacy members are absent from this contract altogether — `IsTrialUsed` and
 * `Subscribed`. A consumer needing either must not read it from here.
 *
 * This contract publishes `username` and `displayName` instead. The substitution is the API's: it keeps the
 * human label the grid needs while declining to broadcast an address on a listing that exists to administer
 * role membership.
 */
export interface UserRole {
  /**
   * The assignment row's identifier.
   *
   * `UserRoles.UserRoleID` is seeded `IDENTITY(1, 1)`, so unlike a role or a tenant identifier this one does
   * begin at one. That is a fact about this column and not a licence to test any identifier for truthiness.
   */
  readonly userRoleId: number;

  /**
   * The assigned account's identifier. Seeded `IDENTITY(1, 1)`.
   */
  readonly userId: number;

  /**
   * The assigned account's sign-in name. Never null.
   */
  readonly username: string;

  /**
   * The assigned account's display name, as the grid labels the row. Never null.
   */
  readonly displayName: string;

  /**
   * The assigned role's identifier.
   *
   * Declared explicitly because the legacy class inherited it rather than declaring it — see the note on this
   */
  readonly roleId: number;

  /**
   * The assigned role's name, denormalised so the grid needs no second request.
   */
  readonly roleName: string;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` when it has no start bound and is in
   * force immediately.
   *
   * MIGRATION: the legacy code had no way to say "no date". It wrote the `Null.NullDate` sentinel —
   * `Date.MinValue`, that is 0001-01-01 — into the in-memory property instead, clamping a past start back to
   * it, and its emptiness test compared only the date part. That sentinel is the legacy spelling of "unset"
   * and it must NEVER be read as a real date, nor written back as one. It does not reach this contract: the
   * column is SQL Server `datetime`, whose range starts at 1753-01-01 and which refuses 0001-01-01 outright,
   * so absence is a genuine `null` on the wire and `null` is what this member carries. Should a
   * minimum-value instant ever appear in a payload, treat it as "no date set" and not as the first day of
   * the first year.
   */
  readonly effectiveDate: string | null;

  /**
   * When the assignment ceases as an ISO 8601 instant, or `null` when it does not expire.
   *
   * `null` is the ordinary case for an unpaid role. A far-future instant of 9999-12-31 is not an error
   * either: it is what the `O` billing code means, and it reaches the wire exactly as stored.
   *
   * The same sentinel rule applies as for {@link UserRole.effectiveDate} — a minimum-value instant means "no
   * expiry" and never a date in the year one.
   */
  readonly expiryDate: string | null;
}

/**
 * Body of `POST /api/v1/roles`.
 *
 * Mirrors `CreateRoleRequest`. Thirteen members: the fourteen of {@link Role} less `roleId`, which the
 * server assigns. The tenant is not carried either — it is in the route.
 *
 * This is a WHOLE-ROW WRITE, so send every member the form holds. A member left out is a member sent as
 * `null`, which is a request to store no value rather than a request to leave a value alone.
 *
 * MIGRATION: this replaces a positional call. The legacy controller took the role's facts as an ordered
 * argument list, so adding, removing or reordering one silently changed the meaning of every argument after
 * it. Naming them removes that hazard entirely, and no `out` or `ref` parameter survives anywhere in the
 * contract: a failed write is reported by the response status and a problem-details body, not by a mutated
 * argument.
 */
export interface CreateRoleRequest {
  /**
   * The role's name, at most 50 characters. Required.
   */
  readonly roleName: string;

  /**
   * The role's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;

  /**
   * The recurring fee, or `null` for none. Zero or greater.
   */
  readonly serviceFee: number | null;

  /**
   * How many {@link CreateRoleRequest.billingFrequency} units a cycle spans, or `null`.
   */
  readonly billingPeriod: number | null;

  /**
   * The billing cycle's unit, or `null`.
   *
   * Send the CHARACTER, never the member name and never a number. A numeric value is refused with HTTP 400
   * by the converter that owns this member, which was verified against a running API; `"Month"` is not a
   * value the vocabulary contains either.
   */
  readonly billingFrequency: BillingFrequency | null;

  /**
   * The trial fee, or `null` for none. Zero or greater.
   */
  readonly trialFee: number | null;

  /**
   * How many {@link CreateRoleRequest.trialFrequency} units the trial spans, or `null`.
   */
  readonly trialPeriod: number | null;

  /**
   * The trial period's unit, or `null`.
   *
   * The same six-character vocabulary as the billing unit, by design. Sending `N` declares that the role
   * offers no trial and that the billing terms govern expiry.
   */
  readonly trialFrequency: BillingFrequency | null;

  /**
   * Whether accounts may subscribe to the role themselves.
   */
  readonly isPublic: boolean;

  /**
   * Whether new accounts are enrolled in the role automatically.
   */
  readonly autoAssignment: boolean;

  /**
   * The grouping to file the role under, or `null` for none.
   *
   * Send `null` for "no group", never `-1`; and never `-2`, which is a listing filter's value and has no
   * meaning on a write. `0` is a valid group identifier.
   */
  readonly roleGroupId: number | null;

  /**
   * The invitation code, or `null`.
   */
  readonly rsvpCode: string | null;

  /**
   * Relative path of the role's icon image, or `null`.
   */
  readonly iconFile: string | null;
}

/**
 * Body of `PUT /api/v1/roles/{roleId}`.
 *
 * Mirrors `UpdateRoleRequest`. The same thirteen members as {@link CreateRoleRequest} — the role being
 * written is identified by the route, so no identifier appears in the body and none may be added to it.
 *
 * A whole-row replacement, with the consequence that omitting a term does not mean "leave it alone", so the
 * form sends every member it holds on every write.
 *
 * Declared separately from {@link CreateRoleRequest} rather than aliased to it. The API binds two distinct
 * contracts behind the two verbs, and an alias would make a later divergence between them invisible on this
 * side.
 */
export interface UpdateRoleRequest {
  /**
   * The role's name, at most 50 characters. Required.
   */
  readonly roleName: string;

  /**
   * The role's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;

  /**
   * The grouping to file the role under, or `null` for none. Never `-1`, never `-2`.
   */
  readonly roleGroupId: number | null;

  /**
   * Whether accounts may subscribe to the role themselves.
   */
  readonly isPublic: boolean;

  /**
   * Whether new accounts are enrolled in the role automatically.
   */
  readonly autoAssignment: boolean;

  /**
   * The recurring fee, or `null` for none. Zero or greater.
   */
  readonly serviceFee: number | null;

  /**
   * How many {@link UpdateRoleRequest.billingFrequency} units a cycle spans, or `null`.
   */
  readonly billingPeriod: number | null;

  /**
   * The billing cycle's unit as its single character, or `null`.
   */
  readonly billingFrequency: BillingFrequency | null;

  /**
   * The trial fee, or `null` for none. Zero or greater.
   */
  readonly trialFee: number | null;

  /**
   * How many {@link UpdateRoleRequest.trialFrequency} units the trial spans, or `null`.
   */
  readonly trialPeriod: number | null;

  /**
   * The trial period's unit as its single character, or `null`.
   */
  readonly trialFrequency: BillingFrequency | null;

  /**
   * The invitation code, or `null`.
   */
  readonly rsvpCode: string | null;

  /**
   * Relative path of the role's icon image, or `null`.
   */
  readonly iconFile: string | null;

  /**
   * The {@link Role.concurrencyToken} of the revision this update was composed against, or `null`.
   *
   * Optional on the wire: omitting it asks the server to write unconditionally. Supplying it asks the
   * server to write ONLY IF the stored role is still at that revision, and to answer `409` with the
   * problem type `urn:dnnmigration:error:role.concurrency_conflict` when it is not.
   *
   * A screen that read the role must send the token it read. Sending `null` after a read is not a
   * neutral choice — it discards the one fact that would have revealed someone else's change.
   */
  readonly concurrencyToken: string | null;
}

/**
 * Body of `POST /api/v1/roles/{roleId}/users`, which enrols one account in one role.
 *
 * Mirrors `RoleAssignmentRequest`. The role is in the route, so the body names only the account and the
 * terms of the assignment. A successful call answers 204 with no payload; read the membership back from
 * `GET /api/v1/roles/{roleId}/users` if the screen needs the stored row.
 *
 * MIGRATION: this replaces a seven-argument positional call. Two of those arguments do not appear here at
 * all — the ambient per-request settings composite, which becomes the tenant in the route, and the operating
 * account's own identifier, which the server reads from the presented credential so that no request body can
 * assert who is acting.
 *
 * MIGRATION: absent means absent on the way in. The legacy screen substituted the `Null.NullDate` sentinel
 * for an empty date box; here an unset bound is `null` and stays `null`. What the server does with an unset
 * expiry is unchanged in effect: it derives one from the role's billing or trial terms using the
 * six-character vocabulary above, and the code `O` still yields the far-future 9999-12-31. No date
 * arithmetic is performed on this side.
 */
export interface RoleAssignmentRequest {
  /**
   * The account to enrol. Seeded `IDENTITY(1, 1)`, so never zero in practice.
   */
  readonly userId: number;

  /**
   * When the assignment takes effect as an ISO 8601 instant, or `null` for immediately.
   *
   * Send `null` for "no start bound". Never send a minimum-value instant to mean it: that was the legacy
   * in-memory spelling and the column cannot hold it.
   */
  readonly effectiveDate: string | null;

  /**
   * When the assignment ceases as an ISO 8601 instant, or `null` to let the server derive one from the
   * role's terms.
   *
   * The legacy screen validated this as strictly later than the effective bound, and the same rule applies
   * here.
   */
  readonly expiryDate: string | null;

  /**
   * Whether the operator asked for the account to be notified.
   *
   * MIGRATION: the flag survives on the contract because it was a genuine choice the legacy screen offered,
   * but the mail subsystem it drove is out of scope for this migration. No notification is sent, and a
   * successful response must not be read as implying one was. A deliberate functional reduction, recorded
   * rather than absorbed.
   */
  readonly notifyUser: boolean;
}

/**
 * Body of `POST /api/v1/role-groups`.
 *
 * Mirrors `CreateRoleGroupRequest`. Two members only: the group's own two editable facts. Its identifier is
 * assigned by the server and its tenant comes from the route, so neither appears here even though both are
 * published on {@link RoleGroup}.
 */
export interface CreateRoleGroupRequest {
  /**
   * The group's name, at most 50 characters. Required.
   */
  readonly roleGroupName: string;

  /**
   * The group's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;
}

/**
 * Body of `PUT /api/v1/role-groups/{roleGroupId}`.
 *
 * Mirrors `UpdateRoleGroupRequest`. The same two members as {@link CreateRoleGroupRequest}, and declared
 * separately for the same reason: the API binds a distinct contract to each verb, and aliasing them here
 * would hide a later divergence.
 */
export interface UpdateRoleGroupRequest {
  /**
   * The group's name, at most 50 characters. Required.
   */
  readonly roleGroupName: string;

  /**
   * The group's description, at most 1000 characters, or `null`.
   */
  readonly description: string | null;
}

/**
 * The words behind each stored billing-frequency character.
 *
 * ⚠ THE CODE IS THE DATA AND THE WORD IS PRESENTATION. `dbo.Roles.BillingFrequency` and
 * `dbo.Roles.TrialFrequency` are `char(1)` columns constrained by `FK_Roles_CodeFrequency`, and
 * `Library/Components/Security/Roles/RoleController.vb:L540-L546` switches on the raw characters. The
 * code is never renamed, case-folded, aliased or turned into an integer anywhere in this application;
 * this map only says what each one MEANS, and only for the benefit of a reader.
 *
 * ⚠ IT LIVES HERE, BESIDE THE CONTRACT, SO THAT ONE VOCABULARY SERVES EVERY SCREEN. The role editor
 * declared it first, as the captions of its two frequency selects. The role LISTING then needed the
 * same words — it renders the stored character verbatim, as the legacy grid did, and a bare `M` means
 * nothing to anyone reading it — and importing the editor's copy from the listing would have pulled a
 * lazily-loaded feature component into another feature's bundle. Two copies of user-facing wording is
 * how two screens start disagreeing about what `M` is called, so there is one copy and it is here.
 *
 * MIGRATION: the legacy filled both frequency selects FROM THE DATABASE —
 * `EditRoles.ascx.vb:L116-L125` calls `ListController.GetListEntryInfoCollection("Frequency", "")` and
 * data-binds the result. The `Library/Components/Lists` subsystem is out of scope, so no frequency
 * lookup endpoint exists and none is invented. The vocabulary is closed and fixed by a foreign key, so
 * declaring it here loses nothing; the captions are the ones the API's own refusal message names.
 */
export const BILLING_FREQUENCY_NAMES: Readonly<Record<string, string>> = Object.freeze({
  N: 'None',
  O: 'One Time',
  D: 'Day',
  W: 'Week',
  M: 'Month',
  Y: 'Year',
});

/**
 * Decodes one listed role row.
 *
 * The two frequency members are checked for shape, not for membership of the write vocabulary. A non-string
 * and a string of any length other than one are refused, because the column is `char(1)` and admitting
 * `"Monthly"` on its first character would read it as the month code. A single character the six do not
 * declare is DATA and is carried through.
 *
 * Both frequency members decode against the READ vocabulary rather than the closed write one - see {@link
 * StoredBillingFrequency} - because refusing an entire listing on account of one row holding a character
 * from a superseded code set is not strictness; it is a screen that cannot be opened.
 *
 * Every fee and period is nullable because an unpaid role has none, and the fees are decoded as numbers
 * rather than integers: a service fee is a money amount and carries a fraction.
 */
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
 * Decodes one role in full.
 *
 * `roleId` uses {@link decodeInteger} with no positivity test: the schema declares `Roles.RoleID` as
 * `IDENTITY(0, 1)`, so ZERO is the first role ever created and an ordinary identifier. `roleGroupId` is
 * nullable because a role need not belong to a group, and that null is the only expression of "ungrouped" —
 * it is never coalesced to zero, which would silently move the role into the first group.
 *
 * `concurrencyToken` is decoded as a REQUIRED STRING and nothing more. It is deliberately not validated for
 * shape, length or encoding: it is the server's own opaque marker, and a decoder that asserted a format
 * would start rejecting valid tokens the moment the server changed how it mints them. What it IS checked for
 * is presence, because `RoleDetailDto.ConcurrencyToken` is a non-nullable member the mapper always populates,
 * so a response omitting it is malformed. Tolerating absence was the defect: the null decoded silently,
 * reached a write the server treats as last-writer-wins, and the optimistic check was skipped with nothing
 * anywhere saying so. The empty string is the server's own unset spelling and is carried through as received.
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
  // ⚠ REQUIRED, NOT NULLABLE, and `nullable(...)` here was the defect. See the note above.
  concurrencyToken: decodeString,
});

/**
 * Decodes one role group.
 */
export const decodeRoleGroup: Decoder<RoleGroup> = objectOf<RoleGroup>({
  roleGroupId: decodeInteger,
  portalId: decodeInteger,
  roleGroupName: decodeString,
  description: nullable(decodeString),
});

/**
 * Decodes one user-to-role assignment.
 *
 * Both date bounds are nullable, and the status is derived from them. An assignment with no bounds is
 * permanently active; one with an expiry in the past is expired. A malformed date reaching that derivation
 * would classify the assignment by comparing against `Invalid Date`, whose every comparison is false — so an
 * expired membership would be presented as active, and the person would keep an entitlement they had lost.
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
