namespace DnnMigration.Application.Dtos.Auth;

// MIGRATION: this contract replaces the legacy sign-in outcome wholesale rather than translating it.
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L187 validated credentials and
// then reported the outcome through a `ByRef loginStatus` argument while the *session* was
// established as a side effect, by writing an encrypted forms-authentication cookie. Nothing was
// returned to the caller, because there was no caller: the page wrote a cookie and redirected. Here
// the session is the response - a short-lived bearer token the client sends on every subsequent
// request - so the outcome is data rather than an ambient side effect, and the status enumeration
// travels as a failure reason on Result<LoginResponse> instead of through an out-parameter.
//
// MIGRATION: `FormsAuthentication.SignOut` has no counterpart. Access tokens are stateless and
// cannot be recalled, so sign-out revokes the refresh token server-side and discards the access
// token client-side; the access token remains technically valid until it expires, which is precisely
// why ExpiresAtUtc below is short-lived and is published rather than left implicit.
//
// MIGRATION: no password, password hash, password question or password answer appears on this type,
// and none may be added. The legacy provider was registered with reversible encryption and
// `enablePasswordRetrieval="true"` (Website/release.config:L236-L246), so a password could be read
// back out of the store; hashes are one-way and retrieval is deliberately not carried forward.

/// <summary>
/// The successful outcome of a sign-in or a token refresh: the bearer credentials the client uses
/// from that point on, together with a snapshot of who the caller is.
/// </summary>
/// <remarks>
/// <para>
/// Two tokens are issued with deliberately different lifetimes. The access token is short-lived and
/// is presented on every request; the refresh token is longer-lived, is presented only to the
/// refresh endpoint, and is rotated on each use so that a captured refresh token is single-use. Both
/// expiry instants are published in UTC so a client can schedule a refresh instead of discovering
/// expiry through a rejected request.
/// </para>
/// <para>
/// <see cref="User"/> is a convenience snapshot taken at issue time, and it is a snapshot rather
/// than a live view: a role granted after the token was issued does not appear here, nor in the
/// token's claims, until the client refreshes. Server-side authorisation always re-evaluates against
/// stored state, so a stale snapshot can never widen access - it can only make the client render an
/// affordance that the API then refuses, which is the correct failure direction.
/// </para>
/// </remarks>
public sealed class LoginResponse
{
    /// <summary>The signed bearer token presented in the <c>Authorization</c> header as <c>Bearer &lt;token&gt;</c>.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>The token scheme the client must use. Always <c>Bearer</c>; published so a client need not hard-code it.</summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>The access token's remaining lifetime in whole seconds at the moment it was issued.</summary>
    public int ExpiresIn { get; set; }

    /// <summary>The instant, in UTC, at which the access token stops being accepted.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The single-use token presented to the refresh endpoint to obtain a new pair.
    /// </summary>
    /// <remarks>
    /// Rotated on every successful refresh: the presented token is retired and a new one is issued,
    /// so replaying a captured refresh token fails and the replay is detectable.
    /// </remarks>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>The instant, in UTC, after which the refresh token can no longer be redeemed and the caller must sign in again.</summary>
    public DateTime RefreshTokenExpiresAtUtc { get; set; }

    /// <summary>Who the caller is, as of the instant the tokens were issued.</summary>
    public CurrentUserDto User { get; set; } = new();
}
