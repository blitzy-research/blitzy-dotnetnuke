// MIGRATION: This file replaces the Web Forms page lifecycle of Website/Default.aspx and its code-behind
//   Website/Default.aspx.vb. The legacy page derived from DotNetNuke.Framework.DefaultPage and re-assembled
//   itself on the server for every single request; this application is composed once, in the browser, and
//   thereafter navigates client-side without returning to the server for markup.
// MIGRATION: Two legacy injection seams disappear with that page. LoadSkin (Default.aspx.vb L217), which
//   loaded a skin control into the SkinPlaceHolder, becomes the static shell under src/app/layout/**; and
//   ManageStyleSheets (Default.aspx.vb L355), which filled the StylePlaceholder / CSS placeholder pair,
//   becomes the global stylesheet bundle that the build injects into index.html. ViewState and the postback
//   round trip are not translated at all - client state is held in signals under src/app/core/state - and the
//   multipart dnn:Form, the hidden SkinError label and the ScrollTop / __dnnVariable inputs have no successor.
// MIGRATION: No server-rendering or client-hydration provider is registered, here or anywhere in this
//   workspace, and that absence is deliberate and load-bearing rather than an oversight: it is what keeps the
//   single known runtime advisory against the pinned Angular line unreachable. Do not add one without first
//   revisiting that assessment.
// MIGRATION: Every provider is declared exactly once, in appConfig, superseding the web.config chain of 8 HTTP
//   modules (release.config L95-L105) and 7 handlers (L106-L120) whose true successor is the API middleware
//   pipeline, not this shell. Nothing is configured in this file, so nothing here can drift out of step with
//   the composition root.

import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

// The rejection handler is deliberately present and deliberately minimal. An unhandled rejection at this
// point surfaces only as a generic browser warning that gives no hint the application never started - the
// page simply stays blank. It is not routed through the notification service, because a failure to bootstrap
// is precisely the moment at which no injector exists yet to resolve one, and it is not swallowed, because
// this is the only signal a reader gets.
bootstrapApplication(AppComponent, appConfig).catch((err: unknown) => {
  console.error('The application failed to start.', err);
});
