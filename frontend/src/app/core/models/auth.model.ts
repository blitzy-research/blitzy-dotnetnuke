// ## One behaviour beyond declaration, and why it lives here

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeString,
  nonEmptyString,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

/**
 * The outcome of a single legacy sign-in attempt, ported for vocabulary and for message mapping. Not a
 * wire contract.
 */
export enum UserLoginStatus {
  /** Authentication was refused. Zero, and it must stay zero. */
  Failure = 0,

  /** Authentication succeeded for an ordinary portal user. */
  Success = 1,

  /**
   * Authentication succeeded for a host, or super-user, account. Reported separately from {@link
   * UserLoginStatus.Success} because such an account is authorised across every portal in the
   * installation rather than within one, even though both are completed sign-ins.
   */
  SuperUser = 2,

  /**
   * The account is locked out by the membership store and was refused for that reason, independently of
   * whether the supplied credentials were correct.
   */
  UserLockedOut = 3,

  /**
   * The account exists but has not been approved and is awaiting verification. The legacy control
   * intercepted this value before its authenticated test and drove the verification-code flow from it, so
   * it counted as neither a refusal nor a completed sign-in.
   */
  UserNotApproved = 4,

  /**
   * Authentication succeeded for the built-in administrator account, which presented the product's
   * well-known default credential. Reached by promoting {@link UserLoginStatus.Success} *after* the
   * credentials had already been accepted, so it is a completed sign-in carrying a security warning
   * rather than a refusal.
   */
  InsecureAdminPassword = 5,

  /**
   * Authentication succeeded for the built-in host account, which presented the product's well-known
   * default credential. Reached by promoting {@link UserLoginStatus.SuperUser} after the credentials had
   * already been accepted, on the same terms as {@link UserLoginStatus.InsecureAdminPassword}.
   */
  InsecureHostPassword = 6,
}

/**
 * Credentials posted to `POST auth/login`. Exactly three members, which is the whole of the wire
 * contract.
 */
export interface LoginRequest {
  /** The account name. */
  readonly username: string;

  /**
   * The plaintext password, sent once over the transport and never stored. Inbound clear text is correct
   * and expected here — checking a submitted credential is the entire purpose of a sign-in request — and
   * this is the only member in the contract where one appears.
   */
  readonly password: string;

  /**
   * The verification code, supplied only by tenants whose registration mode is verified registration.
   * Genuinely optional.
   */
  readonly verificationCode?: string | null;
}

// MIGRATION: net-new type with no legacy predecessor, so nobody should go looking for one.

export interface RefreshTokenRequest {
  readonly refreshToken: string;
}

/**
 * The signed-in caller's own identity and interface-gating data, returned by `GET auth/me` and carried
 * inside a {@link LoginResponse}. A purpose-built claims projection — not a user entity in disguise, and
 * not a second copy of the administrative user-detail shape in `user.model.ts`.
 */
export interface CurrentUser {
  /**
   * The surrogate key of the signed-in user. The legacy identity name is retained rather than generalised
   * to `id`, so every identifier lines up against the schema without a translation table.
   */
  readonly userId: number;

  /**
   * The portal, or tenant, the caller is signed in to. Supplied from the per-request portal context that
   * the server's alias resolution establishes, **never read from the user row**: the legacy `Users` table
   * has no portal column at all, and no script in the 88-script upgrade chain adds one, because
   * per-portal membership is a table of its own keyed by user and portal.
   */
  readonly portalId: number;

  /** The display name of the portal the caller is signed in to. */
  readonly portalName: string;

  /** The caller's sign-in name. */
  readonly username: string;

  /**
   * The caller's presentation name. **Never null, and never absent.** The column is `nvarchar(128) NOT
   * NULL` defaulting to the empty string, so here the empty-string encoding of "absent" is a schema
   * constraint rather than a data-layer convention: a caller with no display name has `""`, and the shell
   * renders {@link CurrentUser.username} in its place.
   */
  readonly displayName: string;

  /**
   * The caller's e-mail address, as a single member. the legacy object model declared this attribute
   * twice — once on the user object, whose accessor wrote straight through to a second declaration on the
   * membership object — and the two collapse into **one** wire member here. **NOT AN ACCOUNT KEY.** The
   * column carries no unique constraint and the legacy membership provider was registered with unique
   * addresses explicitly not required, so two accounts may legitimately share one address.
   */
  readonly email: string;

  /**
   * Whether the account is a host, or super-user, account. A host account is admitted to every tenant, so
   * a permission check must not treat this flag as a substitute for a permission key — it widens the set
   * of tenants reachable, not the set of operations permitted within one.
   */
  readonly isSuperUser: boolean;

  readonly isPortalAdministrator: boolean;

