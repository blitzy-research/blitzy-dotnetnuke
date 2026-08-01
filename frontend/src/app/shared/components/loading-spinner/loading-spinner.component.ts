import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

export type LoadingSpinnerSize = 'small' | 'medium' | 'large';

/**
 * Async progress indicator.
 *
 * A presentational leaf: no state beyond its two inputs, no I/O, nothing injected
 * or emitted, no content slot and no lifecycle hook. The caller decides *whether*
 * to render it, typically from a feature store's loading signal, which is why
 * there is no `loading` input to toggle.
 *
 * Accessibility is carried entirely by the host element, and the host has TWO
 * mutually exclusive modes selected by whether a label was supplied. The switch
 * is `hasVisibleLabel`, which is the single source of that decision for both the
 * host bindings and the template; see that member for the full reasoning.
 *
 * LABELLED (the default, reached by omitting the label input):
 *
 * - `role="status"` is an implicit *polite* live region: the visible label is
 *   the region's content, so assistive technology announces it without
 *   interrupting the user. The role takes its name from its contents rather
 *   than from the author, so the *accessible name* is deliberately left empty
 *   and the label reaches the user as region content instead. No `aria-label` is
 *   set precisely because it would introduce a separate name that can diverge
 *   from the visible text, breaking parity between what is seen and what is
 *   announced.
 * - `aria-busy="true"` accompanies it. The element is only in the DOM while
 *   something is in flight, so within this mode there is nothing to toggle.
 *
 * VISUAL-ONLY (reached by deliberately passing a blank label):
 *
 * - The role and the busy state are BOTH withheld and the host is
 *   `aria-hidden="true"`, so the component is purely decorative. Retaining a
 *   status role here would publish a live region with neither content nor name —
 *   the sole child element is `aria-hidden` — which can never announce anything
 *   and only occupies the accessibility tree. A consumer choosing this mode owns
 *   communicating the loading state by other means.
 *
 * In both modes:
 *
 * - `data-size` is reflected as an *attribute* rather than a class so the
 *   stylesheet can match `:host([data-size='…'])` and keep every dimension in
 *   a token.
 *
 * Deliberately absent: `aria-live` (redundant with the status role),
 * `aria-valuenow` and `role="progressbar"` (this indicator is indeterminate
 * and has no value semantics), and `tabindex` (it is not interactive).
 *
 * @example
 * ```html
 * @if (store.loading()) {
 *   <app-loading-spinner size="small" label="Loading portals…" />
 * }
 * ```
 *
 * MIGRATION: this component is a net-new affordance with no legacy ancestor,
 * and that is reported rather than papered over. The five in-scope DotNetNuke
 * admin trees are pure full-page-postback Web Forms — measured zero
 * occurrences of `UpdatePanel`, `UpdateProgress`, `ScriptManager` and
 * `AsyncPostBack` — so the browser's own page-load indicator was the only
 * progress affordance the legacy application had. Neither legacy stylesheet
 * declares a spinner, progress, loading, throbber or busy selector anywhere in
 * its inventory, and the one legacy progress image in the repository is
 * reachable only from trees that are out of scope. Nothing is being ported
 * here; the indicator is an addition demanded by the single-page model.
 *
 * MIGRATION: the host accessibility contract is net-new. The legacy admin
 * markup and both legacy stylesheets contain no ARIA whatsoever — measured
 * zero `role=` and `aria-` attributes — so the status role, the busy state and
 * the accessible name are all additions. Each is invisible and therefore
 * carries zero visual cost.
 *
 * MIGRATION: the default label wording is authored directly in English.
 * Localisation is not carried forward — neither the Angular localize runtime
 * nor any translation pipeline is part of the pinned dependency set — and no
 * legacy progress wording exists to inherit, so there is no resource key to
 * honour.
 *
 * MIGRATION: the three named `size` steps resolve to net-new spacing tokens in
 * the stylesheet rather than to literal dimensions. The legacy CSS has no
 * spacing system and declares `@media`, `border-radius` and `box-shadow` zero
 * times each, so every dimension, radius and elevation this component renders
 * is an addition — a deliberate visual refinement that alters no behaviour.
 *
 * MIGRATION: the indicator is static rather than animated, and no animation API
 * is reachable from here by design. The Angular animations package is absent
 * from the pinned dependency set, so no imperative animation exists, and the
 * stylesheet degrades to a static indeterminate presentation because the design
 * system exports neither a duration token nor a reduced-motion guard — both gaps
 * are reported in full in the sibling stylesheet. The legacy portal animated
 * nothing, so this diverges from it in nothing, and a static presentation
 * honours a reduced-motion preference for every reader without a media query.
 */
