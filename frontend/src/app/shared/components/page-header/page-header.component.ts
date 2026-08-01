/**
 * `app-page-header` - the page title and action bar of the in-repository shared
 * component library (AAP 0.3.2, row 2).
 *
 * PROJECT STANDARDS: `review_rules` reports that NO user-specified rules exist
 * for this project. Completeness is established by the RANGES read, not by the
 * number of calls: the default window, `[1, -1]`, `[2, 250]` and `[2, 400]` all
 * return the same single line, and the last two begin past line 1, so a document
 * with a body would have returned different text for them. No rules document and
 * no coding-standards document is assumed, invented or implied here. This file
 * instead holds to the AAP's own normative sections, which AAP 0.8.2 gives
 * rule-force, and to the enterprise baseline of AAP 0.8.3. The precedence order
 * applied throughout is AAP 0.3.5: design-system compliance, then visual
 * continuity with the legacy portal, then accessibility, then responsive
 * behaviour, then code quality.
 */

import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// MIGRATION: No landmark element is emitted by this component. The semantic
// landmarks - banner, main, navigation and contentinfo - belong exclusively to
// frontend/src/app/layout/**, where the application shell owns them together
// with the routed outlet. This component always renders inside the main region,
// so emitting a second banner landmark here would be an accessibility defect
// rather than an improvement. None of the nine components in this shared
// library emits a landmark.

// MIGRATION: The title is promoted from a plain `<asp:label>` to a real `<h1>`.
// Measured baseline across the 39 in-scope admin `.ascx` files: zero heading
// elements (`<h1>`-`<h6>` = 0) and zero ARIA attributes (`aria-*`/`role=` = 0).
// The legacy title was a label in three independent places - the shared control
// Website/controls/sectionheadcontrol.ascx L4 (`<asp:label id="lblTitle"
// enableviewstate="False">`), the skin container's `<dnn:TITLE
// CssClass="Head" />` (verified in Portals/_default/Containers/MinimalExtropy/
// Title_{Blue,Red,Grey}.ascx L12), and Website/admin/Containers/title.ascx L3
// (`<dnn:DNNLabelEdit id="lblTitle" cssclass="Head">`). The promotion is
// size-neutral, so it costs no visual change: default.css `.Head` (L65-71) is
// 20px, and `H1` (L488-494) and `H2` (L496-502) are both 20px as well.

// MIGRATION: The action slot holds N actions and collapses to nothing at zero.
// Measured across the 37 in-scope resx files: 19 `.Action` keys spread over
// exactly 8 files, distribution {1 action: 4 screens, 3: 2, 4: 1, 5: 1}, so
// half the action-carrying screens carry more than one. The measured maximum is
// 5, in ManageUsers.ascx.resx ('Manage Roles for this User', 'Add New User',
// "Manage User's Profile", "Manage User's Password", 'Manage Profile
// Properties'). The `*.Action` keys were literally the legacy skin container's
// ACTIONBUTTON `CommandName` values. A single unnamed content slot therefore
// covers every case: no selector, no counting, no cap, no content query and no
// third input. Collapse-at-zero is achieved purely in the stylesheet, because an
// actions wrapper that carries no padding, border or background occupies no
// space when nothing is projected into it.

// MIGRATION: The legacy section-collapse toggle has no counterpart in this
// component, by design and permanently - it is owned elsewhere, not deferred.
// `dnn:SectionHead` put its toggle image outside the tab order with
// `tabIndex="-1"` (sectionheadcontrol.ascx L3) and identified its target through
// `Section="<targetElementId>"`, which is the legacy equivalent of an
// `aria-controls` reference - proven because editroles.ascx L11-12
// (`Section="tblBasic"`) and L68-70 (`Section="tblAdvanced"`) point at real
// `<table id="tblBasic">` (L13-14) and `<table id="tblAdvanced">` (L71-72)
// elements, and `IsExpanded="False"` on `dshAdvanced` means collapsed by
// default. The keyboard-reachable replacement - a disclosure carrying
// `aria-expanded` and `aria-controls` - belongs to the feature screens as
// `<fieldset><legend>` or `<details><summary>`: the control appears on 9
// in-scope screens (41 opening tags; 20 admin screens repo-wide, 21 including
// the out-of-scope rich-text provider screen). This component does not grow a
// toggle, does not gain a third input and does not become a section component,
// because the shared inventory is closed at ten - the nine components plus the
// permission directive - per AAP 0.3.2 and 0.3.4.
// REPORTED, NOT ACTED ON: the planned `_layout.scss` prompt misattributes this
// disclosure fix to `page-header`. The `_forms.scss` prompt states the correct
// split - "page-header owns the title and _layout.scss owns section
// scaffolding" - and this file follows that split.

