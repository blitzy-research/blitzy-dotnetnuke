namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a write was refused because the record it addressed had already been changed by another
/// caller, so applying it would have destroyed that caller's committed edit.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS. Every whole-record write in this solution asks the caller to round-trip an opaque
/// token derived from the values it read, and refuses the write when the token no longer matches.
/// </para>
/// <para>
/// Where it is raised, and why not lower. Recognising the store's refusal is a provider concern - a zero
/// affected-row count reported by the object-relational mapper, or a serialisation failure reported by the
/// database engine - so the translation belongs to the persistence assembly, the only one that references
/// the database client.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>The authored message used when a thrower supplies none.</summary>
    private const string DefaultMessage =
        "The write was refused because the record had already been changed by another caller.";

    /// <summary>Initialises the exception with the authored default message.</summary>
    public ConcurrencyConflictException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initialises the exception with an authored message.</summary>
    /// <param name="message">An authored, caller-safe description.</param>
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with an authored message and the provider fault it explains.</summary>
    /// <param name="message">An authored, caller-safe description.</param>
    /// <param name="innerException">The provider exception, preserved for diagnostics only.</param>
    public ConcurrencyConflictException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception for a store refusal, recording how the store reported the lost update.
    /// </summary>
    /// <param name="innerException">The provider exception, preserved for diagnostics only.</param>
    /// <returns>The exception to throw.</returns>
    public static ConcurrencyConflictException ForLostUpdate(Exception? innerException) =>
        new(DefaultMessage, innerException);
}
