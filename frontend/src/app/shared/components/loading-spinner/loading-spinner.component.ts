import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * The closed set of indicator sizes.
 *
 * Deliberately a string-literal union rather than an enumeration: the
 * workspace compiles with isolated modules, under which a compile-time
 * inlined enumeration cannot cross a module boundary. The union also gives
 * consumers compile-time checking of the literal they write in a template.
 *
 * Each step is a *name*, never a measurement — the stylesheet resolves it to a
 * design token, so no dimension is expressible from TypeScript.
 */
export type LoadingSpinnerSize = 'small' | 'medium' | 'large';

/**
 * Async progress indicator.
 *
 * A pure presentational leaf primitive: it holds no state beyond its two
 * inputs, performs no I/O, injects nothing, emits nothing, exposes no content
 * slot and implements no lifecycle hook. Its entire job is to project its two
 * inputs onto the host element.
 *
 * The caller decides *whether* to render it — typically from a feature store's
 * `loading` signal — so this component never owns, derives or toggles loading
 * state itself. That separation is why there is no `loading` input.
 *
 * Accessibility is carried entirely by the host element:
 *
 * - `role="status"` is an implicit *polite* live region: the visible label is
 *   the region's content, so assistive technology announces it without
 *   interrupting the user. The role takes its name from the author rather than
 *   from its contents, so the *accessible name* is deliberately left empty and
 *   the label reaches the user as region content instead — which is correct,
 *   because the role does not require a name. No `aria-label` is set on the
 *   host precisely because it would introduce a separate name that can diverge
 *   from the visible text, breaking parity between what is seen and what is
 *   announced.
 * - `aria-busy` is a static `'true'`. The element is only in the DOM while
 *   something is in flight, so there is nothing to toggle and nothing to lie
 *   about.
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
 * MIGRATION: the spin animation is expressed purely in CSS. The Angular
 * animations package is absent from the pinned dependency set, so no
 * imperative animation API is available, and none is used.
 */
@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  imports: [],
  templateUrl: './loading-spinner.component.html',
  styleUrl: './loading-spinner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'role': 'status',
    'aria-busy': 'true',
    '[attr.data-size]': 'size',
  },
})
export class LoadingSpinnerComponent {
  /**
   * Visual scale of the indicator.
   *
   * Reflected onto the host as `data-size`, which the stylesheet matches with
   * `:host([data-size='…'])`. Public by necessity: the workspace enables
   * strict input access modifiers, so a non-public input is a compile error in
   * every consuming template.
   */
  @Input() size: LoadingSpinnerSize = 'medium';

  /**
   * Visible, announced description of what is loading.
   *
   * The sibling template renders it only when non-empty, so a caller that
   * genuinely wants a bare indicator can pass an empty string. The default
   * exists because an indicator that announces nothing is an accessibility
   * defect, and it must never be reachable by omission — passing an empty
   * string has to be a deliberate choice, never the consequence of omitting
   * the input.
   *
   * Public for the same strict-input-access reason as `size`.
   */
  @Input() label = 'Loading…';
}
