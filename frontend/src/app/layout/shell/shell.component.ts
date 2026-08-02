import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  booleanAttribute,
  inject,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { environment } from '../../../environments/environment';
import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';

/**
 * The fragment identifier the skip link targets, and therefore the identifier the
 * main region must carry.
 *
 * Held as two derived constants from one source so that the link target and the
 * element identifier cannot drift apart. A skip link whose fragment names no
 * element is worse than no skip link: it is announced, it is reachable, and
 * activating it does nothing.
 */
const MAIN_REGION_ID = 'main-content';

/**
 * The skip link's `href`.
 */
const SKIP_LINK_TARGET = `#${MAIN_REGION_ID}`;

/**
 * The application shell.
 *
 * Composes the four fixed regions of every administration screen — skip link,
 * banner, secondary navigation and footer — around the routed outlet, and is the
 * only component in the workspace that renders a router outlet.
 *
 * SELECTOR CONTRACT
 * -----------------
 * The grid itself is published GLOBALLY by `src/styles/_layout.scss`, not by this
 * component's own stylesheet, because a `:host`-scoped grid cannot place children
 * that the global partial names. `shell.component.scss` therefore compiles to only
 * three rules, and each of the three is a statement about this component's markup:
 *
 * ```text
 * :host(:not(.shell)) { display: block }   the fallback when the host class is missing
 * .shell__skip-link   { color: … }         the skip link's foreground on the primary fill
 * main                { min-inline-size: 0; overflow-wrap: break-word }
 * ```
 *
 * The first of those three is the reason this component declares
 * `host: { class: 'shell' }`. The global partial keys the entire grid off the
 * `.shell` class, so a host without it would lay out as a plain block and every
 * region would stack in source order at full width regardless of viewport. The
 * class is applied through the component's own host metadata rather than left to
 * whichever parent happens to mount it, so the shell cannot be mounted wrongly.
 *
 * The third rule is why the main region is a native `<main>` element rather than a
 * `<div role="main">`: the stylesheet selects the bare element name.
 *
 * PRESENTATIONAL BY CONSTRUCTION
 * ------------------------------
 * The shell injects nothing. The session facts the banner needs — the signed-in
 * account's display name, whether a sign-out is in flight — arrive as inputs and
 * are forwarded straight through, and the sign-out gesture is re-emitted upward
 * unchanged. That is a deliberate scope decision. The authentication service and
 * its token store are present in `core/services`, but no login route or
 * authenticated container mounts them, so no session has been established for the
 * shell to read. A layout that injected the authentication surface to obtain one
 * display name would couple every screen beneath it to that surface. Because
 * the shell forwards rather than resolves, the session can be wired in one place
 * later — the component that mounts the shell — with no change to this file.
 *
 * ACCESSIBILITY
 * -------------
 * Three landmarks are rendered and each is a native element, so none needs an
 * explicit role: `<header>` from the banner component, `<main>` here, `<footer>`
 * from the footer component. The skip link is the first focusable thing in the
 * document, is visually hidden until focused — the global partial does that with
 * `:not(:focus-visible)` rather than `display: none`, so it stays in the tab order
 * — and moves focus into the main region, which carries `tabindex="-1"` so that it
 * can receive programmatic focus without joining the tab order itself.
 *
 * MIGRATION: the legacy shell was not a component at all. `Website/Default.aspx`
 * declared a single content placeholder and `Website/Default.aspx.vb` filled it at
 * run time — `LoadSkin` (L217) selected and instantiated a per-portal skin file
 * chosen from portal settings, and `ManageStyleSheets` (L355) injected that skin's
 * stylesheet links into the page head. Skinning, containers and skin objects are
 * out of scope for this migration, so the run-time skin selection, the per-portal
 * skin registry and the dynamic stylesheet injection are all deliberately not
 * carried forward. The shell is one static composition for every tenant, and the
 * stylesheet is a build-time entry point.
 *
 * MIGRATION: the legacy page also rendered a control panel band above the content
 * for administrators (`Website/admin/ControlPanel/`), which is explicitly out of
 * scope, so the shell has no equivalent region.
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, HeaderComponent, FooterComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The global layout partial keys the shell grid off this class. Applying it here
  // rather than at every mount site makes a correctly laid-out shell the only shell
  // that can exist, and makes the stylesheet's `:host(:not(.shell))` fallback the
  // genuinely exceptional path it is written as.
  host: { class: 'shell' },
})
export class ShellComponent {
  /**
   * The banner's identity text, forwarded to the banner component.
   *
   * Defaults to the build-time application name so that the shell is correct with
   * no binding at all.
   */
  @Input() applicationName: string = environment.applicationName;

  /**
   * The signed-in account's display name, or `undefined` when no account is
   * signed in.
   *
   * Forwarded verbatim; the shell applies no interpretation of its own, so the
   * banner's rule that a blank name means no session holds unchanged whichever
   * component the value is read from.
   */
  @Input() userName?: string;

  /**
   * Whether a sign-out is currently in flight.
   *
   * Declared with `booleanAttribute` so the bare attribute form is understood as
   * `true`, matching the banner input it forwards to.
   */
  @Input({ transform: booleanAttribute }) signingOut = false;

  /**
   * Re-emitted when the banner's sign-out control is activated.
   *
   * The shell adds nothing to the gesture — no confirmation, no state change —
   * because it owns no session to end. Re-emitting rather than handling keeps the
   * decision with whichever component supplied the session in the first place.
   */
  @Output() readonly signOut = new EventEmitter<void>();

  /**
   * The identifier the main region carries, and the target the skip link names.
   */
  readonly mainRegionId: string = MAIN_REGION_ID;

  /**
   * The skip link's fragment target.
   */
  readonly skipLinkTarget: string = SKIP_LINK_TARGET;

  /**
   * This component's own host element, used to locate the main region when the
   * skip link is activated.
   *
   * Queried from the host rather than from `document` so the lookup is scoped to
   * the shell that was activated. A duplicate identifier introduced by a routed
   * feature therefore cannot capture the shell's own skip link.
   */
  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * Forwards the banner's sign-out gesture to this component's own consumer.
   */
  protected onSignOut(): void {
    this.signOut.emit();
  }

  /**
   * Moves keyboard focus to the main region, and cancels the anchor's default
   * action.
   *
   * ⚠ THE CANCELLATION IS LOAD-BEARING, AND IT GUARDS A DEFECT THAT WAS MEASURED
   * RATHER THAN INFERRED. A fragment-only `href` resolves against the document's
   * BASE url, and `index.html` declares `<base href="/">` because deep links
   * require it. From any address below the root the href therefore names a
   * DIFFERENT PATH, and letting it resolve performs a FULL DOCUMENT RELOAD that
   * discards the current screen — observed in a real browser against the
   * production bundle as `/portals/3/settings` becoming `/` with every bundle
   * re-fetched.
   *
   * Two alternatives were evaluated and rejected. `routerLink` with `fragment`
   * navigates without reloading but moves NO focus, which is the one thing the
   * affordance exists to do. Removing `<base href="/">` would fix the resolution
   * and break deep linking.
   *
   * The anchor keeps its `href` regardless: the `href` is what makes it a tab stop
   * and what makes assistive technology announce it as a link.
   */
  protected focusMainRegion(event: Event): void {
    event.preventDefault();

    const mainRegion = this.hostElement.nativeElement.querySelector<HTMLElement>(
      `#${MAIN_REGION_ID}`,
    );

    // `tabindex="-1"` on the region is what makes this call effective; without it a
    // `<main>` is not focusable and focus would stay on the link.
    mainRegion?.focus();
  }
}
