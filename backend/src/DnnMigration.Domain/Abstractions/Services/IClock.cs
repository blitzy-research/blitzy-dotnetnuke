namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: the legacy absent-date sentinel (Date.MinValue, published as Null.NullDate) is not reproduced.

/// <summary>Supplies the present instant, and is the only sanctioned source of it for the backend.</summary>
/// <remarks>
/// <para>
/// Domain, Application and Infrastructure code obtains the present instant through an injected <see
/// cref="IClock"/>. A direct machine-clock read cannot be substituted by a test, which is what makes a
/// time-dependent business rule non-deterministic; the legacy VB.NET code had no time abstraction at all.
/// </para>
/// <para>
/// An implementation is expected to hold no mutable state, to be registered as a singleton and to be
/// acquired by constructor injection rather than through an ambient accessor. A test supplies its own
/// implementation instead of widening this contract, which is why no member for adjusting the yielded value
/// is declared.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Gets the present instant in Coordinated Universal Time.</summary>
    /// <value>A <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Utc"/>.</value>
    DateTime UtcNow { get; }
}
