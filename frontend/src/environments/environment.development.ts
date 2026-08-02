import type { AppEnvironment } from './environment';

/**
 * Build-time configuration for the DEVELOPMENT build.
 *
 * `angular.json` swaps this module in for `environment.ts` through the
 * development configuration's `fileReplacements` entry, so the shape must stay
 * assignment-compatible with {@link AppEnvironment}; the explicit annotation
 * below is what makes a divergence a compile error rather than a run-time
 * surprise.
 *
 * The one substantive difference is the API base path. During development the
 * SPA is served by the Angular dev server on port 4200 while the API runs on
 * 8080, so there is no shared origin and no proxy in front of either. The
 * absolute origin is therefore correct HERE and wrong in the production bundle,
 * which is exactly why the two files exist. The API's CORS policy is configured
 * to admit this origin.
 *
 * This mirrors the legacy deployment's own twin-configuration convention
 * (`Website/release.config` alongside `Website/development.config`) rather than
 * introducing a new one.
 */
export const environment: AppEnvironment = {
  production: false,
  apiBaseUrl: 'http://localhost:8080/api/v1',
  applicationName: 'DotNetNuke Administration',
};
