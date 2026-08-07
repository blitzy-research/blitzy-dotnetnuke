/**
 * The account-administration store: the paged listing and its three prefix searches,
 * the selected account, that account's profile, the credential operations, the
 * tenant's account policy, and the tenant's profile declarations.
 *
 * ## Why this file exists at all
 *
 * The transport beside it is deliberately dull — nineteen methods, one endpoint each,
 * every one returning a stream that it never subscribes to. That leaves exactly one
 * thing unowned, and this file owns it: COMPOSITION. Sequencing two calls so that the
 * second can use what the first returned, holding what came back, recording what
 * failed, reconciling a listing after a write, and deciding when a request is worth
 * dispatching at all. None of that belongs in a transport, because a transport that
 * decided any of it would have to decide it the same way for every screen.
 *
 * Equally, none of it belongs in a component. The legacy screens are the argument:
 * `Website/admin/Users/Users.ascx.vb` interleaved the query, the paging arithmetic,
 * the search branch, the grid binding and the page lifecycle in one file, which is
 * why the same account query could not be reused by a picker without re-deriving it.
 *
 * What this file does NOT own is stated once here and enforced throughout:
 *
 * - **No transport.** No address is built here, no query string is assembled, no
 *   header is set and no response code is read. The transport owns the endpoint
 *   catalogue and the parameter encoding.
 * - **No presentation.** No text is composed for display, no value is formatted, no
 *   markup is produced, and nothing is ever marked as trusted markup for rendering.
 *   The one advisory boolean about pager visibility below is derived state, and the
 *   decision to render remains the shared pagination component's.
 * - **No validation.** Length, composition, confirmation match and required-field
 *   rules are the server's, and a form may add a convenience check of its own. This
 *   file adds none, and in particular does not tighten the credential policy.
 * - **No authorisation.** See the note on the server being authoritative, below.
 * - **No caching.** See the note on the legacy cache, below.
 *
 * ## Signals, and nothing else
 *
 * Every slice is a writable signal held privately and published through
 * `asReadonly()`, so a component can observe state but cannot reach in and change it;
 * every derived value is a `computed()`; every update replaces rather than mutates,
 * because an in-place mutation would not change the reference an `OnPush` consumer
 * compares. There is no external store library here and no long-lived multicast
 * source standing in for state — the framework's own reactive primitive is the whole
 * mechanism.
 *
 * There is no `effect()` in this file, and the omission is deliberate rather than an
 * oversight: an effect is for a genuine side effect, and every side effect this store
 * performs is a request dispatched from an explicit command that a caller invoked.
 * Reacting to a signal change by dispatching a request would make the dispatch
 * implicit, and would make the order of two dispatches depend on scheduling rather
 * than on the sequence the caller wrote. The request-linked and derived-writable
 * primitives are likewise unused, for the same reason: nothing here needs state that
 * re-derives itself from a request, because the request is always commanded.
 *
 * MIGRATION: control state is eliminated entirely, and there is markedly less to
 * translate than to delete. The two direct ancestors of the slices below are page
 * state and selection state that the legacy application round-tripped through the
 * rendered page on every postback: `Website/admin/Users/ManageUsers.ascx.vb`
 * L174-L185 kept the page number in control state, seeding a private field to 0 at
 * L176, reading it back at L177-L178 and writing it at L183; and
 * `Library/Components/Users/UserModuleBase.vb` L466-L505 kept the account identifier
 * there too, seeding it from the integer null marker at L468, testing for its absence
 * at L469, and writing it at L491 and L503. Those two are the whole of the control
 * state in the five in-scope library trees, and they become two ordinary signals. A
 * third, the selected profile declaration at
 * `Website/admin/Users/EditProfileDefinition.ascx.vb` L120-L126, becomes a third.
 *
 * MIGRATION: the return-address key that appears eleven times across the
 * administration pages — for instance in `Website/admin/Portal/SiteSettings.ascx.vb`
 * and `Website/admin/Portal/EditPortalAlias.ascx.vb` — is deliberately NOT a slice
 * here. It recorded where to navigate back to, which is the router's concern in this
 * application, and modelling it as store state would put navigation history in a
 * place no navigation happens.
 *
 * MIGRATION: server-session state has no counterpart because there was none to carry.
 * A direct search for session access across both the five in-scope library trees and
 * the thirty-nine administration code-behinds finds zero sites, so the plan's
 * "Session state" input is satisfied vacuously. No analogue is manufactured, and no
 * general key-and-value bag imitating control state is built: a bag would reproduce
 * the very indirection that made a mis-spelled key a run-time failure rather than a
 * compilation one.
 *
 * ## The server is authoritative, and this store does not second-guess it
 *
 * MIGRATION: authorisation moved server-side, completely.
 * `Library/Components/Users/UserModuleBase.vb` L466-L505 embedded a full
 * authorisation decision INSIDE a page property getter — comparing the caller's
 * identifier against the requested one at L474, short-circuiting for an installation
 * administrator at L476, re-reading the requested account at L481 to check at L484
 * that a tenant administrator was not editing an installation administrator, and
 * redirecting to a denial page at L494 when none of that held. Not one line of it is
 * reproduced here. The API decides, and reports a refusal as a status with a problem
 * document; this store records that failure and presents it as a refusal rather than
 * as a fault. The permission vocabulary decides nothing on this side.
 *
 * That extends to the rules that look like they could be pre-checked and cannot be.
 * A tenant that has reached its account allowance is refused on create; an attempt to
 * change an installation administrator is refused on update. Counting accounts first
 * would cost a request, would race every other administrator, and would still have to
 * handle the refusal it was trying to predict.
 *
 * MIGRATION: the tenant is not a parameter anywhere in this file. The API resolves one
 * portal per request from the host the caller reached it on, reconciled against the
 * alias table, so a tenant identifier sent from here would either be redundant or be
 * a second, disagreeing opinion about which tenant was meant. The tenant reported by
 * {@link UserStore.observedPortalId} is therefore an OBSERVATION of what came back,
 * never an instruction about what to ask for.
 *
 * MIGRATION: the legacy cache is not reproduced on this side.
 * `Library/Components/Providers/Caching/DataCache.vb` is reached from a hundred and
 * sixteen in-scope call sites, fifteen of them from
 * `Library/Components/Users/UserController.vb`, each pairing a static read with a
 * write whose expiry was a per-entity timeout multiplied by a global performance
 * factor, and invalidating by clearing a whole tenant or the whole installation.
 * There is no cache map here, no expiry instant, no time-to-live and no staleness
 * marker. What replaces it is stated plainly in the reconciliation note below: after
 * a write, this store re-reads.
 *
 * MIGRATION: user-facing wording is authored in the templates that show it, with the
 * thirty-seven localised resource files read only as the reference for what the
 * legacy screens actually said. The legacy mechanism was specific to the abandoned
 * presentation framework, and no translation runtime is added to this workspace, so
 * nothing here holds a resource key or resolves one.
 *
 * ## Reconciliation after a write, and why it is a re-read
 *
 * A write here is followed by a re-read of whatever it could have changed, rather than
 * by a local edit that guesses the result. Two reasons, and the second is the
 * load-bearing one. A create or a delete moves rows between pages under an ordering
 * this side does not own, so a locally spliced list would be right only until it was
 * not. And the paging facts are the server's: the page count in particular is
 * computed there and documented as read-only, so adjusting a total locally after a
 * delete would fabricate a server fact and give a pager two sources of truth. Where
 * the server hands back the written entity, that entity is adopted directly — which
 * is a reconciliation from the server's own answer, not an optimistic guess.
 */

