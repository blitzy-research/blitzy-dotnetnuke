//
// Portal (tenant) state for the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE IS
// ---------------------------------------------------------------------------
// One root-provided signal store owning four slices of portal state: the PAGED
// portal listing with its name filter, the selected portal in full, that portal's
// settings projection, and its deliberately UNPAGED host-name aliases.
//
// It is the COMPOSITION layer. `core/services/portal.service.ts` is a typed
// transport - one member per endpoint, returning the observable the framework
// produced, subscribing to nothing - and it says so of itself: "Return the
// observable and let the state layer subscribe." Angular services are confined to
// API communication by the migration discipline, so everything a transport may not
// do lives here instead:
//
//   * subscribing, and cancelling an in-flight request that a newer one supersedes;
//   * sequencing two calls into one operation (a write followed by the read that
//     confirms it, where the endpoint answers with no body);
//   * holding the loading and structured-failure state of each concern;
//   * optimistic removal from a collection already in hand;
//   * deriving projections over held state.
//
// And, symmetrically, everything this file may NOT do, each of which has an owner:
//
//   * BUILD A REQUEST. No URL, no query string, no header, no transport type. Route
//     templates belong to the endpoint-template registry under `core/config/`, query
//     parameters to the parameter-serialising utility under `core/utils/`, and the
//     correlation identifier, bearer token and failure translation to the three
//     interceptors in `core/interceptors/`. NONE of those modules is imported here,
//     and their names are deliberately not written out above so that a search for an
//     illegitimate dependency on one of them finds nothing in this file.
//   * VALIDATE. Request shape is checked by the typed reactive forms under
//     `features/portal/**` and by the server's own validators; re-checking here
//     would give this caller a different answer from every other caller.
//   * DECIDE A PERMISSION. The server is authoritative. `core/guards/` imports this
//     store; this store imports no guard.
//   * FORMAT FOR DISPLAY. No wording is composed here and no markup is produced or
//     held. Wording belongs to `core/utils/form-errors.util.ts`, which this file
//     consults for CLASSIFICATION only, and rendering to the shared components.
//   * OWN THE PAGER. See the note on {@link PortalStore.pagerRequired}.
//
// ---------------------------------------------------------------------------
// PROVENANCE
// ---------------------------------------------------------------------------
// The state this file holds is the state five Web Forms code-behinds held in the
// page instance, the query string and `ViewState`:
//
//   Website/admin/Portal/Portals.ascx.vb          the listing, its filter and its page
//   Website/admin/Portal/SiteSettings.ascx.vb     the settings projection
//   Website/admin/Portal/Signup.ascx.vb           portal creation
//   Website/admin/Portal/PortalAlias.ascx.vb      the unpaged alias collection
//   Website/admin/Portal/EditPortalAlias.ascx.vb  one alias, and the selected-row identifier
//
// with the data-layer semantics taken from Library/Components/Portal/PortalController.vb,
// the sentinel contract from Library/Components/Shared/Null.vb, and the identity
// seeds and legacy tenant-resolution predicate from
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider.
//
// MIGRATION: `ViewState` and `Session` are eliminated entirely, and the two halves
//   of that statement are not symmetrical.
//
//   `ViewState` genuinely existed and genuinely carried the ancestors of the slices
//   below. EditPortalAlias.ascx.vb:L62-L63 wrote `ViewState.Add("PortalAliasID", …)`
//   and `ViewState.Add("PortalID", …)`, adding the portal again at L79 and L85, and
//   read the alias identifier back at L176 and L220 and the portal identifier at
//   L221 and L234 - twenty-one raw `ViewState` references across the five portal
//   code-behinds, against two hundred and nineteen across the whole administration
//   tree. Those become {@link PortalStore.selectedPortalId} and
//   {@link PortalStore.selectedAliasId}: two signals, no serialised control tree, no
//   hidden form field and no round trip. There is deliberately NO general key/value
//   bag and no serialise/deserialise pair - a typed slice per concern is the point.
//
//   `ViewState("UrlReferrer")` is the one entry that does NOT become a signal.
//   EditPortalAlias.ascx.vb captured the referring address at L133, defaulted it to
//   the empty string at L135, and redirected to it on cancel at L159, on delete at
//   L189 and after an update at L243; SiteSettings.ascx.vb does the same at
//   L482-L577. That is RETURN NAVIGATION, which is the Angular router's concern -
//   the router already holds where a screen came from - so reproducing it as store
//   state would give the application two answers to one question.
//
//   `Session(` has ZERO occurrences: not one in Website/ and not one in Library/.
//   The migration plan lists "Session state" among this file's sources, and that
//   requirement is therefore satisfied vacuously. Measured, reported, and NOT
//   substituted for - no session analogue is manufactured here.
//
// MIGRATION: none of the legacy caching is reproduced client-side. The legacy portal
//   controller reached the shared cache thirteen times - PortalController.vb:L211
//   reads through `DataCache.GetCache(key)`, L218 computes an expiry as a per-entity
//   timeout multiplied by a global performance setting, L240 writes with
//   `DataCache.SetCache`, and L1232/L1246 repeat the pair - and invalidated whole
//   scopes at a stroke, at L916 and L1573 with `DataCache.ClearPortalCache(PortalId,
//   True)` and at L1128 and L1205 with `DataCache.ClearHostCache(True)`. That is one
//   of one hundred and sixteen in-scope call sites into the 317-line
//   Library/Components/Providers/Caching/DataCache.vb. Caching is now a server
//   concern behind a named-key cache service with explicit invalidation. There is no
//   cache map, no time to live, no expiry stamp and no staleness flag below, so
//   nothing here can serve a portal the server has since changed.
//   (The migration plan cites this file under Library/Components/Shared/, where no
//   such file exists; the path above is the real one.)
//
// MIGRATION: localisation is not ported. The legacy screens resolved every label
//   through `Localization.GetString` against a per-control resource file, and this
//   application authors its English wording directly in its templates with those
//   resource files as the reference. No translation runtime is added and no
//   message identifier appears here.
//

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
 * Everything a consumer needs to know about one failed operation, classified once.
 *
 * The store holds the STRUCTURED problem document rather than a sentence, and this
 * contract is the classified view over it. Every member is either the document
 * itself or a value obtained from `core/utils/form-errors.util.ts`, which owns
 * classification; not one member is composed here, and no member is markup.
 *
 * MIGRATION: a refusal is a WARNING and a fault is an ERROR, and the legacy
 * application is the authority for the distinction rather than a house style.
 * Website/admin/Security/AccessDenied.ascx.vb performs no permission check at all -
 * it only presents a denial - and BOTH of its branches render at
 * `ModuleMessage.ModuleMessageType.YellowWarning`: L43 for the message handed in
 * through the query string and L45 for the localised default. A `403` therefore
 * resolves to `'warning'` here, through {@link ProblemSeverity}, and never to danger
 * styling that would tell an operator something is broken when the system is working
 * exactly as configured.
 */
export interface PortalFailure {
  /**
   * The RFC 7807 document for this failure. NEVER `null`: a failure that carried no
   * document - a request that never reached the server, or a response whose body is not a
   * problem document - is given one composed from its status by `transportProblem`.
   *
   * A document received from the server is held whole and unaltered. That is what
   * preserves `traceId` and `correlationId` for an operator: the two are independent
   * identifiers in two different formats, the second being the support reference that also
   * appears on the response header and in the server's own log, and discarding either
   * would leave a browser-side report with no join key to a server-side one.
   *
   * ⚠ THE NON-NULLABILITY IS LOAD-BEARING FOR THE CONSUMER'S TEMPLATE. It is what lets a
   * screen present every failure through the one shared banner - severity word, title,
   * detail, field errors and support reference - instead of falling through to a bare
   * sentence for the two modes that carry no body, which is what runtime testing found
   * happening and could not tell apart.
   */
  readonly problem: ProblemDetails;

  /**
   * Whether {@link PortalFailure.problem} was COMPOSED from the transport status rather than
   * published by the server.
   *
   * ⚠ A CONSUMER WITH ITS OWN WORDING FOR THIS OPERATION MUST CHECK THIS BEFORE PREFERRING
   * THE DOCUMENT'S DETAIL. A composed document carries a truthful but generic sentence
   * derived from the status alone, so a screen that has the wording its legacy predecessor
   * used - "An error was encountered during the creation of your portal…" - has something
   * strictly better to show and should show it. A document the server published always wins,
   * because it knows something this code does not.
   *
   * The flag is carried here rather than inferred from the document because the obvious
   * inference is wrong: `type: 'about:blank'` is what RFC 7807 §4.2 designates for a problem
   * with no semantics beyond its status, and this API's own error handler legitimately sends
   * exactly that for several refusals - so keying on it would treat real server documents as
   * composed ones.
   */
  readonly synthesised: boolean;

  /**
   * The status to classify by: the document's own status where it carried one, and
   * otherwise the transport status of the failed response.
   *
   * `null` when neither was available. Deliberately a plain number rather than a
   * union of the statuses this API is known to return, matching the decision the
   * problem-details contract already documents: the status is the server's to
   * choose, and a union would turn an unforeseen one into a compile error at the
   * consumer.
   */
  readonly status: number | null;

  /** How forcefully to present the failure. Decided by {@link problemSeverity}. */
  readonly severity: ProblemSeverity;

  /**
   * The state-refusal code the server published, or `null` when the failure was
   * not one of the recognised refusals.
   *
   * The three this store can receive are the last-remaining-portal refusal, answered
   * to a portal delete; the duplicate-host-name refusal, answered to an alias create
   * or update; and the active-alias refusal, answered to an alias update or delete
   * when the row addressed is the one the request resolved the tenant through. The
   * last two both arrive as `409`, so the code and never the status is what tells
   * them apart - see `isDuplicateAliasCode` and `isAliasInUseCode`.
   *
   * MIGRATION: THREE LEGACY RESOURCE KEYS, NONE OF WHICH IS THE VALUE COMPARED HERE.
   * Named in full so the parity claim stays checkable, each with where it was
   * measured:
   *
   * - `LastPortal`, read by `Library/Components/Portal/PortalController.vb:L200`
   *   through `Localization.GetString("LastPortal")` with no local resource file,
   *   because the entry is GLOBAL - `Website/App_GlobalResources/SharedResources.resx`
   *   declares it at L942 as `<data name="LastPortal.Text">`. Recorded as a
   *   divergence from the requirements, which spell this identifier
   *   `Portal.LastPortal`: that spelling occurs nowhere in the repository, and the
   *   measured key is the unqualified `LastPortal`. Neither spelling is a wire value.
   * - `DuplicatePortalAlias`, read by `Website/admin/Portal/Signup.ascx.vb:L263`.
   * - `DuplicateAlias`, read by `Website/admin/Portal/EditPortalAlias.ascx.vb:L226`
   *   and again at L238.
   *
   * The API publishes its own reason codes instead. The two legacy alias keys
   * collapse onto ONE of them, because the server reports one code for the collision
   * however it was reached, and the mapping from every legacy key to the published
   * code is stated once in `core/utils/form-errors.util.ts` and consulted from here.
   * No code string is written into this file, so this file cannot drift from that
   * mapping - which is also why comparing against a hard-coded legacy key here would
   * be a defect that no compiler would catch.
   */
  readonly conflictCode: ConflictCode | null;

