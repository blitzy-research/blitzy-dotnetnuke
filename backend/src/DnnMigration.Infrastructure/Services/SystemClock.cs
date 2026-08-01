using DnnMigration.Domain.Abstractions.Services;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: The legacy trees read the web server's LOCAL clock directly at every
// MIGRATION: time-dependent site measured for this migration: the demo-expiry and
// MIGRATION: portal-expiry calculations in
// MIGRATION: Library/Components/Portal/PortalController.vb at L335 and L1504, the
// MIGRATION: event-message stamp in Library/Components/Modules/ModuleController.vb at
// MIGRATION: L273, and the role effective-date and cancellation reads in
// MIGRATION: Library/Components/Security/Roles/RoleController.vb at L278 and L496. Each
// MIGRATION: of those is an ambient, server-local, untestable read.
//
// MIGRATION: DIVERGENCE, server-local to UTC. Those reads are normalized here onto one
// MIGRATION: injected boundary that speaks Coordinated Universal Time only. A date-only
// MIGRATION: value derived from this clock can therefore differ by one calendar day from
// MIGRATION: the legacy figure, according to the host's offset and the moment of
// MIGRATION: evaluation. The divergence is deliberate rather than accidental, and the
// MIGRATION: repository-root MIGRATION_NOTES.md is where it is recorded; this file does
// MIGRATION: not modify that document.

/// <summary>
/// Reads the machine clock and reports the present instant in Coordinated Universal
/// Time. This is the production implementation of <see cref="IClock"/> and the
/// solution's only sanctioned system-clock boundary.
/// </summary>
/// <remarks>
/// <para>
/// Sole clock boundary. Every layer above obtains the present instant by injecting
/// <see cref="IClock"/>, so this file is the single place in the backend where the
/// machine clock is touched. Confining that boundary to one type is what makes every
/// time-dependent business rule substitutable under test: a test supplies its own
/// <see cref="IClock"/> implementation in place of this one and needs no hook on this
/// class to do it. That is why the surface below is one property and nothing besides.
/// </para>
/// <para>
/// Coordinated Universal Time only, deliberately. The contract exposes no local-time,
/// calendar-date, offset-bearing or time-zone member, and neither does this
/// implementation. A caller needing a calendar date derives it from the instant it
/// already holds; a caller needing to present a local time converts at the presentation
/// boundary, never here. Widening this class beyond its contract would reintroduce
/// exactly the ambiguity the migration removes, because a second member would let two
/// callers disagree about which reading they meant.
/// </para>
/// <para>
/// Lifetime and thread safety. The type is registered with the dependency-injection
/// container as a singleton (AAP 0.4.3), which is safe because it holds no state at all:
/// no field, no cached value and no captured collaborator. Every caller therefore sees a
/// live reading rather than a value fixed when the container built the instance, and
/// concurrent callers on any number of threads cannot interfere with one another. The
/// visibility is internal on purpose — consumers depend on the abstraction, never on
/// this class — and the type is sealed because there is no behaviour here worth
/// specialising.
/// </para>
/// </remarks>
internal sealed class SystemClock : IClock
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SystemClock"/> class.
    /// </summary>
    /// <remarks>
    /// Declared explicitly, and public, for two reasons. The dependency-injection
    /// container chooses a constructor from the public constructors of the
    /// implementation type, so an internal class still needs a public one to be
    /// activatable at all; and spelling the empty parameter list in source puts the
    /// singleton guarantee where a reader looks for it. The body is intentionally empty.
    /// Accepting a collaborator here would let a singleton capture a shorter-lived
    /// service, and there is no initialisation to perform in any case, because the
    /// instant is read on each access rather than stored.
    /// </remarks>
    public SystemClock()
    {
    }

    /// <summary>
    /// Gets the present instant in Coordinated Universal Time, read from the machine
    /// clock at the moment of access.
    /// </summary>
    /// <value>
    /// A <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/>. The value is always a genuine reading: it is never
    /// absent and it never stands in for a missing date. The legacy code signalled an
    /// absent date with a sentinel value; nothing of that kind is ever returned here, and
    /// a caller that must model an absent date does so with a nullable value at its own
    /// boundary.
    /// </value>
    /// <remarks>
    /// Each access performs a fresh reading, so two accesses can yield different values;
    /// a caller needing one instant for several calculations reads once into a local. The
    /// member is synchronous by design rather than by oversight: reading a clock is a
    /// pure in-process operation that performs no I/O, so the solution-wide rule that
    /// I/O-bound members must be awaitable does not reach it. Do not reshape this member
    /// into an awaitable form.
    /// </remarks>
    public DateTime UtcNow => DateTime.UtcNow;
}
