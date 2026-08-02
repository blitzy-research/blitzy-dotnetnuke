import { Pipe, PipeTransform } from '@angular/core';

// The wording is taken from the legacy shared string resources but authored here: no
// translation runtime exists in this application, so there is nothing to resolve it from.
const AFFIRMATIVE_TEXT = 'Yes';

const NEGATIVE_TEXT = 'No';

/**
 * Renders a boolean flag as display text: `Yes` for `true` and `No` for `false`.
 *
 * The input is a non-nullable `boolean` deliberately. In the legacy model the marker for
 * an absent Boolean was `False` itself, so a nullable input would be indistinguishable
 * from a legitimate `false`; the transform therefore compares by identity and never tests
 * truthiness. Both branches yield non-empty text, because the legacy cell was never blank
 * in either state.
 *
 * Output is plain text, escaped by interpolation, and must never be bound as raw HTML.
 *
 * MIGRATION: the legacy grids showed these flags as a pair of mutually exclusive images
 * with no alternative text, so the state is now announced rather than silently drawn.
 * Neither image is carried forward; a consumer that wants a glyph supplies its own,
 * because nothing here emits markup or an asset reference.
 */
@Pipe({
  name: 'yesNo',
  standalone: true,
})
export class YesNoPipe implements PipeTransform {
  /**
   * Maps a boolean flag onto its display text.
   *
   * @param value The flag to render. `false` is meaningful data rather than an
   * absent value, and renders as the negative word instead of empty output.
   * @returns `Yes` when the flag is `true`, otherwise `No`. Never empty.
   */
  transform(value: boolean): string {
    if (value === true) {
      return AFFIRMATIVE_TEXT;
    }

    return NEGATIVE_TEXT;
  }
}