  /**
   * The same document, narrowed, when it carries per-field failures; `null`
   * otherwise.
   *
   * Read the dictionary with BRACKET access - `problem.errors['PortalName']` - and
   * never with dot access, which is a compile error in this workspace by design.
   * The keys are the server's model-state keys and are Pascal-cased, because they
   * name model members rather than JSON members.
   */
  readonly validation: ValidationProblemDetails | null;

  /**
   * The identifier an operator quotes when reporting this failure, or `null` when
   * the document carried none.
   *
   * Surfaced because it is the only join key between something a person saw in a
   * browser and a request the server logged.
   */
  readonly supportReference: string | null;
}

/**
 * The ordering to apply to the portal listing, or its deliberate absence.
 *
 * `null` in either member means "no preference", which is transmitted as an
 * omission so that the server's own ordering applies. The direction tokens are the
 * server's member names, reproduced by the paging contract verbatim because the
 * binder accepts the member name and rejects an abbreviated or lower-cased
 * spelling.
 */
export interface PortalSort {
  /** The field to order by, or `null` for the server's own ordering. */
  readonly sortBy: string | null;

  /** The direction to apply, or `null` for the server's default. */
  readonly sortDir: SortDirection | null;
}

/**
 * A COMPLETE listing query: which page, how large, filtered how, ordered how.
 *
 * Applied as one unit by {@link PortalStore.applyListQuery}, which is why it is an interface
 * rather than four arguments - the four coordinates are restored together when a screen keeps
 * them in its address, and applying them one at a time would issue one request per coordinate
 * and reset the page index three times on the way.
 */
export interface PortalListQuery extends PortalSort {
  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /** The size to ask for, or `null` to express no preference and take the server's default. */
  readonly pageSize: number | null;

  /** The name filter, or `null` for no filter. Forwarded byte for byte. */
  readonly name: string | null;
}

// ---------------------------------------------------------------------------
// FAILURE READING
//
// Two structural readers and one classifier, module-private.
//
// WHY STRUCTURAL AND NOT BY TYPE. `core/interceptors/error.interceptor.ts` announces
// a failure and then RE-THROWS the original value, so what arrives at a subscriber
// here is the framework's own failed-response object. This module may not import a
// transport type - that is the boundary the migration discipline draws around a state
// layer, and it is the same boundary that keeps a URL, a query string and a header
// out of this file - so the two members that are needed are read by shape instead of
// by class. Both readers widen to `unknown` first, which is what forces every read
// below them to be guarded.
//
// WHAT THEY DELIBERATELY DO NOT DO. They do not parse a string body. The interceptor
// already handles that case - a proxy answering with an HTML page rather than the API
// answering with a document - and it is the layer that owns unwrapping. Duplicating
// the parse here would put two implementations of one decision in the workspace, and
// would gain nothing: every portal call asks for JSON, so a document that exists
// arrives already parsed, and an HTML page is not a document under any parse.
// ---------------------------------------------------------------------------

/**
 * Reads the transport status out of a failed operation.
 *
 * Read FIRST, before the body, and the ordering is load-bearing rather than tidy -
 * see {@link readProblem}.
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
 * Reads the RFC 7807 document out of a failed operation.
 *
 * ONLY THE BODY IS TESTED, never the response object around it, and that is a
 * correctness requirement rather than a preference. The narrowing predicate is
 * deliberately permissive about absence - any subset of the standard members is a
 * legal document - so a failed response, which carries a numeric `status` of its own,
 * would itself satisfy the predicate. Testing the wrapper would therefore store the
 * transport object as though it were the server's document, and every consumer would
 * then read a `detail` and a `title` that were never sent.
 *
 * A transport status of ZERO short-circuits before the body is read, for the same
 * reason the interceptor does it in that order: the framework puts a DOM progress
 * event in the body slot when no response arrived, and a progress event carries a
 * string `type`, which is one of the members the predicate accepts. Reading the body
 * first would mistake an unreachable server for a document that happens to say
 * nothing.
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
 * Every judgement is delegated to `core/utils/form-errors.util.ts`: the severity, the
 * refusal code, the validation narrowing and the support reference. Nothing is
 * decided here and no wording is composed, which is what keeps this a structured
 * value rather than a rendered one.
 *
 * @param cause The value the subscriber's failure channel delivered.
 * @returns The classified failure.
 */
