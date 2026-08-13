import { Injectable, type OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription, catchError, concatMap, finalize, from, of, tap, type Observable } from 'rxjs';

import {
  DEFAULT_PAGE_SIZE,
  emptyPagedResult,
  type ApiMeta,
  type PagedResult,
  type SortDirection,
} from '../models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../models/problem-details.model';
import type {
  CreateProfilePropertyDefinitionRequest,
  ProfilePropertyDefinition,
  UpdateProfilePropertyDefinitionRequest,
  UserProfile,
  UserProfileSubmission,
} from '../models/profile.model';
import type {
  ChangePasswordRequest,
  CreateUserRequest,
  MemberService,
  MembershipSettings,
  MembershipSettingsUpdateResult,
  RedeemServiceCodeResult,
  UpdateUserRequest,
  UserDetail,
  UserListItem,
  UserListQuery,
  UserSortField,
} from '../models/user.model';
import { UserService } from '../services/user.service';
import {
  failureCode,
  summarizeProblem,
  type ProblemSummary,
} from '../utils/form-errors.util';

// THE TENANT'S OPENING-VIEW POLICY
// The three values `Display_Mode` may hold, named rather than left as integers at the one place that
// branches on them.

/** `DisplayMode.All`: open on every account, paged and unfiltered. */
const DISPLAY_MODE_ALL = 0;

/** `DisplayMode.FirstLetter`: open on the first letter of the alphabet strip. */
const DISPLAY_MODE_FIRST_LETTER = 1;

/**
 * `DisplayMode.None`: open with no query at all. The default the legacy applied when the setting was
 * absent, and the server reproduces that default - so this is the mode a tenant that has configured
 * nothing is published as having.
 */
const DISPLAY_MODE_NONE = 2;

/**
 * The letter the first-letter mode opens on. `Website/admin/Users/Users.ascx.vb` L502 took
 * `Localization.GetString("Filter.Text")` and kept its FIRST CHARACTER; the resource value in
 * `Website/admin/Users/App_LocalResources/Users.ascx.resx` is `"A,B,C,D,…,Z"`, so the character is `A`.
 */
const OPENING_LETTER = 'A';

// ---------------------------------------------------------------------------
// THE SEARCH AXIS, AS A TYPED DISCRIMINATOR
// ---------------------------------------------------------------------------

/**
 * The letter a `FirstLetter` tenant's listing opens on. `A`, matching the legacy screen, which opened its
 * alphabet strip on the first letter rather than on a remembered one.
 */
const FIRST_LETTER_SEARCH_TEXT = 'A';

export type UserSearchMode = 'none' | 'all' | 'username' | 'email' | 'profileProperty';

/**
 * The search the listing is applying, as a closed discriminated union. The four searchable modes
 * correspond exactly to the branches the legacy screen offered that survive into the target endpoint
 * surface.
 */
export type UserSearch =
  | {
      readonly mode: 'none';
    }
  | {
      /**
       * Every account in the tenant, paged and unfiltered. the successor to `Users.ascx.vb` L264-L265,
       * which is a FOURTH legacy branch distinct from the three prefix searches and from the no-query
       * case: it called `UserController.GetUsers` with page coordinates and no filter whatsoever.
       */
      readonly mode: 'all';
    }
  | {
      /** Accounts whose sign-in name starts with {@link text}. */
      readonly mode: 'username';

      /** The caller's text, raw and exactly as typed. */
      readonly text: string;
    }
  | {
      /** Accounts whose electronic-mail address starts with {@link text}. */
      readonly mode: 'email';

      /** The caller's text, raw and exactly as typed. */
      readonly text: string;
    }
  | {
      /** Accounts whose named profile property starts with {@link text}. */
      readonly mode: 'profileProperty';

      /** The profile property to match on. an OPEN SET, and deliberately a plain string. */
      readonly propertyName: string;

      /** The caller's text, raw and exactly as typed. */
      readonly text: string;
    };

// ---------------------------------------------------------------------------
// FAILURE
// ---------------------------------------------------------------------------

/**
 * Which command a recorded failure belongs to. One member per transport method, so a screen showing
 * several slices at once can tell whether the listing failed or the credential change did, instead of
 * showing one message against all of them.
 */
export type UserOperation =
  | 'loadUsers'
  | 'loadUser'
  | 'createUser'
  | 'updateUser'
  | 'deleteUser'
  | 'loadProfile'
  | 'saveProfile'
  | 'changePassword'
  | 'resetPassword'
  | 'setApproval'
  | 'unlockUser'
  | 'requirePasswordChange'
  | 'loadMembershipSettings'
  | 'saveMembershipSettings'
  | 'loadProfileDefinitions'
  | 'loadProfileDefinition'
  | 'createProfileDefinition'
  | 'updateProfileDefinition'
  | 'applyProfileDefinitionEdits'
  | 'deleteProfileDefinition'
  | 'loadMemberServices'
  | 'subscribeToService'
  | 'cancelService'
  | 'startServiceTrial'
  | 'redeemServiceCode';

/**
 * One staged replacement in a profile-declaration batch. The pairing of an identifier with the members to
 * write, because the endpoint addresses the declaration in its path and carries the members in its body.
 */
export interface ProfileDefinitionEdit {
  /** The declaration to replace. */
  readonly propertyDefinitionId: number;

  /** The members to write, position included. */
  readonly request: UpdateProfilePropertyDefinitionRequest;
}

/**
 * A failure, as this store records it. The STRUCTURED problem document is retained alongside the summary
 * derived from it, and no message is ever composed, decorated or turned into markup here.
 */
/**
 * One row of a profile-declaration batch that the server refused, with the row it belongs to. ⚠ WHY A
 * LIST AND NOT JUST THE SETTLED OUTCOME. The batch is ONE store command with ONE settled result, which is
 * what stops a screen mistaking a sibling's outcome for its own — but a batch can refuse SEVERAL rows,
 * and an operator told only "something was refused" cannot tell which of five declarations to correct.
 */
export interface ProfileDefinitionBatchRefusal {
  /** The declaration whose write was refused. */
  readonly propertyDefinitionId: number;

  /** The refusal, described but never published into the store's one shared failure slot. */
  readonly failure: UserFailure;
}

export interface UserFailure {
  /** The command that failed. */
  readonly operation: UserOperation;

  /**
   * The problem document exactly as it arrived, or null when the failure carried none. Held so that a
   * caller needing the machine-readable detail has it.
   */
  readonly problem: ProblemDetails | null;

  /**
   * The failure summarised by the one function in the workspace that decides severity and wording. a
   * permission refusal is a WARNING, not an error, and that outcome is delegated rather than re-decided
   * here.
   */
  readonly summary: ProblemSummary;

  /**
   * The machine-readable failure code the document carried, or null when it carried none. A STRING,
   * always.
   */
  readonly code: string | null;
}

/**
 * One settled write, identified. ## The defect this closes This store used to publish ONE boolean for "a
 * write is in flight" and ONE failure slot, and it is provided at the application root. Every screen that
 * dispatched a write therefore watched the same boolean fall and then read the same slot to learn its own
 * outcome.
 */
export interface UserMutation {
  /** The identifier the store issued when this write was dispatched. */
  readonly id: number;

  /** Which command settled. */
  readonly operation: UserOperation;

  /**
   * This write's own failure, or null when it succeeded. ⚠ READ THIS, NOT {@link UserStore.failure}, TO
   * SETTLE A WRITE. This member is captured by the write it belongs to and cannot be affected by anything
   * another screen does.
   */
  readonly failure: UserFailure | null;
}

// ---------------------------------------------------------------------------
// PURE HELPERS
// ---------------------------------------------------------------------------

/** The filter members of the listing query that a search contributes. */
type UserSearchFilter = Pick<
  UserListQuery,
  'userName' | 'email' | 'profilePropertyName' | 'profilePropertyValue'
>;

/** The ordering members of the listing query. */
type UserOrdering = Pick<UserListQuery, 'sortBy' | 'sortDir'>;

/**
 * Translates a search into the filter members the listing query carries. THE TRAILING WILDCARD IS THE
 * SERVER'S, AND IS NOT ADDED HERE. The legacy screen composed its pattern at the call site —
 * `Users.ascx.vb` L269, L271 and L274 each appended one trailing per-cent character to the search text
 * before calling down — and the API reproduces that, wildcard included.
 *
 * @param search The search to translate.
 * @returns The filter members to merge into the query, or none at all.
 */
function userSearchFilter(search: UserSearch): UserSearchFilter {
  switch (search.mode) {
    case 'none':
    case 'all':
      // Absence is expressed by OMITTING the member, never by sending a reserved word
      // and never by sending empty text, which is a legitimate value on this contract.
      return {};
    case 'username':
      return { userName: search.text };
    case 'email':
      return { email: search.text };
    case 'profileProperty':
      return {
        profilePropertyName: search.propertyName,
        profilePropertyValue: search.text,
      };
    default:
      return {};
  }
}

