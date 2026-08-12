//
// Account, profile, account-policy and profile-declaration transport for the
// dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE IS
// ---------------------------------------------------------------------------
// A typed transport wrapper over two controllers: the account resource and the
// profile-declaration resource. Nine legacy administration screens collapsed onto
// the first of those two - the account listing, the two account editors, the
// profile editor and viewer, the credential screen, the account-policy screen, the
// per-account settings screen and the member-services screen - and two more
// collapsed onto the second. Every one of them is reachable through the methods
// below and through nothing else.
//
// ---------------------------------------------------------------------------
// THE GOVERNING RULE: SERVICES ARE RESTRICTED TO API COMMUNICATION
// ---------------------------------------------------------------------------
// One method addresses one endpoint with one call and hands back the observable it
// produced. That is the whole responsibility, and the exclusions are as much a part
// of the contract as the inclusions:
//
//   NO VALIDATION. Not a credential length or strength check, not an address-format
//   check, not a name-uniqueness check, not a confirmation-match check, not an
//   account-quota check. Every one of those rules is enforced by the server, which
//   answers a breach with a status and an RFC 7807 problem document. A second copy
//   here would be free to drift from the copy that is actually enforced, and the
//   drift would show up as a request the client refuses to send even though the
//   server would have accepted it - a failure with no error message anywhere.
//
//   NO DERIVATION AND NO ORCHESTRATION. No method issues two requests. Creating an
//   account and then writing its profile is two operations with two outcomes, and
//   sequencing them here would hide a partial failure behind a single observable
//   that reports only the second one.
//
//   NO STATE, NO CACHE, NO RETRY. The feature store holds screen state and decides
//   when to re-read; caching is a server concern behind its own abstraction; a retry
//   policy belongs to whatever can tell a safe operation from an unsafe one.
//
//   NO SUBSCRIPTION. Every method returns cold. Nothing here starts a request; the
//   caller does, by subscribing. A method that subscribed would fire on being
//   called, would leak, and would swallow the failure its caller needed to render.
//
//   NO HEADERS. The correlation identifier, the bearer token and the translation of
//   a failure into a problem document are applied by the three HTTP interceptors
//   registered once at application configuration, in that fixed order: the
//   correlation identifier first so the server's middleware can consume it and echo
//   it back as a trace identifier, the token second, and the failure translation
//   last so it observes the final response after any token refresh has been retried.
//   A header set here would either duplicate or defeat one of them.
//
//   NO LOGGING. Not of a request, not of a response, not of a failure. The bodies
//   that pass through this file include credentials and token-adjacent material, and
//   a diagnostic line is the easiest way for one of those to reach a place it was
//   never meant to be persisted.
//
// ---------------------------------------------------------------------------
// NO PROJECT RULES DOCUMENT EXISTS
// ---------------------------------------------------------------------------
// The engagement supplied no rules document: the rules review answers with a single
// line stating that none were provided, and it answers identically for ranges that
// begin past the first and second lines, which is what proves the answer is the
// whole document rather than its first line. Nothing in this file is therefore
// justified by a project rule, and nothing is relaxed by their absence either - the
// enterprise baseline the action plan sets out governs instead, and every constraint
// documented above is held to as if it had been written down as a rule.
//
// ---------------------------------------------------------------------------
// WHERE THE URLS AND THE QUERY STRINGS COME FROM
// ---------------------------------------------------------------------------
// Neither is built here. `core/config/api-endpoints.ts` is the single declaration
// point for every route template and joins each one to the configured API base, so a
// template is passed to the client exactly as returned and never prefixed a second
// time - prefixing twice yields a doubled version segment, which is a 404 that no
// compiler and no test that stubs the client can catch. `core/utils/http-params.util.ts`
// is the single owner of query-string serialisation, so no separator, no template
// literal and no browser search-parameter type appears in this file.
//
// This file does not read the environment module. The dependency chain that carries
// the configured base into the bundle is discharged transitively, through the
// endpoint module that does read it.
//

import { HttpClient, type HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, map } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import {
  decodeProfilePropertyDefinition,
  decodeUserProfile,
} from '../models/profile.model';
import {
  decodeMemberService,
  decodeMembershipSettings,
  decodeMembershipSettingsUpdateResult,
  decodeRedeemServiceCodeResult,
  decodeUserChoice,
  decodeUserDetail,
  decodeUserListItem,
} from '../models/user.model';
import { arrayOf, decodeResponse, envelopeOf, pageOf } from '../utils/decode.util';
import { presentedInContext } from './notification.service';

import type { Decoder } from '../utils/decode.util';
import type { PagedRequestParams } from '../utils/http-params.util';
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
  PagedUserChoiceList,
  PagedUserList,
  RedeemServiceCodeRequest,
  RedeemServiceCodeResult,
  UpdateUserRequest,
  UserDetail,
  UserListQuery,
} from '../models/user.model';
import {
  identifiesAPerson,
  pagedRequestParams,
  userApprovalParams,
  userListParams,
  userSearchBody,
} from '../utils/http-params.util';

/**
 * One decoder per response shape this transport reads, composed once at module scope.
 *
 * ⚠ NONE IS `nullable`, AND THAT MIRRORS THE CONTRACT RATHER THAN BEING OPTIMISTIC. Each of
 * these endpoints answers a successful read with a populated payload, and expresses "the target
 * does not resolve" as a `404` problem document — never as a `200` carrying nothing.
 *
 * MIGRATION: THE ACCOUNT READ, THE PROFILE READ, THE ACCOUNT-POLICY READ AND THE SINGLE
 *   DECLARATION READ USED TO ADMIT A SUCCESSFUL `null`, which is a response this API cannot
 *   send. Every value-bearing outcome is translated by one helper on the server, and that helper
 *   answers `NotFoundProblem()` — a full RFC 7807 document carrying `type`, `title`, `detail` and
 *   `traceId` — as soon as the outcome's value is null, keeping `200` plus the shared envelope
 *   for a value that exists. The nullable declaration therefore described a shape no deployment
 *   produces, and it cost accuracy twice over: an account or a profile that had gone was
 *   presented as a successfully blank record instead of an announced absence, and the real 404
 *   path was left looking exceptional. An undeclared `null` is now refused at the boundary, so a
 *   drifted body becomes one located failure rather than an apparently successful nothing.
 */
