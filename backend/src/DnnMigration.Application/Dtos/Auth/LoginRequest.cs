using System.Text.Json.Serialization;

namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// Request body of <c>POST /api/v1/auth/login</c>: the credentials a caller submits in exchange for a token
/// pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type carries a credential in clear text and must never be logged.</b> Nothing on it may reach a
/// log sink, an audit record, a trace, a metrics dimension or an exception message.
/// </para>
/// <para>
/// Validation is declared separately, in <c>Application/Validation/LoginRequestValidator.cs</c>. This type
/// enforces no rule whatsoever: it does not trim, clamp, coerce, reject or re-shape any value beyond the
/// empty-string initialisers below, so what the caller sent is exactly what the validator and the service
/// observe.
/// </para>
/// </remarks>
public sealed class LoginRequest
{
    /// <summary>
    /// Gets or sets the tenant the credential is being presented to. Populated by the Api layer, never by
    /// the caller.
    /// </summary>
    /// <remarks>
    /// <b>Nullable, and never defaulted.</b> Absence is a null reference, never a numeric stand-in:
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>, so both zero and minus one are real
    /// tenants and neither can be borrowed to mean "unspecified". A sign-in whose tenant is absent is
    /// refused rather than attributed to a default.
    /// </remarks>
    [JsonIgnore]
    public int? PortalId { get; set; }

    /// <summary>Gets or sets the sign-in name supplied by the caller.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the clear-text password supplied by the caller.</summary>
    /// <remarks>
    /// A maximum length IS enforced, and it is net-new rather than ported. The legacy table capped its
    /// password column at 20 characters, but that was a storage limit of a schema which no longer holds the
    /// credential at all, so it is not the source of the bound and is not reproduced.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional verification code, which only tenants whose registration mode is verified
    /// registration ever require.
    /// </summary>
    /// <remarks>
    /// <b>The sign-in service is the only consumer.</b> <c>IAuthService.LoginAsync</c> reads this member
    /// and nothing else does, and it is that contract - not this one - that declares the two outcomes the
    /// code produces: <c>auth.verification_required</c> when the account awaits verification and no code
    /// accompanied the credential, and <c>auth.verification_code_invalid</c> when a code was supplied and
    /// did not match.
    /// </remarks>
    public string? VerificationCode { get; set; }

    // Deliberate divergences: inputs the legacy sign-in path had that this contract does NOT carry.
    // Recorded here under Rule T5 so that every absence is a documented decision rather than an omission.

    // MIGRATION: The hard-coded "DNN" authentication-type argument is dropped.

    // Neither the tenant identity nor the caller's network address is accepted from the client, even though
    // Login.ascx.vb:L164 passed all three of those values into the legacy call as its first, sixth and
    // seventh arguments -- the tenant key, the tenant display name and the network address.

    // No "keep me signed in" flag is carried forward.
}
