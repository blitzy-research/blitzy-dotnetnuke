import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Resolves the four-digit calendar year from the platform clock.
 *
 * The one clock read for this band, lifted to module scope so the value has a single
 * origin. Two independent reads - one in the class and another in the markup or a
 * specification - could straddle a New Year boundary and disagree; one named seam makes
 * that impossible, and the paired specification asserts that the rendered year is the
 * value this function returned rather than a literal year.
 *
 * @returns The current four-digit calendar year in the host's local time zone.
 */
function resolveCurrentYear(): number {
  return new Date().getFullYear();
}

// MIGRATION: the copyright line is STATIC, where the legacy band rendered a per-portal,
// database-driven value - the portal's own footer text when non-empty, otherwise a formatted
// "Copyright (c) <year> <portal name>". Neither the admin-editable override nor the per-portal
// name substitution is carried forward: `GET /api/v1/auth/me` exposes the portal identifier and
// display name but requires an authenticated caller, and this band renders on the
// unauthenticated login route as well, while `GET /api/v1/portals/{id}` is an administrative read
// a visitor definitionally cannot perform. Resolving the band at build time is what lets it
// render identically before and after sign-in, and the portal-name substitution resolves to the
// fixed product designation `DotNetNuke`, taken from the repository's own committed service
// catalogue and published documentation rather than invented.
//
// MIGRATION: `(c)` is retained as literal ASCII, byte-exact with the measured legacy resource
// value. The typographic copyright glyph is deliberately NOT substituted, because altering the
// rendered characters would be a silent divergence.
//
// MIGRATION: the year is read once at construction, through the single named seam declared above,
// and never in the paired markup - a markup-side clock read would re-evaluate on every
// change-detection pass and could not be asserted against. The legacy footer text was
// admin-authored and emitted as live markup; no value in this band may ever be bound as markup,
// and the template interpolates plain text only, which the framework escapes.
//
// MIGRATION: four legacy mechanisms are not reproduced. The footer root-links strip and the
// Privacy Statement and Terms Of Use skin objects merely duplicated the primary navigation, which
// the shell exposes exactly once through its single navigation landmark, and the route table
// declares no privacy or terms route, so either link would dangle. Runtime dynamic skin loading
// and the ordered, cached runtime stylesheet cascade are replaced by this static component and one
// build-time bundle. View state, the multipart server form and the hidden scroll and variable
// inputs are eliminated, so this band posts nothing back and scroll restoration is handled once by
// the router's in-memory scrolling configuration. And localisation is not ported: the measured
// legacy resource files are read for wording only, so the English wording is authored directly in
// the paired template.
@Component({
  selector: 'app-footer',
  standalone: true,
  imports: [],
  templateUrl: './footer.component.html',
  styleUrl: './footer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FooterComponent {
  /**
   * The four-digit year rendered in the copyright line.
   *
   * Read once at construction through {@link resolveCurrentYear} and immutable
   * thereafter, so on-push change detection never re-evaluates it and a plain
   * `readonly` field carries no reactivity it would never use. Every other member, and
   * the paired specification, derives from this one value rather than consulting the
   * clock again.
   */
  readonly currentYear: number = resolveCurrentYear();

  readonly copyrightText: string = `Copyright (c) ${this.currentYear} DotNetNuke`;
}
