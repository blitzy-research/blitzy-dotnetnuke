import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

import { map } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import {
  decodeRole,
  decodeRoleGroup,
  decodeRoleListItem,
  decodeUserRole,
} from '../models/role.model';
import { arrayOf, decodeResponse, pageOf, responseOf } from '../utils/decode.util';
import { pagedRequestParams, roleListParams } from '../utils/http-params.util';
import { presentedInContext } from './notification.service';

import type { Decoder } from '../utils/decode.util';
import type { ApiResponse, PagedResponse } from '../models/paged-result.model';
import type {
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
import type { PagedRequestParams, RoleListFilter } from '../utils/http-params.util';
import type { Observable } from 'rxjs';

/** One decoder per response shape this transport reads, composed once at module scope. */
const ROLE_PAGE: Decoder<PagedResponse<RoleListItem>> = pageOf(decodeRoleListItem);
const MEMBERSHIP_PAGE: Decoder<PagedResponse<UserRole>> = pageOf(decodeUserRole);
const MEMBERSHIP_RESPONSE: Decoder<ApiResponse<UserRole>> = responseOf(decodeUserRole);
const ROLE_RESPONSE: Decoder<ApiResponse<Role>> = responseOf(decodeRole);
const ROLE_GROUP_RESPONSE: Decoder<ApiResponse<RoleGroup>> = responseOf(decodeRoleGroup);
const ROLE_GROUP_LIST_RESPONSE: Decoder<ApiResponse<readonly RoleGroup[]>> = responseOf(
  arrayOf(decodeRoleGroup),
);

/** The roles one account holds, which is an UNPAGED sequence rather than a page. */
const USER_ROLE_LIST_RESPONSE: Decoder<ApiResponse<readonly RoleListItem[]>> = responseOf(
  arrayOf(decodeRoleListItem),
);

@Injectable({ providedIn: 'root' })
export class RoleService {
  private readonly http = inject(HttpClient);

  // -------------------------------------------------------------------------
  // ROLES
  // -------------------------------------------------------------------------

  /**
   * `GET /api/v1/roles` — one page of the tenant's roles. Answers `200` with a page, and an empty page is
   * a legitimate answer rather than a failure: a consumer distinguishes "past the end" from "nothing
   * matched" by reading the total on the envelope's metadata, never by finding a member missing.
   *
   * @param request The page to return, its size, the ordering, and the paging contract's own free-text
   * filter.
   * @param filter The role-group narrowing, or omitted or `null` for none.
   * @returns The page, in the paged wire envelope.
   */
  listRoles(
    request: PagedRequestParams,
    filter?: RoleListFilter | null,
  ): Observable<PagedResponse<RoleListItem>> {
    const params: HttpParams = roleListParams(request, filter);

    return this.http
      .get<unknown>(API_ENDPOINTS.roles.forCurrentPortal.collection(), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_PAGE, body)));
  }

  /**
   * `GET /api/v1/roles/{roleId}` — one role, with its paid-membership terms. Answers `200` with the role
   * in the single-payload envelope, `403` when the caller does not administer the resolved tenant, and
   * `404` when the tenant defines no role with that identifier — which is also the answer for a role that
   * exists in a different tenant, matching how the legacy screens treated a cross-tenant identifier.
   *
   * @param roleId The role to read, forwarded exactly as supplied.
   * @returns The role, in the single-payload wire envelope.
   */
  getRole(roleId: number): Observable<ApiResponse<Role>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.roles.forCurrentPortal.byId(roleId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_RESPONSE, body)));
  }

  /**
   * `POST /api/v1/roles` — creates a role in the resolved tenant. Answers `201` with the created role in
   * the single-payload envelope.
   *
   * @param request The role's name, description, grouping, visibility and the two sets of paid-membership
   * terms.
   * @returns The created role, in the single-payload wire envelope.
   */
  createRole(request: CreateRoleRequest): Observable<ApiResponse<Role>> {
    return this.http
      .post<unknown>(API_ENDPOINTS.roles.forCurrentPortal.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_RESPONSE, body)));
  }

  /**
   * `PUT /api/v1/roles/{roleId}` — replaces a role's editable state. Answers `200` with the updated role
   * in the single-payload envelope, `400` for a refused body, `403` for a caller who does not administer
   * the resolved tenant or for a field-level rule the tenant protects, `404` for an unknown role and
   * `409` for a name the tenant already uses.
   *
   * @param roleId The role to replace, forwarded exactly as supplied.
   * @param request The complete editable state to store.
   * @returns The updated role, in the single-payload wire envelope.
   */
  updateRole(roleId: number, request: UpdateRoleRequest): Observable<ApiResponse<Role>> {
    return this.http
      .put<unknown>(API_ENDPOINTS.roles.forCurrentPortal.byId(roleId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_RESPONSE, body)));
  }

  /**
   * @param roleId The role to remove, forwarded exactly as supplied.
   * @returns Completion , with no payload.
   */
  deleteRole(roleId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.roles.forCurrentPortal.byId(roleId), {
      context: presentedInContext(),
    });
  }

  // -------------------------------------------------------------------------
  // ROLE MEMBERSHIP — the accounts holding a role
  // -------------------------------------------------------------------------

  /**
   * `GET /api/v1/roles/{roleId}/users` — one page of the accounts holding a role. Answers `200` with a
   * page of membership rows, each carrying the assignment's own identifier, the account's name and
   * display label, the role, and the two date bounds.
   *
   * @param roleId The role whose members to read, forwarded exactly as supplied.
   * @param request The page to return, its size, the ordering and the free-text filter.
   * @returns The page of memberships, in the paged wire envelope.
   */
  listUsers(roleId: number, request: PagedRequestParams): Observable<PagedResponse<UserRole>> {
    const params: HttpParams = pagedRequestParams(request);

    return this.http
      .get<unknown>(API_ENDPOINTS.roles.forCurrentPortal.members(roleId), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBERSHIP_PAGE, body)));
  }

  /**
   * @param roleId The role to ask about, interpolated exactly as supplied.
   * @param userId The account to ask about, interpolated exactly as supplied.
   * @returns The membership, in the single-payload envelope.
   */
  getMembership(roleId: number, userId: number): Observable<ApiResponse<UserRole>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.roles.forCurrentPortal.member({ roleId, userId }), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MEMBERSHIP_RESPONSE, body)));
  }

  /**
   * @param roleId The role to grant, forwarded exactly as supplied.
   * @param request The account to enrol, the two optional date bounds, and the notification choice.
   * @returns Completion , with no payload.
   */
  assignUser(roleId: number, request: RoleAssignmentRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.roles.forCurrentPortal.members(roleId), request, {
      context: presentedInContext(),
    });
  }

  /**
   * @param roleId The role the account is being removed from, forwarded exactly as supplied.
   * @param userId The account being removed, forwarded exactly as supplied.
   * @returns Completion , with no payload.
   */
  removeUser(roleId: number, userId: number): Observable<void> {
    return this.http.delete<void>(
      API_ENDPOINTS.roles.forCurrentPortal.member({ roleId, userId }),
      { context: presentedInContext() },
    );
  }

  // -------------------------------------------------------------------------
  // ROLE GROUPS — the optional grouping above a role
  // -------------------------------------------------------------------------

  /** @returns The tenant's role groups, in the single-payload wire envelope. */
  listRoleGroups(): Observable<ApiResponse<readonly RoleGroup[]>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.roleGroups.forCurrentPortal.collection(), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_GROUP_LIST_RESPONSE, body)));
  }

  /**
   * `GET /api/v1/users/{userId}/roles` — the roles one account holds in the resolved tenant. UNPAGED, and
   * no page, size, sort or filter parameter is sent, because the server accepts none: it declares the
   * read unpaged on the grounds that the legacy reader it replaces returned every role an account held
   * with no pager at all.
   *
   * @param userId The account whose roles to read.
   * @returns The roles the account holds, inside the shared success envelope.
   */
  listRolesHeldByUser(userId: number): Observable<ApiResponse<readonly RoleListItem[]>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.roles.forCurrentPortal.heldByUser(userId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(USER_ROLE_LIST_RESPONSE, body)));
  }

  /**
   * `POST /api/v1/role-groups` — creates a role group in the resolved tenant. Answers `201` with the
   * created group in the single-payload envelope.
   *
   * @param request The group's name and description.
   * @returns The created group, in the single-payload wire envelope.
   */
  createRoleGroup(request: CreateRoleGroupRequest): Observable<ApiResponse<RoleGroup>> {
    return this.http
      .post<unknown>(API_ENDPOINTS.roleGroups.forCurrentPortal.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_GROUP_RESPONSE, body)));
  }

  /**
   * `GET /api/v1/role-groups/{roleGroupId}` — one role group. Answers `200` with the group in the
   * single-payload envelope, `403` when the caller does not administer the resolved tenant, and `404`
   * when the tenant defines no group with that identifier.
   *
   * @param roleGroupId The group to read, forwarded exactly as supplied.
   * @returns The group, in the single-payload wire envelope.
   */
  getRoleGroup(roleGroupId: number): Observable<ApiResponse<RoleGroup>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_GROUP_RESPONSE, body)));
  }

  /**
   * `PUT /api/v1/role-groups/{roleGroupId}` — replaces a role group's editable state. Answers `200` with
   * the updated group in the single-payload envelope, `400` for a refused body, `403` for a caller who
   * does not administer the resolved tenant, `404` for an unknown group and `409` for a name the tenant
   * already uses.
   *
   * @param roleGroupId The group to replace, forwarded exactly as supplied.
   * @param request The complete editable state to store.
   * @returns The updated group, in the single-payload wire envelope.
   */
  updateRoleGroup(
    roleGroupId: number,
    request: UpdateRoleGroupRequest,
  ): Observable<ApiResponse<RoleGroup>> {
    return this.http
      .put<unknown>(API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(ROLE_GROUP_RESPONSE, body)));
  }

  /**
   * @param roleGroupId The group to remove, forwarded exactly as supplied.
   * @returns Completion , with no payload.
   */
  deleteRoleGroup(roleGroupId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId), {
      context: presentedInContext(),
    });
  }
}
