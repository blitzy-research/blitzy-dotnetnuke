/**
 * Wire contracts for the authentication endpoints, mirroring the API's `Dtos/Auth`
 * shapes member for member.
 *
 * Type-only declarations plus one reference enumeration. Nothing here performs
 * I/O, reads a clock, decodes a token, computes a derived value or decides an
 * authorisation question; every value is assigned by the server and read by a
 * caller. The endpoint surface is closed at four:
 *
 * - `POST auth/login` — anonymous; body {@link LoginRequest}, success
 *   {@link LoginResponse}
 * - `POST auth/refresh` — anonymous; body {@link RefreshTokenRequest}, success
 *   {@link LoginResponse} again, deliberately not a type of its own
 * - `POST auth/logout` — anonymous; body {@link RefreshTokenRequest}; revokes the
 *   refresh token only
 * - `GET auth/me` — requires a bearer token; success {@link CurrentUser}
 *
 * ## How these members map onto the wire
 *
 * The API serialises with a camel-cased naming policy, so every member below is
 * spelled exactly as it appears in the JSON. Three properties of that
 * configuration are load-bearing and were read from the host rather than assumed:
 *
 * 1. **Nothing is omitted.** The serialiser runs with its ignore condition set to
 *    never, so a `false`, an empty string and an empty array all reach the wire as
 *    themselves. A "when writing null" or "when writing default" condition would
 *    silently erase exactly those values, which is why neither is configured and
 *    why no member here is optional merely to tolerate absence.
 * 2. **Enumerations travel as numbers.** No string-enumeration converter is
 *    registered, so an enumeration on the wire is its ordinal. {@link
 *    UserLoginStatus} is therefore declared with every ordinal written out.
 * 3. **Identity members carry a single lowercase `d`.** The server spells them
 *    `UserId` and `PortalId`, which camel-case to `userId` and `portalId`. The
 *    trap is that the policy lowercases a leading uppercase *run*, so a
 *    server-side `UserID` would have produced `userID` instead — and binding the
 *    wrong one yields `undefined` at runtime with no compile error. Both were
 *    checked against the server contracts.
 *
 * Instants arrive as ISO 8601 strings and are typed `string` rather than `Date`,
 * because `JSON.parse` produces a string and nothing in the transport converts it.
 * Typing them `Date` would be a claim the runtime does not honour.
 *
 * ## Sentinel discipline
 *
 * The legacy data layer encoded "absent" as a per-type sentinel rather than SQL
 * `NULL`: minus one for integers, **the empty string — not a null reference — for
 * text** (`Library/Components/Shared/Null.vb` L71-L75 literally returns `""`), the
 * minimum date for dates and `false` for booleans. Its absence test reported true
 * for all of them, including `false` itself (`Null.vb` L227-L228). Two consequences
 * bind every member below:
 *
 * - **Every boolean is a plain, non-nullable `boolean`.** A legacy `false` and a
 *   legacy "unknown" were never distinguishable, so admitting a third state would
 *   advertise a distinction the source data cannot make.
 * - **Zero and minus one are both legitimate identifiers.** `Portals.PortalID` is
 *   declared `IDENTITY(-1, 1)`, so minus one is simultaneously a real tenant key
 *   and the legacy encoding for a missing integer, while the shipped default
 *   portal is inserted explicitly as zero. Never test an identifier with
 *   `if (id)`, `id > 0` or `id <= 0`, and never default one with `id ?? -1`: use
 *   an explicit `=== undefined` or `=== null` comparison. Role, tab and module
 *   keys are seeded from zero for the same reason.
 *
 * ## Deliberate omissions
 *
 * No member of the server's token configuration appears on any shape here — not
 * the signing material, not the issuer or audience the server signs and checks
 * against, and not the configured lifetimes of either credential. The distinction
 * is exact: *when this token lapses* is a fact about this response and is
 * published; *how long tokens live* is a fact about the deployment and never is.
 * Nor does any member of the OAuth 2.0 token-endpoint vocabulary appear — no
 * token-type member, no scope, no snake-cased lifetime — because this is a
 * first-party contract for one known client rather than an RFC 6749 token
 * endpoint, so the presentation scheme is fixed by the API documentation instead
 * of restated in every response.
 *
 * MIGRATION: the legacy sign-in call took EIGHT arguments — the tenant key, the
 * user name, the password, an authentication-type literal, the verification code,
 * the tenant display name, the caller's network address, and a by-reference status
 * argument — at
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L164`. Five of
 * those eight have no counterpart on {@link LoginRequest}, and each absence is a
 * decision rather than an oversight:
 *
 * - The authentication-type literal `"DNN"`, passed twice — at L164 and again at
 *   L191 when constructing the authenticated-event arguments — disappeared with
 *   the single bearer-token path. A discriminator that can hold only one value is
 *   not a contract member.
 * - The tenant key and the tenant display name were ambient server-side values
 *   read from the Web Forms page, never posted by the browser. Accepting either
 *   from a request body would be a tenant-crossing vector, because a caller could
 *   name a site it was not addressing; the server resolves the tenant per request
 *   from the host instead.
 * - The caller's network address is observed by the server from the connection.
 *   Accepting it would let a caller choose the audit trail recorded against its
 *   own sign-in attempt, which is a spoofing surface.
 * - The `ByRef loginStatus` out-parameter became a failure code carried in an
 *   RFC 7807 problem document; see {@link UserLoginStatus}.
 *
 * MIGRATION: no "keep me signed in" flag is carried forward. The sign-in screen
 * this contract derives from never offered one, and the paired legacy
 * configuration value (`Website/release.config:L51`, shipped as `0`) governed a
 * Forms-authentication cookie lifetime that has no counterpart under stateless
 * bearer tokens. Refresh-token rotation is the modern equivalent.
 *
 * MIGRATION: password retrieval is abolished outright rather than ported. The
 * legacy flow at `Website/admin/Security/SendPassword.ascx.vb:L198-L211`
 * decrypted the stored credential and mailed it, which was only possible because
 * the membership provider was registered with a reversible format and retrieval
 * enabled (`Website/release.config:L245` and its neighbours). Credentials are now
 * held as a one-way adaptive hash, so no endpoint, screen or model member here
 * can return one, and an administrative reset is the only remedy for a forgotten
 * credential. No shape in this file carries a credential in either direction
 * except {@link LoginRequest.password}, which is the submitted value being
 * checked.
 */

