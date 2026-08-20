import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Resolves the four-digit calendar year from the platform clock.
 *
 * @returns The current four-digit calendar year in the host's local time zone.
 */
function resolveCurrentYear(): number {
  return new Date().getFullYear();
}

// MIGRATION: `(c)` is retained as literal ASCII, byte-exact with the measured legacy resource value. The
// typographic copyright glyph is deliberately NOT substituted, because altering the rendered characters
// would be a silent divergence.
@Component({
  selector: 'app-footer',
  standalone: true,
  imports: [],
  templateUrl: './footer.component.html',
  styleUrl: './footer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FooterComponent {
  /** The four-digit year rendered in the copyright line. */
  readonly currentYear: number = resolveCurrentYear();

  readonly copyrightText: string = `Copyright (c) ${this.currentYear} DotNetNuke`;
}
