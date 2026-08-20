using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Api.Diagnostics;

/// <summary>
/// Reports, once shortly after start-up, every stored portal alias that this deployment cannot deliver a
/// request to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THIS EXISTS.</strong> <see cref="PortalAliasTopology"/> bounds an alias to one path segment
/// drawn from a narrow vocabulary, and the request pipeline now FAILS CLOSED rather than falling back to
/// the bare host when an address names a segment no stored alias matches.
/// </para>
/// <para>
/// <strong>WHY A HOSTED SERVICE, WHICH THIS SOLUTION OTHERWISE AVOIDS.</strong> The scan needs the store,
/// and the store is not reachable at the moment the service provider is built - a synchronous probe there
/// would either block start-up on a database round trip or throw during composition, in an application
/// whose own health endpoints exist precisely so that a not-yet-ready database is reported rather than
/// fatal.
/// </para>
/// </remarks>
internal sealed class PortalAliasConformanceMonitor : BackgroundService
{
    /// <summary>How long the scan waits before reading the store.</summary>
    /// <remarks>
    /// Long enough that the scan does not compete with the first requests a freshly started container
    /// receives, and short enough that an operator watching a deployment sees the answer while they are
    /// still watching. Cancelled by host shutdown, so a short-lived host - an integration test host, for
    /// instance - simply never performs the scan.
    /// </remarks>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PortalAliasConformanceMonitor> _logger;

    /// <summary>Initialises a new instance of the <see cref="PortalAliasConformanceMonitor"/> class.</summary>
    /// <param name="scopes">Factory used to obtain a scope for the scoped alias repository.</param>
    /// <param name="logger">Sink for the scan's findings.</param>
    /// <exception cref="ArgumentNullException">When any argument is <see langword="null"/>.</exception>
    public PortalAliasConformanceMonitor(
        IServiceScopeFactory scopes,
        ILogger<PortalAliasConformanceMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Waits briefly, scans once, and completes.</summary>
    /// <param name="stoppingToken">Cancelled when the host is shutting down.</param>
    /// <returns>A task that completes when the single scan has been reported.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            await ScanAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping. There is nothing to report and nothing to recover: a diagnostic
            // that did not run is not a failure of the thing it would have diagnosed.
        }
        catch (Exception failure)
        {
            // Every failure is swallowed ON PURPOSE. This type exists to tell an operator something useful;
            // it must never be the reason an installation fails to serve traffic.
            _logger.LogWarning(
                failure,
                "The stored portal aliases could not be checked against the addressable topology. "
                + "No conclusion is drawn: this reports only that the check did not complete.");
        }
    }

    /// <summary>Reads every stored alias and reports the ones this deployment cannot address.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the findings have been logged.</returns>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        IPortalAliasRepository aliases =
            scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

        IReadOnlyList<PortalAlias> stored =
            await aliases.GetAllAsync(cancellationToken).ConfigureAwait(false);

        int nonConforming = 0;

        foreach (PortalAlias alias in stored)
        {
            string? breach = DescribeBreach(alias.HttpAlias);

            if (breach is null)
            {
                continue;
            }

            nonConforming++;

            _logger.LogWarning(
                "Portal alias {PortalAliasId} on portal {PortalId} cannot be addressed by this "
                + "deployment because {Breach}. Requests naming it resolve to no tenant rather than "
                + "falling back to the bare host name, so the alias should be retired or replaced "
                + "with one carrying at most {MaximumPathSegments} path segment of letters, digits, "
                + "hyphens and underscores.",
                alias.PortalAliasId,
                alias.PortalId,
                breach,
                PortalAliasTopology.MaximumPathSegments);
        }

        if (nonConforming == 0)
        {
            _logger.LogInformation(
                "All {AliasCount} stored portal aliases can be addressed by this deployment.",
                stored.Count);

            return;
        }

        _logger.LogWarning(
            "{NonConformingCount} of {AliasCount} stored portal aliases cannot be addressed by this "
            + "deployment. Each is identified above by its surrogate key.",
            nonConforming,
            stored.Count);
    }

    /// <summary>Classifies why a stored alias falls outside the addressable topology.</summary>
    /// <param name="httpAlias">The stored alias value.</param>
    /// <returns>
    /// A phrase naming the CATEGORY of breach, suitable for a log message and carrying none of the value
    /// itself; or <see langword="null"/> when the alias is addressable.
    /// </returns>
    private static string? DescribeBreach(string? httpAlias)
    {
        if (string.IsNullOrWhiteSpace(httpAlias))
        {
            return "it holds no host name at all";
        }

        if (PortalAliasTopology.IsSupportedAddress(httpAlias))
        {
            return null;
        }

        int pathStart = httpAlias.IndexOf(PortalAliasTopology.PathSeparator, StringComparison.Ordinal);

        if (pathStart < 0)
        {
            // Unreachable while IsSupportedAddress answers true for every value without a path, and
            // stated rather than assumed so that a change there cannot turn into an index fault here.
            return "it is not a form this deployment can address";
        }

        string[] segments = httpAlias[(pathStart + 1)..].Split(PortalAliasTopology.PathSeparator);

        if (segments.Length > PortalAliasTopology.MaximumPathSegments)
        {
            return "it carries more path segments than this deployment can route";
        }

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                return "it carries an empty path segment, or ends with a separator";
            }

            if (PortalAliasTopology.IsReservedPathSegment(segment))
            {
                return "its path segment names an address this deployment reserves for itself";
            }
        }

        return "its path segment carries a character outside letters, digits, hyphen and underscore";
    }
}
