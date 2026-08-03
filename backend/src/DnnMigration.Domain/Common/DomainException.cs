
namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a domain invariant has been violated: the model was asked to enter a state it
/// must never occupy.
/// </summary>
/// <remarks>
/// <para>
/// This is the thrown half of a two-part error model whose other half, <c>Result</c> and
/// <c>Result&lt;T&gt;</c>, is returned rather than thrown. The dividing line as the solution
/// actually draws it: an outcome the caller is expected to inspect and act on - a record that was
/// not found, a duplicate user name or e-mail address, an incorrect password, a refused write - is
/// returned as a failed <c>Result</c>; a violated invariant is thrown.
/// </para>
/// <para>
/// The line is not "validation is always returned". Application services throw this exception for
/// request-shape failures as well - a required name left blank, a value past its column width, a
/// negative fee, an unrecognised billing code, a negative page index - on the reasoning that a
/// request which cannot be formed at all is a caller defect rather than a business outcome. Those
/// throws are reached only when a caller bypasses the FluentValidation validator that the API layer
/// applies first, so on the HTTP path the same failure normally surfaces as a validation problem
/// response instead. Both channels end in a <c>400</c>: the API's exception handler maps this
/// exception to <c>400 Bad Request</c>, publishing <see cref="PublicDetail"/> when the thrower
/// supplied one. Callers of an Application service directly must therefore be prepared for both a
/// failed <c>Result</c> and a thrown <c>DomainException</c>, and must not read "the method returned
/// a Result" as "the method does not throw".
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
/// exception types, because a caller that needs to tell one failure from another reads the
/// <c>Result</c> reason code, and every throw of this type lands on one status. It is left unsealed
/// so that a future aggregate-specific
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
