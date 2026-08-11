/**
 * The account-administration store: the paged listing and its three prefix searches,
 * the selected account, that account's profile, the credential operations, the
 * tenant's account policy, and the tenant's profile declarations.
 *
 * ## Why this file exists at all
 *
 * The transport beside it is deliberately dull — twenty-four methods, one endpoint each,
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
import { Subscription, catchError, concatMap, finalize, from, of, tap, type Observable } from 'rxjs';

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


// ---------------------------------------------------------------------------
// THE TENANT'S OPENING-VIEW POLICY
// ---------------------------------------------------------------------------
//
// The three values `Display_Mode` may hold, named rather than left as integers at the one
// place that branches on them. They are the members of the legacy `DisplayMode` enumeration
// in their declared order, and the server publishes the setting as a plain integer because
// it publishes no closed code table for it - so the names live on the side that interprets
// the value.

/** `DisplayMode.All`: open on every account, paged and unfiltered. */
const DISPLAY_MODE_ALL = 0;

/** `DisplayMode.FirstLetter`: open on the first letter of the alphabet strip. */
const DISPLAY_MODE_FIRST_LETTER = 1;

/**
 * `DisplayMode.None`: open with no query at all.
 *
 * The default the legacy applied when the setting was absent
 * (`Library/Components/Users/UserModuleBase.vb` L126-L130), and the server reproduces that
 * default - so this is the mode a tenant that has configured nothing is published as having.
 */
const DISPLAY_MODE_NONE = 2;

/**
 * The letter the first-letter mode opens on.
 *
 * `Website/admin/Users/Users.ascx.vb` L502 took `Localization.GetString("Filter.Text")` and
 * kept its FIRST CHARACTER; the resource value in
 * `Website/admin/Users/App_LocalResources/Users.ascx.resx` is `"A,B,C,D,…,Z"`, so the
 * character is `A`. Held here rather than derived from a strip this store does not own, and
 * cited so the provenance is checkable.
 */
const OPENING_LETTER = 'A';

// ---------------------------------------------------------------------------
// THE SEARCH AXIS, AS A TYPED DISCRIMINATOR
// ---------------------------------------------------------------------------

/**
 * The letter a `FirstLetter` tenant's listing opens on.
 *
 * `A`, matching the legacy screen, which opened its alphabet strip on the first letter rather
 * than on a remembered one. Upper case because that is what the strip renders and what the
 * server's prefix comparison is insensitive to.
 */
const FIRST_LETTER_SEARCH_TEXT = 'A';

/**
 * The status the account-policy read answers with when the tenant stores no policy.
 *
 * ⚠ THIS IS AN "ABSENT" ANSWER WEARING AN ERROR'S STATUS, and the mismatch originates in the
 * transport rather than in either layer's intent. The Application layer returns a SUCCESSFUL outcome
 * carrying no value, and the shared response helper maps that onto `404` by a convention it documents
 * as "a nullable value on a successful outcome is how this solution expresses 'asked, and it is not
 * there'". There is no cleaner status for a GET whose answer is legitimately nothing.
 *
 * Named rather than inlined because the number alone reads as an error at the one place it is tested,
 * which is precisely the misreading that produced the defect it now prevents.
 *
 * ⚠ UNAMBIGUOUS ONLY BECAUSE THE TENANT IS RESOLVED FIRST. An unresolvable tenant never reaches this
 * read - the controller answers `403` with `portal.tenant_unresolved` before consulting the service,
 * measured directly against a non-aliased origin - so a `404` arriving here after a successful
 * authentication can only mean the resolved tenant has no policy to return.
 */
const MEMBERSHIP_SETTINGS_ABSENT_STATUS = 404;


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
  | 'applyProfileDefinitionEdits'
  | 'deleteProfileDefinition'
  | 'loadMemberServices'
  | 'subscribeToService'
  | 'cancelService'
  | 'startServiceTrial'
  | 'redeemServiceCode';

/**
 * One staged replacement in a profile-declaration batch.
 *
 * The pairing of an identifier with the members to write, because the endpoint addresses the
 * declaration in its path and carries the members in its body. Position is one of those
 * members, which is why re-ordering a set of declarations is expressed as a batch of
 * replacements rather than as a move command: `Website/admin/Users/ProfileDefinitions.ascx.vb`
 * L176-L193 swapped two positions and L326 renumbered a whole set, and both are the same write
 * seen from different distances.
 */
export interface ProfileDefinitionEdit {
  /** The declaration to replace. Passed on exactly as supplied. */
  readonly propertyDefinitionId: number;

  /** The members to write, position included. */
  readonly request: UpdateProfilePropertyDefinitionRequest;
}

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
/**
 * One row of a profile-declaration batch that the server refused, with the row it belongs to.
 *
 * ⚠ WHY A LIST AND NOT JUST THE SETTLED OUTCOME. The batch is ONE store command with ONE settled
 * result, which is what stops a screen mistaking a sibling's outcome for its own — but a batch can
 * refuse SEVERAL rows, and an operator told only "something was refused" cannot tell which of five
 * declarations to correct. The rows are independent and the batch is not a transaction, so each
 * refusal is a fact of its own and is kept as one. The screen names the property and announces one
 * sentence per entry.
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

/**
 * One settled write, identified.
 *
 * ## The defect this closes
 *
 * This store used to publish ONE boolean for "a write is in flight" and ONE failure slot, and it is
 * provided at the application root. Every screen that dispatched a write therefore watched the same
 * boolean fall and then read the same slot to learn its own outcome. Three distinct wrong answers
 * follow from that, and not one of them is visible from inside a single screen:
 *
 * - TWO WRITES, ONE FLAG. The account list dispatches a removal; a settings pane dispatches a save;
 *   the save settles first. The flag falls, and BOTH screens conclude their own write is done. The
 *   list clears the marker naming the row it was deleting, so the refusal that arrives afterwards has
 *   nothing left to attribute itself to and the row silently stays.
 * - SOMEBODY ELSE'S FAILURE. One write succeeds and another is refused. The successful one reads the
 *   shared slot, finds the other's refusal in it, and reports the refusal as its own outcome — so an
 *   operator is told the thing that worked did not.
 * - A REFUSAL SEEN AS A SUCCESS. Every dispatch clears the slot, so a refusal recorded by one write is
 *   erased by the next command anything issues. Whether a screen sees its own refusal at all depends
 *   on what else the application happened to do next, which is not a property of the write.
 *
 * The profile-declaration screen made the first of those routine rather than occasional: it launches a
 * BATCH of parallel writes, all against the one flag and the one slot.
 *
 * ## The correction
 *
 * Every write command returns the identifier it was issued, and the settled outcome is published under
 * that identifier carrying THAT WRITE'S OWN failure. A caller keeps the identifier it was handed and
 * acts only when the published result names it. The aggregate remains, because "is the store busy" is
 * a real question, but it can no longer be mistaken for "did my write finish": it is a count, so it
 * stays raised while anything is open.
 */
