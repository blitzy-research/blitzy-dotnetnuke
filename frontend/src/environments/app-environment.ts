/** The shape both `environment.ts` and `environment.development.ts` must satisfy. */
export interface AppEnvironment {
  /**
   * Whether this bundle was produced by the production configuration. Used only to gate development-only
   * affordances.
   */
  readonly production: boolean;

  /**
   * The base path every API request is issued against. ⚠ THE OTHER PERMITTED DIVERGENCE, and the only
   * member whose value is load-bearing for deployment. It MUST be the RELATIVE path in `environment.ts`,
   * because `docker/nginx.conf` proxies `/api/` on the origin that served the bundle, and it is absolute
   * ONLY in `environment.development.ts`, where no dev-server proxy exists.
   */
  readonly apiBaseUrl: string;

  /**
   * The application name rendered in the shell's banner band. Held here rather than hard-coded into a
   * template so the one identity string has a single origin and the header stays presentational.
   */
  readonly applicationName: string;
}
