import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Page title and action bar for the administration screens: a single page-level
 * heading, an optional line of supporting text beneath it, and a slot for the
 * host screen's page-level actions.
 *
 * Emits no landmark element and no landmark role. Landmarks belong to the
 * application shell and this component always renders inside the main region, so
 * a second banner landmark here would be an accessibility defect rather than an
 * improvement.
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
 * not produce a header without a heading - it produces a heading without a name.
 * That is a specific, tool-detectable accessibility defect: assistive technology
 * announces "heading level one" and then falls silent, and the document outline
 * gains an unlabelled top-level entry. Because the element is emitted
 * unconditionally, refusing the value is the only way to guarantee it never
 * happens.
 *
 * The trim is normalisation, not validation. HTML collapses leading and trailing
 * white space, so trimming changes no rendered pixel; what it does change is
 * that a whitespace-only title becomes indistinguishable from an empty one and
 * both are rejected. Without it, `' '` would pass a naive length check and still
 * render an unnamed heading.
 *
 * Throwing rather than substituting a fallback is deliberate: an invented title
 * would put incorrect content in the document outline, which is worse than a
 * loud failure because nobody would notice it.
 *
 * This complements, rather than duplicates, `required: true` on the input, and
 * the division of labour is narrower than it looks. `required: true` makes an
 * OMITTED title a compile-time `NG8008` error at every template call site, which
 * is where the mistake is usually made. But it is a TEMPLATE type check, and
 * three paths run straight past it:
 *
 * - It runs only where templates are type-checked, so nothing in a spec and
 *   nothing built without full ahead-of-time compilation is protected by it.
 * - It cannot see through a binding. `[title]="portal.name"` satisfies it
 *   completely while still delivering an empty string for a record with no name.
 * - It does not apply to a component created imperatively, where inputs are set
 *   through the component reference rather than through a template.
 *
 * This function closes all three at input assignment, before a frame is ever
 * painted. Features are documented to arrive with the title already resolved, so
 * an empty one is a programming error rather than a data condition.
 *
 * @param value The raw bound title.
 * @returns The trimmed title, guaranteed non-blank.
 * @throws Error if the supplied title is empty or contains only white space.
 */
