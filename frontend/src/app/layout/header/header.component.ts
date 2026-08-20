import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, booleanAttribute } from '@angular/core';
import { RouterLink } from '@angular/router';

import { environment } from '../../../environments/environment';

/**
 * The identity affordance's destination. Lifted to a named module-scoped constant rather than written
 * inline in the template so that the one link target has a single, greppable origin: the same value would
 * otherwise appear in the template and again in the paired specification, free to disagree.
 */
const BRAND_LINK = '/';

const ACCOUNT_PROFILE_LABEL = 'Manage Profile';

/** The caption of the own-password affordance. */
const ACCOUNT_PASSWORD_LABEL = 'Manage Password';

// MIGRATION: the legacy banner had no equivalent single element, and no component
// stylesheet to port, because the band was not a styleable unit. It was assembled
// per request by `Website/Default.aspx.vb` `LoadSkin` (L217): L224 resolved a
// per-portal skin file with `LoadControl("~" & SkinPath)`, L226 executed any server
// logic inside it with `ctlSkin.DataBind()`, L240 read a package-level doctype with
// `SetSkinDoctype(SkinPath)`, and L590 injected the result into the
// `<asp:PlaceHolder ID="SkinPlaceHolder">` declared at `Website/Default.aspx` L25.
// A skin that failed to load reported itself only to administrators, role-gated
// behind `PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)` and
// escaped through `Server.HtmlEncode(exc.Message)`. Skinning and containers are out
// of scope, so this is ONE static compile-time component: no runtime skin
// selection, no skin registry, no theme selector, and no per-portal logo image —
// the identity affordance renders the application's name as text instead.
//
// MIGRATION: the cached, ordered, twice-executed runtime stylesheet cascade is
// replaced by one build-time bundle plus the paired component-scoped sheet.
// `ManageStyleSheets` (L355) read `DataCache.GetCache("CSS")` into a `Hashtable` at
// L360 and appended, in order, `default.css` at L368, the skin package's
// `skin.css` existence-probed through `Server.MapPath` at L373-L374, the skin-file
// stylesheet, then `portal.css` at L402-L405, with the cache writes gated on
// `Common.Globals.PerformanceSetting` at L378 and L394. It ran TWICE, straddling
// the skin injection: L587 `ManageStyleSheets(False)`, L590 the placeholder add,
// L593 `ManageStyleSheets(True)`, L596 `ManageFavicon()`. The target declares one
// global stylesheet at build time, so no ordering can vary per request.
//
// MIGRATION: view state, the multipart form and the hidden round-trip inputs are
// eliminated outright. `Website/Default.aspx` L23 wrapped the entire page in
// `<dnn:Form ENCTYPE="multipart/form-data" autocomplete="off">`, and L26-L27
// declared the hidden `ScrollTop` and `__dnnVariable` inputs that carried scroll
// position and client state across every postback — `Default.aspx.vb` L58-L65
// exposed `PageScrollTop` over the former and L640-L642 restored it on load with
// `__dnn_setScrollTop()`. This band posts nothing: it holds its state in the
// component and its consumer holds the session, so there is no round trip to
// preserve anything across. Scroll restoration is a router concern now, configured
// once where the router is provided.
//
// MIGRATION: the excluded legacy header chrome is deliberately not ported. The
// measured skin declares thirteen skin objects — USER, LOGIN, LOGO, NAV, SEARCH,
// LANGUAGE, BREADCRUMB, TEXT, LINKS, STYLES, TERMS, PRIVACY and COPYRIGHT — and
// `Website/admin/Skins/` ships 24 such controls in total. Only the two session
// objects are carried forward. The language selector, search box, breadcrumb, logo
// image, navigation menu, banner, tab strip and control-panel band all belong to
// features this migration places out of scope, so the band renders none of them
// and no placeholder stands in for them.
//
// MIGRATION: signing out has no stateless server-side counterpart. The legacy
// gesture was a FULL SERVER REDIRECT — `Website/admin/Skins/Login.ascx.vb` L120
// issued `Response.Redirect(NavigateURL(PortalSettings.ActiveTab.TabID, "Logoff"))`
// — resolving to the single tree-wide `FormsAuthentication.SignOut` site, which
// cleared a cookie and took effect at once. A signed bearer token cannot be
// recalled once issued, so the target gesture is refresh-token revocation plus
// client-side discard: the endpoint revokes only the refresh token, keeps no
// deny-list and answers 204 whatever it finds. Because token custody is held in
// memory rather than persisted, a full page reload legitimately requires
// re-authentication. This band therefore reports the gesture and does not perform
// it; nothing here clears a token or navigates, and expiry-driven sign-out is owned
// entirely by the request interceptor that detects it.
//
// MIGRATION: the legacy session cluster rendered a REGISTER affordance beside the
// sign-in link, and it is deliberately not carried forward. `User.ascx.vb` gated it
// twice — L92 on the portal's registration mode being anything other than
// no-registration, and L101 on the user count being below the portal's user quota
// (or the quota being zero) — with the caption "Register" taken from
// `App_LocalResources/User.ascx.resx` L42-L43. Public self-registration is an
// end-user affordance rather than an administration one and the console has no
// registration screen, so no register affordance is rendered and neither gate has
// anything to govern.
//
// MIGRATION: the sign-out affordance is a BUTTON, where the legacy affordance was a
// link. The legacy control navigated to a "Logoff" address, and the target route
// table deliberately declares no sign-out route, because ending a session is a
// command rather than a destination. A link to a route that does not exist would be
// announced as a link and would navigate nowhere.
//
// MIGRATION: the identity affordance is retained as legacy parity but resolves to
// the application root rather than to a profile screen. In the legacy band the
// identity WAS the profile link: `User.ascx.vb` L110-L114 captioned the control
// with `objUserInfo.DisplayName` and gave it the tooltip "Click Here To Edit Your
// Account Profile" (`User.ascx.resx` L45-L46), and L149-L155 navigated to
// `NavigateURL(ActiveTab.TabID, "Profile", "UserID=" + objUserInfo.UserID)`. The
// paired template and stylesheet publish a two-child cluster with no anchor around
// the display name, so the profile destination is not reachable from this file
// alone; it belongs with the markup that would carry it.
@Component({
  selector: 'app-header',
  standalone: true,
  // `RouterLink` only, and it is mandatory rather than optional: the paired template binds `[routerLink]`,
  // and under strict template checking a directive used but not imported is a compile error rather than a
  // silent no-op.
  imports: [RouterLink],
  templateUrl: './header.component.html',
  styleUrl: './header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HeaderComponent {
  /**
   * The text of the identity affordance. Defaults to the build-time application name so that the band is
   * correct with no binding at all, which is what lets the shell mount it without knowing anything about
   * deployment configuration.
   */
  @Input() applicationName: string = environment.applicationName;

  /**
   * The signed-in account's display name, or `undefined` when no account is signed in. `undefined` and
   * the empty string are treated identically by {@link HeaderComponent.hasSignedInUser}, so a container
   * that has not yet resolved the session and one that resolved it to nothing both render the same band:
   * no name and no sign-out control.
   */
  @Input() userName?: string;

  /** The address of the signed-in account's own profile screen, or `undefined`. */
  @Input() accountProfileLink?: string;

  /** The address of the signed-in account's own change-password screen, or `undefined`. */
  @Input() accountPasswordLink?: string;

  /** Whether a sign-out is currently in flight. */
  @Input({ transform: booleanAttribute }) signingOut = false;

  /** Emitted once per accepted activation of the sign-out control. */
  @Output() readonly signOut = new EventEmitter<void>();

  /** The identity affordance's route. */
  readonly brandLink: string = BRAND_LINK;

  /** The caption of the own-profile affordance. */
  readonly accountProfileLabel: string = ACCOUNT_PROFILE_LABEL;

  /** The caption of the own-password affordance. */
  readonly accountPasswordLabel: string = ACCOUNT_PASSWORD_LABEL;

  /**
   * Whether an account is signed in, and therefore whether the session cluster has anything to render. A
   * getter rather than a stored flag so that it cannot fall out of step with the input it derives from;
   * it is evaluated only during change detection of a template that is already being re-rendered.
   */
  protected get hasSignedInUser(): boolean {
    return this.userName !== undefined && this.userName.trim().length > 0;
  }

  /**
   * Whether the own-profile affordance can be rendered. Presence is decided by an explicit comparison
   * against `undefined` followed by a length test on the trimmed value, on exactly the terms {@link
   * hasSignedInUser} applies to the caption: an empty address would render a link that navigates to the
   * current page.
   */
  protected get hasAccountProfileLink(): boolean {
    return this.hasUsableLink(this.accountProfileLink);
  }

  /** Whether the own-password affordance can be rendered. */
  protected get hasAccountPasswordLink(): boolean {
    return this.hasUsableLink(this.accountPasswordLink);
  }

  /**
   * Whether an account-scoped address is present, non-blank and accompanied by a session. The session
   * test is not redundant with the address test: a stale address could outlive the identity that produced
   * it, and a link naming an account nobody is signed in as would navigate to a screen the caller is not
   * entitled to and be refused there.
   *
   * @param link The address to test.
   * @returns True when the affordance may be rendered.
   */
  private hasUsableLink(link: string | undefined): boolean {
    return this.hasSignedInUser && link !== undefined && link.trim().length > 0;
  }

  /** Handles activation of the sign-out control. Reports the gesture and does nothing else. */
  protected onSignOutClick(): void {
    if (this.signingOut) {
      return;
    }

    this.signOut.emit();
  }
}
