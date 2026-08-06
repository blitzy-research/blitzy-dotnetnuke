/**
 * Signal-backed state for role administration: the role listing and its three-way
 * role-group narrowing, the role-group listing, the role currently under edit, and
 * the user-to-role assignments that join accounts to a role.
 *
 * Replaces `Website/admin/Security/Roles.ascx.vb`,
 * `Website/admin/Security/SecurityRoles.ascx.vb`,
 * `Website/admin/Security/EditRoles.ascx.vb` and
 * `Website/admin/Security/EditGroups.ascx.vb`, together with the orchestration half of
 * `Library/Components/Security/Roles/RoleController.vb`.
 *
 * ---------------------------------------------------------------------------
 * WHAT LIVES HERE, AND WHAT DELIBERATELY DOES NOT
 *
 * `core/services/role.service.ts` is one method per endpoint, returning the cold
 * observable the HTTP client produced and never subscribing. Composition is therefore
 * this module's job, and it is the only job this module has:
 *
 * - Multi-call sequencing. Two sequences are load-bearing: read the groups and only
 *   then read the roles, and re-read the assignments after a removal.
 * - The loading and failure slices, so a screen never derives either from the data.
 * - Derived projections over held state, immutably updated.
 *
 * Everything below is somebody else's file, and each exclusion is a decision rather
 * than an omission:
 *
 * - NO HTTP. No client, no parameter object, no URL, no base address. The service owns
 *   the wire, and a second opinion about a query string here would be a second answer.
 * - NO VALIDATION. The feature forms under `app/features/role/**` own their rules, and
 *   the server refuses a malformed request with a field-level 400 regardless.
 * - NO FORMATTING. The legacy screen carried three display helpers — a period renderer
 *   and a fee renderer at `Roles.ascx.vb:L152-L162` and `:L175`, and a date renderer in
 *   `SecurityRoles.ascx.vb` that filtered nulls out for display. All three are
 *   presentation and belong to a component or a shared pipe. This module produces no
 *   display text of any kind, and no currency, plural or date rendering.
 * - NO AUTHORISATION DECISION. The affordance projections below say what a screen may
 *   usefully offer; the server says what is permitted, with 403 or 409, and it is the
 *   only authority.
 * - NO CACHE. See MIGRATION note 14.
 * - NO PERMISSION STATE. Permissions are a read-only catalogue reached through
 *   `core/services/permission.service.ts`; there is deliberately no permission store,
 *   and absorbing that state here would invent one.
 *
 * ---------------------------------------------------------------------------
 * NO USER-SPECIFIED RULES GOVERN THIS FILE
 *
 * The project supplies no rules document — the rules review returns a one-line absence
 * statement, including for ranges beginning past the first line, so there is no body to
 * page through. No file therefore enters scope on rule grounds. Absence is not licence
 * to lower the bar: the migration plan's enterprise baseline governs instead, and the
 * paragraphs above are its code-organisation item applied to this file.
 *
 * ---------------------------------------------------------------------------
 * THE PAGING SHAPES ARE NOT UNIFORM, AND MUST NOT BE MADE TO LOOK UNIFORM
 *
 * Settled by reading the service rather than by preferring one sentence of the plan,
 * which describes the role listing as paged in one place and unpaged in another:
 *
 * - The ROLE listing is PAGED. `RoleService.listRoles` answers
 *   `PagedResponse<RoleListItem>`, the service imports the paged envelope, the wire
 *   contract states it in prose, and the shared query builder carries a purpose-built
 *   entry point for it. Four independent authorities, one answer.
 * - The ASSIGNMENT listing is PAGED too. `RoleService.listUsers` answers
 *   `PagedResponse<UserRole>`.
 * - The ROLE-GROUP listing is UNPAGED. `RoleService.listRoleGroups` answers
 *   `ApiResponse<readonly RoleGroup[]>` — a plain array in the single-payload envelope.
 *
 * So this module holds a page coordinate for the two paged listings and holds NOTHING
 * of the kind for the groups: no coordinate, no size, no total, no pager projection.
 * The legacy screen paged neither (`Roles.ascx.vb:L77` and `:L108` both bind untyped
 * lists), so paging is an ADDED capability on two of the three and a caller wanting
 * every role asks for a large enough page rather than omitting the coordinates.
 *
 * ---------------------------------------------------------------------------
 * MIGRATION RECORD — every deliberate divergence, with the line it was measured
 * against. Each was read first-hand in this repository. Notes elaborated on the member
 * they govern are cross-referenced rather than repeated.
 *
 * 1. VIEW STATE AND SESSION STATE ARE ELIMINATED, AND THERE WAS NOTHING TO TRANSLATE.
 *    The migration plan describes these stores as replacing legacy view state, but the
 *    security tree contains ZERO view-state sites — as do the module and page admin
 *    trees — and session state has zero sites anywhere in the migrated surface. What
 *    the legacy screen did instead was re-read everything on every postback: the bind
 *    routine re-queried the roles and the groups each time, and `:L91` rebound the grid.
 *    Signals replace that with held state, updated immutably, and an explicit re-read
 *    only where the server may have diverged from what is held — see note 7. The one
 *    view-state key that does appear elsewhere in the admin tree, the return-navigation
 *    referrer, is a ROUTER concern and deliberately never becomes a signal, which is
 *    what the two edit screens use to get back to the listing.
 *
 * 2. THE THREE-WAY GROUP NARROWING IS A TYPED DISCRIMINATOR, NOT A SIGNED INTEGER.
 *    See {@link RoleGroupFilter}.
 *
 * 3. THE MEASURED DEFAULT IS GLOBAL ROLES, NOT ALL ROLES. See
 *    {@link DEFAULT_ROLE_GROUP_FILTER}.
 *
 * 4. DELETING A GROUP RESETS THE NARROWING, AND THE TYPE SYSTEM IS NOW THE GUARD ON
 *    WHICH GROUPS MAY BE DELETED. See {@link RoleStore.deleteRoleGroup}.
 *
 * 5. THE DELETE-ONLY-WHEN-EMPTY AFFORDANCE SURVIVES AS A PROJECTION, AND THE CONFLICT
 *    IS STILL HANDLED. See {@link RoleStore.canDeleteSelectedGroup}.
 *
 * 6. THE PAGING SHAPES DIFFER PER LISTING, AS SET OUT ABOVE.
 *
 * 7. ENDING A PAID ASSIGNMENT EXPIRES THE ROW RATHER THAN DELETING IT, SO A SUCCESSFUL
 *    REMOVAL DOES NOT MEAN THE ROW IS GONE. See {@link RoleStore.removeAssignment}.
 *    This is the single most consequential behaviour in this file.
 *
 * 8. THE ASSIGNMENT WRITE IS AN UPDATE-OR-ADD WHOSE RESPONSE DOES NOT SAY WHICH
 *    HAPPENED. See {@link RoleStore.assignUser}.
 *
 * 9. THE SIX FREQUENCY CODES ARE PERSISTED CHARACTERS, PRESERVED VERBATIM. See
 *    {@link billingTermsBound}.
 *
 * 10. THE LEGACY FREQUENCY BRANCH HAD NO FALLBACK ARM; THIS ONE DOES. See
 *     {@link billingTermsBound}.
 *
 * 11. FOUR SEPARATE VOCABULARIES SPELL THEMSELVES WITH THE SAME NEGATIVE NUMBERS, AND
 *     THEY ARE KEPT APART. Nothing in this file normalises a negative identifier,
 *     because each negative means something different depending on which vocabulary it
 *     belongs to:
 *     (a) The legacy group NARROWING, where -2 meant "every role" and -1 meant "only
 *         the ungrouped roles" (`Roles.ascx.vb:L112` and `:L114`). Modelled as
 *         {@link RoleGroupFilter}; neither number appears as a value in this file.
 *     (b) The group FIELD on a role row, where -1 meant "belongs to no group". The API
 *         publishes `null` for it, which is that column's honest form: the group table
 *         is seeded from zero with a foreign key pointing at it, so -1 was never a
 *         stored value — the legacy reader manufactured it outbound and undid it
 *         inbound. Read it with `=== null`.
 *     (c) The PSEUDO-ROLE constants at `Library/Components/Shared/Globals.vb:L95-L98`,
 *         which are `String` constants — "-1" all users, "-2" superuser, "-3"
 *         unauthenticated, "-4" nothing — compared as strings, never parsed to numbers.
 *         None is a row in the role table and none may reach the narrowing above.
 *     (d) The ABSENT-INTEGER marker at `Library/Components/Shared/Null.vb:L41-L45`,
 *         whose body literally returns -1, used as "no existing assignment row" at
 *         `RoleController.vb:L503` and as "no expiry at all" at `:L537`.
 *     A fifth reading exists and is explicitly NOT this file's: in the module and page
 *     permission contracts a role identifier of -1, -2 or -3 names a REAL principal and
 *     must never become null. That rule governs permission-bearing contracts; it does
 *     not govern the group column, and collapsing the two is a live defect.
 *
 * 12. A FEE OF ZERO MEANS FREE. See {@link RoleStore.selectedRoleIsPaid}.
 *
 * 13. THE THREE LEGACY DISPLAY HELPERS ARE PRESENTATION AND ARE NOT IMPLEMENTED HERE,
 *     as set out above.
 *
 * 14. THE LEGACY CACHING LAYER IS NOT REPRODUCED. The legacy code reached a static
 *     cache helper from well over a hundred call sites across the migrated domains,
 *     with expiry computed as a per-entity timeout times a global multiplier and
 *     invalidation performed portal-wide or host-wide;
 *     `Library/Components/Providers/Caching/DataCache.vb` is 317 lines of it. This
 *     module holds no cache, no expiry stamp, no staleness flag and no invalidation.
 *     Caching is a server concern now, behind the API's own abstraction, which is what
 *     lets the code performing a write invalidate it; a second, unsynchronised cache
 *     here would answer from stale state after another administrator's change. The plan
 *     cites this file under the shared component directory, which does not exist — the
 *     real path is under the caching provider directory.
 *
 * 15. AUTHORISATION IS DECIDED SERVER-SIDE, as set out above.
 *
 * 16. A FORBIDDEN RESPONSE IS A WARNING, NOT AN ERROR. See {@link RoleStoreFailure}.
 *
 * 17. LOCALISATION IS NOT PORTED. The legacy narrowing branches keyed off LOCALISED
 *     strings, looking up the two pseudo-entries by resource key
 *     (`Roles.ascx.vb:L112` and `:L114`), which made the control flow depend on the
 *     active language. The target keys off {@link RoleGroupFilter} instead, so the
 *     branch is language-independent. English wording is authored in the feature
 *     templates with the legacy resource files as the reference; no translation runtime
 *     is added and no message text originates in this file.
 *
 * 18. EVERY IMPLICIT COERCION IS MADE EXPLICIT, AND ONE OF THEM IS REMOVED OUTRIGHT.
 *     The legacy admin screens compiled with strictness disabled
 *     (`Website/release.config:L125`), so they could legally narrow implicitly and bind
 *     late. The narrowing selector is the concrete instance: `Roles.ascx.vb:L275` and
 *     `:L292` read the dropdown's selected value — a string — and hand it straight to
 *     an unguarded integer parse, which throws on any non-numeric value rather than
 *     reporting one. {@link RoleGroupFilter} removes the parse entirely: a narrowing is
 *     constructed as a typed value, so there is no string to convert and no
 *     unrepresentable input to guard against. A discrepancy worth recording, since the
 *     plan describes this as an implicit narrowing: it is an EXPLICIT parse, and the
 *     defect is that it is unguarded rather than that it is implicit.
 * ---------------------------------------------------------------------------
 */

