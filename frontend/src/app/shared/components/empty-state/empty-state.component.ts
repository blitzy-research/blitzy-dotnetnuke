import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

const DEFAULT_EMPTY_STATE_MESSAGE = 'No records found.';

/**
 * Separator used to fold a repeated route parameter back into one sentence. A bare comma with no trailing
 * space is deliberate rather than stylistic.
 */
const DUPLICATE_VALUE_SEPARATOR = ',';

/** Largest number of UTF-16 code units retained from a supplied message. */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * Folds an arbitrary runtime payload into a bounded candidate string.
 *
 * @param value Caller-supplied payload of any runtime shape.
 * @returns A string of at most {@link MAX_MESSAGE_LENGTH} code units.
 */
function candidateFrom(value: unknown): string {
  let normalised: string;

  if (typeof value === 'string') {
    normalised = value;
  } else if (Array.isArray(value)) {
    const elements: readonly unknown[] = value;
    const stringElements: string[] = [];

    for (const element of elements) {
      // Filtering before joining is what makes this total. `Array.prototype.join` coerces its elements, and
      // coercion throws on a symbol and on any object whose `toString` throws.
      if (typeof element === 'string') {
        stringElements.push(element);
      }
    }

    normalised = stringElements.join(DUPLICATE_VALUE_SEPARATOR);
  } else {
    normalised = '';
  }

  if (normalised.length <= MAX_MESSAGE_LENGTH) {
    return normalised;
  }

  const truncated = normalised.slice(0, MAX_MESSAGE_LENGTH);
  const lastUnit = truncated.charCodeAt(truncated.length - 1);
  // Step back off a lone high surrogate so the retained text never ends in half a
  // code point, which a renderer shows as the U+FFFD replacement character.
  const endsOnHighSurrogate = lastUnit >= 0xd800 && lastUnit <= 0xdbff;

  return endsOnHighSurrogate ? truncated.slice(0, truncated.length - 1) : truncated;
}

/**
 * Presentational component that explains the absence of content, then optionally offers one projected
 * call to action. It serves two readings.
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [],
  templateUrl: './empty-state.component.html',
  styleUrl: './empty-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmptyStateComponent {
  private resolvedMessage: string = DEFAULT_EMPTY_STATE_MESSAGE;

  /**
   * Sets the sentence explaining why no content is shown. The write type is deliberately wider than the
   * read type, because `null` and `undefined` are values the router's binder genuinely passes and
   * template expressions over optional model fields are routinely nullable.
   *
   * @param value Caller-supplied wording, the values of a repeated route parameter, or a blank value to
   * request the default.
   */
  @Input()
  public set message(value: string | readonly string[] | null | undefined) {
    // Normalising and bounding BEFORE the blank test is load-bearing: a payload of 2000 spaces must bound
    // to 1024 spaces and then still be recognised as blank, and an array of empty strings must fold to an
    // empty string and then select the default. Testing first would let either case store unusable wording.
    const candidate = candidateFrom(value);
    this.resolvedMessage = candidate.trim().length > 0 ? candidate : DEFAULT_EMPTY_STATE_MESSAGE;
  }

  public get message(): string {
    return this.resolvedMessage;
  }
}
