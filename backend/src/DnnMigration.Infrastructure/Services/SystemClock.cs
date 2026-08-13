using DnnMigration.Domain.Abstractions.Services;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: DIVERGENCE, server-local to UTC. Those reads are normalized here onto one injected boundary
// that speaks Coordinated Universal Time only.

/// <summary>
/// Reads the machine clock and reports the present instant in Coordinated Universal Time. This is the
/// production implementation of <see cref="IClock"/> and the solution's only sanctioned system-clock
/// boundary.
/// </summary>
/// <remarks>
/// Sole clock boundary. Every layer above obtains the present instant by injecting <see cref="IClock"/>, so
/// this file is the single place in the backend where the machine clock is touched.
/// </remarks>
internal sealed class SystemClock : IClock
{
    /// <summary>Initializes a new instance of the <see cref="SystemClock"/> class.</summary>
    /// <remarks>
    /// Declared explicitly, and public, for two reasons. The dependency-injection container chooses a
    /// constructor from the public constructors of the implementation type, so an internal class still
    /// needs a public one to be activatable at all; and spelling the empty parameter list in source puts
    /// the singleton guarantee where a reader looks for it.
    /// </remarks>
    public SystemClock()
    {
    }

    /// <summary>
    /// Gets the present instant in Coordinated Universal Time, read from the machine clock at the moment of
    /// access.
    /// </summary>
    /// <value>A <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Utc"/>.</value>
    public DateTime UtcNow => DateTime.UtcNow;
}
