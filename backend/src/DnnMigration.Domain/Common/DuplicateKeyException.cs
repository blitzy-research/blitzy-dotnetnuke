namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a write was refused because it would have duplicated a value the store requires to be
/// unique, and that no check the caller could have performed first would have prevented it.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: SEC-F6. This type exists because a uniqueness rule cannot be enforced by reading first. Every
/// create path in this solution asks the store whether the value is taken and reports a duplicate as a
/// failed <c>Result</c>, which is right and stays: it answers the ordinary case precisely, names the field,
/// and costs one indexed read. What it cannot do is close the window between the read and the flush. Two
/// requests carrying the same name, arriving together, both read "not taken", and the second one's insert is
/// refused by the unique index. Measured on a live installation, ten simultaneous identical role creations
/// produced one <c>201</c>, seven <c>409</c> and two <c>500</c> - the two that lost the race after passing
/// the pre-check, reported to the caller as a server fault although the server had behaved correctly and the
/// store held exactly one row.
/// </para>
/// <para>
/// It is a plain <see cref="Exception"/> and deliberately NOT a <see cref="DomainException"/>. That type
/// documents itself as the thrown half of the invariant-violation model and every transport maps it to
/// <c>400 Bad Request</c>; a lost insert race is neither a broken invariant nor a malformed request, and
/// answering it <c>400</c> would tell the caller its submission was unusable when a competing request had
/// simply arrived first. It also carries no reason code, for the same reason
/// <see cref="DomainException"/> carries none: the code belongs to the failed <c>Result</c> the application
/// service returns, and each create path returns the SAME code its own pre-check emits so that a caller sees
/// one taxonomy whether it lost the race or merely arrived second.
/// </para>
/// <para>
/// Where it is raised, and why not lower. The store's own error is a provider concern, so the translation
/// belongs to the persistence assembly - the only one that references the database client - and nothing
/// above it names a provider type. Where it is caught, and why not higher: an application service knows
/// which field of which aggregate a unique index protects and can therefore name it, whereas a transport
/// stage knows only that something was duplicated. The transport does carry a last-resort arm, for a path
/// that has not yet been given a specific one, so an untranslated race can never again be published as a
/// server fault.
/// </para>
/// <para>
/// <see cref="ConstraintName"/> is BEST EFFORT and every consumer must treat it as such. The store names the
/// index it refused in the text of its own error, which is not a contract: the wording is
/// version-dependent, the name itself is an installation detail, and a rename would change it without
/// changing any behaviour. It is carried because one create path writes several unique-constrained rows in
/// one flush and can use it to choose between two reason codes, and it must always be paired with a code
/// that applies when it is absent or unrecognised.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is AUTHORED HERE and never taken from the provider. A provider message
/// quotes the duplicated key value and the object name, so publishing it would disclose both a caller's
/// input and the schema; the provider's own exception is preserved as the inner one, where a log can reach
/// it and a response cannot. Instances are not mutated after construction, so the type is exactly as
/// thread-safe as its base.
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
    /// <param name="message">
    /// An authored, caller-safe description. Never a provider message: see the note on this type.
    /// </param>
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
    /// Creates the exception for a store refusal, carrying the constraint the store named when it
    /// disclosed one.
    /// </summary>
    /// <param name="constraintName">
    /// The index or constraint the store named, or <see langword="null"/> when it named none. Best effort:
    /// see the note on <see cref="ConstraintName"/>.
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

    /// <summary>
    /// Gets the index or constraint the store named, or <see langword="null"/> when it named none.
    /// </summary>
    /// <remarks>
    /// BEST EFFORT, and never the sole basis of a decision. It is parsed from provider text that carries no
    /// stability guarantee, so a consumer that switches on it must also have an answer for the absent and
    /// the unrecognised case. Nothing a caller submitted appears in it - it is a schema name - so it is safe
    /// to log, though not to publish, since publishing it would disclose the schema.
    /// </remarks>
    public string? ConstraintName { get; private init; }
}
