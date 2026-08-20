namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Reads the installation-wide configuration rows that the legacy platform persisted in the
/// <c>HostSettings</c> table.
/// </summary>
/// <remarks>
/// The rows behind these members live in a genuine relational table and are shared by every portal in the
/// installation, which is what separates this abstraction from <c>IPortalContext</c>: that sibling covers
/// the per-request composite the legacy code assembled and mutated for the lifetime of a single request,
/// whereas these values outlive any request.
/// </remarks>
public interface IHostSettingsService
{
    // MIGRATION: the legacy read shapes are not carried forward.

    // MIGRATION: five of the six legacy members reached from migrated code are deliberately absent because
    // reconstructing the excluded Globals module was never the intent, and each has its own named home
    // instead: the runtime performance multiplier becomes a bound options class in the Application layer,
    // the application virtual path and the two physical-path members become host-environment and
    // link-generation services in the API layer, and the unauthenticated-user role name becomes a domain
    // constant.

    // A key with no matching row now yields null. The legacy reader answered a missing key with the
    // empty-string sentinel and hydration mapped a database NULL to the empty string as well, so an absent
    // row, a NULL column and a genuinely empty value were indistinguishable to every caller.

    /// <summary>Retrieves the value of a single host setting.</summary>
    /// <param name="settingName">
    /// Name of the setting to look up, matched against the setting-name column exactly as the legacy schema
    /// stores it.
    /// </param>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>
    /// A task whose completed value is the stored setting value, or <see langword="null"/> when no row
    /// carries <paramref name="settingName"/>.
    /// </returns>
    Task<string?> GetSettingAsync(string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every host setting as a single materialised, read-only projection keyed by setting name.
    /// </summary>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>A task whose completed value maps every setting name to its stored value.</returns>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);
}
