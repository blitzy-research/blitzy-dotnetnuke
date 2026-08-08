/**
 * PRODUCTION build-time configuration for the `dnn-migration` Angular workspace.
 *
 * The `fileReplacements` direction is the opposite of the usual Angular scaffold, so read it before changing
 * a value here. `angular.json` declares a replacement under its `development` configuration only, which makes
 * THIS module the production one and `environment.development.ts` the overlay:
 *
 * production  -> fileReplacements: []                       (no substitution)
 * development -> fileReplacements: [ this module -> environment.development.ts ]
 *
 * `ng build --configuration production` - also the build target's `defaultConfiguration` - therefore ships
 * exactly the values written here, while `ng serve` and the development build swap this module out. The `test`
 * target declares no substitution either, so every Karma spec compiles against this file and against the
 * relative base path below.
 *
 * Everything in this module is inlined into a world-readable JavaScript bundle at build time. Nothing here is
 * fetched at run time and nothing confidential may ever be added: a value that must stay private, or that
 * varies per deployment, belongs to the API's configuration or to the reverse proxy.
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
 *
 * MIGRATION: the CONTRACT the two twins satisfy lives in `./app-environment`, and it
 * is imported by both of them rather than declared here. An earlier arrangement
 * exported the interface from THIS module and had the development twin restate it
 * inline — because a substituted module cannot import from the module it replaces —
 * and described the result as a shared type contract. It was not one: two independent
 * declarations of the same name, neither of which the compiler had any reason to
 * compare, because every consumer imports the `environment` VALUE and none imports the
 * type. A member added to one twin and not the other compiled cleanly. The contract is
 * now a third, never-substituted module holding no value at all, so a type-only import
 * from each twin erases completely and the substitution has nothing to interact with.
 * ---------------------------------------------------------------------------
 */

import type { AppEnvironment } from './app-environment';

/**
 * The production environment: the values that ship inside the container image.
 */
export const environment: AppEnvironment = {
  production: true,

  // RELATIVE, and it must stay relative. `docker/nginx.conf` proxies `/api/` to `http://api:8080/api/` on
  // this very origin and serves `index.html` for everything else, so the SPA and the API share one origin,
  // no request is cross-origin, and the bundle is portable to any host name the proxy is served under. An
  // absolute value type-checks, lints, bundles and deploys without a complaint and leaves both containers
  // reporting healthy, so nothing in the toolchain detects the substitution: a compose service name resolves
  // only inside the Docker network, `localhost` works only from the machine that published the API's port,
  // and any other host bypasses the proxy and becomes a cross-origin request that succeeds only if that
  // exact origin is listed in `Cors:AllowedOrigins`. Change it only in lockstep with the proxy configuration
  // that serves the bundle and with the API's configured origin list.
  apiBaseUrl: '/api/v1',

  applicationName: 'DotNetNuke Administration',
};