  /**
   * The role names the account holds in the resolved tenant. Never null; an empty array means the caller
   * holds none.
   */
  readonly roles: readonly string[];

  /**
   * The permission keys the account holds in the resolved tenant. Never null; an empty array means the
   * caller holds none.
   */
  readonly permissions: readonly string[];
}

/**
 * The token pair and identity returned by `POST auth/login` and by `POST auth/refresh`. **SECURITY: this
 * shape carries bearer credentials and must never be logged.** Both token members are live: anything
 * holding the access token can act as the caller until it lapses, and anything holding the refresh token
 * can obtain a fresh pair.
 */
export interface LoginResponse {
  /** The bearer token presented on subsequent requests. SECURITY: a live credential. */
  readonly accessToken: string;

  /**
   * Absolute expiry of the access token, as an ISO 8601 instant in **Coordinated Universal Time (UTC)** —
   * never in a local or portal-preferred zone. An absolute instant, deliberately not a remaining-seconds
   * duration.
   */
  readonly expiresAtUtc: string;

  /**
   * The token used to obtain a replacement pair. Rotated on every use: a successful refresh retires the
   * token that was presented and returns a new one, so a captured value is single-use and a replay is
   * detectable.
   */
  readonly refreshToken: string;

  readonly mustChangePassword: boolean;

  /**
   * Whether the caller's credential is approaching its expiry. `false` means no such advisory.
   * **NON-BLOCKING, and the only one of the three that was.** The legacy screen showed the same
   * interstitial but enabled its "proceed anyway" panel for this case alone.
   */
  readonly passwordExpiring: boolean;

  /**
   * Whether the caller must complete or correct their profile before continuing. `false` means no such
   * requirement. **BLOCKING**, and the one advisory the legacy flow sent to a different step rather than
   * to the credential interstitial.
   */
  readonly mustUpdateProfile: boolean;

  /**
   * Who the caller is, as of the moment the credentials were issued. Embedded as a single member rather
   * than flattened into this shape or fetched separately: flattening would restate an identity vocabulary
   * that already exists and give it two places to drift, and a separate call would cost a second round
   * trip before the application shell could render.
   */
  readonly user: CurrentUser;
}

export interface AuthSession {
  /** The bearer token presented on subsequent requests. Never logged. */
  readonly accessToken: string;

  /** Absolute expiry of {@link AuthSession.accessToken}, as an ISO 8601 UTC instant. */
  readonly expiresAtUtc: string;

  /** The rotating token used to obtain a replacement pair. Never logged. */
  readonly refreshToken: string;

  /** Blocking advisory: the credential must be changed before continuing. */
  readonly mustChangePassword: boolean;

  /**
   * Blocking advisory: required profile properties must be completed before continuing. Retained so the
   * advisory has somewhere to be read from.
   */
  readonly mustUpdateProfile: boolean;

  /** Non-blocking advisory: the credential is approaching its expiry. */
  readonly passwordExpiring: boolean;

  /** The identity the pair was issued for. */
  readonly user: CurrentUser;
}

/**
 * Projects a login or refresh response into the session shape that is kept. A pure, total field
 * selection: it reads only the argument, allocates one object, and performs no clock read, no expiry
 * arithmetic, no token parsing and no normalisation of any value.
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
    mustUpdateProfile: response.mustUpdateProfile,
    passwordExpiring: response.passwordExpiring,
    user: response.user,
  };
}

export const decodeCurrentUser: Decoder<CurrentUser> = objectOf<CurrentUser>({
  userId: decodeInteger,
  portalId: decodeInteger,
  portalName: decodeString,
  username: decodeString,
  displayName: decodeString,
  email: decodeString,
  isSuperUser: decodeBoolean,
  // The server DERIVES this from the tenant's administrator-role designation and the caller's live role
  // assignments, so it is decoded rather than recomputed here. It is non-nullable: `false` travels as data
  // and must never be read as an absence, which is why `decodeBoolean` is used and no fallback is supplied.
  isPortalAdministrator: decodeBoolean,
  roles: arrayOf(decodeString),
  permissions: arrayOf(decodeString),
});

/**
 * Validates an untrusted value as a {@link LoginResponse}. The strictest decoder in the workspace, and
 * deliberately so: its output becomes a bearer credential written into an `Authorization` header and a
 * renewal credential presented to the server.
 */
export const decodeLoginResponse: Decoder<LoginResponse> = objectOf<LoginResponse>({
  accessToken: nonEmptyString,
  expiresAtUtc: decodeDateString,
  refreshToken: nonEmptyString,
  mustChangePassword: decodeBoolean,
  passwordExpiring: decodeBoolean,
  mustUpdateProfile: decodeBoolean,
  user: decodeCurrentUser,
});
