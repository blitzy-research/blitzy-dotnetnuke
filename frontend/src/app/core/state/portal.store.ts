import { Injectable, computed, inject, signal } from '@angular/core';

import type { OnDestroy } from '@angular/core';
import { AsyncSubject } from 'rxjs';

import type { Observable, Subscription } from 'rxjs';

import { emptyPagedResult } from '../models/paged-result.model';
import { isProblemDetails } from '../models/problem-details.model';
import { PortalService } from '../services/portal.service';
import {
  failureCode,
  isConflictCode,
  isValidationProblemDetails,
  problemSeverity,
  problemSupportReference,
  transportProblem,
} from '../utils/form-errors.util';

import type { ApiMeta, SortDirection } from '../models/paged-result.model';
import type {
  CreatePortalAliasRequest,
  CreatePortalRequest,
  PortalAdministrator,
  PortalAlias,
  PortalDetail,
  PortalListItem,
  PortalListPage,
  PortalSettings,
  UpdatePortalAliasRequest,
  UpdatePortalRequest,
  UpdatePortalSettingsRequest,
} from '../models/portal.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../models/problem-details.model';
import type { ConflictCode, ProblemSeverity } from '../utils/form-errors.util';

/**
 * Everything a consumer needs to know about one failed operation, classified once. The store holds the
 * STRUCTURED problem document rather than a sentence, and this contract is the classified view over it.
 */
export interface PortalFailure {
  /**
   * The RFC 7807 document for this failure. NEVER `null`: a failure that carried no document - a request
   * that never reached the server, or a response whose body is not a problem document - is given one
   * composed from its status by `transportProblem`.
   */
  readonly problem: ProblemDetails;

  /**
   * Whether {@link PortalFailure.problem} was COMPOSED from the transport status rather than published by
   * the server. ⚠ A CONSUMER WITH ITS OWN WORDING FOR THIS OPERATION MUST CHECK THIS BEFORE PREFERRING
   * THE DOCUMENT'S DETAIL. A composed document carries a truthful but generic sentence derived from the
   * status alone, so a screen that has the wording its legacy predecessor used - "An error was
   * encountered during the creation of your portal…" - has something strictly better to show and should
   * show it.
   */
  readonly synthesised: boolean;

  /**
   * The status to classify by: the document's own status where it carried one, and otherwise the
   * transport status of the failed response.
   */
  readonly status: number | null;

  /** How forcefully to present the failure. */
  readonly severity: ProblemSeverity;

  /**
   * The state-refusal code the server published, or `null` when the failure was not one of the recognised
   * refusals.
   */
  readonly conflictCode: ConflictCode | null;

  /** The same document, narrowed, when it carries per-field failures; `null` otherwise. */
  readonly validation: ValidationProblemDetails | null;

  /**
   * The identifier an operator quotes when reporting this failure, or `null` when the document carried
   * none.
   */
  readonly supportReference: string | null;
}

/**
 * The ordering to apply to the portal listing, or its deliberate absence. `null` in either member means
 * "no preference", which is transmitted as an omission so that the server's own ordering applies.
 */
export interface PortalSort {
  /** The field to order by, or `null` for the server's own ordering. */
  readonly sortBy: string | null;

  /** The direction to apply, or `null` for the server's default. */
  readonly sortDir: SortDirection | null;
}

/**
 * A COMPLETE listing query: which page, how large, filtered how, ordered how. Applied as one unit by
 * {@link PortalStore.applyListQuery}, which is why it is an interface rather than four arguments - the
 * four coordinates are restored together when a screen keeps them in its address, and applying them one
 * at a time would issue one request per coordinate and reset the page index three times on the way.
 */
export interface PortalListQuery extends PortalSort {
  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /** The size to ask for, or `null` to express no preference and take the server's default. */
  readonly pageSize: number | null;

  /** The name filter, or `null` for no filter. */
  readonly name: string | null;
}

// FAILURE READING
// WHY STRUCTURAL AND NOT BY TYPE. `core/interceptors/error.interceptor.ts` announces a failure and then
// RE-THROWS the original value, so what arrives at a subscriber here is the framework's own failed-response
// object.

/**
 * Reads the transport status out of a failed operation. Read FIRST, before the body, and the ordering is
 * load-bearing rather than tidy - see {@link readProblem}.
 *
 * @param cause The value the subscriber's failure channel delivered.
 * @returns The status, or `null` when the value carried none.
 */
function readStatus(cause: unknown): number | null {
  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  if ('status' in cause && typeof cause.status === 'number') {
    return cause.status;
  }

  return null;
}

/**
 * Reads the RFC 7807 document out of a failed operation. ONLY THE BODY IS TESTED, never the response
 * object around it, and that is a correctness requirement rather than a preference.
 *
 * @param cause The value the subscriber's failure channel delivered.
 * @param status The status already resolved by {@link readStatus}.
 * @returns The document, or `null` when the failure carried none.
 */
function readProblem(cause: unknown, status: number | null): ProblemDetails | null {
  if (status === 0) {
    return null;
  }

  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  if ('error' in cause) {
    const body: unknown = cause.error;

    if (isProblemDetails(body)) {
      return body;
    }
  }

  return null;
}

/**
 * Classifies a failure once, so that no consumer classifies it again.
 *
 * @param cause The value the subscriber's failure channel delivered.
 * @returns The classified failure.
 */
function classifyFailure(cause: unknown): PortalFailure {
  const status: number | null = readStatus(cause);
  const problem: ProblemDetails | null = readProblem(cause, status);

  let effectiveStatus: number | null = status;

  if (problem !== null && typeof problem.status === 'number') {
    effectiveStatus = problem.status;
  }

  const code: string | null = failureCode(problem);

  const document: ProblemDetails = problem ?? transportProblem(effectiveStatus);

  return {
    problem: document,
    synthesised: problem === null,
    status: effectiveStatus,
    severity: problemSeverity(effectiveStatus),
    conflictCode: isConflictCode(code) ? code : null,
    validation: isValidationProblemDetails(problem) ? problem : null,
    supportReference: problemSupportReference(document),
  };
}

