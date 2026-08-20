import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';
import { resolveTenantPathBase } from './app/core/config/tenant-resolution';
import { environment } from './environments/environment';

// THE TENANT PATH PREFIX IS SETTLED BEFORE THE APPLICATION STARTS, AND IT HAS TO BE HERE RATHER THAN IN AN
// INITIALISER. `app.config.ts` supplies the prefix as the router's `APP_BASE_HREF`, which the location
// strategy reads when the router is constructed during bootstrap - so a decision taken any later would
// arrive after the base href had already been fixed and after the first navigation had been resolved
// against it. `resolveTenantPathBase` costs no request at all for the bare host and for every one of the
// console's own routes, and it never rejects: every failure mode - including an unreachable API - resolves
// to a decision, so this chain cannot be the reason a document fails to bootstrap.
resolveTenantPathBase(environment.apiBaseUrl)
  .then(() => bootstrapApplication(AppComponent, appConfig))
  // The rejection handler is deliberately present and deliberately minimal. An unhandled rejection at this
  // point surfaces only as a generic browser warning that gives no hint the application never started - the
  // page simply stays blank.
  .catch((err: unknown) => {
    console.error('The application failed to start.', err);
  });
