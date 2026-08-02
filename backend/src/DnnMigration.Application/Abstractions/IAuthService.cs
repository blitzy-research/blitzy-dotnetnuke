using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

// MIGRATION: this contract owns the sign-in USE CASE; ITokenService owns the token MECHANISM. The
// division is not stylistic. ITokenService already declares issuing, refreshing and revoking, and
// its own documentation at ITokenService.cs:L168 says its issuing member is "called by the sign-in
// service at the end of a successful sign-in" - this is that service. Nothing here restates a token
// member as a pass-through, because a second declaration of the same operation would be a second
// place for its contract to drift.
//
// MIGRATION: the legacy sign-in decision was spread across three files, and this contract collapses
// it into one member. The screen's submit handler at
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197 called
// UserController.ValidateUser at L164 with eight arguments, one of them a by-reference status; that
// method, at Library/Components/Users/UserController.vb:L1132-L1157, delegated to the membership
// provider at L1136 and then re-graded the status; and the provider itself, at
// Library/Providers/MembershipProviders/AspNetMembershipProvider/AspNetMembershipProvider.vb:L1429-L1513,
// made the actual decision. The by-reference status argument becomes the reason code on the
// returned result, and the screen's message-string branching becomes the caller's business.
//
// MIGRATION: the fixed authentication-mechanism argument is dropped. The legacy call supplied the
// literal "DNN" twice, at Login.ascx.vb:L164 and again at L191, and the provider branched on it at
// AspNetMembershipProvider.vb:L1439 and L1482 to choose between a password check and a token check.
// The target has exactly one sign-in path, so a discriminator could hold only one value and is not
// carried. LoginRequest records the same decision.

