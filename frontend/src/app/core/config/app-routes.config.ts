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