import { Injectable, computed, inject, signal } from '@angular/core';
import { finalize, switchMap, tap } from 'rxjs';

import { emptyPagedResult, toPagedResult, DEFAULT_PAGE_SIZE } from '../models/paged-result.model';
import { isProblemDetails } from '../models/problem-details.model';
import { RoleService } from '../services/role.service';
import { failureCode, isConflictCode, summarizeProblem } from '../utils/form-errors.util';

import type { ApiMeta, PagedResult, SortDirection } from '../models/paged-result.model';
import type { ProblemDetails } from '../models/problem-details.model';
import type {
  BillingFrequency,
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  UpdateRoleGroupRequest,
  UpdateRoleRequest,
  UserRole,
} from '../models/role.model';
import type { ConflictCode, ProblemSummary } from '../utils/form-errors.util';

// ---------------------------------------------------------------------------
// THE GROUP NARROWING
// ---------------------------------------------------------------------------

/**
 * Which roles the listing should consider, as three named intents.
 *
 * MIGRATION: this replaces a single signed integer that carried three meanings at once,
 * and separating them is the whole point. `Website/admin/Security/Roles.ascx.vb`
 * declared the field at `:L48`, gave its "all roles" entry the value -2 at `:L112` and
 * its "global roles" entry the value -1 at `:L114`, then read the value back with TWO
 * DIFFERENT comparisons that do not agree with each other:
 *
 * - `:L72` branches on the value being STRICTLY BELOW -1 to choose the whole-portal
 *   query over the by-group query. So -2 selects every role, while -1 falls through to
 *   the by-group query and selects the roles belonging to no group.
 * - `:L79` branches on the value merely being NEGATIVE to hide the edit and delete
 *   controls, so it treats -1 and -2 alike where the first comparison does not.
 *
 * Rewriting either comparison as the other changes behaviour, which is precisely why
 * neither is reproduced. The three intents are named instead, and the numbers -2 and -1
 * appear nowhere in this file as values — only in this commentary, as provenance.
 *
 * Note carefully that `-1` was doubly loaded in the legacy design: as a NARROWING it
 * meant "show me the ungrouped roles", and as a FIELD on a role row it meant "this role
 * belongs to no group". Those are two different facts about two different things. This
 * type is the narrowing only. The field is `Role.roleGroupId`, which the API publishes
 * as `null` rather than -1, and the two must never stand in for one another.
 *
 * How each intent reaches the wire is deliberately NOT a magic number either. The
 * endpoint takes a group key and a scope as two separate arguments, so an intent no key
 * can express is named rather than encoded — see {@link RoleStore.loadRoles}.
 */
export type RoleGroupFilter =
  /** Every role in the portal, whatever its grouping. Legacy narrowing value -2. */
  | { readonly kind: 'AllRoles' }
  /** Only the roles belonging to no group at all. Legacy narrowing value -1. */
  | { readonly kind: 'GlobalRoles' }
  /**
   * Only the roles in one named group.
   *
   * `roleGroupId` is a real key and may legitimately be `0`: the group table is seeded
   * `IDENTITY(0, 1)`, so the first group any portal creates has key zero. Never test it
   * for truthiness and never test its sign.
   */
  | { readonly kind: 'Group'; readonly roleGroupId: number };

/**
 * The narrowing a freshly constructed store starts with.
 *
 * MIGRATION: the measured legacy default is GLOBAL ROLES, not all roles, and this
 * corrects the migration plan, which states the opposite.
 * `Website/admin/Security/Roles.ascx.vb:L48` initialises the field to -1, and -1 is the
 * ungrouped narrowing per `:L114`. The value -2 arises from exactly two places, neither
 * of which is initialisation: an explicit choice in the dropdown, and the no-groups
 * fallback at `:L129` that {@link RoleStore.loadRoleAdministration} reproduces. So the
 * legacy screen's first paint listed the ungrouped roles, and so does this one.
 */
export const DEFAULT_ROLE_GROUP_FILTER: RoleGroupFilter = Object.freeze({
  kind: 'GlobalRoles',
});

/**
 * Reports that a discriminated value fell outside its own declared union.
 *
 * Reachable only if a union above gains a member without its branch gaining an arm, so
 * this is the compiler's message made to survive into runtime rather than a condition
 * any input can produce. Declaring the parameter `never` is what makes the omission a
 * compile error at the call site; throwing is what stops a silent wrong answer if one
 * ever slips past, which is exactly the failure the legacy branch at
 * `RoleController.vb:L540-L547` had — see {@link billingTermsBound}.
 *
 * @param value The value no branch matched.
 * @returns Never returns; always throws.
 */
