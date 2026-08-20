/**
 * THE ONE CLIENT-SIDE E-MAIL GRAMMAR, and the reason it exists is that there were two.
 *
 * Measured finding: the account form and the portal form each declared a pattern of their own, and they
 * did not agree with each other or with the server. The account form's pattern
 * (`/^[\w.-]+(\+[\w-]*)?@([\w-]+\.)+[\w-]+$/`) admitted a final domain label of one character, admitted
 * digits and hyphens in that label, and imposed no length bound on it - so `a@b.c`, `a@b.c1` and
 * `a@b.example-` all passed on the client and were then refused by the server, which is the worst place
 * for a disagreement to surface: the reader has already submitted.
 *
 * The grammar below is the CLIENT MIRROR of the server's single definition in
 * `Domain/ValueObjects/EmailAddress`, character class for character class:
 *
 * - local part - `[a-zA-Z0-9._%\-+']`, the legacy `glbEmailRegEx` class exactly;
 * - domain - `[a-zA-Z0-9.\-]`, likewise;
 * - final label - letters only, at least 2 and at most 63.
 *
 * ⚠ THE 2-TO-63 BOUND ON THE FINAL LABEL IS A PRE-EXISTING, DOCUMENTED DIVERGENCE FROM THE LEGACY RULE,
 * NOT A NEW ONE. `glbEmailRegEx` bounded it at `{2,4}`, which refuses `.info`, `.museum` and every other
 * modern top-level domain, and DotNetNuke itself ships accounts whose stored address does not satisfy its
 * own validator. The server therefore widened the bound and recorded the reasoning under MIGRATION 3 in
 * `MIGRATION_NOTES.md`. This file's job is to make the client agree with the server, and it deliberately
 * does not re-open that decision: a client stricter than the server refuses addresses the system accepts,
 * and a client looser than the server promises acceptances it cannot keep.
 *
 * The server remains the authority. This pattern exists so a reader is told about a malformed address
 * while the box is in front of them, never to decide the outcome.
 */
export const EMAIL_PATTERN = /^[a-zA-Z0-9._%+'-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,63}$/;
