/**
 * Compiles a TENANT-AUTHORED regular expression for use in the browser, refusing any expression that could
 * make the tab stop responding.
 *
 * ⚠ WHY THIS FILE EXISTS AT ALL, AND WHY IT IS ALLOWED TO SAY NO.
 *
 * A profile-property declaration may carry a `ValidationExpression`, and the tenant administrator who wrote
 * it is not the person whose browser runs it. An expression such as `^(a+)+$` is a perfectly ordinary thing
 * to type and takes exponential time on a non-matching input, so running one on every keystroke can hang the
 * tab of an operator who has no idea why. The server has a real answer to this — .NET's non-backtracking
 * engine plus a match timeout — and JavaScript has neither: `RegExp` cannot be given a time limit, and there
 * is no linear-time engine to fall back to.
 *
 * So the browser does the one thing it can do safely: it SCREENS the expression first and declines the ones
 * that could blow up, leaving those to the server, which can run them under a timeout. The result is
 * immediate feedback beside the box for the expressions administrators actually write, and a refusal that
 * arrives from the server for the pathological ones — rather than the previous arrangement, in which NOTHING
 * was checked in the browser and every rule cost a round trip to discover.
 */

/**
 * The longest expression that will be compiled. Matches the server's own ceiling, so an expression the
 * server would refuse to store is not one this can be handed either.
 */
const MAXIMUM_EXPRESSION_LENGTH = 512;

/**
 * A group whose body contains a quantifier or an alternation, and which is ITSELF quantified — the shape
 * that makes catastrophic backtracking possible.
 *
 * Read outward: an opening parenthesis with an optional group prefix, a body with no nested parentheses that
 * contains at least one `+`, `*`, `?`, `{` or `|`, a closing parenthesis, and then an outer `+`, `*` or
 * open-ended `{n,}`. `(a+)+`, `(a|aa)*` and `(\d+){2,}` all match; `(abc)+`, `\d{4}` and `(a|b)` do not.
 *
 * It is deliberately a SUFFICIENT rather than a necessary condition for danger: it accepts some expressions
 * that would be fine, and refuses none that are dangerous by this mechanism. Over-refusal costs a round
 * trip; under-refusal costs a frozen tab.
 */
const NESTED_QUANTIFIER = /\((?:\?[:=!]|\?<[=!]?[A-Za-z]*>)?[^()]*[+*?{|][^()]*\)\s*(?:[+*]|\{\d+,\}?)/;

/**
 * A backreference. Refused outright: backreferences are the other well-known route to exponential
 * backtracking, and the server's linear-time engine cannot run them either, so an expression using one is
 * already taking the server's slower path.
 */
const BACKREFERENCE = /\\[1-9]/;

/**
 * Compiles a tenant-authored expression, or reports that it must be left to the server.
 *
 * @param expression The exact expression the declaration carries.
 * @returns The compiled expression, or `null` when it is empty, unparseable, or too risky to run here.
 */
export function compileTenantPattern(expression: string): RegExp | null {
  const trimmed = expression.trim();

  if (trimmed.length === 0 || trimmed.length > MAXIMUM_EXPRESSION_LENGTH) {
    return null;
  }

  if (NESTED_QUANTIFIER.test(trimmed) || BACKREFERENCE.test(trimmed)) {
    return null;
  }

  try {
    // ⚠ NO FLAGS, AND EACH OMISSION IS A PARITY DECISION rather than an oversight. The server matches with
    // `RegexOptions.CultureInvariant` and nothing else, so:
    //   · no `i` — the server is case-sensitive, and adding it here would accept values the server rejects;
    //   · no `m` — `^` and `$` must anchor the whole value, as they do for the server;
    //   · no `g` — a global expression carries `lastIndex` between calls, so the same value would alternate
    //     between valid and invalid as the operator typed;
    //   · no `u` — Unicode mode makes some escapes a syntax error that .NET accepts, so an expression the
    //     server stores happily would fail to compile here.
    return new RegExp(trimmed);
  } catch {
    // An expression the browser cannot parse is not necessarily invalid — .NET's grammar is not JavaScript's
    // — so this is a handover to the server, not a verdict.
    return null;
  }
}

/**
 * Whether a value satisfies a compiled tenant expression.
 *
 * ⚠ UNANCHORED, BECAUSE THE SERVER IS UNANCHORED. The server tests with `Regex.IsMatch`, which succeeds on a
 * match ANYWHERE in the value. Angular's own `Validators.pattern` wraps its argument in `^(?:...)$` when the
 * argument is a string, so using it here would make the browser strictly stricter than the server — and a
 * value the server would have stored would be refused before it ever left the screen, with no way for the
 * operator to tell which side was wrong.
 *
 * @param pattern The compiled expression.
 * @param value The value being tested.
 * @returns True when the value matches, or when there is nothing to test.
 */
export function matchesTenantPattern(pattern: RegExp, value: string): boolean {
  // An empty value is the absence of a value, and whether absence is allowed is the required rule's question,
  // not the format rule's. The server draws the line in exactly the same place.
  if (value.length === 0) {
    return true;
  }

  return pattern.test(value);
}
