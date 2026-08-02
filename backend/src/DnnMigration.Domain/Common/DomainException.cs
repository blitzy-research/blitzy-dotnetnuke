
namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a domain invariant has been violated: the model was asked to enter a state it
/// must never occupy.
/// </summary>
/// <remarks>
/// <para>
/// This is the thrown half of a deliberate two-part error model whose other half, <c>Result</c>
/// and <c>Result&lt;T&gt;</c>, is returned rather than thrown, and the split decides where every
/// failure in the solution is expressed. An invariant violation is a state the domain must never
/// reach; it is a defect in the calling code with no meaningful recovery at the call site, and it
/// is thrown. An expected failure - a validation miss, a record that was not found, a duplicate
/// user name or e-mail address, an incorrect password - is part of the contract and is returned as
/// a failed <c>Result</c>, never thrown. Inverting that pairing either turns ordinary control flow
/// into an exception or hides a defect behind a value the caller may ignore.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is developer-facing and must be treated as such: throw sites in
/// this project echo the rejected input and cite legacy source locations to aid diagnosis. A
/// transport boundary must therefore publish <see cref="PublicDetail"/> when the thrower supplied
/// one and a generic detail otherwise, logging the message privately either way.
/// </para>
/// <para>
/// The type is intentionally minimal and each omission is deliberate. It carries no error code,
/// which would duplicate the enumerated failure reasons belonging to <c>Result</c>; no status or
/// transport mapping, because the Domain project declares no project or package reference and so
/// holds nothing that could express a web concern; no logging, for the same reason; and no derived
/// exception types, because "not found", "validation failed" and "duplicate" are <c>Result</c>
/// failure reasons rather than exceptions. It is left unsealed so that a future aggregate-specific
/// invariant can specialise it while <c>catch (DomainException)</c> continues to select every such
/// failure. Instances are not mutated after construction, so the type is exactly as thread-safe as
/// its base.
/// </para>
/// </remarks>
/// <example>
/// Guarding an invariant while constructing an aggregate:
/// <code>
/// if (string.IsNullOrWhiteSpace(portalName))
/// {
///     throw new DomainException("A portal must always have a name.");
/// }
/// </code>
/// </example>
public class DomainException : Exception
{
    public DomainException()
        : base()
    {
    }

    /// <summary>
    /// Initialises a new instance carrying a developer-facing description of the violated
    /// invariant. Supply <see cref="PublicDetail"/> in an object initialiser when a caller-safe
    /// detail should accompany it.
    /// </summary>
    /// <param name="message">A description of the invariant that was violated.</param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance carrying a developer-facing description of the violated
    /// invariant together with the exception that revealed it.
    /// </summary>
    /// <param name="message">A description of the invariant that was violated.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the caller-safe detail that a transport boundary may publish, or
    /// <see langword="null"/> when the thrower supplied none.
    /// </summary>
    /// <remarks>
    /// Opt in only for text written deliberately for an untrusted caller. Leaving this
    /// <see langword="null"/> is the safe default and instructs the boundary to publish a generic
    /// detail instead, because <see cref="Exception.Message"/> is written for a developer and may
    /// echo rejected input or name internal sources.
    /// </remarks>
    public string? PublicDetail { get; init; }
}
