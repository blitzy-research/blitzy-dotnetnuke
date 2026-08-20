import { adoptTenantPathBase, candidateTenantPathBase, deploymentPathBase } from './tenant-path';

/**
 * Settles, before the application starts, whether the first path segment of the address this document was
 * served at really names a tenant.
 *
 * ## Why this module exists
 *
 * A portal may be addressed beneath one path segment of a shared host (`host/acme`, a stored
 * `PortalAlias`), and one built bundle is served to every tenant, so the segment can only be derived at run
 * time from the address the document arrived at. `tenant-path.ts` derives the CANDIDATE, and a candidate is
 * all a client can produce on its own: a mistyped console route has exactly the same shape as a child
 * portal's segment.
 *
 * ⚠ THE MEASURED CONSEQUENCE OF ADOPTING A CANDIDATE UNASKED, which is what this module exists to end. A
 * one-character typo of the most-typed admin path - `/portls` for `/portals` - was taken as a tenant prefix.
 * The router's base became `/portls/`, which left the routable address empty, so the root redirect ran and
 * the console painted a pristine, fully styled sign-in form. Every request beneath the segment resolved no
 * tenant and was refused, so CORRECT credentials were answered `401` and the screen reported the refusal as
 * though the credential were at fault. The address was the fault, no in-application link could escape the
 * prefix, and the route table's own catch-all - the view that states plainly that nothing answers an
 * address - was unreachable for the one case that needed it most.
 *
 * ## What it asks, and what it does with each answer
 *
 * The deployment holds the alias rows, so the deployment is asked: `GET {candidate}/api/v1/tenant-address`,
 * the one anonymous read in the API, which reports the path prefix the request itself resolved beneath. Its
 * REACHABILITY is as informative as its body - the API moves a tenant's segment out of the routable path
 * only when the address resolves to an alias carrying that segment, and it fails closed with no bare-host
 * fallback, so an unrecognised segment matches no route at all.
 *
 * | Answer | Decision | Why |
 * |---|---|---|
 * | `200` reporting the candidate | adopt the candidate | The segment is a stored alias's own path |
 * | `200` reporting nothing, or another segment | adopt the root | The address reaches a tenant addressed elsewhere |
 * | `4xx` other than `408`/`429` | adopt the root | Nothing answers beneath the segment, so it names no tenant |
 * | `5xx`, `408`, `429`, transport failure, timeout | keep the candidate | The deployment did not say, and an outage must not strand a real tenant's address |
 *
 * The last row is the deliberate asymmetry: only a definite answer demotes a candidate. A deployment whose
 * API is unreachable answers nothing for ANY address, and rendering "nothing answers this address" for a
 * legitimate child portal during an outage would misdescribe an outage as a missing tenant - while keeping
 * the candidate leaves the caller with the honest service-unavailable report the error surface already
 * gives.
 *
 * ## Cost
 *
 * One request, and only for an address that proposes a candidate at all. The bare host and every one of the
 * console's own routes - which is every address the application itself navigates to - propose none and are
 * decided without touching the network, so first paint is unchanged for them.
 */

/** The API resource that reports the prefix a request resolved beneath. */
const TENANT_ADDRESS_RESOURCE = 'tenant-address';

/**
 * How long the deployment has to answer before the answer is treated as unavailable.
 *
 * Bounded because this request blocks the application's start: an unanswered probe must degrade to "keep the
 * candidate" in a few seconds rather than leave a blank document for a browser's own connection timeout,
 * which is measured in tens of seconds.
 */
const PROBE_TIMEOUT_MS = 4000;

/**
 * Statuses in the caller-correctable range that describe a TRANSIENT condition rather than an address that
 * reaches nothing: a timeout between proxy and API, and a rate limiter refusing a caller that is early.
 * Neither says anything about whether the segment names a tenant.
 */
const TRANSIENT_STATUSES: readonly number[] = [408, 429];

/** What the deployment said about the candidate. */
type ProbeOutcome = 'confirmed' | 'rejected' | 'inconclusive';

/**
 * Settles the tenant path prefix for this document and records the decision, so that the router's base href
 * and every API URL are composed from one confirmed value.
 *
 * ⚠ IT NEVER REJECTS. Every failure mode resolves to a decision, because the application's start awaits
 * this call: a rejection here would stop the document from bootstrapping at all, which is a far worse
 * outcome than proceeding with the address's own candidate.
 *
 * @param apiBaseUrl The configured API base, supplied by the caller rather than imported here so that the
 * one composition root - `main.ts` - names the configuration this decision depends on, and so that the
 * decision can be stated for either topology without rebuilding a bundle.
 * @returns The prefix adopted, including its leading slash, or the empty string for the root.
 */
