/**
 * Strict parsing of the numeric record identifiers this application reads out of the URL.
 *
 * This module is the SINGLE place a route parameter becomes a number. Every routed screen
 * that addresses a record by identifier — a portal, a module, a page, an account, a role, a
 * profile property — reaches that identifier through {@link parseRouteId} and through
 * nothing else. Hand-rolling the conversion at a call site is what produced the family of
 * defects this module exists to close.
 *
 * It is a set of pure functions over immutable values: no class, no decorator, no
 * dependency injection, no module-level mutable state and no reachable side effect. Nothing
 * here touches the router, the injector or the DOM, which is what makes every branch below
 * directly testable from a specification with no test bed at all.
 *
 * ---------------------------------------------------------------------------
 * WHY `Number.parseInt` IS THE WRONG TOOL, AND WHY `Number` IS ALSO WRONG
 * ---------------------------------------------------------------------------
 * Both of the obvious implementations admit values that address the wrong record, and each
 * fails differently:
 *
 *     Number.parseInt('12abc', 10)   //  12   — a PREFIX parse; the tail is discarded
 *     Number.parseInt('9007199254740993', 10)
 *                                    //  9007199254740992 — silently ROUNDED to a
 *                                    //  different integer, so it names a different row
 *     Number.parseInt('0x10', 10)    //  0    — stops at the `x`
 *     Number(' 12 ')                 //  12   — whitespace tolerated
 *     Number('1e3')                  //  1000 — exponent notation accepted
 *     Number('12.0')                 //  12   — a fractional literal accepted as an integer
 *     Number('')                     //  0    — the EMPTY STRING becomes a REAL KEY
 *     Number('Infinity')             //  Infinity
 *
 * The last two are the sharpest. In this schema `0` is a legitimate primary key, so
 * `Number('')` yielding `0` does not produce an obviously broken value that a later guard
 * would catch — it produces a plausible one that addresses an actual record. And a prefix
 * parse of `/users/12abc` silently addresses account 12, which the review recorded as a
 * live defect at five separate call sites.
 *
 * ---------------------------------------------------------------------------
 * ⚠ `-1` AND `0` ARE VALID IDENTIFIERS AND MUST SURVIVE
 * ---------------------------------------------------------------------------
 * This is the constraint that rules out every off-the-shelf "positive integer" parser, and
 * it is measured rather than assumed:
 *
 *   - `Portals.PortalID` is declared `IDENTITY(-1, 1)`, so the FIRST portal a fresh
 *     installation creates has identifier `-1` and the second has `0`. The setup log for
 *     this workspace confirms it in the live database: the seeded baseline portal is
 *     `PortalID = -1`.
 *   - `Roles.RoleID` is declared `IDENTITY(0, 1)`
 *     (`01.00.00.SqlDataProvider:L114`), so `0` is the first real role.
 *   - `Library/Components/Shared/Null.vb:L41-L45` defines the legacy integer "absent"
 *     marker as MINUS ONE — the same value that is simultaneously a real portal key. The
 *     collision is genuine and is documented in the plan's sentinel analysis.
 *
 * So a parser may not treat `-1` as absent, may not treat `0` as falsy, and may not require
 * a positive value. It rejects on GRAMMAR, MAGNITUDE and RANGE, never on sign and never on
 * a value being zero.
 *
 * ---------------------------------------------------------------------------
 * WHAT IS ACCEPTED
 * ---------------------------------------------------------------------------
 * Exactly a full-string signed decimal integer, then two numeric bounds:
 *
 *   1. GRAMMAR. The whole string must match an optional single `-`, then one or more
 *      decimal digits, and nothing else. No leading `+`, because no address in this
 *      application produces one and accepting it would let two different strings name the
 *      same record. No surrounding whitespace, because a route segment does not carry any
 *      and tolerating it would mean `%20` variants of an address resolved identically. No
 *      radix prefix, no exponent, no decimal point, no digit separator, no unicode digit
 *      outside the ASCII range.
 *   2. SAFE MAGNITUDE. `Number.isSafeInteger` must hold, so a value beyond the range in
 *      which every integer is exactly representable is refused rather than rounded to a
 *      neighbour that names a different row.
 *   3. SIGNED 32-BIT RANGE. The value must fit `int`, because every identifier column in
 *      this schema is a SQL Server `int` and the API's own route binder parses to `int`. A
 *      value outside that range cannot name a record, so refusing it here spares a
 *      guaranteed round trip and a 400.
 *
 * A leading zero is accepted deliberately — `007` parses to `7`. It is a well-formed
 * decimal integer naming exactly one record, the API's `int.TryParse` accepts it, and
 * refusing it would reject a working address for tidiness.
 *
 * ---------------------------------------------------------------------------
 * WHAT A REFUSAL LOOKS LIKE
 * ---------------------------------------------------------------------------
 * `null`, always, and never a thrown error and never a sentinel number. A caller must
 * therefore handle the refusal explicitly — the compiler makes it — and cannot accidentally
 * carry a rejected value onward. `null` rather than `undefined` because a missing route
 * parameter and an unusable one are the same fact to every caller, and returning two
 * different absent values would invite two different tests for it. `NaN` is deliberately
 * not used: it compares unequal to itself, propagates silently through arithmetic and is
 * truthy in none of the ways a guard expects, so a forgotten check would surface far from
 * its cause.
 *
 * MIGRATION: the legacy screens did none of this, and could not have. They compiled with
 * strict type checking DISABLED (`Website/release.config:L125`,
 * `<compilation debug="false" strict="false">`), under which a query-string value coerced
 * to an integer implicitly and a malformed one became `0` — which, as noted above, is a
 * real key in this schema. Identifiers also arrived as query-string state on
 * `Website/Default.aspx` rather than as path segments, so the legacy code had no notion of
 * a route parameter at all. Making every coercion explicit is exactly what the migration
 * discipline requires, and this module is where that obligation is discharged for
 * identifiers.
 */

