import { APP_BASE_HREF } from '@angular/common';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { type ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import {
  PreloadAllModules,
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
  withPreloading,
} from '@angular/router';

import { APP_ROUTES } from './app.routes';
import { appBaseHref } from './core/config/tenant-path';
import { correlationIdInterceptor } from './core/interceptors/correlation-id.interceptor';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';

/**
 * The application's SINGLE provider composition root.
 *
 * `src/main.ts` passes the object below to `bootstrapApplication` whole, and it is
 * the only argument that carries providers. There is deliberately no second place a
 * provider can be added: `main.ts` declares no inline `providers` array and
 * `app.component.ts` declares no component-level `providers` array, so a provider
 * absent from this file is absent from the application. That is the property this
 * file exists to have — one place to answer "what is configured?".
 *
 * ═══════════════════════════════════════════════════════════════════════════════
 * LEGACY LINEAGE
 * ═══════════════════════════════════════════════════════════════════════════════
 * This module replaces two artefacts of the DotNetNuke 4.9.0 VB.NET Web Forms
 * application. Neither is edited by this migration; both remain in the tree as
 * read-only references, and the line numbers below were measured against them.
 *
 * `Website/Default.aspx.vb` (700 lines) was the request pipeline. EVERY address in
 * the application arrived at that one page, which then decided at RUN TIME both
 * what to render and how it looked:
 *
 *   - `Page_Init` (L499) resolved the requested tab and called `LoadSkin` (L217),
 *     which loaded the tenant's skin control dynamically and injected it into the
 *     single `<asp:PlaceHolder ID="SkinPlaceHolder">` the document declared
 *     (`Website/Default.aspx` L25). Which control answered a given address was
 *     therefore tenant CONFIGURATION rather than application STRUCTURE.
 *   - `ManageStyleSheets` (L355, reached from L587 and L593) assembled an ordered,
 *     cache-backed stylesheet cascade per request.
 *
 * Both are now BUILD-TIME concerns and neither has a provider here. The route table
 * decides what mounts, the bundler emits the stylesheet, and the reverse proxy
 * returns the same document for every path.
 *
 * `Website/release.config` (444 lines) was the configuration surface: fourteen
 * `defaultProvider` attributes, eight HTTP modules (L67-74) and seven handlers
 * (L77-83), plus the `<forms timeout="60">` session window (L146-147) whose value
 * the API's token lifetime preserves. The notes on the providers below record which
 * part of it each one descends from.
 */

// ═════════════════════════════════════════════════════════════════════════════
// ABSENCES, RECORDED BECAUSE AN ABSENCE IS INVISIBLE
// ═════════════════════════════════════════════════════════════════════════════
//
// MIGRATION: NO SERVER-SIDE-RENDERING OR CLIENT-HYDRATION PROVIDER APPEARS HERE,
// AND THAT ABSENCE IS A SECURITY CONTROL RATHER THAN A SIMPLIFICATION. A pristine
// workspace at the pinned framework version reports 48 advisories, of which all but
// one are build-toolchain transitives that never reach the browser bundle. The
// single exception touches a runtime dependency: a high-severity client-hydration
// advisory against the framework core whose affected range covers every release of
// the pinned minor line, with no remedy available inside the major version this
// migration is pinned to. It is UNREACHABLE here for exactly three reasons, and all
// three must stay true — the workspace was scaffolded without server-side
// rendering, the server-rendering platform package is not installed, and no
// hydration provider appears anywhere in the source. Adding one would convert a
// documented non-issue into a real, unpatched exposure, so this is not a provider a
// future editor may add for a rendering experiment. For the same reason `npm audit`
// is deliberately NOT a build gate: a pristine workspace would fail it on day one
// over a vector this application cannot execute.
//
// MIGRATION: NO PROVIDER READS CONFIGURATION AT RUN TIME, and the API's base path is
// why that matters. There is no bootstrap-time configuration fetch and no
// configuration provider of any kind, because `apiBaseUrl` is a COMPILE-TIME
// constant declared in `src/environments/environment.ts` and consumed by
// `core/config/api-endpoints.ts` and the feature services — this file holds no copy
// of it and imports nothing from that module. Its production value is the RELATIVE
// path `/api/v1`, and it must stay relative: `docker/nginx.conf` serves this bundle
// and proxies `/api/` to the API container on the very SAME ORIGIN, so no request is
// ever cross-origin and the bundle is portable to whatever host name the proxy is
// published under. An absolute value type-checks, bundles and deploys with both
// containers reporting healthy — nothing in the toolchain detects the substitution —
// and then fails in the browser, because the compose service name the API answers to
// is resolved by Docker's embedded DNS and does not resolve in a browser at all.
//
// The remaining absences are consequences of the pinned dependency surface, which is
// closed: the animations, service-worker, component-library and localisation
// packages are all absent from it, so a provider drawn from any of them would not
// resolve. There is likewise no external state-store provider — state is framework
// signals held in the per-feature stores under `core/state/` — no module-bridging
// provider helper and no module class anywhere in this workspace to bridge FROM, and
// no global error-handler override, because client-side error reporting is the error
// interceptor below together with `core/services/notification.service.ts`.
export const appConfig: ApplicationConfig = {
  providers: [
    /**
     * Zone-based change detection with event coalescing.
     *
     * Zone-based rather than zoneless because `angular.json` declares
     * `polyfills: ["zone.js"]` for the build target and
     * `["zone.js", "zone.js/testing"]` for the test target. The zoneless provider
     * would contradict that configuration, so it is not used.
     *
     * `eventCoalescing` collapses the several change-detection passes a single user
     * gesture would otherwise schedule — a `pointerdown`/`pointerup`/`click`
     * sequence on one button being the everyday case — into one. Every component in
     * the workspace already declares `ChangeDetectionStrategy.OnPush`, so this
     * reduces the NUMBER of passes without altering what any pass observes, and it
     * changes no rendered output.
     */
    provideZoneChangeDetection({ eventCoalescing: true }),

    /**
     * The base every address the router produces and parses is relative to, taken from
     * the address THIS DOCUMENT WAS SERVED AT rather than from the document's own
     * `<base href>`.
     *
     * ⚠ THIS IS WHAT LETS A CHILD PORTAL'S ADDRESSES SURVIVE A NAVIGATION. The legacy
     * product addressed a child portal by a path segment beneath a shared host name -
     * `Website/admin/Portal/Signup.ascx.vb` L232-L236 composes and stores exactly
     * `domain/segment` - and the reverse proxy serves this one document for every path
     * beneath it. The document declares `<base href="/">` and must keep doing so, because
     * that is what keeps the built assets root-anchored for every tenant; but with the
     * document base alone, the router treats `/child/portals` as the route `child/portals`,
     * matches nothing, and a caller who signed in at `https://host/child` is navigated to
     * `https://host/portals` - the PARENT - by the first link they follow.
     *
     * Providing the base here takes precedence over the document's, so the router strips
     * the prefix before matching and restores it in every address it emits. For the
     * ordinary root deployment the value is `/`, which is exactly what the document
     * declares, so nothing changes for it.
     *
     * The value is a FUNCTION CALL rather than a constant because it is a property of the
     * running document; `core/config/tenant-path.ts` owns the derivation and states the two
     * limits of the rule it applies.
     */
    { provide: APP_BASE_HREF, useFactory: appBaseHref },

    /**
     * The router. All four arguments are load-bearing; each is annotated in place.
     *
     * `APP_ROUTES` is imported under that name rather than under the `routes` alias
     * the same module also publishes. Both names are bindings onto the identical
     * array, so behaviour is the same either way — but `app.routes.ts` documents
     * this file as the consumer of `APP_ROUTES` by name, and that module is not ours
     * to edit, so using the alias would leave its note describing something untrue.
     */
    // MIGRATION: THE ROUTE TABLE REPLACES THE LEGACY PAGE PIPELINE OUTRIGHT. The
    // legacy application had no route table at all — see `LoadSkin` in the header
    // note above — so addresses were opaque and identifier-bearing, and the mapping
    // from an address to the control that answered it lived in tenant data rather
    // than in the application. `APP_ROUTES` makes that mapping application structure
    // instead: it is readable, path-based, and every screen is reached through a
    // dynamic import, so no feature is part of the initial bundle.
    provideRouter(
      APP_ROUTES,

      /**
       * Delivers route parameters AND route `data` into declared component inputs.
       *
       * Mandatory, and doubly so. It is what lets a routed screen receive
       * `portalId`, `moduleId`, `userId` or `roleId` as a plain declared input
       * instead of injecting `ActivatedRoute` and subscribing — which is what keeps
       * those components presentational and directly constructible in a
       * specification. It is ALSO what binds route `data`, and the catch-all route
       * depends on that second behaviour: it supplies its wording to the reused
       * shared empty-state component through `data: { message: ... }`.
       *
       * Both failure modes are silent. Omitting this feature compiles, builds and
       * serves; the parameterised detail screens and the not-found view simply
       * render without their content, with nothing anywhere to say why.
       */
      withComponentInputBinding(),

      /**
       * Puts the viewport at the top of each newly activated screen.
       */
      // MIGRATION: THIS RETIRES A HIDDEN FORM FIELD RATHER THAN PORTING IT. The
      // legacy document carried `<input id="ScrollTop" runat="server"
      // name="ScrollTop" type="hidden" />` (`Website/Default.aspx` L26) for the sole
      // purpose of carrying scroll position across a full-page postback. Every
      // legacy navigation WAS such a postback, so each one landed at a known offset.
      // A single-page application has no postback, and the router's DEFAULT is to
      // leave the viewport wherever the previous screen left it — which on a long
      // administration form means arriving part-way down a screen whose heading is
      // off-screen. Declaring `'top'` reproduces the legacy behaviour by intent
      // instead of by accident, and needs no field in the document to do it.
      //
      // ⚠ THIS IS ALSO THE ANSWER TO A REPORTED FINDING, AND THE FINDING IS DECLINED
      // HERE RATHER THAN IN THE SCREEN THAT RAISED IT. A review of the roles listing
      // reported that returning with BACK does not restore the scroll offset the
      // listing was left at. That is true, and it is this declaration doing it, ON
      // PURPOSE: `'enabled'` is the setting that would restore the offset, and the
      // project plan names `scrollPositionRestoration: 'top'` explicitly as the
      // router configuration for this application. Changing it to satisfy one screen
      // would change arrival behaviour on all twenty-five routes, including every
      // long form, so the setting stands and the finding is recorded as a deliberate
      // divergence. The half of that finding which WAS a defect — that returning
      // re-walked the whole listing instead of the page it was left on — is fixed,
      // by the listing carrying its page in its own address.
      withInMemoryScrolling({ scrollPositionRestoration: 'top' }),

      /**
       * Fetches every lazily-declared feature bundle in the background once the
       * initial navigation has settled.
       *
       * Required by the project's non-functional requirements, which place the
       * preloading strategy in this file specifically. Lazy declaration is what
       * keeps the initial payload inside its budget; preloading is what stops that
       * economy from being paid for again on the first navigation into each feature.
       *
       * Eager preloading is the right trade HERE and would not be everywhere: this
       * is an administration console reached by an authenticated operator who will
       * visit several screens in one session, so the bundles are very likely to be
       * wanted. A public site with a single landing page would choose differently.
       *
       * ⚠ THE FRAMEWORK'S BUILT-IN, NAMED EXACTLY AS THE PROJECT PLAN NAMES IT, and a
       * bespoke substitute for it is deliberately NOT used. An earlier revision
       * installed a session-gated strategy of its own, on the grounds that
       * `PreloadAllModules` begins fetching as soon as the FIRST navigation settles —
       * which for an anonymous visitor is the sign-in screen, so 163,918 bytes,
       * 54.49% of the application's JavaScript, reached anyone who could reach that
       * screen, carrying every administration route name with it. The measurement was
       * real; the conclusion was not this file's to draw. The plan fixes the router
       * configuration verbatim as
       * `provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling(…), withPreloading(PreloadAllModules))`,
       * and a bundle name is not authority: every one of those routes is refused by
       * its own gate and re-authorised server-side, so what was downloaded could not
       * be used. Substituting a different strategy traded a frozen specification for
       * a bandwidth saving, which is the wrong direction. Should the exposure be
       * judged to outweigh the specification, that is an amendment to the plan rather
       * than a local decision here.
       */
      withPreloading(PreloadAllModules),
    ),

    /**
     * The HTTP client and its interceptor chain.
     *
     * Interceptors are FUNCTIONS in this version of the framework
     * (`HttpInterceptorFn`), so they are listed directly. They are deliberately not
     * registered through the injector-resolved interceptor feature, and none is a
     * class behind a multi-provider token; neither mechanism appears anywhere in
     * this workspace.
     */
    // MIGRATION: THE LEGACY REQUEST PIPELINE COLLAPSES INTO THIS CHAIN PLUS THE
    // ROUTER ABOVE. `Website/release.config` registered eight HTTP modules at L67-74
    // — script handling, compression, request filtering, URL rewriting, exception
    // handling, users-online tracking, membership and personalisation — and seven
    // handlers at L77-83, for the script-resource, application-service and
    // web-service endpoints, sign-off, syndication, link tracking and the CAPTCHA
    // image. Each was a class named in a configuration string and resolved by
    // reflection at start-up. Almost none of it survives as client-side code:
    // compression, filtering and rewriting are the reverse proxy's responsibility,
    // exception handling and audit logging are the API's, and the CAPTCHA is
    // deliberately gone — the API's rate limiter on its credential endpoints, which
    // refuses with 429, is the compensating control. What genuinely belonged to the
    // caller is the three interceptors below, and nothing else.
    //
    // MIGRATION: FOURTEEN REFLECTION-RESOLVED REGISTRATIONS BECOME EXPLICIT
    // PROVIDERS. `Website/release.config` declared fourteen `defaultProvider`
    // attributes — at L218, 258, 299, 325, 335, 345, 359, 374, 386, 397, 411, 419,
    // 427 and 435 — each naming an implementation for data access, caching, logging,
    // membership, roles, profiles, scheduling, search, navigation, friendly URLs and
    // the HTML editor, to be instantiated by reflection from a type name held in a
    // string. That indirection is REMOVED rather than reproduced: the API binds
    // strongly-typed options through dependency injection, and on this side every
    // dependency is a `provide*` call written in this file, so one that cannot be
    // resolved is a compile error instead of a start-up failure. A browser needs
    // almost none of that machinery, which is why a handful of providers stand in
    // for several hundred lines of configuration.
    //
    // ═══════════════════════════════════════════════════════════════════════════
    // MIGRATION: THE ORDER OF THIS ARRAY IS LOAD-BEARING, AND THE REASON IS NOT THE
    // ONE THE MIGRATION PLAN GIVES. The plan justifies putting error translation last
    // by saying it then "observes the final response, after any auth retry has been
    // attempted". Under the framework's actual chain semantics that is impossible.
    // The order is kept — but for a different, real reason, and the correction is
    // recorded here rather than quietly inherited.
    //
    // `withInterceptors([A, B, C])` composes as `A(next = B(next = C(next =
    // backend)))`. The listed order is therefore the order on the way OUT and its
    // REVERSE on the way back:
    //
    //     request:   correlationId -> auth -> error -> backend
    //     response:  backend -> error -> auth -> correlationId
    //
    //   1. correlationIdInterceptor is OUTERMOST on the request path, which is the
    //      whole point of listing it first. It stamps `X-Correlation-Id` before the
    //      auth interceptor adds `Authorization` and before anything downstream can
    //      retry, clone or fail the request, so every request that leaves this
    //      application carries an identifier — including a retry, which is cloned
    //      from the already-stamped original and is thereby identifiable as the same
    //      logical operation rather than a second, unrelated one. The API's
    //      correlation middleware reads the header, echoes it back under the same
    //      name, and carries the value in `ProblemDetails.correlationId`. That is the
    //      member to quote in a support report; `ProblemDetails.traceId` is a
    //      DIFFERENT identifier and agrees with this one only by coincidence.
    //   2. authInterceptor sits in the MIDDLE and owns the entire 401 lifecycle:
    //      attaching the token, the single refresh, the single retry, and discarding
    //      the session when that refresh fails.
    //   3. errorInterceptor is last in the array and therefore INNERMOST on the
    //      response path. It sees the RAW response FIRST — before the auth
    //      interceptor that surrounds it can attempt anything — and it sees the retry
    //      too, so BOTH attempts pass through it rather than only the last. It
    //      consequently cannot be the place a 401 is announced: doing so would tell
    //      an operator their session had expired at the very moment it was being
    //      renewed for them, and would report a refused sign-in a second time in a
    //      place they cannot act on. Ownership is reassigned instead of reordering
    //      the array, and `error.interceptor.ts` returns silently on 401 as its half
    //      of that agreement.
    //
    // So the array is NOT rearranged to make the plan's description true, because
    // stamping the correlation identifier outermost is the property actually worth
    // having. Reordering these three neither fails to compile nor fails a type check
    // — it produces a working application that reports spurious session errors and
    // stamps retries inconsistently. Each interceptor annotates its own position, and
    // their specifications assert this order rather than leaving it to this comment.
    // ═══════════════════════════════════════════════════════════════════════════
    provideHttpClient(
      withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor]),
    ),
  ],
};
