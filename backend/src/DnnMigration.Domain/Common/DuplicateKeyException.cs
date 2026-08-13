namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a write was refused because it would have duplicated a value the store requires to be
/// unique, and that no check the caller could have performed first would have prevented it.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because a uniqueness rule cannot be enforced by reading first. Every create path in
/// this solution asks the store whether the value is taken and reports a duplicate as a failed
/// <c>Result</c>, which is right and stays: it answers the ordinary case precisely, names the field, and
/// costs one indexed read.
/// </para>
/// <para>
/// Where it is raised, and why not lower. The store's own error is a provider concern, so the translation
/// belongs to the persistence assembly - the only one that references the database client - and nothing
/// above it names a provider type.
/// </para>
/// </remarks>
public sealed class DuplicateKeyException : Exception
{
    /// <summary>The authored message used when a thrower names no constraint.</summary>
    private const string UnnamedConstraintMessage =
        "The write was refused because it would have duplicated a value the store requires to be unique.";

    /// <summary>Initialises the exception with the authored default message.</summary>
    public DuplicateKeyException()
        : base(UnnamedConstraintMessage)
    {
    }

    /// <summary>Initialises the exception with an authored message.</summary>
    /// <param name="message">An authored, caller-safe description.</param>
    public DuplicateKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with an authored message and the provider fault it explains.</summary>
    /// <param name="message">An authored, caller-safe description.</param>
    /// <param name="innerException">The provider exception, preserved for diagnostics only.</param>
    public DuplicateKeyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception for a store refusal, carrying the constraint the store named when it disclosed
    /// one.
    /// </summary>
    /// <param name="constraintName">
    /// The index or constraint the store named, or <see langword="null"/> when it named none.
    /// </param>
    /// <param name="innerException">The provider exception, preserved for diagnostics only.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>
    /// A named factory rather than a third constructor, because the constraint form takes the same two
    /// parameter types as the message form and a reader at the call site could not tell which it had
    /// reached. The name makes the choice explicit and cannot be resolved the wrong way by overloading.
    /// </remarks>
    public static DuplicateKeyException ForConstraint(string? constraintName, Exception? innerException)
    {
        string? named = string.IsNullOrWhiteSpace(constraintName) ? null : constraintName;

        string message = named is null
            ? UnnamedConstraintMessage
            : "The write was refused because it would have duplicated a value the store requires to be "
              + "unique, under constraint " + named + ".";

        return new DuplicateKeyException(message, innerException) { ConstraintName = named };
    }

    /// <summary>Gets the index or constraint the store named, or <see langword="null"/> when it named none.</summary>
    /// <remarks>
    /// BEST EFFORT, and never the sole basis of a decision. It is parsed from provider text that carries no
    /// stability guarantee, so a consumer that switches on it must also have an answer for the absent and
    /// the unrecognised case.
    /// </remarks>
    public string? ConstraintName { get; private init; }
}
