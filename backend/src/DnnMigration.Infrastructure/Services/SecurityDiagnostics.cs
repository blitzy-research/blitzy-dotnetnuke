using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// The single implementation of <see cref="ISecurityDiagnostics"/>, writing each occurrence as a structured
/// log event.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS LIVES HERE. Logging is a framework concern and this is the innermost layer that may take a
/// dependency on one: <c>Microsoft.Extensions.Logging.Abstractions</c> arrives with the persistence packages
/// this project already declares, so no manifest entry is added to obtain it, and the Application layer above
/// - whose package surface is fixed - reaches it through the Domain contract instead.
/// </para>
/// <para>
/// EVERY VALUE IS A STRUCTURED PROPERTY AND THE TEMPLATE IS A CONSTANT. Nothing is interpolated into the
/// message, so no supplied value can become part of a message template and no newline in a value can forge a
/// second log line. Combined with the contract's signature - which admits no message, exception or object -
/// this makes it impossible for credential material, request payloads or exception text to be written through
/// this route.
/// </para>
/// <para>
/// LEVEL CHOICE IS DELIBERATE. Every member of the enumeration is a warning: none of them fails the caller's
/// request, so error would over-report, and none of them is routine, so information would leave them
/// invisible under a normal production filter. They are exactly the class of event an operator wants surfaced
/// without being paged.
/// </para>
/// <para>
/// IT NEVER THROWS. The contract requires that, because every call site has already decided the occurrence
/// does not warrant failing the request; a recorder that threw would turn those into the failures they were
/// judged not to be. The only work performed is a bounded scan of a short string and one logger call.
/// </para>
/// </remarks>
internal sealed class SecurityDiagnostics : ISecurityDiagnostics
{
    /// <summary>
    /// The longest reason code that is written. Anything longer is treated as not being a code.
    /// </summary>
    /// <remarks>
    /// Sixty-four characters comfortably admits every failure code this solution defines - the longest is well
    /// under half of it - and every exception type name in the framework, while excluding anything
    /// sentence-shaped. The bound exists so that a caller which mistakenly passes an exception's message has
    /// it discarded rather than written, which is a guarantee this type enforces rather than documents.
    /// </remarks>
    private const int MaximumReasonCodeLength = 64;

    /// <summary>
    /// The substitute written when a supplied reason code is not code-shaped.
    /// </summary>
    /// <remarks>
    /// A substitute rather than an omission, deliberately: the occurrence itself is still worth recording, and
    /// a distinctive placeholder additionally makes the mistaken call site findable by searching the logs -
    /// which an omission would not.
    /// </remarks>
    private const string RejectedReasonCode = "unrecognised-code";

    private readonly ILogger<SecurityDiagnostics> _logger;

    /// <summary>Initialises a new instance of the <see cref="SecurityDiagnostics"/> class.</summary>
    /// <param name="logger">The logger occurrences are written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public SecurityDiagnostics(ILogger<SecurityDiagnostics> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public void Record(
        SecurityDiagnosticEvent occurrence,
        int? portalId = null,
        int? userId = null,
        string? reasonCode = null)
    {
        // The member NAME is written rather than its ordinal, so a log remains readable after a member is
        // added and so a reader never has to hold the enumeration's numbering in their head. Ordinals are
        // also the one thing about an enumeration that can change without a compiler complaining.
        _logger.LogWarning(
            "Security diagnostic {Occurrence} recorded for portal {PortalId} and account {UserId} with reason {ReasonCode}.",
            occurrence.ToString(),
            portalId,
            userId,
            Sanitise(reasonCode));
    }

    /// <summary>
    /// Reduces a supplied reason code to something that is certainly a code.
    /// </summary>
    /// <param name="reasonCode">The value the caller supplied.</param>
    /// <returns>
    /// The value unchanged when it has the shape of a code, <see langword="null"/> when none was supplied, and
    /// <see cref="RejectedReasonCode"/> when the value is not code-shaped.
    /// </returns>
    /// <remarks>
    /// The permitted set is ASCII letters, digits, and the four separators codes in this solution actually use
    /// - dot, underscore, hyphen and colon. Everything else is excluded, which in particular excludes the
    /// space, the newline and the carriage return: a value carrying any of those is prose or a forged log line
    /// rather than a code, and is precisely what must not be written.
    /// </remarks>
    private static string? Sanitise(string? reasonCode)
    {
        if (string.IsNullOrEmpty(reasonCode))
        {
            return null;
        }

        if (reasonCode.Length > MaximumReasonCodeLength)
        {
            return RejectedReasonCode;
        }

        foreach (char character in reasonCode)
        {
            bool permitted = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-' or ':';

            if (!permitted)
            {
                return RejectedReasonCode;
            }
        }

        return reasonCode;
    }
}
