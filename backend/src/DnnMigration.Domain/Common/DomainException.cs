namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a domain invariant has been violated: the model was asked to enter a state it must never
/// occupy.
/// </summary>
/// <remarks>
/// <para>
/// The line is not "validation is always returned". Application services throw this exception for
/// request-shape failures as well - a required name left blank, a value past its column width, a negative
/// fee, an unrecognised billing code, a negative page index - on the reasoning that a request which cannot
/// be formed at all is a caller defect rather than a business outcome.
/// </para>
/// <para>
/// The type is intentionally minimal and each omission is deliberate.
/// </para>
/// </remarks>
public class DomainException : Exception
{
    public DomainException()
        : base()
    {
    }

    /// <summary>
    /// Initialises a new instance carrying a developer-facing description of the violated invariant. Supply
    /// <see cref="PublicDetail"/> in an object initialiser when a caller-safe detail should accompany it.
    /// </summary>
    /// <param name="message">A description of the invariant that was violated.</param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance carrying a developer-facing description of the violated invariant
    /// together with the exception that revealed it.
    /// </summary>
    /// <param name="message">A description of the invariant that was violated.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the caller-safe detail that a transport boundary may publish, or <see langword="null"/> when
    /// the thrower supplied none.
    /// </summary>
    public string? PublicDetail { get; init; }
}
