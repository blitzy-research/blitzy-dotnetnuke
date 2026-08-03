/**
 * Wire contracts for the authentication endpoints, mirroring the API's
 * `Dtos/Auth` shapes member for member.
 *
 * The API serialises with camel-cased member names, writes enumerations as
 * strings and omits null members, so these interfaces name their members exactly
 * as they appear on the wire. Instants arrive as ISO 8601 strings and are typed
 * `string` rather than `Date`, because `JSON.parse` produces a string and nothing
 * in the transport converts it — typing them `Date` would be a claim the runtime
 * does not honour.
 *
 * MIGRATION: the legacy sign-in call was
 * `UserController.ValidateUser(PortalId, username, password, "DNN", verification,
 * portalName, ipAddress, ByRef loginStatus)`. Three of those arguments have no
 * counterpart here and their absence is deliberate: the authentication-type
 * literal `"DNN"` disappeared with the single bearer-token path, the CAPTCHA
 * verification argument disappeared with the excluded CAPTCHA control, and the
 * `ByRef loginStatus` out-parameter became the failure code carried in the
 * problem document. The caller's address is now observed by the server from the
 * connection rather than supplied by the client, which is the only place it can
 * be trusted.
 */

/**
 * Credentials posted to `POST /auth/login`.
 */
export interface LoginRequest {
  /** The account name. Compared case-insensitively by the server. */
  readonly username: string;

  /** The plaintext password, sent once over the transport and never stored. */
  readonly password: string;

  /**
   * The tenant being signed in to.
   *
   * Optional, and the server resolves it from the request host when omitted. It
   * must be supplied when the host matches no configured portal alias — a
   * deployment that has not registered an alias for the host it is served from
   * will otherwise be told, in a validation problem document, that the portal
   * must be stated explicitly.
   */
  readonly portalId?: number;
}

/**
 * The token pair and identity returned by `POST /auth/login` and
 * `POST /auth/refresh`.
 */
export interface LoginResponse {
  /** The bearer token presented on subsequent requests. */
  readonly accessToken: string;

  /**
   * Absolute expiry of the access token, as an ISO 8601 instant in UTC.
   *
   * The only expiry the server publishes. It deliberately sends neither a
   * remaining-seconds duration — one expiry representation cannot disagree with
   * itself — nor an expiry for the refresh token, which is rotation state the
   * server owns. There is likewise no `tokenType` member: the scheme is fixed by
   * the API contract as `Bearer` rather than restated in every response.
   */
  readonly expiresAtUtc: string;

  /**
   * The token used to obtain a replacement pair.
   *
   * Rotated on every use: a successful refresh invalidates the token that was
   * presented and returns a new one, so a client that keeps the old value cannot
   * reuse it. Presenting a token that has already been used is treated as a
   * replay and revokes the account's entire refresh-token set.
   */
  readonly refreshToken: string;

  /**
   * Whether the caller must change their credential before continuing.
   *
   * MIGRATION: the legacy post-credential check reported one of five values with
   * a fixed precedence, so only ever one condition surfaced. Three independent
   * booleans replace it, which can express combinations the legacy could not.
   * This one covers both blocking credential cases — an administrator-forced
   * update and an already-expired credential — and also the two success-with-
   * caveat sign-in statuses raised when a shipped default credential is still in
   * use, because forcing a change was the legacy remediation for those too.
   * BLOCKING: the legacy interstitial offered no way past it.
   */
  readonly mustChangePassword: boolean;

  /**
   * Whether the caller's credential is approaching expiry.
   *
   * NON-BLOCKING, and the only one of the three that was: the legacy screen
   * showed the same interstitial but enabled its "proceed anyway" panel for this
   * case alone. Treat it as a prompt the caller may decline, never as a gate.
   */
  readonly passwordExpiring: boolean;

  /**
   * Whether the caller must complete or correct their profile before continuing.
   *
   * BLOCKING. The legacy flow sent this case to a different step rather than to
   * the credential interstitial.
   */
  readonly mustUpdateProfile: boolean;

  /** The signed-in identity, including the roles and permissions it holds. */
  readonly user: CurrentUser;
}

/**
 * The token presented to `POST /auth/refresh` and `POST /auth/logout`.
 *
 * Both endpoints are anonymous — they authenticate the refresh token itself
 * rather than a bearer token, which is what allows a refresh to succeed after the
 * access token has already expired.
 */
export interface RefreshTokenRequest {
  readonly refreshToken: string;
}

/**
 * The signed-in identity, as returned by `GET /auth/me` and carried inside a
 * {@link LoginResponse}.
 */
export interface CurrentUser {
  readonly userId: number;
  readonly portalId: number;
  readonly portalName: string;
  readonly username: string;
  readonly displayName: string;
  readonly email: string;

  /**
   * Whether the account is a host (super-user) account.
   *
   * A host account is admitted to every tenant, so permission checks must not
   * treat this flag as a substitute for a permission key — it widens the set of
   * tenants reachable, not the set of operations permitted within one.
   */
  readonly isSuperUser: boolean;

  /** The role names the account holds in the resolved tenant. */
  readonly roles: readonly string[];

  /**
   * The permission keys the account holds in the resolved tenant.
   *
   * The source consulted by the `hasPermission` structural directive. Client-side
   * checks built on this list decide what to RENDER; they are never the
   * enforcement mechanism, because a client can be modified and the list arrives
   * from a response the client received. Every operation is authorised again by
   * the server.
   */
  readonly permissions: readonly string[];
}

/**
 * A stored authentication session: the token pair, its expiry, and the identity
 * it was issued for.
 *
 * Structurally identical to {@link LoginResponse}, and declared separately rather
 * than aliased because the two evolve for different reasons — one is a wire
 * contract owned by the API, the other is what this application chooses to keep.
 * The alias would make a server-side addition silently become stored state.
 */
export interface AuthSession {
  readonly accessToken: string;
  readonly expiresAtUtc: string;
  readonly refreshToken: string;
  readonly mustChangePassword: boolean;
  readonly passwordExpiring: boolean;
  readonly mustUpdateProfile: boolean;
  readonly user: CurrentUser;
}

/**
 * Projects a login or refresh response into the session shape that is kept.
 *
 * The three advisory booleans are kept alongside the pair so that a reload does
 * not lose a prompt the caller has not yet acted on. They stay plain booleans
 * rather than optional ones: `false` means "no advisory" and is always present on
 * the wire, so there is no third state to model.
 *
 * @param response A successful login or refresh response.
 * @returns The session to store.
 */
export function sessionFromLoginResponse(response: LoginResponse): AuthSession {
  return {
    accessToken: response.accessToken,
    expiresAtUtc: response.expiresAtUtc,
    refreshToken: response.refreshToken,
    mustChangePassword: response.mustChangePassword,
    passwordExpiring: response.passwordExpiring,
    mustUpdateProfile: response.mustUpdateProfile,
    user: response.user,
  };
}
