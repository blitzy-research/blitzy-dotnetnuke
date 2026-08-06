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
 * - Most resources use one canonical FLAT family. Their tenant is resolved from the
 *   request host and the authenticated context; adding a portal segment would create
 *   a second public identity for the same operation. Only three operations are
 *   nested, and each is nested because it is a property OF a named portal rather
 *   than a tenant-scoped view of something else.
 * - The versioned prefix is mandatory. The API does not assume a default version
 *   when none is supplied, so a request that omits the prefix does not fall back —
 *   it fails to route. `environment.apiBaseUrl` carries the prefix, which is the
 *   second reason invariant 1 exists.
 *
 * ---------------------------------------------------------------------------
 * WHY FLAT, WITH THE AUTHORITY QUOTED RATHER THAN ASSERTED.
 *
 * This shape is not a client-side preference and it is not inferred from the
 * controllers agreeing with these templates — two sides can agree and both be wrong
 * against the specification. The frozen action plan names each family explicitly in
 * its API-layer file mapping (§0.5.1.4), and the wording below is its wording:
 *
 * | Family                     | Action plan §0.5.1.4                                       |
 * | -------------------------- | ---------------------------------------------------------- |
 * | `portals`                  | "Attribute-routed to `/api/v1/portals`"                    |
 * | `modules`                  | "`POST /api/v1/modules/{id}/export` and `POST /api/v1/modules/import`" |
 * | `module-definitions`       | "Read-only lookup surface at `/api/v1/module-definitions`" |
 * | `profile-definitions`      | "Lookup surface at `/api/v1/profile-definitions`"          |
 * | `permissions`              | "Read-only `/api/v1/permissions` catalogue"                |
 * | `roles`, `role-groups`     | "Role membership assignment becomes `POST`/`DELETE` on `/api/v1/roles/{id}/users`" |
 * | `users`                    | "one resource controller with profile, password and settings sub-resources" |
 * | `tabs`                     | "`GET`/`PUT /api/v1/tabs/{id}` only"                       |
 * | `auth`                     | "`POST /api/v1/auth/login`, `refresh`, `logout` and `GET /api/v1/auth/me`" |
 *
 * THE THREE NESTED OPERATIONS, and their authority — note that there are three and
 * not two, which is the count a reader is most likely to get wrong:
 *
 * - `portals/{portalId}/aliases` — "Nested under `/api/v1/portals/{id}/aliases`". An
 *   alias has no identity apart from the portal it resolves to.
 * - `portals/{portalId}/tabs` — "`GET /api/v1/portals/{id}/tabs`", and the same
 *   sentence fixes the sibling page resource as flat, so the page family is
 *   deliberately split across both shapes: the LISTING belongs to a portal, an
 *   individual page is addressed on its own.
 * - `portals/{portalId}/settings` — the settings projection is a representation of
 *   the portal itself rather than a separate resource, which is why it lives on the
 *   portal controller and carries no family of its own here.
 *
 * A template must never be "tidied" from one shape into the other. Both directions
 * are equally wrong: nesting a flat family invents a second identity the API does not
 * serve, and flattening a nested one addresses a resource that has no standalone
 * identity. Either produces a 404 no build step detects, and either departs from a
 * specification that is frozen and is not reinterpreted from this file.
 * ---------------------------------------------------------------------------
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

/** One account's membership of one role, within the tenant the API resolves. */
export interface RoleMemberScope {
  /** The role the membership belongs to. */
  readonly roleId: number;