/**
 * Assembles the ordering members, omitting each one that has not been chosen. A direction without a field
 * is meaningless, so the direction is only carried when a field is present; the server applies its own
 * ascending default when the direction is omitted.
 *
 * @param sortBy The field to order by, or undefined when the caller expressed no preference.
 * @param sortDir The direction to apply, or undefined to accept the server's default.
 * @returns The ordering members to merge into the query.
 */
function userOrdering(
  sortBy: UserSortField | undefined,
  sortDir: SortDirection | undefined,
): UserOrdering {
  if (sortBy === undefined) {
    return {};
  }

  if (sortDir === undefined) {
    return { sortBy };
  }

  return { sortBy, sortDir };
}

/**
 * Recovers the problem document from a failure, without assuming what the failure is. A value reaching a
 * subscriber's error path is `unknown` and nothing more: the shared error interceptor re-throws whatever
 * it received, and an operator between here and there may throw anything at all.
 *
 * @param cause The value the subscriber's error path received.
 * @returns The problem document, or null when the failure carried none.
 */
function readProblemDetails(cause: unknown): ProblemDetails | null {
  if (typeof cause !== 'object' || cause === null || Array.isArray(cause)) {
    return null;
  }

  const carrier = cause as Record<string, unknown>;

  if ('error' in carrier === false) {
    return isProblemDetails(cause) ? cause : null;
  }

  if (readTransportStatus(cause) === 0) {
    return null;
  }

  const body: unknown = carrier['error'];

  if (isProblemDetails(body)) {
    return body;
  }

  if (typeof body === 'string') {
    return parseProblemDetails(body);
  }

  return null;
}

/**
 * Parses a response body that may or may not be a problem document.
 *
 * @param body The body as text.
 * @returns The problem document, or null when the text is not one.
 */
function parseProblemDetails(body: string): ProblemDetails | null {
  try {
    const parsed: unknown = JSON.parse(body);

    return isProblemDetails(parsed) ? parsed : null;
  } catch {
    // Malformed text is not a problem document, and a failure while interpreting a
    // failure must not itself throw.
    return null;
  }
}

/**
 * Recovers the transport status from a failure.
 *
 * @param cause The value the subscriber's error path received.
 * @returns The status, or null when the failure carried none.
 */
function readTransportStatus(cause: unknown): number | null {
  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  const carrier = cause as Record<string, unknown>;
  const status: unknown = carrier['status'];

  return typeof status === 'number' ? status : null;
}

/**
 * Resolves the status a failure is to be judged by.
 *
 * @param document The problem document, or null when the failure carried none.
 * @param transportStatus The status the transport reported, or null when it reported none.
 * @returns The status to judge the failure by, or null when neither source has one.
 */
function resolveStatus(
  document: ProblemDetails | null,
  transportStatus: number | null,
): number | null {
  if (transportStatus !== null) {
    return transportStatus;
  }

  if (document === null || document.status === undefined) {
    return null;
  }

  return document.status;
}

/**
 * Attaches the OBSERVED status to a problem document that omitted one, so that severity can be decided at
 * all. This authors no text and invents no field, and it never overwrites a status the server wrote: a
 * document that already carries one is returned untouched, because rewriting server-authored data would
 * misreport what the server actually said.
 *
 * @param problem The document as it arrived, or null when none arrived.
 * @param status The status resolved for the failure, or null when there is none.
 * @returns The document to summarise, or null when there is nothing at all to report.
 */
function withObservedStatus(
  problem: ProblemDetails | null,
  status: number | null,
): ProblemDetails | null {
  if (problem === null) {
    return status === null ? null : { status };
  }

  if (problem.status !== undefined || status === null) {
    return problem;
  }

  return { ...problem, status };
}

// ---------------------------------------------------------------------------
// THE STORE
// ---------------------------------------------------------------------------

/** Holds and sequences every piece of account-administration state. */
@Injectable({ providedIn: 'root' })
export class UserStore implements OnDestroy {
  private readonly transport = inject(UserService);

  // ⚠ THIS STORE HOLDS THE MOST SENSITIVE STATE IN THE APPLICATION: names, email addresses, telephone
  // numbers, postal addresses and free-text profile answers, for accounts belonging to ONE TENANT and read
  // on the authority of ONE OPERATOR. Ending a session discards the credential; it does not discard
  // anything read with it, so without an explicit discard a previous operator's account listing and the
  // profile last opened would still be here for whoever signs in next.
  // -------------------------------------------------------------------------
  // WRITABLE SLICES
  // -------------------------------------------------------------------------

  /**
   * The page of accounts in hand, rows and paging facts together. Seeded with the shared empty envelope
   * rather than with null, so a template renders an empty listing before the first response instead of
   * branching on absence.
   */
  private readonly _users = signal<PagedResult<UserListItem>>(emptyPagedResult<UserListItem>());

  /**
   * The page of records to return, counted from zero, as most recently REQUESTED. the index on the wire
   * is ZERO-BASED, and no arithmetic is performed on it here.
   */
  private readonly _requestedPageIndex = signal<number>(0);

  /** The search the listing is applying. */
  private readonly _search = signal<UserSearch>({ mode: 'none' });

  /** The field to order by, or undefined to accept the server's own ordering. */
  private readonly _sortField = signal<UserSortField | undefined>(undefined);

  /** The direction to order in, or undefined to accept the server's ascending default. */
  private readonly _sortDirection = signal<SortDirection | undefined>(undefined);

  /**
   * Restrict the listing to authorised or to unauthorised accounts, or undefined for both. Undefined
   * rather than a defaulted boolean, because false MEANS "only the unauthorised ones" and is transmitted
   * as false.
   */
  private readonly _approvalFilter = signal<boolean | undefined>(undefined);

  /**
   * The account selected for editing, or undefined when none is. the successor to the control-state slot
   * at `Library/Components/Users/UserModuleBase.vb` L466-L505, which seeded itself from the integer null
   * marker at L468 — that is, from minus one — and tested for absence at L469 with an explicit is-nothing
   * comparison rather than a truthiness test.
   */
  private readonly _selectedUserId = signal<number | undefined>(undefined);

  /** The selected account in full, or null when none has been read. */
  private readonly _selectedUser = signal<UserDetail | null>(null);

  /** The selected account's profile, or null when none has been read. */
  private readonly _profile = signal<UserProfile | null>(null);

  /** The tenant's account policy, or null when it has not been read. */
  private readonly _membershipSettings = signal<MembershipSettings | null>(null);

  private readonly _membershipSettingsUnconfigured = signal<boolean>(false);

  /**
   * The tenant's profile declarations, in the order the server returned them. UNPAGED, and deliberately
   * so: the transport returns a plain array, and this store holds no page index, page size or total for
   * it.
   */
  private readonly _profileDefinitions = signal<readonly ProfilePropertyDefinition[]>([]);

  private readonly _selectedPropertyDefinitionId = signal<number | undefined>(undefined);

  /** The selected profile declaration in full, or null when none has been read. */
  private readonly _selectedProfileDefinition = signal<ProfilePropertyDefinition | null>(null);

  /**
   * The services offered to the account named by {@link _memberServicesAccountId}, with whatever that
   * account already holds against each of them.
   */
  private readonly _memberServices = signal<readonly MemberService[]>([]);

  /**
   * The account the catalogue in hand belongs to, or `undefined` before one has been read. ⚠ HELD SO THAT
   * A CATALOGUE CANNOT BE SHOWN AGAINST THE WRONG ACCOUNT. Every one of these five endpoints is gated on
   * account ownership, so a catalogue read for one account is meaningless for another; publishing the
   * account alongside the rows lets a screen assert that what it is rendering is what it asked for.
   */
  private readonly _memberServicesAccountId = signal<number | undefined>(undefined);

  /**
   * What the last invitation code admitted the account to, or `null` when none has been redeemed since
   * the slice was last cleared. Retained because the legacy screen reported the outcome in words -
   * `RSVPSuccess.Text` against `RSVPFailure.Text` - and the successful half of that report is a LIST: one
   * code may join several roles, since the legacy walk had no early exit.
   */
  private readonly _lastRedemption = signal<RedeemServiceCodeResult | null>(null);

  /**
   * What the last account-policy write did beyond storing the values it was given, or `null` when none
   * has been written since the slice was last cleared. ⚠ RETAINED BECAUSE THE WRITE HAS AN EFFECT THE
   * CALLER CANNOT PREDICT. Adopting a new display-name format recomposes every account's stored display
   * name in the tenant, so the operator who saved the settings screen needs to be told that it happened
   * and to how many accounts.
   */
  private readonly _lastSettingsWrite = signal<MembershipSettingsUpdateResult | null>(null);

