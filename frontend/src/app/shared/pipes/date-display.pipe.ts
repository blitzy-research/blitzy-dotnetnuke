// Centralised display formatting for absolute date values.
//
// This file is the single home for date rendering across the workspace: no
// third-party date library is installed, and none may be added, so every screen
// formats dates through this pipe.

import { formatDate } from '@angular/common';
import { Inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

/**
 * The two rendering modes this pipe supports.
 *
 * A closed string-literal union rather than an enum: `isolatedModules` rules out
 * a `const` enum, and a union gives the same exhaustive type checking with no
 * emitted runtime object.
 *
 * - `'date'` renders the calendar date alone. This is the default because three
 *   of the four legacy formatters emitted a date alone.
 * - `'datetime'` renders the calendar date followed by the time of day,
 *   reproducing the one legacy formatter that printed both.
 */
export type DateDisplayMode = 'date' | 'datetime';

/**
 * What every "absent", blank or unusable input renders as.
 *
 * The legacy code encoded an absent string as the empty string rather than as a
 * database null — `Null.NullString` returns `""`
 * (`Library/Components/Shared/Null.vb` L71-L75) and `Null.IsNull("")` is
 * therefore `True` (`Null.vb` L226) — so an empty cell is the faithful rendering
 * of "nothing to show".
 */
const EMPTY_DISPLAY_VALUE = '';

/**
 * The UTC calendar date of the legacy null-date sentinel, `0001-01-01`.
 *
 * `Null.NullDate` is `Date.MinValue` (`Library/Components/Shared/Null.vb`
 * L66-L70). The month is held as a zero-based value because that is what
 * `Date#getUTCMonth()` returns: `0` is January.
 */
const SENTINEL_UTC_YEAR = 1;
const SENTINEL_UTC_MONTH = 0;
const SENTINEL_UTC_DAY_OF_MONTH = 1;

/**
 * Matches an ISO-8601 date-time that carries no zone designator, capturing the
 * date and time halves so they can be re-joined as an explicit UTC instant.
 *
 * A serialiser that writes a date whose kind is unspecified omits the trailing
 * `Z`, and the language specification then parses such a string as LOCAL time.
 * Left alone that would shift the rendered day for anyone east or west of
 * Greenwich, and could push the sentinel off `0001-01-01` so that it leaked onto
 * the screen as a visible date.
 */
const ZONELESS_DATE_TIME =
  /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)$/;

/**
 * `.ToShortDateString()` under the culture the legacy site is configured with —
 * `<globalization culture="en-US" uiCulture="en" ... />` at
 * `Website/release.config` L163-L165 — is `M/d/yyyy`.
 *
 * The framework's own `'shortDate'` token was measured and rejected: it emits a
 * two-digit year (`7/4/24`), which both diverges from the legacy output and
 * would print the perpetual expiry value as `12/31/99`.
 */
const SHORT_DATE_PATTERN = 'M/d/yyyy';

/**
 * `.ToString()` on a date under the same culture is the general pattern
 * `M/d/yyyy h:mm:ss tt`; `a` is this framework's spelling of the `tt` day-period
 * token and yields `AM`/`PM`.
 */
const DATE_TIME_PATTERN = 'M/d/yyyy h:mm:ss a';

/**
 * Values are rendered in UTC, never in the visitor's zone. See the class
 * documentation for the measurement behind this.
 */
const DISPLAY_TIME_ZONE = 'UTC';

/**
 * Fallback locale, used only when this pipe is constructed directly rather than
 * resolved through the injector. It matches both the framework's own default and
 * the culture configured at `Website/release.config` L163-L165.
 */
const FALLBACK_DISPLAY_LOCALE = 'en-US';

/**
 * Normalises a trimmed input into a candidate the platform parses as an absolute
 * UTC instant.
 *
 * A string that already carries `Z` or a numeric offset is passed through
 * untouched; a date-only string is already parsed as UTC by specification; only
 * a zone-less date-time is rewritten, and it gains both the `T` separator and the
 * `Z` designator.
 *
 * `String#replace` returns its subject unchanged when the pattern does not match,
 * so the non-matching cases need no branch — and expressing the rewrite as a
 * replacement rather than as capture-group indexing removes any chance of an
 * absent group being interpolated into the result.
 */
function toAbsoluteUtcCandidate(trimmed: string): string {
  return trimmed.replace(ZONELESS_DATE_TIME, '$1T$2Z');
}

/**
 * Reports whether an instant is the legacy null-date sentinel.
 *
 * The comparison is deliberately date-part-only, matching `Null.IsNull`
 * (`Library/Components/Shared/Null.vb` L222-L224), which reads
 * `objDate.Date.Equals(NullDate.Date)` — its sibling `Null.GetNull` carries the
 * explanatory note at `Null.vb` L184, "compare the Date part of the DateTime
 * with the DatePart of the NullDate ( this avoids subtle time differences )".
 * Any instant on `0001-01-01` UTC therefore counts, not only exact midnight.
 *
 * The UTC accessors are used because they are independent of the host zone,
 * which keeps this test deterministic on every machine.
 */
function isSentinelDate(instant: Date): boolean {
  return (
    instant.getUTCFullYear() === SENTINEL_UTC_YEAR &&
    instant.getUTCMonth() === SENTINEL_UTC_MONTH &&
    instant.getUTCDate() === SENTINEL_UTC_DAY_OF_MONTH
  );
}