/*
 * MIGRATION: this enumeration is REFERENCE VOCABULARY ONLY — it is deliberately
 * NOT part of any wire contract in this file, and no member below is typed with
 * it. The tension is real and is recorded rather than resolved silently.
 *
 * Why it is declared at all. It is one of the enumerations ported to the server's
 * domain layer, so the vocabulary is shared rather than invented here. Its
 * ordinals were genuinely externally observable in the legacy application: the
 * legacy sign-in control persisted the status in ViewState and cast it back out
 * (`Website/admin/Authentication/Login.ascx.vb:L221`), and ViewState round-trips
 * an enumeration as its underlying integer — the very mechanism this migration
 * replaces with client-held reactive state. A consumer that wants to phrase a
 * message may map a problem document's `type` back onto one of these names, and
 * having the names in one place beats each caller inventing its own.
 *
 * Why no contract carries it. A rejected sign-in never produces a {@link
 * LoginResponse} at all. Expected failures — refusal, lockout, and an account
 * awaiting approval — are the service layer's outcome type, rendered at the API
 * edge as an RFC 7807 problem document; distinguishability comes from that
 * document's `type`, `title` and `detail`, never from a status member on a body
 * returned alongside HTTP 200, which is the exact anti-pattern RFC 7807 exists to
 * prevent. Both server contracts were read at authoring time and confirmed to
 * carry no status member in any form — not as a type, a member, an ordinal or a
 * name. The two success-with-caveat statuses, {@link
 * UserLoginStatus.InsecureAdminPassword} and {@link
 * UserLoginStatus.InsecureHostPassword}, reach a caller folded onto {@link
 * LoginResponse.mustChangePassword} instead.
 *
 * MIGRATION: a legacy defect is recorded here and deliberately NOT fixed, because
 * the target makes it unreachable rather than patching it.
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L188` reads
 * `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`, so only the
 * zero value counted as not-authenticated. The preceding branch at L168
 * special-cases the not-approved value alone, so every other value fell into that
 * else arm — including {@link UserLoginStatus.UserLockedOut}, which is not zero
 * and was therefore treated as authenticated. A locked account could pass the
 * gate. The legacy tree is left byte-identical; no behavioural fix is applied to
 * it. In the target the defect is structurally impossible, because a locked
 * account yields a failure outcome that becomes a problem document and can never
 * produce a {@link LoginResponse}.
 *
 * Consequently this file declares NO authenticated-versus-refused predicate over
 * these values, and no set of "statuses that count as authenticated". Reproducing
 * that comparison is what the defect was.
 *
 * ⚠ THREE ENUMERATIONS, THREE SUCCESS CONVENTIONS. Never assume a zero success
 * value: the post-credential validation enumeration treats 0 as valid, this
 * enumeration treats 1 as success and 0 as failure, and the account-creation
 * enumeration in `user.model.ts` treats 13 as success while 0 names an operation
 * rather than an outcome. Publishing an ordinal from any of them behind a bare
 * number would put three mutually incompatible conventions behind one field.
 */

