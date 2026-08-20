import { Pipe, PipeTransform } from '@angular/core';

// The wording is taken from the legacy shared string resources but authored here: no
// translation runtime exists in this application, so there is nothing to resolve it from.
const AFFIRMATIVE_TEXT = 'Yes';

const NEGATIVE_TEXT = 'No';

/**
 * Renders a boolean flag as display text: `Yes` for `true` and `No` for `false`. The input is a
 * non-nullable `boolean` deliberately.
 */
@Pipe({
  name: 'yesNo',
  standalone: true,
})
export class YesNoPipe implements PipeTransform {
  /**
   * Maps a boolean flag onto its display text.
   *
   * @param value The flag to render.
   * @returns `Yes` when the flag is `true`, otherwise `No`.
   */
  transform(value: boolean): string {
    if (value === true) {
      return AFFIRMATIVE_TEXT;
    }

    return NEGATIVE_TEXT;
  }
}