  /** The account holding the membership. */
  readonly userId: number;
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

// ---------------------------------------------------------------------------
// URL RESOLUTION — the shared basis for both credential tests below
//
// MIGRATION: BOTH TESTS BELOW RESOLVE URLS RATHER THAN COMPARING STRINGS, AND
//   THAT IS A SECURITY PROPERTY RATHER THAN A TIDINESS ONE. A textual prefix or
//   substring comparison against the configured base cannot tell WHERE a URL
//   points, only what it happens to spell. With the production base `/api/v1`,
//   `https://evil.example/api/v1/exfiltrate` starts with nothing relevant but
//   contains the base; `//evil.example/api/v1/exfiltrate` is protocol-relative and
//   resolves to a foreign host while reading as a path; and
//   `https://evil.example/x?next=/api/v1/users` carries the base inside a query
//   parameter. All three are foreign origins, and the only consumer of these tests
//   attaches `Authorization: Bearer <token>` to whatever they admit — so a string
//   test hands the session token to whoever serves that host. Resolving each URL
//   against the document base and comparing the resulting ORIGIN is the only test
//   that answers the question actually being asked.
//
// MIGRATION: THE PATH COMPARISON IS SEGMENT-BOUNDED, not a prefix test. `/api/v10`
//   shares a textual prefix with `/api/v1` and is a different API version; a prefix
//   test admits it. Only an exact path match, or a match followed by a `/`
//   separator, identifies a URL as being inside the configured base.
//
// Both helpers FAIL CLOSED: anything that cannot be resolved, or that resolves to
// an opaque origin, is reported as not-an-API-request. The consequence of a false
// negative is a request sent without a token, which the server refuses visibly. The
// consequence of a false positive is a leaked credential, which nothing refuses.
// ---------------------------------------------------------------------------

/**
 * Resolves a URL against the document's base, or null when it cannot be resolved.
 *
 * `document.baseURI` rather than the current location, because the application
 * declares `<base href="/">` and the framework composes relative request URLs
 * against that same base. Using the location instead would resolve a root-relative
 * request differently once the router had navigated into a nested path.
 *
 * @param url An absolute, protocol-relative or relative URL.
 * @returns The resolved URL, or null when the value is not a URL at all.
 */
function resolveAgainstDocument(url: string): URL | null {
  try {
    return new URL(url, document.baseURI);
  } catch {
    // Not a URL in any form the platform recognises. Deliberately swallowed: the
    // caller's contract is a boolean, and an unresolvable value is simply not an
    // API request.
    return null;
  }
}

/**
 * The configured API base as an absolute URL, or null when it cannot discriminate.
 *
 * Three configurations yield null, and all three for the same reason the original
 * empty-base guard gave: a base that matches everything cannot distinguish an API
 * call from a static asset, and reporting a match would attach the session token to
 * every request the application makes.
 *
 * - an empty base, or one made entirely of slashes;
 * - a base whose resolved path is the site root, which is the absolute spelling of
 *   the same degenerate case — `https://host` and `/` both claim every path;
 * - a base that resolves to an opaque origin, for which origin equality is not a
 *   meaningful test because every opaque origin compares equal to every other.
 *
 * @returns The resolved base, or null when no safe comparison is possible.
 */
function resolveApiBase(): URL | null {
  const configured = environment.apiBaseUrl.replace(/\/+$/, '');

  if (configured.length === 0) {
    return null;
  }

  const base = resolveAgainstDocument(configured);

  if (base === null || base.origin === 'null' || base.pathname === '/') {
    return null;
  }

  return base;
}

/**
 * Whether a resolved path lies at or beneath a base path.
 *
 * @param path The resolved path of the candidate URL.
 * @param basePath The resolved path of the configured API base.
 * @returns True when the path is the base itself or a descendant of it.
 */
function isWithinBasePath(path: string, basePath: string): boolean {
  return path === basePath || path.startsWith(`${basePath}/`);
}

/**
 * Whether a request URL addresses this application's API at all.
 *
 * Used to keep credentials off requests that are not going to the API — a static
 * asset, a template, or a third-party URL. Attaching a bearer token to those would
 * disclose it to whoever serves them.
 *
 * The URL and the configured base are both resolved to absolute form and then
 * compared on origin and on path, so the answer describes where the request goes
 * rather than how it is spelled. See the note above the helpers for the specific
 * inputs a textual comparison admitted.
 *
 * Note the consequence of the two configured bases. The production base is
 * relative, so it resolves to the document's own origin and every same-origin API
 * path matches — which is exactly the same-origin arrangement the reverse proxy
 * serves. The development base is absolute, so a root-relative request URL resolves
 * to the document origin, does NOT match the configured API origin, and is refused;
 * that is correct, because in development every request URL is composed from the
 * absolute base by this very module, so a root-relative one did not come from here.
 *
 * @param url The outbound request URL.
 * @returns True when the request is addressed to the API.
 */
export function isApiRequest(url: string): boolean {
  const base = resolveApiBase();

  if (base === null) {
    return false;
  }

  const candidate = resolveAgainstDocument(url);

  if (candidate === null || candidate.origin !== base.origin) {
    return false;
  }

  return isWithinBasePath(candidate.pathname, base.pathname);
}

/**
 * Whether a request URL addresses one of the anonymous authentication endpoints.
 *
 * Compares resolved paths, so a URL that carries a query string or arrives fully
 * qualified still matches. The comparison is an exact path match rather than a
 * prefix or suffix test, because a prefix test on `auth/login` would also match a
 * hypothetical `auth/login-history` and silently stop sending its token, and a
 * suffix test would be satisfied by any host willing to serve a path ending in
 * `/auth/login`.
 *
 * Gated on {@link isApiRequest} first, so a foreign origin can never be classified
 * as one of this application's anonymous endpoints however its path is spelled.
 *
 * @param url The outbound request URL.
 * @returns True when the request must be sent without a bearer token.
 */
export function isAnonymousAuthEndpoint(url: string): boolean {
  if (!isApiRequest(url)) {
    return false;
  }

  const candidate = resolveAgainstDocument(url);

  if (candidate === null) {
    return false;
  }

  return ANONYMOUS_AUTH_ENDPOINTS.some((endpoint) => {
    const declared = resolveAgainstDocument(endpoint);

    return declared !== null && declared.pathname === candidate.pathname;
  });
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
     * `GET` the configuration projection of one portal; `PUT` its complete editable settings state.
     *
     * MIGRATION: this is a column projection and replacement, not a key/value settings
     * table. The legacy schema defines no portal-settings table, and the site-setting
     * keys with no portal-column counterpart remain excluded. GET and PUT intentionally
     * share this URL because they are the read and write representations of the same
     * settings screen required by the frozen API contract.
     */
    settings: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.settings}`),
  },

  /**
   * Portal aliases — the host names that resolve to a portal.
   *
   * The portal-owned family is the only public shape. The legacy host-wide repair
   * seam is intentionally not published: it would create a second identity for the
   * same alias rows outside the AAP-authorized portal resource.
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
  },

  /**
   * Module placements. The route family is flat; the API resolves the tenant from
   * the request host and authenticated context before any service call.
   */
  modules: {
    /** `GET` the resolved tenant's placements; `POST` to add one. */
    collection: (): string => apiUrl(SEGMENT.modules),

    /**
     * `GET`, `PUT` or `DELETE` one placement.
     *
     * The delete is soft: the placement moves to the recycle bin rather than being
     * erased, which is why the module listing accepts a flag for including deleted
     * rows. Restoring one, and emptying the bin, are not exposed — see the recycle
     * bin note on {@link API_ENDPOINTS.tabs}.
     */
    byId: (moduleId: number): string => apiUrl(`${SEGMENT.modules}/${moduleId}`),

    /** `GET` or `PUT` the per-placement settings. */
    settings: (moduleId: number): string =>
      apiUrl(`${SEGMENT.modules}/${moduleId}/${SEGMENT.settings}`),

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
    export: (moduleId: number): string =>
      apiUrl(`${SEGMENT.modules}/${moduleId}/${SEGMENT.export}`),

    /**
     * `POST` to import content into the resolved tenant.
     *
     * MIGRATION: the target module travels IN THE BODY, not in the path, so this
     * template takes no identifier. That mirrors the legacy screen, which received
     * its target as a request value rather than as part of its address —
     * `Website/admin/Modules/Import.ascx.vb` L67-L68 parses
     * `Request.QueryString("moduleid")` into the field declared at L51 — and it also
     * reflects that the import is validated as one payload: the document, its
     * declared type and the target are refused together, so splitting the target out
     * into the path would let a caller address a module the payload contradicts.
     */
    import: (): string => apiUrl(`${SEGMENT.modules}/${SEGMENT.import}`),
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
   * Accounts. The canonical family is flat; membership, approval and profile remain
   * tenant-scoped facts because the API resolves one tenant before dispatching the
   * request rather than accepting a second portal identity in the path.
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
    /** `GET` the resolved tenant's paged accounts; `POST` to create one. */
    collection: (): string => apiUrl(SEGMENT.users),

    /** `GET`, `PUT` or `DELETE` one account. */
    byId: (userId: number): string => apiUrl(`${SEGMENT.users}/${userId}`),

    /** `GET` or `PUT` the profile values of one account. */
    profile: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.profile}`),

    /**
     * `POST` a credential change made by the account holder, who supplies the
     * current credential alongside the new one.
     */
    password: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.password}`),

    /**
     * `POST` a credential reset performed by an administrator.
     *
     * Separate from the holder's own change because the two differ in what they
     * require and in who may call them: a reset proves nothing about the old
     * credential and is therefore restricted to a portal administrator. Neither
     * endpoint discloses a credential — see the retrieval note on
     * {@link AUTH_ENDPOINTS}.
     */
    passwordReset: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.passwordReset}`),

    /** `POST` to release an account locked out by failed sign-in attempts. */
    unlock: (userId: number): string => apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.unlock}`),

    /**
     * `PUT` the approval state of one account.
     *
     * One endpoint carrying the state, rather than a pair of verb-shaped routes for
     * granting and withdrawing it. The state being set is a query parameter that
     * `core/utils/http-params.util.ts` builds, so it does not appear here.
     */
    approval: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.approval}`),

    /** `POST` to oblige an account to change its credential at next sign-in. */
    requirePasswordChange: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.requirePasswordChange}`),

    /**
     * `GET` or `PUT` the portal-wide account policy.
     *
     * The policy governs every account in the resolved tenant and belongs to no
     * individual account, so it is the `settings` child of the account collection.
     */
    membershipSettings: (): string => apiUrl(`${SEGMENT.users}/${SEGMENT.settings}`),
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
    /** Definitions of the tenant the API resolves from the request. The only public family. */
    forCurrentPortal: {
      /** `GET` the definitions; `POST` to create one. Unpaged. */
      collection: (): string => apiUrl(SEGMENT.profileDefinitions),

      /** `GET`, `PUT` or `DELETE` one definition. */
      byId: (propertyDefinitionId: number): string =>
        apiUrl(`${SEGMENT.profileDefinitions}/${propertyDefinitionId}`),
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
    /** Roles of the tenant the API resolves from the request. The only public family. */
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

      // ⚠ THE ROLES-HELD-BY-ONE-ACCOUNT READ IS DELIBERATELY NOT DECLARED HERE, and its absence is the
      // resolution rather than an omission. The endpoint exists — `GET /api/v1/users/{userId}/roles` — but
      // no screen in this application reads it: the account editor shows an account's roles from the
      // `roles` member the account's own detail contract already carries, and no per-account role route is
      // declared anywhere in the route table. A template with no consumer is a claim about the wire that
      // nothing exercises, so it can drift from its controller silently — which is exactly the failure this
      // file's own preamble warns about. Declaring it belongs with the screen that needs it, on the day one
      // does.
    },
  },

  /** Role groups — the optional grouping above a role. */
  roleGroups: {
    /** Groups of the tenant the API resolves from the request. The only public family. */
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
  },
} as const;