  private readonly _usersLoading = signal<boolean>(false);
  private readonly _selectedUserLoading = signal<boolean>(false);
  private readonly _profileLoading = signal<boolean>(false);
  private readonly _membershipSettingsLoading = signal<boolean>(false);
  private readonly _profileDefinitionsLoading = signal<boolean>(false);
  private readonly _memberServicesLoading = signal<boolean>(false);

  /**
   * How many writes are in flight. A COUNT AND NOT A FLAG, because this store is provided at the
   * application root and several screens write through it at once.
   */
  private readonly _pendingWrites = signal<number>(0);

  /** The most recently settled write, identified, or null when none has settled since the last reset. */
  private readonly _mutation = signal<UserMutation | null>(null);

  /**
   * The identifier last issued to a write. Pre-incremented, so the first identifier ever issued is 1 and
   * zero is free for a caller to use as "no write of mine is outstanding" without colliding with a real
   * one.
   */
  private nextMutationId = 0;

  /**
   * How many staged replacements of the current declaration batch have still to be written. ⚠ A COUNT
   * RATHER THAN A FLAG, and the count is what makes the batch's progress observable and its overlap
   * impossible.
   */
  private readonly _profileDefinitionBatchRemaining = signal<number>(0);

  /** Backing state for {@link UserStore.profileDefinitionBatchRefusals}. */
  private readonly _profileDefinitionBatchRefusals = signal<readonly ProfileDefinitionBatchRefusal[]>(
    [],
  );

  /** The most recent failure, or null when nothing has failed since it was last cleared. */
  private readonly _failure = signal<UserFailure | null>(null);

  // -------------------------------------------------------------------------
  // IN-FLIGHT REQUESTS
  // -------------------------------------------------------------------------

  private listRequest: Subscription | null = null;
  private detailRequest: Subscription | null = null;
  private profileRequest: Subscription | null = null;
  private settingsRequest: Subscription | null = null;
  private definitionsRequest: Subscription | null = null;
  private definitionRequest: Subscription | null = null;
  private memberServicesRequest: Subscription | null = null;

  private readonly writeRequests = new Set<Subscription>();

  // -------------------------------------------------------------------------
  // PUBLISHED STATE
  // -------------------------------------------------------------------------

  /** The page of accounts in hand, rows and paging facts together. */
  readonly users = this._users.asReadonly();

  /** The search currently applied. */
  readonly search = this._search.asReadonly();

  /** The field the listing is ordered by, or undefined for the server's own ordering. */
  readonly sortField = this._sortField.asReadonly();

  /** The direction the listing is ordered in, or undefined for the server's default. */
  readonly sortDirection = this._sortDirection.asReadonly();

  /** The approval restriction, or undefined when both are included. */
  readonly approvalFilter = this._approvalFilter.asReadonly();

  /** The account selected for editing, or undefined when none is. */
  readonly selectedUserId = this._selectedUserId.asReadonly();

  /** The selected account in full, or null when none has been read. */
  readonly selectedUser = this._selectedUser.asReadonly();

  /** The selected account's profile, or null when none has been read. */
  readonly profile = this._profile.asReadonly();

  /** The tenant's account policy, or null when it has not been read. */
  readonly membershipSettings = this._membershipSettings.asReadonly();

  /**
   * Whether the tenant legitimately stores no account policy, as opposed to one that could not be read.
   * True only after a successful read whose document reported `isStored: false`.
   */
  readonly membershipSettingsUnconfigured = this._membershipSettingsUnconfigured.asReadonly();

  /** The tenant's profile declarations, unpaged and in the server's order. */
  readonly profileDefinitions = this._profileDefinitions.asReadonly();

  /** The profile declaration selected for editing, or undefined when none is. */
  readonly selectedPropertyDefinitionId = this._selectedPropertyDefinitionId.asReadonly();

  /** The selected profile declaration in full, or null when none has been read. */
  readonly selectedProfileDefinition = this._selectedProfileDefinition.asReadonly();

  /** The services offered to {@link memberServicesAccountId}, with what that account holds. */
  readonly memberServices = this._memberServices.asReadonly();

  /** The account the catalogue in hand belongs to. `undefined` before one has been read. */
  readonly memberServicesAccountId = this._memberServicesAccountId.asReadonly();

  /** What the last invitation code admitted the account to, or `null`. */
  readonly lastRedemption = this._lastRedemption.asReadonly();

  /** What the last account-policy write did beyond storing its values, or `null`. */
  readonly lastSettingsWrite = this._lastSettingsWrite.asReadonly();

  /** Whether the listing is being read. */
  readonly usersLoading = this._usersLoading.asReadonly();

  /** Whether the selected account is being read. */
  readonly selectedUserLoading = this._selectedUserLoading.asReadonly();

  /** Whether the profile is being read. */
  readonly profileLoading = this._profileLoading.asReadonly();

  /** Whether the account policy is being read. */
  readonly membershipSettingsLoading = this._membershipSettingsLoading.asReadonly();

  /** Whether the profile declarations are being read. */
  readonly profileDefinitionsLoading = this._profileDefinitionsLoading.asReadonly();

  /** Whether the member-services catalogue is being read. */
  readonly memberServicesLoading = this._memberServicesLoading.asReadonly();

  /**
   * Whether ANY write is in flight. ⚠ AN AGGREGATE, AND IT MUST NOT BE USED TO SETTLE A PARTICULAR WRITE.
   * It answers "is this store busy writing", which is the right question for a global busy indicator and
   * the wrong question for "has my write finished" — several screens write through this store at once, so
   * it falls when the FIRST of them settles.
   */
  readonly saving = computed<boolean>(() => this._pendingWrites() > 0);

  /** The most recently settled write: its identifier, its operation and its own outcome. */
  readonly mutation = this._mutation.asReadonly();

  /**
   * HOW MANY writes are in flight, not merely whether one is. A view over the same counter {@link
   * UserStore.saving} reduces to a boolean.
   */
  readonly writesInFlight = this._pendingWrites.asReadonly();

  /** How many staged replacements of the current declaration batch remain unwritten. */
  readonly profileDefinitionBatchRemaining = this._profileDefinitionBatchRemaining.asReadonly();

  /**
   * Every row of the most recent profile-declaration batch that the server refused, in the order the
   * refusals arrived. Emptied when a batch is dispatched, so it always describes the latest one and never
   * accumulates across attempts.
   */
  readonly profileDefinitionBatchRefusals = this._profileDefinitionBatchRefusals.asReadonly();

  /** The most recent failure, or null when nothing has failed. */
  readonly failure = this._failure.asReadonly();

  // -------------------------------------------------------------------------
  // DERIVED STATE
  // -------------------------------------------------------------------------

  /** The rows on the page in hand. */
  readonly userRows = computed<readonly UserListItem[]>(() => this._users().items);

  /** The paging facts locating the page in hand within the whole match set. */
  readonly pageMeta = computed<ApiMeta>(() => this._users().meta);

  /**
   * The page of records the SERVER reported returning, counted from zero. This, not the requested index,
   * is what a pager should be bound to.
   */
  readonly currentPageIndex = computed<number>(() => this._users().meta.pageIndex);

  /** The page of records most recently requested, counted from zero. */
  readonly requestedPageIndex = this._requestedPageIndex.asReadonly();

  /** The total no of records that satisfy the criteria, counted across every page. */
  readonly totalCount = computed<number>(() => this._users().meta.totalCount);

  /** The number of pages the match set spans. */
  readonly totalPages = computed<number>(() => this._users().meta.totalPages);

  /** The size of the page the server actually applied to the payload in hand. */
  readonly appliedPageSize = computed<number>(() => this._users().meta.pageSize);

  /** The size of the page to request. this is a PER-TENANT SETTING and is never a constant in this file. */
  readonly effectivePageSize = computed<number>(() => {
    const settings = this._membershipSettings();

    if (settings === null) {
      return DEFAULT_PAGE_SIZE;
    }

    return settings.recordsPerPage;
  });

  /** Whether the page in hand carries any rows. */
  readonly hasRecords = computed<boolean>(() => this._users().items.length > 0);

  /** Whether nothing at all matched, as distinct from having paged past the end. */
  readonly isEmptyResult = computed<boolean>(() => this._users().meta.totalCount === 0);

  /**
   * Whether the listing has been asked for NOTHING, as distinct from having asked and matched nothing.
   * The two look identical on screen and mean opposite things: an empty match set says the tenant has no
   * account answering the query, while this says no query was ever issued and the tenant's accounts are
   * simply unrequested.
   */
  readonly noQueryIssued = computed<boolean>(() => this._search().mode === 'none');