/**
 * `dateDisplay` — renders an absolute UTC ISO-8601 date string for display.
 *
 * ## Why this pipe exists
 *
 * Each legacy administration screen carried its own private date formatter. A
 * census of `Website/admin/{Portal,Users,Security,Modules,Tabs}` found exactly
 * four, and they do not agree with one another:
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
  /**
   * @param locale Resolved from the injector. The parameter carries a default so
   * that a test can also construct the pipe directly, without an injection
   * context.
   */
  constructor(
    @Inject(LOCALE_ID) private readonly locale: string = FALLBACK_DISPLAY_LOCALE,
  ) {}

  /**
   * @param value An absolute UTC ISO-8601 date string, or nothing. `Date` is not
   * accepted: every date crosses the wire as a string, and no consumer holds a
   * parsed instance.
   * @param mode Which of the two legacy shapes to reproduce. Defaults to
   * `'date'`, the shape three of the four legacy formatters emitted.
   * @returns The formatted date, or an empty string when there is nothing
   * meaningful to show.
   */
  transform(
    value: string | null | undefined,
    mode: DateDisplayMode = 'date',
  ): string {
    // MIGRATION: (2) an absent value and the legacy null-date sentinel render
    // identically. The erasure is confined to the display layer; the wire still
    // distinguishes them, because the API serialises a minimum-value date as a
    // real ISO-8601 string rather than omitting the property or writing null.
    if (value === undefined || value === null) {
      return EMPTY_DISPLAY_VALUE;
    }

    const trimmed = value.trim();
    if (trimmed === EMPTY_DISPLAY_VALUE) {
      return EMPTY_DISPLAY_VALUE;
    }

    const instant = new Date(toAbsoluteUtcCandidate(trimmed));

    // MIGRATION: (8) an unparseable input renders as an empty cell, never as the
    // platform's own invalid-date wording. This mirrors the legacy exception
    // path: `DisplayDate` seeded its result with `Null.NullString`
    // (`Website/admin/Users/Users.ascx.vb` L397) and its handler at L404-L406
    // swallowed the failure, so the seeded empty string was returned. The check
    // is an explicit numeric test, because a date instance is truthy even when
    // it holds no usable value.
    if (Number.isNaN(instant.getTime())) {
      return EMPTY_DISPLAY_VALUE;
    }

    // MIGRATION: (1) the legacy null-date sentinel renders as an empty cell
    // rather than as `1/1/0001`. `Null.NullDate` is `Date.MinValue`
    // (`Library/Components/Shared/Null.vb` L66-L70) and the sentinel survives
    // serialisation intact, so the client has to recognise it. Detection is
    // date-part-only, exactly as `Null.IsNull` does at `Null.vb` L222-L224, and
    // it happens BEFORE formatting: year one predates modern zone rules, and
    // formatting it would emit a wrong and alarming date.
    //
    // MIGRATION: (3) `9999-12-31` is deliberately NOT treated the same way. It
    // is the perpetual, one-off billing expiry assigned at
    // `Library/Components/Security/Roles/RoleController.vb` L542
    // (`Case "O" : ExpiryDate = New System.DateTime(9999, 12, 31)`), so it is a
    // real value and is rendered like any other. Presenting it as "never
    // expires" is a decision for the screen, not for this pipe.
    if (isSentinelDate(instant)) {
      return EMPTY_DISPLAY_VALUE;
    }

    // MIGRATION: (5) the default mode emits a date alone, which differs from the
    // pipe's closest legacy ancestor. `DisplayDate`
    // (`Website/admin/Users/Users.ascx.vb` L396-L408, L400) called `.ToString`
    // and so printed a time as well, but the other three formatters —
    // `Website/admin/Portal/Portals.ascx.vb` L250-L260 L254,
    // `Website/admin/Security/SecurityRoles.ascx.vb` L377-L383 L379 and
    // `Website/admin/Users/MemberServices.ascx.vb` L172-L186 L177 — all called
    // `.ToShortDateString`. The three-to-one majority becomes the default, and
    // the user-list columns (`users.ascx` L64 and L70) pass `'datetime'` to
    // reproduce their legacy output exactly.
    //
    // MIGRATION: (6) the "Expired" substitution is deliberately absent from this
    // pipe. The fourth formatter compares the value with `Date.Today`
    // (`Website/admin/Users/MemberServices.ascx.vb` L176) and substitutes a
    // localised word at L179. That reads the clock, so it belongs to the
    // membership-settings screen — computed against an injected clock — and not
    // to a pure pipe.
    if (mode === 'datetime') {
      return this.render(instant, DATE_TIME_PATTERN);
    }
    return this.render(instant, SHORT_DATE_PATTERN);
  }

  /**
   * Applies one of the two fixed patterns, degrading to an empty cell if the
   * formatter rejects the request.
   *
   * The guard is not defensive padding: the formatter throws for a locale whose
   * data has not been registered, and three of the four legacy formatters wrapped
   * their body in a handler that returned the seeded empty string
   * (`Website/admin/Users/Users.ascx.vb` L404-L406,
   * `Website/admin/Portal/Portals.ascx.vb` L256-L258,
   * `Website/admin/Users/MemberServices.ascx.vb` L182-L184). Letting an
   * exception escape a template expression would take down the whole view.
   */
  private render(instant: Date, pattern: string): string {
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
