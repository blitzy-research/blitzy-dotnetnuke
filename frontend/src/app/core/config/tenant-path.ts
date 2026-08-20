/**
 * WHERE THE APPLICATION IS MOUNTED, and WHICH TENANT the running document belongs to. Two questions, two
 * answers, and they are deliberately kept apart.
 *
 * 1. THE DEPLOYMENT MOUNT POINT is read from the ONE value the deployment controls: the document's own
 *    `base` element. It is a TRUSTED DEPLOYMENT VALUE - authored by whoever serves the bundle, the same
 *    value the framework resolves `APP_BASE_HREF` from, and impossible for a caller to influence. A root
 *    deployment ships `<base href="/">` and reports no prefix; a bundle served beneath `/apps/dnn-admin/`
 *    reports that path and every URL this application composes follows it.
 * 2. THE TENANT PATH PREFIX is the child-portal alias segment a request may be addressed beneath, and it is
 *    answered by the SERVER rather than inferred here.
 *
 * ⚠ THIS MODULE USED TO INFER THE TENANT PREFIX FROM THE ADDRESS BAR AND ACT ON IT, AND THAT WAS A DEFECT.
 * The previous derivation treated any first path segment of alias shape that named no reserved route as a
 * child-portal prefix. No client can tell a real child-portal alias from a typo by inspecting the address,
 * because the two have exactly the same shape, so the inference was wrong in both directions and the wrong
 * direction was the common one:
 *
 *   - `/this-route-does-not-exist` was claimed as the prefix `/this-route-does-not-exist/`, which left the
 *     router an EMPTY remaining URL. The empty path matches the root redirect, so a mistyped address
 *     resolved to the caller's landing screen - measured as a rewrite to
 *     `/this-route-does-not-exist/login` - and the route table's `**` fallback was never consulted at all.
 *     A shipped, working not-found screen was structurally unreachable for every single-segment address.
 *   - Every link the shell rendered then carried the fabricated prefix, because `Location` builds an
 *     `href` from the injected base. The not-found screen's own recovery link, authored `routerLink="/"`,
 *     rendered `href="/nope/"` and navigating it landed on `/nope/login`: the phantom tenant was sticky
 *     and no in-application affordance returned the caller to the real root.
 *
 * WHAT REPLACES IT, AND WHY A ROUND TRIP IS ACCEPTED RATHER THAN THE INFERENCE SIMPLY DROPPED. The address
 * still yields a CANDIDATE ({@link detectTenantPathBase}), because path-addressed child portals are a real
 * legacy capability - the legacy signup screen composes and stores `domain/segment`, and the API's
 * `TenantPathBaseMiddleware` resolves it - so answering "no tenant, ever" would withdraw a supported
 * deployment shape. What changed is that a candidate is never ACTED upon: `tenant-resolution.ts` asks
 * the server whether the segment names a configured alias, and only a CONFIRMED answer is adopted
 * ({@link tenantPathBase}). An unconfirmed address is rendered as a route, so it either matches a screen or
 * reaches the `**` catch-all with the address intact. The probe is paid only by an address that is genuinely
 * ambiguous: the bare root and the application's own screens cannot name a tenant and are answered from the
 * path alone.
 *
 * THE TWO PREFIXES COMPOSE. A root-relative URL is built as mount point, then confirmed tenant segment, then
 * the configured path, so a bundle served beneath a path reaches its API through the same proxy that served
 * it whether or not a tenant segment is also in play. Neither is ever taken from the address bar
 * unconfirmed.
 */

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
 * The prefix this document has been CONFIRMED to be served beneath, or `null` while no confirmation has
 * been recorded. See {@link tenantPathBase} for why the two states are distinguished and
 * `tenant-resolution.ts` for who records one.
 */
let confirmedTenantPathBase: string | null = null;

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
 * Derives the CANDIDATE tenant path prefix from a path, as a pure function of it. Pure and exported so that
 * every case a specification needs to state can be stated without navigating a browser: the address is the
 * whole input.
 *
 * ⚠ THIS IS A CANDIDATE AND NOT AN ANSWER, AND ACTING ON IT AS AN ANSWER WAS A MEASURED DEFECT. No client
 * can tell a real child-portal alias from a typo by inspecting the address, because the two have exactly
 * the same shape - so this reports every lone segment of alias shape that names no reserved route, and
 * `tenant-resolution.ts` is what decides whether the candidate is real. What went wrong while this
 * function's answer WAS the answer is worth recording, because it is the reason the extra round trip is
 * accepted: a mistyped one-segment address was claimed as a tenant, which set the router's base href to
 * that segment, which meant the router never saw the address it had been given, so the `**` catch-all -
 * which does carry the not-found view - was unreachable and the internal URL collapsed to the root. The
 * caller landed on a sign-in form served at a path-relative address, every request went out beneath a
 * prefix no portal owned, sign-in answered 401 forever, and the only escape was editing the address bar.
 * Entirely silently.
 *
 * @param pathname A path, with or without a leading slash, as `Location.pathname` gives it.
 * @returns The candidate prefix INCLUDING its leading slash (`/child`), or the empty string when the path
 * cannot name a tenant at all.
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

