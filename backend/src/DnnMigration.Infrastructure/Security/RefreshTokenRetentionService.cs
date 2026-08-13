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
/// MIGRATION: PRIV-02. IT EXISTS BECAUSE RECLAMATION USED TO BE A SIDE EFFECT OF TRAFFIC. Both shipped stores deleted
/// expired records only while issuing or rotating a token, so an installation nobody signed in to retained
/// every expired family and every revoked record it had ever written - the account, the tenant and the token
/// digest of sessions long ended - indefinitely. That is exactly the installation where no operator is
/// watching, and in the durable store the records survive a restart as well. A caller independent of any
/// request is the only thing that turns a retention setting into a retention behaviour.
/// </para>
/// <para>
/// ⚠ IT NEVER THROWS, AND THAT IS DELIBERATE RATHER THAN LAZY. An unhandled exception escaping
/// <see cref="BackgroundService.ExecuteAsync"/> stops the host by default under .NET 8, so a sweep that met a
/// transient database fault would take the whole API down - trading a data-retention concern for an outage,
/// which is a strictly worse exchange. Every fault is logged and the next interval is awaited. The stores
/// already convert their own faults into <see cref="RefreshTokenOutcome.StoreUnavailable"/> rather than
/// raising, so the catch here is the backstop for everything else.
/// </para>
/// <para>
/// ⚠ IT RESOLVES THE STORE PER SWEEP FROM A SCOPE, NOT ONCE IN THE CONSTRUCTOR. A hosted service is a
/// singleton, and <see cref="IRefreshTokenStore"/> is registered as one too in every shipped configuration -
/// but a deployment registering its own store behind the <c>External</c> provider may register it scoped, and
/// capturing a scoped service in a singleton is a captive dependency that would either fail at start-up or
/// hold one scope open for the process lifetime. A scope per sweep costs nothing at this frequency.
/// </para>
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
    /// <summary>
    /// How long the service waits after start-up before its first sweep.
    /// </summary>
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

            // The COUNT and nothing else. No identifier, no digest and no subject reaches this log: the
            // records being described are token records, and a log line naming whose sessions were reclaimed
            // would put in the logging store precisely the personal data the sweep exists to remove.
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
            // ⚠ EVERY OTHER FAULT IS ABSORBED. See the class remarks: an exception leaving ExecuteAsync stops
            // the host, so a transient store fault would become an API outage. The type is logged; the
            // message is not, because a provider message can carry server, catalogue and login metadata -
            // the same reasoning the store applies to its own diagnostics.
            _logger.LogError(
                "Refresh-token retention sweep failed with {FaultType}. The sweep will be attempted again at "
                + "the next interval; no session record has been changed.",
                error.GetType().Name);
        }
    }
}