function assertUnreachable(value: never): never {
  throw new Error(`Unhandled variant in role state: ${JSON.stringify(value)}`);
}

// ---------------------------------------------------------------------------
// PAID-MEMBERSHIP TERMS
// ---------------------------------------------------------------------------

/**
 * The absent-integer marker the legacy period columns used.
 *
 * `Library/Components/Shared/Null.vb:L41-L45` is a property whose body returns -1, and
 * `RoleController.vb:L537` tests a period against it to mean NO EXPIRY AT ALL rather
 * than "unset, apply a default". Named here so the comparison reads as the domain test
 * it is, and so no bare negative literal appears in the logic.
 */
const LEGACY_ABSENT_PERIOD = -1;

/**
 * Whether a role's paid-membership terms bound the membership in time, and how.
 *
 * A classification of persisted terms, not a date and not display text — see
 * {@link billingTermsBound} for why the distinction matters.
 */
export type BillingTermsBound =
  /** The terms set no expiry, so the membership does not lapse. */
  | 'Unbounded'
  /** The terms set a perpetual far-future expiry rather than none. */
  | 'Perpetual'
  /** The terms advance the expiry by a period, so the membership lapses. */
  | 'Bounded';

/**
 * Classifies a role's frequency-and-period pair by whether it bounds the membership.
 *
 * Legacy: `Library/Components/Security/Roles/RoleController.vb:L537-L547`.
 *
 * MIGRATION: the six codes are PERSISTED CHARACTERS and are preserved verbatim. They
 * are the literal bytes in the `BillingFrequency` and `TrialFrequency` columns of the
 * role table, both single-character, and one vocabulary serves both columns. Renaming
 * one, folding its case, spelling it as a word or turning it into a number would not
 * fail a compilation — it would silently mis-read live rows. The legacy meanings, which
 * this function classifies without reproducing:
 *
 * - `N` sets no expiry.
 * - `O` sets the far-future perpetual date 9999-12-31. That is a real stored value that
 *   merely looks like a sentinel; it is neither absence nor an error.
 * - `D` advances by the period in days, `W` by the period times seven days — a scaled
 *   day count, not a distinct unit — `M` by the period in months, `Y` in years.
 *
 * MIGRATION: A PERIOD OF {@link LEGACY_ABSENT_PERIOD} MEANS NO EXPIRY, and it is tested
 * first because the legacy tested it first: `:L537` short-circuits the whole frequency
 * branch when the period is the absent-integer marker. It does not mean "unset, apply a
 * default", so it is never coalesced away and never made positive.
 *
 * MIGRATION: THE LEGACY BRANCH HAD NO FALLBACK ARM. `:L540-L547` is a six-arm branch
 * with no final catch-all, so an unrecognised code left the expiry at whatever value it
 * already held and reported nothing. This function is exhaustive over the union and
 * ends in {@link assertUnreachable}, so an unrecognised code becomes a loud failure
 * instead of a silent wrong answer. That is a deliberate improvement, not a
 * transliteration.
 *
 * WHAT THIS FUNCTION DOES NOT DO, and why: it performs NO date arithmetic and returns
 * NO date. The expiry bound is derived server-side from an injected clock, and the
 * bounds on the wire are absolute instants; a second derivation here would disagree
 * with the server across a clock skew and neither answer would be reproducible. It also
 * returns no display text — rendering a period and a frequency as a phrase was the
 * legacy screen's display helper, which is a component or pipe concern.
 *
 * @param frequency The persisted frequency code, or `null` when the role records none.
 * @param period How many units one cycle spans, or `null` when the role records none.
 * @returns How the terms bound the membership in time.
 */
export function billingTermsBound(
  frequency: BillingFrequency | null,
  period: number | null,
): BillingTermsBound {
  // The absent-period short circuit, ahead of the frequency, exactly as `:L537` orders it.
  if (period === LEGACY_ABSENT_PERIOD) {
    return 'Unbounded';
  }

  // No recorded frequency is a different fact from the code `N`, and both leave the
  // membership unbounded. Tested with an explicit absence check, never truthiness.
  if (frequency === null) {
    return 'Unbounded';
  }

  switch (frequency) {
    case 'N':
      return 'Unbounded';
    case 'O':
      return 'Perpetual';
    case 'D':
    case 'W':
    case 'M':
    case 'Y':
      return 'Bounded';
    default:
      return assertUnreachable(frequency);
  }
}

// ---------------------------------------------------------------------------
// FAILURE STATE
// ---------------------------------------------------------------------------

/**
 * Which command failed, so a screen can attribute a failure without guessing.
 *
 * Named after the command methods on {@link RoleStore} rather than after HTTP verbs,
 * because a screen reacts to "the removal failed", not to "a DELETE failed".
 */
export type RoleStoreOperation =
  | 'loadRoles'
  | 'loadRoleGroups'
  | 'loadRole'
  | 'createRole'
  | 'updateRole'
  | 'deleteRole'
  | 'loadAssignments'
  | 'assignUser'
  | 'removeAssignment'
  | 'createRoleGroup'
  | 'updateRoleGroup'
  | 'deleteRoleGroup';

/**
 * One failure, held structurally.
 *
 * MIGRATION: A FORBIDDEN RESPONSE IS A WARNING, NOT AN ERROR, and the severity is not
 * decided here. `severity` comes from {@link ProblemSummary}, whose owning module maps a
 * forbidden response — along with an unauthenticated, a not-found and a rate-limited one
 * — to warning severity and everything else to error. That reproduces the security
 * tree's own precedent: `Website/admin/Security/AccessDenied.ascx.vb` performs no
 * permission check at all and renders BOTH of its branches, at `:L43` and `:L45`, as a
 * yellow warning rather than a red error.
 *
 * MIGRATION: THE STRUCTURED DOCUMENT IS HELD; NO MESSAGE IS COMPOSED HERE. Wording,
 * severity mapping and the stripping of the legacy leading break tag — which the legacy
 * code emitted in both spellings — all belong to `core/utils/form-errors.util.ts`, and
 * this module calls into it rather than restating any of it. Nothing here is marked as
 * trusted markup, and no value from this slice may ever be bound as raw HTML: the
 * legacy resource files carry live HTML in dozens of values, including script elements,
 * and the legacy precedent for displaying an untrusted message was to encode it first.
 *
 * `supportReference` on the summary is deliberately retained. It resolves from the
 * problem document's correlation identifier, falling back to its trace identifier, which
 * is the value the outgoing correlation header round-trips into the response body. It is
 * the operator's only join key between a browser report and a server log, so it is
 * carried rather than dropped.
 */
export interface RoleStoreFailure {
  /** Which command failed. */
  readonly operation: RoleStoreOperation;

  /**
   * Severity, wording and per-field messages, produced by the module that owns them.
   *
   * Its `severity` is what a banner should honour; its `supportReference` is the
   * correlation value to show an operator.
   */
  readonly summary: ProblemSummary;

  /**
   * The server's RFC 7807 document verbatim, or `null` when the failure carried none.
   *
   * Only ever a genuine document. When a transport failure arrives with no problem body,
   * this stays `null` and the summary is derived from the transport status alone, so a
   * consumer can always distinguish "the server explained itself" from "it did not".
   *
   * Its per-field dictionary is an index signature, so it is read with bracket access —
   * `problem.errors?.['RoleName']` — and never with dotted access, which the workspace's
   * compiler settings refuse by design.
   */
  readonly problem: ProblemDetails | null;

  /**
   * The server's conflict code verbatim, or `null` when the failure was not a
   * recognised conflict.
   *
   * MIGRATION: the codes are taken from the shared catalogue rather than spelled here,
   * and this corrects the folder requirements, which assert the identifiers
   * `DuplicateRole`, `DuplicateRoleGroup` and `RoleRemoveError`. The implemented
   * catalogue in `core/utils/form-errors.util.ts` uses dotted lower-case identifiers
   * instead — a duplicate role name, a duplicate role-group name, and a protected
   * assignment whose message is the legacy "You Can Not Remove The Portal Administrator
   * Or The Registered Users Role". Because the codes are consumed as
   * {@link ConflictCode} through that module's own recogniser, this file spells none of
   * them and cannot drift from the catalogue.
   */
  readonly conflict: ConflictCode | null;
}