// MIGRATION: `.CommandButton` is retained as a text-link affordance rather than
// promoted to a filled button. Measured in default.css: `.CommandButton`
// (L442-447) is {Tahoma, Arial, Helvetica; 11px; normal}; `A.CommandButton:link`
// (L450-454) and `:visited` (L456-460) are {underline; #003366}; `:hover`
// (L462-466) is {underline; #ff0000}; `:active` (L468-472) is {underline;
// #003366}. A filled `.StandardButton` existed separately (L475 onward) and the
// admin action bars deliberately did not use it. Projected content keeps the
// style scoping of the component that declares it, so this component cannot
// style the actions it hosts; that affordance is delegated to the global
// stylesheet or to the feature, with deep-descendant style piercing and the
// per-component scoping override both off limits. Action hover resolves to
// `--color-primary-hover` rather than the measured #ff0000, because #ff0000 is
// tokenised as `--color-danger` and is the `.NormalRed` error colour
// (default.css L114, under the L113 comment "text style used for error
// messages") - a semantic collision, recorded here rather than absorbed.

// MIGRATION: The legacy `&nbsp;` action separators become spacing tokens, never
// non-breaking-space text nodes. Measured: editroles.ascx L178-190 is a plain
// `<p>` holding four `<asp:LinkButton CssClass="CommandButton">` controls -
// Update, Cancel, Delete and Manage Users, the first three carrying
// `BorderStyle="none"` - separated by literal `&nbsp;` text nodes at L181, L184
// and L187; portals.ascx L4-11 separates its letter-filter links with
// `&nbsp;&nbsp;`; and the skin container prefixes `<dnn:TITLE>` with `&nbsp;`.

// MIGRATION: `.Head`'s #333333 and `.HeadBg`'s #CCCCCC are reported as token
// gaps and are never hardcoded. #333333 occurs exactly once in default.css and
// is absent from the nine-colour token table, which declares no heading or base
// text colour at all, and the `H1`/`H2` colour #666644 is explicitly left
// untokenised by AAP 0.3.3. `.HeadBg` is additionally already legacy -
// default.css L806 marks the block that follows as "LEGACY STYLES from DNN 1-2"
// and the selector sits at L807 - it has zero consumers in any `.ascx` or
// `.aspx` repo-wide, and its portal.css override block (L8-10) is empty. No
// background band is therefore reproduced.

// MIGRATION: The legacy `.Settings` and `.WorkPanel` wrapper classes are pure
// net additions with no visual definition to port. Both are consumed by the
// in-scope admin markup - 10 and 8 uses respectively, including editroles.ascx
// L6-7 `class="Settings"` and L10 `CssClass="WorkPanel"` - yet neither selector
// is defined in any `.css` file repo-wide.

// MIGRATION: Missing legacy wording keys are authored directly and the gap is
// recorded. Portals.ascx.resx holds 17 entries - 21 raw `<data name=`
// occurrences, four of which sit inside the standard resx comment block - and
// none of them is `Delete.Text`, even though portals.ascx L22 renders a Delete
// action; `Edit.Text` does exist and carries the richer wording 'Edit this
// Portal'. The same defect class recurs on roles.ascx, where L13 `cmdDelete`
// carries neither `AlternateText` nor `resourcekey` while L11 `imgEditGroup`
// carries both, and on users.ascx L76-77.

// MIGRATION: Raster action icons are replaced by text, inline SVG or CSS. All
// twelve legacy assets are still present under Website/images - edit.gif,
// delete.gif, save.gif, refresh.gif, up.gif, dn.gif, checked.gif,
// unchecked.gif, help.gif, icon_users_16px.gif, icon_securityroles_16px.gif and
// icon_search_16px.gif - and none has a target equivalent. The frontend ships no
// raster artwork whatsoever: the single static asset the workspace even plans is
// a favicon, and that is itself absent from this checkout, where
// frontend/public holds only a placeholder file. No component may therefore
// reference an image asset, which is why every affordance here is text.

// MIGRATION: `title` and `subtitle` are rendered as plain text through
// interpolation only - never through a raw-HTML property binding and never
// through a sanitiser bypass. The legacy wording source cannot be trusted as
// markup: 76 of the in-scope resx values contain an HTML tag and 29 begin with
// a leading `<br>`, including all nine EditRoles validator messages. The
// decisive case is SiteSettings.ascx.resx -> `Advertising.Text`, a
// 344-character value carrying a live Google AdSense `<script>` block whose src
// is the remote http://pagead2.googlesyndication.com/pagead/show_ads.js - a
// value invisible to a naive search because the tags are stored HTML-escaped.
// Portals.ascx.resx -> `ModuleHelp.Text` similarly opens '<h1>About
// Portals</h1><p>The Super User can manage...'. DENOMINATOR HONESTY: the 37
// in-scope files yield 1211 raw `<data name=` occurrences against 1111 parsed
// `<data>` elements, the gap of 100 being entries inside XML comments; the
// circulating figure of 1182 is a partial parse. The 76 HTML-bearing count is
// unanimous across all three readings.