/**
 * The listing's seed: the coordinates a server response carries when nothing matched and no paging
 * applied.
 */
const EMPTY_PORTAL_PAGE: PortalListPage = emptyPagedResult<PortalListItem>();

/** The first page, counted from zero. ZERO, and the base is the wire's rather than the legacy screen's. */
const FIRST_PAGE_INDEX = 0;

/**
 * Portal state for the administration front end: the paged listing, the selected portal, its settings
 * projection and its unpaged host-name aliases. Root-provided and therefore a single instance for the
 * application.
 */
@Injectable({ providedIn: 'root' })
export class PortalStore implements OnDestroy {
  /** The typed transport. */
  private readonly portalApi = inject(PortalService);

  // ⚠ THIS STORE IS DISCARDED AT A SESSION BOUNDARY, AND IT DOES NOT ARRANGE THAT ITSELF. Every slice here
  // belongs to ONE TENANT and, through the permissions that admitted the read, to ONE OPERATOR. Ending a
  // session discards the credential; it does not discard anything read with it, so without the discard the
  // portals a previous operator listed would still be here, rendered by whatever screen the next operator
  // lands on.
  // IN-FLIGHT REQUEST HANDLES

  private listRequest: Subscription | null = null;

  private detailRequest: Subscription | null = null;

  private settingsRequest: Subscription | null = null;

  private administratorsRequest: Subscription | null = null;

  private aliasRequest: Subscription | null = null;

  /**
   * The current tenant's protected-facts read. A handle of its own rather than a share of {@link
   * detailRequest}, because the two address different portals and either may be in flight while the other
   * is: an operator browsing one portal's settings must not cancel the read that tells a role screen
   * which role its own tenant protects.
   */
  private contextRequest: Subscription | null = null;

  private readonly writeRequests = new Set<Subscription>();

  // -------------------------------------------------------------------------
  // LISTING
  // -------------------------------------------------------------------------

  /**
   * The page in hand, exactly as the server reported it: its rows and its coordinates together. replaces
   * the untyped collection the legacy screen held.
   */
  private readonly _page = signal<PortalListPage>(EMPTY_PORTAL_PAGE);

  /**
   * The page of records to return, counted from zero. The REQUESTED index, which is what the next request
   * will carry.
   */
  private readonly _pageIndex = signal<number>(FIRST_PAGE_INDEX);

  /**
   * The size of the page to ask for, or `null` to express no preference. `null` is transmitted as an
   * OMISSION so the server applies its own default.
   */
  private readonly _requestedPageSize = signal<number | null>(null);

  /**
   * The operator's portal-name filter, raw and exactly as typed, or `null` for no filter. the trailing
   * wildcard is the SERVER'S to compose and no character of a pattern is contributed here.
   */
  private readonly _nameFilter = signal<string | null>(null);

  /** The field to order the listing by, or `null` for the server's own ordering. */
  private readonly _sortBy = signal<string | null>(null);

  /**
   * The direction to order in, or `null` for the server's default. The token is the server's member name,
   * taken from the paging contract rather than spelled here, because the binder accepts the member name
   * and answers an abbreviated or lower-cased spelling with a rejection.
   */
  private readonly _sortDir = signal<SortDirection | null>(null);

  /** Whether a listing request is in flight. */
  private readonly _listLoading = signal<boolean>(false);

  /** The last listing failure, classified, or `null` when the last attempt succeeded. */
  private readonly _listFailure = signal<PortalFailure | null>(null);

  /** Whether a listing read has ever COMPLETED for this store instance. */
  private listingRead = false;

  // -------------------------------------------------------------------------
  // SELECTED PORTAL
  // -------------------------------------------------------------------------

  /**
   * The portal an operator is working on, or `undefined` when none is selected. THE ABSENCE OF A
   * SELECTION IS A DISTINCT `undefined`.
   */
  private readonly _selectedPortalId = signal<number | undefined>(undefined);

  /**
   * The selected portal in full, or `null` when it has not been read. DISTINCT FROM {@link
   * PortalStore.selectedPortalId} BEING ABSENT, and the two must not be collapsed: an identifier with no
   * record means "selected, not yet loaded", whereas no identifier means "nothing is selected".
   */
  private readonly _selectedPortal = signal<PortalDetail | null>(null);

  /** Whether a single-portal read or write is in flight. */
  private readonly _detailLoading = signal<boolean>(false);

  /** The last single-portal failure, classified, or `null`. */
  private readonly _detailFailure = signal<PortalFailure | null>(null);

  // -------------------------------------------------------------------------
  // SETTINGS PROJECTION
  // -------------------------------------------------------------------------

  /**
   * The selected portal's configuration projection, or `null` when it has not been read. there is no
   * portal-settings table and this is not a key/value bag.
   */
  private readonly _settings = signal<PortalSettings | null>(null);

  /** Whether a settings read or write is in flight. */
  private readonly _settingsLoading = signal<boolean>(false);

  /** The last settings failure, classified, or `null`. */
  private readonly _settingsFailure = signal<PortalFailure | null>(null);

  /**
   * The accounts the selected portal may designate as its administrator, or `null` when they have not
   * been read.
   */
  private readonly _administrators = signal<readonly PortalAdministrator[] | null>(null);

  /** Which portal the held candidate list belongs to, or `undefined` when none has been read. */
  private readonly _administratorsPortalId = signal<number | undefined>(undefined);

  /** Whether a candidate read is in flight. */
  private readonly _administratorsLoading = signal<boolean>(false);

  /** The last candidate-read failure, classified, or `null`. */
  private readonly _administratorsFailure = signal<PortalFailure | null>(null);

  // -------------------------------------------------------------------------
  // ALIASES - DELIBERATELY UNPAGED
  // -------------------------------------------------------------------------

