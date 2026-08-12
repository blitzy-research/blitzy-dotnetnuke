namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Raised when a cache entry's value factory did not finish inside the budget the cache allows a shared
/// production to run for.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A DEDICATED TYPE EXISTS, AND WHAT IT REPLACED.</b> The cache used to raise a plain
/// <see cref="TimeoutException"/> for this condition, and a review found the consequence rather than
/// the intent: the store-failure classifier that decides whether a request is answered
/// <c>503 Service Unavailable</c> treated any bare <see cref="TimeoutException"/> in an exception chain
/// as evidence that the DATABASE was unreachable. A cache defect was therefore reported to callers as a
/// dependency outage, complete with a <c>Retry-After</c> hint - which invites a retry of a request that
/// will fail in exactly the same way, and hides the defect behind the retry.
/// </para>
/// <para>
/// A distinct type settles that at the source rather than by pattern-matching on messages. The classifier
/// now requires structural data-access context before it will read a generic timeout as an outage, and
/// this type carries no such context, so the two conditions are told apart by construction.
/// </para>
/// <para>
/// <b>IT DELIBERATELY DOES NOT DERIVE FROM <see cref="TimeoutException"/>.</b> The inheritance would read
/// naturally - this IS a timeout - and it would reintroduce the defect: every arm that matches a timeout
/// by type would match this again, including the one that was just narrowed, and the narrowing would then
/// depend on the order of the pattern arms rather than on the types. Deriving from
/// <see cref="InvalidOperationException"/> states what the condition actually is from the caller's point
/// of view: the operation could not be completed as asked, and the reason is a defect in the value factory
/// rather than in any dependency.
/// </para>
/// <para>
/// <b>WHAT IT MEANS WHEN IT IS SEEN.</b> A value factory ignored the cancellation token it was handed and
/// ran past the cache's shared-production budget. That is an application defect: the cache has already
/// retired the registration and cancelled the production, so the next caller starts a fresh attempt rather
/// than joining a stalled one, and nothing about the database is implied. It is answered as an unexpected
/// server failure, which is the honest classification, and it is not advertised as retryable.
/// </para>
/// <para>
/// <b>NO KEY, NO VALUE AND NO FACTORY DETAIL CROSSES THIS BOUNDARY.</b> The message a thrower supplies is
/// its own business, but nothing here obliges one to name the entry, and the transport publishes fixed
/// caller-safe wording for every unexpected failure rather than an exception's own text - so a cache key,
/// which can carry a tenant or an account identifier, does not reach a caller through this type.
/// </para>
/// </remarks>
public sealed class CacheProductionTimeoutException : InvalidOperationException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CacheProductionTimeoutException"/> class.
    /// </summary>
    public CacheProductionTimeoutException()
        : base("Producing a cache entry did not complete within the permitted budget.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CacheProductionTimeoutException"/> class with an
    /// explanation for the operator reading the log.
    /// </summary>
    /// <param name="message">
    /// What happened and what the cache did about it. It reaches the log rather than the caller: the
    /// transport publishes fixed wording for an unexpected failure and never an exception's own text.
    /// </param>
    public CacheProductionTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CacheProductionTimeoutException"/> class with an
    /// explanation and the failure that caused it.
    /// </summary>
    /// <param name="message">What happened and what the cache did about it.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public CacheProductionTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
