using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using DnnMigration.Application.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Mints bearer tokens for the integration suite and attaches them to <see cref="HttpClient"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why tokens are minted rather than obtained by signing in.</strong> Most suites here need a
/// caller with a particular shape - a host account, a portal administrator, a plain member, a caller whose
/// portal claim disagrees with the route - and driving each of those through the sign-in endpoint would
/// make every test depend on the credential store, on approval state, and on the sign-in rate limit.
/// Minting the token directly removes those dependencies while still exercising the whole validation path:
/// the API verifies the signature, the issuer, the audience, the lifetime and the claim types exactly as it
/// would for a token it issued itself. The sign-in endpoint is still tested end to end, by
/// <c>AuthApiTests</c>, which is where that behaviour belongs.
/// </para>
/// <para>
/// <strong>Why it constructs the token the same way production does.</strong> This type uses the
/// <see cref="JwtSecurityToken"/> constructor followed by
/// <see cref="JwtSecurityTokenHandler.WriteToken(Microsoft.IdentityModel.Tokens.SecurityToken)"/>, which is precisely what
/// <c>Infrastructure/Security/JwtTokenService</c> does. That matters because the alternative
/// - building a <see cref="SecurityTokenDescriptor"/> and calling
/// <see cref="JwtSecurityTokenHandler.CreateToken(SecurityTokenDescriptor)"/> - applies the handler's
/// outbound claim-type map and would rename <see cref="ClaimTypes.Role"/> to <c>role</c> on the way out.
/// The API sets <c>MapInboundClaims = false</c>, so a renamed role claim would arrive under a type that
/// <c>RoleClaimType</c> does not match and every role-based policy would silently fail. Mirroring the
/// production construction keeps a minted token representative of a real one.
/// </para>
/// </remarks>
public static class AuthenticatedClientFactory
{
    /// <summary>Claim value written for a boolean claim, matching what the token service emits.</summary>
    private const string TrueValue = "true";

    /// <summary>Claim value written for a boolean claim, matching what the token service emits.</summary>
    private const string FalseValue = "false";

    /// <summary>Default lifetime of a minted token: long enough for a suite, short enough to be a token.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Mints a signed bearer token carrying the claim vocabulary the API reads.
    /// </summary>
    /// <param name="secret">The signing secret; must match the host's <c>Jwt:Secret</c>.</param>
    /// <param name="issuer">The issuer; must match the host's <c>Jwt:Issuer</c>.</param>
    /// <param name="audience">The audience; must match the host's <c>Jwt:Audience</c>.</param>
    /// <param name="userId">The account identifier written to the subject claim.</param>
    /// <param name="userName">The account name written to the unique-name claim.</param>
    /// <param name="portalId">The tenant written to the portal claim.</param>
    /// <param name="isSuperUser">Whether the caller is a host account.</param>
    /// <param name="roles">Role names, written under <see cref="ClaimTypes.Role"/>.</param>
    /// <param name="permissions">Permission keys, one claim each - never a delimited list.</param>
    /// <param name="lifetime">How long the token stays valid; defaults to thirty minutes.</param>
    /// <param name="notBefore">
    /// When the token becomes valid; defaults to one minute ago so that a token cannot be rejected by the
    /// clock skew of a host that started a moment later.
    /// </param>
    /// <returns>The compact serialised token.</returns>
    public static string CreateToken(
        string secret,
        string issuer,
        string audience,
        int userId,
        string userName,
        int portalId,
        bool isSuperUser = false,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        TimeSpan? lifetime = null,
        DateTime? notBefore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentNullException.ThrowIfNull(userName);

        DateTime issuedAt = notBefore ?? DateTime.UtcNow.AddMinutes(-1);
        DateTime expires = issuedAt.Add(lifetime ?? DefaultLifetime);

        var claims = new List<Claim>
        {
            new(DnnClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture)),
            new(DnnClaimTypes.JwtId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            new(DnnClaimTypes.UniqueName, userName),
            new(DnnClaimTypes.PortalId, portalId.ToString(CultureInfo.InvariantCulture)),
            new(DnnClaimTypes.SuperUser, isSuperUser ? TrueValue : FalseValue, ClaimValueTypes.Boolean),
        };

        foreach (string role in roles ?? Array.Empty<string>())
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (string permission in permissions ?? Array.Empty<string>())
        {
            claims.Add(new Claim(DnnClaimTypes.Permission, permission));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Attaches a bearer token to a client and returns the same client.</summary>
    /// <param name="client">The client to authenticate.</param>
    /// <param name="token">The compact serialised token.</param>
    /// <returns>The client, so calls can be chained.</returns>
    public static HttpClient Authenticate(HttpClient client, string token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
