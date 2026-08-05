using DnnMigration.Infrastructure.HealthChecks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Infrastructure;

// THE CONCRETE READINESS PROBE IS THE SUBJECT OF THIS FILE. Every fact below runs
// DnnMigration.Infrastructure.HealthChecks.DatabaseHealthCheck itself. The type is internal sealed, and
// the owning project grants this assembly - and only this assembly - internals visibility for exactly
// this purpose; the justification is written next to that item in
// backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj.
//
// NO FACT HERE REACHES A DATABASE, and none needs to. The probe's own behaviour is entirely a matter of
// which of its three outcomes it selects, and each outcome is reachable without a reachable instance:
// the absent-configuration verdict is decided before any connection is constructed, the cancellation
// path is decided by an already-signalled token, and the unavailable verdict is reached by a connection
// string the provider itself refuses to parse. Whether a real instance can be opened is settled by the
// integration suite, which is where a dependency on a running SQL Server belongs.
//
// THE FILTER THESE FACTS EXIST TO PIN was added because the probe converted the CALLER'S OWN
// cancellation into an unhealthy verdict. Two things followed. A probe deadline, or the host shutting
// down, published a spurious database outage - and both the container HEALTHCHECK and the compose
// dependency read this signal, so a shutdown could be recorded by whatever was watching as a database
// failure. And the cancellation contract was broken: the infrastructure that supplies the token
// distinguishes "we stopped asking" from "we asked and the answer was no" by whether cancellation
// surfaces, and swallowing it collapsed the two.

/// <summary>
/// Facts covering the SQL Server readiness probe's three outcomes, and in particular the boundary
/// between a probe the caller abandoned and a database that could not be reached.
/// </summary>
public sealed class DatabaseHealthCheckTests
{
    /// <summary>
    /// A well-formed connection string naming a host that is never contacted, because every fact using it
    /// either cancels before the attempt or is asserted on the outcome the cancellation produces.
    /// </summary>
    /// <remarks>
    /// The one-second connect timeout is a safety net rather than part of any assertion: if the token check
    /// inside the provider were ever bypassed, this bounds the damage to a second and a failed fact instead
    /// of a suite that hangs.
    /// </remarks>
    private const string WellFormedConnectionString =
        "Server=localhost,1;Database=DnnMigrationProbe;User ID=probe;Password=probe;"
        + "TrustServerCertificate=True;Connect Timeout=1";

    /// <summary>
    /// A connection string the provider's own parser refuses, which reaches the failure verdict without any
    /// network activity at all.
    /// </summary>
    private const string UnparseableConnectionString = "Server=localhost;NotAConnectionStringKeyword=1";

    /// <summary>
    /// An absent connection string is REPORTED as unhealthy rather than raised, because "not configured" is
    /// a legitimate answer to a readiness question and is the answer a container gives before its
    /// environment is complete.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The description is asserted verbatim, and the assertion that it does NOT name the missing key is the
    /// load-bearing half: an anonymous caller reads whatever this returns.
    /// </remarks>
    [Fact]
    public async Task CheckHealth_WithNoConfiguredConnectionString_ReportsUnhealthyWithoutRaising()
    {
        DatabaseHealthCheck check = CheckWith(connectionString: null);

        HealthCheckResult result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Database connectivity is not configured.");
        result.Exception.Should().BeNull();
        result.Description.Should().NotContain("ConnectionStrings");
    }

