// MIGRATION: this service is the client half of a READ-ONLY catalogue, and that is a
// boundary rather than a phase. Three legacy controllers are collapsed behind it, measured
// rather than estimated: PermissionController.vb is 71 lines with 9 public members,
// Library/Components/Security/Permissions/ModulePermissionController.vb is 389 lines with
// 18, and TabPermissionController.vb is 349 lines with 15 — 42 members, of which exactly
// TWO reads survive as HTTP endpoints. The reduction is accounted for rather than trimmed
// by taste. The catalogue MUTATORS are not ported (PermissionController.vb:L55
// DeletePermission, :L59 AddPermission, :L63 UpdatePermission) because catalogue rows are
// reference data seeded by the upgrade scripts and written only by the module installer,
// which is out of scope; there is no administration screen for them anywhere under
// Website/admin/. Consequently this service declares no create, update or delete method,
// and none may be added: a write over installer-owned reference data would invite a partial
// reimplementation of the installer.
//
// MIGRATION: permission EVALUATION is deliberately absent here. This service asks WHICH
// permissions exist; it never decides whether a caller holds one. Deciding happens in
// exactly one place — Infrastructure/Security/PermissionEvaluator.cs, reached through
// Api/Authorization/PermissionAuthorizationHandler.cs — because a second evaluator that
// could disagree with the first is the worst outcome available in this area: the two would
// agree throughout testing and diverge on the single case that mattered. The client-side
// permission affordances (shared/directives/has-permission.directive.ts, and the planned
// route check permission.guard.ts) hide controls a caller cannot use; they are a
// convenience and never the enforcement, which is applied server-side on every request.
//
// MIGRATION: the ";"-delimited role string and the bracketed "[userId]" pseudo-role are
// NOT reproduced, in this file or anywhere else on the client. The legacy display format is
// Library/Components/Security/Permissions/ModulePermissionController.vb:L239-L251 —
// GetModulePermissions accumulated strRoles and strUsers (L240-L241), appended
// `RoleName + ";"` for a role grant and `"[" + UserID.ToString + "];"` for a user grant
// (L244-L248), and returned `";" & strRoles & strUsers` (L251). Measured across
// Library/Components/Security/Permissions/*.vb there are 22 semicolon-join occurrences and
// 8 bracket occurrences of that idiom. It packed two different kinds of subject into one
// string that then had to be split apart again by whatever read it, and a role whose name
// contained the delimiter could not survive the round trip. The target models a grant's
// userId as a first-class nullable integer alongside allowAccess, so nothing here builds,
// emits or parses either form.
//
// MIGRATION: PermissionInfo became Permission
// (Library/Components/Security/Permissions/Permission.vb:L28, namespace
// DotNetNuke.Security.Permissions). Its five properties map one for one onto the five
// members of the client model: PermissionID L42, PermissionCode L51, ModuleDefID L60,
// PermissionKey L69, PermissionName L78. The XML serialisation attributes are dropped — the
// legacy class carried <XmlElement> on three properties and, worth recording because it is
// easy to miss, <XmlIgnore()> on TWO of them rather than one: ModuleDefID (L60) AND
// PermissionName (L78). The transfer contract now owns the wire shape outright, so the
// entity is never serialised directly and no attribute governs what a caller receives.
//
// MIGRATION: TWO CLOSED VOCABULARIES EXIST AND THEY ARE NOT INTERCHANGEABLE. Confusing
// them is the likeliest mistake anyone will make in this area, so both are named here.
//   (1) The PERSISTED permission key, whose whole vocabulary is VIEW, EDIT, READ and WRITE.
//       These are upper case and the member name IS the stored value, compared with exact
//       string equality and gated by a separate allow-access flag on every comparison —
//       Library/Components/Security/Permissions/ModulePermissionController.vb:L36, :L243
//       and :L333, and TabPermissionController.vb:L41, :L218 and :L309. READ and WRITE are
//       folder-scope keys and never appear in a policy. This vocabulary is what the
//       `permissionKey` filter and the catalogue payload carry.
//   (2) The AUTHORISATION POLICY names, whose whole vocabulary is ModuleView, ModuleEdit,
//       TabView, TabEdit and PortalAdministrator. These are mixed case and never travel in
//       a request from this service. No policy provider is registered on the server, so an
//       unregistered policy name fails at request time rather than at start-up.
// A value from one set is never a substitute for a value from the other, and this service
// maps neither onto the other. It also validates against neither: a closed-set check here
// would be business logic, and it would wrongly reject a key an installation legitimately
// holds, since the catalogue stores whatever the installer seeded.
//
// MIGRATION: there is NO deny prefix in this generation of the platform. Later releases
// marked a denial by prefixing the key with a leading exclamation mark; this codebase does
// not, and the absence was measured rather than assumed — across all eleven files under
// Library/Components/Security/Permissions/ there are zero occurrences of such a prefix in
// any form. Denial is carried by the allow-access flag alone. Nothing here emits, parses or
// strips a prefix, and adding that handling would silently corrupt a legitimate key.
//
// MIGRATION: the legacy caching layer is not reproduced client-side. The measured 116
// in-scope call sites into Library/Components/Providers/Caching/DataCache.vb (317 lines)
// included 11 from ModulePermissionController and 10 from TabPermissionController; note the
// real path, because the plan cites Library/Components/Shared/DataCache.vb, which does not
// exist in this repository (verified: the Providers/Caching path is present, the Shared one
// is absent). Caching is now a server concern, IMemoryCache behind ICacheService, where a
// single eviction serves every caller. NOTHING is cached, replayed or shared here: each
// method returns a cold observable that performs one request per subscription, and adding a
// replay operator or a retained response would put a second, quieter cache in front of the
// first and let a stale catalogue outlive the server's own eviction.

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
 * Both are built with `responseOf` rather than `envelopeOf` because the published signatures of
 * the methods below RETURN THE ENVELOPE, and validating must not change the shape they return.
 */
