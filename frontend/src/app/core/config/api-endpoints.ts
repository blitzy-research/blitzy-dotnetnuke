import { environment } from '../../../environments/environment';

/**
 * The single place REST route templates are declared for this application.
 *
 * This module is a DECLARATION module. It holds constants and pure functions that
 * build strings, and deliberately nothing else: no HTTP client, no injectable, no
 * reactive state, no side effect and no business rule. Feature services under
 * `core/services/` decide WHEN to call the API and what to do with the answer; this
 * module decides only WHERE the API is. Keeping the two apart is what makes a route
 * change a one-file edit instead of a search across the code base.
 *
 * ---------------------------------------------------------------------------
 * THREE INVARIANTS. Each has a specific failure mode that no compiler catches.
 *
 * 1. Every path is composed from `environment.apiBaseUrl`, never from a hard-coded
 *    `/api/v1` literal. The base is relative in a production build, and that is a
 *    deployment requirement rather than a preference — see the note on
 *    {@link apiUrl}. Reading it here rather than inlining it is also what puts the
 *    configured value into the emitted bundle at all: the ahead-of-time compiler
 *    walks the import graph from `src/main.ts`, and this module is the only member
 *    of the reachable graph that consumes the environment module.
 *
 * 2. No template contains a query string — not a separator character, not a
 *    delimiter, not one encoded parameter. Query values (paging coordinates,
 *    filters, sort keys) belong to `core/utils/http-params.util.ts`, which builds
 *    them into request parameters so that encoding happens once, correctly, in one
 *    place. A template that concatenated its own filter string would bypass that
 *    and re-introduce the legacy habit of assembling URLs by hand: see
 *    `Website/admin/Portal/Portals.ascx.vb` L215-L225, which built `filter=` and
 *    `currentpage=` fragments with string addition.
 *
 * 3. Identifiers are interpolated exactly as supplied. Never guarded, never
 *    defaulted, never coalesced — see the sentinel note on {@link PortalScope}.
 * ---------------------------------------------------------------------------
 *
 * The route surface below was taken from the API's own controllers rather than from
 * a written summary of them, because a template that disagrees with its controller
 * fails as a run-time 404 that nothing in the build detects. Two consequences of
 * that reading are worth stating up front, because both contradict what a reader
 * might reasonably expect:
 *
 * - Most collections are addressed UNDER a portal. Modules and accounts have no
 *   unscoped form at all; roles, role groups and profile definitions have both an
 *   explicitly scoped form and a form whose tenant the API resolves from the request
 *   host. Both forms are declared here, in separate groups, so a caller picks one on
 *   purpose instead of guessing.
 * - The versioned prefix is mandatory. The API does not assume a default version
 *   when none is supplied, so a request that omits the prefix does not fall back —
 *   it fails to route. `environment.apiBaseUrl` carries the prefix, which is the
 *   second reason invariant 1 exists.
 */

/**
 * The complete vocabulary of literal path segments, in kebab-case.
 *
 * Every template composes its path from these members rather than from inline
 * strings. That converts the most likely defect in a file of this kind — a
 * mistyped or inconsistently cased segment — from a run-time 404 into a compile
 * error, because a property that is not declared here does not exist. It also
 * guarantees that a segment used by several templates is spelled one way.
 *
 * Not exported: the assembled templates are the contract, and a caller that
 * reached for a raw segment would be building its own URLs, which is exactly what
 * this module exists to prevent.
 */
