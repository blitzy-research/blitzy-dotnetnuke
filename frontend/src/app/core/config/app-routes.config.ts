/**
 * The application addresses that more than one module has to name, declared once.
 *
 * ## Why this module exists
 *
 * `app.routes.ts` declares the route TABLE — which addresses exist, in which order, behind
 * which gate. This module declares the small set of address LITERALS that code outside the
 * route table has to spell in order to navigate to one of them. The two are different jobs:
 * the table is a value the router consumes, whereas these are strings a guard, an interceptor
 * or a component has to build a `UrlTree` from, and importing the table to recover a string
 * from it would be both indirect and fragile.
 *
 * ⚠ THIS MODULE IS DELIBERATELY INERT. It declares constants and nothing else — no
 * injectable, no function, no import of the router, no import of the route table. That is
 * what lets the guards, the interceptor and the root component all import it without any of
 * them acquiring a dependency on another's module, and it is why a route table change cannot
 * create a cycle through it.
 *
 * ## The duplication this replaces
 *
 * MIGRATION: four modules each held a PRIVATE copy of the sign-in address —
 * `core/guards/auth.guard.ts`, `core/guards/permission.guard.ts`,
 * `core/interceptors/auth.interceptor.ts` and `app.component.ts` — and two of them carried a
 * comment arguing the duplication was preferable, on the grounds that "the four agreeing by
 * construction matters less than no one of them reaching into another". The premise was
 * sound and the conclusion did not follow: reaching into a peer is indeed the thing to avoid,
 * and a neutral constants module is precisely how the four stop doing that WITHOUT holding
 * four values free to disagree.
 *
 * The four had to agree, and nothing made them. The address is only correct if it matches an
 * ungated entry in the route table, and every consequence of a mismatch is silent: a router
 * navigation to an address the table does not declare resolves to the catch-all, so a renamed
 * route would leave an expired session landing on the not-found view rather than on the
 * sign-in screen, with no compile error, no template error and no console output to say why.
 * Three copies updated out of four is the failure this module makes impossible.
 */

/**
 * Where a caller with no usable session is sent.
 *
 * ⚠ MUST MATCH AN UNGATED ENTRY IN `app.routes.ts`, and it must stay ungated. The sign-in
 * screen is the one destination an expired session is sent to, so guarding it would bounce
 * that session between the guard and whatever redirected it. The route table mounts the auth
 * feature here without `canActivate`, and `app.routes.spec.ts` asserts that absence directly.
 *
 * Consumed by both route gates, by the bearer interceptor's terminal-renewal path and by the
 * root component's session-ended path. Leading slash included, because every consumer passes
 * it to the router as an absolute address rather than composing it onto a base.
 */
export const SIGN_IN_ROUTE = '/login';

/**
 * The query parameter the sign-in address carries the refused destination under.
 *
 * ⚠ EVERY PARTY THAT WRITES THIS VALUE AND THE ONE PARTY THAT READS IT MUST AGREE, and
 * until this constant existed nothing made them. The address itself was consolidated here
 * for exactly the reason recorded at the head of this module; the KEY it is carried under
 * survived that consolidation as three private copies — one in each route gate and one in
 * the sign-in screen that consumes it — and the bearer interceptor held no copy at all,
 * which is how it came to navigate to a bare sign-in address with no destination attached.
 *
 * The failure mode is worse than the address's was, because it is asymmetric rather than
 * total. A gate-blocked navigation preserved the destination and a refused-request ejection
 * did not, so the two paths disagreed about the same operator's session and the one that
 * lost the destination was the COMMON one — a token lapsing mid-session, rather than the
 * rarer deliberate navigation to a screen the account may not use. Nothing failed loudly:
 * the operator signed in again and simply arrived somewhere else, having lost whatever
 * screen they were on.
 *
 * Consumed by both route gates, by the bearer interceptor's terminal-renewal path, and by
 * the sign-in screen that reads the destination back. The value is a bare parameter name
 * and is deliberately NOT pre-encoded: percent-encoding it for transport is the router's
 * own job when it serialises the tree, and doing it here as well would double-encode the
 * destination and hand the sign-in screen an address it could not navigate back to.
 */
export const RETURN_URL_QUERY_KEY = 'returnUrl';
