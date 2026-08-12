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
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        RefreshTokenStoreOptions settings = _options.Value;

        if (_activeStore is not RefreshTokenStore inProcess)
        {
            IReadOnlyDictionary<string, object> externalData =
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["provider"] = settings.Provider,
                    ["storeIsThisSolutions"] = false,
                };

            return Task.FromResult(HealthCheckResult.Healthy(ExternalStoreDescription, externalData));
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
            return Task.FromResult(HealthCheckResult.Degraded(
                "The process-local refresh-token store is at its tracked-generation ceiling, so the oldest "
                + "refresh families are being retired early and their holders must sign in again. "
                + usage
                + " Raise RefreshTokenStore:MaximumTrackedTokens, or register a shared store behind "
                + "IRefreshTokenStore and declare RefreshTokenStore:Provider as "
                + RefreshTokenStoreOptions.ExternalProvider
                + ".",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Refresh-token state is process-local: it is not shared between replicas and does not survive a "
            + "restart, so this deployment must run a single API instance unless a shared store is registered "
            + "behind IRefreshTokenStore. "
            + usage,
            data));
    }

    /// <summary>Expresses tracked generations as a whole percentage of the ceiling.</summary>
    /// <param name="capacity">The snapshot to describe.</param>
    /// <returns>The utilisation, rounded to a whole percent and never negative.</returns>
    /// <remarks>
    /// The ceiling cannot legitimately be zero - both the options type and the Api layer's validator refuse
    /// anything below one - but a probe that divided by a configured value without checking it would be a
    /// probe that could throw, and a health check that throws reports the application unhealthy for a reason
    /// that has nothing to do with the application.
    /// </remarks>
    private static int DescribeUtilisation(RefreshTokenStore.CapacitySnapshot capacity)
    {
        if (capacity.Ceiling <= 0)
        {
            return 100;
        }

        double ratio = (double)capacity.TrackedGenerations / capacity.Ceiling * 100d;

        return (int)Math.Clamp(Math.Round(ratio, MidpointRounding.AwayFromZero), 0d, 100d);
    }
}