const USER_PAGE: Decoder<PagedUserList> = pageOf(decodeUserListItem);
const USER_CHOICE_PAGE: Decoder<PagedUserChoiceList> = pageOf(decodeUserChoice);
const USER_DETAIL_RESPONSE: Decoder<UserDetail> = envelopeOf(decodeUserDetail);
const USER_PROFILE_RESPONSE: Decoder<UserProfile> = envelopeOf(decodeUserProfile);
const MEMBERSHIP_SETTINGS_RESPONSE: Decoder<MembershipSettings> = envelopeOf(
  decodeMembershipSettings,
);
const MEMBERSHIP_SETTINGS_UPDATE_RESPONSE: Decoder<MembershipSettingsUpdateResult> =
  envelopeOf(decodeMembershipSettingsUpdateResult);
const PROFILE_DEFINITION_RESPONSE: Decoder<ProfilePropertyDefinition> = envelopeOf(
  decodeProfilePropertyDefinition,
);
const PROFILE_DEFINITION_LIST_RESPONSE: Decoder<readonly ProfilePropertyDefinition[]> =
  envelopeOf(arrayOf(decodeProfilePropertyDefinition));
const MEMBER_SERVICE_LIST_RESPONSE: Decoder<readonly MemberService[]> = envelopeOf(
  arrayOf(decodeMemberService),
);
const REDEEM_SERVICE_CODE_RESPONSE: Decoder<RedeemServiceCodeResult> = envelopeOf(
  decodeRedeemServiceCodeResult,
);

/**
 * Transport for accounts, profiles, the tenant's account policy and the profile
 * declarations a profile is composed of.
 *
 * Deliberately flat and deliberately dull: twenty-four methods, each one endpoint, no
 * branch in any of them that is not the endpoint's own shape. Everything that could
 * be described as a decision - which accounts to ask for, whether an identifier is
 * known, what to do with a failure - belongs to the caller, because the caller has
 * the context to decide it and this file does not.
 *
 * MIGRATION: every one of these operations used to be a postback handler on an
 * administration control, which meant the rule, the query, the presentation and the
 * page lifecycle were interleaved in one file per screen. Splitting transport out is
 * what allows the same account query to serve a list screen, a picker and a report
 * without any of them re-deriving the request.
 *
 * The tenant is NOT a parameter on any method. The API resolves one portal per
 * request - from the host the caller reached it on, reconciled against the alias
 * table - before it dispatches to a controller, so a portal identifier in a path or
 * a query string here would either be redundant or be a second, disagreeing opinion
 * about which tenant the caller meant.
 *
 * MIGRATION: ADMINISTERED ROLE MEMBERSHIP IS NOT EXPOSED FROM THIS SIDE. Assigning a
 * role to an account, removing one and time-bounding one all live on the role resource,
 * as a sub-collection of one role, and belong to the role service. Each such assignment
 * is one row joining an account to a role with an effective and an expiry date, so a
 * parallel administrative memberships route here would have been a second name for one
 * table with two places to keep the rules consistent.
 *
 * SELF-SERVICE IS A DIFFERENT MATTER, AND IT DOES LIVE HERE. An earlier revision of this
 * note concluded from the paragraph above that "there is accordingly no membership,
 * service or subscription method on this service, and none should be added"; that
 * conclusion is WITHDRAWN, because it reasoned from the storage shape and the legacy
 * screen's authority is its CALLER. `Website/admin/Users/MemberServices.ascx.vb` acted
 * only ever on `UserInfo.UserID` - the signed-in account (`PortalModuleBase.vb:L319-L323`)
 * - and its container hid it from an administrator outright (`manageusers.ascx.vb:L61-L66`),
 * so its four affordances (catalogue, subscribe or cancel, trial, invitation code) are
 * operations an account performs on ITSELF. The API gates all five endpoints on account
 * ownership, which the role resource's endpoints cannot express: they are gated on tenant
 * administration, so routing self-service through them would have required admitting every
 * account holder to an administrative surface. The five methods at the foot of this class
 * are those operations, and they write assignments through the same server-side primitives
 * the role resource writes them through - one implementation of the rules, two callers.
 *
 * MIGRATION: NOTHING IS CACHED HERE. The legacy domain layer reached its cache
 * directly from inside the business logic, 116 times across the code this migration
 * covers, through the static helper at
 * `Library/Components/Providers/Caching/DataCache.vb` - and the caching of an account
 * or a policy was therefore a fact about the caller rather than about the data. (The
 * action plan cites that helper under a shared path; the file actually lives under the
 * caching provider directory named here.) Caching is now a server concern, held behind
 * its own abstraction with named keys and explicit invalidation, so this file issues a
 * request every time it is asked to and holds nothing between calls. A client-side
 * cache added here would be a third opinion about freshness, invisible to the two that
 * already exist.
 *
 * MIGRATION: THE NINE SOURCE SCREENS COMPILED WITH STRICT TYPE CHECKING SWITCHED OFF.
 * `Website/release.config` L125 configures the web compilation with strict mode
 * disabled, so every one of those administration code-behinds could legally perform
 * late binding and implicit narrowing - a value read from an untyped hash table and
 * used as a number, a null collapsing into a zero or an empty string - and none of it
 * announced itself at compile time. Nothing was carried across mechanically for that
 * reason: the screens were read for their rules and their wording, and every coercion
 * they left implicit is explicit in the target. Strict TypeScript is what makes that
 * hold on this side, which is why no assertion, no escape comment and no permissive
 * type appears anywhere in this file - each one would reopen exactly the hole the
 * legacy configuration left open.
 */
@Injectable({ providedIn: 'root' })
export class UserService {
  /**
   * The transport.
   *
   * Injected as a field rather than through a constructor parameter, matching the
   * rest of this folder. The client itself is provided once at application
   * configuration together with its interceptor chain, so no module import and no
   * multi-provider token is involved anywhere.
   */
  private readonly http = inject(HttpClient);

  // -------------------------------------------------------------------------
  // Accounts
  // -------------------------------------------------------------------------

