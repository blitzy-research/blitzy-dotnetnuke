// MIGRATION: this service is the client half of a READ-ONLY catalogue, and that is a boundary rather than a
// phase. Three legacy permission controllers collapse behind it and exactly two reads survive as HTTP
// endpoints. The catalogue MUTATORS are deliberately not ported, because catalogue rows are reference data
// seeded by the upgrade scripts and written only by the out-of-scope module installer - there is no
// administration screen for them anywhere under Website/admin/. So this service declares no create, update or
// delete method, and none may be added: a write over installer-owned reference data would invite a partial
// reimplementation of the installer.
//
// MIGRATION: permission EVALUATION is deliberately absent here. This service asks WHICH permissions exist; it
// never decides whether a caller holds one. Deciding happens in exactly one place -
// Infrastructure/Security/PermissionEvaluator.cs, reached through
// Api/Authorization/PermissionAuthorizationHandler.cs - because a second evaluator that could disagree with
// the first would agree throughout testing and diverge on the single case that mattered. The client-side
// affordances hide controls a caller cannot use; they are a convenience and never the enforcement.
//
// MIGRATION: the ";"-delimited role string and the bracketed "[userId]" pseudo-role are NOT reproduced, here
// or anywhere else on the client. That format packed two kinds of subject into one string that had to be split
// apart again by whatever read it, and a role whose name contained the delimiter could not survive the round
// trip. A grant now carries userId as a first-class nullable integer alongside allowAccess.
//
// Two closed vocabularies exist and they are not interchangeable - confusing them is the likeliest mistake in
// this area, so both are named:
//   (1) the PERSISTED permission key, whose whole vocabulary is VIEW, EDIT, READ and WRITE. Upper case, the
//     member name IS the stored value, compared with exact string equality and gated by a separate
//     allow-access flag on every comparison. READ and WRITE are folder-scope keys and never appear in a
//     policy. This is what the `permissionKey` filter and the catalogue payload carry.
//   (2) the AUTHORISATION POLICY names, a closed set of NINE: ModuleView, ModuleEdit, TabView, TabEdit,
//     PortalAdministrator, HostAdministrator, AccountOwner, AccountOwnerOrPortalAdministrator and
//     PortalContentEditor. Mixed case, and never travelling in a request from this service. The client's
//     single source of truth for them is the PERMISSION_POLICIES tuple in core/guards/permission.guard.ts,
//     which mirrors Api/Authorization/PolicyNames.cs; this note lists them rather than importing them
//     because it exists to say the two vocabularies are NOT the same set, and that point is lost if one of
//     them is only a reference. No policy provider is registered on the server, so an unregistered policy
//     name fails at request time rather than at start-up.
// This service maps neither onto the other and validates against neither: a closed-set check here would be
// business logic, and it would wrongly reject a key an installation legitimately holds.
//
// MIGRATION: there is NO deny prefix in this generation of the platform. Later releases marked a denial by
// prefixing the key with an exclamation mark; this codebase does not, and denial is carried by the
// allow-access flag alone. Nothing here emits, parses or strips a prefix, and adding that handling would
// silently corrupt a legitimate key.
//
// MIGRATION: the legacy caching layer is not reproduced client-side. NOTHING is cached, replayed or shared
// here: each method returns a cold observable performing one request per subscription, because a replay
// operator or a retained response would put a second, quieter cache in front of the server's and let a stale
// catalogue outlive its eviction.

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { decodePermission } from '../models/permission.model';
import { arrayOf, decodeResponse, decodeString, responseOf } from '../utils/decode.util';
import { presentedInContext } from './notification.service';
import { map } from 'rxjs';

import type { Decoder } from '../utils/decode.util';
import type { Permission } from '../models/permission.model';
import type { ApiResponse } from '../models/paged-result.model';
import { permissionListParams, type PermissionListFilter } from '../utils/http-params.util';

/**
 * One decoder per response shape this transport reads, composed once at module scope.
 *
 * Both are built with `responseOf` rather than `envelopeOf` because the published signatures of the methods
 * below RETURN THE ENVELOPE, and validating must not change the shape they return.
 */
const PERMISSION_KEY_LIST_RESPONSE: Decoder<ApiResponse<readonly string[]>> = responseOf(
  arrayOf(decodeString),
);
const PERMISSION_RESPONSE: Decoder<ApiResponse<Permission>> = responseOf(decodePermission);

/**
 * Reads the permission catalogue: which permission keys this installation defines.
 *
 * Transport only: each method is one endpoint, one typed request, one cold observable returned directly from
 * the HTTP client. No validation, derivation, orchestration, multi-call sequencing, retry, caching or
 * reshaping, because the migration discipline restricts an Angular service to API communication - selecting,
 * sorting, grouping and holding belong to a feature store, and every access decision to the server.
 *
 * Neither method subscribes, so no request is issued until a caller asks for one and every caller controls its
 * own teardown. No header is set here either: the correlation identifier, the bearer credential and the
 * translation of an RFC 7807 problem document are applied once each, in that order, by the interceptor chain
 * configured in `app.config.ts`, and setting one here would duplicate an interceptor whose copy would
 * eventually disagree.
 *
 * The server applies `PolicyNames.PortalAdministrator` to both endpoints, so a caller with no credential
 * receives 401 and a signed-in caller without that standing receives 403 - a distinction the legacy redirect
 * to the access-denied page could not express.
 *
 * @example
 * ```ts
 * const everyKey$ = this.permissions.list();
 * const forDefinition$ = this.permissions.list({ moduleDefinitionId: 42 });
 * // The legacy code-and-key lookup, as one conjunctive query.
 * const oneKey$ = this.permissions.list({ permissionCode: 'SYSTEM_MODULE_DEFINITION', permissionKey: 'EDIT' });
 * const definition$ = this.permissions.getById(7);
 * ```
 */
