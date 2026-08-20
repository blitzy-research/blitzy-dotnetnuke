using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>
/// Reports which refresh-token store this process is running, whether that store is safe to replicate, and
/// how much of its capacity is in use.
/// </summary>
/// <remarks>
/// <strong>Where its output is actually read.</strong> The health response body is contractually four
/// members - status, timestamp, version and service name - so nothing here appears in it.
/// </remarks>
internal sealed class RefreshTokenStoreHealth : IHealthCheck
{
    /// <summary>Description used when a deployment-supplied store is active.</summary>
    private const string ExternalStoreDescription =
        "Refresh-token state is held by a deployment-supplied store, as RefreshTokenStore:Provider declares. "
        + "This probe reports no capacity or locality for it.";

    private readonly IRefreshTokenStore _activeStore;
    private readonly IOptions<RefreshTokenStoreOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStoreHealth"/> class.</summary>
    /// <param name="activeStore">The store the container resolves.</param>
    /// <param name="options">The declared store settings.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public RefreshTokenStoreHealth(
        IRefreshTokenStore activeStore,
        IOptions<RefreshTokenStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(activeStore);
        ArgumentNullException.ThrowIfNull(options);

        _activeStore = activeStore;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        RefreshTokenStoreOptions settings = _options.Value;

        if (_activeStore is SqlServerRefreshTokenStore shared)
        {
            return await CheckSharedStoreAsync(shared, cancellationToken).ConfigureAwait(false);
        }

        if (_activeStore is not RefreshTokenStore inProcess)
        {
            IReadOnlyDictionary<string, object> externalData =
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["provider"] = settings.Provider,
                    ["storeIsThisSolutions"] = false,
                };