// ---------------------------------------------------------------------------
// PAGE COORDINATES
// ---------------------------------------------------------------------------

/**
 * The page coordinate, ordering and free-text filter for one PAGED listing.
 *
 * Applies to the role listing and the assignment listing. It deliberately does NOT apply
 * to the role-group listing, which is unpaged and for which this module holds no
 * coordinate of any kind.
 *
 * The index is ZERO-BASED, which is the base the API both accepts and reports, so an
 * index sent may be compared with an index read back without arithmetic. The legacy
 * screens that did page converted a one-based control index by subtracting one; nothing
 * here needs that conversion because nothing here is one-based.
 *
 * Every member is nullable so that "no opinion" is expressible and distinct from a
 * value. An omitted size lets the server apply its own default rather than a literal
 * chosen here, and an omitted ordering lets it apply its own.
 */
export interface RolePageCoordinate {
  /** The page to return, counted from zero. */
  readonly pageIndex: number;

  /** How many records the page should hold, or `null` to accept the server's default. */
  readonly pageSize: number | null;

  /** The member to order by, or `null` to accept the server's default ordering. */
  readonly sortBy: string | null;

  /** The direction to order in, or `null` to accept the server's default. */
  readonly sortDir: SortDirection | null;

  /** The free-text filter, or `null` for none. */
  readonly query: string | null;
}

/**
 * The coordinate a freshly constructed store starts each paged listing at.
 *
 * The first page, at the shared default size, unordered and unfiltered.
 */
const INITIAL_PAGE_COORDINATE: RolePageCoordinate = Object.freeze({
  pageIndex: 0,
  pageSize: DEFAULT_PAGE_SIZE,
  sortBy: null,
  sortDir: null,
  query: null,
});

/**
 * Recovers the server's RFC 7807 document from whatever the HTTP layer threw.
 *
 * The error interceptor re-throws the original transport error rather than replacing it,
 * so what arrives here is that error with the problem document, when there is one, in its
 * BODY. This reads it structurally: the transport error type belongs to the HTTP module,
 * which this file must not import, and it does not need to — the shape is inspected here
 * and the payload is validated by the recogniser the contract module owns.
 *
 * THE DOCUMENT IS ONLY EVER READ OUT OF THE BODY, AND THAT ORDERING IS LOAD-BEARING. The
 * recogniser is deliberately permissive: it accepts any object carrying even one valid
 * standard member, and `status` is one of them. A transport error carries a numeric status
 * of its own, so testing the thrown value itself BEFORE its body would accept the envelope
 * as though it were the document — silently discarding the real one nested inside it,
 * along with the trace identifier and the failure code it carries. This mirrors the
 * precedence the error interceptor already established for the same reason.
 *
 * A status of zero is resolved first and yields no document at all. It means the response
 * never arrived — the network was unavailable, or the request was blocked or aborted — and
 * the framework puts a progress event in the body slot for that condition. A progress
 * event carries a string `type` member, which the permissive recogniser also accepts, so
 * reading the body in that case would manufacture a document that says nothing.
 *
 * A string body is parsed defensively rather than trusted, because a failure produced by
 * the reverse proxy rather than by the API — a gateway timeout answering with an HTML page
 * — arrives as text. A parse failure yields `null` rather than propagating: replacing the
 * caller's failure with a syntax error would report the wrong problem entirely.
 *
 * @param error Whatever the observable's failure path delivered.
 * @returns The document when the failure carried a genuine one, otherwise `null`.
 */
function readProblem(error: unknown): ProblemDetails | null {
  if (typeof error !== 'object' || error === null || !('error' in error)) {
    return null;
  }

  // A response that never arrived carries no document, whatever sits in the body slot.
  if (readStatus(error) === 0) {
    return null;
  }

  const body: unknown = error.error;

  if (isProblemDetails(body)) {
    return body;
  }

  if (typeof body !== 'string' || body.trim().length === 0) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(body);

    return isProblemDetails(parsed) ? parsed : null;
  } catch {
    // Not JSON at all — an HTML error page, most likely. Deliberately swallowed and only
    // here: the status-derived wording describes such a page better than any part of it
    // would, and the failure itself is still recorded by the caller.
    return null;
  }
}

/**
 * Recovers the transport status when the failure carried no problem document.
 *
 * Needed so that severity still resolves for a failure the server did not explain — a
 * forbidden response with an empty body is still a warning rather than an error. Read
 * structurally, for the same reason as {@link readProblem}.
 *
 * @param error Whatever the observable's failure path delivered.
 * @returns The status when one is present and numeric, otherwise `null`.
 */
function readStatus(error: unknown): number | null {
  if (typeof error === 'object' && error !== null && 'status' in error) {
    const status: unknown = error.status;

    if (typeof status === 'number') {
      return status;
    }
  }

  return null;
}

// ---------------------------------------------------------------------------
// THE STORE
// ---------------------------------------------------------------------------

/**
 * Holds and coordinates role-administration state.
 *
 * Registered at the root so one instance serves the whole application; no component
 * declares a provider for it, and no component may write to it. Every slice is exposed
 * read-only, and the only way to change anything is to call a command method, each of
 * which is the single place its own invariants live.
 *
 * Guards under `app/core/guards` import this store; this store imports no guard, no
 * interceptor and no component. The dependency direction is one-way by design.
 */
@Injectable({ providedIn: 'root' })
export class RoleStore {
  private readonly roleService = inject(RoleService);

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private without exception
  // -------------------------------------------------------------------------

  /** The current page of the role listing. Paged; see the file header. */
  private readonly _roles = signal<PagedResult<RoleListItem>>(emptyPagedResult<RoleListItem>());

  /**
   * Every role group in the portal.
   *
   * UNPAGED, and there is deliberately no coordinate, size or total beside it. The
   * endpoint answers a plain array and the legacy screen bound one at `:L108`.
   */
  private readonly _roleGroups = signal<readonly RoleGroup[]>([]);

  /** The narrowing applied to the role listing. */
  private readonly _groupFilter = signal<RoleGroupFilter>(DEFAULT_ROLE_GROUP_FILTER);

  /**
   * The role under edit, or `null` when none is selected.
   *
   * Absence is `null` and nothing else. It is never `0`, never `-1` and never `-2`: the
   * role table is seeded `IDENTITY(0, 1)` so a role key of zero is the portal's FIRST
   * role, and encoding absence as any of those numbers would silently lose a real row.
   */
  private readonly _selectedRole = signal<Role | null>(null);

  /** The current page of the assignment listing. Paged; see the file header. */
  private readonly _assignments = signal<PagedResult<UserRole>>(emptyPagedResult<UserRole>());

  /**
   * The role whose assignments {@link RoleStore.assignments} currently holds, or `null`.
   *
   * Absence is `null`, for the reason given on {@link RoleStore._selectedRole}.
   */
  private readonly _assignmentsRoleId = signal<number | null>(null);

  /** The coordinate the role listing was last read at. */
  private readonly _rolesPage = signal<RolePageCoordinate>(INITIAL_PAGE_COORDINATE);

  /** The coordinate the assignment listing was last read at. */
  private readonly _assignmentsPage = signal<RolePageCoordinate>(INITIAL_PAGE_COORDINATE);

  private readonly _rolesLoading = signal<boolean>(false);
  private readonly _roleGroupsLoading = signal<boolean>(false);
  private readonly _selectedRoleLoading = signal<boolean>(false);
  private readonly _assignmentsLoading = signal<boolean>(false);
  private readonly _saving = signal<boolean>(false);

  /** The last failure, or `null` when the last command succeeded. */
  private readonly _failure = signal<RoleStoreFailure | null>(null);

  // -------------------------------------------------------------------------
  // READ-ONLY PROJECTIONS
  // -------------------------------------------------------------------------

  /** The current page of roles, with the total across every page on its metadata. */
  readonly roles = this._roles.asReadonly();

  /** Every role group in the portal, unpaged. */
  readonly roleGroups = this._roleGroups.asReadonly();

  /** The narrowing currently applied to the role listing. */
  readonly groupFilter = this._groupFilter.asReadonly();

