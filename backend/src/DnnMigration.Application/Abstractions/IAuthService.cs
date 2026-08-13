using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

// MIGRATION: the fixed authentication-mechanism argument is dropped.

// MIGRATION: the sign-in ticket is not reproduced. SetAuthCookie at UserController.vb:L919 issued
// MIGRATION: the Forms-authentication ticket, and the CreatePersistentCookie argument at MIGRATION:
// UserController.vb:L991 and L1024 governed its lifetime.

// The credential store changes from reversible to one-way, and that is a security fix rather than a
// refactor.

// MIGRATION: credential retrieval is not carried forward at all. GetPassword at UserController.vb:L433
// returned the caller's own credential in clear text, and the recovery MIGRATION: screen invoked it at
// SendPassword.ascx.vb:L198-L200 whenever retrieval was enabled.

// MIGRATION: the credential question-and-answer pair is omitted.

// MIGRATION: the authenticated-event model is not reproduced. UserUserControlBase.vb:L59-L65 declared seven
// user-lifecycle events, and the sign-in screen raised one at Login.ascx.vb:L193 by MIGRATION: constructing
// UserAuthenticatedEventArgs and calling OnUserAuthenticated.

/// <summary>
/// The authentication contract: it decides whether a submitted credential is accepted, mints and rotates
/// the caller's session, retires a session on request, and describes the authenticated caller to itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this contract owns, and what it does not.</b> It owns the sign-in <em>use case</em>.
/// </para>
/// <para>
/// <b>Outcome model.</b> Every member returns a result. An <em>expected</em> refusal - and a wrong
/// credential is the most expected refusal in the system - is a failed result carrying a stable code, never
/// an exception: throwing would surface a rejected sign-in as a server fault instead of an authentication
/// failure.
/// </para>
/// </remarks>
public interface IAuthService
{
    /// <summary>
    /// Verifies a submitted credential against one tenant's accounts and, when it is accepted, mints the
    /// caller's session.
    /// </summary>
    /// <param name="request">
    /// The submission: the account name, the credential, the tenant it is being presented to, and the
    /// optional account-verification code.
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed while accounts, roles and the session store are read and written.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the token pair and the caller's own
    /// description - optionally with the <c>auth.insecure_admin_password</c> or
    /// <c>auth.insecure_host_password</c> advisory attached - or a failed result carrying
    /// <c>auth.request_invalid</c> when the submission is unusable, <c>auth.invalid_credentials</c> for
    /// every rejected credential, <c>auth.locked_out</c> for a locked account when the caller is entitled
    /// to that detail, one of <c>auth.verification_required</c>, <c>auth.verification_code_invalid</c> or
    /// <c>auth.account_not_approved</c> when a proved credential met an account the tenant has not yet
    /// admitted, <c>auth.approval_store_unavailable</c> when a correct code could not be recorded, or
    /// <c>TOKEN_STORE_UNAVAILABLE</c> propagated unchanged when the credential was accepted and the session
    /// could not be recorded.
    /// </returns>
    /// <remarks>
    /// The separation is load-bearing. <see cref="IPasswordHasher"/> remains BCrypt-only and permanently
    /// contains no reversible capability; <see cref="ILegacyCredentialVerifier"/> is the bounded bridge and
    /// cannot mint or persist a credential.
    /// </remarks>
    // MIGRATION: the image-based human-verification challenge is dropped, and it is a security relevant
    // reduction rather than a cosmetic one.

    // MIGRATION: verifying the credential BEFORE disclosing an account's approval state reverses the legacy
    // order and is a deliberate divergence.

    // MIGRATION: approving an account only AFTER the credential has been verified is the second deliberate
    // divergence, and it closes a genuine legacy defect.

    // The three approval outcomes keep their legacy identities but not their legacy wording.

    // The two insecure-default statuses become advisories on a successful result rather than refusals, and
    // the independent five-member validity status is treated the same way.
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a valid refresh token for a new token pair, retiring the presented token.</summary>
    /// <param name="request">The refresh token to redeem.</param>
    /// <param name="cancellationToken">Token observed while the session store is read and written.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the new pair, or a failed result
    /// carrying <c>auth.invalid_refresh_token</c> when the presented token is unknown, expired, already
    /// redeemed or revoked, or when the account it names no longer exists.
    /// </returns>
    /// <remarks>
    /// <b>The caller's description is re-read, not copied.</b> Roles and permission keys are resolved from
    /// stored state during the exchange rather than carried over from the retired token, which is how a
    /// role granted - or withdrawn - since the previous exchange becomes effective. This is the only
    /// mechanism by which an entitlement change reaches a signed-in caller before its access token expires.
    /// </remarks>
    Task<Result<LoginResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retires a session by revoking the refresh token that sustains it.</summary>
    /// <param name="request">The refresh token to revoke.</param>
    /// <param name="cancellationToken">Token observed while the session store is written.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the token was already unknown or
    /// already revoked - sign-out is idempotent, and reporting "no such token" would let a caller probe
    /// which tokens exist. <c>TOKEN_STORE_UNAVAILABLE</c> propagates unchanged when the revocation could
    /// not be recorded, because a sign-out that silently failed to revoke anything must not be reported as
    /// a sign-out.
    /// </returns>
    /// <remarks>
    /// <b>Read the honest semantics before relying on this member.</b> It revokes the presented refresh
    /// token, which breaks the rotation chain and prevents any further session from being minted from it.
    /// </remarks>
    // FormsAuthentication.SignOut at PortalSecurity.vb:L77 has no stateless counterpart.
    Task<Result> LogoutAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates the blocking credential and profile work for one authenticated account from current
    /// stored state.
    /// </summary>
    /// <param name="portalId">Tenant the session is scoped to.</param>
    /// <param name="userId">Authenticated account identifier.</param>
    /// <param name="cancellationToken">Token observed while account policy and profile state are read.</param>
    /// <returns>
    /// A successful outcome carrying the current remediation state, or a failed outcome when the tenant,
    /// membership or required policy state cannot be resolved.
    /// </returns>
    Task<Result<AuthenticationRemediationState>> EvaluateRemediationAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the caller of the current request to itself, resolving the identity the caller's own
    /// access token asserts.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token observed while the account, its roles and its permission keys are read.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the caller's own description, or a
    /// successful result whose value is <see langword="null"/> when the caller is not authenticated or the
    /// account its token names no longer exists.
    /// </returns>
    /// <remarks>
    /// <b>Claims are not trusted as the whole answer.</b> Roles and permission keys are read from the store
    /// rather than copied out of the token, because a token minted before a role was withdrawn still
    /// asserts it: the token establishes <em>who</em> is asking and the store establishes what they may now
    /// do. This member is therefore a read, not a projection of the token.
    /// </remarks>
    // The legacy answer to this question was an ambient read with no call and no failure MIGRATION: mode.
    Task<Result<CurrentUserDto?>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