import { Injectable, type OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';

import {
  DEFAULT_PAGE_SIZE,
  emptyPagedResult,
  type ApiMeta,
  type PagedResult,
  type SortDirection,
} from '../models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../models/problem-details.model';
/*
 * The profile surface is imported from its own contract rather than restated here.
 * `user.model.ts` declares in as many words that it deliberately does not carry the
 * profile shapes and that `profile.model.ts` is their single home, warning that a
 * second copy of one wire contract would leave a caller unable to tell which of the
 * two the server actually honours. Six of the nineteen transport methods this store
 * calls name these types in their signatures, so they are consumed from that single
 * home.
 */
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
  MembershipSettings,
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


// ---------------------------------------------------------------------------
// THE SEARCH AXIS, AS A TYPED DISCRIMINATOR
// ---------------------------------------------------------------------------

/**
 * Which search the account listing is currently applying.
 *
 * MIGRATION: the legacy screen chose its query by comparing the search text against
 * LOCALISED STRINGS — `Website/admin/Users/Users.ascx.vb` L258, L261 and L264 each
 * compared it against a resource lookup, and L266 against a bare magic string — so
 * the query a person got depended on the language the page was rendered in, and any
 * one of those words could not be searched for even though each is an ordinary thing
 * to type. Every branch is a typed discriminator here, and no user-supplied text is
 * ever compared against a reserved word.
 */
export type UserSearchMode = 'none' | 'all' | 'username' | 'email' | 'profileProperty';

/**
 * The search the listing is applying, as a closed discriminated union.
 *
 * The four searchable modes correspond exactly to the branches the legacy screen
 * offered that survive into the target endpoint surface.
 *
 * MIGRATION: two legacy list modes are DROPPED rather than reproduced, and this union
 * has no member for either. `Users.ascx.vb` L258-L260 answered one of them with
 * `UserController.GetUnAuthorizedUsers` and hid the pager, and L261-L263 answered the
 * signed-in-accounts view from session tracking and hid the pager too. Neither took a
 * page coordinate at all, so each returned an unbounded set, and the second depended
 * on session tracking and a scheduled purge that this migration does not carry
 * forward — the purge job at `Library/Components/Users/Users Online/PurgeUsersOnline.vb`
 * L44 is an intentional omission. No mode below restores either, and this store holds
 * no slice of currently-signed-in accounts.
 *
 * The approval axis is a separate, PAGED filter — see
 * {@link UserStore.setApprovalFilter} — and is emphatically not a restoration of the
 * first of those two modes.
 */
export type UserSearch =
  | {
      /**
       * No query has been dispatched and none should be.
       *
       * MIGRATION: this is the successor to the bare magic string the legacy screen
       * compared against at `Users.ascx.vb` L266, which fell through every branch and
       * left the grid unbound. In the target it is expressed by NOT DISPATCHING —
       * {@link UserStore.loadUsers} returns without a request in this mode — and never
       * by transmitting a reserved word as a filter.
       */
      readonly mode: 'none';
    }
  | {
      /**
       * Every account in the tenant, paged and unfiltered.
       *
       * MIGRATION: the successor to `Users.ascx.vb` L264-L265, which is a FOURTH
       * legacy branch distinct from the three prefix searches and from the no-query
       * case: it called `UserController.GetUsers` with page coordinates and no filter
       * whatsoever. In the target that is the listing with no filter member present.
       * The distinction from the mode above is real and is preserved: this one
       * dispatches a request that matches everything, that one dispatches nothing.
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

      /**
       * The profile property to match on.
       *
       * MIGRATION: an OPEN SET, and deliberately a plain string. `Users.ascx.vb`
       * L272-L274 passed its field name straight through to
       * `GetUsersByProfileProperty` as the property name, and appended it to the
       * screen's own query string at L275, because a tenant may declare whatever
       * profile properties it likes. It is therefore never validated here, never
       * case-normalised, and never checked against a fixed list — the names come from
       * {@link UserStore.profileDefinitions}, and an unknown one is the server's to
       * refuse.
       */
      readonly propertyName: string;

      /** The caller's text, raw and exactly as typed. */
      readonly text: string;
    };

// ---------------------------------------------------------------------------
// FAILURE
// ---------------------------------------------------------------------------

/**
 * Which command a recorded failure belongs to.
 *
 * One member per transport method, so a screen showing several slices at once can tell
 * whether the listing failed or the credential change did, instead of showing one
 * message against all of them. The union is closed because the command surface is.
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
  | 'deleteProfileDefinition';

/**
 * A failure, as this store records it.
 *
 * The STRUCTURED problem document is retained alongside the summary derived from it,
 * and no message is ever composed, decorated or turned into markup here. That is a
 * safety property rather than a stylistic one: measured across the thirty-seven
 * in-scope localised resource files, seventy-six values carry an HTML tag — the
 * histogram runs list item 60, paragraph 58, line break 57, heading 42, anchor 38,
 * strong 34, bold 32, and four of them carry a live script element, one of which sits
 * in `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx`. Legacy message
 * text is therefore untrusted markup by measurement, and the legacy code knew it:
 * `Website/admin/Security/AccessDenied.ascx.vb` L43 encoded the message it had just
 * decoded before showing it. Nothing here is ever handed to a template as trusted
 * markup, and no sanitiser is involved, because nothing is treated as markup at all.
 */
export interface UserFailure {
  /** The command that failed. */
  readonly operation: UserOperation;

  /**
   * The problem document exactly as it arrived, or null when the failure carried none.
   *
   * Held so that a caller needing the machine-readable detail has it. Its per-field
   * dictionary is an index-signature map, so a member of it is read with a bracket —
   * property access on it is a compilation error in this workspace by design. In
   * practice no read is needed, because {@link UserFailure.summary} already carries the
   * per-field messages in the order the document listed them.
   */
  readonly problem: ProblemDetails | null;

  /**
   * The failure summarised by the one function in the workspace that decides severity
   * and wording.
   *
   * MIGRATION: a permission refusal is a WARNING, not an error, and that outcome is
   * delegated rather than re-decided here. `Website/admin/Security/AccessDenied.ascx.vb`
   * is fifty lines that perform no permission check at all — the page only PRESENTS a
   * denial — and both branches of its load handler, at L43 and L45, render at the
   * warning message type rather than the error one. The shared summariser already
   * returns the warning severity for that status, so this store obtains the required
   * behaviour by asking it, and encoding the rule a second time here is exactly the
   * duplication that would let the two disagree.
   *
   * Its support reference is retained deliberately: it is the correlation value the
   * server validated for the request, which is the only join key between what a person
   * saw in the browser and the request as the server recorded it.
   */
  readonly summary: ProblemSummary;

  /**
   * The machine-readable failure code the document carried, or null when it carried
   * none.
   *
   * A STRING, always. MIGRATION: the legacy outcome vocabularies are numeric and their
   * ordinals disagree with each other in ways that make keying on a number unsafe —
   * account creation numbers its members explicitly and succeeds at THIRTEEN
   * (`Library/Components/Users/Membership/UserCreateStatus.vb` L23-L42, whose zero
   * member is not an outcome but the initial no-error-yet marker, as
   * `Website/admin/Users/User.ascx.vb` L175 and L185 prove by treating any other value
   * as a failure), the credential-change vocabulary assigns NO explicit values so its
   * declaration order is its ordinal and it succeeds at ZERO
   * (`Library/Components/Users/Membership/PasswordUpdateStatus.vb` L23-L32), and the
   * sign-in vocabulary succeeds at ONE. An assumption that zero means success is wrong
   * two times in three. Neither ordinal crosses the boundary: an outcome arrives as a
   * status and a code string, and that is what this member holds.
   */
  readonly code: string | null;
}

// ---------------------------------------------------------------------------
// PURE HELPERS
// ---------------------------------------------------------------------------

/**
 * The filter members of the listing query that a search contributes.
 *
 * Derived from the query contract with a projection rather than restated, so a change
 * to the contract's member names cannot leave a stale copy here compiling.
 */
type UserSearchFilter = Pick<
  UserListQuery,
  'userName' | 'email' | 'profilePropertyName' | 'profilePropertyValue'
>;

/** The ordering members of the listing query. */
type UserOrdering = Pick<UserListQuery, 'sortBy' | 'sortDir'>;

/**
 * Translates a search into the filter members the listing query carries.
 *
 * MIGRATION: THE TRAILING WILDCARD IS THE SERVER'S, AND IS NOT ADDED HERE. The legacy
 * screen composed its pattern at the call site — `Users.ascx.vb` L269, L271 and L274
 * each appended one trailing per-cent character to the search text before calling down
 * — and the API reproduces that, wildcard included. Appending one here would produce a
 * doubled pattern; leading with one would silently turn a starts-with into a contains.
 * These three filters are PREFIX matches and must never be described as containing,
 * fuzzy or wildcard searches.
 *
 * The text is passed through untouched for the same reason: not trimmed, not
 * case-folded and not encoded. Trimming would make a leading space unsearchable,
 * case-folding would presume a collation this side does not know, and encoding is the
 * transport's business.
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
      // Unreachable while the union holds, and kept regardless: the union is a
      // compile-time guarantee, and a value that crossed a boundary at run time could
      // carry anything. Omitting every filter is the safe answer, and the workspace
      // requires a switch to be exhaustive in any case.
      return {};
  }
}

/**
 * Assembles the ordering members, omitting each one that has not been chosen.
 *
 * A direction without a field is meaningless, so the direction is only carried when a
 * field is present; the server applies its own ascending default when the direction is
 * omitted.
 *
 * @param sortBy The field to order by, or undefined when the caller expressed no
 * preference.
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
 * Recovers the problem document from a failure, without assuming what the failure is.
 *
 * A value reaching a subscriber's error path is `unknown` and nothing more: the shared
 * error interceptor re-throws whatever it received, and an operator between here and
 * there may throw anything at all. Reading a member off such a value without narrowing
 * would yield undefined and compare unequal to every branch that consumed it.
 *
 * The failed-response object is read STRUCTURALLY, by indexed access, rather than by
 * importing the transport's response type — this file takes no transport dependency,
 * and the body a failed response carries is reached the same way either side of that
 * decision.
 *
 * ## The carrier is never itself tested, and the ordering below is load-bearing
 *
 * The shared narrowing predicate is deliberately PERMISSIVE about absence, because
 * every subset of the standard members is legal, so one well-typed member is enough for
 * a value to pass. A failed response always carries a numeric status, which is one of those
 * members — so testing the carrier itself would ALWAYS succeed, would return the
 * carrier in place of the document, and would silently discard the body along with the
 * machine-readable code and the support reference inside it. The body slot is therefore
 * consulted first and the carrier is tested only when there is no body slot at all,
 * which is the case for a document an operator threw directly.
 *
 * A transport status of zero is resolved BEFORE the body is read, for the same reason
 * the shared interceptor resolves it first: the framework puts a DOM progress event in
 * the body slot when a response never arrived, and such an event carries a string
 * `type` member that the permissive predicate accepts — so reading the body first would
 * mistake a network failure for a problem document that happens to say nothing.
 *
 * @param cause The value the subscriber's error path received.
 * @returns The problem document, or null when the failure carried none.
 */
function readProblemDetails(cause: unknown): ProblemDetails | null {
  if (typeof cause !== 'object' || cause === null || Array.isArray(cause)) {
    return null;
  }

  const carrier = cause as Record<string, unknown>;

  // No body slot means this is not a failed response. It may still be a problem
  // document thrown directly, which is the one case where testing the value itself is
  // the right question. The plural per-field member of a document is spelled
  // differently from the singular body slot, so the two cannot be confused.
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

  // A problem document that arrived as text, because the caller asked for text or
  // because a reverse proxy answered instead of the API. Parsed here rather than
  // discarded, since the machine-readable code and the support reference are the two
  // things worth keeping from a failure.
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
 * Read structurally for the same reason as the body above.
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
 * The TRANSPORT status is preferred over the one repeated inside the document. The two
 * agree for every response this API produces, but a body written by a reverse proxy
 * rather than by the API carries no status at all, and the transport status is the one
 * that is always present. The document's own status is the fallback, for the case where
 * an operator threw a document with no response around it.
 *
 * @param document The problem document, or null when the failure carried none.
 * @param transportStatus The status the transport reported, or null when it reported
 * none.
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
 * Attaches the OBSERVED status to a problem document that omitted one, so that severity
 * can be decided at all.
 *
 * This authors no text and invents no field, and it never overwrites a status the
 * server wrote: a document that already carries one is returned untouched, because
 * rewriting server-authored data would misreport what the server actually said.
 * The attachment matters because severity is derived from the status, and a document
 * without one is judged at the most forceful severity by default — which would present
 * a permission refusal as a fault, the one outcome the legacy precedent forbids.
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

/**
 * Holds and sequences every piece of account-administration state.
 *
 * Provided at the root and injected, never declared as a provider on a component: the
 * listing, the editor and the profile screen are looking at one account and one
 * tenant policy, and a per-component instance would give each of them a private copy
 * that the others' writes could not reach.
 *
 * ## Concurrency
 *
 * Each slice keeps at most one request in flight, and starting a new one CANCELS the
 * previous. That is not tidiness: without it, a person paging quickly can have two
 * listing requests outstanding and the slower one can answer last, leaving the screen
 * showing a page nobody asked for. Cancellation is per slice rather than global, so
 * loading a profile does not abandon the listing behind it.
 *
 * ## Failure
 *
 * There is ONE failure slot, holding the most recent failure together with the command
 * it belongs to. Every command clears it before dispatching, so a retry never shows the
 * previous attempt's message, and a caller that cares which slice failed reads
 * {@link UserFailure.operation}. A refusal is recorded exactly as it arrived and is
 * presented at the severity the shared summariser decides.
 */
@Injectable({ providedIn: 'root' })
export class UserStore implements OnDestroy {
  private readonly transport = inject(UserService);

  /*
   * ⚠ THIS STORE HOLDS THE MOST SENSITIVE STATE IN THE APPLICATION: names, email addresses,
   * telephone numbers, postal addresses and free-text profile answers, for accounts belonging
   * to ONE TENANT and read on the authority of ONE OPERATOR. Ending a session discards the
   * credential; it does not discard anything read with it, so without an explicit discard a
   * previous operator's account listing and the profile last opened would still be here for
   * whoever signs in next.
   *
   * That discard is not arranged here. `core/state/session-teardown.service.ts` and
   * `core/state/session-lifecycle.service.ts` each call this store's own `reset()` — the
   * first from the transport layer when a renewal is refused and from the identity store, the
   * second from the shell on a deliberate sign-out — and `reset()` cancels the in-flight reads
   * and writes before clearing the slices, so nothing still in the air can refill them.
   *
   * ⚠ DO NOT ADD A REGISTRATION CALL HERE; see the note in `portal.store.ts` for why the
   * fan-out belongs to the two services and not to the stores.
   */
  // -------------------------------------------------------------------------
  // WRITABLE SLICES
  // -------------------------------------------------------------------------

  /**
   * The page of accounts in hand, rows and paging facts together.
   *
   * Seeded with the shared empty envelope rather than with null, so a template renders
   * an empty listing before the first response instead of branching on absence. The
   * seed reports the same coordinates a server response carries for an unpaged,
   * zero-record answer, which is what makes the branch unnecessary.
   */
  private readonly _users = signal<PagedResult<UserListItem>>(emptyPagedResult<UserListItem>());

  /**
   * The page of records to return, counted from zero, as most recently REQUESTED.
   *
   * MIGRATION: the index on the wire is ZERO-BASED, and no arithmetic is performed on
   * it here. The legacy screen kept a one-based counter — `Users.ascx.vb` L51 declares
   * it as 1 — and subtracted one immediately before every call down, at L265, L269,
   * L271 and L274; the provider's own paging set its row lower bound to the page size
   * multiplied by the index, so index zero addresses the first row. The provider's base
   * is the one this application carries. Corroborated independently by
   * `ManageUsers.ascx.vb` L176, whose page field seeds at 0.
   *
   * The shared pagination component is zero-based too and converts to a one-based
   * position for display inside itself, so there is no base conversion anywhere in this
   * file: nothing here adds one, and nothing subtracts one.
   */
  private readonly _requestedPageIndex = signal<number>(0);

  /** The search the listing is applying. Nothing is dispatched in the initial mode. */
  private readonly _search = signal<UserSearch>({ mode: 'none' });

  /** The field to order by, or undefined to accept the server's own ordering. */
  private readonly _sortField = signal<UserSortField | undefined>(undefined);

  /** The direction to order in, or undefined to accept the server's ascending default. */
  private readonly _sortDirection = signal<SortDirection | undefined>(undefined);

  /**
   * Restrict the listing to authorised or to unauthorised accounts, or undefined for
   * both.
   *
   * Undefined rather than a defaulted boolean, because false MEANS "only the
   * unauthorised ones" and is transmitted as false. The legacy absence marker for a
   * boolean was itself false — `Library/Components/Shared/Null.vb` L76-L80 — so the two
   * were indistinguishable there and had to be separated here.
   */
  private readonly _approvalFilter = signal<boolean | undefined>(undefined);

  /**
   * The account selected for editing, or undefined when none is.
   *
   * MIGRATION: the successor to the control-state slot at
   * `Library/Components/Users/UserModuleBase.vb` L466-L505, which seeded itself from
   * the integer null marker at L468 — that is, from minus one — and tested for absence
   * at L469 with an explicit is-nothing comparison rather than a truthiness test.
   *
   * Undefined is the ONLY expression of "none selected" here. Minus one is not
   * available for it and neither is zero, because both are legitimate identifiers in
   * this schema: the tenant table seeds its key at minus one and the role, page and
   * module tables seed theirs at zero, while minus one is simultaneously the legacy
   * marker for a missing integer. Account identifiers themselves seed at one, but the
   * same discipline is applied so that no screen reasons differently from the next.
   * Every test against this slice is an explicit comparison with undefined.
   */
  private readonly _selectedUserId = signal<number | undefined>(undefined);

  /** The selected account in full, or null when none has been read. */
  private readonly _selectedUser = signal<UserDetail | null>(null);

  /** The selected account's profile, or null when none has been read. */
  private readonly _profile = signal<UserProfile | null>(null);

  /** The tenant's account policy, or null when it has not been read. */
  private readonly _membershipSettings = signal<MembershipSettings | null>(null);

  /**
   * The tenant's profile declarations, in the order the server returned them.
   *
   * UNPAGED, and deliberately so: the transport returns a plain array, and this store
   * holds no page index, page size or total for it. Nor is it re-sorted here — position
   * among siblings is a field on the declaration and the server's ordering is the
   * authority.
   */
  private readonly _profileDefinitions = signal<readonly ProfilePropertyDefinition[]>([]);

  /**
   * The profile declaration selected for editing, or undefined when none is.
   *
   * MIGRATION: the successor to the control-state slot at
   * `Website/admin/Users/EditProfileDefinition.ascx.vb` L120-L126, and subject to the
   * same identifier discipline as the selected account above.
   */
  private readonly _selectedPropertyDefinitionId = signal<number | undefined>(undefined);

  /** The selected profile declaration in full, or null when none has been read. */
  private readonly _selectedProfileDefinition = signal<ProfilePropertyDefinition | null>(null);

  private readonly _usersLoading = signal<boolean>(false);
  private readonly _selectedUserLoading = signal<boolean>(false);
  private readonly _profileLoading = signal<boolean>(false);
  private readonly _membershipSettingsLoading = signal<boolean>(false);
  private readonly _profileDefinitionsLoading = signal<boolean>(false);

  /** Whether a write is in flight. Distinct from a read, so a form can disable itself. */
  private readonly _saving = signal<boolean>(false);

  /** The most recent failure, or null when nothing has failed since it was last cleared. */
  private readonly _failure = signal<UserFailure | null>(null);

  // -------------------------------------------------------------------------
  // IN-FLIGHT REQUESTS
  // -------------------------------------------------------------------------

  /*
   * One handle per READ slice, so that starting a read cancels the previous read of the
   * same slice and nothing else. A cancelled read has no server-side consequence, which
   * is what makes cancelling it safe.
   */
  private listRequest: Subscription | null = null;
  private detailRequest: Subscription | null = null;
  private profileRequest: Subscription | null = null;
  private settingsRequest: Subscription | null = null;
  private definitionsRequest: Subscription | null = null;
  private definitionRequest: Subscription | null = null;

  /*
   * Every WRITE still outstanding. A write is never superseded by a later one: abandoning a
   * write client-side does not undo it server-side, so cancelling one because a second was
   * issued would leave this store confident about a change it can no longer observe.
   * Concurrency is instead surfaced through the saving flag, which a form binds to disable its
   * own submit. The handles exist so that a SESSION BOUNDARY and teardown can release them.
   *
   * ⚠ A `Set` OF HANDLES RATHER THAN ONE `Subscription` CONTAINER, AND THE CHANGE FIXES A REAL
   * TRAP. An RxJS `Subscription` used as a container is CLOSED once unsubscribed, and anything
   * added to a closed container is unsubscribed the instant it is added. With a container, the
   * first session boundary would release the writes correctly and then silently cancel EVERY
   * SUBSEQUENT WRITE for the remaining life of the application — every save after one sign-out
   * would appear to be dispatched and never report an outcome. A set is emptied and reused.
   */
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

  /** The tenant's profile declarations, unpaged and in the server's order. */
  readonly profileDefinitions = this._profileDefinitions.asReadonly();

  /** The profile declaration selected for editing, or undefined when none is. */
  readonly selectedPropertyDefinitionId = this._selectedPropertyDefinitionId.asReadonly();

  /** The selected profile declaration in full, or null when none has been read. */
  readonly selectedProfileDefinition = this._selectedProfileDefinition.asReadonly();

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

  /** Whether a write is in flight. */
  readonly saving = this._saving.asReadonly();

  /** The most recent failure, or null when nothing has failed. */
  readonly failure = this._failure.asReadonly();

  // -------------------------------------------------------------------------
  // DERIVED STATE
  // -------------------------------------------------------------------------

  /** The rows on the page in hand. */
  readonly userRows = computed<readonly UserListItem[]>(() => this._users().items);

  /**
   * The paging facts locating the page in hand within the whole match set.
   *
   * Read from the envelope, where they live nested together, and never recombined from
   * loose values.
   */
  readonly pageMeta = computed<ApiMeta>(() => this._users().meta);

  /**
   * The page of records the SERVER reported returning, counted from zero.
   *
   * This, not the requested index, is what a pager should be bound to. The shared
   * pagination component states the reason: a consumer rebinds only once the requested
   * page has actually been fetched, which is what stops a pager from claiming to be on
   * a page whose request failed.
   */
  readonly currentPageIndex = computed<number>(() => this._users().meta.pageIndex);

  /** The page of records most recently requested, counted from zero. */
  readonly requestedPageIndex = this._requestedPageIndex.asReadonly();

  /**
   * The total no of records that satisfy the criteria, counted across every page.
   *
   * Therefore not the number of rows in hand: the two coincide only when the whole
   * match set fits on one page.
   */
  readonly totalCount = computed<number>(() => this._users().meta.totalCount);

  /**
   * The number of pages the match set spans.
   *
   * SERVER-COMPUTED and read as given. It is deliberately not divided out from the
   * total and the page size here: the contract documents the value as the server's, and
   * a locally derived figure that disagreed would give a pager two sources of truth.
   */
  readonly totalPages = computed<number>(() => this._users().meta.totalPages);

  /** The size of the page the server actually applied to the payload in hand. */
  readonly appliedPageSize = computed<number>(() => this._users().meta.pageSize);

  /**
   * The size of the page to request.
   *
   * MIGRATION: this is a PER-TENANT SETTING and is never a constant in this file.
   * `Users.ascx.vb` L114-L119 read it from the tenant's records-per-page module
   * setting through `UserModuleBase.GetSetting`, and
   * `Library/Components/Users/UserModuleBase.vb` L134-L136 supplied ten only when that
   * setting was unset. The setting is part of the account policy this store reads, so
   * the policy's value is preferred whenever the policy is in hand and the shared
   * fallback is used only in its absence — which is exactly the division of
   * responsibility the paging contract documents for that constant.
   *
   * MIGRATION: the coerced read at `Users.ascx.vb` L117 narrowed an untyped setting
   * straight to an integer with no guard, which the pre-strict compiler permitted
   * (`Website/release.config` L125 compiled the administration pages with strict mode
   * off). Here the value arrives already typed by the policy contract, so the coercion
   * has no counterpart at all.
   *
   * The value is passed on UNCLAMPED. The paging contract states that nothing is
   * corrected, coerced or clamped on either side of the wire and that the server
   * reports an out-of-range size as a field-level refusal, so a tenant that has
   * configured a size the server will not accept learns so from the server. Silently
   * substituting a different size would be this store deciding a rule that is not its
   * to decide, and would hide a misconfiguration instead of surfacing it — the legacy
   * screen passed its setting on unchecked in the same way.
   */
  readonly effectivePageSize = computed<number>(() => {
    const settings = this._membershipSettings();

    if (settings === null) {
      return DEFAULT_PAGE_SIZE;
    }

    return settings.recordsPerPage;
  });

  /** Whether the page in hand carries any rows. */
  readonly hasRecords = computed<boolean>(() => this._users().items.length > 0);

  /**
   * Whether nothing at all matched, as distinct from having paged past the end.
   *
   * The two deserve different wording, which is why the total is consulted rather than
   * the row count.
   */
  readonly isEmptyResult = computed<boolean>(() => this._users().meta.totalCount === 0);

  /** Whether the requested page lies beyond a match set that is not itself empty. */
  readonly isPastEnd = computed<boolean>(() => {
    const page = this._users();

    return page.items.length === 0 && page.meta.totalCount > 0;
  });

  /**
   * Whether a pager is warranted for the page in hand.
   *
   * ADVISORY ONLY. MIGRATION: the rule is the legacy one, reproduced exactly.
   * `Users.ascx.vb` L278-L280 narrowed its pager's visibility to the case where the
   * page size was smaller than the total, and did so ONLY when the tenant had asked for
   * the pager to be suppressed — when it had not, the pager stayed as the branch left
   * it. The suppression flag is a plain boolean on the account policy and false is DATA
   * on it, not an absence, so it is compared explicitly.
   *
   * Whether to RENDER a pager remains the shared pagination component's decision, and
   * that component declines to render for a genuinely unpaged resource on its own
   * terms. This value exists so that a screen can express the tenant's preference
   * without restating the rule.
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
   * The profile property names a property search may name.
   *
   * Taken from the tenant's own declarations, which is what makes the third search axis
   * an open set rather than a fixed list. Empty until the declarations have been read;
   * a name absent from it is still the server's to refuse rather than this store's to
   * reject.
   */
  readonly profilePropertyNames = computed<readonly string[]>(() =>
    this._profileDefinitions().map((definition) => definition.propertyName),
  );

  /** Whether the tenant has declared any profile property. */
  readonly hasProfileDefinitions = computed<boolean>(
    () => this._profileDefinitions().length > 0,
  );

  /**
   * The selected account's profile values, or an empty sequence when no profile has
   * been read.
   */
  readonly profileValues = computed(() => {
    const held = this._profile();

    if (held === null) {
      return [];
    }

    return held.properties;
  });

  /**
   * Whether the selected account must change its credential before it can proceed, or
   * undefined when no account has been read.
   *
   * Three states, deliberately. MIGRATION: the legacy absence marker for a boolean was
   * false (`Library/Components/Shared/Null.vb` L76-L80) and its absence test reported
   * false as absent, so "not set" and "no" were the same value there. They are not the
   * same here: the flag on the account contract is a plain, non-nullable boolean and
   * false is DATA. Undefined below means only that no account has been read, and is
   * produced by this store rather than by the wire.
   */
  readonly selectedUserMustChangePassword = computed<boolean | undefined>(() => {
    const held = this._selectedUser();

    if (held === null) {
      return undefined;
    }

    return held.mustChangePassword;
  });

  /**
   * The tenant the loaded records were read THROUGH, or undefined when nothing has been
   * read.
   *
   * An observation, never an instruction: the API resolves the tenant per request and
   * takes no tenant parameter, so this cannot influence what is asked for. It is
   * published because a screen showing several slices benefits from being able to say
   * which tenant it is looking at.
   *
   * Zero and minus one are both legitimate values here — the tenant table seeds its key
   * at minus one, so the first tenant really is minus one and the second really is zero
   * — which is why absence is undefined and is tested for as undefined.
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
      this._saving(),
  );

  /**
   * How forcefully to present the most recent failure, or undefined when there is none.
   *
   * Delegated: a permission refusal resolves to a warning here because the shared
   * summariser decides so, not because this file re-decides it.
   */
  readonly failureSeverity = computed(() => {
    const held = this._failure();

    if (held === null) {
      return undefined;
    }

    return held.summary.severity;
  });

  /**
   * The per-field messages the most recent failure reported, in the order the document
   * listed them.
   *
   * Empty when nothing failed or when the failure reported nothing per field. The
   * dictionary underlying these is an index-signature map whose keys are the server's
   * model-state keys and are NOT camel-cased; it is read by the shared summariser with
   * bracket access, which is the only form this workspace permits, and is not re-read
   * here.
   */
  readonly failureFieldMessages = computed(() => {
    const held = this._failure();

    if (held === null) {
      return [];
    }

    return held.summary.fieldMessages;
  });

  /**
   * The machine-readable code the most recent failure carried, or undefined when there
   * is no failure and null when the failure carried no code.
   *
   * A conflict code is surfaced verbatim so that a screen can key on it exactly as the
   * server spelled it.
   */
  readonly failureReasonCode = computed<string | null | undefined>(() => {
    const held = this._failure();

    if (held === null) {
      return undefined;
    }

    return held.code;
  });

  /**
   * The support reference to quote when reporting the most recent failure, or undefined
   * when there is none to quote.
   *
   * The correlation value the server validated for the request, which is the only join
   * key between what a person saw in the browser and the request as the server recorded
   * it. Diagnostic: quote it, do not present it as an explanation.
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
   * Releases every request still outstanding when the injector holding this store is
   * destroyed.
   *
   * A root-provided store lives as long as the application, so this runs at shutdown
   * and in a test that destroys its environment between specs — which is precisely
   * where an unreleased request would leak across specs and make one spec's failure
   * depend on another's timing.
   */
  ngOnDestroy(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE LISTING
  // -------------------------------------------------------------------------

  /**
   * Brings the store up for a listing screen: reads the tenant's account policy, then
   * reads the first page at the size that policy declares.
   *
   * MIGRATION: THIS SEQUENCE IS THE WHOLE REASON COMPOSITION LIVES HERE. The size of a
   * page is a per-tenant setting rather than a constant — `Users.ascx.vb` L114-L119 read
   * it from the tenant's records-per-page setting, whose fallback of ten lives at
   * `Library/Components/Users/UserModuleBase.vb` L134-L136 — so the listing cannot be
   * requested correctly until the policy that declares that size is in hand. A transport
   * cannot sequence the two calls without deciding for every screen that they belong
   * together, and a component that sequenced them would re-derive the same order on
   * every screen that lists accounts.
   *
   * The listing is dispatched whether or not the policy could be read. A tenant whose
   * policy is unavailable still has accounts, and the shared fallback size exists for
   * exactly this case; the policy failure remains recorded, so nothing is hidden.
   *
   * Idempotent in the sense that it may be called again to re-read both.
   */
  initialise(): void {
    this._failure.set(null);
    this.dispatchSettings(true);
  }

  /**
   * Re-reads the current page with the current search, ordering and filters.
   *
   * Dispatches NOTHING while no search has been chosen. MIGRATION: that is the successor
   * to `Users.ascx.vb` L266, where a bare magic string fell through every branch and left
   * the grid unbound — the legacy screen genuinely issued no query in that state, and
   * neither does this. It is distinct from the unfiltered listing, which
   * {@link showAllAccounts} dispatches and which really does ask the server for
   * everything.
   */
  loadUsers(): void {
    this._failure.set(null);
    this.dispatchUsers();
  }

  /**
   * Moves to another page of the current match set.
   *
   * @param pageIndex The page of records to return, counted from ZERO. Passed on
   * exactly as supplied: nothing is added to it, subtracted from it or clamped. The
   * shared pagination component emits only an index inside the available range, and the
   * server refuses a negative index with a field-level message rather than
   * reinterpreting it, so a caller learns that a request was malformed instead of
   * quietly receiving a neighbouring page.
   */
  goToPage(pageIndex: number): void {
    this._failure.set(null);
    this._requestedPageIndex.set(pageIndex);
    this.dispatchUsers();
  }

  /**
   * Lists every account in the tenant, paged and unfiltered.
   *
   * MIGRATION: the successor to `Users.ascx.vb` L264-L265, a fourth legacy branch that
   * called the unfiltered paged reader. In the target it is the listing with no filter
   * member present at all.
   */
  showAllAccounts(): void {
    this.applySearch({ mode: 'all' });
  }

  /**
   * Lists accounts whose sign-in name STARTS WITH the given text.
   *
   * MIGRATION: the successor to `Users.ascx.vb` L270-L271. A prefix match, not a
   * containing one, and the trailing wildcard belongs to the server.
   *
   * @param text The caller's text, raw and exactly as typed. Not trimmed, not
   * case-folded, not decorated and not encoded here.
   */
  searchByUsername(text: string): void {
    this.applySearch({ mode: 'username', text });
  }

  /**
   * Lists accounts whose electronic-mail address STARTS WITH the given text.
   *
   * MIGRATION: the successor to `Users.ascx.vb` L268-L269. The address is not an
   * identifier and is not required to be unique — the legacy provider was registered
   * with uniqueness switched off at `Website/release.config` L244 — so this can
   * legitimately match several accounts.
   *
   * @param text The caller's text, raw and exactly as typed.
   */
  searchByEmail(text: string): void {
    this.applySearch({ mode: 'email', text });
  }

  /**
   * Lists accounts whose named profile property STARTS WITH the given text.
   *
   * MIGRATION: the successor to `Users.ascx.vb` L272-L274, the third search axis, whose
   * field name was passed straight through as the property name. The name is an OPEN SET
   * and is not validated, case-normalised or checked against a fixed list here: a tenant
   * may declare whatever properties it likes, {@link profilePropertyNames} publishes the
   * ones it has declared, and an unrecognised name is the server's to refuse.
   *
   * @param propertyName The profile property to match on.
   * @param text The caller's text, raw and exactly as typed.
   */
  searchByProfileProperty(propertyName: string, text: string): void {
    this.applySearch({ mode: 'profileProperty', propertyName, text });
  }

  /**
   * Drops the search and lists every account in the tenant again.
   *
   * Resolves to the unfiltered listing rather than to the no-query state, because
   * clearing a filter on a listing screen means "show me everything", not "show me
   * nothing". The no-query state is reachable through {@link reset}.
   */
  clearSearch(): void {
    this.applySearch({ mode: 'all' });
  }

  /**
   * Orders the listing by a field, or hands the ordering back to the server.
   *
   * @param sortBy The field to order by, or undefined to accept the server's own
   * ordering. Only the fields the listing actually supports are expressible, so an
   * unsupported name is a compilation error rather than a rejected request.
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
   * The two tokens are the server's own member names; an abbreviated or lower-cased
   * spelling is refused, which is why the type admits neither.
   */
  setSortDirection(sortDir: SortDirection | undefined): void {
    this._failure.set(null);
    this._sortDirection.set(sortDir);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /**
   * Restricts the listing to authorised or to unauthorised accounts, or to neither
   * restriction.
   *
   * MIGRATION: this is a PAGED filter over the account table and is NOT a restoration of
   * the legacy unpaged unauthorised-accounts view at `Users.ascx.vb` L258-L260, which
   * took no page coordinate at all and hid the pager. The distinction matters: that view
   * returned an unbounded set, and this one returns a page.
   *
   * @param isApproved The state to restrict to, or undefined to include both. False is
   * transmitted as false and means "only the unauthorised ones"; it is not an absence,
   * even though the legacy absence marker for a boolean was itself false.
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

  /**
   * Selects an account and reads it in full.
   *
   * The previously held account and profile are dropped as soon as the selection
   * changes, so a screen cannot render one account's details beside another's profile
   * while the second is still arriving.
   *
   * @param userId The account to select. Passed on exactly as supplied and never
   * inspected first: no identifier in this file is guarded on being truthy, positive or
   * non-negative, because this schema makes every such guard wrong somewhere. Account
   * keys seed at one, tenant keys seed at minus one, and role, page and module keys seed
   * at zero, while minus one is simultaneously the legacy marker for a missing integer.
   * One vocabulary cannot carry both meanings, so absence is expressed by undefined and
   * by nothing else.
   */
  selectUser(userId: number): void {
    this._failure.set(null);

    if (this._selectedUserId() !== userId) {
      this._selectedUser.set(null);
      this._profile.set(null);
      this._selectedUserId.set(userId);
    }

    this.dispatchUser(userId);
  }

  /**
   * Clears the selection and everything read for it.
   *
   * Sets the selection to undefined. MIGRATION: the legacy slot at
   * `Library/Components/Users/UserModuleBase.vb` L468 expressed the same state as minus
   * one, which this schema also uses as a real identifier; undefined removes the
   * collision rather than inheriting it.
   */
  clearSelectedUser(): void {
    this.cancelDetailReads();
    this._selectedUserId.set(undefined);
    this._selectedUser.set(null);
    this._profile.set(null);
  }

  /**
   * Creates an account in the tenant and selects it.
   *
   * The created account is adopted from the server's own answer rather than assembled
   * from the request, so the identifier it issued and any value it defaulted are the
   * ones held. The listing is then re-read, because where the new row falls depends on an
   * ordering this side does not own.
   *
   * MIGRATION: the tenant's account allowance is the server's rule and is not
   * pre-checked here; a tenant that has reached it is refused with a permission status
   * naming the rule, which the failure slot records as a refusal rather than as a fault.
   *
   * MIGRATION: nothing about the credential is validated here, and the credential itself
   * is never retained. The request travels to the transport and is not written into any
   * slice, so no part of this store ever holds a password.
   *
   * @param request The account to create.
   */
  createUser(request: CreateUserRequest): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.create(request).subscribe({
        next: (created: UserDetail) => {
          this._saving.set(false);
          this._selectedUserId.set(created.userId);
          this._selectedUser.set(created);
          this._profile.set(null);
          this.dispatchUsers();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('createUser', cause);
        },
      }),
    );
  }

  /**
   * Updates an account's own details.
   *
   * The written account is adopted from the server's answer. It replaces the held
   * selection only when it IS the selection, so updating one account from a listing does
   * not silently change which account another pane is showing. The listing is re-read
   * because the columns it shows include the members that were just written.
   *
   * MIGRATION: the update contract carries no sign-in name, because the legacy source
   * marked that field read-only and offered no rename; no credential, no approval flag
   * and no lockout flag, because each is changed through its own command below. That
   * separation is what keeps a routine details edit from silently carrying an
   * authorisation change.
   *
   * MIGRATION: an attempt to update an installation administrator is refused by the
   * server with a permission status. It is not pre-checked here — see the note on
   * authorisation at the head of this file, and the legacy check embedded in a page
   * property getter at `Library/Components/Users/UserModuleBase.vb` L466-L505 that this
   * store deliberately does not reproduce.
   *
   * @param userId The account to update. Passed on exactly as supplied.
   * @param request The members to write.
   */
  updateUser(userId: number, request: UpdateUserRequest): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.update(userId, request).subscribe({
        next: (written: UserDetail) => {
          this._saving.set(false);

          if (this._selectedUserId() === userId) {
            this._selectedUser.set(written);
          }

          this.dispatchUsers();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('updateUser', cause);
        },
      }),
    );
  }

  /**
   * Removes one account.
   *
   * MIGRATION: removal is PER ACCOUNT, and there is no bulk command here or anywhere.
   * `Users.ascx.vb` L326-L328 declared a routine whose single provider call destroyed an
   * unbounded number of accounts from one click, with no per-row confirmation and no way
   * to review the set first. That is not carried forward; a caller names what it is
   * removing.
   *
   * The response carries no body, so the listing is RE-READ rather than edited locally.
   * Splicing the row out here would additionally require adjusting the total, and the
   * paging facts are the server's — the page count in particular is computed there and
   * documented as read-only, so a locally adjusted total would fabricate a server fact
   * and would leave a pager with two sources of truth.
   *
   * @param userId The account to remove. Passed on exactly as supplied.
   */
  deleteUser(userId: number): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.delete(userId).subscribe({
        next: () => {
          this._saving.set(false);

          if (this._selectedUserId() === userId) {
            this.clearSelectedUser();
          }

          this.dispatchUsers();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('deleteUser', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE PROFILE
  // -------------------------------------------------------------------------

  /**
   * Reads one account's profile.
   *
   * MIGRATION: a profile is a set of rows keyed by the tenant's own declarations, not a
   * fixed field list. `Library/Components/Users/Profile/UserProfile.vb` declared nineteen
   * members of which seventeen were hardcoded named fields, so anything a tenant added
   * was reachable only through a separate untyped collection. None of those fields is
   * reproduced in any slice here.
   *
   * @param userId The account whose profile to read. Passed on exactly as supplied.
   */
  loadProfile(userId: number): void {
    this._failure.set(null);
    this.dispatchProfile(userId);
  }

  /**
   * Writes one account's profile values.
   *
   * The response carries no body, so the profile is re-read afterwards rather than
   * assumed from the submission: the server records the instant each value was last
   * written, and a locally assembled profile would carry no such instant or a wrong one.
   *
   * @param userId The account whose profile to write. Passed on exactly as supplied.
   * @param submission The values to write, each with the visibility to apply.
   */
  saveProfile(userId: number, submission: UserProfileSubmission): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.updateProfile(userId, submission).subscribe({
        next: () => {
          this._saving.set(false);
          this.dispatchProfile(userId);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('saveProfile', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — CREDENTIALS
  // -------------------------------------------------------------------------

  /**
   * Changes an account's credential on behalf of the account holder, who supplies the
   * credential in force alongside the replacement.
   *
   * MIGRATION: THE POLICY IS PRESERVED VERBATIM AND IS NOT TIGHTENED, and it is enforced
   * server-side. As shipped, the legacy provider required a seven-character credential
   * (`Website/release.config` L242), required none of it to be non-alphanumeric (L243) and
   * did not require an address to be unique (L244); it enabled reset (L240) and required
   * no question-and-answer pair (L241). Raising any of those during a migration would lock
   * out every existing account that satisfies the old rule and not the new one. This store
   * checks none of it — not a length, not a composition, not the match between the
   * replacement and its confirmation, which is a server answer.
   *
   * MIGRATION: NO CREDENTIAL IS EVER HELD, LOGGED OR PUBLISHED. The request is handed to
   * the transport and is not written into any slice, no slice has a member that could
   * carry one, and no response in this store carries one either — the replacement store is
   * a one-way hash. The legacy arrangement is why the point is laboured: the provider was
   * registered with a reversible format and with retrieval switched on
   * (`release.config` L245 and L239) and the symmetric key that reversed it was committed
   * to source control in the clear at L89-L93, with an identical copy in the development
   * configuration, so anyone who could read the repository could read every stored
   * credential.
   *
   * The response carries no body, so the selected account is re-read when it is the
   * account that was changed: a credential change moves the instant it was last changed,
   * and can clear the obligation to change it. Nothing is assumed about either.
   *
   * @param userId The account whose credential to change. Passed on exactly as supplied.
   * @param request The credential in force and its replacement.
   */
  changePassword(userId: number, request: ChangePasswordRequest): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.changePassword(userId, request).subscribe({
        next: () => {
          this._saving.set(false);
          this.reconcileSelectedAccount(userId);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('changePassword', cause);
        },
      }),
    );
  }

  /**
   * Resets an account's credential on behalf of an administrator, who does not supply the
   * credential in force.
   *
   * A separate command from {@link changePassword} rather than a mode of it, because the
   * two differ in what they require and in who may call them.
   *
   * MIGRATION: A RESET IS CARRIED FORWARD; RETRIEVAL IS NOT. The two were enabled
   * independently by the legacy provider and only retrieval required a reversible store,
   * so only retrieval is abolished. There is deliberately NO recover-it, remind-me or
   * reveal-it command on this store, and none could be written: the transport exposes no
   * method that returns a credential.
   *
   * @param userId The account whose credential to reset. Passed on exactly as supplied.
   * @param request The replacement credential.
   */
  resetPassword(userId: number, request: ChangePasswordRequest): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.passwordReset(userId, request).subscribe({
        next: () => {
          this._saving.set(false);
          this.reconcileSelectedAccount(userId);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('resetPassword', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — ACCOUNT STATE TRANSITIONS
  // -------------------------------------------------------------------------

  /**
   * Sets one account's approval state.
   *
   * The state is stated explicitly rather than implied by a verb, because the server
   * reports setting the state an account already holds as a conflict — an answer that is
   * only meaningful if the caller said which state it meant.
   *
   * Both the listing and the selected account carry the approval flag, so both are
   * re-read: the response carries no body to adopt.
   *
   * @param userId The account to set. Passed on exactly as supplied.
   * @param isApproved The state to set. Transmitted either way; false is DATA here, not
   * an absence.
   */
  setApproval(userId: number, isApproved: boolean): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.setApproval(userId, isApproved).subscribe({
        next: () => {
          this._saving.set(false);
          this.reconcileSelectedAccount(userId);
          this.dispatchUsers();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('setApproval', cause);
        },
      }),
    );
  }

  /**
   * Releases one account that repeated failed sign-in attempts have locked out.
   *
   * Both the listing and the selected account carry the lockout flag, so both are
   * re-read.
   *
   * @param userId The account to release. Passed on exactly as supplied.
   */
  unlockUser(userId: number): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.unlock(userId).subscribe({
        next: () => {
          this._saving.set(false);
          this.reconcileSelectedAccount(userId);
          this.dispatchUsers();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('unlockUser', cause);
        },
      }),
    );
  }

  /**
   * Obliges one account to change its credential at its next sign-in.
   *
   * Sets the obligation only: it does not choose, generate, transmit or return a
   * credential.
   *
   * Only the SELECTED ACCOUNT is re-read, and the listing deliberately is not: the
   * obligation appears on the full account contract and on no column of the listing, so
   * re-reading the listing would cost a request that could not change a single rendered
   * value.
   *
   * MIGRATION: the legacy state behind this was one member of a five-member status
   * vocabulary that the sign-in path resolved BY PRECEDENCE, so it could report exactly
   * one condition at a time even when several held at once. The successor is a set of
   * INDEPENDENT advisory flags, which means combinations the legacy field could not
   * express — an expired credential on an account that also owes a profile update — are
   * now expressible. That is a real divergence from legacy behaviour rather than a
   * cosmetic one. This command writes one of those facts and reads none of them.
   *
   * @param userId The account to oblige. Passed on exactly as supplied.
   */
  requirePasswordChange(userId: number): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.requirePasswordChange(userId).subscribe({
        next: () => {
          this._saving.set(false);
          this.reconcileSelectedAccount(userId);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('requirePasswordChange', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE TENANT'S ACCOUNT POLICY
  // -------------------------------------------------------------------------

  /**
   * Reads the tenant's account policy on its own, without touching the listing.
   *
   * MIGRATION: the legacy application returned this as an untyped hash table from
   * `Library/Components/Users/UserController.vb` L656, so every caller had to know both
   * the key spelling and the value type and a mistake in either failed at run time. It is
   * a typed contract here, and the records-per-page member of it is what
   * {@link effectivePageSize} prefers over the shared fallback.
   */
  loadMembershipSettings(): void {
    this._failure.set(null);
    this.dispatchSettings(false);
  }

  /**
   * Writes the tenant's account policy.
   *
   * The response carries no body, so the policy is re-read afterwards. The listing is
   * re-read as well, because the policy declares the size of a page and the listing in
   * hand was fetched at the previous size — leaving it alone would show a page whose size
   * contradicts the setting that was just saved.
   *
   * MIGRATION: the credential policy is NOT part of this contract. Minimum length, the
   * non-alphanumeric requirement and the address-uniqueness rule are server-side options
   * that never cross the boundary, so this command cannot tighten them and no slice here
   * restates their values.
   *
   * @param request The policy to write.
   */
  saveMembershipSettings(request: MembershipSettings): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.updateMembershipSettings(request).subscribe({
        next: () => {
          this._saving.set(false);
          this.dispatchSettings(true);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('saveMembershipSettings', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — THE TENANT'S PROFILE DECLARATIONS
  // -------------------------------------------------------------------------

  /**
   * Reads the tenant's profile declarations.
   *
   * UNPAGED: the transport returns a plain array and this store holds no page index, page
   * size or total for it. Nor is the array re-sorted here — position among siblings is a
   * field on each declaration and the server's ordering is the authority.
   */
  loadProfileDefinitions(): void {
    this._failure.set(null);
    this.dispatchDefinitions();
  }

  /**
   * Selects one profile declaration and reads it in full.
   *
   * @param propertyDefinitionId The declaration to select. Spelled as the PROPERTY
   * definition, which is load-bearing on both sides of the wire: the route constrains an
   * integer under that name and the contract spells its identity member the same way, so
   * a near-miss produces a route that does not match rather than a parameter that is
   * quietly ignored. Passed on exactly as supplied and never inspected first.
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
   * Declares a new profile property for the tenant.
   *
   * The declaration list is re-read afterwards rather than appended to, because where the
   * new declaration falls depends on the position field and on the server's ordering.
   *
   * @param request The declaration to create, position included.
   */
  createProfileDefinition(request: CreateProfilePropertyDefinitionRequest): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.createProfileDefinition(request).subscribe({
        next: (created: ProfilePropertyDefinition) => {
          this._saving.set(false);
          this._selectedPropertyDefinitionId.set(created.propertyDefinitionId);
          this._selectedProfileDefinition.set(created);
          this.dispatchDefinitions();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('createProfileDefinition', cause);
        },
      }),
    );
  }

  /**
   * Replaces one profile declaration.
   *
   * MIGRATION: THIS IS ALSO HOW ORDERING IS CHANGED, and there is deliberately no
   * move-up or move-down command. Position among siblings is a FIELD on the declaration,
   * and the legacy pair of buttons was never an operation on one row:
   * `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L187 read the neighbouring
   * declaration's position and SWAPPED the two, while a separate bulk pass at L326
   * renumbered a whole set from each item's index. Modelling a two-row write as a
   * one-row command would have made it look atomic when it is not. Deciding which
   * positions to write — swapping a pair, renumbering after a drag — belongs to the
   * feature, because only the feature knows the set it is looking at; this store writes
   * the position it is given.
   *
   * @param propertyDefinitionId The declaration to replace. Passed on exactly as
   * supplied.
   * @param request The members to write, position included.
   */
  updateProfileDefinition(
    propertyDefinitionId: number,
    request: UpdateProfilePropertyDefinitionRequest,
  ): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.updateProfileDefinition(propertyDefinitionId, request).subscribe({
        next: (written: ProfilePropertyDefinition) => {
          this._saving.set(false);

          if (this._selectedPropertyDefinitionId() === propertyDefinitionId) {
            this._selectedProfileDefinition.set(written);
          }

          this.dispatchDefinitions();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('updateProfileDefinition', cause);
        },
      }),
    );
  }

  /**
   * Removes one profile declaration.
   *
   * A declaration that cannot be removed — because values are recorded against it, or
   * because the tenant requires it — is refused with a status and a problem document,
   * which reaches the failure slot rather than leaving a silently unchanged list.
   *
   * @param propertyDefinitionId The declaration to remove. Passed on exactly as supplied.
   */
  deleteProfileDefinition(propertyDefinitionId: number): void {
    this._failure.set(null);
    this._saving.set(true);

    this.track(
      this.transport.deleteProfileDefinition(propertyDefinitionId).subscribe({
        next: () => {
          this._saving.set(false);

          if (this._selectedPropertyDefinitionId() === propertyDefinitionId) {
            this.clearSelectedProfileDefinition();
          }

          this.dispatchDefinitions();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('deleteProfileDefinition', cause);
        },
      }),
    );
  }

  // -------------------------------------------------------------------------
  // COMMANDS — HOUSEKEEPING
  // -------------------------------------------------------------------------

  /** Discards the recorded failure, so a screen can dismiss a message. */
  clearFailure(): void {
    this._failure.set(null);
  }

  /**
   * Returns every slice to its initial state and abandons every read in flight.
   *
   * The search returns to the no-query state, which is the one state in which
   * {@link loadUsers} dispatches nothing. Writes already in flight are NOT abandoned, for
   * the reason given where they are dispatched: abandoning one client-side would not undo
   * it server-side.
   */
  reset(): void {
    // ⚠ WRITES ARE RELEASED HERE TOO, WHICH READS ALONE DID NOT DO. A write's callback selects
    // an account, re-reads the listing and records an outcome; left listening across a session
    // boundary it performs all three on behalf of the session that ended, repopulating slices
    // this method has just cleared with the PREVIOUS OPERATOR'S accounts — personal data, shown
    // to whoever signed in next, with no command issued to explain where it came from.
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
    this._profileDefinitions.set([]);
    this._selectedPropertyDefinitionId.set(undefined);
    this._selectedProfileDefinition.set(null);

    this._usersLoading.set(false);
    this._selectedUserLoading.set(false);
    this._profileLoading.set(false);
    this._membershipSettingsLoading.set(false);
    this._profileDefinitionsLoading.set(false);
    this._saving.set(false);
    this._failure.set(null);
  }

  // -------------------------------------------------------------------------
  // INTERNALS
  // -------------------------------------------------------------------------

  /**
   * Adopts a search, returns to the first page and reads.
   *
   * The page is reset because a new search produces a different match set, and asking for
   * the fifth page of a set that now has one page would answer with nothing at all while
   * the pager insisted there was something there. The legacy screen re-bound from its
   * first page for the same reason.
   *
   * @param search The search to apply.
   */
  private applySearch(search: UserSearch): void {
    this._failure.set(null);
    this._search.set(search);
    this.returnToFirstPage();
    this.dispatchUsers();
  }

  /** Returns the requested page to the first one, which is index zero. */
  private returnToFirstPage(): void {
    this._requestedPageIndex.set(0);
  }

  /**
   * Assembles the listing query from the slices that describe it.
   *
   * Every optional member is OMITTED rather than sent empty, because empty text, zero and
   * false are all legitimate values on this contract and a member that is present is a
   * member the server will act on.
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
   * Reads the listing, unless no search has been chosen.
   *
   * Does not clear the failure slot, so that a failure recorded by whatever sequenced this
   * read survives it — which is what lets {@link initialise} report an unreadable policy
   * while still listing the accounts.
   */
  private dispatchUsers(): void {
    if (this._search().mode === 'none') {
      // MIGRATION: the no-query state issues no request at all, exactly as the legacy
      // screen's fall-through at `Users.ascx.vb` L266 left its grid unbound. The loading
      // flag is cleared so a screen entering this state does not spin for a request that
      // will never be made.
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

  /**
   * Reads one account in full.
   *
   * MIGRATION: A SUCCESSFUL READ ALWAYS CARRIES AN ACCOUNT. The endpoint answers `200` with the
   *   account or refuses with a not-found problem document, so an identifier matching nothing
   *   reaches the error handler and is announced. This handler used to accept a successful `null`
   *   and commit it, which showed a blank account record as though the read had succeeded — a
   *   response the server cannot send and a state a screen cannot explain. The SLICE stays
   *   nullable, because "no account selected" is a real state of this store; what is gone is the
   *   idea that the transport reports absence that way.
   *
   * @param userId The account to read.
   */
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
   * A successful read carries the profile, declarations included. An identifier naming no
   * account is a not-found problem document and arrives at the error handler, so no blank
   * profile is ever committed as a success.
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
   * Reads the tenant's account policy, and optionally reads the listing once it has
   * arrived.
   *
   * The listing follows on BOTH outcomes when it has been asked for. On success it uses
   * the size the policy declares; on failure it uses the shared fallback, which is what
   * that constant documents itself as being for. Dispatching only on success would leave
   * a tenant with an unreadable policy unable to see its accounts at all, which is a
   * worse answer than a listing at the default size beside a recorded failure.
   *
   * @param thenReadListing Whether to read the listing once the policy has been resolved.
   */
  private dispatchSettings(thenReadListing: boolean): void {
    this._membershipSettingsLoading.set(true);
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = this.transport.getMembershipSettings().subscribe({
      // A successful read carries the whole policy. A tenant the server cannot resolve is a
      // not-found problem document and lands in the error handler below, where the listing still
      // follows at the shared fallback size.
      next: (settings: MembershipSettings) => {
        this._membershipSettings.set(settings);
        this._membershipSettingsLoading.set(false);

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
      error: (cause: unknown) => {
        this._membershipSettingsLoading.set(false);
        this.recordFailure('loadMembershipSettings', cause);

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
    });
  }

  /**
   * Reads the listing once the policy has been resolved, promoting the no-query state to
   * the unfiltered listing so that the first page actually appears.
   *
   * MIGRATION: this is where the fourth legacy branch is chosen. `Users.ascx.vb`
   * L264-L265 listed everything, paged and unfiltered, and that is the sensible opening
   * state for a listing screen; the no-query fall-through at L266 is the state a caller
   * reaches deliberately, through {@link reset}, and is not what a screen should open in.
   * A search already chosen is left exactly as it is.
   */
  private readListingAfterSettings(): void {
    if (this._search().mode === 'none') {
      this._search.set({ mode: 'all' });
      this.returnToFirstPage();
    }

    this.dispatchUsers();
  }

  /**
   * Reads the tenant's profile declarations.
   */
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
   * Re-reads the selected account, but only when the account just written IS the selected
   * one.
   *
   * The guard matters: a listing screen can act on a row without having selected it, and
   * re-reading in that case would replace whichever account another pane was showing.
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
   * Records a failure against the command that produced it.
   *
   * The problem document is recovered, the observed transport status is attached when the
   * document carried none, severity and wording are DELEGATED to the shared summariser,
   * and the machine-readable code is extracted as a string. Nothing is composed here and
   * no ordinal is consulted.
   *
   * @param operation The command that failed.
   * @param cause The value the subscriber's error path received.
   */
  private recordFailure(operation: UserOperation, cause: unknown): void {
    const document: ProblemDetails | null = readProblemDetails(cause);
    const status: number | null = resolveStatus(document, readTransportStatus(cause));
    const problem: ProblemDetails | null = withObservedStatus(document, status);

    this._failure.set({
      operation,
      problem,
      summary: summarizeProblem(problem),
      code: failureCode(problem),
    });
  }

  /**
   * Holds a write's handle until it settles, so a session boundary and teardown can release it.
   *
   * ⚠ THE COMPLETION TEARDOWN IS WHAT MAKES A SET SAFE HERE. An RxJS `Subscription` container
   * detached a finished child by itself; a set does not, so a handle is removed on completion
   * explicitly. Without that the set would grow for the life of the application, one entry per
   * write ever issued.
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
   * Releases every write handle.
   *
   * Only for a session boundary and for teardown, for the reason recorded on the handles: a
   * write in flight is not otherwise abandoned, because releasing the handle stops the client
   * listening without undoing anything the server may already have committed.
   *
   * Iterated over a COPY, because each release removes its own handle from the set through the
   * teardown registered alongside it, and mutating a set while iterating it skips entries.
   */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();
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
    this.cancelDetailReads();
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