  /** The role under edit, or `null`. */
  readonly selectedRole = this._selectedRole.asReadonly();

  /** The current page of assignments for {@link RoleStore.assignmentsRoleId}. */
  readonly assignments = this._assignments.asReadonly();

  /** The role the held assignments belong to, or `null`. */
  readonly assignmentsRoleId = this._assignmentsRoleId.asReadonly();

  /** The coordinate the role listing was last read at. */
  readonly rolesPage = this._rolesPage.asReadonly();

  /** The coordinate the assignment listing was last read at. */
  readonly assignmentsPage = this._assignmentsPage.asReadonly();

  readonly rolesLoading = this._rolesLoading.asReadonly();
  readonly roleGroupsLoading = this._roleGroupsLoading.asReadonly();
  readonly selectedRoleLoading = this._selectedRoleLoading.asReadonly();
  readonly assignmentsLoading = this._assignmentsLoading.asReadonly();

  /** Whether a write is in flight. */
  readonly saving = this._saving.asReadonly();

  /** The last failure, or `null`. */
  readonly failure = this._failure.asReadonly();

  // -------------------------------------------------------------------------
  // DERIVED PROJECTIONS
  // -------------------------------------------------------------------------

  /** The roles on the current page. */
  readonly roleItems = computed<readonly RoleListItem[]>(() => this._roles().items);

  /** The role listing's paging metadata, including the total across every page. */
  readonly rolesMeta = computed<ApiMeta>(() => this._roles().meta);

  /** The assignments on the current page. */
  readonly assignmentItems = computed<readonly UserRole[]>(() => this._assignments().items);

  /** The assignment listing's paging metadata. */
  readonly assignmentsMeta = computed<ApiMeta>(() => this._assignments().meta);

  /** Whether any read or write is in flight. */
  readonly busy = computed<boolean>(
    () =>
      this._rolesLoading() ||
      this._roleGroupsLoading() ||
      this._selectedRoleLoading() ||
      this._assignmentsLoading() ||
      this._saving(),
  );

  /**
   * Whether the portal declares any role group at all.
   *
   * Legacy: `Roles.ascx.vb:L110` branches on the group count, showing the narrowing row
   * at `:L128` when there is at least one and hiding it at `:L131` when there is none.
   * This projection is the DATA behind that decision; whether to render the row is the
   * feature component's call, because a visibility flag is presentation and this module
   * produces none.
   */
  readonly hasRoleGroups = computed<boolean>(() => this._roleGroups().length > 0);

  /**
   * The group the narrowing names, or `null` when it names none.
   *
   * `null` for both pseudo-intents, because neither is a group. Note that a group key of
   * `0` is a REAL group, so a consumer distinguishes "no group named" from "group zero"
   * by testing `=== null` and never by truthiness.
   */
  readonly selectedRoleGroupId = computed<number | null>(() => {
    const filter = this._groupFilter();

    // The one place in this file where the narrowing's own variants are compared. These
    // are the domain's named intents, NOT sentinel identifiers, so this is not the
    // truthiness-on-an-identifier pattern that is forbidden elsewhere.
    switch (filter.kind) {
      case 'AllRoles':
      case 'GlobalRoles':
        return null;
      case 'Group':
        return filter.roleGroupId;
      default:
        return assertUnreachable(filter);
    }
  });

  /**
   * The group the narrowing names, resolved against the loaded groups, or `null`.
   *
   * `null` when the narrowing names no group, and also when it names one that the loaded
   * listing does not contain — which is a real possibility while a read is in flight, or
   * after another administrator removed the group.
   */
  readonly selectedRoleGroup = computed<RoleGroup | null>(() => {
    const roleGroupId = this.selectedRoleGroupId();

    if (roleGroupId === null) {
      return null;
    }

    return this._roleGroups().find((group) => group.roleGroupId === roleGroupId) ?? null;
  });

  /**
   * Whether a screen should OFFER to delete the group the narrowing names.
   *
   * Legacy: `Website/admin/Security/Roles.ascx.vb:L79-L85`, which is two guards, not one:
   *
   * - `:L79-L81` hides both the edit-group and the delete control whenever the narrowing
   *   is negative, because neither pseudo-intent is a group you can act on. Here that
   *   falls out of {@link RoleStore.selectedRoleGroupId} being `null`.
   * - `:L85` sets the delete control's visibility from the currently listed roles being
   *   empty. A discrepancy worth recording: the folder requirements cite `:L84` for this,
   *   but `:L84` assigns the edit-group link's navigation address — the delete guard is
   *   `:L85`.
   *
   * MIGRATION: THIS IS AN AFFORDANCE, NOT A DECISION. It exists so a screen can disable a
   * control, and it is never consulted to decide whether to send the request. The listing
   * it reads may be a stale page, and it counts only the roles on the CURRENT page rather
   * than every role in the group, so it can say yes when the server will say no. The
   * server is the authority and answers with a conflict;
   * {@link RoleStore.deleteRoleGroup} handles that conflict unconditionally and never
   * suppresses the call on the strength of this projection.
   */
  readonly canDeleteSelectedGroup = computed<boolean>(() => {
    if (this.selectedRoleGroupId() === null) {
      return false;
    }

    return this._roles().items.length === 0;
  });

  /**
   * How the selected role's billing terms bound the membership, or `null` if no role is
   * selected.
   *
   * Classification only; see {@link billingTermsBound} for what it deliberately does not
   * compute.
   */
  readonly selectedRoleBillingTerms = computed<BillingTermsBound | null>(() => {
    const role = this._selectedRole();

    if (role === null) {
      return null;
    }

    return billingTermsBound(role.billingFrequency, role.billingPeriod);
  });

  /**
   * How the selected role's trial terms bound the trial, or `null` if no role is
   * selected.
   *
   * MIGRATION: WHICH SET OF TERMS ACTUALLY GOVERNS AN ASSIGNMENT IS DECIDED SERVER-SIDE,
   * and cannot be decided here. `RoleController.vb:L521` chose the trial terms over the
   * billing terms when the trial had NOT already been used and the trial frequency was
   * not the no-expiry code. The first half of that test reads a flag on the ASSIGNMENT
   * row, not on the role, and the API publishes it on neither the role contract nor the
   * membership contract — so this module cannot see it and does not pretend to. Both term
   * sets are therefore classified and exposed separately, and the choice between them
   * stays where the data to make it lives.
   *
   * A related correction to the folder requirements, which list a trial-used flag among
   * the role's own paid-membership members: the role contract carries fourteen members
   * and that flag is not among them. Its six paid-membership members are the two
   * frequencies, the two periods, the service fee and the trial fee.
   */
  readonly selectedRoleTrialTerms = computed<BillingTermsBound | null>(() => {
    const role = this._selectedRole();

    if (role === null) {
      return null;
    }

    return billingTermsBound(role.trialFrequency, role.trialPeriod);
  });

  /**
   * Whether the selected role charges a fee, or `null` if no role is selected.
   *
   * MIGRATION: A FEE OF ZERO MEANS FREE, AND IS REAL DATA. `RoleController.vb:L494`
   * discriminates paid from free with a strictly-greater-than-zero test on the fee, so
   * zero falls on the free side deliberately. The fee is a single-precision column whose
   * legacy absent-marker was the type's minimum value — not zero and not minus one — so
   * zero is never absence. This projection therefore never coalesces the fee to zero and
   * never treats zero as missing: a `null` fee means the role records none, which is also
   * not paid, and the two are distinguished before the comparison.
   */
  readonly selectedRoleIsPaid = computed<boolean | null>(() => {
    const role = this._selectedRole();

    if (role === null) {
      return null;
    }

    const fee = role.serviceFee;

    if (fee === null) {
      return false;
    }

    return fee > 0;
  });

  // -------------------------------------------------------------------------
  // INTERNAL
  // -------------------------------------------------------------------------

