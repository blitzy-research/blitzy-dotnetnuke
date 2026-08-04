using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>
/// Reports whether the SQL Server instance this service persists through can be reached.
/// </summary>
/// <remarks>
/// <para>
/// Three unauthenticated callers depend on the answer. The API image declares a <c>HEALTHCHECK</c> that
/// probes the health path with <c>wget --spider</c>, because the Alpine runtime ships no <c>curl</c>; the
/// compose file makes the frontend service's <c>depends_on</c> conditional on the API becoming healthy;
/// and the end-to-end validation gate probes the same path directly. None of them carries a credential,
/// which is why the endpoint that surfaces this check is anonymous and why this type takes no dependency
/// on a caller identity, a resolved tenant, or an ambient request of any kind. It answers correctly when
/// invoked outside a request altogether.
/// </para>
/// <para>
/// <strong>Connectivity only, and deliberately so.</strong> Opening a connection is the whole probe: no
/// command is issued afterwards, nothing is counted, and no mapped object is asserted to exist. Each of
/// those would turn the answer into a claim about the CONTENT of the store rather than its reachability,
/// so a correctly running service would be reported unhealthy against a freshly provisioned store - and
/// because the frontend waits on this signal, that verdict would take the rest of the deployment down
/// with it. Whether the model agrees with what it maps is settled by the integration suite, which is the
/// right place for it. Opening is also the cheapest honest probe available: it performs exactly the work
/// the connection pool would perform for the next request anyway.
/// </para>
/// <para>
/// <strong>Read-only by construction.</strong> The legacy connectivity test this replaces did far more
/// than ask a question - it read an installation script off disk, substituted a database owner and an
/// object qualifier into it, ran the resulting batches against the live store, and returned a formatted
/// list of provider error indexes, classes, numbers and messages to its caller. Every one of those
/// behaviours is forbidden here. This type issues no data-definition language, applies no migration,
/// reads no legacy provider script, and never mutates anything: the existing store is treated as
/// strictly read-only, and whatever an external tool installed into it is left untouched here.
/// </para>
/// <para>
/// <strong>Nothing harvested reaches the response.</strong> All three outcomes carry a fixed authored
/// sentence. The configured value, the host it names, the credential it carries and the text of any
/// error raised while opening are never interpolated into a description, never placed in a data entry
/// and never logged from here. The caught error travels on the result object only so that the
/// health-check infrastructure can record it through the host's own logger, where it is not readable by
/// an anonymous caller.
/// </para>
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    // MIGRATION: this readiness probe is net-new and does NOT port Website/KeepAlive.aspx, whose entire
    // behaviour was a 300-second meta refresh that echoed the current time to keep the IIS worker
    // process warm; it asserted nothing whatever about the database. Two divergences follow from that.
    // First, there is no predecessor to match, so nobody should look for one. Second, and operationally
    // the more important: ../DependencyInjection.cs registers this check with the "ready" tag, which
    // makes it a READINESS signal only. The api layer's anonymous /health endpoint is the LIVENESS view
    // and must exclude ready-tagged checks, so that a starting container answers 200 during the window
    // in which no reachable database is yet guaranteed; readiness must be surfaced by the api layer as a
    // separate view over the ready tag. That split is stated here rather than left implicit in the tag,
    // because the tag alone does not say which view is which. Neither the endpoint predicate nor the
    // response writer belongs in this file.

    /// <summary>
    /// The configured connection string, or <see langword="null"/> when the key is absent.
    /// </summary>
    /// <remarks>
    /// Captured once at construction and never widened beyond this field: no member returns it, no
    /// result carries it, and nothing writes it to a log. Everything the health endpoint emits is
    /// readable without authenticating, and this value carries a credential.
    /// </remarks>
    private readonly string? _connectionString;

    /// <summary>
    /// Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="configuration">
    /// Configuration supplying <c>ConnectionStrings:Default</c>. A container overrides that key with the
    /// environment variable <c>ConnectionStrings__Default</c>, which the compose file populates from the
    /// documented host variable. It is the only key read here; the legacy connection-string name is
    /// deliberately not read at all.
    /// </param>
    /// <remarks>
    /// There is no argument guard and no failure path, deliberately. The container supplies this
    /// dependency and cannot supply it as <see langword="null"/>, and absent configuration is expressed
    /// as a health RESULT rather than as a construction failure - a check that threw while being
    /// created would fail the endpoint itself, which is the one outcome a probe must never produce.
    /// Holding a captured string keeps the type safe at any lifetime, so nothing scoped is captured.
    /// </remarks>
    public DatabaseHealthCheck(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default");
    }

    /// <summary>
    /// Probes the configured SQL Server instance by opening a connection to it.
    /// </summary>
    /// <param name="context">
    /// The registration being evaluated, supplied by the health-check infrastructure. It is not read:
    /// this dependency is binary, so the verdict is <see cref="HealthStatus.Healthy"/> or
    /// <see cref="HealthStatus.Unhealthy"/> and never a middle state that would let the frontend start
    /// against an API that cannot serve a single request.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that abandons the attempt once the caller has stopped waiting, so a probe deadline or a
    /// host shutdown cannot leave the attempt running behind it.
    /// </param>
    /// <returns>
    /// A healthy result when a connection was opened; an unhealthy result carrying a fixed description
    /// when the connection string is absent, or a fixed description and the raised error when opening
    /// did not succeed. The method itself never faults.
    /// </returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            // Reported, not raised. "Not configured" is a legitimate answer to "is this service ready",
            // and it is the answer a container gives before its environment is complete. The
            // description says only that much; the value that is missing is never echoed back, because
            // an anonymous caller reads whatever this returns.
            return HealthCheckResult.Unhealthy("Database connectivity is not configured.");
        }

        try
        {
            // A fresh connection for every probe, never a cached or shared one: a handle held across
            // probes would eventually report the condition of something nobody is using rather than the
            // condition of the instance. Disposal is asynchronous so returning the handle to the pool
            // never blocks a thread, and it happens inside this block on purpose, so that a fault
            // raised while closing is caught below alongside a fault raised while opening.
            await using var connection = new SqlConnection(_connectionString);

            // The entire probe. The supplied token is passed through rather than dropped, so an attempt
            // the caller has abandoned ends at once instead of holding a socket open behind it.
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Database connectivity is available.");
        }
        catch (Exception exception)
        {
            // Every failure converges here, cancellation included, and nothing is rethrown. A provider
            // surfaces an unreachable instance as any of a dozen types, and an escaping fault would
            // break the endpoint itself - turning a recoverable dependency outage into an unreadable
            // one, and taking with it the frontend that waits on this signal. The error rides on the
            // result so the infrastructure can log it privately; nothing is read off it here.
            return HealthCheckResult.Unhealthy(
                "Database connectivity is unavailable.",
                exception);
        }
    }
}
