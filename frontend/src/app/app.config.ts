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
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { correlationIdInterceptor } from './core/interceptors/correlation-id.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';

/**
 * The application's single composition root.
 *
 * Every provider the application relies on is declared here and nowhere else, so
 * that a reader has one place to answer "what is configured?" and a component
 * never has to declare a provider of its own to obtain framework infrastructure.
 * There is no `NgModule` anywhere in this workspace; every component is
 * standalone and this configuration object is what `bootstrapApplication`
 * consumes.
 *
 * WHY EACH PROVIDER IS PRESENT
 * ----------------------------
 * `provideZoneChangeDetection({ eventCoalescing: true })` — coalescing collapses
 * the several change-detection passes that a single user gesture would otherwise
 * trigger (for example a `pointerdown`/`pointerup`/`click` sequence on one
 * button) into one. Every component in the workspace already declares
 * `ChangeDetectionStrategy.OnPush`, so this is a reduction in the number of
 * passes rather than a change to what any pass observes, and it alters no
 * rendered output.
 *
 * `provideRouter` with three features:
 *   - `withComponentInputBinding()` lets a routed component receive a route
 *     parameter as a plain declared input. Without it every routed screen would
 *     have to inject `ActivatedRoute` and subscribe, which would put routing
 *     infrastructure inside components whose job is presentation.
 *   - `withInMemoryScrolling({ scrollPositionRestoration: 'top' })` puts the
 *     viewport at the top of each newly activated screen. The default is to
 *     leave the scroll position where the previous screen left it, which on a
 *     long administration form means arriving part-way down a screen whose
 *     heading is off-screen.
 *   - `withPreloading(PreloadAllModules)` fetches every lazily-declared feature
 *     bundle in the background once the initial navigation has settled. The
 *     initial payload therefore stays small — which is the reason the features
 *     are lazily declared at all — while a subsequent navigation costs no
 *     network round trip. This is an administration console reached by an
 *     authenticated operator who will visit several screens in a session, so
 *     eager preloading is the right trade; a public site with a single landing
 *     page would choose differently.
 *
 * `provideHttpClient(withInterceptors([...]))` — the client and its interceptor
 * chain. Interceptors are FUNCTIONS in this version of the framework
 * (`HttpInterceptorFn`), not classes, so they are listed directly rather than
 * registered through a multi-provider token.
 *
 * INTERCEPTOR ORDER
 * -----------------
 * The array order is the execution order on the way OUT and the reverse of it on
 * the way back, so the position of each entry is a behavioural decision rather
 * than a formatting one. The correlation-id interceptor is placed FIRST and
 * deliberately so: it stamps an identifier that the API's own correlation
 * middleware reads and echoes into every log line it writes for the request, and
 * stamping first guarantees the identifier is present on the outgoing request
 * before any later interceptor can retry, clone or replace it. The interceptor
 * itself is idempotent — it leaves an existing `X-Correlation-Id` header alone —
 * so a caller that supplies its own identifier keeps it, which is what makes a
 * client-initiated trace possible.
 *
 * The authentication and error-translation interceptors named by the migration
 * plan are NOT listed here, and their absence is stated rather than left to be
 * discovered: neither module exists in this workspace yet. Listing a name that
 * resolves to nothing would not compile, and adding a placeholder implementation
 * to make the list look complete would put an unspecified, untested behaviour on
 * the path of every request. When each is authored it is appended AFTER the
 * correlation-id entry — authentication second so the bearer token is attached
 * to an already-identified request, error translation last so that it observes
 * the final response rather than an intermediate one.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(
      APP_ROUTES,
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'top' }),
      withPreloading(PreloadAllModules),
    ),
    // ORDER IS LOAD-BEARING and matches the order the interceptors are declared
    // in. Interceptors see the outbound request in listed order and the inbound
    // response in reverse, so:
    //
    //   1. correlationIdInterceptor stamps the diagnostic header FIRST, so every
    //      request carries one — including a request that a later interceptor
    //      retries, which is then identifiable as a retry of the same operation.
    //   2. authInterceptor attaches the bearer token SECOND and owns the single
    //      recovery path for a 401.
    //   3. errorInterceptor translates failures LAST, which means it observes the
    //      FINAL response. A 401 that the authentication interceptor recovers by
    //      refreshing and retrying therefore never reaches it, so a person is
    //      never told their session expired when it was renewed for them.
    //
    // Reordering these does not fail to compile and does not fail a type check; it
    // produces a working application that reports spurious errors and stamps
    // retries inconsistently. The order is asserted by the interceptors' own
    // specifications rather than left as a comment alone.
    provideHttpClient(
      withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor]),
    ),
  ],
};