const SEGMENT = {
  /** Alias sub-collection of one portal. */
  aliases: 'aliases',

  /** Approval state of one account. */
  approval: 'approval',

  /** Authentication resource. */
  auth: 'auth',

  /** Installed-package grouping above a module definition. */
  desktopModules: 'desktop-modules',

  /** Content export of one module placement. */
  export: 'export',

  /** Content import into one portal. */
  import: 'import',

  /** Credential exchange. */
  login: 'login',

  /** Refresh-token revocation. */
  logout: 'logout',

  /** The signed-in identity. */
  me: 'me',

  /** Portal-wide account policy. */
  membershipSettings: 'membership-settings',

  /** Module definition catalogue. */
  moduleDefinitions: 'module-definitions',

  /** Module placements within one portal. */
  modules: 'modules',

  /** Credential change performed by the account holder. */
  password: 'password',

  /** Credential reset performed by an administrator. */
  passwordReset: 'password-reset',

  /** Permission catalogue. */
  permissions: 'permissions',

  /** Host-level alias collection, spanning every portal. */
  portalAliases: 'portal-aliases',

  /** Portal collection. */
  portals: 'portals',

  /** Profile values of one account. */
  profile: 'profile',

  /** Profile property definitions of one portal. */
  profileDefinitions: 'profile-definitions',

  /** Access-token renewal. */
  refresh: 'refresh',

  /** Flag obliging an account to change its credential at next sign-in. */
  requirePasswordChange: 'require-password-change',

  /** Role group collection. */
  roleGroups: 'role-groups',

  /** Role collection. */
  roles: 'roles',

  /** Configuration projection of one portal. */
  settings: 'settings',

  /** Page collection. */
  tabs: 'tabs',

  /** Release of a locked-out account. */
  unlock: 'unlock',

  /** Account collection. */
  users: 'users',
} as const;

/**
 * Joins the configured API base with a relative path.
 *
 * Collapses the boundary between the two so that a base with a trailing slash and
 * a path with a leading slash cannot produce a doubled separator — a URL that
 * most servers reject or, worse, route differently.
 *
 * MIGRATION: the base this prepends is RELATIVE in a production build, and must
 * stay that way. The legacy application was same-origin by construction, because
 * IIS served the pages and handled their postbacks; this split stack preserves that
 * property through a reverse proxy instead, which forwards `/api/` to the API
 * service on the very origin that served the application. An absolute base — a
 * compose service name, or a developer's own host — type-checks, bundles and
 * deploys without complaint, leaves both containers reporting healthy, and still
 * breaks every call the moment a browser makes one, because the service name
 * resolves only inside the container network and a reachable absolute host would
 * bypass the proxy and turn each call into a cross-origin request the API's named
 * policy is not written to admit. Nothing in the toolchain detects it. The value
 * itself lives in the environment module, which documents the same hazard where a
 * future editor will meet it.
 *
 * @param path A path relative to the API base, with or without a leading slash.
 * @returns The absolute-or-root-relative URL to request.
 */
export function apiUrl(path: string): string {
  const base = environment.apiBaseUrl.replace(/\/+$/, '');
  const suffix = path.replace(/^\/+/, '');

  return suffix.length === 0 ? base : `${base}/${suffix}`;
}

/**
 * A portal identifier, naming the tenant a request is scoped to.
 *
 * MIGRATION: `portalId` may legitimately be `0` OR `-1`, and no template may treat
 * either as absent. Two facts collide in the legacy schema. `Portals.PortalID` is
 * declared `IDENTITY (-1, 1)`, so the first portal ever created carries `-1` and the
 * second carries `0`; and `Library/Components/Shared/Null.vb` simultaneously defines
 * `NullInteger = -1` as the marker for a missing integer. The same value therefore
 * means both "the first portal" and "no portal", depending only on context that a
 * URL builder does not have. `Website/admin/Modules/Import.ascx.vb` L51 shows the
 * hazard in the legacy source itself — `Private Shadows ModuleId As Integer = -1`,
 * an identifier field initialised to the null marker — and the seeds for roles,
 * pages and module placements start at `0` for the same reason.
 *
 * Consequently every template in this module interpolates the number it was given,
 * unchanged. No identifier is subjected to a truthiness test, a positive-value or
 * non-negative test, a comparison against the sentinel, a null-coalescing default or
 * a logical-or default anywhere in this file, and none may be added: every one of
 * those would silently rewrite a request for a real row into a request for a
 * different one, or drop it. Not one template function contains a branch at all —
 * each is a single interpolation. Deciding whether an identifier is known is the
 * caller's business, and the caller has the context to decide it.
 */
export interface PortalScope {
  /** The portal the request is scoped to. Interpolated exactly as supplied. */
  readonly portalId: number;
}

/**
 * One alias of one portal.
 *
 * MIGRATION: the child segment is `portalAliasId`. The legacy screen addressed the
 * same row through the query-string key `paid`
 * (`Website/admin/Portal/EditPortalAlias.ascx.vb` L57); that abbreviation is not
 * carried forward, because a path segment is read far more often than it is typed.
 */