  /**
   * Every host name bound to the portal named by {@link PortalStore.aliasesPortalId}, or `null` when they
   * have not been read. THREE STATES, and the first two must not be collapsed - the same three the
   * response contract distinguishes.
   */
  private readonly _aliases = signal<readonly PortalAlias[] | null>(null);

  /**
   * Which portal the held alias collection belongs to, or `undefined` when none has been read. Held so
   * that a screen can tell whether the collection in hand is the one it is showing.
   */
  private readonly _aliasesPortalId = signal<number | undefined>(undefined);

  private readonly _selectedAliasId = signal<number | undefined>(undefined);

  /** One alias row read on its own, or `null` when none has been. */
  private readonly _aliasDetail = signal<PortalAlias | null>(null);

  /** Whether an alias read or write is in flight. */
  private readonly _aliasLoading = signal<boolean>(false);

  /** The last alias failure, classified, or `null`. */
  private readonly _aliasFailure = signal<PortalFailure | null>(null);

  // THE CURRENT TENANT'S PROTECTED FACTS
  // A slice of its own, deliberately separate from {@link _selectedPortal}.

  /**
   * The tenant the protected facts describe, or `undefined` before the first read. ⚠ `undefined` MEANS
   * UNREAD AND NOTHING ELSE. Portal keys are `IDENTITY(-1, 1)` (`01.00.00.SqlDataProvider:L77`), so BOTH
   * `-1` and `0` are real tenants and neither may be read as absence. That is why this is `number |
   * undefined` rather than a sentinel.
   */
  private readonly _contextPortalId = signal<number | undefined>(undefined);

  /** The current tenant's own record, or `null` before the first successful read. */
  private readonly _context = signal<PortalDetail | null>(null);

  /** Whether the protected-facts read is in flight. */
  private readonly _contextLoading = signal<boolean>(false);

  // PUBLIC STATE - READ-ONLY PROJECTIONS

  /** The page in hand: its rows and the coordinates the server reported for them. */
  readonly page = this._page.asReadonly();

  /** The page of records to return, counted from zero, as the next request will carry it. */
  readonly pageIndex = this._pageIndex.asReadonly();

  /** The size of the page to ask for, or `null` to let the server apply its default. */
  readonly requestedPageSize = this._requestedPageSize.asReadonly();

  /** The operator's portal-name filter, raw, or `null` for no filter. */
  readonly nameFilter = this._nameFilter.asReadonly();

  /** The field the listing is ordered by, or `null` for the server's own ordering. */
  readonly sortBy = this._sortBy.asReadonly();

  /** The direction the listing is ordered in, or `null` for the server's default. */
  readonly sortDir = this._sortDir.asReadonly();

  /** Whether a listing request is in flight. */
  readonly listLoading = this._listLoading.asReadonly();

  /** The last listing failure, classified, or `null` when the last attempt succeeded. */
  readonly listFailure = this._listFailure.asReadonly();

  /** The portal an operator is working on, or `undefined` when none is selected. */
  readonly selectedPortalId = this._selectedPortalId.asReadonly();

  /** The selected portal in full, or `null` when it has not been read. */
  readonly selectedPortal = this._selectedPortal.asReadonly();

  /** Whether a single-portal read or write is in flight. */
  readonly detailLoading = this._detailLoading.asReadonly();

  /** The last single-portal failure, classified, or `null`. */
  readonly detailFailure = this._detailFailure.asReadonly();

  /** The selected portal's configuration projection, or `null` when unread. */
  readonly settings = this._settings.asReadonly();

  /** Whether a settings read or write is in flight. */
  readonly settingsLoading = this._settingsLoading.asReadonly();

  /** The last settings failure, classified, or `null`. */
  readonly settingsFailure = this._settingsFailure.asReadonly();

  /**
   * The accounts the selected portal may designate as its administrator, or `null` when unread. ⚠ GATE ON
   * {@link PortalStore.administratorsPortalId} BEFORE OFFERING THESE. The candidate list belongs to the
   * portal it was read for, and a screen that moved to another portal would otherwise offer the previous
   * portal's administrators for a moment - long enough to submit one, which the server would then refuse
   * for a reason the operator could not see.
   */
  readonly administrators = this._administrators.asReadonly();

  /** Which portal the held candidate list belongs to, or `undefined` when none has been read. */
  readonly administratorsPortalId = this._administratorsPortalId.asReadonly();

  /** Whether a candidate read is in flight. */
  readonly administratorsLoading = this._administratorsLoading.asReadonly();

  /** The last candidate-read failure, classified, or `null`. */
  readonly administratorsFailure = this._administratorsFailure.asReadonly();

  /** Every host name of the portal named by {@link PortalStore.aliasesPortalId}, or `null` when unread. */
  readonly aliases = this._aliases.asReadonly();

  /** Which portal the held alias collection belongs to, or `undefined` when none has been read. */
  readonly aliasesPortalId = this._aliasesPortalId.asReadonly();

  /** The alias row an operator is editing, or `undefined` when none is selected. */
  readonly selectedAliasId = this._selectedAliasId.asReadonly();

  /** One alias row read on its own, or `null` when none has been. */
  readonly aliasDetail = this._aliasDetail.asReadonly();

  /** Whether an alias read or write is in flight. */
  readonly aliasLoading = this._aliasLoading.asReadonly();

  /** The last alias failure, classified, or `null`. */
  readonly aliasFailure = this._aliasFailure.asReadonly();

  /** Whether the current tenant's protected-facts read is in flight. */
  readonly contextLoading = this._contextLoading.asReadonly();

  // THE CURRENT TENANT'S PROTECTED FACTS — DERIVED
  // ⚠ ADVISORY, NOT ENFORCEMENT. The API refuses a protected write on its own terms and answers a problem
  // document; these exist so a screen does not OFFER what will be refused.

  /**
   * The current tenant's own record, or `null` before the first successful read. Exposed whole as well as
   * through the four facts below, because a screen that needs a fifth column should read it here rather
   * than have another projection added.
   */
  readonly currentPortal = this._context.asReadonly();

