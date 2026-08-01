import { formatDate } from '@angular/common';
import { Inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

export type DateDisplayMode = 'date' | 'datetime';

/** What every absent, blank or unusable input renders as: nothing at all. */
const EMPTY_DISPLAY_VALUE = '';

/**
 * The UTC calendar date of the legacy null-date sentinel, `0001-01-01`. The month
 * is held zero-based because that is what `Date#getUTCMonth` returns.
 */
const SENTINEL_UTC_YEAR = 1;
const SENTINEL_UTC_MONTH = 0;
const SENTINEL_UTC_DAY_OF_MONTH = 1;

/**
 * Matches an ISO-8601 date-time that carries no zone designator, capturing the
 * date and time halves so they can be re-joined as an explicit UTC instant.
 *
 * A serialiser writing a date of unspecified kind omits the trailing `Z`, and the
 * language then parses such a string as LOCAL time. Left alone that shifts the
 * rendered day for anyone either side of Greenwich, and can push the sentinel off
 * `0001-01-01` so that it leaks onto the screen as a visible date.
 */
const ZONELESS_DATE_TIME =
  /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)$/;

/**
 * Spelled out rather than taken from the framework's own `shortDate` token, which
 * emits a two-digit year and would print a year-9999 expiry as `99`.
 */
const SHORT_DATE_PATTERN = 'M/d/yyyy';

/** `a` is this framework's spelling of the day-period token, yielding `AM`/`PM`. */
const DATE_TIME_PATTERN = 'M/d/yyyy h:mm:ss a';

/**
 * The only two format patterns this pipe will ever hand to the framework
 * formatter.
 *
 * Written as `typeof` the two constants rather than as `string`, so the union is
 * the two literal values themselves. That turns a documented invariant into a
 * COMPILE-TIME one: {@link DateDisplayPipe.render} cannot be passed a pattern
 * that is not one of these two, so no caller-influenced or dynamically assembled
 * format string can reach the formatter even by mistake. The reasoning that
 * depends on this is recorded at the call site.
 */
type DisplayDatePattern = typeof SHORT_DATE_PATTERN | typeof DATE_TIME_PATTERN;

/**
 * Values are rendered in UTC, never in the visitor's zone. See the class
 * documentation for the measurement behind this.
 */
const DISPLAY_TIME_ZONE = 'UTC';

/**
 * The greatest number of characters a candidate date string may carry and still
 * be considered.
 *
 * The wire contract is a single absolute ISO-8601 instant. The longest such form
 * that the language's own date-time string grammar accepts - an expanded year,
 * fractional seconds and a numeric offset, as in
 * `+002024-07-04T21:35:09.123456789+05:30` - runs to under 40 characters, so no
 * string longer than this bound can parse. The bound is therefore
 * OBSERVATIONALLY EQUIVALENT to the existing unparseable-input path: such a value
 * already rendered as an empty cell, by way of `new Date(...)` yielding an invalid
 * instant. What the bound adds is that the work is not done at all - no
 * normalisation pass and no parse attempt over a payload whose length is set by a
 * remote response rather than by this application.
 *
 * It is applied to the TRIMMED value, deliberately not to the raw one, so a
 * legitimate value padded with an unusual amount of surrounding whitespace still
 * renders exactly as it does today. Behavioural equivalence outranks shaving one
 * linear pass.
 */
const MAX_WIRE_DATE_LENGTH = 64;

/**
 * Fallback locale, used only when this pipe is constructed directly rather than
 * resolved through the injector. It matches both the framework's own default and
 * the culture configured at `Website/release.config` L163-L165.
 */
const FALLBACK_DISPLAY_LOCALE = 'en-US';

