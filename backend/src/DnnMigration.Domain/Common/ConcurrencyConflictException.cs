namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a write was refused because the record it addressed had already been changed by another
/// caller, so applying it would have destroyed that caller's committed edit.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS. Every whole-record write in this solution asks the caller to round-trip an opaque
/// token derived from the values it read, and refuses the write when the token no longer matches. That check
/// answers the ordinary case precisely - it names the condition, tells the caller to re-read, and costs
/// nothing - and it cannot close the window between the read and the flush on its own: it is a PRE-CHECK, and
/// two requests that both pass it can still both write. The write paths therefore perform the read, the
/// check and the flush inside one serialisable scope, and this type is how the store's refusal of the losing
/// half travels back to the layer that can report it.
/// </para>
/// <para>
/// It is a plain <see cref="Exception"/> and deliberately NOT a <see cref="DomainException"/>, for the same
/// reason <see cref="DuplicateKeyException"/> is not: that type documents itself as the thrown half of the
/// invariant-violation model and every transport maps it to <c>400 Bad Request</c>, whereas a lost update is
/// neither a broken invariant nor a malformed request. The submitted values may have been perfectly valid; a
/// competing request simply arrived first, and the correct answer is <c>409</c> with an instruction to
/// re-read and re-apply.
/// </para>
/// <para>
/// It carries no reason code, again for the same reason: the code belongs to the failed <c>Result</c> the
/// application service returns, and each write path returns the SAME code its own token pre-check emits, so
/// a caller sees one taxonomy whether it lost the race or merely held a stale snapshot.
/// </para>
/// <para>
/// Where it is raised, and why not lower. Recognising the store's refusal is a provider concern - a zero
/// affected-row count reported by the object-relational mapper, or a serialisation failure reported by the
/// database engine - so the translation belongs to the persistence assembly, the only one that references
/// the database client. Nothing above it names a provider type. Where it is caught, and why not higher: an
/// application service knows which aggregate the caller was editing and can therefore name it, whereas a
/// transport stage knows only that something moved underneath somebody. The transport does carry a
/// last-resort arm, so an untranslated conflict can never be published as a server fault.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is AUTHORED HERE and never taken from the provider, because a provider
/// message for a serialisation failure names the objects and process identifiers involved - a schema and
/// deployment disclosure that no caller needs. The provider's own exception is preserved as the inner one,
/// where a log can reach it and a response cannot. Instances are not mutated after construction, so the type
/// is exactly as thread-safe as its base.
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
    /// <param name="message">
    /// An authored, caller-safe description. Never a provider message: see the note on this type.
    /// </param>
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
    /// <remarks>
    /// A named factory rather than a second single-argument constructor, because the two would take the same
    /// parameter type and a reader at the call site could not tell which had been reached.
    /// </remarks>
    public static ConcurrencyConflictException ForLostUpdate(Exception? innerException) =>
        new(DefaultMessage, innerException);
}