@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  imports: [],
  templateUrl: './loading-spinner.component.html',
  styleUrl: './loading-spinner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    // Every one of these is BOUND rather than static, and all three are driven
    // by the single `hasVisibleLabel` getter so the host's exposure to assistive
    // technology and the template's rendering cannot disagree. See that getter
    // for why the two modes are mutually exclusive.
    '[attr.role]': "hasVisibleLabel ? 'status' : null",
    '[attr.aria-busy]': "hasVisibleLabel ? 'true' : null",
    '[attr.aria-hidden]': "hasVisibleLabel ? null : 'true'",
    '[attr.data-size]': 'size',
  },
})
export class LoadingSpinnerComponent {
  /**
   * Visual scale of the indicator. A name, never a measurement: the stylesheet
   * resolves it to a token, so no dimension is expressible from here.
   *
   * Public by necessity, as is `label` — the strict input access check rejects a
   * non-public input at every consuming template.
   */
  @Input() size: LoadingSpinnerSize = 'medium';

  /**
   * Visible, announced description of what is loading.
   *
   * The template renders it only when non-empty, so a caller that genuinely wants
   * a bare indicator can pass an empty string. The default exists because an
   * indicator that announces nothing is an accessibility defect, and that outcome
   * must be a deliberate choice rather than the consequence of omitting the input.
   */
  @Input() label = 'Loading…';

  /**
   * Whether this indicator has label text worth announcing, and therefore which
   * of the component's two mutually exclusive accessibility modes applies.
   *
   * This getter is the single source of that decision. The host bindings for
   * `role`, `aria-busy` and `aria-hidden` all read it, and so does the template's
   * guard on the label element, which is what makes the two modes impossible to
   * desynchronise.
   *
   * WHY THIS EXISTS. The component supports a deliberate visual-only mode, in
   * which a caller passes an empty label to get a bare indicator. Previously the
   * host kept a STATIC `role="status"` and `aria-busy="true"` in that mode, while
   * the only element inside it — the spinning indicator — is `aria-hidden`. The
   * result was a live region with no content and no accessible name: `status`
   * takes its name from its contents rather than from an author-supplied name,
   * so there was nothing to announce and nothing to name it. Assistive
   * technology was handed an empty, permanently busy region that could never say
   * anything. That is strictly worse than being absent, because it occupies the
   * accessibility tree while conveying nothing.
   *
   * The two modes are therefore now genuinely separate:
   *
   * - LABELLED — `role="status"` and `aria-busy="true"` are present and the
   *   label renders as the region's content, which is the announcement. This is
   *   the default, reached by omitting the input.
   * - VISUAL-ONLY — the role and busy state are removed and the host is
   *   `aria-hidden`, so the component is entirely decorative. Nothing announces,
   *   which is honest: there is nothing to announce. A consumer choosing this
   *   mode owns communicating the loading state by other means, exactly as it
   *   already owns the loading state itself.
   *
   * The other two resolutions were considered and rejected. Requiring a
   * non-blank label would delete the visual-only mode, a documented capability.
   * Supplying an `aria-label` fallback would contradict this component's own
   * reasoning for having no `aria-label` at all — a separate author-supplied name
   * can diverge from the visible text, breaking parity between what is seen and
   * what is announced — and it would also re-announce a label the caller
   * explicitly asked not to show.
   *
   * WHITESPACE IS BLANK. The test trims, which closes a second, quieter case:
   * the previous truthiness check treated a whitespace-only label as present, so
   * `label=" "` rendered a label element containing nothing visible and produced
   * exactly the empty live region described above by a different route. Trimming
   * makes both spellings of "no label" behave identically.
   */
  protected get hasVisibleLabel(): boolean {
    return this.label.trim().length > 0;
  }
}
