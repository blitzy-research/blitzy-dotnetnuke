import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// MIGRATION: the wording is authored here rather than resolved through a resource lookup,
// so it is not per-locale. Module-private on purpose: the public surface of this module is
// the component class and its one `message` member.
const DEFAULT_EMPTY_STATE_MESSAGE = 'No records found.';

/**
 * Separator used to fold a repeated route parameter back into one sentence.
 *
 * A bare comma with no trailing space is deliberate rather than stylistic. The legacy
 * analogue read a query-string collection whose accessor folds duplicate values into one
 * comma-delimited string with no padding, so joining with a comma and a space would render
 * wording the legacy page never produced.
 *
 * The framework's own first-value-wins policy for a repeated parameter is deliberately NOT
 * adopted, because it discards data the legacy page displayed.
 *
 * The fold is total, so blank duplicates are joined too: two blank values yield a lone
 * separator, and one blank among real wording keeps its trailing separator. That is untidy
 * but it is what the legacy page rendered, and it is pinned by its own expectation.
 * Filtering blank elements out first would read as tidier while being a divergence.
 */
const DUPLICATE_VALUE_SEPARATOR = ',';

/**
 * Largest number of UTF-16 code units retained from a supplied message.
 *
 * This is a memory and layout bound, NOT the overflow remedy. Long-word overflow is owned
 * by the sibling stylesheet, which breaks anywhere and caps the measure; a caller's
 * sentence must reflow rather than be clipped, and that stays true of every message within
 * this bound. The bound exists only so an unbounded external payload cannot be retained and
 * laid out verbatim.
 *
 * The value sits far above the longest legacy string that is genuinely one explanatory
 * sentence, so no message this component is meant to render can reach it; the few legacy
 * values that exceed it are long-form release-note and help-page bodies, which this
 * component does not render.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * Folds an arbitrary runtime payload into a bounded candidate string.
 *
 * Pure and module-private: it depends on no component state, which keeps the setter body a
 * readable mirror of the legacy branch it reproduces instead of burying that branch under
 * normalisation.
 *
 * The parameter is declared `unknown` rather than the setter's own union because setting an
 * input programmatically performs no runtime type check, so this function has to stay total
 * over every shape the router can hand it.
 *
 * @param value Caller-supplied payload of any runtime shape.
 * @returns A string of at most {@link MAX_MESSAGE_LENGTH} code units. Returns the empty
 * string for every payload carrying no usable wording, which lets the caller's single blank
 * test fail closed to the documented default.
 */
function candidateFrom(value: unknown): string {
  let normalised: string;

  if (typeof value === 'string') {
    normalised = value;
  } else if (Array.isArray(value)) {
    // `Array.isArray` narrows to `any[]`, which would make the loop variable
    // implicitly `any` and silently disable every check below. Rebinding through
    // `readonly unknown[]` restores an honest element type with no assertion,
    // because `any` is assignable to it.
    const elements: readonly unknown[] = value;
    const stringElements: string[] = [];

    for (const element of elements) {
      // Filtering before joining is what makes this total. `Array.prototype.join`
      // coerces its elements, and coercion throws on a symbol and on any object
      // whose `toString` throws. For the router's own duplicate-key arrays every
      // element is already a string, so the filter is a no-op in the real case.
      if (typeof element === 'string') {
        stringElements.push(element);
      }
    }

    normalised = stringElements.join(DUPLICATE_VALUE_SEPARATOR);
  } else {
    // Fail closed. `String(value)` is deliberately not used: it throws on a
    // symbol and otherwise yields diagnostic noise such as an object tag, which
    // is worse wording than the documented default. `null` and `undefined` land
    // here too, which preserves the pre-existing blank-to-default behaviour
    // exactly.
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
 * Presentational component that explains the absence of content, then optionally offers one
 * projected call to action.
 *
 * It serves two readings. As an in-page zero-result state it stands in for a populated
 * table or list, and callers typically project an "Add New ..." affordance into the content
 * slot. As a not-found view it is the body of the catch-all route, which supplies its own
 * wording and projects nothing. Serving both is why it injects nothing, takes no constructor
 * parameters, needs no navigation awareness and assumes no surrounding table, grid or list
 * context - it renders correctly as a full-page view and inside a card alike. None of those
 * absences may be "tidied up" into a dependency later.
 *
 * MIGRATION: the legacy admin grids declared no empty-result template, so an empty result
 * set rendered silently. Explaining the absence instead is a deliberate improvement, and the
 * legacy pane class this component is named after was a layout-collapse utility carrying no
 * messaging of its own.
 *
 * Wording can arrive from the router rather than from a template binding, which constrains
 * how `message` is declared. The router's component-input binder merges route data with the
 * route's parameters and writes `merged[name]` for every declared input, so three things
 * follow: the member must carry `@Input()`, because the binder reports an unknown property
 * for anything else and then silently does nothing; it must be public, or it will not
 * compile at the consumer; and an input the route does not populate is written as
 * `undefined` rather than skipped, which is the case the setter below absorbs.
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
   * Sets the sentence explaining why no content is shown.
   *
   * The write type is deliberately wider than the read type, because `null` and `undefined`
   * are values the router's binder genuinely passes and template expressions over optional
   * model fields are routinely nullable. Widening here keeps those call sites honest instead
   * of pushing a non-null assertion onto the caller.
   *
   * What happens to a supplied value, in this order:
   *
   * - a string is taken as it stands, and an array is folded into one separator-joined
   *   string with every element that is not a string DISCARDED rather than coerced;
   * - any other runtime shape - a number or an object arriving off the wire despite the
   *   declared type - yields the empty string;
   * - the result is truncated to {@link MAX_MESSAGE_LENGTH} UTF-16 code units, and one
   *   further unit is dropped if that cut would leave a trailing lone high surrogate, so the
   *   retained text never ends in half a character;
   * - only then is it tested for blankness. Blank or whitespace-only selects the default,
   *   which is why this is an accessor rather than a plain field: a plain field would let an
   *   explicitly bound empty value blank the view. Whitespace-only counts as blank because
   *   whitespace collapses in HTML.
   *
   * Whatever survives all of that is stored verbatim, surrounding whitespace included, so a
   * caller that deliberately indents its wording gets what it asked for.
   *
   * The value is untrusted: it must never be bound to a raw-markup sink, and no
   * sanitiser-backed companion member may be added.
   *
   * @param value Caller-supplied wording, the values of a repeated route parameter, or a
   * blank value to request the default.
   */
  @Input()
  public set message(value: string | readonly string[] | null | undefined) {
    // Normalising and bounding BEFORE the blank test is load-bearing: a payload of
    // 2000 spaces must bound to 1024 spaces and then still be recognised as blank,
    // and an array of empty strings must fold to an empty string and then select
    // the default. Testing first would let either case store unusable wording.
    const candidate = candidateFrom(value);
    this.resolvedMessage = candidate.trim().length > 0 ? candidate : DEFAULT_EMPTY_STATE_MESSAGE;
  }

  public get message(): string {
    return this.resolvedMessage;
  }
}
