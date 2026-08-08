/**
 * Specification for `./role.store`, the signal-backed state behind role administration.
 *
 * Exercises the store against a mock HTTP backend rather than against a mocked service,
 * so each case proves the URL, the verb, the query parameters, the response handling AND
 * — decisively for this store — the NUMBER OF REQUESTS in a single pass. The request
 * count is not incidental here: the store's most consequential behaviour is that a
 * successful assignment removal is followed by a SECOND request, and a mocked service
 * would let a missing second request pass unnoticed.
 *
 * ---------------------------------------------------------------------------
 * NO USER-SPECIFIED RULES GOVERN THIS FILE
 *
 * The project supplies no rules document. The rules review returns a one-line absence
 * statement, and it returns the byte-identical statement for ranges that BEGIN PAST THE
 * FIRST LINE, which establishes there is no document body left to page through. No file
 * therefore enters scope on rule grounds and none is invented. Absence is not licence to
 * lower the bar: the migration plan's enterprise baseline governs instead, and its
 * behavioural-equivalence, code-organisation, migration-record and strict-typing items
 * are what the cases below are organised around.
 *
 * ---------------------------------------------------------------------------
 * THE HARNESS, AND WHY IT IS SHAPED THIS WAY
 *
 * - The real client is registered FIRST and the testing backend SECOND. The testing
 *   provider overrides the backend the real one installed, so the order is load-bearing:
 *   reversed, the genuine backend survives and the cases attempt live requests.
 * - No interceptor is registered. This file is about the store's own behaviour, and
 *   running the interceptor chain here would assert two units at once.
 * - `httpMock.verify()` runs after every case. It is the load-bearing assertion of the
 *   whole file: it fails on any request opened and never consumed, which is what proves
 *   each command issues exactly the requests it should and is the entire mechanism behind
 *   the re-read cases and the no-request cases alike.
 * - Every asserted URL is RELATIVE. The test target declares no environment file
 *   replacement, so these cases compile against the production environment module, whose
 *   API base is the root-relative `/api/v1`. The reverse proxy serves the bundle and the
 *   API from one origin, so a relative base is the correct value and an absolute one
 *   would be a portability defect that no build step detects.
 * - No timer, no wall-clock read, no randomness. Every instant in a fixture is a fixed
 *   absolute string, which is also what the wire carries.
 * - Two families of statement in the store are DELIBERATELY left uncovered, and neither is
 *   an omission. The first is the exhaustiveness arm at the end of each branch over a
 *   discriminated union: it is reachable in principle but not in practice, because the
 *   compiler rejects any call that could reach it — covering it would require the very type
 *   assertion the workspace forbids, and the compiler's refusal is the stronger proof. The
 *   second is the structural guard in the failure reader that rejects a thrown value which
 *   is not an object, or which carries no numeric status: the mock backend answers every
 *   failure with the framework's own error response, which always carries both, so no case
 *   written through this harness can produce that input. Anything else uncovered is a gap.
 * - The store registers no reactive side effect, so no effect-flushing step is needed;
 *   every projection below is a pull-based derivation read at the moment it is asserted.
 *
 * ---------------------------------------------------------------------------
 * MIGRATION RECORD — the legacy behaviour each group of cases pins, with the line it was
 * measured against. Every citation below was read first-hand in this repository.
 *
 * 1. VIEW STATE AND SESSION STATE ARE ELIMINATED, AND THERE WAS NOTHING TO TRANSLATE.
 *    The security admin tree contains ZERO view-state sites — as do the module and page
 *    admin trees — and session state has zero sites anywhere in the migrated surface.
 *    What `Website/admin/Security/Roles.ascx.vb` did instead was RE-BIND EVERYTHING ON
 *    EVERY POSTBACK: it re-queried the roles and the groups each time and rebound the
 *    grid at `:L91`. So this store replaces postback re-binding, not state round-tripping,
 *    and the cases assert held state that survives between commands. The one view-state
 *    key that appears elsewhere in the admin tree, the return-navigation referrer, is a
 *    router concern and deliberately never becomes a signal.
 *
 * 2. THE THREE-WAY GROUP NARROWING IS A TYPED DISCRIMINATOR, NOT A SIGNED INTEGER.
 *    `Roles.ascx.vb` declared one field at `:L48`, gave its every-role entry the value -2
 *    at `:L112` and its ungrouped entry the value -1 at `:L114`, then read the field back
 *    with TWO COMPARISONS THAT DISAGREE: `:L72` branches on the value being strictly
 *    below -1 to choose the whole-portal query, while `:L79` branches on the value merely
 *    being negative to hide the edit and delete controls. Rewriting either as the other
 *    changes behaviour. The target names the three intents instead, so neither number
 *    exists as a value — which is why no case below asserts on -2 as a narrowing.
 *
 * 3. THE MEASURED DEFAULT IS THE UNGROUPED INTENT, NOT THE EVERY-ROLE ONE.
 *    `Roles.ascx.vb:L48` initialises the field to -1, and -1 is the ungrouped narrowing
 *    per `:L114`. This CORRECTS the folder requirements, which imply -2. The value -2
 *    arises from exactly two places, neither of them initialisation: an explicit choice
 *    in the dropdown, and the no-groups fall-back at `:L129`.
 *
 * 4. DELETING A GROUP RESETS THE NARROWING TO THE UNGROUPED INTENT, NOT TO THE EVERY-ROLE
 *    ONE. `Roles.ascx.vb:L292-L295` is the third guard on the feature: `:L292` parses the
 *    selected value, `:L293` performs the delete for a real key alone, `:L294` deletes and
 *    `:L295` resets the field to -1. The numeric guard is now the type system's job — a
 *    key can be had from a real-group narrowing and from nothing else — so the reset is
 *    what remains observable, and it is asserted.
 *
 * 5. THE DELETE-WHEN-EMPTY AFFORDANCE IS AT `:L85`, NOT `:L84`. `Roles.ascx.vb:L85` sets
 *    the delete control's visibility from the listed roles being empty; `:L84` assigns the
 *    edit-group link's navigation address. The folder requirements cite `:L84`, which is
 *    the wrong line. Both halves are asserted: the affordance projection, AND that the
 *    request is issued regardless of it, because the server is the authority and answers
 *    a stale affordance with a conflict.
 *
 * 6. THE PAGING SHAPES DIFFER PER LISTING, AND MUST NOT BE MADE TO LOOK UNIFORM. Resolved
 *    from the contract rather than from prose, because the migration plan describes the
 *    role listing as paged in one place and unpaged in another. Four authorities agree
 *    that the ROLE listing is PAGED: the service's list method answers the paged envelope,
 *    the service imports that envelope, the shared query builder carries a purpose-built
 *    entry point that merges paging with the narrowing, and the store holds a page
 *    coordinate for it. The ASSIGNMENT listing is PAGED too. The ROLE-GROUP listing is
 *    UNPAGED — a plain array in the single-payload envelope, requested with no query
 *    parameters at all. The legacy screen paged NEITHER (`:L77` and `:L108` both bind
 *    untyped lists), so paging is an ADDED capability on two of the three listings, and
 *    the cases below assert a page coordinate for two and its total absence for the third.
 *
 * 7. ENDING A PAID ASSIGNMENT EXPIRES THE ROW RATHER THAN DELETING IT, SO AN EMPTY SUCCESS
 *    DOES NOT MEAN THE ROW IS GONE. This is the highest-value group in this file.
 *    `Library/Components/Security/Roles/RoleController.vb:L494` branches on the ASSIGNMENT
 *    row being paid and having used its trial — note it reads the assignment's own fee and
 *    trial flag, not the role's, which corrects the folder requirements — and in that case
 *    `:L496` back-dates the expiry bound to YESTERDAY and `:L497` writes the row back.
 *    Just the other branch, `:L500`, genuinely deletes. The endpoint answers an empty
 *    success in BOTH cases and nothing in the response distinguishes them, so the store
 *    RE-READS and never removes the row optimistically. A store that dropped the row and
 *    issued no second request would fail these cases, which is the point of them.
 *
 * 8. THE ASSIGNMENT WRITE IS AN UPDATE-OR-ADD WHOSE RESPONSE DOES NOT SAY WHICH HAPPENED.
 *    `RoleController.vb:L503` seeded an assignment key with the absent-integer marker,
 *    `:L550` looked for an existing row and updated it, and the else arm added one. The
 *    endpoint preserves that, so it legitimately answers created or no-content and the
 *    client cannot tell them apart. Both are asserted, both as successes, and the row is
 *    re-read rather than synthesised from the request — the server derives the expiry
 *    bound from its own clock and clamps the bounds it was given at `:L529-L534`.
 *
 * 9. THE SIX FREQUENCY CODES ARE PERSISTED CHARACTERS, PRESERVED VERBATIM.
 *    `RoleController.vb:L537-L547`. A period equal to the absent-integer marker means NO
 *    EXPIRY AT ALL and is tested FIRST, exactly as `:L537` orders it — it does not mean
 *    "unset, apply a default", so it is never coalesced and never made positive. The code
 *    `N` sets no expiry; `O` sets the far-future perpetual instant 9999-12-31, which is a
 *    real stored value that merely looks like a marker; `D` advances by the period, `W` by
 *    the period times seven — a scaled count of the same unit, not a distinct unit — `M`
 *    and `Y` by their own units. One vocabulary serves both the billing and the trial
 *    column. The table driving these cases is typed as the model's own union, so a folded
 *    case or a word-spelled code could not compile, let alone pass.
 *
 * 10. THE LEGACY FREQUENCY BRANCH HAD NO FALLBACK ARM. `:L540-L547` is a six-arm branch
 *     with no final catch-all, so an unrecognised code left the expiry at whatever value
 *     it already held and reported nothing. The target's classification is exhaustive over
 *     the union and ends in an unreachable-variant failure, which turns that silent wrong
 *     answer into a loud one. A deliberate improvement, not a transliteration.
 *
 * 11. FOUR SEPARATE VOCABULARIES SPELL THEMSELVES WITH THE SAME NEGATIVE NUMBERS, AND THE
 *     CASES KEEP THEM APART PAIRWISE:
 *     (a) The group NARROWING, where -2 meant every role and -1 meant the ungrouped ones
 *         (`Roles.ascx.vb:L112`, `:L114`). Now a typed intent; neither number is a value.
 *     (b) The group FIELD on a role row, where -1 meant "belongs to no group". The API
 *         publishes `null` for it — the grouping table is seeded from zero with a foreign
 *         key pointing at it, so -1 was never a stored value; the legacy reader
 *         manufactured it outbound and undid it inbound. Read with an absence check.
 *     (c) The PSEUDO-ROLE constants at `Library/Components/Shared/Globals.vb:L95-L98`,
 *         which are STRING constants — "-1" all users, "-2" superuser, "-3"
 *         unauthenticated, "-4" nothing — compared as strings at
 *         `Library/Components/Portal/PortalController.vb:L2303` and `:L2305`, never parsed
 *         to numbers. That file's path is worth stating because the migration plan cites a
 *         common-component path that does not exist; the real one is under the shared
 *         component directory.
 *     (d) The ABSENT-INTEGER marker at `Library/Components/Shared/Null.vb:L41-L45`, whose
 *         body returns -1, used as "no existing assignment row" at `RoleController.vb:L503`
 *         and as "no expiry at all" at `:L537`.
 *     A case is written per pair, and a further case asserts the store publishes no
 *     general negative-identifier normaliser, because such a helper is exactly what would
 *     collapse these four into one.
 *
 * 12. A FEE OF ZERO MEANS FREE, AND IS REAL DATA. `RoleController.vb:L494` discriminates
 *     paid from free with a strictly-greater-than-zero test, so zero falls on the free side
 *     deliberately. The fee is a single-precision column whose legacy absent-marker was the
 *     type's minimum value — neither zero nor minus one — so zero is never absence, and a
 *     recorded zero is asserted to survive as zero.
 *
 * 13. THE THREE LEGACY DISPLAY HELPERS ARE PRESENTATION AND ARE ABSENT FROM THE STORE.
 *     `Roles.ascx.vb:L152-L162` rendered a period as a phrase and `:L175` rendered a fee as
 *     currency; a third helper in `SecurityRoles.ascx.vb` rendered a date and filtered
 *     absent ones out for display. A case asserts the store publishes no such member, and
 *     would report one as a code-organisation violation rather than test it.
 *
 * 14. THE LEGACY CACHING LAYER IS NOT REPRODUCED CLIENT-SIDE. The legacy code reached a
 *     static cache helper from well over a hundred call sites across the migrated domains,
 *     with coarse portal-wide and host-wide invalidation;
 *     `Library/Components/Providers/Caching/DataCache.vb` is 317 lines of it, and the
 *     permission controllers alone accounted for 26 of those sites. The migration plan
 *     cites a shared-component path for that file, which does not exist. Caching is a
 *     server concern now, and the cases assert re-reads where the server may have diverged
 *     rather than cache hits.
 *
 * 15. AUTHORISATION IS DECIDED SERVER-SIDE. The refusal arrives as a forbidden response or
 *     a conflict, and a case asserts the store publishes no permission-deciding member.
 *     The two closed permission vocabularies — the persisted keys and the policy names —
 *     are never interchanged, and neither carries a negation prefix in this generation of
 *     the product.
 *
 * 16. A FORBIDDEN RESPONSE IS A WARNING, NOT AN ERROR, and the security tree's own file is
 *     the precedent: `Website/admin/Security/AccessDenied.ascx.vb` performs no permission
 *     check at all and renders BOTH of its branches, at `:L43` and `:L45`, as a yellow
 *     warning. A case asserts warning severity for a forbidden response and error severity
 *     for a conflict. A rate-limited response does not arise on these endpoints — it
 *     belongs to the authentication routes — so no case asserts one here.
 *
 * 17. LOCALISATION IS NOT PORTED. The legacy narrowing entries were LOCALISED strings
 *     looked up by resource key at `Roles.ascx.vb:L112` and `:L114`, which made the control
 *     flow depend on the active language. The target keys off a typed intent, so the branch
 *     is language-independent, and no case asserts on message wording the server owns.
 *
 * 18. EVERY IMPLICIT COERCION IS MADE EXPLICIT, AND ONE IS REMOVED OUTRIGHT. The legacy
 *     admin screens compiled with strictness disabled (`Website/release.config:L125`), so
 *     they could legally narrow implicitly and bind late. `Roles.ascx.vb:L292` is the
 *     concrete instance: it hands the dropdown's selected value — a string — to an
 *     unguarded integer parse, which throws on a non-numeric value rather than reporting
 *     one. The typed intent removes the parse entirely. A discrepancy worth recording,
 *     since the plan calls this an implicit narrowing: it is an EXPLICIT parse, and the
 *     defect is that it is unguarded. Note also that `Roles.ascx.vb:L111` spells the
 *     control in lower case where `:L112`, `:L118` and `:L125` capitalise it — identical in
 *     the legacy language, two different identifiers in this one.
 *
 * The repository-root migration notes document belongs to another agent and is not edited
 * from here; the record above and the completion report carry this file's contribution.
 * ---------------------------------------------------------------------------
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  DEFAULT_ROLE_GROUP_FILTER,
  MAXIMUM_ROLE_PAGES,
  ROLES_FETCH_PAGE_SIZE,
  RoleStore,
  billingTermsBound,
} from './role.store';

import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../models/paged-result.model';

import type { TestRequest } from '@angular/common/http/testing';
import type { Signal } from '@angular/core';
import type { ApiMeta, ApiResponse, PagedResponse } from '../models/paged-result.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import type {
  BillingFrequency,
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  RoleStatus,
  UpdateRoleGroupRequest,
  UpdateRoleRequest,
  UserRole,
} from '../models/role.model';
import type {
  BillingTermsBound,
  RoleGroupFilter,
  RolePageCoordinate,
  RoleStoreFailure,
  RoleStoreOperation,
} from './role.store';

// ---------------------------------------------------------------------------
// THE ADDRESSES UNDER TEST — every one root-relative
// ---------------------------------------------------------------------------

/** The role collection. Paged; accepts a creation. */
const ROLES_URL = '/api/v1/roles';

/**
 * One role, addressed with the identity seed itself.
 *
 * The role table is declared with an identity seed of zero
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115`), so
 * the FIRST role every tenant creates carries the key zero — and the shipped data seeds a
 * registered-users role at `:L7194`. A truthiness guard anywhere on the path would issue
 * no request for this address at all, which the backend verification turns into a failure.
 */
const ROLE_ZERO_URL = '/api/v1/roles/0';

/** One role, addressed with an ordinary positive key, for contrast with the seed. */
const ROLE_SEVEN_URL = '/api/v1/roles/7';

/** The accounts holding the seed-keyed role. */
const ROLE_ZERO_MEMBERS_URL = '/api/v1/roles/0/users';

/** The accounts holding an ordinarily-keyed role. Reads a page; accepts an assignment. */
const ROLE_SEVEN_MEMBERS_URL = '/api/v1/roles/7/users';

/** One account's membership of one role. */
const ROLE_SEVEN_MEMBER_URL = '/api/v1/roles/7/users/42';

/** The role-group collection. Unpaged, and parameterless. */
const ROLE_GROUPS_URL = '/api/v1/role-groups';

/** One role group, addressed with its own identity seed of zero. */
const ROLE_GROUP_ZERO_URL = '/api/v1/role-groups/0';

/**
 * The memberships-of-one-account address, for an ordinary account key.
 *
 * Nested under the ACCOUNT because the answer is one person's memberships. This is the read
 * that lets the role listing open narrowed to the person an operator was just looking at.
 */
const USER_ROLES_URL = '/api/v1/users/42/roles';

/** The same address for the account keyed nought, which no truthiness test may drop. */
const USER_ZERO_ROLES_URL = '/api/v1/users/0/roles';

/** An ordinary account key, for the per-account membership read. */
const HELD_ROLES_USER_ID = 42;

/** A second account key, so a switch of subject is provable. */
const OTHER_HELD_ROLES_USER_ID = 43;

/** The second account's address. */
const OTHER_USER_ROLES_URL = '/api/v1/users/43/roles';

