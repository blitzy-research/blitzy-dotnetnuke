import { ChangeDetectionStrategy, Component } from '@angular/core';

import { ShellComponent } from './layout/shell/shell.component';

/**
 * The application's root component.
 *
 * Mounts the shell and nothing else. `src/main.ts` bootstraps this component into
 * the `<app-root>` element declared by `src/index.html`, and the shell it renders
 * owns every fixed region of the administration console — skip link, banner,
 * secondary navigation, routed outlet and footer.
 *
 * WHY THE ROOT IS THIS THIN
 * -------------------------
 * A root component that rendered regions directly would put layout in two places:
 * here, and in the shell that also renders layout. Keeping the root to a single
 * element means the shell can be mounted in a specification, in a future
 * authenticated wrapper, or nowhere at all, without the root's contents having to
 * be reasoned about. The paired stylesheet is written to exactly that expectation —
 * its own comment records that "the template mounts a single element" — and it
 * therefore styles only the host box: a block display so that a custom element does
 * not shrink-wrap the shell, and a viewport-height floor so that a route rendering
 * very little still fills the window.
 *
 * NO SESSION IS BOUND, AND THAT IS DELIBERATE
 * -------------------------------------------
 * The shell accepts a signed-in display name, an in-flight flag and a sign-out
 * output, and this component binds none of the three. That is a scope decision, not
 * an omission. The authentication service and its memory-first token store are both
 * present in `core/services`, and the bearer interceptor already consumes them — but
 * nothing in this workspace mounts a login route or an authenticated container, so
 * there is no established session for the chrome to read. Binding one here would
 * mean hard-coding a display name, which is worse than rendering no session cluster
 * at all. Because the shell FORWARDS session facts rather than resolving them, the
 * seam is a single element in this file: when the login screen lands, the
 * authentication service is injected here and the three bindings are added to
 * `app.component.html`, with no change to the shell, the banner, or any of their
 * specifications.
 *
 * Until then the banner correctly renders no session cluster, because the shell
 * passes `undefined` through and the banner treats an absent name as no session.
 * The behaviour is complete for the state the application is in rather than a
 * placeholder for a state it is not.
 *
 * MIGRATION: the legacy root was `Website/Default.aspx`, a single Web Forms page
 * whose code-behind (`Website/Default.aspx.vb`) resolved the tenant, selected a
 * skin, injected that skin's stylesheets and instantiated the requested page's
 * modules — all per request, on the server. Every one of those responsibilities has
 * moved: tenant resolution to the API's alias-resolution middleware, skin selection
 * out of scope with skinning itself, stylesheet composition to the build-time entry
 * point `src/styles.scss`, and page composition to the router. What remains on the
 * client is the mount point this component provides.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ShellComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {}