/**
 * The grammar an identifier segment must match in full.
 *
 * Anchored at both ends, so this is a whole-string test rather than a search — the anchors
 * are what turn `12abc` into a refusal instead of a prefix parse.
 *
 * ⚠ THE ANCHORS AND THE ABSENCE OF THE `g` FLAG ARE BOTH LOAD-BEARING. A global regular
 * expression carries mutable `lastIndex` state between calls to `test`, which would make
 * this shared instance answer differently depending on what was tested before it. Without
 * the `g` flag the instance is stateless and safe to share, which is why it is declared once
 * at module scope rather than constructed per call.
 *
 * The pattern is a fixed literal authored here, NOT a value from configuration or from the
 * server. That distinction matters: a pattern with no alternation, no nesting and no
 * unbounded repetition of a group cannot backtrack catastrophically, so this is not the
 * class of regular expression that needs a time bound.
 */
const SIGNED_DECIMAL_INTEGER = /^-?\d+$/;

/**
 * The inclusive lower bound of a signed 32-bit integer.
 *
 * Every identifier column in this schema is a SQL Server `int`, and the API binds each route
 * identifier with `int.TryParse`. Written as an expression over `2 ** 31` rather than as the
 * digit string `-2147483648`, so the intent is legible and a transcription slip is
 * impossible.
 */
const MINIMUM_ROUTE_ID = -(2 ** 31);

/** The inclusive upper bound of a signed 32-bit integer. @see MINIMUM_ROUTE_ID */
const MAXIMUM_ROUTE_ID = 2 ** 31 - 1;

/**
 * Parses a route parameter into a record identifier, or refuses it.
 *
 * THE ONLY SUPPORTED WAY TO TURN A ROUTE PARAMETER INTO A NUMBER in this application. It
 * accepts the value in every shape a caller can hold one — the `string | null` that
 * `ParamMap.get` returns, the `string | undefined` a bound component input carries, or a
 * value already narrowed to `string` — so no call site needs to pre-normalise and none has
 * an excuse to parse inline.
 *
 * Accepts `-1` and `0`, which are real identifiers in this schema. Refuses anything that is
 * not a complete signed decimal integer within the exactly-representable and signed 32-bit
 * ranges. See this module's header for the measured reasons behind each rule.
 *
 * Pure and total: it returns for every input, mutates nothing, and throws for none.
 *
 * @param value The raw route parameter, or null or undefined when the route carries none.
 * @returns The identifier, or null when the value is absent or unusable.
 */
export function parseRouteId(value: unknown): number | null {
  // Absent and unusable collapse to the same answer, and the check comes first so that
  // nothing below has to consider a non-string. An empty string is caught by the grammar
  // rather than here, because `^-?\d+$` requires at least one digit - which is exactly the
  // rule that stops the empty string from becoming the real key `0`.
  if (typeof value !== 'string') {
    return null;
  }

  if (SIGNED_DECIMAL_INTEGER.test(value) === false) {
    return null;
  }

  // `Number` rather than `Number.parseInt`, and only AFTER the grammar has been proved.
  // Every value reaching this line is a bare signed decimal integer, so the two functions
  // agree on it - but `Number` cannot silently discard a tail, which keeps the parse honest
  // even if the grammar above is ever loosened.
  const parsed = Number(value);

  // Magnitude before range. A value beyond the exactly-representable integers has already
  // been rounded by the conversion above, so the number in hand is not the number that was
  // written; refusing it is the only correct response, because the rounded neighbour names a
  // different row. Note that a string of 400 digits passes the grammar and lands here, which
  // is precisely why this check is not redundant with the range check below.
  if (Number.isSafeInteger(parsed) === false) {
    return null;
  }

  if (parsed < MINIMUM_ROUTE_ID || parsed > MAXIMUM_ROUTE_ID) {
    return null;
  }

  return parsed;
}

/**
 * Whether a value is a usable record identifier.
 *
 * The predicate form of {@link parseRouteId}, for the call sites that hold a number already
 * — one bound through `withComponentInputBinding()` with a numeric transform, or one read
 * back off a loaded record — and need to know it is addressable before using it as a key.
 *
 * Applies the same two numeric rules as the parser and deliberately not the grammar rule,
 * which has no meaning for a value that was never a string. Accepts `-1` and `0` for the
 * reasons set out in this module's header.
 *
 * @param value A candidate identifier from any source.
 * @returns True when the value is a safe integer within the signed 32-bit range.
 */
export function isRouteId(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isSafeInteger(value) &&
    value >= MINIMUM_ROUTE_ID &&
    value <= MAXIMUM_ROUTE_ID
  );
}
