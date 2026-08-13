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
import type { Subscription } from 'rxjs';
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
 * Which roles the listing should consider, as three named intents. this replaces a single signed integer
 * that carried three meanings at once, and separating them is the whole point.
 */
export type RoleGroupFilter =
  /** Every role in the portal, whatever its grouping. */
  | { readonly kind: 'AllRoles' }
  /** Only the roles belonging to no group at all. */
  | { readonly kind: 'GlobalRoles' }
  /**
   * Only the roles in one named group. `roleGroupId` is a real key and may legitimately be `0`: the group
   * table is seeded `IDENTITY(0, 1)`, so the first group any portal creates has key zero.
   */
  | { readonly kind: 'Group'; readonly roleGroupId: number };

export const DEFAULT_ROLE_GROUP_FILTER: RoleGroupFilter = Object.freeze({
  kind: 'GlobalRoles',
});

/**
 * @param value The value no branch matched.
 * @returns Never returns; always throws.
 */
function assertUnreachable(value: never): never {
  throw new Error(`Unhandled variant in role state: ${JSON.stringify(value)}`);
}

// ---------------------------------------------------------------------------
// PAID-MEMBERSHIP TERMS
// ---------------------------------------------------------------------------

const LEGACY_ABSENT_PERIOD = -1;

/** Whether a role's paid-membership terms bound the membership in time, and how. */
export type BillingTermsBound =
  /** The terms set no expiry, so the membership does not lapse. */
  | 'Unbounded'
  /** The terms set a perpetual far-future expiry rather than none. */
  | 'Perpetual'
  /** The terms advance the expiry by a period, so the membership lapses. */
  | 'Bounded'
  /**
   * The stored code is outside the supported vocabulary, so the terms cannot be classified. ⚠ NOT AN
   * ERROR STATE, AND NOT A SYNONYM FOR `Unbounded`.
   */
  | 'Unsupported';

/**
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
      return 'Unsupported';
  }
}

// ---------------------------------------------------------------------------
// FAILURE STATE
// ---------------------------------------------------------------------------

/**
 * Which command failed, so a screen can attribute a failure without guessing. Named after the command
 * methods on {@link RoleStore} rather than after HTTP verbs, because a screen reacts to "the removal
 * failed", not to "a DELETE failed".
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
 * One failure, held structurally. A FORBIDDEN RESPONSE IS A WARNING, NOT AN ERROR, and the severity is
 * not decided here.
 */
export interface RoleStoreFailure {
  /** Which command failed. */
  readonly operation: RoleStoreOperation;

  /** The transport status, or `null` when the failure carried none at all. */
  readonly status: number | null;

  /** Severity, wording and per-field messages, produced by the module that owns them. */
  readonly summary: ProblemSummary;

  /**
   * The server's RFC 7807 document verbatim, or `null` when the failure carried none. Only ever a genuine
   * document.
   */
  readonly problem: ProblemDetails | null;

  /** The server's conflict code verbatim, or `null` when the failure was not a recognised conflict. */
  readonly conflict: ConflictCode | null;
}

/**
 * One settled write, identified so that the screen which started it can recognise it. ## The defect this
 * closes This store is provided at the application root, so the role listing, the role form and the
 * membership screen all share ONE instance and their writes overlap freely.
 */
export interface RoleMutation {
  /** The identifier the write command returned to its caller. */
  readonly id: number;

  /** Which command settled. Kept so a caller can assert the kind as well as the identity. */
  readonly operation: RoleStoreOperation;

  /**
   * The refusal this particular write met, or `null` when it succeeded. ⚠ CARRIED HERE RATHER THAN READ
   * FROM THE SHARED SLOT. {@link RoleStore.failure} holds the most recent failure of any command and is
   * cleared by the next dispatch, so a caller reading it after its own write settled could find a
   * concurrent write's refusal, or find nothing where its own refusal had been a moment earlier.
   */
  readonly failure: RoleStoreFailure | null;
}

// ---------------------------------------------------------------------------
// PAGE COORDINATES
// ---------------------------------------------------------------------------

/**
 * The page coordinate, ordering and free-text filter for one PAGED listing. Applies to the role listing
 * and the assignment listing.
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
 * The coordinate a freshly constructed store starts each paged listing at. The first page, at the shared
 * default size, unordered and unfiltered.
 */
