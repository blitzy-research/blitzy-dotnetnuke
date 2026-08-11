import { formatDate } from '@angular/common';
import { Inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

export type DateDisplayMode = 'date' | 'datetime';

/** What every absent, blank or unusable input renders as: nothing at all. */
const EMPTY_DISPLAY_VALUE = '';

/**
 * The UTC calendar date of the legacy null-date sentinel, `0001-01-01`. The month is
 * held zero-based because that is what `Date#getUTCMonth` returns.
 */
const SENTINEL_UTC_YEAR = 1;
const SENTINEL_UTC_MONTH = 0;
const SENTINEL_UTC_DAY_OF_MONTH = 1;

/**
 * Matches an ISO-8601 date-time carrying no zone designator, capturing the date and time
 * halves so they can be re-joined as an explicit UTC instant.
 *
 * A serialiser writing a date of unspecified kind omits the trailing `Z`, and the language
 * then parses such a string as LOCAL time. Left alone that shifts the rendered day for
 * anyone either side of Greenwich, and can push the sentinel off `0001-01-01` so it leaks
 * onto the screen as a visible date.
 */
const ZONELESS_DATE_TIME =
  /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)$/;

/**
 * Spelled out rather than taken from the framework's `shortDate` token, which emits a
 * two-digit year and would print a year-9999 expiry as `99`.
 */
const SHORT_DATE_PATTERN = 'M/d/yyyy';

/** `a` is this framework's spelling of the day-period token, yielding `AM`/`PM`. */
const DATE_TIME_PATTERN = 'M/d/yyyy h:mm:ss a';

/**
 * The only two format patterns this pipe will ever hand to the framework formatter.
 *
 * SECURITY: written as `typeof` the two constants rather than as `string`, so the union is
 * the two literal values themselves. The formatter's FORMAT argument is its untrusted-input
 * vector, so this turns the invariant into a COMPILE-TIME one:
 * {@link DateDisplayPipe.render} cannot be handed a pattern that is not one of these two,
 * and no caller-supplied or dynamically assembled format string can reach the formatter.
 * Never widen this to `string` and never derive a pattern from a value that crosses the
 * wire; either change reintroduces the vector.
 */
type DisplayDatePattern = typeof SHORT_DATE_PATTERN | typeof DATE_TIME_PATTERN;

/**
 * Values are rendered in UTC, never in the visitor's zone. The class documentation records
 * why.
 */
const DISPLAY_TIME_ZONE = 'UTC';

/**
 * The greatest number of characters a candidate date string may carry and still be
 * considered.
 *
 * The wire contract is a single absolute ISO-8601 instant. The longest form the language's
 * own date-time grammar accepts - an expanded year, fractional seconds and a numeric
 * offset - runs to under 40 characters, so no longer string can parse. The bound is
 * therefore OBSERVATIONALLY EQUIVALENT to the existing unparseable-input path: such a
 * value already rendered as an empty cell. What it adds is that no normalisation pass and
 * no parse attempt is performed over a payload whose length is set by a remote response.
 *
 * It is applied to the TRIMMED value, deliberately not the raw one, so a legitimate value
 * padded with unusual whitespace still renders exactly as it does today. Behavioural
 * equivalence outranks shaving one linear pass.
 */
const MAX_WIRE_DATE_LENGTH = 64;

/**
 * Fallback locale, used only when this pipe is constructed directly rather than resolved
 * through the injector. It matches both the framework's own default and the culture the
 * legacy application was configured with.
 */
const FALLBACK_DISPLAY_LOCALE = 'en-US';

/**
 * The exhaustive set of wire shapes this pipe accepts.
 *
 * The platform's date parser is far more permissive than the wire contract, and its extra
 * tolerance is not harmless - it is silently WRONG. Each of these is accepted by
 * `new Date(...)` and turned into a plausible but incorrect instant:
 *
 * | Input          | yields     | would have displayed |
 * | -------------- | ---------- | -------------------- |
 * | `'0'`          | 2000-01-01 | `1/1/2000`           |
 * | `'2024'`       | 2024-01-01 | `1/1/2024`           |
 * | `'12/31/2024'` | 2024-12-31 | `12/31/2024`         |
 * | `'Sep 10 2024'`| 2024-09-10 | `9/10/2024`          |
 * | `'2024-9-10'`  | 2024-09-10 | `9/10/2024`          |
 *
 * Those are not equivalent renderings of a malformed value; they are confident assertions
 * about data the caller never sent. A numeric identifier that reached a date column through
 * a mismapped field would display as a January date rather than as an empty cell, which is
 * precisely the failure the legacy formatters' empty seed avoided.
 *
 * The pattern therefore admits only what the API emits: the round-trip form
 * (`YYYY-MM-DDTHH:mm:ss[.fffffff][Z|±HH:mm]`), the date-only form, and the space-separated
 * zone-less form the sibling normaliser rewrites. The fractional part is left unbounded
 * because .NET emits seven digits while other producers emit three, and precision beyond
 * milliseconds is unambiguous - the platform truncates it.
 *
 * Time-of-day RANGES are deliberately not encoded here. The parser already rejects an
 * out-of-range component outright, so the existing unparseable guard covers them without
 * duplicating the rule.
 */
