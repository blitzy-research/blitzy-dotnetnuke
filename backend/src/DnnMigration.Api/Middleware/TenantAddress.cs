using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Builds the single address string every stage of the pipeline uses when it asks which tenant a request
/// belongs to, and reads the path portion back out of a matched alias.
/// </summary>
/// <remarks>
/// <para>
/// WHY ONE PLACE AND NOT THREE. Three components need the tenant for the same request - the stage that
/// rewrites the path base, the stage that resolves the tenant for the rest of the pipeline, and the
/// portal-administration authorisation handler - and resolution is MEMOISED per request, so whichever of
/// them runs first supplies the address that all three then observe. That memoisation is a feature: it is
/// what lets authorisation insist on a resolved tenant without depending on having run after the resolution
/// stage. But it also means a component that computed the address differently would not be corrected by the
/// others; it would simply be ignored, silently, and only on the requests where the difference mattered.
/// Computing it in one member removes the possibility rather than documenting it.
/// </para>
/// <para>
/// MIGRATION: this is the target's counterpart to <c>Globals.GetDomainName(Request)</c>
/// (<c>Library/Components/Shared/Globals.vb:L551</c>, implemented from L563). That member walked the request
/// URL's segments building a value to compare against the alias column, stopping when it reached one of the
/// framework directories - <c>admin</c>, <c>controls</c>, <c>desktopmodules</c>, <c>mobilemodules</c>,
/// <c>premiummodules</c>, <c>providers</c> - and excluding any segment that named a handler file. Its result
/// was therefore <c>www.domain.com</c> for a request at the root and <c>www.domain.com/directory</c> for one
/// beneath a sub-directory, which is exactly the distinction a child portal is addressed by. This member
/// carries the whole path instead of stopping at a recognised directory, and the candidate chain the holder
/// builds from it is what performs the equivalent narrowing: the legacy walk guessed where the alias ended
/// from a fixed list of directory names, whereas the chain asks the store which prefixes are actually
/// configured. That is a strictly better answer to the same question, and it needs no list to be kept in
/// step with the routes.
/// </para>
/// </remarks>
internal static class TenantAddress
{
    /// <summary>Bytes of the address digest that are recorded, giving a 16-character property.</summary>
    private const int FingerprintByteLength = 8;

    /// <summary>Recorded in place of a fingerprint that cannot be produced.</summary>
    private const string AbsentHostMarker = "(absent)";

    /// <summary>
    /// Builds the address a tenant is resolved from, for the current request.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>
    /// The host - host and port together, exactly as sent - followed by the request's full path, or the bare
    /// host when the request is at the root. Never <see langword="null"/>, and never carries a trailing
    /// separator.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The path is read as <c>PathBase</c> followed by <c>Path</c>, and that is not incidental. The stage
    /// that rewrites the path base MOVES segments from the second to the first, so either one alone changes
    /// as the request travels the pipeline while their concatenation does not. Reading both makes this
    /// member's answer invariant, so a later caller cannot be handed a shorter address than an earlier one.
    /// </para>
    /// <para>
    /// The host is not lower-cased, no scheme is prepended and no trailing separator is added: the alias
    /// column stores host and port together in exactly this shape, and under the default case-insensitive
    /// collation host names compare case-insensitively as host names should. Imposing a case here would
    /// only hide a collation an operator had deliberately chosen.
    /// </para>
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
        // exact shape the legacy signup screen composed and stored. The trailing separator a caller may
        // have sent is dropped, because an alias is never stored with one and "host/child/" and
        // "host/child" address the same tenant.
        return string.Concat(host, path.TrimEnd('/'));
    }

    /// <summary>
    /// Produces a short, stable fingerprint of a whole address, for correlating repeated failures without
    /// retaining the address.
    /// </summary>
    /// <param name="address">The address <see cref="Of(HttpContext)"/> produced.</param>
    /// <returns>A lower-case hexadecimal digest prefix, or <c>(absent)</c> for a blank address.</returns>
    /// <remarks>
    /// <para>
    /// This is what replaces the raw address in a diagnostic entry. Two failures against the same full
    /// address share a fingerprint, so an operator can still see that one address is failing repeatedly
    /// rather than many addresses failing once - which is the question the raw value was there to answer -
    /// while the text itself, including the request path, is not retained anywhere.
    /// </para>
    /// <para>
    /// A digest PREFIX rather than the whole hash, because the value is a correlation key and not a security
    /// claim: nothing authenticates or authorises on it, so 64 bits of it is ample to distinguish the
    /// addresses one installation sees, and a shorter property keeps the log line readable. The comparison
    /// is case-insensitive at the source - host names are matched case-insensitively - so the value is
    /// lower-cased before hashing, and a host differing only in case fingerprints identically.
    /// </para>
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

    /// <summary>
    /// Reads the path portion out of a matched alias.
    /// </summary>
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
