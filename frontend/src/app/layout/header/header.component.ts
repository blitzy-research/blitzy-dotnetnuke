import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, booleanAttribute } from '@angular/core';
import { RouterLink } from '@angular/router';

import { environment } from '../../../environments/environment';

/**
 * The identity affordance's destination.
 *
 * Lifted to a named module-scoped constant rather than written inline in the
 * template so that the one link target has a single, greppable origin: the same
 * value would otherwise appear in the template and again in the paired
 * specification, free to disagree.
 */
const BRAND_LINK = '/';

/**
 * The application shell's single `banner` landmark.
 *
 * Renders the band's two clusters — the identity affordance at the leading edge
 * and the session cluster at the trailing edge — against the selector contract
 * that `header.component.scss` publishes in its header:
 *
 * ```text
 * <header>                              the banner landmark
 *   <a class="app-header__brand">        the identity affordance
 *   <div class="app-header__actions">    the trailing session cluster
 *     <span class="app-header__user">    the signed-in account's display name
 *     <button class="app-header__logout"> the sign-out control
 * ```
 *
 * PRESENTATIONAL BY CONSTRUCTION
 * ------------------------------
 * The component injects nothing, reads nothing asynchronous and holds no state
 * beyond its declared inputs. The signed-in account arrives as an input and the
 * sign-out gesture leaves as an output, so the band has no opinion about how
 * either is obtained. That is a deliberate scope decision rather than an omission:
 * the authentication service and its token store are present in `core/services`,
 * but no login route or authenticated container mounts them, so no session has been
 * established for this band to display. A band that injected the authentication
 * surface to obtain one display name would couple the shell's chrome to it, and
 * would be untestable without it.
 * Wiring the input and handling the output belongs to whichever container owns
 * the session — the shell today, the authentication feature when it lands.
 *
 * ACCESSIBILITY
 * -------------
 * The band is a native `<header>`, which is the `banner` landmark whenever it is
 * not nested inside another sectioning element — and the shell renders it as a
 * direct grid child, so it is. No `role` attribute is therefore written, because
 * an explicit `role="banner"` on a native `<header>` is redundant and a redundant
 * role is one more thing that can drift from the element it annotates. The
 * sign-out control is a real `<button type="button">` so that it is reachable and
 * operable from the keyboard with no handler of our own, and it carries a
 * `[disabled]` binding rather than an `aria-disabled` attribute because it must
 * genuinely stop accepting activation while a sign-out is in flight, not merely
 * announce that it will not.
 *
 * MIGRATION: the legacy banner had no equivalent single element. The band was
 * assembled per portal from skin objects — `Website/admin/Skins/Logo.ascx` for the
 * identity affordance and `Website/admin/Skins/User.ascx` plus
 * `Website/admin/Skins/Login.ascx` for the session cluster — composed into a
 * per-portal skin file at run time. Skinning is out of scope for this migration,
 * so the band is a single static component and the per-portal logo image, the
 * per-portal skin selection and the skin-object registry are all deliberately not
 * carried forward. The identity affordance therefore renders the application's
 * name as text rather than a portal-specific image.
 *
 * MIGRATION: the legacy session cluster rendered a REGISTER link beside the
 * sign-in link when the portal's registration mode permitted it
 * (`Website/admin/Skins/Login.ascx.vb`). Public self-registration is an end-user
 * affordance rather than an administration one and the console has no
 * registration screen, so no register affordance is rendered.
 */
@Component({
  selector: 'app-header',
  standalone: true,
  // `RouterLink` only. The band renders no shared component: the identity
  // affordance is a plain anchor and the sign-out control a plain button, both
  // styled entirely by the paired stylesheet and the global form partial, so
  // importing a shared component here would add a dependency with nothing to do.
  imports: [RouterLink],
  templateUrl: './header.component.html',
  styleUrl: './header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HeaderComponent {
  /**
   * The text of the identity affordance.
   *
   * Defaults to the build-time application name so that the band is correct with
   * no binding at all, which is what lets the shell mount it without knowing
   * anything about deployment configuration. Held as a plain field rather than a
   * signal because on-push change detection already re-renders an input-driven
   * template when the input changes, and nothing derives from this value.
   */
  @Input() applicationName: string = environment.applicationName;

  /**
   * The signed-in account's display name, or `undefined` when no account is
   * signed in.
   *
   * `undefined` and the empty string are treated identically by the template's
   * truthiness guard, so a container that has not yet resolved the session and
   * one that resolved it to nothing both render the same band: no name and no
   * sign-out control. The alternative — rendering an empty `<span>` and a
   * sign-out control that cannot work — would put two nodes in the accessibility
   * tree that announce nothing and offer a gesture with no effect.
   */
  @Input() userName?: string;

  /**
   * Whether a sign-out is currently in flight.
   *
   * Declared with `booleanAttribute` so that the bare attribute form
   * (`<app-header signingOut>`) is understood as `true`, matching the shared
   * confirm-dialog component's `danger` input and keeping one convention across
   * the workspace. While set, the control is genuinely disabled, which is what
   * prevents a second sign-out request being issued by an impatient second click.
   */
  @Input({ transform: booleanAttribute }) signingOut = false;

  /**
   * Emitted once per accepted activation of the sign-out control.
   *
   * Carries no payload: the account to sign out is the one the session already
   * holds, and passing the display name back would invite a consumer to treat a
   * presentation string as an identifier.
   */
  @Output() readonly signOut = new EventEmitter<void>();

  /**
   * The identity affordance's route.
   */
  readonly brandLink: string = BRAND_LINK;

  /**
   * Whether an account is signed in, and therefore whether the session cluster
   * has anything to render.
   *
   * A getter rather than a stored flag so that it cannot fall out of step with
   * the input it derives from; it is evaluated only during change detection of a
   * template that is already being re-rendered.
   */
  protected get hasSignedInUser(): boolean {
    return this.userName !== undefined && this.userName.trim().length > 0;
  }

  /**
   * Handles activation of the sign-out control.
   *
   * The in-flight guard is applied HERE as well as through the template's
   * `[disabled]` binding, and the duplication is deliberate: a disabled attribute
   * stops a pointer and a keyboard activation, but a consumer that calls this
   * method directly — or a synthetic event dispatched by a test — would otherwise
   * bypass it. The guard makes "at most one request in flight" a property of the
   * component rather than of its markup.
   */
  protected onSignOutClick(): void {
    if (this.signingOut) {
      return;
    }

    this.signOut.emit();
  }
}
