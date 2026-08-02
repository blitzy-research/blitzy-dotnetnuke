namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: server-local clock to UTC. The legacy code read the web server's local clock;
// the canonical site is Library/Components/Security/Roles/RoleController.vb:L496, which
// derives a role expiry from Date.Today(). This contract speaks UTC only, so a date-only
// value derived from it can differ by one calendar day from the legacy figure according to
// the host's offset and the moment of evaluation.
//
// MIGRATION: the legacy absent-date sentinel (Date.MinValue, published as Null.NullDate) is
// not reproduced. UtcNow is non-nullable because an instant always exists; an absent date is
// a nullable DateTime, and sentinel semantics survive only where a wire contract is
// externally observable, at the DTO and API boundary.

/// <summary>
/// Supplies the present instant, and is the only sanctioned source of it for the backend.
/// </summary>
/// <remarks>
/// <para>
/// Domain, Application and Infrastructure code obtains the present instant through an injected
/// <see cref="IClock"/>. A direct machine-clock read cannot be substituted by a test, which is
/// what makes a time-dependent business rule non-deterministic; the legacy VB.NET code had no
/// time abstraction at all.
/// </para>
/// <para>
/// There is deliberately no <c>Today</c>, local-time or offset-bearing member. A caller needing
/// a calendar date derives <c>UtcNow.Date</c> and a caller needing local time converts at the
/// presentation boundary, which keeps one instant source for the solution and makes the UTC
/// basis of every derived value visible at the point of use.
/// </para>
/// <para>
/// An implementation is expected to hold no mutable state, to be registered as a singleton and
/// to be acquired by constructor injection rather than through an ambient accessor. A test
/// supplies its own implementation instead of widening this contract, which is why no member for
/// adjusting the yielded value is declared.
/// </para>
/// <para>
/// Entities do not stamp themselves: the Domain layer references nothing and so cannot acquire a
/// clock. Stamping <c>CreatedDate</c> and <c>LastUpdatedDate</c> is an Application service's
/// responsibility, performed with an injected <see cref="IClock"/>.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>
    /// Gets the present instant in Coordinated Universal Time.
    /// </summary>
    /// <value>
    /// A <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/>. The value is always a genuine reading and never stands in
    /// for a missing date.
    /// </value>
    /// <remarks>
    /// Synchronous by design: reading a clock performs no I/O, so the solution-wide rule that
    /// I/O-bound members be awaitable does not apply. Do not reshape this member.
    /// </remarks>
    DateTime UtcNow { get; }
}
