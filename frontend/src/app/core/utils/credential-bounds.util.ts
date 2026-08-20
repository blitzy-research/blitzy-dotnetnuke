/**
 * The maximum length this application allows a credential to reach in the browser. This module is the
 * SINGLE place that figure is written down.
 */

/**
 * The largest credential the browser will accept, in UTF-16 code units. Numerically equal to the server's
 * `CredentialBounds.MaximumByteLength`.
 */
export const CREDENTIAL_MAX_LENGTH = 256;
