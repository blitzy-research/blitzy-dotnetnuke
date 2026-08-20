using System.Text;

namespace DnnMigration.Application.Validation;

/// <summary>
/// The single shared upper bound applied to every clear-text credential this application accepts, together
/// with the predicate that measures it and the message reported when it is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// Every credential entry point applies <see cref="IsWithinMaximumByteLength"/> before a credential can
/// reach <see cref="Domain.Abstractions.Services.IPasswordHasher"/>, and the hasher applies it again as
/// defence in depth. Both sides read the same constant, so the two can never disagree.
/// </para>
/// <para>
/// The bound is expressed in UTF-8 bytes because that is the form the hashing algorithm consumes. It is a
/// compile-time constant rather than a configurable setting, deliberately: a maximum that a deployment can
/// raise provides no guarantee.
/// </para>
/// </remarks>
public static class CredentialBounds
{
    /// <summary>The greatest number of UTF-8 bytes a clear-text credential may occupy.</summary>
    /// <remarks>
    /// Measured in bytes, not characters, because the hashing algorithm consumes the UTF-8 encoding.
    /// Declared <see langword="const"/> so no configuration source can raise it.
    /// </remarks>
    public const int MaximumByteLength = 256;

    /// <summary>The message reported when a credential exceeds <see cref="MaximumByteLength"/>.</summary>
    /// <remarks>
    /// Deliberately identical at every entry point, so a caller cannot infer which endpoint it reached from
    /// the wording, and deliberately free of the submitted value, so no credential material is ever echoed
    /// back or written to a log.
    /// </remarks>
    public const string MaximumByteLengthMessage =
        "The password supplied is too long. A password may be at most 256 bytes when encoded as UTF-8.";

    /// <summary>Reports whether <paramref name="credential"/> is within <see cref="MaximumByteLength"/>.</summary>
    /// <param name="credential">The candidate clear-text credential.</param>
    /// <returns>
    /// <see langword="true"/> when the value occupies no more than <see cref="MaximumByteLength"/> UTF-8
    /// bytes; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// An absent or empty credential passes this check on purpose. Requiredness is a separate rule with its
    /// own legacy-derived message, and a single omitted value must produce a single message rather than one
    /// complaint per rule that happens to look at it.
    /// </remarks>
    public static bool IsWithinMaximumByteLength(string? credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return true;
        }

        return Encoding.UTF8.GetByteCount(credential) <= MaximumByteLength;
    }
}
