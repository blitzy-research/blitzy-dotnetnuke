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
 * THE TWO LIMITS OF THE RULE, STATED RATHER THAN HIDDEN
 * ---------------------------------------------------------------------------
 * 1. A tenant whose path segment SPELLS one of the reserved words - a child alias of
 *    `host/users`, say - is indistinguishable from this application's own account listing to a
 *    browser that has not yet spoken to the API, and is read as the application's. There is no
 *    fix available on this side: the document that would tell them apart is the same document
 *    for both. Recorded in `MIGRATION_NOTES.md`.
 * 2. ONE segment of prefix is honoured, matching what the legacy signup screen could compose.
 *    A deeper stored alias would need the proxy's own matcher widened in step, so the bound is
 *    the same on both sides rather than being generous on one of them.
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
 * Extensions that identify a served DOCUMENT OR ASSET rather than a tenant.
 *
 * ⚠ WITHOUT THIS THE RULE MISREADS A TEST HARNESS AND A DIRECTLY OPENED ASSET. The Karma
 * context page is served at `/context.html`, so its first segment is neither reserved nor a
 * tenant, and reading it as a tenant would silently prefix every URL every specification
 * asserts. The same is true of any address that names a file the proxy serves from disk: the
 * static-asset location answers those before the document fallback is ever reached, so an
 * address ending in one of these was never a tenant address to begin with.
 *
 * A CLOSED LIST rather than "any segment containing a dot", because a dot is legal in a tenant
 * path - `host/acme.co` is a perfectly good child alias - and refusing every dotted segment
 * would break such a tenant to solve a problem only these extensions cause.
 */
const DOCUMENT_EXTENSIONS: readonly string[] = [
  '.css',
  '.gif',
  '.htm',
  '.html',
  '.ico',
  '.jpeg',
  '.jpg',
  '.js',
  '.json',
  '.map',
  '.mjs',
  '.png',
  '.svg',
  '.txt',
  '.wasm',
  '.webp',
  '.woff',
  '.woff2',
  '.xml',
];

/**
 * Whether a path segment names a document or asset rather than a tenant.
 *
 * @param segment The raw first segment of a path.
 * @returns True when the segment ends in one of {@link DOCUMENT_EXTENSIONS}.
 */
function namesADocument(segment: string): boolean {
  const lowered = segment.toLowerCase();

  return DOCUMENT_EXTENSIONS.some((extension) => lowered.endsWith(extension));
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

  if (RESERVED_TOP_LEVEL_SEGMENTS.includes(first.toLowerCase())) {
    // One of the application's own screens, reached at the root deployment. Compared
    // case-insensitively because a caller may type an address in any case, while the segment
    // itself is preserved verbatim in the returned prefix for the case where it IS a tenant -
    // an alias is stored as it was authored and the API matches it under the database's own
    // collation.
    return '';
  }

  if (namesADocument(first)) {
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
