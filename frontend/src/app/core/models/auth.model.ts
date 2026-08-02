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

  /** The scheme to present the access token under. Always `Bearer`. */
  readonly tokenType: string;

  /** Lifetime of the access token in seconds, from the moment it was issued. */
  readonly expiresIn: number;

  /** Absolute expiry of the access token, as an ISO 8601 instant in UTC. */
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

  /** Absolute expiry of the refresh token, as an ISO 8601 instant in UTC. */
  readonly refreshTokenExpiresAtUtc: string;

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
  readonly tokenType: string;
  readonly expiresAtUtc: string;
  readonly refreshToken: string;
  readonly refreshTokenExpiresAtUtc: string;
  readonly user: CurrentUser;
}

/**
 * Projects a login or refresh response into the session shape that is kept.
 *
 * `expiresIn` is deliberately dropped: it is a relative lifetime that is only
 * meaningful at the instant of issue, and keeping it alongside the absolute
 * expiry would create two representations of one fact that drift apart the moment
 * they are stored.
 *
 * @param response A successful login or refresh response.
 * @returns The session to store.
 */
export function sessionFromLoginResponse(response: LoginResponse): AuthSession {
  return {
    accessToken: response.accessToken,
    tokenType: response.tokenType,
    expiresAtUtc: response.expiresAtUtc,
    refreshToken: response.refreshToken,
    refreshTokenExpiresAtUtc: response.refreshTokenExpiresAtUtc,
    user: response.user,
  };
}
