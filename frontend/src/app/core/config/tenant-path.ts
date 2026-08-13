/**
 * The first path segments the application itself owns. Taken from the top-level `path` values of
 * `APP_ROUTES`, lower-cased.
 */
export const RESERVED_TOP_LEVEL_SEGMENTS: readonly string[] = [
  'login',
  'modules',
  'portals',
  'role-groups',
  'roles',
  'settings',
  'users',
];

/**
 * The first path segments the SERVER itself owns, which no stored alias may use either. ⚠ HELD SEPARATELY
 * FROM {@link RESERVED_TOP_LEVEL_SEGMENTS} ON PURPOSE. That set is asserted by `tenant-path.spec.ts` to
 * be EXACTLY the route table's own top-level paths, which is what keeps a newly added route from being
 * read as a tenant.
 */
export const RESERVED_PLATFORM_SEGMENTS: readonly string[] = [
  'api',
  'health',
  'openapi',
  'swagger',
];

/** Matches one segment made only of ASCII letters, digits, hyphens and underscores. */
const ADDRESSABLE_SEGMENT = /^[0-9A-Za-z_-]+$/u;

/**
 * Whether a path segment is one the server could have stored as a tenant address. Mirrors
 * `PortalAliasTopology.IsAddressableSegment`.
 *
 * @param segment The raw first segment of a path.
 * @returns True when the segment could name a tenant.
 */
function isAddressableSegment(segment: string): boolean {
  if (ADDRESSABLE_SEGMENT.test(segment) === false) {
    return false;
  }

  const lowered = segment.toLowerCase();

  return (
    RESERVED_TOP_LEVEL_SEGMENTS.includes(lowered) === false &&
    RESERVED_PLATFORM_SEGMENTS.includes(lowered) === false
  );
}

/**
 * Derives the tenant path prefix from a path, as a pure function of it. Pure and exported so that every
 * case a specification needs to state can be stated without navigating a browser: the address is the
 * whole input.
 *
 * ⚠ AN UNRECOGNISED SINGLE SEGMENT IS CLAIMED AS A TENANT PREFIX, AND NOTHING HERE CAN AVOID IT.
 * No client can tell a real child-portal alias from a typo without asking the server, so a lone segment
 * of alias shape that names no reserved route is treated as a tenant path base. One consequence is worth
 * stating plainly: the router's `**` catch-all is NOT reached for such an address - the internal URL
 * becomes the empty root, which the root redirect answers - so a mistyped one-segment address lands on
 * the caller's landing screen rather than the not-found view. Reserved segments are excluded for exactly
 * this reason: they are the addresses that must keep their own routes.
 *
 * @param pathname A path, with or without a leading slash, as `Location.pathname` gives it.
 * @returns The prefix INCLUDING its leading slash (`/child`), or the empty string when the path names no
 * tenant.
 */
export function detectTenantPathBase(pathname: string): string {
  const segments = pathname.split('/').filter((segment) => segment.length > 0);

  if (segments.length === 0) {
    // The bare root: the ordinary single-tenant deployment, and the address the application is
    // served at for the bare-host alias.
    return '';
  }

  const first = segments[0];

  if (isAddressableSegment(first) === false) {
    // One of the application's own screens, one of the server's own roots, or a served file.
    return '';
  }

  return `/${first}`;
}

/**
 * The tenant path prefix for the document currently loaded, or the empty string. Recomputed on each call
 * rather than memoised, because the whole computation is one split of one string and a memoised value
 * would have to be invalidated by something - and nothing can invalidate it, since the prefix changes
 * only when a new document is loaded.
 *
 * @returns The prefix including its leading slash, or the empty string.
 */
export function tenantPathBase(): string {
  return detectTenantPathBase(window.location.pathname);
}

/**
 * The value to provide as the router's base href.
 *
 * @returns `/child/` for a tenant-prefixed document, `/` otherwise.
 */
export function appBaseHref(): string {
  const prefix = tenantPathBase();

  return prefix.length === 0 ? '/' : `${prefix}/`;
}