/**
 * The exhaustive set of wire shapes this pipe accepts.
 *
 * The platform's own date parser is far more permissive than the wire contract,
 * and its extra tolerance is not harmless — it is silently WRONG. Every value
 * below was measured against this workspace's Node 20 runtime, and each one is
 * accepted by `new Date(...)` and turned into a plausible but incorrect instant:
 *
 * | Input          | `new Date(...)` yields | Would have displayed |
 * | -------------- | ---------------------- | -------------------- |
 * | `'0'`          | 2000-01-01             | `1/1/2000`           |
 * | `'1'`          | 2001-01-01             | `1/1/2001`           |
 * | `'2024'`       | 2024-01-01             | `1/1/2024`           |
 * | `'12/31/2024'` | 2024-12-31             | `12/31/2024`         |
 * | `'Sep 10 2024'`| 2024-09-10             | `9/10/2024`          |
 * | `'2024-9-10'`  | 2024-09-10             | `9/10/2024`          |
 * | `'-2024-09-10'`| 2024-09-10             | `9/10/2024`          |
 *
 * Those are not equivalent renderings of a malformed value; they are confident
 * assertions about data the caller never sent. A numeric identifier that reached
 * a date column through a mismapped field would display as a January date rather
 * than as an empty cell, which is precisely the failure the legacy formatters'
 * seeded `Null.NullString` avoided.
 *
 * The pattern therefore admits only what the API actually emits: `System.Text.Json`
 * round-trip form (`YYYY-MM-DDTHH:mm:ss[.fffffff][Z|±HH:mm]`), the date-only form,
 * and the space-separated zone-less form the sibling normaliser rewrites. The
 * fractional part is left unbounded because .NET emits seven digits while other
 * producers emit three, and precision beyond milliseconds is unambiguous — the
 * platform simply truncates it.
 *
 * Time-of-day RANGES are deliberately not encoded here. The specification's own
 * parser already rejects an out-of-range component outright — `'…T25:00:00Z'`,
 * `'…T14:60:00Z'` and `'…T14:30:61Z'` were each measured as `NaN` — so the
 * existing unparseable guard covers them without duplicating the rule.
 */
const ISO_WIRE_FORMAT =
  /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$/;

/**
 * Reports whether a year, month and day denote a date that genuinely exists.
 *
 * This closes the second half of the permissiveness gap, and it is the half a
 * shape check alone cannot reach. The platform does not reject an over-long day
 * for its month; it silently ROLLS FORWARD into the next one. Measured:
 * `'2024-02-30T00:00:00Z'` becomes 2024-03-01, `'2023-02-29'` becomes 2023-03-01
 * (2023 is not a leap year) and `'2024-09-31'` becomes 2024-10-01. Each would
 * have displayed a real-looking date one day past the end of the intended month.
 *
 * The check is deliberately performed on the DATE FIELDS ALONE rather than by
 * comparing the parsed instant's UTC date back against the string, because a
 * legitimate value carrying a numeric offset may legitimately land on a different
 * UTC day: `'2024-09-10T01:00:00+02:00'` is 2024-09-09T23:00Z. A round-trip
 * comparison would reject that valid value, so only the calendar triple is
 * validated and the offset is left to the parser.
 *
 * The probe is built with `setUTCFullYear` rather than `Date.UTC`, because
 * `Date.UTC` maps years 0-99 onto 1900-1999: a probe for `0050-06-15` would land
 * on 1950 and the date would be judged non-existent and blanked. `setUTCFullYear`
 * applies the year literally, so every year from 1 to 9999 is validated as
 * written. The framework's own date helper documents having hit exactly this trap
 * — see the note above `createDate` in `@angular/common`, "`setFullYear()` allows
 * years like 0001 to be set correctly".
 *
 * The precise scope of that choice is worth stating, because it is narrower than
 * it first appears and was verified by mutation rather than assumed. For the
 * sentinel year 1 the two constructions are OBSERVATIONALLY EQUIVALENT: a
 * `Date.UTC` probe rejects `0001-01-01` at this check, the sentinel guard would
 * have blanked it one step later, and the rendered output is an empty cell either
 * way. The difference is only observable for years 2-99, which is why the suite
 * pins a non-sentinel low year explicitly — without that expectation this choice
 * would be untested, and the guard could silently degenerate into a filter on
 * small years in general.
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
 * Normalises a trimmed input into a candidate the platform parses as an absolute
 * UTC instant.
 *
 * Expressing the rewrite as a replacement rather than as capture-group indexing
 * means the non-matching cases need no branch and no absent group can be
 * interpolated into the result.
 */
function toAbsoluteUtcCandidate(trimmed: string): string {
  return trimmed.replace(ZONELESS_DATE_TIME, '$1T$2Z');
}

