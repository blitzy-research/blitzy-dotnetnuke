import { ChangeDetectionStrategy, Component } from '@angular/core';

import { ShellComponent } from './layout/shell/shell.component';

// ---------------------------------------------------------------------------------------
// FILE HEADER — LEGACY LINEAGE
//
// This file is the client-side successor to the single page every legacy request was
// served through. Two sources, both read from the checkout rather than summarised, and
// both REFERENCE-ONLY — neither is edited by this migration:
//
//   Website/Default.aspx      (30 lines).  The whole document. Line 25,
//                             `<asp:PlaceHolder ID="SkinPlaceHolder" runat="server" />`,
//                             was the SINGLE injection point for the entire page body.
//                             This component, mounting one `<app-shell />`, is its
//                             replacement — same role, one element, nothing else.
//
//   Website/Default.aspx.vb   (700 lines). The code-behind that filled that placeholder.
//                             `Page_Init` (L499) chose a layout and called `LoadSkin`
//                             (L217-L243) at four separate call sites (L510, L518, L537,
//                             L549) depending on how tenant resolution had gone, and
//                             L590 added whatever came back to the placeholder.
//
// Three `// MIGRATION:` notes below record what happened to the responsibilities that
// arrangement carried — two here at file scope, covering layout loading and the page
// lifecycle, and a third inside the component metadata, covering dependency registration.
// They are deliberately the inline form rather than doc prose, because each is a statement
// about the CODE ADJACENT TO IT rather than about the class as a whole.
//
// Also measured, because the opposite assumption is the natural one: the legacy checkout
// contains ZERO master-page files. This generation of the product composed pages from
// skins, so there is no master-page-to-layout mapping owed here or anywhere else.
// ---------------------------------------------------------------------------------------

// MIGRATION: run-time dynamic layout loading is replaced by a static, compile-time shell. None of that
// survives. `imports: [ShellComponent]` below is the entire replacement: one class, resolved by the
// compiler, identical for every request and every tenant. There is no layout registry, no per-portal chrome,
// no theme lookup and no dynamic control loading, because skinning, containers and skin objects are all out
// of scope. That is why this component has no error surface of its own.

// MIGRATION: the Web Forms page lifecycle and its state transport are eliminated outright
// rather than translated. `Website/Default.aspx` wrapped the whole document in ONE
// server-side multipart form (L23: `<dnn:Form ENCTYPE="multipart/form-data"
// autocomplete="off" style="height: 100%">`) carrying two hidden inputs whose only job was
// to smuggle client state across a postback: `ScrollTop` (L26), read and written through
// the code-behind's `PageScrollTop` property (L49-L65) and re-applied on load by a body
// handler wired at L639-L642 (`__dnn_setScrollTop();`), and `__dnnVariable` (L27), the
// client-variable bag. There is no form below, no hidden input, no view state and no round
// trip. Correspondingly this class declares NO lifecycle hook and NO member of any kind —
// no initialise handler, no view-init handler, no event wiring — because there is no
// lifecycle left to hook and nothing left for the root to decide: composition is
// declarative, and the change-detection strategy declared below means a render happens
// when a signal a template reads actually changes rather than when the server decides to
// rebuild the page. Client state now lives in signals, held by the shell and the feature
// stores, and scroll position is restored by the router's own in-memory scrolling,
// configured once in `app.config.ts`. `AJAX.AddScriptManager(Me)` (L210) and
// `RegisterClientScriptInclude("dnncore", "~/js/dnncore.js")` (L213), which the legacy page
// re-registered on EVERY request, are both replaced wholesale by the compiled bundle.

