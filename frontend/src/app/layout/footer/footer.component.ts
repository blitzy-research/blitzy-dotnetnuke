/**
 * Application footer — the single `contentinfo` band of the administration shell.
 *
 * Composition: rendered exactly once, by `layout/shell`, positioned after the shell's
 * `<main>` element so that DOM order and tab order both place the footer last. It is
 * never routed and it is never instantiated with bindings.
 *
 * Responsibility boundary — deliberately minimal. This class owns ONLY the copyright
 * data. It depends on nothing, performs no I/O, subscribes to nothing, declares no
 * template inputs or outputs, registers no provider and implements no lifecycle hook.
 * `footer.component.html` owns the semantics and the plain-text rendering;
 * `footer.component.scss` owns every visual value, resolved through the global design
 * tokens. No colour, spacing or typographic literal appears in this file.
 *
 * Legacy provenance — the DotNetNuke `Copyright` skin object:
 * - `Website/admin/Skins/Copyright.ascx.vb` `Page_Load` L78-L91 selected between
 *   `PortalSettings.FooterText` (L85-L86) and a formatted resource string (L88).
 * - `Website/admin/Skins/copyright.ascx` L2 rendered the result into
 *   `<asp:label id="lblCopyright" cssclass="SkinObject" enableviewstate="False">`.
 * - `Website/admin/Skins/App_LocalResources/Copyright.ascx.resx` L42-L44 holds that
 *   file's only resource entry: `Copyright.Text` = `Copyright (c) {0} {1}`, where
 *   `{0}` is `Year(Now())` and `{1}` is `PortalSettings.PortalName`.
 * - `Website/Default.aspx.vb` L191-L199 independently corroborates the same wording
 *   and the same two substitutions on the code path that fed the copyright meta tag.
 *
 * Every behavioural difference from that legacy band is annotated below with a
 * `// MIGRATION:` comment, per AAP Rule T5.
 */
import { ChangeDetectionStrategy, Component } from '@angular/core';