/**
 * The outcome of a single legacy sign-in attempt, ported for vocabulary and for
 * message mapping.
 *
 * Not a wire contract. No member of {@link LoginRequest}, {@link LoginResponse},
 * {@link RefreshTokenRequest} or {@link CurrentUser} is typed with this
 * enumeration; see the migration note above for why, and for the legacy defect it
 * must not be used to reproduce.
 *
 * Ported from `Library/Components/Users/Membership/UserLoginStatus.vb` (members at
 * L24-L30), whose `LOGIN_`-prefixed names are normalised to PascalCase exactly as
 * the server's domain enumeration normalises them. Every ordinal is written out
 * explicitly, matching the legacy source, which also spelled all seven out even
 * though declaration order would have produced the same numbers — deliberate
 * intent rather than an accident of ordering. **The values are part of the
 * contract and must never be renumbered.**
 *
 * Declared as an ordinary numeric enumeration rather than a constant one, because
 * the project compiles with isolated modules, under which a constant enumeration
 * is not permitted. It is consequently the only declaration in this file that
 * emits runtime JavaScript; every other export is erased at compile time.
 */
export enum UserLoginStatus {
  /**
   * Authentication was refused.
   *
   * Zero, and it must stay zero. Seven separate legacy sites seeded a status
   * variable to this value before attempting authentication — including
   * `Login.ascx.vb:L163` — so a path that neglected to assign a result failed
   * closed. Because refusal is the zero value, an unassigned value is a refusal
   * too. Reordering so a success took zero would make "not yet decided" mean
   * "authenticated", which is a security regression.
   */
  Failure = 0,

  /** Authentication succeeded for an ordinary portal user. */
  Success = 1,

  /**
   * Authentication succeeded for a host, or super-user, account.
   *
   * Reported separately from {@link UserLoginStatus.Success} because such an
   * account is authorised across every portal in the installation rather than
   * within one, even though both are completed sign-ins.
   */
  SuperUser = 2,

  /**
   * The account is locked out by the membership store and was refused for that
   * reason, independently of whether the supplied credentials were correct.
   *
   * This is the value the legacy defect described above mishandled.
   */
  UserLockedOut = 3,

  /**
   * The account exists but has not been approved and is awaiting verification.
   *
   * The legacy control intercepted this value before its authenticated test
   * (`Login.ascx.vb:L168`) and drove the verification-code flow from it, so it
   * counted as neither a refusal nor a completed sign-in. It is the value that
   * made {@link LoginRequest.verificationCode} necessary.
   */
  UserNotApproved = 4,

  /**
   * Authentication succeeded for the built-in administrator account, which
   * presented the product's well-known default credential.
   *
   * Reached by promoting {@link UserLoginStatus.Success} *after* the credentials
   * had already been accepted (`Library/Components/Users/UserController.vb`
   * L1144-L1147), so it is a completed sign-in carrying a security warning rather
   * than a refusal. A caller sees it as {@link
   * LoginResponse.mustChangePassword}.
   */
  InsecureAdminPassword = 5,

  /**
   * Authentication succeeded for the built-in host account, which presented the
   * product's well-known default credential.
   *
   * Reached by promoting {@link UserLoginStatus.SuperUser} after the credentials
   * had already been accepted (`UserController.vb` L1149-L1152), on the same
   * terms as {@link UserLoginStatus.InsecureAdminPassword}.
   */
  InsecureHostPassword = 6,
}

/*
 * MIGRATION: the image-based human-verification challenge is deliberately DELETED,
 * and no member of {@link LoginRequest} corresponds to it.
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx` declared the
 * control at L22 inside the rows opened at L18 and L21, and `Login.ascx.vb:L162`
 * gated the ENTIRE sign-in on a conjunction of a per-tenant switch and that
 * control reporting itself valid. The control belongs to `Library/Controls/**`, a
 * tree this migration excludes wholesale, so there is nothing for a member here to
 * bind to.
 *
 * This is a DELIBERATE FUNCTIONAL REDUCTION, not an oversight, and it is not
 * uncompensated. The named compensating control is request rate limiting on the
 * credential endpoints: partitioned by calling client address, rejecting with
 * HTTP 429, and configured on the server with a permit count and window that are
 * deployment settings rather than contract members — which is precisely why no
 * number for either is restated here. It is registered as a limiter the pipeline
 * applies by request method and path rather than as an attribute a controller
 * opts into, so a newly written credential endpoint cannot silently escape it.
 * Bounding the submitted credential's length is the companion control and lives in
 * the server's validators; the two address different halves of the same problem
 * and neither substitutes for the other.
 *
 * For a caller, the consequence is that a 429 is an expected outcome of this
 * endpoint and must be handled. It arrives in the same RFC 7807 shape as every
 * other error — see `problem-details.model.ts` — so it needs no special-cased
 * body, only a message that distinguishes "too many attempts" from "wrong
 * credentials".
 */

