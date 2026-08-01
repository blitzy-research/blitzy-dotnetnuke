using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;

// MIGRATION: this contract is net-new. The legacy application shipped no token service and no
// abstraction resembling one, so an implementer must not search the VB.NET trees for a
// predecessor to transliterate - there is none. What it displaces is the .NET Framework 2.0
// membership-and-cookie chain declared in Website/release.config: the machine-key element at
// L89-L93 and the SqlMembershipProvider registration at L217-L246. That chain is replaced
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
// already been accepted. The legacy store was reversible by design: Website/release.config
// L89-L93 commits a 3DES decryption key directly into source control, and L217-L246 registers
// the membership provider with a reversible storage format and credential retrieval enabled, so
// anything held could be recovered in clear text. One-way BCrypt hashing replaces that store,
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
/// Lifetime, and the trap that comes with it. The intended registration is a <b>singleton</b>:
/// signing is stateless and the configuration behind it is fixed for the process lifetime. A
/// singleton must therefore not capture a scoped dependency - not the database context, not a
/// repository, not the tenant context, not the caller-identity abstraction. Doing so either pins
/// one request's state for the life of the process or fails at start-up with a "cannot consume
/// scoped service from singleton" validation error, and no compiler catches either. Refresh-token
/// persistence is genuine I/O, so an implementation that needs a store must resolve a scope per
/// call, or delegate to a store that does, and must hold no scoped instance in a field.
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
/// What the access token asserts. The emitted token states, at minimum, the subject identifier,
/// the sign-in name, the portal identifier, the super-user flag, one entry per role name and one
/// entry per permission key. That is precisely the set of facts <see cref="ICurrentUser"/>
/// projects at the Api edge, and that projection is their only consumer - which is why the
/// parameters of <see cref="IssueTokensAsync"/> mirror it member for member. The role entries
/// derive from the <c>Roles</c>, <c>UserRoles</c> and <c>RoleGroups</c> tables, read by the
/// caller through the domain layer's role repository; this service performs no such read and must
/// never invent, filter, deduplicate or re-order what it is handed.
/// </para>
/// <para>
/// Rotation. A refresh token is <b>single-use</b>. Exchanging one through
/// <see cref="RefreshAsync"/> issues a new access token <em>and</em> a new refresh token, marking
/// the presented value as used in the same atomic unit of work that writes its successor, so that
/// two concurrent presentations of one value cannot both succeed. A refresh token that is
/// unknown, already used, revoked or past its absolute expiry is refused - never honoured "just
/// this once". Because the rotated access token is minted afresh, a role or permission change
/// takes effect at the next exchange rather than at the next sign-in; an implementation must
/// re-read the caller's current roles and permission keys for the new token rather than copying
/// the entries of the token being replaced.
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
/// caller, never in clear text, so that a compromised store yields nothing replayable. Give every
/// refresh token an absolute expiry as well as a used marker, so rotation cannot extend a session
/// indefinitely. Generate token values from a cryptographically secure random source, never from a
/// counter, a timestamp or a hash of caller data. Emit no token value and no secret to any log,
/// metric, trace or exception message - record the user identifier and the outcome code instead.
/// Read the current instant through the injected clock. Honour the cancellation token on every
/// store round trip. Keep no per-caller state in a field.
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
    /// Called by the sign-in service at the end of a successful sign-in, and by any
    /// administrative flow that must hand a caller a new pair. Every fact the emitted token
    /// asserts arrives as a parameter, which is what keeps this member free of repository access
    /// and therefore safe to implement in a singleton.
    /// </para>
    /// <para>
    /// No entity crosses this boundary. There is no overload accepting a user, role or portal
    /// entity and none may be added: the domain model stays inside the domain and application
    /// layers, and this contract is consumed from the Api layer. There is likewise no parameter
    /// for a credential, a stored password hash or a verification answer - by the time this member
    /// runs, the credential decision has already been taken elsewhere.
    /// </para>
    /// <para>
    /// The implementation writes exactly one refresh-token record per call and returns the pair
    /// only once that record is durably stored, because a pair whose refresh half was never
    /// recorded would appear to work and then fail at the caller's first exchange. It attaches no
    /// advisory reason of its own: the weak-credential advisories the legacy sign-in flow reported
    /// as LOGIN_INSECUREADMINPASSWORD and LOGIN_INSECUREHOSTPASSWORD are the sign-in service's to
    /// propagate onto its own outcome, not this service's to discover.
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
    /// <param name="userName">
    /// Sign-in name of the caller, asserted verbatim so that the Api edge can project it without
    /// a further lookup. Trimming, casing and canonicalisation are the sign-in service's
    /// responsibility and must not be applied here.
    /// </param>
    /// <param name="isSuperUser">
    /// <see langword="true"/> when the caller is a host-level super-user. Asserted as its own
    /// entry rather than inferred from a role name, because the legacy authorisation checks
    /// short-circuit on this flag independently of role membership.
    /// </param>
    /// <param name="roles">
    /// Names of the roles the caller currently holds, already resolved by the caller from the
    /// <c>Roles</c>, <c>UserRoles</c> and <c>RoleGroups</c> tables. Pass an empty collection - not
    /// <see langword="null"/> - when the caller holds none.
    /// </param>
    /// <param name="permissionKeys">
    /// Permission keys the caller currently holds, already resolved by the caller. Pass an empty
    /// collection - not <see langword="null"/> - when the caller holds none. These entries inform
    /// the client which affordances to render; they never replace the server-side authorisation
    /// policy, which re-evaluates permissions on every request.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the refresh-token write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the
    /// access token, the refresh token and the access token's absolute expiry. The single expected
    /// failure is code <c>TOKEN_STORE_UNAVAILABLE</c>, reported when the refresh-token record
    /// could not be persisted; no pair is returned in that case, because handing a caller a
    /// refresh token that was never recorded would defer the failure to its first exchange.
    /// </returns>
    Task<Result<LoginResponse>> IssueTokensAsync(
        int userId,
        int portalId,
        string userName,
        bool isSuperUser,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissionKeys,
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
    /// The new access token is minted from the caller's <em>current</em> roles and permission keys,
    /// re-read for this exchange, not copied from the token being replaced. A role granted or
    /// withdrawn since the last exchange therefore takes effect within one access-token lifetime
    /// instead of persisting until the caller signs in again. Because the implementation is a
    /// singleton, that re-read must happen through a scope resolved for this call.
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
    /// <param name="cancellationToken">
    /// Token used to abandon the store reads and writes if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the new
    /// access token, the new refresh token and the new access token's absolute expiry. Expected
    /// failures are reported as a failed result with one of these codes, evaluated in exactly this
    /// order so that two implementations agree on which one applies:
    /// <c>REFRESH_TOKEN_NOTFOUND</c> when no record matches the presented value, whether because it
    /// was never issued or because an expired record has since been pruned;
    /// <c>REFRESH_TOKEN_ALREADYUSED</c> when the record exists and has already been exchanged, which
    /// is also what triggers the family-wide revocation described above;
    /// <c>REFRESH_TOKEN_REVOKED</c> when the record was explicitly revoked by a sign-out, an
    /// administrative reset or a previous theft response;
    /// <c>REFRESH_TOKEN_EXPIRED</c> when the record is unused and unrevoked but its absolute expiry
    /// has passed; and <c>TOKEN_STORE_UNAVAILABLE</c> when the rotated pair could not be persisted.
    /// None of these is thrown - each is an expected outcome the caller is required to handle.
    /// </returns>
    Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
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
    /// response is to revoke the whole family rather than just the replayed value - so
    /// <see cref="RefreshAsync"/> needs this operation to exist in order to behave correctly. It
    /// serves a second, documented purpose: after an administrative credential reset, outstanding
    /// refresh tokens must stop being exchangeable, otherwise a reset would leave a compromised
    /// session alive for as long as the client kept rotating.
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