  /** Whether the requested page lies beyond a match set that is not itself empty. */
  readonly isPastEnd = computed<boolean>(() => {
    const page = this._users();

    return page.items.length === 0 && page.meta.totalCount > 0;
  });

  /**
   * Whether a pager is warranted for the page in hand. ADVISORY ONLY. the rule is the legacy one,
   * reproduced exactly.
   */
  readonly pagerWarranted = computed<boolean>(() => {
    const settings = this._membershipSettings();

    if (settings === null) {
      return true;
    }

    if (settings.displaySuppressPager === false) {
      return true;
    }

    const meta = this._users().meta;

    return meta.pageSize < meta.totalCount;
  });

  /** Which search is applied, as a bare discriminator for a template to switch on. */
  readonly searchMode = computed<UserSearchMode>(() => this._search().mode);

  /**
   * The profile property names a property search may name. Taken from the tenant's own declarations,
   * which is what makes the third search axis an open set rather than a fixed list.
   */
  readonly profilePropertyNames = computed<readonly string[]>(() =>
    this._profileDefinitions().map((definition) => definition.propertyName),
  );

  /** Whether the tenant has declared any profile property. */
  readonly hasProfileDefinitions = computed<boolean>(
    () => this._profileDefinitions().length > 0,
  );

  /**
   * Whether the account is offered any service at all. A tenant that publishes no public role offers
   * nothing, which is an ordinary state and not a failure - the legacy screen simply rendered an empty
   * grid for it.
   */
  readonly hasMemberServices = computed<boolean>(() => this._memberServices().length > 0);

  /** The services the account currently holds, lapsed ones included. */
  readonly heldMemberServices = computed<readonly MemberService[]>(() =>
    this._memberServices().filter((offer: MemberService) => offer.isSubscribed),
  );

  readonly lapsedMemberServices = computed<readonly MemberService[]>(() =>
    this._memberServices().filter((offer: MemberService) => offer.isExpired),
  );

  /** The selected account's profile values, or an empty sequence when no profile has been read. */
  readonly profileValues = computed(() => {
    const held = this._profile();

    if (held === null) {
      return [];
    }

    return held.properties;
  });

  /**
   * Whether the selected account must change its credential before it can proceed, or undefined when no
   * account has been read. Three states, deliberately.
   */
  readonly selectedUserMustChangePassword = computed<boolean | undefined>(() => {
    const held = this._selectedUser();

    if (held === null) {
      return undefined;
    }

    return held.mustChangePassword;
  });

  /**
   * The tenant the loaded records were read THROUGH, or undefined when nothing has been read. An
   * observation, never an instruction: the API resolves the tenant per request and takes no tenant
   * parameter, so this cannot influence what is asked for.
   */
  readonly observedPortalId = computed<number | undefined>(() => {
    const held = this._selectedUser();

    if (held !== null) {
      return held.portalId;
    }

    const rows = this._users().items;
    const first = rows.at(0);

    if (first === undefined) {
      return undefined;
    }

    return first.portalId;
  });

  /** Whether any read or write is in flight. */
  readonly busy = computed<boolean>(
    () =>
      this._usersLoading() ||
      this._selectedUserLoading() ||
      this._profileLoading() ||
      this._membershipSettingsLoading() ||
      this._profileDefinitionsLoading() ||
      this._memberServicesLoading() ||
      this.saving(),
  );

  /** How forcefully to present the most recent failure, or undefined when there is none. */
  readonly failureSeverity = computed(() => {
    const held = this._failure();

    if (held === null) {
      return undefined;
    }

    return held.summary.severity;
  });

  /**
   * The per-field messages the most recent failure reported, in the order the document listed them. Empty
   * when nothing failed or when the failure reported nothing per field.
   */
  readonly failureFieldMessages = computed(() => {
    const held = this._failure();

    if (held === null) {
      return [];
    }

    return held.summary.fieldMessages;
  });

  /**
   * The machine-readable code the most recent failure carried, or undefined when there is no failure and
   * null when the failure carried no code.
   */
  readonly failureReasonCode = computed<string | null | undefined>(() => {
    const held = this._failure();

    if (held === null) {
      return undefined;
    }

    return held.code;
  });