export interface PortalAliasScope extends PortalScope {
  /** The alias within the portal. */
  readonly portalAliasId: number;
}

/** One module placement within one portal. */
export interface ModuleScope extends PortalScope {
  /** The module placement within the portal. */
  readonly moduleId: number;
}

/** One account within one portal. */
export interface UserScope extends PortalScope {
  /** The account within the portal. */
  readonly userId: number;
}

/** One role within one explicitly named portal. */
export interface PortalRoleScope extends PortalScope {
  /** The role within the portal. */
  readonly roleId: number;
}

/** One account's membership of one role, within the tenant the API resolves. */
export interface RoleMemberScope {
  /** The role the membership belongs to. */
  readonly roleId: number;

  /** The account holding the membership. */
  readonly userId: number;
}

/** One account's membership of one role, within one explicitly named portal. */
export interface PortalRoleMemberScope extends PortalRoleScope {
  /** The account holding the membership. */
  readonly userId: number;
}

/** One role group within one explicitly named portal. */
export interface PortalRoleGroupScope extends PortalScope {
  /** The role group within the portal. */
  readonly roleGroupId: number;
}

/** One profile property definition within one explicitly named portal. */
export interface ProfileDefinitionScope extends PortalScope {
  /** The property definition within the portal. */
  readonly propertyDefinitionId: number;
}

/**
 * The authentication endpoints, closed at four.
 *
 * Login, refresh and logout are anonymous on the server: they authenticate the
 * credentials or the refresh token they carry, not a bearer token. That is what
 * allows a refresh to succeed after the access token has already expired, and it is
 * why the authentication interceptor must leave them alone. The whole resource is
 * rate limited by originating address, and only this resource is — a rejected
 * request is answered `429` rather than being queued.
 *
 * Held as ready strings rather than as functions because none of the four takes a
 * path parameter, and because they are compared against outbound request URLs by
 * {@link isAnonymousAuthEndpoint}.
 *
 * MIGRATION: the surface stops at four, and two arguments the legacy sign-in
 * carried are gone. The CAPTCHA is deleted: the legacy page gated its whole
 * credential check on one
 * (`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb` L59 declares
 * `UseCaptcha`, L137-L142 shows the two rows it governed, and L162 reads
 * `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then`), and the
 * control that rendered it is out of scope. The address-partitioned rate limit on
 * this resource is the compensating defence against automated credential guessing,
 * and it applies to every caller rather than only to those the legacy configuration
 * happened to challenge. The literal `"DNN"` authentication-type argument is gone
 * too — L164 passed it to `ValidateUser` and L191 passed it again into the
 * authenticated event — because there is now exactly one credential path and a
 * discriminator with one possible value carries no information.
 *
 * MIGRATION: credential RETRIEVAL is not carried forward, and no endpoint here
 * offers it. The legacy application had a dedicated screen for it —
 * `Website/admin/Security/SendPassword.ascx.vb` L38 states its purpose as allowing
 * "a user to retrieve their password", L167 branches on
 * `MembershipProviderConfig.RequiresQuestionAndAnswer`, and L200 calls
 * `UserController.GetPassword(objUser, txtAnswer.Text)` — which was only possible
 * because credentials were stored reversibly. They are now stored as one-way
 * hashes, so retrieval is not merely withheld, it is impossible. A RESET is
 * supported instead, through the two account endpoints declared under
 * {@link API_ENDPOINTS}. There is deliberately no `forgot-password`,
 * `reset-password`, `send-password`, `register`, `verify`, `change-password`,
 * `external` or `providers` endpoint; in particular the verification code the
 * legacy sign-in read from its query string (`Login.ascx.vb` L109-L114) has no
 * successor route.
 */
