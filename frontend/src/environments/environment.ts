/**
 * PRODUCTION build-time configuration for the `dnn-migration` Angular workspace. The `fileReplacements`
 * direction is the opposite of the usual Angular scaffold, so read it before changing a value here.
 */

import type { AppEnvironment } from './app-environment';

/** The production environment: the values that ship inside the container image. */
export const environment: AppEnvironment = {
  production: true,

  // RELATIVE, and it must stay relative. `docker/nginx.conf` proxies `/api/` to `http://api:8080/api/` on
  // this very origin and serves `index.html` for everything else, so the SPA and the API share one origin,
  // no request is cross-origin, and the bundle is portable to any host name the proxy is served under.
  apiBaseUrl: '/api/v1',

  applicationName: 'DotNetNuke Administration',
};
