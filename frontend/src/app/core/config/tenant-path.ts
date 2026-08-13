/**
 * The tenant path prefix this document was served under, detected at RUN TIME.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS MODULE EXISTS
 * ---------------------------------------------------------------------------
 * The legacy product let a child portal be addressed by a PATH SEGMENT beneath a shared host
 * name. `Website/admin/Portal/Signup.ascx.vb` L232-L236 composes and stores exactly
 * `domain/segment`, and every request beneath that segment belonged to the child tenant. The
 * API reproduces that: `TenantPathBaseMiddleware` resolves the tenant from the host AND the
 * path and then moves the tenant's segment out of the routable path into the request's path
 * base, so `/child/api/v1/portals` resolves the child and still routes to the same action as
 * `/api/v1/portals`.
 *
 * ⚠ THAT CONTRACT REQUIRES THE PREFIX TO ARRIVE, AND THE BROWSER IS THE ONLY PLACE IT CAN BE
 * PUT BACK. A caller who opens `https://host/child` is served this application's document -
 * the reverse proxy falls back to it for every path it cannot serve from disk - and every
 * request the application then made was addressed to `/api/v1/...` at the ROOT, because the
 * configured base is a root-relative constant. The API therefore resolved the BARE-HOST tenant
 * for a caller who had asked for the child, which is a tenant-isolation defect rather than a
 * cosmetic one: sign-in authenticated against the wrong portal, and every later request
 * operated in the wrong arrival context.
 *
 * ---------------------------------------------------------------------------
 * WHY IT IS DETECTED RATHER THAN CONFIGURED
 * ---------------------------------------------------------------------------
 * AAP 0.4.1.2 and 0.5.2.5 fix the production API base at the RELATIVE `/api/v1`, and one built
 * bundle is served to every tenant of the deployment - the container image holds no per-tenant
 * value and cannot, because the proxy serves the same files for the bare host and for every
 * child beneath it. The prefix is therefore a property of the ADDRESS THIS DOCUMENT WAS SERVED
 * AT, which only the running browser knows. Nothing here changes the configured base: the
 * prefix is composed with it, in the one place that composes URLs at all.
 *
 * ---------------------------------------------------------------------------
 * HOW A TENANT SEGMENT IS TOLD FROM AN APPLICATION SEGMENT
 * ---------------------------------------------------------------------------
 * The console's route table is CLOSED - AAP 0.4.4 fixes it at twenty-five addresses - so the
 * set of first segments the application itself owns is finite and knowable. A first segment
 * that is one of them is the application's own; anything else was contributed by the address
 * the document was served under. That is the whole rule, and its correctness rests on the
 * route table staying closed, which `tenant-path.spec.ts` pins against `APP_ROUTES` itself.
 *
 * ⚠ THE RESERVED SET IS DUPLICATED HERE ON PURPOSE, AND THE DUPLICATION IS POLICED BY A TEST
 * RATHER THAN REMOVED. `app.routes.ts` imports every feature route barrel, and each barrel
 * imports the components it mounts; importing that module HERE would drag the entire route
 * graph into whatever imports this one - including `api-endpoints.ts`, which every service
 * imports - and defeat the lazy loading the initial-bundle budget depends on. The alternative
 * to duplication is a cycle, so the values are restated and a specification asserts they are
 * exactly the top-level paths `APP_ROUTES` declares.
 *
 * ---------------------------------------------------------------------------
 * THE RULE IS THE SERVER'S, AND THE SERVER NOW ENFORCES IT ON THE WRITE PATH
 * ---------------------------------------------------------------------------
 * MIGRATION: SEC-01. The five mirrors named below DID disagree, demonstrably rather than
 * theoretically: one stored alias row spelling `api` turned a successful sign-in into a 401.
 * This module is now derived from the one server-side authority. Recorded in MIGRATION_NOTES.md.
 *
 * ⚠ THIS MODULE IS ONE OF FIVE MIRRORS OF ONE CONTRACT, and the authority is
 * `backend/src/DnnMigration.Domain/Common/PortalAliasTopology.cs`. The other four are the alias
 * write path, the request pipeline that resolves an arriving address, the screen that binds an
 * alias, and the reverse-proxy location in `docker/api-proxy.conf`. They previously disagreed:
 * the write path stored addresses of unbounded depth with dots in any segment, the resolver
 * considered four segments and then fell back to the bare host when none matched, this module
 * honoured one segment, and the proxy could deliver one. An alias the server accepted could
 * therefore be undeliverable, and an address naming no tenant was answered by the PARENT tenant.
 *
 * Two consequences for the rule below, both of which SIMPLIFY it:
 *
 * 1. A SEGMENT CONTAINING A DOT CAN NO LONGER BE A TENANT, because the server refuses to store
 *    one. That is why the closed list of nineteen document extensions this module used to carry
 *    is gone: with dots excluded from the alias vocabulary, "names a served file" and "names a
 *    tenant" are disjoint by construction, and no extension can be omitted from a list that no
 *    longer exists. `/context.html`, `/main-ABCD1234.js` and `/robots.txt` are all read as the
 *    application's own addresses for the same structural reason.
 * 2. A SEGMENT SPELLING ONE OF THE RESERVED WORDS CAN NO LONGER BE A TENANT either. This used to
 *    be an unavoidable ambiguity - the document served for `host/users` is the same document
 *    whether that names a child portal or this console's account listing - and it is now
 *    prevented at the point of storage instead of tolerated at the point of reading.
 *
 * ONE segment of prefix is honoured, matching what the legacy signup screen could compose and
 * what the proxy can deliver. Widening it means widening all five mirrors in one change.
 *
 * This module is a DECLARATION module in the same sense as `api-endpoints.ts`: pure functions
 * over strings, no injectable, no state, no side effect.
 */

