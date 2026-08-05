using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;

// MIGRATION: nothing here is ported. The legacy application shipped no token service and no
// abstraction resembling one, so an implementer must not search the VB.NET trees for a
// predecessor to transliterate - there is none. What it displaces is the .NET Framework 2.0
// membership-and-cookie chain, whose signing material and provider registration both lived in the
// legacy web configuration and are not reproduced here. That chain is replaced
// wholesale rather than ported. The mechanics live in Infrastructure/Security/, in the JWT token
// service and its refresh-token store, and this project deliberately cannot see either: the
// application layer declares exactly one project reference, to the domain layer, so the JWT
// libraries are not resolvable here and the layering cannot be breached by accident.
//
// MIGRATION: logout has no exact counterpart, and that is the single most consequential
// behavioural difference on this contract. Library/Components/Security/PortalSecurity.vb:L77
// declares Public Sub SignOut(), which invoked FormsAuthentication.SignOut() and then destroyed
// four further cookies by name - "language", "authentication", "portalaliasid" and
// "portalroles" - back-dating the last two by thirty years so the browser dropped them at once;
// the same file's Shared ClearRoles() performs the "portalroles" half alone. Destroying a cookie
// ended the session instantly, because the session lived in the cookie. A bearer access token is
// self-contained and asserts its own validity, so once one has been handed to a caller no server
// action retracts it. Logout therefore has exactly three parts here: revoking the refresh token
// so that no successor access token can be minted, allowing the short access-token lifetime to
// elapse, and the client discarding its own copy. No server-side list of rejected access tokens
// is introduced and no per-request revocation lookup is performed - either would turn stateless
// bearer authentication back into the server-held session this migration exists to leave behind,
// and would add a store read to every single request. The Jwt:ExpirationMinutes setting is the
// only thing that bounds the residual window, which is why it must stay small.
//
// MIGRATION: the cookie-issuing counterpart has no equivalent either.
// Library/Components/Users/UserController.vb:L919 declares
// SetAuthCookie(username, CreatePersistentCookie), an empty Sub in this checkout, while the live
// call sits at L1033 inside UserLogin as
// FormsAuthentication.SetAuthCookie(user.Username, CreatePersistentCookie), followed by a
// hand-built persistent ticket that honours a PersistentCookieTimeout application setting. A
// persistent cookie becomes a refresh token. Surviving beyond the browser session is exactly
// what that cookie was for, and a rotating refresh token does the same job while being
// single-use and revocable, which the cookie was neither. Nothing on this contract mentions a
// cookie: how the two tokens travel is the caller's concern, and the Angular client holds them
// in memory first by design.
//
// MIGRATION: no member here accepts or returns a password, a password hash, a verification
// answer, a signing key or a secret of any kind, and every one of those exclusions is
// deliberate. Credential handling belongs to the domain layer's hasher abstraction and is
// orchestrated by the sign-in service, so this contract is reached only once a credential has
// already been accepted. The legacy store was reversible by design — its membership provider was
// registered with a reversible storage format and credential retrieval enabled, over signing
// material held in source control — so anything held could be recovered in clear text. Neither
// that material nor its location is reproduced anywhere in this tree. One-way BCrypt hashing
// replaces that store,
// and the signing secret behind the tokens described below reaches the implementation only
// through bound configuration - never as a parameter, never as a return value, never in a log.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Mints, rotates and revokes the bearer tokens that carry an authenticated caller's identity,
/// exposing nothing about how those tokens are built, signed, stored or parsed.
/// </summary>
/// <remarks>
/// <para>
/// Scope. This contract answers one question: <em>which token pair should this
/// already-authenticated caller hold, and is the pair it is presenting still good?</em> Whether
/// a credential is acceptable is a different question, owned by the sign-in service and by the
/// domain layer's hasher abstraction. Nothing here verifies a credential, reads a profile or
/// consults a repository on the caller's behalf: every fact a token carries is passed in, never
/// fetched.
/// </para>
/// <para>
/// <b>This documentation is the specification.</b> The implementation lives in
/// <c>Infrastructure/Security/JwtTokenService.cs</c> with its refresh-token store beside it, in a
/// project this one cannot reference and whose author has nothing else to read. The failure
/// codes, their precedence, the idempotence rules, the atomicity requirement and the lifetime
/// constraint below are therefore binding terms of the agreement between the two projects, not
/// advisory notes.
/// </para>
/// <para>
/// Registration. This abstraction is deliberately NOT registered by the application layer's
/// <c>AddApplication()</c> extension, which registers exactly seven services -
/// <c>IPortalService</c>, <c>IModuleService</c>, <c>IUserService</c>, <c>IRoleService</c>,
/// <c>IPermissionService</c>, <c>ITabService</c> and <c>IAuthService</c> - and excludes this one
/// because no implementation of it can exist in a project that cannot see a JWT library.
/// <c>AddInfrastructure(IConfiguration)</c> registers it instead.
/// </para>
/// <para>
/// Lifetime. Signing is stateless and refresh state is held by the store itself, so the shipped
/// implementation is registered as a <b>singleton</b> alongside <c>IRefreshTokenStore</c>, which is
/// what AAP section 0.4.3 specifies. It captures no scoped service - no database context, no request
/// accessor, no tenant snapshot - and it must stay that way: scope validation is enabled by the hosted
/// integration fixture, so an accidental singleton-to-scoped dependency fails at startup rather than
/// pinning one request's state for the life of the process.
/// </para>
/// <para>
/// Refresh state is PROCESS-LOCAL, and the reason is a hard constraint rather than a preference. An
/// earlier revision recorded digests in an application-owned <c>DnnMigration.RefreshTokens</c> table
/// provisioned by an operator-run script; that table does not exist in the unaltered DotNetNuke schema
/// this API is mandated to map onto, and creating one is forbidden by AAP rule T4, so login - which
/// issues a refresh token before returning any pair - could not complete at all. The store therefore
/// holds its own state. Two consequences follow and are recorded in <c>MIGRATION_NOTES.md</c>: a restart
/// does not preserve outstanding refresh families, and replicas do not share them, so a caller refreshes
/// against the instance that issued it or signs in again. An already-issued access token is unaffected
/// until its stamped expiry. A deployment needing cross-process continuity supplies its own shared
/// implementation of <c>IRefreshTokenStore</c>; the legacy <c>dbo</c> objects remain untouched either way.
/// </para>
/// <para>
/// Rule T4 is preserved by ownership and deployment mechanics: no migration or application startup
/// creates, alters or drops a legacy table. The refresh schema is separate, additive and explicit;
/// deployments apply its idempotent script before enabling token issuance.
/// </para>
/// <para>
/// Session length is bounded absolutely, not by idleness. A refresh token family receives one
/// absolute deadline when the first token of it is issued, and exchanging a token does not move
/// that deadline: the successor inherits it. A session therefore ends at a determined instant
/// however continuously it is used, and a caller wanting to continue past it authenticates again.
/// This is a security property rather than an implementation note — a deadline that each exchange
/// pushed further out would never be reached by a token that kept being exchanged, including one
/// being exchanged by a thief.
/// </para>
/// <para>
/// Configuration. The signing secret, issuer, audience, access-token lifetime and refresh-token
/// lifetime arrive through the bound JWT options class in the application layer's Options folder,
/// populated by the Api layer from <c>appsettings.json</c> under the settled keys
/// <c>Jwt:Secret</c>, <c>Jwt:Issuer</c>, <c>Jwt:Audience</c>, <c>Jwt:ExpirationMinutes</c> and
/// <c>Jwt:RefreshTokenExpirationDays</c>. That options type is an input to the implementation and
/// appears nowhere on this surface, because a caller of this contract has no business choosing a
/// token's lifetime. The current instant is read through the domain layer's injected clock
/// abstraction rather than from the ambient system clock, so expiry and rotation stay testable.
/// </para>
/// <para>
/// What the access token asserts. The emitted token contains only the subject identifier, portal
/// identifier, token identifier and the standard issuer, audience and time claims. User names,
/// host authority, roles, permission keys and profile data are deliberately absent because each can
/// change independently of the token lifetime. The server re-reads authority for protected
/// operations, and clients load their display snapshot through <c>GET /auth/me</c>.
/// </para>
/// <para>
/// Rotation. A refresh token is <b>single-use</b>. Exchanging one through
/// <see cref="RefreshAsync"/> issues a new access token <em>and</em> a new refresh token, marking
/// the presented value as used in the same atomic unit of work that writes its successor, so that
/// two concurrent presentations of one value cannot both succeed. A refresh token that is
/// unknown, already used, revoked or past its absolute expiry is refused - never honoured "just
/// this once". AuthService performs every fallible tenant, account, approval, lockout and advisory
/// read before it asks this contract to consume the token, so a dependency failure cannot spend the
/// caller's only usable value without returning its successor.
/// </para>
/// <para>
/// What rotation may change, and what it may never change. Exchanging a token proves possession of
/// something issued to one caller in one tenant, and nothing about that proof can license
/// describing the successor as belonging to anybody else. The subject identifier, the portal
/// identifier are therefore taken from the store's own record of the token being exchanged,
/// never from anything the caller supplies alongside it. No mutable authority or display field is
/// accepted by this contract, so none can be copied from the request or persisted with the family.
/// </para>
/// <para>
/// Why no read-or-validate member exists. Inspecting or validating an access token is
/// deliberately absent from this contract. The Api layer's bearer authentication handler already
/// validates every inbound token on the request path - signature, issuer, audience and lifetime -
/// and <see cref="ICurrentUser"/> is projected from the resulting verified principal, so an
/// application service that needs to know who is calling reads that abstraction instead of
/// parsing a string. A second validation entry point would create two independent code paths that
/// could disagree about whether a token is acceptable, which is a security defect rather than a
/// convenience. Should service-side inspection ever be genuinely required, it belongs on its own
/// contract with its own documented terms.
/// </para>
/// <para>
/// Failure reporting. Every member reports an expected failure by returning a failed
/// <see cref="Result"/>, never by throwing: an expired or replayed refresh token is ordinary
/// control flow, not an exceptional condition. The reason travels as a
/// <see cref="ResultReason"/> whose <c>Code</c> values are fixed by this contract and enumerated
/// on each member, so that two implementations report the same condition identically and the Api
/// layer can map each code to a problem-details response. Genuinely unexpected conditions - a
/// malformed configuration, a defect in the store - are left to surface and are translated once,
/// at the Api edge, by the global exception handler. On success, the value carried by a
/// <see cref="Result{T}"/> returned from this contract is always non-null: there is no
/// "succeeded, but there is no token pair" state, so the absent-value convention that applies
/// elsewhere in the domain never arises here.
/// </para>
/// <para>
/// Implementer's checklist. Store a refresh token as a one-way hash of the value handed to the
/// caller, never in clear text, so that a compromised store yields nothing replayable. Fix one
/// absolute expiry when a family is created, share it across every successor, and never recompute
/// it on exchange, so rotation cannot extend a session; keep a used marker per token alongside it,
/// so a replay is still recognised. Remove state that can no longer be acted on - once a family has
/// passed its deadline nothing in it can be exchanged and nothing in it remains to protect, so
/// retaining it grows the store without bounding it; do that removal on the operations that caused
/// the growth, with a ceiling on how much any one of them pays for. Support revoking every family a
/// caller holds in one indivisible step, because a credential change that revoked them one at a
/// time would leave a window in which an exchange could mint a successor into a family it had not
/// reached yet. Make the amount of state held observable, since a store of credentials must not
/// report on itself by logging. Generate token values from a cryptographically secure random source,
/// never from a counter, a timestamp or a hash of caller data. Emit no token value and no secret to
/// any log, metric, trace or exception message - record the user identifier and the outcome code
/// instead. Read the current instant through the injected clock. Honour the cancellation token on
/// every store round trip. Keep no per-caller state in a field.
/// </para>
/// </remarks>
public interface ITokenService
{
    /// <summary>
    /// Issues a fresh access-token and refresh-token pair for a caller whose credential has
    /// already been accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the sign-in service at the end of a successful sign-in. The access token carries
    /// only the account, tenant, token identifier and time/envelope claims needed to authenticate a
    /// request; mutable authority is re-read by server-side policies.
    /// </para>
    /// <para>
    /// No entity crosses this boundary. There is no overload accepting a user, role or portal
    /// entity and none may be added: the domain model stays inside the domain and application
    /// layers, and this contract is consumed from the Api layer. There is likewise no parameter
    /// for a credential, a stored password hash or a verification answer - by the time this member
    /// runs, the credential decision has already been taken elsewhere.
    /// </para>
    /// <para>
    /// The implementation writes exactly one refresh-token record per call and returns the pair only
    /// once that record is in place, because a pair whose refresh half was never recorded would
    /// appear to work and then fail at the caller's first exchange. "In place" means recorded in
    /// whichever store the deployment supplied, with the durability that store has and no more: read
    /// the paragraph on refresh state above before assuming it survives a restart. It attaches no
    /// advisory reason of its own: the weak-credential advisories the legacy sign-in flow reported
    /// as LOGIN_INSECUREADMINPASSWORD and LOGIN_INSECUREHOSTPASSWORD are the sign-in service's to
    /// propagate onto its own outcome, not this service's to discover.
    /// </para>
    /// <para>
    /// This call also fixes the absolute deadline of the session it begins. Every refresh token that
    /// later descends from the one issued here inherits that deadline unchanged, so the last instant
    /// at which this caller can obtain a new access token without authenticating again is already
    /// determined before this member returns.
    /// </para>
    /// </remarks>
    /// <param name="userId">
    /// Identifier of the authenticated caller, asserted verbatim as the token's subject. Not
    /// sentinel-checked and never coalesced: both 0 and -1 are legitimate identifiers in this
    /// schema, so neither may be read as meaning "absent".
    /// </param>
    /// <param name="portalId">
    /// Identifier of the tenant the caller signed in to, resolved upstream by the portal-alias
    /// middleware and asserted verbatim. The column seeds at -1, so -1 identifies a real tenant
    /// here and must not be treated as a missing value.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the refresh-token write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the
    /// access token, the refresh token and the access token's absolute expiry. The single expected
    /// failure is code <c>TOKEN_STORE_UNAVAILABLE</c>, reported when the refresh-token record
    /// could not be persisted; no pair is returned in that case, because handing a caller a
    /// refresh token that was never recorded would defer the failure to its first exchange. The
    /// code is part of this contract for the benefit of a store whose write can fail - a shared or durable one
    /// reached over a network. A store held in the process has no failing write and so never reports
    /// it, which is why the code must be handled rather than relied upon: whether it can occur at
    /// all is a property of the store the deployment supplied.
    /// </returns>
    Task<Result<LoginResponse>> IssueTokensAsync(
        int userId,
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a refresh token for a brand-new pair, invalidating the token presented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rotation operation, and rotation is the whole point of the design: the token
    /// presented is single-use, so a successful exchange both mints a new access token and issues
    /// a new refresh token while marking the presented value as used. Marking-as-used and writing
    /// the successor happen in one atomic unit of work, so two concurrent presentations of the
    /// same value cannot both be honoured.
    /// </para>
    /// <para>
    /// The replacement access token repeats only the minimal account and tenant identity recorded by
    /// the refresh-token store. Roles, permission keys, names and host authority are deliberately absent
    /// from the token and are re-read by the server or by the explicit current-user endpoint.
    /// </para>
    /// <para>
    /// The exchange does not extend the session. The successor's own deadline is the one fixed when
    /// the family began, so a caller that exchanges continuously still reaches that deadline and
    /// authenticates again there. Only the access token's expiry moves forward, and the value carried
    /// back describes that expiry rather than the session's.
    /// </para>
    /// <para>
    /// Re-presentation of an already-used token is the signature of a stolen token: the legitimate
    /// holder rotated it, so a second presentation came from somewhere else. On detecting that
    /// condition the implementation must revoke every one of that user's refresh tokens, exactly as
    /// <see cref="RevokeAllRefreshTokensAsync"/> does, before reporting the failure - the correct
    /// response to a suspected theft is to end every session, not merely to decline this one
    /// exchange.
    /// </para>
    /// <para>
    /// That reach is precisely why the revoked condition is evaluated before the already-used one,
    /// and why the order given below is not interchangeable. Ending every session revokes the
    /// replayed record along with the rest, so a third and a fourth presentation of the same value
    /// land on the revoked arm and change nothing: the theft response fires once and then falls
    /// quiet. Were the already-used condition evaluated first instead, a single copied value would
    /// become a reusable instrument for ending whatever sessions its owner had opened since, which
    /// hands a means of permanently signing somebody off to exactly the party who should not hold
    /// one.
    /// </para>
    /// <para>
    /// Failure here is reported in full detail because the caller is asking for a new pair and the
    /// distinction is actionable: the Api layer answers an expired token differently from a revoked
    /// one. That is the opposite of <see cref="RevokeRefreshTokenAsync"/>, which is deliberately
    /// silent about whether a token existed.
    /// </para>
    /// </remarks>
    /// <param name="refreshToken">
    /// The refresh-token value the caller presents, exactly as it was issued. Treated as an opaque
    /// string: no structure, no encoding and no embedded identifier may be inferred from it, and it
    /// must never be logged. Compared against the stored one-way hash rather than against a stored
    /// clear-text value.
    /// </param>
    /// <param name="clientBinding">
    /// A bounded server-observed client fingerprint. It is used only to keep a near-simultaneous
    /// retry from revoking the family while still treating a replay from another client as theft.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store reads and writes if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the new
    /// access token, the new refresh token and the new access token's absolute expiry. Expected
    /// failures are reported as a failed result with one of these codes, evaluated in exactly this
    /// order so that two implementations agree on which one applies:
    /// <c>REFRESH_TOKEN_NOTFOUND</c> when no record matches the presented value, whether because it
    /// was never issued or because a record that had reached its end has since been pruned;
    /// <c>REFRESH_TOKEN_REVOKED</c> when the record was explicitly revoked by a sign-out, an
    /// administrative reset or a previous theft response;
    /// <c>REFRESH_TOKEN_ALREADYUSED</c> when the record exists, has not been revoked, and has
    /// already been exchanged, which is also what triggers the revocation of every refresh token
    /// the same user holds, as described above;
    /// <c>REFRESH_TOKEN_EXPIRED</c> when the record is unused and unrevoked but the deadline of the
    /// chain it belongs to has passed;
    /// and <c>TOKEN_STORE_UNAVAILABLE</c> when the rotated pair could not be persisted.
    /// None of these is thrown - each is an expected outcome the caller is required to handle.
    /// </returns>
    Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a single refresh token so that no further access token can be minted from it. This
    /// is the only server-side effect a logout can have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backs <c>POST /api/v1/auth/logout</c>. It ends the caller's ability to <em>continue</em> a
    /// session; it cannot end the session already in progress, because the access token the caller
    /// holds remains valid until its own expiry and no server action retracts it. The caller's
    /// obligations are therefore to call this member and to discard its copy of the access token;
    /// the residual window is bounded by <c>Jwt:ExpirationMinutes</c> alone.
    /// </para>
    /// <para>
    /// Deliberately <b>idempotent</b>. Revoking a value that is unknown, already used or already
    /// revoked succeeds. Two reasons: a logout that fails is worse than useless, because a client
    /// that cannot complete one is likely to keep the token; and reporting "no such token" here
    /// would make this member an oracle that tells an unauthenticated caller whether a guessed
    /// value exists. <see cref="RefreshAsync"/> makes that distinction because its caller is asking
    /// for something and the answer is actionable; this member does not, because its caller is
    /// giving something up.
    /// </para>
    /// </remarks>
    /// <param name="refreshToken">
    /// The refresh-token value to revoke, exactly as it was issued. Opaque, matched against the
    /// stored one-way hash, and never logged.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once the token is known not to be usable
    /// - including when it was already unusable, or was never issued at all. The single expected
    /// failure is code <c>TOKEN_STORE_UNAVAILABLE</c>, reported when the revocation could not be
    /// persisted and the token consequently remains exchangeable.
    /// </returns>
    Task<Result> RevokeRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every refresh token currently held by one user, ending that user's ability to
    /// continue any session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member exists because rotation requires it, not as a convenience. Detecting that a
    /// single-use refresh token has been presented twice is a theft signal, and the required
    /// response is the reach this member describes: every refresh token the user holds, not the
    /// replayed value alone and not merely the chain it belongs to. Revoking only that chain would
    /// end one session while leaving every other session the same user had opened untouched, and
    /// nothing about a value having been copied says the rest were not copied with it - so
    /// <see cref="RefreshAsync"/> needs this operation, at exactly this width, in order to behave
    /// correctly. It serves a second, documented purpose: after an administrative credential
    /// reset, outstanding refresh tokens must stop being exchangeable, otherwise a reset would
    /// leave a compromised session alive for as long as the client kept rotating.
    /// </para>
    /// <para>
    /// <b>Its callers are named, and they are obligations rather than options.</b> Every service
    /// operation that ends an account's right to sign in, or that changes the credential by which it
    /// does so, must call this member as part of the same request: a self-service credential change,
    /// an administrative reset, the withdrawal of an approval and the deletion of an account. Each
    /// of those leaves an account that may no longer authenticate, and each would otherwise leave it
    /// holding the means to keep obtaining new access tokens regardless. The obligation is restated
    /// on those operations in the user-service contract so that it cannot be met by accident on one
    /// path and missed on another.
    /// </para>
    /// <para>
    /// <b>Indivisible across the whole account.</b> Every family belonging to the user is revoked in
    /// one step, not one family at a time. Revoking them serially would leave a window in which a
    /// concurrent exchange could mint a successor into a family the operation had not reached yet,
    /// and that successor would outlive the revocation entirely - which is exactly the outcome the
    /// member exists to prevent.
    /// </para>
    /// <para>
    /// It is <b>not</b> a way to revoke access tokens, which cannot be revoked at all. Every access
    /// token already issued to this user stays valid until its own expiry, which is short by
    /// configuration; what this member guarantees is that none of them is ever succeeded by
    /// another.
    /// </para>
    /// <para>
    /// Deliberately <b>idempotent</b>, for the same reasons as
    /// <see cref="RevokeRefreshTokenAsync"/>: a user holding no refresh token is already in the
    /// desired state, so the call succeeds rather than reporting that nothing was found.
    /// </para>
    /// </remarks>
    /// <param name="userId">
    /// Identifier of the user whose refresh tokens are to be revoked. Not sentinel-checked and
    /// never coalesced: both 0 and -1 are legitimate identifiers in this schema.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once no refresh token belonging to this
    /// user is exchangeable - including when the user held none to begin with. The single expected
    /// failure is code <c>TOKEN_STORE_UNAVAILABLE</c>, reported when the revocation could not be
    /// persisted; because the operation is idempotent, a caller may safely retry it.
    /// </returns>
    Task<Result> RevokeAllRefreshTokensAsync(
        int userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The claim vocabulary shared by whatever mints an access token and whatever reads one.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside <see cref="ITokenService"/> rather than beside either implementation because it is
/// part of the contract, not of a particular realisation of it. The claim names an access token carries
/// are observable output: the layer that signs a token and the layer that projects an authenticated
/// principal back into <see cref="ICurrentUser"/> must agree on them exactly, and a disagreement is
/// silent - a mis-spelled claim name does not fail to compile and does not throw. It presents as a caller
/// who authenticates successfully and then appears to belong to no portal and hold no permission, which
/// is indistinguishable from a legitimate authorisation denial. Declaring the vocabulary once, on the
/// abstraction both sides already depend on, removes the opportunity.
/// </para>
/// <para>
/// The registered names are spelled here rather than taken from a token library so that the wire
/// format is fixed by this contract and does not shift when a library renames its own constants between
/// major versions - which the underlying library has done.
/// </para>
/// <para>
/// Mutable roles, permissions, names and host flags are deliberately absent. Server-side authorization
/// re-reads those facts from authoritative storage instead of trusting a token snapshot.
/// </para>
/// </remarks>
public static class DnnClaimTypes
{
    /// <summary>The tenant the token was issued for.</summary>
    /// <remarks>
    /// Both -1 and 0 are legitimate portal identifiers, so a reader must not treat either as "absent".
    /// </remarks>
    public const string PortalId = "portal_id";

    /// <summary>The token's subject - the authenticated account's identifier.</summary>
    public const string Subject = "sub";

    /// <summary>A unique identifier for one particular token.</summary>
    public const string JwtId = "jti";
}