  /**
   * Reads one page of the resolved tenant's accounts.
   *
   * The whole query - the page coordinates, the ordering and the search - is one
   * argument, and it is handed to the query-string owner unchanged. There is no
   * branch here that inspects it: which search axes may be combined is the server's
   * rule, and it answers a combination it refuses with a field-level failure naming
   * the offending parameter, which is strictly more useful than this file quietly
   * declining to send it.
   *
   * The same object is passed twice because it satisfies both halves of the
   * serialiser's signature - the paging contract and the account filter - and
   * splitting it into two locals here would only give a reader two names for one
   * thing.
   *
   * MIGRATION: the page index travels ZERO-BASED and no arithmetic is applied to it
   * anywhere in this file. The legacy listing kept a one-based counter for its pager
   * - `Website/admin/Users/Users.ascx.vb` L51 declares
   * `Private _CurrentPage As Integer = 1` - and subtracted one at every provider
   * call: L265, L269, L271 and L274 each pass CurrentPage - 1. The portal listing did
   * the same at `Website/admin/Portal/Portals.ascx.vb` L142. The wire now carries the
   * zero-based value directly, so translating between the two - if a pager needs to -
   * is the presentation layer's business and happens exactly once, there.
   *
   * MIGRATION: the three search axes match on a PREFIX. The legacy screen appended
   * one trailing wildcard character to whatever had been typed before handing it to
   * the provider (`Users.ascx.vb` L269, L271 and L274), so every search was a
   * starts-with. The server reproduces that, wildcard included, which is why the
   * caller passes bare text: appending a wildcard here would produce a doubled
   * pattern, and leading with one would silently widen a starts-with into a
   * substring match. Nothing in this file appends a wildcard, and the query-string
   * owner and the search control are held to the same prohibition.
   *
   * MIGRATION: the third axis takes an ARBITRARY profile-property name.
   * `Users.ascx.vb` L272-L274 fell through to
   * `GetUsersByProfileProperty(UsersPortalId, False, SearchField, ...)` and L275
   * carried the chosen property's name in the screen's own URL as a second pair. The
   * set of property names is tenant data, declared through the profile-declaration
   * methods further down this file, so it is not a closed vocabulary. Whatever string
   * arrives is transmitted verbatim - never matched against a list, never trimmed and
   * never case-folded, because a declared property name is free to differ from
   * another only in case.
   *
   * MIGRATION: the legacy sentinel that meant "no search" is not transmitted.
   * `Users.ascx.vb` L266 guarded the whole search block by comparing the search text
   * against a magic string spelled None, so that value could never be searched for
   * even though it is a perfectly ordinary thing to type. Absence is expressed here
   * by omitting the parameter, which the query-string owner decides by an explicit
   * nullish test rather than a truthiness test - and that distinction matters,
   * because empty text, zero and false are all legitimate values on this contract.
   *
   * MIGRATION: two unpaged legacy list modes are dropped rather than reproduced.
   * `Users.ascx.vb` L258-L260 answered one of them with
   * `UserController.GetUnAuthorizedUsers(UsersPortalId, False)` and hid the pager,
   * and L261-L263 answered the other - the signed-in-accounts view - from session
   * tracking and hid the pager too. Neither took page coordinates at all, so both
   * returned an unbounded set. The approval axis on this listing is a paged filter
   * over the account table and is NOT a restoration of either: the signed-in view
   * depended on session tracking and a scheduled purge that this migration does not
   * carry forward, and no method in this file exposes it.
   *
   * MIGRATION: no bulk operation exists, here or anywhere. `Users.ascx.vb` L326
   * declared `Private Sub DeleteUnAuthorizedUsers()`, whose single provider call at
   * L328 destroyed an unbounded number of accounts from one click, with no per-row
   * confirmation and no way to review the set first. (The declaration at L326 and the
   * provider member it calls at L328 spell the same word with different capitals,
   * which is a fair measure of how little the pre-strict compiler was checking.)
   * Removal is per-account here, so the caller names what it is removing.
   *
   * MIGRATION: the size of the page is a per-tenant setting, not a constant, and is
   * not defaulted here. `Users.ascx.vb` L114-L119 read it from the "User Accounts"
   * module setting `Records_PerPage`, whose fallback of ten lives at
   * `Library/Components/Users/UserModuleBase.vb` L134-L136. That setting is now part
   * of the account policy this file reads and writes, and the one shared default
   * belongs to the paging model alone - restating it here would create a second copy
   * free to disagree with it.
   *
   * MIGRATION: the total no longer arrives through an argument passed by reference.
   * Each legacy provider call above ended in `ByRef TotalRecords`, so the count
   * reached the screen as a side effect on one of its own fields. It now travels
   * inside the returned envelope alongside the rows.
   *
   * @param query The page to return, its size, the ordering and the search. Passed to
   * the query-string owner exactly as supplied.
   * @returns The page: the account rows plus the envelope carrying the total, the
   * zero-based index and the size the server applied. The listing answers with that
   * envelope directly rather than nesting it inside the single-payload wrapper, so
   * nothing is unwrapped and no paging fact is discarded.
   */
  list(query: UserListQuery): Observable<PagedUserList> {
    // ⚠ THE TRANSPORT IS CHOSEN BY WHETHER THE QUERY NAMES A PERSON, AND THIS BRANCH MUST NOT BE
    // COLLAPSED TO ONE CALL. A user name, an email address and an arbitrary profile-property name
    // paired with the value to match all identify somebody. Sent as query parameters they end up in
    // the REQUEST TARGET, which is the most widely recorded part of an HTTP exchange: the browser's
    // own history, every forward and reverse proxy's access log, the server's access log, and any
    // telemetry that samples URLs. Each of those recorders sits at an END of the encrypted channel
    // rather than in the middle of it, so HTTPS does not address the exposure — this is CWE-598,
    // "Use of GET Request Method With Sensitive Query Strings". A request body is written to none of
    // them by default, so an identifying search goes in one.
    //
    // The profile pair is the sharpest case, because a tenant defines whatever properties it likes:
    // the value being matched is arbitrary personal data whose meaning neither side knows, and it
    // may perfectly well be a national identifier or a telephone number.
    //
    // ⚠ THE PAGING CONTRACT'S GENERIC `query` COUNTS AS IDENTIFYING TOO, AND OMITTING IT LEFT THE
    // COMPENSATOR WITH A HOLE. The server matches that member as a SUBSTRING across the login name,
    // the display name AND the electronic-mail address (`UserRepository.cs` L131-L134), so a search
    // by any one of a person's three identifiers reaches the same rows the named filters reach —
    // through a member that used to stay on the `GET` however it was filled in. Moving the four
    // NAMED filters into a body while leaving that one in the request target protected the
    // identifiers an operator selects a mode for and not the identifier they simply type, which is
    // the more likely of the two. The predicate below now classifies it, and the rule it applies to
    // it differs from the rule it applies to the four for a reason stated there.
    //
    // A listing that names nobody — a page index, a page size, an ordering, at most an approval
    // state, and a blank or absent free-text filter — stays on the `GET`, which keeps it cacheable
    // and idempotent. Both addresses reach the same server capability under the same authorisation
    // policy and the same paging bounds, and both answer the same envelope, so the DECODER below is
    // shared and the caller cannot tell which was used.
    if (identifiesAPerson(query)) {
      return this.http
        .post<unknown>(API_ENDPOINTS.users.search(), userSearchBody(query, query), {
          context: presentedInContext(),
        })
        .pipe(map((body) => decodeResponse(USER_PAGE, body)));
    }

    const params: HttpParams = userListParams(query, query);

    return this.http
      .get<unknown>(API_ENDPOINTS.users.collection(), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_PAGE, body)));
  }

  /**
   * Reads one page of the tenant's accounts as an account PICKER needs them.
   *
   * ⚠ NOT A THINNER MODE OF {@link UserService.list} — A DIFFERENT ENDPOINT, AND THE
   * DISTINCTION IS THE WHOLE POINT. A performance and privacy review measured the
   * role-assignment screen building its account drop-down, and its account-count probe,
   * from the account LISTING. Every candidate row carried a postal address, a telephone
   * number, an electronic-mail address, a creation instant, a last-login instant and four
   * status flags across the wire so that three values could be rendered — and that screen
   * may enumerate a tenant of up to a thousand accounts before it decides to offer a name
   * box instead. `GET /api/v1/users/choices` answers a key and two captions and nothing
   * else, so no field arrives that the screen cannot use.
   *
   * It is also the CHEAPEST COUNT AVAILABLE. Asking for a single row and reading
   * `meta.totalCount` answers "how many accounts does this tenant hold" while disclosing
   * one key and two captions; the same probe against the listing disclosed a complete
   * account row to read a number.
   *
   * ⚠ THE ANSWER IS NOT CACHED HERE AND MUST NOT BE HOISTED INTO A ROOT STORE. A roster
   * held across a session would mean an account created moments earlier on the account
   * screens was ABSENT from this drop-down until the whole application was reloaded, with
   * nothing on screen to suggest why — an operator would conclude the account had not been
   * created. Retention is the caller's decision and belongs at the caller's own scope.
   *
   * MIGRATION: this is `cboUsers` on `Website/admin/Security/securityroles.ascx`, filled by
   * `UserModuleBase.vb:L178-L186`. That code read the tenant's account count first and
   * offered the drop-down only at or below one thousand accounts, so both halves of the
   * legacy behaviour — the count and the enumeration — are served from this one address.
   *
   * @param query The page to return, its size, the ordering and the caption prefix to match.
   * The prefix matches the START of either caption; the server composes the pattern, so no
   * wildcard is appended here and any wildcard the operator typed matches itself.
   * @returns The page: the choices plus the envelope carrying the total, the zero-based
   * index and the size the server applied.
   */
  listChoices(query: PagedRequestParams): Observable<PagedUserChoiceList> {
    // A plain `GET`, unlike the listing's identifying-search branch. The only filter this
    // address takes is a prefix of a caption the drop-down already displays, and every value
    // it can match is a value the response returns — so a request target recording it
    // discloses nothing the response body did not already carry. The listing's four named
    // filters and its free-text member reach an electronic-mail address and arbitrary
    // tenant-defined profile values, which is why those move into a body (CWE-598) and this
    // does not.
    return this.http
      .get<unknown>(API_ENDPOINTS.users.choices(), {
        params: pagedRequestParams(query),
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_CHOICE_PAGE, body)));
  }

  /**
   * Reads one account.
   *
   * An identifier naming no account in the resolved tenant is refused with a not-found
   * problem document, so a successful answer always carries an account. The absent case
   * is an error path and is deliberately not modelled as a successful empty answer — see
   * the note on the decoders at the head of this file for why the server cannot send one.
   *
   * MIGRATION: the identifier is interpolated exactly as supplied and is never
   * inspected first. No request in this file is guarded on an identifier being
   * truthy, positive or non-negative, and the reason is that the legacy schema makes
   * every such guard wrong somewhere. `Users.UserID` is `IDENTITY (1, 1)`
   * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L98),
   * so an account identifier does start at one - but `Portals.PortalID` is
   * `IDENTITY (-1, 1)` at L77 of the same script, so both zero and minus one name real
   * portals, while `Library/Components/Shared/Null.vb` L41-L45 simultaneously defines
   * minus one as the marker for a missing integer. Role, page and module-placement
   * identifiers seed at zero for the same reason. One vocabulary cannot carry both
   * meanings, so this file declines to interpret them at all and leaves the question
   * of whether an identifier is known to the caller, which has the context to answer
   * it.
   *
   * @param userId The account to read. Interpolated unchanged.
   * @returns The account. An identifier matching none arrives as a `404` failure.
   */
  getById(userId: number): Observable<UserDetail> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.byId(userId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(USER_DETAIL_RESPONSE, body)));
  }

  /**
   * Creates an account in the resolved tenant.
   *
   * MIGRATION: the account-quota rule is the server's, and is not pre-checked. A
   * tenant that has reached its allowance is refused with a forbidden status naming
   * the rule; counting the accounts here first would cost an extra request, would
   * race against every other administrator, and would still have to handle the
   * refusal it was trying to predict.
   *
   * MIGRATION: nothing about the credential is validated here - see the note on
   * {@link changePassword} for the policy this migration deliberately preserves
   * rather than tightens, and for where it is enforced.
   *
   * @param request The account to create.
   * @returns The created account as the server recorded it, including the identifier
   * it issued.
   */
  create(request: CreateUserRequest): Observable<UserDetail> {
    return this.http
      .post<unknown>(API_ENDPOINTS.users.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_DETAIL_RESPONSE, body)));
  }

  /**
   * Replaces one account's editable fields.
   *
   * The body is sent exactly as supplied, member for member. Nothing is removed from
   * it because it reads as empty: the server serialises without eliding a default, so
   * an empty string, a zero, a false and a null all survive a round trip in both
   * directions, and a client that stripped one on the way out would turn a
   * deliberate clearing of a field into a silent no-op. This applies with most force
   * to the account policy - see {@link updateMembershipSettings} - but it is the rule
   * for every body in this file.
   *
   * @param userId The account to replace. Interpolated unchanged.
   * @param request The fields to write.
   * @returns The account as the server recorded it.
   */
  update(userId: number, request: UpdateUserRequest): Observable<UserDetail> {
    return this.http
      .put<unknown>(API_ENDPOINTS.users.byId(userId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_DETAIL_RESPONSE, body)));
  }

  /**
   * Removes one account.
   *
   * Answers with no body, so the observable emits once and carries nothing. A refusal
   * - an account the tenant may not remove, or one whose removal would violate a
   * constraint - arrives as a status with a problem document and reaches the caller
   * as a failure.
   *
   * @param userId The account to remove. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  delete(userId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.users.byId(userId), {
      context: presentedInContext(),
    });
  }

  // -------------------------------------------------------------------------
  // One account's profile
  // -------------------------------------------------------------------------

  /**
   * Reads one account's profile: every property the tenant declares, whether or not
   * this account has recorded a value for it.
   *
   * The declarations arrive with the values, so a profile editor does not have to
   * read the declaration list separately in order to render a field it has no value
   * for. The visibility each entry reports is the resolved hint - the account's own
   * choice where it made one, and the tenant's default where it did not.
   *
   * @param userId The account whose profile to read. Interpolated unchanged.
   * @returns The profile. An identifier naming no account arrives as a `404` failure.
   */
  getProfile(userId: number): Observable<UserProfile> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.profile(userId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(USER_PROFILE_RESPONSE, body)));
  }

  /**
   * Replaces one account's profile.
   *
   * A REPLACE, not a merge: the submission carries every declared property, and a
   * property left out of it is a property cleared. That is the server's semantics and
   * the submission contract is shaped to match, so nothing here diffs the payload
   * against a previously read profile - a client that sent only what it believed had
   * changed would erase everything it omitted.
   *
   * Answers with no body.
   *
   * @param userId The account whose profile to replace. Interpolated unchanged.
   * @param submission Every declared property's value and visibility.
   * @returns Completion. No payload.
   */
  updateProfile(userId: number, submission: UserProfileSubmission): Observable<void> {
    return this.http.put<void>(API_ENDPOINTS.users.profile(userId), submission, {
      context: presentedInContext(),
    });
  }

  // -------------------------------------------------------------------------
  // Credentials
  // -------------------------------------------------------------------------

  /**
   * Changes one account's credential on behalf of the account holder, who supplies
   * the credential in force alongside the replacement.
   *
   * Answers with no body. Nothing on the request is ever echoed back, and nothing
   * about it is logged.
   *
   * MIGRATION: THE POLICY IS PRESERVED VERBATIM AND DELIBERATELY NOT TIGHTENED, and
   * it is enforced server-side rather than here. As shipped, the legacy provider
   * required a seven-character credential, required none of it to be
   * non-alphanumeric, and did not require an address to be unique
   * (`Website/release.config` L242-L244, inside the provider registration at
   * L236-L247); it also enabled reset at L240 and required no question-and-answer
   * pair at L241. Raising any of those during a migration would lock out every
   * existing account that satisfies the old rule and not the new one, so the rule is
   * carried across unchanged. This file checks none of it - not a length, not a
   * composition, not the match between the replacement and its confirmation. The
   * confirmation mismatch in particular is a server answer, and a client-side
   * convenience check, if a form wants one, belongs to that form.
   *
   * MIGRATION: the legacy outcome vocabulary for this operation is a numeric
   * enumeration whose members carry NO explicit values, so declaration order is the
   * ordinal - `Library/Components/Users/Membership/PasswordUpdateStatus.vb` L24-L31
   * runs Success, PasswordMissing, PasswordNotDifferent, PasswordResetFailed,
   * PasswordInvalid, PasswordMismatch, InvalidPasswordAnswer, InvalidPasswordQuestion,
   * which is zero through seven in that sequence. Note that this differs from the
   * account-creation vocabulary, which numbers its members explicitly and whose
   * success member is thirteen rather than zero. Neither ordinal crosses this
   * boundary: an outcome arrives as a status and a problem document whose machine
   * -readable code is a STRING, and anything that renders a message keys on that
   * string. Keying on an ordinal would break the moment either enumeration is edited.
   *
   * @param userId The account whose credential to change. Interpolated unchanged.
   * @param request The credential in force and its replacement.
   * @returns Completion. No payload.
   */
  changePassword(userId: number, request: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.password(userId), request, {
      context: presentedInContext(),
    });
  }

  /**
   * Resets one account's credential on behalf of an administrator, who does not
   * supply the credential in force.
   *
   * A separate endpoint from {@link changePassword} rather than a mode of it, because
   * the two differ in what they require and in who may call them: a reset proves
   * nothing about the previous credential, so it is restricted to a tenant
   * administrator. Named after the resource it addresses, so the method, the route
   * template and the URL segment all read the same way.
   *
   * Answers with no body.
   *
   * MIGRATION: A RESET IS CARRIED FORWARD; RETRIEVAL IS NOT, AND NEITHER ENDPOINT
   * DISCLOSES A CREDENTIAL. The legacy store was reversible by design and by
   * configuration: the provider was registered with an encrypted - that is, decryptable
   * - format and with retrieval switched on (`Website/release.config` L245 and L239),
   * and the symmetric key that reversed it was committed to source control in the
   * clear at L91 of that same file, alongside the algorithm at L92, with an identical
   * copy in the development configuration. Anyone who could read the repository could
   * read every stored credential. The replacement store is a one-way hash, which makes
   * retrieval impossible rather than merely disabled, and that is the point: there is
   * no method on this service that returns a credential, and there is no
   * send-it-to-me, remind-me or recover-it endpoint anywhere on the surface to write
   * one against. The two operations the legacy provider enabled independently are
   * treated independently here - the one that needed reversibility is gone, and the
   * one that did not survives as this method.
   *
   * @param userId The account whose credential to reset. Interpolated unchanged.
   * @param request The replacement credential.
   * @returns Completion. No payload.
   */
  passwordReset(userId: number, request: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.passwordReset(userId), request, {
      context: presentedInContext(),
    });
  }

  // -------------------------------------------------------------------------
  // Account state transitions
  // -------------------------------------------------------------------------

  /**
   * Sets one account's approval state.
   *
   * The state is a REQUIRED argument carried as a query parameter, and false is
   * transmitted as false rather than treated as an absence. One endpoint taking the
   * desired state replaces what a pair of verb-shaped routes would have been, and the
   * reason is the server's own answer: setting the state an account already holds is
   * reported as a conflict, which is only a meaningful thing to say if the caller
   * stated which state it meant.
   *
   * The parameter is built by the query-string owner, which is also why no separator
   * appears in this file.
   *
   * Answers with no body.
   *
   * @param userId The account to set. Interpolated unchanged.
   * @param isApproved The state to set. Transmitted either way.
   * @returns Completion. No payload.
   */
  setApproval(userId: number, isApproved: boolean): Observable<void> {
    const params: HttpParams = userApprovalParams(isApproved);

    return this.http.put<void>(API_ENDPOINTS.users.approval(userId), null, {
      params,
      context: presentedInContext(),
    });
  }

  /**
   * Releases one account that has been locked out by failed sign-in attempts.
   *
   * Takes no body: the account is the whole of the request, and there is nothing to
   * configure about releasing it. Answers with no body either.
   *
   * @param userId The account to release. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  unlock(userId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.unlock(userId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Obliges one account to change its credential at its next sign-in.
   *
   * Takes no body, and answers with none. Sets the obligation; it does not choose,
   * generate, transmit or return a credential.
   *
   * MIGRATION: the legacy account state that drove this was one member of a
   * five-member status vocabulary the sign-in path evaluated - valid, credential
   * expired, credential expiring, profile incomplete, credential must change - which
   * conflated a hard block with two advisories and a separate concern about the
   * profile. The successor is a small set of independent advisory flags carried on the
   * session rather than one enumeration, so an expiring credential no longer has to be
   * distinguished from an incomplete profile by reading a single number. This method
   * writes one of those facts and reads none of them; the session-scoped flags belong
   * to the authentication contract.
   *
   * @param userId The account to oblige. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  requirePasswordChange(userId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.requirePasswordChange(userId), null, {
      context: presentedInContext(),
    });
  }

  // -------------------------------------------------------------------------
  // The tenant's account policy
  // -------------------------------------------------------------------------

  /**
   * Reads the resolved tenant's account policy.
   *
   * The policy governs every account in the tenant and belongs to no individual
   * account, which is why it is the settings child of the account collection rather
   * than a resource of its own. NOTE that the API path and the screen route are
   * different strings and neither is derived from the other: the API addresses the
   * settings child of the account resource, while the administration route that edits
   * it sits at the top level of the application. Simplifying either into the other
   * produces a 404 on one side or a dead link on the other.
   *
   * MIGRATION: this is a MULTI-KEY contract, not a single number, and it is typed
   * rather than a bag of keys. The legacy application answered with an untyped hash
   * table, so every caller had to know both the spelling of each key and the type of
   * each value, and a mistake in either failed at run time with no diagnostic.
   * `Library/Components/Users/UserModuleBase.vb` shows the defaults being filled in
   * one key at a time - the listing's display mode at L126-L130, whether the pager is
   * hidden at L131-L133, the size of a page at L134-L136 with its fallback of ten, and
   * the default visibility applied to an unset profile value at L138-L142, followed by
   * whether the editor offers a visibility choice at all at L143-L145. Every one of
   * those is a named, typed member of the contract now. The credential policy is NOT
   * part of it: those values are server-side options that never cross the boundary,
   * and restating them here would create a second copy free to drift from the one that
   * is enforced.
   *
   * MIGRATION: the size of a page reaching the client from here, rather than from a
   * constant, is what makes the legacy behaviour reproducible - it was a per-tenant
   * module setting all along, as the listing's own reader at
   * `Website/admin/Users/Users.ascx.vb` L114-L119 shows.
   *
   * ⚠ A TENANT THAT STORES NOTHING IS STILL A `200`. The read answers a portal with no
   * "User Accounts" module instance with the measured legacy defaults and marks them
   * `isStored: false`, so absence of a STORE travels inside the document and never on the
   * status line. A consumer must therefore read provenance from that member and must not
   * infer it from a status: the write for the same address answers `409`
   * `user.membership-settings.storage-conflict` for that tenant, which is a state an
   * operator can repair rather than a resource that is missing.
   *
   * @returns The policy. Every member of the contract is populated on a `200`, whether the
   * values are stored decisions or the platform defaults. Any non-success status is a
   * failure like any other read's.
   */
  getMembershipSettings(): Observable<MembershipSettings> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.membershipSettings(), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBERSHIP_SETTINGS_RESPONSE, body)));
  }

  /**
   * Replaces the resolved tenant's account policy.
   *
   * A REPLACE of the whole contract: every member is sent, exactly as supplied, and
   * NOTHING IS OMITTED FOR READING AS EMPTY. This is the body where that rule earns
   * its keep, because the policy is dense with values that are both legitimate and
   * falsy - a false for each of the nine listing columns and for each of the three
   * profile-editor switches, a zero for a display mode, and a null for each of the
   * three landing pages that means "use the default" rather than "unset". A client
   * that filtered the payload by truthiness would silently turn every unticked column
   * into an absent member, and the server - which serialises and binds without eliding
   * a default - would read the difference as an instruction it was never given.
   *
   * ⚠ ANSWERS WITH A BODY, WHERE EVERY OTHER SETTINGS WRITE IN THIS WORKSPACE ANSWERS
   * `204`. Adopting a new display-name format has a tenant-wide side effect the caller
   * cannot predict from its own request - every account's stored display name is
   * recomposed from it - so the write reports what it did rather than leaving the caller
   * to guess. See {@link MembershipSettingsUpdateResult}.
   *
   * MIGRATION: two collision traps in this vocabulary are worth stating because
   * neither is visible from the type. An allowance of zero means UNLIMITED while minus
   * one means NOT SET, so the two must never be coalesced into one another nor either
   * defaulted to the other; and where a cache duration and a default cache duration
   * both appear they are DISTINCT facts, so one is never substituted for the other.
   * The general rule they are instances of is the legacy sentinel table at
   * `Library/Components/Shared/Null.vb`, whose marker for a missing integer is minus
   * one (L41-L45) and whose marker for a missing string is the EMPTY STRING rather than
   * a null (L71-L75) - `Users.ascx.vb` L252 initialises its query string from that
   * second marker, and L58 initialises a count from the first. Because the markers
   * collide with real data, this file coalesces nothing, defaults nothing and clamps
   * nothing: a value passes through as given.
   *
   * @param request The whole policy to write.
   * @returns What the write did beyond storing the values: whether the display-name format
   * changed, and how many accounts were rewritten as a result.
   */
  updateMembershipSettings(
    request: MembershipSettings,
  ): Observable<MembershipSettingsUpdateResult> {
    return this.http
      .put<unknown>(API_ENDPOINTS.users.membershipSettings(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBERSHIP_SETTINGS_UPDATE_RESPONSE, body)));
  }

  // -------------------------------------------------------------------------
  // Profile declarations - the fields a profile may carry
  // -------------------------------------------------------------------------

  /**
   * Reads every profile declaration the resolved tenant publishes, in its declared
   * display order.
   *
   * DELIBERATELY UNPAGED, and it takes no argument at all. The declaration set is
   * bounded by how many fields an administrator has chosen to define, so paging it
   * would add coordinates to every call in exchange for nothing; and the tenant is
   * resolved by the API from the request, so there is no tenant parameter either.
   * Consequently no page coordinate, no ordering and no filter is emitted on this
   * call - not an empty one, not a defaulted one.
   *
   * @returns The declarations. A read-only array, because the answer is already final.
   */
  listProfileDefinitions(): Observable<readonly ProfilePropertyDefinition[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection(), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PROFILE_DEFINITION_LIST_RESPONSE, body)));
  }

  /**
   * Declares a new profile property in the resolved tenant.
   *
   * MIGRATION: the create and replace bodies are DISTINCT contracts and this method
   * takes the create one. The difference is not stylistic - it follows the terminal
   * stored procedures, which genuinely disagree: the one that adds a declaration
   * accepts a module association and the one that updates a declaration neither
   * declares that parameter nor writes that column. An association can therefore be
   * established when a property is declared and never afterwards, which is exactly
   * what the two shapes express.
   *
   * Neither shape carries a visibility, and its absence is enforced by the contract
   * rather than merely documented: no visibility column exists on the declaration
   * table at any point in the schema's upgrade history, so the value a declaration
   * REPORTS on the way out is a default hint derived from the tenant's account policy,
   * and nothing could persist a value sent on the way in. The API rejects an
   * undeclared member outright instead of discarding it, so a form that offered the
   * choice would collect a decision the server then fails the whole request over.
   *
   * @param request The declaration to create, including the module association only a
   * create may decide.
   * @returns The declaration as recorded, including the identifier the store issued.
   */
  createProfileDefinition(
    request: CreateProfilePropertyDefinitionRequest,
  ): Observable<ProfilePropertyDefinition> {
    return this.http
      .post<unknown>(
        API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection(),
        request,
        { context: presentedInContext() },
      )
      .pipe(map((body) => decodeResponse(PROFILE_DEFINITION_RESPONSE, body)));
  }

  /**
   * Reads one profile declaration.
   *
   * The parameter names a PROPERTY definition, and that spelling is load-bearing on
   * both sides of the wire - the route constrains it as an integer under that name and
   * the contract spells its identity member the same way. A near-miss produces a route
   * that does not match rather than a parameter that is quietly ignored.
   *
   * @param propertyDefinitionId The declaration to read. Interpolated unchanged, and
   * never inspected first - see {@link getById} for why no identifier in this file is
   * guarded on being truthy or positive.
   * @returns The declaration. An identifier naming none arrives as a `404` failure.
   */
  getProfileDefinition(
    propertyDefinitionId: number,
  ): Observable<ProfilePropertyDefinition> {
    return this.http
      .get<unknown>(
        API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(propertyDefinitionId),
        { context: presentedInContext() },
      )
      .pipe(map((body) => decodeResponse(PROFILE_DEFINITION_RESPONSE, body)));
  }

  /**
   * Replaces one profile declaration.
   *
   * MIGRATION: THIS IS ALSO HOW ORDERING IS CHANGED. Position among siblings is a
   * FIELD on the declaration, written through this method, and there is deliberately
   * no endpoint for nudging a declaration up or down. The legacy pair of buttons was
   * never an operation on one row: `Website/admin/Users/ProfileDefinitions.ascx.vb`
   * L182-L187 read the neighbouring declaration's position and swapped the two - the
   * comment at L185 says so in as many words - while L387-L390 supplied the two image
   * columns' labels and L519-L521 dispatched the two commands; a separate bulk pass at
   * L326 renumbered a whole set by assigning each position from its index. Modelling a
   * two-row write as a one-row route would have needed a second call just to discover
   * the neighbour, and would have made the write look atomic when it was not. The
   * caller states the position it wants.
   *
   * Consequently this service exposes exactly the five operations of this resource and
   * no ordering helper of any kind. Computing which positions to write - swapping a
   * pair, renumbering a list after a drag - is the feature's business, because only
   * the feature knows the set it is looking at.
   *
   * The body is the replace contract, which is the shared member set alone: the
   * identifier arrives from the route, the tenant is resolved by the API, and the
   * module association cannot be changed after the declaration exists.
   *
   * @param propertyDefinitionId The declaration to replace. Interpolated unchanged.
   * @param request The members to write, position included.
   * @returns The declaration as recorded.
   */
  updateProfileDefinition(
    propertyDefinitionId: number,
    request: UpdateProfilePropertyDefinitionRequest,
  ): Observable<ProfilePropertyDefinition> {
    return this.http
      .put<unknown>(
        API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(propertyDefinitionId),
        request,
        { context: presentedInContext() },
      )
      .pipe(map((body) => decodeResponse(PROFILE_DEFINITION_RESPONSE, body)));
  }

  /**
   * Removes one profile declaration.
   *
   * Answers with no body. A declaration that cannot be removed - because values are
   * recorded against it, or because the tenant requires it - is refused with a status
   * and a problem document, which reaches the caller as a failure rather than as a
   * silently unchanged list.
   *
   * @param propertyDefinitionId The declaration to remove. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  deleteProfileDefinition(propertyDefinitionId: number): Observable<void> {
    return this.http.delete<void>(
      API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(propertyDefinitionId),
      { context: presentedInContext() },
    );
  }

  // -------------------------------------------------------------------------
  // The account's own subscriptions
  // -------------------------------------------------------------------------

  /**
   * Reads the services offered to one account, with whatever that account already holds
   * against each of them.
   *
   * Replaces the whole read half of `Website/admin/Users/MemberServices.ascx`: the
   * seven-column `grdServices` grid (L26-L71) bound `GetUserRoles(portalId, userId, False)`
   * (`:L147-L157`), whose `False` selected the terminal `GetServices` procedure
   * (`DNNRoleProvider.vb:L481-L488`) and therefore the tenant's PUBLIC roles only.
   *
   * ⚠ EVERY PRESENTATION DECISION ARRIVES DECIDED. The legacy markup called four
   * code-behind helpers per row - `ServiceText` for the command label, `ShowSubscribe` and
   * `ShowTrial` for the two link visibilities, and `FormatExpiryDate` for the lapsed test -
   * each of which read stored role terms, stored assignment dates and the server's own
   * clock. All four are members of the contract, so nothing here or above re-derives them:
   * a client that recomputed "is this lapsed" against the browser's clock would disagree
   * with the server that refuses the command.
   *
   * Unpaged, exactly as the legacy grid was. The answer is one array in the shared envelope.
   *
   * @param userId The account whose catalogue to read. Interpolated unchanged; zero and
   * negative values are transmitted as they stand, per the sentinel discipline recorded on
   * the contract.
   * @returns The catalogue. A read-only array; an account offered nothing has an empty one.
   */
  listMemberServices(userId: number): Observable<readonly MemberService[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.services(userId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBER_SERVICE_LIST_RESPONSE, body)));
  }

  /**
   * Subscribes the account to one service, or renews a subscription that has lapsed.
   *
   * ONE method for both, because the legacy screen had one link for both: `ServiceText`
   * returned `Subscribe` or `Renew` from the same row state and the same command ran
   * (`MemberServices.ascx.vb:L288-L305`, dispatched at `:L439-L452`). The catalogue names
   * which of the two words a row is offering; this call is the same request either way.
   *
   * Takes no body: the account and the service are the whole of the request. Answers with
   * none.
   *
   * ⚠ A SERVICE THAT CHARGES A FEE IS REFUSED, NOT CHARGED. The legacy path handed such a
   * role to `~/admin/Sales/PayPalSubscription.aspx` (`:L113`); sales administration is out
   * of scope for this migration, so the API answers 403 with a distinct reason. The
   * catalogue reports which rows those are, so a caller can explain the refusal instead of
   * offering a command that cannot complete.
   *
   * @param userId The account to subscribe. Interpolated unchanged.
   * @param roleId The service to subscribe to. Interpolated unchanged; zero is a real role.
   * @returns Completion. No payload.
   */
  subscribeToService(userId: number, roleId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.serviceSubscription(userId, roleId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Cancels the account's subscription to one service.
   *
   * ⚠ CANCELLING MAY EXPIRE THE ASSIGNMENT RATHER THAN REMOVE IT, and that is the legacy
   * rule rather than a compromise: `RoleController.vb:L494-L496` expires an assignment whose
   * role charges a fee instead of deleting it, so a paid history is not destroyed by a
   * cancellation. The API reports which of the two it did as a success reason; either way the
   * account no longer holds the service, and re-reading the catalogue is what shows the new
   * state.
   *
   * ⚠ A FEE-BEARING SERVICE IS REFUSED IN THIS DIRECTION TOO. The legacy cancel arm shared
   * the subscribe gate and reached the same payment page with `&cancel=1` appended
   * (`MemberServices.ascx.vb:L115`), so cancelling one locally while a processor still held
   * the arrangement would leave the two systems disagreeing about a paying subscriber.
   *
   * Answers with no body.
   *
   * @param userId The account to cancel for. Interpolated unchanged.
   * @param roleId The service to cancel. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  cancelService(userId: number, roleId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.users.serviceSubscription(userId, roleId), {
      context: presentedInContext(),
    });
  }

  /**
   * Takes one service's trial period on the account's behalf.
   *
   * A second command rather than a variant of the first, because the legacy screen gated it
   * separately: `ShowTrial` (`MemberServices.ascx.vb:L325-L342`) offers it only for a public
   * role that DOES charge a service fee, charges nothing for its trial, and has not already
   * been tried by this account. A trial that the catalogue offers is always performable -
   * the operation's own gate is the zero trial fee - so there is no payment counterpart to
   * this call.
   *
   * Takes no body, and answers with none.
   *
   * @param userId The account taking the trial. Interpolated unchanged.
   * @param roleId The service whose trial to take. Interpolated unchanged.
   * @returns Completion. No payload.
   */
  startServiceTrial(userId: number, roleId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.serviceTrial(userId, roleId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Redeems an invitation code, joining the account to every role recorded against it.
   *
   * The legacy affordance was a fifty-character box and a subscribe command
   * (`MemberServices.ascx:L14-L15`) whose handler walked the tenant's whole role set with NO
   * EARLY EXIT (`MemberServices.ascx.vb:L397-L433`), subscribing on every role whose stored
   * code matched. The answer therefore names the roles that were joined, and may name more
   * than one.
   *
   * ⚠ THE CODE IS SENT AS TYPED. The legacy comparison was ordinary string equality against
   * the stored code, so leading space and case both mattered; trimming or folding it here
   * would admit codes the legacy application refused.
   *
   * ⚠ AN EMPTY SUBMISSION IS REFUSED BY THE API RATHER THAN IGNORED. The legacy handler
   * guarded on a non-empty code, and that guard was load-bearing: a role with no code
   * recorded read as the empty string through the legacy null contract, so an empty
   * submission would have matched every such role. Sending it and letting the server refuse
   * it keeps one rule in one place; the caller may of course also decline to submit.
   *
   * @param userId The account redeeming the code. Interpolated unchanged.
   * @param request The code as typed.
   * @returns The roles the code admitted the account to. Never empty on success - a code that
   * matched nothing is a refusal.
   */
  redeemServiceCode(
    userId: number,
    request: RedeemServiceCodeRequest,
  ): Observable<RedeemServiceCodeResult> {
    return this.http
      .post<unknown>(API_ENDPOINTS.users.serviceRedemptions(userId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(REDEEM_SERVICE_CODE_RESPONSE, body)));
  }
}