/**
 * Reports whether an instant falls on the legacy null-date sentinel.
 *
 * The comparison is date-part-only, as the legacy test was, so any instant on
 * `0001-01-01` UTC counts and not only exact midnight. The UTC accessors keep the
 * answer independent of the host zone.
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
 * Each legacy administration screen carried its own private formatter and they
 * disagreed with one another: most emitted a calendar date alone and one also
 * printed the time of day. Both shapes are offered here, with the date-only shape
 * as the default.
 *
 * Values render in UTC rather than in the visitor's zone. The wire contract is an
 * absolute instant and the legacy pages printed the stored calendar date verbatim,
 * so localising it would shift dates across midnight and would also split the
 * sentinel test from the rendered output.
 *
 * | Legacy formatter   | Location                                                | Emits                          |
 * | ------------------ | ------------------------------------------------------- | ------------------------------ |
 * | `DisplayDate`      | `Website/admin/Users/Users.ascx.vb` L396-L408            | `.ToString` — date AND time    |
 * | `FormatExpiryDate` | `Website/admin/Portal/Portals.ascx.vb` L250-L260         | `.ToShortDateString` — date    |
 * | `FormatDate`       | `Website/admin/Security/SecurityRoles.ascx.vb` L377-L383 | `.ToShortDateString` — date    |
 * | `FormatExpiryDate` | `Website/admin/Users/MemberServices.ascx.vb` L172-L186   | `.ToShortDateString` — date    |
 *
 * Three of the four emit a date alone, so `'date'` is the default. The single
 * outlier is reproduced by asking for `'datetime'`, which keeps each screen
 * comparable with the page it replaces.
 *
 * ## Per-consumer mode mapping
 *
 * | Consumer        | Field(s)                      | Mode                |
 * | --------------- | ----------------------------- | ------------------- |
 * | user list       | `createdDate`, `lastLoginDate` | `'datetime'`       |
 * | portal list     | `expiryDate`                  | `'date'` (default)  |
 * | role assignment | `effectiveDate`, `expiryDate` | `'date'` (default)  |
 * | every other date| any absolute date value       | `'date'` (default)  |
 *
 * ## Behaviour
 *
 * - The legacy null-date sentinel renders as an empty cell in both modes, and the
 *   test for it is date-part-only (`Null.vb` L222-L224), so any instant on
 *   `0001-01-01` UTC counts.
 * - `9999-12-31` is a genuine value meaning "perpetual"
 *   (`Library/Components/Security/Roles/RoleController.vb` L542) and renders
 *   normally as `12/31/9999`.
 * - `null`, `undefined`, a blank string and an unparseable string all render as
 *   the same empty cell — never a placeholder, never an error message.
 * - Only the wire shapes the API actually emits are accepted, and the accepted
 *   shape must also denote a date that exists on the calendar. Anything else —
 *   a bare number such as `'0'`, a locale-formatted string such as `'12/31/2024'`,
 *   an unpadded month such as `'2024-9-10'`, or a non-existent day such as
 *   `'2024-02-30'` — renders as the same empty cell. This deliberately NARROWS
 *   the platform's parser, which would otherwise turn each of those into a
 *   plausible but incorrect date; see `ISO_WIRE_FORMAT` and
 *   `isRealCalendarDate` for the measurements.
 * - An offset-bearing value is NOT rejected merely because its UTC calendar date
 *   differs from the date written in its own text. `2024-07-04T02:00:00+05:00` is
 *   genuinely July 3 in UTC and renders as such, which is why the calendar check
 *   validates the date triple alone and leaves the offset to the parser.
 * - So does any input that is not a string at all. The declared parameter type
 *   says a template cannot pass one, and strict template checking enforces that;
 *   but the value normally arrives off the wire, typed only by a DTO interface
 *   that is erased at runtime, so a malformed response can still deliver a number
 *   or an object. That is treated as unusable input rather than allowed to throw
 *   inside a template expression, which would abort the whole view rather than one
 *   cell.
 * - So does a candidate longer than any parseable ISO-8601 instant, which is
 *   rejected before it is normalised or parsed.
 * - The result is plain text. It is never markup and must never be bound through
 *   a raw-markup sink, because legacy resource wording is untrusted content.
 * - The pipe is pure — it is never marked otherwise — so it re-runs only when its
 *   arguments change, preserving the change-detection guarantees its consumers
 *   depend on. It reads no clock and holds no state, so identical inputs always
 *   produce identical output.
 *
 * ## Time zone
 *
 * Values are rendered in UTC. The wire contract is an absolute UTC instant, and
 * the legacy pages printed the stored calendar date verbatim, so rendering in the
 * visitor's zone would shift dates across midnight — measured at a full day for
 * `2024-07-04T02:00:00Z` viewed from `America/Los_Angeles`. Pinning the zone also
 * keeps the sentinel test and the rendered output in one frame.
 *
 * @example
 * ```html
 * <!-- 7/4/2024 -->
 * {{ portal.expiryDate | dateDisplay }}
 *
 * <!-- 7/4/2024 9:35:09 PM -->
 * {{ user.lastLoginDate | dateDisplay: 'datetime' }}
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
   * MIGRATION: the legacy membership screen replaced an expiry date already in the
   * past with a word instead of showing the date. That reads the clock, so it
   * belongs to the screen that owns a clock and is deliberately not done here.
   *
   * @param value An absolute UTC ISO-8601 date string, or nothing. A `Date` is not
   * accepted: dates cross the wire as strings and no consumer holds a parsed one.
   * @param mode Which of the two shapes to render. Defaults to `'date'`, the shape
   * most of the legacy formatters emitted.
   * @returns The formatted date, or empty text when there is nothing meaningful to
   * show.
   */
  transform(
    value: string | null | undefined,
    mode: DateDisplayMode = 'date',
  ): string {
    // MIGRATION: (2) an absent value and the legacy null-date sentinel render
    // identically. The erasure is confined to the display layer; the wire still
    // distinguishes them, because the API serialises a minimum-value date as a
    // real ISO-8601 string rather than omitting the property or writing null.
    //
    // The test is `typeof`, not a pair of equality checks against `null` and
    // `undefined`, and that is the load-bearing difference. The declared parameter
    // type is honest about what a template may pass, and strict template checking
    // enforces it at compile time - but a value reaching this pipe has usually come
    // off the wire, typed only by a DTO interface that is erased at runtime. A
    // malformed or evolved response can therefore deliver a number, an object or an
    // array where the contract promised a string. An equality check lets such a
    // value straight through to `.trim()`, which throws; and because a pipe runs
    // inside a template expression, that exception is not contained to one cell -
    // it aborts the whole view. `typeof` subsumes both nullish cases and every
    // other non-string payload, so the unusable input renders as the same empty
    // cell as any other unusable input.
    //
    // The parameter type is deliberately NOT widened to `unknown` to match. Doing
    // so would silently withdraw the compile-time protection template authors have
    // today, trading a real guarantee for an appearance of thoroughness.
    if (typeof value !== 'string') {
      return EMPTY_DISPLAY_VALUE;
    }

    const trimmed = value.trim();
    if (trimmed === EMPTY_DISPLAY_VALUE) {
      return EMPTY_DISPLAY_VALUE;
    }

    // A candidate longer than any parseable ISO-8601 instant is rejected without
    // being normalised or parsed. See {@link MAX_WIRE_DATE_LENGTH}: this cannot
    // change what renders, because such a value already produced an empty cell by
    // failing to parse. The bound is applied to the TRIMMED value, so a legitimate
    // date padded with unusual whitespace is not blanked.
    if (trimmed.length > MAX_WIRE_DATE_LENGTH) {
      return EMPTY_DISPLAY_VALUE;
    }

    // MIGRATION: (9) the wire shape is validated BEFORE the value is parsed, and
    // a value outside that shape renders as an empty cell rather than as whatever
    // the platform's permissive parser makes of it. This is a deliberate
    // NARROWING of platform behaviour, not a reproduction of it: `new Date('0')`
    // yields 2000-01-01 and `new Date('Sep 10 2024')` succeeds, so without this
    // guard a mismapped identifier or a locale-formatted string would render as a
    // confident, wrong date. An empty cell is the legacy outcome — every one of
    // the four legacy formatters seeded its result with `Null.NullString` and
    // returned that seed when the value could not be used
    // (`Website/admin/Users/Users.ascx.vb` L397 and L404-L406) — so degrading is
    // faithful and displaying a fabricated date is not. See `ISO_WIRE_FORMAT` for
    // the measured table of values this rejects.
    const wireFields = ISO_WIRE_FORMAT.exec(trimmed);
    if (wireFields === null) {
      return EMPTY_DISPLAY_VALUE;
    }

    // MIGRATION: (10) a syntactically well-formed date that does not exist on the
    // calendar also renders as an empty cell. The platform rolls such a value
    // forward instead of rejecting it — `2024-02-30` becomes 2024-03-01 and
    // `2023-02-29` becomes 2023-03-01 — which is indistinguishable on screen from
    // a real date and therefore worse than showing nothing. The month and day
    // groups are guaranteed present by the pattern, because the date portion is
    // not optional within it. This single check is the whole of the calendar
    // validation: it is performed on the date triple alone, so an offset-bearing
    // value that legitimately lands on a different UTC day is preserved, and it
    // probes with `setUTCFullYear` so low years are not remapped.
    if (
      !isRealCalendarDate(
        Number(wireFields[1]),
        Number(wireFields[2]),
        Number(wireFields[3]),
      )
    ) {
      return EMPTY_DISPLAY_VALUE;
    }

    const instant = new Date(toAbsoluteUtcCandidate(trimmed));

    // An explicit numeric test, because a date instance is truthy even when it
    // holds no usable value - and the platform's own invalid-date wording must
    // never reach a cell.
    if (Number.isNaN(instant.getTime())) {
      return EMPTY_DISPLAY_VALUE;
    }

    // Tested before formatting: year one predates modern zone rules, so formatting
    // it would emit a wrong and alarming date. A year-9999 date is deliberately
    // not treated this way - it is a real "perpetual" expiry and renders like any
    // other value, since presenting it as "never expires" is the screen's call.
    if (isSentinelDate(instant)) {
      return EMPTY_DISPLAY_VALUE;
    }

    if (mode === 'datetime') {
      return this.render(instant, DATE_TIME_PATTERN);
    }
    return this.render(instant, SHORT_DATE_PATTERN);
  }

  /**
   * Applies one of the two fixed patterns, degrading to empty text if the formatter
   * rejects the request.
   *
   * The guard is not defensive padding: the formatter throws for a locale whose
   * data has not been registered, and three of the four legacy formatters wrapped
   * their body in a handler that returned the seeded empty string
   * (`Website/admin/Users/Users.ascx.vb` L404-L406,
   * `Website/admin/Portal/Portals.ascx.vb` L256-L258,
   * `Website/admin/Users/MemberServices.ascx.vb` L182-L184). Letting an
   * exception escape a template expression would take down the whole view.
   *
   * @param instant The already-validated instant to render.
   * @param pattern One of the two fixed patterns. Typed as the closed union
   * {@link DisplayDatePattern} rather than as `string`, for the security reason
   * recorded in the body.
   * @returns The formatted text, or an empty cell if the formatter rejects the
   * request.
   */
  private render(instant: Date, pattern: DisplayDatePattern): string {
    // SECURITY: GHSA-48r7-hpm6-gfxm - `@angular/common` denial of service through
    // an out-of-memory condition in `formatDate` (CWE-400, CWE-1333) - has no
    // reachable precondition here, and this signature is what keeps that true.
    //
    // The advisory affects every `@angular/common` release up to and including
    // 19.2.25, and no patched release exists inside the mandated major version:
    // the only published remedy is a semver-major move to Angular 21, which the
    // agreed dependency baseline for this migration rules out. So the defence has
    // to be structural rather than a version bump.
    //
    // The vulnerable input is the FORMAT argument, not the value and not the zone.
    // The advisory's own text states the exemption explicitly: an application is
    // not vulnerable when the format is hardcoded or is validated to a reasonable
    // length. Both hold here, by construction rather than by convention:
    //
    //   * `pattern` is typed as a two-member union of literal types, so the
    //     compiler rejects any other value - including a dynamically assembled or
    //     caller-supplied one;
    //   * the only two call sites pass the module-level constants
    //     `SHORT_DATE_PATTERN` and `DATE_TIME_PATTERN`, selected by the closed
    //     `DateDisplayMode` union rather than by any inbound data;
    //   * `render` is private, so no consumer can reach the formatter at all; and
    //   * this is the only `formatDate` call in the workspace, and nothing uses
    //     `DatePipe` or the `date` pipe, so there is no second, unguarded path.
    //
    // Any future change that widens `pattern` back to `string`, or derives a
    // pattern from a value that crosses the wire, reintroduces the precondition
    // this note rules out. Do neither.
    // MIGRATION: (4) no third-party date library is introduced — the workspace
    // pins a deliberately small dependency set, and none is available. All
    // formatting goes through the framework's own date formatter.
    //
    // MIGRATION: (7) the locale is injected rather than hard-coded, but the two
    // patterns are fixed to the shapes the legacy screens produced under the
    // configured `culture="en-US"` (`Website/release.config` L163-L165). The
    // legacy runtime also switched culture per portal and per user; that
    // behaviour is not reproduced, because no translation runtime is installed in
    // this workspace. Locale-sensitive tokens still follow whatever locale the
    // application provides.
    try {
      return formatDate(instant, pattern, this.locale, DISPLAY_TIME_ZONE);
    } catch {
      return EMPTY_DISPLAY_VALUE;
    }
  }
}
