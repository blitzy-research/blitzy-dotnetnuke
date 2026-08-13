using System.Text.Json.Serialization;

namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// Request body of <c>POST /api/v1/auth/refresh</c>: the payload a caller submits to trade a refresh token
/// that is still good for a newly issued access token.
/// </summary>
/// <remarks>
/// There is deliberately no <c>RefreshTokenRequestValidator</c>, and this type carries no validation of its
/// own. Whether a refresh token is present, well formed, unredeemed, unrevoked and still inside its
/// permitted window is settled by <c>RefreshTokenStore</c> and <c>JwtTokenService</c>, which have to
/// consult server-side state whatever a check on this object might already have concluded.
/// </remarks>
public sealed class RefreshTokenRequest
{
    // MIGRATION: Net-new type with no legacy predecessor, so nobody should go looking for one.

    // The state that makes each refresh token redeemable exactly once is held server-side on purpose.
    // Exposing any part of it here would hand the caller control of replay detection, and accepting a time
    // limit from the caller would let the caller choose how long its own token stays usable.

    /// <summary>
    /// The refresh token previously issued to this caller, presented here so it can be exchanged for a
    /// newly issued access token.
    /// </summary>
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