  /**
   * Whether the protected facts have been resolved from the server. ⚠ A SCREEN GUARDING A PROTECTED FLOW
   * MUST TEST THIS, and not merely test the facts for absence. `administratorRoleId` reads `null` both
   * when the tenant designates no administrator role — a real configuration in which nothing is protected
   * — and when the record has not been read yet.
   */
  readonly contextResolved = computed<boolean>(() => this._context() !== null);

  /** The account the current tenant designates as its administrator, or `null`. `Portals.AdministratorId`. */
  readonly administratorUserId = computed<number | null>(
    () => this._context()?.administratorId ?? null,
  );

  /**
   * The role the current tenant designates as conferring administration, or `null`.
   * `Portals.AdministratorRoleId`. ⚠ ZERO IS A REAL ROLE: `Roles.RoleID` is `IDENTITY(0, 1)`
   * (`01.00.00.SqlDataProvider:L114`), and the tenant's Administrators role is usually role zero
   * precisely because it is created first.
   */
  readonly administratorRoleId = computed<number | null>(
    () => this._context()?.administratorRoleId ?? null,
  );

  /**
   * The role every authenticated caller of the current tenant holds, or `null`.
   * `Portals.RegisteredRoleId`.
   */
  readonly registeredRoleId = computed<number | null>(
    () => this._context()?.registeredRoleId ?? null,
  );

  /**
   * Whether the current tenant has a payment processor configured. DEFECT 5, REPRODUCED RATHER THAN
   * REPAIRED. `EditRoles.ascx.vb:L104-L109` reads `If (objPortalInfo Is Nothing OrElse
   * String.IsNullOrEmpty(objPortalInfo.ProcessorUserId))` and then SHOWS the warning, while its own
   * comment says the warning appears when a processor IS configured.
   */
  readonly paymentProcessorConfigured = computed<boolean>(() => {
    const processorUserId: string | null | undefined = this._context()?.processorUserId;

    return processorUserId !== null && processorUserId !== undefined && processorUserId.length > 0;
  });

  // =========================================================================
  // DERIVED VIEWS
  // =========================================================================

  /** The rows on the page in hand, in the order the query produced them. */
  readonly portals = computed<readonly PortalListItem[]>(() => this._page().items);

  /** The paging facts locating the page in hand within the whole match set. */
  readonly listMeta = computed<ApiMeta>(() => this._page().meta);

  /**
   * The total no of records that satisfy the criteria, counted across every page and not only the page
   * returned.
   */
  readonly totalCount = computed<number>(() => this._page().meta.totalCount);

  /**
   * The size of the page that produced the rows in hand: the size the server actually applied, which can
   * differ from the size asked for once a request has been validated.
   */
  readonly servedPageSize = computed<number>(() => this._page().meta.pageSize);

  /** The number of pages the total divides into at the served page size. */
  readonly totalPages = computed<number>(() => this._page().meta.totalPages);

  /** The page the server actually served, counted from zero. */
  readonly servedPageIndex = computed<number>(() => this._page().meta.pageIndex);

  /** Whether the match set is empty - nothing matched at all. */
  readonly isListEmpty = computed<boolean>(() => this._page().meta.totalCount === 0);

  /** Whether records exist but the page in hand holds none - the requested page is past the end. */
  readonly isPastEnd = computed<boolean>(
    () => this._page().meta.totalCount > 0 && this._page().items.length === 0,
  );

  readonly pagerRequired = computed<boolean>(
    () => this._page().meta.pageSize < this._page().meta.totalCount,
  );

  /** The current ordering, as one value. */
  readonly sort = computed<PortalSort>(() => ({
    sortBy: this._sortBy(),
    sortDir: this._sortDir(),
  }));

  /** Whether the listing is restricted by a portal-name filter. */
  readonly isFiltered = computed<boolean>(() => this._nameFilter() !== null);

  /** Whether a portal is selected. */
  readonly hasSelection = computed<boolean>(() => this._selectedPortalId() !== undefined);

  /** Whether the alias collection has been read for the portal it belongs to. */
  readonly aliasesLoaded = computed<boolean>(() => this._aliases() !== null);

  /**
   * How many host names the portal has, or `null` when they have not been read. `null` rather than nought
   * for the unread state, preserving the three-state distinction the collection itself carries.
   */
  readonly aliasCount = computed<number | null>(() => {
    const held: readonly PortalAlias[] | null = this._aliases();

    return held === null ? null : held.length;
  });

  /**
   * The selected alias: the row from the collection in hand where it holds one, the individually read row
   * otherwise, and `null` when neither is available. The collection is preferred because it is the more
   * recently refreshed of the two after a write, and the fallback is what lets a screen reached directly
   * by address show a row before the collection has been read.
   */
  readonly selectedAlias = computed<PortalAlias | null>(() => {
    const chosen: number | undefined = this._selectedAliasId();

    if (chosen === undefined) {
      return null;
    }

    const held: readonly PortalAlias[] | null = this._aliases();

    if (held !== null) {
      const found: PortalAlias | undefined = held.find(
        (alias: PortalAlias) => alias.portalAliasId === chosen,
      );

      if (found !== undefined) {
        return found;
      }
    }

    const fetched: PortalAlias | null = this._aliasDetail();

    if (fetched !== null && fetched.portalAliasId === chosen) {
      return fetched;
    }

    return null;
  });

  /** Whether any request at all is in flight. */
  readonly busy = computed<boolean>(
    () =>
      this._listLoading() ||
      this._detailLoading() ||
      this._settingsLoading() ||
      this._administratorsLoading() ||
      this._aliasLoading(),
  );

  /** Whether any concern is reporting a failure. */
  readonly hasFailure = computed<boolean>(
    () =>
      this._listFailure() !== null ||
      this._detailFailure() !== null ||
      this._settingsFailure() !== null ||
      this._administratorsFailure() !== null ||
      this._aliasFailure() !== null,
  );

