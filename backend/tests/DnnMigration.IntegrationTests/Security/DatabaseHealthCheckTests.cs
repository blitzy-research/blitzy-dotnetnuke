using DnnMigration.Infrastructure.HealthChecks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Holds the database readiness probe to two properties: a cancelled attempt is not reported as a
/// dependency verdict, and no result it produces carries a provider exception.
/// </summary>
/// <remarks>
/// <para>
/// WHY BOTH MATTER. This probe answers the one endpoint that is anonymous, is polled several times a
/// minute for the life of the deployment, and gates whether the rest of the topology starts at all.
/// </para>
/// <para>
/// The cancellation property is a correctness one. A probe deadline expiring, a caller disconnecting or
/// the host shutting down all arrive as a cancellation of the supplied token, and the earlier single catch
/// answered every one of them with "the database is unavailable". So the last verdict a gracefully
/// stopping application published blamed its store for a fault that had not occurred - and a report
/// produced by abandoning the attempt says nothing about the store's condition in any case, because the
/// attempt never finished.
/// </para>
/// <para>
/// The exception property is a disclosure one. The health-check infrastructure logs the exception an
/// unhealthy entry carries, messages and all, and a connection failure's message routinely quotes the
/// server, the database, the login it used and the network error underneath. Attaching it therefore put
/// deployment topology and account names into the log of that continuously polled endpoint.
/// </para>
/// <para>
/// Both are asserted against a connection string pointing at an address nothing listens on, so the probe
/// genuinely fails rather than being mocked into failing - which is what makes the assertion about the
/// REAL provider exception rather than about a stand-in.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class DatabaseHealthCheckTests
{
    /// <summary>
    /// A connection string that parses but cannot be opened, with a short timeout so the test does not
    /// wait on a network stack.
    /// </summary>
    /// <remarks>
    /// Port 1 on the loopback address: reserved, never listening, and refused immediately rather than
    /// dropped, so the failure arrives promptly and deterministically on every platform.
    /// </remarks>
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=DoesNotExist;User Id=nobody;Password=nothing;"
        + "Connect Timeout=1;TrustServerCertificate=True;Encrypt=False";

    /// <summary>
    /// An unreachable store is reported unhealthy, and the result carries NO exception.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CheckHealth_WhenTheStoreIsUnreachable_ReportsUnhealthyWithoutTheProviderException()
    {
        DatabaseHealthCheck check = Build(UnreachableConnectionString);

        HealthCheckResult result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be(
            "Database connectivity is unavailable.",
            "the description is fixed authored text, because an anonymous caller reads it");
        result.Exception.Should().BeNull(
            "the infrastructure logs whatever exception an unhealthy result carries, messages and all, and "
            + "a connection failure's message quotes the server, the database and the login");
        // MIGRATION: RE-ORACLED FROM "the data dictionary is empty" TO "the data dictionary carries only the
        // bounded classification". Two revisions removed the exception from this result and disagreed about
        // what replaces it: one recorded the failure's type chain as a data entry, the other logged it and
        // left the dictionary empty. Both are now done, because they serve different readers - a test or an
        // in-process diagnostic reads the entry without parsing text, and an operator's log is where a probe
        // that keeps failing is noticed - and neither reaches the caller: the endpoint's response writer
        // emits status, timestamp, version and service name and nothing else, which is what makes the entry
        // safe. The value is a TYPE CHAIN, so it can name a socket refusal or a login refusal and cannot
        // carry a server name, a database name or a credential.
        result.Data.Should().ContainSingle("only the bounded failure classification is attached")
            .Which.Key.Should().Be("failureKind");
        result.Data["failureKind"].ToString().Should().NotBeNullOrWhiteSpace();
        result.Data["failureKind"].ToString().Should().NotContain(
            UnreachableConnectionString,
            "a classification that quoted the connection it failed on would be the disclosure this "
            + "removed");
    }

    /// <summary>
    /// An absent connection string is an answer, not a fault, and it names nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the state a container is in before its environment is complete, so it has to be reportable.
    /// </remarks>
    [Fact]
    public async Task CheckHealth_WithNoConnectionString_ReportsUnhealthyWithFixedText()
    {
        DatabaseHealthCheck check = Build(connectionString: null);

        HealthCheckResult result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Database connectivity is not configured.");
        result.Exception.Should().BeNull();
    }

    /// <summary>
    /// A cancelled probe PROPAGATES the cancellation instead of blaming the dependency.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CheckHealth_WhenTheSuppliedTokenIsCancelled_PropagatesTheCancellation()
    {
        DatabaseHealthCheck check = Build(UnreachableConnectionString);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> probe = () => check.CheckHealthAsync(Context(), cancelled.Token);

        await probe.Should().ThrowAsync<OperationCanceledException>(
            "an abandoned attempt has established nothing about the store, so it must not be reported as a "
            + "verdict about it - least of all during a graceful shutdown");
    }

    /// <summary>Builds the probe over a configuration carrying the supplied connection string.</summary>
    /// <param name="connectionString">The value to configure, or <see langword="null"/> for none.</param>
    /// <returns>The probe.</returns>
    private static DatabaseHealthCheck Build(string? connectionString)
    {
        return new DatabaseHealthCheck(new SingleConnectionStringConfiguration(connectionString), new NoOpLogger());
    }

    /// <summary>Builds the registration context the infrastructure would supply.</summary>
    /// <returns>The context.</returns>
    /// <remarks>
    /// The probe does not read it - the verdict is binary - so the registration is the minimum the type
    /// requires rather than a copy of the production one.
    /// </remarks>
    private static HealthCheckContext Context()
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "database",
                _ => new NeverInvokedCheck(),
                HealthStatus.Unhealthy,
                tags: null),
        };
    }

    /// <summary>
    /// The smallest configuration that answers the one question the probe asks.
    /// </summary>
    /// <param name="connectionString">The configured value, or <see langword="null"/> for none.</param>
    /// <remarks>
    /// Hand-written rather than built from the in-memory configuration provider, deliberately: that
    /// provider lives in <c>Microsoft.Extensions.Configuration</c>, which this test project does not
    /// reference, and adding a package to a test project so that one probe can be constructed would widen
    /// the pinned dependency inventory for no behavioural gain. The abstraction itself is available, and
    /// the probe reads exactly one key through <c>GetConnectionString</c> - which resolves the
    /// <c>ConnectionStrings</c> section and indexes it - so that is all this implements. Everything else
    /// answers emptily, which is honest: it is never called.
    /// </remarks>
    private sealed class SingleConnectionStringConfiguration(string? connectionString) : IConfiguration
    {
        /// <inheritdoc />
        public string? this[string key]
        {
            get => string.Equals(key, "ConnectionStrings:Default", StringComparison.Ordinal)
                ? connectionString
                : null;

            set => throw new NotSupportedException("The probe never writes configuration.");
        }

        /// <inheritdoc />
        public IEnumerable<IConfigurationSection> GetChildren() => [];

        /// <inheritdoc />
        public IChangeToken GetReloadToken() => new NeverChangesToken();

        /// <inheritdoc />
        public IConfigurationSection GetSection(string key)
        {
            return string.Equals(key, "ConnectionStrings", StringComparison.Ordinal)
                ? new ConnectionStringsSection(connectionString)
                : new ConnectionStringsSection(value: null);
        }
    }

    /// <summary>
    /// The <c>ConnectionStrings</c> section, carrying the single key the probe indexes.
    /// </summary>
    /// <param name="value">The value the <c>Default</c> key holds, or <see langword="null"/> for none.</param>
    private sealed class ConnectionStringsSection(string? value) : IConfigurationSection
    {
        /// <inheritdoc />
        public string? this[string key]
        {
            get => string.Equals(key, "Default", StringComparison.Ordinal) ? value : null;
            set => throw new NotSupportedException("The probe never writes configuration.");
        }

        /// <inheritdoc />
        public string Key => "ConnectionStrings";

        /// <inheritdoc />
        public string Path => "ConnectionStrings";

        /// <inheritdoc />
        public string? Value
        {
            get => null;
            set => throw new NotSupportedException("The probe never writes configuration.");
        }

        /// <inheritdoc />
        public IEnumerable<IConfigurationSection> GetChildren() => [];

        /// <inheritdoc />
        public IChangeToken GetReloadToken() => new NeverChangesToken();

        /// <inheritdoc />
        public IConfigurationSection GetSection(string key) => new ConnectionStringsSection(value: null);
    }

    /// <summary>A change token for configuration that cannot change.</summary>
    private sealed class NeverChangesToken : IChangeToken
    {
        /// <inheritdoc />
        public bool HasChanged => false;

        /// <inheritdoc />
        public bool ActiveChangeCallbacks => false;

        /// <inheritdoc />
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            new NoOpRegistration();

        /// <summary>A registration with nothing to release.</summary>
        private sealed class NoOpRegistration : IDisposable
        {
            /// <inheritdoc />
            public void Dispose()
            {
                // Nothing was registered, so there is nothing to release.
            }
        }
    }

    /// <summary>A logger that discards everything, since what is asserted here is the RESULT.</summary>
    /// <remarks>
    /// The sanitised diagnostic the probe writes is asserted where it is observable end to end; this suite
    /// asserts the two properties of the result, and a recording logger here would test the same rendering
    /// twice.
    /// </remarks>
    private sealed class NoOpLogger : ILogger<DatabaseHealthCheck>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => false;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Discarded deliberately: this suite asserts the result, not the log entry.
        }
    }

    /// <summary>
    /// Stands in for the registration's factory, which the probe never calls.
    /// </summary>
    private sealed class NeverInvokedCheck : IHealthCheck
    {
        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}
