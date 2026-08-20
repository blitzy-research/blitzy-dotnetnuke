import { formatDate } from '@angular/common';
import { Inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

export type DateDisplayMode = 'date' | 'datetime';

/** What every absent, blank or unusable input renders as: nothing at all. */
const EMPTY_DISPLAY_VALUE = '';

/**
 * The UTC calendar date of the legacy null-date sentinel, `0001-01-01`. The month is held zero-based
 * because that is what `Date#getUTCMonth` returns.
 */
const SENTINEL_UTC_YEAR = 1;
const SENTINEL_UTC_MONTH = 0;
const SENTINEL_UTC_DAY_OF_MONTH = 1;

/**
 * Matches an ISO-8601 date-time carrying no zone designator, capturing the date and time halves so they
 * can be re-joined as an explicit UTC instant. A serialiser writing a date of unspecified kind omits the
 * trailing `Z`, and the language then parses such a string as LOCAL time.
 */
const ZONELESS_DATE_TIME =
  /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)$/;

/**
 * Spelled out rather than taken from the framework's `shortDate` token, which emits a two-digit year and
 * would print a year-9999 expiry as `99`.
 */
const SHORT_DATE_PATTERN = 'M/d/yyyy';

/** `a` is this framework's spelling of the day-period token, yielding `AM`/`PM`. */
const DATE_TIME_PATTERN = 'M/d/yyyy h:mm:ss a';

/**
 * The only two format patterns this pipe will ever hand to the framework formatter. SECURITY: written as
 * `typeof` the two constants rather than as `string`, so the union is the two literal values themselves.
 */
type DisplayDatePattern = typeof SHORT_DATE_PATTERN | typeof DATE_TIME_PATTERN;

/** Values are rendered in UTC, never in the visitor's zone. */
const DISPLAY_TIME_ZONE = 'UTC';

/**
 * The greatest number of characters a candidate date string may carry and still be considered. The wire
 * contract is a single absolute ISO-8601 instant.
 */
const MAX_WIRE_DATE_LENGTH = 64;

/**
 * Fallback locale, used only when this pipe is constructed directly rather than resolved through the
 * injector. It matches both the framework's own default and the culture the legacy application was
 * configured with.
 */
const FALLBACK_DISPLAY_LOCALE = 'en-US';

/** The exhaustive set of wire shapes this pipe accepts. */
const ISO_WIRE_FORMAT =
  /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$/;

/**
 * Reports whether a year, month and day denote a date that genuinely exists. This closes the half of the
 * permissiveness gap a shape check cannot reach.
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

/** Normalises a trimmed input into a candidate the platform parses as an absolute UTC instant. */
function toAbsoluteUtcCandidate(trimmed: string): string {
  return trimmed.replace(ZONELESS_DATE_TIME, '$1T$2Z');
}

/**
 * Reports whether an instant falls on the legacy null-date sentinel. The comparison is date-part-only, as
 * the legacy test was, so any instant on `0001-01-01` UTC counts and not only exact midnight.
 */
function isSentinelDate(instant: Date): boolean {
  return (
    instant.getUTCFullYear() === SENTINEL_UTC_YEAR &&
    instant.getUTCMonth() === SENTINEL_UTC_MONTH &&
    instant.getUTCDate() === SENTINEL_UTC_DAY_OF_MONTH
  );
}

/**
 * Renders an absolute UTC ISO-8601 date string as display text. Each legacy administration screen carried
 * its own private formatter and they disagreed: three of the four emitted a calendar date alone and one
 * also printed the time of day.
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
   * @param value An absolute UTC ISO-8601 date string, or nothing.
   * @param mode Which of the two shapes to render.
   * @returns The formatted date, or empty text when there is nothing meaningful to show.
   */
  transform(
    value: string | null | undefined,
    mode: DateDisplayMode = 'date',
  ): string {
    // An absent value, an unusable one and the legacy null-date sentinel all render identically, as an
    // empty cell.
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
   * Applies one of the two fixed patterns, degrading to empty text if the formatter rejects the request.
   * The guard is not defensive padding: the formatter throws for a locale whose data has not been
   * registered, the legacy formatters wrapped their bodies in a handler that returned their empty seed,
   * and letting an exception escape a template expression would take down the whole view.
   *
   * @param instant The already-validated instant to render.
   * @param pattern One of the two fixed patterns.
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
 * Parses a wire date into the instant the display layer will actually show, or `null` when there is
 * nothing meaningful to show. ⚠ THIS IS THE ONE PLACE THAT DECIDES WHETHER A WIRE DATE IS USABLE, AND
 * BOTH CALLERS MUST USE IT. {@link DateDisplayPipe} renders whatever this returns and paints an empty
 * cell for `null`; a screen that needs to reason about the instant - is this expiry in the past? - needs
 * the same verdict, because a screen that accepted a value the pipe rejects would paint a qualifier
 * beside an empty cell, and one that rejected a value the pipe accepts would paint a date with no
 * qualifier.
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
