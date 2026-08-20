using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>Pins the solution's only sanctioned system-clock boundary.</summary>
/// <remarks>
/// <para>
/// <strong>One property, and every time-dependent rule in the application depends on it.</strong> The clock
/// is the single place the machine clock is read, which is what makes every expiry, effective date and
/// audit stamp substitutable under test.
/// </para>
/// <para>
/// <strong>Why the boundary is asserted through the container.</strong> Every consumer injects <see
/// cref="IClock"/>, so what matters is not that <c>SystemClock</c> behaves but that the composed
/// application resolves it, and resolves ONE of it.
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
    /// reading once would report the moment the container was built for the remainder of the process, so
    /// every expiry check in the application would compare against a fixed past. The delay is generous
    /// against the coarsest system timer resolution, so the strict inequality is not a race.
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
    /// Asserted structurally because it is a structural guarantee. A field added here - a cached instant,
    /// an injected collaborator, a time-zone - would either fix the reading or capture a shorter-lived
    /// service in a singleton, and neither would fail any behavioural assertion above on the first call.
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
