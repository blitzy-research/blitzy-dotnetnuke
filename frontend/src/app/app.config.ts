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

// NO SERVER-SIDE-RENDERING OR CLIENT-HYDRATION PROVIDER APPEARS HERE, AND THAT ABSENCE IS A SECURITY
// CONTROL RATHER THAN A SIMPLIFICATION. A pristine workspace at the pinned framework version reports 48
// advisories, of which all but one are build-toolchain transitives that never reach the browser bundle.
export const appConfig: ApplicationConfig = {
  providers: [
    /** Zone-based change detection with event coalescing. */
    provideZoneChangeDetection({ eventCoalescing: true }),

    /**
     * The router's mount point, resolved from the document's own `base` element — the value the DEPLOYMENT
     * authors — and never inferred from the address bar.
     *
     * ⚠ THE PROVIDER IS RETAINED RATHER THAN DROPPED, even though the framework would resolve the same
     * element by itself, because this factory NORMALISES the value: one function reduces the attribute to a
     * prefix, and both the router base and the API base in `core/config/api-endpoints.ts` are built from
     * it, so the two can never disagree about where the application is mounted. `core/config/tenant-path.ts`
     * records the routing defect that the previous address-sniffing factory caused.
     */
    { provide: APP_BASE_HREF, useFactory: appBaseHref },

    /** The router. All four arguments are load-bearing; each is annotated in place. */
    provideRouter(
      APP_ROUTES,

      /**
       * Delivers route parameters AND route `data` into declared component inputs. Mandatory, and doubly
       * so.
       */
      withComponentInputBinding(),

      /** Puts the viewport at the top of each newly activated screen. */
      // ⚠ THIS IS ALSO THE ANSWER TO A REPORTED FINDING, AND THE FINDING IS DECLINED HERE RATHER THAN IN
      // THE SCREEN THAT RAISED IT. A review of the roles listing reported that returning with BACK does not
      // restore the scroll offset the listing was left at.
      withInMemoryScrolling({ scrollPositionRestoration: 'top' }),

      withPreloading(PreloadAllModules),
    ),

    /**
     * The HTTP client and its interceptor chain. Interceptors are FUNCTIONS in this version of the
     * framework (`HttpInterceptorFn`), so they are listed directly.
     */
    provideHttpClient(
      withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor]),
    ),
  ],
};