const PERMISSION_KEY_LIST_RESPONSE: Decoder<ApiResponse<readonly string[]>> = responseOf(
  arrayOf(decodeString),
);
const PERMISSION_RESPONSE: Decoder<ApiResponse<Permission>> = responseOf(decodePermission);

/**
 * Reads the permission catalogue: which permission keys this installation defines.
 *
 * TRANSPORT ONLY. Each method is one endpoint, one typed request, one observable returned
 * directly from the HTTP client. There is no validation, no derivation, no orchestration,
 * no multi-call sequencing, no retry, no caching and no reshaping of any response, because
 * the migration discipline restricts an Angular service to API communication. Everything a
 * screen needs beyond the bytes — selecting, sorting, grouping, holding — belongs to a
 * feature store, and every access decision belongs to the server.
 *
 * NEITHER METHOD SUBSCRIBES. Both hand back a cold observable, so no request is issued
 * until a caller asks for one and every caller controls its own teardown. Nothing here
 * blocks, converts to a promise or takes the first value on the caller's behalf.
 *
 * NO HEADER IS SET HERE. The correlation identifier, the bearer credential and the
 * translation of an RFC 7807 problem document are each applied once, in order, by the
 * request pipeline configured in `app.config.ts`: the correlation-id interceptor runs first
 * so the server's correlation middleware can consume the value and echo it back as a trace
 * identifier, the auth interceptor second, and the error interceptor last so that it
 * observes the final response after any credential refresh has been retried. Setting a
 * header here would duplicate one of those and the copies would eventually disagree.
 *
 * THE ADMINISTRATOR POLICY PROTECTS BOTH ENDPOINTS. The server applies
 * `PolicyNames.PortalAdministrator` at the controller, which is the declarative equivalent
 * of the legacy gate `PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)` that
 * protected the administration screens. A caller who presents no credential receives 401
 * and one who is signed in without that standing receives 403 — a distinction the legacy
 * redirect to the access-denied page could not express.
 *
 * @example
 * ```ts
 * private readonly permissions = inject(PermissionService);
 *
 * // The whole catalogue: every key this installation defines. Nothing is requested until
 * // the returned observable is consumed, which is the store's job rather than this one's.
 * const everyKey$ = this.permissions.list();
 *
 * // Narrowed to the keys declared under one module definition.
 * const forDefinition$ = this.permissions.list({ moduleDefinitionId: 42 });
 *
 * // The legacy code-and-key lookup, as one conjunctive query.
 * const oneKey$ = this.permissions.list({
 *   permissionCode: 'SYSTEM_MODULE_DEFINITION',
 *   permissionKey: 'EDIT',
 * });
 *
 * // One definition, as a whole record rather than a bare key.
 * const definition$ = this.permissions.getById(7);
 * ```
 */
@Injectable({ providedIn: 'root' })
export class PermissionService {
  /**
   * The HTTP client, resolved from the injector rather than declared as a constructor
   * parameter so that the class needs no constructor at all.
   *
   * The client itself is provided once, application-wide, by `provideHttpClient` in
   * `app.config.ts`. No component provides this service: `providedIn: 'root'` registers it
   * against the root injector, which keeps it tree-shakeable in a production build and
   * guarantees one instance rather than one per component that happened to list it.
   */
  private readonly http = inject(HttpClient);

