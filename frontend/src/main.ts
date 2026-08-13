import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

// The rejection handler is deliberately present and deliberately minimal. An unhandled rejection at this
// point surfaces only as a generic browser warning that gives no hint the application never started - the
// page simply stays blank.
bootstrapApplication(AppComponent, appConfig).catch((err: unknown) => {
  console.error('The application failed to start.', err);
});