/**
 * Credentials posted to `POST auth/login`.
 *
 * Exactly three members, which is the whole of the wire contract. The shape is
 * derived from what the legacy screen actually posted rather than from any stored
 * shape, so no entity crosses this boundary: `Login.ascx` declares exactly three
 * caller-supplied inputs — `txtUsername` (L10), `txtVerification` (L16) and
 * `txtPassword` (L29) — and there is no remember-me checkbox, no portal selector,
 * no authentication-type selector and no e-mail field anywhere in that markup.
 *
 * **This type carries a credential in clear text and must never be logged.**
 * Nothing on it may reach a log sink, an audit record, a trace, a metrics
 * dimension or an exception message. The contract is kept deliberately narrow so
 * that there is as little to leak as possible.
 *
 * Validation lives on the server, which reproduces the legacy password policy
 * verbatim rather than tightening it — hardening a policy mid-migration would stop
 * existing accounts from signing in. This type enforces nothing: it does not trim,
 * clamp, coerce or re-shape any value, so what the caller sends is exactly what
 * the server observes.
 */
export interface LoginRequest {
  /** The account name. Compared case-insensitively by the server. */
  readonly username: string;

  /**
   * The plaintext password, sent once over the transport and never stored.
   *
   * Inbound clear text is correct and expected here — checking a submitted
   * credential is the entire purpose of a sign-in request — and this is the only
   * member in the contract where one appears. It is compared against a one-way
   * hash on the server; no hashed, encrypted, derived or recoverable form of a
   * credential, no knowledge-based challenge pair and no confirmation copy
   * travels on this or any other shape in this file.
   */
  readonly password: string;

  /**
   * The verification code, supplied only by tenants whose registration mode is
   * verified registration.
   *
   * Genuinely optional. Both of the rows carrying the legacy field shipped
   * `visible="false"` (`Login.ascx` L12 and L15), and `Login.ascx.vb` revealed
   * them only on the branch at L168 — where the sign-in status came back as
   * {@link UserLoginStatus.UserNotApproved} — combined with L170, where the
   * tenant's registration mode was verified registration. That mode is
   * `UserRegistrationMode.VerifiedRegistration`, declared in `portal.model.ts`
   * and deliberately neither duplicated nor imported here: a request contract has
   * no need of the enumeration, so it is named rather than depended upon. Outside
   * that branch the legacy screen never collected a code, so requiring one here
   * would reject sign-ins the legacy application accepted.
   *
   * **A `null` value, an `undefined` value and an empty string all mean exactly
   * the same thing on this member: no code was supplied.** The legacy branch at
   * `Login.ascx.vb:L177` reads `If txtVerification.Text <> "" Then` to tell an
   * incorrect code from a missing one, and the legacy absent-text sentinel
   * returns the empty string rather than a null reference
   * (`Library/Components/Shared/Null.vb` L71-L75), so the two were
   * indistinguishable in the original contract. **Nothing on either side of this
   * boundary normalises one form into the other** — converting `""` to
   * `undefined`, or `undefined` to `""`, would silently move the L177 decision
   * boundary. The judgement is kept in exactly one place, on the server, so that
   * its validator and its sign-in service cannot disagree about it.
   *
   * The two outcomes the server distinguishes are "a code is required and none
   * accompanied the credential" and "a code was supplied and did not match"; both
   * arrive as RFC 7807 problem documents rather than as a member of a successful
   * response. Their legacy wording authority is the resource file beside the
   * legacy administrative sign-in control, whose `EnterCode`, `InvalidCode` and
   * `UserNotAuthorized` entries must not be replaced with invented text.
   *
   * MIGRATION: THE DECLARATION NOW ADMITS `null`, WHICH IS WHAT THE PARAGRAPH ABOVE
   * ALREADY CLAIMED. It was typed `verificationCode?: string`, which permits the
   * member to be omitted or to hold a string and forbids `null` outright — so the
   * documented three-way equivalence was unachievable from a caller written against
   * this type, and the server's own contract disagreed with it: `LoginRequest.cs`
   * declares `string?` and treats a null and an empty string as identical. The
   * mismatch is a compile error rather than a curiosity under this workspace's
   * `strict` setting: a reactive control that has not been touched reads as
   * `string | null`, so assigning one straight into this member was rejected and a
   * caller was pushed into normalising the value — which is exactly what the
   * paragraph above forbids. Admitting `null` alongside `undefined` removes the
   * contradiction without moving the decision boundary.
   */
  readonly verificationCode?: string | null;
}

/*
 * MIGRATION: net-new type with no legacy predecessor, so nobody should go looking
 * for one. The legacy application authenticated with a sliding Forms cookie
 * (`Website/release.config:L147`, `timeout="60"`) protected by a machine key
 * committed to source control at L89-L93, so a browser never held a token, never
 * saw when its own session would elapse and never called a renewal endpoint. The
 * 60-minute access-token lifetime the server is configured with is deliberate
 * parity with that `timeout="60"`, which is why the number belongs to the server's
 * configuration and appears nowhere on these contracts.
 *
 * MIGRATION: ending a session has no true server-side counterpart.
 * `FormsAuthentication.SignOut` cleared a cookie and took effect at once, whereas
 * a signed bearer token cannot be recalled once issued. Logging out therefore
 * revokes the refresh token server-side and discards the access token
 * client-side, leaving the access token technically valid until it lapses — which
 * is why that lifetime is short and why the expiry instant is published rather
 * than left implicit. There is no blacklist and no server-side revocation of an
 * already-issued access token.
 *
 * ⚠ CONTRACT NOTE, verified against the controller rather than assumed: the logout
 * endpoint DOES accept a body, and it is this type. This type is reused rather
 * than a logout-specific request type being introduced, because the payload is
 * identical — the refresh token to revoke — and a second single-member type would
 * be a synonym. The endpoint is anonymous and succeeds regardless of whether the
 * presented token was still redeemable, so a caller must treat logout as
 * unconditionally successful and clear its own state either way.
 */

