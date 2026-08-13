using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Drives <see cref="IRefreshTokenStore.PurgeRetiredAsync"/> on a fixed interval, so that expired and
/// retired refresh records are reclaimed whether or not anybody is signing in.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ IT LOGS AT INFORMATION ONLY WHEN IT REMOVED SOMETHING. A sweep that finds nothing is the ordinary case
/// and is recorded at debug, because an hourly "removed 0 records" line in a production log is noise that
/// trains an operator to ignore the channel. A sweep that could not reach the store is a warning: the
/// retention policy is not being applied, which is a fact an operator should see.
/// </para>
/// <para>
/// SECOND HOSTED SERVICE IN THIS SOLUTION, after the portal-alias conformance monitor. It follows the same
/// shape: bounded work, no throwing, and an initial delay so start-up is not competing with it.
/// </para>
/// </remarks>
internal sealed class RefreshTokenRetentionService : BackgroundService
{
    /// <summary>How long the service waits after start-up before its first sweep.</summary>
    /// <remarks>
    /// Long enough that the first sweep is not contending with the connection-pool warm-up, the health
    /// probe's first pass and whatever the platform is doing while the container is still being judged
    /// healthy; short enough that a deployment restarted more often than the sweep interval still reclaims.
    /// </remarks>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RefreshTokenRetentionService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenRetentionService"/> class.</summary>
    /// <param name="scopes">Creates the per-sweep scope the store is resolved from.</param>
    /// <param name="options">Validated store options, read for the sweep interval.</param>
    /// <param name="logger">Sink for sweep outcomes.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RefreshTokenRetentionService(
        IServiceScopeFactory scopes,
        IOptions<RefreshTokenStoreOptions> options,
        ILogger<RefreshTokenRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(options.Value.RetentionSweepMinutes);
    }

    /// <summary>Sweeps once after the start-up delay, then once per configured interval.</summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    /// <returns>A task that completes when the host stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            // PeriodicTimer rather than a Delay loop, so the interval is measured between TICKS rather than
            // between the end of one sweep and the start of the next - a sweep that takes a while does not
            // push every subsequent one later. It also disposes cleanly on the stopping token.
            using PeriodicTimer timer = new(_interval);

            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a fault, and deliberately not logged: shutting down during the
            // start-up delay or mid-wait is the ordinary way this service ends.
        }
    }

    /// <summary>Performs one reclamation sweep, converting every fault into a log entry.</summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    /// <returns>A task that completes when the sweep has been attempted.</returns>
    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

            IRefreshTokenStore store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

            RefreshTokenPurgeResult result = await store
                .PurgeRetiredAsync(stoppingToken)
                .ConfigureAwait(false);

            if (!result.Answered)
            {
                _logger.LogWarning(
                    "Refresh-token retention sweep could not reach the session store. Expired and revoked "
                    + "records are not being reclaimed; the store's own diagnostics name the fault.");

                return;
            }

            if (result.RemovedRecords == 0)
            {
                _logger.LogDebug("Refresh-token retention sweep found nothing to reclaim.");

                return;
            }

            // The COUNT and nothing else.
            _logger.LogInformation(
                "Refresh-token retention sweep reclaimed {RemovedRecords} retired session record(s).",
                result.RemovedRecords);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown observed mid-sweep. Rethrown to the caller's own handler by way of the loop's token.
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                "Refresh-token retention sweep failed with {FaultType}. The sweep will be attempted again at "
                + "the next interval; no session record has been changed.",
                error.GetType().Name);
        }
    }
}
