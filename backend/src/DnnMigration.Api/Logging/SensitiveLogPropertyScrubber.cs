using Serilog.Core;
using Serilog.Events;

namespace DnnMigration.Api.Logging;

/// <summary>
/// Removes the log properties that the hosting layer attaches from caller input, so that no entry the
/// application writes carries a value the caller chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing diagnostic is lost.</b> The request identifier the hosting scope also carries is
/// server-generated and is left in place, the correlation identifier is on every entry and on every
/// response, and the request envelope names the route template, the method, the status and the duration.
/// What is removed is the one member of that set that the caller wrote.
/// </para>
/// <para>
/// MIGRATION: net-new, with no legacy counterpart - the legacy application wrote no request log at all. It
/// is recorded here because the divergence is not from legacy behaviour but from the framework's default
/// behaviour, which is to publish the path everywhere.
/// </para>
/// </remarks>
internal sealed class SensitiveLogPropertyScrubber : ILogEventEnricher
{
    /// <summary>Names of the properties removed from every event.</summary>
    /// <remarks>
    /// A closed list, deliberately short, and deliberately naming properties this application never sets
    /// itself: everything here is attached by a layer below and is removed precisely because it was not
    /// authored here.
    /// </remarks>
    private static readonly string[] ScrubbedProperties = ["RequestPath"];

    /// <summary>Removes every scrubbed property from the event.</summary>
    /// <param name="logEvent">The event being enriched.</param>
    /// <param name="propertyFactory">Creates property values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> is <see langword="null"/>.</exception>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (string property in ScrubbedProperties)
        {
            logEvent.RemovePropertyIfPresent(property);
        }
    }
}