// ---------------------------------------------------------------------------------------------------
// The deployment mount point: declared by the document, never inferred from the address.
// ---------------------------------------------------------------------------------------------------

/** The base element's `href` when the document declares none. Root, which is also the framework default. */
const ROOT_BASE_HREF = '/';

/**
 * Reduces a `base` element's `href` to a path prefix with NO trailing slash, which is the shape both
 * consumers here need. Pure, and exported so that every case a specification needs to state can be stated
 * without a document: the base value is the whole input.
 *
 * Accepts every form the attribute legitimately takes - a bare slash, a path with or without its trailing
 * slash, and an absolute URL, which is what `document.baseURI` yields - and answers the empty string for a
 * root mount so that a caller can concatenate the result unconditionally.
 *
 * @param baseHref The `href` of the document's base element, or an absolute URL carrying it.
 * @returns The prefix INCLUDING its leading slash (`/child`), or the empty string for a root mount.
 */
export function deploymentPathBaseFrom(baseHref: string): string {
  const trimmed = baseHref.trim();

  if (trimmed.length === 0) {
    return '';
  }

  // An absolute URL - `document.baseURI` always is one - is reduced to its path. The second argument is a
  // base for the relative case and is never consulted for an absolute one; it is a syntactically valid
  // placeholder rather than a value with meaning, because only `pathname` is read back out.
  let pathname: string;

  try {
    pathname = new URL(trimmed, 'http://base.invalid/').pathname;
  } catch {
    // Unreachable for any value a browser accepts as a base href, and answered as a root mount rather
    // than thrown: a malformed base must not stop the application from booting.
    return '';
  }

  // Collapse the path to its segments and rebuild, which normalises a missing leading slash, a trailing
  // slash and any doubled separator in one pass.
  const segments = pathname.split('/').filter((segment) => segment.length > 0);

  return segments.length === 0 ? '' : `/${segments.join('/')}`;
}

/**
 * The path prefix the running document is mounted beneath. Recomputed on each call rather than memoised,
 * because the whole computation is one parse of one attribute and a memoised value would have to be
 * invalidated by something - and nothing can invalidate it, since the base element is fixed for the life
 * of the document.
 *
 * @returns The prefix including its leading slash, or the empty string for a root mount.
 */
export function deploymentPathBase(): string {
  return deploymentPathBaseFrom(readBaseHref());
}

/**

}

/**
 * Reads the mount point the deployment DECLARED, and nothing else.
 *
 * ⚠ `document.baseURI` IS DELIBERATELY NOT USED AS A FALLBACK, and the reason is the very defect this
 * module exists to end: with no base element the platform resolves `baseURI` to the CURRENT ADDRESS, so a
 * document requested at `/nope/deeper` would report the mount point `/nope/deeper` and the address bar
 * would once again be deciding where the application lives. A document that declares no base element is
 * therefore treated as the root mount, which is both the framework's own default and the only answer that
 * cannot be influenced by a caller.
 *
 * @returns The declared base href, or the root when none is declared.
 */
function readBaseHref(): string {
  const declared = document.querySelector('base')?.getAttribute('href') ?? '';

  return declared.trim().length === 0 ? ROOT_BASE_HREF : declared;
}

// ---------------------------------------------------------------------------------------------------
// The tenant path prefix: a candidate from the address, an answer only from the server.
// ---------------------------------------------------------------------------------------------------

/**
 * The tenant path prefix the currently loaded document PROPOSES, before anything has confirmed it.
 * Recomputed on each call rather than memoised, because the whole computation is one split of one string.
 *
 * ⚠ THE DEPLOYMENT MOUNT POINT IS REMOVED FIRST, and it has to be. The two prefixes are different questions
 * with different authorities - the mount point is a value the deployment declares, the tenant segment is a
 * value the server confirms - and they arrive concatenated in one `Location.pathname`. A bundle served
 * beneath `/apps/dnn-admin/` would otherwise propose `apps` as a tenant on every single address it is opened
 * at, which is the address bar deciding tenancy again by another route.
 *
 * @returns The candidate prefix including its leading slash, or the empty string.
 */
export function candidateTenantPathBase(): string {
  const mount = deploymentPathBase();
  const pathname = window.location.pathname;
  const beneathMount =
    mount.length > 0 && pathname.startsWith(mount) ? pathname.slice(mount.length) : pathname;

  return detectTenantPathBase(beneathMount);
}