// ---------------------------------------------------------------------------
// WIRE VOCABULARY
// ---------------------------------------------------------------------------

/**
 * The prefix the server wraps a failure code in before publishing it.
 *
 * The code arrives in exactly one place and it is not a member of its own: the server
 * writes it into the problem document's type member. A consumer that does not read that
 * member cannot key on a code at all, so every failure fixture below carries it.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * The conflict codes the server publishes for this feature, spelled as IT spells them.
 *
 * MIGRATION: this CORRECTS the folder requirements, which assert three capitalised
 * identifiers descended from the legacy enumeration member names. Those values could
 * never match anything taken off the wire. The wording the migration has to preserve is
 * preserved by the shared failure catalogue; the KEY is what the server sends, and these
 * are the three keys that reach role administration — a duplicate role name, a duplicate
 * role-group name, and a protected assignment whose legacy wording refused to strip the
 * portal administrator or the registered-users role.
 */
const CONFLICT_CODE = Object.freeze({
  duplicateRoleName: 'role.name_duplicate',
  duplicateRoleGroupName: 'role_group.name_duplicate',
  protectedAssignment: 'role_assignment.protected',
} as const);

/**
 * A fixed correlation value, shaped like the trace parent the server derives one from.
 *
 * Fixed rather than generated, because a generated value would make the case
 * irreproducible. It is the operator's single join key between a browser report and a
 * server log, so its survival into the held failure is asserted rather than assumed.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/** A second fixed correlation value, for proving which of the two members wins. */
const CORRELATION_ID = 'e7bf0e34-9c1a-4f2f-9c0e-4a1d5c8b2f10';

/**
 * The absent-instant marker, as the wire spells it.
 *
 * `Library/Components/Shared/Null.vb:L66-L70` returns the minimum representable date for
 * an absent instant rather than nothing at all, so a row carrying this value means "no
 * expiry" and is DATA. Normalising it to absence would discard that distinction.
 */
const MIN_INSTANT = '0001-01-01T00:00:00Z';

/**
 * The perpetual instant the one-time-fee code produces.
 *
 * `RoleController.vb:L542` assigns 9999-12-31 for that code. A real stored value that
 * merely looks like a marker; neither absence nor an error.
 */
const PERPETUAL_INSTANT = '9999-12-31T00:00:00Z';

/** An instant already in the past, which is what a back-dated expiry bound looks like. */
const PAST_INSTANT = '2019-03-14T00:00:00Z';

/** An instant in the future, for an assignment that has not lapsed. */
const FUTURE_INSTANT = '2099-06-01T00:00:00Z';

/**
 * The six persisted frequency codes, typed as the model's own union.
 *
 * Typing the table this way is what makes a folded case, a word-spelled unit or an
 * integer substitution a COMPILE failure rather than a runtime surprise.
 */
const FREQUENCY_CODES: readonly BillingFrequency[] = Object.freeze([
  'N',
  'O',
  'D',
  'W',
  'M',
  'Y',
] as const);

/**
 * The assignment-status vocabulary the model declares.
 *
 * Present in the contract module but a member of NEITHER the role NOR the membership
 * contract, so no held value can carry one — which is itself the assertion: the store
 * does not re-derive a status the server never sent. Its members are disjoint from the
 * store's own term classification, and a case proves the two are not confused.
 */
const ROLE_STATUS_VALUES: readonly RoleStatus[] = Object.freeze([
  'Pending',
  'Active',
  'Expired',
] as const);

/**
 * The legacy pseudo-role identifiers, as STRING constants.
 *
 * `Library/Components/Shared/Globals.vb:L95-L98`. They are a third, independent negative
 * vocabulary: not rows in the role table, not group keys, and never parsed to numbers.
 * Held here as strings, which is what they are, so a case can prove the store neither
 * parses one into the narrowing space nor confuses the superuser constant with the
 * every-role intent.
 */
const LEGACY_PSEUDO_ROLE_IDS: readonly string[] = Object.freeze([
  '-1',
  '-2',
  '-3',
  '-4',
] as const);

// ---------------------------------------------------------------------------
// FIXTURE FACTORIES
// ---------------------------------------------------------------------------
//
// Each is a FUNCTION rather than a shared constant, so no case can mutate a value another
// case depends on. Each default deliberately carries the awkward values rather than tidy
// ones — a key of zero, an absent-integer period, a fee of zero, empty strings and false
// booleans — because the server serialises with no ignore condition at all and every one
// of those arrives on the wire as DATA.
//
// The member spellings are taken from the contract module, not guessed. They carry a
// single lower-case letter in the identity suffix, and a mis-spelled fixture key would
// yield nothing at run time with no compile complaint, so the return type is annotated on
// every factory to force the compiler to check the spelling for us.

/**
 * One row of the role listing.
 *
 * Defaults to the seed key zero and to the plural administrator name the shipped data
 * actually creates (`Library/Components/Portal/PortalController.vb:L1390` creates
 * "Administrators" with a positional monthly billing code, a no-expiry trial code and two
 * positional false booleans; `:L1393` creates the registered-users role). No case compares
 * a role NAME for behaviour — the server decides names — the name is fixture noise.
 *
 * Note what the listing contract does NOT carry: the grouping key. That is deliberate, so
 * grouping is observable on the detail contract alone.
 *
 * @param overrides Members to replace on the default row.
 * @returns One listing row.
 */
function aRoleListItem(overrides: Partial<RoleListItem> = {}): RoleListItem {
  return {
    roleId: 0,
    roleName: 'Administrators',
    description: 'Portal Administrators',
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    isPublic: false,
    autoAssignment: false,
    ...overrides,
  };
}

/**
 * One role in full, as the detail endpoint publishes it.
 *
 * The grouping key defaults to absence, which is the form the API publishes for a role
 * belonging to no group — not the legacy manufactured minus one.
 *
 * @param overrides Members to replace on the default role.
 * @returns One role.
 */
function aRole(overrides: Partial<Role> = {}): Role {
  return {
    roleId: 0,
    roleGroupId: null,
    roleName: 'Administrators',
    description: 'Portal Administrators',
    billingFrequency: 'M',
    serviceFee: 0,
    trialFrequency: 'N',
    trialPeriod: -1,
    billingPeriod: -1,
    trialFee: 0,
    isPublic: false,
    autoAssignment: false,
    rsvpCode: '',
    iconFile: null,
    ...overrides,
  };
}

/**
 * One role group.
 *
 * The tenant key defaults to minus one, which is a REAL tenant: the portal table is
 * declared with an identity seed of minus one
 * (`01.00.00.SqlDataProvider:L77`), so the first tenant ever created carries minus one and
 * the second carries zero — while the absent-integer marker is also minus one. Both of
 * those values are asserted to survive on this member.
 *
 * @param overrides Members to replace on the default group.
 * @returns One role group.
 */
function aRoleGroup(overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId: 0,
    portalId: -1,
    roleGroupName: 'Site Groups',
    description: '',
    ...overrides,
  };
}

/**
 * One user-to-role assignment.
 *
 * Both instants default to fixed absolute strings. The membership contract carries neither
 * a fee nor a trial-usage flag nor a status, which is precisely why the store cannot
 * classify a removal's outcome and must re-read instead.
 *
 * @param overrides Members to replace on the default assignment.
 * @returns One assignment.
 */
function anAssignment(overrides: Partial<UserRole> = {}): UserRole {
  return {
    userRoleId: 1,
    userId: 42,
    username: 'host',
    displayName: 'SuperUser Account',
    roleId: 7,
    roleName: 'Subscribers',
    effectiveDate: MIN_INSTANT,
    expiryDate: FUTURE_INSTANT,
    ...overrides,
  };
}

/**
 * A paged wire envelope.
 *
 * The total is stated independently of the page's length, because a consumer that read the
 * array's length would page wrongly the moment a second page existed.
 *
 * @param items The page's rows.
 * @param totalCount The total across every page.
 * @param pageIndex The zero-based index of this page.
 * @returns The paged envelope.
 */
function pageOf<T>(
  items: readonly T[],
  totalCount: number,
  pageIndex = 0,
): PagedResponse<T> {
  const pageSize = DEFAULT_PAGE_SIZE;
  const meta: ApiMeta = {
    totalCount,
    pageIndex,
    pageSize,
    totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize),
  };

  return { items, meta };
}

/**
 * A single-payload wire envelope.
 *
 * @param data The payload.
 * @returns The envelope, with no paging metadata.
 */
function envelopeOf<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A problem document, shaped as the server's own exception handler shapes one.
 *
 * The status is written into the BODY as well as onto the response, because severity
 * resolves from the document's own status member and a body without one would resolve to
 * the default severity for a reason unrelated to the case under test.
 *
 * @param status The status to report, in the body and expected on the response.
 * @param code The failure code, or absence for a failure the server did not classify.
 * @param detail The human-readable explanation, held verbatim and never laundered.
 * @param extra Further standard members to merge in.
 * @returns The document.
 */
function aProblem(
  status: number,
  code: string | null,
  detail: string,
  extra: Partial<ProblemDetails> = {},
): ProblemDetails {
  const base: ProblemDetails = {
    title: 'Request refused',
    status,
    detail,
    instance: ROLES_URL,
    traceId: TRACE_ID,
  };

  return code === null ? { ...base, ...extra } : { ...base, type: `${FAILURE_TYPE_PREFIX}${code}`, ...extra };
}

/**
 * A model-state refusal, carrying the per-member dictionary.
 *
 * The dictionary's keys are the server's own model-state keys and are NOT lower-camel: it
 * publishes them as the request contract declares them. The dictionary is an index
 * signature and the workspace's compiler settings refuse dotted access to one by design,
 * so every read of it below is a bracket read.
 *
 * @param fieldErrors The per-member messages.
 * @param detail The overall explanation.
 * @returns The validation document.
 */
