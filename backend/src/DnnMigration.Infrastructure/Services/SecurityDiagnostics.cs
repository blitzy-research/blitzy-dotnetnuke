using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// The single implementation of <see cref="ISecurityDiagnostics"/>, writing each occurrence as a structured
/// log event.
/// </summary>
/// <remarks>
/// EVERY VALUE IS A STRUCTURED PROPERTY AND THE TEMPLATE IS A CONSTANT. Nothing is interpolated into the
/// message, so no supplied value can become part of a message template and no newline in a value can forge
/// a second log line.
/// </remarks>
internal sealed class SecurityDiagnostics : ISecurityDiagnostics
{
    /// <summary>The longest reason code that is written. Anything longer is treated as not being a code.</summary>
    private const int MaximumReasonCodeLength = 64;

    /// <summary>The substitute written when a supplied reason code is not code-shaped.</summary>
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
        try
        {
            _logger.LogWarning(
                "Security diagnostic {Occurrence} recorded for portal {PortalId} and account {UserId} with reason {ReasonCode}.",
                occurrence.ToString(),
                portalId,
                userId,
                Sanitise(reasonCode));
        }
        catch (Exception)
        {
            // The contract is a last-resort signal for conditions that deliberately do not fail a request.
            // A logging provider that throws must not reverse that decision.
        }
    }

    /// <summary>Reduces a supplied reason code to something that is certainly a code.</summary>
    /// <param name="reasonCode">The value the caller supplied.</param>
    /// <returns>
    /// The value unchanged when it has the shape of a code, <see langword="null"/> when none was supplied,
    /// and <see cref="RejectedReasonCode"/> when the value is not code-shaped.
    /// </returns>
    /// <remarks>
    /// The permitted set is ASCII letters, digits, and the four separators codes in this solution actually
    /// use - dot, underscore, hyphen and colon. Everything else is excluded, which in particular excludes
    /// the space, the newline and the carriage return: a value carrying any of those is prose or a forged
    /// log line rather than a code, and is precisely what must not be written.
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
