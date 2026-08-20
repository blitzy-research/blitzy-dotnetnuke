using System.Globalization;
using System.Security.Claims;
using DnnMigration.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// The single place the authorisation handlers read an identifier out of a principal or a matched route.
/// </summary>
/// <remarks>
/// WHY ONE HELPER RATHER THAN A COPY PER HANDLER. Three handlers need the same three readings - the
/// caller's account key, the tenant the token was minted for, and a tenant or resource key named by the
/// route - and every one of those readings has a rule that is wrong by default. The account key may arrive
/// under either of two claim names depending on host configuration.
/// </remarks>
internal static class AuthorizationClaims
{
    /// <summary>The route value naming the tenant a request is about.</summary>
    public const string PortalRouteKey = "portalId";

    /// <summary>
    /// The registered claim name carrying a token's subject, used when the host has not mapped it onto the
    /// framework-standard name-identifier claim.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from a token-handling library so that no authorisation handler needs a
    /// dependency on one.
    /// </remarks>
    private const string JwtRegisteredSubjectClaim = "sub";

    /// <summary>Reads the caller's account key from their claims.</summary>
    /// <param name="user">The caller.</param>
    /// <returns>The account key, or <see langword="null"/> when no usable claim is present.</returns>
    /// <remarks>
    /// The framework-standard name-identifier claim is preferred, with the registered JWT subject claim as
    /// a fallback, because whether the inbound token's <c>sub</c> claim has been mapped to the former
    /// depends on host configuration a handler should not have to know about. Nothing else is consulted,
    /// and a value that is not an integer is treated as absent rather than coerced.
    /// </remarks>
    public static int? ReadUserId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue(JwtRegisteredSubjectClaim);

        return ParseIdentifier(raw);
    }

    /// <summary>Reads the tenant the caller's token was minted for.</summary>
    /// <param name="user">The caller.</param>
    /// <returns>The tenant, or <see langword="null"/> when the claim is absent or unusable.</returns>
    /// <remarks>
    /// This is the tenant the credential was presented to, and binding an administrative decision to it is
    /// what stops a token issued in one tenant from carrying authority into another. It is deliberately a
    /// different question from which tenant the request arrived at and from which tenant the route names; a
    /// handler that treats the three as interchangeable has no tenant binding at all.
    /// </remarks>
    public static int? ReadTokenPortalId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return ParseIdentifier(user.FindFirstValue(DnnClaimTypes.PortalId));
    }

    /// <summary>Reads one route value as an identifier.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="key">The route value name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or unparseable.</returns>
    public static int? ReadRouteInt(HttpContext httpContext, string key)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.RouteValues.TryGetValue(key, out object? raw))
        {
            return null;
        }

        return ParseIdentifier(raw as string ?? raw?.ToString());
    }

    /// <summary>Parses an identifier the invariant way, treating anything unusable as absent.</summary>
    /// <param name="raw">The text to parse.</param>
    /// <returns>The identifier, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Invariant culture because the value was written by this system in that culture, and a token or a
    /// route must mean the same thing wherever it is read. An unparseable value yields null rather than a
    /// substituted default: a malformed identifier is not identifier zero, and treating it as one would aim
    /// an authorisation decision at whatever row happens to occupy that key.
    /// </remarks>
    private static int? ParseIdentifier(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
}
