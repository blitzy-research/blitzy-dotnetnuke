using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Builds the single address string every stage of the pipeline uses when it asks which tenant a request
/// belongs to, and reads the path portion back out of a matched alias.
/// </summary>
/// <remarks>
/// WHY ONE PLACE AND NOT THREE. Three components need the tenant for the same request - the stage that
/// rewrites the path base, the stage that resolves the tenant for the rest of the pipeline, and the
/// portal-administration authorisation handler - and resolution is MEMOISED per request, so whichever of
/// them runs first supplies the address that all three then observe.
/// </remarks>
internal static class TenantAddress
{
    /// <summary>Bytes of the address digest that are recorded, giving a 16-character property.</summary>
    private const int FingerprintByteLength = 8;

    /// <summary>Recorded in place of a fingerprint that cannot be produced.</summary>
    private const string AbsentHostMarker = "(absent)";

    /// <summary>Builds the address a tenant is resolved from, for the current request.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>
    /// The host - host and port together, exactly as sent - followed by the request's full path, or the
    /// bare host when the request is at the root.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The host is not lower-cased, no scheme is prepended and no trailing separator is added: the alias
    /// column stores host and port together in exactly this shape, and under the default case-insensitive
    /// collation host names compare case-insensitively as host names should. Imposing a case here would
    /// only hide a collation an operator had deliberately chosen.
    /// </remarks>
    public static string Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string host = context.Request.Host.Value ?? string.Empty;

        string path = string.Concat(
            context.Request.PathBase.Value ?? string.Empty,
            context.Request.Path.Value ?? string.Empty);

        if (path.Length == 0)
        {
            return host;
        }

        // A path always begins with the separator, so concatenating directly yields "host/segment" - the
        // exact shape the legacy signup screen composed and stored.
        return string.Concat(host, path.TrimEnd('/'));
    }

    /// <summary>
    /// Produces a short, stable fingerprint of a whole address, for correlating repeated failures without
    /// retaining the address.
    /// </summary>
    /// <param name="address">The address <see cref="Of(HttpContext)"/> produced.</param>
    /// <returns>A lower-case hexadecimal digest prefix, or <c>(absent)</c> for a blank address.</returns>
    /// <remarks>
    /// A digest PREFIX rather than the whole hash, because the value is a correlation key and not a
    /// security claim: nothing authenticates or authorises on it, so 64 bits of it is ample to distinguish
    /// the addresses one installation sees, and a shorter property keeps the log line readable.
    /// </remarks>
    public static string FingerprintOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return AbsentHostMarker;
        }

        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(address.ToLowerInvariant()));

        return Convert.ToHexString(digest.AsSpan(0, FingerprintByteLength)).ToLowerInvariant();
    }

    /// <summary>Reads the path portion out of a matched alias.</summary>
    /// <param name="httpAlias">The alias the request resolved to.</param>
    /// <returns>
    /// The alias's path portion as a path string - for example <c>/child</c> from
    /// <c>localhost:8080/child</c> - or <see cref="PathString.Empty"/> when the alias is a bare host.
    /// </returns>
    /// <remarks>
    /// A bare host is the ordinary case and yields an empty result rather than a fault, so a caller can use
    /// this member unconditionally. Nothing is unescaped: the alias column holds the value the operator
    /// typed and the request path holds what the caller sent, and comparing them as-is is what makes a
    /// child portal reachable at the address it was configured for.
    /// </remarks>
    public static PathString PathPortionOf(string? httpAlias)
    {
        if (string.IsNullOrWhiteSpace(httpAlias))
        {
            return PathString.Empty;
        }

        string trimmed = httpAlias.Trim().TrimEnd('/');

        int separator = trimmed.IndexOf('/', StringComparison.Ordinal);

        if (separator < 0 || separator == trimmed.Length - 1)
        {
            return PathString.Empty;
        }

        return new PathString(trimmed[separator..]);
    }
}
