using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>Reports whether the SQL Server instance this service persists through can be reached.</summary>
/// <remarks>
/// <strong>Connectivity only, and deliberately so.</strong> Opening a connection is the whole probe: no
/// command is issued afterwards, nothing is counted, and no mapped object is asserted to exist.
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    // MIGRATION: this readiness probe is net-new and does NOT port Website/KeepAlive.aspx, whose entire
    // behaviour was a meta refresh that kept the IIS worker process warm and asserted nothing about the
    // database.

    /// <summary>Name of the single data entry a failed probe carries.</summary>
    private const string FailureKindDataKey = "failureKind";

    /// <summary>The configured connection string, or <see langword="null"/> when the key is absent.</summary>
    private readonly string? _connectionString;

    /// <summary>Records why a probe did not complete, in a form that carries no provider message.</summary>
    private readonly ILogger<DatabaseHealthCheck> _diagnostics;

    /// <summary>Largest number of exceptions the recorded type chain may name.</summary>
    /// <remarks>
    /// A provider failure is routinely wrapped twice - a socket fault inside a provider fault - and a
    /// pathological chain must not be able to write an unbounded log entry from an endpoint that is probed
    /// several times a minute for the life of the deployment.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 5;

    /// <summary>Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.</summary>
    /// <param name="configuration">Configuration supplying <c>ConnectionStrings:Default</c>.</param>
    /// <param name="diagnostics">Receives the sanitized reason a probe did not complete.</param>
    public DatabaseHealthCheck(IConfiguration configuration, ILogger<DatabaseHealthCheck> diagnostics)
    {
        _connectionString = configuration.GetConnectionString("Default");
        _diagnostics = diagnostics;
    }

    /// <summary>Probes the configured SQL Server instance by opening a connection to it.</summary>
    /// <param name="context">The registration being evaluated, supplied by the health-check infrastructure.</param>
    /// <param name="cancellationToken">
    /// Token that abandons the attempt once the caller has stopped waiting, so a probe deadline or a host
    /// shutdown cannot leave the attempt running behind it.
    /// </param>
    /// <returns>
    /// A healthy result when a connection was opened; an unhealthy result carrying a fixed description when
    /// the connection string is absent, or a fixed description plus the failure's type name when opening
    /// did not succeed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown, deliberately, when <paramref name="cancellationToken"/> is cancelled - and it is the only
    /// exception that leaves this method.
    /// </exception>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            // Reported, not raised. "Not configured" is a legitimate answer to "is this service ready", and
            // it is the answer a container gives before its environment is complete.
            return HealthCheckResult.Unhealthy("Database connectivity is not configured.");
        }

        try
        {
            // A fresh connection for every probe, never a cached or shared one: a handle held across probes
            // would eventually report the condition of something nobody is using rather than the condition
            // of the instance.
            await using var connection = new SqlConnection(_connectionString);

            // The entire probe. The supplied token is passed through rather than dropped, so an attempt the
            // caller has abandoned ends at once instead of holding a socket open behind it.
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Database connectivity is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is NOT a database outage, and reporting it as one is a lie an operator acts on.
            throw;
        }
        catch (Exception exception)
        {
            _diagnostics.LogWarning(
                "The database connectivity probe did not complete. Failure: {FailureType}.",
                DescribeFailureType(exception));

            return HealthCheckResult.Unhealthy(
                "Database connectivity is unavailable.",
                exception: null,
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [FailureKindDataKey] = DescribeFailureType(exception),
                });
        }
    }

    /// <summary>Names a failure using type names only, so that no provider message can reach the log.</summary>
    /// <param name="failure">The failure to describe.</param>
    /// <returns>
    /// The full type name of each exception in the chain, outermost first, joined by an arrow and bounded
    /// by <see cref="MaximumDescribedChainDepth"/>.
    /// </returns>
    /// <remarks>
    /// The guarantee is a property of what this method READS rather than of anything it filters: it touches
    /// only <see cref="System.Type.FullName"/>, so a server name, a database name, a login, a connection
    /// string fragment or a statement cannot appear in the result by any path.
    /// </remarks>
    private static string DescribeFailureType(Exception failure)
    {
        StringBuilder description = new();
        Exception? current = failure;
        int depth = 0;

        while (current is not null && depth < MaximumDescribedChainDepth)
        {
            if (depth > 0)
            {
                description.Append(" ---> ");
            }

            description.Append(current.GetType().FullName ?? current.GetType().Name);

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            description.Append(" ---> (chain truncated)");
        }

        return description.ToString();
    }
}