const ISO_WIRE_FORMAT =
  /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$/;

/**
 * Reports whether a year, month and day denote a date that genuinely exists.
 *
 * This closes the half of the permissiveness gap a shape check cannot reach. The platform
 * does not reject an over-long day for its month; it silently ROLLS FORWARD into the next
 * one - `2024-02-30` becomes 2024-03-01, `2023-02-29` becomes 2023-03-01 and `2024-09-31`
 * becomes 2024-10-01. Each would have displayed a real-looking date one day past the end
 * of the intended month.
 *
 * The check is performed on the DATE FIELDS ALONE rather than by comparing the parsed
 * instant's UTC date back against the string, because a legitimate value carrying a numeric
 * offset may legitimately land on a different UTC day: `2024-09-10T01:00:00+02:00` is
 * 2024-09-09T23:00Z. A round-trip comparison would reject that valid value, so only the
 * calendar triple is validated and the offset is left to the parser.
 *
 * The probe is built with `setUTCFullYear` rather than `Date.UTC`, because `Date.UTC` maps
 * years 0-99 onto 1900-1999: a probe for `0050-06-15` would land on 1950 and the date would
 * be judged non-existent and blanked. `setUTCFullYear` applies the year literally, so every
 * year from 1 to 9999 is validated as written. The difference is observable only for years
 * 2-99 - for the sentinel year 1 both constructions end in an empty cell - which is why the
 * suite pins a non-sentinel low year explicitly. Without that expectation this choice would
 * be untested and the guard could silently degenerate into a filter on small years.
 */
function isRealCalendarDate(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12 || day < 1 || day > 31) {
    return false;
  }

  const probe = new Date(0);
  probe.setUTCFullYear(year, month - 1, day);
  probe.setUTCHours(0, 0, 0, 0);

  return (
    probe.getUTCFullYear() === year &&
    probe.getUTCMonth() === month - 1 &&
    probe.getUTCDate() === day
  );
}

/**
 * Normalises a trimmed input into a candidate the platform parses as an absolute UTC
 * instant.
 *
 * Expressing the rewrite as a replacement rather than as capture-group indexing means the
 * non-matching cases need no branch and no absent group can be interpolated into the result.
 */
function toAbsoluteUtcCandidate(trimmed: string): string {
  return trimmed.replace(ZONELESS_DATE_TIME, '$1T$2Z');
}

/**
 * Reports whether an instant falls on the legacy null-date sentinel.
 *
 * The comparison is date-part-only, as the legacy test was, so any instant on `0001-01-01`
 * UTC counts and not only exact midnight. The UTC accessors keep the answer independent of
 * the host zone.
 */
function isSentinelDate(instant: Date): boolean {
  return (
    instant.getUTCFullYear() === SENTINEL_UTC_YEAR &&
    instant.getUTCMonth() === SENTINEL_UTC_MONTH &&
    instant.getUTCDate() === SENTINEL_UTC_DAY_OF_MONTH
  );
}