/**
 * The refresh token presented to `POST auth/refresh` and to `POST auth/logout`.
 *
 * Exactly one member, which is the whole of the request. Both endpoints are
 * anonymous: they authenticate the refresh token itself rather than a bearer
 * token, which is what allows a refresh to succeed after the access token has
 * already expired.
 *
 * **The response to a refresh is {@link LoginResponse}** — the same shape
 * `POST auth/login` returns, because both endpoints hand back a fresh pair. There
 * is deliberately no refresh-specific response type anywhere in the contract, so a
 * client needs one shape for both operations.
 *
 * The bookkeeping that lets a refresh token be redeemed exactly once, and that
 * supersedes a redeemed token with its replacement, is server-side state a caller
 * can neither read nor supply. There is consequently no rotation counter here, no
 * token identifier, no device or session identity, no access token and **no
 * caller-supplied expiry of any kind** — an expiry the caller states is an expiry
 * the caller can forge. Nor is the caller's network address accepted, for the
 * spoofing reason recorded in the module note above. The token is itself the
 * credential, so the server derives the caller from it rather than from anything
 * asserted beside it.
 *
 * **SECURITY: a live credential, and a longer-lived one than the access token.**
 * It must never be logged.
 */
export interface RefreshTokenRequest {
  /**
   * The refresh token previously issued to this caller.
   *
   * An empty value and an omitted value mean the same thing to the server:
   * neither can be redeemed, so both are refused. That preserves the legacy
   * empty-string-as-absent contract, and this boundary converts neither form into
   * the other — the value is carried through exactly as held, and the server
   * decides. Typed as a plain `string` rather than a nullable one because the
   * server initialises it to the empty string and never to null.
   */
  readonly refreshToken: string;
}

/**
 * The signed-in caller's own identity and interface-gating data, returned by
 * `GET auth/me` and carried inside a {@link LoginResponse}.
 *
 * A purpose-built claims projection — not a user entity in disguise, and not a
 * second copy of the administrative user-detail shape in `user.model.ts`. It is
 * fetched on every application-shell render, so it stays deliberately small.
 *
 * **SECURITY: no credential material and no session artefact appears here, and
 * none may ever be added.** Being the payload most likely to be captured by
 * request and response logging is exactly what makes keeping every
 * credential-shaped member off it the thing that makes such logging safe by
 * construction. The legacy password, password-answer and password-question
 * members, every password format, salt and token, and the legacy hydration,
 * dirty-tracking and HTTP cache-policy artefacts are all deliberately absent, as
 * are the legacy profile and membership composites — those have their own
 * contracts, fetched deliberately so that this per-render response stays small.
 * No status enumeration appears either, and no date member at all: the shell needs
 * none, so no date sentinel decision arises here.
 */
export interface CurrentUser {
  /**
   * The surrogate key of the signed-in user.
   *
   * The legacy identity name is retained rather than generalised to `id`, so
   * every identifier lines up against the schema without a translation table.
   * Seeded from one, so zero never appears — but see {@link CurrentUser.portalId}
   * for why that reasoning does not generalise.
   */
  readonly userId: number;

  /**
   * The portal, or tenant, the caller is signed in to.
   *
   * Supplied from the per-request portal context that the server's alias
   * resolution establishes, **never read from the user row**: the legacy `Users`
   * table has no portal column at all, and no script in the 88-script upgrade
   * chain adds one, because per-portal membership is a table of its own keyed by
   * user and portal. A legacy user object exposed a portal property, which is
   * what makes this worth stating explicitly.
   *
   * ⚠ IDENTIFIER TRAP: do not test this value for absence. The column is declared
   * `IDENTITY(-1, 1)`, so minus one is a real tenant key, and the shipped default
   * portal is inserted explicitly as zero. Minus one is *also* the legacy
   * encoding for a missing integer, so one value means both a real portal and "no
   * portal at all". A guard that rejects a non-positive identifier rejects two
   * real tenants.
   */
  readonly portalId: number;

  /**
   * The display name of the portal the caller is signed in to.
   *
   * Carried so the shell header can title itself without a second round trip.
   * Like {@link CurrentUser.portalId} it comes from the resolved portal context
   * rather than from the user row.
   */
  readonly portalName: string;

  /**
   * The caller's sign-in name.
   *
   * The only uniquely constrained attribute on the user row, so this member — and
   * not {@link CurrentUser.email} — is the account key.
   */
  readonly username: string;