  /**
   * Translates a narrowing intent into the two arguments the endpoint takes.
   *
   * MIGRATION: THE ENDPOINT SEPARATES THE CONCERNS THE LEGACY INTEGER CONFLATED. It takes
   * a group key and a scope as two independent arguments, so the key stays a plain key
   * with no magic values and the intent no key can express is named instead. The two
   * pseudo-intents therefore travel as a scope name — every role, or only the ungrouped
   * ones — and a real group travels as a key.
   *
   * THE ONE HARD CONSTRAINT: naming the ungrouped scope TOGETHER WITH a group key is a
   * contradiction the API refuses with a bad request rather than preferring one of the
   * two. Each arm below returns exactly one of the pair, which makes that contradiction
   * unconstructible rather than merely avoided.
   *
   * No type is imported for the returned shape and none is declared, deliberately. The
   * shape's own declaration lives in the query-building module, which this file must not
   * depend on, so the literals are returned as constants and the compiler proves them
   * assignable at the call site. Naming a local copy of that shape would be a second
   * declaration free to drift from the first.
   *
   * @param filter The narrowing intent to translate.
   * @returns The group key, or the scope name — never both.
   */
  private narrowingFor(filter: RoleGroupFilter) {
    switch (filter.kind) {
      case 'AllRoles':
        return { scope: 'All' } as const;
      case 'GlobalRoles':
        return { scope: 'Ungrouped' } as const;
      case 'Group':
        return { roleGroupId: filter.roleGroupId } as const;
      default:
        return assertUnreachable(filter);
    }
  }

  /**
   * Reproduces the legacy fall-back to listing every role when the portal declares no
   * groups.
   *
   * Legacy: `Website/admin/Security/Roles.ascx.vb:L129` assigns the narrowing the
   * every-role value in the else arm of the group-count test, unconditionally — so it
   * overrides a previously chosen group as well as the default. Reproduced exactly.
   *
   * @param groups The groups just read from the server.
   */
  private applyNoGroupsFallback(groups: readonly RoleGroup[]): void {
    if (groups.length > 0) {
      return;
    }

    if (this._groupFilter().kind === 'AllRoles') {
      return;
    }

    this._groupFilter.set({ kind: 'AllRoles' });
  }

  /**
   * Narrows a role to the eleven members the listing publishes.
   *
   * Used to reconcile a listing row against a detail response without inventing values.
   * The projection is explicit rather than a spread because the listing contract omits
   * the group key, the invitation code and the icon BY DESIGN — reading any of them off a
   * listing row yields nothing at runtime — so copying them in would contradict the
   * contract even though the compiler would allow it.
   *
   * @param role The detail payload the server returned.
   * @returns The same values, in the listing's shape.
   */
  private projectListItem(role: Role): RoleListItem {
    return {
      roleId: role.roleId,
      roleName: role.roleName,
      description: role.description,
      serviceFee: role.serviceFee,
      billingPeriod: role.billingPeriod,
      billingFrequency: role.billingFrequency,
      trialFee: role.trialFee,
      trialPeriod: role.trialPeriod,
      trialFrequency: role.trialFrequency,
      isPublic: role.isPublic,
      autoAssignment: role.autoAssignment,
    };
  }

  /**
   * Records a failure structurally, deciding nothing about how it should read.
   *
   * The severity, the wording and the per-field breakdown all come from the module that
   * owns them; the conflict code comes from that module's own recogniser, so no code
   * string is spelled here. When the failure carried no problem document, the transport
   * status alone is passed through so that severity still resolves — a forbidden response
   * with an empty body is still a warning — while `problem` stays `null` so a consumer can
   * tell that the server did not explain itself.
   *
   * @param operation The command that failed.
   * @param error Whatever the observable's failure path delivered.
   */
  private recordFailure(operation: RoleStoreOperation, error: unknown): void {
    const problem: ProblemDetails | null = readProblem(error);
    const status: number | null = readStatus(error);
    const described: ProblemDetails | null =
      problem ?? (status === null ? null : { status });
    const code: string | null = failureCode(problem);

    this._failure.set({
      operation,
      summary: summarizeProblem(described),
      problem,
      conflict: isConflictCode(code) ? code : null,
    });
  }

  /** Discards the held failure, so a fresh command starts from a clean slate. */
  private clearFailure(): void {
    if (this._failure() !== null) {
      this._failure.set(null);
    }
  }

  /**
   * Whether a failure is a recognised conflict, without recording it.
   *
   * Needed so a caller can decide to refresh BEFORE recording, which is what keeps the
   * refresh from erasing the failure that prompted it — see
   * {@link RoleStore.deleteRoleGroup}.
   *
   * @param error Whatever the observable's failure path delivered.
   * @returns `true` when the server reported a code the shared catalogue recognises.
   */
  private isConflictFailure(error: unknown): boolean {
    return isConflictCode(failureCode(readProblem(error)));
  }

  // -------------------------------------------------------------------------
  // READ COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Reads the groups, then reads the roles for whatever narrowing survives.
   *
   * This is the sequence the listing screen opens with, and the order is load-bearing
   * rather than incidental: the groups decide whether the narrowing row can be offered at
   * all, and — per {@link RoleStore.applyNoGroupsFallback} — an empty group listing
   * overrides the narrowing before the roles are read. Reading the two concurrently would
   * race that override and could read the roles under a narrowing the portal cannot
   * support.
   *
   * Legacy: `Roles.ascx.vb` bound the groups and the roles on every postback, one after
   * the other. MIGRATION: the re-read on every interaction is what signals eliminate —
   * state is held and updated immutably from here on, and a further read happens only
   * where the server may have diverged from what is held.
   *
   * Composed as one chain so a single failure surfaces once, attributed to whichever half
   * failed, rather than as two independent failures a screen would have to reconcile.
   */
  loadRoleAdministration(): void {
    this._roleGroupsLoading.set(true);
    this._rolesLoading.set(true);
    this.clearFailure();

    this.roleService
      .listRoleGroups()
      .pipe(
        tap((response) => {
          this._roleGroups.set(response.data);
          this._roleGroupsLoading.set(false);
          this.applyNoGroupsFallback(response.data);
        }),
        switchMap(() => this.roleService.listRoles(this._rolesPage(), this.narrowingFor(this._groupFilter()))),
        finalize(() => {
          this._roleGroupsLoading.set(false);
          this._rolesLoading.set(false);
        }),
      )
      .subscribe({
        next: (response) => {
          this._roles.set(toPagedResult(response));
        },
        error: (error: unknown) => {
          this.recordFailure(this._roleGroups().length === 0 ? 'loadRoleGroups' : 'loadRoles', error);
        },
      });
  }

  /**
   * Reads the current page of roles under the current narrowing.
   *
   * Legacy: `Roles.ascx.vb:L72-L77`, whose two-armed query choice is now the narrowing
   * translation in {@link RoleStore.narrowingFor}.
   */
  loadRoles(): void {
    this._rolesLoading.set(true);
    this.clearFailure();

    this.roleService
      .listRoles(this._rolesPage(), this.narrowingFor(this._groupFilter()))
      .pipe(finalize(() => this._rolesLoading.set(false)))
      .subscribe({
        next: (response) => {
          this._roles.set(toPagedResult(response));
        },
        error: (error: unknown) => {
          this.recordFailure('loadRoles', error);
        },
      });
  }

  /**
   * Reads every role group in the portal.
   *
   * Legacy: `Roles.ascx.vb:L108`. Unpaged, and no coordinate is held for it.
   */
  loadRoleGroups(): void {
    this._roleGroupsLoading.set(true);
    this.clearFailure();

    this.roleService
      .listRoleGroups()
      .pipe(finalize(() => this._roleGroupsLoading.set(false)))
      .subscribe({
        next: (response) => {
          this._roleGroups.set(response.data);
          this.applyNoGroupsFallback(response.data);
        },
        error: (error: unknown) => {
          this.recordFailure('loadRoleGroups', error);
        },
      });
  }

  /**
   * Reads one role in full and holds it as the selection.
   *
   * Legacy: the edit screen `Website/admin/Security/EditRoles.ascx.vb`, which read the
   * role on entry. Its return navigation is deliberately NOT held here — that was a
   * view-state referrer in other admin screens and is a router concern.
   *
   * @param roleId The role to read, forwarded exactly as supplied. May legitimately be
   * `0`, so it is neither inspected nor defaulted.
   */
  selectRole(roleId: number): void {
    this._selectedRoleLoading.set(true);
    this.clearFailure();

    this.roleService
      .getRole(roleId)
      .pipe(finalize(() => this._selectedRoleLoading.set(false)))
      .subscribe({
        next: (response) => {
          this._selectedRole.set(response.data);
        },
        error: (error: unknown) => {
          this.recordFailure('loadRole', error);
        },
      });
  }