/**
 * The application's root component.
 *
 * Mounts the shell and does nothing else. `src/main.ts` bootstraps this component into the
 * `<app-root>` element declared by `src/index.html`, and the shell it renders owns every
 * fixed region of the administration console — skip link, banner, secondary navigation,
 * routed outlet and footer — as well as the session those regions present.
 *
 * WHY THE ROOT IS EMPTY
 * ---------------------
 * ⚠ THIS CLASS HAS NO BODY, AND THE EMPTINESS IS THE CONTRACT RATHER THAN AN OVERSIGHT.
 * The required composition is an empty root mounting one shell, and the shell composing
 * header, navigation, the routed outlet and footer. A root that rendered regions directly
 * would put layout in two places; a root that resolved the session would put the chrome's
 * state boundary above the chrome. Both were true of an earlier revision of this file,
 * which imported the navigation rail, projected it into a slot the shell published, and
 * held the display name, the in-flight sign-out flag and the sign-out handler. All of it
 * now lives in `layout/shell/shell.component.ts`, which is where the specified structure
 * puts it, and this component is the mount point that structure asks for and nothing more.
 *
 * Keeping the root to a single element also means the shell can be mounted in a
 * specification, or nowhere at all, without the root's contents having to be reasoned
 * about. The paired stylesheet is written to exactly that expectation — its own comment
 * records that "the template mounts a single element" — and it therefore styles the host
 * box and nothing else: `display: block`, so that a custom element, which has no
 * user-agent display and would otherwise lay out inline, does not shrink-wrap the shell.
 *
 * It declares NO viewport-height floor, and that is a decision rather than an omission. Full height belongs to
 * exactly one authority: `.shell` in `styles/_layout.scss`, which sets `min-block-size: 100vh` and then
 * `100dvh` so an engine that understands the dynamic unit measures the VISUAL viewport. Restating `100vh` on
 * this host would silently defeat that, because a parent minimum cannot be reduced by a child - on a mobile
 * browser whose toolbar has retracted the root would stay taller than the visible area and push a scroll range
 * onto a page that already fits.
 *
 * WHERE THE SESSION LIVES NOW
 * ---------------------------
 * In the shell. It reads the signed-in identity from `AuthStore`, hands the banner the
 * name and the in-flight flag, and turns the banner's gesture into an ended session
 * followed by a navigation to the sign-in screen — with the discard itself owned by
 * `core/state/session-lifecycle.service.ts`, the one place a session ends. Nothing about
 * that chain passes through this file, so nothing here can disagree with it.
 *
 * MIGRATION: the legacy root was `Website/Default.aspx`, a single Web Forms page whose
 * code-behind (`Website/Default.aspx.vb`) resolved the tenant, selected a skin, injected
 * that skin's stylesheets and instantiated the requested page's modules — all per
 * request, on the server. Every one of those responsibilities has moved: tenant
 * resolution to the API's alias-resolution middleware, skin selection out of scope with
 * skinning itself, stylesheet composition to the build-time entry point
 * `src/styles.scss`, and page composition to the router. What remains on the client is
 * the mount point this component provides.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  // The standalone flag above is not decoration. There is no Angular module anywhere in
  // this workspace, so the root is bootstrapped as a component and nothing declares it.
  //
  // ⚠ `ShellComponent` must be listed even though the template mounts it as a single
  // element and nothing here reads it. `strictTemplates` is enabled, so an unimported
  // selector is a COMPILE ERROR in `app.component.html` rather than a silently inert
  // element — which is the failure mode that would otherwise render a blank page.
  //
  // ⚠ AND IT IS THE ONLY ENTRY. The navigation rail is NOT imported here: the shell
  // imports and renders it, so listing it again would either be unused — which the
  // compiler reports — or invite a second mount of a region that must exist once.
  imports: [ShellComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // MIGRATION: this metadata deliberately registers NOTHING with the injector, and the absence is
  // load-bearing. Every dependency the application needs is declared exactly once, in `appConfig` in
  // `app.config.ts`, which `src/main.ts` hands whole to `bootstrapApplication`. Registering anything here
  // would open a SECOND injector scope beneath the application's own and give this component's subtree -
  // which is the whole application - a private, divergent copy, so a service intended to be shared would
  // silently exist twice. The legacy per-request, configuration-driven indirection collapses into that
  // single composition root, and this file's one obligation is to not reopen it.
})
export class AppComponent {}