/**
 * The tenant path prefix in force for this document: the decision `tenant-resolution.ts` recorded, and NO
 * prefix at all until one has been recorded.
 *
 * ⚠ ONE ANSWER FOR THE ROUTER AND FOR EVERY API URL, AND THAT IS WHY A RECORDED DECISION IS READ RATHER THAN
 * THE LIVE ADDRESS. `app.config.ts` supplies this value as the router's `APP_BASE_HREF` once, at bootstrap,
 * while `api-endpoints.ts` composes it into every request for the life of the document. Were this to read
 * the address on each call, the two could disagree the moment the router left the address alone: a rejected
 * candidate KEEPS its segment in the address bar - deliberately, so a mistyped URL stays visible - and every
 * later request would be addressed beneath a segment the router had already refused to treat as a tenant.
 *
 * ⚠ AN UNRECORDED PREFIX READS AS NO PREFIX, AND THAT DEFAULT IS THE SAFE ONE RATHER THAN THE CONVENIENT
 * ONE. Answering with the candidate here would put the address bar back in charge of the answer, which is
 * the defect this module was rewritten to end, and it would do so in exactly the state where nothing has
 * been established. It costs a genuine child portal nothing: `main.ts` AWAITS the decision before the
 * application is created, and `tenant-resolution.ts` records one for EVERY outcome - including an
 * unreachable API, where it records the candidate deliberately so an outage cannot strand a real tenant at
 * the root. Keeping the candidate is therefore a DECISION that module takes, never a default this one
 * infers.
 *
 * @returns The prefix including its leading slash, or the empty string.
 */
export function tenantPathBase(): string {
  return confirmedTenantPathBase ?? '';
}

/**
 * Records the prefix this document is to be served beneath, as decided by `tenant-resolution.ts` from the
 * deployment's own answer, and by specifications that need to state what the application does once a given
 * answer is in hand.
 *
 * ⚠ THE EMPTY STRING IS A DECISION, NOT AN ABSENCE. Adopting `''` is how a candidate is REJECTED: it puts
 * the router's base back at the mount point so the address the caller typed reaches the route table
 * unaltered and the catch-all renders the not-found view. That is why the decision is held in a nullable of
 * its own rather than inferred from emptiness.
 *
 * @param prefix The confirmed prefix including its leading slash, or the empty string for no tenant.
 */
export function adoptTenantPathBase(prefix: string): void {
  confirmedTenantPathBase = prefix;
}

/**
 * Discards any confirmed prefix, returning the module to its pre-resolution state. Exists for
 * specifications, so that one case cannot leak its answer into the next.
 */
export function forgetTenantPathBase(): void {
  confirmedTenantPathBase = null;
}

// ---------------------------------------------------------------------------------------------------
// WHO ASKS THE SERVER, AND WHY IT IS NOT THIS MODULE.
//
// A probe was written here as well as in `tenant-resolution.ts`, and both closed the same defect: a
// candidate segment must be confirmed before it is acted upon. ONE probe survives, because two would issue
// two requests before first paint and could disagree with each other about the same address. The surviving
// one is `tenant-resolution.ts`, which is awaited by `main.ts`: it asks beneath the candidate itself, so
// reachability is part of the evidence; it bounds the wait; and it demotes a candidate only on a DEFINITE
// answer, so a deployment whose API is briefly unavailable cannot strand a real child portal at the root.
//
// The endpoint the probe removed from here used - `GET /api/v1/tenancy/path-prefix?segment=…` - remains part
// of the API and remains covered by its own tests. It answers the same question for any caller that wants
// one segment adjudicated without addressing anything beneath it.
// ---------------------------------------------------------------------------------------------------

/**
 * The value to provide as the router's base href: the deployment's mount point followed by the CONFIRMED
 * tenant prefix.
 *
 * Both contributions matter and neither may be dropped. The mount point is where the bundle is served from,
 * so a document served beneath `/apps/dnn-admin/` must keep that path on every route it renders; the tenant
 * segment is the child portal the server confirmed. Answering `/` for an address whose first segment names no
 * confirmed tenant is what lets the router see that segment and reach the `**` catch-all, so the not-found
 * view renders with the address intact - anonymously as much as for a signed-in caller.
 *
 * @returns `/child/` for a confirmed tenant-prefixed document, `/apps/dnn-admin/` for a bundle mounted
 * beneath a path, the two concatenated when both apply, and `/` otherwise. NEVER the empty string: the
 * location strategy requires a non-empty base.
 */
export function appBaseHref(): string {
  const prefix = `${deploymentPathBase()}${tenantPathBase()}`;

  return prefix.length === 0 ? ROOT_BASE_HREF : `${prefix}/`;
}