export interface UserMutation {
  /**
   * The identifier the store issued when this write was dispatched.
   *
   * Never zero. The counter pre-increments so that a caller may use zero to mean "no write of mine is
   * outstanding" without colliding with a real write.
   */
  readonly id: number;

  /** Which command settled. */
  readonly operation: UserOperation;

  /**
   * This write's own failure, or null when it succeeded.
   *
   * ⚠ READ THIS, NOT {@link UserStore.failure}, TO SETTLE A WRITE. This member is captured by the
   * write it belongs to and cannot be affected by anything another screen does. The shared slot is one
   * slot for the whole store and is cleared at every dispatch.
   */
  readonly failure: UserFailure | null;
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
   * Whether the tenant stores no account policy at all, as distinct from one that could not be read.
   *
   * ⚠ THIS DISTINCTION IS THE WHOLE POINT, because the slice beside it cannot express it. A null
   * policy means "no policy in hand" and arises from three unrelated situations — not read yet, read
   * and refused, read and legitimately absent — and every consumer that tests it for null was
   * therefore forced to treat an ordinary tenant as a broken one.
   *
   * MIGRATION: absence is a LEGITIMATE answer here, not a failure, and the server says so in its own
   * words. `UserService.GetMembershipSettingsAsync` returns `Success` carrying a null value and
   * records why: "A tenant with no settings source legitimately answers with no value, which is what
   * the legacy reader did ... and the screens that consumed it fell back to their own defaults.
   * Reporting a failure instead would change behaviour those screens depended upon." That traces to
   * `Library/Components/Users/UserController.vb:L656-L671`, where `GetUserSettings` located the
   * "User Accounts" module by definition name and assigned its result ONLY inside a not-nothing
   * guard, so a tenant without that module received `Nothing` and no error whatsoever.
   *
   * The transport nevertheless has to answer a GET with a status, and the shared translation helper
   * maps a successful outcome carrying no value onto `404` by documented convention. So the wire
   * cannot distinguish "absent" from "missing" on the status line alone, and this flag is where that
   * distinction is recovered and published once instead of being re-derived, differently, per screen.
   *
   * ⚠ ABSENCE IS NOT THE SAME AS WRITABILITY. A tenant reaching this state cannot store a policy
   * either: the write answers `404` with `user.membership_settings.source_missing` and the measured
   * sentence "Portal -1 has no \"User Accounts\" module instance to store membership settings
   * against." A consumer must therefore NOT read this flag as licence to offer a save.
   */
  private readonly _membershipSettingsUnconfigured = signal<boolean>(false);

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

  /**
   * The services offered to the account named by {@link _memberServicesAccountId}, with
   * whatever that account already holds against each of them.
   *
   * Replaces the `grdServices` grid of `Website/admin/Users/MemberServices.ascx`. Held as
   * one array rather than as two - offered and held - because the legacy grid was one grid:
   * a row carried both the service's terms and the account's assignment to it, and splitting
   * them here would need a join to render a row.
   */
  private readonly _memberServices = signal<readonly MemberService[]>([]);

  /**
   * The account the catalogue in hand belongs to, or `undefined` before one has been read.
   *
   * ⚠ HELD SO THAT A CATALOGUE CANNOT BE SHOWN AGAINST THE WRONG ACCOUNT. Every one of these
   * five endpoints is gated on account ownership, so a catalogue read for one account is
   * meaningless for another; publishing the account alongside the rows lets a screen assert
   * that what it is rendering is what it asked for. `undefined` and never a numeric marker,
   * because zero and minus one are both real identifiers in this schema.
   */
  private readonly _memberServicesAccountId = signal<number | undefined>(undefined);

  /**
   * What the last invitation code admitted the account to, or `null` when none has been
   * redeemed since the slice was last cleared.
   *
   * Retained because the legacy screen reported the outcome in words - `RSVPSuccess.Text`
   * against `RSVPFailure.Text` - and the successful half of that report is a LIST: one code
   * may join several roles, since the legacy walk had no early exit
   * (`MemberServices.ascx.vb:L397-L433`). A screen that only re-read the catalogue could say
   * that something changed but not what.
   */
  private readonly _lastRedemption = signal<RedeemServiceCodeResult | null>(null);

  /**
   * What the last account-policy write did beyond storing the values it was given, or `null`
   * when none has been written since the slice was last cleared.
   *
   * ⚠ RETAINED BECAUSE THE WRITE HAS AN EFFECT THE CALLER CANNOT PREDICT. Adopting a new
   * display-name format recomposes every account's stored display name in the tenant, so the
   * operator who saved the settings screen needs to be told that it happened and to how many
   * accounts. The legacy screen ran that sweep on a background thread
   * (`Website/admin/Users/UserSettings.ascx.vb:L175-L182` ->
   * `Library/Components/Users/UserController.vb:L1259-L1268`) and reported nothing at all.
   */
  private readonly _lastSettingsWrite = signal<MembershipSettingsUpdateResult | null>(null);

  private readonly _usersLoading = signal<boolean>(false);
  private readonly _selectedUserLoading = signal<boolean>(false);
  private readonly _profileLoading = signal<boolean>(false);
  private readonly _membershipSettingsLoading = signal<boolean>(false);
  private readonly _profileDefinitionsLoading = signal<boolean>(false);
  private readonly _memberServicesLoading = signal<boolean>(false);