export const AUTH_ENDPOINTS = {
  /** `POST` — exchanges credentials for a token pair. Anonymous. */
  login: apiUrl(`${SEGMENT.auth}/${SEGMENT.login}`),

  /** `POST` — exchanges a refresh token for a new pair, rotating it. Anonymous. */
  refresh: apiUrl(`${SEGMENT.auth}/${SEGMENT.refresh}`),

  /** `POST` — revokes a refresh token. Anonymous, and answers `204` regardless. */
  logout: apiUrl(`${SEGMENT.auth}/${SEGMENT.logout}`),

  /** `GET` — returns the signed-in identity. Requires a bearer token. */
  me: apiUrl(`${SEGMENT.auth}/${SEGMENT.me}`),
} as const;

/**
 * The endpoints an interceptor must never attach a bearer token to, and must never
 * attempt to recover with a refresh.
 *
 * `me` is deliberately absent: it is the one authentication endpoint that does
 * require a bearer token, so it takes one and a 401 from it is a genuine expiry
 * worth refreshing.
 */
export const ANONYMOUS_AUTH_ENDPOINTS: readonly string[] = Object.freeze([
  AUTH_ENDPOINTS.login,
  AUTH_ENDPOINTS.refresh,
  AUTH_ENDPOINTS.logout,
]);

/**
 * Whether a request URL addresses one of the anonymous authentication endpoints.
 *
 * Compares against the path portion only, so a URL that carries a query string or
 * arrives fully qualified still matches. The comparison is an exact path match
 * rather than a prefix test, because a prefix test on `auth/login` would also match
 * a hypothetical `auth/login-history` and silently stop sending its token.
 *
 * @param url The outbound request URL.
 * @returns True when the request must be sent without a bearer token.
 */
export function isAnonymousAuthEndpoint(url: string): boolean {
  const path = url.split('?')[0] ?? url;

  return ANONYMOUS_AUTH_ENDPOINTS.some((endpoint) => path === endpoint || path.endsWith(endpoint));
}

/**
 * Whether a request URL addresses this application's API at all.
 *
 * Used to keep credentials off requests that are not going to the API — a static
 * asset, a template, or a third-party URL. Attaching a bearer token to those would
 * disclose it to whoever serves them.
 *
 * A relative base makes the test a prefix comparison on the path. An absolute base
 * is also handled, because the configured value is compared as a substring of the
 * request URL rather than being assumed to be root-relative.
 *
 * @param url The outbound request URL.
 * @returns True when the request is addressed to the API.
 */
export function isApiRequest(url: string): boolean {
  const base = environment.apiBaseUrl.replace(/\/+$/, '');

  if (base.length === 0) {
    // A base that is empty or entirely slashes cannot distinguish an API call
    // from anything else, so nothing is treated as one. Reporting true here would
    // attach the token to every request the application makes, including requests
    // for static assets.
    return false;
  }

  return url.startsWith(base) || url.includes(`${base}/`);
}

/**
 * Every route template the application may address, grouped by resource.
 *
 * Frozen through `as const`, so the tree is readonly at every depth and an
 * accidental assignment does not compile. Each member is a function even where its
 * path takes no parameter, so that call sites read uniformly and so that adding a
 * parameter later is not a breaking change in shape. Templates that several verbs
 * share are declared once and documented with the verbs that use them: a resource
 * addressed by `GET`, `PUT` and `DELETE` has ONE identity, and duplicating the
 * string per verb would invite the three copies to drift apart.
 *
 * Multi-identifier templates take a NAMED object rather than positional numbers.
 * Two adjacent parameters of type `number` are interchangeable to the compiler, so
 * a positional signature cannot detect a caller that swaps a portal for a module;
 * a named member can, and it makes the call site legible without a comment.
 *
 * ---------------------------------------------------------------------------
 * WHAT IS ABSENT IS AS DELIBERATE AS WHAT IS PRESENT. The groups below are closed.
 * Do not add a template for a route that does not exist on a controller — a
 * plausible-looking one produces a 404 that surfaces only when a user reaches the
 * screen, and the migration notes for several of these omissions are recorded
 * inline so nobody restores them by accident.
 * ---------------------------------------------------------------------------
 */
