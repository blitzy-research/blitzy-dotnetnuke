// MIGRATION: DomainException is a net-new abstraction with no legacy predecessor.
// The five in-scope DotNetNuke 4.9.0 trees - Library/Components/{Portal, Modules,
// Users, Security, Tabs} - declare no custom exception type at all. Failure was
// reported instead by mutating a ByRef argument and returning a status code, an
// idiom that survives at 30 measured call sites; the canonical one is
//     Public Shared Function CreateUser(ByRef objUser As UserInfo) As UserCreateStatus
// in Library/Components/Users/UserController.vb, backed by the 18-member
// UserCreateStatus and the 7-member UserLoginStatus enumerations. Absence was
// encoded the same value-based way, through the sentinels in
// Library/Components/Shared/Null.vb (NullInteger = -1, NullString = "").
//
// Every one of those return-code paths maps to Result / Result<T>, NOT to this
// exception: promoting an expected, enumerated outcome into a thrown exception
// would be a behavioural change rather than a migration. This type therefore
// covers only the case the legacy code never expressed at all - a broken
// invariant. The repository-root MIGRATION_NOTES.md is where that divergence is
// recorded at repository level; this annotation is its counterpart in code.

namespace DnnMigration.Domain.Common;

/// <summary>
/// Signals that a domain invariant has been violated: the model was asked to enter a
/// state it must never occupy.
/// </summary>
/// <remarks>
/// <para>
/// This type is the thrown half of a deliberate two-part error model whose other half,
/// <c>Result</c> and <c>Result&lt;T&gt;</c>, is returned rather than thrown. The split is
/// not stylistic; it decides where every failure in the solution is expressed.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     An invariant violation is a state the domain must never reach - an aggregate whose
///     required name is absent after construction, where the schema declares that column
///     <c>NOT NULL</c>. It is a defect in the calling code, it has no meaningful recovery at
///     the call site, and it is thrown as a <see cref="DomainException"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     An expected failure is the ordinary, enumerated outcome of a legitimate request - a
///     validation miss, a record that was not found, a duplicate user name or e-mail
///     address, an incorrect password. It is part of the contract, the caller is expected to
///     handle it, and it is returned as a failed <c>Result</c>. It is never thrown.
///     </description>
///   </item>
/// </list>
/// <para>
/// Never invert that pairing. Throwing for an expected failure turns ordinary control flow
/// into an exception and pushes a routine outcome to the edge of the process; returning a
/// result for a broken invariant hides a defect behind a value the caller may ignore.
/// </para>
/// <para>
/// The type is intentionally minimal, and each omission is deliberate. It carries no error
/// code, because an error code would duplicate the enumerated failure reasons that belong to
/// <c>Result</c>. It carries no status or transport mapping, because translating an unhandled
/// exception into the RFC 7807 error response body is the responsibility of the API layer's
/// global exception handler - the Domain project declares no project or package reference, so
/// it holds nothing that could express a web concern. It performs no logging, for the same
/// reason. And it is deliberately not accompanied by derived exception types: "not found",
/// "validation failed" and "duplicate" are <c>Result</c> failure reasons rather than
/// exceptions, which leaves this the single invariant-violation signal for the layer and
/// keeps a hierarchy from growing to mirror one that should not exist.
/// </para>
/// <para>
/// The class is left unsealed so that a future aggregate-specific invariant can specialise it
/// if a genuine need arises, while <c>catch (DomainException)</c> continues to select every
/// such failure. Instances carry no state beyond what <see cref="Exception"/> already provides
/// and are not mutated after construction, so the type is exactly as thread-safe as its base.
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
    /// <summary>
    /// Initialises a new instance of the <see cref="DomainException"/> class with a message
    /// supplied by the runtime.
    /// </summary>
    /// <remarks>
    /// Provided because a public exception type is expected to offer the three conventional
    /// constructors, which keeps it predictable for callers and for reflection-based tooling.
    /// Prefer <see cref="DomainException(string)"/> in production code so that the violated
    /// invariant is named rather than left to a generic runtime message.
    /// </remarks>
    public DomainException()
        : base()
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="DomainException"/> class with a message
    /// that describes the violated invariant.
    /// </summary>
    /// <param name="message">
    /// The message that describes the invariant that was violated. State what the domain
    /// requires rather than what the caller did, and never include a password, token,
    /// connection string or any other sensitive value: the message reaches the structured log
    /// and, for an unhandled exception, the diagnostic output of the API layer.
    /// </param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="DomainException"/> class with a message
    /// that describes the violated invariant and the exception that caused it.
    /// </summary>
    /// <param name="message">
    /// The message that describes the invariant that was violated, subject to the same
    /// guidance as <see cref="DomainException(string)"/>.
    /// </param>
    /// <param name="innerException">
    /// The exception that caused the invariant to be violated, or that revealed the violation.
    /// It is preserved on <see cref="Exception.InnerException"/> so that the original
    /// diagnostic detail survives translation at the edge of the process.
    /// </param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
