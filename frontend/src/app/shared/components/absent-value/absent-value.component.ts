import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The mark a reader SEES where a value is absent. An em dash, which is the mark three of the four listings
 * had already chosen for themselves; the fourth painted nothing at all.
 */
const ABSENT_VALUE_MARK = '\u2014';

/**
 * The words a reader HEARS in place of that mark. Fixed rather than configurable, because one wording for
 * every absent value is the whole reason this component exists: the four conventions it replaces included two
 * different sentences for the same state.
 */
const ABSENT_VALUE_DESCRIPTION = 'not recorded';

/**
 * The one rendering of an absent value, for every screen that has one to render.
 *
 * ⚠ IT REPLACES FOUR DIFFERENT CONVENTIONS, AND THE WORST OF THEM WAS EMPTINESS. Measured across the
 * listings: six of seven portal expiry cells were entirely blank - empty text, whitespace-only content and no
 * children at all, so neither a sighted reader nor a screen reader was told anything - while elsewhere an
 * absent value appeared as an em dash inherited in body-text black, as a muted em dash, and as an em dash
 * followed by one of two different screen-reader sentences. A reader moving between two listings could not
 * tell "nothing recorded" from "nothing rendered".
 *
 * The split is deliberate and is the same one the listings that got it right already used: the MARK is
 * decorative and hidden from assistive technology, and the WORDS are the content, exposed only to it. A
 * sighted reader is not read a sentence on every row of a full page, and a screen-reader user is not read a
 * punctuation character.
 *
 * Deliberately absent: any input. A per-caller description would reintroduce exactly the divergence this
 * exists to end, and a per-caller mark would make the sighted convention inconsistent again.
 */
@Component({
  selector: 'app-absent-value',
  standalone: true,
  imports: [],
  template: `<span class="absent-value__mark" aria-hidden="true">{{ mark }}</span
    ><span class="absent-value__description" data-visually-hidden>{{ description }}</span>`,
  styleUrl: './absent-value.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AbsentValueComponent {
  /** {@link ABSENT_VALUE_MARK} — painted, and hidden from assistive technology. */
  protected readonly mark = ABSENT_VALUE_MARK;

  /** {@link ABSENT_VALUE_DESCRIPTION} — exposed to assistive technology, and hidden from the painted page. */
  protected readonly description = ABSENT_VALUE_DESCRIPTION;
}

export { ABSENT_VALUE_DESCRIPTION, ABSENT_VALUE_MARK };