export function requireNonBlankTitle(value: string): string {
  // The parameter is typed `string`, but a JavaScript caller or an `any`-typed
  // binding can still deliver null or undefined, and `.trim()` on either would
  // throw a TypeError whose message names neither this component nor this input.
  // Coalescing first means every rejection path produces the explanatory message
  // below instead.
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

/**
 * Normalises the `subtitle` input, collapsing a blank one to absent.
 *
 * The subtitle is optional, so unlike the title a blank value is not an error to
 * report - it is simply nothing to render. The template guards the paragraph on the
 * truthiness of this input precisely so that "absent" renders no element at all,
 * rather than an empty paragraph that would occupy vertical space and add a node
 * carrying no accessible name to the accessibility tree.
 *
 * A truthiness guard alone cannot deliver that, because `' '` is truthy in
 * JavaScript: a whitespace-only value - the shape a consumer most plausibly arrives
 * at by interpolating a record field that happens to hold padding, or by binding a
 * value assembled from an empty resource string - satisfied the guard and produced
 * exactly the blank paragraph the guard exists to prevent. Normalising here closes
 * that at input assignment, which makes the guard correct by construction instead
 * of leaving the template to compensate for a value that never should have been
 * stored: after this transform the stored value is either a non-blank string or
 * `undefined`, so there is no third state for a read site to get wrong.
 *
 * The value is trimmed rather than merely tested, mirroring
 * {@link requireNonBlankTitle}, so the two inputs normalise identically and cannot
 * drift into treating padding differently from one another. Trimming is not
 * visually observable - a paragraph collapses leading and trailing white space when
 * rendered - but it does make the rendered text exactly the supplied value, which
 * is what lets a specification assert on it without trimming first.
 *
 * @param value The raw bound subtitle, which may be absent.
 * @returns The trimmed subtitle, or `undefined` when there is nothing to render.
 */
export function normaliseOptionalSubtitle(value: string | undefined): string | undefined {
  // Coalesced for the same reason the title transform coalesces: the declared type
  // does not stop a JavaScript caller or an `any`-typed binding from delivering
  // null, and `.trim()` on that would throw a TypeError naming neither this
  // component nor this input.
  const normalised = (value ?? '').trim();

  return normalised.length === 0 ? undefined : normalised;
}

@Component({
  selector: 'app-page-header',
  standalone: true,
  imports: [],
  templateUrl: './page-header.component.html',
  styleUrl: './page-header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The framework copies a *static* template attribute onto the rendered host in
  // addition to assigning the matching input, so the ordinary call site
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
   * Public by mandate: `strictInputAccessModifiers` is enabled, so a `private` or
   * `protected` input would fail to compile at every consumer site. Not marked
   * `readonly`, because the framework assigns inputs.
   *
   * REQUIRED, and never blank. There is no default and absence is not
   * expressible: the component emits the page's single `<h1>` unconditionally, so
   * a header without a title would render a heading without a name. Three
   * mechanisms together make that unreachable:
   *
   *   1. `required: true` - omitting the input is a compile-time `NG8008` error at
   *      every template call site. Note the limit: it is a template type check
   *      and cannot see through a binding that supplies an empty value.
   *   2. `transform` - a present-but-blank value is rejected at assignment and
   *      surrounding white space is normalised away, so `' '` cannot slip through
   *      a naive length check. See {@link requireNonBlankTitle}, which records
   *      exactly which paths the compile-time check leaves open.
   *   3. The template interpolates this member directly, with no fallback and no
   *      guard, so there is no third path to an empty heading.
   *
   * The definite-assignment marker is the cost of that guarantee. A plain
   * `string` defaulting to the empty string would avoid the marker under `strict`
   * but make "no title" a legal, silent state; a required input is assigned
   * before first render and is never observably undefined, so the marker
   * documents a real invariant rather than suppressing one.
   */
  // MIGRATION: Titles arrive already resolved. The legacy title-key convention
  // encoded the screen's edit mode into the key name, and each screen's resource
  // file carried only the modes that screen supported - with enough irregularity
  // in suffixes and mode names that no general key-mapping rule exists. Mode
  // resolution therefore stays in the feature: this component takes a plain
  // resolved string, maps no keys, models no modes and consults no routing state.
  //
  // MIGRATION: The member name collides with the global HTML `title` attribute.
  // The name is kept because the fixed shared-library API mandates it, and the
  // value is never published to the host element: no host entry writes it, no
  // host-property decorator exposes it, and the paired template carries no
  // `[title]` binding. Those three omissions are necessary but NOT sufficient,
  // which is the non-obvious part - the framework copies a static template
  // attribute onto the rendered element in addition to assigning the matching
  // input, so the ordinary call site `<app-page-header title="Portals">` leaves a
  // live `title` attribute on the host. Such an attribute supplies advisory text
  // for the element and every descendant, which is the native-tooltip condition,
  // and it also promotes an otherwise ignored presentational wrapper into a named
  // node in the accessibility tree. The component therefore strips the attribute
  // in its own host metadata above, making the guarantee unconditional instead of
  // a convention every future consumer has to remember. The strip is invisible,
  // changing no rendered pixel, and it withholds an affordance the legacy portal
  // never had: its admin titles were plain labels carrying no tooltip.
  @Input({ required: true, transform: requireNonBlankTitle })
  title!: string;

  /**
   * Optional supporting text rendered beneath the title.
   *
   * Optional rather than defaulted, so "absent" is `undefined` and a consumer may
   * bind a value that is itself absent. It is supporting text rather than a
   * second heading, so it renders as a paragraph and stays out of the document
   * outline.
   *
   * Normalised on assignment by {@link normaliseOptionalSubtitle}, so a value that
   * is blank or nothing but white space is stored as `undefined` and renders no
   * paragraph at all. Absent and blank are therefore the same state here by
   * construction rather than by the template remembering to treat them alike.
   */
  @Input({ transform: normaliseOptionalSubtitle }) subtitle?: string;
}
