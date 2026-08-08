/**
 * The contract both environment modules satisfy, declared ONCE so the twins cannot drift.
 *
 * ## Why this file exists at all
 *
 * `angular.json` substitutes one environment module for the other under its `development`
 * configuration:
 *
 *     production  -> fileReplacements: []                       (no substitution)
 *     development -> fileReplacements: [ environment.ts -> environment.development.ts ]
 *
 * That substitution is what previously kept the contract from living in one place.
 * `environment.development.ts` could not import the interface from `./environment` — under
 * the very configuration that substitutes it, the module it would be importing from is the
 * one being replaced — so it restated the interface inline, as a second, separately-declared
 * `AppEnvironment` that merely happened to be structurally identical. The production module
 * described that arrangement as a shared type contract, which it was not: two independent
 * declarations of the same name, either of which could gain, lose or retype a member while
 * still compiling, because TypeScript compares them structurally only where a value crosses
 * between them.
 *
 * ⚠ THE DRIFT THAT ARRANGEMENT ADMITTED WAS SILENT ON EXACTLY THE MEMBER THAT MATTERS.
 * Every consumer imports the `environment` VALUE and never the type, so nothing forced the
 * two declarations to be compared. A member added to one twin and not the other, or retyped
 * in one, produced no diagnostic in either file; the failure surfaced only if some consumer
 * happened to read the diverged member, and the one member no consumer reads reactively is
 * the API base path — the value whose correctness is a deployment property rather than a
 * compile-time one.
 *
 * ## Why a THIRD file is safe where importing the twin was not
 *
 * This module declares no value and is never substituted. Both twins import the interface
 * from here with a type-only import, which the compiler erases entirely, so the emitted
 * bundle contains no reference to this file under either configuration and the substitution
 * has nothing to interact with. The folder consequently holds three modules rather than two,
 * and that is the point: the two that are interchangeable, and the one that says what
 * interchangeable means.
 *
 * ## What may and may not be added here
 *
 * Every member is `readonly`, because these are build-time constants inlined into a
 * world-readable bundle: an accidental run-time assignment should not compile. Nothing
 * confidential and nothing that varies per deployment may be added to this contract at all —
 * such a value belongs to the API's own server-side configuration or to the reverse proxy.
 * A member added here obliges BOTH twins to declare it in the same change, which is exactly
 * the coupling this file exists to impose.
 */

/** The shape both `environment.ts` and `environment.development.ts` must satisfy. */
export interface AppEnvironment {
  /**
   * Whether this bundle was produced by the production configuration.
   *
   * Used only to gate development-only affordances. It never changes a business rule, so
   * both bundles behave identically against the same API.
   *
   * ⚠ ONE OF THE TWO PERMITTED DIVERGENCES between the twins: `true` in `environment.ts`
   * and `false` in `environment.development.ts`.
   */
  readonly production: boolean;

  /**
   * The base path every API request is issued against.
   *
   * ⚠ THE OTHER PERMITTED DIVERGENCE, and the only member whose value is load-bearing for
   * deployment. It MUST be the RELATIVE path in `environment.ts`, because `docker/nginx.conf`
   * proxies `/api/` on the origin that served the bundle, and it is absolute ONLY in
   * `environment.development.ts`, where no dev-server proxy exists. The failure mode of
   * getting that backwards is invisible to the whole toolchain — it type-checks, bundles,
   * deploys and leaves both containers reporting healthy — so the reasoning is written out on
   * each value in full, and `core/config/api-endpoints.spec.ts` asserts the production value
   * exactly.
   */
  readonly apiBaseUrl: string;

  /**
   * The application name rendered in the shell's banner band.
   *
   * Held here rather than hard-coded into a template so the one identity string has a single
   * origin and the header stays presentational. Consumed as the default of an `@Input` by
   * both `layout/shell/shell.component.ts` and `layout/header/header.component.ts`, so it is
   * a required member of this contract rather than a speculative addition.
   *
   * ⚠ NOT a permitted divergence: the application's identity is not environment-specific, so
   * the twins must carry an identical value.
   */
  readonly applicationName: string;
}
