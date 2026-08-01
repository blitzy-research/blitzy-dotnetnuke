import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Page title and action bar for the administration screens: a single page-level
 * heading, an optional line of supporting text beneath it, and a slot for the
 * host screen's page-level actions.
 *
 * Emits no landmark element and no landmark role. Landmarks belong to the
 * application shell, and this component always renders inside the main region,
 * so a second banner landmark here would be an accessibility defect rather than
 * an improvement.
 *
 * The title renders as the page's single `<h1>`. Consumers compose the rest of
 * their outline beneath it with `<h2>` and below, and must not place two
 * instances of this component on one screen.
 *
 * The action slot is unnamed and accepts any number of actions. Page-level
 * actions belong here; a form's Update/Cancel/Delete bar belongs to the
 * feature's own footer, and a grid row's commands belong to the data table.
 * Styling them is the consumer's responsibility, not this component's.
 *
 * `title` and `subtitle` are interpolated and therefore escaped. Neither may be
 * routed through a raw-HTML binding or a sanitiser bypass.
 */
/**
 * Normalises the `title` input and refuses a blank one.
 *
 * A page header always renders the page's single `<h1>`, so a blank title does
 * not produce a header without a heading — it produces a heading without a
 * name. That is a specific, tool-detectable accessibility defect: assistive
 * technology announces "heading level one" and then falls silent, and the
 * document outline gains an unlabelled top-level entry. Because the element is
 * emitted unconditionally, the only way to guarantee it never happens is to
 * refuse the value that causes it.
 *
 * The trim is normalisation, not validation. A title arriving with surrounding
 * whitespace — from a resource file, a template literal or a server field —
 * renders identically to a trimmed one, since HTML collapses leading and
 * trailing white space, so trimming changes no rendered pixel. What it does
 * change is that a whitespace-only title is now indistinguishable from an empty
 * one, and both are rejected. Without the trim, `' '` would pass a
 * length check and still render an unnamed heading.
 *
 * Throwing is deliberate, and the alternatives were considered and rejected:
 *
 * - Substituting a fallback string would invent page content that no feature
 *   asked for, and would put an incorrect heading in the document outline —
 *   worse than a loud failure, because nobody would notice it.
 * - Rendering no heading at all would silently degrade the page from "heading
 *   with no name" to "page with no heading", trading one defect for another
 *   while removing the evidence.
 * - Leaving it to a linter is not available: this workspace ships no lint step,
 *   so a convention would be unenforced.
 *
 * The throw complements, rather than duplicates, the `required` flag on the
 * input — and the division of labour between them is narrower than it first
 * appears, which is worth stating precisely because it was measured rather than
 * assumed.
 *
 * `required: true` makes an OMITTED title a compile-time error at every template
 * call site, which is where the mistake is actually made. That was verified
 * directly: compiling a host whose template reads `<app-page-header />` through
 * the Angular compiler reports `NG8008: Required input 'title' from component
 * PageHeaderComponent must be specified.` It is a genuine build failure, not a
 * convention.
 *
 * What that check does NOT cover is broader than expected, and is the reason
 * this function exists rather than being redundant:
 *
 * - It is a TEMPLATE type check, so it runs only where templates are
 *   type-checked. The unit-test builder in this workspace does not run the
 *   template type checker at all — proven by compiling a binding to an input
 *   that does not exist and observing no diagnostic — so nothing in a spec, and
 *   nothing built without full ahead-of-time compilation, is protected by it.
 * - It cannot see through a binding. `[title]="portal.name"` satisfies the check
 *   completely while still delivering an empty string at runtime for a record
 *   with no name.
 * - It does not apply to a component created imperatively, where inputs are set
 *   through the component reference rather than through a template.
 *
 * This function closes all three, at the earliest possible moment — input
 * assignment, before a frame is ever painted. Features are documented to arrive
 * with the title already resolved, so an empty one is a programming error rather
 * than a data condition.
 *
 * This mirrors a convention the workspace already follows. The shared
 * `breakpoint()` function in `_mixins.scss` raises a build error on an unknown
 * step rather than emitting a query that can never match, for the same reason:
 * a contract that degrades quietly is a contract nobody honours.
 *
 * @param value The raw bound title.
 * @returns The trimmed title, guaranteed non-blank.
 * @throws Error if the supplied title is empty or contains only white space.
 */
export function requireNonBlankTitle(value: string): string {
  // The parameter is typed `string`, but a JavaScript caller or an `any`-typed
  // binding can still deliver null or undefined, and `.trim()` on either would
  // throw a TypeError whose message names neither this component nor this
  // input. Coalescing first means every rejection path produces the explanatory
  // message below instead.
  const normalised = (value ?? '').trim();

  if (normalised.length === 0) {
    throw new Error(
      'PageHeaderComponent: `title` must be a non-blank string. It is ' +
        'rendered as the page\'s single <h1>, and an unnamed heading is an ' +
        'accessibility defect. Resolve the title in the feature before ' +
        'binding it — including any fallback for a record with no name — ' +
        'rather than passing an empty or whitespace-only value.',
    );
  }

  return normalised;
}

@Component({
  selector: 'app-page-header',
  standalone: true,
  imports: [],
  templateUrl: './page-header.component.html',
  styleUrl: './page-header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The framework copies a *static* template attribute onto the rendered host
  // in addition to assigning the matching input, so the ordinary call site
  // `<app-page-header title="...">` would leave a live global `title` attribute
  // there: a native tooltip, and a competing accessible name for the whole
  // subtree. Stripping it here keeps that impossible whichever binding syntax a
  // consumer writes, rather than relying on a call-site convention.
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
   * REQUIRED, and never blank. There is no default and absence is not
   * expressible: the component emits the page's single `<h1>` unconditionally,
   * so a header without a title would render a heading without a name — an
   * unnamed top-level entry in the document outline, which assistive technology
   * announces as a heading and then cannot read out. Three mechanisms together
   * make that unreachable:
   *
   *   1. `required: true` — omitting the input is a COMPILE-TIME error at every
   *      template call site, verified as `NG8008` from the Angular compiler.
   *      Note the limit of that guarantee: it is a template type check, and it
   *      cannot see through a binding that supplies an empty value.
   *   2. `transform` — a present-but-blank value is rejected at assignment, and
   *      surrounding white space is normalised away so `' '` cannot slip
   *      through a naive length check. See {@link requireNonBlankTitle}, which
   *      records exactly which paths the compile-time check leaves open.
   *   3. The template interpolates this member directly, with no fallback and no
   *      guard, so there is no third path by which an empty heading could be
   *      produced.
   *
   * The definite-assignment marker is the cost of that guarantee and is
   * deliberate. An earlier revision declared a plain `string` defaulting to the
   * empty string specifically to avoid the marker under `strict`, but that
   * default WAS the defect: it made "no title" a legal, silent state. A required
   * input is assigned by the framework before first render and is never
   * observably undefined, so the marker documents a real invariant rather than
   * suppressing a real one.
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
  @Input({ required: true, transform: requireNonBlankTitle })
  title!: string;

  /**
   * Optional supporting text rendered beneath the title.
   *
   * Optional rather than defaulted, so "absent" is `undefined` and a consumer
   * may bind a value that is itself absent. It is supporting text rather than a
   * second heading, so it renders as a paragraph and stays out of the document
   * outline.
   */
  @Input() subtitle?: string;
}