function aValidationProblem(
  fieldErrors: Readonly<Record<string, readonly string[]>>,
  detail: string,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.rejected`,
    title: 'One or more validation errors occurred.',
    status: 400,
    detail,
    instance: ROLES_URL,
    traceId: TRACE_ID,
    errors: fieldErrors,
  };
}

// ---------------------------------------------------------------------------
// ASSERTION HELPERS
// ---------------------------------------------------------------------------

/**
 * Narrows away absence by throwing, so no case needs a cast or a suppression comment.
 *
 * The workspace forbids the non-null assertion operator, and reaching for one here would
 * hide the very distinction several of these cases exist to prove — that absence is a
 * distinct value and not a stand-in for zero or minus one. Throwing fails the case with a
 * message naming what was missing, which is strictly more useful than a type assertion.
 *
 * @param value The value to narrow.
 * @param what What was expected, for the failure message.
 * @returns The value, narrowed.
 */
function present<T>(value: T | null | undefined, what: string): T {
  if (value === null || value === undefined) {
    throw new Error(`Expected ${what} to be present, but it was absent.`);
  }

  return value;
}

/**
 * Every member name the store publishes, instance members and methods alike.
 *
 * Reads both the instance's own members — where the signal projections live, since they
 * are assigned in field initialisers — and the prototype's, where the commands live. Used
 * by the cases that assert what the store deliberately does NOT publish.
 *
 * @param store The store to enumerate.
 * @returns Every own and prototype member name, the constructor excluded.
 */
function publishedMembers(store: RoleStore): readonly string[] {
  const own: readonly string[] = Object.getOwnPropertyNames(store);
  const inherited: readonly string[] = Object.getOwnPropertyNames(
    RoleStore.prototype,
  ).filter((name) => name !== 'constructor');

  return [...own, ...inherited];
}

describe('RoleStore', () => {
  let store: RoleStore;
  let httpMock: HttpTestingController;

  /**
   * Claims the single open request at one PATH, issued with one verb.
   *
   * Always the predicate form, and always matched on the verb and the PATH rather than by
   * whole-string comparison. The backend's string matcher compares the address WITH its
   * query, so a bare address would fail to match any paged read at all and would couple
   * every other assertion to the order parameters happen to be appended in. Matching the
   * verb as well is what keeps a write and the read that follows it on the same address —
   * a creation and then the listing — from being claimed in the wrong order.
   *
   * @param method The verb the request must carry.
   * @param url The path the request must address, without its query.
   * @returns The one matching open request.
   */
  function expectRequest(method: string, url: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
    );
  }

  /**
   * Claims the single open read at one path.
   *
   * @param url The path the read must address.
   * @returns The one matching open read.
   */
  function expectGet(url: string): TestRequest {
    return expectRequest('GET', url);
  }

  /**
   * Claims the single open creation at one path.
   *
   * @param url The path the creation must address.
   * @returns The one matching open creation.
   */
  function expectPost(url: string): TestRequest {
    return expectRequest('POST', url);
  }

  /**
   * Claims the single open replacement at one path.
   *
   * @param url The path the replacement must address.
   * @returns The one matching open replacement.
   */
  function expectPut(url: string): TestRequest {
    return expectRequest('PUT', url);
  }

  /**
   * Claims the single open removal at one path.
   *
   * @param url The path the removal must address.
   * @returns The one matching open removal.
   */
  function expectDelete(url: string): TestRequest {
    return expectRequest('DELETE', url);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // The real client FIRST and the testing backend SECOND. The testing provider
        // overrides the backend the real one installed, so the order is not cosmetic:
        // reversed, the genuine backend survives and these cases attempt live requests.
        provideHttpClient(),
        provideHttpClientTesting(),
        // Listed explicitly so each case runs against a freshly constructed store whose
        // slices start at their initial values, independent of the decorator's own root
        // registration. The store's collaborator resolves from the root injector either
        // way, so nothing about the composition under test changes.
        RoleStore,
      ],
    });

    store = TestBed.inject(RoleStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The load-bearing assertion of this file. Fails on any request opened and never
    // consumed, which is what proves a command issued EXACTLY the requests it should —
    // and it is the mechanism by which a missing re-read after a removal, or a request
    // suppressed by a truthiness guard on a key of zero, becomes a visible failure rather
    // than a silent pass.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // THE PUBLISHED SURFACE, AND ITS READ-ONLY GUARANTEE
  // -------------------------------------------------------------------------

  describe('the published surface and its read-only guarantee', () => {
    it('publishes every state slice as a signal that cannot be written from outside', () => {
      // A writable signal carries a setter and an updater; a read-only projection carries
      // neither. Presence is therefore the whole proof, and it needs no cast — reaching
      // for one to attempt a write would require exactly the type assertion the
      // workspace's settings forbid, and would prove less.
      const projections: readonly (readonly [string, Signal<unknown>])[] = [
        ['roles', store.roles],
        ['roleGroups', store.roleGroups],
        ['groupFilter', store.groupFilter],
        ['selectedRole', store.selectedRole],
        ['assignments', store.assignments],
        ['assignmentsRoleId', store.assignmentsRoleId],
        ['rolesPage', store.rolesPage],
        ['assignmentsPage', store.assignmentsPage],
        ['rolesLoading', store.rolesLoading],
        ['roleGroupsLoading', store.roleGroupsLoading],
        ['selectedRoleLoading', store.selectedRoleLoading],
        ['assignmentsLoading', store.assignmentsLoading],
        ['saving', store.saving],
        ['failure', store.failure],
        ['rolesHeldByUser', store.rolesHeldByUser],
        ['heldRolesUserId', store.heldRolesUserId],
        ['heldRolesLoading', store.heldRolesLoading],
      ];

      for (const [name, projection] of projections) {
        expect('set' in projection)
          .withContext(`${name} must not expose a setter to a consumer`)
          .toBeFalse();
        expect('update' in projection)
          .withContext(`${name} must not expose an updater to a consumer`)
          .toBeFalse();
      }
    });

    it('publishes every derived projection as a signal that cannot be written from outside', () => {
      const derived: readonly (readonly [string, Signal<unknown>])[] = [
        ['roleItems', store.roleItems],
        ['rolesMeta', store.rolesMeta],
        ['assignmentItems', store.assignmentItems],
        ['assignmentsMeta', store.assignmentsMeta],
        ['probedAssignment', store.probedAssignment],
        ['probedAssignmentKey', store.probedAssignmentKey],
        ['busy', store.busy],
        ['hasRoleGroups', store.hasRoleGroups],
        ['selectedRoleGroupId', store.selectedRoleGroupId],
        ['selectedRoleGroup', store.selectedRoleGroup],
        ['canDeleteSelectedGroup', store.canDeleteSelectedGroup],
        ['selectedRoleBillingTerms', store.selectedRoleBillingTerms],
        ['selectedRoleTrialTerms', store.selectedRoleTrialTerms],
        ['selectedRoleIsPaid', store.selectedRoleIsPaid],
      ];

      for (const [name, projection] of derived) {
        expect('set' in projection)
          .withContext(`${name} is derived and must not be writable`)
          .toBeFalse();
        expect('update' in projection)
          .withContext(`${name} is derived and must not be writable`)
          .toBeFalse();
      }
    });

    it('publishes every command this specification exercises', () => {
      const commands: readonly string[] = [
        'loadRoleAdministration',
        'loadRoles',
        'loadRoleGroups',
        'selectRole',
        'clearSelectedRole',
        'loadAssignments',
        'reloadAssignments',
        'probeAssignment',
        'clearProbedAssignment',
        'setGroupFilter',
        'setRolesSort',
        'setRolesQuery',
        'setAssignmentsPage',
        'createRole',
        'updateRole',
        'deleteRole',
        'assignUser',
        'removeAssignment',
        'createRoleGroup',
        'updateRoleGroup',
        'deleteRoleGroup',
        'loadRolesHeldByUser',
        'clearRolesHeldByUser',
        'clearError',
        'reset',
      ];
      const published: readonly string[] = publishedMembers(store);

      for (const command of commands) {
        expect(published.includes(command))
          .withContext(`${command} must be part of the store's public contract`)
          .toBeTrue();
      }
    });

    it('starts every slice at its documented initial value', () => {
      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.assignmentItems()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentsRoleId()).toBeNull();
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasRoleGroups()).toBeFalse();

      // No account is the subject until one is asked for, and `null` is that fact. It is a
      // DIFFERENT fact from an account that holds no roles, which is the empty sequence.
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // THE ROLES ONE ACCOUNT HOLDS
  //
  // MIGRATION: `SecurityRoles.ascx.vb:L413-L418` served two modes from one screen, keyed by
  // either a role or an account, and `Users.ascx.vb:L542` reached the account-keyed mode from
  // the account listing. This slice is the account-keyed mode: an unpaged answer to "what does
  // this person hold", held apart from the browsable listing so neither overwrites the other.
  // -------------------------------------------------------------------------

  describe('the roles one account holds', () => {
    it('reads the memberships at the account address and publishes them in their own slice', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })]),
      );

      expect(store.rolesHeldByUser()).toEqual([
        aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
      ]);
      expect(store.heldRolesUserId()).toBe(HELD_ROLES_USER_ID);
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('leaves the BROWSABLE listing completely alone, so neither answer overwrites the other', () => {
      // ⚠ THE TWO ANSWERS ARE DIFFERENT QUESTIONS. The listing is paged, ordered and
      // filterable; this is unpaged. Landing the narrowed answer in `roles` would put rows the
      // pager cannot account for into a slice whose meta says otherwise.
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 7, roleName: 'Subscribers' })], 1));

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })]),
      );

      expect(store.roleItems().map((each) => each.roleName)).toEqual(['Subscribers']);
      expect(store.rolesMeta().totalCount).toBe(1);
      expect(present(store.rolesHeldByUser(), 'the narrowed answer').map((e) => e.roleName)).toEqual(
        ['Administrators'],
      );
    });

    it('addresses an account key of NOUGHT rather than dropping it from the path', () => {
      store.loadRolesHeldByUser(0);

      expectGet(USER_ZERO_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      expect(store.heldRolesUserId()).toBe(0);
      expect(store.rolesHeldByUser()).toHaveSize(1);
    });

    it('records the account at DISPATCH, so a screen knows who is being waited for', () => {
      // Recording it on arrival would leave a screen unable to distinguish "waiting for this
      // person" from "waiting, subject unknown", and it would keep the PREVIOUS subject on
      // screen for the whole of the new read.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.heldRolesUserId())
        .withContext('the subject is known before the answer arrives')
        .toBe(HELD_ROLES_USER_ID);
      expect(store.heldRolesLoading()).toBeTrue();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('holds an account that belongs to NO role as the empty sequence, not as null', () => {
      // ⚠ THE TWO ARE DIFFERENT FACTS. `null` means no account is the subject; the empty
      // sequence means this account holds nothing. A screen that conflated them would fall
      // back to the unnarrowed listing for a person who genuinely holds no role.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.rolesHeldByUser()).toEqual([]);
      expect(store.rolesHeldByUser()).not.toBeNull();
      expect(store.heldRolesUserId()).toBe(HELD_ROLES_USER_ID);
    });

    it('ABANDONS the narrowing when the read fails, rather than showing an empty membership set', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(aProblem(403, null, 'Forbidden'), {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(store.rolesHeldByUser())
        .withContext('an empty set would read as "this account holds nothing"')
        .toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
      expect(present(store.failure(), 'the reported failure').status).toBe(403);
    });

    it('cancels a read in flight before starting another, so the last subject asked for wins', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const abandoned = expectGet(USER_ROLES_URL);

      store.loadRolesHeldByUser(OTHER_HELD_ROLES_USER_ID);

      expect(abandoned.cancelled)
        .withContext('the first read is released rather than left to answer later')
        .toBeTrue();
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);

      expectGet(OTHER_USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 7 })]));

      expect(present(store.rolesHeldByUser(), 'the answer').map((each) => each.roleId)).toEqual([7]);
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);
    });

    it('DISCARDS the previous account\u2019s answer the moment the subject changes', () => {
      // ⚠ THE WINDOW THIS CLOSES. The subject is recorded at dispatch, so without this a consumer
      // comparing "which account is the subject" against "is there an answer" found them in
      // agreement while the collection still described the PREVIOUS account — and presented one
      // person's memberships, and their count, under another person's name.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));
      expect(store.rolesHeldByUser()).toHaveSize(1);

      store.loadRolesHeldByUser(OTHER_HELD_ROLES_USER_ID);

      expect(store.rolesHeldByUser())
        .withContext('no answer is held for the new subject until it answers')
        .toBeNull();
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);

      expectGet(OTHER_USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 7 })]));

      expect(present(store.rolesHeldByUser(), 'the new answer').map((each) => each.roleId)).toEqual([
        7,
      ]);
    });

    it('KEEPS the answer when the SAME account is re-read, so a refresh does not blank it', () => {
      // The counterpart to the case above, and the reason the discard is conditional. An answer for
      // the account still on screen is not wrong, so discarding it would blank the very listing the
      // refresh exists to update.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.rolesHeldByUser())
        .withContext('the account on screen keeps its rows while they are re-read')
        .toHaveSize(1);

      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0 }), aRoleListItem({ roleId: 7 })]),
      );

      expect(store.rolesHeldByUser()).toHaveSize(2);
    });

    it('stops treating any account as the subject on request, without contacting the server', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      store.clearRolesHeldByUser();

      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('cancels a read in flight when the narrowing is cleared, so no answer re-narrows it', () => {
      // ⚠ A RESPONSE ARRIVING AFTER THE CLEARING WOULD RE-NARROW THE LISTING with no command
      // on screen to explain it. Discarding the answer is not enough; the read is released.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const inFlight = expectGet(USER_ROLES_URL);

      store.clearRolesHeldByUser();

      expect(inFlight.cancelled).toBeTrue();
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('counts itself busy while the membership read is outstanding', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.busy())
        .withContext('a screen must be able to show progress for this read too')
        .toBeTrue();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.busy()).toBeFalse();
    });

    it('returns the narrowing to its initial values on a reset, and releases the read', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const inFlight = expectGet(USER_ROLES_URL);

      store.reset();

      expect(inFlight.cancelled).toBeTrue();
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('publishes the narrowed answer read-only, so a consumer cannot write it', () => {
      expect('set' in store.rolesHeldByUser).toBeFalse();
      expect('update' in store.rolesHeldByUser).toBeFalse();
      expect('set' in store.heldRolesUserId).toBeFalse();
      expect('set' in store.heldRolesLoading).toBeFalse();
    });

    it('clears a previously reported failure when a new membership read begins', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(aProblem(500, null, 'Server Error'), {
        status: 500,
        statusText: 'Server Error',
      });
      expect(store.failure()).not.toBeNull();

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.failure())
        .withContext('a fresh attempt is not presented beside the previous failure')
        .toBeNull();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));
    });
  });

  // -------------------------------------------------------------------------
  // ROLE IDENTITY — A KEY OF ZERO IS A REAL ROLE
  // -------------------------------------------------------------------------

  describe('role identity: a key of zero is a real role, and absence is a distinct value', () => {
    it('issues a request for the role keyed zero rather than treating the key as absent', () => {
      store.selectRole(0);

      // Matched on verb and PATH rather than by whole-string comparison, because the
      // backend's string matcher compares the address WITH its query and would couple the
      // assertion to parameter order.
      const request = expectGet(ROLE_ZERO_URL);

      expect(request.request.url)
        .withContext('a truthiness guard on the key would issue no request at all')
        .toBe(ROLE_ZERO_URL);

      request.flush(envelopeOf(aRole({ roleId: 0 })));

      expect(present(store.selectedRole(), 'the selected role').roleId).toBe(0);
    });

    it('holds a role keyed zero unchanged, rather than dropping or defaulting it', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, roleName: 'Registered Users' })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleId).toBe(0);
      expect(held.roleName).toBe('Registered Users');
    });

    it('retains the seed key alongside ordinary keys through every derivation', () => {
      store.loadRoles();
      httpMock
        .expectOne((candidate) => candidate.url === ROLES_URL)
        .flush(
          pageOf(
            [
              aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
              aRoleListItem({ roleId: 1, roleName: 'Registered Users' }),
              aRoleListItem({ roleId: 2, roleName: 'Subscribers' }),
            ],
            3,
          ),
        );

      // The derivation must be a projection and not a filter: a row whose key is the seed
      // is indistinguishable from a row whose key is absent to any predicate that tests
      // the key for truth, and the first role of every tenant is exactly that row.
      expect(store.roleItems().map((item) => item.roleId)).toEqual([0, 1, 2]);
      expect(store.roles().items.map((item) => item.roleId)).toEqual([0, 1, 2]);
      expect(store.rolesMeta().totalCount).toBe(3);
    });

    it('reads the assignments of the role keyed zero', () => {
      store.loadAssignments(0);

      const request = expectGet(ROLE_ZERO_MEMBERS_URL);

      request.flush(pageOf([anAssignment({ roleId: 0 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
      expect(present(store.assignmentItems()[0], 'the first assignment').roleId).toBe(0);
    });

    it('treats a scope of zero as present when re-reading, so the re-read is issued', () => {
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.reloadAssignments();

      // The second request is the whole assertion: the scope in hand is zero, and a
      // truthiness guard would treat it as "no scope" and issue nothing.
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
    });

    it('expresses "nothing in scope" as absence, and never as zero or a negative key', () => {
      const scope: number | null = store.assignmentsRoleId();

      expect(scope).toBeNull();
      expect(scope).not.toBe(0);
      expect(scope).not.toBe(-1);
      expect(scope).not.toBe(-2);
      expect(store.selectedRole()).toBeNull();
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('issues no request when re-reading with nothing in scope', () => {
      store.reloadAssignments();

      // Asserted by the backend verification in the teardown: an unconsumed request would
      // fail it. Stated here as well so the intent is visible at the point of the case.
      httpMock.expectNone(() => true);
      expect(store.assignmentsRoleId()).toBeNull();
    });

    it('retains a tenant key of minus one and a tenant key of zero, both being real tenants', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, portalId: -1 }),
            aRoleGroup({ roleGroupId: 1, portalId: 0 }),
          ]),
        );

      // The tenant is not a query parameter on this feature at all — the server resolves it
      // from the request host and the caller's claims — so the honest place to prove both
      // values survive is the payload member that carries them. Minus one is the FIRST
      // tenant the shipped schema creates and is simultaneously the absent-integer marker;
      // zero is the second. Neither may be read as absence.
      expect(store.roleGroups().map((group) => group.portalId)).toEqual([-1, 0]);
    });
  });

  // -------------------------------------------------------------------------
  // THE THREE-WAY GROUP NARROWING
  // -------------------------------------------------------------------------

  describe('the three-way group narrowing, and its two separate meanings of minus one', () => {
    it('starts at the ungrouped intent, which is the measured legacy default', () => {
      // MIGRATION: `Roles.ascx.vb:L48` initialises the field to -1, and -1 is the ungrouped
      // entry per `:L114`. This CORRECTS the folder requirements, which imply the
      // every-role intent. The every-role value arises from the dropdown or from the
      // no-groups fall-back at `:L129`, never from initialisation.
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.groupFilter().kind).not.toBe('AllRoles');
      expect(DEFAULT_ROLE_GROUP_FILTER.kind).toBe('GlobalRoles');
    });

    it('sends the every-role scope and no grouping key for the every-role intent', () => {
      store.setGroupFilter({ kind: 'AllRoles' });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('scope')).toBe('All');
      // The pseudo-intent travels as a NAMED scope, never as a magic grouping key, so the
      // legacy every-role value cannot reach a persisted member.
      expect(request.request.params.has('roleGroupId'))
        .withContext('a pseudo-intent must not be encoded as a grouping key')
        .toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends the ungrouped scope and no grouping key for the ungrouped intent', () => {
      store.setGroupFilter({ kind: 'GlobalRoles' });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('scope')).toBe('Ungrouped');
      expect(request.request.params.has('roleGroupId')).toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends the grouping key zero as a literal, because the grouping table also seeds at zero', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });

      const request = expectGet(ROLES_URL);

      // The second place on this feature where a truthiness guard would silently break: the
      // first group any tenant creates carries the key zero.
      expect(request.request.params.get('roleGroupId')).toBe('0');
      expect(request.request.params.has('scope'))
        .withContext('a real key and a named scope together is a contradiction the server refuses')
        .toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('publishes the grouping key for a real group and absence for either pseudo-intent', () => {
      expect(store.selectedRoleGroupId()).toBeNull();

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroupId())
        .withContext('the key zero is a real group and must not read as absence')
        .toBe(0);

      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('does not read minus one as absence when it arrives as a grouping key', () => {
      // Vocabulary (a) against vocabulary (d): the narrowing's key space against the
      // absent-integer marker. The store forwards what it was given, so a negative key
      // stays a negative NUMBER and does not collapse to absence.
      store.setGroupFilter({ kind: 'Group', roleGroupId: -1 });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('roleGroupId')).toBe('-1');
      request.flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBe(-1);
      expect(store.selectedRoleGroupId()).not.toBeNull();
    });

    it('resolves the narrowing against the loaded groups, and to absence for an unknown key', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(present(store.selectedRoleGroup(), 'the resolved group').roleGroupName).toBe(
        'Site Groups',
      );

      store.setGroupFilter({ kind: 'Group', roleGroupId: 99 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroup())
        .withContext('a key the loaded listing does not contain resolves to absence')
        .toBeNull();
    });

    it('does not confuse the ungrouped NARROWING with the ungrouped FIELD on a role row', () => {
      // The pairing this feature is most likely to get wrong. The narrowing is a question
      // about the listing; the grouping key on a role row is a fact about that row. The
      // legacy design spelled both minus one, so proving they stay apart matters.
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: -1 })));

      store.setGroupFilter({ kind: 'GlobalRoles' });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleGroupId)
        .withContext('setting the narrowing must not rewrite a role row')
        .toBe(-1);
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      // And the converse: a row whose grouping key is minus one is not the every-role
      // intent, which the narrowing continues to report as its own named value.
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('retains an absent grouping key as absence rather than manufacturing a negative one', () => {
      // MIGRATION: the API publishes absence for a role belonging to no group. The legacy
      // reader manufactured minus one outbound and undid it inbound, and the grouping table
      // is seeded from zero with a foreign key pointing at it, so minus one was never a
      // stored value. Absence and minus one are therefore distinguishable, and both survive.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, roleGroupId: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleGroupId).toBeNull();
      expect(held.roleGroupId).not.toBe(-1);
      expect(held.roleGroupId).not.toBe(0);
    });

    it('falls back to the every-role intent when the tenant declares no group', () => {
      // Legacy: `Roles.ascx.vb:L129` assigns the every-role value in the else arm of the
      // group-count test, unconditionally, so it overrides a chosen group as well as the
      // default.
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      expect(store.hasRoleGroups()).toBeFalse();
      expect(store.groupFilter().kind).toBe('AllRoles');
    });

    it('applies the no-group fall-back BEFORE reading the roles, not after', () => {
      // The ordering is the assertion. Reading the two listings concurrently would race the
      // override and could read the roles under a narrowing the tenant cannot support.
      store.loadRoleAdministration();

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope'))
        .withContext('the roles must be read under the overridden narrowing')
        .toBe('All');

      rolesRequest.flush(pageOf([aRoleListItem()], 1));

      expect(store.groupFilter().kind).toBe('AllRoles');
    });

    it('leaves an already-every-role narrowing exactly as it is when no group is declared', () => {
      // The fall-back is idempotent, so a narrowing that already names every role is not
      // reassigned. Worth pinning because a reassignment here would replace an equal value with
      // a new object and wake every consumer of the narrowing for no reason.
      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      const before: RoleGroupFilter = store.groupFilter();

      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      expect(store.groupFilter().kind).toBe('AllRoles');
      expect(store.groupFilter())
        .withContext('an idempotent fall-back must not replace an equal narrowing')
        .toBe(before);
    });

    it('resolves no group for either pseudo-intent, even with groups loaded', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.selectedRoleGroup())
        .withContext('a pseudo-intent names no group, so none resolves')
        .toBeNull();

      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroup()).toBeNull();
    });

    it('leaves the narrowing alone when the tenant does declare a group', () => {
      store.loadRoleAdministration();

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope')).toBe('Ungrouped');
      rolesRequest.flush(pageOf([aRoleListItem()], 1));

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.hasRoleGroups()).toBeTrue();
    });

    it('re-reads the whole listing from the first page when the narrowing changes', () => {
      store.loadRoles();
      // A self-consistent single page: one row against a total of one, so the walk completes on
      // this response. An earlier revision of this fixture reported a total of FORTY against one
      // row, which the walk of the day accepted as complete because it stopped on a short page —
      // the very behaviour this file now refuses. See the shortfall case further down.
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      store.setGroupFilter({ kind: 'AllRoles' });

      const request = expectGet(ROLES_URL);

      // A different narrowing yields a different result set, and the walk that reads it
      // always starts at the first page.
      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesPage().pageIndex).toBe(0);
    });
  });

  // -------------------------------------------------------------------------
  // THE PAGING SHAPES — TWO PAGED LISTINGS, ONE UNPAGED
  // -------------------------------------------------------------------------

  describe('paging shapes: the roles and the assignments are paged, the groups are not', () => {
    it('reads the role listing from the first page at the largest legal request size', () => {
      store.loadRoles();

      const request = expectGet(ROLES_URL);

      // Zero-based, which is the base the API both accepts and reports, so an index sent
      // may be compared with an index read back without arithmetic. The legacy screens that
      // did page converted a one-based control index by subtracting one; nothing here is
      // one-based, so nothing here needs that conversion.
      expect(request.request.params.get('pageIndex')).toBe('0');
      // The server's own maximum, not a comfortable number: the paging validator refuses a
      // larger page. It reduces the number of round trips the walk has to make and is NOT by
      // itself the completeness guarantee - see the walk test below.
      expect(request.request.params.get('pageSize')).toBe(String(ROLES_FETCH_PAGE_SIZE));
      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize', 'scope']);

      // The server's own record total is what ends the walk: one row against a total of one means
      // everything it said exists is in hand, so this single response completes it.
      //
      // ⚠ AN EARLIER REVISION OF THIS CASE ENCODED THE DEFECT RATHER THAN THE CONTRACT. It flushed
      // one row against a reported total of a hundred and thirty-seven, asserted that the walk
      // ended there because "a short page is the last page by definition", and then asserted the
      // published total was 137 on the reasoning that "a shortfall stays visible rather than
      // passing as complete". The screen this slice feeds has no pager and renders no total, so
      // nothing made that shortfall visible to anybody: one role was presented as the tenant's
      // whole set of roles. A short page is no longer a termination condition, and a server that
      // stops short of its own total now produces a refusal - see the case below.
      request.flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesMeta().pageIndex)
        .withContext('one envelope holding everything reports the unpaged coordinate')
        .toBe(0);
      expect(store.rolesMeta().totalCount)
        .withContext("the SERVER's total, which a completed walk has necessarily satisfied")
        .toBe(1);
      expect(store.roleItems().length).toBe(1);
    });

    it('refuses the listing rather than publishing a subset when the server stops short', () => {
      // ⚠ THE M-3 REGRESSION, PINNED. The server reports far more roles than it supplies and then
      // answers with an empty page. Publishing the rows in hand would present them as the whole
      // listing on a pager-less screen, so the walk raises instead.
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 137));

      const second = expectGet(ROLES_URL);

      expect(second.request.params.get('pageIndex'))
        .withContext('a reported total larger than the rows supplied warrants another page')
        .toBe('1');

      second.flush(pageOf([], 137, 1));

      expect(store.rolesLoading()).toBeFalse();
      expect(store.roleItems())
        .withContext('nothing is published, because a subset would look like the whole listing')
        .toEqual([]);
      expect(store.failure()?.operation).toBe('loadRoles');
      expect(store.failure()).not.toBeNull();
    });

    it('stops at the page ceiling by refusing rather than by truncating', () => {
      // ⚠ A SERVER WHOSE TOTAL IS NEVER REACHED. Every page carries a row and the reported total
      // stays out of reach, so neither of the walk's successful endings can ever fire. The walk
      // must not run for ever, and it must not quietly publish what it managed to collect either.
      store.loadRoles();

      let issued = 0;
      let inFlightMoreThanOne = 0;

      for (;;) {
        const outstanding = httpMock.match(
          (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
        );

        if (outstanding.length === 0) {
          break;
        }

        if (outstanding.length > 1) {
          inFlightMoreThanOne += 1;
        }

        issued += outstanding.length;
        outstanding.forEach((request) => {
          const pageIndex = Number(request.request.params.get('pageIndex') ?? '0');

          // One row per page against an unreachable total. Cheap to produce and sufficient: the
          // walk continues on the TOTAL rather than on a full page, so a single row keeps it going.
          request.flush(
            pageOf([aRoleListItem({ roleId: pageIndex })], Number.MAX_SAFE_INTEGER, pageIndex),
          );
        });
      }

      expect(inFlightMoreThanOne)
        .withContext('the walk is sequential: never more than one request in flight')
        .toBe(0);
      expect(issued)
        .withContext('the walk stops at the declared ceiling rather than running unbounded')
        .toBe(MAXIMUM_ROLE_PAGES);
      expect(store.roleItems())
        .withContext('a ceiling hit publishes nothing rather than a plausible-looking subset')
        .toEqual([]);
      expect(store.failure()?.operation).toBe('loadRoles');
      expect(store.rolesLoading()).toBeFalse();
    });

    it('walks every page and joins them, so no role is silently left off the listing', () => {
      // ⚠ THE REGRESSION THIS PINS DOWN. The listing endpoint is paged and the screen that
      // consumes this slice offers no pager, exactly as the legacy screen offered none, so a
      // single windowed request would present the first page AS the whole set - a silent data
      // loss rather than a smaller view. Every page is therefore walked and joined here.
      const firstPage: readonly RoleListItem[] = Array.from(
        { length: ROLES_FETCH_PAGE_SIZE },
        (_unused, index) => aRoleListItem({ roleId: index, roleName: `Role ${index}` }),
      );

      store.loadRoles();

      const first = expectGet(ROLES_URL);

      expect(first.request.params.get('pageIndex')).toBe('0');
      // A FULL page, which is the signal that another may exist.
      first.flush(pageOf(firstPage, ROLES_FETCH_PAGE_SIZE + 3));

      const second = expectGet(ROLES_URL);

      expect(second.request.params.get('pageIndex'))
        .withContext('the walk continues to the next page rather than stopping at the window')
        .toBe('1');
      expect(second.request.params.get('pageSize')).toBe(String(ROLES_FETCH_PAGE_SIZE));

      second.flush(
        pageOf(
          [
            aRoleListItem({ roleId: 100, roleName: 'Role 100' }),
            aRoleListItem({ roleId: 101, roleName: 'Role 101' }),
            aRoleListItem({ roleId: 102, roleName: 'Role 102' }),
          ],
          ROLES_FETCH_PAGE_SIZE + 3,
          1,
        ),
      );

      // A short second page ends the walk, so no third request is issued.
      httpMock.verify();

      expect(store.roleItems().length).toBe(ROLES_FETCH_PAGE_SIZE + 3);
      expect(store.roleItems()[0]?.roleName).toBe('Role 0');
      expect(store.roleItems()[ROLES_FETCH_PAGE_SIZE + 2]?.roleName)
        .withContext('a role beyond the first window is present rather than truncated away')
        .toBe('Role 102');
      expect(store.rolesMeta().totalCount).toBe(ROLES_FETCH_PAGE_SIZE + 3);
      expect(store.rolesLoading()).toBeFalse();
    });

    it('carries the ordering members and returns to the first page when they change', () => {
      store.setRolesSort('RoleName', 'Ascending');

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('sortBy')).toBe('RoleName');
      expect(request.request.params.get('sortDir')).toBe('Ascending');
      expect(request.request.params.get('pageIndex')).toBe('0');

      request.flush(pageOf([aRoleListItem()], 1));

      const coordinate: RolePageCoordinate = store.rolesPage();

      expect(coordinate.sortBy).toBe('RoleName');
      expect(coordinate.sortDir).toBe('Ascending');
    });

    it('omits the ordering members when the caller expresses no opinion', () => {
      store.setRolesSort(null, null);

      const request = expectGet(ROLES_URL);

      // Absence lets the server apply its own ordering rather than a literal chosen in the
      // client, and absence is expressed by OMITTING the parameter.
      expect(request.request.params.has('sortBy')).toBeFalse();
      expect(request.request.params.has('sortDir')).toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends a free-text filter verbatim, with no wildcard appended', () => {
      store.setRolesQuery('admin');

      const request = expectGet(ROLES_URL);
      const filter: string = present(request.request.params.get('query'), 'the filter parameter');

      expect(filter).toBe('admin');
      // The legacy queries wrapped a filter in wildcards server-side. Composing a pattern in
      // the client would hand the server a value it would wrap again, matching on the
      // wildcard itself.
      expect(filter).not.toContain('%');

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('distinguishes an empty filter from no filter at all', () => {
      store.setRolesQuery('');

      const empty = expectGet(ROLES_URL);

      // The legacy contract treated the empty string and absence as one value — one screen
      // compared a message against a literal empty string while another compared it against
      // the absent-string marker, whose body returns the empty string. The target keeps them
      // distinct on the wire and lets the server decide, so an empty filter is SENT.
      expect(empty.request.params.has('query')).toBeTrue();
      expect(empty.request.params.get('query')).toBe('');
      empty.flush(pageOf([aRoleListItem()], 1));

      store.setRolesQuery(null);

      const absent = expectGet(ROLES_URL);

      expect(absent.request.params.has('query')).toBeFalse();
      absent.flush(pageOf([aRoleListItem()], 1));
    });

    it('reads the assignment listing with its own page coordinate', () => {
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize']);
      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([anAssignment()], 1));

      store.setAssignmentsPage(2);

      const second = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(second.request.params.get('pageIndex')).toBe('2');
      second.flush(pageOf([anAssignment()], 25, 2));

      expect(store.assignmentsPage().pageIndex).toBe(2);
      expect(store.assignmentsMeta().totalCount).toBe(25);
    });

    it('issues exactly ONE request per assignment read, following no further page', () => {
      // ⚠ THE REGRESSION THIS PINS DOWN. An earlier revision read the assignment listing at the widest
      // legal page and then requested every further page the metadata reported, together, joining them
      // into one set - a burst of concurrent requests bounded only by a thousand-page ceiling, the whole
      // membership of a role retained in memory, and a repeat of the walk after every assignment write.
      // The screen that consumes this slice mounts the shared pager instead, so the page is the unit.
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.get('pageSize')).toBe(String(DEFAULT_PAGE_SIZE));

      // A FULL page against a much larger total, which is the strongest possible invitation to walk.
      request.flush(pageOf([anAssignment()], 137));

      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentItems()).toHaveSize(1);
      // The SERVER's own coordinate and total are held as published, because that is what a pager binds
      // to and what tells an operator there is more to see.
      expect(store.assignmentsMeta().totalCount).toBe(137);
      expect(store.assignmentsMeta().pageSize).toBe(DEFAULT_PAGE_SIZE);
    });

    it('returns the assignment coordinate to the first page when the addressed role changes', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25));

      store.setAssignmentsPage(2);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25, 2));

      expect(store.assignmentsPage().pageIndex).toBe(2);

      // A different role, and the page an operator was standing on belongs to the role they were
      // looking at. Carrying the index across would request a window the new role may not have and
      // present an empty grid for a role that has members. Zero is a REAL role, so this is also the
      // case that proves the change of address is detected by identity rather than by truthiness.
      store.loadAssignments(0);

      const fresh = expectGet('/api/v1/roles/0/users');

      expect(fresh.request.params.get('pageIndex')).toBe('0');
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentItems())
        .withContext('and the previous role\'s rows are not published under the new role')
        .toEqual([]);

      fresh.flush(pageOf([anAssignment({ roleId: 0 })], 1));
    });

    it('steps back to the last page that exists when a removal leaves the coordinate past the end', () => {
      // ⚠ THE DEAD END THIS PREVENTS, WHICH ONLY PAGING CAN REACH. Eleven members at ten a page;
      // the operator moves to the second page, where the eleventh sits alone, and removes it. The
      // write's re-read asks for page one again and the server answers an empty window whose
      // metadata still reports ten members. Without a correction the grid renders nothing AND the
      // pager is withdrawn — it is drawn on `pageSize < totalCount`, which ten against ten makes
      // false — so the operator is left on a blank grid with no affordance back to the ten rows
      // that are still there. An unpaged grid could not reach that state.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 11));

      store.setAssignmentsPage(1);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ userRoleId: 11 })], 11, 1));

      expect(store.assignmentsPage().pageIndex).toBe(1);

      // The re-read after a removal, answered as the server would: the window is empty, the total
      // has fallen to ten, and the reported page count no longer reaches the index asked for.
      store.reloadAssignments();

      const stranded = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(stranded.request.params.get('pageIndex')).toBe('1');
      stranded.flush(pageOf([], 10, 1));

      // ONE corrective read, at the last page the server says exists.
      const corrected = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(corrected.request.params.get('pageIndex'))
        .withContext('the step-back target comes from the reported page count, not from arithmetic on rows')
        .toBe('0');
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsLoading())
        .withContext('a read is still outstanding, so the grid must not report itself at rest')
        .toBeTrue();

      corrected.flush(pageOf([anAssignment()], 10));

      expect(store.assignmentItems()).toHaveSize(1);
      expect(store.assignmentsMeta().totalCount).toBe(10);
      expect(store.assignmentsLoading()).toBeFalse();
      expect(store.failure())
        .withContext('a corrected coordinate is not a failure')
        .toBeNull();
    });

    it('issues no corrective read when the listing is legitimately empty', () => {
      // A role with no members answers an empty window with a total of nought, and that is a
      // COMPLETE answer. Correcting it would cost a second request to be told the same thing, and
      // the positive-total clause is the only thing separating the two cases.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25));

      store.setAssignmentsPage(2);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0, 2));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentItems()).toEqual([]);
      expect(store.assignmentsPage().pageIndex)
        .withContext('the coordinate is left exactly where the caller put it')
        .toBe(2);
      expect(store.assignmentsLoading()).toBeFalse();
    });

    it('issues no corrective read for an empty FIRST page, whatever the total says', () => {
      // The first page is never past the end. A server reporting an empty first page beside a
      // positive total is inconsistent with itself, and a correction would ask for page nought
      // again — the page it just answered — for ever.
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([], 25));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsLoading()).toBeFalse();
    });

    it('believes a server that reports the window it was asked for does exist', () => {
      // An empty page can be a truthful answer for a window that DOES exist — every row in it
      // removed by another administrator between the count and the window. The reported page
      // count still covers the index, so nothing is corrected.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35));

      store.setAssignmentsPage(1);

      // Thirty-five members at ten a page is four pages, so page one is well inside the set.
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 35, 1));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex).toBe(1);
      expect(store.assignmentItems()).toEqual([]);
    });

    it('corrects AT MOST ONCE per read, so a listing shrinking underneath cannot loop', () => {
      // ⚠ THE TERMINATION PROOF. The corrective read is issued with correction withheld, so a
      // listing that keeps shrinking between requests costs one extra request and stops — rather
      // than chasing a receding end for as long as the shrinking continues.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35));

      store.setAssignmentsPage(3);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35, 3));

      store.reloadAssignments();
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 21, 3));

      // Corrected to page two, the last of three that twenty-one members make.
      const corrected = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(corrected.request.params.get('pageIndex')).toBe('2');

      // And the listing has shrunk AGAIN in the meantime, so this answer is past the end too.
      corrected.flush(pageOf([], 10, 2));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex)
        .withContext('the coordinate the corrective read used is kept; it is not chased further')
        .toBe(2);
      expect(store.assignmentsLoading())
        .withContext('and the slice is at rest rather than waiting on a request nobody issued')
        .toBeFalse();
    });

    it('reads the role-group listing with no query parameter whatsoever', () => {
      store.loadRoleGroups();

      const request = expectGet(ROLE_GROUPS_URL);

      // UNPAGED, and not merely un-indexed. The endpoint answers a plain array in the
      // single-payload envelope and the legacy screen bound a plain list at `:L108`. There is
      // also no tenant parameter: the server resolves the tenant from the request host and
      // the caller's claims, so publishing one here would create a second public identity
      // for the same operation.
      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.params.has('pageIndex')).toBeFalse();
      expect(request.request.params.has('pageSize')).toBeFalse();
      expect(request.request.params.has('portalId')).toBeFalse();

      request.flush(envelopeOf([aRoleGroup({ roleGroupId: 0 }), aRoleGroup({ roleGroupId: 1 })]));

      expect(store.roleGroups().length).toBe(2);
    });

    it('holds the groups as a plain sequence, with no coordinate, size or total beside it', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(Array.isArray(store.roleGroups()))
        .withContext('the groups are a sequence, not a page')
        .toBeTrue();

      // Enumerated rather than merely asserted one name at a time, so a paging slice added
      // to the groups in future fails here rather than passing unnoticed.
      const groupPagingMembers: readonly string[] = publishedMembers(store).filter((name) => {
        const lowered = name.toLowerCase();

        return (
          lowered.includes('group') &&
          (lowered.includes('page') || lowered.includes('total') || lowered.includes('size'))
        );
      });

      expect(groupPagingMembers)
        .withContext('the group listing is unpaged and must publish no paging member')
        .toEqual([]);
    });

    it('publishes a page coordinate for each paged listing and for neither of the others', () => {
      const published: readonly string[] = publishedMembers(store);

      expect(published.includes('rolesPage')).toBeTrue();
      expect(published.includes('assignmentsPage')).toBeTrue();
      expect(published.includes('roleGroupsPage')).toBeFalse();
      expect(published.includes('roleGroupsMeta')).toBeFalse();
      expect(published.includes('setRoleGroupsPage')).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // ASSIGNMENT REMOVAL — THE EMPTY SUCCESS THAT MAY LEAVE THE ROW IN PLACE
  // -------------------------------------------------------------------------

  // -------------------------------------------------------------------------
  // THE SINGLE-ACCOUNT MEMBERSHIP PROBE
  // -------------------------------------------------------------------------

  describe('the keyed membership probe answers about ONE pairing, in ONE request', () => {
    it('filters the listing by the login name and matches the row by identifier', () => {
      store.probeAssignment(7, 42, 'host');

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      // ⚠ THE NAME IS THE FILTER; THE IDENTIFIER IS THE MATCH. The listing matches the free-text
      // filter as a case-insensitive substring of either the login name or the display name, so a
      // name that is a fragment of another brings back neighbours. The wanted row is picked out
      // afterwards by identity, because a name is not an identifier.
      expect(request.request.params.get('query')).toBe('host');
      // BOUNDED: one request, at the widest page the contract allows, and never a walk.
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe(String(MAX_PAGE_SIZE));

      // The pairing being asked about is published BEFORE the answer lands, so a screen can see
      // which question is outstanding.
      expect(store.probedAssignmentKey()).toEqual({ roleId: 7, userId: 42 });
      expect(store.assignmentProbeLoading()).toBeTrue();

      const wanted = anAssignment({ userRoleId: 9, userId: 42 });

      request.flush(
        pageOf([anAssignment({ userRoleId: 8, userId: 43, username: 'hostess' }), wanted], 2),
      );

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.probedAssignment()).toEqual(wanted);
      expect(store.assignmentProbeLoading()).toBeFalse();
      // And the LISTING slice is untouched by a probe: the two answer different questions and neither
      // may overwrite the other.
      expect(store.assignmentItems()).toEqual([]);
    });

    it('reports NO MEMBERSHIP when the filtered page holds no row for the account', () => {
      // Role zero and account zero, because both tables carry a legitimate zero key and a truthiness
      // test on either would make this case pass for the wrong reason.
      store.probeAssignment(0, 0, 'ada');

      expectGet('/api/v1/roles/0/users').flush(pageOf([anAssignment({ userId: 99 })], 1));

      // A settled "holds nothing", told apart from "nothing has been asked" by the key beside it.
      expect(store.probedAssignment()).toBeNull();
      expect(store.probedAssignmentKey()).toEqual({ roleId: 0, userId: 0 });
    });

    it('records a failed probe under its own operation, never as a failed listing', () => {
      store.probeAssignment(7, 42, 'host');

      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(
        aProblem(500, 'server.unexpected_failure', 'Something went wrong.'),
        { status: 500, statusText: 'Internal Server Error' },
      );

      // A screen reports a listing failure by matching the OPERATION, so a probe reported as
      // `loadAssignments` would be announced as the grid having failed to load.
      expect(present(store.failure(), 'the recorded failure').operation).toBe('probeAssignment');
      expect(store.probedAssignment())
        .withContext('a failed probe holds nothing, which is the state an unknown account shows')
        .toBeNull();
      expect(store.assignmentProbeLoading()).toBeFalse();
    });

    it('abandons the probe it replaces, so an older answer cannot land on a newer question', () => {
      store.probeAssignment(7, 42, 'host');

      const first = expectGet(ROLE_SEVEN_MEMBERS_URL);

      store.probeAssignment(7, 43, 'hostess');

      const second = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(first.cancelled).withContext('superseded').toBeTrue();
      expect(second.cancelled).withContext('current').toBeFalse();
      expect(store.probedAssignmentKey()).toEqual({ roleId: 7, userId: 43 });

      second.flush(pageOf([anAssignment({ userId: 43 })], 1));

      expect(present(store.probedAssignment(), 'the newer answer').userId).toBe(43);
    });

    it('forgets the answer and the question together, and abandons a probe in flight', () => {
      store.probeAssignment(7, 42, 'host');

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      store.clearProbedAssignment();

      expect(request.cancelled).withContext('abandoned').toBeTrue();
      // Both are cleared, so the state is "nothing has been asked" rather than "the pairing holds
      // nothing" - two different facts that read differently to an operator.
      expect(store.probedAssignment()).toBeNull();
      expect(store.probedAssignmentKey()).toBeNull();
      expect(store.assignmentProbeLoading()).toBeFalse();
    });
  });

  describe('assignment removal expires the row rather than deleting it', () => {
    /**
     * Puts one assignment in scope so a removal has something to act on.
     *
     * @param row The assignment to hold.
     */
    function givenOneAssignment(row: UserRole): void {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([row], 1));
    }

    it('re-reads the assignment listing after an empty success, because the row may survive', () => {
      // THE MOST CONSEQUENTIAL CASE IN THIS FILE. `RoleController.vb:L494` branches on the
      // assignment being paid and having used its trial, and in that case `:L496` back-dates
      // the expiry bound to YESTERDAY and `:L497` writes the row back — it does NOT delete.
      // Just the other branch, `:L500`, genuinely deletes. The endpoint answers an empty
      // success in BOTH cases and nothing in the response distinguishes them, so the sole
      // correct response is to re-read. A store that removed the row and issued no second
      // request would fail here, which is exactly why the case is written this way.
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);

      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      const reread = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(reread.request.method)
        .withContext('an empty success cannot say whether the row was deleted or expired')
        .toBe('GET');

      reread.flush(pageOf([], 0));

      expect(store.assignmentItems()).toEqual([]);
    });

    it('does not drop the row from held state before the re-read resolves', () => {
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      // The re-read is open and unresolved at this instant. An optimistic removal would be
      // wrong roughly half the time and the mistake would be invisible until the page was
      // next read, so nothing is removed on speculation.
      expect(store.assignmentItems().length)
        .withContext('held state must not be edited on the strength of an empty success')
        .toBe(1);
      expect(present(store.assignmentItems()[0], 'the held assignment').userId).toBe(42);

      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));
    });

    it('keeps an expired-but-present row, with its back-dated bound held verbatim', () => {
      // The paid, trial-used branch: the row is still there, its expiry bound moved to
      // yesterday so the trial-usage record survives. The bound is PRESENT and populated, so
      // recognising the condition is a comparison against the present instant and never an
      // absence check — which is why the bound is held exactly as received.
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7, expiryDate: FUTURE_INSTANT }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7, expiryDate: PAST_INSTANT })], 1));

      const held = present(store.assignmentItems()[0], 'the expired assignment');

      expect(store.assignmentItems().length)
        .withContext('an expired assignment is retained, not deleted')
        .toBe(1);
      expect(held.userId).toBe(42);
      expect(held.expiryDate)
        .withContext('the bound is an absolute instant, held exactly as the server sent it')
        .toBe(PAST_INSTANT);
      expect(store.failure())
        .withContext('a surviving row is not a failure')
        .toBeNull();
    });

    it('reflects the genuine deletion when the re-read comes back without the row', () => {
      // The other branch: a free assignment, or one whose trial was never used, is genuinely
      // deleted. Same status, different outcome, and the re-read is what tells them apart.
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      expect(store.assignmentItems()).toEqual([]);
      expect(store.assignmentsMeta().totalCount).toBe(0);
    });

    it('holds a recorded fee of zero as zero, because zero means free rather than absent', () => {
      // `RoleController.vb:L494` discriminates paid from free with a strictly-greater-than-
      // zero test, so zero falls on the free side deliberately. The fee's legacy
      // absent-marker was the single-precision type's minimum value, neither zero nor minus
      // one, so a recorded zero is never absence and is never coalesced away.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: 0 })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.serviceFee).toBe(0);
      expect(held.serviceFee).not.toBeNull();
      expect(store.selectedRoleIsPaid())
        .withContext('a fee of zero is a free role, which is a real classification')
        .toBeFalse();
    });

    it('distinguishes a recorded fee of zero from no recorded fee at all', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.serviceFee).toBeNull();
      expect(held.serviceFee).not.toBe(0);
      expect(store.selectedRoleIsPaid()).toBeFalse();
    });

    it('classifies a positive fee as paid', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: 12.5 })));

      expect(store.selectedRoleIsPaid()).toBeTrue();
      expect(present(store.selectedRole(), 'the selected role').serviceFee).toBe(12.5);
    });

    it('reports no paid classification while no role is selected', () => {
      expect(store.selectedRoleIsPaid())
        .withContext('absence of a selection is distinct from a free role')
        .toBeNull();
    });

    it('surfaces a protected-assignment conflict verbatim and re-reads nothing', () => {
      // The legacy guard refusing to strip the portal administrator or the registered-users
      // role is enforced server-side and arrives as a conflict. There is nothing to re-read,
      // because nothing changed.
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.protectedAssignment,
            'You Can Not Remove The Portal Administrator Or The Registered Users Role',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('removeAssignment');
      expect(failure.conflict).toBe(CONFLICT_CODE.protectedAssignment);
      expect(store.assignmentItems().length)
        .withContext('a refused removal leaves held state exactly as it was')
        .toBe(1);
      expect(store.saving()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // ASSIGNMENT CREATION — CREATED OR NO CONTENT, BOTH SUCCESSES
  // -------------------------------------------------------------------------

  describe('assignment creation answers created or no content, and both are successes', () => {
    const request: RoleAssignmentRequest = Object.freeze({
      userId: 42,
      effectiveDate: null,
      expiryDate: null,
      notifyUser: false,
    });

    it('handles a created answer and re-reads the listing', () => {
      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      expect(write.request.body).toEqual(request);
      write.flush(null, { status: 201, statusText: 'Created' });

      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
      expect(store.failure()).toBeNull();
    });

    it('handles a no-content answer identically, and does not read a response body', () => {
      // MIGRATION: the legacy write was an update-or-add. `RoleController.vb:L503` seeded an
      // assignment key with the absent-integer marker, `:L550` updated a row it found and the
      // else arm added one. The endpoint preserves that, so it legitimately answers either
      // and the client cannot tell which happened. Neither answer is a failure and neither
      // carries a body.
      store.assignUser(7, request);

      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7 })], 1));

      expect(store.failure())
        .withContext('a no-content answer is a success, not a failure')
        .toBeNull();
      expect(store.assignmentItems().length).toBe(1);
      expect(store.saving()).toBeFalse();
    });

    it('re-reads the row rather than synthesising it from the request', () => {
      // The server derives the expiry bound from the role's trial or billing terms and its
      // own clock, and clamps the bounds it was given at `:L529-L534`, so a row built from
      // the request would show bounds the server did not store. The request carried no
      // bounds at all here, and the held row carries the server's.
      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf(
            [
              anAssignment({
                userId: 42,
                roleId: 7,
                effectiveDate: MIN_INSTANT,
                expiryDate: PERPETUAL_INSTANT,
              }),
            ],
            1,
          ),
        );

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(request.expiryDate).toBeNull();
      expect(held.expiryDate)
        .withContext('the bound held is the server\u2019s, never the one asked for')
        .toBe(PERPETUAL_INSTANT);
      expect(held.effectiveDate).toBe(MIN_INSTANT);
    });

    it('does not confuse the absent-row marker with the ungrouped narrowing', () => {
      // Vocabulary (d) again, in its second legacy use. The absent-integer marker meant "no
      // existing assignment row" at `:L503`; it is a server-side local that never reaches the
      // wire, and it is emphatically not the ungrouped narrowing. Nothing in the store
      // converts between the two, and the narrowing is unmoved by an assignment write.
      const before: RoleGroupFilter = store.groupFilter();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 1));

      expect(store.groupFilter()).toEqual(before);
      expect(store.selectedRoleGroupId()).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // THE SIX PERSISTED FREQUENCY CODES
  // -------------------------------------------------------------------------

  describe('the six persisted frequency codes survive verbatim', () => {
    it('holds each of the six codes on the billing member exactly as sent', () => {
      // Table-driven over the model's own union, so a folded case, a word-spelled unit or an
      // integer substitution could not compile, let alone reach this assertion. The codes are
      // the literal bytes in two single-character columns and one vocabulary serves both;
      // renaming one would silently mis-read live rows.
      for (const code of FREQUENCY_CODES) {
        store.selectRole(7);
        expectGet(ROLE_SEVEN_URL)
          .flush(envelopeOf(aRole({ roleId: 7, billingFrequency: code })));

        expect(present(store.selectedRole(), 'the selected role').billingFrequency)
          .withContext(`the billing code ${code} must survive untouched`)
          .toBe(code);

        store.clearSelectedRole();
      }
    });

    it('holds each of the six codes on the trial member, which shares the vocabulary', () => {
      for (const code of FREQUENCY_CODES) {
        store.selectRole(7);
        expectGet(ROLE_SEVEN_URL)
          .flush(envelopeOf(aRole({ roleId: 7, trialFrequency: code })));

        expect(present(store.selectedRole(), 'the selected role').trialFrequency)
          .withContext(`the trial code ${code} must survive untouched`)
          .toBe(code);

        store.clearSelectedRole();
      }
    });

    it('classifies each code by whether it bounds the membership, without producing a date', () => {
      // The classification is a fact about the terms, not an instant and not display text.
      // The bound itself is derived server-side from an injected clock and arrives as an
      // absolute instant; a second derivation in the client would disagree across a clock
      // skew and neither answer would be reproducible.
      const expected: readonly (readonly [BillingFrequency, BillingTermsBound])[] = [
        ['N', 'Unbounded'],
        ['O', 'Perpetual'],
        ['D', 'Bounded'],
        ['W', 'Bounded'],
        ['M', 'Bounded'],
        ['Y', 'Bounded'],
      ];

      for (const [code, bound] of expected) {
        expect(billingTermsBound(code, 12))
          .withContext(`the code ${code} classifies as ${bound}`)
          .toBe(bound);
      }
    });

    it('treats the absent-integer period as no expiry at all, ahead of the code', () => {
      // `RoleController.vb:L537` short-circuits the whole frequency branch when the period
      // equals the absent-integer marker. It does not mean "unset, apply a default", so it is
      // never coalesced to zero and never made positive — and it is tested FIRST, so it wins
      // over a code that would otherwise bound the membership.
      expect(billingTermsBound('M', -1)).toBe('Unbounded');
      expect(billingTermsBound('Y', -1)).toBe('Unbounded');
      expect(billingTermsBound('D', -1)).toBe('Unbounded');
    });

    it('treats no recorded code as unbounded, which is a different fact from the no-expiry code', () => {
      expect(billingTermsBound(null, 12)).toBe('Unbounded');
      expect(billingTermsBound(null, null)).toBe('Unbounded');
      expect(billingTermsBound('N', 12)).toBe('Unbounded');
    });

    it('reports a stored code outside the supported vocabulary as unsupported', () => {
      // MIGRATION: THE CLASSIFIER USED TO THROW HERE, and the throw was reachable from shipped
      //   data rather than from drift. Every DotNetNuke installation seeds roles whose stored
      //   frequency characters come from the superseded numeric code set, the API carries a stored
      //   character through losslessly, and the six-arm branch ended in an unreachable-case
      //   assertion — so describing one of those roles raised an error on a screen that only
      //   wanted to say what its terms were.
      //
      // `Unsupported` is neither of the two answers it could be mistaken for, and that is the
      // point. `RoleController.vb:L540-L547` has no final arm, so an unrecognised code applied NO
      // advance and left the expiry exactly as it stood: the terms neither remove an expiry
      // (`Unbounded`) nor advance one (`Bounded`), so claiming either would assert something the
      // stored terms do not say.
      expect(billingTermsBound('4', 12)).toBe('Unsupported');
      expect(billingTermsBound('0', 1)).toBe('Unsupported');

      // Case is data. A lower-case `m` is a different stored byte from `M`, and it is classified
      // as unsupported rather than folded onto the month code — the server's persistence read is
      // case-sensitive for the same reason.
      expect(billingTermsBound('m', 12)).toBe('Unsupported');
    });

    it('still lets the absent-integer period win over an unsupported code', () => {
      // The period short-circuit is tested FIRST in the legacy branch and still is here, so the
      // widened vocabulary cannot reorder the two.
      expect(billingTermsBound('4', -1)).toBe('Unbounded');
    });

    it('classifies the terms of a role whose stored code is unsupported, rather than failing', () => {
      // The end-to-end form of the same claim: a role carrying a legacy code is read, held and
      // classified, and the code itself survives on the held role untouched.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(
        envelopeOf(aRole({ roleId: 7, billingFrequency: '4', billingPeriod: 1 })),
      );

      expect(present(store.selectedRole(), 'the selected role').billingFrequency).toBe('4');
      expect(store.selectedRoleBillingTerms()).toBe('Unsupported');
      expect(store.failure()).withContext('a legacy code is data, not a failure').toBeNull();
    });

    it('retains a period of minus one on the role rather than coalescing it', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(
          envelopeOf(
            aRole({ roleId: 7, billingFrequency: 'M', billingPeriod: -1, trialPeriod: -1 }),
          ),
        );

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.billingPeriod).toBe(-1);
      expect(held.trialPeriod).toBe(-1);
      expect(store.selectedRoleBillingTerms())
        .withContext('an absent-integer period means no expiry, whatever the code says')
        .toBe('Unbounded');
    });

    it('classifies the billing and the trial terms separately, since the server chooses between them', () => {
      // `RoleController.vb:L521` chose the trial terms over the billing terms when the trial
      // had not already been used and the trial code was not the no-expiry one. The first half
      // of that test reads a flag on the ASSIGNMENT row, which no published contract carries,
      // so the choice stays where the data to make it lives and both sets are classified here.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(
          envelopeOf(
            aRole({
              roleId: 7,
              billingFrequency: 'M',
              billingPeriod: 1,
              trialFrequency: 'O',
              trialPeriod: 1,
            }),
          ),
        );

      expect(store.selectedRoleBillingTerms()).toBe('Bounded');
      expect(store.selectedRoleTrialTerms()).toBe('Perpetual');
    });

    it('reports no term classification while no role is selected', () => {
      expect(store.selectedRoleBillingTerms()).toBeNull();
      expect(store.selectedRoleTrialTerms()).toBeNull();
    });

    it('holds the perpetual instant as a real value rather than an error or an absence', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ expiryDate: PERPETUAL_INSTANT })], 1));

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(held.expiryDate).toBe(PERPETUAL_INSTANT);
      expect(held.expiryDate).not.toBeNull();
      expect(store.failure()).toBeNull();
    });

    it('holds the minimum instant as a real value meaning no expiry, not as absence', () => {
      // The legacy absent-instant marker is the minimum representable date, not nothing at
      // all. Normalising it to absence would discard the distinction between "no expiry
      // recorded" and "no expiry, recorded as such".
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf([anAssignment({ effectiveDate: MIN_INSTANT, expiryDate: MIN_INSTANT })], 1),
        );

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(held.expiryDate).toBe(MIN_INSTANT);
      expect(held.effectiveDate).toBe(MIN_INSTANT);
      expect(held.expiryDate).not.toBeNull();
    });

    it('distinguishes a recorded minimum instant from no recorded instant at all', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf(
            [
              anAssignment({ userRoleId: 1, effectiveDate: MIN_INSTANT, expiryDate: null }),
              anAssignment({ userRoleId: 2, effectiveDate: null, expiryDate: MIN_INSTANT }),
            ],
            2,
          ),
        );

      const first = present(store.assignmentItems()[0], 'the first assignment');
      const second = present(store.assignmentItems()[1], 'the second assignment');

      expect(first.effectiveDate).toBe(MIN_INSTANT);
      expect(first.expiryDate).toBeNull();
      expect(second.effectiveDate).toBeNull();
      expect(second.expiryDate).toBe(MIN_INSTANT);
    });
  });

  // -------------------------------------------------------------------------
  // THE GROUP-DELETE AFFORDANCE, AND THE AUTHORITATIVE CONFLICT
  // -------------------------------------------------------------------------

  describe('the group-delete affordance, and the conflict that overrules it', () => {
    /**
     * Selects a real group and settles the role listing under it.
     *
     * @param roleGroupId The group to narrow to.
     * @param roles The roles the listing answers with.
     */
    function givenGroupSelected(roleGroupId: number, roles: readonly RoleListItem[]): void {
      store.setGroupFilter({ kind: 'Group', roleGroupId });
      expectGet(ROLES_URL).flush(pageOf(roles, roles.length));
    }

    it('offers the deletion when a real group is selected and its listing is empty', () => {
      // Legacy: `Roles.ascx.vb:L85` sets the delete control's visibility from the listed roles
      // being empty. The folder requirements cite `:L84`, which is the edit-group link's
      // navigation address — the delete guard is `:L85`.
      givenGroupSelected(0, []);

      expect(store.canDeleteSelectedGroup()).toBeTrue();
    });

    it('withholds the deletion while the selected group still classifies a role', () => {
      givenGroupSelected(0, [aRoleListItem({ roleId: 0 })]);

      expect(store.canDeleteSelectedGroup()).toBeFalse();
    });

    it('withholds the deletion for either pseudo-intent, which names no group to act on', () => {
      // Legacy: `:L79-L81` hides both the edit-group and the delete control whenever the
      // narrowing is negative, because neither pseudo-intent is a group you can act on.
      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.canDeleteSelectedGroup()).toBeFalse();

      store.setGroupFilter({ kind: 'GlobalRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.canDeleteSelectedGroup()).toBeFalse();
    });

    it('issues the deletion even while the affordance withholds it, because the server decides', () => {
      // The affordance exists so a screen can disable a control. It is never consulted to
      // decide whether to send the request: the listing it reads may be a stale page and it
      // counts the roles on the CURRENT page rather than every role in the group, so it can
      // disagree with the server in both directions.
      givenGroupSelected(0, [aRoleListItem({ roleId: 0 })]);

      expect(store.canDeleteSelectedGroup()).toBeFalse();

      store.deleteRoleGroup(0);

      const removal = expectDelete(ROLE_GROUP_ZERO_URL);

      expect(removal.request.url)
        .withContext('the request must not be suppressed by a client-side affordance')
        .toBe(ROLE_GROUP_ZERO_URL);

      removal.flush(null, { status: 204, statusText: 'No Content' });

      // The success path resets the narrowing and re-reads both listings.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));
      expectGet(ROLES_URL).flush(pageOf([], 0));
    });

    it('resets the narrowing to the ungrouped intent after a successful deletion', () => {
      // Legacy: `:L295` resets the field to minus one, which is the UNGROUPED entry per
      // `:L114` — not the every-role one. The two are easily transposed, which is why the
      // reset target is asserted by name.
      givenGroupSelected(0, []);

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.rolesPage().pageIndex).toBe(0);

      // The re-read then follows, groups first, and the roles are read under the reset
      // narrowing rather than under the group that no longer exists.
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 1, roleGroupName: 'Other Groups' })]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope')).toBe('Ungrouped');
      rolesRequest.flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('surfaces a conflict on the deletion verbatim, and refreshes the stale affordance', () => {
      // The affordance can say yes while the server says no, so the conflict is handled
      // unconditionally. Because the conflict PROVES the held listing was stale, both
      // listings are re-read on the failure path, which corrects the affordance instead of
      // leaving a control enabled that would fail again — and the failure that prompted the
      // refresh survives it.
      givenGroupSelected(0, []);

      expect(store.canDeleteSelectedGroup()).toBeTrue();

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleGroupName,
            'The group still classifies at least one role.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('deleteRoleGroup');
      expect(failure.conflict)
        .withContext('the server\u2019s code is surfaced exactly as it spells it')
        .toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(store.canDeleteSelectedGroup())
        .withContext('the refreshed listing corrects the affordance')
        .toBeFalse();
    });

    it('leaves the narrowing untouched when the deletion is refused', () => {
      givenGroupSelected(0, []);

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleGroupName, 'Still in use.'), {
          status: 409,
          statusText: 'Conflict',
        });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      // A refused deletion did not delete, so the narrowing must still name the group.
      expect(store.selectedRoleGroupId()).toBe(0);
      expect(store.groupFilter().kind).toBe('Group');
    });

    it('creates a group and re-reads the unpaged listing', () => {
      const request: CreateRoleGroupRequest = { roleGroupName: 'Site Groups', description: '' };

      store.createRoleGroup(request);

      const write = expectPost(ROLE_GROUPS_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRoleGroup({ roleGroupId: 0 })), {
        status: 201,
        statusText: 'Created',
      });

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(store.roleGroups().length).toBe(1);
      expect(store.failure()).toBeNull();
    });

    it('surfaces a duplicate-group conflict verbatim without re-reading', () => {
      const request: CreateRoleGroupRequest = { roleGroupName: 'Site Groups', description: null };

      store.createRoleGroup(request);
      expectPost(ROLE_GROUPS_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleGroupName,
            'A role group with the same name already exists. The new group was not added.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('createRoleGroup');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(store.roleGroups()).toEqual([]);
    });

    it('patches a renamed group in place, matching the key zero by identity', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' }),
            aRoleGroup({ roleGroupId: 1, roleGroupName: 'Other Groups' }),
          ]),
        );

      const request: UpdateRoleGroupRequest = {
        roleGroupName: 'Renamed Groups',
        description: 'Renamed',
      };

      store.updateRoleGroup(0, request);

      const write = expectPut(ROLE_GROUP_ZERO_URL);

      expect(write.request.body).toEqual(request);
      write.flush(
        envelopeOf(
          aRoleGroup({ roleGroupId: 0, roleGroupName: 'Renamed Groups', description: 'Renamed' }),
        ),
      );

      // Matched on identity with a strict comparison, never by truth: the grouping table is
      // seeded from zero, so the row being patched here is exactly the one a truthy test
      // would omit. No re-read follows, because a group's identity does not determine its own
      // membership of the group listing.
      expect(store.roleGroups().map((group) => group.roleGroupName)).toEqual([
        'Renamed Groups',
        'Other Groups',
      ]);
    });
  });

  // -------------------------------------------------------------------------
  // SENTINEL FIDELITY — EVERY AWKWARD VALUE IS DATA
  // -------------------------------------------------------------------------

  describe('sentinel fidelity: zero, minus one, empty and false all survive unchanged', () => {
    it('holds a role carrying every awkward value exactly as the server sent it', () => {
      // The server serialises with no ignore condition at all, so none of these values is
      // elided on the way out and every one arrives as DATA. A member-by-member comparison is
      // deliberate: a whole-object comparison would pass even if a member had been replaced
      // by an equal-looking value of a different type.
      const wire: Role = aRole({
        roleId: 0,
        roleGroupId: null,
        description: '',
        serviceFee: 0,
        trialFee: 0,
        billingPeriod: -1,
        trialPeriod: -1,
        billingFrequency: 'M',
        trialFrequency: 'N',
        isPublic: false,
        autoAssignment: false,
        rsvpCode: '',
        iconFile: null,
      });

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(wire));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleId).toBe(0);
      expect(held.roleGroupId).toBeNull();
      expect(held.description).toBe('');
      expect(held.serviceFee).toBe(0);
      expect(held.trialFee).toBe(0);
      expect(held.billingPeriod).toBe(-1);
      expect(held.trialPeriod).toBe(-1);
      expect(held.billingFrequency).toBe('M');
      expect(held.trialFrequency).toBe('N');
      expect(held.rsvpCode).toBe('');
      expect(held.iconFile).toBeNull();
      expect(held).toEqual(wire);
    });

    it('holds a false boolean as data, because false was indistinguishable from unset before', () => {
      // The legacy absence test reported true for false, so a false flag and an unset one were
      // one value. Every wire boolean here is non-nullable, so false is DATA — and it matters
      // acutely because the shipped tenant creation passes two false flags positionally
      // (`PortalController.vb:L1390`).
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, isPublic: false, autoAssignment: false })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.isPublic).toBe(false);
      expect(held.autoAssignment).toBe(false);
      expect(held.isPublic).not.toBeUndefined();
      expect(held.autoAssignment).not.toBeUndefined();
    });

    it('holds a true boolean as data too, so the flag is genuinely carried', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, isPublic: true, autoAssignment: true })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.isPublic).toBe(true);
      expect(held.autoAssignment).toBe(true);
    });

    it('holds an empty string as an empty string, distinct from absence', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, description: '', rsvpCode: '', iconFile: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.description).toBe('');
      expect(held.description).not.toBeNull();
      expect(held.iconFile).toBeNull();
      expect(held.iconFile).not.toBe('');
    });

    it('holds a listing row carrying every awkward value unchanged', () => {
      const row: RoleListItem = aRoleListItem({
        roleId: 0,
        description: '',
        serviceFee: 0,
        trialFee: 0,
        billingPeriod: -1,
        trialPeriod: -1,
        isPublic: false,
        autoAssignment: false,
      });

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([row], 1));

      expect(present(store.roleItems()[0], 'the listing row')).toEqual(row);
    });

    it('holds a zero total and an empty page as a legitimate answer, not a failure', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.roleItems()).toEqual([]);
      expect(store.rolesMeta().totalCount).toBe(0);
      expect(store.rolesMeta().totalPages).toBe(0);
      expect(store.failure())
        .withContext('an empty page is an answer, not an error')
        .toBeNull();
    });

    it('publishes no general negative-identifier normaliser', () => {
      // Such a helper is precisely what would collapse the four negative vocabularies into
      // one, which is why its ABSENCE is asserted rather than its behaviour tested.
      const suspicious: readonly string[] = publishedMembers(store).filter((name) =>
        /normalis|normaliz|sanitis|sanitiz|coerce|clamp|toIdentifier|fromSentinel/i.test(name),
      );

      expect(suspicious)
        .withContext('nothing in the store may normalise a negative identifier')
        .toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // THE FOUR NEGATIVE VOCABULARIES, KEPT APART PAIRWISE
  // -------------------------------------------------------------------------

  describe('the four negative vocabularies are never conflated', () => {
    it('keeps the pseudo-role STRINGS out of the numeric narrowing space', () => {
      // Vocabulary (c) against vocabulary (a). The pseudo-role identifiers are String
      // constants compared as strings, never parsed to numbers. A payload carrying one — here
      // as a hostile group name — is held as the string it is, and the narrowing is unmoved.
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, roleGroupName: '-2' }),
            aRoleGroup({ roleGroupId: 1, roleGroupName: '-1' }),
          ]),
        );

      const names: readonly string[] = store.roleGroups().map((group) => group.roleGroupName);

      for (const name of names) {
        expect(typeof name)
          .withContext('a pseudo-role identifier is a string and stays one')
          .toBe('string');
        expect(LEGACY_PSEUDO_ROLE_IDS.includes(name)).toBeTrue();
      }

      // The superuser constant and the every-role intent are spelled with the same digits and
      // are entirely different things. The narrowing did not move.
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('keeps a pseudo-role STRING distinct from absence', () => {
      // Vocabulary (c) against vocabulary (d). The all-users constant is the string "-1"; the
      // absent-integer marker is the number minus one. Neither is the other, and neither is
      // absence.
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: '-1' })]));

      const group: RoleGroup = present(store.roleGroups()[0], 'the group');

      expect(group.roleGroupName).toBe('-1');
      expect(group.roleGroupName).not.toBeNull();
      expect(group.roleGroupId).toBe(0);
    });

    it('keeps the grouping FIELD distinct from the absent-integer marker', () => {
      // Vocabulary (b) against vocabulary (d). Absence and minus one are both representable on
      // this member and are told apart, which is what stops the legacy manufactured value from
      // being reintroduced as though it were the same fact.
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: null })));

      expect(present(store.selectedRole(), 'the selected role').roleGroupId).toBeNull();

      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, roleGroupId: -1 })));

      const second = present(store.selectedRole(), 'the selected role');

      expect(second.roleGroupId).toBe(-1);
      expect(second.roleGroupId).not.toBeNull();
    });

    it('keeps the narrowing distinct from the grouping field when both name the same digits', () => {
      // Vocabulary (a) against vocabulary (b), stated once more against a real group key so
      // that the pairing is covered in the positive direction as well as the negative.
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: 0 })));

      // The narrowing names group zero; the role also belongs to group zero. The two agree
      // here by coincidence of data, and are still read from two separate members.
      expect(store.selectedRoleGroupId()).toBe(0);
      expect(present(store.selectedRole(), 'the selected role').roleGroupId).toBe(0);
      expect(store.roleItems().length).toBe(1);
    });
  });

  // -------------------------------------------------------------------------
  // FAILURES — HELD STRUCTURALLY, NEVER COMPOSED HERE
  // -------------------------------------------------------------------------

  describe('failures are held structurally as the server described them', () => {
    it('holds a forbidden response at warning severity, following the security tree\u2019s precedent', () => {
      // `Website/admin/Security/AccessDenied.ascx.vb` performs no permission check at all and
      // renders BOTH of its branches, at `:L43` and `:L45`, as a yellow warning rather than a
      // red error. That is this feature's own precedent, so a refusal is a warning here too.
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(403, 'authorization.forbidden', 'You do not administer this tenant.'), {
          status: 403,
          statusText: 'Forbidden',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.summary.severity).toBe('warning');
      expect(failure.summary.status).toBe(403);
      expect(failure.operation).toBe('loadRoles');
    });

    it('holds a conflict at error severity, so the two are not levelled', () => {
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleName,
            'A role with the same name already exists. The role was not added.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.summary.severity).toBe('error');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
      expect(failure.operation).toBe('createRole');
    });

    it('carries the correlation value through into the held failure', () => {
      // Required, not optional. The value is the operator's single join key between a browser
      // report and a server log: the outgoing correlation header round-trips into the response
      // body, and dropping it here would sever that join.
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.summary.supportReference).toBe(TRACE_ID);
    });

    it('prefers the correlation member over the trace member when the server sends both', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.', { correlationId: CORRELATION_ID }), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(problem.correlationId).toBe(CORRELATION_ID);
      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.summary.supportReference).toBe(CORRELATION_ID);
    });

    it('holds the per-member dictionary under the server\u2019s own keys, read with bracket access', () => {
      // The dictionary is an index signature and the workspace's compiler settings refuse
      // dotted access to one by design, so every read of it is a bracket read. The keys are the
      // server's model-state keys and are NOT lower-camel.
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(
          aValidationProblem(
            {
              RoleName: ['The role name is required.'],
              BillingFrequency: ['The billing code is not recognised.'],
            },
            'One or more validation errors occurred.',
          ),
          { status: 400, statusText: 'Bad Request' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');
      const fieldErrors = present(problem.errors, 'the per-member dictionary');

      expect(fieldErrors['RoleName']).toEqual(['The role name is required.']);
      expect(fieldErrors['BillingFrequency']).toEqual(['The billing code is not recognised.']);
      expect(failure.summary.hasFieldMessages).toBeTrue();
      expect(failure.conflict)
        .withContext('a model-state refusal is not a recognised conflict')
        .toBeNull();
    });

    it('holds an unsafe message as an inert string, marking nothing as trusted markup', () => {
      // The legacy resource files carry live markup in dozens of values, script elements
      // included, and the legacy precedent for showing an untrusted message was to encode it
      // first. The document is therefore held as received and nothing is wrapped, blessed or
      // rendered here; a consumer binds it as text.
      const hostile = '<script>alert(1)</script>';

      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, hostile), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(typeof problem.detail)
        .withContext('the message is a plain string and not a trusted-markup wrapper')
        .toBe('string');
      expect(problem.detail).toBe(hostile);
      expect(typeof failure.summary.message).toBe('string');
    });

    it('holds a message prefixed with the unspaced legacy break tag verbatim', () => {
      // The legacy code prefixed messages with a break tag in BOTH spellings. Stripping it for
      // display belongs to the shared form-error module; the store holds the raw structured
      // document so nothing downstream is denied the original.
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, '<br>The role was not added.'), {
          status: 409,
          statusText: 'Conflict',
        });

      const problem: ProblemDetails = present(
        present(store.failure(), 'the held failure').problem,
        'the problem document',
      );

      expect(problem.detail).toBe('<br>The role was not added.');
    });

    it('holds a message prefixed with the self-closing legacy break tag verbatim', () => {
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, '<br/>The role was not added.'), {
          status: 409,
          statusText: 'Conflict',
        });

      const problem: ProblemDetails = present(
        present(store.failure(), 'the held failure').problem,
        'the problem document',
      );

      expect(problem.detail).toBe('<br/>The role was not added.');
    });

    it('records absence of a document when the server explained nothing', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(null, { status: 503, statusText: 'Service Unavailable' });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.problem)
        .withContext('a consumer must be able to tell that the server said nothing')
        .toBeNull();
      expect(failure.summary.status)
        .withContext('severity still resolves from the transport status alone')
        .toBe(503);
      expect(failure.summary.severity).toBe('error');
    });

    it('attributes each failure to the command that caused it', () => {
      const operations: readonly RoleStoreOperation[] = ['loadRoles', 'loadRole', 'deleteRole'];

      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[0]);

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(aProblem(404, null, 'No such role.'), { status: 404, statusText: 'Not Found' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[1]);

      store.deleteRole(0);
      expectDelete(ROLE_ZERO_URL)
        .flush(aProblem(409, null, 'In use.'), { status: 409, statusText: 'Conflict' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[2]);
    });

    it('clears the held failure when a fresh command starts', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(store.failure()).not.toBeNull();

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.failure())
        .withContext('a command a user starts deserves a clean slate')
        .toBeNull();
    });

    it('records no document when the response never arrived at all', () => {
      // The network was unavailable, or the request was blocked. The framework reports a
      // status of zero and puts a progress event where a body would go, so reading that slot
      // would manufacture a document saying nothing. Absence is the honest answer, and the
      // failure is still recorded.
      store.loadRoles();
      expectGet(ROLES_URL).error(new ProgressEvent('error'));

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.problem)
        .withContext('a progress event is not a problem document')
        .toBeNull();
      expect(failure.summary.status).toBe(0);
      expect(failure.operation).toBe('loadRoles');
    });

    it('recovers a document the server sent as text rather than as an object', () => {
      // A document can arrive as a string, so it is parsed rather than discarded — the failure
      // code and the correlation value are carried inside it and would otherwise be lost.
      const document: ProblemDetails = aProblem(
        409,
        CONFLICT_CODE.duplicateRoleName,
        'A role with the same name already exists. The role was not added.',
      );

      store.createRole(aCreateRequest());
      expectPost(ROLES_URL).flush(JSON.stringify(document), {
        status: 409,
        statusText: 'Conflict',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the parsed document');

      expect(problem.status).toBe(409);
      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
    });

    it('records no document when a gateway answered with a page instead of one', () => {
      // A failure produced by the reverse proxy rather than by the API arrives as markup. It is
      // parsed defensively: a parse failure yields absence rather than replacing the caller's
      // failure with a syntax error, which would report the wrong problem entirely.
      store.loadRoles();
      expectGet(ROLES_URL).flush('<html><body>504 Gateway Time-out</body></html>', {
        status: 504,
        statusText: 'Gateway Timeout',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.problem).toBeNull();
      expect(failure.summary.status).toBe(504);
      expect(failure.summary.severity).toBe('error');
    });

    it('records no document when the text parsed but described something else', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(JSON.stringify({ message: 'not a problem document' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(present(store.failure(), 'the held failure').problem).toBeNull();
    });

    it('attributes a failure in the opening sequence to the half that failed', () => {
      // The listing screen opens with two reads in sequence. When the FIRST fails the second is
      // never issued, and the failure names the group read rather than the role read.
      store.loadRoleAdministration();
      expectGet(ROLE_GROUPS_URL).flush(aProblem(403, 'authorization.forbidden', 'Refused.'), {
        status: 403,
        statusText: 'Forbidden',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadRoleGroups');
      expect(failure.summary.severity).toBe('warning');
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.rolesLoading()).toBeFalse();
    });

    it('attributes a failure in the second half of the opening sequence to the role read', () => {
      store.loadRoleAdministration();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(aProblem(500, null, 'Unexpected.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadRoles');
      expect(store.roleGroups().length)
        .withContext('the half that succeeded keeps what it read')
        .toBe(1);
      expect(store.busy()).toBeFalse();
    });

    it('attributes a failed group read and settles its own indicator', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(aProblem(500, null, 'Unexpected.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(present(store.failure(), 'the held failure').operation).toBe('loadRoleGroups');
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.roleGroups()).toEqual([]);
    });

    it('attributes a failed assignment read and settles its own indicator', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(aProblem(404, null, 'No such role.'), {
        status: 404,
        statusText: 'Not Found',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadAssignments');
      expect(failure.summary.severity)
        .withContext('a not-found response is a warning, as a refusal is')
        .toBe('warning');
      expect(store.assignmentsLoading()).toBeFalse();
      // The scope was recorded before the read was attempted, so a retry knows what to retry.
      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('attributes a refused role update and leaves the listing untouched', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.updateRole(0, anUpdateRequest());
      expectPut(ROLE_ZERO_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('updateRole');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
      expect(present(store.roleItems()[0], 'the untouched row').roleName).toBe('Administrators');
      expect(store.saving()).toBeFalse();
    });

    it('attributes a refused assignment write and re-reads nothing', () => {
      store.assignUser(7, {
        userId: 42,
        effectiveDate: null,
        expiryDate: null,
        notifyUser: false,
      });
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(
        aProblem(400, null, 'The account is unknown.'),
        { status: 400, statusText: 'Bad Request' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('assignUser');
      expect(failure.summary.severity).toBe('error');
      expect(store.assignmentItems()).toEqual([]);
    });

    it('attributes a refused group rename and leaves the held group untouched', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(
        envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]),
      );

      store.updateRoleGroup(0, { roleGroupName: 'Renamed Groups', description: null });
      expectPut(ROLE_GROUP_ZERO_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleGroupName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('updateRoleGroup');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(present(store.roleGroups()[0], 'the untouched group').roleGroupName).toBe(
        'Site Groups',
      );
    });
  });

  // -------------------------------------------------------------------------
  // ROLE WRITES
  // -------------------------------------------------------------------------

  describe('role writes reconcile from the response and re-read the listing', () => {
    it('creates a role, holds the server\u2019s own payload, then re-reads the listing', () => {
      const request: CreateRoleRequest = aCreateRequest();

      store.createRole(request);

      const write = expectPost(ROLES_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Subscribers' })), {
        status: 201,
        statusText: 'Created',
      });

      // Re-read rather than spliced in: where the role falls, and whether it falls on the
      // current page at all, depends on the narrowing, the ordering and the page size, none of
      // which the store may re-implement.
      expectGet(ROLES_URL)
        .flush(pageOf([aRoleListItem({ roleId: 0, roleName: 'Subscribers' })], 1));

      expect(present(store.selectedRole(), 'the created role').roleId).toBe(0);
      expect(store.roleItems().length).toBe(1);
      expect(store.saving()).toBeFalse();
    });

    it('patches the listing row from the response body, then re-reads', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(
          pageOf(
            [
              aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
              aRoleListItem({ roleId: 1, roleName: 'Registered Users' }),
            ],
            2,
          ),
        );

      const request: UpdateRoleRequest = anUpdateRequest();

      store.updateRole(0, request);

      const write = expectPut(ROLE_ZERO_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Site Administrators' })));

      // The patch copies the SERVER's values, not the request's, so it cannot show something
      // the server did not accept — and it matches the row keyed zero by identity, which is
      // exactly the row a truthy test would omit.
      expect(store.roleItems().map((item) => item.roleName)).toEqual([
        'Site Administrators',
        'Registered Users',
      ]);

      // The re-read then settles membership, because a changed grouping can move the role out
      // of the current narrowing entirely, which no local patch can determine.
      expectGet(ROLES_URL)
        .flush(pageOf([aRoleListItem({ roleId: 1, roleName: 'Registered Users' })], 1));

      expect(store.roleItems().map((item) => item.roleId)).toEqual([1]);
    });

    it('deletes the role keyed zero, discards the matching selection, then re-reads', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0 })));

      store.deleteRole(0);

      const removal = expectDelete(ROLE_ZERO_URL);

      removal.flush(null, { status: 204, statusText: 'No Content' });

      // MIGRATION: for a ROLE an empty success really does mean the row is gone, which is what
      // makes the assignment removal the exception rather than the rule — and why the two are
      // deliberately not implemented alike.
      expect(store.selectedRole()).toBeNull();

      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.roleItems()).toEqual([]);
    });

    it('keeps a selection that is not the deleted role', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7 })));

      store.deleteRole(0);
      expectDelete(ROLE_ZERO_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(present(store.selectedRole(), 'the surviving selection').roleId).toBe(7);
    });
  });

  // -------------------------------------------------------------------------
  // RESPONSIBILITIES THAT DELIBERATELY LIVE ELSEWHERE
  // -------------------------------------------------------------------------

  describe('the store publishes no presentation and decides no authorisation', () => {
    it('publishes no formatting member of any kind', () => {
      // Legacy: `Roles.ascx.vb:L152-L162` rendered a period as a phrase and `:L175` rendered a
      // fee as currency; a third helper in `SecurityRoles.ascx.vb` rendered a date and filtered
      // absent ones out for display. All three are presentation and belong to a component or a
      // shared pipe. A member found here would be a code-organisation violation to report, not
      // a behaviour to test.
      const formatters: readonly string[] = publishedMembers(store).filter((name) =>
        /format|render|display|currency|toFixed|toLocale|label|caption|text$/i.test(name),
      );

      expect(formatters)
        .withContext('presentation must not leak into held state')
        .toEqual([]);
    });

    it('publishes no permission-deciding member', () => {
      // Authorisation is decided server-side and arrives as a refusal or a conflict. The two
      // closed permission vocabularies — the persisted keys and the policy names — are never
      // interchanged, and neither carries a negation prefix in this generation of the product.
      // Nothing here decides anything about either.
      // The fragments are ANCHORED rather than loose. A loose fragment matching a role-bearing
      // name would flag `hasRoleGroups`, which is a data projection reporting whether the
      // tenant declares any group at all and decides nothing about permission — the sort of
      // false positive that gets an assertion weakened rather than corrected.
      const decidingMembers: readonly string[] = [
        'hasPermission',
        'isInRole',
        'hasRole',
        'isAuthorised',
        'isAuthorized',
        'authorise',
        'authorize',
        'evaluatePermission',
        'isSuperUser',
        'isAdmin',
        'canView',
        'canEdit',
        'canRead',
        'canWrite',
        'canAdminister',
      ];
      const deciders: readonly string[] = publishedMembers(store).filter((name) =>
        decidingMembers.includes(name),
      );

      expect(deciders)
        .withContext('the server is the sole authority on what is permitted')
        .toEqual([]);
    });

    it('publishes no cache, expiry stamp or invalidation member', () => {
      // The legacy code reached a static cache helper from well over a hundred call sites with
      // coarse portal-wide and host-wide invalidation. A second, unsynchronised cache here
      // would answer from stale state after another administrator's change.
      const caching: readonly string[] = publishedMembers(store).filter((name) =>
        /cache|invalidat|expiresAt|staleness|isStale|evict/i.test(name),
      );

      expect(caching).toEqual([]);
    });

    it('derives no assignment status, because the server never sends one', () => {
      // The status vocabulary exists in the contract module but is a member of neither the role
      // nor the membership contract, so no held value can carry one. The store's own term
      // classification is a different vocabulary entirely, and the two are disjoint — which is
      // what proves a status has not been quietly re-derived from an instant comparison.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(envelopeOf(aRole({ roleId: 7, billingFrequency: 'M', billingPeriod: 1 })));

      const classification: BillingTermsBound | null = store.selectedRoleBillingTerms();

      expect(classification).toBe('Bounded');

      // The two vocabularies are disjoint, and the COMPILER enforces it: writing the
      // comparison against a status value directly is rejected outright, because no member of
      // the status union is assignable to the term union. Widening to a plain string is what
      // lets the disjointness be stated as a runtime assertion at all, and the emptiness of
      // the intersection below is the machine-checkable form of the same claim.
      const termVocabulary: readonly string[] = [
        'Unbounded',
        'Perpetual',
        'Bounded',
        'Unsupported',
      ];
      const statusVocabulary: readonly string[] = ROLE_STATUS_VALUES;
      const overlap: readonly string[] = termVocabulary.filter((term) =>
        statusVocabulary.includes(term),
      );

      expect(overlap)
        .withContext('a term classification can never be mistaken for an assignment status')
        .toEqual([]);

      const held: string = present(classification, 'the term classification');

      expect(termVocabulary.includes(held)).toBeTrue();
      expect(statusVocabulary.includes(held)).toBeFalse();

      const statusMembers: readonly string[] = publishedMembers(store).filter((name) =>
        /roleStatus|assignmentStatus|isExpired|isActive|isPending/i.test(name),
      );

      expect(statusMembers).toEqual([]);
    });

    it('publishes no member that composes a request address', () => {
      // The wire belongs to the service. A second opinion about a query string here would be a
      // second answer, and this file asserts addresses the service composed rather than any
      // the store composed.
      const wireMembers: readonly string[] = publishedMembers(store).filter((name) =>
        /^url|Url$|endpoint|baseAddress|httpClient|apiBase/i.test(name),
      );

      expect(wireMembers).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // IMMUTABILITY AND LIFECYCLE
  // -------------------------------------------------------------------------

  // -------------------------------------------------------------------------
  // STALE RESPONSES AND SESSION TEARDOWN
  // -------------------------------------------------------------------------
  //
  // Two properties, one mechanism. Every read holds its handle and abandons the previous
  // request before dispatching, so a slice is a function of the LATEST request rather than of
  // whichever response happens to arrive last; and `reset()` abandons everything before
  // clearing, so no response already on the wire can repopulate what a sign-out discarded.
  describe('stale responses cannot overwrite newer state', () => {
    it('abandons a superseded listing read, so a late answer can never land', () => {
      // ⚠ THE REGRESSION THIS PINS DOWN. Rapid narrowing, ordering or filter changes issue A
      // then B. Without cancellation, B answering first and A answering second leaves the
      // slice describing A - the narrowing the operator has already moved on from.
      store.setRolesQuery('alpha');
      const first = expectGet(ROLES_URL);

      store.setRolesQuery('beta');
      const second = expectGet(ROLES_URL);

      expect(first.cancelled)
        .withContext('the superseded read is abandoned when the next one is dispatched')
        .toBeTrue();

      // The testing backend refuses to answer a cancelled request at all, which is a stronger
      // statement than any arrival order this specification could stage: the stale read cannot
      // deliver a value to this store under ANY interleaving.
      expect(() => first.flush(pageOf([aRoleListItem({ roleName: 'Alpha' })], 1))).toThrowError(
        /cancelled/i,
      );

      second.flush(pageOf([aRoleListItem({ roleId: 1, roleName: 'Beta' })], 1));

      expect(store.roleItems().length).toBe(1);
      expect(store.roleItems()[0]?.roleName)
        .withContext('the state describes the latest request, never the last response to arrive')
        .toBe('Beta');
    });

    it('abandons a superseded single-role read, including one addressing role zero', () => {
      store.selectRole(0);
      const first = expectGet(`${ROLES_URL}/0`);

      store.selectRole(5);
      const second = expectGet(`${ROLES_URL}/5`);

      expect(first.cancelled).toBeTrue();

      second.flush(envelopeOf(aRole({ roleId: 5, roleName: 'Five' })));

      // Role ZERO is a real role - `dbo.Roles.RoleID` is `IDENTITY(0, 1)` - so the abandoned
      // read addressed a legitimate record rather than an absent one, and it still cannot land.
      expect(() => first.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Zero' })))).toThrowError(
        /cancelled/i,
      );

      expect(store.selectedRole()?.roleId).toBe(5);
    });

    it('does NOT let one read cancel a different slice, because each holds its own handle', () => {
      // The assignment screen legitimately reads a role and its members at the same time. One
      // shared handle would make the second dispatch cancel the first and leave that slice empty.
      store.selectRole(7);
      const roleCall = expectGet(`${ROLES_URL}/7`);

      store.loadAssignments(7);
      const membersCall = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(roleCall.cancelled)
        .withContext('an assignment read must not abandon the single-role read')
        .toBeFalse();

      roleCall.flush(envelopeOf(aRole({ roleId: 7 })));
      membersCall.flush(pageOf([anAssignment()], 1));

      expect(store.selectedRole()?.roleId).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });
  });

  describe('reset cannot be repopulated by work that was already in flight', () => {
    it('cancels every read, lowers every flag, and leaves nothing outstanding', () => {
      store.loadRoles();
      const rolesCall = expectGet(ROLES_URL);

      store.loadRoleGroups();
      const groupsCall = expectGet(ROLE_GROUPS_URL);

      store.selectRole(3);
      const roleCall = expectGet(`${ROLES_URL}/3`);

      store.loadAssignments(7);
      const membersCall = expectGet(ROLE_SEVEN_MEMBERS_URL);

      store.reset();

      // ⚠ CLEARING A SLICE WHILE ITS REQUEST IS IN FLIGHT IS THE SAME DISCLOSURE WITH A DELAY IN
      // FRONT OF IT: the response repopulates exactly what the sign-out discarded. Both halves -
      // cancel, then clear - are necessary and neither is sufficient.
      expect(rolesCall.cancelled).toBeTrue();
      expect(groupsCall.cancelled).toBeTrue();
      expect(roleCall.cancelled).toBeTrue();
      expect(membersCall.cancelled).toBeTrue();

      expect(store.rolesLoading()).toBeFalse();
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.selectedRoleLoading()).toBeFalse();
      expect(store.assignmentsLoading()).toBeFalse();
      expect(store.busy()).toBeFalse();

      // Nothing is left outstanding, which the `afterEach` verification would report anyway; it is
      // asserted here so the failure names this behaviour rather than the next specification.
      httpMock.verify();

      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentItems()).toEqual([]);
    });

    it('abandons the complete-listing walk mid-flight rather than finishing it', () => {
      const fullPage: readonly RoleListItem[] = Array.from(
        { length: ROLES_FETCH_PAGE_SIZE },
        (_unused, index) => aRoleListItem({ roleId: index, roleName: `Role ${index}` }),
      );

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf(fullPage, ROLES_FETCH_PAGE_SIZE * 3));

      // The walk has issued its second request and is waiting on it.
      const second = expectGet(ROLES_URL);

      expect(second.request.params.get('pageIndex')).toBe('1');

      store.reset();

      expect(second.cancelled)
        .withContext('a multi-request walk must be abandonable as one unit')
        .toBeTrue();
      expect(store.roleItems()).toEqual([]);
      expect(store.rolesLoading()).toBeFalse();
      httpMock.verify();
    });

    it('releases every handle on teardown', () => {
      store.loadRoles();
      const call = expectGet(ROLES_URL);

      store.ngOnDestroy();

      expect(call.cancelled)
        .withContext('a request left listening across an injector boundary reports into a replaced store')
        .toBeTrue();
      httpMock.verify();
    });
  });

  describe('held state is replaced immutably and can be returned to its initial values', () => {
    it('replaces the listing rather than editing the sequence a consumer already read', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(pageOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })], 1));

      const readEarlier: readonly RoleListItem[] = store.roleItems();

      store.updateRole(0, anUpdateRequest());
      expectPut(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, roleName: 'Site Administrators' })));

      // A consumer that read the sequence before the write still sees what it read. That is
      // possible solely because the update replaced the sequence instead of editing it in
      // place, and it is what lets a reference-identity change-detection strategy work at all.
      expect(present(readEarlier[0], 'the earlier row').roleName).toBe('Administrators');
      expect(present(store.roleItems()[0], 'the current row').roleName).toBe(
        'Site Administrators',
      );
      expect(store.roleItems()).not.toBe(readEarlier);

      expectGet(ROLES_URL).flush(pageOf([], 0));
    });

    it('replaces the group sequence rather than editing it in place', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]));

      const readEarlier: readonly RoleGroup[] = store.roleGroups();

      store.updateRoleGroup(0, { roleGroupName: 'Renamed Groups', description: null });
      expectPut(ROLE_GROUP_ZERO_URL)
        .flush(envelopeOf(aRoleGroup({ roleGroupId: 0, roleGroupName: 'Renamed Groups' })));

      expect(present(readEarlier[0], 'the earlier group').roleGroupName).toBe('Site Groups');
      expect(present(store.roleGroups()[0], 'the current group').roleGroupName).toBe(
        'Renamed Groups',
      );
      expect(store.roleGroups()).not.toBe(readEarlier);
    });

    it('discards the held failure on request, without contacting the server', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(store.failure()).not.toBeNull();

      store.clearError();

      expect(store.failure()).toBeNull();
    });

    it('discards the selected role on request, without contacting the server', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0 })));

      store.clearSelectedRole();

      expect(store.selectedRole()).toBeNull();
    });

    it('returns every slice to its initial value, the narrowing to the measured default', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 1));

      store.reset();

      // For a sign-out or a tenant change, after which nothing held is still true. Not a cache
      // eviction: there is no cache to evict.
      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.assignmentItems()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentsRoleId()).toBeNull();
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.groupFilter().kind)
        .withContext('the reset target is the ungrouped intent, per the measured legacy default')
        .toBe('GlobalRoles');
      expect(store.rolesPage().pageIndex).toBe(0);
      // The role listing is read WHOLE, so its coordinate reports the size its requests
      // actually carry rather than the shared paged default the assignments use.
      expect(store.rolesPage().pageSize).toBe(ROLES_FETCH_PAGE_SIZE);
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsPage().pageSize).toBe(DEFAULT_PAGE_SIZE);
    });

    it('reports itself busy while a read is in flight and idle once it settles', () => {
      store.loadRoles();

      expect(store.rolesLoading()).toBeTrue();
      expect(store.busy()).toBeTrue();

      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesLoading()).toBeFalse();
      expect(store.busy()).toBeFalse();
    });

    it('reports itself busy while a write is in flight and idle once it settles', () => {
      store.createRole(aCreateRequest());

      expect(store.saving()).toBeTrue();
      expect(store.busy()).toBeTrue();

      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'), {
          status: 409,
          statusText: 'Conflict',
        });

      expect(store.saving())
        .withContext('a refused write must still settle the in-flight indicator')
        .toBeFalse();
      expect(store.busy()).toBeFalse();
    });
  });
  // -------------------------------------------------------------------------
  // SESSION ISOLATION AND READ CONCURRENCY
  //
  // This store had a `reset` that cleared its slices and released NOTHING, and no request handles
  // at all. Two consequences, neither of which looks like a defect from inside one screen:
  //
  // Clearing without releasing meant a reset cleared the slices and then let the responses already
  // in flight repopulate them moments later — so the store ended up holding the PREVIOUS SESSION'S
  // roles, groups and memberships, with no command issued to explain where they came from.
  //
  // No handles meant two reads of the same thing raced, and the winner was whichever response
  // arrived LAST rather than whichever request was issued last. Responses are not ordered by
  // request order, so a first request delayed behind a slow query lands after a second and
  // overwrites the newer page with the older one — a grid showing a page the pager says it is not
  // on, with nothing reproducible about it.
  // -------------------------------------------------------------------------
  describe('session isolation and read concurrency', () => {
    // The shared helper matches on `request.url`, which is the path WITHOUT its query string, so
    // the bare paths are what these cases claim. The coordinates and the narrowing the listing
    // carries are asserted by the cases that exist for that purpose.
    const ROLES_LISTING_URL = ROLES_URL;
    const ZERO_MEMBERS_LISTING_URL = ROLE_ZERO_MEMBERS_URL;
    const SEVEN_MEMBERS_LISTING_URL = ROLE_SEVEN_MEMBERS_URL;

    it('cancels a read in flight on reset, so its answer cannot repopulate the store', () => {
      store.loadRoles();

      const pending = expectGet(ROLES_LISTING_URL);

      store.reset();

      expect(pending.cancelled)
        .withContext('the request is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.rolesLoading()).toBeFalse();
      expect(store.roles().items.length).toBe(0);
    });

    it('cancels a write in flight on reset, so its callback cannot act for the ended session', () => {
      // A write's callback re-reads the listing and records an outcome. Left listening across a
      // boundary it would do both on behalf of the session that ended.
      store.createRole(aCreateRequest());

      const pending = expectPost(ROLES_URL);

      store.reset();

      expect(pending.cancelled).toBeTrue();
      expect(store.saving()).toBeFalse();
    });

    it('abandons the earlier role listing read when a second is issued', () => {
      // ⚠ THE STALE-ANSWER RACE. Both requests answer the same question, so the LAST response to
      // arrive wins — which is not necessarily the one asked for last.
      store.loadRoles();

      const first = expectGet(ROLES_LISTING_URL);

      store.loadRoles();

      const second = expectGet(ROLES_LISTING_URL);

      expect(first.cancelled).toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(pageOf([aRoleListItem({ roleId: 5, roleName: 'Second answer' })], 1));

      expect(store.roles().items.length).toBe(1);
      expect(store.roles().items[0].roleName).toBe('Second answer');
    });

    it('abandons the earlier single-role read when another role is selected', () => {
      // The read most likely to be issued twice in quick succession, because moving between rows
      // re-issues it — and the two answers describe DIFFERENT roles, so a stale winner shows one
      // role's billing terms under another role's name.
      store.selectRole(0);

      const first = expectGet(ROLE_ZERO_URL);

      store.selectRole(7);

      const second = expectGet(ROLE_SEVEN_URL);

      expect(first.cancelled).toBeTrue();

      second.flush(envelopeOf(aRole({ roleId: 7, roleName: 'The one asked for' })));

      expect(store.selectedRole()?.roleId).toBe(7);
      expect(store.selectedRoleLoading()).toBeFalse();
    });

    it('abandons the earlier membership read when another role’s members are listed', () => {
      store.loadAssignments(0);

      const first = expectGet(ZERO_MEMBERS_LISTING_URL);

      store.loadAssignments(7);

      const second = expectGet(SEVEN_MEMBERS_LISTING_URL);

      expect(first.cancelled).toBeTrue();

      second.flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignments().items.length).toBe(1);
    });

    it('releases both reads of the combined administration chain', () => {
      // The chain performs BOTH reads, so its handle occupies both slots. Releasing either must
      // release the chain, and releasing an already-released subscription must be a no-op.
      store.loadRoleAdministration();

      const groups = expectGet(ROLE_GROUPS_URL);

      store.reset();

      expect(groups.cancelled).toBeTrue();
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.rolesLoading()).toBeFalse();
    });

    it('keeps accepting writes after a reset, which a Subscription container would have broken', () => {
      // ⚠ A REGRESSION GUARD FOR A REAL TRAP. An RxJS `Subscription` used as a container is CLOSED
      // once unsubscribed, and anything added afterwards is unsubscribed the instant it is added —
      // so the FIRST boundary would release the writes correctly and then silently cancel every
      // subsequent write for the rest of the application's life.
      store.reset();

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });

      const pending = expectPost(ROLE_GROUPS_URL);

      expect(pending.cancelled)
        .withContext('a write issued after a reset must not be cancelled on arrival')
        .toBeFalse();

      pending.flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });

      // The create re-reads the groups, which proves the callback ran rather than being discarded.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      expect(store.roleGroups().length).toBe(1);
      expect(store.saving()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // WRITE IDENTITY, AND WHY AN AGGREGATE FLAG COULD NOT SETTLE A WRITE
  //
  // This store used to publish ONE boolean for "a write is in flight" and nothing else. It is
  // provided at the application root, so every screen that dispatched a write watched that one
  // boolean fall and then read the store's single failure slot to learn the outcome. Three distinct
  // wrong answers follow from that, and none of them is visible from inside one screen:
  //
  //   (a) TWO WRITES, ONE FLAG. Screen A dispatches, screen B dispatches, A settles -> the flag
  //       falls while B is still open, and BOTH screens conclude their own write is done.
  //   (b) SOMEBODY ELSE'S FAILURE. A's write succeeds and B's is refused. A reads the failure slot,
  //       finds B's refusal there and reports it as the outcome of its own successful write.
  //   (c) A SUCCESS READ AS A FAILURE, AND THE REVERSE. The slot is cleared at every dispatch, so
  //       whether a screen sees a refusal at all depends on which dispatch happened to be last.
  //
  // The correction is an identifier per write, handed back at dispatch, and a published result that
  // carries the identifier, the operation and that write's OWN failure. The cases below pin each
  // half: the identity is unique and monotone, the result names the write it belongs to, and the
  // failure travels with the result rather than being read out of shared state.
  // -------------------------------------------------------------------------
  describe('every write is settled by identity rather than by an aggregate flag', () => {
    it('hands back a distinct identifier for every write, and never the absent value', () => {
      // Zero is reserved as "no write awaited" by the screens that hold one of these, so the
      // FIRST identifier must not be zero. The counter therefore pre-increments, and that is
      // asserted here rather than left to a comment.
      const first = store.createRole(aCreateRequest());
      const second = store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });

      expect(first).withContext('the absent marker must never be issued').not.toBe(0);
      expect(second).not.toBe(first);
      expect(second).toBeGreaterThan(first);

      expectPost(ROLES_URL).flush(envelopeOf(aRole({ roleId: 7 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));
      expectPost(ROLE_GROUPS_URL).flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));
    });

    it('publishes a result naming the write that settled, its operation and its own outcome', () => {
      const issued = store.deleteRole(7);

      expect(store.mutation())
        .withContext('nothing is published while the write is open')
        .toBeNull();

      expectDelete(ROLE_SEVEN_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      const settled = present(store.mutation(), 'the settled write');

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('deleteRole');
      expect(settled.failure)
        .withContext('a success settles with no failure attached')
        .toBeNull();
    });

    it('carries a refusal ON the settled result, so no screen reads it out of shared state', () => {
      const issued = store.updateRole(7, anUpdateRequest());

      expectPut(ROLE_SEVEN_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const settled = present(store.mutation(), 'the settled write');
      const failure = present(settled.failure, 'the refusal on the result');

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('updateRole');
      expect(failure.operation).toBe('updateRole');
      expect(failure.status).toBe(409);
    });

    it('settles the first of two open writes without settling the second', () => {
      // ⚠ THIS IS DEFECT (a), AND IT IS THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. Both writes
      // are open; the second answers first. The result must name the SECOND, and the aggregate must
      // stay raised because the first is still open.
      const firstWrite = store.createRole(aCreateRequest());
      const secondWrite = store.createRoleGroup({
        roleGroupName: 'Paid Services',
        description: null,
      });

      const roleCreate = expectPost(ROLES_URL);

      expectPost(ROLE_GROUPS_URL).flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      expect(present(store.mutation(), 'the settled write').id).toBe(secondWrite);
      expect(store.saving())
        .withContext('one write settling must not report the other as settled')
        .toBeTrue();

      roleCreate.flush(envelopeOf(aRole({ roleId: 7 })), { status: 201, statusText: 'Created' });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(present(store.mutation(), 'the settled write').id).toBe(firstWrite);
      expect(store.saving())
        .withContext('the aggregate falls only once every write has settled')
        .toBeFalse();
    });

    it('does not attach one write\u2019s refusal to another write\u2019s result', () => {
      // ⚠ THIS IS DEFECT (b). The refused write and the successful one overlap, and the successful
      // one settles LAST — so the shared failure slot holds a refusal at the very moment the
      // successful write's result is published. The result must still carry no failure.
      store.updateRole(7, anUpdateRequest());

      const refused = expectPut(ROLE_SEVEN_URL);
      const succeeding = store.createRoleGroup({
        roleGroupName: 'Paid Services',
        description: null,
      });
      const groupCreate = expectPost(ROLE_GROUPS_URL);

      refused.flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'), {
        status: 409,
        statusText: 'Conflict',
      });

      // The refusal IS in the shared slot at this instant, which is what makes the second half of
      // this case meaningful rather than vacuous.
      expect(present(store.failure(), 'the shared slot').operation).toBe('updateRole');

      groupCreate.flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      const settled = present(store.mutation(), 'the settled write');

      expect(settled.id).toBe(succeeding);
      expect(settled.failure)
        .withContext('a successful write must not inherit the other write\u2019s refusal')
        .toBeNull();

      // ⚠ AND THE SHARED SLOT IS ALREADY EMPTY, WHICH IS DEFECT (c) IN ITS PUREST FORM. The refusal
      // WAS recorded there — asserted above — but the successful write's own listing re-read cleared
      // it moments later, because every read clears the slot at dispatch.
      // So a screen that learned its outcome from this slot would here have concluded that a REFUSED
      // update succeeded, purely because an unrelated write finished afterwards. Whether the slot
      // holds anything at all depends on what else the application happened to do next, which is
      // exactly why the outcome travels on the settled result instead.
      expect(store.failure())
        .withContext('the shared slot cannot be relied on to still hold the refusal')
        .toBeNull();
    });

    it('clears the published result and the pending count on a session boundary', () => {
      store.deleteRole(7);
      expectDelete(ROLE_SEVEN_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.mutation()).not.toBeNull();

      store.reset();

      expect(store.mutation())
        .withContext('a result from the ended session must not settle a new one\u2019s write')
        .toBeNull();
      expect(store.saving()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // WHICH ROLE A SETTLED MEMBERSHIP WRITE MAY REFRESH
  // -------------------------------------------------------------------------
  describe('a membership write refreshes only a listing it cannot misattribute', () => {
    const request: RoleAssignmentRequest = Object.freeze({
      userId: 42,
      effectiveDate: null,
      expiryDate: null,
      notifyUser: false,
    });

    it('adopts the role it wrote to when no listing is in scope', () => {
      // Nothing is being viewed, so there is no set to overwrite and no heading to mis-label. The
      // refresh is what makes the enrolment observable at all on a fresh store.
      expect(store.assignmentsRoleId()).toBeNull();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refreshes the listing when the write addressed the role already in scope', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refuses to move the scope when a DIFFERENT role is the one being viewed', () => {
      // ⚠ THE CROSS-ROLE REPUBLICATION. Role 7 is enrolled, the operator moves to role 0, and role
      // 7's write then settles. The previous code forced the scope back to 7, cancelled the read of
      // 0 and rendered 7's named members under 0's heading — silently, because a cancelled read
      // delivers neither a value nor an error.
      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      store.loadAssignments(0);

      const otherRead = expectGet(ROLE_ZERO_MEMBERS_URL);

      write.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId())
        .withContext('the scope stays where the operator put it')
        .toBe(0);
      expect(otherRead.cancelled)
        .withContext('the read the operator is waiting for must survive')
        .toBeFalse();

      otherRead.flush(pageOf([anAssignment({ roleId: 0, userId: 9 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
      expect(present(store.assignmentItems()[0], 'the held row').roleId).toBe(0);
    });

    it('applies the same test to a removal, which re-reads for a different reason', () => {
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL).flush(null, { status: 204, statusText: 'No Content' });

      // No second read is opened. `httpMock.verify()` in the shared teardown is what proves it.
      expect(store.assignmentsRoleId()).toBe(0);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refreshes the listing on screen when the write addressed the role it shows', () => {
      // The ordinary path, now stated through the screen's own announcement rather than through the
      // scope standing in for it.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('refuses the refresh once the screen that asked has been LEFT', () => {
      // ⚠ THE DEFECT RUNTIME VALIDATION CAUGHT, and the reason the screen's announcement exists at
      // all. The scope test alone cannot see this case: leaving a membership listing does not blank
      // the rows, so the scope STILL names role 7 here and agreed with the write. Reproduced three
      // times out of three in a real browser by enrolling an account and clicking back to the role
      // listing before the write settled — which is the ordinary path from one role's memberships to
      // another's, the listing being the unavoidable stop between them.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      // The operator returns to the role listing: the membership screen is destroyed, so it closes.
      store.closeAssignmentsView(7);

      write.flush(null, { status: 204, statusText: 'No Content' });

      // No re-read is opened. `httpMock.verify()` in the shared teardown is what proves it.
      expect(store.assignmentsRoleId())
        .withContext('the rows keep their label; only the refresh is refused')
        .toBe(7);
    });

    it('applies the same refusal to a removal, which re-reads for a different reason', () => {
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      store.removeAssignment(7, 42);

      const write = expectDelete(ROLE_SEVEN_MEMBER_URL);

      store.closeAssignmentsView(7);
      write.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('refreshes again for a screen that leaves and comes back to the same role', () => {
      // Returning re-announces, so the refusal is not sticky: the operator is looking at role 7's
      // grid again and must see the change their own write made.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      store.closeAssignmentsView(7);
      store.openAssignmentsView(7);

      write.flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('ignores a departing screen that names a role another screen has already claimed', () => {
      // A screen tears down and a replacement announces itself in an order this store does not
      // control. Were the close unconditional, the predecessor's teardown would un-announce the
      // replacement and the replacement's own legitimate refresh would then be refused.
      store.openAssignmentsView(0);
      store.closeAssignmentsView(7);

      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(0, {
        ...request,
        userId: 9,
      });
      expectPost(ROLE_ZERO_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0, userId: 9 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('adopts again after a session reset has discarded the departed screen', () => {
      // A reset returns the store to "nothing has been on screen", not to "the screen has left", so
      // the adoption contract holds for the next session.
      store.openAssignmentsView(7);
      store.closeAssignmentsView(7);
      store.reset();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('treats role zero as an occupied scope rather than as an absent one', () => {
      // Role keys are `IDENTITY(0, 1)`, so role 0 is the Administrators role of a fresh tenant. A
      // truthiness test on the scope would read it as "nothing in scope" and take the adoption
      // branch — reintroducing the republication for precisely the most important role.
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId()).toBe(0);
    });
  });


});

/**
 * A role creation request carrying the awkward values rather than tidy ones.
 *
 * Declared as a function so no case can mutate a value another depends on, and annotated
 * so the compiler checks every member spelling against the contract.
 *
 * @returns One creation request.
 */
function aCreateRequest(): CreateRoleRequest {
  return {
    roleName: 'Subscribers',
    description: '',
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    isPublic: false,
    autoAssignment: false,
    roleGroupId: null,
    rsvpCode: '',
    iconFile: null,
  };
}

/**
 * A role update request carrying the awkward values rather than tidy ones.
 *
 * @returns One update request.
 */
function anUpdateRequest(): UpdateRoleRequest {
  return {
    roleName: 'Site Administrators',
    description: '',
    roleGroupId: null,
    isPublic: false,
    autoAssignment: false,
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    rsvpCode: '',
    iconFile: null,
  };
}
