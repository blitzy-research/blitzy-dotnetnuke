using System.Text.Json.Serialization;

namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// Request body of <c>POST /api/v1/auth/refresh</c>: the payload a caller submits to
/// trade a refresh token that is still good for a newly issued access token.
/// </summary>
/// <remarks>
/// <para>
/// Response type. A successful exchange returns <c>LoginResponse</c> - the same envelope
/// <c>POST /api/v1/auth/login</c> returns - because both endpoints hand back a fresh set
/// of tokens. This endpoint has no response type of its own, and no alias for one is
/// declared or referenced anywhere in the solution.
/// </para>
/// <para>
/// What this type deliberately does not carry. The single property below is the whole of
/// the request. The bookkeeping that lets a refresh token be redeemed exactly once, and
/// that supersedes a redeemed token with its replacement, is server-side state owned by
/// <c>Infrastructure/Security/RefreshTokenStore.cs</c>; a caller can neither read it nor
/// supply it. There is therefore no counter here, no token identifier, no device
/// identity, no caller-supplied time limit and no access token, because a limit the
/// caller states is a limit the caller can forge. The configured limits live on
/// <c>Application/Options/JwtOptions.cs</c> and are enforced by the store together with
/// <c>Infrastructure/Security/JwtTokenService.cs</c>. No identity field appears either:
/// the refresh token is itself the credential, so the server derives the caller from the
/// token rather than from anything the caller asserts beside it.
/// </para>
/// <para>
/// There is deliberately no <c>RefreshTokenRequestValidator</c>, and this type carries no
/// validation of its own. Whether a refresh token is present, well formed, unredeemed,
/// unrevoked and still inside its permitted window is settled by <c>RefreshTokenStore</c>
/// and <c>JwtTokenService</c>, which have to consult server-side state whatever a check
/// on this object might already have concluded. A presence check here would add nothing
/// and would wrongly imply that an empty token is the only way the exchange can fail. A
/// rejected exchange surfaces as an RFC 7807 problem-details response produced at the API
/// edge, never as a status field on a body.
/// </para>
/// <para>
/// Shape. An inert carrier, model-bound from a JSON body: one settable auto-property, the
/// implicit public parameterless constructor, and no behaviour, no I/O and nothing to
/// configure. The property name matches the wire contract exactly, so the client-side
/// model mirrors this type one for one.
/// </para>
/// </remarks>
public sealed class RefreshTokenRequest
{
    // MIGRATION: Net-new type with no legacy predecessor, so nobody should go looking for
    //            one. DotNetNuke 4.9.0 authenticated with a sliding forms cookie
    //            configured at Website/release.config:L215 and protected by the
    //            machineKey element at Website/release.config:L89-L93, so a browser never
    //            held a token, never saw when its own session would elapse, and never
    //            called a renewal endpoint. The legacy sign-in handler at
    //            Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197
    //            checks the submitted credentials and raises an event; there is nothing in
    //            it for this request to have been translated from.

    // MIGRATION: FormsAuthentication.SignOut has no counterpart in a stateless
    //            bearer-token design, so ending a session becomes a token left to elapse
    //            plus a client-side discard. That is why the logout endpoint accepts no
    //            body at all, and why no revoke flag is added to this type to compensate
    //            for its absence.

    // MIGRATION: The state that makes each refresh token redeemable exactly once is held
    //            server-side on purpose. Exposing any part of it here would hand the
    //            caller control of replay detection, and accepting a time limit from the
    //            caller would let the caller choose how long its own token stays usable.

    // MIGRATION: The legacy encoded-absent contract used the empty string, not a null
    //            reference, as the absent value for a string: the sentinel property at
    //            Library/Components/Shared/Null.vb:L71-L75 returns the empty string, and
    //            the absence test at Null.vb:L226 and L235 reports an empty string and a
    //            missing object alike. The default below preserves that equivalence
    //            instead of quietly normalising one form into the other.

    /// <summary>
    /// The refresh token previously issued to this caller, presented here so it can be
    /// exchanged for a newly issued access token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <c>string.Empty</c> rather than <see langword="null"/>, which keeps the
    /// property non-null under the solution-wide nullable context instead of leaning on
    /// the bounded CS8618 suppression in <c>backend/Directory.Build.props</c>.
    /// </para>
    /// <para>
    /// An empty value and an omitted value mean the same thing to
    /// <c>RefreshTokenStore</c>: neither can be redeemed, so both are refused. That
    /// matches the legacy contract described above, and this property keeps the two
    /// distinguishable on the wire by declining to convert either into the other. The
    /// value is carried through exactly as the caller sent it, and the store decides.
    /// </para>
    /// </remarks>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bounded server-observed client binding used for concurrent redemption grace.
    /// </summary>
    /// <remarks>
    /// Excluded from JSON: the API computes it from trusted connection metadata after forwarded headers
    /// have been processed. A caller cannot choose the value carried into the store.
    /// </remarks>
    [JsonIgnore]
    public string ClientBinding { get; set; } = string.Empty;
}
