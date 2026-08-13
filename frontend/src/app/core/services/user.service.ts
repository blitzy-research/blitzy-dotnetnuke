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
 * One decoder per response shape this transport reads, composed once at module scope. ⚠ NONE IS
 * `nullable`, AND THAT MIRRORS THE CONTRACT RATHER THAN BEING OPTIMISTIC. Each of these endpoints answers
 * a successful read with a populated payload, and expresses "the target does not resolve" as a `404`
 * problem document — never as a `200` carrying nothing.
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

@Injectable({ providedIn: 'root' })
export class UserService {
  /**
   * The transport. Injected as a field rather than through a constructor parameter, matching the rest of
   * this folder.
   */
  private readonly http = inject(HttpClient);

  // -------------------------------------------------------------------------
  // Accounts
  // -------------------------------------------------------------------------

  /**
   * Reads one page of the resolved tenant's accounts. The whole query - the page coordinates, the
   * ordering and the search - is one argument, and it is handed to the query-string owner unchanged.
   *
   * @param query The page to return, its size, the ordering and the search.
   * @returns The page: the account rows plus the envelope carrying the total, the zero-based index and
   * the size the server applied.
   */
  list(query: UserListQuery): Observable<PagedUserList> {
    // ⚠ THE TRANSPORT IS CHOSEN BY WHETHER THE QUERY NAMES A PERSON, AND THIS BRANCH MUST NOT BE COLLAPSED
    // TO ONE CALL. A user name, an email address and an arbitrary profile-property name paired with the
    // value to match all identify somebody.
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
   * Reads one page of the tenant's accounts as an account PICKER needs them. ⚠ NOT A THINNER MODE OF
   * {@link UserService.list} — A DIFFERENT ENDPOINT, AND THE DISTINCTION IS THE WHOLE POINT. A
   * performance and privacy review measured the role-assignment screen building its account drop-down,
   * and its account-count probe, from the account LISTING. Every candidate row carried a postal address,
   * a telephone number, an electronic-mail address, a creation instant, a last-login instant and four
   * status flags across the wire so that three values could be rendered — and that screen may enumerate a
   * tenant of up to a thousand accounts before it decides to offer a name box instead.
   *
   * @param query The page to return, its size, the ordering and the caption prefix to match.
   * @returns The page: the choices plus the envelope carrying the total, the zero-based index and the
   * size the server applied.
   */
  listChoices(query: PagedRequestParams): Observable<PagedUserChoiceList> {
    // A plain `GET`, unlike the listing's identifying-search branch.
    return this.http
      .get<unknown>(API_ENDPOINTS.users.choices(), {
        params: pagedRequestParams(query),
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_CHOICE_PAGE, body)));
  }

