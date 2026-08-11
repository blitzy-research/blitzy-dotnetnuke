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

import {
  DEFAULT_PAGE_SIZE,
  emptyPagedResult,
  toPagedResult,
} from '../models/paged-result.model';
import { isProblemDetails } from '../models/problem-details.model';
import { RoleService } from '../services/role.service';
import { failureCode, isConflictCode, summarizeProblem } from '../utils/form-errors.util';

import type { OnDestroy } from '@angular/core';
import type { Observable, Subscription } from 'rxjs';
import type { PagedResponse } from '../models/paged-result.model';
import type { ApiMeta, PagedResult, SortDirection } from '../models/paged-result.model';
import type { ProblemDetails } from '../models/problem-details.model';
import type {
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  StoredBillingFrequency,
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
 * ever slips past — the failure mode the legacy branch at `RoleController.vb:L540-L547`
 * had, which {@link billingTermsBound} answers with a named classification instead,
 * because there the missing case is reachable from real stored data rather than from
 * an omission in this file.
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
  | 'Bounded'
  /**
   * The stored code is outside the supported vocabulary, so the terms cannot be classified.
   *
   * ⚠ NOT AN ERROR STATE, AND NOT A SYNONYM FOR `Unbounded`. It is the honest reading of a
   * stored character the six codes do not declare — which shipped DotNetNuke data really
   * contains — and it is deliberately distinct from both of the answers it could be mistaken
   * for. See {@link billingTermsBound} for the measured legacy behaviour it describes.
   */
  | 'Unsupported';

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
 * MIGRATION: THE LEGACY BRANCH HAD NO FALLBACK ARM, AND THIS FUNCTION NAMES WHAT THAT MEANT.
 * `:L540-L547` is a six-arm branch with no final catch-all, so an unrecognised code applied NO
 * advance and left the expiry at whatever value it already held — which at that point was the
 * current instant, or the row's existing expiry clamped up to it. So an unsupported code neither
 * removes the expiry nor advances it, and reporting it as `Unbounded` or as `Bounded` would each
 * assert something the terms do not say. It is classified as {@link BillingTermsBound}
 * `Unsupported` instead.
 *
 * MIGRATION: THE PARAMETER IS THE STORED CODE, NOT THE WRITE VOCABULARY. The API carries a
 * stored frequency character through losslessly and shipped DotNetNuke data contains two roles
 * whose characters fall outside the published six, so a classifier that could only accept those
 * six could not be called with real data. An earlier revision was exhaustive over the closed
 * union and ended in an unreachable-case assertion; that assertion is gone from the frequency
 * arm because the case is reachable, and pretending otherwise would have turned one legacy row
 * into a thrown error on a screen that merely wanted to describe it.
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
  frequency: StoredBillingFrequency | null,
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
      // REACHABLE, and reached by shipped data rather than by drift. The code is a real stored
      // character that the supported vocabulary does not describe, so it is reported as
      // unsupported and is never folded onto one of the six above.
      return 'Unsupported';
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
  | 'loadRolesHeldByUser'
  | 'probeAssignment'
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
   * The transport status, or `null` when the failure carried none at all.
   *
   * Published SEPARATELY from {@link RoleStoreFailure.problem} because the two go missing
   * independently: a transport failure has a status and no document, and a document may omit
   * its own status member. A screen reproducing the legacy wording branches on the status, and
   * reading it off the document instead would collapse exactly that distinction.
   */
  readonly status: number | null;

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

/**
 * One settled write, identified so that the screen which started it can recognise it.
 *
 * ## The defect this closes
 *
 * This store is provided at the application root, so the role listing, the role form and the
 * membership screen all share ONE instance and their writes overlap freely. Before this type
 * existed, a screen settled its own write by watching a single boolean fall — and a boolean says
 * only "something is writing", never "yours is". Three concrete failures followed, and none of
 * them produced an error anywhere:
 *
 * - The role form announced success and NAVIGATED AWAY the moment any unrelated role write
 *   settled. Its own save was still outstanding, so the operator was told it had worked and
 *   moved off the screen before the server had answered — and if the server then refused, there
 *   was no longer a form to report the refusal on.
 * - The form paired that with the SHARED failure slot: if the unrelated write had failed, the
 *   form reported that other failure as its own.
 * - The membership screen released its enrolment lock on the same signal, so a second enrolment
 *   could be submitted while the first was still in the air.
 *
 * ## How a caller uses it
 *
 * Every write command returns a number. A caller that cares about the outcome keeps it and acts
 * only on a published result whose {@link RoleMutation.id} matches:
 *
 * ```ts
 * private readonly awaited = signal<number>(0);
 *
 * save(): void {
 *   this.awaited.set(this.store.updateRole(roleId, request));
 * }
 *
 * // in an effect
 * const settled = this.store.mutation();
 * if (settled === null || settled.id !== this.awaited()) {
 *   return;                       // somebody else's write
 * }
 * ```
 *
 * The comparison is a strict equality on a number that is never 0 for a real write, so a caller
 * whose marker is still at its zero initialiser matches nothing — the same "the first ticket is 1,
 * and 0 matches nothing" discipline `core/utils/operation-generation.util.ts` applies to reads.
 */
export interface RoleMutation {
  /** The identifier the write command returned to its caller. */
  readonly id: number;

  /** Which command settled. Kept so a caller can assert the kind as well as the identity. */
  readonly operation: RoleStoreOperation;

  /**
   * The refusal this particular write met, or `null` when it succeeded.
   *
   * ⚠ CARRIED HERE RATHER THAN READ FROM THE SHARED SLOT. {@link RoleStore.failure} holds the most
   * recent failure of any command and is cleared by the next dispatch, so a caller reading it after
   * its own write settled could find a concurrent write's refusal, or find nothing where its own
   * refusal had been a moment earlier. This member is the failure of THIS write and of no other.
   */
  readonly failure: RoleStoreFailure | null;
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
 * Which account's membership of which role one probe answered.
 *
 * The probe in {@link RoleStore.probeAssignment} answers a question about ONE pairing, so
 * its answer is only meaningful beside the pairing it was asked about. Publishing the two
 * together is what lets a screen tell "this account holds no membership" from "the answer
 * in hand belongs to a different account", which are different states and read differently
 * to an operator.
 *
 * Both members are plain identifiers and both may legitimately be `0` or negative: the role
 * table is seeded `IDENTITY(0, 1)`, so role zero is a portal's first role.
 */
export interface AssignmentProbeKey {
  /** The role the probe asked about. */
  readonly roleId: number;

  /** The account the probe asked about. */
  readonly userId: number;
}

/**
 * Whether a membership listing is on screen, and for which role.
 *
 * Distinct from {@link RoleStore.assignmentsRoleId}, and the distinction is the whole reason the
 * type exists. That member labels the ROWS IN HAND — it answers "whose memberships are these",
 * which stays answerable long after the screen that asked for them has gone, because the rows are
 * still those of that role until something replaces them. This type answers a different question:
 * "is anybody looking, and at what".
 *
 * Conflating the two was a defect, and a reachable one. The rows' label is only ever MOVED, never
 * vacated: a screen that leaves does not blank the grid on its way out, so the label still named the
 * role after the operator had returned to the role listing. A settled write asking "is my role still
 * on screen" was answered from that stale label and told yes, and re-read a listing nobody was
 * looking at — which is not merely a wasted round trip, because the re-read clears the shared
 * failure slot and would erase a message the LISTING screen was showing. Runtime validation
 * reproduced it three times out of three on the ordinary operator path, one role's memberships to
 * another, because the role listing is the unavoidable stop in between.
 *
 * The three states are exhaustive and each carries a different answer for that question:
 *
 * - `none` — no membership listing has been on screen since the last reset. A settled write ADOPTS
 *   the role it wrote to, because there is no set to overwrite and no heading to mis-label. This is
 *   the published contract of both membership writes and the only thing that makes a write
 *   observable at all on a fresh store.
 * - `open` — a listing is on screen for `roleId`. A settled write refreshes it when the role
 *   matches, and is refused when it does not.
 * - `left` — a listing WAS on screen and has gone. A settled write refreshes nothing: there is no
 *   grid to update, and the rows in hand are stale by definition.
 *
 * `left` is reachable only from `open`, and only for the role that was open, so a screen tearing
 * down cannot cancel a replacement that has already announced itself.
 */
type AssignmentsViewState =
  | { readonly kind: 'none' }
  | { readonly kind: 'open'; readonly roleId: number }
  | { readonly kind: 'left' };

/**
 * The state a store with no membership listing on screen begins in.
 *
 * Named rather than written inline so the initial value and {@link RoleStore.reset} cannot drift
 * apart, which would leave a reset store in `left` and silently suppress the adoption contract.
 */
const NO_ASSIGNMENTS_VIEW: AssignmentsViewState = Object.freeze({ kind: 'none' });

/**
 * The state a store is in once the membership listing that was on screen has gone.
 */
const ASSIGNMENTS_VIEW_LEFT: AssignmentsViewState = Object.freeze({ kind: 'left' });

/**
 * The page size the ROLE listing reads with.
 *
 * ⚠ THE LISTING IS NOW GENUINELY PAGED, AND THE CHANGE REPLACED A COMPLETE-LISTING WALK. This slice
 * used to request page after page at the endpoint's maximum of a hundred records and join them into
 * one unpaged envelope, because the screen consuming it offered no pager and a single window would
 * have presented the first page AS the whole set. Runtime testing measured what that cost on a real
 * tenant: a hundred and forty-five roles rendered as an eight-thousand-pixel document at 320 units
 * wide with no way to reach anything but by scrolling, THREE requests per arrival, the whole walk
 * re-issued on every browser Back and after every write, and thirty-six of those re-reads aborted
 * mid-flight. The pager is what removes all of that, and it removes the walk with it: completeness is
 * delivered by being able to REACH every page rather than by holding every page at once, which is
 * exactly how the sibling assignment listing in this same store has always worked.
 *
 * MIGRATION: A PAGER IS AN ADDITION, AND IT IS RECORDED AS ONE. `roles.ascx` declares no paging
 * control and no `AllowPaging`, its grid bound a plain untyped list (`Roles.ascx.vb:L77`), and the
 * code-behind holds no page index, size or record total anywhere. A tenant of the size the legacy
 * product shipped — six stock roles — fits one page, so it renders with no page-to-page affordance at
 * all and reads as it always did; the addition only becomes visible on a tenant the legacy screen
 * could not have displayed usefully in the first place. The divergence is written up in
 * `MIGRATION_NOTES.md`.
 *
 * The shared default is taken rather than a size chosen for this screen, so the role listing pages
 * like every other listing in the application. The endpoint's own validator caps a page at a hundred
 * records (`Application/Validation/PagedRequestValidator.cs`), well above this.
 */
export const ROLES_PAGE_SIZE = DEFAULT_PAGE_SIZE;

/**
 * The coordinate the ROLE listing starts at.
 *
 * Identical in shape and size to {@link INITIAL_PAGE_COORDINATE}, which seeds the ASSIGNMENT
 * listing, and kept as its own constant so the two listings can be re-seeded independently — a reset
 * of one must not silently move the other. The size it publishes is the size its requests actually
 * carry, which is what keeps {@link RoleStore.rolesPage} honest for the pager reading it.
 */
const INITIAL_ROLES_COORDINATE: RolePageCoordinate = Object.freeze({
  pageIndex: 0,
  pageSize: ROLES_PAGE_SIZE,
  sortBy: null,
  sortDir: null,
  query: null,
});

/**
 * The sentence announcing a membership read that could not be completed.
 *
 * AUTHORED WORDING, and recorded as such: the legacy screen had no equivalent outcome to
 * borrow from, because it read the whole membership in one unpaged call
 * (`Website/admin/Security/SecurityRoles.ascx.vb:L204`) and so could neither truncate nor
 * report a truncation. It is written for the decision the operator is about to make —
 * whether to add or remove a membership — so it says plainly that the list is partial and
 * gives both counts rather than a vague apology.
 *
 * Both numbers are stated because either alone is useless: the rows in hand tell the
 * operator what they can act on, and the server's total tells them how much is missing.
 *
 * Plain text with no markup, because every consumer renders a failure summary as text —
 * the legacy resource files carry live HTML in dozens of values and the workspace's rule is
 * that no failure wording is ever bound as raw markup.
 *
 * @param gathered The envelope the walk assembled, carrying the server's own total.
 * @returns One sentence naming the shortfall in both directions.
 */
function assignmentShortfallMessage(gathered: PagedResult<UserRole>): string {
  const held: string = String(gathered.items.length);
  const total: string = String(gathered.meta.totalCount);

  return (
    `Only ${held} of ${total} memberships could be read for this role, ` +
    'so the list below is incomplete. Narrow the role or try again before adding or ' +
    'removing a membership.'
  );
}

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
export class RoleStore implements OnDestroy {

  private readonly roleService = inject(RoleService);

  // -------------------------------------------------------------------------
  // REQUEST HANDLES
  // -------------------------------------------------------------------------
  //
  // One handle per INDEPENDENT read, plus one set holding every write in flight. They exist
  // so that a read can be ABANDONED, which is what makes each slice a function of the
  // latest request rather than of whichever response happens to arrive last.
  //
  // ⚠ WHY A HANDLE PER READ RATHER THAN ONE FOR ALL OF THEM. Each read writes a DIFFERENT
  // slice and several are legitimately outstanding at once — the list screen reads the
  // groups and the roles together, and the assignment screen reads a role and its members
  // together. Sharing one handle would let the second read cancel the first and leave that
  // slice permanently empty. Sharing is correct only between requests that write the SAME
  // slice, which is why the complete-listing walk and the plain listing read share one.
  //
  // ⚠ A READ IS CANCELLED, A WRITE IS NOT. Abandoning a read discards an answer nobody is
  // waiting for. Abandoning a write would stop this client listening WITHOUT undoing
  // anything the server may already have committed, so writes are released only on teardown
  // and on a reset that is discarding the whole store.
  //
  // MIGRATION: the legacy screen could not have this defect, so there is no legacy rule to
  // preserve here. `Website/admin/Security/Roles.ascx.vb` rebuilt its grid synchronously
  // inside each post-back, so a second read could not overtake a first. Once reads became
  // asynchronous the ordering that arrangement gave away for free had to be stated, and this
  // is where it is stated.

  /** The role listing read, whether the complete walk or a single request within it. */
  private rolesRequest: Subscription | null = null;

  /** The handle for the roles-held-by-one-account read. Its own, so it cancels independently. */
  private heldRolesRequest: Subscription | null = null;

  /** The role-group listing read. */
  private roleGroupsRequest: Subscription | null = null;

  /** The single-role read. */
  private selectedRoleRequest: Subscription | null = null;

  /** The assignment listing read. */
  private assignmentsRequest: Subscription | null = null;

  /**
   * The single-account membership probe.
   *
   * Held apart from {@link RoleStore.assignmentsRequest} because the two answer different
   * questions over the same address: the listing is the page a screen is showing, the probe is
   * "does this one account hold this role, and on what terms". Sharing one handle would let a
   * probe abandon the page read the grid is waiting on, and the reverse.
   */
  private assignmentProbeRequest: Subscription | null = null;

  /**
   * Every write in flight.
   *
   * A set rather than a single handle, because two writes may legitimately overlap and
   * neither should cancel the other. Each entry removes itself once it settles, so the set
   * cannot grow without bound.
   */
  private readonly writeRequests = new Set<Subscription>();

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

  /**
   * Whether a membership listing is on screen, and for which role.
   *
   * Maintained by the screen itself through {@link RoleStore.openAssignmentsView} and
   * {@link RoleStore.closeAssignmentsView}, because the screen is the only thing that knows when it
   * arrives and when it goes. Read only by {@link RoleStore.refreshAssignmentsIfCurrent}; see
   * {@link AssignmentsViewState} for why this cannot be inferred from
   * {@link RoleStore.assignmentsRoleId}.
   */
  private readonly _assignmentsView = signal<AssignmentsViewState>(NO_ASSIGNMENTS_VIEW);

  /**
   * The page, ordering, filter and request size the role listing was last read with.
   *
   * Every member is live. The page index moves through {@link RoleStore.setRolesPage}, and the
   * remaining three are re-seeded to the first page whenever they change, because a different
   * narrowing, ordering or filter yields a different result set in which the page the caller was
   * standing on has no counterpart.
   */
  private readonly _rolesPage = signal<RolePageCoordinate>(INITIAL_ROLES_COORDINATE);

  /** The coordinate the assignment listing was last read at. */
  private readonly _assignmentsPage = signal<RolePageCoordinate>(INITIAL_PAGE_COORDINATE);

  /**
   * The membership one probe found, or `null` when the probe found none.
   *
   * `null` is a real answer — "the account the key names holds no membership of the role the
   * key names" — and is distinguished from "no probe has answered" by
   * {@link RoleStore._probedAssignmentKey} being `null` instead.
   */
  private readonly _probedAssignment = signal<UserRole | null>(null);

  /** The pairing {@link RoleStore._probedAssignment} answers for, or `null` when none has. */
  private readonly _probedAssignmentKey = signal<AssignmentProbeKey | null>(null);

  private readonly _rolesLoading = signal<boolean>(false);

  /**
   * The roles ONE ACCOUNT holds, when the listing has been narrowed to an account.
   *
   * ⚠ HELD APART FROM {@link RoleStore._roles}, NOT WRITTEN OVER IT. The browsable listing is
   * paged, ordered and filterable and a screen may be showing it; this is an unpaged answer to a
   * different question. Writing one into the other would make opening an account's memberships
   * silently replace a sibling listing's rows and discard the page it was on — the same defect the
   * module store's picker slice exists to avoid.
   *
   * `null` means NO ANSWER FOR THE CURRENT SUBJECT — either because no account is the subject at
   * all, or because the subject has just changed and the new account's read has not answered yet.
   * It is distinct from an account that holds no role, which is an EMPTY ARRAY and a successful
   * answer.
   *
   * ⚠ THE SECOND MEANING IS WHY THIS SLICE IS DISCARDED WHEN THE SUBJECT CHANGES. Holding the
   * previous account's answer while the next account's read was outstanding let a consumer read a
   * non-null collection alongside the NEW subject key and present one person's memberships — or a
   * count of them — under another person's name. A consumer cannot be relied on to notice that
   * window, so the window is closed here instead. See {@link RoleStore.loadRolesHeldByUser}.
   */
  private readonly _rolesHeldByUser = signal<readonly RoleListItem[] | null>(null);

  /**
   * Which account {@link RoleStore._rolesHeldByUser} describes, or `undefined` when none.
   *
   * Recorded so a screen can tell whether the collection in hand is the one it is showing, rather
   * than assuming the answer it is looking at answers the account it asked about.
   */
  private readonly _heldRolesUserId = signal<number | undefined>(undefined);

  /** Whether the roles-held-by-one-account read is in flight. */
  private readonly _heldRolesLoading = signal<boolean>(false);
  private readonly _roleGroupsLoading = signal<boolean>(false);
  private readonly _selectedRoleLoading = signal<boolean>(false);
  private readonly _assignmentsLoading = signal<boolean>(false);

  /**
   * How many writes are outstanding.
   *
   * ⚠ A COUNT RATHER THAN A FLAG, and the difference is a correctness one. This store is provided at
   * the application root, so several screens share it and their writes overlap. A boolean set true at
   * each dispatch and false at each settlement is simply WRONG under overlap: two writes start, the
   * first settles, the flag falls, and the second is reported as finished while it is still in the
   * air. A count is right for any number of concurrent writes and degenerates to the flag's behaviour
   * for one.
   *
   * Decremented with a floor at zero so that a settlement arriving twice - which `finalize` cannot
   * produce, but a future refactor could - cannot drive the count negative and leave `saving`
   * permanently false.
   */
  private readonly _pendingWrites = signal<number>(0);

  /**
   * The most recently settled write, identified.
   *
   * ⚠ THIS, NOT THE COUNT, IS WHAT A SCREEN SETTLES ON. See {@link RoleStore.mutation}.
   */
  private readonly _mutation = signal<RoleMutation | null>(null);

  /**
   * The identifier issued to the last write dispatched.
   *
   * A plain counter rather than a signal: nothing observes it, and it is read only to produce the
   * next value. Pre-incremented, so the first identifier ever issued is 1 and 0 is a value no write
   * holds - which lets a consumer use 0 as "no write of mine is outstanding" without a nullable
   * field.
   */
  private nextMutationId = 0;

  /** Whether the single-account membership probe is in flight. */
  private readonly _assignmentProbeLoading = signal<boolean>(false);

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

  /**
   * The membership the last probe found, or `null` when it found none.
   *
   * Read it BESIDE {@link RoleStore.probedAssignmentKey}: an answer is only about the pairing
   * that key names, so a consumer confirms the key matches the pairing it cares about before
   * acting on the answer. See {@link RoleStore.probeAssignment}.
   */
  readonly probedAssignment = this._probedAssignment.asReadonly();

  /** The pairing {@link RoleStore.probedAssignment} answers for, or `null` when none has. */
  readonly probedAssignmentKey = this._probedAssignmentKey.asReadonly();

  readonly rolesLoading = this._rolesLoading.asReadonly();

  /** The roles one account holds, or `null` when no account is the subject. */
  readonly rolesHeldByUser = this._rolesHeldByUser.asReadonly();

  /** Which account {@link RoleStore.rolesHeldByUser} describes, or `undefined`. */
  readonly heldRolesUserId = this._heldRolesUserId.asReadonly();

  /** Whether the roles-held-by-one-account read is in flight. */
  readonly heldRolesLoading = this._heldRolesLoading.asReadonly();
  readonly roleGroupsLoading = this._roleGroupsLoading.asReadonly();
  readonly selectedRoleLoading = this._selectedRoleLoading.asReadonly();
  readonly assignmentsLoading = this._assignmentsLoading.asReadonly();

  /** Whether the single-account membership probe is in flight. */
  readonly assignmentProbeLoading = this._assignmentProbeLoading.asReadonly();

  /**
   * Whether ANY write is in flight, anywhere in the application.
   *
   * ⚠ AN AGGREGATE, AND IT MUST NOT BE USED TO SETTLE A PARTICULAR WRITE. It is derived from the
   * pending-write count, so it is now accurate under overlap - it stays true until the LAST
   * outstanding write settles rather than until the first one does - but accurate is not the same as
   * specific. It answers "is the store writing", which is the right question for disabling a submit
   * affordance and the wrong question for "did MY save succeed".
   *
   * MIGRATION: waiting for this to fall was exactly how the role screens used to settle their own
   * writes, and it was a defect in two directions. The form announced success and navigated away when
   * an unrelated write elsewhere finished, before its own had; and the membership screen released its
   * assignment lock on the same signal, so a second enrolment could be submitted while the first was
   * still outstanding. Both now settle on {@link RoleStore.mutation}.
   */
  readonly saving = computed<boolean>(() => this._pendingWrites() > 0);

  /**
   * The most recently settled write, carrying the identifier its caller was given.
   *
   * ⚠ THE ONLY CORRECT WAY TO SETTLE A WRITE. Every write command returns a number, and a caller that
   * cares about the outcome keeps it and compares it against `mutation()?.id`. A published result
   * whose identifier is not the caller's belongs to somebody else and must be ignored - which is a
   * total test, needing no knowledge of which other screens exist or what they are doing.
   *
   * The failure travels ON THE RESULT rather than being read from {@link RoleStore.failure}, and that
   * is the second half of the same fix: the failure slot is shared and a concurrent write clears it at
   * dispatch, so a caller reading it could find another write's refusal, or find nothing where its own
   * refusal had been. `failure` remains the right thing for a banner to BIND to; it is not the right
   * thing to DECIDE with.
   */
  readonly mutation = this._mutation.asReadonly();

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
      this._heldRolesLoading() ||
      this._roleGroupsLoading() ||
      this._selectedRoleLoading() ||
      this._assignmentsLoading() ||
      this._assignmentProbeLoading() ||
      this.saving(),
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
  private recordFailure(operation: RoleStoreOperation, error: unknown): RoleStoreFailure {
    const problem: ProblemDetails | null = readProblem(error);
    const status: number | null = readStatus(error);
    const described: ProblemDetails | null =
      problem ?? (status === null ? null : { status });
    const code: string | null = failureCode(problem);

    const failure: RoleStoreFailure = {
      operation,
      status,
      summary: summarizeProblem(described),
      problem,
      conflict: isConflictCode(code) ? code : null,
    };

    this._failure.set(failure);

    // ⚠ RETURNED AS WELL AS PUBLISHED, so a write can carry its OWN failure on its own settled
    // result. The published slot is shared and the next dispatch clears it, so a write that had to
    // re-read this slot at settlement time could find a concurrent write's refusal or find nothing
    // at all. Handing the record straight back removes the second read.
    return failure;
  }

  /**
   * Discards the held failure, so a fresh command starts from a clean slate.
   *
   * ⚠ THE ASSIGNMENT IS UNCONDITIONAL, AND THE GUARD IT REPLACES WAS ACTIVELY HARMFUL. Testing the
   * slice first bought nothing — a signal set to a value it already holds compares equal and
   * notifies nobody — while the test itself was a READ, and every command begins by calling this.
   * Any caller reaching a command from inside a reactive computation therefore took a dependency on
   * the failure slice without asking for one, and the consequence was demonstrable: a screen whose
   * route observer issued a read re-ran on a recorded failure, cleared the very failure it had
   * reacted to, and left the observer waiting on that failure to report a refused write as a
   * success. Writing unconditionally reads nothing and behaves identically.
   */
  private clearFailure(): void {
    this._failure.set(null);
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
    // Both reads this command owns are abandoned first, so a slower pair issued for an
    // earlier narrowing cannot land after this one and leave the groups and the roles
    // describing different narrowings.
    this.roleGroupsRequest?.unsubscribe();
    this.rolesRequest?.unsubscribe();
    this._roleGroupsLoading.set(true);
    this._rolesLoading.set(true);
    this.clearFailure();

    // ONE handle for the chain, held as the roles handle because the roles read is its
    // tail: cancelling it abandons whichever half is still outstanding.
    this.rolesRequest = this.roleService
      .listRoleGroups()
      .pipe(
        tap((response) => {
          this._roleGroups.set(response.data);
          this._roleGroupsLoading.set(false);
          this.applyNoGroupsFallback(response.data);
        }),
        switchMap(() =>
          this.roleService.listRoles(this._rolesPage(), this.narrowingFor(this._groupFilter())),
        ),
        finalize(() => {
          this._roleGroupsLoading.set(false);
          this._rolesLoading.set(false);
        }),
      )
      .subscribe({
        next: (response) => {
          // The envelope is unwrapped HERE rather than inside the chain, because the chain's tail is
          // now the service call itself: what arrives is the raw paged response, and the record type is
          // named explicitly because the framing decoder validates framing only and is generic in the
          // record.
          this._roles.set(toPagedResult<RoleListItem>(response));
        },
        error: (error: unknown) => {
          this.recordFailure(this._roleGroups().length === 0 ? 'loadRoleGroups' : 'loadRoles', error);
        },
      });
  }

  /**
   * Reads ONE PAGE of the role listing.
   *
   * Legacy: `Roles.ascx.vb:L72-L77`, whose two-armed query choice is now the narrowing translation in
   * {@link RoleStore.narrowingFor}.
   *
   * ⚠ EXACTLY ONE REQUEST PER READ, AND THE PAGE IS THE UNIT. This replaced a complete-listing walk
   * that requested page after page and joined them — see {@link ROLES_PAGE_SIZE} for what that cost on
   * a real tenant and why it went. The endpoint counts and windows per request, so the page in hand
   * plus the total on its metadata is everything a pager needs, and {@link RoleStore.setRolesPage}
   * reaches every other page. Completeness is delivered by being able to REACH every role rather than
   * by holding every role.
   *
   * The narrowing, the ordering and the free-text filter remain the server's work and are forwarded on
   * the request. Nothing is sorted, filtered, joined or de-duplicated here.
   */
  loadRoles(): void {
    this.rolesRequest?.unsubscribe();
    this.clearFailure();
    this.dispatchRoles();
  }

  /**
   * Issues one page read for the role listing, correcting a coordinate left past the end.
   *
   * ⚠ WHY A CORRECTION IS NEEDED, AND WHY IT ARRIVED WITH THE PAGING. A DELETION CAN STRAND THE
   * OPERATOR. Remove the only role on the last page and the coordinate they are standing on stops
   * existing: the write's re-read asks for it again, the server answers an empty page whose metadata
   * still reports the true total, and the grid renders nothing. Worse, the pager is drawn on
   * `pageSize < totalCount` — eleven roles at ten a page becomes ten at ten a page, that predicate
   * turns false, and the pager is WITHDRAWN, leaving a blank grid with no affordance back to the rows
   * that are still there. The unpaged listing this replaced could not reach that state; the row-level
   * delete command on the listing makes it reachable, so the correction ships with them both.
   *
   * ⚠ THIS IS NOT A REINTERPRETATION OF WHAT A CALLER ASKED FOR. {@link RoleStore.setRolesPage} sends
   * the index it was given, untouched — an index past the last page is a real state of the world and
   * the server is entitled to answer it. What is acted on here is the SERVER'S OWN ANSWER: an empty
   * window beyond a positive total is the server saying the coordinate no longer names anything.
   *
   * The correction is BOUNDED BY CONSTRUCTION: the corrective read is issued with correction withheld,
   * so a listing that keeps shrinking underneath the screen costs at most one extra request per read
   * and can never loop. The step-back target comes from the server's own reported page count, never
   * from arithmetic over the rows in hand.
   *
   * Written as the mirror of {@link RoleStore.dispatchAssignments}, deliberately: two paged listings in
   * one store that corrected differently would be two behaviours to reason about instead of one.
   *
   * @param correctionAllowed Whether a past-the-end answer may issue one corrective read. `false` on
   * the corrective read itself, which is what makes the recursion terminate.
   */
  private dispatchRoles(correctionAllowed = true): void {
    this.rolesRequest?.unsubscribe();
    this._rolesLoading.set(true);

    // The loading flag is lowered EXPLICITLY rather than through `finalize`, for the reason given on
    // the assignment dispatcher: a corrective read is issued from inside the first read's `next`, and
    // a finaliser would run after it and report the grid at rest with a request still outstanding.
    this.rolesRequest = this.roleService
      .listRoles(this._rolesPage(), this.narrowingFor(this._groupFilter()))
      .subscribe({
        next: (response) => {
          const page: PagedResult<RoleListItem> = toPagedResult<RoleListItem>(response);
          const requestedPageIndex: number = this._rolesPage().pageIndex;
          const lastExistingPageIndex = page.meta.totalPages - 1;

          // Every clause is load-bearing. A positive total separates "this window is past the end"
          // from "this narrowing matches no roles", which is a legitimate empty answer that must not
          // provoke a second request. A first-page request is never past the end, whatever the total.
          // And the server's page count must actually fall short of the index asked for, so a server
          // reporting a window that does exist is believed.
          const pastTheEnd =
            correctionAllowed &&
            page.items.length === 0 &&
            page.meta.totalCount > 0 &&
            requestedPageIndex > 0 &&
            lastExistingPageIndex < requestedPageIndex;

          if (pastTheEnd) {
            this._rolesPage.update((coordinate) => ({
              ...coordinate,
              // Never negative: this arm is only reached with a positive total, so the server reported
              // at least one page. The floor is stated rather than assumed because the alternative is
              // a negative index on the wire.
              pageIndex: lastExistingPageIndex > 0 ? lastExistingPageIndex : 0,
            }));

            this.dispatchRoles(false);
            return;
          }

          this._roles.set(page);
          this._rolesLoading.set(false);
        },
        error: (error: unknown) => {
          this._rolesLoading.set(false);
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
    // ⚠ AN IDENTICAL READ ALREADY IN FLIGHT IS JOINED, NOT RESTARTED, and this line used to abandon it.
    // Two role screens each need the group list on entry and each asks for it, which is correct - neither
    // may render a group selector from data it has not read. What was not correct is what happened when the
    // second asked while the first was still answering: the handle was unsubscribed, so the first request
    // was cancelled mid-flight and reported as an aborted request, and a second identical one was issued in
    // its place. Runtime testing saw the pair on ordinary role-feature route changes.
    //
    // Returning here is NOT a cache and introduces no staleness of any kind: a read IS in flight, it will
    // publish to the same slots this call would have published to, and the caller's need is met by it. The
    // read is still re-issued on every entry where none is in flight, so a screen never renders stale
    // groups, and the explicit re-reads after a role-group write are untouched.
    //
    // Deliberately narrower than a load-once flag, which was considered and refused: the group list is
    // reference data a selector must show CURRENT, so remembering that it was once read would trade a
    // measured duplicate for an unmeasurable staleness window.
    //
    // ⚠ THE TEST IS THE LOADING SIGNAL AND NOT THIS METHOD'S OWN HANDLE, and testing the handle was the
    // first attempt at this and was WRONG. Two different methods here read the group list: this one, whose
    // handle is `roleGroupsRequest`, and {@link RoleStore.loadRoleAdministration}, which reads the groups as
    // the HEAD of a chain and holds the whole chain on `rolesRequest` so that cancelling it abandons
    // whichever half is outstanding. A handle test therefore cannot see the read the listing screen has in
    // flight, which is precisely the pair a route change from the listing to the form produces - so the
    // guard would have looked correct and prevented nothing on the one path that was measured. The loading
    // signal is the only fact BOTH strategies maintain: each raises it before reading and each lowers it in
    // a `finalize`, which runs on a value, an error and an unsubscription alike.
    if (this._roleGroupsLoading()) {
      return;
    }

    this._roleGroupsLoading.set(true);
    this.clearFailure();

    // ⚠ THE HANDLE IS RELEASED HERE, AND THE GUARD ABOVE IS UNSOUND WITHOUT IT. `finalize` runs on every
    // ending - a value, an error, and an unsubscription - so nulling it there is what makes "a read is in
    // flight" a question the handle can actually answer. Left set, a completed subscription would read as
    // in-flight for the rest of the session and the guard above would refuse every later read, turning a
    // duplicate-request fix into a screen that never refreshes its group list. The order matters too: the
    // handle is cleared alongside the loading flag, in the one place that is reached however the read ends.
    this.roleGroupsRequest = this.roleService
      .listRoleGroups()
      .pipe(
        finalize(() => {
          this.roleGroupsRequest = null;
          this._roleGroupsLoading.set(false);
        }),
      )
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
   * Reads the roles ONE ACCOUNT holds, making that account the subject of the listing.
   *
   * MIGRATION: THIS RESTORES A PER-ACCOUNT VIEW THE TARGET HAD LOST. The legacy account listing's
   * roles command navigated to a per-account screen — `Users.ascx.vb:L542` built
   * `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD")` — and the screen it reached served TWO
   * MODES from one page, keyed by either a role or an account (`SecurityRoles.ascx.vb:L413-L418`).
   * The target's role listing had only the role-keyed mode, so the command discarded the row's
   * account and landed on the unnarrowed listing: an operator who asked "what does this person
   * hold" was shown every role in the tenant instead, with the account they had chosen nowhere on
   * screen.
   *
   * ⚠ THE ANSWER LANDS IN ITS OWN SLICE and never in {@link RoleStore.roles}. The browsable
   * listing is paged, ordered and filterable, and a screen may be showing it; this is an unpaged
   * answer to a different question. See {@link RoleStore._rolesHeldByUser}.
   *
   * The read is UNPAGED because the server declares it so. No page coordinate is sent and none is
   * published, so no pager can be built over the result.
   *
   * @param userId The account whose roles to read. Forwarded exactly as supplied — an account
   * identifier of nought is a real account.
   */
  loadRolesHeldByUser(userId: number): void {
    this.heldRolesRequest?.unsubscribe();
    this._heldRolesLoading.set(true);
    this.clearFailure();

    // ⚠ A CHANGE OF SUBJECT DISCARDS THE PREVIOUS ANSWER, and this is the whole reason the two
    // slices are written together here. The account is recorded at DISPATCH rather than on arrival,
    // so a screen can tell which account is being waited for and not merely which one was last
    // answered — but that alone left a window in which the recorded subject was the NEW account
    // while the collection still described the OLD one. A consumer comparing the two found them in
    // agreement and rendered the previous account's memberships, and its count, under the new
    // account's name: a wrong answer presented as a right one, for the duration of a request.
    //
    // Re-reading the SAME account keeps its answer, so a refresh does not blank the screen it is
    // refreshing. Only a genuine change of subject discards.
    if (this._heldRolesUserId() !== userId) {
      this._rolesHeldByUser.set(null);
    }

    this._heldRolesUserId.set(userId);

    this.heldRolesRequest = this.roleService
      .listRolesHeldByUser(userId)
      .pipe(finalize(() => this._heldRolesLoading.set(false)))
      .subscribe({
        // An empty payload is a successful answer meaning the account holds no role. It is NOT the
        // same fact as no account being the subject, which is the null the slice opens in.
        next: (response) => {
          this._rolesHeldByUser.set(response.data);
        },
        error: (error: unknown) => {
          // ⚠ THE SUBJECT IS KEPT ON FAILURE, AND CLEARING IT WAS THE DEFECT. The collection is
          // discarded because there is no answer, but the ACCOUNT is still what the listing is about:
          // the address still names it and the heading still says so. Clearing the subject made the
          // state claim no account was being asked about, which is how a consumer comparing the two
          // slices came to treat a refused narrowing as an ordinary unnarrowed listing.
          //
          // ⚠ WHAT THAT PRODUCED, measured against `/roles?userId=999`: the read answers `404`
          // ("Portal -1 has no member bearing identifier 999"), the narrowing was abandoned, and the
          // screen rendered ALL THREE roles under the subtitle "Roles held by account 999" - asserting
          // in writing that a non-existent account holds every role in the tenant. The failure was
          // recorded here but the listing did not surface this operation, so nothing contradicted it.
          //
          // The original reasoning - that an empty set would read as "this account holds nothing" - is
          // sound and is honoured differently: the consumer distinguishes a REFUSED read from an empty
          // successful one and renders neither the memberships nor the unnarrowed listing, showing the
          // reported failure instead. Absence of an answer is now expressed as absence rather than
          // borrowed from a different question.
          this._rolesHeldByUser.set(null);
          this.recordFailure('loadRolesHeldByUser', error);
        },
      });
  }

  /**
   * Stops treating any account as the subject, returning to the unnarrowed listing.
   *
   * Cancels a read in flight as well as discarding the answer, because a response arriving after
   * the narrowing was cleared would re-narrow the listing with no command to explain it.
   */
  clearRolesHeldByUser(): void {
    this.heldRolesRequest?.unsubscribe();
    this.heldRolesRequest = null;
    this._rolesHeldByUser.set(null);
    this._heldRolesUserId.set(undefined);
    this._heldRolesLoading.set(false);

    // ⚠ THE NARROWED READ'S FAILURE GOES WITH THE NARROWING, and this became necessary the moment
    // that failure started being surfaced. Measured: from `/roles?userId=999` - refused with `404`
    // "Portal -1 has no member bearing identifier 999" - pressing the on-screen affordance that
    // returns to the unnarrowed listing left the warning standing above a correct three-row listing,
    // stable across four seconds of sampling. That path re-renders retained rows and starts no new
    // read, so nothing else would ever clear the slot; the operator was left with a refusal about an
    // account the screen is no longer about.
    //
    // ⚠ CONDITIONAL, NOT A BLANKET CLEAR. One slot serves the whole store, so clearing it outright
    // here would silently discard an unrelated refusal - a rejected group rename, say - merely
    // because the reader widened the listing. Only the failure this command actually invalidates is
    // released.
    if (this._failure()?.operation === 'loadRolesHeldByUser') {
      this.clearFailure();
    }
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
    this.selectedRoleRequest?.unsubscribe();
    this._selectedRoleLoading.set(true);
    this.clearFailure();

    this.selectedRoleRequest = this.roleService
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
   * Reads ONE PAGE of assignments for one role.
   *
   * Legacy: `Website/admin/Security/SecurityRoles.ascx.vb`, which listed the accounts
   * holding a role together with the effective and expiry bounds of each assignment.
   * Those bounds are absolute instants on the wire and are held exactly as received; this
   * module neither reconstructs them from a local offset nor derives them.
   *
   * ⚠ EXACTLY ONE REQUEST PER READ, AND THE PAGE IS THE UNIT. The endpoint counts and windows
   * per request, so the page in hand plus the total on its metadata is everything a pager
   * needs, and {@link RoleStore.setAssignmentsPage} reaches every other page. An earlier
   * revision instead read page zero and then fetched every page the metadata reported,
   * together, and joined them: one screen became a burst of concurrent requests, the whole
   * membership of a role was retained in memory, and every assignment write repeated the walk.
   * That is why this method exists in one shape only. Completeness is delivered by the pager,
   * which keeps every membership addressable — the legacy grid declared no pager
   * (`Website/admin/Security/securityroles.ascx:L56`), so a role small enough to have fitted in
   * it renders with no pager at all and looks exactly as it did.
   *
   * MIGRATION: a change of addressed role returns the coordinate to the FIRST page. The page
   * an operator was standing on belongs to the role they were looking at; carrying its index
   * onto a different role would request a window that role may not have and present an empty
   * grid for a role that has members. Nothing is clamped or corrected here — see the paging
   * note on {@link RoleStore.setAssignmentsPage}; a fresh address simply gets a fresh
   * coordinate.
   *
   * @param roleId The role whose assignments to read.
   */
  loadAssignments(roleId: number): void {
    // The in-flight read is released by {@link RoleStore.dispatchAssignments}, which has to own
    // that release anyway because the corrective read needs it too. Releasing it here as well
    // would be a second unsubscribe of the same handle for no gain.
    //
    // ⚠ A CHANGE OF ROLE DISCARDS THE ROWS IN HAND, AND DOES SO NOW RATHER THAN ON ARRIVAL. The
    // addressed role is published immediately, so leaving the previous role's memberships in place
    // would publish them UNDER THE NEW ROLE for as long as the read takes - a consumer that checks
    // {@link RoleStore.assignmentsRoleId} before rendering, which is the correct check, would be
    // told the rows belong to a role they do not. A RE-READ of the same role keeps its rows, so a
    // refresh after a write does not blank the grid it is refreshing.
    if (this._assignmentsRoleId() !== roleId) {
      this._assignments.set(emptyPagedResult<UserRole>());
      this._assignmentsPage.set(INITIAL_PAGE_COORDINATE);
    }

    this._assignmentsRoleId.set(roleId);

    this.clearFailure();

    this.dispatchAssignments(roleId);
  }

  /**
   * Issues one page read for a role's memberships, correcting a coordinate left past the end.
   *
   * ⚠ WHY A CORRECTION IS NEEDED AT ALL, AND WHY ONLY HERE. A REMOVAL CAN STRAND THE OPERATOR.
   * Take the only member of the last page away and the coordinate they are standing on stops
   * existing: the write's re-read asks for it again, the server answers an empty page whose
   * metadata still reports the true total, and the grid renders nothing. Worse, the pager is
   * drawn on `pageSize < totalCount` — eleven members at ten a page becomes ten at ten a page,
   * that predicate turns false, and the pager is WITHDRAWN. The operator is left on a blank
   * grid with no affordance back to the rows that are still there. An unpaged grid could not
   * reach that state, which is why the correction arrived with the paging and belongs with it.
   *
   * ⚠ THIS IS NOT A REINTERPRETATION OF WHAT A CALLER ASKED FOR, and the distinction is the
   * whole reason it is safe. {@link RoleStore.setAssignmentsPage} still sends the index it was
   * given, untouched — an index past the last page is a real state of the world and the server
   * is entitled to answer it. What is acted on here is the SERVER'S OWN ANSWER: an empty window
   * beyond a positive total is the server saying the coordinate no longer names anything.
   *
   * The correction is BOUNDED BY CONSTRUCTION. The corrective read is issued with correction
   * withheld, so a listing that keeps shrinking underneath the screen costs at most one extra
   * request per read and can never loop. The step-back target comes from the server's own
   * reported page count, never from arithmetic over the rows in hand.
   *
   * @param roleId The role to read, forwarded exactly as supplied. Role zero is real.
   * @param correctionAllowed Whether a past-the-end answer may issue one corrective read.
   * `false` on the corrective read itself, which is what makes the recursion terminate.
   */
  private dispatchAssignments(roleId: number, correctionAllowed = true): void {
    this.assignmentsRequest?.unsubscribe();
    this._assignmentsLoading.set(true);

    // The loading flag is lowered EXPLICITLY rather than through `finalize`, because a
    // corrective read is dispatched from inside the first read's `next` and a finaliser runs
    // after it — so the first read's teardown would lower the flag while its own correction was
    // still in flight, and the grid would report itself at rest with a request outstanding.
    this.assignmentsRequest = this.roleService.listUsers(roleId, this._assignmentsPage()).subscribe({
      next: (response) => {
        // The record type is named explicitly: the framing decoder validates FRAMING only and
        // is generic in the record, so nothing infers it once the page is held in a local
        // rather than passed straight into the slice's setter.
        const page: PagedResult<UserRole> = toPagedResult<UserRole>(response);
        const requestedPageIndex: number = this._assignmentsPage().pageIndex;

        // Every clause is load-bearing. A positive total is what separates "this window is
        // past the end" from "this role has no members", which is a legitimate empty answer
        // and must not provoke a second request. A first-page request is never past the end,
        // whatever the total. And the server's page count must actually fall short of the
        // index asked for, so a server reporting a window that does exist is believed.
        const lastExistingPageIndex = page.meta.totalPages - 1;
        const pastTheEnd =
          correctionAllowed &&
          page.items.length === 0 &&
          page.meta.totalCount > 0 &&
          requestedPageIndex > 0 &&
          lastExistingPageIndex < requestedPageIndex;

        if (pastTheEnd) {
          this._assignmentsPage.update((coordinate) => ({
            ...coordinate,
            // Never negative: this arm is only reached with a positive total, so the server
            // reported at least one page. The floor is stated rather than assumed because the
            // alternative is a negative index on the wire.
            pageIndex: lastExistingPageIndex > 0 ? lastExistingPageIndex : 0,
          }));

          this.dispatchAssignments(roleId, false);
          return;
        }

        this._assignments.set(page);
        this._assignmentsLoading.set(false);
      },
      error: (error: unknown) => {
        this._assignmentsLoading.set(false);
        this.recordFailure('loadAssignments', error);
      },
    });
  }

  /**
   * Asks whether ONE account holds one role, and on what terms.
   *
   * Legacy: `SecurityRoles.ascx.vb:L273-L303` (`GetDates`) and `:L656-L658`, which answered
   * two questions from the grid it had already bound — what bounds to show for the chosen
   * account, and whether to relabel the action 'Update User Role'. That grid held the WHOLE
   * membership, because the legacy screen was unpaged, so scanning it answered both questions
   * for any member of the role.
   *
   * ⚠ THIS IS THE REPLACEMENT FOR THAT SCAN, AND IT IS WHY THE LISTING NEED NOT BE READ WHOLE.
   * A paged grid holds one window, so a scan of the rows on screen would answer "no membership"
   * for an account whose row happens to sit on another page — a behavioural regression against
   * the legacy answer. One exact request settles the question instead:
   * `GET /api/v1/roles/{roleId}/users/{userId}` addresses the pairing itself, so the answer
   * cannot depend on where a row falls and no filtering or matching happens on this side at all.
   *
   * ⚠ THE ADDRESS IS TWO IDENTIFIERS AND THE PREVIOUS ONE WAS A NAME, WHICH IS THE WHOLE REASON
   * THIS CHANGED. The probe used to narrow the membership LISTING by the account's login name,
   * carried in the paging contract's free-text filter — and the server matches that filter
   * against the login name and the display name, so the name had to be there for the request to
   * work. A query string is the least private part of a request: it is kept in browser history,
   * written in full to every forward and reverse proxy's access log and to the server's own, and
   * forwarded in the referrer of any subsequent navigation. None of those recorders is on the
   * wire, so transport encryption does not address them; this is CWE-598, and it stood beside an
   * account search that had already been moved to a request body to avoid exactly it. Two opaque
   * numeric identifiers in a path identify nobody, so the request stays a cacheable `GET` and
   * needs no compensating body.
   *
   * The answer lands on {@link RoleStore.probedAssignment} beside the pairing it belongs to on
   * {@link RoleStore.probedAssignmentKey}, and the key is published BEFORE the request so a
   * consumer can see which pairing is being asked about while the answer is outstanding.
   *
   * ⚠ A `404` IS THE ANSWER "HOLDS NOTHING" AND IS NOT RECORDED AS A FAILURE. The endpoint
   * answers `200` with the membership or `404` when the account holds none, so the refusal IS
   * the negative answer — the state the legacy screen showed by blanking its date box (`:L484`)
   * — and reporting it as a failure would put a banner on an ordinary outcome. Every OTHER
   * status still records a failure under this command's own operation name, so a screen can tell
   * a genuinely failed probe from a settled "holds nothing", and neither is ever mistaken for a
   * failed listing. The write behind the screen is an upsert in either case, so the server
   * settles which of the two it performs.
   *
   * @param roleId The role to ask about, forwarded exactly as supplied. Role zero is real.
   * @param userId The account to ask about, forwarded exactly as supplied.
   */
  probeAssignment(roleId: number, userId: number): void {
    this.assignmentProbeRequest?.unsubscribe();

    // The answer in hand is discarded as soon as a different pairing is asked about, rather than
    // on arrival, so a consumer gated on the key cannot briefly read one account's membership
    // under another account's name.
    this._probedAssignment.set(null);
    this._probedAssignmentKey.set({ roleId, userId });
    this._assignmentProbeLoading.set(true);
    this.clearFailure();

    this.assignmentProbeRequest = this.roleService
      .getMembership(roleId, userId)
      .pipe(finalize(() => this._assignmentProbeLoading.set(false)))
      .subscribe({
        next: (response) => {
          this._probedAssignment.set(response.data);
        },
        error: (error: unknown) => {
          this._probedAssignment.set(null);

          // Read structurally through the shared helper, which is the same one severity is
          // derived from. A 404 here means the pairing holds nothing, which is an answer; it is
          // NOT swallowed for any other status, and it is not swallowed in the transport either,
          // because only a caller knows whether absence is a legitimate outcome of its question.
          if (readStatus(error) === 404) {
            return;
          }

          this.recordFailure('probeAssignment', error);
        },
      });
  }

  /**
   * Forgets the probe's answer without contacting the server.
   *
   * The key is cleared alongside it, so the state becomes "no probe has answered" rather than
   * "the pairing holds nothing" — which is the distinction {@link AssignmentProbeKey} exists
   * for.
   */
  clearProbedAssignment(): void {
    this.assignmentProbeRequest?.unsubscribe();
    this.assignmentProbeRequest = null;
    this._assignmentProbeLoading.set(false);
    this._probedAssignment.set(null);
    this._probedAssignmentKey.set(null);
  }

  /**
   * Re-reads a role's assignments unless some OTHER role is the one in scope.
   *
   * ## The defect this closes
   *
   * Both assignment writes used to re-read UNCONDITIONALLY, and one of them additionally moved the
   * scope onto the role it was writing before doing so. Because this store is provided at the
   * application root, that produced a cross-role republication with no error anywhere:
   *
   *     membership screen opens role A, enrols an account       -> write A dispatched
   *     operator navigates to role B; the screen re-reads       -> read B dispatched
   *     write A settles                                         -> scope FORCED back to A,
   *                                                                read B CANCELLED,
   *                                                                read A dispatched
   *     read A lands                                            -> role A's members rendered
   *                                                                under role B's heading
   *
   * The consequences compound rather than stopping at a stale grid. The membership rows carry named
   * accounts, so one role's membership was disclosed under another role's name; and the removal
   * affordance on each row submits with the ROUTE's role key, so removing a row the operator could
   * see would have stripped an account from role B that only ever belonged to role A. Cancelling
   * read B is what made it silent — a cancelled read delivers neither a value nor an error.
   *
   * ## Two questions, asked separately
   *
   * The question is not "is this the newest read" — that is the read handle's job and it already
   * supersedes correctly — but "would refreshing this role write into something that is not mine".
   * Two independent facts answer it, and the first version of this guard asked only one of them.
   *
   * WHO IS LOOKING is {@link RoleStore._assignmentsView}, maintained by the screen itself. WHOSE
   * ROWS ARE HELD is {@link RoleStore.assignmentsRoleId}. They are not interchangeable: the rows'
   * label is only ever moved to another role, never vacated, because a departing screen does not
   * blank the grid on its way out. So after the operator returns to the role listing the label still
   * names the role they left, and a guard reading only the label is told the screen is still there.
   * That was the defect, and runtime validation reproduced it three times out of three on the
   * ordinary operator path. See {@link AssignmentsViewState}.
   *
   * The three refusals are in the body, each against the fact that justifies it. Two cases refresh:
   *
   * - a listing for THIS role is on screen — the ordinary path, where the operator is looking at the
   *   very grid the write changed and must see the change;
   * - nothing has been on screen at all, with no rows held — nobody is looking, so there is no set to
   *   overwrite and no heading to render rows under. Refreshing here ADOPTS the role, which is the
   *   published contract of both writes: a caller that enrols an account and then reads
   *   {@link RoleStore.assignmentItems} sees the enrolment, and {@link RoleStore.assignmentsRoleId}
   *   names the role it belongs to. Suppressing this case would leave the write with no observable
   *   effect whatsoever on a fresh store.
   *
   * A blanket equality test — refresh only when the scope ALREADY names this role — was tried first.
   * It closed the cross-role hazard but broke that contract, and the four specifications pinning the
   * contract failed as one. It is recorded because the mistake is easy to repeat: the cross-role
   * hazard is created by MOVING an occupied scope or by a screen that has LEFT, never by filling an
   * empty one.
   *
   * @param roleId The role the settled write addressed.
   */
  private refreshAssignmentsIfCurrent(roleId: number): void {
    const view: AssignmentsViewState = this._assignmentsView();

    // ⚠ (1) THE SCREEN THAT ASKED HAS GONE. Runtime validation caught this case, and it is the one
    // the scope test below cannot see: leaving a membership listing does not blank the rows, so the
    // scope still names the role afterwards and agreed with the write. Reproduced three times out of
    // three by enrolling an account and returning to the role listing before the write settled — the
    // ordinary path from one role's memberships to another's, since the listing is the stop between
    // them. There is no grid left to refresh, and the re-read would clear the shared failure slot
    // beneath whichever screen replaced it.
    if (view.kind === 'left') {
      return;
    }

    // (2) A DIFFERENT ROLE IS ON SCREEN. Distinct from (3) rather than implied by it: a listing that
    // has announced itself but whose first read has not yet dispatched leaves the scope empty, and
    // (3) would read that emptiness as "nobody is looking" and adopt the written role underneath it.
    if (view.kind === 'open' && view.roleId !== roleId) {
      return;
    }

    // (3) THE ROWS IN HAND BELONG TO A DIFFERENT ROLE. Refreshing would force the scope back, cancel
    // the read of the role actually being held, and republish this role's named members under the
    // other role's heading — silently, because a cancelled read delivers neither a value nor an
    // error. Kept as its own clause because it is answerable without any screen at all, which is how
    // a caller driving the store directly is protected.
    //
    // ⚠ COMPARED WITH STRICT INEQUALITY, AND ABSENCE IS TESTED EXPLICITLY. Role keys are
    // `IDENTITY(0, 1)`, so role 0 is a real role — the Administrators role of a fresh tenant — and a
    // truthiness test would read it as "nothing in scope" and take the adoption branch on a scope
    // that is genuinely occupied, reintroducing the defect for exactly one role.
    const inScope: number | null = this._assignmentsRoleId();

    if (inScope !== null && inScope !== roleId) {
      return;
    }

    // ⚠ THE PAGE IN HAND IS RE-READ, NOT THE FIRST PAGE. `loadAssignments` keeps the coordinate
    // for a role it is already showing, so an enrolment or a removal refreshes the window the
    // operator is standing on rather than throwing them back to page one. The whole-set re-read this
    // used to perform is gone with the walk itself: reading every page of a role's membership on
    // every write retained the entire set and repeated the read for each one, and the shared pager
    // now reaches the pages this window does not hold.
    this.loadAssignments(roleId);
  }

  /**
   * Declares that a membership listing for one role is now on screen.
   *
   * Called by the membership screen as soon as it knows which role it addresses, and again with the
   * new role when one instance is reused across a change of route parameter. Announcing the
   * replacement is all that is needed in that case — there is no matching close, because the screen
   * never left.
   *
   * Only {@link RoleStore.refreshAssignmentsIfCurrent} consumes this. Nothing rendered depends on
   * it, and it deliberately does not touch the rows, the scope, the failure slot or any read: it
   * records who is looking, and answering that question must not itself change what is being looked
   * at.
   *
   * @param roleId The role the listing on screen addresses.
   */
  openAssignmentsView(roleId: number): void {
    this._assignmentsView.set({ kind: 'open', roleId });
  }

  /**
   * Declares that the membership listing for one role has gone.
   *
   * Called from the screen's destruction hook. After this, a membership write that settles late
   * refreshes nothing — which is the point: the grid it would have refreshed no longer exists, and
   * the re-read would clear the shared failure slot underneath whichever screen replaced it.
   *
   * ⚠ THE ROLE IS PASSED AND CHECKED, rather than the state simply being cleared. A screen tears
   * itself down, and a replacement announces itself, in an order this store does not control. Were
   * the close unconditional, a replacement that had already called
   * {@link RoleStore.openAssignmentsView} would be un-announced by its predecessor's teardown, and
   * the replacement's own legitimate refresh would then be refused. Checking the role makes the
   * close idempotent and order-independent.
   *
   * Leaves `none` alone as well, so a screen that never resolved a role — and therefore never
   * announced one — cannot put the store into `left` and suppress the adoption contract for
   * everything that follows.
   *
   * @param roleId The role the departing listing addressed.
   */
  closeAssignmentsView(roleId: number): void {
    const view = this._assignmentsView();

    if (view.kind !== 'open' || view.roleId !== roleId) {
      return;
    }

    this._assignmentsView.set(ASSIGNMENTS_VIEW_LEFT);
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
   * Adopts a narrowing and a page together WITHOUT reading anything.
   *
   * ⚠ THIS COMMAND DISPATCHES NOTHING, WHICH IS THE WHOLE POINT OF IT, AND A CALLER MUST FOLLOW IT WITH
   * A READ. Every other command on this store couples a state change to a read, which is right when the
   * change originates in an affordance the operator just used. It is wrong when the change originates in
   * the ADDRESS: the listing screen keeps its page and its narrowing in the address so that a reload, a
   * bookmark and the browser's back and forward buttons all reproduce what was on screen, and on entry it
   * therefore restores BOTH coordinates at once. Restoring them through {@link setGroupFilter} and
   * {@link setRolesPage} would issue two reads for one view, and the first of them would be for the wrong
   * page, because a change of narrowing correctly returns to the first page and would discard the page the
   * address had just asked for.
   *
   * Separating the change from the read also lets the caller choose WHICH read follows, and the two are
   * materially different. On entry the groups are not in hand, so the caller reads
   * {@link loadRoleAdministration}, which fetches the groups and the listing as one chain. On a later
   * address change the groups are already held and unaffected by a page turn, so the caller reads
   * {@link loadRoles} alone — which is what keeps a page change to the single request it was measured down
   * to, rather than returning it to the pair it used to cost.
   *
   * THE PAGE IS NOT RESET HERE, and that is the difference from {@link setGroupFilter}. The address states
   * a narrowing and a page as one fact and both are honoured exactly as given; whoever WROTE that address
   * is the party that decided whether a new narrowing should return to the first page.
   *
   * MIGRATION: no legacy counterpart, because the legacy screen had no separation to make. Its narrowing
   * dropdown posted the whole page back and rebound the grid in one round trip
   * (`Roles.ascx.vb:L273-L278`), so state and read were inseparable by construction.
   *
   * THE ORDERING IS STAGED HERE TOO, FOR THE SAME REASON THE PAGE IS. It is a third coordinate the address
   * states, so restoring it through {@link setRolesSort} would issue a read AND reset the page index the
   * address had just asked for — the identical pair of faults the paragraph above describes. Both ordering
   * members are stored exactly as given; the caller that read them out of the address is the party that
   * validated them against what the endpoint accepts.
   *
   * @param filter The narrowing to adopt.
   * @param pageIndex The page to adopt, counted from zero. Stored exactly as supplied; nothing here
   * clamps it against a total this store may not yet know.
   * @param sortBy The column to order by, or `null` to accept the endpoint's default ordering.
   * @param sortDir The direction, or `null`. Meaningful only alongside `sortBy`.
   */
  stageListQuery(
    filter: RoleGroupFilter,
    pageIndex: number,
    sortBy: string | null = null,
    sortDir: SortDirection | null = null,
  ): void {
    this._groupFilter.set(filter);
    this._rolesPage.update((coordinate) => ({ ...coordinate, pageIndex, sortBy, sortDir }));
  }

  /**
   * Moves the role listing to a page and re-reads it.
   *
   * ⚠ THIS COMMAND USED NOT TO EXIST, AND ITS ABSENCE WAS DOCUMENTED AS DELIBERATE. The reasoning was
   * sound for the shape it described: the listing was read WHOLE, so a command that moved it to a page
   * could not have been honoured by the read behind it, and publishing one would have invited a caller
   * to ask for a page, believe it received one, and lose every role beyond that window. What changed is
   * the read, not the reasoning — see {@link ROLES_PAGE_SIZE} for the measurements that retired the
   * walk. The command exists now because the read behind it genuinely is windowed.
   *
   * The index is stored and sent EXACTLY as supplied. Nothing here clamps it against the total,
   * corrects it or reinterprets it: roles can be deleted between a page being requested and rendered,
   * so an index past the last page is a real state of the world rather than a caller's mistake. The
   * server answers it with an empty page whose metadata still reports the true total, and
   * {@link RoleStore.dispatchRoles} acts on THAT answer. The shared pager only ever emits an index
   * inside the range it was told about, so the arithmetic has one home and it is not this one.
   *
   * The read it dispatches cancels whichever page read was in flight, so clicking through the pager
   * cannot leave an earlier page's answer to land on top of a later one.
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
   * The index is stored and sent EXACTLY as supplied. Nothing here clamps it against the
   * total, corrects it or reinterprets it: records can be removed between a page being
   * requested and rendered, so an index past the last page is a real state of the world
   * rather than a caller's mistake, and the server answers it with an empty page whose
   * metadata still reports the true total. The shared pager only ever emits an index inside
   * the range it was told about, so the arithmetic has one home and it is not this one.
   *
   * The read it dispatches cancels whichever page read was in flight, so clicking through the
   * pager cannot leave an earlier page's answer to land on top of a later one.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  setAssignmentsPage(pageIndex: number): void {
    this._assignmentsPage.update((coordinate) => ({ ...coordinate, pageIndex }));
    this.reloadAssignments();
  }

  /**
   * Re-orders the assignment listing and re-reads it from the first page.
   *
   * The first page for the same reason a page move re-reads at all: a record's page depends on the
   * ordering, so a coordinate measured under one order does not address the same records under another.
   *
   * The field name is the ENDPOINT's, not a column key: the membership listing orders by ACCOUNT fields,
   * bounded by `SortableFields.RoleUsers` in
   * `backend/src/DnnMigration.Application/Validation/SortableFields.cs`. That set deliberately excludes the
   * two assignment dates the projection carries, so a caller must not offer them; the exclusion is the
   * server's rule and this command does not restate it - it forwards whatever it is given, exactly as
   * {@link RoleStore.setRolesSort} does, and the boundary refuses an unpermitted name with a field-level
   * message rather than silently ignoring it.
   *
   * @param sortBy The account field to order by, or `null` for the server's own ordering.
   * @param sortDir The direction, or `null` for the server's default. Passing `null` for BOTH is how a
   * caller returns the listing to the order it arrived in.
   */
  setAssignmentsSort(sortBy: string | null, sortDir: SortDirection | null): void {
    this._assignmentsPage.update((coordinate) => ({
      ...coordinate,
      sortBy,
      sortDir,
      pageIndex: 0,
    }));
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
  createRole(request: CreateRoleRequest): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .createRole(request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'createRole', failure)))
        .subscribe({
          next: (response) => {
            this._selectedRole.set(response.data);
            // ⚠ THE LISTING IS DELIBERATELY NOT RE-READ HERE, and removing that read is a fix rather than an
            // omission. Runtime testing quantified the duplicate it caused: 2,466 B sent and 25,283 B decoded
            // per save, in two shapes depending on timing - a complete-and-discard, and a network abort, with
            // 36 aborted listing refetches in a single session. The cause is that this store re-read the
            // listing at the same moment the only caller of this command navigated TO the listing, which reads
            // itself from its own address on entry; the two raced and the loser was cancelled mid-flight.
            //
            // The listing is the sole owner of listing reads now that its page, narrowing and ordering live in
            // its address: it reads on entry and on every address change, so a caller arriving there always
            // sees authoritative rows and totals without this command asking for them too. The row patch above
            // covers the other direction - a listing that is still mounted sees the change immediately.
            //
            // ⚠ THE DELETE COMMAND KEEPS ITS RE-READ and must. It is reachable from the listing itself, where
            // no navigation follows and no address changes, so without it a removed row would stay on screen.
          },
          error: (error: unknown) => {
            failure = this.recordFailure('createRole', error);
          },
        }),
    );

    return mutationId;
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
  updateRole(roleId: number, request: UpdateRoleRequest): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .updateRole(roleId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'updateRole', failure)))
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

            // See the note on `createRole`: the listing owns its own reads, so re-reading here duplicated the
            // read the caller's navigation was about to issue and one of the two was cancelled in flight. The
            // row patch immediately above is what keeps a still-mounted listing correct.
          },
          error: (error: unknown) => {
            failure = this.recordFailure('updateRole', error);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Deletes a role, and re-reads the listing only when the caller asks for it.
   *
   * MIGRATION: for a ROLE, an empty success really does mean the row is gone — which is
   * exactly what makes {@link RoleStore.removeAssignment} the exception rather than the
   * rule, and why the two are not implemented alike. When the listing IS re-read it is read
   * rather than having the row spliced out, because removing a record shifts every page
   * after it.
   *
   * ⚠ `thenReadListing` IS REQUIRED, AND DELIBERATELY HAS NO DEFAULT. It is the same rule
   * the create and update commands follow — the listing owns listing reads, because its
   * page, narrowing and ordering live in its address and it reads itself on entry and on
   * every address change. This command therefore never DECIDES to read the listing; it only
   * carries out a decision the caller alone can make, and the caller states it at the call
   * site so a future one cannot inherit the wrong answer silently:
   *
   * - `false` when the caller navigates to the listing afterwards. The navigation's own
   *   read is authoritative, and asking here as well issued a second, identical read that
   *   the route teardown then cancelled mid-flight. Runtime measurement of the delete path
   *   recorded exactly that: two listing reads with different correlation ids, the first
   *   `net::ERR_ABORTED` after ~7 ms and the second returning the rows actually displayed.
   * - `true` when the caller REMAINS where it is. A row-level delete on the listing changes
   *   no address, so nothing else will refresh the grid and the removed row would stay on
   *   screen. The read also passes through the page dispatcher, which steps back a page when
   *   the removal emptied the one being viewed — a state a row-level delete can create and
   *   the unpaged listing never could.
   *
   * The selection is discarded only when it is the role that was deleted, compared by
   * identity, and that happens on both branches: it describes what is held, not what is
   * displayed.
   *
   * @param roleId The role to delete, forwarded exactly as supplied.
   * @param options Whether this command should re-read the listing once the delete succeeds.
   */
  deleteRole(roleId: number, options: { readonly thenReadListing: boolean }): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .deleteRole(roleId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'deleteRole', failure)))
        .subscribe({
          next: () => {
            const selected = this._selectedRole();

            if (selected !== null && selected.roleId === roleId) {
              this._selectedRole.set(null);
            }

            if (options.thenReadListing) {
              this.loadRoles();
            }
          },
          error: (error: unknown) => {
            failure = this.recordFailure('deleteRole', error);
          },
        }),
    );

    return mutationId;
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
  assignUser(roleId: number, request: RoleAssignmentRequest): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .assignUser(roleId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'assignUser', failure)))
        .subscribe({
          next: () => {
            // ⚠ NOT REFRESHED WHILE A DIFFERENT ROLE IS THE ONE IN SCOPE. See
            // {@link RoleStore.refreshAssignmentsIfCurrent} for the cross-role republication this
            // test prevents; the previous code re-read unconditionally, and additionally MOVED the
            // scope to this role first, which made the test it needed impossible to write.
            this.refreshAssignmentsIfCurrent(roleId);
          },
          error: (error: unknown) => {
            failure = this.recordFailure('assignUser', error);
          },
        }),
    );

    return mutationId;
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
  removeAssignment(roleId: number, userId: number): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .removeUser(roleId, userId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'removeAssignment', failure)))
        .subscribe({
          next: () => {
            // Deliberately a re-read and not a removal. See the note above: the row may
            // still exist, expired as of yesterday, and an empty success cannot say.
            //
            // And not while a DIFFERENT role is the one in scope, for the reason recorded on
            // {@link RoleStore.refreshAssignmentsIfCurrent}.
            this.refreshAssignmentsIfCurrent(roleId);
          },
          error: (error: unknown) => {
            failure = this.recordFailure('removeAssignment', error);
          },
        }),
    );

    return mutationId;
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
  createRoleGroup(request: CreateRoleGroupRequest): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .createRoleGroup(request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'createRoleGroup', failure)))
        .subscribe({
          next: () => {
            this.loadRoleGroups();
          },
          error: (error: unknown) => {
            failure = this.recordFailure('createRoleGroup', error);
          },
        }),
    );

    return mutationId;
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
  updateRoleGroup(roleGroupId: number, request: UpdateRoleGroupRequest): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .updateRoleGroup(roleGroupId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'updateRoleGroup', failure)))
        .subscribe({
          next: (response) => {
            const updated: RoleGroup = response.data;

            this._roleGroups.update((groups) =>
              groups.map((group) => (group.roleGroupId === updated.roleGroupId ? updated : group)),
            );
          },
          error: (error: unknown) => {
            failure = this.recordFailure('updateRoleGroup', error);
          },
        }),
    );

    return mutationId;
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
  deleteRoleGroup(roleGroupId: number): number {
    const mutationId = this.beginWrite();
    let failure: RoleStoreFailure | null = null;

    this.clearFailure();

    this.track(
      this.roleService
        .deleteRoleGroup(roleGroupId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'deleteRoleGroup', failure)))
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

            failure = this.recordFailure('deleteRoleGroup', error);
          },
        }),
    );

    return mutationId;
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
   * ⚠ IN-FLIGHT WORK IS ABANDONED FIRST, AND THAT ORDERING IS THE WHOLE POINT. This store is
   * registered at the application root, so it outlives every screen and every session and
   * nothing destroys it when an operator signs out. Clearing the slices without abandoning
   * the requests behind them would let a response that was already on the wire repopulate
   * exactly what the reset discarded — one operator's roles, role groups and member
   * assignments becoming visible to whoever signs in next, with a delay in front of it
   * instead of no delay at all. `core/state/session-lifecycle.service.ts` is what calls this,
   * on an explicit sign-out, on a terminal refusal, and whenever the identity or tenant
   * behind the session is replaced.
   *
   * Writes are released as well, because this is discarding the whole store rather than
   * superseding one request with another. Releasing a write handle stops this client
   * listening; it does not undo anything the server has already committed, and the reset
   * makes clear that nothing held here describes the new session either way.
   *
   * Not a cache eviction: there is no cache to evict. See MIGRATION note 14.
   *
   * ⚠ IN-FLIGHT WORK IS CANCELLED FIRST, reads and writes alike. Without that, a response
   * that was already on the wire when the session ended would land after the reset and
   * repopulate exactly what the reset had cleared — one operator's roles, groups and
   * assignments becoming visible to the next. `core/state/session-teardown.service.ts` calls
   * this member on every session boundary — reached from the bearer interceptor, from the
   * session store, and from `core/state/session-lifecycle.service.ts` on an explicit
   * sign-out — so it has to leave nothing listening.
   */
  reset(): void {
    this.cancelReads();
    this.cancelWrites();

    this._roles.set(emptyPagedResult<RoleListItem>());
    // The account-narrowed slice is a tenant-scoped, person-identifying answer, so it is discarded
    // with everything else: the next operator to sign in must not find the previous one's account
    // still the subject of the listing.
    this._rolesHeldByUser.set(null);
    this._heldRolesUserId.set(undefined);
    this._heldRolesLoading.set(false);
    this._roleGroups.set([]);
    this._groupFilter.set(DEFAULT_ROLE_GROUP_FILTER);
    this._selectedRole.set(null);
    this._assignments.set(emptyPagedResult<UserRole>());
    this._assignmentsRoleId.set(null);
    // Back to `none` rather than `left`: the session is over, so no screen from it is owed anything,
    // and leaving the store in `left` would suppress the adoption contract for the next session.
    this._assignmentsView.set(NO_ASSIGNMENTS_VIEW);
    this._rolesPage.set(INITIAL_ROLES_COORDINATE);
    this._assignmentsPage.set(INITIAL_PAGE_COORDINATE);
    this._probedAssignment.set(null);
    this._probedAssignmentKey.set(null);
    this._rolesLoading.set(false);
    this._roleGroupsLoading.set(false);
    this._selectedRoleLoading.set(false);
    this._assignmentsLoading.set(false);
    this._pendingWrites.set(0);
    this._mutation.set(null);
    this._failure.set(null);
  }

  /**
   * Releases every request handle when the injector holding this store is destroyed.
   *
   * A root-provided store lives as long as the application, so in production this runs on
   * teardown. It matters most in a specification, where each one builds its own injector and
   * a request left listening across that boundary would report into a store the next
   * specification has already replaced.
   */
  ngOnDestroy(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  // -------------------------------------------------------------------------
  // PRIVATE — THE COMPLETE-LISTING WALK AND THE REQUEST HANDLES
  // -------------------------------------------------------------------------



  /**
   * Holds a write's handle until it settles, so that teardown can release it.
   *
   * A write is never cancelled by a later request, so the handle is discarded when the write
   * FINISHES rather than when the next one starts, which is what keeps the set from growing
   * without bound. A handle that is already closed — which happens whenever a response
   * arrives synchronously, as it does under test — is not held at all.
   *
   * @param request The handle to hold.
   */
  private track(request: Subscription): void {
    if (request.closed) {
      return;
    }

    this.writeRequests.add(request);
    request.add(() => {
      this.writeRequests.delete(request);
    });
  }

  /**
   * Abandons every read in flight and forgets its handle.
   *
   * The loading flags come down with them, because a cancelled read never lowers its own and
   * a flag left raised presents as a screen that is permanently busy.
   */
  private cancelReads(): void {
    this.heldRolesRequest?.unsubscribe();
    this.heldRolesRequest = null;
    this.rolesRequest?.unsubscribe();
    this.rolesRequest = null;
    this.roleGroupsRequest?.unsubscribe();
    this.roleGroupsRequest = null;
    this.selectedRoleRequest?.unsubscribe();
    this.selectedRoleRequest = null;
    this.assignmentsRequest?.unsubscribe();
    this.assignmentsRequest = null;
    this.assignmentProbeRequest?.unsubscribe();
    this.assignmentProbeRequest = null;

    this._rolesLoading.set(false);
    this._roleGroupsLoading.set(false);
    this._selectedRoleLoading.set(false);
    this._assignmentsLoading.set(false);
    this._assignmentProbeLoading.set(false);
  }

  /**
   * Releases every write handle.
   *
   * Only for teardown and for a reset that is discarding the whole store. A write in flight is
   * not otherwise abandoned, because releasing the handle stops this client listening without
   * undoing anything the server may already have committed.
   */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();

    // The count is ZEROED rather than decremented, because releasing a handle does not run the
    // pipeline's `finalize` for a subscription that was already closed and a per-handle decrement
    // could therefore leave a residue. Anything that was outstanding is abandoned wholesale here, so
    // zero is the truth. The last settled result is deliberately NOT cleared: this member is reached
    // only from teardown and from a reset, and a reset clears it explicitly right after.
    this._pendingWrites.set(0);
  }

  /**
   * Issues the identifier for a write that is starting and records it as outstanding.
   *
   * A PRE-increment, so the first identifier ever issued is 1 and 0 is a value no write holds — which
   * lets a caller use 0 as "no write of mine is outstanding" without a nullable field.
   *
   * Call at DISPATCH, beside the request, and return the value to the caller. The count and the
   * identifier move together and only here, which is what keeps them consistent.
   *
   * @returns The identifier to return to the caller and to settle with.
   */
  private beginWrite(): number {
    this.nextMutationId += 1;
    this._pendingWrites.update((count) => count + 1);

    return this.nextMutationId;
  }

  /**
   * Records a write as finished and publishes its outcome under its own identifier.
   *
   * Called from `finalize`, which runs on completion, on failure AND on unsubscription — so the count
   * comes down on every path a write can leave by, and a screen waiting on this identifier is never
   * left waiting on a write that has already gone.
   *
   * ⚠ THE FAILURE IS PASSED IN, not read from the shared slot. `finalize` runs after the error
   * handler, so the handler hands its own record forward; re-reading {@link RoleStore.failure} here
   * would find whatever a concurrent write had most recently put there, or nothing if a concurrent
   * dispatch had just cleared it.
   *
   * @param id The identifier {@link RoleStore.beginWrite} issued.
   * @param operation Which command settled.
   * @param failure The refusal this write met, or null when it succeeded.
   */
  private settleWrite(
    id: number,
    operation: RoleStoreOperation,
    failure: RoleStoreFailure | null,
  ): void {
    // Floored at zero so a settlement that somehow arrived twice cannot drive the count negative and
    // leave `saving` reporting false while a write is still outstanding.
    this._pendingWrites.update((count) => Math.max(count - 1, 0));
    this._mutation.set({ id, operation, failure });
  }
}
