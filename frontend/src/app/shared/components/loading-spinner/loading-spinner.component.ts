import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

export type LoadingSpinnerSize = 'small' | 'medium' | 'large';

/**
 * Async progress indicator. A presentational leaf: no state beyond its two inputs, no I/O, nothing
 * injected or emitted, no content slot and no lifecycle hook.
 */
@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  imports: [],
  templateUrl: './loading-spinner.component.html',
  styleUrl: './loading-spinner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    // NO `aria-busy` IS DECLARED HERE, and its absence is deliberate rather than an omission — see the
    // "Deliberately absent" list on the class for the full reasoning.
    '[attr.role]': "hasVisibleLabel ? 'status' : null",
    '[attr.aria-hidden]': "hasVisibleLabel ? null : 'true'",
    '[attr.data-size]': 'size',
  },
})
export class LoadingSpinnerComponent {
  /**
   * Visual scale of the indicator. A name, never a measurement: the stylesheet resolves it to a token, so
   * no dimension is expressible from here.
   */
  @Input() size: LoadingSpinnerSize = 'medium';

  /**
   * Visible, announced description of what is loading. The template renders it only when non-empty, so a
   * caller that genuinely wants a bare indicator can pass an empty string.
   */
  @Input() label = 'Loading…';

  protected get hasVisibleLabel(): boolean {
    return this.label.trim().length > 0;
  }
}