export const API_ENDPOINTS = {
  /**
   * Authentication. The same four strings as {@link AUTH_ENDPOINTS}, reachable from
   * within the grouped tree so a caller need not know both names.
   */
  auth: AUTH_ENDPOINTS,

  /** Portals — the multi-tenant site containers. */
  portals: {
    /** `GET` the paged collection; `POST` to create. */
    collection: (): string => apiUrl(SEGMENT.portals),

    /** `GET`, `PUT` or `DELETE` one portal. */
    byId: (portalId: number): string => apiUrl(`${SEGMENT.portals}/${portalId}`),

    /**
     * `GET` the configuration projection of one portal.
     *
     * MIGRATION: READ-ONLY, and the absence of a writer is deliberate rather than an
     * omission. There is no portal-settings table in the legacy schema at all — the
     * projection is assembled from columns on the portal row itself — so there is no
     * key/value settings resource to expose, and the legacy site-setting keys that
     * had no column behind them are not carried forward. Portal fields are edited
     * through `PUT` on {@link API_ENDPOINTS.portals.byId}. Do not add a `PUT` here.
     */
    settings: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.settings}`),
  },

  /**
   * Portal aliases — the host names that resolve to a portal.
   *
   * Two shapes exist and both are declared, because they are protected differently:
   * the portal-scoped shape is available to an administrator of that portal, while
   * the host-level collection spans every tenant and is reserved to a host
   * administrator. Choosing the wrong one is a `403`, not a `404`.
   */
  portalAliases: {
    /** Aliases addressed beneath their owning portal. */
    forPortal: {
      /** `GET` the aliases of one portal; `POST` to add one. */
      collection: (portalId: number): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.aliases}`),

      /** `GET`, `PUT` or `DELETE` one alias of one portal. */
      byId: ({ portalId, portalAliasId }: PortalAliasScope): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.aliases}/${portalAliasId}`),
    },

    /** Aliases addressed across every portal. Host administrators only. */
    host: {
      /** `GET` every alias in the installation. */
      collection: (): string => apiUrl(SEGMENT.portalAliases),

      /** `GET`, `PUT` or `DELETE` one alias without naming its portal. */
      byId: (portalAliasId: number): string =>
        apiUrl(`${SEGMENT.portalAliases}/${portalAliasId}`),
    },
  },

  /**
   * Module placements. Always addressed beneath their portal — there is no unscoped
   * module collection, because a placement has no meaning outside a tenant.
   */
  modules: {
    /** `GET` the placements of one portal; `POST` to add one. */
    collection: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.modules}`),

    /**
     * `GET`, `PUT` or `DELETE` one placement.
     *
     * The delete is soft: the placement moves to the recycle bin rather than being
     * erased, which is why the module listing accepts a flag for including deleted
     * rows. Restoring one, and emptying the bin, are not exposed — see the recycle
     * bin note on {@link API_ENDPOINTS.tabs}.
     */
    byId: ({ portalId, moduleId }: ModuleScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.modules}/${moduleId}`),

    /** `GET` or `PUT` the per-placement settings. */
    settings: ({ portalId, moduleId }: ModuleScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.modules}/${moduleId}/${SEGMENT.settings}`),

    /**
     * `POST` to export one placement's content.
     *
     * MIGRATION: the exported document is returned IN THE RESPONSE BODY. The legacy
     * screen wrote it to a folder on the server that the operator chose from a drop
     * down — `Website/admin/Modules/Export.ascx.vb` L124-L125 composes a file name
     * and passes it with `cboFolders.SelectedItem.Value` to a routine whose portable
     * contract returns the document as a string, which was then persisted for the
     * operator to collect. There is no filesystem or upload endpoint in this API by
     * design, so the string the contract already produced is simply returned to the
     * caller, which hands it to the browser. Nothing is written on the server.
     */
    export: ({ portalId, moduleId }: ModuleScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.modules}/${moduleId}/${SEGMENT.export}`),

    /**
     * `POST` to import content into one portal.
     *
     * MIGRATION: the target module travels IN THE BODY, not in the path, so this
     * template takes only the portal. That mirrors the legacy screen, which received
     * its target as a request value rather than as part of its address —
     * `Website/admin/Modules/Import.ascx.vb` L67-L68 parses
     * `Request.QueryString("moduleid")` into the field declared at L51 — and it also
     * reflects that the import is validated as one payload: the document, its
     * declared type and the target are refused together, so splitting the target out
     * into the path would let a caller address a module the payload contradicts.
     */
    import: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.modules}/${SEGMENT.import}`),
  },

  /**
   * The module definition catalogue. READ-ONLY.
   *
   * MIGRATION: installing, packaging or removing a module is not exposed. The legacy
   * installer wrote packages to disk and reflected over the assemblies it found,
   * which is a filesystem and late-binding surface this API deliberately does not
   * have; the catalogue is therefore reference data that the upgrade scripts seed and
   * this application only reads.
   */
  moduleDefinitions: {
    /** `GET` every definition available to the resolved tenant. Unpaged. */
    collection: (): string => apiUrl(SEGMENT.moduleDefinitions),

    /** `GET` one definition. */
    byId: (moduleDefinitionId: number): string =>
      apiUrl(`${SEGMENT.moduleDefinitions}/${moduleDefinitionId}`),

    /**
     * `GET` the definitions belonging to one installed package.
     *
     * The package is a PATH segment. The tenant is not a parameter here at all — the
     * API resolves it from the request — so this template takes one identifier only.
     */
    forDesktopModule: (desktopModuleId: number): string =>
      apiUrl(`${SEGMENT.moduleDefinitions}/${SEGMENT.desktopModules}/${desktopModuleId}`),
  },

  /**
   * Accounts. Always addressed beneath their portal: an account's membership,
   * approval state and profile are all portal-scoped facts, so there is no unscoped
   * account collection.
   *
   * MIGRATION: no bulk operation is exposed, on this resource or any other. The
   * legacy administration offered several — `Website/admin/Users/Users.ascx.vb` L326
   * declares `DeleteUnAuthorizedUsers()`, and `Website/admin/Portal/Portals.ascx.vb`
   * L189-L191 declares `DeleteExpiredPortals(GetAbsoluteServerPath(Request))` behind
   * the expired-portal listing at L139 — each of which destroyed an unbounded number
   * of rows from a single click, with no per-row confirmation and no way to review
   * the set first. Removal is per-resource here, so the caller names what it is
   * removing. Nor is the online-account view carried forward: L262 read
   * `UserController.GetOnlineUsers(UsersPortalId)`, which was backed by session
   * tracking and a scheduled purge job that this migration does not reproduce.
   */
  users: {
    /** `GET` the paged accounts of one portal; `POST` to create one. */
    collection: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}`),

    /** `GET`, `PUT` or `DELETE` one account. */
    byId: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}`),

    /** `GET` or `PUT` the profile values of one account. */
    profile: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.profile}`),

    /**
     * `POST` a credential change made by the account holder, who supplies the
     * current credential alongside the new one.
     */
    password: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.password}`),

    /**
     * `POST` a credential reset performed by an administrator.
     *
     * Separate from the holder's own change because the two differ in what they
     * require and in who may call them: a reset proves nothing about the old
     * credential and is therefore restricted to a portal administrator. Neither
     * endpoint discloses a credential — see the retrieval note on
     * {@link AUTH_ENDPOINTS}.
     */
    passwordReset: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.passwordReset}`),

    /** `POST` to release an account locked out by failed sign-in attempts. */
    unlock: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.unlock}`),

    /**
     * `PUT` the approval state of one account.
     *
     * One endpoint carrying the state, rather than a pair of verb-shaped routes for
     * granting and withdrawing it. The state being set is a query parameter that
     * `core/utils/http-params.util.ts` builds, so it does not appear here.
     */
    approval: ({ portalId, userId }: UserScope): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.approval}`),

    /** `POST` to oblige an account to change its credential at next sign-in. */
    requirePasswordChange: ({ portalId, userId }: UserScope): string =>
      apiUrl(
        `${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.requirePasswordChange}`,
      ),

    /**
     * `GET` or `PUT` the portal-wide account policy.
     *
     * Note the shape: this is a resource OF THE PORTAL, not of the account
     * collection, because the policy governs every account in the tenant and
     * belongs to none of them. It is addressed as a sibling of the account
     * collection rather than beneath it.
     */
    membershipSettings: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.membershipSettings}`),
  },

  /**
   * Profile property definitions — the fields an account's profile may carry.
   *
   * MIGRATION: ordering is a FIELD, set through `PUT` on one definition. There is no
   * move-up or move-down endpoint, because the legacy pair was not an operation on
   * one row at all: `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L187 read
   * the neighbouring definition and swapped the two view orders, and L519-L521 bound
   * that swap to the two image columns. Modelling it as a route would have made a
   * two-row write look like a one-row action and would have needed a second call to
   * discover the neighbour. The caller sets the order it wants.
   */
  profileDefinitions: {
    /** Definitions of the tenant the API resolves from the request. */
    forCurrentPortal: {
      /** `GET` the definitions; `POST` to create one. Unpaged. */
      collection: (): string => apiUrl(SEGMENT.profileDefinitions),

      /** `GET`, `PUT` or `DELETE` one definition. */
      byId: (propertyDefinitionId: number): string =>
        apiUrl(`${SEGMENT.profileDefinitions}/${propertyDefinitionId}`),
    },

    /** Definitions of an explicitly named portal. */
    forPortal: {
      /** `GET` the definitions of one portal; `POST` to create one. */
      collection: (portalId: number): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.profileDefinitions}`),

      /** `GET`, `PUT` or `DELETE` one definition of one portal. */
      byId: ({ portalId, propertyDefinitionId }: ProfileDefinitionScope): string =>
        apiUrl(
          `${SEGMENT.portals}/${portalId}/${SEGMENT.profileDefinitions}/${propertyDefinitionId}`,
        ),
    },
  },

  /**
   * Roles, and the memberships that connect them to accounts.
   *
   * MIGRATION: subscribing to and unsubscribing from a paid service is expressed as
   * role membership, not as a service resource of its own. The legacy member-services
   * screen (`Website/admin/Users/MemberServices.ascx.vb`) presented subscriptions as
   * their own concept, but each one was a row joining an account to a role with an
   * effective and an expiry date — which is exactly what the membership endpoints
   * below write. A parallel `services` or `subscriptions` route would have been a
   * second name for one table.
   */
  roles: {
    /** Roles of the tenant the API resolves from the request. */
    forCurrentPortal: {
      /** `GET` the paged roles; `POST` to create one. */
      collection: (): string => apiUrl(SEGMENT.roles),

      /** `GET`, `PUT` or `DELETE` one role. */
      byId: (roleId: number): string => apiUrl(`${SEGMENT.roles}/${roleId}`),

      /** `GET` the accounts holding one role; `POST` to add a membership. */
      members: (roleId: number): string => apiUrl(`${SEGMENT.roles}/${roleId}/${SEGMENT.users}`),

      /** `DELETE` one account's membership of one role. */
      member: ({ roleId, userId }: RoleMemberScope): string =>
        apiUrl(`${SEGMENT.roles}/${roleId}/${SEGMENT.users}/${userId}`),

      /** `GET` the roles held by one account. */
      forUser: (userId: number): string => apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.roles}`),
    },

    /** Roles of an explicitly named portal. */
    forPortal: {
      /** `GET` the paged roles of one portal; `POST` to create one. */
      collection: (portalId: number): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.roles}`),

      /** `GET`, `PUT` or `DELETE` one role of one portal. */
      byId: ({ portalId, roleId }: PortalRoleScope): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.roles}/${roleId}`),

      /** `GET` the accounts holding one role; `POST` to add a membership. */
      members: ({ portalId, roleId }: PortalRoleScope): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.roles}/${roleId}/${SEGMENT.users}`),

      /** `DELETE` one account's membership of one role. */
      member: ({ portalId, roleId, userId }: PortalRoleMemberScope): string =>
        apiUrl(
          `${SEGMENT.portals}/${portalId}/${SEGMENT.roles}/${roleId}/${SEGMENT.users}/${userId}`,
        ),

      /** `GET` the roles held by one account of one portal. */
      forUser: ({ portalId, userId }: UserScope): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.users}/${userId}/${SEGMENT.roles}`),
    },
  },

  /** Role groups — the optional grouping above a role. */
  roleGroups: {
    /** Groups of the tenant the API resolves from the request. */
    forCurrentPortal: {
      /** `GET` the groups; `POST` to create one. Unpaged. */
      collection: (): string => apiUrl(SEGMENT.roleGroups),

      /**
       * `GET`, `PUT` or `DELETE` one group.
       *
       * The delete refuses a group that still contains roles, rather than cascading:
       * removing a grouping must not silently remove the permissions grouped by it.
       */
      byId: (roleGroupId: number): string => apiUrl(`${SEGMENT.roleGroups}/${roleGroupId}`),
    },

    /** Groups of an explicitly named portal. */
    forPortal: {
      /** `GET` the groups of one portal; `POST` to create one. */
      collection: (portalId: number): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.roleGroups}`),

      /** `GET`, `PUT` or `DELETE` one group of one portal. */
      byId: ({ portalId, roleGroupId }: PortalRoleGroupScope): string =>
        apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.roleGroups}/${roleGroupId}`),
    },
  },

  /**
   * Pages. THREE endpoints, and no more — the narrowest surface in this file.
   *
   * The two templates below deliberately have DIFFERENT SHAPES rather than sharing a
   * prefix: the listing hangs beneath its portal, while an individual page is
   * addressed at the root because its identifier is unique across the installation.
   * A single prefixed group would have got the listing wrong.
   *
   * Pages are consumed here as a LOOKUP — the module screens need to know which page
   * a placement sits on — rather than as a feature of their own, which is why there
   * is no page-management area in this application.
   *
   * MIGRATION: creating, deleting, reordering and moving a page are all absent, and
   * that closes a genuinely large legacy surface. `Website/admin/Tabs/Tabs.ascx.vb`
   * L70 declares `Private Sub DeleteTab()` and L73 calls
   * `TabController.DeleteTab(objTab.TabID, PortalSettings, UserId)`; L214 declares
   * `UpDown_Click ... Handles cmdDown.Click, cmdUp.Click`, and the four ordering
   * calls at L188, L190, L223 and L225 drive `UpdatePortalTabOrder` with the
   * one-step deltas that the arrow buttons produced. Reordering a page hierarchy by
   * relative nudge depends on the whole tree being rendered in one postback, which
   * is the presentation model this migration replaces; and page CREATION carries the
   * skin and container selection that is explicitly out of scope, so the update
   * endpoint accepts neither.
   *
   * MIGRATION: page export, page import and the recycle bin are dropped with it.
   * `Website/admin/Tabs/Export.ascx.vb` and `Website/admin/Tabs/Import.ascx.vb`
   * moved page definitions through server-side folders, which this API has no
   * filesystem surface for, and `Website/admin/Tabs/RecycleBin.ascx.vb` restored and
   * purged soft-deleted pages — an operation with nothing to act on here, because no
   * endpoint deletes a page in the first place. Module placements are still soft
   * deleted, so their rows survive; only the screen that managed them is absent.
   */
  tabs: {
    /** `GET` the pages of one portal. */
    forPortal: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.tabs}`),

    /** `GET` or `PUT` one page. Addressed at the root, without its portal. */
    byId: (tabId: number): string => apiUrl(`${SEGMENT.tabs}/${tabId}`),
  },

  /**
   * The permission catalogue. READ-ONLY, and unpaged — it is small, bounded
   * reference data seeded by the upgrade scripts.
   *
   * MIGRATION: nothing here mutates a permission. The legacy application edited
   * grants through a permission grid, per module and per page, and that grid is the
   * surface this migration does not replace; what remains is the catalogue of
   * permission definitions, which lets a caller discover what the keys mean. Grants
   * themselves are enforced server-side on every request, and the client's own
   * permission directive is a convenience for hiding controls, never the enforcement.
   */
  permissions: {
    /** `GET` the catalogue. Filters are query parameters and so are not declared here. */
    collection: (): string => apiUrl(SEGMENT.permissions),

    /** `GET` one permission definition. */
    byId: (permissionId: number): string => apiUrl(`${SEGMENT.permissions}/${permissionId}`),

    /** `GET` the permission definitions that apply to one module placement. */
    forModule: (moduleId: number): string =>
      apiUrl(`${SEGMENT.permissions}/${SEGMENT.modules}/${moduleId}`),

    /** `GET` the permission definitions that apply to one page. */
    forTab: (tabId: number): string => apiUrl(`${SEGMENT.permissions}/${SEGMENT.tabs}/${tabId}`),
  },
} as const;