  /**
   * How many writes are in flight.
   *
   * A COUNT AND NOT A FLAG, because this store is provided at the application root and several
   * screens write through it at once. See {@link UserMutation} for the three wrong answers the flag
   * gave; the short version is that a boolean cannot say WHICH write settled, so the first write to
   * finish reported every open write as finished.
   */
  private readonly _pendingWrites = signal<number>(0);

  /**
   * The most recently settled write, identified, or null when none has settled since the last reset.
   */
  private readonly _mutation = signal<UserMutation | null>(null);

  /**
   * The identifier last issued to a write.
   *
   * Pre-incremented, so the first identifier ever issued is 1 and zero is free for a caller to use
   * as "no write of mine is outstanding" without colliding with a real one.
   */
  private nextMutationId = 0;

  /**
   * How many staged replacements of the current declaration batch have still to be written.
   *
   * ⚠ A COUNT RATHER THAN A FLAG, and the count is what makes the batch's progress observable and
   * its overlap impossible. A single boolean cannot say how much of a batch is left, and the
   * defect this replaces was exactly that: one write per row, each lowering the shared saving flag
   * as it landed, so the flag fell on the FIRST completion while the rest were still in flight and
   * a second batch could be started on top of the first.
   *
   * Zero means no batch is running. It is never negative, because it is set from the size of the
   * batch and decremented once per settled row.
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
  private memberServicesRequest: Subscription | null = null;

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

  /**
   * Whether the tenant legitimately stores no account policy, as opposed to one that could not be read.
   *
   * True only after a read that the server answered with "absent". A read still outstanding, a read
   * that succeeded, and a read that genuinely failed all report false, so a consumer testing this can
   * rely on it meaning exactly one thing.
   *
   * ⚠ NOT A LICENCE TO OFFER A SAVE. See the backing slice: the same tenant cannot store a policy
   * either, so a screen reading this must explain the state rather than open a form over it.
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
   * Whether ANY write is in flight.
   *
   * ⚠ AN AGGREGATE, AND IT MUST NOT BE USED TO SETTLE A PARTICULAR WRITE. It answers "is this store
   * busy writing", which is the right question for a global busy indicator and the wrong question for
   * "has my write finished" — several screens write through this store at once, so it falls when the
   * FIRST of them settles. A caller waiting on its own write holds the identifier that command
   * returned and watches {@link UserStore.mutation}.
   */
  readonly saving = computed<boolean>(() => this._pendingWrites() > 0);

  /**
   * The most recently settled write: its identifier, its operation and its own outcome.
   *
   * The only correct way to settle a write. A caller compares the identifier against the one the
   * command handed it, and reads the failure from HERE rather than from {@link UserStore.failure} —
   * see {@link UserMutation}.
   */
  readonly mutation = this._mutation.asReadonly();

  /**
   * HOW MANY writes are in flight, not merely whether one is.
   *
   * A view over the same counter {@link UserStore.saving} reduces to a boolean. Exposed because the
   * count itself is the evidence that a shared flag was the wrong shape here, and the screen that
   * proves it is the profile declaration list: this store is provided at the root and every one of
   * its writes reports through one slice, so a boolean answers "is anybody writing", which is
   * indistinguishable from "is MY write finished" only while at most one write can be outstanding.
   * With a flag the FIRST write to answer set it false and every flag-watching flow concluded its
   * own write had finished, while the rest were still on the wire.
   *
   * A caller settling its OWN write still watches {@link UserStore.mutation} and compares the
   * identifier its command returned; this member is for asserting and displaying the aggregate.
   */
  readonly writesInFlight = this._pendingWrites.asReadonly();

  /**
   * How many staged replacements of the current declaration batch remain unwritten.
   *
   * Zero when no batch is running, so a screen can both report progress and refuse to start a
   * second batch. See {@link UserStore.applyProfileDefinitionEdits}.
   */
  readonly profileDefinitionBatchRemaining = this._profileDefinitionBatchRemaining.asReadonly();

  /**
   * Every row of the most recent profile-declaration batch that the server refused, in the order the
   * refusals arrived.
   *
   * Emptied when a batch is dispatched, so it always describes the latest one and never accumulates
   * across attempts. A batch that was wholly accepted leaves it empty, which is what lets a screen
   * announce nothing on a clean run.
   */
  readonly profileDefinitionBatchRefusals = this._profileDefinitionBatchRefusals.asReadonly();

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

  /**
   * Whether the listing has been asked for NOTHING, as distinct from having asked and matched
   * nothing.
   *
   * The two look identical on screen and mean opposite things: an empty match set says the tenant
   * has no account answering the query, while this says no query was ever issued and the tenant's
   * accounts are simply unrequested. A screen that showed the same "nothing found" wording for
   * both would state a falsehood in the second case, and the operator would act on it.
   *
   * MIGRATION: this is the state `Website/admin/Users/Users.ascx.vb` L266 left its grid in - every
   * branch of `BindData` excluded the bare marker `"None"`, so `grdUsers.DataSource` was assigned
   * `Nothing` and the grid rendered unbound with no message of any kind. It is reachable two ways:
   * a tenant whose `Display_Mode` selects it, which is also the default the legacy applied to an
   * absent setting (`Library/Components/Users/UserModuleBase.vb` L126-L130), and a caller that has
   * {@link reset} the store.
   */
  readonly noQueryIssued = computed<boolean>(() => this._search().mode === 'none');

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
   * Whether the account is offered any service at all.
   *
   * A tenant that publishes no public role offers nothing, which is an ordinary state and not
   * a failure - the legacy screen simply rendered an empty grid for it.
   */
  readonly hasMemberServices = computed<boolean>(() => this._memberServices().length > 0);

  /**
   * The services the account currently holds, lapsed ones included.
   *
   * Derived rather than read separately, because the catalogue already carries the assignment
   * state per row and a second request would be a second opinion about it.
   */
  readonly heldMemberServices = computed<readonly MemberService[]>(() =>
    this._memberServices().filter((offer: MemberService) => offer.isSubscribed),
  );

