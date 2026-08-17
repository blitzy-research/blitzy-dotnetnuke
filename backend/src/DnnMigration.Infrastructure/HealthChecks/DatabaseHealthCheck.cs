using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>Reports whether the SQL Server instance this service persists through can be reached.</summary>
/// <remarks>
/// <para>
/// <strong>Reachability, established by a round trip, and deliberately nothing more.</strong> The probe opens
/// a connection and executes the cheapest possible statement on it, then asserts the answer came back. It
/// counts nothing, reads no table and asserts no mapped object exists - a readiness probe that consulted the
/// schema would report a migration state as an outage.
/// </para>
/// <para>
/// ⚠ THE STATEMENT IS THE LOAD-BEARING PART, AND OPENING ALONE WAS NOT ENOUGH. <c>new SqlConnection(...)</c>
/// followed by <c>OpenAsync</c> does not reach the server when the pool already holds a connection for this
/// string: ADO.NET hands the pooled one back and <c>Open</c> completes with no network traversal at all. A
/// probe built that way answers Healthy in under two milliseconds while the database is wedged and the very
/// same instance is failing real requests after thirty-five seconds - which is the worst failure a readiness
/// probe can have, because readiness exists precisely to take such an instance out of rotation. Executing a
/// statement forces the traversal the answer is supposed to be about.
/// </para>
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    // MIGRATION: this readiness probe is net-new and does NOT port Website/KeepAlive.aspx, whose entire
    // behaviour was a meta refresh that kept the IIS worker process warm and asserted nothing about the
    // database.

    /// <summary>Name of the single data entry a failed probe carries.</summary>
    private const string FailureKindDataKey = "failureKind";

    /// <summary>
    /// The statement the probe executes: the cheapest round trip SQL Server can be asked for.
    /// </summary>
    /// <remarks>
    /// A constant expression, so it touches no table, takes no lock, reads no page and cannot be affected by
    /// the schema, by a migration in progress or by another caller's transaction. What it proves is exactly
    /// what readiness is about - a request reached the server and an answer came back - and nothing else.
    /// </remarks>
    private const string ProbeStatement = "SELECT 1;";

    /// <summary>The value <see cref="ProbeStatement"/> must answer with for the probe to pass.</summary>
    private const int ExpectedProbeAnswer = 1;

    /// <summary>Greatest time the probe statement is allowed before the provider abandons it.</summary>
    /// <remarks>
    /// <para>
    /// ONE SECOND, AND DELIBERATELY SHORTER THAN THE TWO-SECOND REGISTRATION TIMEOUT AROUND IT. This is the
    /// INNER of two independent bounds, and it exists for two reasons. It bounds the attempt when no token is
    /// supplied at all - a caller invoking the health-check service directly, outside the registration that
    /// carries the timeout - so the probe cannot hang on any path. And when it is the bound that ends the
    /// attempt, the failure surfaces through the handler below, which writes the sanitized failure kind to the
    /// operator log; the outer timeout is reported by the health-check infrastructure instead, which describes
    /// the check rather than the fault.
    /// </para>
    /// <para>
    /// ⚠ WHICH BOUND ENDS THE ATTEMPT IS NOT GUARANTEED, AND THAT IS A PROPERTY OF THE PROVIDER RATHER THAN OF
    /// THESE NUMBERS. When a command times out, the provider tries to tell the server it has been abandoned
    /// and waits for the server to acknowledge that. A server that is merely slow acknowledges, this bound
    /// ends the attempt, and the sanitized failure kind is recorded. A connection that is being black-holed
    /// never acknowledges, so the provider stays in that wait, the outer registration timeout is what
    /// releases the call, and the infrastructure records it. Measured against a wedged dependency, the first
    /// probe after the wedge reports through this bound and the following ones through the outer one.
    /// </para>
    /// <para>
    /// BOTH OUTCOMES ARE THE SAME WHERE IT MATTERS: the registration's failure status is unhealthy, so the
    /// readiness view answers 503 either way and the published document discloses nothing either way. Only the
    /// operator-log detail differs, so nothing a caller or an orchestrator depends on rests on the race.
    /// </para>
    /// <para>
    /// One second is generous for a constant expression that takes no lock and reads no page, so a healthy but
    /// busy server cannot be reported unready by it; and the cost of being wrong in the other direction is an
    /// instance kept in rotation while it cannot answer.
    /// </para>
    /// </remarks>
    private const int ProbeStatementTimeoutSeconds = 1;

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

    /// <summary>
    /// Probes the configured SQL Server instance by executing one constant-expression statement against it.
    /// </summary>
    /// <param name="context">The registration being evaluated, supplied by the health-check infrastructure.</param>
    /// <param name="cancellationToken">
    /// Token that abandons the attempt once the caller has stopped waiting, so a probe deadline or a host
    /// shutdown cannot leave the attempt running behind it.
    /// </param>
    /// <returns>
    /// A healthy result when the statement was answered as expected; an unhealthy result carrying a fixed
    /// description when the connection string is absent, when the answer did not arrive or was not the
    /// expected one, or a fixed description plus the failure's type name when the attempt did not succeed.
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
            // A fresh connection OBJECT for every probe, never a handle held across probes - which would
            // eventually report the condition of something nobody is using.
            //
            // ⚠ A FRESH OBJECT IS NOT A FRESH NETWORK CONNECTION, and reading it as one is the mistake this
            // probe used to make. `Open` draws from the ADO.NET pool for this connection string, so on a warm
            // pool it returns an existing socket without contacting the server. Pooling is deliberately left
            // ON: the probe should answer the question a real request asks - can this instance get an answer
            // through the pool it actually uses - rather than the different and stricter question of whether a
            // brand-new connection can be established, which would also pay a TCP, TLS and login handshake
            // several times a minute for the life of the deployment.
            await using var connection = new SqlConnection(_connectionString);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqlCommand probe = connection.CreateCommand();
            probe.CommandText = ProbeStatement;
            probe.CommandType = CommandType.Text;
            probe.CommandTimeout = ProbeStatementTimeoutSeconds;

            // THE ROUND TRIP, and the part that cannot be satisfied from the pool: a wedged server leaves the
            // statement unanswered, so the attempt is ended by whichever of the two bounds gets there first -
            // the command timeout above, reported by the handler below, or the registration's timeout, which
            // arrives as cancellation and is reported by the health-check infrastructure. Both answer 503.
            // The supplied token is passed through rather than dropped, so an attempt the caller has already
            // abandoned ends at once instead of holding a socket open behind it.
            object? answer = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (answer is not int reported || reported != ExpectedProbeAnswer)
            {
                // Reached only by something answering in the server's place - a proxy, a load balancer or a
                // middlebox terminating the connection itself. Reported as unavailable rather than absorbed,
                // because a reply that is not the server's own is not evidence the server can be reached.
                _diagnostics.LogWarning(
                    "The database connectivity probe was answered with an unexpected result. "
                    + "Answer kind: {AnswerKind}.",
                    answer?.GetType().FullName ?? "null");

                return HealthCheckResult.Unhealthy("Database connectivity is unavailable.");
            }

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