/// <summary>
/// The sign-in contract: it decides whether a submitted credential authenticates, applies the
/// account-verification flow, and answers the authenticated caller's question about itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outcome model.</b> Both members return a result. An <em>expected</em> refusal is a failed
/// result carrying a stable code, never an exception; the API edge turns a code into a problem
/// document and the client turns it into wording. An <em>unexpected</em> fault stays an exception
/// and is shaped by the global handler at the edge.
/// </para>
/// <para>
/// <b>One answer for every rejected credential.</b> An unknown account name and a wrong password
/// are reported identically, with code <c>INVALID_CREDENTIALS</c> and identical wording, because
/// any difference between the two turns this member into an account-name oracle. The sign-in
/// request validator states the same rule for its messages, and the two must not disagree.
/// </para>
/// <para>
/// <b>Refusal codes.</b> The set an implementation may raise is fixed:
/// <c>INVALID_CREDENTIALS</c>, the submitted name and password did not match an account of this
/// tenant; <c>ACCOUNT_LOCKED_OUT</c>, the account is locked and its automatic-unlock window has
/// not elapsed; <c>VERIFICATION_REQUIRED</c>, the account is not yet approved, this tenant
/// registers its users by verification, and no verification code accompanied the credential;
/// <c>VERIFICATION_CODE_INVALID</c>, the same account state but a code was supplied and did not
/// match; and <c>ACCOUNT_NOT_APPROVED</c>, the account is not yet approved and this tenant does
/// <em>not</em> register by verification, so no code exists that would admit it and an
/// administrator must act. A refusal from the token store propagates unchanged as
/// <c>TOKEN_STORE_UNAVAILABLE</c> and is not re-coded here, so a caller can tell "your credential
/// was refused" from "your credential was accepted and we could not record the session".
/// </para>
/// <para>
/// <b>The codes are upper snake case</b>, matching <see cref="ITokenService"/>, which is the other
/// half of the same endpoint group and already reports <c>TOKEN_STORE_UNAVAILABLE</c> in that
/// form. The domain-administration contracts in this folder use lower dotted codes instead. The
/// inconsistency is pre-existing and is not resolved by inventing a third style here: matching the
/// nearest neighbour keeps the two halves of one endpoint group legible together.
/// </para>
/// <para>
/// <b>A success may still carry an advisory.</b> The legacy flow graded two sign-ins as successful
/// but insecure - <c>UserController.vb:L1144-L1153</c> re-graded a successful administrator whose
/// password was still "admin" or "dnnadmin", and a successful host whose password was still "host"
/// or "dnnhost" - and the screen warned rather than refused. That grading survives as an advisory
/// reason on a <em>successful</em> result, code <c>INSECURE_DEFAULT_PASSWORD</c>, which is
/// precisely the case the result type was built for: its own documentation at
/// <c>Domain/Common/Result.cs:L195</c> names "the weak-password caveat the legacy login flow"
/// as the reason a success can carry a reason. Refusing such a sign-in instead would lock an
/// installation out of the two accounts every installation ships with.
/// </para>
/// <para>
/// <b>Layering.</b> This contract names no store, no hashing algorithm, no token library and no
/// web-framework type. It reads the submitted credential from a request contract, reaches accounts
/// and roles through the domain repository abstractions, verifies the password through the domain
/// password-hasher abstraction, and mints the session through <see cref="ITokenService"/>.
/// </para>
/// <para>
/// <b>Implementer's checklist.</b> Decide in the order the members below prescribe and not in the
/// order the legacy provider used - the difference is deliberate and is annotated on
/// <see cref="LoginAsync"/>. Compare the password through the domain hasher, never by string
/// equality, and never with an early return that shortens the comparison for an unknown account.
/// Read the current instant through the injected clock. Emit no password, no verification code and
/// no token value to any log, metric, trace or exception message; record the tenant, the account
/// identifier where one was resolved, and the outcome code. Honour the cancellation token on every
/// store round trip. Keep no per-caller state in a field.
/// </para>
/// </remarks>
public interface IAuthService
{
    /// <summary>
    /// Exchanges a valid refresh token for a new token pair, retiring the presented token.
    /// </summary>
    /// <param name="request">The refresh token to redeem.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the new pair. Fails with
    /// <c>auth.invalid_refresh_token</c> when the token is unknown, expired, already redeemed or revoked.
    /// </returns>
    /// <remarks>
    /// Rotation is mandatory rather than optional: the presented token is retired as part of the same
    /// operation, so replaying a captured refresh token fails and the replay is detectable. Claims are
    /// re-resolved from stored state during the refresh, which is how a role granted after the previous
    /// issue becomes effective.
    /// </remarks>
    Task<Result<LoginResponse>> RefreshAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a refresh token so it can no longer be redeemed.
    /// </summary>
    /// <param name="request">The refresh token to revoke.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the token was already unknown
    /// or already revoked - sign-out is idempotent, and reporting "no such token" would let a caller
    /// probe which tokens exist.
    /// </returns>
    /// <remarks>
    /// The caller's current access token is <em>not</em> invalidated, because a stateless bearer token
    /// cannot be recalled; the client discards it and it lapses at its published expiry. This is the one
    /// respect in which sign-out is weaker than the legacy cookie clearance, and it is why the access
    /// token's lifetime is short.
    /// </remarks>
    Task<Result> LogoutAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Verifies a submitted credential against one tenant's accounts and, when it is accepted,
    /// mints the caller's session.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant the credential is being presented to, backed by
    /// <c>Portals.PortalID</c>, which is declared <c>IDENTITY(-1, 1)</c>, so zero and minus one
    /// are both real tenants and neither may be rejected as though it meant "absent".
    /// </param>
    /// <param name="request">
    /// The submitted credential, whose shape has already been checked by
    /// <c>LoginRequestValidator</c>. Its verification-code member is consumed by this member and
    /// by nothing else.
    /// </param>
    /// <param name="ipAddress">
    /// The caller's address as the host observed it, or <see langword="null"/> when it cannot be
    /// determined. It is recorded on the sign-in audit entry described below and never used to
    /// decide the outcome, so a caller that cannot be attributed is refused or accepted on the
    /// merits of its credential alone. It is a parameter rather than something this layer reads for
    /// itself because the address is a property of the transport, which this layer cannot see.
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed while accounts, roles and the token store are read and written.
    /// </param>
    /// <returns>
    /// A successful result carrying the access-token and refresh-token pair, optionally with the
    /// <c>INSECURE_DEFAULT_PASSWORD</c> advisory attached, or a failed result carrying one of the
    /// codes listed on this interface.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The verification code is a one-time approval token, not a second factor.</b> That is a
    /// measured fact, not an interpretation: the legacy provider compared the submitted code
    /// against the composed value <c>portalId &amp; "-" &amp; userId</c> at
    /// <c>AspNetMembershipProvider.vb:L1468</c> and, on a match, set the account approved at L1470
    /// and persisted that change at L1473. An account therefore verified once and stayed verified.
    /// </para>
    /// <para>
    /// <b>The code's composition must be preserved exactly.</b> It is not stored anywhere - no
    /// column for it appears in the 88-script schema chain - it is recomputed from the tenant and
    /// account identifiers at the moment it is checked. Any account awaiting verification at the
    /// moment of migration therefore still holds a code that this implementation can accept, and
    /// changing the composition would strand every one of them.
    /// </para>
    /// <para>
    /// <b>Which accounts the flow applies to.</b> The legacy guard at
    /// <c>AspNetMembershipProvider.vb:L1466</c> entered the approval branch only when the account
    /// was unapproved <em>and</em> was not a super user, so super users bypassed verification
    /// entirely. That exemption is preserved. Whether an unapproved account is asked for a code or
    /// refused outright depends on the tenant's registration mode: the screen consulted it at
    /// <c>Login.ascx.vb:L170</c> and produced its "enter a code" prompt only for verified
    /// registration, answering "not authorised" otherwise, which is the distinction between
    /// <c>VERIFICATION_REQUIRED</c> and <c>ACCOUNT_NOT_APPROVED</c> here.
    /// </para>
    /// <para>
    /// <b>Required decision order.</b> Resolve the account within the tenant; verify the password;
    /// then evaluate lockout and approval; then, when a correct code accompanied a correct
    /// password on an unapproved account, approve the account and persist that; then issue the
    /// token pair. Absent and blank verification codes are the same submission, as the request
    /// contract records, so both lead to <c>VERIFICATION_REQUIRED</c> and only a supplied,
    /// non-matching code leads to <c>VERIFICATION_CODE_INVALID</c> - which reproduces the legacy
    /// branch at <c>Login.ascx.vb:L177-L181</c> exactly.
    /// </para>
    /// </remarks>
    // MIGRATION: verifying the password BEFORE disclosing an account's approval state reverses the
    // legacy order and is a deliberate divergence. The legacy provider graded lockout and approval
    // first and then skipped the credential check altogether for either state
    // (AspNetMembershipProvider.vb:L1481), so a caller who supplied nothing but a valid account
    // name learned that the account existed and was awaiting verification. A legitimate user's
    // experience is unchanged, because a legitimate user supplies the correct password and reaches
    // the same outcome by either order; only an enumerating caller is affected. The legacy
    // behaviour is not preserved because preserving it would mean shipping the enumeration leak
    // into new code.
    //
    // MIGRATION: approving an account only AFTER the password has been verified is the second
    // deliberate divergence, and it closes a genuine legacy defect rather than a stylistic one.
    // Because the legacy provider approved at L1470 before it checked any credential at L1481, a
    // caller who could guess the composed code - and it is composed from two integers, so it is
    // guessable - could permanently approve somebody else's pending account without ever holding
    // its password. Requiring the password first makes the guessable code insufficient on its own.
    // Rate limiting on the sign-in endpoint is the named compensating control for the guessing
    // itself, and the code's composition is preserved for the parity reason given above.
    //
    // MIGRATION: a locked-out account is refused here, and the legacy screen's own flag said
    // otherwise. Login.ascx.vb:L187 computed authenticated as "status is not LOGIN_FAILURE", which
    // is TRUE for the locked-out status; what actually stopped the sign-in was the provider
    // clearing its user object at AspNetMembershipProvider.vb:L1505-L1507, leaving the screen with
    // an authenticated flag and no user. Depending on a null to contradict a boolean is not a
    // behaviour worth reproducing. ACCOUNT_LOCKED_OUT is returned as a failure, so the refusal is
    // stated once and cannot be read two ways. The automatic-unlock window is preserved: the
    // legacy check at L1454-L1461 unlocked the account and continued rather than refusing, and an
    // implementation must do the same before concluding that an account is locked.
    //
    // MIGRATION: the sign-in audit entry survives the change of mechanism. ValidateUser wrote an
    // event-log row for the locked-out and outright-failure statuses only, at
    // UserController.vb:L1138-L1141, passing the tenant, the submitted name, the tenant name and
    // the caller's address. The storage provider behind it is excluded from this migration, so the
    // entry becomes a structured log event carrying the same facts and the outcome code - and
    // never the submitted password or verification code.
    Task<Result<LoginResponse>> LoginAsync(
        int portalId,
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the caller of the current request to itself, resolving the identity the caller's
    /// own access token asserts.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token observed while the account, its roles and its permission keys are read.
    /// </param>
    /// <returns>
    /// A successful result carrying the caller's own description, or a successful result whose
    /// value is <see langword="null"/> when the account the token names no longer exists. Absent
    /// is not a failure, so this member documents no refusal code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>It takes no identifier, and that is the contract.</b> The subject is the caller, read
    /// from the request's own claims through <see cref="ICurrentUser"/>. Accepting an account or
    /// tenant identifier would turn a member that describes the caller into one that describes
    /// anybody, and reading another account is what the account-administration contract's own read
    /// is for, behind its own authorisation.
    /// </para>
    /// <para>
    /// <b>Why it can answer "absent" at all.</b> A bearer token asserts its own validity and is
    /// not revoked by anything the server does, so an account may be deleted while a token naming
    /// it is still within its lifetime. The nullable payload is how that window is reported: the
    /// caller is authenticated and the subject is gone, which is a legitimate answer rather than a
    /// fault.
    /// </para>
    /// <para>
    /// <b>Claims are not trusted as the whole answer.</b> The roles and permission keys are read
    /// from the store rather than copied out of the token, because a token minted before a role
    /// was withdrawn still asserts it; the token establishes <em>who</em> is asking, and the store
    /// establishes what they may now do. This member is therefore a read and not a projection of
    /// the token.
    /// </para>
    /// </remarks>
    // MIGRATION: this member is net-new and has no legacy predecessor to transliterate. The legacy
    // application answered the same question without a call, by reading the ambient per-request
    // UserInfo that its page base classes exposed; the target has no ambient request state in this
    // layer, so the question becomes an explicit read. The projection it fills, CurrentUserDto,
    // carries the tenant name alongside the account so that a client can render its own header
    // without a second request.
    Task<Result<CurrentUserDto?>> GetCurrentUserAsync(
        CancellationToken cancellationToken = default);
}
