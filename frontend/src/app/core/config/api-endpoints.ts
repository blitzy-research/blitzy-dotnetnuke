import { environment } from '../../../environments/environment';

/**
 * The single place API route templates are declared.
 *
 * Every path here is expressed RELATIVE to the configured API base, and the base
 * itself is relative in a production build (`/api/v1`). That is a deployment
 * requirement rather than a preference: the reverse proxy in front of the
 * containers forwards `/api/` to the API service, so the browser must reach the
 * API through the same origin that served the application. An absolute base such
 * as `http://api:8080/api/v1` resolves only from inside the container network and
 * would fail from a browser while every build step still succeeded.
 *
 * Declaring the paths once is what allows the authentication interceptor to
 * recognise the endpoints it must not attach a bearer token to. A hard-coded
 * string comparison in the interceptor would silently stop matching the first time
 * a route moved.
 */

/**
 * Joins the configured API base with a relative path.
 *
 * Collapses the boundary between the two so that a base with a trailing slash and
 * a path with a leading slash cannot produce a doubled separator — a URL that
 * most servers reject or, worse, route differently.
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
 * The authentication endpoints.
 *
 * Login, refresh and logout are anonymous on the server: they authenticate the
 * credentials or the refresh token they carry, not a bearer token. That is what
 * allows a refresh to succeed after the access token has already expired, and it
 * is why the authentication interceptor must leave them alone.
 */
export const AUTH_ENDPOINTS = {
  /** `POST` — exchanges credentials for a token pair. Anonymous. */
  login: apiUrl('auth/login'),

  /** `POST` — exchanges a refresh token for a new pair, rotating it. Anonymous. */
  refresh: apiUrl('auth/refresh'),

  /** `POST` — revokes a refresh token. Anonymous. */
  logout: apiUrl('auth/logout'),

  /** `GET` — returns the signed-in identity. Requires a bearer token. */
  me: apiUrl('auth/me'),
} as const;

/**
 * The endpoints an interceptor must never attach a bearer token to, and must
 * never attempt to recover with a refresh.
 *
 * `me` is deliberately absent: it is the one authentication endpoint that does
 * require a bearer token, so it takes one and a 401 from it is a genuine
 * expiry worth refreshing.
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
 * rather than a prefix test, because a prefix test on `auth/login` would also
 * match a hypothetical `auth/login-history` and silently stop sending its token.
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