  /**
   * The caller's presentation name.
   *
   * **Never null, and never absent.** The column is `nvarchar(128) NOT NULL`
   * defaulting to the empty string, so here the empty-string encoding of "absent"
   * is a schema constraint rather than a data-layer convention: a caller with no
   * display name has `""`, and the shell renders {@link CurrentUser.username} in
   * its place. Given and family names are not carried; the user-detail contract
   * owns those.
   */
  readonly displayName: string;

  /**
   * The caller's e-mail address, as a single member.
   *
   * MIGRATION: the legacy object model declared this attribute twice — once on the
   * user object, whose accessor wrote straight through to a second declaration on
   * the membership object — and the two collapse into **one** wire member here.
   *
   * **NOT AN ACCOUNT KEY.** The column carries no unique constraint and the legacy
   * membership provider was registered with unique addresses explicitly not
   * required (`Website/release.config:L244`, `requiresUniqueEmail="false"`), so two
   * accounts may legitimately share one address. Nothing may treat this member as
   * an identifier or as a sign-in credential. Non-nullable and empty when unknown,
   * following the legacy absent-text encoding rather than the column's
   * nullability.
   */
  readonly email: string;

  /**
   * Whether the account is a host, or super-user, account.
   *
   * A host account is admitted to every tenant, so a permission check must not
   * treat this flag as a substitute for a permission key — it widens the set of
   * tenants reachable, not the set of operations permitted within one. Host-level
   * administration is beyond this migration's scope, so the flag exists to let the
   * shell suppress affordances rather than to unlock any.
   */
  readonly isSuperUser: boolean;

  /**
   * The role names the account holds in the resolved tenant.
   *
   * Never null; an empty array means the caller holds none. Role names only — the
   * role contracts own the richer shape.
   *
   * MIGRATION: the legacy accessor performed database access. It tested a private
   * hydration flag and, when unset, constructed a role controller and queried the
   * roles for the user and portal *from inside the property getter*, so merely
   * serialising the object issued a query. On a response fetched once per shell
   * render that is the wrong place for I/O, so both the lazy getter and its flag
   * are gone and the server fills this array from data it has already loaded.
   *
   * These are the roles of the SIGNED-IN CALLER, which is a different subject from
   * the roles of an account being administered; those live on the user contracts
   * and are not duplicated here.
   */
  readonly roles: readonly string[];

  /**
   * The permission keys the account holds in the resolved tenant.
   *
   * Never null; an empty array means the caller holds none. Plain strings rather
   * than the domain permission-key enumeration, because the legacy permission keys
   * were themselves strings evaluated by the legacy permission controllers, and
   * because the structural directive that consumes this list takes a string.
   *
   * ⚠ **The client-side `hasPermission` directive is NEVER the sole enforcement
   * mechanism.** This list is advisory: it exists so the client can hide
   * affordances the caller cannot exercise. Authoritative enforcement is
   * server-side policy-based authorisation, which re-evaluates every request
   * against stored state and answers HTTP 403. A caller who tampers with this
   * response changes what a menu looks like and nothing about what the API will
   * permit. This shape offers no "may the caller do this" helper, because that
   * would place an authorisation decision inside a data carrier.
   */
  readonly permissions: readonly string[];
}

/*
 * MIGRATION: the legacy post-credential check reported ONE of five values with a
 * fixed precedence; the target reports THREE INDEPENDENT BOOLEANS. This is a real
 * semantic divergence and is annotated rather than absorbed.
 *
 * The legacy post-credential validation enumeration lived in its own source file
 * under `Library/Components/Users/Membership/` (members at L24-L28, every ordinal
 * written out: valid 0, password-expired 1, password-expiring 2, update-profile 3,
 * update-password 4) and was returned by
 * `Library/Components/Users/UserController.vb:L1171`. It is named descriptively
 * here rather than by identifier because it is deliberately NOT declared anywhere
 * in this file: it is not one of the ported domain enumerations, nothing serialises
 * it, and its meaning survives entirely as the three flags below. Value by value:
 *
 *   0 valid              -> no flag set; all three false
 *   1 password-expired   -> mustChangePassword   (legacy behaviour BLOCKING)
 *   2 password-expiring  -> passwordExpiring     (legacy behaviour NON-BLOCKING)
 *   3 update-profile     -> mustUpdateProfile    (legacy behaviour BLOCKING)
 *   4 update-password    -> mustChangePassword   (legacy behaviour BLOCKING)
 *
 * The blocking distinction is measured, not inferred: the legacy administrative
 * sign-in control rendered the same interstitial for 1, 2 and 4 but enabled its
 * "proceed anyway" panel for value 2 alone, and sent value 3 to a different step
 * entirely.
 *
 * WHY THIS IS A DIVERGENCE. The legacy enumeration was SINGLE-VALUED WITH
 * PRECEDENCE — `UserController.vb` L1176 assigned the forced update first, then
 * L1182 and L1185 handled the expired and expiring cases, and L1189-L1194 tested
 * the profile case only while the value was still `VALID`, so exactly one
 * condition could ever be reported and the rest were masked. Three independent
 * booleans can therefore express combinations the legacy could not, such as an
 * expired credential on an account that also owes a profile update. That is
 * strictly more information, never less, so a client handling each flag
 * independently cannot regress — but it IS a behavioural difference and is
 * recorded as one.
 *
 * ADDITIONALLY, the two success-with-caveat sign-in statuses — {@link
 * UserLoginStatus.InsecureAdminPassword} and {@link
 * UserLoginStatus.InsecureHostPassword}, promoted at `UserController.vb`
 * L1144-L1152 when a shipped default credential is presented — FOLD ONTO
 * `mustChangePassword`, because forcing a credential change is precisely the legacy
 * remediation intent for both. A FOURTH DEDICATED BOOLEAN WAS CONSIDERED AND
 * REJECTED: it would add a member no client could act on differently, and this
 * contract is held to a minimal surface.
 *
 * The `mustChangePassword` identifier is spelled identically here, on the
 * administrative account contract in `user.model.ts`, and on both server contracts
 * behind them. The agreement is load-bearing: were the names to drift, one screen
 * would stop learning that the account it just loaded must change its password.
 */