  // =========================================================================
  // COMMANDS - LISTING AND PAGING
  // =========================================================================

  /**
   * Reads the page described by the listing slices. Cancels any listing request already in flight, then
   * sends the page index, the page size preference, the ordering and the portal-name filter exactly as
   * they are held.
   */
  loadPortals(): void {
    this.listRequest?.unsubscribe();
    this._listLoading.set(true);
    this._listFailure.set(null);

    this.listRequest = this.portalApi
      .list(
        {
          pageIndex: this._pageIndex(),
          pageSize: this._requestedPageSize(),
          sortBy: this._sortBy(),
          sortDir: this._sortDir(),
        },
        { name: this._nameFilter() },
      )
      .subscribe({
        next: (received: PortalListPage) => {
          this._page.set(received);
          this._listLoading.set(false);
          this.listingRead = true;
        },
        error: (cause: unknown) => {
          this._listFailure.set(classifyFailure(cause));
          this._listLoading.set(false);
        },
      });
  }

  /**
   * Re-reads the current page without changing any listing slice. The same operation as {@link
   * PortalStore.loadPortals}, named for the intent of refreshing what is on screen after a write.
   */
  reloadPortals(): void {
    this.loadPortals();
  }

  /**
   * Re-reads the listing ONLY when a listing read has already completed. ⚠ THE MEASURED DEFECT THIS
   * EXISTS TO CLOSE. The settings save used to call the unconditional refresh above, so every save issued
   * `GET /api/v1/portals?pageIndex=0` - measured six times for six saves, an exact one-for-one pairing.
   */
  refreshListingIfRead(): void {
    if (!this.listingRead) {
      return;
    }
    this.loadPortals();
  }

  /**
   * Folds a stored settings projection back into the selected portal, when it is the same one. ⚠ THE
   * MEASURED DEFECT THIS EXISTS TO CLOSE. A settings save updated the settings slice and re-read the
   * listing, and left the DETAIL slice holding what it had read before the write.
   *
   * @param stored The projection the server echoed back from the write.
   */
  private reconcileSelectedPortal(stored: PortalSettings): void {
    const held: PortalDetail | null = this._selectedPortal();

    if (held === null || held.portalId !== stored.portalId) {
      return;
    }

    this._selectedPortal.set({ ...held, ...stored });
  }

  /**
   * Moves to a page and reads it.
   *
   * @param pageIndex The page of records to return, counted from ZERO: 0 is the first page.
   */
  goToPage(pageIndex: number): void {
    this._pageIndex.set(pageIndex);
    this.loadPortals();
  }

  /**
   * Changes the page size preference and re-reads from the first page. The first page rather than the
   * current one, because the page an operator was on addresses different records once the window changes,
   * so holding the index would move them somewhere they did not ask to go.
   *
   * @param pageSize The size of the page to ask for, or `null` to express no preference and let the
   * server apply its own default.
   */
  setPageSize(pageSize: number | null): void {
    this._requestedPageSize.set(pageSize);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this.loadPortals();
  }

  /**
   * Restricts the listing to portals matching a name, and reads the first page. returning to the first
   * page reproduces the legacy behaviour exactly rather than adding a convenience.
   *
   * @param name The operator's text, exactly as typed.
   */
  setNameFilter(name: string | null): void {
    this._nameFilter.set(name);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this.loadPortals();
  }

  /** Removes the name filter and reads the first page. This is what the letter strip's "All" entry calls. */
  clearNameFilter(): void {
    this.setNameFilter(null);
  }

  /**
   * Orders the listing and reads the first page. The first page for the same reason a page-size change
   * returns to it: a row's page depends on the ordering.
   *
   * @param sort The field to order by and the direction to apply, either member `null` to express no
   * preference.
   */
  setSort(sort: PortalSort): void {
    this._sortBy.set(sort.sortBy);
    this._sortDir.set(sort.sortDir);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this.loadPortals();
  }

  /** Returns the listing to the server's own ordering and reads the first page. */
  clearSort(): void {
    this.setSort({ sortBy: null, sortDir: null });
  }

  /** @param query The whole listing query. */
  applyListQuery(query: PortalListQuery): void {
    this._pageIndex.set(query.pageIndex);
    this._requestedPageSize.set(query.pageSize);
    this._nameFilter.set(query.name);
    this._sortBy.set(query.sortBy);
    this._sortDir.set(query.sortDir);
    this.loadPortals();
  }

  // =========================================================================
  // COMMANDS - SELECTION AND ONE PORTAL
  // =========================================================================

  /**
   * Records which portal an operator is working on, without requesting anything. Pairs with {@link
   * PortalStore.loadSelectedPortal}, {@link PortalStore.loadSettings} and {@link
   * PortalStore.loadAliases}, so a screen can adopt a selection from its route and then read only the
   * parts it shows.
   *
   * @param portalId The portal to select.
   */
  selectPortal(portalId: number): void {
    if (this._selectedPortalId() === portalId) {
      return;
    }

    this._selectedPortalId.set(portalId);
    this.discardPortalScopedState();
  }

  /** Clears the selection and everything held about the portal that was selected. */
  clearSelection(): void {
    this._selectedPortalId.set(undefined);
    this.discardPortalScopedState();
  }

  /**
   * Selects a portal and reads it in full.
   *
   * @param portalId The portal to read.
   */
  loadPortal(portalId: number): void {
    this.selectPortal(portalId);
    this.readPortal(portalId);
  }

  /** Re-reads the selected portal. */
  loadSelectedPortal(): void {
    const chosen: number | undefined = this._selectedPortalId();

    if (chosen === undefined) {
      return;
    }

    this.readPortal(chosen);
  }