/**
 * Renders an absolute UTC ISO-8601 date string as display text.
 *
 * Each legacy administration screen carried its own private formatter and they disagreed:
 * three of the four emitted a calendar date alone and one also printed the time of day.
 * Both shapes are offered here, with the date-only shape as the default and the outlier
 * reproduced by asking for `'datetime'`, so each screen stays comparable with the page it
 * replaces.
 *
 * Values render in UTC rather than in the visitor's zone. The wire contract is an absolute
 * instant and the legacy pages printed the stored calendar date verbatim, so localising it
 * would shift dates across midnight - a full day for `2024-07-04T02:00:00Z` viewed from
 * `America/Los_Angeles` - and would also split the sentinel test from the rendered output.
 *
 * Behaviour:
 *
 * - The legacy null-date sentinel renders as an empty cell in both modes, and the test for
 *   it is date-part-only, so any instant on `0001-01-01` UTC counts.
 * - `9999-12-31` is a genuine value meaning "perpetual" and renders normally as
 *   `12/31/9999`. Presenting it as "never expires" is the calling screen's decision.
 * - `null`, `undefined`, a blank string and an unparseable string all render as the same
 *   empty cell - never a placeholder, never an error message.
 * - Only the wire shapes the API emits are accepted, and the accepted shape must also
 *   denote a date that exists on the calendar. A bare number such as `'0'`, a
 *   locale-formatted string such as `'12/31/2024'`, an unpadded month such as `'2024-9-10'`
 *   or a non-existent day such as `'2024-02-30'` renders as the same empty cell. This
 *   deliberately NARROWS the platform's parser, which would otherwise turn each into a
 *   plausible but incorrect date; see `ISO_WIRE_FORMAT` and `isRealCalendarDate`.
 * - An offset-bearing value is NOT rejected merely because its UTC calendar date differs
 *   from the date written in its own text. `2024-07-04T02:00:00+05:00` is genuinely July 3
 *   in UTC and renders as such, which is why the calendar check validates the date triple
 *   alone and leaves the offset to the parser.
 * - So does any input that is not a string at all. The declared parameter type says a
 *   template cannot pass one, and strict template checking enforces that; but the value
 *   normally arrives off the wire, typed only by an interface that is erased at runtime, so
 *   a malformed response can still deliver a number or an object. That is treated as
 *   unusable input rather than allowed to throw inside a template expression, which would
 *   abort the whole view rather than one cell.
 * - So does a candidate longer than any parseable ISO-8601 instant, rejected before it is
 *   normalised or parsed.
 * - The result is plain text. It is never markup and must never be bound through a
 *   raw-markup sink, because legacy resource wording is untrusted content.
 * - The pipe is pure and is never marked otherwise, so it re-runs only when its arguments
 *   change. It reads no clock and holds no state, so identical inputs always produce
 *   identical output.
 *
 * A consumer selects `'datetime'` only where the legacy screen it replaces printed a time
 * of day; every other date field takes the default.
 *
 * @example
 * ```html
 * {{ expiryDate | dateDisplay }}
 * {{ lastLoginDate | dateDisplay: 'datetime' }}
 * ```
 */
@Pipe({
  name: 'dateDisplay',
  standalone: true,
})
export class DateDisplayPipe implements PipeTransform {
  constructor(
    @Inject(LOCALE_ID) private readonly locale: string = FALLBACK_DISPLAY_LOCALE,
  ) {}

  /**
   * Renders the value in one of the two shapes.
   *
   * MIGRATION: the legacy membership screen replaced an expiry date already in the past
   * with a word instead of showing the date. That reads the clock, so it belongs to the
   * screen that owns a clock and is deliberately not done here.
   *
   * @param value An absolute UTC ISO-8601 date string, or nothing. A `Date` is not
   * accepted: dates cross the wire as strings and no consumer holds a parsed one.
   * @param mode Which of the two shapes to render. Defaults to `'date'`, the shape most of
   * the legacy formatters emitted.
   * @returns The formatted date, or empty text when there is nothing meaningful to show.
   */
  transform(
    value: string | null | undefined,
    mode: DateDisplayMode = 'date',
  ): string {
    // MIGRATION: an absent value, an unusable one and the legacy null-date sentinel all render
    // identically, as an empty cell. The erasure is confined to the display layer; the wire
    // still distinguishes them, because the API serialises a minimum-value date as a real
    // ISO-8601 string rather than omitting the property or writing null.
    //
    // Every one of those decisions - and the deliberate NARROWING of the platform's parser
    // that makes them safe - now lives in {@link parseDisplayInstant}, which this pipe and any
    // screen that needs to reason ABOUT the instant both call. See that function for the whole
    // rationale; it moved there unchanged so that the two can never disagree about which
    // values are usable.
    const instant: Date | null = parseDisplayInstant(value);
    if (instant === null) {
      return EMPTY_DISPLAY_VALUE;
    }

    if (mode === 'datetime') {
      return this.render(instant, DATE_TIME_PATTERN);
    }
    return this.render(instant, SHORT_DATE_PATTERN);
  }