export async function resolveTenantPathBase(apiBaseUrl: string): Promise<string> {
  const candidate = candidateTenantPathBase();

  if (candidate.length === 0) {
    // No candidate to settle: the bare host, one of the console's own routes, or a served file. Recorded
    // rather than left unrecorded, so that the value in force is always a decision.
    adoptTenantPathBase('');

    return '';
  }

  const probeUrl = buildProbeUrl(candidate, apiBaseUrl);

  if (probeUrl === null) {
    // The API is configured at an absolute address, which is the development topology: the document and the
    // API are served by different origins there, so a path segment on the document's origin cannot be
    // matched against an alias by the API at all and asking would answer about the wrong address.
    adoptTenantPathBase(candidate);

    return candidate;
  }

  const outcome = await askDeployment(probeUrl, candidate);
  const decision = outcome === 'rejected' ? '' : candidate;

  adoptTenantPathBase(decision);

  return decision;
}

/**
 * Composes the address the probe is sent to, or reports that no probe is possible.
 *
 * @param candidate The candidate prefix, including its leading slash.
 * @param apiBaseUrl The configured API base.
 * @returns The probe address, or `null` when the API is not addressed relative to this origin.
 */
function buildProbeUrl(candidate: string, apiBaseUrl: string): string | null {
  const apiBase = apiBaseUrl.replace(/\/+$/, '');

  if (apiBase.startsWith('/') === false) {
    return null;
  }

  // Mount point, then candidate, then the configured base - the same order `api-endpoints.ts` composes every
  // other URL in, so the probe is addressed exactly where the API answers. A root-mounted bundle contributes
  // nothing here and the address reads `/acme` + `/api/v1` + `/tenant-address`; a bundle served beneath
  // `/apps/dnn-admin/` reaches its API through the proxy that served it. This is the ONE place the API base
  // is composed outside `api-endpoints.ts`, and it has to be: that module already imports `tenant-path.ts`,
  // so the probe cannot live beside the endpoint vocabulary without a circular import.
  return `${deploymentPathBase()}${candidate}${apiBase}/${TENANT_ADDRESS_RESOURCE}`;
}

/**
 * Asks the deployment about the candidate, translating every possible answer into one of three outcomes.
 *
 * @param probeUrl The address to ask.
 * @param candidate The candidate prefix being asked about.
 * @returns What the deployment said.
 */
async function askDeployment(probeUrl: string, candidate: string): Promise<ProbeOutcome> {
  const controller = new AbortController();
  const expiry = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);

  try {
    const response = await fetch(probeUrl, {
      method: 'GET',
      // No credential is attached and none exists: an access token in this application lives in memory
      // only, so a document that has just loaded has none, which is exactly why the endpoint is anonymous.
      credentials: 'omit',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    });

    if (response.ok) {
      return readReportedPrefix(response, candidate);
    }

    // A refusal the caller could correct means nothing answers beneath the segment - the endpoint was never
    // reached, because the segment was never moved out of the routable path. The two transient statuses are
    // excluded: both describe the moment rather than the address.
    const definitelyUnreachable =
      response.status >= 400 &&
      response.status < 500 &&
      TRANSIENT_STATUSES.includes(response.status) === false;

    return definitelyUnreachable ? 'rejected' : 'inconclusive';
  } catch {
    // A transport failure or the timeout above. The deployment said nothing, so nothing is concluded.
    return 'inconclusive';
  } finally {
    clearTimeout(expiry);
  }
}

/**
 * Reads the prefix the deployment reported and compares it with the candidate.
 *
 * @param response The answered response.
 * @param candidate The candidate prefix being asked about.
 * @returns Whether the reported prefix confirms the candidate.
 */
async function readReportedPrefix(
  response: Response,
  candidate: string,
): Promise<ProbeOutcome> {
  let reported: unknown;

  try {
    const payload: unknown = await response.json();
    reported =
      typeof payload === 'object' && payload !== null
        ? (payload as { data?: { pathPrefix?: unknown } }).data?.pathPrefix
        : undefined;
  } catch {
    // The endpoint answered but its body could not be read. Reaching it at all is the primary signal - the
    // request could only have matched a route because the segment had already been recognised as a tenant's
    // - so the candidate stands rather than being demoted on a parsing accident.
    return 'confirmed';
  }

  if (typeof reported !== 'string') {
    return 'confirmed';
  }

  // Compared case-insensitively, because the API matches an alias case-insensitively and answers with the
  // stored spelling: a caller that typed `/Acme` for a row stored as `/acme` has reached the tenant, and
  // its own spelling is the one that must stay in the address bar.
  return reported.toLowerCase() === candidate.toLowerCase() ? 'confirmed' : 'rejected';
}