            return HealthCheckResult.Healthy(ExternalStoreDescription, externalData);
        }

        RefreshTokenStore.CapacitySnapshot capacity = inProcess.DescribeCapacity();
        int utilisationPercent = DescribeUtilisation(capacity);

        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["provider"] = RefreshTokenStoreOptions.InProcessProvider,
            ["storeIsThisSolutions"] = true,
            ["replicaSafe"] = false,
            ["survivesRestart"] = false,
            ["trackedGenerations"] = capacity.TrackedGenerations,
            ["trackedGenerationCeiling"] = capacity.Ceiling,
            ["utilisationPercent"] = utilisationPercent,
        };

        string usage = FormattableString.Invariant(
            $"Tracking {capacity.TrackedGenerations} of {capacity.Ceiling} generations ({utilisationPercent} per cent of capacity).");

        // At or above the ceiling the store is already retiring the oldest families to make room, so
        // callers holding them are being signed out by capacity rather than by policy. That is the one
        // state of this store an operator has to act on, and it is the only one reported as degraded.
        if (capacity.TrackedGenerations >= capacity.Ceiling)
        {
            return HealthCheckResult.Degraded(
                "The process-local refresh-token store is at its tracked-generation ceiling, so the oldest "
                + "refresh families are being retired early and their holders must sign in again. "
                + usage
                + " Raise RefreshTokenStore:MaximumTrackedTokens, or register a shared store behind "
                + "IRefreshTokenStore and declare RefreshTokenStore:Provider as "
                + RefreshTokenStoreOptions.ExternalProvider
                + ".",
                data: data);
        }

        return HealthCheckResult.Healthy(
            "Refresh-token state is process-local: it is not shared between replicas and does not survive a "
            + "restart, so this deployment must run a single API instance unless a shared store is registered "
            + "behind IRefreshTokenStore. "
            + usage,
            data);
    }

    /// <summary>Probes the shared, durable store and reports what it established.</summary>
    /// <param name="shared">The active shared store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report for this probe.</returns>
    /// <remarks>
    /// THE TWO FAILURES ARE REPORTED APART because their remedies are opposite: an unreachable catalogue is
    /// a connectivity or credentials problem, and a reachable catalogue with no session table is an unrun
    /// provisioning step.
    /// </remarks>
    private static async Task<HealthCheckResult> CheckSharedStoreAsync(
        SqlServerRefreshTokenStore shared,
        CancellationToken cancellationToken)
    {
        SqlServerRefreshTokenStore.SharedStoreReadiness readiness = await shared
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);

        int utilisationPercent = Utilisation(readiness.TrackedGenerations, readiness.Ceiling);

        // Every value is a count, a configured number or a closed enumeration member - never an account, a
        // tenant, a token, a digest, a catalogue name or a connection string - so the whole dictionary is
        // safe by construction, which is the standard the process-local branch is held to as well.
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["provider"] = RefreshTokenStoreOptions.SqlServerProvider,
            ["storeIsThisSolutions"] = true,
            ["replicaSafe"] = true,
            ["survivesRestart"] = true,
            ["catalogueReachable"] = readiness.Outcome != SharedStoreReadinessOutcome.Unreachable,
            ["tablePresent"] = readiness.Outcome == SharedStoreReadinessOutcome.Ready,
            ["trackedGenerations"] = readiness.TrackedGenerations,
            ["trackedGenerationCeiling"] = readiness.Ceiling,
            ["utilisationPercent"] = utilisationPercent,
        };

        switch (readiness.Outcome)
        {
            case SharedStoreReadinessOutcome.Unreachable:
                return HealthCheckResult.Degraded(
                    "The shared refresh-token catalogue could not be reached, so no session can be started or "
                    + "renewed while it stays unavailable. Requests carrying an access token already issued "
                    + "are unaffected. Check RefreshTokenStore:ConnectionString, the catalogue's availability "
                    + "and the principal's rights; the provider fault is recorded against the store rather "
                    + "than here, because its message names the catalogue and the login.",
                    data: data);

            case SharedStoreReadinessOutcome.TableMissing:
                return HealthCheckResult.Degraded(
                    "The shared refresh-token catalogue is reachable and holds no session table, so no session "
                    + "can be started or renewed. The table is provisioned by a deployment step rather than by "
                    + "this application, which neither creates nor alters it: run "
                    + "docker/sql/refresh-token-store.sql against the configured catalogue.",
                    data: data);

            case SharedStoreReadinessOutcome.Ready when readiness.TrackedGenerations >= readiness.Ceiling:
                return HealthCheckResult.Degraded(
                    "The shared refresh-token store is at its tracked-generation ceiling, so the refresh "
                    + "families nearest their absolute expiry are being retired early and their holders must "
                    + "sign in again. "
                    + Usage(readiness, utilisationPercent)
                    + " Raise RefreshTokenStore:MaximumTrackedTokens.",
                    data: data);

            case SharedStoreReadinessOutcome.Ready:
            default:
                return HealthCheckResult.Healthy(
                    "Refresh-token state is held in a shared SQL Server catalogue: every replica observes the "
                    + "same families and they survive a restart, so this deployment may run more than one API "
                    + "instance. "
                    + Usage(readiness, utilisationPercent),
                    data);
        }
    }

    /// <summary>Describes a shared-store probe's capacity position.</summary>
    /// <param name="readiness">What the probe established.</param>
    /// <param name="utilisationPercent">The utilisation already computed for it.</param>
    /// <returns>The sentence appended to the probe's description.</returns>
    private static string Usage(
        SqlServerRefreshTokenStore.SharedStoreReadiness readiness,
        int utilisationPercent) =>
        FormattableString.Invariant(
            $"Tracking {readiness.TrackedGenerations} of {readiness.Ceiling} generations ({utilisationPercent} per cent of capacity).");

    /// <summary>Expresses tracked generations as a whole percentage of the ceiling.</summary>
    /// <param name="capacity">The snapshot to describe.</param>
    /// <returns>The utilisation, rounded to a whole percent and never negative.</returns>
    private static int DescribeUtilisation(RefreshTokenStore.CapacitySnapshot capacity) =>
        Utilisation(capacity.TrackedGenerations, capacity.Ceiling);

    /// <summary>Expresses a tracked count as a whole percentage of a ceiling.</summary>
    /// <param name="tracked">How many generations are tracked.</param>
    /// <param name="ceiling">The ceiling they are bounded by.</param>
    /// <returns>The utilisation, rounded to a whole percent and never negative.</returns>
    private static int Utilisation(long tracked, int ceiling)
    {
        if (ceiling <= 0)
        {
            return 100;
        }

        double ratio = (double)tracked / ceiling * 100d;

        return (int)Math.Clamp(Math.Round(ratio, MidpointRounding.AwayFromZero), 0d, 100d);
    }
}