  /**
   * Reads one account. An identifier naming no account in the resolved tenant is refused with a not-found
   * problem document, so a successful answer always carries an account.
   *
   * @param userId The account to read.
   * @returns The account.
   */
  getById(userId: number): Observable<UserDetail> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.byId(userId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(USER_DETAIL_RESPONSE, body)));
  }

  /**
   * Creates an account in the resolved tenant. the account-quota rule is the server's, and is not
   * pre-checked.
   *
   * @param request The account to create.
   * @returns The created account as the server recorded it, including the identifier it issued.
   */
  create(request: CreateUserRequest): Observable<UserDetail> {
    return this.http
      .post<unknown>(API_ENDPOINTS.users.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_DETAIL_RESPONSE, body)));
  }

  /**
   * Replaces one account's editable fields. The body is sent exactly as supplied, member for member.
   *
   * @param userId The account to replace.
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
   * Removes one account. Answers with no body, so the observable emits once and carries nothing.
   *
   * @param userId The account to remove.
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
   * Reads one account's profile: every property the tenant declares, whether or not this account has
   * recorded a value for it. The declarations arrive with the values, so a profile editor does not have
   * to read the declaration list separately in order to render a field it has no value for.
   *
   * @param userId The account whose profile to read.
   * @returns The profile.
   */
  getProfile(userId: number): Observable<UserProfile> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.profile(userId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(USER_PROFILE_RESPONSE, body)));
  }

  /**
   * @param userId The account whose profile to replace.
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
   * Changes one account's credential on behalf of the account holder, who supplies the credential in
   * force alongside the replacement. Answers with no body.
   *
   * @param userId The account whose credential to change.
   * @param request The credential in force and its replacement.
   * @returns Completion. No payload.
   */
  changePassword(userId: number, request: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.password(userId), request, {
      context: presentedInContext(),
    });
  }

  /**
   * Resets one account's credential on behalf of an administrator, who does not supply the credential in
   * force. A separate endpoint from {@link changePassword} rather than a mode of it, because the two
   * differ in what they require and in who may call them: a reset proves nothing about the previous
   * credential, so it is restricted to a tenant administrator.
   *
   * @param userId The account whose credential to reset.
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
   * @param userId The account to set.
   * @param isApproved The state to set.
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
   * @param userId The account to release.
   * @returns Completion. No payload.
   */
  unlock(userId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.unlock(userId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Obliges one account to change its credential at its next sign-in. Takes no body, and answers with
   * none.
   *
   * @param userId The account to oblige.
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
   * Reads the resolved tenant's account policy. The policy governs every account in the tenant and
   * belongs to no individual account, which is why it is the settings child of the account collection
   * rather than a resource of its own.
   *
   * @returns The policy.
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
   * @param request The whole policy to write.
   * @returns What the write did beyond storing the values: whether the display-name format changed, and
   * how many accounts were rewritten as a result.
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
   * Reads every profile declaration the resolved tenant publishes, in its declared display order.
   * DELIBERATELY UNPAGED, and it takes no argument at all.
   *
   * @returns The declarations.
   */
  listProfileDefinitions(): Observable<readonly ProfilePropertyDefinition[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection(), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PROFILE_DEFINITION_LIST_RESPONSE, body)));
  }

  /**
   * Declares a new profile property in the resolved tenant. the create and replace bodies are DISTINCT
   * contracts and this method takes the create one.
   *
   * @param request The declaration to create, including the module association only a create may decide.
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
   * Reads one profile declaration. The parameter names a PROPERTY definition, and that spelling is
   * load-bearing on both sides of the wire - the route constrains it as an integer under that name and
   * the contract spells its identity member the same way.
   *
   * @param propertyDefinitionId The declaration to read.
   * @returns The declaration.
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
   * Replaces one profile declaration. THIS IS ALSO HOW ORDERING IS CHANGED. Position among siblings is a
   * FIELD on the declaration, written through this method, and there is deliberately no endpoint for
   * nudging a declaration up or down.
   *
   * @param propertyDefinitionId The declaration to replace.
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
   * Removes one profile declaration. Answers with no body.
   *
   * @param propertyDefinitionId The declaration to remove.
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
   * Reads the services offered to one account, with whatever that account already holds against each of
   * them.
   *
   * @param userId The account whose catalogue to read.
   * @returns The catalogue.
   */
  listMemberServices(userId: number): Observable<readonly MemberService[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.users.services(userId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBER_SERVICE_LIST_RESPONSE, body)));
  }

  /**
   * @param userId The account to subscribe.
   * @param roleId The service to subscribe to.
   * @returns Completion. No payload.
   */
  subscribeToService(userId: number, roleId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.serviceSubscription(userId, roleId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Cancels the account's subscription to one service. ⚠ CANCELLING MAY EXPIRE THE ASSIGNMENT RATHER THAN
   * REMOVE IT, and that is the legacy rule rather than a compromise: `RoleController.vb:L494-L496`
   * expires an assignment whose role charges a fee instead of deleting it, so a paid history is not
   * destroyed by a cancellation.
   *
   * @param userId The account to cancel for.
   * @param roleId The service to cancel.
   * @returns Completion. No payload.
   */
  cancelService(userId: number, roleId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.users.serviceSubscription(userId, roleId), {
      context: presentedInContext(),
    });
  }

  /**
   * Takes one service's trial period on the account's behalf. A second command rather than a variant of
   * the first, because the legacy screen gated it separately: `ShowTrial` offers it only for a public
   * role that DOES charge a service fee, charges nothing for its trial, and has not already been tried by
   * this account.
   *
   * @param userId The account taking the trial.
   * @param roleId The service whose trial to take.
   * @returns Completion. No payload.
   */
  startServiceTrial(userId: number, roleId: number): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.users.serviceTrial(userId, roleId), null, {
      context: presentedInContext(),
    });
  }

  /**
   * Redeems an invitation code, joining the account to every role recorded against it. The legacy
   * affordance was a fifty-character box and a subscribe command whose handler walked the tenant's whole
   * role set with NO EARLY EXIT, subscribing on every role whose stored code matched.
   *
   * @param userId The account redeeming the code.
   * @param request The code as typed.
   * @returns The roles the code admitted the account to.
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
