using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;

// Nothing here is ported. The legacy application shipped no token service and no abstraction resembling
// one, so an implementer must not search the VB.NET trees for a predecessor to transliterate - there is
// none.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Mints, rotates and revokes the bearer tokens that carry an authenticated caller's identity, exposing
/// nothing about how those tokens are built, signed, stored or parsed.
/// </summary>
/// <remarks>
/// <para>
/// What the access token asserts. The emitted token contains only the subject identifier, portal
/// identifier, token identifier and the standard issuer, audience and time claims.
/// </para>
/// <para>
/// Why no read-or-validate member exists. Inspecting or validating an access token is deliberately absent
/// from this contract.
/// </para>
/// </remarks>
public interface ITokenService
{
    /// <summary>
    /// Issues a fresh access-token and refresh-token pair for a caller whose credential has already been
    /// accepted.
    /// </summary>
    /// <remarks>
    /// Called by the sign-in service at the end of a successful sign-in. The access token carries only the
    /// account, tenant, token identifier and time/envelope claims needed to authenticate a request; mutable
    /// authority is re-read by server-side policies.
    /// </remarks>
    /// <param name="userId">Identifier of the authenticated caller, asserted verbatim as the token's subject.</param>
    /// <param name="portalId">
    /// Identifier of the tenant the caller signed in to, resolved upstream by the portal-alias middleware
    /// and asserted verbatim.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the refresh-token write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the access token,
    /// the refresh token and the access token's absolute expiry.
    /// </returns>
    Task<Result<LoginResponse>> IssueTokensAsync(
        int userId,
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a brand-new pair, invalidating the token presented.</summary>
    /// <remarks>
    /// The replacement access token repeats only the minimal account and tenant identity recorded by the
    /// refresh-token store. Roles, permission keys, names and host authority are deliberately absent from
    /// the token and are re-read by the server or by the explicit current-user endpoint.
    /// </remarks>
    /// <param name="refreshToken">The refresh-token value the caller presents, exactly as it was issued.</param>
    /// <param name="clientBinding">A bounded server-observed client fingerprint.</param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store reads and writes if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose non-null value carries the new access
    /// token, the new refresh token and the new access token's absolute expiry.
    /// </returns>
    Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a single refresh token so that no further access token can be minted from it. This is the
    /// only server-side effect a logout can have.
    /// </summary>
    /// <remarks>
    /// Backs <c>POST /api/v1/auth/logout</c>. It ends the caller's ability to <em>continue</em> a session;
    /// it cannot end the session already in progress, because the access token the caller holds remains
    /// valid until its own expiry and no server action retracts it.
    /// </remarks>
    /// <param name="refreshToken">The refresh-token value to revoke, exactly as it was issued.</param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once the token is known not to be usable -
    /// including when it was already unusable, or was never issued at all.
    /// </returns>
    Task<Result> RevokeRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every refresh token currently held by one user, ending that user's ability to continue any
    /// session.
    /// </summary>
    /// <remarks>
    /// <b>Its callers are named, and they are obligations rather than options.</b> Every service operation
    /// that ends an account's right to sign in, or that changes the credential by which it does so, must
    /// call this member as part of the same request: a self-service credential change, an administrative
    /// reset, the withdrawal of an approval and the deletion of an account.
    /// </remarks>
    /// <param name="userId">Identifier of the user whose refresh tokens are to be revoked.</param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once no refresh token belonging to this user is
    /// exchangeable - including when the user held none to begin with.
    /// </returns>
    Task<Result> RevokeAllRefreshTokensAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>ERASES the session records held for one account, or for one account within one tenant.</summary>
    /// <remarks>
    /// PRIV-02. <b>Revocation is not deletion, and only the first existed on this contract.</b> <see
    /// cref="RevokeAllRefreshTokensAsync"/> stamps every family so that it can no longer be redeemed while
    /// leaving the record in place - which is exactly right for a sign-out, because a stamped record is
    /// what makes a later replay of that family recognisable.
    /// </remarks>
    /// <param name="userId">Identifier of the account whose session records are erased.</param>
    /// <param name="portalId">
    /// Tenant to confine the erasure to, or <see langword="null"/> to erase the account's records in every
    /// tenant.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once no session record for that scope remains -
    /// including when there was none to begin with, because erasure is idempotent.
    /// </returns>
    Task<Result> PurgeAccountSessionRecordsAsync(
        int userId,
        int? portalId = null,
        CancellationToken cancellationToken = default);

    /// <summary>ERASES every session record held for one tenant, whichever account holds it.</summary>
    /// <remarks>
    /// PRIV-02. The tenant counterpart of <see cref="PurgeAccountSessionRecordsAsync"/>, and it is not
    /// redundant with it: a tenant's records outlive its members.
    /// </remarks>
    /// <param name="portalId">Identifier of the tenant whose session records are erased.</param>
    /// <param name="cancellationToken">
    /// Token used to abandon the store write if the caller's request is abandoned first.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once no session record for that tenant remains.
    /// </returns>
    Task<Result> PurgePortalSessionRecordsAsync(
        int portalId,
        CancellationToken cancellationToken = default);
}

/// <summary>The claim vocabulary shared by whatever mints an access token and whatever reads one.</summary>
/// <remarks>
/// <para>
/// The registered names are spelled here rather than taken from a token library so that the wire format is
/// fixed by this contract and does not shift when a library renames its own constants between major
/// versions - which the underlying library has done.
/// </para>
/// <para>
/// Mutable roles, permissions, names and host flags are deliberately absent. Server-side authorization
/// re-reads those facts from authoritative storage instead of trusting a token snapshot.
/// </para>
/// </remarks>
public static class DnnClaimTypes
{
    /// <summary>The tenant the token was issued for.</summary>
    public const string PortalId = "portal_id";

    /// <summary>The token's subject - the authenticated account's identifier.</summary>
    public const string Subject = "sub";

    /// <summary>A unique identifier for one particular token.</summary>
    public const string JwtId = "jti";
}
