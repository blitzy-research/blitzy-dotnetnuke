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
/// <para>
/// <strong>WHY A PROBE AND NOT ONLY A DOCUMENT.</strong> The store this solution ships holds refresh state in
/// the API process, so it is neither shared between replicas nor carried across a restart. That fact was
/// previously recorded in prose only - in the store's own remarks, in <c>README.md</c> and in
/// <c>MIGRATION_NOTES.md</c> - which meant a running deployment offered no way to confirm it, and no way at
/// all to see the one dynamic consequence it has: that once the tracked-generation ceiling is reached, the
/// oldest refresh families are retired early and their holders are signed out. This probe makes both
/// observable from the deployment itself.
/// </para>
/// <para>
/// <strong>It is a LIVENESS probe, deliberately, and not a readiness one.</strong> Readiness decides whether
/// traffic should reach this instance, and neither a process-local store nor a full one stops the API serving
/// requests: sign-in still works, every access token already issued still verifies, and every data endpoint
/// still answers. Tagging this readiness would take a serving instance out of rotation over a condition that
/// costs a sign-in, which is strictly worse than the condition. It is registered with a DEGRADED failure
/// status for the same reason the audit-delivery probe is: degraded remains an HTTP-successful health
/// response, so the container stays healthy and the front end still starts, while the named probe tells an
/// operator what is wrong.
/// </para>
/// <para>
/// <strong>Where its output is actually read.</strong> The health response body is contractually four members
/// - status, timestamp, version and service name - so nothing here appears in it. What reaches an operator is
/// the aggregate status plus the structured log entry the API layer writes for every probed report, which
/// records each probe's name, status and DESCRIPTION. That is why the numbers below are composed into the
/// description rather than left only in the data dictionary: the data dictionary is deliberately excluded
/// from that log, because a probe's data routinely carries connection detail. Every value this probe
/// contributes is a count or a configured name - never an account, a tenant, a token or a digest - so it is
/// safe in a log by construction.
/// </para>
/// <para>
/// <strong>What it reports about a REPLACEMENT store, and why that is deliberately little.</strong> Capacity
/// is a property of this solution's implementation rather than of <c>IRefreshTokenStore</c>, so a
/// deployment-supplied store is not obliged to answer for it - obliging it would make the substitution seam
/// harder to satisfy for no benefit. When the active store is not this solution's, the probe says exactly
/// that and stops; the replacement's own monitoring owns its locality and its capacity.
/// </para>
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
    /// <param name="activeStore">
    /// The store the container resolves. Injected as the CONTRACT rather than as the concrete type, so that a
    /// deployment which substituted its own is observed as substituted instead of being reported on through
    /// an instance nothing uses.
    /// </param>
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

        // ⚠ THE SHARED STORE IS ASKED ABOUT ITSELF, WHICH IT USED NOT TO BE. MIGRATION: SEC-09. This method
        // tested the active store against the process-local implementation and reported ANYTHING else as "held
        // by a deployment-supplied store", healthy, with no check performed. For the durable store this
        // solution itself ships that published three false facts at once - that the store is not this
        // solution's, and by omission that nothing can be said about its locality or capacity when in truth it
        // is replica-safe and restart-surviving - and it reported a deployment whose session catalogue was
        // unreachable or unprovisioned as perfectly healthy right up to the moment every sign-in failed. A
        // probe whose answer cannot distinguish a working store from a missing one is not a probe.
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

        // At or above the ceiling the store is already retiring the oldest families to make room, so callers
        // holding them are being signed out by capacity rather than by policy. That is the one state of this
        // store an operator has to act on, and it is the only one reported as degraded.
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
    /// <para>
    /// THE TWO FAILURES ARE REPORTED APART because their remedies are opposite: an unreachable catalogue is a
    /// connectivity or credentials problem, and a reachable catalogue with no session table is an unrun
    /// provisioning step. Collapsing them into one "unhealthy" would send an operator to the wrong half of the
    /// system, and the second failure is the one a first deployment actually hits, now that the table is
    /// provisioned out of band rather than created by the running API.
    /// </para>
    /// <para>
    /// DEGRADED RATHER THAN UNHEALTHY, for the reason this whole probe is a liveness check: an instance whose
    /// session catalogue is unavailable still serves every data endpoint and still verifies every access token
    /// already issued. What it cannot do is start or renew a session, which is a named degradation an operator
    /// must act on rather than grounds for removing the instance from rotation - and removing it would not help,
    /// because the catalogue is shared by every replica.
    /// </para>
    /// <para>
    /// Nothing here can throw: the probe reports outcomes rather than raising them, and the arithmetic is the
    /// same guarded division the process-local branch uses.
    /// </para>
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
        // tenant, a token, a digest, a catalogue name or a connection string - so the whole dictionary is safe
        // by construction, which is the standard the process-local branch is held to as well.
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
    /// <remarks>
    /// The ceiling cannot legitimately be zero - both the options type and the Api layer's validator refuse
    /// anything below one - but a probe that divided by a configured value without checking it would be a
    /// probe that could throw, and a health check that throws reports the application unhealthy for a reason
    /// that has nothing to do with the application.
    /// </remarks>
    private static int DescribeUtilisation(RefreshTokenStore.CapacitySnapshot capacity) =>
        Utilisation(capacity.TrackedGenerations, capacity.Ceiling);

    /// <summary>Expresses a tracked count as a whole percentage of a ceiling.</summary>
    /// <param name="tracked">How many generations are tracked.</param>
    /// <param name="ceiling">The ceiling they are bounded by.</param>
    /// <returns>The utilisation, rounded to a whole percent and never negative.</returns>
    /// <remarks>
    /// Shared by both store branches so the two cannot come to express the same ratio differently, and taking
    /// the count as a 64-bit value because the shared store counts rows rather than dictionary entries.
    /// </remarks>
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
