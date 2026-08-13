import { environment } from '../../../environments/environment';
import { tenantPathBase } from './tenant-path';

/** The complete vocabulary of literal path segments, in kebab-case. */
const SEGMENT = {
  /** Accounts eligible to administer one portal. */
  administrators: 'administrators',

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

  /** Roles an invitation code admits an account to. */
  redemptions: 'redemptions',

  /** Access-token renewal. */
  refresh: 'refresh',

  /** Flag obliging an account to change its credential at next sign-in. */
  requirePasswordChange: 'require-password-change',

  /** Role group collection. */
  roleGroups: 'role-groups',

  /** Role collection. */
  roles: 'roles',

  /** Body-bound account search. */
  search: 'search',
  /** Subscribable services offered to one account. */
  services: 'services',

  /** Configuration projection of one portal. */
  settings: 'settings',

  /** One account's assignment to one service. */
  subscription: 'subscription',

  /** Page collection. */
  tabs: 'tabs',

  /** A service's trial period, taken once by an account. */
  trial: 'trial',

  /** Release of a locked-out account. */
  unlock: 'unlock',

  /** Account collection. */
  users: 'users',

  /** The account picker's projection: a key and the two captions an option shows. */
  choices: 'choices',
} as const;

/**
 * Joins the configured API base with a relative path. Collapses the boundary between the two so that a
 * base with a trailing slash and a path with a leading slash cannot produce a doubled separator — a URL
 * that most servers reject or, worse, route differently.
 *
 * @param path A path relative to the API base, with or without a leading slash.
 * @returns The absolute-or-root-relative URL to request.
 */
export function apiUrl(path: string): string {
  const base = configuredApiBase();
  const suffix = path.replace(/^\/+/, '');

  return suffix.length === 0 ? base : `${base}/${suffix}`;
}

/** @returns The base with no trailing slash. */
function configuredApiBase(): string {
  const configured = environment.apiBaseUrl.replace(/\/+$/, '');

  if (!configured.startsWith('/')) {
    return configured;
  }

  return `${tenantPathBase()}${configured}`;
}

/**
 * A portal identifier, naming the tenant a request is scoped to. `portalId` may legitimately be `0` OR
 * `-1`, and no template may treat either as absent.
 */
export interface PortalScope {
  /** The portal the request is scoped to. */
  readonly portalId: number;
}

/** One alias of one portal. the child segment is `portalAliasId`. */
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
 * The authentication endpoints, closed at four. Login, refresh and logout are anonymous on the server:
 * they authenticate the credentials or the refresh token they carry, not a bearer token.
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
 * The endpoints an interceptor must never attach a bearer token to, and must never attempt to recover
 * with a refresh. `me` is deliberately absent: it is the one authentication endpoint that does require a
 * bearer token, so it takes one and a 401 from it is a genuine expiry worth refreshing.
 */
export const ANONYMOUS_AUTH_ENDPOINTS: readonly string[] = Object.freeze([
  AUTH_ENDPOINTS.login,
  AUTH_ENDPOINTS.refresh,
  AUTH_ENDPOINTS.logout,
]);

// URL RESOLUTION — the shared basis for both credential tests below
// BOTH TESTS BELOW RESOLVE URLS RATHER THAN COMPARING STRINGS, AND THAT IS A SECURITY PROPERTY RATHER THAN
// A TIDINESS ONE. A textual prefix or substring comparison against the configured base cannot tell WHERE a
// URL points, only what it happens to spell.

/**
 * Resolves a URL against the document's base, or null when it cannot be resolved.
 *
 * @param url An absolute, protocol-relative or relative URL.
 * @returns The resolved URL, or null when the value is not a URL at all.
 */
function resolveAgainstDocument(url: string): URL | null {
  try {
    return new URL(url, document.baseURI);
  } catch {
    return null;
  }
}

/**
 * The configured API base as an absolute URL, or null when it cannot discriminate.
 *
 * @returns The resolved base, or null when no safe comparison is possible.
 */