  /**
   * Creates a portal. The request is handed to the transport exactly as the caller composed it and is NOT
   * retained: a create request carries the first administrator's password, and application state is no
   * place for it.
   *
   * @param request The portal to create, with its first host name and the administrator account to
   * establish alongside it.
   * @returns A ticket emitting the created portal once, after the state above has settled, and completing
   * without emitting if the write fails.
   */
  createPortal(request: CreatePortalRequest): Observable<PortalDetail> {
    const outcome = PortalStore.outcomeTicket<PortalDetail>();

    this._detailLoading.set(true);
    this._detailFailure.set(null);

    this.track(
      this.portalApi.create(request).subscribe({
        next: (created: PortalDetail) => {
          this._selectedPortalId.set(created.portalId);
          this._selectedPortal.set(created);
          this._settings.set(null);
          this._aliases.set(null);
          this._aliasesPortalId.set(undefined);
          this._selectedAliasId.set(undefined);
          this._aliasDetail.set(null);
          this._detailLoading.set(false);

          // ⚠ THE LISTING IS DELIBERATELY NOT RE-READ HERE. This command's caller navigates to the listing
          // on success, and the listing issues its own read from its address on entry and on every address
          // change - the one place it is issued, as that subscription's own block records - so asking for
          // the rows here produced two identical reads for one create.

          // Published LAST, so a continuation cannot observe half-settled state.
          outcome.next(created);
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._detailFailure.set(classifyFailure(cause));
          this._detailLoading.set(false);
          // Completes empty rather than erroring: the failure already reaches the screen
          // through the failure slice, and erroring would report it a second time.
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  /**
   * Replaces one portal's editable state. The identifier travels in the path and on the body, because the
   * write contract declares its own and the server requires the two to agree.
   *
   * @param portalId The portal to write.
   * @param request The complete editable state to store.
   * @returns A ticket emitting the stored portal once.
   */
  updatePortal(portalId: number, request: UpdatePortalRequest): Observable<PortalDetail> {
    const outcome = PortalStore.outcomeTicket<PortalDetail>();

    this._detailLoading.set(true);
    this._detailFailure.set(null);

    this.track(
      this.portalApi.update(portalId, request).subscribe({
        next: (stored: PortalDetail) => {
          this._selectedPortal.set(stored);
          this._detailLoading.set(false);

          outcome.next(stored);
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._detailFailure.set(classifyFailure(cause));
          this._detailLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  /**
   * @param portalId The portal to remove.
   * @returns A ticket emitting once after the removal has settled.
   */
  deletePortal(portalId: number): Observable<void> {
    const outcome = PortalStore.outcomeTicket<void>();

    this._detailLoading.set(true);
    this._detailFailure.set(null);

    this.track(
      this.portalApi.delete(portalId).subscribe({
        next: () => {
          this._page.update((held: PortalListPage) => ({
            items: held.items.filter((row: PortalListItem) => row.portalId !== portalId),
            meta: held.meta,
          }));

          if (this._selectedPortalId() === portalId) {
            this._selectedPortalId.set(undefined);
            this.discardPortalScopedState();
          }

          this._detailLoading.set(false);
          this.reloadPortals();

          outcome.next();
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._detailFailure.set(classifyFailure(cause));
          this._detailLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  // =========================================================================
  // COMMANDS - SETTINGS PROJECTION
  // =========================================================================

  /**
   * Reads one portal's configuration projection. Cancels any settings request already in flight, for the
   * same reason a listing request is cancelled: a slower earlier response must not land on top of a
   * faster later one and leave a form bound to the wrong portal's values.
   *
   * @param portalId The portal whose settings to read.
   */
  loadSettings(portalId: number): void {
    this.selectPortal(portalId);
    this.settingsRequest?.unsubscribe();
    this._settingsLoading.set(true);
    this._settingsFailure.set(null);

    this.settingsRequest = this.portalApi.getSettings(portalId).subscribe({
      next: (received: PortalSettings) => {
        this._settings.set(received);
        this._settingsLoading.set(false);
      },
      error: (cause: unknown) => {
        this._settingsFailure.set(classifyFailure(cause));
        this._settingsLoading.set(false);
      },
    });
  }

  /**
   * Replaces one portal's configuration projection. The whole projection is written, because the endpoint
   * publishes no single-setting write and no partial patch.
   *
   * @param portalId The portal to write.
   * @param request The complete settings state to store.
   * @returns A ticket emitting the stored projection once.
   */
  saveSettings(
    portalId: number,
    request: UpdatePortalSettingsRequest,
  ): Observable<PortalSettings> {
    const outcome = PortalStore.outcomeTicket<PortalSettings>();

    this._settingsLoading.set(true);
    this._settingsFailure.set(null);

    this.track(
      this.portalApi.updateSettings(portalId, request).subscribe({
        next: (stored: PortalSettings) => {
          this._settings.set(stored);
          this._settingsLoading.set(false);
          this.reconcileSelectedPortal(stored);
          this.refreshListingIfRead();

          outcome.next(stored);
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._settingsFailure.set(classifyFailure(cause));
          this._settingsLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  /**
   * Reads the accounts one portal may designate as its administrator. fills the selector the legacy
   * screen built at `Website/admin/Portal/SiteSettings.ascx.vb:L331-L336` from the members of the
   * portal's own administrator role.
   *
   * @param portalId The portal whose eligible administrators to read.
   */
  loadAdministrators(portalId: number): void {
    this.administratorsRequest?.unsubscribe();
    this._administratorsLoading.set(true);
    this._administratorsFailure.set(null);

    this.administratorsRequest = this.portalApi.listAdministrators(portalId).subscribe({
      next: (received: readonly PortalAdministrator[]) => {
        this._administrators.set(received);
        this._administratorsPortalId.set(portalId);
        this._administratorsLoading.set(false);
      },
      error: (cause: unknown) => {
        this._administrators.set(null);
        this._administratorsPortalId.set(undefined);
        this._administratorsFailure.set(classifyFailure(cause));
        this._administratorsLoading.set(false);
      },
    });
  }

  // =========================================================================
  // COMMANDS - HOST-NAME ALIASES (UNPAGED)
  // =========================================================================

  /**
   * Reads every host name bound to one portal. UNPAGED: no page index, no page size, no ordering and no
   * filter is sent, because the server binds none of them for this collection and a parameter it does not
   * bind is discarded silently, leaving a caller believing it had asked for something it had not.
   *
   * @param portalId The portal whose host names to read.
   */
  loadAliases(portalId: number): void {
    this.selectPortal(portalId);
    this.aliasRequest?.unsubscribe();
    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.aliasRequest = this.portalApi.listAliases(portalId).subscribe({
      next: (received: readonly PortalAlias[]) => {
        this._aliases.set(received);
        this._aliasesPortalId.set(portalId);
        this._aliasLoading.set(false);
      },
      error: (cause: unknown) => {
        this._aliasFailure.set(classifyFailure(cause));
        this._aliasLoading.set(false);
      },
    });
  }

  /**
   * Records which alias row an operator is editing, without requesting anything. The successor of
   * `ViewState("PortalAliasID")` - see {@link PortalStore.selectedAliasId}.
   *
   * @param portalAliasId The alias row to select.
   */
  selectAlias(portalAliasId: number): void {
    if (this._selectedAliasId() === portalAliasId) {
      return;
    }

    this._selectedAliasId.set(portalAliasId);
    this._aliasDetail.set(null);
  }

  clearAliasSelection(): void {
    this._selectedAliasId.set(undefined);
    this._aliasDetail.set(null);
  }

  /**
   * Reads one alias row on its own. For a screen reached directly by address, which holds an identifier
   * but not the collection.
   *
   * @param portalId The portal that owns the row.
   * @param portalAliasId The row to read.
   */
  loadAlias(portalId: number, portalAliasId: number): void {
    this.selectPortal(portalId);
    this._selectedAliasId.set(portalAliasId);
    this.aliasRequest?.unsubscribe();
    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.aliasRequest = this.portalApi.getAlias(portalId, portalAliasId).subscribe({
      next: (received: PortalAlias) => {
        this._aliasDetail.set(received);
        this._aliasLoading.set(false);
      },
      error: (cause: unknown) => {
        this._aliasFailure.set(classifyFailure(cause));
        this._aliasLoading.set(false);
      },
    });
  }

  /**
   * Binds an additional host name to one portal. The server answers with the created row, carrying the
   * identifier the database assigned, so it is appended to the collection in hand IMMUTABLY - a new
   * array, never a push - which is what makes a change-detection-on-push grid re-render.
   *
   * @param portalId The portal to bind the host name to.
   * @param request The host name to bind.
   * @returns A ticket emitting the created row once.
   */
  createAlias(
    portalId: number,
    request: CreatePortalAliasRequest,
  ): Observable<PortalAlias> {
    const outcome = PortalStore.outcomeTicket<PortalAlias>();

    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.track(
      this.portalApi.createAlias(portalId, request).subscribe({
        next: (created: PortalAlias) => {
          this._aliasDetail.set(created);
          this._selectedAliasId.set(created.portalAliasId);
          this._aliasLoading.set(false);

          this.loadAliases(portalId);

          outcome.next(created);
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._aliasFailure.set(classifyFailure(cause));
          this._aliasLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  /**
   * Changes the host name one alias binds. The endpoint answers with the stored row, so the record just
   * written is adopted from the response rather than reconstructed from the request.
   *
   * @param portalId The portal that owns the row.
   * @param portalAliasId The row to change.
   * @param request The host name to store in place of the current one.
   * @returns A ticket emitting the stored row once, after the write has been stored and the collection
   * re-read.
   */
  updateAlias(
    portalId: number,
    portalAliasId: number,
    request: UpdatePortalAliasRequest,
  ): Observable<PortalAlias> {
    const outcome = PortalStore.outcomeTicket<PortalAlias>();

    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.track(
      this.portalApi.updateAlias(portalId, portalAliasId, request).subscribe({
        next: (stored: PortalAlias) => {
          this._aliasDetail.set(stored);
          this._aliasLoading.set(false);
          this.loadAliases(portalId);

          outcome.next(stored);
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._aliasFailure.set(classifyFailure(cause));
          this._aliasLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  /**
   * Unbinds one alias from one portal. Answered with no body.
   *
   * @param portalId The portal that owns the row.
   * @param portalAliasId The row to unbind.
   * @returns A ticket emitting once after the removal has settled.
   */
  deleteAlias(portalId: number, portalAliasId: number): Observable<void> {
    const outcome = PortalStore.outcomeTicket<void>();

    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.track(
      this.portalApi.deleteAlias(portalId, portalAliasId).subscribe({
        next: () => {
          this._aliases.update((held: readonly PortalAlias[] | null) =>
            held === null
              ? null
              : held.filter((alias: PortalAlias) => alias.portalAliasId !== portalAliasId),
          );

          if (this._selectedAliasId() === portalAliasId) {
            this._selectedAliasId.set(undefined);
            this._aliasDetail.set(null);
          }

          this.loadAliases(portalId);

          outcome.next();
          outcome.complete();
        },
        error: (cause: unknown) => {
          this._aliasFailure.set(classifyFailure(cause));
          this._aliasLoading.set(false);
          outcome.complete();
        },
      }),
    );

    return outcome.asObservable();
  }

  // =========================================================================
  // COMMANDS - FAILURE AND LIFECYCLE
  // =========================================================================

  /** Discards every held failure without touching any data slice. */
  /**
   * Reads the protected facts of the tenant the caller is SIGNED IN TO. ⚠ IDEMPOTENT BY DESIGN, AND EVERY
   * SCREEN THAT NEEDS THE FACTS CALLS IT. A second call for a tenant already held returns without a
   * request, and a call made while the read is in flight does the same, so four screens may each ask on
   * initialisation and exactly one request is issued.
   *
   * @param portalId The tenant the caller is signed in to.
   */
  loadCurrentPortalContext(portalId: number): void {
    if (this._contextPortalId() === portalId && (this._context() !== null || this._contextLoading())) {
      return;
    }

    // A change of tenant discards the previous tenant's facts NOW rather than on arrival, so that no screen
    // can read one tenant's protected role while another tenant's key is published. A re-read of the same
    // tenant keeps what is in hand.
    if (this._contextPortalId() !== portalId) {
      this._context.set(null);
    }

    this._contextPortalId.set(portalId);
    this.contextRequest?.unsubscribe();
    this._contextLoading.set(true);

    this.contextRequest = this.portalApi.getById(portalId).subscribe({
      next: (received: PortalDetail) => {
        this._context.set(received);
        this._contextLoading.set(false);
      },
      error: () => {
        // Absorbed. See the note above: an unresolved fact leaves the API's own refusal as
        // the operative rule, which is strictly better than a banner nobody asked for.
        this._contextLoading.set(false);
      },
    });
  }

  clearFailures(): void {
    this._listFailure.set(null);
    this._detailFailure.set(null);
    this._settingsFailure.set(null);
    this._administratorsFailure.set(null);
    this._aliasFailure.set(null);
  }

  /**
   * Returns every slice to the state it held before the first request. For a sign-out, so that one
   * operator's portals, selection and host names are not visible to the next.
   */
  reset(): void {
    this.cancelReads();
    this.cancelWrites();

    this._page.set(EMPTY_PORTAL_PAGE);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this._requestedPageSize.set(null);
    this._nameFilter.set(null);
    this._sortBy.set(null);
    this._sortDir.set(null);
    this._listLoading.set(false);

    // ⚠ THE LATCH GOES WITH THE SLICE IT DESCRIBES, AND LEAVING IT BEHIND REOPENED A DEFECT THIS STORE HAD
    // ALREADY CLOSED ONCE. It records that a listing read COMPLETED, and every other member of the listing
    // slice above is cleared here - so a latch that survived said "a listing is in hand" about a page that
    // had just been emptied, for a session that no longer existed.
    this.listingRead = false;

    this._selectedPortalId.set(undefined);
    this._selectedPortal.set(null);
    this._detailLoading.set(false);

    this._settings.set(null);
    this._settingsLoading.set(false);

    this._administrators.set(null);
    this._administratorsPortalId.set(undefined);
    this._administratorsLoading.set(false);

    this._aliases.set(null);
    this._aliasesPortalId.set(undefined);
    this._selectedAliasId.set(undefined);
    this._aliasDetail.set(null);
    this._aliasLoading.set(false);

    // The protected facts go with the rest. They describe the tenant of the session being discarded, so
    // leaving them would let the next operator's screens protect the previous operator's roles - and would
    // suppress the fresh read that gets it right.
    this._contextPortalId.set(undefined);
    this._context.set(null);
    this._contextLoading.set(false);

    this.clearFailures();
  }

  /**
   * Releases every request handle when the injector holding this store is destroyed. A root-provided
   * store lives as long as the application, so this runs on teardown - which matters for a test, where
   * each specification builds its own injector and a request left listening across that boundary would
   * report into a store the next specification has replaced.
   */
  ngOnDestroy(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  // =========================================================================
  // PRIVATE
  // =========================================================================

  /**
   * Reads one portal in full, without touching the selection. The body shared by {@link
   * PortalStore.loadPortal} and {@link PortalStore.loadSelectedPortal}, so that the cancellation and the
   * loading-and-failure bookkeeping exist once.
   *
   * @param portalId The portal to read.
   */
  private readPortal(portalId: number): void {
    this.detailRequest?.unsubscribe();
    this._detailLoading.set(true);
    this._detailFailure.set(null);

    this.detailRequest = this.portalApi.getById(portalId).subscribe({
      next: (received: PortalDetail) => {
        this._selectedPortal.set(received);
        this._detailLoading.set(false);
      },
      error: (cause: unknown) => {
        this._detailFailure.set(classifyFailure(cause));
        this._detailLoading.set(false);
      },
    });
  }

  /** Discards everything that belongs to one particular portal. */
  private discardPortalScopedState(): void {
    this.detailRequest?.unsubscribe();
    this.detailRequest = null;
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = null;
    this.aliasRequest?.unsubscribe();
    this.aliasRequest = null;

    this._selectedPortal.set(null);
    this._settings.set(null);
    this._aliases.set(null);
    this._aliasesPortalId.set(undefined);
    this._selectedAliasId.set(undefined);
    this._aliasDetail.set(null);

    this._detailLoading.set(false);
    this._settingsLoading.set(false);
    this._aliasLoading.set(false);

    this._detailFailure.set(null);
    this._settingsFailure.set(null);
    this._aliasFailure.set(null);
  }

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

  /**
   * The ticket every write in this class returns, in place of accepting a caller's callback. WHAT THIS
   * REPLACES, AND WHY IT WAS WORTH REPLACING Each of the seven writes here used to take an optional
   * continuation — `onCreated`, `onUpdated`, `onSaved`, `onDeleted` — and invoke it from inside its
   * response handler.
   *
   * @returns A fresh, operation-scoped subject for one write.
   */
  private static outcomeTicket<T>(): AsyncSubject<T> {
    return new AsyncSubject<T>();
  }

  /** Cancels every read in flight and forgets its handle. */
  private cancelReads(): void {
    this.listRequest?.unsubscribe();
    this.listRequest = null;
    this.detailRequest?.unsubscribe();
    this.detailRequest = null;
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = null;
    this.administratorsRequest?.unsubscribe();
    this.administratorsRequest = null;
    this.aliasRequest?.unsubscribe();
    this.aliasRequest = null;
    this.contextRequest?.unsubscribe();
    this.contextRequest = null;
  }

  /** Releases every write handle. */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();
  }
}

// CLOSING MIGRATION NOTES
// MIGRATION: OFFSET PAGING ONLY, and every alternative is deliberately absent. The only coordinates
// anywhere above are a page index and a page size.
