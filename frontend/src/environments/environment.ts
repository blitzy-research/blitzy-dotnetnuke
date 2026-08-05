/**
 * PRODUCTION build-time configuration for the `dnn-migration` Angular workspace.
 *
 * READ THIS BEFORE CHANGING A VALUE BELOW, because the direction is the opposite
 * of the convention most Angular readers expect. This module is the PRODUCTION
 * configuration; `environment.development.ts` is the override. `angular.json`
 * declares a `fileReplacements` entry under its `development` configuration only:
 *
 *     production  -> fileReplacements: []                       (no substitution)
 *     development -> fileReplacements: [ this module -> environment.development.ts ]
 *
 * `ng build --configuration production` — which is also the build target's
 * `defaultConfiguration` — therefore ships exactly the values written here, while
 * `ng serve` and `ng build --configuration development` swap this module out. The
 * `test` target declares no substitution either, so every Karma spec compiles
 * against this file and against the relative base path below.
 *
 * Everything in this module is inlined into a world-readable JavaScript bundle by
 * the compiler. Nothing here is fetched from the server at run time, and nothing
 * confidential may ever be added: a value that must stay private, or that varies
 * per deployment, belongs to the API's own configuration or to the reverse proxy.
 *
 * ---------------------------------------------------------------------------
 * Deliberate divergences from the legacy DotNetNuke 4.9.0 deployment, and from
 * the ambient Angular convention, recorded inline rather than absorbed silently.
 *
 * MIGRATION: The `fileReplacements` direction is INVERTED relative to the usual
 * Angular scaffold, in which `environment.ts` holds development values and a
 * production overlay replaces it. Here `environment.ts` IS the production module
 * because only the `development` configuration declares a replacement, exactly as
 * summarised above. That is why `production` reads `true` and `apiBaseUrl` is the
 * relative path in THIS file, and it is also why a reader who assumes the usual
 * direction will get both values backwards. The polarity is deliberate: the
 * default build is the one that must be correct in a container, and the overlay is
 * the local-development special case — the same posture the legacy pair took, in
 * which `Website/release.config` was the deployed file and
 * `Website/development.config` the developer's substitute.
 *
 * MIGRATION: The twin-file arrangement is a DISCOVERED legacy pattern, not an
 * invention. The legacy application shipped `Website/release.config` beside
 * `Website/development.config`, and a structural diff of that pair shows it
 * differed on exactly four things: one build-posture flag (`debug="false"` at
 * release.config L125 versus `debug="true"` at development.config L123), the
 * code-access-trust block being commented out in release and active in
 * development, one `machineKey` validation value, and one infrastructure binding
 * (`objectQualifier=""` at release.config L354 versus `objectQualifier="dnn_"` at
 * development.config L352). This pair mirrors that discipline exactly: it differs
 * on ONE infrastructure binding (`apiBaseUrl`) and ONE build-posture flag
 * (`production`). Nothing else may ever differ between the two files.
 *
 * MIGRATION: `apiBaseUrl` is RELATIVE here by deployment requirement. The legacy
 * application was same-origin by construction — IIS served the pages and handled
 * the postbacks — and this split stack preserves that property through a reverse
 * proxy instead of through co-hosting: `docker/nginx.conf` L135-136 forwards
 * `location /api/` to `http://api:8080/api/`, so the browser reaches the API
 * through the very origin that served the application. `api` is a compose service
 * name resolved by Docker's embedded DNS and does not resolve in a browser. See
 * the warning on the value itself, which is the durable guard.
 *
 * MIGRATION: The legacy `SiteSqlServer` connection string
 * (`Website/release.config` L21-26) deliberately has no counterpart in this module.
 * It became the API's own server-side `Default` configuration entry, supplied to the
 * container as an environment variable, so the database is never addressable from a
 * browser and its location is never disclosed to one.
 *
 * MIGRATION: The legacy `machineKey` material (`Website/release.config` L89-93,
 * a committed `decryptionKey` [redacted] under `decryption="3DES"`) has NO
 * counterpart in this client bundle by design. Note precisely what the legacy twins
 * did: the development twin (`Website/development.config` L88-92) committed the
 * IDENTICAL key value, so the twin-file convention isolated infrastructure bindings
 * but emphatically did not isolate confidential material. That half of the precedent
 * is deliberately NOT reproduced. Together with the legacy provider settings that
 * stored credentials reversibly and permitted their retrieval (release.config
 * L232-246), it is the exact anti-pattern this migration removes: credential
 * verification is now one-way and server-side, and retrieval is gone. Confidential
 * values live in the API's configuration, never in a browser bundle.
 *
 * MIGRATION: The legacy configuration declared 14 `defaultProvider` attributes,
 * each selecting a reflection-resolved implementation for data access, caching,
 * logging, membership, roles, profiles and the rest. That indirection is REMOVED
 * rather than reproduced: the API binds strongly-typed options classes through
 * dependency injection, and the browser needs none of that machinery. Hence a
 * handful of values here where the legacy deployment carried several hundred.
 * ---------------------------------------------------------------------------
 */

