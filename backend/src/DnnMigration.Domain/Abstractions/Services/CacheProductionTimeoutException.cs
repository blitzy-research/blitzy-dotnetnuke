namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Raised when a cache entry's value factory did not finish inside the budget the cache allows a shared
/// production to run for.
/// </summary>
/// <remarks>
/// <para>
/// A distinct type settles that at the source rather than by pattern-matching on messages. The classifier
/// now requires structural data-access context before it will read a generic timeout as an outage, and this
/// type carries no such context, so the two conditions are told apart by construction.
/// </para>
/// <para>
/// <b>IT DELIBERATELY DOES NOT DERIVE FROM <see cref="TimeoutException"/>.</b> The inheritance would read
/// naturally - this IS a timeout - and it would reintroduce the defect: every arm that matches a timeout by
/// type would match this again, including the one that was just narrowed, and the narrowing would then
/// depend on the order of the pattern arms rather than on the types.
/// </para>
/// </remarks>
public sealed class CacheProductionTimeoutException : InvalidOperationException
{
    /// <summary>Initialises a new instance of the <see cref="CacheProductionTimeoutException"/> class.</summary>
    public CacheProductionTimeoutException()
        : base("Producing a cache entry did not complete within the permitted budget.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="CacheProductionTimeoutException"/> class with an
    /// explanation for the operator reading the log.
    /// </summary>
    /// <param name="message">What happened and what the cache did about it.</param>
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