const INITIAL_PAGE_COORDINATE: RolePageCoordinate = Object.freeze({
  pageIndex: 0,
  pageSize: DEFAULT_PAGE_SIZE,
  sortBy: null,
  sortDir: null,
  query: null,
});

/**
 * Which account's membership of which role one probe answered. The probe in {@link
 * RoleStore.probeAssignment} answers a question about ONE pairing, so its answer is only meaningful
 * beside the pairing it was asked about.
 */
export interface AssignmentProbeKey {
  /** The role the probe asked about. */
  readonly roleId: number;

  /** The account the probe asked about. */
  readonly userId: number;
}

/**
 * Whether a membership listing is on screen, and for which role. Distinct from {@link
 * RoleStore.assignmentsRoleId}, and the distinction is the whole reason the type exists.
 */
type AssignmentsViewState =
  | { readonly kind: 'none' }
  | { readonly kind: 'open'; readonly roleId: number }
  | { readonly kind: 'left' };

/** The state a store with no membership listing on screen begins in. */
const NO_ASSIGNMENTS_VIEW: AssignmentsViewState = Object.freeze({ kind: 'none' });

/** The state a store is in once the membership listing that was on screen has gone. */
const ASSIGNMENTS_VIEW_LEFT: AssignmentsViewState = Object.freeze({ kind: 'left' });

/**
 * The page size the ROLE listing reads with. ⚠ THE LISTING IS NOW GENUINELY PAGED, AND THE CHANGE
 * REPLACED A COMPLETE-LISTING WALK. This slice used to request page after page at the endpoint's maximum
 * of a hundred records and join them into one unpaged envelope, because the screen consuming it offered
 * no pager and a single window would have presented the first page AS the whole set.
 */
export const ROLES_PAGE_SIZE = DEFAULT_PAGE_SIZE;

/**
 * The coordinate the ROLE listing starts at. Identical in shape and size to {@link
 * INITIAL_PAGE_COORDINATE}, which seeds the ASSIGNMENT listing, and kept as its own constant so the two
 * listings can be re-seeded independently — a reset of one must not silently move the other.
 */
const INITIAL_ROLES_COORDINATE: RolePageCoordinate = Object.freeze({
  pageIndex: 0,
  pageSize: ROLES_PAGE_SIZE,
  sortBy: null,
  sortDir: null,
  query: null,
});

/**
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
    return null;
  }
}

/**
 * Recovers the transport status when the failure carried no problem document.
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
 * Holds and coordinates role-administration state. Registered at the root so one instance serves the
 * whole application; no component declares a provider for it, and no component may write to it.
 */
@Injectable({ providedIn: 'root' })
export class RoleStore implements OnDestroy {
  private readonly roleService = inject(RoleService);

  // REQUEST HANDLES
  // One handle per INDEPENDENT read, plus one set holding every write in flight. They exist so that a read
  // can be ABANDONED, which is what makes each slice a function of the latest request rather than of
  // whichever response happens to arrive last.

  /** The role listing read, whether the complete walk or a single request within it. */
  private rolesRequest: Subscription | null = null;

  /** The handle for the roles-held-by-one-account read. */
  private heldRolesRequest: Subscription | null = null;

  /** The role-group listing read. */
  private roleGroupsRequest: Subscription | null = null;

  /** The single-role read. */
  private selectedRoleRequest: Subscription | null = null;

  /** The assignment listing read. */
  private assignmentsRequest: Subscription | null = null;

  /** The single-account membership probe. */
  private assignmentProbeRequest: Subscription | null = null;

  /** Every write in flight. */
  private readonly writeRequests = new Set<Subscription>();

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private without exception
  // -------------------------------------------------------------------------

  /** The current page of the role listing. */
  private readonly _roles = signal<PagedResult<RoleListItem>>(emptyPagedResult<RoleListItem>());

  private readonly _roleGroups = signal<readonly RoleGroup[]>([]);

  /** The narrowing applied to the role listing. */
  private readonly _groupFilter = signal<RoleGroupFilter>(DEFAULT_ROLE_GROUP_FILTER);

  /** The role under edit, or `null` when none is selected. Absence is `null` and nothing else. */
  private readonly _selectedRole = signal<Role | null>(null);

  /** The current page of the assignment listing. */
  private readonly _assignments = signal<PagedResult<UserRole>>(emptyPagedResult<UserRole>());

