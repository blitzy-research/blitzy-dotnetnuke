/**
 * DEVELOPMENT build-time configuration for the `dnn-migration` Angular workspace.
 *
 * READ THE DIRECTION BEFORE CHANGING A VALUE BELOW, because it is the opposite of
 * the convention most Angular readers carry in their heads. This module is the
 * OVERRIDE, not the default. `environment.ts` is both the default and the
 * production module: it is what every consumer imports and what
 * `ng build --configuration production` ships. This file is substituted in its
 * place only by `ng serve` and `ng build --configuration development`.
 *
 * Verified in `frontend/angular.json` rather than assumed:
 *
 *     build.defaultConfiguration       -> "production"
 *     build.configurations.production  -> fileReplacements: []        (no substitution)
 *     build.configurations.development -> fileReplacements: [ {
 *                                             replace: src/environments/environment.ts,
 *                                             with:    src/environments/environment.development.ts } ]
 *     serve / build targets            -> no proxyConfig, on either target or their configurations
 *     test target                      -> no fileReplacements at all
 *
 * Four consequences are worth internalising before editing.
 *
 * The filename is load-bearing. `angular.json` names it literally, so renaming
 * this module raises no error anywhere: the substitution simply stops happening,
 * `ng serve` falls back to the production relative base path, and every API call
 * fails against the dev server with nothing in the output pointing at the cause.
 *
 * Because the `test` target declares no substitution, Karma specs compile against
 * `environment.ts` and therefore against its relative base path. Specs must assert
 * relative request paths through `HttpTestingController`; a spec that hard-codes
 * the absolute origin below would be asserting against a bundle that never exists
 * under test.
 *
 * This module must be SELF-CONTAINED — it deliberately imports nothing at all. The
 * reason is a consequence of the substitution that is easy to get wrong, and it was
 * established by building rather than by reasoning: because `fileReplacements` maps
 * `./environment` onto THIS module for the whole development compilation, a
 * `import type { AppEnvironment } from './environment'` written here resolves back
 * to this very file, which does not declare that name, and
 * `ng build --configuration development` fails with
 * `TS2724: '"./environment"' has no exported member named 'AppEnvironment'`.
 * Erasure does not save it: `import type` removes the RUNTIME edge, so there is
 * genuinely no runtime cycle, but the compiler still has to resolve the name and it
 * resolves it through the replaced path. The production build and
 * `tsc -p tsconfig.spec.json` both pass regardless, because neither applies the
 * substitution — so the failure appears ONLY in the one configuration this file
 * exists to serve. The contract is therefore restated inline below.
 *
 * Finally, a development build is still a browser build. Everything in this module
 * is inlined into a world-readable JavaScript bundle, so nothing confidential may
 * ever be added here — the divergence notes below record the legacy precedent for
 * doing precisely the wrong thing, and decline it deliberately.
 *
 * ---------------------------------------------------------------------------
 * Deliberate divergences from the legacy DotNetNuke 4.9.0 deployment, and from the
 * ambient Angular convention, recorded inline rather than absorbed silently.
 *
 * MIGRATION: This module is the OVERRIDE, not the default, so the `fileReplacements`
 * direction is INVERTED relative to the usual Angular scaffold in which
 * `environment.ts` holds development values and a production overlay replaces it.
 * Only the `development` configuration declares a replacement, as quoted above, so
 * `environment.ts` IS the production module and this file is the local-development
 * special case. That is why `production` reads `false` and the base path is
 * absolute here, and why a reader who assumes the usual direction will get both
 * values backwards. The polarity is deliberate: the default build is the one that
 * has to be correct inside a container. It is also the posture the legacy pair
 * took, in which `Website/release.config` was the deployed file and
 * `Website/development.config` the developer's substitute.
 *
 * MIGRATION: The absolute origin below is correct HERE AND ONLY HERE, because
 * local development has no shared origin and no proxy to create one.
 * `frontend/angular.json` declares no `proxyConfig` on either target nor on their
 * configurations, and no `proxy.conf.json` exists — verified, and a deliberate
 * choice rather than an omission. The dev server and the API therefore listen on
 * different ports, nothing sits in front of either, and the browser has to address
 * the API host directly. Production inverts this completely: `docker/nginx.conf`
 * serves the bundle and forwards `location /api/` to the API container on the very
 * origin that served the application, which is why `environment.ts` carries a
 * RELATIVE path. See the pointed warning on the value itself — that comment, not
 * this one, is the durable guard.
 *
 * MIGRATION: The twin-file arrangement is a DISCOVERED legacy pattern, not an
 * invention. The legacy application shipped `Website/release.config` beside
 * `Website/development.config`, and this pair preserves that organisation rather
 * than introducing a new one.
 *
 * MIGRATION: The parallel is semantic, not merely cosmetic, and it was measured. A
 * full diff of the legacy twins shows four substantive differences and nothing
 * else: one machineKey `validationKey` value; the code-access-trust block
 * (`<trust level="Medium" originUrl=".*" />`) commented out in release but active
 * in development at development.config L120-121; one build-posture flag
 * (`<compilation debug="false" …>` at release.config L125 versus `debug="true"` at
 * development.config L123); and one infrastructure binding (`objectQualifier=""`
 * at release.config L354 versus `objectQualifier="dnn_"` at development.config
 * L352). The remaining hunks are pure indentation and one comment-only line. This
 * pair mirrors that discipline exactly: it differs on ONE infrastructure binding
 * (`apiBaseUrl`) and ONE build-posture flag (`production`), and NOTHING ELSE MAY
 * EVER DIFFER. Note that `objectQualifier` and `databaseOwner="dbo"` remain
 * server-side concerns and deliberately do not appear here at all — they became
 * Fluent table-mapping defaults in the API's persistence layer, where a browser
 * has no business seeing them.
 *
 * MIGRATION: The legacy `SiteSqlServer` connection string (`Website/release.config`
 * L21-26, repeated as an appSettings entry at L36, and byte-identical in the
 * development twin) deliberately has no counterpart in this module. It became the
 * API's own server-side `Default` database connection string, supplied to the
 * container as an environment variable, so the database is never addressable from
 * a browser and its location is never disclosed to one.
 *
 * MIGRATION: The legacy machineKey material — a committed `decryptionKey` [redacted]
 * under `decryption="3DES"`, at release.config L89-93 — has NO counterpart in this client
 * bundle by design, and the reason to state that in the DEVELOPMENT file rather
 * than only in the production one is precise: the diff of the legacy twins does
 * not list `decryptionKey` at all, which means the development twin
 * (development.config L88-92) committed the IDENTICAL value. The legacy twin-file
 * convention isolated infrastructure bindings but emphatically did not isolate
 * confidential material — it duplicated it, into the very file whose lower stakes
 * made that feel acceptable. Combined with the legacy provider settings that
 * stored credentials reversibly and permitted their retrieval (release.config
 * L239-245), that is the exact anti-pattern this migration removes: credential
 * verification is now one-way and server-side, and retrieval is gone. This file
 * honours the twin-file STRUCTURE and breaks with its committed-material half.
 * "It is only the development configuration" is the reasoning that put reversible
 * cryptographic material into this repository in the first place.
 *
 * MIGRATION: The legacy configuration declared 14 `defaultProvider` attributes
 * (verified: `grep -c 'defaultProvider'` reports 14 in release.config and 14 in
 * development.config), each selecting a reflection-resolved implementation for data
 * access, caching, logging, membership, roles, profiles and the rest. That
 * indirection is REMOVED rather than reproduced: the API binds strongly-typed
 * options classes through dependency injection, and the browser needs none of that
 * machinery. Hence three values here where the legacy pair carried several hundred.
 * ---------------------------------------------------------------------------
 */