function resolveApiBase(): URL | null {
  // ⚠ THE SAME BASE {@link apiUrl} COMPOSES, INCLUDING THE TENANT PATH PREFIX, AND THAT IDENTITY IS
  // LOAD-BEARING. This is what {@link isApiRequest} compares against, and that predicate is what the
  // authentication and correlation interceptors use to decide whether a request belongs to this API.
  // Resolving the base WITHOUT the prefix while the URL builder composes it WITH one would make every
  // request under a child portal fail the comparison: the bearer token would be dropped from every call and
  // the console would answer 401 to a signed-in operator, with nothing in the build to say why.
  const configured = configuredApiBase();

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
 * Whether a request URL addresses this application's API at all. Used to keep credentials off requests
 * that are not going to the API — a static asset, a template, or a third-party URL. Attaching a bearer
 * token to those would disclose it to whoever serves them.
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
 * Whether a request URL addresses one of the anonymous authentication endpoints. Compares resolved paths,
 * so a URL that carries a query string or arrives fully qualified still matches.
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
 * Every route template the application may address, grouped by resource. Frozen through `as const`, so
 * the tree is readonly at every depth and an accidental assignment does not compile.
 */
export const API_ENDPOINTS = {
  /**
   * Authentication. The same four strings as {@link AUTH_ENDPOINTS}, reachable from within the grouped
   * tree so a caller need not know both names.
   */
  auth: AUTH_ENDPOINTS,

  /** Portals — the multi-tenant site containers. */
  portals: {
    /** `GET` the paged collection; `POST` to create. */
    collection: (): string => apiUrl(SEGMENT.portals),

    /** `GET`, `PUT` or `DELETE` one portal. */
    byId: (portalId: number): string => apiUrl(`${SEGMENT.portals}/${portalId}`),

    /**
     * `GET` the configuration projection of one portal; `PUT` its complete editable settings state. this
     * is a column projection and replacement, not a key/value settings table.
     */
    settings: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.settings}`),

    /**
     * `GET` the accounts one portal may designate as its administrator. this lives under the PORTAL
     * resource rather than under a role one because the portal has to come from the path.
     */
    administrators: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.administrators}`),
  },

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
   * Module placements. The route family is flat; the API resolves the tenant from the request host and
   * authenticated context before any service call.
   */
  modules: {
    /** `GET` the resolved tenant's placements; `POST` to add one. */
    collection: (): string => apiUrl(SEGMENT.modules),

    /** `GET`, `PUT` or `DELETE` one placement. */
    byId: (moduleId: number): string => apiUrl(`${SEGMENT.modules}/${moduleId}`),

    /** `GET` or `PUT` the per-placement settings. */
    settings: (moduleId: number): string =>
      apiUrl(`${SEGMENT.modules}/${moduleId}/${SEGMENT.settings}`),

    export: (moduleId: number): string =>
      apiUrl(`${SEGMENT.modules}/${moduleId}/${SEGMENT.export}`),

    /**
     * `POST` to import content into the resolved tenant. the target module travels IN THE BODY, not in
     * the path, so this template takes no identifier.
     */
    import: (): string => apiUrl(`${SEGMENT.modules}/${SEGMENT.import}`),
  },

  /** The module definition catalogue. */
  moduleDefinitions: {
    /** `GET` every definition available to the resolved tenant. Unpaged. */
    collection: (): string => apiUrl(SEGMENT.moduleDefinitions),

    /** `GET` one definition. */
    byId: (moduleDefinitionId: number): string =>
      apiUrl(`${SEGMENT.moduleDefinitions}/${moduleDefinitionId}`),

    /** `GET` the definitions belonging to one installed package. The package is a PATH segment. */
    forDesktopModule: (desktopModuleId: number): string =>
      apiUrl(`${SEGMENT.moduleDefinitions}/${SEGMENT.desktopModules}/${desktopModuleId}`),
  },

  /**
   * Accounts. The canonical family is flat; membership, approval and profile remain tenant-scoped facts
   * because the API resolves one tenant before dispatching the request rather than accepting a second
   * portal identity in the path.
   */
  users: {
    /**
     * `GET` the resolved tenant's paged accounts; `POST` to create one. ⚠ THE `GET` IS FOR A LISTING THAT
     * NAMES NOBODY. It carries a page index, a page size, an ordering and an approval state, none of
     * which identifies a person. A search by user name, email address or profile property must go to
     * {@link API_ENDPOINTS.users.search} instead — see the note there.
     */
    collection: (): string => apiUrl(SEGMENT.users),

    /**
     * `POST` an account search whose filters travel in the REQUEST BODY. ⚠ THIS EXISTS FOR A PRIVACY
     * REASON, NOT AN ERGONOMIC ONE, AND MUST NOT BE COLLAPSED BACK INTO A QUERY STRING. The account
     * listing filters on a user name, an email address and an arbitrary profile-property name paired with
     * the value to match.
     */
    search: (): string => apiUrl(`${SEGMENT.users}/${SEGMENT.search}`),

    /**
     * `GET` the resolved tenant's accounts as an account PICKER needs them. ⚠ A SEPARATE ADDRESS FROM
     * {@link API_ENDPOINTS.users.collection}, AND IT MUST NOT BE COLLAPSED INTO IT. This answers a key
     * and the two captions an option renders — `userId`, `username` and `displayName` — and nothing else.
     */
    choices: (): string => apiUrl(`${SEGMENT.users}/${SEGMENT.choices}`),

    /** `GET`, `PUT` or `DELETE` one account. */
    byId: (userId: number): string => apiUrl(`${SEGMENT.users}/${userId}`),

    /** `GET` or `PUT` the profile values of one account. */
    profile: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.profile}`),

    /**
     * `POST` a credential change made by the account holder, who supplies the current credential
     * alongside the new one.
     */
    password: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.password}`),

    /**
     * `POST` a credential reset performed by an administrator. Separate from the holder's own change
     * because the two differ in what they require and in who may call them: a reset proves nothing about
     * the old credential and is therefore restricted to a portal administrator.
     */
    passwordReset: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.passwordReset}`),

    /** `POST` to release an account locked out by failed sign-in attempts. */
    unlock: (userId: number): string => apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.unlock}`),

    /** `PUT` the approval state of one account. */
    approval: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.approval}`),

    /** `POST` to oblige an account to change its credential at next sign-in. */
    requirePasswordChange: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.requirePasswordChange}`),

    /**
     * `GET` or `PUT` the portal-wide account policy. The policy governs every account in the resolved
     * tenant and belongs to no individual account, so it is the `settings` child of the account
     * collection.
     */
    membershipSettings: (): string => apiUrl(`${SEGMENT.users}/${SEGMENT.settings}`),

    services: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.services}`),

    /** `POST` to subscribe to one service or renew a lapsed subscription; `DELETE` to cancel one. */
    serviceSubscription: (userId: number, roleId: number): string =>
      apiUrl(
        `${SEGMENT.users}/${userId}/${SEGMENT.services}/${roleId}/${SEGMENT.subscription}`,
      ),

    serviceTrial: (userId: number, roleId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.services}/${roleId}/${SEGMENT.trial}`),

    /** `POST` an invitation code, joining the account to every role recorded against it. */
    serviceRedemptions: (userId: number): string =>
      apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.services}/${SEGMENT.redemptions}`),
  },

  /**
   * Profile property definitions — the fields an account's profile may carry. ordering is a FIELD, set
   * through `PUT` on one definition.
   */
  profileDefinitions: {
    /** Definitions of the tenant the API resolves from the request. The only public family. */
    forCurrentPortal: {
      /** `GET` the definitions; `POST` to create one. */
      collection: (): string => apiUrl(SEGMENT.profileDefinitions),

      /** `GET`, `PUT` or `DELETE` one definition. */
      byId: (propertyDefinitionId: number): string =>
        apiUrl(`${SEGMENT.profileDefinitions}/${propertyDefinitionId}`),
    },
  },

  /**
   * Roles, and the memberships that connect them to accounts. subscribing to and unsubscribing from a
   * paid service is expressed as role membership, not as a service resource of its own.
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

      /** @param userId The account whose roles to read. */
      heldByUser: (userId: number): string =>
        apiUrl(`${SEGMENT.users}/${userId}/${SEGMENT.roles}`),
    },
  },

  /** Role groups — the optional grouping above a role. */
  roleGroups: {
    /** Groups of the tenant the API resolves from the request. The only public family. */
    forCurrentPortal: {
      /** `GET` the groups; `POST` to create one. */
      collection: (): string => apiUrl(SEGMENT.roleGroups),

      /**
       * `GET`, `PUT` or `DELETE` one group. The delete refuses a group that still contains roles, rather
       * than cascading: removing a grouping must not silently remove the permissions grouped by it.
       */
      byId: (roleGroupId: number): string => apiUrl(`${SEGMENT.roleGroups}/${roleGroupId}`),
    },
  },

  /** Pages. THREE endpoints, and no more — the narrowest surface in this file. */
  tabs: {
    /** `GET` the pages of one portal. */
    forPortal: (portalId: number): string =>
      apiUrl(`${SEGMENT.portals}/${portalId}/${SEGMENT.tabs}`),

    /** `GET` or `PUT` one page. */
    byId: (tabId: number): string => apiUrl(`${SEGMENT.tabs}/${tabId}`),
  },

  /** The permission catalogue. */
  permissions: {
    /** `GET` the catalogue. */
    collection: (): string => apiUrl(SEGMENT.permissions),

    /** `GET` one permission definition. */
    byId: (permissionId: number): string => apiUrl(`${SEGMENT.permissions}/${permissionId}`),
  },
} as const;