    /// <summary>
    /// A cancelled token PROPAGATES as cancellation and is not converted into a verdict, because a probe the
    /// caller abandoned has established nothing about the database.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the fact the exception filter exists for. Before it, the broad handler caught the provider's
    /// cancellation alongside every genuine fault and answered "Database connectivity is unavailable." - a
    /// statement about the database that no attempt supported.
    /// </remarks>
    [Fact]
    public async Task CheckHealth_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        DatabaseHealthCheck check = CheckWith(WellFormedConnectionString);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> probe = () => check.CheckHealthAsync(Context(), cancelled.Token);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// A failure the database itself caused, raised while the supplied token was never signalled, is still
    /// REPORTED as unhealthy, and it is classified by the failure's TYPE NAME rather than by the raised
    /// error.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The counterpart to the cancellation fact, and together with it the whole of the discrimination the
    /// filter performs: the filter reads nothing but the token, so a driver-level connect timeout - which is
    /// cancellation-SHAPED yet leaves the caller's token unsignalled - takes this same path by construction.
    /// An escaping fault here would break the endpoint itself and take the frontend that waits on it down
    /// with it, which is why only cancellation is ever raised.
    /// </remarks>
    [Fact]
    public async Task CheckHealth_WhenTheStoreCannotBeReached_ReportsUnhealthyAndCarriesTheError()
    {
        DatabaseHealthCheck check = CheckWith(UnparseableConnectionString);

        HealthCheckResult result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Database connectivity is unavailable.");

        // The RAISED ERROR IS DELIBERATELY NOT ATTACHED. A provider's own message for an unreachable
        // instance routinely names the server, the database and sometimes the login, and the framework's
        // health reporting renders whatever it is given. What identifies the failure instead is its type
        // name, carried as a data entry that no response writer emits - enough to tell a socket refusal
        // from a login refusal from a malformed configured value, and incapable of carrying an address or
        // a credential.
        result.Exception.Should().BeNull();
        result.Data.Should().ContainKey("failureKind");
        result.Data["failureKind"].Should().BeOfType<string>()
            .Which.Should().NotBeNullOrWhiteSpace();

        // Nothing harvested reaches the description or the classification: the configured value names a
        // host and would, on a real deployment, name a credential too.
        result.Description.Should().NotContain("localhost");
        result.Description.Should().NotContain("NotAConnectionStringKeyword");
        result.Data["failureKind"].ToString().Should().NotContain("localhost");
        result.Data["failureKind"].ToString().Should().NotContain("NotAConnectionStringKeyword");
    }

    /// <summary>
    /// The verdict is binary. A probe never answers <see cref="HealthStatus.Degraded"/>, because a frontend
    /// that started against a middle state would start against an API that cannot serve one request.
    /// </summary>
    /// <param name="connectionString">The configured value, or <see langword="null"/> when absent.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(UnparseableConnectionString)]
    public async Task CheckHealth_NeverReportsDegraded(string? connectionString)
    {
        DatabaseHealthCheck check = CheckWith(connectionString);

        HealthCheckResult result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Builds the probe over a configuration carrying the one key it reads.
    /// </summary>
    /// <param name="connectionString">
    /// The value to publish under <c>ConnectionStrings:Default</c>, or <see langword="null"/> to publish no
    /// value at all - which is the state a container is in before its environment is complete.
    /// </param>
    /// <returns>A probe reading that configuration.</returns>
    /// <remarks>
    /// The seam is the ABSTRACTION rather than a built configuration root, and deliberately: this project's
    /// manifest carries no configuration-provider package, only what flows from the layer under test, so
    /// binding an in-memory provider here would mean adding a dependency to assert a value the probe reads
    /// through one indexer. The double reproduces exactly the path <c>GetConnectionString</c> takes - the
    /// <c>ConnectionStrings</c> section, then the named key - so the production call is unmodified.
    /// </remarks>
    private static DatabaseHealthCheck CheckWith(string? connectionString)
    {
        var section = new Mock<IConfigurationSection>(MockBehavior.Loose);
        section.Setup(s => s["Default"]).Returns(connectionString);

        var configuration = new Mock<IConfiguration>(MockBehavior.Strict);
        configuration.Setup(c => c.GetSection("ConnectionStrings")).Returns(section.Object);

        // MIGRATION: the probe gained a logger when the raised error stopped being attached to the result.
        // The classification it writes is asserted by the suite that owns that behaviour; here the logger
        // exists only so the constructor is satisfiable, which is why a plain no-op double is correct.
        return new DatabaseHealthCheck(configuration.Object, NullLogger<DatabaseHealthCheck>.Instance);
    }

    /// <summary>
    /// Builds the registration context the infrastructure supplies. The probe does not read it, and that is
    /// asserted by supplying a registration whose own failure status is one the probe never returns.
    /// </summary>
    /// <returns>A registration context for the probe under test.</returns>
    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "database",
            _ => CheckWith(connectionString: null),
            HealthStatus.Degraded,
            new[] { "ready" }),
    };
}