// ---------------------------------------------------------------------------
// Documented divergences from the legacy footer band. AAP Rule T5: "Preserve
// behaviour, document divergence ... annotated inline with a `// MIGRATION:`
// comment, never silently absorbed."
// ---------------------------------------------------------------------------
//
// MIGRATION: The copyright line is STATIC. Legacy rendered a per-portal,
// database-driven value — `PortalSettings.FooterText` when non-empty, otherwise
// `String.Format("Copyright (c) {0} {1}", Year(Now()), PortalSettings.PortalName)`
// (`Website/admin/Skins/Copyright.ascx.vb` L85-L89 with
// `Website/admin/Skins/App_LocalResources/Copyright.ascx.resx` L42-L44, corroborated
// at `Website/Default.aspx.vb` L191-L199). Neither the admin-editable override nor the
// per-portal name substitution is carried forward: the closed API surface exposes no
// ambient current-portal resource. `GET /api/v1/auth/me` yields a portal identifier
// only, and `GET /api/v1/portals/{id}` is an administrative read that an
// unauthenticated visitor on the login route definitionally cannot perform. Fetching
// either from shell chrome would add speculative surface, contrary to Minimal Change
// Clause item 5, so the band is resolved at build time instead.
//
// MIGRATION: The `{1}` substitution resolves to the fixed product designation
// `DotNetNuke`, replacing `PortalSettings.PortalName`. That designation is grounded in
// the repository's own committed `catalog-info.yaml` and `docs/` per AAP 0.1.2.3, which
// requires names be taken from published documentation rather than invented.
//
// MIGRATION: `(c)` is retained as literal ASCII, byte-exact with the measured resource
// value at `Copyright.ascx.resx` L43. The typographic copyright glyph is deliberately
// NOT substituted — altering the rendered characters would be a silent divergence.
//
// MIGRATION: The year is computed component-locally via `new Date().getFullYear()`.
// There is no frontend clock abstraction to depend on — `IClock` is a backend Domain
// abstraction (AAP 0.5.1.1) — so a direct platform read is the correct and only
// mechanism available here, and the closed dependency surface contains no date library.
// It is evaluated exactly once, when the component is created, and never in the paired
// markup — a markup-side `new Date()` would re-evaluate on every change-detection pass
// and could not be asserted against. The paired specification therefore asserts a
// four-digit pattern, never a literal year, so the band stays correct across year
// boundaries without a rebuild of the assertion.
//
// MIGRATION: `PortalSettings.FooterText` (`Library/Components/Portal/PortalInfo.vb`
// L36 and L109-L116, edited through the "Copyright:" field at
// `Website/admin/Portal/sitesettings.ascx` L60-L64) was admin-authored text that
// `Label.Text` emitted as live markup. It is deliberately not carried forward, and no
// value in this band is ever bound as markup: the template interpolates plain text
// only, which the framework escapes. Legacy precedent for encoding untrusted text is
// `Website/Default.aspx.vb` L232, `Server.HtmlEncode(exc.Message)`.
//
// MIGRATION: The footer root-links strip is not reproduced. Legacy skins emitted
// `<dnn:LINKS ... Level="Root" Separator="...|...">` at
// `Website/Portals/_default/Skins/MinimalExtropy/index.ascx` L109 (registered L10),
// which merely duplicated the primary navigation. That navigation now lives once, in
// `layout/sidebar`, which is the shell's sole navigation landmark.
//
// MIGRATION: The Privacy Statement and Terms Of Use skin objects are not reproduced.
// Their wording is measured — `Privacy.ascx.resx` `Privacy.Text` = "Privacy Statement"
// and `Terms.ascx.resx` `Terms.Text` = "Terms Of Use", rendered by the hyperlinks at
// `Website/admin/Skins/privacy.ascx` L2 and `Website/admin/Skins/terms.ascx` L2 — but
// the target route table declares no privacy or terms route, so either link would
// dangle. Adding routes for them would exceed the defined scope.
//
// MIGRATION: Runtime dynamic skin loading is replaced by this static, compile-time
// component. Legacy resolved the band per request: `LoadSkin`
// (`Website/Default.aspx.vb` L217-L243) called `LoadControl("~" & SkinPath)` at L224,
// bound it with `ctlSkin.DataBind()` at L226, and the result was injected into
// `<asp:PlaceHolder ID="SkinPlaceHolder" runat="server" />` (`Website/Default.aspx`
// L25) at L590. Skinning and containers are out of scope (AAP 0.2.2.2, 0.2.2.4).
//
// MIGRATION: The cached, ordered runtime stylesheet cascade is replaced by one
// build-time bundle. `ManageStyleSheets` (`Website/Default.aspx.vb` L355-L408, with
// `default.css` flagged in source as the required default at L366) executed twice — at
// L587 and again at L593 — straddling the skin injection at L590. All styling for this
// band is now resolved at build time from the paired stylesheet and the global tokens.
//
// MIGRATION: View state, the multipart `dnn:Form` and the `ScrollTop` /
// `__dnnVariable` hidden inputs are eliminated (`Website/Default.aspx` L23, L26, L27).
// This band posts nothing back, so it contributes no round-tripped state; scroll
// restoration is handled once by the router's in-memory scrolling configuration.
//
// MIGRATION: Localisation is not ported. The legacy value came from a resource lookup,
// `Services.Localization.Localization.GetString("Copyright", ...)` at
// `Copyright.ascx.vb` L88. The measured resource files are read for wording only; the
// Angular localisation package is absent from the closed dependency surface, so the
// English wording is authored directly in the paired template.
@Component({
  selector: 'app-footer',
  standalone: true,
  templateUrl: './footer.component.html',
  styleUrl: './footer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FooterComponent {
  /**
   * Four-digit calendar year of the copyright notice.
   *
   * Replaces the legacy `Year(Now())` argument supplied as `{0}` to the
   * `Copyright.Text` resource string (`Copyright.ascx.vb` L88). Read once from the
   * platform clock at creation time and held immutable for the component's lifetime,
   * so `OnPush` change detection never needs to re-evaluate it. A signal would add
   * reactivity with nothing to react to, so a plain `readonly` field is used.
   */
  readonly currentYear: number = new Date().getFullYear();

  /**
   * The fully composed copyright notice, for example `Copyright (c) 2026 DotNetNuke`.
   *
   * Derived from {@link FooterComponent.currentYear}, which keeps the year a single
   * source of truth across both members. The wording reproduces the measured
   * `Copyright (c) {0} {1}` resource value byte-for-byte apart from the two documented
   * substitutions above, and it is plain text — never markup.
   *
   * Both this member and `currentYear` are exposed so that the paired template may
   * bind whichever granularity it needs: the precomposed line, or just the year with
   * the wording expressed in the markup. Neither is a placeholder; both are fully
   * computed values.
   */
  readonly copyrightText: string = `Copyright (c) ${this.currentYear} DotNetNuke`;
}