@Injectable({ providedIn: 'root' })
export class PermissionService {
  /**
   * The HTTP client, resolved from the injector rather than declared as a constructor parameter so that the
   * class needs no constructor at all.
   *
   * The client itself is provided once, application-wide, by `provideHttpClient` in `app.config.ts`. No
   * component provides this service: `providedIn: 'root'` registers it against the root injector, which
   * keeps it tree-shakeable in a production build and guarantees one instance rather than one per component
   * that happened to list it.
   */
  private readonly http = inject(HttpClient);

  /**
   * Reads the catalogue, optionally narrowed by the filters the server accepts.
   *
   * The payload is a bare array of key strings, not a list of records - that is the server's contract, and
   * it is what a client-side permission check tests against. Full records come from {@link getById}, where a
   * key alone would be ambiguous because the same key is declared repeatedly across scopes.
   *
   * The element type is `string` rather than a union of the four known keys: the column is `varchar(20)` and
   * the producer is open, so an installation may carry a key seeded by a module this codebase has never
   * seen. The narrower union would statically promise something untrue and make a check that looks
   * exhaustive fail at run time. Narrow an individual value with the guard in
   * `shared/directives/has-permission.directive.ts`, never with a cast.
   *
   * Unpaged deliberately: the catalogue is small, bounded reference data, so the whole sequence is returned,
   * `meta` is `null`, and nothing matching is 200 with an empty array rather than a 404 or a null array.
   *
   * @param filter The restrictions to apply, or omitted or `null` to read the whole catalogue. The three
   *   members the server binds - `permissionCode`, `moduleDefinitionId` and `permissionKey` - compose
   *   conjunctively. Absence is decided by the shared serialiser on an explicit undefined-or-null test,
   *   which is what lets `0`, `-1`, `false` and the empty string travel as the real values they are, so this
   *   method adds no test and no default. Values are forwarded verbatim, never trimmed, case-folded, mapped
   *   onto a policy name or checked against a closed set, because duplicating the server's validation rule
   *   here would produce two copies that eventually disagree.
   * @returns The success envelope carrying every matching key. 200 is the only success status. A malformed
   *   filter answers 400, an absent or invalid credential 401, and a caller without administrator standing
   *   403; each of those arrives as a problem document that the error interceptor translates, so no failure
   *   is handled here.
   */
  list(filter?: PermissionListFilter | null): Observable<ApiResponse<readonly string[]>> {
    // The URL arrives fully composed, already carrying the configured API base and the version segment.
    // Prefixing it again would produce a doubled base that type-checks, bundles and deploys without
    // complaint and then answers 404 on every call. The query string is likewise built only by the shared
    // serialiser, the single owner of parameter serialisation here; assembling one by hand would be a second
    // implementation of a rule already measured against the server's binder.
    return this.http
      .get<unknown>(API_ENDPOINTS.permissions.collection(), {
        params: permissionListParams(filter),
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PERMISSION_KEY_LIST_RESPONSE, body)));
  }

  /**
   * Reads one catalogue definition by its identifier.
   *
   * Answers with the whole record rather than a bare key, because the same key is declared repeatedly across
   * scopes and a key alone would not say which scope code or module definition it belongs to. MIGRATION: the
   * legacy lookup could return a null object; absence is now a 404, so a successful response always carries
   * a definition.
   *
   * The catalogue carries no portal column, so this read is installation-wide exactly as the listing is, and
   * the administrator policy protects it just the same.
   *
   * @param permissionId Identifier of the definition wanted, interpolated exactly as supplied. No bound is
   *   imposed on it here or in the URL builder, and none may be added: a local test is unsafe in this
   *   schema, where the legacy absent-integer marker is `-1` yet the portal table is `IDENTITY (-1, 1)` and
   *   the role, page and module tables seed at `0`, so one value means both "the first row" and "no row"
   *   depending on context this method does not have. Nothing here applies a truthiness test, a sign
   *   comparison, a comparison against the marker or a coalescing default; an unknown identifier is reported
   *   as absent by the read itself.
   * @returns The success envelope carrying the definition. 200 on success; 400 if the identifier cannot be
   *   bound, 401 without a valid credential, 403 without administrator standing, and 404 when no definition
   *   bears that identifier.
   */
  getById(permissionId: number): Observable<ApiResponse<Permission>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.permissions.byId(permissionId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PERMISSION_RESPONSE, body)));
  }
}