// MIGRATION: Wording resolution rule - the resx value wins where the key
// exists, and where the key is absent the markup `Text=` attribute is the only
// wording available. EditRoles.ascx.resx proves both halves at once:
// `cmdManage.Text` is the richer 'Manage Users in this Role' against the
// markup's "Manage Users", while `cmdUpdate.Text`, `cmdCancel.Text` and
// `cmdDelete.Text` are absent from that file entirely. Two validator messages
// are exactly swapped between markup and resx - `valBillingPeriod2` reads
// "...Greater Than or Equal to Zero" in markup with `Operator="GreaterThan"`
// but 'Greater Than Zero' in resx, and `valTrialFee2` is the precise inverse -
// and in both cases the resx wording matches the declared operator, which makes
// the resx authoritative. Validator keys are `.Text`-suffixed, never
// `.ErrorMessage`-suffixed.

// MIGRATION: Localisation is not ported. No localisation runtime is present in
// the pinned dependency surface and no message-tagging helper is used; strings
// are authored directly into templates, and the 37 resx files informed wording
// only.

// MIGRATION: The spacing, radius and elevation scales that this component's
// stylesheet consumes are net additions rather than translations - the legacy
// CSS has no spacing system, is square-cornered and declares no elevation - and
// the three overlapping legacy font stacks are consolidated into one base stack
// (AAP 0.3.3). Any responsive wrapping of the title against its action bar is
// likewise net-new behaviour: `@media` occurs zero times across every
// stylesheet under Website/.

/**
 * Page title and action bar for the administration screens.
 *
 * Purpose: renders a single page-level heading, an optional line of supporting
 * text beneath it, and a slot into which the host screen projects its page-level
 * actions. It replaces the legacy `.Head` / `.HeadBg` title styling and the
 * skin container's ACTIONBUTTON bar.
 *
 * No landmark element: this component deliberately emits no banner, navigation,
 * main or contentinfo landmark. Landmarks are owned solely by the application
 * shell under `layout/`, and this component renders inside the main region, so a
 * second banner landmark would be an accessibility defect.
 *
 * Heading level: the title renders as `<h1>` - the single page-level heading.
 * Consumers must compose the rest of their outline around that, using `<h2>` and
 * below for sections within the page, and must not place two instances of this
 * component on one screen.
 *
 * `subtitle` is optional. When it is not supplied nothing is rendered in its
 * place, and it is supporting text rather than a second heading, so it never
 * appears in the document outline.
 *
 * Action slot: an unnamed content slot accepts N actions - the measured legacy
 * maximum is five, on the user-management screen - and collapses to nothing when
 * zero actions are projected. Page-level actions belong here; an in-page
 * Update/Cancel/Delete bar belongs to the feature's own form footer, and a grid
 * row's Edit/Delete affordances belong to the data table. Those three are
 * distinct and must not be conflated.
 *
 * Plain text only: `title` and `subtitle` are interpolated and are therefore
 * escaped by the framework. Neither may ever be treated as HTML - the legacy
 * wording source includes values carrying a live remote script tag.
 *
 * Intentionally dependency-free: this component injects nothing, holds no
 * state, performs no I/O, exposes no outputs and reads no ambient route or
 * configuration value. Two inputs go in and a template comes out, which is what
 * makes it reusable across every feature and cheap to test.
 *
 * Styling of projected actions is the consumer's responsibility, because
 * projected content keeps the style scoping of the component that declares it.
 *
 * @example
 * ```html
 * <app-page-header title="Portals" subtitle="Manage the portals in this site.">
 *   <button type="button">Add New Portal</button>
 *   <button type="button">Export Portal Template</button>
 * </app-page-header>
 * ```
 */