function classifyFailure(cause: unknown): PortalFailure {
  const status: number | null = readStatus(cause);
  const problem: ProblemDetails | null = readProblem(cause, status);

  // The document's own status where it carried one, and the transport status
  // otherwise. The two agree for every response this API produces, but a body written
  // by a proxy rather than by the API carries no status at all.
  let effectiveStatus: number | null = status;

  if (problem !== null && typeof problem.status === 'number') {
    effectiveStatus = problem.status;
  }

  const code: string | null = failureCode(problem);

  // A FAILURE ALWAYS CARRIES A DOCUMENT FROM HERE ON, even when the transport gave none.
  // Two failure modes arrive with no RFC 7807 body - a response whose body is not a problem
  // document, and a request that never completed - and a null document left every consumer
  // with a second, poorer presentation for them: runtime measurement found both collapsing
  // to one unstyled `<p role="status">`, byte-identical to each other, with no severity
  // word, no title, no support reference and no retry. `transportProblem` composes a
  // truthful document from the status so that the shared banner is the ONLY failure
  // presentation this store's consumers ever need.
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
 * The listing's seed: the coordinates a server response carries when nothing matched
 * and no paging applied.
 *
 * Built by the paging contract's own factory rather than written out here, so a
 * component cannot tell a seeded value from a received one and needs no separate
 * branch for the not-yet-loaded state.
 */
const EMPTY_PORTAL_PAGE: PortalListPage = emptyPagedResult<PortalListItem>();

/**
 * The first page, counted from zero.
 *
 * MIGRATION: ZERO, and the base is the wire's rather than the legacy screen's.
 * Website/admin/Portal/Portals.ascx.vb:L47 seeded its counter as
 * `Private _CurrentPage As Integer = 1` - ONE-based - and L142 subtracted one
 * immediately before the call, reading
 * `PortalController.GetPortalsByName(Filter <trailing wildcard>, CurrentPage - 1, PageSize, TotalRecords)`.
 * The one-based value was therefore never anything but a presentation detail, and it
 * stays one: the pager component may count from one for a person, and the translation
 * between the two bases is performed there. NO arithmetic is applied to a page index
 * anywhere in this file - neither the addition of one nor the subtraction of one - and
 * no base is silently normalised on the way in or out.
 * Corroborated independently by Website/admin/Users/ManageUsers.ascx.vb:L174-L184,
 * whose `PageNo` property seeds `Dim _PageNo As Integer = 0` at L176 and guards its
 * stored value with `If Not ViewState("PageNo") Is Nothing` at L177.
 */
const FIRST_PAGE_INDEX = 0;

/**
 * Portal state for the administration front end: the paged listing, the selected
 * portal, its settings projection and its unpaged host-name aliases.
 *
 * Root-provided and therefore a single instance for the application. NO component
 * declares a provider for it: two instances would give two screens two different
 * answers about which portal is selected, and a screen that re-created the store on
 * navigation would discard a page an operator had just paged to.
 *
 * EVERY PUBLIC SLICE IS READ-ONLY. Each is a private writable signal projected
 * through `asReadonly()`, so a consumer can observe state but cannot write it; the
 * only way to change anything below is a command method on this class, which is what
 * keeps the transitions in one reviewable place. Every write is IMMUTABLE - a new
 * array or a new object, never an in-place edit - because identity change is what
 * makes a change-detection-on-push consumer re-render.
 *
 * There is deliberately NO `effect()` in this store. An effect is for a genuine side
 * effect, and this store has none to perform: a load happens because a command was
 * called, not because a slice changed, and expressing it as an effect would make the
 * request an invisible consequence of assignment. `linkedSignal()` and `resource()`
 * are likewise unused - no slice here is derived-but-overridable, and the request
 * lifecycle is driven by explicit commands whose cancellation is handled below.
 *
 * MIGRATION: no client-side cache, no page-state round trip, no view state. See the
 * notes at the head of this module.
 */
@Injectable({ providedIn: 'root' })
export class PortalStore implements OnDestroy {

  /**
   * The typed transport. Every request in this file goes through it, and no URL,
   * query string or header is composed here.
   */
  private readonly portalApi = inject(PortalService);

  /*
   * ⚠ THIS STORE IS DISCARDED AT A SESSION BOUNDARY, AND IT DOES NOT ARRANGE THAT ITSELF.
   * Every slice here belongs to ONE TENANT and, through the permissions that admitted the
   * read, to ONE OPERATOR. Ending a session discards the credential; it does not discard
   * anything read with it, so without the discard the portals a previous operator listed
   * would still be here, rendered by whatever screen the next operator lands on.
   *
   * The discard is driven from `core/state/session-teardown.service.ts` (reached from the
   * transport layer when a renewal is refused, and from the identity store) and from
   * `core/state/session-lifecycle.service.ts` (reached from the shell when the operator
   * signs out). Both call this store's own `reset()`, which cancels its in-flight reads and
   * writes BEFORE clearing its slices — the ordering that stops a response still in the air
   * from refilling what was just emptied.
   *
   * ⚠ DO NOT ADD A REGISTRATION CALL HERE. Putting the fan-out in the two services and not
   * in the stores is what keeps the dependency pointing one way: no domain store imports the
   * identity store, so no domain store is in a position to start making authorization
   * decisions of its own, and the data layer stays out of the initial bundle. A store that
   * registered itself would need a reference in the opposite direction to do it.
   */
  // -------------------------------------------------------------------------
  // IN-FLIGHT REQUEST HANDLES
  //
  // Not state, and deliberately not signals: these are resource handles, and no
  // consumer has any business observing them. The loading slices below are what a
  // consumer reads.
  //
  // One CANCELLABLE handle per read concern. Starting a read cancels the previous
  // read of the same concern, which both aborts the request and - more importantly -
  // removes the possibility of a slower earlier response landing on top of a faster
  // later one and leaving the screen showing the page an operator has already left.
  //
  // Writes are held in a set instead and are NEVER cancelled by a later request.
  // Cancelling an in-flight write only stops the client listening; it does not undo
  // what the server may already have committed, so abandoning one would leave the
  // operator with no report of an operation that nonetheless happened. The set exists
  // solely so that teardown can release them.
  // -------------------------------------------------------------------------

  private listRequest: Subscription | null = null;

  private detailRequest: Subscription | null = null;

  private settingsRequest: Subscription | null = null;

  private administratorsRequest: Subscription | null = null;

  private aliasRequest: Subscription | null = null;

  /**
   * The current tenant's protected-facts read.
   *
   * A handle of its own rather than a share of {@link detailRequest}, because the two
   * address different portals and either may be in flight while the other is: an operator
   * browsing one portal's settings must not cancel the read that tells a role screen which
   * role its own tenant protects.
   */
  private contextRequest: Subscription | null = null;

  private readonly writeRequests = new Set<Subscription>();

  // -------------------------------------------------------------------------
  // LISTING
  // -------------------------------------------------------------------------

  /**
   * The page in hand, exactly as the server reported it: its rows and its
   * coordinates together.
   *
   * MIGRATION: replaces the untyped collection the legacy screen held.
   * Website/admin/Portal/Portals.ascx.vb:L48 declared
   * `Private _Portals As ArrayList = New ArrayList` and L55 held the total in a
   * SEPARATE field, `Protected TotalRecords As Integer`, filled through a by-reference
   * argument at L142. The rows and the total therefore had no structural relationship,
   * and either could be updated without the other. They now travel on one value, so a
   * consumer cannot read a total that belongs to a different query.
   * L54 is worth recording too: `Protected TotalPages As Integer = -1` seeded the page
   * count to the legacy absent-integer sentinel. The page count here is the server's
   * `totalPages`, which is a real number on every response and is never recomputed
   * from the total and the page size.
   */
  private readonly _page = signal<PortalListPage>(EMPTY_PORTAL_PAGE);

  /**
   * The page of records to return, counted from zero.
   *
   * The REQUESTED index, which is what the next request will carry. The index the
   * server actually served is on {@link PortalStore.listMeta}, and the two differ
   * between calling {@link PortalStore.goToPage} and the response arriving.
   */
  private readonly _pageIndex = signal<number>(FIRST_PAGE_INDEX);

  /**
   * The size of the page to ask for, or `null` to express no preference.
   *
   * `null` is transmitted as an OMISSION so the server applies its own default. That
   * is deliberate, and three measured facts sit behind it. The legacy portal screen
   * hard-coded twenty - `Portals.ascx.vb:L92-L98` returns the literal, with the
   * per-portal `Records_PerPage` read commented out at L94-L95, so the setting it
   * meant to honour was never consulted. The account screen, which did honour that
   * setting, defaulted it to ten. And the effective size is a per-portal setting
   * rather than a constant. Substituting either literal here would pick one of three
   * answers and hide the choice; omitting the parameter lets the one authority for it
   * answer. `null` is used rather than a sentinel: minus one is the legacy
   * absent-integer marker and the paging contract states that it must never be sent
   * as a page coordinate.
   */
  private readonly _requestedPageSize = signal<number | null>(null);

  /**
   * The operator's portal-name filter, raw and exactly as typed, or `null` for no
   * filter.
   *
   * MIGRATION: the trailing wildcard is the SERVER'S to compose and no character of a
   * pattern is contributed here. `Portals.ascx.vb:L142` concatenated a trailing
   * wildcard onto the operator's text at the call site, before the text reached the
   * data layer, giving an anchored - starts-with - predicate. Pattern composition now
   * belongs entirely to the repository behind the data-access abstraction, so this
   * slice holds the text byte for byte: untrimmed, its case unchanged, undecorated and
   * unencoded. Sending a pattern character would double whatever pattern the
   * repository already builds and would change which rows match; trimming or folding
   * the case would change it too.
   *
   * The predicate itself is the server's to state and has changed, which is recorded
   * rather than absorbed: the transport documents the implemented repository as
   * trimming the text, folding its case and matching it ANYWHERE within the name,
   * where the legacy predicate was anchored at the start. This store neither performs
   * nor re-describes that test - it forwards text - and a screen that tells an
   * operator what the field does should take the wording from the server's behaviour.
   *
   * MIGRATION: `null` rather than the empty string for "no filter", even though the
   * legacy code seeded `Private _Filter As String = ""` at `Portals.ascx.vb:L46` and
   * tested `If Filter <> ""` at L134. In the legacy contract the empty string WAS the
   * absent string - `Library/Components/Shared/Null.vb:L71-L75` has a body of
   * literally `Return ""` - so the two were indistinguishable. They are not
   * indistinguishable here, and the parameter serialiser treats `null` and omission
   * alike, so absence is stated rather than encoded.
   *
   * MIGRATION: the legacy `Expired` pseudo-filter is DROPPED and no successor is
   * invented. `Portals.ascx.vb:L138-L140` compared this same filter text against
   * `Localization.GetString("Expired", LocalResourceFile)` and, on a match, called
   * `PortalController.GetExpiredPortals()` and hid the pager outright - so WHICH QUERY
   * RAN DEPENDED ON THE DISPLAY LANGUAGE, and translating a resource file changed the
   * behaviour of the screen. No endpoint in the target exposes that listing, this
   * slice therefore has no mode of any kind, and nothing in this store branches on
   * user-facing text. The letter strip that fed this filter
   * (`Portals.ascx.vb:L170-L179`, built from resource strings) is presentation: its
   * "All" entry calls {@link PortalStore.clearNameFilter} and it has no expired entry.
   */
  private readonly _nameFilter = signal<string | null>(null);

  /** The field to order the listing by, or `null` for the server's own ordering. */
  private readonly _sortBy = signal<string | null>(null);

  /**
   * The direction to order in, or `null` for the server's default.
   *
   * The token is the server's member name, taken from the paging contract rather than
   * spelled here, because the binder accepts the member name and answers an
   * abbreviated or lower-cased spelling with a rejection.
   */
  private readonly _sortDir = signal<SortDirection | null>(null);

  /** Whether a listing request is in flight. */
  private readonly _listLoading = signal<boolean>(false);

  /** The last listing failure, classified, or `null` when the last attempt succeeded. */
  private readonly _listFailure = signal<PortalFailure | null>(null);

  /**
   * Whether a listing read has ever COMPLETED for this store instance.
   *
   * A plain boolean rather than a signal, deliberately: nothing renders it and no derivation
   * depends on it, so a signal would add a dependency edge for no reader. Read only by
   * {@link PortalStore.reloadPortals}, which explains why it exists.
   */
  private listingRead = false;

  // -------------------------------------------------------------------------
  // SELECTED PORTAL
  // -------------------------------------------------------------------------

  /**
   * The portal an operator is working on, or `undefined` when none is selected.
   *
   * MIGRATION: THE ABSENCE OF A SELECTION IS A DISTINCT `undefined`. It is NEVER
   * nought and NEVER minus one, and this is the single most consequential decision in
   * this file, because both of those are legitimate portal identifiers.
   * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77`
   * declares `[PortalID] [int] IDENTITY (-1, 1) NOT NULL`, so the FIRST portal ever
   * created carries minus one and the second carries nought - while
   * `Library/Components/Shared/Null.vb:L41-L45` defines the absent-integer marker as
   * minus one, its body being literally `Return -1`. One number therefore means both
   * "the first portal" and "no portal", and only context distinguishes them.
   *
   * Consequently every presence test in this file is an explicit comparison against
   * `undefined`. A truthiness test would lose the portal at nought; a comparison
   * against the marker would lose the portal at minus one; a lower bound would lose
   * both. The legacy code is the authority for testing absence explicitly rather than
   * by truth: every `ViewState` read guards with `Is Nothing`, as at
   * `EditPortalAlias.ascx.vb:L218` - `If Not ViewState("PortalAliasID") Is Nothing
   * Then` - and every query string read does the same, as at L55 and at
   * `Portals.ascx.vb:L343` and `:L347`. A row identifier of nought survives
   * `Is Nothing`; it does not survive truthiness.
   */
  private readonly _selectedPortalId = signal<number | undefined>(undefined);

  /**
   * The selected portal in full, or `null` when it has not been read.
   *
   * DISTINCT FROM {@link PortalStore.selectedPortalId} BEING ABSENT, and the two must
   * not be collapsed: an identifier with no record means "selected, not yet loaded",
   * whereas no identifier means "nothing is selected". A screen that conflated them
   * would show its empty state while a request was in flight.
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
   * The selected portal's configuration projection, or `null` when it has not been
   * read.
   *
   * MIGRATION: there is no portal-settings table and this is not a key/value bag.
   * `Library/Components/Portal/PortalController.vb:L1209-L1210` shows
   * `GetCurrentPortalSettings()` returning
   * `CType(HttpContext.Current.Items("PortalSettings"), PortalSettings)` - a composite
   * assembled per request and held in AMBIENT REQUEST STATE, never a persisted
   * aggregate - and no settings table appears in the schema scripts. Portal
   * configuration is columns on the portal row, this is a projection of those columns,
   * and the ambient composite became an immutable request-scoped context on the
   * server. The whole projection is read and the whole projection is replaced: there
   * is no single-setting read, no single-setting write and no partial patch, here or
   * on the transport.
   *
   * MIGRATION: the quota members are held EXACTLY as received and are never
   * coalesced, defaulted or normalised, because nought and minus one mean different
   * things. `PortalController.vb:L86-L87` hydrated them through
   * `Convert.ToInt32(Null.SetNull(dr("PageQuota"), …))` and the matching line for the
   * account quota, and `Null.SetNull` maps a database null onto the sentinel for the
   * target field's type - so a NULL column hydrated to MINUS ONE, meaning NOT SET. The
   * create path meanwhile seeded `Dim intPageQuota As Integer = 0` at L350 and
   * `Dim intUserQuota As Integer = 0` at L355, each only overwritten when the
   * corresponding host setting was non-blank, and passed them positionally at L369 -
   * so NOUGHT means UNLIMITED. The same file makes the contrast explicit two lines
   * later: `Dim intSiteLogHistory As Integer = -1` at L360 seeds the history retention
   * to NOT SET while the quotas beside it seed to UNLIMITED. The identical care
   * applies to the host space, the host fee and the history retention, and to the time
   * zone offset, where nought is a real offset. Not one of them is read through a
   * coalescing operator anywhere in this file.
   *
   * MIGRATION: the six page references are likewise held verbatim. On them minus one
   * means "no such page is configured" while nought is a REAL page, because the page
   * table's identity seed is nought. Interpreting them is the feature's business;
   * this store neither coalesces them nor treats either value as absence.
   *
   * MIGRATION: two members that never appear. The mapped file-system path of a
   * portal's home directory is not modelled on the client at all, and the payment
   * processor's credential is never on a response - the read contracts carry the
   * processor name and its account identifier and nothing else. Neither is held here,
   * neither is derived here, and no request contract is retained after it has been
   * handed to the transport, so a write-only credential cannot linger in application
   * state. Nothing in this file writes a secret into a literal, which is the legacy
   * anti-pattern `Website/release.config:L89-L93` committed to source control.
   */
  private readonly _settings = signal<PortalSettings | null>(null);

  /** Whether a settings read or write is in flight. */
  private readonly _settingsLoading = signal<boolean>(false);

  /** The last settings failure, classified, or `null`. */
  private readonly _settingsFailure = signal<PortalFailure | null>(null);

  /**
   * The accounts the selected portal may designate as its administrator, or `null` when
   * they have not been read.
   *
   * Held beside the settings projection rather than inside it, because the two are separate
   * reads of different lifetimes: the projection is what the form is bound to, while this is
   * the vocabulary one of its fields may choose from, and the portal a screen addresses can
   * change without the vocabulary needing to be discarded before the new one arrives.
   *
   * `null` and an EMPTY LIST are distinct and neither is rewritten into the other. Unread
   * means the selector cannot be offered yet; empty means the portal genuinely designates no
   * administrator role, or one with no members, which is a state the screen must render.
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
   * Every host name bound to the portal named by
   * {@link PortalStore.aliasesPortalId}, or `null` when they have not been read.
   *
   * THREE STATES, and the first two must not be collapsed - the same three the
   * response contract distinguishes. `null` means not loaded and says NOTHING about
   * how many exist. An empty array means loaded and there are none. A populated array
   * means loaded and these are they.
   *
   * DELIBERATELY UNPAGED: there is no page index, no page size, no total and no page
   * count for this collection, here or on the wire. The endpoint returns a bare array
   * and the legacy screen bound one whole -
   * `Website/admin/Portal/PortalAlias.ascx.vb` calls
   * `GetPortalAliasArrayByPortalID(intPortalID)` and binds the result to its grid with
   * no pager present at all. Introducing paging state for it would describe parameters
   * the server does not bind, and a parameter it does not bind is discarded silently.
   */
  private readonly _aliases = signal<readonly PortalAlias[] | null>(null);

  /**
   * Which portal the held alias collection belongs to, or `undefined` when none has
   * been read.
   *
   * Held so that a screen can tell whether the collection in hand is the one it is
   * showing. Without it, navigating between two portals would briefly display the
   * previous portal's host names as though they were this portal's - and a host name
   * shown against the wrong tenant is exactly the class of mistake the resolution
   * change described below exists to prevent.
   */
  private readonly _aliasesPortalId = signal<number | undefined>(undefined);

  /**
   * The alias row an operator is editing, or `undefined` when none is selected.
   *
   * MIGRATION: this signal is the direct successor of `ViewState("PortalAliasID")`.
   * `EditPortalAlias.ascx.vb:L57` read the row identifier out of the query string as
   * `CType(Request.QueryString("paid"), Integer)`, L62 stored it with
   * `ViewState.Add("PortalAliasID", intPortalAliasID)`, and L176 and L220 read it back
   * out. L218 then used its PRESENCE - `If Not ViewState("PortalAliasID") Is Nothing
   * Then` - to decide between editing an existing row and adding a new one, which is
   * precisely the decision `undefined` expresses here.
   *
   * Absence is `undefined` and never nought, for the same reason as the portal
   * identifier: nought is a legitimate row identifier. The legacy code contains the
   * defect that proves the point. At L231, inside the branch reached only when that
   * `ViewState` entry is provably `Nothing`, it evaluates
   * `Convert.ToInt32(ViewState("PortalAliasID"))` - and the administration pages
   * compiled with strictness OFF (`Website/release.config:L125` declares
   * `<compilation debug="false" strict="false">`), so `Nothing` coerced silently to
   * NOUGHT and a real row identifier was passed as the row to exclude from the
   * duplicate check. No compiler reported it. Strict TypeScript makes the same
   * coercion impossible to write.
   */
  private readonly _selectedAliasId = signal<number | undefined>(undefined);

  /**
   * One alias row read on its own, or `null` when none has been.
   *
   * Populated by {@link PortalStore.loadAlias}, which a screen reached directly by
   * address needs because the collection may not have been loaded. A screen that
   * already holds the collection reads {@link PortalStore.selectedAlias} instead and
   * makes no second request.
   */
  private readonly _aliasDetail = signal<PortalAlias | null>(null);

  /** Whether an alias read or write is in flight. */
  private readonly _aliasLoading = signal<boolean>(false);

  /** The last alias failure, classified, or `null`. */
  private readonly _aliasFailure = signal<PortalFailure | null>(null);

  // =========================================================================
  // THE CURRENT TENANT'S PROTECTED FACTS
  //
  // A slice of its own, deliberately separate from {@link _selectedPortal}. The selected
  // portal is whichever record an operator happens to be BROWSING, and it changes as they
  // move around the portal screens; these are the facts about the tenant the caller is
  // SIGNED IN TO, which do not change while the session lasts. Sharing one slice would
  // mean that opening another portal's settings screen silently changed which role a role
  // screen believed to be protected.
  // =========================================================================

  /**
   * The tenant the protected facts describe, or `undefined` before the first read.
   *
   * ⚠ `undefined` MEANS UNREAD AND NOTHING ELSE. Portal keys are `IDENTITY(-1, 1)`
   * (`01.00.00.SqlDataProvider:L77`), so BOTH `-1` and `0` are real tenants and neither may
   * be read as absence. That is why this is `number | undefined` rather than a sentinel.
   */
  private readonly _contextPortalId = signal<number | undefined>(undefined);

  /** The current tenant's own record, or `null` before the first successful read. */
  private readonly _context = signal<PortalDetail | null>(null);

  /** Whether the protected-facts read is in flight. */
  private readonly _contextLoading = signal<boolean>(false);

  // =========================================================================
  // PUBLIC STATE - READ-ONLY PROJECTIONS
  //
  // Every one is the `asReadonly()` view of a slice above. Not one writable signal
  // leaves this class.
  // =========================================================================

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
   * The accounts the selected portal may designate as its administrator, or `null` when unread.
   *
   * ⚠ GATE ON {@link PortalStore.administratorsPortalId} BEFORE OFFERING THESE. The candidate
   * list belongs to the portal it was read for, and a screen that moved to another portal would
   * otherwise offer the previous portal's administrators for a moment - long enough to submit one,
   * which the server would then refuse for a reason the operator could not see.
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

  // =========================================================================
  // THE CURRENT TENANT'S PROTECTED FACTS — DERIVED
  //
  // ⚠ WHY THESE EXIST AT ALL. Four screens have to know which role and which account the
  // product protects before they may offer an action, and NOTHING ON THE RECORD ITSELF
  // SAYS SO: `RoleInfo.vb` carries no `IsSystem`, `SystemRole` or `IsAdmin` member of any
  // kind, and neither does the target's `Role` contract. The designations are PORTAL-scoped
  // columns — `Portals.AdministratorRoleId`, `Portals.RegisteredRoleId`,
  // `Portals.AdministratorId` — and the only place the API exposes them is the portal
  // record. Screens that could not read that record expressed the guards as optional inputs
  // nothing supplied, so protected roles stayed deletable, protected memberships stayed
  // removable, and a fee warning stayed wrong, until the API refused a request that should
  // never have been offered.
  //
  // ⚠ ADVISORY, NOT ENFORCEMENT. The API refuses a protected write on its own terms and
  // answers a problem document; these exist so a screen does not OFFER what will be
  // refused. Every one of them reads `null` or `false` until the record has been read, which
  // is the fail-safe direction for a guard: an unresolved fact protects nothing extra and
  // grants nothing extra either — it leaves the API's refusal as the operative rule, exactly
  // as before.
  // =========================================================================

  /**
   * The current tenant's own record, or `null` before the first successful read.
   *
   * Exposed whole as well as through the four facts below, because a screen that needs a
   * fifth column should read it here rather than have another projection added.
   */
  readonly currentPortal = this._context.asReadonly();

  /**
   * Whether the protected facts have been resolved from the server.
   *
   * ⚠ A SCREEN GUARDING A PROTECTED FLOW MUST TEST THIS, and not merely test the facts for
   * absence. `administratorRoleId` reads `null` both when the tenant designates no
   * administrator role — a real configuration in which nothing is protected — and when the
   * record has not been read yet. Those are different situations and only this flag
   * distinguishes them.
   */
  readonly contextResolved = computed<boolean>(() => this._context() !== null);

  /**
   * The account the current tenant designates as its administrator, or `null`.
   *
   * `Portals.AdministratorId`. The legacy delete guard on the account listing
   * (`Users.ascx.vb:L693-L694`) hid the affordance for exactly this account, and the
   * membership screen (`SecurityRoles.ascx.vb:L523`) refused to remove this account from
   * the administrator role.
   *
   * ⚠ COMPARED BY EQUALITY, NEVER BY TRUTHINESS. `Users.UserID` seeds at 1, but a `null`
   * here is a tenant with no designation and `0` would be a real key on any table seeded
   * from zero, so a truthiness test is forbidden on principle as well as in fact.
   */
  readonly administratorUserId = computed<number | null>(
    () => this._context()?.administratorId ?? null,
  );

  /**
   * The role the current tenant designates as conferring administration, or `null`.
   *
   * `Portals.AdministratorRoleId`. ⚠ ZERO IS A REAL ROLE: `Roles.RoleID` is
   * `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L114`), and the tenant's Administrators
   * role is usually role zero precisely because it is created first. Nothing may read it
   * as absence.
   */
  readonly administratorRoleId = computed<number | null>(
    () => this._context()?.administratorRoleId ?? null,
  );

  /**
   * The role every authenticated caller of the current tenant holds, or `null`.
   *
   * `Portals.RegisteredRoleId`. The stricter of the two protected roles: it may be neither
   * updated nor deleted, and its membership screen is meaningless because every account
   * holds it (`EditRoles.ascx.vb:L174-L182`).
   */
  readonly registeredRoleId = computed<number | null>(
    () => this._context()?.registeredRoleId ?? null,
  );

  /**
   * Whether the current tenant has a payment processor configured.
   *
   * MIGRATION — DEFECT 5, REPRODUCED RATHER THAN REPAIRED. `EditRoles.ascx.vb:L104-L109`
   * reads `If (objPortalInfo Is Nothing OrElse String.IsNullOrEmpty(objPortalInfo.ProcessorUserId))`
   * and then SHOWS the warning, while its own comment says the warning appears when a
   * processor IS configured. The code is the behaviour and the code is also the sensible
   * reading — the warning tells an administrator to configure a processor before charging
   * for a role — so the code is what is reproduced and the contradiction is recorded rather
   * than tidied away.
   *
   * ⚠ MEASURED ON `processorUserId`, NOT ON `paymentProcessor`, because that is the column
   * the legacy condition reads. A whitespace-only value is treated as configured, exactly as
   * `String.IsNullOrEmpty` treats it, so no rule is tightened here either.
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

  /**
   * The paging facts locating the page in hand within the whole match set.
   *
   * These are the SERVER'S coordinates and stay exactly as it reported them until the
   * next response replaces them. In particular the page count is read and never
   * recomputed from the total and the page size: the server guards that division
   * twice, and a locally derived value that disagreed with the server's would give a
   * pager two answers and no way to choose.
   */
  readonly listMeta = computed<ApiMeta>(() => this._page().meta);

  /**
   * The total no of records that satisfy the criteria, counted across every page and
   * not only the page returned.
   */
  readonly totalCount = computed<number>(() => this._page().meta.totalCount);

  /**
   * The size of the page that produced the rows in hand: the size the server actually
   * applied, which can differ from the size asked for once a request has been
   * validated.
   */
  readonly servedPageSize = computed<number>(() => this._page().meta.pageSize);

  /** The number of pages the total divides into at the served page size. Server-computed. */
  readonly totalPages = computed<number>(() => this._page().meta.totalPages);

  /**
   * The page the server actually served, counted from zero.
   *
   * Read this rather than {@link PortalStore.pageIndex} when reporting where an
   * operator IS, and read the other when reporting where they are going: between a
   * page change and its response the two disagree, and that disagreement is real.
   */
  readonly servedPageIndex = computed<number>(() => this._page().meta.pageIndex);

  /**
   * Whether the match set is empty - nothing matched at all.
   *
   * DISTINCT FROM {@link PortalStore.isPastEnd}, and the response contract calls the
   * distinction out explicitly: no rows on the fourth page of three means "past the
   * end", whereas a total of nought means "no matches at all", and those deserve
   * different wording. Deciding the wording is the empty-state component's business.
   */
  readonly isListEmpty = computed<boolean>(() => this._page().meta.totalCount === 0);

  /** Whether records exist but the page in hand holds none - the requested page is past the end. */
  readonly isPastEnd = computed<boolean>(
    () => this._page().meta.totalCount > 0 && this._page().items.length === 0,
  );

  /**
   * Whether more records exist than fit on one page.
   *
   * MIGRATION: this is the legacy pager predicate, preserved exactly.
   * `Website/admin/Portal/Portals.ascx.vb:L155-L157` reads
   * `If SuppressPager And ctlPagingControl.Visible Then ctlPagingControl.Visible =
   * (PageSize < TotalRecords)`, and the comparison above is that same test against the
   * two coordinates the server reported.
   *
   * THE STORE DOES NOT OWN PAGER VISIBILITY, and this signal is not a visibility
   * decision. Visibility is presentation and belongs to
   * `shared/components/pagination`, whose declared inputs are the page, the page size
   * and the total count and whose output is a page change - the same three facts the
   * legacy pager was handed at L148-L150. This is offered as the derivation of the
   * legacy predicate, so that a component reproducing it need not restate the
   * arithmetic, and it deliberately says nothing about whether a control is drawn.
   */
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

  /** Whether a portal is selected. An explicit absence test, never a truthiness test. */
  readonly hasSelection = computed<boolean>(() => this._selectedPortalId() !== undefined);

  /**
   * Whether the alias collection has been read for the portal it belongs to.
   *
   * `false` while it is `null`, which is the "not loaded" state, and `true` for an
   * empty collection, which is the "loaded, none exist" state. Collapsing the two is
   * what makes a screen show its empty state during a request.
   */
  readonly aliasesLoaded = computed<boolean>(() => this._aliases() !== null);

  /**
   * How many host names the portal has, or `null` when they have not been read.
   *
   * `null` rather than nought for the unread state, preserving the three-state
   * distinction the collection itself carries.
   *
   * MIGRATION: the legacy screen used this count to hide an affordance -
   * `EditPortalAlias.ascx.vb:L107` reads
   * `If colPortalAlias.Count <= 1 Then cmdDelete.Visible = False`, so the removal
   * button disappeared for a portal's last host name. That is a presentation rule and
   * it stays one; the count is offered, the decision is not made here. The server does
   * NOT refuse the write - the transport records that unbinding a portal's last alias
   * is answered normally - so a component reproducing the rule is choosing an
   * affordance rather than enforcing a constraint.
   */
  readonly aliasCount = computed<number | null>(() => {
    const held: readonly PortalAlias[] | null = this._aliases();

    return held === null ? null : held.length;
  });

  /**
   * The selected alias: the row from the collection in hand where it holds one, the
   * individually read row otherwise, and `null` when neither is available.
   *
   * The collection is preferred because it is the more recently refreshed of the two
   * after a write, and the fallback is what lets a screen reached directly by address
   * show a row before the collection has been read. The fallback is taken only when
   * the individually read row IS the selected one, so a stale row from a previous
   * selection can never be presented as the current one.
   *
   * MATCHED ON THE IDENTIFIER AND ON NOTHING ELSE.
   *
   * MIGRATION: alias resolution changed from a substring match to an exact match, and
   * the change closed a multi-tenant mis-resolution hazard rather than tidying a query.
   * The legacy tenant resolver - created as `GetPortalSettings` at
   * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569`,
   * a name that describes a settings reader and a body that resolves a tenant - matched
   * the requested host name at L4582 with a pattern wrapping it in wildcards on BOTH
   * sides and then took `min(PortalID)`. One portal's host name being a fragment of
   * another's was therefore enough to resolve a request to the wrong tenant, and the
   * lowest identifier won. Resolution is now an exact match performed by the alias
   * resolution middleware, and that procedure was dropped for good in a later schema
   * script.
   *
   * Consequently this store performs NO host-name matching of any kind. There is no
   * fragment search over the collection, no case-folding comparison and - deliberately
   * - no lookup by host name at all, because tenant resolution is the server's and a
   * client-side answer to it could disagree. The comparison below is identifier
   * equality between two numbers.
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
   * Reads the page described by the listing slices.
   *
   * Cancels any listing request already in flight, then sends the page index, the page
   * size preference, the ordering and the portal-name filter exactly as they are held.
   *
   * The request is composed as a plain value handed to the transport, which passes it
   * to the one module that serialises a query string. NOTHING is corrected on the way:
   * no index is adjusted between bases, no filter text is trimmed, folded or decorated,
   * and no absent member is replaced by a literal. An out-of-range coordinate is
   * therefore answered by the server with a field-level rejection, which this store
   * surfaces on {@link PortalStore.listFailure}, rather than being quietly changed into
   * a request the operator did not make.
   *
   * MIGRATION: the parameter spellings and the counting base both changed, deliberately.
   * The legacy screen's `FilterURL` helper at
   * `Website/admin/Portal/Portals.ascx.vb:L215-L232` emitted lower-case keys -
   * `"filter=" & Filter` at L219 and L221 and `"currentpage=" & CurrentPage` at L219 and
   * L225 - and the page number it carried was the screen's ONE-based value. It also
   * took that number as a `String` parameter, `ByVal CurrentPage As String`, so a page
   * coordinate was a piece of text the compiler never checked. The target spells its
   * parameters in camel case, counts from zero, and types the index as a number, so the
   * spelling, the base and the type all changed and every one of the three is
   * intentional.
   *
   * MIGRATION: an implicit coercion in the legacy screen, made explicit here. That same
   * file WROTE the key as lower-case `currentpage` at L219 and L225 but READ it back
   * capitalised at L343 as `Request.QueryString("CurrentPage")`, which worked only
   * because the platform's query string lookup is case-insensitive; L344 then converted
   * the text with `CType(…, Integer)` under strictness-off compilation. Nothing here
   * relies on a case-insensitive lookup or on a textual page number: the index is a
   * number in a named member, and the one module that spells the parameter owns its
   * spelling.
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
   * Re-reads the current page without changing any listing slice.
   *
   * The same operation as {@link PortalStore.loadPortals}, named for the intent of
   * refreshing what is on screen after a write. Kept as a distinct member because a
   * call site that means "refresh" reads better than one that means "load", and because
   * the write commands below use it and their behaviour should be legible from their
   * own bodies.
   *
   * UNCONDITIONAL, and used by the three write commands that create, replace or remove a
   * PORTAL. Those are host-account operations reached from the listing itself, so the
   * listing is on screen by construction and the re-read is always wanted. A caller that
   * cannot make that guarantee wants {@link PortalStore.refreshListingIfRead} instead.
   */
  reloadPortals(): void {
    this.loadPortals();
  }

  /**
   * Re-reads the listing ONLY when a listing read has already completed.
   *
   * ⚠ THE MEASURED DEFECT THIS EXISTS TO CLOSE. The settings save used to call the
   * unconditional refresh above, so every save issued `GET /api/v1/portals?pageIndex=0` -
   * measured six times for six saves, an exact one-for-one pairing. A PORTAL
   * ADMINISTRATOR IS NOT PERMITTED TO READ THE PORTAL LISTING, so each of those answered
   * 403, and each 403 produced a console error and a warning notification reading "You do
   * not have access to this content." That notification is raised globally by the failure
   * interceptor, so it landed on whatever screen the operator had navigated to by the time
   * the response arrived - a permission complaint about a listing they never asked for, on
   * a screen with nothing to do with portals, immediately after a save that had actually
   * SUCCEEDED. That is the worst available reading of a successful write.
   *
   * REFRESHING SOMETHING NEVER READ IS MEANINGLESS, which is what makes this a correction
   * rather than a workaround. The re-read exists to keep a listing ALREADY IN HAND
   * coherent with a write - the settings projection carries the portal name, the expiry
   * date, the host fee and the host space, each also a listing column - and where no
   * listing has been read there is no coherence to maintain and nothing on screen to
   * refresh.
   *
   * The flag records a COMPLETED read rather than an attempted one, so a caller who was
   * refused the listing once is not asked to request it again after every subsequent
   * write. `loadPortals` itself is deliberately NOT guarded either: a screen that means
   * "load the listing" is entitled to try, and its failure belongs to that screen's own
   * surface.
   */
  refreshListingIfRead(): void {
    if (!this.listingRead) {
      return;
    }
    this.loadPortals();
  }

  /**
   * Folds a stored settings projection back into the selected portal, when it is the same one.
   *
   * ⚠ THE MEASURED DEFECT THIS EXISTS TO CLOSE. A settings save updated the settings slice
   * and re-read the listing, and left the DETAIL slice holding what it had read before the
   * write. The settings screen shows the portal's name beside its title, and it reads that
   * name from the detail slice — so renaming a portal and saving it produced a success
   * notification while the heading beside it still announced the OLD name, and it stayed
   * wrong until the route was entered again. Any other reader of the detail slice was equally
   * stale; the heading is simply where it was visible.
   *
   * The same argument that governs {@link PortalStore.refreshListingIfRead} governs this: a
   * slice ALREADY IN HAND has to stay coherent with a write, and a slice never read has
   * nothing to reconcile. Hence the null guard rather than a fetch — this method never issues
   * a request, so it costs nothing on a screen that holds no detail.
   *
   * THE MERGE IS TOTAL RATHER THAN A LIST OF FIELDS. Every one of the settings projection's
   * members is also a member of the detail, so spreading the projection over the held detail
   * updates all of them and invents none. A hand-written field list would be the thing that
   * rots: the next column added to the projection would silently stay stale here.
   *
   * The identity guard is not ceremonial. A save can complete after the operator has already
   * selected a different portal, and writing one portal's values onto another's detail is a
   * worse outcome than leaving the first one stale.
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
   * @param pageIndex The page of records to return, counted from ZERO: 0 is the first
   * page. Passed through untouched - no arithmetic, no clamping and no reinterpretation
   * of a negative value, which the server rejects with a field-level message rather
   * than correcting. A pager that derives its indices from the page count cannot
   * produce one.
   */
  goToPage(pageIndex: number): void {
    this._pageIndex.set(pageIndex);
    this.loadPortals();
  }

  /**
   * Changes the page size preference and re-reads from the first page.
   *
   * The first page rather than the current one, because the page an operator was on
   * addresses different records once the window changes, so holding the index would
   * move them somewhere they did not ask to go.
   *
   * @param pageSize The size of the page to ask for, or `null` to express no
   * preference and let the server apply its own default. Not bounded here: the server
   * answers a size above its maximum with a field-level message, and clamping would
   * silently serve a different page than the one requested.
   */
  setPageSize(pageSize: number | null): void {
    this._requestedPageSize.set(pageSize);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this.loadPortals();
  }

  /**
   * Restricts the listing to portals matching a name, and reads the first page.
   *
   * MIGRATION: returning to the first page reproduces the legacy behaviour exactly
   * rather than adding a convenience. `Website/admin/Portal/Portals.ascx.vb:L217-L222`
   * shows that changing the filter navigated to an address carrying `filter` and no
   * `currentpage`, so the screen's counter fell back to the default seeded at L47 -
   * `Private _CurrentPage As Integer = 1`, the first page. Holding the index instead
   * would leave an operator on the fourth page of a match set that now has one.
   *
   * @param name The operator's text, exactly as typed. Forwarded byte for byte:
   * untrimmed, its case unchanged, and with NO pattern character appended - composing
   * the pattern is the repository's business and contributing to it here would double
   * whatever pattern it already builds. Pass `null` for no filter.
   */
  setNameFilter(name: string | null): void {
    this._nameFilter.set(name);
    this._pageIndex.set(FIRST_PAGE_INDEX);
    this.loadPortals();
  }

  /**
   * Removes the name filter and reads the first page.
   *
   * This is what the letter strip's "All" entry calls. The legacy strip built its
   * entries from resource strings at `Portals.ascx.vb:L170-L179` and the screen tested
   * the chosen text against a localised value; nothing here compares user-facing text,
   * and there is no expired entry to compare against because that listing has no
   * successor endpoint.
   */
  clearNameFilter(): void {
    this.setNameFilter(null);
  }

  /**
   * Orders the listing and reads the first page.
   *
   * The first page for the same reason a page-size change returns to it: a row's page
   * depends on the ordering.
   *
   * @param sort The field to order by and the direction to apply, either member `null`
   * to express no preference. The direction token is the server's member name; an
   * abbreviated or lower-cased spelling is rejected by the binder, which is why the
   * token type comes from the paging contract rather than being written out at a call
   * site.
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

  /**
   * Adopts a COMPLETE listing query - page, size, filter and ordering together - and issues
   * exactly ONE read for it.
   *
   * ⚠ THIS EXISTS BECAUSE THE FOUR SINGLE-COORDINATE COMMANDS CANNOT BE COMBINED, AND THE
   * ALTERNATIVE WAS MEASURED RATHER THAN IMAGINED. Each of {@link PortalStore.goToPage},
   * {@link PortalStore.setPageSize}, {@link PortalStore.setNameFilter} and
   * {@link PortalStore.setSort} reads as its last act, which is right when an operator changes
   * one thing - and wrong when a whole query arrives at once, because four calls would issue
   * four requests for the same page and the first three answers would be discarded on arrival.
   * Three of them would also have reset the page index that the fourth was trying to restore.
   *
   * The caller for this is a screen that keeps its listing state in the ADDRESS: on entry, and
   * on every back or forward, the whole query arrives together and must be applied together.
   * That also makes the address the one place a stale filter can live, which is what stops a
   * root-provided store from replaying a previous visit's filter over a fresh navigation - the
   * defect this member closes. A screen that does NOT keep its state in the address is
   * unaffected and keeps using the single-coordinate commands.
   *
   * The page index is adopted VERBATIM rather than reset, which is the opposite of what the
   * single-coordinate commands do and is correct here for the same reason they are: they reset
   * it because a coordinate CHANGED under the operator, whereas this member is told what the
   * page is as part of the query being restored. Resetting it would make a reload of page four
   * land on page one, which is precisely the round trip this exists to make work.
   *
   * @param query The whole listing query. Every member is adopted exactly as given; nothing is
   * clamped, defaulted or validated here, because the address is parsed by the screen that owns
   * it and an out-of-range page is the server's to refuse with a field-level message.
   */
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
   * Records which portal an operator is working on, without requesting anything.
   *
   * Pairs with {@link PortalStore.loadSelectedPortal},
   * {@link PortalStore.loadSettings} and {@link PortalStore.loadAliases}, so a screen
   * can adopt a selection from its route and then read only the parts it shows.
   *
   * When the selection actually MOVES, everything held about the previous portal is
   * discarded: its record, its settings projection and its host names all belong to a
   * portal that is no longer on screen, and leaving them in place would let a screen
   * render one portal's identifier beside another portal's host names. Nothing is
   * discarded when the same portal is re-selected, so a repeated call costs nothing.
   *
   * @param portalId The portal to select. EVERY integer is meaningful, minus one and
   * nought included - see {@link PortalStore.selectedPortalId} for why neither may be
   * treated as an absence.
   */
  selectPortal(portalId: number): void {
    if (this._selectedPortalId() === portalId) {
      return;
    }

    this._selectedPortalId.set(portalId);
    this.discardPortalScopedState();
  }

  /**
   * Clears the selection and everything held about the portal that was selected.
   *
   * The selection returns to `undefined` - the distinct absence - and never to nought
   * or minus one, both of which are real portal identifiers.
   */
  clearSelection(): void {
    this._selectedPortalId.set(undefined);
    this.discardPortalScopedState();
  }

  /**
   * Selects a portal and reads it in full.
   *
   * The composition a route entry needs: one call establishes the selection, discards
   * whatever belonged to the previous portal, and issues the read.
   *
   * @param portalId The portal to read. Interpolated by the transport exactly as
   * supplied and never tested first: a portal that does not exist is reported by the
   * server as not found, which surfaces on {@link PortalStore.detailFailure}.
   */
  loadPortal(portalId: number): void {
    this.selectPortal(portalId);
    this.readPortal(portalId);
  }

  /**
   * Re-reads the selected portal.
   *
   * Does nothing when no portal is selected, because there is nothing to read - and
   * absence is tested explicitly against `undefined` rather than by truth, since a
   * selection of nought or minus one is a real selection.
   */
  loadSelectedPortal(): void {
    const chosen: number | undefined = this._selectedPortalId();

    if (chosen === undefined) {
      return;
    }

    this.readPortal(chosen);
  }

  /**
   * Creates a portal.
   *
   * The request is handed to the transport exactly as the caller composed it and is
   * NOT retained: a create request carries the first administrator's password, and
   * application state is no place for it. Every member is transmitted, including one
   * holding nought, minus one, an empty string or `false`, because each of those is a
   * value in this domain rather than an absence - the server's serialiser is
   * configured never to elide a written member, so both sides agree about what
   * "present" means. Stripping a member here because it looked empty would ask for a
   * different portal than the one described.
   *
   * On success the created portal becomes the selection, so a screen can move straight
   * to it, and the listing is re-read so the new row appears with coordinates the
   * server reported rather than coordinates invented here.
   *
   * MIGRATION: a positional parameter list becomes a request contract.
   * `Library/Components/Portal/PortalController.vb:L980` declared `CreatePortal` with
   * FIFTEEN positional parameters, eleven of them `String`, so adjacent arguments were
   * interchangeable to the compiler and a transposed pair produced a portal with its
   * description in its keywords and no error anywhere. The provider call it made at
   * L369 passed nine more positionally. A named contract cannot be transposed.
   *
   * @param request The portal to create, with its first host name and the
   * administrator account to establish alongside it.
   * @returns A ticket emitting the created portal once, after the state above has settled,
   * and completing without emitting if the write fails. See
   * {@link PortalStore.outcomeTicket}. The store never navigates: routing belongs to the
   * component, so a caller that must move on subscribes and supplies its own step, and this
   * class imports no router.
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
          this.reloadPortals();

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
   * Replaces one portal's editable state.
   *
   * The identifier travels in the path and on the body, because the write contract
   * declares its own and the server requires the two to agree. Both are forwarded
   * unchanged and neither is reconciled here: detecting a disagreement is the server's
   * check, and silently overwriting one with the other would hide a caller's mistake
   * and write a portal it did not mean to address.
   *
   * The request is not retained. It carries the payment processor's credential
   * reference, which is write-only and appears on no read contract, so holding it would
   * put in application state a value the server declines to hand back.
   *
   * On success the returned record replaces the held one and the listing is re-read,
   * because a name, an expiry date, a host fee and a host space are all on both the
   * record and the listing row.
   *
   * MIGRATION: a positional parameter list becomes a request contract.
   * `PortalController.vb:L1568` declared `UpdatePortalInfo` with TWENTY-SEVEN
   * positional parameters and passed all twenty-seven straight through to the data
   * provider in the same order, so the call site and the provider had to agree on a
   * sequence nothing verified.
   *
   * MIGRATION: the quota members reach the server exactly as the form composed them.
   * Nought means unlimited and minus one means not set - see the note on
   * {@link PortalStore.settings} - so neither is coalesced, defaulted or normalised on
   * the way through, and the same holds for the host space, the host fee, the history
   * retention and the time zone offset.
   *
   * @param portalId The portal to write. Must match the identifier on the body.
   * @param request The complete editable state to store.
   * @returns A ticket emitting the stored portal once. See
   * {@link PortalStore.outcomeTicket}.
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
          this.reloadPortals();

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
   * Removes one portal.
   *
   * Answered with no body, so there is nothing to store. The row is removed from the
   * page in hand immediately - an optimistic edit, so a grid responds without waiting -
   * and the listing is then re-read.
   *
   * THE PAGING COORDINATES ARE NOT ADJUSTED by the optimistic edit, and that is
   * deliberate rather than an oversight: the total, the served page size and the page
   * count are the server's values, the page count in particular is guarded arithmetic
   * the server performs, and decrementing a total locally would put a fabricated number
   * where a reported one belongs. They stay exactly as last reported until the re-read
   * replaces them, which is also why the re-read is issued rather than left to a caller.
   *
   * MIGRATION: a refusal is possible and is surfaced, not swallowed. An installation
   * must retain at least one portal, and the server answers the attempt with a conflict
   * carrying its own reason code, which reaches
   * {@link PortalFailure.conflictCode}. The legacy wording lived under the resource key
   * `LastPortal` in the GLOBAL resource file and was read by
   * `PortalController.vb:L200`; the mapping from that key to the published code is held
   * once in `core/utils/form-errors.util.ts` and is not restated here.
   *
   * @param portalId The portal to remove. Every integer is meaningful.
   * @returns A ticket emitting once after the removal has settled. See
   * {@link PortalStore.outcomeTicket}. A caller with nothing to do afterwards — the listing
   * screen, whose grid the optimistic edit above has already corrected — may ignore it, and
   * the state updates still happen.
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
   * Reads one portal's configuration projection.
   *
   * Cancels any settings request already in flight, for the same reason a listing
   * request is cancelled: a slower earlier response must not land on top of a faster
   * later one and leave a form bound to the wrong portal's values.
   *
   * @param portalId The portal whose settings to read. Also adopted as the selection,
   * so that a settings screen reached directly by address does not need a second call
   * to establish which portal it is showing.
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
   * Replaces one portal's configuration projection.
   *
   * The whole projection is written, because the endpoint publishes no single-setting
   * write and no partial patch. The body is the write contract without its identifier -
   * the portal is already named by the path - and it is forwarded exactly as the form
   * composed it, quota members and all, then released rather than retained.
   *
   * The listing is re-read afterwards - but ONLY when a listing has actually been read -
   * because the projection carries the portal name, the expiry date, the host fee and the
   * host space, every one of which is also a column of the listing row. This screen is
   * reachable by a portal administrator who may not read the listing at all, which is why
   * the conditional variant is used here and the unconditional one is not. See
   * {@link PortalStore.refreshListingIfRead}.
   *
   * The DETAIL slice is reconciled under the same argument and the same condition: it carries
   * every member the projection carries, so a screen holding it would otherwise go on showing
   * pre-save values after a write that succeeded. See
   * {@link PortalStore.reconcileSelectedPortal}.
   *
   * @param portalId The portal to write.
   * @param request The complete settings state to store.
   * @returns A ticket emitting the stored projection once. See
   * {@link PortalStore.outcomeTicket}.
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
   * Reads the accounts one portal may designate as its administrator.
   *
   * MIGRATION: fills the selector the legacy screen built at
   * `Website/admin/Portal/SiteSettings.ascx.vb:L331-L336` from the members of the portal's own
   * administrator role. The affordance had no target equivalent at all until this read existed,
   * because every role read resolves its tenant from the caller's own context rather than from a
   * path segment and so cannot enumerate the administrators of the portal a settings screen
   * happens to be addressing.
   *
   * The portal is RECORDED alongside the answer rather than inferred from the selection, and the
   * two are set together on success so a consumer can never read a list belonging to one portal
   * while believing it belongs to another. The selection is deliberately NOT adopted here — this
   * read accompanies a settings read that has already established it, and adopting it a second
   * time would make the order of two independent reads matter.
   *
   * An earlier read is cancelled, on the same reasoning as the settings read: a slower earlier
   * response must not land on top of a faster later one and offer the wrong portal's accounts.
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
        // The held list is CLEARED rather than kept, and its portal with it. A stale list beside a
        // recorded failure is the worst of the three states: the screen would offer accounts it
        // could not vouch for, and the operator would have no reason to distrust them.
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
   * Reads every host name bound to one portal.
   *
   * UNPAGED: no page index, no page size, no ordering and no filter is sent, because
   * the server binds none of them for this collection and a parameter it does not bind
   * is discarded silently, leaving a caller believing it had asked for something it had
   * not. The transport says the same of itself and sends an explicitly empty parameter
   * set rather than relying on omission.
   *
   * @param portalId The portal whose host names to read. Also adopted as the selection,
   * and recorded on {@link PortalStore.aliasesPortalId} so a screen can tell whether the
   * collection in hand is the one it is showing.
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
   * Records which alias row an operator is editing, without requesting anything.
   *
   * The successor of `ViewState("PortalAliasID")` - see
   * {@link PortalStore.selectedAliasId}. When the selection moves, the individually
   * read row is discarded, because it describes the row that was selected before.
   *
   * @param portalAliasId The alias row to select. Every integer is meaningful; nought is
   * a legitimate row identifier.
   */
  selectAlias(portalAliasId: number): void {
    if (this._selectedAliasId() === portalAliasId) {
      return;
    }

    this._selectedAliasId.set(portalAliasId);
    this._aliasDetail.set(null);
  }

  /**
   * Clears the alias selection.
   *
   * MIGRATION: the ABSENCE of an alias selection is what the legacy screen used to
   * distinguish adding a host name from editing one -
   * `EditPortalAlias.ascx.vb:L218` branches on
   * `If Not ViewState("PortalAliasID") Is Nothing Then` - so returning this slice to
   * `undefined` is how a screen says "adding". It is never returned to nought, which
   * would name a real row.
   */
  clearAliasSelection(): void {
    this._selectedAliasId.set(undefined);
    this._aliasDetail.set(null);
  }

  /**
   * Reads one alias row on its own.
   *
   * For a screen reached directly by address, which holds an identifier but not the
   * collection. A screen that already holds the collection reads
   * {@link PortalStore.selectedAlias} and makes no request.
   *
   * MIGRATION: the legacy query string key is not carried forward.
   * `EditPortalAlias.ascx.vb:L55` addressed a row through
   * `Request.QueryString("paid")` - guarded, as every legacy read is, with an explicit
   * `Is Nothing` rather than a truthiness test - and L57 converted the text with
   * `CType(…, Integer)`. The target names the row in full as a path segment, because a
   * segment is read far more often than it is typed, and the identifier is a number
   * throughout.
   *
   * @param portalId The portal that owns the row. Checked by the server against the
   * stored row's owner, because reading or changing another tenant's host name is
   * exactly what the resolution change described on
   * {@link PortalStore.selectedAlias} exists to prevent.
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
   * Binds an additional host name to one portal.
   *
   * The server answers with the created row, carrying the identifier the database
   * assigned, so it is appended to the collection in hand IMMUTABLY - a new array,
   * never a push - which is what makes a change-detection-on-push grid re-render. The
   * appended row is the server's, not the request's, so nothing about it is invented
   * here: a host name the server normalised is the normalised one.
   *
   * Nothing is appended while the collection is unread, because `null` means "not
   * loaded" and says nothing about how many exist; seeding a one-row collection from a
   * write would claim the portal has exactly one host name.
   *
   * MIGRATION: a duplicate host name is refused and the refusal is surfaced, not
   * swallowed. The legacy screen caught the provider's exception and rendered the
   * resource key `DuplicateAlias` at `EditPortalAlias.ascx.vb:L226` and again at L238 -
   * catching an untyped failure and asserting a cause. The server now answers with a
   * conflict carrying its own reason code, which reaches
   * {@link PortalFailure.conflictCode}; the two legacy keys for this collision collapse
   * onto that one code, and the mapping is held once in
   * `core/utils/form-errors.util.ts`.
   *
   * @param portalId The portal to bind the host name to.
   * @param request The host name to bind. Composed by the form, which owns the
   * stripping of a scheme or a share prefix the legacy screen performed at L209-L215.
   * @returns A ticket emitting the created row once. See
   * {@link PortalStore.outcomeTicket}.
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

          // ⚠ THE LISTING IS RE-READ RATHER THAN APPENDED TO, so that both write paths on this
          // resource leave the collection in the same state. Creation used to splice the 201
          // body onto the held array while {@link PortalStore.updateAlias} re-read the
          // collection, and the reason for the split was a difference in the CONTRACTS rather
          // than a decision: `POST` answers with the created resource and `PUT` answers with no
          // body, so update had nothing to merge and had to ask again.
          //
          // That is an explanation and not a justification. A spliced row is the client's OWN
          // idea of where the record belongs: it lands at the end of the array regardless of the
          // order the collection endpoint applies, and it carries only the members the create
          // response happened to include. Re-reading makes the listing the server's answer in
          // both cases, so the order is the server's order and no row can be assembled here from
          // a partial view of it.
          //
          // The response body is still used - it is recorded as the detail and as the selection
          // above, both of which concern the record just written rather than the collection - so
          // nothing about the created resource is discarded, and the cost is one GET on a write
          // the operator has just waited for.
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
   * Changes the host name one alias binds.
   *
   * THE ENDPOINT RETURNS NO BODY, so the stored row is re-read rather than reconstructed
   * from the request. That is the composition this store exists for: it is the one write
   * on the transport that answers with nothing, and guessing the stored value would
   * display the text as typed even where the server stored something else. The
   * collection is re-read for the same reason, and the two reads are sequenced here so
   * that no caller has to know the endpoint is unusual.
   *
   * MIGRATION: a duplicate host name is refused here too, with the same code as on
   * create - see {@link PortalStore.createAlias}.
   *
   * @param portalId The portal that owns the row.
   * @param portalAliasId The row to change.
   * @param request The host name to store in place of the current one. The owning portal
   * is not re-bound: the contract carries the host name alone.
   * @returns A ticket emitting once after the write has been stored and the collection
   * re-read. See {@link PortalStore.outcomeTicket}.
   */
  updateAlias(
    portalId: number,
    portalAliasId: number,
    request: UpdatePortalAliasRequest,
  ): Observable<void> {
    const outcome = PortalStore.outcomeTicket<void>();

    this._aliasLoading.set(true);
    this._aliasFailure.set(null);

    this.track(
      this.portalApi.updateAlias(portalId, portalAliasId, request).subscribe({
        next: () => {
          this._aliasLoading.set(false);
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

  /**
   * Unbinds one alias from one portal.
   *
   * Answered with no body. The row is removed from the collection in hand immutably, and
   * that removal is EXACT rather than optimistic bookkeeping: the collection is unpaged,
   * so its size is its length and there is no server-reported total to fall out of step
   * with. No re-read is therefore issued.
   *
   * The selection is cleared when the removed row was the selected one, returning it to
   * `undefined` rather than to nought.
   *
   * The server does not refuse this write for a portal's last host name; the legacy
   * screen merely hid the affordance, at `EditPortalAlias.ascx.vb:L107`, and that remains
   * a presentation choice - see {@link PortalStore.aliasCount}.
   *
   * @param portalId The portal that owns the row.
   * @param portalAliasId The row to unbind.
   * @returns A ticket emitting once after the removal has settled. See
   * {@link PortalStore.outcomeTicket}.
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

          // ⚠ REMOVAL IS APPLIED LOCALLY AND IS NOT RE-READ, WHICH IS A DELIBERATE ASYMMETRY WITH
          // {@link PortalStore.createAlias}. The two are not the same problem. An INSERTION has to
          // decide WHERE the new record belongs and WHICH of its members are known, and the client
          // can be wrong about both - it appends to the end regardless of the order the collection
          // endpoint applies, and it holds only the members the create response carried. Neither
          // question arises when removing: the row to drop is named by the identifier the caller
          // just deleted, its position is whatever it was, and no member of it is needed. Filtering
          // it out therefore reaches exactly the state a re-read would, without the request.
          //
          // Recorded here so the asymmetry reads as a decision rather than as the inconsistency
          // that creation genuinely was.
          this._aliasLoading.set(false);

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

  /**
   * Discards every held failure without touching any data slice.
   *
   * For a screen dismissing an error surface. The data is left exactly as it was,
   * because dismissing a report of a failure is not the same as undoing whatever the
   * failure interrupted.
   */
  /**
   * Reads the protected facts of the tenant the caller is SIGNED IN TO.
   *
   * ⚠ IDEMPOTENT BY DESIGN, AND EVERY SCREEN THAT NEEDS THE FACTS CALLS IT. A second call
   * for a tenant already held returns without a request, and a call made while the read is
   * in flight does the same, so four screens may each ask on initialisation and exactly one
   * request is issued. That is what lets a guard be expressed as "ask, then read" without
   * any screen having to know whether a sibling asked first.
   *
   * ⚠ THE ARGUMENT IS THE CALLER'S OWN TENANT, from `CurrentUser.portalId`, and never the
   * portal a screen happens to be browsing. Passing a browsed portal would make a role
   * screen protect another tenant's roles.
   *
   * ⚠ A FAILURE IS ABSORBED RATHER THAN PUBLISHED, and that is deliberate. These facts are
   * advisory: the API refuses a protected write on its own terms whatever this client
   * believes. Routing a refusal into the shared failure slot would put a banner on a screen
   * whose primary read succeeded, reporting a request the operator never made — so the facts
   * simply stay unresolved, {@link PortalStore.contextResolved} stays false, and the
   * screens fall back to exactly the behaviour they had before this slice existed: offer
   * the action and let the server's refusal govern.
   *
   * @param portalId The tenant the caller is signed in to. EVERY integer is meaningful,
   * minus one and nought included.
   */
  loadCurrentPortalContext(portalId: number): void {
    if (this._contextPortalId() === portalId && (this._context() !== null || this._contextLoading())) {
      return;
    }

    // A change of tenant discards the previous tenant's facts NOW rather than on arrival, so
    // that no screen can read one tenant's protected role while another tenant's key is
    // published. A re-read of the same tenant keeps what is in hand.
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
   * Returns every slice to the state it held before the first request.
   *
   * For a sign-out, so that one operator's portals, selection and host names are not
   * visible to the next. In-flight requests are cancelled first, because a response
   * arriving after a reset would repopulate exactly what the reset cleared.
   *
   * The listing seed is the paging contract's own empty envelope rather than a literal,
   * so the reset state is indistinguishable from a response that matched nothing - which
   * means a component needs no separate branch for it.
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

    // The protected facts go with the rest. They describe the tenant of the session being
    // discarded, so leaving them would let the next operator's screens protect the previous
    // operator's roles - and would suppress the fresh read that gets it right.
    this._contextPortalId.set(undefined);
    this._context.set(null);
    this._contextLoading.set(false);

    this.clearFailures();
  }

  /**
   * Releases every request handle when the injector holding this store is destroyed.
   *
   * A root-provided store lives as long as the application, so this runs on teardown -
   * which matters for a test, where each specification builds its own injector and a
   * request left listening across that boundary would report into a store the next
   * specification has replaced.
   */
  ngOnDestroy(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  // =========================================================================
  // PRIVATE
  // =========================================================================

  /**
   * Reads one portal in full, without touching the selection.
   *
   * The body shared by {@link PortalStore.loadPortal} and
   * {@link PortalStore.loadSelectedPortal}, so that the cancellation and the
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

  /**
   * Discards everything that belongs to one particular portal.
   *
   * Called when the selection moves or is cleared. The record, the settings projection
   * and the host names are all portal-scoped, so every one of them is wrong the instant
   * the portal changes. The LISTING is deliberately untouched: it belongs to the query,
   * not to the selection, and clearing it would empty a grid every time an operator
   * clicked a row.
   */
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
   * A write is never cancelled by a later request - see the note on the handles at the
   * head of this class - so the handle is discarded when the write finishes rather than
   * when the next one starts, and the set cannot grow without bound.
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
   * The ticket every write in this class returns, in place of accepting a caller's callback.
   *
   * ---------------------------------------------------------------------------------------
   * WHAT THIS REPLACES, AND WHY IT WAS WORTH REPLACING
   *
   * Each of the seven writes here used to take an optional continuation — `onCreated`,
   * `onUpdated`, `onSaved`, `onDeleted` — and invoke it from inside its response handler.
   * This store is provided at the root, so it outlives every screen that calls it, and a
   * callback handed to it is a CLOSURE OVER A COMPONENT: over that component's signals, its
   * router, its notification service and its `this`. Retaining one makes a root-lived object
   * hold a destroyed component's scope, and a response arriving after the operator has
   * navigated away runs that component's continuation anyway — announcing a success into a
   * screen that is gone and, where the continuation navigates, moving a route the operator
   * did not ask to move. Nothing about it is observable from the component, which cannot see
   * the reference, and nothing cancels it, because there is no handle to cancel.
   *
   * It was also the ONLY store in this application that did this, so it was a local
   * inconsistency as well as a leak: every other store reports through its state slices.
   *
   * ---------------------------------------------------------------------------------------
   * WHY AN `AsyncSubject` SPECIFICALLY
   *
   * Three properties are needed at once, and this is the one subject that has all three:
   *
   *   1. IT EMITS ONLY ON COMPLETION, so a caller cannot observe a half-finished write. The
   *      value is published after the store's own state updates have run, so a continuation
   *      always sees settled state.
   *   2. IT REPLAYS TO A LATE SUBSCRIBER. The caller subscribes after the command returns,
   *      and a transport that answers synchronously — which is exactly what a test's
   *      controller does — would already have completed a plain `Subject` by then, silently
   *      dropping the outcome. An `AsyncSubject` hands the value to whoever subscribes next.
   *   3. IT EMITS AT MOST ONCE, which is what "one write, one outcome" means. A continuation
   *      cannot be run twice by a retry or a redelivery.
   *
   * ⚠ A FAILURE COMPLETES WITHOUT EMITTING, AND DOES NOT ERROR. Erroring the ticket would
   * hand callers an unhandled rejection to guard against and would report the same failure
   * twice, because a failure ALREADY reaches the screen through this store's failure slices
   * and the failure effects the screens already have. Completing empty means the success
   * continuation simply does not run — which is the whole of what a caller needs — while the
   * failure keeps its single existing route to the operator.
   *
   * The store still subscribes to the transport itself, so STATE UPDATES REMAIN
   * UNCONDITIONAL: they happen whether or not anybody is listening to the ticket, and a
   * caller that ignores the return value loses nothing but its own continuation.
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

  /**
   * Releases every write handle.
   *
   * Only for teardown and for a reset that is discarding the whole store. A write in
   * flight is not otherwise abandoned, because releasing the handle stops the client
   * listening without undoing anything the server may already have committed.
   */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();
  }
}

// ---------------------------------------------------------------------------
// CLOSING MIGRATION NOTES
// ---------------------------------------------------------------------------
//
// MIGRATION: EVERY WIRE BOOLEAN IS DATA, and `false` is never read as an absence.
//   The legacy contract could not make that distinction:
//   `Library/Components/Shared/Null.vb:L76-L80` defines the absent boolean as `False`,
//   and its `IsNull` at L208-L237 consequently answers TRUE for `False` exactly as it
//   answers true for minus one and for the empty string. So a legacy caller asking
//   "is this set?" of a boolean got the same answer for "no" as for "not stated". The
//   read contracts this store holds declare their booleans non-nullable and the
//   server's serialiser is configured never to elide a written member, so a `false`
//   arriving here means `false`. Nothing in this file tests a boolean for presence.
//
// MIGRATION: OFFSET PAGING ONLY, and every alternative is deliberately absent. The
//   only coordinates anywhere above are a page index and a page size. There is no
//   opaque forward-only position handle, no continuation marker, no per-page hypertext
//   address, no link collection, and no offset-and-limit pair under any other pair of
//   names. The legacy provider took a page index, a page size and a by-reference total
//   - `Portals.ascx.vb:L142` - and the target's contract carries the same three facts.
//   Adopting a forward-only scheme would be an unrequested behavioural change: it
//   would silently forbid addressing an arbitrary page, which is the one thing the
//   legacy pager could do.
//
// MIGRATION: the wire arrives UN-ELIDED and every sentinel on it is data. The server
//   writes every declared member, including one holding nought, an empty string or
//   `false`, and no converter maps an empty string or a minus one onto a null. This
//   store therefore neither restores an absent member nor removes a present one: the
//   quota members, the six page references, the time zone offset and the immutable
//   portal identifier are all held exactly as received. Not one coalescing operator
//   is applied to any of them, which is what makes "nought means unlimited" and
//   "minus one means not set" survive the crossing.
//
// MIGRATION: no HTML is produced, held or trusted. Message wording relayed by the API
//   descends from legacy resource files in which raw markup is commonplace - anchors,
//   list items, paragraphs, line breaks, and in a handful of values a script block,
//   one of them in this very feature's own resource file - so that text is untrusted.
//   This store holds structured problem documents and plain values, exposes no
//   pre-sanitised or "safe markup" member, and reaches for no sanitiser: escaping
//   belongs to the renderer, and Angular's default text interpolation escapes by
//   construction.
//
// MIGRATION: the sibling slices that do NOT exist here, so that nobody looks for them.
//   Profile definitions, module definitions, desktop modules, the permissions
//   catalogue, roles, role groups and the page tree are all outside this store's
//   concern - each has its own owner, and several of them are unpaged collections whose
//   handling would otherwise be duplicated. The return address the legacy screens kept
//   in view state is the router's. Permission decisions are the server's. And there is
//   no notification slice: announcing a failure is the error interceptor's job, already
//   done by the time a failure reaches the classifier above, so reporting it a second
//   time from here would show an operator the same sentence twice.
