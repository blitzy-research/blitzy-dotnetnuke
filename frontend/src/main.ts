import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

/**
 * The application entry point, and the ONLY file `tsconfig.app.json` names in its
 * `files` array — so the production build's compilation unit is exactly the
 * transitive closure of this module's imports. A file that nothing here reaches,
 * directly or through a lazy route, is not compiled by the build target at all.
 *
 * `bootstrapApplication` is used rather than `platformBrowserDynamic().bootstrapModule`
 * because there is no `NgModule` in this workspace: the root component is
 * standalone and its providers arrive as the {@link appConfig} object.
 *
 * The rejection handler is deliberately present and deliberately minimal.
 * `bootstrapApplication` returns a promise, and an unhandled rejection from it
 * surfaces only as a browser-level "unhandled promise rejection" with no
 * indication that the application never started — the page simply stays blank.
 * Reporting to the console gives that failure a name. It is NOT routed through
 * the notification service: bootstrap is precisely the moment at which no
 * injector exists yet, so the service could not be resolved, and it is not
 * swallowed either, because a silent catch would hide the only signal there is.
 */
bootstrapApplication(AppComponent, appConfig).catch((error: unknown) => {
  // eslint-disable-next-line no-console -- the injector does not exist yet, so
  // there is no logging seam to use; this is the one console call in the workspace.
  console.error('The application failed to start.', error);
});