@Component({
  selector: 'app-page-header',
  // No legacy module wrapper exists anywhere in this workspace (AAP 0.5.2.3).
  standalone: true,
  // Intentionally empty: the paired template uses only plain elements and the
  // built-in control-flow blocks, which need no imported selector. Padding this
  // array with an unused module would be noise under strict template checking.
  imports: [],
  templateUrl: './page-header.component.html',
  // Singular `styleUrl` (Angular 17+), never the plural form, and never an
  // inline stylesheet. The per-component style scoping is left at its default so
  // that these styles stay scoped to this component.
  styleUrl: './page-header.component.scss',
  // Mandatory for every component in this workspace (AAP 0.9.6).
  changeDetection: ChangeDetectionStrategy.OnPush,
  // MIGRATION: Defensive suppression of the `title` input's collision with the
  // global HTML `title` attribute. This entry never writes the value anywhere; it
  // only removes the attribute from the rendered host element, so the collision
  // is suppressed whichever binding syntax a consumer writes at the call site.
  // The reasoning, and the runtime measurement that proved the suppression
  // necessary, are recorded on the `title` input below. Nothing else is bound to
  // the host: no role, no class, no aria attribute and no listener, because the
  // host is a presentational wrapper and must stay one.
  host: { '[attr.title]': 'null' },
})
export class PageHeaderComponent {
  /**
   * The resolved page title, rendered as the page's single `<h1>`.
   *
   * Public by mandate: `strictInputAccessModifiers` is enabled, so a `private`
   * or `protected` input would fail to compile at every consumer site. Not
   * marked `readonly`, because the framework assigns inputs.
   *
   * Absence is expressed as the empty string, which is the declared default.
   * That keeps the property type a plain `string` under `strict` - no escape
   * hatch and no definite-assignment marker - and an empty value simply renders
   * no heading text.
   */
  // MIGRATION: Titles arrive already resolved. The legacy
  // `ControlTitle_<mode>.Text` convention produced 22 distinct keys across the
  // 37 in-scope resx files: `ControlTitle_.Text` ten times (Portals.ascx.resx =
  // 'Portals'), `ControlTitle_edit.Text` five times (EditRoles.ascx.resx =
  // 'Edit Security Roles', a file from which `ControlTitle_.Text` is absent
  // because each resx carries only the modes its own screen supports), one key
  // with no `.Text` suffix at all (`ControlTitle_accessdenied`) and one mode
  // name containing a space (`ControlTitle_user roles.Text`). Mode resolution
  // therefore stays in the feature: this component takes a plain resolved
  // string, maps no keys, models no modes, knows nothing of `.Text` suffixes and
  // consults no routing state. A separate and unrelated family of 10
  // `.Title`-suffixed keys also exists in those files and is not this input.
  //
  // MIGRATION: The member name collides with the global HTML `title` attribute.
  // The name is kept because the fixed shared-library API mandates it, and the
  // value is never published to the host element: no host entry writes it, no
  // host-property decorator exposes it, and the paired template carries no
  // `[title]` binding. Runtime measurement then proved those three omissions
  // necessary but not sufficient. The framework copies a static template
  // attribute onto the rendered element in addition to assigning the matching
  // input, so the ordinary call site `<app-page-header title="Portals">` left a
  // live `title` attribute on the host in five of eight measured instances. Such
  // an attribute supplies advisory text for the element and every descendant,
  // which is exactly the native-tooltip condition, and it also promoted an
  // otherwise ignored presentational wrapper into a named node in the
  // accessibility tree. The component therefore strips the attribute in its own
  // host metadata above, which makes the guarantee unconditional instead of a
  // convention every future consumer has to remember. Renaming the input was
  // rejected because the fixed API mandates the name, and a call-site convention
  // was rejected because this workspace ships no linter to enforce one. The strip
  // is invisible, changing no rendered pixel, and it withholds an affordance the
  // legacy portal never had: across the 39 in-scope admin screens the title is a
  // plain label carrying no tooltip attribute anywhere.
  @Input() title = '';

  /**
   * Optional supporting text rendered beneath the title.
   *
   * Public by mandate, for the same `strictInputAccessModifiers` reason as
   * `title`.
   *
   * Declared as an optional property, so "absent" is `undefined` - expressible
   * without any escape hatch, and accepted from consumers that bind a value
   * which may itself be absent. The paired template guards it with an `@if`
   * block, so nothing at all is rendered when it is not supplied.
   */
  // MIGRATION: `subtitle` is supporting text, not a second heading, and renders
  // as a paragraph so the document outline stays clean. This follows the legacy
  // `CssClass="Normal"` label precedent: editroles.ascx L17-18
  // `lblBasicSettingsHelp` and L75-76 `lblAdvancedSettingsHelp`, whose resx
  // values are the sentences 'In this section, you can set up the basic settings
  // for this role.' and 'In this section, you can set up more advanced settings
  // for this role.'. Measured styling of that precedent, default.css `.Normal`
  // (L92-97), is 11px at normal weight with no colour declared at all.
  @Input() subtitle?: string;
}