  /**
   * The support reference to quote when reporting the most recent failure, or undefined when there is
   * none to quote.
   */
  readonly failureSupportReference = computed<string | null | undefined>(() => {
    const held = this._failure();

    if (held === null) {
      return undefined;
    }

    return held.summary.supportReference;
  });

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Releases every request still outstanding when the injector holding this store is destroyed. A
   * root-provided store lives as long as the application, so this runs at shutdown and in a test that
   * destroys its environment between specs — which is precisely where an unreleased request would leak
   * across specs and make one spec's failure depend on another's timing.
   */
  ngOnDestroy(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE LISTING
  // -------------------------------------------------------------------------

  /**
   * Brings the store up for a listing screen: reads the tenant's account policy, then reads the first
   * page at the size that policy declares.
   */
  initialise(): void {
    this._failure.set(null);
    this.dispatchSettings(true);
  }

  /**
   * Re-reads the current page with the current search, ordering and filters. Dispatches NOTHING while no
   * search has been chosen.
   */
  loadUsers(): void {
    this._failure.set(null);
    this.dispatchUsers();
  }

  /**
   * Moves to another page of the current match set.
   *
   * @param pageIndex The page of records to return, counted from ZERO. Passed on exactly as supplied:
   * nothing is added to it, subtracted from it or clamped.
   */
  goToPage(pageIndex: number): void {
    this._failure.set(null);
    this._requestedPageIndex.set(pageIndex);
    this.dispatchUsers();
  }

  /**
   * Lists every account in the tenant, paged and unfiltered. the successor to `Users.ascx.vb` L264-L265,
   * a fourth legacy branch that called the unfiltered paged reader.
   */
  showAllAccounts(): void {
    this.applySearch({ mode: 'all' });
  }

  /** @param text The caller's text, raw and exactly as typed. */
  searchByUsername(text: string): void {
    this.applySearch({ mode: 'username', text });
  }

  /**
   * Lists accounts whose electronic-mail address STARTS WITH the given text. the successor to
   * `Users.ascx.vb` L268-L269.
   *
   * @param text The caller's text, raw and exactly as typed.
   */
  searchByEmail(text: string): void {
    this.applySearch({ mode: 'email', text });
  }

  /**
   * Lists accounts whose named profile property STARTS WITH the given text. the successor to
   * `Users.ascx.vb` L272-L274, the third search axis, whose field name was passed straight through as the
   * property name.
   *
   * @param propertyName The profile property to match on.
   * @param text The caller's text, raw and exactly as typed.
   */
  searchByProfileProperty(propertyName: string, text: string): void {
    this.applySearch({ mode: 'profileProperty', propertyName, text });
  }

  /**
   * Drops the search and lists every account in the tenant again. Resolves to the unfiltered listing
   * rather than to the no-query state, because clearing a filter on a listing screen means "show me
   * everything", not "show me nothing".
   */
  clearSearch(): void {
    this.applySearch({ mode: 'all' });
  }

  /**
   * Returns the listing to the state a first visit shows: no query chosen, no rows. ⚠ THIS EXISTS TO
   * CLOSE A MEASURED CONTRADICTION BETWEEN THIS STORE AND THE SCREEN THAT DISPLAYS IT. The listing
   * screen's free-text box and its search-axis control are component state, so they are reconstructed
   * EMPTY every time the screen is mounted, while this store outlives the screen and kept the previous
   * search.
   */
  resetSearchCriteria(): void {
    this.cancelListingRead();

    this._failure.set(null);
    this._users.set(emptyPagedResult<UserListItem>());
    this._requestedPageIndex.set(0);
    this._search.set({ mode: 'none' });
    this._sortField.set(undefined);
    this._sortDirection.set(undefined);
    this._approvalFilter.set(undefined);
  }

  /**
   * Orders the listing by a field, or hands the ordering back to the server.
   *
   * @param sortBy The field to order by, or undefined to accept the server's own ordering.
   */
  setSortField(sortBy: UserSortField | undefined): void {
    this._failure.set(null);
    this._sortField.set(sortBy);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * Sets the direction the listing is ordered in.
   *
   * @param sortDir The direction, or undefined to accept the server's ascending default.
   */
  setSortDirection(sortDir: SortDirection | undefined): void {
    this._failure.set(null);
    this._sortDirection.set(sortDir);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * Orders the listing by a field IN a direction, in one request. ⚠ WHY THIS EXISTS ALONGSIDE THE TWO
   * SETTERS ABOVE. Each of those dispatches a read of its own, so a caller expressing one ordering
   * through both would issue TWO requests for one reader action - and the first of the pair asks a
   * question nobody wanted: the new field in the OLD direction.
   *
   * @param sortBy The field to order by, or undefined to hand the ordering back to the server.
   * @param sortDir The direction, or undefined to accept the server's default.
   */
  setSort(sortBy: UserSortField | undefined, sortDir: SortDirection | undefined): void {
    this._failure.set(null);
    this._sortField.set(sortBy);
    this._sortDirection.set(sortDir);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * Restricts the listing to authorised or to unauthorised accounts, or to neither restriction. this is a
   * PAGED filter over the account table and is NOT a restoration of the legacy unpaged
   * unauthorised-accounts view at `Users.ascx.vb` L258-L260, which took no page coordinate at all and hid
   * the pager.
   *
   * @param isApproved The state to restrict to, or undefined to include both.
   */
  setApprovalFilter(isApproved: boolean | undefined): void {
    this._failure.set(null);
    this._approvalFilter.set(isApproved);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  // -------------------------------------------------------------------------
  // COMMANDS — ONE ACCOUNT
  // -------------------------------------------------------------------------

  /** @param userId The account to select. */
  selectUser(userId: number): void {
    this._failure.set(null);

    if (this._selectedUserId() !== userId) {
      this._selectedUser.set(null);
      this._profile.set(null);
      this._selectedUserId.set(userId);
    }

    this.dispatchUser(userId);
  }

  /** Clears the selection and everything read for it. Sets the selection to undefined. */
  clearSelectedUser(): void {
    this.cancelDetailReads();
    this._selectedUserId.set(undefined);
    this._selectedUser.set(null);
    this._profile.set(null);
  }

  /**
   * Creates an account in the tenant and selects it. The created account is adopted from the server's own
   * answer rather than assembled from the request, so the identifier it issued and any value it defaulted
   * are the ones held.
   *
   * @param request The account to create.
   */
  createUser(request: CreateUserRequest): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.create(request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'createUser', failure)))
        .subscribe({
          next: (created: UserDetail) => {
            this._selectedUserId.set(created.userId);
            this._selectedUser.set(created);
            this._profile.set(null);
            this.dispatchUsers();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('createUser', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Updates an account's own details. The written account is adopted from the server's answer.
   *
   * @param userId The account to update.
   * @param request The members to write.
   */
  updateUser(userId: number, request: UpdateUserRequest): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.update(userId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'updateUser', failure)))
        .subscribe({
          next: (written: UserDetail) => {
            if (this._selectedUserId() === userId) {
              this._selectedUser.set(written);
            }

            this.dispatchUsers();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('updateUser', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Removes one account. removal is PER ACCOUNT, and there is no bulk command here or anywhere.
   *
   * @param userId The account to remove.
   */
  deleteUser(userId: number): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.delete(userId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'deleteUser', failure)))
        .subscribe({
          next: () => {
            if (this._selectedUserId() === userId) {
              this.clearSelectedUser();
            }

            this.dispatchUsers();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('deleteUser', cause);
          },
        }),
    );

    return mutationId;
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE PROFILE
  // -------------------------------------------------------------------------

  /**
   * Reads one account's profile. a profile is a set of rows keyed by the tenant's own declarations, not a
   * fixed field list.
   *
   * @param userId The account whose profile to read.
   */
  loadProfile(userId: number): void {
    this._failure.set(null);
    this.dispatchProfile(userId);
  }

  /**
   * Writes one account's profile values. The response carries no body, so the profile is re-read
   * afterwards rather than assumed from the submission: the server records the instant each value was
   * last written, and a locally assembled profile would carry no such instant or a wrong one.
   *
   * @param userId The account whose profile to write.
   * @param submission The values to write, each with the visibility to apply.
   */
  saveProfile(userId: number, submission: UserProfileSubmission): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.updateProfile(userId, submission)
        .pipe(finalize(() => this.settleWrite(mutationId, 'saveProfile', failure)))
        .subscribe({
          next: () => {
            this.dispatchProfile(userId);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('saveProfile', cause);
          },
        }),
    );

    return mutationId;
  }

  // -------------------------------------------------------------------------
  // COMMANDS — CREDENTIALS
  // -------------------------------------------------------------------------

  /**
   * Changes an account's credential on behalf of the account holder, who supplies the credential in force
   * alongside the replacement. THE POLICY IS PRESERVED VERBATIM AND IS NOT TIGHTENED, and it is enforced
   * server-side.
   *
   * @param userId The account whose credential to change.
   * @param request The credential in force and its replacement.
   */
  changePassword(userId: number, request: ChangePasswordRequest): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.changePassword(userId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'changePassword', failure)))
        .subscribe({
          next: () => {
            this.reconcileSelectedAccount(userId);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('changePassword', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Resets an account's credential on behalf of an administrator, who does not supply the credential in
   * force. A separate command from {@link changePassword} rather than a mode of it, because the two
   * differ in what they require and in who may call them.
   *
   * @param userId The account whose credential to reset.
   * @param request The replacement credential.
   */
  resetPassword(userId: number, request: ChangePasswordRequest): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.passwordReset(userId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'resetPassword', failure)))
        .subscribe({
          next: () => {
            this.reconcileSelectedAccount(userId);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('resetPassword', cause);
          },
        }),
    );

    return mutationId;
  }

  // -------------------------------------------------------------------------
  // COMMANDS — ACCOUNT STATE TRANSITIONS
  // -------------------------------------------------------------------------

  /**
   * Sets one account's approval state.
   *
   * @param userId The account to set.
   * @param isApproved The state to set.
   */
  setApproval(userId: number, isApproved: boolean): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.setApproval(userId, isApproved)
        .pipe(finalize(() => this.settleWrite(mutationId, 'setApproval', failure)))
        .subscribe({
          next: () => {
            this.reconcileSelectedAccount(userId);
            this.dispatchUsers();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('setApproval', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Releases one account that repeated failed sign-in attempts have locked out.
   *
   * @param userId The account to release.
   */
  unlockUser(userId: number): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.unlock(userId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'unlockUser', failure)))
        .subscribe({
          next: () => {
            this.reconcileSelectedAccount(userId);
            this.dispatchUsers();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('unlockUser', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Obliges one account to change its credential at its next sign-in. Sets the obligation only: it does
   * not choose, generate, transmit or return a credential.
   *
   * @param userId The account to oblige.
   */
  requirePasswordChange(userId: number): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.requirePasswordChange(userId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'requirePasswordChange', failure)))
        .subscribe({
          next: () => {
            this.reconcileSelectedAccount(userId);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('requirePasswordChange', cause);
          },
        }),
    );

    return mutationId;
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE TENANT'S ACCOUNT POLICY
  // -------------------------------------------------------------------------

  /**
   * Reads the tenant's account policy on its own, without touching the listing. the legacy application
   * returned this as an untyped hash table from `Library/Components/Users/UserController.vb` L656, so
   * every caller had to know both the key spelling and the value type and a mistake in either failed at
   * run time.
   */
  loadMembershipSettings(): void {
    this._failure.set(null);
    this.dispatchSettings(false);
  }

  /**
   * Writes the tenant's account policy. The policy is re-read afterwards rather than assembled from the
   * request, because the server normalises several members on the way in.
   *
   * @param request The policy to write.
   */
  saveMembershipSettings(request: MembershipSettings): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;
    this._lastSettingsWrite.set(null);

    this.track(
      this.transport
        .updateMembershipSettings(request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'saveMembershipSettings', failure)))
        .subscribe({
          next: (report: MembershipSettingsUpdateResult) => {
            this.dispatchSettings(true);
            this._lastSettingsWrite.set(report);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('saveMembershipSettings', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Discards the report of the last account-policy write. Exists so a screen can dismiss the notice it
   * raised without re-reading anything.
   */
  clearSettingsWriteReport(): void {
    this._lastSettingsWrite.set(null);
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE TENANT'S PROFILE DECLARATIONS
  // -------------------------------------------------------------------------

  /**
   * Reads the tenant's profile declarations. UNPAGED: the transport returns a plain array and this store
   * holds no page index, page size or total for it.
   */
  loadProfileDefinitions(): void {
    this._failure.set(null);
    this.dispatchDefinitions();
  }

  /**
   * Selects one profile declaration and reads it in full.
   *
   * @param propertyDefinitionId The declaration to select.
   */
  selectProfileDefinition(propertyDefinitionId: number): void {
    this._failure.set(null);

    if (this._selectedPropertyDefinitionId() !== propertyDefinitionId) {
      this._selectedProfileDefinition.set(null);
      this._selectedPropertyDefinitionId.set(propertyDefinitionId);
    }

    this.definitionRequest?.unsubscribe();
    this.definitionRequest = this.transport
      .getProfileDefinition(propertyDefinitionId)
      .subscribe({
        // A successful read carries the declaration; an identifier naming none is refused with a
        // not-found problem document and is recorded as a failure rather than selected as empty.
        next: (definition: ProfilePropertyDefinition) => {
          this._selectedProfileDefinition.set(definition);
        },
        error: (cause: unknown) => {
          this.recordFailure('loadProfileDefinition', cause);
        },
      });
  }

  /** Clears the selected profile declaration. */
  clearSelectedProfileDefinition(): void {
    this.definitionRequest?.unsubscribe();
    this.definitionRequest = null;
    this._selectedPropertyDefinitionId.set(undefined);
    this._selectedProfileDefinition.set(null);
  }

  /**
   * Declares a new profile property for the tenant. The declaration list is re-read afterwards rather
   * than appended to, because where the new declaration falls depends on the position field and on the
   * server's ordering.
   *
   * @param request The declaration to create, position included.
   */
  createProfileDefinition(request: CreateProfilePropertyDefinitionRequest): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.createProfileDefinition(request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'createProfileDefinition', failure)))
        .subscribe({
          next: (created: ProfilePropertyDefinition) => {
            this._selectedPropertyDefinitionId.set(created.propertyDefinitionId);
            this._selectedProfileDefinition.set(created);
            this.dispatchDefinitions();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('createProfileDefinition', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Replaces one profile declaration. THIS IS ALSO HOW ORDERING IS CHANGED, and there is deliberately no
   * move-up or move-down command.
   *
   * @param propertyDefinitionId The declaration to replace.
   * @param request The members to write, position included.
   */
  updateProfileDefinition(
    propertyDefinitionId: number,
    request: UpdateProfilePropertyDefinitionRequest,
  ): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.updateProfileDefinition(propertyDefinitionId, request)
        .pipe(finalize(() => this.settleWrite(mutationId, 'updateProfileDefinition', failure)))
        .subscribe({
          next: (written: ProfilePropertyDefinition) => {
            if (this._selectedPropertyDefinitionId() === propertyDefinitionId) {
              this._selectedProfileDefinition.set(written);
            }

            this.dispatchDefinitions();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('updateProfileDefinition', cause);
          },
        }),
    );

    return mutationId;
  }

  /**
   * Writes a batch of staged declaration replacements, ONE AT A TIME, then re-reads the catalogue ONCE.
   * Legacy: `Website/admin/Users/ProfileDefinitions.ascx.vb` L446-L448 — the Apply handler called
   * `UpdateProperties()` and then `RefreshGrid()`.
   *
   * @param edits The staged replacements, applied in the order supplied.
   */
  applyProfileDefinitionEdits(edits: readonly ProfileDefinitionEdit[]): number {
    if (edits.length === 0 || this._profileDefinitionBatchRemaining() > 0) {
      return 0;
    }

    this._profileDefinitionBatchRemaining.set(edits.length);
    this._profileDefinitionBatchRefusals.set([]);

    // ⚠ THE BATCH IS ONE WRITE AS FAR AS THE STORE IS CONCERNED, and it is opened through the same
    // accounting every other write uses.
    const mutationId = this.beginWrite();

    // The first refusal, held until the batch settles so that the single recorded failure is the
    // one the operator has to act on rather than whichever row happened to answer last.
    let firstRefusal: unknown = null;
    let refused = false;
    let batchFailure: UserFailure | null = null;

    this.track(
      from(edits)
        .pipe(
          concatMap((edit: ProfileDefinitionEdit) =>
            this.transport.updateProfileDefinition(edit.propertyDefinitionId, edit.request).pipe(
              tap((written: ProfilePropertyDefinition) => {
                if (this._selectedPropertyDefinitionId() === edit.propertyDefinitionId) {
                  this._selectedProfileDefinition.set(written);
                }
              }),
              // A refused row is CAUGHT rather than allowed to end the batch, and the rows behind
              // it are still attempted.
              catchError((cause: unknown) => {
                if (!refused) {
                  refused = true;
                  firstRefusal = cause;
                }

                // ⚠ EVERY REFUSED ROW IS KEPT, NOT ONLY THE FIRST, AND IT IS KEPT WITH ITS ROW. The rows
                // are independent and the batch is not a transaction, so a five-row apply can come back
                // with three refusals and an operator told only that "something" was refused cannot tell
                // which declarations to correct.
                this._profileDefinitionBatchRefusals.update((refusals) => [
                  ...refusals,
                  {
                    propertyDefinitionId: edit.propertyDefinitionId,
                    failure: this.describeFailure('applyProfileDefinitionEdits', cause),
                  },
                ]);

                return of(null);
              }),
              tap(() => {
                this._profileDefinitionBatchRemaining.update((remaining) => remaining - 1);
              }),
            ),
          ),
        )
        // ⚠ SETTLED FROM `finalize`, NOT FROM `complete`.
        .pipe(
          finalize(() => {
            // ⚠ THE REFUSAL LIST IS NOT CLEARED HERE, AND MUST NOT BE. It is what the screen reads to
            // report the batch, and this runs immediately before the settled result is published — so
            // emptying it here would leave every refusal unreported.
            this._profileDefinitionBatchRemaining.set(0);
            this.settleWrite(mutationId, 'applyProfileDefinitionEdits', batchFailure);
          }),
        )
        .subscribe({
          // Deliberately EMPTY. Nothing is committed per row: the catalogue is read once when the batch
          // completes, because where each declaration falls depends on its position and on the server's
          // ordering, neither of which this store may re-derive.
          next: () => undefined,
          complete: () => {
            if (refused) {
              // Captured as well as published, so the settled result below carries this batch's own
              // refusal rather than whatever the shared slot happens to hold by then.
              batchFailure = this.recordFailure('applyProfileDefinitionEdits', firstRefusal);
            }

            // ONE read, after the last row has settled, on both outcomes - the legacy handler
            // rebound its grid unconditionally too.
            this.dispatchDefinitions();
          },
        }),
    );

    return mutationId;
  }

  /**
   * Removes one profile declaration. A declaration that cannot be removed — because values are recorded
   * against it, or because the tenant requires it — is refused with a status and a problem document,
   * which reaches the failure slot rather than leaving a silently unchanged list.
   *
   * @param propertyDefinitionId The declaration to remove.
   */
  deleteProfileDefinition(propertyDefinitionId: number): number {
    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport.deleteProfileDefinition(propertyDefinitionId)
        .pipe(finalize(() => this.settleWrite(mutationId, 'deleteProfileDefinition', failure)))
        .subscribe({
          next: () => {
            if (this._selectedPropertyDefinitionId() === propertyDefinitionId) {
              this.clearSelectedProfileDefinition();
            }

            this.dispatchDefinitions();
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('deleteProfileDefinition', cause);
          },
        }),
    );

    return mutationId;
  }

  // COMMANDS — THE ACCOUNT'S OWN SUBSCRIPTIONS
  // EVERY COMMAND RE-READS THE CATALOGUE ON SUCCESS, and that is the legacy behaviour rather than caution:
  // each command answers with no body, and the legacy handlers re-bound the grid after acting.

  /**
   * Reads the services offered to one account.
   *
   * @param userId The account whose catalogue to read.
   */
  loadMemberServices(userId: number): void {
    this._failure.set(null);
    this.dispatchMemberServices(userId);
  }

  /**
   * @param userId The account to subscribe.
   * @param roleId The service to subscribe to.
   */
  subscribeToService(userId: number, roleId: number): void {
    this.dispatchServiceCommand(
      'subscribeToService',
      userId,
      this.transport.subscribeToService(userId, roleId),
    );
  }

  /**
   * Cancels the account's subscription to one service. The server may EXPIRE the assignment rather than
   * remove it — `RoleController.vb:L494-L496` expires an assignment whose role charges a fee, so a paid
   * history is not destroyed by a cancellation — and either outcome is a success.
   *
   * @param userId The account to cancel for.
   * @param roleId The service to cancel.
   */
  cancelService(userId: number, roleId: number): void {
    this.dispatchServiceCommand(
      'cancelService',
      userId,
      this.transport.cancelService(userId, roleId),
    );
  }

  /**
   * @param userId The account taking the trial.
   * @param roleId The service whose trial to take.
   */
  startServiceTrial(userId: number, roleId: number): void {
    this.dispatchServiceCommand(
      'startServiceTrial',
      userId,
      this.transport.startServiceTrial(userId, roleId),
    );
  }

  /**
   * Redeems an invitation code, joining the account to every role recorded against it. The one command
   * here that answers with a payload, and it is retained: the legacy screen reported the outcome in
   * words, and the successful half of that report is a list of roles rather than a single fact.
   *
   * @param userId The account redeeming the code.
   * @param code The code as typed.
   */
  redeemServiceCode(userId: number, code: string): void {
    this._lastRedemption.set(null);

    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      this.transport
        .redeemServiceCode(userId, { code })
        .pipe(finalize(() => this.settleWrite(mutationId, 'redeemServiceCode', failure)))
        .subscribe({
          next: (joined: RedeemServiceCodeResult) => {
            // ⚠ THE RE-READ IS DISPATCHED BEFORE THE REPORT IS RECORDED, AND THE ORDER IS LOAD-BEARING. The
            // read adopts this account and discards a report belonging to a different one (see {@link
            // dispatchMemberServices}); recording first would hand it the report it has just been given and
            // clear it on the very first redemption, when no catalogue had yet been read and the account
            // was therefore "changing".
            this.dispatchMemberServices(userId);
            this._lastRedemption.set(joined);
          },
          error: (cause: unknown) => {
            failure = this.recordFailure('redeemServiceCode', cause);
          },
        }),
    );
  }

  /** Discards the last redemption outcome, so a screen can dismiss its report. */
  clearRedemption(): void {
    this._lastRedemption.set(null);
  }

  // -------------------------------------------------------------------------
  // COMMANDS — HOUSEKEEPING
  // -------------------------------------------------------------------------

  /** Discards the recorded failure, so a screen can dismiss a message. */
  clearFailure(): void {
    this._failure.set(null);
  }

  /** Returns every slice to its initial state and abandons every read in flight. */
  reset(): void {
    this.cancelReads();
    this.cancelWrites();

    this._users.set(emptyPagedResult<UserListItem>());
    this._requestedPageIndex.set(0);
    this._search.set({ mode: 'none' });
    this._sortField.set(undefined);
    this._sortDirection.set(undefined);
    this._approvalFilter.set(undefined);
    this._selectedUserId.set(undefined);
    this._selectedUser.set(null);
    this._profile.set(null);
    this._membershipSettings.set(null);
    // Released with the policy itself: the flag describes the PREVIOUS tenant's read, and a session
    // boundary can change which tenant the next read addresses.
    this._membershipSettingsUnconfigured.set(false);
    this._profileDefinitions.set([]);
    this._selectedPropertyDefinitionId.set(undefined);
    this._selectedProfileDefinition.set(null);
    this._memberServices.set([]);
    this._memberServicesAccountId.set(undefined);
    this._lastRedemption.set(null);
    this._lastSettingsWrite.set(null);

    this._usersLoading.set(false);
    this._selectedUserLoading.set(false);
    this._profileLoading.set(false);
    this._membershipSettingsLoading.set(false);
    this._profileDefinitionsLoading.set(false);
    this._pendingWrites.set(0);
    this._mutation.set(null);
    // Released with the writes above, so a batch abandoned at a session boundary cannot leave a
    // count standing that would refuse the next operator's first batch.
    this._profileDefinitionBatchRemaining.set(0);
    this._profileDefinitionBatchRefusals.set([]);
    this._memberServicesLoading.set(false);
    this._failure.set(null);
  }

  // -------------------------------------------------------------------------
  // INTERNALS
  // -------------------------------------------------------------------------

  /**
   * Adopts a search, returns to the first page and reads. The page is reset because a new search produces
   * a different match set, and asking for the fifth page of a set that now has one page would answer with
   * nothing at all while the pager insisted there was something there.
   *
   * @param search The search to apply.
   */
  private applySearch(search: UserSearch): void {
    this._failure.set(null);
    this._search.set(search);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * Adopts a search and a page together WITHOUT reading anything. ⚠ THIS COMMAND DISPATCHES NOTHING,
   * WHICH IS THE WHOLE POINT OF IT. Every other search command on this store couples the change to a
   * read, which is right when the change originates in an affordance the operator just used.
   *
   * @param search The search to adopt.
   * @param pageIndex The page to adopt, counted from zero.
   * @param sortBy The endpoint field to order by, or undefined to accept the endpoint's own default.
   * @param sortDir The direction, or undefined.
   */
  stageSearch(
    search: UserSearch,
    pageIndex: number,
    sortBy: UserSortField | undefined = undefined,
    sortDir: SortDirection | undefined = undefined,
  ): void {
    this._failure.set(null);
    this._search.set(search);
    this._requestedPageIndex.set(pageIndex);
    this._sortField.set(sortBy);
    this._sortDirection.set(sortDir);

    // Only this mode clears. Every other mode is about to be read, and emptying the grid first would
    // replace the operator's current rows with a blank frame for the duration of the request - the teardown
    // flicker a sibling finding was raised about.
    if (search.mode === 'none') {
      this._users.set(emptyPagedResult<UserListItem>());
    }
  }

  /** Returns the requested page to the first one, which is index zero. */
  private returnToFirstPage(): void {
    this._requestedPageIndex.set(0);
  }

  /**
   * Assembles the listing query from the slices that describe it.
   *
   * @returns The page to return, its size, the ordering and the search.
   */
  private buildListQuery(): UserListQuery {
    return {
      pageIndex: this._requestedPageIndex(),
      pageSize: this.effectivePageSize(),
      ...userOrdering(this._sortField(), this._sortDirection()),
      ...this.approvalRestriction(),
      ...userSearchFilter(this._search()),
    };
  }

  /**
   * The approval restriction as a query member, or nothing when both states are included.
   *
   * @returns The member to merge into the query.
   */
  private approvalRestriction(): Pick<UserListQuery, 'isApproved'> {
    const isApproved = this._approvalFilter();

    if (isApproved === undefined) {
      return {};
    }

    return { isApproved };
  }

  /**
   * Reads the listing, unless no search has been chosen. Does not clear the failure slot, so that a
   * failure recorded by whatever sequenced this read survives it — which is what lets {@link initialise}
   * report an unreadable policy while still listing the accounts.
   */
  private dispatchUsers(): void {
    if (this._search().mode === 'none') {
      this.listRequest?.unsubscribe();
      this.listRequest = null;
      this._usersLoading.set(false);

      return;
    }

    const query: UserListQuery = this.buildListQuery();

    this._usersLoading.set(true);
    this.listRequest?.unsubscribe();
    this.listRequest = this.transport.list(query).subscribe({
      next: (page: PagedResult<UserListItem>) => {
        this._users.set(page);
        this._usersLoading.set(false);
      },
      error: (cause: unknown) => {
        this._usersLoading.set(false);
        this.recordFailure('loadUsers', cause);
      },
    });
  }

  /** @param userId The account to read. */
  private dispatchUser(userId: number): void {
    this._selectedUserLoading.set(true);
    this.detailRequest?.unsubscribe();
    this.detailRequest = this.transport.getById(userId).subscribe({
      next: (held: UserDetail) => {
        this._selectedUser.set(held);
        this._selectedUserLoading.set(false);
      },
      error: (cause: unknown) => {
        this._selectedUserLoading.set(false);
        this.recordFailure('loadUser', cause);
      },
    });
  }

  /**
   * Reads one account's profile.
   *
   * @param userId The account whose profile to read.
   */
  private dispatchProfile(userId: number): void {
    this._profileLoading.set(true);
    this.profileRequest?.unsubscribe();
    this.profileRequest = this.transport.getProfile(userId).subscribe({
      next: (held: UserProfile) => {
        this._profile.set(held);
        this._profileLoading.set(false);
      },
      error: (cause: unknown) => {
        this._profileLoading.set(false);
        this.recordFailure('loadProfile', cause);
      },
    });
  }

  /**
   * Reads the tenant's account policy, and optionally reads the listing once it has arrived. The listing
   * follows on BOTH outcomes when it has been asked for.
   *
   * @param thenReadListing Whether to read the listing once the policy has been resolved.
   */
  private dispatchSettings(thenReadListing: boolean): void {
    this._membershipSettingsLoading.set(true);
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = this.transport.getMembershipSettings().subscribe({
      // A successful read carries the whole policy, whether or not the tenant stores one: a tenant with no
      // settings source is answered with the legacy defaults and says so through `isStored`.
      next: (settings: MembershipSettings) => {
        this._membershipSettings.set(settings);
        this._membershipSettingsLoading.set(false);

        this._membershipSettingsUnconfigured.set(settings.isStored === false);

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
      error: (cause: unknown) => {
        this._membershipSettingsLoading.set(false);

        this.recordFailure('loadMembershipSettings', cause);

        this._membershipSettingsUnconfigured.set(false);

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
    });
  }

  private readListingAfterSettings(): void {
    if (this._search().mode !== 'none') {
      // A search chosen before the policy arrived outranks the policy's opening view.
      this.dispatchUsers();

      return;
    }

    const opening: UserSearch | null = this.openingSearchForPolicy();

    // `None` yields no opening search, and nothing is dispatched. The mode is already 'none',
    // so there is nothing to write either — the screen simply waits to be asked.
    if (opening === null) {
      return;
    }

    this._search.set(opening);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * The search the tenant's display-mode policy opens the listing on.
   *
   * @returns The opening search, or `null` to leave the store in its no-query state - which is what the
   * policy's third mode asks for and what its own default is.
   */
  private openingSearchForPolicy(): UserSearch | null {
    const policy: MembershipSettings | null = this._membershipSettings();

    if (policy === null) {
      // The whole policy is unavailable. See the remark on the caller.
      return { mode: 'all' };
    }

    switch (policy.displayMode) {
      case DISPLAY_MODE_ALL:
        return { mode: 'all' };

      case DISPLAY_MODE_FIRST_LETTER:
        return { mode: 'username', text: OPENING_LETTER };

      case DISPLAY_MODE_NONE:
        return null;

      default:
        return { mode: 'all' };
    }
  }

  /** Reads the tenant's profile declarations. */
  private dispatchDefinitions(): void {
    this._profileDefinitionsLoading.set(true);
    this.definitionsRequest?.unsubscribe();
    this.definitionsRequest = this.transport.listProfileDefinitions().subscribe({
      next: (definitions: readonly ProfilePropertyDefinition[]) => {
        this._profileDefinitions.set(definitions);
        this._profileDefinitionsLoading.set(false);
      },
      error: (cause: unknown) => {
        this._profileDefinitionsLoading.set(false);
        this.recordFailure('loadProfileDefinitions', cause);
      },
    });
  }

  /**
   * Reads the member-services catalogue of one account, replacing whatever was held. The account is
   * recorded ALONGSIDE the rows, and on the request rather than on the response, so that a screen can
   * tell whose catalogue it is rendering even while the read is in flight.
   *
   * @param userId The account whose catalogue to read.
   */
  private dispatchMemberServices(userId: number): void {
    if (this._memberServicesAccountId() !== userId) {
      this._memberServices.set([]);
      this._lastRedemption.set(null);
    }

    this._memberServicesAccountId.set(userId);
    this._memberServicesLoading.set(true);
    this.memberServicesRequest?.unsubscribe();
    this.memberServicesRequest = this.transport.listMemberServices(userId).subscribe({
      next: (offered: readonly MemberService[]) => {
        this._memberServices.set(offered);
        this._memberServicesLoading.set(false);
      },
      error: (cause: unknown) => {
        this._memberServicesLoading.set(false);
        this.recordFailure('loadMemberServices', cause);
      },
    });
  }

  /**
   * Runs one payload-free subscription command and re-reads the catalogue on success. The three commands
   * differ only in which request they issue and which operation name a failure is recorded under, so they
   * share one body: a divergence between them would be a divergence in how a refusal is reported, which
   * is precisely what a caller relies on to explain one.
   *
   * @param operation The command name a failure is recorded under.
   * @param userId The account the command acts on, and whose catalogue is re-read.
   * @param request The transport call to run.
   */
  private dispatchServiceCommand(
    operation: UserOperation,
    userId: number,
    request: Observable<void>,
  ): void {
    this._lastRedemption.set(null);

    const mutationId = this.beginWrite();
    let failure: UserFailure | null = null;

    this.track(
      request.pipe(finalize(() => this.settleWrite(mutationId, operation, failure))).subscribe({
        next: () => {
          this.dispatchMemberServices(userId);
        },
        error: (cause: unknown) => {
          failure = this.recordFailure(operation, cause);
        },
      }),
    );
  }

  /**
   * Re-reads the selected account, but only when the account just written IS the selected one.
   *
   * @param userId The account that was written.
   */
  private reconcileSelectedAccount(userId: number): void {
    if (this._selectedUserId() !== userId) {
      return;
    }

    this.dispatchUser(userId);
  }

  /**
   * Opens a write and returns the identifier the caller settles it by. Pre-increments, so the first
   * identifier ever issued is 1.
   *
   * @returns The identifier issued to this write.
   */
  private beginWrite(): number {
    if (this._pendingWrites() === 0) {
      this._failure.set(null);
    }

    this.nextMutationId += 1;
    this._pendingWrites.update((open) => open + 1);

    return this.nextMutationId;
  }

  /**
   * Settles one write: lowers the pending count and publishes the outcome under its identifier. ⚠ CALLED
   * FROM `finalize` RATHER THAN FROM THE TWO CALLBACKS. `finalize` runs on completion, on error AND on
   * unsubscription, which is the only one of the three that a pair of callbacks cannot see: a write
   * released by a session boundary or by teardown would otherwise leave the count raised for the life of
   * the application, and the store would report itself permanently busy. ⚠ THE COUNT IS FLOORED AT ZERO.
   * `finalize` runs exactly once per subscription, so it cannot legitimately go negative — but a negative
   * count would make the aggregate read false while a write was still open, which is the one failure mode
   * this whole mechanism exists to remove, so it is made unrepresentable rather than merely unlikely.
   *
   * @param id The identifier this write was issued.
   * @param operation Which command settled.
   * @param failure This write's own failure, or null when it succeeded.
   */
  private settleWrite(id: number, operation: UserOperation, failure: UserFailure | null): void {
    this._pendingWrites.update((open) => (open > 0 ? open - 1 : 0));
    this._mutation.set({ id, operation, failure });
  }

  /**
   * Describes a refusal WITHOUT publishing it anywhere. ⚠ EXTRACTED SO THAT A PER-ROW REFUSAL CAN BE
   * DESCRIBED WITHOUT TOUCHING THE SHARED SLOT. There is one failure slot for the whole store, so a batch
   * that published each refused row into it would leave only the last one standing and would clear
   * whatever another screen was showing.
   *
   * @param operation The command the refusal belongs to.
   * @param cause The refusal as the transport delivered it.
   * @returns The described failure.
   */
  private describeFailure(operation: UserOperation, cause: unknown): UserFailure {
    const document: ProblemDetails | null = readProblemDetails(cause);
    const status: number | null = resolveStatus(document, readTransportStatus(cause));
    const problem: ProblemDetails | null = withObservedStatus(document, status);

    return {
      operation,
      problem,
      summary: summarizeProblem(problem),
      code: failureCode(problem),
    };
  }

  /**
   * Records a failure against the command that produced it, publishing it into the shared slot.
   *
   * @param operation The command that failed.
   * @param cause The value the subscriber's error path received.
   * @returns The recorded failure.
   */
  private recordFailure(operation: UserOperation, cause: unknown): UserFailure {
    const failure: UserFailure = this.describeFailure(operation, cause);

    this._failure.set(failure);

    return failure;
  }

  /**
   * Holds a write's handle until it settles, so a session boundary and teardown can release it. ⚠ THE
   * COMPLETION TEARDOWN IS WHAT MAKES A SET SAFE HERE. An RxJS `Subscription` container detached a
   * finished child by itself; a set does not, so a handle is removed on completion explicitly. Without
   * that the set would grow for the life of the application, one entry per write ever issued.
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

  /** Releases every write handle. */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();
    // A batch abandoned mid-flight lowers its own count here, because a cancelled stream never
    // completes and so never reaches the arm that would lower it.
    this._profileDefinitionBatchRemaining.set(0);
    this._profileDefinitionBatchRefusals.set([]);
  }

  /** Abandons every read in flight, leaving writes alone. */
  private cancelReads(): void {
    this.listRequest?.unsubscribe();
    this.listRequest = null;
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = null;
    this.definitionsRequest?.unsubscribe();
    this.definitionsRequest = null;
    this.definitionRequest?.unsubscribe();
    this.definitionRequest = null;
    this.memberServicesRequest?.unsubscribe();
    this.memberServicesRequest = null;

    // ⚠ EVERY READ FLAG THIS METHOD ABANDONS A REQUEST FOR IS LOWERED HERE, AND THREE OF THEM WERE NOT.
    // Unsubscribing kills the request WITHOUT delivering next, error or complete, so nothing downstream
    // ever runs the handler that would have lowered the flag - the abandonment is silent by design.
    this._usersLoading.set(false);
    this._membershipSettingsLoading.set(false);
    this._profileDefinitionsLoading.set(false);
    this._memberServicesLoading.set(false);
    this.cancelDetailReads();
  }

  /**
   * Abandons the read that belongs to the listing query, and nothing else. Separate from {@link
   * UserStore.cancelReads} because the two have different scopes and only one of them is safe to call
   * when a query is merely being replaced: the tenant's account policy and profile declarations are not
   * part of a query, so a request in flight for either must survive.
   */
  private cancelListingRead(): void {
    this.listRequest?.unsubscribe();
    this.listRequest = null;
    this._usersLoading.set(false);
  }

  /** Abandons the reads that belong to the selected account. */
  private cancelDetailReads(): void {
    this.detailRequest?.unsubscribe();
    this.detailRequest = null;
    this.profileRequest?.unsubscribe();
    this.profileRequest = null;
    this._selectedUserLoading.set(false);
    this._profileLoading.set(false);
  }
}