  /**
   * Applies one of the two fixed patterns, degrading to empty text if the formatter rejects
   * the request.
   *
   * The guard is not defensive padding: the formatter throws for a locale whose data has not
   * been registered, the legacy formatters wrapped their bodies in a handler that returned
   * their empty seed, and letting an exception escape a template expression would take down
   * the whole view.
   *
   * MIGRATION: no third-party date library is introduced - the dependency set is
   * deliberately small and none is available - so all formatting goes through the
   * framework's own date formatter. The locale is injected rather than hard-coded, but the
   * two patterns are fixed to the shapes the legacy screens produced under their configured
   * culture. The legacy runtime also switched culture per portal and per user; that is not
   * reproduced, because no translation runtime is installed here.
   *
   * @param instant The already-validated instant to render.
   * @param pattern One of the two fixed patterns. Typed as the closed union
   * {@link DisplayDatePattern} rather than as `string`, for the security reason recorded on
   * that type.
   * @returns The formatted text, or an empty cell if the formatter rejects the request.
   */
  private render(instant: Date, pattern: DisplayDatePattern): string {
    try {
      return formatDate(instant, pattern, this.locale, DISPLAY_TIME_ZONE);
    } catch {
      return EMPTY_DISPLAY_VALUE;
    }
  }
}

/**
 * Parses a wire date into the instant the display layer will actually show, or `null` when there
 * is nothing meaningful to show.
 *
 * ⚠ THIS IS THE ONE PLACE THAT DECIDES WHETHER A WIRE DATE IS USABLE, AND BOTH CALLERS MUST USE
 * IT. {@link DateDisplayPipe} renders whatever this returns and paints an empty cell for `null`;
 * a screen that needs to reason about the instant - is this expiry in the past? - needs the same
 * verdict, because a screen that accepted a value the pipe rejects would paint a qualifier beside
 * an empty cell, and one that rejected a value the pipe accepts would paint a date with no
 * qualifier. Re-implementing the checks at a call site is what makes those two disagreements
 * possible, so the chain is stated once here.
 *
 * The behaviour, unchanged from where it was inlined in the pipe:
 *
 * - Anything that is not a string is unusable. The test is `typeof`, not a pair of equality
 *   checks against `null` and `undefined`, and that is load-bearing: a value reaching here has
 *   usually come off the wire, typed only by an interface that is erased at runtime, so a
 *   malformed or evolved response can deliver a number, an object or an array where the contract
 *   promised a string. An equality check lets such a value through to `.trim()`, which throws -
 *   and because the pipe runs inside a template expression, that exception aborts the whole view
 *   rather than one cell.
 * - A blank string is unusable.
 * - A candidate longer than any parseable ISO-8601 instant is rejected without being normalised
 *   or parsed; see {@link MAX_WIRE_DATE_LENGTH}. The bound applies to the TRIMMED value, so a
 *   legitimate date padded with unusual whitespace is not blanked.
 * - The wire SHAPE is validated before the value is parsed, which deliberately NARROWS the
 *   platform's parser rather than reproducing it: `new Date('0')` yields 2000-01-01 and
 *   `new Date('Sep 10 2024')` succeeds, so without this guard a mismapped identifier or a
 *   locale-formatted string would render as a confident, wrong date.
 * - A syntactically well-formed date that does not exist on the calendar is rejected, because
 *   the platform rolls such a value FORWARD instead of refusing it - `2024-02-30` becomes
 *   2024-03-01 - which is indistinguishable on screen from a real date and therefore worse than
 *   showing nothing. Validated on the date triple alone, so an offset-bearing value that
 *   legitimately lands on a different UTC day is preserved.
 * - The legacy null-date sentinel is rejected: year one predates modern zone rules, so
 *   formatting it would emit a wrong and alarming date. `9999-12-31` is deliberately NOT treated
 *   this way - it is a real "perpetual" expiry and is returned like any other instant.
 *
 * @param value An absolute UTC ISO-8601 date string, or nothing.
 * @returns The parsed instant, or `null` when the value is absent, unparseable or the sentinel.
 */
export function parseDisplayInstant(value: string | null | undefined): Date | null {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();
  if (trimmed === EMPTY_DISPLAY_VALUE) {
    return null;
  }

  if (trimmed.length > MAX_WIRE_DATE_LENGTH) {
    return null;
  }

  const wireFields = ISO_WIRE_FORMAT.exec(trimmed);
  if (wireFields === null) {
    return null;
  }

  // The month and day groups are guaranteed present by the pattern, because the date portion is
  // not optional within it.
  if (
    !isRealCalendarDate(
      Number(wireFields[1]),
      Number(wireFields[2]),
      Number(wireFields[3]),
    )
  ) {
    return null;
  }

  const instant = new Date(toAbsoluteUtcCandidate(trimmed));

  // An explicit numeric test, because a date instance is truthy even when it holds no usable
  // value - and the platform's own invalid-date wording must never reach a cell.
  if (Number.isNaN(instant.getTime())) {
    return null;
  }

  return isSentinelDate(instant) ? null : instant;
}
