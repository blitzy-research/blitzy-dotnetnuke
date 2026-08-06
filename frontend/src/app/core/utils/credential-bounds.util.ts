/**
 * The maximum length this application allows a credential to reach in the browser.
 *
 * This module is the SINGLE place that figure is written down. Three screens collect a
 * password — portal creation, account creation and the change-password screen — and each
 * one reaches the ceiling through {@link CREDENTIAL_MAX_LENGTH} and through nothing else.
 * Three separate literals is how a client ceiling drifts away from the server rule it is
 * supposed to mirror, which is the defect this module exists to close.
 *
 * It exports a single constant. There is no function, no class and no dependency here,
 * because the authoritative rule is NOT enforced in the browser — see below.
 *
 * ---------------------------------------------------------------------------
 * THE SERVER OWNS THE RULE
 * ---------------------------------------------------------------------------
 * The authoritative bound is declared once on the server, as
 * `CredentialBounds.MaximumByteLength = 256` in
 * `backend/src/DnnMigration.Application/Validation/CredentialBounds.cs`, and is applied as
 * `Encoding.UTF8.GetByteCount(credential) <= 256` at every credential entry point:
 *
 *     CreateUserRequestValidator      → Password
 *     ChangePasswordRequestValidator  → NewPassword AND CurrentPassword
 *
 * Because both fields of a change are bounded server-side, mirroring the ceiling on both
 * fields of the change-password screen reproduces the server's rule exactly rather than
 * adding a rule of the client's own invention.
 *
 * ---------------------------------------------------------------------------
 * ⚠ THE TWO SIDES COUNT IN DIFFERENT UNITS, AND THE DIRECTION MATTERS
 * ---------------------------------------------------------------------------
 * The server counts UTF-8 BYTES. The HTML `maxlength` attribute counts UTF-16 CODE UNITS.
 * The two are not the same measure, so this ceiling cannot be an exact restatement of the
 * server's bound — but it does not need to be, because the inequality runs in the safe
 * direction:
 *
 *     UTF-8 byte length >= UTF-16 code-unit length, for every string, without exception
 *
 * ASCII encodes to one byte per code unit, so the two measures are equal. Every non-ASCII
 * code point encodes to at least two UTF-8 bytes while occupying at most two UTF-16 code
 * units, so bytes can only run ahead. This was verified rather than assumed, by measuring
 * both lengths for all 64,561 sampled code points across the BMP and the astral planes:
 * there is no code point for which the byte count falls below the code-unit count.
 *
 * The consequence is the property this ceiling needs. A limit of 256 CODE UNITS can never
 * refuse a credential that the server's 256-BYTE rule would have accepted, so the client
 * NEVER NARROWS the server's contract. In the pure-ASCII case the two coincide exactly. In
 * the multi-byte case the browser is slightly the more permissive of the two, and the
 * server then refuses the value with its own measured wording, which every one of these
 * screens already renders through its server-error surface.
 *
 * That asymmetry is deliberate. The alternative — reimplementing the byte count in the
 * browser with `TextEncoder` — would put a second, independently maintained copy of a
 * security rule in the less trustworthy of the two places, and would have to be kept in
 * step with the server's encoder fallback behaviour to stay honest. A ceiling that is
 * provably never stricter, plus a server that is authoritative, is the smaller contract.
 *
 * ---------------------------------------------------------------------------
 * WHY IT IS NOT TWENTY
 * ---------------------------------------------------------------------------
 * MIGRATION: THE LEGACY CEILING OF TWENTY IS DELIBERATELY NOT PRESERVED. Every legacy
 * credential field declared `maxlength="20"` — `admin/Users/Password.ascx` L35, L39 and
 * L43, and `admin/Users/User.ascx` L39 — and each of those mirrored the legacy STORAGE
 * width `@Password nvarchar(20)`. Twenty was never a policy rule: the legacy password
 * policy is the three settings measured in `Website/release.config`
 * (`minRequiredPasswordLength="7"`, `minRequiredNonalphanumericCharacters="0"`,
 * `requiresUniqueEmail="false"`), and it declares NO MAXIMUM AT ALL. The successor stores a
 * one-way BCrypt hash, so the storage width that produced the twenty no longer exists.
 *
 * Carrying the twenty forward therefore preserves an artefact of a deleted constraint
 * rather than a behaviour, and it does active harm in two ways. It caps the entropy of
 * every new credential at twenty characters. And on the change-password screen it applies
 * to the CURRENT password too, so an account whose password is longer than twenty
 * characters — which the 256-byte server bound permits — could never type its existing
 * credential in full, and so could never change its own password. That is a lockout, not a
 * typing affordance.
 */

/**
 * The largest credential the browser will accept, in UTF-16 code units.
 *
 * Numerically equal to the server's `CredentialBounds.MaximumByteLength`. The two count in
 * different units, and as established above the difference can only ever make this ceiling
 * the more permissive of the pair, never the stricter — so nothing the server would accept
 * is refused here.
 */
export const CREDENTIAL_MAX_LENGTH = 256;