  /** The role whose assignments {@link RoleStore.assignments} currently holds, or `null`. */
  private readonly _assignmentsRoleId = signal<number | null>(null);

  /** Whether a membership listing is on screen, and for which role. */
  private readonly _assignmentsView = signal<AssignmentsViewState>(NO_ASSIGNMENTS_VIEW);

  /**
   * The page, ordering, filter and request size the role listing was last read with. Every member is
   * live.
   */
  private readonly _rolesPage = signal<RolePageCoordinate>(INITIAL_ROLES_COORDINATE);

  /** The coordinate the assignment listing was last read at. */
  private readonly _assignmentsPage = signal<RolePageCoordinate>(INITIAL_PAGE_COORDINATE);

  /** The membership one probe found, or `null` when the probe found none. */
  private readonly _probedAssignment = signal<UserRole | null>(null);

  /** The pairing {@link RoleStore._probedAssignment} answers for, or `null` when none has. */
  private readonly _probedAssignmentKey = signal<AssignmentProbeKey | null>(null);

  private readonly _rolesLoading = signal<boolean>(false);

  /**
   * The roles ONE ACCOUNT holds, when the listing has been narrowed to an account. ⚠ HELD APART FROM
   * {@link RoleStore._roles}, NOT WRITTEN OVER IT. The browsable listing is paged, ordered and filterable
   * and a screen may be showing it; this is an unpaged answer to a different question.
   */
  private readonly _rolesHeldByUser = signal<readonly RoleListItem[] | null>(null);

  /** Which account {@link RoleStore._rolesHeldByUser} describes, or `undefined` when none. */
  private readonly _heldRolesUserId = signal<number | undefined>(undefined);

  /** Whether the roles-held-by-one-account read is in flight. */
  private readonly _heldRolesLoading = signal<boolean>(false);
  private readonly _roleGroupsLoading = signal<boolean>(false);
  private readonly _selectedRoleLoading = signal<boolean>(false);
  private readonly _assignmentsLoading = signal<boolean>(false);

  /**
   * How many writes are outstanding. ⚠ A COUNT RATHER THAN A FLAG, and the difference is a correctness
   * one. This store is provided at the application root, so several screens share it and their writes
   * overlap.
   */
  private readonly _pendingWrites = signal<number>(0);

  /**
   * The most recently settled write, identified. ⚠ THIS, NOT THE COUNT, IS WHAT A SCREEN SETTLES ON. See
   * {@link RoleStore.mutation}.
   */
  private readonly _mutation = signal<RoleMutation | null>(null);

  /**
   * The identifier issued to the last write dispatched. A plain counter rather than a signal: nothing
   * observes it, and it is read only to produce the next value.
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
   * The membership the last probe found, or `null` when it found none. Read it BESIDE {@link
   * RoleStore.probedAssignmentKey}: an answer is only about the pairing that key names, so a consumer
   * confirms the key matches the pairing it cares about before acting on the answer.
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
   * Whether ANY write is in flight, anywhere in the application. ⚠ AN AGGREGATE, AND IT MUST NOT BE USED
   * TO SETTLE A PARTICULAR WRITE. It is derived from the pending-write count, so it is now accurate under
   * overlap - it stays true until the LAST outstanding write settles rather than until the first one does
   * - but accurate is not the same as specific.
   */
  readonly saving = computed<boolean>(() => this._pendingWrites() > 0);