/**
 * The shape both environment modules must satisfy.
 *
 * Declared here rather than in a third file so the folder holds exactly the two
 * twin modules. `environment.development.ts` annotates itself against this
 * interface through a type-only import, which is what makes a divergence between
 * the two a compile error instead of a run-time surprise after a substitution.
 *
 * Every member is `readonly`: the values are build-time constants, so an
 * accidental assignment at run time should not compile.
 */
export interface AppEnvironment {
  /**
   * Whether this bundle was produced by the production configuration.
   *
   * Used only to gate development-only affordances. It never changes a business
   * rule, so both bundles behave identically against the same API.
   */
  readonly production: boolean;

  /**
   * The base path every API request is issued against.
   *
   * MUST stay RELATIVE in this file. The reasoning and the failure mode are on
   * the value below, where a future editor will actually read them.
   */
  readonly apiBaseUrl: string;

  /**
   * The application name rendered in the shell's banner band.
   *
   * Held here rather than hard-coded into a template so the one identity string
   * has a single origin and the header stays presentational. Consumed as the
   * default of an `@Input` by both `layout/shell/shell.component.ts` and
   * `layout/header/header.component.ts`, so it is a required member of this
   * contract rather than a speculative addition, and it must carry an identical
   * value in both twin modules — it is not environment-specific.
   */
  readonly applicationName: string;
}

/**
 * The production environment: the values that ship inside the container image.
 */
export const environment: AppEnvironment = {
  production: true,

  // MIGRATION: RELATIVE, and it must stay relative. `docker/nginx.conf` L135-136
  // proxies `/api/` to `http://api:8080/api/` on this very origin, and L191 serves
  // `index.html` for everything else, so the SPA and the API share one origin, no
  // request is cross-origin, and the bundle is portable to any host name the proxy
  // is served under. An absolute value type-checks, lints, bundles and deploys
  // without a single complaint and leaves both containers reporting healthy, so
  // nothing in the toolchain detects the substitution — but what breaks depends on
  // which absolute value is written, and the difference is worth knowing:
  //   * a compose service name such as 'http://api:8080/api/v1' resolves only
  //     inside the Docker network, so every call fails in the browser;
  //   * 'http://localhost:8080/api/v1' works from a browser on the machine that
  //     published the API's port, because `Cors:AllowedOrigins` admits
  //     'http://localhost:4200' and the published port is reachable — and then
  //     fails for every other client, because their `localhost` is not this host;
  //   * any other absolute host bypasses the proxy and becomes a cross-origin
  //     request, which succeeds only if that exact origin is listed in
  //     `Cors:AllowedOrigins` on the API.
  // In none of the three is the value portable, which is the point. Change it only
  // in lockstep with the proxy configuration that serves the bundle and with the
  // API's configured origin list.
  apiBaseUrl: '/api/v1',

  applicationName: 'DotNetNuke Administration',
};