  /**
   * Reads the catalogue, optionally narrowed by the filters the server accepts.
   *
   * THE PAYLOAD IS A BARE ARRAY OF KEY STRINGS, NOT A LIST OF RECORDS. That is the
   * server's contract rather than an accident of this method: a list of keys is precisely
   * what the vocabulary is, and it is what a client-side permission check tests against.
   * Full records come from {@link getById}, where a key alone would be ambiguous because
   * the same key is declared repeatedly across scopes. Do not attempt to read this payload
   * as an array of {@link Permission}; the two shapes differ on purpose.
   *
   * THE ELEMENT TYPE IS `string` AND NOT A UNION OF THE FOUR KNOWN KEYS. The column is
   * `varchar(20)` and the producer is open — an installation may carry a key seeded years
   * ago by a module this codebase has never seen — so typing the array as the narrower
   * union would statically promise that every element is one of four literals while it is
   * not, and a check that looked exhaustive to both a reader and the compiler would fail at
   * run time. Narrow an individual value with the guard in
   * `shared/directives/has-permission.directive.ts`, never with a cast.
   *
   * UNPAGED, DELIBERATELY. The catalogue is small, bounded reference data seeded by the
   * upgrade scripts, so the whole sequence is returned rather than a page of it. No page
   * coordinate, page size or ordering parameter is sent, and the response envelope's `meta`
   * is `null` because a scalar-or-sequence payload has no page to describe. Nothing matching
   * is 200 with an empty array, never a 404 and never a null array.
   *
   * @param filter The restrictions to apply, or omitted or `null` to read the whole
   * catalogue. The three members the server binds are `permissionCode` (free text, matched
   * as stored), `moduleDefinitionId` and `permissionKey`; they compose conjunctively because
   * each selects on a column of the catalogue row itself. Absence of any one member is
   * decided by the shared serialiser using an explicit undefined-or-null test and nothing
   * else, which is what lets `0`, `-1`, `false` and the empty string travel as the real
   * values they are; this method therefore performs no test of its own and adds no default.
   * The values are forwarded verbatim — never trimmed, case-folded, normalised, mapped onto
   * a policy name or checked against a closed set, since duplicating the server's own
   * validation rule here would produce two copies that eventually disagree.
   * @returns The success envelope carrying every matching key. 200 is the only success
   * status. A malformed filter answers 400, an absent or invalid credential 401, and a
   * caller without administrator standing 403; each of those arrives as a problem document
   * that the error interceptor translates, so no failure is handled here.
   */
  list(filter?: PermissionListFilter | null): Observable<ApiResponse<readonly string[]>> {
    // The URL arrives fully composed, already carrying the configured API base and the
    // version segment, and is passed straight through. Prefixing it again here would
    // produce a doubled base that type-checks, bundles and deploys without complaint and
    // then answers 404 on every call — a defect no step in the toolchain detects.
    //
    // The query string is built exclusively by the shared serialiser, which is the single
    // owner of parameter serialisation in this application. Assembling one here — by
    // concatenation, by hand-written separators or by a URL-search-parameter object — would
    // be a second implementation of a rule that has already been measured against the
    // server's binder.
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
   * Answers with the whole record rather than a bare key, because a key on its own could
   * not say which scope code or module definition it was declared under — and the same key
   * is declared repeatedly across scopes, so the key alone would identify nothing. This
   * reproduces `PermissionController.GetPermission(permissionID)`
   * (`Library/Components/Security/Permissions/PermissionController.vb:L30`), whose legacy
   * form could return a null object; the target reports absence as a 404 instead, so a
   * successful response always carries a definition.
   *
   * The catalogue carries no portal column, so this read is installation-wide exactly as
   * the listing is, and the administrator policy protects it just the same.
   *
   * @param permissionId Identifier of the definition wanted, interpolated exactly as
   * supplied. NO BOUND IS IMPOSED ON IT, here or in the URL builder, and none may be added.
   * An unknown identifier is reported as absent by the read itself, which is a better
   * answer than any local test could give, and a local test is actively unsafe in this
   * schema: the legacy marker for a missing integer is `-1`
   * (`Library/Components/Shared/Null.vb:L41-L45`, whose body is `Return -1`), yet the portal
   * table is declared `IDENTITY (-1, 1)` so `-1` is a real portal key, and the role, page
   * and module tables all seed at `0` so `0` is a real key there too. One value therefore
   * means both "the first row" and "no row" depending on context this method does not have.
   * Accordingly nothing here subjects the identifier to a truthiness test, a positive or
   * non-negative comparison, a comparison against the marker, or a coalescing default.
   * @returns The success envelope carrying the definition. 200 on success; 400 if the
   * identifier cannot be bound, 401 without a valid credential, 403 without administrator
   * standing, and 404 when no definition bears that identifier.
   */
  getById(permissionId: number): Observable<ApiResponse<Permission>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.permissions.byId(permissionId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PERMISSION_RESPONSE, body)));
  }
}