  /**
   * The most recently settled write, carrying the identifier its caller was given. ⚠ THE ONLY CORRECT WAY
   * TO SETTLE A WRITE. Every write command returns a number, and a caller that cares about the outcome
   * keeps it and compares it against `mutation()?.id`.
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

  readonly hasRoleGroups = computed<boolean>(() => this._roleGroups().length > 0);

  /** The group the narrowing names, or `null` when it names none. */
  readonly selectedRoleGroupId = computed<number | null>(() => {
    const filter = this._groupFilter();

    // The one place in this file where the narrowing's own variants are compared. These are the domain's
    // named intents, NOT sentinel identifiers, so this is not the truthiness-on-an-identifier pattern that
    // is forbidden elsewhere.
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
   * The group the narrowing names, resolved against the loaded groups, or `null`. `null` when the
   * narrowing names no group, and also when it names one that the loaded listing does not contain — which
   * is a real possibility while a read is in flight, or after another administrator removed the group.
   */
  readonly selectedRoleGroup = computed<RoleGroup | null>(() => {
    const roleGroupId = this.selectedRoleGroupId();

    if (roleGroupId === null) {
      return null;
    }

    return this._roleGroups().find((group) => group.roleGroupId === roleGroupId) ?? null;
  });

  /**
   * Whether a screen should OFFER to delete the group the narrowing names. Legacy:
   * `Website/admin/Security/Roles.ascx.vb:L79-L85`, which is two guards, not one: - `:L79-L81` hides both
   * the edit-group and the delete control whenever the narrowing is negative, because neither
   * pseudo-intent is a group you can act on.
   */
  readonly canDeleteSelectedGroup = computed<boolean>(() => {
    if (this.selectedRoleGroupId() === null) {
      return false;
    }

    return this._roles().items.length === 0;
  });

  /** How the selected role's billing terms bound the membership, or `null` if no role is selected. */
  readonly selectedRoleBillingTerms = computed<BillingTermsBound | null>(() => {
    const role = this._selectedRole();

    if (role === null) {
      return null;
    }

    return billingTermsBound(role.billingFrequency, role.billingPeriod);
  });

  /**
   * How the selected role's trial terms bound the trial, or `null` if no role is selected. WHICH SET OF
   * TERMS ACTUALLY GOVERNS AN ASSIGNMENT IS DECIDED SERVER-SIDE, and cannot be decided here.
   */
  readonly selectedRoleTrialTerms = computed<BillingTermsBound | null>(() => {
    const role = this._selectedRole();

    if (role === null) {
      return null;
    }

    return billingTermsBound(role.trialFrequency, role.trialPeriod);
  });

  /**
   * Whether the selected role charges a fee, or `null` if no role is selected. A FEE OF ZERO MEANS FREE,
   * AND IS REAL DATA. `RoleController.vb:L494` discriminates paid from free with a
   * strictly-greater-than-zero test on the fee, so zero falls on the free side deliberately.
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
   * Translates a narrowing intent into the two arguments the endpoint takes. THE ENDPOINT SEPARATES THE
   * CONCERNS THE LEGACY INTEGER CONFLATED. It takes a group key and a scope as two independent arguments,
   * so the key stays a plain key with no magic values and the intent no key can express is named instead.
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

  /** @param groups The groups just read from the server. */
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

    return failure;
  }

  /**
   * Discards the held failure, so a fresh command starts from a clean slate. ⚠ THE ASSIGNMENT IS
   * UNCONDITIONAL, AND THE GUARD IT REPLACES WAS ACTIVELY HARMFUL. Testing the slice first bought nothing
   * — a signal set to a value it already holds compares equal and notifies nobody — while the test itself
   * was a READ, and every command begins by calling this.
   */
  private clearFailure(): void {
    this._failure.set(null);
  }

  /**
   * Whether a failure is a recognised conflict, without recording it. Needed so a caller can decide to
   * refresh BEFORE recording, which is what keeps the refresh from erasing the failure that prompted it —
   * see {@link RoleStore.deleteRoleGroup}.
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
   * Reads the groups, then reads the roles for whatever narrowing survives. This is the sequence the
   * listing screen opens with, and the order is load-bearing rather than incidental: the groups decide
   * whether the narrowing row can be offered at all, and — per {@link RoleStore.applyNoGroupsFallback} —
   * an empty group listing overrides the narrowing before the roles are read.
   */
  loadRoleAdministration(): void {
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
          this._roles.set(toPagedResult<RoleListItem>(response));
        },
        error: (error: unknown) => {
          this.recordFailure(this._roleGroups().length === 0 ? 'loadRoleGroups' : 'loadRoles', error);
        },
      });
  }

  /**
   * Reads ONE PAGE of the role listing. Legacy: `Roles.ascx.vb:L72-L77`, whose two-armed query choice is
   * now the narrowing translation in {@link RoleStore.narrowingFor}. ⚠ EXACTLY ONE REQUEST PER READ, AND
   * THE PAGE IS THE UNIT. This replaced a complete-listing walk that requested page after page and joined
   * them — see {@link ROLES_PAGE_SIZE} for what that cost on a real tenant and why it went.
   */
  loadRoles(): void {
    this.rolesRequest?.unsubscribe();
    this.clearFailure();
    this.dispatchRoles();
  }

  /**
   * Issues one page read for the role listing, correcting a coordinate left past the end. ⚠ WHY A
   * CORRECTION IS NEEDED, AND WHY IT ARRIVED WITH THE PAGING. A DELETION CAN STRAND THE OPERATOR. Remove
   * the only role on the last page and the coordinate they are standing on stops existing: the write's
   * re-read asks for it again, the server answers an empty page whose metadata still reports the true
   * total, and the grid renders nothing.
   *
   * @param correctionAllowed Whether a past-the-end answer may issue one corrective read.
   */
  private dispatchRoles(correctionAllowed = true): void {
    this.rolesRequest?.unsubscribe();
    this._rolesLoading.set(true);

    // The loading flag is lowered EXPLICITLY rather than through `finalize`, for the reason given on the
    // assignment dispatcher: a corrective read is issued from inside the first read's `next`, and a
    // finaliser would run after it and report the grid at rest with a request still outstanding.
    this.rolesRequest = this.roleService
      .listRoles(this._rolesPage(), this.narrowingFor(this._groupFilter()))
      .subscribe({
        next: (response) => {
          const page: PagedResult<RoleListItem> = toPagedResult<RoleListItem>(response);
          const requestedPageIndex: number = this._rolesPage().pageIndex;
          const lastExistingPageIndex = page.meta.totalPages - 1;

          // Every clause is load-bearing. A positive total separates "this window is past the end" from
          // "this narrowing matches no roles", which is a legitimate empty answer that must not provoke a
          // second request.
          const pastTheEnd =
            correctionAllowed &&
            page.items.length === 0 &&
            page.meta.totalCount > 0 &&
            requestedPageIndex > 0 &&
            lastExistingPageIndex < requestedPageIndex;

          if (pastTheEnd) {
            this._rolesPage.update((coordinate) => ({
              ...coordinate,
              // Never negative: this arm is only reached with a positive total, so the server reported at
              // least one page. The floor is stated rather than assumed because the alternative is a
              // negative index on the wire.
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

  loadRoleGroups(): void {
    // Returning here is NOT a cache and introduces no staleness of any kind: a read IS in flight, it will
    // publish to the same slots this call would have published to, and the caller's need is met by it.
    if (this._roleGroupsLoading()) {
      return;
    }

    this._roleGroupsLoading.set(true);
    this.clearFailure();

    // ⚠ THE HANDLE IS RELEASED HERE, AND THE GUARD ABOVE IS UNSOUND WITHOUT IT. `finalize` runs on every
    // ending - a value, an error, and an unsubscription - so nulling it there is what makes "a read is in
    // flight" a question the handle can actually answer.
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
   * Reads the roles ONE ACCOUNT holds, making that account the subject of the listing. THIS RESTORES A
   * PER-ACCOUNT VIEW THE TARGET HAD LOST. The legacy account listing's roles command navigated to a
   * per-account screen — `Users.ascx.vb:L542` built `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD")`
   * — and the screen it reached served TWO MODES from one page, keyed by either a role or an account.
   *
   * @param userId The account whose roles to read.
   */
  loadRolesHeldByUser(userId: number): void {
    this.heldRolesRequest?.unsubscribe();
    this._heldRolesLoading.set(true);
    this.clearFailure();

    // ⚠ A CHANGE OF SUBJECT DISCARDS THE PREVIOUS ANSWER, and this is the whole reason the two slices are
    // written together here.
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
          // ⚠ THE SUBJECT IS KEPT ON FAILURE, AND CLEARING IT WOULD LOSE IT. The collection is discarded
          // because there is no answer, but the ACCOUNT is still what the listing is about: the address
          // still names it and the heading still says so.
          this._rolesHeldByUser.set(null);
          this.recordFailure('loadRolesHeldByUser', error);
        },
      });
  }

  /**
   * Stops treating any account as the subject, returning to the unnarrowed listing. Cancels a read in
   * flight as well as discarding the answer, because a response arriving after the narrowing was cleared
   * would re-narrow the listing with no command to explain it.
   */
  clearRolesHeldByUser(): void {
    this.heldRolesRequest?.unsubscribe();
    this.heldRolesRequest = null;
    this._rolesHeldByUser.set(null);
    this._heldRolesUserId.set(undefined);
    this._heldRolesLoading.set(false);

    // ⚠ THE NARROWED READ'S FAILURE GOES WITH THE NARROWING, and this became necessary the moment that
    // failure started being surfaced.
    if (this._failure()?.operation === 'loadRolesHeldByUser') {
      this.clearFailure();
    }
  }

  /**
   * Reads one role in full and holds it as the selection. Legacy: the edit screen
   * `Website/admin/Security/EditRoles.ascx.vb`, which read the role on entry.
   *
   * @param roleId The role to read, forwarded exactly as supplied.
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

  /** @param roleId The role whose assignments to read. */
  loadAssignments(roleId: number): void {
    // ⚠ A CHANGE OF ROLE DISCARDS THE ROWS IN HAND, AND DOES SO NOW RATHER THAN ON ARRIVAL. The addressed
    // role is published immediately, so leaving the previous role's memberships in place would publish them
    // UNDER THE NEW ROLE for as long as the read takes - a consumer that checks {@link
    // RoleStore.assignmentsRoleId} before rendering, which is the correct check, would be told the rows
    // belong to a role they do not.
    if (this._assignmentsRoleId() !== roleId) {
      this._assignments.set(emptyPagedResult<UserRole>());
      this._assignmentsPage.set(INITIAL_PAGE_COORDINATE);
    }

    this._assignmentsRoleId.set(roleId);

    this.clearFailure();

    this.dispatchAssignments(roleId);
  }

  /**
   * Issues one page read for a role's memberships, correcting a coordinate left past the end. ⚠ WHY A
   * CORRECTION IS NEEDED AT ALL, AND WHY ONLY HERE. A REMOVAL CAN STRAND THE OPERATOR. Take the only
   * member of the last page away and the coordinate they are standing on stops existing: the write's
   * re-read asks for it again, the server answers an empty page whose metadata still reports the true
   * total, and the grid renders nothing.
   *
   * @param roleId The role to read, forwarded exactly as supplied.
   * @param correctionAllowed Whether a past-the-end answer may issue one corrective read.
   */
  private dispatchAssignments(roleId: number, correctionAllowed = true): void {
    this.assignmentsRequest?.unsubscribe();
    this._assignmentsLoading.set(true);

    // The loading flag is lowered EXPLICITLY rather than through `finalize`, because a corrective read is
    // dispatched from inside the first read's `next` and a finaliser runs after it — so the first read's
    // teardown would lower the flag while its own correction was still in flight, and the grid would report
    // itself at rest with a request outstanding.
    this.assignmentsRequest = this.roleService.listUsers(roleId, this._assignmentsPage()).subscribe({
      next: (response) => {
        const page: PagedResult<UserRole> = toPagedResult<UserRole>(response);
        const requestedPageIndex: number = this._assignmentsPage().pageIndex;

        // Every clause is load-bearing. A positive total is what separates "this window is past the end"
        // from "this role has no members", which is a legitimate empty answer and must not provoke a second
        // request.
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
            // Never negative: this arm is only reached with a positive total, so the server reported at
            // least one page. The floor is stated rather than assumed because the alternative is a negative
            // index on the wire.
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
   * Asks whether ONE account holds one role, and on what terms. Legacy: `SecurityRoles.ascx.vb:L273-L303`
   * (`GetDates`) and `:L656-L658`, which answered two questions from the grid it had already bound — what
   * bounds to show for the chosen account, and whether to relabel the action 'Update User Role'.
   *
   * @param roleId The role to ask about, forwarded exactly as supplied.
   * @param userId The account to ask about, forwarded exactly as supplied.
   */
  probeAssignment(roleId: number, userId: number): void {
    this.assignmentProbeRequest?.unsubscribe();

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

          if (readStatus(error) === 404) {
            return;
          }

          this.recordFailure('probeAssignment', error);
        },
      });
  }

  /** Forgets the probe's answer without contacting the server. */
  clearProbedAssignment(): void {
    this.assignmentProbeRequest?.unsubscribe();
    this.assignmentProbeRequest = null;
    this._assignmentProbeLoading.set(false);
    this._probedAssignment.set(null);
    this._probedAssignmentKey.set(null);
  }

  /** @param roleId The role the settled write addressed. */
  private refreshAssignmentsIfCurrent(roleId: number): void {
    const view: AssignmentsViewState = this._assignmentsView();

    // ⚠ (1) THE SCREEN THAT ASKED HAS GONE. Runtime validation caught this case, and it is the one the
    // scope test below cannot see: leaving a membership listing does not blank the rows, so the scope still
    // names the role afterwards and agreed with the write.
    if (view.kind === 'left') {
      return;
    }

    // (2) A DIFFERENT ROLE IS ON SCREEN. Distinct from (3) rather than implied by it: a listing that has
    // announced itself but whose first read has not yet dispatched leaves the scope empty, and (3) would
    // read that emptiness as "nobody is looking" and adopt the written role underneath it.
    if (view.kind === 'open' && view.roleId !== roleId) {
      return;
    }

    // ⚠ COMPARED WITH STRICT INEQUALITY, AND ABSENCE IS TESTED EXPLICITLY. Role keys are `IDENTITY(0, 1)`,
    // so role 0 is a real role — the Administrators role of a fresh tenant — and a truthiness test would
    // read it as "nothing in scope" and take the adoption branch on a scope that is genuinely occupied,
    // reintroducing the defect for exactly one role.
    const inScope: number | null = this._assignmentsRoleId();

    if (inScope !== null && inScope !== roleId) {
      return;
    }

    this.loadAssignments(roleId);
  }

  /**
   * Declares that a membership listing for one role is now on screen. Called by the membership screen as
   * soon as it knows which role it addresses, and again with the new role when one instance is reused
   * across a change of route parameter.
   *
   * @param roleId The role the listing on screen addresses.
   */
  openAssignmentsView(roleId: number): void {
    this._assignmentsView.set({ kind: 'open', roleId });
  }

  /**
   * Declares that the membership listing for one role has gone. Called from the screen's destruction
   * hook.
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

  /** Re-reads the assignments for the role already in scope. */
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
   * Applies a narrowing intent and re-reads the roles under it. Legacy: the narrowing dropdown's change
   * handler at `Roles.ascx.vb:L273-L278`, which parsed the selected value and rebound.
   *
   * @param filter The narrowing to apply.
   */
  setGroupFilter(filter: RoleGroupFilter): void {
    this._groupFilter.set(filter);
    this._rolesPage.update((coordinate) => ({ ...coordinate, pageIndex: 0 }));
    this.loadRoles();
  }

  /**
   * Adopts a narrowing and a page together WITHOUT reading anything. ⚠ THIS COMMAND DISPATCHES NOTHING,
   * WHICH IS THE WHOLE POINT OF IT, AND A CALLER MUST FOLLOW IT WITH A READ. Every other command on this
   * store couples a state change to a read, which is right when the change originates in an affordance
   * the operator just used.
   *
   * @param filter The narrowing to adopt.
   * @param pageIndex The page to adopt, counted from zero.
   * @param sortBy The column to order by, or `null` to accept the endpoint's default ordering.
   * @param sortDir The direction, or `null`.
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
   * Moves the role listing to a page and re-reads it. ⚠ THIS COMMAND USED NOT TO EXIST, AND ITS ABSENCE
   * WAS DOCUMENTED AS DELIBERATE. The reasoning was sound for the shape it described: the listing was
   * read WHOLE, so a command that moved it to a page could not have been honoured by the read behind it,
   * and publishing one would have invited a caller to ask for a page, believe it received one, and lose
   * every role beyond that window.
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
   * @param query The filter, or `null` for none.
   */
  setRolesQuery(query: string | null): void {
    this._rolesPage.update((coordinate) => ({ ...coordinate, query, pageIndex: 0 }));
    this.loadRoles();
  }

  /**
   * Moves the assignment listing to a page and re-reads it. The index is stored and sent EXACTLY as
   * supplied.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  setAssignmentsPage(pageIndex: number): void {
    this._assignmentsPage.update((coordinate) => ({ ...coordinate, pageIndex }));
    this.reloadAssignments();
  }

  /**
   * Re-orders the assignment listing and re-reads it from the first page. The first page for the same
   * reason a page move re-reads at all: a record's page depends on the ordering, so a coordinate measured
   * under one order does not address the same records under another.
   *
   * @param sortBy The account field to order by, or `null` for the server's own ordering.
   * @param sortDir The direction, or `null` for the server's default.
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
   * Creates a role, then re-reads the listing. Answers a conflict when the portal already holds a role of
   * that name, which {@link RoleStore.recordFailure} surfaces verbatim through the shared catalogue.
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
            // The listing is the sole owner of listing reads now that its page, narrowing and ordering live
            // in its address: it reads on entry and on every address change, so a caller arriving there
            // always sees authoritative rows and totals without this command asking for them too.
          },
          error: (error: unknown) => {
            failure = this.recordFailure('createRole', error);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Updates a role, reconciles what is held against the server's own response, then re-reads the listing.
   * Both steps are deliberate and neither is redundant.
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
          },
          error: (error: unknown) => {
            failure = this.recordFailure('updateRole', error);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Deletes a role, and re-reads the listing only when the caller asks for it. for a ROLE, an empty
   * success really does mean the row is gone — which is exactly what makes {@link
   * RoleStore.removeAssignment} the exception rather than the rule, and why the two are not implemented
   * alike.
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
   * Assigns an account to a role, then RE-READS the assignments. THE WRITE IS AN UPDATE-OR-ADD AND THE
   * RESPONSE DOES NOT SAY WHICH HAPPENED. `Library/Components/Security/Roles/RoleController.vb:L550-L555`
   * seeded an assignment key with the absent-integer marker at `:L503`, looked for an existing row, and
   * updated it when one was found or added one when it was not.
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
            // ⚠ NOT REFRESHED WHILE A DIFFERENT ROLE IS THE ONE IN SCOPE. See {@link
            // RoleStore.refreshAssignmentsIfCurrent} for the cross-role republication this test prevents;
            // the previous code re-read unconditionally, and additionally MOVED the scope to this role
            // first, which made the test it needed impossible to write.
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
   * Creates a role group, then re-reads the group listing. Legacy:
   * `Website/admin/Security/EditGroups.ascx.vb`.
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
   * Renames or re-describes a role group, reconciling what is held from the response. The listing is
   * patched immutably from the server's own response body rather than re-read, which is safe here in a
   * way it is not for a role: a group's identity does not determine its own membership of the group
   * listing, so nothing can move it out.
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
   * Deletes a role group, resets the narrowing, then re-reads. Legacy:
   * `Website/admin/Security/Roles.ascx.vb:L290-L297`, which is three measured behaviours, all reproduced:
   * 1.
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
            // THE ORDER MATTERS AND IS NOT COSMETIC. Both read commands begin by discarding the held
            // failure, because a command a user starts deserves a clean slate — but a refresh the store
            // starts in response to a failure is not a new user command, and letting it run after the
            // failure was recorded would erase the very conflict it exists to explain.
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

  reset(): void {
    this.cancelReads();
    this.cancelWrites();

    this._roles.set(emptyPagedResult<RoleListItem>());
    // The account-narrowed slice is a tenant-scoped, person-identifying answer, so it is discarded with
    // everything else: the next operator to sign in must not find the previous one's account still the
    // subject of the listing.
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
   * Releases every request handle when the injector holding this store is destroyed. A root-provided
   * store lives as long as the application, so in production this runs on teardown.
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

  /** Abandons every read in flight and forgets its handle. */
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

  /** Releases every write handle. */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();

    // The count is ZEROED rather than decremented, because releasing a handle does not run the pipeline's
    // `finalize` for a subscription that was already closed and a per-handle decrement could therefore
    // leave a residue. Anything that was outstanding is abandoned wholesale here, so zero is the truth.
    this._pendingWrites.set(0);
  }

  /**
   * Issues the identifier for a write that is starting and records it as outstanding. A PRE-increment, so
   * the first identifier ever issued is 1 and 0 is a value no write holds — which lets a caller use 0 as
   * "no write of mine is outstanding" without a nullable field.
   *
   * @returns The identifier to return to the caller and to settle with.
   */
  private beginWrite(): number {
    this.nextMutationId += 1;
    this._pendingWrites.update((count) => count + 1);

    return this.nextMutationId;
  }

  /**
   * Records a write as finished and publishes its outcome under its own identifier. Called from
   * `finalize`, which runs on completion, on failure AND on unsubscription — so the count comes down on
   * every path a write can leave by, and a screen waiting on this identifier is never left waiting on a
   * write that has already gone. ⚠ THE FAILURE IS PASSED IN, not read from the shared slot.
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
