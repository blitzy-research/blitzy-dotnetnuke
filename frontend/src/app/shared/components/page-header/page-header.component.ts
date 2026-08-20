import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Page title and action bar for the administration screens: a single page-level heading, an optional line
 * of supporting text beneath it, and a slot for the host screen's page-level actions. Emits no landmark
 * element and no landmark role.
 */
/**
 * @param value The raw bound title.
 * @returns The trimmed title, guaranteed non-blank.
 * @throws Error if the supplied title is empty or contains only white space.
 */
export function requireNonBlankTitle(value: string): string {
  // The parameter is typed `string`, but a JavaScript caller or an `any`-typed binding can still deliver
  // null or undefined, and `.trim()` on either would throw a TypeError whose message names neither this
  // component nor this input.
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
 * Normalises the `subtitle` input, collapsing a blank one to absent. The subtitle is optional, so unlike
 * the title a blank value is not an error to report - it is simply nothing to render.
 *
 * @param value The raw bound subtitle, which may be absent.
 * @returns The trimmed subtitle, or `undefined` when there is nothing to render.
 */
export function normaliseOptionalSubtitle(value: string | undefined): string | undefined {
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
  host: { '[attr.title]': 'null' },
})
export class PageHeaderComponent {
  // The member name collides with the global HTML `title` attribute. The name is kept because the fixed
  // shared-library API mandates it, and the value is never published to the host element: no host entry
  // writes it, no host-property decorator exposes it, and the paired template carries no `[title]` binding.
  @Input({ required: true, transform: requireNonBlankTitle })
  title!: string;

  /** Optional supporting text rendered beneath the title. */
  @Input({ transform: normaliseOptionalSubtitle }) subtitle?: string;
}