/**
 * The token pair and identity returned by `POST auth/login` and by
 * `POST auth/refresh`.
 *
 * **SECURITY: this shape carries bearer credentials and must never be logged.**
 * Both token members are live: anything holding the access token can act as the
 * caller until it lapses, and anything holding the refresh token can obtain a
 * fresh pair. Request and response logging must exclude this body in full, and no
 * structured log event may capture either member or any fragment of one.
 *
 * A rejected sign-in does not produce this shape at all — expected failures arrive
 * as RFC 7807 problem documents, so there is no `success`, `isSuccess`, `result`,
 * `error`, `errorCode`, `errorMessage` or `statusCode` member here, and no status
 * enumeration in any form. The HTTP status is the status, and the correlation
 * identifier travels as a response header rather than in the body. Neither the
 * caller's roles nor its permissions are duplicated at this level: {@link
 * LoginResponse.user} owns them, and a second copy would be a second source of
 * truth.
 */
export interface LoginResponse {
  /**
   * The bearer token presented on subsequent requests.
   *
   * SECURITY: a live credential. Never log it, never place it in a URL, and never
   * persist it where another origin can read it. The scheme it is presented under
   * is fixed by the API contract and is deliberately not restated as a member of
   * this response.
   */
  readonly accessToken: string;

  /**
   * Absolute expiry of the access token, as an ISO 8601 instant in **Coordinated
   * Universal Time (UTC)** — never in a local or portal-preferred zone.
   *
   * An absolute instant, deliberately not a remaining-seconds duration. A client
   * schedules its refresh against this value instead of discovering expiry through
   * a rejected request, and the value does not drift with transit or parse time the
   * way a relative lifetime would — a relative lifetime is only correct at the
   * moment the response is written. **Exactly one expiry representation is
   * published**, so there is no second member to reconcile it against: there is no
   * relative lifetime, no refresh-token expiry, and no bearer-scheme member.
   *
   * Typed `string` because that is what `JSON.parse` yields and nothing in the
   * transport converts it. This shape performs no arithmetic on it and exposes no
   * derived "has it lapsed" or "seconds remaining" member, because that question
   * must be answered against the reader's own clock at the moment it is asked, not
   * against a value frozen when the response was serialised.
   *
   * On a successful outcome this is always a real future moment. It is never the
   * minimum date, which is what the legacy layer used to encode an absent date, so
   * no sentinel interpretation applies to it.
   */
  readonly expiresAtUtc: string;

  /**
   * The token used to obtain a replacement pair.
   *
   * Rotated on every use: a successful refresh retires the token that was
   * presented and returns a new one, so a captured value is single-use and a replay
   * is detectable. Its server-side state — when it lapses, whether it has been
   * redeemed, and which family it belongs to — is deliberately not published: an
   * expiry a client can read is an expiry a client can reason around, and a
   * rotation counter would be an oracle.
   *
   * SECURITY: a live credential, and a longer-lived one than {@link
   * LoginResponse.accessToken}. The prohibition on logging applies with more force.
   */
  readonly refreshToken: string;

  /**
   * Whether the caller must change their credential before continuing.
   * `false` means no such requirement.
   *
   * **BLOCKING** — the legacy interstitial offered no way past it. Absorbs both
   * legacy blocking credential cases (an administrator-forced update and an
   * already-expired credential) and both success-with-caveat sign-in statuses
   * raised when a shipped default credential is still in use. See the migration
   * note above. The API enforces the current stored value on every protected
   * request; this client-side value exists to choose a remediation experience, not
   * to make an authorization decision.
   */
  readonly mustChangePassword: boolean;

