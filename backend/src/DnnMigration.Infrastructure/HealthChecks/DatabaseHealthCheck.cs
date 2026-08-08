using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>
/// Reports whether the SQL Server instance this service persists through can be reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which probe reads this check.</b> It is registered with the <c>ready</c> tag, so it is
/// surfaced by <c>/health/ready</c> and by that view alone. The three probes the deployment
/// artefacts declare - the API image's <c>HEALTHCHECK</c>, the compose api check the frontend's
/// <c>depends_on</c> waits on, and the end-to-end validation gate - all read <c>/health</c>, the
/// LIVENESS view, which excludes ready-tagged checks by predicate: the store is external and may
/// legitimately be unreachable while the process starts, so holding the container unhealthy on it
/// would hold the whole topology back. Every caller is unauthenticated, which is why this type
/// takes no dependency on a caller identity, a resolved tenant or an ambient request, and answers
/// correctly when invoked outside a request altogether.
/// </para>
/// <para>
/// <strong>Connectivity only, and deliberately so.</strong> Opening a connection is the whole
/// probe: no command is issued afterwards, nothing is counted, and no mapped object is asserted to
/// exist. Each of those would turn the answer into a claim about the CONTENT of the store rather
/// than its reachability, so a correctly running service would be reported unhealthy against a
/// freshly provisioned store - and because the frontend waits on this signal, that verdict would
/// take the rest of the deployment down with it.
/// </para>
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    // MIGRATION: this readiness probe is net-new and does NOT port Website/KeepAlive.aspx, whose entire
    // behaviour was a meta refresh that kept the IIS worker process warm and asserted nothing about the
    // database. Two things follow: the probe is read-only, issuing no data-definition language and applying
    // no migration - unlike the legacy connectivity test, which ran installation batches against the live
    // store - and nothing it harvests reaches the response, so the connection string, the host it names and
    // the text of any error raised while opening are never described, logged or returned.

    /// <summary>Name of the single data entry a failed probe carries.</summary>
    /// <remarks>
    /// It holds the failure's type name and nothing else - never a message, never an address, never a
    /// credential.
    /// </remarks>
    private const string FailureKindDataKey = "failureKind";

    /// <summary>
    /// The configured connection string, or <see langword="null"/> when the key is absent.
    /// </summary>
    /// <remarks>
    /// Captured once at construction and never widened beyond this field: no member returns it, no
    /// result carries it, and nothing writes it to a log.
    /// </remarks>
    private readonly string? _connectionString;

    /// <summary>
    /// Records why a probe did not complete, in a form that carries no provider message.
    /// </summary>
    /// <remarks>
    /// The probe's own logger rather than the health-check infrastructure's, because the
    /// infrastructure records whatever exception an unhealthy result carries - messages included -
    /// and this type deliberately carries none.
    /// </remarks>
    private readonly ILogger<DatabaseHealthCheck> _diagnostics;

    /// <summary>Largest number of exceptions the recorded type chain may name.</summary>
    /// <remarks>
    /// A provider failure is routinely wrapped twice - a socket fault inside a provider fault - and
    /// a pathological chain must not be able to write an unbounded log entry from an endpoint that
    /// is probed several times a minute for the life of the deployment.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 5;

    /// <summary>
    /// Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="configuration">
    /// Configuration supplying <c>ConnectionStrings:Default</c>.
    /// </param>
    /// <param name="diagnostics">Receives the sanitized reason a probe did not complete.</param>
    /// <remarks>
    /// There is no argument guard and no failure path, deliberately. The container supplies both
    /// dependencies and cannot supply either as <see langword="null"/>, and absent configuration is
    /// expressed as a health RESULT rather than as a construction failure - a check that threw
    /// while being created would fail the endpoint itself, which is the one outcome a probe must
    /// never produce.
    /// </remarks>
    public DatabaseHealthCheck(IConfiguration configuration, ILogger<DatabaseHealthCheck> diagnostics)
    {
        _connectionString = configuration.GetConnectionString("Default");
        _diagnostics = diagnostics;
    }

    /// <summary>Probes the configured SQL Server instance by opening a connection to it.</summary>
    /// <param name="context">
    /// The registration being evaluated, supplied by the health-check infrastructure.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that abandons the attempt once the caller has stopped waiting, so a probe deadline or
    /// a host shutdown cannot leave the attempt running behind it.
    /// </param>
    /// <returns>
    /// A healthy result when a connection was opened; an unhealthy result carrying a fixed
    /// description when the connection string is absent, or a fixed description plus the failure's
    /// type name when opening did not succeed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown, deliberately, when <paramref name="cancellationToken"/> is cancelled - and it is the
    /// only exception that leaves this method.
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
            // of the instance. Disposal is asynchronous so returning the handle to the pool never blocks a
            // thread, and it happens inside this block on purpose, so that a fault raised while closing is
            // caught below alongside a fault raised while opening.
            await using var connection = new SqlConnection(_connectionString);

            // The entire probe. The supplied token is passed through rather than dropped, so an attempt the
            // caller has abandoned ends at once instead of holding a socket open behind it.
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Database connectivity is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is NOT a database outage, and reporting it as one is a lie an operator acts on.
            // The supplied token is cancelled by the probe's own deadline elapsing, by the caller
            // disconnecting, or by the host shutting down - none of which says anything whatever about
            // whether the instance is reachable.
            //
            // The guard clause is what makes this correct rather than merely well intentioned. The provider
            // raises this same type for an internal command timeout while the token is still live, and THAT
            // is a genuine connectivity failure that must fall through to the handler below.
            throw;
        }
        catch (Exception exception)
        {
            // Every other failure converges here and nothing is rethrown. A provider surfaces an unreachable
            // instance as any of a dozen types, and an escaping fault would break the endpoint itself -
            // turning a recoverable dependency outage into an unreadable one, and taking with it the
            // frontend that waits on this signal.
            //
            // SEC: THE RAISED ERROR IS NOT ATTACHED TO THE RESULT, AND THE CLASSIFICATION THAT REPLACES IT
            // IS RECORDED TWICE ON PURPOSE. It used to be attached on the stated ground that the
            // infrastructure could then log it privately - but "privately" was not a property of the result:
            // the framework's health-check logging records the exception carried by an unhealthy entry,
            // messages and all, and a connection failure's message routinely quotes the server name, the
            // database name, the login it used and the network error underneath.
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

    /// <summary>
    /// Names a failure using type names only, so that no provider message can reach the log.
    /// </summary>
    /// <param name="failure">The failure to describe.</param>
    /// <returns>
    /// The full type name of each exception in the chain, outermost first, joined by an arrow and
    /// bounded by <see cref="MaximumDescribedChainDepth"/>.
    /// </returns>
    /// <remarks>
    /// The guarantee is a property of what this method READS rather than of anything it filters: it
    /// touches only <see cref="System.Type.FullName"/>, so a server name, a database name, a login,
    /// a connection string fragment or a statement cannot appear in the result by any path. The
    /// chain is walked iteratively and bounded, because a provider failure is routinely wrapped
    /// twice and a pathological chain must not produce an unbounded entry.
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