  /**
   * The services the account holds whose subscription has lapsed.
   *
   * The set the legacy screen labelled `Renew` rather than `Unsubscribe`
   * (`MemberServices.ascx.vb:L288-L305`). The lapsed test is the SERVER'S - each row carries
   * it decided - so nothing here compares a date against the browser's clock.
   */
  readonly lapsedMemberServices = computed<readonly MemberService[]>(() =>
    this._memberServices().filter((offer: MemberService) => offer.isExpired),
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
      this._memberServicesLoading() ||
      this.saving(),
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
   * Returns the listing to the state a first visit shows: no query chosen, no rows.
   *
   * ⚠ THIS EXISTS TO CLOSE A MEASURED CONTRADICTION BETWEEN THIS STORE AND THE SCREEN THAT
   * DISPLAYS IT. The listing screen's free-text box and its search-axis control are component
   * state, so they are reconstructed EMPTY every time the screen is mounted, while this store
   * outlives the screen and kept the previous search. Measured: filter the listing, open an
   * account, come back — the controls claimed no filter was applied, the strip showed no letter
   * applied, and the store nonetheless re-issued the retained search, so the operator was shown
   * an empty grid reading "Nothing to Display" with nothing on screen to explain why and no
   * control to undo. The rows and the controls described two different queries.
   *
   * The screen calls this on initialisation, so a fresh arrival is genuinely fresh and every
   * control on it is telling the truth. Distinct from {@link clearSearch}, which means "show me
   * everything" and DOES ask the server: this asks for nothing and then lets the screen's normal
   * opening path decide what to request, which is the point — it RESTORES the first-visit
   * behaviour rather than imposing a state of its own.
   *
   * ⚠ WHAT THE OPERATOR THEN SEES IS THE TENANT'S CHOICE, NOT AN EMPTY SCREEN, and that was
   * verified at runtime rather than assumed. {@link openingSearchForPolicy} derives the opening
   * search from the tenant's display-mode policy, so after this call the listing opens on whatever
   * that policy asks for — the unfiltered listing, an opening letter, or genuinely nothing when the
   * policy names the no-query mode. On a tenant whose membership settings are unavailable the
   * policy resolves to the unfiltered listing, so a return arrival was measured showing the full
   * listing with an empty search box and no letter applied: controls and rows agreeing, which is
   * the whole objective. An earlier draft of this remark claimed the screen would be left asking
   * for nothing; that was wrong, and only the description was — the behaviour is correct.
   *
   * It is also the faithful reading of the legacy screen, which carried its filter in the ADDRESS
   * (`Users.ascx.vb` `FilterURL`) — so arriving at a bare address with no filter in it showed no
   * filter — and whose opening state was likewise the tenant's display-mode policy rather than a
   * remembered one.
   *
   * Narrow on purpose. It touches ONLY the four slices that describe which records the listing is
   * asking for, and deliberately not {@link reset}: that one is the session boundary, and calling
   * it here would discard the tenant's membership settings and profile declarations that this
   * screen has just asked for, turning one stale query into several redundant requests.
   */
  resetSearchCriteria(): void {
    // Reads in flight belong to the query being abandoned. Left running, the later of the two
    // answers wins and repopulates the listing this method has just emptied.
    this.cancelReads();

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
   * Orders the listing by a field IN a direction, in one request.
   *
   * ⚠ WHY THIS EXISTS ALONGSIDE THE TWO SETTERS ABOVE. Each of those dispatches a read of its own, so a
   * caller expressing one ordering through both would issue TWO requests for one reader action - and the
   * first of the pair asks a question nobody wanted: the new field in the OLD direction. The second answer
   * would usually land last and hide it, but which answer lands last is not something a caller can
   * guarantee. This command changes both coordinates and then reads once, which is what a heading press in
   * the shared grid means.
   *
   * The single setters are kept for the callers that genuinely change one coordinate alone, and both remain
   * in use.
   *
   * @param sortBy The field to order by, or undefined to hand the ordering back to the server. Only the
   * fields the listing actually supports are expressible, so an unsupported name is a compilation error
   * rather than a rejected request.
   * @param sortDir The direction, or undefined to accept the server's default. Passing undefined for BOTH
   * arguments is how a caller clears the ordering entirely, which is the state the listing arrives in.
   */
  setSort(sortBy: UserSortField | undefined, sortDir: SortDirection | undefined): void {
    this._failure.set(null);
    this._sortField.set(sortBy);
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
   * Both the listing and the selected account carry the lockout flag, so both are
   * re-read.
   *
   * @param userId The account to release. Passed on exactly as supplied.
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
   * The policy is re-read afterwards rather than assembled from the request, because the
   * server normalises several members on the way in. The listing is re-read as well, because
   * the policy declares the size of a page and the listing in hand was fetched at the previous
   * size — leaving it alone would show a page whose size contradicts the setting that was just
   * saved.
   *
   * ⚠ THE RESPONSE CARRIES A REPORT, AND IT IS KEPT. Adopting a new display-name format
   * rewrites every account's stored display name in the tenant, which the caller cannot infer
   * from its own request; {@link lastSettingsWrite} is how a screen tells the operator what
   * happened. The report is retained even when it says nothing was rewritten, because "the
   * sweep ran and changed nothing" and "no sweep ran" are different answers and the operator
   * is looking for the difference.
   *
   * The re-read is dispatched BEFORE the report is published, for the same reason the
   * redemption command does it in that order: a re-read clears state that belongs to a
   * previous answer, and publishing first would let it discard the report it was meant to
   * accompany.
   *
   * MIGRATION: the credential policy is NOT part of this contract. Minimum length, the
   * non-alphanumeric requirement and the address-uniqueness rule are server-side options
   * that never cross the boundary, so this command cannot tighten them and no slice here
   * restates their values.
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
   * Discards the report of the last account-policy write.
   *
   * Exists so a screen can dismiss the notice it raised without re-reading anything. Separate
   * from {@link reset} because dismissing a notice is not abandoning the screen.
   */
  clearSettingsWriteReport(): void {
    this._lastSettingsWrite.set(null);
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
   * Writes a batch of staged declaration replacements, ONE AT A TIME, then re-reads the
   * catalogue ONCE.
   *
   * Legacy: `Website/admin/Users/ProfileDefinitions.ascx.vb` L446-L448 — the Apply handler called
   * `UpdateProperties()` and then `RefreshGrid()`. `UpdateProperties` (L291-L298) walked the
   * collection and called the update for each row whose dirty flag was up, SEQUENTIALLY, because
   * that is all a `For Each` inside one post-back can be; and the grid was rebound exactly once
   * afterwards. This method is that shape, restated for an asynchronous transport.
   *
   * ⚠ THIS EXISTS BECAUSE THE PER-ROW COMMAND WAS THE WRONG UNIT FOR A BATCH. A screen applying
   * N staged edits by calling {@link UserStore.updateProfileDefinition} N times produced N
   * concurrent writes AND up to N full catalogue re-reads — each write refreshing the whole
   * catalogue on its own completion — while the shared saving flag fell on the first write to
   * land, leaving a second batch startable on top of the first. Concurrency is bounded to ONE
   * here, the catalogue is read once after the last row settles, and the flag stays raised for
   * the whole batch.
   *
   * ⚠ THE BATCH IS NOT ATOMIC, AND THAT IS REPORTED RATHER THAN HIDDEN. The rows address
   * different declarations, so the server applies each on its own merits and a refusal of one
   * leaves the others applied. Every row is attempted — a refusal does not abandon the rows
   * behind it, which would strand work the operator asked for — and the FIRST failure is the one
   * recorded, because the failure slot holds one document and the first refusal is the one whose
   * cause the operator has to deal with. The single re-read afterwards is what lets a screen
   * derive exactly which rows are still outstanding: whatever still differs from the server.
   *
   * A second batch is REFUSED while one is running, silently and without contacting the server,
   * for the same reason the count exists — see
   * {@link UserStore.profileDefinitionBatchRemaining}. An empty batch is likewise a no-op: nothing
   * staged is nothing to write, and re-reading the catalogue to prove it would be a request
   * spent to change nothing.
   *
   * @param edits The staged replacements, applied in the order supplied.
   */
  applyProfileDefinitionEdits(edits: readonly ProfileDefinitionEdit[]): number {
    // ⚠ ZERO IS RETURNED WHEN NOTHING IS DISPATCHED, and zero is an identifier no write ever
    // holds, so a caller can hold the return value unconditionally: an empty batch and a batch
    // refused because one is already running are both "no write of mine is outstanding".
    if (edits.length === 0 || this._profileDefinitionBatchRemaining() > 0) {
      return 0;
    }

    this._profileDefinitionBatchRemaining.set(edits.length);
    this._profileDefinitionBatchRefusals.set([]);

    // ⚠ THE BATCH IS ONE WRITE AS FAR AS THE STORE IS CONCERNED, and it is opened through the
    // same accounting every other write uses. The count rises once here and falls once when the last
    // row has settled, so the aggregate busy read stays true for the WHOLE batch rather than for each
    // row, and the outcome is published under one identifier a caller can compare against - so a
    // screen settles on ITS batch rather than on whichever write in the store answered last.
    const mutationId = this.beginWrite();

    // The first refusal, held until the batch settles so that the single recorded failure is the
    // one the operator has to act on rather than whichever row happened to answer last.
    let firstRefusal: unknown = null;
    let refused = false;
    let batchFailure: UserFailure | null = null;

    this.track(
      from(edits)
        .pipe(
          // ⚠ `concatMap`, NEVER `mergeMap`. This is the whole bound: the next request is neither
          // composed nor issued until the previous one has settled, because a concatenation
          // subscribes to one inner stream at a time and a transport call is cold until subscribed.
          // A batch of any size is therefore one request in flight, whatever its length.
          concatMap((edit: ProfileDefinitionEdit) =>
            this.transport.updateProfileDefinition(edit.propertyDefinitionId, edit.request).pipe(
              tap((written: ProfilePropertyDefinition) => {
                // The selected declaration is reconciled from the server's own answer, exactly as
                // the single-row command does, so a screen showing one row beside the grid cannot
                // drift from it.
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

                // ⚠ EVERY REFUSED ROW IS KEPT, NOT ONLY THE FIRST, AND IT IS KEPT WITH ITS ROW. The
                // rows are independent and the batch is not a transaction, so a five-row apply can
                // come back with three refusals and an operator told only that "something" was
                // refused cannot tell which declarations to correct. Described rather than recorded:
                // publishing each one into the store's single failure slot would leave only the last
                // standing and would clear whatever another screen was showing.
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
        // ⚠ SETTLED FROM `finalize`, NOT FROM `complete`. `finalize` also runs when the batch is
        // ABANDONED - by a session boundary or by teardown - which is the one path a completion
        // handler cannot see, and without it an abandoned batch would leave both the pending-write
        // count and the remaining-row count standing, so the store would report itself permanently
        // busy and refuse the next operator's first batch.
        .pipe(
          finalize(() => {
            // ⚠ THE REFUSAL LIST IS NOT CLEARED HERE, AND MUST NOT BE. It is what the screen reads to
            // report the batch, and this runs immediately before the settled result is published — so
            // emptying it here would leave every refusal unreported. It is emptied when the NEXT batch
            // is dispatched, and by teardown.
            this._profileDefinitionBatchRemaining.set(0);
            this.settleWrite(mutationId, 'applyProfileDefinitionEdits', batchFailure);
          }),
        )
        .subscribe({
          // Deliberately EMPTY. Nothing is committed per row: the catalogue is read once when the
          // batch completes, because where each declaration falls depends on its position and on
          // the server's ordering, neither of which this store may re-derive.
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
   * Removes one profile declaration.
   *
   * A declaration that cannot be removed — because values are recorded against it, or
   * because the tenant requires it — is refused with a status and a problem document,
   * which reaches the failure slot rather than leaving a silently unchanged list.
   *
   * @param propertyDefinitionId The declaration to remove. Passed on exactly as supplied.
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

  // -------------------------------------------------------------------------
  // COMMANDS — THE ACCOUNT'S OWN SUBSCRIPTIONS
  //
  // The four affordances of `Website/admin/Users/MemberServices.ascx`, which was
  // SELF-SERVICE throughout: every operation it performed passed `UserInfo.UserID` — the
  // signed-in account (`PortalModuleBase.vb:L319-L323`) — even though its container assigned
  // it a user identifier at `manageusers.ascx.vb:L517`, and the container hid the tab
  // outright whenever an administrator reached the screen (`:L61-L66`). The API gates all
  // five endpoints on account ownership for that reason, so the identifier a caller passes
  // here is its own.
  //
  // EVERY COMMAND RE-READS THE CATALOGUE ON SUCCESS, and that is the legacy behaviour rather
  // than caution: each command answers with no body, and the legacy handlers re-bound the
  // grid after acting (`MemberServices.ascx.vb:L118`, `:L133`, `:L430`). One row's state is
  // not the only thing a command can change — a redemption may join several roles at once —
  // so nothing is patched locally in place of the read.
  // -------------------------------------------------------------------------

  /**
   * Reads the services offered to one account.
   *
   * @param userId The account whose catalogue to read. Passed on exactly as supplied; zero
   * and minus one are real identifiers and are never treated as absence.
   */
  loadMemberServices(userId: number): void {
    this._failure.set(null);
    this.dispatchMemberServices(userId);
  }

  /**
   * Subscribes the account to one service, or renews a subscription that has lapsed.
   *
   * ONE command for both, because the legacy screen had one link for both: `ServiceText`
   * returned `Subscribe` or `Renew` from the same row state and the same handler ran. The
   * catalogue row says which word to render; this command is the same request either way.
   *
   * ⚠ A SERVICE THAT CHARGES A FEE IS REFUSED BY THE SERVER, NOT CHARGED. The legacy path
   * handed such a role to a payment page, which this migration excludes, so the refusal
   * reaches the failure slot with its own reason and the catalogue is left as it was.
   *
   * @param userId The account to subscribe.
   * @param roleId The service to subscribe to. Zero is a real service.
   */
  subscribeToService(userId: number, roleId: number): void {
    this.dispatchServiceCommand(
      'subscribeToService',
      userId,
      this.transport.subscribeToService(userId, roleId),
    );
  }

  /**
   * Cancels the account's subscription to one service.
   *
   * The server may EXPIRE the assignment rather than remove it — `RoleController.vb:L494-L496`
   * expires an assignment whose role charges a fee, so a paid history is not destroyed by a
   * cancellation — and either outcome is a success. Re-reading the catalogue is what shows
   * which one happened, because the row's own state is the answer.
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
   * Takes one service's trial period on the account's behalf.
   *
   * Separately gated from the subscription, as the legacy screen gated it: `ShowTrial`
   * (`MemberServices.ascx.vb:L325-L342`) offered a trial only for a public role that DOES
   * charge a service fee, charges nothing for its trial, and has not already been tried by
   * this account. A trial the catalogue offers is always performable.
   *
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
   * Redeems an invitation code, joining the account to every role recorded against it.
   *
   * The one command here that answers with a payload, and it is retained: the legacy screen
   * reported the outcome in words, and the successful half of that report is a list of roles
   * rather than a single fact. A code that matched nothing is a REFUSAL rather than an empty
   * success, so it lands in the failure slot; the previous outcome is discarded first, so a
   * refusal cannot be read alongside an earlier success.
   *
   * ⚠ THE CODE IS PASSED ON AS TYPED. The legacy comparison was ordinary string equality
   * against the stored code, so leading space and case both mattered. Nothing is trimmed,
   * folded or rejected here — including the empty string, which the server refuses with a
   * reason of its own. A form may of course decline to submit one.
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
            // ⚠ THE RE-READ IS DISPATCHED BEFORE THE REPORT IS RECORDED, AND THE ORDER IS
            // LOAD-BEARING. The read adopts this account and discards a report belonging to a
            // different one (see {@link dispatchMemberServices}); recording first would hand it
            // the report it has just been given and clear it on the very first redemption, when
            // no catalogue had yet been read and the account was therefore "changing".
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
    // Released with the policy itself: the flag describes the PREVIOUS tenant's read, and a session
    // boundary can change which tenant the next read addresses.
    this._membershipSettingsUnconfigured.set(false);
    this._profileDefinitions.set([]);
    this._selectedPropertyDefinitionId.set(undefined);
    this._selectedProfileDefinition.set(null);
    // ⚠ THE CATALOGUE AND ITS ACCOUNT ARE CLEARED TOGETHER. A catalogue is the personal
    // subscription state of ONE account, and every endpoint that produces it is gated on
    // ownership — so leaving either behind across a session boundary would show the previous
    // operator's subscriptions, or worse, show them under the new operator's account key.
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

  /**
   * Adopts a search and a page together WITHOUT reading anything.
   *
   * ⚠ THIS COMMAND DISPATCHES NOTHING, WHICH IS THE WHOLE POINT OF IT. Every other search command on this
   * store couples the change to a read, which is right when the change originates in an affordance the
   * operator just used. It is wrong when the change originates in the ADDRESS. The listing screen keeps its
   * search, its axis and its page in the address so that a reload, a bookmark and the browser's back and
   * forward buttons all reproduce what was on screen, and on entry it therefore restores BOTH coordinates at
   * once — through one command, because {@link applySearch} RETURNS TO THE FIRST PAGE and would discard the
   * page the address had just asked for.
   *
   * Separating the change from the read is also what lets an address-borne search take part in the opening
   * sequence rather than racing it. {@link initialise} reads the tenant's policy and then decides the opening
   * view, and it deliberately yields to a search that was already chosen — so a screen that stages the
   * address's search BEFORE calling it gets exactly one listing read, at the address's own coordinates, with
   * the policy's opening view correctly overridden. Staging afterwards, or dispatching here, would cost two.
   *
   * A staged mode of `none` is a real instruction and not a no-op: it means the address asked for nothing, so
   * the policy is left to choose the opening view exactly as it does on a first ever visit.
   *
   * MIGRATION: no legacy counterpart. The legacy screen posted back for every query
   * (`Users.ascx.vb` L264-L275), so a state change and a read were inseparable by construction.
   *
   * THE ORDERING IS STAGED HERE TOO, and for the same reason the page is. It is a further coordinate the
   * address states, so restoring it through {@link UserStore.setSortField} and
   * {@link UserStore.setSortDirection} would take two commands to express one view - and each of those is
   * a state change a caller is expected to follow with a read, which is precisely the coupling this
   * command exists to avoid. Both members are stored EXACTLY as supplied: the caller that read them out
   * of the address is the party that validated the field against what the endpoint accepts, and this
   * store is not the place to second-guess that.
   *
   * @param search The search to adopt.
   * @param pageIndex The page to adopt, counted from zero. Stored exactly as supplied; nothing here clamps
   * it against a total this store may not yet know.
   * @param sortBy The endpoint field to order by, or undefined to accept the endpoint's own default.
   * @param sortDir The direction, or undefined. Meaningful only alongside `sortBy`.
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

    // ⚠ THE NOTHING-ASKED-FOR STATE HAS NO RESULT SET, AND SAYING SO IS PART OF ENTERING IT. This store is
    // provided at the application root and OUTLIVES the listing route, so a previous visit's rows are still
    // held here; and this mode deliberately issues NO request, so nothing would ever replace them. Runtime
    // testing measured the consequence exactly: the screen printed "No accounts have been requested yet"
    // directly above 255 retained rows from an earlier query, and pressing Back onto the bare address changed
    // the address without changing the view. Clearing the rows is what makes the two agree.
    //
    // Only this mode clears. Every other mode is about to be read, and emptying the grid first would replace
    // the operator's current rows with a blank frame for the duration of the request - the teardown flicker a
    // sibling finding was raised about.
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
        // Cleared on every success, so a tenant that gains an account module stops reporting absence
        // without needing the store to be reset.
        this._membershipSettingsUnconfigured.set(false);

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
      error: (cause: unknown) => {
        this._membershipSettingsLoading.set(false);

        // ⚠ ABSENCE IS SORTED FROM FAILURE HERE, and it is the only read in this store that needs to
        // be. Every other read asks for something a caller named, so "not there" IS a failure worth
        // reporting. This one asks for OPTIONAL tenant configuration whose absence has a defined
        // meaning - fall back to the legacy defaults - which is why the server reports it as a
        // SUCCESS carrying no value and only the transport's status line makes it look like an error.
        //
        // ⚠ THE DEFECT THIS REMOVES was a user-facing one on three working screens: recording a
        // failure here put "Not Found / The requested resource does not exist. / Reference:
        // <correlation guid>" and a "Try again" button on a fully loaded account listing, because that
        // screen surfaces this operation's failure alongside its own. Nothing was wrong, nothing could
        // be retried into existence, and the reference invited a support conversation about a
        // correctly functioning tenant.
        //
        // Described rather than recorded, so the status can be read without publishing anything: the
        // slot is set below only on the branch that genuinely warrants it. A non-absent failure still
        // takes the ordinary path and still reaches every surface that watches for it.
        const described: UserFailure = this.describeFailure('loadMembershipSettings', cause);
        const absent: boolean = described.problem?.status === MEMBERSHIP_SETTINGS_ABSENT_STATUS;

        this._membershipSettingsUnconfigured.set(absent);

        if (!absent) {
          this._failure.set(described);
        }

        if (thenReadListing) {
          this.readListingAfterSettings();
        }
      },
    });
  }

  /**
   * Reads the listing once the policy has been resolved, opening it in the presentation the
   * TENANT'S POLICY selects rather than in one fixed view.
   *
   * MIGRATION: this reproduces `Website/admin/Users/Users.ascx.vb` L494-L506, which is where the
   * legacy screen chose its opening filter. It read `Display_Mode` and set `Filter` from it, and
   * the value of `Filter` then decided which branch of `BindData` (L248-L290) ran:
   *
   *  * `DisplayMode.All` set `Filter` to the localised word "All", which took the L264 branch and
   *    listed every account, paged and unfiltered.
   *  * `DisplayMode.FirstLetter` set `Filter` to the FIRST CHARACTER of the alphabet strip -
   *    `Localization.GetString("Filter.Text").Substring(0, 1)`, and the resource value is
   *    `"A,B,C,…"`, so the character is `A`. That fell through to the search-axis switch at L267
   *    with `ddlSearchType.SelectedItem.Value`, whose first-added item was `"Username"` (L577), so
   *    the screen opened on accounts whose name began with A.
   *  * `DisplayMode.None` set `Filter` to the bare marker `"None"`, which every branch of
   *    `BindData` excluded - so NO QUERY WAS ISSUED and the grid stayed unbound until the operator
   *    pressed a letter or searched. `UserModuleBase.vb` L126-L130 defaulted the setting to this
   *    mode, which is why a tenant that has configured nothing opens with no rows.
   *
   * ⚠ AN EARLIER REVISION PROMOTED THE NO-QUERY STATE TO THE UNFILTERED LISTING UNCONDITIONALLY,
   * which discarded the policy entirely: the mode a tenant had chosen made no difference to what
   * the screen did. That is the behaviour being corrected here. The no-query state is not a defect
   * to be worked around - it is the deliberate choice a large tenant makes so that opening the
   * screen does not page through a hundred thousand accounts, and the alphabet strip and the
   * unfiltered affordance are both on the screen for the operator to act on.
   *
   * A search already chosen is left exactly as it is: the policy decides how the screen OPENS, not
   * what it shows after an operator has asked for something.
   *
   * @remarks
   * An unreadable policy opens on the unfiltered listing rather than on the legacy default. The
   * two situations differ: the legacy default applied when a KEY WAS ABSENT from a policy it could
   * still read, whereas here the whole policy could not be read, and the page size falls back for
   * exactly the same reason - a tenant whose policy is unavailable still has accounts. The failure
   * remains recorded, so nothing is concealed.
   */
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
   * @returns The opening search, or `null` to leave the store in its no-query state - which is
   * what the policy's third mode asks for and what its own default is.
   */
  private openingSearchForPolicy(): UserSearch | null {
    const policy: MembershipSettings | null = this._membershipSettings();

    if (policy === null) {
      // The whole policy is unavailable. See the remark on the caller.
      return { mode: 'all' };
    }

    // ⚠ COMPARED AGAINST EACH MODE EXPLICITLY, NEVER TESTED FOR TRUTHINESS. Nought is the "list
    // everything" mode, so a falsy test would send the most permissive setting down the same path
    // as an unrecognised one.
    switch (policy.displayMode) {
      case DISPLAY_MODE_ALL:
        return { mode: 'all' };

      case DISPLAY_MODE_FIRST_LETTER:
        return { mode: 'username', text: OPENING_LETTER };

      case DISPLAY_MODE_NONE:
        return null;

      default:
        // MIGRATION: the legacy `Select Case` had no `Case Else`, so an unrecognised mode left
        // `Filter` as the empty string - which was not the "All" word, was not "None", and
        // therefore fell through to the search-axis switch and queried `GetUsersByUserName(…, "%")`.
        // An empty prefix plus the server's own trailing wildcard matches every account, so the
        // legacy outcome was the unfiltered listing, and that is what is reproduced.
        //
        // ⚠ REPRODUCED AS THE UNFILTERED LISTING RATHER THAN AS AN EMPTY-PREFIX NAME SEARCH. The
        // target endpoint refuses a filter that was supplied but blank - "omit it to search
        // without it" - so sending the legacy's literal empty prefix would be a refused request
        // where the legacy served a page. The RESULT SET is identical; only the way of asking for
        // it differs.
        return { mode: 'all' };
    }
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
   * Reads the member-services catalogue of one account, replacing whatever was held.
   *
   * The account is recorded ALONGSIDE the rows, and on the request rather than on the
   * response, so that a screen can tell whose catalogue it is rendering even while the read
   * is in flight. A previous read is abandoned first: two catalogues for two accounts must
   * never be able to settle in either order.
   *
   * ⚠ THE ROWS ARE CLEARED WHEN THE ACCOUNT CHANGES, AND ONLY THEN. Clearing on every read
   * would blank the grid on a refresh that is about to answer with almost the same rows;
   * NOT clearing on an account change would show one account's subscriptions under another
   * account's key for as long as the request takes.
   *
   * A failed read leaves the rows in hand rather than emptying them, for the reason the
   * failure slot exists: an empty grid beside a message reads as "you are offered nothing",
   * which is a different and false statement.
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
   * Runs one payload-free subscription command and re-reads the catalogue on success.
   *
   * The three commands differ only in which request they issue and which operation name a
   * failure is recorded under, so they share one body: a divergence between them would be a
   * divergence in how a refusal is reported, which is precisely what a caller relies on to
   * explain one.
   *
   * The previous redemption report is discarded, because a subscription changed after a code
   * was redeemed makes that report no longer a description of the state on screen.
   *
   * @param operation The command name a failure is recorded under.
   * @param userId The account the command acts on, and whose catalogue is re-read.
   * @param request The transport call to run. Subscribed exactly once, here.
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
  /**
   * Opens a write and returns the identifier the caller settles it by.
   *
   * Pre-increments, so the first identifier ever issued is 1. That is what lets a caller hold zero as
   * "no write of mine is outstanding" without the value colliding with a real write — a collision that
   * would make the very first write on a fresh store settle something that was never dispatched.
   *
   * @returns The identifier issued to this write.
   */
  private beginWrite(): number {
    // ⚠ THE SHARED FAILURE SLOT IS EMPTIED ONLY WHEN NOTHING ELSE IS OUTSTANDING, and that is the
    // second half of the race the count fixes. There is ONE slot, and a write that emptied it as it
    // started erased a refusal an earlier, still-open write had already recorded - discarded by a
    // sibling request rather than by anything the operator did, leaving the refused row looking as
    // though it had been written. Clearing only when the store is idle preserves the behaviour a
    // single write has always had (a fresh attempt starts from a clean slot) while letting a batch's
    // refusals survive the batch. A caller settling its OWN write still reads the failure from the
    // published result rather than from here; see {@link UserStore.mutation}.
    if (this._pendingWrites() === 0) {
      this._failure.set(null);
    }

    // ⚠ AND NO WRITE COMMAND MAY EMPTY THE SLOT ITSELF. Fourteen of them used to, on the line after
    // the one that calls this — which made the guard above dead code and left the defect it exists to
    // close fully open. The clearing belongs HERE, once, because only this method knows whether
    // anything else is outstanding; a command clearing on its own behalf cannot know.

    this.nextMutationId += 1;
    this._pendingWrites.update((open) => open + 1);

    return this.nextMutationId;
  }

  /**
   * Settles one write: lowers the pending count and publishes the outcome under its identifier.
   *
   * ⚠ CALLED FROM `finalize` RATHER THAN FROM THE TWO CALLBACKS. `finalize` runs on completion, on
   * error AND on unsubscription, which is the only one of the three that a pair of callbacks cannot
   * see: a write released by a session boundary or by teardown would otherwise leave the count raised
   * for the life of the application, and the store would report itself permanently busy.
   *
   * ⚠ THE COUNT IS FLOORED AT ZERO. `finalize` runs exactly once per subscription, so it cannot
   * legitimately go negative — but a negative count would make the aggregate read false while a write
   * was still open, which is the one failure mode this whole mechanism exists to remove, so it is made
   * unrepresentable rather than merely unlikely.
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
   * Describes a refusal WITHOUT publishing it anywhere.
   *
   * ⚠ EXTRACTED SO THAT A PER-ROW REFUSAL CAN BE DESCRIBED WITHOUT TOUCHING THE SHARED SLOT. There is
   * one failure slot for the whole store, so a batch that published each refused row into it would
   * leave only the last one standing and would clear whatever another screen was showing. A batch
   * describes each refused row with this, keeps the descriptions in its own list, and publishes just
   * one of them — the first — as the batch's settled outcome.
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

  private recordFailure(operation: UserOperation, cause: unknown): UserFailure {
    const failure: UserFailure = this.describeFailure(operation, cause);

    this._failure.set(failure);

    // ⚠ RETURNED AS WELL AS PUBLISHED, AND THE RETURN IS WHAT A WRITE MUST USE. The slot below is one
    // slot for the whole store and every dispatch clears it, so by the time a write settles it may
    // hold another operation's refusal or nothing at all. A write captures the value returned here and
    // publishes it on its own settled result; the slot remains for the surfaces that legitimately want
    // "the most recent failure, whatever it was".
    return failure;
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
    this._memberServicesLoading.set(false);
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