/**
 * The shape this module must satisfy, imported from the ONE place it is declared.
 *
 * MIGRATION: this used to be a second `AppEnvironment` interface restated inline, on
 * the reasoning that importing it from `./environment` cannot work under the very
 * build configuration that substitutes this file. That reasoning was correct about
 * `./environment` and wrong about the conclusion. The contract now lives in
 * `./app-environment`, which declares no value and is NEVER substituted, so the
 * type-only import below erases at compile time, leaves nothing in the bundle under
 * either configuration, and cannot interact with the replacement at all.
 *
 * What the inline copy actually cost is worth recording, because it looked harmless.
 * Two independent declarations of one name are compared by the compiler only where a
 * value crosses between them — and no value ever does, because every consumer imports
 * the `environment` VALUE and not the type. So a member added to one twin, dropped from
 * one twin or retyped in one twin produced no diagnostic anywhere, and the drift
 * surfaced only if some consumer happened to read the diverged member. The one member
 * whose correctness nothing can detect that way is `apiBaseUrl`.
 */
import type { AppEnvironment } from './app-environment';

/**
 * The development environment: the values `ng serve` and
 * `ng build --configuration development` compile in place of `environment.ts`.
 *
 * Members appear in the same order as the interface declares them.
 */
export const environment: AppEnvironment = {
  production: false,

  // MIGRATION: ABSOLUTE, and absolute ONLY here. There is no dev-server proxy —
  // `angular.json` declares no `proxyConfig` and no `proxy.conf.json` exists — so
  // the dev server and the API have no shared origin and the browser must reach
  // the API host directly. The API admits this origin through its named
  // cross-origin policy. The mirror value in `environment.ts` is RELATIVE and must
  // stay that way: an absolute value there type-checks, lints, bundles, deploys
  // and leaves both containers reporting healthy, yet breaks every call the moment
  // a browser makes one, because it bypasses the reverse proxy that makes the SPA
  // and the API same-origin and turns each request into a cross-origin one the
  // API's policy is not written to admit. Nothing in the toolchain detects that;
  // the only check that does is a grep of the built production bundle. So never
  // copy this value into `environment.ts`, and never make this module the default
  // by re-export, barrel, conditional import or `angular.json` edit.
  apiBaseUrl: 'http://localhost:8080/api/v1',

  // Identical to the production twin on purpose: the application's identity is not
  // environment-specific, so it is not one of the two permitted divergences.
  applicationName: 'DotNetNuke Administration',
};
