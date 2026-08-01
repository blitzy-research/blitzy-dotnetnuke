namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: This contract is net-new. AAP 0.5.1.1 lists the Domain Abstractions/Services
// MIGRATION: contracts as CREATE with no one-to-one legacy predecessor: the VB.NET source had
// MIGRATION: no time abstraction whatsoever, so there is nothing here to translate line for
// MIGRATION: line. The interface exists to give that missing seam a name.
//
// MIGRATION: DIVERGENCE, server-local to UTC. The legacy code reads the web server's LOCAL
// MIGRATION: clock. The canonical measured site is
// MIGRATION: Library/Components/Security/Roles/RoleController.vb:L496,
// MIGRATION:     userRole.ExpiryDate = DateAdd(DateInterval.Day, -1, Date.Today())
// MIGRATION: in which Date.Today() is the host machine's local calendar date. The same
// MIGRATION: method also reads the bare VB Now at L505, L530, L533 and L534, which binds
// MIGRATION: through the Imports Microsoft.VisualBasic at L25 and is likewise server-local.
// MIGRATION: This contract speaks UTC only, so a date-only value computed from it CAN differ
// MIGRATION: by one calendar day from the legacy figure, according to the host's time-zone
// MIGRATION: offset and the moment of evaluation. That is a deliberate, documented
// MIGRATION: divergence rather than an accident, and MIGRATION_NOTES.md records it.
//
// MIGRATION: There is deliberately no Today member. A caller needing a calendar date derives
// MIGRATION: it as UtcNow.Date, which keeps one instant source for the whole solution and
// MIGRATION: makes the UTC basis of every derived date visible at the point of use.
//
// MIGRATION: The legacy absent-date sentinel is Date.MinValue, published as Null.NullDate by
// MIGRATION: Library/Components/Shared/Null.vb L66-L70, and it is NOT reproduced here.
// MIGRATION: UtcNow is non-nullable because an instant always exists; domain models express
// MIGRATION: an absent date as a nullable DateTime, and preserving the legacy sentinel where
// MIGRATION: it is externally observable belongs to the DTO and API boundary (AAP Rule T7).

/// <summary>
/// Supplies the present instant to the DnnMigration backend, and is the only sanctioned
/// source of it.
/// </summary>
/// <remarks>
/// <para>
/// Sole sanctioned source. Business code in the Domain, Application and Infrastructure
/// layers obtains the present instant through an injected <see cref="IClock"/>. Reading the
/// machine clock directly — <c>DateTime.Now</c>, <c>DateTime.UtcNow</c>, or any
/// offset-bearing equivalent — is forbidden in those layers, because an ambient read cannot
/// be substituted by a test and so makes every time-dependent business rule
/// non-deterministic. The legacy VB.NET code had no time abstraction at all, which is
/// precisely the weakness this contract removes.
/// </para>
/// <para>
/// Coordinated Universal Time only. <see cref="UtcNow"/> always speaks UTC. A caller that
/// needs a calendar date derives it as <c>UtcNow.Date</c>; a caller that needs to present a
/// local time converts at the presentation boundary, never here. There is deliberately no
/// <c>Today</c> member, no local-time member and no offset-bearing member: every
/// time-dependent behaviour measured in the legacy trees is expressible through UTC, so a
/// second member would be speculation rather than a requirement.
/// </para>
/// <para>
/// Implementation and lifetime. <c>Infrastructure/Services/SystemClock.cs</c> provides the
/// production implementation and is registered with the dependency-injection container as a
/// singleton (AAP 0.4.3). The contract holds no mutable state, so one shared instance is
/// safe to reuse across requests. Consumers acquire it by constructor injection only; there
/// is no ambient accessor to reach for, which is the point of AAP Rule T8. A test supplies
/// its own implementation — one that is fixed, or that advances under the test's control —
/// rather than widening this contract, which is why the surface below exposes no member for
/// adjusting the value it yields.
/// </para>
/// <para>
/// Why entities do not stamp themselves. <c>Common/AuditableEntity.cs</c> deliberately
/// contains no <c>DateTime.Now</c> or <c>DateTime.UtcNow</c> call. Stamping
/// <c>CreatedDate</c> and <c>LastUpdatedDate</c> is the Application service layer's
/// responsibility, performed with an injected <see cref="IClock"/>. A self-stamping entity
/// would have to reach for a clock itself, and the Domain layer references nothing
/// (AAP Rule T1), so it has no way to acquire one. This contract is what makes that
/// prohibition workable.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>
    /// Gets the present instant in Coordinated Universal Time.
    /// </summary>
    /// <value>
    /// A <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/>. The value is always a genuine reading: it is never
    /// absent and never stands in for a missing date.
    /// </value>
    /// <remarks>
    /// The synchronous shape is deliberate, not an oversight. Reading a clock is a pure
    /// in-process operation that performs no I/O, so the solution-wide rule that I/O-bound
    /// members must be awaitable (AAP Rule T6) does not apply. Do not reshape this member
    /// into an awaitable form.
    /// </remarks>
    DateTime UtcNow { get; }
}