  /** Discards the selected role without contacting the server. */
  clearSelectedRole(): void {
    this._selectedRole.set(null);
  }

  /**
   * Reads the current page of assignments for one role.
   *
   * Legacy: `Website/admin/Security/SecurityRoles.ascx.vb`, which listed the accounts
   * holding a role together with the effective and expiry bounds of each assignment.
   * Those bounds are absolute instants on the wire and are held exactly as received; this
   * module neither reconstructs them from a local offset nor derives them.
   *
   * @param roleId The role whose assignments to read.
   */
  loadAssignments(roleId: number): void {
    this._assignmentsRoleId.set(roleId);
    this._assignmentsLoading.set(true);
    this.clearFailure();

    this.roleService
      .listUsers(roleId, this._assignmentsPage())
      .pipe(finalize(() => this._assignmentsLoading.set(false)))
      .subscribe({
        next: (response) => {
          this._assignments.set(toPagedResult(response));
        },
        error: (error: unknown) => {
          this.recordFailure('loadAssignments', error);
        },
      });
  }

  /**
   * Re-reads the assignments for the role already in scope.
   *
   * Does nothing when no role is in scope, which is tested with an explicit absence check
   * because a role key of `0` is a real role.
   */
  reloadAssignments(): void {
    const roleId = this._assignmentsRoleId();

    if (roleId === null) {
      return;
    }

    this.loadAssignments(roleId);
  }

  // -------------------------------------------------------------------------
  // NARROWING AND PAGE COORDINATES
  // -------------------------------------------------------------------------

  /**
   * Applies a narrowing intent and re-reads the roles under it.
   *
   * Legacy: the narrowing dropdown's change handler at `Roles.ascx.vb:L273-L278`, which
   * parsed the selected value and rebound. MIGRATION: the parse is gone — a narrowing
   * arrives as a typed value, so there is no string to convert and no unrepresentable
   * input to guard.
   *
   * Returns to the first page, because a different narrowing yields a different result set
   * and the page the caller was on has no counterpart in it.
   *
   * @param filter The narrowing to apply.
   */
  setGroupFilter(filter: RoleGroupFilter): void {
    this._groupFilter.set(filter);
    this._rolesPage.update((coordinate) => ({ ...coordinate, pageIndex: 0 }));
    this.loadRoles();
  }

  /**
   * Moves the role listing to a page and re-reads it.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  setRolesPage(pageIndex: number): void {
    this._rolesPage.update((coordinate) => ({ ...coordinate, pageIndex }));
    this.loadRoles();
  }

  /**
   * Re-orders the role listing and re-reads it from the first page.
   *
   * @param sortBy The member to order by, or `null` for the server's default.
   * @param sortDir The direction, or `null` for the server's default.
   */
  setRolesSort(sortBy: string | null, sortDir: SortDirection | null): void {
    this._rolesPage.update((coordinate) => ({ ...coordinate, sortBy, sortDir, pageIndex: 0 }));
    this.loadRoles();
  }

  /**
   * Applies the free-text filter to the role listing and re-reads it from the first page.
   *
   * @param query The filter, or `null` for none. An empty string is passed through
   * unchanged rather than folded to `null`: the legacy contract treated the empty string
   * and absence as one value, and silently normalising between them here would decide a
   * question that belongs to the server.
   */
  setRolesQuery(query: string | null): void {
    this._rolesPage.update((coordinate) => ({ ...coordinate, query, pageIndex: 0 }));
    this.loadRoles();
  }

