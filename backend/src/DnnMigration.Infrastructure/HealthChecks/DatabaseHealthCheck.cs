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
/// Three unauthenticated callers depend on the answer. The API image declares a <c>HEALTHCHECK</c> that
/// probes <c>/health/ready</c> with <c>wget --spider</c>, because the Alpine runtime ships no
/// <c>curl</c>; compose makes the frontend service's <c>depends_on</c> conditional on the API becoming
/// ready; and the end-to-end validation gate probes the same readiness path. None carries a credential,
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
/// and never logged from here. The raised error itself does not travel on the result at all: a failed
/// probe carries the failure's TYPE NAME in one data entry and nothing more, which distinguishes a
/// socket refusal from a login refusal from a malformed configured value while being structurally
/// incapable of carrying an address or a credential. The endpoint's response writer emits no data
/// dictionary, so even that classification stays inside the process.
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
    // and EXCLUDES ready-tagged checks through an explicit predicate, so that a starting container
    // answers 200 during the window in which no reachable database is yet guaranteed; readiness is
    // surfaced by the api layer as a separate view over the ready tag, at /health/ready. That split is
    // stated here rather than left implicit in the tag, because the tag alone does not say which view is
    // which. Neither the endpoint predicate nor the response writer belongs in this file.
    //
    // MIGRATION: COMPLETION. For a period this statement was aspirational rather than true - the tag was
    // applied here and the single endpoint ran every registered check regardless, so the tag had no
    // semantic effect and the liveness view depended on a reachable database. The predicates now exist
    // and the two views are distinct; the container probe and the compose health condition address the
    // liveness path unchanged, which is what makes that arrangement safe for a deployment whose database
    // is external and may not be reachable when the process starts.

    /// <summary>
    /// Name of the single data entry a failed probe carries.
    /// </summary>
    /// <remarks>
    /// It holds the failure's type name and nothing else - never a message, never an address, never a
    /// credential. The endpoint's response writer emits no data dictionary at all, so the entry is
    /// readable by in-process diagnostics only, which is what makes it safe to record on a probe whose
    /// result an anonymous caller can read.
    /// </remarks>
    private const string FailureKindDataKey = "failureKind";

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
    /// Records why a probe did not complete, in a form that carries no provider message.
    /// </summary>
    /// <remarks>
    /// The probe's own logger rather than the health-check infrastructure's, because the
    /// infrastructure records whatever exception an unhealthy result carries - messages included -
    /// and this type deliberately carries none. Writing the diagnostic here is what keeps the
    /// actionable part of a failure (which KIND of failure it was) while leaving out the part that
    /// quotes the server, the database, the login and the network error.
    /// </remarks>
    private readonly ILogger<DatabaseHealthCheck> _diagnostics;

    /// <summary>
    /// Largest number of exceptions the recorded type chain may name.
    /// </summary>
    /// <remarks>
    /// A provider failure is routinely wrapped twice - a socket fault inside a provider fault - and a
    /// pathological chain must not be able to write an unbounded log entry from an endpoint that is
    /// probed several times a minute for the life of the deployment.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 5;

    /// <summary>
    /// Initialises a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="configuration">
    /// Configuration supplying <c>ConnectionStrings:Default</c>. A container overrides that key with the
    /// environment variable <c>ConnectionStrings__Default</c>, which the compose file populates from the
    /// documented host variable. It is the only key read here; the legacy connection-string name is
    /// deliberately not read at all.
    /// </param>
    /// <param name="diagnostics">
    /// Receives the sanitized reason a probe did not complete. Nothing else is ever written through it,
    /// and no successful probe writes at all: this endpoint is polled continuously, so an entry per
    /// success would be the highest-volume record the application produces and would say nothing.
    /// </param>
    /// <remarks>
    /// There is no argument guard and no failure path, deliberately. The container supplies both
    /// dependencies and cannot supply either as <see langword="null"/>, and absent configuration is
    /// expressed as a health RESULT rather than as a construction failure - a check that threw while
    /// being created would fail the endpoint itself, which is the one outcome a probe must never
    /// produce. Holding a captured string and a logger keeps the type safe at any lifetime, so nothing
    /// scoped is captured.
    /// </remarks>
    public DatabaseHealthCheck(IConfiguration configuration, ILogger<DatabaseHealthCheck> diagnostics)
    {
        _connectionString = configuration.GetConnectionString("Default");
        _diagnostics = diagnostics;
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
    /// host shutdown cannot leave the attempt running behind it. The registration imposes a two-second
    /// deadline, so this token is what bounds the probe in practice.
    /// </param>
    /// <returns>
    /// A healthy result when a connection was opened; an unhealthy result carrying a fixed description
    /// when the connection string is absent, or a fixed description plus the failure's type name when
    /// opening did not succeed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown, deliberately, when <paramref name="cancellationToken"/> is cancelled - and it is the only
    /// exception that leaves this method. Cancellation means either that the registration's deadline
    /// elapsed or that the caller stopped listening, and only the infrastructure can tell those apart,
    /// so it has to see the cancellation to report the right one. See the filtered catch below.
    /// </exception>
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is NOT a database outage, and reporting it as one is a lie an operator
            // acts on. The supplied token is cancelled by the probe's own deadline elapsing, by the
            // caller disconnecting, or by the host shutting down - none of which says anything
            // whatever about whether the instance is reachable. Absorbing it here would answer a
            // shutdown with "the database is unavailable" and put a spurious dependency outage into
            // the record of every rolling restart, which is precisely the diagnosis an on-call
            // engineer would then chase.
            //
            // The guard clause is what makes this correct rather than merely well intentioned. The
            // provider raises this same type for an internal command timeout while the token is
            // still live, and THAT is a genuine connectivity failure that must fall through to the
            // handler below. Testing the token distinguishes the two; testing the type alone cannot.
            //
            // Rethrowing is safe for the endpoint. The health infrastructure treats a cancelled check
            // as cancellation of the whole report rather than as a fault, so nothing here can turn a
            // recoverable outage into an unreadable one - and there is by definition nobody left
            // waiting for an answer.
            throw;
        }
        catch (Exception exception)
        {
            // Every other failure converges here and nothing is rethrown. A provider surfaces an
            // unreachable instance as any of a dozen types, and an escaping fault would break the
            // endpoint itself - turning a recoverable dependency outage into an unreadable one, and
            // taking with it the frontend that waits on this signal.
            //
            // SEC: THE RAISED ERROR IS NOT ATTACHED TO THE RESULT, AND THE CLASSIFICATION THAT REPLACES IT
            // IS RECORDED TWICE ON PURPOSE. It used to be attached on the stated ground that the
            // infrastructure could then log it privately - but "privately" was not a property of the result:
            // the framework's health-check logging records the exception carried by an unhealthy entry,
            // messages and all, and a connection failure's message routinely quotes the server name, the
            // database name, the login it used and the network error underneath. That put deployment topology
            // and account names into the log of the one endpoint that answers anonymously, several times a
            // minute, for the life of the deployment.
            //
            // What is recorded instead is the failure's TYPE CHAIN - enough to tell a socket refusal from a
            // login refusal from a name-resolution failure from a malformed configured value, and incapable
            // of carrying an address or a credential. Two revisions each chose one destination for it and
            // both are kept, because they serve different readers: the DATA ENTRY is what an in-process
            // diagnostic or a test can read off the report without parsing text, and the WARNING is what
            // reaches the operator's log where a probe that keeps failing is actually noticed. Neither
            // duplicates the other, and the endpoint's own response writer emits neither the exception nor
            // the data dictionary, so nothing here reaches the anonymous caller.
            //
            // The DESCRIPTION returned to the caller is unchanged and remains fixed text.
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