/**
 * The first path segments the application itself owns.
 *
 * Taken from the top-level `path` values of `APP_ROUTES`, lower-cased. `''` (the redirect) and
 * `'**'` (the fallback) contribute no segment and are therefore absent.
 *
 * ⚠ A SEGMENT MISSING FROM THIS SET IS READ AS A TENANT PREFIX, so an address the application
 * owns would have its own first segment prepended to every API request and every request would
 * be answered by the wrong tenant or by nothing. That is why the set is asserted against the
 * route table itself rather than reviewed by eye.
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
 * The first path segments the SERVER itself owns, which no stored alias may use either.
 *
 * ⚠ HELD SEPARATELY FROM {@link RESERVED_TOP_LEVEL_SEGMENTS} ON PURPOSE. That set is asserted by
 * `tenant-path.spec.ts` to be EXACTLY the route table's own top-level paths, which is what keeps
 * a newly added route from being read as a tenant. These four are not routes at all - they are
 * the roots the API answers - so folding them into that array would break the assertion that
 * makes it trustworthy. `PortalAliasTopology.ReservedPathSegments` on the server is the union of
 * the two, and a unit test there asserts that every prefix the request pipeline exempts from
 * tenant resolution appears in it.
 *
 * `api` matters most: every request this application makes is addressed beneath it, so a tenant
 * segment spelling `api` would make the console unusable for that tenant.
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
 * Whether a path segment is one the server could have stored as a tenant address.
 *
 * Mirrors `PortalAliasTopology.IsAddressableSegment`. A segment the server cannot store cannot
 * name a tenant, so anything this refuses belongs to the application's own address space -
 * including every served file, since a file name carries a dot and a dot is not in the
 * vocabulary.
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
 * Derives the tenant path prefix from a path, as a pure function of it.
 *
 * Pure and exported so that every case a specification needs to state can be stated without
 * navigating a browser: the address is the whole input.
 *
 * @param pathname A path, with or without a leading slash, as `Location.pathname` gives it.
 * @returns The prefix INCLUDING its leading slash (`/child`), or the empty string when the
 * path names no tenant.
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
    // Reserved words are compared case-insensitively because a caller may type an address in any
    // case, while the segment itself is preserved verbatim in the returned prefix for the case
    // where it IS a tenant - an alias is stored as it was authored and the API matches it under
    // the database's own collation.
    return '';
  }

  return `/${first}`;
}

/**
 * The tenant path prefix for the document currently loaded, or the empty string.
 *
 * Recomputed on each call rather than memoised, because the whole computation is one split of
 * one string and a memoised value would have to be invalidated by something - and nothing can
 * invalidate it, since the prefix changes only when a new document is loaded. Reading the live
 * location also means a specification can arrange the address with the platform's own history
 * API and this answers accordingly, with no module state to reset between cases.
 *
 * ⚠ THE ROUTER'S NAVIGATIONS DO NOT CHANGE THE ANSWER, and that is what makes reading the live
 * location safe. `APP_BASE_HREF` is provided from this value, so every address the router
 * pushes keeps the prefix as its first segment; a navigation from `/child/portals` to
 * `/child/users` therefore still reports `/child`.
 *
 * @returns The prefix including its leading slash, or the empty string.
 */
export function tenantPathBase(): string {
  return detectTenantPathBase(window.location.pathname);
}

/**
 * The value to provide as the router's base href.
 *
 * `'/'` rather than the empty string for the ordinary deployment, because that is the base the
 * document itself declares and the location strategy requires a non-empty value.
 *
 * @returns `/child/` for a tenant-prefixed document, `/` otherwise.
 */
export function appBaseHref(): string {
  const prefix = tenantPathBase();

  return prefix.length === 0 ? '/' : `${prefix}/`;
}