  /**
   * Whether the caller's credential is approaching its expiry.
   * `false` means no such advisory.
   *
   * **NON-BLOCKING, and the only one of the three that was.** The legacy screen
   * showed the same interstitial but enabled its "proceed anyway" panel for this
   * case alone. Treat it as a prompt the caller may decline, never as a gate —
   * which is exactly why it is a separate member from {@link
   * LoginResponse.mustChangePassword} rather than folded into it.
   */
  readonly passwordExpiring: boolean;

  /**
   * Whether the caller must complete or correct their profile before continuing.
   * `false` means no such requirement.
   *
   * **BLOCKING**, and the one advisory the legacy flow sent to a different step
   * rather than to the credential interstitial. Because the legacy enumeration was
   * single-valued and tested this case last — only while no other advisory had been
   * raised — the legacy flow could never report it together with a credential
   * advisory. This contract can, and that widening is the divergence recorded
   * above.
   *
   * The server decides it from both halves of the legacy condition: a per-tenant
   * gate read from the tenant's membership settings, and a completeness walk of the
   * tenant's required profile-property definitions against the caller's stored
   * values. It is carried here because the sign-in contract is the one that owns
   * this signal — the administrative account contract in `user.model.ts` does not
   * carry it, and says so explicitly. The API re-evaluates that state on every
   * protected request and fails closed when it cannot be read; this value is a
   * presentation signal for the same server-enforced gate.
   */
  readonly mustUpdateProfile: boolean;

  /**
   * Who the caller is, as of the moment the credentials were issued.
   *
   * Embedded as a single member rather than flattened into this shape or fetched
   * separately: flattening would restate an identity vocabulary that already exists
   * and give it two places to drift, and a separate call would cost a second round
   * trip before the application shell could render. The fuller administrative
   * identity shape is deliberately not embedded — it is far larger than a sign-in
   * needs.
   *
   * A snapshot rather than a live view: an entitlement granted after issue appears
   * neither here nor in the token until the client refreshes. Because the server
   * always re-authorises against stored state, a stale snapshot can only make a
   * client offer an affordance the API then refuses — it can never widen access,
   * which is the correct direction for it to fail.
   */
  readonly user: CurrentUser;
}

/**
 * A stored authentication session: the token pair, its expiry, the advisories this
 * application can act on, and the identity the pair was issued for.
 *
 * Declared separately from {@link LoginResponse} rather than aliased to it,
 * deliberately, because the two evolve for different reasons — one is a wire
 * contract owned by the API, the other is what this application chooses to keep. An
 * alias would make any server-side addition silently become stored state.
 *
 * ALL THREE ADVISORIES ARE KEPT, and the two blocking ones are kept because the API
 * enforces them against authoritative storage on every protected request. An earlier
 * revision retained two and dropped {@link LoginResponse.mustUpdateProfile} on the
 * grounds that no screen consumed it yet. That reasoning was wrong in a way worth
 * recording, because the shape of the mistake recurs: the sign-in method returns the
 * identity rather than the session, so dropping the advisory here did not leave it
 * "readable from the response by whatever needs it" — it made the advisory
 * **unreachable by any caller at all**, and the legacy blocking profile-completion
 * prompt was therefore lost rather than deferred. An advisory the server troubles
 * itself to compute and send is kept, and it is kept in the one place a caller can
 * reach it from, whether or not the screen that finally acts on it exists today.
 *
 * The advisories are carried on the RESPONSE and in this session, and nowhere else:
 * the access token carries no claim describing them, so a client neither can nor need
 * decode one, and the server recomputes the gate from storage on every request it
 * guards. Keeping the values here makes the response and the browser session describe
 * the same gate; they remain advisory to presentation code, because the API is always
 * the enforcement point.
 *
 * MIGRATION: the legacy flow sent the profile advisory to a different step rather than
 * to the credential interstitial, and no profile-completion step is built yet. What
 * survives is therefore the SIGNAL, not the interstitial: the advisory is stored,
 * projected and readable, and the screen that consumes it is outstanding work rather
 * than a dropped requirement.
 *
 * All three advisories are plain booleans rather than optional ones: `false` means
 * "no advisory" and is always present on the wire, so there is no third state to
 * model, and a missing key must never become indistinguishable from an explicit
 * `false`.
 */
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
   * Blocking advisory: required profile properties must be completed before continuing.
   *
   * Retained so the advisory has somewhere to be read from. See the note on this
   * interface for why dropping it made it unreachable rather than deferred.
   */
  readonly mustUpdateProfile: boolean;

  /** Non-blocking advisory: the credential is approaching its expiry. */
  readonly passwordExpiring: boolean;

  /** The identity the pair was issued for. */
  readonly user: CurrentUser;
}

/**
 * Projects a login or refresh response into the session shape that is kept.
 *
 * A pure, total field selection: it reads only the argument, allocates one object,
 * and performs no clock read, no expiry arithmetic, no token parsing and no
 * normalisation of any value. All three advisories are carried through unchanged
 * so that a refresh does not lose a blocking requirement or expiry prompt the
 * caller has not yet acted on.
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
