namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Reads the installation-wide configuration rows that the legacy platform persisted
/// in the <c>HostSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. This contract stands in for the reachable surface of two excluded legacy
/// subsystems - the <c>DotNetNuke.Common.Globals</c> module, referenced by 111 files, and the
/// three-file <c>DotNetNuke.Entities.Host</c> namespace, referenced by 53 - whose measured
/// coupling from the code actually being migrated is only six distinct members across fourteen
/// call sites. It covers exactly the <c>HostSettings</c>-table portion of those six; the other
/// five belong elsewhere, as the migration notes below record, so that neither excluded module is
/// reconstructed here.
/// </para>
/// <para>
/// The rows behind these members live in a genuine relational table and are shared by every
/// portal in the installation, which is what separates this abstraction from
/// <c>IPortalContext</c>: that sibling covers the per-request composite the legacy code assembled
/// and mutated for the lifetime of a single request, whereas these values outlive any request.
/// </para>
/// <para>
/// A key with no matching row is reported as <see langword="null"/> rather than as the legacy
/// empty string. <c>DnnMigration.Infrastructure</c> is expected to supply the single
/// implementation: it alone may hold the persistence context needed to materialise the rows.
/// </para>
/// </remarks>
public interface IHostSettingsService
{
    // MIGRATION: the legacy read shapes are not carried forward. Both host reads
    // answered with a forward-only data-reader (Library/Components/Providers/Data/DataProvider.vb
    // L87 and L88) and the whole-table read answered with an untyped, mutable, pre-generics
    // collection (Library/Components/Host/HostSettings.vb L47-L70). The reader type is
    // unavailable to this project by design, because the Domain layer takes no reference of any
    // kind; the collection becomes IReadOnlyDictionary<string, string>, typed and immutable to
    // its caller.

    // MIGRATION: a synchronous property that performed a database read becomes an awaitable,
    // cancellable method. Library/Components/Shared/Globals.vb L221-L225 declared the whole-table
    // read as a read-only property and reached storage from inside its getter, so all twenty-one
    // call sites issued blocking I/O through what looked like a field access.

    // MIGRATION: five of the six legacy members reached from migrated code are deliberately absent
    // because reconstructing the excluded Globals module was never the intent, and each has its own
    // named home instead: the runtime performance multiplier becomes a bound options class in the
    // Application layer, the application virtual path and the two physical-path members become
    // host-environment and link-generation services in the API layer, and the unauthenticated-user
    // role name becomes a domain constant. Also absent is the legacy secure-settings reader, whose
    // name contradicts its behaviour: it answers with only the NON-secure rows and additionally
    // drops any name containing "password". It served the excluded host-level administration
    // screens, and its naming defect is left uncorrected rather than repaired here, so that any
    // future reimplementation reproduces the legacy behaviour and not the name's promise.

    // MIGRATION: a key with no matching row now yields null. The legacy reader answered a missing
    // key with the empty-string sentinel and hydration mapped a database NULL to the empty string
    // as well, so an absent row, a NULL column and a genuinely empty value were indistinguishable
    // to every caller. Where an externally visible contract still depends on the empty-string
    // form, the DTO and API boundary preserves it explicitly.

    /// <summary>
    /// Retrieves the value of a single host setting.
    /// </summary>
    /// <param name="settingName">
    /// Name of the setting to look up, matched against the setting-name column exactly as the
    /// legacy schema stores it.
    /// </param>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>
    /// A task whose completed value is the stored setting value, or <see langword="null"/> when no
    /// row carries <paramref name="settingName"/>. An empty string therefore means a row exists and
    /// holds an empty value, which the legacy reader could not express.
    /// </returns>
    Task<string?> GetSettingAsync(string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every host setting as a single materialised, read-only projection keyed by setting
    /// name.
    /// </summary>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>
    /// A task whose completed value maps every setting name to its stored value. The projection is
    /// empty when the table holds no rows and is never <see langword="null"/>.
    /// </returns>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);
}