  /**
   * Moves the assignment listing to a page and re-reads it.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  setAssignmentsPage(pageIndex: number): void {
    this._assignmentsPage.update((coordinate) => ({ ...coordinate, pageIndex }));
    this.reloadAssignments();
  }

  // -------------------------------------------------------------------------
  // ROLE WRITES
  // -------------------------------------------------------------------------

  /**
   * Creates a role, then re-reads the listing.
   *
   * Answers a conflict when the portal already holds a role of that name, which
   * {@link RoleStore.recordFailure} surfaces verbatim through the shared catalogue.
   *
   * The listing is re-read rather than having the new role spliced in, because where the
   * role falls — or whether it falls on the current page at all — depends on the narrowing,
   * the ordering and the page size, none of which this module may re-implement.
   *
   * @param request The role to create.
   */
  createRole(request: CreateRoleRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .createRole(request)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: (response) => {
          this._selectedRole.set(response.data);
          this.loadRoles();
        },
        error: (error: unknown) => {
          this.recordFailure('createRole', error);
        },
      });
  }

  /**
   * Updates a role, reconciles what is held against the server's own response, then
   * re-reads the listing.
   *
   * Both steps are deliberate and neither is redundant. The reconciliation patches the
   * listing row IMMUTABLY from the response body, so a grid reflects the change at once
   * instead of showing stale values while the read is in flight — and it copies the
   * server's values rather than the request's, so it cannot show something the server did
   * not accept. The re-read then settles membership, because changing a role's group can
   * move it out of the current narrowing entirely, which no local patch can determine.
   *
   * The row is matched on identity with a strict comparison. It is never matched by
   * truthiness: the role table is seeded `IDENTITY(0, 1)`, so the portal's first role has
   * key zero and a truthy test would omit it.
   *
   * @param roleId The role to update, forwarded exactly as supplied.
   * @param request The new values.
   */
  updateRole(roleId: number, request: UpdateRoleRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .updateRole(roleId, request)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: (response) => {
          const updated: Role = response.data;

          this._selectedRole.set(updated);
          this._roles.update((page) => ({
            ...page,
            items: page.items.map((item) =>
              item.roleId === updated.roleId ? this.projectListItem(updated) : item,
            ),
          }));
          this.loadRoles();
        },
        error: (error: unknown) => {
          this.recordFailure('updateRole', error);
        },
      });
  }

  /**
   * Deletes a role, then re-reads the listing.
   *
   * MIGRATION: for a ROLE, an empty success really does mean the row is gone — which is
   * exactly what makes {@link RoleStore.removeAssignment} the exception rather than the
   * rule, and why the two are not implemented alike. The listing is still re-read rather
   * than having the row spliced out, because removing a record shifts every page after it.
   *
   * The selection is discarded only when it is the role that was deleted, compared by
   * identity.
   *
   * @param roleId The role to delete, forwarded exactly as supplied.
   */
  deleteRole(roleId: number): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .deleteRole(roleId)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: () => {
          const selected = this._selectedRole();

          if (selected !== null && selected.roleId === roleId) {
            this._selectedRole.set(null);
          }

          this.loadRoles();
        },
        error: (error: unknown) => {
          this.recordFailure('deleteRole', error);
        },
      });
  }

  // -------------------------------------------------------------------------
  // ASSIGNMENT WRITES
  // -------------------------------------------------------------------------

  /**
   * Assigns an account to a role, then RE-READS the assignments.
   *
   * MIGRATION: THE WRITE IS AN UPDATE-OR-ADD AND THE RESPONSE DOES NOT SAY WHICH HAPPENED.
   * `Library/Components/Security/Roles/RoleController.vb:L550-L555` seeded an assignment
   * key with the absent-integer marker at `:L503`, looked for an existing row, and updated
   * it when one was found or added one when it was not. The endpoint preserves that, so it
   * legitimately answers either created or no-content and the client cannot tell them
   * apart — the service types it as empty for that reason. Neither answer is a failure and
   * neither carries a body.
   *
   * Consequently the row is NOT synthesised from the request. The server derives the expiry
   * bound itself, from the role's trial or billing terms and its own clock
   * (`:L521`, `:L537-L547`), and clamps the bounds it was given at `:L529-L534` — so a row
   * built from the request would show bounds the server did not store. Re-reading is the
   * only way to hold what is actually there.
   *
   * @param roleId The role to assign into.
   * @param request The account and the requested bounds.
   */
  assignUser(roleId: number, request: RoleAssignmentRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .assignUser(roleId, request)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: () => {
          this._assignmentsRoleId.set(roleId);
          this.loadAssignments(roleId);
        },
        error: (error: unknown) => {
          this.recordFailure('assignUser', error);
        },
      });
  }

  /**
   * Ends an account's assignment to a role, then RE-READS the assignments.
   *
   * MIGRATION: THIS IS THE MOST CONSEQUENTIAL BEHAVIOUR IN THIS FILE. A successful removal
   * DOES NOT MEAN THE ROW IS GONE. `RoleController.vb:L493-L501` branches on the
   * assignment being paid and having used its trial, and in that case it does NOT delete:
   * `:L496` back-dates the expiry bound to YESTERDAY and `:L497` writes the row back, so
   * the trial-usage record survives. Only the other branch, `:L500`, genuinely deletes.
   * The endpoint answers an empty success in BOTH cases and there is nothing in the
   * response to distinguish them.
   *
   * Therefore the row is NEVER optimistically removed from what is held. An optimistic
   * removal would be wrong roughly half the time — the row is still there, merely expired
   * — and the mistake is invisible until the page is next read. The listing is re-read
   * instead, which is correct for both branches.
   *
   * Two details that make the expired case recognisable, and one trap. The bound is set to
   * yesterday, so recognising an expired assignment is a comparison against the present
   * instant and never an absence check — the bound is present and populated. And an
   * assignment created under the one-time-fee code carries the far-future date 9999-12-31,
   * which is a real stored value rather than a sentinel or an error. The trap: the temporal
   * classification is deliberately NOT derived here. No published contract carries it, the
   * server owns the clock the comparison needs, and a client recomputing it from its own
   * wall clock would disagree across a skew — so the bounds are held verbatim and the
   * classification is left to the presentation layer that renders them.
   *
   * The guard the legacy applied here — refusing to strip the portal administrator or the
   * registered-users role — is enforced server-side and arrives as a conflict, which
   * {@link RoleStore.recordFailure} surfaces verbatim.
   *
   * @param roleId The role to remove from.
   * @param userId The account to remove.
   */
  removeAssignment(roleId: number, userId: number): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .removeUser(roleId, userId)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: () => {
          // Deliberately a re-read and not a removal. See the note above: the row may
          // still exist, expired as of yesterday, and an empty success cannot say.
          this.loadAssignments(roleId);
        },
        error: (error: unknown) => {
          this.recordFailure('removeAssignment', error);
        },
      });
  }

  // -------------------------------------------------------------------------
  // ROLE-GROUP WRITES
  // -------------------------------------------------------------------------

  /**
   * Creates a role group, then re-reads the group listing.
   *
   * Legacy: `Website/admin/Security/EditGroups.ascx.vb`. Answers a conflict when the
   * portal already holds a group of that name.
   *
   * @param request The group to create.
   */
  createRoleGroup(request: CreateRoleGroupRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .createRoleGroup(request)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: () => {
          this.loadRoleGroups();
        },
        error: (error: unknown) => {
          this.recordFailure('createRoleGroup', error);
        },
      });
  }

  /**
   * Renames or re-describes a role group, reconciling what is held from the response.
   *
   * The listing is patched immutably from the server's own response body rather than
   * re-read, which is safe here in a way it is not for a role: a group's identity does not
   * determine its own membership of the group listing, so nothing can move it out. The row
   * is matched on identity with a strict comparison — never by truthiness, because the
   * group table is seeded `IDENTITY(0, 1)` and a group key of zero is a real group.
   *
   * @param roleGroupId The group to update, forwarded exactly as supplied.
   * @param request The new values.
   */
  updateRoleGroup(roleGroupId: number, request: UpdateRoleGroupRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .updateRoleGroup(roleGroupId, request)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: (response) => {
          const updated: RoleGroup = response.data;

          this._roleGroups.update((groups) =>
            groups.map((group) => (group.roleGroupId === updated.roleGroupId ? updated : group)),
          );
        },
        error: (error: unknown) => {
          this.recordFailure('updateRoleGroup', error);
        },
      });
  }

  /**
   * Deletes a role group, resets the narrowing, then re-reads.
   *
   * Legacy: `Website/admin/Security/Roles.ascx.vb:L290-L297`, which is three measured
   * behaviours, all reproduced:
   *
   * 1. `:L293` performs the delete ONLY for a real group key, guarding against the two
   *    pseudo-intents. MIGRATION: that numeric guard is replaced by the TYPE SYSTEM rather
   *    than restated. A key can only be obtained from a `Group` narrowing — the pseudo-
   *    intents carry none — so the legacy comparison has nothing left to test and
   *    reproducing it would be a sentinel check on a value that can no longer be a
   *    sentinel.
   * 2. `:L295` resets the narrowing to the UNGROUPED intent, not to the every-role one.
   *    Reproduced exactly, and worth stating because the two are easily transposed.
   * 3. `:L296` re-reads the groups. The target re-reads the roles as well: the narrowing
   *    just changed, so the roles on screen no longer answer to it, and the legacy screen
   *    got away with rebinding only the dropdown because its next postback re-read
   *    everything anyway.
   *
   * MIGRATION: THE CONFLICT IS HANDLED UNCONDITIONALLY, and this call is never suppressed
   * by {@link RoleStore.canDeleteSelectedGroup}. A group that still classifies a role
   * cannot be removed — the server-side expression of the legacy affordance guard at
   * `:L85` — and it answers with a conflict. Because that conflict PROVES the held listing
   * was stale, the groups and roles are re-read on the failure path too, which corrects
   * the affordance instead of leaving a control enabled that will fail again.
   *
   * @param roleGroupId The group to delete, forwarded exactly as supplied.
   */
  deleteRoleGroup(roleGroupId: number): void {
    this._saving.set(true);
    this.clearFailure();

    this.roleService
      .deleteRoleGroup(roleGroupId)
      .pipe(finalize(() => this._saving.set(false)))
      .subscribe({
        next: () => {
          // The legacy reset target is the UNGROUPED intent, per `:L295`.
          this._groupFilter.set({ kind: 'GlobalRoles' });
          this._rolesPage.update((coordinate) => ({ ...coordinate, pageIndex: 0 }));
          this.loadRoleAdministration();
        },
        error: (error: unknown) => {
          // A conflict means the group still classifies at least one role, so what is held
          // was stale and re-reading corrects the affordance.
          //
          // THE ORDER MATTERS AND IS NOT COSMETIC. Both read commands begin by discarding
          // the held failure, because a command a user starts deserves a clean slate — but
          // a refresh the store starts in response to a failure is not a new user command,
          // and letting it run after the failure was recorded would erase the very
          // conflict it exists to explain. So the refresh is dispatched first and the
          // failure recorded afterwards, which is correct whether the reads answer
          // synchronously or not.
          if (this.isConflictFailure(error)) {
            this.loadRoleGroups();
            this.loadRoles();
          }

          this.recordFailure('deleteRoleGroup', error);
        },
      });
  }

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  /** Discards the held failure without contacting the server. */
  clearError(): void {
    this._failure.set(null);
  }

  /**
   * Returns every slice to its initial value.
   *
   * For a sign-out or a tenant change, after which nothing held is still true. The
   * narrowing returns to {@link DEFAULT_ROLE_GROUP_FILTER} — the ungrouped intent, per the
   * measured legacy default — and both coordinates return to the first page.
   *
   * Not a cache eviction: there is no cache to evict. See MIGRATION note 14.
   */
  reset(): void {
    this._roles.set(emptyPagedResult<RoleListItem>());
    this._roleGroups.set([]);
    this._groupFilter.set(DEFAULT_ROLE_GROUP_FILTER);
    this._selectedRole.set(null);
    this._assignments.set(emptyPagedResult<UserRole>());
    this._assignmentsRoleId.set(null);
    this._rolesPage.set(INITIAL_PAGE_COORDINATE);
    this._assignmentsPage.set(INITIAL_PAGE_COORDINATE);
    this._rolesLoading.set(false);
    this._roleGroupsLoading.set(false);
    this._selectedRoleLoading.set(false);
    this._assignmentsLoading.set(false);
    this._saving.set(false);
    this._failure.set(null);
  }
}
