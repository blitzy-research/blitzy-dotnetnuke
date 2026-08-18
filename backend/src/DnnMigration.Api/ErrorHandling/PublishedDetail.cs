using System.Text.RegularExpressions;

namespace DnnMigration.Api.ErrorHandling;

/// <summary>
/// Removes facts about the installation from text that is about to be published to a caller as the RFC 7807
/// <c>detail</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This redacts what is published, and never what is recorded.</strong> A failed outcome's own message
/// and an exception's message are diagnostic text: they are written to name the tenant the request resolved
/// to, because that is the fact that makes a support report actionable, and they keep naming it in the log.
/// This type sits at the two edges where that same text becomes a response body, and only there.
/// </para>
/// <para>
/// The identifier being removed is the <em>portal</em> - the tenant discriminator. It is redacted because it is
/// not the caller's to know: on the majority of routes it is resolved from the caller's own token by
/// <c>PortalAliasResolutionMiddleware</c> rather than supplied in the URL, so publishing it turns an ordinary
/// "no such account" answer into a disclosure of how the installation numbers its tenants. Note that the legacy
/// schema seeds <c>Portals.PortalID</c> with <c>IDENTITY(-1,1)</c>, so the value is frequently negative and a
/// rule that only matched digits would miss the most common installation of all.
/// </para>
/// <para>
/// Every OTHER identifier in these messages is left exactly as it is, and that is a deliberate distinction
/// rather than an oversight. An account id, role id, page id or module id appearing in a not-found detail is
/// the address the caller just asked about, echoed back so they can see which of several addresses in a batch
/// was the one that failed. Redacting those would cost the caller real diagnostic value and protect nothing,
/// because they already hold the value.
/// </para>
/// </remarks>
internal static partial class PublishedDetail
{
    /// <summary>What a redacted mention of the tenant reads as mid-sentence.</summary>
    private const string MidSentenceReplacement = "this portal";

    /// <summary>What a redacted mention of the tenant reads as when it opened the sentence.</summary>
    private const string SentenceOpeningReplacement = "This portal";

    /// <summary>
    /// Matches the word <c>portal</c> followed by an integer identifier, capturing the leading letter so its
    /// case can be carried over to the replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source-generated rather than constructed at runtime, so the matcher is built once at compile time and
    /// the pattern is validated by the compiler instead of on first use at an error path - which is the worst
    /// possible moment to discover a malformed pattern.
    /// </para>
    /// <para>
    /// <c>\b</c> anchors the left edge so a word merely ENDING in "portal" is untouched, and the identifier
    /// side requires at least one digit so the phrase "this portal" - which this method itself produces - can
    /// never be re-matched on a second pass. The optional sign is what covers the negative seed described on
    /// the type.
    /// </para>
    /// </remarks>
    /// <returns>The matcher for a tenant mention.</returns>
    [GeneratedRegex(
        @"\b(?<lead>[Pp])ortal\s+-?\d+",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex TenantMention();

    /// <summary>Rewrites every mention of a specific tenant so the text names no identifier.</summary>
    /// <param name="message">The text about to be published.</param>
    /// <returns>
    /// The text with each tenant identifier replaced by a demonstrative phrase, or <paramref name="message"/>
    /// unchanged when it names no tenant.
    /// </returns>
    /// <remarks>
    /// Returns the SAME reference when nothing matched, so the overwhelmingly common case - a message that
    /// never mentioned a tenant - allocates nothing.
    /// </remarks>
    internal static string Redact(string message)
    {
        // Cheap ordinal pre-test before the matcher runs. Most published details do not contain the word at
        // all, and this keeps them off the regex path entirely.
        if (message.IndexOf("ortal", StringComparison.Ordinal) < 0)
        {
            return message;
        }

        return TenantMention().Replace(
            message,
            static match => match.Groups["lead"].ValueSpan[0] == 'P'
                ? SentenceOpeningReplacement
                : MidSentenceReplacement);
    }

    /// <summary>Rewrites a mention of a specific tenant when text is present.</summary>
    /// <param name="message">The text about to be published, which may be absent.</param>
    /// <returns>The redacted text, or <paramref name="message"/> when it is <see langword="null"/> or blank.</returns>
    /// <remarks>
    /// A convenience for the callers that hold an optional detail, so the null check is written once here
    /// rather than at each edge.
    /// </remarks>
    internal static string? RedactOptional(string? message) =>
        string.IsNullOrWhiteSpace(message) ? message : Redact(message);
}
