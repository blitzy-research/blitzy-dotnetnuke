using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>
/// Pins the solution's only sanctioned system-clock boundary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One property, and every time-dependent rule in the application depends on it.</strong> The clock is
/// the single place the machine clock is read, which is what makes every expiry, effective date and audit
/// stamp substitutable under test. That concentration is also what makes it worth pinning: a clock that
/// reported LOCAL time would shift every date-only derivation by up to a day depending on the host's offset,
/// and would do it silently, because a local reading is still a plausible-looking instant. The migration notes
/// record the server-local-to-universal move as a deliberate divergence; this is the assertion that keeps the
/// delivered code on the universal side of it.
/// </para>
/// <para>
/// <strong>Why the boundary is asserted through the container.</strong> Every consumer injects
/// <see cref="IClock"/>, so what matters is not that <c>SystemClock</c> behaves but that the composed
/// application resolves it, and resolves ONE of it. A per-request clock would still pass a behavioural test
/// while allocating an instance per request for no benefit; a clock that cached its reading would report the
/// moment the container was built for the life of the process, which is the failure mode that would make
/// every expiry comparison in the application wrong in the same direction.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SystemClockTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="SystemClockTests"/> class.</summary>
    /// <param name="fixture">The shared host, whose container supplies the clock.</param>
    public SystemClockTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The composed application resolves this implementation, once.</summary>
    /// <remarks>
    /// Singleton is asserted the way it is observable - two resolutions from two independent scopes yielding
    /// the same reference - rather than by reading a registration descriptor, because the descriptor is what
    /// was asked for and the reference is what the application gets.
    /// </remarks>
    [Fact]
    public void TheComposedApplication_ResolvesOneSystemClock()
    {
        IClock fromRoot = _fixture.Services.GetRequiredService<IClock>();

        fromRoot.Should().BeOfType<SystemClock>(
            "this is the solution's only sanctioned system-clock boundary, and a second implementation would "
            + "let two callers disagree about what 'now' means");

        using IServiceScope first = _fixture.Services.CreateScope();
        using IServiceScope second = _fixture.Services.CreateScope();

        IClock fromFirstScope = first.ServiceProvider.GetRequiredService<IClock>();
        IClock fromSecondScope = second.ServiceProvider.GetRequiredService<IClock>();

        fromFirstScope.Should().BeSameAs(fromRoot);
        fromSecondScope.Should().BeSameAs(
            fromRoot,
            "the clock is registered as a singleton, which is safe precisely because it holds no state");
    }

    /// <summary>The reported instant is Coordinated Universal Time.</summary>
    /// <remarks>
    /// The kind is asserted rather than inferred from the value, because a local reading and a universal one
    /// are indistinguishable by magnitude on a host whose offset is zero - which is exactly what a container
    /// usually is. Asserting the kind is therefore the only assertion that would fail on a developer machine
    /// AND in a container.
    /// </remarks>
    [Fact]
    public void UtcNow_IsCoordinatedUniversalTime()
    {
        IClock clock = _fixture.Services.GetRequiredService<IClock>();

        clock.UtcNow.Kind.Should().Be(
            DateTimeKind.Utc,
            "an unspecified or local kind would make every comparison against a stored universal instant "
            + "wrong by the host's offset, and would do it without failing anything");
    }

    /// <summary>The reported instant lies between two readings taken either side of it.</summary>
    /// <remarks>
    /// This is what distinguishes a live reading from a fixed one: a clock that returned a constant, a
    /// captured value, or a date-only value would fall outside a window this narrow. The window is bounded by
    /// the test's own readings, so it needs no tolerance and no assumption about the host's accuracy.
    /// </remarks>
    [Fact]
    public void UtcNow_LiesBetweenTwoReadingsTakenAroundIt()
    {
        IClock clock = _fixture.Services.GetRequiredService<IClock>();

        DateTime before = DateTime.UtcNow;
        DateTime reported = clock.UtcNow;
        DateTime after = DateTime.UtcNow;

        reported.Should().BeOnOrAfter(before);
        reported.Should().BeOnOrBefore(
            after,
            "a value outside a window this narrow is not a reading of the present at all");
    }

    /// <summary>Each access reads afresh rather than returning a value fixed earlier.</summary>
    /// <remarks>
    /// The singleton lifetime makes this the failure that would matter most: an instance that captured its
    /// reading once would report the moment the container was built for the remainder of the process, so every
    /// expiry check in the application would compare against a fixed past. The delay is generous against the
    /// coarsest system timer resolution, so the strict inequality is not a race.
    /// </remarks>
    [Fact]
    public async Task UtcNow_AdvancesBetweenAccesses()
    {
        IClock clock = _fixture.Services.GetRequiredService<IClock>();

        DateTime first = clock.UtcNow;

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        DateTime second = clock.UtcNow;

        second.Should().BeAfter(
            first,
            "a captured reading would make a singleton clock report the container's build time forever");
    }

    /// <summary>The implementation holds no state, which is what makes the singleton safe.</summary>
    /// <remarks>
    /// Asserted structurally because it is a structural guarantee. A field added here - a cached instant, an
    /// injected collaborator, a time-zone - would either fix the reading or capture a shorter-lived service in
    /// a singleton, and neither would fail any behavioural assertion above on the first call.
    /// </remarks>
    [Fact]
    public void TheImplementation_HoldsNoState()
    {
        FieldInfo[] fields = typeof(SystemClock).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        fields.Should().BeEmpty(
            "a singleton that held a field could fix its reading or capture a scoped collaborator, and the "
            + "clock's whole safety argument is that it holds nothing at all");

        typeof(SystemClock).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name)
            .Should()
            .BeEquivalentTo(
                new[] { nameof(IClock.UtcNow) },
                "the contract exposes no local-time, calendar-date, offset-bearing or time-zone member, and "
                + "a second member here would let two callers disagree about which reading they meant");
    }
}
