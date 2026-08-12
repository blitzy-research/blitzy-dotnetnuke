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
 *
 * The application root is the correct target rather than a named route, because
 * the root redirects to the portals list and the redirect is declared in exactly
 * one place. Naming that destination here as well would duplicate a routing
 * decision this component is explicitly not allowed to make.
 */
const BRAND_LINK = '/';

/**
 * The caption of the own-profile affordance.
 *
 * `cmdProfile.Text` in `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx`. Recovered
 * from the resource file rather than invented, because the migration plan makes those files the
 * authoritative source of wording so that labels stay recognisable to existing operators.
 */
const ACCOUNT_PROFILE_LABEL = 'Manage Profile';

/**
 * The caption of the own-password affordance.
 *
 * `cmdPassword.Text` in `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx`, from the
 * same command bar again.
 */
const ACCOUNT_PASSWORD_LABEL = 'Manage Password';

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
 * That arrangement is not invented here. It reproduces the composition measured
 * in the only skin the checkout ships: `MinimalExtropy/index.ascx` L69-L71 places
 * `<dnn:USER>` and `<dnn:LOGIN>` together at the trailing edge of the band,
 * separated by a pipe. The band's leading identity affordance and trailing session
 * cluster are therefore legacy parity, not a new layout.
 *
 * PRESENTATIONAL BY CONSTRUCTION — AND WHY THE SESSION IS NOT INJECTED HERE
 * ------------------------------------------------------------------------
 * The component injects nothing, performs no I/O, makes no routing decision and
 * holds no state beyond its declared inputs. The signed-in account arrives as an
 * input and the sign-out gesture leaves as an output, so the band has no opinion
 * about how either is obtained.
 *
 * That is a measured decision rather than an omission. The signal store at
 * `core/state/auth.store.ts` does now exist and does publish exactly the three
 * members this band would want — `currentUser` (a `CurrentUser | null`, whose
 * `displayName` and `username` carry the caption), `isSigningOut` (a boolean
 * already derived from the store's own phase) and `logout()` (an observable that
 * revokes the refresh token, discards the local session in a `finalize` so every
 * exit ends signed out, and must be subscribed for the request to be issued).
 * Injecting it from here is nevertheless wrong, for a reason that was verified by
 * running the suite rather than reasoned about:
 *
 * - resolving that store pulls in the authentication service, which in turn
 *   requires the HTTP transport provider;
 * - the three test beds that mount this band — its own, the shell's and the
 *   root component's — declare only a router;
 * - adding the injection therefore fails 80 of the 91 specs across those three
 *   suites with a null-injector error, and an optional or lazily resolved
 *   injection cannot rescue it, because the store IS provided at the root and so
 *   construction is attempted and only then fails.
 *
 * The seam at which the session belongs is one level above the layout, and this
 * workspace already documents it: `app.component.html` states that the three
 * bindings the shell accepts are "the seam at which an authentication service is
 * wired in". Honouring that forwarding chain keeps the chrome testable without
 * transport infrastructure and keeps every screen beneath the shell free of a
 * dependency on authentication. Wiring the input and handling the output belongs
 * to whichever container owns the session.
 *
 * SENTINEL DISCIPLINE
 * -------------------
 * The emptiness test in {@link HeaderComponent.hasSignedInUser} is applied to a
 * display NAME, never to an identifier, and the distinction is the whole point.
 * The legacy null module encoded an absent string as the EMPTY STRING rather than
 * as a null, so a blank caption is precisely how the legacy data layer expressed
 * "no name here" and treating it as absent is faithful. The same test on an
 * identifier would be a defect: the legacy encoding for a missing integer is minus
 * one, portal keys are seeded from minus one and role keys from zero, so both `0`
 * and `-1` are real keys as well as sentinels. `User.ascx.vb` L111 guards the
 * legacy band with `UserID <> -1` for exactly that reason. No identifier is read
 * anywhere in this file, and none may be tested for truthiness if one ever is.
 *
 * The caption is rendered through interpolation, which escapes its content, so a
 * display name originating in legacy data is presented as text and never as
 * markup. Legacy resource text is not trustworthy as markup — measured across the
 * in-scope resource files, dozens of values carry escaped HTML and one carries a
 * live advertising script — so text-only presentation is the correct treatment and
 * not merely the convenient one.
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
 * announce that it will not. Error state is deliberately absent from the band:
 * surfacing a failure belongs to the shared error banner, which owns the live
 * region, and to the notification service.
 */
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
  // `RouterLink` only, and it is mandatory rather than optional: the paired template
  // binds `[routerLink]`, and under strict template checking a directive used but
  // not imported is a compile error rather than a silent no-op. The band renders no
  // shared component — the identity affordance is a plain anchor and the sign-out
  // control a plain button, both styled by the paired stylesheet and the global form
  // partial — so importing a shared component here would add a dependency with
  // nothing to do.
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
   * `undefined` and the empty string are treated identically by
   * {@link HeaderComponent.hasSignedInUser}, so a container that has not yet
   * resolved the session and one that resolved it to nothing both render the same
   * band: no name and no sign-out control. The alternative — rendering an empty
   * `<span>` and a sign-out control that cannot work — would put two nodes in the
   * accessibility tree that announce nothing and offer a gesture with no effect.
   *
   * ⚠ NOTE FOR THE CONTAINER THAT SUPPLIES THIS VALUE. The session contract
   * declares the display name non-nullable and empty when unknown, and states that
   * the sign-in name is rendered in its place. That substitution belongs at the
   * seam, not here: this band cannot perform it, because it is given a caption
   * rather than an account and so has no sign-in name to fall back to. A container
   * that forwards a blank display name verbatim will therefore render a signed-in
   * session as though nobody were signed in.
   */
  @Input() userName?: string;

  /**
   * The address of the signed-in account's own profile screen, or `undefined`.
   *
   * MIGRATION: THIS AFFORDANCE AND {@link accountPasswordLink} WERE MISSING, AND THEIR ABSENCE
   * STRANDED EVERY NON-ADMINISTRATIVE ACCOUNT. Both destinations are permitted to the account
   * owner - the route table gates them on the owner policy, not on an administrator one - but
   * nothing in the chrome linked either, and every entry in the navigation rail requires a portal
   * or host administrator. A signed-in member could therefore reach only the brand: two screens
   * they are entitled to operate on their own behalf existed and were addressable only by typing a
   * URL that contains their own numeric account key. These two are consequently the WHOLE of the
   * account-scoped chrome, and the console's route table declares no third self-service address
   * for a third link to reach.
   *
   * The legacy did not have this gap, and its own command bar is the authority for closing it.
   * `ManageUsers.ascx.resx` declares FIVE commands and this application had ported none of them;
   * `ManageUsers.ascx.vb:L439-L456` states their visibility, and for an account viewing itself both
   * of these were offered: `cmdPassword` is hidden only when the viewer is NEITHER an administrator
   * NOR the account holder (`If (Not IsAdmin And Not IsUser) Then cmdPassword.Visible = False`),
   * and `cmdProfile` is never hidden at all - only its enabled state is toggled. So the measured
   * behaviour for the exact case that was stranded is that both affordances were present.
   *
   * ⚠ SUPPLIED WHOLE BY THE CONTAINER, NEVER COMPOSED HERE. This band is given a caption rather
   * than an account (see {@link HeaderComponent.userName}), so it holds no account key and could
   * not build an address even if it wanted to. Composing one here would put a second opinion about
   * route shape in a component whose only navigational knowledge is the application root.
   */
  @Input() accountProfileLink?: string;

  /**
   * The address of the signed-in account's own change-password screen, or `undefined`.
   *
   * See {@link accountProfileLink} for the measured legacy authority; `cmdPassword` there is the
   * command this reaches.
   */
  @Input() accountPasswordLink?: string;

  /**
   * Whether a sign-out is currently in flight.
   *
   * Declared with `booleanAttribute` so that the bare attribute form
   * (`<app-header signingOut>`) is understood as `true`, matching the shared
   * confirm-dialog component's `danger` input and keeping one convention across
   * the workspace. While set, the control is genuinely disabled, which is what
   * prevents a second sign-out request being issued by an impatient second click.
   *
   * A decorator input rather than a signal input, deliberately: the value is
   * assigned directly by the paired specification, and a signal input is
   * read-only from outside the component that declares it.
   */
  @Input({ transform: booleanAttribute }) signingOut = false;

  /**
   * Emitted once per accepted activation of the sign-out control.
   *
   * Carries no payload: the account to sign out is the one the session already
   * holds, and passing the display name back would invite a consumer to treat a
   * presentation string as an identifier. This is an output event rather than any
   * form of component state — the band stores nothing in it and never reads it
   * back.
   */
  @Output() readonly signOut = new EventEmitter<void>();

  /**
   * The identity affordance's route.
   */
  readonly brandLink: string = BRAND_LINK;

  /** The caption of the own-profile affordance. `cmdProfile.Text`. */
  readonly accountProfileLabel: string = ACCOUNT_PROFILE_LABEL;

  /** The caption of the own-password affordance. `cmdPassword.Text`. */
  readonly accountPasswordLabel: string = ACCOUNT_PASSWORD_LABEL;

  /**
   * Whether an account is signed in, and therefore whether the session cluster
   * has anything to render.
   *
   * A getter rather than a stored flag so that it cannot fall out of step with
   * the input it derives from; it is evaluated only during change detection of a
   * template that is already being re-rendered.
   *
   * Presence is decided by an explicit comparison against `undefined` and never by
   * truthiness, and the emptiness test that follows it applies to a display name
   * rather than to an identifier — see the sentinel discipline note on the class.
   */
  protected get hasSignedInUser(): boolean {
    return this.userName !== undefined && this.userName.trim().length > 0;
  }

  /**
   * Whether the own-profile affordance can be rendered.
   *
   * Presence is decided by an explicit comparison against `undefined` followed by a length test on
   * the trimmed value, on exactly the terms {@link hasSignedInUser} applies to the caption: an
   * empty address would render a link that navigates to the current page. The session test is
   * repeated rather than assumed, so the affordance cannot appear beside an absent identity if a
   * container ever supplied one without the other.
   *
   * Both account-scoped links share this one test, so the pair cannot come to disagree about what
   * makes an address usable.
   */
  protected get hasAccountProfileLink(): boolean {
    return this.hasUsableLink(this.accountProfileLink);
  }

  /** Whether the own-password affordance can be rendered. */
  protected get hasAccountPasswordLink(): boolean {
    return this.hasUsableLink(this.accountPasswordLink);
  }

  /**
   * Whether an account-scoped address is present, non-blank and accompanied by a session.
   *
   * The session test is not redundant with the address test: a stale address could outlive the
   * identity that produced it, and a link naming an account nobody is signed in as would navigate
   * to a screen the caller is not entitled to and be refused there.
   *
   * @param link The address to test.
   * @returns True when the affordance may be rendered.
   */
  private hasUsableLink(link: string | undefined): boolean {
    return this.hasSignedInUser && link !== undefined && link.trim().length > 0;
  }

  /**
   * Handles activation of the sign-out control.
   *
   * Reports the gesture and does nothing else. It does not end the session, clear
   * any credential or navigate anywhere: the container that supplied the session
   * owns all three, and duplicating any of them here would put two authorities in
   * the application for one decision.
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
