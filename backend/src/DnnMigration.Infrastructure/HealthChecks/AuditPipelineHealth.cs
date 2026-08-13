using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnnMigration.Infrastructure.HealthChecks;

/// <summary>Exposes whether the process has failed to deliver an audit record since it started.</summary>
/// <remarks>
/// The audit contract deliberately preserves a completed business operation when its logging sink fails.
/// That resilience must not turn the loss into silence, so the sink increments this process-local,
/// saturating counter before attempting its secondary diagnostic.
/// </remarks>
internal sealed class AuditPipelineHealth : IHealthCheck
{
    private const string HealthyDescription = "Audit records are reaching the configured logging pipeline.";
    private const string DegradedDescription =
        "One or more audit records failed to reach the configured logging pipeline since process start.";

    private long _failureCount;

    /// <summary>Gets the number of failed deliveries observed by this process.</summary>
    internal long FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>Records one failed audit delivery without ever overflowing the counter.</summary>
    internal void RecordFailure()
    {
        while (true)
        {
            long current = Volatile.Read(ref _failureCount);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _failureCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        long failures = FailureCount;
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["failureCount"] = failures,
        };

        HealthCheckResult result = failures == 0
            ? HealthCheckResult.Healthy(HealthyDescription, data)
            : HealthCheckResult.Degraded(DegradedDescription, data: data);

        return Task.FromResult(result);
    }
}
