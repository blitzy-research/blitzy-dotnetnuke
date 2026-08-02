/**
 * Build-time configuration for the PRODUCTION build, which is the workspace's
 * default configuration (`angular.json` sets `defaultConfiguration: production`
 * on the build target and declares the development overlay as a
 * `fileReplacements` entry that swaps this module for
 * `environment.development.ts`).
 *
 * Because the replacement runs the other way round — this file is the baseline
 * and the development file overrides it — the values below are the ones that
 * ship inside the container image. Nothing here is read at run time from the
 * server; a value that must vary per deployment belongs in the reverse proxy or
 * in the API's own configuration, not here, because everything in this module is
 * inlined into a public JavaScript bundle by the compiler.
 */
export interface AppEnvironment {
  /**
   * Whether this bundle was produced by the production configuration. Consumed
   * only to decide whether to enable development-only affordances; it is never
   * used to change a business rule, so the two bundles behave identically.
   */
  readonly production: boolean;

  /**
   * The base path every API request is issued against.
   *
   * MUST stay RELATIVE in the production bundle. `docker/nginx.conf` proxies
   * `/api/` to `http://api:8080/api/`, so the browser reaches the API through
   * the very origin that served the application and no request is cross-origin.
   * An absolute value such as `http://api:8080/api/v1` would resolve only from
   * inside the Docker network — never from a browser — and would additionally
   * turn every call into a cross-origin request subject to the API's CORS
   * policy. Neither failure is detectable at build time, which is why the
   * constraint is recorded here rather than left to convention.
   */
  readonly apiBaseUrl: string;

  /**
   * The application name rendered by the shell's banner band. Held here rather
   * than hard-coded into the header component so that the one identity string
   * has a single origin and the header stays presentational.
   */
  readonly applicationName: string;
}

/**
 * The production environment.
 */
export const environment: AppEnvironment = {
  production: true,
  apiBaseUrl: '/api/v1',
  applicationName: 'DotNetNuke Administration',
};
