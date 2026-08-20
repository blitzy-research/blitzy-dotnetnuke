import type { AppEnvironment } from './app-environment';

/**
 * The development environment: the values `ng serve` and `ng build --configuration development` compile
 * in place of `environment.ts`. Members appear in the same order as the interface declares them.
 */
export const environment: AppEnvironment = {
  production: false,

  // ABSOLUTE, and absolute ONLY here. There is no dev-server proxy — `angular.json` declares no
  // `proxyConfig` and no `proxy.conf.json` exists — so the dev server and the API have no shared origin and
  // the browser must reach the API host directly.
  apiBaseUrl: 'http://localhost:8080/api/v1',

  // Identical to the production twin on purpose: the application's identity is not
  // environment-specific, so it is not one of the two permitted divergences.
  applicationName: 'DotNetNuke Administration',
};
