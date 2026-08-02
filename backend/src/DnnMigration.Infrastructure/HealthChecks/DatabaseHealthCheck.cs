using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>
/// Reports whether the API can reach the DotNetNuke database it persists through.
/// </summary>
/// <remarks>
/// <para>
/// This check exists because two independent things depend on the answer. The API container declares a
/// <c>HEALTHCHECK</c> that probes <c>/health</c> with <c>wget --spider</c>, and the compose file makes the
/// frontend service's <c>depends_on</c> conditional on <c>service_healthy</c> - so an API that reports
/// unhealthy prevents the frontend from ever starting. The end-to-end validation gate then curls the same
/// path directly. The endpoint that surfaces this check must therefore be anonymous: were it to require a
/// bearer token, the container would be permanently unhealthy and the frontend would never come up, even
/// though both images build and both processes run correctly.
/// </para>
/// <para>
/// <strong>Connectivity only, and deliberately so.</strong> The probe asks whether a connection can be
/// opened and nothing more. It does not read a table, count rows or verify that any mapped object exists.
/// A schema assertion would make the answer depend on the database having been populated, which would
/// report a correctly running API as unhealthy against a freshly provisioned or empty database and would
/// take the frontend down with it. Whether the schema matches the model is settled by the integration
/// suite, which is the right place for it - a liveness probe that fails on a data condition is a probe
/// that cannot be trusted to mean what it says.
/// </para>
/// <para>
/// MIGRATION: the nearest legacy analogue is <c>Website/KeepAlive.aspx</c>, which existed to stop the
/// application pool being recycled rather than to report readiness, and which asserted nothing about the
/// database at all. This is a net-new capability, not a translation, and it is named as such so nobody
/// searches for a predecessor to match.
/// </para>
/// <para>
/// Nothing this check returns carries a credential. The connection string is never placed in a
/// description, a datum or an exception message: the database and server names are reported because an
/// operator needs to know which instance was probed, and the rest of the string - which includes the
/// password - is never touched.
/// </para>
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    /// <summary>The datum key carrying the database that was probed.</summary>
    private const string DatabaseDatumKey = "database";

    /// <summary>The datum key carrying the server that was probed.</summary>
    private const string ServerDatumKey = "server";

    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.</summary>
    /// <param name="context">
    /// The context whose connection is probed. Resolved per check, because the context is scoped and the
    /// health-check middleware creates a scope for each evaluation.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public DatabaseHealthCheck(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Probes the database connection.</summary>
    /// <param name="context">The registration being evaluated, supplying the configured failure status.</param>
    /// <param name="cancellationToken">Token that cancels the probe.</param>
    /// <returns>
    /// A healthy result when a connection could be opened; otherwise the failure status the registration
    /// configured, carrying a description and - when an exception was raised - that exception.
    /// </returns>
    /// <remarks>
    /// The registration's own <see cref="HealthCheckContext.Registration"/> supplies the failure status
    /// rather than this method hard-coding <see cref="HealthStatus.Unhealthy"/>, so a deployment that
    /// wants an unreachable database to degrade rather than fail can express that at registration time
    /// without this type changing.
    /// </remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        HealthStatus failureStatus = context.Registration.FailureStatus;
        IReadOnlyDictionary<string, object> data = DescribeConnection();

        try
        {
            bool reachable = await _context.Database
                .CanConnectAsync(cancellationToken)
                .ConfigureAwait(false);

            return reachable
                ? HealthCheckResult.Healthy("The DotNetNuke database is reachable.", data)
                : new HealthCheckResult(
                    failureStatus,
                    "The DotNetNuke database could not be reached.",
                    exception: null,
                    data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The probe was cancelled by the caller - a shutdown, or a probe timeout. That is not a
            // statement about the database, so it is rethrown rather than reported as a failed check.
            throw;
        }
        catch (Exception error)
        {
            // A provider can surface an unreachable database as any number of exception types, and a
            // health check that let one escape would fail the endpoint itself rather than report an
            // unhealthy dependency - turning a recoverable outage into an unreadable one. The exception
            // travels on the result so the configured response writer can log it.
            return new HealthCheckResult(
                failureStatus,
                "The DotNetNuke database could not be reached.",
                error,
                data);
        }
    }

    /// <summary>Describes which instance was probed, without disclosing how it is reached.</summary>
    /// <returns>The database and server names, omitting either when the provider does not expose it.</returns>
    /// <remarks>
    /// Reads the two individual accessors rather than the connection string, so there is no path by which
    /// a credential can reach the response. Both are wrapped because a provider is free to throw when a
    /// connection has never been configured, and a description is never worth failing a probe over.
    /// </remarks>
    private IReadOnlyDictionary<string, object> DescribeConnection()
    {
        Dictionary<string, object> data = new(StringComparer.Ordinal);

        try
        {
            System.Data.Common.DbConnection connection = _context.Database.GetDbConnection();

            if (!string.IsNullOrWhiteSpace(connection.Database))
            {
                data[DatabaseDatumKey] = connection.Database;
            }

            if (!string.IsNullOrWhiteSpace(connection.DataSource))
            {
                data[ServerDatumKey] = connection.DataSource;
            }
        }
        catch (InvalidOperationException)
        {
            // No connection has been configured, so there is nothing to describe. The probe below will
            // report the real condition; this is only the label on it.
        }

        return data;
    }
}
